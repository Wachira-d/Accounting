using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 213 (คำตัดสินข้อ 172 · เจ้าของ: "แอดมินต้องตั้งโควตา ราคา แพ็กเกจ จำนวนข้อความ LINE ได้") — เพดานข้อความช่องกลาง<b>เฉพาะบริษัท</b>
/// (<c>Subscription.MaxSharedMessagesPerMonth</c>: ว่าง = ตามแพ็กเกจ · -1 = ไม่จำกัด · ตัวเลข = เพดาน) + ยอดใช้/โบนัสของบริษัท (อ่านอย่างเดียว) ·
/// ผู้อ่านเชิงธุรกิจของค่านี้ = <c>NotifyQuotaService.LoadLimitAsync</c> → <see cref="NotifyQuotaPolicy.PlanLimit"/> ตัวเดียว ·
/// ราคา/ขนาด/อายุแพ็ก <c>notify.topup</c> ตั้งที่ <c>/pages/admin-addons.html</c> (MeteringAdminController) · เพดานต่อแพ็กเกจที่ <c>/admin/plans.html</c>
/// </summary>
[ApiController]
[Route("api/admin/companies/{companyId:guid}/notify-quota")]
[Authorize(Roles = "SystemAdmin")]
public class AdminNotifyQuotaController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly INotifyQuotaService _quota;

    public AdminNotifyQuotaController(AccountingDbContext db, INotifyQuotaService quota)
    {
        _db = db;
        _quota = quota;
    }

    /// <summary><c>Clear</c> = กลับไปใช้ค่าของแพ็กเกจ (ล้างค่าเฉพาะบริษัท) · ไม่งั้นใช้ <c>MaxSharedMessagesPerMonth</c> (-1 = ไม่จำกัด) ·
    /// <c>Channel</c> (รอบ 254 WP-11 L4 · N9): <c>line</c>/<c>email</c> = ตั้งเพดานแยกช่องของบริษัทนี้ (<c>Subscription.MaxSharedLineMessagesPerMonth</c>/<c>MaxSharedEmailsPerMonth</c>) ·
    /// ว่าง = ก้อนรวมเดิม</summary>
    public record SetOverrideRequest(int? MaxSharedMessagesPerMonth, bool Clear = false, string? Notes = null, string? Channel = null);

    private sealed record Overrides(int? All, int? Line, int? Email);

    private async Task<object> BuildAsync(Guid companyId, Overrides o, CancellationToken ct)
    {
        var st = await _quota.GetStatusAsync(companyId, ct);
        return new
        {
            maxSharedMessagesPerMonth = o.All,
            maxSharedLineMessagesPerMonth = o.Line,
            maxSharedEmailsPerMonth = o.Email,
            status = st,
            summary = NotifyQuotaPolicy.AdminSummary(o.All, st.Used, st.PlanLimit, st.BonusRemaining, st.BonusExpiresAt),
            // รอบ 254 WP-11 L4: บรรทัดสรุปต่อช่อง (เซิร์ฟเวอร์ประกอบ) — ช่องที่ไม่แยกบอกว่านับรวมก้อนเดิม
            channelSummaries = st.Channels.Select(c => new
            {
                channel = c.Channel, label = c.Label, ownLimit = c.OwnLimit,
                summary = c.OwnLimit
                    ? NotifyQuotaPolicy.AdminSummary(c.Channel == NotifyQuotaPolicy.ChannelLine ? o.Line : o.Email, c.Used, c.PlanLimit, c.BonusRemaining, c.BonusExpiresAt)
                    : $"ยังไม่แยกเพดาน — นับรวมก้อนเดิม (ยอดที่นับแยกเดือนนี้ {c.Used:N0})",
            }).ToList(),
        };
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(Guid companyId, CancellationToken ct)
    {
        var sub = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new { s.Id, s.MaxSharedMessagesPerMonth, s.MaxSharedLineMessagesPerMonth, s.MaxSharedEmailsPerMonth }).FirstOrDefaultAsync(ct);
        if (sub == null) return NotFound(new ApiResponse<object>(false, null, "บริษัทนี้ยังไม่มีแพ็กเกจ"));
        return Ok(new ApiResponse<object>(true, await BuildAsync(companyId,
            new Overrides(sub.MaxSharedMessagesPerMonth, sub.MaxSharedLineMessagesPerMonth, sub.MaxSharedEmailsPerMonth), ct)));
    }

    /// <summary>ตั้ง/ล้างเพดานเฉพาะบริษัท — มีผลกับข้อความถัดไปทันที (ตัวนับงวดนี้ไม่ถูกรีเซ็ต) · ประวัติ SubscriptionHistory + audit chain</summary>
    [HttpPut]
    public async Task<ActionResult<ApiResponse<object>>> Set(Guid companyId, [FromBody] SetOverrideRequest req, CancellationToken ct)
    {
        int? value = req.Clear ? null : req.MaxSharedMessagesPerMonth;
        if (!req.Clear && value == null)
            return BadRequest(new ApiResponse<object>(false, null, "ระบุเพดาน (-1 = ไม่จำกัด) หรือเลือก \"ตามแพ็กเกจ\""));
        if (NotifyQuotaPolicy.ValidateOverride(value) is string err)
            return BadRequest(new ApiResponse<object>(false, null, err));

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted, ct);
        if (sub == null) return NotFound(new ApiResponse<object>(false, null, "บริษัทนี้ยังไม่มีแพ็กเกจ"));
        // รอบ 254 WP-11 L4 (N9): ช่องที่ตั้ง — ตัวตัดสินชื่อช่องเดียว NotifyQuotaPolicy.NormalizeChannel (ค่าอื่น = ก้อนรวม)
        // ฝ่ายค้าน fix2 L5: ช่องที่ไม่รู้จัก ⇒ 400 (เดิมกลายเป็นก้อนรวมเงียบ ๆ)
        if (!NotifyQuotaPolicy.TryParseChannel(req.Channel, out var channel))
            return BadRequest(new ApiResponse<object>(false, null, NotifyQuotaPolicy.UnknownChannelMessage));
        int? before;
        switch (channel)
        {
            case NotifyQuotaPolicy.ChannelLine:
                before = sub.MaxSharedLineMessagesPerMonth;
                sub.MaxSharedLineMessagesPerMonth = value;
                break;
            case NotifyQuotaPolicy.ChannelEmail:
                before = sub.MaxSharedEmailsPerMonth;
                sub.MaxSharedEmailsPerMonth = value;
                break;
            default:
                before = sub.MaxSharedMessagesPerMonth;
                sub.MaxSharedMessagesPerMonth = value;
                break;
        }
        sub.UpdatedAt = DateTime.UtcNow;
        var channelLabel = NotifyQuotaPolicy.ChannelLabel(channel);

        var adminId = JwtHelper.GetUserIdFromClaims(User).ToString();
        string Show(int? v) => v is int x ? (x < 0 ? "ไม่จำกัด" : x.ToString("N0")) : "ตามแพ็กเกจ";
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "AdminNotifyQuotaChanged",
            Notes = $"เพดานข้อความช่องกลาง/เดือน ({channelLabel}): {Show(before)} → {Show(value)}" + (string.IsNullOrWhiteSpace(req.Notes) ? "" : $" · {req.Notes.Trim()}"),
            PerformedBy = adminId,
        });
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            Action = AuditAction.Update,
            EntityType = "Subscription",
            EntityId = sub.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { channel, limit = before }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "AdminNotifyQuotaChanged", channel, limit = value, by = adminId, notes = req.Notes }),
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, await BuildAsync(companyId,
            new Overrides(sub.MaxSharedMessagesPerMonth, sub.MaxSharedLineMessagesPerMonth, sub.MaxSharedEmailsPerMonth), ct),
            $"ตั้งเพดานข้อความ ({channelLabel}) เป็น {Show(value)} แล้ว"));
    }
}
