using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 260-5 (คำตัดสิน 260-5-2) — งานคืนค่าสแกน API v1 ที่ถูกคิดทั้งที่สแกนล้ม/ใช้ผลเดิม บน PostgreSQL จริง ·
/// ทิศ: (1) หลังจ่าย ยังไม่เข้าใบ ⇒ ถอดออกจากรอบบิล · (2) สแกนสำเร็จจริงไม่ถูกแตะ · (3) เข้าใบแล้ว ⇒ รอใบลดหนี้ (แถวคงเดิม ไม่อ้างว่าคืน) ·
/// (4) เติมเงิน ⇒ คืนเครดิต (ยังไม่ปิดรอบ = ถอดด้วย · ปิดรอบแล้ว = แถวคงไว้) · (5) สแกนของบริษัทอื่น ⇒ ไม่แตะ ·
/// (6) audit ทุกแถว + กระดิ่งถึงเจ้าของ · (7) ลบ marker แล้วรันซ้ำ = <b>ไม่คืนเครดิตซ้ำ</b> · marker มีแล้ว = ไม่ทำอะไร
/// <para>marker เป็นของทั้งฐาน ⇒ ทุกทิศอยู่ในเทสต์เดียว · ตรวจเฉพาะแถวของบริษัทที่สร้างในเทสต์นี้</para>
/// </summary>
[Trait("Category", "Db")]
public class OcrFailedScanRefundRound2605DbTests
{
    private readonly ITestOutputHelper _out;
    public OcrFailedScanRefundRound2605DbTests(ITestOutputHelper output) => _out = output;

