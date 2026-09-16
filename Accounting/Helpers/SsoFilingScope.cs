namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสิน **ตัวเดียว** ว่า "แถวพนักงานแถวนี้จะถูกประกาศในไฟล์ สปส.1-10 หรือไม่"
///
/// ทำไมต้องมี: ยอดที่ระบบ **นำส่ง** มาจาก
/// <c>PayrollRun.TotalSocialSecurityEmployee/Employer</c> ซึ่ง
/// <c>PayrollService</c> คิดจาก <c>run.Details.Sum(...)</c> **ทุกแถว ไม่ดูธงใด ๆ**
/// (PayrollService.cs:1231/1481/2137) ขณะที่ไฟล์ สปส.1-10 ทั้ง .txt และ .xlsx
/// ตัดแถวด้วยเงื่อนไขของตัวเอง ⇒ แถวที่ "มีเงินแต่ไม่ถูกประกาศ" ทำให้
/// **เงินที่โอนไป ≠ ยอดที่ประกาศ** แล้ว สปส. ตีกลับทั้งไฟล์ — เงียบสนิทเพราะ
/// ยอดบนหน้าจอกับยอดในไฟล์ไม่เคยถูกเอามาเทียบกัน
///
/// ⚠️ ถ้าจะเปลี่ยนเงื่อนไขการตัดแถว ให้แก้ที่นี่ที่เดียว — ทั้ง exporter และ
/// ด่าน <c>SSO-PAIR-CONFLICT</c> ใน <c>StatutoryRemittanceService.RemitAsync</c>
/// อ่านจากตัวนี้ ⇒ drift เป็นศูนย์โดยโครงสร้าง (กลไกเดียวกับ
/// <see cref="PayrollRunFilingScope"/> และ <c>WhtRemitScope</c>)
/// </summary>
public static class SsoFilingScope
{
    /// <summary>true = แถวนี้จะมีอยู่ในไฟล์ สปส.1-10
    /// (ธง "อยู่ในระบบประกันสังคม" ของพนักงาน **และ** มียอดสมทบฝั่งลูกจ้าง)</summary>
    public static bool IsDeclared(bool employeeIsSubjectToSso, decimal ssoEmployee)
        => employeeIsSubjectToSso && ssoEmployee > 0;

    /// <summary>true = แถวนี้ **มีเงินอยู่ในยอดนำส่ง แต่จะไม่ถูกประกาศในไฟล์**
    /// — คู่ที่ขัดกันซึ่งต้องบล็อกก่อนเงินออก. ครอบสองทรงที่เกิดจริง:
    /// (ก) ธงเป็น false แต่มียอดสมทบ (ปลดธงหลังรันเงินเดือน / import ส่งยอดมา
    ///     แต่ไม่ตั้งธง) (ข) ธงเป็น true แต่ฝั่งลูกจ้าง = 0 ขณะที่ฝั่งนายจ้าง &gt; 0
    ///     ซึ่ง **ม.46 บอกว่าเป็นไปไม่ได้** (สองฝั่งสมทบในอัตราเดียวกันจากฐานเดียวกัน)</summary>
    public static bool IsFundedButUndeclared(
        bool employeeIsSubjectToSso, decimal ssoEmployee, decimal ssoEmployer)
        => (ssoEmployee > 0 || ssoEmployer > 0)
           && !IsDeclared(employeeIsSubjectToSso, ssoEmployee);

    /// <summary>เหตุผลรายแถวแบบสั้น — ใช้ต่อท้ายชื่อพนักงานในข้อความปฏิเสธ
    /// เพื่อให้ผู้ใช้รู้ว่าต้องไปแก้อะไร ไม่ใช่แค่บอกว่า "ข้อมูลผิด"</summary>
    public static string ReasonOf(
        bool employeeIsSubjectToSso, decimal ssoEmployee, decimal ssoEmployer)
    {
        if (!employeeIsSubjectToSso)
            return "ไม่ได้ติ๊ก “อยู่ในระบบประกันสังคม” แต่มียอดสมทบ";
        if (ssoEmployee <= 0 && ssoEmployer > 0)
            return "ฝั่งลูกจ้างเป็น 0 แต่ฝั่งนายจ้างมียอด (ม.46 ให้ใช้ฐานเดียวกัน)";
        return "ยอดสมทบไม่เข้าเกณฑ์ที่ไฟล์ สปส.1-10 ประกาศได้";
    }
}
