using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **รอบ 242 OCR — ตาราง ภ.พ. แบบอัตรานำหน้า (ไม่มีรหัส) ของใบเสร็จซูเปอร์มาร์เก็ต** (รายงานเจ้าของ 2026-11-06)
///
/// <para>ก่อนแก้: engine คืนรายการ 5 จาก 21 แถว (Σ 301.45 · ทุกบรรทัด 7%) และใส่ยอดรวมทั้งสิ้น 1,961.25 ในช่องยอดก่อน VAT ⇒ [Σ-GAP]
/// "Σ บรรทัด 301.45 น้อยกว่ายอดก่อน VAT 1,961.25 — OCR อาจอ่านบรรทัดขาด 1,659.80" ทั้งที่กระดาษพิมพ์ตาราง ภ.พ. ซ้ายล่าง
/// <c>0.00% 1,502.25 0.00 1,502.25</c> / <c>7.00% 428.97 30.03 1,961.25</c> ซึ่งปิดยอดพอดี · สาเหตุ: <see cref="OcrLineVatMarks.ReadGroups"/>
/// รับแต่แถวที่มีรหัส (ตัวอักษร + อัตรา · จำนวนชิ้น + รหัสตัวเลข) ⇒ ไม่มีตารางให้ "ตารางชนะ" (<see cref="OcrVatCodeLines.Decide"/> ข้อ 2) ·
/// และฐานกระทบยอดใช้ช่องยอดก่อน VAT ตรง ๆ แม้มันเท่ากับยอดรวมขณะ VAT &gt; 0</para>
/// <para>ล็อกทั้งสองทิศ (CLAUDE.md #4 §H): ใบที่พังกลับมาถูก (บรรทัดสรุปต่ออัตรา · ข้อความยอดก่อน VAT = 1,931.22) และใบที่ถูกอยู่แล้วไม่ถูกแตะ
/// (ตารางอัตราเดียว · ตารางไม่ปิดยอด · แถวส่วนลด % · ยอดก่อน VAT ที่ถูกอยู่แล้ว · ใบเล็กที่ VAT &lt; 1 บาท)</para>
/// </summary>
public class OcrVatRateTableTests
{
    private static string Norm(string s) => ThaiTextNormalizer.Normalize(s);

    private static IReadOnlyList<OcrVatCodeLineInput> FiveEngineLines() => new[]
    {
        new OcrVatCodeLineInput(29.00m, null, "เห็ดหูหนู"),
        new OcrVatCodeLineInput(35.00m, null, "พริกขี้หนูแดง"),
        new OcrVatCodeLineInput(143.50m, null, "สันนอกหมูตัดชิ้น"),
        new OcrVatCodeLineInput(79.00m, null, "เตนิช ไก่หยอง"),
        new OcrVatCodeLineInput(15.00m, null, "ถั่วงอก"),
    };

    // ── ตัวอ่านตาราง ───────────────────────────────────────────────────────────────

    [Fact]
    public void ใบเจ้าของ_ตารางอัตรานำหน้า_คอลัมน์รวมแถวท้ายเป็นยอดทั้งใบ_อ่านได้สองกลุ่ม()
    {
        var t = OcrLineVatMarks.ReadGroups(Norm(OcrPaperSamples.SupermarketRateTableReceipt));

        Assert.True(t.Found);
        Assert.Equal(2, t.Groups.Count);
        Assert.Equal(("0%", OcrVatGroupKind.Unknown, 1502.25m, 0m), (t.Groups[0].PaperCode, t.Groups[0].Kind, t.Groups[0].Net, t.Groups[0].Vat));
        Assert.Equal(("7%", OcrVatGroupKind.Standard7, 428.97m, 30.03m), (t.Groups[1].PaperCode, t.Groups[1].Kind, t.Groups[1].Net, t.Groups[1].Vat));
        Assert.Equal(1931.22m, t.Net);
        Assert.Equal(30.03m, t.Vat);
        Assert.Equal(1961.25m, t.Gross);
        Assert.False(t.AllKnown);   // แถว 0% ไม่มีป้ายบอกยกเว้น/อัตราศูนย์ ⇒ ตัวตัดสินความหมาย (ไม่เดาที่ตัวอ่าน)
    }

