using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// ผลข้างเคียงของ "เปลี่ยนรายชื่อคนรับเงิน" (➕ เพิ่ม / 🗑 เอาออก) บนตัวรอบ — ตัวเดียวของทั้งสองทางเข้า (รอบ 201 ทีม PR2)
///
/// <para><b>คำตัดสินข้อ 69</b>: รายชื่อคนรับเงินคือสิ่งที่ผู้อนุมัติอนุมัติ ⇒ เปลี่ยนรายชื่อของรอบ <c>Approved</c> แล้ว
/// การอนุมัติเดิมไม่ครอบ ⇒ กลับเป็น <c>Calculated</c> + ล้าง <c>ApprovedBy</c>/<c>ApprovedAt</c> (แบบเดียวกับคำนวณใหม่หลังอนุมัติ ·
/// <c>ApprovePayrollAsync</c> รับเฉพาะ Calculated) · <b>✏️ แก้ยอดรายคนไม่ผ่านตัวนี้</b> (คงพฤติกรรมเดิม — คำตัดสินแยกไว้)</para>
///
/// <para><b>X4</b>: ประทับ <c>ManualRosterChangedAt</c> ทุกครั้ง ⇒ คำเตือนก่อน "คำนวณใหม่" (<see cref="PayrollRunEditPolicy.RecalculateWarning"/>)
/// บอกได้ว่าคำนวณใหม่จะทับรายชื่อที่แก้มือ · <c>CalculatePayrollAsync</c> ล้างค่านี้หลังคำนวณ (รายชื่อกลับมาจากเงื่อนไขงวด)</para>
/// </summary>
public static class PayrollRosterChange
{
    /// <summary>ข้อความถึงผู้ใช้เมื่อรอบที่อนุมัติแล้วถูกดีดกลับ</summary>
    public const string ReapprovalNotice =
        "รอบนี้อนุมัติไปแล้ว — การเปลี่ยนรายชื่อคนรับเงินทำให้รอบกลับเป็น “คำนวณแล้ว” ต้องกด “อนุมัติ” ใหม่ก่อนจ่าย "
        + "(ผู้อนุมัติต้องเห็นรายชื่อชุดใหม่)";

    /// <summary>ใช้ผลข้างเคียงกับรอบ — คืนข้อความที่ต้องบอกผู้ใช้ (null = สถานะไม่เปลี่ยน)</summary>
    public static string? Apply(PayrollRun run, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(run);
        run.ManualRosterChangedAt = nowUtc;
        if (run.Status != PayrollRunEditPolicy.Approved) return null;
        run.Status = PayrollRunEditPolicy.Calculated;
        run.ApprovedBy = null;
        run.ApprovedAt = null;
        return ReapprovalNotice;
    }
}
