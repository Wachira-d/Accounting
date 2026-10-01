using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เครื่องคิดภาษีหัก ณ ที่จ่ายเงินเดือนรายคนตัวเดียว (รอบ 201 ทีม PR2 · คำตัดสินข้อ 73) — ย้ายจาก inline ใน
/// <c>PayrollService.CalculatePayrollAsync</c> เพื่อให้ปุ่ม "🧮 คำนวณภาษีให้" ใช้สูตรเดียวกัน
///
/// <para><b>Golden ก่อน/หลัง</b>: <see cref="Reference"/> คือสูตร inline เดิม<b>คำต่อคำ</b>จาก
/// <c>git show 9c9580e6:Accounting/Services/Implementations/PayrollService.cs</c> (บรรทัด ~2313–2385 · ค่าคงที่เดิม 60,000 ·
/// 30,000 · 500,000 · literal 60_000/30_000/100_000/10) — ทุกกรณีในตารางต้องได้ผล<b>เท่ากันทุกช่อง</b> ⇒ การคำนวณรอบเดิมไม่เปลี่ยน
/// แม้แต่สตางค์เดียว · กรณีตัวเลขตายตัว (เงินเดือน 50,000 ⇒ ทั้งปี 20,450) ล็อกว่าสูตรอ้างอิงเองก็ยังเป็นตัวเลขที่ถูก</para>
/// </summary>
public class PayrollWithholdingTaxTests
{
    // ── สูตรเดิม (9c9580e6) — ห้ามแก้ให้ตรงกับ helper: นี่คือ "ก่อน" ──
    private static PitResult Reference(Employee emp, int runYear, int runMonth, decimal ytdIncome, decimal recurringMonthly,
        decimal cumulativeTax, decimal ssoEmployee, decimal ssoMaxContribution, decimal pvdEmployee,
        TaxRuleConfig? taxCfg, PitBracket[]? brackets)
    {
        const decimal PitPersonalAllowance = 60_000m;
        const decimal PitPerDependantAllowance = 30_000m;
        const decimal PitPvdMaxDeductible = 500_000m;
        var lastPayMonth = emp.EndDate.HasValue && emp.EndDate.Value.Year == runYear
            ? Math.Min(12, Math.Max(runMonth, emp.EndDate.Value.Month))
            : 12;
        var remainingPeriodsAfterThis = Math.Max(0, lastPayMonth - runMonth);
        var annualSso = Math.Min(ssoEmployee * 12m, ssoMaxContribution * 12m);
        var annualPvd = Math.Min(pvdEmployee * 12m, PitPvdMaxDeductible);
        var personalAllow = taxCfg?.PersonalAllowance ?? PitPersonalAllowance;
        var spouseAllow = taxCfg?.SpouseAllowance ?? 60_000m;
        var childAllow = taxCfg?.ChildAllowance ?? 30_000m;
        var childPost2561Bonus = (taxCfg?.ChildAllowancePost2561 ?? 60_000m) - childAllow;
        var parentAllow = taxCfg?.ParentAllowance ?? 30_000m;
        var lifeInsCap = taxCfg?.LifeInsuranceCap ?? 100_000m;
        var pvdCap = taxCfg?.PvdCap ?? PitPvdMaxDeductible;
        var donationCapPct = (taxCfg?.DonationCapPercent ?? 10m) / 100m;
        var perDependantLegacy = PitPerDependantAllowance;
        var hasDetailed = emp.HasSpouseAllowance
            || emp.ChildAllowanceCount > 0 || emp.SecondAndLaterChildren > 0
            || emp.ParentAllowanceCount > 0 || emp.LifeInsurancePremium > 0
            || emp.RmfSsfContribution > 0;
        var pitAllowances = new PitAllowances(
            Personal: personalAllow,
            Spouse: emp.HasSpouseAllowance ? spouseAllow : 0m,
            Children: hasDetailed
                ? (emp.ChildAllowanceCount * childAllow) + (emp.SecondAndLaterChildren * childPost2561Bonus)
                : 0m,
            Parents: hasDetailed ? Math.Min(4, emp.ParentAllowanceCount) * parentAllow : 0m,
            LifeInsurance: hasDetailed ? Math.Min(lifeInsCap, emp.LifeInsurancePremium) : 0m,
            ProvidentFund: (hasDetailed ? Math.Min(pvdCap, emp.RmfSsfContribution) : 0m) + annualPvd,
            SocialSecurity: annualSso);
        var pitAllowancesEffective = hasDetailed
            ? pitAllowances
            : pitAllowances with { Children = perDependantLegacy * Math.Max(0, emp.TaxAllowances) };
        return ThaiPitCalculator.Compute(
            taxableIncomeYtd: ytdIncome,
            recurringMonthlyIncome: recurringMonthly,
            remainingPeriodsAfterThis: remainingPeriodsAfterThis,
            allowances: pitAllowancesEffective,
            taxWithheldYtdBeforeThisPeriod: cumulativeTax,
            donationAmount: emp.DonationAmount,
            donationCapPercent: donationCapPct * 100m,
            expenseCap: taxCfg?.Section42TwiCap,
            brackets: brackets);
    }

