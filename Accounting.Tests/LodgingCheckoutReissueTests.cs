using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม IN — A-IN5 (คำตัดสินข้อ 36): ใบเช็คเอาต์ถูกยกเลิก ⇒ ออกใบใหม่อ้างใบเดิมผ่านตัวสร้างของที่พัก · ยอดต่าง = ใบลด/เพิ่มหนี้
///
/// <para>สองทิศ: เคสที่เคยไม่มีทางไป (เช็คเอาต์แล้ว + ใบถูกยกเลิก) ออกได้เมื่อยอด/ชนิดเท่าเดิม · ใบที่ยังไม่ยกเลิก/ยอดต่าง/ชนิดต่าง/
/// ที่พักปิดบัญชี/ยังเช็คอินอยู่ ห้ามออก (ใบกำกับซ้อน = ภาษีขายสองรอบ)</para>
/// </summary>
public class LodgingCheckoutReissueTests
{
    private static string? P(LodgingReservationStatus st = LodgingReservationStatus.CheckedOut,
        DocumentStatus? fin = DocumentStatus.Voided, bool off = false,
        DocumentType oldType = DocumentType.TaxInvoice, decimal oldTotal = 5_350m,
        DocumentType newType = DocumentType.TaxInvoice, decimal newTotal = 5_350m)
        => LodgingCheckoutReissue.Problem(st, fin, off, oldType, oldTotal, newType, newTotal, "TIV-20261001-0001");

    [Fact]
    public void เช็คเอาต์แล้ว_ใบถูกยกเลิก_ยอดเท่าเดิม_ออกได้()
    {
        Assert.Null(P());
        Assert.True(LodgingCheckoutReissue.CanOffer(LodgingReservationStatus.CheckedOut, DocumentStatus.Voided, false));
    }

    [Fact]
    public void ยอดต่างกันแม้สตางค์เดียว_ปฏิเสธพร้อมทางใบลดเพิ่มหนี้()
    {
        var why = P(newTotal: 5_350.01m);
        Assert.NotNull(why);
        Assert.Contains("ใบลด/เพิ่มหนี้", why);
    }

    [Fact]
    public void ยอดต่างกันเฉพาะเศษปัดเกินสองตำแหน่ง_ถือว่าเท่า()
        => Assert.Null(P(newTotal: 5_350.004m));

    [Fact]
    public void ใบเดิมยังไม่ยกเลิก_ห้ามออกซ้อน()
    {
        Assert.Contains("ใบเดียว", P(fin: DocumentStatus.Approved));
        Assert.False(LodgingCheckoutReissue.CanOffer(LodgingReservationStatus.CheckedOut, DocumentStatus.Approved, false));
    }

    [Fact]
    public void ยังเช็คอินอยู่_ใช้เส้นเดิม_ไม่เปิดปุ่ม()
    {
        Assert.NotNull(P(st: LodgingReservationStatus.CheckedIn));
        Assert.False(LodgingCheckoutReissue.CanOffer(LodgingReservationStatus.CheckedIn, DocumentStatus.Voided, false));
    }

    [Fact]
    public void ที่พักโหมดไม่ออกเอกสาร_ไม่เปิดปุ่ม()
    {
        Assert.NotNull(P(off: true));
        Assert.False(LodgingCheckoutReissue.CanOffer(LodgingReservationStatus.CheckedOut, DocumentStatus.Voided, true));
    }

    [Fact]
    public void ชนิดเอกสารเปลี่ยนเพราะค่าตั้งVATเปลี่ยน_ปฏิเสธ()
        => Assert.Contains("ชนิดเอกสาร", P(newType: DocumentType.Invoice));

    [Fact]
    public void หมายเหตุอ้างเลขใบเดิม()
        => Assert.Contains("TIV-20261001-0001", LodgingCheckoutReissue.ReferenceNote("TIV-20261001-0001"));

    // ── ฝ่ายค้าน X2: ผู้ใช้ออกใบแทนเองที่หน้าเอกสารแล้ว (ข้อความก่อนรอบ 201 สั่งให้ทำ) ──────────

    [Fact]
    public void X2_มีใบขายที่ยังมีผลอ้างเลขจองอยู่แล้ว_ปฏิเสธ_และไม่เปิดปุ่ม()
    {
        var why = LodgingCheckoutReissue.Problem(LodgingReservationStatus.CheckedOut, DocumentStatus.Voided, false,
            DocumentType.TaxInvoice, 5_350m, DocumentType.TaxInvoice, 5_350m, "TIV-20261001-0001",
            liveReplacementNumber: "TIV-20261005-0003", confirmedNoManualReissue: true);
        Assert.NotNull(why);
        Assert.Contains("TIV-20261005-0003", why);
        Assert.False(LodgingCheckoutReissue.CanOffer(LodgingReservationStatus.CheckedOut, DocumentStatus.Voided, false, "TIV-20261005-0003"));
    }

    [Fact]
    public void X2_ไม่ติ๊กยืนยันว่ายังไม่ได้ออกใบแทนเอง_ปฏิเสธ()
        => Assert.Contains("ติ๊กยืนยัน", LodgingCheckoutReissue.Problem(LodgingReservationStatus.CheckedOut, DocumentStatus.Voided, false,
            DocumentType.TaxInvoice, 5_350m, DocumentType.TaxInvoice, 5_350m, "TIV-20261001-0001",
            liveReplacementNumber: null, confirmedNoManualReissue: false));

    [Fact]
    public void X2_ไม่มีใบแทน_ติ๊กยืนยันแล้ว_ออกได้_และเปิดปุ่ม()
    {
        Assert.Null(LodgingCheckoutReissue.Problem(LodgingReservationStatus.CheckedOut, DocumentStatus.Voided, false,
            DocumentType.TaxInvoice, 5_350m, DocumentType.TaxInvoice, 5_350m, "TIV-20261001-0001",
            liveReplacementNumber: null, confirmedNoManualReissue: true));
        Assert.True(LodgingCheckoutReissue.CanOffer(LodgingReservationStatus.CheckedOut, DocumentStatus.Voided, false, null));
    }

    [Fact]
    public void X2_ใบรับชำระและใบมัดจำ_ไม่อยู่ในชนิดที่นับเป็นใบแทน()
    {
        Assert.DoesNotContain(DocumentType.CreditNote, LodgingCheckoutReissue.SaleTypes);
        Assert.DoesNotContain(DocumentType.ReceiptVoucher, LodgingCheckoutReissue.SaleTypes);
        Assert.Contains(DocumentType.TaxInvoice, LodgingCheckoutReissue.SaleTypes);
    }
}
