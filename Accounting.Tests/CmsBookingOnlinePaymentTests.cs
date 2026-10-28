using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 205 W2 (R-08) — มัดจำการจองนัดหมายจ่ายได้เท่าไร/ตอนไหน · กันซ้ำด้วยเลขอ้างอิง · ด่านยอด/วันที่ · Q5 = ข (ไซต์เปิดเอง)</summary>
public class CmsBookingOnlinePaymentTests
{
    [Theory]
    [InlineData(BookingType.PrePayment, BookingStatus.Pending, 500, 0, 500)]
    [InlineData(BookingType.PrePayment, BookingStatus.Confirmed, 500, 200, 300)]
    [InlineData(BookingType.PrePayment, BookingStatus.Pending, 500, 500, 0)]
    [InlineData(BookingType.PrePayment, BookingStatus.Pending, 500, 900, 0)]      // จ่ายเกินแล้ว ⇒ ไม่ติดลบ
    [InlineData(BookingType.PrePayment, BookingStatus.Waitlisted, 500, 0, 0)]    // คิวสำรองยังไม่ได้คิว
    [InlineData(BookingType.PrePayment, BookingStatus.Cancelled, 500, 0, 0)]
    [InlineData(BookingType.Guaranteed, BookingStatus.Pending, 500, 0, 0)]       // ไม่ใช่บริการชำระล่วงหน้า
    [InlineData(BookingType.PrePayment, BookingStatus.Pending, 0, 0, 0)]         // มัดจำ 0 = ไม่มีอะไรให้จ่าย
    public void DepositDue_เฉพาะPrePaymentที่ยังรับเงินได้_ไม่เกินมัดจำ(BookingType type, BookingStatus status, decimal deposit, decimal paid, decimal expected)
        => Assert.Equal(expected, CmsBookingOnlinePayment.DepositDue(type, status, deposit, paid));

    [Fact]
    public void ไซต์ไม่เปิดรับมัดจำออนไลน์_มีเหตุผลบอกทางไปต่อ_ไม่silent()
    {
        var note = CmsBookingOnlinePayment.BlockedNote(false, BookingType.PrePayment, BookingStatus.Pending, 500m);
        Assert.NotNull(note);
        Assert.Contains("ยังไม่เปิดรับชำระมัดจำออนไลน์", note);
    }

    [Fact]
    public void ไซต์เปิด_จ่ายได้_ไม่มีเหตุบล็อก()
        => Assert.Null(CmsBookingOnlinePayment.BlockedNote(true, BookingType.PrePayment, BookingStatus.Pending, 500m));

    [Fact]
    public void รับครบแล้ว_ไม่ถือเป็นการบล็อก_แม้ไซต์ปิด()
        => Assert.Null(CmsBookingOnlinePayment.BlockedNote(false, BookingType.PrePayment, BookingStatus.Confirmed, 0m));

    [Fact]
    public void ยกเลิกหรือคิวสำรอง_บอกสถานะ()
    {
        Assert.Contains("ยกเลิก", CmsBookingOnlinePayment.BlockedNote(true, BookingType.PrePayment, BookingStatus.Cancelled, 0m));
        Assert.Contains("คิวสำรอง", CmsBookingOnlinePayment.BlockedNote(true, BookingType.PrePayment, BookingStatus.Waitlisted, 0m));
        Assert.Null(CmsBookingOnlinePayment.BlockedNote(false, BookingType.Guaranteed, BookingStatus.Pending, 0m));
    }

    [Fact]
    public void เลขอ้างอิงซ้ำ_ถูกจับ_และต่อท้ายไม่ซ้ำ()
    {
        var r1 = GatewayPaymentRecord.Reference(Guid.NewGuid());
        var r2 = GatewayPaymentRecord.Reference(Guid.NewGuid());
        var stored = CmsBookingOnlinePayment.AppendReference(null, r1);
        Assert.Equal(r1, stored);
        Assert.True(CmsBookingOnlinePayment.AlreadyRecorded(stored, r1));
        Assert.False(CmsBookingOnlinePayment.AlreadyRecorded(stored, r2));
        var both = CmsBookingOnlinePayment.AppendReference(stored, r2);
        Assert.True(CmsBookingOnlinePayment.AlreadyRecorded(both, r1));
        Assert.True(CmsBookingOnlinePayment.AlreadyRecorded(both, r2));
        Assert.Equal(both, CmsBookingOnlinePayment.AppendReference(both, r1));   // ซ้ำ = ไม่ต่อท้ายอีก
        Assert.False(CmsBookingOnlinePayment.AlreadyRecorded(both, null));
        Assert.False(CmsBookingOnlinePayment.AlreadyRecorded(null, r1));
    }

    [Theory]
    [InlineData(0, 1000, 0, false)]
    [InlineData(-5, 1000, 0, false)]
    [InlineData(500.005, 1000, 0, false)]   // ทศนิยม 3 ตำแหน่ง
    [InlineData(500, 1000, 0, true)]
    [InlineData(1000, 1000, 0, true)]
    [InlineData(600, 1000, 500, false)]     // เกินยอดค้างของการจอง
    [InlineData(500, 1000, 500, true)]
    public void ValidateAmount_ด่านยอด(decimal amount, decimal total, decimal paid, bool ok)
        => Assert.Equal(ok, CmsBookingOnlinePayment.ValidateAmount(amount, total, paid) == null);

    [Fact]
    public void ValidatePaidAt_อนาคตและเก่าเกินปีถูกปฏิเสธ_nullผ่าน()
    {
        var now = new DateTime(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);
        Assert.Null(CmsBookingOnlinePayment.ValidatePaidAt(null, now));
        Assert.Null(CmsBookingOnlinePayment.ValidatePaidAt(now.AddHours(-3), now));
        Assert.NotNull(CmsBookingOnlinePayment.ValidatePaidAt(now.AddDays(2), now));
        Assert.NotNull(CmsBookingOnlinePayment.ValidatePaidAt(now.AddYears(-2), now));
    }

    [Theory]
    [InlineData(500, 500, true)]
    [InlineData(500, 499.996, true)]
    [InlineData(500, 499.98, false)]
    [InlineData(0, 0, false)]
    public void DepositSettled_เกณฑ์เดียวกับAutoApprove(decimal deposit, decimal paid, bool settled)
    {
        Assert.Equal(settled, CmsBookingOnlinePayment.DepositSettled(deposit, paid));
        Assert.Equal(settled, CmsBookingErpPolicy.AutoApprove(BookingType.PrePayment, deposit, paid));
    }

    [Fact]
    public void PaidNote_บอกครบมัดจำเมื่อครบ()
    {
        var at = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);
        var n = CmsBookingOnlinePayment.PaidNote(500m, "PAY-INTENT-x", at, online: true, paidTotal: 500m, depositAmount: 500m);
        Assert.Contains("ครบมัดจำ", n);
        Assert.Contains("ออนไลน์", n);
        var partial = CmsBookingOnlinePayment.PaidNote(200m, null, at, online: false, paidTotal: 200m, depositAmount: 500m);
        Assert.DoesNotContain("ครบมัดจำ", partial);
        Assert.Contains("พนักงานบันทึก", partial);
    }

    [Fact]
    public void โทเคนสาธารณะ_32ตัวอักษรฐานสิบหก_ไม่ซ้ำ()
    {
        var a = CmsBookingOnlinePayment.NewPublicToken();
        var b = CmsBookingOnlinePayment.NewPublicToken();
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, b);
    }
}
