using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// 2026-10-16 — คำสั่งเจ้าของ: "ถ้า OCR อ่านรายการสินค้ามาได้ด้วย อัตรา VAT ต่อบรรทัดยังเดาจากชื่อสินค้า ยังไม่ได้ยึดตารางรหัส ภ.พ. … ควรต้องตามตาราง ภพ"
/// ตัวตัดสิน <see cref="OcrVatCodeLines"/> — สองทิศตาม CLAUDE.md §H:
/// <list type="bullet">
/// <item>ใบผสมยกเว้น/7% ที่พิมพ์รหัส ภ.พ. → อัตรารายบรรทัดถูก + ส่วนลดแบ่งต่อรหัสให้ Σ ต่อกลุ่ม = ตาราง</item>
/// <item>ตารางกระทบกับบรรทัดไม่ได้ (แถวขาด/ส่วนลดเป็นแถว/รหัสไม่รู้ความหมาย/แบ่งได้หลายทาง) → ไม่แตะ และด่านผลสุดท้ายฟ้อง ไม่ลงเงียบ</item>
/// <item>ใบที่ไม่มีตารางรหัส ภ.พ. ผสม → NotApplicable (คำตอบเดิม · snapshot ทั้ง Corpus ล็อกใน <see cref="OcrReplayLineGoldenTests"/>)</item>
/// </list>
/// <para>กระดาษจริงที่มี: Makro หน้า 2/2 (ข้อความถอดจากภาพ + ข้อความ Tesseract 5.3.4 จริงของภาพเดียวกัน) — หน้านี้มีแต่แถวส่วนลด ·
/// แถวสินค้าที่พิมพ์รหัส ภ.พ. มาจาก <see cref="OcrPaperSamples.MakroPage1of2Synthetic"/> ซึ่ง<b>สังเคราะห์</b> (ไม่มีหน้า 1 ของจริง)</para>
/// </summary>
public class OcrVatCodeLinesTests
{
    private static string MakroFull => Norm(OcrPaperSamples.MakroPage1of2Synthetic + "\n" + OcrPaperSamples.MakroPage2of2);
    private static string Norm(string s) => ThaiTextNormalizer.Normalize(s);
    private static readonly decimal[] MakroItems = { 1000.00m, 248.00m, 1134.00m, 2590.00m, 1180.00m, 960.00m, 4096.00m };

    private static IReadOnlyList<OcrVatCodeLineInput> In(decimal[] amounts, decimal?[]? preset = null)
        => amounts.Select((a, i) => new OcrVatCodeLineInput(a, preset?[i])).ToList();

    [Fact]
    public void ตารางMakroจริง_อ่านได้สองกลุ่มรู้ความหมาย_ผสม()
    {
        var t = OcrLineVatMarks.ReadGroups(MakroFull);
        Assert.True(OcrVatCodeLines.IsMixed(t));
        Assert.True(t.AllKnown);
        Assert.Equal(10980.00m, t.Gross);
    }

    [Fact]
    public void อ่านรหัสบนแถวสินค้า_คอลัมน์ราคาต่อหน่วย_รหัส_ยอด()
    {
        var marks = OcrVatCodeLines.ReadRowCodes(MakroFull, new[] { "1", "2" });
        Assert.Equal(new[] { "1", "1", "2", "2", "2", "2", "2" }, marks.Select(m => m.Code).ToArray());
        Assert.Equal(MakroItems, marks.Select(m => m.Amount).ToArray());
    }

    [Fact]
    public void Makroหน้า1และ2_ไข่ไก่ยกเว้นตามรหัส1_ส่วนลดแบ่งต่อรหัส_ยกเว้นรวม1220_มีVAT9760()
    {
        var t = OcrLineVatMarks.ReadGroups(MakroFull);
        var plan = OcrVatCodeLines.Plan(In(MakroItems), MakroFull, t, 638.50m, 10980.00m, 228.00m,
            reconPricesIncludeVat: true, reconTargetLineSum: 10980.00m);

        Assert.Equal(OcrVatCodeVerdict.Applied, plan.Verdict);
        // ก่อนแก้: ไข่ไก่ = 7% (ตัวเดาจากชื่อ) ⇒ [Σ-GAP] VatRateMismatch
        Assert.Equal(new decimal?[] { -1m, -1m, 7m, 7m, 7m, 7m, 7m }, plan.Rates);
        Assert.True(plan.PricesIncludeVat);
        var after = plan.AmountsAfterDiscount!;
        Assert.Equal(1220.00m, after[0] + after[1]);                 // ส่วนลดกลุ่มยกเว้น 28 (ไม่ใช่ 2.03% ทั้งใบ)
        Assert.Equal(9760.00m, after.Skip(2).Sum());                 // ส่วนลดกลุ่ม VAT 200
        Assert.Equal(new[] { 977.56m, 242.44m }, after.Take(2).ToArray());
        Assert.Equal(2.24m, plan.DiscountPercents![0]);
        Assert.Equal(2.01m, plan.DiscountPercents[2]);
        Assert.Contains("รหัสที่พิมพ์บนแถวสินค้า 7 บรรทัด", plan.Note);
    }

