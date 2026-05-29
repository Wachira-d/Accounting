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

    public DocumentController(IDocumentService documentService, IDocumentEmailService docEmailService, AccountingDbContext db)
    {
        _documentService = documentService;
        _docEmailService = docEmailService;
        _db = db;
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
        [FromQuery] Guid? projectId = null, [FromQuery] Guid? contactId = null,
        [FromQuery] string? status = null, [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null,
        [FromQuery] Guid? relatedDocumentId = null, [FromQuery] Guid? revenueContractId = null,
        [FromQuery] bool staleOnly = false)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _documentService.GetDocumentsForUserAsync(companyId, userId, type, new PagedRequest(page, pageSize, search),
            projectId, contactId, status, fromDate, toDate, relatedDocumentId, revenueContractId, staleOnly);
        return Ok(new ApiResponse<PagedResponse<DocumentResponse>>(true, result));
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

    /// <summary>
    /// ลบเอกสารและข้อมูลเกี่ยวข้องทั้งหมด (journal, payment, WHT, eTax)
    /// ลบถาวร ไม่สามารถกู้คืนได้ — เหมือนไม่เคยสร้างมาเลย
    /// เฉพาะ Owner / SystemAdmin เท่านั้น + บันทึก Audit Log
    /// </summary>
    [HttpDelete("{documentId:guid}/purge")]
    public async Task<ActionResult<ApiResponse<string>>> PurgeDocument(Guid companyId, Guid documentId)
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

        await _documentService.PurgeDocumentAsync(companyId, documentId, userId);
        return Ok(new ApiResponse<string>(true, null, "ลบเอกสารและข้อมูลเกี่ยวข้องทั้งหมดสำเร็จ"));
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
