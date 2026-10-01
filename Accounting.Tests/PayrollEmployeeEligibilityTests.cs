using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "พนักงานคนนี้อยู่ในงวดเงินเดือนนี้ไหม" — ตัวตั้งตัวเดียวของเส้นคำนวณ · ➕ เพิ่มพนักงานเข้ารอบ · รายชื่อที่เพิ่มได้
/// (รอบ 200 ทีม PR1 · ย้ายจาก inline ใน CalculatePayrollAsync)
///
/// สองทิศ: คนที่ต้องอยู่ในงวด (เริ่มงานกลางงวด · ลาออกกลางงวด D-S2) ต้องผ่าน · คนนอกงวดต้องไม่ผ่านพร้อมเหตุผล ·
/// และ <see cref="PayrollEmployeeEligibility.Reason"/> ต้องตัดสินตรงกับ expression ที่ EF ใช้ทุกกรณี
/// </summary>
public class PayrollEmployeeEligibilityTests
{
    private static readonly DateTime Start = new(2026, 3, 1);
    private static readonly DateTime End = new(2026, 3, 31);

    private static Employee Emp(DateTime start, DateTime? end = null, bool active = true)
        => new() { StartDate = start, EndDate = end, IsActive = active, FirstNameTh = "ทดสอบ" };

    private static bool InPeriod(Employee e)
        => PayrollEmployeeEligibility.InPeriod(Start, End).Compile()(e);

    [Fact]
    public void เริ่มงานกลางงวด_มีสิทธิ์()
    {
        var e = Emp(new DateTime(2026, 3, 16));
        Assert.True(InPeriod(e));
        Assert.Null(PayrollEmployeeEligibility.Reason(e, Start, End));
    }

    [Fact]
    public void เริ่มงานวันสิ้นงวดพอดี_มีสิทธิ์()
    {
        var e = Emp(End);
        Assert.True(InPeriod(e));
        Assert.Null(PayrollEmployeeEligibility.Reason(e, Start, End));
    }

    [Fact]
    public void เริ่มงานหลังสิ้นงวด_ไม่มีสิทธิ์พร้อมเหตุผล()
    {
        var e = Emp(new DateTime(2026, 4, 1));
        Assert.False(InPeriod(e));
        var reason = PayrollEmployeeEligibility.Reason(e, Start, End);
        Assert.NotNull(reason);
        Assert.Contains("01/04/2026", reason);
        Assert.Contains("หลังวันสิ้นงวด", reason);
    }

    [Fact]
    public void ลาออกกลางงวด_IsActiveเป็นเท็จ_ยังมีสิทธิ์งวดสุดท้าย_DS2()
    {
        // Terminate ตั้ง IsActive=false — ถ้าตัวตัดสินดูแค่ IsActive คนนี้จะหายจากรอบสุดท้ายเงียบ ๆ (D-S2)
        var e = Emp(new DateTime(2024, 1, 1), end: new DateTime(2026, 3, 15), active: false);
        Assert.True(InPeriod(e));
        Assert.Null(PayrollEmployeeEligibility.Reason(e, Start, End));
    }

    [Fact]
    public void พ้นสภาพก่อนเริ่มงวด_ไม่มีสิทธิ์พร้อมเหตุผล()
    {
        var e = Emp(new DateTime(2024, 1, 1), end: new DateTime(2026, 2, 28), active: false);
        Assert.False(InPeriod(e));
        var reason = PayrollEmployeeEligibility.Reason(e, Start, End);
        Assert.NotNull(reason);
        Assert.Contains("28/02/2026", reason);
        Assert.Contains("ก่อนวันเริ่มงวด", reason);
    }

    [Fact]
    public void ปิดใช้งานโดยไม่มีวันพ้นสภาพ_ไม่มีสิทธิ์พร้อมทางไปต่อ()
    {
        var e = Emp(new DateTime(2024, 1, 1), end: null, active: false);
        Assert.False(InPeriod(e));
        var reason = PayrollEmployeeEligibility.Reason(e, Start, End);
        Assert.NotNull(reason);
        Assert.Contains("วันพ้นสภาพ", reason);
    }

    [Fact]
    public void พนักงานปกติทั้งงวด_มีสิทธิ์()
    {
        var e = Emp(new DateTime(2020, 5, 1));
        Assert.True(InPeriod(e));
        Assert.Null(PayrollEmployeeEligibility.Reason(e, Start, End));
    }

    /// <summary>expression (EF) กับ Reason (ข้อความ) ต้องตัดสินตรงกันทุกกรณี — ไล่ตารางวันเริ่ม × วันพ้นสภาพ × active</summary>
    [Fact]
    public void ReasonตรงกับInPeriodทุกกรณี()
    {
        var starts = new[] { new DateTime(2026, 2, 1), Start, new DateTime(2026, 3, 15), End, new DateTime(2026, 4, 1) };
        var ends = new DateTime?[] { null, new DateTime(2026, 2, 28), Start, new DateTime(2026, 3, 20), End, new DateTime(2026, 5, 1) };
        foreach (var s in starts)
        foreach (var en in ends)
        foreach (var active in new[] { true, false })
        {
            var e = Emp(s, en, active);
            Assert.Equal(InPeriod(e), PayrollEmployeeEligibility.Reason(e, Start, End) == null);
        }
    }
}
