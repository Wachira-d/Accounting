using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 232 (คำตัดสิน 232-Checkout-2) — หน้าซื้อแพ็กเกจสาธารณะ: ตัวตัดสิน <see cref="PublicCheckoutPolicy"/>
/// ① สวิตช์แอดมิน (ค่าเริ่มต้นปิด · ปิด = ทุกอย่างปิด) ② โหมดช่องทาง: Test = ซื้อได้ + ป้ายทดสอบ · Live ต้องผ่านด่านแพลตฟอร์ม · ปิด/ไม่มีวิธี = เหตุ+ทางไปต่อ
/// ③ แพ็กเกจที่ขาย (ฟรีถาวร/ราคา 0 ไม่ขาย) ④ resume รายการรอชำระ (ไม่สร้างซ้ำ) ⑤ ผลลัพธ์หน้าสำเร็จ (เงินทดสอบ ≠ เปิดสิทธิ์ · เงินจริงรอตรวจ ≠ เปิดสิทธิ์)
/// </summary>
public class PublicCheckoutRound232Tests
{
    // ═══════════ ① สวิตช์ ═══════════
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void ปิดสวิตช์_หรือไม่มีแถวค่าตั้ง_ทุกอย่างปิดพร้อมเหตุ(bool? enabled)
    {
        var a = PublicCheckoutPolicy.Decide(enabled, gatewayConfigured: true, gatewayActive: true, PaymentProviderMode.Live, 2, platformLiveConfirmed: true);
        Assert.False(a.Enabled);
        Assert.False(a.OnlineAvailable);
        Assert.False(a.IsTestMode);
        Assert.Equal(PublicCheckoutPolicy.DisabledMessage, a.Reason);
    }

    // ═══════════ ② โหมดช่องทาง ═══════════
    [Fact]
    public void โหมดทดสอบ_ซื้อได้และติดป้ายทดสอบ()
    {
        var a = PublicCheckoutPolicy.Decide(true, true, true, PaymentProviderMode.Test, 2, platformLiveConfirmed: false);
        Assert.True(a.Enabled);
        Assert.True(a.OnlineAvailable);
        Assert.True(a.IsTestMode);
        Assert.Null(a.Reason);
    }

    [Fact]
    public void โหมดจริง_ผ่านด่านแพลตฟอร์มแล้ว_รับเงินจริง_ไม่มีป้ายทดสอบ()
    {
        var a = PublicCheckoutPolicy.Decide(true, true, true, PaymentProviderMode.Live, 1, platformLiveConfirmed: true);
        Assert.True(a.OnlineAvailable);
        Assert.False(a.IsTestMode);
        Assert.Null(a.Reason);
    }

    [Fact]
    public void โหมดจริง_ยังไม่ผ่านด่านแพลตฟอร์ม_ห้ามรับเงินและห้ามตกไปโหมดทดสอบเงียบ()
    {
        var a = PublicCheckoutPolicy.Decide(true, true, true, PaymentProviderMode.Live, 1, platformLiveConfirmed: false);
        Assert.True(a.Enabled);
        Assert.False(a.OnlineAvailable);
        Assert.False(a.IsTestMode);
        Assert.Equal(PlatformPayAvailability.OfflineReason(true, PaymentProviderMode.Live, 1, false), a.Reason);
    }

    [Theory]
    [InlineData(false, true, 2)]    // ไม่มี config
    [InlineData(true, false, 2)]    // config ปิด
    [InlineData(true, true, 0)]     // ไม่มีวิธีชำระ
    public void ช่องทางไม่พร้อม_หน้าเปิดแต่ซื้อออนไลน์ไม่ได้_บอกทางไปต่อ(bool configured, bool active, int methods)
    {
        var a = PublicCheckoutPolicy.Decide(true, configured, active, configured ? PaymentProviderMode.Test : null, methods, false);
        Assert.True(a.Enabled);
        Assert.False(a.OnlineAvailable);
        Assert.False(a.IsTestMode);
        Assert.Contains("แนบสลิป", a.Reason);
    }

    // ═══════════ ③ แพ็กเกจที่ขาย ═══════════
    [Theory]
    [InlineData(true, false, 990, true)]
    [InlineData(true, true, 990, false)]    // ฟรีถาวร — ไม่ใช่ของที่ซื้อ
    [InlineData(true, false, 0, false)]     // ราคา 0
    [InlineData(false, false, 990, false)]  // ปิดใช้
    public void แพ็กเกจที่ขายบนหน้าสาธารณะ(bool active, bool permanentFree, decimal monthly, bool expected)
        => Assert.Equal(expected, PublicCheckoutPolicy.IsSellable(active, permanentFree, monthly));

