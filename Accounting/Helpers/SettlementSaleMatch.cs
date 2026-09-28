using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ชนิดของสิ่งที่บรรทัดขาย/คืนเงินจับคู่ได้</summary>
public enum SettlementMatchCandidateKind
{
    Document = 1,
    PaymentIntent = 2,
    Reservation = 3,
}

/// <summary>ผู้สมัคร 1 รายการที่เลขออเดอร์ตรงกัน (tenant เดียวกัน · ค้นแบบตรงตัวเท่านั้น)</summary>
/// <param name="CanReceive">รับชำระจากผังพักได้จริง — เอกสาร: ลูกหนี้ (<c>ArApScope</c>) ที่ออกแล้วและยังมียอดค้าง ·
/// intent: รับเงินสำเร็จแล้ว (ลงผังพักไว้แล้ว)</param>
/// <param name="IsRefundTarget">เป็นต้นทางของใบลดหนี้ได้ — เอกสารขายที่ออกแล้ว · intent: คืนเงินผ่านระบบแล้ว (มี JE คืนเงินลงผังพัก)</param>
/// <param name="OpenAmount">ยอดค้างของเอกสาร (เทียบกับยอดขายของออเดอร์ในรอบโอน)</param>
public sealed record SettlementMatchCandidate(
    SettlementMatchCandidateKind Kind,
    Guid Id,
    string Label,
    decimal? OpenAmount,
    bool CanReceive,
    bool IsRefundTarget);

/// <summary>ผลการจับคู่ของบรรทัดหนึ่ง</summary>
public sealed record SettlementMatchDecision(
    SettlementMatchStatus Status,
    Guid? DocumentId,
    Guid? PaymentIntentId,
    Guid? ReservationId,
    IReadOnlyList<SettlementMatchCandidate> Candidates,
    string? Note);

/// <summary>
/// **จับคู่บรรทัดขาย/คืนเงินใน settlement กับของในระบบ — pure · ตัวตัดสินตัวเดียว** (รอบ 198 เฟส 1 ทีม B · DECISIONS ข้อ 3)
///
/// <para>═══ หลัก ═══ จับด้วย "เลขออเดอร์ตรงตัว" เท่านั้น (ค้นในบริษัทเดียวกัน) · <b>ไม่เดา</b>: ผู้สมัครหลายรายการ · มีแต่เอกสารที่รับชำระไม่ได้
/// (ใบเสร็จ/ร่าง/ชำระครบแล้ว) · มีแต่การจองที่พัก ⇒ <c>Unmatched</c> พร้อมผู้สมัครให้คนเลือก — <b>ไม่ตกเป็นใบขายสรุปรายวัน</b>
/// เพราะเลขออเดอร์นี้มีร่องรอยในระบบแล้ว ออกใบสรุปอีกใบ = รายได้ซ้ำ · <c>AutoSummary</c> เฉพาะเมื่อ "ไม่มีร่องรอยเลย"
/// (ไม่มีเลขออเดอร์ หรือค้นแล้วไม่พบอะไร) ⇒ ทีม C ออกใบขายสรุปรายวันให้เฉพาะบรรทัดสถานะนี้</para>
/// <para>ยอด: เอกสารที่รับชำระได้ตัวเดียว ⇒ เทียบ Σ ยอดขายของออเดอร์ในรอบโอน กับยอดค้าง ±0.01 — ต่าง ⇒ <c>AmountMismatch</c>
/// (ยังผูกเอกสารไว้ให้คนตรวจ — ห้ามตกใบสรุป) · คืนเงินที่ไม่รู้ใบเดิม ⇒ <c>Unmatched</c> (ตัวคิดแผนบล็อกตาม §86/10)</para>
/// </summary>
public static class SettlementSaleMatch
{
    public const decimal AmountTolerance = 0.01m;

