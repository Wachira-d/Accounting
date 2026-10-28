namespace Accounting.Helpers;

/// <summary>เหตุที่ลูกค้าไม่ได้ใช้บริการ — ตัดสินค่าธรรมเนียมคนละทาง (ยกเลิกเองดูหน้าต่างยกเลิกฟรี · ไม่มา = เลยเวลานัดแล้วเสมอ)</summary>
public enum BookingCancelKind { CustomerCancel = 1, NoShow = 2 }

/// <summary>แผนค่าธรรมเนียมของการจองนัดหมายที่ลูกค้ายกเลิก/ไม่มา (รอบ 205 W2 · R-10 R-11)</summary>
/// <param name="PolicyUndefined">บริการไม่ได้ตั้ง <c>CancellationFeePercent</c> เลย — "ไม่รู้ = บอกว่าไม่รู้": ระบบไม่ริบ/ไม่คืนเอง มัดจำคงเป็นหนี้สินรอพนักงานตัดสิน (เส้นเดิม)</param>
/// <param name="WithinFreeWindow">ยกเลิกก่อนเวลานัดไม่น้อยกว่า <c>FreeCancellationHours</c> ⇒ ไม่มีค่าธรรมเนียม</param>
/// <param name="Fee">ค่าธรรมเนียมตามนโยบาย (รวม VAT · หน่วยราคาที่ลูกค้าเห็น) ก่อนเพดาน</param>
/// <param name="ForfeitFromDeposit">ส่วนที่ริบจากมัดจำที่รับไว้จริง = min(Fee, มัดจำที่รับ) — คำตัดสินเจ้าของข้อ 126 (เพดาน = มัดจำ)</param>
/// <param name="RefundDue">มัดจำที่รับไว้ − ส่วนที่ริบ ⇒ รอคืน (ศูนย์มัดจำ/ใบลดหนี้ §86/10)</param>
/// <param name="UncollectedFee">ค่าธรรมเนียมส่วนที่เกินมัดจำ — ไม่มีเงินให้ริบ ⇒ บันทึกเป็นยอดค้างใน CRM เท่านั้น (ไม่ออกเอกสารเอง)</param>
public sealed record BookingCancellationFeePlan(
    BookingCancelKind Kind, bool PolicyUndefined, bool WithinFreeWindow,
    decimal Fee, decimal ForfeitFromDeposit, decimal RefundDue, decimal UncollectedFee)
{
    public bool HasForfeit => ForfeitFromDeposit > 0.005m;
    public bool HasRefund => RefundDue > 0.005m;
}

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ค่าธรรมเนียมยกเลิก/ไม่มา ของการจองนัดหมาย (CMS booking · รอบ 205 แพ็ก W2 · R-10 R-11)
/// <para>นโยบายต่อบริการ: <c>FreeCancellationHours</c> (ยกเลิกฟรีถ้าก่อนนัด ≥ ชั่วโมงนี้) · <c>CancellationFeePercent</c> (% ของค่าบริการรวม)
/// — เดิมสองค่านี้แสดงบนหน้าเว็บเฉย ๆ ไม่มีผู้อ่านเชิงธุรกิจ (F2 #2)</para>
/// <para>กติกา: (1) ไม่ตั้ง % เลย ⇒ <see cref="BookingCancellationFeePlan.PolicyUndefined"/> ระบบไม่ตัดสินแทน ·
/// (2) ยกเลิกเองในหน้าต่างฟรี ⇒ ค่าธรรมเนียม 0 คืนมัดจำทั้งหมด · ไม่มา (<see cref="BookingCancelKind.NoShow"/>) ไม่มีหน้าต่างฟรี ·
/// (3) ค่าธรรมเนียม = round(ค่าบริการรวม × %/100, 2) · (4) ริบได้ไม่เกินมัดจำที่รับไว้จริง (ข้อ 126) · ส่วนเกินเป็นยอดค้าง CRM ·
/// (5) ส่วนที่เหลือของมัดจำ = รอคืน — ระบบ<b>ไม่</b>คืนเงิน/ออกใบลดหนี้เอง (DECISION_DOCTRINE R1 — เงินยังไม่ออกจริง)</para>
/// </summary>
public static class BookingCancellationFee
{
    public const string RuleCode = "CMS-BOOKING-CANCEL-FEE";
    /// <summary>คำตัดสินเจ้าของข้อ 126 (เพดานค่าปรับ = มัดจำ) · ใบลดหนี้คืนมัดจำ §86/10</summary>
    public const string LegalReference = "DECISIONS-126 · RD-86/10";

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static BookingCancellationFeePlan Plan(BookingCancelKind kind, DateTime nowUtc, DateTime bookingStartUtc,
        int? freeCancellationHours, decimal? cancellationFeePercent, decimal totalAmount, decimal depositPaid)
    {
        var paid = Math.Max(0m, R2(depositPaid));
        if (cancellationFeePercent is null)
            return new(kind, PolicyUndefined: true, WithinFreeWindow: false, Fee: 0m, ForfeitFromDeposit: 0m, RefundDue: 0m, UncollectedFee: 0m);

        var pct = Math.Clamp(cancellationFeePercent.Value, 0m, 100m);
        var hoursBefore = (bookingStartUtc - nowUtc).TotalHours;
        var withinFree = kind == BookingCancelKind.CustomerCancel
            && freeCancellationHours is int h && h >= 0 && hoursBefore >= h;
        var fee = withinFree || pct <= 0m ? 0m : R2(Math.Max(0m, totalAmount) * pct / 100m);
        var forfeit = Math.Min(fee, paid);
        return new(kind, PolicyUndefined: false, WithinFreeWindow: withinFree,
            Fee: fee, ForfeitFromDeposit: forfeit, RefundDue: R2(paid - forfeit), UncollectedFee: R2(fee - forfeit));
    }

