using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ภาษีเงินได้บุคคลธรรมดาจากเงินเดือน (D-T1 … D-T4)
///
/// ═══ บั๊กจริงที่เทสต์ชุดนี้ล็อกไว้ ═══
/// เครื่องคิดภาษีเดิม (~470 บรรทัดกลาง <c>PayrollService</c> ไม่มีเทสต์เลย)
/// ผิด 4 ทางพร้อมกัน — ตัวเลขในเทสต์นี้คือตัวเลขที่ผลตรวจคำนวณไว้ ใช้เป็น
/// ground truth: <b>เงินเดือน 50,000 ระบบหัก 31,925/ปี ควรเป็น 20,450</b> ·
/// <b>มีคู่สมรส+บุตร 2 ควรเป็น 6,475 (ต่างกัน 4.9 เท่า)</b> ·
/// <b>โบนัส 300,000 เดือน 6 ระบบหักรวม 82,221 ควรเป็น 61,925</b>
///
/// <para>ยอดประกันสังคมที่ใช้ในเทสต์ = 10,500/ปี (เพดานค่าจ้าง 17,500 × 5% × 12
/// ตามปี 2569) — ส่งเข้ามาเป็นพารามิเตอร์ ไม่ hard-code ในตัวคำนวณ</para>
/// </summary>
public class ThaiPitCalculatorTests
{
    private const decimal AnnualSso = 10_500m;

    private static PitAllowances Allow(
        decimal spouse = 0, decimal children = 0, decimal parents = 0,
        decimal life = 0, decimal pvd = 0, decimal sso = AnnualSso, decimal personal = 60_000m)
        => new(personal, spouse, children, parents, life, pvd, sso);

    // ── §42ทวิ ค่าใช้จ่าย 50% ไม่เกิน 100,000 (D-T1) ──

    [Fact]
    public void เงินเดือน_50000_โสด_ต้องเสียภาษี_20450_ต่อปี()
    {
        // ★ เคสหลักของผลตรวจ — เดิมระบบหัก 31,925 (ไม่หักค่าใช้จ่าย §42ทวิ)
        var r = ThaiPitCalculator.Compute(
            taxableIncomeYtd: 50_000m,
            recurringMonthlyIncome: 50_000m,
            remainingPeriodsAfterThis: 11,
            allowances: Allow(),
            taxWithheldYtdBeforeThisPeriod: 0m);

        Assert.Equal(600_000m, r.EstimatedAnnualIncome);
        Assert.Equal(100_000m, r.ExpenseDeduction);      // 50% = 300,000 แต่ติดเพดาน
        Assert.Equal(429_500m, r.TaxableIncome);         // 600,000 − 100,000 − 70,500
        Assert.Equal(20_450m, r.EstimatedAnnualTax);
    }

    [Fact]
    public void เกณฑ์เดิม_ไม่หักค่าใช้จ่าย_ได้_31925_ซึ่งเกินจริง()
    {
        // negative test: พิสูจน์ว่าตัวเลขที่ผลตรวจรายงาน (31,925) มาจากการ
        // "ลืมหักค่าใช้จ่าย §42ทวิ" จริง ๆ ไม่ใช่จากสาเหตุอื่น
        var withoutExpense = ThaiPitCalculator.AnnualTax(600_000m - 70_500m);
        Assert.Equal(31_925m, withoutExpense);

        var withExpense = ThaiPitCalculator.AnnualTax(600_000m - 100_000m - 70_500m);
        Assert.Equal(20_450m, withExpense);
        Assert.Equal(11_475m, withoutExpense - withExpense);   // เกินไป 956.25/เดือน
    }

    [Theory]
    [InlineData(120_000, 60_000)]      // 50% ยังไม่ถึงเพดาน
    [InlineData(200_000, 100_000)]     // 50% = เพดานพอดี
    [InlineData(600_000, 100_000)]     // ติดเพดาน
    [InlineData(5_000_000, 100_000)]   // ติดเพดานเหมือนกัน
    [InlineData(0, 0)]
    public void ค่าใช้จ่ายหักได้_50_เปอร์เซ็นต์แต่ไม่เกิน_100000(decimal income, decimal expected)
        => Assert.Equal(expected, ThaiPitCalculator.ExpenseDeduction(income));

