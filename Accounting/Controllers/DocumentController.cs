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
        AccountingDbContext db, IPermissionService permissions)
    {
        _documentService = documentService;
        _docEmailService = docEmailService;
        _db = db;
        _permissions = permissions;
    }

    private async Task<DocumentType?> GetDocumentTypeAsync(Guid companyId, Guid documentId) =>
        await _db.Documents.Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => (DocumentType?)d.DocumentType).FirstOrDefaultAsync();

    private ActionResult<ApiResponse<T>> Forbid403<T>(string th)
        => StatusCode(403, new ApiResponse<T>(false, default, th));

    // ===== Send document via email =====

    [HttpPost("{documentId:guid}/send-email")]
    public async Task<ActionResult<ApiResponse<DocumentEmailLogResponse>>> SendEmail(
        Guid companyId, Guid documentId, [FromBody] SendDocumentEmailRequest request)
    {
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
        // ใช้ permission เดียวกับ Edit (เพราะแก้ Draft อยู่)
        var lines = (request.Lines ?? new()).Select(l =>
            (l.AccountId, l.DebitAmount, l.CreditAmount, l.Description, l.ProjectId, l.Reason));
        var result = await _documentService.SaveAdjustingJournalLinesAsync(
            companyId, documentId, lines, userIdGuid.ToString());
        return Ok(new ApiResponse<DocumentResponse>(true, result,
            $"บันทึก adjusting JE lines {request.Lines?.Count ?? 0} รายการแล้ว"));
    }

    [HttpPost("{documentId:guid}/void")]
    public async Task<ActionResult<ApiResponse<string>>> VoidDocument(Guid companyId, Guid documentId)
    {
        var userIdGuid = JwtHelper.GetUserIdFromClaims(User);
        var docType = await GetDocumentTypeAsync(companyId, documentId);
        if (docType == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanVoidAsync(_permissions, companyId, userIdGuid, docType.Value))
            return Forbid403<string>(
                $"ไม่มีสิทธิ์ยกเลิกเอกสาร {docType} (ต้องการ Document.Void หรือ Document.{(DocumentPermissionHelper.IsRevenue(docType.Value) ? "Revenue" : "Purchase")}.Void)");
        await _documentService.VoidDocumentAsync(companyId, documentId);
        return Ok(new ApiResponse<string>(true, null, "ยกเลิกเอกสารสำเร็จ"));
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

    [HttpPost("{documentId:guid}/convert/{targetType}")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> ConvertDocument(Guid companyId, Guid documentId, DocumentType targetType)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _documentService.ConvertDocumentAsync(companyId, documentId, targetType, userId);
        return Ok(new ApiResponse<DocumentResponse>(true, result, "แปลงเอกสารสำเร็จ"));
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

        var targets = DocumentService.GetValidConversionTargets(docType.Value).ToList();
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
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
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
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _documentService.BatchConvertDocumentsAsync(companyId, request.DocumentIds, targetType, userId);
        return Ok(new ApiResponse<List<DocumentResponse>>(true, result,
            $"แปลงสำเร็จ {result.Count}/{request.DocumentIds.Count} ฉบับ"));
    }

    [HttpPost("from-obligation/{performanceObligationId:guid}")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> CreateInvoiceFromObligation(
        Guid companyId, Guid performanceObligationId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
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
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
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
            var performedBy = Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User).ToString();
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
        var result = await _documentService.UpdateContactAsync(companyId, contactId, request);
        return Ok(new ApiResponse<ContactResponse>(true, result));
    }

    [HttpDelete("contacts/{contactId:guid}")]
    public async Task<ActionResult<ApiResponse<ContactDeleteResult>>> DeleteContact(Guid companyId, Guid contactId)
    {
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
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
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
        await _documentService.VoidPaymentAsync(companyId, paymentId);
        return Ok(new ApiResponse<string>(true, null, "ยกเลิกการชำระเงินสำเร็จ"));
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
