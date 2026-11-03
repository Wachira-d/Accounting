using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 229 (erp-review/2026-11-02/QUOTA_ADDON_SUBSCRIPTION_AUDIT.md · คำตัดสินข้อ 229-1..7) — โควตาเสริม:
/// เพดานที่มีผล (<see cref="QuotaLimitPolicy"/>) · ตัวคิดยอด 3 วิธีซื้อ (<see cref="QuotaAddOnPricing"/>) · พื้นที่ (<see cref="QuotaStorageLimit"/>) ·
/// แพ็กเอกสารเทียบข้อเสนอที่เห็น · รหัสส่วนเสริมปิด · เงินโควตาเสริมเป็นรายได้แพลตฟอร์ม
/// <para>ทุกด่านมีทั้งทิศ "ถูกบล็อก" และทิศตรงข้าม "ไม่ถูกแตะ" (F2 ข้อ 8)</para>
/// </summary>
public class QuotaAddOnRound229Tests
{
    private static readonly DateTime Now = new(2026, 11, 15, 3, 0, 0, DateTimeKind.Utc);   // 15/11/2569 10:00 ไทย

    private static QuotaLimitPolicy.Purchase Live(int units, int size = 1)
        => new(units, size, QuotaAddOnStatus.Active, Now.AddDays(-1), Now.AddDays(10));

    // ═════════ เพดานที่มีผล ═════════

    [Fact]
    public void ไม่ตั้งอะไรเลย_ไม่จำกัด_deployแล้วผู้เช่าเดิมไม่ถูกบล็อก()
    {
        var l = QuotaLimitPolicy.Effective(null, null, false, null, Array.Empty<QuotaLimitPolicy.Purchase>(), Now);
        Assert.True(l.IsUnlimited);
        Assert.Equal("default", l.Source);
        Assert.True(QuotaLimitPolicy.Decide(QuotaResources.CmsSites, 500, 1, l, canBuy: true).Allowed);
    }

    [Fact]
    public void override_ชนะ_แพ็กเกจและช่องเดิม()
    {
        var l = QuotaLimitPolicy.Effective(5, new QuotaLimitPolicy.Allowance(2, null), false, 1, new[] { Live(1) }, Now);
        Assert.Equal(5, l.Included);
        Assert.Equal(6, l.Effective);
        Assert.Equal("override", l.Source);
    }

    [Fact]
    public void ลำดับ_แพ็กเกจก่อนช่องเดิม_ช่องเดิมเมื่อไม่มีแพ็กเกจ()
    {
        Assert.Equal(3, QuotaLimitPolicy.Effective(null, new QuotaLimitPolicy.Allowance(3, null), false, 10, Array.Empty<QuotaLimitPolicy.Purchase>(), Now).Effective);
        Assert.Equal(10, QuotaLimitPolicy.Effective(null, null, false, 10, Array.Empty<QuotaLimitPolicy.Purchase>(), Now).Effective);
    }

    [Fact]
    public void ทดลองใช้_ใช้เพดานทดลอง_ไม่ตั้ง_ใช้ค่าปกติ()
    {
        Assert.Equal(1, QuotaLimitPolicy.Effective(null, new QuotaLimitPolicy.Allowance(5, 1), true, null, Array.Empty<QuotaLimitPolicy.Purchase>(), Now).Effective);
        Assert.Equal(5, QuotaLimitPolicy.Effective(null, new QuotaLimitPolicy.Allowance(5, null), true, null, Array.Empty<QuotaLimitPolicy.Purchase>(), Now).Effective);
        Assert.Equal(5, QuotaLimitPolicy.Effective(null, new QuotaLimitPolicy.Allowance(5, 1), false, null, Array.Empty<QuotaLimitPolicy.Purchase>(), Now).Effective);
    }