    [Fact]
    public void เพดานค่าใช้จ่ายปรับได้จากตั้งค่าของบริษัท()
    {
        // Section42TwiCap มีอยู่ในตารางตั้งค่ามาตลอดแต่ไม่เคยมีใครอ่าน
        Assert.Equal(150_000m, ThaiPitCalculator.ExpenseDeduction(600_000m, cap: 150_000m));
    }

    // ── ค่าลดหย่อน §47 (D-T2) ──

    [Fact]
    public void คู่สมรส_บุตร_2_คน_เงินเดือน_50000_ต้องเสีย_6475()
    {
        // ★ เคสที่ต่างจากระบบเดิม 4.9 เท่า (31,925 → 6,475)
        // บุตรคนแรก 30,000 · บุตรคนที่สองขึ้นไปที่เกิดหลัง 2561 = 60,000
        var r = ThaiPitCalculator.Compute(
            taxableIncomeYtd: 50_000m,
            recurringMonthlyIncome: 50_000m,
            remainingPeriodsAfterThis: 11,
            allowances: Allow(spouse: 60_000m, children: 30_000m + 60_000m),
            taxWithheldYtdBeforeThisPeriod: 0m);

        Assert.Equal(320_500m, r.ExpenseDeduction + r.TotalAllowances);
        Assert.Equal(279_500m, r.TaxableIncome);
        Assert.Equal(6_475m, r.EstimatedAnnualTax);
    }

    [Fact]
    public void ลดหย่อนทุกช่องถูกนำมารวม()
    {
        var a = Allow(spouse: 60_000m, children: 90_000m, parents: 60_000m,
                      life: 100_000m, pvd: 50_000m);
        Assert.Equal(60_000m + 60_000m + 90_000m + 60_000m + 100_000m + 50_000m + AnnualSso, a.Total);
    }

    [Fact]
    public void เงินบริจาคหักได้ไม่เกินร้อยละที่กำหนดของเงินได้หลังลดหย่อน()
    {
        var r = ThaiPitCalculator.Compute(
            taxableIncomeYtd: 600_000m, recurringMonthlyIncome: 0m, remainingPeriodsAfterThis: 0,
            allowances: Allow(), taxWithheldYtdBeforeThisPeriod: 0m,
            donationAmount: 1_000_000m, donationCapPercent: 10m);

        // หลังหักค่าใช้จ่าย+ลดหย่อน = 429,500 ⇒ บริจาคหักได้ 42,950
        Assert.Equal(42_950m, r.DonationDeduction);
        Assert.Equal(386_550m, r.TaxableIncome);
    }

    // ── โบนัสต้องไม่ถูกคูณเป็นรายปี (D-T3) ──

    [Fact]
    public void โบนัส_300000_ในเดือน_6_ต้องเสียภาษีรวม_61925_ไม่ใช่_82221()
    {
        // ★ เดือน 6: ได้เงินเดือนมาแล้ว 6 เดือน + โบนัสก้อนเดียว
        // สูตรเดิม ytd × 12 ÷ 6 = (300,000 + 300,000) × 2 = 1,200,000 ⇒ ฉายโบนัส
        // เป็นรายเดือน · สูตรใหม่ฉายเฉพาะ "ฐานประจำ" ไปข้างหน้า
        var r = ThaiPitCalculator.Compute(
            taxableIncomeYtd: 300_000m + 300_000m,   // เงินเดือน 6 เดือน + โบนัส
            recurringMonthlyIncome: 50_000m,
            remainingPeriodsAfterThis: 6,
            allowances: Allow(),
            taxWithheldYtdBeforeThisPeriod: 0m);

        Assert.Equal(900_000m, r.EstimatedAnnualIncome);   // ไม่ใช่ 1,200,000
        Assert.Equal(729_500m, r.TaxableIncome);
        Assert.Equal(61_925m, r.EstimatedAnnualTax);
    }

    [Fact]
    public void เกณฑ์เดิม_คูณ_ytd_ด้วย_12_หารเดือน_ทำให้โบนัสกลายเป็นรายเดือน()
    {
        // negative test ของสูตรเดิม
        var oldEstimate = (300_000m + 300_000m) * 12m / 6m;
        Assert.Equal(1_200_000m, oldEstimate);
        var newEstimate = ThaiPitCalculator.EstimateAnnualIncome(600_000m, 50_000m, 6);
        Assert.Equal(900_000m, newEstimate);
        Assert.True(oldEstimate > newEstimate);
    }

