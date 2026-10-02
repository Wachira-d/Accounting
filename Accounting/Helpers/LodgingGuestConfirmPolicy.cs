using System.Globalization;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ตัวเลือกโหมดยืนยันการจองบนหน้าตั้งค่า (ป้าย + ผลต่อแขก) — หน้าเว็บสร้าง dropdown จากลิสต์นี้ ห้ามมีสำเนาข้อความใน JS</summary>
public sealed record LodgingGuestConfirmModeOption(LodgingGuestConfirmMode Value, string Label, string Description);

/// <summary>สถานะเริ่มต้นของการจองใหม่ + เวลาถือห้อง (นาที · null = ไม่มี hold เพราะยืนยันแล้ว)</summary>
public readonly record struct LodgingInitialState(LodgingReservationStatus Status, int? HoldMinutes);

/// <summary>ผลของการที่แขกส่งสลิป — รอคนตรวจ (กติกาเดิม) หรือยืนยันการจองทันที (RequireSlip + AutoConfirmOnSlip)</summary>
public enum LodgingSlipOutcome
{
    AwaitReview = 0,
    ConfirmNow = 1,
}

/// <summary>เงื่อนไขการจองที่แขกเห็นก่อนกดจอง (ขั้นสรุปราคา) — เซิร์ฟเวอร์คิด หน้าแสดงอย่างเดียว</summary>
public readonly record struct LodgingQuoteTerms(bool SlipRequired, string AmountLabel, string? Note);

/// <summary>ข้อเท็จจริงของการจองหนึ่งใบที่ใช้ตัดสินป้าย/ข้อความฝั่งแขกและฝั่งหน้าบ้าน</summary>
/// <param name="Mode">โหมดที่ตรึงบนใบ (null = ใบก่อนรอบ 202 คำตัดสินข้อ 128)</param>
/// <param name="SlipAwaitingReview">แขกส่งสลิปแล้วและยังไม่ถูกปฏิเสธ (SlipUploadedAt != null)</param>
/// <param name="PaymentProblem">เงินเข้า/ส่งสลิปแล้วแต่ยืนยันไม่ได้ (PaymentProblemAt != null)</param>
public readonly record struct LodgingGuestFacts(
    LodgingGuestConfirmMode? Mode, LodgingReservationStatus Status,
    decimal DepositRequired, decimal DepositPaid, decimal TotalAmount, decimal FolioTotal, decimal PaidAmount,
    DateTime? HoldExpiresAt, bool SlipAwaitingReview, bool SlipUploadBlocked,
    string? ConfirmedBy, string? CancellationReason, bool PaymentProblem);

/// <summary>สิ่งที่หน้าแขกแสดง — ป้ายสถานะ · ข้อความ · ต้องส่งสลิปไหม/ภายในเมื่อไร/ยอดเท่าไร · เปิดช่องส่งสลิปไหม</summary>
public sealed record LodgingGuestView(
    string StatusLabel, string? Note, bool SlipRequired, DateTime? SlipDueAt, decimal? AmountToTransfer, bool CanUploadSlip);

/// <summary>
/// **การจองจากเว็บ "สำเร็จ" เมื่อไร — ตัวตัดสินเดียว** (รอบ 202 · คำตัดสินข้อ 128)
///
/// <para><b>ที่มา</b>: ผู้ใช้ — "ปรับให้ตั้งค่าได้ว่าต้องส่งสลิปโอนเงินก่อนถึงจะจองสำเร็จได้ หรือ จะเป็นแบบเดิมนี้ก็ได้" ·
/// เดิมที่พักที่ติ๊ก ConfirmWithoutDeposit (หรือมัดจำ 0) แขกกดจองแล้วขึ้น “จองสำเร็จ · ยืนยันแล้ว” ทันทีทั้งที่ชำระ ฿0
/// แล้วมีกล่องอัปโหลดสลิปตามหลัง · และสูตร <c>prop.ConfirmWithoutDeposit || …</c> กระจายอยู่ในเส้นสร้างจอง + ตัวคิดมัดจำ</para>
///
/// <para><b>สามโหมด</b> (<see cref="LodgingGuestConfirmMode"/>): Instant (เดิม ConfirmWithoutDeposit) · RequireSlip (ใหม่) ·
/// RequireDeposit (เดิม) — ทุกเส้นอ่านโหมดผ่าน <see cref="Resolve(LodgingProperty)"/> แล้วตัดสินที่นี่ ห้ามอ่านธงเดิมตรงอีก</para>
///
/// <para><b>ส่งสลิป ≠ ชำระแล้ว</b> (R1 · DECISION_DOCTRINE): ไม่มีเมธอดไหนในนี้ประทับยอดเงิน · โหมด RequireSlip + AutoConfirmOnSlip
/// เปลี่ยน<b>สถานะการจอง</b>เป็นยืนยันตามค่าตั้งที่เจ้าของเลือก (ผู้ยืนยัน = <see cref="SlipConfirmActor"/>) แต่ DepositPaid/PaidAmount
/// คงเดิมจนกว่าพนักงานยืนยันรับเงินผ่านเส้น ConfirmAsync · ปฏิเสธสลิปของใบที่ยืนยันเพราะสลิป ⇒ กลับเป็นรอชำระ + ถือห้องตามกติกาเดิม</para>
/// </summary>
public static class LodgingGuestConfirmPolicy
{
    public const int DefaultSlipDeadlineMinutes = 30;
    public const int MinSlipDeadlineMinutes = 5;
    public const int MaxSlipDeadlineMinutes = 1440;

