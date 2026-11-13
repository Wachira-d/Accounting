using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ใบวางบิล "จ่ายทั้งใบ" — ตัวตัดสินเดียว (pure)** (รอบ 267 แพ็ก P5 · คำตัดสินเจ้าของ 267-1-9 v2 · 267-1-72 · DOCUMENT_FLOW §2.12n-P5)
///
/// <para>ไม่มีเส้นเงินใหม่: เป้าหมายของรายการชำระ = ใบวางบิล (<c>PaymentSourceKind.Document</c> + <c>PublicPaymentController</c> + <c>IPaymentProvider</c> เดิม) ·
/// เงินเข้าแล้ว ⇒ <c>DocumentPaymentHandler</c> ตัวเดิมเรียก <c>IDocumentService.CreateMultiDocPaymentAsync</c> ตัวเดิม (การรับชำระหนึ่งรายการ จัดสรรให้ใบแจ้งหนี้แต่ละใบ ·
/// ล็อกแถว · ลง JE · ตัดลูกหนี้ · ใบเสร็จรายใบตามกติกา 258-1-20) — ที่นี่ตัดสินแค่ว่า "ชุดใบนี้จ่ายรวมได้ไหม" และ "จัดสรรอย่างไร"</para>
///
/// <para>เข้มกว่าการจ่ายรายใบโดยตั้งใจ (ฝ่ายค้านบังคับ — ดีไซน์ §18 267-1-9): ทุกใบที่ค้างต้องจ่ายรายใบได้ (ตัวตัดสินชั้นจ่ายเดิม <c>DocumentPayLinkPolicy.IssueRefusal</c>) ·
/// ไม่มีภาษีหัก ณ ที่จ่ายที่ผู้ซื้อต้องหัก (การหักหลายใบในการชำระเดียว + 50 ทวิ = เสี่ยง ⇒ ให้จ่ายรายใบ) · บาทเท่านั้น · ผู้ติดต่อเดียวกับใบวางบิล ·
/// ไม่ถูกแทนด้วยใบกำกับ · ไม่ครบ = เหตุ + ทางไปต่อ "ชำระรายใบ" (ปุ่ม "ชำระใบนี้" ของรอบ 267 P4 ยังอยู่)</para>
/// </summary>
public static class BillingNoteCombinedPay
{
    public const string RuleCode = "BN-PAY-ALL-267";

    /// <summary>ใบในใบวางบิล ณ ตอนถาม — <paramref name="PayRefusal"/> = เหตุที่ชั้นจ่ายเดิมปฏิเสธใบนี้ (null = จ่ายรายใบได้) ·
    /// <paramref name="ExpectedWht"/> = ภาษีหัก ณ ที่จ่ายที่ผู้ซื้อต้องหักของยอดค้าง (ตัวคิดเดียวกับลิงก์ชำระ) · <paramref name="Superseded"/> = ถูกใบกำกับภาษีแทนแล้ว</summary>
    public sealed record Child(Guid Id, string Number, DocumentType Type, DocumentStatus Status, Guid ContactId, string? Currency,
        decimal BalanceDue, decimal ExpectedWht, string? PayRefusal, bool Superseded);

    public sealed record Plan(decimal Total, IReadOnlyList<(Guid DocumentId, decimal Amount)> Allocations, string? Refusal)
    {
        public bool Payable => Refusal == null && Total > 0.005m;
    }

    /// <summary>ใบที่ยังค้าง (ยอดค้าง &gt; 0 · ไม่ยกเลิก) — ใบที่จ่ายครบแล้วไม่อยู่ในการจ่ายรวม</summary>
    public static bool IsOpen(Child c) => c.Status != DocumentStatus.Voided && c.BalanceDue > 0.005m;

    private static bool IsThb(string? currency) => string.IsNullOrWhiteSpace(currency) || string.Equals(currency.Trim(), "THB", StringComparison.OrdinalIgnoreCase);

    public const string PayEachHint = " — ชำระรายใบด้วยปุ่ม \"ชำระใบนี้\" ในรายการด้านล่าง";