    [Fact]
    public void หักเกินในเดือนโบนัสแล้วคืนได้ในเดือนถัดไป_แต่ไม่เกินที่หักไป()
    {
        // ★ เดิม Math.Max(0, …) ทำให้คืนไม่ได้เลยตลอดปี
        Assert.Equal(-1_000m, ThaiPitCalculator.WithholdingForPeriod(
            estimatedAnnualTax: 9_000m, taxWithheldYtdBeforeThisPeriod: 10_000m,
            periodsRemainingIncludingThis: 1));

        // คืนได้ไม่เกินยอดที่หักไปแล้ว — ยอดสะสมห้ามติดลบ
        Assert.Equal(-500m, ThaiPitCalculator.WithholdingForPeriod(
            estimatedAnnualTax: 0m, taxWithheldYtdBeforeThisPeriod: 500m,
            periodsRemainingIncludingThis: 1));
    }

    // ── คนเข้ากลางปี (D-T4) ──

    [Fact]
    public void เข้างาน_1_กรกฎาคม_เงินเดือน_150000_หักเท่ากันทุกงวด()
    {
        // ★ เดิมประมาณการจาก "เดือนที่ผ่านมา" ⇒ งวดแรกหัก 305 บาท แล้วพุ่งเป็น
        // 36,759 ในงวดสุดท้าย (25% ของเงินเดือน) — พนักงานเห็นเงินเข้าไม่เท่ากัน
        // ทุกเดือนโดยไม่มีคำอธิบาย
        decimal withheld = 0m;
        var perPeriod = new List<decimal>();
        for (var i = 0; i < 6; i++)
        {
            var r = ThaiPitCalculator.Compute(
                taxableIncomeYtd: 150_000m * (i + 1),
                recurringMonthlyIncome: 150_000m,
                remainingPeriodsAfterThis: 5 - i,
                allowances: Allow(),
                taxWithheldYtdBeforeThisPeriod: withheld);
            perPeriod.Add(r.WithholdingThisPeriod);
            withheld += r.WithholdingThisPeriod;
            Assert.Equal(900_000m, r.EstimatedAnnualIncome);
            Assert.Equal(61_925m, r.EstimatedAnnualTax);
        }

        // ทุกงวดเท่ากัน (คลาดได้ไม่เกิน 1 สตางค์จากการปัดเศษ) และรวมได้ภาษีทั้งปี
        Assert.All(perPeriod, x => Assert.True(Math.Abs(x - perPeriod[0]) <= 0.01m,
            $"งวดต่างกันเกินการปัดเศษ: {string.Join(", ", perPeriod)}"));
        Assert.Equal(61_925m, withheld);
    }

    [Fact]
    public void งวดที่เหลือเป็นศูนย์_ไม่หารด้วยศูนย์()
        => Assert.Equal(0m, ThaiPitCalculator.WithholdingForPeriod(50_000m, 0m, 0));

    // ── ขั้นภาษี ──

    [Theory]
    [InlineData(0, 0)]
    [InlineData(150_000, 0)]              // ขั้นยกเว้น
    [InlineData(150_001, 0.05)]           // บาทแรกของขั้น 5%
    [InlineData(300_000, 7_500)]
    [InlineData(500_000, 27_500)]
    [InlineData(750_000, 65_000)]
    [InlineData(1_000_000, 115_000)]
    [InlineData(2_000_000, 365_000)]
    [InlineData(5_000_000, 1_265_000)]
    public void ขั้นภาษี_48_1_ถูกต้องทุกขอบขั้น(decimal taxable, decimal expected)
        => Assert.Equal(expected, ThaiPitCalculator.AnnualTax(taxable));

    [Fact]
    public void เงินได้สุทธิติดลบไม่คิดภาษี()
    {
        Assert.Equal(0m, ThaiPitCalculator.AnnualTax(-1m));
        var r = ThaiPitCalculator.Compute(
            taxableIncomeYtd: 10_000m, recurringMonthlyIncome: 10_000m,
            remainingPeriodsAfterThis: 11, allowances: Allow(),
            taxWithheldYtdBeforeThisPeriod: 0m);
        Assert.Equal(0m, r.TaxableIncome);
        Assert.Equal(0m, r.EstimatedAnnualTax);
    }

    [Fact]
    public void ขั้นภาษีที่บริษัทตั้งเองถูกใช้แทนค่าเริ่มต้น()
    {
        var flat = new[] { new PitBracket(decimal.MaxValue, 0.10m) };
        Assert.Equal(60_000m, ThaiPitCalculator.AnnualTax(600_000m, flat));
    }
}
