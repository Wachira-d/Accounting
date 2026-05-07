using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Company;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/company/{companyId:guid}/roles")]
[Authorize]
public class RolePermissionController : ControllerBase
{
    private readonly IRolePermissionService _roleService;

    public RolePermissionController(IRolePermissionService roleService)
    {
        _roleService = roleService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<CompanyRoleResponse>>>> GetRoles(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _roleService.GetRolesAsync(companyId, userId);
        return Ok(new ApiResponse<List<CompanyRoleResponse>>(true, result));
    }

    [HttpGet("{roleId:guid}")]
    public async Task<ActionResult<ApiResponse<CompanyRoleResponse>>> GetRole(Guid companyId, Guid roleId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _roleService.GetRoleByIdAsync(companyId, roleId, userId);
        return Ok(new ApiResponse<CompanyRoleResponse>(true, result));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<CompanyRoleResponse>>> CreateRole(Guid companyId, [FromBody] CreateCompanyRoleRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _roleService.CreateRoleAsync(companyId, userId, request);
        return StatusCode(201, new ApiResponse<CompanyRoleResponse>(true, result, "สร้าง Role สำเร็จ"));
    }

    [HttpPut("{roleId:guid}")]
    public async Task<ActionResult<ApiResponse<CompanyRoleResponse>>> UpdateRole(Guid companyId, Guid roleId, [FromBody] UpdateCompanyRoleRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _roleService.UpdateRoleAsync(companyId, roleId, userId, request);
        return Ok(new ApiResponse<CompanyRoleResponse>(true, result, "อัปเดต Role สำเร็จ"));
    }

    [HttpDelete("{roleId:guid}")]
    public async Task<ActionResult> DeleteRole(Guid companyId, Guid roleId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _roleService.DeleteRoleAsync(companyId, roleId, userId);
        return NoContent();
    }

    [HttpPost("{roleId:guid}/assign/{targetUserId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> AssignRole(Guid companyId, Guid roleId, Guid targetUserId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _roleService.AssignRoleAsync(companyId, targetUserId, userId, new AssignCustomRoleRequest(roleId));
        return Ok(new ApiResponse<string>(true, null, "กำหนด Role สำเร็จ"));
    }

    [HttpPost("seed-defaults")]
    public async Task<ActionResult<ApiResponse<string>>> SeedDefaults(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _roleService.SeedDefaultRolesAsync(companyId);
        return Ok(new ApiResponse<string>(true, null, "สร้าง Role เริ่มต้นสำเร็จ"));
    }

    [HttpGet("my-permissions")]
    public async Task<ActionResult<ApiResponse<MyPermissionsResponse>>> GetMyPermissions(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _roleService.GetMyPermissionsAsync(companyId, userId);
        return Ok(new ApiResponse<MyPermissionsResponse>(true, result));
    }
}
