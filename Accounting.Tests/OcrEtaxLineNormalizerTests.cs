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

    /// <summary>บรรทัดเอกสาร (สิ่งที่ MapEtaxToOcrData ประกอบ) จากผลตัดสิน</summary>
    private static OcrExtractedLineItem ToItem(EtaxPdfXmlExtractor.LineItem li, OcrEtaxLine n) => new()
    {
        Description = li.Description, Quantity = n.Quantity, UnitPrice = n.UnitPrice, Amount = n.Amount,
        LineDiscountAmount = n.LineDiscount, QuantityFromEtaxXml = n.QuantityFromDocument,
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
        var ship = Norm(items.Single(x => x.LineNo == 10));   // "ค่าขนส่ง CTD"
        Assert.Equal((120m, 0.93m, 74.22m, 37.38m), (ship.Quantity!.Value, ship.UnitPrice!.Value, ship.LineDiscount!.Value, ship.Amount!.Value));
    }

    [Fact]
    public void ใบCRC_ผลรวมบรรทัดตรงหัวใบ_ส่วนลดหัวใบไม่ถูกหักซ้ำ()
    {
        var r = Crc();
        Assert.Equal(9, r.Items.Count);
        var lines = r.Items.Select(Norm).ToList();
        Assert.Equal(2991.59m, lines.Sum(x => x.Amount!.Value));
        Assert.Equal(r.LineTotal, lines.Sum(x => x.Amount!.Value));
        Assert.Equal(2991.59m, r.TaxBasis);
        Assert.Equal(209.41m, r.VatAmount);
        Assert.Equal(3201.00m, r.GrandTotal);
        Assert.Equal(r.GrandTotal, r.LineTotal + r.VatAmount);
        // AllowanceTotalAmount 1,080.00 (รวม VAT) = Σ ส่วนลดบรรทัด — อยู่ในยอดบรรทัดแล้ว: LineTotal − TaxBasis = 0 ⇒ ไม่มีส่วนลดท้ายบิล
        Assert.Equal(1080.00m, r.Items.Sum(x => x.LineAllowance!.Value));
        Assert.Equal(0m, r.LineTotal!.Value - r.TaxBasis!.Value);
        // Σ (จำนวน × ราคาก่อน VAT − ส่วนลดก่อน VAT) = ฐานภาษีหัวใบพอดี
        Assert.Equal(2991.59m, lines.Sum(x => R2(x.Quantity!.Value * x.UnitPrice!.Value) - x.LineDiscount!.Value));
    }

    [Fact]
    public void ใบCRC_ตัวกันจำนวนระเบิดไม่หารจำนวนใหม่_ยอดคงเดิม()
    {
        var r = Crc();
        var d = new OcrExtractedData();
        foreach (var li in r.Items) d.Items.Add(ToItem(li, Norm(li)));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(9, d.Items.Count);
        Assert.Equal(new[] { 3m, 4m, 12m, 10m, 6m, 6m, 4m, 1m, 120m }, d.Items.Select(x => x.Quantity!.Value).ToArray());
        Assert.Equal(2991.59m, d.Items.Sum(x => x.Amount!.Value));
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
        var lines = Crc().Items.Select(Norm).ToList();
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
        Assert.Equal((78.80m, (decimal?)24.94m), OcrEtaxLineNormalizer.AmountAfterLineDiscount(3m, 34.58m, 24.94m));
        // ผู้ใช้ลดราคาจนส่วนลดเกินยอดก่อนลด ⇒ ทิ้งส่วนลด (ยอดติดลบไม่มีจริง)
        Assert.Equal((10.00m, (decimal?)null), OcrEtaxLineNormalizer.AmountAfterLineDiscount(1m, 10.00m, 24.94m));
        // ไม่มีส่วนลด = สูตรเดิม round(จำนวน × ราคา)
        Assert.Equal((103.74m, (decimal?)null), OcrEtaxLineNormalizer.AmountAfterLineDiscount(3m, 34.58m, null));
    }
}