    // ═══════════ ④ resume ═══════════
    [Fact]
    public void รายการรอชำระเดิม_แพ็กเกจรอบตรง_ใช้ต่อ()
        => Assert.True(PublicCheckoutPolicy.CanResume(SubscriptionPaymentStatus.Pending, SubscriptionPlan.Pro, BillingCycle.Annual,
            SubscriptionPlan.Pro, BillingCycle.Annual, linkedToInvoice: false, hasSlip: false, held: false));

    [Theory]
    [InlineData(SubscriptionPaymentStatus.UnderReview, SubscriptionPlan.Pro, BillingCycle.Annual, false, false, false)]   // รอตรวจ (สลิป)
    [InlineData(SubscriptionPaymentStatus.Pending, SubscriptionPlan.Basic, BillingCycle.Annual, false, false, false)]     // คนละแพ็กเกจ
    [InlineData(SubscriptionPaymentStatus.Pending, SubscriptionPlan.Pro, BillingCycle.Monthly, false, false, false)]      // คนละรอบ
    [InlineData(SubscriptionPaymentStatus.Pending, SubscriptionPlan.Pro, BillingCycle.Annual, true, false, false)]        // ผูกใบแจ้งหนี้
    [InlineData(SubscriptionPaymentStatus.Pending, SubscriptionPlan.Pro, BillingCycle.Annual, false, true, false)]        // แนบสลิปแล้ว
    [InlineData(SubscriptionPaymentStatus.Pending, SubscriptionPlan.Pro, BillingCycle.Annual, false, false, true)]        // พักให้แอดมิน
    public void ทิศตรงข้าม_รายการที่ใช้ต่อไม่ได้(SubscriptionPaymentStatus status, SubscriptionPlan plan, BillingCycle cycle, bool invoice, bool slip, bool held)
        => Assert.False(PublicCheckoutPolicy.CanResume(status, plan, cycle, SubscriptionPlan.Pro, BillingCycle.Annual, invoice, slip, held));

    // ═══════════ ⑤ ผลลัพธ์หน้าสำเร็จ ═══════════
    [Fact]
    public void อนุมัติแล้ว_เปิดสิทธิ์()
        => Assert.Equal(PublicCheckoutPolicy.Outcome.Activated, PublicCheckoutPolicy.Resolve(SubscriptionPaymentStatus.Approved, PaymentIntentStatus.Succeeded, false));

    [Fact]
    public void เงินทดสอบสำเร็จ_ไม่ใช่เปิดสิทธิ์()
    {
        var o = PublicCheckoutPolicy.Resolve(SubscriptionPaymentStatus.Pending, PaymentIntentStatus.Succeeded, isTestMode: true);
        Assert.Equal(PublicCheckoutPolicy.Outcome.TestPaid, o);
        Assert.Contains("ไม่เปิดสิทธิ์", PublicCheckoutPolicy.OutcomeMessage(o));
    }

    [Fact]
    public void เงินจริงเข้าแต่รายการยังไม่อนุมัติ_รอตรวจ_ไม่โกหกว่าเปิดแล้ว()
    {
        var o = PublicCheckoutPolicy.Resolve(SubscriptionPaymentStatus.UnderReview, PaymentIntentStatus.Succeeded, isTestMode: false);
        Assert.Equal(PublicCheckoutPolicy.Outcome.PaidAwaitingApproval, o);
    }

    [Theory]
    [InlineData(SubscriptionPaymentStatus.Pending, PaymentIntentStatus.Failed)]
    [InlineData(SubscriptionPaymentStatus.Pending, PaymentIntentStatus.Expired)]
    [InlineData(SubscriptionPaymentStatus.Cancelled, null)]
    [InlineData(SubscriptionPaymentStatus.Rejected, PaymentIntentStatus.Pending)]
    public void ล้มเหลว_หรือรายการถูกปิด(SubscriptionPaymentStatus pay, PaymentIntentStatus? intent)
        => Assert.Equal(PublicCheckoutPolicy.Outcome.Failed, PublicCheckoutPolicy.Resolve(pay, intent, false));

    [Theory]
    [InlineData(null)]
    [InlineData(PaymentIntentStatus.Created)]
    [InlineData(PaymentIntentStatus.Pending)]
    public void ยังไม่ได้รับเงิน(PaymentIntentStatus? intent)
        => Assert.Equal(PublicCheckoutPolicy.Outcome.AwaitingPayment, PublicCheckoutPolicy.Resolve(SubscriptionPaymentStatus.Pending, intent, false));

