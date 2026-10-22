using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ด่าน "บริษัทต้องมีเจ้าของอย่างน้อย 1 คน" — <b>ตัวตัดสินตัวเดียว</b>ของการลดบทบาท/ถอดสมาชิก (รอบ 205 ทีม I · ข้อ 2)
///
/// <para>เดิมเขียนนับ Owner เองสองที่ใน <c>CompanyService</c> (ลดบทบาท · ถอดสมาชิก) และนับเฉพาะ <see cref="UserRole.Owner"/> ·
/// ตอนนี้ "เจ้าของ" = <see cref="OwnerlessCompanyRepair.IsOwnerRole"/> (Owner/SystemAdmin — ผ่านด่านเจ้าของเหมือนกัน) นิยามเดียวกับกล่อง
/// "บริษัทที่ยังไม่มีเจ้าของ" · บทบาทกำหนดเอง (<c>CompanyRoleId</c>) ลดสิทธิ์เจ้าของไม่ได้อยู่แล้ว (ด่านสิทธิ์ให้เจ้าของผ่านก่อนดูบทบาทกำหนดเอง)</para>
/// </summary>
public static class LastOwnerGuard
{
    public const string RuleCode = "COMPANY-LAST-OWNER";

    /// <summary>null = ทำได้ · <paramref name="otherOwnerCount"/> = จำนวนสมาชิกบทบาทเจ้าของ <b>ไม่นับเป้าหมาย</b> ·
    /// <paramref name="newRole"/> = null หมายถึงถอดออกจากบริษัท</summary>
    public static string? Block(UserRole currentRole, UserRole? newRole, int otherOwnerCount)
    {
        if (!OwnerlessCompanyRepair.IsOwnerRole(currentRole)) return null;            // เป้าหมายไม่ใช่เจ้าของ
        if (newRole is { } r && OwnerlessCompanyRepair.IsOwnerRole(r)) return null;   // ยังเป็นเจ้าของ
        if (otherOwnerCount > 0) return null;                                          // ยังมีเจ้าของคนอื่น
        return newRole == null
            ? "ลบไม่ได้ — บริษัทต้องมีเจ้าของอย่างน้อย 1 คน (เพิ่ม/ยกระดับเจ้าของคนใหม่ก่อน หรือใช้ “โอนความเป็นเจ้าของ”)"
            : "ลดสิทธิ์ไม่ได้ — บริษัทต้องมีเจ้าของอย่างน้อย 1 คน (เพิ่ม/ยกระดับเจ้าของคนใหม่ก่อน หรือใช้ “โอนความเป็นเจ้าของ”)";
    }
}
