using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Payments;
using Accounting.Tests.Db;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 235 (คำตัดสิน 235-1…235-10 · erp-review/2026-11-03/LICENSE_CASE_MATRIX.md §5) — ปิดงานค้างฝั่งแพ็กเกจ/บิล · เทสต์ pure สองทิศทุกด่าน
/// (บั๊ก/ช่องที่ปิด + ของที่ถูกอยู่แล้วไม่ถูกแตะ) · เวลาคงที่ · วันที่ไทย พ.ศ. ตรวจมือได้
/// </summary>
public class PlatformRound235Tests
{
    private static DateTime Thai(int y, int m, int d, int hh, int mm) => new DateTime(y, m, d, hh, mm, 0, DateTimeKind.Utc).AddHours(-7);
    private static DateTime ThaiMidnight(int y, int m, int d) => Thai(y, m, d, 0, 0);
    private static DateTime ThaiTen(int y, int m, int d) => Thai(y, m, d, 10, 0);

    private static PlanPurchasePreview.Current Cur(SubscriptionPlan plan, BillingCycle cycle, SubscriptionStatus status, DateTime end, decimal monthly)
        => new(plan, plan.ToString(), cycle, status, false, end, monthly, false, null, null);
    private static PlanPurchasePreview.Target Tgt(SubscriptionPlan plan, BillingCycle cycle, decimal monthly) => new(plan, plan.ToString(), cycle, monthly);

    // ═════════ K-4 (235-2): บวกเดือนบนปฏิทินไทย ═════════

    [Fact]
    public void K4_จ่าย31มค0630ไทย_บวก1เดือน_ได้28กพ_ไม่ใช่1มีค()
    {
        var paid = Thai(2027, 1, 31, 6, 30);                       // = 30 ม.ค. 23:30 UTC
        var end = PlatformPlanPricing.AddCycleMonths(paid, 1);
        Assert.Equal(Thai(2027, 2, 28, 6, 30), end);               // ปีปกติ ⇒ 28 ก.พ.
        Assert.NotEqual(paid.AddMonths(1), end);                   // บั๊กเดิม: AddMonths บน UTC = 28 ก.พ. 23:30 UTC = 1 มี.ค. ไทย
        Assert.Equal(Thai(2028, 2, 29, 6, 30), PlatformPlanPricing.AddCycleMonths(Thai(2028, 1, 31, 6, 30), 1));   // ปีอธิกสุรทิน ⇒ 29 ก.พ.
    }

    [Fact]
    public void K4_ทิศตรงข้าม_เวลาหลัง07น_และกลางเดือน_ผลเท่าเดิม()
    {
        foreach (var t in new[] { ThaiTen(2027, 1, 31), ThaiTen(2027, 1, 15), Thai(2027, 3, 15, 23, 59) })
            Assert.Equal(t.AddMonths(1), PlatformPlanPricing.AddCycleMonths(t, 1));
        Assert.Equal(ThaiTen(2027, 1, 15), PlatformPlanPricing.AddCycleMonths(ThaiTen(2027, 1, 15), 0));
    }

