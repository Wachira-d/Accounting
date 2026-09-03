using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>resolver สิทธิ์ตัวเดียว — ดูเหตุผลเชิงออกแบบที่ <see cref="IEntitlementService"/></summary>
public class EntitlementService : IEntitlementService
{
    private readonly AccountingDbContext _db;
    private readonly ISubscriptionService _subscriptions;
    private readonly ILogger<EntitlementService> _logger;

    public EntitlementService(AccountingDbContext db, ISubscriptionService subscriptions,
        ILogger<EntitlementService> logger)
    {
        _db = db;
        _subscriptions = subscriptions;
        _logger = logger;
    }

    public async Task<EntitlementResult> CheckAsync(Guid companyId, FeatureFlags flag, CancellationToken ct = default)
        => await CheckAsync(companyId, flag.ToString(), ct);

    public async Task<EntitlementResult> CheckAsync(Guid companyId, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return EntitlementResult.Ok();

        // ① แพ็กเกจต้องยังไม่หมดอายุ (รวม grace) — ใช้ resolver เดิมตัวเดียว
        var eff = await _subscriptions.GetEffectivePlanAsync(companyId);
        if (eff == null)
            return new EntitlementResult(false, "บริษัทนี้ยังไม่มีแพ็กเกจ", "เลือกแพ็กเกจเพื่อเริ่มใช้งาน");
        if (!eff.IsActive)
            return new EntitlementResult(false,
                $"แพ็กเกจหมดอายุเมื่อ {eff.EndDate.AddHours(7):dd/MM/yyyy} — ระบบอยู่ในโหมดอ่านอย่างเดียว",
                "ต่ออายุแพ็กเกจเพื่อใช้งานต่อ");

        // ② ชื่อบิตของแพ็กเกจ (Payroll, Inventory, LodgingModule …)
        if (Enum.TryParse<FeatureFlags>(code, ignoreCase: true, out var flag) && flag != 0)
        {
            // อ่านผ่าน CheckFeatureAccessAsync เดิม — ตัวนั้นครอบ OwnerDisabledFeatures
            // (เจ้าของกดซ่อนฟีเจอร์ที่ไม่ใช้) ซึ่ง EnabledFeatures ดิบไม่มี
            var ok = await _subscriptions.CheckFeatureAccessAsync(companyId, flag);
            return ok
                ? EntitlementResult.Ok()
                : new EntitlementResult(false,
                    $"แพ็กเกจปัจจุบัน ({eff.Plan}) ไม่รวมความสามารถนี้",
                    "อัปเกรดแพ็กเกจเพื่อเปิดใช้งาน");
        }

        // ③ add-on ที่ขายแยก (string code)
        var feature = await _db.ApiFeatures.AsNoTracking()
            .FirstOrDefaultAsync(f => f.FeatureCode == code && !f.IsDeleted, ct);
        var row = await _db.CompanyFeatures.AsNoTracking()
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.FeatureCode == code && !f.IsDeleted, ct);

        // ยังไม่มีในแคตตาล็อกเลย = โค้ดอ้างรหัสที่ไม่มีจริง → **ปล่อยผ่าน**
        // (fail-open) แล้ว log — ห้ามให้การพิมพ์รหัสผิดกลายเป็นการปิดฟีเจอร์
        // ให้ลูกค้าเงียบ ๆ ซึ่งหาสาเหตุยากมาก
        if (feature == null)
        {
            _logger.LogWarning("ตรวจสิทธิ์ add-on ที่ไม่มีในแคตตาล็อก: {Code} (company={Company}) — ปล่อยผ่าน", code, companyId);
            return EntitlementResult.Ok();
        }

        var price = await ResolvePriceAsync(companyId, code, ct);

        // เปิดอยู่แล้ว → ผ่าน (บอกด้วยว่าอยู่ใน trial ไหม เพื่อให้ UI ติดป้ายได้)
        if (row is { IsEnabled: true })
        {
            var inTrial = row.TrialUntil is DateTime t && t > DateTime.UtcNow;
            return EntitlementResult.Ok(inTrial, inTrial ? row.TrialUntil : null);
        }

