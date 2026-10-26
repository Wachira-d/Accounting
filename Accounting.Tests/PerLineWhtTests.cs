using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม F — หัก ณ ที่จ่ายตามรายการ (<c>docs/PER_LINE_WHT_DESIGN.md</c>) · ตัวคำนวณกลาง <see cref="PerLineWht"/> · ตัวตัดสินอัตรา <see cref="DocumentFx.Decide"/> ·
/// OCR ยอดบาทบนใบต่างสกุล <see cref="OcrThbEquivalent"/> · OCR อัตราต่อบรรทัด <see cref="OcrLineWhtPlan"/> ·
/// ทุกเรื่องมีสองครึ่ง: เคสใหม่ถูก และ<b>ใบบาท/ปิดฟีเจอร์ไม่ถูกแตะแม้แต่สตางค์เดียว</b> (เทสต์ทิศตรงข้าม)
/// </summary>
public class PerLineWhtTests
{
    private static Document Doc(string currency, decimal rate, decimal? thbBase, params (decimal Amount, decimal Rate, decimal Wht, string? Code)[] lines)
    {
        var d = new Document { Currency = currency, ExchangeRate = rate, DocumentThbBase = thbBase, DocumentNumber = "PV-TEST" };
        var order = 1;
        foreach (var l in lines)
            d.Lines.Add(new DocumentLine
            {
                Id = Guid.NewGuid(), LineOrder = order++, Description = $"บรรทัด {order}", Quantity = 1, UnitPrice = l.Amount,
                Amount = l.Amount, WithholdingTaxRate = l.Rate, WithholdingTaxAmount = l.Wht, IncomeTypeCode = l.Code,
            });
        d.SubTotal = d.Lines.Sum(x => x.Amount);
        d.WithholdingTaxAmount = d.Lines.Sum(x => x.WithholdingTaxAmount);
        return d;
    }

    // ═══ PerLineWht — ใบบาท = ยอดที่เก็บไว้ตรงตัว (ทิศตรงข้าม) ═══

    [Fact]
    public void Thb_document_returns_stored_amounts_exactly_even_when_reconciled_satang_differ_from_rate()
    {
        // บรรทัดที่ตัวกระทบเศษปรับไว้ (101.50 × 3% = 3.045 → เก็บ 3.04 ตามกลุ่ม) — ตัวคำนวณต้องไม่คิดใหม่
        var r = PerLineWht.ToBaht(new[]
        {
            new PerLineWhtLine(101.50m, 3m, 3.04m, "8"),
            new PerLineWhtLine(500m, 2m, 10m, "8ad"),
        }, 1m, null);
        Assert.Equal(101.50m, r[0].BaseThb);
        Assert.Equal(3.04m, r[0].WhtThb);
        Assert.Equal(10m, r[1].WhtThb);
        Assert.Equal(WhtBahtBasis.Thb, PerLineWht.BasisOf(1m, null));
        Assert.Equal(WhtBahtBasis.Thb, PerLineWht.BasisOf(0m, null));   // ข้อมูลเก่าอัตรา 0 = บาท (เหมือน DocumentFx.ToBaht)
    }

    [Fact]
    public void Foreign_document_converts_base_with_rate_and_tax_is_baht_base_times_rate()
    {
        var r = PerLineWht.ToBaht(new[] { new PerLineWhtLine(1_000m, 3m, 30m, "8") }, 36.5m, null);
        Assert.Equal(36_500m, r[0].BaseThb);
        Assert.Equal(1_095m, r[0].WhtThb);
    }

    [Fact]
    public void Foreign_tax_uses_baht_base_not_converted_rounded_foreign_tax()
    {
        // 100.01 USD × 3% = 3.0003 → 3.00 USD → × 36 = 108.00 · ฐานบาท 3,600.36 × 3% = 108.01 (นิยามตามกฎหมาย)
        var r = PerLineWht.ToBaht(new[] { new PerLineWhtLine(100.01m, 3m, 3.00m, "8") }, 36m, null);
        Assert.Equal(3_600.36m, r[0].BaseThb);
        Assert.Equal(108.01m, r[0].WhtThb);
    }

    private static IReadOnlyList<decimal> Bases(decimal[] amounts, decimal rate, decimal? printed)
        => PerLineWht.ToBaht(amounts.Select(a => new PerLineWhtLine(a, 0m, 0m, null)).ToList(), rate, printed).Select(x => x.BaseThb).ToList();

    [Fact]
    public void Printed_baht_base_is_allocated_pro_rata_and_sums_exactly()
    {
        var bases = Bases(new[] { 600m, 400m }, 36.5m, 36_512.34m);
        Assert.Equal(21_907.40m, bases[0]);
        Assert.Equal(14_604.94m, bases[1]);
        Assert.Equal(36_512.34m, bases.Sum());

        // เศษสตางค์ไปบรรทัดยอดสูงสุด (เท่ากัน ⇒ บรรทัดแรก) — กำหนดแน่นอน
        var thirds = Bases(new[] { 1m, 1m, 1m }, 30m, 100m);
        Assert.Equal(new[] { 33.34m, 33.33m, 33.33m }, thirds);
    }

    [Fact]
    public void Printed_base_with_zero_line_sum_falls_back_to_rate()
    {
        Assert.Equal(0m, Bases(new[] { 0m }, 36m, 100m)[0]);
    }

    [Fact]
    public void Mixed_document_groups_by_income_type_and_rate_and_excludes_zero_rate_goods()
    {
        // ค่าบริการ 3% + ค่าโฆษณา 2% + ค่าสินค้า 0% (ตัวอย่างของผู้ใช้) + หักแต่ไม่ระบุประเภท ⇒ "8" (ค่าตั้งต้นเดียวกับ 50 ทวิ)
        var groups = PerLineWht.GroupsOf(Doc("THB", 1m, null,
            (5_000m, 3m, 150m, "8"), (3_000m, 2m, 60m, "8ad"), (2_000m, 0m, 0m, null), (1_000m, 3m, 30m, null)));
        Assert.Equal(2, groups.Count);
        Assert.Equal(("8", 3m, 6_000m, 180m, 2), (groups[0].IncomeTypeCode, groups[0].RatePercent, groups[0].IncomeThb, groups[0].WhtThb, groups[0].LineCount));
        Assert.Equal(("8ad", 2m, 3_000m, 60m, 1), (groups[1].IncomeTypeCode, groups[1].RatePercent, groups[1].IncomeThb, groups[1].WhtThb, groups[1].LineCount));
    }

