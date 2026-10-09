using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **สแกนจริง f1690d11 (2026-10-09)** — e-Tax XML (PDF/A-3) จาก บริษัท ซีอาร์ซี ไทวัสดุ จำกัด เลขที่ SRCIE26100075384
/// (T03 ใบเสร็จรับเงิน/ใบกำกับภาษี) ผู้ซื้อ = หจก.แอม แฮปปี้เนส (กิจการโรงแรม) · 9 บรรทัด: อุปกรณ์แอร์/วัสดุ 8 บรรทัด
/// 2,954.21 + "ค่าขนส่ง CTD" 37.38 · รวมก่อน VAT 2,991.59 · VAT 209.41 · รวม 3,201.00
///
/// <para>ผลผิดสามข้อที่ผู้ใช้เห็น (และเทสต์ล็อกทั้งสองทิศ):
/// (1) [DEPOSIT-BUY] จากแม่แบบ XML "หักเงินมัดจำ 0.00" + ช่อง <c>DepositAllowanceChargeInd1</c> ·
/// (2) หมวด "ค่าขนส่ง" + [WHT-SUGGEST] 1% บนยอดทั้งใบ ≈ 29.92 จากบรรทัดค่าส่ง 1.25% ของมูลค่า ·
/// (3) Dr 21230 เจ้าหนี้กรรมการ / Cr 51530 ต้นทุนซ่อมบำรุงห้องพัก (กลับด้าน)</para>
/// </summary>
public class OcrF1690EtaxScanTests
{
    /// <summary>XML จริงที่ตัดเหลือโครงที่เกี่ยว (ค่าทุกตัวคัดจากไฟล์ — ไม่แต่ง)</summary>
    internal const string RealXml = """
        <?template name="TIV0102" xslt="V10000"?><?xml-model href="TaxInvoice_Schematron_2p0.sch" type="application/xml"?><rsm:TaxInvoice_CrossIndustryInvoice xmlns:ram="urn:etda:uncefact:data:standard:TaxInvoice_ReusableAggregateBusinessInformationEntity:2" xmlns:rsm="urn:etda:uncefact:data:standard:TaxInvoice_CrossIndustryInvoice:2">
            <rsm:ExchangedDocument>
                <ram:ID>SRCIE26100075384</ram:ID>
                <ram:Name>ใบเสร็จรับเงิน/ใบกำกับภาษี</ram:Name>
                <ram:TypeCode listAgencyID="RD/ETDA">T03</ram:TypeCode>
                <ram:IssueDateTime>2026-10-05T23:15:00</ram:IssueDateTime>
                <ram:Purpose>-</ram:Purpose>
                <ram:IncludedNote>
                    <ram:Subject>TradeAllowanceReason</ram:Subject>
                    <ram:Content>หักเงินมัดจำ</ram:Content>
                </ram:IncludedNote>
                <ram:IncludedNote>
                    <ram:Subject>DepositAllowanceChargeInd1</ram:Subject>
                    <ram:Content>-</ram:Content>
                </ram:IncludedNote>
                <ram:IncludedNote>
                    <ram:Subject>DepositAllowanceActualAmount1</ram:Subject>
                    <ram:Content>-</ram:Content>
                </ram:IncludedNote>
                <ram:IncludedNote>
                    <ram:Subject>SellerContactPhoneNo</ram:Subject>
                    <ram:Content>-</ram:Content>
                </ram:IncludedNote>
            </rsm:ExchangedDocument>
            <rsm:SupplyChainTradeTransaction>
                <ram:ApplicableHeaderTradeAgreement>
                    <ram:SellerTradeParty>
                        <ram:Name>บริษัท ซีอาร์ซี ไทวัสดุ จำกัด</ram:Name>
                    </ram:SellerTradeParty>
                    <ram:BuyerTradeParty>
                        <ram:Name>ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส</ram:Name>
                    </ram:BuyerTradeParty>
                </ram:ApplicableHeaderTradeAgreement>
                <ram:ApplicableHeaderTradeSettlement>
                    <ram:SpecifiedTradeAllowanceCharge>
                        <ram:ChargeIndicator>false</ram:ChargeIndicator>
                        <ram:ActualAmount currencyID="THB">0.00</ram:ActualAmount>
                        <ram:Reason>หักเงินมัดจำ</ram:Reason>
                    </ram:SpecifiedTradeAllowanceCharge>
                    <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
                        <ram:LineTotalAmount currencyID="THB">2991.59</ram:LineTotalAmount>
                        <ram:TaxTotalAmount currencyID="THB">209.41</ram:TaxTotalAmount>
                        <ram:GrandTotalAmount currencyID="THB">3201.00</ram:GrandTotalAmount>
                    </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
                </ram:ApplicableHeaderTradeSettlement>
                <ram:IncludedSupplyChainTradeLineItem>
                    <ram:SpecifiedTradeProduct>
                        <ram:Name>ค่าขนส่ง CTD</ram:Name>
                    </ram:SpecifiedTradeProduct>
                    <ram:SpecifiedLineTradeDelivery>
                        <ram:BilledQuantity unitCode="-">120.00</ram:BilledQuantity>
                    </ram:SpecifiedLineTradeDelivery>
                    <ram:SpecifiedLineTradeSettlement>
                        <ram:SpecifiedTradeAllowanceCharge>
                            <ram:ChargeIndicator>false</ram:ChargeIndicator>
                            <ram:ActualAmount currencyID="THB">80.00</ram:ActualAmount>
                        </ram:SpecifiedTradeAllowanceCharge>
                    </ram:SpecifiedLineTradeSettlement>
                </ram:IncludedSupplyChainTradeLineItem>
            </rsm:SupplyChainTradeTransaction>
            <ds:Signature xmlns:ds="http://www.w3.org/2000/09/xmldsig#" Id="xmldsig-57db8e8b">
                <ds:SignatureValue>BnbqRq5kmYigV6NRUoPshjztbV0fkm+6pzb6aS7pris6UbEOpTd0ZTqPo</ds:SignatureValue>
                <ds:X509IssuerName>C=TH,O=Internet Thailand Public Company Limited</ds:X509IssuerName>
            </ds:Signature>
        </rsm:TaxInvoice_CrossIndustryInvoice>
        """;

