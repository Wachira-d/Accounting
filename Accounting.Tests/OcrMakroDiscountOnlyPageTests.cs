using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// 2026-10-16 ทีม OCR 7 — Makro หน้า 2/2 (405901117163) สแกนซ้ำ: หน้ารีวิวโชว์ "ยอดก่อน VAT 11208" ทั้งที่ยอดรวม 10,980 ถูก ·
/// ตัวตัดสินเจ้าของช่อง = <see cref="OcrTotalAnchor.Plan"/> (สาขา Confirmed เดิมไม่เขียนอะไร) · เทสต์สองทิศ (CLAUDE.md #4 §H)
/// </summary>
public class OcrMakroDiscountOnlyPageTests
{
    private static string Norm(string s) => ThaiTextNormalizer.Normalize(s);

    [Fact]
    public void ยอดรวมถูกแล้ว_ยอดก่อนVATคือยอดรวมVATก่อนส่วนลด11208_ใช้ฐานที่พิมพ์10341_50()
    {
        var text = Norm(OcrPaperSamples.MakroPage2of2);
        var r = OcrTotalAnchor.Find(text, 10980.00m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        // ก่อนแก้: สาขา Confirmed ไม่เขียนอะไร ⇒ ExtractedSubTotal ค้าง 11,208 (ภาพหน้าจอ)
        Assert.Null(OcrTotalAnchor.Plan(r, 11208.00m, 638.50m).SubTotal);
        var plan = OcrTotalAnchor.Plan(r, 11208.00m, 638.50m, text);
        Assert.Null(plan.Total);                                     // ยอดรวมไม่แตะ (ถูกอยู่แล้ว)
        Assert.Equal(10341.50m, plan.SubTotal);
        Assert.Equal(638.50m, plan.Vat);
        Assert.Equal(OcrFieldSource.PaperLabel, plan.SubSource);
        Assert.Contains("รวม VAT แล้ว (ไม่ใช่ฐาน)", plan.Trace);
    }

    [Fact]
    public void ทิศตรงข้าม_ยอดก่อนVATที่ปิดยอดอยู่แล้ว_หรือไม่มีป้ายรวมVAT_ไม่แตะ()
    {
        var text = Norm(OcrPaperSamples.MakroPage2of2);
        var r = OcrTotalAnchor.Find(text, 10980.00m);
        // ฐานถูกอยู่แล้ว ⇒ ไม่เขียน
        Assert.Null(OcrTotalAnchor.Plan(r, 10341.50m, 638.50m, text).SubTotal);
        // "รวมเงิน" ก่อนหักส่วนลดที่เป็นฐานจริง (ไม่มีป้ายรวม VAT บนตัวเลขนั้น) — ใบร้านวัสดุ: ฐาน 1,000 · ลด 100 · VAT 63 · รวม 963
        const string hardware = "ใบกำกับภาษี\nรวมเงิน 1,000.00\nส่วนลด 100.00\nหลังหักส่วนลด 900.00\nภาษีมูลค่าเพิ่ม 7% 63.00\nจำนวนเงินรวมทั้งสิ้น 963.00\n";
        var h = OcrTotalAnchor.Find(Norm(hardware), 963.00m);
        Assert.Null(OcrTotalAnchor.Plan(h, 1000.00m, 63.00m, Norm(hardware)).SubTotal);
        // ยึดยอดไม่ได้ (Unknown — ข้อความ Tesseract ของใบเดียวกัน) ⇒ ไม่รู้ = ไม่เขียนทับ
        var t = Norm(OcrMakroPage2VatAddedTwiceTests.MakroPage2of2Tesseract);
        Assert.Null(OcrTotalAnchor.Plan(OcrTotalAnchor.Find(t, 10980.00m), 11208.00m, 638.50m, t).SubTotal);
    }
}
