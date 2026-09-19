using System;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ฐานประจำ vs ครั้งคราว ต้องมาจากสิ่งที่ผู้ใช้ระบุ ไม่ใช่ prefix ของรหัส**
/// (ผลตรวจรอบ 181 · D6-3 · P0)
///
/// ═══ บั๊กที่ล็อกไว้ ═══
/// <para><c>PayrollService</c> เคยตัดสินด้วย <c>Code.StartsWith("OT")</c> /
/// <c>== "COM"</c> / <c>== "BONUS"</c> และ <b>ที่เหลือทุกตัว = เบี้ยเลี้ยงประจำ</b>
/// ⇒ บริษัทที่ตั้งรหัสเอง (<c>BN01</c> ชื่อ "โบนัส") ได้โบนัสถูกฉาย × งวดที่เหลือ
/// ในการประมาณการเงินได้ทั้งปี ⇒ <b>หักภาษีเกินจริง</b> — golden ของโจทย์:
/// เงินเดือน 50,000 + โบนัส 300,000 จ่ายเดือน 6 ต้องได้ภาษีทั้งปี <b>61,925</b>
/// (เทียบ <c>ThaiPitCalculatorTests</c>) ไม่ใช่ 82,221</para>
///
/// <para>และ <c>PayrollItem.IsTaxable</c> เคยเป็น <b>silent no-op</b> — เขียนแล้ว
/// ไม่มีใครอ่านในเส้นคำนวณ (กฎเหล็ก #4 A)</para>
///
/// <para>ครึ่งหลัง = <b>ทิศตรงข้าม</b>: เบี้ยเลี้ยงประจำจริง (ค่าตำแหน่งทุกเดือน)
/// ต้องยังถูกฉายเหมือนเดิม · และแถวที่ยังไม่ระบุลักษณะต้องได้ตัวเลข
/// <b>เท่าเดิมเป๊ะ</b> (migration คงพฤติกรรม)</para>
/// </summary>
public class PayrollItemNatureTests
{
    /// <summary>ประกันสังคมทั้งปี 10,500 (เพดาน 17,500 × 5% × 12 ปี 2569) —
    /// ชุดค่าลดหย่อนเดียวกับ <c>ThaiPitCalculatorTests</c> เพื่อให้ golden
    /// 61,925 เทียบกันได้ตรง ๆ</summary>
    private const decimal AnnualSso = 10_500m;

    private static PitAllowances Allow() => new(
        Personal: 60_000m, Spouse: 0m, Children: 0m, Parents: 0m,
        LifeInsurance: 0m, ProvidentFund: 0m, SocialSecurity: AnnualSso);

    // ════════ 1. golden ของโจทย์ — BN01 "โบนัส" ════════

    [Fact]
    public void BN01_ที่ระบุว่าเป็นโบนัส_ต้องไม่ถูกฉาย_ภาษีทั้งปี_61925()
    {
        var b = PayrollIncomeNatureRules.Accumulate(new[]
        {
            (PayrollIncomeNature.Bonus, "BN01", true, 300_000m),
        });

        Assert.Equal(300_000m, b.Bonus);
        Assert.Equal(0m, b.RecurringAllowance);      // ★ ต้องไม่ตกเป็นฐานประจำ

        // ฐานประจำที่ถูกฉาย = เงินเดือนอย่างเดียว
        var recurringMonthly = 50_000m + b.RecurringAllowance;
        var ytd = (50_000m * 6m) + b.TaxableExtras;  // เงินเดือน 6 เดือน + โบนัส

        var pit = ThaiPitCalculator.Compute(
            taxableIncomeYtd: ytd,
            recurringMonthlyIncome: recurringMonthly,
            remainingPeriodsAfterThis: 6,
            allowances: Allow(),
            taxWithheldYtdBeforeThisPeriod: 0m);

        Assert.Equal(900_000m, pit.EstimatedAnnualIncome);
        Assert.Equal(61_925m, pit.EstimatedAnnualTax);
    }

