using Accounting.Helpers;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// <b>คลังกระดาษจริง (golden corpus) ของ e-Tax XML</b> — ทุกใบจริงใน <see cref="EtaxFixtures"/> ต้องได้ "เอกสารที่คาดไว้" ทุกตัวเลข
/// (รายบรรทัด: จำนวน · ราคา · ส่วนลด · ยอด · VAT · หัวเอกสาร: ยอดก่อน VAT · VAT · รวม · ราคารวม VAT · ผลต่างปัดเศษ · ไม่มีแท็กห้ามอนุมัติ)
///
/// <para>ที่มา (รอบ 7 · ผู้ใช้ 2026-10-09: "ครอบคลุมทุกรูปแบบเอกสาร และห้ามถดถอยของที่แก้แล้ว"): ใบ CRC สามใบจากผู้ขายเดียวกันพังคนละแบบ
/// (ใบแรกลงตัวรายบรรทัด · ใบสอง/สามผู้ขายคิด VAT ระดับหัวใบ) — การแก้ใบหนึ่งเคยทำให้อีกใบเสีย ⇒ ทุกใบจริงต้องถูกตรวจพร้อมกันทุกครั้ง
/// (CLAUDE.md §H "ใบที่ถูกอยู่แล้วไม่ถูกแตะ")</para>
///
/// <para><b>เพิ่มใบจริงใบใหม่</b> = เพิ่ม fixture ใน <see cref="EtaxFixtures"/> (XML จริงตัดลายเซ็น) + แถวคาดหวังหนึ่งแถวใน <see cref="Expected"/>
/// (ตัวเลขคาดหวังคิดจากกระดาษด้วยมือ/สคริปต์อิสระ ไม่ใช่คัดลอกผลของโค้ด) — เทสต์ <see cref="ทุกfixtureมีแถวคาดหวัง"/> ฟ้องถ้าลืม</para>
///
/// <para>ข้อจำกัดที่รู้: <see cref="ScanDocumentSimulator"/> เดินลำดับของ <c>OcrService.MapEtaxToOcrData</c> → <c>SanitizeVatSplitArtifacts</c> →
/// <c>BuildScanLinesAsync</c> ด้วย helper ตัวจริงทุกตัว แต่ตัวเมธอดใน service (DB) ไม่ได้ถูกเรียก — ลำดับใน service ล็อกด้วย
/// <c>tools/required_call_site_check.py</c> · จำลอง: อัตรา VAT รายบรรทัด (อัตราจาก XML → พิสูจน์ทั้งใบ 7% → เดาจากชื่อ) + ด่านยอดตรงกระดาษ
/// (OcrAmountIntegrity) · ส่วนที่ไม่จำลอง: สัญลักษณ์ VAT ท้ายบรรทัด/ยอดแยกภาษีจากข้อความดิบ (e-Tax ไม่มี) · ผังบัญชี · WHT</para>
/// </summary>
public class EtaxGoldenCorpusTests
{
    public sealed record GoldenLine(decimal Qty, decimal UnitPrice, decimal Discount, decimal Amount, decimal Vat);

    public sealed record GoldenInvoice(string Xml, bool PricesIncludeVat, decimal SubTotal, decimal Vat, decimal Total,
        decimal RoundingAdjustment, GoldenLine[] Lines);

