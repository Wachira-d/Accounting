using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กฎที่ปฏิทินนำส่งบนแดชบอร์ดใช้ตัดสิน — กำหนดยื่นต่อแบบ และ "แบบไหนต้องยื่น
/// ทุกเดือนแม้ยอด 0".
///
/// สองเรื่องนี้พลาดแล้วเสียเงินจริง:
///   • วันครบกำหนดคลาดไป 1 วัน → เตือนช้า → เบี้ยปรับ/เงินเพิ่ม
///   • ตกหล่นข้อ "ยื่นแบบเปล่า" → ผู้ใช้เห็นจอว่างแล้วไม่ยื่น → ปรับอาญา
///     (ภ.พ.30 §83 ยื่นทุกเดือนแม้ไม่มีรายรับ; สปส.1-10 ทุกเดือนแม้ไม่มีค่าจ้าง)
/// </summary>
public class FilingCalendarRulesTests
{
    // ── กำหนดยื่น (เดือนถัดจากงวด) ──────────────────────────────────

    [Fact]
    public void Sso_is_due_on_the_15th_of_the_following_month_for_both_channels()
    {
        var (paper, efiling) = StatutoryRemittanceService.DueDates("SsoSps110", 2026, 7);
        Assert.Equal(new DateTime(2026, 8, 15), paper);
        Assert.Equal(new DateTime(2026, 8, 15), efiling);   // ปกส. ไม่มีส่วนขยาย e-Filing
    }

    [Fact]
    public void Vat_paper_is_the_15th_and_efiling_gets_eight_extra_days()
    {
        var (paper, efiling) = StatutoryRemittanceService.DueDates("VatPp30", 2026, 7);
        Assert.Equal(new DateTime(2026, 8, 15), paper);
        Assert.Equal(new DateTime(2026, 8, 23), efiling);
    }

    [Theory]
    [InlineData("WhtPnd1")]
    [InlineData("WhtPnd3")]
    [InlineData("WhtPnd53")]
    public void Withholding_forms_are_due_on_the_7th_paper_and_15th_efiling(string type)
    {
        var (paper, efiling) = StatutoryRemittanceService.DueDates(type, 2026, 7);
        Assert.Equal(new DateTime(2026, 8, 7), paper);
        Assert.Equal(new DateTime(2026, 8, 15), efiling);
    }

    [Fact]
    public void December_period_rolls_into_january_of_the_next_year()
    {
        var (paper, efiling) = StatutoryRemittanceService.DueDates("VatPp30", 2026, 12);
        Assert.Equal(new DateTime(2027, 1, 15), paper);
        Assert.Equal(new DateTime(2027, 1, 23), efiling);
    }

    // ── ต้องยื่นทุกเดือนแม้ยอด 0 หรือไม่ ────────────────────────────

    [Theory]
    [InlineData("VatPp30")]    // §83 — จด VAT แล้วต้องยื่นทุกเดือนแม้ไม่มีรายรับ
    [InlineData("SsoSps110")]  // นายจ้างขึ้นทะเบียนแล้วต้องยื่นทุกเดือน
    [InlineData("WhtPnd1")]    // จ่ายเงินเดือนแล้วต้องยื่นแม้ภาษีหัก = 0
    public void Nil_returns_are_still_mandatory_for_monthly_forms(string type)
    {
        var (always, legal) = StatutoryRemittanceService.FilingRule(type);
        Assert.True(always);
        Assert.False(string.IsNullOrWhiteSpace(legal));   // ต้องอ้างกฎให้ผู้ใช้ตรวจได้
    }

    [Theory]
    [InlineData("WhtPnd3")]    // ไม่มีการหัก = ไม่ต้องยื่น
    [InlineData("WhtPnd53")]
    [InlineData("VatPp36")]    // ไม่ได้จ่ายค่าบริการ ตปท. = ไม่ต้องยื่น
    public void Event_driven_forms_are_not_filed_on_empty_months(string type)
    {
        var (always, _) = StatutoryRemittanceService.FilingRule(type);
        Assert.False(always);
    }
}
