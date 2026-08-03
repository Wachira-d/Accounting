using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวตัดสิน "ดึงเอกสารเข้ารายงาน ภ.พ.30 งวดไหนได้บ้าง" — ใช้ร่วมกันทั้งรายการ
/// "ดึงเอกสาร" และตอนดึงจริง สิ่งที่โชว์กับสิ่งที่กดได้จึงต้องตรงกันเสมอ
///
/// กฎที่ทดสอบ:
///   • ห้ามย้อนก่อนเดือนภาษีของเอกสาร (ทั้งฝั่งซื้อ/ขาย) — ใบเดือน ก.ค. ยื่นในแบบ
///     เดือน มิ.ย. ไม่ได้
///   • ฝั่งซื้อ: เคลมได้ในเดือนใบ + 6 เดือนถัดไป (§82/3) เกินแล้วต้องลงค่าใช้จ่าย
///   • ฝั่งขาย: ไม่มีกรอบ 6 เดือน (นำส่งช้าได้ แต่ย้อนอดีตไม่ได้)
/// ครอบ TAX-U-03 / TAX-I-08 ตาม TEST_PLAN.md
/// </summary>
public class VatClaimPeriodTests
{
    private static readonly DateTime JuneInvoice = new(2026, 6, 26);

    // ── ฝั่งซื้อ: กรอบ 6 เดือน §82/3 ────────────────────────────────

    [Theory]
    [InlineData(2026, 6)]   // เดือนเดียวกับใบ
    [InlineData(2026, 7)]   // +1
    [InlineData(2026, 12)]  // +6 = เดือนสุดท้ายที่ยังเคลมได้
    public void Input_vat_can_be_claimed_within_six_months(int year, int month)
    {
        var (ok, reason) = TaxService.EvaluateClaimPeriod(JuneInvoice, isInput: true, year, month);
        Assert.True(ok, reason);
    }

    [Fact]
    public void Input_vat_past_six_months_is_blocked_with_the_deadline_in_the_message()
    {
        // ใบ มิ.ย. 2026 → เคลมได้ถึงงวด 12/2026; งวด 01/2027 เกินแล้ว
        var (ok, reason) = TaxService.EvaluateClaimPeriod(JuneInvoice, isInput: true, 2027, 1);
        Assert.False(ok);
        Assert.Contains("82/3", reason);
        Assert.Contains("12/2026", reason);   // บอกงวดสุดท้ายที่ยังทันให้ผู้ใช้รู้
    }

    // ── ห้ามย้อนอดีต (ทั้งสองฝั่ง) ──────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cannot_pull_into_a_period_before_the_document_month(bool isInput)
    {
        var (ok, reason) = TaxService.EvaluateClaimPeriod(JuneInvoice, isInput, 2026, 5);
        Assert.False(ok);
        Assert.Contains("เก่ากว่า", reason);
    }

    [Fact]
    public void Same_month_is_allowed_even_on_the_last_day()
    {
        var (ok, _) = TaxService.EvaluateClaimPeriod(new DateTime(2026, 6, 30), isInput: true, 2026, 6);
        Assert.True(ok);
    }

    // ── ฝั่งขาย: ไม่มีกรอบ 6 เดือน ──────────────────────────────────

    [Fact]
    public void Output_vat_has_no_six_month_limit()
    {
        // นำส่งภาษีขายช้ากว่า 6 เดือนยังต้องนำส่ง (มีเบี้ยปรับ แต่ระบบต้องให้บันทึกได้)
        var (ok, reason) = TaxService.EvaluateClaimPeriod(JuneInvoice, isInput: false, 2027, 6);
        Assert.True(ok, reason);
    }

    // ── ข้ามปี ──────────────────────────────────────────────────────

    [Fact]
    public void Six_month_window_crosses_year_boundary_correctly()
    {
        var octInvoice = new DateTime(2026, 10, 15);
        Assert.True(TaxService.EvaluateClaimPeriod(octInvoice, true, 2027, 4).Ok);    // +6 → ยังทัน
        Assert.False(TaxService.EvaluateClaimPeriod(octInvoice, true, 2027, 5).Ok);   // +7 → เกิน
    }
}
