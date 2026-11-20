using System.Collections;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Filters;

/// <summary>
/// รอบ 276 — ด่านทุกคำขอของผู้ใช้ "เห็นเฉพาะเอกสารที่ตัวเองสร้าง" / "สร้าง/แก้ได้เฉพาะร่าง" (global · ลงทะเบียนหลัง <c>TenantGuardFilter</c>) ·
/// ผูกกับ <b>ผู้ใช้ + บริษัทที่ทำงานอยู่</b> ไม่ใช่รูป URL (ฝ่ายค้าน 276): route มี {companyId} ⇒ บริษัทนั้น · ไม่มี ⇒ บริษัทจาก header
/// <c>X-Company-Id</c> สำหรับตัวกรองแถว + ค่าจำกัดที่เข้มที่สุดของทุกบริษัทที่ผู้ใช้เป็นสมาชิกสำหรับรายการอนุญาต (ภาพรวมข้ามบริษัทต้องไม่รั่ว) ·
/// ทางเข้านิรนาม ([AllowAnonymous]) / API key ไม่ใช่ผู้ใช้ ⇒ ข้าม
/// <list type="number">
/// <item>ตั้งขอบเขตบน DbContext ของคำขอ (ตัวกรองแถว EF ของ Documents + ผู้ใช้ที่ถูกจำกัดสำหรับประทับผู้สร้าง)</item>
/// <item>ทางเข้าต้องอยู่ในรายการอนุญาต (<see cref="DocumentRestrictionPolicy.Decide"/>) — ไม่อยู่ ⇒ 403 + เหตุ (ห้าม silent no-op)</item>
/// <item>id เอกสารใน route/entity/ไฟล์แนบ/body (<c>*DocumentId</c> · <c>SourceLineId</c>) ต้องเป็นของตัวเอง ⇒ ไม่ใช่ = <b>404</b> ·
/// สแกน OCR ต้องเป็นของผู้ใช้เอง · เขียนได้เฉพาะใบ Draft/Rejected</item>
/// </list>
/// ผู้ใช้ไม่ถูกจำกัด = ผ่าน (หนึ่ง query ต่อคำขอ) · พฤติกรรมเดิมไม่เปลี่ยน
/// </summary>
public class DocumentRestrictionFilter : IAsyncActionFilter
{
    private static readonly string[] DocumentIdRouteKeys = { "documentId", "invoiceId", "sourceId" };
    private readonly AccountingDbContext _db;

    public DocumentRestrictionFilter(AccountingDbContext db) => _db = db;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (http.User?.Identity?.IsAuthenticated != true
            || (http.Items.TryGetValue("IsApiKeyAuth", out var apiKey) && apiKey is true)
            || context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any()
            || !Guid.TryParse(http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            await next();
            return;
        }

        var routeCompany = context.RouteData.Values.TryGetValue("companyId", out var rawCompany) && Guid.TryParse(rawCompany?.ToString(), out var rc)
            ? rc : (Guid?)null;
        var workingCompany = routeCompany ?? TenantCompanyId.FromHttp(http).CompanyId;
        DocumentRestrictionGuard.UserScope scope = workingCompany is Guid wc
            ? await DocumentRestrictionGuard.ForUserAsync(_db, wc, userId, http.RequestAborted)
            : new DocumentRestrictionGuard.UserScope(DocumentRestrictionPolicy.Restriction.None, null);
        DocumentRestrictionGuard.ApplyScope(_db, userId, scope);
        // ไม่มีบริษัทใน route ⇒ ทางเข้านี้ไม่ผูกบริษัทเดียว (เช่นภาพรวมข้ามบริษัท) ⇒ ใช้ค่าที่เข้มที่สุดของทุกบริษัท
        var r = routeCompany != null ? scope.Restriction : await DocumentRestrictionGuard.StrictestAsync(_db, userId, http.RequestAborted);
        if (!r.IsRestricted) { await next(); return; }
        var companyId = routeCompany ?? workingCompany ?? Guid.Empty;

        var decision = DocumentRestrictionPolicy.Decide(r, http.Request.Method, context.ActionDescriptor.AttributeRouteInfo?.Template);
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
            if (r.OwnDocumentsOnly && !await VisibleAsync(companyId, docId, http.RequestAborted))
            {
                context.Result = Deny(404, DocumentRestrictionPolicy.NotFoundMessage);
                return;
            }
            docForStatus ??= docId;
        }