    [Fact]
    public void Payer_borne_lines_scale_stored_amounts_by_baht_factor()
    {
        // ออกแทนครั้งเดียว: ภาษี 30.93 · ส่วนที่ออกแทน 30.93 ของฐาน 1,000 USD @ 36 ⇒ คงสัดส่วน (ไม่คิด ฐาน × อัตรา ใหม่)
        var r = PerLineWht.ToBaht(new[] { new PerLineWhtLine(1_000m, 3m, 30.93m, "8", PayerBorne: 30.93m) }, 36m, null);
        Assert.Equal(36_000m, r[0].BaseThb);
        Assert.Equal(1_113.48m, r[0].PayerBorneThb);
        Assert.Equal(1_113.48m, r[0].WhtThb);
        Assert.Equal(37_113.48m, r[0].IncomeThb);
    }

    [Fact]
    public void Printed_base_usable_only_while_consistent_with_lines()
    {
        Assert.Equal(36_512.34m, PerLineWht.UsablePrintedBase(Doc("USD", 36.51234m, 36_512.34m, (1_000m, 3m, 30m, "8"))));
        // ผู้ใช้แก้บรรทัดจนฐานเปลี่ยน (อัตราตรึงไว้) ⇒ ยอดบนกระดาษไม่ใช่ฐานของบรรทัดชุดนี้แล้ว
        Assert.Null(PerLineWht.UsablePrintedBase(Doc("USD", 36.51234m, 36_512.34m, (1_200m, 3m, 36m, "8"))));
        Assert.Null(PerLineWht.UsablePrintedBase(Doc("USD", 36.5m, null, (1_000m, 3m, 30m, "8"))));
        Assert.Null(PerLineWht.UsablePrintedBase(Doc("USD", 36.5m, 0m, (1_000m, 3m, 30m, "8"))));
    }

    [Fact]
    public void ForDocument_uses_printed_base_and_falls_back_to_rate_when_unusable()
    {
        var doc = Doc("USD", 36.51234m, 36_512.34m, (600m, 3m, 18m, "8"), (400m, 2m, 8m, "8ad"));
        var map = PerLineWht.ForDocument(doc);
        var lines = doc.Lines.OrderBy(l => l.LineOrder).ToList();
        Assert.Equal(21_907.40m, map[lines[0].Id].BaseThb);
        Assert.Equal(657.22m, map[lines[0].Id].WhtThb);     // 21,907.40 × 3% = 657.222
        Assert.Equal(292.10m, map[lines[1].Id].WhtThb);     // 14,604.94 × 2% = 292.0988
        Assert.Equal(WhtBahtBasis.PrintedThb, PerLineWht.BasisOf(doc));

        doc.DocumentThbBase = 50_000m;   // ไม่สอดคล้องกับฐาน × อัตรา ⇒ ใช้อัตรา
        Assert.Equal(WhtBahtBasis.ExchangeRate, PerLineWht.BasisOf(doc));
        Assert.Equal(DocumentFx.ToBaht(600m, 36.51234m), PerLineWht.ForDocument(doc)[lines[0].Id].BaseThb);
    }

    [Fact]
    public void ForDocument_skips_deleted_lines()
    {
        var doc = Doc("USD", 36m, null, (100m, 3m, 3m, "8"), (50m, 3m, 1.5m, "8"));
        var deleted = doc.Lines.Last();
        deleted.IsDeleted = true;
        var map = PerLineWht.ForDocument(doc);
        Assert.False(map.ContainsKey(deleted.Id));
        Assert.Single(map);
    }

    [Fact]
    public void TieOut_absorbs_satang_into_largest_and_refuses_large_gaps()
    {
        Assert.Equal(new[] { 1_095.01m, 10m }, PerLineWht.TieOut(new[] { 1_095.00m, 10m }, 1_105.01m));
        // ต่างเกินระดับเศษ ⇒ คงเดิม (ผู้เรียกล้มดัง/ตัวกระทบ GL ฟ้อง ไม่ดูดเงียบ)
        Assert.Equal(new[] { 1_095.00m, 10m }, PerLineWht.TieOut(new[] { 1_095.00m, 10m }, 1_200m));
    }

    [Fact]
    public void Print_breakdown_only_for_multiple_rates()
    {
        Assert.Empty(PerLineWht.RateBreakdownForPrint(Doc("THB", 1m, null, (1_000m, 3m, 30m, "8"), (500m, 3m, 15m, "8"))));
        var mixed = PerLineWht.RateBreakdownForPrint(Doc("THB", 1m, null, (1_000m, 2m, 20m, "8ad"), (500m, 3m, 15m, "8"), (300m, 0m, 0m, null)));
        Assert.Equal(2, mixed.Count);
        Assert.Equal((3m, 15m), (mixed[0].RatePercent, mixed[0].WhtAmount));
        Assert.Equal((2m, 20m), (mixed[1].RatePercent, mixed[1].WhtAmount));
    }

    [Fact]
    public void GroupsOf_thb_document_matches_stored_lines()
    {
        var doc = Doc("THB", 1m, null, (5_000m, 3m, 150m, "8"), (3_000m, 2m, 60m, "8ad"), (2_000m, 0m, 0m, null));
        var g = PerLineWht.GroupsOf(doc);
        Assert.Equal(210m, g.Sum(x => x.WhtThb));
        Assert.Equal(8_000m, g.Sum(x => x.IncomeThb));
    }

    // ═══ DocumentFx — อัตราจากยอดบาทที่พิมพ์ + ตัวตัดสินตอนสร้าง ═══

    [Fact]
    public void Rate_from_printed_baht_is_six_decimals_away_from_zero()
    {
        Assert.Equal(36.51234m, DocumentFx.RateFromPrintedBaht(1_000m, 36_512.34m));
        Assert.Equal(36.550026m, DocumentFx.RateFromPrintedBaht(12_345.67m, 451_234.56m));   // 36.5500259… → 6 ตำแหน่ง
        Assert.Null(DocumentFx.RateFromPrintedBaht(0m, 100m));
        Assert.Null(DocumentFx.RateFromPrintedBaht(100m, 0m));
    }