    [Fact]
    public void engineส่งรหัสภพมาเป็นอัตรา1และ2_แทนด้วยอัตราตามรหัส_อัตราไทยจริงไม่ถูกแตะ()
    {
        var t = OcrLineVatMarks.ReadGroups(MakroFull);
        var plan = OcrVatCodeLines.Plan(In(MakroItems, new decimal?[] { 1m, 1m, 2m, 2m, 2m, 2m, 7m }),
            Norm(OcrPaperSamples.MakroPage2of2), t, 638.50m, 10980.00m, 228.00m, true, 10980.00m);
        Assert.Equal(OcrVatCodeVerdict.Applied, plan.Verdict);
        Assert.Equal(new[] { true, true, true, true, true, true, false }, plan.ReplaceEngineRate);
        Assert.Equal(new decimal?[] { -1m, -1m, 7m, 7m, 7m, 7m, null }, plan.Rates);   // 7 จริงของ engine ไม่ถูกเขียนทับ
        Assert.Contains("ไม่ใช่ %", plan.Note);
    }

    // ── ไม่มีรหัสรายบรรทัด: ผลรวมของตารางบังคับ "ทางเดียว" ──

    private const string MixedTableNoDiscount =
        "ใบเสร็จรับเงิน/ใบกำกับภาษี\n" +
        "ไข่ไก่ เบอร์ 2 แผง 30 ฟอง 500.00\n" +
        "น้ำยาล้างจาน 300.00\n" +
        "กระดาษทิชชู่ 770.00\n" +
        "จำนวนชิ้น รหัส ภ.พ. ราคาสินค้า ภาษีมูลค่าเพิ่ม รวม\n" +
        "1 1 500.00 0.00 500.00\n" +
        "2 2 1,000.00 70.00 1,070.00\n" +
        "รวม 1,500.00 70.00 1,570.00\n" +
        "รหัส ภ.พ. 1=สินค้าได้รับการยกเว้นภาษีมูลค่าเพิ่ม 2=สินค้าที่ต้องเสียภาษีมูลค่าเพิ่ม\n" +
        "จำนวนเงินรวมทั้งสิ้น 1,570.00";

    [Fact]
    public void ไม่มีรหัสรายบรรทัด_ไม่มีส่วนลด_แบ่งได้ทางเดียว_ใช้ตาราง()
    {
        var t = OcrLineVatMarks.ReadGroups(MixedTableNoDiscount);
        var plan = OcrVatCodeLines.Plan(In(new[] { 500.00m, 300.00m, 770.00m }), MixedTableNoDiscount, t,
            70.00m, 1570.00m, 0m, true, null);
        Assert.Equal(OcrVatCodeVerdict.Applied, plan.Verdict);
        Assert.Equal(new decimal?[] { -1m, 7m, 7m }, plan.Rates);
        Assert.Null(plan.AmountsAfterDiscount);
        Assert.Contains("ทางเดียว", plan.Note);
    }

    [Fact]
    public void ทิศตรงข้าม_แบ่งได้สองทาง_ไม่เดา_และด่านผลสุดท้ายฟ้องเมื่ออัตราที่เดาผิด()
    {
        var t = OcrLineVatMarks.ReadGroups(MixedTableNoDiscount);
        var plan = OcrVatCodeLines.Plan(In(new[] { 500.00m, 500.00m, 570.00m }), MixedTableNoDiscount, t,
            70.00m, 1570.00m, 0m, true, null);
        Assert.Equal(OcrVatCodeVerdict.Unreconciled, plan.Verdict);
        Assert.Contains("มากกว่าหนึ่งทาง", plan.Note);
        Assert.All(plan.Rates, r => Assert.Null(r));
        // ตัวเดาจากชื่อตั้งทั้งสามบรรทัด 7% ⇒ กลุ่มยกเว้น 0 ≠ 500 บนกระดาษ ⇒ ฟ้อง (ไม่ลงเงียบ)
        var gap = OcrVatCodeLines.TableMismatch(new[]
        {
            new OcrPlannedLine(467.29m, 7m, 32.71m), new OcrPlannedLine(467.29m, 7m, 32.71m), new OcrPlannedLine(535.42m, 7m, 4.58m),
        }, t);
        Assert.NotNull(gap);
        Assert.StartsWith(OcrVatCodeLines.MismatchLead, gap);
        Assert.Contains("ยกเว้น VAT เอกสาร 0.00 · กระดาษ 500.00", gap);
    }

    [Fact]
    public void ทิศตรงข้าม_อ่านแถวขาด_Σไม่เท่าตาราง_ไม่แตะ()
    {
        var t = OcrLineVatMarks.ReadGroups(MixedTableNoDiscount);
        var plan = OcrVatCodeLines.Plan(In(new[] { 500.00m, 770.00m }), MixedTableNoDiscount, t, 70.00m, 1570.00m, 0m, false, null);
        Assert.Equal(OcrVatCodeVerdict.Unreconciled, plan.Verdict);
        Assert.Contains("ไม่เท่าตาราง", plan.Note);
    }

