using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Integration;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Ocr;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
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
    private readonly Data.AccountingDbContext _db;
    private readonly IPermissionService _permissions;

    public IntegrationController(IIntegrationService service, IFileAttachmentService attachmentService,
        Data.AccountingDbContext db, IPermissionService permissions)
    {
        _service = service;
        _attachmentService = attachmentService;
        _db = db;
        _permissions = permissions;
    }

    /// <summary>
    /// **ด่าน "เจ้าของเท่านั้น" ของการออก/แก้/ลบ/สร้างคีย์ใหม่ + การผูกผู้ใช้ที่คีย์สวมได้** (G2-01 · คำตัดสินข้อ 37)
    ///
    /// <para>การออกคีย์คือ "การให้สิทธิ์" — เดิม endpoint มีแค่ <c>[Authorize]</c> (= ล็อกอินไหม) ⇒ พนักงาน
    /// "ดูอย่างเดียว" ออกคีย์เขียน/ลบได้ทุกอย่าง แล้วสวมเป็นเจ้าของด้วย <c>X-Acting-User</c></para>
    ///
    /// <para>ใช้กลไกเดิมของระบบ: <c>CompanyUser.Role</c> = Owner/SystemAdmin (แบบเดียวกับ
    /// <c>OwnerConfigController</c>/<c>PermissionCatalogController</c>) · <b>ปฏิเสธเสมอเมื่อเข้ามาด้วย API key</b> —
    /// คีย์ออกคีย์เองไม่ได้ (ไม่งั้นคีย์รุ่นเก่าที่สวมเป็นเจ้าของด้วยอีเมลจะออกคีย์ใหม่สิทธิ์เต็มให้ตัวเองได้)</para>
    /// </summary>
    private async Task<ActionResult?> RequireOwnerAsync(Guid companyId, string verb)
    {
        if (HttpContext.Items.TryGetValue("IsApiKeyAuth", out var ak) && ak is true)
            return StatusCode(403, new ApiResponse<object>(false, null,
                $"{verb}ด้วย API key ไม่ได้ — ต้องเข้าสู่ระบบเป็นเจ้าของบริษัท"));

        var userId = JwtHelper.GetUserIdFromClaims(User);
        var role = await _db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
            .Select(cu => (UserRole?)cu.Role)
            .FirstOrDefaultAsync();
        if (role == UserRole.Owner || role == UserRole.SystemAdmin) return null;

        return StatusCode(403, new ApiResponse<object>(false, new { requiredRole = "Owner" },
            $"ไม่มีสิทธิ์{verb} — การออก/แก้/ลบคีย์เชื่อมต่อระบบและการผูกผู้ใช้ที่คีย์สวมได้ ทำได้เฉพาะเจ้าของบริษัท"));
    }

    /// <summary>ด่านของ Account Mapping (ผูก category ภายนอก → ผังบัญชี) — ไม่ใช่การให้สิทธิ์คีย์
    /// จึงใช้คีย์สิทธิ์ที่มีอยู่แล้ว <see cref="PermissionKeys.CompanySettingsEdit"/> (Owner ผ่านอัตโนมัติ)</summary>
    private async Task<ActionResult?> RequireSettingsAsync(Guid companyId, string verb)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.CompanySettingsEdit))
            return null;
        return StatusCode(403, new ApiResponse<object>(false, new
        {
            requiredPermission = PermissionKeys.CompanySettingsEdit.Replace("perm:", ""),
        }, $"ไม่มีสิทธิ์{verb} — ต้องได้รับสิทธิ์ \u201c{PermissionKeys.CompanySettingsEdit.Replace("perm:", "")}\u201d จากเจ้าของบริษัทก่อน"));
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
        if (await RequireOwnerAsync(companyId, "ออกคีย์เชื่อมต่อระบบ") is { } deny) return deny;
        var result = await _service.CreateIntegrationAsync(companyId, request);
        return StatusCode(201, new ApiResponse<IntegrationCreatedResponse>(true, result, "สร้าง Integration สำเร็จ (เก็บ API Key ไว้ จะแสดงครั้งเดียว)"));
    }

    [HttpPut("{integrationId:guid}")]
    public async Task<ActionResult<ApiResponse<IntegrationResponse>>> UpdateIntegration(Guid companyId, Guid integrationId, [FromBody] UpdateIntegrationRequest request)
    {
        if (await RequireOwnerAsync(companyId, "แก้คีย์เชื่อมต่อระบบ") is { } deny) return deny;
        var result = await _service.UpdateIntegrationAsync(companyId, integrationId, request);
        return Ok(new ApiResponse<IntegrationResponse>(true, result));
    }

    [HttpDelete("{integrationId:guid}")]
    public async Task<IActionResult> DeleteIntegration(Guid companyId, Guid integrationId)
    {
        if (await RequireOwnerAsync(companyId, "ลบคีย์เชื่อมต่อระบบ") is { } deny) return deny;
        await _service.DeleteIntegrationAsync(companyId, integrationId);
        return NoContent();
    }

    [HttpPost("{integrationId:guid}/regenerate-key")]
    public async Task<ActionResult<ApiResponse<IntegrationCreatedResponse>>> RegenerateKey(Guid companyId, Guid integrationId)
    {
        if (await RequireOwnerAsync(companyId, "สร้างคีย์เชื่อมต่อระบบใหม่") is { } deny) return deny;
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
        if (await RequireSettingsAsync(companyId, "เพิ่ม Account Mapping") is { } deny) return deny;
        var result = await _service.CreateMappingAsync(companyId, integrationId, request);
        return StatusCode(201, new ApiResponse<AccountMappingResponse>(true, result));
    }

    [HttpPut("{integrationId:guid}/mappings/{mappingId:guid}")]
    public async Task<ActionResult<ApiResponse<AccountMappingResponse>>> UpdateMapping(Guid companyId, Guid integrationId, Guid mappingId, [FromBody] UpdateAccountMappingRequest request)
    {
        if (await RequireSettingsAsync(companyId, "แก้ Account Mapping") is { } deny) return deny;
        var result = await _service.UpdateMappingAsync(companyId, integrationId, mappingId, request);
        return Ok(new ApiResponse<AccountMappingResponse>(true, result));
    }

    [HttpDelete("{integrationId:guid}/mappings/{mappingId:guid}")]
    public async Task<IActionResult> DeleteMapping(Guid companyId, Guid integrationId, Guid mappingId)
    {
        if (await RequireSettingsAsync(companyId, "ลบ Account Mapping") is { } deny) return deny;
        await _service.DeleteMappingAsync(companyId, integrationId, mappingId);
        return NoContent();
    }

    // ===== Operator (user) mapping =====
    // Map the partner's operator key (sent in X-Acting-User) → a NextAcc user
    // so integration-created documents carry the real operator's creator
    // signature. รอบ 193 (G2-01): แถวที่นี่คือ **ทางเดียว** ที่คีย์ใหม่สวมผู้ใช้ได้
    // (email match เหลือเฉพาะคีย์รุ่นเก่าในช่วงผ่อนผัน) ⇒ การเขียนแถว = การให้สิทธิ์
    // ⇒ เจ้าของบริษัทเท่านั้น

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
        if (await RequireOwnerAsync(companyId, "ผูกผู้ใช้ที่คีย์สวมได้") is { } deny) return deny;
        if (string.IsNullOrWhiteSpace(req.ExternalUserKey))
            return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุ ExternalUserKey"));
        // The target user must be a member of this company.
        var isMember = await db.CompanyUsers.AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == req.UserId);
        if (!isMember)
            return BadRequest(new ApiResponse<object>(false, null, "ผู้ใช้ที่เลือกไม่ได้เป็นสมาชิกของบริษัทนี้"));
        // integration ต้องเป็นของบริษัทนี้ (กฎ M) — เดิมรับ integrationId จาก URL โดยไม่ตรวจ
        var ownsIntegration = await db.ExternalIntegrations.AnyAsync(i => i.Id == integrationId && i.CompanyId == companyId);
        if (!ownsIntegration)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบ Integration"));

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
        if (await RequireOwnerAsync(companyId, "ลบการผูกผู้ใช้ที่คีย์สวมได้") is { } deny) return deny;
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
public class ExternalIntegrationController : ControllerBase, IAsyncActionFilter
{
    private readonly IIntegrationService _service;
    private readonly IFileAttachmentService _attachmentService;

