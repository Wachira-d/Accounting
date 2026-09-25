using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลตรวจคำขอคืนเงินผ่าน gateway 1 ครั้ง</summary>
public readonly record struct GatewayRefundCheck(
    bool Ok,
    string? Message,
    // ยอดที่คืนครั้งนี้ (ผู้ใช้ไม่ระบุ = คืนส่วนที่เหลือทั้งหมด)
    decimal Amount,
    // ยอดคืนสะสมหลังครั้งนี้
    decimal NewRefundedTotal,
    // ครบยอดที่รับไว้แล้วหรือยัง ⇒ สถานะ Refunded (ไม่ครบ = PartiallyRefunded)
    bool IsFullRefund);

/// <summary>สถานะ "ออกใบลดหนี้ตามการคืนเงินแล้วหรือยัง" (§86/10) ของรายการชำระเงินหนึ่ง</summary>
public enum GatewayRefundCreditNoteState
{
    /// <summary>ยังไม่มีการคืนเงินผ่านระบบ</summary>
    NotRefunded = 0,
    /// <summary>มีใบลดหนี้ที่อนุมัติแล้วอ้างเอกสารต้นทาง ยอดรวม ≥ ยอดที่คืน</summary>
    CreditNoteCovered = 1,
    /// <summary>คืนเงินแล้ว แต่ยอดใบลดหนี้ที่อ้างเอกสารต้นทางยังไม่ถึงยอดที่คืน — ต้องมีคนออกใบลดหนี้</summary>
    CreditNoteMissing = 2,
    /// <summary>คืนเงินแล้ว แต่ต้นทางไม่ใช่เอกสารในระบบ (บิล POS/ออเดอร์เว็บ/การจอง) — ระบบตรวจย้อนเองไม่ได้
    /// ต้องให้คนยืนยันว่าออกใบลดหนี้แล้ว (ไม่รู้ = บอกว่าไม่รู้ ห้ามตีเป็น "ครบ")</summary>
    CannotTrace = 3,
}

/// <summary>
/// **คืนเงินผ่านผู้ให้บริการรับชำระเงิน — ตัวตัดสินตัวเดียว** (รอบ 198 G-1)
///
/// <para>═══ ที่มา ═══ เดิม endpoint คืนเงินเรียกผู้ให้บริการคืนเงินจริงแล้ว<b>ไม่ลงบัญชีอะไรเลย</b> ⇒ บัญชีพัก 11340
/// ยังถือยอดเต็ม (ยอดที่ผู้ให้บริการจะโอนให้เราน้อยกว่าที่บัญชีบอก) · พอผู้ใช้ออกใบลดหนี้ตามคำแนะนำ ใบลดหนี้ Cr ลูกหนี้
/// ที่ถูกล้างไปแล้วตอนรับเงิน ⇒ <b>ลูกหนี้ติดลบ</b> · และ "ครบยอดไหม" เทียบแค่ยอดครั้งนี้กับยอดเต็ม (ไม่นับที่คืนไปก่อน)
/// ⇒ คืนบางส่วนสองครั้งรวมเกินยอดรับได้</para>
///
/// <para>═══ JE ═══ <c>Dr ลูกหนี้การค้า (ผังของลูกค้า) / Cr บัญชีพักผู้ให้บริการ</c> — เงินออกจากบัญชีพัก (ผู้ให้บริการ
/// คืนจากยอดที่ถือไว้) และตั้งลูกหนี้กลับเป็น "ยอดรอใบลดหนี้" · ใบลดหนี้ §86/10 (Dr รายได้ + ภาษีขาย / Cr ลูกหนี้) ที่คนออก
/// ทีหลังจะล้างยอดนี้เป็นศูนย์พอดี · ระบบ<b>ไม่ออกใบลดหนี้เอง</b> (เหตุผลตาม closed list · ยอดสะสมห้ามเกินใบเดิม
/// ต้องให้คนตัดสิน) แต่แสดงรายการ "คืนเงินแล้ว ยังไม่ออกใบลดหนี้" ผ่าน <see cref="CreditNoteState"/></para>
///
/// <para>G6: pure · ไม่มี I/O</para>
/// </summary>
public static class GatewayRefundMath
{
    /// <summary>เศษที่ยอมให้ (ครึ่งสตางค์) — ตรงกับที่ endpoint เดิมใช้</summary>
    public const decimal Tolerance = 0.005m;

