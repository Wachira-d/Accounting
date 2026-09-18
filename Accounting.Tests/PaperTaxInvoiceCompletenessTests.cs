using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **"ใบนี้เป็นใบกำกับภาษีที่สมบูรณ์ไหม"** — ด่านที่ตัดสินว่า "ความเงียบของกระดาษ"
/// นับเป็นหลักฐานหรือไม่ (คำตัดสินเจ้าของ 2026-09-18: "ถ้าเป็นใบกำกับภาษีที่สมบูรณ์
/// กระดาษชนะ")
///
/// ล็อกสองครึ่งตามกฎเหล็ก #4 H: ครึ่งที่ใบสมบูรณ์ยังชนะเหมือนเดิม และครึ่งที่ใบไม่ครบ
/// **ต้องไม่ถูกนับ** — ด่านที่ตอบ Complete ให้ทุกใบ = ไม่มีด่าน
/// </summary>
public class PaperTaxInvoiceCompletenessTests
{
    // เลขนิติบุคคลที่ผ่าน checksum จริง (ใช้ตัวเดียวกับเทสต์อื่นในเรพ)
    private const string GoodTaxId = "0105556123453";   // ผ่าน mod-11 จริง (คำนวณแล้ว)

    private static PaperTaxInvoiceFacts Full(
        string? rawText = "ใบกำกับภาษี / ใบเสร็จรับเงิน",
        string? sellerName = "บริษัท ขายของ จำกัด",
        string? sellerTaxId = GoodTaxId,
        string? sellerAddress = "123 ถนนสุขุมวิท กรุงเทพฯ",
        string? buyerName = "บริษัท ผู้ซื้อ จำกัด",
        string? buyerAddress = "456 ถนนพระราม 4 กรุงเทพฯ",
        string? docNo = "INV-2569-0001",
        DateTime? docDate = null,
        int lineCount = 2,
        decimal? sub = 1000m, decimal? vat = 70m, decimal? total = 1070m)
        => new(rawText, sellerName, sellerTaxId, sellerAddress, buyerName, buyerAddress,
            docNo, docDate ?? new DateTime(2026, 9, 18), lineCount, sub, vat, total);

    // ══════════ ครึ่งแรก: ใบที่สมบูรณ์ต้องได้ Complete ══════════

    [Fact]
    public void ใบกำกับเต็มรูปครบทุกช่อง_ต้องสมบูรณ์()
    {
        var v = PaperTaxInvoiceCompleteness.Judge(Full());

        Assert.Equal(PaperTaxInvoiceGrade.Complete, v.Grade);
        Assert.Empty(v.Missing);
    }

    [Fact]
    public void ยอดคลาดเคลื่อนหนึ่งสตางค์จากการปัดเศษ_ยังสมบูรณ์()
        => Assert.Equal(PaperTaxInvoiceGrade.Complete,
            PaperTaxInvoiceCompleteness.Judge(Full(sub: 1000m, vat: 70m, total: 1070.01m)).Grade);

    [Fact]
    public void ไม่มียอดรวมบนใบ_แต่ช่องอื่นครบ_ยังสมบูรณ์()
        // ยอดรวมเป็น 0/ไม่มี ⇒ ไม่มีอะไรให้กระทบยอด แต่ VAT ยังแยกบรรทัดให้เห็นแล้ว
        => Assert.Equal(PaperTaxInvoiceGrade.Complete,
            PaperTaxInvoiceCompleteness.Judge(Full(total: null)).Grade);

    // ══════════ ครึ่งหลัง: ใบที่ไม่ครบต้องไม่ถูกนับ ══════════

    [Fact]
    public void ไม่มีกระดาษให้ดู_คือ_ยังไม่ได้ตรวจ_ไม่ใช่ไม่ครบ()
    {
        // "ไม่รู้" กับ "ตรวจแล้วไม่ครบ" ต้องเป็นคนละค่า (หลักการ G3b)
        Assert.Equal(PaperTaxInvoiceGrade.Unknown,
            PaperTaxInvoiceCompleteness.Judge(Full(rawText: null)).Grade);
        Assert.Equal(PaperTaxInvoiceGrade.Unknown,
            PaperTaxInvoiceCompleteness.Judge(Full(rawText: "   ")).Grade);
    }

