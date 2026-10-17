using Accounting.Helpers;
using Accounting.Services;
using Accounting.Services.Ai.Distillation;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม OCR 15 (2026-10-16) — ฝ่ายค้าน OCR 13: คำท้าย " จำกัด" ข้ามคอลัมน์ (P1-1) · ชื่อผูกเลขผู้ขายสลับคอลัมน์ (P1-2) · P2 ทุกข้อ ·
/// ล็อกทั้ง "ใบที่พังกลับมาถูก" และ "ใบที่ถูกอยู่แล้วไม่ถูกแตะ" (กฎเหล็ก #4 §H) · golden ช่องคู่สัญญาของชุดกระดาษอยู่ที่ <see cref="OcrReplayPartyGoldenTests"/>
/// </summary>
public class PdfTextOcr15Tests
{
    private const string TesseractSeparator = "\n\n──── TESSERACT ────\n\n";

    private static OcrExtractedData TextPath(string text)
    {
        var combined = text + TesseractSeparator;
        var data = OcrService.ParseThaiDocument(ThaiTextNormalizer.Normalize(combined));
        SmartFieldExtractor.Enrich(data, combined);
        return data;
    }

    /// <summary>ข้อความตามพิกัดของใบ BS2026100001 (inv4/boonsap.pdf · ตัดเฉพาะหัวใบ + แถวลายเซ็น — ชื่อ/เลขอยู่ใน OcrPaperSamples แล้ว)</summary>
    private const string BoonsapPositionalHead =
        "ใบแจ้งหนี้/ใบกำกับภาษี\nต้นฉบับ\n" +
        "บริษัท บุญทรัพย์ ถาวร จำกัด (สำนักงานใหญ่)\tเลขที่\tBS2026100001\n" +
        "126/194 หมู่ 4 ตำบลบางละมุง อำเภอบางละมุง จังหวัดชลบุรี\tวันที่\t07/10/2026\n" +
        "20150\tครบกำหนด\t07/10/2026\n" +
        "เลขประจำตัวผู้เสียภาษี 0205569010244\tผู้ขาย\tภานุพงศ์ อ่วมปลั่ง\n" +
        "โทร. 081-153-542-6\tอ้างอิง\tQBS2026100003\n" +
        "ลูกค้า\nหจก.แอม แฮปปี้เนส (สำนักงานใหญ่)\n" +
        "202/24 หมู่ที่5 ซอยบ้านห้วยกุ่ม4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี\n20110\n" +
        "เลขประจำตัวผู้เสียภาษี 0203562005871\n" +
        "รวมเป็นเงิน\t21,005.00 บาท\nภาษีมูลค่าเพิ่ม 7%\t1,470.35 บาท\nจำนวนเงินรวมทั้งสิ้น\t22,475.35 บาท\n" +
        "ในนาม หจก.แอม แฮปปี้เนส\tในนาม บริษัท บุญทรัพย์ ถาวร จำกัด\n" +
        "ผู้รับสินค้า / บริการ\tวันที่\tผู้แจ้งหนี้\tวันที่";

    // ── P1-1 คำท้าย " จำกัด" ต้องอยู่ช่องเดียวกับชื่อ · ห้างหุ้นส่วนไม่ต่อคำท้าย ─────────────────────────────────

    [Theory]
    [InlineData("ในนาม หจก.แอม แฮปปี้เนส\tในนาม บริษัท บุญทรัพย์ ถาวร จำกัด", "หจก. แอม แฮปปี้เนส")]
    [InlineData("ร้าน สมใจ\tบริษัท วาย จำกัด", "ร้าน สมใจ")]
    [InlineData("ร้าน สมใจ\nบริษัท วาย จำกัด", "ร้าน สมใจ")]
    [InlineData("หจก. เอบีซี จำกัด", "หจก. เอบีซี")]
    [InlineData("ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส\tบริษัท วาย จำกัด", "ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส")]
    public void คำท้ายจำกัด_ไม่ข้ามคอลัมน์หรือบรรทัด_ห้างหุ้นส่วนไม่ต่อ(string text, string expected)
        => Assert.Equal(expected, ThaiCompanyName.FindAll(text)[0].FullName);