    /// <summary>ป้ายช่องบนหน้าตั้งค่า — ข้อความปฏิเสธชี้ป้ายที่ผู้ใช้เห็น</summary>
    public const string SlipDeadlineLabel = "ส่งสลิปภายใน (นาที)";

    /// <summary>ผู้ยืนยัน (ConfirmedBy) เมื่อระบบยืนยันเพราะแขกส่งสลิป — ตัวแยก "ยืนยันเพราะสลิป (ยังไม่รับเงิน)" ออกจากการยืนยันของพนักงาน</summary>
    public const string SlipConfirmActor = "guest-slip";

    /// <summary>ปฏิเสธสลิปแล้วให้เวลาแขกส่งใหม่ (กติกาเดิมของ RejectSlipAsync)</summary>
    public static readonly TimeSpan ResubmitWindow = TimeSpan.FromHours(24);

    /// <summary>เหตุการณ์แจ้งเตือนตอนสร้างการจอง — แจ้งเจ้าของ "จองใหม่" (อีเมล + LINE) + อีเมลแขก</summary>
    public const string EventCreated = "created";
    /// <summary>การจองที่ยังไม่สำเร็จเพราะรอสลิป — อีเมลแขกอย่างเดียว (เจ้าของได้แจ้งตอนแขกส่งสลิป ไม่ใช่ตอนกดจอง)</summary>
    public const string EventAwaitingSlip = "awaiting-slip";

    // ═══════════════════════════ ค่าตั้ง ═══════════════════════════

    /// <summary>โหมดที่มีผลของที่พัก — ค่าที่บันทึกไว้ชนะ · แถวเดิมที่ยังไม่มีโหมดอ่านจากธงเดิม (true ⇒ Instant · อื่น ⇒ RequireDeposit) ·
    /// ค่าที่ไม่อยู่ใน enum (ฐานถูกแก้มือ) ⇒ ตกไปอ่านธงเดิมเหมือนไม่มีค่า</summary>
    public static LodgingGuestConfirmMode Resolve(LodgingGuestConfirmMode? stored, bool legacyConfirmWithoutDeposit)
        => stored is LodgingGuestConfirmMode m && Enum.IsDefined(m) ? m
         : legacyConfirmWithoutDeposit ? LodgingGuestConfirmMode.Instant : LodgingGuestConfirmMode.RequireDeposit;

    public static LodgingGuestConfirmMode Resolve(LodgingProperty p) => Resolve(p.GuestConfirmMode, p.ConfirmWithoutDeposit);

    /// <summary>สำเนาธงเดิมที่ระบบเขียนตามโหมด (ทางเดียว — หน้าเว็บไม่มีช่องนี้แล้ว · client รุ่นเก่าที่ยังอ่านธงนี้เห็นค่าที่ตรงกับโหมด)</summary>
    public static bool LegacyConfirmWithoutDeposit(LodgingGuestConfirmMode mode) => mode == LodgingGuestConfirmMode.Instant;

