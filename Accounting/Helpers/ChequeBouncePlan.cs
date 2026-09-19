using System;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสินตัวเดียวว่า "เช็คเด้งใบนี้ต้องถอยอะไรบ้าง"** (pure · OWNER file).
///
/// ═══ ของเดิมพังตรงไหน (`DECISION_AUDIT_2026-09-18.md` §3 D4-6, P1) ═══
/// `ChequeService.MarkBouncedAsync` **เปลี่ยนแค่สถานะ + เหตุผล**:
///  - `Payment` ที่ผูกอยู่ยังอยู่ครบ ⇒ เอกสารยังเป็น "ชำระแล้ว"
///  - JE ที่ลงตอนบันทึกการชำระยังอยู่ ⇒ **เงินที่ไม่เคยเข้า/ไม่เคยออก
///    ยังอยู่ในบัญชีธนาคารของงบ**
///  - ลูกหนี้ที่ยังไม่ได้เงินหายจากรายงานอายุหนี้
/// ⇒ เป็นราก R1 ตรง ๆ: "สถานะปลายทาง (ชำระแล้ว) ที่ไม่มีเงินจริงรองรับ"
///
/// ═══ กติกาของทิศ (G5) ═══
/// เช็คเด้ง = **เงินไม่เคยเปลี่ยนมือ**. ทิศที่ความเสียหายมองเห็นและแก้ทัน คือ
/// "ถอยการชำระออกให้หมด แล้วให้ใบกลับไปค้างชำระ" — ผู้ใช้เห็นใบค้างแล้วตาม
/// เก็บเงินต่อได้. ทิศตรงข้าม (ปล่อยไว้) คือเงินปลอมที่ไม่มีใครเห็น
///
/// ═══ กันนับซ้ำ ═══
/// `MarkClearedAsync` ขยับ `BankAccount.CurrentBalance` **เฉพาะเมื่อการชำระ
/// ที่ผูกอยู่ยังไม่ได้ขยับ** (`paymentAlreadyMovedBalance`). ฝั่งเด้งต้องสมมาตร:
/// เช็คที่ยังไม่ `Cleared` **ไม่เคยขยับยอดด้วยตัวเอง** ⇒ ไม่ต้องคืนยอดเอง
/// ปล่อยให้การกลับรายการชำระเป็นคนคืน (ไม่งั้นยอดถูกคืนสองรอบ)
/// </summary>
public static class ChequeBouncePlan
{
    /// <param name="ReversePayment">ต้องกลับรายการ `Payment` ที่ผูกอยู่ไหม</param>
    /// <param name="RestoreBankBalanceDirectly">ต้องคืน `CurrentBalance` เองไหม
    /// (จริงเฉพาะตอนเช็คเป็นคนขยับยอดเอง = ไม่มีการชำระผูกอยู่ **และ** เคยขึ้นเงินแล้ว)</param>
    /// <param name="RuleCode">รหัสกฎ — ลง audit ได้</param>
    /// <param name="Reason">เหตุผลภาษาไทยสำหรับ log / หน้าจอ</param>
    public sealed record Plan(
        bool ReversePayment,
        bool RestoreBankBalanceDirectly,
        string RuleCode,
        string Reason);

    /// <summary>
    /// ตัดสินจาก "สถานะก่อนเด้ง" + "มีการชำระผูกอยู่ไหม".
    /// </summary>
    /// <param name="isInbound">true = เช็คที่เรารับมาจากลูกค้า · false = เช็คที่เราจ่ายออก</param>
    /// <param name="wasCleared">ก่อนเด้ง เช็คอยู่สถานะ `Cleared` แล้วหรือยัง</param>
    /// <param name="hasLinkedPayment">มี `Cheque.PaymentId` ไหม</param>
    public static Plan Decide(bool isInbound, bool wasCleared, bool hasLinkedPayment)
    {
        if (wasCleared)
            // ธนาคารเรียกเก็บผ่านแล้วแต่คืนภายหลัง — ต้องถอยทั้งยอดและการชำระ
            // (วันนี้ `MarkBouncedAsync` ยังบล็อกเส้นนี้อยู่ ดูรายงานรอบนี้)
            return new Plan(hasLinkedPayment, !hasLinkedPayment, "CHEQUE-BOUNCE-AFTER-CLEAR",
                "เช็คถูกคืนหลังจากเรียกเก็บผ่านแล้ว — ต้องถอยทั้งยอดเงินและการชำระ");

        if (hasLinkedPayment)
            return new Plan(true, false, "CHEQUE-BOUNCE-REVERSE-PAYMENT",
                isInbound
                    ? "เช็ครับเด้ง — เงินไม่เคยเข้าบัญชี ต้องกลับรายการรับชำระและให้ใบกลับไปค้างชำระ"
                    : "เช็คจ่ายเด้ง — เงินไม่เคยออกจากบัญชี ต้องกลับรายการจ่ายและให้ใบกลับไปค้างจ่าย");

        // ไม่มีการชำระผูก + ยังไม่ขึ้นเงิน = ยังไม่มีอะไรลงบัญชี
        return new Plan(false, false, "CHEQUE-BOUNCE-NO-POSTING",
            "เช็คยังไม่ถูกเรียกเก็บและไม่ได้ผูกกับรายการชำระ — ไม่มีรายการบัญชีให้ถอย");
    }
}
