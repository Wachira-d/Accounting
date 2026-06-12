using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Integration;
using Accounting.Services.Implementations.Ocr;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Integration Management — ตั้งค่าและจัดการการเชื่อมต่อระบบภายนอก (per-company)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/integrations")]
[Authorize]
public class IntegrationController : ControllerBase
{
    private readonly IIntegrationService _service;
    private readonly IFileAttachmentService _attachmentService;

    public IntegrationController(IIntegrationService service, IFileAttachmentService attachmentService)
    {
        _service = service;
        _attachmentService = attachmentService;
    }

    // ===== Integration Config =====

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<IntegrationResponse>>>> GetIntegrations(Guid companyId)
    {
        var result = await _service.GetIntegrationsAsync(companyId);
        return Ok(new ApiResponse<List<IntegrationResponse>>(true, result));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<IntegrationCreatedResponse>>> CreateIntegration(Guid companyId, [FromBody] CreateIntegrationRequest request)
    {
        var result = await _service.CreateIntegrationAsync(companyId, request);
        return StatusCode(201, new ApiResponse<IntegrationCreatedResponse>(true, result, "สร้าง Integration สำเร็จ (เก็บ API Key ไว้ จะแสดงครั้งเดียว)"));
    }

    [HttpPut("{integrationId:guid}")]
    public async Task<ActionResult<ApiResponse<IntegrationResponse>>> UpdateIntegration(Guid companyId, Guid integrationId, [FromBody] UpdateIntegrationRequest request)
    {
        var result = await _service.UpdateIntegrationAsync(companyId, integrationId, request);
        return Ok(new ApiResponse<IntegrationResponse>(true, result));
    }

    [HttpDelete("{integrationId:guid}")]
    public async Task<IActionResult> DeleteIntegration(Guid companyId, Guid integrationId)
    {
        await _service.DeleteIntegrationAsync(companyId, integrationId);
        return NoContent();
    }

    [HttpPost("{integrationId:guid}/regenerate-key")]
    public async Task<ActionResult<ApiResponse<IntegrationCreatedResponse>>> RegenerateKey(Guid companyId, Guid integrationId)
    {
        var result = await _service.RegenerateApiKeyAsync(companyId, integrationId);
        return Ok(new ApiResponse<IntegrationCreatedResponse>(true, result, "สร้าง API Key ใหม่สำเร็จ"));
    }

    // ===== Account Mapping =====

    [HttpGet("{integrationId:guid}/mappings")]
    public async Task<ActionResult<ApiResponse<List<AccountMappingResponse>>>> GetMappings(Guid companyId, Guid integrationId)
    {
        var result = await _service.GetMappingsAsync(companyId, integrationId);
        return Ok(new ApiResponse<List<AccountMappingResponse>>(true, result));
    }

    [HttpPost("{integrationId:guid}/mappings")]
    public async Task<ActionResult<ApiResponse<AccountMappingResponse>>> CreateMapping(Guid companyId, Guid integrationId, [FromBody] CreateAccountMappingRequest request)
    {
        var result = await _service.CreateMappingAsync(companyId, integrationId, request);
        return StatusCode(201, new ApiResponse<AccountMappingResponse>(true, result));
    }

    [HttpPut("{integrationId:guid}/mappings/{mappingId:guid}")]
    public async Task<ActionResult<ApiResponse<AccountMappingResponse>>> UpdateMapping(Guid companyId, Guid integrationId, Guid mappingId, [FromBody] UpdateAccountMappingRequest request)
    {
        var result = await _service.UpdateMappingAsync(companyId, integrationId, mappingId, request);
        return Ok(new ApiResponse<AccountMappingResponse>(true, result));
    }

    [HttpDelete("{integrationId:guid}/mappings/{mappingId:guid}")]
    public async Task<IActionResult> DeleteMapping(Guid companyId, Guid integrationId, Guid mappingId)
    {
        await _service.DeleteMappingAsync(companyId, integrationId, mappingId);
        return NoContent();
    }

    // ===== Operator (user) mapping =====
    // Map the partner's operator key (sent in X-Acting-User) → a NextAcc user
    // so integration-created documents carry the real operator's creator
    // signature. Email keys auto-match against company members without a row
    // here; this is for non-email external ids.

    public sealed record UserMappingRequest(string ExternalUserKey, Guid UserId, string? ExternalUserName);

    [HttpGet("{integrationId:guid}/user-mappings")]
    public async Task<ActionResult<ApiResponse<object>>> GetUserMappings(
        Guid companyId, Guid integrationId, [FromServices] Data.AccountingDbContext db)
    {
        var rows = await db.IntegrationUserMappings.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.IntegrationId == integrationId && !m.IsDeleted)
            .Select(m => new { m.Id, m.ExternalUserKey, m.UserId, m.ExternalUserName,
                UserName = db.Users.Where(u => u.Id == m.UserId).Select(u => u.FullName).FirstOrDefault(),
                UserEmail = db.Users.Where(u => u.Id == m.UserId).Select(u => u.Email).FirstOrDefault() })
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, rows));
    }

    [HttpPost("{integrationId:guid}/user-mappings")]
    public async Task<ActionResult<ApiResponse<object>>> UpsertUserMapping(
        Guid companyId, Guid integrationId, [FromBody] UserMappingRequest req,
        [FromServices] Data.AccountingDbContext db)
    {
        if (string.IsNullOrWhiteSpace(req.ExternalUserKey))
            return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุ ExternalUserKey"));
        // The target user must be a member of this company.
        var isMember = await db.CompanyUsers.AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == req.UserId);
        if (!isMember)
            return BadRequest(new ApiResponse<object>(false, null, "ผู้ใช้ที่เลือกไม่ได้เป็นสมาชิกของบริษัทนี้"));

        var key = req.ExternalUserKey.Trim();
        var existing = await db.IntegrationUserMappings
            .FirstOrDefaultAsync(m => m.CompanyId == companyId && m.IntegrationId == integrationId
                && m.ExternalUserKey.ToLower() == key.ToLower() && !m.IsDeleted);
        if (existing != null)
        {
            existing.UserId = req.UserId;
            existing.ExternalUserName = req.ExternalUserName;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            db.IntegrationUserMappings.Add(new Models.Entities.IntegrationUserMapping
            {
                CompanyId = companyId, IntegrationId = integrationId,
                ExternalUserKey = key, UserId = req.UserId, ExternalUserName = req.ExternalUserName,
            });
        }
        await db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, "บันทึก mapping ผู้ใช้สำเร็จ"));
    }

    [HttpDelete("{integrationId:guid}/user-mappings/{mappingId:guid}")]
    public async Task<IActionResult> DeleteUserMapping(
        Guid companyId, Guid integrationId, Guid mappingId, [FromServices] Data.AccountingDbContext db)
    {
        var row = await db.IntegrationUserMappings
            .FirstOrDefaultAsync(m => m.Id == mappingId && m.CompanyId == companyId && m.IntegrationId == integrationId);
        if (row == null) return NotFound();
        row.IsDeleted = true;
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ===== Mapping Templates =====

    [HttpGet("mapping-templates")]
    public ActionResult<ApiResponse<List<MappingTemplateResponse>>> GetMappingTemplates()
    {
        var result = _service.GetMappingTemplates();
        return Ok(new ApiResponse<List<MappingTemplateResponse>>(true, result));
    }

    // ===== Sync Logs =====

    [HttpGet("sync-logs")]
    public async Task<ActionResult<ApiResponse<List<SyncLogResponse>>>> GetSyncLogs(Guid companyId, [FromQuery] Guid? integrationId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _service.GetSyncLogsAsync(companyId, integrationId, page, pageSize);
        return Ok(new ApiResponse<List<SyncLogResponse>>(true, result));
    }

    // ===== Dashboard =====

    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResponse<IntegrationDashboardResponse>>> GetDashboard(Guid companyId)
    {
        var result = await _service.GetDashboardAsync(companyId);
        return Ok(new ApiResponse<IntegrationDashboardResponse>(true, result));
    }

    // ===== Revenue Reports =====

    [HttpGet("reports/revenue-by-category")]
    public async Task<ActionResult<ApiResponse<List<RevenueByCategoryItem>>>> GetRevenueByCategory(Guid companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var result = await _service.GetRevenueByCategoryAsync(companyId, from, to);
        return Ok(new ApiResponse<List<RevenueByCategoryItem>>(true, result));
    }

    [HttpGet("reports/revenue-by-source")]
    public async Task<ActionResult<ApiResponse<List<RevenueBySourceItem>>>> GetRevenueBySource(Guid companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var result = await _service.GetRevenueBySourceAsync(companyId, from, to);
        return Ok(new ApiResponse<List<RevenueBySourceItem>>(true, result));
    }

    [HttpGet("reports/deposit-summary")]
    public async Task<ActionResult<ApiResponse<DepositSummaryResponse>>> GetDepositSummary(Guid companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var result = await _service.GetDepositSummaryAsync(companyId, from, to);
        return Ok(new ApiResponse<DepositSummaryResponse>(true, result));
    }

    [HttpGet("reports/daily-revenue")]
    public async Task<ActionResult<ApiResponse<List<DailyRevenueItem>>>> GetDailyRevenue(Guid companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var result = await _service.GetDailyRevenueAsync(companyId, from, to);
        return Ok(new ApiResponse<List<DailyRevenueItem>>(true, result));
    }
}