    [Fact]
    public void K4_PaidPeriod_ต่ออายุและหมดอายุแล้ว_ใช้ปฏิทินไทยตัวเดียว()
    {
        var oldEnd = Thai(2027, 1, 31, 6, 30);
        var renew = PlanPurchasePreview.PaidPeriod(PlatformPlanPricing.ChangeKind.Same, true, false, false, false, false, oldEnd, ThaiTen(2027, 1, 20), 1);
        Assert.Equal(oldEnd, renew.StartUtc);
        Assert.Equal(Thai(2027, 2, 28, 6, 30), renew.EndUtc);
        var expired = PlanPurchasePreview.PaidPeriod(PlatformPlanPricing.ChangeKind.Same, true, false, false, false, false, ThaiMidnight(2026, 12, 1),
            Thai(2027, 1, 31, 2, 0), 1);
        Assert.Equal(Thai(2027, 2, 28, 2, 0), expired.EndUtc);    // ตัวอย่างใน MATRIX K-4: 31 ม.ค. 02:00 → เดิม 1 มี.ค.
        // ปิดสถานะ "ไม่มีรอบ" + เปลี่ยนรอบบิล ใช้ตัวเดียวกัน
        Assert.Equal(Thai(2027, 2, 28, 6, 30), SubscriptionExpiry.CloseOpenEnded(Thai(2027, 1, 31, 6, 30), BillingCycle.Monthly).EndDate);
        var cc = PlatformProration.CycleChange(1_000m, 1, 3_000m, 3, ThaiMidnight(2027, 2, 15), Thai(2027, 1, 31, 6, 30));
        Assert.Equal(Thai(2027, 4, 30, 6, 30), cc.NewEndUtc);
    }

    // ═════════ K-2 (235-1): คูปองตอนต่ออายุ ═════════

    private static PlatformCoupon Coupon(PlatformCouponPurchaseKinds kinds, PlatformCouponScope scope = PlatformCouponScope.Subscription)
        => new() { Code = "R235", Kind = PlatformCouponKind.Percent, Value = 10m, AppliesTo = scope, PurchaseKinds = kinds, IsActive = true, MaxUsesPerCompany = null };

    private static PlatformCouponRule.Evaluation Apply(PlatformCoupon c, bool renewal, PlatformCouponScope scope = PlatformCouponScope.Subscription)
        => PlatformCouponRule.Apply(c, 1_000m, new PlatformCouponRule.Target(scope, "Pro", "Annual", scope == PlatformCouponScope.QuotaAddOn ? "ocr" : null, false, renewal),
            ThaiTen(2026, 11, 4), 0, 0);

    [Fact]
    public void K2_คูปองที่เปิดต่ออายุ_ใช้ตอนต่ออายุได้_ลดก่อนVAT()
    {
        var ev = Apply(Coupon(PlatformCouponPurchaseKinds.Both), renewal: true);
        Assert.True(ev.Ok);
        Assert.Equal(100m, ev.Discount);
        Assert.Equal(900m, ev.AmountAfter);
        // ใบ: ส่วนลดหักก่อน VAT (ตัวแยกเดียว) — ฐานหลังลด + VAT = เงินที่รับพอดี
        var (full, disc, net, vat) = PlatformCouponRule.SplitDiscounted(900m, 100m, true);
        Assert.Equal(net + disc, full);
        Assert.Equal(900m, net + vat);
    }

    [Fact]
    public void S1_คูปองเดิมและคูปองใหม่ค่าตั้งต้น_ซื้อใหม่เท่านั้น_ต่ออายุไม่ลด()
    {
        Assert.Equal(PlatformCouponPurchaseKinds.New, new PlatformCoupon().PurchaseKinds);   // คูปองใหม่: แอดมินต้องเปิดต่ออายุเอง
        // แถวที่มีอยู่ก่อนคอลัมน์ = DEFAULT 1 (ซื้อใหม่เท่านั้น — คงนโยบาย 233-5) · ไม่มี UPDATE ที่รันทุกบูต (ไม่ทับค่าที่แอดมินตั้ง)
        var sql = string.Join("\n", PlatformCouponMigration.Statements());
        Assert.Contains("\"PurchaseKinds\" integer NOT NULL DEFAULT 1", sql);
        Assert.DoesNotContain("UPDATE \"PlatformCoupons\"", sql);
        var legacy = new PlatformCoupon { Code = "OLD10", Kind = PlatformCouponKind.Percent, Value = 10m, AppliesTo = PlatformCouponScope.Subscription, IsActive = true, MaxUsesPerCompany = null };
        Assert.False(Apply(legacy, renewal: true).Ok);
        Assert.True(Apply(legacy, renewal: false).Ok);   // ทิศตรงข้าม: ซื้อใหม่ยังลดตามเดิม
    }

