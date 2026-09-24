using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 192 (Total-first) — ตัวอ่านตัวเลขบนกระดาษที่ขั้นยึดยอดใช้: <see cref="OcrLineVatMarks.ReadGroups"/> (ตารางสรุปตามรหัส ภ.พ.) ·
/// <see cref="OcrPaperAmounts"/> · <see cref="VatBackCalcGuard.PrintedVatContradicts"/> (ห้ามแต่ง VAT ที่ขัดกับกระดาษ) ·
/// <see cref="OcrPageSet"/> (หน้าไม่ครบ)
///
/// <para><b>ครึ่งที่ 1</b>: ใบ Makro 3/3 — ตารางรหัสตัวเลข + คำอธิบาย "1=ยกเว้น 2=ต้องเสียภาษี" · VAT 1,148.28 ที่พิมพ์ไว้
/// ต้องกันการแต่ง 7/107 = 1,577.29 · หน้า 3 จาก 3 = ขาดหน้า 1–2</para>
/// <para><b>ครึ่งที่ 2</b>: ตารางที่ผลรวมไม่ตรงแถว "รวม" / ไม่มีแถว "รวม" / รหัสที่ไม่มีคำอธิบาย / คำอธิบายขัดตัวเลข = ไม่ใช้ ·
/// Wine Pro ที่ VAT = 7/107 ยังแยก VAT ได้ตามเดิม · ใบที่ไม่พิมพ์ VAT เลยไม่ถูกแตะ · ไฟล์ครบทุกหน้าไม่เตือน ·
/// <see cref="OcrLineVatMarks.Read"/> เดิมไม่เห็นตาราง Makro (ไม่แตะ)</para>
/// </summary>
public class OcrVatGroupTableTests
{
    // ── ReadGroups ──────────────────────────────────────────────────────────

    [Fact]
    public void Makro_ตารางรหัสตัวเลข_ยกเว้น6260_มีVAT16403_97_รวม23812_25()
    {
        var t = OcrLineVatMarks.ReadGroups(OcrPaperSamples.MakroPage3of3);

        Assert.True(t.Found);
        Assert.True(t.AllKnown);
        Assert.Equal(2, t.Groups.Count);
        Assert.Equal(("1", OcrVatGroupKind.Exempt, 6260.00m, 0m), (t.Groups[0].PaperCode, t.Groups[0].Kind, t.Groups[0].Net, t.Groups[0].Vat));
        Assert.Equal(("2", OcrVatGroupKind.Standard7, 16403.97m, 1148.28m), (t.Groups[1].PaperCode, t.Groups[1].Kind, t.Groups[1].Net, t.Groups[1].Vat));
        Assert.Equal(22663.97m, t.Net);
        Assert.Equal(1148.28m, t.Vat);
        Assert.Equal(23812.25m, t.Gross);
        Assert.Contains("ยกเว้น", t.Groups[0].Evidence);   // ความหมายมาจากคำอธิบายบนกระดาษ
    }

    [Fact]
    public void Makro_ตัวอ่านเดิมRead_ไม่เห็นตารางรหัสตัวเลข_ไม่ถูกแตะ()
    {
        var p = OcrLineVatMarks.Read(OcrPaperSamples.MakroPage3of3);
        Assert.Empty(p.Marks);
        Assert.Empty(p.CodeRates);
    }

    [Fact]
    public void ไม่มีคำอธิบายรหัส_พิสูจน์ด้วยตัวเลขของแถว()
    {
        var t = OcrLineVatMarks.ReadGroups(
            "   17  1  6,260.00  0.00  6,260.00\n  134  2  16,403.97  1,148.28  17,552.25\n  รวม  22,663.97  1,148.28  23,812.25");
        Assert.True(t.AllKnown);
        Assert.Equal(OcrVatGroupKind.Exempt, t.Groups[0].Kind);      // VAT 0.00
        Assert.Equal(OcrVatGroupKind.Standard7, t.Groups[1].Kind);   // VAT = 7% ของฐาน
    }

    [Fact]
    public void WinePro_แถวสรุปรหัสตัวอักษร_กลุ่มเดียว()
    {
        var t = OcrLineVatMarks.ReadGroups(OcrPaperSamples.WinePro);
        Assert.True(t.Found);
        Assert.Single(t.Groups);
        Assert.Equal(OcrVatGroupKind.Standard7, t.Groups[0].Kind);
        Assert.Equal(3593.00m, t.Gross);
    }

