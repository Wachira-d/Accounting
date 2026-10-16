using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านของ 7c782d24..afbb0956 (รหัส ภ.พ. รายบรรทัด · 2026-10-16) — ทุกข้อเทสต์สองทิศ (CLAUDE.md §H) · ข้อที่ชี้ "→ ตารางชนะ" ใช้คำตัดสินเจ้าของ
/// DECISIONS ข้อ 153 ข้อ 1 (<see cref="OcrVatCodeLines.Decide"/> คืน <see cref="OcrVatCodeLineSource.TableSummary"/>)
/// </summary>
public class OcrReviewAfbb0956Tests
{
    private static string Norm(string s) => ThaiTextNormalizer.Normalize(s);
    private static string MakroFull => Norm(OcrPaperSamples.MakroPage1of2Synthetic + "\n" + OcrPaperSamples.MakroPage2of2);
    private static readonly decimal[] MakroItems = { 1000.00m, 248.00m, 1134.00m, 2590.00m, 1180.00m, 960.00m, 4096.00m };

    private static IReadOnlyList<OcrVatCodeLineInput> In(decimal[] amounts, decimal?[]? preset = null)
        => amounts.Select((a, i) => new OcrVatCodeLineInput(a, preset?[i])).ToList();

    private const string Legend = "รหัส ภ.พ. 1=สินค้าได้รับการยกเว้นภาษีมูลค่าเพิ่ม 2=สินค้าที่ต้องเสียภาษีมูลค่าเพิ่ม\n";

    // ── P1-1: ส่วนลดต่อกลุ่มที่ไม่มีหลักฐาน · รหัสบนแถวต้องชนะรหัส-ที่-เป็น-อัตราของ engine ──

    /// <summary>กระดาษของฝ่ายค้าน: "1" บนแถวน้ำปลาคือจำนวน (ไม่มีคอลัมน์จำนวนแยก) · ห้างให้ส่วนลด 150 ทั้งหมดกับกลุ่ม 2 (950 = 100 + 1,000 − 150)</summary>
    private const string ReviewerP11 =
        "ข้าวสาร 500.00 1 500.00\n" +
        "น้ำปลา 100.00 1 100.00\n" +
        "ทิชชู่ 1,000.00 2 1,000.00\n" +
        "ส่วนลด 150.00\n" +
        "จำนวนชิ้น รหัส ภ.พ. ราคาสินค้า ภาษีมูลค่าเพิ่ม รวม\n" +
        "1 1 500.00 0.00 500.00\n" +
        "2 2 887.85 62.15 950.00\n" +
        "รวม 1,387.85 62.15 1,450.00\n" + Legend;

    [Fact]
    public void P1_1_กระดาษฝ่ายค้าน_เดิมApplied_น้ำปลายกเว้น_ส่วนลด100และ50ที่แต่งขึ้น_ตอนนี้ไม่ใช้แผน_ตารางชนะ()
    {
        var t = OcrLineVatMarks.ReadGroups(ReviewerP11);
        // ก่อนแก้: รหัส "1" ของน้ำปลา (คือจำนวน) ถูกอ่านเป็นรหัส ⇒ Applied ยกเว้น 600 − ส่วนลด 100 · กลุ่ม 2 ลด 50 ทั้งที่กระดาษให้ 150 กับกลุ่ม 2
        var plan = OcrVatCodeLines.Plan(In(new[] { 500.00m, 100.00m, 1000.00m }), ReviewerP11, t, 62.15m, 1450.00m, 150.00m, true, 1450.00m);
        Assert.Equal(OcrVatCodeVerdict.Unreconciled, plan.Verdict);
        Assert.Null(plan.AmountsAfterDiscount);
        var d = OcrVatCodeLines.Decide(In(new[] { 500.00m, 100.00m, 1000.00m }), ReviewerP11, t, 1387.85m, 62.15m, 1450.00m, 150.00m, false, true);
        Assert.Equal(OcrVatCodeLineSource.TableSummary, d.Source);
        Assert.Equal(new[] { (OcrVatGroupKind.Exempt, 500.00m, 0.00m), (OcrVatGroupKind.Standard7, 887.85m, 62.15m) },
            d.TableLines.Select(g => (g.Kind, g.Net, g.Vat)).ToArray());
        Assert.Equal(1450.00m, d.TableLines.Sum(g => g.Net + g.Vat));
    }