        // ยังไม่เปิด — ต้องบอกให้ครบว่าเปิดยังไงและราคาเท่าไร
        var priceText = price is decimal p && p > 0 ? $" (+{p:N0} บาท/เดือน)" : "";
        var minPlanBlock = MinPlanBlocked(feature.MinPlanCsv, eff.Plan);
        if (minPlanBlock != null)
            return new EntitlementResult(false,
                $"{feature.Name} เปิดใช้ได้เฉพาะแพ็กเกจ {minPlanBlock} ขึ้นไป (ปัจจุบัน {eff.Plan})",
                $"อัปเกรดแพ็กเกจเป็น {minPlanBlock} ก่อน", code, price);

        if (!feature.IsPublished)
            return new EntitlementResult(false,
                $"{feature.Name} ยังไม่เปิดให้บริการ", "ติดต่อทีมงานหากต้องการใช้ก่อนใคร", code, price);

        var trialText = feature.TrialDays > 0 ? $" · ทดลองฟรี {feature.TrialDays} วัน" : "";
        return new EntitlementResult(false,
            $"ยังไม่ได้เปิดใช้ส่วนเสริม \"{feature.Name}\"",
            $"เปิดใช้{priceText}{trialText}", code, price);
    }

    public async Task<List<string>> GetEnabledAddOnCodesAsync(Guid companyId, CancellationToken ct = default)
        => await _db.CompanyFeatures.AsNoTracking()
            .Where(f => f.CompanyId == companyId && f.IsEnabled && !f.IsDeleted)
            .Select(f => f.FeatureCode)
            .ToListAsync(ct);

    /// <summary>แพ็กเกจปัจจุบันต่ำกว่าที่ add-on ต้องการไหม — คืนชื่อแพ็กเกจต่ำสุดที่ผ่าน
    /// (null = ไม่บล็อก). ว่าง/ไม่ระบุ = ขายได้ทุกแพ็กเกจ</summary>
    private static string? MinPlanBlocked(string? minPlanCsv, SubscriptionPlan current)
    {
        if (string.IsNullOrWhiteSpace(minPlanCsv)) return null;
        var allowed = minPlanCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<SubscriptionPlan>(s, true, out var p) ? (SubscriptionPlan?)p : null)
            .Where(p => p != null).Select(p => p!.Value).ToList();
        if (allowed.Count == 0) return null;
        if (allowed.Contains(current)) return null;
        // ชื่อแพ็กเกจต่ำสุดในลิสต์ — บอกผู้ใช้ว่าอัปเกรดถึงตรงไหนก็พอ
        return allowed.OrderBy(p => (int)p).First().ToString();
    }

    /// <summary>ราคาปัจจุบันของ add-on (ดีลเฉพาะกลุ่มชนะราคามาตรฐาน) — logic เดียวกับ
    /// `UsageMeteringService.ResolvePlanAsync` แต่ตัวนั้น private อยู่ในคลาสของมัน
    /// และเป็นเส้น "คิดเงิน" ส่วนตัวนี้เป็นเส้น "แสดงราคา" ⇒ ถ้าวันหนึ่งเกณฑ์เลือก
    /// แผนเปลี่ยน ต้องแก้ทั้งสองที่ (มีเทสต์ล็อกไว้ที่ EntitlementPriceTests)</summary>
    private async Task<decimal?> ResolvePriceAsync(Guid companyId, string code, CancellationToken ct)
    {
        var accountId = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.BillingAccountId).FirstOrDefaultAsync(ct);
        var now = DateTime.UtcNow;
        var plans = await _db.ApiPricingPlans.AsNoTracking()
            .Where(p => p.FeatureCode == code && !p.IsDeleted
                     && p.EffectiveFrom <= now && (p.EffectiveTo == null || p.EffectiveTo > now)
                     && (p.BillingAccountId == null || p.BillingAccountId == accountId))
            .ToListAsync(ct);
        return plans
            .OrderByDescending(p => p.BillingAccountId.HasValue)
            .ThenByDescending(p => p.EffectiveFrom)
            .Select(p => (decimal?)p.UnitPrice)
            .FirstOrDefault();
    }
}
