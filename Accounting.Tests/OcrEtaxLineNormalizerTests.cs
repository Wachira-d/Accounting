using Accounting.Helpers;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// <see cref="OcrEtaxLineNormalizer"/> กับไฟล์ e-Tax XML <b>จริง</b> ของ CRC ไทวัสดุ (<see cref="EtaxFixtures.CrcThaiwatsaduXml"/> ·
/// สแกน f1690d11 · ผู้ใช้รายงาน 2026-10-09)
///
/// <para><b>ใบที่พัง (ต้องกลับมาถูก)</b>: ราคาต่อหน่วยรวม VAT + ส่วนลดรายบรรทัดรวม VAT ⇒ เดิมจำนวนถูกหารจากยอด
/// (3 → 2.13 · ค่าขนส่ง 120 → 37.38) · ราคา 37.00 (รวม VAT) ลงเอกสารราคาก่อน VAT · ส่วนลด 0 · [Gateway] ฟ้อง 9 บรรทัด</para>
/// <para><b>ใบที่ถูกอยู่แล้ว (ห้ามแตะ)</b>: Shopee ราคาก่อน VAT ไม่มีส่วนลด (2 × 250.47 = 500.94 ≈ 500.93) · บรรทัดไม่มี BilledQuantity ·
/// ตัวเลขที่ไม่ลงตัวทั้งสองทาง · บรรทัดมีค่าบริการ ⇒ ค่าเดิมทุกตัว</para>
/// </summary>
public class OcrEtaxLineNormalizerTests
{
    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static EtaxPdfXmlExtractor.ExtractResult Crc()
    {
        var r = EtaxPdfXmlExtractor.ParseEtaxXml(EtaxFixtures.CrcThaiwatsaduXml);
        Assert.NotNull(r);
        return r!;
    }

    private static OcrEtaxLine Norm(EtaxPdfXmlExtractor.LineItem li)
        => OcrEtaxLineNormalizer.Normalize(new OcrEtaxLineFacts(
            li.Quantity, li.UnitPrice, li.LineAllowance, li.LineCharge, li.VatRatePercent, li.Amount, li.NetIncludingVatAmount));

    private static OcrEtaxLineFacts Facts(EtaxPdfXmlExtractor.LineItem li)
        => new(li.Quantity, li.UnitPrice, li.LineAllowance, li.LineCharge, li.VatRatePercent, li.Amount, li.NetIncludingVatAmount);

    /// <summary>ทั้งใบผ่าน <see cref="OcrEtaxLineNormalizer.NormalizeInvoice"/> (ทางที่ MapEtaxToOcrData ใช้จริง)</summary>
    private static OcrEtaxInvoiceLines CrcInvoice(decimal? lineTotal = 2991.59m, decimal? vat = 209.41m, decimal? grand = 3201.00m)
        => OcrEtaxLineNormalizer.NormalizeInvoice(Crc().Items.Select(Facts).ToList(), lineTotal, vat, grand);

    /// <summary>บรรทัดเอกสาร (สิ่งที่ MapEtaxToOcrData ประกอบ) จากผลตัดสิน</summary>
    private static OcrExtractedLineItem ToItem(EtaxPdfXmlExtractor.LineItem li, OcrEtaxLine n) => new()
    {
        Description = li.Description, Quantity = n.Quantity, UnitPrice = n.UnitPrice, Amount = n.Amount,
        LineDiscountAmount = n.LineDiscount, QuantityFromEtaxXml = n.QuantityFromDocument,
        PriceIncludesVat = n.PriceIncludesVat, VatStripResidual = n.VatStripResidual,
        VatRate = n.PriceIncludesVat ? n.VatRate : null,
    };

    // LineID · BilledQuantity · ราคา (รวม VAT) · ส่วนลด (รวม VAT) · ยอดก่อน VAT · ยอดรวม VAT — ตัวเลขบนไฟล์จริงทุกตัว
    public static IEnumerable<object[]> CrcLines => new[]
    {
        new object[] { 1, 3m, 37.00m, 26.68m, 78.80m, 84.32m },
        new object[] { 2, 4m, 229.00m, 220.14m, 650.34m, 695.86m },
        new object[] { 4, 12m, 123.00m, 354.72m, 1047.93m, 1121.28m },
        new object[] { 5, 10m, 28.00m, 67.29m, 198.79m, 212.71m },
        new object[] { 6, 6m, 43.00m, 62.00m, 183.18m, 196.00m },
        new object[] { 7, 6m, 43.00m, 62.00m, 183.18m, 196.00m },
        new object[] { 8, 4m, 43.00m, 41.34m, 122.11m, 130.66m },
        new object[] { 9, 1m, 690.00m, 165.83m, 489.88m, 524.17m },
        new object[] { 10, 120m, 1.00m, 80.00m, 37.38m, 40.00m },
    };

    [Theory]
    [MemberData(nameof(CrcLines))]
    public void ตัวสกัดเก็บส่วนลด_ยอดรวมVAT_ฐาน_อัตราของบรรทัดจากไฟล์จริง(
        int lineId, decimal qty, decimal gross, decimal allowance, decimal net, decimal netIncl)
    {
        var li = Crc().Items.Single(x => x.LineNo == lineId);
        Assert.Equal(qty, li.Quantity);
        Assert.Equal(gross, li.UnitPrice);
        Assert.Equal(allowance, li.LineAllowance);
        Assert.Null(li.LineCharge);
        Assert.Equal(net, li.Amount);
        Assert.Equal(netIncl, li.NetIncludingVatAmount);
        Assert.Equal(7.00m, li.VatRatePercent);
        // BasisAmount ของผู้ขายรายนี้ = ฐานก่อนส่วนลด (qty × ราคา ÷ 1.07) — เก็บไว้ตรวจย้อน
        Assert.Equal(R2(qty * gross * 100m / 107m), li.TaxBasisAmount);
    }

