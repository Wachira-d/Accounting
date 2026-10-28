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
    public void ETax_หลังแก้_หัวใบพิสูจน์ราคารวมVAT_ส่วนลด576รวมVAT_ส่งต่อเป็น538_32ก่อนVAT_เคสF_ฐาน1340_19_VAT93_81_รวม1434()
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
        // ฝ่ายค้านข้อ 4: ช่องส่วนลดของสแกนความหมายเดียว = ยอดก่อน VAT (538.32 เท่าหน้า PDF และเส้นกระดาษ) ไม่ใช่ 576 รวม VAT
        Assert.Equal((decimal?)538.32m, OcrEtaxLineNormalizer.HeaderBillDiscount(r.LineTotal, r.TaxBasis, inv));
        Assert.Equal(OcrBillDiscount.Read(PageText, 1434.00m).Amount, OcrEtaxLineNormalizer.HeaderBillDiscount(r.LineTotal, r.TaxBasis, inv));

        var recon = OcrLineReconciler.Classify(2010.00m, OcrHeaderAmounts.NetSubTotal(1340.19m, 93.81m, 1434.00m, 538.32m), 93.81m, 1434.00m, 538.32m);
        Assert.Equal(OcrLineReconcileCase.DiscountPreVatOnInclusivePrices, recon.Case);
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

    // ── (3) VAT ต้องห้าม 140.70: XML ส่งเลขผู้ซื้อ "N/A" แต่ข้อความบนหน้า PDF มีเลขเรา (คำตัดสินเจ้าของข้อ 158) ─────────

    [Fact]
    public void ผู้ซื้อ_ก่อนแก้_XMLเลขผู้ซื้อNA_ปิดเคลมทั้งใบ_หลังแก้_เติมเลขและสาขาจากหน้าPDF_เปิดเคลม_อนุมัติเองได้_พร้อมหมายเหตุที่มา()
    {
        var r = Xml();
        Assert.Equal(Buyer, r.BuyerName);
        Assert.Null(r.BuyerTaxId);   // เดิม "N/A" ไหลลงช่องเลขผู้ซื้อ — ไม่ใช่เลขผู้เสียภาษี
        Assert.Equal(SellerTaxId, r.SellerTaxId);

        // ก่อนแก้: ดู XML อย่างเดียว (= FromEtax ไม่มีข้อความหน้า · ตัวเดิม FromStructured) ⇒ §82/5(1) บล็อก (= "VAT ต้องห้าม" ทุกบรรทัด)
        Assert.True(OcrBuyerOnPaper.JudgeClaim(true, 93.81m, OcrBuyerOnPaper.FromEtax(r.BuyerName, r.BuyerTaxId, null, null, null, null)).BlockClaim);

        // หลังแก้: XML + ข้อความบนหน้า PDF (เลขเราตรงตัวเท่านั้น)
        var ev = OcrBuyerOnPaper.FromEtax(r.BuyerName, r.BuyerTaxId, PageText, r.SellerTaxId, BuyerTaxId, Buyer);
        Assert.Equal((OcrPaperPresence.Printed, OcrPaperPresence.Printed), (ev.Name, ev.TaxId));
        Assert.False(OcrBuyerOnPaper.JudgeClaim(true, 93.81m, ev).BlockClaim);
        // คำตัดสินข้อ 158: เลือกข้อมูลที่ครบและถูกต้องที่สุดต่อช่อง — เลขผู้ซื้อ + สาขาจากหน้า (XML ขาด) · ชื่อคงตาม XML
        var fill = OcrEtaxPageFill.ResolveBuyer(r.BuyerName, r.BuyerTaxId, r.BuyerBranchCode, r.SellerTaxId, PageText, BuyerTaxId, Buyer);
        Assert.Equal((Buyer, BuyerTaxId, "00000"), (fill.BuyerName, fill.BuyerTaxId, fill.BuyerBranchCode));
        Assert.Equal(new[] { "เลขประจำตัวผู้เสียภาษีของผู้ซื้อ", "สาขาผู้ซื้อ" }, fill.FilledFromPage);
        Assert.Empty(fill.Conflicts);
        Assert.False(fill.BlockingConflict);
        Assert.NotNull(fill.Note);
        Assert.StartsWith(OcrEtaxPageFill.FillTag, fill.Note);
        Assert.Contains(OcrEtaxPageFill.RuleCode, fill.Note);
        Assert.Contains("แจ้งผู้ขายแก้ข้อมูล e-Tax", fill.Note);
        Assert.Contains("ไม่ได้ตรวจลายเซ็น", fill.Note);
        Assert.DoesNotContain(OcrEtaxPageFill.ConflictTag, fill.Note);
        Assert.True(OcrPostingReadiness.Evaluate(fill.Note, true).CanAutoApprove);   // ไม่บังคับให้คนยืนยัน (ข้อ 158)
        // ค่าที่เติมแล้วผ่านด่าน §82/5(1) ด้วย XML อย่างเดียว (ตัวเดียวกับที่ service ส่งต่อหลังเติม)
        Assert.False(OcrBuyerOnPaper.JudgeClaim(true, 93.81m, OcrBuyerOnPaper.FromEtax(fill.BuyerName, fill.BuyerTaxId, null, null, null, null)).BlockClaim);
    }

    [Fact]
    public void ผู้ซื้อ_ทิศตรงข้าม_หน้าเอกสารก็ไม่มีเลขผู้ซื้อ_ยังบล็อก_XMLครบ_ไม่มีหมายเหตุ_ไม่มีข้อความหน้า_ตามXMLเดิม()
    {
        const string pageNoBuyer = "ใบกำกับภาษี / TAX INVOICE\nบริษัท แรพบิซซ์ ไดเรคท์ จำกัด\nเลขประจำตัวผู้เสียภาษี 0105559170207\nรวมทั้งสิ้น 1,434.00";
        var noBuyer = OcrBuyerOnPaper.FromEtax(Buyer, null, pageNoBuyer, SellerTaxId, BuyerTaxId, Buyer);
        Assert.Equal(OcrPaperPresence.Missing, noBuyer.TaxId);
        Assert.True(OcrBuyerOnPaper.JudgeClaim(true, 93.81m, noBuyer).BlockClaim);
        var fillNoBuyer = OcrEtaxPageFill.ResolveBuyer(Buyer, null, null, SellerTaxId, pageNoBuyer, BuyerTaxId, Buyer);
        Assert.Equal(((string?)null, (string?)null), (fillNoBuyer.BuyerTaxId, fillNoBuyer.BuyerBranchCode));
        Assert.Null(fillNoBuyer.Note);

        // ฝ่ายค้านข้อ 1: เลขอื่นบนหน้าที่ checksum ผ่าน (ไม่ใช่เลขผู้ขาย · ไม่ใช่เลขเรา) ต้องไม่ชนะ "N/A" — ตัวอ่านกระดาษเดิม (Read) ยอมเลขนี้
        const string pageOtherId = pageNoBuyer + "\nเลขอ้างอิง 0105555175590";
        Assert.Equal(OcrPaperPresence.Printed, OcrBuyerOnPaper.Read(pageOtherId, SellerTaxId, null, Buyer, BuyerTaxId, Buyer).TaxId);
        var otherId = OcrBuyerOnPaper.FromEtax(Buyer, null, pageOtherId, SellerTaxId, BuyerTaxId, Buyer);
        Assert.Equal(OcrPaperPresence.Missing, otherId.TaxId);
        Assert.True(OcrBuyerOnPaper.JudgeClaim(true, 93.81m, otherId).BlockClaim);
        Assert.Null(OcrEtaxPageFill.ResolveBuyer(Buyer, null, null, SellerTaxId, pageOtherId, BuyerTaxId, Buyer).BuyerTaxId);
        // เลขเราอ่านเพี้ยนหนึ่งหลัก ⇒ ไม่นับ (ตัวเลขล้วนไม่มีการสะกดผิด — ไม่ fuzzy)
        var nearMiss = OcrBuyerOnPaper.FromEtax(Buyer, null, pageNoBuyer + "\nเลขผู้ซื้อ 0203562005872", SellerTaxId, BuyerTaxId, Buyer);
        Assert.Equal(OcrPaperPresence.Missing, nearMiss.TaxId);
        Assert.Null(OcrEtaxPageFill.ResolveBuyer(Buyer, null, null, SellerTaxId, pageNoBuyer + "\nเลขผู้ซื้อ 0203562005872", BuyerTaxId, Buyer).BuyerTaxId);
        // ไม่รู้เลขบริษัทเรา ⇒ ไม่มีอะไรให้เทียบตรงตัว ⇒ ตาม XML (บล็อก)
        Assert.Equal(OcrPaperPresence.Missing, OcrBuyerOnPaper.FromEtax(Buyer, null, PageText, SellerTaxId, null, Buyer).TaxId);

        var complete = OcrBuyerOnPaper.FromEtax(Buyer, BuyerTaxId, PageText, SellerTaxId, BuyerTaxId, Buyer);
        Assert.Equal(OcrBuyerOnPaper.FromEtax(Buyer, BuyerTaxId, null, null, null, null), complete);
        var fillComplete = OcrEtaxPageFill.ResolveBuyer(Buyer, BuyerTaxId, "00000", SellerTaxId, PageText, BuyerTaxId, Buyer);
        Assert.Null(fillComplete.Note);   // XML ครบและตรงหน้า ⇒ ไม่มีอะไรต้องบอก
        var fillNoPage = OcrEtaxPageFill.ResolveBuyer(Buyer, null, null, SellerTaxId, null, BuyerTaxId, Buyer);
        Assert.Equal(((string?)null, (string?)null, (string?)null), (fillNoPage.BuyerTaxId, fillNoPage.BuyerBranchCode, fillNoPage.Note));

        var xmlOnly = OcrBuyerOnPaper.FromEtax(Buyer, null, null, SellerTaxId, BuyerTaxId, Buyer);
        Assert.Equal((OcrPaperPresence.Printed, OcrPaperPresence.Missing), (xmlOnly.Name, xmlOnly.TaxId));
        Assert.True(OcrBuyerOnPaper.JudgeClaim(true, 93.81m, xmlOnly).BlockClaim);   // ไม่มีข้อความหน้า ⇒ ตาม XML เดิม (บล็อก)
    }

    [Fact]
    public void ผู้ซื้อ_ข้อมูลXMLมีค่าจริงแต่ขัดกับหน้า_คงค่าXML_สาขาขัดเตือนไม่หยุด_เลขผู้ซื้อหรือผู้ขายขัดจริงหยุด()
    {
        // สาขาใน XML 00012 · หน้าพิมพ์สำนักงานใหญ่ ⇒ คง 00012 + เตือน (ไม่หยุดอนุมัติ)
        var branch = OcrEtaxPageFill.ResolveBuyer(Buyer, BuyerTaxId, "00012", SellerTaxId, PageText, BuyerTaxId, Buyer);
        Assert.Equal("00012", branch.BuyerBranchCode);
        Assert.Single(branch.Conflicts);
        Assert.False(branch.BlockingConflict);
        Assert.StartsWith(OcrEtaxPageFill.FillTag, branch.Note);
        Assert.True(OcrPostingReadiness.Evaluate(branch.Note, true).CanAutoApprove);

        // เลขผู้ซื้อใน XML เป็นเลขอื่นที่ถูกต้อง แต่หน้าพิมพ์เลขเรา (ไม่มีเลขใน XML บนหน้า) ⇒ คงค่า XML + หยุด
        var buyer = OcrEtaxPageFill.ResolveBuyer(Buyer, "0105555175590", null, SellerTaxId, PageText, BuyerTaxId, Buyer);
        Assert.Equal("0105555175590", buyer.BuyerTaxId);
        Assert.True(buyer.BlockingConflict);
        Assert.Contains(OcrEtaxPageFill.ConflictTag, buyer.Note);
        Assert.Contains(OcrPostingReadiness.BlockingTags, t => t.Tag == OcrEtaxPageFill.ConflictTag);
        Assert.False(OcrPostingReadiness.Evaluate(buyer.Note, true).CanAutoApprove);

        // เลขผู้ขายใน XML ไม่อยู่บนหน้า และหน้าพิมพ์เลขผู้ขายอื่นที่ถูกต้อง ⇒ หยุด
        var page = "ใบกำกับภาษี\nผู้ขาย เลขประจำตัวผู้เสียภาษี 0105555175590\nผู้ซื้อ เลขที่ผู้เสียภาษี : 0203562005871 สาขาที่ : สำนักงานใหญ่";
        var seller = OcrEtaxPageFill.ResolveBuyer(Buyer, null, null, SellerTaxId, page, BuyerTaxId, Buyer);
        Assert.True(seller.BlockingConflict);
        Assert.Equal(BuyerTaxId, seller.BuyerTaxId);   // เลขเราตรงตัวยังเติมได้ — ข้อขัดแย้งอยู่ที่ฝั่งผู้ขาย
        Assert.False(OcrPostingReadiness.Evaluate(seller.Note, true).CanAutoApprove);
        // สาขาที่พิมพ์เป็นตัวเลขบนบรรทัดเลขเรา
        var numbered = OcrEtaxPageFill.ResolveBuyer(Buyer, null, null, SellerTaxId,
            "เลขผู้เสียภาษี 0105559170207\nลูกค้า เลขที่ผู้เสียภาษี 0203562005871 สาขาที่ 00003", BuyerTaxId, Buyer);
        Assert.Equal("00003", numbered.BuyerBranchCode);
        Assert.False(numbered.BlockingConflict);
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

    /// <summary>ข้อความหน้า PDF ของใบนี้ — ตัวจริงอยู่ที่ <see cref="OcrPaperSamples.RapbizzSpIn00933PdfText"/> (R-34 2026-10-26: ย้ายเข้าชุด replay ·
    /// ชื่อใบ <c>rapbizz-spin00933-prevat-discount-on-incl</c>) · เทสต์ในไฟล์นี้อ้างชื่อเดิมต่อ</summary>
    private const string PageText = OcrPaperSamples.RapbizzSpIn00933PdfText;

    // ── ทีม T 2026-10-26 (ผู้ใช้: "ทำไมยังบันทึกได้ไม่ตรง" หลัง deploy ทีม R): ไล่เส้นจริงทั้งสาย XML → DTO → เอกสาร → เปิดแก้แล้วบันทึก เป็นตัวเลขทุกขั้น
    //    (docs/OCR_TOTALS_MATRIX.md §1) — ทุก helper เป็นตัวจริงที่ service เรียก · ตัวจำลองเดินลำดับเดียวกับ MapEtaxToOcrData → ScanBillDiscount → BuildScanLinesAsync ──

    [Fact]
    public void ทีมT_เส้นจริงทั้งสาย_XMLถึงเอกสาร_ทุกขั้นเป็นตัวเลข_ฐาน1340_19_VAT93_81_สุทธิ1434_เปิดแก้แล้วบันทึกไม่ขยับ()
    {
        var r = Xml();
        // ขั้น 1 MapEtaxToOcrData — ตัวตัดสินบรรทัด (หัวใบพิสูจน์ราคารวม VAT) + ส่วนลดหัวใบ (ก่อน VAT) ⇒ ExtractedSubTotal/Vat/Total/Discount
        var inv = OcrEtaxLineNormalizer.NormalizeInvoice(Facts(r), r.LineTotal, r.VatAmount, r.GrandTotal, r.AllowanceTotal, r.TaxBasis);
        Assert.Equal((true, (decimal?)576.00m), (inv.PricesIncludeVat, inv.BillAllowanceInclVat));
        Assert.Equal((decimal?)538.32m, OcrEtaxLineNormalizer.HeaderBillDiscount(r.LineTotal, r.TaxBasis, inv));
        Assert.Equal((decimal?)1340.19m, r.LineTotal);
        Assert.Equal((decimal?)93.81m, r.VatAmount);
        Assert.Equal((decimal?)1434.00m, r.GrandTotal);
        // ขั้น 2 CreateDocumentFromScanCoreAsync → ScanBillDiscount (OcrTotalDecomposer.ForScan บน RawTextContent = XML ทั้งก้อน):
        //        XML ไม่มีแถว "ส่วนลด 538.32" หลังยอดรวม ⇒ ไม่ใช่ "ปรับตอนชำระ" ⇒ ส่วนลดคงเดิม 538.32 ไม่มีหมายเหตุ [PAY≠TOTAL]
        var scanDisc = OcrTotalDecomposer.ForScan(ThaiTextNormalizer.Normalize(EtaxFixtures.RapbizzSpIn00933Xml), 1340.19m, 93.81m, 1434.00m, 538.32m, null);
        Assert.Equal(538.32m, scanDisc.DiscountToSpread);
        Assert.Null(scanDisc.NoteToAppend);
        // ขั้น 3 ResolveHeaderSubTotal ⇒ Document.SubTotal (ตั้งก่อนสร้างบรรทัด) = ยอดรวม − VAT
        Assert.Equal(1340.19m, OcrHeaderAmounts.NetSubTotal(1340.19m, 93.81m, 1434.00m, 538.32m));
        // ขั้น 4 BuildScanLinesAsync — คำตัดสินข้อ 159: เคส F ⇒ BillDiscountAmount 538.32 ตามกระดาษ · บรรทัด 1,840/170 ไม่มีส่วนลด ·
        //        DocumentService.PlanLineAmounts: ฐานก่อนลด 1,719.63 + 158.88 = 1,878.51 ("จำนวนเงิน Amount") − จัดสรร 492.79/45.53 ⇒ 1,226.84/113.35 · VAT 85.88/7.93
        var doc = ScanDocumentSimulator.Build(r, EtaxFixtures.RapbizzSpIn00933Xml);
        Assert.True(doc.PricesIncludeVat);
        Assert.Equal(538.32m, doc.BillDiscount);
        Assert.Equal(new[] { (2.00m, 920.00m, 0m, 1226.84m, 85.88m, 7m), (1.00m, 170.00m, 0m, 113.35m, 7.93m, 7m) },
            doc.Lines.Select(l => (l.Qty, l.UnitPrice, l.Discount, l.Amount, l.Vat, l.VatRate)).ToArray());
        Assert.Equal(1878.51m, Math.Round(1840.00m * 100m / 107m, 2, MidpointRounding.AwayFromZero) + Math.Round(170.00m * 100m / 107m, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(0m, doc.RoundingAdjustment);
        Assert.Equal((1340.19m, 93.81m, 1434.00m),
            (doc.Lines.Sum(l => l.Amount), doc.Lines.Sum(l => l.Vat), doc.Lines.Sum(l => l.Amount) + doc.Lines.Sum(l => l.Vat)));
        Assert.Equal((1340.19m, 93.81m, 1434.00m), (doc.HeaderSubTotal, doc.HeaderVat, doc.HeaderTotal));   // Document.SubTotal / VatAmount / TotalAmount
        Assert.Equal(0m, doc.UnreconciledGap);
        Assert.True(doc.CanAutoApprove, doc.BlockReason);
        // ข้อ 159: Document.DiscountAmount (Σ ส่วนลดบรรทัด) = 0 · ส่วนลดท้ายบิล 538.32 = กระดาษ (ก่อนข้อ 159: 576.00 ต่อบรรทัด — ยอดเท่ากันแต่หน้าตาต่าง)
        Assert.Equal(0m, doc.Lines.Sum(l => l.Discount));
        Assert.Equal(538.32m, Math.Round(576.00m * 100m / 107m, 2, MidpointRounding.AwayFromZero));
        // ขั้น 5 หมายเหตุที่ไปหน้าตรวจ: [e-Tax] ข้อสังเกตเดียว (ระบบตรวจพบและปรับให้แล้ว) — ไม่มี [Σ-GAP]/แท็กหยุด ⇒ ห้ามขึ้นเป็น "ระบบไม่แน่ใจ"
        Assert.Single(doc.Notes);
        Assert.StartsWith(OcrEtaxLineNormalizer.NoteTag, doc.Notes[0]);
        Assert.Contains("ส่วนลดท้ายบิลของเอกสาร 538.32", doc.Notes[0]);
        Assert.DoesNotContain(doc.Notes, n => n.StartsWith("[Σ-GAP]", StringComparison.Ordinal));
        Assert.True(OcrPostingReadiness.Evaluate(string.Join("\n", doc.Notes), true).CanAutoApprove);
        // ขั้น 6 เปิดฟอร์มแก้ไข (documents.html hydrate billDiscountAmount 538.32 โหมด ฿ · PricesIncludeVat จาก entity) แล้วบันทึก
        //        = DocumentService AllocateBillDeductions → ComputeLineAmounts → ReconcileTaxRounding (ตัวจริง) ⇒ บรรทัดเดิมทุกสตางค์ ⇒ ยอดหัวเอกสารไม่ขยับ
        Assert.Equal(doc.Lines.Select(l => (l.Amount, l.Vat)).ToList(), ScanDocumentSimulator.Resave(doc));
        var hdr = Accounting.Services.Implementations.DocumentService.PreviewTotals(doc.Lines.Select(l => new Accounting.Models.DTOs.Document.DocumentLineRequest("", l.Qty, null, l.UnitPrice, 0m, l.VatRate, 0m, null)).ToList(), true, 538.32m);
        Assert.Equal((1340.19m, 93.81m, 1434.00m, 538.32m), (hdr.Net, hdr.Vat, hdr.Total, hdr.TradeDiscount));
    }

    [Fact]
    public void ทีมT_ข้อ159_ทิศตรงข้าม_ถ้าส่วนลดท้ายบิลหาย_ยอดกลับเป็น1878_51บวกVAT131_50_ฟอร์มจึงต้องส่งbillDiscountAmountจากเซิร์ฟเวอร์()
    {
        // จุดที่ตัวเลข "เปลี่ยนกลับ" ได้หลังข้อ 159: บรรทัดไม่มีส่วนลดแล้ว — ถ้า BillDiscountAmount หาย (หน้าเว็บส่ง 0 เหมือนเดิม) ⇒ ฐาน 1,878.51 · VAT 131.50 · 2,010.01 ≠ 1,434
        var doc = ScanDocumentSimulator.Build(Xml(), EtaxFixtures.RapbizzSpIn00933Xml);
        var lost = ScanDocumentSimulator.Resave(doc with { BillDiscount = 0m });
        Assert.Equal(1878.51m, lost.Sum(x => x.Amount));
        Assert.NotEqual(1434.00m, lost.Sum(x => x.Amount) + lost.Sum(x => x.Vat));
        // เคส C (ร้านวัสดุ · ส่วนลดก่อน VAT บนราคาก่อน VAT) ยังกระจายลงบรรทัดเหมือนเดิม — ไม่ลง BillDiscountAmount
        Assert.Null(OcrLineReconciler.Classify(1395m, 1325.25m, 92.77m, 1418.02m, 69.75m).BillDiscountPreVat);
        Assert.Null(OcrLineReconciler.Classify(774m, 687.20m, 48.10m, 735.30m, 38.70m).BillDiscountPreVat);   // เคส E
        Assert.Equal((decimal?)538.32m, OcrLineReconciler.Classify(2010.00m, 1340.19m, 93.81m, 1434.00m, 538.32m).BillDiscountPreVat);
    }

    [Fact]
    public void ทีมT_ทิศตรงข้าม_รูปกระจายเดิม_ถ้าฟอร์มส่งแต่เปอร์เซ็นต์28_66_ยอดจะขยับเป็น1433_94_จึงต้องส่งส่วนลดบาทต่อบรรทัดเสมอ()
    {
        // รูปการบันทึก "กระจายส่วนลดลงบรรทัด" (เคส C/E ยังใช้ · เคส F ก่อนข้อ 159): ถ้าช่องส่วนลดบาทหาย ComputeLineAmounts คิดจาก % ⇒ 1,840 × 28.66% = 527.34 ≠ 527.28
        //   ⇒ ฐาน 1,340.13 · VAT 93.81 · สุทธิ 1,433.94 ≠ 1,434.00 — documents.html จึงต้อง hydrate โหมด ฿ (บรรทัด "discountAmount > 0 → โหมด ฿")
        var built = ScanDocumentSimulator.Build(Xml(), EtaxFixtures.RapbizzSpIn00933Xml);
        var doc = built with { BillDiscount = 0m, Lines = new List<ScanDocumentSimulator.SimLine>
        {
            new(2.00m, 920.00m, 527.28m, 1226.84m, 85.88m, 7m), new(1.00m, 170.00m, 48.72m, 113.35m, 7.93m, 7m),
        } };
        Assert.Equal(doc.Lines.Select(l => (l.Amount, l.Vat)).ToList(), ScanDocumentSimulator.Resave(doc));   // รูปกระจายก็บันทึกซ้ำได้เท่าเดิม
        var pctOnly = doc with
        {
            Lines = doc.Lines.Select(l => l with { Discount = Math.Round(l.Qty * l.UnitPrice * 28.66m / 100m, 2, MidpointRounding.AwayFromZero) }).ToList(),
        };
        Assert.Equal(new[] { 527.34m, 48.72m }, pctOnly.Lines.Select(l => l.Discount).ToArray());
        var resaved = ScanDocumentSimulator.Resave(pctOnly);
        Assert.Equal(new[] { (1226.78m, 85.88m), (113.35m, 7.93m) }, resaved.ToArray());
        Assert.Equal(1433.94m, resaved.Sum(x => x.Amount) + resaved.Sum(x => x.Vat));
        Assert.NotEqual(1434.00m, resaved.Sum(x => x.Amount) + resaved.Sum(x => x.Vat));
    }

    [Fact]
    public void ทีมT_เส้นกระดาษของใบเดียวกัน_ScanBillDiscountคงส่วนลด538_32_ตำแหน่งก่อนVAT_ไม่ใช่ปรับตอนชำระ()
    {
        // ภาพ/PDF ไม่มี XML: RawTextContent = ข้อความหน้า · 1,878.51 − 538.32 = 1,340.19 พิมพ์ครบ ⇒ PreVat (ไม่ใช่ PostInvoice ที่จะทำให้ส่วนลดเป็น 0 แล้วตก Ambiguous)
        var d = OcrTotalDecomposer.Decompose(ThaiTextNormalizer.Normalize(PageText), 1340.19m, 93.81m, 1434.00m, 538.32m);
        Assert.NotEqual(OcrDiscountPlacement.PostInvoice, d.Placement);   // PreVat หรือ Unknown (ใบมีคำ THB) — ทั้งคู่คงส่วนลด · PostInvoice เท่านั้นที่ทำให้เป็น 0
        Assert.Equal(538.32m, d.DiscountToSpread);
        Assert.Equal(538.32m, OcrTotalDecomposer.ForScan(ThaiTextNormalizer.Normalize(PageText), 1340.19m, 93.81m, 1434.00m, 538.32m, null).DiscountToSpread);
    }

    [Fact]
    public void ทีมT_ข้อ159_ฝ่ายค้าน_ราคาต่อหน่วยมีเศษ_4x1228_04เท่ากับ4912_16แต่พิมพ์4912_15_ไม่บวกเศษซ้ำ_ΣบรรทัดบวกRAเท่าฐานกระดาษ_บันทึกซ้ำไม่ขยับ()
    {
        // สังเคราะห์ (รูปเดียวกับใบ Rapbizz · ราคาต่อหน่วยแบบ Lazada 1,228.04 × 4 = 4,912.16 แต่กระดาษพิมพ์ 4,912.15): Σ พิมพ์ 5,082.15 − ส่วนลด 576 (รวม VAT) = 4,506.15 ·
        // VAT 294.80 · ฐาน 4,211.35 · ส่วนลดก่อน VAT 538.32 ⇒ หัวใบพิสูจน์ (เคส F) ⇒ BillDiscountAmount 538.32
        var r = new EtaxPdfXmlExtractor.ExtractResult
        {
            Found = true, DocumentTypeCode = "T03", MappedDocumentType = "TaxInvoice", DocumentNumber = "SYN-F-ROUND-01",
            SellerTaxId = SellerTaxId, LineTotal = 4211.35m, TaxBasis = 4211.35m, AllowanceTotal = 576.00m, VatAmount = 294.80m, GrandTotal = 4506.15m,
            Items =
            {
                new() { Description = "นมผง", Quantity = 4m, UnitPrice = 1228.04m, Amount = 4912.15m, NetIncludingVatAmount = 5256.00m, VatRatePercent = 7m },
                new() { Description = "ค่าขนส่งสินค้า", Quantity = 1m, UnitPrice = 170.00m, Amount = 170.00m, NetIncludingVatAmount = 181.90m, VatRatePercent = 7m },
            },
        };
        var inv = OcrEtaxLineNormalizer.NormalizeInvoice(Facts(r), r.LineTotal, r.VatAmount, r.GrandTotal, r.AllowanceTotal, r.TaxBasis);
        Assert.Equal((decimal?)576.00m, inv.BillAllowanceInclVat);
        var doc = ScanDocumentSimulator.Build(r);
        Assert.Equal(538.32m, doc.BillDiscount);
        // DocumentService: ฐานก่อนลด round(4 × 1,228.04 = 4,912.16 ÷ 1.07) 4,590.80 + 158.88 = 4,749.68 · จัดสรร 520.31/18.01 · VAT 284.93/9.86 ⇒ ขั้นปัดขยับบรรทัดใหญ่ 0.01 ⇒ 4,070.48/284.94
        Assert.Equal(new[] { (4m, 1228.04m, 0m, 4070.48m, 284.94m), (1m, 170.00m, 0m, 140.87m, 9.86m) },
            doc.Lines.Select(l => (l.Qty, l.UnitPrice, l.Discount, l.Amount, l.Vat)).ToArray());
        // เศษราคาต่อหน่วย +0.01 ไม่ถูกบวกซ้ำ (เดิมจะได้ 4,070.49) · ผลต่างปัดเศษ = Σ บรรทัดจริง − ฐานกระดาษ = 0 ⇒ SubTotal = Σ บรรทัด + RA = ฐานกระดาษ
        Assert.Equal(0m, doc.RoundingAdjustment);
        Assert.Equal(4211.35m, doc.Lines.Sum(l => l.Amount) + doc.RoundingAdjustment);
        Assert.Equal((4211.35m, 294.80m, 4506.15m), (doc.HeaderSubTotal, doc.HeaderVat, doc.HeaderTotal));
        Assert.Equal(294.80m, doc.Lines.Sum(l => l.Vat));
        // เปิดแก้แล้วบันทึก (สูตรจริงของ DocumentService) = เท่าเดิมทุกสตางค์ — ทิศตรงข้ามของ "บวกเศษซ้ำ"
        Assert.Equal(doc.Lines.Select(l => (l.Amount, l.Vat)).ToList(), ScanDocumentSimulator.Resave(doc));
    }
}
