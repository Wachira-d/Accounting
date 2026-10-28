using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินเดียว "การจองของเครื่องจอง CMS ที่จ่ายด้วยบัตรคอร์ส" ↔ สมุดเครดิต <c>ICreditBookingLedger</c> (รอบ 205 W6 · R-39 / COURSE_CREDIT_DESIGN B4) — pure ·
/// เครื่องจองเป็นเจ้าของช่องเวลา/ความจุ (<c>BookingSlotEngine</c> — ไม่แตะ) · โมดูลคอร์สเป็นเจ้าของเครดิต + รายได้ · ตรงนี้คือ "สายต่อ" เท่านั้น
///
/// <para><b>กติกา:</b> จองที่เลือกใช้บัตร ⇒ <c>Hold</c> ในธุรกรรมเดียวกับ <c>Decide</c> (ล้ม = ไม่มีการจอง) · ยอดเงินของการจอง = 0 (ไม่มีเงินต้องเก็บ — รายได้มาจาก JE รับรู้ของบัตร)
/// · <b>ไม่ออกเอกสาร ERP</b> (ใบยืนยัน/ใบกำกับ/มัดจำ) และไม่เดิน tax-point ของบริการ (ภาษีขายเกิดครบตอนขายคอร์ส §78/1) · ให้บริการเสร็จ ⇒ <c>Settle(attended)</c> · ไม่มา ⇒ <c>Settle(noShow)</c> ·
/// ยกเลิก ⇒ <c>Release(byBusiness)</c> (ร้านยกเลิกจากหลังบ้าน = คืนเสมอ) · การจองที่ไม่ใช้บัตร ⇒ ไม่แตะสมุดเลย</para>
/// </summary>
public static class CreditBookingLink
{
    public const string RuleCode = "TFRS-NPAE-6-CREDIT-BOOKING";
    public const string RefPrefix = "SiteBooking:";

    public enum LedgerAction { None = 0, SettleAttended = 1, SettleNoShow = 2, Release = 3 }

    /// <summary>เลขอ้างอิงในสมุดเครดิต (CreditClassBooking.ExternalRef) ของการจอง CMS — รูปแบบเดียวทั้งระบบ</summary>
    public static string Ref(Guid siteBookingId) => RefPrefix + siteBookingId.ToString("D");

    /// <summary>การจองที่จ่ายด้วยบัตรไม่ออกเอกสาร ERP (ไม่ซ้ำกับรายได้ที่รับรู้จากบัตร) — การจองอื่นเดินกติกาเดิมของเครื่องจองทุกข้อ</summary>
    public static bool ShouldSyncErp(bool paidWithCredit) => !paidWithCredit;

    /// <summary>เปลี่ยนสถานะการจอง ⇒ ต้องทำอะไรกับสมุดเครดิต (เฉพาะการจองที่จ่ายด้วยบัตร · ผู้เรียกตรวจ <paramref name="paidWithCredit"/> ก่อน)</summary>
    public static LedgerAction ActionFor(BookingStatus oldStatus, BookingStatus newStatus, bool paidWithCredit)
    {
        if (!paidWithCredit || oldStatus == newStatus) return LedgerAction.None;
        return newStatus switch
        {
            BookingStatus.Completed => LedgerAction.SettleAttended,
            BookingStatus.NoShow => LedgerAction.SettleNoShow,
            BookingStatus.Cancelled => LedgerAction.Release,
            _ => LedgerAction.None,
        };
    }

    /// <summary>เวลาเริ่มของการจอง (วัน + เวลาไทย) เป็น UTC — ให้สมุดเครดิตตรวจอายุบัตร/นโยบายยกเลิก</summary>
    public static DateTime StartUtc(DateTime bookingDate, TimeOnly startTime)
        => DateTime.SpecifyKind(bookingDate.Date.Add(startTime.ToTimeSpan()).AddHours(-7), DateTimeKind.Utc);

    /// <summary>หมายเหตุบนการจอง (InternalNotes) เมื่อจ่ายด้วยบัตร — บอกบัตร · เครดิตที่กัน · และราคาบริการเดิมที่ไม่เก็บ</summary>
    public static string PaidNote(string passNumber, decimal credits, decimal servicePrice)
        => $"[CREDIT-PASS] ชำระด้วยบัตรคอร์ส {passNumber} — กันเครดิต {credits:0.##} · ไม่ออกเอกสาร ERP/ไม่เก็บเงิน (ราคาบริการตามรายการ {servicePrice:N2} บาท · รายได้รับรู้จากบัตรเมื่อให้บริการเสร็จ)";

    /// <summary>ล้มดังเมื่อสมุดเครดิตปรับไม่สำเร็จหลังเปลี่ยนสถานะ (สถานะการจองบันทึกแล้ว) — ประทับบนการจอง + ตอบผู้กด</summary>
    public static string LedgerFailureNote(LedgerAction action, string reason)
        => $"[CREDIT-PASS-FAILED] {ActionTh(action)}ในสมุดเครดิตไม่สำเร็จ: {reason} — เครดิตของบัตรยังค้างสถานะเดิม · ไปที่หน้า \"คอร์ส & เครดิต\" › บัตรของลูกค้า แล้วปิด/ยกเลิกการจองนั้นเอง";

    public static string ActionTh(LedgerAction a) => a switch
    {
        LedgerAction.SettleAttended => "ตัดเครดิต (มาเรียน)",
        LedgerAction.SettleNoShow => "ตัดเครดิต (ไม่มา)",
        LedgerAction.Release => "คืนเครดิต (ยกเลิก)",
        _ => "ไม่มีการเปลี่ยนแปลง",
    };
}
