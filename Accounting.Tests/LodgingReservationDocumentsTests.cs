using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 205 แพ็ก W5 (R-50) — ป้ายบทบาทของเอกสารทุกใบของการจอง: ตัดสินจากเอกสารที่ผูกจริงก่อนชนิด · ใบรับเงินใบที่ 2+ ได้ป้ายมัดจำ ·
/// ใบที่ยกเลิกยังอยู่ในรายการพร้อมป้าย (ไม่ซ่อน)</summary>
public class LodgingReservationDocumentsTests
{
    private static readonly DateTime D = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
    private static LodgingReservationDocumentRow Row(Guid id, DocumentType t, bool deposit = false, DepositNature? nature = null, DocumentStatus s = DocumentStatus.Approved)
        => new(id, "DOC-1", t, s, D, 1000m, deposit, nature);

    [Fact]
    public void เอกสารที่ผูกจริงชนะชนิด_ใบล่วงหน้า_เช็คเอาต์_เงินประกัน()
    {
        var adv = Guid.NewGuid(); var fin = Guid.NewGuid(); var sec = Guid.NewGuid();
        Assert.Equal("ใบกำกับภาษีล่วงหน้า", LodgingReservationDocuments.RoleLabel(Row(adv, DocumentType.TaxInvoice), adv, fin, sec));
        Assert.Equal("เอกสารเช็คเอาต์", LodgingReservationDocuments.RoleLabel(Row(fin, DocumentType.TaxInvoice), adv, fin, sec));
        Assert.Equal("ใบรับเงินประกันความเสียหาย", LodgingReservationDocuments.RoleLabel(Row(sec, DocumentType.Receipt, deposit: true), adv, fin, sec));
        // ลักษณะเงินประกันบนใบ (ไม่ได้ผูก) ก็เป็นเงินประกัน
        Assert.Equal("ใบรับเงินประกันความเสียหาย",
            LodgingReservationDocuments.RoleLabel(Row(Guid.NewGuid(), DocumentType.Receipt, deposit: true, nature: DepositNature.RefundableSecurity), null, null, null));
    }

    [Fact]
    public void ใบรับเงินมัดจำใบที่สอง_ได้ป้ายมัดจำพร้อมชนิด_ใบลดหนี้ได้ป้ายใบลดหนี้()
    {
        Assert.Equal("ใบรับเงินมัดจำ (ใบเสร็จรับเงิน)", LodgingReservationDocuments.RoleLabel(Row(Guid.NewGuid(), DocumentType.Receipt, deposit: true), null, null, null));
        Assert.Equal("ใบรับเงินมัดจำ (ใบกำกับภาษี)", LodgingReservationDocuments.RoleLabel(Row(Guid.NewGuid(), DocumentType.TaxInvoice, deposit: true), null, null, null));
        Assert.Equal("ใบลดหนี้", LodgingReservationDocuments.RoleLabel(Row(Guid.NewGuid(), DocumentType.CreditNote), null, null, null));
        Assert.Equal("ใบแจ้งหนี้", LodgingReservationDocuments.RoleLabel(Row(Guid.NewGuid(), DocumentType.Invoice), null, null, null));
    }

    [Theory]
    [InlineData(DocumentStatus.Voided, "ยกเลิกแล้ว")]
    [InlineData(DocumentStatus.Rejected, "ถูกตีกลับ")]
    [InlineData(DocumentStatus.Paid, "ชำระแล้ว")]
    [InlineData(DocumentStatus.Approved, "มีผล")]
    [InlineData(DocumentStatus.PartiallyPaid, "มีผล")]
    public void ป้ายสถานะ(DocumentStatus s, string label) => Assert.Equal(label, LodgingReservationDocuments.StatusLabel(s));
}