    /// <summary>โหมดที่จะบันทึกจากคำขอ — ส่งโหมดมา = ใช้โหมดนั้น (ค่านอก enum ⇒ ปฏิเสธ) · client รุ่นเก่าที่ส่งแค่ธงเดิม ⇒
    /// เปลี่ยนโหมด<b>เฉพาะเมื่อธงต่างจากสำเนาของโหมดปัจจุบัน</b> (หน้ารุ่นเก่าบันทึกซ้ำต้องไม่ทำให้ที่พัก RequireSlip ตกเป็น RequireDeposit เงียบ ๆ)</summary>
    public static LodgingGuestConfirmMode ModeOnSave(LodgingGuestConfirmMode? requested, bool requestedLegacyFlag, LodgingGuestConfirmMode current)
    {
        if (requested is LodgingGuestConfirmMode r)
        {
            if (!Enum.IsDefined(r))
                throw new BusinessRuleException("โหมดยืนยันการจองไม่ถูกต้อง — เลือกจากรายการในหน้าตั้งค่า", "LODGING-CONFIRM-MODE");
            return r;
        }
        if (requestedLegacyFlag == LegacyConfirmWithoutDeposit(current)) return current;
        return requestedLegacyFlag ? LodgingGuestConfirmMode.Instant : LodgingGuestConfirmMode.RequireDeposit;
    }

    /// <summary>ช่อง "ส่งสลิปภายใน (นาที)" นอกช่วง 5–1440 ⇒ ข้อความปฏิเสธ (ไม่แก้ค่าให้เงียบ ๆ) · null = ใช้ได้</summary>
    public static string? SlipDeadlineProblem(int minutes)
        => minutes < MinSlipDeadlineMinutes || minutes > MaxSlipDeadlineMinutes
            ? $"{SlipDeadlineLabel} ต้องอยู่ระหว่าง {MinSlipDeadlineMinutes}–{MaxSlipDeadlineMinutes} นาที (ได้ {minutes})"
            : null;

    /// <summary>ตัวเลือกบนหน้าตั้งค่า (ลำดับ = ลำดับใน dropdown)</summary>
    public static IReadOnlyList<LodgingGuestConfirmModeOption> Options { get; } = new[]
    {
        new LodgingGuestConfirmModeOption(LodgingGuestConfirmMode.Instant,
            "จองสำเร็จทันที (ไม่ต้องชำระก่อน)",
            "แขกกดจองแล้วขึ้น “จองสำเร็จ · ยืนยันแล้ว” ทันที ไม่ต้องโอนก่อน (ไม่เก็บมัดจำ) — แขกส่งสลิป/ชำระออนไลน์ภายหลังได้ ที่พักบันทึกรับเงินเอง"),
        new LodgingGuestConfirmModeOption(LodgingGuestConfirmMode.RequireSlip,
            "ต้องส่งสลิปโอนเงินก่อน การจองจึงสำเร็จ",
            "แขกกดจองแล้วขึ้น “ยังไม่สำเร็จ — กรุณาโอนและส่งสลิปภายในเวลาที่กำหนด” · ไม่ส่งในเวลา ระบบยกเลิกและปล่อยห้อง · "
            + "ยอดที่ต้องโอน = มัดจำตามค่าตั้ง (มัดจำ 0 = ยอดเต็ม) · ส่งสลิปแล้วยืนยันทันที หรือรอที่พักตรวจ เลือกได้ · "
            + "ส่งสลิปไม่ใช่การรับเงิน — ยอดเงินบันทึกเมื่อพนักงานกดบันทึกรับเงินตามสลิป"),
        new LodgingGuestConfirmModeOption(LodgingGuestConfirmMode.RequireDeposit,
            "รอชำระมัดจำ แล้วที่พักยืนยัน (แบบเดิม)",
            "แขกกดจองแล้วได้สถานะ “รอชำระมัดจำ” · ถือห้องตาม “กันห้องรอชำระมัดจำ (นาที)” · ที่พักกดยืนยันเมื่อได้รับมัดจำ "
            + "(หรือระบบยืนยันเองเมื่อแขกจ่ายออนไลน์ ถ้าเปิด “ยืนยันอัตโนมัติเมื่อได้รับมัดจำ”) · มัดจำตามค่าตั้ง 0 = ยืนยันทันที"),
    };

    // ═══════════════════════════ สร้างการจอง ═══════════════════════════

