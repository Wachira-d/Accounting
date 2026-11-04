using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 235 (คำตัดสิน 235-1/235-3/235-6/235-7/235-9) — เส้นที่แตะการบันทึกเงิน/วันที่บนฐานจริง:
/// ต่ออายุพร้อมคูปอง (จอง → เงินเข้า → นับ · วันหมดอายุต่อจากเดิมบนปฏิทินไทย) · คูปอง "ซื้อใหม่เท่านั้น" ไม่ลดตอนต่ออายุ ·
/// แจ้ง "รอตรวจ" เฉพาะเมื่อแนบสลิป · แอดมินบันทึกรับเงินเอง: preview = วันที่ที่ประทับจริง + ตัวตรวจภาษีหักเดียว · 50 ทวิ แนบเข้าแถวทะเบียนโดยอ้างไฟล์เดิม
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class PlatformRound235DbTests
{
    private readonly ITestOutputHelper _out;
    public PlatformRound235DbTests(ITestOutputHelper output) => _out = output;

    private const string JuristicTaxId = "0105556000114";   // checksum ผ่าน · นิติบุคคล (ชุดเดียวกับรอบ 234)

    private static async Task EnsureProTemplateAsync(Accounting.Data.AccountingDbContext db)
    {
        if (await db.PlanTemplates.AnyAsync(t => t.Plan == SubscriptionPlan.Pro && t.IsActive && t.MonthlyPrice > 0m)) return;
        db.PlanTemplates.Add(new PlanTemplate
        {
            Name = "แพ็กเกจทดสอบรอบ 235", Plan = SubscriptionPlan.Pro, IsActive = true,
            MonthlyPrice = 1_490m, QuarterlyPrice = 4_200m, SemiAnnualPrice = 8_000m, AnnualPrice = 15_198m,
            MaxUsers = 5, MaxCompanies = 1, MaxDocumentsPerMonth = 500, MaxJournalEntriesPerMonth = 500,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<PlatformCoupon> CouponAsync(Accounting.Data.AccountingDbContext db, PlatformCouponPurchaseKinds kinds)
    {
        var c = new PlatformCoupon
        {
            Code = "T235-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Kind = PlatformCouponKind.Percent, Value = 10m,
            AppliesTo = PlatformCouponScope.Subscription, PurchaseKinds = kinds, MaxUsesTotal = null, MaxUsesPerCompany = null, IsActive = true,
        };
        db.PlatformCoupons.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    /// <summary>เจ้าของบริษัท (ผู้รับกระดิ่ง) — ไม่มีเจ้าของ = เทสต์การแจ้งไม่มีความหมาย</summary>
    private static async Task AddOwnerAsync(Accounting.Data.AccountingDbContext db, Guid companyId)
    {
        var u = new User { Email = $"r235-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของรอบ 235" };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = companyId, Role = UserRole.Owner });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task K2_ต่ออายุพร้อมคูปอง_ลดยอด_จองแล้วนับเมื่อเงินเข้า_วันหมดอายุต่อจากเดิม_ไม่แจ้งรอตรวจตอนสร้าง()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsureProTemplateAsync(db);
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db, plan: SubscriptionPlan.Pro, status: SubscriptionStatus.Active, endInDays: 20);
        await AddOwnerAsync(db, f.BuyerId);
        var pending = new List<string>();
        var notes = F4Proxy<INotificationService>.Create((name, args) =>
        {
            if (name == nameof(INotificationService.SendAsync) && args.Length > 3 && args[3] is string title && title == PlatformPaymentReviewNotice.Title) pending.Add(title);
            return null;
        });
        var svc = new SubscriptionService(db, notes);
        var coupon = await CouponAsync(db, PlatformCouponPurchaseKinds.Both);
        var oldEnd = (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).EndDate;

        var res = await svc.RequestRenewalPaymentAsync(f.BuyerId, SubscriptionPlan.Pro, BillingCycle.Monthly, coupon.Code.ToLowerInvariant(), null, "owner");
        var pay = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == res.Id);
        Assert.Equal(coupon.Id, pay.CouponId);
        Assert.NotNull(pay.ListAmount);
        Assert.Equal(PlatformCouponRule.R2(pay.ListAmount!.Value * 0.10m), pay.CouponDiscountAmount);
        Assert.Equal(pay.ListAmount.Value - pay.CouponDiscountAmount, pay.Amount);
        Assert.Equal(pay.Amount, pay.QuotedAmount);   // ช่องทางออนไลน์อนุมัติอัตโนมัติด้วยยอดหลังคูปอง
        Assert.Equal(1, pay.RequestedPeriodMonths);
        Assert.Equal(PlatformCouponRedemptionStatus.Reserved,
            (await db.PlatformCouponRedemptions.AsNoTracking().SingleAsync(r => r.CouponId == coupon.Id && r.SourceId == pay.Id)).Status);
        Assert.Empty(pending);   // 235-9: ยังไม่จ่าย ⇒ ไม่แจ้ง "รอตรวจ"

        await svc.ReviewPaymentAsync(pay.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);
        var sub = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id);
        var expected = PlatformPlanPricing.AddCycleMonths(oldEnd, 1);
        Assert.True(Math.Abs((sub.EndDate - expected).TotalMilliseconds) < 1, $"ต่อจากวันหมดอายุเดิมบนปฏิทินไทย: {expected:O} vs {sub.EndDate:O}");
        Assert.Equal(PlatformCouponRedemptionStatus.Redeemed,
            (await db.PlatformCouponRedemptions.AsNoTracking().SingleAsync(r => r.CouponId == coupon.Id && r.SourceId == pay.Id)).Status);
    }

    [Fact]
    public async Task K2_ทิศตรงข้าม_คูปองซื้อใหม่เท่านั้น_ต่ออายุถูกปฏิเสธไม่เกิดรายการ_ต่ออายุไม่ใส่คูปองได้ยอดเต็ม()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsureProTemplateAsync(db);
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db, plan: SubscriptionPlan.Pro, status: SubscriptionStatus.Active, endInDays: 20);
        var svc = new SubscriptionService(db, F4Proxy<INotificationService>.Create((_, _) => null));
        var newOnly = await CouponAsync(db, PlatformCouponPurchaseKinds.New);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.RequestRenewalPaymentAsync(f.BuyerId, SubscriptionPlan.Pro, BillingCycle.Monthly, newOnly.Code, null, "owner"));
        Assert.Contains("ต่ออายุไม่ได้", ex.Message);
        Assert.False(await db.SubscriptionPayments.AnyAsync(p => p.SubscriptionId == f.Sub.Id));
        Assert.False(await db.PlatformCouponRedemptions.AnyAsync(r => r.CouponId == newOnly.Id));
        // ฝ่ายค้าน S-1: คูปองที่ไม่ได้ตั้งชนิดการซื้อ (คูปองเดิม/ค่าตั้งต้น) = ซื้อใหม่เท่านั้น ⇒ ต่ออายุถูกปฏิเสธเช่นกัน
        var legacy = new PlatformCoupon
        {
            Code = "T235L-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Kind = PlatformCouponKind.Percent, Value = 10m,
            AppliesTo = PlatformCouponScope.Subscription, MaxUsesPerCompany = null, IsActive = true,
        };
        db.PlatformCoupons.Add(legacy);
        await db.SaveChangesAsync();
        Assert.Equal(PlatformCouponPurchaseKinds.New, (await db.PlatformCoupons.AsNoTracking().SingleAsync(c => c.Id == legacy.Id)).PurchaseKinds);
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.RequestRenewalPaymentAsync(f.BuyerId, SubscriptionPlan.Pro, BillingCycle.Monthly, legacy.Code, null, "owner"));
        Assert.False(await db.SubscriptionPayments.AnyAsync(p => p.SubscriptionId == f.Sub.Id));
        var plain = await svc.RequestRenewalPaymentAsync(f.BuyerId, SubscriptionPlan.Pro, BillingCycle.Monthly, null, null, "owner");
        var row = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == plain.Id);
        Assert.Null(row.CouponId);
        Assert.Equal(0m, row.CouponDiscountAmount);
        // เปลี่ยนแพ็ก/รอบผ่านเส้นต่ออายุไม่ได้ (ไม่ทับเส้นซื้อ/อัปเกรด)
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.RequestRenewalPaymentAsync(f.BuyerId, SubscriptionPlan.Pro, BillingCycle.Annual, null, null, "owner"));
    }

    [Fact]
    public async Task ข้อ9_แจ้งชำระยังไม่แนบสลิปไม่แจ้ง_แนบสลิปแจ้งครั้งเดียว()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsureProTemplateAsync(db);
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db, plan: SubscriptionPlan.Pro, status: SubscriptionStatus.Active, endInDays: 20);
        await AddOwnerAsync(db, f.BuyerId);
        var pending = 0;
        var notes = F4Proxy<INotificationService>.Create((name, args) =>
        {
            if (name == nameof(INotificationService.SendAsync) && args.Length > 3 && args[3] is string t && t == PlatformPaymentReviewNotice.Title) pending++;
            return null;
        });
        var svc = new SubscriptionService(db, notes);
        var template = await db.PlanTemplates.AsNoTracking().FirstAsync(t => t.Plan == SubscriptionPlan.Pro && t.IsActive);
        var due = await PlatformPriceOverrideStore.AmountDueAsync(db, f.BuyerId, null, template, BillingCycle.Monthly, DateTime.UtcNow, currentPlan: SubscriptionPlan.Pro);
        var pay = await svc.SubmitPaymentAsync(f.BuyerId, new SubmitSubscriptionPaymentRequest(f.Sub.Id, due, DateTime.UtcNow, PaymentMethod.BankTransfer,
            null, null, null, null, "REF-235", SubscriptionPlan.Pro, BillingCycle.Monthly, 1, "ต่ออายุ"), "owner");
        Assert.Equal(0, pending);   // บั๊กเดิม: แจ้ง "รอตรวจสอบ" ทันทีทั้งที่ยังไม่มีสลิป/เงิน
        await svc.UploadPaymentSlipAsync(pay.Id, "s1.png", "s1.png", "image/png", 10, "uploads/slips/s1.png", "owner");
        Assert.Equal(1, pending);
        await svc.UploadPaymentSlipAsync(pay.Id, "s2.png", "s2.png", "image/png", 10, "uploads/slips/s2.png", "owner");
        Assert.Equal(1, pending);   // เปลี่ยนไฟล์ ⇒ ไม่แจ้งซ้ำ

        // ฝ่ายค้าน N-3 (K-7): preview หน้าตรวจสลิป = วันหมดอายุที่ประทับจริงหลังแอดมินอนุมัติ (ตัวคำนวณเดียว)
        var pv = await svc.PreviewPaymentApprovalAsync(pay.Id);
        Assert.NotNull(pv.Preview.NewEndUtc);
        Assert.Null(pv.WithholdingProblem);
        var reviewer = new User { Email = $"r235-rv-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "แอดมินตรวจสลิป 235", IsSystemAdmin = true };
        db.Users.Add(reviewer);
        await db.SaveChangesAsync();
        await svc.ReviewPaymentAsync(pay.Id, new ReviewSubscriptionPaymentRequest(true, "ตรวจสลิปแล้ว", null), reviewer.Id.ToString());
        var sub = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == f.Sub.Id);
        Assert.True(Math.Abs((sub.EndDate - pv.Preview.NewEndUtc!.Value).TotalMilliseconds) < 1, $"preview {pv.Preview.NewEndUtc:O} vs ประทับ {sub.EndDate:O}");
    }

    [Fact]
    public async Task ข้อ6_K7_บันทึกรับเงินเอง_preview_เท่ากับวันที่ประทับจริง_ภาษีหักผ่านตัวตรวจเดียว()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsureProTemplateAsync(db);
        var person = await PlatformBillingPhase2DbTests.SeedAsync(db, plan: SubscriptionPlan.Pro, status: SubscriptionStatus.Active, endInDays: 20);
        var personCo = await db.Companies.FirstAsync(c => c.Id == person.BuyerId);
        personCo.TaxId = "";   // ไม่ใช่นิติบุคคล (คอลัมน์ NOT NULL — ไม่มีเลข = "")
        await db.SaveChangesAsync();
        var svc = new SubscriptionService(db, F4Proxy<INotificationService>.Create((_, _) => null));
        var reviewer = new User { Email = $"r235-admin-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "แอดมินทดสอบ 235", IsSystemAdmin = true };
        db.Users.Add(reviewer);
        await db.SaveChangesAsync();
        var admin = reviewer.Id.ToString();   // ReviewedByUserId มี FK ไป Users
        RecordManualPaymentRequest Req(decimal amount, decimal wht) => new(amount, DateTime.UtcNow, PaymentMethod.BankTransfer, SubscriptionPlan.Pro,
            BillingCycle.Monthly, 1, false, null, "REF", null, wht);

        // บุคคลธรรมดา + กรอกภาษีหัก ⇒ preview บอกปัญหา · บันทึกจริงถูกปฏิเสธ (เดิมผ่าน ⇒ ใบเต็มยอด/ทะเบียนภาษีถูกหักผิด) · ไม่เกิดรายการ
        var pvBad = await svc.PreviewManualPaymentAsync(person.BuyerId, Req(1_460m, 30m));
        Assert.Contains("นิติบุคคล", pvBad.WithholdingProblem);
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RecordManualPaymentAsync(person.BuyerId, Req(1_460m, 30m), admin));
        Assert.Equal("PLATFORM-WHT-INVALID", refused.RuleCode);
        Assert.False(await db.SubscriptionPayments.AnyAsync(p => p.SubscriptionId == person.Sub.Id));

        // ทิศตรงข้าม: นิติบุคคล ยอดในเพดาน ⇒ บันทึกได้ · วันหมดอายุที่ประทับ = ที่ preview บอก (ตัวคำนวณเดียว)
        var juristic = await PlatformBillingPhase2DbTests.SeedAsync(db, person.PlatformId, plan: SubscriptionPlan.Pro, status: SubscriptionStatus.Active, endInDays: 20);
        var jc = await db.Companies.FirstAsync(c => c.Id == juristic.BuyerId);
        jc.TaxId = JuristicTaxId;
        await db.SaveChangesAsync();
        var vat = await Accounting.Services.Platform.PlatformWithholdingReader.PlatformIssuesTaxInvoiceAsync(db);
        var wht = PlatformWhtSuggestion.MaxWithholding(PlatformWhtSuggestion.BaseOfReceived(1_490m, vat));
        var ok = Req(1_490m - wht, wht);
        var pv = await svc.PreviewManualPaymentAsync(juristic.BuyerId, ok);
        Assert.Null(pv.WithholdingProblem);
        Assert.Contains(pv.Preview.Lines, l => l.Contains("หักภาษี ณ ที่จ่าย"));
        Assert.Equal(1_490m, pv.GrossAmount);
        await svc.RecordManualPaymentAsync(juristic.BuyerId, ok, admin);
        var sub = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == juristic.Sub.Id);
        Assert.NotNull(pv.Preview.NewEndUtc);
        Assert.True(Math.Abs((sub.EndDate - pv.Preview.NewEndUtc!.Value).TotalMilliseconds) < 1, $"preview {pv.Preview.NewEndUtc:O} vs ประทับ {sub.EndDate:O}");
        var row = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.SubscriptionId == juristic.Sub.Id);
        Assert.Equal(wht, row.WithholdingTaxAmount);
    }

    [Fact]
    public async Task W3_ไฟล์50ทวิ_แนบเข้าแถวทะเบียนโดยอ้างไฟล์เดิม_ซ้ำได้id_เดิม_ไม่มีไฟล์ไม่แต่ง()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var platform = new Company { Name = "NextAcc ผู้ให้บริการ (รอบ 235)", TaxId = "0105556000001" };
        db.Companies.Add(platform);
        await db.SaveChangesAsync();
        var creditId = Guid.NewGuid();
        var p = new SubscriptionPayment { PaymentNumber = "PAY-235-W3", WhtCertificateFileName = "c235.pdf" };
        var stored = ("/srv/wwwroot/uploads/slips/c235.pdf", "application/pdf");
        var id1 = await Accounting.Services.Platform.PlatformWhtCertificateService.CertificateAttachmentAsync(db, platform.Id, creditId, p, stored);
        var id2 = await Accounting.Services.Platform.PlatformWhtCertificateService.CertificateAttachmentAsync(db, platform.Id, creditId, p, stored);
        Assert.NotNull(id1);
        Assert.Equal(id1, id2);   // idempotent — ไม่เกิดแถวไฟล์ซ้ำ
        var att = await db.FileAttachments.AsNoTracking().SingleAsync(a => a.Id == id1);
        Assert.Equal("WhtCredit", att.EntityType);
        Assert.Equal(creditId, att.EntityId);
        Assert.Equal(platform.Id, att.CompanyId);
        Assert.Equal(stored.Item1, att.StoragePath);   // อ้างไฟล์เดิม ไม่คัดลอก
        Assert.Null(att.UploadedByUserId);
        Assert.Equal(WhtCreditFileLinkKind.AlreadyThis, AttachmentPermissionScope.WhtCreditFileLink(att.EntityType, att.EntityId, creditId));   // MarkReceived ไม่ย้าย/ไม่ปฏิเสธ
        Assert.Null(await Accounting.Services.Platform.PlatformWhtCertificateService.CertificateAttachmentAsync(db, platform.Id, creditId, p, null));

        // ฝ่ายค้าน N-1: ลบไฟล์แนบที่หน้า wht-credit = ถอดแถวอย่างเดียว — ไฟล์ (ที่รายการชำระอ้างอยู่ด้วย) คงไว้ตาม AttachmentRetention (WhtCredit = หลักฐาน 5 ปี)
        Assert.True(AttachmentRetention.MustKeepPhysicalFile(att.EntityType, null));
        var deleted = new List<string>();
        var storage = F4Proxy<Accounting.Services.Storage.IFileStorage>.Create((name, args) =>
        {
            if (name == nameof(Accounting.Services.Storage.IFileStorage.DeleteAsync)) deleted.Add(args[0]?.ToString() ?? "");
            // ctor ของ FileAttachmentService อ่าน Locator.AttachmentsRoot — proxy คืน null ⇒ NRE (CI รอบ 235)
            if (name == "get_" + nameof(Accounting.Services.Storage.IFileStorage.Locator))
            {
                var root = Path.GetTempPath();
                return new Accounting.Services.Storage.FileStorageLocator(root, root, root, root);
            }
            return null;
        });
        var files = new FileAttachmentService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<FileAttachmentService>.Instance,
            F4Proxy<IErrorLogService>.Create((_, _) => null), storage);
        await files.DeleteAsync(platform.Id, id1!.Value, keepPhysicalFile: AttachmentRetention.MustKeepPhysicalFile(att.EntityType, null));
        Assert.Empty(deleted);   // ไฟล์จริงไม่ถูกลบ
        Assert.True((await db.FileAttachments.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == id1)).IsDeleted);
    }
}
