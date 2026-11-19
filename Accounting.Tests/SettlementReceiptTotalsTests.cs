using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Pdf;
using UglyToad.PdfPig;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 273 — แถวยอดของใบเสร็จรับชำระที่ไม่ถือ VAT ของใบต้นทาง (<see cref="SettlementReceiptTotals"/>)
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-11-19) ═══ TIV-20261026-0001 มูลค่า 700,000.00 + VAT 49,000.00 = 749,000.00 → บันทึกรับชำระ → REC-20261029-0001
/// บรรทัดเดียว 749,000.00 · กระดาษ + หน้ารายละเอียดพิมพ์ "ยอดรวมก่อน VAT 749,000.00 · VAT 0.00" (เงินที่รับรวม VAT แล้ว และ VAT ถูกเรียกเก็บที่ใบกำกับแล้ว)
/// ⇒ ต้องเป็น "ยอดรับชำระ 749,000.00" + "(ตามใบกำกับภาษีเลขที่ … : มูลค่า 700,000.00 · VAT 49,000.00 · รวม 749,000.00)" ·
/// ทิศตรงข้าม: ใบเสร็จขายสดที่ถือ VAT เอง ยังพิมพ์ "ยอดรวมก่อน VAT / VAT" เหมือนเดิม
/// </summary>
public class SettlementReceiptTotalsTests
{
    private static readonly SettlementReceiptTotals.Source Tiv = new(
        DocumentType.TaxInvoice, "TIV-20261026-0001", new DateTime(2026, 10, 26), 700_000m, 49_000m, 0m, 749_000m);

    private static SettlementReceiptTotals.Facts SettlementFacts(decimal received, params SettlementReceiptTotals.Source[] src)
        => new(DocumentType.Receipt, IsTaxInvoiceByLaw: false, IsDeposit: false, ReceiptVatAmount: 0m,
            ReceiptWithholdingTaxAmount: 0m, ReceiptTotalAmount: received, Sources: src);

    // ─────────────── ตัวตัดสิน ───────────────

    [Fact]
    public void ใบเสร็จรับชำระTIV700kVAT49k_ยอดรับชำระ749k_อ้างอิงใบต้นทาง_ไม่ใช่บางส่วน()
    {
        var v = SettlementReceiptTotals.Decide(SettlementFacts(749_000m, Tiv));
        Assert.NotNull(v);
        Assert.Equal((749_000m, false, false, 749_000m), (v!.AmountReceived, v.ShowSettledGrossRow, v.IsPartial, v.SourcesTotal));
        Assert.Equal(Tiv, Assert.Single(v.References));
        Assert.Equal(749_000m, SettlementReceiptTotals.GrossOf(Tiv));
    }

    [Fact]
    public void บรรทัดอ้างอิง_ไทยและอังกฤษ_ผ่านDocumentLabels()
    {
        var v = SettlementReceiptTotals.Decide(SettlementFacts(749_000m, Tiv))!;
        Assert.Equal(new[]
        {
            "(ตามใบกำกับภาษีเลขที่ TIV-20261026-0001 ลงวันที่ 26/10/2026: มูลค่า 700,000.00 · VAT 49,000.00 · รวม 749,000.00)",
            "ยอดรับชำระรวมภาษีมูลค่าเพิ่มที่เรียกเก็บตามเอกสารอ้างอิงแล้ว — ใบเสร็จนี้ไม่เรียกเก็บภาษีมูลค่าเพิ่มซ้ำ",
        }, DocumentLabels.For("th").SettlementTotalsLines(v));
        var en = DocumentLabels.For("en").SettlementTotalsLines(v);
        Assert.Equal("(Per Tax Invoice No. TIV-20261026-0001 dated 26 Oct 2026: value 700,000.00 · VAT 49,000.00 · total 749,000.00)", en[0]);
        Assert.All(en, line => Assert.DoesNotMatch("[฀-๿]", line));   // โหมดอังกฤษไม่มีไทยหลุด
        Assert.Equal(("ยอดรับชำระ", "Amount received"), (DocumentLabels.For("th").TotalAmountReceived, DocumentLabels.For("en").TotalAmountReceived));
    }

    [Fact]
    public void รับชำระบางส่วน_บอกว่าบางส่วนของยอดใด()
    {
        var v = SettlementReceiptTotals.Decide(SettlementFacts(300_000m, Tiv))!;
        Assert.True(v.IsPartial);
        Assert.Equal(300_000m, v.AmountReceived);
        var th = DocumentLabels.For("th").SettlementTotalsLines(v);
        Assert.Contains("รับชำระบางส่วนของยอด 749,000.00 ตามเอกสารอ้างอิง", th);
        Assert.Contains("มูลค่า 700,000.00 · VAT 49,000.00 · รวม 749,000.00", th[0]);   // อ้างยอดเต็มของใบกำกับ ไม่แต่งสัดส่วน VAT ขึ้นเอง
    }

