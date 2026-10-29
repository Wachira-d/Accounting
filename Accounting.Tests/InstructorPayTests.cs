using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 209 · P5 (R-209-C6 · R-209-D8 · คำตัดสินข้อ 168 ฏ) — ค่าตอบแทนผู้สอน (ต่อคลาส/ต่อหัว/%) · มูลค่าการจองด้วยบัตรคอร์ส = รายได้ที่รับรู้ (ไม่ใช่ 0) ·
/// ฐานคอมมิชชันการจองไม่นับซ้ำบิล POS · ทิศตรงข้าม: การจองปกติ/รายงานเดิมได้ตัวเลขเดิม
/// </summary>
public class InstructorPayTests
{
    private static readonly Guid Svc = Guid.NewGuid();
    private static readonly DateTime Day = new(2026, 11, 5);

    private static InstructorWorkBooking W(BookingStatus status, int heads = 1, decimal value = 0m, decimal share = 100m, string start = "18:00",
        bool pos = false, DateTime? day = null)
        => new(Guid.NewGuid(), Svc, day ?? Day, start, status, heads, value, share, pos);

    private static InstructorPayLine Pay(InstructorPayMode mode, decimal rate, IReadOnlyList<InstructorWorkBooking> work, decimal min = 0m)
        => InstructorPayRule.Compute(Guid.NewGuid(), "ครูเอ", null, new InstructorPaySettings(mode, rate, min), work);

    [Fact]
    public void ต่อหัว_8คนคูณ50_เท่ากับ400()
    {
        var work = Enumerable.Range(0, 8).Select(_ => W(BookingStatus.Completed)).ToList();
        var line = Pay(InstructorPayMode.PerHead, 50m, work);
        Assert.Equal(400m, line.Pay);
        Assert.Equal(8, line.Heads);
        Assert.Equal(1, line.Classes);
    }

    [Fact]
    public void ต่อหัว_นับจำนวนคนของการจองกลุ่ม_และไม่นับคนที่ไม่มา()
    {
        var work = new[] { W(BookingStatus.Completed, heads: 3), W(BookingStatus.NoShow, heads: 2), W(BookingStatus.Cancelled, heads: 4) };
        Assert.Equal(150m, Pay(InstructorPayMode.PerHead, 50m, work).Pay);
    }

    [Fact]
    public void ต่อหัว_มีขั้นต่ำต่อคลาส_มาน้อย_หรือมา0คนแต่คลาสเกิด_ได้ขั้นต่ำ()
    {
        Assert.Equal(300m, Pay(InstructorPayMode.PerHead, 50m, new[] { W(BookingStatus.Completed), W(BookingStatus.Completed) }, 300m).Pay);
        Assert.Equal(300m, Pay(InstructorPayMode.PerHead, 50m, new[] { W(BookingStatus.NoShow), W(BookingStatus.NoShow) }, 300m).Pay);
        Assert.Equal(500m, Pay(InstructorPayMode.PerHead, 50m, Enumerable.Range(0, 10).Select(_ => W(BookingStatus.Completed)).ToList(), 300m).Pay);
    }

    [Fact]
    public void ยกเลิกทั้งรอบ_หรือยังไม่ถึง_ไม่นับเป็นคลาส()
    {
        var work = new[] { W(BookingStatus.Cancelled), W(BookingStatus.Confirmed, start: "19:00") };
        var line = Pay(InstructorPayMode.PerClass, 500m, work);
        Assert.Equal(0, line.Classes);
        Assert.Equal(0m, line.Pay);
        Assert.Equal(0m, Pay(InstructorPayMode.PerHead, 50m, work, 300m).Pay);
    }

    [Fact]
    public void ต่อคลาส_สองรอบ_และแบ่งตามเปอร์เซ็นต์งาน()
    {
        var work = new[] { W(BookingStatus.Completed), W(BookingStatus.Completed), W(BookingStatus.Completed, start: "19:00") };
        Assert.Equal(1000m, Pay(InstructorPayMode.PerClass, 500m, work).Pay);
        var shared = new[] { W(BookingStatus.Completed, share: 60m) };
        Assert.Equal(300m, Pay(InstructorPayMode.PerClass, 500m, shared).Pay);
        Assert.Equal(30m, Pay(InstructorPayMode.PerHead, 50m, shared).Pay);
    }