    public ExternalIntegrationController(IIntegrationService service, IFileAttachmentService attachmentService)
    {
        _service = service;
        _attachmentService = attachmentService;
    }

    // ผลตรวจคีย์ของ request นี้ (controller เป็น per-request) — ตรวจครั้งเดียวในฟิลเตอร์ แล้วทุก action
    // ใช้ซ้ำผ่าน AuthenticateIntegration() ไม่ต้องเสีย BCrypt ซ้ำ
    private bool _authResolved;
    private (Guid CompanyId, Guid IntegrationId, IntegrationKeyScopes Scopes)? _auth;

    /// <summary>
    /// **ด่านสิทธิ์ของคีย์บนทางเข้า X-Integration-Key** (รอบ 193 · G2-01)
    ///
    /// <para>ทางเข้านี้เป็น <c>[AllowAnonymous]</c> + ตรวจคีย์เอง จึง<b>ไม่ผ่าน</b> <c>ApiKeyScopeFilter</c>
    /// (ซึ่งทำงานเฉพาะ X-Api-Key) ⇒ ถ้าไม่มีด่านนี้ คีย์ใหม่ที่เจ้าของตั้ง "อ่านอย่างเดียว" จะยังสร้างใบกำกับ/
    /// ยกเลิกเอกสาร/กลับรายการ JE ได้ผ่านทางเข้านี้ (R5 "ทางเข้าอื่นไม่เดินด่าน") · method → สิทธิ์ ตัดสินที่
    /// <c>IntegrationKeyPolicy</c> ตัวเดียวกับ X-Api-Key</para>
    ///
    /// <para>ไม่มีคีย์/คีย์ผิด ⇒ ปล่อยให้ action ตอบ 401 แบบเดิม (สัญญากับคู่ค้าไม่เปลี่ยน)</para>
    /// </summary>
    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var auth = await AuthenticateIntegration();
        if (auth != null && !IntegrationKeyPolicy.Allows(auth.Value.Scopes, Request.Method))
        {
            var scope = IntegrationKeyPolicy.RequiredScope(Request.Method);
            context.Result = new ObjectResult(new
            {
                success = false,
                message = $"API key นี้ไม่มีสิทธิ์{IntegrationKeyPolicy.ScopeLabel(scope)} — "
                    + "เจ้าของบริษัทแก้สิทธิ์ของคีย์ได้ที่หน้าเชื่อมต่อระบบ",
                requiredScope = IntegrationKeyPolicy.ScopeName(scope),
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }
        await next();
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

    private async Task<(Guid CompanyId, Guid IntegrationId, IntegrationKeyScopes Scopes)?> AuthenticateIntegration()
    {
        if (_authResolved) return _auth;
        _authResolved = true;

        var apiKey = Request.Headers["X-Integration-Key"].FirstOrDefault()
            ?? Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");

        if (string.IsNullOrEmpty(apiKey)) return _auth = null;
        return _auth = await _service.ValidateApiKeyAsync(apiKey);
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
