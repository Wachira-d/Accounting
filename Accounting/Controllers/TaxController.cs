using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class TaxController : ControllerBase
{
    private readonly ITaxService _taxService;
    private readonly IPdfGenerationService _pdfService;

    public TaxController(ITaxService taxService, IPdfGenerationService pdfService)
    {
        _taxService = taxService;
        _pdfService = pdfService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<TaxReportResponse>>>> GetTaxReports(
        Guid companyId, [FromQuery] TaxType? taxType = null, [FromQuery] int? year = null)
    {
        var result = await _taxService.GetTaxReportsAsync(companyId, taxType, year);
        return Ok(new ApiResponse<List<TaxReportResponse>>(true, result));
    }

    [HttpGet("{reportId:guid}")]
    public async Task<ActionResult<ApiResponse<TaxReportResponse>>> GetTaxReport(Guid companyId, Guid reportId)
    {
        var result = await _taxService.GetTaxReportAsync(companyId, reportId);
        return Ok(new ApiResponse<TaxReportResponse>(true, result));
    }

    [HttpPost("generate")]
    public async Task<ActionResult<ApiResponse<TaxReportResponse>>> GenerateTaxReport(Guid companyId, [FromBody] CreateTaxReportRequest request)
    {
        var result = await _taxService.GenerateTaxReportAsync(companyId, request);
        return Ok(new ApiResponse<TaxReportResponse>(true, result, "สร้างรายงานภาษีสำเร็จ"));
    }

    [HttpPost("{reportId:guid}/file")]
    public async Task<ActionResult<ApiResponse<TaxReportResponse>>> FileTaxReport(Guid companyId, Guid reportId)
    {
        var result = await _taxService.FileTaxReportAsync(companyId, reportId);
        return Ok(new ApiResponse<TaxReportResponse>(true, result, "ยื่นรายงานภาษีสำเร็จ"));
    }

    [HttpPut("{reportId:guid}")]
    public async Task<ActionResult<ApiResponse<TaxReportResponse>>> UpdateTaxReport(Guid companyId, Guid reportId, [FromBody] UpdateTaxReportRequest request)
    {
        var result = await _taxService.UpdateTaxReportAsync(companyId, reportId, request);
        return Ok(new ApiResponse<TaxReportResponse>(true, result, "แก้ไขรายงานภาษีสำเร็จ"));
    }

    [HttpPost("{reportId:guid}/regenerate")]
    public async Task<ActionResult<ApiResponse<TaxReportResponse>>> RegenerateTaxReport(Guid companyId, Guid reportId)
    {
        var result = await _taxService.RegenerateTaxReportAsync(companyId, reportId);
        return Ok(new ApiResponse<TaxReportResponse>(true, result, "สร้างรายงานภาษีใหม่สำเร็จ"));
    }

    /// <summary>เอกสารงวดอื่นที่มี VAT และยังไม่ถูกใช้ในรายงานใด — สำหรับเลือกดึงเข้างวดนี้</summary>
    [HttpGet("{reportId:guid}/pullable-documents")]
    public async Task<ActionResult<ApiResponse<List<PullableDocumentDto>>>> GetPullableDocuments(
        Guid companyId, Guid reportId,
        [FromQuery] string? search = null, [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null)
    {
        var result = await _taxService.GetPullableDocumentsAsync(companyId, reportId, search, fromDate, toDate);
        return Ok(new ApiResponse<List<PullableDocumentDto>>(true, result));
    }

    /// <summary>ดึงเอกสารเก่าเข้ารายงานภาษีงวดนี้เป็นบรรทัดใหม่</summary>
    [HttpPost("{reportId:guid}/pull-document")]
    public async Task<ActionResult<ApiResponse<TaxReportResponse>>> PullDocument(
        Guid companyId, Guid reportId, [FromBody] PullDocumentRequest request)
    {
        var result = await _taxService.PullDocumentIntoReportAsync(companyId, reportId, request.DocumentId);
        return Ok(new ApiResponse<TaxReportResponse>(true, result, "ดึงเอกสารเข้ารายงานสำเร็จ"));
    }

    /// <summary>ส่งออกรายงานภาษีเป็นไฟล์ Excel (.xlsx)</summary>
    [HttpGet("{reportId:guid}/export-xlsx")]
    public async Task<IActionResult> ExportTaxReportXlsx(Guid companyId, Guid reportId)
    {
        var (content, fileName) = await _taxService.ExportTaxReportXlsxAsync(companyId, reportId);
        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    /// <summary>PDF รายงานภาษี — kind: purchase (ภาษีซื้อ) · sales (ภาษีขาย) ·
    /// pp30 (แบบสรุป ภ.พ.30). แสดง inline (download=false) หรือแนบไฟล์.</summary>
    [HttpGet("{reportId:guid}/export-pdf")]
    public async Task<IActionResult> ExportTaxReportPdf(Guid companyId, Guid reportId,
        [FromQuery] string kind = "purchase", [FromQuery] bool download = false)
    {
        var k = kind?.ToLowerInvariant() switch { "sales" => "sales", "pp30" => "pp30", _ => "purchase" };
        var report = await _taxService.GetTaxReportAsync(companyId, reportId);
        var pdf = await _pdfService.GenerateVatReportPdfAsync(companyId, report, k);
        var label = k switch { "sales" => "รายงานภาษีขาย", "pp30" => "แบบภพ30", _ => "รายงานภาษีซื้อ" };
        var fileName = $"{label}_{report.Month:D2}-{report.Year + 543}.pdf";
        if (download) return File(pdf, "application/pdf", fileName);
        Response.Headers["Content-Disposition"] = "inline";
        return File(pdf, "application/pdf");
    }

    [HttpDelete("{reportId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> DeleteTaxReport(Guid companyId, Guid reportId)
    {
        await _taxService.DeleteTaxReportAsync(companyId, reportId);
        return Ok(new ApiResponse<string>(true, "ลบรายงานภาษีสำเร็จ"));
    }

    [HttpPost("auto-refresh")]
    public async Task<ActionResult<ApiResponse<object>>> AutoRefresh(Guid companyId, [FromQuery] int months = 2)
    {
        var count = await _taxService.AutoRefreshReportsAsync(companyId, months);
        return Ok(new ApiResponse<object>(true, new { refreshed = count }));
    }

    [HttpGet("vat-debug")]
    public async Task<ActionResult<ApiResponse<object>>> GetVatDebug(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var result = await _taxService.GetVatDebugAsync(companyId, year, month);
        return Ok(new ApiResponse<object>(true, result));
    }

    // ===== Task 4 ERP Upgrade — Defer VAT / Unlock / Reject & Reverse =====

    public record DeferInputVatRequest(Guid DocumentId, int DeferredToPeriod, string? Reason);

    [HttpPost("defer-input-vat")]
    public async Task<ActionResult<ApiResponse<object>>> DeferInputVat(
        Guid companyId, [FromBody] DeferInputVatRequest request)
    {
        var userId = Helpers.JwtHelper.GetUserIdFromClaims(User).ToString();
        var d = await _taxService.DeferInputVatAsync(companyId, request.DocumentId, request.DeferredToPeriod, request.Reason, userId);
        return Ok(new ApiResponse<object>(true,
            new { d.Id, d.DocumentId, d.DeferredFromPeriod, d.DeferredToPeriod, d.DeferredAmount },
            $"เลื่อน Input VAT {d.DeferredAmount:N2} บาท ไปงวด {d.DeferredToPeriod}"));
    }

    public record UnlockTaxFilingRequest(string Reason);

    [HttpPost("{reportId:guid}/unlock-filing")]
    public async Task<ActionResult<ApiResponse<string>>> UnlockTaxFiling(
        Guid companyId, Guid reportId, [FromBody] UnlockTaxFilingRequest request)
    {
        var userId = Helpers.JwtHelper.GetUserIdFromClaims(User).ToString();
        await _taxService.UnlockTaxFilingAsync(companyId, reportId, userId, request.Reason);
        return Ok(new ApiResponse<string>(true, null, "ปลดล็อกการยื่นภาษีสำเร็จ"));
    }

    public record RejectTaxReportRequest(string Reason, Guid? NonClaimableVatAccountId);

    /// <summary>
    /// Cancel a Filed report — generates an autonomous reversal JE that
    /// moves the previously-claimed Input VAT into a non-claimable VAT
    /// expense account (per Thai RD practice on rejected refund claims).
    /// </summary>
    [HttpPost("{reportId:guid}/reject-reverse")]
    public async Task<ActionResult<ApiResponse<TaxReportResponse>>> RejectAndReverse(
        Guid companyId, Guid reportId, [FromBody] RejectTaxReportRequest request)
    {
        var userId = Helpers.JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _taxService.RejectAndReverseTaxReportAsync(companyId, reportId, request.Reason, request.NonClaimableVatAccountId, userId);
        return Ok(new ApiResponse<TaxReportResponse>(true, result, "Reject + Reverse สำเร็จ — สร้าง reversal JE แล้ว"));
    }

    // ===== Task 4 ERP Upgrade — RD pipe-delimited e-Filing export =====

    /// <summary>
    /// Generate a Thai RD e-Filing pipe-delimited file for the given form
    /// + period. Returns the text content as `text/plain` attachment so the
    /// browser saves it directly. Also persists an EFilingExport audit row.
    /// </summary>
    [HttpPost("e-filing/{formType}")]
    public async Task<IActionResult> GenerateEFiling(
        Guid companyId, string formType, [FromQuery] int year, [FromQuery] int month)
    {
        var userId = Helpers.JwtHelper.GetUserIdFromClaims(User).ToString();
        var export = await _taxService.GenerateEFilingAsync(companyId, formType, year, month, userId);
        var fileName = $"{formType}_{year}{month:D2}.txt";
        var bytes = System.Text.Encoding.UTF8.GetBytes(export.FileContent);
        return File(bytes, "text/plain; charset=utf-8", fileName);
    }

    public sealed record RecordAckRequest(string RdAckNumber, DateTime RdAcknowledgedAt,
        string? Status, string? RejectionReason, string? AckDocumentUrl);

    /// <summary>Record the RD e-Filing acknowledgement after admin
    /// uploads the report via the RD portal. The RD returns an ACK
    /// number + timestamp (or a rejection); persisting these lets
    /// the admin dashboard show "Filed + accepted" vs "Filed but ACK
    /// pending" + the legal "received by RD" date for deadline
    /// compliance.</summary>
    [HttpPost("{reportId:guid}/rd-ack")]
    public async Task<ActionResult<ApiResponse<object>>> RecordAck(
        Guid companyId, Guid reportId, [FromBody] RecordAckRequest req,
        [FromServices] Data.AccountingDbContext db,
        CancellationToken ct)
    {
        var report = await db.TaxReports.FirstOrDefaultAsync(
            r => r.Id == reportId && r.CompanyId == companyId, ct);
        if (report == null) return NotFound(new ApiResponse<object>(false, null, "TaxReport not found"));
        report.RdAckNumber = req.RdAckNumber;
        report.RdAcknowledgedAt = req.RdAcknowledgedAt;
        report.RdSubmissionStatus = req.Status ?? "Accepted";
        report.RdRejectionReason = req.RejectionReason;
        report.RdAcknowledgementDocumentUrl = req.AckDocumentUrl;
        await db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, new
        {
            reportId, ackNumber = req.RdAckNumber,
            acknowledgedAt = req.RdAcknowledgedAt,
            status = report.RdSubmissionStatus,
        }));
    }

    /// <summary>Local rule-based pre-check before e-Filing submission.
    /// Returns mechanical errors (RD-rejected fields), warnings
    /// (likely-wrong values), and infos (observations). The UI uses
    /// CanSubmit=false to block the "ยื่นแบบ" button until errors
    /// are resolved. Cost: zero — pure local rules.</summary>
    [HttpGet("{reportId:guid}/precheck")]
    public async Task<ActionResult<ApiResponse<Services.Implementations.Tax.TaxComplianceReport>>> PreCheck(
        Guid companyId, Guid reportId,
        [FromServices] Services.Implementations.Tax.ITaxComplianceChecker checker,
        CancellationToken ct)
    {
        var report = await checker.CheckAsync(reportId, ct);
        return Ok(new ApiResponse<Services.Implementations.Tax.TaxComplianceReport>(true, report));
    }
}
