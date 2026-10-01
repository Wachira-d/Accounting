using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// <b>เครื่องคิดภาษีหัก ณ ที่จ่ายเงินเดือนรายคนตัวเดียว</b> (ม.40(1) · §50(1)) — บริบททั้งปีของพนักงานหนึ่งคนในงวดหนึ่ง
/// (รายได้/ภาษีสะสมของงวดก่อน · ค่าลดหย่อน §47 รายช่อง · ตารางขั้นของบริษัท · งวดที่เหลือ) ประกอบเป็นคำขอของ
/// <see cref="ThaiPitCalculator.Compute"/>
///
/// <para>═══ ที่มา (รอบ 201 ทีม PR2 · คำตัดสินข้อ 73) ═══ ส่วนประกอบบริบทภาษีเคยอยู่ inline กลาง
/// <c>PayrollService.CalculatePayrollAsync</c> ⇒ ➕ เพิ่มพนักงาน / ✏️ แก้ยอด ไม่มีทางเรียกได้ จึงบังคับผู้ใช้กรอกภาษีเอง ·
/// ปุ่ม "คำนวณภาษีให้" ต้องให้ผลจาก<b>สูตรเดียวกัน</b> (ห้ามสำเนาสูตรชุดที่สอง — D-T1..T4 เพิ่งยุบสำเนาทิ้ง) ⇒ ย้ายมาที่นี่
/// <b>คำต่อคำ</b> แล้วให้ทั้งการคำนวณรอบและพรีวิวรายคนเรียกตัวนี้ (เทสต์ <c>PayrollWithholdingTaxTests</c> เทียบกับสูตร inline
/// เดิมจาก <c>9c9580e6</c> ทุกบาท) · ค่าคงที่เดิมของ <c>PayrollService</c> (60,000 · 30,000 · 500,000) เปลี่ยนไปอ้างตัวตั้งของ
/// <see cref="ThaiPitCalculator"/> ซึ่งเป็นตัวเลขเดียวกัน</para>
/// </summary>
public static class PayrollWithholdingTax
{
    /// <summary>ยอดสะสมของงวดก่อนในปีเดียวกัน (แถวรายคนของรอบที่ไม่ถูกยกเลิก เดือนก่อนหน้า) —
    /// <see cref="TaxBase"/> = ฐานภาษีของงวดก่อน (D-09 · ไม่ใช่ gross ที่รวมสวัสดิการยกเว้น)</summary>
    public readonly record struct PriorTotals(decimal Income, decimal TaxBase, decimal Tax);

    /// <summary>รวมยอดสะสมของงวดก่อน — ตัวเดียวของเส้นคำนวณรอบ · ➕ เพิ่ม · ✏️ แก้ยอด · พรีวิวภาษี</summary>
    public static PriorTotals PriorYtd(IEnumerable<PayrollDetail>? priorDetails)
    {
        var list = priorDetails?.ToList() ?? new List<PayrollDetail>();
        return new PriorTotals(
            Income: list.Sum(d => d.GrossIncome),
            TaxBase: list.Sum(d => PayrollIncomeBase.PriorTaxBase(d.TaxableGross, d.GrossIncome)),
            Tax: list.Sum(d => d.WithholdingTax));
    }

