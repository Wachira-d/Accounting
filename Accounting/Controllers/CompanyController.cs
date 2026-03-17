using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Company;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CompanyController : ControllerBase
{
    private readonly ICompanyService _companyService;

    public CompanyController(ICompanyService companyService)
    {
        _companyService = companyService;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<CompanyResponse>>> Create([FromBody] CreateCompanyRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _companyService.CreateAsync(userId, request);
        return Ok(new ApiResponse<CompanyResponse>(true, result, "สร้างบริษัทสำเร็จ"));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<CompanyResponse>>>> GetMyCompanies()
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _companyService.GetUserCompaniesAsync(userId);
        return Ok(new ApiResponse<List<CompanyResponse>>(true, result));
    }

    [HttpGet("{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<CompanyResponse>>> GetById(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _companyService.GetByIdAsync(companyId, userId);
        return Ok(new ApiResponse<CompanyResponse>(true, result));
    }

    [HttpPut("{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<CompanyResponse>>> Update(Guid companyId, [FromBody] UpdateCompanyRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _companyService.UpdateAsync(companyId, userId, request);
        return Ok(new ApiResponse<CompanyResponse>(true, result));
    }

    [HttpPost("{companyId:guid}/users")]
    public async Task<ActionResult<ApiResponse<string>>> AddUser(Guid companyId, [FromBody] AddCompanyUserRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _companyService.AddUserAsync(companyId, userId, request);
        return Ok(new ApiResponse<string>(true, null, "เพิ่มผู้ใช้สำเร็จ"));
    }

    [HttpDelete("{companyId:guid}/users/{targetUserId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> RemoveUser(Guid companyId, Guid targetUserId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _companyService.RemoveUserAsync(companyId, userId, targetUserId);
        return Ok(new ApiResponse<string>(true, null, "ลบผู้ใช้สำเร็จ"));
    }
}
