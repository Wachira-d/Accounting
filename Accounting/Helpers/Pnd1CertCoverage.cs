using System;

namespace Accounting.Helpers;

/// <summary>ช่องว่างระหว่าง "ภาษีที่หักจากเงินเดือนไปแล้ว" กับ "หนังสือรับรอง
/// 50 ทวิ ที่ออกจริง" ของงวดหนึ่ง</summary>
/// <param name="EmployeeCount">จำนวนคนที่ยังไม่มีใบ (ประมาณจากส่วนต่างจำนวนใบ)</param>
/// <param name="TaxAmount">ยอดภาษีที่ยังไม่มีใบรองรับ</param>
public readonly record struct Pnd1CoverageGap(int EmployeeCount, decimal TaxAmount)
{
    /// <summary>มีช่องว่างที่ต้องเตือน/บล็อกไหม — **ดูทั้งจำนวนใบและยอดเงิน**
    ///
    /// <para>ดูอย่างเดียวไม่พอ: HR ที่แก้ยอดบนใบรับรองรายคนทำให้ "จำนวนใบครบ
    /// แต่ยอดไม่ครบ" ได้ (จำนวนตรง ยอดขาด) และคนที่ไม่มีเลขผู้เสียภาษีทำให้
    /// "ยอดขาดพร้อมจำนวนขาด" ⇒ เกณฑ์ต้องเป็น OR</para></summary>
    public bool Any => EmployeeCount > 0 || TaxAmount > Pnd1CertCoverage.Tolerance;
}

/// <summary>
/// ตัวตัดสิน**ตัวเดียว**ของ "ยอด ภ.ง.ด.1 ของงวดนี้มีใบ 50 ทวิ รองรับครบไหม"
///
/// <para>═══ ทำไมต้องมี (D6-4 · คำตัดสินเจ้าของรอบ 170) ═══
/// นโยบาย: "50 ทวิ ออกอัตโนมัติเป็น <c>Issued</c> ตอนจ่าย **ทุกทางเข้า** ·
/// เอกสารที่หัก WHT แต่ไม่มี cert ออกจริง = ช่องโหว่ที่ต้องเตือน + บล็อกนำส่ง
/// **ห้ามนับเงียบ**"</para>
///
/// <para>ด่านนี้มีอยู่แล้วสำหรับ ภ.ง.ด.3/53/54 (เทียบเอกสารกับ certs) แต่
/// **ไม่มีสำหรับ ภ.ง.ด.1**: ยอดบนหน้านำส่งอ่านจาก
/// <c>PayrollRun.TotalWithholdingTax</c> ตรง ๆ ขณะที่
/// <c>PayrollService.IssueMonthlyPnd1CertsAsync</c> <b>ข้ามพนักงานที่ยังไม่
/// กรอกเลขประจำตัวผู้เสียภาษีแบบเงียบ ๆ</b> (และ <c>catch → LogWarning</c>
/// เมื่อทั้งงวดล้ม) ⇒ กดนำส่งได้ตามยอดที่หักจริง โดยที่ไฟล์ยื่นและใบรับรอง
/// ประกาศน้อยกว่า — และพนักงานคนนั้นไม่มี 50 ทวิ ไปยื่นแบบของตัวเอง</para>
///
/// <para>═══ ทำไมคำนวณสดทุกครั้ง ไม่เก็บธง ═══ ธงที่เขียนไว้ตอนจ่ายจะค้างเป็น
/// "มีช่องโหว่" ตลอดไปแม้ HR จะออกใบครบแล้ว (defect class "สถานะปลายทางที่
/// ระบบประทับเอง") · การเทียบสองแหล่งสด ๆ **ซ่อมตัวเอง**ทันทีที่ใบครบ</para>
/// </summary>
public static class Pnd1CertCoverage
{
    /// <summary>ผลต่างที่ยอมรับได้ (เศษสตางค์จากการปัด)</summary>
    public const decimal Tolerance = 0.009m;

