using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// **ส่งออก CSV/Excel ของหน้ารายการเอกสาร · ซื้อสินค้า · ผู้ติดต่อ · สินค้า** (ทีม X2 · EXPORT_AUDIT 2026-10-14)
///
/// <para>ทุกปุ่มส่งออกเดินผ่าน <b>service ตัวเดียวกับที่หน้าจอใช้ดึงรายการ</b> (ตัวกรองชุดเดียวกัน · ด่านสิทธิ์ทิศทาง/ชั้นความลับเดียวกัน)
/// แล้ววนดึง<b>ทุกหน้า</b> (<see cref="TradeListExport.CollectAllAsync{T}"/>) — แทนของเดิมที่หน้าเว็บขอ <c>pageSize=10000</c> แต่ได้ 200 แถว
/// โดยไม่มีใครรู้ · ไฟล์เขียนโดยตัวกลาง <see cref="TabularExport"/> (ผ่าน <see cref="TradeListExport.Write"/> · BOM · ตัวเลขจริง · กันสูตร)</para>
///
/// <para>ด่านสิทธิ์: ต้องมีสิทธิ์ดูรายการ (ตามหน้าจอ) <b>และ</b> สิทธิ์ส่งออกของชนิดข้อมูลนั้นจาก
/// <see cref="ImportExportPermissionScope.ForExport"/> (ตัวตั้งเดียวกับศูนย์นำเข้า/ส่งออก — เอกสาร/ผู้ติดต่อ/สินค้า = Document.Export)</para>
/// </summary>
[ApiController]
[Authorize]
public class TradeListExportController : ControllerBase
{
    private readonly IDocumentService _documents;
    private readonly IProductService _products;
    private readonly IPermissionService _permissions;
    private readonly AccountingDbContext _db;

    public TradeListExportController(IDocumentService documents, IProductService products,
        IPermissionService permissions, AccountingDbContext db)
    {
        _documents = documents;
        _products = products;
        _permissions = permissions;
        _db = db;
    }

    private ActionResult Deny(string message) => StatusCode(403, new ApiResponse<string>(false, null, message));

    private async Task<ActionResult?> DenyExportAsync(Guid companyId, Guid userId, string entityType)
    {
        var key = ImportExportPermissionScope.ForExport(entityType);
        return await _permissions.HasPermissionAsync(companyId, userId, key)
            ? null
            : Deny(ImportExportPermissionScope.DeniedMessage(entityType, key, isExport: true));
    }

    private FileContentResult FileOf(TabularFile f) => File(f.Content, f.ContentType, f.FileName);

    // ═══════════════════════ เอกสาร (หน้าเอกสาร · หน้าซื้อสินค้า) ═══════════════════════

