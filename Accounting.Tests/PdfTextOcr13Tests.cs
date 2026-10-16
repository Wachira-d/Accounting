using Accounting.Helpers;
using Accounting.Services;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม OCR 13 (2026-10-16) — ฝ่ายค้าน OCR 10: PDF จริง 24 ไฟล์ผ่านข้อความเดิม (page.Text) กับข้อความตามพิกัด แล้วช่องที่เคยอ่านได้กลับแย่ลง ·
/// เทสต์ล็อกทั้ง "ใบที่พังกลับมาถูก" และ "ใบที่ถูกอยู่แล้วไม่ถูกแตะ" (กฎเหล็ก #4 §H) · ตัวเทียบ PDF จริงทั้งชุดอยู่ที่ <see cref="OcrPdfTextHarness"/>
/// (PDF ไม่อยู่ในเรพ) · พิกัดในเทสต์วัดจากกระดาษจริง (50 ทวิ ที่ระบบเราสร้าง — HeadlessChrome)
/// </summary>
public class PdfTextOcr13Tests
{
    private const string TesseractSeparator = "\n\n──── TESSERACT ────\n\n";

    private static (OcrExtractedData Data, string Combined) TextPath(string pdfText)
    {
        var combined = pdfText + TesseractSeparator;
        var data = OcrService.ParseThaiDocument(ThaiTextNormalizer.Normalize(combined));
        SmartFieldExtractor.Enrich(data, combined);
        var (cleanDocNo, note) = DocumentNumberSanitizer.Sanitize(data.DocumentNumber, combined);
        if (note != null) data.DocumentNumber = cleanDocNo;
        return (data, combined);
    }

    private static IEnumerable<PdfGlyph> Run(string text, double x, double y, double size = 10, string? font = null, double advance = 0.5)
    {
        foreach (var ch in text)
        {
            var mark = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark
                       || (ch >= '\uF700' && ch <= '\uF71A');   // PUA ไทยกว้าง 0 อย่างที่ PdfPig ให้
            var w = mark ? 0 : advance * size;
            yield return new PdfGlyph(ch.ToString(), x, x + w, y, size, true, font);
            x += w;
        }
    }

    // ── P1-1 เลขในช่องตีกรอบ (50 ทวิ / แบบฟอร์มกรมสรรพากร) ─────────────────────────────────────────────

    /// <summary>พิกัดจริง 50 ทวิ (r204/after_w53.pdf · ขนาด 9.8): หลักกว้าง 5.5 · ระยะหลักถึงหลัก 12.0 · ข้ามกลุ่มผ่านขีด 15.7 · ขีดกว้าง 2.8</summary>
    private static List<PdfGlyph> BoxedTaxId(string grouped, double y = 600)
    {
        var glyphs = Run("เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)*", 237.6, y, 9.8).ToList();
        var x = 403.0;
        var prevDigit = false;
        foreach (var ch in grouped)
        {
            if (ch == '-')
            {
                glyphs.Add(new PdfGlyph("-", x - 12.0 + 5.5 + 3.7, x - 12.0 + 5.5 + 3.7 + 2.8, y, 9.8));
                x += 3.7;   // ข้ามกลุ่ม 15.7 = 12.0 + 3.7
                prevDigit = false;
                continue;
            }
            glyphs.Add(new PdfGlyph(ch.ToString(), x, x + 5.5, y, 9.8));
            x += 12.0;
            prevDigit = true;
        }
        _ = prevDigit;
        return glyphs;
    }

    [Fact]
    public void เลขในช่องตีกรอบ13หลัก_ต่อกลับเป็นก้อนเดียว_ตัวอ่านเลขผู้เสียภาษีเห็นเลข()
    {
        var text = PdfPositionalText.Build(BoxedTaxId("0-1075-38000-53-3"))!;
        Assert.EndsWith("0-1075-38000-53-3", text);
        Assert.Contains(SmartFieldExtractor.ExtractValidThaiTaxIds(text), t => t.Id == "0107538000533");
    }

