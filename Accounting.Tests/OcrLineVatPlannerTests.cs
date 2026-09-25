using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 195 — <see cref="OcrLineVatPlanner"/>: "ตัวเลขหัวใบพิสูจน์อัตรา VAT ทั้งใบ" อยู่เหนือตัวเดาจากชื่อสินค้า
///
/// <para>กระดาษจริงของเจ้าของ: ใบ Scommerce TXE05202609T004679 (<see cref="OcrPaperSamples.ScommerceLazada"/>) —
/// "นมผงเอนฟาโกร…" ถูก <see cref="ThaiVatTypeRule"/> ตีเป็นยกเว้น §81 (คำ "นม") ⇒ VAT บรรทัด 0 · ยอด 4,695.33 ≠ 5,024.00 ·
/// คำเตือน 3 ข้อ · ภาษีซื้อ 328.67 หาย (ถดถอยจาก <c>5e3a323b</c>)</para>
///
/// <para><see cref="Chain"/> เดินลำดับขั้นตัวเลขของ <c>OcrService.BuildScanLinesAsync</c> ด้วย helper ตัวจริงทุกตัว
/// (สัญลักษณ์บนกระดาษ → กระทบยอด/กระจายส่วนลด → <b>ชั้นพิสูจน์ทั้งใบ</b> → เดาจากชื่อ → เฉลี่ย VAT → ผลต่างปัดเศษ → ด่านยอด)
/// — ลำดับจริงในเซอร์วิสถูกล็อกด้วย <c>tools/required_call_site_check.py</c> (planner ต้องมาก่อน <c>ThaiVatTypeRule.Suggest</c>)</para>
///
/// <para><b>ครึ่งที่ 1</b> ใบ Scommerce ⇒ [7,7] · VAT 328.67 · บรรทัด 4,695.34 · ผลต่างปัด −0.01 · รวม 5,024.00 · ไม่มี [Σ-GAP] ·
/// <b>ครึ่งที่ 2</b> (ต้องไม่ถูกแตะ) ใบค้าส่งผสม (7% × 764 = 53.48 ≠ 28) · Makro 951/49/1,000 · Makro หน้า 3/3 (ยกเว้น 6,260) ·
/// ใบส่งออก VAT 0 · ใบผสม "นมสด + น้ำปลา" (ตัวเดาเดิมยังตัดสิน) · ใบที่กระดาษพิมพ์ยอดยกเว้น &gt; 0 · ชั้นบนตั้งอัตราไว้แล้ว</para>
/// </summary>
public class OcrLineVatPlannerTests
{
    private sealed record ScanItem(string Description, decimal? Quantity, decimal? UnitPrice, decimal? Amount, decimal? VatRate = null);

    private sealed record ChainResult(
        decimal[] Rates, decimal[] LineVat, decimal[] LineAmount, decimal RoundingAdjustment,
        decimal DocTotal, OcrLineVatPlan Plan, IReadOnlyList<OcrAmountIntegrityProblem> Gaps);