    private static IEnumerable<Employee> Employees()
    {
        yield return new Employee { BaseSalary = 50_000m };
        yield return new Employee { BaseSalary = 15_000m, TaxAllowances = 2 };
        yield return new Employee { BaseSalary = 120_000m, HasSpouseAllowance = true, ChildAllowanceCount = 2, SecondAndLaterChildren = 1 };
        yield return new Employee { BaseSalary = 80_000m, ParentAllowanceCount = 6, LifeInsurancePremium = 150_000m, RmfSsfContribution = 600_000m, DonationAmount = 40_000m };
        yield return new Employee { BaseSalary = 300_000m, EndDate = new DateTime(2026, 8, 15), TaxAllowances = -1 };
        yield return new Employee { BaseSalary = 40_000m, EndDate = new DateTime(2025, 12, 31) };
        yield return new Employee { BaseSalary = 9_000m, EndDate = new DateTime(2026, 2, 1), DonationAmount = 5_000m };
    }

    private static IEnumerable<TaxRuleConfig?> Configs()
    {
        yield return null;
        yield return new TaxRuleConfig { FiscalYear = 2026 };
        yield return new TaxRuleConfig
        {
            FiscalYear = 2026, PersonalAllowance = 70_000m, SpouseAllowance = 50_000m, ChildAllowance = 25_000m,
            ChildAllowancePost2561 = 55_000m, ParentAllowance = 35_000m, LifeInsuranceCap = 80_000m, PvdCap = 400_000m,
            DonationCapPercent = 12.5m, Section42TwiCap = 90_000m,
        };
    }

    [Fact]
    public void Golden_สูตรใหม่ให้ผลเท่าสูตรinlineเดิมทุกช่อง_ทุกกรณีในตาราง()
    {
        var brackets = new List<PitBracket[]?> { null, ThaiPitCalculator.DefaultBrackets,
            new[] { new PitBracket(200_000m, 0m), new PitBracket(decimal.MaxValue, 0.2m) } };
        var cases = 0;
        foreach (var emp in Employees())
        foreach (var cfg in Configs())
        foreach (var bk in brackets)
        foreach (var month in new[] { 1, 6, 8, 12 })
        foreach (var (ytdExtra, cumTax) in new[] { (0m, 0m), (250_000m, 12_000m), (1_000_000m, 90_000m) })
        foreach (var (sso, pvd) in new[] { (875m, 0m), (0m, 2_500m), (750m, 50_000m) })
        {
            var ytd = ytdExtra + emp.BaseSalary;
            var recurring = Math.Max(0m, emp.BaseSalary + 3_000m);
            var expected = Reference(emp, 2026, month, ytd, recurring, cumTax, sso, 875m, pvd, cfg, bk);
            var actual = PayrollWithholdingTax.Compute(emp, 2026, month,
                taxableIncomeYtd: ytd, recurringMonthlyIncome: recurring, taxWithheldYtdBeforeThisPeriod: cumTax,
                ssoEmployeeThisPeriod: sso, ssoMaxContribution: 875m, pvdEmployeeThisPeriod: pvd,
                taxCfg: cfg, brackets: bk);
            Assert.Equal(expected, actual);           // record struct — เทียบทุกช่อง (ภาษีทั้งปี · ค่าใช้จ่าย · ลดหย่อน · งวดนี้)
            cases++;
        }
        Assert.Equal(7 * 3 * 3 * 4 * 3 * 3, cases);
    }

