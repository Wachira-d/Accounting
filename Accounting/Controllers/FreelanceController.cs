using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Freelance;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Authorize]
[Route("api/companies/{companyId:guid}/freelance")]
public class FreelanceController : ControllerBase
{
    private readonly IFreelanceService _freelanceService;

    public FreelanceController(IFreelanceService freelanceService)
    {
        _freelanceService = freelanceService;
    }

    // ===== Invitations (Company Owner) =====

    [HttpPost("invite")]
    public async Task<ActionResult<ApiResponse<InvitationResponse>>> Invite(Guid companyId, [FromBody] InviteFreelanceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _freelanceService.InviteFreelanceAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<InvitationResponse>(true, result, "ส่งคำเชิญสำเร็จ"));
    }

    [HttpGet("invitations")]
    public async Task<ActionResult<ApiResponse<List<InvitationResponse>>>> GetInvitations(Guid companyId)
    {
        var result = await _freelanceService.GetInvitationsAsync(companyId);
        return Ok(new ApiResponse<List<InvitationResponse>>(true, result));
    }

    [HttpDelete("invitations/{invitationId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> RevokeInvitation(Guid companyId, Guid invitationId)
    {
        await _freelanceService.RevokeInvitationAsync(companyId, invitationId);
        return Ok(new ApiResponse<string>(true, "ยกเลิกคำเชิญสำเร็จ"));
    }

    // ===== Accept Invitation (Public - Freelance) =====

    [HttpPost("~/api/freelance/accept-invitation")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<InvitationResponse>>> AcceptInvitation([FromBody] AcceptInvitationRequest request)
    {
        var result = await _freelanceService.AcceptInvitationAsync(request);
        return Ok(new ApiResponse<InvitationResponse>(true, result, "ตอบรับคำเชิญสำเร็จ"));
    }

    // ===== Freelance Access Management (Company Owner) =====

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<FreelanceAccessResponse>>>> GetFreelancers(Guid companyId)
    {
        var result = await _freelanceService.GetCompanyFreelancersAsync(companyId);
        return Ok(new ApiResponse<List<FreelanceAccessResponse>>(true, result));
    }

    [HttpGet("{accessId:guid}")]
    public async Task<ActionResult<ApiResponse<FreelanceAccessResponse>>> GetFreelanceAccess(Guid companyId, Guid accessId)
    {
        var result = await _freelanceService.GetFreelanceAccessAsync(companyId, accessId);
        return Ok(new ApiResponse<FreelanceAccessResponse>(true, result));
    }

    [HttpPut("{accessId:guid}")]
    public async Task<ActionResult<ApiResponse<FreelanceAccessResponse>>> UpdateFreelanceAccess(
        Guid companyId, Guid accessId, [FromBody] UpdateFreelanceAccessRequest request)
    {
        var result = await _freelanceService.UpdateFreelanceAccessAsync(companyId, accessId, request);
        return Ok(new ApiResponse<FreelanceAccessResponse>(true, result, "อัพเดทสิทธิ์สำเร็จ"));
    }

    [HttpPost("{accessId:guid}/deactivate")]
    public async Task<ActionResult<ApiResponse<string>>> DeactivateFreelance(Guid companyId, Guid accessId)
    {
        await _freelanceService.DeactivateFreelanceAccessAsync(companyId, accessId);
        return Ok(new ApiResponse<string>(true, null, "ปิดสิทธิ์ Freelance สำเร็จ"));
    }

    // ===== Task Management =====

    [HttpPost("tasks")]
    public async Task<ActionResult<ApiResponse<FreelanceTaskResponse>>> CreateTask(Guid companyId, [FromBody] CreateFreelanceTaskRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _freelanceService.CreateTaskAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<FreelanceTaskResponse>(true, result, "สร้างงานสำเร็จ"));
    }

    [HttpGet("tasks")]
    public async Task<ActionResult<ApiResponse<List<FreelanceTaskSummary>>>> GetTasks(Guid companyId, [FromQuery] Guid? freelanceAccessId = null)
    {
        var result = await _freelanceService.GetTasksAsync(companyId, freelanceAccessId);
        return Ok(new ApiResponse<List<FreelanceTaskSummary>>(true, result));
    }

    [HttpGet("tasks/{taskId:guid}")]
    public async Task<ActionResult<ApiResponse<FreelanceTaskResponse>>> GetTask(Guid companyId, Guid taskId)
    {
        var result = await _freelanceService.GetTaskAsync(companyId, taskId);
        return Ok(new ApiResponse<FreelanceTaskResponse>(true, result));
    }

    [HttpPut("tasks/{taskId:guid}")]
    public async Task<ActionResult<ApiResponse<FreelanceTaskResponse>>> UpdateTask(
        Guid companyId, Guid taskId, [FromBody] UpdateFreelanceTaskRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _freelanceService.UpdateTaskAsync(companyId, taskId, request, userId);
        return Ok(new ApiResponse<FreelanceTaskResponse>(true, result));
    }

    [HttpPost("tasks/{taskId:guid}/submit")]
    public async Task<ActionResult<ApiResponse<FreelanceTaskResponse>>> SubmitTask(Guid companyId, Guid taskId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _freelanceService.SubmitTaskAsync(companyId, taskId, userId);
        return Ok(new ApiResponse<FreelanceTaskResponse>(true, result, "ส่งงานสำเร็จ"));
    }

    [HttpPost("tasks/{taskId:guid}/review")]
    public async Task<ActionResult<ApiResponse<FreelanceTaskResponse>>> ReviewTask(
        Guid companyId, Guid taskId, [FromQuery] bool approve, [FromBody] string? notes)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _freelanceService.ReviewTaskAsync(companyId, taskId, approve, notes, userId);
        return Ok(new ApiResponse<FreelanceTaskResponse>(true, result));
    }

    // ===== Comments & Time Logs =====

    [HttpPost("tasks/{taskId:guid}/comments")]
    public async Task<ActionResult<ApiResponse<TaskCommentResponse>>> AddComment(
        Guid companyId, Guid taskId, [FromBody] AddTaskCommentRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _freelanceService.AddCommentAsync(taskId, userId, request);
        return StatusCode(201, new ApiResponse<TaskCommentResponse>(true, result));
    }

    [HttpPost("tasks/{taskId:guid}/time-logs")]
    public async Task<ActionResult<ApiResponse<TimeLogResponse>>> AddTimeLog(
        Guid companyId, Guid taskId, [FromBody] CreateTimeLogRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _freelanceService.AddTimeLogAsync(taskId, userId, request);
        return StatusCode(201, new ApiResponse<TimeLogResponse>(true, result));
    }

    // ===== Activity Logs =====

    [HttpGet("activity-logs")]
    public async Task<ActionResult<ApiResponse<PagedResponse<FreelanceActivityLogResponse>>>> GetActivityLogs(
        Guid companyId, [FromQuery] Guid? freelanceAccessId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _freelanceService.GetActivityLogsAsync(companyId, freelanceAccessId, new PagedRequest(page, pageSize));
        return Ok(new ApiResponse<PagedResponse<FreelanceActivityLogResponse>>(true, result));
    }

    // ===== Freelance Dashboard (for Freelancer) =====

    [HttpGet("~/api/freelance/dashboard")]
    public async Task<ActionResult<ApiResponse<FreelanceDashboardResponse>>> GetDashboard()
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _freelanceService.GetFreelanceDashboardAsync(userId);
        return Ok(new ApiResponse<FreelanceDashboardResponse>(true, result));
    }
}