    [Fact]
    public void BN01_ที่ยังไม่ระบุลักษณะ_ยังถูกนับเป็นเบี้ยเลี้ยงประจำ_เหมือนก่อนแก้()
    {
        // ★ ทิศ "คงพฤติกรรมเดิม" — แถวที่ migration ยังไม่แตะ ตัวเลขต้องไม่ขยับเอง
        var b = PayrollIncomeNatureRules.Accumulate(new[]
        {
            (PayrollIncomeNature.Unspecified, "BN01", true, 300_000m),
        });

        Assert.Equal(300_000m, b.RecurringAllowance);
        Assert.Equal(0m, b.Bonus);

        var pit = ThaiPitCalculator.Compute(
            taxableIncomeYtd: (50_000m * 6m) + 300_000m,
            recurringMonthlyIncome: 50_000m + b.RecurringAllowance,
            remainingPeriodsAfterThis: 6,
            allowances: Allow(),
            taxWithheldYtdBeforeThisPeriod: 0m);

        // ตัวเลขเดิมที่ผิด — ล็อกไว้เพื่อพิสูจน์ว่า migration ไม่เปลี่ยนอะไร
        Assert.True(pit.EstimatedAnnualTax > 61_925m);
    }

    // ════════ 2. กฎรหัสเดิม = สูตรที่ migration ใช้ ════════

    [Theory]
    [InlineData("OT", PayrollIncomeNature.Overtime)]
    [InlineData("OT-HOLIDAY", PayrollIncomeNature.Overtime)]
    [InlineData("ot1", PayrollIncomeNature.Overtime)]
    [InlineData("COM", PayrollIncomeNature.Commission)]
    [InlineData("com", PayrollIncomeNature.Commission)]
    [InlineData("BONUS", PayrollIncomeNature.Bonus)]
    [InlineData("BN01", PayrollIncomeNature.RecurringAllowance)]        // ★ จุดที่เคยผิด
    [InlineData("INCENTIVE", PayrollIncomeNature.RecurringAllowance)]   // ★ จุดที่เคยผิด
    [InlineData("POSITION", PayrollIncomeNature.RecurringAllowance)]
    [InlineData("", PayrollIncomeNature.RecurringAllowance)]
    [InlineData(null, PayrollIncomeNature.RecurringAllowance)]
    public void กฎรหัสเดิม_ต้องตรงกับพฤติกรรมก่อนแก้ทุกกรณี(string? code, PayrollIncomeNature expected)
        => Assert.Equal(expected,
            PayrollIncomeNatureRules.Effective(PayrollIncomeNature.Unspecified, code));

    [Fact]
    public void ระบุลักษณะแล้ว_ต้องชนะกฎรหัสเดิมเสมอ()
    {
        // "COM" ที่บริษัทใช้เป็นค่าตำแหน่งประจำ ต้องเคารพสิ่งที่ผู้ใช้ระบุ
        Assert.Equal(PayrollIncomeNature.RecurringAllowance,
            PayrollIncomeNatureRules.Effective(PayrollIncomeNature.RecurringAllowance, "COM"));
        Assert.Equal(PayrollIncomeNature.Bonus,
            PayrollIncomeNatureRules.Effective(PayrollIncomeNature.Bonus, "OT-SPECIAL"));
    }

    // ════════ 3. ทิศตรงข้าม — เบี้ยเลี้ยงประจำจริงต้องยังถูกฉาย ════════

    [Fact]
    public void ค่าตำแหน่งที่ได้ทุกเดือน_ต้องยังถูกฉายไปงวดที่เหลือเหมือนเดิม()
    {
        var b = PayrollIncomeNatureRules.Accumulate(new[]
        {
            (PayrollIncomeNature.RecurringAllowance, "POSITION", true, 5_000m),
        });

        Assert.Equal(5_000m, b.RecurringAllowance);

        var pit = ThaiPitCalculator.Compute(
            taxableIncomeYtd: (50_000m + 5_000m) * 6m,
            recurringMonthlyIncome: 50_000m + b.RecurringAllowance,
            remainingPeriodsAfterThis: 6,
            allowances: Allow(),
            taxWithheldYtdBeforeThisPeriod: 0m);

        // 55,000 × 12 = 660,000 — ค่าตำแหน่งยังถูกนับทั้งปีตามเดิม
        Assert.Equal(660_000m, pit.EstimatedAnnualIncome);
    }