    [Fact]
    public void S2_ต่ออายุที่ได้ราคาพิเศษอยู่แล้ว_คูปองไม่ลดซ้อน_ซื้อใหม่ยังลดตามเดิม()
    {
        var c = Coupon(PlatformCouponPurchaseKinds.Both);
        var t = new PlatformCouponRule.Target(PlatformCouponScope.Subscription, "Pro", "Annual", null, false, IsRenewal: true, RenewalSpecialPrice: true);
        var ev = PlatformCouponRule.Apply(c, 1_000m, t, ThaiTen(2026, 11, 4), 0, 0);
        Assert.False(ev.Ok);
        Assert.Equal(PlatformCouponRule.RenewalSpecialPriceMessage, ev.Problem);
        Assert.True(PlatformCouponRule.Apply(c, 1_000m, t with { RenewalSpecialPrice = false }, ThaiTen(2026, 11, 4), 0, 0).Ok);
        // ซื้อใหม่/อัปเกรด (231-8: ราคาพิเศษ → คูปอง) ไม่ถูกด่านนี้
        Assert.True(PlatformCouponRule.Apply(c, 1_000m, t with { IsRenewal = false }, ThaiTen(2026, 11, 4), 0, 0).Ok);
    }

    [Fact]
    public void K2_ทิศตรงข้าม_คูปองซื้อใหม่เท่านั้น_ไม่ลดตอนต่ออายุ_แต่ลดตอนซื้อใหม่()
    {
        var c = Coupon(PlatformCouponPurchaseKinds.New);
        var renew = Apply(c, renewal: true);
        Assert.False(renew.Ok);
        Assert.Equal(0m, renew.Discount);
        Assert.Contains("ต่ออายุไม่ได้", renew.Problem);
        Assert.True(Apply(c, renewal: false).Ok);
        var only = Coupon(PlatformCouponPurchaseKinds.Renewal);
        Assert.Contains("เฉพาะการต่ออายุ", Apply(only, renewal: false).Problem);
        Assert.True(Apply(only, renewal: true).Ok);
        // โควตาเสริมไม่ดูช่องนี้ (ไม่มีความหมายของ "ต่ออายุ" ในเส้นนั้น)
        Assert.True(Apply(Coupon(PlatformCouponPurchaseKinds.Renewal, PlatformCouponScope.Both), renewal: false, PlatformCouponScope.QuotaAddOn).Ok);
    }

    [Fact]
    public void K2_ต่ออายุคือแพ็กเดิมรอบเดิม_ไม่ใช่ทดลอง_ฟรีถาวร_ส่วนต่าง()
    {
        Assert.True(PlatformCouponRule.IsRenewalTarget(SubscriptionStatus.Active, false, SubscriptionPlan.Pro, BillingCycle.Annual, SubscriptionPlan.Pro, BillingCycle.Annual));
        Assert.True(PlatformCouponRule.IsRenewalTarget(SubscriptionStatus.Expired, false, SubscriptionPlan.Pro, BillingCycle.Annual, SubscriptionPlan.Pro, BillingCycle.Annual));
        Assert.False(PlatformCouponRule.IsRenewalTarget(SubscriptionStatus.Trial, false, SubscriptionPlan.Pro, BillingCycle.Annual, SubscriptionPlan.Pro, BillingCycle.Annual));
        Assert.False(PlatformCouponRule.IsRenewalTarget(SubscriptionStatus.Active, true, SubscriptionPlan.Pro, BillingCycle.Annual, SubscriptionPlan.Pro, BillingCycle.Annual));
        Assert.False(PlatformCouponRule.IsRenewalTarget(SubscriptionStatus.Active, false, SubscriptionPlan.Basic, BillingCycle.Annual, SubscriptionPlan.Pro, BillingCycle.Annual));
        Assert.False(PlatformCouponRule.IsRenewalTarget(SubscriptionStatus.Active, false, SubscriptionPlan.Pro, BillingCycle.Monthly, SubscriptionPlan.Pro, BillingCycle.Annual));
        Assert.False(PlatformCouponRule.IsRenewalTarget(SubscriptionStatus.Active, false, SubscriptionPlan.Pro, BillingCycle.Annual, SubscriptionPlan.Pro, BillingCycle.Annual, prorated: true));
    }

