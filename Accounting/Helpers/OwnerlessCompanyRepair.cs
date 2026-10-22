using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวซ่อม "บริษัทที่ยังไม่มีเจ้าของ" — <b>ตัวตัดสินตัวเดียว</b>ว่าใครควรเป็นเจ้าของ และหลักฐานชัดพอให้ระบบตั้งเองไหม (รอบ 205 ทีม I · ข้อ 1)
///
/// <para>ที่มา: ก่อนรอบ 205 แอดมินแพลตฟอร์มที่สร้างบริษัท (รวมบริษัทของตัวเอง) ได้บทบาท support แทน Owner ⇒ บริษัทค้างในกล่อง
/// "บริษัทที่ยังไม่มีเจ้าของ" · โค้ดใหม่ (<see cref="CompanyCreatorMembership"/>) ไม่สร้างของเสียเพิ่ม แต่แถวที่บันทึกไว้แล้วยังผิด ⇒ ต้องมีตัวซ่อม</para>
///
/// <para>กติกา — <b>หลักฐานชัดเท่านั้นที่ระบบตั้งเอง</b> (หลักการ F2 ข้อ 3: ไม่รู้ = บอกว่าไม่รู้ ห้ามเดา):</para>
/// <list type="number">
/// <item>ผู้สร้าง (<c>Company.CreatedBy</c> = id ผู้ใช้) ยังเป็นสมาชิก และบทบาทเป็นบทบาทของลูกค้า (ไม่ใช่ support) ⇒ <see cref="Kind.AutoPromote"/> ผู้สร้าง</item>
/// <item>ไม่รู้ผู้สร้าง/ผู้สร้างไม่ได้เป็นสมาชิกแล้ว และมีสมาชิกบทบาทลูกค้า <b>คนเดียว</b> ⇒ <see cref="Kind.AutoPromote"/> คนนั้น (ไม่มีใครอื่นให้เลือก)</item>
/// <item>ผู้สร้างเป็นผู้ดูแลแพลตฟอร์ม (support) ⇒ <see cref="Kind.NeedsAdmin"/> — แยกไม่ได้ว่า "เปิดให้ลูกค้า" หรือ "บริษัทของแอดมินเอง"
/// (ผู้สมัครจากเส้นเก่าไม่มีธง OpenForCustomer) · เสนอผู้สร้าง + สมาชิกลูกค้าเป็นตัวเลือก</item>
/// <item>สมาชิกบทบาทลูกค้าหลายคน (ไม่รู้ผู้สร้าง) ⇒ <see cref="Kind.NeedsAdmin"/> เรียงตามวันเข้าร่วม · ไม่มีสมาชิกเลย ⇒ <see cref="Kind.NeedsAdmin"/> ไม่มีตัวเลือก</item>
/// </list>
/// <para>"เจ้าของ" ในที่นี้ = <see cref="UserRole.Owner"/> หรือ <see cref="UserRole.SystemAdmin"/> (ผ่านด่านเจ้าของเหมือนกัน — <c>EnsureOwnerAccessAsync</c>) ·
/// บริษัทที่มีอย่างใดอย่างหนึ่งไม่ใช่ "ไม่มีเจ้าของ"</para>
/// </summary>
public static class OwnerlessCompanyRepair
{
    public const string RuleCode = "COMPANY-OWNERLESS-REPAIR";

    public enum Kind { AutoPromote, NeedsAdmin }

    public sealed record Member(Guid UserId, UserRole Role, DateTime JoinedAt);

    public sealed record Proposal(Kind Kind, Guid? UserId, string Reason, IReadOnlyList<Guid> Candidates);

    /// <summary>บทบาทที่ผ่านด่านเจ้าของ — นิยามเดียวของ "บริษัทมีเจ้าของ" (กล่องแอดมิน · ตัวซ่อม · ด่านลดสิทธิ์)</summary>
    public static bool IsOwnerRole(UserRole role) => role is UserRole.Owner or UserRole.SystemAdmin;

    public static bool HasOwner(IEnumerable<UserRole> roles) => roles.Any(IsOwnerRole);

