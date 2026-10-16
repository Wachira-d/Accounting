using System.Diagnostics;
using Accounting.Helpers;
using Accounting.Services;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม OCR 10 (2026-10-16) — <b>เส้นข้อความ PDF (Tier 3 hybrid) บนข้อความที่ประกอบตามพิกัด</b> ของกระดาษจริงสองใบ
/// (<see cref="OcrPaperSamples.PeaElectricityBill0869Positional"/> · <see cref="OcrPaperSamples.PramieRe111Positional"/>) + ตัวอ่านหัวใบที่แก้ในรอบนี้
///
/// <para>ลำดับเท่า <c>OcrService.ProcessScanAsync</c> เส้น PDF text-layer (ส่วนที่ไม่ต้องมี DB): ข้อความ PDF + ตัวคั่น Tesseract (Tesseract ว่าง) →
/// <c>ParseThaiDocument</c> (normalize) → <see cref="SmartFieldExtractor.Enrich"/> → <c>DocumentNumberSanitizer</c> → ตัวแตกบรรทัดจากข้อความ
/// (<see cref="RawTextLineSplitter"/> + <see cref="OcrLineSplitGuard"/> — ทาง <c>TrySplitLineItemsAsync</c> เมื่อไม่มีโมเดล) → <c>SanitizeVatSplitArtifacts</c>
/// (ตัวกันจำนวน) · ตัวจัดฝั่งที่ต้องรู้ตัวตนบริษัทเรา (<see cref="OcrPartyResolver"/>) เรียกด้วยตัวตนของผู้ซื้อจริง (หจก. แอม แฮปปี้เนส)</para>
///
/// <para>ใบเดิม (page.Text) ล็อกคำตอบเดิมไว้ที่ <c>OcrMeterBillLumpSumTests</c> — คู่ "ก่อน/หลัง" ของกระดาษใบเดียวกัน</para>
/// </summary>
public class OcrPdfTextPathTests
{
    private const string TesseractSeparator = "\n\n──── TESSERACT ────\n\n";

    private static readonly OcrOurIdentity Us = new("ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส", null, "0203562005871", "00000",
        "202/24 หมู่ 5 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110");

    /// <summary>เส้น Tier 3 hybrid ของ service: ข้อความ PDF + ตัวคั่น + Tesseract (ว่าง)</summary>
    private static (OcrExtractedData Data, string Combined) TextPath(string pdfText)
    {
        var combined = pdfText + TesseractSeparator;
        var data = OcrService.ParseThaiDocument(ThaiTextNormalizer.Normalize(combined));
        SmartFieldExtractor.Enrich(data, combined);
        var (cleanDocNo, note) = DocumentNumberSanitizer.Sanitize(data.DocumentNumber, combined);
        if (note != null) data.DocumentNumber = cleanDocNo;
        return (data, combined);
    }

    /// <summary>ทาง <c>TrySplitLineItemsAsync</c> เมื่อไม่มีโมเดลตอบ + ตัวกันจำนวน (<c>SanitizeVatSplitArtifacts</c>)</summary>
    private static void SplitLines(OcrExtractedData data, string combined)
    {
        var json = RawTextLineSplitter.SplitToJson(combined);
        Assert.NotNull(json);
        var guard = OcrLineSplitGuard.Evaluate(json, data.SubTotal ?? 0m, data.TotalAmount ?? 0m);
        Assert.True(guard.Accepted, guard.Reason);
        foreach (var l in guard.Lines)
            data.Items.Add(new OcrExtractedLineItem { Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, Amount = l.Amount, Unit = l.Unit });
        OcrService.SanitizeVatSplitArtifacts(data, false, combined);
    }