    [Fact]
    public void Decide_thb_document_keeps_rate_one_and_rejects_printed_base()
    {
        var ok = DocumentFx.Decide("THB", 1m, false, null, null, 1_000m, false, null, null);
        Assert.Equal((1m, (string?)null, (decimal?)null, (string?)null), (ok.Rate, ok.Source, ok.ThbBase, ok.Error));
        var bad = DocumentFx.Decide("THB", 1m, false, null, 36_500m, 1_000m, false, null, null);
        Assert.NotNull(bad.Error);
    }

    [Fact]
    public void Decide_printed_base_derives_rate_and_marks_document_source()
    {
        var d = DocumentFx.Decide("USD", 36.4m, true, "User", 36_512.34m, 1_000m, false, null, null);
        Assert.Null(d.Error);
        Assert.Equal(36.51234m, d.Rate);
        Assert.Equal(DocumentFx.SourceDocument, d.Source);
        Assert.Equal(36_512.34m, d.ThbBase);
        Assert.Equal(36_512.34m, DocumentFx.ToBaht(1_000m, d.Rate));   // ฐานสกุลเอกสาร × อัตรา = ยอดบนกระดาษ (JE ตรงกระดาษ)
    }

    [Fact]
    public void Decide_rejects_non_positive_printed_base_and_zero_subtotal()
    {
        Assert.NotNull(DocumentFx.Decide("USD", 36m, false, null, 0m, 1_000m, false, null, null).Error);
        Assert.NotNull(DocumentFx.Decide("USD", 36m, false, null, -5m, 1_000m, false, null, null).Error);
        Assert.NotNull(DocumentFx.Decide("USD", 36m, false, null, 36_000m, 0m, false, null, null).Error);
    }

    [Fact]
    public void Decide_locked_to_source_keeps_source_rate_but_stores_printed_base()
    {
        // ใบลดหนี้ที่ใช้อัตราใบเดิม — ตัดหนี้ต้องเท่ายอดที่ตั้ง ⇒ อัตราไม่ถูกเปลี่ยน
        var d = DocumentFx.Decide("USD", 36.2m, true, null, 3_650m, 100m, true, DocumentFx.SourceBot, null);
        Assert.Equal(36.2m, d.Rate);
        Assert.Equal(DocumentFx.SourceBot, d.Source);
        Assert.Equal(3_650m, d.ThbBase);
    }

    [Fact]
    public void Decide_inherits_printed_base_from_full_conversion()
    {
        var d = DocumentFx.Decide("USD", 36.51234m, true, null, null, 1_000m, true, DocumentFx.SourceDocument, 36_512.34m);
        Assert.Equal((36.51234m, DocumentFx.SourceDocument, (decimal?)36_512.34m), (d.Rate, d.Source, d.ThbBase));
    }

    [Fact]
    public void Decide_source_labels_for_plain_foreign_documents()
    {
        Assert.Equal(DocumentFx.SourceBot, DocumentFx.Decide("USD", 36m, false, null, null, 100m, false, null, null).Source);
        Assert.Equal(DocumentFx.SourceUser, DocumentFx.Decide("USD", 36m, true, null, null, 100m, false, null, null).Source);
        Assert.Equal(DocumentFx.SourceBot, DocumentFx.Decide("USD", 36m, true, "BOT", null, 100m, false, null, null).Source);
        // อ้างว่า "จากเอกสาร" แต่ไม่ส่งยอดบาท ⇒ ไม่เชื่อ (ผู้ใช้กรอก)
        Assert.Equal(DocumentFx.SourceUser, DocumentFx.Decide("USD", 36m, true, "Document", null, 100m, false, null, null).Source);
        Assert.Null(DocumentFx.SourceLabel(null));
        Assert.NotNull(DocumentFx.SourceLabel(DocumentFx.SourceDocument));
    }

    // ═══ OcrThbEquivalent — ยอดบาทบนใบต่างสกุล (ข้อความตัวอย่าง) ═══

    private const string EnglishInvoice = """
        INVOICE
        Currency: USD
        Subtotal  USD 1,000.00
        VAT 7%  USD 70.00
        Total  USD 1,070.00
        Exchange Rate: 1 USD = 36.5120 THB
        Subtotal (THB) 36,512.00
        VAT 7% (THB) 2,555.84
        Total (THB) 39,067.84
        """;

    [Fact]
    public void Reads_rate_subtotal_vat_and_total_in_baht()
    {
        var r = OcrThbEquivalent.Read(EnglishInvoice);
        Assert.Equal(36.5120m, r.Rate);
        Assert.Equal(36_512.00m, r.SubTotalThb);
        Assert.Equal(2_555.84m, r.VatThb);
        Assert.Equal(39_067.84m, r.TotalThb);

        var plan = OcrThbEquivalent.Plan(r, 1_000m, 70m, 1_070m, botRate: null);   // หลักฐาน 3 แหล่งสอดคล้อง ⇒ ไม่ต้องพึ่ง ธปท.
        Assert.Null(plan.Problem);
        Assert.Equal(36.512m, plan.Rate);
        Assert.Equal(36_512m, plan.ThbBase);
    }

    [Fact]
    public void Thai_equivalent_total_without_vat_split_gives_rate_only()
    {
        const string text = """
            ใบแจ้งหนี้ / Invoice
            รวมเงิน 2,500.00 USD
            ภาษีมูลค่าเพิ่ม 7% 175.00 USD
            รวมทั้งสิ้น 2,675.00 USD
            คิดเป็นเงินบาท 97,637.50 บาท
            (เก้าหมื่นเจ็ดพันหกร้อยสามสิบเจ็ดบาทห้าสิบสตางค์)
            """;
        var r = OcrThbEquivalent.Read(text);
        Assert.Equal(97_637.50m, r.TotalThb);
        Assert.Null(r.SubTotalThb);
        var plan = OcrThbEquivalent.Plan(r, 2_500m, 175m, 2_675m, botRate: 36.4m);   // แหล่งเดียว ⇒ ต้องใกล้ ธปท. (≤ 3%)
        Assert.Equal(36.5m, plan.Rate);
        Assert.Null(plan.ThbBase);   // กระดาษไม่บอกฐานก่อน VAT เป็นบาท ⇒ ฐานมาจากอัตรา (ไม่แต่ง)
    }

