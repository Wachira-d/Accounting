using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// กติกาสถานะเอกสารที่ทุกเส้นต้องใช้ร่วมกัน — แทนการเขียน <c>== DocumentStatus.Approved</c>
/// เป๊ะ ๆ ซึ่ง CLAUDE.md บันทึกว่า "มักผิด" เพราะสถานะเดินต่อได้เอง (Approved → Sent →
/// PartiallyPaid → Paid → Overdue). เขียนด่านเป็น "ห้ามสถานะไหน" ปลอดภัยกว่า "ต้องเป็น
/// สถานะไหน" — ผลตรวจ ERP_REVIEW_2026-09-05 A-01 (ค่ารับรอง YTD) และ A-02 (portal).
/// </summary>
public static class DocumentStatusRules
{
    /// <summary>
    /// ยังไม่ "ออก" เป็นเอกสารจริง: ไม่มีเลขรัน §86/4 (ยัง <c>DRAFT-{guid}</c>) · ไม่มี JE ·
    /// ไม่เข้า ภ.พ.30 · ลูกค้าปลายทางต้องไม่เห็น. เก็บเป็น array เพื่อให้ EF แปลเป็น
    /// <c>NOT IN (...)</c> ได้ตรง ๆ (<c>!NotIssued.Contains(d.Status)</c>).
    /// </summary>
    public static readonly DocumentStatus[] NotIssued =
    {
        DocumentStatus.Draft,
        DocumentStatus.WaitingApproval,
        DocumentStatus.Rejected,
    };

    /// <summary>ออกแล้ว (มีเลข/JE) — รวม Voided ด้วย เพราะใบยกเลิกก็เคยออกจริงและต้องเก็บ 5 ปี.
    /// ผู้เรียกที่ต้องตัด Voided (เช่น ยอด YTD) ให้เช็คเพิ่มเอง</summary>
    public static bool IsIssued(DocumentStatus status) => !NotIssued.Contains(status);

    /// <summary>ออกแล้วและยังมีผลทางบัญชี/ภาษี (ตัด Voided ออก) — ใช้กับยอดรวม/YTD/aging</summary>
    public static bool IsEffective(DocumentStatus status)
        => IsIssued(status) && status != DocumentStatus.Voided;
}