    [Fact]
    public void เปอร์เซ็นต์รายได้_10เปอร์เซ็นต์ของ3000_เท่ากับ300_ไม่มาไม่นับรายได้()
    {
        var work = new[] { W(BookingStatus.Completed, value: 1000m), W(BookingStatus.Completed, value: 2000m), W(BookingStatus.NoShow, value: 500m) };
        var line = Pay(InstructorPayMode.PercentOfRevenue, 10m, work);
        Assert.Equal(3000m, line.RevenueShare);
        Assert.Equal(300m, line.Pay);
        Assert.Equal(0m, Pay(InstructorPayMode.None, 10m, work).Pay);
    }

    [Fact]
    public void มูลค่าการจองด้วยบัตร_เท่ากับรายได้ที่รับรู้_ไม่ใช่ศูนย์_การจองปกติเท่าเดิม()
    {
        // บัตร 10 ครั้ง 4,205.61 ⇒ ครั้งละ 420.56 (ยอดการจอง TotalAmount = 0)
        Assert.Equal(420.56m, BookingServiceValue.ForReport(0m, 420.56m));
        Assert.Equal(420.56m, BookingServiceValue.ForCommission(0m, 420.56m, 7m));
        // ทิศตรงข้าม: การจองปกติในรายงาน = ราคาการจองเดิม · คอมมิชชัน = ถอด VAT (ฐานเดียวกับ POS ก่อน VAT)
        Assert.Equal(1070m, BookingServiceValue.ForReport(1070m, null));
        Assert.Equal(1000m, BookingServiceValue.ForCommission(1070m, null, 7m));
        Assert.Equal(1070m, BookingServiceValue.ForCommission(1070m, null, 0m));
        Assert.Equal(934.58m, BookingServiceValue.ForCommission(1000m, null, 7m));
    }

    [Fact]
    public void ฐานคอมมิชชันการจอง_ไม่นับบิลPOSซ้ำ_ไม่นับไม่มา_แบ่งตามเปอร์เซ็นต์()
    {
        var work = new[]
        {
            W(BookingStatus.Completed, value: 1000m, share: 50m),            // นับ 500
            W(BookingStatus.Completed, value: 2000m, pos: true),             // ปิดที่ POS — นับในฐาน POS แล้ว
            W(BookingStatus.NoShow, value: 800m),                            // ไม่มา — ไม่มีรายได้
            W(BookingStatus.Completed, value: 420.56m),                      // จ่ายด้วยบัตร (มูลค่า = รายได้ที่รับรู้)
        };
        Assert.Equal(920.56m, BookingServiceValue.BookingSalesBasis(work));
    }

    [Fact]
    public void ตรวจค่าตั้ง()
    {
        Assert.Null(InstructorPayRule.SettingsProblem(InstructorPayMode.None, 0m, 0m));
        Assert.Null(InstructorPayRule.SettingsProblem(InstructorPayMode.PerHead, 50m, 300m));
        Assert.NotNull(InstructorPayRule.SettingsProblem(InstructorPayMode.PercentOfRevenue, 101m, 0m));
        Assert.NotNull(InstructorPayRule.SettingsProblem(InstructorPayMode.PerClass, -1m, 0m));
        Assert.NotNull(InstructorPayRule.SettingsProblem(InstructorPayMode.PerClass, 500m, 100m));
        Assert.NotNull(InstructorPayRule.SettingsProblem(InstructorPayMode.PerClass, 0m, 0m));
    }

    [Fact]
    public void รายงานผู้ให้บริการ_การจองด้วยบัตรได้รายได้ที่รับรู้_และติดป้ายบัตร()
    {
        var rid = Guid.NewGuid();
        var providers = new List<(Guid, string, decimal)> { (rid, "ครูบี", 100m) };
        var rows = new List<BookingProviderReportRow>
        {
            new(Guid.NewGuid(), "BK-1", Day, "18:00", "19:00", 60, BookingStatus.Completed, Svc, "โยคะ", "ก", BookingServiceValue.ForReport(0m, 420.56m), providers, true),
            new(Guid.NewGuid(), "BK-2", Day, "18:00", "19:00", 60, BookingStatus.Completed, Svc, "โยคะ", "ข", BookingServiceValue.ForReport(500m, null), providers),
        };
        var (list, totals) = BookingProviderReport.Build(rows);
        Assert.Equal(920.56m, totals.Revenue);
        Assert.Equal(920.56m, list[0].RevenueShare);
        Assert.True(list[0].Jobs.Single(j => j.BookingNumber == "BK-1").PaidWithCredit);
        Assert.False(list[0].Jobs.Single(j => j.BookingNumber == "BK-2").PaidWithCredit);
    }
}