    [Fact]
    public void Same_line_dual_currency_and_incl_vat_label_and_amount_words()
    {
        const string text = """
            Total USD 1,070.00 (THB 39,067.84)
            อัตราแลกเปลี่ยน 36.5120
            จำนวนเงิน (สามหมื่นเก้าพันหกสิบเจ็ดบาทถ้วน) 39,999.00
            """;
        var r = OcrThbEquivalent.Read(text);
        Assert.Equal(39_067.84m, r.TotalThb);   // เลขที่ติดคำบ่งบาท ไม่ใช่เลข USD · บรรทัด "…บาทถ้วน" ไม่นับ
        Assert.Equal(36.5120m, r.Rate);
        Assert.Null(r.VatThb);

        var incl = OcrThbEquivalent.Read("Total incl. VAT (THB) 39,067.84");
        Assert.Equal(39_067.84m, incl.TotalThb);   // "รวม VAT" = ยอดรวม ไม่ใช่ยอด VAT
        Assert.Null(incl.VatThb);
    }

    [Fact]
    public void Inconsistent_baht_figures_are_not_used()
    {
        const string text = """
            Exchange Rate: 1 USD = 36.50 THB
            Total (THB) 45,000.00
            """;
        var plan = OcrThbEquivalent.Plan(OcrThbEquivalent.Read(text), 1_000m, 70m, 1_070m, botRate: 36.4m);
        Assert.Null(plan.Rate);
        Assert.Contains(OcrThbEquivalent.ConflictTag, plan.Problem);
    }

    [Fact]
    public void Page_without_baht_figures_gives_nothing()
    {
        // ทิศตรงข้าม: บรรทัดบัญชีธนาคารที่มีคำ THB แต่ไม่มีป้ายยอด ⇒ ไม่ใช่ยอดบาท
        const string text = """
            Total USD 1,070.00
            Bank: KBank THB account 123-4-56789-0
            """;
        var r = OcrThbEquivalent.Read(text);
        Assert.False(r.Any);
        var plan = OcrThbEquivalent.Plan(r, 1_000m, 70m, 1_070m, botRate: 36.4m);
        Assert.Null(plan.Rate);
        Assert.Null(plan.Problem);
        Assert.False(OcrThbEquivalent.Read(null).Any);
    }

    // ═══ ฝ่ายค้านทีม F ═══

    [Fact]
    public void Single_source_baht_figure_needs_bot_rate_within_tolerance()
    {
        var r = new OcrThbReading(null, null, null, 97_637.50m);   // "คิดเป็นเงินบาท" บรรทัดเดียว
        // ไม่มี ธปท. ⇒ ไม่ใช้ + แท็กเตือน (เดิมรับทันทีแล้วประทับ "จากเอกสาร")
        var none = OcrThbEquivalent.Plan(r, 2_500m, 175m, 2_675m, botRate: null);
        Assert.Null(none.Rate);
        Assert.Contains(OcrThbEquivalent.UnverifiedTag, none.Problem);
        // ห่าง ธปท. เกิน 3% (อ่านตัวเลขผิดหลัก) ⇒ ไม่ใช้
        var far = OcrThbEquivalent.Plan(r, 2_500m, 175m, 2_675m, botRate: 30m);
        Assert.Null(far.Rate);
        Assert.Contains(OcrThbEquivalent.UnverifiedTag, far.Problem);
        // ทิศตรงข้าม: ใกล้ ธปท. ⇒ ใช้
        Assert.Equal(36.5m, OcrThbEquivalent.Plan(r, 2_500m, 175m, 2_675m, botRate: 36.0m).Rate);
    }

    [Fact]
    public void Small_unit_currency_printed_base_that_does_not_round_trip_is_refused()
    {
        // VND 98,765,432 ↔ 135,432.10 บาท ⇒ อัตรา 6 ตำแหน่ง 0.001371 ⇒ คูณกลับ 135,407.41 (ต่าง −24.69) ⇒ 50 ทวิ ≠ GL/ภ.พ.30
        Assert.Equal(0.001371m, DocumentFx.RateFromPrintedBaht(98_765_432m, 135_432.10m));
        Assert.False(DocumentFx.PrintedBahtRoundTrips(98_765_432m, 0.001371m, 135_432.10m));
        var d = DocumentFx.Decide("VND", 0.00137m, false, null, 135_432.10m, 98_765_432m, false, null, null);
        Assert.NotNull(d.Error);
        Assert.Contains("กรอกอัตราแลกเปลี่ยนเอง", d.Error);
        // เอกสารที่เก็บไว้แล้ว (ก่อนด่าน) ⇒ ยอดบาทไม่ใช่ฐาน (ตกไปใช้อัตรา — 50 ทวิ ตรง GL)
        Assert.Null(PerLineWht.UsablePrintedBase(Doc("VND", 0.001371m, 135_432.10m, (98_765_432m, 3m, 2_962_962.96m, "8"))));
        // OCR: ไม่ใช้ + แท็กขัดกัน (หยุดอนุมัติเอง)
        var plan = OcrThbEquivalent.Plan(new OcrThbReading(null, 135_432.10m, null, null), 98_765_432m, 0m, 98_765_432m, botRate: 0.00137m);
        Assert.Null(plan.Rate);
        Assert.Contains(OcrThbEquivalent.ConflictTag, plan.Problem);
        // ทิศตรงข้าม: USD ที่แปลงกลับได้ตรง ⇒ ใช้ได้
        Assert.True(DocumentFx.PrintedBahtRoundTrips(1_000m, 36.51234m, 36_512.34m));
    }

    [Fact]
    public void System_split_wht_and_baht_conflicts_stop_auto_approve()
    {
        foreach (var tag in new[] { OcrLineWhtPlan.AppliedTag, OcrLineWhtPlan.MismatchTag, OcrThbEquivalent.ConflictTag })
        {
            Assert.Contains(OcrPostingReadiness.BlockingTags, t => t.Tag == tag);
            Assert.False(OcrPostingReadiness.Evaluate(tag + " รายละเอียด", true).CanAutoApprove);
        }
        // ทิศตรงข้าม: ใช้ยอดบาทที่ตรวจไขว้แล้ว (ไม่มีแท็กขัดกัน) ไม่หยุดเพราะแท็กนี้
        Assert.True(OcrPostingReadiness.Evaluate(OcrThbEquivalent.UsedTag + " ใช้ยอดบาท", true).CanAutoApprove);
    }

