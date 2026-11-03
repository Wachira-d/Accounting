using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 232 (คำตัดสิน 232-Checkout-2 · Omise ขอ "Shopping Cart + Checkout ที่จบในเว็บเรา") — <b>ตัวตัดสินเดียวของหน้าซื้อแพ็กเกจสาธารณะ</b>
/// (<c>/pricing.html</c> → <c>/checkout.html</c> → <c>/checkout-success.html</c> · <c>Controllers/PublicCheckoutController</c> · <c>Services/Platform/PublicCheckoutService</c>)
/// <list type="bullet">
/// <item><b>สวิตช์แอดมิน</b> <c>SiteSettings.PublicCheckoutEnabled</c> (ค่าเริ่มต้น <b>ปิด</b>) — ปิดอยู่ ⇒ <b>เส้นเปิดหน้าร้าน/สร้าง/เริ่มชำระ</b>
/// (แคตตาล็อก · ยอดตะกร้า · สร้างรายการ · เริ่มชำระ) ตอบ 404 + เหตุผล · <b>เส้นอ่านของรายการที่มีอยู่แล้ว</b> (สถานะ intent · สถานะคำสั่งซื้อ · ใบเสร็จ)
/// <b>ยังเปิดโดยตั้งใจ</b> (ฝ่ายค้าน S2) — ผู้ที่จ่ายไปก่อนแอดมินปิดสวิตช์ต้องดูผล/โหลดใบเสร็จได้ · ทุกเส้นยังต้องเป็นเจ้าของบริษัท</item>
/// <item><b>ชนิดคำสั่งซื้อ</b> <see cref="DecideOrder"/> (ฝ่ายค้าน B1) — ตัดสิน<b>ก่อน</b>เรียก service ที่มี side-effect: ทดลองใช้/หมดอายุ ⇒ ซื้อใหม่ ·
/// Active แพ็กเกจ+รอบเดิม ⇒ ต่ออายุ · อัปเกรด/รอบยาวขึ้น ⇒ เปลี่ยนแพ็กเกจ (จ่ายก่อนมีผล) · ดาวน์เกรด/รอบสั้นลง ⇒ ปฏิเสธก่อนมี side-effect (ทำที่หน้าแพ็กเกจในระบบ)</item>
/// <item><b>โหมดช่องทาง</b>: Live + ผ่านด่านแพลตฟอร์มแล้ว ⇒ เงินจริง · Test ⇒ เส้นเดียวกัน <c>IPlatformPaymentService.StartSubscriptionPaymentAsync(allowTestMode: true)</c>
/// (ฝ่ายค้าน S1 — ด่านเดิมครบ: ยอดตรงราคา · ไม่ซ้อนสลิป · วิธีที่เปิด · ต่างกันเฉพาะยอมให้โหมดทดสอบผ่าน <see cref="PlatformPayAvailability.StartRefusal"/>) ·
/// Test ⇒ <b>ป้าย "โหมดทดสอบ"</b> ทั้งหน้า (Omise ตรวจเว็บก่อนเปิดบัญชี = คีย์ทดสอบ) · เงินทดสอบไม่มีผลทางธุรกิจ (<see cref="TestModePaymentPolicy"/> 232-Omise-2:
/// ไม่เปิดสิทธิ์ ไม่ออกใบเสร็จ รายการค่าแพ็กเกจคงรอชำระ) · ปิด/ยังไม่ตั้ง/ไม่มีวิธี ⇒ ซื้อออนไลน์ไม่ได้ (บอกเหตุ + ทางไปต่อ: สมัครแล้วโอนในระบบ)</item>
/// <item><b>ยอดมาจากเซิร์ฟเวอร์เท่านั้น</b> — รายการค่าแพ็กเกจสร้างผ่าน <c>ISubscriptionService.ConvertTrialAsync</c>/<c>ChangeSubscriptionAsync</c>
/// (ราคาพิเศษ → คูปอง → ยอด: <c>PlatformPriceOverrideStore</c> + <c>PlatformCouponStore</c> ใน <c>RequestPlanPaymentAsync</c>) · หน้าเว็บ/controller ไม่คิดเงินเอง</item>
/// <item><b>ไม่สร้างซ้ำ</b> — รายการรอชำระของแพ็กเกจ/รอบเดียวกันถูกใช้ต่อ (resume) · intent ที่ยังไม่จบของรายการเดิมถูกใช้ต่อโดย <c>PaymentIntentService.StartAsync</c></item>
/// </list>
/// </summary>
public static class PublicCheckoutPolicy
{
    public const string RuleCode = "PLATFORM-PUBLIC-CHECKOUT";
    public const string DisabledMessage = "หน้าซื้อแพ็กเกจออนไลน์ยังไม่เปิดให้บริการ — สมัครใช้งานได้ที่หน้า /register.html แล้วเลือกแพ็กเกจในระบบ";

