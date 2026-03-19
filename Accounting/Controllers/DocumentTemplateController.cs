using Accounting.Models.DTOs;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/document-templates")]
[Authorize]
public class DocumentTemplateController : ControllerBase
{
    private readonly IDocumentTemplateService _templateService;
    private readonly IPdfGenerationService _pdfService;

    public DocumentTemplateController(IDocumentTemplateService templateService, IPdfGenerationService pdfService)
    {
        _templateService = templateService;
        _pdfService = pdfService;
    }

    // ===== Template CRUD =====

    [HttpPost]
    public async Task<ActionResult<ApiResponse<DocumentTemplateResponse>>> Create(
        Guid companyId, [FromBody] CreateDocumentTemplateRequest request)
    {
        var result = await _templateService.CreateAsync(companyId, request);
        return Ok(new ApiResponse<DocumentTemplateResponse>(true, result, "สร้างเทมเพลตสำเร็จ"));
    }

    [HttpGet("{templateId:guid}")]
    public async Task<ActionResult<ApiResponse<DocumentTemplateResponse>>> GetById(Guid companyId, Guid templateId)
    {
        var result = await _templateService.GetByIdAsync(companyId, templateId);
        return Ok(new ApiResponse<DocumentTemplateResponse>(true, result));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<DocumentTemplateListResponse>>>> GetAll(
        Guid companyId, [FromQuery] DocumentType? documentType)
    {
        var result = await _templateService.GetAllAsync(companyId, documentType);
        return Ok(new ApiResponse<List<DocumentTemplateListResponse>>(true, result));
    }

    [HttpPut("{templateId:guid}")]
    public async Task<ActionResult<ApiResponse<DocumentTemplateResponse>>> Update(
        Guid companyId, Guid templateId, [FromBody] UpdateDocumentTemplateRequest request)
    {
        var result = await _templateService.UpdateAsync(companyId, templateId, request);
        return Ok(new ApiResponse<DocumentTemplateResponse>(true, result));
    }

    [HttpDelete("{templateId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid companyId, Guid templateId)
    {
        await _templateService.DeleteAsync(companyId, templateId);
        return Ok(new ApiResponse<bool>(true, true, "ลบเทมเพลตสำเร็จ"));
    }

    [HttpGet("default/{documentType}")]
    public async Task<ActionResult<ApiResponse<DocumentTemplateResponse>>> GetDefault(Guid companyId, DocumentType documentType)
    {
        var result = await _templateService.GetDefaultTemplateAsync(companyId, documentType);
        return Ok(new ApiResponse<DocumentTemplateResponse>(true, result));
    }

    [HttpPost("{templateId:guid}/set-default")]
    public async Task<ActionResult<ApiResponse<bool>>> SetDefault(Guid companyId, Guid templateId)
    {
        await _templateService.SetDefaultAsync(companyId, templateId);
        return Ok(new ApiResponse<bool>(true, true, "ตั้งเป็นค่าเริ่มต้นสำเร็จ"));
    }

    [HttpPost("{templateId:guid}/duplicate")]
    public async Task<ActionResult<ApiResponse<DocumentTemplateResponse>>> Duplicate(
        Guid companyId, Guid templateId, [FromQuery] string newName)
    {
        var result = await _templateService.DuplicateAsync(companyId, templateId, newName);
        return Ok(new ApiResponse<DocumentTemplateResponse>(true, result, "คัดลอกเทมเพลตสำเร็จ"));
    }

    // ===== PDF Generation =====

    [HttpPost("generate-pdf")]
    public async Task<ActionResult> GeneratePdf(Guid companyId, [FromBody] GeneratePdfRequest request)
    {
        var result = await _pdfService.GenerateDocumentPdfAsync(companyId, request);
        return File(result.PdfData, result.ContentType, result.FileName);
    }

    [HttpGet("preview")]
    public async Task<ActionResult> Preview(Guid companyId, [FromQuery] Guid? templateId, [FromQuery] string? language)
    {
        var pdfBytes = await _pdfService.GeneratePreviewPdfAsync(companyId, new PdfPreviewRequest(templateId, language));
        return File(pdfBytes, "application/pdf", "preview.pdf");
    }

    [HttpPost("withholding-tax/{certId:guid}/pdf")]
    public async Task<ActionResult> GenerateWhtPdf(Guid companyId, Guid certId)
    {
        var result = await _pdfService.GenerateWithholdingTaxCertPdfAsync(companyId, certId);
        return File(result.PdfData, result.ContentType, result.FileName);
    }

    [HttpPost("receipt/{paymentId:guid}/pdf")]
    public async Task<ActionResult> GenerateReceiptPdf(Guid companyId, Guid paymentId)
    {
        var result = await _pdfService.GenerateReceiptPdfAsync(companyId, paymentId);
        return File(result.PdfData, result.ContentType, result.FileName);
    }
}
