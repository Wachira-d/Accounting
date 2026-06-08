using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Company;
using Accounting.Models.Enums;
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
        return StatusCode(201, new ApiResponse<CompanyResponse>(true, result, "สร้างบริษัทสำเร็จ"));
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

    [HttpGet("{companyId:guid}/users")]
    public async Task<ActionResult<ApiResponse<List<CompanyMemberResponse>>>> GetMembers(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _companyService.GetMembersAsync(companyId, userId);
        return Ok(new ApiResponse<List<CompanyMemberResponse>>(true, result));
    }

    [HttpPost("{companyId:guid}/users")]
    public async Task<ActionResult<ApiResponse<object>>> AddUser(Guid companyId, [FromBody] AddCompanyUserRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _companyService.AddUserAsync(companyId, userId, request);
        // Honest message per outcome. When email isn't configured we DON'T
        // claim it was sent — we tell the owner to share the link manually
        // (returned in the payload so the UI can show a copy button).
        string message;
        if (!result.WasInvited)
            message = "เพิ่มผู้ใช้สำเร็จ";
        else if (result.EmailSent)
            message = $"ส่งคำเชิญไปยัง {result.Email} แล้ว — ผู้รับจะได้รับลิงก์สมัครและเข้าร่วมทีมในอีเมล";
        else
            message = $"สร้างคำเชิญสำหรับ {result.Email} แล้ว แต่ระบบยังไม่ได้ตั้งค่าอีเมล — กรุณาคัดลอกลิงก์ด้านล่างส่งให้ผู้รับเอง";
        return StatusCode(201, new ApiResponse<object>(true,
            new { result.WasInvited, result.Email, result.InvitationId, result.EmailSent, result.InviteLink }, message));
    }

    [HttpPut("{companyId:guid}/users/{targetUserId:guid}/role")]
    public async Task<ActionResult<ApiResponse<string>>> UpdateUserRole(Guid companyId, Guid targetUserId, [FromBody] UpdateUserRoleRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _companyService.UpdateUserRoleAsync(companyId, userId, targetUserId, request.Role);
        return Ok(new ApiResponse<string>(true, null, "เปลี่ยน Role สำเร็จ"));
    }

    [HttpDelete("{companyId:guid}/users/{targetUserId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> RemoveUser(Guid companyId, Guid targetUserId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _companyService.RemoveUserAsync(companyId, userId, targetUserId);
        return NoContent();
    }
}
