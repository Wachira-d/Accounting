using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 218 (คำตัดสินข้อ 177) — <b>ตัวตัดสินเดียว</b>ของสลิปโอนเงินของการจองนัดหมาย (แนบ/ตรวจ/ปฏิเสธ) ·
/// ความจริงเรื่องเงิน = <c>PaymentIntent</c> (manual-slip) — ร้านยืนยัน ⇒ <c>ApplyChargeAsync(Succeeded)</c> ⇒ <c>SiteBookingPaymentHandler</c> ⇒
/// <c>RecordDepositPaymentAsync</c> (ผู้เขียนยอดเงินคนเดียว) · ไฟล์สลิป = PII (ชื่อ/เลขบัญชีผู้โอน) ⇒ ที่เก็บกลาง ไม่ใช่ static สาธารณะ
/// </summary>
public static class BookingSlipRule
{
    public const string RuleCode = "CMS-BOOKING-SLIP";
    public const string AttachmentEntityType = "SiteBooking";
    /// <summary>ขนาดสูงสุดของไฟล์สลิป (ตรงกับ <c>RequestSizeLimit</c> ของ endpoint)</summary>
    public const long MaxBytes = 10L * 1024 * 1024;
    public const int MaxReasonLength = 500;

    /// <summary>ลูกค้าแนบสลิปได้ไหม — คืนข้อความไทย (null = ได้)</summary>
    public static string? UploadProblem(BookingStatus status, decimal due, bool transferReady, bool slipPending, long fileBytes)
    {
        if (fileBytes <= 0) return "กรุณาเลือกไฟล์สลิป";
        if (fileBytes > MaxBytes) return "ไฟล์สลิปใหญ่เกิน 10 MB — ถ่ายภาพหน้าจอสลิปแล้วแนบใหม่";
        if (status is BookingStatus.Cancelled or BookingStatus.NoShow)
            return "การจองนี้ถูกยกเลิกแล้ว — หากโอนเงินไปแล้ว กรุณาติดต่อร้านพร้อมเลขที่การจอง";
        if (status is not (BookingStatus.Pending or BookingStatus.Confirmed)) return "การจองนี้ไม่มียอดให้ชำระล่วงหน้าแล้ว";
        if (due <= 0.005m) return "การจองนี้ชำระครบแล้ว — ไม่ต้องแนบสลิปเพิ่ม (ถ้าโอนเกิน ติดต่อร้านเพื่อขอคืน)";
        if (!transferReady) return "ร้านนี้ไม่รับชำระด้วยการโอน/แนบสลิป — ชำระออนไลน์ หรือติดต่อร้าน";
        if (slipPending) return "มีสลิปรอร้านตรวจอยู่แล้ว — รอร้านตรวจก่อน (ถ้าแนบผิดไฟล์ ติดต่อร้าน)";
        return null;
    }

    /// <summary>เหตุผลปฏิเสธสลิป — ต้องมี (ลูกค้าเห็นตรง ๆ ว่าต้องทำอะไรต่อ) · ตัดความยาว</summary>
    public static (string? Reason, string? Error) NormalizeRejectReason(string? raw)
    {
        var r = raw?.Trim() ?? "";
        if (r.Length == 0) return (null, "ต้องระบุเหตุผลที่สลิปไม่ผ่าน — ข้อความนี้คือสิ่งเดียวที่ลูกค้าจะเห็นว่าต้องทำอะไรต่อ");
        return (r.Length > MaxReasonLength ? r[..MaxReasonLength] : r, null);
    }

    /// <summary>ร้านตรวจสลิปได้ไหม (ยืนยัน/ปฏิเสธ) — ต้องมีสลิปรอตรวจ + รายการชำระของสลิป · null = ได้</summary>
    public static string? DecideProblem(Guid? slipIntentId, DateTime? slipUploadedAt)
        => slipIntentId == null || slipUploadedAt == null ? "การจองนี้ไม่มีสลิปรอตรวจ (อาจถูกตรวจไปแล้ว — รีเฟรชหน้า)" : null;

    /// <summary>เงินที่เพิ่งบันทึกปิดสถานะ "สลิปรอตรวจ" ไหม — เลขอ้างอิงคือรายการชำระของสลิปนี้ หรือรับครบมัดจำแล้ว</summary>
    public static bool SettlesPendingSlip(Guid? slipIntentId, string? recordedReference, decimal depositAmount, decimal paidAmount)
        => (slipIntentId is Guid id && string.Equals(recordedReference?.Trim(), GatewayPaymentRecord.Reference(id), StringComparison.Ordinal))
           || CmsBookingOnlinePayment.DepositSettled(depositAmount, paidAmount);

    private static string Baht(decimal v) => v.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>แจ้งร้าน: ลูกค้าแนบสลิปของการจอง (ShopNotify — ไม่นับโควตา)</summary>
    public static CmsNotification ShopSlipUploaded(string siteName, string bookingNumber, string serviceName, string? customerName, decimal amount)
        => new($"ลูกค้าแนบสลิปการจอง #{bookingNumber} — {siteName}",
            $"ลูกค้า {(string.IsNullOrWhiteSpace(customerName) ? "ลูกค้า" : customerName.Trim())} แนบสลิปชำระล่วงหน้าของการจอง {bookingNumber} ({serviceName}) ยอด {Baht(amount)} บาท\n"
            + "ตรวจยอดในบัญชีธนาคาร แล้วกด \"ยืนยันสลิป\" หรือ \"ปฏิเสธสลิป\" ที่เมนู การจอง");

    /// <summary>แจ้งลูกค้า: สลิปไม่ผ่าน + เหตุผล + ทางไปต่อ (ลิงก์จัดการการจองต่อท้ายโดยผู้เรียก)</summary>
    public static CmsNotification CustomerSlipRejected(string siteName, string bookingNumber, string serviceName, string? customerName, string reason,
        DateTime? holdUntilUtc)
        => new($"สลิปการจอง #{bookingNumber} ยังไม่ผ่าน — {serviceName}",
            $"เรียน {(string.IsNullOrWhiteSpace(customerName) ? "ลูกค้า" : customerName.Trim())}\n{siteName} ตรวจสลิปของการจอง {bookingNumber} แล้วยังไม่ผ่าน\nเหตุผล: {reason}\n"
            + "กรุณาแนบสลิปใหม่หรือชำระออนไลน์ที่ลิงก์จัดการการจอง"
            + (holdUntilUtc is DateTime u ? $" ภายใน {u.AddHours(7).ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)} น." : ""));

    /// <summary>ปฏิเสธสลิปแล้ว ⇒ ต่อกำหนดชำระให้ส่งใหม่ (ใบที่ถือรอบอยู่เท่านั้น · ไม่เกินเวลานัด)</summary>
    public static DateTime? HoldAfterReject(DateTime? currentHoldUtc, DateTime nowUtc, DateTime startUtc)
    {
        if (currentHoldUtc == null) return null;
        var extended = nowUtc.AddMinutes(BookingPaymentPolicy.SlipResubmitGraceMinutes);
        var target = extended > currentHoldUtc.Value ? extended : currentHoldUtc.Value;
        return target > startUtc ? startUtc : target;
    }
}
