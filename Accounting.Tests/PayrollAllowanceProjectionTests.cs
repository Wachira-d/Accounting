using System.Collections.Generic;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **เบี้ยเลี้ยงผันแปรต้องไม่ถูกฉาย × งวดที่เหลือ** — D6-3 แหล่งที่ 2/3
/// (DECISION_AUDIT_2026-09-18 §10.5 ข้อ 9)
///
/// <para>═══ บั๊กที่ล็อกไว้ ═══ รอบที่แล้วแก้ให้ <c>PayrollItem</c> แยกประจำ/
/// ครั้งคราวแล้ว แต่ <b>อีกสองแหล่ง</b>ยังไหลเข้าถัง <c>allowances</c> ตัวเดียวกัน
/// แล้วถูกคูณกับงวดที่เหลือ:
/// <list type="number">
/// <item>เบี้ยเลี้ยงจาก <b>การลงเวลา</b> — per-diem · ค่าที่พัก · ค่าอาหารวัน OT ·
///   ค่าอาหารรายวัน (<c>PayrollService.cs:1734</c> เดิม)</item>
/// <item><c>CustomAllowanceItem</c> แบบ <c>Daily</c> ที่คูณจำนวนวันทำงานจริง</item>
/// </list>
/// ⇒ พนักงานที่ไปต่างจังหวัด<b>เดือนเดียว</b> ถูกประมาณการรายได้ทั้งปีสูงเกินจริง
/// แล้ว<b>ถูกหักภาษี ณ ที่จ่ายเกิน</b>ในงวดนั้น (ลูกจ้างออกเงินให้บริษัทไปก่อน)
/// และตัวเลข "ประมาณการเงินได้ทั้งปี" บนสลิปก็โกหก</para>
///
/// <para>═══ golden ของรอบนี้ ═══ เงินเดือน 30,000 · เบี้ยเลี้ยงเดินทาง 8,000
/// ในเดือน 3 เดือนเดียว · ค่าลดหย่อน 60,000 + ปกส. 10,500
/// <list type="bullet">
/// <item><b>เดิม</b> ประมาณการทั้งปี 440,000 → ภาษีทั้งปี 5,975 → หักงวดนี้ 564.58</item>
/// <item><b>ใหม่</b> ประมาณการทั้งปี 368,000 → ภาษีทั้งปี 2,375 → หักงวดนี้ 204.58</item>
/// </list></para>
///
/// <para>ครึ่งตรงข้าม (บังคับตาม G7 · กฎเหล็ก #4 H): ค่าตำแหน่งรายเดือนที่เป็น
/// เงินประจำจริง (<c>PayrollItem</c> RecurringAllowance · custom allowance แบบ
/// <c>Monthly</c>) <b>ต้องยังถูกฉายเหมือนเดิมทุกบาท</b></para>
/// </summary>
public class PayrollAllowanceProjectionTests
{
    private const decimal AnnualSso = 10_500m;   // เพดาน 17,500 × 5% × 12 (ปี 2569)

    private static PitAllowances Allow() => new(
        Personal: 60_000m, Spouse: 0m, Children: 0m, Parents: 0m,
        LifeInsurance: 0m, ProvidentFund: 0m, SocialSecurity: AnnualSso);

    /// <summary>ภาษีสะสมที่หักไปแล้วในเดือน 1–2 (เงินเดือนล้วน 30,000) = 329.16</summary>
    private const decimal WithheldBeforeMonth3 = 329.16m;

    // ═══════════ 1. golden — เบี้ยเลี้ยงเดินทางจากการลงเวลา ═══════════

    [Fact]
    public void เบี้ยเลี้ยงจากการลงเวลา_ต้องลงถังครั้งคราว_ไม่ใช่ประจำ()
    {
        var b = PayrollIncomeNatureRules.Accumulate(new[]
        {
            (PayrollIncomeNatureRules.AttendanceAllowance, (string?)"ATTENDANCE", true, 8_000m),
        });

        Assert.Equal(0m, b.RecurringAllowance);     // ★ จุดที่เคยผิด
        Assert.Equal(8_000m, b.OneTimeAllowance);
        // แต่ยอดที่แสดงบนสลิปต้องยังเป็น 8,000 เท่าเดิม (ไม่ใช่หายไป)
        Assert.Equal(8_000m, b.AllowanceTotal);
    }

    [Fact]
    public void ไปต่างจังหวัดเดือนเดียว_ประมาณการทั้งปีต้องเป็น_368000_ไม่ใช่_440000()
    {
        var b = PayrollIncomeNatureRules.Accumulate(new[]
        {
            (PayrollIncomeNatureRules.AttendanceAllowance, (string?)"ATTENDANCE", true, 8_000m),
        });

        var ytd = (30_000m * 2m) + (30_000m + b.AllowanceTotal);   // 98,000
        var recurringMonthly = 30_000m + b.RecurringAllowance;     // 30,000 (เดิม 38,000)

        var pit = ThaiPitCalculator.Compute(
            taxableIncomeYtd: ytd,
            recurringMonthlyIncome: recurringMonthly,
            remainingPeriodsAfterThis: 9,
            allowances: Allow(),
            taxWithheldYtdBeforeThisPeriod: WithheldBeforeMonth3);

        Assert.Equal(368_000m, pit.EstimatedAnnualIncome);
        Assert.Equal(2_375m, pit.EstimatedAnnualTax);
        Assert.Equal(204.58m, pit.WithholdingThisPeriod);
    }