    [Fact]
    public void แถว0ไม่มีป้าย_ตัวตัดสินความหมาย_ค่าตั้งต้นยกเว้น_ความมั่นใจต่ำ()
    {
        var r = OcrVatCodeMeaning.ResolvedTable(Norm(OcrPaperSamples.SupermarketRateTableReceipt), null);

        Assert.True(r.Table.AllKnown);
        Assert.Equal(OcrVatGroupKind.Exempt, r.Table.Groups[0].Kind);
        var e = Assert.Single(r.Entries);
        Assert.Equal(("0%", OcrVatCodeMeaningSource.ColdStartDefault, 0.50m), (e.Code, e.Source, e.Confidence));
        Assert.Contains("อัตรา 0% ตามตาราง ภ.พ.", r.Note);   // ข้อความไม่เรียก "รหัส 0%"
    }

    [Fact]
    public void คอลัมน์รวมรายแถว_และแถวรวมสามช่อง_ได้ตารางเดียวกัน()
    {
        const string paper = "อัตรา  ราคาสินค้า  VAT  รวม\n"
            + "0.00%  1,502.25  0.00  1,502.25\n"
            + "7.00%  428.97  30.03  459.00\n"
            + "รวม  1,931.22  30.03  1,961.25\n";
        var t = OcrLineVatMarks.ReadGroups(paper);

        Assert.True(t.Found);
        Assert.Equal((1931.22m, 30.03m, 1961.25m), (t.Net, t.Vat, t.Gross));
        Assert.Equal(3, t.TotalLineNo);   // แถว "รวม" ของตาราง
    }

    [Fact]
    public void ป้ายบนแถวบอกความหมาย_ยกเว้นและNONVAT_ได้ยกเว้นจากกระดาษ_ไม่ต้องถาม()
    {
        var a = OcrLineVatMarks.ReadGroups("ยกเว้นภาษี 0.00%  1,502.25  0.00\nVAT 7.00%  428.97  30.03\n");
        const string paperB = "NON-VAT 0%  1,502.25  0.00  1,502.25\nVAT 7%  428.97  30.03  459.00\n";
        var b = OcrLineVatMarks.ReadGroups(paperB);
        var z = OcrLineVatMarks.ReadGroups("อัตราศูนย์ 0.00%  1,502.25  0.00\n7.00%  428.97  30.03\n");
        var plain = OcrLineVatMarks.ReadGroups("ยกเว้น 0%  1,502.25  0.00\n7%  428.97  30.03\n");
        Assert.Equal(OcrVatGroupKind.Exempt, plain.Groups[0].Kind);

        Assert.True(a.AllKnown);
        Assert.Equal(OcrVatGroupKind.Exempt, a.Groups[0].Kind);
        Assert.Equal(1961.25m, a.Gross);   // ไม่มีคอลัมน์รวม ⇒ ฐาน + VAT
        Assert.True(b.AllKnown);
        Assert.Equal(OcrVatGroupKind.Exempt, b.Groups[0].Kind);
        Assert.Empty(OcrVatCodeMeaning.ResolvedTable(paperB, null).Entries);   // ไม่ต้องถาม AI/ใช้ค่าตั้งต้น
        Assert.Equal(OcrVatGroupKind.ZeroRated, z.Groups[0].Kind);              // กระดาษบอก "อัตราศูนย์" เอง ⇒ 0% ตามที่พิมพ์
    }

    // ── ทิศตรงข้าม: ตารางที่ไม่ควรถูกอ่าน ⇒ None (เส้นเดิมทุกตัวอักษร) ─────────────────────────────