    [Theory]
    [InlineData("บริษัท เอ\nจำกัด\nรวม 100.00", "บริษัท เอ จำกัด")]                                         // ตัวจบของ regex ข้ามบรรทัด (เดิม)
    [InlineData("บริษัท ซีอาร์ซี ไทวัสดุ (สาขาศรีราชา) จำกัด", "บริษัท ซีอาร์ซี ไทวัสดุ จำกัด")]                   // คำท้ายบนช่องเดียวกัน (เดิม)
    [InlineData("บริษัท ทิพยประกันภัย จำกัด (มหาชน)\t1115 พระราม 3", "บริษัท ทิพยประกันภัย จำกัด (มหาชน)")]
    public void ทิศตรงข้าม_คำท้ายของชื่อตัวเองยังต่อ(string text, string expected)
        => Assert.Equal(expected, ThaiCompanyName.FindAll(text)[0].FullName);

    [Fact]
    public void ใบBS2026100001ตามพิกัด_ผู้ซื้อไม่ถูกต่อจำกัด_ผู้ขายยังผูกกับเลขผู้ขาย()
    {
        var data = TextPath(BoonsapPositionalHead);
        Assert.Equal("หจก. แอมแฮปปี้เนส", data.BuyerName);            // OCR 13 = "หจก. แอมแฮปปี้เนส จำกัด" (จำกัด ของคอลัมน์ขวา)
        Assert.Equal("0203562005871", data.BuyerTaxId);
        Assert.Equal("บริษัท บุญทรัพย์ถาวร จำกัด", data.VendorName);    // ชื่อเหนือเลขผู้ขาย คอลัมน์เดียวกัน (ป้าย "ผู้ขาย" = พนักงานขาย)
        Assert.Equal("0205569010244", data.VendorTaxId);
    }

    // ── P1-2 ชื่อผูกเลขผู้ขายต้องอยู่คอลัมน์เดียวกัน ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("\t")]
    [InlineData(" ")]
    public void หัวกระดาษสองคอลัมน์_ชื่อไม่สลับฝั่ง_ทั้งtabและเว้นวรรคเดียว(string sep)
    {
        var text = $"ใบกำกับภาษี\nผู้ขาย{sep}ผู้ซื้อ\nบริษัท เอ จำกัด{sep}บริษัท บี จำกัด\n" +
                   $"เลขประจำตัวผู้เสียภาษี 0105555021215{sep}เลขประจำตัวผู้เสียภาษี 0107538000533\nรวมเงิน 1,070.00";
        var data = TextPath(text);
        Assert.Equal("บริษัท เอ จำกัด", data.VendorName);   // OCR 13 = บริษัท บี (ชื่อท้ายบรรทัดใกล้เลขผู้ขายที่สุด)
        Assert.Equal("0105555021215", data.VendorTaxId);
        Assert.Equal("บริษัท บี จำกัด", data.BuyerName);
        Assert.Equal("0107538000533", data.BuyerTaxId);
    }

    [Fact]
    public void คอลัมน์ของชื่อกับเลข_ตัดสินจากลำดับช่อง_ตัดสินไม่ได้คืนnull()
    {
        const string tab = "บริษัท เอ จำกัด\tบริษัท บี จำกัด\nเลข 0105555021215\tเลข 0107538000533";
        var names = new[] { 0, tab.IndexOf("บริษัท บี", StringComparison.Ordinal) };
        var ids = new[] { tab.IndexOf("0105555021215", StringComparison.Ordinal), tab.IndexOf("0107538000533", StringComparison.Ordinal) };
        Assert.True(OcrVendorNameByTaxId.SameColumn(tab, names[0], names, ids[0], ids));
        Assert.False(OcrVendorNameByTaxId.SameColumn(tab, names[1], names, ids[0], ids));
        Assert.Equal("บริษัท เอ จำกัด", OcrVendorNameByTaxId.Pick(tab,
            new[] { ("บริษัท เอ จำกัด", names[0]), ("บริษัท บี จำกัด", names[1]) }, names, ids[0], ids));

        // เว้นวรรคเดียว: บรรทัดมีสองชื่อ/สองเลข ⇒ ไม่รู้คอลัมน์ ⇒ ไม่ตอบ (ผู้เรียกคงคำตอบใกล้ป้ายผู้ขาย = คำตอบของ 7ca8c0d8)
        var space = tab.Replace('\t', ' ');
        Assert.Null(OcrVendorNameByTaxId.SameColumn(space, names[1], names, ids[0], ids));
        Assert.Null(OcrVendorNameByTaxId.Pick(space,
            new[] { ("บริษัท เอ จำกัด", names[0]), ("บริษัท บี จำกัด", names[1]) }, names, ids[0], ids));

        // ทิศตรงข้าม: หัวใบเรียงลงคอลัมน์เดียว (ชื่อเดียว · เลขเดียวต่อบรรทัด) ⇒ ยังผูกชื่อกับเลข
        const string stacked = "บริษัท เอ จำกัด\nเลข 0105555021215\nลูกค้า บริษัท บี จำกัด\nเลข 0107538000533";
        var sNames = new[] { 0, stacked.IndexOf("บริษัท บี", StringComparison.Ordinal) };
        var sIds = new[] { stacked.IndexOf("0105555021215", StringComparison.Ordinal), stacked.IndexOf("0107538000533", StringComparison.Ordinal) };
        Assert.True(OcrVendorNameByTaxId.SameColumn(stacked, 0, sNames, sIds[0], sIds));
        // ชื่อกับเลขบนบรรทัดเดียวกัน = ของกันและกัน
        const string sameLine = "ชื่อ\tบริษัท เอ จำกัด\tเลขประจำตัวผู้เสียภาษี\t0105555021215";
        Assert.True(OcrVendorNameByTaxId.SameColumn(sameLine, 5, new[] { 5 }, sameLine.IndexOf("0105", StringComparison.Ordinal),
            new[] { sameLine.IndexOf("0105", StringComparison.Ordinal) }));
    }