    /// <summary>โหมดที่ใช้กับการจองจากช่องทางนี้ — เส้นพนักงาน (walk-in/โทร/OTA) ไม่ถูกบังคับส่งสลิป ⇒ RequireSlip เดินแบบ RequireDeposit ·
    /// Instant ใช้กับทุกช่องทางเหมือนธงเดิม</summary>
    public static LodgingGuestConfirmMode ForChannel(LodgingGuestConfirmMode propertyMode, bool isStaff)
        => isStaff && propertyMode == LodgingGuestConfirmMode.RequireSlip ? LodgingGuestConfirmMode.RequireDeposit : propertyMode;

    /// <summary>มัดจำที่ "ต้องชำระ" ของการจอง — Instant ⇒ 0 (ธงเดิม) · RequireSlip ⇒ มัดจำตามค่าตั้ง หรือ<b>ยอดรวมทั้งหมด</b>เมื่อมัดจำคิดได้ 0
    /// (ต้องโอนอะไรสักอย่างถึงจะมีสลิป — ไม่ใช่ ฿0) · RequireDeposit ⇒ ตามค่าตั้ง</summary>
    public static decimal QuotedDeposit(LodgingGuestConfirmMode channelMode, decimal computedDeposit, decimal totalAmount) => channelMode switch
    {
        LodgingGuestConfirmMode.Instant => 0m,
        LodgingGuestConfirmMode.RequireSlip => computedDeposit > 0m ? computedDeposit : Math.Max(0m, totalAmount),
        _ => computedDeposit,
    };

    /// <summary>สถานะเริ่มต้น + เวลาถือห้อง — แทนสูตรเดิม <c>prop.ConfirmWithoutDeposit || deposit &lt;= 0 || (isStaff &amp;&amp; ConfirmImmediately)</c> ·
    /// RequireSlip ถือห้อง <paramref name="slipDeadlineMinutes"/> (หมดแล้ว ExpireHoldsAsync ยกเลิกตามกติกาเดิม) · RequireDeposit ถือ <paramref name="paymentHoldMinutes"/></summary>
    public static LodgingInitialState Initial(LodgingGuestConfirmMode channelMode, decimal depositRequired, bool staffConfirmImmediately,
        int paymentHoldMinutes, int slipDeadlineMinutes)
    {
        if (staffConfirmImmediately || channelMode == LodgingGuestConfirmMode.Instant || depositRequired <= 0m)
            return new LodgingInitialState(LodgingReservationStatus.Confirmed, null);
        return channelMode == LodgingGuestConfirmMode.RequireSlip
            ? new LodgingInitialState(LodgingReservationStatus.Pending, Math.Clamp(slipDeadlineMinutes, MinSlipDeadlineMinutes, MaxSlipDeadlineMinutes))
            : new LodgingInitialState(LodgingReservationStatus.Pending, paymentHoldMinutes);
    }

    /// <summary>เหตุการณ์แจ้งเตือนตอนสร้าง — ใบที่ยังรอสลิปไม่ใช่ "จองใหม่สำเร็จ" ⇒ ไม่แจ้งเจ้าของ (แจ้งตอนแขกส่งสลิป) · แขกยังได้อีเมลพร้อมลิงก์ส่งสลิป</summary>
    public static string CreatedEvent(LodgingGuestConfirmMode? reservationMode, LodgingReservationStatus status)
        => reservationMode == LodgingGuestConfirmMode.RequireSlip && status == LodgingReservationStatus.Pending ? EventAwaitingSlip : EventCreated;

    /// <summary>เงื่อนไขที่แขกเห็นก่อนกดจอง</summary>
    public static LodgingQuoteTerms QuoteTerms(LodgingGuestConfirmMode channelMode, decimal depositRequired, bool autoConfirmOnSlip, int slipDeadlineMinutes)
    {
        if (channelMode == LodgingGuestConfirmMode.RequireSlip && depositRequired > 0m)
            return new LodgingQuoteTerms(true, "ยอดที่ต้องโอน",
                $"กดจองแล้วกรุณาโอน ฿{depositRequired.ToString("N2", CultureInfo.InvariantCulture)} และส่งสลิปภายใน {DurationText(slipDeadlineMinutes)} "
                + "การจองจึงจะสำเร็จ — ไม่ส่งในเวลา ระบบยกเลิกการจองอัตโนมัติ"
                + (autoConfirmOnSlip ? " · ส่งสลิปแล้วยืนยันการจองทันที" : " · ส่งสลิปแล้วที่พักตรวจสอบและยืนยันให้"));
        return new LodgingQuoteTerms(false, "มัดจำที่ต้องชำระ", null);
    }

