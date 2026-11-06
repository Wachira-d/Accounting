namespace Accounting.Helpers;

/// <summary>
/// **โทเคนพอร์ทัลลูกค้า — กติกาตัวเดียว** (รอบ 255 WP-12B ฝ่ายค้าน H1/M9) · pure
/// <para>เดิมเส้น <c>[Authorize]</c> ของพอร์ทัลใช้ scheme เดียวของระบบ (JwtBearer ของพนักงาน) แต่โทเคนพอร์ทัลเป็น HMAC base64 ไม่ใช่ JWT ⇒ ตอบ 401 ทุกครั้ง ·
/// ตอนนี้: scheme แยก <see cref="Scheme"/> (<c>PortalAuthenticationHandler</c>) — โทเคนพนักงานใช้กับเส้นพอร์ทัลไม่ได้ · โทเคนพอร์ทัลใช้กับเส้นพนักงานไม่ได้ (JwtBearer อ่านไม่ออก) ·
/// โทเคนมีชนิด access/refresh (ช่องที่ 5) · เส้น API รับเฉพาะ access · /refresh รับเฉพาะ refresh · ทุกคำขอตรวจสิทธิ์พอร์ทัลยังเปิดอยู่ + บริษัทในเส้นทางต้องตรงโทเคน ·
/// โทเคนรูปเก่า (4 ช่อง ไม่มีชนิด) = ปฏิเสธ (ไม่เคยใช้ได้จริงอยู่แล้ว — DECISIONS 255-WP12B-13)</para>
/// </summary>
public static class PortalTokenPolicy
{
    public const string Scheme = "Portal";
    public const string TypeAccess = "a";
    public const string TypeRefresh = "r";
    public const string ClaimContactId = "portal_contact_id";
    public const string ClaimCompanyId = "portal_company_id";
    public const string ClaimAccessId = "portal_access_id";

    public static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(2);
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);

    public const string UnauthorizedMessage = "เซสชันพอร์ทัลหมดอายุหรือไม่ถูกต้อง — กรุณาเข้าสู่ระบบพอร์ทัลใหม่";

    private static readonly string[] Placeholders = { "changeme", "secret", "your-secret-key", "portal-signing-key", "CHANGE_ME" };

    /// <summary>รอบ 255 ฝ่ายค้าน LOW (กฎ #4 C): กุญแจลงนามโทเคนพอร์ทัลใช้ได้ไหมใน production · null = ใช้ได้ · ว่าง/สั้นกว่า 32/placeholder = ปฏิเสธ (fail-fast ที่ Program.cs)</summary>
    public static string? SigningKeyProblem(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "PORTAL_SIGNING_KEY ไม่ได้ตั้ง — ตั้ง env var (≥32 อักษร สุ่มจริง) ใน production";
        if (key.Trim().Length < 32 || Placeholders.Any(p => key.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return "PORTAL_SIGNING_KEY เป็นค่า placeholder/สั้นเกินไป — ตั้งค่าสุ่มจริง ≥32 อักษรใน production";
        return null;
    }

    /// <summary>ผลถอดโทเคนผ่านกติกาสำหรับคำขอ API ไหม · null = ผ่าน · ข้อความ = เหตุ (บันทึกฝั่งเซิร์ฟเวอร์ — ผู้เรียกเห็นข้อความกลาง)</summary>
    public static string? AccessProblem(string tokenType, Guid tokenCompanyId, Guid? routeCompanyId, bool accessActive, Guid accessCompanyId)
    {
        if (tokenType != TypeAccess) return "ไม่ใช่โทเคน access";
        if (!accessActive) return "สิทธิ์พอร์ทัลถูกปิด";
        if (accessCompanyId != tokenCompanyId) return "บริษัทของสิทธิ์ไม่ตรงโทเคน";
        if (routeCompanyId is Guid r && r != tokenCompanyId) return "บริษัทในเส้นทางไม่ตรงโทเคน";
        return null;
    }
}