    [Theory]
    // Σ แถว ≠ แถว "รวม" ⇒ ใช้ไม่ได้ทั้งตาราง
    [InlineData("   17  1  6,260.00  0.00  6,260.00\n  134  2  16,403.97  1,148.28  17,552.25\n  รวม  22,663.97  1,148.28  23,900.00")]
    // ตารางรหัสตัวเลขไม่มีแถว "รวม" ยืนยัน ⇒ ไม่ใช้ (กันแถวตัวเลขล้วนอื่นหลุดเข้ามา)
    [InlineData("   17  1  6,260.00  0.00  6,260.00\n  134  2  16,403.97  1,148.28  17,552.25")]
    // แถวที่ ฐาน + VAT ≠ รวม ⇒ อ่านผิด ไม่ใช้
    [InlineData("   17  1  6,260.00  0.00  6,300.00\n  รวม  6,260.00  0.00  6,300.00")]
    // ไม่มีตารางเลย
    [InlineData("ใบเสร็จ\nรวมทั้งสิ้น 1,000.00")]
    public void ตารางที่ใช้ไม่ได้_คืนว่างทั้งตาราง(string paper)
        => Assert.False(OcrLineVatMarks.ReadGroups(paper).Found);

    [Fact]
    public void รหัสที่คำอธิบายไม่บอกความหมาย_Unknown_ไม่เดา()
    {
        var t = OcrLineVatMarks.ReadGroups(
            "   17  1  6,260.00  0.00  6,260.00\n  5  4  100.00  7.00  107.00\n  รวม  6,360.00  7.00  6,367.00\n"
            + "1=สินค้ายกเว้นภาษีมูลค่าเพิ่ม · 4=สินค้ามูลค่าเฉพาะคิดภาษีมูลค่าเพิ่มจาก Legal Amount");
        Assert.True(t.Found);
        Assert.Equal(OcrVatGroupKind.Unknown, t.Groups[1].Kind);
        Assert.False(t.AllKnown);   // ⇒ ตัวสร้างบรรทัดไม่แยกกลุ่ม
    }

    [Fact]
    public void คำอธิบายว่ายกเว้นแต่แถวมีVAT_ขัดกัน_Unknown()
    {
        var t = OcrLineVatMarks.ReadGroups(
            "   17  1  6,260.00  10.00  6,270.00\n  134  2  16,403.97  1,148.28  17,552.25\n  รวม  22,663.97  1,158.28  23,822.25\n"
            + "1=สินค้ายกเว้นภาษีมูลค่าเพิ่ม · 2=สินค้าที่ต้องเสียภาษีมูลค่าเพิ่ม");
        Assert.Equal(OcrVatGroupKind.Unknown, t.Groups[0].Kind);
        Assert.Equal(OcrVatGroupKind.Standard7, t.Groups[1].Kind);
    }

    // ── OcrPaperAmounts ─────────────────────────────────────────────────────

    [Fact]
    public void ยอดเงินบนบรรทัด_ตัดเปอร์เซ็นต์_วงเล็บเป็นบวก_ไม่หยิบเลขที่วันที่()
    {
        Assert.Equal(new[] { 328.67m }, OcrPaperAmounts.MoneyOn("ภาษีมูลค่าเพิ่ม 7% / VAT 7%   328.67").ToArray());
        Assert.Equal(new[] { 216.82m }, OcrPaperAmounts.MoneyOn("ส่วนลด/ Discount   (216.82)").ToArray());
        Assert.Empty(OcrPaperAmounts.MoneyOn("เลขที่ 005901363513   วันที่ 09/09/2026"));
        Assert.Empty(OcrPaperAmounts.MoneyOn("ค่าจัดส่ง +฿37"));
    }

    [Fact]
    public void ยอดVATที่พิมพ์_รวมตารางกลุ่ม_ไม่รวมแถวราคารวมภาษี()
    {
        Assert.Equal(new[] { 1148.28m }, OcrPaperAmounts.VatAmounts(OcrPaperSamples.MakroPage3of3).Select(v => v.Amount).ToArray());
        Assert.Equal(new[] { 35.07m }, OcrPaperAmounts.VatAmounts(OcrPaperSamples.UptoyouShopee).Select(v => v.Amount).ToArray());
        Assert.Equal(new[] { 328.67m }, OcrPaperAmounts.VatAmounts(OcrPaperSamples.ScommerceLazada).Select(v => v.Amount).ToArray());
    }