    // ═══════════════════════════ แขกส่งสลิป / ที่พักปฏิเสธสลิป ═══════════════════════════

    /// <summary>ผลของสลิปที่แขกส่ง (ตัดสินใต้ล็อกที่พักหลังอ่านแถวใหม่) — ยืนยันทันทีเฉพาะ: ใบตรึงโหมด RequireSlip · ที่พักเปิด AutoConfirmOnSlip ·
    /// ใบยังรอชำระ · <b>ไม่มีปัญหา</b> (hold หมดแล้วห้องเต็ม / ใบถูกระบบยกเลิก ⇒ ธงคำตัดสินข้อ 127 ไม่ใช่ยืนยัน) · อื่น ๆ รอคนตรวจตามกติกาเดิม</summary>
    public static LodgingSlipOutcome OnSlipUploaded(LodgingGuestConfirmMode? reservationMode, bool autoConfirmOnSlip,
        LodgingReservationStatus statusUnderLock, bool problemFound)
        => reservationMode == LodgingGuestConfirmMode.RequireSlip && autoConfirmOnSlip
           && statusUnderLock == LodgingReservationStatus.Pending && !problemFound
            ? LodgingSlipOutcome.ConfirmNow : LodgingSlipOutcome.AwaitReview;

    /// <summary>ใบนี้ "ยืนยันเพราะแขกส่งสลิป และยังไม่มีการบันทึกรับเงิน" — ที่พักต้องตรวจยอดโอน · ปฏิเสธสลิปแล้วกลับเป็นรอชำระ</summary>
    public static bool IsSlipConfirmed(LodgingReservationStatus status, string? confirmedBy, decimal depositPaid)
        => status == LodgingReservationStatus.Confirmed && depositPaid <= 0m
           && string.Equals(confirmedBy, SlipConfirmActor, StringComparison.Ordinal);

    /// <summary>เวลาถือห้องหลังปฏิเสธสลิป — กติกาเดิม: รอชำระ ⇒ ต่อ 24 ชม. ให้ส่งใหม่ · ปิดรับสลิป ⇒ ไม่ต่อ · ใบยืนยันปกติ ⇒ ไม่แตะ ·
    /// <b>ใบที่ยืนยันเพราะสลิปแล้วถูกปฏิเสธ</b> (กลับเป็นรอชำระ ต้องมี hold ไม่งั้นกันห้องตลอดกาล): ส่งใหม่ได้ ⇒ 24 ชม. ·
    /// ปิดรับสลิป ⇒ เท่ากำหนดส่งสลิปของที่พัก (ให้แขกจ่ายออนไลน์/ติดต่อที่พัก แล้วปล่อยห้องตามกติกา)</summary>
    public static DateTime? HoldAfterSlipRejected(LodgingReservationStatus statusBefore, bool revertedFromSlipConfirm, DateTime? currentHold,
        bool blockFurtherUploads, DateTime now, int slipDeadlineMinutes)
    {
        if (revertedFromSlipConfirm)
            return blockFurtherUploads
                ? now.AddMinutes(Math.Clamp(slipDeadlineMinutes, MinSlipDeadlineMinutes, MaxSlipDeadlineMinutes))
                : now + ResubmitWindow;
        if (statusBefore != LodgingReservationStatus.Pending || blockFurtherUploads) return currentHold;
        return now + ResubmitWindow;
    }

    /// <summary>ข้อความในอีเมล/แจ้งเตือนเจ้าของเมื่อแขกส่งสลิป</summary>
    public static string OwnerSlipHeadline(bool confirmedBySlip)
        => confirmedBySlip
            ? "แขกส่งสลิปแล้ว — ระบบยืนยันการจองให้อัตโนมัติตามค่าตั้ง (ยังไม่ได้บันทึกรับเงิน) · กรุณาตรวจยอดโอน แล้วกด “บันทึกรับเงินตามสลิป” หรือปฏิเสธสลิป"
            : "แขกอัปโหลดสลิปมัดจำแล้ว รอตรวจสอบ";

