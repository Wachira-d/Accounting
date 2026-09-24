using Accounting.Helpers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Accounting.Filters;

/// <summary>
/// **action นี้ต้องทำโดยคนที่ล็อกอิน — คำขอที่ยืนยันตัวด้วย API key (acc_/int_) ได้ 403** (ฝ่ายค้านรอบ 193 · W-C1/W-C2/W-C3)
///
/// <para>ใช้กับงาน "เปลี่ยนนโยบาย/ข้อมูลรับรอง · ทำลายหลักฐานถาวร · การเงินของ subscription" ซึ่งด่านสิทธิ์ปกติ
/// (<c>RequirePermission</c>) ปล่อยผ่านเสมอเมื่อตัวตนเป็นเจ้าของ — และคีย์ <c>acc_</c> ทุกดอกถือตัวตนเจ้าของผู้ออกคีย์ ·
/// คีย์ <c>int_</c> รุ่นเก่าสวมเจ้าของได้ด้วย <c>X-Acting-User</c> ⇒ ด่านสิทธิ์อย่างเดียวไม่กันคีย์</para>
///
/// <para>ตัวตัดสินอยู่ที่ <see cref="OwnerActionGuard"/> ตัวเดียว (ไฟล์นี้แค่ต่อสายเข้า MVC) · เป็น authorization filter จึงปฏิเสธ
/// ก่อน model binding · <c>tools/owner_action_wiring_check.py</c> ล็อกว่า action ที่ระบุต้องมี attribute นี้</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RejectApiKeyAttribute : Attribute, IAuthorizationFilter
{
    /// <summary>ชื่องานในข้อความ 403 (เช่น "ลบเอกสารถาวร")</summary>
    public string Verb { get; }

    public RejectApiKeyAttribute(string verb = "งานนี้") { Verb = verb; }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (OwnerActionGuard.DenyResult(context.HttpContext, Verb) is { } deny)
            context.Result = deny;
    }
}
