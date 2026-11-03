using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §B.7 · คำตัดสินข้อ 231-3) — แอดมินแพลตฟอร์ม: <b>ตารางราคาส่วนเกิน</b> (ทรัพยากร × แพ็กเกจ · ประวัติราคา) และ
/// <b>นโยบายส่วนเกินรายบริษัท</b> (ดู/ตั้งแทน/ปลดล็อก พร้อมเหตุผล + audit) — หน้า <c>/pages/admin-quota.html</c> · ค่าตั้งผ่อนผัน/ขั้นต่ำ/เกณฑ์เตือนอยู่ที่
/// <c>/admin/platform-billing.html</c> (<c>AdminPlatformBillingController.UpdateSettings</c>)
/// </summary>
[ApiController]
[Route("api/admin/quota")]
[Authorize(Roles = "SystemAdmin")]
public class AdminQuotaOverageController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IOverageService _overage;

    public AdminQuotaOverageController(AccountingDbContext db, IOverageService overage) { _db = db; _overage = overage; }

    private PlatformBillingActor Actor() => new(JwtHelper.GetUserIdFromClaims(User), User.Identity?.Name ?? "system-admin", HttpContext.Connection.RemoteIpAddress?.ToString());

    /// <summary>ราคาทั้งหมด (รวมประวัติ) + แม่แบบแพ็กเกจ + ทรัพยากรส่วนเกิน (closed list — หน้าเว็บไม่พิมพ์รหัสเอง)</summary>
    [HttpGet("overage-rates")]
    public async Task<ActionResult<ApiResponse<object>>> Rates(CancellationToken ct)
    {
        var rates = await _overage.RatesAsync(ct);
        var plans = await _db.PlanTemplates.AsNoTracking().Where(t => !t.IsDeleted).OrderBy(t => t.Plan).ThenBy(t => t.CreatedAt)
            .Select(t => new { id = t.Id, name = t.Name, plan = t.Plan.ToString(), isActive = t.IsActive }).ToListAsync(ct);
        var now = DateTime.UtcNow;
        return Ok(new ApiResponse<object>(true, new
        {
            resources = OverageResources.All.Select(r => new { key = r.Key, label = r.Label, unit = r.Unit, realtime = r.Realtime }),
            plans,
            rates = rates.Select(r => new
            {
                id = r.Id, resource = r.ResourceKey, planTemplateId = r.PlanTemplateId, unitSize = r.UnitSize, unitPrice = r.UnitPrice,
                effectiveFrom = r.EffectiveFromUtc, effectiveTo = r.EffectiveToUtc, live = OveragePolicy.RateLive(r, now),
            }),
            policyVersion = OveragePolicy.PolicyVersion,
        }));
    }

    public sealed record RateBody(string Resource, Guid? PlanTemplateId, decimal? UnitPrice, int UnitSize = 1, string? Reason = null);

    /// <summary>ตั้งราคา — ปิดแถวเดิม + แถวใหม่ (ประวัติคงอยู่) · ราคา 0/ว่าง = ปิดราคา ⇒ ผู้เช่าเปิดส่วนเกินทรัพยากรนั้นไม่ได้ (ไม่คิด 0)</summary>
    [HttpPut("overage-rates")]
    public async Task<ActionResult<ApiResponse<object>>> SetRate([FromBody] RateBody b, CancellationToken ct)
    {
        await _overage.UpsertRateAsync(b.Resource ?? "", b.PlanTemplateId, b.UnitPrice, b.UnitSize, b.Reason ?? "", Actor(), ct);
        return Ok(new ApiResponse<object>(true, null, b.UnitPrice is > 0m
            ? "บันทึกราคาแล้ว — มีผลกับส่วนเกินที่เกิดหลังจากนี้ (ของที่บันทึกแล้วคงราคาเดิม)"
            : "ปิดราคาแล้ว — ผู้เช่าเปิดส่วนเกินของรายการนี้ไม่ได้จนกว่าจะตั้งราคาใหม่"));
    }

    [HttpGet("companies/{companyId:guid}/overage")]
    public async Task<ActionResult<ApiResponse<OverageMonthSummary>>> CompanyOverage(Guid companyId, CancellationToken ct)
    {
        if (!await _db.Companies.AnyAsync(c => c.Id == companyId, ct)) return NotFound(new ApiResponse<OverageMonthSummary>(false, null, "ไม่พบบริษัท"));
        return Ok(new ApiResponse<OverageMonthSummary>(true, await _overage.MonthSummaryAsync(companyId, null, ct)));
    }

    public sealed record CompanyPolicyBody(string Mode, decimal? MonthlyCapAmount, bool Unlock, string Reason);

    /// <summary>ตั้งแทนผู้เช่า / ปลดล็อกที่ระบบล็อกเพราะค้างชำระ — เหตุผลบังคับ · audit <c>PLATFORM-OVERAGE-POLICY</c>/<c>-UNLOCKED</c></summary>
    [HttpPut("companies/{companyId:guid}/overage")]
    public async Task<ActionResult<ApiResponse<OverageMonthSummary>>> SetCompanyPolicy(Guid companyId, [FromBody] CompanyPolicyBody b, CancellationToken ct)
    {
        if (!Enum.TryParse<OverageMode>(b.Mode, true, out var mode))
            return BadRequest(new ApiResponse<OverageMonthSummary>(false, null, "โหมดต้องเป็น HardLock หรือ AllowOverage"));
        var res = await _overage.AdminSetPolicyAsync(companyId, mode, b.MonthlyCapAmount, b.Unlock, b.Reason ?? "", Actor(), ct);
        return Ok(new ApiResponse<OverageMonthSummary>(true, res, b.Unlock ? "ปลดล็อกส่วนเกินแล้ว" : "บันทึกนโยบายส่วนเกินของบริษัทแล้ว"));
    }
}
