using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 202 ทีม LO — night audit ไม่ประทับสถานะปลายทาง (O-P0-2 · คำตัดสินข้อ 119 · R1) · "ค้างปิด" + ทางปิดของแถวรุ่นเก่า</summary>
public class LodgingOverdueRuleTests
{
    private static readonly DateTime Today = new(2026, 10, 2);

    [Theory]
    [InlineData(LodgingReservationStatus.CheckedIn, LodgingOverdueKind.StayNotCheckedOut)]
    [InlineData(LodgingReservationStatus.Confirmed, LodgingOverdueKind.ArrivalNotRecorded)]
    [InlineData(LodgingReservationStatus.Pending, LodgingOverdueKind.ArrivalNotRecorded)]
    [InlineData(LodgingReservationStatus.CheckedOut, LodgingOverdueKind.None)]
    [InlineData(LodgingReservationStatus.Cancelled, LodgingOverdueKind.None)]
    [InlineData(LodgingReservationStatus.NoShow, LodgingOverdueKind.None)]
    public void เลยวันออกแล้ว_จัดชนิดค้างตามสถานะ(LodgingReservationStatus st, LodgingOverdueKind expected)
        => Assert.Equal(expected, LodgingOverdueRule.Classify(st, Today.AddDays(-1), Today));

    [Fact]
    public void วันออกวันนี้หรืออนาคต_ไม่ค้าง()
    {
        Assert.Equal(LodgingOverdueKind.None, LodgingOverdueRule.Classify(LodgingReservationStatus.CheckedIn, Today, Today));
        Assert.Equal(LodgingOverdueKind.None, LodgingOverdueRule.Classify(LodgingReservationStatus.Confirmed, Today.AddDays(3), Today));
        Assert.Null(LodgingOverdueRule.Label(LodgingOverdueKind.None));
    }

    [Fact]
    public void ติดธงเมื่อเกินระยะผ่อนและยังไม่เคยติด_ครั้งเดียว()
    {
        var co = Today.AddDays(-(LodgingOverdueRule.FlagGraceDays + 1));
        Assert.True(LodgingOverdueRule.ShouldFlag(LodgingOverdueKind.StayNotCheckedOut, co, Today, null));
        Assert.False(LodgingOverdueRule.ShouldFlag(LodgingOverdueKind.StayNotCheckedOut, co, Today, Today.AddDays(-1)));   // ติดแล้ว ไม่ซ้ำ
        Assert.False(LodgingOverdueRule.ShouldFlag(LodgingOverdueKind.StayNotCheckedOut, Today.AddDays(-LodgingOverdueRule.FlagGraceDays), Today, null));   // ยังในระยะผ่อน
        Assert.False(LodgingOverdueRule.ShouldFlag(LodgingOverdueKind.None, co, Today, null));
    }

    [Fact]
    public void หมายเหตุบอกทางไปต่อด้วยเส้นปกติ_และบอกว่าระบบไม่ปิดเอง()
    {
        var stay = LodgingOverdueRule.FlagNote(LodgingOverdueKind.StayNotCheckedOut, Today.AddDays(-5));
        var arr = LodgingOverdueRule.FlagNote(LodgingOverdueKind.ArrivalNotRecorded, Today.AddDays(-5));
        Assert.Contains("เช็คเอาต์ + ออกบิล", stay);
        Assert.Contains("No-show", arr);
        Assert.Contains("ระบบไม่ปิดสถานะเอง", stay);
        Assert.Contains("ระบบไม่ปิดสถานะเอง", arr);
    }

    // ── แถวที่ night audit รุ่นก่อนรอบ 202 ประทับไปแล้ว ──

    private const string OldJobNote = "[01/09/2026 06:00] ระบบปิดการเข้าพักอัตโนมัติ (เลยวันเช็คเอาต์ 28/08/2026 เกิน 2 วัน) — **ยังไม่ได้ออกบิล** กรุณาออกเอกสารย้อนหลังที่หน้ารายละเอียดการจอง";

    [Fact]
    public void แถวรุ่นเก่าปิดเองยังไม่ออกบิล_ออกใบย้อนหลังได้()
        => Assert.True(LodgingOverdueRule.IsLegacyAutoCheckout(LodgingReservationStatus.CheckedOut, null, OldJobNote, accountingOff: false));