    [Fact]
    public void P1_1_Makroจริง_engineให้รหัสเป็นอัตรา_2_1_1_2_2_2_2_รหัสที่พิมพ์บนแถวชนะ()
    {
        var t = OcrLineVatMarks.ReadGroups(MakroFull);
        var plan = OcrVatCodeLines.Plan(In(MakroItems, new decimal?[] { 2m, 1m, 1m, 2m, 2m, 2m, 2m }), MakroFull, t,
            638.50m, 10980.00m, 228.00m, true, 10980.00m);
        // ก่อนแก้: ข้าวสาร 7% · น้ำยาล้างจานยกเว้น (ค่าของ engine ชนะ) — ส่วนลดต่อกลุ่มแต่งให้ลงตารางเอง
        Assert.Equal(OcrVatCodeVerdict.Applied, plan.Verdict);
        Assert.Equal(new decimal?[] { -1m, -1m, 7m, 7m, 7m, 7m, 7m }, plan.Rates);
        Assert.All(plan.ReplaceEngineRate, Assert.True);
        Assert.Equal(1220.00m, plan.AmountsAfterDiscount!.Take(2).Sum());
        Assert.Contains("รหัสบนแถวชนะค่าของ engine 2 บรรทัด", plan.Note);
    }

    [Fact]
    public void P1_1_ทิศตรงข้าม_engineให้รหัสตรงกับที่พิมพ์_เท่าเดิม_และอัตราไทยที่ขัดกับรหัสที่พิมพ์_ไม่เลือกข้างเงียบ()
    {
        var t = OcrLineVatMarks.ReadGroups(MakroFull);
        var same = OcrVatCodeLines.Plan(In(MakroItems, new decimal?[] { 1m, 1m, 2m, 2m, 2m, 2m, 2m }), MakroFull, t,
            638.50m, 10980.00m, 228.00m, true, 10980.00m);
        Assert.Equal(OcrVatCodeVerdict.Applied, same.Verdict);
        Assert.DoesNotContain("ชนะค่าของ engine", same.Note);
        Assert.Equal(new[] { 977.56m, 242.44m }, same.AmountsAfterDiscount!.Take(2).ToArray());
        // ผู้ใช้/engine ตั้งไข่ไก่ 7% (อัตราไทยจริง) แต่กระดาษพิมพ์รหัส 1 ⇒ กระทบไม่ได้ (Unreconciled → ตารางชนะใน Decide)
        var conflict = OcrVatCodeLines.Plan(In(MakroItems, new decimal?[] { null, 7m, null, null, null, null, null }), MakroFull, t,
            638.50m, 10980.00m, 228.00m, true, 10980.00m);
        Assert.Equal(OcrVatCodeVerdict.Unreconciled, conflict.Verdict);
        Assert.Contains("รหัส 1 ที่พิมพ์บนแถว", conflict.Note);
    }

    // ── P2-1: จำนวนถูกอ่านเป็นรหัส ──

    [Theory]
    [InlineData("ITEM 2  59.00  2  118.00")]
    [InlineData("ลำดับ 2 ส่วนลด 10.00 1")]
    [InlineData("ค่าขนส่ง 100.00 1")]
    [InlineData("ไข่ไก่ 53.50 2 107.00\nน้ำปลา 107.00 1 107.00")]
    public void P2_1_จำนวนหรือลำดับไม่ใช่รหัส(string rows)
        => Assert.Empty(OcrVatCodeLines.ReadRowCodes(rows, new[] { "1", "2" }));

