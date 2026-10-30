using System.Linq.Expressions;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวซ่อม "บริษัทที่ยังไม่มีเจ้าของ" — <b>ตัวตัดสินตัวเดียว</b>ว่าใครควรเป็นเจ้าของ และหลักฐานชัดพอให้ระบบตั้งเองไหม (รอบ 205 ทีม I · ข้อ 1)
///
/// <para>ที่มา: ก่อนรอบ 205 แอดมินแพลตฟอร์มที่สร้างบริษัท (รวมบริษัทของตัวเอง) ได้บทบาท support แทน Owner ⇒ บริษัทค้างในกล่อง
/// "บริษัทที่ยังไม่มีเจ้าของ" · โค้ดใหม่ (<see cref="CompanyCreatorMembership"/>) ไม่สร้างของเสียเพิ่ม แต่แถวที่บันทึกไว้แล้วยังผิด ⇒ ต้องมีตัวซ่อม</para>
///
/// <para>กติกา — <b>หลักฐานชัดเท่านั้นที่ระบบตั้งเอง</b> (หลักการ F2 ข้อ 3: ไม่รู้ = บอกว่าไม่รู้ ห้ามเดา · ฝ่ายค้านรอบ 205 ทีม I):</para>
/// <list type="number">
/// <item>ผู้สร้าง (<c>Company.CreatedBy</c> = id ผู้ใช้) ยังเป็นสมาชิก · บัญชียัง active · บทบาทไม่ใช่ support ⇒ <see cref="Kind.AutoPromote"/> ผู้สร้าง</item>
/// <item>อย่างอื่นทั้งหมด ⇒ <see cref="Kind.NeedsAdmin"/> — "เหลือสมาชิกคนเดียว" <b>ไม่ใช่</b>หลักฐานความเป็นเจ้าของ (Staff/Viewer ที่ถูกเชิญ · บัญชีปิดแล้ว) ·
/// ผู้สร้างเป็น support แยกไม่ได้ว่า "เปิดให้ลูกค้า" หรือ "บริษัทของแอดมินเอง"</item>
/// <item>ตัวเลือกให้แอดมิน = ผู้สร้าง (ถ้ายังเป็นสมาชิก) + สมาชิกลูกค้าที่ active · <b>ตัดผู้ดูแลแพลตฟอร์ม</b> (บทบาท support หรือบัญชีแอดมินแพลตฟอร์ม) ออกเสมอ
/// เว้นแต่เป็นผู้สร้าง — สิทธิ์แพลตฟอร์มไม่ใช่สิทธิ์ถือบริษัทลูกค้า (ข้อ 105)</item>
/// </list>
/// <para>"เจ้าของ" ในกล่องนี้ = <see cref="UserRole.Owner"/> หรือ <see cref="UserRole.SystemAdmin"/> (ผ่านด่านเจ้าของเหมือนกัน — <c>EnsureOwnerAccessAsync</c>) ·
/// ด่านเจ้าของคนสุดท้าย (<see cref="LastOwnerGuard"/>) เข้มกว่า: นับเฉพาะ Owner ที่ไม่ใช่แอดมินแพลตฟอร์ม</para>
/// </summary>
public static class OwnerlessCompanyRepair
{
    public const string RuleCode = "COMPANY-OWNERLESS-REPAIR";

    public enum Kind { AutoPromote, NeedsAdmin }

    /// <param name="IsPlatformAdmin">บัญชีเป็นแอดมินแพลตฟอร์ม (<c>User.IsSystemAdmin</c>)</param>
    /// <param name="IsActive">บัญชีใช้งานได้ (<c>UserStatus.Active</c>)</param>
    /// <param name="IsSuspended">บัญชีถูกระงับชั่วคราว (<c>UserStatus.Suspended</c> · ไม่ถูกลบ) — ฝ่ายค้านรอบ 214 (ฑ-2) B2-b</param>
    public sealed record Member(Guid UserId, UserRole Role, DateTime JoinedAt, bool IsPlatformAdmin = false, bool IsActive = true, bool IsSuspended = false);

