using System.Globalization;
using System.Text;
using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// <b>เมทริกซ์รูปแบบ e-Tax XML (สังเคราะห์)</b> — รอบ 7 (ผู้ใช้ 2026-10-09 "ครอบคลุมทุกรูปแบบเอกสาร"): คลังกระดาษจริง
/// (<see cref="EtaxGoldenCorpusTests"/>) มีแค่สองธรรมเนียม (Shopee ราคาก่อน VAT · CRC ราคารวม VAT + ส่วนลดบรรทัด) ⇒ ไล่รูปแบบที่ ขมธอ.3-2560
/// อนุญาตแต่ยังไม่เจอบนกระดาษจริง · <b>กติกาเดียว</b>: ทุกรูปแบบต้องได้ "เอกสารที่ลงตัว" (อนุมัติเองได้) หรือ "หมายเหตุห้ามอนุมัติเอง" —
/// ห้ามมีตัวเลขผิดที่อนุมัติเองได้เงียบ ๆ (<see cref="ทุกรูปแบบ_อนุมัติเองได้ต้องลงตัว"/>)
///
/// <para>"ลงตัว" (<see cref="Incoherence"/>): จำนวนตาม XML ทุกบรรทัด · อัตรา VAT ตาม XML (0 = ไม่มี VAT: 0%/ยกเว้น) · round(จำนวน × ราคา) − ส่วนลด
/// = ยอดของบรรทัด (ฐานเดียวกับราคา — เปิดแก้แล้วบันทึกไม่ขยับ) · Σ ยอด + ผลต่างปัดเศษ = ยอดก่อน VAT หลังส่วนลดหัวใบ · Σ VAT = VAT หัวใบ ·
/// รวม = ยอดรวมทั้งสิ้น</para>
///
/// <para>ผลที่คาดของทุกแถวคิดด้วยสคริปต์จำลองอิสระ (python · บันทึกรอบ 7) ที่ให้ผลตรงคลังกระดาษจริงทั้ง 4 ใบก่อน · แถวที่ต้องแก้โค้ด:
/// <c>mixed_tiny_zero</c> (เดิมบรรทัด 0% ยอดเล็กติด 7% + VAT เฉลี่ยเงียบ ๆ) · <c>mixed_ex</c>/<c>mixed_incl</c> (เดิมห้ามอนุมัติเองทั้งที่ XML บอกอัตรา)
/// ⇒ <see cref="OcrEtaxLineNormalizer.LineVatRate"/> · บรรทัดที่ตัดสินไม่ได้ (ค่าบริการ · ติดลบ · ไม่มีจำนวน) ⇒ ทบทวน e8547899 ข้อ 2</para>
/// </summary>
public class EtaxFormatMatrixTests
{
    // ── ตัวประกอบ XML (โครงตาม ขมธอ.3-2560 · เฉพาะ element ที่ตัวสกัดอ่าน + element รบกวนที่พบในไฟล์จริง) ──

    public sealed record XLine(string Name, string? Qty, string? Price, string? Net, string? NetIncl, string? Rate = "7",
        string? Allowance = null, string? Charge = null, string? UnitCode = null);

