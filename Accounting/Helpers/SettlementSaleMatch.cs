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
/// <param name="OpenAmount">ยอดค้างของเอกสาร (เทียบกับยอดขายของออเดอร์ในรอบโอน) · intent = ยอดที่รับชำระ (ก่อนคืน)</param>
/// <param name="Reason">เหตุผลที่ใช้เป็นผู้สมัครรับชำระ/ต้นทางคืนเงินไม่ได้ — แสดงเป็นหมายเหตุการจับคู่แทนข้อความทั่วไป (null = ใช้ข้อความทั่วไป)</param>
/// <param name="RefundRemaining">intent: ยอดคืนเงินผ่านระบบที่ยังไม่ถูกนับในบรรทัดคืนเงินของรอบโอนใด (R-B2) · เอกสาร = null</param>
/// <param name="RefundReason">เหตุผลที่ใช้เป็นต้นทางคืนเงินไม่ได้ (แยกจาก <paramref name="Reason"/> ของฝั่งรับชำระ — สาเหตุต่างกัน · R-B2)</param>
public sealed record SettlementMatchCandidate(
    SettlementMatchCandidateKind Kind,
    Guid Id,
    string Label,
    decimal? OpenAmount,
    bool CanReceive,
    bool IsRefundTarget,
    string? Reason = null,
    decimal? RefundRemaining = null,
    string? RefundReason = null);