    [Fact]
    public void บิลกฟภจริง_ข้อความตามพิกัด_หัวใบยอดและบรรทัดตรงกระดาษ()
    {
        var (data, combined) = TextPath(OcrPaperSamples.PeaElectricityBill0869Positional);
        // เลขที่ (No.) ของใบกำกับ — ไม่ใช่เลขที่ใบแจ้งหนี้ 516004632612 (ข้อความเดิมวางป้ายกับค่าสลับคู่)
        Assert.Equal("XH0712609004157", data.DocumentNumber);
        Assert.Equal(new DateTime(2026, 9, 11), data.DocumentDate);
        // ยอด: เดิม 689.92 / 48.29 / 738.21 จาก "1,356.7120,738.21" ที่ติดกัน
        Assert.Equal(19381.50m, data.SubTotal);
        Assert.Equal(1356.71m, data.VatAmount);
        Assert.Equal(20738.21m, data.TotalAmount);
        Assert.Contains(data.ReasoningTrace, t => t.Contains("[AmountWords]", StringComparison.Ordinal) && t.Contains("✓", StringComparison.Ordinal));
        // ตัวยึดยอดรวม: ป้าย "รวมทั้งสิ้น (Total)" นำหน้า 20,738.21 แล้ว (เดิม Unknown — ตัวเลขพิมพ์ก่อนป้าย)
        var anchor = OcrTotalAnchor.Find(combined, data.TotalAmount);
        Assert.Equal(OcrTotalVerdict.Confirmed, anchor.Verdict);
        Assert.Equal(20738.21m, anchor.Total);
        // ผู้ขาย: การไฟฟ้าส่วนภูมิภาค (เดิม = ชื่อผู้ซื้อ) · สาขา 00000 จากประโยค "สาขาที่ออกใบกำกับภาษี" (เดิมว่าง — "1090000000")
        Assert.Equal("การไฟฟ้าส่วนภูมิภาค", data.VendorName);
        Assert.Equal("0994000165501", data.VendorTaxId);
        Assert.Equal("00000", data.VendorBranchCode);
        // ผู้ซื้อ: เลขที่มีป้ายอีกตัว + สาขาบนบรรทัดเดียวกัน · ชื่อใกล้เลขนั้น (เดิม = บริษัทบัตรที่หักบัญชี) — ช่องว่างกลางชื่อหายตาม ThaiTextNormalizer (ทุกเส้นข้อความ)
        Assert.Equal("0203562005871", data.BuyerTaxId);
        Assert.Equal("00000", data.BuyerBranchCode);
        Assert.Equal("ห้างหุ้นส่วนจำกัด แอมแฮปปี้เนส", data.BuyerName);
        // ตัวจัดฝั่ง (ตัวตนเราจาก DB): เราเป็นผู้ซื้อด้วยเลขผู้เสียภาษี · ไม่สลับฝั่ง
        var res = OcrPartyResolver.Resolve(ThaiTextNormalizer.Normalize(combined), Us,
            new OcrPartyBlock(data.VendorName, data.VendorTaxId, data.VendorAddress, data.VendorBranchCode),
            new OcrPartyBlock(data.BuyerName, data.BuyerTaxId, data.BuyerAddress, data.BuyerBranchCode));
        Assert.Equal(OcrSelfSide.Buyer, res.OurSide);
        Assert.Equal("การไฟฟ้าส่วนภูมิภาค", res.Vendor.Name);
        Assert.Equal("0994000165501", res.Vendor.TaxId);
        // บรรทัด: ตารางพิมพ์ จำนวนหน่วย + จำนวนเงิน ไม่มีราคา ⇒ ตัวแตกบรรทัดส่งราคาว่าง ⇒ ตัวกันจำนวนลง 1 × ยอด (รูปของทีม OCR 8)
        SplitLines(data, combined);
        var line = Assert.Single(data.Items);
        Assert.Equal("ค่าไฟฟ้า ประจำเดือน 08/2569 (4,272 หน่วย)", line.Description);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(19381.50m, line.UnitPrice);
        Assert.Equal(19381.50m, line.Amount);
        Assert.Null(line.LineDiscountAmount);
    }

