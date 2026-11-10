using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §4.1 · เฟส 243) — <b>ตัวตัดสินเดียว</b>ของสถานะ "50 ทวิ ที่ลูกค้าต้องส่งให้เรา" ต่อเอกสารขาย
///
/// <para>ผู้เรียกทุกจุด (รายการเอกสาร · หน้าเอกสาร · ทะเบียนภาษีถูกหัก · การ์ดแดชบอร์ด/ผู้ติดต่อ · รายงานปลายปี · ตัวกรอง) เรียก <see cref="Of"/> ตัวเดียว ·
/// หน้าเว็บแสดง <c>Label/Tone</c> จากเซิร์ฟเวอร์ (หลักการ #5 — ห้ามสำเนาป้ายใน JS) · enum ออกเป็นชื่อ ·
/// ห้ามผู้เรียกเทียบ <see cref="WhtCreditStatus.Pending"/> เองเพื่อแสดงป้าย (required_call_site_check R252)</para>
///
/// <para><b>NotYetPaid ≠ ค้าง</b>: ใบที่คาดว่าจะถูกหักแต่ยังไม่รับชำระ (หรือเกณฑ์สิทธิ์ที่ตั้ง 11910 ตอนออกใบแต่ยังไม่รับเงิน) ไม่ใช่การค้างของลูกค้า —
/// กันคำเตือนฟ้องใบถูก (หลักการ #8)</para>
/// </summary>
public static class WhtCertIntakeStatus
{
    public enum State
    {
        /// <summary>ไม่มี WHT ในใบนี้</summary>
        NotApplicable,
        /// <summary>คาดว่าจะถูกหัก แต่ยังไม่รับชำระ — ไม่ใช่การค้าง</summary>
        NotYetPaid,
        /// <summary>ถูกหักแล้ว ยังไม่มีใบ</summary>
        AwaitingCert,
        /// <summary>มีใบเข้ากล่องรับ รอตรวจ/รอยืนยัน</summary>
        Submitted,
        /// <summary>ได้ใบบางส่วน (ผ่อนงวด/ใบรวมยังไม่ครบ)</summary>
        PartiallyReceived,
        /// <summary>ได้ใบครบ</summary>
        Received,
        /// <summary>ลูกค้าแจ้งว่าหักผ่าน e-WHT แต่ยังไม่มีหลักฐาน</summary>
        EWhtAwaitingEvidence,
        /// <summary>ใบถูกตีกลับ — รอส่งใหม่</summary>
        Rejected,
        /// <summary>ใช้เครดิตในแบบแล้ว</summary>
        Claimed,
        /// <summary>ตัดสูญ</summary>
        WrittenOff,
    }

    /// <summary>ค่าตั้งต้น "เกินกำหนด" (วันหลังถูกหัก) — AUTO_SEND §4.1 / §11 ข้อ 4 (ทวง 7/21/45 · ป้ายแดงวันที่ 30)</summary>
    public const int DefaultOverdueDays = 30;

    /// <param name="WithheldAmount">ยอดที่ถูกหักตามทะเบียน (0 = ไม่มีแถวทะเบียน)</param>
    /// <param name="ExpectedWht">ยอดหักที่คาดบนเอกสาร (<c>Document.WithholdingTaxAmount</c>) — ใช้ตัดสิน NotYetPaid เมื่อยังไม่มีแถว</param>
    /// <param name="ReceivedAmount">ยอดที่ได้ใบแล้ว (Σ allocation ยืนยัน)</param>
    /// <param name="RowStatus">สถานะแถวทะเบียน · null = ไม่มีแถว</param>
    /// <param name="HasPendingIntake">มีใบในกล่องรับที่รอยืนยันและเสนอจับคู่กับแถวนี้</param>
    /// <param name="LastIntakeRejected">ใบล่าสุดที่จับคู่แถวนี้ถูกตีกลับ (และยังไม่มีใบใหม่)</param>
    /// <param name="WithheldSinceUtc">วันที่ถูกหักจริง (รับชำระ) · null = ยังไม่รับชำระ</param>
    public readonly record struct Facts(
        decimal WithheldAmount,
        decimal ExpectedWht,
        decimal ReceivedAmount,
        WhtCreditStatus? RowStatus,
        bool HasPendingIntake,
        bool LastIntakeRejected,
        WhtEvidenceKind EvidenceKind,
        DateTime? WithheldSinceUtc);

