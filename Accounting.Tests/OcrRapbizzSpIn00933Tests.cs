using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ใบจริง SP-IN2026-10-00933 (ผู้ใช้ 2026-10-26: "ocr เอกสารมา ยังลงค่าไม่ถูกต้อง … อย่าไปแก้อะไรที่ของเดิมทำได้ดีแล้วทำให้แย่ลง")**
/// — ใบเสร็จรับเงิน/ใบกำกับภาษี e-Tax (PDF/A-3 + XML ฝัง) Rapbizz Direct 0105559170207 → หจก.แอม แฮปปี้เนส
///
/// <para>กระดาษ: เก้าอี้สนาม 2 × 920.00 = 1,840.00 · ค่าขนส่ง 1 × 170.00 · "จำนวนเงิน Amount 1,878.51" (= 2,010 ÷ 1.07) · "ส่วนลด 538.32" (ก่อน VAT)
/// · "ยอดรวม Total 1,340.19" · VAT 93.81 · "ราคารวม Sub Total 1,434.00" (ป้ายกลับความหมาย: Sub Total = ยอดสุทธิ)</para>
/// <para>สิ่งที่ผู้ใช้เห็น (ทำซ้ำในครึ่งแรกของแต่ละเทสต์): บรรทัด 920/170 ราคาไม่รวม VAT ไม่มีส่วนลด · ยอดก่อน VAT 2,010 · VAT เฉลี่ย 85.88/7.93 ·
/// "VAT ต้องห้าม 140.70" (= 2,010 × 7% ของบรรทัดที่ถูกปิดเคลม) — สามต้นเหตุ: (1) ตัวตัดสินบรรทัด e-Tax ไม่รู้จัก "ราคารวม VAT + ส่วนลดท้ายบิลรวม VAT
/// ที่หัวใบประกาศ" (2) ส่วนลดหัวใบของเส้น e-Tax = LineTotal − TaxBasis = 0 (3) XML ส่งเลขผู้ซื้อ "N/A" ⇒ §82/5(1) ปิดเคลมทั้งใบ ทั้งที่หน้า PDF ที่ลงนามพิมพ์เลขไว้</para>
/// <para>เส้นกระดาษ (ข้อความหน้า PDF ตามที่ pdftotext อ่าน · ยุบช่องว่างต่อเนื่องเหลือ 2) ใช้กับเคส F ของตัวกระทบยอด + ตัวอ่านส่วนลดหน้าต้นฉบับ/สำเนา —
/// ไม่อยู่ใน <see cref="OcrPaperSamples"/> เพราะใบนี้เข้าระบบทางเส้น e-Tax XML จริง (golden อยู่ใน <see cref="EtaxGoldenCorpusTests"/>)</para>
/// <para>ทุกเทสต์มีสองครึ่ง (§H): ใบนี้กลับมาถูก + ใบเดิมที่ถูกอยู่แล้วไม่ถูกแตะ</para>
/// </summary>
public class OcrRapbizzSpIn00933Tests
{
    private const string Buyer = "ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส";
    private const string BuyerTaxId = "0203562005871";
    private const string SellerTaxId = "0105559170207";

    private static EtaxPdfXmlExtractor.ExtractResult Xml()
    {
        var r = EtaxPdfXmlExtractor.ParseEtaxXml(EtaxFixtures.RapbizzSpIn00933Xml);
        Assert.NotNull(r);
        return r!;
    }

    private static List<OcrEtaxLineFacts> Facts(EtaxPdfXmlExtractor.ExtractResult r)
        => r.Items.Select(li => new OcrEtaxLineFacts(li.Quantity, li.UnitPrice, li.LineAllowance, li.LineCharge,
            li.VatRatePercent, li.Amount, li.NetIncludingVatAmount)).ToList();

    // ── (1)+(2) เส้น e-Tax: บรรทัดรวม VAT + ส่วนลดท้ายบิลรวม VAT ที่หัวใบพิสูจน์ ─────────────────────────────

