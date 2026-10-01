using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ขั้นที่ต้องทำกับใบสำคัญจ่ายที่ผูกกับรายการต้นทาง (ใบเบิก · เงินทดรอง) ตอนกด "จ่ายเงิน"</summary>
public enum LinkedPayVoucherStep
{
    /// <summary>ยังไม่มีใบ (หรือใบเดิมถูกยกเลิก/ปฏิเสธ/ลบ) — สร้างใบใหม่แล้วอนุมัติ</summary>
    Create = 0,
    /// <summary>มีใบร่าง/รออนุมัติจากการกดครั้งก่อนที่อนุมัติไม่ผ่าน — อนุมัติใบเดิม (ห้ามสร้างซ้ำ)</summary>
    ReuseDraft = 1,
    /// <summary>ใบเดิมออกแล้ว (ผู้ใช้ไปอนุมัติที่หน้าเอกสารเอง) — ไม่ต้องอนุมัติซ้ำ แค่ปิดรายการต้นทาง</summary>
    AlreadyIssued = 2,
}

/// <summary>รายการต้นทางที่ออกใบสำคัญจ่ายอัตโนมัติตอนกดจ่าย</summary>
public enum LinkedPayVoucherSource
{
    /// <summary>ใบเบิกค่าใช้จ่ายพนักงาน (<c>ExpenseClaimService.MarkAsPaidAsync</c>)</summary>
    ExpenseClaim = 0,
    /// <summary>เงินทดรองจ่ายพนักงาน (<c>SalaryAdvanceService.DisburseAsync</c>)</summary>
    SalaryAdvance = 1,
}

/// <summary>
/// **รายการต้นทาง → ใบสำคัญจ่าย: กดจ่ายซ้ำต้องไม่สร้างใบร่างซ้ำ** (รอบ 201 ฝ่ายค้าน TX RTX-5 · รอบสาม P2-3 ขยายจากใบเบิกมาเป็นตัวกลาง)
/// <para>เดิม <c>ExpenseClaimService.MarkAsPaidAsync</c> และ <c>SalaryAdvanceService.DisburseAsync</c> สร้าง PV ทุกครั้งแล้วค่อยอนุมัติ — ถ้าการอนุมัติหยุดด้วย
/// คำเตือน (ทางเข้านี้ไม่มีหน้าจอรับทราบ) ใบร่างค้างแล้วกดใหม่ได้ใบร่างเพิ่มอีกใบ และ exception ของคำเตือนไม่ถูกแปลง ⇒ 500 ข้อความกลาง ๆ ·
/// ตอนนี้: ผูกใบร่างกับรายการต้นทางทันทีหลังสร้าง · กดซ้ำใช้ใบเดิม · คำเตือนที่ต้องมีคนรับทราบ ⇒ ข้อความไทยพร้อมทางไปต่อ
/// (เปิดใบสำคัญจ่ายแล้วอนุมัติที่หน้าเอกสาร แล้วกลับมากดจ่าย) · ตัวเดียวของทั้งสองเส้น — ห้ามเขียนสำเนา</para>
/// </summary>
public static class LinkedPayVoucher
{
    /// <summary>รหัสกฎของการหยุดเพราะคำเตือนก่อนอนุมัติใบสำคัญจ่ายของรายการต้นทาง (ใบเบิกคงรหัสเดิม)</summary>
    public static string WarningsRuleCode(LinkedPayVoucherSource source) => source switch
    {
        LinkedPayVoucherSource.SalaryAdvance => "ADVANCE-PAY-PV-WARNINGS",
        _ => "EXPENSE-PAY-PV-WARNINGS",
    };

    /// <param name="existingVoucherId">ใบสำคัญจ่ายที่ผูกกับรายการต้นทางอยู่</param>
    /// <param name="existingStatus">สถานะของใบนั้น (null = ไม่พบ/ถูกลบ/คนละบริษัท)</param>
    public static LinkedPayVoucherStep StepFor(Guid? existingVoucherId, DocumentStatus? existingStatus)
    {
        if (existingVoucherId == null || existingStatus == null) return LinkedPayVoucherStep.Create;
        return existingStatus.Value switch
        {
            DocumentStatus.Draft or DocumentStatus.WaitingApproval => LinkedPayVoucherStep.ReuseDraft,
            DocumentStatus.Voided or DocumentStatus.Rejected => LinkedPayVoucherStep.Create,
            _ => DocumentStatusRules.IsIssued(existingStatus.Value) ? LinkedPayVoucherStep.AlreadyIssued : LinkedPayVoucherStep.Create,
        };
    }

    /// <summary>ข้อความเมื่อการอนุมัติใบสำคัญจ่ายหยุดเพราะคำเตือนที่ต้องมีคนรับทราบ — บอกเลขใบร่าง + ทางไปต่อ</summary>
    public static string WarningsMessage(LinkedPayVoucherSource source, string voucherNumber, IReadOnlyList<string> warnings)
    {
        var notPaid = source == LinkedPayVoucherSource.SalaryAdvance ? "ยังไม่ได้จ่ายเงินทดรอง" : "ยังไม่ได้จ่ายใบเบิก";
        var origin = source == LinkedPayVoucherSource.SalaryAdvance ? "รายการเงินทดรอง" : "ใบเบิก";
        return $"{notPaid} — ใบสำคัญจ่าย {voucherNumber} มีคำเตือน {warnings.Count} ข้อที่ต้องมีคนรับทราบก่อนอนุมัติ: "
               + string.Join(" · ", warnings)
               + $" — เปิดใบสำคัญจ่ายนี้ที่หน้าเอกสาร ตรวจแล้วกด “อนุมัติ” (รับทราบคำเตือน) แล้วกลับมากด “จ่ายเงิน” ที่{origin}อีกครั้ง "
               + "(ระบบใช้ใบเดิม ไม่สร้างซ้ำ)";
    }
}
