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

    /// <summary>ร้านตรวจสลิปได้ไหม (ยืนยัน/ปฏิเสธ) — ต้องมีสลิปรอตรวจ + รายการชำระของสลิป + การจองยังไม่ยกเลิก/ไม่มา (ฝ่ายค้าน S2) · null = ได้</summary>
    public static string? DecideProblem(Guid? slipIntentId, DateTime? slipUploadedAt, BookingStatus status = BookingStatus.Pending)
    {
        if (status is BookingStatus.Cancelled or BookingStatus.NoShow)
            return "การจองนี้ถูกยกเลิก/บันทึกไม่มาแล้ว — ยืนยันสลิปไม่ได้ · ถ้าเงินเข้าแล้วจริง ให้คืนเงินลูกค้า หรือออกใบเสร็จแยกที่หน้าเอกสาร (อ้างเลขการจอง)";
        return slipIntentId == null || slipUploadedAt == null ? "การจองนี้ไม่มีสลิปรอตรวจ (อาจถูกตรวจไปแล้ว — รีเฟรชหน้า)" : null;
    }

    /// <summary>ฝ่ายค้านรอบ 218 (S1): "ยืนยันด้วยมือ" ที่หน้ารายการชำระของ intent สลิปการจอง — ได้เฉพาะเมื่อการจองยังรอตรวจสลิปของ intent นี้อยู่
    /// (ไม่งั้น = เงินก้อนเดิมถูกบันทึกไปแล้ว/ถูกแทน/ยกเลิก ⇒ ยืนยันซ้ำ = รับเงินสองครั้ง) · null = ได้</summary>
    public static string? ManualConfirmProblem(Guid intentId, Guid? bookingSlipIntentId, DateTime? bookingSlipUploadedAt, BookingStatus bookingStatus)
    {
        if (bookingSlipIntentId != intentId || bookingSlipUploadedAt == null)
            return "รายการนี้เป็นสลิปของการจองที่ไม่ได้รอตรวจแล้ว (ถูกตรวจ/แทนด้วยการรับเงินอื่น/ยกเลิก) — ยืนยันซ้ำจะรับเงินสองครั้ง · ตรวจที่หน้าการจอง";
        return DecideProblem(bookingSlipIntentId, bookingSlipUploadedAt, bookingStatus);
    }

    /// <summary>ความยาวสูงสุดของบันทึกตอนยืนยันสลิป (ไม่ใส่ใน ConfirmedBy — ฝ่ายค้าน S3)</summary>
    public const int MaxVerifyNoteLength = 300;

    public static string? NormalizeVerifyNote(string? raw)
    {
        var t = raw?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > MaxVerifyNoteLength ? t[..MaxVerifyNoteLength] : t;
    }

    /// <summary>ฝ่ายค้านรอบ 218 (B2): วันที่รับเงินของสลิป = วันที่โอน (tax point §78/1 ของใบรับมัดจำ) ไม่ใช่วันที่ร้านกดยืนยัน ·
    /// ไม่ระบุ ⇒ เวลาที่ลูกค้าแนบสลิป · ระบุ (วันที่ไทย) ⇒ เที่ยงวันเวลาไทยของวันนั้น · ห้ามอนาคต · ห้ามก่อนวันที่จอง · ห้ามเก่าเกินกฎเดิม (ValidatePaidAt)</summary>
    public static (DateTime PaidAtUtc, string? Error) ResolvePaidAt(DateOnly? transferDateBkk, DateTime slipUploadedAtUtc, DateTime bookingCreatedAtUtc, DateTime nowUtc)
    {
        var paidAt = transferDateBkk is DateOnly d
            ? DateTime.SpecifyKind(d.ToDateTime(new TimeOnly(12, 0)).AddHours(-7), DateTimeKind.Utc)
            : slipUploadedAtUtc;
        var todayBkk = DateOnly.FromDateTime(nowUtc.AddHours(7));
        if (DateOnly.FromDateTime(paidAt.AddHours(7)) > todayBkk) return (paidAt, "วันที่โอนเป็นอนาคตไม่ได้");
        if (DateOnly.FromDateTime(paidAt.AddHours(7)) < DateOnly.FromDateTime(bookingCreatedAtUtc.AddHours(7)))
            return (paidAt, $"วันที่โอนต้องไม่ก่อนวันที่จอง ({bookingCreatedAtUtc.AddHours(7):dd/MM/yyyy})");
        if (CmsBookingOnlinePayment.ValidatePaidAt(paidAt, nowUtc) is string err) return (paidAt, err);
        return (paidAt, null);
    }

    /// <summary>เลขอ้างอิงนี้คือเงินของสลิปที่รอตรวจไหม (บันทึกเป็น "โอนเงิน (ตรวจสลิป)" ไม่ใช่ "ออนไลน์")</summary>
    public static bool IsSlipReference(Guid? slipIntentId, string? reference)
        => slipIntentId is Guid id && string.Equals(reference?.Trim(), GatewayPaymentRecord.Reference(id), StringComparison.Ordinal);

    /// <summary>สถานะดิบ (ProviderStatusRaw) ตอนปิดรายการชำระของสลิปที่ไม่ใช้แล้ว</summary>
    public const string SupersededRaw = "slip_superseded";
    public const string AmountChangedMessage = "ยอดที่ต้องชำระของการจองเปลี่ยนระหว่างแนบสลิป — รีเฟรชหน้าแล้วแนบใหม่ (ไฟล์ที่ส่งเก็บไว้ให้ร้านตรวจได้)";
    public const string AttachFailedMessage = "บันทึกสลิปไม่สำเร็จ — ไฟล์ของคุณถูกเก็บไว้ให้ร้านตรวจแล้ว · รีเฟรชหน้าแล้วลองใหม่ หรือแจ้งร้านพร้อมเลขที่การจอง";
    public const string AlreadyPaidMessage = "การจองนี้มีการชำระที่ยืนยันแล้ว — ติดต่อร้านเพื่อชำระส่วนที่เหลือ (ระบบไม่รับสลิปซ้อนรายการที่ยืนยันแล้ว)";
    public const string BookingClosedRaw = "slip_booking_closed";
    public const string AttachFailedRaw = "slip_attach_failed";

    /// <summary>เงินที่เพิ่งบันทึกปิดสถานะ "สลิปรอตรวจ" ไหม — เลขอ้างอิงคือรายการชำระของสลิปนี้ หรือรับครบมัดจำแล้ว</summary>
    public static bool SettlesPendingSlip(Guid? slipIntentId, string? recordedReference, decimal depositAmount, decimal paidAmount)
        => IsSlipReference(slipIntentId, recordedReference) || CmsBookingOnlinePayment.DepositSettled(depositAmount, paidAmount);

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
