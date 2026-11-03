using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 231 (คำตัดสินข้อ 231-8 · `erp-review/2026-11-03/LICENSE_USAGE_OVERAGE_AUDIT.md` §C.1) — <b>ราคาพิเศษรายบริษัท/ราย License ที่คงทน</b>
/// (เดิมแอดมินแก้ยอดได้ครั้งเดียวต่อรายการ ⇒ ใบต่ออายุ/ส่วนต่าง/ดูราคา ใช้ราคาแม่แบบเสมอ)
///
/// <para>ระดับแพลตฟอร์ม (ไม่ใช่ข้อมูลผู้เช่า — ไม่ใช่ <c>TenantEntity</c>) · อ้าง <see cref="CompanyId"/> <b>หรือ</b> <see cref="AccountSubscriptionId"/> อย่างใดอย่างหนึ่ง ·
/// แถวบริษัทชนะแถว License · ต่อ <see cref="PlanTemplateId"/> (แพ็กเกจที่ได้สิทธิ์) · มีผล [EffectiveFromUtc, EffectiveToUtc) · ไม่แก้ทับ: ต่อสัญญา/เปลี่ยนเงื่อนไข = ปิดแถวเดิม
/// (<see cref="EndedAtUtc"/>) + แถวใหม่ · ทุกการสร้าง/ปิดลง audit chain <c>PLATFORM-PRICE-OVERRIDE-SET/END</c> + <c>SubscriptionHistory</c> ·
/// ตัวตัดสินราคาเดียว <c>Helpers/PlatformPlanPricing.EffectiveCyclePrice</c> ผ่าน <c>Helpers/PlatformPriceOverrideStore.ResolveAsync</c> (ทุกจุดเรียกราคาแพ็กเกจ) ·
/// คูปองซ้อนบนราคาพิเศษ (ราคาพิเศษแทนราคาตั้ง → คูปองลดจากราคาพิเศษ → แอดมินแก้ยอดครั้งเดียวทับสุดท้าย)</para>
/// </summary>
public class CompanyPlanPriceOverride : BaseEntity
{
    public Guid? CompanyId { get; set; }
    public Guid? AccountSubscriptionId { get; set; }
    /// <summary>แพ็กเกจที่ได้สิทธิ์ในราคานี้ (เช่น Pro)</summary>
    public Guid PlanTemplateId { get; set; }
    public PlanPriceOverrideMode Mode { get; set; } = PlanPriceOverrideMode.PercentOff;
    /// <summary><see cref="PlanPriceOverrideMode.PriceOfPlan"/>: แพ็กเกจที่ใช้ราคาแทน (เช่น Standard)</summary>
    public Guid? ReferencePlanTemplateId { get; set; }
    /// <summary><see cref="PlanPriceOverrideMode.FixedPerCycle"/>: ราคาต่อรอบ (รวม/ไม่รวม VAT ตามค่าตั้งราคาแพ็กเกจ) · null = ใช้ราคาแม่แบบรอบนั้น</summary>
    public decimal? FixedMonthly { get; set; }
    public decimal? FixedQuarterly { get; set; }
    public decimal? FixedSemiAnnual { get; set; }
    public decimal? FixedAnnual { get; set; }
    /// <summary><see cref="PlanPriceOverrideMode.PercentOff"/>: ลด % (0 &lt; x ≤ 100)</summary>
    public decimal? PercentOff { get; set; }
    public DateTime EffectiveFromUtc { get; set; } = DateTime.UtcNow;
    /// <summary>null = ตลอดไป (จนกว่าจะปิด)</summary>
    public DateTime? EffectiveToUtc { get; set; }
    public bool AppliesToUpgrades { get; set; } = true;
    public bool AppliesToRenewals { get; set; } = true;
    /// <summary>บังคับ (audit)</summary>
    public string Reason { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? EndedAtUtc { get; set; }
    public string? EndedBy { get; set; }
    public string? EndReason { get; set; }
}
