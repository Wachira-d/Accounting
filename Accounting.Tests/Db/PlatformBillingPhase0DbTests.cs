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

    // ═══ ฝ่ายค้านรอบ 219 ═══

    /// <summary>แม่แบบที่ service จะเลือก (คำค้นเดียวกับ service) — ฐานร่วมอาจมีหลายแถว ⇒ เทสต์ตัดสินจากแถวที่ service เห็นจริง</summary>
    private static Task<PlanTemplate?> TemplateAsync(Accounting.Data.AccountingDbContext db, SubscriptionPlan plan)
        => db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Plan == plan && p.IsActive);

    [Fact]
    public async Task Permanent_free_upgrade_approved_starts_period_from_now_not_after_100_years()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsurePricedTemplateAsync(db, SubscriptionPlan.Pro);
        var (cid, sub) = await SeedAsync(db, SubscriptionStatus.Active, SubscriptionPlan.FreeTrial);
        sub.IsPermanentFree = true;
        sub.EndDate = DateTime.UtcNow.AddYears(100);
        await db.SaveChangesAsync();

        var svc = new SubscriptionService(db, Notifications());
        var res = await svc.ConvertTrialAsync(cid, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Monthly), "owner");
        Assert.NotNull(res.Payment);
        await svc.ReviewPaymentAsync(res.Payment!.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);

        var after = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        var tpl = await TemplateAsync(db, SubscriptionPlan.Pro);
        Assert.Equal(SubscriptionPlan.Pro, after.Plan);
        if (tpl is { IsPermanentFree: false })
        {
            Assert.False(after.IsPermanentFree);
            // B1: ≈ วันนี้ + 1 เดือน (เดิม +100 ปี +1 เดือน)
            Assert.True(after.EndDate < DateTime.UtcNow.AddDays(32), $"EndDate {after.EndDate:O}");
            Assert.True(after.EndDate > DateTime.UtcNow.AddDays(27));
        }
    }

    [Fact]
    public async Task Mid_cycle_upgrade_starts_now_but_same_plan_renewal_extends_from_end()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsurePricedTemplateAsync(db, SubscriptionPlan.Pro);
        var basic = await TemplateAsync(db, SubscriptionPlan.Basic);
        var pro = await TemplateAsync(db, SubscriptionPlan.Pro);
        var svc = new SubscriptionService(db, Notifications());

        async Task<Subscription> PayAsync(SubscriptionPlan from, SubscriptionPlan to)
        {
            var (_, sub) = await SeedAsync(db, SubscriptionStatus.Active, from);
            sub.BillingCycle = BillingCycle.Annual;
            sub.EndDate = DateTime.UtcNow.AddDays(200);
            var pay = new SubscriptionPayment
            {
                SubscriptionId = sub.Id, PaymentNumber = "SP-B1-" + Guid.NewGuid().ToString("N")[..6], Amount = 990m, PaymentDate = DateTime.UtcNow,
                RequestedPlan = to, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = 1, Status = SubscriptionPaymentStatus.Pending,
            };
            db.SubscriptionPayments.Add(pay);
            await db.SaveChangesAsync();
            await svc.ReviewPaymentAsync(pay.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);
            return await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        }

        // ทิศตรงข้าม: ต่ออายุแพ็กเกจเดิม ⇒ ต่อจากวันหมดอายุเดิม (200 วัน + 1 เดือน)
        var renewed = await PayAsync(SubscriptionPlan.Pro, SubscriptionPlan.Pro);
        Assert.True(renewed.EndDate > DateTime.UtcNow.AddDays(220));

        // Starter รายปีกลางรอบ → Pro รายเดือน (อัปเกรด) ⇒ ≈ วันนี้ + 1 เดือน
        if (basic != null && pro != null
            && PlatformPlanPricing.Classify(basic.MonthlyPrice, pro.MonthlyPrice) == PlatformPlanPricing.ChangeKind.Upgrade && !pro.IsPermanentFree)
        {
            var upgraded = await PayAsync(SubscriptionPlan.Basic, SubscriptionPlan.Pro);
            Assert.Equal(SubscriptionPlan.Pro, upgraded.Plan);
            Assert.True(upgraded.EndDate < DateTime.UtcNow.AddDays(32), $"EndDate {upgraded.EndDate:O}");
        }
    }

    [Fact]
    public async Task Scheduled_downgrade_to_permanent_free_plan_stays_active()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        if (!await db.PlanTemplates.AnyAsync(t => t.Plan == SubscriptionPlan.FreeTrial && t.IsActive))
        {
            db.PlanTemplates.Add(new PlanTemplate { Name = "ฟรีถาวร (ทดสอบ)", Plan = SubscriptionPlan.FreeTrial, IsActive = true, IsPermanentFree = true, MaxUsers = 1, MaxCompanies = 1 });
            await db.SaveChangesAsync();
        }
        var tpl = await TemplateAsync(db, SubscriptionPlan.FreeTrial);
        var (_, sub) = await SeedAsync(db, SubscriptionStatus.Active, SubscriptionPlan.Pro);
        sub.EndDate = DateTime.UtcNow.AddMinutes(-5);
        sub.ScheduledPlan = SubscriptionPlan.FreeTrial;
        sub.ScheduledBillingCycle = BillingCycle.Monthly;
        sub.ScheduledPlanEffectiveAt = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();

        await new SubscriptionService(db, Notifications()).ProcessExpiredSubscriptionsAsync();

        var after = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Null(after.ScheduledPlan);
        Assert.Equal(SubscriptionPlan.FreeTrial, after.Plan);
        if (tpl is { IsPermanentFree: true })
        {
            // S2: ฟรีถาวร ⇒ Active ไม่หมดอายุ (เดิมตกเป็น PastDue ในรอบเดียวกัน)
            Assert.Equal(SubscriptionStatus.Active, after.Status);
            Assert.True(after.EndDate > DateTime.UtcNow.AddYears(50));
        }
    }

    [Fact]
    public async Task Reprotect_pass_migrates_dev_key_secrets_and_leaves_unknown_key_untouched()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบเข้ารหัสใหม่", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var devCipher = new SecretProtector(EncryptionKeyResolver.DevKey).Protect("whsec_dev");
        var foreignCipher = new SecretProtector("unknown-key-xyz").Protect("whsec_foreign");
        var a = new WebhookRegistration { CompanyId = company.Id, Name = "a", Url = "https://example.com/a", Secret = devCipher };
        var b = new WebhookRegistration { CompanyId = company.Id, Name = "b", Url = "https://example.com/b", Secret = foreignCipher };
        db.WebhookRegistrations.AddRange(a, b);
        await db.SaveChangesAsync();

        var current = new SecretProtector("prod-key-219-db", null, new[] { EncryptionKeyResolver.DevKey });
        var r = await Accounting.Services.Background.SecretReprotectPass.RunAsync(db, current, default, company.Id);
        Assert.Equal(1, r.Reprotected);

        using var check = DbTestDatabase.TryCreateContext()!;
        var a2 = await check.WebhookRegistrations.AsNoTracking().SingleAsync(w => w.Id == a.Id);
        var b2 = await check.WebhookRegistrations.AsNoTracking().SingleAsync(w => w.Id == b.Id);
        Assert.Equal("whsec_dev", new SecretProtector("prod-key-219-db").Unprotect(a2.Secret));   // ย้ายไปคีย์ปัจจุบันแล้ว
        Assert.Equal(foreignCipher, b2.Secret);                                                   // ไม่รู้คีย์ = ไม่แตะ
        Assert.False(current.IsUsable(b2.Secret));
        // รันซ้ำ = ไม่มีอะไรให้ทำ (idempotent)
        Assert.Equal(0, (await Accounting.Services.Background.SecretReprotectPass.RunAsync(check, current, default, company.Id)).Reprotected);
    }

    // ═══ ฝ่ายค้านรอบ 219 รอบ 2 ═══

    [Fact]
    public async Task Reprotect_pass_does_not_overwrite_secret_rotated_between_read_and_write()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบหมุนคีย์พร้อมงาน", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var current = new SecretProtector("prod-key-219-r2", null, new[] { EncryptionKeyResolver.DevKey });
        var devCipher = new SecretProtector(EncryptionKeyResolver.DevKey).Protect("whsec_old");
        var rotated = new WebhookRegistration { CompanyId = company.Id, Name = "rotated", Url = "https://example.com/r", Secret = devCipher };
        var quiet = new WebhookRegistration { CompanyId = company.Id, Name = "quiet", Url = "https://example.com/q",
            Secret = new SecretProtector(EncryptionKeyResolver.DevKey).Protect("whsec_quiet") };
        db.WebhookRegistrations.AddRange(rotated, quiet);
        await db.SaveChangesAsync();
        var userNewCipher = current.Protect("whsec_user_rotated");

        // ผู้ใช้บันทึกความลับใหม่ "ระหว่าง" งานอ่านแล้วกำลังจะเขียน (บริบทแยก = คำขออื่น)
        var r = await Accounting.Services.Background.SecretReprotectPass.RunAsync(db, current, default, company.Id,
            beforeWrite: async id =>
            {
                if (id != rotated.Id) return;
                using var user = DbTestDatabase.TryCreateContext()!;
                var w = await user.WebhookRegistrations.SingleAsync(x => x.Id == id);
                w.Secret = userNewCipher;
                await user.SaveChangesAsync();
            });

        using var check = DbTestDatabase.TryCreateContext()!;
        var after = await check.WebhookRegistrations.AsNoTracking().SingleAsync(w => w.Id == rotated.Id);
        Assert.Equal(userNewCipher, after.Secret);                                    // ค่าของผู้ใช้ชนะ — ไม่ถูกทับด้วยค่าเก่าที่เข้ารหัสใหม่
        Assert.Equal("whsec_user_rotated", current.Unprotect(after.Secret));
        Assert.Equal(1, r.SkippedConcurrent);
        // ทิศตรงข้าม: แถวที่ไม่มีใครแตะ ⇒ ย้ายไปคีย์ปัจจุบันตามปกติ + มี audit ต่อแถว
        var quietAfter = await check.WebhookRegistrations.AsNoTracking().SingleAsync(w => w.Id == quiet.Id);
        Assert.Equal("whsec_quiet", new SecretProtector("prod-key-219-r2").Unprotect(quietAfter.Secret));
        Assert.Equal(1, r.Reprotected);
        Assert.True(await check.AuditLogs.AsNoTracking().AnyAsync(a => a.EntityType == "WebhookRegistrations" && a.EntityId == quiet.Id.ToString()));
        Assert.False(await check.AuditLogs.AsNoTracking().AnyAsync(a => a.EntityType == "WebhookRegistrations" && a.EntityId == rotated.Id.ToString()));
    }

    [Fact]
    public async Task Reprotect_pass_streams_more_than_one_batch()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบหลายชุด", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var dev = new SecretProtector(EncryptionKeyResolver.DevKey);
        var n = Accounting.Services.Background.SecretReprotectPass.BatchSize + 3;
        for (var i = 0; i < n; i++)
            db.WebhookRegistrations.Add(new WebhookRegistration { CompanyId = company.Id, Name = "b" + i, Url = "https://example.com/" + i, Secret = dev.Protect("s" + i) });
        await db.SaveChangesAsync();
        var current = new SecretProtector("prod-key-219-r2b", null, new[] { EncryptionKeyResolver.DevKey });
        var r = await Accounting.Services.Background.SecretReprotectPass.RunAsync(db, current, default, company.Id);
        Assert.Equal(n, r.Reprotected);   // ชุดที่สองถูกอ่านต่อ (keyset) ไม่ตกหล่น
    }

    [Fact]
    public async Task Legacy_payment_intent_with_null_test_mode_falls_back_to_config_but_stamped_live_wins()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบโหมดทดสอบ", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cfg = new PaymentProviderConfig { CompanyId = company.Id, ProviderCode = "omise", DisplayName = "Omise", Mode = PaymentProviderMode.Test, IsActive = true };
        db.PaymentProviderConfigs.Add(cfg);
        await db.SaveChangesAsync();
        PaymentIntent Make(bool? stamped) => new()
        {
            CompanyId = company.Id, ProviderConfigId = cfg.Id, ProviderCode = "omise", SourceKind = PaymentSourceKind.Document,
            SourceId = Guid.NewGuid(), Amount = 100m, IsTestMode = stamped,
        };
        // S8 รอบ 2: config ถูกสลับเป็น Test ภายหลัง — intent ที่ประทับ Live (false) ต้องยังเป็นเงินจริง (เดิม: false = "ไม่รู้" ⇒ อ่าน config ⇒ บล็อก)
        Assert.False(await PaymentIntentTestMode.ResolveAsync(db, Make(false), default));
        Assert.True(await PaymentIntentTestMode.ResolveAsync(db, Make(true), default));
        Assert.True(await PaymentIntentTestMode.ResolveAsync(db, Make(null), default));   // แถวเดิม ⇒ อ่าน config
    }
}
