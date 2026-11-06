using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 242 · คำตัดสินข้อ 242-B (เจ้าของ 2026-11-06 — ภาพหน้าจอหน้ารวมใบสั่งซื้อ): PO-20260930-0002 → PI-20261001-0005 ยอด 3,584.50 เท่ากัน ·
/// PO-20261002-0001 → PI-20261002-0003 · PO-20261007-0001 → PI-20261007-0004 ขึ้น "◐ … · ไม่ทราบสัดส่วน" ทั้งที่ยอดเท่ากันเป๊ะ —
/// บรรทัดของ PI ไม่ได้ชี้บรรทัดของ PO (จับคู่แน่นอนตามกติกา <see cref="SourceLineBackfill"/> ไม่ได้ — คำอธิบาย/รหัสสินค้าบนใบลูกไม่ตรงใบต้นทาง) ⇒
/// กติกาใหม่ในตัวตัดสินเดียว <see cref="DocumentConversionProgress.SoleChildMatchesTotal"/>: ใบลูกที่ผูกหัวใบใบเดียว + ยอดฐานเท่ากัน ⇒ ครบ (บอกที่มาบนป้าย) ·
/// ทิศตรงข้าม: ใบลูกหลายใบ · ยอดไม่เท่า · ใบมัดจำ · ใบขอซื้อ (วัดด้วยจำนวน) ⇒ ยังไม่ทราบสัดส่วน · มีแถวจับคู่รายบรรทัด ⇒ % ตามแถวเดิม
/// </summary>
public class DocumentConversionHeaderTotalRound242Tests
{
    private static Document Po(string no, decimal total) => new()
    {
        DocumentType = DocumentType.PurchaseOrder, DocumentNumber = no,
        Status = DocumentStatus.Approved, TotalAmount = total, BalanceDue = total,
    };

    private static DocumentService.ConversionSummary Summary(DocumentConversionProgress.Progress p, string childNo, int count = 1)
        => new(p, new DocumentBrief(Guid.NewGuid(), childNo, DocumentType.PurchaseInvoice, DocumentStatus.Approved,
            new DateTime(2026, 10, 1), 3_584.50m), count);

    private static readonly (DocumentType, decimal, decimal)[] NoRows = Array.Empty<(DocumentType, decimal, decimal)>();

    private static DocumentConversionProgress.HeaderLinkedChild Pi(decimal amount, bool deposit = false)
        => new(DocumentType.PurchaseInvoice, amount, deposit);

    [Theory]
    [InlineData("PO-20260930-0002", "PI-20261001-0005", 3_350.00)]
    [InlineData("PO-20261002-0001", "PI-20261002-0003", 1_250.00)]
    [InlineData("PO-20261007-0001", "PI-20261007-0004", 12_164.72)]
    public void ภาพหน้าจอเจ้าของ_ยอดเท่ากัน_ใบลูกใบเดียว_บรรทัดไม่จับคู่_ขึ้นครบ(string po, string pi, double baseAmount)
    {
        var amount = (decimal)baseAmount;
        var p = DocumentConversionProgress.EvaluateByValue(3m, amount, NoRows, hasActiveLinkedChild: true, new[] { Pi(amount) });
        Assert.Equal(ConversionProgressState.Full, p.State);
        Assert.Equal(100m, p.Percent);
        Assert.True(p.ByHeaderTotal);
        var (status, reason) = DocumentService.ComputeLifecycle(Po(po, amount * 1.07m), Summary(p, pi));
        Assert.Equal("Done", status);
        Assert.Equal($"✓ ออกใบแจ้งหนี้ซื้อ {pi} แล้ว · " + DocumentConversionProgress.HeaderTotalText, reason);
        Assert.DoesNotContain("ไม่ทราบสัดส่วน", reason);
    }

    [Fact]
    public void เศษปัดไม่เกินครึ่งสตางค์_ยังถือว่าเท่ากัน_เกินกว่านั้นไม่ใช่()
    {
        Assert.True(DocumentConversionProgress.SoleChildMatchesTotal(3_350m, 0, new[] { Pi(3_350.004m) }));
        Assert.False(DocumentConversionProgress.SoleChildMatchesTotal(3_350m, 0, new[] { Pi(3_350.01m) }));
    }

    [Fact]
    public void ทิศตรงข้าม_ใบลูกสองใบผูกหัว_ยังไม่ทราบสัดส่วน()
    {
        // แบ่งวางบิลสองใบ (ยอดใบหนึ่งบังเอิญเท่าใบต้นทาง) — ไม่รู้ว่าใบไหนแบ่งยอดเท่าไร ⇒ ห้ามแต่ง "ครบ"
        var p = DocumentConversionProgress.EvaluateByValue(3m, 3_350m, NoRows, true, new[] { Pi(3_350m), Pi(1_000m) });
        Assert.Equal(ConversionProgressState.Partial, p.State);
        Assert.Null(p.Percent);
        Assert.False(p.ByHeaderTotal);
        var (_, reason) = DocumentService.ComputeLifecycle(Po("PO-X", 3_584.50m), Summary(p, "PI-X", 2));
        Assert.Contains(DocumentConversionProgress.UnknownShareText, reason);
    }

    [Fact]
    public void ทิศตรงข้าม_ยอดไม่เท่ากัน_ยังไม่ทราบสัดส่วน()
    {
        var p = DocumentConversionProgress.EvaluateByValue(3m, 3_350m, NoRows, true, new[] { Pi(2_000m) });
        Assert.Equal(ConversionProgressState.Partial, p.State);
        Assert.Null(p.Percent);
    }