    [Fact]
    public void ใบต้นทางมีภาษีหัก_รับสุทธิครบ_ไม่ใช่บางส่วน_บรรทัดอ้างอิงมีภาษีหัก()
    {
        var src = Tiv with { WithholdingTaxAmount = 21_000m, TotalAmount = 728_000m };
        var v = SettlementReceiptTotals.Decide(SettlementFacts(728_000m, src))!;
        Assert.False(v.IsPartial);
        Assert.EndsWith("รวม 749,000.00 · ภาษีหัก ณ ที่จ่าย 21,000.00)", DocumentLabels.For("th").SettlementTotalsLines(v)[0]);
    }

    [Fact]
    public void ใบเสร็จมีภาษีหัก_คงแถวยอดตัดชำระ()
    {
        var f = SettlementFacts(728_000m, Tiv with { WithholdingTaxAmount = 21_000m, TotalAmount = 728_000m }) with { ReceiptWithholdingTaxAmount = 21_000m };
        Assert.True(SettlementReceiptTotals.Decide(f)!.ShowSettledGrossRow);
    }

    [Fact]
    public void หลายใบต้นทาง_บรรทัดอ้างอิงต่อใบ_ยอดรวมทุกใบ()
    {
        var inv = new SettlementReceiptTotals.Source(DocumentType.Invoice, "INV-1", new DateTime(2026, 10, 1), 1_000m, 70m, 0m, 1_070m);
        var v = SettlementReceiptTotals.Decide(SettlementFacts(750_070m, Tiv, inv))!;
        Assert.Equal((2, 750_070m, false), (v.References.Count, v.SourcesTotal, v.IsPartial));
        Assert.StartsWith("(ตามใบแจ้งหนี้เลขที่ INV-1", DocumentLabels.For("th").SettlementTotalsLines(v)[1]);
    }

    // ── ทิศตรงข้าม: ใบที่ถือ VAT เอง / ไม่ใช่ใบรับชำระของใบที่มี VAT ⇒ แถวปกติ ──

    [Fact]
    public void ทิศตรงข้าม_ใบเสร็จขายสดถือVATเอง_แถวปกติ()
        => Assert.Null(SettlementReceiptTotals.Decide(new SettlementReceiptTotals.Facts(DocumentType.Receipt, false, false,
            ReceiptVatAmount: 70m, 0m, 1_070m, Array.Empty<SettlementReceiptTotals.Source>())));

    [Fact]
    public void ทิศตรงข้าม_ใบกำกับณวันรับเงิน_มัดจำ_ใบต้นทางไม่มีVAT_ไม่มีใบต้นทาง_แถวปกติ()
    {
        Assert.Null(SettlementReceiptTotals.Decide(SettlementFacts(749_000m, Tiv) with { IsTaxInvoiceByLaw = true }));
        Assert.Null(SettlementReceiptTotals.Decide(SettlementFacts(749_000m, Tiv) with { IsDeposit = true }));
        Assert.Null(SettlementReceiptTotals.Decide(SettlementFacts(1_000m, Tiv with { VatAmount = 0m, SubTotal = 1_000m, TotalAmount = 1_000m })));
        Assert.Null(SettlementReceiptTotals.Decide(SettlementFacts(749_000m)));
        Assert.Null(SettlementReceiptTotals.Decide(SettlementFacts(749_000m, Tiv with { Type = DocumentType.Quotation })));
        Assert.Null(SettlementReceiptTotals.Decide(SettlementFacts(749_000m, Tiv) with { ReceiptType = DocumentType.TaxInvoice }));
    }

    // ─────────────── ตัวเรนเดอร์ทั้งสอง (HTML + QuestPDF) ───────────────

    private static PdfGenerationService Renderer() => new(null!, null!);

