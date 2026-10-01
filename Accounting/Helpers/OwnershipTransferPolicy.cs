using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// บทบาท "ผู้ดูแลแพลตฟอร์ม (support)" + เส้นโอนความเป็นเจ้าของ — <b>ตัวตัดสินตัวเดียว</b> (รอบ 201 ทีม PL · C-4 · คำตัดสินข้อ 77 · review193-r2-W P-4)
///
/// <para>═══ ที่มา ═══ แอดมินแพลตฟอร์มที่เปิดบริษัทให้ลูกค้า (ช่วยตั้งค่า) ได้แถว <c>CompanyUser</c> บทบาท <b>Owner</b> ถาวร
/// (<c>CompanyService.CreateAsync</c>) ⇒ โผล่เป็น "เจ้าของ" ในหน้าทีมของลูกค้า · ได้สิทธิ์ระดับเจ้าของ (คีย์ API · ปิดงวด · สมาชิก) ในข้อมูลลูกค้า
/// แม้วันหนึ่งถูกถอดสิทธิ์แอดมินแพลตฟอร์มแล้ว · ไม่มีทางส่งมอบบริษัทให้ลูกค้าที่บันทึกร่องรอย</para>
///
/// <para>กติกา:</para>
/// <list type="bullet">
/// <item>ผู้สร้างบริษัทที่เป็นแอดมินแพลตฟอร์ม ⇒ <see cref="UserRole.PlatformSupport"/> (ไม่ใช่ Owner) · ผู้ใช้ทั่วไป ⇒ Owner (เดิม)</item>
/// <item>PlatformSupport <b>ไม่ผ่านด่านเจ้าของ</b> (บทบาทนี้ไม่อยู่ใน <c>OwnerGateDecision</c>/<c>EnsureOwnerAccessAsync</c>) · สิทธิ์ตั้งค่าเริ่มระบบผ่าน
/// <see cref="SupportDefaultKeys"/> (ไม่รวมเงินเดือน/ข้อมูลส่วนบุคคล/สมาชิก/คีย์ API)</item>
/// <item>โอนความเป็นเจ้าของ: ผู้เรียก = แอดมินแพลตฟอร์ม (claim) หรือสมาชิก Owner/SystemAdmin/PlatformSupport · ปฏิเสธคำขอจาก API key ·
/// ผู้รับต้องมีบัญชีในระบบแล้ว (ไม่สร้างบัญชีให้) · ผู้รับเป็น Owner อยู่แล้ว = ไม่มีอะไรต้องทำ (409)</item>
/// <item>บทบาท PlatformSupport ให้ผ่านทีม/คำเชิญไม่ได้ (<see cref="AssignableByMembers"/>) — เกิดได้ทางเดียวคือแอดมินแพลตฟอร์มสร้างบริษัท</item>
/// </list>
/// </summary>
public static class OwnershipTransferPolicy
{
    public enum Outcome { Allow, DenyApiKey, DenyNotAllowed, DenyTargetMissing, DenyTargetAlreadyOwner }

    public const string RuleCode = "PLATFORM-OWNERSHIP-TRANSFER";

    /// <summary>บทบาทของผู้สร้างบริษัท</summary>
    public static UserRole CreatorRole(bool creatorIsPlatformAdmin)
        => creatorIsPlatformAdmin ? UserRole.PlatformSupport : UserRole.Owner;

    /// <summary>บทบาทที่สมาชิก (เจ้าของ) ตั้งให้คนอื่นผ่านหน้าทีม/คำเชิญได้ — PlatformSupport ไม่ได้</summary>
    public static bool AssignableByMembers(UserRole role) => role != UserRole.PlatformSupport;

    public static Outcome Decide(bool isApiKeyRequest, bool isPlatformAdmin, UserRole? callerRole,
        bool targetUserExists, UserRole? targetCurrentRole)
    {
        if (isApiKeyRequest) return Outcome.DenyApiKey;
        if (!isPlatformAdmin && callerRole is not (UserRole.Owner or UserRole.SystemAdmin or UserRole.PlatformSupport))
            return Outcome.DenyNotAllowed;
        if (!targetUserExists) return Outcome.DenyTargetMissing;
        if (targetCurrentRole == UserRole.Owner) return Outcome.DenyTargetAlreadyOwner;
        return Outcome.Allow;
    }

    /// <summary>HTTP status ของผลปฏิเสธ</summary>
    public static int StatusCode(Outcome o) => o switch
    {
        Outcome.DenyApiKey or Outcome.DenyNotAllowed => 403,
        Outcome.DenyTargetMissing => 404,
        Outcome.DenyTargetAlreadyOwner => 409,
        _ => 200,
    };

    /// <summary>ข้อความถึงผู้ใช้ (ไทย · บอกทางไปต่อ)</summary>
    public static string Message(Outcome o) => o switch
    {
        Outcome.DenyApiKey => "โอนความเป็นเจ้าของผ่านคีย์ API ไม่ได้ — ต้องทำบนเว็บโดยผู้ที่ล็อกอิน",
        Outcome.DenyNotAllowed => "โอนความเป็นเจ้าของได้เฉพาะเจ้าของบริษัทหรือผู้ดูแลแพลตฟอร์มที่ตั้งค่าบริษัทนี้ให้",
        Outcome.DenyTargetMissing => "ไม่พบบัญชีผู้ใช้ของอีเมลนี้ — ให้ลูกค้าสมัครใช้งานด้วยอีเมลนี้ก่อน แล้วโอนอีกครั้ง",
        Outcome.DenyTargetAlreadyOwner => "ผู้ใช้นี้เป็นเจ้าของบริษัทอยู่แล้ว",
        _ => "โอนความเป็นเจ้าของแล้ว",
    };

    /// <summary>คีย์สิทธิ์ที่ PlatformSupport ได้โดยปริยาย — งานตั้งค่าเริ่มระบบให้ลูกค้า (ค่าตั้งบริษัท · ผังบัญชี · ผู้ติดต่อ · สินค้า · ดูบัญชี/แดชบอร์ด) ·
    /// <b>ไม่รวม</b> เงินเดือน · ข้อมูลส่วนบุคคล (Pii.View) · สมาชิก/บทบาท (Users/Roles.Manage) · อนุมัติเอกสาร — งานเจ้าของ/นักบัญชีของลูกค้า</summary>
    public static readonly IReadOnlySet<string> SupportDefaultKeys = new HashSet<string>
    {
        Accounting.Models.Constants.PermissionKeys.CompanySettingsEdit,
        Accounting.Models.Constants.PermissionKeys.ChartOfAccountsEdit,
        Accounting.Models.Constants.PermissionKeys.ContactEdit,
        Accounting.Models.Constants.PermissionKeys.ProductEdit,
        Accounting.Models.Constants.PermissionKeys.AccountingView,
        Accounting.Models.Constants.PermissionKeys.ReportsDashboard,
    };
}
