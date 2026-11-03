using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §C/§B.8/E7 · คำตัดสินข้อ 231-8..231-11) — ตัวตัดสิน pure:
/// ราคาพิเศษรายบริษัท (<see cref="PlatformPlanPricing.EffectiveCyclePrice"/> — ลำดับ/ช่วงวัน/3 โหมด/ซ้อนคูปอง) ·
/// เปลี่ยนรอบบิลกลางรอบ (<see cref="PlatformProration.CycleChange"/> — ทั้งสองทิศ · ห้ามติดลบ) ·
/// ชนิด subscription (<see cref="SubscriptionKindRule"/>) · นโยบายปิดข้อมูลทดลองใช้ (<see cref="TrialDataRetentionPolicy"/> — legal hold ห้ามลบ) ·
/// ด่านอ่านของทดลองใช้หมดอายุ (<see cref="SubscriptionGatePolicy.Decide"/> — เปิด/ไม่เปิดสวิตช์)
/// <para>ทุกด่านมีทิศ "ใช้ราคาพิเศษ/บล็อก/ลบ" และทิศตรงข้าม "ราคาตั้ง/ผ่าน/ไม่แตะ" (F2 ข้อ 8) · ตัวอย่างเงินตรวจมือได้</para>
/// </summary>
public class PlatformPriceOverrideRound231Tests
{
    private static readonly DateTime Now = new(2026, 11, 15, 3, 0, 0, DateTimeKind.Utc);   // 15/11/2569 10:00 ไทย
    private static readonly Guid ProId = Guid.NewGuid();
    private static readonly Guid StdId = Guid.NewGuid();

    private static PlanTemplate Pro() => new() { Id = ProId, Name = "Pro", Plan = SubscriptionPlan.Pro, MonthlyPrice = 1_990m, QuarterlyPrice = 5_700m, SemiAnnualPrice = 10_900m, AnnualPrice = 19_900m };
    private static PlanTemplate Standard() => new() { Id = StdId, Name = "Standard", Plan = SubscriptionPlan.Basic, MonthlyPrice = 990m, QuarterlyPrice = 2_800m, SemiAnnualPrice = 5_400m, AnnualPrice = 9_900m };

    private static PlatformPlanPricing.PriceOverrideInput Ov(PlanPriceOverrideMode mode, Guid? reference = null, decimal? pct = null,
        decimal? fm = null, decimal? fa = null, DateTime? from = null, DateTime? to = null, bool active = true, bool upgrades = true, bool renewals = true)
        => new(Guid.NewGuid(), mode, reference, fm, null, null, fa, pct, from ?? Now.AddDays(-1), to, active, upgrades, renewals);

    // ═════════ ราคาพิเศษ — ลำดับ/โหมด ═════════

