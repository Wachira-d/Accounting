using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// round-trip write→verify ของ audit hash chain บน PostgreSQL จริง (รอบ 201 ทีม PL · A-PL1 + A-PL2 · คำตัดสินข้อ 32/33 ·
/// กฎเหล็ก #4 G "control ที่ไม่มี round-trip test = ไม่มี control")
///
/// <para>ต่างจาก <c>AuditHashChainTests</c> (จำลองการอ่านกลับในหน่วยความจำ): ที่นี่เขียนผ่าน <c>AccountingDbContext.SaveChangesAsync</c>
/// ตัวจริง (ถอดแถว → ล็อก <c>pg_advisory_xact_lock</c> → ประทับ) หลาย context พร้อมกัน แล้วอ่านกลับจาก Npgsql ให้
/// <see cref="AuditHashChain.Analyze"/> ตัดสิน — ถ้าถอดล็อกออก การเขียนพร้อมกันจะอ่านปลาย chain เดียวกันแล้วแตกกิ่ง (ForkCount &gt; 0)</para>
///
/// <para>ทิศตรงข้าม: <see cref="Verifier_sees_fork_written_outside_the_lock"/> เขียนสองแถวที่ผูกปลายเดียวกันด้วย SQL ตรง (นอกล็อก) —
/// ตัวตรวจต้องเห็น fork จากข้อมูลที่อ่านกลับจากฐาน (พิสูจน์ว่าเทสต์บวกไม่ได้ผ่านเพราะตัวตรวจตาบอด)</para>
/// </summary>
[Trait("Category", "Db")]
public class AuditChainDbTests
{
    private readonly ITestOutputHelper _out;
    public AuditChainDbTests(ITestOutputHelper output) => _out = output;

    private static AuditLog Row(Guid companyId, int worker, int i) => new()
    {
        CompanyId = companyId,
        UserEmail = $"w{worker}@example.com",
        Action = AuditAction.Update,
        EntityType = "DbTest",
        EntityId = $"{worker}-{i}",
        NewValues = $"{{\"worker\":{worker},\"i\":{i}}}",
        Timestamp = DateTime.UtcNow,
    };

    private static async Task<List<AuditLog>> ReadChainAsync(AccountingDbContext db, Guid companyId)
        => await db.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.RowHash != null)
            .OrderBy(a => a.Id)
            .ToListAsync();

    [Fact]
    public async Task Concurrent_writers_of_one_company_produce_one_unbroken_chain()
    {
        using var probe = DbTestDatabase.TryCreateContext();
        if (probe == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }

        var companyId = Guid.NewGuid();
        const int workers = 8, perWorker = 6;
        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < perWorker; i++)
            {
                using var db = DbTestDatabase.TryCreateContext()!;
                db.AddChainedAuditLog(Row(companyId, w, i));
                if (i % 2 == 0) db.AddChainedAuditLog(Row(companyId, w, 100 + i));   // สองแถวในคำขอเดียว
                await db.SaveChangesAsync();
            }
        })).ToList();
        start.SetResult();
        await Task.WhenAll(tasks);

        var rows = await ReadChainAsync(probe, companyId);
        var a = AuditHashChain.Analyze(rows);
        _out.WriteLine($"rows={a.TotalRows} forks={a.ForkCount} tampered={a.Tampered.Count} dangling={a.Dangling.Count}");
        Assert.Equal(workers * (perWorker + perWorker / 2), a.TotalRows);
        Assert.Equal(0, a.ForkCount);
        Assert.Empty(a.Tampered);     // ประทับแล้วอ่านกลับผ่าน Npgsql ตรง (สูตร v2 round-trip)
        Assert.Empty(a.Dangling);
        Assert.True(rows.All(r => r.RowHash!.StartsWith(AuditHashChain.V2Prefix)));
    }

    [Fact]
    public async Task Direct_AuditLogs_Add_is_sealed_into_the_chain_too()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var companyId = Guid.NewGuid();
        db.AuditLogs.Add(Row(companyId, 0, 1));          // เส้นเดิมที่ยังเหลือในเรพ — ต้องไม่หลุดนอก chain
        db.AddChainedAuditLog(Row(companyId, 0, 2));
        await db.SaveChangesAsync();

        var rows = await ReadChainAsync(db, companyId);
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].PrevHash);
        Assert.Equal(rows[0].RowHash, rows[1].PrevHash);
        Assert.False(AuditHashChain.Analyze(rows).HasIntegrityFindings);
    }

    [Fact]
    public async Task Verifier_sees_fork_written_outside_the_lock()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var companyId = Guid.NewGuid();
        db.AddChainedAuditLog(Row(companyId, 0, 1));
        await db.SaveChangesAsync();
        var tip = (await ReadChainAsync(db, companyId)).Single().RowHash;

        // จำลองสองคำขอที่อ่านปลายเดียวกันโดยไม่มีล็อก (พฤติกรรมก่อนรอบ 201) — เขียนด้วย SQL ตรง ข้าม SaveChanges
        foreach (var i in new[] { 2, 3 })
        {
            var r = Row(companyId, 0, i);
            AuditHashChain.Seal(r, tip);
            await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ""AuditLogs""
                (""CompanyId"", ""UserEmail"", ""Action"", ""EntityType"", ""EntityId"", ""NewValues"", ""Timestamp"", ""PrevHash"", ""RowHash"")
                VALUES ({r.CompanyId}, {r.UserEmail}, {(int)r.Action}, {r.EntityType}, {r.EntityId}, {r.NewValues}, {r.Timestamp}, {r.PrevHash}, {r.RowHash})");
        }

        var a = AuditHashChain.Analyze(await ReadChainAsync(db, companyId));
        Assert.Equal(3, a.TotalRows);
        Assert.Equal(1, a.ForkCount);
        Assert.False(a.HasIntegrityFindings);   // fork ≠ ถูกแก้ — ทุกแถวยังตรวจเนื้อผ่านหลัง round-trip
    }
}