    /// <summary>ข้อความตอบกลับแขกหลังส่งสลิป (แบนเนอร์บนหน้าการจอง)</summary>
    public static string SlipUploadedMessage(LodgingReservationStatus status, string? confirmedBy, decimal depositPaid, bool paymentProblem,
        LodgingGuestConfirmMode? mode)
    {
        if (paymentProblem) return "ได้รับสลิปแล้ว — ที่พักจะตรวจสอบและติดต่อกลับ";
        if (IsSlipConfirmed(status, confirmedBy, depositPaid)) return "ส่งสลิปแล้ว — จองสำเร็จ ยืนยันการจองแล้ว (ที่พักจะตรวจยอดโอนอีกครั้ง)";
        if (status == LodgingReservationStatus.Pending && mode == LodgingGuestConfirmMode.RequireSlip)
            return "ส่งคำขอจองสำเร็จ — รอที่พักตรวจสลิปและยืนยันการจอง";
        return status == LodgingReservationStatus.Pending
            ? "ส่งสลิปแล้ว — ที่พักจะตรวจสอบและยืนยันการจอง"
            : "ส่งสลิปแล้ว — ที่พักจะตรวจสอบยอดชำระ";
    }

    /// <summary>ข้อความตอบกลับแขกหลังกดจอง</summary>
    public static string CreatedMessage(bool slipRequired, string? guestNote, LodgingReservationStatus status, string reservationNumber, decimal depositRequired)
    {
        if (status == LodgingReservationStatus.Confirmed) return $"จองสำเร็จ เลขที่ {reservationNumber}";
        if (slipRequired && guestNote != null) return $"การจอง {reservationNumber} — {guestNote}";
        return $"รับคำขอจอง {reservationNumber} แล้ว — กรุณาชำระมัดจำ {depositRequired.ToString("N2", CultureInfo.InvariantCulture)} บาท และอัปโหลดสลิป";
    }

    // ═══════════════════════════ ป้าย / ข้อความ ═══════════════════════════

    /// <summary>ป้ายฝั่งหน้าบ้าน (พนักงาน) — แยก "รอสลิป (หมดเวลา …)" · "สลิปรอตรวจ" · "ยืนยันจากสลิป ยังไม่บันทึกรับเงิน" · null = ไม่มีเรื่องสลิป</summary>
    public static string? StaffSlipLabel(LodgingGuestFacts f)
    {
        if (f.SlipAwaitingReview && IsSlipConfirmed(f.Status, f.ConfirmedBy, f.DepositPaid))
            return "ยืนยันจากสลิป · ยังไม่บันทึกรับเงิน";
        if (f.Status == LodgingReservationStatus.Pending && f.SlipAwaitingReview && f.DepositPaid <= 0m)
            return "สลิปรอตรวจ";
        if (f.Status == LodgingReservationStatus.Pending && f.Mode == LodgingGuestConfirmMode.RequireSlip
            && !f.SlipAwaitingReview && !f.PaymentProblem && f.DepositPaid <= 0m && f.HoldExpiresAt is DateTime h)
            return $"รอสลิป (หมดเวลา {ThaiTime(h)})";
        return null;
    }

    /// <summary>ใบที่ยืนยันเพราะสลิปและสลิปยังอยู่ — ปุ่ม “บันทึกรับเงินตามสลิป” ของหน้าบ้าน</summary>
    public static bool AwaitingSlipMoneyCheck(LodgingGuestFacts f)
        => f.SlipAwaitingReview && IsSlipConfirmed(f.Status, f.ConfirmedBy, f.DepositPaid);

