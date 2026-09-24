using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลตัดสินของด่าน "เปิดรับสมัคร" ของแพลตฟอร์ม</summary>
/// <param name="Allowed">true = ทำต่อได้</param>
/// <param name="MayCreateCompany">true = เส้นนี้สร้างบริษัทใหม่ให้ได้ (false เมื่อผ่านเข้ามาด้วยคำเชิญขณะปิดรับสมัคร
/// — ผู้ใช้เข้าบริษัทที่เชิญเท่านั้น ห้ามงอกบริษัท stub)</param>
/// <param name="Message">ข้อความไทยถึงผู้ใช้เมื่อไม่ผ่าน</param>
public readonly record struct RegistrationDecision(bool Allowed, bool MayCreateCompany, string? Message)
{
    public string? RuleCode => Allowed ? null : RegistrationPolicy.RuleCode;

    /// <summary>403 เมื่อไม่ผ่าน — ผู้ดูแลแพลตฟอร์มปิดทางเข้านี้ไว้ (ไม่ใช่ข้อมูลที่ผู้ใช้กรอกผิด)</summary>
    public int StatusCode => Allowed ? 200 : 403;
}

/// <summary>
/// **ด่าน "เปิดรับสมัครสมาชิก" (<c>SiteSettings.RegistrationEnabled</c>) ตัวเดียวของทุกทางที่สร้างผู้ใช้/บริษัทใหม่** —
/// ผลตรวจ S-08 (รอบ 193)
///
/// <para>═══ บั๊กจริง ═══ แอดมินปิดรับสมัครแล้ว มีแค่ <c>register.html</c> ที่ซ่อนฟอร์ม · ฝั่ง server
/// (<c>AuthService.RegisterAsync</c> · สมัครผ่าน SSO ใน <c>SsoLoginAsync</c> · <c>CompanyService.CreateAsync</c>) ไม่ตรวจเลย
/// ⇒ ยิง API ตรงยังสร้างบัญชี + บริษัทได้ · และหน้าเว็บเองก็ซ่อนฟอร์ม<b>แม้มาจากลิงก์คำเชิญ</b> ⇒ คนที่ถูกเชิญเข้าบริษัท
/// ที่มีอยู่สมัครไม่ได้</para>
///
/// <para>═══ กติกา ═══
/// (1) ไม่มีแถว <c>SiteSettings</c> = เปิด (ตรงกับ entity default <c>true</c> และ <c>/api/site/landing</c> ที่ตอบ <c>?? true</c>) ·
/// (2) ปิด + มีคำเชิญที่ยังใช้ได้<b>ของอีเมลนี้</b> = สมัครได้ แต่<b>ห้ามสร้างบริษัท</b> (เข้าบริษัทที่เชิญเท่านั้น) ·
/// (3) ปิด + ไม่มีคำเชิญ = 403 พร้อมทางไปต่อ · (4) เปิดบริษัทใหม่ขณะปิด = ผู้ดูแลแพลตฟอร์มเท่านั้น</para>
/// </summary>
public static class RegistrationPolicy
{
    public const string RuleCode = "PLATFORM-REGISTRATION-CLOSED";

    public const string ClosedMessage =
        "ระบบปิดรับสมัครสมาชิกใหม่ชั่วคราว — ถ้าคุณได้รับคำเชิญเข้าบริษัท ให้สมัครผ่านลิงก์ในอีเมลคำเชิญ "
        + "(ด้วยอีเมลเดียวกับที่ถูกเชิญ) · ถ้ามีบัญชีอยู่แล้ว เข้าสู่ระบบได้ตามปกติ";

    public const string NewCompanyClosedMessage =
        "ระบบปิดรับการเปิดบริษัทใหม่ชั่วคราว (ผู้ดูแลแพลตฟอร์มปิดรับสมัครไว้) — บริษัทที่มีอยู่ใช้งานได้ตามปกติ · "
        + "ติดต่อผู้ดูแลระบบหากต้องการเปิดบริษัทเพิ่ม";

    /// <summary>เปิดรับสมัครอยู่ไหม · null = ไม่มีแถวค่าตั้งแพลตฟอร์ม = เปิด (ค่าเริ่มต้นของระบบ)</summary>
    public static bool IsOpen(bool? registrationEnabled) => registrationEnabled != false;

    /// <summary>คำเชิญใบนี้ยังใช้ได้กับอีเมลนี้ไหม — <b>predicate ตัวเดียว</b>ที่ทั้งด่านนี้และ
    /// <c>AuthService.ConsumeInvitationAsync</c> ใช้ (ด่านบอกว่า "ผ่านด้วยคำเชิญ" ต้องแปลว่าคำเชิญถูกใช้ได้จริง)</summary>
    public static bool IsInvitationUsable(
        InvitationStatus status, DateTime expiresAtUtc, string? invitedEmail, string? email, DateTime nowUtc)
        => status == InvitationStatus.Pending
           && expiresAtUtc > nowUtc
           && !string.IsNullOrWhiteSpace(email)
           && string.Equals(invitedEmail?.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>สร้าง<b>บัญชีผู้ใช้ใหม่</b> (สมัครด้วยฟอร์ม · สมัครผ่าน SSO) ·
    /// <paramref name="hasUsableInvitation"/> = ผลของ <see cref="IsInvitationUsable"/> กับคำเชิญที่แนบมา</summary>
    public static RegistrationDecision EvaluateNewAccount(bool? registrationEnabled, bool hasUsableInvitation)
    {
        if (IsOpen(registrationEnabled)) return new RegistrationDecision(true, true, null);
        return hasUsableInvitation
            ? new RegistrationDecision(true, false, null)
            : new RegistrationDecision(false, false, ClosedMessage);
    }

    /// <summary>ผู้ใช้ที่มีบัญชีอยู่แล้วเปิด<b>บริษัทใหม่</b> (วิซาร์ด / "เพิ่มบริษัท") ·
    /// ผู้ดูแลแพลตฟอร์ม (คนที่ปิดสวิตช์เอง) ยังเปิดให้ลูกค้าได้</summary>
    public static RegistrationDecision EvaluateNewCompany(bool? registrationEnabled, bool actorIsPlatformAdmin)
        => IsOpen(registrationEnabled) || actorIsPlatformAdmin
            ? new RegistrationDecision(true, true, null)
            : new RegistrationDecision(false, false, NewCompanyClosedMessage);
}