    [Fact]
    public void ETax_ก่อนแก้_ทำซ้ำหน้าจอผู้ใช้_บรรทัด920และ170ราคาก่อนVAT_ไม่มีส่วนลด_Ambiguous_VATเฉลี่ย85_88และ7_93()
    {
        var r = Xml();
        Assert.Equal(((decimal?)1340.19m, (decimal?)1340.19m, (decimal?)93.81m, (decimal?)1434.00m), (r.LineTotal, r.TaxBasis, r.VatAmount, r.GrandTotal));
        Assert.Equal((decimal?)576.00m, r.AllowanceTotal);
        // ตัวตัดสินเดิม (ไม่ส่งส่วนลดหัวใบ) — ผลเดียวกับที่ผู้ใช้เห็น
        var before = OcrEtaxLineNormalizer.NormalizeInvoice(Facts(r), r.LineTotal, r.VatAmount, r.GrandTotal);
        Assert.False(before.PricesIncludeVat);
        Assert.Equal(new decimal?[] { 1840.00m, 170.00m }, before.Lines.Select(l => l.Amount).ToArray());
        Assert.All(before.Lines, l => Assert.Equal(OcrEtaxPriceBasis.ExclusiveOfVat, l.Basis));
        Assert.Null(OcrEtaxLineNormalizer.HeaderBillDiscount(r.LineTotal, r.TaxBasis, before));   // LineTotal − TaxBasis = 0
        Assert.Equal(OcrLineReconcileCase.Ambiguous, OcrLineReconciler.Classify(2010.00m, 1340.19m, 93.81m, 1434.00m, 0m).Case);
        Assert.Equal(new[] { 85.88m, 7.93m }, ThaiVatTypeRule.SpreadHeaderVat(new[] { (1840.00m, 7m), (170.00m, 7m) }, 93.81m));
    }

    [Fact]
    public void ETax_หลังแก้_หัวใบพิสูจน์ราคารวมVAT_ส่วนลด576รวมVAT_เคสE_ฐาน1340_19_VAT93_81_รวม1434()
    {
        var r = Xml();
        var inv = OcrEtaxLineNormalizer.NormalizeInvoice(Facts(r), r.LineTotal, r.VatAmount, r.GrandTotal, r.AllowanceTotal, r.TaxBasis);
        Assert.True(inv.PricesIncludeVat);
        Assert.Equal((decimal?)576.00m, inv.BillAllowanceInclVat);
        Assert.All(inv.Lines, l => Assert.True(l.PriceIncludesVat && l.QuantityFromDocument && l.VatRate == 7m && l.LineDiscount is null));
        Assert.Equal(new decimal?[] { 2.00m, 1.00m }, inv.Lines.Select(l => l.Quantity).ToArray());
        Assert.Equal(new decimal?[] { 920.00m, 170.00m }, inv.Lines.Select(l => l.UnitPrice).ToArray());
        Assert.Equal(new decimal?[] { 1840.00m, 170.00m }, inv.Lines.Select(l => l.Amount).ToArray());
        Assert.Single(inv.Notes);
        Assert.StartsWith(OcrEtaxLineNormalizer.NoteTag, inv.Notes[0]);   // ข้อสังเกต ไม่ใช่แท็กห้ามอนุมัติเอง
        Assert.True(OcrPostingReadiness.Evaluate(inv.Notes[0], true).CanAutoApprove);
        Assert.Equal((decimal?)576.00m, OcrEtaxLineNormalizer.HeaderBillDiscount(r.LineTotal, r.TaxBasis, inv));

        var recon = OcrLineReconciler.Classify(2010.00m, OcrHeaderAmounts.NetSubTotal(1340.19m, 93.81m, 1434.00m, 576.00m), 93.81m, 1434.00m, 576.00m);
        Assert.Equal(OcrLineReconcileCase.DiscountOnTotalInclVat, recon.Case);
        Assert.Equal((true, 28.66m, (decimal?)1434.00m, 0m), (recon.PricesIncludeVat, recon.DiscountPercent, recon.TargetLineSum, recon.UnreconciledGap));
    }

