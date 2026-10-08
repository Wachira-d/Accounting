using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คำตัดสินข้อ 139 (2026-10-08): ผูกใบแจ้งหนี้ที่สร้างแยกเข้ากับใบเสนอราคาภายหลัง — ด่านใบลูก/ใบต้นทาง + ตัวเสนอการจับคู่บรรทัด
/// (เคสจริง: ใบแจ้งหนี้ใบที่ 2 ที่ผู้ใช้สร้างเองเพราะแปลงไม่ได้)
/// </summary>
public class DocumentLinkPolicyTests
{
    private static DocumentLinkPolicy.ChildFacts Child(DocumentType t = DocumentType.Invoice,
        DocumentStatus s = DocumentStatus.Approved, bool parent = false, bool deposit = false,
        decimal depDeducted = 0m, bool replacement = false, bool lineSource = false)
        => new(t, s, parent, deposit, depDeducted, replacement, lineSource);

    [Fact]
    public void ใบแจ้งหนี้อนุมัติแล้วที่ยังไม่มีต้นทาง_ผูกได้()
    {
        Assert.Null(DocumentLinkPolicy.ChildBlockReason(Child()));
        Assert.Null(DocumentLinkPolicy.ChildBlockReason(Child(DocumentType.TaxInvoice, DocumentStatus.Paid)));
        Assert.Null(DocumentLinkPolicy.SourceBlockReason(DocumentType.Quotation, DocumentStatus.Approved, true, true));
    }

    [Theory]
    [InlineData(DocumentType.Receipt)]          // หลายรายงานตีความใบรับเงินที่ไม่มีต้นทาง = ขายสด — ผูกภายหลังเปลี่ยนยอดที่รายงานไปแล้ว
    [InlineData(DocumentType.CreditNote)]
    [InlineData(DocumentType.Quotation)]
    public void ทิศตรงข้าม_ชนิดใบลูกนอกขอบเขต_ถูกกัน(DocumentType t)
        => Assert.NotNull(DocumentLinkPolicy.ChildBlockReason(Child(t)));

    [Fact]
    public void ทิศตรงข้าม_ใบที่มีต้นทางแล้ว_ยกเลิกแล้ว_มัดจำ_ใบแทน_ถูกกัน()
    {
        Assert.NotNull(DocumentLinkPolicy.ChildBlockReason(Child(parent: true)));
        Assert.NotNull(DocumentLinkPolicy.ChildBlockReason(Child(lineSource: true)));
        Assert.NotNull(DocumentLinkPolicy.ChildBlockReason(Child(s: DocumentStatus.Voided)));
        Assert.NotNull(DocumentLinkPolicy.ChildBlockReason(Child(deposit: true)));
        Assert.NotNull(DocumentLinkPolicy.ChildBlockReason(Child(depDeducted: 100m)));
        Assert.NotNull(DocumentLinkPolicy.ChildBlockReason(Child(replacement: true)));
    }

    [Fact]
    public void ทิศตรงข้าม_ใบต้นทางผิดเงื่อนไข_ถูกกัน()
    {
        Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.Invoice, DocumentStatus.Approved, true, true));
        Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.Quotation, DocumentStatus.Voided, true, true));
        Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.Quotation, DocumentStatus.Approved, false, true));
        Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.Quotation, DocumentStatus.Approved, true, false));
    }

    [Fact]
    public void จับคู่บรรทัด_รหัสสินค้าก่อน_แล้วคำอธิบาย_แล้วลำดับ()
    {
        Guid s1 = Guid.NewGuid(), s2 = Guid.NewGuid(), c1 = Guid.NewGuid(), c2 = Guid.NewGuid();
        var src = new[] { new DocumentLinkPolicy.LineKey(s1, 1, "P-01", "สว่าน"), new DocumentLinkPolicy.LineKey(s2, 2, null, "ค่าติดตั้ง  งาน") };
        var child = new[] { new DocumentLinkPolicy.LineKey(c1, 1, null, "ค่าติดตั้ง งาน"), new DocumentLinkPolicy.LineKey(c2, 2, "p-01", "อื่น") };
        var map = DocumentLinkPolicy.SuggestLineMap(child, src).ToDictionary(x => x.ChildLineId, x => x.SourceLineId);
        Assert.Equal(s2, map[c1]);   // คำอธิบายตรง (ไม่สนช่องว่างซ้อน)
        Assert.Equal(s1, map[c2]);   // รหัสสินค้าตรง (ไม่สนตัวพิมพ์)
    }

    [Fact]
    public void จับคู่บรรทัด_จับไม่ได้และจำนวนบรรทัดไม่เท่า_ได้null_ไม่เดา()
    {
        var src = new[] { new DocumentLinkPolicy.LineKey(Guid.NewGuid(), 1, null, "A"), new DocumentLinkPolicy.LineKey(Guid.NewGuid(), 2, null, "B") };
        var child = new[] { new DocumentLinkPolicy.LineKey(Guid.NewGuid(), 1, null, "Z") };
        Assert.Null(DocumentLinkPolicy.SuggestLineMap(child, src)[0].SourceLineId);
    }

    [Fact]
    public void จับคู่บรรทัด_เคสผู้ใช้_หนึ่งบรรทัดต่อหนึ่งบรรทัด_ใช้ลำดับ()
    {
        Guid s = Guid.NewGuid(), c = Guid.NewGuid();
        var map = DocumentLinkPolicy.SuggestLineMap(
            new[] { new DocumentLinkPolicy.LineKey(c, 1, null, "งวดที่ 2") },
            new[] { new DocumentLinkPolicy.LineKey(s, 1, null, "Test") });
        Assert.Equal(s, map[0].SourceLineId);
    }
}
