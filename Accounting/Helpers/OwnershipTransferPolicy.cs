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
/// <item>โอนความเป็นเจ้าของ: ผู้เรียก = สมาชิก Owner/SystemAdmin ของบริษัท <b>หรือ</b> PlatformSupport ของบริษัทนั้นที่ <b>ยังเป็นแอดมินแพลตฟอร์มอยู่</b>
/// (ฝ่ายค้าน PL-S1/PL-S2 · คำตัดสินข้อ 105: แอดมินแพลตฟอร์มที่ไม่ได้เป็น support ของบริษัทนั้นโอนไม่ได้ — สิทธิ์แพลตฟอร์มไม่ใช่สิทธิ์ยึดบริษัทลูกค้า ·
/// support ที่ถูกถอดสิทธิ์แอดมินแล้วโอนไม่ได้) · ห้ามโอนให้ตัวเอง (ยกเว้นผู้เรียกเป็น Owner — ซึ่งจะตก 409 อยู่แล้ว) · ปฏิเสธคำขอจาก API key ·
/// ผู้รับต้องมีบัญชีในระบบแล้ว (ไม่สร้างบัญชีให้) · ผู้รับเป็น Owner อยู่แล้ว = ไม่มีอะไรต้องทำ (409)</item>
/// <item>บทบาท PlatformSupport ให้ผ่านทีม/คำเชิญไม่ได้ (<see cref="MayAssign"/>) · SystemAdmin (99) ตั้งได้เฉพาะแอดมินแพลตฟอร์ม (ข้อ 107) — เกิดได้ทางเดียวคือแอดมินแพลตฟอร์มสร้างบริษัท</item>
/// </list>
/// </summary>
public static class OwnershipTransferPolicy
{
    public enum Outcome { Allow, DenyApiKey, DenyNotAllowed, DenyTargetMissing, DenyTargetAlreadyOwner, DenySelf }

    public const string RuleCode = "PLATFORM-OWNERSHIP-TRANSFER";

    /// <summary>บทบาทของผู้สร้างบริษัท</summary>
    public static UserRole CreatorRole(bool creatorIsPlatformAdmin)
        => creatorIsPlatformAdmin ? UserRole.PlatformSupport : UserRole.Owner;

    /// <summary>ตั้งบทบาทนี้ได้ไหม — ตัวตัดสินเดียวของทุกทางตั้งบทบาท (หน้าทีม · คำเชิญ · หน้าแอดมินลูกค้า) · PlatformSupport เกิดได้ทางเดียว
    /// (แอดมินแพลตฟอร์มสร้างบริษัท) · SystemAdmin (99) ตั้งได้เฉพาะแอดมินแพลตฟอร์ม (ข้อ 107) · บทบาทลูกค้าตั้งได้ตามเดิม</summary>
    public static bool MayAssign(UserRole role, bool callerIsPlatformAdmin)
        => role != UserRole.PlatformSupport && (role != UserRole.SystemAdmin || callerIsPlatformAdmin);

    /// <summary>ข้อความปฏิเสธการตั้งบทบาทระดับแพลตฟอร์ม</summary>
    public static string AssignDeniedMessage(UserRole role) => role == UserRole.PlatformSupport
        ? "ตั้งบทบาทผู้ดูแลแพลตฟอร์ม (support) ไม่ได้ — บทบาทนี้เกิดเฉพาะเมื่อผู้ดูแลแพลตฟอร์มเปิดบริษัทให้ลูกค้า (ส่งมอบด้วย \u201Cโอนความเป็นเจ้าของ\u201D)"
        : "ตั้งบทบาทแอดมินระบบได้เฉพาะผู้ดูแลแพลตฟอร์ม — เลือกบทบาทของบริษัท (เจ้าของ/นักบัญชี/พนักงาน/ผู้ตรวจสอบ/ผู้ดู)";

