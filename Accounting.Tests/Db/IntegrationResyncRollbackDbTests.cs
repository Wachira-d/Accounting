using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ฝ่ายค้านรอบสอง RV2-8 (รอบ 201 ทีม GW) — resync ที่ล้มกลางธุรกรรมต้อง <c>ChangeTracker.Clear()</c> ก่อนโยนต่อ
/// (<c>IntegrationService.RunResyncLockedAsync</c>) เพราะผู้เรียก (<c>HandleSyncError → SaveSyncLog</c>) เรียก SaveChanges <b>นอกธุรกรรม</b>
///
/// <para>ใช้แถว <c>AuditLogs</c> แทน JE (ไม่มี FK ไปบริษัท · แยกด้วย CompanyId สุ่ม) — กลไกเดียวกัน: ของที่ค้าง "Added" ใน change tracker ตอน
/// ธุรกรรม rollback จะถูก INSERT โดย SaveChanges ครั้งถัดไป · สองทิศ: ไม่ Clear = ของที่ rollback แล้วโผล่ (พิสูจน์ว่ากลไกจริง) · Clear = มีแค่ sync log</para>
/// </summary>
[Trait("Category", "Db")]
public class IntegrationResyncRollbackDbTests
{
    private readonly ITestOutputHelper _out;
    public IntegrationResyncRollbackDbTests(ITestOutputHelper output) => _out = output;

    private static AuditLog Row(Guid companyId, string id) => new()
    {
        CompanyId = companyId, UserEmail = "resync@example.com", Action = AuditAction.Create,
        EntityType = "ResyncRollbackDbTest", EntityId = id, NewValues = "{}", Timestamp = DateTime.UtcNow,
    };

    private static async Task<List<string>> IdsAsync(Accounting.Data.AccountingDbContext db, Guid companyId)
        => await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId && a.EntityType == "ResyncRollbackDbTest")
            .OrderBy(a => a.EntityId).Select(a => a.EntityId).ToListAsync();

    /// <summary>จำลองธุรกรรม resync ที่ล้มหลังบันทึกไปบางส่วน: A บันทึกในธุรกรรมแล้ว · B ค้าง Added · โยน ⇒ rollback</summary>
    private static async Task FailMidTransactionAsync(Accounting.Data.AccountingDbContext db, Guid companyId)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        db.AuditLogs.Add(Row(companyId, "A-reversal"));
        await db.SaveChangesAsync();
        db.AuditLogs.Add(Row(companyId, "B-new-je"));
        // ล้มก่อน SaveChanges/commit — dispose = rollback
    }

    [Fact]
    public async Task ไม่Clear_SaveChangesถัดไปบันทึกของที่rollbackแล้วครึ่งเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var companyId = Guid.NewGuid();
        await FailMidTransactionAsync(db, companyId);
        db.AuditLogs.Add(Row(companyId, "C-sync-log"));   // แทน SaveSyncLog ของ HandleSyncError
        await db.SaveChangesAsync();
        // A หาย (rollback) แต่ B ถูก INSERT นอกธุรกรรม = ครึ่ง ๆ กลาง ๆ — ข้อบกพร่องที่ RV2-8 ปิด
        Assert.Equal(new[] { "B-new-je", "C-sync-log" }, await IdsAsync(db, companyId));
    }

    [Fact]
    public async Task Clearหลังrollback_มีแค่synclog()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var companyId = Guid.NewGuid();
        await FailMidTransactionAsync(db, companyId);
        db.ChangeTracker.Clear();                          // สิ่งที่ RunResyncLockedAsync ทำใน catch ก่อนโยนต่อ
        db.AuditLogs.Add(Row(companyId, "C-sync-log"));
        await db.SaveChangesAsync();
        Assert.Equal(new[] { "C-sync-log" }, await IdsAsync(db, companyId));
    }
}
