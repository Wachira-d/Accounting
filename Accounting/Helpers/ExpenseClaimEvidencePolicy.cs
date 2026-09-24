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
/// <para>═══ กติกา ═══ <c>Draft</c> แนบ/ถอดได้ · <c>Submitted</c> เพิ่มได้ ถอดไม่ได้ · ตั้งแต่ <c>Approved</c> ขึ้นไป (รวม Paid ·
/// Rejected · Voided) ผู้ยื่นทำเองไม่ได้ ต้องให้ผู้มีสิทธิ์อนุมัติ<b>คนอื่น</b> (<see cref="PermissionKeys.ExpenseApprove"/> /
/// <see cref="PermissionKeys.HrAdmin"/>) ทำให้ — ผู้ยื่นที่ถือคีย์ผู้ตรวจเองก็ไม่ได้ (SoD) · <b>การดู</b>ไม่ถูกล็อก</para>
///
/// <para>สถานะที่ไม่รู้จัก (ค่า enum ใหม่ในอนาคต) = ล็อก — "ไม่รู้" ห้ามตกเป็น "ผ่าน" (DOCTRINE §1)</para>
/// </summary>
public static class ExpenseClaimEvidencePolicy
{
    /// <summary>ผู้ยื่นแนบ (<paramref name="isRemoval"/> = false) หรือถอด (true) หลักฐานของใบตัวเองได้ในสถานะนี้ไหม
    ///
    /// <para>ฝ่ายค้านรอบ 193 (S2-P1): ช่วง <c>Submitted</c> ผู้ยื่นถอดหลักฐานได้หลังผ่านด่าน §65 ทวิ ตอนส่ง แล้วขั้นอนุมัติไม่ตรวจซ้ำ
    /// ⇒ ใบ "ไม่มีใบเสร็จ" ถูกอนุมัติโดยไม่มีหลักฐานสักไฟล์ + สลับไฟล์ระหว่างผู้อนุมัติกำลังดูได้ · ตอนนี้ Submitted =
    /// <b>เพิ่มได้ ถอดไม่ได้</b> (ผู้อนุมัติมักขอหลักฐานเพิ่ม แต่ชุดที่ผู้อนุมัติเห็นต้องไม่หายไป) และขั้นอนุมัติตรวจซ้ำด้วย
    /// <see cref="MissingEvidenceMessage"/></para></summary>
    public static bool OwnerMayChange(ExpenseClaimStatus status, bool isRemoval)
        => status switch
        {
            ExpenseClaimStatus.Draft => true,
            ExpenseClaimStatus.Submitted => !isRemoval,
            _ => false,
        };

    /// <summary>
    /// **แยกหน้าที่ (SoD)** — ผู้ยื่นที่ถือคีย์ผู้ตรวจเอง (Owner/ผู้อนุมัติที่เบิกเอง) ห้ามใช้คีย์นั้นแก้หลักฐานของใบ<b>ตัวเอง</b>
    /// หลังล็อก (ฝ่ายค้านรอบ 193 · S2-P2: "อนุมัติเอง แก้เอง") · ผู้ตรวจคนอื่นยังแก้ให้ได้ · ใบของคนอื่นไม่เกี่ยว
    /// </summary>
    /// <returns>true = ใช้คีย์ผู้ตรวจผ่านได้</returns>
    public static bool ReviewerKeyApplies(bool isClaimOwner) => !isClaimOwner;

    /// <summary>ข้อความเมื่อผู้ยื่นถูกล็อก — บอกเหตุผลและ<b>ทางไปต่อ</b> (ขอผู้อนุมัติคนอื่น)</summary>
    public static string LockedMessage(ExpenseClaimStatus status, string verb)
    {
        var state = status switch
        {
            ExpenseClaimStatus.Submitted => "ส่งอนุมัติแล้ว (เพิ่มหลักฐานได้ แต่ถอดไม่ได้)",
            ExpenseClaimStatus.Approved => "อนุมัติแล้ว",
            ExpenseClaimStatus.Paid => "จ่ายเงินแล้ว",
            ExpenseClaimStatus.Rejected => "ถูกปฏิเสธแล้ว",
            ExpenseClaimStatus.Voided => "ถูกยกเลิกแล้ว",
            _ => "ผ่านขั้นรออนุมัติไปแล้ว",
        };
        return $"ใบเบิกนี้{state} — ผู้ยื่น{verb}หลักฐานของใบตัวเองไม่ได้ เพื่อให้หลักฐานตรงกับชุดที่ผู้อนุมัติตรวจ "
             + "(แม้ผู้ยื่นจะมีสิทธิ์อนุมัติเองก็ตาม — แยกหน้าที่ผู้เบิกกับผู้ตรวจ) · ถ้าต้องเพิ่มหรือแก้หลักฐาน "
             + "ให้ขอผู้อนุมัติคนอื่น (สิทธิ์ "
             + PermissionKeys.ExpenseApprove.Replace("perm:", "") + " หรือ "
             + PermissionKeys.HrAdmin.Replace("perm:", "") + ") ทำให้";
    }

    /// <summary>
    /// ด่าน §65 ทวิ "เบิกโดยไม่มีใบเสร็จต้องมีหลักฐาน ≥ 1 ไฟล์" — ใช้<b>ทั้งตอนส่งและตอนอนุมัติ</b> (เว็บ + มือถือ) ·
    /// คืน null = ผ่าน
    /// </summary>
    public static string? MissingEvidenceMessage(bool noReceipt, int attachmentCount, bool atApproval)
    {
        if (!noReceipt || attachmentCount > 0) return null;
        return atApproval
            ? "ใบเบิกแบบไม่มีใบเสร็จนี้ไม่มีหลักฐานแนบเหลืออยู่ — อนุมัติไม่ได้ (§65 ทวิ) · "
              + "ให้ผู้ยื่นแนบหลักฐาน (รูปสินค้า · slip การโอน · statement) แล้วค่อยอนุมัติ หรือปฏิเสธใบนี้"
            : "เบิกแบบไม่มีใบเสร็จต้องแนบหลักฐานอย่างน้อย 1 ไฟล์ก่อนส่งอนุมัติ "
              + "(เช่น รูปสินค้า, รูป meter taxi, slip การโอน, statement บัตรเครดิต) — §65 ทวิ";
    }
}