    [Fact]
    public void ทิศตรงข้าม_ตัวเลขเว้นช่องที่ไม่ครบ13หลัก_หรือระยะไม่สม่ำเสมอ_ไม่ถูกต่อ()
    {
        // 12 หลัก (เลขบัตร/บัญชีที่ไม่ใช่เลขผู้เสียภาษี) — คงช่องว่างตามพิกัด
        Assert.Contains("0 - 1 0 7 5", PdfPositionalText.Build(BoxedTaxId("0-1075-38000-53"))!);
        // ตัวเลขทีละหลักแต่ระยะห่างไม่สม่ำเสมอ (ตารางตัวเลขเล็ก ๆ) — ไม่ต่อ
        var uneven = new List<PdfGlyph>();
        var x = 100.0;
        for (var i = 0; i < 13; i++)
        {
            uneven.Add(new PdfGlyph(((i * 7) % 10).ToString(), x, x + 5, 300, 10));
            x += i % 2 == 0 ? 7 : 16;
        }
        Assert.Contains(" ", PdfPositionalText.Build(uneven)!);
        // ตัวเลขที่พิมพ์ติดกันปกติ ไม่ถูกแตะ
        Assert.Equal("0105560113122", PdfPositionalText.Build(Run("0105560113122", 100, 300).ToList()));
    }

    // ── P2 ตัวหนาวาดทับ: ฟอนต์เดียวกัน · ความคลาดเทียบความกว้างตัวเอง ──────────────────────────────────

    [Fact]
    public void ตัวเลขadvanceแคบมาก_ไม่ถูกตัดเป็นตัวซ้ำ_ตัวหนาวาดทับยังถูกตัด()
    {
        // advance 0.09 em — "0" ตัวถัดไปอยู่ห่างแค่ 0.09 × ขนาด (เดิมเกณฑ์ 0.1 × ขนาด ⇒ "2,00.0")
        Assert.Equal("2,000.00", PdfPositionalText.Build(Run("2,000.00", 100, 300, 10, advance: 0.09).ToList()));
        // ตัวหนาวาดทับตำแหน่งเดิม ฟอนต์เดียวกัน — เก็บตัวเดียว (กฟภ.)
        var bold = Run("4,272.00", 400, 503, 14, "THSarabunNew").Concat(Run("4,272.00", 400, 503, 14, "THSarabunNew")).ToList();
        Assert.Equal("4,272.00", PdfPositionalText.Build(bold));
        // ทิศตรงข้าม: คนละฟอนต์ที่ตำแหน่งเดียวกัน ไม่ใช่ตัวหนาวาดทับ ⇒ ไม่ตัด
        var twoFonts = Run("1", 400, 503, 14, "A").Concat(Run("1", 400, 503, 14, "B")).ToList();
        Assert.Equal("11", PdfPositionalText.Build(twoFonts));
    }

    // ── P2 PUA: แปลงเป็นไทยเฉพาะฟอนต์ที่วาดอักษรไทย · อื่น ๆ ตัดและนับ ────────────────────────────────

    [Fact]
    public void PUAของไอคอนฟอนต์_ไม่กลายเป็นอักษรไทย_ตัดออกและนับ_PUAของฟอนต์ไทยยังแปลง()
    {
        var glyphs = Run("แผน", 10, 100, 10, "Sarabun")
            .Concat(new[] { new PdfGlyph("", 10, 15, 80, 10, true, "FontAwesome5"), new PdfGlyph("", 16, 21, 80, 10, true, "FontAwesome5") })
            .Concat(Run("085-914-8140", 30, 80, 10, "Sarabun")).ToList();
        var layout = PdfPositionalText.Layout(glyphs);
        Assert.Equal("แผ่น\n085-914-8140", layout.Text);   // เดิม "ฐ" + ไม้เอกลอย หน้าเบอร์โทร
        Assert.Equal(2, layout.PuaDropped);
        Assert.DoesNotContain("ฐ", layout.Text);
    }

