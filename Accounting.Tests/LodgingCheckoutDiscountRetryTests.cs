using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 208 — แก้ตามฝ่ายค้าน W5 (ส่วนลดเช็คเอาต์ที่พัก) · ล็อกทั้ง "ใบที่พังกลับมาถูก" และ "ใบที่ถูกอยู่แล้วไม่ถูกแตะ" (ไม่มีส่วนลด = เดิมทุกสตางค์)
/// <list type="bullet">
/// <item>ข้อ 1 <see cref="LodgingCheckoutDiscount.ReconcileApplied"/>: กดซ้ำหลังใบลดหนี้ส่วนลด commit แล้ว — เว้นว่าง = ยกค่าเดิม (ไม่เรียกเก็บคืน) · ต่าง = ปฏิเสธ</item>
/// <item>ข้อ 2 <see cref="LodgingCheckoutDiscount.ResumeProblem"/>: ใบออกแล้ว คำขอส่งส่วนลดต่างจากใบ = ปฏิเสธ (ห้าม no-op เงียบ)</item>
/// <item>ข้อ 6 <see cref="LodgingCheckoutDiscount.SplitStayFirst"/>: ฐานส่วนลดบนใบล่วงหน้า = ค่าห้อง + folio เท่าเส้นปกติ (แบ่งค่าห้องก่อน)</item>
/// <item>ข้อ 7 <see cref="LodgingCheckoutDiscount.NeedsVatMixCheck"/> · ข้อ 9 <see cref="LodgingCheckoutDiscount.GuestNote"/> · ข้อ 10 <see cref="LodgingReservationDocuments.HiddenStatuses"/> ·
///   ข้อ 4 <see cref="DepositRefundCreditNoteBookingMigration"/></item>
/// </list>
/// </summary>
public class LodgingCheckoutDiscountRetryTests
{
    private static List<DocumentLineRequest> Stay(decimal gross) => new()
    {
        new(Description: "ค่าห้องพัก", Quantity: 1, Unit: "คืน", UnitPrice: gross, DiscountPercent: 0, VatRate: 7m, WithholdingTaxRate: 0, AccountId: null),
    };

    private static LodgingCheckoutDiscountPlan Discount(decimal amountInclVat, decimal stayNet)
    {
        var (plan, problem) = LodgingCheckoutDiscount.Resolve(amountInclVat, null, "ลูกค้าประจำ", stayNet, true, 7m);
        Assert.Null(problem);
        return plan;
    }

    // ═══ ข้อ 1 — กดซ้ำหลังใบลดหนี้ส่วนลด commit แล้ว ═══

    [Fact]
    public void ข้อ1_บั๊กเดิม_กดซ้ำไม่ส่งส่วนลด_ส่วนลดถูกเรียกเก็บคืนเป็นส่วนต่างค่าห้อง()
    {
        // ใบล่วงหน้า 7,450 (ราคารวม VAT) · ครั้งแรกลด 500 ⇒ ใบลดหนี้ 500 commit แล้ว ⇒ ยอดคงเหลือใบล่วงหน้า 6,950
        var lines = Stay(7450m);
        var stayBefore = DocumentService.PreviewTotals(lines, true, 0m);
        var first = Discount(500m, stayBefore.Net);
        var afterFirst = DocumentService.PreviewTotals(lines, true, first.BaseAmount);
        Assert.True(afterFirst.Total < stayBefore.Total - 499m);
        // กดซ้ำแบบเดิม (ไม่มีตัวตัดสินข้อ 1): คำขอไม่มีส่วนลด ⇒ ส่วนต่าง +500 = "ส่วนต่างค่าห้องพัก" เรียกเก็บคืน
        var raw = LodgingTaxInvoiceTimingPolicy.StayDelta(true, stayBefore.Net, stayBefore.Total, afterFirst.Net, afterFirst.Total);
        Assert.True(raw.IsCharge);
        Assert.True(raw.Gross > 499m);   // ≈ ส่วนลด 500 ถูกเรียกเก็บคืนทั้งก้อน

        // ตัวตัดสินข้อ 1: ยกส่วนลดเดิม ⇒ ส่วนต่าง 0 (ไม่เรียกเก็บคืน · ไม่ออกใบลดหนี้ใบที่สอง)
        var (reused, problem) = LodgingCheckoutDiscount.ReconcileApplied(LodgingCheckoutDiscountPlan.None, first.BaseAmount);
        Assert.Null(problem);
        Assert.True(reused.HasDiscount);
        Assert.Equal(first.BaseAmount, reused.BaseAmount);
        Assert.Equal(LodgingCheckoutDiscount.ReusedReason, reused.Reason);
        var now = DocumentService.PreviewTotals(lines, true, reused.BaseAmount);
        var delta = LodgingTaxInvoiceTimingPolicy.StayDelta(true, now.Net, now.Total, afterFirst.Net, afterFirst.Total);
        Assert.False(delta.IsCharge);
        Assert.False(delta.IsCredit);
    }