    [Fact]
    public void ค่าลบ_ไม่จำกัด_แม้มีการซื้อ()
    {
        var l = QuotaLimitPolicy.Effective(-1, null, false, 3, new[] { Live(2) }, Now);
        Assert.True(l.IsUnlimited);
    }

    [Fact]
    public void นับเฉพาะที่ซื้อและยังมีผล_รอตรวจ_หมดอายุ_ยังไม่เริ่ม_ไม่นับ()
    {
        var purchases = new[]
        {
            Live(2, 10),                                                                                  // 20 หน่วย
            new QuotaLimitPolicy.Purchase(5, 1, QuotaAddOnStatus.PendingReview, Now.AddDays(-1), null),    // สลิปรอตรวจ — ยังไม่ให้สิทธิ์
            new QuotaLimitPolicy.Purchase(5, 1, QuotaAddOnStatus.Active, Now.AddDays(-40), Now.AddDays(-1)), // หมดอายุแล้ว
            new QuotaLimitPolicy.Purchase(5, 1, QuotaAddOnStatus.Active, Now.AddDays(1), null),             // ยังไม่เริ่ม
            new QuotaLimitPolicy.Purchase(7, 1, QuotaAddOnStatus.Active, Now.AddDays(-3), null),            // รายเดือนไม่มีกำหนดสิ้นสุด
        };
        Assert.Equal(27, QuotaLimitPolicy.PurchasedUnits(purchases, Now));
        // ขอบเขต [start, end): ถึงวันสิ้นสุดพอดี = หมดแล้ว
        Assert.False(QuotaLimitPolicy.IsLive(QuotaAddOnStatus.Active, Now.AddDays(-1), Now, Now));
        Assert.True(QuotaLimitPolicy.IsLive(QuotaAddOnStatus.Active, Now, Now.AddSeconds(1), Now));
    }

    [Fact]
    public void เต็มแล้ว_บล็อกการเพิ่ม_ข้อความมีทางไปต่อสองทาง_และRuleCode()
    {
        var l = QuotaLimitPolicy.Effective(null, new QuotaLimitPolicy.Allowance(3, null), false, null, Array.Empty<QuotaLimitPolicy.Purchase>(), Now);
        var d = QuotaLimitPolicy.Decide(QuotaResources.CmsSites, 3, 1, l, canBuy: true, "เว็บละ ฿199.00/เดือน");
        Assert.False(d.Allowed);
        Assert.Equal("QUOTA-FULL:cms.sites", d.RuleCode);
        Assert.Equal("/pages/billing.html#buy=cms.sites", d.BuyUrl);
        Assert.Contains("ลบเว็บที่ไม่ใช้", d.BlockReason);
        Assert.Contains("ซื้อเพิ่ม", d.BlockReason);
        Assert.Contains("฿199.00", d.BlockReason);
        // คำตัดสินข้อ 229-2: บอกตรง ๆ ว่าของเดิมไม่ถูกแตะ
        Assert.Contains("ของเดิมทั้งหมดยังใช้และแก้ไขได้", d.BlockReason);
    }

    [Fact]
    public void ทิศตรงข้าม_ยังไม่เต็ม_ผ่าน_และเกินเพดานหลังดาวน์เกรด_ยังไม่มีคำสั่งลบ_แค่บล็อกการเพิ่ม()
    {
        var l = QuotaLimitPolicy.Effective(null, new QuotaLimitPolicy.Allowance(3, null), false, null, Array.Empty<QuotaLimitPolicy.Purchase>(), Now);
        Assert.True(QuotaLimitPolicy.Decide(QuotaResources.CmsSites, 2, 1, l, canBuy: false).Allowed);
        // ใช้อยู่ 5 แต่เพดานใหม่ 3 (ดาวน์เกรด) ⇒ เพิ่มไม่ได้ · ผลมีแค่ "ไม่อนุญาตให้เพิ่ม" (ด่านไม่มีทางลบ/ปิดของเดิม)
        var over = QuotaLimitPolicy.Decide(QuotaResources.CmsSites, 5, 1, l, canBuy: false);
        Assert.False(over.Allowed);
        Assert.Equal(0, over.Remaining);
        Assert.Contains("ติดต่อผู้ดูแลระบบ", over.BlockReason);   // ไม่เปิดขาย ⇒ ไม่ชี้ไปหน้าซื้อที่ไม่มี
        Assert.Null(over.BuyUrl);
    }