    /// <summary>
    /// ตัดสินชุดใบ — คืนยอดรวม (ยอดค้างของทุกใบที่ค้าง · ตามลำดับในใบวางบิล) + การจัดสรรรายใบ (= ยอดค้างเต็มของแต่ละใบ — ใบแจ้งหนี้ที่มี VAT ผูกบางส่วนไม่ได้)
    /// หรือเหตุที่จ่ายรวมไม่ได้ · ไม่มีใบค้าง = ยอด 0 (ไม่ใช่เหตุ — หน้าแสดง "ชำระครบแล้ว")
    /// </summary>
    public static Plan Decide(Guid billingNoteContactId, IReadOnlyList<Child> children)
    {
        var open = children.Where(IsOpen).ToList();
        if (open.Count == 0) return new Plan(0m, Array.Empty<(Guid, decimal)>(), null);
        foreach (var c in open)
        {
            if (c.ContactId != billingNoteContactId)
                return Refuse($"ใบ {c.Number} เป็นของผู้ติดต่ออื่น — จ่ายรวมทั้งใบวางบิลไม่ได้");
            if (!IsThb(c.Currency))
                return Refuse($"ใบ {c.Number} เป็นสกุลเงินต่างประเทศ — จ่ายรวมรับเฉพาะเงินบาท");
            if (c.Superseded)
                return Refuse($"ใบ {c.Number} ออกใบกำกับภาษีแทนแล้ว — ชำระที่ใบกำกับภาษี");
            if (c.PayRefusal != null)
                return Refuse($"ใบ {c.Number}: {c.PayRefusal}");
            if (c.ExpectedWht > 0.005m)
                return Refuse($"ใบ {c.Number} มีภาษีหัก ณ ที่จ่ายที่ท่านต้องหัก — จ่ายรวมทั้งใบวางบิลยังไม่รองรับการหักหลายใบในครั้งเดียว");
        }
        var allocations = open.Select(c => (c.Id, Math.Round(c.BalanceDue, 2, MidpointRounding.AwayFromZero))).ToList();
        return new Plan(allocations.Sum(a => a.Item2), allocations, null);
    }

    private static Plan Refuse(string why) => new(0m, Array.Empty<(Guid, decimal)>(), why + PayEachHint);

    /// <summary>
    /// ตอนเงินเข้าแล้ว (ตัวจัดการเงินเข้าเดิม): ยอดที่ได้รับต้อง<b>เท่ากับ</b>ยอดรวมที่ตัดสินใหม่ ณ ตอนนี้ (พอดี — ไม่มีค่าคลาดเคลื่อน) — ระหว่างลูกค้าจ่าย มีใบถูกชำระ/ยกเลิก/แก้ยอด
    /// ⇒ ไม่เดาว่าจะจัดสรรอย่างไร (เงินเกินห้ามเข้าเงินรอจัดสรรเองจากทางอัตโนมัติ — 258-1-7) · คืนเหตุให้ผู้เรียกล้มดัง (ประวัติรายการชำระ + แจ้งเจ้าของ)
    /// </summary>
    public static string? SettleRefusal(decimal received, Plan plan, string billingNoteNumber)
    {
        if (plan.Refusal != null) return $"เงินเข้าแล้ว {received:N2} บาทสำหรับใบวางบิล {billingNoteNumber} แต่ชุดใบจ่ายรวมไม่ได้แล้ว: {plan.Refusal} — บันทึกรับชำระด้วยมือ";
        if (plan.Allocations.Count == 0) return $"เงินเข้าแล้ว {received:N2} บาทสำหรับใบวางบิล {billingNoteNumber} แต่ไม่มีใบค้างชำระแล้ว — ตรวจการชำระซ้ำแล้วคืนเงิน/บันทึกด้วยมือ";
        // ฝ่ายค้าน 267-P5 LOW-8: เท่ากันพอดี (เงินบาทจากผู้ให้บริการเป็นสตางค์เต็ม) — ต่าง 0.01 ก็ไม่ลง (เกิน = กลายเป็นเงินส่วนเกินที่ต้องมีเหตุ · ขาด = จัดสรรเกินยอดรับ)
        if (received != plan.Total)
            return $"เงินเข้า {received:N2} บาทไม่เท่ายอดค้างรวมของใบวางบิล {billingNoteNumber} ตอนนี้ ({plan.Total:N2}) — บางใบถูกชำระ/แก้ยอดระหว่างนั้น · บันทึกรับชำระด้วยมือ";
        return null;
    }
}