    [Fact]
    public void ข้อ1_กดซ้ำด้วยส่วนลดเท่าเดิม_ผ่าน_ใช้คำขอ()
    {
        var p = Discount(500m, 6962.62m);
        var (plan, problem) = LodgingCheckoutDiscount.ReconcileApplied(p, p.BaseAmount);
        Assert.Null(problem);
        Assert.Same(p, plan);
    }

    [Fact]
    public void ข้อ1_กดซ้ำด้วยส่วนลดต่าง_ปฏิเสธพร้อมทางไปต่อ_ไม่ออกใบลดหนี้ใบที่สอง()
    {
        var applied = Discount(500m, 6962.62m).BaseAmount;
        var (plan, problem) = LodgingCheckoutDiscount.ReconcileApplied(Discount(800m, 6962.62m), applied);
        Assert.False(plan.HasDiscount);
        Assert.NotNull(problem);
        Assert.Contains("ใบลดหนี้", problem);
        Assert.Contains(applied.ToString("N2"), problem);
    }

    [Fact]
    public void ข้อ1_ทิศตรงข้าม_ยังไม่เคยใช้ส่วนลด_คำขอผ่านตามเดิม()
    {
        var p = Discount(500m, 6962.62m);
        Assert.Same(p, LodgingCheckoutDiscount.ReconcileApplied(p, null).Plan);
        Assert.Same(LodgingCheckoutDiscountPlan.None, LodgingCheckoutDiscount.ReconcileApplied(LodgingCheckoutDiscountPlan.None, null).Plan);
        Assert.Same(LodgingCheckoutDiscountPlan.None, LodgingCheckoutDiscount.ReconcileApplied(LodgingCheckoutDiscountPlan.None, 0m).Plan);
    }

    // ═══ ข้อ 2 — ทำเช็คเอาต์ต่อเมื่อใบออกแล้ว ═══

    [Fact]
    public void ข้อ2_ใบออกพร้อมส่วนลด_คำขอส่วนลดต่าง_ปฏิเสธ_ห้ามnoopเงียบ()
    {
        var problem = LodgingCheckoutDiscount.ResumeProblem(800m, null, 467.29m, "TIV-0009", true, 7m, 6962.62m);
        Assert.NotNull(problem);
        Assert.Contains("ใบ TIV-0009 ออกแล้วพร้อมส่วนลด 467.29", problem);
        Assert.Contains("ใบลดหนี้", problem);
        // ใบออกโดยไม่มีส่วนลด แต่กดซ้ำพร้อมส่วนลด = ปฏิเสธเช่นกัน
        Assert.NotNull(LodgingCheckoutDiscount.ResumeProblem(500m, null, 0m, "TIV-0009", true, 7m, 6962.62m));
    }