    [Theory]
    [MemberData(nameof(CrcLines))]
    public void ใบCRC_ราคารวมVAT_จำนวนจริงคงเดิม_ราคาก่อนVAT_ส่วนลดลงตัวพอดี(
        int lineId, decimal qty, decimal gross, decimal allowance, decimal net, decimal netIncl)
    {
        _ = netIncl;
        var n = Norm(Crc().Items.Single(x => x.LineNo == lineId));
        if (lineId == 10)
        {
            // ทางสำรองราคาก่อน VAT: 120 × 0.93 − 74.22 ⇒ เศษจากถอด VAT 0.55 > 0.5% ของ 37.38 ⇒ ไม่ตัดสิน (ฝ่ายค้านรอบสาม)
            Assert.Equal(OcrEtaxPriceBasis.Unknown, n.Basis);
            Assert.False(n.QuantityFromDocument);
            return;
        }
        Assert.Equal(OcrEtaxPriceBasis.InclusiveOfVat, n.Basis);
        Assert.Equal(qty, n.Quantity);                                   // ไม่เคยหารจากยอด
        Assert.True(n.QuantityFromDocument);
        Assert.Equal(R2(gross * 100m / 107m), n.UnitPrice);             // ราคาก่อน VAT ทศนิยม 2 ตำแหน่ง
        Assert.Equal(net, n.Amount);                                     // = NetLineTotalAmount
        Assert.NotNull(n.LineDiscount);
        Assert.Equal(net, R2(qty * n.UnitPrice!.Value) - n.LineDiscount!.Value);   // สมการ DocumentService.ComputeLineAmounts
        // ส่วนลดก่อน VAT ใกล้ ส่วนลดรวม VAT ÷ 1.07 (ต่างได้แค่เศษปัดของราคาต่อหน่วย × จำนวน)
        Assert.True(Math.Abs(n.LineDiscount!.Value - allowance * 100m / 107m) <= qty * 0.005m + 0.01m);
    }

    [Fact]
    public void ใบCRC_บรรทัด1_และค่าขนส่ง_ตัวเลขตรง()
    {
        var items = Crc().Items;
        var l1 = Norm(items.Single(x => x.LineNo == 1));
        Assert.Equal((3m, 34.58m, 24.94m, 78.80m), (l1.Quantity!.Value, l1.UnitPrice!.Value, l1.LineDiscount!.Value, l1.Amount!.Value));
        Assert.Equal(0.01m, l1.VatStripResidual);   // 24.94 − round(26.68 ÷ 1.07) = 24.93 ⇒ เศษ 1 สตางค์ (อยู่ในเพดาน)
        // ค่าขนส่ง: ทางสำรองไม่ตัดสิน (เศษเกินเพดาน) · ทางหลัก (ทั้งใบราคารวม VAT) ⇒ ค่าตามกระดาษ 120 × 1.00 − 80.00 = 40.00 รวม VAT
        Assert.Equal(OcrEtaxPriceBasis.Unknown, Norm(items.Single(x => x.LineNo == 10)).Basis);
        var ship = CrcInvoice().Lines[^1];
        Assert.Equal((120m, 1.00m, 80.00m, 40.00m), (ship.Quantity!.Value, ship.UnitPrice!.Value, ship.LineDiscount!.Value, ship.Amount!.Value));
        Assert.True(ship.PriceIncludesVat);
    }

    [Fact]
    public void ใบCRC_ผลรวมบรรทัดตรงหัวใบ_ส่วนลดหัวใบไม่ถูกหักซ้ำ()
    {
        var r = Crc();
        Assert.Equal(9, r.Items.Count);
        var inv = CrcInvoice();
        Assert.True(inv.PricesIncludeVat);
        var lines = inv.Lines;
        // ยอดบรรทัด (รวม VAT หลังส่วนลดบรรทัด) รวม = ยอดรวมทั้งสิ้นของหัวใบ
        Assert.Equal(3201.00m, lines.Sum(x => x.Amount!.Value));
        // ถอด VAT รายบรรทัดด้วยสูตรเอกสาร ⇒ ก่อน VAT รวม = LineTotal ของหัวใบ
        Assert.Equal(r.LineTotal, lines.Sum(x => DocumentLineVatConvention.SplitLine(x.Amount!.Value, 7m, null, true).Net));
        Assert.Equal(2991.59m, r.TaxBasis);
        Assert.Equal(209.41m, r.VatAmount);
        Assert.Equal(3201.00m, r.GrandTotal);
        Assert.Equal(r.GrandTotal, r.LineTotal + r.VatAmount);
        // AllowanceTotalAmount 1,080.00 (รวม VAT) = Σ ส่วนลดบรรทัด — อยู่ในยอดบรรทัดแล้ว: LineTotal − TaxBasis = 0 ⇒ ไม่มีส่วนลดท้ายบิล
        Assert.Equal(1080.00m, r.Items.Sum(x => x.LineAllowance!.Value));
        Assert.Equal(0m, r.LineTotal!.Value - r.TaxBasis!.Value);
        // ส่วนลดบรรทัดตามกระดาษ (รวม VAT) รวม = AllowanceTotalAmount พอดี — ไม่มีการแต่ง
        Assert.Equal(1080.00m, lines.Sum(x => x.LineDiscount!.Value));
    }

    [Fact]
    public void ใบCRC_ตัวกันจำนวนระเบิดไม่หารจำนวนใหม่_ยอดคงเดิม()
    {
        var r = Crc();
        var d = new OcrExtractedData();
        var inv = CrcInvoice();
        for (var i = 0; i < r.Items.Count; i++) d.Items.Add(ToItem(r.Items[i], inv.Lines[i]));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(9, d.Items.Count);
        Assert.Equal(new[] { 3m, 4m, 12m, 10m, 6m, 6m, 4m, 1m, 120m }, d.Items.Select(x => x.Quantity!.Value).ToArray());
        Assert.Equal(3201.00m, d.Items.Sum(x => x.Amount!.Value));
        Assert.All(d.Items, x => Assert.Equal(x.Amount, R2(x.Quantity!.Value * x.UnitPrice!.Value) - x.LineDiscountAmount!.Value));
    }