    /// <summary>บรรทัดจริงทั้ง 9 (ชื่อ + NetLineTotalAmount จาก XML)</summary>
    internal static readonly (string Description, decimal Amount)[] RealLines =
    {
        ("ยางแบนรองขาแอร์  TEK 1 ชุด (4 ชิ้น) ดำ", 78.80m),
        ("ขาแขวนคอยล์ร้อนแอร์ 2.2 มม. 50 CM. TEK  เบจ", 650.34m),
        ("รางครอบท่อแอร์Abco LEETECH A-AR75 ขาว 2ม.", 1047.93m),
        ("ข้อต่อตรง LEETECH A-JCAR75 ขาว", 198.79m),
        ("ข้องอโค้ง LEETECH A-JFAR75 ขาว", 183.18m),
        ("ข้องอมุม LEETECH A-JEAR75 ขาว", 183.18m),
        ("ฝาครอบ LEETECH A-JAAR75 ขาว", 122.11m),
        ("เครื่องตรวจหาโครงผนังสำหรับผนังหนา DEWALT DW0150 1-1/2 นิ้ว เหลือง-ดำ", 489.88m),
        ("ค่าขนส่ง CTD", 37.38m),
    };

    private const string Vendor = "บริษัท ซีอาร์ซี ไทวัสดุ จำกัด";

    // ═══════════════ (1) มัดจำ — OcrDepositMarker + OcrEtaxXmlText ═══════════════

    [Fact]
    public void มัดจำ_XML_แม่แบบหักเงินมัดจำ0_และช่องDepositAllowance_ไม่ใช่ใบมัดจำ()
    {
        var d = OcrDepositMarker.Decide(RealXml, RealLines.Select(l => ((string?)l.Description, (decimal?)l.Amount)));
        Assert.False(d.IsDeposit);
        Assert.Contains("แม่แบบ", d.Reason);
    }