        // ── ฝ่ายค้าน MED-5: id เอกสาร/บรรทัดต้นทางใน body (RelatedDocumentId · SourceLineId ฯลฯ) ต้องเป็นของที่มองเห็น ⇒ ไม่ใช่ = 404 (ไม่ข้ามเงียบ) ──
        if (r.OwnDocumentsOnly)
        {
            var (docIds, lineIds) = BodyReferences(context.ActionArguments.Values);
            foreach (var id in docIds)
                if (!await VisibleAsync(companyId, id, http.RequestAborted))
                { context.Result = Deny(404, DocumentRestrictionPolicy.NotFoundMessage + "ต้นทางที่อ้างถึง"); return; }
            foreach (var lid in lineIds)
                if (!await _db.Documents.AsNoTracking().AnyAsync(d => d.CompanyId == companyId && d.Lines.Any(l => l.Id == lid), http.RequestAborted))
                { context.Result = Deny(404, "ไม่พบบรรทัดต้นทางที่อ้างถึง"); return; }
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
            if (r.OwnDocumentsOnly && !await VisibleAsync(companyId, entityId, http.RequestAborted))
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
                || (r.OwnDocumentsOnly && !await VisibleAsync(companyId, att.EntityId, http.RequestAborted)))
            {
                context.Result = Deny(404, "ไม่พบไฟล์แนบ");
                return;
            }
            docForStatus ??= att.EntityId;
        }
        if (checks.HasFlag(DocumentRestrictionPolicy.RouteCheck.OwnScan) && !await OwnScanAsync(context, companyId, userId, http.RequestAborted))
        {
            context.Result = Deny(404, "ไม่พบสแกนนี้ — บัญชีของคุณใช้ได้เฉพาะสแกนที่คุณอัปโหลดเอง");
            return;
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

        // ฝ่ายค้าน MED-4: ทางเขียน (สร้าง/แก้/แปลง/คัดลอก/ขออนุมัติ) — ทุก id ที่อ้างถูกตรวจว่าเป็นของตัวเองแล้วข้างบน ⇒ ด่านระดับบริษัทใน service
        // (ยอดที่แปลงไปแล้ว · ผลรวมใบลดหนี้ · ใบซ้ำ · สต็อก) ต้องเห็นทั้งบริษัท ไม่ใช่เห็นแค่ใบของเขา ⇒ ยกตัวกรองแถวของคำขอนี้ (ธงเฉพาะร่าง + ผู้สร้างคงอยู่)
        if (r.OwnDocumentsOnly && DocumentRestrictionPolicy.IsWrite(http.Request.Method))
            DocumentRestrictionGuard.LiftRowFilterForWrite(_db);

        await next();
    }

    private Task<bool> VisibleAsync(Guid companyId, Guid docId, CancellationToken ct) =>
        _db.Documents.AsNoTracking().AnyAsync(d => d.Id == docId && d.CompanyId == companyId, ct);

    /// <summary>สแกน (scanId) / ไฟล์ (fileAttachmentId) ใน route ต้องเป็นของผู้ใช้คนนี้ (ผู้อัปโหลด หรือผู้สร้างแถวสแกน)</summary>
    private async Task<bool> OwnScanAsync(ActionExecutingContext context, Guid companyId, Guid userId, CancellationToken ct)
    {
        var uid = userId.ToString();
        if (Guid.TryParse(Value(context, "scanId"), out var scanId))
        {
            var scan = await _db.OcrScanResults.AsNoTracking().Where(s => s.Id == scanId && s.CompanyId == companyId)
                .Select(s => new { s.CreatedBy, s.FileAttachmentId }).FirstOrDefaultAsync(ct);
            if (scan == null) return false;
            if (scan.CreatedBy == uid) return true;
            return scan.FileAttachmentId is Guid fa
                && await _db.FileAttachments.AsNoTracking().AnyAsync(f => f.Id == fa && f.CompanyId == companyId && f.UploadedByUserId == userId, ct);
        }
        if (Guid.TryParse(Value(context, "fileAttachmentId"), out var fileId))
            return await _db.FileAttachments.AsNoTracking().AnyAsync(f => f.Id == fileId && f.CompanyId == companyId && f.UploadedByUserId == userId, ct);
        return false;
    }

    /// <summary>id เอกสาร/บรรทัดที่ body อ้าง — property ชนิด Guid/Guid? ที่ชื่อลงท้าย <c>DocumentId</c> และ <c>SourceLineId</c>
    /// (ลึก 2 ชั้น รวมรายการบรรทัด) · Guid.Empty = "ล้างค่า" ไม่ใช่การอ้าง</summary>
    internal static (List<Guid> Docs, List<Guid> Lines) BodyReferences(IEnumerable<object?> args)
    {
        var docs = new List<Guid>();
        var lines = new List<Guid>();
        void Walk(object? o, int depth)
        {
            if (o == null || depth > 2) return;
            var t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is string || o is Guid || o is DateTime || o is decimal) return;
            if (o is IEnumerable seq) { foreach (var x in seq) Walk(x, depth + 1); return; }
            if (t.Namespace?.StartsWith("Accounting.Models.DTOs") != true) return;
            foreach (var p in t.GetProperties())
            {
                if (p.GetIndexParameters().Length > 0) continue;
                var v = p.GetValue(o);
                if (v is Guid g && g != Guid.Empty)
                {
                    if (p.Name.EndsWith("DocumentId", StringComparison.Ordinal)) docs.Add(g);
                    else if (p.Name == "SourceLineId") lines.Add(g);
                }
                else if (v != null && v is not string && (v is IEnumerable || v.GetType().Namespace?.StartsWith("Accounting.Models.DTOs") == true))
                    Walk(v, depth + 1);
            }
        }
        foreach (var a in args) Walk(a, 0);
        return (docs.Distinct().ToList(), lines.Distinct().ToList());
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