    [Fact]
    public void ซื้อแล้วสร้างได้ทันที()
    {
        var before = QuotaLimitPolicy.Effective(null, new QuotaLimitPolicy.Allowance(3, null), false, null, Array.Empty<QuotaLimitPolicy.Purchase>(), Now);
        Assert.False(QuotaLimitPolicy.Decide(QuotaResources.CmsSites, 3, 1, before, true).Allowed);
        var after = QuotaLimitPolicy.Effective(null, new QuotaLimitPolicy.Allowance(3, null), false, null, new[] { Live(1) }, Now);
        Assert.True(QuotaLimitPolicy.Decide(QuotaResources.CmsSites, 3, 1, after, true).Allowed);
    }

    [Fact]
    public void ระดับเตือน_80และเต็ม_ไม่จำกัดไม่เตือน()
    {
        Assert.Equal(0, QuotaLimitPolicy.WarnLevel(7, 10));
        Assert.Equal(1, QuotaLimitPolicy.WarnLevel(8, 10));
        Assert.Equal(2, QuotaLimitPolicy.WarnLevel(10, 10));
        Assert.Equal(2, QuotaLimitPolicy.WarnLevel(12, 10));
        Assert.Equal(0, QuotaLimitPolicy.WarnLevel(1000, null));
    }

    // ═════════ ตัวคิดยอด ═════════

    private static QuotaAddOnPricing.Offer Site(decimal? annual = 1990m, bool minCharge = true)
        => new(QuotaResources.CmsSites, 199m, annual, QuotaAddOnMode.OneOffMonth | QuotaAddOnMode.RecurringMonthly | QuotaAddOnMode.Coterm, minCharge, null);

    // แพ็กเกจรายปี 31/03/2569 → 31/03/2570 (ไทย) · วันหมดอายุเก็บเป็น UTC ของเที่ยงคืนไทย
    private static readonly QuotaAddOnPricing.PlanContext AnnualPlan =
        new(true, false, false, new DateTime(2027, 3, 30, 17, 0, 0, DateTimeKind.Utc), 12);

    [Fact]
    public void ถึงวันหมดอายุ_ตัวอย่างในเอกสารตรงเป๊ะ_ราคาปี()
    {
        var q = QuotaAddOnPricing.Compute(Site(), QuotaAddOnMode.Coterm, 2, AnnualPlan, Now);
        Assert.True(q.Allowed);
        Assert.Equal(741.48m, q.PerUnit);       // round(1,990 × 136 / 365)
        Assert.Equal(1482.96m, q.Amount);
        Assert.Contains("136/365", q.Note);
        Assert.Equal(AnnualPlan.PlanEndUtc, q.PeriodEndUtc);
    }

    [Fact]
    public void ถึงวันหมดอายุ_ไม่ตั้งราคาปี_ใช้เดือนคูณ12_ปัดAwayFromZero()
    {
        var q = QuotaAddOnPricing.Compute(Site(annual: null), QuotaAddOnMode.Coterm, 2, AnnualPlan, Now);
        Assert.Equal(889.78m, q.PerUnit);       // round(2,388 × 136 / 365 = 889.775…)
        Assert.Equal(1779.56m, q.Amount);
    }