    [Fact]
    public void ใบพรามี่จริง_ข้อความตามพิกัด_แถวตารางอ่านได้_จำนวน1_ราคา1330_ส่วนลด293_ยอด1037()
    {
        var (data, combined) = TextPath(OcrPaperSamples.PramieRe111Positional);
        Assert.Equal("RE-20261000111", data.DocumentNumber);   // เดิม "202/24" (เลขที่บ้านผู้ซื้อ)
        Assert.Equal(new DateTime(2026, 10, 15), data.DocumentDate);
        Assert.Equal(969.16m, data.SubTotal);
        Assert.Equal(67.84m, data.VatAmount);
        Assert.Equal(1037.00m, data.TotalAmount);
        Assert.Equal("0105567070534", data.VendorTaxId);
        Assert.Equal("00000", data.VendorBranchCode);          // เดิมว่าง ("สํานักงานใหญ\uF70A" ไม่ตรงคำค้น)
        Assert.Equal("00000", data.BuyerBranchCode);
        // "จำนวนเงินที่ถูกหัก ณ ที่จ่าย 0.00 บาท" — ป้ายอ่านได้แล้ว (PUA แปลงแล้ว) แต่ยอดศูนย์ ⇒ ไม่ติ๊กหัก ณ ที่จ่าย (ทิศที่ห้ามแย่ลง)
        Assert.False(data.HasWht);
        Assert.Null(data.WhtRate);
        SplitLines(data, combined);
        var line = Assert.Single(data.Items);
        Assert.Equal("(แพ็ค3)แผ่นรองซับ M 50 แผ่น (P00022)", line.Description);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(1330.00m, line.UnitPrice);
        Assert.Equal(293.00m, line.LineDiscountAmount);       // ส่วนลดบนแถว — ตัวกันจำนวน (OcrLineQuantityGuard) ตัวเดียว
        Assert.Equal(1037.00m, line.Amount);
        Assert.Equal("ชิ้น", line.Unit);
    }

    [Fact]
    public void ข้อความเดิมของใบจริง_ตัวแตกบรรทัดตามตารางไม่ทำงาน_ผลเท่าเดิม()
    {
        // ทิศตรงข้าม: ข้อความ page.Text เดิมไม่มีช่องคั่น ⇒ ไม่มีหัวตาราง ⇒ กติกาท้ายบรรทัดเดิมทุกตัวอักษร
        Assert.Empty(RawTextLineSplitter.TableRows(OcrPaperSamples.PeaElectricityBill0869));
        Assert.Empty(RawTextLineSplitter.TableRows(OcrPaperSamples.PramieLineDiscountPdfText));
        foreach (var p in OcrReplayHarness.Corpus.Where(p => !p.Name.EndsWith("-positional", StringComparison.Ordinal)))
            Assert.Empty(RawTextLineSplitter.TableRows(p.RawText));
    }

    // ── ตัวอ่านที่อยู่ของ ParseThaiDocument: รูปเดิมค้าง (O(n⁴)) บนข้อความบรรทัดเดียวยาว + ตัวคั่น Tesseract ─────────────────────