    /// <summary>โควตาต่อ IP ของเส้นสาธารณะ (แบบ <c>PlatformPayTestController</c> · นับบน PostgreSQL ข้ามเครื่อง)</summary>
    public const int StartPerMinutePerIp = 6;
    public const int StartPerDayPerIp = 60;
    public const int StartPerMinuteGlobal = 60;
    public const int StartPerDayGlobal = 2000;

    /// <summary>ฝ่ายค้าน S3 (คูปองไล่เดาได้): โควตายอดตะกร้าของบริษัทต่อผู้ใช้ + รหัสคูปอง<b>ต่างกัน</b>ที่ลองได้ต่อผู้ใช้ต่อหน้าต่าง 10 นาที
    /// (ถามรหัสเดิมซ้ำ — เช่นสลับรอบบิล — ไม่นับ) · เกิน ⇒ 429 ไทย <see cref="CouponTooManyMessage"/> · นับบน PostgreSQL (<c>IChatRateLimiter</c> · fail-closed)</summary>
    public const int QuotePerMinutePerUser = 20;
    public const int QuotePerDayPerUser = 300;
    public const int CouponCodesPerWindowPerUser = 10;
    public const int CouponWindowMinutes = 10;
    public const string CouponTooManyMessage = "ลองรหัสคูปองหลายรหัสเกินไป — รอ 10 นาทีแล้วลองใหม่";

    /// <summary>เลขหน้าต่างเวลา 10 นาที (fixed window) ของตัวนับคูปอง — pure</summary>
    public static long CouponWindowId(DateTime utcNow) => utcNow.Ticks / TimeSpan.FromMinutes(CouponWindowMinutes).Ticks;

    /// <summary>ผลตัดสินว่าเส้นสาธารณะใช้ได้ไหม — <see cref="Enabled"/> = สวิตช์แอดมิน · <see cref="OnlineAvailable"/> = เริ่มชำระออนไลน์ได้ (จริงหรือทดสอบ) ·
    /// <see cref="IsTestMode"/> = ต้องติดป้าย "โหมดทดสอบ" ทั้งหน้า · <see cref="Reason"/> = เหตุที่ซื้อออนไลน์ไม่ได้ (null = ได้)</summary>
    public sealed record Availability(bool Enabled, bool OnlineAvailable, bool IsTestMode, string? Reason);