    public sealed record XInvoice(XLine[] Lines, string? LineTotal, string? TaxBasis, string? Vat, string? Grand,
        string TypeCode = "T02", string? ReferenceId = null);

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s)!;

    internal static string Xml(XInvoice inv)
    {
        var sb = new StringBuilder();
        sb.Append("""<rsm:TaxInvoice_CrossIndustryInvoice xmlns:ram="urn:etda:uncefact:data:standard:TaxInvoice_ReusableAggregateBusinessInformationEntity:2" xmlns:rsm="urn:etda:uncefact:data:standard:TaxInvoice_CrossIndustryInvoice:2">""");
        sb.Append("<rsm:ExchangedDocument><ram:ID>MX-0001</ram:ID><ram:Name>ใบกำกับภาษี</ram:Name>")
          .Append("<ram:TypeCode>").Append(inv.TypeCode).Append("</ram:TypeCode><ram:IssueDateTime>2026-10-01T00:00:00</ram:IssueDateTime></rsm:ExchangedDocument>");
        sb.Append("<rsm:SupplyChainTradeTransaction><ram:ApplicableHeaderTradeAgreement>")
          .Append("<ram:SellerTradeParty><ram:Name>บริษัท ผู้ขายทดสอบ จำกัด</ram:Name><ram:SpecifiedTaxRegistration><ram:ID schemeID=\"TXID\">010555502121500000</ram:ID></ram:SpecifiedTaxRegistration></ram:SellerTradeParty>")
          .Append("<ram:BuyerTradeParty><ram:Name>บริษัท ผู้ซื้อทดสอบ จำกัด</ram:Name><ram:SpecifiedTaxRegistration><ram:ID schemeID=\"TXID\">020356200587100000</ram:ID></ram:SpecifiedTaxRegistration></ram:BuyerTradeParty>");
        if (inv.ReferenceId is string refId)
            sb.Append("<ram:AdditionalReferencedDocument><ram:IssuerAssignedID>").Append(Esc(refId))
              .Append("</ram:IssuerAssignedID><ram:IssueDateTime>2026-09-01T00:00:00</ram:IssueDateTime><ram:ReferenceTypeCode>T02</ram:ReferenceTypeCode></ram:AdditionalReferencedDocument>");
        sb.Append("</ram:ApplicableHeaderTradeAgreement><ram:ApplicableHeaderTradeDelivery /><ram:ApplicableHeaderTradeSettlement>")
          .Append("<ram:InvoiceCurrencyCode>THB</ram:InvoiceCurrencyCode><ram:SpecifiedTradeSettlementHeaderMonetarySummation>");
        void Amt(string tag, string? v) { if (v != null) sb.Append("<ram:").Append(tag).Append('>').Append(v).Append("</ram:").Append(tag).Append('>'); }
        Amt("LineTotalAmount", inv.LineTotal);
        if (inv.LineTotal != null && inv.TaxBasis != null)
            Amt("AllowanceTotalAmount", (decimal.Parse(inv.LineTotal, CultureInfo.InvariantCulture) - decimal.Parse(inv.TaxBasis, CultureInfo.InvariantCulture)).ToString("0.00", CultureInfo.InvariantCulture));
        Amt("TaxBasisTotalAmount", inv.TaxBasis);
        Amt("TaxTotalAmount", inv.Vat);
        Amt("GrandTotalAmount", inv.Grand);
        sb.Append("</ram:SpecifiedTradeSettlementHeaderMonetarySummation></ram:ApplicableHeaderTradeSettlement>");
        for (var i = 0; i < inv.Lines.Length; i++)
        {
            var l = inv.Lines[i];
            sb.Append("<ram:IncludedSupplyChainTradeLineItem><ram:AssociatedDocumentLineDocument><ram:LineID>").Append(i + 1)
              .Append("</ram:LineID></ram:AssociatedDocumentLineDocument><ram:SpecifiedTradeProduct><ram:Name>").Append(Esc(l.Name))
              .Append("</ram:Name></ram:SpecifiedTradeProduct>");
            if (l.Price != null)
                sb.Append("<ram:SpecifiedLineTradeAgreement><ram:GrossPriceProductTradePrice><ram:ChargeAmount>").Append(l.Price)
                  .Append("</ram:ChargeAmount></ram:GrossPriceProductTradePrice></ram:SpecifiedLineTradeAgreement>");
            sb.Append("<ram:SpecifiedLineTradeDelivery>");
            if (l.Qty != null)
                sb.Append("<ram:BilledQuantity unitCode=\"").Append(l.UnitCode ?? "").Append("\">").Append(l.Qty).Append("</ram:BilledQuantity>");
            sb.Append("</ram:SpecifiedLineTradeDelivery><ram:SpecifiedLineTradeSettlement>");
            if (l.Rate != null)
                sb.Append("<ram:ApplicableTradeTax><ram:TypeCode>VAT</ram:TypeCode><ram:CalculatedRate>").Append(l.Rate)
                  .Append("</ram:CalculatedRate></ram:ApplicableTradeTax>");
            if (l.Allowance != null)
                sb.Append("<ram:SpecifiedTradeAllowanceCharge><ram:ChargeIndicator>false</ram:ChargeIndicator><ram:ActualAmount>").Append(l.Allowance)
                  .Append("</ram:ActualAmount></ram:SpecifiedTradeAllowanceCharge>");
            if (l.Charge != null)
                sb.Append("<ram:SpecifiedTradeAllowanceCharge><ram:ChargeIndicator>true</ram:ChargeIndicator><ram:ActualAmount>").Append(l.Charge)
                  .Append("</ram:ActualAmount></ram:SpecifiedTradeAllowanceCharge>");
            sb.Append("<ram:SpecifiedTradeSettlementLineMonetarySummation>");
            Amt("NetLineTotalAmount", l.Net);
            Amt("NetIncludingTaxesLineTotalAmount", l.NetIncl);
            sb.Append("</ram:SpecifiedTradeSettlementLineMonetarySummation></ram:SpecifiedLineTradeSettlement></ram:IncludedSupplyChainTradeLineItem>");
        }
        sb.Append("</rsm:SupplyChainTradeTransaction></rsm:TaxInvoice_CrossIndustryInvoice>");
        return sb.ToString();
    }

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static string S(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>50 บรรทัดราคาก่อน VAT · VAT คิดจากยอดรวมทั้งใบ</summary>
    private static XInvoice FiftyExclusive()
    {
        var lines = new XLine[50];
        var lt = 0m;
        for (var i = 0; i < 50; i++)
        {
            var q = i % 3 + 1;
            var g = 10m + i / 4m + 0.01m;
            var net = R2(q * g);
            lt += net;
            lines[i] = new XLine($"สินค้า {i + 1}", q.ToString(CultureInfo.InvariantCulture), S(g), S(net), S(net + R2(net * 7m / 100m)));
        }
        var vat = R2(lt * 7m / 100m);
        return new XInvoice(lines, S(lt), S(lt), S(vat), S(lt + vat));
    }

    /// <summary>50 บรรทัดราคารวม VAT · ผู้ขายคิด VAT จากยอดรวมทั้งใบ (แบบ CRC 2/3)</summary>
    private static XInvoice FiftyInclusiveHeaderVat()
    {
        var lines = new XLine[50];
        var gt = 0m;
        for (var i = 0; i < 50; i++)
        {
            var q = i % 4 + 1;
            var g = 20m + i;
            var after = R2(q * g);
            gt += after;
            lines[i] = new XLine($"สินค้า {i + 1}", q.ToString(CultureInfo.InvariantCulture), S(g), S(R2(after * 100m / 107m)), S(after));
        }
        var vat = R2(gt * 7m / 107m);
        return new XInvoice(lines, S(gt - vat), S(gt - vat), S(vat), S(gt));
    }

    public enum Outcome { Coherent, Blocked }

    /// <summary>รูปแบบ → (XML, ผลที่ต้องได้, แท็กห้ามอนุมัติที่คาด ถ้าห้าม)</summary>
    public static readonly Dictionary<string, (XInvoice Invoice, Outcome Expect, string? Tag)> Variants = new()
    {
        // ราคาก่อน VAT ไม่มีส่วนลด
        ["ex_plain"] = (new XInvoice(new[]
        {
            new XLine("สินค้า ก", "3", "100.00", "300.00", "321.00"), new XLine("สินค้า ข", "2", "49.50", "99.00", "105.93"),
        }, "399.00", "399.00", "27.93", "426.93"), Outcome.Coherent, null),
        // ราคาก่อน VAT + ส่วนลดบรรทัด
        ["ex_line_allowance"] = (new XInvoice(new[]
        {
            new XLine("สินค้า ก", "5", "100.00", "450.00", "481.50", Allowance: "50.00"), new XLine("สินค้า ข", "2", "25.00", "50.00", "53.50"),
        }, "500.00", "500.00", "35.00", "535.00"), Outcome.Coherent, null),
        // ราคารวม VAT + ส่วนลดบรรทัด (แบบ CRC)
        ["incl_line_allowance"] = (new XInvoice(new[]
        {
            new XLine("สินค้า ก", "2", "107.00", "190.00", "203.30", Allowance: "10.70"), new XLine("สินค้า ข", "1", "53.50", "50.00", "53.50"),
        }, "240.00", "240.00", "16.80", "256.80"), Outcome.Coherent, null),
        // ส่วนลดท้ายบิลอย่างเดียว (LineTotal − TaxBasis = 100) ⇒ กระจายลงบรรทัด
        ["header_allowance"] = (new XInvoice(new[]
        {
            new XLine("สินค้า ก", "2", "500.00", "1000.00", "1070.00"), new XLine("สินค้า ข", "1", "200.00", "200.00", "214.00"),
        }, "1200.00", "1100.00", "77.00", "1177.00"), Outcome.Coherent, null),
        // ส่วนลดบรรทัด + ส่วนลดท้ายบิล
        ["ex_line_and_header_allowance"] = (new XInvoice(new[]
        {
            new XLine("สินค้า ก", "5", "100.00", "450.00", "481.50", Allowance: "50.00"), new XLine("สินค้า ข", "2", "25.00", "50.00", "53.50"),
        }, "500.00", "480.00", "33.60", "513.60"), Outcome.Coherent, null),
        // ค่าบริการรายบรรทัด (ChargeIndicator=true) — เอกสารไม่มีช่องรองรับ ⇒ ห้ามอนุมัติเอง (เหตุ "ค่าบริการ")
        ["line_charge"] = (new XInvoice(new[]
        {
            new XLine("สินค้า ก", "1", "100.00", "110.00", "117.70", Charge: "10.00"),
        }, "110.00", "110.00", "7.70", "117.70"), Outcome.Blocked, OcrEtaxLineNormalizer.LineCheckTag),
        // ค่าบริการท้ายบิล (TaxBasis > LineTotal) ⇒ ยอดรวมไม่ตรง ⇒ [Σ-GAP]
        ["header_charge"] = (new XInvoice(new[]
        {
            new XLine("สินค้า ก", "1", "100.00", "100.00", "107.00"),
        }, "100.00", "120.00", "8.40", "128.40"), Outcome.Blocked, "[Σ-GAP]"),
        // อัตรา 0 ทั้งใบ (ส่งออก §80/1)
        ["zero_rated"] = (new XInvoice(new[]
        {
            new XLine("บริการส่งออก", "1", "1000.00", "1000.00", "1000.00", Rate: "0"),
        }, "1000.00", "1000.00", "0.00", "1000.00"), Outcome.Coherent, null),
        // ยกเว้น (สินค้าเกษตร §81) — XML อัตรา 0
        ["exempt"] = (new XInvoice(new[]
        {
            new XLine("ผักสด", "4", "25.00", "100.00", "100.00", Rate: "0"),
        }, "100.00", "100.00", "0.00", "100.00"), Outcome.Coherent, null),
        // ผสม 7%/0% ราคาก่อน VAT (เดิม: เดา 7% ทั้งสองบรรทัด ⇒ [Σ-GAP])
        ["mixed_ex"] = (new XInvoice(new[]
        {
            new XLine("สินค้า ก", "1", "1000.00", "1000.00", "1070.00"), new XLine("บริการส่งออก", "1", "500.00", "500.00", "500.00", Rate: "0"),
        }, "1500.00", "1500.00", "70.00", "1570.00"), Outcome.Coherent, null),
        // ผสม 7% ราคารวม VAT + 0% (ทางสำรองราคาก่อน VAT)
        ["mixed_incl"] = (new XInvoice(new[]
        {
            new XLine("สินค้า ก", "1", "107.00", "100.00", "107.00"), new XLine("บริการส่งออก", "1", "50.00", "50.00", "50.00", Rate: "0"),
        }, "150.00", "150.00", "7.00", "157.00"), Outcome.Coherent, null),
        // ผสม 7% + 0% ยอดเล็ก — เดิม "ผิดเงียบ": บรรทัด 0% ติด 7% + VAT เฉลี่ย (ต่างต่ำกว่าเกณฑ์ 0.10 ของด่านอัตรา)
        ["mixed_tiny_zero"] = (new XInvoice(new[]
        {
            new XLine("สินค้า ก", "1", "1000.00", "1000.00", "1070.00"), new XLine("ค่าธรรมเนียม", "1", "1.00", "1.00", "1.00", Rate: "0"),
        }, "1001.00", "1001.00", "70.00", "1071.00"), Outcome.Coherent, null),
        // บรรทัดยอดติดลบ (ใบลดหนี้ที่ใส่เครื่องหมายลบ) ⇒ ห้ามอนุมัติเอง
        ["negative_lines"] = (new XInvoice(new[]
        {
            new XLine("คืนสินค้า", "1", "100.00", "-100.00", "-107.00"),
        }, "-100.00", "-100.00", "-7.00", "-107.00", TypeCode: "81", ReferenceId: "INV-0099"), Outcome.Blocked, OcrEtaxLineNormalizer.LineCheckTag),
        // ไม่มี BilledQuantity ⇒ ห้ามอนุมัติเอง (เดิมไม่มีหมายเหตุเลย)
        ["missing_qty"] = (new XInvoice(new[]
        {
            new XLine("สินค้า ก", null, "100.00", "100.00", "107.00"),
        }, "100.00", "100.00", "7.00", "107.00"), Outcome.Blocked, OcrEtaxLineNormalizer.LineCheckTag),
        // 50 บรรทัด
        ["fifty_ex"] = (FiftyExclusive(), Outcome.Coherent, null),
        ["fifty_incl_header_vat"] = (FiftyInclusiveHeaderVat(), Outcome.Coherent, null),
        // ขอบการปัด: ผู้ขายคิด VAT รายบรรทัด (0.04 × 3 = 0.12 ≠ ปัดรวม 0.11) — เอกสารใช้ VAT ตามหัวใบ (ฟอร์มคง VAT ที่บันทึกไว้ตอนเปิดแก้)
        ["rounding_per_line_vat_ex"] = (new XInvoice(new[]
        {
            new XLine("a", "1", "0.50", "0.50", "0.54"), new XLine("b", "1", "0.50", "0.50", "0.54"), new XLine("c", "1", "0.50", "0.50", "0.54"),
        }, "1.50", "1.50", "0.12", "1.62"), Outcome.Coherent, null),
        // ขอบการปัด: ราคารวม VAT 3 × 0.35 = 1.05 ⇒ ก่อน VAT 0.98 (0.981…)
        ["rounding_incl_boundary"] = (new XInvoice(new[]
        {
            new XLine("a", "3", "0.35", "0.98", "1.05"), new XLine("b", "1", "10.70", "10.00", "10.70"),
        }, "10.98", "10.98", "0.77", "11.75"), Outcome.Coherent, null),
        // จำนวนทศนิยม 3 ตำแหน่ง + หน่วยกิโลกรัม
        ["fractional_qty"] = (new XInvoice(new[]
        {
            new XLine("เหล็ก", "12.345", "25.00", "308.63", "330.23", UnitCode: "KGM"),
        }, "308.63", "308.63", "21.60", "330.23"), Outcome.Coherent, null),
        // ราคารวม VAT ทุกบรรทัด + ส่วนลดท้ายบิล (ก่อน VAT) — ยังไม่รองรับ ⇒ ห้ามอนุมัติเอง (ไม่ใช่ตัวเลขเงียบ)
        ["incl_plus_header_allowance"] = (new XInvoice(new[]
        {
            new XLine("สินค้า ก", "1", "107.00", "100.00", "107.00"), new XLine("สินค้า ข", "1", "214.00", "200.00", "214.00"),
        }, "300.00", "270.00", "18.90", "288.90"), Outcome.Blocked, OcrEtaxLineNormalizer.HeaderGapTag),
    };

    public static IEnumerable<object[]> Names => Variants.Keys.Select(k => new object[] { k });

    /// <summary>เหตุที่เอกสารจำลอง "ไม่ลงตัว" กับ XML (ว่าง = ลงตัว)</summary>
    internal static List<string> Incoherence(ScanDocumentSimulator.SimDoc doc, EtaxPdfXmlExtractor.ExtractResult r)
    {
        var errs = new List<string>();
        if (doc.Lines.Count != r.Items.Count) return new List<string> { $"บรรทัด {doc.Lines.Count} ≠ XML {r.Items.Count}" };
        for (var i = 0; i < doc.Lines.Count; i++)
        {
            var l = doc.Lines[i];
            var x = r.Items[i];
            if (x.Quantity is decimal xq && l.Qty != xq) errs.Add($"บรรทัด {i + 1}: จำนวน {l.Qty} ≠ XML {xq}");
            if (x.VatRatePercent is decimal xr && l.VatRate != xr && !(xr == 0m && l.VatRate <= 0m))
                errs.Add($"บรรทัด {i + 1}: อัตรา {l.VatRate} ≠ XML {xr}");
            var gross = R2(l.Qty * l.UnitPrice);
            var after = gross - (l.Discount > 0m ? Math.Min(R2(l.Discount), gross) : 0m);
            var want = doc.PricesIncludeVat ? l.Amount + l.Vat : l.Amount;
            if (after != want) errs.Add($"บรรทัด {i + 1}: จำนวน × ราคา − ส่วนลด = {after} ≠ ยอด {want}");
        }
        var sub = doc.Lines.Sum(l => l.Amount) + doc.RoundingAdjustment;
        var vat = doc.Lines.Sum(l => l.Vat);
        if (sub != doc.HeaderSubTotal) errs.Add($"ยอดก่อน VAT {sub} ≠ หัวใบ {doc.HeaderSubTotal}");
        if (vat != doc.HeaderVat) errs.Add($"VAT {vat} ≠ หัวใบ {doc.HeaderVat}");
        if (sub + vat != doc.HeaderTotal) errs.Add($"รวม {sub + vat} ≠ หัวใบ {doc.HeaderTotal}");
        return errs;
    }

    private static (ScanDocumentSimulator.SimDoc Doc, EtaxPdfXmlExtractor.ExtractResult Xml) Run(XInvoice inv)
    {
        var r = EtaxPdfXmlExtractor.ParseEtaxXml(Xml(inv));
        Assert.NotNull(r);
        Assert.Equal(inv.Lines.Length, r!.Items.Count);
        return (ScanDocumentSimulator.Build(r), r);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ทุกรูปแบบ_อนุมัติเองได้ต้องลงตัว(string name)
    {
        // กติกาความปลอดภัย (ไม่ขึ้นกับว่าคาดอะไร): อนุมัติเองได้ ⇒ ลงตัวทุกข้อ · ไม่ลงตัว ⇒ ต้องมีเหตุห้ามอนุมัติเอง
        var (doc, r) = Run(Variants[name].Invoice);
        var errs = Incoherence(doc, r);
        if (doc.CanAutoApprove)
            Assert.True(errs.Count == 0, $"{name}: อนุมัติเองได้แต่ไม่ลงตัว — {string.Join(" · ", errs)}");
        else
            Assert.False(string.IsNullOrWhiteSpace(doc.BlockReason), $"{name}: ห้ามอนุมัติเองแต่ไม่บอกเหตุ");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ทุกรูปแบบ_ได้ผลตามที่คาด(string name)
    {
        var (inv, expect, tag) = Variants[name];
        var (doc, r) = Run(inv);
        if (expect == Outcome.Coherent)
        {
            Assert.True(doc.CanAutoApprove, $"{name}: คาดว่าอนุมัติเองได้ แต่ถูกห้าม — {doc.BlockReason} · {string.Join(" | ", doc.Notes)}");
            Assert.Empty(Incoherence(doc, r));
        }
        else
        {
            Assert.False(doc.CanAutoApprove, $"{name}: คาดว่าห้ามอนุมัติเอง");
            Assert.Contains(doc.Notes, n => n.StartsWith(tag!, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ผสม7และ0_บรรทัด0ได้อัตราตามXML_ไม่มีVATเฉลี่ยเข้า()
    {
        // mixed_tiny_zero: เดิมบรรทัด "ค่าธรรมเนียม 1.00" (XML 0%) ถูกเดาเป็น 7% แล้วได้ VAT เฉลี่ยจากหัวใบ 0.07 — Σ ยังตรงจึงเงียบ
        var (doc, _) = Run(Variants["mixed_tiny_zero"].Invoice);
        Assert.Equal((7m, 70.00m), (doc.Lines[0].VatRate, doc.Lines[0].Vat));
        Assert.Equal((0m, 0m), (doc.Lines[1].VatRate, doc.Lines[1].Vat));
        // ยกเว้น (ชื่อเข้าหมวด §81) ⇒ "ยกเว้น" ไม่ใช่ 0% — ตัวเลขเท่ากัน ต่างแค่คอลัมน์รายงาน
        Assert.Equal(ThaiVatTypeRule.ExemptRate, OcrEtaxLineNormalizer.LineVatRate(default, 0m, "ผักสด"));
        Assert.Equal(0m, OcrEtaxLineNormalizer.LineVatRate(default, 0m, "บริการส่งออก"));
        Assert.Equal(7m, OcrEtaxLineNormalizer.LineVatRate(default, 7m, "สินค้า ก"));
        // ไม่มีอัตราใน XML / ติดลบ ⇒ ไม่เดา (ชั้นถัดไปตัดสิน)
        Assert.Null(OcrEtaxLineNormalizer.LineVatRate(default, null, "สินค้า ก"));
        Assert.Null(OcrEtaxLineNormalizer.LineVatRate(default, -1m, "สินค้า ก"));
        // บรรทัดที่พิสูจน์ราคารวม VAT ⇒ อัตราที่ใช้ถอด (อนุมานจากยอดสองตัวเมื่อ XML ไม่มีอัตรา)
        var inferred = OcrEtaxLineNormalizer.Normalize(new OcrEtaxLineFacts(1m, 107.00m, null, null, null, 100.00m, 107.00m));
        Assert.Equal(7m, OcrEtaxLineNormalizer.LineVatRate(inferred, null, "สินค้า ก"));
    }

    [Theory]
    [InlineData("T01", "Receipt")]
    [InlineData("T02", "TaxInvoice")]
    [InlineData("T03", "TaxInvoice")]
    [InlineData("T04", "TaxInvoice")]
    [InlineData("388", "TaxInvoice")]
    [InlineData("380", "Invoice")]
    [InlineData("80", "DebitNote")]
    [InlineData("81", "CreditNote")]
    public void รหัสชนิดเอกสาร_และเอกสารอ้างอิง_ไม่กระทบบรรทัด(string typeCode, string mapped)
    {
        // บรรทัดชุดเดียวกับ ex_plain + รหัสชนิดเอกสารต่าง ๆ + เอกสารอ้างอิง (ใบลด/เพิ่มหนี้อ้างใบเดิม) ⇒ เอกสารเดิมทุกตัวเลข
        var inv = Variants["ex_plain"].Invoice with { TypeCode = typeCode, ReferenceId = "INV-0001" };
        var (doc, r) = Run(inv);
        Assert.Equal(mapped, r.MappedDocumentType);
        Assert.Equal("MX-0001", r.DocumentNumber);   // เลขอ้างอิงไม่แย่งเลขที่เอกสาร
        Assert.True(doc.CanAutoApprove, doc.BlockReason);
        Assert.Empty(Incoherence(doc, r));
        var plain = Run(Variants["ex_plain"].Invoice).Doc;
        Assert.Equal(plain.Lines, doc.Lines);
    }

    [Theory]
    [InlineData("C62")]
    [InlineData("KGM")]
    [InlineData("EA")]
    [InlineData("")]
    public void หน่วยนับ_อ่านตามXML_ไม่กระทบตัวเลข(string unitCode)
    {
        var inv = Variants["ex_plain"].Invoice with
        {
            Lines = Variants["ex_plain"].Invoice.Lines.Select(l => l with { UnitCode = unitCode }).ToArray(),
        };
        var (doc, r) = Run(inv);
        Assert.All(r.Items, li => Assert.Equal(unitCode, li.Unit));
        Assert.True(doc.CanAutoApprove, doc.BlockReason);
        Assert.Empty(Incoherence(doc, r));
    }

    [Fact]
    public void ห้าสิบบรรทัดราคารวมVAT_ขั้นปัดขยับไม่เกินหนึ่งบรรทัด_และไม่มีจำนวนเศษ()
    {
        var (doc, r) = Run(FiftyInclusiveHeaderVat());
        Assert.True(doc.PricesIncludeVat);
        Assert.Equal(r.Items.Select(x => x.Quantity!.Value), doc.Lines.Select(l => l.Qty));
        Assert.True(doc.Notes.Count(n => n.StartsWith(OcrEtaxLineNormalizer.NoteTag, StringComparison.Ordinal)) <= 1);
    }
}