    /// <summary>ลำดับขั้นตัวเลขของ BuildScanLinesAsync (ราคาก่อน VAT/รวม VAT · ส่วนลดเคส C/E) ด้วย helper จริง</summary>
    private static ChainResult Chain(string rawText, IReadOnlyList<ScanItem> items,
        decimal headerNetBase, decimal headerVat, decimal headerTotal, decimal headerDiscount, string? vendorTaxId = "0105560113122")
    {
        var n = items.Count;
        var rates = items.Select(i => i.VatRate).ToArray();
        var split = OcrLineVatMarks.Read(rawText);
        var marks = OcrLineVatMarks.Assign(
            items.Select(x => x.Amount ?? ((x.UnitPrice ?? 0m) * (x.Quantity ?? 1m))).ToList(), split, headerVat);
        if (marks.Applied)
            for (var i = 0; i < n; i++) rates[i] ??= marks.Rates[i];

        var gross = items.Select(x => OcrTotalDecomposer.LineGross(x.Quantity, x.UnitPrice, x.Amount)).ToArray();
        var amounts = items.Select(x => x.Amount ?? 0m).ToArray();
        var recon = OcrLineReconciler.Classify(gross.Sum(), headerNetBase, headerVat, headerTotal, headerDiscount);
        if (recon.DiscountPercent > 0m && recon.TargetLineSum is decimal target)
        {
            decimal assigned = 0m;
            for (var i = 0; i < n; i++)
            {
                amounts[i] = i == n - 1 ? target - assigned
                    : Math.Round(target * gross[i] / gross.Sum(), 2, MidpointRounding.AwayFromZero);
                assigned += amounts[i];
            }
        }

        var plan = OcrLineVatPlanner.PlanWholeInvoice(amounts, rates, headerVat, headerNetBase, headerTotal,
            recon.PricesIncludeVat, OcrLineVatPlanner.PaperExemptAmount(split, OcrLineVatMarks.ReadGroups(rawText)),
            vatPrintedOnPaper: OcrHeaderVatEvidence.Classify(rawText, null, headerVat, null) == OcrHeaderVatSource.Labelled);
        if (plan.Decided)
            for (var i = 0; i < n; i++) rates[i] ??= plan.Rates[i];
        var std = headerVat > 0m ? 7m : 0m;
        var final = new decimal[n];
        for (var i = 0; i < n; i++)
            final[i] = rates[i] ?? (headerVat > 0m
                ? ThaiVatTypeRule.ToVatRate(ThaiVatTypeRule.Suggest(items[i].Description, null, vendorTaxId), std)
                : 0m);

        var vat = ThaiVatTypeRule.SpreadHeaderVat(amounts.Select((a, i) => (a, final[i])).ToList(), headerVat);
        var planned = amounts.Select((a, i) => new OcrPlannedLine(
            recon.PricesIncludeVat ? Math.Round(a - vat[i], 2, MidpointRounding.AwayFromZero) : a, final[i], vat[i])).ToList();
        var check = OcrAmountIntegrity.Check(planned, headerVat, headerTotal, split.TaxableAmount, split.NonTaxableAmount);

        var (shifts, _) = DocumentRounding.CapShifts(items
            .Select((it, i) => final[i] <= 0m ? 0m : DocumentRounding.FromPrintedLine(it.Quantity, it.UnitPrice, gross[i]).Shift)
            .ToList());
        var lineAmount = amounts.Select((a, i) => a + shifts[i]).ToArray();
        var rounding = -shifts.Sum();
        return new ChainResult(final, vat, lineAmount, rounding, lineAmount.Sum() + rounding + vat.Sum(), plan, check.Problems);
    }

    private static readonly ScanItem[] ScommerceItems =
    {
        new("นมผงเอนฟาโกร เอนฟินิทัส สูตร3 นมผง เด็ก นมเอนฟาโกร enfa Enfinitas ชนิดจืด 1425 กรัม:สูตร3", 4m, 1228.04m, 4912.15m),
        new("ค่าจัดส่ง / Shipping Fee", 1m, 0.00m, 0.00m),
    };

    // ── ครึ่งที่ 1: ใบ Scommerce ต้องถูก ────────────────────────────────────────────