    /// <summary>ตัดสินข้อเสนอเจ้าของของบริษัทหนึ่ง (ต้องเรียกเฉพาะบริษัทที่ <see cref="HasOwner"/> = false)</summary>
    public static Proposal Decide(string? createdBy, IReadOnlyList<Member> members)
    {
        Guid? creatorId = Guid.TryParse(createdBy, out var g) && g != Guid.Empty ? g : null;
        var creator = creatorId is { } cid ? members.FirstOrDefault(m => m.UserId == cid) : null;
        var customerMembers = members.Where(m => m.Role != UserRole.PlatformSupport)
            .OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId).ToList();

        if (creator != null && creator.Role != UserRole.PlatformSupport)
            return new(Kind.AutoPromote, creator.UserId,
                "ผู้สร้างบริษัทยังเป็นสมาชิกอยู่ ⇒ ตั้งเป็นเจ้าของ (ผู้สร้างต้องเป็นเจ้าของตั้งแต่สร้าง)", new[] { creator.UserId });

        if (creator != null)   // ผู้สร้าง = support
        {
            var cands = new List<Guid> { creator.UserId };
            cands.AddRange(customerMembers.Select(m => m.UserId));
            return new(Kind.NeedsAdmin, null,
                customerMembers.Count == 0
                    ? "ผู้สร้างเป็นผู้ดูแลแพลตฟอร์ม (support) และยังไม่มีลูกค้าในบริษัท — แยกไม่ได้ว่าเปิดให้ลูกค้าหรือเป็นบริษัทของแอดมินเอง ⇒ แอดมินเลือก (หรือโอนให้ลูกค้าที่หน้าทีม)"
                    : "ผู้สร้างเป็นผู้ดูแลแพลตฟอร์ม (support) และมีสมาชิกลูกค้าแล้ว — เลือกว่าใครเป็นเจ้าของ",
                cands);
        }

        if (customerMembers.Count == 1)
            return new(Kind.AutoPromote, customerMembers[0].UserId,
                "ไม่พบผู้สร้างในสมาชิก และมีสมาชิกของบริษัทคนเดียว ⇒ ตั้งคนนั้นเป็นเจ้าของ", new[] { customerMembers[0].UserId });

        var all = customerMembers.Select(m => m.UserId)
            .Concat(members.Where(m => m.Role == UserRole.PlatformSupport).Select(m => m.UserId)).ToList();
        return new(Kind.NeedsAdmin, null,
            all.Count == 0
                ? "บริษัทไม่มีสมาชิกเลย — เพิ่มผู้ใช้ให้บริษัทก่อน (หน้าแอดมินผู้ใช้ › สร้างผู้ใช้ › ผูกกับบริษัท) แล้วตั้งเป็นเจ้าของ"
                : "มีสมาชิกหลายคนและไม่รู้ว่าใครสร้างบริษัท — แอดมินเลือกเจ้าของ (เรียงตามวันเข้าร่วม)",
            all);
    }

    /// <summary>แอดมินตั้งเจ้าของให้บริษัทที่ไม่มีเจ้าของ — null = ได้ · ผู้รับต้องเป็นสมาชิกอยู่แล้ว · บริษัทต้องยังไม่มีเจ้าของ (ไม่ใช่ทางลัดยึดบริษัทลูกค้า
    /// — ข้อ 105) · แอดมินตั้งตัวเองได้เฉพาะเมื่อเป็นผู้สร้างบริษัท (บริษัทของตัวเองที่สร้างก่อนรอบ 205)</summary>
    public static string? AdminAssignBlock(bool companyHasOwner, bool targetIsMember, bool targetIsSelf, bool callerIsCreator)
    {
        if (companyHasOwner)
            return "บริษัทนี้มีเจ้าของแล้ว — เปลี่ยนเจ้าของด้วย “โอนความเป็นเจ้าของ” ที่หน้าทีมของบริษัท (โดยเจ้าของเดิม)";
        if (!targetIsMember)
            return "ผู้ใช้นี้ไม่ได้เป็นสมาชิกของบริษัท — เพิ่มเป็นสมาชิกก่อน แล้วตั้งเป็นเจ้าของ";
        if (targetIsSelf && !callerIsCreator)
            return "ตั้งตัวเองเป็นเจ้าของได้เฉพาะบริษัทที่คุณเป็นผู้สร้าง — บริษัทลูกค้าให้ตั้งลูกค้าเป็นเจ้าของ";
        return null;
    }
}