    /// <summary>สิ่งที่หน้าแขกแสดง — ใบที่ไม่ใช่ RequireSlip ได้ป้ายเดิม (<see cref="LodgingAmounts.StatusLabel"/>) ไม่มีข้อความเพิ่ม ·
    /// ช่องส่งสลิปเปิดเมื่อ: รอชำระ · ยืนยันแล้วยังมียอดค้าง · ใบที่ระบบยกเลิกเพราะหมดเวลา (เซิร์ฟเวอร์รับสลิปไว้ + ธงให้พนักงานตัดสิน · ข้อ 127) —
    /// และที่พักไม่ได้ปิดรับสลิปของใบนี้</summary>
    public static LodgingGuestView GuestView(LodgingGuestFacts f, DateTime now)
    {
        var baseLabel = LodgingAmounts.StatusLabel(f.Status, f.DepositRequired, f.DepositPaid);
        var balance = LodgingAmounts.BalanceDue(f.TotalAmount, f.FolioTotal, f.PaidAmount);
        var autoExpired = LodgingHoldRule.IsAutoExpiredHold(f.Status, f.CancellationReason, f.DepositPaid);
        var canUpload = !f.SlipUploadBlocked
            && (f.Status == LodgingReservationStatus.Pending
                || (f.Status == LodgingReservationStatus.Confirmed && balance > 0.005m)
                || autoExpired);
        if (f.Mode != LodgingGuestConfirmMode.RequireSlip)
            return new LodgingGuestView(baseLabel, null, false, null, null, canUpload);

        switch (f.Status)
        {
            case LodgingReservationStatus.Pending when f.DepositPaid > 0m:
                return new LodgingGuestView(baseLabel, null, false, null, null, canUpload);
            case LodgingReservationStatus.Pending when f.SlipAwaitingReview || f.PaymentProblem:
                return new LodgingGuestView("ส่งคำขอจองสำเร็จ — รอที่พักตรวจสลิป",
                    "ที่พักได้รับสลิปแล้ว จะตรวจสอบและยืนยันการจองให้ — ระหว่างนี้ห้องถูกกันไว้ให้ท่านแล้ว", false, null, null, canUpload);
            case LodgingReservationStatus.Pending when f.HoldExpiresAt is DateTime due && due <= now:
                return new LodgingGuestView("หมดเวลาส่งสลิป",
                    "หมดเวลาส่งสลิปแล้ว — ระบบกำลังยกเลิกการจองและปล่อยห้อง · หากโอนเงินไปแล้ว ส่งสลิปได้ ที่พักจะตรวจสอบและติดต่อกลับ",
                    false, null, null, canUpload);
            case LodgingReservationStatus.Pending:
            {
                var amount = LodgingAmounts.OnlinePayableAmount(f.Status, f.DepositRequired, f.DepositPaid, f.TotalAmount, f.FolioTotal, f.PaidAmount);
                var dueText = f.HoldExpiresAt is DateTime d ? $" ภายใน {ThaiTime(d)} น." : "";
                var amountText = amount is decimal a ? $"โอน ฿{a.ToString("N2", CultureInfo.InvariantCulture)} และ" : "";
                return new LodgingGuestView("ยังไม่สำเร็จ — รอสลิปโอนเงิน",
                    $"การจองยังไม่สำเร็จ — กรุณา{amountText}ส่งสลิป{dueText} หากไม่ส่งภายในเวลา ระบบจะยกเลิกการจองและปล่อยห้องให้ผู้อื่น",
                    true, f.HoldExpiresAt, amount, canUpload);
            }
            case LodgingReservationStatus.Confirmed when IsSlipConfirmed(f.Status, f.ConfirmedBy, f.DepositPaid):
                return new LodgingGuestView("จองสำเร็จ · ยืนยันแล้ว",
                    "ได้รับสลิปแล้ว — ที่พักจะตรวจยอดโอนอีกครั้ง หากสลิปไม่ถูกต้องที่พักจะแจ้งกลับ", false, null, null, canUpload);
            case LodgingReservationStatus.Cancelled when autoExpired:
                return new LodgingGuestView("หมดเวลาส่งสลิป — การจองถูกยกเลิก",
                    "หากโอนเงินไปแล้ว ส่งสลิปด้านล่างได้ — ที่พักจะตรวจสอบและติดต่อกลับ (ระบบไม่คืนห้องให้อัตโนมัติ)", false, null, null, canUpload);
            default:
                return new LodgingGuestView(baseLabel, null, false, null, null, canUpload);
        }
    }

    /// <summary>เวลาไทย (UTC+7) แบบ dd/MM/yyyy HH:mm — ปฏิทินสากลเสมอ (ไม่ขึ้นกับ culture ของเครื่อง)</summary>
    public static string ThaiTime(DateTime utc) => utc.AddHours(7).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"30 นาที" · "1 ชม." · "1 ชม. 30 นาที"</summary>
    private static string DurationText(int minutes)
    {
        if (minutes < 60) return $"{minutes} นาที";
        var h = minutes / 60; var m = minutes % 60;
        return m == 0 ? $"{h} ชม." : $"{h} ชม. {m} นาที";
    }
}