    [Fact]
    public void K2_หน้าซื้อสาธารณะ_ต่ออายุไม่ถูกปฏิเสธเพราะคูปองแล้ว_ใบต่ออายุออกแล้วยังปฏิเสธ()
    {
        var d = PublicCheckoutPolicy.DecideOrder(SubscriptionStatus.Active, false, SubscriptionPlan.Pro, BillingCycle.Annual, 1_490m,
            SubscriptionPlan.Pro, BillingCycle.Annual, 1_490m, 0m, renewalInvoiceIssued: false);
        Assert.Equal(PublicCheckoutPolicy.OrderKind.Renewal, d.Kind);
        var inv = PublicCheckoutPolicy.DecideOrder(SubscriptionStatus.Active, false, SubscriptionPlan.Pro, BillingCycle.Annual, 1_490m,
            SubscriptionPlan.Pro, BillingCycle.Annual, 1_490m, 0m, renewalInvoiceIssued: true);
        Assert.Equal(PublicCheckoutPolicy.RenewalInvoiceIssuedRefusal, inv.Refusal);
    }

    [Fact]
    public void K2_แอดมินต้องเลือกอย่างน้อยหนึ่งชนิดการซื้อ()
    {
        Assert.NotNull(PlatformCouponRule.AdminProblem("R235", PlatformCouponKind.Percent, 10m, null, null, null, 1, PlatformCouponScope.Subscription,
            PlatformCouponPurchaseKinds.None));
        Assert.Null(PlatformCouponRule.AdminProblem("R235", PlatformCouponKind.Percent, 10m, null, null, null, 1, PlatformCouponScope.Subscription));
        Assert.Equal("ซื้อใหม่/อัปเกรดเท่านั้น", PlatformCouponRule.PurchaseKindsLabel(PlatformCouponPurchaseKinds.New));
    }

    // ═════════ K-7 (235-3) + ข้อ 6 (235-6): preview หน้าแอดมิน ═════════

    [Fact]
    public void K7_แอดมิน_วันรับเงินที่กรอก_เดือนที่กรอก_บรรทัดภาษีหัก()
    {
        var end = ThaiMidnight(2027, 1, 1);
        var cur = Cur(SubscriptionPlan.Pro, BillingCycle.Annual, SubscriptionStatus.Active, end, 1_490m);
        var pv = PlanPurchasePreview.ForPurchase(cur, Tgt(SubscriptionPlan.Pro, BillingCycle.Annual, 1_490m), new PlanPurchasePreview.Money(15_198m, 0m, 15_198m),
            ThaiTen(2026, 11, 4), true, paidAtUtc: ThaiTen(2026, 11, 1), months: 12, approvedAtUtc: ThaiTen(2026, 11, 4), withholdingTax: 426.11m);
        Assert.Equal(PlatformPlanPricing.AddCycleMonths(end, 12), pv.NewEndUtc);   // ต่อจากวันหมดอายุเดิม (ตัวคำนวณเดียวกับตอนอนุมัติ)
        Assert.Contains(pv.Lines, l => l.Contains("หักภาษี ณ ที่จ่าย −฿426.11") && l.Contains("รับสุทธิ ฿14,771.89"));
        // ทิศตรงข้าม: หน้าผู้เช่า (ไม่ส่งพารามิเตอร์ใหม่) — ไม่มีบรรทัดภาษีหัก · วันที่เหมือนเดิม
        var tenant = PlanPurchasePreview.ForPurchase(cur, Tgt(SubscriptionPlan.Pro, BillingCycle.Annual, 1_490m), new PlanPurchasePreview.Money(15_198m, 0m, 15_198m),
            ThaiTen(2026, 11, 4), true);
        Assert.DoesNotContain(tenant.Lines, l => l.Contains("หักภาษี ณ ที่จ่าย"));
        Assert.Equal(pv.NewEndUtc, tenant.NewEndUtc);
        // เดือนที่แอดมินกรอกต่างจากรอบ (เช่น 13 เดือน) ⇒ วันที่ตามที่กรอก
        var custom = PlanPurchasePreview.ForPurchase(cur, Tgt(SubscriptionPlan.Pro, BillingCycle.Annual, 1_490m), new PlanPurchasePreview.Money(15_198m, 0m, 15_198m),
            ThaiTen(2026, 11, 4), true, paidAtUtc: ThaiTen(2026, 11, 1), months: 13);
        Assert.Equal(PlatformPlanPricing.AddCycleMonths(end, 13), custom.NewEndUtc);
    }

