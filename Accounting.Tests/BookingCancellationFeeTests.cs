using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 205 W2 (R-10 R-11) — ค่าธรรมเนียมยกเลิก/ไม่มาของการจองนัดหมาย · เพดาน = มัดจำ (คำตัดสินข้อ 126) · ไม่ตั้ง % = ไม่ตัดสินแทน</summary>
public class BookingCancellationFeeTests
{
    private static readonly DateTime Start = new(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc);   // 10:00 เวลาไทย

    [Fact]
    public void ไม่ตั้งเปอร์เซ็นต์_PolicyUndefined_ไม่ริบไม่คืนเอง()
    {
        var p = BookingCancellationFee.Plan(BookingCancelKind.NoShow, Start.AddHours(1), Start, 24, null, 2000m, 500m);
        Assert.True(p.PolicyUndefined);
        Assert.Equal(0m, p.ForfeitFromDeposit);
        Assert.Equal(0m, p.RefundDue);
        Assert.Contains("ไม่ได้กำหนดค่าธรรมเนียม", BookingCancellationFee.Describe(p, 24));
    }

    [Fact]
    public void ยกเลิกเองในหน้าต่างฟรี_ไม่มีค่าธรรมเนียม_คืนมัดจำทั้งหมด()
    {
        var p = BookingCancellationFee.Plan(BookingCancelKind.CustomerCancel, Start.AddHours(-30), Start, 24, 50m, 2000m, 500m);
        Assert.True(p.WithinFreeWindow);
        Assert.Equal(0m, p.Fee);
        Assert.Equal(500m, p.RefundDue);
        Assert.Contains("ยกเลิกฟรี", BookingCancellationFee.Describe(p, 24));
    }

    [Fact]
    public void ยกเลิกเองหลังหน้าต่างฟรี_ค่าธรรมเนียมตามเปอร์เซ็นต์_ริบไม่เกินมัดจำ_ส่วนเกินเป็นยอดค้าง()
    {
        // 50% ของ 2,000 = 1,000 แต่มัดจำรับไว้ 500 ⇒ ริบ 500 (ข้อ 126) · ค้าง 500 · ไม่มีอะไรคืน
        var p = BookingCancellationFee.Plan(BookingCancelKind.CustomerCancel, Start.AddHours(-2), Start, 24, 50m, 2000m, 500m);
        Assert.False(p.WithinFreeWindow);
        Assert.Equal(1000m, p.Fee);
        Assert.Equal(500m, p.ForfeitFromDeposit);
        Assert.Equal(0m, p.RefundDue);
        Assert.Equal(500m, p.UncollectedFee);
    }

    [Fact]
    public void ค่าธรรมเนียมน้อยกว่ามัดจำ_ริบเท่าค่าธรรมเนียม_ที่เหลือรอคืน()
    {
        var p = BookingCancellationFee.Plan(BookingCancelKind.CustomerCancel, Start.AddHours(-2), Start, 24, 10m, 2000m, 500m);
        Assert.Equal(200m, p.Fee);
        Assert.Equal(200m, p.ForfeitFromDeposit);
        Assert.Equal(300m, p.RefundDue);
        Assert.True(p.HasRefund);
    }

    [Fact]
    public void ไม่มา_ไม่มีหน้าต่างฟรีแม้ยังก่อนนัด_ริบตามเปอร์เซ็นต์()
    {
        // ไม่มา = เลยเวลานัดแล้วเสมอ แต่ให้แน่ใจว่าหน้าต่างฟรีไม่ถูกใช้กับ NoShow
        var p = BookingCancellationFee.Plan(BookingCancelKind.NoShow, Start.AddHours(-30), Start, 24, 100m, 1500m, 1500m);
        Assert.False(p.WithinFreeWindow);
        Assert.Equal(1500m, p.ForfeitFromDeposit);
        Assert.Equal(0m, p.RefundDue);
    }

    [Fact]
    public void เปอร์เซ็นต์ศูนย์_ตั้งใจไม่คิดค่าธรรมเนียม_คืนทั้งหมด_ไม่ใช่PolicyUndefined()
    {
        var p = BookingCancellationFee.Plan(BookingCancelKind.NoShow, Start.AddHours(1), Start, null, 0m, 1000m, 300m);
        Assert.False(p.PolicyUndefined);
        Assert.Equal(0m, p.ForfeitFromDeposit);
        Assert.Equal(300m, p.RefundDue);
    }

    [Fact]
    public void ไม่มีมัดจำ_ค่าธรรมเนียมทั้งก้อนเป็นยอดค้างCRM()
    {
        var p = BookingCancellationFee.Plan(BookingCancelKind.NoShow, Start.AddHours(1), Start, 24, 30m, 1000m, 0m);
        Assert.Equal(300m, p.Fee);
        Assert.Equal(0m, p.ForfeitFromDeposit);
        Assert.Equal(300m, p.UncollectedFee);
        Assert.Contains("ยังไม่ได้ชำระ", BookingCancellationFee.Describe(p, 24));
    }

    [Theory]
    [InlineData(500, 934.58, 1000, 934.58, 467.29)]   // ใบมัดจำ 1,000 รวม VAT (ฐาน 934.58) ริบ 500 ⇒ ฐาน 467.29
    [InlineData(500, 934.58, 1000, 100, 100)]          // ฐานคงค้างเหลือ 100 ⇒ ไม่เกิน
    [InlineData(500, 1000, 1000, 1000, 500)]           // ไม่มี VAT ⇒ ฐาน = gross
    [InlineData(0, 934.58, 1000, 934.58, 0)]
    public void ForfeitBase_สัดส่วนฐานต่อยอดรวม_ไม่เกินฐานคงค้าง(decimal gross, decimal subTotal, decimal total, decimal remaining, decimal expected)
        => Assert.Equal(expected, BookingCancellationFee.ForfeitBase(gross, subTotal, total, remaining));

    [Fact]
    public void เปอร์เซ็นต์เกิน100_ถูกตัดที่100_ค่าติดลบไม่เกิดจำนวนลบ()
    {
        var p = BookingCancellationFee.Plan(BookingCancelKind.NoShow, Start.AddHours(1), Start, 24, 250m, 1000m, -50m);
        Assert.Equal(1000m, p.Fee);
        Assert.Equal(0m, p.ForfeitFromDeposit);
        Assert.Equal(0m, p.RefundDue);
    }
}
