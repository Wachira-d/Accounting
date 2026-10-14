using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>"ผู้ใช้คนนี้ยังทำหน้าที่แทนบริษัทนี้ได้ไหม" — ตัวตัดสินเดียวของผู้อนุมัติ/ผู้ลงนามที่ระบบ<b>อ้างถึงจากค่าที่เก็บไว้</b>
/// (ขั้นของกฎอนุมัติ · ผู้อนุมัติเริ่มต้นของ workflow ข้ามบริษัท · ผู้ลงนามที่เลือก) — รอบ 2026-10-14 ทีม F4 (ID#10 · ID#11)
///
/// <para>═══ ที่มา ═══ ค่าที่เก็บ Guid ของผู้ใช้ไว้ (กฎอนุมัติ · DefaultApproverUserId) ไม่เคยถูกตรวจซ้ำตอนใช้ ⇒ พนักงานที่ถูกถอดออกจากบริษัท
/// (<c>CompanyService.RemoveUserAsync</c> ลบแถว CompanyUsers) หรือถูกปิดบัญชี (<c>User.Status = Inactive</c> จากแอดมิน/ลาออกใน payroll/PDPA)
/// ยังถูกส่งคำขออนุมัติไปหา (เอกสารค้างรออนุมัติตลอดกาล) และยัง "ลงนามอัตโนมัติ" ในนามบริษัทได้</para>
///
/// <para>สมาชิก = มีแถว <c>CompanyUsers</c> ของบริษัทนี้ <b>และ</b> บัญชีไม่ถูกปิด/ระงับ (Inactive · Suspended) · บัญชีที่ยังไม่ยืนยันอีเมล
/// (PendingVerification) นับเป็นสมาชิก — คือคนที่เพิ่งถูกเชิญ ยืนยันเมื่อไรก็ทำหน้าที่ได้</para></summary>
public static class CompanyMembership
{
    /// <summary>สถานะบัญชีที่ยังทำหน้าที่แทนบริษัทได้ (pure)</summary>
    internal static bool CanAct(UserStatus status) => status is not (UserStatus.Inactive or UserStatus.Suspended);

    /// <summary>ชุดผู้ใช้ (จาก <paramref name="userIds"/>) ที่ยังเป็นสมาชิกที่ทำหน้าที่ได้ของ <paramref name="companyId"/> · กรองบริษัทเสมอ (tenant)</summary>
    public static async Task<HashSet<Guid>> ActiveMemberIdsAsync(AccountingDbContext db, Guid companyId,
        IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Where(i => i != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return new HashSet<Guid>();
        var rows = await (from cu in db.CompanyUsers.AsNoTracking()
                          join u in db.Users.AsNoTracking() on cu.UserId equals u.Id
                          where cu.CompanyId == companyId && ids.Contains(cu.UserId) && !u.IsDeleted
                          select new { cu.UserId, u.Status })
            .ToListAsync(ct);
        return rows.Where(r => CanAct(r.Status)).Select(r => r.UserId).ToHashSet();
    }

    /// <summary>ผู้ใช้คนเดียว — ตัวเดียวกับ <see cref="ActiveMemberIdsAsync"/></summary>
    public static async Task<bool> IsActiveMemberAsync(AccountingDbContext db, Guid companyId, Guid userId,
        CancellationToken ct = default)
        => (await ActiveMemberIdsAsync(db, companyId, new[] { userId }, ct)).Contains(userId);
}
