using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 196 · ทีม Q — "จากหน้ารวมใบเสนอราคา จะรู้ได้ยังไงว่าใบไหนออกใบแจ้งหนี้ไปแล้ว โดยไม่ต้องไล่เปิดทีละใบ"
///
/// <para>บั๊กที่ยืนยัน: หน้ารวม (<c>GetDocumentsAsync</c>) ไม่ส่ง % การแปลงเข้า <c>MapDocumentToResponse</c> ⇒ ใบเสนอราคา
/// Approved ทุกใบขึ้น "⏳ รอดำเนินการต่อ" แม้ออกใบแจ้งหนี้ไปแล้ว · สูตรเดิมรวมทุกแกน (ส่งของ 50% + แจ้งหนี้ 50% = "ครบ") ·
/// ป้ายบอกแค่เลขใบลูก ไม่บอกว่าไปเป็นอะไร · ใบลูกที่ถูกยกเลิกยังถูกเอ่ยชื่อ.
/// รอบนี้ list · detail · ตัวกรอง เรียก <see cref="DocumentConversionProgress"/> ตัวเดียว (ล็อกจุดเรียกด้วย
/// <c>tools/required_call_site_check.py</c>)</para>
///
/// <para>สองครึ่ง: (ก) ใบที่ป้ายเคยโกหก ต้องบอกความจริง (ข) ใบที่ถูกอยู่แล้ว/ชนิดอื่น ต้องไม่ถูกแตะ</para>
/// </summary>
public class DocumentConversionProgressTests
{
    private static Document Quotation(DocumentStatus status = DocumentStatus.Approved, decimal total = 10_700m)
        => new()
        {
            DocumentType = DocumentType.Quotation,
            DocumentNumber = "QT-2026-0007",
            Status = status,
            TotalAmount = total,
            // ใบเสนอราคาเก็บ BalanceDue = ยอดเต็ม (ไม่ใช่หนี้) — ตัวที่ทำให้คอลัมน์ "ค้างชำระ" เป็นตัวแดง
            BalanceDue = total,
        };

    private static DocumentService.ConversionSummary Summary(
        DocumentConversionProgress.Progress p, DocumentBrief? latest = null, int count = 0)
        => new(p, latest, count);

    private static DocumentBrief Child(string no, DocumentType type, DocumentStatus status = DocumentStatus.Approved)
        => new(Guid.NewGuid(), no, type, status, new DateTime(2026, 9, 20), 10_700m);

    private static (DocumentType, decimal)[] Rows(params (DocumentType, decimal)[] r) => r;

    // ═══════════════════ (ก) ใบที่ป้ายเคยโกหก — ต้องบอกความจริง ═══════════════════

    [Fact]
    public void ใบเสนอราคาที่ออกใบแจ้งหนี้ครบแล้ว_ป้ายบอกชนิดและเลขใบลูก_ไม่ใช่รอดำเนินการ()
    {
        var p = DocumentConversionProgress.Evaluate(5m, Rows((DocumentType.Invoice, 5m)), hasActiveLinkedChild: true);
        var (status, reason) = DocumentService.ComputeLifecycle(Quotation(),
            Summary(p, Child("INV-2026-0012", DocumentType.Invoice), 1));

        Assert.Equal(ConversionProgressState.Full, p.State);
        Assert.Equal("Done", status);
        Assert.Equal("✓ ออกใบแจ้งหนี้ INV-2026-0012 แล้ว", reason);
        Assert.DoesNotContain("รอดำเนินการ", reason);
    }

    [Fact]
    public void ออกใบกำกับภาษีจากใบเสนอราคา_ป้ายเอ่ยชื่อใบกำกับภาษี()
    {
        var p = DocumentConversionProgress.Evaluate(2m, Rows((DocumentType.TaxInvoice, 2m)), true);
        var (_, reason) = DocumentService.ComputeLifecycle(Quotation(),
            Summary(p, Child("TIV-2026-0003", DocumentType.TaxInvoice), 1));
        Assert.Equal("✓ ออกใบกำกับภาษี TIV-2026-0003 แล้ว", reason);
    }

    [Fact]
    public void ออกใบแจ้งหนี้บางส่วน_60เปอร์เซ็นต์_ป้ายบอกสัดส่วน()
    {
        var p = DocumentConversionProgress.Evaluate(5m, Rows((DocumentType.Invoice, 3m)), true);
        var (status, reason) = DocumentService.ComputeLifecycle(Quotation(),
            Summary(p, Child("INV-2026-0013", DocumentType.Invoice), 1));

        Assert.Equal(ConversionProgressState.Partial, p.State);
        Assert.Equal(60.0m, p.Percent);
        Assert.Equal("PartiallyDone", status);
        Assert.Equal("◐ ออกใบแจ้งหนี้ INV-2026-0013 แล้ว 60%", reason);
    }