    [Fact]
    public void ข้อความเดิมบรรทัดเดียวยาว_ต่อตัวคั่นTesseract_ParseThaiDocumentจบเร็ว_ไม่ค้าง()
    {
        // ก่อนแก้: probe บนบิล กฟภ. เดิม (2,900 ตัวอักษรบรรทัดเดียว) + ตัวคั่น ⇒ CPU 16 นาทีไม่จบ
        var text = ThaiTextNormalizer.Normalize(OcrPaperSamples.PeaElectricityBill0869 + TesseractSeparator);
        var sw = Stopwatch.StartNew();
        var task = Task.Run(() => OcrService.ParseThaiDocument(text));
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "ParseThaiDocument ค้าง (ตัวอ่านที่อยู่ backtrack)");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
        Assert.Equal("TaxInvoice", task.Result.DocumentType);
    }

    [Theory]
    [InlineData("ที่อยู่: 99/1 ถนนสุขุมวิท\nแขวงคลองเตย เขตคลองเตย\nโทร 02-111-2222", "99/1 ถนนสุขุมวิท แขวงคลองเตยเขตคลองเตย")]
    [InlineData("ที่อยู่ : 12 หมู่ 3 ต.บางพระ\n\nโทร 081-111-2222", "12 หมู่ 3 ต.บางพระ")]
    [InlineData("ADDRESS: 1 Main Road\nBangkok 10110\nTAX ID 0105556000005", "1 Main Road Bangkok 10110")]
    public void ตัวอ่านที่อยู่ผู้ขาย_รูปใหม่ได้ผลเท่ารูปเดิม(string text, string expected)
        => Assert.Equal(expected, OcrService.ParseThaiDocument(ThaiTextNormalizer.Normalize(text)).VendorAddress);

    [Fact]
    public void ตัวอ่านที่อยู่ผู้ขาย_เกินสี่บรรทัดก่อนป้าย_ไม่จับ_เหมือนเดิม()
        => Assert.Null(OcrService.ParseThaiDocument("ที่อยู่ 1 ถนนหนึ่ง\nสอง\nสาม\nสี่\nห้า\nโทร 02-111-2222").VendorAddress);

    // ── ผู้ขายหน่วยงานรัฐบนหัวกระดาษ (SmartFieldExtractor) — ทั้งสองทิศ ─────────────────────────────────────────────

    [Fact]
    public void ทิศตรงข้าม_ผู้ขายพิมพ์ชื่อบริษัทบนหัว_หน่วยงานรัฐเป็นผู้ซื้อ_ชื่อผู้ขายไม่ถูกแทน()
    {
        var text = "บริษัท ไฟฟ้าดี จำกัด\nเลขประจำตัวผู้เสียภาษี 0105567070534\nใบกำกับภาษี\nเลขที่ IV-001\n"
                   + "การไฟฟ้าส่วนภูมิภาค อำเภอศรีราชา\nรวมเงิน 1,000.00\nภาษีมูลค่าเพิ่ม 70.00\nรวมทั้งสิ้น 1,070.00";
        var (data, _) = TextPath(text);
        Assert.Equal("บริษัท ไฟฟ้าดี จำกัด", data.VendorName);
    }

    [Fact]
    public void ทิศตรงข้าม_มีป้ายผู้ขายบนกระดาษ_ไม่ใช้กติกาหัวกระดาษ()
    {
        var text = "การไฟฟ้าส่วนภูมิภาค (ผู้ว่าจ้าง)\nผู้ขาย: บริษัท รับเหมาดี จำกัด\nเลขประจำตัวผู้เสียภาษี 0105567070534\nใบกำกับภาษี\n"
                   + "รวมเงิน 1,000.00\nภาษีมูลค่าเพิ่ม 70.00\nรวมทั้งสิ้น 1,070.00";
        var (data, _) = TextPath(text);
        Assert.Equal("บริษัท รับเหมาดี จำกัด", data.VendorName);
    }

    [Fact]
    public void ห้างหุ้นส่วนจำกัด_ไม่ถูกต่อคำว่าจำกัดซ้ำท้ายชื่อ_บริษัทจำกัดยังได้คำท้าย()
    {
        var text = "ใบกำกับภาษี\nผู้ขาย: บริษัท ทดสอบ จำกัด\nเลขประจำตัวผู้เสียภาษี 0105567070534\n"
                   + "ลูกค้า: ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส\nเลขประจำตัวผู้เสียภาษี 0203562005871\nรวมทั้งสิ้น 1,070.00";
        var (data, _) = TextPath(text);
        Assert.Equal("บริษัท ทดสอบ จำกัด", data.VendorName);
        Assert.Equal("ห้างหุ้นส่วนจำกัด แอมแฮปปี้เนส", data.BuyerName);   // เดิม "… แอมแฮปปี้เนส จำกัด"
    }

    // ── เลข/สาขาผู้ซื้อเมื่อรู้เลขผู้ขายแล้ว (SmartFieldExtractor · BranchCodeExtractor.OnTaxIdLine) ──────────────────────────

    [Fact]
    public void ทิศตรงข้าม_เลขผู้ซื้อไม่มีป้ายเลขผู้เสียภาษี_ไม่เดา()
    {
        // เลข 13 หลักลอย (บาร์โค้ด) ⇒ ไม่ใช่เลขผู้ซื้อ — ต้องมีป้ายเลขผู้เสียภาษีนำหน้าทั้งสองตัว
        var text = "ใบกำกับภาษี\nบริษัท ทดสอบ จำกัด\nเลขประจำตัวผู้เสียภาษี 0105567070534\nสินค้า 8850999220017 100.00\nรวมทั้งสิ้น 107.00";
        var (data, _) = TextPath(text);
        Assert.Equal("0105567070534", data.VendorTaxId);
        Assert.Null(data.BuyerTaxId);
    }

    [Theory]
    [InlineData("เลขประจำตัวผู้เสียภาษี (Tax ID No.) 0203562005871  สาขา (Branch No.) 00000", "0203562005871", "00000")]
    [InlineData("Tax ID 0105556000005 สาขาที่ 00001  Tax ID 0203562005871 สาขาที่ 00002", "0105556000005", "00001")]
    [InlineData("Tax ID 0105556000005 สาขาที่ 00001  Tax ID 0203562005871 สาขาที่ 00002", "0203562005871", "00002")]
    [InlineData("เลขที่ภาษี : 0203562005871 (สำนักงานใหญ่)", "0203562005871", "00000")]
    [InlineData("เลขประจำตัวผู้เสียภาษี 0203562005871\nสาขาที่ 00009", "0203562005871", null)]   // คนละบรรทัด = ไม่อ่าน
    [InlineData("เลขประจำตัวผู้เสียภาษี 0203562005871", "0105556000005", null)]                // ไม่มีเลขนั้น
    public void สาขาบนบรรทัดเลขผู้เสียภาษี_อ่านเฉพาะฝั่งของเลขนั้น(string text, string taxId, string? expected)
        => Assert.Equal(expected, BranchCodeExtractor.OnTaxIdLine(text, taxId));

    [Theory]
    [InlineData("สาขา (Branch No.) 00012", "00012")]
    [InlineData("สาขา/ Branch 00003", "00003")]
    [InlineData("สาขาที่ 00005", "00005")]
    [InlineData("Branch No. 8", "00008")]
    [InlineData("สาขา (Branch No.)", null)]
    public void ป้ายสาขาสองภาษา_มีคำแปลในวงเล็บคั่น_อ่านได้(string segment, string? expected)
        => Assert.Equal(expected, BranchCodeExtractor.FromSegment(segment));

    // ── หัก ณ ที่จ่ายที่พิมพ์ 0.00 (PaperWhtReader.PrintedZero) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("จำนวนเงินที่ถูกหัก ณ ที่จ่าย  0.00 บาท", true)]
    [InlineData("หัก ณ ที่จ่าย 3% 30.00", false)]
    [InlineData("หัก ณ ที่จ่าย 0.00\nภาษีหัก ณ ที่จ่าย 3% 30.00", false)]   // ป้ายใดมียอด > 0 ⇒ มีการหัก
    [InlineData("หัก ณ ที่จ่าย", false)]                                   // ไม่มีตัวเลข = ไม่รู้
    [InlineData("หัก ณ ที่จ่าย\n0.00", false)]                              // คนละบรรทัด = ไม่อ่าน
    public void หักณที่จ่ายที่กระดาษพิมพ์ศูนย์(string text, bool expected)
        => Assert.Equal(expected, PaperWhtReader.PrintedZero(text));

    [Theory]
    [InlineData("จำนวนเงินที่ถูกหัก ณ ที่จ่าย  0.00 บาท\nจำนวนเงินที่ชำระ  1,037.00 บาท", null)]   // ใบพรามี่ — เดิม 1,037.00
    [InlineData("หัก ณ ที่จ่าย\n300.00", 300.00)]                                                  // ป้ายกับยอดคนละบรรทัด (พฤติกรรมเดิม)
    [InlineData("หัก ณ ที่จ่าย 3% 30.00", 30.00)]
    [InlineData("หัก ณ ที่จ่าย 0.00\nภาษีหัก ณ ที่จ่าย 3% 30.00", 30.00)]                            // ป้ายถัดไปยังอ่านได้
    public void ยอดหักณที่จ่ายบนกระดาษ_ศูนย์บนบรรทัดป้าย_ไม่หยิบตัวเลขบรรทัดถัดไป(string text, double? expected)
        => Assert.Equal(expected is double d ? (decimal)d : null, PaperWhtReader.Read(text).Amount);

    [Fact]
    public void ทิศตรงข้าม_ป้ายหักณที่จ่ายพร้อมอัตราและยอด_ยังติ๊กหัก()
    {
        var data = OcrService.ParseThaiDocument("ใบแจ้งหนี้\nค่าบริการ 10,000.00\nหัก ณ ที่จ่าย 3% 300.00\nรวมทั้งสิ้น 10,700.00");
        Assert.True(data.HasWht);
        Assert.Equal(3m, data.WhtRate);
    }

    // ── ตัวแตกบรรทัดตามตาราง (RawTextLineSplitter.TableRows) ───────────────────────────────────────────────────

    [Fact]
    public void ตารางคั่นสองช่องว่าง_หลายแถว_บรรทัดคำอธิบายต่อและแถวสรุปไม่ใช่รายการ()
    {
        var text = "ใบกำกับภาษี\n"
                   + "ลำดับ  รายละเอียด  จำนวน  ราคาต่อหน่วย  จำนวนเงิน\n"
                   + "1  ปากกาเจล  2  10.00  20.00\n"
                   + "สีน้ำเงิน\n"
                   + "2  สมุด A4  3  25.50  76.50\n"
                   + "รวมเงิน  96.50\n"
                   + "3  ไม่ใช่แถว  1  1.00  1.00\n";
        var rows = RawTextLineSplitter.TableRows(text);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new RawTextLineSplitter.Candidate("ปากกาเจล", 2m, null, 10.00m, 20.00m), rows[0]);
        Assert.Equal(new RawTextLineSplitter.Candidate("สมุด A4", 3m, null, 25.50m, 76.50m), rows[1]);
    }

    [Fact]
    public void ทิศตรงข้าม_ข้อความช่องว่างเดียว_ไม่มีหัวตาราง_ใช้กติกาเดิม()
    {
        var text = "รายละเอียด จำนวน ราคาต่อหน่วย จำนวนเงิน\nปากกาเจล 2 ด้าม 10.00 20.00";
        Assert.Empty(RawTextLineSplitter.TableRows(text));
        var old = Assert.Single(RawTextLineSplitter.Split(text));
        Assert.Equal(2m, old.Quantity);
        Assert.Equal(10.00m, old.UnitPrice);
    }

    [Fact]
    public void ตาราง_ช่องตัวเลขไม่ใช่ตัวเลข_หรือจำนวนช่องไม่เท่าหัว_ไม่ใช่แถว()
    {
        var text = "รายการ  จำนวนหน่วย  จำนวนเงิน (บาท)\n"
                   + "สองหมื่นบาทถ้วน  รวมทั้งสิ้น (Total)  20,738.21\n"   // ช่องจำนวนเป็นข้อความ
                   + "เลขที่อ่านครั้งหลัง 20733.000  เลขที่อ่านครั้งก่อน 16461.000\n";   // สองช่อง
        Assert.Empty(RawTextLineSplitter.TableRows(text));
    }

    [Fact]
    public void หัวคอลัมน์คำอธิบาย_เป็นคอลัมน์คำอธิบาย()
    {
        var cols = OcrTableColumnMapper.Map(new[] { "คำอธิบาย", "จำนวน", "หน่วย", "ราคา", "ส่วนลด", "VAT", "มูลค่ารวมภาษี" });
        Assert.Equal(new OcrTableColumns(0, 1, 3, 6, 2, 5), cols);
    }
}
