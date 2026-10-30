using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "มัดจำการจองนัดหมาย (CMS booking) จ่ายได้เท่าไร/ตอนไหน" — ใช้ทั้ง <c>PublicPaymentResolver</c> (ยอดจาก
/// เซิร์ฟเวอร์เท่านั้น) · ตัวจัดการเงินเข้า · เส้นพนักงานบันทึกรับเงิน · และคำตอบที่หน้าเว็บแสดง (รอบ 205 แพ็ก W2 · R-08)
///
/// <para><b>ที่มา</b> (BOOKING_REVENUE_AUDIT B-4): บริการแบบ "ชำระล่วงหน้า" (PrePayment) ออกใบเสร็จมัดจำเป็น<b>ร่าง</b>รอเงินเข้า แต่ไม่มี
/// ทางให้แขกจ่ายออนไลน์และ<b>ไม่มีใครเขียน <c>SiteBooking.PaidAmount</c></b> ⇒ ใบร่างค้างตลอดไป · คำตัดสินเจ้าของ Q5 = ข: ปิดเป็นค่าเริ่มต้น
/// ไซต์เปิดเองที่ตั้งค่าเครื่องจอง (<c>SiteBookingSettings.OnlineDepositEnabled</c>)</para>
///
/// <para><b>กติกา</b>: จ่ายออนไลน์ได้เฉพาะ PrePayment ที่สถานะ Pending/Confirmed · ยอด = มัดจำ − ที่รับแล้ว (ไม่รับเกิน) · ไซต์ไม่เปิด ⇒ ยอด 0 +
/// ข้อความบอกทางไปต่อ (โอน/แนบสลิป · ไม่ silent) · เลขอ้างอิงของเงินก้อนนี้กันซ้ำ (webhook/poll ยิงซ้ำ = ไม่บันทึกซ้ำ) ·
/// รับเงินครบมัดจำ ⇒ การจองยืนยัน (Pending → Confirmed) และใบเสร็จมัดจำอนุมัติ<b>ลงวันที่รับเงินจริง</b> (§78/1 tax point)</para>
/// </summary>
public static class CmsBookingOnlinePayment
{
    public const string RuleCode = "CMS-BOOKING-ONLINE-DEPOSIT";
    public const string LegalReference = "RD-78/1";
    /// <summary>ตัวคั่นเลขอ้างอิงหลายรายการใน <c>SiteBooking.PaymentReference</c> (รับหลายงวดได้)</summary>
    public const char ReferenceSeparator = ';';

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static string NewPublicToken() => Guid.NewGuid().ToString("N");

    /// <summary>มัดจำที่ยังค้าง (หน่วยสกุลเอกสาร) — 0 เมื่อไม่ใช่ PrePayment / สถานะไม่รับเงิน / รับครบแล้ว</summary>
    public static decimal DepositDue(BookingType type, BookingStatus status, decimal depositAmount, decimal paidAmount)
    {
        if (type != BookingType.PrePayment) return 0m;
        if (status is not (BookingStatus.Pending or BookingStatus.Confirmed)) return 0m;
        if (depositAmount <= 0.005m) return 0m;
        return Math.Max(0m, R2(depositAmount - paidAmount));
    }

    /// <summary>ข้อความเมื่อจ่ายออนไลน์ไม่ได้ (null = จ่ายได้) — หน้าเว็บแสดงตรง ๆ ไม่คิดเอง</summary>
    /// <param name="slipPending">รอบ 218: ลูกค้าแนบสลิปแล้วรอร้านตรวจ ⇒ ปิดช่องจ่ายออนไลน์ชั่วคราว (กันจ่ายซ้ำ · แบบเดียวกับคำสั่งซื้อ R-06)</param>
    public static string? BlockedNote(bool siteOnlineDepositEnabled, BookingType type, BookingStatus status, decimal due, bool slipPending = false)
    {
        if (type != BookingType.PrePayment) return null;   // ไม่ใช่บริการชำระล่วงหน้า — ไม่มีมัดจำให้จ่าย (ไม่ใช่การบล็อก)
        if (status == BookingStatus.Cancelled) return "การจองนี้ถูกยกเลิกแล้ว";
        if (status == BookingStatus.Waitlisted) return "การจองนี้อยู่ในคิวสำรอง — ชำระมัดจำได้เมื่อได้คิว";
        if (status is BookingStatus.Completed or BookingStatus.NoShow) return null;
        if (due <= 0.005m) return null;   // รับครบแล้ว = ไม่มียอดค้าง
        if (slipPending) return SlipPendingNote;
        if (!siteOnlineDepositEnabled)
            return "เว็บนี้ยังไม่เปิดรับชำระมัดจำออนไลน์ — โอนแล้วแจ้งร้าน ร้านจะบันทึกรับเงินและยืนยันการจองให้";
        return null;
    }

    /// <summary>ตรวจยอดที่พนักงาน/ระบบจะบันทึก — คืนข้อความไทย (null = ผ่าน) · ไม่รับเกิน (ค่าบริการรวม − ที่รับแล้ว)</summary>
    public static string? ValidateAmount(decimal amount, decimal totalAmount, decimal paidAmount)
    {
        if (amount <= 0m) return "ยอดรับเงินต้องมากกว่า 0";
        if (decimal.Round(amount, 2) != amount) return "ยอดรับเงินต้องเป็นทศนิยมไม่เกิน 2 ตำแหน่ง";
        var room = R2(totalAmount - paidAmount);
        if (amount > room + 0.005m)
            return $"ยอดรับเงิน {amount:N2} เกินยอดที่ค้างของการจอง ({Math.Max(0m, room):N2}) — ถ้าลูกค้าจ่ายเกิน ให้บันทึกส่วนเกินเป็นรายการคืนเงิน/เครดิตแยก";
        return null;
    }

    /// <summary>วันที่รับเงินต้องไม่เป็นอนาคต (เผื่อโซนเวลา 1 วัน) และไม่เก่ากว่า 1 ปี (กันพิมพ์ปีผิด)</summary>
    public static string? ValidatePaidAt(DateTime? paidAtUtc, DateTime nowUtc)
    {
        if (paidAtUtc is not DateTime p) return null;
        if (p > nowUtc.AddDays(1)) return "วันที่รับเงินเป็นอนาคตไม่ได้";
        if (p < nowUtc.AddYears(-1)) return "วันที่รับเงินเก่ากว่า 1 ปี — ตรวจปี (ค.ศ.) อีกครั้ง";
        return null;
    }

    /// <summary>เลขอ้างอิงนี้ถูกบันทึกบนการจองแล้วไหม (กันซ้ำของเงินก้อนเดียว — ตัวเดียวกับที่ตัวจัดการเงินเข้าใช้)</summary>
    public static bool AlreadyRecorded(string? recordedReferences, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(recordedReferences) || string.IsNullOrWhiteSpace(incoming)) return false;
        var want = incoming.Trim();
        return recordedReferences.Split(ReferenceSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(r => string.Equals(r, want, StringComparison.Ordinal));
    }

    public static string? AppendReference(string? existing, string? incoming)
    {
        var inc = incoming?.Trim();
        if (string.IsNullOrEmpty(inc)) return existing;
        if (AlreadyRecorded(existing, inc)) return existing;
        return string.IsNullOrWhiteSpace(existing) ? inc : $"{existing.Trim()}{ReferenceSeparator} {inc}";
    }

    /// <summary>รับครบมัดจำแล้วไหม — เกณฑ์เดียวกับ <see cref="CmsBookingErpPolicy.AutoApprove"/> (PrePayment)</summary>
    public static bool DepositSettled(decimal depositAmount, decimal paidAmount)
        => depositAmount > 0.005m && paidAmount + 0.005m >= depositAmount;

    public static string PaidNote(decimal amount, string? reference, DateTime paidAtUtc, bool online, decimal paidTotal, decimal depositAmount)
        => $"[{RuleCode}] รับเงิน {amount:N2} บาท {(online ? "ออนไลน์ผ่านระบบรับชำระเงิน" : "(พนักงานบันทึก)")} "
           + $"{ThaiDate.CalendarDateUtc(paidAtUtc):dd/MM/yyyy}{(string.IsNullOrWhiteSpace(reference) ? "" : $" อ้างอิง {reference.Trim()}")} "
           + $"— รวมรับแล้ว {paidTotal:N2}/{depositAmount:N2}" + (DepositSettled(depositAmount, paidTotal) ? " (ครบมัดจำ ⇒ ยืนยันการจอง)" : "");

    /// <summary>ใบเสร็จมัดจำออกแล้ว แต่มีเงินเข้าเพิ่ม (เช่น จ่ายสองงวด/จ่ายซ้ำ) — ระบบไม่ออกใบซ้ำ บอกทางไปต่อ</summary>
    /// <summary>รอบ 218: ข้อความเมื่อมีสลิปรอร้านตรวจ (ปิดช่องจ่ายออนไลน์ชั่วคราว)</summary>
    public const string SlipPendingNote = "แนบสลิปแล้ว — รอร้านตรวจสอบ (ปิดชำระออนไลน์ชั่วคราวเพื่อกันจ่ายซ้ำ · ร้านปฏิเสธสลิป ⇒ ชำระใหม่ได้)";

    public static string AlreadyIssuedNote(string documentNumber, decimal amount)
        => $"ใบเสร็จมัดจำ {documentNumber} ออกแล้ว — เงินเข้าเพิ่ม {amount:N2} บาท บันทึกบนการจองอย่างเดียว · ถ้าเป็นเงินที่ต้องออกใบ ให้ออกใบเสร็จเพิ่มที่หน้าเอกสาร (อ้างเลขการจอง) หรือคืนส่วนที่เกิน";
}
