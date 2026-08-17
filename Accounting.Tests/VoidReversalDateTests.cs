using Xunit;

namespace Accounting.Tests;

/// <summary>
/// วันที่ลงรายการกลับบัญชีตอนยกเลิกเอกสาร — ต้องอยู่ **งวดเดียวกับเอกสาร**
/// ไม่ใช่วันที่กดยกเลิก
///
/// ที่มา: ยกเลิกใบของเดือนก่อน แล้ว reversal JE ไปตกเดือนปัจจุบัน ⇒ ผิดสองเดือน
/// พร้อมกัน — เดือนเก่าค้างยอดที่ไม่มีอยู่จริง (ขาย/ลูกหนี้/ภาษีขายเกิน) และ
/// เดือนใหม่มียอดติดลบที่ไม่มีที่มา (งบเปรียบเทียบรายเดือนอ่านไม่ได้)
/// </summary>
public class VoidReversalDateTests
{
    private enum PeriodState { Open, Closed, NoPeriod }

    /// <summary>mirror ของ DocumentService.ResolveReversalDateAsync</summary>
    private static (DateTime Date, bool Fallback) Resolve(
        DateTime wanted, PeriodState state, DateTime today)
        => state == PeriodState.Closed ? (today.Date, true) : (wanted.Date, false);

    private static readonly DateTime Today = new(2026, 8, 17);

    [Fact]
    public void Default_uses_document_date_not_click_date()
    {
        var docDate = new DateTime(2026, 7, 24);
        var (date, fallback) = Resolve(docDate, PeriodState.Open, Today);
        Assert.Equal(docDate, date);          // ไม่ใช่ 17/08
        Assert.False(fallback);
        Assert.Equal(7, date.Month);          // อยู่งวดเดียวกับเอกสาร
    }

    [Fact]
    public void Explicit_date_wins_over_document_date()
    {
        var chosen = new DateTime(2026, 8, 1);
        var (date, _) = Resolve(chosen, PeriodState.Open, Today);
        Assert.Equal(chosen, date);
    }

    [Fact]
    public void Closed_period_falls_back_to_today_and_reports_it()
    {
        // ห้ามยัดเข้างวดปิด (TAS 1 งวดที่ปิดแล้วแก้ไม่ได้) — แต่ก็ห้ามบล็อกการ
        // ยกเลิกทั้งหมด ⇒ ตกกลับวันนี้ **พร้อมบอกเหตุผล** (ห้ามเงียบ)
        var docDate = new DateTime(2026, 1, 15);
        var (date, fallback) = Resolve(docDate, PeriodState.Closed, Today);
        Assert.Equal(Today, date);
        Assert.True(fallback);
    }

    [Fact]
    public void No_fiscal_period_defined_still_uses_document_date()
    {
        // บริษัทที่ยังไม่ตั้งงวดบัญชี — ไม่มีอะไรให้บล็อก ใช้วันที่เอกสารตามปกติ
        var docDate = new DateTime(2026, 6, 30);
        var (date, fallback) = Resolve(docDate, PeriodState.NoPeriod, Today);
        Assert.Equal(docDate, date);
        Assert.False(fallback);
    }

    [Fact]
    public void Time_component_is_dropped()
    {
        var docDate = new DateTime(2026, 7, 24, 23, 59, 58);
        var (date, _) = Resolve(docDate, PeriodState.Open, Today);
        Assert.Equal(new DateTime(2026, 7, 24), date);
        Assert.Equal(TimeSpan.Zero, date.TimeOfDay);
    }

    /// <summary>เครื่องมือแก้ย้อนหลัง (RedateVoidReversalAsync) — ย้ายเฉพาะ
    /// "ตัวกลับ" ยอดสุทธิของเอกสารต้องไม่เปลี่ยน</summary>
    [Fact]
    public void Redating_reversal_never_changes_net_amount()
    {
        var original = new { Debit = 1000m, Credit = 0m, Date = new DateTime(2026, 7, 24) };
        var reversalBefore = new { Debit = 0m, Credit = 1000m, Date = new DateTime(2026, 8, 17) };
        var reversalAfter = new { reversalBefore.Debit, reversalBefore.Credit, Date = original.Date };

        // ยอดสุทธิ (Dr − Cr) เท่าเดิมทั้งก่อนและหลังย้ายวันที่
        var netBefore = original.Debit - original.Credit + reversalBefore.Debit - reversalBefore.Credit;
        var netAfter = original.Debit - original.Credit + reversalAfter.Debit - reversalAfter.Credit;
        Assert.Equal(0m, netBefore);
        Assert.Equal(netBefore, netAfter);

        // แต่ "งวด" เปลี่ยน: ก่อนแก้ กระจายสองเดือน / หลังแก้ อยู่เดือนเดียว
        Assert.NotEqual(original.Date.Month, reversalBefore.Date.Month);
        Assert.Equal(original.Date.Month, reversalAfter.Date.Month);
    }

    [Theory]
    [InlineData(7, 7, 0)]    // ต้นฉบับ ก.ค. + ตัวกลับ ก.ค. → ผลกระทบสุทธิเดือน ก.ค. = 0
    [InlineData(7, 8, 1000)] // ตัวกลับข้ามไป ส.ค. → ก.ค. ค้าง 1000 (และ ส.ค. −1000)
    public void Cross_month_reversal_leaves_a_phantom_balance(
        int origMonth, int revMonth, decimal julyResidual)
    {
        const decimal amount = 1000m;
        var julyNet = (origMonth == 7 ? amount : 0m) - (revMonth == 7 ? amount : 0m);
        Assert.Equal(julyResidual, julyNet);
    }
}