    [Fact]
    public void ไม่มีคำว่าใบกำกับภาษีบนหัวเอกสาร_ไม่ครบ()
        => Assert.Equal(PaperTaxInvoiceGrade.Incomplete,
            PaperTaxInvoiceCompleteness.Judge(Full(rawText: "บิลเงินสด ร้านสะดวกซื้อ")).Grade);

    [Fact]
    public void ใบกำกับอย่างย่อ_ไม่ใช่ใบเต็มรูป()
    {
        var v = PaperTaxInvoiceCompleteness.Judge(Full(rawText: "ใบกำกับภาษีอย่างย่อ"));

        Assert.Equal(PaperTaxInvoiceGrade.Incomplete, v.Grade);
        Assert.Contains("อย่างย่อ", v.Reason);
    }

    [Theory]
    [InlineData("ชื่อผู้ขาย")]
    [InlineData("เลขผู้เสียภาษี")]
    [InlineData("ที่อยู่ผู้ขาย")]
    [InlineData("ชื่อผู้ซื้อ")]
    [InlineData("ที่อยู่ผู้ซื้อ")]
    [InlineData("เลขที่")]
    [InlineData("วันที่")]
    [InlineData("บรรทัด")]
    [InlineData("VAT")]
    public void ขาดรายการใดรายการหนึ่งของ_86_4_ก็ไม่สมบูรณ์(string which)
    {
        var f = which switch
        {
            "ชื่อผู้ขาย" => Full(sellerName: null),
            "เลขผู้เสียภาษี" => Full(sellerTaxId: "123"),
            "ที่อยู่ผู้ขาย" => Full(sellerAddress: " "),
            "ชื่อผู้ซื้อ" => Full(buyerName: null),
            "ที่อยู่ผู้ซื้อ" => Full(buyerAddress: null),
            "เลขที่" => Full(docNo: null),
            "วันที่" => Full(docDate: null),
            "บรรทัด" => Full(lineCount: 0),
            _ => Full(vat: null),
        };
        // `docDate: null` ใน Full() แปลว่า "ใช้ค่าตั้งต้น" จึงต้องประกอบเองสำหรับเคสวันที่
        if (which == "วันที่")
            f = f with { DocumentDate = null };

        var v = PaperTaxInvoiceCompleteness.Judge(f);

        Assert.Equal(PaperTaxInvoiceGrade.Incomplete, v.Grade);
        Assert.NotEmpty(v.Missing);
    }

    [Fact]
    public void ยอดกระทบกันไม่ได้_ไม่สมบูรณ์()
        // ก่อน VAT + VAT ≠ ยอดรวม แปลว่าอ่านผิดอย่างน้อยหนึ่งช่อง
        => Assert.Equal(PaperTaxInvoiceGrade.Incomplete,
            PaperTaxInvoiceCompleteness.Judge(Full(sub: 1000m, vat: 70m, total: 2000m)).Grade);

    [Fact]
    public void เลขผู้เสียภาษีที่เป็นบาร์โค้ดสินค้า_ไม่นับว่าครบ()
        // เลข 13 หลักที่ผ่าน EAN-13 แต่ไม่ใช่เลขผู้เสียภาษี — ด่านเดียวกับที่ใช้ทั้งเส้น OCR
        => Assert.Equal(PaperTaxInvoiceGrade.Incomplete,
            PaperTaxInvoiceCompleteness.Judge(Full(sellerTaxId: "8850000000041")).Grade);

    [Fact]
    public void รายการที่ขาดต้องบอกชื่อช่องให้ผู้ใช้ตามได้()
    {
        var v = PaperTaxInvoiceCompleteness.Judge(Full(sellerAddress: null, docNo: null));

        Assert.Equal(2, v.Missing.Count);
        Assert.Contains(v.Missing, m => m.Contains("ที่อยู่ผู้ขาย"));
        Assert.Contains(v.Missing, m => m.Contains("เลขที่"));
    }
}
