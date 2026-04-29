using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Email;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class DocumentController : ControllerBase
{
    private readonly IDocumentService _documentService;
    private readonly IDocumentEmailService _docEmailService;

    public DocumentController(IDocumentService documentService, IDocumentEmailService docEmailService)
    {
        _documentService = documentService;
        _docEmailService = docEmailService;
    }

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
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null,
        [FromQuery] Guid? projectId = null)
    {
        var result = await _documentService.GetDocumentsAsync(companyId, type, new PagedRequest(page, pageSize, search), projectId);
        return Ok(new ApiResponse<PagedResponse<DocumentResponse>>(true, result));
    }

    [HttpGet("{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> GetDocument(Guid companyId, Guid documentId)
    {
        var result = await _documentService.GetDocumentAsync(companyId, documentId);
        return Ok(new ApiResponse<DocumentResponse>(true, result));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> CreateDocument(Guid companyId, [FromBody] CreateDocumentRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _documentService.CreateDocumentAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<DocumentResponse>(true, result, "สร้างเอกสารสำเร็จ"));
    }

    [HttpPut("{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> UpdateDocument(Guid companyId, Guid documentId, [FromBody] UpdateDocumentRequest request)
    {
        var result = await _documentService.UpdateDocumentAsync(companyId, documentId, request);
        return Ok(new ApiResponse<DocumentResponse>(true, result));
    }

    [HttpPost("{documentId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> ApproveDocument(Guid companyId, Guid documentId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _documentService.ApproveDocumentAsync(companyId, documentId, userId);
        return Ok(new ApiResponse<DocumentResponse>(true, result, "อนุมัติเอกสารสำเร็จ"));
    }

    [HttpPost("{documentId:guid}/void")]
    public async Task<ActionResult<ApiResponse<string>>> VoidDocument(Guid companyId, Guid documentId)
    {
        await _documentService.VoidDocumentAsync(companyId, documentId);
        return Ok(new ApiResponse<string>(true, null, "ยกเลิกเอกสารสำเร็จ"));
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

    [HttpPost("{documentId:guid}/convert/{targetType}")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> ConvertDocument(Guid companyId, Guid documentId, DocumentType targetType)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _documentService.ConvertDocumentAsync(companyId, documentId, targetType, userId);
        return Ok(new ApiResponse<DocumentResponse>(true, result, "แปลงเอกสารสำเร็จ"));
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
    public async Task<ActionResult<ApiResponse<List<ContactResponse>>>> GetContacts(
        Guid companyId, [FromQuery] bool? isCustomer = null, [FromQuery] bool? isSupplier = null)
    {
        var result = await _documentService.GetContactsAsync(companyId, isCustomer, isSupplier);
        return Ok(new ApiResponse<List<ContactResponse>>(true, result));
    }

    [HttpPost("contacts")]
    public async Task<ActionResult<ApiResponse<ContactResponse>>> CreateContact(Guid companyId, [FromBody] CreateContactRequest request)
    {
        var result = await _documentService.CreateContactAsync(companyId, request);
        return StatusCode(201, new ApiResponse<ContactResponse>(true, result, "สร้างผู้ติดต่อสำเร็จ"));
    }

    [HttpPut("contacts/{contactId:guid}")]
    public async Task<ActionResult<ApiResponse<ContactResponse>>> UpdateContact(Guid companyId, Guid contactId, [FromBody] UpdateContactRequest request)
    {
        var result = await _documentService.UpdateContactAsync(companyId, contactId, request);
        return Ok(new ApiResponse<ContactResponse>(true, result));
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
        var result = await _documentService.CreatePaymentAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<PaymentResponse>(true, result, "บันทึกการชำระเงินสำเร็จ"));
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
}
