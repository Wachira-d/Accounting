using Accounting.Models.DTOs.Payroll;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// ตัวเติมยอดรายคนตัวเดียวของรอบเงินเดือน — ใช้ร่วมระหว่าง "✏️ แก้ยอด" (<c>UpdatePayrollDetailAsync</c>)
/// และ "➕ เพิ่มพนักงานเข้ารอบ" (<c>AddPayrollDetailAsync</c>)
///
/// <para>═══ ที่มา (รอบ 200 ทีม PR1) ═══ ตรรกะนี้เคยอยู่ inline ในเมธอดแก้ยอด · พอมีทางเข้าที่สองที่สร้างแถวใหม่
/// ถ้าเขียนสูตร ปกส./ภาษี/สุทธิซ้ำอีกชุด = สองสูตรที่ drift แน่นอน (ไฟล์เดียวกันเพิ่งยุบสูตรภาษีซ้ำทิ้งในรอบ
/// D-T1..T4) ⇒ ย้ายมาที่นี่ <b>คำต่อคำ</b> (พฤติกรรม/ข้อความ error ของแก้ยอดเดิมไม่เปลี่ยน) แล้วให้สองทางเข้าเรียกตัวนี้</para>
///
/// <para>กติกาที่ล็อกไว้ (เทสต์ <c>PayrollDetailAmountsTests</c>):
/// · ปกส. ฐานเป็นตัวตั้ง สองฝั่งเป็นผลลัพธ์ (ม.33 ฐานเดียวกัน · เพดานตามปี · ฐาน 0 = ไม่อยู่ใน ม.33)
/// · รายได้เปลี่ยนแต่ไม่ระบุภาษี ⇒ ปฏิเสธ (§50/§54 — ไม่คิดภาษีเองเพราะต้องใช้บริบททั้งปี)
/// · ส่วนรายได้ยกเว้นภาษีเดิมคงไว้ (D-D1) · สุทธิติดลบ ⇒ ปฏิเสธ</para>
/// </summary>
public static class PayrollDetailAmounts
{
    /// <summary>นำคำขอไปใส่แถว + คิด ปกส. จากฐาน + บังคับระบุภาษีเมื่อรายได้เปลี่ยน + รวม Gross/หัก/สุทธิ
    /// (เฉพาะช่องที่ <c>HasValue</c> · ค่าติดลบปัดเป็น 0) · โยน <see cref="BusinessRuleException"/> เมื่อแก้รายได้
    /// โดยไม่ระบุภาษี และ <see cref="InvalidOperationException"/> เมื่อสุทธิติดลบ (ข้อความเดิมของแก้ยอด)
    /// · <paramref name="sso"/> = อัตรา/เพดานของงวดนั้นจาก <c>GetSsoParamsAsync</c> (ห้ามฝังเลขเอง)</summary>
    public static void Apply(PayrollDetail d, UpdatePayrollDetailRequest req,
        (decimal MaxBase, decimal Rate, decimal EmployerRate, decimal MaxContribution, decimal EmployerMaxContribution) sso)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(req);

        static decimal Pos(decimal v) => v < 0 ? 0 : v;

        // ส่วนของรายได้ที่ **ไม่ต้องเสียภาษี** (สวัสดิการยกเว้น เช่นค่ารักษาพยาบาล)
        // — เก็บไว้ก่อนแก้ยอด เพื่อคงไว้หลังรวมรายได้ใหม่ (ผลตรวจ D-D1)
        // (แถวใหม่: Gross = Taxable = 0 ⇒ ส่วนยกเว้น 0 ⇒ TaxableGross = GrossIncome)
        var nonTaxablePortion = Math.Max(0m, d.GrossIncome - d.TaxableGross);

        // รายได้เปลี่ยนไหม — ใช้ตัดสินว่าต้องคิดภาษีใหม่หรือเปล่า (ผลตรวจ D-D2)
        var incomeChanged = req.BaseSalary.HasValue || req.OvertimePay.HasValue
            || req.Allowances.HasValue || req.Commission.HasValue
            || req.Bonus.HasValue || req.OtherIncome.HasValue;