    // ── P2 เลขตีกรอบ: ต้องมีช่องว่างจริงระหว่างกล่อง ─────────────────────────────────────────────────────

    private static List<PdfGlyph> Typed(string text, double size = 10, double advance = 0.55, double space = 0.28, double y = 700)
    {
        var list = new List<PdfGlyph>();
        var x = 50.0;
        foreach (var ch in text)
        {
            if (ch == ' ') { x += space * size; continue; }
            list.Add(new PdfGlyph(ch.ToString(), x, x + advance * size, y, size));
            x += advance * size;
        }
        return list;
    }

    [Theory]
    [InlineData("Tel 0812345678 123")]
    [InlineData("Tel 081-234-5678 123")]
    [InlineData("1234567 123456")]
    [InlineData("0 1055 55021 21 5")]
    public void ตัวเลขที่พิมพ์ติดกันปกติ_ไม่ถูกต่อเป็นเลขตีกรอบ(string printed)
        => Assert.Equal(printed, PdfPositionalText.Build(Typed(printed)));

    // ── P2 ProseStarts / วงเล็บในชื่อจดทะเบียน ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("บริษัท ยินดีมีสุข จำกัด", "บริษัท ยินดีมีสุข จำกัด")]
    [InlineData("บริษัท โตโยต้า มอเตอร์ (ประเทศไทย) จำกัด", "บริษัท โตโยต้า มอเตอร์ (ประเทศไทย) จำกัด")]
    [InlineData("บริษัท เอบีซี (Thailand) จำกัด", "บริษัท เอบีซี (Thailand) จำกัด")]
    [InlineData("บริษัท พรามี่ ควอลิตี้ (ไทยแลนด์) จำกัด", "บริษัท พรามี่ ควอลิตี้ (ไทยแลนด์) จำกัด")]
    [InlineData("บริษัท บุญทรัพย์ ถาวร จำกัด (สำนักงานใหญ่)", "บริษัท บุญทรัพย์ ถาวร จำกัด")]   // ทิศตรงข้าม: วงเล็บสาขายังเป็นจุดหยุด
    [InlineData("บริษัท เอ (สาขาที่ 00002)\tเลขที่ 1", "บริษัท เอ")]
    public void ชื่อจดทะเบียน_วงเล็บประเทศไทยอยู่ในชื่อ_วงเล็บสาขาไม่ใช่(string text, string expected)
        => Assert.Equal(expected, Assert.Single(ThaiCompanyName.FindAll(text)).FullName);

    [Theory]
    [InlineData("บริษัทยินดีให้บริการท่านเสมอ")]
    [InlineData("บริษัท ยินดีต้อนรับ")]
    public void ประโยคยินดีให้บริการ_ไม่ใช่ชื่อ(string text) => Assert.Empty(ThaiCompanyName.FindAll(text));

