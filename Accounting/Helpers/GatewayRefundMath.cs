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
    /// <summary>ข้อมูลขัดกัน (น้อยกว่าที่บันทึก/มากกว่ายอดรับ · ส่วนต่างไม่เท่ายอดที่พยายามคืน) — ล็อกต่อ ให้คนตรวจ</summary>
    Inconsistent = 3,
    /// <summary>ยอดสะสมยังไม่ขยับ แต่เพิ่งพยายามคืนไม่ถึง <see cref="GatewayRefundMath.MinVerifyWait"/> — คำขออาจยังค้างที่ผู้ให้บริการ
    /// ⇒ ยังตัดสินว่า "ไม่มีเงินออก" ไม่ได้ (ฝ่ายค้าน E2-2) · ล็อกต่อ</summary>
    TooEarly = 4,
}

/// <summary>ความหมายของ HTTP status ที่ผู้ให้บริการตอบคำขอคืนเงิน (ฝ่ายค้าน E2-1)</summary>
public enum GatewayRefundHttpOutcome
{
    /// <summary>2xx — ผู้ให้บริการรับคำขอคืนเงินแล้ว</summary>
    Succeeded = 0,
    /// <summary>4xx (ยกเว้น 408) — ผู้ให้บริการปฏิเสธคำขอชัดเจน · ไม่มีเงินออก</summary>
    Refused = 1,
    /// <summary>5xx · 408 · อื่น ๆ — ไม่รู้ว่าเงินออกหรือยัง (ผู้ให้บริการอาจบันทึกแล้วแต่ตอบผิดพลาด) ⇒ ต้องล็อกเหมือนหมดเวลา</summary>
    Unknown = 2,
}

/// <summary>ผลตัดสินด้วยมือของการคืนเงินที่ผลไม่แน่ชัด (ฝ่ายค้าน E2-3) — คนดูแดชบอร์ดผู้ให้บริการแล้วบันทึกผล</summary>
public enum GatewayRefundManualDecision
{
    /// <summary>ไม่ได้ตัดสิน/ค่าที่อ่านไม่ออก — ต้องปฏิเสธ (ไม่ตกเป็นค่าใดค่าหนึ่ง)</summary>
    Unspecified = 0,
    /// <summary>ผู้ให้บริการไม่มีรายการคืนของครั้งนี้ ⇒ ปลดล็อก ไม่ลงบัญชี</summary>
    NoMoneyOut = 1,
    /// <summary>ผู้ให้บริการคืนไปแล้วจริง ⇒ ลงบัญชีคืนเงินตามยอดที่คนยืนยัน + ปลดล็อก</summary>
    MoneyWentOut = 2,
}

/// <summary>ผลตรวจคำขอบันทึกผลด้วยมือ (<c>AmountToBook</c> = ยอดที่ต้องลงบัญชี เฉพาะ MoneyWentOut)</summary>
public readonly record struct GatewayRefundManualCheck(bool Ok, string? Message, decimal AmountToBook);

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