/// <summary>
/// External Integration API — Universal endpoints for ANY external system
/// Authenticated by Integration API Key via X-Integration-Key header
/// </summary>
[ApiController]
[Route("api/integration")]
[AllowAnonymous]
public class ExternalIntegrationController : ControllerBase
{
    private readonly IIntegrationService _service;
    private readonly IFileAttachmentService _attachmentService;

    public ExternalIntegrationController(IIntegrationService service, IFileAttachmentService attachmentService)
    {
        _service = service;
        _attachmentService = attachmentService;
    }

    // ===== Inbound endpoints (external systems push data TO Next Acc) =====

    [HttpPost("customers")]
    public async Task<ActionResult<InboundSyncResponse>> SyncCustomer([FromBody] InboundCustomerRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessCustomerAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Create an invoice from an external system. Supports optional embedded
    /// attachments (base64-encoded) in the same request — see Attachments field.
    /// For files >5MB use the /invoices/multipart variant to avoid base64 overhead.
    /// </summary>
    /// <remarks>
    /// Example payload with attachment:
    /// <code>
    /// {
    ///   "ExternalRef": "POS-INV-2026-001",
    ///   "CustomerTaxId": "0105561234567",
    ///   "DocumentDate": "2026-05-07",
    ///   "DocumentType": "TaxInvoice",
    ///   "Lines": [
    ///     { "ItemName": "เครื่องดื่ม", "Quantity": 2, "UnitPrice": 50 }
    ///   ],
    ///   "VatRate": 7,
    ///   "Attachments": [
    ///     {
    ///       "FileName": "receipt.jpg",
    ///       "ContentType": "image/jpeg",
    ///       "Base64Content": "/9j/4AAQSkZJRgABAQ..."
    ///     }
    ///   ]
    /// }
    /// </code>
    /// Headers: X-Integration-Key: {your-api-key}
    /// </remarks>
    [HttpPost("invoices")]
    public async Task<ActionResult<InboundSyncResponse>> CreateInvoice([FromBody] InboundInvoiceRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessInvoiceAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        result = await AttachFilesAsync(auth.Value.CompanyId, result, request.Attachments);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Multipart variant for systems uploading invoices with large binary attachments
    /// (>5MB where base64 encoding adds 33% overhead). Send the InboundInvoiceRequest
    /// as a JSON string in the "invoice" field, plus IFormFile entries named "files".
    ///
    /// Example with curl:
    ///   curl -X POST -H "X-Integration-Key: ..." \
    ///        -F 'invoice={"DocumentDate":"2026-05-07",...}' \
    ///        -F 'files=@receipt.pdf' \
    ///        -F 'files=@photo.jpg' \
    ///        https://api.example.com/api/companies/{cid}/integrations/invoices/multipart
    /// </summary>
    [HttpPost("invoices/multipart")]
    [RequestSizeLimit(50 * 1024 * 1024)] // 50MB total payload
    public async Task<ActionResult<InboundSyncResponse>> CreateInvoiceMultipart(
        [FromForm] string invoice,
        [FromForm(Name = "files")] List<IFormFile>? files)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        InboundInvoiceRequest? request;
        try
        {
            request = System.Text.Json.JsonSerializer.Deserialize<InboundInvoiceRequest>(invoice,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            return BadRequest(new InboundSyncResponse(false,
                $"Invalid JSON in 'invoice' field: {ex.Message}", null, null, null, null, null));
        }
        if (request == null)
            return BadRequest(new InboundSyncResponse(false, "Empty invoice payload", null, null, null, null, null));

        var result = await _service.ProcessInvoiceAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);

        // Convert IFormFile list to InboundAttachment so the same validation pipeline
        // (size cap, magic-byte check, MIME validation) runs uniformly.
        if (files != null && files.Count > 0 && result.Success && result.DocumentId.HasValue)
        {
            var attachments = new List<InboundAttachment>();
            foreach (var f in files)
            {
                if (f.Length == 0) continue;
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms);
                attachments.Add(new InboundAttachment(
                    f.FileName, f.ContentType ?? "application/octet-stream",
                    Convert.ToBase64String(ms.ToArray())));
            }
            result = await AttachFilesAsync(auth.Value.CompanyId, result, attachments);
        }

        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("payments")]
    public async Task<ActionResult<InboundSyncResponse>> RecordPayment([FromBody] InboundPaymentRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessPaymentAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("credit-notes")]
    public async Task<ActionResult<InboundSyncResponse>> CreateCreditNote([FromBody] InboundCreditNoteRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessCreditNoteAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        result = await AttachFilesAsync(auth.Value.CompanyId, result, request.Attachments);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("debit-notes")]
    public async Task<ActionResult<InboundSyncResponse>> CreateDebitNote([FromBody] InboundDebitNoteRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessDebitNoteAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        result = await AttachFilesAsync(auth.Value.CompanyId, result, request.Attachments);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("expenses")]
    public async Task<ActionResult<InboundSyncResponse>> CreateExpense([FromBody] InboundExpenseRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessExpenseAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        result = await AttachFilesAsync(auth.Value.CompanyId, result, request.Attachments);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>สร้างใบสำคัญจ่าย (จ่ายเงินจริงแล้ว) — เอกสารเดียวจบ:
    /// Dr ค่าใช้จ่าย+ภาษีซื้อ / Cr เงินสด (+Cr WHT ค้างจ่าย พร้อมออกใบ 50 ทวิ
    /// อัตโนมัติ) สำหรับ voucher ที่จ่ายไปแล้วในระบบต้นทาง — ไม่ต้อง map เป็น
    /// expense + payment สองยกอีกต่อไป (expense ใช้กับกรณีตั้งหนี้รอจ่ายเท่านั้น)</summary>
    [HttpPost("payment-vouchers")]
    public async Task<ActionResult<InboundSyncResponse>> CreatePaymentVoucher([FromBody] InboundPaymentVoucherRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessPaymentVoucherAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        result = await AttachFilesAsync(auth.Value.CompanyId, result, request.Attachments);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("certificates-in-lieu")]
    public async Task<ActionResult<InboundSyncResponse>> CreateCertificateInLieu([FromBody] InboundCertificateInLieuRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessCertificateInLieuAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        result = await AttachFilesAsync(auth.Value.CompanyId, result, request.Attachments);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("products")]
    public async Task<ActionResult<InboundSyncResponse>> SyncProduct([FromBody] InboundProductRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessProductAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("journals")]
    public async Task<ActionResult<InboundSyncResponse>> CreateJournal([FromBody] InboundJournalRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessJournalAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("journals/reverse")]
    public async Task<ActionResult<InboundSyncResponse>> ReverseJournal([FromBody] InboundReverseJournalRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessJournalReverseAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Void a document the integration pushed earlier — Receipt, TaxInvoice,
    /// PaymentVoucher, etc. Cascade ครบ (reverse posted JE, void linked payments
    /// with their JE reversals, unmatch bank). Idempotent on already-voided docs.
    /// ใช้ตอน booking system ต้องการยกเลิกใบเสร็จมัดจำเพื่อออกใบเสร็จเต็ม.
    /// </summary>
    [HttpPost("documents/void")]
    public async Task<ActionResult<InboundSyncResponse>> VoidDocument([FromBody] InboundVoidDocumentRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.VoidDocumentByExternalRefAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("daily-summary")]
    public async Task<ActionResult<InboundSyncResponse>> DailySummary([FromBody] InboundDailySummaryRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessDailySummaryAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("batch")]
    public async Task<ActionResult<InboundBatchResponse>> BatchImport([FromBody] InboundBatchRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundBatchResponse(0, 0, 0, new List<BatchResultItem>()));

        var result = await _service.ProcessBatchAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return Ok(result);
    }

    // ===== Outbound endpoints (external systems read data FROM Next Acc) =====

    [HttpGet("documents")]
    public async Task<ActionResult<OutboundPagedResponse<OutboundDocumentResponse>>> GetDocuments([FromQuery] OutboundQueryParams query)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized();

        var result = await _service.GetDocumentsForExternalAsync(auth.Value.CompanyId, query);
        return Ok(result);
    }

