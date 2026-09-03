using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// จัดการ **แคตตาล็อกฟีเจอร์ + ราคา** ของผลิตภัณฑ์ API (ACCOUNT_STRUCTURE.md §7.1)
///
/// เฉพาะ SystemAdmin — ราคาเป็นการตัดสินใจทางธุรกิจ ลูกค้าเลือกได้แค่ว่าจะ
/// "เปิดใช้ฟีเจอร์นี้ที่ราคานี้ไหม" (ผ่าน portal) ไม่ใช่ตั้งราคาเอง
/// </summary>
[ApiController]
[Route("api/admin/metering")]
[Authorize(Roles = "SystemAdmin")]
public class MeteringAdminController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public MeteringAdminController(AccountingDbContext db) { _db = db; }

    // ──────────────────────────────────────────────────────────
    //  ฟีเจอร์ในแคตตาล็อก
    // ──────────────────────────────────────────────────────────

    /// <summary>รายการฟีเจอร์ทั้งหมด + แผนราคาที่มีผลอยู่ตอนนี้</summary>
    [HttpGet("features")]
    public async Task<ActionResult<ApiResponse<object>>> GetFeatures()
    {
        var now = DateTime.UtcNow;
        var features = await _db.ApiFeatures.AsNoTracking()
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToListAsync();

        var plans = await _db.ApiPricingPlans.AsNoTracking()
            .Where(p => p.EffectiveFrom <= now && (p.EffectiveTo == null || p.EffectiveTo > now))
            .ToListAsync();

        // จำนวนบริษัทที่เปิดใช้ — ให้ admin เห็นว่าฟีเจอร์ไหนมีคนพึ่งพาอยู่
        // ก่อนกด unpublish (ถอนของที่ลูกค้าใช้อยู่กลางคันไม่ได้)
        var enabledCounts = await _db.CompanyFeatures.AsNoTracking()
            .Where(c => c.IsEnabled)
            .GroupBy(c => c.FeatureCode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Code, x => x.Count);

        return Ok(new ApiResponse<object>(true, features.Select(f => new
        {
            f.Id, f.FeatureCode, f.Name, f.NameEn, f.Description, f.UnitLabel,
            f.IsPublished, f.RequiredScopes, f.SortOrder,
            f.Icon, f.ModuleCode, f.TrialDays, f.MinPlanCsv,
            kind = f.Kind.ToString(),
            enabledCompanies = enabledCounts.GetValueOrDefault(f.FeatureCode, 0),
            // แผนมาตรฐาน (BillingAccountId = null) แสดงเป็นราคาหลัก;
            // ดีลเฉพาะกลุ่มนับแยกให้เห็นว่ามีกี่ดีล
            standardPlan = plans
                .Where(p => p.FeatureCode == f.FeatureCode && p.BillingAccountId == null)
                .OrderByDescending(p => p.EffectiveFrom)
                .Select(p => new
                {
                    p.Id, method = p.Method.ToString(), p.UnitPrice,
                    p.FreeQuotaPerMonth, p.TierJson, p.EffectiveFrom, p.EffectiveTo, p.AdminNote
                })
                .FirstOrDefault(),
            customPlanCount = plans.Count(p => p.FeatureCode == f.FeatureCode && p.BillingAccountId != null),
        })));
    }

    /// <summary>ช่องท้าย 5 ตัวเป็น nullable ทั้งหมด = "ไม่ได้ส่งมา ให้คงค่าเดิม"
    /// (ฟอร์มเก่าที่ยังไม่รู้จักช่องพวกนี้ต้องไม่ล้างค่าที่ตั้งไว้แล้วโดยไม่ตั้งใจ)</summary>
    public record UpsertFeatureRequest(
        string FeatureCode, string Name, string? NameEn, string? Description,
        string UnitLabel, string RequiredScopes, int SortOrder, bool IsPublished,
        string? Kind = null, string? Icon = null, string? ModuleCode = null,
        int? TrialDays = null, string? MinPlanCsv = null);

    [HttpPost("features")]
    public async Task<ActionResult<ApiResponse<object>>> UpsertFeature([FromBody] UpsertFeatureRequest req)
    {
        var code = (req.FeatureCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาระบุรหัสฟีเจอร์"));

        var row = await _db.ApiFeatures.FirstOrDefaultAsync(f => f.FeatureCode == code);
        if (row == null)
        {
            row = new ApiFeature { FeatureCode = code, CreatedBy = User.Identity?.Name };
            _db.ApiFeatures.Add(row);
        }
        // FeatureCode เปลี่ยนไม่ได้หลังสร้าง — ผูกกับ UsageEvent ย้อนหลังและ
        // contract ที่ลูกค้าเขียนโค้ดไว้แล้ว
        row.Name = req.Name?.Trim() ?? code;
        row.NameEn = req.NameEn?.Trim();
        row.Description = req.Description?.Trim();
        row.UnitLabel = string.IsNullOrWhiteSpace(req.UnitLabel) ? "รายการ" : req.UnitLabel.Trim();
        row.RequiredScopes = req.RequiredScopes?.Trim() ?? "";
        row.SortOrder = req.SortOrder;
        row.IsPublished = req.IsPublished;
        if (req.Kind != null && Enum.TryParse<ApiFeatureKind>(req.Kind, true, out var kind)) row.Kind = kind;
        if (req.Icon != null) row.Icon = req.Icon.Trim();
        if (req.ModuleCode != null) row.ModuleCode = req.ModuleCode.Trim();
        if (req.TrialDays.HasValue) row.TrialDays = Math.Max(0, req.TrialDays.Value);
        if (req.MinPlanCsv != null) row.MinPlanCsv = req.MinPlanCsv.Trim();
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { row.Id, row.FeatureCode }, "บันทึกฟีเจอร์สำเร็จ"));
    }

    // ──────────────────────────────────────────────────────────
    //  แผนราคา
    // ──────────────────────────────────────────────────────────

    public record UpsertPlanRequest(
        string FeatureCode, string Method, decimal UnitPrice, int FreeQuotaPerMonth,
        string? TierJson, DateTime? EffectiveFrom, Guid? BillingAccountId, string? AdminNote);

    /// <summary>
    /// ตั้งราคาใหม่ — **ไม่แก้แผนเดิม** แต่ปิดแผนเดิม (`EffectiveTo = now`)
    /// แล้วเปิดแผนใหม่ต่อ. ทำแบบนี้เพราะบิลที่ออกไปแล้วอ้างราคา ณ วันนั้น
    /// ถ้าแก้แผนเดิมทับ ประวัติจะเปลี่ยนย้อนหลังและอธิบายกับลูกค้าไม่ได้
    /// </summary>
    [HttpPost("plans")]
    public async Task<ActionResult<ApiResponse<object>>> UpsertPlan([FromBody] UpsertPlanRequest req)
    {
        var code = (req.FeatureCode ?? "").Trim();
        if (!await _db.ApiFeatures.AnyAsync(f => f.FeatureCode == code))
            return BadRequest(new ApiResponse<string>(false, null, $"ไม่พบฟีเจอร์รหัส {code}"));

        if (!Enum.TryParse<PricingMethod>(req.Method, true, out var method))
            return BadRequest(new ApiResponse<string>(false, null,
                "วิธีคิดเงินต้องเป็น PerUnit / Tiered / FlatMonthly / PerCall"));

        if (req.UnitPrice < 0)
            return BadRequest(new ApiResponse<string>(false, null, "ราคาต้องไม่ติดลบ"));

        var from = req.EffectiveFrom ?? DateTime.UtcNow;

        // ปิดแผนเดิมในขอบเขตเดียวกัน (มาตรฐาน หรือของกลุ่มนั้น) ที่ยังเปิดอยู่
        var current = await _db.ApiPricingPlans
            .Where(p => p.FeatureCode == code
                     && p.BillingAccountId == req.BillingAccountId
                     && (p.EffectiveTo == null || p.EffectiveTo > from))
            .ToListAsync();
        foreach (var p in current)
        {
            p.EffectiveTo = from;
            p.UpdatedAt = DateTime.UtcNow;
            p.UpdatedBy = User.Identity?.Name;
        }

        var plan = new ApiPricingPlan
        {
            FeatureCode = code,
            BillingAccountId = req.BillingAccountId,
            Method = method,
            UnitPrice = req.UnitPrice,
            FreeQuotaPerMonth = Math.Max(0, req.FreeQuotaPerMonth),
            TierJson = string.IsNullOrWhiteSpace(req.TierJson) ? null : req.TierJson.Trim(),
            EffectiveFrom = from,
            AdminNote = req.AdminNote?.Trim(),
            CreatedBy = User.Identity?.Name,
        };
        _db.ApiPricingPlans.Add(plan);
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<object>(true, new { plan.Id, plan.EffectiveFrom },
            current.Count > 0
                ? $"ตั้งราคาใหม่สำเร็จ — ปิดแผนเดิม {current.Count} แผน ณ {from:dd/MM/yyyy HH:mm} (บิลก่อนหน้าไม่เปลี่ยน)"
                : "ตั้งราคาสำเร็จ"));
    }

    /// <summary>ประวัติราคาของฟีเจอร์ — ใช้ตอบลูกค้าว่า ณ วันนั้นราคาเท่าไร</summary>
    [HttpGet("plans/{featureCode}")]
    public async Task<ActionResult<ApiResponse<object>>> GetPlanHistory(string featureCode)
    {
        // materialize ก่อนแล้วค่อย project — enum.ToString() แปลเป็น SQL ไม่ได้
        var plans = await _db.ApiPricingPlans.AsNoTracking()
            .Where(p => p.FeatureCode == featureCode)
            .OrderByDescending(p => p.EffectiveFrom)
            .ToListAsync();

        var rows = plans.Select(p => new
        {
            p.Id, p.BillingAccountId, method = p.Method.ToString(),
            p.UnitPrice, p.FreeQuotaPerMonth, p.TierJson,
            p.EffectiveFrom, p.EffectiveTo, p.AdminNote, p.CreatedBy, p.CreatedAt
        }).ToList();
        return Ok(new ApiResponse<object>(true, rows));
    }

    // ──────────────────────────────────────────────────────────
    //  ภาพรวมการใช้งานทั้งระบบ
    // ──────────────────────────────────────────────────────────

    /// <summary>ยอดใช้งาน+รายได้รายเดือน แยกตามฟีเจอร์ และ top ลูกค้า</summary>
    [HttpGet("usage")]
    public async Task<ActionResult<ApiResponse<object>>> GetUsage(
        [FromQuery] int? year = null, [FromQuery] int? month = null)
    {
        var now = DateTime.UtcNow;
        var y = year ?? now.Year;
        var m = month ?? now.Month;
        var from = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddMonths(1);

        var events = await _db.UsageEvents.AsNoTracking()
            .Where(u => u.OccurredAt >= from && u.OccurredAt < to)
            .Select(u => new
            {
                u.CompanyId, u.BillingAccountId, u.FeatureCode,
                u.Quantity, u.ChargedAmount, u.IsSandbox, u.CoveredByFreeQuota
            })
            .ToListAsync();

        var byFeature = events.GroupBy(e => e.FeatureCode).Select(g => new
        {
            feature = g.Key,
            quantity = g.Sum(x => x.Quantity),
            revenue = g.Sum(x => x.ChargedAmount),
            freeQuantity = g.Where(x => x.CoveredByFreeQuota).Sum(x => x.Quantity),
            sandboxQuantity = g.Where(x => x.IsSandbox).Sum(x => x.Quantity),
        }).OrderByDescending(x => x.revenue).ToList();

        var accountIds = events.Where(e => e.BillingAccountId.HasValue)
            .Select(e => e.BillingAccountId!.Value).Distinct().ToList();
        var accountNames = accountIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.BillingAccounts.AsNoTracking()
                .Where(a => accountIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Name);

        var byAccount = events.Where(e => e.BillingAccountId.HasValue)
            .GroupBy(e => e.BillingAccountId!.Value)
            .Select(g => new
            {
                billingAccountId = g.Key,
                name = accountNames.GetValueOrDefault(g.Key, "(ไม่พบชื่อ)"),
                quantity = g.Sum(x => x.Quantity),
                revenue = g.Sum(x => x.ChargedAmount),
            })
            .OrderByDescending(x => x.revenue).Take(20).ToList();

        return Ok(new ApiResponse<object>(true, new
        {
            period = $"{y:0000}-{m:00}",
            totalEvents = events.Count,
            totalQuantity = events.Sum(e => e.Quantity),
            totalRevenue = events.Sum(e => e.ChargedAmount),
            billableCompanies = events.Where(e => !e.IsSandbox).Select(e => e.CompanyId).Distinct().Count(),
            byFeature,
            topAccounts = byAccount,
        }));
    }
}