    /// <summary>ตัวตัดสินเดียวของทุก endpoint สาธารณะ + หน้าเว็บ (ผ่านผล DTO) — pure</summary>
    /// <param name="enabled">สวิตช์ <c>SiteSettings.PublicCheckoutEnabled</c> (null = ไม่มีแถวค่าตั้ง = ปิด)</param>
    /// <param name="gatewayConfigured">มี config ผู้ให้บริการออนไลน์ของบริษัทผู้ให้บริการ</param>
    /// <param name="gatewayActive">config เปิดอยู่</param>
    /// <param name="mode">โหมดของ config (null = ไม่มี config)</param>
    /// <param name="methodCount">วิธีชำระที่หน้าชำระทำจบได้</param>
    /// <param name="platformLiveConfirmed">ผ่านด่านเปิดรับเงินจริงของแพลตฟอร์มแล้ว (<c>PlatformLiveConfirmedAt</c>)</param>
    public static Availability Decide(bool? enabled, bool gatewayConfigured, bool gatewayActive, PaymentProviderMode? mode, int methodCount,
        bool platformLiveConfirmed)
    {
        if (enabled != true) return new Availability(false, false, false, DisabledMessage);
        if (!gatewayConfigured || mode == null)
            return new Availability(true, false, false, "ยังไม่ได้ตั้งค่าช่องทางรับชำระออนไลน์ของ NextAcc — สมัครใช้งานแล้วโอนเงิน+แนบสลิปในระบบได้");
        if (!gatewayActive) return new Availability(true, false, false, "ช่องทางรับชำระออนไลน์ของ NextAcc ปิดอยู่ชั่วคราว — สมัครใช้งานแล้วโอนเงิน+แนบสลิปในระบบได้");
        if (methodCount == 0) return new Availability(true, false, false, "ช่องทางรับชำระออนไลน์ของ NextAcc ยังไม่ได้เปิดวิธีชำระใด — สมัครใช้งานแล้วโอนเงิน+แนบสลิปในระบบได้");
        if (mode == PaymentProviderMode.Test) return new Availability(true, true, true, null);
        // Live แต่ยังไม่ผ่านด่านแพลตฟอร์ม = ห้ามรับเงินจริง (PlatformPayAvailability ตัวเดียวกัน) — ไม่ตกไปโหมดทดสอบเงียบ ๆ
        if (!platformLiveConfirmed)
            return new Availability(true, false, false, PlatformPayAvailability.OfflineReason(gatewayActive, mode.Value, methodCount, false)
                ?? "ช่องทางรับชำระออนไลน์ของ NextAcc ยังไม่เปิดรับเงินจริง");
        return new Availability(true, true, false, null);
    }

    /// <summary>แพ็กเกจที่ขายบนหน้าสาธารณะ — เปิดใช้ · มีราคา (ฟรีถาวร/ราคา 0 ไม่ใช่ของที่ "ซื้อ" · ไปทางสมัครธรรมดา)</summary>
    public static bool IsSellable(bool isActive, bool isPermanentFree, decimal monthlyPrice)
        => isActive && !isPermanentFree && monthlyPrice > 0m;

    /// <summary>รายการค่าแพ็กเกจที่ค้างอยู่ใช้ต่อได้ไหม (resume แทนสร้างซ้ำ) — รอชำระ · แพ็กเกจ/รอบตรง · ไม่ผูกใบแจ้งหนี้ · ยังไม่แนบสลิป/ไม่ถูกพัก</summary>
    public static bool CanResume(SubscriptionPaymentStatus status, SubscriptionPlan plan, BillingCycle cycle,
        SubscriptionPlan wantedPlan, BillingCycle wantedCycle, bool linkedToInvoice, bool hasSlip, bool held)
        => status == SubscriptionPaymentStatus.Pending && plan == wantedPlan && cycle == wantedCycle && !linkedToInvoice && !hasSlip && !held;

    /// <summary>สถานะที่หน้าสำเร็จแสดง — ตัวเดียว (หน้าเว็บไม่มีตารางสถานะเอง)</summary>
    public enum Outcome { Activated, PaidAwaitingApproval, TestPaid, AwaitingPayment, Failed }

    /// <summary>ตัดสินจาก (สถานะรายการค่าแพ็กเกจ · สถานะ intent · โหมดทดสอบ) — เงินทดสอบสำเร็จ = <see cref="Outcome.TestPaid"/> (ไม่ใช่ "เปิดใช้แล้ว") ·
    /// เงินจริงเข้าแต่รายการยังไม่อนุมัติ (ยอดไม่ตรง/พักให้แอดมิน) = <see cref="Outcome.PaidAwaitingApproval"/> (ไม่โกหกว่าเปิดแล้ว · F2 ข้อ 3)</summary>
    public static Outcome Resolve(SubscriptionPaymentStatus paymentStatus, PaymentIntentStatus? intentStatus, bool isTestMode)
    {
        if (paymentStatus == SubscriptionPaymentStatus.Approved) return Outcome.Activated;
        if (intentStatus == PaymentIntentStatus.Succeeded) return isTestMode ? Outcome.TestPaid : Outcome.PaidAwaitingApproval;
        if (intentStatus is PaymentIntentStatus.Failed or PaymentIntentStatus.Expired) return Outcome.Failed;
        if (paymentStatus is SubscriptionPaymentStatus.Rejected or SubscriptionPaymentStatus.Cancelled) return Outcome.Failed;
        return Outcome.AwaitingPayment;
    }

