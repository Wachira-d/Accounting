using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 209 · P5 (R-209-C7) — รายงานคลาส: fill-rate (ที่นั่งที่จอง ÷ ความจุ) · no-show rate · คีย์รอบ บริการ+วัน+เวลา · รอบว่างจากตารางรอบจริง ·
/// ความจุไม่รู้ = null (ไม่แต่งตัวเลข) · retention ช่วงนี้เทียบช่วงก่อน · เสี่ยงหาย
/// </summary>
public class ClassFillRateReportTests
{
    private static readonly Guid Yoga = Guid.NewGuid();
    private static readonly Guid Spin = Guid.NewGuid();
    private static readonly Guid TeacherA = Guid.NewGuid();
    private static readonly DateTime Day = new(2026, 11, 5);

    private static ClassBookingInput B(ClassAttendance status, int seats = 1, Guid? svc = null, string start = "18:00", int capacity = 10,
        string? contact = null, Guid? teacher = null)
        => new("เว็บ", svc ?? Yoga, svc == Spin ? "Spin" : "Yoga", Day, start, capacity, seats, status, contact ?? Guid.NewGuid().ToString(),
            teacher is Guid t ? new List<(Guid, string)> { (t, "ครูเอ") } : new List<(Guid, string)>());

    [Fact]
    public void ความจุ10_จอง6_fill60_noShow20()
    {
        var rows = new List<ClassBookingInput>
        {
            B(ClassAttendance.Attended), B(ClassAttendance.Attended), B(ClassAttendance.Attended), B(ClassAttendance.Attended),
            B(ClassAttendance.NoShow), B(ClassAttendance.Upcoming), B(ClassAttendance.Cancelled), B(ClassAttendance.Cancelled),
        };
        var (classes, byService, _, totals) = ClassFillRateReport.Build(rows);
        var c = Assert.Single(classes);
        Assert.Equal(6, c.Booked);
        Assert.Equal(2, c.Cancelled);
        Assert.Equal(60m, c.FillRatePercent);
        Assert.Equal(20m, c.NoShowRatePercent);
        Assert.Equal(60m, Assert.Single(byService).FillRatePercent);
        Assert.Equal(1, totals.Classes);
    }

    [Fact]
    public void บริการนับคน_ที่นั่งตามจำนวนคน()
    {
        var (classes, _, _, _) = ClassFillRateReport.Build(new[] { B(ClassAttendance.Attended, seats: 3), B(ClassAttendance.Upcoming, seats: 2) });
        Assert.Equal(50m, Assert.Single(classes).FillRatePercent);
    }

    [Fact]
    public void คีย์รอบ_บริการวันเวลา_แยกคลาส_และรวมกลุ่มต่อบริการ()
    {
        var rows = new[]
        {
            B(ClassAttendance.Attended), B(ClassAttendance.Attended, start: "19:00"),
            B(ClassAttendance.Attended, svc: Spin, capacity: 4), B(ClassAttendance.Attended, svc: Spin, capacity: 4),
        };
        var (classes, byService, _, totals) = ClassFillRateReport.Build(rows);
        Assert.Equal(3, classes.Count);
        Assert.Equal(3, totals.Classes);
        var spin = byService.Single(s => s.Name == "Spin");
        Assert.Equal(50m, spin.FillRatePercent);
        var yoga = byService.Single(s => s.Name == "Yoga");
        Assert.Equal(2, yoga.Classes);
        Assert.Equal(10m, yoga.FillRatePercent);   // 2 ที่ ÷ 20
        Assert.Equal(16.67m, totals.FillRatePercent);   // 4 ÷ 24
    }