    [Theory]
    [InlineData("VAT 7.00%  428.97  30.03  459.00\nรวม  428.97  30.03  459.00\n")]                         // อัตราเดียว — ชั้นพิสูจน์ทั้งใบตัดสินอยู่แล้ว
    [InlineData("0.00%  1,502.25  0.00  1,502.25\n7.00%  428.97  31.00  459.97\n")]                         // VAT ไม่ใช่ 7% ของฐาน
    [InlineData("0.00%  1,502.25  5.00  1,507.25\n7.00%  428.97  30.03  459.00\n")]                          // แถว 0% มี VAT
    [InlineData("0.00%  1,502.25  0.00  1,502.25\n7.00%  428.97  30.03  1,999.00\n")]                        // คอลัมน์รวมไม่ใช่ทั้งของแถวและยอดสะสม
    [InlineData("0.00%  1,502.25  0.00\n7.00%  428.97  30.03\nรวม  1,900.00  30.03  1,930.03\n")]          // Σ แถว ≠ แถวรวม
    [InlineData("0.00%  1,502.25  0.00\n7.00%  428.97  30.03\n7.00%  100.00  7.00\n")]                     // อัตราซ้ำ — ไม่รู้ว่าแถวไหน
    [InlineData("0.00%  1,502.25  0.00\n10.00%  100.00  10.00\n")]                                          // อัตราไม่ใช่ไทย
    [InlineData("ส่วนลดสมาชิก 7%  100.00  7.00\n0.00%  1,502.25  0.00\n")]                                 // แถวส่วนลด % ไม่ใช่แถว VAT ⇒ เหลืออัตราเดียว
    public void ตารางที่ไม่ผ่านเงื่อนไข_ไม่ถูกอ่านเลย(string paper)
        => Assert.False(OcrLineVatMarks.ReadGroups(paper).Found);

    [Fact]
    public void ตารางรหัสเดิม_ไม่ถูกแตะ_แถวอัตรานำหน้าที่ปนมาไม่ถูกนับ()
    {
        // Makro 3/3 (รหัสตัวเลข + คำอธิบาย) + แถว "0.00% …" แปลกปลอม ⇒ ตารางเดิมทุกตัวเลข
        var before = OcrLineVatMarks.ReadGroups(OcrPaperSamples.MakroPage3of3);
        var after = OcrLineVatMarks.ReadGroups(OcrPaperSamples.MakroPage3of3 + "\n0.00%  1,502.25  0.00\n7.00%  428.97  30.03\n");
        Assert.Equal(before.Evidence, after.Evidence);
        Assert.Equal((before.Net, before.Vat, before.Gross), (after.Net, after.Vat, after.Gross));
    }

    // ── ตัวตัดสินรายการ/ตาราง ────────────────────────────────────────────────────────────

    [Fact]
    public void ใบเจ้าของ_รายการขาด_ตารางปิดยอด_ตารางชนะ_สองบรรทัดสรุป_ไม่มีเลขแต่ง()
    {
        var text = Norm(OcrPaperSamples.SupermarketRateTableReceipt);
        var d = OcrVatCodeLines.Decide(FiveEngineLines(), text, OcrVatCodeMeaning.ResolvedTable(text, null).Table,
            1961.25m, 30.03m, 1961.25m, 0m, pricesIncludeVatPreset: false, tableLinesAllowed: true);

        Assert.Equal(OcrVatCodeLineSource.TableSummary, d.Source);
        Assert.Equal(2, d.TableLines.Count);
        Assert.Equal((OcrVatGroupKind.Exempt, 1502.25m, 0m), (d.TableLines[0].Kind, d.TableLines[0].Net, d.TableLines[0].Vat));
        Assert.Equal((OcrVatGroupKind.Standard7, 428.97m, 30.03m), (d.TableLines[1].Kind, d.TableLines[1].Net, d.TableLines[1].Vat));
        Assert.Equal(1961.25m, d.TableLines.Sum(g => g.Net + g.Vat));
        Assert.Contains("อัตรา 0% ตามตาราง ภ.พ.", d.Note);
    }