    /// <param name="Tone">none · muted · info · warning · danger · success (หน้าเว็บ map เป็นสีเท่านั้น)</param>
    public sealed record Verdict(State State, string Label, string Tone, int? DaysWaiting, bool IsOverdue, string? NextAction);

    public static Verdict Of(Facts f, DateTime nowUtc, int overdueDays = DefaultOverdueDays)
    {
        if (overdueDays <= 0) overdueDays = DefaultOverdueDays;
        if (f.RowStatus is null)
            return f.ExpectedWht > 0.005m
                ? new Verdict(State.NotYetPaid, $"คาดว่าจะถูกหัก ณ ที่จ่าย {f.ExpectedWht:N2} — ขอ 50 ทวิ เมื่อรับชำระ", "muted", null, false, null)
                : new Verdict(State.NotApplicable, "", "none", null, false, null);

        switch (f.RowStatus.Value)
        {
            case WhtCreditStatus.Expired:
                return new Verdict(State.WrittenOff, "50 ทวิ · ตัดสูญแล้ว", "muted", null, false, null);
            case WhtCreditStatus.Claimed:
                return new Verdict(State.Claimed, "50 ทวิ · ใช้เครดิตแล้ว", "success", null, false, null);
            case WhtCreditStatus.Received:
                return new Verdict(State.Received, "ได้รับ 50 ทวิ", "success", null, false, null);
        }

        // Pending — ถูกหักแล้ว (หรือตั้ง 11910 ตอนออกใบ) ยังไม่มีใบครบ
        if (f.WithheldSinceUtc is null)
            return new Verdict(State.NotYetPaid, $"คาดว่าจะถูกหัก ณ ที่จ่าย {Math.Max(f.WithheldAmount, f.ExpectedWht):N2} — ขอ 50 ทวิ เมื่อรับชำระ",
                "muted", null, false, null);

        var days = Math.Max(0, (int)Math.Floor((nowUtc - f.WithheldSinceUtc.Value).TotalDays));
        State st;
        if (f.HasPendingIntake) st = State.Submitted;
        else if (f.ReceivedAmount > 0.005m && f.ReceivedAmount < f.WithheldAmount - 0.005m) st = State.PartiallyReceived;
        else if (f.LastIntakeRejected) st = State.Rejected;
        else if (f.EvidenceKind == WhtEvidenceKind.EWithholding) st = State.EWhtAwaitingEvidence;
        else st = State.AwaitingCert;

        var overdue = IsOverdueState(st) && days > overdueDays;
        return st switch
        {
            State.Submitted => new Verdict(st, "ส่ง 50 ทวิ แล้ว · รอยืนยัน", "info", days, false,
                "เปิดกล่องรับ 50 ทวิ ที่หน้า \"ภาษีถูกหัก ณ ที่จ่าย\" แล้วกดยืนยัน"),
            State.PartiallyReceived => new Verdict(st,
                $"ได้รับ 50 ทวิ บางส่วน {f.ReceivedAmount:N2}/{f.WithheldAmount:N2}" + (overdue ? $" · เกินกำหนด {days} วัน" : ""),
                overdue ? "danger" : "warning", days, overdue, "ตามใบส่วนที่เหลือจากลูกค้า"),
            State.Rejected => new Verdict(st, $"50 ทวิ ถูกตีกลับ · รอส่งใหม่ · {days} วัน", overdue ? "danger" : "warning", days, overdue,
                "แจ้งลูกค้าให้ส่งใบที่ถูกต้องใหม่"),
            State.EWhtAwaitingEvidence => new Verdict(st, $"e-WHT · รอหลักฐาน · {days} วัน", overdue ? "danger" : "muted", days, overdue,
                "แนบหลักฐานจากระบบ e-Withholding Tax"),
            _ => overdue
                ? new Verdict(st, $"50 ทวิ เกินกำหนด · {days} วัน", "danger", days, true, "ทวงหนังสือรับรองจากลูกค้า — ยังเครดิต ภ.ง.ด.50 ไม่ได้")
                : new Verdict(st, $"รอ 50 ทวิ · {days} วัน", "warning", days, false, "ขอหนังสือรับรองจากลูกค้า"),
        };
    }

