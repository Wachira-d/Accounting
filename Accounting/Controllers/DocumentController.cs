using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
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

    public DocumentController(IDocumentService documentService)
    {
        _documentService = documentService;
    }

    // ===== Documents =====

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<DocumentResponse>>>> GetDocuments(
        Guid companyId, [FromQuery] DocumentType? type = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null)
    {
        var result = await _documentService.GetDocumentsAsync(companyId, type, new PagedRequest(page, pageSize, search));
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

    [HttpPost("{documentId:guid}/convert/{targetType}")]
    public async Task<ActionResult<ApiResponse<DocumentResponse>>> ConvertDocument(Guid companyId, Guid documentId, DocumentType targetType)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _documentService.ConvertDocumentAsync(companyId, documentId, targetType, userId);
        return Ok(new ApiResponse<DocumentResponse>(true, result, "แปลงเอกสารสำเร็จ"));
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
}
