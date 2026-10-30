using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 219 เฟส 0 (PLATFORM_BILLING_AUDIT · PLATFORM_GATEWAY_REFUND_POLICY_DESIGN · คำตัดสินข้อ 178) — ตัวตัดสิน pure ของการอุดรั่วเงินค่าบริการ NextAcc ·
/// ทุกด่านล็อกทั้งทิศ "ถูกปิด" และทิศ "ของเดิมที่ถูกต้องไม่ถูกแตะ" (กฎเหล็ก #4 F2 ข้อ 8)
/// </summary>
public class PlatformBillingPhase0Tests
{
    private static readonly Guid Platform = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Other = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime Now = new(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);

    // ═══ PlatformRevenueSource — ชนิดไหนคือเงินของแพลตฟอร์ม ═══

    [Theory]
    [InlineData(PaymentSourceKind.SubscriptionPayment, true)]
    [InlineData(PaymentSourceKind.AddOnPurchase, true)]
    [InlineData(PaymentSourceKind.Document, false)]
    [InlineData(PaymentSourceKind.SiteOrder, false)]
    [InlineData(PaymentSourceKind.LodgingReservation, false)]
    [InlineData(PaymentSourceKind.PosOrder, false)]
    public void Platform_revenue_kinds_are_exactly_subscription_and_addon(PaymentSourceKind kind, bool expected)
        => Assert.Equal(expected, PlatformRevenueSource.IsPlatformRevenue(kind));

    [Fact]
    public void Start_tenant_side_platform_intent_is_refused_store_intents_untouched()
    {
        // G-1: ร้านเริ่มรายการค่าส่วนเสริมใต้บริษัทตัวเอง (= เงินเข้า gateway ของร้าน) ⇒ ปฏิเสธ
        Assert.Equal(PlatformRevenueSource.TenantSideMessage,
            PlatformRevenueSource.StartDenyReason(PaymentSourceKind.AddOnPurchase, Tenant, Platform, Tenant));
        // ยังไม่ตั้งบริษัทผู้ให้บริการ ⇒ ปฏิเสธ (ไม่ตกไปใช้ gateway ใด)
        Assert.NotNull(PlatformRevenueSource.StartDenyReason(PaymentSourceKind.SubscriptionPayment, Tenant, null, Tenant));
        // ใต้บริษัทผู้ให้บริการแต่ไม่รู้ผู้จ่าย ⇒ ปฏิเสธ
        Assert.NotNull(PlatformRevenueSource.StartDenyReason(PaymentSourceKind.SubscriptionPayment, Platform, Platform, null));
        // ทางที่ถูก (เฟส 1): ใต้บริษัทผู้ให้บริการ + ระบุผู้จ่าย ⇒ ผ่าน
        Assert.Null(PlatformRevenueSource.StartDenyReason(PaymentSourceKind.AddOnPurchase, Platform, Platform, Tenant));
        // ทิศตรงข้าม: ออเดอร์/ใบแจ้งหนี้ของร้าน ไม่ถูกแตะ ไม่ว่าตั้งค่าแพลตฟอร์มหรือไม่
        Assert.Null(PlatformRevenueSource.StartDenyReason(PaymentSourceKind.SiteOrder, Tenant, Platform, null));
        Assert.Null(PlatformRevenueSource.StartDenyReason(PaymentSourceKind.Document, Tenant, null, null));
        Assert.Null(PlatformRevenueSource.StartDenyReason(PaymentSourceKind.Document, Platform, Platform, null));
    }