/// <summary>ข้อเท็จจริงของ PaymentIntent 1 รายการที่เลขอ้างอิงตรงกับเลขออเดอร์ในไฟล์ (ค้นทั้งบริษัท — R-B4) — ผู้เรียกหาจากฐาน (tenant แล้ว)</summary>
/// <param name="UsableOnChannel">ช่องทางนี้ผูกกับ gateway ของรายการนี้ และผังพักของช่องทาง = ผังที่ขาเงินเข้าลงไว้ (R-A1) — false = พบแต่ใช้ไม่ได้</param>
/// <param name="Received">รับเงินสำเร็จแล้ว (ลงผังพักแล้ว)</param>
/// <param name="SettledByLegacyRound">ถูกบันทึกรอบโอนด้วยเส้นเดิม (<c>SettlementJournalEntryId</c>)</param>
/// <param name="OwnerBatchId">รอบโอน settlement ที่เป็นเจ้าของยอดขายของรายการนี้ (<c>PaymentIntent.SettlementBatchId</c>)</param>
/// <param name="RefundInOtherLines">Σ ยอดคืน (บวก) ของบรรทัดคืนเงินที่อ้างรายการนี้อยู่แล้วในรอบโอนใดก็ได้ของบริษัท (ไม่รวมบรรทัดที่กำลังตัดสิน)</param>
/// <param name="RefundOutcomeUnknown">มีการคืนเงินที่ผลยังไม่แน่ชัด (<c>RefundOutcomeUnknownSince</c> · review198-E2 E2-10)</param>
public sealed record SettlementIntentFact(
    Guid Id, string? ProviderRef, decimal Amount, decimal RefundedAmount, bool UsableOnChannel, bool Received,
    bool SettledByLegacyRound, Guid? OwnerBatchId, decimal RefundInOtherLines, bool RefundOutcomeUnknown);

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
                    candidates, c.RefundReason ?? c.Reason ?? (c.Kind == SettlementMatchCandidateKind.PaymentIntent
                        ? "รายการชำระนี้ยังไม่เคยคืนเงินผ่านระบบ — คืนเงินนอกระบบต้องเลือกใบขายเดิมเพื่อออกใบลดหนี้"
                        : "พบรายการที่เลขตรงกันแต่ใช้เป็นใบเดิมของใบลดหนี้ไม่ได้ — ตรวจและเลือกเอง"));
            return c.Kind == SettlementMatchCandidateKind.PaymentIntent
                ? new(SettlementMatchStatus.Matched, null, c.Id, null, candidates, null)
                : new(SettlementMatchStatus.Matched, c.Id, null, null, candidates, null);
        }

        switch (c.Kind)
        {
            case SettlementMatchCandidateKind.PaymentIntent when c.CanReceive:
                // R-B12: ยอดขายของออเดอร์ในไฟล์ต้องเท่ายอดที่รับชำระผ่านระบบ — ต่าง = ส่วนต่างค้างผังพักตลอดไป (สมการรอบโอนจับไม่ได้)
                if (c.OpenAmount is decimal paid && Math.Abs(paid - orderGroupAmount) > AmountTolerance)
                    return new(SettlementMatchStatus.AmountMismatch, null, c.Id, null, candidates,
                        $"ยอดขายของออเดอร์ในรอบโอน ({orderGroupAmount:N2}) ไม่เท่ายอดที่รับชำระผ่านระบบ {c.Label} — ตรวจก่อนลงบัญชี "
                        + "(ถ้าส่วนต่างเป็นค่าธรรมเนียม/ส่วนลดที่ไฟล์แยกไว้ ให้จัดประเภทบรรทัดนั้นให้ถูก)");
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
                    c.Reason ?? $"เลขออเดอร์ตรงกับ {c.Label} ซึ่งรับชำระจากผังพักไม่ได้ (ร่าง · ใบเสร็จ · ชำระครบแล้ว) — ตรวจว่ารายได้ถูกบันทึกแล้วหรือไม่ แล้วเลือกเอง");
        }
    }

    /// <summary>
    /// **ผู้สมัครจาก PaymentIntent** (review198-B R-B2 · R-B4 · review198-E2 E2-10) — ตัวตัดสินตัวเดียวของ "รายการรับชำระนี้ใช้รับชำระ/เป็นต้นทางคืนเงินได้ไหม"
    /// <list type="bullet">
    /// <item><b>R-B4</b>: พบ intent แต่ช่องทางนี้ใช้ไม่ได้ (ไม่ผูก gateway นั้น / ผังพักไม่ตรง) ⇒ ผู้สมัครที่ใช้ไม่ได้ + เหตุผล ⇒ ตัวตัดสินให้ <c>Unmatched</c>
    /// <b>ไม่ตก AutoSummary</b> (รายได้ของรายการนี้บันทึกไว้แล้วตอนรับชำระ — DOCTRINE §1 "เท็จเพราะไม่มีข้อมูล ห้ามผ่าน")</item>
    /// <item><b>R-B2</b>: ต้นทางคืนเงิน = ยอดคืนผ่านระบบ (<c>RefundedAmount</c>) ที่ยังไม่ถูกนับในบรรทัดคืนเงินรอบใด <b>ไม่ว่ารอบไหนเป็นเจ้าของยอดขาย</b> ·
    /// เหตุผลแยกตามสาเหตุ: ไม่เคยคืนผ่านระบบ · นับครบแล้วในรอบอื่น · บันทึกรอบโอนด้วยเส้นเดิม</item>
    /// <item><b>E2-10</b>: การคืนเงินผลไม่แน่ชัด ⇒ ใช้ไม่ได้ทั้งรับชำระและคืนเงินจนกว่าจะตรวจผล</item>
    /// </list>
    /// </summary>
    public static SettlementMatchCandidate IntentCandidate(SettlementIntentFact i, Guid currentBatchId)
    {
        var label = $"รายการรับชำระ {i.ProviderRef} ({i.Amount:N2})";
        SettlementMatchCandidate No(string reason)
            => new(SettlementMatchCandidateKind.PaymentIntent, i.Id, label, i.Amount, false, false, reason, 0m, reason);
        if (!i.UsableOnChannel)
            return No($"เลขนี้ตรงกับ{label}ในระบบ แต่ช่องทางนี้ไม่ได้ผูกกับ gateway ของรายการนั้น (หรือผังพักไม่ตรงกับผังที่รับเงินไว้) — "
                + "รายได้ของรายการนี้บันทึกไว้แล้วตอนรับชำระ ห้ามออกใบขายสรุปซ้ำ · ผูกช่องทางกับการตั้งค่า gateway ให้ผังพักตรงกัน "
                + "หรือบันทึกบรรทัดนี้เป็นรายการปรับปรุง");
        if (i.RefundOutcomeUnknown)
            return No($"{label} มีการคืนเงินที่ผลยังไม่แน่ชัด — ตรวจผลการคืนเงินกับผู้ให้บริการก่อน แล้วกดจับคู่ใหม่");
        if (i.SettledByLegacyRound)
            return No($"{label} ถูกบันทึกรอบโอนด้วยหน้ารอบโอน gateway เดิมแล้ว — ยอดขาย/คืนเงินภายหลังของรายการนี้ลงผ่านหน้านั้น ห้ามนับซ้ำในรอบโอนนี้");
        var ownedElsewhere = i.OwnerBatchId is Guid owner && owner != currentBatchId;
        var canReceive = i.Received && !ownedElsewhere;
        var saleReason = canReceive ? null
            : ownedElsewhere ? $"{label} อยู่ในรอบโอนอื่นแล้ว (รอบแรกเป็นเจ้าของยอดขาย) — ยอดขายนับซ้ำไม่ได้"
            : $"{label} ยังไม่รับเงินสำเร็จ";
        var remaining = Math.Max(0m, i.RefundedAmount - i.RefundInOtherLines);
        var isRefundTarget = remaining > AmountTolerance;
        var refundReason = isRefundTarget ? null
            : i.RefundedAmount <= 0m
                ? "รายการชำระนี้ยังไม่เคยคืนเงินผ่านระบบ — คืนเงินนอกระบบต้องเลือกใบขายเดิมเพื่อออกใบลดหนี้"
                : $"ยอดคืนเงินผ่านระบบของ{label} ({i.RefundedAmount:N2}) ถูกนับในบรรทัดคืนเงินของรอบโอนแล้วครบ — บรรทัดนี้อาจเป็นการคืนซ้ำในไฟล์ "
                    + "หรือคืนนอกระบบ (เลือกใบขายเดิมเพื่อออกใบลดหนี้)";
        return new(SettlementMatchCandidateKind.PaymentIntent, i.Id, label, i.Amount, canReceive, isRefundTarget, saleReason, remaining,
            refundReason);
    }

    /// <summary>
    /// **จัดสรรยอดคืนของ intent ให้บรรทัดคืนเงินทีละบรรทัด** (R-B2) — สองบรรทัดคืน 50 ของรายการที่คืนผ่านระบบแค่ 50 ห้ามจับคู่ได้ทั้งคู่ ·
    /// บรรทัดที่ยอดเกินที่เหลือ ⇒ <c>Unmatched</c> พร้อมเหตุผล · คืนยอดที่เหลือหลังหักบรรทัดนี้
    /// </summary>
    public static (SettlementMatchDecision Decision, decimal Remaining) ApplyIntentRefundCapacity(SettlementMatchDecision d,
        decimal lineRefundAmount, decimal remaining)
    {
        if (d.Status != SettlementMatchStatus.Matched || d.PaymentIntentId is null) return (d, remaining);
        var need = Math.Abs(lineRefundAmount);
        if (need <= remaining + AmountTolerance) return (d, Math.Max(0m, remaining - need));
        return (new SettlementMatchDecision(SettlementMatchStatus.Unmatched, null, null, null, d.Candidates,
            $"ยอดคืนของบรรทัดนี้ ({need:N2}) เกินยอดคืนเงินผ่านระบบที่ยังไม่ถูกนับ ({remaining:N2}) — ส่วนที่เกินเป็นการคืนนอกระบบ "
            + "เลือกใบขายเดิมเพื่อออกใบลดหนี้"), remaining);
    }

    /// <summary>
    /// **ยอดขายของแต่ละออเดอร์ในรอบโอน** — Σ ยอดของบรรทัดองค์ประกอบยอดขาย (ขาย+ส่วนลด) ต่อเลขออเดอร์ (ตัดช่องว่าง · ตรงตัว) ·
    /// ตัวตั้งตัวเดียวของตัวจับคู่อัตโนมัติ (<c>MatchLinesAsync</c>) หน้าจอ (ผู้สมัครที่เลือกได้) และการตัดสินของคน (<c>AssignLineMatchAsync</c>)
    /// — สามทางต้องเทียบยอดชุดเดียวกัน ไม่งั้นหน้าจอให้เลือกแต่เซิร์ฟเวอร์ตีกลับ (review198-D D-01)
    /// </summary>
    public static Dictionary<string, decimal> OrderGroupAmounts(IEnumerable<(SettlementLineType Type, string? OrderId, decimal Amount)> lines)
        => lines
            .Where(l => SettlementLineTypeRules.For(l.Type).Posting == SettlementPostingKind.SaleComponent && !string.IsNullOrWhiteSpace(l.OrderId))
            .GroupBy(l => l.OrderId!.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount), StringComparer.Ordinal);

    /// <summary>
    /// **ผู้สมัครรายการนี้ "คนเลือกได้" ไหม — ตัวตัดสินตัวเดียวของหน้าจอและของ <c>AssignLineMatchAsync</c>** (review198-D D-01) ·
    /// null = เลือกได้ · ไม่ null = เหตุผลภาษาไทยที่เลือกไม่ได้ (หน้าจอแสดงข้อความนี้ · เซิร์ฟเวอร์ตีกลับด้วยข้อความเดียวกัน)
    /// <list type="bullet">
    /// <item>เอกสาร: ขาย = รับชำระจากผังพักได้ (<c>CanReceive</c>) · คืนเงิน = ใบเดิมของใบลดหนี้ได้ (<c>IsRefundTarget</c>) — ยอดไม่ตรงคนเลือกได้
    /// (ตัวคิดแผนรับชำระเท่ายอดของบรรทัด · พฤติกรรมเดิมของการเลือกเอกสาร)</item>
    /// <item>รายการรับชำระ (PaymentIntent): <b>ด่านเดียวกับการจับคู่อัตโนมัติ</b> — ขาย = <c>CanReceive</c> (รวม "อยู่รอบอื่นแล้ว" · ผลคืนเงินไม่แน่ชัด ·
    /// ช่องทางใช้ไม่ได้) <b>และยอดขายของออเดอร์ = ยอดที่รับชำระ</b> ±0.01 (ต่าง = ส่วนต่างค้างผังพักตลอดไป R-B12) · คืนเงิน = <c>IsRefundTarget</c>
    /// และยอดคืนของบรรทัด ≤ ยอดคืนผ่านระบบที่ยังไม่ถูกนับ (<see cref="ApplyIntentRefundCapacity"/> R-B2)</item>
    /// <item>การจองที่พัก: เลือกเป็นคู่ของบรรทัดไม่ได้ (การลงบัญชี OTA เฟส 4) — เลือกใบขายของการจองนั้นแทน</item>
    /// </list>
    /// </summary>
    /// <param name="orderGroupAmount">ยอดของออเดอร์จาก <see cref="OrderGroupAmounts"/> (บรรทัดที่ไม่มีเลขออเดอร์ = ยอดของบรรทัดเอง)</param>
    /// <param name="lineAmount">ยอดของบรรทัดที่กำลังตัดสิน (คืนเงิน = ติดลบ)</param>
    public static string? AssignRefusal(SettlementMatchCandidate c, SettlementLineType type, decimal orderGroupAmount, decimal lineAmount)
    {
        var rule = SettlementLineTypeRules.For(type);
        if (!rule.RequiresSaleMatch) return $"บรรทัดประเภท \"{rule.LabelTh}\" ไม่ต้องจับคู่ใบขาย";
        var isRefund = rule.Posting == SettlementPostingKind.Refund;
        switch (c.Kind)
        {
            case SettlementMatchCandidateKind.Document:
                if (isRefund)
                    return c.IsRefundTarget ? null
                        : c.RefundReason ?? c.Reason ?? "เอกสารนี้ใช้เป็นใบเดิมของใบลดหนี้ไม่ได้ (ต้องออกแล้วและไม่ถูกยกเลิก)";
                return c.CanReceive ? null
                    : c.Reason ?? "เอกสารนี้รับชำระจากผังพักไม่ได้ (ต้องเป็นใบแจ้งหนี้/ใบกำกับที่ออกแล้วและยังมียอดค้าง) — "
                        + "ถ้ารายได้ของออเดอร์นี้บันทึกด้วยใบเสร็จแล้ว ให้บันทึกบรรทัดนี้เป็นรายการปรับปรุงแทน (กันรายได้ซ้ำ)";
            case SettlementMatchCandidateKind.PaymentIntent:
                if (isRefund)
                {
                    if (!c.IsRefundTarget)
                        return c.RefundReason ?? c.Reason ?? "รายการชำระนี้ยังไม่เคยคืนเงินผ่านระบบ — คืนเงินนอกระบบต้องเลือกใบขายเดิมเพื่อออกใบลดหนี้";
                    var need = Math.Abs(lineAmount);
                    var left = c.RefundRemaining ?? 0m;
                    return need <= left + AmountTolerance ? null
                        : $"ยอดคืนของบรรทัดนี้ ({need:N2}) เกินยอดคืนเงินผ่านระบบที่ยังไม่ถูกนับ ({left:N2}) — ส่วนที่เกินเป็นการคืนนอกระบบ "
                          + "เลือกใบขายเดิมเพื่อออกใบลดหนี้";
                }
                if (!c.CanReceive) return c.Reason ?? $"{c.Label} ใช้รับชำระจากผังพักไม่ได้";
                if (c.OpenAmount is decimal paid && Math.Abs(paid - orderGroupAmount) > AmountTolerance)
                    return $"ยอดขายของออเดอร์ในรอบโอน ({orderGroupAmount:N2}) ไม่เท่ายอดที่รับชำระผ่านระบบ {c.Label} — ส่วนต่างจะค้างผังพักตลอดไป · "
                           + "จัดประเภทบรรทัดค่าธรรมเนียม/ส่วนลดที่ไฟล์แยกไว้ให้ถูกก่อน หรือบันทึกส่วนต่างเป็นรายการปรับปรุง";
                return null;
            default:
                return "การจองที่พักเลือกเป็นคู่ของบรรทัดโดยตรงไม่ได้ (การลงบัญชี OTA ยังไม่รองรับ — เฟส 4) — เลือกใบขายของการจองนี้แทน";
        }
    }

    /// <summary>
    /// **การจับคู่ที่คนตัดสินเอง ห้ามถูกจับคู่อัตโนมัติทับ** (review198-B R-B1) — เปลี่ยนประเภทบรรทัดแล้วยังต้องจับคู่ในกลุ่มเดิม
    /// (องค์ประกอบยอดขาย ↔ องค์ประกอบยอดขาย · คืนเงิน ↔ คืนเงิน) ⇒ คงคำตัดสินของคน · เปลี่ยนข้ามกลุ่ม/ไม่ต้องจับคู่แล้ว ⇒ คำตัดสินเดิมหมดความหมาย
    /// </summary>
    public static bool KeepUserMatch(bool matchDecidedByUser, SettlementLineType oldType, SettlementLineType newType)
    {
        if (!matchDecidedByUser) return false;
        var n = SettlementLineTypeRules.For(newType);
        return n.RequiresSaleMatch && SettlementLineTypeRules.For(oldType).Posting == n.Posting;
    }

    /// <summary>
    /// **บรรทัดที่อ้าง PaymentIntent ถูกล้างออกจากผังพักไปแล้วด้วยเส้นอื่นไหม** (ด่านผู้ลงบัญชี R-A1 · แก้คู่กับ R-B2) — ฝั่งขาย: เส้นเดิมบันทึกรอบโอนแล้ว
    /// หรือรอบโอนอื่นเป็นเจ้าของยอดขาย · ฝั่งคืนเงิน: เฉพาะเส้นเดิม — คืนเงินภายหลังของรายการที่รอบก่อนเป็นเจ้าของยอดขาย คือบรรทัดที่ควรอยู่รอบนี้
    /// (เดิมตัดสินแบบเดียวกันทั้งสองฝั่ง ⇒ บรรทัดคืนเงินภายหลังทุกบรรทัดถูกบล็อกว่า "นับซ้ำ")
    /// </summary>
    public static bool IntentSettledElsewhere(bool refundLine, Guid? legacySettlementJournalEntryId, Guid? ownerBatchId, Guid batchId)
        => legacySettlementJournalEntryId is not null
           || (!refundLine && ownerBatchId is Guid owner && owner != batchId);

    /// <summary>
    /// **ตรวจซ้ำใต้ล็อก gateway: บรรทัดฝั่งขายที่<b>เพิ่งอ้าง</b> intent ซึ่งรอบโอนอื่นเป็นเจ้าของแล้ว** (รอบ 201 ฝ่ายค้าน ST-X6) — ตัวจับคู่/คนเลือกอ่าน intent ก่อนถือล็อก gateway
    /// (ล็อกช่องทางอย่างเดียว) ⇒ รอบโอนของ<b>ช่องทางอื่นที่ใช้ gateway เดียวกัน</b>ประทับ intent ตัวนั้นไประหว่างนี้ได้ · เดิมข้ามเงียบ แล้วไปติดตาข่าย
    /// <see cref="IntentSettledElsewhere"/> ตอนลงบัญชี ⇒ ล้มดังทันทีแบบเดียวกับ "ถูกบันทึกด้วยหน้ารอบโอน gateway เดิม" ·
    /// <b>บรรทัดคืนเงินยกเว้น</b> (คืนเงินภายหลังของรายการที่รอบก่อนเป็นเจ้าของยอดขาย = ปกติ) · <b>เฉพาะการอ้างที่เพิ่งเกิดในคำสั่งนี้</b> — การอ้างค้างจากก่อนหน้า
    /// ไม่ล้มทุกการแก้ของรอบ (ทางตันถาวร) แต่ยังติดด่านลงบัญชีที่มีทางไปต่อ · pure
    /// </summary>
    /// <param name="refundLine">บรรทัดนั้นเป็นฝั่งคืนเงิน (<c>SettlementLineTypeRules.For(t).Posting == Refund</c>)</param>
    /// <param name="newlyReferenced">บรรทัดเพิ่งอ้าง intent นี้ในคำสั่งนี้ (บรรทัดใหม่ หรือช่อง PaymentIntentId เพิ่งเปลี่ยน)</param>
    public static bool SaleReferenceTakenByOtherBatch(bool refundLine, bool newlyReferenced, Guid? ownerBatchId, Guid batchId)
        => !refundLine && newlyReferenced && ownerBatchId is Guid owner && owner != batchId;

    /// <summary>R-B16: แก้ค่าธรรมเนียมของ PaymentIntent ที่อยู่ในรอบโอน settlement แล้วไม่มีผล (บรรทัดค่าธรรมเนียมถูกบันทึกไปแล้ว) — ต้องบอก ไม่เงียบ</summary>
    public static string? FeeEditBlockedByBatch(Guid? settlementBatchId)
        => settlementBatchId is null ? null
            : "รายการนี้อยู่ในรอบโอน settlement แล้ว — ค่าธรรมเนียมถูกบันทึกเป็นบรรทัดของรอบโอนนั้นไปแล้ว แก้ที่นี่จะไม่มีผล · "
              + "ถ้ารอบโอนยังไม่ลงบัญชี ให้ยกเลิกรอบโอนแล้วแก้ค่าธรรมเนียมก่อนประกอบรอบโอนใหม่ · ถ้าลงบัญชีแล้ว ให้เพิ่มบรรทัดปรับปรุงในรอบโอนถัดไป";

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

    /// <summary>
    /// **รอบโอนที่ลงบัญชีค้างครึ่งทางแก้/ยกเลิกไม่ได้** (review198-C C-1(b)) — สถานะยังเป็น Matched แต่มีเอกสาร/การรับชำระที่การลงบัญชีสร้างแล้ว ⇒
    /// แก้บรรทัดทำให้ของที่ออกแล้วไม่ตรงแผน · ยกเลิกรอบโอนทำให้ของที่ออกแล้วเป็นกำพร้า (นำเข้าใหม่แล้วลงซ้ำ = ค่าใช้จ่าย/ภาษีซื้อ/รายได้ซ้ำ)
    /// </summary>
    /// <param name="postingArtifacts">จำนวนเอกสาร/การรับชำระที่ยังไม่ถูกยกเลิกซึ่งมีป้ายของรอบโอนนี้</param>
    public static bool IsEditable(SettlementBatchStatus s, int postingArtifacts)
        => IsEditable(s) && postingArtifacts == 0;
}