    [Fact]
    public void ใบScommerce_ตัวเลขหัวใบพิสูจน์ทั้งใบ7เปอร์เซ็นต์_VAT32867_รวม5024_ไม่มีคำเตือน()
    {
        var r = Chain(OcrPaperSamples.ScommerceLazada, ScommerceItems,
            headerNetBase: 4695.33m, headerVat: 328.67m, headerTotal: 5024.00m, headerDiscount: 216.82m);

        Assert.Equal(OcrLineVatPlanVerdict.AllStandard7, r.Plan.Verdict);
        Assert.Equal(new[] { 7m, 7m }, r.Rates);
        Assert.Equal(new[] { 328.67m, 0m }, r.LineVat);
        Assert.Equal(4695.34m, r.LineAmount[0]);          // 4 × 1,228.04 = 4,912.16 − ส่วนลด 216.82 (ยอดตาม ม.86/4)
        Assert.Equal(-0.01m, r.RoundingAdjustment);       // ผลต่างปัดเศษของราคาต่อหน่วย (RND-01)
        Assert.Equal(5024.00m, r.DocTotal);
        Assert.Empty(r.Gaps);                              // ไม่มี [Σ-GAP] ⇒ ไม่มีคำเตือนตอนอนุมัติ
        Assert.Contains("328.67", r.Plan.Reason);
        Assert.Contains("4,695.33", r.Plan.Reason);
    }

    [Fact]
    public void ชื่อที่ตัวเดายังตีเป็นยกเว้น_ชั้นพิสูจน์ทั้งใบชนะ_ไม่พึ่งรายการคำ()
    {
        // ชื่อที่ไม่มีคำ "นมผง" (P2 ไม่ครอบ) ตัวเดาจากชื่อยังให้ "ยกเว้น" — ตัวเลขหัวใบต้องชนะเอง ไม่ใช่รอเติมรายการคำทีละคำ
        var items = new[] { ScommerceItems[0] with { Description = "นมเอนฟาโกร enfa Enfinitas 1425 กรัม" }, ScommerceItems[1] };
        Assert.Equal("Exempt", ThaiVatTypeRule.Suggest(items[0].Description, null, "0105560113122"));
        var r = Chain(OcrPaperSamples.ScommerceLazada, items, 4695.33m, 328.67m, 5024.00m, 216.82m);
        Assert.Equal(new[] { 7m, 7m }, r.Rates);            // ชั้นพิสูจน์ทั้งใบชนะตัวเดา
        Assert.Empty(r.Gaps);
    }

    [Fact]
    public void เติมเฉพาะบรรทัดที่ยังว่าง_ค่าที่ชั้นบนตั้ง7ไว้แล้วไม่แตะ()
    {
        var p = OcrLineVatPlanner.PlanWholeInvoice(new[] { 4695.33m, 0m }, new decimal?[] { 7m, null },
            328.67m, 4695.33m, 5024.00m, false, 0m, vatPrintedOnPaper: true);
        Assert.True(p.Decided);
        Assert.Null(p.Rates[0]);
        Assert.Equal(7m, p.Rates[1]);
    }

    [Fact]
    public void ราคารวมVAT_ยอดรวมคูณ7ส่วน107เท่าVATหัวใบ_ตัดสินได้()
    {
        // ใบซูเปอร์มาร์เก็ต (ราคารวม VAT · ลดสมาชิก) หลังกระจายส่วนลด: 735.30 × 7/107 = 48.10
        var p = OcrLineVatPlanner.PlanWholeInvoice(new[] { 151.05m, 415.89m, 168.36m }, new decimal?[3],
            48.10m, 687.20m, 735.30m, pricesIncludeVat: true, paperExemptAmount: null, vatPrintedOnPaper: true);
        Assert.Equal(OcrLineVatPlanVerdict.AllStandard7, p.Verdict);
        Assert.Contains("7/107", p.Reason);
    }

    // ── ครึ่งที่ 2: ใบที่ต้องไม่ถูกแตะ ──────────────────────────────────────────────

    [Fact]
    public void ใบค้าส่งผสม_7เปอร์เซ็นต์ของ764ไม่เท่า28_ไม่ตัดสิน()
    {
        var p = OcrLineVatPlanner.PlanWholeInvoice(new[] { 125m, 189m, 50m, 400m }, new decimal?[4],
            28m, 764m, 792m, false, null, vatPrintedOnPaper: true);
        Assert.Equal(OcrLineVatPlanVerdict.Unknown, p.Verdict);
        Assert.Contains("53.48", p.Reason);
        Assert.All(p.Rates, r => Assert.Null(r));
    }