    [Fact]
    public void ป้ายรอบบิลมาจากตัวตั้งเดียว()
    {
        Assert.Equal("รายปี (12 เดือน)", PublicCheckoutPolicy.CycleLabel(BillingCycle.Annual));
        Assert.Contains(PlatformPlanPricing.CycleLabel(BillingCycle.Quarterly), PublicCheckoutPolicy.CycleLabel(BillingCycle.Quarterly));
    }

    // ═══════════ ⑥ ฝ่ายค้าน B1: ชนิดคำสั่งซื้อ — ตัดสินก่อนมี side-effect ═══════════
    private static PublicCheckoutPolicy.OrderDecision Order(SubscriptionStatus status, SubscriptionPlan cur, BillingCycle curCycle, decimal curPrice,
        SubscriptionPlan target, BillingCycle targetCycle, decimal targetPrice, decimal quote = 990m, bool permanentFree = false,
        bool renewalInvoiceIssued = false)
        => PublicCheckoutPolicy.DecideOrder(status, permanentFree, cur, curCycle, curPrice, target, targetCycle, targetPrice, quote, renewalInvoiceIssued);

    [Theory]
    [InlineData(SubscriptionStatus.Trial)]
    [InlineData(SubscriptionStatus.Expired)]
    [InlineData(SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Cancelled)]
    public void ทดลองใช้หรือหมดอายุ_ซื้อใหม่ผ่าน_ConvertTrial_เดิม(SubscriptionStatus status)
        => Assert.Equal(PublicCheckoutPolicy.OrderKind.NewPurchase,
            Order(status, SubscriptionPlan.Pro, BillingCycle.Annual, 1990m, SubscriptionPlan.Basic, BillingCycle.Monthly, 990m, quote: 0m).Kind);

    [Fact]
    public void ฟรีถาวร_ซื้อใหม่_แม้สถานะ_Active()
        => Assert.Equal(PublicCheckoutPolicy.OrderKind.NewPurchase,
            Order(SubscriptionStatus.Active, SubscriptionPlan.Basic, BillingCycle.Monthly, 990m, SubscriptionPlan.Basic, BillingCycle.Monthly, 990m, permanentFree: true).Kind);

    [Fact]
    public void บั๊กเดิม_Active_แพ็กเกจและรอบเดิม_คือการต่ออายุ_ไม่ใช่ล้ม()
    {
        // เดิม ConvertTrialAsync → ChangeSubscriptionAsync คืน Payment=null ("เป็นแพ็กเกจปัจจุบันอยู่แล้ว") ⇒ throw — ลูกค้าจ่ายแพ็กเกจเดิมรอบถัดไปไม่ได้
        var d = Order(SubscriptionStatus.Active, SubscriptionPlan.Pro, BillingCycle.Annual, 1990m, SubscriptionPlan.Pro, BillingCycle.Annual, 1990m, quote: 0m);
        Assert.Equal(PublicCheckoutPolicy.OrderKind.Renewal, d.Kind);
        Assert.Null(d.Refusal);
    }

    [Fact]
    public void ต่ออายุ_แต่ใบแจ้งหนี้ต่ออายุออกแล้ว_ปฏิเสธพร้อมทางไปต่อ()
    {
        var inv = Order(SubscriptionStatus.Active, SubscriptionPlan.Pro, BillingCycle.Annual, 1990m, SubscriptionPlan.Pro, BillingCycle.Annual, 1990m, renewalInvoiceIssued: true);
        Assert.Equal(PublicCheckoutPolicy.OrderKind.Refused, inv.Kind);
        Assert.Equal(PublicCheckoutPolicy.RenewalInvoiceIssuedRefusal, inv.Refusal);
        // รอบ 235 (K-2 · คำตัดสิน 235-1 แทน 233-5): คูปองไม่ใช่เหตุปฏิเสธที่นี่แล้ว — ตัดสินที่ PlatformCouponRule (PurchaseKinds) ตอนดูราคา/จอง
        // (เทสต์: PlatformRound235Tests.K2_* · Db: PlatformRound235DbTests.K2_*)
    }

    [Fact]
    public void บั๊กเดิม_ดาวน์เกรด_ปฏิเสธก่อนมี_side_effect_พร้อมข้อความไทย()
    {
        // เดิม ChangeSubscriptionAsync บันทึก ScheduledPlan แล้วจึง throw ⇒ ลูกค้าเห็น "ไม่สำเร็จ" แต่ระบบตั้งดาวน์เกรดไว้เงียบ ๆ
        var d = Order(SubscriptionStatus.Active, SubscriptionPlan.Pro, BillingCycle.Annual, 1990m, SubscriptionPlan.Basic, BillingCycle.Annual, 990m);
        Assert.Equal(PublicCheckoutPolicy.OrderKind.Refused, d.Kind);
        Assert.Contains("ลดแพ็กเกจได้ที่หน้าแพ็กเกจในระบบ — มีผลสิ้นรอบ", d.Refusal);
    }

