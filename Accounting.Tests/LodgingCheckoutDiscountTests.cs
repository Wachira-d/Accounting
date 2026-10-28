using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ส่วนลดเช็คเอาต์ที่พัก (รอบ 205 แพ็ก W5 · R-49) — ส่วนลด = ส่วนของราคา คิด<b>ก่อน</b>หักมัดจำ
/// <list type="bullet">
/// <item>ตัวตัดสิน <see cref="LodgingCheckoutDiscount.Resolve"/>: บาทตามราคาที่แขกเห็น (ราคารวม VAT ⇒ แยกฐาน) · % ของฐาน · เหตุผลบังคับ · เกินยอด/ติดลบ/ทั้งสองแบบ = ปฏิเสธ</item>
/// <item>เดินผ่าน <see cref="DocumentService.PreviewTotals"/> + <see cref="LodgingDepositSettlement.PlanCheckout"/> ตัวจริง: 7,450 ลด 500 มัดจำ 2,000 ⇒
///   ฐานใบ = 6,962.62 − 467.29 − 1,869.16 · VAT ของทั้งเรื่องยังเท่ากับ VAT ของ (7,450 − 500) ครั้งเดียว</item>
/// <item><b>ติดลบ</b>: ลดจนราคาต่ำกว่ามัดจำที่ออกใบกำกับแล้ว ⇒ หักได้เท่าฐานหลังลด ส่วนเกิน = ค้างคืน (ใบลดหนี้อ้างใบมัดจำตอนยืนยันคืนเงิน — เส้นเดิม)</item>
/// <item><b>ทิศตรงข้าม</b>: ไม่ใส่ส่วนลด ⇒ <see cref="LodgingCheckoutDiscountPlan.None"/> ⇒ PlanCheckout ตัวเลขเดิมทุกสตางค์ ทั้งมัดจำ VAT ทันที · VAT พัก · เต็มยอด</item>
/// </list>
/// </summary>
public class LodgingCheckoutDiscountTests
{
    private static readonly Guid Guest = Guid.NewGuid();

    private static List<DocumentLineRequest> Stay(decimal gross) => new()
    {
        new(Description: "ค่าห้องพัก", Quantity: 1, Unit: "คืน", UnitPrice: gross, DiscountPercent: 0, VatRate: 7m, WithholdingTaxRate: 0, AccountId: null),
    };

    // มัดจำ 2,000 รวม VAT 7% → ฐาน 1,869.16 + VAT 130.84 (ออกใบกำกับแล้ว)
    private static LodgingDepositSnapshot Taxed2000() => new(Guid.NewGuid(), "TIV-0001", Guest, 1869.16m, 130.84m, 2000m, false, 0m, 0m);
    private static LodgingDepositSnapshot Pending2000() => new(Guid.NewGuid(), "DEP-0001", Guest, 1869.16m, 130.84m, 2000m, VatPending: true, 0m, 0m);
    private static LodgingDepositSnapshot Full2000() => new(Guid.NewGuid(), "REC-0001", Guest, 2000m, 0m, 2000m, VatPending: true, 0m, 0m);

    private static LodgingCheckoutDepositPlan Plan(IReadOnlyList<DocumentLineRequest> lines, LodgingDepositSnapshot dep, decimal discountBase)
    {
        var full = DocumentService.PreviewTotals(lines, true, discountBase);
        return LodgingDepositSettlement.PlanCheckout(new[] { dep }, full.Net, deducted => DocumentService.PreviewTotals(lines, true, discountBase, deducted).Total);
    }

    // ═══ ตัวตัดสิน ═══

    [Fact]
    public void ไม่กรอกหรือศูนย์_เท่ากับNone_แม้มีเหตุผล()
    {
        Assert.Equal((LodgingCheckoutDiscountPlan.None, (string?)null), LodgingCheckoutDiscount.Resolve(null, null, null, 6962.62m, true, 7m));
        Assert.Equal((LodgingCheckoutDiscountPlan.None, (string?)null), LodgingCheckoutDiscount.Resolve(0m, 0m, "เหตุผล", 6962.62m, true, 7m));
        Assert.False(LodgingCheckoutDiscountPlan.None.HasDiscount);
    }

    [Fact]
    public void บาทบนราคารวมVAT_แยกฐานด้วยอัตราที่พัก_และเก็บเหตุผล()
    {
        var (p, problem) = LodgingCheckoutDiscount.Resolve(500m, null, "  ชดเชยแอร์เสีย ", 6962.62m, true, 7m);
        Assert.Null(problem);
        Assert.True(p.HasDiscount);
        Assert.False(p.IsPercent);
        Assert.Equal(500m, p.Entered);
        Assert.Equal(467.29m, p.BaseAmount);       // 500 × 100/107
        Assert.Equal("ชดเชยแอร์เสีย", p.Reason);
    }

