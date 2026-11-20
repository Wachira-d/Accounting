using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 276 — "ใครอนุมัติเอกสารนี้ได้" ตัวเดียว: สมาชิกที่ยัง active + ผ่านด่านเดียวกับปุ่มอนุมัติ
/// (<see cref="DocumentPermissionHelper.CanApproveAsync"/>) + ไม่ถูกจำกัด "เฉพาะร่าง" (<see cref="DocumentRestrictionGuard.ForUserAsync"/>) ·
/// ชนิดเอกสารไม่รู้ (null) = อนุมัติได้อย่างน้อยหนึ่งฝั่ง · ผู้เรียก: <c>NotificationEngine</c> (บทบาทผู้รับ <c>DocumentApprover</c>) ·
/// <c>DocumentApprovalRequestService</c> (นับผู้อนุมัติก่อนส่ง · คิว · ปุ่มบนใบ)
/// </summary>
public static class DocumentApproverDirectory
{
    public static async Task<List<Guid>> ResolveAsync(AccountingDbContext db, IPermissionService perms, Guid companyId, DocumentType? type,
        CancellationToken ct = default)
    {
        var members = await db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.User.Status != UserStatus.Inactive)
            .Select(cu => cu.UserId).ToListAsync(ct);
        var result = new List<Guid>();
        foreach (var uid in members.Distinct())
            if (await MayApproveAsync(db, perms, companyId, uid, type, ct)) result.Add(uid);
        return result;
    }

    /// <summary>ผู้ใช้คนนี้อนุมัติเอกสารชนิดนี้ได้ไหม (สิทธิ์ + ไม่ถูกจำกัดเฉพาะร่าง)</summary>
    public static async Task<bool> MayApproveAsync(AccountingDbContext db, IPermissionService perms, Guid companyId, Guid userId, DocumentType? type,
        CancellationToken ct = default)
    {
        if ((await DocumentRestrictionGuard.ForUserAsync(db, companyId, userId, ct)).Restriction.DraftOnly) return false;
        if (type is DocumentType t) return await DocumentPermissionHelper.CanApproveAsync(perms, companyId, userId, t);
        return await DocumentPermissionHelper.CanApproveAsync(perms, companyId, userId, DocumentType.Invoice)
            || await DocumentPermissionHelper.CanApproveAsync(perms, companyId, userId, DocumentType.PurchaseInvoice);
    }
}