    [Fact]
    public void P2_1_สลับแถว_ไข่ไก่ราคา53_50จำนวน2_ไม่ถูกตีเป็นรหัส2_แบ่งได้สองทาง_ตารางชนะ()
    {
        const string paper =
            "ไข่ไก่ 53.50 2 107.00\n" +
            "น้ำปลา 107.00 1 107.00\n" +
            "จำนวนชิ้น รหัส ภ.พ. ราคาสินค้า ภาษีมูลค่าเพิ่ม รวม\n" +
            "2 1 107.00 0.00 107.00\n" +
            "1 2 100.00 7.00 107.00\n" +
            "รวม 207.00 7.00 214.00\n" + Legend;
        var t = OcrLineVatMarks.ReadGroups(paper);
        // ก่อนแก้: Applied โดยสลับ — ไข่ไก่ 7% (รหัส "2" คือจำนวน) · น้ำปลายกเว้น
        var d = OcrVatCodeLines.Decide(In(new[] { 107.00m, 107.00m }), paper, t, 207.00m, 7.00m, 214.00m, 0m, false, true);
        Assert.Equal(OcrVatCodeVerdict.Unreconciled, d.Plan.Verdict);
        Assert.Contains("มากกว่าหนึ่งทาง", d.Plan.Note);
        Assert.Equal(OcrVatCodeLineSource.TableSummary, d.Source);
        Assert.Equal(214.00m, d.TableLines.Sum(g => g.Net + g.Vat));
    }

    [Fact]
    public void P2_1_ทิศตรงข้าม_Makroมีคอลัมน์จำนวนแยก_รหัสทุกแถวยังอ่านได้_และแถวรหัส1ราคาเท่ายอดรับเพราะคอลัมน์ถูกพิสูจน์()
    {
        var marks = OcrVatCodeLines.ReadRowCodes(MakroFull, new[] { "1", "2" });
        Assert.Equal(new[] { "1", "1", "2", "2", "2", "2", "2" }, marks.Select(m => m.Code).ToArray());
        // หน้า 1 ที่มีแต่แถวรหัส 1 (ราคา × 1 = ยอด — "อาจเป็นจำนวน") ไม่มีแถวพิสูจน์คอลัมน์ ⇒ ไม่รับ (ตกทางแบ่งด้วยผลรวม/ตารางชนะ)
        Assert.Empty(OcrVatCodeLines.ReadRowCodes("1 120345 ข้าวสาร 1 EA 1,000.00 1 1,000.00\n2 210778 ไข่ไก่ 1 EA 248.00 1 248.00", new[] { "1", "2" }));
    }

    // ── P2-2: ส่วนลดบรรทัดสุดท้ายติดลบ ──

    [Fact]
    public void P2_2_หกบรรทัด10บาท_ส่วนลด3สตางค์_ไม่มีบรรทัดติดลบ_รวมเท่าส่วนลด()
    {
        var shares = OcrVatCodeLines.SplitProportional(0.03m, Enumerable.Repeat(10.00m, 6).ToList());
        Assert.All(shares, s => Assert.True(s >= 0m));                   // เดิม: 0.01 × 5 แล้วตัวสุดท้าย −0.02
        Assert.Equal(0.03m, shares.Sum());
        Assert.Equal(new[] { 0.01m, 0.01m, 0.01m, 0m, 0m, 0m }, shares);
    }

    [Fact]
    public void P2_2_ทิศตรงข้าม_Makroส่วนลดต่อกลุ่มยังเท่าเดิมทุกสตางค์()
    {
        Assert.Equal(new[] { 22.44m, 5.56m }, OcrVatCodeLines.SplitProportional(28.00m, new[] { 1000.00m, 248.00m }));
        Assert.Equal(new[] { 22.77m, 52.01m, 23.69m, 19.28m, 82.25m },
            OcrVatCodeLines.SplitProportional(200.00m, new[] { 1134.00m, 2590.00m, 1180.00m, 960.00m, 4096.00m }));
    }

    // ── P2-3: หาทางแบ่งตรงสตางค์แต่ตรวจแบบมีค่าเผื่อ ──

    private const string MixedNoDiscount =
        "จำนวนชิ้น รหัส ภ.พ. ราคาสินค้า ภาษีมูลค่าเพิ่ม รวม\n" +
        "1 1 500.00 0.00 500.00\n" +
        "3 2 1,000.00 70.00 1,070.00\n" +
        "รวม 1,500.00 70.00 1,570.00\n" + Legend;

