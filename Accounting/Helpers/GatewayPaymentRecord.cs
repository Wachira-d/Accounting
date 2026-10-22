using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>"เงินก้อนนี้ถูกบันทึกแล้วหรือยัง" ของเงินที่เข้าทางระบบรับชำระออนไลน์ — ตัวตั้งตัวเดียว</b>
/// (BOOKING_REVENUE_AUDIT · รอบ 205 ทีม E) · pure
///
/// <para>ที่มา (บั๊กจริงสองตัว):</para>
/// <list type="number">
///   <item><b>ที่พัก — ชำระยอดคงเหลือออนไลน์หาย</b>: handler เดิมข้ามเมื่อ <c>DepositPaid ≥ ยอดของรายการชำระ</c> ⇒ มัดจำ 1,500 แล้วแขกจ่าย
///     ยอดคงเหลือ 1,500 ออนไลน์ = "ซ้ำ" ⇒ <b>เงินเข้า gateway แต่ไม่มีใบ ไม่มี JE ไม่มีธง</b> (ยอดเท่ากันหรือน้อยกว่ามัดจำหายทุกครั้ง) ·
///     ตอนนี้ "ซ้ำ" ตัดสินด้วย<b>เลขอ้างอิงของรายการชำระ</b>เท่านั้น — ยอดเงินไม่ใช่ตัวตน</item>
///   <item><b>คำสั่งซื้อหน้าเว็บ — จ่ายออนไลน์แล้วออเดอร์ไม่ถูกยืนยันเลย</b>: <c>ConfirmPaymentAsync(paymentId: null)</c> หยิบแถว
///     <c>SiteOrderPayment</c> ล่าสุด แต่แถวนี้เกิดจากการแนบสลิปเท่านั้น ⇒ ออเดอร์ที่จ่ายผ่าน gateway โดยไม่แนบสลิป = "ไม่พบรายการชำระเงิน"
///     ⇒ เงินเข้าแต่ไม่มีเอกสาร/JE/ตัดสต็อก · ตอนนี้ handler สร้างแถวของรายการชำระนั้นเอง (คีย์ = <see cref="Reference"/>)</item>
/// </list>
/// </summary>
public static class GatewayPaymentRecord
{
    /// <summary>รหัสกฎของ "เงินเข้าแล้วแต่หาต้นทางไม่พบ" — handler โยน · <c>PaymentIntentService</c> ใช้ตัดสินว่าต้องแจ้งเจ้าของเอง</summary>
    public const string SourceMissingRuleCode = "GATEWAY-SOURCE-MISSING";

    /// <summary>เลขอ้างอิงที่ผูกการบันทึกรับเงินกับรายการชำระออนไลน์ 1 รายการ — ใช้ทั้งตอนเขียนและตอนกันซ้ำ
    /// (canonical function เดียว · เดิมอยู่ใน <c>DocumentPaymentHandler.PaymentReference</c>)</summary>
    public static string Reference(Guid intentId) => $"PAY-INTENT-{intentId:N}";

    /// <summary>เลขอ้างอิงที่ประทับบนการจองที่พัก — เลขของผู้ให้บริการ (พนักงานใช้ค้นในแดชบอร์ด gateway) · ไม่มีเลข ⇒ <see cref="Reference"/>
    /// (เดิมส่ง null ⇒ ด่านกันซ้ำของ <c>ConfirmAsync</c> ไม่ทำงานเลย)</summary>
    public static string ReservationReference(Guid intentId, string? providerRef)
        => string.IsNullOrWhiteSpace(providerRef) ? Reference(intentId) : providerRef.Trim();

    /// <summary>การจองบันทึกรับเงินก้อนนี้ไปแล้วไหม — <b>ตัดสินด้วยเลขอ้างอิงเท่านั้น</b> ไม่ใช่ยอดเงิน
    /// (ยอดเท่ากันไม่ได้แปลว่าเงินก้อนเดียวกัน: มัดจำ 1,500 + ยอดคงเหลือ 1,500 คือเงินสองก้อน)</summary>
    /// <param name="recordedReference">เลขอ้างอิงล่าสุดที่บันทึกบนการจอง (<c>LodgingReservation.PaymentReference</c>)</param>
    /// <param name="depositPaid">ยอดที่รับบนการจองแล้ว — 0 = ยังไม่เคยรับ ⇒ ไม่ซ้ำแน่นอน</param>
    /// <param name="incomingReference">เลขอ้างอิงของเงินก้อนที่กำลังจะบันทึก</param>
    public static bool AlreadyRecordedOnReservation(string? recordedReference, decimal depositPaid, string? incomingReference)
        => !string.IsNullOrWhiteSpace(incomingReference)
           && depositPaid > 0m
           && string.Equals(recordedReference?.Trim(), incomingReference.Trim(), StringComparison.Ordinal);

    /// <summary>วิธีชำระบนแถว <c>SiteOrderPayment</c>/ใบรับชำระ ของเงินที่เข้าทาง gateway</summary>
    public static PaymentMethod MethodOf(PaymentMethodKind kind) => kind switch
    {
        PaymentMethodKind.PromptPay => PaymentMethod.PromptPay,
        PaymentMethodKind.Card => PaymentMethod.CreditCard,
        PaymentMethodKind.MobileBanking or PaymentMethodKind.InternetBanking => PaymentMethod.BankTransfer,
        PaymentMethodKind.TrueMoney => PaymentMethod.EWallet,
        _ => PaymentMethod.Other,
    };

    /// <summary>ข้อความของ exception เมื่อเงินเข้าแล้วแต่หาต้นทางไม่พบ — handler โยนแทน <c>LogError</c> + <c>return</c> เงียบ
    /// (เดิมประวัติของรายการชำระไม่มีร่องรอย · <c>PaymentIntentService</c> บันทึกข้อความนี้ลงประวัติ + แจ้งเจ้าของ · รหัส <see cref="SourceMissingRuleCode"/>)</summary>
    public static string SourceMissingMessage(string what, Guid intentId, decimal amount)
        => $"เงินเข้าแล้ว {amount:N2} บาท (รายการชำระ {intentId:N}) แต่หา{what}ไม่พบ — ต้องบันทึกรับเงินด้วยมือหรือคืนเงินลูกค้า";
}