    [Fact]
    public void ยอดบรรทัด_เท่ากับ_round_จำนวนคูณราคาต่อหน่วย_เสมอ()
    {
        foreach (var units in new[] { 1, 2, 3, 7, 13 })
            foreach (var mode in new[] { QuotaAddOnMode.OneOffMonth, QuotaAddOnMode.RecurringMonthly, QuotaAddOnMode.Coterm })
            {
                var q = QuotaAddOnPricing.Compute(Site(), mode, units, AnnualPlan, Now);
                Assert.Equal(Math.Round(units * q.PerUnit, 2, MidpointRounding.AwayFromZero), q.Amount);
            }
        Assert.Equal(2.49m, QuotaAddOnPricing.R2(2.485m));   // AwayFromZero — banker's จะได้ 2.48
        Assert.Equal(0.13m, QuotaAddOnPricing.R2(0.125m));   // banker's จะได้ 0.12
    }

    [Fact]
    public void ถึงวันหมดอายุ_เหลือไม่ถึงวัน_ปฏิเสธ_ให้ซื้อพร้อมต่ออายุ()
    {
        var plan = AnnualPlan with { PlanEndUtc = Now.AddHours(5) };   // วันเดียวกันตามเวลาไทย
        var q = QuotaAddOnPricing.Compute(Site(), QuotaAddOnMode.Coterm, 1, plan, Now);
        Assert.False(q.Allowed);
        Assert.Contains("ต่ออายุ", q.DenyReason);
    }

    [Fact]
    public void ฟรีถาวรและทดลองใช้_ไม่มีโหมดถึงวันหมดอายุ_แต่เดือนเดียวยังซื้อได้()
    {
        Assert.False(QuotaAddOnPricing.Compute(Site(), QuotaAddOnMode.Coterm, 1, AnnualPlan with { IsPermanentFree = true }, Now).Allowed);
        Assert.False(QuotaAddOnPricing.Compute(Site(), QuotaAddOnMode.Coterm, 1, AnnualPlan with { IsTrial = true }, Now).Allowed);
        Assert.True(QuotaAddOnPricing.Compute(Site(), QuotaAddOnMode.OneOffMonth, 1, AnnualPlan with { IsPermanentFree = true }, Now).Allowed);
    }

    [Fact]
    public void ยอดขั้นต่ำหนึ่งเดือน_เปิด_ใช้ราคาเดือน_ปิด_คิดตามวันจริง()
    {
        var plan = AnnualPlan with { PlanEndUtc = new DateTime(2026, 11, 19, 17, 0, 0, DateTimeKind.Utc) };   // เหลือ 5 วัน
        var withMin = QuotaAddOnPricing.Compute(Site(), QuotaAddOnMode.Coterm, 2, plan, Now);
        Assert.True(withMin.MinChargeApplied);
        Assert.Equal(199m, withMin.PerUnit);
        Assert.Equal(398m, withMin.Amount);
        var noMin = QuotaAddOnPricing.Compute(Site(minCharge: false), QuotaAddOnMode.Coterm, 2, plan, Now);
        Assert.False(noMin.MinChargeApplied);
        Assert.True(noMin.PerUnit < 199m);
    }

    [Fact]
    public void รายเดือน_เดือนแรกคิดตามวันที่เหลือรวมวันนี้_เดือนถัดไปเต็ม()
    {
        // 15 พ.ย. (30 วัน) ⇒ เหลือ 16 วัน ⇒ round(199 × 16 / 30) = 106.13
        var q = QuotaAddOnPricing.Compute(Site(), QuotaAddOnMode.RecurringMonthly, 1, AnnualPlan, Now);
        Assert.Equal(106.13m, q.PerUnit);
        Assert.Equal(199m, q.NextMonthlyAmount);
        Assert.Null(q.PeriodEndUtc);
        Assert.Equal(398m, QuotaAddOnPricing.RecurringMonthAmount(2, 199m));
    }

