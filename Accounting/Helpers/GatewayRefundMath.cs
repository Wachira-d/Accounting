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

/// <summary>ผลตรวจการคืนเงินที่ผลไม่แน่ชัด (ฝ่ายค้าน E-2)</summary>
public enum GatewayRefundVerificationOutcome
{
    /// <summary>ผู้ให้บริการไม่ส่งยอดคืนสะสม — ยังไม่รู้ (ล็อกต่อ)</summary>
    ProviderSilent = 0,
    /// <summary>ยอดคืนสะสมของผู้ให้บริการ = ที่ระบบบันทึก ⇒ ครั้งที่ไม่แน่ชัดไม่ได้เกิด ⇒ ปลดล็อก</summary>
    NoMoneyOut = 1,
    /// <summary>ผู้ให้บริการคืนมากกว่าที่ระบบบันทึก ⇒ เงินออกไปแล้ว ⇒ ลงบัญชีส่วนต่าง + ปลดล็อก</summary>
    MoneyWentOut = 2,
    /// <summary>ข้อมูลขัดกัน (น้อยกว่าที่บันทึก/มากกว่ายอดรับ) — ล็อกต่อ ให้คนตรวจ</summary>
    Inconsistent = 3,
}

/// <summary>ผลตรวจ + ยอดที่ต้องลงบัญชีเพิ่ม (เฉพาะ <see cref="GatewayRefundVerificationOutcome.MoneyWentOut"/>)</summary>
public readonly record struct GatewayRefundVerification(
    GatewayRefundVerificationOutcome Outcome, decimal AmountToBook, string Message)
{
    /// <summary>ปลดล็อกได้ไหม — เฉพาะเมื่อรู้แน่แล้วว่าเงินออกหรือไม่ออก</summary>
    public bool Resolves => Outcome is GatewayRefundVerificationOutcome.NoMoneyOut or GatewayRefundVerificationOutcome.MoneyWentOut;
}

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

    /// <summary>ข้อความเมื่อการคืนเงินครั้งก่อน "ผลไม่แน่ชัด" (ฝ่ายค้าน E-2) — ตัวเดียวของด่านและหน้าเว็บ</summary>
    public const string OutcomeUnknownMessage =
        "การคืนเงินครั้งก่อนของรายการนี้ผลไม่แน่ชัด (ผู้ให้บริการไม่ตอบ/หมดเวลา — เงินอาจออกไปแล้ว) · "
        + "ระบบล็อกการคืนเงินผ่านระบบของรายการนี้ไว้จนกว่าจะตรวจผลกับผู้ให้บริการ: กดปุ่ม \"ตรวจผลการคืนเงิน\" "
        + "ที่หน้ารายการรับชำระออนไลน์ (ห้ามคืนซ้ำที่แดชบอร์ดผู้ให้บริการก่อนตรวจ — อาจเป็นเงินออกสองรอบ)";

    /// <summary>ตรวจคำขอคืนเงิน — สถานะต้องเป็น "รับเงินแล้ว" · ยอดรวมที่คืนห้ามเกินยอดที่รับ ·
    /// คืนบางส่วนก่อนระบบบันทึกยอดคืน (สถานะ PartiallyRefunded แต่ยอดสะสม = 0) = ไม่รู้ว่าคืนไปเท่าไร ⇒ ห้ามคืนเพิ่มผ่านระบบ ·
    /// ฝ่ายค้าน E-2: การคืนครั้งก่อนผลไม่แน่ชัด (<paramref name="refundOutcomeUnknown"/>) ⇒ ห้ามคืนเพิ่มจนกว่าจะตรวจผลกับผู้ให้บริการ</summary>
    public static GatewayRefundCheck Check(PaymentIntentStatus status, decimal amount, decimal alreadyRefunded,
        decimal? requested, bool refundOutcomeUnknown = false)
    {
        // ก่อนทุกด่าน: เงินอาจออกไปแล้วโดยระบบไม่รู้ยอด — ด่านยอดคงเหลือข้างล่างใช้ยอดสะสมที่อาจต่ำกว่าความจริง
        if (refundOutcomeUnknown)
            return Fail(OutcomeUnknownMessage);

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

    /// <summary>ผลตรวจการคืนเงินที่ "ผลไม่แน่ชัด" กับยอดคืนสะสมที่ผู้ให้บริการรายงาน (ฝ่ายค้าน E-2)</summary>
    public static GatewayRefundVerification Verify(decimal recordedRefunded, decimal? providerRefundedTotal, decimal amount)
    {
        if (providerRefundedTotal is not decimal provider)
            return new(GatewayRefundVerificationOutcome.ProviderSilent, 0m,
                "ผู้ให้บริการไม่ส่งยอดคืนสะสมของรายการนี้มา — ตรวจที่แดชบอร์ดของผู้ให้บริการ แล้วติดต่อผู้ดูแลระบบ "
                + "(ระบบยังล็อกการคืนเงินของรายการนี้ไว้ ห้ามเดาว่าเงินออกหรือไม่)");
        provider = R(provider);
        if (provider > amount + Tolerance)
            return new(GatewayRefundVerificationOutcome.Inconsistent, 0m,
                $"ผู้ให้บริการรายงานยอดคืนสะสม {provider:N2} มากกว่ายอดที่รับ {amount:N2} — ข้อมูลขัดกัน ตรวจที่แดชบอร์ดผู้ให้บริการ (ยังล็อกไว้)");
        if (Math.Abs(provider - recordedRefunded) <= Tolerance)
            return new(GatewayRefundVerificationOutcome.NoMoneyOut, 0m,
                $"ผู้ให้บริการยืนยันยอดคืนสะสม {provider:N2} เท่ากับที่ระบบบันทึก — การคืนครั้งที่ผลไม่แน่ชัดไม่ได้เกิดขึ้น · ปลดล็อกแล้ว คืนใหม่ได้");
        if (provider < recordedRefunded)
            return new(GatewayRefundVerificationOutcome.Inconsistent, 0m,
                $"ผู้ให้บริการรายงานยอดคืนสะสม {provider:N2} น้อยกว่าที่ระบบบันทึก {recordedRefunded:N2} — ข้อมูลขัดกัน "
                + "ตรวจที่แดชบอร์ดผู้ให้บริการ (ยังล็อกไว้)");
        var diff = R(provider - recordedRefunded);
        return new(GatewayRefundVerificationOutcome.MoneyWentOut, diff,
            $"ผู้ให้บริการคืนเงินไปแล้วจริง {diff:N2} บาท (ยอดคืนสะสม {provider:N2}) — ระบบลงบัญชีคืนเงินส่วนนี้ให้แล้ว · ปลดล็อก");
    }

    /// <summary>ต้องมีคนตามออกใบลดหนี้ไหม (ป้าย "คืนเงินแล้ว ยังไม่ออกใบลดหนี้")</summary>
    public static bool NeedsCreditNote(GatewayRefundCreditNoteState state)
        => state is GatewayRefundCreditNoteState.CreditNoteMissing or GatewayRefundCreditNoteState.CannotTrace;

    private static GatewayRefundCheck Fail(string message) => new(false, message, 0m, 0m, false);

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
