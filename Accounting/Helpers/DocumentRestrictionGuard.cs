using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 276 — ส่วนที่แตะฐานของด่าน "เห็นเฉพาะของตัวเอง / เฉพาะร่าง" (ตัวตัดสินอยู่ที่ <see cref="DocumentRestrictionPolicy"/>) ·
/// <list type="bullet">
/// <item><see cref="ForUserAsync"/> — ค่าที่มีผลของผู้ใช้ในบริษัท (ผู้ใช้ ∪ บทบาท) · ผู้เรียก: <c>DocumentRestrictionFilter</c> · <c>RolePermissionService</c> · <c>DocumentApprovalRequestService</c></item>
/// <item><see cref="ApplyScope"/> — ตั้งขอบเขตของคำขอบน DbContext (ตัวกรองแถว EF + ธงเฉพาะร่าง)</item>
/// <item><see cref="EnsureMayFinalizeAsync"/> — ด่านชั้น service ของการอนุมัติ/ยกเลิก (ทุกทางเข้า: เว็บ · มือถือ · LINE · workflow · งานอัตโนมัติในคำขอของผู้ถูกจำกัด)</item>
/// </list>
/// </summary>
public static class DocumentRestrictionGuard
{
    public sealed record UserScope(DocumentRestrictionPolicy.Restriction Restriction, string? Email);

    /// <summary>ค่าที่มีผลของ <paramref name="userId"/> ใน <paramref name="companyId"/> · ไม่ใช่สมาชิก = ไม่จำกัด (ด่านสมาชิกเป็นของ TenantGuard)</summary>
    public static async Task<UserScope> ForUserAsync(AccountingDbContext db, Guid companyId, Guid userId, CancellationToken ct = default)
    {
        var row = await db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
            .Select(cu => new
            {
                cu.Role, cu.OwnDocumentsOnly, cu.DraftDocumentsOnly,
                RoleOwn = cu.CompanyRole != null && cu.CompanyRole.OwnDocumentsOnly,
                RoleDraft = cu.CompanyRole != null && cu.CompanyRole.DraftDocumentsOnly,
                cu.User.Email,
            })
            .FirstOrDefaultAsync(ct);
        if (row == null) return new UserScope(DocumentRestrictionPolicy.Restriction.None, null);
        return new UserScope(
            DocumentRestrictionPolicy.Effective(row.Role, row.OwnDocumentsOnly, row.DraftDocumentsOnly, row.RoleOwn, row.RoleDraft),
            row.Email);
    }

    /// <summary>ผู้กระทำที่ service ได้รับเป็นข้อความ (id ผู้ใช้ หรืออีเมลสมาชิก — ทาง LINE ส่งอีเมล) → ค่าที่มีผล · ป้ายระบบ (<c>system:*</c> ฯลฯ) = ไม่จำกัด</summary>
    public static async Task<DocumentRestrictionPolicy.Restriction> ForActorAsync(AccountingDbContext db, Guid companyId, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor)) return DocumentRestrictionPolicy.Restriction.None;
        if (Guid.TryParse(actor, out var uid)) return (await ForUserAsync(db, companyId, uid, ct)).Restriction;
        if (!actor.Contains('@')) return DocumentRestrictionPolicy.Restriction.None;
        var email = actor.Trim();
        var byEmail = await db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.User.Email == email)
            .Select(cu => (Guid?)cu.UserId).FirstOrDefaultAsync(ct);
        return byEmail is Guid id ? (await ForUserAsync(db, companyId, id, ct)).Restriction : DocumentRestrictionPolicy.Restriction.None;
    }

    /// <summary>ตั้งขอบเขตของคำขอนี้บน DbContext (scoped) — เห็นเฉพาะของตัวเอง ⇒ ตัวกรองแถว · เฉพาะร่าง ⇒ ธงของคำขอ · ไม่จำกัด = ล้างค่า</summary>
    public static void ApplyScope(AccountingDbContext db, Guid userId, UserScope scope)
    {
        db.DocumentOwnerScopeUserId = scope.Restriction.OwnDocumentsOnly ? userId.ToString() : null;
        db.DocumentOwnerScopeEmail = scope.Restriction.OwnDocumentsOnly && !string.IsNullOrWhiteSpace(scope.Email) ? scope.Email : null;
        db.DocumentDraftOnlyRequest = scope.Restriction.DraftOnly;
    }

    /// <summary>ด่านชั้น service: อนุมัติ/ยกเลิก/ออกเลข ทำไม่ได้เมื่อ (ก) คำขอนี้มาจากผู้ "เฉพาะร่าง" หรือ (ข) ผู้กระทำเป็นสมาชิก "เฉพาะร่าง" ·
    /// โยน <see cref="BusinessRuleException"/> 403 พร้อมทางไปต่อ (ส่งขออนุมัติ)</summary>
    public static async Task EnsureMayFinalizeAsync(AccountingDbContext db, Guid companyId, string? actor, string verb, CancellationToken ct = default)
    {
        if (db.DocumentDraftOnlyRequest || (await ForActorAsync(db, companyId, actor, ct)).DraftOnly)
            throw new BusinessRuleException(DocumentRestrictionPolicy.DraftOnlyDeniedMessage(verb), DocumentRestrictionPolicy.DraftOnlyRuleCode, 403);
    }
}
