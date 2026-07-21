using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/email-schedule")]
[Authorize]
public class EmailScheduleController : ControllerBase
{
    private readonly IEmailScheduleService _svc;
    public EmailScheduleController(IEmailScheduleService svc) { _svc = svc; }

    public sealed record RuleRequest(
        Guid Id, string Trigger, bool IsActive, string? DocumentType,
        int OffsetDays, int SendAtHour, int RepeatEveryDays,
        string? SubjectTemplate, string? BodyTemplate, string? BccEmails,
        int DayOfMonth = 5);

    [HttpGet("rules")]
    public async Task<ActionResult<ApiResponse<List<EmailScheduleRule>>>> GetRules(Guid companyId)
        => Ok(new ApiResponse<List<EmailScheduleRule>>(true, await _svc.GetRulesAsync(companyId)));

    [HttpPut("rules")]
    public async Task<ActionResult<ApiResponse<EmailScheduleRule>>> UpsertRule(
        Guid companyId, [FromBody] RuleRequest req)
    {
        var rule = new EmailScheduleRule
        {
            Id = req.Id,
            CompanyId = companyId,
            Trigger = req.Trigger,
            IsActive = req.IsActive,
            DocumentType = req.DocumentType,
            OffsetDays = req.OffsetDays,
            SendAtHour = Math.Clamp(req.SendAtHour, 0, 23),
            RepeatEveryDays = Math.Max(0, req.RepeatEveryDays),
            SubjectTemplate = req.SubjectTemplate,
            BodyTemplate = req.BodyTemplate,
            BccEmails = req.BccEmails,
            DayOfMonth = Math.Clamp(req.DayOfMonth, 1, 28),
        };
        var saved = await _svc.UpsertRuleAsync(companyId, rule,
            User.Identity?.Name ?? JwtHelper.GetUserIdFromClaims(User).ToString());
        return Ok(new ApiResponse<EmailScheduleRule>(true, saved, "บันทึกกฎสำเร็จ"));
    }

    [HttpDelete("rules/{ruleId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteRule(Guid companyId, Guid ruleId)
    {
        await _svc.DeleteRuleAsync(companyId, ruleId);
        return Ok(new ApiResponse<object>(true, null, "ลบกฎแล้ว"));
    }

    [HttpGet("queue")]
    public async Task<ActionResult<ApiResponse<object>>> GetQueue(Guid companyId, [FromQuery] int take = 50)
    {
        var (pending, sent, failed) = await _svc.GetQueueStatsAsync(companyId);
        var recent = await _svc.GetRecentQueueAsync(companyId, take);
        return Ok(new ApiResponse<object>(true, new
        {
            stats = new { pending, sent, failed },
            items = recent.Select(q => new
            {
                q.Id, q.EntityType, q.EntityId, q.ToEmail, q.Subject,
                q.Status, q.RetryCount, q.ScheduledFor, q.SentAt,
                q.ErrorMessage, q.CreatedAt,
            }),
        }));
    }

    [HttpPost("queue/{queueId:guid}/retry")]
    public async Task<ActionResult<ApiResponse<object>>> Retry(Guid companyId, Guid queueId)
    {
        await _svc.RetryFailedAsync(companyId, queueId);
        return Ok(new ApiResponse<object>(true, null, "พักไว้ในคิวรอบใหม่"));
    }

    [HttpPost("queue/{queueId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<object>>> Cancel(Guid companyId, Guid queueId)
    {
        await _svc.CancelPendingAsync(companyId, queueId);
        return Ok(new ApiResponse<object>(true, null, "ยกเลิกคิวแล้ว"));
    }
}