    /// <summary>
    /// ส่งออกรายการเอกสารตามตัวกรองบนหน้าจอ — พารามิเตอร์ชุดเดียวกับ <c>GET document</c> (+ <c>side</c> · <c>ids</c>)
    /// <para><c>level=documents</c> (ค่าตั้งต้น) = 1 แถว/ใบ · <c>level=lines</c> = 1 แถว/บรรทัดรายการ (กระทบยอดระดับบรรทัด: สินค้า ·
    /// จำนวน · ราคา · ส่วนลด · VAT · WHT · บัญชี) · <c>side</c> = revenue|expense (กติกาเดียวกับ Layout.docSide) ·
    /// <c>ids</c> = เฉพาะใบที่เลือก (ยังต้องผ่านตัวกรอง/สิทธิ์เดียวกัน)</para>
    /// </summary>
    [HttpGet("api/companies/{companyId:guid}/document/export")]
    public async Task<ActionResult> ExportDocuments(Guid companyId,
        [FromQuery] string? format = "xlsx", [FromQuery] string? level = "documents", [FromQuery] string? side = null,
        [FromQuery] List<Guid>? ids = null,
        [FromQuery] DocumentType? type = null, [FromQuery] List<DocumentType>? types = null,
        [FromQuery] string? search = null, [FromQuery] Guid? projectId = null, [FromQuery] Guid? contactId = null,
        [FromQuery] string? status = null, [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null,
        [FromQuery] bool staleOnly = false, [FromQuery] string? lifecycle = null,
        [FromQuery] string? sortBy = null, [FromQuery] bool sortDesc = false, [FromQuery] string? conversion = null)
    {
        var fmt = TradeListExport.NormalizeFormat(format);
        var wantLines = string.Equals((level ?? "").Trim(), "lines", StringComparison.OrdinalIgnoreCase);
        var sideNorm = TradeListExport.NormalizeSide(side);
        var userId = JwtHelper.GetUserIdFromClaims(User);

        if (!DocumentConversionProgress.TryParseFilter(conversion, out var conversionState))
            return BadRequest(new ApiResponse<string>(false, null,
                "ตัวกรอง \"ออกเอกสารต่อ\" ไม่ถูกต้อง — ใช้ได้เฉพาะ None / Partial / Full"));

        if (await DenyExportAsync(companyId, userId, "documents") is { } denyExport) return denyExport;

        // ด่านทิศทาง — ตัวเดียวกับ GET document (Sales ที่ดูได้แค่ฝั่งรายรับ ส่งออกใบสำคัญจ่ายไม่ได้)
        var visibility = await DocumentPermissionHelper.VisibleDirectionsAsync(_permissions, companyId, userId);
        if (type.HasValue && !visibility.Allows(type.Value))
            return Deny($"คุณไม่มีสิทธิ์ดูเอกสารประเภท {type.Value} (ต้องการ Document.Revenue.View หรือ Document.Purchase.View)");

        var effTypes = types is { Count: > 0 } ? types : null;
        if (!type.HasValue && effTypes == null && sideNorm != null) effTypes = TradeListExport.TypesForSide(sideNorm);
        if (effTypes != null && !visibility.ShowsEverything)
            effTypes = effTypes.Where(t => visibility.Allows(t)).ToList();

        var rows = await TradeListExport.CollectAllAsync(
            req => _documents.GetDocumentsForUserAsync(companyId, userId, type, req, projectId, contactId, status,
                fromDate, toDate, null, null, staleOnly, effTypes, conversionState),
            p => new PagedRequest(p, TradeListExport.FetchPageSize, search, sortBy, sortDesc),
            TradeListExport.MaxDocumentRows);

        // ตัวกรองหลังดึง — ชุดเดียวกับ GET document + หน้าเว็บ (ทิศทาง · lifecycle · ฝั่ง · ใบที่เลือก)
        IEnumerable<DocumentResponse> q = rows;
        if (!visibility.ShowsEverything) q = q.Where(d => visibility.Allows(d.DocumentType));
        if (!string.IsNullOrWhiteSpace(lifecycle))
            q = q.Where(d => string.Equals(d.LifecycleStatus, lifecycle, StringComparison.OrdinalIgnoreCase));
        if (sideNorm != null && !type.HasValue)
            q = q.Where(d => TradeListExport.IsOnSide(d.DocumentType, d.CnDnPurchaseSideOverride, sideNorm));
        if (ids is { Count: > 0 })
        {
            var pick = ids.ToHashSet();
            q = q.Where(d => pick.Contains(d.Id));
        }
        var docs = q.ToList();

        var baseName = sideNorm == "revenue" ? "เอกสารรายรับ" : sideNorm == "expense" ? "เอกสารรายจ่าย" : "เอกสาร";
        if (type.HasValue) baseName = DocumentTypeNames.Title(type.Value, "th");

        if (wantLines)
        {
            var lineCount = docs.Sum(d => Math.Max(d.Lines?.Count ?? 0, 1));
            if (lineCount > TradeListExport.MaxLineRows)
                throw new BusinessRuleException(
                    $"บรรทัดรายการตามตัวกรองมี {lineCount:N0} บรรทัด เกินเพดานไฟล์ละ {TradeListExport.MaxLineRows:N0} — กรุณากรองช่วงวันที่ให้แคบลง",
                    "EXPORT-TOO-LARGE");
            var accountIds = docs.SelectMany(d => d.Lines ?? new List<DocumentLineResponse>())
                .Where(l => l.AccountId.HasValue && string.IsNullOrEmpty(l.AccountCode))
                .Select(l => l.AccountId!.Value).Distinct().ToList();
            var accounts = new Dictionary<Guid, (string Code, string Name)>();
            if (accountIds.Count > 0)
            {
                var accRows = await _db.ChartOfAccounts.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && accountIds.Contains(a.Id))
                    .Select(a => new { a.Id, a.AccountCode, a.AccountName })
                    .ToListAsync();
                foreach (var a in accRows) accounts[a.Id] = (a.AccountCode, a.AccountName);
            }
            var lineRows = docs.SelectMany(d => TradeListExport.DocumentLineRows(d, accounts));
            return FileOf(TradeListExport.Write(fmt, TradeListExport.DocumentLineColumns, lineRows,
                baseName + "_บรรทัดรายการ", "บรรทัดรายการ"));
        }

        var extras = await LoadDocumentExtrasAsync(companyId, docs);
        var headerRows = docs.Select(d => TradeListExport.DocumentRow(d, extras.GetValueOrDefault(d.Id)));
        return FileOf(TradeListExport.Write(fmt, TradeListExport.DocumentColumns, headerRows, baseName, "เอกสาร"));
    }

