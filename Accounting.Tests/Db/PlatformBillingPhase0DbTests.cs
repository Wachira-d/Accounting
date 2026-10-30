using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 219 เฟส 0 (PLATFORM_BILLING_AUDIT · คำตัดสินข้อ 178) — การอุดรั่วเงินค่าบริการ NextAcc บน PostgreSQL จริง:
/// อัปเกรดไม่เปิดแพ็กเกจก่อนจ่าย (G-0) · ยอดแจ้งชำระมาจากเซิร์ฟเวอร์ (G-4) · handler ค่าแพ็กเกจไม่อนุมัติข้ามบริษัท/ไม่รู้ผู้จ่าย (G-3) ·
/// ค่าเหมาส่วนเสริมไม่เก็บซ้อนงวดที่จ่ายล่วงหน้า (G-5) · ส่วนเสริมค้างชำระเกินระยะผ่อนผันถูกระงับ (G-11)
/// </summary>
[Trait("Category", "Db")]
public class PlatformBillingPhase0DbTests
{
    private readonly ITestOutputHelper _out;
    public PlatformBillingPhase0DbTests(ITestOutputHelper output) => _out = output;

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    private static async Task<(Guid CompanyId, Subscription Sub)> SeedAsync(Accounting.Data.AccountingDbContext db,
        SubscriptionStatus status, SubscriptionPlan plan = SubscriptionPlan.FreeTrial)
    {
        var company = new Company { Name = "บริษัททดสอบค่าบริการเฟส 0", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var sub = new Subscription
        {
            CompanyId = company.Id, Plan = plan, Status = status,
            StartDate = DateTime.UtcNow.AddDays(-3), EndDate = DateTime.UtcNow.AddDays(20), UsageResetDate = DateTime.UtcNow.AddDays(20),
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        return (company.Id, sub);
    }

    /// <summary>ให้มีแม่แบบแพ็กเกจที่มีราคา (ฐานร่วม — ใช้ของเดิมถ้ามี)</summary>
    private static async Task EnsurePricedTemplateAsync(Accounting.Data.AccountingDbContext db, SubscriptionPlan plan)
    {
        if (await db.PlanTemplates.AnyAsync(t => t.Plan == plan && t.IsActive && t.MonthlyPrice > 0)) return;
        db.PlanTemplates.Add(new PlanTemplate
        {
            Name = "แพ็กเกจทดสอบเฟส 0", Plan = plan, IsActive = true,
            MonthlyPrice = 990m, QuarterlyPrice = 2800m, SemiAnnualPrice = 5400m, AnnualPrice = 9900m,
            MaxUsers = 5, MaxCompanies = 1, MaxDocumentsPerMonth = 500, MaxJournalEntriesPerMonth = 500,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Convert_trial_creates_pending_payment_and_does_not_activate_paid_plan()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        await EnsurePricedTemplateAsync(db, SubscriptionPlan.Pro);
        var (cid, sub) = await SeedAsync(db, SubscriptionStatus.Trial);

        var svc = new SubscriptionService(db, Notifications());
        var res = await svc.ConvertTrialAsync(cid, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Monthly), "owner");

        // G-0: ทดลองใช้ต่อ — ไม่ได้แพ็กเกจจ่ายเงินฟรี
        var after = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Equal(SubscriptionStatus.Trial, after.Status);
        Assert.Equal(SubscriptionPlan.FreeTrial, after.Plan);
        Assert.NotNull(res.Payment);
        var pay = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.SubscriptionId == sub.Id);
        Assert.Equal(SubscriptionPaymentStatus.Pending, pay.Status);
        Assert.Equal(SubscriptionPlan.Pro, pay.RequestedPlan);
        Assert.Equal(1, pay.RequestedPeriodMonths);
        Assert.True(pay.Amount > 0m);

        // กดซ้ำ = รายการเดิม (ไม่เกิดรายการซ้อน)
        var again = await svc.ConvertTrialAsync(cid, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Monthly), "owner");
        Assert.Equal(res.Payment!.Id, again.Payment!.Id);
        Assert.Equal(1, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id));
    }

