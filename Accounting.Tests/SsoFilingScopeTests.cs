using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "เงินที่โอน" ต้องเท่ากับ "ยอดที่ประกาศในไฟล์ สปส.1-10" เสมอ
///
/// ที่มา (รอบ 168): ยอดนำส่งมาจาก <c>PayrollRun.TotalSocialSecurityEmployee/Employer</c>
/// ซึ่งรวม <b>ทุกแถว</b> (PayrollService คิดจาก <c>run.Details.Sum(...)</c> ไม่ดูธงใด ๆ)
/// ขณะที่ exporter ตัดแถวด้วยเงื่อนไขของตัวเอง ⇒ แถวที่ "มีเงินแต่ไม่ถูกประกาศ"
/// ทำให้ สปส. ตีกลับทั้งไฟล์ · ด่านเดิมเขียน <c>d.IsSubjectToSocialSecurity</c> บน
/// <c>PayrollDetail</c> ซึ่ง **ไม่มีพร็อพเพอร์ตี้นี้** (CS1061 ล้มทั้ง solution) และ
/// ต่อให้แก้ชนิดถูก ก็ยังครอบไม่ถึงเคส "ธง = false แต่มียอด"
/// </summary>
public class SsoFilingScopeTests
{
    [Theory]
    // ปกติ — ธงติ๊ก + มียอดสองฝั่ง ⇒ อยู่ในไฟล์
    [InlineData(true, "750", "750", true, false)]
    // ไม่อยู่ในระบบ ปกส. และไม่มียอดเลย ⇒ ไม่อยู่ในไฟล์ และ **ไม่ใช่ความขัดแย้ง**
    [InlineData(false, "0", "0", false, false)]
    // ทรง (ก) — ปลดธงหลังรันเงินเดือน / import ส่งยอดมาแต่ไม่ตั้งธง
    [InlineData(false, "750", "750", false, true)]
    [InlineData(false, "0", "750", false, true)]
    // ทรง (ข) — ม.46 ให้สองฝั่งใช้ฐานเดียวกัน ⇒ 0 คู่กับมียอด = ข้อมูลเสียเสมอ
    [InlineData(true, "0", "750", false, true)]
    // ฝั่งลูกจ้างมี แต่ฝั่งนายจ้างเป็น 0 — ยังประกาศได้ (ไฟล์ตัดจากฝั่งลูกจ้าง)
    [InlineData(true, "750", "0", true, false)]
    public void ตัดสินแถวตรงกับที่ไฟล์จะประกาศ(
        bool subject, string emp, string empr, bool declared, bool conflict)
    {
        // xUnit InlineData รับ decimal ไม่ได้ — ส่งเป็นสตริงแล้วแปลงเอง
        var e = decimal.Parse(emp, System.Globalization.CultureInfo.InvariantCulture);
        var r = decimal.Parse(empr, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(declared, SsoFilingScope.IsDeclared(subject, e));
        Assert.Equal(conflict, SsoFilingScope.IsFundedButUndeclared(subject, e, r));
    }

    /// <summary>invariant ที่จับคลาสบั๊กนี้ได้: แถวที่ **มีเงิน** ต้องเป็นอย่างใด
    /// อย่างหนึ่งเท่านั้น — อยู่ในไฟล์ หรือถูกด่านจับ. ห้ามมีแถวที่ "มีเงิน แต่
    /// ไม่อยู่ในไฟล์ และด่านก็ไม่เห็น" (ซึ่งเป็นช่องที่เงินหายเงียบ)</summary>
    [Fact]
    public void ทุกแถวที่มีเงิน_ต้องอยู่ในไฟล์หรือถูกด่านจับ_อย่างใดอย่างหนึ่ง()
    {
        var amounts = new[] { 0m, 0.01m, 375m, 750m, 875m };
        foreach (var subject in new[] { true, false })
            foreach (var e in amounts)
                foreach (var r in amounts)
                {
                    var funded = e > 0 || r > 0;
                    var declared = SsoFilingScope.IsDeclared(subject, e);
                    var conflict = SsoFilingScope.IsFundedButUndeclared(subject, e, r);
                    if (!funded) { Assert.False(conflict); continue; }
                    Assert.True(declared ^ conflict,
                        $"subject={subject} emp={e} empr={r}: declared={declared} conflict={conflict}");
                }
    }

    [Fact]
    public void เหตุผลรายแถว_บอกสิ่งที่ต้องไปแก้_ไม่ใช่แค่ว่าข้อมูลผิด()
    {
        Assert.Contains("อยู่ในระบบประกันสังคม", SsoFilingScope.ReasonOf(false, 750m, 750m));
        Assert.Contains("ม.46", SsoFilingScope.ReasonOf(true, 0m, 750m));
    }
}