    /// <summary>ใบจริงทุกใบ → เอกสารที่ต้องได้ (ตัวเลขจากกระดาษ · สคริปต์อิสระ golden_gen ในบันทึกรอบ 7)</summary>
    public static readonly Dictionary<string, GoldenInvoice> Expected = new()
    {
        // Shopee (ราคาก่อน VAT): 2 × 250.47 = 500.94 ⇒ บรรทัดใช้ จำนวน × ราคา (§86/4) ผลต่างปัดเศษ −0.01 ⇒ ยอดก่อน VAT 500.93 ตามกระดาษ
        ["ShopeeUptoyou"] = new(EtaxFixtures.ShopeeUptoyouXml, PricesIncludeVat: false, SubTotal: 500.93m, Vat: 35.07m, Total: 536.00m, RoundingAdjustment: -0.01m, Lines: new GoldenLine[]
        {
            new(2m, 250.47m, 0m, 500.94m, 35.07m),
        }),
        ["CrcThaiwatsadu"] = new(EtaxFixtures.CrcThaiwatsaduXml, PricesIncludeVat: true, SubTotal: 2991.59m, Vat: 209.41m, Total: 3201.00m, RoundingAdjustment: 0m, Lines: new GoldenLine[]
        {
            new(3.00m, 37.00m, 26.68m, 78.80m, 5.52m),
            new(4.00m, 229.00m, 220.14m, 650.34m, 45.52m),
            new(12.00m, 123.00m, 354.72m, 1047.93m, 73.35m),
            new(10.00m, 28.00m, 67.29m, 198.79m, 13.92m),
            new(6.00m, 43.00m, 62.00m, 183.18m, 12.82m),
            new(6.00m, 43.00m, 62.00m, 183.18m, 12.82m),
            new(4.00m, 43.00m, 41.34m, 122.11m, 8.55m),
            new(1.00m, 690.00m, 165.83m, 489.88m, 34.29m),
            new(120.00m, 1.00m, 80.00m, 37.38m, 2.62m),
        }),
        ["CrcThaiwatsadu2"] = new(EtaxFixtures.CrcThaiwatsadu2Xml, PricesIncludeVat: true, SubTotal: 2953.27m, Vat: 206.73m, Total: 3160.00m, RoundingAdjustment: 0m, Lines: new GoldenLine[]
        {
            new(1.00m, 1730.00m, 422.99m, 1221.51m, 85.50m),
            new(6.00m, 30.00m, 44.01m, 127.09m, 8.90m),
            new(4.00m, 118.00m, 115.40m, 333.27m, 23.33m),
            new(1.00m, 1130.00m, 276.28m, 797.87m, 55.85m),
            new(5.00m, 38.00m, 46.45m, 134.16m, 9.39m),
            new(1.00m, 388.00m, 94.87m, 273.95m, 19.18m),
            new(150.00m, 1.00m, 80.00m, 65.42m, 4.58m),
        }),
        ["CrcThaiwatsadu3"] = new(EtaxFixtures.CrcThaiwatsadu3Xml, PricesIncludeVat: true, SubTotal: 2862.62m, Vat: 200.38m, Total: 3063.00m, RoundingAdjustment: 0m, Lines: new GoldenLine[]
        {
            new(6.00m, 33.00m, 49.46m, 138.82m, 9.72m),
            new(6.00m, 25.00m, 37.47m, 105.17m, 7.36m),
            new(1.00m, 890.00m, 222.34m, 624.00m, 43.66m),
            new(11.00m, 77.00m, 211.59m, 593.84m, 41.57m),
            new(5.00m, 35.00m, 43.72m, 122.69m, 8.59m),
            new(6.00m, 20.00m, 29.98m, 84.13m, 5.89m),
            new(6.00m, 35.00m, 52.46m, 147.23m, 10.31m),
            new(6.00m, 25.00m, 37.47m, 105.17m, 7.36m),
            new(6.00m, 30.00m, 44.97m, 126.20m, 8.83m),
            new(1.00m, 168.00m, 41.97m, 117.79m, 8.24m),
            new(1.00m, 208.00m, 51.96m, 145.83m, 10.21m),
            new(12.00m, 23.00m, 86.95m, 176.68m, 12.37m),
            new(1.00m, 165.00m, 41.22m, 115.68m, 8.10m),
            new(1.00m, 100.00m, 24.98m, 70.11m, 4.91m),
            new(1.00m, 190.00m, 47.46m, 133.21m, 9.33m),
            new(140.00m, 1.00m, 80.00m, 56.07m, 3.93m),
        }),
    };

    public static IEnumerable<object[]> Names => Expected.Keys.Select(k => new object[] { k });