    /// <param name="orderGroupAmount">Σ ยอดของบรรทัดองค์ประกอบยอดขาย (ขาย+ส่วนลด) ของออเดอร์เดียวกันในรอบโอนนี้</param>
    /// <param name="existingPaymentIntentId">บรรทัดที่ adapter ประกอบจาก PaymentIntent (อยู่ในผังพักแล้ว)</param>
    public static SettlementMatchDecision Decide(
        SettlementLineType type,
        string? orderId,
        decimal orderGroupAmount,
        IReadOnlyList<SettlementMatchCandidate> candidates,
        Guid? existingPaymentIntentId = null)
    {
        var rule = SettlementLineTypeRules.For(type);
        var none = Array.Empty<SettlementMatchCandidate>();
        if (!rule.Postable)
            return new(SettlementMatchStatus.Unmatched, null, null, null, none, "ยังไม่ได้จัดประเภท — จับคู่หลังเลือกประเภท");
        if (!rule.RequiresSaleMatch)
            return new(SettlementMatchStatus.NotRequired, null, null, null, none, null);
        if (existingPaymentIntentId is Guid pi)
            return new(SettlementMatchStatus.Matched, null, pi, null, none, null);

        var isRefund = rule.Posting == SettlementPostingKind.Refund;
        if (string.IsNullOrWhiteSpace(orderId) || candidates.Count == 0)
            return isRefund
                ? new(SettlementMatchStatus.Unmatched, null, null, null, none,
                    "คืนเงินที่ไม่พบใบขายเดิม — เลือกใบขายของออเดอร์นี้ (ใบลดหนี้ต้องอ้างใบเดิม §86/10)")
                : new(SettlementMatchStatus.AutoSummary, null, null, null, none, null);

        if (candidates.Count > 1)
            return new(SettlementMatchStatus.Unmatched, null, null, null, candidates,
                $"เลขออเดอร์ \"{orderId}\" ตรงกับ {candidates.Count} รายการในระบบ — เลือกเองว่าเป็นรายการไหน (ระบบไม่เดา)");

        var c = candidates[0];
        if (isRefund)
        {
            if (!c.IsRefundTarget)
                return new(SettlementMatchStatus.Unmatched, null, null, c.Kind == SettlementMatchCandidateKind.Reservation ? c.Id : null,
                    candidates, c.Kind == SettlementMatchCandidateKind.PaymentIntent
                        ? "รายการชำระนี้ยังไม่เคยคืนเงินผ่านระบบ — คืนเงินนอกระบบต้องเลือกใบขายเดิมเพื่อออกใบลดหนี้"
                        : "พบรายการที่เลขตรงกันแต่ใช้เป็นใบเดิมของใบลดหนี้ไม่ได้ — ตรวจและเลือกเอง");
            return c.Kind == SettlementMatchCandidateKind.PaymentIntent
                ? new(SettlementMatchStatus.Matched, null, c.Id, null, candidates, null)
                : new(SettlementMatchStatus.Matched, c.Id, null, null, candidates, null);
        }

        switch (c.Kind)
        {
            case SettlementMatchCandidateKind.PaymentIntent when c.CanReceive:
                return new(SettlementMatchStatus.Matched, null, c.Id, null, candidates, null);
            case SettlementMatchCandidateKind.Document when c.CanReceive:
                var diff = c.OpenAmount is decimal open ? Math.Abs(open - orderGroupAmount) : decimal.MaxValue;
                return diff <= AmountTolerance
                    ? new(SettlementMatchStatus.Matched, c.Id, null, null, candidates, null)
                    : new(SettlementMatchStatus.AmountMismatch, c.Id, null, null, candidates,
                        $"ยอดขายของออเดอร์ในรอบโอน ({orderGroupAmount:N2}) ไม่เท่ายอดค้างของ {c.Label} — ตรวจก่อนลงบัญชี");
            case SettlementMatchCandidateKind.Reservation:
                return new(SettlementMatchStatus.Unmatched, null, null, c.Id, candidates,
                    "เลขตรงกับการจองที่พัก — การลงบัญชี OTA ยังไม่รองรับ (เฟส 4) · เลือกใบขายของการจองนี้เอง");
            default:
                return new(SettlementMatchStatus.Unmatched, null, null, null, candidates,
                    $"เลขออเดอร์ตรงกับ {c.Label} ซึ่งรับชำระจากผังพักไม่ได้ (ร่าง · ใบเสร็จ · ชำระครบแล้ว) — ตรวจว่ารายได้ถูกบันทึกแล้วหรือไม่ แล้วเลือกเอง");
        }
    }

    /// <summary>บรรทัดที่ยังต้องให้คนตัดสินก่อนลงบัญชี</summary>
    public static bool NeedsDecision(SettlementMatchStatus s)
        => s is SettlementMatchStatus.Unmatched or SettlementMatchStatus.AmountMismatch;

    /// <summary>สถานะรอบโอนหลังนำเข้า/จัดประเภท/จับคู่ — ตัวตัดสินตัวเดียว (ทีม B ไม่ตั้ง Posted/BankMatched — เป็นของผู้ลงบัญชี)</summary>
    public static SettlementBatchStatus DeriveImportStatus(IEnumerable<(SettlementLineType Type, SettlementMatchStatus Match)> lines)
    {
        var list = lines.ToList();
        if (list.Any(l => l.Type == SettlementLineType.Unclassified)) return SettlementBatchStatus.Imported;
        if (list.Any(l => SettlementLineTypeRules.For(l.Type).RequiresSaleMatch && NeedsDecision(l.Match)))
            return SettlementBatchStatus.Classified;
        return SettlementBatchStatus.Matched;
    }

    /// <summary>รอบโอนยังแก้ได้ (จัดประเภท · จับคู่ · นำเข้าเพิ่ม · ยกเลิก) — ลงบัญชีแล้ว/ยกเลิกแล้ว ห้าม</summary>
    public static bool IsEditable(SettlementBatchStatus s)
        => s is SettlementBatchStatus.Imported or SettlementBatchStatus.Classified or SettlementBatchStatus.Matched;
}