    [Fact]
    public void ทำซ้ำบั๊ก_แมปตรงจากXMLแบบเดิม_จำนวนถูกหารจากยอด()
    {
        // ตัวเลขที่ผู้ใช้เห็นบนเอกสารที่สร้าง (2.13 · 37.38) — ยืนยันว่าแก้ถูกตัว
        var r = Crc();
        var d = new OcrExtractedData();
        foreach (var li in r.Items)
            d.Items.Add(new OcrExtractedLineItem { Description = li.Description, Quantity = li.Quantity, UnitPrice = li.UnitPrice, Amount = li.Amount });
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(2.13m, d.Items[0].Quantity);
        Assert.Equal(37.38m, d.Items[^1].Quantity);
    }

    private static OcrConfidenceGateway.GatewayResult Gate(IEnumerable<OcrConfidenceGateway.LineItemForValidation> lines)
        => OcrConfidenceGateway.Validate(
            modelConfidence: 1.0m, documentDate: new DateTime(2026, 10, 5),
            subTotal: 2991.59m, vatAmount: 209.41m, total: 3201.00m,
            vendorTaxId: "0105555021215", buyerTaxId: "0203562005871",
            lineItems: lines.ToList(), documentNumber: "SRCIE26100075384", vendorName: "บริษัท ซีอาร์ซี ไทวัสดุ จำกัด");

    private static bool QtyWarning(OcrConfidenceGateway.GatewayResult g)
        => g.Warnings.Any(w => w.Contains("Qty × UnitPrice ≠ Amount", StringComparison.Ordinal));

    [Fact]
    public void ด่านGateway_ส่วนลดบรรทัดอธิบายยอด_ไม่ฟ้อง_ไม่ส่งส่วนลดยังฟ้อง()
    {
        var lines = CrcInvoice().Lines;
        Assert.False(QtyWarning(Gate(lines.Select(n =>
            new OcrConfidenceGateway.LineItemForValidation(n.Quantity, n.UnitPrice, n.Amount, n.LineDiscount)))));
        // ทิศตรงข้าม: ส่วนลดหาย (หรือส่วนลดที่ไม่อธิบายยอด) ⇒ ด่านต้องยังฟ้อง
        Assert.True(QtyWarning(Gate(lines.Select(n =>
            new OcrConfidenceGateway.LineItemForValidation(n.Quantity, n.UnitPrice, n.Amount)))));
        Assert.True(QtyWarning(Gate(lines.Select(n =>
            new OcrConfidenceGateway.LineItemForValidation(n.Quantity, n.UnitPrice, n.Amount, 1m)))));
    }

    // ── ใบที่ถูกอยู่แล้ว / พิสูจน์ไม่ได้ — ค่าเดิมทุกตัว ──

    [Fact]
    public void Shopee_ราคาก่อนVAT_ไม่มีส่วนลด_พฤติกรรมเดิม()
    {
        var r = EtaxPdfXmlExtractor.ParseEtaxXml(EtaxFixtures.ShopeeUptoyouXml)!;
        var li = Assert.Single(r.Items);
        Assert.Null(li.LineAllowance);          // ActualAmount 0.00 ไม่ใช่ส่วนลด
        Assert.Equal(536.00m, li.NetIncludingVatAmount);
        var n = Norm(li);
        Assert.Equal(OcrEtaxPriceBasis.ExclusiveOfVat, n.Basis);
        Assert.Equal(2m, n.Quantity);
        Assert.Equal(250.47m, n.UnitPrice);
        Assert.Null(n.LineDiscount);
        Assert.Equal(500.93m, n.Amount);
        // ตัวกันจำนวนระเบิดให้ผลเท่าเดิม (2 × 250.47 = 500.94 ≈ 500.93 อยู่ในเกณฑ์อยู่แล้ว)
        var d = new OcrExtractedData();
        d.Items.Add(ToItem(li, n));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal((2m, 250.47m, 500.93m), (d.Items[0].Quantity!.Value, d.Items[0].UnitPrice!.Value, d.Items[0].Amount!.Value));
    }

    [Fact]
    public void ไม่มีBilledQuantity_พฤติกรรมเดิม()
    {
        var n = OcrEtaxLineNormalizer.Normalize(new OcrEtaxLineFacts(null, 37.00m, 26.68m, null, 7m, 78.80m, 84.32m));
        Assert.Equal(OcrEtaxPriceBasis.Unknown, n.Basis);
        Assert.Null(n.Quantity);
        Assert.Equal(37.00m, n.UnitPrice);
        Assert.Null(n.LineDiscount);
        Assert.Equal(78.80m, n.Amount);
        Assert.False(n.QuantityFromDocument);
    }

    [Fact]
    public void ราคาก่อนVAT_มีส่วนลดบรรทัด_ราคาและจำนวนตามXML_ส่วนลดตามXML()
    {
        // 5 × 100.00 − 50.00 = 450.00 = ยอดก่อน VAT ⇒ ราคาก่อน VAT อยู่แล้ว
        var n = OcrEtaxLineNormalizer.Normalize(new OcrEtaxLineFacts(5m, 100.00m, 50.00m, null, 7m, 450.00m, 481.50m));
        Assert.Equal(OcrEtaxPriceBasis.ExclusiveOfVat, n.Basis);
        Assert.Equal((5m, 100.00m, 50.00m, 450.00m), (n.Quantity!.Value, n.UnitPrice!.Value, n.LineDiscount!.Value, n.Amount!.Value));
    }

    [Theory]
    // ไม่ลงตัวทั้งสองทาง (ส่วนลดหาย/ตัวเลขขัดกัน) ⇒ ห้ามเดา
    [InlineData(3, 37.00, 0, 78.80, 84.32)]
    // ยอดเล็กจน ยอดก่อน/รวม VAT ห่างกันไม่ถึง 2 สตางค์ ⇒ กำกวม
    [InlineData(1, 0.10, 0, 0.09, 0.10)]
    public void ตัวเลขพิสูจน์ไม่ได้_คงค่าเดิมทุกตัว(int qty, double gross, double allowance, double net, double netIncl)
    {
        var n = OcrEtaxLineNormalizer.Normalize(new OcrEtaxLineFacts(
            qty, (decimal)gross, (decimal)allowance, null, 7m, (decimal)net, (decimal)netIncl));
        Assert.Equal(OcrEtaxPriceBasis.Unknown, n.Basis);
        Assert.Equal((decimal)qty, n.Quantity);
        Assert.Equal((decimal)gross, n.UnitPrice);
        Assert.Null(n.LineDiscount);
        Assert.Equal((decimal)net, n.Amount);
        Assert.False(n.QuantityFromDocument);
    }