    [Fact]
    public void ข้อ2_ทิศตรงข้าม_เว้นว่างหรือเท่าเดิม_ทำต่อได้()
    {
        Assert.Null(LodgingCheckoutDiscount.ResumeProblem(null, null, 467.29m, "TIV-0009", true, 7m, 6962.62m));
        Assert.Null(LodgingCheckoutDiscount.ResumeProblem(0m, 0m, 467.29m, "TIV-0009", true, 7m, 6962.62m));
        Assert.Null(LodgingCheckoutDiscount.ResumeProblem(500m, null, 467.29m, "TIV-0009", true, 7m, 6962.62m));   // 500 รวม VAT = ฐาน 467.29
        Assert.Null(LodgingCheckoutDiscount.ResumeProblem(null, null, 0m, "TIV-0009", true, 7m, 6962.62m));        // ไม่มีส่วนลดทั้งสองฝั่ง
    }

    [Fact]
    public void ข้อ2_เปอร์เซ็นต์_เทียบจากฐานก่อนส่วนลดของใบ_ไม่รู้ฐาน_ปฏิเสธพร้อมทางไปต่อ()
    {
        // 10% ของ 1,000 = 100 ตรงกับที่ออก
        Assert.Null(LodgingCheckoutDiscount.ResumeProblem(null, 10m, 100m, "INV-1", false, 0m, 1000m));
        Assert.NotNull(LodgingCheckoutDiscount.ResumeProblem(null, 20m, 100m, "INV-1", false, 0m, 1000m));
        Assert.NotNull(LodgingCheckoutDiscount.ResumeProblem(null, 10m, 100m, "INV-1", false, 0m, null));
    }

    // ═══ ข้อ 6 — ฐานส่วนลดบนใบล่วงหน้า = ค่าห้อง + folio ═══

    [Fact]
    public void ข้อ6_ส่วนลดลงค่าห้องก่อน_ส่วนที่เกินลงใบส่วนเพิ่ม_รวมเท่าเส้นปกติ()
    {
        var (stay, folio) = LodgingCheckoutDiscount.SplitStayFirst(1200m, 1000m, 500m);
        Assert.Equal(1000m, stay);
        Assert.Equal(200m, folio);
        Assert.Equal(1200m, stay + folio);
        // ไม่เกินค่าห้อง ⇒ ลงค่าห้องทั้งหมด (ตัวเลขเดิมของรอบ 205)
        Assert.Equal((300m, 0m), LodgingCheckoutDiscount.SplitStayFirst(300m, 1000m, 500m));
        // ไม่มีส่วนลด = (0, 0) ⇒ ใบส่วนเพิ่มไม่มี BillDiscountAmount
        Assert.Equal((0m, 0m), LodgingCheckoutDiscount.SplitStayFirst(0m, 1000m, 500m));
    }

    [Fact]
    public void ข้อ6_ส่วนลดเกินค่าห้องแต่ไม่เกินค่าห้องบวกfolio_ตัวตัดสินรับ_เดิมปฏิเสธ()
    {
        // ฐานเดียวกับเส้นปกติ (ค่าห้อง + folio) — เดิมใบล่วงหน้าใช้แค่ค่าห้อง ⇒ ปฏิเสธส่วนลดที่เส้นปกติรับ
        var onlyStay = LodgingCheckoutDiscount.Resolve(1200m, null, "ชดเชย", 1000m, false, 7m);
        Assert.NotNull(onlyStay.Problem);
        var stayPlusFolio = LodgingCheckoutDiscount.Resolve(1200m, null, "ชดเชย", 1000m + 500m, false, 7m);
        Assert.Null(stayPlusFolio.Problem);
        Assert.Equal(1200m, stayPlusFolio.Plan.BaseAmount);
    }

    [Fact]
    public void ข้อ6_กดซ้ำหลังfolioเปลี่ยน_ส่วนค่าห้องคงที่_ไม่เกิดส่วนต่างค่าห้องใหม่()
    {
        var a = LodgingCheckoutDiscount.SplitStayFirst(800m, 1000m, 500m);
        var b = LodgingCheckoutDiscount.SplitStayFirst(800m, 1000m, 900m);
        Assert.Equal(a.Stay, b.Stay);
    }

    // ═══ ข้อ 7 / 9 ═══