        if (req.BaseSalary.HasValue) d.BaseSalary = Pos(req.BaseSalary.Value);
        if (req.OvertimePay.HasValue) d.OvertimePay = Pos(req.OvertimePay.Value);
        if (req.Allowances.HasValue) d.Allowances = Pos(req.Allowances.Value);
        if (req.Commission.HasValue) d.Commission = Pos(req.Commission.Value);
        if (req.Bonus.HasValue) d.Bonus = Pos(req.Bonus.Value);
        if (req.OtherIncome.HasValue) d.OtherIncome = Pos(req.OtherIncome.Value);
        // ── ประกันสังคม: **ฐานเป็นตัวตั้ง ทั้งสองฝั่งเป็นผลลัพธ์** ──
        // เดิมแก้ฝั่งลูกจ้างได้อิสระ ฝั่งนายจ้างค้างค่าเดิม ⇒ ยอดนำส่งสองฝั่ง
        // ไม่เท่ากัน (เคสจริง: ลูกจ้าง 4,381 vs นายจ้าง 4,403 ต่างกัน 22 = ยอด
        // ของพนักงานที่ถูกแก้พอดี) ทั้งที่ ม.33 ใช้ฐานเดียวกันทั้งคู่
        // และไฟล์ สปส.1-10 ก็ประกาศค่าจ้างที่ 5% ไม่ลงตัวกับเงินสมทบ
        if (req.SocialSecurityBase.HasValue)
        {
            // ผู้ใช้ระบุ "ค่าจ้างที่ใช้เป็นฐาน" มาเอง (ม.5: เบี้ยเลี้ยง/ค่าน้ำมัน
            // เหมาจ่ายไม่ใช่ค่าจ้าง) → คิดทั้งสองฝั่งใหม่จากฐานนั้น
            d.SocialSecurityBase = Pos(req.SocialSecurityBase.Value);
            if (d.SocialSecurityBase > 0)
            {
                var clamped = SsoWageBase.Clamp(d.SocialSecurityBase, sso.MaxBase);
                d.SocialSecurityEmployee = SsoWageBase.Contribution(
                    clamped, sso.Rate, sso.MaxContribution);
                // ฝั่งนายจ้างคิดจาก "ยอดลูกจ้าง" ตามสัดส่วนอัตรา ไม่ใช่คำนวณจากฐาน
                // ใหม่อีกรอบ — อัตราเท่ากันต้องได้ยอดเท่ากันเป๊ะ ไม่ต่างกันด้วยเศษปัด
                d.SocialSecurityEmployer = SsoWageBase.EmployerFrom(
                    d.SocialSecurityEmployee, sso.Rate,
                    sso.EmployerRate, sso.EmployerMaxContribution);
            }
            else
            {
                d.SocialSecurityEmployee = 0m;
                d.SocialSecurityEmployer = 0m;
            }
        }
        else if (req.SocialSecurityEmployee.HasValue)
        {
            // แก้ "ยอดสมทบ" ตรง ๆ (ทางเดิมที่ผู้ใช้คุ้น) → ย้อนกลับไปหาฐานที่ทำให้
            // ยอดนั้นถูกต้อง แล้วให้ฝั่งนายจ้างตามฐานเดียวกัน — เว้นแต่ผู้ใช้ระบุ
            // ฝั่งนายจ้างมาเองในคำขอเดียวกัน (กรณีอัตราสองฝั่งไม่เท่ากันจริง เช่น
            // ประกาศลดอัตราชั่วคราวช่วงโควิด ซึ่งกฎหมายเคยกำหนดคนละอัตรา)
            d.SocialSecurityEmployee = Pos(req.SocialSecurityEmployee.Value);
            d.SocialSecurityBase = SsoWageBase.Resolve(
                0m, d.SocialSecurityEmployee, d.GrossIncome,
                sso.Rate, sso.MaxContribution);
            if (!req.SocialSecurityEmployer.HasValue)
                d.SocialSecurityEmployer = SsoWageBase.EmployerFrom(
                    d.SocialSecurityEmployee, sso.Rate,
                    sso.EmployerRate, sso.EmployerMaxContribution);
        }
        if (req.SocialSecurityEmployer.HasValue) d.SocialSecurityEmployer = Pos(req.SocialSecurityEmployer.Value);
        if (req.WithholdingTax.HasValue) d.WithholdingTax = Pos(req.WithholdingTax.Value);
        if (req.ProvidentFundEmployee.HasValue) d.ProvidentFundEmployee = Pos(req.ProvidentFundEmployee.Value);
        if (req.LoanDeduction.HasValue) d.LoanDeduction = Pos(req.LoanDeduction.Value);
        if (req.OtherDeductions.HasValue) d.OtherDeductions = Pos(req.OtherDeductions.Value);

        // รวมยอดใหม่ — หักฝั่งลูกจ้างเท่านั้นที่กระทบ net (ปกส./PVD นายจ้าง = cost บริษัท)
        d.GrossIncome = d.BaseSalary + d.OvertimePay + d.Allowances + d.Commission + d.Bonus + d.OtherIncome;