    public sealed record Proposal(Kind Kind, Guid? UserId, string Reason, IReadOnlyList<Guid> Candidates);

    /// <summary>บทบาทที่ผ่านด่านเจ้าของ — นิยามของ "บริษัทมีเจ้าของ" (กล่องแอดมิน · ตัวซ่อม)</summary>
    private static bool IsOwnerRole(UserRole role) => role is UserRole.Owner or UserRole.SystemAdmin;


    /// <summary>ฝ่ายค้านรอบ 214 (ฑ-2) B2-a: <b>นิยามเดียว</b>ของ "เจ้าของที่ยังทำหน้าที่ได้" (บทบาทเจ้าของ · บัญชีไม่ถูกลบ · ไม่ Inactive/Suspended —
    /// เงื่อนไขเดียวกับ <c>CompanyMembership.CanAct</c>) เป็น expression ที่ EF แปลได้ — ใช้ร่วม: รายการบริษัทไม่มีเจ้าของ · assign-owner ·
    /// สร้างผู้ใช้เป็น Owner · ด่านเจ้าของคนสุดท้ายของการลบ (<c>PdpaService.ErasureOwnershipBlockersAsync</c> — กรองแอดมินแพลตฟอร์มเพิ่ม) ห้ามเขียนสำเนา inline</summary>
    public static readonly Expression<Func<CompanyUser, bool>> ActingOwner = cu =>
        (cu.Role == UserRole.Owner || cu.Role == UserRole.SystemAdmin)
        && !cu.User.IsDeleted && cu.User.Status != UserStatus.Inactive && cu.User.Status != UserStatus.Suspended;

    /// <summary>ผู้ใช้นี้เป็นฝั่งแพลตฟอร์ม (บทบาท support หรือบัญชีแอดมินแพลตฟอร์ม) — ห้ามเป็นเจ้าของบริษัทลูกค้า เว้นแต่เป็นผู้สร้าง</summary>
    public static bool IsPlatformSide(UserRole role, bool isPlatformAdmin) => role == UserRole.PlatformSupport || isPlatformAdmin;

    /// <summary>ตัดสินข้อเสนอเจ้าของของบริษัทหนึ่ง (ต้องเรียกเฉพาะบริษัทที่ไม่มีสมาชิกผ่าน <see cref="ActingOwner"/>)</summary>
    public static Proposal Decide(string? createdBy, IReadOnlyList<Member> members)
    {
        Guid? creatorId = Guid.TryParse(createdBy, out var g) && g != Guid.Empty ? g : null;
        var creator = creatorId is { } cid ? members.FirstOrDefault(m => m.UserId == cid) : null;
        var customers = members.Where(m => !IsPlatformSide(m.Role, m.IsPlatformAdmin) && m.IsActive && m.UserId != creator?.UserId)
            .OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId).Select(m => m.UserId).ToList();
        var cands = new List<Guid>();
        if (creator != null) cands.Add(creator.UserId);
        cands.AddRange(customers);

        // ฝ่ายค้านรอบ 214 (ฑ-2) B2-b: เจ้าของเดิมแค่ "ถูกระงับ" (ไม่ใช่ปิด/ลบ) — ระงับคืนได้ ⇒ ระบบไม่ตั้งเจ้าของใหม่เอง แอดมินตัดสิน
        if (members.Any(m => IsOwnerRole(m.Role) && m.IsSuspended))
            return new(Kind.NeedsAdmin, null,
                "เจ้าของเดิมถูกระงับบัญชีชั่วคราว (ไม่ได้ปิด/ลบ) — ระบบไม่ตั้งเจ้าของใหม่เอง: ปลดระงับเจ้าของเดิม หรือเลือกเจ้าของใหม่", cands);