    [Fact]
    public void ทุกfixtureมีแถวคาดหวัง()
    {
        // ใบจริงทุกใบที่เป็น XML ใน EtaxFixtures ต้องอยู่ในคลัง — เพิ่ม fixture แล้วลืมแถวคาดหวัง = แดง
        var fixtures = typeof(EtaxFixtures).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Xml", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.NotEmpty(fixtures);
        foreach (var xml in fixtures)
            Assert.Contains(Expected.Values, e => ReferenceEquals(e.Xml, xml) || e.Xml == xml);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ใบจริง_ได้เอกสารตามที่คาดทุกตัวเลข(string name)
    {
        var want = Expected[name];
        var r = EtaxPdfXmlExtractor.ParseEtaxXml(want.Xml);
        Assert.NotNull(r);
        var doc = ScanDocumentSimulator.Build(r!);

        Assert.Equal(want.PricesIncludeVat, doc.PricesIncludeVat);
        Assert.Equal(want.Lines.Length, doc.Lines.Count);
        for (var i = 0; i < want.Lines.Length; i++)
        {
            var w = want.Lines[i];
            var g = doc.Lines[i];
            Assert.True(w == new GoldenLine(g.Qty, g.UnitPrice, g.Discount, g.Amount, g.Vat),
                $"{name} บรรทัดที่ {i + 1}: คาด {w} ได้ {g}");
        }
        Assert.Equal(want.RoundingAdjustment, doc.RoundingAdjustment);
        Assert.Equal(want.SubTotal, doc.Lines.Sum(l => l.Amount) + doc.RoundingAdjustment);
        Assert.Equal(want.Vat, doc.Lines.Sum(l => l.Vat));
        Assert.Equal(want.Total, want.SubTotal + want.Vat);
        Assert.Equal((want.SubTotal, want.Vat, want.Total), (doc.HeaderSubTotal, doc.HeaderVat, doc.HeaderTotal));
        Assert.True(doc.CanAutoApprove, $"{name}: {doc.BlockReason}");
        // บันทึกซ้ำ (ComputeLineAmounts + ReconcileTaxRounding) ได้บรรทัดเดิมทุกสตางค์
        Assert.Equal(doc.Lines.Select(l => (l.Amount, l.Vat)).ToList(), ScanDocumentSimulator.Resave(doc));
    }
}

/// <summary>เดินลำดับเดียวกับ OcrService (MapEtaxToOcrData → SanitizeVatSplitArtifacts → BuildScanLinesAsync) ด้วย helper ตัวจริง — ดูข้อจำกัดใน
/// <see cref="EtaxGoldenCorpusTests"/> · ใช้ร่วมกับเมทริกซ์รูปแบบ XML (<see cref="EtaxFormatMatrixTests"/>)</summary>
internal static class ScanDocumentSimulator
{
    internal sealed record SimLine(decimal Qty, decimal UnitPrice, decimal Discount, decimal Amount, decimal Vat, decimal VatRate);

