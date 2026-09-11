using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่านตรวจเลขที่เอกสารจาก vision — กันเลข 2 ชุดถูกต่อกัน
/// (ที่มา: บิลค่าไฟ กฟภ. จริงที่ผู้ใช้ส่งภาพมา — vision เอา "เลขที่ (No.)"
/// ต่อกับ "เลขที่ใบแจ้งหนี้ (Invoice No.)" เป็นเลขเดียวที่ไม่มีอยู่บนเอกสาร)
/// </summary>
public class DocumentNumberSanitizerTests
{
    // raw text ตามหน้าบิล กฟภ. จริงในภาพที่ผู้ใช้รายงาน
    private const string PeaBill = """
        การไฟฟ้าส่วนภูมิภาค (2000)
        เลขที่ 200 ถนนงามวงศ์วาน แขวงลาดยาว เขตจตุจักร จ.กรุงเทพมหานคร 10900
        ใบเสร็จรับเงิน/ ใบกำกับภาษี
        e-Receipt/ e-Tax Invoice
        เลขที่ (No.) XH0712608004488
        วันที่ (Date) 11/08/2569
        เลขที่ใบแจ้งหนี้ (Invoice No.) 510504578045
        สาขาที่ออกใบกำกับภาษี (Branch No.) 00000
        เลขประจำตัวผู้เสียภาษี (Tax ID No.) 0994000165501
        ชื่อ (Name) ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส
        ค่าไฟฟ้า ประจำเดือน 07/2569
        รหัสเครื่องวัด 5700025917 ประเภทอัตรา 1125 วันที่อ่านหน่วย 25/07/2569
        รวมเงิน 17,496.08
        """;

    // ── เคสจริงที่ผู้ใช้เจอ ──────────────────────────────────────────

    [Fact]
    public void Real_pea_bill_concatenation_is_split_and_the_tax_invoice_number_wins()
    {
        var (doc, note) = DocumentNumberSanitizer.Sanitize(
            "XH0712608004488510504578045", PeaBill);
        Assert.Equal("XH0712608004488", doc);
        Assert.NotNull(note);                       // ต้องมีร่องรอยว่าแก้อะไร
        Assert.Contains("510504578045", note);      // บอกเลขที่ถูกตัดออก
    }

    [Fact]
    public void Reversed_concatenation_still_picks_the_primary_number()
    {
        // vision อาจต่อกลับด้าน (invoice no ก่อน) — ตัวเลือกต้องดูป้าย ไม่ใช่ตำแหน่ง
        var (doc, _) = DocumentNumberSanitizer.Sanitize(
            "510504578045XH0712608004488", PeaBill);
        Assert.Equal("XH0712608004488", doc);
    }

    // ── เคสปกติต้องไม่ถูกแตะ ─────────────────────────────────────────

    [Fact]
    public void Correct_number_passes_untouched()
    {
        var (doc, note) = DocumentNumberSanitizer.Sanitize("XH0712608004488", PeaBill);
        Assert.Equal("XH0712608004488", doc);
        Assert.Null(note);
    }

    [Fact]
    public void Number_not_on_paper_but_not_a_concatenation_is_left_alone()
    {
        // OCR อ่านตัวอักษรเพี้ยน (O↔0) — เทียบตรง ๆ ไม่เจอ แต่ไม่ใช่การต่อเลข
        // ห้ามเดาแก้ (แก้มั่วแย่กว่าปล่อยให้ผู้ใช้เห็นและแก้เองในฟอร์ม)
        var (doc, note) = DocumentNumberSanitizer.Sanitize("XHO7126O8OO4488", PeaBill);
        Assert.Equal("XHO7126O8OO4488", doc);
        Assert.Null(note);
    }