    [Fact]
    public void K7_วันรับเงินย้อนหลังเกินรอบ_บอกว่านับจากวันอนุมัติ_ทิศตรงข้ามไม่บอก()
    {
        var cur = Cur(SubscriptionPlan.Pro, BillingCycle.Monthly, SubscriptionStatus.Expired, ThaiMidnight(2025, 1, 1), 1_490m);
        var late = PlanPurchasePreview.ForPurchase(cur, Tgt(SubscriptionPlan.Pro, BillingCycle.Monthly, 1_490m), new PlanPurchasePreview.Money(1_490m, 0m, 1_490m),
            ThaiTen(2026, 11, 4), true, paidAtUtc: ThaiTen(2025, 6, 1), months: 1, approvedAtUtc: ThaiTen(2026, 11, 4));
        Assert.Contains(PlanPurchasePreview.BackdatedNote, late.Notes);
        var ok = PlanPurchasePreview.ForPurchase(cur, Tgt(SubscriptionPlan.Pro, BillingCycle.Monthly, 1_490m), new PlanPurchasePreview.Money(1_490m, 0m, 1_490m),
            ThaiTen(2026, 11, 4), true, paidAtUtc: ThaiTen(2026, 11, 1), months: 1, approvedAtUtc: ThaiTen(2026, 11, 4));
        Assert.DoesNotContain(PlanPurchasePreview.BackdatedNote, ok.Notes);
    }

    [Fact]
    public void K7_ตัดชำระใบอย่างเดียว_วันหมดอายุคงเดิม()
    {
        var end = ThaiMidnight(2027, 1, 1);
        var pv = PlanPurchasePreview.ForInvoiceOnly(Cur(SubscriptionPlan.Pro, BillingCycle.Annual, SubscriptionStatus.Active, end, 1_490m), 2_140m, 60m, true, ThaiTen(2026, 11, 4));
        Assert.True(pv.EndUnchanged);
        Assert.Equal(end, pv.NewEndUtc);
        Assert.Contains("01/01/2570", pv.DateLine);
        Assert.Contains(pv.Lines, l => l.Contains("รับสุทธิ ฿2,080.00"));
    }

    [Fact]
    public void ข้อ6_แอดมินกรอกภาษีหักเอง_ตัวตรวจเดียว_นิติบุคคล_เพดานอัตรา_ห้ามติดลบ()
    {
        Assert.Null(SubscriptionService.ManualWithholdingProblem(0m, 1_000m, true, false));                         // ไม่หัก = ผ่านทุกผู้จ่าย
        Assert.Null(SubscriptionService.ManualWithholdingProblem(426.11m, 14_771.89m, true, true));                  // Pro รายปี 15,198 ⇒ เพดาน 426.11 พอดี
        Assert.Contains("นิติบุคคล", SubscriptionService.ManualWithholdingProblem(426.11m, 14_771.89m, true, false));   // บุคคลธรรมดาหักไม่ได้
        Assert.Contains("เกินอัตรา", SubscriptionService.ManualWithholdingProblem(500m, 14_698m, true, true));
        Assert.Contains("ติดลบ", SubscriptionService.ManualWithholdingProblem(-5m, 1_000m, true, true));            // เดิมตัดเป็น 0 เงียบ
    }