    [Fact]
    public void ทิศตรงข้าม_ใบมัดจำยอดเต็ม_ไม่ใช่การเติมเต็มรายการ()
    {
        var p = DocumentConversionProgress.EvaluateByValue(3m, 3_350m, NoRows, true, new[] { Pi(3_350m, deposit: true) });
        Assert.Equal(ConversionProgressState.Partial, p.State);
        Assert.False(p.ByHeaderTotal);
    }

    [Fact]
    public void ทิศตรงข้าม_ใบขอซื้อวัดด้วยจำนวน_ผู้เรียกส่งยอด0_กติกายอดรวมไม่ทำงาน()
    {
        Assert.False(DocumentConversionProgress.SoleChildMatchesTotal(0m, 0, new[] { Pi(0m) }));
        var p = DocumentConversionProgress.EvaluateByValue(10m, 0m, NoRows, true,
            new[] { new DocumentConversionProgress.HeaderLinkedChild(DocumentType.PurchaseOrder, 8_000m, false) });
        Assert.Equal(ConversionProgressState.Partial, p.State);
        Assert.Null(p.Percent);
    }

    [Fact]
    public void ใบที่จับคู่รายบรรทัดได้บางส่วน_ยังแสดงเปอร์เซ็นต์ตามแถว_ไม่ถูกยอดหัวใบทับ()
    {
        // วางบิลบางส่วน 40% ผ่านบรรทัดที่จับคู่จริง — แม้ยอดหัวใบลูกจะเท่าใบต้นทาง (เช่น ผู้ใช้เพิ่มบรรทัดอื่นเอง) แถวจับคู่ชนะ
        var p = DocumentConversionProgress.EvaluateByValue(3m, 10_000m,
            new[] { (DocumentType.PurchaseInvoice, 1m, 4_000m) }, true, new[] { Pi(10_000m) });
        Assert.Equal(ConversionProgressState.Partial, p.State);
        Assert.Equal(40m, p.Percent);
        Assert.False(p.ByHeaderTotal);
        var (_, reason) = DocumentService.ComputeLifecycle(Po("PO-Y", 10_700m), Summary(p, "PI-Y"));
        Assert.EndsWith(" 40%", reason);
    }

    // ── ฝ่ายค้านรอบ 242 N6 ──

    [Fact]
    public void N6_POที่ครบตามยอดรวม_แปลงแกนเดียวกันต้องถามรับทราบ_แกนอื่นหรือครบจริงรายบรรทัดไม่ถาม()
    {
        var header = DocumentConversionProgress.EvaluateByValue(3m, 3_350m, NoRows, true, new[] { Pi(3_350m) });
        var warn = DocumentConversionProgress.HeaderTotalConvertWarning(header, "PO-20260930-0002", "PI-20261001-0005",
            DocumentType.PurchaseInvoice, DocumentType.PurchaseInvoice);
        Assert.NotNull(warn);
        Assert.Contains("PI-20261001-0005", warn);
        Assert.Contains("ครบตามยอดรวมทั้งใบ", warn);
        // แกนส่งมอบ (ใบรับสินค้า) ≠ แกนวางบิล ⇒ ไม่ถาม
        Assert.Null(DocumentConversionProgress.HeaderTotalConvertWarning(header, "PO", "PI", DocumentType.PurchaseInvoice, DocumentType.GoodsReceiptNote));
        // ครบจากรายการที่จับคู่จริง ⇒ ด่านรายบรรทัดเห็นอยู่แล้ว ไม่ถามซ้ำ
        var lined = DocumentConversionProgress.EvaluateByValue(1m, 3_350m, new[] { (DocumentType.PurchaseInvoice, 1m, 3_350m) }, true);
        Assert.Null(DocumentConversionProgress.HeaderTotalConvertWarning(lined, "PO", "PI", DocumentType.PurchaseInvoice, DocumentType.PurchaseInvoice));
    }

    [Fact]
    public void N6_ใบวางบิลที่ยังมียอดค้าง_ห้ามจบจากกติกายอดรวมทั้งใบ_ทิศตรงข้ามครบจริงรายบรรทัดจบได้()
    {
        var bn = new Document
        {
            DocumentType = DocumentType.BillingNote, DocumentNumber = "BN-2026-0042",
            Status = DocumentStatus.Approved, TotalAmount = 10_700m, BalanceDue = 10_700m,
        };
        var header = DocumentConversionProgress.EvaluateByValue(1m, 10_000m, NoRows, true,
            new[] { new DocumentConversionProgress.HeaderLinkedChild(DocumentType.Invoice, 10_000m, false) });
        Assert.True(header.ByHeaderTotal);
        var (status, _) = DocumentService.ComputeLifecycle(bn, new DocumentService.ConversionSummary(header,
            new DocumentBrief(Guid.NewGuid(), "INV-1", DocumentType.Invoice, DocumentStatus.Approved, new DateTime(2026, 10, 1), 10_700m), 1));
        Assert.NotEqual("Done", status);

        var lined = DocumentConversionProgress.EvaluateByValue(1m, 10_000m, new[] { (DocumentType.Invoice, 1m, 10_000m) }, true);
        var (status2, _) = DocumentService.ComputeLifecycle(bn, new DocumentService.ConversionSummary(lined,
            new DocumentBrief(Guid.NewGuid(), "INV-1", DocumentType.Invoice, DocumentStatus.Approved, new DateTime(2026, 10, 1), 10_700m), 1));
        Assert.Equal("Done", status2);
    }

    [Fact]
    public void ไม่ส่งข้อเท็จจริงใบลูกผูกหัว_พฤติกรรมเดิมทุกประการ()
    {
        var p = DocumentConversionProgress.EvaluateByValue(3m, 3_350m, NoRows, true);
        Assert.Equal(ConversionProgressState.Partial, p.State);
        Assert.Null(p.Percent);
    }
}
