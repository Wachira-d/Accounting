using Accounting.Helpers;
using Accounting.Models.DTOs.Payroll;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวเติมยอดรายคนตัวเดียวของ ✏️ แก้ยอด และ ➕ เพิ่มพนักงานเข้ารอบ (รอบ 200 ทีม PR1) — ย้ายจาก inline ใน
/// <c>UpdatePayrollDetailAsync</c> คำต่อคำ · เทสต์ล็อกทั้งทิศ "ปฏิเสธ" (ไม่ระบุภาษี · สุทธิติดลบ) และทิศ "ผ่านแล้วตัวเลขถูก"
/// (ปกส. เพดาน/ฐานขั้นต่ำ/ฐาน 0 · คงส่วนยกเว้นภาษี D-D1 · แก้แค่รายการหักไม่ต้องระบุภาษี)
/// </summary>
public class PayrollDetailAmountsTests
{
    // ม.33 ปี 2026 สมมติ: เพดานฐาน 15,000 · 5% ทั้งสองฝั่ง · เพดานสมทบ 750
    private static readonly (decimal MaxBase, decimal Rate, decimal EmployerRate, decimal MaxContribution, decimal EmployerMaxContribution)
        Sso = (15_000m, 0.05m, 0.05m, 750m, 750m);

    [Fact]
    public void แถวใหม่_ฐานเกินเพดาน_สมทบสองฝั่งชนเพดาน750_และสุทธิถูก()
    {
        var d = new PayrollDetail();
        PayrollDetailAmounts.Apply(d, new UpdatePayrollDetailRequest(
            SocialSecurityBase: 30_000m, BaseSalary: 30_000m, WithholdingTax: 1_000m), Sso);

        Assert.Equal(30_000m, d.SocialSecurityBase);          // เก็บค่าจ้างจริง (ไม่ cap) — ไฟล์ สปส.1-10 ให้กรอกค่าจ้างจริง
        Assert.Equal(750m, d.SocialSecurityEmployee);
        Assert.Equal(750m, d.SocialSecurityEmployer);
        Assert.Equal(30_000m, d.GrossIncome);
        Assert.Equal(30_000m, d.TaxableGross);                 // แถวใหม่: ไม่มีส่วนยกเว้น ⇒ ฐานภาษี = รายได้รวม
        Assert.Equal(1_750m, d.TotalDeductions);
        Assert.Equal(28_250m, d.NetPay);
    }

    [Fact]
    public void ฐาน15000พอดี_สมทบ750()
    {
        var d = new PayrollDetail();
        PayrollDetailAmounts.Apply(d, new UpdatePayrollDetailRequest(
            SocialSecurityBase: 15_000m, BaseSalary: 15_000m, WithholdingTax: 0m), Sso);
        Assert.Equal(750m, d.SocialSecurityEmployee);
        Assert.Equal(750m, d.SocialSecurityEmployer);
        Assert.Equal(14_250m, d.NetPay);
    }

    [Fact]
    public void ฐานต่ำกว่าขั้นต่ำ_คิดจาก1650()
    {
        var d = new PayrollDetail();
        PayrollDetailAmounts.Apply(d, new UpdatePayrollDetailRequest(
            SocialSecurityBase: 1_000m, BaseSalary: 1_000m, WithholdingTax: 0m), Sso);
        Assert.Equal(82.50m, d.SocialSecurityEmployee);
        Assert.Equal(82.50m, d.SocialSecurityEmployer);
    }

    [Fact]
    public void ฐาน0_ไม่อยู่ในมาตรา33_สมทบเป็น0ทั้งสองฝั่ง()
    {
        var d = new PayrollDetail();
        PayrollDetailAmounts.Apply(d, new UpdatePayrollDetailRequest(
            SocialSecurityBase: 0m, BaseSalary: 20_000m, WithholdingTax: 500m), Sso);
        Assert.Equal(0m, d.SocialSecurityBase);
        Assert.Equal(0m, d.SocialSecurityEmployee);
        Assert.Equal(0m, d.SocialSecurityEmployer);
        Assert.Equal(19_500m, d.NetPay);
    }

