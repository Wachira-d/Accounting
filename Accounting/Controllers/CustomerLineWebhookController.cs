using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 211 (คำตัดสินข้อ 170) — webhook ของ LINE OA <b>ของบริษัท</b> (ร้านตั้งใน LINE Developers ว่า <c>{App:BaseUrl}/api/line/webhook/c/{companyId}</c>)
/// <list type="bullet">
/// <item>ด่าน: บริษัทต้องมี Channel Secret ที่ถอดได้ · ลายเซ็นตรวจด้วย <see cref="LineWebhookSignature"/> ตัวเดียวของระบบ · ไม่ได้ตั้ง/ลายเซ็นผิด ⇒ 401 แบบเดียวกัน · body ≤ 256 KB</item>
/// <item>ข้อความ <c>ผูก LN-…</c> ⇒ ผูก LINE ของลูกค้า (<see cref="ICustomerLineLinkService.TryRedeemAsync"/> · scope company + companyId จาก URL ที่ลายเซ็นผ่าน) · ข้อความอื่นไม่ตอบ (แชตของร้านเอง)</item>
/// <item><c>follow</c> ⇒ เปิดลิงก์ที่บล็อกไว้กลับ + ตอบวิธีผูก · <c>unfollow</c> ⇒ ประทับบล็อก (ไม่ส่งจนกว่าจะเพิ่มเพื่อนกลับ)</item>
/// <item>ลายเซ็นผ่านแล้ว ⇒ ตอบ 200 เสมอ (LINE ส่งซ้ำเมื่อไม่ใช่ 2xx) · ความล้มเหลวภายใน log ระดับ Error</item>
/// </list>
/// OA ของ NextAcc ใช้ <c>LineWebhookController</c> (<c>/api/line-webhook</c>) เดิม
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/line/webhook/c/{companyId:guid}")]
public class CustomerLineWebhookController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ICustomerLineLinkService _links;
    private readonly ILogger<CustomerLineWebhookController> _logger;
    private readonly ICustomerRelinkInviteService? _relink;
    /// <summary>รอบ 254 WP-11 L2: ผูก/หยุด/บล็อก LINE ของ<b>ผู้ติดต่อ</b> (ลิงก์/QR ผูก LINE ผ่าน OA ของบริษัท) — ก่อนเส้นลูกค้าเว็บ</summary>
    private readonly IContactLineBindingService? _contactLine;
    /// <summary>รอบ 254 WP-11 L3: กลุ่ม LINE ของผู้ติดต่อ + กัน webhook ซ้ำ</summary>
    private readonly IContactLineGroupService? _groups;

    public CustomerLineWebhookController(AccountingDbContext db, ISecretProtector secrets, ICustomerLineLinkService links,
        ILogger<CustomerLineWebhookController> logger, ICustomerRelinkInviteService? relink = null, IContactLineBindingService? contactLine = null,
        IContactLineGroupService? groups = null)
    {
        _db = db; _secrets = secrets; _links = links; _logger = logger; _relink = relink; _contactLine = contactLine; _groups = groups;
    }

    /// <summary>body ของ LINE webhook เล็ก (event ไม่กี่ตัว) — จำกัด 256 KB กันยิง body ใหญ่ใส่ทางเข้าสาธารณะ (ฝ่ายค้าน ข้อ 6)</summary>
    public const int MaxBodyBytes = 256 * 1024;

    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(Guid companyId, CancellationToken ct)
    {
        // ฝ่ายค้าน ข้อ 6: ดูค่าตั้งก่อนอ่าน body · "ไม่ได้ตั้ง" กับ "ลายเซ็นผิด" ตอบ 401 แบบเดียวกัน (ไม่บอกคนนอกว่าบริษัทไหนตั้ง LINE ไว้)
        var enc = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => s.LineChannelSecret).FirstOrDefaultAsync(ct);
        var secret = _secrets.Unprotect(enc);
        if (string.IsNullOrEmpty(secret)) return Unauthorized();

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (!LineWebhookSignature.Verify(secret, body, Request.Headers["X-Line-Signature"].FirstOrDefault()))
        {
            _logger.LogWarning("LINE webhook ของบริษัท {CompanyId}: ลายเซ็นไม่ตรง", companyId);
            return Unauthorized();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var destination = doc.RootElement.TryGetProperty("destination", out var d) ? d.GetString() : null;
            var destinationAccepted = await _links.LearnDestinationAsync(companyId, destination, ct);
            // รอบ 212 แพ็ก B: bot ใหม่ของ OA ร้าน (ร้านเปลี่ยน OA โดยใช้ช่อง/URL เดิม) ⇒ บันทึกการสลับ ให้งานเบื้องหลังเชิญลูกค้าที่ผูกกับ bot เดิมผูกใหม่ (ไม่โยน) ·
            // ฝ่ายค้าน (B+D) ข้อ 2: บอทไม่ตรงกับที่จำไว้ (secret ของ OA อื่น) ⇒ ไม่ใช่การสลับ — ไม่บันทึก
            if (destinationAccepted && _relink != null) await _relink.NoteWebhookDestinationAsync(CustomerNotifyChannel.ScopeCompany, companyId, destination, ct);
            // ปิดความเสี่ยงค้างของฝ่ายค้าน (B+D) ข้อ 2: เหตุการณ์จากบอทที่ไม่ตรง (secret ของ OA อื่น) ห้ามผูก/ปลดลูกค้า —
            // userId ของ OA นั้นส่งด้วย token ปัจจุบันไม่ได้ ⇒ ไม่ประมวลผล (หน้า LINE ตั้งค่าแสดงปัญหาให้ร้านแก้ secret แล้ว)
            if (!destinationAccepted) return Ok();
            if (!doc.RootElement.TryGetProperty("events", out var events)) return Ok();
            var anyFailed = false;
            foreach (var ev in events.EnumerateArray())
            {
                var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;
                // รอบ 254 WP-11 L3 (G10): LINE ส่ง webhook ซ้ำได้ ⇒ event ที่เห็นแล้วข้าม (ผูก/ตอบไม่ซ้ำ · ตารางกลางหลายเครื่อง)
                var eventId = ev.TryGetProperty("webhookEventId", out var we) ? we.GetString() : null;
                if (_groups != null && !await _groups.MarkEventSeenAsync(eventId, ct)) continue;
                // ฝ่ายค้าน fix2 L1: ประทับ "เห็นแล้ว" ค้างไว้เฉพาะเมื่อประมวลผลสำเร็จ — ล้ม ⇒ ถอนการประทับ + ตอบ 500 ให้ LINE ส่งซ้ำ (เหตุการณ์ที่สำเร็จในชุดเดียวกันถูกข้าม)
                try
                {
                if (!ev.TryGetProperty("source", out var src) || !src.TryGetProperty("type", out var st)) continue;
                var sourceType = st.GetString();
                if (sourceType is "group" or "room")
                {
                    // รอบ 254 WP-11 L3: กลุ่มของผู้ติดต่อ (#HeyNextAcc/#ByeNextAcc · join/leave · สมาชิก) — ข้อความอื่นในกลุ่มไม่ประมวล/ไม่เก็บ
                    if (_groups == null) continue;
                    var outcome = await _groups.HandleCompanyGroupEventAsync(companyId, destination, ev, ct);
                    var groupReplyToken = ev.TryGetProperty("replyToken", out var grt) ? grt.GetString() : null;
                    if (!string.IsNullOrEmpty(outcome.Reply) && !string.IsNullOrEmpty(groupReplyToken))
                        await _links.ReplyAsync(CustomerNotifyChannel.ScopeCompany, companyId, groupReplyToken, outcome.Reply, ct);
                    if (outcome.LeaveAfterReply && src.TryGetProperty("groupId", out var leaveGroup))
                        await _groups.LeaveGroupIfUnboundAsync(companyId, destination, leaveGroup.GetString() ?? "", ct);   // Q8 หลังตอบ
                    continue;
                }
                if (sourceType != "user") continue;
                var userId = src.TryGetProperty("userId", out var u) ? u.GetString() ?? "" : "";
                var replyToken = ev.TryGetProperty("replyToken", out var rt) ? rt.GetString() : null;
                string? reply = null;
                switch (type)
                {
                    case "message":
                        if (ev.TryGetProperty("message", out var msg) && msg.TryGetProperty("type", out var mt) && mt.GetString() == "text")
                        {
                            var text = msg.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                            // รอบ 254 WP-11 L2: รหัสของลิงก์ผูก LINE ผู้ติดต่อ / "หยุด" ก่อน — ไม่ใช่ของผู้ติดต่อ (null) ⇒ เส้นลูกค้าเว็บเดิม
                            if (_contactLine != null)
                                reply = await _contactLine.TryRedeemAsync(companyId, destination, userId, text, ct)
                                    ?? await _contactLine.TryStopAsync(companyId, destination, userId, text, ct);
                            reply ??= await _links.TryRedeemAsync(CustomerNotifyChannel.ScopeCompany, companyId, destination, userId, text, ct);
                        }
                        break;
                    case "follow":
                        var restored = await _links.SetBlockedAsync(CustomerNotifyChannel.ScopeCompany, companyId, userId, blocked: false, ct);
                        if (_contactLine != null) restored += await _contactLine.SetBlockedAsync(companyId, destination, userId, blocked: false, ct);
                        reply = restored > 0 ? CustomerLineLinkRule.Replies.WelcomeBack : CustomerLineLinkRule.Replies.FollowHint;
                        break;
                    case "unfollow":
                        await _links.SetBlockedAsync(CustomerNotifyChannel.ScopeCompany, companyId, userId, blocked: true, ct);
                        if (_contactLine != null) await _contactLine.SetBlockedAsync(companyId, destination, userId, blocked: true, ct);
                        break;
                }
                if (!string.IsNullOrEmpty(reply) && !string.IsNullOrEmpty(replyToken))
                    await _links.ReplyAsync(CustomerNotifyChannel.ScopeCompany, companyId, replyToken, reply, ct);
                }
                catch (Exception evEx) when (evEx is not OperationCanceledException)
                {
                    anyFailed = true;
                    _logger.LogError(evEx, "LINE webhook ของบริษัท {CompanyId}: ประมวลเหตุการณ์ {EventId} ล้ม — ถอนการประทับให้ส่งซ้ำได้", companyId, eventId);
                    if (_groups != null) await _groups.UnmarkEventSeenAsync(eventId, CancellationToken.None);
                }
            }
            if (anyFailed) return StatusCode(500);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "LINE webhook ของบริษัท {CompanyId} ประมวลผลล้ม", companyId);
        }
        return Ok();
    }
}