    internal sealed record SimDoc(bool PricesIncludeVat, List<SimLine> Lines, decimal RoundingAdjustment,
        decimal HeaderSubTotal, decimal HeaderVat, decimal HeaderTotal, bool CanAutoApprove, string? BlockReason,
        IReadOnlyList<string> Notes, decimal UnreconciledGap);

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    internal static SimDoc Build(EtaxPdfXmlExtractor.ExtractResult r)
    {
        // ── MapEtaxToOcrData ──
        var inv = OcrEtaxLineNormalizer.NormalizeInvoice(
            r.Items.Select(li => new OcrEtaxLineFacts(li.Quantity, li.UnitPrice, li.LineAllowance, li.LineCharge,
                li.VatRatePercent, li.Amount, li.NetIncludingVatAmount)).ToList(),
            r.LineTotal, r.VatAmount, r.GrandTotal);
        var data = new OcrExtractedData();
        for (var i = 0; i < r.Items.Count; i++)
        {
            var n = inv.Lines[i];
            data.Items.Add(new OcrExtractedLineItem
            {
                Description = r.Items[i].Description, Quantity = n.Quantity, UnitPrice = n.UnitPrice, Amount = n.Amount,
                LineDiscountAmount = n.LineDiscount, QuantityFromEtaxXml = n.QuantityFromDocument, PriceIncludesVat = n.PriceIncludesVat,
                VatStripResidual = n.VatStripResidual,
                VatRate = OcrEtaxLineNormalizer.LineVatRate(n, r.Items[i].VatRatePercent, r.Items[i].DeclaresVatExemption),
            });
        }
        var hdrSub = r.LineTotal ?? r.TaxBasis ?? 0m;
        var hdrVat = r.VatAmount ?? 0m;
        var hdrTotal = r.GrandTotal ?? 0m;
        var hdrDisc = r.LineTotal is decimal lt && r.TaxBasis is decimal tb && lt - tb > 0m ? lt - tb : 0m;

        // ── ScanAsync: ตัวกันจำนวนระเบิด (สแกน e-Tax) ──
        OcrService.SanitizeVatSplitArtifacts(data, quantitiesFromSignedXml: true);
        var items = data.Items;

        // ── BuildScanLinesAsync ──
        var own = items.Select(x => OcrEtaxLineNormalizer.ProvenLineDiscount(x.Quantity, x.UnitPrice, x.Amount, x.LineDiscountAmount)).ToList();
        var lineGross = items.Select((x, gi) => own[gi] > 0m ? x.Amount!.Value : OcrTotalDecomposer.LineGross(x.Quantity, x.UnitPrice, x.Amount)).ToList();
        var netSub = hdrSub;
        if (hdrDisc > 0m && hdrTotal > 0m) netSub = OcrHeaderAmounts.NetSubTotal(hdrSub, hdrVat, hdrTotal, hdrDisc);
        else if (hdrSub <= 0m && hdrTotal > 0m) netSub = Math.Max(0m, hdrTotal - hdrVat);
        var recon = OcrLineReconciler.Classify(lineGross.Sum(), netSub, hdrVat, hdrTotal, hdrDisc);
        var incl = recon.PricesIncludeVat
            || OcrEtaxLineNormalizer.AllLinesPriceIncludeVat(items.Select(x => (x.Amount ?? 0m, x.PriceIncludesVat)).ToList());
        var docPct = recon.DiscountPercent;
        if (docPct > 0m && recon.TargetLineSum is decimal target && items.Count > 0)
        {
            var grossTotal = lineGross.Sum();
            decimal assigned = 0m;
            for (var di = 0; di < items.Count; di++)
            {
                var share = di == items.Count - 1 ? target - assigned : R2(target * lineGross[di] / grossTotal);
                assigned += share;
                items[di].Amount = share;
            }
        }
        // อัตรารายบรรทัด: พิสูจน์ทั้งใบ 7% จากหัวใบ (OcrLineVatPlanner · VAT ของ e-Tax = Labelled) → เดาจากชื่อ (ThaiVatTypeRule) เฉพาะบรรทัดที่ยังว่าง
        var vatPlan = OcrLineVatPlanner.PlanWholeInvoice(items.Select(x => x.Amount ?? 0m).ToList(), items.Select(x => x.VatRate).ToList(),
            hdrVat, netSub, hdrTotal, incl, paperExemptAmount: null, vatPrintedOnPaper: true);
        if (vatPlan.Decided)
            for (var vi = 0; vi < items.Count; vi++)
                if (!items[vi].VatRate.HasValue && vatPlan.Rates[vi] is decimal planRate) items[vi].VatRate = planRate;
        foreach (var it in items)
            it.VatRate ??= hdrVat > 0m
                ? ThaiVatTypeRule.ToVatRate(ThaiVatTypeRule.Suggest(it.Description, null, r.SellerTaxId), 7m)
                : 0m;
        var spread = (incl
                ? OcrEtaxLineNormalizer.InclusiveLineVats(items.Select(x => (x.Amount ?? 0m, x.VatRate ?? 0m, x.PriceIncludesVat)).ToList(), hdrVat)
                : null)
            ?? ThaiVatTypeRule.SpreadHeaderVat(items.Select(x => (x.Amount ?? 0m, x.VatRate ?? 0m)).ToList(), hdrVat);
        var (shifts, _) = DocumentRounding.CapShifts(items
            .Select((it, idx) => it.VatRate is decimal rr && rr <= 0m ? 0m
                : DocumentRounding.FromPrintedLine(it.Quantity, it.UnitPrice, lineGross[idx] + own[idx]).Shift)
            .ToList());
        var lines = new List<SimLine>();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var amount = it.Amount ?? 0m;
            var disc = (docPct > 0m ? R2((it.UnitPrice.HasValue ? lineGross[i] : amount * (it.Quantity ?? 1m)) - amount) : 0m) + own[i];
            lines.Add(new SimLine(it.Quantity ?? 1m, it.UnitPrice ?? it.Amount ?? 0m, disc,
                (incl ? R2(amount - spread[i]) : amount) + shifts[i], spread[i], it.VatRate ?? 0m));
        }
        var notes = inv.Notes.ToList();
        if (recon.UnreconciledGap != 0m) notes.Add("[Σ-GAP] " + recon.Note);
        // ด่าน "เอกสารที่จะสร้าง ยอดตรงกับกระดาษไหม" (AppendAmountIntegrityGaps — ไม่มีข้อความดิบ ⇒ ไม่มียอดแยกภาษีบนกระดาษ)
        var planned = items.Select((it, pi) => new OcrPlannedLine(
            incl ? R2((it.Amount ?? 0m) - spread[pi]) : it.Amount ?? 0m, it.VatRate ?? 0m, spread[pi])).ToList();
        foreach (var p in OcrAmountIntegrity.Check(planned, hdrVat, hdrTotal).Problems)
        {
            if (recon.UnreconciledGap != 0m && p.Kind is OcrAmountIntegrityKind.TotalMismatch or OcrAmountIntegrityKind.VatRateMismatch) continue;
            notes.Add("[Σ-GAP] " + p.Message);
        }
        var verdict = OcrPostingReadiness.Evaluate(string.Join("\n", notes), true);
        return new SimDoc(incl, lines, -shifts.Sum(), hdrSub - hdrDisc, hdrVat, hdrTotal, verdict.CanAutoApprove, verdict.Reason,
            notes, recon.UnreconciledGap);
    }

