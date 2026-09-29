using Accounting.Helpers;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 200 ทีม R · D-04 — หน้าเว็บ preview กำหนดนำส่ง/เงินเพิ่ม สปส. ผ่าน <see cref="SsoLateFee"/> ตัวเดียวกับตอนลงบัญชี
/// สองทิศ: วันครบกำหนดที่ตรงวันหยุดถูกเลื่อน (หน้าเว็บเดิมขึ้น "เกินกำหนด" ผิด) · ตัวห่อเดิม ComputeSsoLateFee ได้ผลเท่าเดิมทุกวัน</summary>
public class SsoLateFeePreviewTests
{
    private const decimal Total = 10_000m;

    [Fact]
    public void วันที่15ตรงวันอาทิตย์_ครบกำหนดเลื่อนเป็นวันจันทร์_ไม่มีเงินเพิ่ม()
    {
        var r = SsoLateFee.Compute(2026, 2, new DateTime(2026, 3, 16), Total);
        Assert.Equal(new DateTime(2026, 3, 16), r.DueDate);
        Assert.Equal(0m, r.Fee);
        Assert.Equal(0, r.MonthsLate);
    }

    [Fact]
    public void ช้า30วันข้ามเดือน31วัน_ได้1เดือน_ไม่ใช่สูตรหน้าเว็บเดิม()
    {
        // หน้าเว็บเดิม: new Date(2026, 2, 15) = 15 มี.ค. · จ่าย 16 เม.ย. = 32 วัน ⇒ ceil(32/30) = 2 เดือน (400)
        var r = SsoLateFee.Compute(2026, 2, new DateTime(2026, 4, 16), Total);
        Assert.Equal(1, r.MonthsLate);
        Assert.Equal(200m, r.Fee);
        Assert.Equal(31, r.DaysLate);
    }

    [Fact]
    public void ตัวห่อเดิม_ComputeSsoLateFee_ได้ผลเท่าตัวใหม่ทุกวันตลอดปี()
    {
        for (var d = new DateTime(2026, 3, 1); d < new DateTime(2027, 3, 1); d = d.AddDays(1))
            Assert.Equal(SsoLateFee.Compute(2026, 2, d, Total).Fee, PayrollService.ComputeSsoLateFee(2026, 2, d, Total));
    }
}
