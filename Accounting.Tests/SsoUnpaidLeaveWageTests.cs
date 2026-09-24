using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **D-02 — ฐาน ปกส./กองทุนเงินทดแทน ต้องหัก "ลาไม่รับค่าจ้าง"** (คำตัดสินเจ้าของ #35 รอบ 193)
///
/// <para>═══ บั๊กที่รายงาน (report-D-payroll-sso-wht.md D-02) ═══ เงินเดือน 20,000 ลาไม่รับ
/// ค่าจ้างทั้งเดือน → แถวในรอบแสดง <b>รายได้ 0 · หัก ปกส. 875 · สุทธิ −875</b> และ
/// "✏️ แก้ยอด" ซ่อมไม่ได้เพราะด่าน "ยอดสุทธิติดลบ" · ไฟล์ สปส.1-10 ประกาศค่าจ้าง 17,500
/// ในเดือนที่ไม่ได้จ่ายค่าจ้างเลย</para>
///
/// <para>เทสต์เรียก <c>SsoWageBase.ForPeriod</c> — <b>ฟังก์ชันเดียวกับที่</b> <c>PayrollService.CalculatePayrollAsync</c>
/// เรียก (รอบ 193 M2: เดิมประกอบสูตรเองในเทสต์ ⇒ ถอดการแก้ใน service แล้วยังเขียว) ·
/// สูตร "เดิม" ใน <see cref="Legacy"/> ยกมาจาก <c>git show 58203b2:Accounting/Services/Implementations/PayrollService.cs</c>
/// บรรทัด 1962-1979 (<c>Clamp(GrossWage(proratedBaseSalary, ssoWageAllowances))</c>) ไม่ใช่เขียนจากความจำ</para>
///
/// <para>สองครึ่งตามกฎเหล็ก #4 H: (1) เคสลาไม่รับค่าจ้างกลับมาถูก (2) พนักงานที่ไม่มีลาไม่รับ
/// ค่าจ้าง <b>ยอดไม่ขยับแม้สตางค์เดียว</b> — ครึ่งที่สองคือสิ่งที่พิสูจน์ว่าไม่ได้ "ปิดด่านทิ้ง"</para>
/// </summary>
public class SsoUnpaidLeaveWageTests
{
    // ปี 2569 (2026): เพดาน 17,500 · 5% · สมทบสูงสุด 875 ต่อฝ่าย
    private const decimal Ceiling = 17_500m;
    private const decimal Rate = 0.05m;
    private const decimal MaxContribution = 875m;

    private sealed record Row(decimal Wage, decimal Base, decimal Employee, decimal Employer,
        decimal WorkersComp, decimal Gross, decimal Net);

    /// <summary>ส่วนของ CalculatePayrollAsync ที่เกี่ยวกับ ปกส./เงินทดแทน/สุทธิ — ลำดับเดียวกับ service</summary>
    private static Row Current(decimal proratedSalary, decimal leaveDeduction,
        decimal wageAllowances = 0m, decimal otherIncome = 0m, decimal wcRatePercent = 0.2m)
    {
        var taxableGross = proratedSalary - leaveDeduction + wageAllowances + otherIncome;
        var gross = taxableGross;   // ไม่มีสวัสดิการยกเว้นภาษีในเคสทดสอบ
        // ★ รอบ 193 (ฝ่ายค้าน M2): เรียก **ตัวประกอบสูตรตัวเดียวกับที่ service เรียก** (SsoWageBase.ForPeriod)
        //   ไม่ประกอบ GrossWage/SalaryPaidThisPeriod/PeriodBase เองในเทสต์ — เดิมประกอบเอง ⇒ ถอดการหักลาใน
        //   service ทิ้งแล้วเทสต์ยังเขียว · checker tools/required_call_site_check.py ล็อกว่า
        //   CalculatePayrollAsync ยังเรียก ForPeriod และไม่ประกอบสูตรเองซ้ำ
        var a = SsoWageBase.ForPeriod(proratedSalary, leaveDeduction, wageAllowances,
            totalPaidThisPeriod: gross, subjectToSso: true,
            ceiling: Ceiling, rate: Rate, maxContribution: MaxContribution,
            employerRate: Rate, employerMaxContribution: MaxContribution);
        var wc = WorkersCompensationBase.Contribution(a.StatutoryWage, wcRatePercent);
        return new Row(a.StatutoryWage, a.BaseWage, a.Employee, a.Employer, wc, gross, gross - a.Employee);
    }

    /// <summary>สูตรก่อนแก้ (58203b2) — ฐานไม่หักลา และไม่มีกรณี "ไม่ได้จ่ายเลย = 0"</summary>
    private static Row Legacy(decimal proratedSalary, decimal leaveDeduction,
        decimal wageAllowances = 0m, decimal otherIncome = 0m, decimal wcRatePercent = 0.2m)
    {
        var gross = proratedSalary - leaveDeduction + wageAllowances + otherIncome;
        var wage = SsoWageBase.GrossWage(proratedSalary, wageAllowances);
        var baseWage = SsoWageBase.Clamp(wage, Ceiling);
        var ee = SsoWageBase.Contribution(baseWage, Rate, MaxContribution);
        var er = SsoWageBase.Contribution(baseWage, Rate, MaxContribution);
        var wc = WorkersCompensationBase.Contribution(wage, wcRatePercent);
        return new Row(wage, baseWage, ee, er, wc, gross, gross - ee);
    }

    /// <summary>หักลาไม่รับค่าจ้างตามสูตรใน service: round(เงินเดือน × วันลา ÷ วันในเดือน, 2)</summary>
    private static decimal LeaveDeduction(decimal salary, decimal unpaidDays, int daysInMonth)
        => Math.Round(salary * unpaidDays / daysInMonth, 2, MidpointRounding.AwayFromZero);

    // ═══════════ ครึ่งที่ 1 — เคสที่พังต้องกลับมาถูก ═══════════

    [Fact]
    public void ลาไม่รับค่าจ้างทั้งเดือน_สูตรเดิมให้ตัวเลขตรงกับที่รายงาน_875_สุทธิติดลบ()
    {
        // reproduce ก่อนแก้ (กฎเหล็ก #4 G): ตัวเลขต้องตรงกับที่ผู้ตรวจรายงานเป๊ะ
        var old = Legacy(20_000m, LeaveDeduction(20_000m, 30, 30));
        Assert.Equal(0m, old.Gross);
        Assert.Equal(17_500m, old.Base);
        Assert.Equal(875m, old.Employee);
        Assert.Equal(-875m, old.Net);
    }

    [Fact]
    public void ลาไม่รับค่าจ้างทั้งเดือน_ปกส_เป็น0_สุทธิไม่ติดลบ()
    {
        var r = Current(20_000m, LeaveDeduction(20_000m, 30, 30));
        Assert.Equal(0m, r.Gross);
        Assert.Equal(0m, r.Wage);
        Assert.Equal(0m, r.Base);          // ไม่ใช่ 1,650 — ไม่เสกค่าจ้างให้เดือนที่ไม่ได้จ่าย
        Assert.Equal(0m, r.Employee);
        Assert.Equal(0m, r.Employer);
        Assert.Equal(0m, r.WorkersComp);
        Assert.True(r.Net >= 0m);
    }

    [Fact]
    public void ลาคลอดวันที่46ถึง98_ส่วนที่นายจ้างไม่จ่ายทั้งเดือน_ปกส_เป็น0()
    {
        // เดือนที่อยู่ในช่วงวันที่ 46–98 ของการลาคลอดทั้งเดือน — service นับเป็นลาไม่รับค่าจ้าง
        var r = Current(30_000m, LeaveDeduction(30_000m, 31, 31));
        Assert.Equal(0m, r.Employee);
        Assert.Equal(0m, r.Net);
    }

    [Fact]
    public void ลาไม่รับค่าจ้างครึ่งเดือน_ฐานคือเงินเดือนที่จ่ายจริง()
    {
        // 20,000 ลา 15/30 วัน → จ่ายจริง 10,000 → สมทบ 500 (เดิมคิดจาก 17,500 = 875)
        var r = Current(20_000m, LeaveDeduction(20_000m, 15, 30));
        Assert.Equal(10_000m, r.Wage);
        Assert.Equal(500m, r.Employee);
        Assert.Equal(875m, Legacy(20_000m, LeaveDeduction(20_000m, 15, 30)).Employee);
        // เงินทดแทน 0.2% ก็ต้องตามฐานจริง: 10,000 × 0.2% = 20
        Assert.Equal(20m, r.WorkersComp);
    }

    [Fact]
    public void ค่าจ้างที่จ่ายจริงต่ำกว่า1650_ยังใช้ขั้นต่ำ1650ตาม_ม33()
    {
        // 20,000 ลา 28/30 วัน → จ่ายจริง 1,333.33 > 0 ⇒ ขั้นต่ำ 1,650 ยังใช้ (ไม่ใช่ 0)
        var r = Current(20_000m, LeaveDeduction(20_000m, 28, 30));
        Assert.Equal(1_333.33m, r.Wage);
        Assert.Equal(1_650m, r.Base);
        Assert.Equal(82.50m, r.Employee);
        Assert.True(r.Net > 0m);
    }

    [Fact]
    public void ลาเกินเงินเดือนที่เฉลี่ยแล้ว_ไม่กินเบี้ยเลี้ยงที่เป็นค่าจ้าง()
    {
        // เข้างานกลางเดือน (เฉลี่ยได้ 10,000) แต่ยอดหักลาคิดจากเงินเดือนเต็ม 13,333.33
        // ⇒ ส่วนเงินเดือนเป็น 0 ไม่ติดลบ — เบี้ยเลี้ยงค่าจ้าง 2,000 ยังอยู่ในฐานครบ
        Assert.Equal(0m, SsoWageBase.SalaryPaidThisPeriod(10_000m, 13_333.33m));
        var r = Current(10_000m, 13_333.33m, wageAllowances: 2_000m);
        Assert.Equal(2_000m, r.Wage);
    }

    // ═══════════ ครึ่งที่ 2 — ใบที่ถูกอยู่แล้วต้องไม่ขยับแม้สตางค์เดียว ═══════════

    public static IEnumerable<object[]> NoUnpaidLeave() => new[]
    {
        // prorated salary · wage allowances · other income (commission/OT ที่ไม่เข้าฐาน)
        new object[] { 20_000m, 0m, 0m },
        new object[] { 14_093.55m, 0m, 0m },     // ไม่ลงตัว 5% — การปัดเศษต้องเท่าเดิม
        new object[] { 17_500m, 0m, 0m },        // ชนเพดานพอดี
        new object[] { 150_000m, 0m, 0m },       // เกินเพดาน
        new object[] { 1_000m, 0m, 0m },         // ต่ำกว่าขั้นต่ำ → 1,650
        new object[] { 1_650m, 0m, 0m },
        new object[] { 14_000m, 3_000m, 0m },    // เบี้ยเลี้ยงที่ติ๊กเป็นค่าจ้าง (Q1)
        new object[] { 6_451.61m, 0m, 0m },      // เข้างานกลางเดือน (D-S3 เฉลี่ยแล้ว)
        new object[] { 0m, 0m, 5_000m },         // เงินเดือนฐาน 0 แต่มีคอมมิชชัน → ยังคิดขั้นต่ำ 1,650 เหมือนเดิม
        new object[] { 0m, 2_000m, 0m },
        new object[] { 25_000m, 0m, 12_345.67m },
    };

    [Theory]
    [MemberData(nameof(NoUnpaidLeave))]
    public void ไม่มีลาไม่รับค่าจ้าง_ทุกตัวเลขเท่าสูตรเดิมทุกสตางค์(
        decimal prorated, decimal wageAllowances, decimal otherIncome)
    {
        var now = Current(prorated, 0m, wageAllowances, otherIncome);
        var old = Legacy(prorated, 0m, wageAllowances, otherIncome);
        Assert.Equal(old, now);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(1.0)]
    public void ไม่มีลาไม่รับค่าจ้าง_เงินทดแทนเท่าเดิมทุกอัตรา(double ratePercent)
    {
        var rate = (decimal)ratePercent;
        foreach (var salary in new[] { 9_000m, 20_000m, 35_000m })
            Assert.Equal(Legacy(salary, 0m, wcRatePercent: rate).WorkersComp,
                Current(salary, 0m, wcRatePercent: rate).WorkersComp);
    }

    [Fact]
    public void ไม่อยู่ในระบบประกันสังคม_สมทบ0แต่ค่าจ้างยังคืนให้กองทุนเงินทดแทน()
    {
        var a = SsoWageBase.ForPeriod(20_000m, 0m, 0m, 20_000m, subjectToSso: false,
            Ceiling, Rate, MaxContribution, Rate, MaxContribution);
        Assert.Equal(20_000m, a.StatutoryWage);
        Assert.Equal(0m, a.BaseWage);
        Assert.Equal(0m, a.Employee);
        Assert.Equal(0m, a.Employer);
    }
}