    [Fact]
    public void ตัวอ่านคู่สัญญาโซนสำรอง_เก็บวงเล็บประเทศไทย()
        => Assert.Contains("บริษัท โตโยต้า มอเตอร์ (ประเทศไทย) จำกัด",
            FieldPatternLibrary.ExtractCandidates("บริษัท โตโยต้า มอเตอร์ (ประเทศไทย) จำกัด\nรวม 100.00", FieldPatternLibrary.FieldType.CompanyName).Select(c => c.Value));

    // ── P2 ลายนิ้วมือโครงกระดาษ: รูปอักขระรุ่นต่าง ๆ ได้คีย์เดียวกัน ────────────────────────────────────────

    [Fact]
    public void ลายนิ้วมือโครงกระดาษ_นิคหิตสระอา_ำา_อักขระควบคุม_ได้คีย์เดียวกับรูปปกติ()
    {
        const string plain = "ใบกำกับภาษี บริษัท บุญทรัพย์ ถาวร จำกัด ตำบลบางละมุง อำเภอบางละมุง จังหวัดชลบุรี ลูกค้า หจก.แอม แฮปปี้เนส สายไฟ ยาซากิ เมตร รวมเป็นเงิน";
        var legacy = plain.Replace("ำ", "ํา").Replace("ใบ", "ใ\0บ");
        var ocr10 = plain.Replace("จำกัด", "จำากัด").Replace("ตำบล", "ตำาบล");
        var fp = LineSplitDistillationModel.ShapeFingerprint(plain);
        Assert.NotEqual("", fp);
        Assert.Equal(fp, LineSplitDistillationModel.ShapeFingerprint(legacy));
        Assert.Equal(fp, LineSplitDistillationModel.ShapeFingerprint(ocr10));
        // ทิศตรงข้าม: ข้อความคนละโครงยังได้คีย์ต่างกัน
        Assert.NotEqual(fp, LineSplitDistillationModel.ShapeFingerprint(plain.Replace("สายไฟ", "ท่อพีวีซี")));
    }

    // ── P2 ยอดมัดจำต้องเป็นรูปเงิน ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ใบกำกับภาษี\nสินค้า A 1,000.00\nรวมเงิน 1,000.00\nเงื่อนไข: ยกเลิกหลังชำระ ไม่คืนเงินมัดจำ 30 วัน")]
    [InlineData("ใบเสร็จรับเงิน\nรวม 500.00\nมัดจำ 50% ก่อนส่งของ")]
    [InlineData("ใบเสร็จรับเงิน\nเงินมัดจำ\n2")]
    public void คำว่ามัดจำกับเลขจำนวนเต็มเปล่า_ไม่ใช่ใบมัดจำ(string text)
        => Assert.False(OcrDepositMarker.Decide(text, null).IsDeposit);

    [Theory]
    [InlineData("ใบรับเงินมัดจำ\nเงินมัดจำค่าก่อสร้าง 50,000.00")]
    [InlineData("รับเงินมัดจำ 5,000")]
    [InlineData("รับเงินมัดจำ 5000 บาท")]
    [InlineData("ค่ามัดจำ ฿500")]
    [InlineData("เงินมัดจำ\n1,500.00")]
    public void ทิศตรงข้าม_ยอดมัดจำรูปเงิน_ยังติดธง(string text)
        => Assert.True(OcrDepositMarker.Decide(text, null).IsDeposit);

    // ── P2 ที่อยู่ผู้ขายหยุดที่บรรทัดป้ายผู้ซื้อ ─────────────────────────────────────────────────────────────

    [Fact]
    public void ที่อยู่ผู้ขาย_หยุดที่บรรทัดป้ายผู้ซื้อ()
    {
        Assert.Equal("1 ถนนหนึ่ง กรุงเทพฯ 10110",
            OcrVendorAddressReader.Read("ผู้ขาย: บริษัท เอ จำกัด\nที่อยู่ 1 ถนนหนึ่ง กรุงเทพฯ 10110\nผู้ซื้อ: บริษัท บี จำกัด\nเลขประจำตัวผู้เสียภาษี 0105555021215"));
        // ทิศตรงข้าม: ที่อยู่สองบรรทัดที่ไม่มีป้ายผู้ซื้อ ยังต่อครบ
        Assert.Equal("1 ถนนหนึ่ง แขวงคลองเตย กรุงเทพฯ 10110",
            OcrVendorAddressReader.Read("บริษัท เอ จำกัด\nที่อยู่ 1 ถนนหนึ่ง\nแขวงคลองเตย กรุงเทพฯ 10110\nโทร 02-111-1111"));
    }
}