    [Fact]
    public void S5_ทางยกเว้นตาม50ทวิจริง_ต้องติ๊กและเหตุผลอย่างน้อย10ตัว_ยอดติดลบไม่มีทางยกเว้น()
    {
        var gate = SubscriptionService.ManualWithholdingProblem(426.11m, 14_771.89m, true, false)!;   // บุคคลธรรมดา
        Assert.Contains("บันทึกตามหนังสือรับรองจริง", SubscriptionService.ManualWithholdingOverrideRefusal(gate, 426.11m, false, null));   // ไม่ติ๊ก ⇒ ด่านเดิม + ทางไปต่อ
        Assert.NotNull(SubscriptionService.ManualWithholdingOverrideRefusal(gate, 426.11m, true, "สั้นไป"));   // เหตุผล < 10
        Assert.Null(SubscriptionService.ManualWithholdingOverrideRefusal(gate, 426.11m, true, "ลูกค้าส่ง 50 ทวิ ฉบับจริงระบุยอดนี้"));
        var neg = SubscriptionService.ManualWithholdingProblem(-5m, 1_000m, true, true)!;
        Assert.Equal(neg, SubscriptionService.ManualWithholdingOverrideRefusal(neg, -5m, true, "ลูกค้าส่ง 50 ทวิ ฉบับจริงระบุยอดนี้"));
        Assert.Null(SubscriptionService.ManualWithholdingOverrideRefusal(null, 0m, false, null));   // ไม่มีปัญหา = ผ่าน
    }

    // ═════════ ฝ่ายค้าน S-4: คืนเงินรายการที่หัก ณ ที่จ่าย ═════════

    [Fact]
    public void S4_คืนเต็ม_ภาษีหักทั้งหมดกลับ_ใบลดหนี้เต็มยอด_คืนบางส่วนตามสัดส่วน()
    {
        Assert.Equal(30m, PlatformRefundCreditNote.WithholdingShare(1_040m, 1_040m, 30m, 0m, 0m));
        var full = PlatformRefundCreditNote.Decide(1_040m, true, false, 1_040m, 0m, true, 30m);
        Assert.Equal(1_070m, full.BaseAmount + full.VatAmount);   // ใบเดิม 1,070 − ใบลดหนี้ 1,070 = 0
        Assert.Equal(15m, PlatformRefundCreditNote.WithholdingShare(520m, 1_040m, 30m, 0m, 0m));
        Assert.Equal(15m, PlatformRefundCreditNote.WithholdingShare(520m, 1_040m, 30m, 520m, 15m));   // ครั้งสุดท้าย = ที่เหลือทั้งหมด (ไม่มีเศษค้าง)
        Assert.Equal(3m, PlatformRefundCreditNote.WithholdingRate(30m, 1_000m));
        // ทิศตรงข้าม: ไม่ได้หัก ⇒ ใบลดหนี้ = เงินที่คืนเท่าเดิม
        Assert.Equal(0m, PlatformRefundCreditNote.WithholdingShare(535m, 535m, 0m, 0m, 0m));
        var plain = PlatformRefundCreditNote.Decide(107m, true, false, 535m, 0m, true);
        Assert.Equal(107m, plain.BaseAmount + plain.VatAmount);
    }