    [Fact]
    public void อักขระควบคุมจากฟอนต์ไม่มีToUnicode_ถูกตัด_ไม่แทรกกลางคำ()
    {
        // PDF HeadlessChrome: วรรณยุกต์ถอดเป็น "\0" (กว้าง 0 ที่ x ของตัวถัดไป) ⇒ เดิม "ใ\0บกำกับภาษี" ⇒ ชนิดเอกสารหลุดจากใบกำกับภาษี
        var glyphs = new List<PdfGlyph> { new("ใ", 10, 14, 50, 10), new("\0", 14, 14, 50, 10), new("บ", 14, 19, 50, 10) };
        Assert.Equal("ใบ", PdfPositionalText.Build(glyphs));
    }

    // ── P2 ทางถอยเงียบ ⇒ เหตุผลไปถึง trace ─────────────────────────────────────────────────────

    [Fact]
    public void ประกอบไม่ได้_บอกเหตุผล_ไม่เงียบ()
    {
        Assert.Equal("ไม่มี glyph บนหน้า", PdfPositionalText.Layout(null).FallbackReason);
        var rotated = Run("ROTATED", 10, 10).Select(g => g with { Horizontal = false }).Concat(Run("ok", 10, 50)).ToList();
        var r = PdfPositionalText.Layout(rotated);
        Assert.Null(r.Text);
        Assert.Contains("หน้าหมุน", r.FallbackReason);
        Assert.Null(PdfPositionalText.Layout(Run("ok text", 10, 50).ToList()).FallbackReason);
    }

    // ── P1-4 คอลัมน์: tab รอด Normalize · ตัวอ่านชื่อหยุดที่คอลัมน์ ───────────────────────────────────

    [Fact]
    public void ตัวคั่นคอลัมน์เป็นtab_รอดNormalize_ชื่อไม่กลืนคอลัมน์ขวา()
    {
        var glyphs = Run("หจก.แอม แฮปปี้เนส", 100, 500, 10).Concat(Run("เลขที่/ No.", 400, 500, 10)).ToList();
        var text = PdfPositionalText.Build(glyphs)!;
        Assert.Equal("หจก.แอม แฮปปี้เนส\tเลขที่/ No.", text);
        var normalized = ThaiTextNormalizer.Normalize(text);
        Assert.Contains("\t", normalized);   // เดิมสองช่องว่าง ⇒ Normalize ตัดทิ้ง ⇒ "แฮปปี้เนสเลขที่"
        Assert.Equal("หจก. แอมแฮปปี้เนส", Assert.Single(ThaiCompanyName.FindAll(normalized)).FullName);
    }

    // ── P1-5 ชื่อนิติบุคคล: ตัวตั้งตัวเดียว ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส\nที่อยู่ 202/24", "ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส")]
    [InlineData("บริษัท ทิพยประกันภัย จำกัด (มหาชน)\n1115 พระราม 3", "บริษัท ทิพยประกันภัย จำกัด (มหาชน)")]
    [InlineData("บริษัท ซีอาร์ซี ไทวัสดุ จำกัด (สาขาศรีราชา)\tเลขที่ SRCIE26100075384", "บริษัท ซีอาร์ซี ไทวัสดุ จำกัด")]
    [InlineData("หจก.แอม แฮปปี้เนส เลขประจำตัวผู้เสียภาษี 0203562005871", "หจก. แอม แฮปปี้เนส")]
    public void ชื่อนิติบุคคล_คำท้ายจำกัดเฉพาะหลังชื่อ_หยุดที่ป้ายและคอลัมน์(string text, string expected)
        => Assert.Equal(expected, Assert.Single(ThaiCompanyName.FindAll(text)).FullName);

    [Theory]
    [InlineData("• บริษัทขอสงวนสิทธิ์ในการรับเปลี่ยน/คืนสินค้าภายใน 30 วัน")]
    [InlineData("มิฉะนั้นบริษัทฯ จะถือว่าเอกสารฉบับนี้ถูกต้องและสมบูรณ์")]
    [InlineData("มิฉะนั้นบริษัทจะไม่รับผิดชอบใดๆทั้งสิ้น")]
    public void ประโยคเงื่อนไขท้ายใบที่ขึ้นด้วยคำว่าบริษัท_ไม่ใช่ชื่อนิติบุคคล(string text)
        => Assert.Empty(ThaiCompanyName.FindAll(text));