        if (creator != null && creator.IsActive && creator.Role != UserRole.PlatformSupport)
            return new(Kind.AutoPromote, creator.UserId,
                "ผู้สร้างบริษัทยังเป็นสมาชิกและบัญชียังใช้งานอยู่ ⇒ ตั้งเป็นเจ้าของ (ผู้สร้างต้องเป็นเจ้าของตั้งแต่สร้าง)", cands);

        string reason;
        if (creator != null && creator.Role == UserRole.PlatformSupport)
            reason = customers.Count == 0
                ? "ผู้สร้างเป็นผู้ดูแลแพลตฟอร์ม (support) และยังไม่มีลูกค้าในบริษัท — แยกไม่ได้ว่าเปิดให้ลูกค้าหรือเป็นบริษัทของแอดมินเอง ⇒ แอดมินเลือก (หรือโอนให้ลูกค้าที่หน้าทีม)"
                : "ผู้สร้างเป็นผู้ดูแลแพลตฟอร์ม (support) และมีสมาชิกลูกค้าแล้ว — เลือกว่าใครเป็นเจ้าของ";
        else if (creator != null)
            reason = "บัญชีผู้สร้างบริษัทถูกปิดใช้งาน — เลือกเจ้าของจากสมาชิก";
        else if (cands.Count == 0)
            reason = "ไม่มีสมาชิกลูกค้าที่ใช้งานได้ — เพิ่มผู้ใช้ให้บริษัทก่อน (หน้าแอดมินผู้ใช้ › สร้างผู้ใช้ › ผูกกับบริษัท) แล้วตั้งเป็นเจ้าของ";
        else
            reason = "ไม่พบผู้สร้างในสมาชิก — สมาชิกที่เหลือไม่ใช่หลักฐานความเป็นเจ้าของ (อาจเป็นพนักงานที่ถูกเชิญ) ⇒ แอดมินเลือก (เรียงตามวันเข้าร่วม)";
        return new(Kind.NeedsAdmin, null, reason, cands);
    }

    /// <summary>แอดมินตั้งเจ้าของให้บริษัทที่ไม่มีเจ้าของ — null = ได้ · ตัวตัดสินเดียวของ assign-owner และ "สร้างผู้ใช้เป็น Owner" ·
    /// บริษัทต้องยังไม่มีเจ้าของ · ผู้รับต้องเป็นสมาชิก (หรือกำลังถูกสร้างเป็นสมาชิก) · ผู้รับที่เป็นฝั่งแพลตฟอร์ม (support/แอดมินแพลตฟอร์ม — รวมผู้เรียกเอง)
    /// ได้เฉพาะเมื่อเป็นผู้สร้างบริษัท (บริษัทของตัวเองที่สร้างก่อนรอบ 205) — ไม่ใช่ทางลัดยึดบริษัทลูกค้า (ข้อ 105)</summary>
    public static string? AdminAssignBlock(bool companyHasOwner, bool targetIsMember, bool targetIsPlatformSide, bool targetIsCreator)
    {
        if (companyHasOwner)
            return "บริษัทนี้มีเจ้าของแล้ว — เปลี่ยนเจ้าของด้วย \u201Cโอนความเป็นเจ้าของ\u201D ที่หน้าทีมของบริษัท (โดยเจ้าของเดิม)";
        if (!targetIsMember)
            return "ผู้ใช้นี้ไม่ได้เป็นสมาชิกของบริษัท — เพิ่มเป็นสมาชิกก่อน แล้วตั้งเป็นเจ้าของ";
        if (targetIsPlatformSide && !targetIsCreator)
            return "ตั้งผู้ดูแลแพลตฟอร์ม (support/แอดมินแพลตฟอร์ม) เป็นเจ้าของได้เฉพาะบริษัทที่ผู้นั้นสร้างเอง — บริษัทลูกค้าให้ตั้งลูกค้าเป็นเจ้าของ";
        return null;
    }
}
