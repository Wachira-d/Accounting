using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 202 — ผู้ใช้รายงาน "แก้ไขยอดประกันสังคมไม่ได้": เพิ่มพนักงานเข้ารอบ เงินเดือนงวดนี้ 7,000 แต่ฐาน ปกส. ค้างเงินเดือนเต็ม 30,000
/// ⇒ ปกส. 875 · ล็อกข้อความเตือนฝั่งเซิร์ฟเวอร์ (ไม่บล็อก) สองทิศ</summary>
public class PayrollSsoBaseAboveWagesTests
{
    [Fact]
    public void ฐาน_30000_รายได้งวด_7000_ต้องเตือน_พร้อมทางแก้()
    {
        var n = PayrollSsoFlagGuard.BaseAboveWagesNotice(30000m, 7000m);
        Assert.NotNull(n);
        Assert.Contains("30,000.00", n);
        Assert.Contains("7,000.00", n);
        Assert.Contains("แก้ยอด", n);
    }

    [Theory]
    [InlineData(7000, 7000)]     // ฐาน = รายได้ (เคสปกติ)
    [InlineData(5000, 7000)]     // ฐานต่ำกว่ารายได้ (ไม่รวมเบี้ยเลี้ยง — ม.5)
    [InlineData(0, 7000)]        // ไม่อยู่ใน ม.33
    [InlineData(1650, 0)]        // ไม่มีรายได้ — ด่านอื่นตัดสิน (รายได้ 0 ถูกปฏิเสธที่ ➕ อยู่แล้ว)
    public void ฐานไม่เกินรายได้_หรือไม่มีฐาน_ไม่เตือน(decimal ssoBase, decimal gross)
        => Assert.Null(PayrollSsoFlagGuard.BaseAboveWagesNotice(ssoBase, gross));
}
