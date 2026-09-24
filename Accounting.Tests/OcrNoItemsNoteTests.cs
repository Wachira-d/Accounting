using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 (คำตัดสินเจ้าของข้อ 5) — ใบที่ไฟล์ไม่มีรายการสินค้า (Makro หน้า 3/3) ⇒ บรรทัดสรุปต่อกลุ่มภาษี + คำเตือนเรื่องสต็อก <b>ที่ไม่หยุด</b>
///
/// <para><b>ครึ่งที่ 1</b>: ข้อความตรงถ้อยคำเจ้าของ · บอกจำนวนบรรทัดสรุป</para>
/// <para><b>ครึ่งที่ 2</b>: แท็กนี้ไม่อยู่ในตัวหยุดอนุมัติเอง (ยอด/ภาษีถูกตามกระดาษแล้ว) — ใบที่มีแค่แท็กนี้ยังอนุมัติเองได้ ·
/// ใบที่มีรายการสินค้า (Wine Pro) ไม่ได้บรรทัดสรุป จึงไม่มีคำเตือนนี้</para>
/// </summary>
public class OcrNoItemsNoteTests
{
    private static IReadOnlyList<OcrVatGroup> MakroGroups()
        => OcrTotalDecomposer.SummaryGroupLines(
            OcrTotalDecomposer.Decompose(OcrPaperSamples.MakroPage3of3, 22663.97m, 1148.28m, 23812.25m, 297.75m),
            23812.25m, 1148.28m, headerSubTotal: 22663.97m);

    [Fact]
    public void Makroไม่มีรายการ_คำเตือนตรงถ้อยคำเจ้าของ_บอกจำนวนบรรทัดสรุป()
    {
        var note = OcrTotalDecomposer.NoItemsNote(MakroGroups());
        Assert.StartsWith(OcrTotalDecomposer.NoItemsTag, note);
        Assert.Contains("ไม่มีรายการสินค้า หากต้องตัดสต็อกให้ครบถ้วน กรุณาบันทึกรายการย่อยเอง", note);
        Assert.Contains("2 บรรทัด", note);
    }

    [Fact]
    public void คำเตือนไม่มีรายการ_ไม่หยุดการอนุมัติเอง()
    {
        Assert.DoesNotContain(OcrPostingReadiness.BlockingTags, b => b.Tag == OcrTotalDecomposer.NoItemsTag);
        var notes = "[Tier] Azure DI สำเร็จ\n" + OcrTotalDecomposer.NoItemsNote(MakroGroups());
        Assert.True(OcrPostingReadiness.Evaluate(notes, true).CanAutoApprove);
    }

    [Fact]
    public void ใบที่มีรายการสินค้า_ไม่มีบรรทัดสรุป_จึงไม่มีคำเตือน()
    {
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.WinePro, 3357.94m, 235.06m, 3593.00m, 0m);
        Assert.Empty(OcrTotalDecomposer.SummaryGroupLines(d, 3593m, 235.06m, 3357.94m));
    }
}