    [Fact]
    public void บรรทัดมีค่าบริการ_ไม่ตัดสิน()
    {
        var n = OcrEtaxLineNormalizer.Normalize(new OcrEtaxLineFacts(3m, 37.00m, 26.68m, 5m, 7m, 78.80m, 84.32m));
        Assert.Equal(OcrEtaxPriceBasis.Unknown, n.Basis);
        Assert.Equal(37.00m, n.UnitPrice);
    }

    [Fact]
    public void ส่วนลดที่พิสูจน์ได้_และแก้บรรทัดในรีวิวคงส่วนลด()
    {
        Assert.Equal(24.94m, OcrEtaxLineNormalizer.ProvenLineDiscount(3m, 34.58m, 78.80m, 24.94m));
        Assert.Equal(0m, OcrEtaxLineNormalizer.ProvenLineDiscount(3m, 34.58m, 78.80m, 10m));    // ไม่อธิบายยอด
        Assert.Equal(0m, OcrEtaxLineNormalizer.ProvenLineDiscount(3m, 34.58m, 78.80m, null));
        Assert.Equal(0m, OcrEtaxLineNormalizer.ProvenLineDiscount(3m, 34.58m, 78.80m, 200m));   // เกินยอดก่อนลด
        // แก้ชื่อรายการเฉย ๆ ⇒ ยอดยังเป็นยอดหลังลด (เดิมกลับเป็น 103.74)
        Assert.Equal((78.80m, (decimal?)24.94m, false), OcrEtaxLineNormalizer.AmountAfterLineDiscount(3m, 34.58m, 24.94m));
        // ผู้ใช้ลดราคาจนส่วนลดเกินยอดก่อนลด ⇒ ทิ้งส่วนลด (ยอดติดลบไม่มีจริง)
        Assert.Equal((10.00m, (decimal?)null, true), OcrEtaxLineNormalizer.AmountAfterLineDiscount(1m, 10.00m, 24.94m));
        // ไม่มีส่วนลด = สูตรเดิม round(จำนวน × ราคา)
        Assert.Equal((103.74m, (decimal?)null, false), OcrEtaxLineNormalizer.AmountAfterLineDiscount(3m, 34.58m, null));
    }

    // ══ ฝ่ายค้านรอบสอง (2026-10-09) ══════════════════════════════════════════════════════════════

    /// <summary>สมการที่ DocumentService.ComputeLineAmounts คิดตอนเปิดแก้แล้วบันทึก (ราคาก่อน VAT): round(จำนวน × ราคา) − min(ส่วนลด, ยอดก่อนลด)</summary>
    private static decimal Resave(decimal q, decimal p, decimal? d)
    {
        var gross = R2(q * p);
        return gross - (d is > 0m ? Math.Min(R2(d.Value), gross) : 0m);
    }

    [Theory]
    // ราคารวม VAT ไม่มีส่วนลดบนกระดาษ — เศษจากการถอด VAT ต้องมีที่อยู่ (ข้อ 1)
    // ทางสำรอง (ราคาก่อน VAT) — ใช้เมื่อใบไม่ผ่านทางหลักเท่านั้น · เศษ 0.03 ≤ 1 บาท และ ≤ 0.5% ของ 65.42 (0.33) ⇒ ใช้ได้
    [InlineData(7, 10.00, 65.42, 70.00, 9.35, 0.03)]      // 9.35 × 7 = 65.45 > 65.42
    public void ราคารวมVAT_ไม่มีส่วนลด_บรรทัดลงตัวพอดี_บันทึกซ้ำแล้วยอดไม่หลุด(
        int qty, double gross, double net, double netIncl, double expectPrice, double expectDisc)
    {
        var n = OcrEtaxLineNormalizer.Normalize(new OcrEtaxLineFacts(
            qty, (decimal)gross, null, null, 7m, (decimal)net, (decimal)netIncl));
        Assert.Equal(OcrEtaxPriceBasis.InclusiveOfVat, n.Basis);
        Assert.Equal((decimal)qty, n.Quantity);
        Assert.Equal((decimal)expectPrice, n.UnitPrice);
        Assert.Equal((decimal)expectDisc, n.LineDiscount);
        Assert.Equal((decimal)net, n.Amount);
        Assert.Equal((decimal)net, Resave(qty, n.UnitPrice!.Value, n.LineDiscount));
        // ตัวสร้างบรรทัด/ด่านเห็นเป็นส่วนลดที่อธิบายยอดได้ (ไม่ใช่จำนวนผิด)
        Assert.Equal((decimal)expectDisc, OcrEtaxLineNormalizer.ProvenLineDiscount(qty, n.UnitPrice, n.Amount, n.LineDiscount));
        Assert.Equal((decimal)expectDisc, n.VatStripResidual);   // ทั้งก้อนคือเศษ — ไม่มีส่วนลดบนกระดาษ
    }

    [Fact]
    public void ราคารวมVAT_ถอดแล้วลงตัวอยู่แล้ว_ไม่แต่งส่วนลด()
    {
        // 3 × 37.00 (รวม VAT) = 111 ⇒ 103.74 = 3 × 34.58 พอดี ⇒ ไม่มีเศษ ไม่มีส่วนลด
        var n = OcrEtaxLineNormalizer.Normalize(new OcrEtaxLineFacts(3m, 37.00m, null, null, 7m, 103.74m, 111.00m));
        Assert.Equal(OcrEtaxPriceBasis.InclusiveOfVat, n.Basis);
        Assert.Equal((3m, 34.58m, 103.74m), (n.Quantity!.Value, n.UnitPrice!.Value, n.Amount!.Value));
        Assert.Null(n.LineDiscount);
    }