    [Fact]
    public void Staff_actions_on_platform_intents_only_for_system_admin_in_platform_tenant()
    {
        // G-2: เจ้าของร้านยืนยันมือ/คืนเงินค่าบริการของตัวเอง ⇒ ปฏิเสธ
        Assert.Equal(PlatformRevenueSource.TenantSideMessage,
            PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.SubscriptionPayment, Tenant, Platform, isSystemAdmin: false));
        // ฝ่ายค้านรอบ 219 S6: รายการเก่าใต้บริษัทร้าน — แอดมินแพลตฟอร์มจัดการได้ (ให้สิทธิ์ไม่ได้อยู่แล้วที่ EntitlementBlockReason)
        Assert.Null(PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.AddOnPurchase, Tenant, Platform, isSystemAdmin: true));
        // พนักงานของบริษัทผู้ให้บริการที่ไม่ใช่แอดมินแพลตฟอร์ม ⇒ ปฏิเสธ
        Assert.NotNull(PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.AddOnPurchase, Platform, Platform, isSystemAdmin: false));
        // ฝ่ายค้านรอบ 219 รอบ 2: ยังไม่ได้ตั้งบริษัทผู้ให้บริการ ⇒ รายการทั้งหมดเป็นรายการเก่าใต้ร้าน — แอดมินแพลตฟอร์มยังจัดการได้ (goodwill)
        // (เดิม assert NotNull ขัดกับ S6 ที่ปล่อย SystemAdmin ก่อนตรวจบริษัทผู้ให้บริการ)
        Assert.Null(PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.AddOnPurchase, Platform, null, isSystemAdmin: true));
        Assert.NotNull(PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.AddOnPurchase, Tenant, null, isSystemAdmin: false));
        // ทางที่ถูก ⇒ ผ่าน
        Assert.Null(PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.SubscriptionPayment, Platform, Platform, isSystemAdmin: true));
        // ทิศตรงข้าม: เจ้าของร้านยืนยันมือรายการของลูกค้าตัวเองได้ตามเดิม
        Assert.Null(PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.Document, Tenant, Platform, isSystemAdmin: false));
        Assert.Null(PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.LodgingReservation, Tenant, null, isSystemAdmin: false));
    }

    [Theory]
    [InlineData(PlatformRevenueSource.StaffAction.Refund)]
    [InlineData(PlatformRevenueSource.StaffAction.VerifyRefund)]
    [InlineData(PlatformRevenueSource.StaffAction.ConfirmManually)]
    [InlineData(PlatformRevenueSource.StaffAction.Redispatch)]
    [InlineData(PlatformRevenueSource.StaffAction.ResolveRefundManually)]
    [InlineData(PlatformRevenueSource.StaffAction.RecordLegacyRefund)]
    public void Platform_revenue_staff_actions_including_refund_are_system_admin_only(PlatformRevenueSource.StaffAction action)
    {
        // ฝ่ายค้านรอบ 219 รอบ 2 (ข้อ 4 · คำตัดสินเจ้าของ: ค่าบริการไม่คืนเงิน — แอดมินทำ goodwill เท่านั้น): รอบแรกให้ร้านคืน/ตรวจผลคืนรายการเก่าเองได้
        // ⇒ ตั้งลูกหนี้รอใบลดหนี้ในสมุดร้านที่ไม่มีวันล้าง · ตอนนี้ร้านทำไม่ได้ทุกงาน ทั้งรายการเก่าใต้ร้านและรายการใต้บริษัทผู้ให้บริการ
        Assert.Equal(PlatformRevenueSource.TenantSideMessage,
            PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.AddOnPurchase, Tenant, Platform, false, action));
        Assert.NotNull(PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.SubscriptionPayment, Platform, Platform, false, action));
        // ทิศตรงข้าม: แอดมินแพลตฟอร์มทำได้ทุกงาน (ทั้งสองที่อยู่) · รายการของร้านเอง ร้านทำได้ตามเดิม
        Assert.Null(PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.AddOnPurchase, Tenant, Platform, true, action));
        Assert.Null(PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.SubscriptionPayment, Platform, Platform, true, action));
        Assert.Null(PlatformRevenueSource.StaffActionDenyReason(PaymentSourceKind.SiteOrder, Tenant, Platform, false, action));
    }

    [Fact]
    public void Refund_of_platform_revenue_skips_store_receivable_but_store_kinds_still_book()
    {
        Assert.True(PlatformRevenueSource.RefundSkipsStoreReceivable(PaymentSourceKind.SubscriptionPayment));
        Assert.True(PlatformRevenueSource.RefundSkipsStoreReceivable(PaymentSourceKind.AddOnPurchase));
        // ทิศตรงข้าม: รายการของร้านยังตั้งลูกหนี้รอใบลดหนี้ตามเดิม (รอบ 198 G-1)
        Assert.False(PlatformRevenueSource.RefundSkipsStoreReceivable(PaymentSourceKind.SiteOrder));
        Assert.False(PlatformRevenueSource.RefundSkipsStoreReceivable(PaymentSourceKind.Document));
        Assert.False(PlatformRevenueSource.RefundSkipsStoreReceivable(PaymentSourceKind.LodgingReservation));
    }

    [Fact]
    public void Period_start_upgrade_and_permanent_free_start_now_renewal_extends()
    {
        var end = Now.AddDays(200);
        // B1: ฟรีถาวร (EndDate +100 ปี) ⇒ เริ่มจากวันนี้ (เดิมได้ 100 ปี + 1 เดือน)
        Assert.Equal(Now, PlatformPlanPricing.PeriodStart(PlatformPlanPricing.ChangeKind.Upgrade, false, true, Now.AddYears(100), Now));
        Assert.Equal(Now, PlatformPlanPricing.PeriodStart(PlatformPlanPricing.ChangeKind.Same, true, true, Now.AddYears(100), Now));
        // อัปเกรดทันทีกลางรอบ ⇒ วันนี้ (ส่วนที่เหลือสละ — นโยบายชั่วคราวจนกว่าจะมี proration)
        Assert.Equal(Now, PlatformPlanPricing.PeriodStart(PlatformPlanPricing.ChangeKind.Upgrade, false, false, end, Now));
        // ทิศตรงข้าม: ต่ออายุแพ็กเกจเดิม / ดาวน์เกรดรอครบรอบ ⇒ ต่อจากวันหมดอายุเดิม
        Assert.Equal(end, PlatformPlanPricing.PeriodStart(PlatformPlanPricing.ChangeKind.Same, true, false, end, Now));
        Assert.Equal(end, PlatformPlanPricing.PeriodStart(PlatformPlanPricing.ChangeKind.Downgrade, false, false, end, Now));
        Assert.Equal(Now, PlatformPlanPricing.PeriodStart(PlatformPlanPricing.ChangeKind.Same, true, false, Now.AddDays(-5), Now));
    }

    [Fact]
    public void Account_cascade_trial_once_and_renewal_window_rules()
    {
        Assert.True(PlatformPlanPricing.MayCascadeToAccountPlan(SubscriptionPlan.Pro, SubscriptionPlan.Pro));
        Assert.False(PlatformPlanPricing.MayCascadeToAccountPlan(SubscriptionPlan.Basic, SubscriptionPlan.Enterprise));   // S5
        Assert.False(PlatformPlanPricing.MayCascadeToAccountPlan(SubscriptionPlan.Pro, null));
        Assert.True(PlatformPlanPricing.MayStartAccountTrial(Array.Empty<SubscriptionPlan>(), SubscriptionPlan.Pro));
        Assert.False(PlatformPlanPricing.MayStartAccountTrial(new[] { SubscriptionPlan.Pro }, SubscriptionPlan.Pro));       // S4
        Assert.True(PlatformPlanPricing.MayStartAccountTrial(new[] { SubscriptionPlan.Basic }, SubscriptionPlan.Pro));
        // S9: ออกใบแจ้งหนี้ต่ออายุของรอบนี้แล้ว ⇒ ไม่รับดาวน์เกรด · ยังไม่ออก/ออกของรอบก่อน ⇒ ได้
        Assert.NotNull(PlatformPlanPricing.ScheduleBlockedByIssuedRenewal(Now.AddDays(10), Now.AddDays(10)));
        Assert.Null(PlatformPlanPricing.ScheduleBlockedByIssuedRenewal(null, Now.AddDays(10)));
        Assert.Null(PlatformPlanPricing.ScheduleBlockedByIssuedRenewal(Now.AddDays(-20), Now.AddDays(10)));
    }

    [Fact]
    public void Payment_date_is_clamped_server_side()
    {
        Assert.Equal(Now, PlatformPlanPricing.ClampPaymentDate(default, Now, Now));
        Assert.Equal(Now, PlatformPlanPricing.ClampPaymentDate(Now.AddDays(3), Now, Now));          // อนาคต
        Assert.Equal(Now.AddDays(-31), PlatformPlanPricing.ClampPaymentDate(Now.AddYears(-1), Now, Now));
        Assert.Equal(Now.AddDays(-2), PlatformPlanPricing.ClampPaymentDate(Now.AddDays(-2), Now, Now));
    }

    [Fact]
    public void Entitlement_blocked_for_tenant_routed_unknown_payer_and_test_mode()
    {
        Assert.NotNull(PlatformRevenueSource.EntitlementBlockReason(PaymentSourceKind.AddOnPurchase, Tenant, Platform, Tenant, isTestMode: false));
        Assert.NotNull(PlatformRevenueSource.EntitlementBlockReason(PaymentSourceKind.AddOnPurchase, Tenant, null, Tenant, isTestMode: false));
        Assert.NotNull(PlatformRevenueSource.EntitlementBlockReason(PaymentSourceKind.SubscriptionPayment, Platform, Platform, null, isTestMode: false));
        // part B (a): จ่ายโหมดทดสอบ ⇒ ไม่ให้สิทธิ์จริง
        Assert.Contains("โหมดทดสอบ",
            PlatformRevenueSource.EntitlementBlockReason(PaymentSourceKind.SubscriptionPayment, Platform, Platform, Tenant, isTestMode: true));
        Assert.Null(PlatformRevenueSource.EntitlementBlockReason(PaymentSourceKind.SubscriptionPayment, Platform, Platform, Tenant, isTestMode: false));
        // ทิศตรงข้าม: ออเดอร์ของร้านในโหมดทดสอบ ยังส่งต่อตามเดิม (ไม่ใช่เงินของแพลตฟอร์ม)
        Assert.Null(PlatformRevenueSource.EntitlementBlockReason(PaymentSourceKind.SiteOrder, Tenant, Platform, null, isTestMode: true));
    }

    [Fact]
    public void Source_must_belong_to_payer()
    {
        Assert.True(PlatformRevenueSource.SourceBelongsToPayer(Tenant, Tenant));
        Assert.False(PlatformRevenueSource.SourceBelongsToPayer(Tenant, Other));   // G-3 ข้ามบริษัท
        Assert.False(PlatformRevenueSource.SourceBelongsToPayer(null, Tenant));    // ไม่รู้ผู้จ่าย = ไม่ใช่
    }

    // ═══ PlatformPlanPricing — ตัวตั้งราคาแพ็กเกจเดียว ═══

    private static PlanTemplate Tpl(decimal m, decimal q, decimal s, decimal a) => new()
    { Name = "T", Plan = SubscriptionPlan.Pro, MonthlyPrice = m, QuarterlyPrice = q, SemiAnnualPrice = s, AnnualPrice = a };

    [Theory]
    [InlineData(BillingCycle.Monthly, 990, 1)]
    [InlineData(BillingCycle.Quarterly, 2800, 3)]
    [InlineData(BillingCycle.SemiAnnual, 5400, 6)]
    [InlineData(BillingCycle.Annual, 9900, 12)]
    public void Cycle_price_and_months(BillingCycle cycle, decimal price, int months)
    {
        var t = Tpl(990m, 2800m, 5400m, 9900m);
        Assert.Equal(price, PlatformPlanPricing.CyclePrice(t, cycle));
        Assert.Equal(months, PlatformPlanPricing.MonthsOf(cycle));
        Assert.Equal(0, PlatformPlanPricing.MonthsOf((BillingCycle)99));
    }

    [Fact]
    public void Amount_due_applies_discount_and_rounds_away_from_zero()
    {
        var t = Tpl(990m, 0m, 0m, 9900m);
        Assert.Equal(9900m, PlatformPlanPricing.AmountDue(t, BillingCycle.Annual));
        Assert.Equal(8910m, PlatformPlanPricing.AmountDue(t, BillingCycle.Annual, 10m));
        // 990 × (1 − 0.125) = 866.25 (ตรง) · 990 × (1 − 0.12345) = 867.7845 → 867.78
        Assert.Equal(866.25m, PlatformPlanPricing.AmountDue(t, BillingCycle.Monthly, 12.5m));
        Assert.Equal(867.78m, PlatformPlanPricing.AmountDue(t, BillingCycle.Monthly, 12.345m));
        // ส่วนลดนอกช่วง = ไม่ลด (ไม่ติดลบ)
        Assert.Equal(990m, PlatformPlanPricing.AmountDue(t, BillingCycle.Monthly, 150m));
    }

    [Theory]
    [InlineData(0, 9900, true)]        // ไม่ได้แจ้ง = ใช้ยอดเซิร์ฟเวอร์
    [InlineData(9900, 9900, true)]
    [InlineData(9900.01, 9900, true)]
    [InlineData(1, 9900, false)]       // G-4: ฿1 ขอแพ็กเกจรายปี
    [InlineData(9600, 9900, false)]    // หัก ณ ที่จ่ายไว้ ⇒ แจ้งยอดเต็ม (แอดมินบันทึก WHT)
    public void Claimed_amount_must_match_server_price(decimal claimed, decimal due, bool ok)
        => Assert.Equal(ok, PlatformPlanPricing.ClaimedAmountAcceptable(claimed, due));

    [Fact]
    public void Classify_and_defer_downgrade_to_cycle_end()
    {
        Assert.Equal(PlatformPlanPricing.ChangeKind.Upgrade, PlatformPlanPricing.Classify(490m, 990m));
        Assert.Equal(PlatformPlanPricing.ChangeKind.Downgrade, PlatformPlanPricing.Classify(990m, 490m));
        Assert.Equal(PlatformPlanPricing.ChangeKind.Same, PlatformPlanPricing.Classify(990m, 990m));
        // ดาวน์เกรดระหว่างรอบที่ยังใช้งาน ⇒ รอวันครบรอบ · อัปเกรด ⇒ ทันที · หมดอายุแล้ว ⇒ ทันที
        Assert.True(PlatformPlanPricing.DeferToCycleEnd(PlatformPlanPricing.ChangeKind.Downgrade, SubscriptionStatus.Active, Now.AddDays(10), Now));
        Assert.False(PlatformPlanPricing.DeferToCycleEnd(PlatformPlanPricing.ChangeKind.Upgrade, SubscriptionStatus.Active, Now.AddDays(10), Now));
        Assert.False(PlatformPlanPricing.DeferToCycleEnd(PlatformPlanPricing.ChangeKind.Downgrade, SubscriptionStatus.Active, Now.AddDays(-1), Now));
        Assert.False(PlatformPlanPricing.DeferToCycleEnd(PlatformPlanPricing.ChangeKind.Downgrade, SubscriptionStatus.Trial, Now.AddDays(10), Now));
    }

    [Fact]
    public void Unpaid_self_activation_report_rule()
    {
        Assert.True(PlatformPlanPricing.IsUnpaidSelfActivation(Now, null));                 // เปิดเองแล้วไม่เคยจ่าย
        Assert.True(PlatformPlanPricing.IsUnpaidSelfActivation(Now, Now.AddDays(-30)));     // จ่ายรอบเก่า แล้วอัปเกรดเองทีหลัง
        Assert.False(PlatformPlanPricing.IsUnpaidSelfActivation(Now.AddDays(-30), Now));    // จ่ายหลังเปิด
        Assert.False(PlatformPlanPricing.IsUnpaidSelfActivation(null, null));               // ไม่เคยเปิดเอง (แอดมินตั้ง)
    }

    // ═══ AddOnPaymentPolicy — ระยะผ่อนผัน (G-11) + ไม่เก็บค่าเหมาซ้อน (G-5) ═══

    [Fact]
    public void Unpaid_addon_usable_within_grace_then_blocked()
    {
        var since = Now.AddDays(-AddOnPaymentPolicy.UnpaidGraceDays);
        Assert.True(AddOnPaymentPolicy.UsableWhilePending(AddOnPaymentStatus.AwaitingPayment, since, since.AddDays(AddOnPaymentPolicy.UnpaidGraceDays)));
        Assert.False(AddOnPaymentPolicy.UsableWhilePending(AddOnPaymentStatus.AwaitingPayment, since, since.AddDays(AddOnPaymentPolicy.UnpaidGraceDays).AddSeconds(1)));
        // แนบสลิปแล้ว รอแอดมิน ⇒ ใช้ได้แม้เกินระยะ · จ่ายแล้ว ⇒ ใช้ได้ · ปฏิเสธ ⇒ ไม่ได้
        Assert.True(AddOnPaymentPolicy.UsableWhilePending(AddOnPaymentStatus.PendingReview, since, Now.AddDays(30)));
        Assert.True(AddOnPaymentPolicy.UsableWhilePending(AddOnPaymentStatus.Paid, since, Now.AddDays(30)));
        Assert.False(AddOnPaymentPolicy.UsableWhilePending(AddOnPaymentStatus.Rejected, null, Now));
        // แถวเดิมก่อนเฟส 0 (ไม่มีเวลาเริ่มรอชำระ) ⇒ พฤติกรรมเดิม (เจ้าของตัดสิน)
        Assert.True(AddOnPaymentPolicy.UsableWhilePending(AddOnPaymentStatus.AwaitingPayment, null, Now.AddYears(1)));
        Assert.Equal(since.AddDays(7), AddOnPaymentPolicy.GraceEndsAt(since));
        Assert.Null(AddOnPaymentPolicy.GraceEndsAt(null));
    }

    [Fact]
    public void Flat_monthly_not_double_charged_for_upfront_period()
    {
        // G-5: งวดที่จ่ายล่วงหน้าแล้ว ⇒ ฿0 · งวดถัดไป ⇒ คิดตามปกติ
        var paid = AddOnBilling.FlatAmount(299m, null, null, false, Now, AddOnPaymentStatus.Paid, "2026-10", "2026-10");
        Assert.Equal((0m, "upfront-paid"), paid);
        Assert.Equal((299m, "charged"), AddOnBilling.FlatAmount(299m, null, null, false, Now, AddOnPaymentStatus.Paid, "2026-10", "2026-11"));
        // แนบสลิปแล้วรอตรวจ = ถือว่าครอบงวดนั้น
        Assert.Equal(0m, AddOnBilling.FlatAmount(299m, null, null, false, Now, AddOnPaymentStatus.PendingReview, "2026-10", "2026-10").Amount);
        // ยังไม่จ่ายล่วงหน้า ⇒ ไม่ออกบิลค่าเดือน (ถูกระงับหลังผ่อนผัน — ห้ามเก็บเงินโดยไม่มีบริการ)
        Assert.Equal((0m, "upfront-unpaid"), AddOnBilling.FlatAmount(299m, null, null, false, Now, AddOnPaymentStatus.AwaitingPayment, "2026-10", "2026-11"));
        // ทิศตรงข้าม: แถวที่ไม่ได้เข้าเส้นจ่ายล่วงหน้า (ไม่มี UpfrontPeriod) ⇒ เหมือนเดิมทุกประการ
        Assert.Equal((299m, "charged"), AddOnBilling.FlatAmount(299m, null, null, false, Now, AddOnPaymentStatus.AwaitingPayment, null, "2026-10"));
        Assert.Equal((299m, "charged"), AddOnBilling.FlatAmount(299m, null, null, false, Now));
        // trial ยังชนะ
        Assert.Equal((0m, "trial"), AddOnBilling.FlatAmount(299m, null, Now.AddDays(3), false, Now, AddOnPaymentStatus.AwaitingPayment, "2026-10", "2026-10"));
    }

    // ═══ EncryptionKeyResolver — แหล่งคีย์เดียว + fail-fast production (part B (c)) ═══

    [Fact]
    public void Encryption_key_env_wins_and_production_refuses_missing_or_dev_key()
    {
        Assert.Equal("env-key", EncryptionKeyResolver.Resolve("env-key", "cfg-key"));
        Assert.Equal("cfg-key", EncryptionKeyResolver.Resolve("  ", "cfg-key"));
        Assert.Null(EncryptionKeyResolver.Resolve(null, ""));
        // S3: คีย์ดิบไม่ถูก Trim (PBKDF2 ไวต่อช่องว่าง)
        Assert.Equal(" env-key ", EncryptionKeyResolver.Resolve(" env-key ", null));
        Assert.Throws<InvalidOperationException>(() => EncryptionKeyResolver.RequireUsable(null, isProduction: true, "test"));
        Assert.Throws<InvalidOperationException>(() => EncryptionKeyResolver.RequireUsable(EncryptionKeyResolver.DevKey, isProduction: true, "test"));
        Assert.Equal(" real-key ", EncryptionKeyResolver.RequireUsable(" real-key ", isProduction: true, "test"));
        Assert.Throws<InvalidOperationException>(() => EncryptionKeyResolver.RequireUsable(" " + EncryptionKeyResolver.DevKey + " ", isProduction: true, "test"));
        // เครื่องนักพัฒนา: ไม่มีคีย์ ⇒ dev key (พฤติกรรมเดิม)
        Assert.Equal(EncryptionKeyResolver.DevKey, EncryptionKeyResolver.RequireUsable(null, isProduction: false, "test"));
    }

    [Fact]
    public void Legacy_key_candidates_cover_old_config_env_variants_and_dev_key()
    {
        var c = EncryptionKeyResolver.LegacyCandidates("env-key", "env-key", " cfg-key ");
        Assert.Equal(new[] { " cfg-key ", "cfg-key", EncryptionKeyResolver.DevKey }, c);   // ไม่มีคีย์ปัจจุบัน/ซ้ำ
        Assert.Contains("env-key", EncryptionKeyResolver.LegacyCandidates("env-key ", "env-key ", null));   // S3 ลองแบบตัดช่องว่าง
    }

    [Fact]
    public void Secret_encrypted_with_dev_key_still_decrypts_after_switching_key_and_is_reprotected()
    {
        // B2: production ที่ตั้งแค่ ENCRYPTION_KEY — ค่าเดิมเข้ารหัสด้วย dev key (SecretProtector รุ่นเดิมไม่อ่าน env)
        var legacy = new SecretProtector(EncryptionKeyResolver.DevKey).Protect("sk_live_123");
        var current = new SecretProtector("production-key-219", null,
            EncryptionKeyResolver.LegacyCandidates("production-key-219", "production-key-219", null));
        Assert.True(current.IsUsable(legacy));
        Assert.Equal("sk_live_123", current.Unprotect(legacy));
        var migrated = current.Reprotect(legacy);
        Assert.NotNull(migrated);
        Assert.Null(current.Reprotect(migrated));   // ย้ายแล้ว = ไม่ต้องทำซ้ำ (เดิม NeedsReprotect — ถูกลบเพราะไม่มีผู้เรียกในโค้ดจริง)
        Assert.Equal("sk_live_123", new SecretProtector("production-key-219").Unprotect(migrated));   // ถอดด้วยคีย์ปัจจุบันอย่างเดียวได้แล้ว
        // ค่าที่อยู่บนคีย์ปัจจุบันแล้ว ⇒ ไม่ต้องทำ
        Assert.Null(current.Reprotect(current.Protect("x-value")));
    }

    [Fact]
    public void Secret_with_unknown_key_stays_unusable_and_untouched()
    {
        var foreign = new SecretProtector("some-other-key").Protect("sk_live_999");
        var current = new SecretProtector("production-key-219", null, new[] { EncryptionKeyResolver.DevKey });
        Assert.False(current.IsUsable(foreign));
        Assert.Equal(string.Empty, current.Unprotect(foreign));
        Assert.Null(current.Reprotect(foreign));
    }

    // ═══ ฝ่ายค้านรอบ 219 รอบ 2 ═══

    [Fact]
    public void Approved_payment_date_uses_gateway_paid_time_and_keeps_admin_backdate()
    {
        var selectedAt = Now.AddDays(-5);   // วันที่เลือกแพ็กเกจ (RequestPlanPaymentAsync)
        var paidAt = Now.AddHours(-1);
        // ข้อ 5: ระบบยืนยันจาก gateway ⇒ เวลาเงินเข้าจริง (เดิมคงวันที่เลือกแพ็กเกจ)
        Assert.Equal(paidAt, PlatformPlanPricing.ApprovedPaymentDate(true, paidAt, false, selectedAt, selectedAt, Now));
        Assert.Equal(Now, PlatformPlanPricing.ApprovedPaymentDate(true, null, false, selectedAt, selectedAt, Now));
        Assert.Equal(Now, PlatformPlanPricing.ApprovedPaymentDate(true, Now.AddDays(2), false, selectedAt, selectedAt, Now));   // นาฬิกาเพี้ยน
        // แอดมินบันทึกย้อนหลัง 60 วัน ⇒ คงตามที่กรอก (เดิมถูกบีบเป็น 31 วันเงียบ ๆ)
        Assert.Equal(Now.AddDays(-60), PlatformPlanPricing.ApprovedPaymentDate(false, null, true, Now.AddDays(-60), Now, Now));
        // ทิศตรงข้าม: ลูกค้าแจ้งโอนย้อน 60 วัน ⇒ ยังบีบที่ 31 วัน
        Assert.Equal(Now.AddDays(-31), PlatformPlanPricing.ApprovedPaymentDate(false, null, false, Now.AddDays(-60), Now, Now));
    }

    [Fact]
    public void Admin_payment_date_in_future_is_rejected_but_past_and_today_accepted()
    {
        Assert.NotNull(PlatformPlanPricing.AdminPaymentDateProblem(Now.AddDays(1), Now));
        Assert.Null(PlatformPlanPricing.AdminPaymentDateProblem(Now.AddDays(-90), Now));
        Assert.Null(PlatformPlanPricing.AdminPaymentDateProblem(Now, Now));
        Assert.Null(PlatformPlanPricing.AdminPaymentDateProblem(default, Now));
    }

    [Fact]
    public void Scheduled_change_on_permanent_free_is_effective_now_not_in_100_years()
    {
        Assert.Equal(Now, PlatformPlanPricing.ScheduledChangeEffectiveAt(true, Now.AddYears(100), Now));
        // ทิศตรงข้าม: แพ็กเกจที่จ่ายรอบอยู่ ⇒ มีผลวันครบรอบ
        Assert.Equal(Now.AddDays(20), PlatformPlanPricing.ScheduledChangeEffectiveAt(false, Now.AddDays(20), Now));
        Assert.Equal(Now, PlatformPlanPricing.ScheduledChangeEffectiveAt(false, Now.AddDays(-1), Now));
    }

    [Fact]
    public void Stamped_test_mode_wins_over_config_and_null_falls_back()
    {
        // ข้อ 3: intent ประทับว่าเงินจริง (false) แล้ว config ถูกสลับเป็น Test ⇒ ยังเป็นเงินจริง (เดิม false = "ไม่รู้" ⇒ บล็อก)
        Assert.False(PaymentIntentTestMode.Decide(false, PaymentProviderMode.Test));
        Assert.True(PaymentIntentTestMode.Decide(true, PaymentProviderMode.Live));
        // แถวเดิม (null) ⇒ อ่าน config
        Assert.True(PaymentIntentTestMode.Decide(null, PaymentProviderMode.Test));
        Assert.False(PaymentIntentTestMode.Decide(null, PaymentProviderMode.Live));
        Assert.False(PaymentIntentTestMode.Decide(null, null));
        Assert.Null(PaymentIntentTestMode.StampAtCreate(null));
        Assert.True(PaymentIntentTestMode.StampAtCreate(new PaymentProviderConfig { Mode = PaymentProviderMode.Test }));
        Assert.False(PaymentIntentTestMode.StampAtCreate(new PaymentProviderConfig { Mode = PaymentProviderMode.Live }));
    }

    [Fact]
    public void Derived_key_cache_is_bounded_and_round_trips()
    {
        // ข้อ 2: คีย์ที่ derive แล้วถูกแคช (PBKDF2 100k ต่อคีย์ครั้งเดียว) — ผลต้องเหมือนไม่แคชทุกประการ (เข้ารหัส→ถอดข้ามคีย์ละตัว)
        var a = EncryptionHelper.Encrypt("secret-a", "cache-key-a");
        Assert.Equal("secret-a", EncryptionHelper.Decrypt(a, "cache-key-a"));
        Assert.Equal("secret-a", EncryptionHelper.Decrypt(a, "cache-key-a"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => EncryptionHelper.Decrypt(a, "cache-key-b"));
        for (var i = 0; i < EncryptionHelper.MaxCachedKeys + 5; i++) EncryptionHelper.Encrypt("x", "cache-flood-" + i);
        Assert.True(EncryptionHelper.CachedKeyCount <= EncryptionHelper.MaxCachedKeys);
        Assert.Equal("secret-a", EncryptionHelper.Decrypt(a, "cache-key-a"));   // ถูกล้างแล้วก็ derive ใหม่ได้ค่าเดิม
    }
}