    [Fact]
    public void S4_ทะเบียน50ทวิ_Pendingลดหรือหมดสิทธิ์_ยืนยันแล้วไม่แตะขึ้นเรื่องค้าง()
    {
        Assert.Equal(PlatformRefundCreditNote.RegistryAction.Expire, PlatformRefundCreditNote.RegistryActionFor(WhtCreditStatus.Pending, 30m, 0m));
        Assert.Equal(PlatformRefundCreditNote.RegistryAction.Reduce, PlatformRefundCreditNote.RegistryActionFor(WhtCreditStatus.Pending, 15m, 15m));
        Assert.Equal(PlatformRefundCreditNote.RegistryAction.AttentionConfirmed, PlatformRefundCreditNote.RegistryActionFor(WhtCreditStatus.Received, 30m, 0m));
        Assert.Equal(PlatformRefundCreditNote.RegistryAction.AttentionConfirmed, PlatformRefundCreditNote.RegistryActionFor(WhtCreditStatus.Claimed, 30m, 0m));
        Assert.Equal(PlatformRefundCreditNote.RegistryAction.None, PlatformRefundCreditNote.RegistryActionFor(null, 30m, 0m));
        Assert.Equal(PlatformRefundCreditNote.RegistryAction.None, PlatformRefundCreditNote.RegistryActionFor(WhtCreditStatus.Pending, 0m, 30m));
    }

    // ═════════ ข้อ 9 (235-9): แจ้ง "รอตรวจ" เมื่อมีหลักฐานเท่านั้น ═════════

    [Fact]
    public void ข้อ9_สร้างรายการเฉยๆไม่แจ้ง_แนบสลิปครั้งแรกแจ้ง_แนบซ้ำไม่แจ้ง_ปิดแล้วไม่แจ้ง()
    {
        Assert.False(PlatformPaymentReviewNotice.ShouldNotify(hasSlipNow: false, hadSlipBefore: false, isPending: true));   // บั๊กเดิม: ต่ออายุผ่าน checkout แจ้งทั้งที่ยังไม่จ่าย
        Assert.True(PlatformPaymentReviewNotice.ShouldNotify(true, false, true));
        Assert.False(PlatformPaymentReviewNotice.ShouldNotify(true, true, true));
        Assert.False(PlatformPaymentReviewNotice.ShouldNotify(true, false, false));
        Assert.Contains("PAY-1", PlatformPaymentReviewNotice.Message("PAY-1", 590m));
    }

    // ═════════ K-8 (235-4): อีเมล ═════════

    [Fact]
    public void K8_อีเมล_แพ็ก_รอบ_ใช้ได้ถึงพศ_ยอด_ภาษีหัก()
    {
        var lines = PlatformPurchaseEmailText.Lines("Professional", BillingCycle.Annual, ThaiMidnight(2028, 1, 1), true, 14_771.89m, 426.11m);
        Assert.Contains(lines, l => l.Contains("แพ็กเกจ Professional") && l.Contains("รายปี") && l.Contains("12 เดือน"));
        Assert.Contains(lines, l => l.Contains("ใช้ได้ถึง 01/01/2571") && l.Contains("ถ้าชำระวันนี้"));
        Assert.Contains(lines, l => l.Contains("ยอดค่าบริการ 15,198.00") && l.Contains("หักภาษี ณ ที่จ่าย 426.11") && l.Contains("สุทธิ 14,771.89"));
        Assert.Contains(lines, l => l.Contains("50 ทวิ"));
        // ทิศตรงข้าม: ไม่หัก ⇒ ไม่มีบรรทัดภาษี/50 ทวิ · ใบเสร็จ (วันที่ประทับแล้ว) ไม่มีคำว่า "ถ้าชำระวันนี้" · ไม่รู้วันที่ ⇒ ไม่แต่ง
        var plain = PlatformPurchaseEmailText.Lines("Starter", BillingCycle.Monthly, ThaiMidnight(2027, 2, 1), false, 590m, 0m);
        Assert.Contains("ยอดชำระ 590.00 บาท", plain);
        Assert.DoesNotContain(plain, l => l.Contains("50 ทวิ") || l.Contains("ถ้าชำระวันนี้"));
        Assert.DoesNotContain(PlatformPurchaseEmailText.Lines(null, null, null, false, 2_140m, 0m), l => l.Contains("ใช้ได้ถึง") || l.Contains("แพ็กเกจ"));
    }

