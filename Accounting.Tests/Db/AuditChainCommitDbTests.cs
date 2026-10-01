using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ประทับ audit ตอน commit บน PostgreSQL จริง (รอบ 201 ทีม PL · ฝ่ายค้าน PL-X1..X5 · คำตัดสินข้อ 104) — trait <c>Category=Db</c> (job CI <c>db-test</c>)
///
/// <para>คู่ที่ฝ่ายค้านพิสูจน์ว่า deadlock ได้ในรุ่นแรก (ล็อก audit ตั้งแต่ SaveChanges แรกแล้วถือจน commit) — จำลองด้วยล็อก advisory ของเลข JE จริง
/// (<see cref="AdvisoryLockKey.JournalSequence"/>) และบังคับลำดับด้วยสัญญาณ ⇒ ทุกเคสต้อง "จบทั้งคู่ภายในเวลา + chain ไม่แตกกิ่ง" (ทิศบวก) ·
/// ทิศตรงข้าม: rollback ไม่ทิ้งแถว audit · แถวลูกไม่ผูกบริษัทได้บริษัทของ batch (ไม่ใช้ล็อก "บริษัทว่าง" ร่วมกับ tenant อื่น)</para>
/// </summary>
[Trait("Category", "Db")]
public class AuditChainCommitDbTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly ITestOutputHelper _out;
    public AuditChainCommitDbTests(ITestOutputHelper output) => _out = output;

    private static AuditLog Row(Guid? companyId, string tag) => new()
    {
        CompanyId = companyId,
        UserEmail = "commit@example.com",
        Action = AuditAction.Update,
        EntityType = "DbTestCommit",
        EntityId = tag,
        NewValues = "{\"t\":\"" + tag + "\"}",
        Timestamp = DateTime.UtcNow,
    };

    private static Task<List<AuditLog>> ChainAsync(AccountingDbContext db, Guid companyId)
        => db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId && a.RowHash != null).OrderBy(a => a.Id).ToListAsync();

    private static Task LockJeAsync(AccountingDbContext db, Guid companyId, string prefix)
        => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.JournalSequence, prefix) });

    /// <summary>PL-X2/X3: T1 = บันทึก (มีแถว audit) แล้วค่อยขอล็อกเลข JE · T2 = ขอล็อกเลข JE ก่อนแล้วบันทึก — รุ่นแรก T1 ถือล็อก audit รอ JE · T2 ถือ JE รอ audit ⇒ 40P01</summary>
    [Fact]
    public async Task Save_then_lock_vs_lock_then_save_in_one_company_do_not_deadlock()
    {
        using var probe = DbTestDatabase.TryCreateContext();
        if (probe == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = Guid.NewGuid();
        var t1Saved = new TaskCompletionSource();
        var t2Locked = new TaskCompletionSource();

        var t1 = Task.Run(async () =>
        {
            using var db = DbTestDatabase.TryCreateContext()!;
            await using var tx = await db.Database.BeginTransactionAsync();
            db.AddChainedAuditLog(Row(company, "t1"));
            await db.SaveChangesAsync();
            t1Saved.SetResult();
            await t2Locked.Task;
            await LockJeAsync(db, company, "JV-202610-");      // รอ T2 commit (T2 ถือล็อกนี้)
            await tx.CommitAsync();
        });
        var t2 = Task.Run(async () =>
        {
            await t1Saved.Task;
            using var db = DbTestDatabase.TryCreateContext()!;
            await using var tx = await db.Database.BeginTransactionAsync();
            await LockJeAsync(db, company, "JV-202610-");
            t2Locked.SetResult();
            db.AddChainedAuditLog(Row(company, "t2"));
            await db.SaveChangesAsync();                        // รุ่นแรก: รอล็อก audit ที่ T1 ถือ = วงรอ
            await tx.CommitAsync();
        });
        var all = Task.WhenAll(t1, t2);
        Assert.Same(all, await Task.WhenAny(all, Task.Delay(Deadline)));
        await all;

        var a = AuditHashChain.Analyze(await ChainAsync(probe, company));
        Assert.Equal(2, a.TotalRows);
        Assert.Equal(0, a.ForkCount);
        Assert.False(a.HasIntegrityFindings);
    }

    /// <summary>PL-X1: แถวลูกที่ไม่ผูกบริษัท (CompanyId ว่าง/NULL) ในธุรกรรมของบริษัท C ได้บริษัท C — สองบริษัทต่างกันทำพร้อมกันได้ ไม่รอล็อก "บริษัทว่าง" ร่วมกัน</summary>
    [Fact]
    public async Task Orphan_rows_join_their_tenant_and_two_tenants_do_not_block_each_other()
    {
        using var probe = DbTestDatabase.TryCreateContext();
        if (probe == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();
        var cSaved = new TaskCompletionSource();
        var dDone = new TaskCompletionSource();

        var tc = Task.Run(async () =>
        {
            using var db = DbTestDatabase.TryCreateContext()!;
            await using var tx = await db.Database.BeginTransactionAsync();
            db.AddChainedAuditLog(Row(c, "c-head"));
            db.AddChainedAuditLog(Row(Guid.Empty, "c-line"));   // แบบ DocumentLine
            db.AddChainedAuditLog(Row(null, "c-null"));         // แบบ AuditMiddleware ไม่มีบริษัท
            await db.SaveChangesAsync();
            cSaved.SetResult();
            await dDone.Task;                                    // ค้างธุรกรรมไว้จน D จบ
            await tx.CommitAsync();
        });
        var td = Task.Run(async () =>
        {
            await cSaved.Task;
            using var db = DbTestDatabase.TryCreateContext()!;
            db.AddChainedAuditLog(Row(d, "d-head"));
            db.AddChainedAuditLog(Row(Guid.Empty, "d-line"));
            await db.SaveChangesAsync();                         // ต้องไม่รอธุรกรรมของ C
            dDone.SetResult();
        });
        var all = Task.WhenAll(tc, td);
        Assert.Same(all, await Task.WhenAny(all, Task.Delay(Deadline)));
        await all;

        var cRows = await ChainAsync(probe, c);
        Assert.Equal(3, cRows.Count);                            // แถวลูก/NULL อยู่ใน chain ของ C
        Assert.False(AuditHashChain.Analyze(cRows).HasIntegrityFindings);
        Assert.Equal(2, (await ChainAsync(probe, d)).Count);
    }

    /// <summary>ทิศตรงข้าม: rollback ⇒ ไม่มีแถว audit ของสิ่งที่ไม่เกิด · บันทึกครั้งต่อไปของบริษัทเดียวกันต่อ chain ได้ปกติ</summary>
    [Fact]
    public async Task Rollback_drops_deferred_audit_rows()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = Guid.NewGuid();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.AddChainedAuditLog(Row(company, "gone"));
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }
        db.AddChainedAuditLog(Row(company, "kept"));
        await db.SaveChangesAsync();
        var rows = await ChainAsync(db, company);
        Assert.Single(rows);
        Assert.Equal("kept", rows[0].EntityId);
        Assert.Null(rows[0].PrevHash);
    }

    /// <summary>ฝ่ายค้านรอบสาม P1-1: ธุรกรรม Serializable ที่มีแถว audit รอประทับ ⇒ commit ล้มดัง (ไม่มีอะไรถูกบันทึก) ·
    /// ทิศตรงข้าม: Serializable ที่ไม่มีแถว audit commit ได้ · ReadCommitted บันทึกได้ตามปกติ</summary>
    [Fact]
    public async Task Serializable_transaction_with_audit_rows_fails_loud_and_read_committed_seals()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = Guid.NewGuid();
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            db.AddChainedAuditLog(Row(company, "serializable"));
            await db.SaveChangesAsync();
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => tx.CommitAsync());
            Assert.Contains("Serializable", ex.ToString());
        }
        Assert.Empty(await ChainAsync(db, company));

        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
            await tx.CommitAsync();                                  // ไม่มีแถวรอ ⇒ ไม่มีอะไรต้องตัดสิน

        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted))
        {
            db.AddChainedAuditLog(Row(company, "read-committed"));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        var rows = await ChainAsync(db, company);
        Assert.Single(rows);
        Assert.Equal("read-committed", rows[0].EntityId);
    }

    /// <summary>ฝ่ายค้านรอบสาม P2-1: แถวเกินหนึ่งชุด INSERT (500) ในการ commit เดียว ⇒ ลำดับ Id = ลำดับ chain · ไม่แตกกิ่ง · ครบทุกแถว</summary>
    [Fact]
    public async Task Batched_insert_keeps_chain_order_across_batches()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = Guid.NewGuid();
        var n = AuditChainScope.InsertBatchRows * 2 + 7;
        for (var i = 0; i < n; i++) db.AddChainedAuditLog(Row(company, $"b{i:D5}"));
        await db.SaveChangesAsync();
        var rows = await ChainAsync(db, company);
        Assert.Equal(n, rows.Count);
        Assert.Equal(Enumerable.Range(0, n).Select(i => $"b{i:D5}"), rows.Select(r => r.EntityId));
        var a = AuditHashChain.Analyze(rows);
        Assert.Equal(0, a.ForkCount);
        Assert.False(a.HasIntegrityFindings);
    }

    /// <summary>หลายคำขอพร้อมกันที่ SaveChanges หลายครั้งในธุรกรรมของตัวเอง ⇒ chain เดียว ไม่แตกกิ่ง (ประทับตอน commit ภายใต้ล็อก)</summary>
    [Fact]
    public async Task Concurrent_caller_transactions_produce_one_unbroken_chain()
    {
        using var probe = DbTestDatabase.TryCreateContext();
        if (probe == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = Guid.NewGuid();
        var go = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 6).Select(w => Task.Run(async () =>
        {
            await go.Task;
            using var db = DbTestDatabase.TryCreateContext()!;
            await using var tx = await db.Database.BeginTransactionAsync();
            db.AddChainedAuditLog(Row(company, $"w{w}-a"));
            await db.SaveChangesAsync();
            db.AddChainedAuditLog(Row(company, $"w{w}-b"));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        })).ToList();
        go.SetResult();
        var all = Task.WhenAll(tasks);
        Assert.Same(all, await Task.WhenAny(all, Task.Delay(Deadline)));
        await all;
        var a = AuditHashChain.Analyze(await ChainAsync(probe, company));
        Assert.Equal(12, a.TotalRows);
        Assert.Equal(0, a.ForkCount);
        Assert.False(a.HasIntegrityFindings);
    }
}
