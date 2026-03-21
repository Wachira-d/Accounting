using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class FixedAssetController : ControllerBase
{
    private readonly IFixedAssetService _assetService;

    public FixedAssetController(IFixedAssetService assetService)
    {
        _assetService = assetService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<FixedAssetResponse>>>> GetAll(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null)
    {
        var result = await _assetService.GetAllAsync(companyId, new PagedRequest(page, pageSize, search));
        return Ok(new ApiResponse<PagedResponse<FixedAssetResponse>>(true, result));
    }

    [HttpGet("{assetId:guid}")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> GetById(Guid companyId, Guid assetId)
    {
        var result = await _assetService.GetByIdAsync(companyId, assetId);
        return Ok(new ApiResponse<FixedAssetResponse>(true, result));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> Create(
        Guid companyId, [FromBody] CreateFixedAssetRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.CreateAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<FixedAssetResponse>(true, result, "สร้างสินทรัพย์ถาวรสำเร็จ"));
    }

    [HttpPut("{assetId:guid}")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> Update(
        Guid companyId, Guid assetId, [FromBody] UpdateFixedAssetRequest request)
    {
        var result = await _assetService.UpdateAsync(companyId, assetId, request);
        return Ok(new ApiResponse<FixedAssetResponse>(true, result));
    }

    [HttpPost("{assetId:guid}/dispose")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> Dispose(
        Guid companyId, Guid assetId, [FromBody] DisposeAssetRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.DisposeAsync(companyId, assetId, request, userId);
        return Ok(new ApiResponse<FixedAssetResponse>(true, result, "จำหน่ายสินทรัพย์สำเร็จ"));
    }

    [HttpGet("{assetId:guid}/depreciations")]
    public async Task<ActionResult<ApiResponse<List<DepreciationResponse>>>> GetDepreciations(Guid companyId, Guid assetId)
    {
        var result = await _assetService.GetDepreciationsAsync(companyId, assetId);
        return Ok(new ApiResponse<List<DepreciationResponse>>(true, result));
    }

    [HttpPost("depreciate")]
    public async Task<ActionResult<ApiResponse<List<DepreciationResponse>>>> CalculateDepreciation(
        Guid companyId, [FromBody] CalculateDepreciationRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.CalculateDepreciationAsync(companyId, request, userId);
        return Ok(new ApiResponse<List<DepreciationResponse>>(true, result, $"คำนวณค่าเสื่อมราคาสำเร็จ {result.Count} รายการ"));
    }

    [HttpPost("{assetId:guid}/revalue")]
    public async Task<ActionResult<ApiResponse<RevaluationResponse>>> Revalue(
        Guid companyId, Guid assetId, [FromBody] RevalueAssetRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.RevalueAsync(companyId, assetId, request, userId);
        return Ok(new ApiResponse<RevaluationResponse>(true, result, "ตีราคาสินทรัพย์ใหม่สำเร็จ"));
    }
}
