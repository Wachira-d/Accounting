using Accounting.Filters;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    // รอบ 200 ทีม RF (R200-X1): เส้นเขียนเทมเพลตทุกเส้น (สร้าง/แก้/ลบ/ตั้งค่าเริ่มต้น/คัดลอก) ต้องมีสิทธิ์ตั้งค่าบริษัท
    // (CompanySettings.Edit — คำอธิบายคีย์ครอบ "template" · DECISIONS ข้อ 34: เจ้าของ + Admin โดยปริยาย) · เดิมมีแค่ [Authorize]
    // ⇒ ผู้ดูอย่างเดียวแก้เทมเพลต default ของบริษัทได้ (หัวเอกสาร/CSS ที่ไปโผล่ในพรีวิวของเจ้าของ)

    /// <summary>ตัวตรวจค่าหน้าตาตอนบันทึก — ค่าที่ไม่ถูกรูป = 400 พร้อมข้อความไทย (Helpers/DocumentTemplateStyle ตัวเดียวกับตอน render)</summary>
    private static string? StyleRejection(Accounting.Helpers.TemplateStyleInput input)
    {
        var errs = Accounting.Helpers.DocumentTemplateStyle.RejectReasons(input);
        return errs.Count == 0 ? null : "บันทึกเทมเพลตไม่ได้: " + string.Join(" · ", errs);
    }

    [HttpPost]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    public async Task<ActionResult<ApiResponse<DocumentTemplateResponse>>> Create(
        Guid companyId, [FromBody] CreateDocumentTemplateRequest request)
    {
        if (StyleRejection(new Accounting.Helpers.TemplateStyleInput(
                request.PaperSize, request.Orientation, request.FontFamily, request.BodyFontSize, request.TitleFontSize,
                request.PrimaryColor, request.AccentColor, request.TableHeaderColor, request.TableHeaderTextColor,
                request.HeaderBackgroundColor, request.HeaderTextColor, request.TableStripedColor)) is { } bad)
            return BadRequest(new ApiResponse<DocumentTemplateResponse>(false, null, bad));
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
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    public async Task<ActionResult<ApiResponse<DocumentTemplateResponse>>> Update(
        Guid companyId, Guid templateId, [FromBody] UpdateDocumentTemplateRequest request)
    {
        if (StyleRejection(new Accounting.Helpers.TemplateStyleInput(
                request.PaperSize, request.Orientation, request.FontFamily, request.BodyFontSize, request.TitleFontSize,
                request.PrimaryColor, request.AccentColor, request.TableHeaderColor, request.TableHeaderTextColor,
                request.HeaderBackgroundColor, request.HeaderTextColor, request.TableStripedColor)) is { } bad)
            return BadRequest(new ApiResponse<DocumentTemplateResponse>(false, null, bad));
        var result = await _templateService.UpdateAsync(companyId, templateId, request);
        return Ok(new ApiResponse<DocumentTemplateResponse>(true, result));
    }

    [HttpDelete("{templateId:guid}")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
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
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    public async Task<ActionResult<ApiResponse<bool>>> SetDefault(Guid companyId, Guid templateId)
    {
        await _templateService.SetDefaultAsync(companyId, templateId);
        return Ok(new ApiResponse<bool>(true, true, "ตั้งเป็นค่าเริ่มต้นสำเร็จ"));
    }

    [HttpPost("{templateId:guid}/duplicate")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    public async Task<ActionResult<ApiResponse<DocumentTemplateResponse>>> Duplicate(
        Guid companyId, Guid templateId, [FromQuery] string newName)
    {
        var result = await _templateService.DuplicateAsync(companyId, templateId, newName);
        return Ok(new ApiResponse<DocumentTemplateResponse>(true, result, "คัดลอกเทมเพลตสำเร็จ"));
    }

    // ===== PDF Generation =====

    /// <summary>รอบ 200 ทีม R (review193-r4 P4-7 · W2-P6 ส่วนที่เหลือ): ด่านชั้นความลับของเอกสารใบเดียว — ด่านเดียวกับหน้าเอกสาร/อีเมล
    /// (<c>ISensitivityService.CanViewAsync</c> + ข้อความ <c>Helpers/SensitivityAccess</c>) · เดิม generate-pdf/html ส่งเนื้อหาใบลับ
    /// ให้สมาชิกทุกคนที่รู้ documentId · ไม่มี ISensitivityService/DbContext ใน DI = ผ่าน (พฤติกรรมเดียวกับ DocumentController)</summary>
    private async Task<string?> DenySensitiveAsync(Guid companyId, Guid documentId)
    {
        if (HttpContext.RequestServices.GetService(typeof(Accounting.Data.AccountingDbContext)) is not Accounting.Data.AccountingDbContext db)
            return null;
        var kind = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => d.Sensitivity).FirstOrDefaultAsync();
        if (!Accounting.Helpers.SensitivityAccess.NeedsCheck(kind)) return null;
        if (HttpContext.RequestServices.GetService(typeof(ISensitivityService)) is not ISensitivityService svc) return null;
        return await svc.CanViewAsync(companyId, Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User), kind)
            ? null
            : Accounting.Helpers.SensitivityAccess.DeniedMessage(kind, "พิมพ์/ดาวน์โหลด");
    }

    [HttpPost("generate-pdf")]
    public async Task<ActionResult> GeneratePdf(Guid companyId, [FromBody] GeneratePdfRequest request)
    {
        if (await DenySensitiveAsync(companyId, request.DocumentId) is { } denied)
            return StatusCode(403, new ApiResponse<object>(false, null, denied));
        var result = await _pdfService.GenerateDocumentPdfAsync(companyId, request);
        return File(result.PdfData, result.ContentType, result.FileName);
    }

    /// <summary>Same template + layout the PDF would use, but
    /// returned as raw HTML. The print modal embeds this so
    /// Ctrl-P print output matches the downloaded PDF exactly.</summary>
    [HttpPost("generate-html")]
    public async Task<ActionResult<string>> GenerateHtml(Guid companyId, [FromBody] GeneratePdfRequest request)
    {
        if (await DenySensitiveAsync(companyId, request.DocumentId) is { } denied)
            return StatusCode(403, new ApiResponse<object>(false, null, denied));
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