    /// <summary>ฝ่ายค้านรอบสาม P1-2: หน้าแอดมินลูกค้าเปลี่ยนบทบาทสมาชิก — null = ได้ · ตั้งใครเป็น Owner หรือลด Owner ⇒ ต้องผ่าน "โอนความเป็นเจ้าของ"
    /// (ตัวตัดสิน <see cref="Decide"/> · ข้อ 105 · <see cref="Outcome.DenySelf"/>) ไม่ใช่ทางลัดของแอดมิน · บทบาทระดับแพลตฟอร์มตาม <see cref="MayAssign"/></summary>
    public static string? AdminRoleChangeBlock(UserRole current, UserRole requested)
    {
        if (!MayAssign(requested, callerIsPlatformAdmin: true)) return AssignDeniedMessage(requested);
        if (current == requested) return null;
        if (requested == UserRole.Owner || current == UserRole.Owner)
            return "ตั้งหรือลดบทบาทเจ้าของ (Owner) จากหน้านี้ไม่ได้ — ใช้ \u201Cโอนความเป็นเจ้าของ\u201D ที่หน้าทีมของบริษัท "
                   + "(โดยเจ้าของเดิม หรือผู้ดูแล support ของบริษัทที่ยังเป็นแอดมินแพลตฟอร์ม) เพื่อให้ผ่านด่านเดียวกันและมีร่องรอยการโอน";
        return null;
    }

    /// <summary>ฝ่ายค้านรอบสาม P2-2: รับคำเชิญ — ตรวจบทบาทซ้ำตอนรับ (คำเชิญที่ออกก่อนรอบ 201 ยังค้างได้) · ผู้เชิญไม่ใช่/ไม่รู้ว่าเป็นแอดมินแพลตฟอร์ม
    /// ⇒ SystemAdmin ไม่ได้ · PlatformSupport ไม่ได้เสมอ · null = รับได้</summary>
    public static string? InvitationRoleBlock(UserRole role, bool inviterIsPlatformAdmin)
    {
        if (MayAssign(role, inviterIsPlatformAdmin)) return null;
        return AssignDeniedMessage(role) + " — คำเชิญนี้ใช้ไม่ได้ ขอคำเชิญใหม่ที่ระบุบทบาทของบริษัทจากเจ้าของบริษัท";
    }

    public static Outcome Decide(bool isApiKeyRequest, bool isPlatformAdmin, UserRole? callerRole,
        bool targetUserExists, UserRole? targetCurrentRole, bool targetIsSelf = false)
    {
        if (isApiKeyRequest) return Outcome.DenyApiKey;
        var memberOwner = callerRole is UserRole.Owner or UserRole.SystemAdmin;
        var activeSupport = callerRole == UserRole.PlatformSupport && isPlatformAdmin;
        if (!memberOwner && !activeSupport) return Outcome.DenyNotAllowed;
        if (!targetUserExists) return Outcome.DenyTargetMissing;
        if (targetCurrentRole == UserRole.Owner) return Outcome.DenyTargetAlreadyOwner;
        if (targetIsSelf && callerRole != UserRole.Owner) return Outcome.DenySelf;
        return Outcome.Allow;
    }

    /// <summary>HTTP status ของผลปฏิเสธ</summary>
    public static int StatusCode(Outcome o) => o switch
    {
        Outcome.DenyApiKey or Outcome.DenyNotAllowed or Outcome.DenySelf => 403,
        Outcome.DenyTargetMissing => 404,
        Outcome.DenyTargetAlreadyOwner => 409,
        _ => 200,
    };

    /// <summary>ข้อความถึงผู้ใช้ (ไทย · บอกทางไปต่อ)</summary>
    public static string Message(Outcome o) => o switch
    {
        Outcome.DenyApiKey => "โอนความเป็นเจ้าของผ่านคีย์ API ไม่ได้ — ต้องทำบนเว็บโดยผู้ที่ล็อกอิน",
        Outcome.DenyNotAllowed => "โอนความเป็นเจ้าของได้เฉพาะเจ้าของบริษัท หรือผู้ดูแลแพลตฟอร์มที่เป็นผู้ดูแล (support) ของบริษัทนี้และยังมีสิทธิ์แอดมินแพลตฟอร์ม — บริษัทอื่นต้องให้เจ้าของเดิมโอนเอง",
        Outcome.DenySelf => "ผู้ดูแลแพลตฟอร์มโอนความเป็นเจ้าของให้ตัวเองไม่ได้ — ส่งมอบให้ลูกค้า (อีเมลของลูกค้า)",
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