    [Fact]
    public async Task Submit_payment_with_wrong_amount_is_409_and_server_amount_is_stored()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsurePricedTemplateAsync(db, SubscriptionPlan.Pro);
        var (cid, sub) = await SeedAsync(db, SubscriptionStatus.Active, SubscriptionPlan.Pro);
        var svc = new SubscriptionService(db, Notifications());

        SubmitSubscriptionPaymentRequest Req(decimal amount, int months) => new(sub.Id, amount, DateTime.UtcNow, PaymentMethod.BankTransfer,
            null, null, null, null, null, SubscriptionPlan.Pro, BillingCycle.Annual, months, null);

        // G-4: ฿1 ขอรายปี ⇒ 409 · ไม่เกิดรายการ
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SubmitPaymentAsync(cid, Req(1m, 12), "owner"));
        Assert.Equal(409, ex.StatusCode);
        // ขอ 120 เดือนบนรอบรายปี ⇒ ปฏิเสธ
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SubmitPaymentAsync(cid, Req(0m, 120), "owner"));
        Assert.Equal(0, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id));

        // ไม่แจ้งยอด ⇒ ใช้ยอดของเซิร์ฟเวอร์
        var ok = await svc.SubmitPaymentAsync(cid, Req(0m, 0), "owner");
        var tpl = await db.PlanTemplates.AsNoTracking().FirstAsync(t => t.Plan == SubscriptionPlan.Pro && t.IsActive);
        Assert.Equal(PlatformPlanPricing.AmountDue(tpl, BillingCycle.Annual), ok.Amount);
        Assert.Equal(12, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == ok.Id)).RequestedPeriodMonths);
    }

    [Fact]
    public async Task Subscription_handler_refuses_unknown_or_foreign_payer()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sub) = await SeedAsync(db, SubscriptionStatus.Active, SubscriptionPlan.Pro);
        var (otherCid, _) = await SeedAsync(db, SubscriptionStatus.Active, SubscriptionPlan.Pro);
        var pay = new SubscriptionPayment
        {
            SubscriptionId = sub.Id, PaymentNumber = "SP-TEST-219", Amount = 990m, PaymentDate = DateTime.UtcNow,
            RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = 1,
            Status = SubscriptionPaymentStatus.Pending,
        };
        db.SubscriptionPayments.Add(pay);
        await db.SaveChangesAsync();

        var reviewed = false;
        var subs = F4Proxy<ISubscriptionService>.Create((name, _) => { if (name == nameof(ISubscriptionService.ReviewPaymentAsync)) reviewed = true; return null; });
        var handler = new SubscriptionPaymentHandler(db, subs, NullLogger<SubscriptionPaymentHandler>.Instance);

        // G-3: intent ที่ไม่รู้ผู้จ่าย (รุ่นก่อนเฟส 0 — เกิดใต้บริษัทของร้าน) และ intent ของบริษัทอื่น ⇒ ไม่อนุมัติ
        foreach (var payer in new Guid?[] { null, otherCid })
        {
            var intent = new PaymentIntent
            {
                CompanyId = cid, PayerCompanyId = payer, ProviderCode = "manual-slip",
                SourceKind = PaymentSourceKind.SubscriptionPayment, SourceId = pay.Id, Amount = 990m,
            };
            await Assert.ThrowsAsync<BusinessRuleException>(() => handler.HandleSucceededAsync(intent));
        }
        Assert.False(reviewed);
        Assert.Equal(SubscriptionPaymentStatus.Pending, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).Status);
    }

    private static async Task<(Guid CompanyId, string Code)> SeedFlatAddOnAsync(Accounting.Data.AccountingDbContext db,
        AddOnPaymentStatus status, string? upfrontPeriod, DateTime? awaitingSince)
    {
        var code = "test.flat219." + Guid.NewGuid().ToString("N")[..8];
        var company = new Company { Name = "ร้านทดสอบส่วนเสริมเฟส 0", TaxId = "0105556000002" };
        db.Companies.Add(company);
        db.ApiPricingPlans.Add(new ApiPricingPlan
        {
            FeatureCode = code, Method = PricingMethod.FlatMonthly, UnitPrice = 299m, EffectiveFrom = DateTime.UtcNow.AddDays(-30),
        });
        await db.SaveChangesAsync();
        db.CompanyFeatures.Add(new CompanyFeature
        {
            CompanyId = company.Id, FeatureCode = code, IsEnabled = true, EnabledAt = DateTime.UtcNow.AddDays(-1),
            GrantSource = AddOnGrantSource.OwnerSelfServe, AcceptedUnitPrice = 299m,
            PaymentStatus = status, UpfrontPeriod = upfrontPeriod, PaymentAwaitingSince = awaitingSince,
        });
        await db.SaveChangesAsync();
        return (company.Id, code);
    }

    [Fact]
    public async Task Flat_monthly_skips_period_paid_upfront_but_legacy_rows_still_charged()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var period = AddOnBilling.PeriodOf(DateTime.UtcNow);
        var metering = new UsageMeteringService(db, NullLogger<UsageMeteringService>.Instance);

        // G-5: จ่ายล่วงหน้างวดนี้แล้ว ⇒ แถว ฿0 (ไม่เก็บซ้อน)
        var (paidCid, paidCode) = await SeedFlatAddOnAsync(db, AddOnPaymentStatus.Paid, period, DateTime.UtcNow.AddDays(-1));
        var r1 = await metering.RecordFlatMonthlyAsync(paidCid, paidCode, period);
        Assert.True(r1.Recorded);
        Assert.Equal(0m, r1.ChargedAmount);

        // ทิศตรงข้าม: แถวเดิมที่ไม่ได้เข้าเส้นจ่ายล่วงหน้า ⇒ คิดเต็มเหมือนเดิม
        var (legacyCid, legacyCode) = await SeedFlatAddOnAsync(db, AddOnPaymentStatus.AwaitingPayment, null, null);
        var r2 = await metering.RecordFlatMonthlyAsync(legacyCid, legacyCode, period);
        Assert.True(r2.Recorded);
        Assert.Equal(299m, r2.ChargedAmount);
    }

    [Fact]
    public async Task Unpaid_addon_is_disabled_after_grace_but_legacy_and_pending_review_still_usable()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var metering = new UsageMeteringService(db, NullLogger<UsageMeteringService>.Instance);
        var period = AddOnBilling.PeriodOf(DateTime.UtcNow);

        var (lateCid, lateCode) = await SeedFlatAddOnAsync(db, AddOnPaymentStatus.AwaitingPayment, period, DateTime.UtcNow.AddDays(-8));
        Assert.False(await metering.IsFeatureEnabledAsync(lateCid, lateCode));
        var (freshCid, freshCode) = await SeedFlatAddOnAsync(db, AddOnPaymentStatus.AwaitingPayment, period, DateTime.UtcNow.AddDays(-1));
        Assert.True(await metering.IsFeatureEnabledAsync(freshCid, freshCode));
        var (slipCid, slipCode) = await SeedFlatAddOnAsync(db, AddOnPaymentStatus.PendingReview, period, DateTime.UtcNow.AddDays(-30));
        Assert.True(await metering.IsFeatureEnabledAsync(slipCid, slipCode));
        var (legacyCid, legacyCode) = await SeedFlatAddOnAsync(db, AddOnPaymentStatus.AwaitingPayment, null, null);
        Assert.True(await metering.IsFeatureEnabledAsync(legacyCid, legacyCode));
    }
}
