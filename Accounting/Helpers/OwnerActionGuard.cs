using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Accounting.Helpers;

/// <summary>
/// **ด่าน "งานระดับเจ้าของต้องทำโดยคนที่ล็อกอิน ไม่ใช่ API key" ตัวเดียวของระบบ** — ฝ่ายค้านรอบ 193 ข้อ C1
///
/// <para>═══ ช่องโหว่จริง (C1) ═══ คีย์ integration รุ่นเก่าในช่วงผ่อนผัน (หรือคีย์ที่ผูกกับเจ้าของ) ส่ง
/// <c>X-Acting-User: &lt;อีเมลเจ้าของ&gt;</c> ⇒ NameIdentifier = เจ้าของ ⇒ ผ่าน <c>CompanyService.EnsureOwnerAccessAsync</c>
/// (ดูแค่ role) ⇒ <c>PUT settings</c> เปิด <c>EnableApiAccess</c> ⇒ <c>POST settings/api-keys</c> ออกคีย์ <c>acc_</c>
/// สิทธิ์เต็ม ไม่มีวันหมดอายุ ⇒ หลุดทั้ง "วันเลิกใช้" และ "ขอบเขตสิทธิ์" ของคำตัดสินข้อ 37 · ทางเชิญสมาชิก/เปลี่ยนบทบาท
/// เป็นช่องเดียวกัน. <c>IntegrationController.RequireOwnerAsync</c> ปิดไว้แค่ทาง <c>int_</c> (มีสำเนาเช็กของตัวเอง)</para>
///
/// <para>═══ กติกา ═══ งานที่ "ให้สิทธิ์/เปลี่ยนนโยบาย/ปิดงวด" (ออก-หมุน-เพิกถอนคีย์ · เชิญ/เปลี่ยนบทบาท/ถอดสมาชิก ·
/// role/permission · webhook · ค่าตั้งเจ้าของ · ปิดงวด/ปิดปี) ต้องมาจากคนที่ล็อกอินด้วย JWT เท่านั้น — คีย์ทุกชนิด
/// (<c>acc_</c> / <c>int_</c>) ถูกปฏิเสธ<b>แม้ตัวตนที่คีย์ถืออยู่จะเป็นเจ้าของจริง</b> (คีย์ <c>acc_</c> ถือ
/// <c>CreatedByUserId</c> = เจ้าของที่ออกคีย์เสมอ) · ทางไปต่อ: เจ้าของเข้าสู่ระบบบนเว็บแล้วทำเอง</para>
///
/// <para>ตัวบอกว่าเป็นคีย์: <c>Items["IsApiKeyAuth"]</c> (ApiKeyMiddleware ตั้งทั้งสองชนิด) <b>หรือ</b> claim
/// <c>AuthMethod</c> = <c>ApiKey</c>/<c>IntegrationKey</c> — ดูสองที่เพื่อไม่ให้ด่านหายเงียบเมื่อมีคนย้ายการตั้ง Items</para>
/// </summary>
public static class OwnerActionGuard
{
    public const string RuleCode = "OWNER-ACTION-NO-API-KEY";

    /// <summary>ชื่อ claim/ค่าที่ ApiKeyMiddleware mint ให้ identity ของคีย์</summary>
    public const string AuthMethodClaim = "AuthMethod";

    /// <summary>ข้อความ 403 — บอกทั้งสาเหตุและทางไปต่อ</summary>
    public static string DeniedMessage(string? verb = null)
        => (string.IsNullOrWhiteSpace(verb) ? "งานนี้" : verb.Trim())
           + "ทำด้วย API key ไม่ได้ — งานระดับเจ้าของ (ออกคีย์ · สมาชิก/บทบาท · ค่าตั้งเจ้าของ · webhook · ปิดงวด) "
           + "ต้องเข้าสู่ระบบเป็นเจ้าของบริษัทบนเว็บแล้วทำเอง";

    /// <summary>request นี้ยืนยันตัวด้วย API key (acc_ หรือ int_) ไหม — pure (เทสต์ได้โดยไม่ต้องมี HttpContext)</summary>
    public static bool IsApiKeyRequest(IDictionary<object, object?>? items, ClaimsPrincipal? user)
    {
        if (items != null && items.TryGetValue("IsApiKeyAuth", out var flag) && flag is true) return true;
        var method = user?.FindFirst(AuthMethodClaim)?.Value;
        return string.Equals(method, "ApiKey", StringComparison.Ordinal)
               || string.Equals(method, "IntegrationKey", StringComparison.Ordinal);
    }

    /// <summary>ตัวห่อสำหรับ controller/service · ไม่มี HttpContext (job/เทสต์) = ไม่ใช่คำขอจากคีย์</summary>
    public static bool IsApiKeyRequest(HttpContext? ctx)
        => ctx != null && IsApiKeyRequest(ctx.Items, ctx.User);

    /// <summary>โยน <see cref="BusinessRuleException"/> 403 เมื่อเป็นคำขอจากคีย์ — ใช้ในชั้น service
    /// (<c>CompanyService.EnsureOwnerAccessAsync</c> · <c>RolePermissionService</c>)</summary>
    public static void EnsureNotApiKey(HttpContext? ctx, string? verb = null)
    {
        if (IsApiKeyRequest(ctx))
            throw new BusinessRuleException(DeniedMessage(verb), RuleCode, 403);
    }
}