    [Fact]
    public void ตารางชนะแม้VATหัวใบหาย_ใช้VATที่กระดาษพิสูจน์()
    {
        // engine อ่าน VAT ไม่ได้ (0) — ยอดที่ยึด (OcrTotalAnchor) ให้ VAT 30.03 จากตาราง ⇒ ด่าน "ตารางลงยอด" ผ่านด้วยหลักฐานกระดาษ
        var text = Norm(OcrPaperSamples.SupermarketRateTableReceipt);
        var anchor = OcrTotalAnchor.Find(text, 1961.25m);
        Assert.Equal(OcrTotalVerdict.Confirmed, anchor.Verdict);
        var d = OcrVatCodeLines.Decide(FiveEngineLines(), text, OcrVatCodeMeaning.ResolvedTable(text, null).Table,
            1961.25m, 0m, 1961.25m, 0m, false, true, paperTotal: anchor.Total, paperVat: anchor.PrintedVat);
        Assert.Equal(OcrVatCodeLineSource.TableSummary, d.Source);
    }

    [Fact]
    public void ทิศตรงข้าม_รายการครบลงตาราง_คงรายการ_อัตรารายบรรทัดตามตาราง_ไม่เดาจากชื่อ()
    {
        const string paper = "ผักกาดขาว  40.00\nหมูสับ  150.00\nซอสปรุงรส  53.50\nน้ำปลา  53.50\n"
            + "รวมทั้งสิ้น  297.00\n0.00%  190.00  0.00  190.00\n7.00%  100.00  7.00  107.00\n";
        var lines = new[]
        {
            new OcrVatCodeLineInput(40.00m, null, "ผักกาดขาว"), new OcrVatCodeLineInput(150.00m, null, "หมูสับ"),
            new OcrVatCodeLineInput(53.50m, null, "ซอสปรุงรส"), new OcrVatCodeLineInput(53.50m, null, "น้ำปลา"),
        };
        var d = OcrVatCodeLines.Decide(lines, paper, OcrVatCodeMeaning.ResolvedTable(paper, null).Table,
            290m, 7m, 297m, 0m, false, true);

        Assert.Equal(OcrVatCodeLineSource.Items, d.Source);
        Assert.True(d.Plan.Applied);
        Assert.Equal(new decimal?[] { -1m, -1m, 7m, 7m }, d.Plan.Rates);   // ทางเดียวที่ Σ ต่อกลุ่ม = ตาราง (40 + 150 = 190)
        Assert.True(d.PricesIncludeVat);
    }

    [Fact]
    public void ทิศตรงข้าม_ตารางไม่ลงยอดหัวใบ_ไม่ชนะ_คงรายการ()
    {
        // ยอดรวมหัวใบ 2,000 ไม่ใช่ 1,961.25 และกระดาษไม่มีป้ายยืนยัน 1,961.25 ⇒ ตารางไม่ "ลงยอด" ⇒ ไม่แทนรายการ (ตัวกระทบยอดฟ้องตามเดิม)
        const string paper = "0.00%  1,502.25  0.00  1,502.25\n7.00%  428.97  30.03  1,961.25\n";
        var d = OcrVatCodeLines.Decide(FiveEngineLines(), paper, OcrVatCodeMeaning.ResolvedTable(paper, null).Table,
            1969.97m, 30.03m, 2000.00m, 0m, false, true);
        Assert.Equal(OcrVatCodeLineSource.Items, d.Source);
        Assert.Empty(d.TableLines);
    }

    [Fact]
    public void ทิศตรงข้าม_มีหักณที่จ่าย_บรรทัดสรุปไม่ได้_คงรายการ()
    {
        var text = Norm(OcrPaperSamples.SupermarketRateTableReceipt);
        var d = OcrVatCodeLines.Decide(FiveEngineLines(), text, OcrVatCodeMeaning.ResolvedTable(text, null).Table,
            1961.25m, 30.03m, 1961.25m, 0m, false, tableLinesAllowed: false);
        Assert.Equal(OcrVatCodeLineSource.Items, d.Source);
    }

    // ── ยอดก่อน VAT ที่เป็นยอดรวมทั้งสิ้น ─────────────────────────────────────────────────────────