    [Fact]
    public void มัดจำ_ต้นเหตุ_ชื่อช่องDepositAllowanceChargeInd1_ถ้าอ่านเป็นข้อความ_ติดธงผิด()
    {
        // ยืนยันว่าเทสต์ข้างบนจับตัวที่ถูก: ไฟล์เดียวกันที่ตัวตัดสินไม่รู้ว่าเป็น XML (เส้นข้อความเดิม) เห็น
        // "DepositAllowanceChargeInd1" = คำว่า DEPOSIT + เงิน 1 บาทบนบรรทัดเดียวกัน ⇒ ใบมัดจำปลอม
        var asPlainText = RealXml.Replace("CrossIndustryInvoice", "Document");
        Assert.False(OcrEtaxXmlText.IsEtaxXml(asPlainText));
        var d = OcrDepositMarker.Decide(asPlainText, null);
        Assert.True(d.IsDeposit);
        Assert.Contains("DepositAllowanceChargeInd1", d.Reason);
    }

    [Fact]
    public void มัดจำ_XML_รายการเป็นมัดจำและมียอด_ยังติดธง()
    {
        // ทิศตรงข้าม: e-Tax ใบรับเงินมัดจำจริง (รายการ "ค่ามัดจำห้องพัก" 5,000) ต้องยังเป็นมัดจำ
        var d = OcrDepositMarker.Decide(RealXml, new[] { ((string?)"ค่ามัดจำห้องพัก 30%", (decimal?)5000m) });
        Assert.True(d.IsDeposit);
    }

    [Fact]
    public void มัดจำ_XML_ชื่อเอกสารระบุมัดจำ_ยังติดธง()
    {
        var xml = RealXml.Replace("<ram:Name>ใบเสร็จรับเงิน/ใบกำกับภาษี</ram:Name>",
            "<ram:Name>ใบรับเงินมัดจำ/ใบกำกับภาษี</ram:Name>");
        var d = OcrDepositMarker.Decide(xml, new[] { ((string?)"ค่างวดงานติดตั้ง", (decimal?)10000m) });
        Assert.True(d.IsDeposit);
        Assert.Contains("ชื่อเอกสาร", d.Reason);
    }

    [Fact]
    public void มัดจำ_XML_หักเงินมัดจำมียอดจริง_คือใบสุดท้ายที่หักมัดจำเดิม_ไม่ใช่ใบมัดจำ()
    {
        // ">0.00<" มีที่เดียว (แถวส่วนลดบรรทัดค่าส่งคือ ">80.00<")
        var xml = RealXml.Replace("\">0.00</ram:ActualAmount>", "\">3000.00</ram:ActualAmount>");
        Assert.Contains("3000.00", xml);
        var d = OcrDepositMarker.Decide(xml, null);
        Assert.False(d.IsDeposit);
        Assert.Contains("3,000.00", d.Reason);
    }

    [Fact]
    public void มัดจำ_ข้อความกระดาษปกติ_ไม่ถูกแตะด้วยเส้น_XML()
    {
        // ใบมัดจำที่อ่านจากภาพ (ไม่ใช่ XML) ยังเดินกติกาข้อความเดิม
        Assert.False(OcrEtaxXmlText.IsEtaxXml("ใบเสร็จรับเงิน\nรับเงินมัดจำ 30%\n15,000.00"));
        Assert.True(OcrDepositMarker.Decide("ใบเสร็จรับเงิน\nรับเงินมัดจำ 30%\n15,000.00", null).IsDeposit);
    }

    [Fact]
    public void ข้อความXML_ตัดชื่อแท็ก_ชื่อช่อง_ลายมือชื่อ_คงข้อความผู้ขาย()
    {
        Assert.True(OcrEtaxXmlText.IsEtaxXml(RealXml));
        var c = OcrEtaxXmlText.ContentOnly(RealXml);
        Assert.DoesNotContain("Deposit", c, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Delivery", c, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Internet", c, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Phone", c, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ค่าขนส่ง CTD", c);
        Assert.Contains("หักเงินมัดจำ", c);
        Assert.Contains("SRCIE26100075384", c);
        // ข้อความที่ไม่ใช่ XML คืนตามเดิม
        Assert.Equal("ใบกำกับภาษี\nค่าบริการ 1,000.00", OcrEtaxXmlText.ContentOnly("ใบกำกับภาษี\nค่าบริการ 1,000.00"));
    }