    [Fact]
    public void ตัวเลขตายตัว_เงินเดือน50000_มกราคม_ทั้งปี20450_งวดนี้1704_17()
    {
        var emp = new Employee { BaseSalary = 50_000m };
        var pit = PayrollWithholdingTax.Compute(emp, 2026, 1,
            taxableIncomeYtd: 50_000m, recurringMonthlyIncome: 50_000m, taxWithheldYtdBeforeThisPeriod: 0m,
            ssoEmployeeThisPeriod: 875m, ssoMaxContribution: 875m, pvdEmployeeThisPeriod: 0m,
            taxCfg: null, brackets: null);
        Assert.Equal(600_000m, pit.EstimatedAnnualIncome);
        Assert.Equal(100_000m, pit.ExpenseDeduction);          // §42ทวิ 50% ไม่เกิน 100,000
        Assert.Equal(70_500m, pit.TotalAllowances);            // ส่วนตัว 60,000 + ปกส. 875×12
        Assert.Equal(20_450m, pit.EstimatedAnnualTax);
        Assert.Equal(1_704.17m, pit.WithholdingThisPeriod);    // 20,450 ÷ 12 ปัด AwayFromZero
    }

    [Fact]
    public void ลาออกเดือนสิงหา_งวดที่เหลือหลังมิถุนายนคือ2_ปีอื่นไม่นับ()
    {
        Assert.Equal(2, PayrollWithholdingTax.RemainingPeriodsAfter(2026, 6, new DateTime(2026, 8, 31)));
        Assert.Equal(0, PayrollWithholdingTax.RemainingPeriodsAfter(2026, 9, new DateTime(2026, 8, 31)));
        Assert.Equal(6, PayrollWithholdingTax.RemainingPeriodsAfter(2026, 6, new DateTime(2027, 3, 31)));
        Assert.Equal(11, PayrollWithholdingTax.RemainingPeriodsAfter(2026, 1, null));
    }

    [Fact]
    public void ยอดสะสมงวดก่อน_ฐานภาษีใช้TaxableGross_ไม่ใช่Gross_และแถวเก่าTaxable0ตกกลับGross()
    {
        var prior = PayrollWithholdingTax.PriorYtd(new[]
        {
            new PayrollDetail { GrossIncome = 52_000m, TaxableGross = 50_000m, WithholdingTax = 1_700m },   // มีสวัสดิการยกเว้น 2,000
            new PayrollDetail { GrossIncome = 50_000m, TaxableGross = 0m, WithholdingTax = 1_704.17m },     // แถวเก่าก่อนมี TaxableGross
        });
        Assert.Equal(102_000m, prior.Income);
        Assert.Equal(100_000m, prior.TaxBase);
        Assert.Equal(3_404.17m, prior.Tax);

        var none = PayrollWithholdingTax.PriorYtd(null);
        Assert.Equal(0m, none.Income);
        Assert.Equal(0m, none.TaxBase);
        Assert.Equal(0m, none.Tax);
    }

    [Fact]
    public void ยอดรายการเงินเดือน_Fixedคงที่_Percentageคิดจากเงินเดือน_และถังประจำแยกจากโบนัส()
    {
        var fixedItem = new PayrollItem { Code = "POS", CalculationType = "Fixed", FixedAmount = 2_000m };
        var pctItem = new PayrollItem { Code = "COL", CalculationType = "Percentage", Percentage = 10m };
        var bonusItem = new PayrollItem { Code = "BONUS", CalculationType = "Fixed", FixedAmount = 5_000m };
        Assert.Equal(2_000m, PayrollWithholdingTax.ItemAmount(fixedItem, 30_000m));
        Assert.Equal(3_000m, PayrollWithholdingTax.ItemAmount(pctItem, 30_000m));
        Assert.Equal(0m, PayrollWithholdingTax.ItemAmount(new PayrollItem { CalculationType = "Fixed" }, 30_000m));

        var buckets = PayrollWithholdingTax.ItemBuckets(new[]
        {
            (Item: fixedItem, Amount: 2_000m), (Item: pctItem, Amount: 3_000m), (Item: bonusItem, Amount: 5_000m),
        });
        Assert.Equal(5_000m, buckets.RecurringAllowance);      // ฉายไปงวดที่เหลือ
        Assert.Equal(5_000m, buckets.Bonus);                   // ไม่ฉาย
    }
}
