namespace Accounting.Helpers;

/// <summary>
/// รอบ 235 (ค้างจากรอบ 232 · คำตัดสิน 235-9) — <b>ตัวตัดสินเดียว</b>ว่า "แจ้งเจ้าของ/แอดมินบริษัทว่ามีการชำระค่าบริการรอตรวจสอบ" เมื่อไร · pure
/// <para>เดิม <c>SubscriptionService.SubmitPaymentAsync</c> แจ้ง "มีการชำระเงิน Subscription รอตรวจสอบ" ทันทีที่สร้างรายการ — รวมการต่ออายุผ่านหน้าซื้อแพ็กเกจสาธารณะ
/// ที่เพิ่งสร้างรายการ "รอชำระ" (ยังไม่มีเงิน/สลิป) ⇒ เจ้าของได้ข้อความว่ามีเงินรอตรวจทั้งที่ยังไม่จ่าย</para>
/// <list type="bullet">
/// <item>แจ้งเฉพาะเมื่อมี <b>หลักฐานการจ่าย</b> = แนบสลิป (ครั้งแรกของรายการ — แนบซ้ำไม่แจ้งซ้ำ) · เงินเข้าผ่านช่องทางออนไลน์มีข้อความของตัวเอง
/// (อนุมัติอัตโนมัติ/พักให้แอดมิน — <c>ReviewPaymentLockedAsync</c>/<c>PlatformPaymentAttention</c>) จึงไม่ใช่เส้นนี้</item>
/// <item>สร้างรายการเฉย ๆ (ยังไม่มีสลิป) ⇒ ไม่แจ้ง — เลขอ้างอิงการโอนที่พิมพ์มาเองไม่ใช่หลักฐาน (ไม่มีสลิปให้ตรวจ)</item>
/// </list>
/// ผู้เรียก (ล็อกด้วย <c>tools/required_call_site_check.py</c>): <c>SubmitPaymentAsync</c> · <c>UploadPaymentSlipAsync</c>
/// </summary>
public static class PlatformPaymentReviewNotice
{
    public const string Title = "มีการชำระเงิน Subscription รอตรวจสอบ";

    /// <param name="hasSlipNow">รายการมีสลิปแนบหลังการกระทำนี้</param>
    /// <param name="hadSlipBefore">มีสลิปอยู่ก่อนการกระทำนี้ (แนบซ้ำ/เปลี่ยนไฟล์ ⇒ ไม่แจ้งซ้ำ)</param>
    /// <param name="isPending">รายการยังรอตรวจ (อนุมัติ/ปฏิเสธ/ยกเลิกแล้ว ⇒ ไม่แจ้ง)</param>
    public static bool ShouldNotify(bool hasSlipNow, bool hadSlipBefore, bool isPending)
        => isPending && hasSlipNow && !hadSlipBefore;

    /// <summary>ข้อความแจ้ง (เลขรายการ · ยอดที่โอน)</summary>
    public static string Message(string paymentNumber, decimal amount)
        => $"การชำระเงิน {paymentNumber} จำนวน {amount:N2} THB แนบสลิปแล้ว — รอการตรวจสอบ";
}