    [Fact]
    public void ไม่มีราคาพิเศษ_ราคาตั้ง_Source_list()
    {
        var p = PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, null, null, Now);
        Assert.Equal(19_900m, p.Price);
        Assert.Equal(19_900m, p.ListPrice);
        Assert.False(p.Applied);
        Assert.Equal(PlatformPlanPricing.SourceList, p.Source);
        Assert.Null(PlatformPlanPricing.SpecialPriceNote(p, BillingCycle.Annual));
    }

    [Fact]
    public void PriceOfPlan_จ่ายราคาStandard_ได้Pro_ทุกรอบ()
    {
        var ov = Ov(PlanPriceOverrideMode.PriceOfPlan, reference: StdId);
        foreach (var (cycle, expect) in new[] { (BillingCycle.Monthly, 990m), (BillingCycle.Quarterly, 2_800m), (BillingCycle.SemiAnnual, 5_400m), (BillingCycle.Annual, 9_900m) })
        {
            var p = PlatformPlanPricing.EffectiveCyclePrice(Pro(), cycle, ov, Standard(), Now);
            Assert.Equal(expect, p.Price);
            Assert.True(p.Applied);
            Assert.Equal(ov.Id, p.OverrideId);
            Assert.Equal(PlatformPlanPricing.SourceOverridePlan, p.Source);
        }
        Assert.Equal(19_900m, PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, ov, Standard(), Now).ListPrice);
        Assert.Contains("ราคาพิเศษสำหรับคุณ: ฿9,900.00/ปี (ราคาตั้ง ฿19,900.00)",
            PlatformPlanPricing.SpecialPriceNote(PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, ov, Standard(), Now), BillingCycle.Annual));
    }

    [Fact]
    public void PriceOfPlan_ไม่มีแม่แบบอ้างอิง_ตกกลับราคาตั้ง_ไม่แต่งราคาเอง()
    {
        var ov = Ov(PlanPriceOverrideMode.PriceOfPlan, reference: StdId);
        var p = PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, ov, null, Now);
        Assert.Equal(19_900m, p.Price);
        Assert.False(p.Applied);
        Assert.NotNull(p.Note);
        // แม่แบบอ้างอิงคนละตัวกับที่ระบุ = ไม่ใช้
        var wrong = new PlanTemplate { Id = Guid.NewGuid(), Name = "Other", MonthlyPrice = 1m };
        Assert.False(PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, ov, wrong, Now).Applied);
    }

    [Fact]
    public void FixedPerCycle_รอบที่กำหนดใช้ราคานั้น_รอบที่เว้นว่างใช้ราคาแม่แบบ()
    {
        var ov = Ov(PlanPriceOverrideMode.FixedPerCycle, fa: 12_345.678m, fm: null);
        var annual = PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, ov, null, Now);
        Assert.Equal(12_345.68m, annual.Price);   // ปัด 2 ตำแหน่ง AwayFromZero
        Assert.True(annual.Applied);
        var monthly = PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Monthly, ov, null, Now);
        Assert.Equal(1_990m, monthly.Price);
        Assert.False(monthly.Applied);
    }

    [Fact]
    public void PercentOff_ลด15_จาก19900_เท่ากับ16915_ปัดAwayFromZero_นอกช่วงตกกลับ()
    {
        var p = PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, Ov(PlanPriceOverrideMode.PercentOff, pct: 15m), null, Now);
        Assert.Equal(16_915m, p.Price);
        Assert.Equal(PlatformPlanPricing.SourceOverridePercent, p.Source);
        // 1,990 × (1 − 0.3333) = 1,326.733 ⇒ 1,326.73 · 0.125% ของ 1,990 = 2.4875 ⇒ 1,987.5125 ⇒ 1,987.51
        Assert.Equal(1_326.73m, PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Monthly, Ov(PlanPriceOverrideMode.PercentOff, pct: 33.33m), null, Now).Price);
        Assert.Equal(1_987.51m, PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Monthly, Ov(PlanPriceOverrideMode.PercentOff, pct: 0.125m), null, Now).Price);
        // 100% = 0 (ไม่ติดลบ) · 0 / 101 / null = ตกกลับราคาตั้ง
        Assert.Equal(0m, PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, Ov(PlanPriceOverrideMode.PercentOff, pct: 100m), null, Now).Price);
        Assert.False(PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, Ov(PlanPriceOverrideMode.PercentOff, pct: 0m), null, Now).Applied);
        Assert.False(PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, Ov(PlanPriceOverrideMode.PercentOff, pct: 101m), null, Now).Applied);
        Assert.False(PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, Ov(PlanPriceOverrideMode.PercentOff, pct: null), null, Now).Applied);
    }

    [Fact]
    public void ช่วงวัน_ก่อนเริ่ม_หมดช่วง_ปิดใช้_ไม่ใช้_ทิศตรงข้ามในช่วงใช้()
    {
        Assert.False(PlatformPlanPricing.OverrideLive(Ov(PlanPriceOverrideMode.PercentOff, pct: 10m, from: Now.AddDays(1)), Now, false));
        Assert.False(PlatformPlanPricing.OverrideLive(Ov(PlanPriceOverrideMode.PercentOff, pct: 10m, from: Now.AddDays(-30), to: Now), Now, false));   // To = exclusive
        Assert.False(PlatformPlanPricing.OverrideLive(Ov(PlanPriceOverrideMode.PercentOff, pct: 10m, active: false), Now, false));
        Assert.True(PlatformPlanPricing.OverrideLive(Ov(PlanPriceOverrideMode.PercentOff, pct: 10m, from: Now, to: Now.AddSeconds(1)), Now, false));
        Assert.True(PlatformPlanPricing.OverrideLive(Ov(PlanPriceOverrideMode.PercentOff, pct: 10m, to: null), Now, false));
        // หมดช่วง ⇒ กลับราคาตั้ง (ทิศตรงข้ามของการใช้)
        var expired = PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, Ov(PlanPriceOverrideMode.PercentOff, pct: 50m, from: Now.AddDays(-10), to: Now.AddDays(-1)), null, Now);
        Assert.Equal(19_900m, expired.Price);
        Assert.False(expired.Applied);
    }

    [Fact]
    public void ขอบเขตงาน_ต่ออายุเท่านั้น_ไม่ใช้กับอัปเกรด_และกลับกัน()
    {
        var renewOnly = Ov(PlanPriceOverrideMode.PercentOff, pct: 10m, upgrades: false, renewals: true);
        Assert.True(PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, renewOnly, null, Now, forRenewal: true).Applied);
        Assert.False(PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, renewOnly, null, Now, forRenewal: false).Applied);
        var upgradeOnly = Ov(PlanPriceOverrideMode.PercentOff, pct: 10m, upgrades: true, renewals: false);
        Assert.False(PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, upgradeOnly, null, Now, forRenewal: true).Applied);
        Assert.True(PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, upgradeOnly, null, Now, forRenewal: false).Applied);
    }

    [Fact]
    public void ซ้อน_ราคาพิเศษก่อน_แล้วส่วนลดเปอร์เซ็นต์_แล้วคูปอง()
    {
        // ราคาพิเศษ 9,900 (PriceOfPlan) → โปรโมชันแปลงทดลองใช้ 10% ⇒ 8,910 → คูปอง 500 บาท ⇒ 8,410
        var eff = PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, Ov(PlanPriceOverrideMode.PriceOfPlan, reference: StdId), Standard(), Now);
        var afterPromo = PlatformPlanPricing.AmountDue(eff.Price, 10m);
        Assert.Equal(8_910m, afterPromo);
        var coupon = PlatformCouponRule.Apply(new PlatformCoupon { Code = "T231", Kind = PlatformCouponKind.Fixed, Value = 500m, AppliesTo = PlatformCouponScope.Both, IsActive = true },
            afterPromo, new PlatformCouponRule.Target(PlatformCouponScope.Subscription, "Pro", "Annual", null, false), Now, 0, 0);
        Assert.True(coupon.Ok);
        Assert.Equal(8_410m, coupon.AmountAfter);
        // ทิศตรงข้าม: ไม่มีราคาพิเศษ ⇒ 19,900 → 17,910 → 17,410
        Assert.Equal(17_910m, PlatformPlanPricing.AmountDue(PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, null, null, Now).Price, 10m));
        // AmountDue จากราคาที่มีผล: ส่วนลด null/0/101 = ไม่ลด · ไม่ติดลบ
        Assert.Equal(9_900m, PlatformPlanPricing.AmountDue(9_900m, null));
        Assert.Equal(9_900m, PlatformPlanPricing.AmountDue(9_900m, 101m));
        Assert.Equal(0m, PlatformPlanPricing.AmountDue(-5m, null));
    }

    // ═════════ เปลี่ยนรอบบิลกลางรอบ (231-9 · §B.8) ═════════

    [Fact]
    public void รายเดือนเป็นรายปี_กลางเดือน_ตัวอย่างตรวจมือ()
    {
        // รอบรายเดือน 01/11/2569 → 01/12/2569 (30 วัน) · วันนี้ 15/11 ⇒ เหลือ 16 วัน · ราคาเดิม 1,990/เดือน · ใหม่ 19,900/ปี
        // credit = round(1,990 × 16/30, 2) = 1,061.33 · amount = 19,900 − 1,061.33 = 18,838.67 · รอบใหม่ = 15/11/2569 + 12 เดือน
        var monthEnd = new DateTime(2026, 11, 30, 17, 0, 0, DateTimeKind.Utc);   // 01/12/2569 00:00 ไทย
        var r = PlatformProration.CycleChange(1_990m, 1, 19_900m, 12, monthEnd, Now);
        Assert.True(r.Applies);
        Assert.Equal(16, r.DaysRemaining);
        Assert.Equal(30, r.CycleDays);
        Assert.Equal(1_061.33m, r.Credit);
        Assert.Equal(18_838.67m, r.Amount);
        Assert.Equal(Now, r.NewStartUtc);
        Assert.Equal(Now.AddMonths(12), r.NewEndUtc);
        Assert.Equal(12, r.NewCycleMonths);
        Assert.Contains("16/30", r.Note);
    }

    [Fact]
    public void รายเดือนเป็นรายปี_พร้อมอัปเกรด_ใช้ราคาเดิมของแพ็กเกจเดิม_ราคาใหม่ของแพ็กเกจใหม่()
    {
        // Standard รายเดือน 990 → Pro รายปี 19,900 · เหลือ 16/30 ⇒ credit 528.00 · amount 19,372.00
        var monthEnd = new DateTime(2026, 11, 30, 17, 0, 0, DateTimeKind.Utc);
        var r = PlatformProration.CycleChange(990m, 1, 19_900m, 12, monthEnd, Now);
        Assert.True(r.Applies);
        Assert.Equal(528.00m, r.Credit);
        Assert.Equal(19_372.00m, r.Amount);
    }

    [Fact]
    public void วันสุดท้ายของรอบ_ไม่คิด_และ_เครดิตมากกว่าราคาใหม่_ยอดศูนย์ห้ามติดลบ()
    {
        var sameDay = PlatformProration.CycleChange(1_990m, 1, 19_900m, 12, Now.AddHours(2), Now);
        Assert.False(sameDay.Applies);
        Assert.NotNull(sameDay.NotApplicableReason);
        // ราคาเดิมต่อรอบสูงผิดปกติ (แอดมินตั้ง) > ราคาใหม่ ⇒ amount 0 · credit ไม่ถูกตัด (ห้ามคืน)
        var monthEnd = new DateTime(2026, 11, 30, 17, 0, 0, DateTimeKind.Utc);
        var r = PlatformProration.CycleChange(100_000m, 1, 19_900m, 12, monthEnd, Now);
        Assert.True(r.Applies);
        Assert.Equal(0m, r.Amount);
        Assert.Equal(53_333.33m, r.Credit);
    }

    [Fact]
    public void ทิศตรงข้าม_รอบสั้นลงหรือเท่าเดิม_ทดลอง_ฟรีถาวร_ไม่active_ไม่ใช่เส้นนี้()
    {
        var annualEnd = new DateTime(2027, 3, 30, 17, 0, 0, DateTimeKind.Utc);
        Assert.False(PlatformProration.CycleChange(19_900m, 12, 1_990m, 1, annualEnd, Now).Applies);    // สั้นลง = Scheduled* (ไม่คืนเงิน)
        Assert.False(PlatformProration.CycleChange(1_990m, 1, 1_990m, 1, annualEnd, Now).Applies);
        var monthEnd = new DateTime(2026, 11, 30, 17, 0, 0, DateTimeKind.Utc);
        Assert.False(PlatformProration.CycleChange(1_990m, 1, 19_900m, 12, monthEnd, Now, isTrial: true).Applies);
        Assert.False(PlatformProration.CycleChange(1_990m, 1, 19_900m, 12, monthEnd, Now, isPermanentFree: true).Applies);
        Assert.False(PlatformProration.CycleChange(1_990m, 1, 19_900m, 12, monthEnd, Now, planActive: false).Applies);
        Assert.False(PlatformProration.CycleChange(1_990m, 0, 19_900m, 12, monthEnd, Now).Applies);
        Assert.False(PlatformProration.CycleChange(1_990m, 1, 0m, 12, monthEnd, Now).Applies);   // รอบใหม่ไม่มีราคา
    }

    [Fact]
    public void ข้อความบรรทัดหักเครดิตบนใบเสร็จ_วันที่ไทย()
    {
        var monthEnd = new DateTime(2026, 11, 30, 17, 0, 0, DateTimeKind.Utc);
        var s = PlatformProration.CycleChangeCreditDescription(1_061.33m, 16, 30, monthEnd);
        Assert.Contains("หักมูลค่าคงเหลือรอบเดิม 1,061.33 บาท", s);
        Assert.Contains("16/30", s);
        Assert.Contains("2569", s);
    }

    // ═════════ ฝ่ายค้านรอบ 231 — S2 ฐานราคาเดิม · S5 สิ้นวันไทย · S6 แม่แบบอ้างอิงปิด · N1 ต่ออายุ/อัปเกรด ═════════

    [Fact]
    public void ฐานราคาเดิม_ใช้ยอดที่จ่ายจริงล่าสุด_ไม่มีจึงใช้ราคาที่resolverคืน()
    {
        Assert.Equal(1_500m, PlatformProration.OldPriceBasis(1_500m, 1_990m));        // จ่ายจริง (หลังคูปอง) ชนะราคาแม่แบบ/พิเศษ
        Assert.Equal(1_990m, PlatformProration.OldPriceBasis(null, 1_990m));          // ไม่เคยจ่ายรอบนี้ (แอดมินเปิดให้)
        Assert.Equal(0m, PlatformProration.OldPriceBasis(0m, 1_990m));               // รอบ 233 ฝ่ายค้าน S3: Approved ยอด 0 (คูปอง 100%/ยกเว้น) = จ่ายจริง 0 ⇒ เครดิต 0 (เดิม: ตกไปราคาแม่แบบ)
        Assert.Equal(1_234.57m, PlatformProration.OldPriceBasis(1_234.565m, 0m));     // ปัด AwayFromZero
        // เครดิตเปลี่ยนรอบคิดจากฐานนั้น: จ่ายจริง 1,500 รายเดือน เหลือ 16/30 วัน ⇒ credit 800.00
        var cc = PlatformProration.CycleChange(PlatformProration.OldPriceBasis(1_500m, 1_990m), 1, 19_900m, 12,
            new DateTime(2026, 11, 30, 17, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 14, 17, 0, 0, DateTimeKind.Utc));
        Assert.True(cc.Applies);
        Assert.Equal(800.00m, cc.Credit);
    }

    [Fact]
    public void วันสิ้นสุดราคาพิเศษ_เก็บเป็นสิ้นวันไทย_ครอบวันที่กรอกทั้งวัน()
    {
        var typed = new DateTime(2026, 11, 29, 17, 0, 0, DateTimeKind.Utc);          // 30/11 00:00 ไทย (ที่หน้าเว็บส่ง)
        var to = PlatformPlanPricing.EndOfBangkokDayUtc(typed);
        Assert.Equal(new DateTime(2026, 11, 30, 16, 59, 59, 999, DateTimeKind.Utc), to);   // 30/11 23:59:59.999 ไทย
        var ov = Ov(PlanPriceOverrideMode.PercentOff, pct: 10m, to: to);
        Assert.True(PlatformPlanPricing.OverrideLive(ov, new DateTime(2026, 11, 30, 10, 0, 0, DateTimeKind.Utc), false));   // 30/11 17:00 ไทย ยังอยู่ในช่วง
        Assert.False(PlatformPlanPricing.OverrideLive(ov, new DateTime(2026, 11, 30, 17, 0, 0, DateTimeKind.Utc), false));  // 1/12 00:00 ไทย หมดช่วง
        Assert.Equal(30, to.AddHours(7).Day);   // ตารางแสดงวันเดียวกับที่กรอก
    }

    [Fact]
    public void แม่แบบอ้างอิงถูกปิดหรือลบ_ตกกลับราคาตั้งพร้อมเหตุ_ไม่grandfather()
    {
        var inactive = Standard(); inactive.IsActive = false;
        var r1 = PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, Ov(PlanPriceOverrideMode.PriceOfPlan, reference: StdId), inactive, Now);
        Assert.False(r1.Applied); Assert.Equal(19_900m, r1.Price); Assert.Contains("ปิด/ลบแล้ว", r1.Note);
        var deleted = Standard(); deleted.IsDeleted = true;
        var r2 = PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, Ov(PlanPriceOverrideMode.PriceOfPlan, reference: StdId), deleted, Now);
        Assert.False(r2.Applied); Assert.Equal(19_900m, r2.Price);
        // ทิศตรงข้าม: แม่แบบเปิดใช้ = ราคาอ้างอิง
        Assert.Equal(9_900m, PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, Ov(PlanPriceOverrideMode.PriceOfPlan, reference: StdId), Standard(), Now).Price);
    }

    [Fact]
    public void ซื้อแพ็กเกจเดิมซ้ำคือต่ออายุ_ต่างแพ็กเกจคืออัปเกรด()
    {
        Assert.True(PlatformPlanPricing.IsRenewalPurchase(SubscriptionPlan.Pro, SubscriptionPlan.Pro));
        Assert.False(PlatformPlanPricing.IsRenewalPurchase(SubscriptionPlan.Basic, SubscriptionPlan.Pro));
        // ราคาพิเศษ "ต่ออายุเท่านั้น" ใช้กับการซื้อ Pro ซ้ำ แต่ไม่ใช้กับ Basic→Pro
        var ov = Ov(PlanPriceOverrideMode.PercentOff, pct: 10m, upgrades: false);
        Assert.True(PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, ov, null, Now, PlatformPlanPricing.IsRenewalPurchase(SubscriptionPlan.Pro, SubscriptionPlan.Pro)).Applied);
        Assert.False(PlatformPlanPricing.EffectiveCyclePrice(Pro(), BillingCycle.Annual, ov, null, Now, PlatformPlanPricing.IsRenewalPurchase(SubscriptionPlan.Basic, SubscriptionPlan.Pro)).Applied);
    }

    // ═════════ Subscription.Kind (231-10) ═════════

    [Theory]
    [InlineData(true, SubscriptionStatus.Active, true, false, false, SubscriptionKind.Free)]
    [InlineData(true, SubscriptionStatus.Expired, false, false, false, SubscriptionKind.Free)]
    [InlineData(false, SubscriptionStatus.Trial, true, false, false, SubscriptionKind.Trial)]
    [InlineData(false, SubscriptionStatus.Expired, true, true, false, SubscriptionKind.Trial)]      // Free-trial plan หมดอายุ ไม่เคยจ่าย = ทดลอง
    [InlineData(false, SubscriptionStatus.Expired, true, false, true, SubscriptionKind.Trial)]      // ทดลองแพ็กเกจ Pro หมดอายุ (TrialConfig Expired) = ทดลอง
    [InlineData(false, SubscriptionStatus.Expired, true, false, false, SubscriptionKind.Paid)]      // ฝ่ายค้าน B1 negative: แพ็กเกจจ่ายเงินหมดอายุ LastPaymentDate ว่าง ไม่มีประวัติทดลอง = Paid
    [InlineData(false, SubscriptionStatus.Active, true, false, false, SubscriptionKind.Paid)]       // แอดมินเปิดให้โดยไม่จ่าย = Paid
    [InlineData(false, SubscriptionStatus.Expired, false, true, true, SubscriptionKind.Paid)]       // เคยจ่าย (แม้ Plan FreeTrial/มีประวัติทดลอง) = Paid
    [InlineData(false, SubscriptionStatus.Suspended, false, false, false, SubscriptionKind.Paid)]
    public void Derive_สูตรเดียวกับ_SQL_backfill(bool permFree, SubscriptionStatus status, bool neverPaid, bool freeTrialPlan, bool trialExpired, SubscriptionKind expect)
        => Assert.Equal(expect, SubscriptionKindRule.Derive(permFree, status, neverPaid, freeTrialPlan, trialExpired));

    [Fact]
    public void ไม่เคยจ่าย_ต้องไม่มีทั้งวันชำระและรายการอนุมัติ_หมดทดลองคงชนิดทดลอง()
    {
        Assert.True(SubscriptionKindRule.NeverPaid(null, false));
        Assert.False(SubscriptionKindRule.NeverPaid(null, true));      // มีรายการ Approved แม้ LastPaymentDate ว่าง = เคยจ่าย
        Assert.False(SubscriptionKindRule.NeverPaid(Now, false));
        Assert.Equal(SubscriptionKind.Trial, SubscriptionKindRule.AfterTrialExpired(false));
        Assert.Equal(SubscriptionKind.Free, SubscriptionKindRule.AfterTrialExpired(true));
        // SQL backfill ต้องสะท้อนนิยามเดียวกัน (ไม่มีรายการ Approved · Plan FreeTrial หรือ TrialConfig Expired)
        var sql = PlatformPriceOverrideMigration.Statements().Single(x => x.Contains("UPDATE \"Subscriptions\" s SET \"Kind\""));
        Assert.Contains($"\"SubscriptionPayments\" p WHERE p.\"SubscriptionId\" = s.\"Id\" AND p.\"Status\" = {(int)SubscriptionPaymentStatus.Approved}", sql);   // Approved = 2 (ไม่ใช่ UnderReview = 1)
        Assert.Contains("s.\"Plan\" = 0 OR EXISTS (SELECT 1 FROM \"TrialConfigs\"", sql);
        Assert.DoesNotContain("PricePerCycle", sql);
    }

    [Fact]
    public void เปิดแพ็กเกจจากแม่แบบ_Free_หรือ_Paid()
    {
        Assert.Equal(SubscriptionKind.Free, SubscriptionKindRule.AfterTemplateApplied(true));
        Assert.Equal(SubscriptionKind.Paid, SubscriptionKindRule.AfterTemplateApplied(false));
        Assert.Equal("ทดลองใช้", SubscriptionKindRule.Label(SubscriptionKind.Trial));
    }

    // ═════════ ปิดข้อมูลทดลองใช้ (231-10 · legal hold) ═════════

    private static TrialDataRetentionPolicy.Facts Facts(SubscriptionKind kind = SubscriptionKind.Trial, SubscriptionStatus status = SubscriptionStatus.Expired,
        bool paid = false, bool on = true, int grace = 7, int retention = 90, DateTime? end = null, bool docs = false, bool je = false, bool deleted = false,
        int noticeDays = 0, DateTime? noticeSentAt = null, bool openBilling = false, bool filed = false, bool etax = false, bool wht = false, bool payroll = false)
        => new(kind, status, paid, on, grace, retention, end ?? Now.AddDays(-200), new TrialDataRetentionPolicy.LegalHoldFacts(docs, je, filed, etax, wht, payroll), deleted,
            noticeDays, noticeSentAt, openBilling);

    [Fact]
    public void ลบได้เฉพาะ_ทดลอง_หมดอายุ_ไม่เคยจ่าย_เปิดสวิตช์_พ้นระยะ_ไม่มีเอกสาร()
    {
        var d = TrialDataRetentionPolicy.Decide(Facts(), Now);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.Purge, d.Outcome);
        Assert.Equal((DateTime?)Now.AddDays(-200).AddDays(97), d.DueAtUtc);
    }

    [Fact]
    public void ติดLegalHold_มีเอกสารที่ออกแล้วหรือรายการบัญชี_ห้ามลบ()
    {
        Assert.Equal(TrialDataRetentionPolicy.Outcome.LegalHold, TrialDataRetentionPolicy.Decide(Facts(docs: true), Now).Outcome);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.LegalHold, TrialDataRetentionPolicy.Decide(Facts(je: true), Now).Outcome);
        Assert.Contains("ม.10", TrialDataRetentionPolicy.Decide(Facts(docs: true), Now).Reason);
        // ฝ่ายค้าน B2: legal hold ขยาย — แบบภาษีที่ยื่น · e-Tax ส่งแล้ว · 50 ทวิ · งวดเงินเดือนจ่ายแล้ว
        Assert.Equal(TrialDataRetentionPolicy.Outcome.LegalHold, TrialDataRetentionPolicy.Decide(Facts(filed: true), Now).Outcome);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.LegalHold, TrialDataRetentionPolicy.Decide(Facts(etax: true), Now).Outcome);
        Assert.Contains("50 ทวิ", TrialDataRetentionPolicy.Decide(Facts(wht: true), Now).Reason);
        Assert.Contains("เงินเดือน", TrialDataRetentionPolicy.Decide(Facts(payroll: true), Now).Reason);
        // งานค้าง (รายการชำระ/ใบแจ้งหนี้ค่าบริการ) = ยังไม่เข้าเงื่อนไข
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NotEligible, TrialDataRetentionPolicy.Decide(Facts(openBilling: true), Now).Outcome);
    }

    [Fact]
    public void แจ้งเจ้าของล่วงหน้าก่อนปิด_NoticeDue_แล้วรอครบวันแจ้ง_จึงPurge_และ0วันคือไม่แจ้ง()
    {
        var end = Now.AddDays(-200);                       // due = end + 97 วัน = Now − 103 วัน
        var first = TrialDataRetentionPolicy.Decide(Facts(end: end, noticeDays: 14), Now);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NoticeDue, first.Outcome);
        Assert.Contains("14 วัน", first.Reason);
        // แจ้งแล้ววันนี้ ⇒ ยังปิดไม่ได้จนกว่าจะครบ 14 วันนับจากวันแจ้ง (ไม่ใช่นับจาก due ที่ผ่านไปแล้ว)
        var sent = TrialDataRetentionPolicy.Decide(Facts(end: end, noticeDays: 14, noticeSentAt: Now), Now.AddDays(13));
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NoticeSent, sent.Outcome);
        Assert.Equal((DateTime?)Now.AddDays(14), sent.DueAtUtc);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.Purge, TrialDataRetentionPolicy.Decide(Facts(end: end, noticeDays: 14, noticeSentAt: Now), Now.AddDays(14)).Outcome);
        // แจ้งได้ตั้งแต่ due − 14 วัน (ก่อนครบกำหนดเก็บ) — แต่ปิดได้ไม่ก่อน due
        var early = TrialDataRetentionPolicy.Decide(Facts(end: Now.AddDays(-90), noticeDays: 14), Now);   // due = Now + 7 วัน
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NoticeDue, early.Outcome);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NoticeSent, TrialDataRetentionPolicy.Decide(Facts(end: Now.AddDays(-90), noticeDays: 14, noticeSentAt: Now), Now.AddDays(10)).Outcome);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.Purge, TrialDataRetentionPolicy.Decide(Facts(end: Now.AddDays(-90), noticeDays: 14, noticeSentAt: Now), Now.AddDays(14)).Outcome);
        // ทิศตรงข้าม: ยังไม่ถึงช่วงแจ้ง = NotYetDue · noticeDays 0 = ปิดเมื่อครบกำหนดทันที · legal hold ชนะการแจ้ง
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NotYetDue, TrialDataRetentionPolicy.Decide(Facts(end: Now.AddDays(-50), noticeDays: 14), Now).Outcome);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.Purge, TrialDataRetentionPolicy.Decide(Facts(end: end, noticeDays: 0), Now).Outcome);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.LegalHold, TrialDataRetentionPolicy.Decide(Facts(end: end, noticeDays: 14, docs: true), Now).Outcome);
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่ลบเมื่อ_ไม่เปิดสวิตช์_ยังไม่ครบ_เคยจ่าย_FreeEdition_จ่ายเงิน_ยังไม่หมดอายุ_ปิดแล้ว()
    {
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NotEligible, TrialDataRetentionPolicy.Decide(Facts(on: false), Now).Outcome);   // ค่าตั้งต้น
        var notDue = TrialDataRetentionPolicy.Decide(Facts(end: Now.AddDays(-50)), Now);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NotYetDue, notDue.Outcome);
        Assert.Equal((DateTime?)Now.AddDays(-50).AddDays(97), notDue.DueAtUtc);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NotEligible, TrialDataRetentionPolicy.Decide(Facts(paid: true), Now).Outcome);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NotEligible, TrialDataRetentionPolicy.Decide(Facts(kind: SubscriptionKind.Free), Now).Outcome);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NotEligible, TrialDataRetentionPolicy.Decide(Facts(kind: SubscriptionKind.Paid), Now).Outcome);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NotEligible, TrialDataRetentionPolicy.Decide(Facts(kind: SubscriptionKind.Unknown), Now).Outcome);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NotEligible, TrialDataRetentionPolicy.Decide(Facts(status: SubscriptionStatus.Trial), Now).Outcome);
        Assert.Equal(TrialDataRetentionPolicy.Outcome.NotEligible, TrialDataRetentionPolicy.Decide(Facts(deleted: true), Now).Outcome);
        // ค่าติดลบของแอดมิน = ไม่ขยาย/ไม่ย่นเป็นลบ
        Assert.Equal(Now.AddDays(-200), TrialDataRetentionPolicy.DueAt(Now.AddDays(-200), -5, -9));
    }

    // ═════════ ด่านอ่านของทดลองใช้หมดอายุ (231-10 · BlockAccessOnExpiry / AllowDataExportAfterExpiry) ═════════

    private static SubscriptionGateFacts Read(bool blockReads, bool? planActive, SubscriptionWriteGateMode mode = SubscriptionWriteGateMode.Enforce,
        bool export = false, bool allowExport = false)
        => new(SubscriptionStatus.Expired, FeatureFlags.TrialFeatures, IsWrite: false, mode, CompanySuspended: false, planActive, RequiredFeature: null,
            BlockReadsWhenExpired: blockReads, IsDataExport: export, AllowExportWhenExpired: allowExport);

    [Fact]
    public void เปิดสวิตช์_หมดอายุพ้นผ่อนผัน_บล็อกอ่าน_Enforce_และ_logอย่างเดียวในShadow()
    {
        var v = SubscriptionGatePolicy.Decide(Read(blockReads: true, planActive: false));
        Assert.Equal(SubscriptionGateReason.PlanExpired, v.Block);
        var shadow = SubscriptionGatePolicy.Decide(Read(blockReads: true, planActive: false, mode: SubscriptionWriteGateMode.LogOnly));
        Assert.Equal(SubscriptionGateReason.None, shadow.Block);
        Assert.Contains(SubscriptionGateReason.PlanExpired, shadow.LogOnly);
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่เปิดสวิตช์_หรือยังอยู่ในผ่อนผัน_หรือโหมดOff_อ่านได้ตามเดิม()
    {
        Assert.Equal(SubscriptionGateReason.None, SubscriptionGatePolicy.Decide(Read(blockReads: false, planActive: false)).Block);
        Assert.Equal(SubscriptionGateReason.None, SubscriptionGatePolicy.Decide(Read(blockReads: true, planActive: true)).Block);
        Assert.Equal(SubscriptionGateReason.None, SubscriptionGatePolicy.Decide(Read(blockReads: true, planActive: null)).Block);
        Assert.Equal(SubscriptionGateReason.None, SubscriptionGatePolicy.Decide(Read(blockReads: true, planActive: false, mode: SubscriptionWriteGateMode.Off)).Block);
    }

    [Fact]
    public void ส่งออกข้อมูล_ผ่านเมื่ออนุญาต_บล็อกเมื่อไม่อนุญาต()
    {
        Assert.Equal(SubscriptionGateReason.None, SubscriptionGatePolicy.Decide(Read(true, false, export: true, allowExport: true)).Block);
        Assert.Equal(SubscriptionGateReason.PlanExpired, SubscriptionGatePolicy.Decide(Read(true, false, export: true, allowExport: false)).Block);
        Assert.Equal(SubscriptionGateReason.PlanExpired, SubscriptionGatePolicy.Decide(Read(true, false, export: false, allowExport: true)).Block);
        Assert.True(SubscriptionGatePolicy.IsDataExportPath("/api/companies/x/reports/export"));
        Assert.True(SubscriptionGatePolicy.IsDataExportPath("/api/companies/x/dsr/portability"));
        Assert.True(SubscriptionGatePolicy.IsDataExportPath("/api/companies/x/documents/abc/pdf"));
        Assert.False(SubscriptionGatePolicy.IsDataExportPath("/api/companies/x/documents"));
        Assert.False(SubscriptionGatePolicy.IsDataExportPath(null));
    }

    [Fact]
    public void NeedsReadFactsWhenExpired_โหลดข้อเท็จจริงเฉพาะทดลองหมดอายุที่เปิดสวิตช์()
    {
        Assert.True(SubscriptionGatePolicy.NeedsReadFactsWhenExpired(SubscriptionStatus.Expired, false, SubscriptionWriteGateMode.Enforce, true));
        Assert.False(SubscriptionGatePolicy.NeedsReadFactsWhenExpired(SubscriptionStatus.Expired, false, SubscriptionWriteGateMode.Enforce, false));
        Assert.False(SubscriptionGatePolicy.NeedsReadFactsWhenExpired(SubscriptionStatus.Active, false, SubscriptionWriteGateMode.Enforce, true));
        Assert.False(SubscriptionGatePolicy.NeedsReadFactsWhenExpired(SubscriptionStatus.Expired, false, SubscriptionWriteGateMode.Off, true));
        Assert.False(SubscriptionGatePolicy.NeedsReadFactsWhenExpired(SubscriptionStatus.Expired, true, SubscriptionWriteGateMode.Enforce, true));   // คำขอเขียน = ด่านเขียนเดิมดูแล
        Assert.True(SubscriptionGatePolicy.NeedsWriteFacts(SubscriptionStatus.Active, true, SubscriptionWriteGateMode.LogOnly));   // ด่านเขียนเดิมไม่เปลี่ยน
    }

    // ═════════ override เพดาน — ตัวแปลงค่าเดิม (231-11) ═════════

    [Fact]
    public void แปลงค่าเดิมเป็นoverride_ผู้ใช้999ขึ้นไปไม่จำกัด_พื้นที่ปัดขึ้นเป็นGB()
    {
        Assert.Equal(QuotaLimitPolicy.Unlimited, QuotaOverrideWriter.UsersFromLegacy(999));
        Assert.Equal(5, QuotaOverrideWriter.UsersFromLegacy(5));
        Assert.Equal(0, QuotaOverrideWriter.UsersFromLegacy(-3));
        Assert.Equal(1, QuotaOverrideWriter.StorageGbFromBytes(100L * 1024 * 1024));          // 100 MB ⇒ 1 GB
        Assert.Equal(10, QuotaOverrideWriter.StorageGbFromBytes(10L * 1024 * 1024 * 1024));
        Assert.Equal(11, QuotaOverrideWriter.StorageGbFromBytes(10L * 1024 * 1024 * 1024 + 1));
        Assert.Equal(QuotaLimitPolicy.Unlimited, QuotaOverrideWriter.StorageGbFromBytes(Accounting.Models.Constants.QuotaResources.UnlimitedBytes));
        // ค่า override ชนะช่องเดิมใน QuotaLimitPolicy (ตัวตัดสินเดิม — ยืนยันว่าทางเข้าใหม่มีผลกับ hub)
        Assert.Equal((int?)7, QuotaLimitPolicy.Effective(7, null, false, 3, Array.Empty<QuotaLimitPolicy.Purchase>(), Now).Effective);
    }
}
