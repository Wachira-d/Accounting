using Accounting.Models.DTOs;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/etax")]
[Authorize]
public class EtaxController : ControllerBase
{
    private readonly IEtaxInvoiceService _etaxService;

    public EtaxController(IEtaxInvoiceService etaxService)
    {
        _etaxService = etaxService;
    }

    [HttpPost("generate")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> Generate(
        Guid companyId, [FromBody] GenerateEtaxRequest request)
    {
        var result = await _etaxService.GenerateAsync(companyId, request);
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, result, "สร้าง e-Tax Invoice สำเร็จ"));
    }

    [HttpGet("{etaxId:guid}")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> GetById(Guid companyId, Guid etaxId)
    {
        var result = await _etaxService.GetByIdAsync(companyId, etaxId);
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, result));
    }

    [HttpGet("document/{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> GetByDocument(Guid companyId, Guid documentId)
    {
        var result = await _etaxService.GetByDocumentIdAsync(companyId, documentId);
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, result));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<EtaxInvoiceResponse>>>> GetAll(
        Guid companyId, [FromQuery] EtaxStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _etaxService.GetAllAsync(companyId, status, new PagedRequest(page, pageSize));
        return Ok(new ApiResponse<PagedResponse<EtaxInvoiceResponse>>(true, result));
    }

    [HttpPost("{etaxId:guid}/sign")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> Sign(Guid companyId, Guid etaxId)
    {
        var result = await _etaxService.SignAsync(companyId, etaxId);
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, result, "ลงนามดิจิทัลสำเร็จ"));
    }

    [HttpPost("{etaxId:guid}/submit")]
    public async Task<ActionResult<ApiResponse<EtaxInvoiceResponse>>> Submit(Guid companyId, Guid etaxId)
    {
        var result = await _etaxService.SubmitToRevenueAsync(companyId, etaxId);
        return Ok(new ApiResponse<EtaxInvoiceResponse>(true, result, "ส่งกรมสรรพากรสำเร็จ"));
    }

    [HttpGet("{etaxId:guid}/xml")]
    public async Task<ActionResult> GetXml(Guid companyId, Guid etaxId)
    {
        var xml = await _etaxService.GetXmlAsync(companyId, etaxId);
        return Content(xml, "application/xml");
    }

    [HttpPost("{etaxId:guid}/void")]
    public async Task<ActionResult<ApiResponse<bool>>> Void(Guid companyId, Guid etaxId)
    {
        await _etaxService.VoidAsync(companyId, etaxId);
        return Ok(new ApiResponse<bool>(true, true, "ยกเลิกสำเร็จ"));
    }
}