    [Fact]
    public void ใบค้าส่งผสมจริง_สัญลักษณ์บนกระดาษยังตัดสิน_ชั้นพิสูจน์ไม่แตะ()
    {
        var items = OcrPaperSamples.WholesaleLineAmounts.Select((a, i) => new ScanItem(
            new[] { "ไข่ไก่", "หมูสามชั้น", "ผักกาดขาว", "น้ำมันพืช", "น้ำปลา", "ผงซักฟอก" }[i], null, null, a)).ToArray();
        var r = Chain(OcrPaperSamples.WholesaleMixedVat, items, 764m, 28m, 792m, 0m);
        Assert.False(r.Plan.Decided);
        Assert.Equal(new[] { -1m, -1m, -1m, 7m, 7m, 7m }, r.Rates);
    }

    [Fact]
    public void Makro951_49_1000_ไม่ตัดสิน()
    {
        var p = OcrLineVatPlanner.PlanWholeInvoice(new[] { 700m, 251m }, new decimal?[2], 49m, 951m, 1000m, false, null, vatPrintedOnPaper: true);
        Assert.False(p.Decided);
        Assert.Contains("66.57", p.Reason);
    }

    [Fact]
    public void Makroหน้า3ใน3_กระดาษพิมพ์ยอดยกเว้น6260_ไม่ตัดสิน()
    {
        var exempt = OcrLineVatPlanner.PaperExemptAmount(
            OcrLineVatMarks.Read(OcrPaperSamples.MakroPage3of3), OcrLineVatMarks.ReadGroups(OcrPaperSamples.MakroPage3of3));
        Assert.Equal(6260.00m, exempt);
        var p = OcrLineVatPlanner.PlanWholeInvoice(new[] { 6260.00m, 16403.97m }, new decimal?[2],
            1148.28m, 22663.97m, 23812.25m, false, exempt, vatPrintedOnPaper: true);
        Assert.False(p.Decided);
        Assert.Contains("6,260.00", p.Reason);
    }

    [Fact]
    public void ใบส่งออกVAT0_ไม่ตัดสิน()
    {
        var p = OcrLineVatPlanner.PlanWholeInvoice(new[] { 100000m }, new decimal?[1], 0m, 100000m, 100000m, false, null, vatPrintedOnPaper: true);
        Assert.Equal(OcrLineVatPlanVerdict.Unknown, p.Verdict);
    }

    [Fact]
    public void ใบผสมนมสดกับน้ำปลา_ไม่ตัดสิน_ตัวเดาเดิมยังแยกยกเว้นได้()
    {
        const string raw = "ร้านค้าส่ง\nใบกำกับภาษี\nนมสด 2 ลิตร 100.00\nน้ำปลา 700 มล. 200.00\nรวม 300.00\nภาษีมูลค่าเพิ่ม 7% 14.00\nรวมทั้งสิ้น 314.00";
        var items = new[] { new ScanItem("นมสด 2 ลิตร", 1m, 100m, 100m), new ScanItem("น้ำปลา 700 มล.", 1m, 200m, 200m) };
        var r = Chain(raw, items, 300m, 14m, 314m, 0m);
        Assert.False(r.Plan.Decided);                       // 7% × 300 = 21 ≠ 14
        Assert.Equal(new[] { ThaiVatTypeRule.ExemptRate, 7m }, r.Rates);
        Assert.Equal(new[] { 0m, 14m }, r.LineVat);
        Assert.Empty(r.Gaps);
    }

    [Fact]
    public void กระดาษพิมพ์ยอดยกเว้นมากกว่า0_ไม่ตัดสินแม้เลขคณิตลงตัว()
    {
        var p = OcrLineVatPlanner.PlanWholeInvoice(new[] { 4695.33m, 0m }, new decimal?[2],
            328.67m, 4695.33m, 5024.00m, false, paperExemptAmount: 100m, vatPrintedOnPaper: true);
        Assert.False(p.Decided);
        Assert.Contains("100.00", p.Reason);
    }

