using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ตัดสินว่า "เปิด add-on ตัวนี้ ต้องจ่ายเงินก่อนไหม" — ที่เดียวของระบบ** (LDG-P0-03)
///
/// ═══ ที่มา ═══
/// <para>เดิมการเปิด add-on = ติ๊กยอมรับค่าใช้จ่ายแล้วกดเปิด <b>ได้ทันทีฟรี</b>
/// แล้วค่อยเก็บเงินทีหลังผ่าน <c>AddOnMonthlyBillingJob</c> · ไม่มีทั้งช่องจ่ายผ่าน
/// gateway และช่องแนบสลิป และ<b>การที่แอดมินปฏิเสธสลิปไม่ได้ปิดสิทธิ์อะไรเลย</b></para>
///
/// <para><b>ทำไมต้องเป็น pure class</b> — เกณฑ์นี้ตัดสินว่าลูกค้าถูกเรียกเก็บเงินไหม
/// จึงต้องมีเทสต์ล็อกไว้ทุกทิศ (กฎเหล็ก #4 G) และต้องมีที่เดียวเพื่อไม่ให้หน้าเว็บ
/// กับเซิร์ฟเวอร์ตัดสินคนละแบบ (defect class "สำเนามือฝั่ง JS")</para>
/// </summary>
public static class AddOnPaymentPolicy
{
    /// <summary>
    /// ต้องเก็บเงินก่อนเปิดใช้หรือไม่ — <c>true</c> เฉพาะเมื่อครบทุกข้อ:
    /// <list type="number">
    ///   <item>ลูกค้า<b>กดเปิดเอง</b> (<c>OwnerSelfServe</c>) — ของแถมจาก admin
    ///     หรือที่รวมมากับแพ็กเกจไม่เก็บซ้ำ</item>
    ///   <item>วิธีคิดราคาเป็น <b>เหมารายเดือน</b> — แบบต่อหน่วย/ต่อ call เก็บตาม
    ///     ปริมาณที่ใช้จริงทีหลัง จะเก็บล่วงหน้าไม่ได้เพราะยังไม่รู้ยอด</item>
    ///   <item>ราคา &gt; 0 — ยังไม่ตั้งราคา = ใช้ได้ฟรีตามเดิม (ห้ามบล็อกลูกค้า
    ///     เพราะเรายังตั้งราคาไม่เสร็จ)</item>
    ///   <item><b>ไม่อยู่ในช่วงทดลองใช้ฟรี</b> — trial คือการให้ลองก่อนจ่าย
    ///     ถ้าเก็บเงินตอนเปิดก็ไม่ใช่ trial อีกต่อไป</item>
    /// </list>
    /// </summary>
    public static bool RequiresPayment(
        AddOnGrantSource grantSource, PricingMethod? method, decimal? unitPrice,
        DateTime? trialUntil, DateTime nowUtc)
    {
        if (grantSource != AddOnGrantSource.OwnerSelfServe) return false;
        if (method != PricingMethod.FlatMonthly) return false;
        if ((unitPrice ?? 0m) <= 0m) return false;
        if (trialUntil is { } t && t > nowUtc) return false;
        return true;
    }

    /// <summary>สถานะเริ่มต้นตอนกดเปิด — ยังไม่จ่าย = <c>AwaitingPayment</c></summary>
    public static AddOnPaymentStatus InitialStatus(bool requiresPayment)
        => requiresPayment ? AddOnPaymentStatus.AwaitingPayment : AddOnPaymentStatus.NotRequired;

    /// <summary>
    /// ระหว่างที่ยังตรวจสลิปไม่เสร็จ ให้ใช้ฟีเจอร์ได้ไหม
    ///
    /// <para><b>ให้ใช้ได้</b> — แต่ต้องติดป้ายว่ายังไม่ยืนยันการชำระเงิน:
    /// ลูกค้าที่โอนจริงแล้วรอแอดมินตรวจข้ามคืน ไม่ควรถูกปิดฟีเจอร์ที่เพิ่งจ่ายไป
    /// ส่วนคนที่ส่งสลิปปลอมจะถูกปิดตอนแอดมินปฏิเสธ (ซึ่งเป็นด่านที่ทำงานจริงแล้ว)</para>
    ///
    /// <para><c>Rejected</c> คือสถานะเดียวที่ตัดสิทธิ์ — และตอนนั้น
    /// <c>IsEnabled</c> ถูกปิดไปพร้อมกันอยู่แล้ว</para>
    /// </summary>
    public static bool UsableWhilePending(AddOnPaymentStatus status)
        => status != AddOnPaymentStatus.Rejected;

    /// <summary>
    /// ยอดที่ยังต้องชำระของ add-on ตัวหนึ่ง — <b>สูตรอยู่ที่นี่ที่เดียว</b>
    ///
    /// <para>ใช้ <c>AcceptedUnitPrice</c> (ราคาที่ลูกค้า "เห็นและยอมรับ" ตอนกดเปิด)
    /// ไม่ใช่ราคาปัจจุบัน — ราคาอาจถูกปรับหลังจากนั้น และเราเรียกเก็บได้เฉพาะยอด
    /// ที่มีหลักฐานว่าแจ้งไปแล้ว</para>
    ///
    /// <para>⚠️ เคยเขียนซ้ำสองที่ (<c>AddOnPurchaseService.AmountDueAsync</c> กับ
    /// projection ของ <c>MeteringController.GetFeatures</c>) — ยุบมาที่นี่ก่อนจะ
    /// drift เพราะตัวเลขนี้คือยอดที่ขึ้นบนปุ่ม "ชำระออนไลน์" ของลูกค้า</para>
    /// </summary>
    public static decimal AmountDue(AddOnPaymentStatus status, decimal? acceptedUnitPrice)
        => status is AddOnPaymentStatus.NotRequired or AddOnPaymentStatus.Paid
            ? 0m
            : Math.Max(0m, acceptedUnitPrice ?? 0m);

    /// <summary>ข้อความสถานะที่หน้าเว็บ<b>แสดงอย่างเดียว</b> — ห้ามให้ JS แต่งเอง</summary>
    public static string StatusLabel(AddOnPaymentStatus status) => status switch
    {
        AddOnPaymentStatus.NotRequired => "",
        AddOnPaymentStatus.AwaitingPayment => "รอชำระเงิน",
        AddOnPaymentStatus.PendingReview => "รอผู้ดูแลระบบตรวจสอบการชำระเงิน",
        AddOnPaymentStatus.Paid => "ชำระเงินแล้ว",
        AddOnPaymentStatus.Rejected => "การชำระเงินไม่ผ่าน — ฟีเจอร์ถูกปิด",
        _ => status.ToString(),
    };
}