    // ═══════════════ (2) หมวด/WHT จากบรรทัดส่วนน้อย — OcrLineValueShare + ExpenseCategoryResolver ═══════════════

    private static List<OcrPricedLine> Priced((string Description, decimal Amount)[] lines)
        => lines.Select(l => new OcrPricedLine(l.Description, l.Amount)).ToList();

    [Fact]
    public void สัดส่วน_ค่าขนส่ง37_38จาก2991_59_เป็นส่วนน้อย()
    {
        var s = OcrLineValueShare.Measure(Priced(RealLines), d => d.Contains("ขนส่ง"));
        Assert.True(s.Known);
        Assert.Equal(37.38m, s.MatchedValue);
        Assert.Equal(2991.59m, s.TotalValue);
        Assert.True(s.IsMinority);
    }

    [Fact]
    public void สัดส่วน_ไม่รู้ยอด_ไม่ถือว่าส่วนน้อย()
    {
        var s = OcrLineValueShare.Measure(new[] { new OcrPricedLine("ค่าขนส่ง", 0m) }, d => d.Contains("ขนส่ง"));
        Assert.False(s.Known);
        Assert.False(s.IsMinority);
    }

    [Fact]
    public void หมวด_ใบซื้อวัสดุที่มีค่าส่งบรรทัดเดียว_ไม่เป็นค่าขนส่ง_ไม่เสนอหัก_ณ_ที่จ่าย()
    {
        var diags = new List<string>();
        var raw = OcrEtaxXmlText.ContentOnly(RealXml);
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: Vendor, headerDescription: null,
            lineDescriptions: RealLines.Select(l => (string?)l.Description).ToList(),
            rawText: raw, industry: IndustryType.Hotel,
            pricedLines: Priced(RealLines), diagnostics: diags);