    /// <summary>ฐานก่อน VAT ของส่วนที่ริบ — ใบมัดจำเก็บ (ฐาน, VAT) แยก ⇒ ริบตามสัดส่วนฐาน/ยอดรวมของใบนั้น · ไม่เกินฐานคงค้าง</summary>
    public static decimal ForfeitBase(decimal forfeitGross, decimal depositSubTotal, decimal depositTotal, decimal remainingBase)
    {
        if (forfeitGross <= 0m || depositTotal <= 0m) return 0m;
        var b = R2(forfeitGross * depositSubTotal / depositTotal);
        return Math.Min(b, Math.Max(0m, remainingBase));
    }

    /// <summary>คำอธิบายให้ลูกค้า/พนักงานเห็นก่อนกด (ตัวเดียว — หน้าเว็บห้ามคิดเอง F2 #5)</summary>
    public static string Describe(BookingCancellationFeePlan p, int? freeCancellationHours)
    {
        if (p.PolicyUndefined)
            return "บริการนี้ไม่ได้กำหนดค่าธรรมเนียมยกเลิก — มัดจำที่ชำระไว้ (ถ้ามี) ร้านจะติดต่อกลับเรื่องการคืนเงิน";
        if (p.WithinFreeWindow)
            return $"ยกเลิกฟรี (ก่อนเวลานัดอย่างน้อย {freeCancellationHours} ชม.)" + (p.HasRefund ? $" — มัดจำ {p.RefundDue:N2} บาท รอคืน" : "");
        var parts = new List<string>();
        if (p.Fee > 0m) parts.Add($"ค่าธรรมเนียม{(p.Kind == BookingCancelKind.NoShow ? "ไม่มาตามนัด" : "ยกเลิก")} {p.Fee:N2} บาท");
        if (p.HasForfeit) parts.Add($"หักจากมัดจำ {p.ForfeitFromDeposit:N2} บาท");
        if (p.HasRefund) parts.Add($"มัดจำส่วนที่เหลือ {p.RefundDue:N2} บาท รอคืน");
        if (p.UncollectedFee > 0.005m) parts.Add($"ส่วนที่ยังไม่ได้ชำระ {p.UncollectedFee:N2} บาท (บันทึกเป็นยอดค้าง)");
        if (parts.Count == 0) parts.Add("ไม่มีค่าธรรมเนียม");
        return string.Join(" · ", parts);
    }

    /// <summary>ลูกค้าเลื่อนนัดเองได้ไหม — หน้าต่างเดียวกับยกเลิกฟรี (ไม่ตั้งนโยบาย = ได้) · นอกหน้าต่าง ⇒ ติดต่อร้าน (รอบ 206 · ข้อ 165)</summary>
    public static bool CanCustomerReschedule(BookingCancellationFeePlan plan) => plan.PolicyUndefined || plan.WithinFreeWindow;

    public static string RescheduleBlockedNote(int? freeCancellationHours)
        => $"เลื่อนนัดออนไลน์ได้เฉพาะก่อนเวลานัดอย่างน้อย {freeCancellationHours ?? 0} ชม. (ช่วงยกเลิกฟรี) — เลยกำหนดแล้ว กรุณาติดต่อร้านเพื่อเลื่อน";

    /// <summary>หมายเหตุภายในบนการจองเมื่อริบ/รอคืน</summary>
    public static string InternalNote(BookingCancellationFeePlan p, string? depositDocumentNumber, DateTime atUtc)
        => $"[{RuleCode}] {(p.Kind == BookingCancelKind.NoShow ? "ไม่มาตามนัด" : "ลูกค้ายกเลิกเอง")} {ThaiDate.CalendarDateUtc(atUtc):dd/MM/yyyy} — {Describe(p, null)}"
           + (depositDocumentNumber != null ? $" · ใบมัดจำ {depositDocumentNumber}" : "")
           + (p.HasRefund ? " · คืนเงิน/ออกใบลดหนี้ที่ “เงินมัดจำ” (ระบบไม่คืนเอง — R1)" : "");
}
