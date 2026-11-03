using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §B.7/§D.2 · คำตัดสินข้อ 231-1..7) — ฝั่งผู้เช่า: <b>นโยบายส่วนเกิน + ส่วนเกินเดือนนี้ + ประวัติการใช้งาน</b> (hub แท็บ "ใช้ไปเท่าไร")
/// <list type="bullet">
/// <item><c>GET overage/month</c> — นโยบาย · ราคาที่มีผล · ส่วนเกินเดือนนี้ต่อทรัพยากร · ประมาณการบิล (เซิร์ฟเวอร์คิด หน้าเว็บแสดง)</item>
/// <item><c>PUT overage-policy</c> — เลือก "หยุดส่วนที่เกิน" / "ใช้ต่อได้ คิดค่าส่วนเกิน" + เพดานเงิน + ทรัพยากร · ต้องมีสิทธิ์ <c>BillingManage</c> (ก่อค่าใช้จ่ายให้บริษัท) + ยอมรับเงื่อนไขฉบับปัจจุบัน</item>
/// <item><c>GET history?months=12</c> — เดือนนี้ (สด) + ย้อนหลังจาก snapshot (≤ 24 เดือน) ทุกทรัพยากร + AI (ข้อมูลอย่างเดียว)</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/quota")]
[Authorize]
public class QuotaOverageController : ControllerBase
{
    private readonly IOverageService _overage;
    private readonly IUsageSnapshotService _snapshots;

    public QuotaOverageController(IOverageService overage, IUsageSnapshotService snapshots) { _overage = overage; _snapshots = snapshots; }

    private PlatformBillingActor Actor() => new(JwtHelper.GetUserIdFromClaims(User), User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString());

    [HttpGet("overage/month")]
    public async Task<ActionResult<ApiResponse<OverageMonthSummary>>> Month(Guid companyId, [FromQuery] string? period, CancellationToken ct)
    {
        if (period != null && !UsageHistoryRule.TryParsePeriod(period, out _, out _))
            return BadRequest(new ApiResponse<OverageMonthSummary>(false, null, "งวดต้องเป็น yyyy-MM"));
        return Ok(new ApiResponse<OverageMonthSummary>(true, await _overage.MonthSummaryAsync(companyId, period, ct)));
    }

    public sealed record PolicyBody(string Mode, decimal? MonthlyCapAmount, List<string>? Resources, bool PolicyAccepted, string? PolicyVersionShown);

    [HttpPut("overage-policy")]
    [Accounting.Filters.RejectApiKey("ตั้งนโยบายส่วนเกินโควตา")]
    [Accounting.Filters.RequirePermission(PermissionKeys.BillingManage)]
    public async Task<ActionResult<ApiResponse<OverageMonthSummary>>> SetPolicy(Guid companyId, [FromBody] PolicyBody body, CancellationToken ct)
    {
        if (!Enum.TryParse<OverageMode>(body.Mode, true, out var mode))
            return BadRequest(new ApiResponse<OverageMonthSummary>(false, null, "โหมดต้องเป็น HardLock หรือ AllowOverage"));
        var res = await _overage.SetPolicyAsync(companyId, new OveragePolicyUpdate(mode, body.MonthlyCapAmount, body.Resources, body.PolicyAccepted, body.PolicyVersionShown), Actor(), ct);
        return Ok(new ApiResponse<OverageMonthSummary>(true, res, mode == OverageMode.AllowOverage
            ? "เปิด \"ใช้ต่อได้ คิดค่าส่วนเกิน\" แล้ว — ส่วนเกินเข้าใบแจ้งหนี้ค่าใช้งานทุกต้นเดือน"
            : "ตั้งเป็น \"หยุดส่วนที่เกิน\" แล้ว — ใบกำกับภาษี/ใบเสร็จ/ใบลดหนี้ยังออกได้เสมอโดยไม่คิดเงิน"));
    }

    [HttpGet("history")]
    public async Task<ActionResult<ApiResponse<UsageHistoryResponse>>> History(Guid companyId, [FromQuery] int months = UsageHistoryRule.DefaultHistoryMonths, CancellationToken ct = default)
        => Ok(new ApiResponse<UsageHistoryResponse>(true, await _snapshots.HistoryAsync(companyId, Math.Clamp(months, 1, UsageHistoryRule.RetentionMonths), ct)));
}
