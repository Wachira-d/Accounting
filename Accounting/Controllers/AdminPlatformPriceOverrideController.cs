using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 231 (คำตัดสินข้อ 231-8 · LICENSE_USAGE_OVERAGE_AUDIT §C) — แอดมินแพลตฟอร์มจัดการ<b>ราคาพิเศษรายบริษัท/ราย License</b> (<c>CompanyPlanPriceOverride</c>)
/// <list type="bullet">
///   <item><c>GET api/admin/price-overrides?companyId=|accountSubscriptionId=</c> — แถวทั้งหมด (รวมที่ปิดแล้ว) + ตัวอย่างสด "ราคาที่มีผลต่อรอบ" ของทุกแพ็กเกจที่เปิดขาย (ตัวตัดสินเดียว <c>PlatformPriceOverrideStore</c>)</item>
///   <item><c>POST api/admin/price-overrides</c> — สร้าง (เหตุผลบังคับ · ตรวจโหมด · ห้ามซ้อนช่วงกับแถวที่ยังมีผลของ (ขอบเขต, แพ็กเกจ) ใต้ล็อก) · audit chain <c>PLATFORM-PRICE-OVERRIDE-SET</c> + <c>SubscriptionHistory</c></item>
///   <item><c>POST api/admin/price-overrides/{id}/end</c> — ปิด (ไม่ลบ · ไม่แก้ทับ — ต่อสัญญา = แถวใหม่) · audit <c>PLATFORM-PRICE-OVERRIDE-END</c></item>
/// </list>
/// ราคาพิเศษ<b>ไม่</b>แก้รายการชำระที่สร้างไว้แล้ว (ยอดประทับตอนสร้าง — แอดมินแก้ยอดครั้งเดียวได้ตามเดิม) · ใบต่ออายุที่ออกแล้วไม่ถูกแก้ (รอบถัดไปใช้ราคาใหม่)
/// </summary>
[ApiController]
[Route("api/admin/price-overrides")]
[Authorize(Roles = "SystemAdmin")]
public class AdminPlatformPriceOverrideController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public AdminPlatformPriceOverrideController(AccountingDbContext db) => _db = db;

    private string Actor => User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? User.Identity?.Name ?? "system-admin";
    private Guid ActorId => JwtHelper.GetUserIdFromClaims(User);

    public sealed record CreateBody(Guid? CompanyId, Guid? AccountSubscriptionId, Guid PlanTemplateId, PlanPriceOverrideMode Mode,
        Guid? ReferencePlanTemplateId, decimal? FixedMonthly, decimal? FixedQuarterly, decimal? FixedSemiAnnual, decimal? FixedAnnual, decimal? PercentOff,
        DateTime? EffectiveFromUtc, DateTime? EffectiveToUtc, bool AppliesToUpgrades = true, bool AppliesToRenewals = true, string? Reason = null);
    public sealed record EndBody(string? Reason);

    private static object Row(CompanyPlanPriceOverride o, IReadOnlyDictionary<Guid, string> names) => new
    {
        o.Id, o.CompanyId, o.AccountSubscriptionId, o.PlanTemplateId, planName = names.GetValueOrDefault(o.PlanTemplateId),
        mode = o.Mode.ToString(), o.ReferencePlanTemplateId, referencePlanName = o.ReferencePlanTemplateId is Guid r ? names.GetValueOrDefault(r) : null,
        o.FixedMonthly, o.FixedQuarterly, o.FixedSemiAnnual, o.FixedAnnual, o.PercentOff,
        o.EffectiveFromUtc, o.EffectiveToUtc, o.AppliesToUpgrades, o.AppliesToRenewals, o.Reason, o.ApprovedBy, o.CreatedAt,
        live = o.IsActive && o.EndedAtUtc == null && o.EffectiveFromUtc <= DateTime.UtcNow && (o.EffectiveToUtc == null || DateTime.UtcNow < o.EffectiveToUtc),
        o.EndedAtUtc, o.EndedBy, o.EndReason,
    };

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> List([FromQuery] Guid? companyId, [FromQuery] Guid? accountSubscriptionId, CancellationToken ct)
    {
        if (companyId == null && accountSubscriptionId == null)
            return BadRequest(new ApiResponse<object>(false, null, "ระบุ companyId หรือ accountSubscriptionId"));
        var rows = await _db.CompanyPlanPriceOverrides.AsNoTracking()
            .Where(o => (companyId != null && o.CompanyId == companyId) || (accountSubscriptionId != null && o.AccountSubscriptionId == accountSubscriptionId))
            .OrderByDescending(o => o.CreatedAt).ToListAsync(ct);
        var templates = await _db.PlanTemplates.AsNoTracking().IgnoreQueryFilters().OrderBy(t => t.MonthlyPrice).ToListAsync(ct);
        var names = templates.ToDictionary(t => t.Id, t => t.Name);
        var now = DateTime.UtcNow;
        // ตัวอย่างสด: ทุกแพ็กเกจที่เปิดขาย × 4 รอบ — ราคาตั้ง vs ราคาที่มีผล (ตัวตัดสินเดียวกับใบต่ออายุ/ส่วนต่าง/ดูราคา)
        Guid? acctForCompany = companyId is Guid cid
            ? await _db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == cid).Select(s => s.AccountSubscriptionId).FirstOrDefaultAsync(ct)
            : null;
        var preview = new List<object>();
        foreach (var t in templates.Where(t => t.IsActive && !t.IsDeleted && !t.IsPermanentFree))
        {
            var cycles = new List<object>();
            foreach (var c in new[] { BillingCycle.Monthly, BillingCycle.Quarterly, BillingCycle.SemiAnnual, BillingCycle.Annual })
            {
                var p = await PlatformPriceOverrideStore.ResolveAsync(_db, companyId, accountSubscriptionId ?? acctForCompany, t, c, now, forRenewal: true, ct);
                cycles.Add(new { cycle = c.ToString(), label = PlatformPlanPricing.CycleLabel(c), listPrice = p.ListPrice, price = p.Price, applied = p.Applied, source = p.Source, note = p.Note });
            }
            preview.Add(new { planTemplateId = t.Id, planName = t.Name, plan = t.Plan.ToString(), cycles });
        }
        return Ok(new ApiResponse<object>(true, new
        {
            items = rows.Select(o => Row(o, names)).ToList(),
            plans = templates.Where(t => t.IsActive && !t.IsDeleted).Select(t => new { id = t.Id, name = t.Name, plan = t.Plan.ToString(), t.IsPermanentFree }).ToList(),
            modes = Enum.GetNames<PlanPriceOverrideMode>(),
            preview,
        }));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> Create([FromBody] CreateBody b, CancellationToken ct)
    {
        if ((b.CompanyId == null) == (b.AccountSubscriptionId == null))
            return BadRequest(new ApiResponse<object>(false, null, "ระบุ companyId หรือ accountSubscriptionId อย่างใดอย่างหนึ่ง"));
        var reason = (b.Reason ?? "").Trim();
        if (reason.Length < 3) return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุเหตุผล (บันทึกใน audit)"));
        var target = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == b.PlanTemplateId && t.IsActive, ct);
        if (target == null) return BadRequest(new ApiResponse<object>(false, null, "ไม่พบแพ็กเกจที่ได้สิทธิ์ (ต้องเป็นแพ็กเกจที่เปิดใช้)"));
        if (target.IsPermanentFree) return BadRequest(new ApiResponse<object>(false, null, "แพ็กเกจฟรีถาวรไม่มีราคาให้ตั้งราคาพิเศษ"));
        switch (b.Mode)
        {
            case PlanPriceOverrideMode.PriceOfPlan:
                if (b.ReferencePlanTemplateId == null || b.ReferencePlanTemplateId == b.PlanTemplateId)
                    return BadRequest(new ApiResponse<object>(false, null, "โหมด \"ราคาของแพ็กเกจอื่น\" ต้องเลือกแพ็กเกจอ้างอิงที่ต่างจากแพ็กเกจที่ได้สิทธิ์"));
                if (!await _db.PlanTemplates.AsNoTracking().AnyAsync(t => t.Id == b.ReferencePlanTemplateId && t.IsActive, ct))
                    return BadRequest(new ApiResponse<object>(false, null, "ไม่พบแพ็กเกจอ้างอิง"));
                break;
            case PlanPriceOverrideMode.FixedPerCycle:
                if (b.FixedMonthly == null && b.FixedQuarterly == null && b.FixedSemiAnnual == null && b.FixedAnnual == null)
                    return BadRequest(new ApiResponse<object>(false, null, "โหมด \"กำหนดราคาเอง\" ต้องกรอกราคาอย่างน้อย 1 รอบ (รอบที่เว้นว่าง = ราคาแม่แบบ)"));
                if (new[] { b.FixedMonthly, b.FixedQuarterly, b.FixedSemiAnnual, b.FixedAnnual }.Any(v => v is decimal d && d < 0m))
                    return BadRequest(new ApiResponse<object>(false, null, "ราคาต้องไม่ติดลบ"));
                break;
            case PlanPriceOverrideMode.PercentOff:
                if (b.PercentOff is not decimal p || p <= 0m || p > 100m)
                    return BadRequest(new ApiResponse<object>(false, null, "ลด % ต้องอยู่ระหว่าง 0 ถึง 100"));
                break;
            default:
                return BadRequest(new ApiResponse<object>(false, null, "โหมดไม่ถูกต้อง"));
        }
        var from = b.EffectiveFromUtc ?? DateTime.UtcNow;
        // ฝ่ายค้าน S5: วันสิ้นสุดที่กรอกเป็น "วันที่" ⇒ สิ้นวันไทย (ครอบวันนั้นทั้งวัน · ตารางแสดงวันเดียวกับที่กรอก)
        DateTime? effectiveTo = b.EffectiveToUtc is DateTime toRaw ? PlatformPlanPricing.EndOfBangkokDayUtc(toRaw) : null;
        if (effectiveTo is DateTime to && to <= from)
            return BadRequest(new ApiResponse<object>(false, null, "วันสิ้นสุดต้องหลังวันเริ่ม"));

        Guid? subId = null;
        if (b.CompanyId is Guid cid)
        {
            subId = await _db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == cid).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            if (subId == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ subscription ของบริษัท"));
        }
        else if (!await _db.AccountSubscriptions.AsNoTracking().AnyAsync(a => a.Id == b.AccountSubscriptionId && !a.IsDeleted, ct))
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบ License"));

        // ห้ามซ้อนช่วงกับแถวที่ยังมีผลของ (ขอบเขต, แพ็กเกจ) — ตรวจใต้ล็อกของขอบเขต (ปิดแถวเดิมก่อนแล้วค่อยสร้างใหม่)
        // ฝ่ายค้าน S7: แถวบริษัทชนะแถว License ⇒ ถือล็อกของ License ที่บริษัทผูกด้วย (ลำดับคงที่ License → บริษัท กันเดดล็อก) — advisory xact lock ถือซ้อนในธุรกรรมเดียวได้
        var acctLockId = b.AccountSubscriptionId
            ?? await _db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == b.CompanyId).Select(s => s.AccountSubscriptionId).FirstOrDefaultAsync(ct);
        await using var lockTx = acctLockId is Guid acctLock
            ? await SubscriptionPaymentLock.AcquireAsync(_db, acctLock, ct)
            : await SubscriptionPaymentLock.AcquireAsync(_db, b.CompanyId!.Value, ct);
        if (acctLockId != null && b.CompanyId is Guid companyLock)
            await SubscriptionPaymentLock.AcquireAsync(_db, companyLock, ct);   // คืน null (ธุรกรรมเดิม)
        var overlapping = await _db.CompanyPlanPriceOverrides
            .Where(o => o.PlanTemplateId == b.PlanTemplateId && o.IsActive && o.EndedAtUtc == null
                && ((b.CompanyId != null && o.CompanyId == b.CompanyId) || (b.AccountSubscriptionId != null && o.AccountSubscriptionId == b.AccountSubscriptionId))
                && (o.EffectiveToUtc == null || o.EffectiveToUtc > from) && (effectiveTo == null || o.EffectiveFromUtc < effectiveTo))
            .Select(o => o.Id).FirstOrDefaultAsync(ct);
        if (overlapping != Guid.Empty)
            return Conflict(new ApiResponse<object>(false, null, $"มีราคาพิเศษของแพ็กเกจนี้ที่ยังมีผลในช่วงเดียวกันอยู่แล้ว ({overlapping.ToString()[..8].ToUpper()}) — ปิดแถวเดิมก่อน (ไม่แก้ทับ)"));

        var row = new CompanyPlanPriceOverride
        {
            CompanyId = b.CompanyId, AccountSubscriptionId = b.AccountSubscriptionId, PlanTemplateId = b.PlanTemplateId, Mode = b.Mode,
            ReferencePlanTemplateId = b.Mode == PlanPriceOverrideMode.PriceOfPlan ? b.ReferencePlanTemplateId : null,
            FixedMonthly = b.Mode == PlanPriceOverrideMode.FixedPerCycle ? b.FixedMonthly : null,
            FixedQuarterly = b.Mode == PlanPriceOverrideMode.FixedPerCycle ? b.FixedQuarterly : null,
            FixedSemiAnnual = b.Mode == PlanPriceOverrideMode.FixedPerCycle ? b.FixedSemiAnnual : null,
            FixedAnnual = b.Mode == PlanPriceOverrideMode.FixedPerCycle ? b.FixedAnnual : null,
            PercentOff = b.Mode == PlanPriceOverrideMode.PercentOff ? b.PercentOff : null,
            EffectiveFromUtc = from, EffectiveToUtc = effectiveTo, AppliesToUpgrades = b.AppliesToUpgrades, AppliesToRenewals = b.AppliesToRenewals,
            Reason = reason.Length > 500 ? reason[..500] : reason, ApprovedBy = Actor, IsActive = true, CreatedBy = Actor,
        };
        _db.CompanyPlanPriceOverrides.Add(row);
        var annual = PlatformPlanPricing.EffectiveCyclePrice(target, BillingCycle.Annual, PlatformPriceOverrideStore.ToInput(row),
            row.ReferencePlanTemplateId is Guid rid ? await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == rid, ct) : null, from, forRenewal: true);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = b.CompanyId, UserId = ActorId, UserEmail = Actor, Action = AuditAction.Create,
            EntityType = nameof(CompanyPlanPriceOverride), EntityId = row.Id.ToString(),
            OldValues = null,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                row.CompanyId, row.AccountSubscriptionId, row.PlanTemplateId, mode = row.Mode.ToString(), row.ReferencePlanTemplateId,
                row.FixedMonthly, row.FixedQuarterly, row.FixedSemiAnnual, row.FixedAnnual, row.PercentOff, row.EffectiveFromUtc, row.EffectiveToUtc,
                row.AppliesToUpgrades, row.AppliesToRenewals, reason, annualListPrice = annual.ListPrice, annualPrice = annual.Price, ruleCode = "PLATFORM-PRICE-OVERRIDE-SET",
            }),
            IpAddress = HttpContext?.Connection.RemoteIpAddress?.ToString(), Timestamp = DateTime.UtcNow,
        });
        var childSubs = subId is Guid s ? new List<Guid> { s }
            : await _db.Subscriptions.AsNoTracking().Where(x => x.AccountSubscriptionId == b.AccountSubscriptionId && !x.IsDeleted).Select(x => x.Id).ToListAsync(ct);
        foreach (var sid in childSubs)
            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sid, AccountSubscriptionId = b.AccountSubscriptionId, Action = "PriceOverrideSet", ToPlan = target.Plan,
                Notes = $"ราคาพิเศษ {target.Name}: {row.Mode} · รายปี {annual.ListPrice:N2} → {annual.Price:N2} · มีผล {from:yyyy-MM-dd}"
                        + (row.EffectiveToUtc is DateTime e ? $" ถึง {e:yyyy-MM-dd}" : " (ตลอดไป)") + $" — เหตุผล: {reason}",
                PerformedBy = ActorId.ToString(),   // ฝ่ายค้าน N5: PerformedBy = userId เหมือน License/AdminChangeLimits
            });
        await _db.SaveChangesAsync(ct);
        if (lockTx != null) await lockTx.CommitAsync(ct);
        return Ok(new ApiResponse<object>(true, new { row.Id, annualListPrice = annual.ListPrice, annualPrice = annual.Price },
            $"บันทึกราคาพิเศษแล้ว — ต่ออายุรายปีถัดไปจะคิด {annual.Price:N2} บาท (ราคาตั้ง {annual.ListPrice:N2}) · รายการชำระ/ใบที่ออกแล้วไม่ถูกแก้"));
    }

    [HttpPost("{id:guid}/end")]
    public async Task<ActionResult<ApiResponse<object>>> End(Guid id, [FromBody] EndBody b, CancellationToken ct)
    {
        var reason = (b.Reason ?? "").Trim();
        if (reason.Length < 3) return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุเหตุผล (บันทึกใน audit)"));
        var row = await _db.CompanyPlanPriceOverrides.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (row == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบราคาพิเศษ"));
        // ฝ่ายค้าน N2: ปิดใต้ล็อกเดียวกับสร้าง/รายการชำระ (กันสร้างทับ/คิดราคาคร่อมตอนปิด) — ลำดับ License → บริษัท
        var endAcctLock = row.AccountSubscriptionId
            ?? await _db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == row.CompanyId).Select(s => s.AccountSubscriptionId).FirstOrDefaultAsync(ct);
        await using var endTx = endAcctLock is Guid eal
            ? await SubscriptionPaymentLock.AcquireAsync(_db, eal, ct)
            : await SubscriptionPaymentLock.AcquireAsync(_db, row.CompanyId!.Value, ct);
        if (endAcctLock != null && row.CompanyId is Guid endCompanyLock)
            await SubscriptionPaymentLock.AcquireAsync(_db, endCompanyLock, ct);
        await _db.Entry(row).ReloadAsync(ct);
        if (row.EndedAtUtc != null || !row.IsActive) return Ok(new ApiResponse<object>(true, null, "ปิดไปแล้ว"));
        var now = DateTime.UtcNow;
        row.IsActive = false; row.EndedAtUtc = now; row.EndedBy = Actor; row.EndReason = reason.Length > 500 ? reason[..500] : reason;
        row.UpdatedAt = now; row.UpdatedBy = Actor;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = row.CompanyId, UserId = ActorId, UserEmail = Actor, Action = AuditAction.Update,
            EntityType = nameof(CompanyPlanPriceOverride), EntityId = row.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { isActive = true, endedAtUtc = (DateTime?)null }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { isActive = false, endedAtUtc = now, reason, ruleCode = "PLATFORM-PRICE-OVERRIDE-END" }),
            IpAddress = HttpContext?.Connection.RemoteIpAddress?.ToString(), Timestamp = now,
        });
        var childSubs = row.CompanyId is Guid cid
            ? await _db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == cid).Select(s => s.Id).ToListAsync(ct)
            : await _db.Subscriptions.AsNoTracking().Where(s => s.AccountSubscriptionId == row.AccountSubscriptionId && !s.IsDeleted).Select(s => s.Id).ToListAsync(ct);
        foreach (var sid in childSubs)
            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sid, AccountSubscriptionId = row.AccountSubscriptionId, Action = "PriceOverrideEnded",
                Notes = $"ปิดราคาพิเศษ {row.Id.ToString()[..8].ToUpper()} — กลับไปราคาตั้ง · เหตุผล: {reason}", PerformedBy = ActorId.ToString(),   // ฝ่ายค้าน N5: PerformedBy = userId เหมือน License/AdminChangeLimits
            });
        await _db.SaveChangesAsync(ct);
        if (endTx != null) await endTx.CommitAsync(ct);
        return Ok(new ApiResponse<object>(true, null, "ปิดราคาพิเศษแล้ว — รอบถัดไปคิดราคาตั้ง (รายการที่สร้างไว้แล้วไม่ถูกแก้)"));
    }
}