    [Fact]
    public void บาทบนราคาก่อนVAT_ฐานเท่าที่กรอก()
    {
        var (p, problem) = LodgingCheckoutDiscount.Resolve(500m, null, "ลูกค้าประจำ", 6962.62m, false, 7m);
        Assert.Null(problem);
        Assert.Equal(500m, p.BaseAmount);
    }

    [Fact]
    public void เปอร์เซ็นต์_คิดจากฐานเข้าพัก()
    {
        var (p, problem) = LodgingCheckoutDiscount.Resolve(null, 10m, "โปรโมชัน", 6962.62m, true, 7m);
        Assert.Null(problem);
        Assert.True(p.IsPercent);
        Assert.Equal(10m, p.Entered);
        Assert.Equal(696.26m, p.BaseAmount);
    }

    [Theory]
    [InlineData(-1.0, null, "เหตุผล")]         // ติดลบ
    [InlineData(null, -5.0, "เหตุผล")]
    [InlineData(100.0, 10.0, "เหตุผล")]        // ทั้งสองแบบ
    [InlineData(null, 101.0, "เหตุผล")]        // > 100%
    [InlineData(100.0, null, null)]            // ไม่มีเหตุผล
    [InlineData(100.0, null, "   ")]
    [InlineData(9000.0, null, "เหตุผล")]       // เกินยอด (ฐาน 8,411.21 > 6,962.62)
    public void ค่าที่ไม่รับ_ปฏิเสธพร้อมข้อความ_และไม่คืนแผนครึ่งๆ(double? amount, double? percent, string? reason)
    {
        var (p, problem) = LodgingCheckoutDiscount.Resolve((decimal?)amount, (decimal?)percent, reason, 6962.62m, true, 7m);
        Assert.NotNull(problem);
        Assert.Equal(LodgingCheckoutDiscountPlan.None, p);
    }

    [Fact]
    public void เหตุผลยาวเกิน_ถูกตัดที่เพดาน()
    {
        var (p, problem) = LodgingCheckoutDiscount.Resolve(100m, null, new string('ก', 300), 6962.62m, true, 7m);
        Assert.Null(problem);
        Assert.Equal(LodgingCheckoutDiscount.ReasonMaxLength, p.Reason!.Length);
    }

    // ═══ เดินผ่านตัวคำนวณจริง: 7,450 ลด 500 มัดจำ 2,000 ═══

    [Fact]
    public void ลด500_มัดจำVATทันที_ฐานใบลดทั้งส่วนลดและฐานมัดจำ_VATทั้งเรื่องเท่ากับVATของ6950()
    {
        var disc = LodgingCheckoutDiscount.Resolve(500m, null, "ชดเชย", DocumentService.PreviewTotals(Stay(7450m), true, 0m).Net, true, 7m).Plan;
        var plan = Plan(Stay(7450m), Taxed2000(), disc.BaseAmount);
        Assert.Equal(1869.16m, plan.BaseDeducted);      // มัดจำยังหักได้เต็ม (ฐานหลังลด 6,495.33 > 1,869.16)
        Assert.Empty(plan.Excess);

        var final = DocumentService.PreviewTotals(Stay(7450m), true, disc.BaseAmount, plan.BaseDeducted);
        Assert.Equal(467.29m, final.TradeDiscount);
        Assert.Equal(1869.16m, final.DepositBase);
        Assert.Equal(4626.17m, final.Net);              // 6,962.62 − 467.29 − 1,869.16
        Assert.Equal(323.83m, final.Vat);
        // VAT ทั้งเรื่อง = VAT มัดจำ 130.84 + VAT ใบ 323.83 = 454.67 ≈ VAT ของ (7,450 − 500) = 6,950 × 7/107 = 454.67
        Assert.Equal(454.67m, final.Vat + 130.84m);
        Assert.Equal(LodgingDepositSettlement.SplitInclusive(6950m, 7m).Vat, final.Vat + 130.84m);
    }