    [Fact]
    public void แถวส่วนลด_ข้ามแถวยอดศูนย์และแถวหลังหักส่วนลด()
    {
        Assert.Equal(new[] { 216.82m }, OcrPaperAmounts.DiscountRows(OcrPaperSamples.ScommerceLazada).Select(r => r.Amount).ToArray());
        Assert.Equal(new[] { 297.75m }, OcrPaperAmounts.DiscountRows(OcrPaperSamples.MakroPage3of3).Select(r => r.Amount).ToArray());
        Assert.Empty(OcrPaperAmounts.DepositRows(OcrPaperSamples.MakroPage3of3));   // "หักเงินมัดจำ 0.00" ไม่ใช่หลักฐาน
        Assert.True(OcrPaperAmounts.IsPrinted(OcrPaperAmounts.AllPrinted(OcrPaperSamples.UptoyouShopee), 500.93m));
        Assert.False(OcrPaperAmounts.IsPrinted(OcrPaperAmounts.AllPrinted(OcrPaperSamples.UptoyouShopee), 402.93m));
    }

    // ── ห้ามแต่ง VAT ที่ขัดกับกระดาษ (back-calc 7/107 ทั้งสองชุด) ──────────────

    [Fact]
    public void Makro_VATพิมพ์1148_28_ห้ามแต่ง7ส่วน107ของ24110()
    {
        var why = VatBackCalcGuard.PrintedVatContradicts(OcrPaperSamples.MakroPage3of3, 24110m);
        Assert.NotNull(why);
        Assert.Contains("1,148.28", why);
        Assert.Contains("1,577.29", why);

        var d = VatBackCalcGuard.Decide(OcrPaperSamples.MakroPage3of3, "0107567000414", null, totalAmount: 24110m);
        Assert.False(d.Allowed);
    }

    [Fact]
    public void WinePro_VATพิมพ์เท่ากับ7ส่วน107_ไม่ขัด_แยกได้ตามเดิม()
        => Assert.Null(VatBackCalcGuard.PrintedVatContradicts(OcrPaperSamples.WinePro, 3593m));

    [Fact]
    public void กระดาษไม่พิมพ์VATเลย_ไม่ขัด_พฤติกรรมเดิม()
    {
        Assert.Null(VatBackCalcGuard.PrintedVatContradicts("ใบกำกับภาษี\nรวม 1,070.00", 1070m));
        // ไม่ส่งยอดรวม = ด่านเดิมทุกประการ
        Assert.Equal(
            VatBackCalcGuard.Decide("ใบกำกับภาษี\nรวม 1,070.00", "0107567000414", null).Allowed,
            VatBackCalcGuard.Decide("ใบกำกับภาษี\nรวม 1,070.00", "0107567000414", null, totalAmount: 1070m).Allowed);
    }

    // ── หน้าไม่ครบ ──────────────────────────────────────────────────────────

    [Fact]
    public void Makro_หน้า3จาก3_ขาดหน้า1และ2_ข้อสังเกต()
    {
        var c = OcrPageSet.Read(OcrPaperSamples.MakroPage3of3);
        Assert.Equal(3, c.DeclaredPages);
        Assert.Equal(new[] { 1, 2 }, c.MissingPages.ToArray());
        var note = OcrPageSet.PartialNote(OcrPaperSamples.MakroPage3of3);
        Assert.StartsWith(OcrPageSet.PartialTag, note);
        Assert.Contains("1, 2", note);
    }

    [Theory]
    [InlineData("หน้า 1 จาก 2\nรายการ\nหน้า 2 จาก 2")]   // ครบทุกหน้า
    [InlineData("ใบเสร็จไม่มีเลขหน้า")]                    // ไม่พิมพ์ = ไม่รู้ ⇒ ไม่เตือน
    [InlineData("หน้า 1/2\nPage 3 of 3")]                  // จำนวนหน้าทั้งชุดขัดกันเอง ⇒ ไม่รู้
    public void ไฟล์ครบหรือไม่รู้_ไม่เตือน(string paper)
        => Assert.Null(OcrPageSet.PartialNote(paper));
}