    [Fact]
    public void Line_plan_notes_are_recomputed_each_build()
    {
        var kept = OcrLineBuildNotes.StripRecomputed($"{OcrLineWhtPlan.AppliedTag} เก่า\n[VAT-CLAIM] คงไว้\n{OcrLineWhtPlan.MismatchTag} เก่า");
        Assert.Equal("[VAT-CLAIM] คงไว้", kept);
    }

    [Fact]
    public void Plan_tolerance_is_in_baht()
    {
        var lines = new[] { Svc(5_000m, "8"), Svc(3_000m, "8ad"), Goods(2_000m) };   // แผน 210
        // ใบ USD@36: ต่าง 0.50 USD = 18 บาท ⇒ เกิน 1 บาท ⇒ ไม่ใช้
        Assert.False(OcrLineWhtPlan.Decide(lines, 2.1m, "8", 210.50m, true, 36m).Applied);
        // ทิศตรงข้าม: ใบบาทต่าง 0.50 บาท ⇒ ใช้
        Assert.True(OcrLineWhtPlan.Decide(lines, 2.1m, "8", 210.50m, true, 1m).Applied);
        // ไม่รู้อัตรา ⇒ เข้มสุด (± 0.01 สกุลเอกสาร)
        Assert.False(OcrLineWhtPlan.Decide(lines, 2.1m, "8", 210.02m, true, null).Applied);
        Assert.True(OcrLineWhtPlan.Decide(lines, 2.1m, "8", 210.01m, true, null).Applied);
    }

    [Fact]
    public void ForDocument_ties_foreign_tax_to_gl_amount()
    {
        // 100.01 USD @36 · ภาษี 3.00 USD ⇒ GL 108.00 · ฐานบาท × อัตรา = 108.01 ⇒ ผูกเข้า GL (ทุกผู้อ่านเห็นชุดเดียวกับ 50 ทวิ)
        var doc = Doc("USD", 36m, null, (100.01m, 3m, 3.00m, "8"));
        Assert.Equal(108.00m, PerLineWht.ForDocument(doc).Values.Single().WhtThb);
        Assert.Equal(108.00m, PerLineWht.GroupsOf(doc)!.Single().WhtThb);
        Assert.Equal(PerLineWht.DocumentWhtThb(3.00m, 36m), DocumentFx.ToBaht(3.00m, 36m));
    }

    [Fact]
    public void GroupsOf_without_lines_is_unknown_not_empty()
    {
        var doc = new Document { Currency = "THB", ExchangeRate = 1m, WithholdingTaxAmount = 30m, DocumentNumber = "X" };
        Assert.Null(PerLineWht.GroupsOf(doc));
    }

    [Theory]
    [InlineData(30, 30, 36, true)]       // ใบ USD ออกเป็นตัวเลขดอลลาร์
    [InlineData(1_080, 30, 36, false)]   // ทิศตรงข้าม: ออกเป็นบาทแล้ว
    [InlineData(15, 30, 36, true)]       // ใบหลายผู้ถูกหัก ครอบครึ่งเดียว (สกุลเอกสาร)
    [InlineData(540, 30, 36, false)]     // ครึ่งเดียวแต่เป็นบาท
    [InlineData(1_000, 1_000, 0.23, true)]  // JPY ออกเป็นเยน
    [InlineData(230, 1_000, 0.23, false)]   // JPY เป็นบาท
    [InlineData(30, 30, 1.1, false)]        // อัตราใกล้ 1 แยกไม่ออก ⇒ ไม่ฟ้อง
    public void Detects_pre_fix_certificates_in_document_currency(double certTax, double expected, double rate, bool flagged)
        => Assert.Equal(flagged, WhtCertCurrencyCheck.LooksDocumentCurrency((decimal)certTax, (decimal)expected, (decimal)rate));

    // ═══ OcrLineWhtPlan — อัตราต่อบรรทัดจากสแกน ═══

    private static OcrLineWhtInput Svc(decimal a, string code) => new(a, OcrLineWhtKind.Withholdable, code);
    private static OcrLineWhtInput Goods(decimal a) => new(a, OcrLineWhtKind.NotWithholdable, null);
    private static OcrLineWhtInput Unknown(decimal a) => new(a, OcrLineWhtKind.Unknown, null);

    [Fact]
    public void Plan_splits_service_advert_and_goods_when_it_explains_header_wht()
    {
        var r = OcrLineWhtPlan.Decide(new[] { Svc(5_000m, "8"), Svc(3_000m, "8ad"), Goods(2_000m) }, 2.1m, "8", 210m, true);
        Assert.True(r.Applied);
        Assert.Equal((3m, "8", 150m), (r.Lines[0].RatePercent, r.Lines[0].IncomeTypeCode, r.Lines[0].WhtAmount));
        Assert.Equal((2m, "8ad", 60m), (r.Lines[1].RatePercent, r.Lines[1].IncomeTypeCode, r.Lines[1].WhtAmount));
        Assert.Equal((0m, (string?)null, 0m), (r.Lines[2].RatePercent, r.Lines[2].IncomeTypeCode, r.Lines[2].WhtAmount));
        Assert.Contains(OcrLineWhtPlan.AppliedTag, r.Note);
    }

    [Fact]
    public void Plan_that_disagrees_with_header_is_not_used()
    {
        // หัวใบหัก 3% ทั้งใบ (300) แต่แผนได้ 210 ⇒ คงแบบเดิม + แท็กให้คนตรวจ (ยอดใบไม่ถูกเปลี่ยนเงียบ)
        var r = OcrLineWhtPlan.Decide(new[] { Svc(5_000m, "8"), Svc(3_000m, "8ad"), Goods(2_000m) }, 3m, "8", 300m, true);
        Assert.False(r.Applied);
        Assert.Contains(OcrLineWhtPlan.MismatchTag, r.Note);
    }