    /// <summary>ข้อความไทยของผลลัพธ์ (หน้าสำเร็จแสดงตรง ๆ)</summary>
    public static string OutcomeMessage(Outcome o) => o switch
    {
        Outcome.Activated => "ชำระเงินสำเร็จ — เปิดใช้แพ็กเกจแล้ว เข้าสู่ระบบได้ทันที",
        Outcome.PaidAwaitingApproval => "ได้รับเงินแล้ว — ระบบกำลังตรวจยอดก่อนเปิดใช้ (ปกติภายในไม่กี่นาที · ถ้าเกิน 1 ชั่วโมงผู้ดูแลระบบจะติดต่อกลับ)",
        Outcome.TestPaid => "ชำระสำเร็จในโหมดทดสอบ — ไม่มีเงินจริง ไม่เปิดสิทธิ์ ไม่ออกใบเสร็จ (รายการค่าแพ็กเกจยังรอชำระจริง)",
        Outcome.AwaitingPayment => "ยังไม่ได้รับเงิน — กลับไปชำระต่อได้จากปุ่มด้านล่าง",
        Outcome.Failed => "การชำระไม่สำเร็จหรือรายการถูกยกเลิก — เริ่มใหม่ได้จากหน้าเลือกแพ็กเกจ",
        _ => "ไม่ทราบสถานะ",
    };

    // ═══════════ ฝ่ายค้าน B1: ชนิดคำสั่งซื้อ — ตัดสินก่อนมี side-effect ═══════════

    /// <summary>ชนิดคำสั่งซื้อจากหน้าสาธารณะ — <see cref="NewPurchase"/> = เส้น <c>ConvertTrialAsync</c> (ทดลองใช้/หมดอายุ/ฟรีถาวร) ·
    /// <see cref="Renewal"/> = ต่ออายุแพ็กเกจ+รอบเดิม (ราคาต่ออายุ <c>PlatformPlanPricing.IsRenewalPurchase</c>) · <see cref="Change"/> = อัปเกรด/รอบยาวขึ้น (จ่ายก่อนมีผล) ·
    /// <see cref="Refused"/> = ไม่สร้างอะไร (เหตุใน <see cref="OrderDecision.Refusal"/>)</summary>
    public enum OrderKind { NewPurchase, Renewal, Change, Refused }

    public sealed record OrderDecision(OrderKind Kind, string? Refusal);

    public const string DowngradeRefusal = "ลดแพ็กเกจได้ที่หน้าแพ็กเกจในระบบ — มีผลสิ้นรอบ (หน้าซื้อแพ็กเกจออนไลน์รับเฉพาะซื้อใหม่ · ต่ออายุ · อัปเกรด)";
    public const string ShorterCycleRefusal = "ลดรอบบิลได้ที่หน้าแพ็กเกจในระบบ — มีผลสิ้นรอบ (หน้าซื้อแพ็กเกจออนไลน์รับเฉพาะซื้อใหม่ · ต่ออายุ · อัปเกรด)";
    public const string NoPaymentChangeRefusal = "การเปลี่ยนนี้ไม่มียอดต้องชำระตอนนี้ (มีผลสิ้นรอบ) — ทำได้ที่หน้าแพ็กเกจในระบบ";
    public const string RenewalInvoiceIssuedRefusal = "ออกใบแจ้งหนี้ต่ออายุรอบนี้แล้ว — ชำระจากใบแจ้งหนี้ที่ศูนย์บิลในระบบ (ไม่สร้างรายการซ้ำ)";
    public const string RenewalCouponRefusal = "คูปองใช้กับการต่ออายุผ่านหน้านี้ไม่ได้ — ต่ออายุโดยไม่ใส่คูปอง หรือติดต่อผู้ดูแลระบบ";

