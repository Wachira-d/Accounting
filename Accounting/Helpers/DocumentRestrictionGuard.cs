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

    /// <summary>ฝ่ายค้าน 276: ค่าจำกัดที่เข้มที่สุดของทุกบริษัทที่ผู้ใช้เป็นสมาชิก — ทางเข้าที่ไม่ผูกบริษัทเดียว (ไม่มี {companyId} ใน route)
    /// ต้องไม่ปล่อยข้อมูลของบริษัทที่เขาถูกจำกัด (เช่นภาพรวมข้ามบริษัท) · ผู้เรียก: <c>DocumentRestrictionFilter</c></summary>
    public static async Task<DocumentRestrictionPolicy.Restriction> StrictestAsync(AccountingDbContext db, Guid userId, CancellationToken ct = default)
    {
        var rows = await db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.UserId == userId)
            .Select(cu => new
            {
                cu.Role, cu.OwnDocumentsOnly, cu.DraftDocumentsOnly,
                RoleOwn = cu.CompanyRole != null && cu.CompanyRole.OwnDocumentsOnly,
                RoleDraft = cu.CompanyRole != null && cu.CompanyRole.DraftDocumentsOnly,
            }).ToListAsync(ct);
        bool own = false, draft = false;
        foreach (var r in rows)
        {
            var e = DocumentRestrictionPolicy.Effective(r.Role, r.OwnDocumentsOnly, r.DraftDocumentsOnly, r.RoleOwn, r.RoleDraft);
            own |= e.OwnDocumentsOnly; draft |= e.DraftOnly;
        }
        return new DocumentRestrictionPolicy.Restriction(own, draft);
    }

    /// <summary>ฝ่ายค้าน MED-3: บริษัทที่ผู้ใช้ "เห็นเฉพาะเอกสารที่ตัวเองสร้าง" (ค่าที่มีผล) — ผู้เรียก: รายการแจ้งเตือน (<c>NotificationService</c>)</summary>
    public static async Task<List<Guid>> OwnOnlyCompanyIdsAsync(AccountingDbContext db, Guid userId, CancellationToken ct = default)
    {
        var rows = await db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.UserId == userId)
            .Select(cu => new
            {
                cu.CompanyId, cu.Role, cu.OwnDocumentsOnly, cu.DraftDocumentsOnly,
                RoleOwn = cu.CompanyRole != null && cu.CompanyRole.OwnDocumentsOnly,
                RoleDraft = cu.CompanyRole != null && cu.CompanyRole.DraftDocumentsOnly,
            }).ToListAsync(ct);
        return rows.Where(r => DocumentRestrictionPolicy.Effective(r.Role, r.OwnDocumentsOnly, r.DraftDocumentsOnly, r.RoleOwn, r.RoleDraft).OwnDocumentsOnly)
            .Select(r => r.CompanyId).ToList();
    }

    /// <summary>ฝ่ายค้าน MED-3: แจ้งเตือนเหตุการณ์นี้ถึงผู้ใช้คนนี้ได้ไหม — ผู้ "เห็นเฉพาะของตัวเอง" ได้เฉพาะเรื่องของเอกสารที่ตัวเองสร้าง
    /// (เรื่องอื่นของบริษัท เช่นรับชำระ/ใบของคนอื่น = ไม่ได้) · ผู้เรียก: <c>NotificationEngine</c> (ทุกช่องส่วนตัว)</summary>
    public static async Task<bool> MayReceiveAsync(AccountingDbContext db, Guid companyId, Guid userId, string? entityType, Guid? entityId,
        CancellationToken ct = default)
    {
        if (!(await ForUserAsync(db, companyId, userId, ct)).Restriction.OwnDocumentsOnly) return true;
        if (entityType != "Document" || entityId is not Guid docId) return false;
        var uid = userId.ToString();
        return await db.Documents.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(d => d.Id == docId && d.CompanyId == companyId && (d.CreatedByUserId == userId || (d.CreatedByUserId == null && d.CreatedBy == uid)), ct);
    }

    /// <summary>ผู้กระทำ (id ผู้ใช้ หรืออีเมลสมาชิกของบริษัท — ทาง LINE) → id ผู้ใช้ · ป้ายระบบ = null · ผู้เรียก: ด่าน SoD ของคำขออนุมัติ (ฝ่ายค้าน LOW-7)</summary>
    public static async Task<Guid?> ActorUserIdAsync(AccountingDbContext db, Guid companyId, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor)) return null;
        if (Guid.TryParse(actor, out var uid)) return uid;
        if (!actor.Contains('@')) return null;
        var email = actor.Trim();
        return await db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.User.Email == email)
            .Select(cu => (Guid?)cu.UserId).FirstOrDefaultAsync(ct);
    }

    /// <summary>ผู้กระทำที่ service ได้รับเป็นข้อความ (id ผู้ใช้ หรืออีเมลสมาชิก — ทาง LINE ส่งอีเมล) → ค่าที่มีผล · ป้ายระบบ (<c>system:*</c> ฯลฯ) = ไม่จำกัด</summary>
    private static async Task<DocumentRestrictionPolicy.Restriction> ForActorAsync(AccountingDbContext db, Guid companyId, string? actor, CancellationToken ct = default)
        => await ActorUserIdAsync(db, companyId, actor, ct) is Guid id
            ? (await ForUserAsync(db, companyId, id, ct)).Restriction
            : DocumentRestrictionPolicy.Restriction.None;

    /// <summary>ตั้งขอบเขตของคำขอนี้บน DbContext (scoped) — เห็นเฉพาะของตัวเอง ⇒ ตัวกรองแถว · เฉพาะร่าง ⇒ ธงของคำขอ · ไม่จำกัด = ล้างค่า</summary>
    public static void ApplyScope(AccountingDbContext db, Guid userId, UserScope scope)
    {
        db.DocumentOwnerScopeUserId = scope.Restriction.OwnDocumentsOnly ? userId.ToString() : null;
        db.DocumentOwnerScopeGuid = scope.Restriction.OwnDocumentsOnly ? userId : null;
        db.DocumentRestrictedActorId = scope.Restriction.IsRestricted ? userId : null;
        db.DocumentOwnerScopeEmail = scope.Restriction.OwnDocumentsOnly && !string.IsNullOrWhiteSpace(scope.Email) ? scope.Email : null;
        db.DocumentDraftOnlyRequest = scope.Restriction.DraftOnly;
    }

    /// <summary>ฝ่ายค้าน MED-4: ทางเขียนของผู้เห็นเฉพาะของตัวเอง — หลังตัวกรองคำขอตรวจ id ทุกตัวแล้ว ยกตัวกรองแถวเพื่อให้ด่านระดับบริษัทของ service
    /// เห็นทั้งบริษัท · ธง "เฉพาะร่าง" + ผู้สร้างของคำขอคงอยู่ (ยังอนุมัติไม่ได้ · ใบใหม่เป็นของเขา) · ผู้เรียกเดียว: <c>DocumentRestrictionFilter</c></summary>
    public static void LiftRowFilterForWrite(AccountingDbContext db)
    {
        db.DocumentOwnerScopeUserId = null;
        db.DocumentOwnerScopeGuid = null;
        db.DocumentOwnerScopeEmail = null;
    }

    /// <summary>ด่านชั้น service: อนุมัติ/ยกเลิก/ออกเลข ทำไม่ได้เมื่อ (ก) คำขอนี้มาจากผู้ "เฉพาะร่าง" หรือ (ข) ผู้กระทำเป็นสมาชิก "เฉพาะร่าง" ·
    /// โยน <see cref="BusinessRuleException"/> 403 พร้อมทางไปต่อ (ส่งขออนุมัติ)</summary>
    /// <summary>รอบ 283 ฝ่ายค้าน MED-A2: เงื่อนไขเดียวกับ <see cref="EnsureMayFinalizeAsync"/> แบบไม่โยน — เส้นที่ออกเลขเองนอก ApproveDocumentAsync
    /// (ใบสำคัญจ่ายหลักฐาน) ใช้ตัดสิน "ออกเลข หรือ เป็นร่างรออนุมัติ"</summary>
    public static async Task<bool> MayFinalizeAsync(AccountingDbContext db, Guid companyId, string? actor, CancellationToken ct = default)
        => !(db.DocumentDraftOnlyRequest || (await ForActorAsync(db, companyId, actor, ct)).DraftOnly);

    public static async Task EnsureMayFinalizeAsync(AccountingDbContext db, Guid companyId, string? actor, string verb, CancellationToken ct = default)
    {
        if (db.DocumentDraftOnlyRequest || (await ForActorAsync(db, companyId, actor, ct)).DraftOnly)
            throw new BusinessRuleException(DocumentRestrictionPolicy.DraftOnlyDeniedMessage(verb), DocumentRestrictionPolicy.DraftOnlyRuleCode, 403);
    }
}
