using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 230 (คำตัดสินข้อ 230-Q1..Q4) บน PostgreSQL จริง — เส้นเงินที่ pure test มองไม่เห็น:
/// อัปเกรดกลางรอบจ่ายส่วนต่าง (วันหมดอายุคงเดิม) ↔ ดาวน์เกรดมีผลวันครบรอบ (ไม่เก็บเงิน) · ส่วนต่างล้าสมัยถูกพักให้แอดมิน ·
/// คูปอง: จองใต้ล็อก/นับเมื่อเงินเข้า/ปลดเมื่อปฏิเสธ/เกินเพดาน = ไม่สร้างรายการ · webhook ซ้ำนับครั้งเดียว ·
/// เปลี่ยนรายการต่ออายุหลังออกใบ: ยังไม่จ่าย = ปลดใบเดิม (ทิศตรงข้ามของการล็อกใน QuotaAddOnPaymentDbTests) · แอดมินแก้ยอด
/// </summary>
[Trait("Category", "Db")]
public class PlatformCouponProrationDbTests
{
    private readonly ITestOutputHelper _out;
    public PlatformCouponProrationDbTests(ITestOutputHelper output) => _out = output;

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    private sealed class Sp : IServiceProvider
    {
        private readonly ISubscriptionService _subs;
        public Sp(ISubscriptionService subs) => _subs = subs;
        public object? GetService(Type t) => t == typeof(ISubscriptionService) ? _subs : null;
    }

    private static async Task<(PlanTemplate Low, PlanTemplate High)?> TwoTiersAsync(Accounting.Data.AccountingDbContext db)
    {
        async Task Ensure(SubscriptionPlan plan, decimal m, decimal a)
        {
            if (await db.PlanTemplates.AnyAsync(t => t.Plan == plan && t.IsActive)) return;
            db.PlanTemplates.Add(new PlanTemplate
            {
                Name = $"แพ็กเกจทดสอบรอบ 230 {plan}", Plan = plan, IsActive = true, MonthlyPrice = m, QuarterlyPrice = m * 3, SemiAnnualPrice = m * 6, AnnualPrice = a,
                MaxUsers = 5, MaxCompanies = 1, MaxDocumentsPerMonth = 500, MaxJournalEntriesPerMonth = 500,
            });
            await db.SaveChangesAsync();
        }
        await Ensure(SubscriptionPlan.Basic, 490m, 4_900m);
        await Ensure(SubscriptionPlan.Pro, 990m, 9_900m);
        var low = await db.PlanTemplates.AsNoTracking().FirstAsync(t => t.Plan == SubscriptionPlan.Basic && t.IsActive);
        var high = await db.PlanTemplates.AsNoTracking().FirstAsync(t => t.Plan == SubscriptionPlan.Pro && t.IsActive);
        if (PlatformPlanPricing.Classify(low.MonthlyPrice, high.MonthlyPrice) != PlatformPlanPricing.ChangeKind.Upgrade
            || high.IsPermanentFree || PlatformPlanPricing.CyclePrice(high, BillingCycle.Annual) <= PlatformPlanPricing.CyclePrice(low, BillingCycle.Annual))
            return null;   // ฐานร่วมตั้งราคาไว้ไม่เข้าเงื่อนไข — ข้าม (ไม่แก้แม่แบบของเทสต์อื่น)
        return (low, high);
    }