    [Fact]
    public void ใบลูกหลายใบ_ต่อท้ายจำนวนที่เหลือ()
    {
        var p = DocumentConversionProgress.Evaluate(10m,
            Rows((DocumentType.Invoice, 4m), (DocumentType.Invoice, 6m)), true);
        var (_, reason) = DocumentService.ComputeLifecycle(Quotation(),
            Summary(p, Child("INV-2026-0020", DocumentType.Invoice), 3));
        Assert.Equal("✓ ออกใบแจ้งหนี้ INV-2026-0020 แล้ว +2", reason);
    }

    [Fact]
    public void ส่งของ50_แจ้งหนี้50_ของก้อนเดียวกัน_ไม่ใช่ครบ()
    {
        // สูตรเดิมรวมทุกแกน = 100% "✓ ครบ" ทั้งที่ยังแจ้งหนี้ได้อีกครึ่ง (ด่านกันแปลงเกินแยกแกนอยู่แล้ว)
        var p = DocumentConversionProgress.Evaluate(10m,
            Rows((DocumentType.DeliveryNote, 5m), (DocumentType.Invoice, 5m)), true);
        Assert.Equal(ConversionProgressState.Partial, p.State);
        Assert.Equal(50.0m, p.Percent);
    }

    [Fact]
    public void ใบลูกถูกยกเลิก_ไม่นับ_ใบเสนอราคากลับเป็นยังไม่ออก()
    {
        // ตัวโหลดกรองใบลูกด้วย InactiveChildStatuses ⇒ ใบลูกที่ยกเลิกไม่เหลือแถวให้ Evaluate
        Assert.Contains(DocumentStatus.Voided, DocumentConversionProgress.InactiveChildStatuses);
        Assert.Contains(DocumentStatus.Rejected, DocumentConversionProgress.InactiveChildStatuses);
        var p = DocumentConversionProgress.Evaluate(5m, Rows(), hasActiveLinkedChild: false);
        var (status, reason) = DocumentService.ComputeLifecycle(Quotation(), Summary(p));

        Assert.Equal(ConversionProgressState.None, p.State);
        Assert.Equal("Open", status);
        Assert.Equal("⏳ ยังไม่ออกเอกสารต่อ", reason);
    }

    [Fact]
    public void ใบลูกผูกด้วย_RelatedDocumentId_แต่ไม่ได้ยกรายการ_นับเป็นบางส่วน_ไม่ทราบสัดส่วน()
    {
        // เช่น ใบแจ้งหนี้มัดจำที่อ้างใบเสนอราคา — ห้ามบอกว่า "ยังไม่ออก"
        var p = DocumentConversionProgress.Evaluate(5m, Rows(), hasActiveLinkedChild: true);
        var (_, reason) = DocumentService.ComputeLifecycle(Quotation(),
            Summary(p, Child("INV-2026-0030", DocumentType.Invoice), 1));
        Assert.Equal(ConversionProgressState.Partial, p.State);
        Assert.Equal("◐ ออกใบแจ้งหนี้ INV-2026-0030 แล้ว · ไม่ทราบสัดส่วน", reason);
    }

    [Fact]
    public void ใบลูกยังเป็นร่าง_ห้ามบอกว่าออกแล้ว_และห้ามโชว์เลขชั่วคราว()
    {
        var p = DocumentConversionProgress.Evaluate(5m, Rows((DocumentType.Invoice, 5m)), true);
        var draftNo = "DRAFT-" + Guid.NewGuid();
        var (_, reason) = DocumentService.ComputeLifecycle(Quotation(),
            Summary(p, Child(draftNo, DocumentType.Invoice, DocumentStatus.Draft), 1));
        Assert.Equal("✓ ร่างใบแจ้งหนี้ไว้แล้ว (ยังไม่อนุมัติ)", reason);
        Assert.DoesNotContain("DRAFT-", reason);
    }

    [Fact]
    public void เกือบครบ_ปัดแล้วต้องไม่ขึ้น100เปอร์เซ็นต์()
    {
        var p = DocumentConversionProgress.Evaluate(10_000m, Rows((DocumentType.Invoice, 9_996m)), true);
        Assert.Equal(ConversionProgressState.Partial, p.State);
        Assert.True(p.Percent < 100m);
        var (_, reason) = DocumentService.ComputeLifecycle(Quotation(),
            Summary(p, Child("INV-2026-0040", DocumentType.Invoice), 1));
        Assert.EndsWith(" 99%", reason);
    }

