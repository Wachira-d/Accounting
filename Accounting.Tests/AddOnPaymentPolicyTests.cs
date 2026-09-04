using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เกณฑ์ "ต้องจ่ายก่อนเปิด add-on ไหม" — ตัดสินว่าลูกค้าถูกเรียกเก็บเงินหรือไม่
/// จึงต้องล็อกทุกทิศ (กฎเหล็ก #4 G: logic เงินต้องมีเทสต์ในคอมมิตเดียวกัน)
/// </summary>
public class AddOnPaymentPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void เหมารายเดือนมีราคา_ลูกค้ากดเอง_ต้องจ่ายก่อน()
    {
        Assert.True(AddOnPaymentPolicy.RequiresPayment(
            AddOnGrantSource.OwnerSelfServe, PricingMethod.FlatMonthly, 499m, null, Now));
    }

    [Fact]
    public void ยังไม่ตั้งราคา_ห้ามบล็อกลูกค้า()
    {
        // ราคาว่าง/0 = เรายังตั้งราคาไม่เสร็จ ไม่ใช่ความผิดลูกค้า
        Assert.False(AddOnPaymentPolicy.RequiresPayment(
            AddOnGrantSource.OwnerSelfServe, PricingMethod.FlatMonthly, 0m, null, Now));
        Assert.False(AddOnPaymentPolicy.RequiresPayment(
            AddOnGrantSource.OwnerSelfServe, PricingMethod.FlatMonthly, null, null, Now));
        Assert.False(AddOnPaymentPolicy.RequiresPayment(
            AddOnGrantSource.OwnerSelfServe, null, 499m, null, Now));
    }

    [Theory]
    [InlineData(PricingMethod.PerUnit)]
    [InlineData(PricingMethod.PerCall)]
    [InlineData(PricingMethod.Tiered)]
    public void คิดตามปริมาณ_เก็บล่วงหน้าไม่ได้(PricingMethod method)
    {
        // ยังไม่รู้ว่าจะใช้กี่หน่วย ⇒ เรียกเก็บตอนเปิดเป็นการเดายอด
        Assert.False(AddOnPaymentPolicy.RequiresPayment(
            AddOnGrantSource.OwnerSelfServe, method, 5m, null, Now));
    }

    [Theory]
    [InlineData(AddOnGrantSource.AdminGranted)]
    [InlineData(AddOnGrantSource.BundledInPlan)]
    public void ของแถม_ไม่เก็บซ้ำ(AddOnGrantSource source)
    {
        Assert.False(AddOnPaymentPolicy.RequiresPayment(
            source, PricingMethod.FlatMonthly, 499m, null, Now));
    }

    [Fact]
    public void อยู่ในช่วงทดลองใช้ฟรี_ยังไม่เก็บ()
    {
        Assert.False(AddOnPaymentPolicy.RequiresPayment(
            AddOnGrantSource.OwnerSelfServe, PricingMethod.FlatMonthly, 499m,
            trialUntil: Now.AddDays(7), Now));
    }

    [Fact]
    public void trial_หมดแล้ว_กลับมาเก็บ()
    {
        Assert.True(AddOnPaymentPolicy.RequiresPayment(
            AddOnGrantSource.OwnerSelfServe, PricingMethod.FlatMonthly, 499m,
            trialUntil: Now.AddDays(-1), Now));
    }

    [Fact]
    public void สถานะเริ่มต้นตรงกับผลของเกณฑ์()
    {
        Assert.Equal(AddOnPaymentStatus.AwaitingPayment, AddOnPaymentPolicy.InitialStatus(true));
        Assert.Equal(AddOnPaymentStatus.NotRequired, AddOnPaymentPolicy.InitialStatus(false));
    }

    [Fact]
    public void ระหว่างรอตรวจสลิป_ยังใช้ได้_แต่ถูกปฏิเสธแล้วใช้ไม่ได้()
    {
        // คนที่โอนจริงแล้วรอแอดมินข้ามคืน ไม่ควรถูกปิดฟีเจอร์ที่เพิ่งจ่ายไป
        Assert.True(AddOnPaymentPolicy.UsableWhilePending(AddOnPaymentStatus.PendingReview));
        Assert.True(AddOnPaymentPolicy.UsableWhilePending(AddOnPaymentStatus.AwaitingPayment));
        Assert.True(AddOnPaymentPolicy.UsableWhilePending(AddOnPaymentStatus.Paid));
        Assert.False(AddOnPaymentPolicy.UsableWhilePending(AddOnPaymentStatus.Rejected));
    }

    [Theory]
    // ยอดที่ขึ้นบนปุ่ม "ชำระออนไลน์" ของลูกค้า — เคยเขียนซ้ำสองที่ ยุบมาที่เดียวแล้ว
    [InlineData(AddOnPaymentStatus.NotRequired, 499, 0)]
    [InlineData(AddOnPaymentStatus.Paid, 499, 0)]
    [InlineData(AddOnPaymentStatus.AwaitingPayment, 499, 499)]
    [InlineData(AddOnPaymentStatus.PendingReview, 499, 499)]
    [InlineData(AddOnPaymentStatus.Rejected, 499, 499)]
    public void ยอดค้างชำระตรงตามสถานะ(AddOnPaymentStatus status, decimal price, decimal expected)
        => Assert.Equal(expected, AddOnPaymentPolicy.AmountDue(status, price));

    [Fact]
    public void ราคายังไม่ตั้งหรือติดลบ_ยอดค้างต้องเป็นศูนย์_ไม่ใช่ติดลบ()
    {
        // ราคาติดลบไม่ควรเกิด แต่ถ้าหลุดมาจาก DB ต้องไม่กลายเป็น "คืนเงิน" ให้ลูกค้า
        Assert.Equal(0m, AddOnPaymentPolicy.AmountDue(AddOnPaymentStatus.AwaitingPayment, null));
        Assert.Equal(0m, AddOnPaymentPolicy.AmountDue(AddOnPaymentStatus.AwaitingPayment, -50m));
    }

    [Fact]
    public void ข้อความสถานะมาจากที่เดียว_และไม่ว่างในสถานะที่ผู้ใช้ต้องเห็น()
    {
        Assert.Equal("", AddOnPaymentPolicy.StatusLabel(AddOnPaymentStatus.NotRequired));
        foreach (var s in new[]
                 {
                     AddOnPaymentStatus.AwaitingPayment, AddOnPaymentStatus.PendingReview,
                     AddOnPaymentStatus.Paid, AddOnPaymentStatus.Rejected,
                 })
            Assert.False(string.IsNullOrWhiteSpace(AddOnPaymentPolicy.StatusLabel(s)));
    }
}
