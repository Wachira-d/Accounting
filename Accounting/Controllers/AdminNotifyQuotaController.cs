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

    /// <summary><c>Clear</c> = กลับไปใช้ค่าของแพ็กเกจ (ล้างค่าเฉพาะบริษัท) · ไม่งั้นใช้ <c>MaxSharedMessagesPerMonth</c> (-1 = ไม่จำกัด)</summary>
    public record SetOverrideRequest(int? MaxSharedMessagesPerMonth, bool Clear = false, string? Notes = null);

    private async Task<object> BuildAsync(Guid companyId, int? overrideValue, CancellationToken ct)
    {
        var st = await _quota.GetStatusAsync(companyId, ct);
        return new
        {
            maxSharedMessagesPerMonth = overrideValue,
            status = st,
            summary = NotifyQuotaPolicy.AdminSummary(overrideValue, st.Used, st.PlanLimit, st.BonusRemaining, st.BonusExpiresAt),
        };
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(Guid companyId, CancellationToken ct)
    {
        var sub = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new { s.Id, s.MaxSharedMessagesPerMonth }).FirstOrDefaultAsync(ct);
        if (sub == null) return NotFound(new ApiResponse<object>(false, null, "บริษัทนี้ยังไม่มีแพ็กเกจ"));
        return Ok(new ApiResponse<object>(true, await BuildAsync(companyId, sub.MaxSharedMessagesPerMonth, ct)));
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
        var before = sub.MaxSharedMessagesPerMonth;
        sub.MaxSharedMessagesPerMonth = value;
        sub.UpdatedAt = DateTime.UtcNow;

        var adminId = JwtHelper.GetUserIdFromClaims(User).ToString();
        string Show(int? v) => v is int x ? (x < 0 ? "ไม่จำกัด" : x.ToString("N0")) : "ตามแพ็กเกจ";
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "AdminNotifyQuotaChanged",
            Notes = $"เพดานข้อความช่องกลาง/เดือน: {Show(before)} → {Show(value)}" + (string.IsNullOrWhiteSpace(req.Notes) ? "" : $" · {req.Notes.Trim()}"),
            PerformedBy = adminId,
        });
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            Action = AuditAction.Update,
            EntityType = "Subscription",
            EntityId = sub.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { maxSharedMessagesPerMonth = before }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "AdminNotifyQuotaChanged", maxSharedMessagesPerMonth = value, by = adminId, notes = req.Notes }),
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, await BuildAsync(companyId, value, ct), $"ตั้งเพดานข้อความเป็น {Show(value)} แล้ว"));
    }
}