    [Fact]
    public void Unknown_line_keeps_header_rate_and_goods_line_gets_zero()
    {
        var r = OcrLineWhtPlan.Decide(new[] { Unknown(1_000m), Goods(1_000m) }, 3m, "8", 30m, null);
        Assert.True(r.Applied);
        Assert.Equal((3m, "8", 30m), (r.Lines[0].RatePercent, r.Lines[0].IncomeTypeCode, r.Lines[0].WhtAmount));
        Assert.Equal(0m, r.Lines[1].WhtAmount);
    }

    [Fact]
    public void Plan_equal_to_header_everywhere_changes_nothing()
    {
        // ทิศตรงข้าม: ทุกบรรทัดได้คำตอบเท่าหัวใบ ⇒ ไม่ใช้แผน (เฉลี่ยแบบเดิมทุกสตางค์) และไม่มีโน้ต
        var r = OcrLineWhtPlan.Decide(new[] { Svc(1_000m, "8"), Unknown(500m) }, 3m, "8", 45m, true);
        Assert.False(r.Applied);
        Assert.Null(r.Note);
    }

    [Fact]
    public void No_header_wht_means_no_plan()
    {
        Assert.False(OcrLineWhtPlan.Decide(new[] { Svc(5_000m, "8") }, 0m, null, 0m, true).Applied);
        Assert.False(OcrLineWhtPlan.Decide(Array.Empty<OcrLineWhtInput>(), 3m, "8", 30m, true).Applied);
    }

    [Fact]
    public void Small_line_in_a_withheld_document_is_still_withheld()
    {
        // เกณฑ์ 1,000 บาทดูทั้งการจ่าย ไม่ใช่ต่อบรรทัด — ค่าโฆษณา 600 ในใบที่หักอยู่แล้วก็หัก 2%
        var r = OcrLineWhtPlan.Decide(new[] { Svc(5_000m, "8"), Svc(600m, "8ad") }, 2.89m, "8", 162m, true);
        Assert.True(r.Applied);
        Assert.Equal(12m, r.Lines[1].WhtAmount);
    }

    [Fact]
    public void Interest_with_unknown_payee_kind_follows_header()
    {
        // ดอกเบี้ย 40(4)(ก): บุคคล 15% · นิติบุคคล 1% — ไม่รู้ชนิดผู้รับ ⇒ ไม่เดา (ตามหัวใบ)
        var r = OcrLineWhtPlan.Decide(new[] { Svc(1_000m, "4a"), Goods(1_000m) }, 1m, "4a", 10m, null);
        Assert.True(r.Applied);
        Assert.Equal(1m, r.Lines[0].RatePercent);
    }

    [Fact]
    public void Satang_remainder_goes_to_largest_withheld_line()
    {
        // 333.33 × 3% = 10.00 · 333.33 × 2% = 6.67 ⇒ 16.67 · หัวใบพิมพ์ 16.66 ⇒ −0.01 ไปบรรทัดภาษีสูงสุด
        var r = OcrLineWhtPlan.Decide(new[] { Svc(333.33m, "8"), Svc(333.33m, "8ad"), Goods(100m) }, 2.5m, "8", 16.66m, true);
        Assert.True(r.Applied);
        Assert.Equal(9.99m, r.Lines[0].WhtAmount);
        Assert.Equal(6.67m, r.Lines[1].WhtAmount);
        Assert.Equal(16.66m, r.Lines.Sum(l => l.WhtAmount));
    }

    // ═══ ExpenseCategoryResolver.ClassifyLineForWht — จัดประเภทจากคำอธิบายบรรทัดเดียว ═══

    [Fact]
    public void Classifies_single_lines_from_their_own_description()
    {
        Assert.Equal((OcrLineWhtKind.Withholdable, (string?)"8ad"), ExpenseCategoryResolver.ClassifyLineForWht("ค่าโฆษณา Facebook Ads เดือนตุลาคม"));
        Assert.Equal((OcrLineWhtKind.Withholdable, (string?)"8tr"), ExpenseCategoryResolver.ClassifyLineForWht("ค่าบริการขนส่งสินค้า"));
        Assert.Equal((OcrLineWhtKind.Withholdable, (string?)"8"), ExpenseCategoryResolver.ClassifyLineForWht("ค่าบริการออกแบบโลโก้"));
        Assert.Equal((OcrLineWhtKind.NotWithholdable, (string?)null), ExpenseCategoryResolver.ClassifyLineForWht("ปูนซีเมนต์ตราช้าง 50 กก."));
        Assert.Equal((OcrLineWhtKind.Unknown, (string?)null), ExpenseCategoryResolver.ClassifyLineForWht(""));
        Assert.Equal((OcrLineWhtKind.Unknown, (string?)null), ExpenseCategoryResolver.ClassifyLineForWht(null));
    }

    // ═══ ค่าตั้ง + เลเบลเอกสาร ═══

    [Fact]
    public void Setting_defaults_off()
    {
        Assert.False(new CompanySettings().PerLineWhtEnabled);
        Assert.Null(new Document().DocumentThbBase);
        Assert.Null(new Document().ExchangeRateSource);
    }

    [Fact]
    public void Print_label_has_both_languages_without_thai_in_english()
    {
        var th = Accounting.Services.Implementations.Pdf.DocumentLabels.For("th").TotalWhtAtRate(3m);
        var en = Accounting.Services.Implementations.Pdf.DocumentLabels.For("en").TotalWhtAtRate(2.5m);
        Assert.Contains("3%", th);
        Assert.Contains("2.5%", en);
        Assert.DoesNotContain(en, c => c >= '฀' && c <= '๿');
    }

    [Fact]
    public void Withhold_type_lines_have_zero_payer_borne_in_ForDocument()
    {
        var doc = Doc("USD", 36m, null, (1_000m, 3m, 30m, "8"));
        doc.WhtCertType = WithholdingTaxCertType.Withhold;
        var b = PerLineWht.ForDocument(doc).Values.Single();
        Assert.Equal(0m, b.PayerBorneThb);
        Assert.Equal(1_080m, b.WhtThb);
    }

    // ───────────────────────── รอบ 205 W7 — R-18 / คำตัดสินข้อ 162 (Q2) / R-17 / R-20 ─────────────────────────

