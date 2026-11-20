using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 277 (งาน B) — กติกาคำเชิญเข้าบริษัท <b>ตัวเดียว</b> ของทุกทางเข้า (หน้าจัดการทีม · ลิงก์ในอีเมล · สมัครด้วยฟอร์ม · สมัคร/ล็อกอินด้วย
/// Google/LINE/Facebook · ล็อกอินแล้วกดตอบรับ · แอดมินส่งซ้ำ) — ผู้ใช้: <c>Services/Implementations/CompanyInvitationService</c>
///
/// <para>═══ บั๊กจริง (เจ้าของรายงาน 2026-11-20) ═══ (1) กดเชิญแล้วหน้าจัดการทีมไม่มีแถวอะไรเลยจนกว่าผู้รับสมัครเสร็จ (คำเชิญค้างไม่ถูกแสดง ·
/// ส่งซ้ำ/ยกเลิกไม่ได้) · (2) หน้าทีมบอก "ผู้ใช้ต้องสมัคร Next Acc ก่อน" + หน้าสมัครยังโชว์ตัวเลือกแพ็กเกจ ⇒ ผู้รับเข้าใจว่าต้องเปิดบริษัทใหม่ ·
/// สมัครด้วยอีเมลอื่น/ไม่ผ่านลิงก์ ⇒ บัญชีไม่มีบริษัท ⇒ ถูกพาไปหน้า "สร้างบริษัท" · (3) ผู้ที่มีบัญชีอยู่แล้วถูกยัดเข้าบริษัททันทีโดยไม่ได้ตอบรับ ·
/// (4) token ดิบเก็บในฐาน</para>
///
/// <para>═══ กติกา ═══ token สุ่ม 32 ไบต์ (base64url) เก็บเฉพาะ SHA-256 · อายุ 7 วัน · ใช้ครั้งเดียว (สถานะ Pending เท่านั้น) · อีเมลของผู้ตอบรับ
/// ต้องตรงอีเมลที่เชิญ (ไม่สนตัวพิมพ์) · คำเชิญค้าง (Pending ยังไม่หมดอายุ) กินที่นั่ง · ไม่เลือกบทบาท ⇒ เข้าร่วมแบบ "ยังไม่มีสิทธิ์"</para>
/// </summary>
public static class CompanyInvitationPolicy
{
    public const string RuleCode = "TEAM-INVITATION";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>token ดิบสำหรับลิงก์ (256 บิต · base64url ไม่มี padding)</summary>
    public static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>ค่าที่เก็บในฐาน — SHA-256 ของ UTF-8 เป็น hex ตัวเล็ก · <b>สูตรเดียว</b>กับ migration รอบ 277
    /// (<c>encode(sha256(convert_to(token,'UTF8')),'hex')</c>) · token ว่าง = null (ไม่มีวันตรงแถวใด)</summary>
    public static string? HashToken(string? rawToken)
    {
        var t = rawToken?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(t))).ToLowerInvariant();
    }

    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>ผลตรวจคำเชิญตอนตอบรับ/สมัคร</summary>
    public enum Verdict { Ok, NotFound, AlreadyUsed, Cancelled, Expired, EmailMismatch }

    public static Verdict Check(InvitationStatus? status, DateTime? expiresAtUtc, string? invitedEmail, string? actorEmail, DateTime nowUtc)
    {
        if (status is null || expiresAtUtc is null) return Verdict.NotFound;
        if (status == InvitationStatus.Accepted) return Verdict.AlreadyUsed;
        if (status == InvitationStatus.Cancelled) return Verdict.Cancelled;
        if (status == InvitationStatus.Expired || expiresAtUtc.Value <= nowUtc) return Verdict.Expired;
        if (status != InvitationStatus.Pending) return Verdict.NotFound;
        if (string.IsNullOrWhiteSpace(actorEmail) || NormalizeEmail(invitedEmail) != NormalizeEmail(actorEmail)) return Verdict.EmailMismatch;
        return Verdict.Ok;
    }

    /// <summary>สถานะที่แสดง (หน้ารับคำเชิญ · หน้าจัดการทีม) — Pending ที่เลยเวลา = Expired แม้แถวยังไม่ถูกพลิก</summary>
    public static InvitationStatus DisplayStatus(InvitationStatus status, DateTime expiresAtUtc, DateTime nowUtc)
        => status == InvitationStatus.Pending && expiresAtUtc <= nowUtc ? InvitationStatus.Expired : status;

    /// <summary>คำเชิญใบนี้ "กินที่นั่ง" ไหม — Pending ที่ยังไม่หมดอายุ (คำเชิญหมดอายุไม่ล็อกที่นั่งถาวร · ตัวนับเดียวของ QuotaGateService/CompanyService)</summary>
    public static bool HoldsSeat(InvitationStatus status, DateTime expiresAtUtc, DateTime nowUtc)
        => status == InvitationStatus.Pending && expiresAtUtc > nowUtc;

    /// <summary>ข้อความไทยถึงผู้ใช้ต่อผลตรวจ (บอกทางไปต่อ) · Ok = null</summary>
    public static string? Message(Verdict v, string? invitedEmail = null, string? actorEmail = null) => v switch
    {
        Verdict.Ok => null,
        Verdict.NotFound => "ไม่พบคำเชิญ หรือลิงก์ไม่ถูกต้อง (ถ้าเจ้าของกด “ส่งซ้ำ” ลิงก์เดิมจะใช้ไม่ได้ — เปิดลิงก์จากอีเมลฉบับล่าสุด)",
        Verdict.AlreadyUsed => "คำเชิญนี้ถูกตอบรับไปแล้ว — เข้าสู่ระบบด้วยบัญชีที่ตอบรับเพื่อใช้งาน",
        Verdict.Cancelled => "คำเชิญนี้ถูกยกเลิกแล้ว — ติดต่อเจ้าของบริษัทหากต้องการคำเชิญใหม่",
        Verdict.Expired => "คำเชิญหมดอายุแล้ว — ขอให้เจ้าของบริษัทกด “ส่งซ้ำ” ที่หน้าจัดการทีม",
        Verdict.EmailMismatch => $"คำเชิญนี้ส่งถึง {invitedEmail} แต่คุณเข้าสู่ระบบ/สมัครด้วย {actorEmail} — กรุณาใช้อีเมลที่ถูกเชิญ "
                                 + "(หรือขอให้เจ้าของเชิญอีเมลที่คุณใช้)",
        _ => "คำเชิญนี้ใช้ไม่ได้",
    };

    public static int StatusCode(Verdict v) => v switch
    {
        Verdict.Ok => 200,
        Verdict.NotFound => 404,
        Verdict.EmailMismatch => 403,
        _ => 409,
    };

    /// <summary>สมาชิกที่เกิดจากคำเชิญ: ไม่เลือกบทบาท ⇒ ยังไม่มีสิทธิ์ (<c>AccessPending</c>) + บทบาทมาตรฐานต่ำสุด (ผู้ดู) ·
    /// เลือกแล้ว ⇒ บทบาทนั้น · บทบาทระดับแพลตฟอร์ม/เจ้าของตรวจที่ <c>OwnershipTransferPolicy</c> ก่อนถึงตรงนี้</summary>
    public static (UserRole Role, bool AccessPending) MembershipFor(bool roleChosen, UserRole role)
        => roleChosen ? (role, false) : (UserRole.Viewer, true);

    /// <summary>ป้ายบทบาท (อีเมล · หน้ารับคำเชิญ · หน้าจัดการทีม) — หน้าเว็บไม่มีตารางป้ายของตัวเอง</summary>
    public static string RoleLabel(bool roleChosen, UserRole role, string? customRoleName = null)
    {
        if (!roleChosen) return "ยังไม่กำหนดบทบาท (เจ้าของจะตั้งสิทธิ์หลังคุณตอบรับ)";
        var b = role switch
        {
            UserRole.Owner => "เจ้าของ",
            UserRole.Accountant => "นักบัญชี",
            UserRole.Staff => "พนักงาน",
            UserRole.Auditor => "ผู้ตรวจสอบ",
            UserRole.Viewer => "ผู้ดู",
            UserRole.ExternalAccountant => "นักบัญชีภายนอก",
            UserRole.PlatformSupport => "ผู้ดูแลแพลตฟอร์ม (support)",
            _ => role.ToString(),
        };
        return string.IsNullOrWhiteSpace(customRoleName) ? b : $"{b} · {customRoleName}";
    }
}
