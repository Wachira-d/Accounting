using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>รอบ 231 (คำตัดสินข้อ 231-8 · LICENSE_USAGE_OVERAGE_AUDIT §C.2) — <b>ตัวอ่านเดียว</b>ของราคาพิเศษรายบริษัท: โหลดแถวที่มีผล (บริษัทชนะ License) + แม่แบบอ้างอิง
/// แล้วส่งให้ตัวตัดสิน pure <see cref="PlatformPlanPricing.EffectiveCyclePrice"/> · ผู้เรียกทั้ง 7 จุด (ล็อกด้วย <c>tools/required_call_site_check.py</c>):
/// <c>RequestPlanPaymentAsync</c> · <c>ProrationFor</c> (สองฝั่ง) · <c>QuotePlanChangeAsync</c> · <c>SubmitPaymentAsync</c> · <c>SaasBillingDocumentService.PriceFor</c> (ใบต่ออายุ) ·
/// <c>SubscriptionPaymentHandler</c>/<c>PlatformPaymentService</c> (ยอดอนุมัติอัตโนมัติ) · <c>AdminController.AdminChangePlan</c> — ห้ามเรียก <c>PlatformPlanPricing.CyclePrice</c> ตรงในจุดเหล่านั้น</summary>
public static class PlatformPriceOverrideStore
{
    public static PlatformPlanPricing.PriceOverrideInput ToInput(CompanyPlanPriceOverride o)
        => new(o.Id, o.Mode, o.ReferencePlanTemplateId, o.FixedMonthly, o.FixedQuarterly, o.FixedSemiAnnual, o.FixedAnnual, o.PercentOff,
            o.EffectiveFromUtc, o.EffectiveToUtc, o.IsActive && o.EndedAtUtc == null, o.AppliesToUpgrades, o.AppliesToRenewals);

    /// <summary>แถวที่มีผล ณ <paramref name="nowUtc"/> สำหรับ (บริษัท ∪ License ของบริษัท) × แพ็กเกจ — แถวบริษัทชนะ · หลายแถวที่ซ้อนกัน (ไม่ควรเกิด — ตรวจตอนบันทึก) ⇒ เริ่มช้าสุดชนะ</summary>
    private static async Task<CompanyPlanPriceOverride?> FindLiveAsync(AccountingDbContext db, Guid? companyId, Guid? accountSubscriptionId,
        Guid planTemplateId, DateTime nowUtc, bool forRenewal, CancellationToken ct = default)
    {
        if (companyId == null && accountSubscriptionId == null) return null;
        var rows = await db.CompanyPlanPriceOverrides.AsNoTracking()
            .Where(o => o.PlanTemplateId == planTemplateId && o.IsActive && o.EndedAtUtc == null
                && ((companyId != null && o.CompanyId == companyId) || (accountSubscriptionId != null && o.AccountSubscriptionId == accountSubscriptionId)))
            .ToListAsync(ct);
        return rows
            .Where(o => PlatformPlanPricing.OverrideLive(ToInput(o), nowUtc, forRenewal))
            .OrderByDescending(o => o.CompanyId != null)      // บริษัทชนะ License
            .ThenByDescending(o => o.EffectiveFromUtc)
            .FirstOrDefault();
    }

    /// <summary>ราคาต่อรอบที่มีผลของ <paramref name="template"/> สำหรับบริษัทนี้ — null template = ไม่มีแม่แบบ ⇒ ผู้เรียกใช้ค่า fallback เอง</summary>
    public static async Task<PlatformPlanPricing.EffectivePrice> ResolveAsync(AccountingDbContext db, Guid? companyId, Guid? accountSubscriptionId,
        PlanTemplate template, BillingCycle cycle, DateTime nowUtc, bool forRenewal = false, CancellationToken ct = default)
    {
        var ov = await FindLiveAsync(db, companyId, accountSubscriptionId, template.Id, nowUtc, forRenewal, ct);
        PlanTemplate? reference = null;
        // ฝ่ายค้าน S6: โหลดแม่แบบอ้างอิงแม้ปิด/ลบ (IgnoreQueryFilters) แล้วให้ตัวตัดสิน pure ตกกลับพร้อม Note — ไม่ grandfather
        if (ov?.Mode == PlanPriceOverrideMode.PriceOfPlan && ov.ReferencePlanTemplateId is Guid refId)
            reference = await db.PlanTemplates.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == refId, ct);
        return PlatformPlanPricing.EffectiveCyclePrice(template, cycle, ov == null ? null : ToInput(ov), reference, nowUtc, forRenewal);
    }

    /// <summary>ทางลัดสำหรับ subscription ของบริษัท (รู้ CompanyId + AccountSubscriptionId จากแถว)</summary>
    public static Task<PlatformPlanPricing.EffectivePrice> ResolveForSubscriptionAsync(AccountingDbContext db, Subscription sub, PlanTemplate template,
        BillingCycle cycle, DateTime nowUtc, bool forRenewal = false, CancellationToken ct = default)
        => ResolveAsync(db, sub.CompanyId, sub.AccountSubscriptionId, template, cycle, nowUtc, forRenewal, ct);

    /// <summary>ยอดที่ต้องจ่ายของแพ็กเกจนี้สำหรับบริษัทนี้ (ราคาพิเศษ → ส่วนลด %) — ตัวเดียวของ handler/เริ่มชำระออนไลน์/แจ้งชำระ ·
    /// ฝ่ายค้าน S4: <paramref name="currentPlan"/> = แพ็กเกจปัจจุบันของบริษัท ⇒ ซื้อแพ็กเกจเดิมซ้ำ = งานต่ออายุ (<c>AppliesToRenewals</c>) · ต่างแพ็กเกจ = อัปเกรด (<c>AppliesToUpgrades</c>)</summary>
    public static async Task<decimal> AmountDueAsync(AccountingDbContext db, Guid? companyId, Guid? accountSubscriptionId, PlanTemplate template,
        BillingCycle cycle, DateTime nowUtc, decimal? discountPercent = null, SubscriptionPlan? currentPlan = null, CancellationToken ct = default)
    {
        var forRenewal = currentPlan is SubscriptionPlan cur && PlatformPlanPricing.IsRenewalPurchase(cur, template.Plan);
        var eff = await ResolveAsync(db, companyId, accountSubscriptionId, template, cycle, nowUtc, forRenewal, ct);
        return PlatformPlanPricing.AmountDue(eff.Price, discountPercent);
    }

    /// <summary>ฝ่ายค้าน S2: ยอดที่จ่ายจริงล่าสุดของ (แพ็กเกจ, รอบ) นี้ — รายการ Approved ที่ไม่ใช่ส่วนต่าง/เปลี่ยนรอบ · null = ไม่เคยจ่ายรอบนี้</summary>
    public static Task<decimal?> LastApprovedCycleAmountAsync(AccountingDbContext db, Guid subscriptionId, SubscriptionPlan plan, BillingCycle cycle, CancellationToken ct = default)
        => db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.SubscriptionId == subscriptionId && p.Status == SubscriptionPaymentStatus.Approved && p.RequestedPlan == plan
                && p.RequestedBillingCycle == cycle && !p.IsProration && !p.IsCycleChange && p.Amount > 0m)
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.CreatedAt)
            .Select(p => (decimal?)p.Amount).FirstOrDefaultAsync(ct);
}
