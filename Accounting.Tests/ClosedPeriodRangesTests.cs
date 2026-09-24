using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ขอบเขตคิวรอตรวจ = ทุกใบในงวดบัญชีที่ยังไม่ปิด (คำตัดสินเจ้าของ รอบ 193 ข้อ 32)
/// ครึ่งที่ 1: ใบในงวด Closed/Locked ถูกตัดออก (รวมวันสุดท้ายของงวดที่มีเวลา) — ทดสอบผ่านตัวกรองนิพจน์ตัวเดียวกับที่ส่งให้ EF
/// ครึ่งที่ 2: งวดที่เปิด · วันที่นอกทุกงวด · บริษัทที่ยังไม่เคยปิดงวด → อยู่ในคิวทั้งหมด (เก่ากว่า 30 วันก็ยังอยู่)
/// </summary>
public class ClosedPeriodRangesTests
{
    private sealed record Row(DateTime Date);

    /// <summary>ถามผ่านตัวกรองจริงที่ ActiveLearningRanker ส่งให้ EF (compile แล้วรันกับวัตถุ)</summary>
    private static bool IsClosed(DateTime date, IReadOnlyList<ClosedDateRange> ranges)
        => !ClosedPeriodRanges.NotInClosed<Row>(r => r.Date, ranges).Compile()(new Row(date));

    private static (DateTime, DateTime, FiscalPeriodStatus) Month(int y, int m, FiscalPeriodStatus st)
    {
        var start = new DateTime(y, m, 1);
        return (start, start.AddMonths(1).AddDays(-1), st);   // รูปเดียวกับ AccountingService (EndDate = เที่ยงคืนวันสุดท้าย)
    }

    [Fact]
    public void งวดปิดติดกันยุบเป็นช่วงเดียว_งวดเปิดไม่นับ()
    {
        var ranges = ClosedPeriodRanges.Build(new[]
        {
            Month(2026, 1, FiscalPeriodStatus.Locked),
            Month(2026, 2, FiscalPeriodStatus.Closed),
            Month(2026, 3, FiscalPeriodStatus.Closed),
            Month(2026, 4, FiscalPeriodStatus.Open),
            Month(2026, 6, FiscalPeriodStatus.Closed),
        });
        Assert.Equal(2, ranges.Count);
        Assert.Equal(new DateTime(2026, 1, 1), ranges[0].Start);
        Assert.Equal(new DateTime(2026, 4, 1), ranges[0].EndExclusive);
        Assert.Equal(new DateTime(2026, 6, 1), ranges[1].Start);
        Assert.Equal(new DateTime(2026, 7, 1), ranges[1].EndExclusive);
    }

    [Fact]
    public void วันสุดท้ายของงวดที่มีเวลา_ยังอยู่ในงวดที่ปิด()
    {
        var ranges = ClosedPeriodRanges.Build(new[] { Month(2026, 8, FiscalPeriodStatus.Closed) });
        Assert.True(IsClosed(new DateTime(2026, 8, 31, 15, 0, 0), ranges));
        Assert.True(IsClosed(new DateTime(2026, 8, 1), ranges));
        Assert.False(IsClosed(new DateTime(2026, 9, 1), ranges));
        Assert.False(IsClosed(new DateTime(2026, 7, 31, 23, 59, 59), ranges));
    }

    [Fact]
    public void ยังไม่เคยปิดงวด_ทุกวันที่อยู่ในขอบเขต_แม้เก่ากว่าสามสิบวัน()
    {
        var ranges = ClosedPeriodRanges.Build(Array.Empty<(DateTime, DateTime, FiscalPeriodStatus)>());
        Assert.Empty(ranges);
        Assert.False(IsClosed(new DateTime(2024, 1, 15), ranges));
        Assert.False(IsClosed(DateTime.UtcNow.AddDays(-120), ranges));
    }

    [Fact]
    public void งวดที่เปิดอยู่และวันที่นอกทุกงวด_ไม่ถูกตัด()
    {
        var ranges = ClosedPeriodRanges.Build(new[]
        {
            Month(2026, 1, FiscalPeriodStatus.Closed),
            Month(2026, 2, FiscalPeriodStatus.Open),
        });
        Assert.False(IsClosed(new DateTime(2026, 2, 10), ranges));   // งวดเปิด
        Assert.False(IsClosed(new DateTime(2025, 12, 31), ranges));  // ไม่มีแถวงวด = เปิด
        Assert.True(IsClosed(new DateTime(2026, 1, 10), ranges));
    }

    [Fact]
    public void ตัวกรองแบบนิพจน์_ตัดเฉพาะแถวในงวดที่ปิด()
    {
        var ranges = ClosedPeriodRanges.Build(new[]
        {
            Month(2026, 1, FiscalPeriodStatus.Closed),
            Month(2026, 3, FiscalPeriodStatus.Locked),
        });
        var rows = new[]
        {
            new Row(new DateTime(2025, 12, 31)), new Row(new DateTime(2026, 1, 1)), new Row(new DateTime(2026, 1, 31, 18, 0, 0)),
            new Row(new DateTime(2026, 2, 1)), new Row(new DateTime(2026, 3, 15)), new Row(new DateTime(2026, 4, 1)),
        };
        var filter = ClosedPeriodRanges.NotInClosed<Row>(r => r.Date, ranges).Compile();
        var kept = rows.Where(filter).Select(r => r.Date).ToList();
        Assert.Equal(new[] { new DateTime(2025, 12, 31), new DateTime(2026, 2, 1), new DateTime(2026, 4, 1) }, kept);
    }

    [Fact]
    public void ไม่มีงวดปิด_ตัวกรองแบบนิพจน์ผ่านทุกแถว()
    {
        var filter = ClosedPeriodRanges.NotInClosed<Row>(r => r.Date, Array.Empty<ClosedDateRange>()).Compile();
        Assert.True(filter(new Row(new DateTime(2020, 1, 1))));
    }
}
