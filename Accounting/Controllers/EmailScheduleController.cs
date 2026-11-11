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
    /// <summary>รอบ 254 WP-11 L2: คิว LINE ถึงผู้ติดต่อ (หน้าเดียวกับคิวอีเมล)</summary>
    private readonly IContactLineQueueService? _lineQueue;
    /// <summary>รอบ 254 WP-11 L2: ด่านเขียนของคิว LINE (ส่งอีกครั้ง = ส่งข้อความในนามบริษัท) — CompanySettings.Edit · ไม่มีบริการ = ปฏิเสธ (ปิดทางปลอดภัย)</summary>
    private readonly IPermissionService? _permissions;
    /// <summary>รอบ 257 ชุด 4 (C-M5): ป้ายช่องทาง LINE ตามบริษัท (กลุ่มผ่าน OA กลางของบริษัทผู้ให้บริการ)</summary>
    private readonly IContactRecipientResolver? _recipients;
    public EmailScheduleController(IEmailScheduleService svc, IContactLineQueueService? lineQueue = null, IPermissionService? permissions = null,
        IContactRecipientResolver? recipients = null)
    {
        _svc = svc; _lineQueue = lineQueue; _permissions = permissions; _recipients = recipients;
    }

    /// <summary>รอบ 257 ชุด 4 (C-M5 · server computes): ป้ายช่องทาง LINE ของฟอร์มกฎ — บริษัทผู้ให้บริการส่งเข้ากลุ่มผ่าน OA กลางได้ (256-2) · อ่านอย่างเดียว</summary>
    [HttpGet("line-labels")]
    public async Task<ActionResult<ApiResponse<ContactLineChannel.RuleChannelLabels>>> LineLabels(Guid companyId)
    {
        var open = _recipients != null && (await _recipients.LineChannelAsync(companyId)).NextAccGroupOpen;
        return Ok(new ApiResponse<ContactLineChannel.RuleChannelLabels>(true, ContactLineChannel.ChannelLabels(open)));
    }

    /// <summary>รอบ 254 WP-11 L2 → รอบ 258 (A-H1/B-H1): ด่านเขียนของหน้าตารางส่งทั้งหน้า — กฎ (สร้าง/แก้/ลบ) + คิวอีเมล + คิว LINE ·
    /// ตัวตัดสินเดียว <see cref="SettingsWriteGate"/> (CompanySettings.Edit · งานตั้งกฎห้ามใช้ API key) · null = ผ่าน · ไม่มีบริการสิทธิ์ = ปฏิเสธ</summary>
    private async Task<ActionResult?> RequireSettingsAsync(Guid companyId, string verb = "จัดการคิว LINE", bool rejectApiKey = false)
    {
        var decision = await SettingsWriteGate.EvaluateAsync(_permissions, HttpContext, companyId, verb, rejectApiKey);
        return decision.Allowed ? null
            : StatusCode(403, new ApiResponse<object>(false, new { ruleCode = decision.RuleCode }, decision.Message));
    }

    public sealed record RuleRequest(
        Guid Id, string Trigger, bool IsActive, string? DocumentType,
        int OffsetDays, int SendAtHour, int RepeatEveryDays,
        string? SubjectTemplate, string? BodyTemplate, string? BccEmails,
        int DayOfMonth = 5,
        // รอบ 241: ส่งเอกสารอัตโนมัติ — null = ตามค่าบริษัท · ช่องทาง null/ว่าง = ตามค่าบริษัท
        bool? AttachPayLink = null, List<string>? PayLinkChannels = null,
        bool? PostPayEmailReceipt = null, bool? PostPayIncludePartial = null,
        // รอบ 254 WP-11 L2: ช่องทางต่อกฎ (ไม่ส่ง = พฤติกรรมเดิม: อีเมลอย่างเดียว · ถอยไปอีเมลค่าตั้งต้นเปิด — Q7)
        bool ChannelEmail = true, bool ChannelLineUser = false, bool ChannelLineGroup = false, bool LineFallbackToEmail = true);

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
        // รอบ 258 (A-H1/B-H1): กฎ = ใครได้สำเนา (Bcc) · เนื้ออีเมล · ช่องทาง LINE ⇒ ต้องถือ CompanySettings.Edit และห้ามใช้ API key
        if (await RequireSettingsAsync(companyId, "แก้กฎตารางส่ง", rejectApiKey: true) is { } deny) return deny;
        // รอบ 250 WP-03 (ฝ่ายค้าน M4): ค่านอกช่วงตอบ 400 พร้อมช่วง (ช่วงเดียวกับหน้าเว็บ) — เดิม Math.Clamp เงียบ ⇒ ส่งผิดเวลา/ผิดวันโดยผู้ใช้ไม่รู้
        var rangeError = RuleRangeError(req.SendAtHour, req.DayOfMonth, req.RepeatEveryDays)
            ?? Accounting.Helpers.OverdueReminderSchedule.RepeatProblem(req.Trigger, req.RepeatEveryDays);   // ฝ่ายค้าน M2
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
            ChannelEmail = req.ChannelEmail,
            ChannelLineUser = req.ChannelLineUser,
            ChannelLineGroup = req.ChannelLineGroup,
            LineFallbackToEmail = req.LineFallbackToEmail,
        };
        var saved = await _svc.UpsertRuleAsync(companyId, rule,
            User.Identity?.Name ?? JwtHelper.GetUserIdFromClaims(User).ToString());
        return Ok(new ApiResponse<EmailScheduleRule>(true, saved, "บันทึกกฎสำเร็จ"));
    }

    [HttpDelete("rules/{ruleId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteRule(Guid companyId, Guid ruleId)
    {
        if (await RequireSettingsAsync(companyId, "ลบกฎตารางส่ง", rejectApiKey: true) is { } deny) return deny;
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
        // รอบ 258 (A-H1/B-H1): ส่งอีกครั้ง = ส่งอีเมลในนามบริษัท — ด่านเดียวกับคิว LINE
        if (await RequireSettingsAsync(companyId, "ส่งอีเมลในคิวอีกครั้ง") is { } deny) return deny;
        await _svc.RetryFailedAsync(companyId, queueId);
        return Ok(new ApiResponse<object>(true, null, "พักไว้ในคิวรอบใหม่"));
    }

    /// <summary>รอบ 254 WP-11 L2: คิว LINE (ป้ายสถานะ/ชนิดจากเซิร์ฟเวอร์ · ไม่มี LINE id)</summary>
    [HttpGet("line-queue")]
    public async Task<ActionResult<ApiResponse<List<LineQueueItemDto>>>> GetLineQueue(Guid companyId, [FromQuery] int take = 50, CancellationToken ct = default)
        => Ok(new ApiResponse<List<LineQueueItemDto>>(true, _lineQueue == null ? new List<LineQueueItemDto>() : await _lineQueue.GetRecentAsync(companyId, take, ct)));

    [HttpPost("line-queue/{queueId:guid}/retry")]
    public async Task<ActionResult<ApiResponse<object>>> RetryLine(Guid companyId, Guid queueId, CancellationToken ct)
    {
        if (await RequireSettingsAsync(companyId) is { } deny) return deny;
        if (_lineQueue == null) return StatusCode(503, new ApiResponse<object>(false, null, "คิว LINE ยังไม่พร้อม"));
        await _lineQueue.RetryFailedAsync(companyId, queueId, ct);
        return Ok(new ApiResponse<object>(true, null, "พักไว้ในคิว LINE รอบใหม่ (ส่งอีกครั้ง)"));
    }

    [HttpPost("line-queue/{queueId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<object>>> CancelLine(Guid companyId, Guid queueId, CancellationToken ct)
    {
        if (await RequireSettingsAsync(companyId) is { } deny) return deny;
        if (_lineQueue == null) return StatusCode(503, new ApiResponse<object>(false, null, "คิว LINE ยังไม่พร้อม"));
        await _lineQueue.CancelPendingAsync(companyId, queueId, ct);
        return Ok(new ApiResponse<object>(true, null, "ยกเลิกคิว LINE แล้ว"));
    }

    [HttpPost("queue/{queueId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<object>>> Cancel(Guid companyId, Guid queueId)
    {
        if (await RequireSettingsAsync(companyId, "ยกเลิกอีเมลในคิว") is { } deny) return deny;
        await _svc.CancelPendingAsync(companyId, queueId);
        return Ok(new ApiResponse<object>(true, null, "ยกเลิกคิวแล้ว"));
    }
}