    [HttpGet("contacts")]
    public async Task<ActionResult<OutboundPagedResponse<OutboundContactResponse>>> GetContacts([FromQuery] OutboundQueryParams query)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized();

        var result = await _service.GetContactsForExternalAsync(auth.Value.CompanyId, query);
        return Ok(result);
    }

    [HttpGet("payments-list")]
    public async Task<ActionResult<OutboundPagedResponse<OutboundPaymentResponse>>> GetPayments([FromQuery] OutboundQueryParams query)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized();

        var result = await _service.GetPaymentsForExternalAsync(auth.Value.CompanyId, query);
        return Ok(result);
    }

    [HttpGet("account-balances")]
    public async Task<ActionResult<List<OutboundAccountBalanceResponse>>> GetAccountBalances()
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized();

        var result = await _service.GetAccountBalancesForExternalAsync(auth.Value.CompanyId);
        return Ok(result);
    }

    private async Task<(Guid CompanyId, Guid IntegrationId)?> AuthenticateIntegration()
    {
        var apiKey = Request.Headers["X-Integration-Key"].FirstOrDefault()
            ?? Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");

        if (string.IsNullOrEmpty(apiKey)) return null;
        return await _service.ValidateApiKeyAsync(apiKey);
    }

    /// <summary>
    /// After a document is created from an inbound integration request, save any
    /// attached files (base64-encoded) and link them to the new document. Failures
    /// per-file are non-fatal — they're reported as warnings on the response so the
    /// document still goes through even if one supporting image is malformed.
    /// </summary>
    private async Task<InboundSyncResponse> AttachFilesAsync(
        Guid companyId, InboundSyncResponse result, List<InboundAttachment>? attachments)
    {
        if (!result.Success || result.DocumentId == null || attachments == null || attachments.Count == 0)
            return result;

        var attachmentIds = new List<Guid>();
        var warnings = new List<string>();

        foreach (var att in attachments)
        {
            if (string.IsNullOrEmpty(att.FileName) || string.IsNullOrEmpty(att.Base64Content))
            {
                warnings.Add($"Skipped: missing FileName or Base64Content");
                continue;
            }

            byte[] fileBytes;
            try
            {
                fileBytes = Convert.FromBase64String(att.Base64Content);
            }
            catch
            {
                warnings.Add($"{att.FileName}: invalid base64 — skipped");
                continue;
            }

            // Reuse the same preflight that OCR uploads use: size cap, MIME validation,
            // magic-byte check. Prevents external systems from uploading mislabeled or
            // corrupt files that would later 404 in the UI.
            var preflight = OcrPreprocessor.Check(fileBytes, att.ContentType ?? "", att.FileName);
            if (!preflight.Ok)
            {
                warnings.Add($"{att.FileName}: {preflight.ErrorMessage} — skipped");
                continue;
            }

            try
            {
                var shortGuid = Guid.NewGuid().ToString("N")[..8];
                var fileName = $"Document_{result.DocumentId}_{DateTime.UtcNow:yyyyMMddHHmmss}_{shortGuid}{Path.GetExtension(att.FileName)}";
                var storagePath = Path.Combine("uploads", "attachments", companyId.ToString(), fileName);
                var fullPath = Path.Combine(Directory.GetCurrentDirectory(), storagePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await System.IO.File.WriteAllBytesAsync(fullPath, fileBytes);

                // ApiKey-authenticated requests have no user identity — use a synthetic
                // system user marker. Audit log will show "uploaded via integration".
                var systemUserId = Guid.Empty;
                var saved = await _attachmentService.UploadAsync(
                    companyId, "Document", result.DocumentId.Value,
                    fileName, att.FileName, att.ContentType ?? "application/octet-stream",
                    fileBytes.Length, storagePath, systemUserId);
                attachmentIds.Add(saved.Id);
            }
            catch (Exception ex)
            {
                warnings.Add($"{att.FileName}: save failed — {ex.Message}");
            }
        }

        return result with
        {
            AttachmentIds = attachmentIds.Count > 0 ? attachmentIds : null,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }
}