    [Fact]
    public void ยอดก่อนVATเท่ายอดรวมที่กระดาษพิสูจน์_ข้อความกระทบยอดพูดฐานจริง1931_22()
    {
        // ใบไม่มีตาราง (ตัวตัดสินตารางไม่พูด) · ยอดรวม 1,961.25 พิสูจน์แล้ว — ข้อความ [Σ-GAP] ต้องอ้างฐาน 1,931.22 ไม่ใช่ 1,961.25 (ยอดรวม VAT)
        var d = OcrVatCodeLines.Decide(FiveEngineLines(), "", OcrVatGroupTable.None, 1961.25m, 30.03m, 1961.25m, 0m, false, true,
            paperTotal: 1961.25m);
        Assert.Equal(OcrLineReconcileCase.LinesShort, d.Recon.Case);
        Assert.Equal(1931.22m, d.NetSubForRecon);
        Assert.Contains("1,931.22", d.Recon.Note);
        Assert.DoesNotContain("1,961.25", d.Recon.Note);
    }

    [Fact]
    public void ทิศตรงข้าม_ยอดรวมไม่ได้พิสูจน์_ยอดก่อนVATเท่ายอดรวม_ฐานเดิม_เคสB()
    {
        // ฝ่ายค้าน S3: sub = total = 1,000 · VAT 70 · ยอดจริงบนกระดาษ 1,070 ที่ขั้นยึดยอดพิสูจน์ไม่ได้ ⇒ "ยอดรวม" คือตัวที่ผิด — ห้ามถอด VAT ออกจากมัน
        var lines = new[] { new OcrVatCodeLineInput(1000.00m, null, "ค่าบริการ") };
        var d = OcrVatCodeLines.Decide(lines, "", OcrVatGroupTable.None, 1000m, 70m, 1000m, 0m, false, true);
        Assert.Equal(1000m, d.NetSubForRecon);
        Assert.Equal(OcrLineReconcileCase.LinesMatchSubTotal, d.Recon.Case);
        // ยอดที่พิสูจน์ได้ไม่ใช่ยอดรวมที่ถืออยู่ (1,070 ≠ 1,000) ⇒ ก็ไม่ถอดเช่นกัน
        Assert.Equal(1000m, OcrHeaderAmounts.ReconSubTotal(1000m, 70m, 1000m, 1070m));
        Assert.Equal(930m, OcrHeaderAmounts.ReconSubTotal(1000m, 70m, 1000m, 1000m));
    }

    // ── ฝ่ายค้าน B1: รายการครบ (Σ ลงตาราง) แต่แบ่งอัตรารายบรรทัดไม่ได้ ⇒ คงรายการ ไม่ให้ตารางชนะ ───────────────────

    [Fact]
    public void ทิศตรงข้าม_ราคาซ้ำแบ่งได้หลายทาง_Σลงตาราง_คงรายการ_ไม่แต่งอัตรา()
    {
        const string paper = "ผักกาด  53.50\nผักบุ้ง  53.50\nซอส  53.50\nน้ำปลา  53.50\nรวมทั้งสิ้น  214.00\n"
            + "0.00%  107.00  0.00  107.00\n7.00%  100.00  7.00  107.00\n";
        var lines = Enumerable.Range(0, 4).Select(i => new OcrVatCodeLineInput(53.50m, null, $"สินค้า {i + 1}")).ToList();
        var d = OcrVatCodeLines.Decide(lines, paper, OcrVatCodeMeaning.ResolvedTable(paper, null).Table,
            207m, 7m, 214m, 0m, false, true, paperTotal: 214m, paperVat: 7m);

        Assert.Equal(OcrVatCodeLineSource.Items, d.Source);
        Assert.Empty(d.TableLines);
        Assert.Equal(OcrVatCodeVerdict.Unreconciled, d.Plan.Verdict);
        Assert.All(d.Plan.Rates, r => Assert.Null(r));   // อัตรารายบรรทัดไปชั้นพิสูจน์ทั้งใบ (PaperExemptAmount) — ตัวนี้ไม่ตั้งเอง
    }