/// <summary>วันที่ใบสำคัญ + หมายเหตุ ของเงินคืนที่ยืนยันทีหลัง (<see cref="GatewayRefundMath.PastRefundBooking"/>) — <c>Note</c> null = ลงวันที่เงินออกจริง</summary>
public readonly record struct GatewayPastRefundBooking(DateTime EntryDate, string? Note);

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
        + "ที่หน้ารายการรับชำระออนไลน์ (ห้ามคืนซ้ำที่แดชบอร์ดผู้ให้บริการก่อนตรวจ — อาจเป็นเงินออกสองรอบ) · "
        + "ถ้าผู้ให้บริการไม่ส่งผลหรือข้อมูลขัดกัน ให้เจ้าของกิจการกด \"บันทึกผลด้วยมือ\" พร้อมหลักฐานจากแดชบอร์ด";

    /// <summary>เวลาขั้นต่ำหลังพยายามคืน ก่อนจะยอมตัดสินว่า "ไม่มีเงินออก" (ฝ่ายค้าน E2-2) — คำขอที่ฝั่งเราหมดเวลา (20 วินาที) หรือผู้ใช้ปิดหน้า
    /// อาจยังถูกประมวลผลอยู่ที่ผู้ให้บริการ · ยอดสะสมที่อ่านเร็วเกินไปจึงยังไม่ใช่หลักฐาน · "เงินออกแล้ว" ตัดสินได้ทันที (ไม่ต้องรอ)</summary>
    public static readonly TimeSpan MinVerifyWait = TimeSpan.FromMinutes(10);

    /// <summary>แปลง HTTP status ของคำขอคืนเงินเป็นความหมาย (ฝ่ายค้าน E2-1) — ตัวเดียวของ adapter ทุกเจ้า
    ///
    /// <para>เดิม adapter ถือว่า "ไม่ใช่ 2xx = ปฏิเสธ" ⇒ 502/504 จาก edge/proxy หรือ 500 หลังผู้ให้บริการบันทึกแล้ว กลายเป็น "ปฏิเสธ" ·
    /// ไม่มีล็อก · ผู้ใช้กดใหม่ได้ 2xx ⇒ <b>คืนสองรอบ</b> (ทางเดียวกับที่ E-2 ปิดไว้สำหรับหมดเวลา) · 4xx = ผู้ให้บริการตัดสินแล้วว่าไม่ทำ
    /// (ยกเว้น 408 = หมดเวลาระหว่างทาง ไม่รู้ผล) · อย่างอื่นทั้งหมด (5xx · 1xx/3xx ที่ไม่คาด) = ไม่รู้</para></summary>
    public static GatewayRefundHttpOutcome ClassifyRefundHttpStatus(int httpStatus)
        => httpStatus switch
        {
            >= 200 and < 300 => GatewayRefundHttpOutcome.Succeeded,
            408 => GatewayRefundHttpOutcome.Unknown,
            >= 400 and < 500 => GatewayRefundHttpOutcome.Refused,
            _ => GatewayRefundHttpOutcome.Unknown,
        };

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

    /// <summary>ผลตรวจการคืนเงินที่ "ผลไม่แน่ชัด" กับยอดคืนสะสมที่ผู้ให้บริการรายงาน (ฝ่ายค้าน E-2 · E2-2 · E2-7)
    ///
    /// <para><paramref name="attemptedAmount"/> = ยอดของครั้งที่ผลไม่แน่ชัด (null = แถวก่อนมีคอลัมน์และเติมย้อนหลังไม่ได้) ·
    /// <paramref name="markerRefundAmount"/> = ยอดของรายการคืนที่มีเครื่องหมายของครั้งนี้ในรายการคืนของผู้ให้บริการ (null = ไม่พบ/ไม่มีรายการ —
    /// <b>ไม่ใช่</b>หลักฐานว่าไม่มีเงินออก)</para>
    ///
    /// <para>กติกา: ส่วนต่าง (ผู้ให้บริการ − ที่บันทึก) = 0 ⇒ ไม่มีเงินออก <b>เฉพาะเมื่อพ้น <see cref="MinVerifyWait"/> แล้ว</b> (E2-2 — ก่อนนั้นคำขออาจยังค้าง) ·
    /// ส่วนต่าง = ยอดที่พยายามคืน (หรือยอดของรายการที่มีเครื่องหมาย) ⇒ เงินออกแล้ว ลงบัญชีส่วนต่าง · ส่วนต่างอื่น ⇒ ขัดกัน (E2-7 — เดิมลงบัญชีทั้งก้อน
    /// ⇒ คืนเงินที่ลงบัญชีมือไปแล้ว/คืนที่แดชบอร์ด ถูกลงซ้ำ) · ทุกกรณีที่ไม่รู้ ล็อกต่อ (ไม่ประทับผลเอง)</para></summary>
    public static GatewayRefundVerification Verify(decimal recordedRefunded, decimal? providerRefundedTotal, decimal amount,
        decimal? attemptedAmount, decimal? markerRefundAmount, DateTime attemptAtUtc, DateTime nowUtc)
    {
        if (providerRefundedTotal is not decimal provider)
            return new(GatewayRefundVerificationOutcome.ProviderSilent, 0m,
                "ผู้ให้บริการไม่ส่งยอดคืนสะสมของรายการนี้มา — ตรวจที่แดชบอร์ดของผู้ให้บริการ แล้วให้เจ้าของกิจการกด \"บันทึกผลด้วยมือ\" "
                + "พร้อมหลักฐาน (ระบบยังล็อกการคืนเงินของรายการนี้ไว้ ห้ามเดาว่าเงินออกหรือไม่)");
        provider = R(provider);
        if (provider > amount + Tolerance)
            return new(GatewayRefundVerificationOutcome.Inconsistent, 0m,
                $"ผู้ให้บริการรายงานยอดคืนสะสม {provider:N2} มากกว่ายอดที่รับ {amount:N2} — ข้อมูลขัดกัน ตรวจที่แดชบอร์ดผู้ให้บริการ (ยังล็อกไว้)");
        if (provider < recordedRefunded - Tolerance)
            return new(GatewayRefundVerificationOutcome.Inconsistent, 0m,
                $"ผู้ให้บริการรายงานยอดคืนสะสม {provider:N2} น้อยกว่าที่ระบบบันทึก {recordedRefunded:N2} — ข้อมูลขัดกัน "
                + "ตรวจที่แดชบอร์ดผู้ให้บริการ (ยังล็อกไว้)");

        var diff = R(provider - recordedRefunded);
        if (Math.Abs(diff) <= Tolerance)
        {
            if (markerRefundAmount is decimal found)
                return new(GatewayRefundVerificationOutcome.Inconsistent, 0m,
                    $"พบรายการคืนเงินของครั้งนี้ที่ผู้ให้บริการ ({R(found):N2}) แต่ยอดคืนสะสมเท่ากับที่ระบบบันทึก — ข้อมูลขัดกัน "
                    + "ตรวจที่แดชบอร์ดผู้ให้บริการ (ยังล็อกไว้)");
            var readyAt = attemptAtUtc + MinVerifyWait;
            if (nowUtc < readyAt)
                return new(GatewayRefundVerificationOutcome.TooEarly, 0m,
                    $"ยอดคืนสะสมยังไม่ขยับ แต่เพิ่งพยายามคืนไม่ถึง {MinVerifyWait.TotalMinutes:0} นาที — คำขออาจยังค้างที่ผู้ให้บริการ · "
                    + $"กดตรวจอีกครั้งหลัง {readyAt.AddHours(7):HH:mm} น. (เวลาไทย · ยังล็อกไว้ ห้ามคืนซ้ำ)");
            return new(GatewayRefundVerificationOutcome.NoMoneyOut, 0m,
                $"ผู้ให้บริการยืนยันยอดคืนสะสม {provider:N2} เท่ากับที่ระบบบันทึก — การคืนครั้งที่ผลไม่แน่ชัดไม่ได้เกิดขึ้น · ปลดล็อกแล้ว คืนใหม่ได้");
        }

        // เงินออกเพิ่มจากที่บันทึก — ต้องเท่ายอดของครั้งนี้ (เครื่องหมายชนะยอดที่จดไว้) ไม่งั้นมีการคืนนอกระบบปนอยู่ ⇒ ให้คนตัดสิน
        if (markerRefundAmount is decimal m && attemptedAmount is decimal a0 && Math.Abs(R(m) - R(a0)) > Tolerance)
            return new(GatewayRefundVerificationOutcome.Inconsistent, 0m,
                $"รายการคืนของครั้งนี้ที่ผู้ให้บริการ ({R(m):N2}) ไม่เท่ายอดที่สั่งคืน ({R(a0):N2}) — ข้อมูลขัดกัน "
                + "ตรวจที่แดชบอร์ดผู้ให้บริการแล้วให้เจ้าของกิจการบันทึกผลด้วยมือ (ยังล็อกไว้)");
        var expected = markerRefundAmount ?? attemptedAmount;
        if (expected is not decimal exp)
            return new(GatewayRefundVerificationOutcome.Inconsistent, 0m,
                $"ผู้ให้บริการคืนมากกว่าที่ระบบบันทึก {diff:N2} บาท แต่ระบบไม่มียอดของครั้งที่ผลไม่แน่ชัด (รายการก่อนมีการเก็บยอด) — "
                + "แยกไม่ได้ว่าเป็นครั้งนี้ทั้งหมดหรือมีการคืนนอกระบบปน · ให้เจ้าของกิจการตรวจแดชบอร์ดแล้วบันทึกผลด้วยมือ (ยังล็อกไว้)");
        if (Math.Abs(diff - R(exp)) > Tolerance)
            return new(GatewayRefundVerificationOutcome.Inconsistent, 0m,
                $"ผู้ให้บริการคืนมากกว่าที่ระบบบันทึก {diff:N2} บาท แต่ครั้งที่ผลไม่แน่ชัดสั่งคืน {R(exp):N2} บาท — ไม่ตรงกัน "
                + "(อาจมีการคืนที่แดชบอร์ด หรือคืนที่ลงบัญชีด้วยมือไปแล้ว) · ระบบไม่ลงบัญชีทั้งก้อนให้ (เสี่ยงลงซ้ำ) — "
                + "ให้เจ้าของกิจการตรวจแล้วบันทึกผลด้วยมือ (ยังล็อกไว้)");
        return new(GatewayRefundVerificationOutcome.MoneyWentOut, diff,
            $"ผู้ให้บริการคืนเงินไปแล้วจริง {diff:N2} บาท (ยอดคืนสะสม {provider:N2}) — ระบบลงบัญชีคืนเงินส่วนนี้ให้แล้ว · ปลดล็อก");
    }

    /// <summary>ตรวจคำขอ "บันทึกผลด้วยมือ" ของการคืนเงินที่ผลไม่แน่ชัด (ฝ่ายค้าน E2-3 — ทางไปต่อเมื่อผู้ให้บริการเงียบ/ข้อมูลขัดกัน)
    ///
    /// <para>คนตัดสินจากแดชบอร์ดผู้ให้บริการ ⇒ ต้องมีหลักฐานเป็นข้อความเสมอ · "เงินออก" ต้องมียอด (ไม่เกินยอดที่ยังคืนได้) + เลขอ้างอิงการคืน
    /// ของผู้ให้บริการ · "ไม่มีเงินออก" ต้องพ้น <see cref="MinVerifyWait"/> เหมือนการตรวจอัตโนมัติ (คนก็ดูเร็วเกินไปได้)</para></summary>
    public static GatewayRefundManualCheck CheckManualResolution(bool outcomeUnknown, GatewayRefundManualDecision decision,
        decimal? amount, string? providerRefundRef, string? evidence, decimal recordedRefunded, decimal intentAmount,
        DateTime attemptAtUtc, DateTime nowUtc)
    {
        if (!outcomeUnknown)
            return new(false, "รายการนี้ไม่มีการคืนเงินที่ผลไม่แน่ชัด — ไม่ต้องบันทึกผล", 0m);
        if (string.IsNullOrWhiteSpace(evidence))
            return new(false, "กรุณาระบุหลักฐาน — สิ่งที่เห็นในแดชบอร์ดผู้ให้บริการ (ผู้สอบบัญชีต้องเห็นว่าตัดสินจากอะไร)", 0m);
        if (decision == GatewayRefundManualDecision.NoMoneyOut)
        {
            if (nowUtc < attemptAtUtc + MinVerifyWait)
                return new(false, $"เพิ่งพยายามคืนไม่ถึง {MinVerifyWait.TotalMinutes:0} นาที — คำขออาจยังค้างที่ผู้ให้บริการ "
                    + "ยังยืนยันว่า \"ไม่มีเงินออก\" ไม่ได้", 0m);
            return new(true, null, 0m);
        }
        if (decision == GatewayRefundManualDecision.MoneyWentOut)
        {
            var remaining = Remaining(intentAmount, recordedRefunded);
            var paid = R(amount ?? 0m);
            if (paid <= 0m || paid > remaining + Tolerance)
                return new(false, $"ยอดที่ผู้ให้บริการคืนไปต้องมากกว่า 0 และไม่เกินยอดที่ยังคืนได้ ({remaining:N2})", 0m);
            if (string.IsNullOrWhiteSpace(providerRefundRef))
                return new(false, "กรุณาระบุเลขอ้างอิงการคืนเงินจากแดชบอร์ดผู้ให้บริการ — ใช้เป็นเลขอ้างอิงของใบสำคัญคืนเงิน", 0m);
            return new(true, null, paid);
        }
        return new(false, "กรุณาเลือกผล: \"ไม่มีเงินออก\" หรือ \"เงินออกแล้ว\"", 0m);
    }

    /// <summary>วันที่ใบสำคัญของเงินคืนที่ "ผลไม่แน่ชัด" แล้วยืนยันทีหลัง (ตรวจผล/บันทึกผลด้วยมือ) — รอบ 200 ทีม G · review198-E2 E2-12 + คำถามเจ้าของข้อ 4
    ///
    /// <para>═══ ที่มา ═══ เส้นตรวจผล/บันทึกผลด้วยมือลงใบสำคัญ<b>วันนี้</b> ทั้งที่เหตุการณ์และ <c>LastRefundedAt</c> = เวลาที่พยายามคืน ⇒
    /// พยายามคืนสิ้นเดือน ตรวจผลต้นเดือนถัดไป = เงินออกเดือนหนึ่ง ใบสำคัญอยู่อีกเดือน (ยอดบัญชีพัก/ลูกหนี้ ณ สิ้นเดือนผิด)</para>
    /// <para>═══ กติกา (ทิศที่มองเห็นและย้อนได้) ═══ งวดของวันที่เงินออกยังเปิด ⇒ ลงวันนั้น · ปิดแล้ว ⇒ ลงวันนี้ <b>พร้อมข้อความบอกวันที่เงินออกจริง</b>
    /// บนใบสำคัญ (ปรับปรุงในงวดปัจจุบัน — แก้งวดที่ปิดไม่ได้) · ห้ามเลื่อนเงียบ</para></summary>
    public static GatewayPastRefundBooking PastRefundBooking(DateTime attemptAtUtc, DateTime nowUtc, bool attemptPeriodClosed)
    {
        var attemptDate = ThaiDate.CalendarDateUtc(attemptAtUtc);
        var today = ThaiDate.CalendarDateUtc(nowUtc);
        if (!attemptPeriodClosed || attemptDate >= today) return new GatewayPastRefundBooking(attemptDate, null);
        return new GatewayPastRefundBooking(today,
            $"เงินออกจริงวันที่ {ThaiDate.ToThaiDisplayString(attemptAtUtc)} แต่งวดบัญชีของวันนั้นปิดแล้ว — ลงบัญชีวันนี้ (ปรับปรุงในงวดปัจจุบัน)");
    }

    /// <summary>ต้องมีคนตามออกใบลดหนี้ไหม (ป้าย "คืนเงินแล้ว ยังไม่ออกใบลดหนี้")</summary>
    public static bool NeedsCreditNote(GatewayRefundCreditNoteState state)
        => state is GatewayRefundCreditNoteState.CreditNoteMissing or GatewayRefundCreditNoteState.CannotTrace;

    private static GatewayRefundCheck Fail(string message) => new(false, message, 0m, 0m, false);

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