    [Fact]
    public void ชั้นบนบอกว่ามีบรรทัดยกเว้น_หลักฐานขัดกัน_ไม่ตัดสิน()
    {
        var p = OcrLineVatPlanner.PlanWholeInvoice(new[] { 4695.33m, 10m }, new decimal?[] { ThaiVatTypeRule.ExemptRate, null },
            328.67m, 4705.33m, 5034.00m, false, null, vatPrintedOnPaper: true);
        Assert.False(p.Decided);
        Assert.Contains("บรรทัดที่ 1", p.Reason);
    }

    [Fact]
    public void ผลรวมบรรทัดไม่ตรงฐานหัวใบ_ไม่ตัดสิน()
    {
        var p = OcrLineVatPlanner.PlanWholeInvoice(new[] { 4600.00m }, new decimal?[1], 328.67m, 4695.33m, 5024.00m, false, null, vatPrintedOnPaper: true);
        Assert.False(p.Decided);
    }

    // ── คำแนะนำตอนอนุมัติ (P4) ───────────────────────────────────────────────────────

    [Fact]
    public void คำแนะนำตอนอนุมัติ_ใบScommerceที่สร้างไปแล้ว_บอกให้ตั้ง7พร้อมตัวเลข()
    {
        var advice = OcrLineVatPlanner.RateAdvice(
            new[] { (4695.33m, ThaiVatTypeRule.ExemptRate), (0m, 7m) }, 4695.33m, 328.67m, null, vatPrintedOnPaper: true);
        Assert.Equal("ตั้งอัตรา VAT บรรทัดเป็น 7% (VAT หัวใบ 328.67 = 7% × 4,695.33)", advice);
    }

    [Fact]
    public void คำแนะนำตอนอนุมัติ_ไม่แนะนำเมื่อแก้แล้ว_ใบผสม_หรือกระดาษมียอดยกเว้น()
    {
        Assert.Null(OcrLineVatPlanner.RateAdvice(new[] { (4695.34m, 7m), (0m, 7m) }, 4695.33m, 328.67m, null, true));
        Assert.Null(OcrLineVatPlanner.RateAdvice(new[] { (364m, -1m), (400m, 7m) }, 764m, 28m, null, true));
        Assert.Null(OcrLineVatPlanner.RateAdvice(new[] { (4695.33m, -1m) }, 4695.33m, 328.67m, 50m, true));
        Assert.Null(OcrLineVatPlanner.RateAdvice(new[] { (1000m, 0m) }, 1000m, 0m, null, true));
    }

    // ── รอบ 195 ฝ่ายค้าน C1: VAT ที่ระบบคำนวณเองห้ามใช้ "พิสูจน์" ว่าทั้งใบ 7% (ด่านที่ป้อนผลของสูตรที่ตัวเองตรวจ = ผ่านตลอดกาล) ──

    /// <summary>ใบร้านผักที่มีเลขผู้เสียภาษี 13 หลัก — รายการ "ผักกาดขาว 500 · ผลไม้รวม 570" รวม 1,070 ไม่พิมพ์ VAT
    /// (สินค้ายกเว้น ม.81) · ระบบเดิมแยก 7/107 ได้ VAT 70.00 เอง</summary>
    private const string VegetableReceipt =
        "ร้านผักสดป้าแดง\n" +
        "เลขประจำตัวผู้เสียภาษี 0105556012341\n" +
        "ใบเสร็จรับเงิน/ใบกำกับภาษี\n" +
        "ผักกาดขาว                500.00\n" +
        "ผลไม้รวม                 570.00\n" +
        "รวมทั้งสิ้น              1,070.00\n" +
        "สินค้าทุกรายการได้รับการยกเว้นภาษีมูลค่าเพิ่ม";