    /// <summary>ยอดของรายการเงินเดือน (เงินได้/รายการหัก) หนึ่งแถวสำหรับพนักงานหนึ่งคน — Fixed = ยอดคงที่ ·
    /// อื่น ๆ = % ของเงินเดือนฐาน (สูตรเดิมของ <c>CalculatePayrollAsync</c> คำต่อคำ)</summary>
    public static decimal ItemAmount(PayrollItem item, decimal baseSalary)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.CalculationType == "Fixed"
            ? (item.FixedAmount ?? 0)
            : (item.Percentage ?? 0) / 100m * baseSalary;
    }

    /// <summary>ถังลักษณะเงินได้ของรายการเงินเดือน (ผ่าน <see cref="PayrollIncomeNatureRules.Accumulate"/> ตัวเดียว)</summary>
    public static PayrollEarningBuckets ItemBuckets(IEnumerable<(PayrollItem Item, decimal Amount)> earningAmounts)
        => PayrollIncomeNatureRules.Accumulate(
            (earningAmounts ?? Array.Empty<(PayrollItem, decimal)>()).Select(x => (
                x.Item.IncomeNature,
                (string?)x.Item.Code,
                x.Item.IsTaxable,
                x.Amount)));

    /// <summary>งวดที่เหลือ<b>หลัง</b>งวดนี้ — เคารพวันสิ้นสุดการจ้าง (ลาออกกลางปีไม่ถูกประมาณการว่าได้เงินเดือนจนสิ้นปี)</summary>
    public static int RemainingPeriodsAfter(int runYear, int runMonth, DateTime? employmentEnd)
    {
        var lastPayMonth = employmentEnd.HasValue && employmentEnd.Value.Year == runYear
            ? Math.Min(12, Math.Max(runMonth, employmentEnd.Value.Month))
            : 12;
        return Math.Max(0, lastPayMonth - runMonth);
    }

    /// <summary>ค่าลดหย่อน §47/§47ทวิ ของพนักงานหนึ่งคน (ยอดรายปี) — ค่าจากตาราง <c>TaxRuleConfig</c> ของบริษัท×ปี
    /// หรือค่าตั้งต้นของ <see cref="ThaiPitCalculator"/> · ผู้ที่ยังไม่กรอกลดหย่อนรายช่องใช้จำนวนผู้อุปการะแบบเดิม
    /// (<c>TaxAllowances</c> × 30,000)</summary>
    public static PitAllowances Allowances(Employee emp, TaxRuleConfig? taxCfg,
        decimal ssoEmployeeThisPeriod, decimal ssoMaxContribution, decimal pvdEmployeeThisPeriod)
    {
        ArgumentNullException.ThrowIfNull(emp);
        // Annual SSO deduction cap follows the year's ceiling too (12 × monthly max)
        var annualSso = Math.Min(ssoEmployeeThisPeriod * 12m, ssoMaxContribution * 12m);
        var annualPvd = Math.Min(pvdEmployeeThisPeriod * 12m, ThaiPitCalculator.DefaultPvdCap);

        var personalAllow = taxCfg?.PersonalAllowance ?? ThaiPitCalculator.DefaultPersonalAllowance;
        var spouseAllow = taxCfg?.SpouseAllowance ?? ThaiPitCalculator.DefaultSpouseAllowance;
        var childAllow = taxCfg?.ChildAllowance ?? ThaiPitCalculator.DefaultChildAllowance;
        var childPost2561Bonus = (taxCfg?.ChildAllowancePost2561 ?? ThaiPitCalculator.DefaultChildAllowancePost2561) - childAllow;
        var parentAllow = taxCfg?.ParentAllowance ?? ThaiPitCalculator.DefaultParentAllowance;
        var lifeInsCap = taxCfg?.LifeInsuranceCap ?? ThaiPitCalculator.DefaultLifeInsuranceCap;
        var pvdCap = taxCfg?.PvdCap ?? ThaiPitCalculator.DefaultPvdCap;
        var perDependantLegacy = ThaiPitCalculator.DefaultChildAllowance;

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
        return hasDetailed
            ? pitAllowances
            : pitAllowances with { Children = perDependantLegacy * Math.Max(0, emp.TaxAllowances) };
    }

    /// <summary>ภาษีหัก ณ ที่จ่ายของงวดนี้ + ขั้นกลางทั้งหมด (ประมาณการทั้งปี · ค่าใช้จ่าย · ลดหย่อน)
    /// — <b>จุดเรียกเดียว</b>ของ <c>CalculatePayrollAsync</c> และพรีวิว "คำนวณภาษีให้" รายคน</summary>
    /// <param name="taxableIncomeYtd">ฐานภาษีสะสมของงวดก่อน + ฐานภาษีของงวดนี้</param>
    /// <param name="recurringMonthlyIncome">ฐานประจำที่ฉายไปงวดที่เหลือ (เงินเดือนเต็ม + เบี้ยเลี้ยงประจำ)</param>
    /// <param name="taxWithheldYtdBeforeThisPeriod">ภาษีที่หักไปแล้วในงวดก่อนของปีนี้</param>
    /// <param name="brackets">ขั้นภาษีของบริษัท (null = ขั้นตาม §48(1))</param>
    public static PitResult Compute(Employee emp, int runYear, int runMonth,
        decimal taxableIncomeYtd, decimal recurringMonthlyIncome, decimal taxWithheldYtdBeforeThisPeriod,
        decimal ssoEmployeeThisPeriod, decimal ssoMaxContribution, decimal pvdEmployeeThisPeriod,
        TaxRuleConfig? taxCfg, IReadOnlyList<PitBracket>? brackets)
    {
        ArgumentNullException.ThrowIfNull(emp);
        var allowances = Allowances(emp, taxCfg, ssoEmployeeThisPeriod, ssoMaxContribution, pvdEmployeeThisPeriod);
        var donationCapPct = (taxCfg?.DonationCapPercent ?? ThaiPitCalculator.DefaultDonationCapPercent) / 100m;
        return ThaiPitCalculator.Compute(
            taxableIncomeYtd: taxableIncomeYtd,
            recurringMonthlyIncome: recurringMonthlyIncome,
            remainingPeriodsAfterThis: RemainingPeriodsAfter(runYear, runMonth, emp.EndDate),
            allowances: allowances,
            taxWithheldYtdBeforeThisPeriod: taxWithheldYtdBeforeThisPeriod,
            donationAmount: emp.DonationAmount,
            donationCapPercent: donationCapPct * 100m,
            expenseCap: taxCfg?.Section42TwiCap,
            brackets: brackets);
    }
}
