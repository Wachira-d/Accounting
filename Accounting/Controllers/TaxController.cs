using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class TaxController : ControllerBase
{
    private readonly ITaxService _taxService;

    public TaxController(ITaxService taxService)
    {
        _taxService = taxService;
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

    [HttpDelete("{reportId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> DeleteTaxReport(Guid companyId, Guid reportId)
    {
        await _taxService.DeleteTaxReportAsync(companyId, reportId);
        return Ok(new ApiResponse<string>(true, "ลบรายงานภาษีสำเร็จ"));
    }

    [HttpGet("vat-debug")]
    public async Task<ActionResult<ApiResponse<object>>> GetVatDebug(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var result = await _taxService.GetVatDebugAsync(companyId, year, month);
        return Ok(new ApiResponse<object>(true, result));
    }
}
