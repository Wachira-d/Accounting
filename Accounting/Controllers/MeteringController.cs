using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// ฝั่งลูกค้า: เลือกฟีเจอร์ที่จะใช้ + ดูยอดการใช้งานของตัวเอง
/// (ACCOUNT_STRUCTURE.md §7.1, §8)
///
/// **PDPA**: endpoint ชุดนี้คืนเฉพาะ metadata การใช้งาน (ฟีเจอร์/จำนวน/ยอดเงิน/
/// เอกสารอ้างอิง) — ห้ามคืน prompt หรือเนื้อหาที่ส่งไป AI เด็ดขาด
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/metering")]
[Authorize]
public class MeteringController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IUsageMeteringService _metering;

    public MeteringController(AccountingDbContext db, IUsageMeteringService metering)
    { _db = db; _metering = metering; }

    /// <summary>
    /// ฟีเจอร์ที่เปิดขายทั้งหมด + สถานะเปิด/ปิดของบริษัทนี้ + **ราคาที่มีผลตอนนี้**
    ///
    /// ลูกค้าต้องเห็นราคาก่อนกดเปิดเสมอ — การกดเปิดคือการยอมรับค่าใช้จ่าย
    /// </summary>
    [HttpGet("features")]
    public async Task<ActionResult<ApiResponse<object>>> GetFeatures(Guid companyId)
    {
        var now = DateTime.UtcNow;
        var accountId = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.BillingAccountId).FirstOrDefaultAsync();

        var features = await _db.ApiFeatures.AsNoTracking()
            .Where(f => f.IsPublished)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToListAsync();

        var enabled = await _db.CompanyFeatures.AsNoTracking()
            .Where(f => f.CompanyId == companyId)
            .ToDictionaryAsync(f => f.FeatureCode, f => f);

        var plans = await _db.ApiPricingPlans.AsNoTracking()
            .Where(p => p.EffectiveFrom <= now && (p.EffectiveTo == null || p.EffectiveTo > now)
                     && (p.BillingAccountId == null || p.BillingAccountId == accountId))
            .ToListAsync();

        // ฟีเจอร์ที่บริษัทเปิดใช้อยู่แต่ถูก unpublish ไปแล้ว ต้องยังเห็นในรายการ
        // (ใช้ต่อได้ + ปิดเองได้) — ไม่งั้นลูกค้าเจอค่าใช้จ่ายจากของที่มองไม่เห็น
        var extraCodes = enabled.Values.Where(e => e.IsEnabled)
            .Select(e => e.FeatureCode)
            .Except(features.Select(f => f.FeatureCode)).ToList();
        if (extraCodes.Count > 0)
            features.AddRange(await _db.ApiFeatures.AsNoTracking()
                .Where(f => extraCodes.Contains(f.FeatureCode)).ToListAsync());

        return Ok(new ApiResponse<object>(true, features.Select(f =>
        {
            // ราคาเฉพาะกลุ่มชนะราคามาตรฐาน (ต้องตรงกับ UsageMeteringService.ResolvePlanAsync)
            var plan = plans.Where(p => p.FeatureCode == f.FeatureCode)
                .OrderByDescending(p => p.BillingAccountId.HasValue)
                .ThenByDescending(p => p.EffectiveFrom)
                .FirstOrDefault();
            enabled.TryGetValue(f.FeatureCode, out var state);
            return new
            {
                f.FeatureCode, f.Name, f.NameEn, f.Description, f.UnitLabel,
                isPublished = f.IsPublished,
                isEnabled = state?.IsEnabled ?? false,
                enabledAt = state?.EnabledAt,
                enabledBy = state?.EnabledBy,
                pricing = plan == null ? null : new
                {
                    method = plan.Method.ToString(),
                    unitPrice = plan.UnitPrice,
                    freeQuotaPerMonth = plan.FreeQuotaPerMonth,
                    tierJson = plan.TierJson,
                },
                // ยังไม่ตั้งราคา = เปิดใช้ได้แต่ยังไม่คิดเงิน — บอกตรง ๆ ไม่ปล่อยให้เดา
                priceNote = plan == null ? "ยังไม่ได้กำหนดราคา — ใช้งานได้โดยยังไม่มีค่าใช้จ่าย" : null,
            };
        })));
    }

    public record ToggleFeatureRequest(bool Enabled);

    /// <summary>เปิด/ปิดฟีเจอร์ — เป็นสวิตช์ค่าใช้จ่ายจริง จึงบันทึกว่าใครกดตอนไหน</summary>
    [HttpPost("features/{featureCode}")]
    public async Task<ActionResult<ApiResponse<object>>> ToggleFeature(
        Guid companyId, string featureCode, [FromBody] ToggleFeatureRequest req)
    {
        var feature = await _db.ApiFeatures.AsNoTracking()
            .FirstOrDefaultAsync(f => f.FeatureCode == featureCode);
        if (feature == null)
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบฟีเจอร์นี้"));

        // เปิดใหม่ได้เฉพาะฟีเจอร์ที่ยังเปิดขาย — แต่ "ปิด" ทำได้เสมอ
        // (ลูกค้าต้องถอนตัวจากค่าใช้จ่ายได้ตลอดเวลา)
        if (req.Enabled && !feature.IsPublished)
            return BadRequest(new ApiResponse<string>(false, null,
                "ฟีเจอร์นี้ปิดรับผู้ใช้ใหม่แล้ว — กรุณาติดต่อผู้ดูแลระบบ"));

        var actor = User.Identity?.Name ?? "unknown";
        await _metering.SetFeatureEnabledAsync(companyId, featureCode, req.Enabled, actor);

        return Ok(new ApiResponse<object>(true, new { featureCode, enabled = req.Enabled },
            req.Enabled ? $"เปิดใช้ {feature.Name} แล้ว" : $"ปิด {feature.Name} แล้ว — หยุดคิดค่าใช้จ่ายทันที"));
    }

    /// <summary>ยอดใช้งานรายเดือนของบริษัทนี้</summary>
    [HttpGet("usage")]
    public async Task<ActionResult<ApiResponse<object>>> GetUsage(
        Guid companyId, [FromQuery] int? year = null, [FromQuery] int? month = null)
    {
        var now = DateTime.UtcNow;
        var rows = await _metering.GetMonthlyUsageAsync(companyId, year ?? now.Year, month ?? now.Month);
        return Ok(new ApiResponse<object>(true, new
        {
            period = $"{year ?? now.Year:0000}-{month ?? now.Month:00}",
            totalCharged = rows.Sum(r => r.TotalCharged),
            items = rows,
        }));
    }

    /// <summary>
    /// ยอดรวมทั้งกลุ่ม — ต้องเป็นผู้ดูแลกลุ่ม (`BillingAccountAdmin`) เท่านั้น
    ///
    /// การเป็นสมาชิกบริษัทใดบริษัทหนึ่งในกลุ่ม **ไม่ให้สิทธิ์เห็นยอดของบริษัทอื่น**
    /// — แยก "บริหารกลุ่ม" ออกจาก "เป็นพนักงานบริษัทหนึ่ง" ตามหลัก §8
    /// </summary>
    [HttpGet("usage/account")]
    public async Task<ActionResult<ApiResponse<object>>> GetAccountUsage(
        Guid companyId, [FromQuery] int? year = null, [FromQuery] int? month = null)
    {
        var accountId = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.BillingAccountId).FirstOrDefaultAsync();
        if (accountId == null)
            return BadRequest(new ApiResponse<string>(false, null, "บริษัทนี้ไม่ได้สังกัดกลุ่มผู้จ่ายเงิน"));

        var userId = Helpers.JwtHelper.GetUserIdFromClaims(User);
        var isAdmin = await _db.BillingAccountAdmins.AsNoTracking()
            .AnyAsync(a => a.BillingAccountId == accountId.Value && a.UserId == userId);
        if (!isAdmin)
            return StatusCode(403, new ApiResponse<string>(false, null,
                "ต้องเป็นผู้ดูแลกลุ่มจึงจะดูยอดรวมทุกบริษัทได้"));

        var now = DateTime.UtcNow;
        var rows = await _metering.GetAccountMonthlyUsageAsync(
            accountId.Value, year ?? now.Year, month ?? now.Month);

        var account = await _db.BillingAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId.Value);

        return Ok(new ApiResponse<object>(true, new
        {
            period = $"{year ?? now.Year:0000}-{month ?? now.Month:00}",
            accountName = account?.Name,
            paymentModel = account?.PaymentModel.ToString(),
            creditBalance = account?.CreditBalance,
            totalCharged = rows.Sum(r => r.TotalCharged),
            byCompany = rows.GroupBy(r => new { r.CompanyId, r.CompanyName })
                .Select(g => new
                {
                    g.Key.CompanyId, g.Key.CompanyName,
                    charged = g.Sum(x => x.TotalCharged),
                    items = g.ToList(),
                })
                .OrderByDescending(x => x.charged),
        }));
    }
}