    /// <summary>ตัวตัดสินเดียวของชนิดคำสั่งซื้อ — pure · เรียก<b>ก่อน</b> <c>ConvertTrialAsync</c>/<c>SubmitPaymentAsync</c> (เดิมเรียกตรง ⇒ Active+เดิม = Payment null แล้ว throw
    /// ต่ออายุไม่ได้ · ดาวน์เกรด = <c>ChangeSubscriptionAsync</c> บันทึก <c>ScheduledPlan</c> ก่อนแล้วจึง throw = side-effect เงียบ)</summary>
    /// <param name="changeQuoteAmount">ยอดจาก <c>QuotePlanChangeAsync</c> (อ่านอย่างเดียว · ตัวตัดสินเดียวกับตอนยืนยัน) — ≤ 0 บนเส้นเปลี่ยน = จะถูกตั้งเวลา ไม่ใช่จ่าย ⇒ ปฏิเสธ</param>
    /// <param name="renewalInvoiceIssued">ใบแจ้งหนี้ต่ออายุรอบนี้ออกแล้ว (<c>RenewalInvoiceForEndDate == EndDate</c>) ⇒ ต่ออายุซ้ำไม่ได้</param>
    public static OrderDecision DecideOrder(SubscriptionStatus status, bool isPermanentFree,
        SubscriptionPlan currentPlan, BillingCycle currentCycle, decimal currentMonthlyPrice,
        SubscriptionPlan targetPlan, BillingCycle targetCycle, decimal targetMonthlyPrice,
        decimal changeQuoteAmount, bool renewalInvoiceIssued, bool hasCoupon)
    {
        // ทดลองใช้/หมดอายุ/ค้างชำระ/ฟรีถาวร ⇒ ซื้อเต็มรอบ (ConvertTrialAsync ไม่เข้าเส้น ChangeSubscriptionAsync — เงื่อนไขเดียวกับในนั้น)
        if (status != SubscriptionStatus.Active || isPermanentFree) return new OrderDecision(OrderKind.NewPurchase, null);
        var kind = PlatformPlanPricing.Classify(currentMonthlyPrice, targetMonthlyPrice);
        if (kind == PlatformPlanPricing.ChangeKind.Downgrade) return new OrderDecision(OrderKind.Refused, DowngradeRefusal);
        var samePlan = PlatformPlanPricing.IsRenewalPurchase(currentPlan, targetPlan);
        var months = PlatformPlanPricing.MonthsOf(targetCycle);
        var currentMonths = PlatformPlanPricing.MonthsOf(currentCycle);
        if (samePlan && targetCycle == currentCycle)
        {
            if (renewalInvoiceIssued) return new OrderDecision(OrderKind.Refused, RenewalInvoiceIssuedRefusal);
            if (hasCoupon) return new OrderDecision(OrderKind.Refused, RenewalCouponRefusal);
            return new OrderDecision(OrderKind.Renewal, null);
        }
        if (kind != PlatformPlanPricing.ChangeKind.Upgrade && months < currentMonths) return new OrderDecision(OrderKind.Refused, ShorterCycleRefusal);
        // อัปเกรด / รอบยาวขึ้น — ต้องมียอดให้จ่ายจริง (ไม่งั้น ChangeSubscriptionAsync จะตั้งเวลาเปลี่ยน = side-effect ที่หน้านี้ไม่ได้ขอ)
        if (changeQuoteAmount <= 0m) return new OrderDecision(OrderKind.Refused, NoPaymentChangeRefusal);
        return new OrderDecision(OrderKind.Change, null);
    }

    /// <summary>ป้ายรอบบิลไทย (หน้าเว็บไม่มีตารางป้ายเอง) — คู่กับ <see cref="PlatformPlanPricing.CycleLabel"/></summary>
    public static string CycleLabel(BillingCycle cycle) => $"ราย{PlatformPlanPricing.CycleLabel(cycle)} ({PlatformPlanPricing.MonthsOf(cycle)} เดือน)";
}