    [Fact]
    public void Split_requires_both_halves_to_exist_on_paper()
    {
        // ครึ่งเดียวอยู่บนเอกสาร อีกครึ่งไม่อยู่ = ไม่ใช่การต่อเลข 2 ชุด — ไม่ผ่า
        var (doc, note) = DocumentNumberSanitizer.Sanitize("XH0712608004488ZZZZ9999", PeaBill);
        Assert.Equal("XH0712608004488ZZZZ9999", doc);
        Assert.Null(note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_extraction_is_returned_as_is(string? raw)
    {
        var (doc, note) = DocumentNumberSanitizer.Sanitize(raw, PeaBill);
        Assert.True(string.IsNullOrWhiteSpace(doc));
        Assert.Null(note);
    }

    [Fact]
    public void Missing_raw_text_skips_validation()
    {
        // e-Tax XML: field มาจาก XML ตรง ๆ ไม่มี raw text ให้เทียบ — ปล่อยผ่าน
        var (doc, note) = DocumentNumberSanitizer.Sanitize("XH0712608004488510504578045", null);
        Assert.Equal("XH0712608004488510504578045", doc);
        Assert.Null(note);
    }

    // ── ป้ายรองแบบอื่น ───────────────────────────────────────────────

    [Fact]
    public void Contract_number_concatenation_is_split_too()
    {
        var text = """
            ใบแจ้งหนี้
            เลขที่ INV-2026-0042
            เลขที่สัญญา CT-889900
            รวมทั้งสิ้น 5,000.00
            """;
        var (doc, _) = DocumentNumberSanitizer.Sanitize("INV-2026-0042CT-889900", text);
        Assert.Equal("INV-2026-0042", doc);
    }

    // ═══ เลขที่บ้าน ไม่ใช่เลขที่เอกสาร (ผู้ใช้รายงาน 2026-09-11) ═══
    // บิลเงินสดเขียนมือที่ช่อง "เล่มที่/เลขที่" **เว้นว่าง** — Azure หยิบเลขที่บ้าน
    // ของร้านในกรอบบนมาเป็นเลขที่เอกสาร ⇒ ตามใบไม่เจอ + ด่านกันสแกนซ้ำใช้ไม่ได้
    private const string HandwrittenCashBill = """
        อ๊อฟ พิการ
        เล่มที่
        เลขที่
        177/18 ม.5 ต.บางพระ อ.ศรีราชา จ. ชลบุรี
        บิลเงินสด
        CASHSALE
        """;

    [Fact]
    public void เลขที่บ้านที่มีคำบอกที่อยู่ตามหลัง_ต้องถูกล้างทิ้ง()
    {
        var (doc, note) = DocumentNumberSanitizer.Sanitize("177/18", HandwrittenCashBill);
        Assert.Null(doc);                       // "ไม่รู้ = บอกว่าไม่รู้"
        Assert.NotNull(note);
        Assert.Contains("เลขที่บ้าน", note!);
    }

    [Fact]
    public void ป้ายเลขที่ที่อยู่ติดกันบนแบบฟอร์มเปล่า_ต้องไม่ช่วยให้ผ่าน()
    {
        // บนกระดาษใบนี้ "เลขที่" (ป้ายที่ถูกเว้นว่าง) อยู่ก่อน "177/18" แค่บรรทัดเดียว
        // — ด่านที่อิง "ความใกล้ของป้าย" ห้ามใช้เป็นข้อยกเว้นของด่านที่อิงคุณสมบัติ
        // ของตัวข้อมูลเอง (บทเรียนเดิม: บาร์โค้ด EAN-13 ที่บังเอิญมีป้ายอยู่ใกล้)
        Assert.Contains("เลขที่\n", HandwrittenCashBill.Replace("\r", ""));
        Assert.Null(DocumentNumberSanitizer.Sanitize("177/18", HandwrittenCashBill).DocumentNumber);
    }

    [Fact]
    public void เลขที่เอกสารรูปทับที่ไม่มีที่อยู่ตามหลัง_ต้องไม่ถูกแตะ()
    {
        // ร้านเล็กออกเลขบิลแบบ "12/68" (ลำดับ/ปี พ.ศ.) — รูปเหมือนเลขที่บ้าน
        // แต่ไม่มีคำบอกที่อยู่ตามหลัง ⇒ ต้องผ่านตามเดิม
        const string t = "ใบเสร็จรับเงิน\nเลขที่ 12/68\nรวมเงิน 500\n";
        Assert.Equal("12/68", DocumentNumberSanitizer.Sanitize("12/68", t).DocumentNumber);
    }

    [Fact]
    public void เลขที่เอกสารที่บังเอิญซ้ำกับเลขบ้านในที่อยู่_ต้องไม่ถูกทิ้ง()
    {
        // โผล่สองที่: ที่หนึ่งเป็นที่อยู่ อีกที่เป็นเลขที่เอกสารจริง ⇒ ไม่ตัดสิน
        const string t = "บริษัท ก จำกัด\n55/7 ม.2 ต.หนองขาม จ.ชลบุรี\nเลขที่ 55/7\nรวมเงิน 100\n";
        Assert.Equal("55/7", DocumentNumberSanitizer.Sanitize("55/7", t).DocumentNumber);
    }

    [Fact]
    public void When_no_anchor_distinguishes_the_halves_the_left_one_wins()
    {
        // ไม่มีป้ายช่วยตัดสิน — เอาซีกซ้าย (vision อ่านบน→ล่าง เลขหลักมาก่อน
        // บนฟอร์มไทยเสมอ) และต้อง deterministic ไม่สุ่ม
        var text = "ABCD1234\nสินค้า 1 รายการ\nWXYZ7890\n";
        var (doc, _) = DocumentNumberSanitizer.Sanitize("ABCD1234WXYZ7890", text);
        Assert.Equal("ABCD1234", doc);
    }
}
