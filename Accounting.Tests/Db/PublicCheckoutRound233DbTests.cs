using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 233 ฝ่ายค้าน S6/S5 — <see cref="PublicCheckoutService"/> ระดับ service บน PostgreSQL จริง (ข้ามเมื่อไม่มีฐาน):
/// ยอดตะกร้าที่ถูกปฏิเสธ = ใบเสนอ <c>Allowed=false</c> ไม่สร้างอะไร · สร้างรายการที่ถูกปฏิเสธ = throw ไม่มี side-effect (ไม่มี ScheduledPlan/รายการชำระใหม่) ·
/// บริษัทที่เป็นเจ้าของเท่านั้น + lowerPlans · resume รายการส่วนต่างเก่าที่ยอดไม่ตรง ⇒ ออกรายการใหม่ ยกเลิกใบเก่า (ไม่ซ้อน)
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class PublicCheckoutRound233DbTests
{
    private readonly ITestOutputHelper _out;
    public PublicCheckoutRound233DbTests(ITestOutputHelper output) => _out = output;

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    private static PublicCheckoutService Svc(Accounting.Data.AccountingDbContext db)
        => new(db, new SubscriptionService(db, Notifications()),
            F4Proxy<IPlatformPaymentService>.Create((name, _) => name == nameof(IPlatformPaymentService.StatusAsync)
                ? new PlatformPayStatus(false, false, null, null, Array.Empty<string>(), null) : null),
            F4Proxy<ILegalDocumentService>.Create((_, _) => null), F4Proxy<IEmailService>.Create((_, _) => null),
            NullLogger<PublicCheckoutService>.Instance);

    /// <summary>แม่แบบที่มีราคา (ฐานร่วม — ใช้ของเดิมถ้ามี) — คืนแม่แบบที่ใช้จริง</summary>
    private static async Task<PlanTemplate> PricedAsync(Accounting.Data.AccountingDbContext db, SubscriptionPlan plan, decimal monthly)
    {
        var t = await db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Plan == plan && x.IsActive);
        if (t != null) return t;
        t = new PlanTemplate
        {
            Name = "แพ็กเกจทดสอบ 233 " + plan, Plan = plan, IsActive = true,
            MonthlyPrice = monthly, QuarterlyPrice = monthly * 3, SemiAnnualPrice = monthly * 6, AnnualPrice = monthly * 12,
            MaxUsers = 5, MaxCompanies = 1, MaxDocumentsPerMonth = 500, MaxJournalEntriesPerMonth = 500,
        };
        db.PlanTemplates.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    private static async Task<(Guid UserId, Guid CompanyId, Subscription Sub)> SeedAsync(Accounting.Data.AccountingDbContext db, SubscriptionPlan plan,
        BillingCycle cycle, UserRole role = UserRole.Owner, User? user = null)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        user ??= new User { Email = $"pc233-{tag}@test.local", PasswordHash = "x", FullName = "ผู้ซื้อทดสอบ 233" };
        if (db.Entry(user).State == EntityState.Detached) db.Users.Add(user);
        var c = new Company { Name = "บริษัททดสอบหน้าซื้อ 233 " + tag, TaxId = "0105556000009" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = user.Id, CompanyId = c.Id, Role = role });
        var sub = new Subscription
        {
            CompanyId = c.Id, Plan = plan, Status = SubscriptionStatus.Active, BillingCycle = cycle,
            StartDate = DateTime.UtcNow.AddDays(-100), EndDate = DateTime.UtcNow.AddDays(200), UsageResetDate = DateTime.UtcNow.AddDays(20),
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        return (user.Id, c.Id, sub);
    }

    /// <summary>เปิดสวิตช์หน้าซื้อชั่วคราว แล้วคืนค่าเดิมเสมอ (แถวเดียวของระบบ)</summary>
    private static async Task WithCheckoutEnabledAsync(Accounting.Data.AccountingDbContext db, Func<Task> body)
    {
        var s = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
        var created = s == null;
        if (s == null) { s = new SiteSettings(); db.SiteSettings.Add(s); }
        var original = s.PublicCheckoutEnabled;
        s.PublicCheckoutEnabled = true;
        await db.SaveChangesAsync();
        try { await body(); }
        finally
        {
            var again = await db.SiteSettings.FirstAsync(x => x.Id == s.Id);
            if (created) db.SiteSettings.Remove(again); else again.PublicCheckoutEnabled = original;
            await db.SaveChangesAsync();
        }
    }

    private static async Task<bool> DowngradeShapeAsync(Accounting.Data.AccountingDbContext db)
    {
        var ent = await PricedAsync(db, SubscriptionPlan.Enterprise, 4_990m);
        var pro = await PricedAsync(db, SubscriptionPlan.Pro, 1_490m);
        return PlatformPlanPricing.Classify(ent.MonthlyPrice, pro.MonthlyPrice) == PlatformPlanPricing.ChangeKind.Downgrade
               && PublicCheckoutPolicy.IsSellable(pro.IsActive, pro.IsPermanentFree, pro.MonthlyPrice);
    }

    [Fact]
    public async Task Quote_downgrade_returns_refused_preview_and_creates_nothing()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        if (!await DowngradeShapeAsync(db)) { _out.WriteLine("แม่แบบในฐานไม่เป็นรูปดาวน์เกรด — ข้าม"); return; }
        var (_, cid, sub) = await SeedAsync(db, SubscriptionPlan.Enterprise, BillingCycle.Annual);
        var q = await Svc(db).QuoteAsync(cid, SubscriptionPlan.Pro, BillingCycle.Annual, null);
        Assert.NotNull(q.Preview);
        Assert.False(q.Preview!.Allowed);
        Assert.Equal("Refused", q.Preview.Kind);
        Assert.Equal(0m, q.AmountDue);
        Assert.Contains("ลดเป็น", q.Preview.RefusalReason);
        Assert.Equal(0, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id));
        Assert.Null((await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id)).ScheduledPlan);
    }

    [Fact]
    public async Task CreateOrder_downgrade_throws_same_reason_without_side_effects()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        if (!await DowngradeShapeAsync(db)) { _out.WriteLine("แม่แบบในฐานไม่เป็นรูปดาวน์เกรด — ข้าม"); return; }
        var (uid, cid, sub) = await SeedAsync(db, SubscriptionPlan.Enterprise, BillingCycle.Annual);
        await WithCheckoutEnabledAsync(db, async () =>
        {
            var svc = Svc(db);
            var quote = await svc.QuoteAsync(cid, SubscriptionPlan.Pro, BillingCycle.Annual, null);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.CreateOrderAsync(uid, cid, SubscriptionPlan.Pro, BillingCycle.Annual, null));
            Assert.Equal(quote.Preview!.RefusalReason, ex.Message);   // ข้อความเดียวกับกล่องสรุป
        });
        Assert.Equal(0, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id));
        var after = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Null(after.ScheduledPlan);
        Assert.Equal(SubscriptionPlan.Enterprise, after.Plan);
    }

    [Fact]
    public async Task OwnedCompanies_only_owner_rows_and_lower_plans_from_server()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var shape = await DowngradeShapeAsync(db);
        var (uid, owned, _) = await SeedAsync(db, SubscriptionPlan.Enterprise, BillingCycle.Annual);
        var user = await db.Users.FirstAsync(u => u.Id == uid);
        var (_, notOwned, _) = await SeedAsync(db, SubscriptionPlan.Enterprise, BillingCycle.Annual, UserRole.Accountant, user);
        var rows = await Svc(db).OwnedCompaniesAsync(uid);
        Assert.Contains(rows, r => r.Id == owned);
        Assert.DoesNotContain(rows, r => r.Id == notOwned);   // เป็นแค่นักบัญชี ⇒ ไม่เห็น (ไม่เปิดข้อมูลบริษัทอื่น)
        var row = rows.Single(r => r.Id == owned);
        if (shape)
        {
            Assert.Contains("Pro", row.LowerPlans);
            Assert.DoesNotContain("Enterprise", row.LowerPlans);
            Assert.Contains("ต่ำกว่าแพ็กปัจจุบัน", row.LowerNote);
        }
    }

    [Fact]
    public async Task Resume_stale_proration_requotes_and_cancels_old_without_duplicates()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var basic = await PricedAsync(db, SubscriptionPlan.Basic, 590m);
        var pro = await PricedAsync(db, SubscriptionPlan.Pro, 1_490m);
        if (PlatformPlanPricing.Classify(basic.MonthlyPrice, pro.MonthlyPrice) != PlatformPlanPricing.ChangeKind.Upgrade || basic.AnnualPrice <= 0m)
        { _out.WriteLine("แม่แบบในฐานไม่เป็นรูปอัปเกรด — ข้าม"); return; }
        var (uid, cid, sub) = await SeedAsync(db, SubscriptionPlan.Basic, BillingCycle.Annual);
        // ใบส่วนต่างเก่า (ยอดคิดเมื่อหลายวันก่อน — ไม่ตรงวันนี้) ยังไม่มีสลิป/ไม่เคยเริ่มชำระ
        var old = new SubscriptionPayment
        {
            SubscriptionId = sub.Id, PaymentNumber = "SP-233R-" + Guid.NewGuid().ToString("N")[..6], Amount = 1m, QuotedAmount = 1m, PaymentDate = DateTime.UtcNow.AddDays(-3),
            RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Annual, RequestedPeriodMonths = 0, Status = SubscriptionPaymentStatus.Pending,
            IsProration = true, ProrationFromPlan = "Basic", ProrationDays = 300, ProrationCycleDays = 365, ProrationCycleEndUtc = sub.EndDate,
        };
        db.SubscriptionPayments.Add(old);
        await db.SaveChangesAsync();
        PublicCheckoutOrder? order = null;
        await WithCheckoutEnabledAsync(db, async () =>
            order = await Svc(db).CreateOrderAsync(uid, cid, SubscriptionPlan.Pro, BillingCycle.Annual, null));
        Assert.NotNull(order);
        Assert.False(order!.Resumed);
        Assert.NotEqual(old.Id, order.PaymentId);
        Assert.Equal(SubscriptionPaymentStatus.Cancelled, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == old.Id)).Status);
        Assert.Equal(1, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id && p.Status == SubscriptionPaymentStatus.Pending));
        Assert.NotEqual(1m, order.Amount);   // ยอดคิดใหม่วันนี้
    }
}