    private static (Document Doc, Company Co, DocumentTemplate T) SettlementReceipt()
    {
        var co = new Company { Name = "บริษัท ทดสอบใบเสร็จ จำกัด", TaxId = "0105556000009", Address = "1 ถนนสีลม กรุงเทพฯ", IsVatRegistered = true };
        var t = new DocumentTemplate { DocumentType = DocumentType.Receipt, ShowLogo = false };
        var doc = new Document
        {
            DocumentNumber = "REC-20261029-0001", DocumentType = DocumentType.Receipt, Status = DocumentStatus.Paid,
            DocumentDate = new DateTime(2026, 10, 29), Contact = new Contact { Name = "ลูกค้า", TaxId = "0107537000017", Address = "กรุงเทพฯ" },
            IsSettlementReceipt = true, IsTaxInvoiceByLaw = false, Reference = Tiv.Number,
            SubTotal = 749_000m, VatAmount = 0m, TotalAmount = 749_000m, PaidAmount = 749_000m,
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "รับชำระเงินตามใบกำกับภาษีเลขที่ " + Tiv.Number, Quantity = 1, Unit = "รายการ", UnitPrice = 749_000m, Amount = 749_000m });
        doc.SettlementTotals = SettlementReceiptTotals.Decide(SettlementFacts(749_000m, Tiv));
        return (doc, co, t);
    }

    private static (Document Doc, Company Co, DocumentTemplate T) CashSaleReceipt()
    {
        var (doc, co, t) = SettlementReceipt();
        doc.IsSettlementReceipt = false;
        doc.Reference = null;
        doc.SubTotal = 1_000m; doc.VatAmount = 70m; doc.TotalAmount = 1_070m;
        doc.Lines.Clear();
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ขายสินค้า", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        doc.SettlementTotals = null;   // ตัวตัดสินคืน null สำหรับใบที่ถือ VAT เอง (ล็อกไว้ในเทสต์ตัวตัดสิน)
        return (doc, co, t);
    }

    [Fact]
    public void HTML_ใบเสร็จรับชำระ_ยอดรับชำระ_อ้างอิง_ไม่มียอดก่อนVAT()
    {
        var (doc, co, t) = SettlementReceipt();
        var html = Renderer().BuildDocumentHtml(doc, co, null, t, null, "th");
        Assert.DoesNotContain("ยอดรวมก่อน VAT", html);
        Assert.DoesNotContain("ภาษีมูลค่าเพิ่ม 7%", html);
        Assert.Contains("<span>ยอดรับชำระ</span><span>749,000.00</span>", html);
        // HtmlEncode เข้ารหัส "·" (U+00B7) เป็น &#183; — เทียบกับข้อความที่ escape แล้ว (ตัวเดียวกับที่ renderer ใช้)
        Assert.Contains(System.Net.WebUtility.HtmlEncode(DocumentLabels.For("th").SettlementTotalsLines(doc.SettlementTotals!)[0]), html);
        Assert.Contains("ยอดรับชำระรวมภาษีมูลค่าเพิ่มที่เรียกเก็บตามเอกสารอ้างอิงแล้ว", html);
        Assert.DoesNotContain("ยอดรวมสุทธิ", html);
    }

    [Fact]
    public void HTML_ทิศตรงข้าม_ใบเสร็จขายสดถือVAT_ยอดก่อนVATและVATเหมือนเดิม()
    {
        var (doc, co, t) = CashSaleReceipt();
        var html = Renderer().BuildDocumentHtml(doc, co, null, t, null, "th");
        Assert.Contains("<span>ยอดรวมก่อน VAT</span><span>1,000.00</span>", html);
        Assert.Contains("<span>ภาษีมูลค่าเพิ่ม 7%</span><span>70.00</span>", html);
        Assert.Contains("<span>ยอดรวมสุทธิ</span><span>1,070.00</span>", html);
        Assert.DoesNotContain("ยอดรับชำระ", html);
        Assert.DoesNotContain("settlement-ref", html);
    }

    [Fact]
    public void HTML_เลขที่ใบต้นทางถูกescape()
    {
        var (doc, co, t) = SettlementReceipt();
        doc.SettlementTotals = SettlementReceiptTotals.Decide(SettlementFacts(749_000m, Tiv with { Number = "<img src=x onerror=alert(1)>" }));
        var html = Renderer().BuildDocumentHtml(doc, co, null, t, null, "th");
        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("&lt;img src=x", html);
    }

    private static string PdfText(byte[] pdf)
    {
        using var d = PdfDocument.Open(pdf);
        return new string(string.Concat(d.GetPages().Select(p => string.Concat(p.Letters.Select(l => l.Value)))).Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    private static string NoSpace(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    [Fact]
    public void QuestPDF_ใบเสร็จรับชำระ_ป้ายและบรรทัดอ้างอิงเดียวกับHTML_ทิศตรงข้ามคงเดิม()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var L = DocumentLabels.For("en");   // ป้ายอังกฤษ (ไม่ขึ้นกับการตัดคำไทยของตัวอ่าน PDF)
        var (doc, co, t) = SettlementReceipt();
        var settle = PdfText(Renderer().RenderDocumentPdfNative(doc, co, null, t, null, "en"));
        Assert.Contains(NoSpace(L.TotalAmountReceived), settle);
        Assert.Contains(NoSpace(L.SettlementTotalsLines(doc.SettlementTotals!)[0]), settle);
        Assert.DoesNotContain(NoSpace(L.TotalSubtotal), settle);
        Assert.DoesNotContain(NoSpace(L.TotalNet), settle);

        var (cash, co2, t2) = CashSaleReceipt();
        var normal = PdfText(Renderer().RenderDocumentPdfNative(cash, co2, null, t2, null, "en"));
        Assert.Contains(NoSpace(L.TotalSubtotal), normal);
        Assert.Contains(NoSpace(L.TotalNet), normal);
        Assert.DoesNotContain(NoSpace(L.TotalAmountReceived), normal);
    }
}
