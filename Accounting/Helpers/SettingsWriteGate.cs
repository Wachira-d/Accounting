namespace Accounting.Helpers;

/// <summary>
/// รอบ 258 (A-H1 · B-H1 · A-H3): ตัวตัดสินเดียวของ "คำสั่งเขียนที่ส่งข้อความ/ตั้งการส่งในนามบริษัท" —
/// กฎตารางส่ง (สร้าง/แก้/ลบ) · คิวอีเมล/คิว LINE (ส่งอีกครั้ง/ยกเลิก) · ตารางแจ้งเตือนของบริษัท
///
/// <para>═══ ที่มา ═══ เส้นเหล่านี้เคยมีแค่ <c>[Authorize]</c> ⇒ สมาชิกสิทธิ์ดูอย่างเดียวใส่ <c>BccEmails</c> เป็นอีเมลตัวเอง
/// แล้วได้สำเนาใบกำกับ/สลิปเงินเดือน/50 ทวิ ทุกใบ (PDPA ม.37) ขณะที่คิว LINE ในไฟล์เดียวกันมีด่าน = คู่สมมาตรที่แก้ฝั่งเดียว</para>
///
/// <para>═══ กติกา ═══ (1) งานที่ "ตั้งกฎ" (rejectApiKey) ทำด้วย API key ไม่ได้ — คีย์ <c>acc_</c> ถือตัวตนเจ้าของผู้ออกคีย์ จึงผ่านด่านสิทธิ์เสมอ
/// (2) ต้องถือ <c>CompanySettings.Edit</c> · pure — controller ส่งข้อเท็จจริงเข้ามา</para>
/// </summary>
public static class SettingsWriteGate
{
    public const string RuleCode = "SETTINGS-WRITE-GATE";

    /// <summary>ผลตัดสิน — <c>Message</c> null = ผ่าน</summary>
    public sealed record Decision(bool Allowed, string? Message, string? RuleCode);

    private static readonly Decision Pass = new(true, null, null);

    /// <param name="verb">ชื่องานในข้อความ (เช่น "แก้กฎตารางส่ง")</param>
    /// <param name="isApiKeyRequest">คำขอนี้ยืนยันตัวด้วย API key ไหม (OwnerActionGuard.IsApiKeyRequest)</param>
    /// <param name="rejectApiKey">งานนี้ห้ามทำด้วยคีย์ไหม (ตั้ง/ลบกฎ · ตารางแจ้งเตือน = ใช่ · ส่งอีกครั้ง/ยกเลิกคิว = ไม่)</param>
    /// <param name="hasSettingsEdit">ผู้เรียกถือ <c>CompanySettings.Edit</c> ไหม</param>
    public static Decision Decide(string? verb, bool isApiKeyRequest, bool rejectApiKey, bool hasSettingsEdit)
    {
        var what = string.IsNullOrWhiteSpace(verb) ? "งานนี้" : verb.Trim();
        if (rejectApiKey && isApiKeyRequest)
            return new Decision(false, OwnerActionGuard.DeniedMessage(what), OwnerActionGuard.RuleCode);
        if (!hasSettingsEdit)
            return new Decision(false,
                $"ไม่มีสิทธิ์{what} (ต้องการ CompanySettings.Edit) — ขอให้เจ้าของบริษัทเปิดสิทธิ์นี้ให้บทบาทของคุณที่หน้า “บทบาทและสิทธิ์” (/pages/roles.html)",
                RuleCode);
        return Pass;
    }

    /// <summary>ตัวห่อของ controller: อ่านข้อเท็จจริง (คีย์ไหม · ถือสิทธิ์ไหม) แล้วตัดสิน · ไม่มีบริการสิทธิ์ = ปฏิเสธ (ปิดทางปลอดภัย) ·
    /// คีย์ที่ถูกปฏิเสธไม่ถามสิทธิ์ (ตัวตนของคีย์ = เจ้าของเสมอ)</summary>
    public static async Task<Decision> EvaluateAsync(Accounting.Services.Interfaces.IPermissionService? permissions,
        Microsoft.AspNetCore.Http.HttpContext? http, Guid companyId, string? verb, bool rejectApiKey)
    {
        var isKey = OwnerActionGuard.IsApiKeyRequest(http);
        var allowed = false;
        if (!(rejectApiKey && isKey) && permissions != null && http?.User != null)
            allowed = await permissions.HasPermissionAsync(companyId, JwtHelper.GetUserIdFromClaims(http.User),
                Accounting.Models.Constants.PermissionKeys.CompanySettingsEdit);
        return Decide(verb, isKey, rejectApiKey, allowed);
    }
}