    [Fact]
    public void ทิศตรงข้าม_รายการ21บรรทัดเกินเพดานค้นทางแบ่ง_Σลงตาราง_คงรายการ()
    {
        // 19 × 10.00 + 20.00 (ยกเว้น 210) + 107.00 (7% รวม VAT) = 317.00 = แถวรวมของตาราง — free 21 บรรทัด > MaxFreeLines 20
        const string paper = "0.00%  210.00  0.00  210.00\n7.00%  100.00  7.00  107.00\n";
        var lines = Enumerable.Range(0, 19).Select(i => new OcrVatCodeLineInput(10.00m, null, $"ผัก {i + 1}"))
            .Append(new OcrVatCodeLineInput(20.00m, null, "เนื้อหมู")).Append(new OcrVatCodeLineInput(107.00m, null, "น้ำมันพืช")).ToList();
        Assert.Equal(21, lines.Count);
        var d = OcrVatCodeLines.Decide(lines, paper, OcrVatCodeMeaning.ResolvedTable(paper, null).Table,
            310m, 7m, 317m, 0m, false, true, paperTotal: 317m, paperVat: 7m);

        Assert.Equal(OcrVatCodeLineSource.Items, d.Source);
        Assert.Empty(d.TableLines);
    }

    // ── ฝ่ายค้าน S1: "VAT 0.00%" ของแถวตารางที่กำลังตัดสิน ไม่ใช่หลักฐาน 0% ──────────────────────────────────────

    [Fact]
    public void แถวตารางVAT0เปอร์เซ็นต์_ไม่ใช่หลักฐานอัตราศูนย์_คำตอบZeroRatedถูกทิ้ง_ยังเป็นยกเว้น()
    {
        const string paper = "VAT 0.00%  1,502.25  0.00\n7.00%  428.97  30.03\n";
        var t = OcrLineVatMarks.ReadGroups(paper);
        Assert.Equal(OcrVatGroupKind.Unknown, t.Groups[0].Kind);   // ป้าย "VAT" ไม่บอกความหมาย
        Assert.False(OcrVatCodeMeaning.ZeroRatedOnPaper(paper));
        var e = OcrVatCodeMeaning.Choose(t.Groups[0], "ZeroRated", 0.95m, hasModelAnswer: true, usedAi: true, feedbackId: null,
            zeroRatedOnPaper: OcrVatCodeMeaning.ZeroRatedOnPaper(paper));
        Assert.Equal((OcrVatGroupKind.Exempt, OcrVatCodeMeaningSource.ColdStartDefault), (e.Kind, e.Source));
        // ทิศตรงข้าม: ป้าย "ส่งออก"/"zero" บนแถว ⇒ 0% จากกระดาษ
        Assert.Equal(OcrVatGroupKind.ZeroRated, OcrLineVatMarks.ReadGroups("ส่งออก 0%  1,502.25  0.00\n7%  428.97  30.03\n").Groups[0].Kind);
        Assert.Equal(OcrVatGroupKind.ZeroRated, OcrLineVatMarks.ReadGroups("Zero rated 0%  1,502.25  0.00\n7%  428.97  30.03\n").Groups[0].Kind);
    }

    // ── ฝ่ายค้าน N ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ป้ายที่มีoffในคำ_CoffeeOffice_ยังเป็นแถวตาราง_แต่OFFคำเดี่ยวไม่ใช่()
    {
        Assert.True(OcrLineVatMarks.ReadGroups("Coffee 0%  1,502.25  0.00\nOffice 7%  428.97  30.03\n").Found);
        Assert.False(OcrLineVatMarks.ReadGroups("OFF 7%  428.97  30.03\n0%  1,502.25  0.00\n").Found);   // เหลืออัตราเดียว
        Assert.False(OcrLineVatMarks.IsRateTableRow("OFF 7%  428.97  30.03"));
        Assert.True(OcrLineVatMarks.IsRateTableRow("Coffee 0%  1,502.25  0.00"));
    }

    [Fact]
    public void แถวอัตราแปลกปลอมกับแถวรวมแปลกปลอมก่อนตารางรหัสMakro_ไม่ถูกยึดเป็นแถวรวมของตารางรหัส()
    {
        var before = OcrLineVatMarks.ReadGroups(OcrPaperSamples.MakroPage3of3);
        var after = OcrLineVatMarks.ReadGroups("0.00%  100.00  0.00\nรวม  1.00  2.00  3.00\n" + OcrPaperSamples.MakroPage3of3);
        Assert.True(after.Found);
        Assert.Equal(before.Evidence, after.Evidence);
        Assert.Equal((before.Net, before.Vat, before.Gross), (after.Net, after.Vat, after.Gross));
    }