    [Fact]
    public void ตารางรอบจริง_รอบว่างนับ0เปอร์เซ็นต์_รอบที่ยกเลิกไม่นับในค่าเฉลี่ย()
    {
        var sessions = new[]
        {
            new ClassSessionInput("คอร์ส", null, "Pilates", Day, "09:00", 8, false, new List<(Guid, string)> { (TeacherA, "ครูเอ") }),
            new ClassSessionInput("คอร์ส", null, "Pilates", Day, "10:00", 8, true, new List<(Guid, string)>()),
        };
        var bookings = new[] { new ClassBookingInput("คอร์ส", null, "Pilates", Day, "10:00", 8, 1, ClassAttendance.Cancelled, "k:1", new List<(Guid, string)>()) };
        var (classes, _, byInstructor, totals) = ClassFillRateReport.Build(bookings, sessions);
        Assert.Equal(2, classes.Count);
        Assert.Equal(0m, classes[0].FillRatePercent);
        Assert.True(classes[1].SessionCancelled);
        Assert.Null(classes[1].FillRatePercent);
        Assert.Equal(1, totals.Classes);
        Assert.Equal(0m, totals.FillRatePercent);
        Assert.Equal("ครูเอ", Assert.Single(byInstructor).Name);
    }

    [Fact]
    public void ความจุไม่รู้_fillเป็นnull_ไม่มีคนมา_noShowเป็นnull()
    {
        var (classes, _, _, totals) = ClassFillRateReport.Build(new[] { B(ClassAttendance.Upcoming, capacity: 0) });
        Assert.Null(Assert.Single(classes).FillRatePercent);
        Assert.Null(classes[0].NoShowRatePercent);
        Assert.Null(totals.FillRatePercent);
    }

    [Fact]
    public void แยกตามผู้สอน_และแถวยังไม่ระบุอยู่ท้าย()
    {
        var rows = new[] { B(ClassAttendance.Attended, teacher: TeacherA), B(ClassAttendance.Attended, svc: Spin, capacity: 4) };
        var (_, _, byInstructor, _) = ClassFillRateReport.Build(rows);
        Assert.Equal(2, byInstructor.Count);
        Assert.Equal("ครูเอ", byInstructor[0].Name);
        Assert.Equal("ยังไม่ระบุผู้สอน", byInstructor[1].Name);
    }

    [Fact]
    public void Retention_คงอยู่_หาย_ใหม่_และเสี่ยงหาย()
    {
        var from = new DateTime(2026, 11, 1); var to = new DateTime(2026, 11, 30);   // ช่วงก่อน = 2 ต.ค. – 31 ต.ค. (30 วัน)
        var acts = new[]
        {
            new MemberActivity("A", "เอ", new DateTime(2026, 10, 10)), new MemberActivity("A", "เอ", new DateTime(2026, 11, 3)),
            new MemberActivity("B", "บี", new DateTime(2026, 11, 20)),
            new MemberActivity("C", "ซี", new DateTime(2026, 11, 28)),
            new MemberActivity("D", "ดี", new DateTime(2026, 10, 15)),
            new MemberActivity("E", "อี", new DateTime(2026, 9, 1)),    // นอกทั้งสองช่วง
        };
        var r = ClassFillRateReport.Retention(acts, from, to, 20);
        Assert.Equal(3, r.ActiveThisPeriod);
        Assert.Equal(2, r.ActiveLastPeriod);
        Assert.Equal(1, r.Retained);
        Assert.Equal(1, r.Churned);
        Assert.Equal(2, r.New);
        Assert.Equal(50m, r.RetentionRatePercent);
        Assert.Equal(new[] { "D", "A" }, r.AtRisk.Select(m => m.ContactKey).ToArray());   // ห่าง 46 และ 27 วัน
        Assert.Equal(46, r.AtRisk[0].DaysSince);
    }

    [Fact]
    public void วันที่เหลือก่อนหมดอายุ()
    {
        Assert.Equal(0, ClassFillRateReport.DaysLeft(new DateTime(2026, 11, 5), new DateTime(2026, 11, 5)));
        Assert.Equal(7, ClassFillRateReport.DaysLeft(new DateTime(2026, 11, 12), new DateTime(2026, 11, 5)));
        Assert.Null(ClassFillRateReport.Rate(1, 0));
        Assert.Equal(33.33m, ClassFillRateReport.Rate(1, 3));
    }
}