    [Fact]
    public void P2_3_OCRอ่าน500_01_และมีชุดอื่นรวมได้500พอดี_สองทางภายใต้ค่าเผื่อ_ไม่เดา()
    {
        var t = OcrLineVatMarks.ReadGroups(MixedNoDiscount);
        // ก่อนแก้: {200 + 300} = 500.00 พอดีถูกเลือกเป็นยกเว้น ทั้งที่บรรทัด 500.01 (จริง 500.00) คือสินค้ายกเว้น
        var plan = OcrVatCodeLines.Plan(In(new[] { 500.01m, 200.00m, 300.00m, 569.99m }), MixedNoDiscount, t, 70.00m, 1570.00m, 0m, true, null);
        Assert.Equal(OcrVatCodeVerdict.Unreconciled, plan.Verdict);
        Assert.Contains("มากกว่าหนึ่งทาง", plan.Note);
    }

    [Fact]
    public void P2_3_ทิศตรงข้าม_ทางเดียวภายใต้ค่าเผื่อ_ยังใช้ได้()
    {
        var t = OcrLineVatMarks.ReadGroups(MixedNoDiscount);
        var plan = OcrVatCodeLines.Plan(In(new[] { 500.01m, 300.00m, 769.99m }), MixedNoDiscount, t, 70.00m, 1570.00m, 0m, true, null);
        Assert.Equal(OcrVatCodeVerdict.Applied, plan.Verdict);
        Assert.Equal(new decimal?[] { -1m, 7m, 7m }, plan.Rates);
    }

    // ── P2-4: [Σ-GAP] ตารางรหัสค้าง · ตารางตัวอักษรไม่มีคำอธิบายฟ้องทุกสแกน ──

    private const string LetterNoLegend =
        "สินค้า A 1,000.00 V\n" +
        "สินค้า B 500.00 N\n" +
        "V 7 1,000.00 70.00 1,070.00\n" +
        "N 0 500.00 0.00 500.00\n";

    [Fact]
    public void P2_4_ตารางตัวอักษรไม่มีคำอธิบาย_บรรทัดตรงเงิน_ไม่ฟ้อง()
    {
        var t = OcrLineVatMarks.ReadGroups(LetterNoLegend);
        Assert.True(OcrVatCodeLines.IsMixed(t));
        Assert.False(t.AllKnown);                                         // N 0 = ยกเว้นหรือ 0% ไม่รู้
        // เดิม: ฟ้อง "ตารางมีรหัสที่กระดาษไม่บอกความหมาย" ทุกสแกน แม้บรรทัดตรง
        Assert.Null(OcrVatCodeLines.TableMismatch(new[]
            { new OcrPlannedLine(1000m, 7m, 70m), new OcrPlannedLine(500m, ThaiVatTypeRule.ExemptRate, 0m) }, t));
        Assert.Null(OcrVatCodeLines.TableMismatch(new[]
            { new OcrPlannedLine(1000m, 7m, 70m), new OcrPlannedLine(500m, 0m, 0m) }, t));
    }

    [Fact]
    public void P2_4_ทิศตรงข้าม_ตารางตัวอักษร_บรรทัดผิดกลุ่ม_ยังฟ้องพร้อมตัวเลข()
    {
        var t = OcrLineVatMarks.ReadGroups(LetterNoLegend);
        var gap = OcrVatCodeLines.TableMismatch(new[] { new OcrPlannedLine(1401.87m, 7m, 98.13m) }, t);
        Assert.NotNull(gap);
        Assert.Contains("ไม่มี VAT (ยกเว้น/0%) เอกสาร 0.00 · กระดาษ 500.00", gap);
    }