    [Fact]
    public void ใบCRC_ทุกบรรทัดบันทึกซ้ำแล้วยอดเท่าXML()
    {
        // ทางหลัก: เอกสารราคารวม VAT — ComputeLineAmounts (โหมดราคารวม VAT) คิดใหม่ตอนบันทึกซ้ำ ⇒ ก่อน VAT รายบรรทัด = NetLineTotalAmount
        var r = Crc();
        var lines = CrcInvoice().Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            var after = Resave(lines[i].Quantity!.Value, lines[i].UnitPrice!.Value, lines[i].LineDiscount);
            Assert.Equal(lines[i].Amount!.Value, after);
            Assert.Equal(r.Items[i].Amount, DocumentLineVatConvention.SplitLine(after, 7m, null, true).Net);
        }
    }

    private static OcrExtractedData CrcItemsWithoutPerLineFlag(bool keepDiscount)
    {
        var d = new OcrExtractedData();
        foreach (var li in Crc().Items)
        {
            var it = ToItem(li, Norm(li));
            it.QuantityFromEtaxXml = false;              // JSON ที่ธงหาย (เขียนกลับแบบเดิม · สำเนาเก่า)
            if (!keepDiscount) it.LineDiscountAmount = null;
            d.Items.Add(it);
        }
        return d;
    }

    [Fact]
    public void สแกนEtax_ธงรายบรรทัดหาย_engineยังคุ้มครองจำนวน()
    {
        // ข้อ 2: เส้นสร้างซ้ำ/พรีวิว/ดึงรายการซ้ำ ส่ง IsEtaxEngine(result.OcrEngine) — ส่วนลดหายด้วยก็ห้ามหารจำนวน
        var d = CrcItemsWithoutPerLineFlag(keepDiscount: false);
        OcrService.SanitizeVatSplitArtifacts(d, quantitiesFromSignedXml: OcrEtaxLineNormalizer.IsEtaxEngine("EtaxXml"));
        Assert.Equal(new[] { 3m, 4m, 12m, 10m, 6m, 6m, 4m, 1m, 120m }, d.Items.Select(x => x.Quantity!.Value).ToArray());
        // ทิศตรงข้าม: engine อื่น + ไม่มีธง + ไม่มีส่วนลด ⇒ ตัวกันจำนวนระเบิดทำงานตามเดิม (กระดาษที่อ่านหลงคอลัมน์)
        var paper = CrcItemsWithoutPerLineFlag(keepDiscount: false);
        OcrService.SanitizeVatSplitArtifacts(paper, quantitiesFromSignedXml: OcrEtaxLineNormalizer.IsEtaxEngine("AzureDI"));
        Assert.NotEqual(3m, paper.Items[0].Quantity);
        Assert.False(OcrEtaxLineNormalizer.IsEtaxEngine("Cached"));
        Assert.False(OcrEtaxLineNormalizer.IsEtaxEngine(null));
    }

    [Fact]
    public void JSONเขียนกลับตอนอนุมัติ_ชื่อช่องตรง_ส่วนลดและธงกลับมาครบ()
    {
        // รูปเดียวกับ DocumentService.BuildScanItemsJsonFromLines (ชื่อช่องต้องตรง OcrExtractedLineItem)
        const string approved = """
[{"Description":"ยางแบนรองขาแอร์","Quantity":3,"Unit":"ชิ้น","UnitPrice":34.58,"Amount":78.80,"VatRate":7,"LineDiscountAmount":24.94,"QuantityFromEtaxXml":true},
 {"Description":"ค่าขนส่ง CTD","Quantity":120,"Unit":"ชิ้น","UnitPrice":0.93,"Amount":37.38,"VatRate":7,"LineDiscountAmount":74.22,"QuantityFromEtaxXml":true}]
""";
        var items = System.Text.Json.JsonSerializer.Deserialize<List<OcrExtractedLineItem>>(approved)!;
        Assert.Equal(24.94m, items[0].LineDiscountAmount);
        Assert.True(items[0].QuantityFromEtaxXml);
        Assert.True(OcrEtaxLineNormalizer.ItemsJsonCarriesSignedQuantities(approved));
        var d = new OcrExtractedData();
        foreach (var it in items) d.Items.Add(it);
        OcrService.SanitizeVatSplitArtifacts(d);   // engine "Cached" (สำเนา) — ธงรายบรรทัดพอ
        Assert.Equal((3m, 120m), (d.Items[0].Quantity!.Value, d.Items[1].Quantity!.Value));

        // ทิศตรงข้าม = บั๊กเดิม: เขียนกลับแบบเก่า (ไม่มีสองช่อง) ⇒ จำนวนถูกหารใหม่
        const string legacy = """[{"Description":"ยางแบนรองขาแอร์","Quantity":3,"Unit":"ชิ้น","UnitPrice":34.58,"Amount":78.80,"VatRate":7}]""";
        Assert.False(OcrEtaxLineNormalizer.ItemsJsonCarriesSignedQuantities(legacy));
        var old = new OcrExtractedData();
        old.Items.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<OcrExtractedLineItem>>(legacy)!);
        OcrService.SanitizeVatSplitArtifacts(old);
        Assert.Equal(2.279m, old.Items[0].Quantity);
        Assert.False(OcrEtaxLineNormalizer.ItemsJsonCarriesSignedQuantities(null));
        Assert.False(OcrEtaxLineNormalizer.ItemsJsonCarriesSignedQuantities("{not json"));
    }

    [Fact]
    public void บรรทัดEtax_ไม่เกิน1บาท_ไม่ถูกยุบหรือลบ_ไม่รวมบรรทัดซ้ำ()
    {
        // ข้อ 5: ของจริงราคา 1 บาท (ถุง · ค่าส่ง) บนใบที่ลงนาม ห้ามถูกตีเป็น "เศษ"
        OcrExtractedData Build(bool signed)
        {
            var d = new OcrExtractedData();
            d.Items.Add(new OcrExtractedLineItem { Description = "สินค้า A", Quantity = 1m, UnitPrice = 500m, Amount = 500m, QuantityFromEtaxXml = signed });
            d.Items.Add(new OcrExtractedLineItem { Description = "ถุงพลาสติก", Quantity = 1m, UnitPrice = 0.93m, Amount = 0.93m, QuantityFromEtaxXml = signed });
            d.Items.Add(new OcrExtractedLineItem { Description = "สินค้า A", Quantity = 1m, UnitPrice = 500m, Amount = 500m, QuantityFromEtaxXml = signed });
            return d;
        }
        var etax = Build(signed: true);
        OcrService.SanitizeVatSplitArtifacts(etax);
        Assert.Equal(3, etax.Items.Count);
        Assert.Equal(0.93m, etax.Items[1].Amount);
        Assert.Equal(1000.93m, etax.Items.Sum(x => x.Amount!.Value));
        // ทิศตรงข้าม: กระดาษ OCR ⇒ เศษ 0.93 ยุบเข้าบรรทัดใหญ่ (พฤติกรรมเดิม — ยอดรวมคงเดิม บรรทัด 0.93 หายไป)
        var paper = Build(signed: false);
        OcrService.SanitizeVatSplitArtifacts(paper);
        Assert.Equal(2, paper.Items.Count);
        Assert.DoesNotContain(paper.Items, x => x.Amount == 0.93m);
        Assert.Equal(1000.93m, paper.Items.Sum(x => x.Amount!.Value));
        // บรรทัดซ้ำ (ชื่อ+ราคาเดียวกัน) บนกระดาษ OCR ยังถูกรวม · บน e-Tax ไม่รวม
        OcrExtractedData Dup(bool signed)
        {
            var d = new OcrExtractedData();
            d.Items.Add(new OcrExtractedLineItem { Description = "ค่าแรง", Quantity = 1m, UnitPrice = 300m, Amount = 300m, QuantityFromEtaxXml = signed });
            d.Items.Add(new OcrExtractedLineItem { Description = "ค่าแรง", Quantity = 1m, UnitPrice = 300m, Amount = 300m, QuantityFromEtaxXml = signed });
            return d;
        }
        var dupEtax = Dup(true); OcrService.SanitizeVatSplitArtifacts(dupEtax);
        Assert.Equal(2, dupEtax.Items.Count);
        var dupPaper = Dup(false); OcrService.SanitizeVatSplitArtifacts(dupPaper);
        Assert.Single(dupPaper.Items);
        Assert.Equal(2m, dupPaper.Items[0].Quantity);
    }

    [Fact]
    public void ทุนนำเข้าสต็อก_หลังส่วนลดบรรทัด_และDTOอ่านส่วนลดจากJSON()
    {
        // ข้อ 3: 3 × 34.58 − 24.94 = 78.80 ⇒ ทุน/หน่วย 26.2667 ไม่ใช่ 34.58
        Assert.Equal(26.2667m, OcrEtaxLineNormalizer.EffectiveUnitCost(3m, 34.58m, 78.80m, 24.94m));
        // ทิศตรงข้าม: ไม่มีส่วนลด / ส่วนลดที่ไม่อธิบายยอด ⇒ ราคาต่อหน่วยเดิม
        Assert.Equal(34.58m, OcrEtaxLineNormalizer.EffectiveUnitCost(3m, 34.58m, 103.74m, null));
        Assert.Equal(34.58m, OcrEtaxLineNormalizer.EffectiveUnitCost(3m, 34.58m, 78.80m, 5m));
        // ตัวพรีวิวนำเข้าสต็อก (OcrController.StockPreview) อ่าน ExtractedItemsJson เป็น OcrLineItemDto แบบไม่สนตัวพิมพ์ — ส่วนลดต้องมาถึง
        var json = OcrService.SerializeExtractedItems(new List<OcrExtractedLineItem>
        {
            new() { Description = "ยางแบนรองขาแอร์", Quantity = 3m, UnitPrice = 34.58m, Amount = 78.80m, LineDiscountAmount = 24.94m, QuantityFromEtaxXml = true },
        });
        var dto = System.Text.Json.JsonSerializer.Deserialize<List<Accounting.Models.DTOs.Ocr.OcrLineItemDto>>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(24.94m, dto[0].LineDiscountAmount);
        Assert.Equal(26.2667m, OcrEtaxLineNormalizer.EffectiveUnitCost(dto[0].Quantity, dto[0].UnitPrice, dto[0].Amount, dto[0].LineDiscountAmount));
    }

    // ══ ฝ่ายค้านรอบสาม (2026-10-09): ทางหลัก = ราคารวม VAT ตามกระดาษ · ทางสำรอง = ราคาก่อน VAT + เพดานเศษ ═════════════════

    [Fact]
    public void ใบCRC_ทั้งใบราคารวมVAT_เอกสารราคารวมVATด้วยค่าตามกระดาษ()
    {
        var r = Crc();
        var inv = CrcInvoice();
        Assert.True(inv.PricesIncludeVat);
        Assert.Empty(inv.Notes);
        for (var i = 0; i < r.Items.Count; i++)
        {
            var li = r.Items[i];
            var n = inv.Lines[i];
            Assert.True(n.PriceIncludesVat);
            Assert.True(n.QuantityFromDocument);
            Assert.Equal(li.Quantity, n.Quantity);              // 3 · 4 · … · 120
            Assert.Equal(li.UnitPrice, n.UnitPrice);            // 37.00 — ราคาบนกระดาษ (รวม VAT)
            Assert.Equal(li.LineAllowance, n.LineDiscount);     // 26.68 — ส่วนลดบนกระดาษ (รวม VAT)
            Assert.Equal(li.NetIncludingVatAmount, n.Amount);   // 84.32
            Assert.Null(n.VatStripResidual);                    // ไม่มีส่วนลดที่แต่งขึ้น
            Assert.Equal(7.00m, n.VatRate);
        }
        Assert.Null(OcrEtaxLineNormalizer.TiesOutInclusive(inv.Lines, r.Items.Select(x => x.Amount!.Value).ToList(), 2991.59m, 209.41m, 3201.00m));
        // VAT รายบรรทัด (สูตรเดียวกับ ComputeLineAmounts) รวม = VAT หัวใบ 209.41 ⇒ ตัวสร้างบรรทัดใช้แทนการเฉลี่ย
        var vats = OcrEtaxLineNormalizer.InclusiveLineVats(inv.Lines.Select(x => (x.Amount!.Value, 7m, true)).ToList(), 209.41m);
        Assert.NotNull(vats);
        Assert.Equal(209.41m, vats!.Sum());
        Assert.Equal(5.52m, vats[0]);                           // 84.32 − 78.80
    }

    [Theory]
    // ใบบรรทัดเดียว ราคารวม VAT ไม่มีส่วนลด — ทางหลักไม่ต้องแต่งเศษ (เทียบทางสำรองที่ต้องตั้ง 9.35 − 0.03 / 0.94 − 0.65)
    [InlineData(7, 10.00, 65.42, 4.58, 70.00)]
    [InlineData(120, 1.00, 112.15, 7.85, 120.00)]
    public void ราคารวมVAT_ไม่มีส่วนลด_ทางหลักใช้ราคาตามกระดาษ_ไม่มีเศษ(int qty, double gross, double net, double vat, double total)
    {
        var inv = OcrEtaxLineNormalizer.NormalizeInvoice(
            new[] { new OcrEtaxLineFacts(qty, (decimal)gross, null, null, 7m, (decimal)net, (decimal)total) },
            (decimal)net, (decimal)vat, (decimal)total);
        Assert.True(inv.PricesIncludeVat);
        var n = Assert.Single(inv.Lines);
        Assert.Equal(((decimal)qty, (decimal)gross, (decimal)total), (n.Quantity!.Value, n.UnitPrice!.Value, n.Amount!.Value));
        Assert.Null(n.LineDiscount);
        Assert.Empty(inv.Notes);
        // บันทึกซ้ำ (โหมดราคารวม VAT) ⇒ ก่อน VAT = XML
        Assert.Equal((decimal)net, DocumentLineVatConvention.SplitLine(Resave(qty, n.UnitPrice.Value, null), 7m, null, true).Net);
    }

    [Fact]
    public void หัวใบไม่ลงตัว_หรือไม่มีหัวใบ_ใช้ทางสำรองราคาก่อนVAT_พร้อมหมายเหตุ()
    {
        // VAT หัวใบ 209.40 ≠ Σ VAT รายบรรทัด 209.41 ⇒ ห้ามตั้งเอกสารราคารวม VAT (บันทึกซ้ำแล้วยอดหลุด)
        var bad = CrcInvoice(vat: 209.40m);
        Assert.False(bad.PricesIncludeVat);
        Assert.Contains(bad.Notes, x => x.StartsWith("[e-Tax] ราคาบนเอกสารรวม VAT ทุกบรรทัด", StringComparison.Ordinal));
        Assert.All(bad.Lines, n => Assert.False(n.PriceIncludesVat));
        Assert.Equal(OcrEtaxPriceBasis.Unknown, bad.Lines[^1].Basis);              // ค่าขนส่ง: เศษเกินเพดาน
        Assert.Equal((3m, 34.58m, 24.94m), (bad.Lines[0].Quantity!.Value, bad.Lines[0].UnitPrice!.Value, bad.Lines[0].LineDiscount!.Value));
        Assert.Contains(bad.Notes, x => x == "[e-Tax] เศษจากถอด VAT 0.01 บาท ไม่ใช่ส่วนลดบนเอกสาร — บรรทัดที่ 1 (ส่วนลดก่อน VAT 24.94 รวมเศษนี้แล้ว)");
        // ไม่มียอดหัวใบให้เทียบ ⇒ ทางสำรองเช่นกัน
        Assert.False(CrcInvoice(lineTotal: null).PricesIncludeVat);
    }

    [Fact]
    public void VATรายบรรทัดรวมไม่เท่าเป้าของReconcileTaxRounding_ใช้ทางสำรอง()
    {
        // 3 บรรทัด × 1.00 รวม VAT: ก่อน VAT 0.93 + VAT 0.07 ต่อบรรทัด ⇒ Σ VAT 0.21 แต่ round(2.79 × 7%) = 0.20 ⇒ DocumentService จะขยับ
        // บรรทัดใหญ่สุด 1 สตางค์ ⇒ ก่อน VAT รายบรรทัดหลุดจาก XML ⇒ ห้ามทางหลัก
        var f = new OcrEtaxLineFacts(1m, 1.00m, null, null, 7m, 0.93m, 1.00m);
        var inv = OcrEtaxLineNormalizer.NormalizeInvoice(new[] { f, f, f }, 2.79m, 0.21m, 3.00m);
        Assert.False(inv.PricesIncludeVat);
        Assert.Contains(inv.Notes, x => x.Contains("ระบบจะขยับบรรทัดใหญ่สุด", StringComparison.Ordinal));
        Assert.Null(OcrEtaxLineNormalizer.InclusiveLineVats(new[] { (1.00m, 7m, true), (1.00m, 7m, true), (1.00m, 7m, true) }, 0.20m));
    }

    [Fact]
    public void บรรทัดปนราคารวมและไม่รวมVAT_ใช้ทางสำรอง()
    {
        var incl = new OcrEtaxLineFacts(3m, 37.00m, 26.68m, null, 7m, 78.80m, 84.32m);
        var excl = new OcrEtaxLineFacts(2m, 250.47m, null, null, 7m, 500.93m, 536.00m);
        var inv = OcrEtaxLineNormalizer.NormalizeInvoice(new[] { incl, excl }, 579.73m, 40.59m, 620.32m);
        Assert.False(inv.PricesIncludeVat);
        Assert.Equal(OcrEtaxPriceBasis.InclusiveOfVat, inv.Lines[0].Basis);
        Assert.False(inv.Lines[0].PriceIncludesVat);
        Assert.Equal(OcrEtaxPriceBasis.ExclusiveOfVat, inv.Lines[1].Basis);
        Assert.Equal((2m, 250.47m, 500.93m), (inv.Lines[1].Quantity!.Value, inv.Lines[1].UnitPrice!.Value, inv.Lines[1].Amount!.Value));
    }

    [Fact]
    public void เศษจากถอดVATเกินเพดาน_ไม่ตัดสิน()
    {
        // 120 × 1.00 รวม VAT ทางสำรอง: 0.94 × 120 − 112.15 = 0.65 > 0.5% ของ 112.15 (0.56) ⇒ Unknown (ค่าเดิม)
        var n = OcrEtaxLineNormalizer.Normalize(new OcrEtaxLineFacts(120m, 1.00m, null, null, 7m, 112.15m, 120.00m));
        Assert.Equal(OcrEtaxPriceBasis.Unknown, n.Basis);
        Assert.Equal((120m, 1.00m, 112.15m), (n.Quantity!.Value, n.UnitPrice!.Value, n.Amount!.Value));
        Assert.Null(n.LineDiscount);
        Assert.Null(n.VatStripResidual);
        // เกิน 1 บาท (บรรทัดใหญ่): 1,000 × 0.10 รวม VAT ⇒ 0.09 × 1,000 = 90 < 93.46 ⇒ 0.10 × 1,000 − 93.46 = 6.54 > 1.00 ⇒ Unknown
        var big = OcrEtaxLineNormalizer.Normalize(new OcrEtaxLineFacts(1000m, 0.10m, null, null, 7m, 93.46m, 100.00m));
        Assert.Equal(OcrEtaxPriceBasis.Unknown, big.Basis);
        // ทั้งใบที่ไม่ผ่านทางหลัก ⇒ ไม่มีหมายเหตุเศษของบรรทัดที่ไม่ตัดสิน
        var inv = OcrEtaxLineNormalizer.NormalizeInvoice(new[] { new OcrEtaxLineFacts(120m, 1.00m, null, null, 7m, 112.15m, 120.00m) }, null, null, null);
        Assert.False(inv.PricesIncludeVat);
        Assert.DoesNotContain(inv.Notes, x => x.Contains("เศษจากถอด VAT", StringComparison.Ordinal));
    }

    [Fact]
    public void Shopee_ทั้งใบ_ไม่เปลี่ยน()
    {
        var r = EtaxPdfXmlExtractor.ParseEtaxXml(EtaxFixtures.ShopeeUptoyouXml)!;
        var inv = OcrEtaxLineNormalizer.NormalizeInvoice(r.Items.Select(Facts).ToList(), r.LineTotal, r.VatAmount, r.GrandTotal);
        Assert.False(inv.PricesIncludeVat);
        Assert.Empty(inv.Notes);
        var n = Assert.Single(inv.Lines);
        Assert.Equal((OcrEtaxPriceBasis.ExclusiveOfVat, 2m, 250.47m, 500.93m), (n.Basis, n.Quantity!.Value, n.UnitPrice!.Value, n.Amount!.Value));
        Assert.Null(n.LineDiscount);
        Assert.False(n.PriceIncludesVat);
    }

    [Fact]
    public void ป้ายส่วนลด_เซิร์ฟเวอร์บอกฐานและเศษ()
    {
        Assert.Null(OcrEtaxLineNormalizer.DiscountLabel(null, false, null));
        Assert.Contains("รวม VAT", OcrEtaxLineNormalizer.DiscountLabel(26.68m, true, null));
        Assert.Contains("เศษจากการถอด VAT 0.03 บาท (ไม่ใช่ส่วนลดบนเอกสาร)", OcrEtaxLineNormalizer.DiscountLabel(0.03m, false, 0.03m));
        Assert.Equal("ส่วนลดของบรรทัด (ก่อน VAT)", OcrEtaxLineNormalizer.DiscountLabel(50m, false, null));
    }

    [Fact]
    public void ราคารวมVAT_ทุนสต็อกถอดVATก่อนหาร_และJSONเขียนกลับรอบใหม่()
    {
        // 3 × 37.00 − 26.68 = 84.32 รวม VAT ⇒ 78.80 ÷ 3 = 26.2667 (ไม่ใช่ 28.1067)
        Assert.Equal(26.2667m, OcrEtaxLineNormalizer.EffectiveUnitCost(3m, 37.00m, 84.32m, 26.68m, priceIncludesVat: true, vatRate: 7m));
        Assert.Equal(34.58m, OcrEtaxLineNormalizer.EffectiveUnitCost(3m, 37.00m, 111.00m, null, priceIncludesVat: true, vatRate: 7m));
        // ทิศตรงข้าม: ไม่ใช่ราคารวม VAT ⇒ สูตรเดิม
        Assert.Equal(37.00m, OcrEtaxLineNormalizer.EffectiveUnitCost(3m, 37.00m, 111.00m, null));
        // เขียนกลับตอนอนุมัติเอกสารราคารวม VAT: ยอด = ก่อน VAT + VAT (ฐานเดียวกับราคา/ส่วนลด) ⇒ ส่วนลดยังอธิบายยอดได้
        const string approvedIncl = """[{"Description":"ยางแบนรองขาแอร์","Quantity":3,"Unit":"ชิ้น","UnitPrice":37.00,"Amount":84.32,"VatRate":7,"LineDiscountAmount":26.68,"QuantityFromEtaxXml":true,"PriceIncludesVat":true}]""";
        var item = System.Text.Json.JsonSerializer.Deserialize<List<OcrExtractedLineItem>>(approvedIncl)![0];
        Assert.True(item.PriceIncludesVat);
        Assert.Equal(26.68m, OcrEtaxLineNormalizer.ProvenLineDiscount(item.Quantity, item.UnitPrice, item.Amount, item.LineDiscountAmount));
        var dto = System.Text.Json.JsonSerializer.Deserialize<List<Accounting.Models.DTOs.Ocr.OcrLineItemDto>>(approvedIncl,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })![0];
        Assert.True(dto.PriceIncludesVat);
        Assert.Equal(26.2667m, OcrEtaxLineNormalizer.EffectiveUnitCost(dto.Quantity, dto.UnitPrice, dto.Amount, dto.LineDiscountAmount, dto.PriceIncludesVat, dto.VatRate));
        // ทิศตรงข้าม = บั๊กของรอบสอง: ส่วนลดรวม VAT คู่กับยอดก่อน VAT ⇒ ส่วนลดไม่อธิบายยอด
        Assert.Equal(0m, OcrEtaxLineNormalizer.ProvenLineDiscount(3m, 37.00m, 78.80m, 26.68m));
    }
}
