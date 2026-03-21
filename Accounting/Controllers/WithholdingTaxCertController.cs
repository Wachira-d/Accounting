using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/withholding-tax-certs")]
[Authorize]
public class WithholdingTaxCertController : ControllerBase
{
    private readonly IWithholdingTaxCertService _whtService;

    public WithholdingTaxCertController(IWithholdingTaxCertService whtService)
    {
        _whtService = whtService;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<WithholdingTaxCertResponse>>> Create(
        Guid companyId, [FromBody] CreateWithholdingTaxCertRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _whtService.CreateAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<WithholdingTaxCertResponse>(true, result, "สร้างหนังสือรับรองหัก ณ ที่จ่ายสำเร็จ"));
    }

    [HttpGet("{certId:guid}")]
    public async Task<ActionResult<ApiResponse<WithholdingTaxCertResponse>>> GetById(Guid companyId, Guid certId)
    {
        var result = await _whtService.GetByIdAsync(companyId, certId);
        return Ok(new ApiResponse<WithholdingTaxCertResponse>(true, result));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<WithholdingTaxCertResponse>>>> GetAll(
        Guid companyId,
        [FromQuery] TaxType? taxFormType, [FromQuery] int? year, [FromQuery] int? month,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _whtService.GetAllAsync(companyId, taxFormType, year, month, new PagedRequest(page, pageSize));
        return Ok(new ApiResponse<PagedResponse<WithholdingTaxCertResponse>>(true, result));
    }

    [HttpPost("{certId:guid}/issue")]
    public async Task<ActionResult<ApiResponse<WithholdingTaxCertResponse>>> Issue(Guid companyId, Guid certId)
    {
        var result = await _whtService.IssueAsync(companyId, certId);
        return Ok(new ApiResponse<WithholdingTaxCertResponse>(true, result, "ออกหนังสือรับรองสำเร็จ"));
    }

    [HttpPost("{certId:guid}/void")]
    public async Task<ActionResult<ApiResponse<bool>>> Void(Guid companyId, Guid certId)
    {
        await _whtService.VoidAsync(companyId, certId);
        return Ok(new ApiResponse<bool>(true, true, "ยกเลิกสำเร็จ"));
    }

    [HttpGet("contacts/{contactId:guid}")]
    public async Task<ActionResult<ApiResponse<List<WithholdingTaxCertResponse>>>> GetByContact(
        Guid companyId, Guid contactId, [FromQuery] int? year)
    {
        var result = await _whtService.GetByContactAsync(companyId, contactId, year);
        return Ok(new ApiResponse<List<WithholdingTaxCertResponse>>(true, result));
    }
}
