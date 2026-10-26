using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 205 ทีม W1 (R-05 · BOOKING_REVENUE_AUDIT Q-6) — <b>ตัวนับโควตาเอกสารต้องไม่นับขาดเมื่อสองคำขอชนกัน</b>
///
/// <para>เดิม <c>sub.CurrentMonthDocuments++</c> อ่าน-แก้-เขียนบน entity (คนละ DbContext = คนละ request) ⇒ สองใบที่สร้างพร้อมกันเขียนทับกัน
/// นับได้ 1 · ตอนนี้ <c>UPDATE … SET n = n + 1 RETURNING</c> บนฐาน ⇒ N คำขอพร้อมกัน = +N</para>
///
/// <para>ทิศตรงข้าม (ลำดับล็อก "ใบล้ม = ไม่นับ"): เพิ่มตัวนับในธุรกรรมแล้ว rollback ⇒ ตัวนับในฐานไม่ขยับ</para>
/// </summary>
[Trait("Category", "Db")]
public class DocumentQuotaCounterDbTests
{
    private readonly ITestOutputHelper _out;
    public DocumentQuotaCounterDbTests(ITestOutputHelper output) => _out = output;

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    private static async Task<Guid> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบตัวนับโควตา", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.Subscriptions.Add(new Subscription
        {
            CompanyId = company.Id, Plan = SubscriptionPlan.FreeTrial, Status = SubscriptionStatus.Trial,
            StartDate = DateTime.UtcNow.AddDays(-1), EndDate = DateTime.UtcNow.AddDays(30),
            // เพดานสูง ⇒ ไม่ข้ามระดับเตือน (เทสต์นี้วัดตัวนับอย่างเดียว)
            MaxDocumentsPerMonth = 100000, UsageResetDate = DateTime.UtcNow.AddDays(20),
        });
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static async Task IncrementOnceAsync(Guid companyId)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        var svc = new SubscriptionService(db, Notifications());
        await svc.IncrementUsageAsync(companyId, "document");
    }

    private static async Task<int> CounterAsync(Guid companyId)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        return await db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == companyId).Select(s => s.CurrentMonthDocuments).SingleAsync();
    }

    [Fact]
    public async Task R05_แปดคำขอพร้อมกัน_ตัวนับเพิ่มครบแปด()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedAsync(seedDb);

        await Task.WhenAll(Enumerable.Range(1, 8).Select(_ => Task.Run(() => IncrementOnceAsync(cid))));

        Assert.Equal(8, await CounterAsync(cid));
    }

    [Fact]
    public async Task R05_ทิศตรงข้าม_ธุรกรรมถูกถอย_ตัวนับไม่ขยับ()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedAsync(seedDb);

        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var svc = new SubscriptionService(db, Notifications());
            await svc.IncrementUsageAsync(cid, "document");
            // ค่าใน entity ตรงกับฐาน (RETURNING) และ SaveChanges ซ้ำไม่เขียนทับ
            var sub = await db.Subscriptions.SingleAsync(s => s.CompanyId == cid);
            Assert.Equal(1, sub.CurrentMonthDocuments);
            await db.SaveChangesAsync();
            await tx.RollbackAsync();   // = การสร้างใบล้มหลังนับ
        }

        Assert.Equal(0, await CounterAsync(cid));
    }
}