    private static async Task<(Guid CompanyId, Subscription Sub)> SeedAsync(Accounting.Data.AccountingDbContext db, SubscriptionPlan plan,
        SubscriptionStatus status = SubscriptionStatus.Active, BillingCycle cycle = BillingCycle.Annual, int daysLeft = 200)
    {
        var c = new Company { Name = "บริษัททดสอบรอบ 230 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000005" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        var sub = new Subscription
        {
            CompanyId = c.Id, Plan = plan, Status = status, BillingCycle = cycle,
            StartDate = DateTime.UtcNow.AddDays(-100), EndDate = DateTime.UtcNow.AddDays(daysLeft), UsageResetDate = DateTime.UtcNow.AddDays(20),
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        return (c.Id, sub);
    }

    private static async Task<PlatformCoupon> CouponAsync(Accounting.Data.AccountingDbContext db, decimal percent, int? maxTotal, int? perCompany = 1)
    {
        var c = new PlatformCoupon
        {
            Code = "T230-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Kind = PlatformCouponKind.Percent, Value = percent,
            AppliesTo = PlatformCouponScope.Both, MaxUsesTotal = maxTotal, MaxUsesPerCompany = perCompany, IsActive = true,
        };
        db.PlatformCoupons.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task อัปเกรดกลางรอบจ่ายส่วนต่างวันหมดอายุคงเดิม_ดาวน์เกรดไม่เก็บเงินมีผลวันครบรอบ_ส่วนต่างล้าสมัยถูกพัก()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        var (low, high) = tiers.Value;
        var svc = new SubscriptionService(db, Notifications());

        // อัปเกรด ⇒ รายการส่วนต่าง (ยอด = ตัวคิดตัวเดียว) · ยังไม่เปลี่ยนแพ็กเกจจนเงินเข้า
        var (cid, sub) = await SeedAsync(db, SubscriptionPlan.Basic);
        var endBefore = sub.EndDate;
        var expected = PlatformProration.Upgrade(PlatformPlanPricing.CyclePrice(low, BillingCycle.Annual), PlatformPlanPricing.CyclePrice(high, BillingCycle.Annual),
            12, endBefore, DateTime.UtcNow);
        Assert.True(expected.Applies);
        var res = await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Pro), "owner");
        Assert.NotNull(res.Payment);
        var pay = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == res.Payment!.Id);
        Assert.True(pay.IsProration);
        Assert.Equal(0, pay.RequestedPeriodMonths);
        Assert.Equal(expected.Amount, pay.Amount);
        Assert.Equal(pay.Amount, pay.QuotedAmount);
        Assert.Equal(SubscriptionPlan.Basic, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id)).Plan);

        // เงินเข้า (ระบบยืนยัน) ⇒ เปลี่ยนแพ็กเกจทันที · วันหมดอายุเดิม (คลาดเคลื่อน < 1 ms จากความละเอียดของฐาน)
        await svc.ReviewPaymentAsync(pay.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);
        var up = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Equal(SubscriptionPlan.Pro, up.Plan);
        Assert.True(Math.Abs((up.EndDate - endBefore).TotalMilliseconds) < 1, $"วันหมดอายุต้องคงเดิม: {endBefore:O} vs {up.EndDate:O}");
        Assert.Equal(SubscriptionPaymentStatus.Approved, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).Status);

        // ทิศตรงข้าม: ดาวน์เกรด ⇒ ไม่มีรายการชำระ · แพ็กเกจ/วันหมดอายุไม่ขยับ · ตั้งมีผลวันครบรอบ (ไม่คืนเงิน)
        var down = await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Basic), "owner");
        Assert.Null(down.Payment);
        var afterDown = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Equal(SubscriptionPlan.Pro, afterDown.Plan);
        Assert.Equal(SubscriptionPlan.Basic, afterDown.ScheduledPlan);
        Assert.True(Math.Abs((afterDown.EndDate - endBefore).TotalMilliseconds) < 1);
        Assert.Equal(1, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id));

        // ส่วนต่างล้าสมัย (วันหมดอายุเปลี่ยนหลังคิด) + เงินเข้าออนไลน์ ⇒ ไม่เปลี่ยนแพ็กเกจเอง · พักให้แอดมิน (ล้มดัง)
        var (cid2, sub2) = await SeedAsync(db, SubscriptionPlan.Basic);
        var r2 = await svc.ChangeSubscriptionAsync(cid2, new ChangeSubscriptionRequest(SubscriptionPlan.Pro), "owner");
        var tracked = await db.Subscriptions.SingleAsync(s => s.Id == sub2.Id);
        tracked.EndDate = tracked.EndDate.AddYears(1);
        await db.SaveChangesAsync();
        await svc.ReviewPaymentAsync(r2.Payment!.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);
        var held = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == r2.Payment!.Id);
        Assert.Equal(SubscriptionPaymentStatus.UnderReview, held.Status);
        Assert.Equal(PlatformPaymentAttention.RuleHeld, held.AttentionRule);
        Assert.Equal(SubscriptionPlan.Basic, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub2.Id)).Plan);
    }

    [Fact]
    public async Task คูปอง_จองตอนสร้าง_นับเมื่อเงินเข้า_ปลดเมื่อปฏิเสธ_เกินเพดานไม่สร้างรายการ_แอดมินแก้ยอด()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        var (_, high) = tiers.Value;
        var svc = new SubscriptionService(db, Notifications());
        var coupon = await CouponAsync(db, 10m, maxTotal: 2);
        var list = PlatformPlanPricing.AmountDue(high, BillingCycle.Monthly);
        var after = PlatformCouponRule.R2(list - PlatformCouponRule.R2(list * 10m / 100m));

        // บริษัท A: ทดลองใช้ → ซื้อ Pro พร้อมคูปอง ⇒ ยอดหลังส่วนลด + จองไว้ (ยังไม่นับ)
        var (a, _) = await SeedAsync(db, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, BillingCycle.Monthly, 10);
        var ra = await svc.ConvertTrialAsync(a, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Monthly, coupon.Code.ToLowerInvariant()), "owner");
        var pa = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == ra.Payment!.Id);
        Assert.Equal(after, pa.Amount);
        Assert.Equal(list, pa.ListAmount);
        Assert.Equal(coupon.Code, pa.CouponCode);
        Assert.Equal(PlatformCouponRedemptionStatus.Reserved,
            (await db.PlatformCouponRedemptions.AsNoTracking().SingleAsync(r => r.SourceId == pa.Id)).Status);
        Assert.Equal(0, (await db.PlatformCoupons.AsNoTracking().SingleAsync(c => c.Id == coupon.Id)).UsedCount);

        // เงินเข้าออนไลน์ ⇒ handler อนุมัติอัตโนมัติด้วยยอดหลังคูปอง (ยอดที่เซิร์ฟเวอร์เสนอ — เดิมต้องเท่าราคาเต็มเท่านั้น) · นับ (UsedCount 1)
        var handler = new Accounting.Services.Payments.Handlers.SubscriptionPaymentHandler(db, svc,
            NullLogger<Accounting.Services.Payments.Handlers.SubscriptionPaymentHandler>.Instance);
        await handler.HandleSucceededAsync(new PaymentIntent
        {
            Id = Guid.NewGuid(), ProviderCode = "test", SourceKind = PaymentSourceKind.SubscriptionPayment, SourceId = pa.Id, PayerCompanyId = a,
            Amount = pa.Amount, PaidAt = DateTime.UtcNow, ConfirmedBy = "payment-gateway",
        });
        Assert.Equal(SubscriptionPaymentStatus.Approved, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pa.Id)).Status);
        Assert.Equal(PlatformCouponRedemptionStatus.Redeemed,
            (await db.PlatformCouponRedemptions.AsNoTracking().SingleAsync(r => r.SourceId == pa.Id)).Status);
        Assert.Equal(1, (await db.PlatformCoupons.AsNoTracking().SingleAsync(c => c.Id == coupon.Id)).UsedCount);

        // บริษัท B: จองแล้วถูกปฏิเสธ ⇒ ปลด (ไม่นับเข้าเพดาน)
        var (b, _) = await SeedAsync(db, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, BillingCycle.Monthly, 10);
        var rb = await svc.ConvertTrialAsync(b, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Monthly, coupon.Code), "owner");
        await svc.ReviewPaymentAsync(rb.Payment!.Id, new ReviewSubscriptionPaymentRequest(false, null, "สลิปไม่ตรง"), Guid.NewGuid().ToString());
        Assert.Equal(PlatformCouponRedemptionStatus.Released,
            (await db.PlatformCouponRedemptions.AsNoTracking().SingleAsync(r => r.SourceId == rb.Payment!.Id)).Status);

        // บริษัท C ใช้สิทธิ์ที่เหลือ (เพดานรวม 2) · บริษัท D เกินเพดาน ⇒ 400 PLATFORM-COUPON และไม่มีรายการเกิด
        var (c3, _) = await SeedAsync(db, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, BillingCycle.Monthly, 10);
        var pc3 = (await svc.ConvertTrialAsync(c3, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Monthly, coupon.Code), "owner")).Payment!;
        // ฝ่ายค้าน S5: แอดมินแก้ยอดรายการมีคูปอง ⇒ ส่วนลด = ราคาก่อนคูปอง − ยอดใหม่ (ใบกำกับราคาเต็มยังเท่าราคาจริง) · แถวการใช้แก้ตาม
        var newAmt = PlatformCouponRule.R2(list - 1m);
        var ov3 = await svc.OverridePaymentAmountAsync(pc3.Id, newAmt, "ส่วนลดพิเศษเพิ่ม", "admin");
        Assert.Equal(1m, ov3.CouponDiscountAmount);
        Assert.Equal(list, ov3.ListAmount);
        var red3 = await db.PlatformCouponRedemptions.AsNoTracking().SingleAsync(r => r.SourceId == pc3.Id);
        Assert.Equal(1m, red3.DiscountAmount);
        Assert.Equal(newAmt, red3.AmountAfter);
        var (full3, _, net3, vat3) = PlatformCouponRule.SplitDiscounted(newAmt, ov3.CouponDiscountAmount, isVat: true);
        Assert.Equal(PlatformMoneyInAccount.SplitReceived(list, true).Base, full3);
        Assert.Equal(newAmt, net3 + vat3);
        var (d, subD) = await SeedAsync(db, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, BillingCycle.Monthly, 10);
        var full = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.ConvertTrialAsync(d, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Monthly, coupon.Code), "owner"));
        Assert.Equal(PlatformCouponRule.RuleCode, full.RuleCode);
        Assert.Equal(0, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == subD.Id));

        // ทิศตรงข้าม: ไม่ใส่โค้ด ⇒ ราคาเต็ม ไม่แตะคูปอง
        var plain = await svc.ConvertTrialAsync(d, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Monthly), "owner");
        Assert.Equal(list, plain.Payment!.Amount);
        Assert.Null((await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == plain.Payment.Id)).CouponCode);

        // แอดมินแก้ยอดรายการที่ยังไม่จ่าย ⇒ ยอด/ยอดที่อนุมัติอัตโนมัติได้เปลี่ยน + เหตุผล + audit · ไม่มีเหตุผล = ปฏิเสธ
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.OverridePaymentAmountAsync(plain.Payment.Id, 500m, "", "admin"));
        var ov = await svc.OverridePaymentAmountAsync(plain.Payment.Id, 500m, "ตกลงราคาพิเศษ", "admin");
        Assert.Equal(500m, ov.Amount);
        Assert.Equal(500m, ov.QuotedAmount);
        Assert.True(await db.AuditLogs.AnyAsync(x => x.CompanyId == d && x.EntityId == plain.Payment.Id.ToString()
            && x.NewValues!.Contains("PLATFORM-ADMIN-PRICE-OVERRIDE")));
        // อนุมัติแล้วแก้ไม่ได้
        await svc.ReviewPaymentAsync(plain.Payment.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.OverridePaymentAmountAsync(plain.Payment.Id, 400m, "ลองแก้หลังอนุมัติ", "admin"));
    }

    [Fact]
    public async Task โควตาเสริมมีคูปอง_เงินเข้าซ้ำนับครั้งเดียว_เปลี่ยนรายการต่ออายุก่อนจ่ายปลดใบเดิม()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var (cid, sub) = await SeedAsync(db, SubscriptionPlan.Pro);
        var subs = new SubscriptionService(db, Notifications());
        var quota = new QuotaAddOnPurchaseService(db, subs, new Sp(subs), NullLogger<QuotaAddOnPurchaseService>.Instance);
        var coupon = await CouponAsync(db, 50m, maxTotal: null, perCompany: null);

        // รายการโควตาเสริมที่จองคูปองไว้ (ยอดหลังส่วนลด 99.50 จาก 199)
        var row = new CompanyQuotaAddOn
        {
            CompanyId = cid, OfferId = Guid.NewGuid(), ResourceKey = QuotaResources.CmsSites, Units = 1, UnitSize = 1, Mode = QuotaAddOnMode.OneOffMonth,
            PeriodStartUtc = DateTime.UtcNow.AddMinutes(-5), PeriodEndUtc = DateTime.UtcNow.AddDays(20), Status = QuotaAddOnStatus.AwaitingPayment,
            AmountDue = 99.50m, ListAmount = 199m, UnitPriceSnapshot = 199m, MonthlyUnitPriceSnapshot = 199m,
            CouponId = coupon.Id, CouponCode = coupon.Code, CouponDiscountAmount = 99.50m, CreatedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        db.CompanyQuotaAddOns.Add(row);
        db.PlatformCouponRedemptions.Add(new PlatformCouponRedemption
        {
            CouponId = coupon.Id, CompanyId = cid, SourceKind = PlatformCouponRule.SourceQuotaAddOn, SourceId = row.Id,
            ListAmount = 199m, DiscountAmount = 99.50m, AmountAfter = 99.50m, ReservedUntilUtc = DateTime.UtcNow.AddDays(14),
        });
        await db.SaveChangesAsync();
        var intent = Guid.NewGuid();
        await quota.MarkPaidByGatewayAsync(cid, row.Id, intent, 99.50m, "test");
        await quota.MarkPaidByGatewayAsync(cid, row.Id, intent, 99.50m, "test");   // webhook ซ้ำ
        Assert.Equal(1, (await db.PlatformCoupons.AsNoTracking().SingleAsync(c => c.Id == coupon.Id)).UsedCount);
        Assert.Equal(QuotaAddOnStatus.Active, (await db.CompanyQuotaAddOns.AsNoTracking().SingleAsync(x => x.Id == row.Id)).Status);

        // ใบต่ออายุออกแล้วแต่ยังไม่มีใครจ่าย ⇒ เลิกต่อได้ · ปลดตัวชี้ใบเดิม (บนแพ็กเกจและบรรทัด) + ประวัติ "ถูกแทนที่" (ทิศตรงข้ามของ 409)
        var invoiceId = Guid.NewGuid();
        var tracked = await db.Subscriptions.SingleAsync(s => s.Id == sub.Id);
        tracked.RenewalInvoiceDocumentId = invoiceId; tracked.RenewalInvoiceNumber = "INV-T230"; tracked.RenewalInvoiceForEndDate = tracked.EndDate;
        tracked.RenewalInvoiceIssuedAt = DateTime.UtcNow;
        var co = new CompanyQuotaAddOn
        {
            CompanyId = cid, OfferId = Guid.NewGuid(), ResourceKey = QuotaResources.CmsSites, Units = 2, UnitSize = 1, Mode = QuotaAddOnMode.Coterm,
            PeriodStartUtc = DateTime.UtcNow.AddDays(-5), PeriodEndUtc = tracked.EndDate, Status = QuotaAddOnStatus.Active,
            AmountDue = 100m, UnitPriceSnapshot = 50m, MonthlyUnitPriceSnapshot = 199m, RenewWithPlan = true,
            RenewalInvoiceDocumentId = invoiceId, RenewUnitPrice = 1990m, RenewUnits = 2,
        };
        db.CompanyQuotaAddOns.Add(co);
        await db.SaveChangesAsync();
        await quota.SetRenewalAsync(cid, co.Id, false, null, "owner");
        var coAfter = await db.CompanyQuotaAddOns.AsNoTracking().SingleAsync(x => x.Id == co.Id);
        Assert.False(coAfter.RenewWithPlan);
        Assert.Null(coAfter.RenewalInvoiceDocumentId);
        var subAfter = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Null(subAfter.RenewalInvoiceDocumentId);
        Assert.Null(subAfter.RenewalInvoiceNumber);
        Assert.True(await db.SubscriptionHistories.AnyAsync(h => h.SubscriptionId == sub.Id && h.Action == "RenewalInvoiceSuperseded"));

        // ฝ่ายค้าน S2 ทิศตรงข้าม: ใบที่ถูกแจ้งชำระแล้ว ⇒ 409 · ตัวชี้และรายการไม่เปลี่ยน
        var inv2 = Guid.NewGuid();
        var t2 = await db.Subscriptions.SingleAsync(s => s.Id == sub.Id);
        t2.RenewalInvoiceDocumentId = inv2; t2.RenewalInvoiceNumber = "INV-T230-2"; t2.RenewalInvoiceForEndDate = t2.EndDate;
        var co2 = await db.CompanyQuotaAddOns.SingleAsync(x => x.Id == co.Id);
        co2.RenewWithPlan = true; co2.RenewalInvoiceDocumentId = inv2;
        db.SubscriptionPayments.Add(new SubscriptionPayment
        {
            SubscriptionId = sub.Id, PaymentNumber = "SP-S2-" + Guid.NewGuid().ToString("N")[..8], Amount = 100m, PaymentDate = DateTime.UtcNow,
            RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Annual, Status = SubscriptionPaymentStatus.Pending,
            PlatformInvoiceDocumentId = inv2, SlipFileName = "slip.jpg",
        });
        await db.SaveChangesAsync();
        var locked = await Assert.ThrowsAsync<BusinessRuleException>(() => quota.SetRenewalAsync(cid, co.Id, false, null, "owner"));
        Assert.Equal("QUOTA-RENEW-LOCKED", locked.RuleCode);
        Assert.True((await db.CompanyQuotaAddOns.AsNoTracking().SingleAsync(x => x.Id == co.Id)).RenewWithPlan);
        Assert.Equal(inv2, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id)).RenewalInvoiceDocumentId);
    }

    private static Accounting.Services.Payments.PlatformPaymentService PlatformPay(Accounting.Data.AccountingDbContext db)
        => new(db, F4Proxy<Accounting.Services.Payments.IPaymentIntentService>.Create((_, _) => null),
            F4Proxy<Accounting.Services.Payments.IAddOnPurchaseService>.Create((_, _) => null),
            Array.Empty<Accounting.Services.Payments.IPaymentProvider>());

    /// <summary>ฝ่ายค้าน B1: ด่านยอดตอนเริ่มชำระออนไลน์ใช้ตัวตัดสินเดียวกับ handler — รายการส่วนต่าง/คูปองผ่านด่านยอด (ล้มต่อได้แค่เพราะช่องทางยังไม่เปิดในฐานทดสอบ) ·
    /// ทิศตรงข้าม: รายการราคาเต็ม (ไม่มี QuotedAmount) ที่ยอดไม่ตรงราคาปัจจุบัน ⇒ ปฏิเสธเหมือนเดิม</summary>
    [Fact]
    public async Task เริ่มชำระออนไลน์_รายการส่วนต่างและคูปองผ่านด่านยอด_รายการราคาเต็มที่ยอดไม่ตรงถูกปฏิเสธ()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        var svc = new SubscriptionService(db, Notifications());
        const string mismatch = "ไม่ตรงกับราคาแพ็กเกจปัจจุบัน";
        async Task<string?> StartProblem(Guid cid, Guid payId)
        {
            try { await PlatformPay(db).StartSubscriptionPaymentAsync(cid, payId, PaymentMethodKind.PromptPay, null, null); return null; }
            catch (Exception ex) { return ex.Message; }   // ช่องทางยังไม่เปิด/ตัวแทน intent ว่างในฐานทดสอบ — ขอแค่ไม่ใช่ด่านยอด
        }

        var (cid, _) = await SeedAsync(db, SubscriptionPlan.Basic);
        var prorated = (await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Pro), "owner")).Payment!;
        Assert.True(prorated.IsProration);
        Assert.DoesNotContain(mismatch, await StartProblem(cid, prorated.Id) ?? "");

        var coupon = await CouponAsync(db, 10m, maxTotal: null, perCompany: null);
        var (cid2, _) = await SeedAsync(db, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, BillingCycle.Monthly, 10);
        var couponed = (await svc.ConvertTrialAsync(cid2, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Monthly, coupon.Code), "owner")).Payment!;
        Assert.NotNull(couponed.QuotedAmount);
        Assert.DoesNotContain(mismatch, await StartProblem(cid2, couponed.Id) ?? "");

        // ทิศตรงข้าม: ราคาเต็ม ไม่มีคูปอง ⇒ QuotedAmount ไม่ถูกประทับ (คำตัดสิน B1) · ยอดถูกแก้ตรงในฐาน (จำลองราคาเปลี่ยน) ⇒ ปฏิเสธ
        var (cid3, _) = await SeedAsync(db, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, BillingCycle.Monthly, 10);
        var full = (await svc.ConvertTrialAsync(cid3, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Monthly), "owner")).Payment!;
        Assert.Null(full.QuotedAmount);
        var t = await db.SubscriptionPayments.SingleAsync(p => p.Id == full.Id);
        t.Amount = t.Amount + 1m;
        await db.SaveChangesAsync();
        Assert.Contains(mismatch, await StartProblem(cid3, full.Id) ?? "");
    }

    /// <summary>ฝ่ายค้าน S1/S3/S6: ใบต่ออายุเดิมถูกแจ้งชำระแล้ว ⇒ เงินส่วนต่างเข้าออนไลน์ถูกพัก (ไม่แทนที่ใบ) · ส่วนต่างใบใหม่แทนใบเก่าที่ยังไม่จ่าย ·
    /// รายการมีคูปอง/ส่วนต่างค้างเกินระยะจอง ⇒ ยกเลิก + ปลดคูปอง (ทิศตรงข้าม: รายการราคาเต็มค้างไม่ถูกแตะ)</summary>
    [Fact]
    public async Task ใบต่ออายุเดิมถูกจ่ายแล้วพักส่วนต่าง_ส่วนต่างใหม่แทนใบเก่า_ค้างเกินกำหนดยกเลิกและปลดคูปอง()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        var svc = new SubscriptionService(db, Notifications());

        // S1
        var (cid, sub) = await SeedAsync(db, SubscriptionPlan.Basic);
        var invoiceId = Guid.NewGuid();
        var tracked = await db.Subscriptions.SingleAsync(s => s.Id == sub.Id);
        tracked.RenewalInvoiceDocumentId = invoiceId; tracked.RenewalInvoiceNumber = "INV-S1"; tracked.RenewalInvoiceForEndDate = tracked.EndDate;
        db.SubscriptionPayments.Add(new SubscriptionPayment
        {
            SubscriptionId = sub.Id, PaymentNumber = "SP-S1-" + Guid.NewGuid().ToString("N")[..8], Amount = 4_900m, PaymentDate = DateTime.UtcNow,
            RequestedPlan = SubscriptionPlan.Basic, RequestedBillingCycle = BillingCycle.Annual, RequestedPeriodMonths = 12,
            Status = SubscriptionPaymentStatus.Pending, PlatformInvoiceDocumentId = invoiceId, SlipFileName = "slip.jpg",
        });
        await db.SaveChangesAsync();
        var first = (await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Pro), "owner")).Payment!;
        // S6: ส่วนต่างใบใหม่ (ยอดต่าง) แทนใบเก่าที่ยังไม่มีสลิป/intent
        var t1 = await db.SubscriptionPayments.SingleAsync(p => p.Id == first.Id);
        t1.ListAmount = null; t1.Amount += 5m; t1.QuotedAmount = t1.Amount;   // จำลองใบที่คิดเมื่อวาน (ยอดคนละค่า)
        await db.SaveChangesAsync();
        var second = (await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Pro), "owner")).Payment!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(SubscriptionPaymentStatus.Cancelled, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == first.Id)).Status);
        await svc.ReviewPaymentAsync(second.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);
        var held = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == second.Id);
        Assert.Equal(SubscriptionPaymentStatus.UnderReview, held.Status);
        var keep = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Equal(invoiceId, keep.RenewalInvoiceDocumentId);   // ไม่แทนที่ใบที่ถูกแจ้งชำระแล้ว
        Assert.Equal(SubscriptionPlan.Basic, keep.Plan);

        // S3: รายการคูปองค้างเกินระยะจอง ⇒ ยกเลิก + ปลด · ราคาเต็มค้างไม่ถูกแตะ
        var coupon = await CouponAsync(db, 10m, maxTotal: null, perCompany: null);
        var (c2, _) = await SeedAsync(db, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, BillingCycle.Monthly, 10);
        var stale = (await svc.ConvertTrialAsync(c2, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Monthly, coupon.Code), "owner")).Payment!;
        var (c3, _) = await SeedAsync(db, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, BillingCycle.Monthly, 10);
        var plain = (await svc.ConvertTrialAsync(c3, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Monthly), "owner")).Payment!;
        foreach (var id in new[] { stale.Id, plain.Id })
            (await db.SubscriptionPayments.SingleAsync(p => p.Id == id)).CreatedAt = DateTime.UtcNow.AddDays(-(PlatformCouponRule.ReservationDays + 1));
        await db.SaveChangesAsync();
        await svc.ExpireStaleQuotedPaymentsAsync();
        Assert.Equal(SubscriptionPaymentStatus.Cancelled, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == stale.Id)).Status);
        Assert.Equal(PlatformCouponRedemptionStatus.Released, (await db.PlatformCouponRedemptions.AsNoTracking().SingleAsync(r => r.SourceId == stale.Id)).Status);
        Assert.Equal(SubscriptionPaymentStatus.Pending, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == plain.Id)).Status);
    }
}