    [Fact]
    public void พฤติกรรมเดิม_ที่ฉายเบี้ยเลี้ยงเดินทาง_ให้ตัวเลขที่ผิด_440000()
    {
        // ล็อก "ตัวเลขก่อนแก้" ไว้เป็นหลักฐาน — ถ้าใครเผลอรวมสองถังกลับเข้าด้วยกัน
        // เทสต์ข้างบนจะได้ค่านี้แทน แล้วเห็นทันทีว่าถอยกลับไปบั๊กเดิม
        var pit = ThaiPitCalculator.Compute(
            taxableIncomeYtd: 98_000m,
            recurringMonthlyIncome: 38_000m,   // ← เงินเดือน + เบี้ยเลี้ยงเดินทาง
            remainingPeriodsAfterThis: 9,
            allowances: Allow(),
            taxWithheldYtdBeforeThisPeriod: WithheldBeforeMonth3);

        Assert.Equal(440_000m, pit.EstimatedAnnualIncome);
        Assert.Equal(5_975m, pit.EstimatedAnnualTax);
        Assert.Equal(564.58m, pit.WithholdingThisPeriod);
        // ส่วนต่างที่พนักงานถูกหักเกินในงวดที่ไปต่างจังหวัด
        Assert.Equal(360.00m, 564.58m - 204.58m);
    }

    // ═══════════ 2. custom allowance — Daily vs Monthly ═══════════

    [Theory]
    [InlineData("Daily", PayrollIncomeNature.OneTimeAllowance)]
    [InlineData("daily", PayrollIncomeNature.OneTimeAllowance)]
    [InlineData("Monthly", PayrollIncomeNature.RecurringAllowance)]
    [InlineData("", PayrollIncomeNature.RecurringAllowance)]
    [InlineData(null, PayrollIncomeNature.RecurringAllowance)]
    public void เบี้ยเลี้ยงที่บริษัทตั้งเอง_Daily_ผันแปร_Monthly_ประจำ(
        string? type, PayrollIncomeNature expected)
        => Assert.Equal(expected, PayrollIncomeNatureRules.ForCustomAllowance(type));

    [Fact]
    public void ค่าอาหารรายวัน_ต้องไม่ถูกฉาย_แต่ค่าตำแหน่งรายเดือนต้องถูกฉาย()
    {
        var b = PayrollIncomeNatureRules.Accumulate(new List<(PayrollIncomeNature, string?, bool, decimal)>
        {
            (PayrollIncomeNatureRules.ForCustomAllowance("Daily"),   "MEAL", true, 1_100m),
            (PayrollIncomeNatureRules.ForCustomAllowance("Monthly"), "POS",  true, 5_000m),
        });

        Assert.Equal(5_000m, b.RecurringAllowance);   // เฉพาะค่าตำแหน่ง
        Assert.Equal(1_100m, b.OneTimeAllowance);
        Assert.Equal(6_100m, b.AllowanceTotal);       // ยอดที่แสดงต้องครบเท่าเดิม
    }

    [Fact]
    public void เบี้ยเลี้ยงที่ติ๊กยกเว้นภาษี_ต้องไม่เข้าฐานภาษีและไม่ถูกฉาย()
    {
        var b = PayrollIncomeNatureRules.Accumulate(new List<(PayrollIncomeNature, string?, bool, decimal)>
        {
            (PayrollIncomeNatureRules.ForCustomAllowance("Monthly"), "MED", false, 2_000m),
        });

        Assert.Equal(0m, b.RecurringAllowance);
        Assert.Equal(2_000m, b.NonTaxable);
        Assert.Equal(0m, b.TaxableExtras);
    }

    // ═══════════ 3. ครึ่งตรงข้าม — เงินประจำจริงต้องไม่ถูกแตะ ═══════════

    [Fact]
    public void ค่าตำแหน่งรายเดือน_ต้องยังถูกฉายให้ผลเท่าเดิมเป๊ะ()
    {
        var b = PayrollIncomeNatureRules.Accumulate(new[]
        {
            (PayrollIncomeNature.RecurringAllowance, (string?)"POSITION", true, 5_000m),
        });

        var pit = ThaiPitCalculator.Compute(
            taxableIncomeYtd: 35_000m * 3m,
            recurringMonthlyIncome: 30_000m + b.RecurringAllowance,   // = 35,000
            remainingPeriodsAfterThis: 9,
            allowances: Allow(),
            taxWithheldYtdBeforeThisPeriod: 829.16m);

        // ตัวเลขชุดนี้ต้องไม่ขยับจากก่อนแก้
        Assert.Equal(420_000m, pit.EstimatedAnnualIncome);
        Assert.Equal(4_975m, pit.EstimatedAnnualTax);
        Assert.Equal(414.58m, pit.WithholdingThisPeriod);
    }

    [Fact]
    public void รวมหลายแหล่ง_ต้องบวกถังตรงกัน_ไม่ทำยอดรวมหาย()
    {
        var fromItems = PayrollIncomeNatureRules.Accumulate(new[]
        {
            (PayrollIncomeNature.RecurringAllowance, (string?)"POSITION", true, 5_000m),
            (PayrollIncomeNature.Bonus, (string?)"BN01", true, 20_000m),
        });
        var fromAttendance = PayrollIncomeNatureRules.Accumulate(new[]
        {
            (PayrollIncomeNatureRules.AttendanceAllowance, (string?)"ATTENDANCE", true, 3_500m),
        });

        var all = fromItems.Plus(fromAttendance);

        Assert.Equal(5_000m, all.RecurringAllowance);
        Assert.Equal(3_500m, all.OneTimeAllowance);
        Assert.Equal(8_500m, all.AllowanceTotal);
        Assert.Equal(20_000m, all.Bonus);
        Assert.Equal(28_500m, all.TaxableExtras);
    }
}