    /// <summary>รหัสด่าน — ชุดเดียวกับ ภ.ง.ด.3/53/54 เพื่อให้ผู้ใช้เห็นเหตุผล
    /// เดียวกันไม่ว่าชนด่านที่แบบไหน</summary>
    public const string RuleCode = WhtUnissuedCertGate.RuleCode;

    /// <summary>เทียบ "แถวเงินเดือนที่หักภาษี" กับ "ใบ 50 ทวิ ภ.ง.ด.1 ที่ออกแล้ว"
    /// ของงวดเดียวกัน — pure ทั้งหมด</summary>
    /// <param name="payrollRowsWithTax">จำนวนแถวเงินเดือน (รอบที่จ่ายแล้ว) ที่มี
    /// ภาษีหัก &gt; 0 ในงวดนี้</param>
    /// <param name="payrollTaxTotal">ยอดภาษีหักรวมของแถวเหล่านั้น</param>
    /// <param name="issuedCertCount">จำนวนใบ 50 ทวิ แบบ ภ.ง.ด.1 ที่สถานะอยู่ใน
    /// <see cref="WhtCertFilingScope.Filed"/> ของงวดนี้</param>
    /// <param name="issuedCertTaxTotal">ยอดภาษีรวมบนใบเหล่านั้น</param>
    public static Pnd1CoverageGap Evaluate(
        int payrollRowsWithTax, decimal payrollTaxTotal,
        int issuedCertCount, decimal issuedCertTaxTotal)
    {
        // ไม่มีการหักภาษีในงวดนี้ = ไม่มีอะไรต้องรองรับ (ยื่นแบบเปล่ายังต้องยื่น
        // แต่นั่นเป็นคนละเรื่องกับด่านนี้)
        if (payrollTaxTotal <= Tolerance && payrollRowsWithTax <= 0)
            return new Pnd1CoverageGap(0, 0m);

        var countGap = Math.Max(0, payrollRowsWithTax - issuedCertCount);
        var amountGap = payrollTaxTotal - issuedCertTaxTotal;
        // ⚠️ ตัดเศษ **ก่อน** ปัด — ปัดก่อนจะดัน 0.005 ขึ้นเป็น 0.01 ซึ่งเกิน
        // Tolerance พอดี ⇒ ด่านฟ้องทุกงวดที่ปัดเศษไม่ลงตัว = ปิดด่านโดยไม่ตั้งใจ
        if (amountGap <= Tolerance) amountGap = 0m;
        else amountGap = Math.Round(amountGap, 2, MidpointRounding.AwayFromZero);
        return new Pnd1CoverageGap(countGap, amountGap);
    }

    /// <summary>ข้อความเตือน/บล็อก — บอก "ขาดเท่าไร" และ "ทำอะไรต่อ" เสมอ
    /// (ด่านที่ไม่มีทางไปต่อ = ด่านที่ผู้ใช้ต้องหลบ — F2 ข้อ 8)</summary>
    public static string GapMessage(int month, int year, Pnd1CoverageGap gap)
        => $"ภ.ง.ด.1 งวด {month:D2}/{year} มีภาษีที่หักจากเงินเดือนไปแล้ว "
           + $"{gap.TaxAmount:N2} บาท"
           + (gap.EmployeeCount > 0 ? $" (พนักงานประมาณ {gap.EmployeeCount} คน)" : "")
           + " ที่ยังไม่มีหนังสือรับรอง 50 ทวิ ที่ออกแล้วรองรับ — "
           + "สาเหตุที่พบบ่อยคือพนักงานยังไม่ได้กรอกเลขบัตรประชาชน (หรือเลขประจำตัวผู้เสียภาษีสำหรับผู้ไม่มีบัตรไทย) · "
           + "กรอกให้ครบที่หน้า “พนักงาน” แล้วกด “สร้างเอกสารใหม่” ที่รอบเงินเดือนงวดนี้ "
           + "หรือออกใบเองที่หน้า “หนังสือรับรองหัก ณ ที่จ่าย”";
}