    /// <summary>ยอดที่ยังคืนได้</summary>
    public static decimal Remaining(decimal amount, decimal alreadyRefunded)
        => R(Math.Max(0m, amount - alreadyRefunded));

    /// <summary>ตรวจคำขอคืนเงิน — สถานะต้องเป็น "รับเงินแล้ว" · ยอดรวมที่คืนห้ามเกินยอดที่รับ ·
    /// คืนบางส่วนก่อนระบบบันทึกยอดคืน (สถานะ PartiallyRefunded แต่ยอดสะสม = 0) = ไม่รู้ว่าคืนไปเท่าไร ⇒ ห้ามคืนเพิ่มผ่านระบบ</summary>
    public static GatewayRefundCheck Check(PaymentIntentStatus status, decimal amount, decimal alreadyRefunded,
        decimal? requested)
    {
        if (status is not (PaymentIntentStatus.Succeeded or PaymentIntentStatus.PartiallyRefunded))
            return Fail("คืนเงินได้เฉพาะรายการที่รับเงินสำเร็จแล้ว");

        if (status == PaymentIntentStatus.PartiallyRefunded && alreadyRefunded <= 0m)
            return Fail("รายการนี้คืนเงินบางส่วนไปก่อนที่ระบบจะเริ่มบันทึกยอดคืน — ระบบไม่รู้ว่าคืนไปแล้วเท่าไร "
                + "จึงคืนเพิ่มผ่านระบบไม่ได้ (เสี่ยงคืนเกินยอดที่รับ) · ให้คืนที่แดชบอร์ดของผู้ให้บริการ "
                + "แล้วบันทึกรายการบัญชีด้วยมือ + ออกใบลดหนี้ที่เอกสารต้นทาง");

        var remaining = Remaining(amount, alreadyRefunded);
        if (remaining <= 0m)
            return Fail("รายการนี้คืนเงินครบยอดที่รับแล้ว");

        var refund = R(requested ?? remaining);
        if (refund <= 0m || refund > remaining + Tolerance)
            return Fail($"ยอดคืนต้องมากกว่า 0 และไม่เกินยอดที่ยังคืนได้ ({remaining:N2} จากที่รับไว้ {amount:N2})");

        var newTotal = R(alreadyRefunded + refund);
        return new GatewayRefundCheck(true, null, refund, newTotal, newTotal >= amount - Tolerance);
    }

    /// <summary>คืนเงินแล้ว — ออกใบลดหนี้ครอบยอดนั้นหรือยัง
    /// (<paramref name="traceable"/> = ต้นทางเป็นเอกสารในระบบที่ใบลดหนี้อ้างถึงได้)</summary>
    public static GatewayRefundCreditNoteState CreditNoteState(decimal refunded, bool traceable,
        decimal approvedCreditNotesTotal)
    {
        if (refunded <= 0m) return GatewayRefundCreditNoteState.NotRefunded;
        if (!traceable) return GatewayRefundCreditNoteState.CannotTrace;
        return approvedCreditNotesTotal + Tolerance >= refunded
            ? GatewayRefundCreditNoteState.CreditNoteCovered
            : GatewayRefundCreditNoteState.CreditNoteMissing;
    }

    /// <summary>ต้องมีคนตามออกใบลดหนี้ไหม (ป้าย "คืนเงินแล้ว ยังไม่ออกใบลดหนี้")</summary>
    public static bool NeedsCreditNote(GatewayRefundCreditNoteState state)
        => state is GatewayRefundCreditNoteState.CreditNoteMissing or GatewayRefundCreditNoteState.CannotTrace;

    private static GatewayRefundCheck Fail(string message) => new(false, message, 0m, 0m, false);

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