    [Fact]
    public void ทิศตรงข้าม_มีส่วนลดแต่แถวสินค้าไม่มีรหัส_แบ่งส่วนลดตามรหัสไม่ได้_ไม่แตะ()
    {
        var text = Norm(OcrPaperSamples.MakroPage2of2);   // หน้า 2 จริง — ไม่มีแถวสินค้า (รหัสอยู่หน้า 1)
        var plan = OcrVatCodeLines.Plan(In(MakroItems), text, OcrLineVatMarks.ReadGroups(text), 638.50m, 10980.00m, 228.00m, true, 10980.00m);
        Assert.Equal(OcrVatCodeVerdict.Unreconciled, plan.Verdict);
        Assert.Contains("แบ่งส่วนลดตามรหัสไม่ได้", plan.Note);
        Assert.Null(plan.AmountsAfterDiscount);
    }

    [Fact]
    public void กระดาษจริงหน้า2_engineคืนแถวส่วนลดเป็นสินค้า_ไม่แตะ()
    {
        var text = Norm(OcrPaperSamples.MakroPage2of2);
        var plan = OcrVatCodeLines.Plan(In(new[] { 30.00m, 60.00m, 10.00m }), text, OcrLineVatMarks.ReadGroups(text),
            638.50m, 10980.00m, 228.00m, true, null);
        Assert.Equal(OcrVatCodeVerdict.Unreconciled, plan.Verdict);
    }

    [Fact]
    public void ข้อความTesseractจริง_คำอธิบายรหัส1อ่านเพี้ยน_รหัสไม่รู้ความหมาย_ไม่เดา_และด่านผลสุดท้ายฟ้อง()
    {
        var text = Norm(OcrMakroPage2VatAddedTwiceTests.MakroPage2of2Tesseract);
        var t = OcrLineVatMarks.ReadGroups(text);
        Assert.True(OcrVatCodeLines.IsMixed(t));
        Assert.False(t.AllKnown);
        var plan = OcrVatCodeLines.Plan(In(new[] { 1220.00m, 9760.00m }), text, t, 638.50m, 10980.00m, 0m, true, null);
        Assert.Equal(OcrVatCodeVerdict.Unreconciled, plan.Verdict);
        Assert.Contains("ไม่บอกความหมาย", plan.Note);
        var gap = OcrVatCodeLines.TableMismatch(new[] { new OcrPlannedLine(1220.00m, -1m, 0m), new OcrPlannedLine(9121.50m, 7m, 638.50m) }, t);
        Assert.NotNull(gap);
        Assert.Contains("ไม่บอกความหมาย", gap);
    }

    [Fact]
    public void ด่านผลสุดท้าย_บรรทัดตรงตาราง_ไม่ฟ้อง_และใบที่ไม่มีตารางผสม_ไม่พูดอะไร()
    {
        var t = OcrLineVatMarks.ReadGroups(MakroFull);
        Assert.Null(OcrVatCodeLines.TableMismatch(new[]
        {
            new OcrPlannedLine(977.56m, -1m, 0m), new OcrPlannedLine(242.44m, -1m, 0m), new OcrPlannedLine(9121.50m, 7m, 638.50m),
        }, t));
        // Wine Pro: ตาราง "V 7 …" กลุ่มเดียว 7% ⇒ ไม่ผสม · ใบค้าส่ง V/N ไม่มีตารางกลุ่ม ⇒ ไม่มีตาราง
        foreach (var paper in new[] { OcrPaperSamples.WinePro, OcrPaperSamples.WholesaleMixedVat, OcrPaperSamples.HardwareBillDiscount })
        {
            var tt = OcrLineVatMarks.ReadGroups(paper);
            Assert.False(OcrVatCodeLines.IsMixed(tt));
            Assert.Equal(OcrVatCodeVerdict.NotApplicable,
                OcrVatCodeLines.Plan(In(new[] { 100m }), paper, tt, 7m, 107m, 0m, false, null).Verdict);
            Assert.Null(OcrVatCodeLines.TableMismatch(new[] { new OcrPlannedLine(100m, 7m, 7m) }, tt));
        }
    }

    [Fact]
    public void รหัสบนแถวต้องอยู่ในตารางของกระดาษ_ราคาจำนวนยอดไม่ถูกอ่านเป็นรหัส()
    {
        // "35.00 3 105.00" = ราคา · จำนวน · ยอด — 3 ไม่ใช่รหัสบนตาราง (1/2) ⇒ ไม่ใช่รหัส
        Assert.Empty(OcrVatCodeLines.ReadRowCodes("น้ำปลา 700 มล. 35.00 3 105.00", new[] { "1", "2" }));
        // แถวตารางสรุปเองไม่ใช่แถวสินค้า
        Assert.Empty(OcrVatCodeLines.ReadRowCodes("59 2 9,121.50 638.50 9,760.00\nรวม 10,341.50 638.50 10,980.00", new[] { "1", "2" }));
    }
}