    [Fact]
    public void ยังไม่ได้คำนวณ_ห้ามเดาว่ายังไม่ออก()
    {
        // ต้นเหตุบั๊กเดิม: เส้นที่ไม่ได้ส่งผลการแปลงมา ตกไปที่ "⏳ รอดำเนินการต่อ" ⇒ ป้ายโกหก
        var (status, reason) = DocumentService.ComputeLifecycle(Quotation(), conversion: null);
        Assert.Equal("Open", status);
        Assert.Equal("", reason);
    }

    [Fact]
    public void ใบเสนอราคา_ค้างชำระไม่มีความหมาย_ธงเป็นเท็จ()
    {
        Assert.False(ArApScope.CarriesBalance(DocumentType.Quotation));
        Assert.False(ArApScope.CarriesBalance(DocumentType.PurchaseOrder));
        Assert.False(ArApScope.CarriesBalance(DocumentType.BillingNote));   // ใบวางบิลไม่ใช่ลูกหนี้ (ArApScope)
        Assert.False(ArApScope.CarriesBalance(DocumentType.Receipt));
    }

    // ═══════════════════ (ข) ของที่ถูกอยู่แล้ว — ต้องไม่ถูกแตะ ═══════════════════

    [Fact]
    public void ไม่มีใบลูกเลย_ยังไม่ออกเอกสารต่อ()
    {
        var p = DocumentConversionProgress.Evaluate(5m, Rows(), false);
        Assert.Equal(new DocumentConversionProgress.Progress(0m, ConversionProgressState.None), p);
    }

    [Fact]
    public void ไม่มีบรรทัด_ไม่แต่งเปอร์เซ็นต์()
    {
        Assert.Equal(new DocumentConversionProgress.Progress(null, ConversionProgressState.None),
            DocumentConversionProgress.Evaluate(0m, Rows(), false));
        Assert.Equal(new DocumentConversionProgress.Progress(null, ConversionProgressState.Partial),
            DocumentConversionProgress.Evaluate(0m, Rows(), true));
    }

    [Fact]
    public void ครบพอดีและเกิน_เป็นครบ100()
    {
        Assert.Equal(new DocumentConversionProgress.Progress(100m, ConversionProgressState.Full),
            DocumentConversionProgress.Evaluate(5m, Rows((DocumentType.GoodsReceiptNote, 5m)), true));
        Assert.Equal(new DocumentConversionProgress.Progress(100m, ConversionProgressState.Full),
            DocumentConversionProgress.Evaluate(5m, Rows((DocumentType.PurchaseInvoice, 7m)), true));
    }

    [Fact]
    public void สูตรเปอร์เซ็นต์แกนเดียว_ตรงกับสูตรเดิม_ปัดทศนิยม1ตำแหน่ง()
    {
        // สูตรเดิม round(consumed/total×100, 1, AwayFromZero) — แกนเดียวต้องได้ค่าเดิมทุกตัว
        Assert.Equal(33.3m, DocumentConversionProgress.Evaluate(3m, Rows((DocumentType.Invoice, 1m)), true).Percent);
        Assert.Equal(66.7m, DocumentConversionProgress.Evaluate(3m, Rows((DocumentType.Invoice, 2m)), true).Percent);
    }

    [Fact]
    public void ใบร่าง_รออนุมัติ_ยกเลิก_ของต้นทางเอง_ป้ายเดิมไม่เปลี่ยน()
    {
        var full = Summary(new DocumentConversionProgress.Progress(100m, ConversionProgressState.Full),
            Child("INV-1", DocumentType.Invoice), 1);
        Assert.Equal(("Open", ""), DocumentService.ComputeLifecycle(Quotation(DocumentStatus.Draft), full));
        Assert.Equal(("Open", ""), DocumentService.ComputeLifecycle(Quotation(DocumentStatus.WaitingApproval), full));
        Assert.Equal(("Cancelled", ""), DocumentService.ComputeLifecycle(Quotation(DocumentStatus.Voided), full));
    }

    [Fact]
    public void ใบแจ้งหนี้_ป้ายยังตามยอดชำระ_ไม่ถูกตัวแปลงแตะ()
    {
        var inv = new Document { DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved, BalanceDue = 500m };
        var full = Summary(new DocumentConversionProgress.Progress(100m, ConversionProgressState.Full),
            Child("TIV-1", DocumentType.TaxInvoice), 1);
        Assert.Equal(("Open", "⏳ รอจ่าย/รับชำระ · 500.00"), DocumentService.ComputeLifecycle(inv, full));
        Assert.True(ArApScope.CarriesBalance(DocumentType.Invoice));
        Assert.True(ArApScope.CarriesBalance(DocumentType.TaxInvoice));
        Assert.True(ArApScope.CarriesBalance(DocumentType.PurchaseInvoice));
        Assert.True(ArApScope.CarriesBalance(DocumentType.Expense));
        Assert.True(ArApScope.CarriesBalance(DocumentType.DebitNote));
    }

