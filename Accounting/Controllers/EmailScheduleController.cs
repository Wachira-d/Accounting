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
        int DayOfMonth = 5,
        // รอบ 241: ส่งเอกสารอัตโนมัติ — null = ตามค่าบริษัท · ช่องทาง null/ว่าง = ตามค่าบริษัท
        bool? AttachPayLink = null, List<string>? PayLinkChannels = null,
        bool? PostPayEmailReceipt = null, bool? PostPayIncludePartial = null);

    /// <summary>ช่วงที่รับของกฎส่งอีเมล (ตัวเดียว — หน้าเว็บตรวจช่วงเดียวกันก่อนส่ง) · null = ผ่าน</summary>
    internal static string? RuleRangeError(int sendAtHour, int dayOfMonth, int repeatEveryDays)
    {
        if (sendAtHour is < 0 or > 23) return $"ชั่วโมงที่ส่งต้องอยู่ระหว่าง 0–23 (ได้ {sendAtHour})";
        if (dayOfMonth is < 1 or > 28) return $"วันที่ของเดือนต้องอยู่ระหว่าง 1–28 (ได้ {dayOfMonth}) — กันเดือนที่ไม่มีวันที่ 29–31";
        if (repeatEveryDays < 0) return $"ส่งซ้ำทุก (วัน) ต้องไม่ติดลบ (ได้ {repeatEveryDays})";
        return null;
    }

    [HttpGet("rules")]
    public async Task<ActionResult<ApiResponse<List<EmailScheduleRule>>>> GetRules(Guid companyId)
        => Ok(new ApiResponse<List<EmailScheduleRule>>(true, await _svc.GetRulesAsync(companyId)));

    [HttpPut("rules")]
    public async Task<ActionResult<ApiResponse<EmailScheduleRule>>> UpsertRule(
        Guid companyId, [FromBody] RuleRequest req)
    {
        // รอบ 250 WP-03 (ฝ่ายค้าน M4): ค่านอกช่วงตอบ 400 พร้อมช่วง (ช่วงเดียวกับหน้าเว็บ) — เดิม Math.Clamp เงียบ ⇒ ส่งผิดเวลา/ผิดวันโดยผู้ใช้ไม่รู้
        var rangeError = RuleRangeError(req.SendAtHour, req.DayOfMonth, req.RepeatEveryDays);
        if (rangeError != null) return BadRequest(new ApiResponse<EmailScheduleRule>(false, null, rangeError));
        var rule = new EmailScheduleRule
        {
            Id = req.Id,
            CompanyId = companyId,
            Trigger = req.Trigger,
            IsActive = req.IsActive,
            DocumentType = req.DocumentType,
            OffsetDays = req.OffsetDays,
            SendAtHour = req.SendAtHour,
            RepeatEveryDays = req.RepeatEveryDays,
            SubjectTemplate = req.SubjectTemplate,
            BodyTemplate = req.BodyTemplate,
            BccEmails = req.BccEmails,
            DayOfMonth = req.DayOfMonth,
            AttachPayLink = req.AttachPayLink,
            PayLinkChannelsJson = DocumentPayLinkPolicy.SerializeChannels(req.PayLinkChannels),
            PostPayEmailReceipt = req.PostPayEmailReceipt,
            PostPayIncludePartial = req.PostPayIncludePartial,
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
                // ID#12: ป้ายไทยจากเซิร์ฟเวอร์ (รวม "ข้าม (ไม่ส่ง)" ของใบที่ถูกยกเลิก/ชำระครบหลังเข้าคิว) — หน้าเว็บแสดงอย่างเดียว
                StatusLabel = Accounting.Helpers.ScheduledEmailSendCheck.StatusLabel(q.Status),
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