    private static readonly ScanItem[] VegetableItems =
    {
        new("ผักกาดขาว", 1m, 500m, 500m),
        new("ผลไม้รวม", 1m, 570m, 570m),
    };

    /// <summary>หมายเหตุที่ตัวสร้างบรรทัด (BuildScanLinesAsync) จะเขียนจากผลของ <see cref="Chain"/> — [VAT-DERIVED] + [Σ-GAP]</summary>
    private static string NotesAfterBuild(string rawText, decimal headerVat, ChainResult r)
    {
        var src = OcrHeaderVatEvidence.Classify(rawText, null, headerVat, null);
        var notes = "[Tier] Tesseract";
        if (OcrHeaderVatEvidence.DerivedNote(src, headerVat) is string d) notes += "\n" + OcrHeaderVatEvidence.DerivedTag + " " + d;
        foreach (var g in r.Gaps) notes += "\n" + OcrLineBuildNotes.GapTag + " " + g.Message;
        return notes;
    }

    [Fact]
    public void ใบผักรวม1070_VATคำนวณ_ไม่ตัดสิน()
    {
        // VAT 70.00 = round(1,070 × 7/107) — ถ้าชั้นพิสูจน์เชื่อค่านี้ เงื่อนไขเลขคณิตผ่านทุกครั้งโดยการสร้าง
        Assert.Equal(OcrHeaderVatSource.NotOnPaper, OcrHeaderVatEvidence.Classify(VegetableReceipt, null, 70.00m, null));
        var p = OcrLineVatPlanner.PlanWholeInvoice(new[] { 500m, 570m },
            new decimal?[2], 70.00m, 1000m, 1070m, pricesIncludeVat: true, paperExemptAmount: null, vatPrintedOnPaper: false);
        Assert.Equal(OcrLineVatPlanVerdict.Unknown, p.Verdict);
        Assert.All(p.Rates, r => Assert.Null(r));
        Assert.Contains("70.00", p.Reason);
        Assert.Contains("ไม่ได้อ่าน", p.Reason);

        // ทางทั้งเส้น: ไม่มี "พิสูจน์" · ตัวเดาเดิมตีเป็นยกเว้น · ตาข่าย [Σ-GAP] กลับมา · [VAT-DERIVED] · อนุมัติเองไม่ได้
        var r = Chain(VegetableReceipt, VegetableItems, headerNetBase: 1000m, headerVat: 70.00m, headerTotal: 1070m, headerDiscount: 0m,
            vendorTaxId: "0105556012341");
        Assert.False(r.Plan.Decided);
        Assert.Equal(new[] { ThaiVatTypeRule.ExemptRate, ThaiVatTypeRule.ExemptRate }, r.Rates);
        Assert.Equal(new[] { 0m, 0m }, r.LineVat);                         // ไม่มี VAT 70 ปลอมลงบรรทัด
        Assert.NotEmpty(r.Gaps);
        var verdict = OcrPostingReadiness.Evaluate(NotesAfterBuild(VegetableReceipt, 70.00m, r), hasUsableDate: true);
        Assert.False(verdict.CanAutoApprove);
    }