    /// <summary>ผู้สร้าง (ชื่อผู้ใช้จาก <c>Document.CreatedBy</c> = user id) + เลขที่ใบต้นทาง — batch (query คงที่ ไม่ใช่ N+1) ·
    /// ใบที่ถูกซ่อนไม่เปิดเผยผู้สร้าง/ใบต้นทาง</summary>
    private async Task<Dictionary<Guid, TradeListExport.DocumentExtras>> LoadDocumentExtrasAsync(
        Guid companyId, List<DocumentResponse> docs)
    {
        var visibleIds = docs.Where(d => !d.IsRedacted).Select(d => d.Id).ToList();
        var result = new Dictionary<Guid, TradeListExport.DocumentExtras>();
        if (visibleIds.Count == 0) return result;

        var creators = new Dictionary<Guid, string?>();
        foreach (var chunk in visibleIds.Chunk(2000))
        {
            var part = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && chunk.Contains(d.Id))
                .Select(d => new { d.Id, d.CreatedBy })
                .ToListAsync();
            foreach (var p in part) creators[p.Id] = p.CreatedBy;
        }
        var userIds = creators.Values
            .Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().ToList();
        var userNames = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName);

        var relatedIds = docs.Where(d => !d.IsRedacted && d.RelatedDocumentId.HasValue)
            .Select(d => d.RelatedDocumentId!.Value).Distinct().ToList();
        var relatedNumbers = new Dictionary<Guid, string>();
        foreach (var chunk in relatedIds.Chunk(2000))
        {
            var part = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && chunk.Contains(d.Id))
                .Select(d => new { d.Id, d.DocumentNumber })
                .ToListAsync();
            foreach (var p in part) relatedNumbers[p.Id] = p.DocumentNumber;
        }

        foreach (var d in docs.Where(x => !x.IsRedacted))
        {
            var raw = creators.GetValueOrDefault(d.Id);
            string? creator = raw;
            if (Guid.TryParse(raw, out var uid)) creator = userNames.TryGetValue(uid, out var n) ? n : raw;
            var src = d.RelatedDocumentId.HasValue ? relatedNumbers.GetValueOrDefault(d.RelatedDocumentId.Value) : null;
            result[d.Id] = new TradeListExport.DocumentExtras(creator, src);
        }
        return result;
    }

    // ═══════════════════════ ผู้ติดต่อ ═══════════════════════

    /// <summary>ส่งออกทะเบียนผู้ติดต่อตามตัวกรองบนหน้าผู้ติดต่อ (ค้นหา · ลูกค้า/ผู้จำหน่าย) — query เดียวกับ <c>GET document/contacts</c></summary>
    [HttpGet("api/companies/{companyId:guid}/document/contacts/export")]
    public async Task<ActionResult> ExportContacts(Guid companyId, [FromQuery] string? format = "xlsx",
        [FromQuery] bool? isCustomer = null, [FromQuery] bool? isSupplier = null, [FromQuery] string? search = null)
    {
        var fmt = TradeListExport.NormalizeFormat(format);
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await DenyExportAsync(companyId, userId, "contacts") is { } deny) return deny;
        var rows = await TradeListExport.CollectAllAsync(
            req => _documents.GetContactsAsync(companyId, isCustomer, isSupplier, search, req),
            p => new PagedRequest(p, TradeListExport.FetchPageSize),
            TradeListExport.MaxMasterRows);
        var baseName = isCustomer == true ? "ลูกค้า" : isSupplier == true ? "ผู้จำหน่าย" : "ผู้ติดต่อ";
        return FileOf(TradeListExport.Write(fmt, TradeListExport.ContactColumns, rows.Select(TradeListExport.ContactRow), baseName, "ผู้ติดต่อ"));
    }

    // ═══════════════════════ สินค้า/บริการ ═══════════════════════

    /// <summary>ส่งออกรายการสินค้า/บริการตามคำค้น + ประเภทบนหน้าสินค้า — query เดียวกับ <c>GET product</c></summary>
    [HttpGet("api/companies/{companyId:guid}/product/export")]
    public async Task<ActionResult> ExportProducts(Guid companyId, [FromQuery] string? format = "xlsx", [FromQuery] string? search = null,
        [FromQuery] ProductType? productType = null)
    {
        var fmt = TradeListExport.NormalizeFormat(format);
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await DenyExportAsync(companyId, userId, "products") is { } deny) return deny;
        var rows = await TradeListExport.CollectAllAsync(
            req => _products.GetAllAsync(companyId, req, productType),
            p => new PagedRequest(p, TradeListExport.FetchPageSize, search),
            TradeListExport.MaxMasterRows);
        return FileOf(TradeListExport.Write(fmt, TradeListExport.ProductColumns, rows.Select(TradeListExport.ProductRow), "สินค้าและบริการ", "สินค้า"));
    }
}
