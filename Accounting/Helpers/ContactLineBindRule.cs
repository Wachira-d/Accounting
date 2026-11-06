using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 254 WP-11 L2 (คำตอบเจ้าของ Q6 ทางลัด · LINE_MULTICHANNEL_DESIGN §3.b) — <b>ตัวตั้งเดียว</b> (pure) ของ "ลิงก์/QR ผูก LINE ของผู้ติดต่อ" ผ่าน OA ของบริษัท
/// <list type="bullet">
/// <item>token ในลิงก์: 32 ไบต์สุ่ม (base64url) · เก็บ SHA-256 (<see cref="TokenHash"/>) · อายุ <see cref="InviteLifetime"/> · ใช้ครั้งเดียว</item>
/// <item>รหัสในแชต: รูปแบบ/hash/อายุเดียวกับ <see cref="CustomerLineLinkRule"/> (<c>ผูก LN-XXXXXXXX</c>) — ไม่มีรูปแบบรหัสที่สอง</item>
/// <item>ความยินยอม (PDPA ม.19): ผู้รับกดยินยอมเองบนมือถือ <b>ก่อน</b>ได้รหัส · หลักฐาน <c>PdpaConsentRecord</c> (purpose <see cref="ConsentPurpose"/>) ·
/// ถอนได้ด้วยการพิมพ์ <see cref="IsStop"/> ในแชต OA ของบริษัท (ปลดเฉพาะบริษัทนั้น)</item>
/// </list>
/// </summary>
public static class ContactLineBindRule
{
    public const string RuleCode = "PDPA-CONTACT-LINE-BIND";
    public const string ConsentPurpose = "contact-line-notify";
    public const string ConsentChannel = "web-form:contact-line-join";
    public const string SourceLineInvite = "line-invite";
    public const string UnlinkUser = "user";
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    /// <summary>หน้า public ที่ผู้รับเปิด (ไม่มี token ใน path ของ API log — อยู่ใน query ของหน้า)</summary>
    public static string JoinPath(Guid companyId, string token)
        => $"/pages/contact-line-join.html?c={companyId:D}&t={Uri.EscapeDataString(token)}";

