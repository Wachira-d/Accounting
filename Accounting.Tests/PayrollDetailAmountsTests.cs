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

    // ── รอบ 201 ทีม PR2 ──────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyFields_รายได้เปลี่ยนไม่ระบุภาษี_ไม่โยน_ได้ฐานภาษีและปกส_สำหรับพรีวิว_ส่วนApplyยังปฏิเสธ()
    {
        // พรีวิว "คำนวณภาษีให้" ต้องได้ยอดของงวดจากสูตรเดียวกับตอนบันทึก โดยไม่ติดด่าน "ต้องระบุภาษี"
        var preview = new PayrollDetail { BaseSalary = 20_000m, GrossIncome = 22_000m, TaxableGross = 20_000m, WithholdingTax = 300m };
        var changed = PayrollDetailAmounts.ApplyFields(preview,
            new UpdatePayrollDetailRequest(BaseSalary: 30_000m, SocialSecurityBase: 30_000m), Sso);
        Assert.True(changed);
        Assert.Equal(30_000m, preview.GrossIncome);            // 30,000 + ส่วนอื่น 0 (Allowances เดิมไม่มี)
        Assert.Equal(28_000m, preview.TaxableGross);           // คงส่วนยกเว้น 2,000 (D-D1)
        Assert.Equal(750m, preview.SocialSecurityEmployee);
        Assert.Equal(300m, preview.WithholdingTax);             // ไม่แตะภาษีที่ไม่ได้ส่งมา

        // ทิศตรงข้าม: เส้นบันทึกยังปฏิเสธเหมือนเดิม และข้อความชี้ปุ่มคำนวณภาษีให้
        var save = new PayrollDetail { BaseSalary = 20_000m, GrossIncome = 22_000m, TaxableGross = 20_000m, WithholdingTax = 300m };
        var ex = Assert.Throws<BusinessRuleException>(() => PayrollDetailAmounts.Apply(save,
            new UpdatePayrollDetailRequest(BaseSalary: 30_000m, SocialSecurityBase: 30_000m), Sso));
        Assert.Contains("คำนวณภาษีให้", ex.Message);
    }

    [Fact]
    public void ApplyFields_แก้แค่รายการหัก_คืนfalse()
        => Assert.False(PayrollDetailAmounts.ApplyFields(new PayrollDetail(),
            new UpdatePayrollDetailRequest(OtherDeductions: 100m), Sso));

    [Fact]
    public void เงินทดแทน_รอบในระบบ_เปิดใช้และอยู่ในปกส_คิดจากฐาน_เพดาน20000()
    {
        var d = new PayrollDetail { SocialSecurityBase = 30_000m };
        Assert.True(PayrollDetailAmounts.ApplyWorkersCompensation(d, runIsExternalImport: false,
            companyEnabled: true, ratePercent: 0.2m, employeeSubjectToSso: true));
        Assert.Equal(40m, d.WorkersCompensation);              // min(30,000, 20,000) × 0.2%
    }

    [Theory]
    [InlineData(false, 0.2, true, 15_000)]   // บริษัทไม่เปิดใช้
    [InlineData(true, 0, true, 15_000)]      // อัตรา 0
    [InlineData(true, 0.2, false, 15_000)]   // ไม่อยู่ในประกันสังคม
    [InlineData(true, 0.2, true, 0)]         // ฐาน 0
    public void เงินทดแทน_รอบในระบบ_เงื่อนไขไม่ครบ_ตั้งเป็น0_แม้เดิมมีค่า(bool enabled, double rate, bool subject, int ssoBase)
    {
        var d = new PayrollDetail { SocialSecurityBase = ssoBase, WorkersCompensation = 99m };
        Assert.True(PayrollDetailAmounts.ApplyWorkersCompensation(d, false, enabled, (decimal)rate, subject));
        Assert.Equal(0m, d.WorkersCompensation);
    }

    [Fact]
    public void เงินทดแทน_รอบนำเข้า_ไม่แตะ_คืนfalse()
    {
        var d = new PayrollDetail { SocialSecurityBase = 15_000m, WorkersCompensation = 0m };
        Assert.False(PayrollDetailAmounts.ApplyWorkersCompensation(d, runIsExternalImport: true,
            companyEnabled: true, ratePercent: 0.5m, employeeSubjectToSso: true));
        Assert.Equal(0m, d.WorkersCompensation);               // คงศูนย์เท่าแถวนำเข้าอื่น (X3)
        var kept = new PayrollDetail { SocialSecurityBase = 15_000m, WorkersCompensation = 12.34m };
        PayrollDetailAmounts.ApplyWorkersCompensation(kept, true, true, 0.5m, true);
        Assert.Equal(12.34m, kept.WorkersCompensation);        // ไม่แตะค่าเดิม
    }

    [Fact]
    public void YTD_สะสมงวดก่อนบวกงวดนี้_สูตรเดียวกับเส้นคำนวณ()
    {
        var d = new PayrollDetail { GrossIncome = 30_000m, WithholdingTax = 500m };
        PayrollDetailAmounts.SetYtd(d, new PayrollWithholdingTax.PriorTotals(Income: 150_000m, TaxBase: 140_000m, Tax: 2_500m));
        Assert.Equal(180_000m, d.CumulativeIncomeYTD);
        Assert.Equal(3_000m, d.CumulativeTaxYTD);
        PayrollDetailAmounts.SetYtd(d, default);               // พนักงานใหม่ ไม่มีงวดก่อน
        Assert.Equal(30_000m, d.CumulativeIncomeYTD);
        Assert.Equal(500m, d.CumulativeTaxYTD);
    }
}
