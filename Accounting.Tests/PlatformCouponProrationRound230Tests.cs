using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 230 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §E · PLATFORM_OMISE_AUDIT §7.1 · คำตัดสินข้อ 230-Q1..Q4) — ตัวตัดสิน pure:
/// ส่วนต่างอัปเกรดกลางรอบ (<see cref="PlatformProration"/>) · คูปอง (<see cref="PlatformCouponRule"/>) · ยอดที่อนุมัติอัตโนมัติได้ ·
/// หลอดการใช้งานคิดที่เซิร์ฟเวอร์ (<see cref="UsageBarRule"/> · <see cref="QuotaLimitPolicy.Percent"/>)
/// <para>ทุกด่านมีทิศ "ถูกปฏิเสธ/ไม่ใช้" และทิศตรงข้าม "ผ่าน/ไม่ถูกแตะ" (F2 ข้อ 8) · ตัวอย่างเงินตรวจมือได้</para>
/// </summary>
public class PlatformCouponProrationRound230Tests
{
    // 15/11/2569 10:00 ไทย · รอบรายปีหมด 31/03/2570 00:00 ไทย
    private static readonly DateTime Now = new(2026, 11, 15, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AnnualEnd = new(2027, 3, 30, 17, 0, 0, DateTimeKind.Utc);

    // ═════════ ส่วนต่างอัปเกรด ═════════

    [Fact]
    public void อัปเกรดรายปีกลางรอบ_คิดส่วนต่างตามวันที่เหลือ_ตัวอย่างตรวจมือ()
    {
        // ส่วนต่าง 19,900 − 9,900 = 10,000/ปี · เหลือ 136 จาก 365 วัน ⇒ round(10,000 × 136 / 365, 2) = 3,726.03
        var r = PlatformProration.Upgrade(9_900m, 19_900m, 12, AnnualEnd, Now);
        Assert.True(r.Applies);
        Assert.Equal(136, r.DaysRemaining);
        Assert.Equal(365, r.CycleDays);
        Assert.Equal(3_726.03m, r.Amount);
        Assert.Equal(AnnualEnd, r.CycleEndUtc);   // วันหมดอายุคงเดิม
        Assert.Contains("136/365", r.Note);
    }

    [Fact]
    public void ปัดครึ่งขึ้น_AwayFromZero_ไม่ใช่ปัดแบบธนาคาร()
    {
        // รอบรายเดือน 01/05/2570 → 31/05/2570 (30 วัน) เหลือ 1 วัน · ส่วนต่าง 0.15 ⇒ 0.15 × 1 / 30 = 0.005 ⇒ 0.01 (banker's = 0.00)
        var end = new DateTime(2027, 5, 30, 17, 0, 0, DateTimeKind.Utc);   // 31/05/2570 00:00 ไทย
        var now = new DateTime(2027, 5, 30, 3, 0, 0, DateTimeKind.Utc);    // 30/05/2570 10:00 ไทย
        var r = PlatformProration.Upgrade(1m, 1.15m, 1, end, now);   // รอบ 233 E-3: ราคาเดิม 0 = ไม่ใช่ส่วนต่าง (เทสต์ปัดเศษคงส่วนต่าง 0.15)
        Assert.True(r.Applies);
        Assert.Equal(1, r.DaysRemaining);
        Assert.Equal(30, r.CycleDays);
        Assert.Equal(0.01m, r.Amount);
    }

    [Fact]
    public void ไม่คิดส่วนต่าง_ทดลองใช้_ฟรีถาวร_ไม่active_เหลือไม่ถึงวัน_ราคาไม่สูงขึ้น()
    {
        Assert.False(PlatformProration.Upgrade(100m, 200m, 12, AnnualEnd, Now, isTrial: true).Applies);
        Assert.False(PlatformProration.Upgrade(100m, 200m, 12, AnnualEnd, Now, isPermanentFree: true).Applies);
        Assert.False(PlatformProration.Upgrade(100m, 200m, 12, AnnualEnd, Now, planActive: false).Applies);
        var sameDay = PlatformProration.Upgrade(100m, 200m, 12, Now.AddHours(2), Now);
        Assert.False(sameDay.Applies);
        Assert.NotNull(sameDay.NotApplicableReason);
        // ดาวน์เกรด/ราคาเท่ากัน ⇒ ไม่มีส่วนต่าง (ไม่คืนเงิน — มีผลวันครบรอบ)
        Assert.False(PlatformProration.Upgrade(19_900m, 9_900m, 12, AnnualEnd, Now).Applies);
        Assert.False(PlatformProration.Upgrade(9_900m, 9_900m, 12, AnnualEnd, Now).Applies);
    }

    [Fact]
    public void ส่วนต่างล้าสมัย_เมื่อวันหมดอายุหรือแพ็กเกจเปลี่ยน_คลาดเคลื่อนไมโครวินาทีไม่นับ()
    {
        Assert.Null(PlatformProration.StaleReason(AnnualEnd, AnnualEnd.AddTicks(7), "Basic", "Basic", Now));
        Assert.NotNull(PlatformProration.StaleReason(AnnualEnd, AnnualEnd.AddYears(1), "Basic", "Basic", Now));
        Assert.NotNull(PlatformProration.StaleReason(AnnualEnd, AnnualEnd, "Basic", "Pro", Now));
        // ฝ่ายค้าน S6: เงินเข้าหลังรอบที่คิดจบแล้ว (วันหมดอายุยังไม่ถูกแก้) ⇒ ล้าสมัย
        Assert.NotNull(PlatformProration.StaleReason(AnnualEnd, AnnualEnd, "Basic", "Basic", AnnualEnd.AddMinutes(1)));
    }

    [Fact]
    public void ใบกำกับส่วนต่าง_บอกช่วงวันที่ไทยและจำนวนวัน()
    {
        // ช่วง = วันหมดอายุ − 136 วัน (วันที่คิด) ไม่ใช่วันที่จ่าย
        var d = PlatformProration.ReceiptDescription("Basic", "Pro", AnnualEnd, 136, 365);
        Assert.Contains("Basic → Pro", d);
        Assert.Contains("15/11/2569", d);
        Assert.Contains("31/03/2570", d);
        Assert.Contains("136/365", d);
    }

    [Fact]
    public void อนุมัติอัตโนมัติ_เทียบยอดที่เซิร์ฟเวอร์เสนอ_ไม่มีก็ราคาเต็มเดิม()
    {
        Assert.Equal(3_726.03m, PlatformPlanPricing.AutoApproveExpected(3_726.03m, 19_900m));
        Assert.Equal(19_900m, PlatformPlanPricing.AutoApproveExpected(null, 19_900m));
        Assert.Equal(19_900m, PlatformPlanPricing.AutoApproveExpected(0m, 19_900m));
    }

    // ═════════ คูปอง ═════════

    private static PlatformCoupon Coupon(PlatformCouponKind kind = PlatformCouponKind.Percent, decimal value = 10m) => new()
    {
        Code = "NEW10", Kind = kind, Value = value, AppliesTo = PlatformCouponScope.Both, MaxUsesPerCompany = 1, IsActive = true,
    };

    private static PlatformCouponRule.Target Sub(string plan = "Pro", string cycle = "Monthly", bool first = true)
        => new(PlatformCouponScope.Subscription, plan, cycle, null, first);

    [Fact]
    public void ลดเปอร์เซ็นต์และจำนวนเงิน_ไม่ต่ำกว่าศูนย์_ปัดAwayFromZero()
    {
        var p = PlatformCouponRule.Apply(Coupon(), 990m, Sub(), Now, 0, 0);
        Assert.True(p.Ok);
        Assert.Equal(99m, p.Discount);
        Assert.Equal(891m, p.AmountAfter);

        var f = PlatformCouponRule.Apply(Coupon(PlatformCouponKind.Fixed, 500m), 990m, Sub(), Now, 0, 0);
        Assert.True(f.Ok);
        Assert.Equal(500m, f.Discount);
        Assert.Equal(490m, f.AmountAfter);

        // ฝ่ายค้าน S4: ลดเกิน/เท่าราคา ⇒ ไม่ติดลบ และ "ใช้ไม่ได้" ตัดสินที่นี่ตัวเดียว (หน้าดูราคา = ตอนจอง) — ไม่ลดเงิน
        var all = PlatformCouponRule.Apply(Coupon(PlatformCouponKind.Fixed, 2_000m), 990m, Sub(), Now, 0, 0);
        Assert.False(all.Ok);
        Assert.Contains("0 บาท", all.Problem);
        Assert.Equal(990m, all.AmountAfter);
        Assert.False(PlatformCouponRule.Apply(Coupon(value: 100m), 990m, Sub(), Now, 0, 0).Ok);

        // 25% ของ 0.10 = 0.025 ⇒ 0.03 (banker's = 0.02)
        Assert.Equal(0.03m, PlatformCouponRule.Apply(Coupon(value: 25m), 0.10m, Sub(), Now, 0, 0).Discount);
    }

    [Fact]
    public void คูปองใช้ไม่ได้_ทุกเหตุบอกเป็นภาษาไทย_และไม่ลดเงิน()
    {
        var c = Coupon();
        void No(PlatformCoupon cp, PlatformCouponRule.Target t, int used = 0, int mine = 0)
        {
            var r = PlatformCouponRule.Apply(cp, 990m, t, Now, used, mine);
            Assert.False(r.Ok);
            Assert.False(string.IsNullOrWhiteSpace(r.Problem));
            Assert.Equal(990m, r.AmountAfter);
            Assert.Equal(0m, r.Discount);
        }
        No(null!, Sub());
        // ฝ่ายค้าน N1: ไม่มี/ปิดใช้ ⇒ ข้อความเดียวกัน (ไม่บอกว่ารหัสมีจริง)
        Assert.Equal(PlatformCouponRule.InvalidCodeMessage, PlatformCouponRule.Apply(null, 990m, Sub(), Now, 0, 0).Problem);
        Assert.Equal(PlatformCouponRule.InvalidCodeMessage, PlatformCouponRule.Apply(
            new PlatformCoupon { Code = "OFF", Kind = PlatformCouponKind.Percent, Value = 10m, IsActive = false, AppliesTo = PlatformCouponScope.Both }, 990m, Sub(), Now, 0, 0).Problem);
        No(new PlatformCoupon { Code = "X", Kind = PlatformCouponKind.Percent, Value = 10m, IsActive = false, AppliesTo = PlatformCouponScope.Both }, Sub());
        No(new PlatformCoupon { Code = "X", Kind = PlatformCouponKind.Percent, Value = 10m, IsActive = true, AppliesTo = PlatformCouponScope.Both, ValidToUtc = Now }, Sub());
        No(new PlatformCoupon { Code = "X", Kind = PlatformCouponKind.Percent, Value = 10m, IsActive = true, AppliesTo = PlatformCouponScope.Both, ValidFromUtc = Now.AddDays(1) }, Sub());
        No(new PlatformCoupon { Code = "X", Kind = PlatformCouponKind.Percent, Value = 10m, IsActive = true, AppliesTo = PlatformCouponScope.QuotaAddOn }, Sub());
        No(new PlatformCoupon { Code = "X", Kind = PlatformCouponKind.Percent, Value = 10m, IsActive = true, AppliesTo = PlatformCouponScope.Both, PlansCsv = "Basic" }, Sub("Pro"));
        No(new PlatformCoupon { Code = "X", Kind = PlatformCouponKind.Percent, Value = 10m, IsActive = true, AppliesTo = PlatformCouponScope.Both, CyclesCsv = "Annual" }, Sub(cycle: "Monthly"));
        No(new PlatformCoupon { Code = "X", Kind = PlatformCouponKind.Percent, Value = 10m, IsActive = true, AppliesTo = PlatformCouponScope.Both, AddOnResourcesCsv = QuotaResources.Users },
            new PlatformCouponRule.Target(PlatformCouponScope.QuotaAddOn, null, null, QuotaResources.CmsSites, true));
        No(new PlatformCoupon { Code = "X", Kind = PlatformCouponKind.Percent, Value = 10m, IsActive = true, AppliesTo = PlatformCouponScope.Both, FirstPaymentOnly = true }, Sub(first: false));
        No(new PlatformCoupon { Code = "X", Kind = PlatformCouponKind.Percent, Value = 10m, IsActive = true, AppliesTo = PlatformCouponScope.Both, MaxUsesTotal = 3 }, Sub(), used: 3);
        No(c, Sub(), mine: 1);   // ต่อบริษัท 1 ครั้ง ใช้ไปแล้ว
    }

    [Fact]
    public void ทิศตรงข้าม_เงื่อนไขครบ_ใช้ได้_ตัวพิมพ์เล็กใหญ่ไม่มีผล()
    {
        var c = new PlatformCoupon
        {
            Code = "PRO-YEAR", Kind = PlatformCouponKind.Percent, Value = 20m, IsActive = true, AppliesTo = PlatformCouponScope.Subscription,
            PlansCsv = "Basic,Pro", CyclesCsv = "Annual", ValidFromUtc = Now.AddDays(-1), ValidToUtc = Now.AddDays(1), MaxUsesTotal = 3, MaxUsesPerCompany = 1,
            FirstPaymentOnly = true,
        };
        var r = PlatformCouponRule.Apply(c, 19_900m, Sub("pro", "annual", first: true), Now, 2, 0);
        Assert.True(r.Ok);
        Assert.Equal(3_980m, r.Discount);
        Assert.Equal(15_920m, r.AmountAfter);
        Assert.Equal("PRO-YEAR", PlatformCouponRule.Normalize("  pro-year "));
        Assert.Null(PlatformCouponRule.Normalize("   "));
    }

    [Fact]
    public void ค่าที่แอดมินกรอก_ตรวจที่เซิร์ฟเวอร์()
    {
        Assert.NotNull(PlatformCouponRule.AdminProblem("ลด10", PlatformCouponKind.Percent, 10m, null, null, null, 1, PlatformCouponScope.Both));
        Assert.NotNull(PlatformCouponRule.AdminProblem("A", PlatformCouponKind.Percent, 101m, null, null, null, 1, PlatformCouponScope.Both));
        Assert.NotNull(PlatformCouponRule.AdminProblem("A", PlatformCouponKind.Fixed, 0m, null, null, null, 1, PlatformCouponScope.Both));
        Assert.NotNull(PlatformCouponRule.AdminProblem("A", PlatformCouponKind.Percent, 10m, Now, Now, null, 1, PlatformCouponScope.Both));
        Assert.NotNull(PlatformCouponRule.AdminProblem("A", PlatformCouponKind.Percent, 10m, null, null, 0, 1, PlatformCouponScope.Both));
        Assert.NotNull(PlatformCouponRule.AdminProblem("A", PlatformCouponKind.Percent, 10m, null, null, null, 1, PlatformCouponScope.None));
        Assert.Null(PlatformCouponRule.AdminProblem("new-10_x", PlatformCouponKind.Percent, 10m, Now, Now.AddDays(30), 100, 1, PlatformCouponScope.Both));
    }

    [Fact]
    public void ใบกำกับมีส่วนลด_ส่วนลดก่อนVAT_ฐานบวกVATเท่าเงินที่รับพอดี()
    {
        // ราคา 990 (รวม VAT) ลด 99 ⇒ รับ 891
        var (full, disc, net, vat) = PlatformCouponRule.SplitDiscounted(891m, 99m, isVat: true);
        Assert.Equal(925.23m, full);    // round(990 × 100/107)
        Assert.Equal(832.71m, net);     // round(891 × 100/107)
        Assert.Equal(92.52m, disc);
        Assert.Equal(58.29m, vat);
        Assert.Equal(891m, net + vat);
        Assert.Equal(net, full - disc);

        // ไม่จด VAT ⇒ ไม่มี VAT · ไม่มีส่วนลด ⇒ บรรทัดเดิม (ทิศตรงข้าม: ใบที่ไม่มีคูปองไม่ถูกแตะ)
        Assert.Equal((990m, 99m, 891m, 0m), PlatformCouponRule.SplitDiscounted(891m, 99m, isVat: false));
        var (f2, d2, n2, v2) = PlatformCouponRule.SplitDiscounted(990m, 0m, isVat: true);
        Assert.Equal(0m, d2);
        Assert.Equal(f2, n2);
        Assert.Equal(PlatformMoneyInAccount.SplitReceived(990m, true), (n2, v2));
    }

    [Fact]
    public void แอดมินแก้ยอดรายการมีคูปอง_ส่วนลดบนใบเท่าราคาก่อนคูปองลบยอดใหม่_และรับเกินไม่ทำให้ราคาเต็มเกินจริง()
    {
        // ฝ่ายค้าน S5: ราคา 990 (คูปองเดิมลด 99 ⇒ 891) · แอดมินแก้เป็น 800 ⇒ ส่วนลดบนใบ 190 · ราคาเต็มบนใบยังเป็น 990 (ไม่ใช่ 800 + 99)
        var (full, disc, net, vat) = PlatformCouponRule.SplitDiscounted(800m, 990m - 800m, isVat: true);
        Assert.Equal(925.23m, full);
        Assert.Equal(800m, net + vat);
        Assert.Equal(full - disc, net);

        // ฝ่ายค้าน N7: โควตาเสริม ราคา 199 ลด 99.50 ⇒ 99.50 · รับ 99.50 ⇒ ส่วนลด 99.50 · รับเกิน 150 ⇒ ส่วนลด 49 (ราคาเต็มบนใบ = 199) · รับ ≥ 199 ⇒ 0 · ไม่มีคูปอง ⇒ 0
        var row = new CompanyQuotaAddOn { AmountDue = 99.50m, ListAmount = 199m, CouponDiscountAmount = 99.50m };
        Assert.Equal(99.50m, Accounting.Services.Payments.QuotaAddOnPurchaseService.ReceiptDiscount(row, 99.50m));
        Assert.Equal(49m, Accounting.Services.Payments.QuotaAddOnPurchaseService.ReceiptDiscount(row, 150m));
        Assert.Equal(0m, Accounting.Services.Payments.QuotaAddOnPurchaseService.ReceiptDiscount(row, 250m));
        Assert.Equal(0m, Accounting.Services.Payments.QuotaAddOnPurchaseService.ReceiptDiscount(new CompanyQuotaAddOn { AmountDue = 199m }, 199m));
    }

    // ═════════ หลอดการใช้งาน (server computes) ═════════

    [Fact]
    public void หลอดการใช้งาน_คิดที่เซิร์ฟเวอร์_เกณฑ์ไม่จำกัดและสีเตือน()
    {
        var u = UsageBarRule.Users(7, 10);
        Assert.Equal(70.0m, u.Percent);
        Assert.Equal(1, u.Level);
        Assert.False(u.Unlimited);

        var unlimitedUsers = UsageBarRule.Users(50, 999);
        Assert.True(unlimitedUsers.Unlimited);
        Assert.Equal(0m, unlimitedUsers.Percent);
        Assert.Equal("ไม่จำกัด", unlimitedUsers.LimitText);

        Assert.True(UsageBarRule.Documents(10, 0).Unlimited);          // ไม่ตั้ง = ตัวนับเอกสารไม่บังคับ
        Assert.True(UsageBarRule.Documents(10, 99_999).Unlimited);
        Assert.Equal(2, UsageBarRule.Documents(95, 100).Level);
        Assert.True(UsageBarRule.Storage(1, QuotaResources.UnlimitedBytes).Unlimited);
        Assert.Equal(100m, UsageBarRule.Storage(2_000, 1_000).Percent);  // เกินเพดาน = เต็มหลอด ไม่เกิน 100

        var ocr = UsageBarRule.Ocr(90, 50, 30, 20);                       // เพดานจริง = 50 + โบนัส 30 + เครดิต 20 = 100
        Assert.Equal(100, ocr.Limit);
        Assert.Equal(90.0m, ocr.Percent);
        Assert.Equal(2, ocr.Level);
        Assert.True(UsageBarRule.Ocr(5, -1, 0, 0).Unlimited);
        Assert.Equal(100m, UsageBarRule.Ocr(1, 0, 0, 0).Percent);         // เพดาน 0 แต่ใช้แล้ว

        Assert.Equal(0, UsageBarRule.DaysLeft(Now.AddDays(-3), Now));
        Assert.Equal(136, UsageBarRule.DaysLeft(AnnualEnd, Now));
    }

    [Fact]
    public void ความยาวหลอดโควตา_คิดที่เซิร์ฟเวอร์()
    {
        Assert.Equal(75, QuotaLimitPolicy.Percent(3, 4, 0));
        Assert.Equal(100, QuotaLimitPolicy.Percent(9, 4, 2));
        Assert.Equal(100, QuotaLimitPolicy.Percent(1, 0, 2));
        Assert.Equal(0, QuotaLimitPolicy.Percent(5, null, 0));     // ไม่จำกัด
        Assert.Equal(0, QuotaLimitPolicy.Percent(null, null, 0));  // ไม่รู้ = ไม่แต่งตัวเลข
    }
}
