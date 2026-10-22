using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ด่าน "บริษัทต้องมีเจ้าของอย่างน้อย 1 คน" — <b>ตัวตัดสินตัวเดียว</b>ของการลดบทบาท/ถอดสมาชิก (รอบ 205 ทีม I · ข้อ 2)
///
/// <para>เดิมเขียนนับ Owner เองสองที่ใน <c>CompanyService</c> (ลดบทบาท · ถอดสมาชิก) · "เจ้าของ" ของด่านนี้ = <b>Owner ฝั่งลูกค้า</b>
/// (<see cref="UserRole.Owner"/> ที่บัญชีไม่ใช่แอดมินแพลตฟอร์ม) — ฝ่ายค้านรอบ 205: เดิมนับ SystemAdmin(99) ด้วย ⇒ ถอด Owner ลูกค้าคนสุดท้ายได้
/// เมื่อมีแอดมินเป็นสมาชิก · บทบาทกำหนดเอง (<c>CompanyRoleId</c>) ลดสิทธิ์เจ้าของไม่ได้อยู่แล้ว</para>
/// </summary>
public static class LastOwnerGuard
{
    public const string RuleCode = "COMPANY-LAST-OWNER";

    /// <summary>null = ทำได้ · <paramref name="otherCustomerOwnerCount"/> = จำนวน Owner ฝั่งลูกค้า (ไม่ใช่แอดมินแพลตฟอร์ม) <b>ไม่นับเป้าหมาย</b> ·
    /// <paramref name="newRole"/> = null หมายถึงถอดออกจากบริษัท</summary>
    public static string? Block(UserRole currentRole, UserRole? newRole, int otherCustomerOwnerCount)
    {
        if (currentRole != UserRole.Owner) return null;            // เป้าหมายไม่ใช่เจ้าของ
        if (newRole == UserRole.Owner) return null;                // ยังเป็นเจ้าของ
        if (otherCustomerOwnerCount > 0) return null;              // ยังมีเจ้าของลูกค้าคนอื่น
        return newRole == null
            ? "ลบไม่ได้ — บริษัทต้องมีเจ้าของอย่างน้อย 1 คน (เพิ่ม/ยกระดับเจ้าของคนใหม่ก่อน หรือใช้ \u201Cโอนความเป็นเจ้าของ\u201D)"
            : "ลดสิทธิ์ไม่ได้ — บริษัทต้องมีเจ้าของอย่างน้อย 1 คน (เพิ่ม/ยกระดับเจ้าของคนใหม่ก่อน หรือใช้ \u201Cโอนความเป็นเจ้าของ\u201D)";
    }
}
