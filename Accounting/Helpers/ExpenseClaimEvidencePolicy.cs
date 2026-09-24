using Accounting.Models.Constants;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ผู้ยื่นใบเบิกแนบ/ถอดหลักฐานของใบตัวเองได้ถึงเมื่อไร** — ตัวตัดสินตัวเดียวของด่านไฟล์แนบชนิด <c>ExpenseClaim</c>
///
/// <para>═══ ที่มา (ฝ่ายค้านรอบ 193 · P1) ═══ ด่านไฟล์แนบของ U2 ให้ "เจ้าของใบเบิกผ่านเสมอ" โดยไม่ดูสถานะใบ ⇒ ผู้ยื่นถอดใบเสร็จ
/// ที่ผู้อนุมัติดูแล้วอนุมัติ แล้วแนบไฟล์อื่นแทน<b>หลังอนุมัติ/จ่ายเงินแล้ว</b>ได้ · หลักฐานของรายจ่ายที่จ่ายเงินไปแล้วต้องเป็น
/// ชุดที่ผู้อนุมัติเห็น (§65 ตรี (9) · พ.ร.บ.การบัญชี ม.10) — การถอดหลังอนุมัติเก็บไฟล์จริงไว้ (<see cref="AttachmentRetention"/>)
/// ก็จริง แต่รายการที่ผู้ตรวจเปิดดูจะไม่ใช่ชุดเดิมแล้ว</para>
///
/// <para>═══ กติกา ═══ ผู้ยื่นแนบ/ถอดเองได้ระหว่าง <c>Draft</c> และ <c>Submitted</c> (รอผู้อนุมัติ — ผู้อนุมัติมักขอหลักฐาน
/// เพิ่มระหว่างนี้) · ตั้งแต่ <c>Approved</c> ขึ้นไป (รวม Paid · Rejected · Voided) ผู้ยื่นทำเองไม่ได้ ต้องให้ผู้มีสิทธิ์อนุมัติ
/// (<see cref="PermissionKeys.ExpenseApprove"/> / <see cref="PermissionKeys.HrAdmin"/>) ทำให้ — ด่านคีย์ของผู้ตรวจไม่เปลี่ยน ·
/// <b>การดู</b>ไม่ถูกล็อก (ผู้ยื่นยังเปิดหลักฐานของตัวเองได้ทุกสถานะ)</para>
///
/// <para>สถานะที่ไม่รู้จัก (ค่า enum ใหม่ในอนาคต) = ล็อก — "ไม่รู้" ห้ามตกเป็น "ผ่าน" (DOCTRINE §1)</para>
/// </summary>
public static class ExpenseClaimEvidencePolicy
{
    /// <summary>ผู้ยื่นแนบ/ถอดหลักฐานของใบตัวเองได้ในสถานะนี้ไหม</summary>
    public static bool OwnerMayChange(ExpenseClaimStatus status)
        => status is ExpenseClaimStatus.Draft or ExpenseClaimStatus.Submitted;

    /// <summary>ข้อความเมื่อผู้ยื่นถูกล็อก — บอกเหตุผลและ<b>ทางไปต่อ</b> (ขอผู้อนุมัติ)</summary>
    public static string LockedMessage(ExpenseClaimStatus status, string verb)
    {
        var state = status switch
        {
            ExpenseClaimStatus.Approved => "อนุมัติแล้ว",
            ExpenseClaimStatus.Paid => "จ่ายเงินแล้ว",
            ExpenseClaimStatus.Rejected => "ถูกปฏิเสธแล้ว",
            ExpenseClaimStatus.Voided => "ถูกยกเลิกแล้ว",
            _ => "ผ่านขั้นรออนุมัติไปแล้ว",
        };
        return $"ใบเบิกนี้{state} — ผู้ยื่น{verb}หลักฐานเองไม่ได้ เพื่อให้หลักฐานตรงกับชุดที่ผู้อนุมัติตรวจแล้ว · "
             + "ถ้าต้องเพิ่มหรือแก้หลักฐาน ให้ขอผู้อนุมัติ (สิทธิ์ "
             + PermissionKeys.ExpenseApprove.Replace("perm:", "") + " หรือ "
             + PermissionKeys.HrAdmin.Replace("perm:", "") + ") ทำให้";
    }
}
