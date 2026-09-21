using System.Globalization;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Email;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class DocumentController : ControllerBase
{
    private readonly IDocumentService _documentService;
    private readonly IDocumentEmailService _docEmailService;
    private readonly AccountingDbContext _db;
    private readonly IPermissionService _permissions;

    public DocumentController(IDocumentService documentService, IDocumentEmailService docEmailService,
        AccountingDbContext db, IPermissionService permissions, ITaxService taxService)
    {
        _documentService = documentService;
        _docEmailService = docEmailService;
        _db = db;
        _permissions = permissions;
        _taxService = taxService;
    }
    private readonly ITaxService _taxService;

    private async Task<DocumentType?> GetDocumentTypeAsync(Guid companyId, Guid documentId) =>
        await _db.Documents.Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => (DocumentType?)d.DocumentType).FirstOrDefaultAsync();

    private ActionResult<ApiResponse<T>> Forbid403<T>(string th)
        => StatusCode(403, new ApiResponse<T>(false, default, th));

    /// <summary>ระดับสิทธิ์ที่ action ต้องการ — ใช้กับ <see cref="DenyDocAsync"/></summary>
    private enum DocPerm { Create, Approve, Void }

    /// <summary>
    /// **ด่านสิทธิ์ของเอกสาร — ที่เดียว** คืน `null` = ผ่าน · คืนข้อความไทย = ปฏิเสธ
    ///
    /// <para>═══ ที่มา (ผลตรวจ A-D2) ═══ `[Authorize]` ระดับคลาสตอบแค่ "ล็อกอินอยู่ไหม"
    /// ⇒ ตอนแรกมีแต่ create/approve/void ที่เช็คสิทธิ์ ส่วน **PUT · DELETE · convert ·
    /// payments · write-off** ไม่เช็คเลย ⇒ สมาชิกที่ระบบตั้งใจไม่ให้สร้างเอกสาร
    /// (Staff/Viewer ที่ไม่ได้ grant) กลับ **แก้ · ลบ · แปลง · บันทึกรับ-จ่ายเงิน ·
    /// ตัดหนี้สูญ** ได้ทั้งหมด — ลง JE จริงโดยไม่ต้องมีสิทธิ์อะไรเลย</para>
    ///
    /// <para>รวมเป็นเมธอดเดียวเพราะข้อความปฏิเสธต้องบอก **ชื่อคีย์สิทธิ์ที่ต้องขอ**
    /// ให้ตรงกันทุกจุด — 20 จุดที่ต่างคนต่างแต่งข้อความจะ drift แน่นอน</para>
    ///
    /// <para>Owner/SystemAdmin/Accountant ผ่านอัตโนมัติที่ `PermissionService`
    /// (built-in role) ⇒ ด่านนี้กระทบเฉพาะ role ที่ต้อง grant อยู่แล้ว</para>
    /// </summary>
    private async Task<string?> DenyDocAsync(
        Guid companyId, Guid userId, DocumentType type, DocPerm perm, string verb)
    {
        var ok = perm switch
        {
            DocPerm.Create => await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userId, type),
            DocPerm.Approve => await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, type),
            _ => await DocumentPermissionHelper.CanVoidAsync(_permissions, companyId, userId, type),
        };
        if (ok) return null;
        var dir = DocumentPermissionHelper.IsRevenue(type) ? "Revenue"
            : DocumentPermissionHelper.IsPurchase(type) ? "Purchase" : null;
        return $"ไม่มีสิทธิ์{verb}เอกสาร {type} (ต้องการ Document.{perm}"
            + (dir == null ? ")" : $" หรือ Document.{dir}.{perm})");
    }

    /// <summary>ด่านสิทธิ์ที่ไม่ผูกกับเอกสารใบใดใบหนึ่ง (เช่นผู้ติดต่อ, งานล้างข้อมูล
    /// ทั้งบริษัท) — คืน `null` = ผ่าน</summary>
    private async Task<string?> DenyKeyAsync(Guid companyId, Guid userId, string key, string verb)
        => await _permissions.HasPermissionAsync(companyId, userId, key)
            ? null
            : $"ไม่มีสิทธิ์{verb} (ต้องการ {key.Replace("perm:", "")})";

    // ===== Send document via email =====

    [HttpPost("{documentId:guid}/send-email")]
    public async Task<ActionResult<ApiResponse<DocumentEmailLogResponse>>> SendEmail(
        Guid companyId, Guid documentId, [FromBody] SendDocumentEmailRequest request)
    {
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentEmailLogResponse>(false, null, "ไม่พบเอกสาร"));
        var deny = await DenyDocAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
            docType.Value, DocPerm.Create, "ส่งอีเมล");
        if (deny != null) return Forbid403<DocumentEmailLogResponse>(deny);
        var actor = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        var log = await _docEmailService.SendDocumentEmailAsync(companyId, documentId, request, actor);
        var dto = MapEmailLog(log);
        return log.Status == EmailLogStatus.Sent
            ? Ok(new ApiResponse<DocumentEmailLogResponse>(true, dto, "ส่งอีเมลสำเร็จ"))
            : Ok(new ApiResponse<DocumentEmailLogResponse>(false, dto, log.ErrorMessage ?? "ส่งอีเมลไม่สำเร็จ"));
    }

    [HttpGet("{documentId:guid}/email-logs")]
    public async Task<ActionResult<ApiResponse<List<DocumentEmailLogResponse>>>> GetEmailLogs(
        Guid companyId, Guid documentId)
    {
        var logs = await _docEmailService.GetDocumentEmailLogsAsync(companyId, documentId);
        return Ok(new ApiResponse<List<DocumentEmailLogResponse>>(true,
            logs.Select(MapEmailLog).ToList()));
    }

    /// <summary>สร้าง/ต่ออายุลิงก์ให้ลูกค้ากดยอมรับใบเสนอราคาออนไลน์ —
    /// คืน URL สาธารณะ (token 64 hex, อายุ 30 วัน). เรียกซ้ำ = ออก token
    /// ใหม่ (ลิงก์เดิมใช้ไม่ได้ — ทำหน้าที่ revoke ไปในตัว).</summary>
    [HttpPost("{documentId:guid}/quotation-accept-link")]
    public async Task<ActionResult<ApiResponse<object>>> CreateQuotationAcceptLink(
        Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d =>
            d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted);
        if (doc == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบเอกสาร"));
        if (doc.DocumentType != Models.Enums.DocumentType.Quotation)
            return BadRequest(new ApiResponse<object>(false, null,
                "ลิงก์ยอมรับออนไลน์ใช้ได้เฉพาะใบเสนอราคา"));
        var denyLink = await DenyDocAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
            doc.DocumentType, DocPerm.Create, "สร้างลิงก์ยอมรับของ");
        if (denyLink != null) return Forbid403<object>(denyLink);
        if (doc.Status is Models.Enums.DocumentStatus.Draft or Models.Enums.DocumentStatus.Voided
            or Models.Enums.DocumentStatus.Rejected)
            return BadRequest(new ApiResponse<object>(false, null,
                "ต้องอนุมัติใบเสนอราคาก่อนจึงส่งลิงก์ให้ลูกค้าได้"));

        doc.QuotationAcceptToken = PublicQuotationController.NewToken();
        doc.QuotationAcceptTokenExpiresAt = DateTime.UtcNow.AddDays(30);
        await _db.SaveChangesAsync();

        var url = $"{Request.Scheme}://{Request.Host}/pages/quotation-accept.html?token={doc.QuotationAcceptToken}";
        return Ok(new ApiResponse<object>(true, new
        {
            url,
            token = doc.QuotationAcceptToken,
            expiresAt = doc.QuotationAcceptTokenExpiresAt,
            acceptedAt = doc.QuotationAcceptedAt,
        }, "สร้างลิงก์แล้ว — ส่งให้ลูกค้ากดยอมรับได้เลย (อายุ 30 วัน)"));
    }

    /// <summary>สร้าง/ต่ออายุลิงก์ "ลูกค้าเซ็นรับสินค้าออนไลน์" (POD) — เฉพาะ
    /// ใบส่งของที่อนุมัติแล้ว; token อายุ 14 วัน; เรียกซ้ำ = revoke ลิงก์เก่า.</summary>
    [HttpPost("{documentId:guid}/delivery-sign-link")]
    public async Task<ActionResult<ApiResponse<object>>> CreateDeliverySignLink(
        Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d =>
            d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted);
        if (doc == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบเอกสาร"));
        if (doc.DocumentType != Models.Enums.DocumentType.DeliveryNote)
            return BadRequest(new ApiResponse<object>(false, null,
                "ลิงก์เซ็นรับสินค้าใช้ได้เฉพาะใบส่งของ (Delivery Note)"));
        if (doc.Status is Models.Enums.DocumentStatus.Draft or Models.Enums.DocumentStatus.Voided
            or Models.Enums.DocumentStatus.Rejected)
            return BadRequest(new ApiResponse<object>(false, null,
                "ต้องอนุมัติใบส่งของก่อนจึงส่งลิงก์ให้ลูกค้าเซ็นรับได้"));
        var denySign = await DenyDocAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
            doc.DocumentType, DocPerm.Create, "สร้างลิงก์เซ็นรับของ");
        if (denySign != null) return Forbid403<object>(denySign);

        doc.DeliverySignToken = PublicQuotationController.NewToken();
        doc.DeliverySignTokenExpiresAt = DateTime.UtcNow.AddDays(14);
        await _db.SaveChangesAsync();

        var url = $"{Request.Scheme}://{Request.Host}/pages/delivery-sign.html?token={doc.DeliverySignToken}";
        return Ok(new ApiResponse<object>(true, new
        {
            url,
            token = doc.DeliverySignToken,
            expiresAt = doc.DeliverySignTokenExpiresAt,
            signedAt = doc.DeliverySignedAt,
            signedBy = doc.DeliverySignedBy,
        }, "สร้างลิงก์แล้ว — ส่งให้ลูกค้าเซ็นรับสินค้าได้เลย (อายุ 14 วัน)"));
    }

    private static DocumentEmailLogResponse MapEmailLog(Models.Entities.DocumentEmailLog l) => new(
        Id: l.Id,
        DocumentId: l.DocumentId,
        EtaxInvoiceId: l.EtaxInvoiceId,
        ToEmail: l.ToEmail,
        CcEmail: l.CcEmail,
        Subject: l.Subject,
        AttachedPdf: l.AttachedPdf,
        AttachedXml: l.AttachedXml,
        Provider: l.Provider,
        Status: l.Status,
        SentAt: l.SentAt,
        ErrorMessage: l.ErrorMessage,
        IsEtaxByEmail: l.IsEtaxByEmail,
        IncludedRdTimestamp: l.IncludedRdTimestamp,
        CreatedAt: l.CreatedAt);

    // ===== Documents =====

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<DocumentResponse>>>> GetDocuments(
        Guid companyId, [FromQuery] DocumentType? type = null,
        // หลายประเภทพร้อมกัน (ฝั่งรายรับ/รายจ่าย) — กรองที่ server เพื่อ pagination ถูก
        [FromQuery] List<DocumentType>? types = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null,
        [FromQuery] Guid? projectId = null, [FromQuery] Guid? contactId = null,
        [FromQuery] string? status = null, [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null,
        [FromQuery] Guid? relatedDocumentId = null, [FromQuery] Guid? revenueContractId = null,
        [FromQuery] bool staleOnly = false,
        // "Open" | "PartiallyDone" | "Done" | "Cancelled" — derived field;
        // post-filtered after mapping. Lets partner ERPs sync only "still
        // has work" docs without parsing Status+BalanceDue+conversion %
        // separately.
        [FromQuery] string? lifecycle = null,
        // เรียงลำดับ (กดหัวคอลัมน์): number | date | amount | status | duedate
        [FromQuery] string? sortBy = null, [FromQuery] bool sortDesc = false)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);

        // Direction gate — Sales role with only Revenue.View can't list PVs.
        // Explicit type query rejected with 403 so the UI surfaces the
        // missing permission instead of silently returning an empty list.
        var visibility = await DocumentPermissionHelper.VisibleDirectionsAsync(_permissions, companyId, userId);
        if (type.HasValue && !visibility.Allows(type.Value))
            return Forbid403<PagedResponse<DocumentResponse>>(
                $"คุณไม่มีสิทธิ์ดูเอกสารประเภท {type.Value} (ต้องการ Document.Revenue.View หรือ Document.Purchase.View)");

        // จำกัด types[] ตามสิทธิ์ผู้ใช้ (drop ประเภทที่ดูไม่ได้) ก่อนส่งเข้า service
        var effTypes = types;
        if (types != null && types.Count > 0 && !visibility.ShowsEverything)
            effTypes = types.Where(t => visibility.Allows(t)).ToList();

        var result = await _documentService.GetDocumentsForUserAsync(companyId, userId, type, new PagedRequest(page, pageSize, search, sortBy, sortDesc),
            projectId, contactId, status, fromDate, toDate, relatedDocumentId, revenueContractId, staleOnly, effTypes);

        // Visibility redaction — จำเป็นเฉพาะ list ที่ "ไม่ได้ระบุ type/types"
        // (service คืนทุกประเภท). เมื่อมี type/types เราจำกัดด้วยสิทธิ์ server-side
        // (effTypes) ไปแล้ว → กรองซ้ำที่นี่ไม่จำเป็น และการ recompute TotalPages
        // จากแค่ "หน้าเดียว" จะทำให้ paging พัง(เหลือ 1 หน้า ปุ่มเปลี่ยนหน้าหาย)
        // — bug ที่ผู้ใช้ที่ไม่ใช่ ShowsEverything เจอ. คงยอด TotalCount/TotalPages
        // จาก server ไว้ (drop เฉพาะแถวที่ดูไม่ได้).
        if (!visibility.ShowsEverything && !type.HasValue && (types == null || types.Count == 0))
        {
            var filtered = result.Items.Where(d => visibility.Allows(d.DocumentType)).ToList();
            result = new PagedResponse<DocumentResponse>(filtered, result.TotalCount,
                result.Page, result.PageSize, result.TotalPages);
        }

        if (!string.IsNullOrWhiteSpace(lifecycle))
        {
            var filtered = result.Items
                .Where(d => string.Equals(d.LifecycleStatus, lifecycle, StringComparison.OrdinalIgnoreCase))
                .ToList();
            result = new PagedResponse<DocumentResponse>(filtered, filtered.Count,
                result.Page, result.PageSize,
                (int)Math.Ceiling(filtered.Count / (double)result.PageSize));
        }
        return Ok(new ApiResponse<PagedResponse<DocumentResponse>>(true, result));
    }

    /// <summary>เดือน/ปีที่มีเอกสารจริง — สำหรับ dropdown กรองตามงวด (เลือกจาก
    /// ของที่มี). respect ฝั่ง (types[]) + สิทธิ์ผู้ใช้.</summary>
    [HttpGet("periods")]
    public async Task<ActionResult<ApiResponse<List<DocumentPeriod>>>> GetPeriods(
        Guid companyId, [FromQuery] DocumentType? type = null, [FromQuery] List<DocumentType>? types = null)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var visibility = await DocumentPermissionHelper.VisibleDirectionsAsync(_permissions, companyId, userId);
        var effTypes = type.HasValue ? new List<DocumentType> { type.Value } : types;
        if (effTypes != null && effTypes.Count > 0 && !visibility.ShowsEverything)
            effTypes = effTypes.Where(t => visibility.Allows(t)).ToList();
        var periods = await _documentService.GetDocumentPeriodsAsync(companyId, effTypes);
        return Ok(new ApiResponse<List<DocumentPeriod>>(true, periods));
    }

    [HttpGet("{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> GetDocument(Guid companyId, Guid documentId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _documentService.GetDocumentForUserAsync(companyId, documentId, userId);
        return Ok(new ApiResponse<DocumentResponse>(true, result));
    }

    /// <summary>Lookup the OCR scan that produced this document (if any).
    /// Drives the "📦 นำเข้าสต๊อก" button on the document detail modal —
    /// without it the operator can't reuse the stock-import flow that the
    /// scan page exposes for OCR-derived documents.
    /// Returns the scan id + filename when found, success-with-null when
    /// the document wasn't created from a scan.</summary>
    [HttpGet("{documentId:guid}/linked-scan")]
    public async Task<ActionResult<ApiResponse<object>>> GetLinkedScan(Guid companyId, Guid documentId)
    {
        var scan = await _db.OcrScanResults
            .Where(s => s.CompanyId == companyId && s.CreatedDocumentId == documentId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new
            {
                scanId = s.Id,
                fileName = s.OriginalFileName,
                createdAt = s.CreatedAt,
                hasExtractedItems = s.ExtractedItemsJson != null && s.ExtractedItemsJson.Length > 2,
            })
            .FirstOrDefaultAsync();
        return Ok(new ApiResponse<object>(true, scan, scan == null ? "ไม่มีไฟล์ OCR ที่ผูกกับเอกสารนี้" : null));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> CreateDocument(Guid companyId, [FromBody] CreateDocumentRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userIdGuid, request.DocumentType))
            return Forbid403<DocumentResponse>(
                $"ไม่มีสิทธิ์สร้างเอกสาร {request.DocumentType} (ต้องการ Document.Create หรือ Document.{(DocumentPermissionHelper.IsRevenue(request.DocumentType) ? "Revenue" : "Purchase")}.Create)");
        var result = await _documentService.CreateDocumentAsync(companyId, request, userIdGuid.ToString());
        return StatusCode(201, new ApiResponse<DocumentResponse>(true, result, "สร้างเอกสารสำเร็จ"));
    }

    [HttpPut("{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> UpdateDocument(Guid companyId, Guid documentId, [FromBody] UpdateDocumentRequest request)
    {
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        var deny = await DenyDocAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
            docType.Value, DocPerm.Create, "แก้ไข");
        if (deny != null) return Forbid403<DocumentResponse>(deny);
        var result = await _documentService.UpdateDocumentAsync(companyId, documentId, request);
        return Ok(new ApiResponse<DocumentResponse>(true, result));
    }

    /// <summary>เติม/แก้ใบกำกับภาษีซื้อหลังอนุมัติ — สำหรับเอกสารที่ตอน approve
    /// ใบกำกับยังไม่ครบ §86/4 จึงค้างภาษีซื้อไว้ที่ 11640 "ยังไม่ถึงกำหนด".
    /// เมื่อข้อมูลครบ ระบบ gen adjusting JE 11640→11610 อัตโนมัติ (§82/3).</summary>
    [HttpPost("{documentId:guid}/complete-tax-invoice")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> CompleteSupplierTaxInvoice(
        Guid companyId, Guid documentId, [FromBody] CompleteSupplierTaxInvoiceRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        // ต้องมีสิทธิ์แก้เอกสารฝั่งซื้อ (เหมือน update)
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>(
                $"ไม่มีสิทธิ์แก้ไขเอกสาร {docType} (ต้องการ Document.Purchase.Create)");
        var result = await _documentService.CompleteSupplierTaxInvoiceAsync(
            companyId, documentId, request, userIdGuid.ToString());
        return Ok(new ApiResponse<DocumentResponse>(true, result, "อัปเดตใบกำกับภาษีซื้อสำเร็จ"));
    }

    public sealed record SetVatClaimPeriodRequest(string? Period);

    /// <summary>ตั้ง/ย้ายงวดเคลมภาษีซื้อของใบเดียว (yyyy-MM · "" = ตามเดือนเอกสาร)
    /// — ใช้จากหน้านำส่งภาษี (ใบ ภ.พ.36 รับรู้แล้ว) และที่อื่นที่ต้องย้ายงวดโดย
    /// ไม่เปิดฟอร์มเอกสาร. กติกาอยู่ที่ตัวตรวจกลางฝั่ง service ทั้งหมด</summary>
    [HttpPost("{documentId:guid}/vat-claim-period")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> SetVatClaimPeriod(
        Guid companyId, Guid documentId, [FromBody] SetVatClaimPeriodRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>(
                $"ไม่มีสิทธิ์แก้ไขเอกสาร {docType} (ต้องการ Document.Purchase.Create)");
        var result = await _documentService.SetInputVatClaimPeriodAsync(
            companyId, documentId, request.Period, userIdGuid.ToString());
        // sync รายงานร่างของงวดใหม่ทันที — ตั้งเดือนเคลมแล้วต้องเห็นในรายงานเลย
        // โดยไม่ต้อง "สร้างใหม่" (ซึ่งล้างการติ๊กของบรรทัดอื่นทั้งงวด)
        var pullNote = await _taxService.TryPullIntoDraftReportAsync(companyId, documentId);
        return Ok(new ApiResponse<DocumentResponse>(true, result,
            $"ตั้งงวดเคลมภาษีซื้อแล้ว — {pullNote}"));
    }

    /// <summary>ถาม AI ให้แนะนำผังบัญชี GL ของทุกบรรทัด PV — student-first ผ่าน
    /// distillation model + teacher fallback ตาม Distillation Mandate.</summary>
    [HttpPost("ai-suggest-pv-accounting")]
    public async Task<ActionResult<ApiResponse<SuggestPvAccountingResponse>>> SuggestPvAccounting(
        Guid companyId, [FromBody] SuggestPvAccountingRequest request, CancellationToken ct)
    {
        var result = await _documentService.SuggestPaymentVoucherAccountingAsync(companyId, request, ct);
        return Ok(new ApiResponse<SuggestPvAccountingResponse>(true, result));
    }

    /// <summary>รายการเอกสารภาษีซื้อค้าง 11640 รอใบกำกับครบ §86/4 +
    /// 6-month aging (§82/3) สำหรับ dashboard ภาษีซื้อยังไม่ถึงกำหนด.</summary>
    [HttpGet("undue-input-vat")]
    public async Task<ActionResult<ApiResponse<List<UndueInputVatSummary>>>> GetUndueInputVat(Guid companyId)
    {
        var result = await _documentService.GetUndueInputVatAsync(companyId);
        return Ok(new ApiResponse<List<UndueInputVatSummary>>(true, result));
    }

    /// <summary>§82/3: reclassify ภาษีซื้อ 11640 ที่พ้น 6 เดือน (ใบกำกับไม่ครบ) →
    /// ค่าใช้จ่าย. ล้าง 11640 ที่ค้างเป็น asset ลอย. คืนจำนวนเอกสารที่จัดการ.</summary>
    [HttpPost("undue-input-vat/reclassify-expired")]
    public async Task<ActionResult<ApiResponse<object>>> ReclassifyExpiredUndueInputVat(Guid companyId)
    {
        // งานนี้โพสต์ JE ให้ทุกใบที่พ้น 6 เดือนทั้งบริษัท — ระดับเดียวกับการอนุมัติ
        var deny = await DenyKeyAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
            Models.Constants.PermissionKeys.DocumentApprove, "ล้างภาษีซื้อที่พ้น 6 เดือน");
        if (deny != null) return Forbid403<object>(deny);
        var n = await _documentService.ReclassifyExpiredUndueInputVatAsync(companyId, User.Identity?.Name ?? "");
        return Ok(new ApiResponse<object>(true, new { reclassified = n },
            n > 0 ? $"reclassify ภาษีซื้อพ้น 6 เดือน {n} รายการ → ค่าใช้จ่าย" : "ไม่มีภาษีซื้อที่พ้น 6 เดือน"));
    }

    /// <summary>รายการเงินมัดจำคงค้าง/รับรู้แล้ว สำหรับหน้าจัดการมัดจำ
    /// (ขึ้นงบดุลเป็นหนี้สิน ไม่ใช่เจ้าหนี้การค้า).</summary>
    [HttpGet("deposits")]
    public async Task<ActionResult<ApiResponse<List<DepositSummary>>>> GetDeposits(
        Guid companyId, [FromQuery] string? status = null)
    {
        var result = await _documentService.GetDepositsAsync(companyId, status);
        return Ok(new ApiResponse<List<DepositSummary>>(true, result));
    }

    /// <summary>วินิจฉัยหน้าเงินมัดจำ — ใช้ตอน dashboard โชว์ 0 เพื่อบอกว่า
    /// ระบบเห็นบัญชีมัดจำ/JE เครดิต/เอกสารกี่รายการ + สาเหตุ.</summary>
    [HttpGet("deposits/diagnostics")]
    public async Task<ActionResult<ApiResponse<DepositDiagnostics>>> GetDepositDiagnostics(Guid companyId)
    {
        var result = await _documentService.GetDepositDiagnosticsAsync(companyId);
        return Ok(new ApiResponse<DepositDiagnostics>(true, result));
    }

    /// <summary>Deposit Center — payload เดียวจบ (รายการ + KPI + GL tie-out +
    /// timestamp) สำหรับหน้าเงินมัดจำ redesign. URL ใหม่ ไม่เคยถูก cache ที่ชั้นไหน.</summary>
    [HttpGet("deposit-center")]
    public async Task<ActionResult<ApiResponse<DepositCenterResponse>>> GetDepositCenter(Guid companyId)
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        var result = await _documentService.GetDepositCenterAsync(companyId);
        return Ok(new ApiResponse<DepositCenterResponse>(true, result));
    }

    /// <summary>เอกสารทั้งหมดที่ผูก booking เดียวกัน (มัดจำ → ใบสุดท้าย → ใบเสร็จ).</summary>
    [HttpGet("by-booking/{bookingNumber}")]
    public async Task<ActionResult<ApiResponse<List<DocumentResponse>>>> GetByBooking(
        Guid companyId, string bookingNumber)
    {
        var result = await _documentService.GetDocumentsByBookingAsync(companyId, bookingNumber);
        return Ok(new ApiResponse<List<DocumentResponse>>(true, result));
    }

    /// <summary>สรุปมัดจำคงค้างของลูกค้ารายหนึ่ง (หน้า contact + dropdown ใบแจ้งหนี้).</summary>
    [HttpGet("contacts/{contactId:guid}/deposit-summary")]
    public async Task<ActionResult<ApiResponse<ContactDepositSummary>>> GetContactDepositSummary(
        Guid companyId, Guid contactId)
    {
        var result = await _documentService.GetContactDepositSummaryAsync(companyId, contactId);
        return Ok(new ApiResponse<ContactDepositSummary>(true, result));
    }

    /// <summary>คืนเงินมัดจำ (ยกเลิกการจอง) — reversal JE + ใบลดหนี้ output VAT.</summary>
    [HttpPost("{documentId:guid}/refund-deposit")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> RefundDeposit(
        Guid companyId, Guid documentId, [FromBody] RefundDepositRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>("ไม่มีสิทธิ์คืนเงินมัดจำ");
        var result = await _documentService.RefundDepositAsync(companyId, documentId, request, userIdGuid.ToString());
        return Ok(new ApiResponse<DocumentResponse>(true, result, "คืนเงินมัดจำสำเร็จ"));
    }

    /// <summary>นำมัดจำไปหักกับใบแจ้งหนี้/ใบกำกับสุดท้าย (offset prepayment).</summary>
    [HttpPost("{invoiceId:guid}/apply-deposit")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> ApplyDeposit(
        Guid companyId, Guid invoiceId, [FromBody] ApplyDepositRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, invoiceId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>("ไม่มีสิทธิ์นำมัดจำมาหัก");
        var result = await _documentService.ApplyDepositToInvoiceAsync(companyId, invoiceId, request, userIdGuid.ToString());
        return Ok(new ApiResponse<DocumentResponse>(true, result, "นำมัดจำมาหักสำเร็จ"));
    }

    /// <summary>ค้น "JV มัดจำที่ไม่มีเอกสาร" (case B: post ตรงผ่าน /integration/journals
    /// หรือลงมือ) ที่ยังเปิดให้ตัดชำระ — ใส่ query = เลขอ้างอิง/booking/เลข JV จะหา
    /// ที่เกี่ยวข้อง; ไม่ใส่ = list ที่เปิดอยู่. read-only.</summary>
    [HttpGet("journal-deposits")]
    public async Task<ActionResult<ApiResponse<List<JournalDepositCandidate>>>> SearchJournalDeposits(
        Guid companyId, [FromQuery] string? q = null)
    {
        var result = await _documentService.SearchJournalDepositsAsync(companyId, q);
        return Ok(new ApiResponse<List<JournalDepositCandidate>>(true, result));
    }

    /// <summary>นำ JV มัดจำ (ไม่มีเอกสาร) มาตัดชำระใบแจ้งหนี้/ใบกำกับ — หักเต็ม JV.</summary>
    [HttpPost("{invoiceId:guid}/apply-journal-deposit")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> ApplyJournalDeposit(
        Guid companyId, Guid invoiceId, [FromBody] ApplyJournalDepositRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, invoiceId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>("ไม่มีสิทธิ์นำมัดจำมาหัก");
        if (string.IsNullOrWhiteSpace(request.JournalEntryNumber))
            return BadRequest(new ApiResponse<DocumentResponse>(false, null, "ต้องระบุเลขสมุดรายวัน (JournalEntryNumber)"));
        var result = await _documentService.ApplyJournalDepositToInvoiceAsync(
            companyId, invoiceId, request.JournalEntryNumber.Trim(), userIdGuid.ToString(), request.ApplyDate);
        return Ok(new ApiResponse<DocumentResponse>(true, result, "นำ JV มัดจำมาหักสำเร็จ"));
    }

    /// <summary>รับรู้รายได้จากเงินมัดจำเมื่อส่งมอบจริง (ตัด ขายรอรับรู้ →
    /// รายได้). รองรับรับรู้บางส่วน.</summary>
    [HttpPost("{documentId:guid}/realize-deposit")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> RealizeDeposit(
        Guid companyId, Guid documentId, [FromBody] RealizeDepositRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>("ไม่มีสิทธิ์รับรู้รายได้จากมัดจำ");
        var result = await _documentService.RealizeDepositAsync(
            companyId, documentId, request, userIdGuid.ToString());
        return Ok(new ApiResponse<DocumentResponse>(true, result, "รับรู้รายได้จากมัดจำสำเร็จ"));
    }

    /// <summary>สายการแปลงเอกสารทั้งเส้น (ต้นน้ำ→ปลายน้ำ) — ให้ UI วาด stepper
    /// QT → INV → REC โดยผู้ใช้ไม่ต้องไล่เปิดทีละใบเพื่อจับคู่เลขเอง.
    /// เดินขึ้นตาม RelatedDocumentId จนถึงต้นทาง แล้วเดินลงหา "ทายาท" ทุกใบ.</summary>
    [HttpGet("{documentId:guid}/chain")]
    public async Task<ActionResult<ApiResponse<List<DocumentChainNode>>>> GetChain(
        Guid companyId, Guid documentId)
        => Ok(new ApiResponse<List<DocumentChainNode>>(true,
            await _documentService.GetDocumentChainAsync(companyId, documentId)));

    /// <summary>ประวัติ revision ของเอกสาร — ทุกครั้งที่แก้เอกสาร operational
    /// ที่อนุมัติ/ส่งแล้ว ระบบ snapshot สภาพก่อนแก้ + เพิ่ม Rev อัตโนมัติ.</summary>
    [HttpGet("{documentId:guid}/revisions")]
    public async Task<ActionResult<ApiResponse<List<DocumentRevisionListItem>>>> GetRevisions(
        Guid companyId, Guid documentId)
        => Ok(new ApiResponse<List<DocumentRevisionListItem>>(true,
            await _documentService.GetDocumentRevisionsAsync(companyId, documentId)));

    /// <summary>snapshot เต็มของ revision หนึ่ง (JSON) — ดูว่า Rev นั้นมีอะไร.</summary>
    [HttpGet("{documentId:guid}/revisions/{revisionNumber:int}")]
    public async Task<ActionResult> GetRevisionSnapshot(
        Guid companyId, Guid documentId, int revisionNumber)
    {
        var json = await _documentService.GetDocumentRevisionSnapshotAsync(companyId, documentId, revisionNumber);
        if (json == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบ revision นี้"));
        return Content($"{{\"success\":true,\"data\":{json}}}", "application/json");
    }

    public record RecognizeDepositVatRequest(DateTime? RecognizeDate);

    /// <summary>รับรู้ "ภาษีขายรอเรียกเก็บ" ของใบมัดจำ (21913 → 21911) โดยไม่แตะ
    /// รายได้ — ใช้เมื่อจุดรับผิด VAT เกิดก่อนส่งมอบ (§78: ออกใบกำกับตามคำขอ
    /// ลูกค้า / ทบทวนแล้วพบว่าเป็นการรับชำระราคาจริง). หลังเรียก ใบเข้า ภ.พ.30
    /// งวดที่ระบุ และหัวเอกสาร upgrade เป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน".</summary>
    [HttpPost("{documentId:guid}/recognize-deposit-vat")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> RecognizeDepositVat(
        Guid companyId, Guid documentId, [FromBody] RecognizeDepositVatRequest? request = null)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>("ไม่มีสิทธิ์รับรู้ภาษีขายของมัดจำ (กระทบ ภ.พ.30)");
        try
        {
            var result = await _documentService.RecognizeDepositOutputVatAsync(
                companyId, documentId, request?.RecognizeDate, userIdGuid.ToString());
            return Ok(new ApiResponse<DocumentResponse>(true, result,
                "รับรู้ภาษีขายเข้า ภ.พ.30 แล้ว — พิมพ์เอกสารใหม่จะได้หัว \"ใบกำกับภาษี/ใบเสร็จรับเงิน\""));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<DocumentResponse>(false, null, ex.Message));
        }
    }

    [HttpPost("{documentId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<object>>> ApproveDocument(Guid companyId, Guid documentId, [FromBody] ApproveDocumentRequest? request = null)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<object>(
                $"ไม่มีสิทธิ์อนุมัติเอกสาร {docType} (ต้องการ Document.Approve หรือ Document.{(DocumentPermissionHelper.IsRevenue(docType.Value) ? "Revenue" : "Purchase")}.Approve)");
        var userId = userIdGuid.ToString();
        try
        {
            var result = await _documentService.ApproveDocumentAsync(companyId, documentId, userId, request?.AcknowledgeWarnings ?? false);
            return Ok(new ApiResponse<object>(true, result, "อนุมัติเอกสารสำเร็จ"));
        }
        catch (DocumentApprovalWarningsException ex)
        {
            // 422 Unprocessable Entity — semantically "request is well-
            // formed but content violates a pre-condition". Frontend
            // recognises the shape, prompts the operator with the warning
            // list, and re-submits with AcknowledgeWarnings=true. When
            // AI augmentation is online, AiHints[i] aligns with Warnings[i]
            // so the UI can render the suggested fix next to each warning.
            var aiHints = ex.AiHints?.Select(h => new ApprovalWarningAiHintDto(
                h.Primary, h.Confidence, h.Reasoning, h.SuggestedActions,
                h.Risks, h.ComplianceFlags, h.FeedbackId, h.UsedAi)).ToList();
            return StatusCode(422, new ApiResponse<object>(false,
                new ApprovalWarningsResponse(ex.Warnings, aiHints),
                ex.Message));
        }
    }

    /// <summary>เปลี่ยนผังบัญชี (line.AccountId) ของเอกสารที่ approved แล้ว
    /// — Expense/PI/PV เท่านั้น. สร้าง reclassify-JE คู่ใหม่ (Dr ผังใหม่ /
    /// Cr ผังเก่า) ในงวดเดิม. trial balance ก่อน-หลังตรง. ใบกำกับ/ใบเสร็จ
    /// ห้ามใช้ตาม §86/4 (ต้อง void+ออกใหม่). ดู gate ใน
    /// DocumentService.ReclassifyLineAccountAsync</summary>
    [HttpPost("{documentId:guid}/reclassify-line")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> ReclassifyLine(
        Guid companyId, Guid documentId, [FromBody] ReclassifyLineRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        // ใช้ permission เดียวกับ Approve — reclassify มี GL impact ต้องระดับเดียวกัน
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>(
                $"ไม่มีสิทธิ์เปลี่ยนผังบัญชีเอกสาร {docType} (ต้องการ Document.Approve)");
        var result = await _documentService.ReclassifyLineAccountAsync(
            companyId, documentId, request.LineId, request.NewAccountId,
            request.Reason, userIdGuid.ToString());
        return Ok(new ApiResponse<DocumentResponse>(true, result,
            "เปลี่ยนผังบัญชีสำเร็จ (สร้าง JE คู่ใหม่ลงงวดเดิม)"));
    }

    /// <summary>เปลี่ยน "แหล่งเงิน" (บัญชี Cr เงินสด/ธนาคาร) ของเอกสารจ่าย/รับ
    /// สดหลัง approve — แก้เคส OCR เลือกธนาคารผิดโดยไม่ต้อง void. post
    /// correcting-JE (Dr ผังเก่า / Cr ผังใหม่). ดู gate ใน
    /// DocumentService.ReclassifyPaymentSourceAsync</summary>
    [HttpPost("{documentId:guid}/reclassify-payment-source")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> ReclassifyPaymentSource(
        Guid companyId, Guid documentId, [FromBody] ReclassifyPaymentSourceRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        // ใช้ permission เดียวกับ Approve — มี GL impact ต้องระดับเดียวกัน
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>(
                $"ไม่มีสิทธิ์เปลี่ยนแหล่งเงินเอกสาร {docType} (ต้องการ Document.Approve)");
        var result = await _documentService.ReclassifyPaymentSourceAsync(
            companyId, documentId, request.NewBankAccountId, request.NewPaymentAccountId,
            request.Reason, userIdGuid.ToString());
        return Ok(new ApiResponse<DocumentResponse>(true, result,
            "เปลี่ยนแหล่งเงินสำเร็จ (สร้าง JE คู่ใหม่ลงงวดเดิม)"));
    }

    /// <summary>ย้ายฝั่งใบลดหนี้/ใบเพิ่มหนี้ (ซื้อ ↔ ขาย) หลังอนุมัติ — ใช้เมื่อ
    /// ใบถูกจัดฝั่งผิดตั้งแต่อนุมัติ ทำให้ยอดไปโผล่ผิดฝั่งในรายงาน ภ.พ.30.
    /// ระบบกลับ JE เดิมแล้วลงใหม่ให้ถูกฝั่ง (ไม่ใช่ย้ายแค่ตัวเลขในรายงาน —
    /// ไม่งั้น GL กับแบบยื่นภาษีจะขัดกันเอง). ดู gate ใน
    /// DocumentService.ReclassifyCnDnSideAsync</summary>
    [HttpPost("{documentId:guid}/reclassify-cn-side")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> ReclassifyCnDnSide(
        Guid companyId, Guid documentId, [FromBody] ReclassifyCnDnSideRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        // GL + ภ.พ.30 impact → ใช้ permission ระดับเดียวกับ Approve
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>(
                $"ไม่มีสิทธิ์ย้ายฝั่งเอกสาร {docType} (ต้องการ Document.Approve)");
        var result = await _documentService.ReclassifyCnDnSideAsync(
            companyId, documentId, request.ToPurchaseSide, request.Reason, userIdGuid.ToString());
        return Ok(new ApiResponse<DocumentResponse>(true, result,
            $"ย้ายไปฝั่ง{(request.ToPurchaseSide ? "ซื้อ" : "ขาย")}แล้ว — กลับ JE เดิมและลงใหม่ให้ถูกฝั่ง "
            + "(สร้างรายงานภาษีงวดนี้ใหม่เพื่อให้ยอดตรง)"));
    }

    /// <summary>แก้เอกสารที่อนุมัติแล้วให้เป็น/เลิกเป็น "บริการต่างประเทศ (ภ.พ.36 §83/6)"
    /// — ใบที่ลืมติ๊กจะไม่มี <c>Cr 21912</c> (ไม่มีหนี้ ภ.พ.36 ⇒ ไม่โผล่หน้านำส่ง
    /// ⇒ ไม่เคยนำส่ง) และเครดิตผู้รับเงินด้วยยอดรวม VAT (เจ้าหนี้/ธนาคารเกินจริง)
    /// · ระบบกลับ JE เดิมแล้วลงใหม่ผ่านตัวลงบัญชีตัวเดิม ดู gate ใน
    /// DocumentService.ReclassifyForeignServiceAsync</summary>
    [HttpPost("{documentId:guid}/reclassify-foreign-service")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> ReclassifyForeignService(
        Guid companyId, Guid documentId, [FromBody] ReclassifyForeignServiceRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        // GL + ภ.พ.36 impact → permission ระดับเดียวกับ Approve
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>(
                $"ไม่มีสิทธิ์แก้การลงบัญชีของเอกสาร {docType} (ต้องการ Document.Approve)");
        var result = await _documentService.ReclassifyForeignServiceAsync(
            companyId, documentId, request.ToForeignService, request.Reason, userIdGuid.ToString());
        return Ok(new ApiResponse<DocumentResponse>(true, result,
            request.ToForeignService
                ? "ตั้งเป็นบริการต่างประเทศแล้ว — ลงหนี้ ภ.พ.36 (Cr 21912) และแก้ยอดเครดิตผู้รับเงิน"
                  + "ให้เป็นฐานเท่านั้น · ใบนี้จะโผล่ที่หน้านำส่งภาษีของงวดแล้ว"
                : "ยกเลิกสถานะบริการต่างประเทศแล้ว — กลับ JE เดิมและลงใหม่ตามการซื้อในประเทศ"));
    }

    // ===== Adjusting Journal Lines (Option 1: 3 Dr/1 Cr, 1 Dr/3 Cr, ฯลฯ) =====
    public sealed record AdjustingLineDto(Guid AccountId, decimal DebitAmount,
        decimal CreditAmount, string? Description, Guid? ProjectId, string? Reason);
    public sealed record SaveAdjustingLinesRequest(List<AdjustingLineDto> Lines);

    /// <summary>List adjusting JE lines ของเอกสาร</summary>
    [HttpGet("{documentId:guid}/adjusting-lines")]
    public async Task<ActionResult<ApiResponse<List<Models.Entities.DocumentAdjustingJournalLine>>>> ListAdjustingLines(
        Guid companyId, Guid documentId)
    {
        var rows = await _documentService.ListAdjustingJournalLinesAsync(companyId, documentId);
        return Ok(new ApiResponse<List<Models.Entities.DocumentAdjustingJournalLine>>(true, rows));
    }

    /// <summary>Replace adjusting lines ทั้งชุด (full sync). เฉพาะ Draft.</summary>
    [HttpPut("{documentId:guid}/adjusting-lines")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> SaveAdjustingLines(
        Guid companyId, Guid documentId, [FromBody] SaveAdjustingLinesRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        // ⚠️ เดิมคอมเมนต์บอกว่า "ใช้ permission เดียวกับ Edit" แต่ **ไม่เคยเช็คจริง**
        // — ผู้ใช้ระดับดูอย่างเดียวใส่บรรทัด Dr/Cr อะไรก็ได้เข้าเอกสาร Draft ได้
        // แล้วบรรทัดนั้นเข้า GL ตอนผู้อื่นอนุมัติ (service ไม่ตรวจ balance เอง
        // โดยตั้งใจ — "การ block จริงอยู่ที่ AutoPost")
        // สิทธิ์แก้ Draft = สิทธิ์สร้างเอกสารชนิดนั้น (ไม่มี CanEditAsync แยก)
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>($"ไม่มีสิทธิ์แก้ไขเอกสาร {docType}");
        var lines = (request.Lines ?? new()).Select(l =>
            (l.AccountId, l.DebitAmount, l.CreditAmount, l.Description, l.ProjectId, l.Reason));
        var result = await _documentService.SaveAdjustingJournalLinesAsync(
            companyId, documentId, lines, userIdGuid.ToString());
        return Ok(new ApiResponse<DocumentResponse>(true, result,
            $"บันทึก adjusting JE lines {request.Lines?.Count ?? 0} รายการแล้ว"));
    }

    [HttpPost("{documentId:guid}/void")]
    public async Task<ActionResult<ApiResponse<string>>> VoidDocument(Guid companyId, Guid documentId,
        [FromQuery] DateTime? reversalDate = null)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanVoidAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<string>(
                $"ไม่มีสิทธิ์ยกเลิกเอกสาร {docType} (ต้องการ Document.Void หรือ Document.{(DocumentPermissionHelper.IsRevenue(docType.Value) ? "Revenue" : "Purchase")}.Void)");
        await _documentService.VoidDocumentAsync(companyId, documentId, reversalDate);
        return Ok(new ApiResponse<string>(true, null, "ยกเลิกเอกสารสำเร็จ"));
    }

    /// <summary>ย้ายวันที่ JE กลับรายการของเอกสารที่ยกเลิกไปแล้ว — ใช้แก้ใบที่
    /// ถูกยกเลิกตอนระบบยังใช้ "วันที่กด" เป็นวันที่กลับรายการ (ข้ามเดือน).
    /// สิทธิ์เท่ากับการยกเลิก และงวดปลายทางต้องเปิดอยู่</summary>
    [HttpPost("{documentId:guid}/redate-void-reversal")]
    public async Task<ActionResult<ApiResponse<object>>> RedateVoidReversal(
        Guid companyId, Guid documentId, [FromQuery] DateTime? newDate = null,
        [FromBody] RedateVoidReversalRequest? request = null)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanVoidAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<object>("ไม่มีสิทธิ์แก้วันที่รายการกลับบัญชีของเอกสารนี้");

        var moved = await _documentService.RedateVoidReversalAsync(
            companyId, documentId, newDate ?? default, userIdGuid.ToString(),
            request?.Entries);
        return Ok(new ApiResponse<object>(true, new { moved },
            moved > 0
                ? $"ย้ายวันที่รายการกลับบัญชี {moved} ใบสำคัญเรียบร้อย"
                : "ไม่มีรายการกลับบัญชีที่ต้องย้าย (วันที่ตรงอยู่แล้ว)"));
    }

    /// <summary>ตรวจก่อนสร้างจากสแกน: ใบนี้เคยบันทึกไปแล้วหรือยัง — เลขใบกำกับ
    /// ผู้ขายตรงกัน = แน่นอน, คู่ค้า+ยอด+ช่วงวัน = น่าสงสัย. อ่านอย่างเดียว
    /// ไม่บล็อกอะไร (ผู้ขายขายของชุดเดิมซ้ำได้จริง — false positive ที่บล็อก
    /// แรงกว่าปัญหาที่กัน)</summary>
    [HttpGet("duplicate-check")]
    public async Task<ActionResult<ApiResponse<DuplicateCheckResult>>> DuplicateCheck(
        Guid companyId,
        [FromQuery] Guid? contactId = null,
        [FromQuery] string? supplierInvoiceNumber = null,
        [FromQuery] DocumentType? documentType = null,
        [FromQuery] decimal amount = 0,
        [FromQuery] DateTime? documentDate = null,
        [FromQuery] Guid? excludeDocumentId = null)
    {
        var result = await _documentService.CheckDuplicateAsync(
            companyId, contactId, supplierInvoiceNumber, documentType, amount,
            documentDate ?? DateTime.UtcNow.Date, excludeDocumentId);
        return Ok(new ApiResponse<DuplicateCheckResult>(true, result, null));
    }

    /// <summary>รายการบัญชี (JE) ของเอกสาร พร้อมบรรทัดจริงจาก GL — แผง
    /// "ตรวจสอบ/แก้ไขรายการบัญชี" บนหน้าเอกสาร. อ่านอย่างเดียว</summary>
    [HttpGet("{documentId:guid}/journal-entries")]
    public async Task<ActionResult<ApiResponse<List<DocumentJournalEntryDto>>>> GetDocumentJournalEntries(
        Guid companyId, Guid documentId)
    {
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<List<DocumentJournalEntryDto>>(false, null, "ไม่พบเอกสาร"));
        var list = await _documentService.GetDocumentJournalEntriesAsync(companyId, documentId);
        return Ok(new ApiResponse<List<DocumentJournalEntryDto>>(true, list, null));
    }

    /// <summary>ปรับปรุงผังบัญชีของ JE ที่ลงไปแล้ว — ส่ง "สถานะปลายทาง" ของ
    /// ใบสำคัญมา ระบบลงใบปรับปรุงใหม่ตามผลต่าง. ยอดรวมต้องเท่าเดิม + บัญชีคุม
    /// ห้ามขยับ. ใช้สิทธิ์เดียวกับ Approve (มีผลต่อ GL เท่ากัน)</summary>
    [HttpPost("{documentId:guid}/journal-entries/{journalEntryId:guid}/adjust")]
    public async Task<ActionResult<ApiResponse<List<DocumentJournalEntryDto>>>> AdjustDocumentJournalEntry(
        Guid companyId, Guid documentId, Guid journalEntryId,
        [FromBody] AdjustDocumentJournalRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<List<DocumentJournalEntryDto>>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<List<DocumentJournalEntryDto>>(
                $"ไม่มีสิทธิ์ปรับปรุงรายการบัญชีของเอกสาร {docType}");

        var list = await _documentService.AdjustDocumentJournalEntryAsync(
            companyId, documentId, journalEntryId, request, User.Identity?.Name ?? userIdGuid.ToString());
        return Ok(new ApiResponse<List<DocumentJournalEntryDto>>(true, list,
            "ลงใบสำคัญปรับปรุงเรียบร้อย — ผังบัญชีใน GL ถูกต้องแล้ว"));
    }

    /// <summary>ดูก่อนย้าย — เอกสาร 1 ใบมีตัวกลับได้หลายใบ (ใบซื้อ/ขาย +
    /// รับ-จ่ายชำระ + มัดจำ) endpoint นี้บอกว่าใบไหนบ้างจะถูกย้ายจากวันไหนไป
    /// วันไหน และใบไหนย้ายไม่ได้เพราะอะไร. อ่านอย่างเดียว ไม่แก้ข้อมูล</summary>
    [HttpGet("{documentId:guid}/redate-void-reversal/preview")]
    public async Task<ActionResult<ApiResponse<VoidReversalRedatePreview>>> PreviewRedateVoidReversal(
        Guid companyId, Guid documentId, [FromQuery] DateTime? newDate = null)
    {
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<VoidReversalRedatePreview>(false, null, "ไม่พบเอกสาร"));
        var preview = await _documentService.PreviewVoidReversalRedateAsync(companyId, documentId, newDate);
        return Ok(new ApiResponse<VoidReversalRedatePreview>(true, preview, null));
    }

    /// <summary>กู้คืนเอกสารที่ "ยกเลิกผิด" → คืนเป็นฉบับร่าง (Draft) คงเลขเดิม
    /// แล้วผู้ใช้กดอนุมัติใหม่. gate เข้ม: e-Tax Accepted / เดือนภาษียื่นแล้ว →
    /// บล็อก. สิทธิ์เท่ากับการยกเลิก (Document.Void — เป็น operation คู่กัน).</summary>
    [HttpPost("{documentId:guid}/restore")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> RestoreDocument(Guid companyId, Guid documentId)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanVoidAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>(
                $"ไม่มีสิทธิ์กู้คืนเอกสาร {docType} (ต้องการ Document.Void หรือ Document.{(DocumentPermissionHelper.IsRevenue(docType.Value) ? "Revenue" : "Purchase")}.Void)");
        var actor = User.Identity?.Name ?? userIdGuid.ToString();
        var restored = await _documentService.RestoreVoidedDocumentAsync(companyId, documentId, actor);
        return Ok(new ApiResponse<DocumentResponse>(true, restored,
            "กู้คืนเป็นฉบับร่างแล้ว (เลขเดิมคงไว้) — กรุณากดอนุมัติเพื่อลงบัญชีใหม่"));
    }

    /// <summary>
    /// ลบเอกสารถาวร — เฉพาะ Draft ที่ยังไม่กระทบบัญชีและไม่มีการชำระเงิน
    /// เอกสารที่อนุมัติแล้วต้องใช้ POST /void แทน (รักษา audit trail)
    /// </summary>
    [HttpDelete("{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> DeleteDocument(Guid companyId, Guid documentId)
    {
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบเอกสาร"));
        // ลบได้เฉพาะ Draft ที่ยังไม่มี JE/การชำระ (บังคับใน service) ⇒ ใช้สิทธิ์
        // ระดับ Create — ผู้ที่สร้างร่างได้ ต้องลบร่างของตัวเองได้
        var deny = await DenyDocAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
            docType.Value, DocPerm.Create, "ลบ");
        if (deny != null) return Forbid403<string>(deny);
        await _documentService.DeleteDocumentAsync(companyId, documentId);
        return Ok(new ApiResponse<string>(true, null, "ลบเอกสารสำเร็จ"));
    }

    /// <summary>
    /// ลบเอกสารและข้อมูลเกี่ยวข้องทั้งหมด (journal, payment, WHT, eTax)
    /// ลบถาวร ไม่สามารถกู้คืนได้ — เหมือนไม่เคยสร้างมาเลย
    /// เฉพาะ Owner / SystemAdmin เท่านั้น + บันทึก Audit Log
    /// </summary>
    /// <summary>ลบเอกสารถาวร — เฉพาะ Owner/SystemAdmin. เอกสารในช่วงเก็บรักษา
    /// §87/3 ปกติ block (ใช้ Void แทน) แต่ override ได้ด้วย ?force=true&reason=...
    /// (audit log ว่าใคร/ทำไม — ความเสี่ยงทางกฎหมายเป็นของผู้ override).</summary>
    [HttpDelete("{documentId:guid}/purge")]
    public async Task<ActionResult<ApiResponse<string>>> PurgeDocument(Guid companyId, Guid documentId,
        [FromQuery] bool force = false, [FromQuery] string? reason = null)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var isSystemAdmin = User.IsInRole("SystemAdmin");
        if (!isSystemAdmin)
        {
            var role = await _db.CompanyUsers
                .Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
                .Select(cu => cu.Role)
                .FirstOrDefaultAsync();
            if (role != UserRole.Owner)
                return StatusCode(403, new ApiResponse<string>(false, null, "เฉพาะเจ้าของบริษัท (Owner) เท่านั้นที่สามารถลบเอกสารถาวรได้"));
        }

        await _documentService.PurgeDocumentAsync(companyId, documentId, userId, force, reason);
        return Ok(new ApiResponse<string>(true, null,
            force ? "ลบเอกสารถาวรสำเร็จ (override การเก็บรักษาตามกฎหมาย — บันทึก audit แล้ว)"
                  : "ลบเอกสารและข้อมูลเกี่ยวข้องทั้งหมดสำเร็จ"));
    }

    /// <summary>ใบค้างชำระของลูกค้าที่นำมารวมเป็นใบวางบิลได้ — ใบแจ้งหนี้/
    /// ใบกำกับ/ใบเพิ่มหนี้ที่อนุมัติแล้วและยังมียอดค้าง + บอกว่าใบไหนถูกวางบิล
    /// ไปแล้ว (เลขใบวางบิล) เพื่อกันวางบิลซ้ำ.</summary>
    [HttpGet("billing-note/outstanding")]
    public async Task<ActionResult<ApiResponse<List<BillingNoteSourceItem>>>> GetBillingOutstanding(
        Guid companyId, [FromQuery] Guid contactId)
    {
        var items = await _documentService.GetOutstandingInvoicesForBillingAsync(companyId, contactId);
        return Ok(new ApiResponse<List<BillingNoteSourceItem>>(true, items));
    }

    /// <summary>สร้างใบวางบิล (Draft) จากใบค้างชำระหลายใบของลูกค้ารายเดียว —
    /// 1 บรรทัด = 1 ใบ ยอด = คงค้าง · ไม่ลง JE (ตัวหนี้อยู่ที่ใบต้นทาง).</summary>
    [HttpPost("billing-note/from-invoices")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> CreateBillingNoteFromInvoices(
        Guid companyId, [FromBody] CreateBillingNoteFromInvoicesRequest request)
    {
        try
        {
            var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
            var deny = await DenyDocAsync(companyId, userIdGuid,
                DocumentType.BillingNote, DocPerm.Create, "สร้าง");
            if (deny != null) return Forbid403<DocumentResponse>(deny);
            var userId = userIdGuid.ToString();
            var result = await _documentService.CreateBillingNoteFromInvoicesAsync(companyId, request, userId);
            return Ok(new ApiResponse<DocumentResponse>(true, result,
                $"สร้างใบวางบิลรวม {request.InvoiceIds?.Count ?? 0} ใบแล้ว (ร่าง — ตรวจแล้วกดอนุมัติเพื่อออกเลขจริง)"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<DocumentResponse>(false, null, ex.Message));
        }
    }

    [HttpPost("{documentId:guid}/convert/{targetType}")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> ConvertDocument(Guid companyId, Guid documentId, DocumentType targetType)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        // สิทธิ์ของ **ปลายทาง** — การแปลงคือการสร้างเอกสารชนิดนั้นขึ้นมาใหม่
        // (ใบเสนอราคา → ใบกำกับภาษี = สร้างใบกำกับ ไม่ใช่แค่แก้ใบเสนอราคา)
        var deny = await DenyDocAsync(companyId, userIdGuid, targetType, DocPerm.Create, "แปลงเป็น");
        if (deny != null) return Forbid403<DocumentResponse>(deny);
        var userId = userIdGuid.ToString();
        var result = await _documentService.ConvertDocumentAsync(companyId, documentId, targetType, userId);
        return Ok(new ApiResponse<DocumentResponse>(true, result, "แปลงเอกสารสำเร็จ"));
    }

    public sealed record IssueFullTaxInvoiceRequest(string? Reason);

    /// <summary>
    /// ออก <b>ใบกำกับภาษีเต็มรูป "แทน"</b> ใบเสร็จ/ใบกำกับภาษีอย่างย่อ
    /// (§86/6 → §86/4) — เคสจริง: ลูกค้ารับใบย่อไปแล้ว กลับมาขอเต็มรูปเพื่อ
    /// เคลมภาษีซื้อ (ใบย่อเคลมไม่ได้ §82/5(2))
    ///
    /// <para>ไม่ใช่ endpoint แปลงเอกสาร: ใบเดิมนับภาษีขายเข้า ภ.พ.30 ไปแล้ว
    /// ⇒ ใบใหม่เป็น "ใบแทน" (วันที่เดิม · ไม่มี JE ใหม่ · รายงานสลับมานับใบแทน)</para>
    ///
    /// <para>ใช้สิทธิ์เดียวกับ Approve — มันออกเลขจริงตาม §86/4 และเปลี่ยนว่า
    /// เอกสารใบไหนเป็นเจ้าของแถวในรายงานภาษีขาย (ผลระดับเดียวกับการอนุมัติ)</para>
    /// </summary>
    [HttpPost("{documentId:guid}/issue-full-tax-invoice")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> IssueFullTaxInvoice(
        Guid companyId, Guid documentId, [FromBody] IssueFullTaxInvoiceRequest? request = null)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<DocumentResponse>(
                "ไม่มีสิทธิ์ออกใบกำกับภาษีแทน (ต้องการสิทธิ์ระดับเดียวกับการอนุมัติเอกสาร)");

        var result = await _documentService.IssueFullTaxInvoiceForReceiptAsync(
            companyId, documentId, request?.Reason, userIdGuid.ToString());
        return Ok(new ApiResponse<DocumentResponse>(true, result,
            $"ออกใบกำกับภาษีเต็มรูป {result.DocumentNumber} แทนใบเดิมแล้ว — "
            + "ใบเดิมถูกเรียกคืนและไม่นับซ้ำในรายงานภาษีขาย"));
    }

    /// <summary>
    /// Valid conversion targets for a document, per the Thai accounting
    /// workflow rules in DocumentService.ValidConversions. The convert UI
    /// calls this so it never offers an option the backend would reject —
    /// a single source of truth instead of a duplicated client-side map.
    /// </summary>
    [HttpGet("{documentId:guid}/conversion-targets")]
    public async Task<ActionResult<ApiResponse<List<DocumentType>>>> GetConversionTargets(
        Guid companyId, Guid documentId)
    {
        var docType = await _db.Documents
            .Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => (DocumentType?)d.DocumentType)
            .FirstOrDefaultAsync();
        if (docType == null)
            return NotFound(new ApiResponse<List<DocumentType>>(false, null, "ไม่พบเอกสาร"));

        // กรองตามข้อจำกัดของบริษัทด้วย (ไม่จด VAT → ไม่มี "ใบกำกับภาษี" ให้เลือก)
        // — กล่องแปลงเอกสารอ่านรายการนี้ จึงไม่โชว์ตัวเลือกที่กดแล้วต้องเจอ error
        var targets = (await _documentService.GetValidConversionTargetsAsync(companyId, docType.Value)).ToList();
        return Ok(new ApiResponse<List<DocumentType>>(true, targets));
    }

    /// <summary>
    /// แปลงเอกสารบางส่วน — เลือกเฉพาะบางรายการและบางจำนวน เช่น แยก PO เดียว
    /// ออกเป็นใบส่งของหลายใบ หรือ Invoice หลายใบ ระบบติดตามจำนวนคงเหลือให้
    /// (แปลงเกินจำนวนที่สั่ง/คงเหลือไม่ได้).
    /// </summary>
    [HttpPost("{documentId:guid}/convert-partial/{targetType}")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> ConvertDocumentPartial(
        Guid companyId, Guid documentId, DocumentType targetType,
        [FromBody] PartialConvertRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var deny = await DenyDocAsync(companyId, userIdGuid, targetType, DocPerm.Create, "แปลงบางส่วนเป็น");
        if (deny != null) return Forbid403<DocumentResponse>(deny);
        var userId = userIdGuid.ToString();
        var result = await _documentService.ConvertDocumentPartialAsync(
            companyId, documentId, targetType, request, userId);
        return Ok(new ApiResponse<DocumentResponse>(true, result, "แปลงเอกสารบางส่วนสำเร็จ"));
    }

    /// <summary>
    /// สถานะการส่งมอบ/วางบิลรายบรรทัด — จำนวนที่สั่ง, ส่งมอบแล้ว, วางบิลแล้ว
    /// และจำนวนคงเหลือของแต่ละแกน ใช้แสดงในหน้าจอแปลงเอกสารบางส่วน.
    /// </summary>
    [HttpGet("{documentId:guid}/fulfillment")]
    public async Task<ActionResult<ApiResponse<DocumentFulfillmentResponse>>> GetFulfillment(
        Guid companyId, Guid documentId)
    {
        var result = await _documentService.GetDocumentFulfillmentAsync(companyId, documentId);
        return Ok(new ApiResponse<DocumentFulfillmentResponse>(true, result));
    }

    [HttpPost("batch-convert/{targetType}")]
    public async Task<ActionResult<ApiResponse<List<DocumentResponse>>>> BatchConvert(
        Guid companyId, DocumentType targetType, [FromBody] BatchConvertRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var deny = await DenyDocAsync(companyId, userIdGuid, targetType, DocPerm.Create, "แปลงเป็น");
        if (deny != null) return Forbid403<List<DocumentResponse>>(deny);
        var userId = userIdGuid.ToString();
        var result = await _documentService.BatchConvertDocumentsAsync(companyId, request.DocumentIds, targetType, userId);
        return Ok(new ApiResponse<List<DocumentResponse>>(true, result,
            $"แปลงสำเร็จ {result.Count}/{request.DocumentIds.Count} ฉบับ"));
    }

    [HttpPost("from-obligation/{performanceObligationId:guid}")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> CreateInvoiceFromObligation(
        Guid companyId, Guid performanceObligationId)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var deny = await DenyDocAsync(companyId, userIdGuid, DocumentType.Invoice, DocPerm.Create, "สร้าง");
        if (deny != null) return Forbid403<DocumentResponse>(deny);
        var userId = userIdGuid.ToString();
        var result = await _documentService.CreateInvoiceFromObligationAsync(companyId, performanceObligationId, userId);
        return Ok(new ApiResponse<DocumentResponse>(true, result, "สร้างใบแจ้งหนี้จากภาระงานสำเร็จ"));
    }

    /// <summary>
    /// ตัดหนี้สูญ — สร้าง JE: Dr หนี้สูญ, Cr ลูกหนี้ และเคลียร์เอกสาร
    /// ใช้สำหรับ Invoice/TaxInvoice/DebitNote ที่ลูกค้าผิดนัดและมั่นใจว่าจะไม่ได้รับเงิน
    /// </summary>
    [HttpPost("{documentId:guid}/write-off-bad-debt")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> WriteOffBadDebt(
        Guid companyId, Guid documentId, [FromBody] WriteOffBadDebtRequest? request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<DocumentResponse>(false, null, "ไม่พบเอกสาร"));
        // ตัดหนี้สูญ = โพสต์ JE (Dr หนี้สูญ / Cr ลูกหนี้) ⇒ ระดับ Approve
        var deny = await DenyDocAsync(companyId, userIdGuid, docType.Value, DocPerm.Approve, "ตัดหนี้สูญของ");
        if (deny != null) return Forbid403<DocumentResponse>(deny);
        var userId = userIdGuid.ToString();
        var result = await _documentService.WriteOffBadDebtAsync(companyId, documentId, userId, request?.Reason);
        return Ok(new ApiResponse<DocumentResponse>(true, result, "ตัดหนี้สูญสำเร็จ"));
    }

    // ===== Contacts =====

    [HttpGet("contacts")]
    public async Task<ActionResult<ApiResponse<PagedResponse<ContactResponse>>>> GetContacts(
        Guid companyId, [FromQuery] bool? isCustomer = null, [FromQuery] bool? isSupplier = null,
        [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var paging = new PagedRequest(page, pageSize);
        var result = await _documentService.GetContactsAsync(companyId, isCustomer, isSupplier, search, paging);
        return Ok(new ApiResponse<PagedResponse<ContactResponse>>(true, result));
    }

    [HttpGet("contacts/{contactId:guid}")]
    public async Task<ActionResult<ApiResponse<ContactResponse>>> GetContact(Guid companyId, Guid contactId)
    {
        var result = await _documentService.GetContactAsync(companyId, contactId);
        return Ok(new ApiResponse<ContactResponse>(true, result));
    }

    [HttpPost("contacts")]
    public async Task<ActionResult<ApiResponse<ContactResponse>>> CreateContact(Guid companyId, [FromBody] CreateContactRequest request)
    {
        var denyC = await DenyKeyAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
            Models.Constants.PermissionKeys.ContactEdit, "สร้างผู้ติดต่อ");
        if (denyC != null) return Forbid403<ContactResponse>(denyC);
        var result = await _documentService.CreateContactAsync(companyId, request);
        return StatusCode(201, new ApiResponse<ContactResponse>(true, result, "สร้างผู้ติดต่อสำเร็จ"));
    }

    /// <summary>กลุ่มผู้ติดต่อซ้ำ (เลขภาษี+สาขา / ชื่อเหมือนเป๊ะ) — ให้หน้า contacts
    /// โชว์ banner + เครื่องมือรวม.</summary>
    [HttpGet("contacts/duplicates")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetDuplicateContacts(Guid companyId)
    {
        var groups = await _documentService.GetDuplicateContactGroupsAsync(companyId);
        return Ok(new ApiResponse<List<object>>(true, groups));
    }

    public sealed record MergeContactsRequest(Guid KeepId, List<Guid> MergeIds);

    /// <summary>รวมผู้ติดต่อซ้ำเข้า record เดียว — เอกสาร/ประวัติทั้งหมดย้ายตาม.</summary>
    [HttpPost("contacts/merge")]
    public async Task<ActionResult<ApiResponse<object>>> MergeContacts(Guid companyId, [FromBody] MergeContactsRequest request)
    {
        try
        {
            var mergeUserId = Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User);
            var denyM = await DenyKeyAsync(companyId, mergeUserId,
                Models.Constants.PermissionKeys.ContactEdit, "รวมผู้ติดต่อ");
            if (denyM != null) return Forbid403<object>(denyM);
            var performedBy = mergeUserId.ToString();
            var rows = await _documentService.MergeContactsAsync(companyId, request.KeepId, request.MergeIds ?? new List<Guid>(), performedBy);
            return Ok(new ApiResponse<object>(true, new { rowsRepointed = rows },
                $"รวมผู้ติดต่อสำเร็จ — ย้ายการอ้างอิง {rows} รายการ"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpPut("contacts/{contactId:guid}")]
    public async Task<ActionResult<ApiResponse<ContactResponse>>> UpdateContact(Guid companyId, Guid contactId, [FromBody] UpdateContactRequest request)
    {
        var denyU = await DenyKeyAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
            Models.Constants.PermissionKeys.ContactEdit, "แก้ไขผู้ติดต่อ");
        if (denyU != null) return Forbid403<ContactResponse>(denyU);
        var result = await _documentService.UpdateContactAsync(companyId, contactId, request);
        return Ok(new ApiResponse<ContactResponse>(true, result));
    }

    [HttpDelete("contacts/{contactId:guid}")]
    public async Task<ActionResult<ApiResponse<ContactDeleteResult>>> DeleteContact(Guid companyId, Guid contactId)
    {
        var denyD = await DenyKeyAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
            Models.Constants.PermissionKeys.ContactEdit, "ลบผู้ติดต่อ");
        if (denyD != null) return Forbid403<ContactDeleteResult>(denyD);
        var result = await _documentService.DeleteContactAsync(companyId, contactId);
        return Ok(new ApiResponse<ContactDeleteResult>(true, result, result.Message));
    }

    /// <summary>
    /// วิเคราะห์ค่าเริ่มต้นอัตโนมัติจากข้อมูลผู้ติดต่อ
    /// เช่น แบบ ภ.ง.ด., ประเภทเอกสาร, อัตราหัก ณ ที่จ่าย
    /// </summary>
    [HttpGet("contacts/{contactId:guid}/smart-defaults")]
    public async Task<ActionResult<ApiResponse<ContactSmartDefaults>>> GetContactSmartDefaults(Guid companyId, Guid contactId)
    {
        var result = await _documentService.GetContactSmartDefaultsAsync(companyId, contactId);
        return Ok(new ApiResponse<ContactSmartDefaults>(true, result));
    }

    /// <summary>
    /// แปลงที่อยู่แบบ free-text → structured fields อัตโนมัติ (สำหรับ UI smart-fill)
    /// ดึง บ้านเลขที่/ตำบล/อำเภอ/จังหวัด/รหัสไปรษณีย์ ออกจาก text
    /// </summary>
    [HttpPost("contacts/parse-address")]
    public ActionResult<ApiResponse<ParsedAddressResponse>> ParseAddress(
        Guid companyId, [FromBody] ParseAddressRequest request)
    {
        var result = ThaiAddressParser.Parse(request.Address);
        return Ok(new ApiResponse<ParsedAddressResponse>(true, result));
    }

    // ===== Payments =====

    [HttpGet("payments")]
    public async Task<ActionResult<ApiResponse<List<PaymentResponse>>>> GetPayments(Guid companyId, [FromQuery] Guid? documentId = null)
    {
        var result = await _documentService.GetPaymentsAsync(companyId, documentId);
        return Ok(new ApiResponse<List<PaymentResponse>>(true, result));
    }

    [HttpPost("payments")]
    public async Task<ActionResult<ApiResponse<PaymentResponse>>> CreatePayment(Guid companyId, [FromBody] CreatePaymentRequest request)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        // ── ด่านสิทธิ์ (A-D2) ── บันทึกการชำระ = โพสต์ JE + ขยับยอดธนาคาร
        // ⇒ ระดับเดียวกับการอนุมัติ. เส้น allocation แตะได้หลายใบ จึงต้องผ่าน
        // **ทุกใบ** ไม่ใช่ใบแรก (ไม่งั้นแนบใบฝั่งที่ตัวเองมีสิทธิ์ 1 ใบ แล้ว
        // พ่วงใบฝั่งที่ไม่มีสิทธิ์เข้าไปด้วยได้)
        var payTargets = (request.Allocations != null && request.Allocations.Count > 0)
            ? request.Allocations.Select(a => a.DocumentId).Distinct().ToList()
            : new List<Guid> { request.DocumentId };
        foreach (var targetId in payTargets)
        {
            var t = await GetDocumentTypeAsync(companyId, targetId);
            if (t == null) return NotFound(new ApiResponse<PaymentResponse>(false, null, "ไม่พบเอกสารที่จะชำระ"));
            var denyPay = await DenyDocAsync(companyId, userIdGuid, t.Value, DocPerm.Approve, "บันทึกการชำระของ");
            if (denyPay != null) return Forbid403<PaymentResponse>(denyPay);
        }
        var userId = userIdGuid.ToString();
        // Single endpoint, two paths: when Allocations is non-empty the
        // multi-doc settler runs; otherwise legacy 1:1 settler.
        var result = (request.Allocations != null && request.Allocations.Count > 0)
            ? await _documentService.CreateMultiDocPaymentAsync(companyId, request, userId)
            : await _documentService.CreatePaymentAsync(companyId, request, userId);
        var msg = (request.Allocations != null && request.Allocations.Count > 0)
            ? $"บันทึกการชำระสำเร็จ — กระจายเป็น {request.Allocations.Count} เอกสาร"
            : "บันทึกการชำระเงินสำเร็จ";
        return StatusCode(201, new ApiResponse<PaymentResponse>(true, result, msg));
    }

    /// <summary>
    /// ยกเลิกการชำระเงิน — กลับรายการ JE + คืนยอดเอกสาร (audit-safe)
    /// </summary>
    [HttpPost("payments/{paymentId:guid}/void")]
    public async Task<ActionResult<ApiResponse<string>>> VoidPayment(Guid companyId, Guid paymentId)
    {
        // ยกเลิกการชำระ = กลับ JE + คืนยอดธนาคาร ⇒ ระดับ Void ของเอกสารต้นทาง
        var payDocType = await _db.Payments
            .Where(pmt => pmt.Id == paymentId && pmt.CompanyId == companyId && !pmt.IsDeleted)
            .Select(pmt => (DocumentType?)pmt.Document.DocumentType)
            .FirstOrDefaultAsync();
        if (payDocType == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบการชำระเงิน"));
        var denyVoidPay = await DenyDocAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
            payDocType.Value, DocPerm.Void, "ยกเลิกการชำระของ");
        if (denyVoidPay != null) return Forbid403<string>(denyVoidPay);
        await _documentService.VoidPaymentAsync(companyId, paymentId);
        return Ok(new ApiResponse<string>(true, null, "ยกเลิกการชำระเงินสำเร็จ"));
    }

    /// <summary>ออกใบเสร็จรับเงินให้การรับชำระที่บันทึกไปแล้ว (ย้อนหลัง)
    ///
    /// <para>ที่มา: ก่อนด่าน ม.105 ("ใบรับต้องลงวันที่ที่รับเงินจริง") ใบกำกับที่รับ
    /// ครบงวดเดียวจะ<b>ยกหัวเป็นใบเสร็จเอง</b>แล้วไม่ออก REC แยก — พอด่านมา หัวกลับ
    /// เป็น "ใบกำกับภาษี" ถูกต้อง แต่แถวเดิมเหลือ **JE รับเงินที่ไม่มีเอกสารคู่**
    /// (ผู้ใช้รายงาน 2026-09-11). ใบเสร็จตัวจริงต้องออกโดยคน ไม่ใช่ migration ไล่
    /// สร้างย้อนหลัง เพราะเลข §86/4 ต้อง gap-free และตรวจสอบได้ว่าใครออก</para></summary>
    [HttpPost("payments/{paymentId:guid}/receipt")]
    public async Task<ActionResult<ApiResponse<IssuedReceiptResult>>> IssueReceiptForPayment(
        Guid companyId, Guid paymentId)
    {
        // ออกใบเสร็จ = ออกเอกสารตามกฎหมายใบใหม่พร้อมเลขจริง ⇒ ระดับ Create ของ
        // Receipt (เส้นบันทึกชำระใช้ Approve ของใบต้นทางอยู่แล้ว — ที่นี่คนกดอาจ
        // เป็นคนละคนกับผู้รับเงิน จึงถามสิทธิ์ของชนิดที่กำลังจะออกจริง)
        var denyIssue = await DenyDocAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
            DocumentType.Receipt, DocPerm.Create, "ออกใบเสร็จรับเงินสำหรับการชำระของ");
        if (denyIssue != null) return Forbid403<IssuedReceiptResult>(denyIssue);
        var result = await _documentService.IssueReceiptForPaymentAsync(
            companyId, paymentId, JwtHelper.GetUserIdFromClaims(User).ToString());
        var msg = result.AlreadyExisted
            ? $"การรับชำระนี้มีใบเสร็จอยู่แล้ว: {result.ReceiptNumber}"
            : result.IsDraft
                ? $"สร้างใบเสร็จรับเงินเป็น \"ร่าง\" แล้ว — รอผู้มีสิทธิ์อนุมัติเพื่อออกเลขจริง"
                // ⚠️ InvariantCulture — th-TH ทำให้ปฏิทินเริ่มต้นเป็นพุทธ ⇒ 2026 → 2569
                // ปนกับ ค.ศ. ในข้อความเดียวกันโดยเงียบ (บทเรียนเดิมของเรพนี้)
                : $"ออกใบเสร็จรับเงิน {result.ReceiptNumber} ลงวันที่ "
                    + $"{result.ReceiptDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} แล้ว";
        return Ok(new ApiResponse<IssuedReceiptResult>(true, result, msg));
    }

    public sealed record BulkApproveRequest(List<Guid> DocumentIds, bool AcknowledgeWarnings);
    public sealed record BulkApproveResult(int Total, int Approved, int Failed, List<string> Errors);

    /// <summary>Bulk approval — Finance อนุมัติเอกสารหลายใบในคลิกเดียว
    /// (เช่นเงินเดือนเดือนนี้มี Expense 50 ใบ). ทำทีละใบใน try/catch —
    /// ใบที่ throw รวมใน Errors แต่ไม่ stop การประมวลผลใบอื่น.</summary>
    [HttpPost("bulk-approve")]
    public async Task<ActionResult<ApiResponse<BulkApproveResult>>> BulkApprove(
        Guid companyId, [FromBody] BulkApproveRequest req)
    {
        if (req.DocumentIds == null || req.DocumentIds.Count == 0)
            return BadRequest(new ApiResponse<BulkApproveResult>(false, null!, "เลือกเอกสารอย่างน้อย 1 ใบ"));
        if (req.DocumentIds.Count > 200)
            return BadRequest(new ApiResponse<BulkApproveResult>(false, null!, "จำกัด bulk ครั้งละ 200 ใบ"));

        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var userId = userIdGuid.ToString();
        var errors = new List<string>();
        int approved = 0, failed = 0;
        foreach (var docId in req.DocumentIds)
        {
            try
            {
                var docType = await GetDocumentTypeAsync(companyId, docId);
                if (docType == null) { errors.Add($"{docId}: ไม่พบเอกสาร"); failed++; continue; }
                if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userIdGuid, docType.Value))
                { errors.Add($"{docId}: ไม่มีสิทธิ์อนุมัติ {docType}"); failed++; continue; }
                await _documentService.ApproveDocumentAsync(companyId, docId, userId, req.AcknowledgeWarnings);
                approved++;
            }
            catch (Exception ex)
            {
                errors.Add($"{docId}: {ex.Message}");
                failed++;
            }
        }
        return Ok(new ApiResponse<BulkApproveResult>(true,
            new BulkApproveResult(req.DocumentIds.Count, approved, failed, errors),
            $"อนุมัติ {approved}/{req.DocumentIds.Count} ใบ" + (failed > 0 ? $" — ล้มเหลว {failed} ใบ" : "")));
    }
}
