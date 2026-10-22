using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// แถวสมาชิกของ "ผู้สร้างบริษัท" — <b>ตัวสร้างตัวเดียวของทุกทางเข้าที่สร้างบริษัท</b> (รอบ 205 ทีม I · ข้อ 1)
///
/// <para>═══ ที่มา (บั๊กจริง — กล่อง "บริษัทที่ยังไม่มีเจ้าของ" บนหน้าแอดมิน) ═══ ทางสร้างบริษัทมี 3 ทาง
/// (<c>CompanyService.CreateAsync</c> = วิซาร์ดตั้งค่า/สร้างบริษัทเพิ่ม · <c>AuthService.RegisterAsync</c> · <c>AuthService.SsoLoginAsync</c>)
/// แต่ละทางพิมพ์ <c>new CompanyUser { Role = … }</c> เอง · ตั้งแต่ C-4 (รอบ 201) ทางวิซาร์ดให้ <b>ทุก</b> ผู้สร้างที่เป็นแอดมินแพลตฟอร์มได้
/// <see cref="UserRole.PlatformSupport"/> แทน Owner โดยไม่ถามว่า "เปิดให้ลูกค้า" หรือ "บริษัทของตัวเอง" ⇒ เจ้าของแพลตฟอร์ม/ทีมงานที่สร้างบริษัทของตัวเอง
/// ได้บริษัทที่ไม่มีเจ้าของ (คีย์ API · สมาชิก · ปิดงวด · ลบบริษัท ทำไม่ได้) และโอนให้ตัวเองก็ไม่ได้ (<c>OwnershipTransferPolicy.Outcome.DenySelf</c>)</para>
///
/// <para>กติกา (คำสั่งเจ้าของรอบ 205: "สร้างบริษัทใหม่ ⇒ ผู้สร้างได้ Owner ทันที"): ผู้สร้าง = <b>Owner เสมอ</b> · ยกเว้นแอดมินแพลตฟอร์มที่ <b>ติ๊กเอง</b>
/// ว่าเปิดบริษัทนี้ให้ลูกค้า (<c>CreateCompanyRequest.OpenForCustomer</c>) ⇒ <see cref="UserRole.PlatformSupport"/> แล้วส่งมอบด้วย "โอนความเป็นเจ้าของ"
/// (ข้อ 77 ยังอยู่ แต่เป็นทางเลือกที่ตั้งใจ ไม่ใช่ค่าตั้งต้นที่เดาแทน) · ผู้ใช้ทั่วไปติ๊กแล้วไม่มีผล (ไม่ใช่แอดมินแพลตฟอร์ม)</para>
/// </summary>
public static class CompanyCreatorMembership
{
    public const string RuleCode = "COMPANY-CREATOR-OWNER";

    /// <summary>บทบาทของผู้สร้าง — <see cref="UserRole.PlatformSupport"/> เฉพาะเมื่อเป็นแอดมินแพลตฟอร์ม <b>และ</b> ติ๊ก "เปิดให้ลูกค้า"</summary>
    private static UserRole RoleFor(bool creatorIsPlatformAdmin, bool openForCustomer)
        => creatorIsPlatformAdmin && openForCustomer ? UserRole.PlatformSupport : UserRole.Owner;

    /// <summary>แถว <see cref="CompanyUser"/> ของผู้สร้าง (บริษัทแรก/บริษัทที่เพิ่งสร้าง = บริษัทหลัก <c>IsDefault</c>)</summary>
    public static CompanyUser NewRow(Guid companyId, Guid creatorUserId, bool creatorIsPlatformAdmin, bool openForCustomer)
        => new()
        {
            CompanyId = companyId,
            UserId = creatorUserId,
            Role = RoleFor(creatorIsPlatformAdmin, openForCustomer),
            IsDefault = true,
            JoinedAt = DateTime.UtcNow,
        };
}
