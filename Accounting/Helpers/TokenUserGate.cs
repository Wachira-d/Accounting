using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ฝ่ายค้านรอบ 214 S4 (คำตัดสินข้อ 173 ฑ) — access token (JWT) ของบัญชีที่ถูกลบตามคำขอ/ปิดใช้งานต้องใช้ไม่ได้<b>ทันที</b>
/// (เดิมใช้ได้จนหมดอายุ เพราะ JwtBearer ไม่มี OnTokenValidated) · ตัวตัดสินเดียว (pure) — <c>Program.cs</c> OnTokenValidated ค้นแถวผู้ใช้
/// ด้วย PK ต่อคำขอ (ไม่มี static cache — CLAUDE.md §D) แล้วเรียกที่นี่
/// <list type="bullet">
/// <item>ไม่พบแถวผู้ใช้ ⇒ <b>ไม่ตัดสิน</b> (null) — token ชนิดอื่นที่ subject ไม่ใช่ User (เช่น ลูกค้าหน้าร้าน) ไม่ถูกกระทบ</item>
/// <item>ถูกลบ (IsDeleted) หรือ <see cref="UserStatus.Inactive"/> (ลบตามคำขอ ม.33 · แอดมินปิดบัญชี) ⇒ ปฏิเสธ</item>
/// <item><see cref="UserStatus.Suspended"/>/PendingVerification ⇒ คงพฤติกรรมเดิม (ไม่ตัดสินที่ชั้นนี้ — ระงับมีด่านของมันเอง)</item>
/// </list>
/// </summary>
public static class TokenUserGate
{
    public const string RejectMessage = "บัญชีนี้ถูกปิดใช้งานหรือถูกลบแล้ว — เข้าสู่ระบบไม่ได้";

    public static string? Reject(bool userExists, UserStatus status, bool isDeleted)
    {
        if (!userExists) return null;
        if (isDeleted || status == UserStatus.Inactive) return RejectMessage;
        return null;
    }
}