    [Fact]
    public void เดือนเดียว_ราคาเต็ม_ถึงสิ้นเดือนไทย()
    {
        var q = QuotaAddOnPricing.Compute(Site(), QuotaAddOnMode.OneOffMonth, 3, AnnualPlan, Now);
        Assert.Equal(199m, q.PerUnit);
        Assert.Equal(597m, q.Amount);
        Assert.Equal(new DateTime(2026, 11, 30, 17, 0, 0, DateTimeKind.Utc), q.PeriodEndUtc);   // 1 ธ.ค. 00:00 ไทย
        // ข้ามเดือนตามเวลาไทย: 31 ต.ค. 18:00Z = 1 พ.ย. 01:00 ไทย ⇒ สิ้นสุด 1 ธ.ค. ไทย
        Assert.Equal(new DateTime(2026, 11, 30, 17, 0, 0, DateTimeKind.Utc),
            QuotaAddOnPricing.NextThaiMonthStartUtc(new DateTime(2026, 10, 31, 18, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void ทรัพยากรใช้แล้วหมด_ขายได้เฉพาะเดือนเดียว_อายุตั้งต้น60วัน()
    {
        var ocr = new QuotaAddOnPricing.Offer(QuotaResources.OcrPagesMonth, 500m, null,
            QuotaAddOnMode.OneOffMonth | QuotaAddOnMode.RecurringMonthly | QuotaAddOnMode.Coterm, true, null, 500);
        Assert.Equal(QuotaAddOnMode.OneOffMonth, QuotaAddOnPricing.ModesFor(ocr.ResourceKey, ocr.AllowedModes));
        Assert.False(QuotaAddOnPricing.Compute(ocr, QuotaAddOnMode.RecurringMonthly, 1, AnnualPlan, Now).Allowed);
        var q = QuotaAddOnPricing.Compute(ocr, QuotaAddOnMode.OneOffMonth, 1, AnnualPlan, Now);
        Assert.Equal(Now.AddDays(60), q.PeriodEndUtc);
        Assert.NotNull(QuotaAddOnPricing.ValidateOffer(ocr.ResourceKey, 500, 500m, null, QuotaAddOnMode.RecurringMonthly, null, null));
    }

    [Fact]
    public void แพ็กเกจไม่active_ไม่มีราคา_จำนวนผิด_ปฏิเสธ()
    {
        Assert.False(QuotaAddOnPricing.Compute(Site(), QuotaAddOnMode.OneOffMonth, 1, AnnualPlan with { PlanActive = false }, Now).Allowed);
        Assert.False(QuotaAddOnPricing.Compute(Site() with { MonthlyUnitPrice = 0m }, QuotaAddOnMode.OneOffMonth, 1, AnnualPlan, Now).Allowed);
        Assert.False(QuotaAddOnPricing.Compute(Site(), QuotaAddOnMode.OneOffMonth, 0, AnnualPlan, Now).Allowed);
        Assert.False(QuotaAddOnPricing.Compute(Site() with { AllowedModes = QuotaAddOnMode.OneOffMonth }, QuotaAddOnMode.Coterm, 1, AnnualPlan, Now).Allowed);
    }

    [Fact]
    public void ข้อเสนอเปลี่ยน_409_ตรง_ผ่าน()
    {
        var q = QuotaAddOnPricing.Compute(Site(), QuotaAddOnMode.Coterm, 2, AnnualPlan, Now);
        Assert.Null(QuotaAddOnPricing.OfferChanged(741.48m, 1482.96m, q));
        Assert.Null(QuotaAddOnPricing.OfferChanged(null, null, q));
        Assert.NotNull(QuotaAddOnPricing.OfferChanged(null, 1400m, q));
        Assert.NotNull(QuotaAddOnPricing.OfferChanged(700m, null, q));
    }

    [Fact]
    public void บรรทัดต่ออายุ_ราคาเต็มรอบ_รายปีใช้ราคาปี_รายเดือนใช้เดือนคูณจำนวนเดือน()
    {
        Assert.Equal((1990m, 3980m), QuotaAddOnPricing.RenewalLine(Site(), 2, 12));
        Assert.Equal((2388m, 2388m), QuotaAddOnPricing.RenewalLine(Site(annual: null), 1, 12));
        Assert.Equal((597m, 1194m), QuotaAddOnPricing.RenewalLine(Site(), 2, 3));
    }

    [Fact]
    public void ตรวจข้อเสนอของแอดมิน_ทรัพยากรนอกรายการ_ต่อเว็บ_ปฏิเสธ()
    {
        Assert.NotNull(QuotaAddOnPricing.ValidateOffer("lodging.promo", 1, 100m, null, QuotaAddOnMode.OneOffMonth, null, null));
        Assert.NotNull(QuotaAddOnPricing.ValidateOffer(QuotaResources.CmsPagesPerSite, 1, 100m, null, QuotaAddOnMode.OneOffMonth, null, null));
        Assert.NotNull(QuotaAddOnPricing.ValidateOffer(QuotaResources.CmsSites, 1, -1m, null, QuotaAddOnMode.OneOffMonth, null, null));
        Assert.Null(QuotaAddOnPricing.ValidateOffer(QuotaResources.CmsSites, 1, 199m, 1990m, QuotaAddOnMode.Coterm | QuotaAddOnMode.OneOffMonth, 10, null));
    }

    // ═════════ พื้นที่ · แพ็กเอกสาร · รหัสปิด · เงินแพลตฟอร์ม ═════════

    [Fact]
    public void พื้นที่_override_ชนะ_ซื้อเพิ่มบวก_ไม่ตั้งเท่าเดิม()
    {
        const long gb = QuotaResources.BytesPerGb;
        Assert.Equal(5 * gb, QuotaStorageLimit.Compute(5 * gb, null, 0));        // ทิศตรงข้าม: ไม่มีแถว = เพดานเดิมทุกไบต์
        Assert.Equal(15 * gb, QuotaStorageLimit.Compute(5 * gb, null, 10));
        Assert.Equal(22 * gb, QuotaStorageLimit.Compute(5 * gb, 20, 2));
        Assert.Equal(QuotaResources.UnlimitedBytes, QuotaStorageLimit.Compute(5 * gb, -1, 2));
        Assert.True(QuotaResources.UnlimitedBytes + 10 * gb > 0);   // ไม่ล้น (used + เพิ่ม ยังเป็นบวก)
    }

    [Fact]
    public void แพ็กเอกสาร_ข้อเสนอที่เห็นต้องตรง()
    {
        Assert.Null(DocumentQuotaPolicy.TopUpOfferChanged(null, null, 100, 49m));
        Assert.Null(DocumentQuotaPolicy.TopUpOfferChanged(100, 49m, 100, 49m));
        Assert.NotNull(DocumentQuotaPolicy.TopUpOfferChanged(100, 39m, 100, 49m));
        Assert.NotNull(DocumentQuotaPolicy.TopUpOfferChanged(50, 49m, 100, 49m));
        Assert.NotNull(DocumentQuotaPolicy.TopUpOfferChanged(100, 0m, 100, 49m));
    }

    [Fact]
    public void รหัสส่วนเสริมใหม่_ต้องอยู่ในรายการที่โค้ดอ่าน()
    {
        Assert.True(AddOnCodes.IsKnown(AddOnCodes.LodgingGuestPortal));
        Assert.True(AddOnCodes.IsKnown(AddOnCodes.NotifyTopUp));
        Assert.True(AddOnCodes.IsKnown("ocr.scan"));
        Assert.False(AddOnCodes.IsKnown("cms.sites"));      // A-1: เดิมสร้างได้แต่ไม่มีผู้อ่าน
        Assert.False(AddOnCodes.IsKnown("etax.generate"));  // seed แล้วแต่ไม่มี controller คิดเงิน
        Assert.False(AddOnCodes.IsKnown(""));
    }

    [Fact]
    public void เงินโควตาเสริมเป็นรายได้แพลตฟอร์ม_ไม่ไปตกgatewayร้าน()
    {
        Assert.True(PlatformRevenueSource.IsPlatformRevenue(PaymentSourceKind.QuotaAddOnPurchase));
        var tenant = Guid.NewGuid(); var platform = Guid.NewGuid();
        Assert.NotNull(PlatformRevenueSource.StartDenyReason(PaymentSourceKind.QuotaAddOnPurchase, tenant, platform, tenant));
        Assert.Null(PlatformRevenueSource.StartDenyReason(PaymentSourceKind.QuotaAddOnPurchase, platform, platform, tenant));
        Assert.NotNull(PlatformRevenueSource.EntitlementBlockReason(PaymentSourceKind.QuotaAddOnPurchase, platform, platform, tenant, isTestMode: true));
        Assert.Equal("โควตาเสริม", PaymentIntentPolicy.SourceKindLabel(PaymentSourceKind.QuotaAddOnPurchase));
    }

    [Fact]
    public void ทรัพยากร_closed_list_มีด่าน_และชื่อบนใบแจ้งหนี้()
    {
        Assert.All(QuotaResources.All, r => Assert.True(QuotaResources.IsKnown(r.Key)));
        Assert.Equal(QuotaResources.All.Count, QuotaResources.All.Select(r => r.Key).Distinct().Count());
        Assert.Equal("โควตาเสริม เว็บไซต์ (CMS) (รายเดือน)", QuotaResources.InvoiceName(QuotaResources.OfferCode(QuotaResources.CmsSites)));
        Assert.Null(QuotaResources.InvoiceName("documents.topup"));
        Assert.Equal("QADD-SLIP-" + Guid.Empty.ToString("N"), Accounting.Services.Payments.QuotaAddOnPurchaseService.ReceiptReference(Guid.Empty, null));
    }

    // ═════════ ฝ่ายค้านรอบ 229 ═════════

    [Fact]
    public void B2_ข้อเสนอล้าสมัย_รายเดือนข้ามเดือน_ถึงวันหมดอายุแพ็กเกจเปลี่ยน_ช่วงจบแล้ว()
    {
        var quoted = new DateTime(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc);
        var nextMonth = new DateTime(2026, 12, 2, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(QuotaAddOnPricing.Staleness.MonthChanged, QuotaAddOnPricing.StaleCheck(QuotaAddOnMode.RecurringMonthly, quoted, null, null, nextMonth));
        Assert.Equal(QuotaAddOnPricing.Staleness.Fresh, QuotaAddOnPricing.StaleCheck(QuotaAddOnMode.RecurringMonthly, quoted, null, null, quoted.AddDays(3)));
        var planEnd = AnnualPlan.PlanEndUtc;
        Assert.Equal(QuotaAddOnPricing.Staleness.Fresh, QuotaAddOnPricing.StaleCheck(QuotaAddOnMode.Coterm, quoted, planEnd, planEnd, quoted.AddDays(1)));
        Assert.Equal(QuotaAddOnPricing.Staleness.PlanEndChanged,
            QuotaAddOnPricing.StaleCheck(QuotaAddOnMode.Coterm, quoted, planEnd, planEnd.AddYears(1), quoted.AddDays(1)));
        // เดือนเดียวถึงสิ้นเดือน จ่ายเดือนหน้า ⇒ ช่วงจบแล้ว (ไม่เปิดสิทธิ์ช่วงศูนย์วัน)
        var monthEnd = QuotaAddOnPricing.NextThaiMonthStartUtc(quoted);
        Assert.Equal(QuotaAddOnPricing.Staleness.PeriodEnded, QuotaAddOnPricing.StaleCheck(QuotaAddOnMode.OneOffMonth, quoted, monthEnd, null, nextMonth));
    }

    [Fact]
    public void B2_จ่ายช้า_เดือนเดียวแบบกำหนดวัน_เริ่มนับจากวันจ่ายยาวเท่าเดิม_แบบถึงสิ้นเดือนไม่ขยาย()
    {
        var quoted = new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);
        var end = quoted.AddDays(60);
        Assert.True(QuotaAddOnPricing.IsFixedDays(QuotaAddOnMode.OneOffMonth, quoted, end));
        var paid = quoted.AddDays(10);
        Assert.Equal(paid.AddDays(60), QuotaAddOnPricing.LatePaymentEnd(QuotaAddOnMode.OneOffMonth, true, quoted, end, paid));
        var monthEnd = QuotaAddOnPricing.NextThaiMonthStartUtc(quoted);
        Assert.False(QuotaAddOnPricing.IsFixedDays(QuotaAddOnMode.OneOffMonth, quoted, monthEnd));
        Assert.Null(QuotaAddOnPricing.LatePaymentEnd(QuotaAddOnMode.OneOffMonth, false, quoted, monthEnd, paid));
        Assert.Null(QuotaAddOnPricing.LatePaymentEnd(QuotaAddOnMode.Coterm, true, quoted, end, paid));
        Assert.Null(QuotaAddOnPricing.LatePaymentEnd(QuotaAddOnMode.OneOffMonth, true, quoted, end, quoted.AddMinutes(-1)));   // ทิศตรงข้าม: จ่ายทันที = ไม่แตะ
    }

    [Fact]
    public void S9_ใบต่ออายุหลายบรรทัด_ยอดใบเท่ากับยอดที่คิด_ภายในหนึ่งสตางค์ต่อบรรทัด()
    {
        // แพ็กเกจ + เว็บ 2 × 1,990 + เว็บ 3 × 597 (รายไตรมาส) + พื้นที่ 1 × 2,388 — ราคารวม VAT (ค่าตั้งต้น)
        var lines = new[] { (Amount: 9990m, Qty: 1m), (3980m, 2m), (1791m, 3m), (2388m, 1m), (741.48m, 2m) };
        decimal expected = 0m, gross = 0m;
        foreach (var (amount, qty) in lines)
        {
            var r = PlatformInvoiceLineMath.Line(amount, qty, isVat: true, priceIncludesVat: true);
            Assert.Equal(Math.Round(r.Net * 0.07m, 2, MidpointRounding.AwayFromZero), r.Vat);
            Assert.Equal(Math.Round(qty * r.UnitPrice, 2, MidpointRounding.AwayFromZero), r.Net);
            Assert.True(Math.Abs(r.Gross - amount) <= 0.01m, $"บรรทัด {amount} ได้ {r.Gross}");
            expected += amount; gross += r.Gross;
        }
        Assert.True(Math.Abs(gross - expected) <= 0.01m * lines.Length);
        Assert.Equal(3980m, PlatformInvoiceLineMath.Line(3980m, 2m, true, true).Gross);   // ตัวอย่างหลักตรงเป๊ะ
        // ไม่จด VAT ⇒ ยอด = ยอดที่คิด · ราคาไม่รวม VAT ⇒ บวก VAT บนฐาน
        Assert.Equal(1791m, PlatformInvoiceLineMath.Line(1791m, 3m, false, true).Gross);
        Assert.Equal(1916.37m, PlatformInvoiceLineMath.Line(1791m, 3m, true, false).Gross);
    }

    [Fact]
    public void S5_ชุดรายได้แพลตฟอร์มตัวเดียว_มีโควตาเสริม()
    {
        Assert.Contains(PaymentSourceKind.QuotaAddOnPurchase, PlatformRevenueSource.Kinds);
        Assert.DoesNotContain(PaymentSourceKind.SiteOrder, PlatformRevenueSource.Kinds);
        Assert.All(PlatformRevenueSource.Kinds, k => Assert.True(PlatformRevenueSource.IsPlatformRevenue(k)));
    }
}
