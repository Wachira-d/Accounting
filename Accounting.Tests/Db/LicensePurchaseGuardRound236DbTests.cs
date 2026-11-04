using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
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
/// รอบ 236 (คำตัดสิน 236-2/236-3) — ด่านเงิน "บริษัทที่ License คุมอยู่ ซื้อ/ต่ออายุ/อัปเกรดแพ็กเกจระดับบริษัทไม่ได้" บน PostgreSQL จริง (ข้ามเมื่อไม่มีฐาน) ·
/// เคสเจ้าของ: บริษัท Free Edition (ฟรีถาวร) ผูก License Enterprise ถึง 2593 — หน้าซื้อเคยให้จ่าย ฿10,000 ซื้อ Enterprise 12 เดือน ·
/// ทุกทางเข้า (หน้าซื้อ: ใบเสนอ/สร้างคำสั่งซื้อ/ดรอปดาวน์ · SubscriptionService: ซื้อ/เปลี่ยน/แจ้งชำระ/แอดมินบันทึกรับเงิน/ใบเสนอ/preview) ปฏิเสธโดยไม่สร้างรายการ ·
/// ทิศตรงข้าม: ถอดออกจาก License / License ถูกลบ ⇒ ซื้อได้ตามปกติ (มีรายการชำระ)
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class LicensePurchaseGuardRound236DbTests
{
    private readonly ITestOutputHelper _out;
    public LicensePurchaseGuardRound236DbTests(ITestOutputHelper output) => _out = output;

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    private static PublicCheckoutService Checkout(Accounting.Data.AccountingDbContext db)
        => new(db, new SubscriptionService(db, Notifications()),
            F4Proxy<IPlatformPaymentService>.Create((name, _) => name == nameof(IPlatformPaymentService.StatusAsync)
                ? new PlatformPayStatus(false, false, null, null, Array.Empty<string>(), null) : null),
            F4Proxy<ILegalDocumentService>.Create((_, _) => null), F4Proxy<IEmailService>.Create((_, _) => null),
            NullLogger<PublicCheckoutService>.Instance);

    /// <summary>แม่แบบที่มีราคา (ฐานร่วม — ใช้ของเดิมถ้ามี)</summary>
    private static async Task<PlanTemplate> PricedAsync(Accounting.Data.AccountingDbContext db, SubscriptionPlan plan, decimal monthly)
    {
        var t = await db.PlanTemplates.FirstOrDefaultAsync(x => x.Plan == plan && x.IsActive && !x.IsPermanentFree);
        if (t != null) return t;
        t = new PlanTemplate
        {
            Name = "แพ็กเกจทดสอบ 236 " + plan, Plan = plan, IsActive = true,
            MonthlyPrice = monthly, QuarterlyPrice = monthly * 3, SemiAnnualPrice = monthly * 6, AnnualPrice = monthly * 12,
            MaxUsers = 999, MaxCompanies = 10, MaxDocumentsPerMonth = 99999, MaxJournalEntriesPerMonth = 99999,
        };
        db.PlanTemplates.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    /// <summary>บริษัท Free Edition (ฟรีถาวร) ของผู้ซื้อ + License Enterprise ของ "สมฤทธิ์" (ผู้ใช้จริงในตาราง Users) ถึง 31/12/2593 ที่บริษัทผูกอยู่</summary>
    private static async Task<(Guid BuyerId, Guid CompanyId, Subscription Sub, AccountSubscription License)> SeedLicensedAsync(
        Accounting.Data.AccountingDbContext db, SubscriptionStatus licenseStatus = SubscriptionStatus.Active, DateTime? licenseEnd = null,
        bool holderIsOwner = false, bool attach = true)
    {
        var ent = await PricedAsync(db, SubscriptionPlan.Enterprise, 833.33m);
        var tag = Guid.NewGuid().ToString("N")[..6];
        var holder = new User { Email = $"lic236-{tag}@test.local", PasswordHash = "x", FullName = "สมฤทธิ์ ทดสอบ " + tag };
        var buyer = new User { Email = $"buy236-{tag}@test.local", PasswordHash = "x", FullName = "ผู้ซื้อทดสอบ 236" };
        db.Users.AddRange(holder, buyer);
        var c = new Company { Name = "บริษัท เน็ก แอค ทดสอบ 236 " + tag, TaxId = "0105556000114" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = buyer.Id, CompanyId = c.Id, Role = UserRole.Owner });
        if (holderIsOwner) db.CompanyUsers.Add(new CompanyUser { UserId = holder.Id, CompanyId = c.Id, Role = UserRole.Owner });
        var lic = new AccountSubscription
        {
            OwnerUserId = holder.Id, PlanTemplateId = ent.Id, Status = licenseStatus, StartDate = DateTime.UtcNow.AddDays(-30),
            EndDate = licenseEnd ?? new DateTime(2050, 12, 31, 10, 0, 0, DateTimeKind.Utc),
            MaxCompanies = 10, MaxUsersPerCompany = 999, MaxDocumentsPerMonth = 99999, MaxJournalEntriesPerMonth = 99999,
            EnabledFeatures = ent.EnabledFeatures, GracePeriodDays = 7,
        };
        db.AccountSubscriptions.Add(lic);
        await db.SaveChangesAsync();
        var sub = new Subscription
        {
            CompanyId = c.Id, Plan = SubscriptionPlan.FreeTrial, Status = SubscriptionStatus.Active, BillingCycle = BillingCycle.Monthly,
            IsPermanentFree = true, PricePerCycle = 0m,
            StartDate = DateTime.UtcNow.AddDays(-100), EndDate = DateTime.UtcNow.AddYears(100), UsageResetDate = DateTime.UtcNow.AddDays(20),
            AccountSubscriptionId = attach ? lic.Id : null,
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        return (buyer.Id, c.Id, sub, lic);
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

    [Fact]
    public async Task R236_เจ้าของ_หน้าซื้อ_บริษัทใต้LicenseEnterprise_ใบเสนอปฏิเสธ_สร้างคำสั่งซื้อไม่ได้_ไม่มีรายการชำระ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (buyer, cid, sub, lic) = await SeedLicensedAsync(db);
        var holderName = await db.Users.Where(u => u.Id == lic.OwnerUserId).Select(u => u.FullName).SingleAsync();
        await WithCheckoutEnabledAsync(db, async () =>
        {
            var svc = Checkout(db);
            var q = await svc.QuoteAsync(cid, SubscriptionPlan.Enterprise, BillingCycle.Annual, null);
            Assert.NotNull(q.Preview);
            Assert.False(q.Preview!.Allowed);
            Assert.Equal("Refused", q.Preview.Kind);
            Assert.Equal(0m, q.AmountDue);   // เดิม ฿10,000
            Assert.NotEqual("Free Edition", q.Preview.CurrentPlanName);   // เดิม "แพ็กเกจปัจจุบัน Free Edition → Enterprise"
            Assert.Contains(holderName, q.Preview.RefusalReason);
            Assert.Contains("31/12/2593", q.Preview.RefusalReason);

            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.CreateOrderAsync(buyer, cid, SubscriptionPlan.Enterprise, BillingCycle.Annual, null));
            Assert.Equal(PlanIdentityResolver.LicenseCoveredRuleCode, ex.RuleCode);
            Assert.Contains("ถอดบริษัทออกจาก License", ex.Message);

            var row = (await svc.OwnedCompaniesAsync(buyer)).Single(r => r.Id == cid);
            Assert.Contains("ผ่าน License", row.Label);   // เดิม "(FreeTrial · Active)"
            Assert.DoesNotContain("FreeTrial", row.Label);
            Assert.NotNull(row.PurchaseBlocked);
            Assert.Empty(row.LowerPlans);
        });
        Assert.Equal(0, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id));
    }

    [Fact]
    public async Task R236_SubscriptionService_ทุกทางเข้าปฏิเสธ_ใบเสนอและpreviewบอกเหตุ_ไม่มีรายการชำระ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await PricedAsync(db, SubscriptionPlan.Pro, 490m);
        var (buyer, cid, sub, _) = await SeedLicensedAsync(db);
        var svc = new SubscriptionService(db, Notifications());

        var convert = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.ConvertTrialAsync(cid, new ConvertTrialRequest(SubscriptionPlan.Enterprise, BillingCycle.Annual), buyer.ToString()));
        Assert.Equal(PlanIdentityResolver.LicenseCoveredRuleCode, convert.RuleCode);
        var change = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Pro, BillingCycle.Annual), buyer.ToString()));
        Assert.Equal(PlanIdentityResolver.LicenseCoveredRuleCode, change.RuleCode);
        var submit = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SubmitPaymentAsync(cid,
            new SubmitSubscriptionPaymentRequest(sub.Id, 10_000m, DateTime.UtcNow, PaymentMethod.BankTransfer, null, null, null, null, "T236",
                SubscriptionPlan.Enterprise, BillingCycle.Annual, 12, null), buyer.ToString()));
        Assert.Equal(PlanIdentityResolver.LicenseCoveredRuleCode, submit.RuleCode);
        var manual = new RecordManualPaymentRequest(10_000m, DateTime.UtcNow, PaymentMethod.BankTransfer, SubscriptionPlan.Enterprise, BillingCycle.Annual, 12,
            false, null, "T236", null);
        var admin = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RecordManualPaymentAsync(cid, manual, "admin-test"));
        Assert.Equal(PlanIdentityResolver.LicenseCoveredRuleCode, admin.RuleCode);

        var quote = await svc.QuotePlanChangeAsync(cid, SubscriptionPlan.Enterprise, BillingCycle.Annual, null);
        Assert.NotNull(quote.Blocked);
        Assert.Equal(0m, quote.AmountDue);
        Assert.False(quote.Preview!.Allowed);
        var adminPreview = await svc.PreviewManualPaymentAsync(cid, manual);
        Assert.False(adminPreview.Preview.Allowed);
        Assert.Contains("License", adminPreview.Preview.RefusalReason);

        var view = await svc.GetSubscriptionAsync(cid);
        Assert.Equal(PlanIdentityResolver.SourceLicense, view.PlanSource);
        Assert.Equal(SubscriptionPlan.Enterprise, view.Plan);
        Assert.NotNull(view.PurchaseBlocked);

        Assert.Equal(0, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id));
        var after = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Null(after.ScheduledPlan);   // เส้นตั้งเวลาเปลี่ยนก็ไม่ถูกเขียน (ไม่ใช่ silent no-op)
        Assert.Equal(SubscriptionPlan.FreeTrial, after.Plan);
    }

    [Fact]
    public async Task R236_ทิศตรงข้าม_ถอดออกจากLicense_ซื้อได้_มีรายการชำระ_และLicenseถูกลบก็ซื้อได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (buyer, cid, sub, lic) = await SeedLicensedAsync(db);
        var svc = new SubscriptionService(db, Notifications());
        Assert.False((await PlanIdentityReader.PurchaseVerdictAsync(db, cid)).Allowed);

        // License ถูกลบ (soft delete) ⇒ ไม่ใช่ตัวคุมแล้ว — ตรงกับ GetEffectivePlanAsync (ตัวชี้ค้างถูกปลด)
        lic.IsDeleted = true;
        await db.SaveChangesAsync();
        Assert.True((await PlanIdentityReader.PurchaseVerdictAsync(db, cid)).Allowed);
        lic.IsDeleted = false;
        await db.SaveChangesAsync();
        Assert.False((await PlanIdentityReader.PurchaseVerdictAsync(db, cid)).Allowed);

        // ถอดบริษัทออกจาก License ⇒ แถวบริษัท (Free Edition) กลับมามีผล · ซื้อ Enterprise ได้ตามปกติ
        var tracked = await db.Subscriptions.SingleAsync(s => s.Id == sub.Id);
        tracked.AccountSubscriptionId = null;
        await db.SaveChangesAsync();
        var id = await PlanIdentityReader.ForCompanyAsync(db, cid);
        Assert.Equal(PlanIdentityResolver.SourceCompany, id!.Source);
        var quote = await svc.QuotePlanChangeAsync(cid, SubscriptionPlan.Enterprise, BillingCycle.Annual, null);
        Assert.Null(quote.Blocked);
        Assert.True(quote.AmountDue > 0m);
        var res = await svc.ConvertTrialAsync(cid, new ConvertTrialRequest(SubscriptionPlan.Enterprise, BillingCycle.Annual), buyer.ToString());
        Assert.NotNull(res.Payment);
        Assert.Equal(1, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id));
    }

    [Fact]
    public async Task R236_Licenseหมดอายุแต่ยังผูก_ปฏิเสธพร้อมทางถอดออก_ไม่ทวงใบต่ออายุแถวบริษัท()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (_, cid, sub, _) = await SeedLicensedAsync(db, SubscriptionStatus.Expired, DateTime.UtcNow.AddDays(-30));
        var v = await PlanIdentityReader.PurchaseVerdictAsync(db, cid);
        Assert.False(v.Allowed);
        Assert.Contains("หมดอายุแล้ว", v.Reason);
        var governed = await PlanIdentityReader.LicenseGovernedSubscriptionIdsAsync(db, new[] { sub });
        Assert.Contains(sub.Id, governed);
    }

    private static async Task<string> ReviewerIdAsync(Accounting.Data.AccountingDbContext db)
    {
        var reviewer = new User { Email = $"r236-admin-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "แอดมินทดสอบ 236", IsSystemAdmin = true };
        db.Users.Add(reviewer);
        await db.SaveChangesAsync();
        return reviewer.Id.ToString();
    }

    /// <summary>ฝ่ายค้าน B1: ทางเดิมที่ใช้ได้จริง — ผู้ถือ License จ่ายแพ็กระดับเดียวกับ License ผ่านบริษัท ⇒ สร้างรายการได้ (ใบเสนอ = "ต่ออายุ License") ·
    /// แนบสลิปได้ · อนุมัติแล้ว License ถูกยืด · ทิศตรงข้าม: ผู้ใช้อื่น (เจ้าของบริษัทเดียวกัน) จ่ายแพ็กเดียวกัน ⇒ 409</summary>
    [Fact]
    public async Task R236_B1_ผู้ถือLicenseจ่ายแพ็กระดับเดียวกัน_สร้างรายการได้_อนุมัติแล้วLicenseถูกยืด_ผู้ใช้อื่น409()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var licenseEnd = DateTime.UtcNow.AddDays(20);
        var (buyer, cid, sub, lic) = await SeedLicensedAsync(db, licenseEnd: licenseEnd, holderIsOwner: true);
        var holder = lic.OwnerUserId;
        var svc = new SubscriptionService(db, Notifications());

        var other = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.ConvertTrialAsync(cid, new ConvertTrialRequest(SubscriptionPlan.Enterprise, BillingCycle.Annual), buyer.ToString()));
        Assert.Equal(PlanIdentityResolver.LicenseCoveredRuleCode, other.RuleCode);
        Assert.Contains("ให้ผู้ถือ License", other.Message);
        Assert.Equal(0, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id));

        var quote = await svc.QuotePlanChangeAsync(cid, SubscriptionPlan.Enterprise, BillingCycle.Annual, null, holder);
        Assert.Null(quote.Blocked);
        Assert.Equal("LicenseRenewal", quote.Preview!.Kind);
        Assert.Equal(PlanIdentityResolver.LicenseRenewalHeadline, quote.Preview.Headline);
        Assert.True(quote.Preview.NewEndUtc > licenseEnd);

        var res = await svc.ConvertTrialAsync(cid, new ConvertTrialRequest(SubscriptionPlan.Enterprise, BillingCycle.Annual), holder.ToString());
        Assert.NotNull(res.Payment);
        await svc.UploadPaymentSlipAsync(res.Payment!.Id, "s236.jpg", "s236.jpg", "image/jpeg", 10, "uploads/slips/s236.jpg", holder.ToString());
        await svc.ReviewPaymentAsync(res.Payment.Id, new ReviewSubscriptionPaymentRequest(true, "test 236", null), await ReviewerIdAsync(db));
        var after = await db.AccountSubscriptions.AsNoTracking().SingleAsync(a => a.Id == lic.Id);
        Assert.True(after.EndDate > licenseEnd.AddDays(300), $"License ต้องถูกยืด 12 เดือน — ได้ {after.EndDate:O}");
    }

    /// <summary>ฝ่ายค้าน S1: ผูกเข้า License ⇒ รายการรอชำระเก่า (ไม่มีสลิป/ไม่ผูกใบ/ไม่มี intent) ถูกยกเลิก + audit chain · มีสลิปไม่แตะ ·
    /// รายการเก่าที่หลุดค้าง (ผูกก่อนรอบนี้) แนบสลิปไม่ได้ 409</summary>
    [Fact]
    public async Task R236_S1_ผูกLicense_ยกเลิกรายการรอชำระเก่า_มีสลิปไม่แตะ_แนบสลิปรายการค้างได้409()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (buyer, cid, sub, lic) = await SeedLicensedAsync(db, attach: false);
        var svc = new SubscriptionService(db, Notifications());
        var p1 = (await svc.ConvertTrialAsync(cid, new ConvertTrialRequest(SubscriptionPlan.Enterprise, BillingCycle.Annual), buyer.ToString())).Payment!;
        var withSlip = new SubscriptionPayment
        {
            SubscriptionId = sub.Id, PaymentNumber = "SP-236S-" + Guid.NewGuid().ToString("N")[..6], Amount = 990m, PaymentDate = DateTime.UtcNow,
            RequestedPlan = SubscriptionPlan.Enterprise, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = 1,
            Status = SubscriptionPaymentStatus.Pending, Kind = SubscriptionPaymentKind.Normal, SlipFileName = "x.jpg",
        };
        db.SubscriptionPayments.Add(withSlip);
        await db.SaveChangesAsync();

        var tracked = await db.Subscriptions.SingleAsync(x => x.Id == sub.Id);
        tracked.AccountSubscriptionId = lic.Id;
        Assert.Equal(1, await svc.CancelPendingPlanPaymentsForLicenseAsync(sub.Id, cid, "test-attach"));
        await db.SaveChangesAsync();
        Assert.Equal(SubscriptionPaymentStatus.Cancelled, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == p1.Id)).Status);
        Assert.Equal(SubscriptionPaymentStatus.Pending, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == withSlip.Id)).Status);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityId == p1.Id.ToString() && a.NewValues!.Contains(PlanIdentityResolver.LicenseCoveredRuleCode)));

        var stray = new SubscriptionPayment
        {
            SubscriptionId = sub.Id, PaymentNumber = "SP-236T-" + Guid.NewGuid().ToString("N")[..6], Amount = 990m, PaymentDate = DateTime.UtcNow,
            RequestedPlan = SubscriptionPlan.Enterprise, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = 1,
            Status = SubscriptionPaymentStatus.Pending, Kind = SubscriptionPaymentKind.Normal,
        };
        db.SubscriptionPayments.Add(stray);
        await db.SaveChangesAsync();
        var slip = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UploadPaymentSlipAsync(stray.Id, "y.jpg", "y.jpg", "image/jpeg", 10, "uploads/slips/y.jpg", buyer.ToString()));
        Assert.Equal(PlanIdentityResolver.LicenseCoveredRuleCode, slip.RuleCode);

        // S4: เริ่มชำระออนไลน์รายการค้างเดียวกัน ⇒ ด่านเดียวกัน (ก่อนด่านยอด/ช่องทาง)
        var platform = new PlatformPaymentService(db, F4Proxy<IPaymentIntentService>.Create((_, _) => null),
            F4Proxy<IAddOnPurchaseService>.Create((_, _) => null), Array.Empty<IPaymentProvider>());
        var start = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            platform.StartSubscriptionPaymentAsync(cid, stray.Id, PaymentMethodKind.PromptPay, null, null, actorUserId: buyer));
        Assert.Equal(PlanIdentityResolver.LicenseCoveredRuleCode, start.RuleCode);
    }

    /// <summary>ฝ่ายค้าน S4: ใบต่ออายุแพ็กเกจระดับบริษัทของบริษัทที่ License คุม ⇒ <c>GenerateRenewalInvoiceAsync</c> คืน null (ไม่ออกใบ) ·
    /// S2: งานเก็บเงินอัตโนมัติหยุดค่าแพ็กระดับบริษัท — ทิศตรงข้าม: ผู้ถือเป็นเจ้าของ + แพ็กระดับ License (ต่ออายุ License) · ไม่ผูก License</summary>
    [Fact]
    public async Task R236_S4_S2_ใบต่ออายุไม่ออก_งานเก็บเงินอัตโนมัติหยุดเฉพาะบริษัทใต้Licenseที่ไม่ใช่กรณีต่ออายุLicense()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (_, _, sub, _) = await SeedLicensedAsync(db);
        var tracked = await db.Subscriptions.SingleAsync(x => x.Id == sub.Id);
        tracked.Plan = SubscriptionPlan.Pro; tracked.IsPermanentFree = false; tracked.PricePerCycle = 490m;
        tracked.EndDate = DateTime.UtcNow.AddDays(5);
        await db.SaveChangesAsync();
        var billing = new SaasBillingDocumentService(db, F4Proxy<IPdfGenerationService>.Create((_, _) => null),
            F4Proxy<IFileAttachmentService>.Create((_, _) => null), NullLogger<SaasBillingDocumentService>.Instance);
        Assert.Null(await billing.GenerateRenewalInvoiceAsync(sub.Id));
        Assert.Null((await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == sub.Id)).RenewalInvoiceNumber);
        Assert.True(await PlanIdentityReader.StopsCompanyPlanBillingAsync(db, tracked));

        var (_, _, sub2, _) = await SeedLicensedAsync(db, holderIsOwner: true);   // ผู้ถือเป็นเจ้าของ + แถวบริษัท Free → แพ็ก FreeTrial ≠ Enterprise
        var t2 = await db.Subscriptions.SingleAsync(x => x.Id == sub2.Id);
        Assert.True(await PlanIdentityReader.StopsCompanyPlanBillingAsync(db, t2));
        t2.Plan = SubscriptionPlan.Enterprise; t2.IsPermanentFree = false;   // แพ็กระดับ License ⇒ ใบนี้คือการต่ออายุ License
        await db.SaveChangesAsync();
        Assert.False(await PlanIdentityReader.StopsCompanyPlanBillingAsync(db, t2));
        t2.AccountSubscriptionId = null;
        await db.SaveChangesAsync();
        Assert.False(await PlanIdentityReader.StopsCompanyPlanBillingAsync(db, t2));
    }

    /// <summary>ฝ่ายค้าน S3: License Active + แถวบริษัทค้าง Suspended ⇒ OCR ยังใช้ได้ (อ่านตามสถานะ License) · ทิศตรงข้าม: ไม่มี License + Suspended ⇒ ถูกตัด</summary>
    [Fact]
    public async Task R236_S3_OCR_LicenseActive_แถวSuspendedยังใช้ได้_ไม่มีLicenseถูกตัด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (_, cid, sub, lic) = await SeedLicensedAsync(db);
        var trackedLic = await db.AccountSubscriptions.SingleAsync(a => a.Id == lic.Id);
        trackedLic.MaxOcrPagesPerMonth = 50;
        var tracked = await db.Subscriptions.SingleAsync(x => x.Id == sub.Id);
        tracked.Status = SubscriptionStatus.Suspended;
        await db.SaveChangesAsync();
        var ocr = new OcrQuotaService(db, NullLogger<OcrQuotaService>.Instance);
        Assert.True((await ocr.GetQuotaStatusAsync(cid)).TotalAvailable > 0);

        tracked.AccountSubscriptionId = null;
        await db.SaveChangesAsync();
        Assert.Equal(0, (await ocr.GetQuotaStatusAsync(cid)).TotalAvailable);
    }

    /// <summary>ฝ่ายค้าน S3: งานหมดอายุไม่เดินสถานะแถวบริษัทที่ License คุม · ทิศตรงข้าม: ไม่ผูก License ⇒ PastDue ตามเดิม</summary>
    [Fact]
    public async Task R236_S3_งานหมดอายุไม่เดินสถานะแถวที่Licenseคุม_ไม่ผูกเดินตามเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (_, _, sub, _) = await SeedLicensedAsync(db);
        var (_, _, free, _) = await SeedLicensedAsync(db, attach: false);
        foreach (var id in new[] { sub.Id, free.Id })
        {
            var t = await db.Subscriptions.SingleAsync(x => x.Id == id);
            t.Plan = SubscriptionPlan.Pro; t.IsPermanentFree = false; t.EndDate = DateTime.UtcNow.AddDays(-1);
        }
        await db.SaveChangesAsync();
        await new SubscriptionService(db, Notifications()).ProcessExpiredSubscriptionsAsync();
        Assert.Equal(SubscriptionStatus.Active, (await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == sub.Id)).Status);
        Assert.Equal(SubscriptionStatus.PastDue, (await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == free.Id)).Status);
    }
}