    [Fact]
    public void เงินได้ครั้งคราว_เข้าฐานภาษีงวดนี้_แต่ไม่ถูกฉาย()
    {
        var b = PayrollIncomeNatureRules.Accumulate(new[]
        {
            (PayrollIncomeNature.OneTimeAllowance, "REWARD", true, 20_000m),
        });

        Assert.Equal(20_000m, b.OneTimeAllowance);
        Assert.Equal(0m, b.RecurringAllowance);
        Assert.Equal(20_000m, b.TaxableExtras);     // ยังเข้าฐานภาษีของงวดนี้
    }

    // ════════ 4. IsTaxable ต้องมีผลจริง (เลิก silent no-op) ════════

    [Fact]
    public void ติ๊กไม่หักภาษี_ต้องไม่เข้าฐานภาษี_แต่ยังจ่ายให้พนักงาน()
    {
        var b = PayrollIncomeNatureRules.Accumulate(new[]
        {
            (PayrollIncomeNature.RecurringAllowance, "MEDICAL", false, 3_000m),
        });

        Assert.Equal(3_000m, b.NonTaxable);
        Assert.Equal(0m, b.RecurringAllowance);
        Assert.Equal(0m, b.TaxableExtras);          // ★ ธงมีผลจริง
    }

    [Fact]
    public void รายการที่คิดภาษี_ต้องไม่ถูกย้ายไปถังยกเว้นโดยไม่ตั้งใจ()
    {
        var b = PayrollIncomeNatureRules.Accumulate(new[]
        {
            (PayrollIncomeNature.RecurringAllowance, "MEAL", true, 2_000m),
        });

        Assert.Equal(0m, b.NonTaxable);
        Assert.Equal(2_000m, b.RecurringAllowance);
    }

    // ════════ 5. รวมหลายรายการ + ขอบ ════════

    [Fact]
    public void รวมหลายรายการลงถังถูกต้องพร้อมกัน()
    {
        var b = PayrollIncomeNatureRules.Accumulate(new[]
        {
            (PayrollIncomeNature.Overtime, "OT", true, 1_000m),
            (PayrollIncomeNature.RecurringAllowance, "POSITION", true, 5_000m),
            (PayrollIncomeNature.OneTimeAllowance, "REWARD", true, 2_000m),
            (PayrollIncomeNature.Commission, "COM", true, 3_000m),
            (PayrollIncomeNature.Bonus, "BN01", true, 10_000m),
            (PayrollIncomeNature.RecurringAllowance, "MEDICAL", false, 500m),
            (PayrollIncomeNature.Bonus, "ZERO", true, 0m),
        });

        Assert.Equal(1_000m, b.Overtime);
        Assert.Equal(5_000m, b.RecurringAllowance);
        Assert.Equal(2_000m, b.OneTimeAllowance);
        Assert.Equal(3_000m, b.Commission);
        Assert.Equal(10_000m, b.Bonus);
        Assert.Equal(500m, b.NonTaxable);
        Assert.Equal(21_000m, b.TaxableExtras);
    }

    [Fact]
    public void ไม่มีรายการเลย_ทุกถังเป็นศูนย์()
    {
        var b = PayrollIncomeNatureRules.Accumulate(
            Array.Empty<(PayrollIncomeNature, string?, bool, decimal)>());
        Assert.Equal(0m, b.TaxableExtras);
        Assert.Equal(0m, b.NonTaxable);
    }

    [Fact]
    public void คำอธิบายของทุกค่า_ต้องไม่ว่าง_และค่ายังไม่ระบุต้องเตือน()
    {
        foreach (PayrollIncomeNature n in Enum.GetValues<PayrollIncomeNature>())
            Assert.False(string.IsNullOrWhiteSpace(PayrollIncomeNatureRules.Describe(n)));

        Assert.Contains("BN01", PayrollIncomeNatureRules.Describe(PayrollIncomeNature.Unspecified));
    }
}