    [Fact]
    public void ETax_ทิศตรงข้าม_ใบมาตรฐานCII_ส่วนลดหัวใบก่อนVAT_หรือไม่มีVAT_หรือหลายอัตรา_ไม่เข้าทางหัวใบพิสูจน์()
    {
        // ใบมาตรฐาน: บรรทัดก่อน VAT 1,000 + 200 · ส่วนลดท้ายบิล 100 (= LineTotal − TaxBasis) · ฐาน 1,100 · VAT 77 · รวม 1,177 ⇒ ทางเดิมทุกตัวอักษร
        var std = new List<OcrEtaxLineFacts>
        {
            new(2m, 500.00m, null, null, 7m, 1000.00m, 1070.00m),
            new(1m, 200.00m, null, null, 7m, 200.00m, 214.00m),
        };
        var a = OcrEtaxLineNormalizer.NormalizeInvoice(std, 1200.00m, 77.00m, 1177.00m, 100.00m, 1100.00m);
        Assert.False(a.PricesIncludeVat);
        Assert.Null(a.BillAllowanceInclVat);
        Assert.Equal((decimal?)100.00m, OcrEtaxLineNormalizer.HeaderBillDiscount(1200.00m, 1100.00m, a));
        Assert.Equal(OcrEtaxLineNormalizer.NormalizeInvoice(std, 1200.00m, 77.00m, 1177.00m).Lines, a.Lines);

        // ไม่มี VAT (ส่งออก 0%): Σ − ส่วนลด = ยอดรวม = ฐาน — อ่านได้ทั้งสองแบบ ⇒ ไม่เดา
        var zero = new List<OcrEtaxLineFacts> { new(1m, 1000.00m, null, null, 0m, 1000.00m, 1000.00m) };
        Assert.Null(OcrEtaxLineNormalizer.NormalizeInvoice(zero, 1000.00m, 0m, 900.00m, 100.00m, 900.00m).BillAllowanceInclVat);

        // หลายอัตรา (7% + 0%) ⇒ ไม่ตัดสิน
        var mixed = new List<OcrEtaxLineFacts>
        {
            new(2m, 920.00m, null, null, 7m, 1840.00m, 1968.80m),
            new(1m, 170.00m, null, null, 0m, 170.00m, 170.00m),
        };
        Assert.Null(OcrEtaxLineNormalizer.NormalizeInvoice(mixed, 1340.19m, 93.81m, 1434.00m, 576.00m, 1340.19m).BillAllowanceInclVat);

        // มีส่วนลดรายบรรทัด (แบบ CRC) ⇒ ไม่ใช่รูปนี้ (ส่วนลดหัวใบเป็นผลรวมของส่วนลดบรรทัด — ห้ามหักซ้ำ)
        var lineDisc = new List<OcrEtaxLineFacts>
        {
            new(2m, 920.00m, 10.00m, null, 7m, 1830.00m, 1958.10m),
            new(1m, 170.00m, null, null, 7m, 170.00m, 181.90m),
        };
        Assert.Null(OcrEtaxLineNormalizer.NormalizeInvoice(lineDisc, 1340.19m, 93.81m, 1434.00m, 566.00m, 1340.19m).BillAllowanceInclVat);
    }

    // ── (3) VAT ต้องห้าม 140.70: XML ส่งเลขผู้ซื้อ "N/A" แต่หน้า PDF ที่ลงนามพิมพ์เลข ─────────────────────────