    private static async Task<(Guid Company, Guid Owner)> CompanyAsync(AccountingDbContext db, string tag, Guid? billingAccountId = null)
    {
        var co = new Company { Name = "บริษัทคืนค่าสแกน 260-5 " + tag, TaxId = "0105556000009", BillingAccountId = billingAccountId };
        db.Companies.Add(co);
        var u = new User { Email = $"r2605-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของ " + tag, Status = UserStatus.Active };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = co.Id, Role = UserRole.Owner });
        await db.SaveChangesAsync();
        return (co.Id, u.Id);
    }

    private static async Task<Guid> ScanAsync(AccountingDbContext db, Guid companyId, string status, string? engine)
    {
        var scan = new OcrScanResult { CompanyId = companyId, OriginalFileName = "r2605.pdf", ScanStatus = status, OcrEngine = engine };
        db.OcrScanResults.Add(scan);
        await db.SaveChangesAsync();
        return scan.Id;
    }

    private static async Task<Guid> ChargeAsync(AccountingDbContext db, Guid companyId, Guid scanId, decimal charged, Guid? account = null,
        string? billedPeriod = null, bool coveredFree = false)
    {
        var ev = new UsageEvent
        {
            CompanyId = companyId, BillingAccountId = account, FeatureCode = OcrFailedScanRefund.FeatureCode, Quantity = 1, UnitPriceSnapshot = 3m,
            ChargedAmount = charged, CoveredByFreeQuota = coveredFree, RefEntityType = OcrFailedScanRefund.RefEntityType, RefEntityId = scanId,
            BilledPeriod = billedPeriod, OccurredAt = DateTime.UtcNow.AddMonths(-2),
        };
        db.UsageEvents.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static Task<int> MarkerCountAsync(AccountingDbContext db)
    {
        var key = OcrFailedScanRefund.MarkerKey;
        return db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "OneShotDataFixes" WHERE "Key" = {key}""").SingleAsync();
    }

    [Fact]
    public async Task คืนเฉพาะสแกนที่ไม่ได้ใช้เครื่องอ่าน_ยังไม่เข้าใบถอด_เข้าใบแล้วรอใบลดหนี้_เติมเงินคืนเครดิต_รันซ้ำไม่คืนซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var tag = Guid.NewGuid().ToString("N")[..6];

        // หลังจ่าย
        var (a, ownerA) = await CompanyAsync(db, "หลังจ่าย " + tag);
        var failedUnbilled = await ChargeAsync(db, a, await ScanAsync(db, a, "Failed", "Azure"), 3m);
        var okUnbilled = await ChargeAsync(db, a, await ScanAsync(db, a, "Completed", "Azure"), 3m);
        var failedBilled = await ChargeAsync(db, a, await ScanAsync(db, a, "Failed", "Azure"), 3m, billedPeriod: "2026-09");
        var failedFree = await ChargeAsync(db, a, await ScanAsync(db, a, "Failed", null), 0m, coveredFree: true);
        // M2: สแกนล้มที่กินโควตาฟรีของเดือนที่ปิดรอบแล้ว — ไม่มีอะไรให้ถอด แต่ต้องนับให้เห็น (คิวผู้ให้บริการ)
        var failedFreeBilled = await ChargeAsync(db, a, await ScanAsync(db, a, "Failed", "Azure"), 0m, billedPeriod: "2026-09", coveredFree: true);
        // แอดมินแพลตฟอร์ม (M4) — ต้องได้กระดิ่งครั้งเดียว
        var admin = new User { Email = $"r2605-admin-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "แอดมิน " + tag, Status = UserStatus.Active, IsSystemAdmin = true };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        // เติมเงิน
        var acct = new BillingAccount { Name = "กลุ่มเติมเงิน " + tag, PaymentModel = PaymentModel.Prepaid, CreditBalance = 100m };
        db.BillingAccounts.Add(acct);
        await db.SaveChangesAsync();
        var (b, _) = await CompanyAsync(db, "เติมเงิน " + tag, acct.Id);
        var prepaidUnbilled = await ChargeAsync(db, b, await ScanAsync(db, b, "Failed", "Azure"), 5m, acct.Id);
        var prepaidBilled = await ChargeAsync(db, b, await ScanAsync(db, b, "Completed", OcrDuplicateReusePolicy.CachedEngine), 5m, acct.Id, billedPeriod: "2026-09");
        var prepaidOk = await ChargeAsync(db, b, await ScanAsync(db, b, "Completed", "Azure"), 5m, acct.Id);
        // สแกนของบริษัทอื่น (ข้ามบริษัท) — ไม่แตะ
        var (c, _) = await CompanyAsync(db, "ข้ามบริษัท " + tag);
        var crossTenant = await ChargeAsync(db, c, await ScanAsync(db, a, "Failed", "Azure"), 3m);
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "OneShotDataFixes" WHERE "Key" = {0}""", OcrFailedScanRefund.MarkerKey);

        var first = await OcrFailedScanRefund.RunAsync(db);
        Assert.True(first.Removed >= 2 && first.Credited >= 2 && first.NeedsCreditNote >= 1, first.ToString());
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(1, await MarkerCountAsync(db));

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            async Task<UsageEvent> Ev(Guid id) => await check.UsageEvents.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == id);
            Assert.True((await Ev(failedUnbilled)).IsDeleted);               // (1) ถอดจากรอบบิล
            Assert.True((await Ev(failedFree)).IsDeleted);                   // (1) คืนช่องโควตาฟรี
            Assert.False((await Ev(okUnbilled)).IsDeleted);                  // (2) สแกนสำเร็จจริงไม่ถูกแตะ
            var billed = await Ev(failedBilled);                             // (3) เข้าใบแล้ว: แถวคงเดิม
            Assert.Equal((false, 3m, "2026-09"), (billed.IsDeleted, billed.ChargedAmount, billed.BilledPeriod));
            Assert.True((await Ev(prepaidUnbilled)).IsDeleted);              // (4) เติมเงินยังไม่ปิดรอบ: ถอด + คืน
            Assert.True((await Ev(prepaidBilled)).IsDeleted);                // (4) M3: ปิดรอบแบบไม่มีใบ ⇒ คืนเครดิต + ถอด (ไม่ค้างเป็นยอดค้างเก็บ)
            Assert.False((await Ev(prepaidOk)).IsDeleted);
            Assert.Equal(110m, (await check.BillingAccounts.AsNoTracking().SingleAsync(x => x.Id == acct.Id)).CreditBalance);
            Assert.False((await Ev(crossTenant)).IsDeleted);                 // (5) ข้ามบริษัท

            // (6) audit ทุกแถวที่ตัดสิน (chain) + ไม่มีแถว audit ของแถวที่ไม่แตะ
            var audited = await check.AuditLogs.AsNoTracking()
                .Where(x => x.UserEmail == OcrFailedScanRefund.Actor && x.EntityType == nameof(UsageEvent)).Select(x => x.EntityId).ToListAsync();
            foreach (var id in new[] { failedUnbilled, failedFree, failedBilled, prepaidUnbilled, prepaidBilled, failedFreeBilled })
                Assert.Contains(id.ToString(), audited);
            foreach (var id in new[] { okUnbilled, prepaidOk, crossTenant })
                Assert.DoesNotContain(id.ToString(), audited);
            var note = await check.Notifications.AsNoTracking().SingleAsync(n => n.UserId == ownerA && n.CompanyId == a);
            Assert.Contains("ยังไม่ได้คืน", note.Message);                    // ส่วนที่เข้าใบแล้วไม่ถูกอ้างว่าคืน
            Assert.False(await check.Notifications.AsNoTracking().AnyAsync(n => n.CompanyId == c));
            Assert.Contains("โควตาฟรี", note.Message);                       // M2: นับแถวโควตาฟรีของเดือนที่ปิดรอบ
            Assert.Equal(1, await check.Notifications.AsNoTracking().CountAsync(n => n.UserId == admin.Id && n.EntityType == OcrFailedScanRefund.AdminNotificationEntityType));

            // M4: คิวใบลดหนี้ของผู้ให้บริการ — แถวที่เข้าใบแล้ว 3 บาท + แถวโควตาฟรี (0) ของบริษัท a · ไม่มีแถวที่ถอด/คืนแล้ว
            var queue = (await OcrFailedScanRefund.AwaitingCreditNotesAsync(check)).Where(q => q.CompanyId == a).ToList();
            Assert.Equal((2, 3m), (queue.Sum(q => q.Scans), queue.Sum(q => q.Amount)));
            Assert.DoesNotContain(await OcrFailedScanRefund.AwaitingCreditNotesAsync(check), q => q.CompanyId == b);
        }

        // (7) marker มีแล้ว ⇒ ไม่ทำอะไร
        var again = await OcrFailedScanRefund.RunAsync(db);
        Assert.Equal(0, again.Companies);
        // ลบ marker (เช่นกู้ฐาน) แล้วรันซ้ำ ⇒ แถวที่คืนแล้วไม่คืนซ้ำ (กุญแจ audit ต่อแถว)
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "OneShotDataFixes" WHERE "Key" = {0}""", OcrFailedScanRefund.MarkerKey);
        await OcrFailedScanRefund.RunAsync(db);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(110m, (await check.BillingAccounts.AsNoTracking().SingleAsync(x => x.Id == acct.Id)).CreditBalance);
            Assert.Equal(1, await check.AuditLogs.AsNoTracking().CountAsync(x => x.UserEmail == OcrFailedScanRefund.Actor && x.EntityId == prepaidBilled.ToString()));
            Assert.Equal(1, await check.Notifications.AsNoTracking().CountAsync(n => n.UserId == ownerA && n.CompanyId == a));
            Assert.Equal(1, await check.Notifications.AsNoTracking().CountAsync(n => n.UserId == admin.Id && n.EntityType == OcrFailedScanRefund.AdminNotificationEntityType));
        }
    }
}