    public static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string TokenHash(string? token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((token ?? "").Trim()))).ToLowerInvariant();

    /// <summary>token รูปแบบที่เราออกเท่านั้น (กัน input ขยะยาว ๆ เข้า query)</summary>
    public static bool LooksLikeToken(string? token)
        => !string.IsNullOrEmpty(token) && token.Length is >= 40 and <= 64 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>คำเชิญยังใช้ได้ไหม (null = ได้) — ข้อความเดียวสำหรับทุกกรณีที่ใช้ไม่ได้ (ไม่บอกคนนอกว่าเป็นเพราะอะไร)</summary>
    public static string? InviteProblem(bool found, DateTime expiresAt, DateTime? usedAt, DateTime? revokedAt, DateTime nowUtc)
        => !found || usedAt != null || revokedAt != null || expiresAt <= nowUtc
            ? "ลิงก์นี้ใช้ไม่ได้แล้ว (หมดอายุ ใช้ไปแล้ว หรือถูกยกเลิก) — ขอลิงก์ใหม่จากผู้ส่งเอกสาร"
            : null;

    /// <summary>ข้อความยินยอมที่แสดง (เวอร์ชัน = hash ของข้อความ — <see cref="CmsDataConsent.Version"/>) · ระบุผู้รับข้อมูลรายใหม่ (LINE) ตาม §6.3</summary>
    public static string ConsentText(string? companyName, string? contactName)
        => $"ข้าพเจ้ายินยอมให้ {(string.IsNullOrWhiteSpace(companyName) ? "บริษัทผู้ส่ง" : companyName!.Trim())} ส่งการแจ้งเอกสาร (ใบแจ้งหนี้ ใบกำกับภาษี "
           + $"ใบเสนอราคา การแจ้งครบกำหนด) ในนามของ {(string.IsNullOrWhiteSpace(contactName) ? "ผู้ติดต่อ" : contactName!.Trim())} มาที่บัญชี LINE ของข้าพเจ้า "
           + "ผ่าน LINE Official Account ของบริษัทนั้น (ผู้ให้บริการ LINE คือ LY Corporation) · ข้อความมีเฉพาะชื่อเอกสาร เลขที่ ยอด วันครบกำหนด และลิงก์ "
           + "(ไม่มีเลขผู้เสียภาษี/ที่อยู่) · ยกเลิกได้ทุกเมื่อโดยพิมพ์ \"หยุด\" ในแชตนั้น หรือบล็อกบัญชี LINE ของบริษัท";

    private static readonly Regex StopRe = new(@"^\s*(?:หยุด|stop|unsubscribe)\s*[.!]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>ข้อความในแชต 1:1 = ขอหยุดรับ (ทั้งข้อความ — "หยุดก่อนนะ" ไม่นับ)</summary>
    public static bool IsStop(string? text) => !string.IsNullOrWhiteSpace(text) && StopRe.IsMatch(text);

    /// <summary>ป้ายของปลายทางจากชื่อโปรไฟล์ LINE (ฝ่ายค้าน M4) — ตัดอักขระควบคุม · ≤ 100 ตัว · ว่าง ⇒ null</summary>
    public static string? ProfileLabel(string? displayName)
    {
        var t = new string((displayName ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (t.Length == 0) return null;
        return "LINE: " + (t.Length > 90 ? t[..90] : t);
    }

    /// <summary>ชื่อแบบปิดบังสำหรับ audit (ตัวแรก + •••) — audit ห้ามเก็บชื่อเต็ม</summary>
    public static string? MaskName(string? name)
    {
        var t = (name ?? "").Trim();
        if (t.StartsWith("LINE: ", StringComparison.Ordinal)) t = t[6..];
        return t.Length == 0 ? null : t[..1] + "•••";
    }

    // ── RoPA (PDPA ม.39 · ฝ่ายค้าน L7) ──
    public const string RopaPurpose = "ส่งการแจ้งเอกสารถึงผู้ติดต่อทาง LINE (LINE OA ของบริษัท)";
    public const string RopaDataCategories = "LineUserId, LineDisplayName, DocumentNumber, Amount, DueDate";
    public const string RopaRetention = "จนผู้รับยกเลิก (พิมพ์ หยุด/บล็อก) หรือลบตามคำขอ · บันทึกการส่งตามอายุเอกสารบัญชี";
    public const string RopaRecipients = "LY Corporation (LINE)";
    public const string RopaTransferNote = "ข้อกำหนดการใช้ LINE Official Account ของ LY Corporation — ต้องยืนยันสถานที่จัดเก็บกับเอกสารทางการ (LINE_OA_TERMS_CHECK)";
    public const string RopaNote = "ระบบสร้างอัตโนมัติเมื่อสร้างลิงก์ผูก LINE ครั้งแรก (รอบ 254) — ผู้ดูแลข้อมูลทบทวนได้ที่หน้า PDPA";

    public static class Replies
    {
        public static string Linked(string? companyName)
            => $"✅ ผูก LINE สำเร็จ — คุณจะได้รับการแจ้งเอกสารจาก {(string.IsNullOrWhiteSpace(companyName) ? "บริษัท" : companyName)} ทางแชตนี้\n"
               + "ไม่ต้องการรับแล้ว พิมพ์ \"หยุด\" ได้ทุกเมื่อ";
        public const string Invalid = "❌ รหัสไม่ถูกต้อง หมดอายุ หรือใช้ไปแล้ว — เปิดลิงก์เดิมแล้วกด \"รับรหัสใหม่\" (หรือขอลิงก์ใหม่จากผู้ส่งเอกสาร)";
        public const string WrongChannel = "❌ บริษัทเปลี่ยนบัญชี LINE ที่ใช้ส่งเอกสารแล้ว — ขอลิงก์ผูกใหม่จากผู้ส่งเอกสาร";
        public static string Stopped(string? companyName)
            => $"ยกเลิกแล้ว — จะไม่ส่งการแจ้งเอกสารของ {(string.IsNullOrWhiteSpace(companyName) ? "บริษัท" : companyName)} มาที่ LINE นี้อีก · "
               + "หยุดเฉพาะทาง LINE: ถ้าบริษัทตั้งให้ส่งทางอีเมลด้วยหรือส่งอีเมลแทนเมื่อ LINE ส่งไม่ได้ คุณจะยังได้รับเอกสารทางอีเมล";
    }
}
