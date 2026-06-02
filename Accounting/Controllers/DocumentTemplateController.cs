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
        return StatusCode(201, new ApiResponse<DocumentTemplateResponse>(true, result, "สร้างเทมเพลตสำเร็จ"));
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
        return NoContent();
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

    /// <summary>Same template + layout the PDF would use, but
    /// returned as raw HTML. The print modal embeds this so
    /// Ctrl-P print output matches the downloaded PDF exactly.</summary>
    [HttpPost("generate-html")]
    public async Task<ActionResult<string>> GenerateHtml(Guid companyId, [FromBody] GeneratePdfRequest request)
    {
        var html = await _pdfService.GenerateDocumentHtmlAsync(companyId, request);
        return Content(html, "text/html; charset=utf-8");
    }

    [HttpGet("preview")]
    public async Task<ActionResult> Preview(Guid companyId, [FromQuery] Guid? templateId, [FromQuery] string? language)
    {
        var pdfBytes = await _pdfService.GeneratePreviewPdfAsync(companyId, new PdfPreviewRequest(templateId, language));
        return File(pdfBytes, "application/pdf", "preview.pdf");
    }

    /// <summary>Template-preview HTML (sample data) for a template or a doc
    /// type — used for the gallery thumbnails. Works without real documents.</summary>
    [HttpGet("preview-html")]
    public async Task<ActionResult> PreviewHtml(Guid companyId,
        [FromQuery] Guid? templateId, [FromQuery] string? documentType, [FromQuery] string? language)
    {
        try
        {
            var html = await _pdfService.GeneratePreviewHtmlAsync(companyId, templateId, documentType, language);
            return Content(html, "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            // Show the reason inside the preview frame instead of failing the
            // fetch (which would only render a generic "can't preview").
            return Content(PreviewError(ex), "text/html; charset=utf-8");
        }
    }

    private static string PreviewError(Exception ex) =>
        $"<!DOCTYPE html><html><head><meta charset='utf-8'></head><body style=\"font:13px 'Noto Sans Thai',sans-serif;color:#b91c1c;padding:14px;line-height:1.6\">⚠️ สร้างตัวอย่างไม่สำเร็จ<br><span style='color:#475569'>{System.Net.WebUtility.HtmlEncode(ex.Message)}</span></body></html>";

    /// <summary>Live preview from the editor's UNSAVED template form, so every
    /// tick/colour/layout change shows instantly. The body is a transient
    /// DocumentTemplate (never saved).</summary>
    [HttpPost("preview-html-draft")]
    public async Task<ActionResult> PreviewHtmlDraft(Guid companyId, [FromBody] CreateDocumentTemplateRequest draft)
    {
        // Map the request through the template service (same proven binding +
        // mapping as Create) into an unsaved template, then render it.
        try
        {
            var transient = _templateService.BuildTransient(companyId, draft);
            var html = await _pdfService.GeneratePreviewHtmlFromDraftAsync(companyId, transient);
            return Content(html, "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            return Content(PreviewError(ex), "text/html; charset=utf-8");
        }
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
