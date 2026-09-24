using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ยอดที่เปิดให้ **แขกจ่ายออนไลน์** — ตัวเลขนี้กลายเป็นเงินจริงที่เรียกเก็บ
/// จึงต้องล็อกทุกทิศ ไม่ใช่เฉพาะทางที่ตั้งใจแก้
/// (บทเรียน: "เทสต์ที่เขียนจากเคสที่เจอ ไม่ใช่จากโดเมนของ input จะพลาดทิศที่ไม่เคยเจอ")
/// </summary>
public class LodgingAmountsTests
{
    [Fact]
    public void ยอดคงเหลือไม่ติดลบเมื่อจ่ายเกิน()
    {
        Assert.Equal(0m, LodgingAmounts.BalanceDue(1000m, 0m, 1200m));
        Assert.Equal(300m, LodgingAmounts.BalanceDue(1000m, 200m, 900m));
    }

    [Fact]
    public void รอมัดจำ_เก็บเฉพาะส่วนมัดจำที่ยังขาด_ไม่ใช่ยอดเต็ม()
    {
        // ห้อง 4,000 · มัดจำ 50% = 2,000 · ยังไม่จ่ายอะไรเลย
        var due = LodgingAmounts.OnlinePayableAmount(
            LodgingReservationStatus.Pending,
            depositRequired: 2000m, depositPaid: 0m,
            totalAmount: 4000m, folioTotal: 0m, paidAmount: 0m);
        Assert.Equal(2000m, due);
    }

    [Fact]
    public void รอมัดจำ_จ่ายมัดจำไปบางส่วนแล้ว_เก็บเฉพาะที่ขาด()
    {
        var due = LodgingAmounts.OnlinePayableAmount(
            LodgingReservationStatus.Pending, 2000m, 500m, 4000m, 0m, 500m);
        Assert.Equal(1500m, due);
    }

    [Fact]
    public void ที่พักที่ไม่เก็บมัดจำ_ต้องเปิดให้จ่ายยอดค้างทั้งก้อน()
    {
        // DepositRequired = 0 · ถ้าคืน null ปุ่มจ่ายจะไม่มีวันโผล่
        var due = LodgingAmounts.OnlinePayableAmount(
            LodgingReservationStatus.Pending, 0m, 0m, 4000m, 0m, 0m);
        Assert.Equal(4000m, due);
    }

    [Fact]
    public void มัดจำมากกว่ายอดค้าง_ต้องไม่เก็บเกินยอดค้าง()
    {
        // เคสที่ยอดถูกปรับลดหลังจองแล้ว — ห้ามเรียกเก็บตามมัดจำเดิม
        var due = LodgingAmounts.OnlinePayableAmount(
            LodgingReservationStatus.Pending, 2000m, 0m, 1200m, 0m, 0m);
        Assert.Equal(1200m, due);
    }

    [Fact]
    public void ยืนยันแล้ว_เก็บยอดคงเหลือรวม_folio()
    {
        var due = LodgingAmounts.OnlinePayableAmount(
            LodgingReservationStatus.Confirmed, 2000m, 2000m, 4000m, 350m, 2000m);
        Assert.Equal(2350m, due);
    }

    [Theory]
    [InlineData(LodgingReservationStatus.Cancelled)]
    [InlineData(LodgingReservationStatus.NoShow)]
    [InlineData(LodgingReservationStatus.CheckedOut)]
    public void สถานะที่ไม่ควรรับเงินเพิ่ม_ต้องคืน_null(LodgingReservationStatus status)
    {
        Assert.Null(LodgingAmounts.OnlinePayableAmount(status, 2000m, 0m, 4000m, 0m, 0m));
    }

    [Fact]
    public void จ่ายครบแล้ว_ต้องคืน_null_ทุกสถานะที่ยังเปิดอยู่()
    {
        Assert.Null(LodgingAmounts.OnlinePayableAmount(
            LodgingReservationStatus.Confirmed, 2000m, 2000m, 4000m, 0m, 4000m));
        Assert.Null(LodgingAmounts.OnlinePayableAmount(
            LodgingReservationStatus.CheckedIn, 2000m, 2000m, 4000m, 0m, 4000m));
    }

    [Fact]
    public void เศษสตางค์ที่เหลือน้อยกว่าครึ่งสตางค์_ถือว่าจ่ายครบ()
    {
        // กันปุ่ม "ชำระ ฿0.00" ที่กดแล้ว provider ปฏิเสธ
        Assert.Null(LodgingAmounts.OnlinePayableAmount(
            LodgingReservationStatus.Confirmed, 0m, 0m, 1000.004m, 0m, 1000m));
    }

    // ═══ C9 รอบ 193 หลังฝ่ายค้าน — หน้าแขกแสดงยอด/ข้อความจากเซิร์ฟเวอร์ ═══

    [Fact]
    public void ปิดยืนยันอัตโนมัติ_จ่ายมัดจำครบแล้ว_ป้ายไม่บอกให้จ่ายมัดจำอีก_ยอดคือยอดคงเหลือ()
    {
        Assert.Equal("ชำระมัดจำแล้ว รอที่พักยืนยัน", LodgingAmounts.StatusLabel(LodgingReservationStatus.Pending, 2000m, 2000m));
        Assert.Equal(5450m, LodgingAmounts.OnlinePayableAmount(LodgingReservationStatus.Pending, 2000m, 2000m, 7450m, 0m, 2000m));
        var note = LodgingAmounts.OnlinePaymentNote(LodgingReservationStatus.Pending, 2000m, 2000m, 7450m, 0m, 2000m, autoConfirmOnDeposit: false);
        Assert.Contains("รอที่พักยืนยัน", note);
        Assert.DoesNotContain("อัตโนมัติ", note);
    }

    [Fact]
    public void ยังไม่จ่ายมัดจำ_ข้อความตามค่าตั้งยืนยันอัตโนมัติ_สองทิศ()
    {
        Assert.Equal("รอชำระมัดจำ", LodgingAmounts.StatusLabel(LodgingReservationStatus.Pending, 2000m, 0m));
        var on = LodgingAmounts.OnlinePaymentNote(LodgingReservationStatus.Pending, 2000m, 0m, 7450m, 0m, 0m, autoConfirmOnDeposit: true);
        var off = LodgingAmounts.OnlinePaymentNote(LodgingReservationStatus.Pending, 2000m, 0m, 7450m, 0m, 0m, autoConfirmOnDeposit: false);
        Assert.Contains("ยืนยันการจองอัตโนมัติ", on);
        Assert.Contains("ไม่ได้ยืนยันอัตโนมัติ", off);
    }

    [Fact]
    public void ไม่มีอะไรให้จ่าย_ไม่มีข้อความ_และสถานะอื่นป้ายเดิม()
    {
        Assert.Null(LodgingAmounts.OnlinePaymentNote(LodgingReservationStatus.CheckedOut, 0m, 0m, 7450m, 0m, 7450m, true));
        Assert.Null(LodgingAmounts.OnlinePaymentNote(LodgingReservationStatus.Confirmed, 2000m, 2000m, 7450m, 0m, 7450m, true));
        Assert.Equal("ยืนยันแล้ว", LodgingAmounts.StatusLabel(LodgingReservationStatus.Confirmed, 2000m, 2000m));
        Assert.Equal("รอชำระมัดจำ", LodgingAmounts.StatusLabel(LodgingReservationStatus.Pending, 0m, 0m));
    }
}
