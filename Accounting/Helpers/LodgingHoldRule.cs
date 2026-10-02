using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของการจองหนึ่งใบที่ใช้ตัดสินว่า "ยังกันห้องอยู่ไหม" — ตัวเดียวของตัวนับห้องว่าง (LodgingAvailability.Blocks) ·
/// ตัวยกเลิกการจองที่หมดเวลาถือห้อง (ExpireHoldsAsync) · ตัวตรวจห้องชน (AssignCoreAsync)</summary>
/// <param name="SlipAwaitingReview">แขกส่งสลิปแล้ว รอพนักงานตัดสิน (SlipUploadedAt != null)</param>
/// <param name="MoneyArrivedUnconfirmed">เงินออนไลน์เข้าแล้วแต่ยืนยันอัตโนมัติไม่ได้ (PaymentProblemAt != null)</param>
public readonly record struct LodgingHoldFacts(
    LodgingReservationStatus Status, DateTime? HoldExpiresAt, decimal DepositPaid,
    bool SlipAwaitingReview, bool MoneyArrivedUnconfirmed);

/// <summary>
/// **กติกาการกันห้องของการจองที่พัก — ตัวตั้งตัวเดียว** (รอบ 202 ทีม LO · O-P1-4 · O-P1-5 · คำตัดสินข้อ 119)
///
/// <para><b>ที่มา</b>: เดิมมีสองสูตรที่ไม่ตรงกัน — ตัวนับห้องว่างถือว่า Pending "เลิกกันห้อง" ทันทีที่ <c>HoldExpiresAt</c> ผ่าน
/// แต่ตัวยกเลิกอัตโนมัติ<b>ไม่ยกเลิก</b>ใบที่ส่งสลิปแล้ว/รับมัดจำแล้ว ⇒ ใบที่แขกส่งสลิปค้าง "รอตรวจ" เกิน 24 ชม. ไม่ถูกยกเลิกแต่ห้องถูกขาย
/// ให้คนอื่นเงียบ ๆ (O-P1-5) · และใบที่แขกกำลังสแกนจ่ายออนไลน์ hold หมดกลางทาง ⇒ เงินเข้าแต่ห้องหายแล้ว (O-P1-4)</para>
///
/// <para><b>กติกา</b>: Confirmed/CheckedIn กันห้องเสมอ · Pending กันห้องจนกว่า "hold หมด <i>และ</i> ไม่มีอะไรรอคนตัดสิน"
/// (ไม่มีมัดจำ · ไม่มีสลิปรอตรวจ · ไม่มีเงินออนไลน์ที่ค้างยืนยัน) — สองเส้นอ่านเงื่อนไขเดียวกันจากที่นี่
/// ⇒ "ไม่ถูกยกเลิก" กับ "ยังกันห้อง" เป็นความจริงเดียวกันเสมอ</para>
/// </summary>
public static class LodgingHoldRule
{
    /// <summary>กันห้องให้ระหว่างจ่ายออนไลน์เมื่อผู้ให้บริการไม่บอกวันหมดอายุของรายการ (บัตร/ลิงก์ authorize)</summary>
    public static readonly TimeSpan DefaultPaymentWindow = TimeSpan.FromMinutes(30);

    /// <summary>เผื่อเวลาที่ webhook/การ poll มาถึงหลังแขกจ่ายจริง — จ่ายวินาทีสุดท้ายของ QR แล้วยืนยันช้าไปหนึ่งนาทีต้องไม่เสียห้อง</summary>
    public static readonly TimeSpan ConfirmationGrace = TimeSpan.FromMinutes(15);

    /// <summary>ระยะ "จอง" ห้องไว้ระหว่างที่การยืนยันกำลังออกใบมัดจำ (หลังตรวจห้องว่างใต้ล็อกแล้ว) — ยืนยันสำเร็จจะล้าง hold เอง</summary>
    public static readonly TimeSpan ConfirmClaimWindow = TimeSpan.FromMinutes(15);

    /// <summary>การจองที่ยังรอชำระ "หมดเวลาถือห้องแล้วและไม่มีอะไรรอคนตัดสิน" — ยกเลิกอัตโนมัติได้ และเลิกกันห้อง</summary>
    public static bool HoldLapsed(LodgingHoldFacts f, DateTime now)
        => f.Status == LodgingReservationStatus.Pending
           && f.HoldExpiresAt is DateTime h && h <= now
           && f.DepositPaid <= 0m
           && !f.SlipAwaitingReview
           && !f.MoneyArrivedUnconfirmed;

    /// <summary>การจองนี้ยัง "กันห้อง" อยู่ไหม</summary>
    public static bool BlocksInventory(LodgingHoldFacts f, DateTime now)
        => f.Status switch
        {
            LodgingReservationStatus.Confirmed or LodgingReservationStatus.CheckedIn => true,
            LodgingReservationStatus.Pending => !HoldLapsed(f, now),
            _ => false,
        };

    /// <summary>ต่อเวลาถือห้องให้ถึงอย่างน้อย <paramref name="until"/> — ห้ามย่น · hold ว่าง (ไม่มีกำหนด) คงว่าง</summary>
    public static DateTime? ExtendHold(DateTime? current, DateTime until)
        => current is DateTime c ? (c >= until ? c : until) : null;

    /// <summary>ถือห้องถึงเมื่อไรระหว่างจ่ายออนไลน์ = วันหมดอายุของรายการชำระ (หรือค่าตั้งต้นเมื่อไม่มี) + เผื่อการยืนยันมาถึงช้า</summary>
    public static DateTime PaymentHoldUntil(DateTime now, DateTime? intentExpiresAt)
        => (intentExpiresAt is DateTime e && e > now ? e : now + DefaultPaymentWindow) + ConfirmationGrace;

    /// <summary>วันเช็คเอาต์ที่ใช้ "นับการกันห้อง" — แขกที่ยังเช็คอินค้างอยู่หลังวันออก (ยังไม่มีใครกดเช็คเอาต์) กันห้องคืนนี้ต่อ
    /// (คำตัดสินข้อ 119: ห้องที่ CheckedIn ค้างยังกันห้อง — ทิศที่ความเสียหายมองเห็นได้: ห้องว่างลดลงพร้อมป้าย "ค้างปิด"
    /// ดีกว่าขายห้องที่แขกยังอยู่ซ้อน) · วันออก = วันนี้ ยังเป็นปกติ (เช็คเอาต์ก่อนเที่ยง ขายคืนนี้ได้)</summary>
    public static DateTime EffectiveCheckOut(LodgingReservationStatus status, DateTime checkOut, DateTime todayThai)
        => status == LodgingReservationStatus.CheckedIn && checkOut.Date < todayThai.Date
            ? todayThai.Date.AddDays(1)
            : checkOut;
}