    [Fact]
    public void ทิศตรงข้าม_ชื่อจริงที่ขึ้นต้นคล้ายคำในประโยค_ยังเป็นชื่อ()
    {
        Assert.Equal("บริษัท มีเดีย สตูดิโอ จำกัด", Assert.Single(ThaiCompanyName.FindAll("บริษัท มีเดีย สตูดิโอ จำกัด")).FullName);
        Assert.Equal("บริษัท ขอนแก่นบริการ จำกัด", Assert.Single(ThaiCompanyName.FindAll("บริษัท ขอนแก่นบริการ จำกัด")).FullName);
    }

    [Fact]
    public void ตัวอ่านโซนสำรอง_ห้างหุ้นส่วนจำกัด_ไม่ถูกต่อจำกัดซ้ำ()
    {
        // DocumentZoneAnalyzer.FallbackCompanyExtraction (ทาง zone fallback ที่ทำงานจริง) — เดิม "ห้างหุ้นส่วนจำกัด แอมแฮปปี้เนส จำกัด"
        var r = DocumentZoneAnalyzer.Analyze("ห้างหุ้นส่วนจำกัด แอมแฮปปี้เนส\n202/24 หมู่ 5\nบริษัท ทดสอบ จำกัด\nรวม 100.00");
        Assert.Equal("ห้างหุ้นส่วนจำกัด แอมแฮปปี้เนส", r.SellerName);
        Assert.Equal("บริษัท ทดสอบ จำกัด", r.BuyerName);
    }

    // ── P1-2 ผู้ออกบิลหน่วยงานรัฐ: ห้ามแทนเมื่อมีป้ายผู้ซื้อนำหน้า · กุญแจ = เลข 099 ─────────────────────

    [Fact]
    public void หน่วยงานรัฐที่มีป้ายลูกค้านำหน้า_เป็นผู้ซื้อ_ผู้ขายยังเป็นผู้รับเหมา()
    {
        var (data, _) = TextPath("ใบกำกับภาษี\nลูกค้า: การไฟฟ้าส่วนภูมิภาค\nเลขประจำตัวผู้เสียภาษี 0994000165501\nรายการ ค่าแรง 1,000.00\n"
            + "บริษัท ซีอาร์ซี ไทวัสดุ จำกัด เลขประจำตัวผู้เสียภาษี 0105555021215\nรวมทั้งสิ้น 1,070.00");
        Assert.NotEqual("การไฟฟ้าส่วนภูมิภาค", data.VendorName);
        Assert.DoesNotContain(data.ReasoningTrace, t => t.Contains("ผู้ออกบิลบนหัวกระดาษ", StringComparison.Ordinal));
    }

    [Fact]
    public void หัวจดหมายเรียนหน่วยงานรัฐ_ไม่แทนชื่อผู้ขาย()
    {
        var (data, _) = TextPath("เรียน การไฟฟ้าส่วนภูมิภาค\nเลขประจำตัวผู้เสียภาษี 0994000165501\nใบแจ้งหนี้\nบริษัท ซีอาร์ซี ไทวัสดุ จำกัด\n"
            + "เลขประจำตัวผู้เสียภาษี 0105555021215\nรวมทั้งสิ้น 1,070.00");
        Assert.NotEqual("การไฟฟ้าส่วนภูมิภาค", data.VendorName);
    }

    [Fact]
    public void ทิศตรงข้าม_บิลกฟภจริง_ผู้ออกบิลยังเป็นการไฟฟ้า()
        => Assert.Equal("การไฟฟ้าส่วนภูมิภาค", TextPath(OcrPaperSamples.PeaElectricityBill0869Positional).Data.VendorName);