    // ═════════ ข้อ 5 (235-5): ส่วนลดบนใบของโควตาเสริมที่หักภาษี ═════════

    [Fact]
    public void ข้อ5_โควตาเสริมหักภาษี_ส่วนลดบนใบยังเป็นคูปองพอดี_ไม่รวมภาษีหัก()
    {
        var withWht = new CompanyQuotaAddOn { ListAmount = 1_000m, CouponDiscountAmount = 100m, AmountDue = 874.77m, WithholdingTaxAmount = 25.23m };
        Assert.Equal(100m, QuotaAddOnPurchaseService.ReceiptDiscount(withWht, 874.77m));   // เดิม (ไม่บวกภาษีหัก) = 125.23 ⇒ ภาษีหักกลายเป็นส่วนลด
        var plain = new CompanyQuotaAddOn { ListAmount = 1_000m, CouponDiscountAmount = 100m, AmountDue = 900m };
        Assert.Equal(100m, QuotaAddOnPurchaseService.ReceiptDiscount(plain, 900m));
        Assert.Equal(0m, QuotaAddOnPurchaseService.ReceiptDiscount(new CompanyQuotaAddOn { AmountDue = 970m, WithholdingTaxAmount = 30m }, 970m));
    }

    // ═════════ ข้อ 10 (235-10): เส้นบันทึกบัตรเดียว ═════════

    [Fact]
    public async Task ข้อ10_ไม่ติ๊กไม่บันทึก_ติ๊กแต่ไม่ใช่บัตรปฏิเสธ_ติ๊กบัตรบันทึกพร้อมยินยอม()
    {
        var calls = new List<object?[]>();
        var cards = F4Proxy<IPlatformSavedCardService>.Create((name, args) =>
        {
            if (name == nameof(IPlatformSavedCardService.SaveFromTokenAsync)) { calls.Add(args); return new PlatformCardRefs(Guid.NewGuid(), Guid.Empty, "omise", false, "cust", "card"); }
            return null;
        });
        var actor = new PlatformCardActor(Guid.NewGuid(), "a@test.local", "1.1.1.1", "ua");
        Assert.Null(await PlatformSavedCardStart.SaveIfAskedAsync(cards, Guid.NewGuid(),
            new PlatformSavedCardStart.Request(PaymentMethodKind.Card, "tok", false, false, null, false), actor, default));
        Assert.Empty(calls);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => PlatformSavedCardStart.SaveIfAskedAsync(cards, Guid.NewGuid(),
            new PlatformSavedCardStart.Request(PaymentMethodKind.PromptPay, null, true, true, "v2", false), actor, default));
        Assert.Equal(PlatformSavedCardRules.RuleSave, ex.RuleCode);
        var saved = await PlatformSavedCardStart.SaveIfAskedAsync(cards, Guid.NewGuid(),
            new PlatformSavedCardStart.Request(PaymentMethodKind.Card, "tok", true, true, "autocharge-2", true), actor, default);
        Assert.NotNull(saved);
        Assert.Single(calls);
        Assert.Equal(true, calls[0][2]);              // consentAccepted ส่งต่อตามที่ติ๊ก
        Assert.Equal("autocharge-2", calls[0][3]);    // ฉบับข้อความที่แสดง (เซิร์ฟเวอร์ตรวจกับฉบับปัจจุบัน)
        Assert.Equal(true, calls[0][4]);              // รับทราบภาษีหัก ณ ที่จ่าย (นิติบุคคล)
    }
}