    [Fact]
    public void ทิศตรงข้าม_ออกบิลแล้ว_หรือเช็คเอาต์โดยคน_หรือโหมดไม่ออกเอกสาร_ไม่เปิดทาง()
    {
        Assert.False(LodgingOverdueRule.IsLegacyAutoCheckout(LodgingReservationStatus.CheckedOut, Guid.NewGuid(), OldJobNote, false));
        Assert.False(LodgingOverdueRule.IsLegacyAutoCheckout(LodgingReservationStatus.CheckedOut, null, "[..] เช็คเอาต์แบบไม่ออกเอกสาร", false));
        Assert.False(LodgingOverdueRule.IsLegacyAutoCheckout(LodgingReservationStatus.CheckedOut, null, OldJobNote, accountingOff: true));
        Assert.False(LodgingOverdueRule.IsLegacyAutoCheckout(LodgingReservationStatus.CheckedIn, null, OldJobNote, false));
        Assert.False(LodgingOverdueRule.IsLegacyAutoCheckout(LodgingReservationStatus.CheckedOut, null, null, false));
    }

    [Fact]
    public void เปิดกลับจากแถวรุ่นเก่า_รู้จากเช็คอินที่มีเวลาเช็คเอาต์()
    {
        Assert.True(LodgingOverdueRule.IsReopenedLegacy(LodgingReservationStatus.CheckedIn, Today.AddDays(-30)));
        Assert.False(LodgingOverdueRule.IsReopenedLegacy(LodgingReservationStatus.CheckedIn, null));          // เช็คอินปกติ
        Assert.False(LodgingOverdueRule.IsReopenedLegacy(LodgingReservationStatus.CheckedOut, Today));        // เช็คเอาต์ปกติ
    }

    [Fact]
    public void noshowรุ่นเก่ายังไม่คิดค่าปรับ_คิดได้ครั้งเดียว()
    {
        Assert.True(LodgingOverdueRule.IsLegacyAutoNoShow(LodgingReservationStatus.NoShow, LodgingOverdueRule.LegacyAutoNoShowReason, 0m, 0m));
        // คิดแล้ว (เหตุผลถูกเขียนทับ / มีค่าปรับ / มียอดคืน) ⇒ ไม่เปิดซ้ำ
        Assert.False(LodgingOverdueRule.IsLegacyAutoNoShow(LodgingReservationStatus.NoShow, "ไม่มาเข้าพัก", 0m, 0m));
        Assert.False(LodgingOverdueRule.IsLegacyAutoNoShow(LodgingReservationStatus.NoShow, LodgingOverdueRule.LegacyAutoNoShowReason, 500m, 0m));
        Assert.False(LodgingOverdueRule.IsLegacyAutoNoShow(LodgingReservationStatus.NoShow, LodgingOverdueRule.LegacyAutoNoShowReason, 0m, 300m));
        Assert.False(LodgingOverdueRule.IsLegacyAutoNoShow(LodgingReservationStatus.Cancelled, LodgingOverdueRule.LegacyAutoNoShowReason, 0m, 0m));
    }

    [Fact]
    public void ข้อความตัวระบุตรงกับที่jobรุ่นเก่าเขียนจริง()
    {
        // ล็อกกับข้อความของ LodgingNightAuditJob ก่อนรอบ 202 (git show 3a9e1b65:Accounting/Services/Background/LodgingNightAuditJob.cs)
        Assert.Contains(LodgingOverdueRule.LegacyAutoCheckoutMarker, OldJobNote);
        Assert.Equal("ระบบบันทึกอัตโนมัติ: ไม่มาเข้าพักและไม่มีการเช็คอิน", LodgingOverdueRule.LegacyAutoNoShowReason);
    }

    // ── ฝ่ายค้านรอบ 202 ──

    [Theory]
    [InlineData(LodgingReservationStatus.CheckedIn, 0, true)]
    [InlineData(LodgingReservationStatus.Confirmed, 0, true)]
    [InlineData(LodgingReservationStatus.Pending, 500, true)]
    [InlineData(LodgingReservationStatus.Pending, 0, false)]   // P1-2: จองแล้วไม่จ่าย = ไม่ถูกคิด (เหมือนยกเลิกฟรี)
    public void P1_2_มิเตอร์ตอนติดธง_นับเฉพาะพักจริง_ยืนยัน_หรือมีมัดจำ(LodgingReservationStatus st, int paid, bool expected)
        => Assert.Equal(expected, LodgingOverdueRule.ShouldMeterOnFlag(st, paid));

    [Fact]
    public void P1_4_เดือนยื่นแล้ว_ไม่มีคนรับทราบ_ปฏิเสธ()
        => Assert.NotNull(LodgingOverdueRule.BackfillVatAckProblem(periodFiled: true, acknowledged: false, Today.AddDays(-40)));

    [Fact]
    public void P1_4_ทิศตรงข้าม_รับทราบแล้ว_หรือเดือนยังไม่ยื่น_ไปต่อได้()
    {
        Assert.Null(LodgingOverdueRule.BackfillVatAckProblem(periodFiled: true, acknowledged: true, Today.AddDays(-40)));
        Assert.Null(LodgingOverdueRule.BackfillVatAckProblem(periodFiled: false, acknowledged: false, Today.AddDays(-5)));
    }
}
