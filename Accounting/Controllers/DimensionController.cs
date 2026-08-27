using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Dimension;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/dimensions")]
[Authorize]
public class DimensionController : ControllerBase
{
    private readonly IDimensionalAccountingService _service;

    public DimensionController(IDimensionalAccountingService service) => _service = service;

    [HttpPost]
    public async Task<ActionResult<ApiResponse<DimensionResponse>>> Create(Guid companyId, [FromBody] CreateDimensionRequest request)
        => StatusCode(201, new ApiResponse<DimensionResponse>(true, await _service.CreateDimensionAsync(companyId, request)));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<DimensionResponse>>>> GetAll(Guid companyId, [FromQuery] DimensionType? type)
        => Ok(new ApiResponse<List<DimensionResponse>>(true, await _service.GetDimensionsAsync(companyId, type)));

    [HttpGet("{dimensionId:guid}")]
    public async Task<ActionResult<ApiResponse<DimensionResponse>>> GetById(Guid companyId, Guid dimensionId)
        => Ok(new ApiResponse<DimensionResponse>(true, await _service.GetDimensionAsync(companyId, dimensionId)));

    [HttpPut("{dimensionId:guid}")]
    public async Task<ActionResult<ApiResponse<DimensionResponse>>> Update(Guid companyId, Guid dimensionId, [FromBody] UpdateDimensionRequest request)
        => Ok(new ApiResponse<DimensionResponse>(true, await _service.UpdateDimensionAsync(companyId, dimensionId, request)));

    [HttpDelete("{dimensionId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid companyId, Guid dimensionId)
    { await _service.DeleteDimensionAsync(companyId, dimensionId); return NoContent(); }

    [HttpPost("journal-lines/{lineId:guid}/allocations")]
    public async Task<ActionResult<ApiResponse<bool>>> AssignDimensions(Guid companyId, Guid lineId, [FromBody] List<DimensionAllocationRequest> allocations)
    { await _service.AssignDimensionsAsync(companyId, lineId, allocations); return Ok(new ApiResponse<bool>(true, true)); }

    [HttpGet("journal-lines/{lineId:guid}/allocations")]
    public async Task<ActionResult<ApiResponse<List<DimensionAllocationResponse>>>> GetLineDimensions(Guid companyId, Guid lineId)
        => Ok(new ApiResponse<List<DimensionAllocationResponse>>(true, await _service.GetLineDimensionsAsync(companyId, lineId)));

    [HttpGet("{dimensionId:guid}/pnl")]
    public async Task<ActionResult<ApiResponse<DimensionPnLResponse>>> GetPnL(Guid companyId, Guid dimensionId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
        => Ok(new ApiResponse<DimensionPnLResponse>(true, await _service.GetDimensionPnLAsync(companyId, dimensionId, fromDate, toDate)));

    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<List<DimensionSummaryResponse>>>> GetSummary(Guid companyId, [FromQuery] DimensionType type, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
        => Ok(new ApiResponse<List<DimensionSummaryResponse>>(true, await _service.GetDimensionSummaryAsync(companyId, type, fromDate, toDate)));

    // Branches
    [HttpPost("branches")]
    public async Task<ActionResult<ApiResponse<BranchResponse>>> CreateBranch(Guid companyId, [FromBody] CreateBranchRequest request)
        => StatusCode(201, new ApiResponse<BranchResponse>(true, await _service.CreateBranchAsync(companyId, request)));

    /// <param name="includeInactive">หน้าตั้งค่าส่งมา true — ไม่งั้นสาขาที่ปิดใช้งานหายจากจอ เปิดกลับไม่ได้</param>
    [HttpGet("branches")]
    public async Task<ActionResult<ApiResponse<List<BranchResponse>>>> GetBranches(Guid companyId, [FromQuery] bool includeInactive = false)
        => Ok(new ApiResponse<List<BranchResponse>>(true, await _service.GetBranchesAsync(companyId, includeInactive)));

    [HttpPut("branches/{branchId:guid}")]
    public async Task<ActionResult<ApiResponse<BranchResponse>>> UpdateBranch(Guid companyId, Guid branchId, [FromBody] UpdateBranchRequest request)
        => Ok(new ApiResponse<BranchResponse>(true, await _service.UpdateBranchAsync(companyId, branchId, request)));

    [HttpDelete("branches/{branchId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteBranch(Guid companyId, Guid branchId)
    { await _service.DeleteBranchAsync(companyId, branchId); return NoContent(); }
}