    [Fact]
    public void ข้อ7_ตรวจVATอัตราผสมเมื่อมีส่วนลดหรือหักมัดจำ_ไม่มีทั้งสอง_ไม่ตรวจ()
    {
        var d = Discount(500m, 6962.62m);
        Assert.True(LodgingCheckoutDiscount.NeedsVatMixCheck(0m, d));
        Assert.True(LodgingCheckoutDiscount.NeedsVatMixCheck(1869.16m, LodgingCheckoutDiscountPlan.None));
        Assert.False(LodgingCheckoutDiscount.NeedsVatMixCheck(0m, LodgingCheckoutDiscountPlan.None));
    }

    [Fact]
    public void ข้อ7_ใบอัตราผสม_ส่วนลดถูกเฉลี่ยลงบรรทัด0เปอร์เซ็นต์_VATต่ำกว่าที่ควร_ตาข่ายเห็น()
    {
        var lines = new List<DocumentLineRequest>
        {
            new(Description: "ค่าห้อง", Quantity: 1, Unit: "คืน", UnitPrice: 1000m, DiscountPercent: 0, VatRate: 7m, WithholdingTaxRate: 0, AccountId: null),
            new(Description: "บริการยกเว้น", Quantity: 1, Unit: "รายการ", UnitPrice: 1000m, DiscountPercent: 0, VatRate: 0m, WithholdingTaxRate: 0, AccountId: null),
        };
        var t = DocumentService.PreviewTotals(lines, false, 200m);
        var expected = LodgingDepositSettlement.FinalInvoiceVat(t.Net, 0m, 7m);
        Assert.True(Math.Abs(expected - t.Vat) > 0.05m);
    }

    [Fact]
    public void ข้อ9_ข้อความบนเอกสารของแขกไม่มีเหตุผลภายใน_หมายเหตุภายในยังมี()
    {
        var (p, _) = LodgingCheckoutDiscount.Resolve(500m, null, "แขกบ่นเรื่องแอร์เสีย", 6962.62m, true, 7m);
        var guest = LodgingCheckoutDiscount.GuestNote(p.BaseAmount);
        Assert.DoesNotContain("แอร์", guest);
        Assert.Contains("467.29", guest);
        Assert.Contains("แอร์", LodgingCheckoutDiscount.Note(p));
        Assert.DoesNotContain("แอร์", LodgingCheckoutDiscount.OffModeChargeDescription);
    }

    // ═══ ข้อ 10 — การ์ดเอกสารของการจอง ═══

    [Fact]
    public void ข้อ10_ซ่อนร่างและรออนุมัติ_ใบถูกตีกลับยังแสดง()
    {
        Assert.Contains(DocumentStatus.Draft, LodgingReservationDocuments.HiddenStatuses);
        Assert.Contains(DocumentStatus.WaitingApproval, LodgingReservationDocuments.HiddenStatuses);
        Assert.DoesNotContain(DocumentStatus.Rejected, LodgingReservationDocuments.HiddenStatuses);
        Assert.DoesNotContain(DocumentStatus.Voided, LodgingReservationDocuments.HiddenStatuses);
        Assert.DoesNotContain(DocumentStatus.Approved, LodgingReservationDocuments.HiddenStatuses);
    }

    // ═══ ข้อ 4 — ใบลดหนี้คืนมัดจำสืบทอดเลขจอง (migration ของใบเก่า) ═══

    [Fact]
    public void ข้อ4_backfill_เติมเฉพาะใบลดหนี้ที่อ้างใบมัดจำบริษัทเดียวกันและยังว่าง()
    {
        Assert.Equal((int)DocumentType.CreditNote, DepositRefundCreditNoteBookingMigration.CreditNoteTypeValue);
        var sql = DepositRefundCreditNoteBookingMigration.BackfillSql;
        Assert.Contains("cn.\"CompanyId\" = d.\"CompanyId\"", sql);
        Assert.Contains("cn.\"BookingNumber\" IS NULL", sql);
        Assert.Contains("d.\"IsDeposit\" = true", sql);
        Assert.Contains("cn.\"DocumentType\" = 6", sql);
        Assert.Single(DepositRefundCreditNoteBookingMigration.Statements());
    }
}