    [Fact]
    public void ลดจนต่ำกว่ามัดจำ_หักได้เท่าฐานหลังลด_ส่วนเกินค้างคืน_ไม่ติดลบ()
    {
        // เข้าพัก 2,140 (ฐาน 2,000) · ลด 535 (ฐาน 500) · มัดจำออกใบกำกับแล้ว 2,000 (ฐาน 1,869.16) ⇒ ฐานหลังลด 1,500 < 1,869.16
        var fullNet = DocumentService.PreviewTotals(Stay(2140m), true, 0m).Net;
        Assert.Equal(2000m, fullNet);
        var disc = LodgingCheckoutDiscount.Resolve(535m, null, "ชดเชยน้ำไม่ไหล", fullNet, true, 7m).Plan;
        Assert.Equal(500m, disc.BaseAmount);
        var plan = Plan(Stay(2140m), Taxed2000(), disc.BaseAmount);
        Assert.Equal(1500m, plan.BaseDeducted);          // หักได้ไม่เกินฐานหลังส่วนลด — ใบไม่ติดลบ
        var excess = Assert.Single(plan.Excess);
        Assert.True(excess.Gross > 0m);
        Assert.Equal(1605m, plan.GrossDeducted);                   // ฐานหลังลด 1,500 + VAT 105
        Assert.Equal(2000m, plan.GrossDeducted + excess.Gross, 1);  // ที่ใช้ + ที่ค้างคืน = มัดจำทั้งใบ (ต่างได้ ±0.01 จากสูตรปัดคืน C6)
        var final = DocumentService.PreviewTotals(Stay(2140m), true, disc.BaseAmount, plan.BaseDeducted);
        Assert.Equal(0m, final.Net);
        Assert.Equal(0m, final.Total);
        Assert.Contains("ค้างคืนแขก", LodgingCheckoutDiscount.NegativeNote(excess.Gross));
        Assert.Contains("ใบลดหนี้อ้างใบมัดจำ", LodgingCheckoutDiscount.NegativeNote(excess.Gross));
    }

    [Fact]
    public void ลด500_มัดจำเต็มยอด_ตัดชำระได้เท่ายอดใบหลังลด_ส่วนเกินค้างคืน()
    {
        // เข้าพัก 2,140 ลด 535 ⇒ ใบ 1,605 · มัดจำเต็มยอด 2,000 (ไม่มี VAT) ⇒ ตัดชำระ 1,605 ค้างคืน 395
        var fullNet = DocumentService.PreviewTotals(Stay(2140m), true, 0m).Net;
        var disc = LodgingCheckoutDiscount.Resolve(535m, null, "ชดเชย", fullNet, true, 7m).Plan;
        var plan = Plan(Stay(2140m), Full2000(), disc.BaseAmount);
        Assert.Empty(plan.Deduct);
        Assert.Equal(1605m, Assert.Single(plan.Apply).Gross);
        Assert.Equal(395m, plan.ExcessGross);
    }

    // ═══ ทิศตรงข้าม: ไม่ใส่ส่วนลด ⇒ ตัวเลขเดิมทุกสตางค์ ทุกประเภทมัดจำ ═══

    public static IEnumerable<object[]> DepositKinds()
    {
        yield return new object[] { Taxed2000() };
        yield return new object[] { Pending2000() };
        yield return new object[] { Full2000() };
    }

    [Theory]
    [MemberData(nameof(DepositKinds))]
    public void ไม่ใส่ส่วนลด_แผนมัดจำและยอดใบเท่าเส้นเดิมทุกสตางค์(LodgingDepositSnapshot dep)
    {
        var lines = Stay(7450m);
        var none = LodgingCheckoutDiscount.Resolve(null, null, null, DocumentService.PreviewTotals(lines, true, 0m).Net, true, 7m).Plan;
        Assert.Equal(0m, none.BaseAmount);

        // เส้นเดิม (ก่อน R-49): PreviewTotals(lines, incl, 0m) + PlanCheckout(full.Net, deducted => PreviewTotals(lines, incl, 0m, deducted).Total)
        var fullOld = DocumentService.PreviewTotals(lines, true, 0m);
        var planOld = LodgingDepositSettlement.PlanCheckout(new[] { dep }, fullOld.Net, d => DocumentService.PreviewTotals(lines, true, 0m, d).Total);
        var planNew = Plan(lines, dep, none.BaseAmount);

        Assert.Equal(planOld.BaseDeducted, planNew.BaseDeducted);
        Assert.Equal(planOld.VatDeducted, planNew.VatDeducted);
        Assert.Equal(planOld.GrossApplied, planNew.GrossApplied);
        Assert.Equal(planOld.ExcessGross, planNew.ExcessGross);
        Assert.Equal(planOld.DeductionRef, planNew.DeductionRef);
        var finalOld = DocumentService.PreviewTotals(lines, true, 0m, planOld.BaseDeducted);
        var finalNew = DocumentService.PreviewTotals(lines, true, none.BaseAmount, planNew.BaseDeducted);
        Assert.Equal(finalOld, finalNew);
        Assert.Equal(0m, finalNew.TradeDiscount);
    }
}
