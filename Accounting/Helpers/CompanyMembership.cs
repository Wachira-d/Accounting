using Accounting.Data;
using Accounting.Models.Entities;
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
/// (PendingVerification) นับเป็นสมาชิก — คือคนที่เพิ่งถูกเชิญ ยืนยันเมื่อไรก็ทำหน้าที่ได้</para>
///
/// <para>รอบ 277 (ฝ่ายค้าน HIGH-1/HIGH-2): สมาชิกที่ตอบรับคำเชิญโดยไม่กำหนดบทบาท (<c>CompanyUser.AccessPending</c>) <b>ไม่ใช่สมาชิกที่ทำหน้าที่ได้</b> —
/// ตัดในตัวกรองนี้ตัวเดียว (<see cref="Active"/>) ⇒ ทุกทางเข้าที่ไม่ผ่าน <c>TenantAccessMiddleware</c> (บอท LINE · หน้าสำนักงานบัญชี · <c>api/me/permissions</c> ·
/// SignalR <c>JoinCompanyGroup</c>) และผู้อนุมัติ/ผู้ลงนามที่อ้างจากค่าที่เก็บไว้ เดินด่านเดียวกัน · เดิมบอท LINE ค้น CompanyUsers ตรง ⇒ สมาชิกรอสิทธิ์
/// สั่ง "ดูยอด/ถาม/บันทึก" ได้ และหน้าสำนักงานบัญชีโชว์เลขภาษี/ยอดของบริษัท</para></summary>
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
                          where cu.CompanyId == companyId && ids.Contains(cu.UserId) && !u.IsDeleted && !cu.AccessPending
                          select new { cu.UserId, u.Status })
            .ToListAsync(ct);
        return rows.Where(r => CanAct(r.Status)).Select(r => r.UserId).ToHashSet();
    }

    /// <summary>ผู้ใช้คนเดียว — ตัวเดียวกับ <see cref="ActiveMemberIdsAsync"/></summary>
    public static async Task<bool> IsActiveMemberAsync(AccountingDbContext db, Guid companyId, Guid userId,
        CancellationToken ct = default)
        => (await ActiveMemberIdsAsync(db, companyId, new[] { userId }, ct)).Contains(userId);

    /// <summary>รอบ 277: แถวสมาชิกที่ใช้งานได้ (ตัดสมาชิกรอสิทธิ์) — ตัวกรองเดียวของทุกที่ที่เลือก/ตรวจบริษัทของผู้ใช้จาก CompanyUsers</summary>
    public static IQueryable<CompanyUser> Active(IQueryable<CompanyUser> rows)
    {
        return rows.Where(cu => !cu.AccessPending);
    }

    /// <summary>รอบ 277: บริษัทที่ผู้ใช้ใช้งานได้ทั้งหมด (บอท LINE · หน้าสำนักงานบัญชี)</summary>
    public static IQueryable<CompanyUser> ActiveOf(AccountingDbContext db, Guid userId)
        => Active(db.CompanyUsers.Where(cu => cu.UserId == userId));

    /// <summary>รอบ 277: สมาชิกที่ใช้งานได้ของบริษัทนี้ (ไม่ดูสถานะบัญชี — ผู้เรียกที่ล็อกอินอยู่ผ่านด่านบัญชีมาแล้ว) · ใช้กับด่านทางเข้า</summary>
    public static IQueryable<CompanyUser> ActiveRow(AccountingDbContext db, Guid companyId, Guid userId)
        => Active(db.CompanyUsers.Where(cu => cu.CompanyId == companyId && cu.UserId == userId));

    /// <summary>รอบ 277 (ฝ่ายค้าน — ช่องรั่วผ่านการแจ้งเตือน): ผู้ใช้คนนี้รับแจ้งเตือนของบริษัทนี้ได้ไหม — มีแถวสมาชิกแต่ไม่ผ่าน <see cref="Active"/>
    /// (รอสิทธิ์) ⇒ ไม่ได้ · ไม่มีแถวสมาชิก ⇒ ไม่ตัดสินที่นี่ (พฤติกรรมเดิมของผู้เรียก) · ผู้เรียก: <c>NotificationEngine</c> (เส้นบทบาท + ผู้รับระบุตัว)</summary>
    public static async Task<bool> MayReceiveCompanyNotificationAsync(AccountingDbContext db, Guid companyId, Guid userId, CancellationToken ct = default)
    {
        var rows = db.CompanyUsers.Where(cu => cu.CompanyId == companyId && cu.UserId == userId);
        if (!await rows.AnyAsync(ct)) return true;
        return await Active(rows).AnyAsync(ct);
    }

    /// <summary>รอบ 277: บริษัทที่ผู้ใช้เป็นสมาชิกแต่ยังรอสิทธิ์ — กระดิ่งตอนอ่าน (<c>NotificationService</c>) กรองแถวของบริษัทเหล่านี้ออก (รวมแถวเก่าที่ค้าง)</summary>
    public static async Task<List<Guid>> PendingCompanyIdsAsync(AccountingDbContext db, Guid userId, CancellationToken ct = default)
    {
        var mine = db.CompanyUsers.Where(cu => cu.UserId == userId);
        var active = Active(mine).Select(cu => cu.CompanyId);
        return await mine.Select(cu => cu.CompanyId).Where(id => !active.Contains(id)).Distinct().ToListAsync(ct);
    }

    /// <summary>ข้อความเมื่อผู้ใช้มีแต่บริษัทที่รอสิทธิ์ (บอท LINE)</summary>
    public const string OnlyPendingMessage =
        "⏳ คุณเข้าร่วมบริษัทแล้ว แต่เจ้าของยังไม่ได้กำหนดสิทธิ์ให้ — ใช้งานผ่าน LINE ได้เมื่อเจ้าของตั้งบทบาทที่หน้า \u201cจัดการทีม\u201d แล้ว";
}
