using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 226 (TEST_PLAN BKPAY-60…69 · <c>erp-review/2026-11-01/BOOKING_PAYMENT_AUDIT.md</c> §6–§7) — ตัวตัดสินล้วน:
/// <list type="bullet">
///   <item>บั๊ก BK-2611-0004: เว็บ "มัดจำ (บังคับ)" + บริการชนิดที่เก็บเงินไม่ได้ ⇒ เดิมยืนยันอัตโนมัติ · ตอนนี้รอร้านยืนยัน (ไม่ใช่ "จองสำเร็จ") + ทิศตรงข้าม</item>
///   <item>กำหนดชำระตั้งต้น 1440 นาทีเมื่อโหมดบังคับไม่กรอก · บริการกรอก 0 = ไม่กำหนดโดยตั้งใจ</item>
///   <item>จัดส่ง/รับเอง: ลำดับเว็บ/บริการ · วิธีที่ใช้จริง · ที่อยู่ · ยอดขั้นต่ำ · มัดจำไม่รวมค่าส่ง · ชำระเต็มรวมค่าส่ง · แบ่งใบรับมัดจำ</item>
///   <item>ตัวคิดค่าส่งของร้านตัวเดียว (โซน/เหมาจ่าย/ส่งฟรี/เลือกอัตรา) — คำสั่งซื้อและการจองได้ผลเดียวกัน</item>
/// </list>
/// </summary>
public class BookingRound226Tests
{
    private static BookingPaymentRuleInput Rule(BookingPaymentMode? m = null, decimal? fx = null, decimal? pct = null, int? deadline = null)
        => new(m, fx, pct, null, null, deadline);

    // ═════════ บั๊ก: โหมดบังคับ × ชนิดบริการที่เก็บเงินไม่ได้ ═════════