    [Fact]
    public void ผู้ซื้อ_ก่อนแก้_XMLเลขผู้ซื้อNA_ปิดเคลมทั้งใบ_หลังแก้_หน้าPDFที่ลงนามพิมพ์เลขเรา_เปิดเคลม_พร้อมหมายเหตุVATNOTE()
    {
        var r = Xml();
        Assert.Equal(Buyer, r.BuyerName);
        Assert.Null(r.BuyerTaxId);   // เดิม "N/A" ไหลลงช่องเลขผู้ซื้อ — ไม่ใช่เลขผู้เสียภาษี
        Assert.Equal(SellerTaxId, r.SellerTaxId);

        // ก่อนแก้: ดู XML อย่างเดียว (= FromEtax ไม่มีข้อความหน้า · ตัวเดิม FromStructured) ⇒ §82/5(1) บล็อก (= "VAT ต้องห้าม" ทุกบรรทัด)
        Assert.True(OcrBuyerOnPaper.JudgeClaim(true, 93.81m, OcrBuyerOnPaper.FromEtax(r.BuyerName, r.BuyerTaxId, null, null, null, null)).BlockClaim);

        // หลังแก้: XML ∪ หน้าเอกสารที่ลงนาม
        var ev = OcrBuyerOnPaper.FromEtax(r.BuyerName, r.BuyerTaxId, PageText, r.SellerTaxId, BuyerTaxId, Buyer);
        Assert.Equal((OcrPaperPresence.Printed, OcrPaperPresence.Printed), (ev.Name, ev.TaxId));
        Assert.False(OcrBuyerOnPaper.JudgeClaim(true, 93.81m, ev).BlockClaim);
        var note = OcrBuyerOnPaper.EtaxXmlGapNote(r.BuyerName, r.BuyerTaxId, ev);
        Assert.NotNull(note);
        Assert.StartsWith("[VAT-NOTE]", note);
        Assert.Contains("เลขประจำตัวผู้เสียภาษีของผู้ซื้อ", note);
        Assert.DoesNotContain("[VAT-CLAIM]", note);
    }

    [Fact]
    public void ผู้ซื้อ_ทิศตรงข้าม_หน้าเอกสารก็ไม่มีเลขผู้ซื้อ_ยังบล็อก_XMLครบ_ไม่มีหมายเหตุ_ไม่มีข้อความหน้า_ตามXMLเดิม()
    {
        const string pageNoBuyer = "ใบกำกับภาษี / TAX INVOICE\nบริษัท แรพบิซซ์ ไดเรคท์ จำกัด\nเลขประจำตัวผู้เสียภาษี 0105559170207\nรวมทั้งสิ้น 1,434.00";
        var noBuyer = OcrBuyerOnPaper.FromEtax(Buyer, null, pageNoBuyer, SellerTaxId, BuyerTaxId, Buyer);
        Assert.Equal(OcrPaperPresence.Missing, noBuyer.TaxId);
        Assert.True(OcrBuyerOnPaper.JudgeClaim(true, 93.81m, noBuyer).BlockClaim);
        Assert.Null(OcrBuyerOnPaper.EtaxXmlGapNote(Buyer, null, noBuyer));

        var complete = OcrBuyerOnPaper.FromEtax(Buyer, BuyerTaxId, PageText, SellerTaxId, BuyerTaxId, Buyer);
        Assert.Equal(OcrBuyerOnPaper.FromEtax(Buyer, BuyerTaxId, null, null, null, null), complete);
        Assert.Null(OcrBuyerOnPaper.EtaxXmlGapNote(Buyer, BuyerTaxId, complete));

        var xmlOnly = OcrBuyerOnPaper.FromEtax(Buyer, null, null, SellerTaxId, BuyerTaxId, Buyer);
        Assert.Equal((OcrPaperPresence.Printed, OcrPaperPresence.Missing), (xmlOnly.Name, xmlOnly.TaxId));
        Assert.True(OcrBuyerOnPaper.JudgeClaim(true, 93.81m, xmlOnly).BlockClaim);   // ไม่มีข้อความหน้า ⇒ ตาม XML เดิม (บล็อก)
    }

    // ── เส้นกระดาษ (ข้อความหน้า PDF): เคส F + ส่วนลดหน้าต้นฉบับ/สำเนา ──────────────────────────────────────

    [Fact]
    public void กระดาษ_ส่วนลดหน้าต้นฉบับและสำเนาเป็นแถวเดียวกัน_538_32_ไม่ใช่1076_64_แล้วแตกยอดได้ส่วนลดก่อนVAT()
    {
        var disc = OcrBillDiscount.Read(PageText, 1434.00m);
        Assert.Equal((decimal?)538.32m, disc.Amount);   // เดิมรวมสองหน้า = 1,076.64 ⇒ ไม่มีสมการไหนลงตัว
        Assert.Equal(1, disc.RowCount);
        var shape = OcrTotalDecomposer.Decompose(PageText, 1340.19m, 93.81m, 1434.00m, disc.Amount!.Value);
        Assert.Equal(OcrDiscountPlacement.PreVat, shape.Placement);   // 1,878.51 − 538.32 = 1,340.19 ที่ VAT ปิดยอดรวม
        Assert.Equal(538.32m, shape.DiscountToSpread);
    }

