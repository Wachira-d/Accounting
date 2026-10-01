using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ขั้นที่ต้องทำกับใบสำคัญจ่ายของใบเบิกตอนกด "จ่ายเงิน"</summary>
public enum ExpenseClaimPayVoucherStep
{
    /// <summary>ยังไม่มีใบ (หรือใบเดิมถูกยกเลิก/ปฏิเสธ/ลบ) — สร้างใบใหม่แล้วอนุมัติ</summary>
    Create = 0,
    /// <summary>มีใบร่าง/รออนุมัติจากการกดครั้งก่อนที่อนุมัติไม่ผ่าน — อนุมัติใบเดิม (ห้ามสร้างซ้ำ)</summary>
    ReuseDraft = 1,
    /// <summary>ใบเดิมออกแล้ว (ผู้ใช้ไปอนุมัติที่หน้าเอกสารเอง) — ไม่ต้องอนุมัติซ้ำ แค่ปิดใบเบิก</summary>
    AlreadyIssued = 2,
}

/// <summary>
/// **ใบเบิก → ใบสำคัญจ่าย: กดจ่ายซ้ำต้องไม่สร้างใบร่างซ้ำ** (รอบ 201 ฝ่ายค้าน TX RTX-5)
/// <para>เดิม <c>ExpenseClaimService.MarkAsPaidAsync</c> สร้าง PV ทุกครั้งแล้วค่อยอนุมัติ — ถ้าการอนุมัติหยุดด้วยคำเตือน (ทางเข้านี้ไม่มีหน้าจอรับทราบ)
/// ใบร่างค้างแล้วกดใหม่ได้ใบร่างเพิ่มอีกใบ และ exception ของคำเตือนไม่ถูกแปลง ⇒ 500 ข้อความกลาง ๆ · ตอนนี้: ผูกใบร่างกับใบเบิกทันทีหลังสร้าง ·
/// กดซ้ำใช้ใบเดิม · คำเตือนที่ต้องมีคนรับทราบ ⇒ ข้อความไทยพร้อมทางไปต่อ (เปิดใบสำคัญจ่ายแล้วอนุมัติที่หน้าเอกสาร แล้วกลับมากดจ่าย)</para>
/// </summary>
public static class ExpenseClaimPayVoucher
{
    /// <summary>รหัสกฎของการหยุดเพราะคำเตือนก่อนอนุมัติใบสำคัญจ่ายของใบเบิก</summary>
    public const string WarningsRuleCode = "EXPENSE-PAY-PV-WARNINGS";

    /// <param name="existingVoucherId">ใบสำคัญจ่ายที่ผูกกับใบเบิกอยู่</param>
    /// <param name="existingStatus">สถานะของใบนั้น (null = ไม่พบ/ถูกลบ/คนละบริษัท)</param>
    public static ExpenseClaimPayVoucherStep StepFor(Guid? existingVoucherId, DocumentStatus? existingStatus)
    {
        if (existingVoucherId == null || existingStatus == null) return ExpenseClaimPayVoucherStep.Create;
        return existingStatus.Value switch
        {
            DocumentStatus.Draft or DocumentStatus.WaitingApproval => ExpenseClaimPayVoucherStep.ReuseDraft,
            DocumentStatus.Voided or DocumentStatus.Rejected => ExpenseClaimPayVoucherStep.Create,
            _ => DocumentStatusRules.IsIssued(existingStatus.Value) ? ExpenseClaimPayVoucherStep.AlreadyIssued : ExpenseClaimPayVoucherStep.Create,
        };
    }

    /// <summary>ข้อความเมื่อการอนุมัติใบสำคัญจ่ายหยุดเพราะคำเตือนที่ต้องมีคนรับทราบ — บอกเลขใบร่าง + ทางไปต่อ</summary>
    public static string WarningsMessage(string voucherNumber, IReadOnlyList<string> warnings)
        => $"ยังไม่ได้จ่ายใบเบิก — ใบสำคัญจ่าย {voucherNumber} มีคำเตือน {warnings.Count} ข้อที่ต้องมีคนรับทราบก่อนอนุมัติ: "
           + string.Join(" · ", warnings)
           + " — เปิดใบสำคัญจ่ายนี้ที่หน้าเอกสาร ตรวจแล้วกด “อนุมัติ” (รับทราบคำเตือน) แล้วกลับมากด “จ่ายเงิน” ที่ใบเบิกอีกครั้ง "
           + "(ระบบใช้ใบเดิม ไม่สร้างซ้ำ)";
}
