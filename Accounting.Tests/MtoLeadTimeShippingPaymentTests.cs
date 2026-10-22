using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// สินค้าตามสั่ง: วันรับเร็วสุด (ข้ามวันหยุดร้าน) · ค่าส่ง dynamic + VAT ค่าส่ง · ยอดที่ต้องจ่ายตอนนี้ (เต็ม/มัดจำ/จ่ายตอนรับ)
/// </summary>
public class MtoLeadTimeShippingPaymentTests
{
    private static ShopCalendar Cal(string closed = "", int? cutoff = null, params string[] holidays)
        => new(MtoLeadTime.ParseClosedWeekdays(closed), holidays.Select(s => DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet(), cutoff);

    // 2026-10-22 = วันพฤหัสบดี
    private static readonly DateTime ThuMorning = new(2026, 10, 22, 9, 0, 0);
    private static readonly DateTime ThuEvening = new(2026, 10, 22, 18, 30, 0);

    [Fact]
    public void ผลิต0วัน_สั่งก่อนตัดรอบวันเปิด_รับวันนี้()
        => Assert.Equal(new DateOnly(2026, 10, 22), MtoLeadTime.EarliestReadyDate(ThuMorning, 0, 0, Cal(cutoff: 15)));

    [Fact]
    public void ผลิต2วัน_ไม่มีวันหยุด_รับวันเสาร์()
        => Assert.Equal(new DateOnly(2026, 10, 24), MtoLeadTime.EarliestReadyDate(ThuMorning, 2, 0, Cal()));

    [Fact]
    public void ปิดวันอาทิตย์_ข้ามไม่นับเป็นวันผลิต()
        // พฤ → ศ(1) ส(2) [อา ปิด] จ(3)
        => Assert.Equal(new DateOnly(2026, 10, 26), MtoLeadTime.EarliestReadyDate(ThuMorning, 1, 2, Cal("0")));

    [Fact]
    public void สั่งหลังตัดรอบ_เริ่มนับวันถัดไป()
        => Assert.Equal(new DateOnly(2026, 10, 24), MtoLeadTime.EarliestReadyDate(ThuEvening, 1, 0, Cal(cutoff: 17)));

    [Fact]
    public void วันหยุดร้านเฉพาะวัน_และวันสั่งเป็นวันปิด_ถูกข้าม()
    {
        // ศ 23 ต.ค. วันปิยมหาราช = ร้านหยุด · ผลิต 1 วัน ⇒ ส 24
        Assert.Equal(new DateOnly(2026, 10, 24), MtoLeadTime.EarliestReadyDate(ThuMorning, 1, 0, Cal("", null, "2026-10-23")));
        // สั่งวันอาทิตย์ (ปิด) ผลิต 0 วัน ⇒ จันทร์
        Assert.Equal(new DateOnly(2026, 10, 26), MtoLeadTime.EarliestReadyDate(new DateTime(2026, 10, 25, 10, 0, 0), 0, 0, Cal("0")));
    }

    [Fact]
    public void ปิดทุกวัน_ล้มดังไม่วนไม่จบ()
    {
        Assert.Throws<BusinessRuleException>(() => MtoLeadTime.EarliestReadyDate(ThuMorning, 1, 0, Cal("0,1,2,3,4,5,6")));
        Assert.Throws<BusinessRuleException>(() => MtoLeadTime.NormalizeClosedWeekdays("6,5,4,3,2,1,0"));
        Assert.Equal("0,6", MtoLeadTime.NormalizeClosedWeekdays("6, 0 ,x,9,6"));
        Assert.Null(MtoLeadTime.NormalizeClosedWeekdays(""));
    }

    // ─────────── ค่าส่ง ───────────

    private static ShippingRateFacts Rate(ShippingRateType t, decimal baseRate, decimal? perKg = null, decimal? freeAbove = null,
        decimal? min = null, decimal? max = null) => new(t, baseRate, perKg, freeAbove, min, max);

    [Fact]
    public void ค่าส่งเหมาจ่าย_ตามน้ำหนัก_ส่งฟรีเมื่อครบยอด()
    {
        Assert.Equal(50m, ShippingFeeRules.Quote(Rate(ShippingRateType.Flat, 50m), 300m, 1m).Fee);
        Assert.Equal(52.5m, ShippingFeeRules.Quote(Rate(ShippingRateType.ByWeight, 30m, perKg: 15m), 300m, 1.5m).Fee);
        var free = ShippingFeeRules.Quote(Rate(ShippingRateType.Flat, 50m, freeAbove: 1000m), 1000m, 1m);
        Assert.True(free.IsFree);
        Assert.Equal(0m, free.Fee);
        Assert.True(ShippingFeeRules.Quote(Rate(ShippingRateType.Free, 99m), 10m, 1m).IsFree);
    }

    [Fact]
    public void น้ำหนักนอกช่วง_อัตรานี้ใช้ไม่ได้()
        => Assert.False(ShippingFeeRules.Quote(Rate(ShippingRateType.Flat, 50m, min: 2m, max: 5m), 100m, 6m).Applicable);

    [Fact]
    public void ปัดค่าส่งแบบ_AwayFromZero_ไม่ใช่banker()
        // 10 + 0.5 × 0.05 = 10.025 ⇒ 10.03 (banker's ได้ 10.02)
        => Assert.Equal(10.03m, ShippingFeeRules.Quote(Rate(ShippingRateType.ByWeight, 10m, perKg: 0.5m), 0m, 0.05m).Fee);

    [Fact]
    public void ค่าส่งเหมาจ่ายระดับร้าน_และส่งฟรีเมื่อครบ()
    {
        Assert.Equal(40m, ShippingFeeRules.SiteFlatFee(40m, 500m, 499.99m));
        Assert.Equal(0m, ShippingFeeRules.SiteFlatFee(40m, 500m, 500m));
        Assert.Equal(0m, ShippingFeeRules.SiteFlatFee(null, null, 10m));
    }

    [Fact]
    public void VATค่าส่ง_ตามสินค้าในบิล_ผู้ขายไม่จดได้0()
    {
        Assert.Equal(7m, ShippingFeeRules.VatRateForShipping(true, new[] { 0m, 7m }));
        Assert.Equal(0m, ShippingFeeRules.VatRateForShipping(true, new[] { 0m }));
        Assert.Equal(0m, ShippingFeeRules.VatRateForShipping(false, new[] { 7m }));
    }

    [Fact]
    public void วิธีรับสินค้า_ว่างเป็นจัดส่ง_ไม่รู้จักโยน()
    {
        Assert.Equal(FulfillmentMethod.Delivery, FulfillmentMethod.Normalize(null));
        Assert.Equal(FulfillmentMethod.Pickup, FulfillmentMethod.Normalize(" Pickup "));
        Assert.Throws<BusinessRuleException>(() => FulfillmentMethod.Normalize("drone"));
    }

    // ─────────── ยอดที่ต้องจ่ายตอนนี้ ───────────

    [Fact]
    public void เต็มจำนวน_เป็นพฤติกรรมเดิม_ยอดค้างทั้งใบ()
    {
        var d = MtoPaymentTerms.Compute(null, 1500m, null, null, 0m);
        Assert.Equal(PaymentTermMode.Full, d.Mode);
        Assert.Equal(1500m, d.DueNow);
        Assert.Equal(1500m, d.NextPayable);
        var paidHalf = MtoPaymentTerms.Compute(PaymentTermMode.Full, 1500m, null, null, 500m);
        Assert.Equal(1000m, paidHalf.DueNow);
    }

    [Fact]
    public void มัดจำเปอร์เซ็นต์_ปัดAwayFromZero_จ่ายมัดจำแล้วเหลือยอดคงค้างให้จ่ายต่อ()
    {
        var d = MtoPaymentTerms.Compute(PaymentTermMode.DepositPercent, 1234.5m, 30m, null, 0m);
        Assert.Equal(370.35m, d.DepositRequired);              // 370.35 พอดี
        Assert.Equal(370.35m, d.DueNow);
        Assert.Equal(1234.5m, d.Balance);
        var afterDeposit = MtoPaymentTerms.Compute(PaymentTermMode.DepositPercent, 1234.5m, 30m, null, 370.35m);
        Assert.Equal(0m, afterDeposit.DueNow);
        Assert.Equal(864.15m, afterDeposit.Balance);
        Assert.Equal(864.15m, afterDeposit.NextPayable);
        Assert.Equal(1.01m, MtoPaymentTerms.Compute(PaymentTermMode.DepositPercent, 2.01m, 50m, null, 0m).DueNow); // 1.005 ⇒ 1.01
    }

    [Fact]
    public void มัดจำคงที่_เกินยอดใบได้ยอดใบ()
    {
        Assert.Equal(500m, MtoPaymentTerms.Compute(PaymentTermMode.DepositFixed, 1500m, null, 500m, 0m).DueNow);
        Assert.Equal(300m, MtoPaymentTerms.Compute(PaymentTermMode.DepositFixed, 300m, null, 500m, 0m).DueNow);
    }

    [Fact]
    public void ชำระเมื่อรับ_ไม่ต้องจ่ายตอนสั่ง_แต่ยังจ่ายล่วงหน้าได้()
    {
        var d = MtoPaymentTerms.Compute(PaymentTermMode.PayOnPickup, 800m, null, null, 0m);
        Assert.Equal(0m, d.DueNow);
        Assert.Equal(800m, d.NextPayable);
    }

    [Fact]
    public void ยอดจากมัดจำที่ตรึงไว้_ออเดอร์เดิมเป็นเต็มจำนวน_และไม่คิดมัดจำใหม่จากค่าตั้งวันนี้()
    {
        var legacy = MtoPaymentTerms.FromSnapshot(null, 990m, 0m, 0m);
        Assert.Equal(990m, legacy.NextPayable);                 // = TotalAmount − PaidAmount เดิม
        var snap = MtoPaymentTerms.FromSnapshot(PaymentTermMode.DepositPercent, 1000m, 300m, 0m);
        Assert.Equal(300m, snap.DueNow);
        Assert.Equal(700m, MtoPaymentTerms.FromSnapshot(PaymentTermMode.DepositPercent, 1000m, 300m, 300m).NextPayable);
        Assert.Equal(0m, MtoPaymentTerms.FromSnapshot(PaymentTermMode.PayOnPickup, 1000m, 0m, 0m).DueNow);
        // เท่ากับคิดสด ณ วันสั่ง
        var live = MtoPaymentTerms.Compute(PaymentTermMode.DepositPercent, 1000m, 30m, null, 0m);
        Assert.Equal(live, MtoPaymentTerms.FromSnapshot(live.Mode, live.Total, live.DepositRequired, 0m));
    }

    [Fact]
    public void จ่ายเกิน_ไม่ติดลบ_และค่าตั้งผิดถูกกัน()
    {
        var d = MtoPaymentTerms.Compute(PaymentTermMode.Full, 100m, null, null, 150m);
        Assert.Equal(0m, d.DueNow);
        Assert.Equal(0m, d.Balance);
        Assert.Throws<BusinessRuleException>(() => MtoPaymentTerms.ValidateConfig(PaymentTermMode.DepositPercent, 100m, null));
        Assert.Throws<BusinessRuleException>(() => MtoPaymentTerms.ValidateConfig(PaymentTermMode.DepositFixed, null, 0m));
        Assert.Throws<BusinessRuleException>(() => PaymentTermMode.Normalize("installment"));
    }
}