    [Fact]
    public void กระดาษ_ทิศตรงข้าม_ส่วนลดยอดเท่ากันใต้สินค้าคนละตัว_ยังนับสองก้อน()
    {
        var text = "ใบเสร็จรับเงิน\nสินค้า ก 100.00\nส่วนลด 10.00\nสินค้า ข 50.00\nสินค้า ค 30.00\nสินค้า ง 40.00\nสินค้า จ 20.00\n"
            + "สินค้า ฉ 60.00\nสินค้า ช 70.00\nสินค้า ซ 80.00\nสินค้า ฌ 90.00\nส่วนลด 10.00\nรวมทั้งสิ้น 520.00";
        Assert.Equal((decimal?)20.00m, OcrBillDiscount.Read(text, 520.00m).Amount);
        // หน้าซ้ำแต่ใกล้กันเกินระยะหน้า (แถวติดกันของสินค้าซ้ำ) ⇒ ไม่ถือเป็นหน้าสำเนา
        Assert.False(OcrBillDiscount.SameRowOnRepeatedPage("ก\nส่วนลด 10.00\nข\nก\nส่วนลด 10.00\nข".Split('\n'), 1, 4));
    }

    [Fact]
    public void กระดาษ_เคสF_ราคารวมVAT_ส่วนลดก่อนVAT538_32_กระจายเป็น527_28และ48_72_VAT85_88และ7_93()
    {
        // 2,010 ÷ 1.07 = 1,878.50 (กระดาษคิดรายบรรทัดได้ 1,878.51) − 538.32 = 1,340.18 ≈ 1,340.19 · ส่วนลดรวม VAT = 2,010 − 1,434 = 576.00 = 538.32 × 1.07
        Assert.Equal((decimal?)576.00m, OcrLineReconciler.PreVatDiscountOnInclusiveLines(2010.00m, 1340.19m, 93.81m, 1434.00m, 538.32m));
        var recon = OcrLineReconciler.Classify(2010.00m, 1340.19m, 93.81m, 1434.00m, 538.32m);
        Assert.Equal(OcrLineReconcileCase.DiscountPreVatOnInclusivePrices, recon.Case);
        Assert.Equal((true, 28.66m, (decimal?)1434.00m, 0m), (recon.PricesIncludeVat, recon.DiscountPercent, recon.TargetLineSum, recon.UnreconciledGap));

        // ตัวกระจายของตัวสร้างบรรทัด (สูตรเดียวกับ BuildScanLinesAsync) + ส่วนลดรายบรรทัด + VAT ราคารวม VAT
        var first = Math.Round(1434.00m * 1840.00m / 2010.00m, 2, MidpointRounding.AwayFromZero);
        var nets = new[] { first, 1434.00m - first };
        Assert.Equal(new[] { 1312.72m, 121.28m }, nets);
        Assert.Equal((28.66m, 527.28m, (decimal?)null), OcrLineReconciler.LineDiscount(recon.DiscountPercent, 1840.00m, true, nets[0], 2m));
        Assert.Equal((28.66m, 48.72m, (decimal?)null), OcrLineReconciler.LineDiscount(recon.DiscountPercent, 170.00m, true, nets[1], 1m));
        Assert.Equal(new[] { 85.88m, 7.93m }, OcrEtaxLineNormalizer.InclusiveLineVats(new[] { (nets[0], 7m, true), (nets[1], 7m, true) }, 93.81m));
    }