    [Fact]
    public void แพ็กเกจเดิม_รอบสั้นลง_ปฏิเสธ()
        => Assert.Equal(PublicCheckoutPolicy.ShorterCycleRefusal,
            Order(SubscriptionStatus.Active, SubscriptionPlan.Pro, BillingCycle.Annual, 1990m, SubscriptionPlan.Pro, BillingCycle.Monthly, 1990m).Refusal);

    [Theory]
    [InlineData(SubscriptionPlan.Enterprise, BillingCycle.Monthly, 4990)]    // อัปเกรด (รอบสั้นลงก็ได้ — ส่วนต่างคิดบนรอบปัจจุบัน)
    [InlineData(SubscriptionPlan.Enterprise, BillingCycle.Annual, 4990)]     // อัปเกรด
    [InlineData(SubscriptionPlan.Pro, BillingCycle.Annual, 1990)]            // แพ็กเกจเดิม รอบยาวขึ้น (ปัจจุบันรายเดือน)
    public void ทิศตรงข้าม_อัปเกรดหรือรอบยาวขึ้น_ที่มียอด_ผ่าน(SubscriptionPlan target, BillingCycle cycle, int price)
    {
        var curCycle = target == SubscriptionPlan.Pro ? BillingCycle.Monthly : BillingCycle.Annual;
        var d = Order(SubscriptionStatus.Active, SubscriptionPlan.Pro, curCycle, 1990m, target, cycle, price, quote: 500m);
        Assert.Equal(PublicCheckoutPolicy.OrderKind.Change, d.Kind);
        Assert.Null(d.Refusal);
    }

    [Fact]
    public void เปลี่ยนที่ไม่มียอดต้องจ่าย_จะกลายเป็นตั้งเวลา_ปฏิเสธ()
        => Assert.Equal(PublicCheckoutPolicy.NoPaymentChangeRefusal,
            Order(SubscriptionStatus.Active, SubscriptionPlan.Pro, BillingCycle.Monthly, 1990m, SubscriptionPlan.Enterprise, BillingCycle.Monthly, 1990m, quote: 0m).Refusal);

    // ═══════════ ⑦ ฝ่ายค้าน S1: ด่านเริ่มชำระเส้นเดียว — โหมดทดสอบผ่านได้เฉพาะเมื่อผู้เรียกขอ ═══════════
    [Fact]
    public void ผู้เรียกเดิม_ไม่ขอโหมดทดสอบ_โหมดทดสอบยังถูกปฏิเสธเหมือนเดิมทุกตัวอักษร()
    {
        var r = PlatformPayAvailability.StartRefusal(true, PaymentProviderMode.Test, 2, false, allowTestMode: false);
        Assert.NotNull(r);
        Assert.Equal(PlatformPayAvailability.OfflineReason(true, PaymentProviderMode.Test, 2, false), r);
    }

    [Fact]
    public void หน้าซื้อสาธารณะ_ขอโหมดทดสอบ_ผ่านได้เมื่อเปิดและมีวิธี()
        => Assert.Null(PlatformPayAvailability.StartRefusal(true, PaymentProviderMode.Test, 2, false, allowTestMode: true));

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public void โหมดทดสอบที่ปิดหรือไม่มีวิธี_ยังปฏิเสธแม้ขอ(bool active, int methods)
        => Assert.NotNull(PlatformPayAvailability.StartRefusal(active, PaymentProviderMode.Test, methods, false, allowTestMode: true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void โหมดจริง_ยังต้องผ่านด่านแพลตฟอร์ม_ไม่มีทางลัด(bool allowTest)
    {
        Assert.Equal(PlatformPayAvailability.OfflineReason(true, PaymentProviderMode.Live, 2, false),
            PlatformPayAvailability.StartRefusal(true, PaymentProviderMode.Live, 2, false, allowTest));
        Assert.Null(PlatformPayAvailability.StartRefusal(true, PaymentProviderMode.Live, 2, true, allowTest));
    }

    // ═══════════ ⑧ ฝ่ายค้าน S3: หน้าต่างนับคูปอง 10 นาที ═══════════
    [Fact]
    public void หน้าต่างคูปอง_10_นาที_คงที่()
    {
        var t0 = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(PublicCheckoutPolicy.CouponWindowId(t0), PublicCheckoutPolicy.CouponWindowId(t0.AddMinutes(9).AddSeconds(59)));
        Assert.NotEqual(PublicCheckoutPolicy.CouponWindowId(t0), PublicCheckoutPolicy.CouponWindowId(t0.AddMinutes(10)));
    }
}
