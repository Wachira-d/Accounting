using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Filters;

/// <summary>
/// รอบ 276 — ด่านทุกคำขอของผู้ใช้ "เห็นเฉพาะเอกสารที่ตัวเองสร้าง" / "สร้าง/แก้ได้เฉพาะร่าง" (global · ลงทะเบียนหลัง <c>TenantGuardFilter</c>) ·
/// ทำงานเฉพาะ route ที่มี <c>companyId</c> + ผู้ใช้ล็อกอิน (API key ไม่ใช่ผู้ใช้ ⇒ ข้าม) ·
/// <list type="number">
/// <item>อ่านค่าที่มีผล (<see cref="DocumentRestrictionGuard.ForUserAsync"/>) แล้วตั้งขอบเขตบน DbContext ของคำขอ (ตัวกรองแถว EF ของ Documents)</item>
/// <item>ทางเข้าต้องอยู่ในรายการอนุญาต (<see cref="DocumentRestrictionPolicy.Decide"/>) — ไม่อยู่ ⇒ 403 + เหตุ (ห้าม silent no-op)</item>
/// <item>id เอกสารใน route/ไฟล์แนบ/entity ต้องเป็นของตัวเอง ⇒ ไม่ใช่ = <b>404</b> (ไม่ยืนยันว่ามีใบนั้น) · เขียนได้เฉพาะใบ Draft/Rejected</item>
/// </list>
/// ผู้ใช้ไม่ถูกจำกัด = ผ่านทันที (หนึ่ง query ต่อคำขอ) · พฤติกรรมเดิมไม่เปลี่ยน
/// </summary>
public class DocumentRestrictionFilter : IAsyncActionFilter
{
    private static readonly string[] DocumentIdRouteKeys = { "documentId", "invoiceId", "sourceId" };
    private readonly AccountingDbContext _db;

    public DocumentRestrictionFilter(AccountingDbContext db) => _db = db;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!context.RouteData.Values.TryGetValue("companyId", out var rawCompany)
            || !Guid.TryParse(rawCompany?.ToString(), out var companyId)
            || http.User?.Identity?.IsAuthenticated != true
            || (http.Items.TryGetValue("IsApiKeyAuth", out var apiKey) && apiKey is true)
            || !Guid.TryParse(http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            await next();
            return;
        }

        var scope = await DocumentRestrictionGuard.ForUserAsync(_db, companyId, userId, http.RequestAborted);
        DocumentRestrictionGuard.ApplyScope(_db, userId, scope);
        var r = scope.Restriction;
        if (!r.IsRestricted) { await next(); return; }

        var method = http.Request.Method;
        var decision = DocumentRestrictionPolicy.Decide(r, method, context.ActionDescriptor.AttributeRouteInfo?.Template);
        if (!decision.Allowed)
        {
            context.Result = Deny(403, decision.Message!);
            return;
        }

        // ── id เอกสารใน route ต้องมองเห็นได้ (ตัวกรองแถว EF ตั้งแล้วด้านบน) ──
        Guid? docForStatus = null;
        foreach (var key in DocumentIdRouteKeys)
        {
            if (!context.RouteData.Values.TryGetValue(key, out var raw) || !Guid.TryParse(raw?.ToString(), out var docId)) continue;
            if (r.OwnDocumentsOnly && !await _db.Documents.AsNoTracking().AnyAsync(d => d.Id == docId && d.CompanyId == companyId, http.RequestAborted))
            {
                context.Result = Deny(404, DocumentRestrictionPolicy.NotFoundMessage);
                return;
            }
            docForStatus ??= docId;
        }

        var checks = decision.Rule?.Checks ?? DocumentRestrictionPolicy.RouteCheck.None;
        if (checks.HasFlag(DocumentRestrictionPolicy.RouteCheck.DocumentEntityOnly))
        {
            var entityType = Value(context, "entityType");
            var entityIdRaw = Value(context, "entityId");
            if (!string.Equals(entityType, "Document", StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(entityIdRaw, out var entityId))
            {
                context.Result = Deny(403, r.OwnDocumentsOnly ? DocumentRestrictionPolicy.OwnScopeDeniedMessage : DocumentRestrictionPolicy.DraftOnlyDeniedMessage());
                return;
            }
            if (r.OwnDocumentsOnly && !await _db.Documents.AsNoTracking().AnyAsync(d => d.Id == entityId && d.CompanyId == companyId, http.RequestAborted))
            {
                context.Result = Deny(404, DocumentRestrictionPolicy.NotFoundMessage);
                return;
            }
            docForStatus ??= entityId;
        }
        if (checks.HasFlag(DocumentRestrictionPolicy.RouteCheck.AttachmentOfDocument))
        {
            var attId = Guid.TryParse(Value(context, "attachmentId"), out var a) ? a : Guid.Empty;
            var att = await _db.FileAttachments.AsNoTracking()
                .Where(x => x.Id == attId && x.CompanyId == companyId)
                .Select(x => new { x.EntityType, x.EntityId }).FirstOrDefaultAsync(http.RequestAborted);
            if (att == null || !string.Equals(att.EntityType, "Document", StringComparison.OrdinalIgnoreCase)
                || (r.OwnDocumentsOnly && !await _db.Documents.AsNoTracking().AnyAsync(d => d.Id == att.EntityId && d.CompanyId == companyId, http.RequestAborted)))
            {
                context.Result = Deny(404, "ไม่พบไฟล์แนบ");
                return;
            }
            docForStatus ??= att.EntityId;
        }
        if (checks.HasFlag(DocumentRestrictionPolicy.RouteCheck.DraftStatus) && r.DraftOnly && docForStatus is Guid statusDoc)
        {
            var status = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                .Where(d => d.Id == statusDoc && d.CompanyId == companyId)
                .Select(d => (Models.Enums.DocumentStatus?)d.Status).FirstOrDefaultAsync(http.RequestAborted);
            if (status is { } st && !DocumentRestrictionPolicy.IsEditableDraftStatus(st))
            {
                context.Result = Deny(403, DocumentRestrictionPolicy.DraftStatusDeniedMessage);
                return;
            }
        }

        await next();
    }

    private static string? Value(ActionExecutingContext context, string key)
    {
        if (context.RouteData.Values.TryGetValue(key, out var v) && v != null) return v.ToString();
        var q = context.HttpContext.Request.Query[key];
        return q.Count > 0 ? q[0] : null;
    }

    private static ObjectResult Deny(int status, string message) =>
        new(new ApiResponse<object>(false, null, message)) { StatusCode = status };
}