    [Fact]
    public void กระดาษ_ทิศตรงข้าม_ใบเดิมที่ถูกอยู่แล้ว_เคสBCEไม่เปลี่ยน_ส่วนลดที่ไม่เข้าสมการยังAmbiguous_หลายอัตราไม่เข้าเคสF()
    {
        // ราคาก่อน VAT ปกติ (ป้าย Sub Total = ก่อน VAT)
        Assert.Equal(OcrLineReconcileCase.LinesMatchSubTotal, OcrLineReconciler.Classify(1000m, 1000m, 70m, 1070m).Case);
        // ส่วนลดท้ายบิลก่อน VAT แบบเดิม (ร้านวัสดุ 5%)
        Assert.Equal(OcrLineReconcileCase.DiscountOnSubTotal, OcrLineReconciler.Classify(1395m, 1325.25m, 92.77m, 1418.02m, 69.75m).Case);
        // ราคารวม VAT + ส่วนลดรวม VAT (ซูเปอร์มาร์เก็ต)
        Assert.Equal(OcrLineReconcileCase.DiscountOnTotalInclVat, OcrLineReconciler.Classify(774m, 687.20m, 48.10m, 735.30m, 38.70m).Case);
        // ราคารวม VAT ไม่มีส่วนลด
        Assert.Equal(OcrLineReconcileCase.PricesIncludeVat, OcrLineReconciler.Classify(1396m, 1304.67m, 91.33m, 1396m).Case);
        // ส่วนลดที่อ่านได้อธิบายส่วนต่างไม่ได้ ⇒ ยังไม่เดา
        Assert.Equal(OcrLineReconcileCase.Ambiguous, OcrLineReconciler.Classify(2010.00m, 1340.19m, 93.81m, 1434.00m, 500.00m).Case);
        Assert.Equal(OcrLineReconcileCase.Ambiguous, OcrLineReconciler.Classify(1000m, 950m, 66.50m, 1016.50m, 0m).Case);
        // VAT ไม่ใช่ 7% ของฐาน (ใบผสมอัตรา) ⇒ ไม่ใช่เคส F
        Assert.Null(OcrLineReconciler.PreVatDiscountOnInclusiveLines(2010.00m, 1340.19m, 80.00m, 1420.19m, 538.32m));
    }

    [Fact]
    public void กระดาษ_ต้นฉบับสำเนา_แถวรายการซ้ำสองรอบ_ด่านแถวซ้ำรู้จักเคสF_ตัดเหลือสองแถว()
    {
        var rows = new List<OcrCandidateRow>
        {
            new("เก้าอี้สนาม", 2m, 920.00m, 1840.00m), new("ค่าขนส่งสินค้า", 1m, 170.00m, 170.00m),
            new("เก้าอี้สนาม", 2m, 920.00m, 1840.00m), new("ค่าขนส่งสินค้า", 1m, 170.00m, 170.00m),
        };
        var d = OcrDuplicateLineGuard.Decide(rows, 1340.19m, 93.81m, 1434.00m, 538.32m);
        Assert.True(d.Deduped, d.Reason);
        Assert.Equal(new[] { 0, 1 }, d.KeepIndexes);
        // ทิศตรงข้าม: ไม่มีส่วนลด ⇒ ไม่มีเป้าเคส F (คงทุกแถวให้ด่าน Σ ฟ้องเหมือนเดิม)
        Assert.False(OcrDuplicateLineGuard.Decide(rows, 1340.19m, 93.81m, 1434.00m, 0m).Deduped);
    }