    [Fact]
    public void CreditNote_foreign_ForDocument_gives_baht_income_and_tax_tied_to_header()
    {
        // R-18: ใบลดหนี้ USD อัตรา 36 — ตัวเลขที่เทียบกับ 50 ทวิ ต้องเป็นบาท (ตัวตั้งเดียว ForDocument / DocumentWhtThb)
        var cn = Doc("USD", 36m, null, (100m, 3m, 3m, "2"));
        var b = PerLineWht.ForDocument(cn).Values.Single();
        Assert.Equal(3_600m, b.IncomeThb);
        Assert.Equal(108m, b.WhtThb);
        Assert.Equal(108m, PerLineWht.DocumentWhtThb(cn.WithholdingTaxAmount, cn.ExchangeRate));
    }

    [Fact]
    public void CreditNote_thb_ForDocument_returns_stored_amounts_exactly()
    {
        // R-18 ทิศตรงข้าม: ใบลดหนี้บาทไม่ขยับแม้แต่สตางค์
        var cn = Doc("THB", 1m, null, (100.55m, 3m, 3.02m, "2"), (49.45m, 0m, 0m, null));
        var b = PerLineWht.ForDocument(cn);
        Assert.Equal(100.55m, b.Values.First().IncomeThb);
        Assert.Equal(3.02m, b.Values.First().WhtThb);
        Assert.Equal(3.02m, PerLineWht.DocumentWhtThb(cn.WithholdingTaxAmount, cn.ExchangeRate));
    }

    [Fact]
    public void ForDocument_whtRateOverride_uses_payment_rate_and_drops_printed_base()
    {
        // ข้อ 162: ฐานภาษีงวดที่อัตราวันจ่าย 37 — ยอดบาทที่พิมพ์ (อัตราวันใบ) ไม่ใช่ฐานอีกต่อไป
        var doc = Doc("USD", 36m, 36_000m, (1_000m, 3m, 30m, "2"));
        var def = PerLineWht.ForDocument(doc).Values.Single();
        Assert.Equal(36_000m, def.BaseThb);
        Assert.Equal(1_080m, def.WhtThb);
        var paid = PerLineWht.ForDocument(doc, 37m).Values.Single();
        Assert.Equal(37_000m, paid.BaseThb);
        Assert.Equal(1_110m, paid.WhtThb);
        Assert.Equal(WhtBahtBasis.ExchangeRate, PerLineWht.BasisOf(doc, 37m));
        // ตัวเดียวกับขา 2191x: DocumentWhtThb ที่อัตราเดียวกัน ⇒ 50 ทวิ = GL ถึงสตางค์
        Assert.Equal(1_110m, PerLineWht.DocumentWhtThb(doc.WithholdingTaxAmount, 37m));
    }

    [Fact]
    public void ForDocument_whtRateOverride_equal_to_document_rate_is_identical_to_default()
    {
        var doc = Doc("USD", 36m, 36_000m, (1_000m, 3m, 30m, "2"), (500m, 2m, 10m, "8ad"));
        var a = PerLineWht.ForDocument(doc).Values.Select(x => (x.BaseThb, x.WhtThb)).ToList();
        var b = PerLineWht.ForDocument(doc, 36m).Values.Select(x => (x.BaseThb, x.WhtThb)).ToList();
        Assert.Equal(a, b);
        Assert.Equal(WhtBahtBasis.PrintedThb, PerLineWht.BasisOf(doc, 36m));
    }

    [Fact]
    public void ForDocument_thb_document_ignores_whtRateOverride()
    {
        // ทิศตรงข้าม: ใบบาทคืนยอดที่เก็บไว้ตรงตัว แม้ผู้เรียกส่ง override มา
        var doc = Doc("THB", 1m, null, (1_000m, 3m, 30m, "2"));
        var b = PerLineWht.ForDocument(doc, 37m).Values.Single();
        Assert.Equal(1_000m, b.BaseThb);
        Assert.Equal(30m, b.WhtThb);
        Assert.Equal(WhtBahtBasis.Thb, PerLineWht.BasisOf(doc, 37m));
    }

    [Theory]
    [InlineData(WhtFxRateBasis.DocumentDate, 36, 37, 36)]     // ค่าตั้งต้น = อัตราเอกสารเสมอ (เดิม)
    [InlineData(WhtFxRateBasis.PaymentDate, 36, 37, 37)]      // วันจ่าย
    [InlineData(WhtFxRateBasis.PaymentDate, 36, null, 36)]    // ไม่มีอัตราวันจ่าย ⇒ อัตราเอกสาร (ไม่แต่ง)
    [InlineData(WhtFxRateBasis.PaymentDate, 36, 0, 36)]
    [InlineData(WhtFxRateBasis.PaymentDate, 1, 37, 1)]        // ใบบาท ⇒ 1 เสมอ
    [InlineData(WhtFxRateBasis.DocumentDate, 0, 37, 1)]       // อัตราเสีย (ข้อมูลเก่า) ⇒ 1 เหมือน DocumentFx.ToBaht
    public void WhtRateFor_follows_company_setting_with_document_rate_default(WhtFxRateBasis basis, int docRate, int? payRate, int expected)
    {
        Assert.Equal((decimal)expected, DocumentFx.WhtRateFor(basis, docRate, payRate is int p ? (decimal)p : null));
    }

    [Fact]
    public void WhtRateBasisLabel_names_the_rate_actually_used()
    {
        Assert.Equal("อัตราของเอกสาร (วันที่ใบ)", DocumentFx.WhtRateBasisLabel(WhtFxRateBasis.DocumentDate, 36m, 37m));
        Assert.Equal("อัตราของเอกสาร (วันที่ใบ)", DocumentFx.WhtRateBasisLabel(WhtFxRateBasis.PaymentDate, 36m, null));
        Assert.Equal("อัตรา ณ วันจ่าย (ป.132/2548)", DocumentFx.WhtRateBasisLabel(WhtFxRateBasis.PaymentDate, 36m, 37m));
    }