    [Fact]
    public void หน่วยงานรัฐบนหัวแต่เลขแรกบนหน้าไม่ใช่เลขรัฐ_ไม่แทน()
    {
        // กุญแจ = เลขผู้เสียภาษี: หัวกระดาษพิมพ์ชื่อรัฐ (เช่นโลโก้ผู้ให้ใช้สิทธิ์) แต่เลขตัวแรกเป็นของบริษัท ⇒ ไม่มีหลักฐานว่ารัฐเป็นผู้ออก
        var (data, _) = TextPath("การไฟฟ้าส่วนภูมิภาค ตัวแทนรับชำระ\nใบเสร็จรับเงิน\nเลขประจำตัวผู้เสียภาษี 0105555021215\nบริษัท ซีอาร์ซี ไทวัสดุ จำกัด\nรวมทั้งสิ้น 100.00");
        Assert.NotEqual("การไฟฟ้าส่วนภูมิภาค", data.VendorName);
    }

    // ── P1-3 ตารางบางแถว ⇒ เก็บแบบที่ Σ ตรงยอด ─────────────────────────────────────────────────

    [Fact]
    public void ช่องส่วนลดว่าง_แถวยังอ่านได้_ไม่ตกทั้งใบ()
    {
        const string text = "รายการ\tจำนวน\tราคา\tส่วนลด\tจำนวนเงิน\nสินค้า A\t2\t100.00\t10.00\t190.00\nสินค้า B\t1\t300.00\t300.00\nรวมทั้งสิ้น\t490.00";
        var rows = RawTextLineSplitter.Split(text, null, 490.00m);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new RawTextLineSplitter.Candidate("สินค้า B", 1m, null, 300.00m, 300.00m), rows[1]);
        Assert.True(OcrLineSplitGuard.Evaluate(RawTextLineSplitter.SplitToJson(text, null, 490.00m), 0m, 490.00m).Accepted);
    }

    [Fact]
    public void ยอดมีคำว่าบาท_และจำนวนรวมหน่วย_แถวยังอ่านได้()
    {
        const string text = "รายการ\tจำนวน\tราคา\tจำนวนเงิน\nปากกา\t2 ชิ้น\t500.00\t1,000.00 บาท\nสมุด\t1\t250.00\t250.00 บาท\nรวมทั้งสิ้น\t1,250.00";
        var rows = RawTextLineSplitter.Split(text, null, 1250.00m);
        Assert.Equal(new RawTextLineSplitter.Candidate("ปากกา", 2m, "ชิ้น", 500.00m, 1000.00m), rows[0]);
        Assert.Equal(1250.00m, rows.Sum(r => r.Amount));
    }

    [Fact]
    public void ช่องจำนวนกับหน่วยรวมกัน_ใต้หัวที่แยกคอลัมน์หน่วย_วางช่องด้วยผลคูณ()
    {
        const string text = "รายการ\tจำนวน\tหน่วย\tราคา\tจำนวนเงิน\nน็อต\t4 ตัว\t25.00\t100.00\nสกรู\t2\tกล่อง\t50.00\t100.00\nรวมทั้งสิ้น\t200.00";
        var rows = RawTextLineSplitter.Split(text, null, 200.00m);
        Assert.Equal(new RawTextLineSplitter.Candidate("น็อต", 4m, "ตัว", 25.00m, 100.00m), rows[0]);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ตารางอ่านได้บางแถวและไม่ตรงยอด_ถอยไปกติกาท้ายบรรทัดที่ตรงยอด()
    {
        // แถวที่สองมีข้อความในช่องตัวเลข ⇒ ตารางได้แถวเดียว (Σ 190 ≠ 490) · กติกาท้ายบรรทัดบนข้อความเดียวกันได้สองแถว Σ 490
        const string text = "รายการ\tจำนวน\tราคา\tจำนวนเงิน\nสินค้า A\t2\t95.00\t190.00\nสินค้า B ชุดใหญ่\tหนึ่ง\tสามร้อย\t300.00\nรวมทั้งสิ้น\t490.00";
        Assert.Single(RawTextLineSplitter.TableRows(text));
        var rows = RawTextLineSplitter.Split(text, null, 490.00m);
        Assert.Equal(490.00m, rows.Sum(r => r.Amount));
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ทิศตรงข้าม_ตารางที่ตรงยอดอยู่แล้ว_ไม่ถูกแทนด้วยกติกาท้ายบรรทัด()
    {
        var rows = RawTextLineSplitter.Split(OcrPaperSamples.PramieRe111Positional, 969.16m, 1037.00m);
        var row = Assert.Single(rows);
        Assert.Equal(1330.00m, row.UnitPrice);   // แถวตาราง (กติกาท้ายบรรทัดได้ราคา = ยอด)
    }

    [Fact]
    public void ตัวเลขจัดกึ่งกลางระหว่างคำอธิบายสองบรรทัด_ติดคำอธิบายทั้งสองบรรทัด()
    {
        const string text = "รายการ\tจำนวน\tราคา\tจำนวนเงิน\nค่าบริการติดตั้ง\n1\t500.00\t500.00\nระบบไฟฟ้าชั้น 2\nรวมทั้งสิ้น\t500.00";
        var row = Assert.Single(RawTextLineSplitter.Split(text, null, 500.00m));
        Assert.Equal("ค่าบริการติดตั้ง ระบบไฟฟ้าชั้น 2", row.Description);
        Assert.Equal(500.00m, row.Amount);
    }

    [Fact]
    public void คำอธิบายมีอักขระควบคุม_JSONยังอ่านได้()
    {
        var json = RawTextLineSplitter.SplitToJson("ตัวยึดท่อ HACO ชิ\0น 148.53");
        Assert.True(OcrLineSplitGuard.Evaluate(json, 0m, 148.53m).Accepted);
    }

    // ── P2 PrintedZero ยอมให้ตัวอ่านยอดหัก ─────────────────────────────────────────────────────

    [Fact]
    public void ป้ายหักมียอดบรรทัดถัดไป_และอีกป้ายพิมพ์ศูนย์_ไม่นับว่าพิมพ์ศูนย์()
    {
        const string text = "ภาษีหัก ณ ที่จ่าย\n3%  30.00\nยอดชำระ 970.00\nจำนวนเงินที่ถูกหัก ณ ที่จ่าย 0.00 บาท";
        Assert.Equal(30.00m, PaperWhtReader.Read(text).Amount);
        Assert.False(PaperWhtReader.PrintedZero(text));
        var data = OcrService.ParseThaiDocument(ThaiTextNormalizer.Normalize(text));
        Assert.True(data.HasWht);
    }

    [Fact]
    public void ทิศตรงข้าม_ใบพรามี่พิมพ์ศูนย์_ยังไม่ติ๊กหัก()
        => Assert.True(PaperWhtReader.PrintedZero(ThaiTextNormalizer.Normalize(OcrPaperSamples.PramieRe111Positional)));

    [Fact]
    public void ทิศตรงข้าม_ป้ายที่อยู่ของผู้ขายบนหัวใบ_ยังอ่านได้_คอลัมน์ขวาถูกตัด()
    {
        var plain = OcrService.ParseThaiDocument(ThaiTextNormalizer.Normalize(
            "ร้านวัสดุดี\nที่อยู่: 99 ถนนสุขุมวิท ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110\nโทร 038-111-222\nลูกค้า: หจก. ทดสอบ"));
        Assert.Equal("99 ถนนสุขุมวิทต.บางพระอ.ศรีราชาจ.ชลบุรี 20110", plain.VendorAddress);   // Normalize ยุบช่องว่างระหว่างอักษรไทย (กติกาเดิม)
        var columns = OcrService.ParseThaiDocument(ThaiTextNormalizer.Normalize(
            "บริษัท ทดสอบ จำกัด\nที่อยู่\t99 ถนนสุขุมวิท ต.บางพระ จ.ชลบุรี 20110\tวันที่ 18/09/2026\nโทร 038-111-222"));
        Assert.Equal("99 ถนนสุขุมวิทต.บางพระจ.ชลบุรี 20110", columns.VendorAddress);
    }

    [Fact]
    public void ตัวอ่านที่อยู่ผู้ขาย_ป้ายในบล็อกผู้ซื้อ_ไม่ตอบ_หลายบรรทัดคงขึ้นบรรทัดเมื่อขอ()
    {
        Assert.Null(OcrVendorAddressReader.Read("ชื่อลูกค้า/ Customer name\tหจก.แอม\nที่อยู่/ Address\t202/24 ตำบลบางพระ 20110\tวันที่/ Date\t18/09/2026\nเลขประจำตัวผู้เสียภาษี 0203562005871"));
        const string text = "ร้านดี\nที่อยู่: 99 ถนนสุขุมวิท\nต.บางพระ จ.ชลบุรี 20110\tเลขที่ 001\nโทร 038-111-222";
        Assert.Equal("99 ถนนสุขุมวิท ต.บางพระ จ.ชลบุรี 20110", OcrVendorAddressReader.Read(text));
        Assert.Equal("99 ถนนสุขุมวิท\nต.บางพระ จ.ชลบุรี 20110", OcrVendorAddressReader.Read(text, keepLineBreaks: true));
        // ทิศตรงข้าม: ป้ายผู้ซื้ออยู่ใต้ที่อยู่ผู้ขาย ⇒ ยังเป็นที่อยู่ผู้ขาย
        Assert.Equal("99 ถนนสุขุมวิท", OcrVendorAddressReader.Read("ร้านดี\nที่อยู่: 99 ถนนสุขุมวิท\nโทร 038\nลูกค้า: หจก. ทดสอบ"));
    }

    [Fact]
    public void ใบมัดจำจริง_บรรทัดคั่นคอลัมน์_ยังติดธงมัดจำ_ป้ายหักมัดจำศูนย์ไม่ติด()
    {
        Assert.True(OcrDepositMarker.Decide("ใบเสร็จรับเงิน\nรับเงินมัดจำ\t5,000.00\tวันที่\t01/10/2026", null).IsDeposit);
        Assert.False(OcrDepositMarker.Decide("ภาษีมูลค่าเพิ่ม\t209.41 บาท\tหักเงินมัดจำ\t0.00\nรวมทั้งสิ้น\t3,201.00 บาท\tขายสุทธิหลังหักมัดจำ\t3,201.00", null).IsDeposit);
        // ทิศตรงข้าม: ข้อความไม่มี tab (Tesseract/Azure) — ทั้งบรรทัดเหมือนเดิม
        Assert.True(OcrDepositMarker.Decide("ค่ามัดจำห้องพัก 1,000.00", null).IsDeposit);
    }

    // ── กระดาษจริง: ใบ CRC ไทวัสดุ · 50 ทวิ · e-Tax Scommerce (PDF อยู่ในเรพแล้ว) ──────────────────────────────

    [Fact]
    public void ใบCRCไทวัสดุจริง_ผู้ซื้อกลับมาถูก_ส่วนที่OCR10ทำดีขึ้นคงอยู่()
    {
        var (data, combined) = TextPath(OcrPaperSamples.CrcThaiwatsaduSrcie75384Positional);
        // ผู้ซื้อ: รุ่น OCR 10 = "บริษัท จะไม่รับผิดชอบใดๆทั้งสิ้น Date : …" (ประโยคเงื่อนไข) · page.Text เดิม = ชื่อนี้
        Assert.Equal("ห้างหุ้นส่วนจำกัด แอมแฮปปี้เนส", data.BuyerName);
        Assert.Equal("0203562005871", data.BuyerTaxId);   // ป้าย "เลขที่ประจำตัวผู้เสียภาษี" นับเป็นป้ายแล้ว (เดิมว่างทั้งสองรุ่น)
        Assert.Equal("บริษัท ซีอาร์ซีไทวัสดุ จำกัด", data.VendorName);
        Assert.Equal("0105555021215", data.VendorTaxId);
        Assert.Equal("00050", data.VendorBranchCode);
        // ส่วนที่ OCR 10 ทำให้ดีขึ้น (page.Text ได้ 201.00 · เลขที่ 900030696 · 0 บรรทัด)
        Assert.Equal("SRCIE26100075384", data.DocumentNumber);
        Assert.Equal(3201.00m, data.TotalAmount);
        var json = RawTextLineSplitter.SplitToJson(combined, data.SubTotal, data.TotalAmount);
        var guard = OcrLineSplitGuard.Evaluate(json, data.SubTotal ?? 0m, data.TotalAmount ?? 0m);
        Assert.True(guard.Accepted, guard.Reason);
        Assert.Equal(9, guard.Lines.Count);
    }

    [Fact]
    public void หนังสือรับรอง50ทวิจริง_เลขผู้จ่ายผู้รับอ่านได้_ยอดคือจำนวนเงินที่จ่าย()
    {
        var (data, _) = TextPath(OcrPaperSamples.WhtCert50TawiW53Positional);
        Assert.Equal("WHT", data.DocumentType);
        // เลขในช่องตีกรอบ: รุ่น OCR 10 ว่างทั้งคู่ · page.Text เดิมได้สองตัวนี้
        Assert.Equal("0203562005871", data.VendorTaxId);   // ผู้มีหน้าที่หัก (หจก. แอม แฮปปี้เนส)
        Assert.Equal("0107538000533", data.BuyerTaxId);    // ผู้ถูกหัก
        Assert.Equal("บริษัท ทิพยประกันภัย จำกัด (มหาชน)", data.BuyerName);
        // ยอดของเอกสาร = "จำนวนเงินที่จ่าย" 7,264.00 (แถวรวม) ไม่ใช่ภาษีที่หัก 72.64 (คำตัดสินจากกระดาษ — ตัวอักษรท้ายแถวเป็นคำอ่านของยอดภาษี)
        Assert.Equal(7264.00m, data.TotalAmount);
        Assert.DoesNotContain("สมาคม", data.VendorName ?? "");
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative))) dir = dir.Parent;
        Assert.True(dir != null, "หาไฟล์ในเรพไม่เจอ: " + relative);
        return Path.Combine(dir!.FullName, relative);
    }

    [Fact]
    public void eTaxScommerceจริง_ชื่อและที่อยู่ไม่กลืนคอลัมน์ขวา()
    {
        var pdf = File.ReadAllBytes(RepoFile("erp-review/2026-09-25/ocr-scommerce/TXE05202609T004679.pdf"));
        var r = PdfTextLayerExtractor.TryExtract(pdf);
        Assert.True(r.HasUsableText, r.Reason);
        Assert.Contains("ชื่อลูกค้า/ Customer name\tหจก.แอม แฮปปี้เนส\tเลขที่/ No.", r.Text);
        var (data, _) = TextPath(r.Text);
        // รุ่น OCR 10: ผู้ขาย = "หจก. แอมแฮปปี้เนสเลขที่/ No. TXE05202609T004679" · ที่อยู่ผู้ซื้อ = "18/09/2026 สาขา/ Branch …"
        Assert.DoesNotContain("แอม", data.VendorName ?? "");          // ชื่อหลังป้ายลูกค้าไม่ใช่ผู้ขาย (ผู้ขายชื่ออังกฤษ — ตัวอ่านชื่อไทยไม่รู้จัก ⇒ ว่าง)
        Assert.DoesNotContain("TXE", data.VendorName ?? "");
        Assert.Equal("หจก. แอมแฮปปี้เนส", data.BuyerName);
        Assert.Equal("0203562005871", data.BuyerTaxId);
        Assert.Equal("0105560113122", data.VendorTaxId);
        Assert.Equal("00005", data.VendorBranchCode);
        Assert.DoesNotContain("18/09/2026", data.BuyerAddress ?? "");
        Assert.DoesNotContain("202/24", data.VendorAddress ?? "");     // ที่อยู่ใต้ป้ายลูกค้าไม่ใช่ของผู้ขาย
        Assert.Equal(5024.00m, data.TotalAmount);
        Assert.DoesNotContain("ลำดับ stream", r.Reason);
    }
}