    /// <summary>ข้อความหน้า PDF (ต้นฉบับ + สำเนา) ตามที่ pdftotext -layout อ่าน — ฟอนต์ไทยเพี้ยนตามจริง · ยุบช่องว่างต่อเนื่องเหลือ 2</summary>
    private const string PageText = """
  Digitally signed by Rapbizz Direct Company Limited
  DN: cn=บริษัท แรพบิซซ์ ไดเรคท์ จำกัด,nc=TH
  Date: 2026.10.24 14:49:07 +07  หน้า/Page 1 / 1

  Rapbizz Direct Co.,Ltd.  บกาก ับภาษี/ บ สรจร ับ งิน
  65/19 หม่4 ถ.บางนา-ตราด ตาบลบางพลีน ้อย
  TAX INVOICE/RECEIPT
  อาเภอบางบ่อ จังหวัดสมุทรปราการ 10560
  Tel: 0955726342
  เลขประจาตัวผ ้เสียภาษี : 0105559170207  สาขาที่ : สานักงานใหญ่

 Digitally Sign By
 เอกสารฉบับนีไ ้ ด ้จัดทาและส่งข ้อมลให ้แก่กรมสรรพากรด ้วยวิธก
  ี ารทางอิเล็กทรอนิกส  ORIGINAL

  ่ ผูซ
 ชือ  ้ื /Customer CDN2020-12-00002
  ้ อ  ลขที/  ่ Document No.  SP-IN2026-10-00933
 ห ้างหุ ้นส่วนจากัด แอม แฮปปี้ เนส  ว ันที/
  ่ Document Date  15/10/2026
 เลขที่ 202/24 หม่ท ี่ 5 ซอย บ ้านห ้วยกุม
  ่ 4 ตาบลบางพระ  ลขทีอ  ่ า้ งอิง/Ext. Doc. No.
 อาเภอศรีราชา จังหวัดชลบุรี 20110  สกุล งิน/Currency Code  THB
 เลขทีผ่ ้เสียภาษี : 0203562005871 สาขาที่ : สานักงานใหญ่
  สถานทีจ
  ่ ัดส่ง/Ship-to : CDN2020-12-00002
  ห ้างหุ ้นส่วนจากัด แอม แฮปปี้ เนส
  เลขที่ 202/24 หม่ท ี่ 5 ซอย บ ้านห ้วยกุม
  ่ 4 ตาบลบางพระ
  อาเภอศรีราชา จังหวัดชลบุรี 20110
  tea_lemon@hotmail.com




 ลาด ับ รห ัสรายการ  คาอธิบาย  จานวน หน่วยน ับ  ราคาขาย  ส่วนลด  จานวน งิน
 No.  Item No.  Description  Qty UOM  Unit Price  Discount  Amount

 1  FU0101-00001  เก ้าอีส
  ้ นาม พับเก็บได ้ 56x60x100 ซม. รับน้ าหนักได ้ 125  2.00 Piece  920.00  0.00  1,840.00
  KG สีดา JBUYNOW YLFC-032

 2  510901  ค่าขนส่งสินค ้า  1.00  170.00  0.00  170.00




  ิ ธิเลิกสัญญาโดยการส่งหนังสือแสดงเจตนาได ้ ภายใน 7 วัน นับแต่วน
 ผ ้บริโภคมีสท  ั ทีไ่ ด ้รับสินค ้า และ ผ ้ประกอบธุรกิจฯ
 จะคืนเงินเต็มจานวนทีผ  ่ ้บริโภคได ้จ่ายไปเพือ  ้ สินค ้า ภายใน 15 วัน นับแต่วน
  ่ ซือ  ่ ้บริโภคใช ้สิทธิในการคืนสินค ้า
  ั ทีผ


  จานวน งิน  จานวน งิน Amount  1,878.51
  Amount  หนึง่ พันสีร่ ้อยสามสิบสีบ
  ่ าทถ ้วน
  ส่วนลด Discount Amt  538.32

  ยอดรวม Total  1,340.19

  ภาษีมล
  ู ค่า พิม
  ่ 7%  93.81
  ราคารวม Sub Total  1,434.00


  Admin365 ERP  ( RBD API )

  ผ ้จัดทา/Issue By  ผ ้อนุมต
  ั /ิ Approve By  ผ ้รับเงิน/Seller Signature

  บริษัทจะดาเนินการประมวลผลข ้อมลส่วนบุคคคลของลกค ้า โดยสอดคล ้องกับนโยบายความเป็ นส่วนตัวของลกค ้า ซึง่ ท่านสามารถศึกษาได ้ที่ https://www.jssr.co.th/pdpa/Customer_Group_Privacy_Notice

พิมพ์ครงที
  ั้ ่ 1

  หน้า/Page 1 / 1

  Rapbizz Direct Co.,Ltd.  บกาก ับภาษี/ บ สรจร ับ งิน
  65/19 หม่4 ถ.บางนา-ตราด ตาบลบางพลีน ้อย
  TAX INVOICE/RECEIPT
  อาเภอบางบ่อ จังหวัดสมุทรปราการ 10560
  Tel: 0955726342
  เลขประจาตัวผ ้เสียภาษี : 0105559170207  สาขาที่ : สานักงานใหญ่

 Digitally Sign By
 เอกสารฉบับนีไ ้ ด ้จัดทาและส่งข ้อมลให ้แก่กรมสรรพากรด ้วยวิธก
  ี ารทางอิเล็กทรอนิกส  COPY

  ่ ผูซ
 ชือ  ้ื /Customer CDN2020-12-00002
  ้ อ  ลขที/  ่ Document No.  SP-IN2026-10-00933
 ห ้างหุ ้นส่วนจากัด แอม แฮปปี้ เนส  ว ันที/
  ่ Document Date  15/10/2026
 เลขที่ 202/24 หม่ท ี่ 5 ซอย บ ้านห ้วยกุม
  ่ 4 ตาบลบางพระ  ลขทีอ  ่ า้ งอิง/Ext. Doc. No.
 อาเภอศรีราชา จังหวัดชลบุรี 20110  สกุล งิน/Currency Code  THB
 เลขทีผ่ ้เสียภาษี : 0203562005871 สาขาที่ : สานักงานใหญ่
  สถานทีจ
  ่ ัดส่ง/Ship-to : CDN2020-12-00002
  ห ้างหุ ้นส่วนจากัด แอม แฮปปี้ เนส
  เลขที่ 202/24 หม่ท ี่ 5 ซอย บ ้านห ้วยกุม
  ่ 4 ตาบลบางพระ
  อาเภอศรีราชา จังหวัดชลบุรี 20110
  tea_lemon@hotmail.com




 ลาด ับ รห ัสรายการ  คาอธิบาย  จานวน หน่วยน ับ  ราคาขาย  ส่วนลด  จานวน งิน
 No.  Item No.  Description  Qty UOM  Unit Price  Discount  Amount

 1  FU0101-00001  เก ้าอีส
  ้ นาม พับเก็บได ้ 56x60x100 ซม. รับน้ าหนักได ้ 125  2.00 Piece  920.00  0.00  1,840.00
  KG สีดา JBUYNOW YLFC-032

 2  510901  ค่าขนส่งสินค ้า  1.00  170.00  0.00  170.00




  ิ ธิเลิกสัญญาโดยการส่งหนังสือแสดงเจตนาได ้ ภายใน 7 วัน นับแต่วน
 ผ ้บริโภคมีสท  ั ทีไ่ ด ้รับสินค ้า และ ผ ้ประกอบธุรกิจฯ
 จะคืนเงินเต็มจานวนทีผ  ่ ้บริโภคได ้จ่ายไปเพือ  ้ สินค ้า ภายใน 15 วัน นับแต่วน
  ่ ซือ  ่ ้บริโภคใช ้สิทธิในการคืนสินค ้า
  ั ทีผ


  จานวน งิน  จานวน งิน Amount  1,878.51
  Amount  หนึง่ พันสีร่ ้อยสามสิบสีบ
  ่ าทถ ้วน
  ส่วนลด Discount Amt  538.32

  ยอดรวม Total  1,340.19

  ภาษีมล
  ู ค่า พิม
  ่ 7%  93.81
  ราคารวม Sub Total  1,434.00


  Admin365 ERP  ( RBD API )

  ผ ้จัดทา/Issue By  ผ ้อนุมต
  ั /ิ Approve By  ผ ้รับเงิน/Seller Signature

  บริษัทจะดาเนินการประมวลผลข ้อมลส่วนบุคคคลของลกค ้า โดยสอดคล ้องกับนโยบายความเป็ นส่วนตัวของลกค ้า ซึง่ ท่านสามารถศึกษาได ้ที่ https://www.jssr.co.th/pdpa/Customer_Group_Privacy_Notice

พิมพ์ครงที
  ั้ ่ 1
""";
}