    [Theory]
    [InlineData(BotRateType.Mid, 36.5, 36.2, 36.8, 36.5)]
    [InlineData(BotRateType.AverageBuying, 36.5, 36.2, 36.8, 36.2)]
    [InlineData(BotRateType.AverageSelling, 36.5, 36.2, 36.8, 36.8)]
    [InlineData(BotRateType.AverageBuying, 36.5, 0, 36.8, 36.5)]     // ตัวที่เลือกไม่มีค่า ⇒ อัตรากลาง (ไม่คืนศูนย์ให้ GL)
    public void PickBotRate_defaults_to_mid_and_never_returns_zero(BotRateType type, double mid, double buy, double sell, double expected)
    {
        Assert.Equal((decimal)expected, DocumentFx.PickBotRate(type, (decimal)mid, (decimal)buy, (decimal)sell));
    }

    [Fact]
    public void Suggest_header_without_wht_proposes_statutory_rate_per_line_without_applying()
    {
        // R-20: ค่าบริการ 1,000 (ม.40(2) นิติบุคคล 3%) + สินค้า 500 — หัวใบไม่มีหัก · เกณฑ์ 1,000 ผ่าน
        var lines = new List<OcrLineWhtInput>
        {
            new(1_000m, OcrLineWhtKind.Withholdable, "2"),
            new(500m, OcrLineWhtKind.NotWithholdable, null),
        };
        var r = OcrLineWhtPlan.Suggest(lines, 0m, 0m, true, thresholdMet: true);
        Assert.False(r.Applied);
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(3m, r.Lines[0].RatePercent);
        Assert.Equal(30m, r.Lines[0].WhtAmount);
        Assert.Equal(0m, r.Lines[1].WhtAmount);
        Assert.StartsWith(OcrLineWhtPlan.SuggestTag, r.Note);
        Assert.Contains("ไม่หักให้", r.Note);
    }

    [Fact]
    public void Suggest_does_not_fire_when_header_already_withholds_or_threshold_unmet_or_nothing_withholdable()
    {
        var lines = new List<OcrLineWhtInput> { new(1_000m, OcrLineWhtKind.Withholdable, "2") };
        // ทิศตรงข้าม: ใบที่หัวมีหักแล้วไม่ถูกเสนอซ้ำ (เดิน Decide)
        Assert.Null(OcrLineWhtPlan.Suggest(lines, 3m, 30m, true, true).Note);
        Assert.Null(OcrLineWhtPlan.Suggest(lines, 0m, 30m, true, true).Note);
        // เกณฑ์ 1,000 ไม่ผ่าน/ตัดสินไม่ได้ ⇒ ไม่เสนอ
        Assert.Null(OcrLineWhtPlan.Suggest(lines, 0m, 0m, true, false).Note);
        // สินค้าล้วน ⇒ ไม่เสนอ
        Assert.Null(OcrLineWhtPlan.Suggest(new List<OcrLineWhtInput> { new(1_000m, OcrLineWhtKind.NotWithholdable, null) }, 0m, 0m, true, true).Note);
        // ดอกเบี้ย ผู้รับไม่รู้ชนิด ⇒ ไม่เดา 15/1
        Assert.Null(OcrLineWhtPlan.Suggest(new List<OcrLineWhtInput> { new(1_000m, OcrLineWhtKind.Withholdable, "4a") }, 0m, 0m, null, true).Note);
    }

    [Fact]
    public void WithUserChoice_user_pick_wins_over_classification()
    {
        var classified = (OcrLineWhtKind.Unknown, (string?)null);
        Assert.Equal(OcrLineWhtKind.Unknown, OcrLineWhtPlan.WithUserChoice(100m, null, classified).Kind);
        Assert.Equal(OcrLineWhtKind.Unknown, OcrLineWhtPlan.WithUserChoice(100m, "", classified).Kind);
        Assert.Equal(OcrLineWhtKind.NotWithholdable, OcrLineWhtPlan.WithUserChoice(100m, OcrLineWhtPlan.NoWhtOverrideCode, classified).Kind);
        var svc = OcrLineWhtPlan.WithUserChoice(100m, "2", classified);
        Assert.Equal(OcrLineWhtKind.Withholdable, svc.Kind);
        Assert.Equal("2", svc.IncomeTypeCode);
        // รหัสที่ตารางไม่รู้จัก ⇒ ไม่แต่ง (ใช้ผลจัดประเภทของระบบ)
        Assert.Equal(OcrLineWhtKind.Unknown, OcrLineWhtPlan.WithUserChoice(100m, "zz", classified).Kind);
    }

    [Fact]
    public void Decide_with_user_choice_round_trips_and_still_requires_sum_to_match_header()
    {
        // หัวใบ 3% ของ 1,500 = 45 · ระบบตัดสินบรรทัดสองไม่ได้ ⇒ ตามหัวใบ (ไม่ต่าง ⇒ ไม่ใช้แผน)
        var a = OcrLineWhtPlan.WithUserChoice(1_000m, null, (OcrLineWhtKind.Withholdable, "2"));
        var bSys = OcrLineWhtPlan.WithUserChoice(500m, null, (OcrLineWhtKind.Unknown, null));
        Assert.False(OcrLineWhtPlan.Decide(new[] { a, bSys }, 3m, "2", 45m, true, 1m).Applied);
        // ผู้ใช้บอกว่าบรรทัดสองไม่หัก ⇒ Σ 30 ≠ 45 ⇒ ไม่ใช้แผน + แท็กให้คนตรวจ (ไม่แต่งยอดใบ)
        var bNone = OcrLineWhtPlan.WithUserChoice(500m, OcrLineWhtPlan.NoWhtOverrideCode, (OcrLineWhtKind.Unknown, null));
        var mismatch = OcrLineWhtPlan.Decide(new[] { a, bNone }, 3m, "2", 45m, true, 1m);
        Assert.False(mismatch.Applied);
        Assert.StartsWith(OcrLineWhtPlan.MismatchTag, mismatch.Note);
        // ผู้ใช้บอกว่าบรรทัดสองเป็นค่าโฆษณา 2% ⇒ 30 + 10 = 40 · หัวใบ 40 ⇒ ใช้แผน
        var bAd = OcrLineWhtPlan.WithUserChoice(500m, "8ad", (OcrLineWhtKind.Unknown, null));
        var ok = OcrLineWhtPlan.Decide(new[] { a, bAd }, 3m, "2", 40m, true, 1m);
        Assert.True(ok.Applied);
        Assert.Equal(2m, ok.Lines[1].RatePercent);
        Assert.Equal(10m, ok.Lines[1].WhtAmount);
    }
}