        Assert.NotNull(result);
        Assert.NotEqual("ค่าขนส่ง / ค่าจัดส่ง", result!.Category);
        Assert.Null(result.StatutoryWhtRate);
        var data = new OcrExtractedData();
        ExpenseCategoryResolver.ApplyTo(data, result, raw);
        Assert.Null(data.SuggestedWhtRate);
        Assert.Null(data.WhtIncomeTypeCode);
        // เหตุผลต้องถึงผู้ใช้ — รวมเกณฑ์ 1,000 บาทของยอดส่วนนั้น
        Assert.Contains(diags, d => d.Contains("ค่าขนส่ง CTD") && d.Contains("ต่ำกว่าเกณฑ์ 1,000"));
    }

    [Fact]
    public void หมวด_ก่อนแก้_ไม่ส่งยอดรายบรรทัด_ค่าขนส่งชนะเหมือนที่ผู้ใช้เห็น()
    {
        // ยืนยันว่าเทสต์ข้างบนจับตัวที่ถูก: ไม่ส่ง pricedLines + ใช้ XML ดิบ = พฤติกรรมเดิม (score 2.0 จาก "ขนส่ง" + "delivery" ในชื่อแท็ก)
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: Vendor, headerDescription: null,
            lineDescriptions: RealLines.Select(l => (string?)l.Description).ToList(),
            rawText: RealXml, industry: IndustryType.Hotel);
        Assert.NotNull(result);
        Assert.Equal("ค่าขนส่ง / ค่าจัดส่ง", result!.Category);
        Assert.True(result.MoneyBackedEvidence);
    }

    [Fact]
    public void หมวด_ทิศตรงข้าม_ใบที่ส่วนใหญ่เป็นค่าขนส่ง_ยังได้ค่าขนส่งและข้อเสนอหัก1เปอร์เซ็นต์()
    {
        const string raw = "ค่าขนส่งสินค้า / Shipping Fee กรุงเทพ-ชลบุรี 5,000.00";
        var lines = new[] { ("ค่าขนส่งสินค้า / Shipping Fee กรุงเทพ-ชลบุรี", 5000m), ("ค่าพาเลทไม้", 300m) };
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "หจก. สมชายพาณิชย์", headerDescription: null,
            lineDescriptions: lines.Select(l => (string?)l.Item1).ToList(),
            rawText: raw, industry: IndustryType.Hotel,
            pricedLines: Priced(lines));
        Assert.NotNull(result);
        Assert.Equal("ค่าขนส่ง / ค่าจัดส่ง", result!.Category);
        Assert.True(result.MoneyBackedEvidence);
        var data = new OcrExtractedData();
        ExpenseCategoryResolver.ApplyTo(data, result, raw);
        Assert.Equal(1m, data.SuggestedWhtRate);
        Assert.Equal("8tr", data.WhtIncomeTypeCode);
    }

    [Fact]
    public void หมวด_ค่าส่งส่วนน้อยแต่ถึง1000บาท_บอกให้ตรวจหักเฉพาะส่วนนั้น()
    {
        var lines = new[] { ("ตู้เย็น 2 ประตู", 20000m), ("ค่าขนส่งและติดตั้งนอกเขต", 1500m) };
        var diags = new List<string>();
        ExpenseCategoryResolver.Resolve(
            vendorName: "หจก. สมชายพาณิชย์", headerDescription: null,
            lineDescriptions: lines.Select(l => (string?)l.Item1).ToList(),
            rawText: null, industry: IndustryType.Hotel,
            pricedLines: Priced(lines), diagnostics: diags);
        Assert.Contains(diags, d => d.Contains("ถึงเกณฑ์ 1,000 บาท") && d.Contains("1,500.00"));
    }

    [Fact]
    public void หมวด_แบรนด์ขนส่งในชื่อผู้ขาย_ยังชนะแม้บรรทัดค่าส่งเป็นส่วนน้อย()
    {
        // ชื่อผู้ขายผูกกับ "ใครรับเงิน" — เคอรี่ที่ขายกล่องพัสดุพร้อมค่าส่งเล็กน้อย ยังเป็นผู้ขนส่ง
        var lines = new[] { ("กล่องพัสดุ เบอร์ D", 900m), ("ค่าขนส่ง", 60m) };
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "บริษัท เคอรี่ เอ็กซ์เพรส (ประเทศไทย) จำกัด", headerDescription: null,
            lineDescriptions: lines.Select(l => (string?)l.Item1).ToList(),
            rawText: null, pricedLines: Priced(lines));
        Assert.NotNull(result);
        Assert.Equal("ค่าขนส่ง / ค่าจัดส่ง", result!.Category);
    }

    // ═══════════════ (3) Dr/Cr กลับด้าน — OcrAccountPlacement ═══════════════

    private static readonly GlAccountCandidate Director21230 =
        new("21230", "เจ้าหนี้กรรมการ/เงินทดรองรับจากกรรมการ", AccountType.Liability);
    private static readonly GlAccountCandidate RoomRepair51530 =
        new("51530", "ต้นทุนซ่อมบำรุงห้องพัก", AccountType.Expense);
    private static readonly GlAccountCandidate Supplies52120 =
        new("52120", "วัสดุสิ้นเปลือง (บริการ)", AccountType.Expense);
    private static readonly GlAccountCandidate Cash11111 = new("11111", "เงินสด", AccountType.Asset);

    [Fact]
    public void คู่บัญชี_Dr21230_Cr51530_กลับด้าน_ถูกสลับ_ทั้งค่าระบบและค่าผู้ใช้()
    {
        foreach (var system in new[] { true, false })
        {
            var r = OcrAccountPlacement.Check(Director21230, RoomRepair51530, debitIsSystemSuggested: system);
            Assert.Equal(OcrAccountPlacement.Verdict.Swapped, r.Verdict);
            Assert.Equal("51530", r.DebitCode);
            Assert.Equal("21230", r.CreditCode);
            Assert.Contains("กลับด้าน", r.Reason);
        }
    }

    [Fact]
    public void คู่บัญชี_ทิศตรงข้าม_Dr51530_Cr21230_ถูกอยู่แล้ว_ไม่แตะ()
    {
        var r = OcrAccountPlacement.Check(RoomRepair51530, Director21230, debitIsSystemSuggested: true);
        Assert.Equal(OcrAccountPlacement.Verdict.Ok, r.Verdict);
        Assert.Equal("51530", r.DebitCode);
        Assert.Equal("21230", r.CreditCode);
        Assert.Null(r.Reason);
        var cash = OcrAccountPlacement.Check(Supplies52120, Cash11111, debitIsSystemSuggested: true);
        Assert.Equal(OcrAccountPlacement.Verdict.Ok, cash.Verdict);
    }

    [Fact]
    public void คู่บัญชี_แหล่งเงินเป็นค่าใช้จ่าย_ตัดทิ้ง_เดบิตคงไว้()
    {
        var r = OcrAccountPlacement.Check(Supplies52120, RoomRepair51530, debitIsSystemSuggested: false);
        Assert.Equal(OcrAccountPlacement.Verdict.CreditRejected, r.Verdict);
        Assert.Equal("52120", r.DebitCode);
        Assert.Null(r.CreditCode);
    }

    [Fact]
    public void คู่บัญชี_เดบิตหนี้สินลำพัง_ระบบเสนอ_ตัดทิ้ง_แต่ผู้ใช้เลือกเอง_คงไว้()
    {
        // ระบบ (ตัวเรียนรู้ที่ถูกสอนด้วย 21230) ⇒ ตัด · ผู้ใช้ทำใบสำคัญจ่ายชำระหนี้กรรมการเอง (Dr 21230 / Cr เงินสด) ⇒ ทางไปต่อยังอยู่
        var sys = OcrAccountPlacement.Check(Director21230, Cash11111, debitIsSystemSuggested: true);
        Assert.Equal(OcrAccountPlacement.Verdict.DebitRejected, sys.Verdict);
        Assert.Null(sys.DebitCode);
        Assert.Equal("11111", sys.CreditCode);
        var user = OcrAccountPlacement.Check(Director21230, Cash11111, debitIsSystemSuggested: false);
        Assert.Equal(OcrAccountPlacement.Verdict.Ok, user.Verdict);
        Assert.Equal("21230", user.DebitCode);
    }

    [Fact]
    public void คู่บัญชี_ไม่รู้ประเภท_ไม่ตัดสินแทน()
    {
        var r = OcrAccountPlacement.Check(null, null, debitIsSystemSuggested: true);
        Assert.Equal(OcrAccountPlacement.Verdict.Ok, r.Verdict);
        var onlyDebit = OcrAccountPlacement.Check(RoomRepair51530, null, debitIsSystemSuggested: true);
        Assert.Equal(OcrAccountPlacement.Verdict.Ok, onlyDebit.Verdict);
    }

    [Fact]
    public void คู่บัญชี_ใช้เฉพาะใบซื้อที่เดบิตค่าใช้จ่าย()
    {
        Assert.True(OcrAccountPlacement.AppliesTo("PaymentVoucher", isSalesSide: false));
        Assert.True(OcrAccountPlacement.AppliesTo("PurchaseInvoice", isSalesSide: false));
        Assert.True(OcrAccountPlacement.AppliesTo("Expense", isSalesSide: false));
        // ใบลดหนี้ฝั่งซื้อ = Dr เจ้าหนี้ / Cr ต้นทุน เป็นรายการจริง · ฝั่งขาย Cr รายได้ ⇒ ไม่ตรวจ
        Assert.False(OcrAccountPlacement.AppliesTo("CreditNote", isSalesSide: false));
        Assert.False(OcrAccountPlacement.AppliesTo("PaymentVoucher", isSalesSide: true));
        Assert.False(OcrAccountPlacement.AppliesTo(null, isSalesSide: false));
    }
}