    /// <summary>ผลของ DocumentService ตอนเปิดแก้แล้วบันทึก: ComputeLineAmounts (ส่วนลดเป็นจำนวนเงิน) + ขั้น VAT ของ ReconcileTaxRounding
    /// (ผ่าน <see cref="OcrEtaxLineNormalizer.ReplayTaxRounding"/> — สำเนาที่ล็อกกับต้นทางด้วย rcs)</summary>
    internal static List<(decimal Amount, decimal Vat)> Resave(SimDoc doc)
    {
        var nets = new decimal[doc.Lines.Count];
        var vats = new decimal[doc.Lines.Count];
        var rates = new decimal[doc.Lines.Count];
        for (var i = 0; i < doc.Lines.Count; i++)
        {
            var l = doc.Lines[i];
            var gross = R2(l.Qty * l.UnitPrice);
            var after = gross - (l.Discount > 0m ? Math.Min(R2(l.Discount), gross) : 0m);
            if (doc.PricesIncludeVat)
                (nets[i], vats[i]) = DocumentLineVatConvention.SplitLine(after, l.VatRate, null, includeVat: true);
            else
                (nets[i], vats[i]) = (after, l.VatRate > 0m ? R2(after * l.VatRate / 100m) : 0m);
            rates[i] = l.VatRate;
        }
        OcrEtaxLineNormalizer.ReplayTaxRounding(nets, vats, rates, doc.PricesIncludeVat);
        return nets.Select((n, i) => (n, vats[i])).ToList();
    }
}