    [Theory]
    [InlineData(DocumentType.DeliveryNote, DocumentConversionProgress.Axis.Delivery)]
    [InlineData(DocumentType.GoodsReceiptNote, DocumentConversionProgress.Axis.Delivery)]
    [InlineData(DocumentType.Invoice, DocumentConversionProgress.Axis.Billing)]
    [InlineData(DocumentType.TaxInvoice, DocumentConversionProgress.Axis.Billing)]
    [InlineData(DocumentType.BillingNote, DocumentConversionProgress.Axis.Billing)]
    [InlineData(DocumentType.PurchaseInvoice, DocumentConversionProgress.Axis.Billing)]
    [InlineData(DocumentType.Expense, DocumentConversionProgress.Axis.Billing)]
    [InlineData(DocumentType.PurchaseOrder, DocumentConversionProgress.Axis.Billing)]
    [InlineData(DocumentType.Receipt, DocumentConversionProgress.Axis.Other)]
    [InlineData(DocumentType.CreditNote, DocumentConversionProgress.Axis.Other)]
    public void ตารางแกน_ตรงกับด่านกันแปลงเกินเดิม(DocumentType child, DocumentConversionProgress.Axis axis)
        => Assert.Equal(axis, DocumentConversionProgress.AxisOf(child));

    [Fact]
    public void ชนิดต้นทาง_ห้าชนิดเดิม()
    {
        Assert.Equal(new[]
        {
            DocumentType.Quotation, DocumentType.PurchaseRequisition, DocumentType.PurchaseOrder,
            DocumentType.GoodsReceiptNote, DocumentType.DeliveryNote,
        }, DocumentConversionProgress.SourceTypes);
        Assert.False(DocumentConversionProgress.IsConversionBearing(DocumentType.Invoice));
        // ใบลูกที่นับ: ทุกสถานะยกเว้นยกเลิก/ปฏิเสธ (ร่างก็นับ — ด่านกันแปลงเกินนับร่างเช่นกัน)
        Assert.Equal(new[] { DocumentStatus.Voided, DocumentStatus.Rejected },
            DocumentConversionProgress.InactiveChildStatuses);
    }

    [Fact]
    public void ตัวกรอง_รับเฉพาะชื่อ_ไม่รับตัวเลข_ว่างคือไม่กรอง()
    {
        Assert.True(DocumentConversionProgress.TryParseFilter("full", out var full));
        Assert.Equal(ConversionProgressState.Full, full);
        Assert.True(DocumentConversionProgress.TryParseFilter("", out var none));
        Assert.Null(none);
        Assert.True(DocumentConversionProgress.TryParseFilter(null, out _));
        Assert.False(DocumentConversionProgress.TryParseFilter("2", out _));
        Assert.False(DocumentConversionProgress.TryParseFilter("Bogus", out _));
        Assert.False(DocumentConversionProgress.TryParseFilter("Partial, Full", out _));
    }

    [Fact]
    public void ตัวเลือกตัวกรอง_ครบทุกค่าของ_enum_ไม่ซ้ำ()
    {
        var values = DocumentConversionProgress.FilterOptions.Select(o => o.Value).ToList();
        Assert.Equal(Enum.GetValues<ConversionProgressState>().OrderBy(v => v), values.OrderBy(v => v));
        Assert.All(DocumentConversionProgress.FilterOptions, o => Assert.False(string.IsNullOrWhiteSpace(o.Label)));
    }

    [Theory]
    [InlineData(DocumentType.Quotation, "ใบเสนอราคา", "Quotation")]
    [InlineData(DocumentType.Invoice, "ใบแจ้งหนี้", "Invoice")]
    [InlineData(DocumentType.TaxInvoice, "ใบกำกับภาษี", "Tax Invoice")]
    [InlineData(DocumentType.DeliveryNote, "ใบส่งของ", "Delivery Note")]
    [InlineData(DocumentType.PurchaseInvoice, "ใบแจ้งหนี้ซื้อ", "Purchase Invoice")]
    [InlineData(DocumentType.GoodsReceiptNote, "ใบรับสินค้า", "Goods Receipt Note")]
    public void ชื่อชนิดเอกสาร_ตารางเดียวกับหัวกระดาษเดิม(DocumentType t, string th, string en)
    {
        Assert.Equal(th, DocumentTypeNames.Title(t, "th"));
        Assert.Equal(th, DocumentTypeNames.Title(t, null));
        Assert.Equal(en, DocumentTypeNames.Title(t, "en"));
    }
}
