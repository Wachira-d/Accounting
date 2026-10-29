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

    /// <summary>ผู้กระทำที่เป็นลูกค้าหน้าเว็บ (ยกเลิกเองผ่านลิงก์ — <c>CancelByCustomerAsync</c> ตั้ง "customer:{token}") · รอบ 208 ฝ่ายค้าน W6 ข้อ 5:
    /// ลูกค้ายกเลิก ⇒ ใช้นโยบายยกเลิกของคอร์ส (ไม่ใช่ "ร้านยกเลิก" ที่คืนเครดิตเสมอ)</summary>
    public const string CustomerActorPrefix = "customer:";
    /// <summary>รอบ 209 (ทีม C ข้อ C1): จองด้วยบัตรคอร์สได้ไหม — ต้องเป็นพนักงานที่ล็อกอิน (ยังไม่มีการยืนยันตัวตนลูกค้าหน้าร้าน) · null = ได้</summary>
    public static string? PayWithCreditProblem(string? staffUserId)
        => string.IsNullOrWhiteSpace(staffUserId)
            ? "จองด้วยบัตรคอร์สจากหน้าเว็บยังไม่เปิด (ต้องยืนยันตัวตนสมาชิกก่อน) — ติดต่อร้านเพื่อจองด้วยบัตร หรือจองแบบชำระปกติ"
            : null;

    public static bool IsCustomerActor(string? actor) => actor != null && actor.StartsWith(CustomerActorPrefix, StringComparison.Ordinal);

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

    // ───────── รอบ 208 (ฝ่ายค้าน W6 ข้อ 1): การจองที่จ่ายด้วยบัตร ถูกเปิด/ปิดบิลที่ POS ─────────
    // เดิม MarkSourceBookingCompletedAsync ประทับ "เสร็จสิ้น" ตรง ๆ (ยอด 0 ⇒ ผ่านเงื่อนไข "ไม่มีเงินผูก") ⇒ เครดิตค้าง "จองไว้" ตลอด · 21713 ไม่ถูกรับรู้เป็นรายได้

    public enum PosCloseAction { NotCredit = 0, Settle = 1, AlreadySettled = 2, Refuse = 3 }

    /// <summary>สถานะที่มีผลของการจองในสมุดเครดิต จากทุกแถวที่ ExternalRef ตรงกัน (เลื่อนนัด = ยกเลิกแถวเดิม + จองแถวใหม่) ·
    /// ไม่มีแถว ⇒ null (การจองไม่ได้จ่ายด้วยบัตร) · มีแถวที่ยังไม่ยกเลิก ⇒ แถวนั้น (unique index กันไว้ได้แถวเดียว) · เหลือแต่ยกเลิก ⇒ Cancelled</summary>
    public static CreditBookingStatus? LedgerStatusOf(IReadOnlyCollection<CreditBookingStatus> rows)
    {
        if (rows.Count == 0) return null;
        foreach (var s in rows)
            if (s != CreditBookingStatus.Cancelled) return s;
        return CreditBookingStatus.Cancelled;
    }

    /// <summary>ปิดบิล POS ของการจอง CMS ⇒ ต้องทำอะไรกับสมุดเครดิต · <paramref name="ledgerStatus"/> = <see cref="LedgerStatusOf"/> ·
    /// กันไว้ ⇒ <b>ตัดเครดิต (มาเรียน) ในธุรกรรมเดียวกับการปิดบิล</b> · ตัดไปแล้ว (ปิดจากหน้าการจอง/หน้าคอร์ส) ⇒ ไม่ตัดซ้ำ ·
    /// คืนเครดิตไปแล้ว ⇒ ปฏิเสธ (ให้บริการโดยไม่มีทั้งเงินและเครดิต) · ไม่ใช่การจองด้วยบัตร ⇒ กติกาเดิมทุกข้อ</summary>
    /// <para>รอบ 208 ฝ่ายค้านรอบ 2 ข้อ 1: คืนเครดิตแล้ว แต่บิลมีเงินจริง (ร้านให้จ่ายเงินสดแทนบัตร · แก้ราคาบิล) ⇒ ไม่ใช่ "ไม่มีทั้งเงินและเครดิต" ⇒ ปิดแบบบิลปกติ</para>
    public static PosCloseAction ForPosClose(CreditBookingStatus? ledgerStatus, decimal billNet = 0m) => ledgerStatus switch
    {
        null => PosCloseAction.NotCredit,
        CreditBookingStatus.Booked => PosCloseAction.Settle,
        CreditBookingStatus.Cancelled => billNet > 0m ? PosCloseAction.NotCredit : PosCloseAction.Refuse,
        _ => PosCloseAction.AlreadySettled,   // Attended · NoShow · LateCancelled = ใช้เครดิตแล้ว
    };

    /// <summary>ยกเลิกบิล POS ที่ปิดการจองด้วยบัตรไปแล้ว ⇒ ถอย "เสร็จสิ้น" ได้ไหม — เครดิตถูกตัด + รับรู้รายได้แล้ว ⇒ <b>ไม่ถอย</b>
    /// (ถอยแล้วกด "ยกเลิก/ไม่มา" ที่หน้าการจองได้อีก แต่สมุดไม่มีอะไรให้คืน = หน้าจอเล่าคนละเรื่องกับสมุด) · ไม่ใช่การจองด้วยบัตร/ยังกันไว้ ⇒ ถอยได้ตามเดิม</summary>
    public static bool PosVoidRevertsCompleted(CreditBookingStatus? ledgerStatus)
        => ledgerStatus is null or CreditBookingStatus.Booked;

    /// <summary>ข้อความปฏิเสธเมื่อปิดบิล POS ของการจองที่คืนเครดิตไปแล้ว</summary>
    public static string PosCloseRefusal(string? bookingNumber)
        => $"การจอง {bookingNumber ?? "-"} ชำระด้วยบัตรคอร์ส แต่เครดิตถูกคืนไปแล้ว (การจองในสมุดเครดิตถูกยกเลิก) — ปิดบิลนี้จะเท่ากับให้บริการโดยไม่มีทั้งเงินและเครดิต · "
           + "ทางไปต่อ: ยกเลิกบิลนี้ แล้วจองใหม่ด้วยบัตร หรือขายบริการเป็นบิลปกติ (ไม่เปิดจากคิวการจอง)";

    /// <summary>หมายเหตุบนบิล POS ที่เปิดจากการจองที่จ่ายด้วยบัตร</summary>
    public const string PosOpenNote = "ชำระด้วยบัตรคอร์ส — ปิดบิลแล้วระบบตัดเครดิต (มาเรียน) ให้ในธุรกรรมเดียวกัน";

    // ───────── รอบ 209 P4 (คำตัดสินข้อ 168 ญ): นโยบายยกเลิกของการจองด้วยบัตร = กติกาของแพ็กเกจ (ตรึงบนบัตรตอนขาย) ชนะนโยบายของบริการ ─────────

    public const string PolicySourceCreditPass = "CreditPass";
    public const string PolicySourceService = "Service";

    /// <summary>นโยบายยกเลิกที่ใช้กับการจองหนึ่งใบ (server ประกอบ · หน้าเว็บแสดงตรง) — มีบัตร ⇒ กติกาของบัตร (<paramref name="passFreeCancelHours"/>
    /// คือค่าที่ <c>CourseCreditMath.CancelOutcome</c> ใช้ตอนยกเลิกจริง) · ไม่มีบัตร ⇒ ของบริการ · บริการไม่ตั้งอะไรเลย ⇒ ข้อความ null</summary>
    public static (string Source, string? Text) CancellationPolicy(string? passNumber, int? passFreeCancelHours, int? serviceFreeHours, decimal? serviceFeePercent)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!string.IsNullOrWhiteSpace(passNumber) && passFreeCancelHours is int h)
            return (PolicySourceCreditPass, h > 0
                ? $"จองด้วยบัตรคอร์ส {passNumber} — ใช้กติกาของบัตร: ยกเลิกก่อนเวลาเรียนอย่างน้อย {h} ชั่วโมง คืนเครดิต · ช้ากว่านั้นหรือไม่มา = ตัดเครดิต · ร้านยกเลิกคลาส = คืนเครดิตเสมอ"
                : $"จองด้วยบัตรคอร์ส {passNumber} — ใช้กติกาของบัตร: ยกเลิกก่อนเวลาเรียน คืนเครดิต · ไม่มา = ตัดเครดิต · ร้านยกเลิกคลาส = คืนเครดิตเสมอ");
        var fee = serviceFeePercent is decimal f && f > 0 ? f : (decimal?)null;
        if (serviceFreeHours is int sh && sh > 0)
            return (PolicySourceService, fee is decimal fp
                ? $"ยกเลิกฟรีก่อนเวลานัด {sh} ชั่วโมง (หลังจากนั้นค่าธรรมเนียม {fp.ToString("0.##", inv)}%)"
                : $"ยกเลิกฟรีก่อนเวลานัด {sh} ชั่วโมง");
        return (PolicySourceService, fee is decimal fo ? $"ยกเลิกมีค่าธรรมเนียม {fo.ToString("0.##", inv)}%" : null);
    }

    /// <summary>"ถ้ายกเลิกตอนนี้" ของการจองด้วยบัตร — ผลจากตัวตัดสินเดียวกับเส้นยกเลิกจริง (<see cref="CourseCreditMath.CancelOutcome"/>)</summary>
    public static string CancelNowNote(DateTime startUtc, DateTime nowUtc, int passFreeCancelHours)
        => CourseCreditMath.CancelOutcome(startUtc, nowUtc, passFreeCancelHours) == CreditCancelOutcome.Refund
            ? $"ยกเลิกตอนนี้ได้เครดิตคืน (กติกาของบัตร: ก่อนเวลาเรียนอย่างน้อย {Math.Max(0, passFreeCancelHours)} ชั่วโมง)"
            : $"ยกเลิกตอนนี้ = ตัดเครดิต 1 ครั้งตามกติกาของบัตร (น้อยกว่า {Math.Max(0, passFreeCancelHours)} ชั่วโมงก่อนเวลาเรียน)";

    public static string ActionTh(LedgerAction a) => a switch
    {
        LedgerAction.SettleAttended => "ตัดเครดิต (มาเรียน)",
        LedgerAction.SettleNoShow => "ตัดเครดิต (ไม่มา)",
        LedgerAction.Release => "คืนเครดิต (ยกเลิก)",
        _ => "ไม่มีการเปลี่ยนแปลง",
    };
}