    [Fact]
    public void P2_4_หมายเหตุตอนสร้างถูกแทนด้วยผลสด_แก้แล้วหาย_ยังผิดได้ตัวเลขตอนนี้_ตรวจสดไม่ได้คงเดิม()
    {
        var stale = $"[Σ] อื่น\n{OcrLineBuildNotes.GapTag} {OcrVatCodeLines.MismatchLead}: ยกเว้น VAT เอกสาร 0.00 · กระดาษ 500.00\n{OcrLineBuildNotes.GapTag} ข้ออื่น";
        var fixedNow = OcrVatCodeLines.LiveNotes(stale, null, checkedLive: true)!;
        Assert.DoesNotContain(OcrVatCodeLines.MismatchLead, fixedNow);
        Assert.Contains("ข้ออื่น", fixedNow);                              // [Σ-GAP] ข้ออื่นคงทุกตัวอักษร
        Assert.False(OcrPostingReadiness.Evaluate(fixedNow, true).CanAutoApprove);   // ข้ออื่นยังหยุด
        Assert.True(OcrPostingReadiness.Evaluate(OcrVatCodeLines.LiveNotes(
            $"{OcrLineBuildNotes.GapTag} {OcrVatCodeLines.MismatchLead}: x", null, true), true).CanAutoApprove);
        var stillWrong = OcrVatCodeLines.LiveNotes(stale, $"{OcrVatCodeLines.MismatchLead}: ตอนนี้ 100", checkedLive: true)!;
        Assert.Contains("ตอนนี้ 100", stillWrong);
        Assert.DoesNotContain("เอกสาร 0.00 · กระดาษ 500.00", stillWrong);
        Assert.Equal(stale, OcrVatCodeLines.LiveNotes(stale, null, checkedLive: false));
    }

    // ── P2-5: "ตัดสินไม่ได้" ≠ "ไม่เกิน" ──

    [Fact]
    public void P2_5_ผลสามค่า_เกิน_ไม่เกิน_ตัดสินไม่ได้()
    {
        var text = Norm(OcrPaperSamples.MakroPage2of2);
        Assert.True(OcrTotalAnchor.OverstatedNow(text, 11846.50m, 11846.50m));
        Assert.False(OcrTotalAnchor.OverstatedNow(text, 11846.50m, 10980.00m));
        Assert.Null(OcrTotalAnchor.OverstatedNow("ใบเสร็จ ไม่มีตัวเลขยืนยันยอด", 500m, 600m));   // กระดาษไม่ยืนยันยอด
        Assert.Null(OcrTotalAnchor.OverstatedNow(text, 11846.50m, 0m));                             // เอกสารไม่มียอด
    }

    [Fact]
    public void P2_5_ตัดสินไม่ได้_หมายเหตุเกินกระดาษเดิมยังหยุดการอนุมัติเอง_ตรวจแล้วไม่เกินจึงปลด()
    {
        var notes = $"{OcrLineBuildNotes.GapTag} {OcrTotalAnchor.OverstatementLead}: ยอดเอกสาร 11,846.50 สูงกว่ายอดชำระบนกระดาษ 10,980.00";
        Assert.False(OcrPostingReadiness.Evaluate(notes, true, overstatedNow: null).CanAutoApprove);   // เดิม LINE ส่ง false ⇒ ปลด
        Assert.False(OcrPostingReadiness.Evaluate(notes, true, overstatedNow: true).CanAutoApprove);
        Assert.True(OcrPostingReadiness.Evaluate(notes, true, overstatedNow: false).CanAutoApprove);
    }

    // ── minor: "CANON incl. VAT" ──

    [Fact]
    public void Minor_CANON_incl_VAT_ไม่ใช่คำปฏิเสธ_ยังเป็นยอดรวมVAT_ทิศตรงข้าม_not_incl_ยังเป็นปฏิเสธ()
    {
        Assert.True(OcrPaperAmounts.IsVatInclusiveOnLine("CANON incl. VAT 1,070.00", 1070.00m));   // เดิม "CANON" มี "non" ⇒ ปฏิเสธ
        Assert.False(OcrPaperAmounts.IsVatInclusiveOnLine("Amount not incl. VAT 1,000.00", 1000.00m));
        Assert.False(OcrPaperAmounts.IsVatInclusiveOnLine("non incl. VAT 1,000.00", 1000.00m));
    }
}
