using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Organization;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/organization")]
[Authorize]
public class OrganizationController : ControllerBase
{
    private readonly IOrganizationService _service;

    public OrganizationController(IOrganizationService service) => _service = service;

    // ===== Departments =====

    [HttpGet("departments")]
    public async Task<ActionResult<ApiResponse<List<DepartmentResponse>>>> GetDepartments(
        Guid companyId, [FromQuery] bool includeInactive = false)
        => Ok(new ApiResponse<List<DepartmentResponse>>(true,
            await _service.GetDepartmentsAsync(companyId, includeInactive)));

    [HttpGet("departments/{departmentId:guid}")]
    public async Task<ActionResult<ApiResponse<DepartmentResponse>>> GetDepartment(Guid companyId, Guid departmentId)
        => Ok(new ApiResponse<DepartmentResponse>(true, await _service.GetDepartmentAsync(companyId, departmentId)));

    [HttpPost("departments")]
    public async Task<ActionResult<ApiResponse<DepartmentResponse>>> CreateDepartment(
        Guid companyId, [FromBody] CreateDepartmentRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _service.CreateDepartmentAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<DepartmentResponse>(true, result, "สร้างแผนกสำเร็จ"));
    }

    [HttpPut("departments/{departmentId:guid}")]
    public async Task<ActionResult<ApiResponse<DepartmentResponse>>> UpdateDepartment(
        Guid companyId, Guid departmentId, [FromBody] UpdateDepartmentRequest request)
        => Ok(new ApiResponse<DepartmentResponse>(true,
            await _service.UpdateDepartmentAsync(companyId, departmentId, request)));

    [HttpDelete("departments/{departmentId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> DeleteDepartment(Guid companyId, Guid departmentId)
    {
        await _service.DeleteDepartmentAsync(companyId, departmentId);
        return Ok(new ApiResponse<string>(true, null, "ลบแผนกสำเร็จ"));
    }

    // ===== Positions =====

    [HttpGet("positions")]
    public async Task<ActionResult<ApiResponse<List<PositionResponse>>>> GetPositions(
        Guid companyId, [FromQuery] bool includeInactive = false)
        => Ok(new ApiResponse<List<PositionResponse>>(true,
            await _service.GetPositionsAsync(companyId, includeInactive)));

    [HttpGet("positions/{positionId:guid}")]
    public async Task<ActionResult<ApiResponse<PositionResponse>>> GetPosition(Guid companyId, Guid positionId)
        => Ok(new ApiResponse<PositionResponse>(true, await _service.GetPositionAsync(companyId, positionId)));

    [HttpPost("positions")]
    public async Task<ActionResult<ApiResponse<PositionResponse>>> CreatePosition(
        Guid companyId, [FromBody] CreatePositionRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _service.CreatePositionAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<PositionResponse>(true, result, "สร้างตำแหน่งสำเร็จ"));
    }

    [HttpPut("positions/{positionId:guid}")]
    public async Task<ActionResult<ApiResponse<PositionResponse>>> UpdatePosition(
        Guid companyId, Guid positionId, [FromBody] UpdatePositionRequest request)
        => Ok(new ApiResponse<PositionResponse>(true,
            await _service.UpdatePositionAsync(companyId, positionId, request)));

    [HttpDelete("positions/{positionId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> DeletePosition(Guid companyId, Guid positionId)
    {
        await _service.DeletePositionAsync(companyId, positionId);
        return Ok(new ApiResponse<string>(true, null, "ลบตำแหน่งสำเร็จ"));
    }

    // ===== Reporting line lookup =====

    [HttpGet("employees/{employeeId:guid}/direct-manager")]
    public async Task<ActionResult<ApiResponse<DirectManagerInfo>>> GetDirectManager(Guid companyId, Guid employeeId)
        => Ok(new ApiResponse<DirectManagerInfo>(true,
            await _service.GetDirectManagerInfoAsync(companyId, employeeId)));

    // ===== Org chart =====

    [HttpGet("chart")]
    public async Task<ActionResult<ApiResponse<List<OrgChartNode>>>> GetOrgChart(Guid companyId)
        => Ok(new ApiResponse<List<OrgChartNode>>(true, await _service.GetOrgChartAsync(companyId)));
}