    /// <summary>รอบ 257 ชุด 2 (B-M3 · คำตัดสิน 257-2-3): ผู้ขายแจ้งว่า "ผู้จ่ายหักผ่าน e-WHT · รอหลักฐาน" ได้เฉพาะแถวรอใบที่ยังไม่ได้ใบเลย
    /// (ได้ใบบางส่วนแล้ว ⇒ ป้าย "ได้บางส่วน" ชนะอยู่แล้ว — สถานะ <see cref="State.EWhtAwaitingEvidence"/> จะไม่ปรากฏ) · ผู้เรียก: <c>WhtCreditService.SetEWhtPendingAsync</c>
    /// + ธงปุ่มบนทะเบียน (หน้าไม่ประกอบเงื่อนไขเอง) — เดิมไม่มีทางเข้าใดตั้ง EvidenceKind = EWithholding บนแถวรอใบ ⇒ สถานะ/ตัวกรอง "ewht" ไปไม่ถึง</summary>
    public static bool CanMarkEWhtPending(WhtCreditStatus status, decimal receivedAmount)
        => status == WhtCreditStatus.Pending && receivedAmount <= 0.005m;

    /// <summary>แสดงชิปบนรายการเอกสารไหม — ไม่มี WHT / ยังไม่รับชำระ ⇒ ไม่มีชิป (ไม่ใช่การค้าง · กันคำเตือนฟ้องใบถูก)</summary>
    public static bool ShowsChip(State s) => s is not (State.NotApplicable or State.NotYetPaid);

    /// <summary>สถานะที่นับวันค้าง/เกินกำหนดได้ (AUTO_SEND §4.1)</summary>
    private static bool IsOverdueState(State s)
        => s is State.AwaitingCert or State.Rejected or State.EWhtAwaitingEvidence or State.PartiallyReceived;

    /// <summary>สถานะที่ "ต้องตาม" (การ์ดแดชบอร์ด/ผู้ติดต่อ/รายงานปลายปี) — ยังเครดิตไม่ครบและเป็นหน้าที่ลูกค้า</summary>
    public static bool NeedsFollowUp(State s)
        => IsOverdueState(s) || s == State.Submitted;

    /// <summary>ตัวเลือกตัวกรอง "50 ทวิ" ของรายการเอกสาร (ค่า → ป้าย) — หน้าเว็บสร้างดรอปดาวน์จากชุดนี้ (ไม่มีสำเนาใน JS)</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> FilterOptions = new[]
    {
        ("awaiting", "รอใบ"),
        ("overdue", "เกินกำหนด"),
        ("submitted", "รอยืนยัน"),
        ("received", "ได้รับแล้ว"),
        ("ewht", "e-WHT"),
    };

    /// <summary>ค่าตัวกรองที่รู้จัก (ว่าง = ไม่กรอง) — ค่าที่ไม่รู้จัก ⇒ false (controller ตอบ 400 ไม่กรองเงียบ)</summary>
    public static bool TryParseFilter(string? value, out string? filter)
    {
        filter = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var v = value.Trim().ToLowerInvariant();
        if (!FilterOptions.Any(o => o.Value == v)) return false;
        filter = v;
        return true;
    }

    /// <summary>คำตัดสินนี้เข้าตัวกรองไหม — ตัวเดียวกับป้ายบนแถว</summary>
    public static bool MatchesFilter(Verdict v, string filter) => filter switch
    {
        "awaiting" => v.State is State.AwaitingCert or State.PartiallyReceived or State.Rejected,
        "overdue" => v.IsOverdue,
        "submitted" => v.State == State.Submitted,
        "received" => v.State is State.Received or State.Claimed,
        "ewht" => v.State == State.EWhtAwaitingEvidence,
        _ => false,
    };
}