    [Fact]
    public void ทิศตรงข้าม_ยอดก่อนVATถูกอยู่แล้ว_ฐานกระทบยอดเท่าเดิม()
    {
        var d = OcrVatCodeLines.Decide(FiveEngineLines(), "", OcrVatGroupTable.None, 1931.22m, 30.03m, 1961.25m, 0m, false, true);
        Assert.Equal(1931.22m, d.NetSubForRecon);
        // ใบไม่มี VAT (ยอดก่อน VAT = ยอดรวม ถูกต้อง) ⇒ ไม่แตะ
        var noVat = OcrVatCodeLines.Decide(FiveEngineLines(), "", OcrVatGroupTable.None, 1000m, 0m, 1000m, 0m, false, true);
        Assert.Equal(1000m, noVat.NetSubForRecon);
    }

    [Theory]
    [InlineData(1961.25, 30.03, 1961.25, true)]
    [InlineData(1931.22, 30.03, 1961.25, false)]   // ฐานถูก
    [InlineData(10.00, 0.70, 10.70, false)]        // ใบเล็ก VAT < 1 บาท — ค่าเผื่อ 1 บาทจะตีผิด ⇒ เทียบตรงถึงสตางค์
    [InlineData(1000.00, 0.00, 1000.00, false)]    // ไม่มี VAT ⇒ ยอดก่อน VAT = ยอดรวมถูกต้อง
    public void SubTotalIsGrandTotal_เฉพาะเลขเดียวกันสองช่องขณะมีVAT(double sub, double vat, double total, bool expected)
        => Assert.Equal(expected, OcrHeaderAmounts.SubTotalIsGrandTotal((decimal)sub, (decimal)vat, (decimal)total));

    [Fact]
    public void ขั้นเขียนหัวสแกน_ยอดก่อนVATที่เป็นยอดรวม_ฐานที่ไม่ได้พิมพ์เป็นRule_ฐานที่ถูกไม่แตะ()
    {
        // ใบเจ้าของไม่มีแถว "รวม ฐาน VAT รวม" — 1,931.22 คือระบบบวกเอง ⇒ Rule 0.80 (เหลือง) ไม่ใช่ PaperLabel (ฝ่ายค้าน S2)
        var text = Norm(OcrPaperSamples.SupermarketRateTableReceipt);
        var r = OcrTotalAnchor.Find(text, 1961.25m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.Null(r.PrintedBase);
        Assert.Equal(30.03m, r.PrintedVat);
        var plan = OcrTotalAnchor.Plan(r, 1961.25m, 30.03m, text);
        Assert.Equal(1931.22m, plan.SubTotal);
        Assert.Equal(30.03m, plan.Vat);
        Assert.Equal(OcrFieldSource.Rule, plan.SubSource);
        Assert.Equal(OcrTotalAnchor.DerivedConfidence, plan.SubConfidence);
        Assert.Contains("เท่ากับยอดรวมทั้งสิ้น", plan.Trace);
        Assert.DoesNotContain("พิมพ์ว่ารวม VAT แล้ว", plan.Trace);
        Assert.Null(OcrTotalAnchor.Plan(r, 1931.22m, 30.03m, text).SubTotal);
    }

    [Fact]
    public void ขั้นเขียนหัวสแกน_ตารางมีแถวรวมพิมพ์ฐาน_ฐานเป็นPaperLabel()
    {
        const string paper = "รวมทั้งสิ้น  1,961.25\nบัตรเครดิต  1,961.25\n"
            + "0.00%  1,502.25  0.00  1,502.25\n7.00%  428.97  30.03  459.00\nรวม  1,931.22  30.03  1,961.25\n";
        var r = OcrTotalAnchor.Find(paper, 1961.25m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        var plan = OcrTotalAnchor.Plan(r, 1961.25m, 30.03m, paper);
        Assert.Equal((1931.22m, OcrFieldSource.PaperLabel), (plan.SubTotal, plan.SubSource));
    }
}