        // ★ เดิมเขียน `d.TaxableGross = d.GrossIncome` ทับทุกครั้ง (ผลตรวจ D-D1)
        // ⇒ ส่วนที่ยกเว้นภาษี (ค่ารักษาพยาบาล ฯลฯ ที่ engine แยกไว้ตอนคำนวณ)
        // **หายทุกครั้งที่ HR แก้ยอดช่องใด ๆ** แม้แก้ช่องที่ไม่เกี่ยวกันเลย
        // ⇒ ฐานภาษีใน ภ.ง.ด.1 และ 50 ทวิ สูงกว่าจริง = พนักงานถูกหักเกิน
        // คงสัดส่วนที่ยกเว้นไว้ (clamp ไม่ให้ติดลบเมื่อรายได้ใหม่น้อยกว่าส่วนยกเว้น)
        d.TaxableGross = Math.Max(0m, d.GrossIncome - Math.Min(nonTaxablePortion, d.GrossIncome));

        // ★ แก้รายได้แล้วภาษีต้องเปลี่ยนตาม (ผลตรวจ D-D2)
        //
        // เดิมแก้โบนัส/OT ได้แต่ `WithholdingTax` ค้างค่าเดิม ⇒ หักน้อยกว่าที่ควร
        // แล้วผู้จ่ายรับผิดตาม §54 — และเงียบสนิทเพราะยอดสุทธิ "ดูสมเหตุสมผล"
        //
        // **ไม่คำนวณใหม่ที่นี่** เพราะสูตรภาษีต้องใช้บริบทของทั้งปี (รายได้สะสม ·
        // ลดหย่อนรายช่อง · ตารางขั้นภาษีของบริษัท · งวดที่เหลือ) ที่เมธอดนี้ไม่มี
        // — คัดลอกสูตรมาที่นี่ = อัลกอริทึมภาษีชุดที่สองที่จะ drift แน่นอน
        // (defect class ที่ทั้งไฟล์นี้เพิ่งยุบทิ้งไปในรอบ D-T1..T4)
        // จึง **ล้มดังพร้อมบอกทางไปต่อ** แทนการปล่อยตัวเลขผิดผ่านไปเงียบ ๆ
        if (incomeChanged && !req.WithholdingTax.HasValue)
        {
            throw new BusinessRuleException(
                "แก้ยอดรายได้แล้วต้องระบุภาษีหัก ณ ที่จ่ายใหม่ด้วย — "
                + $"ยอดเดิม {d.WithholdingTax:N2} บาท คิดจากรายได้ก่อนแก้ "
                + "ถ้าปล่อยไว้จะหักน้อย/มากกว่าที่ควร (ภาษีที่หักขาด ผู้จ่ายรับผิดตาม §54). "
                + "ทางแก้: กรอกช่อง \"ภาษีหัก ณ ที่จ่าย\" ในโมดัลเดียวกัน "
                + "หรือกด \"คำนวณเงินเดือน\" ใหม่ทั้งรอบเพื่อให้ระบบคิดภาษีให้ทุกคน");
        }

        d.TotalDeductions = d.SocialSecurityEmployee + d.WithholdingTax + d.ProvidentFundEmployee
            + d.LoanDeduction + d.OtherDeductions;
        d.NetPay = d.GrossIncome - d.TotalDeductions;
        if (d.NetPay < 0)
            throw new InvalidOperationException(
                $"ยอดสุทธิติดลบ ({d.NetPay:N2}) — รายการหักรวมมากกว่ารายได้ ตรวจสอบยอดอีกครั้ง");
        d.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>รวมยอดระดับรอบใหม่จากแถวรายคนที่ยังไม่ถูกลบ (แก้ยอด · เพิ่ม · เอาออก ใช้ตัวเดียวกัน) ·
    /// <c>EmployeeCount</c> = จำนวนแถวที่เหลือ · แถวที่ถูก soft-delete ในคำขอเดียวกันยังอยู่ใน
    /// <c>run.Details</c> (EF ตัดออกจาก collection ไม่ได้เพราะ FK บังคับ + Restrict) จึงกรอง <c>!IsDeleted</c> ที่นี่</summary>
    public static void RecomputeRunTotals(PayrollRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var live = run.Details.Where(x => !x.IsDeleted).ToList();
        run.TotalGrossSalary = live.Sum(x => x.GrossIncome);
        run.TotalWithholdingTax = live.Sum(x => x.WithholdingTax);
        run.TotalSocialSecurityEmployee = live.Sum(x => x.SocialSecurityEmployee);
        run.TotalSocialSecurityEmployer = live.Sum(x => x.SocialSecurityEmployer);
        run.TotalProvidentFundEmployee = live.Sum(x => x.ProvidentFundEmployee);
        run.TotalProvidentFundEmployer = live.Sum(x => x.ProvidentFundEmployer);
        run.TotalWorkersCompensation = live.Sum(x => x.WorkersCompensation);
        run.TotalNetPay = live.Sum(x => x.NetPay);
        run.TotalDeductions = live.Sum(x => x.TotalDeductions);
        run.EmployeeCount = live.Count;
    }
}
