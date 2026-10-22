using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รายการพิเศษ (Quick Add) บนบิล POS + ตัวตัดสิน "ดึงคิวการจองมาเปิดบิลได้ไหม"
/// </summary>
public class PosCustomItemAndBookingIntakeTests
{
    // ─────────── รายการพิเศษ ───────────

    [Fact]
    public void รายการพิเศษ_ค่าว่างได้ค่ามาตรฐาน_บริการหน่วยครั้ง()
    {
        var s = PosCustomItemRule.Validate("  ค่าเดินทางนอกสถานที่ ", 300.005m, 1m, null, null);
        Assert.Equal("ค่าเดินทางนอกสถานที่", s.Name);
        Assert.Equal(PosCustomItemRule.KindService, s.Kind);
        Assert.Equal("ครั้ง", s.Unit);
        Assert.Equal(300.01m, s.UnitPrice);                   // AwayFromZero
    }

    [Fact]
    public void รายการพิเศษ_สินค้าไม่ตัดสต็อก_หน่วยชิ้น_รับชื่อชนิดไม่สนตัวพิมพ์()
    {
        var s = PosCustomItemRule.Validate("แจกันฝากขาย", 1500m, 2m, "goods", null);
        Assert.Equal(PosCustomItemRule.KindGoods, s.Kind);
        Assert.Equal("ชิ้น", s.Unit);
    }

    [Theory]
    [InlineData("", 10, 1, null)]
    [InlineData("ค่าแก้งาน", -1, 1, null)]
    [InlineData("ค่าแก้งาน", 10, 0, null)]
    [InlineData("ค่าแก้งาน", 10, 1, "Exempt")]
    public void รายการพิเศษ_ค่าผิด_โยนพร้อมข้อความ(string name, decimal price, decimal qty, string? kind)
    {
        var ex = Assert.Throws<BusinessRuleException>(() => PosCustomItemRule.Validate(name, price, qty, kind, null));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void รายการพิเศษราคา0ได้_เช่นของแถมที่ต้องลงสลิป()
        => Assert.Equal(0m, PosCustomItemRule.Validate("ของแถม", 0m, 1m, null, null).UnitPrice);

    // ─────────── คิวการจอง → เปิดบิล ───────────

    private static PosBookingFacts F(PosBookingState st, string? linked = null, string? billed = null, decimal dep = 0m,
        string src = PosBookingSource.CmsBooking) => new(src, st, linked, billed, dep);

    [Theory]
    [InlineData(PosBookingState.Pending)]
    [InlineData(PosBookingState.Confirmed)]
    [InlineData(PosBookingState.InProgress)]
    public void การจองที่ยังไม่จบ_เปิดบิลได้(PosBookingState st)
    {
        var d = PosBookingIntake.Decide(F(st));
        Assert.True(d.CanOpen);
        Assert.Null(d.BlockReason);
        Assert.Null(d.Warning);
    }

    [Theory]
    [InlineData(PosBookingState.Cancelled)]
    [InlineData(PosBookingState.NoShow)]
    [InlineData(PosBookingState.Completed)]
    public void การจองที่จบแล้ว_เปิดบิลไม่ได้(PosBookingState st)
    {
        var d = PosBookingIntake.Decide(F(st));
        Assert.False(d.CanOpen);
        Assert.NotNull(d.BlockReason);
    }

    [Fact]
    public void มีบิลที่ผูกอยู่แล้ว_เปิดซ้ำไม่ได้_และบอกเลขบิลเดิม()
    {
        var d = PosBookingIntake.Decide(F(PosBookingState.Confirmed, linked: "POS-202610-0042"));
        Assert.False(d.CanOpen);
        Assert.Contains("POS-202610-0042", d.BlockReason);
    }

    [Fact]
    public void ระบบจองออกเอกสารรายได้แล้ว_เปิดบิลไม่ได้_กันรายได้ซ้ำ()
    {
        var d = PosBookingIntake.Decide(F(PosBookingState.Confirmed, billed: "INV-2026-0007"));
        Assert.False(d.CanOpen);
        Assert.Contains("INV-2026-0007", d.BlockReason);
    }

    [Fact]
    public void มีมัดจำ_เปิดได้แต่ต้องเตือน_เพราะPOSยังไม่หักมัดจำ()
    {
        var d = PosBookingIntake.Decide(F(PosBookingState.Confirmed, dep: 500m, src: PosBookingSource.PosReservation));
        Assert.True(d.CanOpen);
        Assert.Contains("500.00", d.Warning);
    }

    [Fact]
    public void แหล่งไม่รู้จัก_เปิดไม่ได้()
        => Assert.False(PosBookingIntake.Decide(F(PosBookingState.Confirmed, src: "Lodging")).CanOpen);
}