    [Fact]
    public void ระบุรายได้แต่ไม่ระบุภาษี_ปฏิเสธ()
    {
        var d = new PayrollDetail();
        var ex = Assert.Throws<BusinessRuleException>(() => PayrollDetailAmounts.Apply(d,
            new UpdatePayrollDetailRequest(SocialSecurityBase: 15_000m, BaseSalary: 15_000m), Sso));
        Assert.Contains("ภาษีหัก ณ ที่จ่าย", ex.Message);
        Assert.Contains("§54", ex.Message);
    }

    [Fact]
    public void สุทธิติดลบ_ปฏิเสธ()
    {
        var d = new PayrollDetail();
        var ex = Assert.Throws<InvalidOperationException>(() => PayrollDetailAmounts.Apply(d,
            new UpdatePayrollDetailRequest(SocialSecurityBase: 0m, BaseSalary: 5_000m,
                WithholdingTax: 0m, OtherDeductions: 6_000m), Sso));
        Assert.Contains("ยอดสุทธิติดลบ", ex.Message);
    }

    [Fact]
    public void แก้ยอด_คงส่วนรายได้ยกเว้นภาษีเดิม_DD1()
    {
        // แถวที่ engine แยกค่ารักษาพยาบาล 2,000 ออกจากฐานภาษีไว้
        var d = new PayrollDetail { BaseSalary = 28_000m, OtherIncome = 2_000m, GrossIncome = 30_000m, TaxableGross = 28_000m };
        PayrollDetailAmounts.Apply(d, new UpdatePayrollDetailRequest(Bonus: 1_000m, WithholdingTax: 900m), Sso);
        Assert.Equal(31_000m, d.GrossIncome);
        Assert.Equal(29_000m, d.TaxableGross);
    }

    [Fact]
    public void แก้แค่รายการหัก_ไม่ต้องระบุภาษีใหม่()
    {
        var d = new PayrollDetail { BaseSalary = 20_000m, GrossIncome = 20_000m, TaxableGross = 20_000m, WithholdingTax = 300m };
        PayrollDetailAmounts.Apply(d, new UpdatePayrollDetailRequest(OtherDeductions: 500m), Sso);
        Assert.Equal(300m, d.WithholdingTax);
        Assert.Equal(800m, d.TotalDeductions);
        Assert.Equal(19_200m, d.NetPay);
    }

    [Fact]
    public void ยอดรวมรอบ_ไม่นับแถวที่ถูกเอาออก_และนับจำนวนคนใหม่()
    {
        var run = new PayrollRun { EmployeeCount = 3 };
        run.Details.Add(new PayrollDetail { GrossIncome = 10_000m, NetPay = 9_000m, WithholdingTax = 100m,
            SocialSecurityEmployee = 500m, SocialSecurityEmployer = 500m, TotalDeductions = 1_000m, WorkersCompensation = 20m });
        run.Details.Add(new PayrollDetail { GrossIncome = 20_000m, NetPay = 18_000m, WithholdingTax = 1_250m,
            SocialSecurityEmployee = 750m, SocialSecurityEmployer = 750m, TotalDeductions = 2_000m, WorkersCompensation = 40m });
        run.Details.Add(new PayrollDetail { GrossIncome = 50_000m, NetPay = 45_000m, IsDeleted = true,
            SocialSecurityEmployee = 750m, SocialSecurityEmployer = 750m, TotalDeductions = 5_000m, WorkersCompensation = 40m });

        PayrollDetailAmounts.RecomputeRunTotals(run);

        Assert.Equal(2, run.EmployeeCount);
        Assert.Equal(30_000m, run.TotalGrossSalary);
        Assert.Equal(27_000m, run.TotalNetPay);
        Assert.Equal(1_350m, run.TotalWithholdingTax);
        Assert.Equal(1_250m, run.TotalSocialSecurityEmployee);
        Assert.Equal(1_250m, run.TotalSocialSecurityEmployer);
        Assert.Equal(3_000m, run.TotalDeductions);
        Assert.Equal(60m, run.TotalWorkersCompensation);
    }
}