    [Fact]
    public void VATคำนวณเอง_รายการไม่ยกเว้น_ไม่มีGap_แต่ยังห้ามอนุมัติเองเพราะVATไม่มีบนกระดาษ()
    {
        // (ง) ใบที่ตัวเดาตั้ง 7% ทุกบรรทัดบน VAT ที่ระบบแยกเอง — ด่านยอด (OcrAmountIntegrity) ผ่านโดยการสร้าง (Σ VAT เฉลี่ยจาก
        // VAT หัวใบ · 7% × ฐาน = VAT แต่ง) ⇒ ตัวหยุดคือ [VAT-DERIVED] ตัวเดียว
        const string raw = "ร้านซ่อมแอร์เย็นฉ่ำ\nเลขประจำตัวผู้เสียภาษี 0105556012341\nใบเสร็จรับเงิน/ใบกำกับภาษี\n"
            + "ค่าบริการล้างแอร์ 2 เครื่อง   1,070.00\nรวมทั้งสิ้น 1,070.00\nราคารวมภาษีมูลค่าเพิ่มแล้ว";
        var r = Chain(raw, new[] { new ScanItem("ค่าบริการล้างแอร์ 2 เครื่อง", 1m, 1070m, 1070m) },
            headerNetBase: 1000m, headerVat: 70.00m, headerTotal: 1070m, headerDiscount: 0m, vendorTaxId: "0105556012341");
        Assert.False(r.Plan.Decided);                  // ไม่ใช่ชั้นพิสูจน์ที่ตั้ง 7% (ตัวเดาจากชื่อ — ชั้นล่างสุด)
        Assert.Equal(new[] { 7m }, r.Rates);
        Assert.Empty(r.Gaps);                          // ด่านยอดผ่านโดยการสร้าง — ตรงนี้คือเหตุที่ต้องมีแท็ก
        var verdict = OcrPostingReadiness.Evaluate(NotesAfterBuild(raw, 70.00m, r), hasUsableDate: true);
        Assert.False(verdict.CanAutoApprove);
        Assert.Contains("ไม่ได้พิมพ์", verdict.Reason);
    }

    [Fact]
    public void ใบScommerceจากAzure_ป้ายVATกับตัวเลขคนละบรรทัด_ยังพิสูจน์ได้7_7_และไม่ติดVATDERIVED()
    {
        // ข้อความจริงจาก Azure DI (erp-review/2026-09-25/ocr-scommerce) — ป้าย "ภาษีมูลค่าเพิ่ม 7% / VAT 7%" แล้ว "328.67" บรรทัดถัดไป
        const string azure = "ใบเสร็จรับเงิน/ใบกำกับภาษี\nนมผงเอนฟาโกร เอนฟินิทัส สูตร3\n4\n1,228.04\n4,912.15\n"
            + "ค่าจัดส่ง / Shipping Fee\n1\n0.00\n0.00\n"
            + "มูลค่าสินค้าที่ยกเว้นภาษีมูลค่าเพิ่ม/ Total Value of Exempt VAT Amount\n0.00\n"
            + "มูลค่าสินค้าก่อนภาษีมูลค่าเพิ่มหลังหักส่วนลด/ Total Value of Gross Amount (Excluded VAT) after discount\n4,695.33\n"
            + "ภาษีมูลค่าเพิ่ม 7% / VAT 7%\n328.67\n"
            + "มูลค่าสินค้ารวมภาษีมูลค่าเพิ่ม/ Total Value of Product Amount (Included VAT)\n5,024.00\n"
            + "รวมเงินทั้งสิ้น/ Total Amount\n5,024.00";
        Assert.Equal(OcrHeaderVatSource.Labelled, OcrHeaderVatEvidence.Classify(azure, null, 328.67m, null));
        var r = Chain(azure, ScommerceItems, 4695.33m, 328.67m, 5024.00m, 216.82m);
        Assert.Equal(new[] { 7m, 7m }, r.Rates);
        Assert.Empty(r.Gaps);
        Assert.True(OcrPostingReadiness.Evaluate(NotesAfterBuild(azure, 328.67m, r), hasUsableDate: true).CanAutoApprove);
    }

    [Fact]
    public void คำแนะนำตอนอนุมัติ_VATคำนวณเอง_ไม่แนะนำให้ตั้ง7()
    {
        Assert.Null(OcrLineVatPlanner.RateAdvice(new[] { (1000m, ThaiVatTypeRule.ExemptRate) }, 1000m, 70m, null, vatPrintedOnPaper: false));
        Assert.NotNull(OcrLineVatPlanner.RateAdvice(new[] { (1000m, ThaiVatTypeRule.ExemptRate) }, 1000m, 70m, null, vatPrintedOnPaper: true));
    }
}