    [Theory]
    [InlineData(BookingType.Appointment)]
    [InlineData(BookingType.Lead)]
    [InlineData(BookingType.Guaranteed)]
    public void มัดจำบังคับของเว็บ_บริการชนิดเก็บเงินไม่ได้_รอร้านยืนยัน_ไม่ยืนยันอัตโนมัติ(BookingType type)
    {
        // ค่าตั้งของเจ้าของ: มัดจำ (บังคับ) 50% · ต้องชำระภายในว่าง · บริการ "Sourdough สังขยา" ฿35 ชนิดตั้งต้นของฟอร์ม (นัดหมาย)
        var e = BookingPaymentPolicy.Resolve(type, Rule(BookingPaymentMode.DepositRequired, pct: 50), Rule());
        Assert.Equal(BookingPaymentMode.Off, e.Mode);
        Assert.True(e.RequiredButUncollectable);
        Assert.Equal(0m, BookingPaymentPolicy.AmountDue(e, 35m, true));
        var seat = BookingPaymentPolicy.OnSeat(e, 0m, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), createdByStaff: false);
        Assert.True(seat.ForcePending);          // เดิม false ⇒ AutoConfirm ⇒ "ยืนยันแล้ว" (BK-2611-0004)
        Assert.Null(seat.PaymentHoldUntil);      // ลูกค้าไม่มีทางจ่ายในระบบ ⇒ ไม่ถือรอบให้ job ยกเลิก
        Assert.StartsWith("⚠️", BookingPaymentPolicy.Describe(e));
        Assert.Contains("รอร้านยืนยัน", BookingPaymentPolicy.Describe(e));
    }

    [Fact]
    public void ทิศตรงข้าม_พนักงานจองแทน_ไม่บังคับ_และโหมดไม่บังคับหรือยกเว้นที่บริการ_ไม่ถูกแตะ()
    {
        var req = BookingPaymentPolicy.Resolve(BookingType.Appointment, Rule(BookingPaymentMode.DepositRequired, pct: 50), Rule());
        Assert.False(BookingPaymentPolicy.OnSeat(req, 0m, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), createdByStaff: true).ForcePending);

        var optional = BookingPaymentPolicy.Resolve(BookingType.Appointment, Rule(BookingPaymentMode.DepositOptional, pct: 50), Rule());
        Assert.False(optional.RequiredButUncollectable);
        Assert.False(BookingPaymentPolicy.OnSeat(optional, 0m, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), false).ForcePending);

        var exempt = BookingPaymentPolicy.Resolve(BookingType.Appointment, Rule(BookingPaymentMode.DepositRequired, pct: 50), Rule(BookingPaymentMode.Off));
        Assert.False(exempt.RequiredButUncollectable);
        Assert.False(BookingPaymentPolicy.OnSeat(exempt, 0m, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), false).ForcePending);

        var legacy = BookingPaymentPolicy.Resolve(BookingType.Lead, Rule(), Rule());
        Assert.False(legacy.RequiredButUncollectable);
        Assert.False(BookingPaymentPolicy.OnSeat(legacy, 0m, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), false).ForcePending);

        var prepay = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 50), Rule());
        Assert.False(prepay.RequiredButUncollectable);
        Assert.Equal(17.50m, BookingPaymentPolicy.AmountDue(prepay, 35m, false));
    }

    [Fact]
    public void ขั้นของลูกค้า_ร้านต้องจัดการชำระเอง_ไม่ขึ้นจองสำเร็จ_ทิศตรงข้ามปกติยังจองสำเร็จ()
    {
        var s = BookingPaymentStep.Decide(BookingStatus.Pending, BookingPaymentMode.Off, 0m, false, null, shopArrangesPayment: true);
        Assert.Equal("ได้รับการจองแล้ว — รอร้านยืนยัน", s.Headline);
        Assert.Equal("รอร้านยืนยัน", s.CustomerStatusLabel);
        Assert.Equal(BookingPaymentPolicy.ShopArrangesPaymentDetail, s.Detail);
        Assert.DoesNotContain("จองสำเร็จ", s.Headline);
        Assert.Equal("จองสำเร็จ", BookingPaymentStep.Decide(BookingStatus.Confirmed, BookingPaymentMode.Off, 0m, false, null).Headline);
        // ร้านยืนยันเองภายหลัง (Confirmed) ⇒ ไม่ค้างข้อความ "รอ"
        Assert.Equal("จองสำเร็จ", BookingPaymentStep.Decide(BookingStatus.Confirmed, BookingPaymentMode.Off, 0m, false, null, shopArrangesPayment: true).Headline);
    }

    [Fact]
    public void หน้าตั้งค่า_กล่องแดงเฉพาะโหมดบังคับที่มีบริการเก็บเงินไม่ได้()
    {
        var p = BookingPaymentPolicy.SiteRequiredUncollectableProblem(BookingPaymentMode.DepositRequired, new[] { "Sourdough สังขยา" });
        Assert.Contains("Sourdough สังขยา", p);
        Assert.Contains("§78/1", p);
        Assert.Null(BookingPaymentPolicy.SiteRequiredUncollectableProblem(BookingPaymentMode.DepositOptional, new[] { "x" }));
        Assert.Null(BookingPaymentPolicy.SiteRequiredUncollectableProblem(BookingPaymentMode.FullRequired, Array.Empty<string>()));
        Assert.Null(BookingPaymentPolicy.SiteRequiredUncollectableProblem(null, new[] { "x" }));
    }

    // ═════════ กำหนดชำระตั้งต้น ═════════

    [Fact]
    public void โหมดบังคับไม่กรอกกำหนดเวลา_ใช้ค่าตั้งต้น1440_บริการกรอก0_ไม่กำหนด_ไม่บังคับ_ไม่มีกำหนด()
    {
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 50), Rule());
        Assert.Equal(BookingPaymentPolicy.DefaultRequiredDeadlineMinutes, e.DeadlineMinutes);
        Assert.Equal(1440, e.DeadlineMinutes);
        Assert.True(e.DeadlineDefaulted);
        Assert.Contains("(ค่าตั้งต้น)", BookingPaymentPolicy.Describe(e));
        var now = new DateTime(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddMinutes(1440), BookingPaymentPolicy.HoldUntil(e, 17.5m, now, now.AddDays(3), false));
        Assert.Equal(now.AddHours(5), BookingPaymentPolicy.HoldUntil(e, 17.5m, now, now.AddHours(5), false));   // ไม่เกินเวลานัด

        var svcZero = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 50), Rule(deadline: 0));
        Assert.Equal(0, svcZero.DeadlineMinutes);
        Assert.False(svcZero.DeadlineDefaulted);
        var site30 = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.FullRequired, deadline: 30), Rule());
        Assert.Equal(30, site30.DeadlineMinutes);
        var optional = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositOptional, pct: 50), Rule());
        Assert.Equal(0, optional.DeadlineMinutes);
        Assert.Equal(0, BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(), Rule(pct: 50)).DeadlineMinutes);   // แบบเดิม
    }

    // ═════════ ยอดล่วงหน้า × ค่าจัดส่ง (คำตัดสิน 226-4) ═════════

    [Fact]
    public void มัดจำคิดจากค่าบริการ_ชำระเต็มรวมค่าจัดส่ง()
    {
        var dep = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 50), Rule());
        Assert.Equal(17.50m, BookingPaymentPolicy.AmountDue(dep, 35m, false, 50m));
        Assert.Contains("ไม่รวมค่าจัดส่ง ฿50.00", BookingPaymentPolicy.AmountBasis(dep, 35m, 17.5m, 50m));
        var full = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.FullRequired), Rule());
        Assert.Equal(85m, BookingPaymentPolicy.AmountDue(full, 35m, false, 50m));
        Assert.Equal(85m, BookingPaymentPolicy.BookingDeposit(BookingType.PrePayment, full, 35m, null, null, false, 50m));
        Assert.Equal("ค่าบริการ ฿35.00 + ค่าจัดส่ง ฿50.00 = ฿85.00", BookingPaymentPolicy.AmountBasis(full, 35m, 85m, 50m));
        // ไม่มีค่าส่ง ⇒ ข้อความเดิมทุกตัวอักษร
        Assert.Equal(BookingPaymentPolicy.AmountBasis(full, 35m, 35m), BookingPaymentPolicy.AmountBasis(full, 35m, 35m, 0m));
    }

    [Fact]
    public void แบ่งยอดล่วงหน้า_ส่วนค่าบริการไม่เกินค่าบริการ_ส่วนเกินคือค่าจัดส่ง()
    {
        Assert.Equal(new BookingPrepaySplit(17.5m, 0m), BookingFulfillment.SplitPrepayment(17.5m, 35m, 50m));
        Assert.Equal(new BookingPrepaySplit(35m, 50m), BookingFulfillment.SplitPrepayment(85m, 35m, 50m));
        Assert.Equal(new BookingPrepaySplit(35m, 0m), BookingFulfillment.SplitPrepayment(35m, 35m, 0m));
        Assert.Equal(new BookingPrepaySplit(35m, 50m), BookingFulfillment.SplitPrepayment(100m, 35m, 50m));   // ไม่เกินค่าส่งจริง
        Assert.Equal(85.01m, BookingFulfillment.GrandTotal(35.005m, 50m));   // AwayFromZero
    }

    // ═════════ จัดส่ง / รับเอง ═════════

    private static BookingFulfillmentRule Site(bool d, bool p, string? def = null) => new(d, p, def);

    [Fact]
    public void ลำดับ_บริการชนะเว็บทีละช่อง_ค่าตั้งต้นที่ชี้วิธีที่ปิด_ไม่มีผล()
    {
        var e = BookingFulfillment.Resolve(Site(true, true, "delivery"), new BookingFulfillmentRule(null, null, null));
        Assert.True(e.Enabled); Assert.True(e.CustomerChooses); Assert.Equal("delivery", e.DefaultMethod); Assert.False(e.FromService);
        var svc = BookingFulfillment.Resolve(Site(true, true, "delivery"), new BookingFulfillmentRule(false, null, null));
        Assert.False(svc.Delivery); Assert.True(svc.Pickup); Assert.Null(svc.DefaultMethod); Assert.True(svc.FromService);
        var off = BookingFulfillment.Resolve(Site(false, false), null);
        Assert.False(off.Enabled);
        Assert.Contains("ไม่มีขั้น", BookingFulfillment.Describe(off));
    }

    [Fact]
    public void วิธีที่ใช้จริง_ว่างได้ค่าตั้งต้นหรือวิธีเดียว_ขอวิธีที่ปิดโยน_บริการไม่มีขั้นนี้ส่งวิธีมาโยน()
    {
        var both = BookingFulfillment.Resolve(Site(true, true), null);
        Assert.Throws<BusinessRuleException>(() => BookingFulfillment.ResolveMethod(both, null));   // ต้องเลือก
        Assert.Equal("pickup", BookingFulfillment.ResolveMethod(both, "PICKUP"));
        Assert.Equal("delivery", BookingFulfillment.ResolveMethod(BookingFulfillment.Resolve(Site(true, true, "delivery"), null), ""));
        var pickupOnly = BookingFulfillment.Resolve(Site(false, true), null);
        Assert.Equal("pickup", BookingFulfillment.ResolveMethod(pickupOnly, null));
        Assert.Throws<BusinessRuleException>(() => BookingFulfillment.ResolveMethod(pickupOnly, "delivery"));
        Assert.Throws<BusinessRuleException>(() => BookingFulfillment.ResolveMethod(both, "drone"));
        var none = BookingFulfillment.Resolve(Site(false, false), null);
        Assert.Null(BookingFulfillment.ResolveMethod(none, null));       // การจองนัดหมายเดิม ไม่แตะ
        Assert.Throws<BusinessRuleException>(() => BookingFulfillment.ResolveMethod(none, "delivery"));
    }

    [Fact]
    public void ที่อยู่จัดส่ง_บังคับเฉพาะจัดส่ง_รหัสไปรษณีย์5หลัก_ยอดขั้นต่ำ()
    {
        Assert.Empty(BookingFulfillment.AddressProblems("pickup", null, null, null));
        Assert.Empty(BookingFulfillment.AddressProblems(null, null, null, null));
        Assert.Equal(3, BookingFulfillment.AddressProblems("delivery", "", "", "").Count);
        Assert.Single(BookingFulfillment.AddressProblems("delivery", "99/1 ถ.สุขุมวิท", "กรุงเทพมหานคร", "1011"));
        Assert.Empty(BookingFulfillment.AddressProblems("delivery", "99/1 ถ.สุขุมวิท", "กรุงเทพมหานคร", "10110"));
        Assert.Contains("รับเอง", BookingFulfillment.MinAmountProblem("delivery", 35m, 100m, pickupAvailable: true));
        Assert.Null(BookingFulfillment.MinAmountProblem("delivery", 100m, 100m, true));
        Assert.Null(BookingFulfillment.MinAmountProblem("pickup", 35m, 100m, true));
        Assert.NotEmpty(BookingFulfillment.ValidateSettings("boat", -1m, null));
        Assert.Empty(BookingFulfillment.ValidateSettings("pickup", 0m, "รับที่ร้าน 10:00–18:00"));
    }

    [Fact]
    public void บรรทัดสรุป_จัดส่งบอกค่าส่งและที่อยู่_รับเองบอกที่รับ()
    {
        Assert.Equal("จัดส่ง (ค่าจัดส่ง ฿50.00) ถึง: 99/1 ถ.สุขุมวิท กรุงเทพมหานคร 10110",
            BookingFulfillment.SummaryLine("delivery", 50m, "99/1 ถ.สุขุมวิท", "กรุงเทพมหานคร", "10110", null));
        Assert.StartsWith("จัดส่ง (ส่งฟรี)", BookingFulfillment.SummaryLine("delivery", 0m, "a", "b", "10110", null));
        Assert.Equal("มารับเองที่ร้าน — สาขาสยาม 10:00", BookingFulfillment.SummaryLine("pickup", 0m, null, null, null, " สาขาสยาม 10:00 "));
        Assert.Equal(FulfillmentMethod.Label("pickup"), BookingFulfillment.Label("pickup"));   // ป้ายตัวตั้งเดียวกับคำสั่งซื้อ
        Assert.Equal("", BookingFulfillment.Label(null));
        Assert.Null(BookingFulfillment.SummaryLine(null, 0m, null, null, null, null));
    }

    // ═════════ ตัวคิดค่าส่งของร้านตัวเดียว ═════════

    private static SiteShippingZone Zone(string name, string? provinces, params (string Name, decimal Rate, decimal? FreeAbove)[] rates)
    {
        var z = new SiteShippingZone { Name = name, Provinces = provinces, IsActive = true };
        var i = 0;
        foreach (var r in rates)
            z.Rates.Add(new SiteShippingRate { Name = r.Name, RateType = ShippingRateType.Flat, BaseRate = r.Rate, FreeAboveAmount = r.FreeAbove, IsActive = true, SortOrder = i++ });
        return z;
    }

    private static CalculateShippingRequest Addr(string? province, string? postal = "10110") => new() { Province = province, PostalCode = postal };

    [Fact]
    public void ค่าส่ง_ตามโซนจังหวัด_ส่งฟรีเมื่อครบ_ไม่มีโซนใช้เหมาจ่าย()
    {
        var zones = new[] { Zone("กทม.", "กรุงเทพมหานคร", ("Grab", 50m, 500m)) };
        var r = ShopDeliveryFee.Compute(zones, Addr("กรุงเทพมหานคร"), 35m, 0m, null, null, false, false, null, true);
        Assert.Null(r.Problem); Assert.Equal(50m, r.Fee); Assert.NotNull(r.Chosen);
        var free = ShopDeliveryFee.Compute(zones, Addr("กรุงเทพมหานคร"), 600m, 0m, null, null, false, false, null, true);
        Assert.Equal(0m, free.Fee); Assert.True(free.Chosen!.IsFree);
        var outside = ShopDeliveryFee.Compute(zones, Addr("เชียงใหม่"), 35m, 0m, null, null, false, false, null, pickupAvailable: true);
        Assert.Contains("มารับเองที่ร้าน", outside.Problem);
        Assert.Equal(0m, outside.Fee);
        var missing = ShopDeliveryFee.Compute(zones, Addr(null, null), 35m, 0m, null, null, false, false, null, true);
        Assert.Equal("กรุณากรอกจังหวัดและรหัสไปรษณีย์เพื่อคำนวณค่าจัดส่ง", missing.Problem);

        var flat = ShopDeliveryFee.Compute(Array.Empty<SiteShippingZone>(), Addr(null, null), 35m, 0m, 40m, 300m, false, false, null, true);
        Assert.Null(flat.Problem); Assert.Equal(40m, flat.Fee); Assert.False(flat.UsesZones);
        Assert.Equal(0m, ShopDeliveryFee.Compute(Array.Empty<SiteShippingZone>(), Addr(null), 300m, 0m, 40m, 300m, false, false, null, true).Fee);
        Assert.Equal(0m, ShopDeliveryFee.Compute(Array.Empty<SiteShippingZone>(), Addr(null), 35m, 0m, 40m, null, false, couponFreeShip: true, null, true).Fee);
    }

    [Fact]
    public void ค่าส่ง_หลายอัตรา_ต้องเลือก_อัตราที่ไม่อยู่ในตัวเลือกโยน_ไม่สลับเงียบ()
    {
        var zones = new[] { Zone("ทั่วไทย", null, ("ไปรษณีย์", 40m, null), ("ด่วน", 90m, null)) };
        var pick = ShopDeliveryFee.Compute(zones, Addr("ขอนแก่น"), 35m, 0m, null, null, false, false, null, false);
        Assert.Equal("กรุณาเลือกวิธีจัดส่ง", pick.Problem);
        Assert.Equal(2, pick.Options.Count);
        var chosen = ShopDeliveryFee.Compute(zones, Addr("ขอนแก่น"), 35m, 0m, null, null, false, false, pick.Options[1].RateId, false);
        Assert.Equal(90m, chosen.Fee);
        Assert.Throws<BusinessRuleException>(() => ShopDeliveryFee.Compute(zones, Addr("ขอนแก่น"), 35m, 0m, null, null, false, false, Guid.NewGuid(), false));
    }

    // ═════════ แจ้ง "ชำระแล้ว — ยืนยันการจอง" ═════════

    [Fact]
    public void คิวแจ้งชำระแล้วยืนยัน_รู้จักชนิด_ข้ามเมื่อไม่ได้ยืนยัน()
    {
        Assert.True(DeferredNotifyPolicy.IsKnownKind(DeferredNotifyPolicy.BookingPaidConfirmed));
        Assert.True(CustomerNotifyEvents.IsKnown(CustomerNotifyEvents.BookingPaidConfirmed));
        Assert.Null(DeferredNotifyPolicy.PaidConfirmedSkipReason(BookingStatus.Confirmed));
        Assert.Null(DeferredNotifyPolicy.PaidConfirmedSkipReason(BookingStatus.Completed));
        Assert.NotNull(DeferredNotifyPolicy.PaidConfirmedSkipReason(BookingStatus.Cancelled));
        Assert.NotNull(DeferredNotifyPolicy.PaidConfirmedSkipReason(BookingStatus.Pending));
        Assert.NotNull(DeferredNotifyPolicy.PaidConfirmedSkipReason(null));
        var msg = CmsNotificationText.CustomerBookingPaidConfirmed("ร้านขนม", "BK-2611-0005", "Sourdough สังขยา", "คุณเอ",
            new DateOnly(2026, 11, 12), new TimeOnly(10, 0), null, 17.5m);
        Assert.Contains("ยืนยันการจอง", msg.Subject);
        Assert.Contains("฿17.50", msg.Text);
        Assert.NotEqual(CustomerNotifyKeys.Booking(Guid.Empty, CustomerNotifyEvents.BookingConfirmed),
            CustomerNotifyKeys.Booking(Guid.Empty, CustomerNotifyEvents.BookingPaidConfirmed));   // คีย์กันซ้ำแยกจากข้อความตอนจอง
    }

    // ═════════ ฝ่ายค้านรอบ 226 ═════════

    [Fact]
    public void ข้อ5_มัดจำบังคับแต่ไม่มียอด_รอร้านยืนยันเหมือนชนิดที่เก็บเงินไม่ได้_ทิศตรงข้ามมียอดไม่แตะ()
    {
        var noAmount = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired), Rule());
        Assert.NotNull(noAmount.Problem);
        Assert.True(noAmount.RequiredButUncollectable);
        Assert.True(BookingPaymentPolicy.OnSeat(noAmount, 0m, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), false).ForcePending);
        var optionalNoAmount = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositOptional), Rule());
        Assert.False(optionalNoAmount.RequiredButUncollectable);   // ไม่บังคับ ⇒ ไม่ถูกกัก
        Assert.False(BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 30), Rule()).RequiredButUncollectable);
    }

    [Fact]
    public void ข้อ4_ได้ที่จากคิวสำรอง_ไม่ใช้กำหนดชำระค่าตั้งต้น_แต่ใช้ค่าที่เจ้าของกรอก()
    {
        var defaulted = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 50), Rule());
        var promo = BookingPaymentPolicy.ForWaitlistPromotion(defaulted);
        Assert.Equal(0, promo.DeadlineMinutes);
        var now = DateTime.UtcNow;
        var seat = BookingPaymentPolicy.OnSeat(promo, 17.5m, now, now.AddDays(2), false);
        Assert.True(seat.ForcePending);
        Assert.Null(seat.PaymentHoldUntil);
        var owner = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 50, deadline: 60), Rule());
        Assert.Equal(60, BookingPaymentPolicy.ForWaitlistPromotion(owner).DeadlineMinutes);
        // ข้อ 3: เจ้าของกรอก 0 = ไม่กำหนด (ไม่ใช่ค่าตั้งต้น)
        var zero = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 50, deadline: 0), Rule());
        Assert.Equal(0, zero.DeadlineMinutes);
        Assert.False(zero.DeadlineDefaulted);
    }

    [Fact]
    public void ค่าตั้งต้นชี้วิธีที่ปิด_ปฏิเสธ_และบิลPOSมีบรรทัดค่าจัดส่ง()
    {
        Assert.NotNull(BookingFulfillment.DefaultPointsToDisabled("delivery", false, true));
        Assert.NotNull(BookingFulfillment.DefaultPointsToDisabled("pickup", true, false));
        Assert.Null(BookingFulfillment.DefaultPointsToDisabled("pickup", false, true));
        Assert.Null(BookingFulfillment.DefaultPointsToDisabled(null, false, false));
        Assert.Equal(50.01m, PosBookingIntake.DeliveryLine(50.005m));
        Assert.Null(PosBookingIntake.DeliveryLine(0m));
        Assert.Equal("ค่าจัดส่ง", PosBookingIntake.DeliveryLineName);
    }
}
