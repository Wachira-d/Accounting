using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/tax-filing-export")]
[Authorize]
public class TaxFilingExportController : ControllerBase
{
    private readonly ITaxFilingExportService _exportService;
    private readonly ISensitivityService _sensitivity;

    public TaxFilingExportController(ITaxFilingExportService exportService, ISensitivityService sensitivity)
    {
        _exportService = exportService;
        _sensitivity = sensitivity;
    }

    private async Task<ActionResult?> CheckPayrollAsync(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _sensitivity.CanViewAsync(companyId, userId, SensitivityKind.Payroll))
            return StatusCode(403, new ApiResponse<object>(false, new
            {
                redacted = true, kind = "Payroll",
                requiredPermission = Models.Constants.PermissionKeys.PayrollView
            }, "ภ.ง.ด.1 มีข้อมูลเงินเดือนรายคน — ต้องมีสิทธิ์ Payroll.View"));
        return null;
    }

    /// <summary>Export ภ.ง.ด.1 (Monthly salary WHT) for e-Filing — sensitive (payroll).</summary>
    [HttpGet("pnd1")]
    public async Task<IActionResult> ExportPnd1(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportPnd1Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export ภ.ง.ด.3 (Service WHT individuals) for e-Filing</summary>
    [HttpGet("pnd3")]
    public async Task<IActionResult> ExportPnd3(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var result = await _exportService.ExportPnd3Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export ภ.ง.ด.53 (Service WHT companies) for e-Filing</summary>
    [HttpGet("pnd53")]
    public async Task<IActionResult> ExportPnd53(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var result = await _exportService.ExportPnd53Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export ภ.ง.ด.1ก (Annual salary summary) — sensitive (payroll).</summary>
    [HttpGet("pnd1k")]
    public async Task<IActionResult> ExportPnd1k(Guid companyId, [FromQuery] int year)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportPnd1kAsync(companyId, year);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export ภ.พ.30 (VAT filing report)</summary>
    [HttpGet("pp30")]
    public async Task<IActionResult> ExportPp30(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var result = await _exportService.ExportPp30Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export สปส.1-10 (SSO monthly contribution) — sensitive (payroll).</summary>
    [HttpGet("sso110")]
    public async Task<IActionResult> ExportSso110(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportSso110Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Get export summary (preview) without downloading</summary>
    [HttpGet("preview/{formCode}")]
    public async Task<ActionResult<ApiResponse<TaxFilingExportResult>>> Preview(
        Guid companyId, string formCode, [FromQuery] int year, [FromQuery] int month = 0)
    {
        var code = formCode.ToUpper();
        // Block preview of payroll-derived forms behind the same Payroll gate.
        if (code is "PND1" or "PND1K" or "SSO110")
        {
            var block = await CheckPayrollAsync(companyId); if (block != null) return (ActionResult<ApiResponse<TaxFilingExportResult>>)block;
        }

        var result = code switch
        {
            "PND1" => await _exportService.ExportPnd1Async(companyId, year, month),
            "PND3" => await _exportService.ExportPnd3Async(companyId, year, month),
            "PND53" => await _exportService.ExportPnd53Async(companyId, year, month),
            "PND1K" => await _exportService.ExportPnd1kAsync(companyId, year),
            "PP30" => await _exportService.ExportPp30Async(companyId, year, month),
            "SSO110" => await _exportService.ExportSso110Async(companyId, year, month),
            _ => throw new ArgumentException($"ไม่รู้จักรหัสแบบฟอร์ม: {formCode}")
        };

        // Return preview without file data
        return Ok(new ApiResponse<TaxFilingExportResult>(true, result with { FileData = Array.Empty<byte>() }));
    }
}
