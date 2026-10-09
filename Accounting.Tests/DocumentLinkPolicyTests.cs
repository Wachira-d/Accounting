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
        decimal depDeducted = 0m, bool replacement = false, bool lineSource = false, bool parentIsPo = false)
        => new(t, s, parent, deposit, depDeducted, replacement, lineSource, parentIsPo);

    [Fact]
    public void ใบแจ้งหนี้อนุมัติแล้วที่ยังไม่มีต้นทาง_ผูกได้()
    {
        Assert.Null(DocumentLinkPolicy.ChildBlockReason(Child()));
        Assert.Null(DocumentLinkPolicy.ChildBlockReason(Child(DocumentType.TaxInvoice, DocumentStatus.Paid)));
        Assert.Null(DocumentLinkPolicy.SourceBlockReason(DocumentType.Invoice, DocumentType.Quotation, DocumentStatus.Approved, true, true));
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

    // ── รุ่นสอง (คำตัดสินเจ้าของ 2026-10-08 "Add DN + GRN") ──────────────────────────────────────────────

    [Fact]
    public void ใบแจ้งหนี้ผูกใบส่งของได้_แม้อนุมัติแล้ว_ใบส่งของไม่ขยับสต็อกไม่ลงบัญชี()
    {
        Assert.Null(DocumentLinkPolicy.SourceBlockReason(DocumentType.Invoice, DocumentType.DeliveryNote, DocumentStatus.Approved, true, true));
        Assert.Null(DocumentLinkPolicy.SourceBlockReason(DocumentType.TaxInvoice, DocumentType.DeliveryNote, DocumentStatus.Draft, true, true));
        Assert.Null(DocumentLinkPolicy.ChildBlockReason(Child(DocumentType.Invoice, DocumentStatus.Paid)));
    }

    [Fact]
    public void ใบแจ้งหนี้ซื้อฉบับร่าง_ผูกใบรับสินค้าที่อนุมัติแล้วได้()
    {
        Assert.Null(DocumentLinkPolicy.ChildBlockReason(Child(DocumentType.PurchaseInvoice, DocumentStatus.Draft)));
        Assert.Null(DocumentLinkPolicy.SourceBlockReason(DocumentType.PurchaseInvoice, DocumentType.GoodsReceiptNote, DocumentStatus.Approved, true, true));
    }

    [Fact]
    public void ทางซ่อม_ใบแจ้งหนี้ซื้อร่างที่ผูกใบสั่งซื้อ_ย้ายไปใบรับสินค้าของใบสั่งซื้อเดียวกันได้_ใบอื่นไม่ได้()
    {
        // ผูก PO อยู่ (ทั้งหัวและบรรทัด) — ทางซ่อมของใบจากสแกน/API ที่ด่าน PI-PO-HAS-GRN กัน
        Assert.Null(DocumentLinkPolicy.ChildBlockReason(Child(DocumentType.PurchaseInvoice, DocumentStatus.Draft, parent: true, lineSource: true, parentIsPo: true)));
        Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.PurchaseInvoice, DocumentType.GoodsReceiptNote, DocumentStatus.Approved, true, true,
            childPoMismatch: true));
        // ทิศตรงข้าม: ผูกต้นทางอื่น (เช่น ใบรับสินค้าอยู่แล้ว) ⇒ ต้องยกเลิกการผูกเดิมก่อน · ใบขายที่ผูก "PO" ไม่ได้รับทางซ่อมนี้
        Assert.NotNull(DocumentLinkPolicy.ChildBlockReason(Child(DocumentType.PurchaseInvoice, DocumentStatus.Draft, parent: true)));
        Assert.NotNull(DocumentLinkPolicy.ChildBlockReason(Child(DocumentType.Invoice, DocumentStatus.Draft, parent: true, parentIsPo: true)));
    }

    [Theory]
    [InlineData(DocumentStatus.WaitingApproval)]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Paid)]
    public void ทิศตรงข้าม_ใบแจ้งหนี้ซื้อที่ไม่ใช่ร่าง_ผูกใบรับสินค้าไม่ได้และถอดไม่ได้(DocumentStatus st)
    {
        // การผูกใบรับสินค้ากำหนดการลงบัญชีตอนอนุมัติ (ล้าง 21240) — ใบที่ลงไปแล้วผูกภายหลังได้แต่ความคืบหน้าที่โกหก
        Assert.NotNull(DocumentLinkPolicy.ChildBlockReason(Child(DocumentType.PurchaseInvoice, st)));
        Assert.NotNull(DocumentLinkPolicy.UnlinkBlockReason(DocumentType.PurchaseInvoice, st));
    }

    [Fact]
    public void ถอดการผูก_ใบแจ้งหนี้ซื้อร่างและใบขายทุกสถานะ_ได้()
    {
        Assert.Null(DocumentLinkPolicy.UnlinkBlockReason(DocumentType.PurchaseInvoice, DocumentStatus.Draft));
        Assert.Null(DocumentLinkPolicy.UnlinkBlockReason(DocumentType.Invoice, DocumentStatus.Paid));
    }

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.WaitingApproval)]
    public void ทิศตรงข้าม_ใบรับสินค้ายังไม่อนุมัติ_ผูกไม่ได้(DocumentStatus st)
        // ยังไม่ตั้ง 21240 ⇒ ใบแจ้งหนี้ซื้อจะรับสต็อกเอง แล้วใบรับสินค้าลงซ้ำตอนอนุมัติ
        => Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.PurchaseInvoice, DocumentType.GoodsReceiptNote, st, true, true));

    [Fact]
    public void ทิศตรงข้าม_คู่ชนิดข้ามฝั่ง_ถูกกัน()
    {
        Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.Invoice, DocumentType.GoodsReceiptNote, DocumentStatus.Approved, true, true));
        Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.PurchaseInvoice, DocumentType.Quotation, DocumentStatus.Approved, true, true));
        Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.PurchaseInvoice, DocumentType.PurchaseOrder, DocumentStatus.Approved, true, true));
        Assert.NotNull(DocumentLinkPolicy.ChildBlockReason(Child(DocumentType.Expense, DocumentStatus.Draft)));
        Assert.True(DocumentLinkPolicy.IsLinkSource(DocumentType.DeliveryNote));
        Assert.False(DocumentLinkPolicy.IsLinkSource(DocumentType.PurchaseOrder));
    }

    [Fact]
    public void ทิศตรงข้าม_ใบต้นทางผิดเงื่อนไข_ถูกกัน()
    {
        Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.Invoice, DocumentType.Invoice, DocumentStatus.Approved, true, true));
        Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.Invoice, DocumentType.Quotation, DocumentStatus.Voided, true, true));
        Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.Invoice, DocumentType.Quotation, DocumentStatus.Approved, false, true));
        Assert.NotNull(DocumentLinkPolicy.SourceBlockReason(DocumentType.Invoice, DocumentType.Quotation, DocumentStatus.Approved, true, false));
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
