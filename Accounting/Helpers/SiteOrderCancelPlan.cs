using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลตัดสินเอกสาร ERP เมื่อยกเลิกออเดอร์หน้าเว็บ (รอบ 205 แพ็ก W2 · R-07 · BOOKING_REVENUE_AUDIT S-3)</summary>
public enum SiteOrderCancelErpAction
{
    /// <summary>ไม่มีเอกสาร / เอกสารตายแล้ว</summary>
    NothingToDo = 0,
    /// <summary>ยกเลิกเอกสารได้ (ยังไม่มีการชำระผูก · เส้นเดิม <c>VoidDocumentAsync</c>)</summary>
    VoidDocument = 1,
    /// <summary>ใบกำกับ/ใบเสร็จมีการชำระผูกแล้ว ⇒ void ถูกบล็อก (เงินลงบัญชีแล้ว) ⇒ <b>เสนอ</b>ใบลดหนี้ §86/10 เป็นร่าง + ชี้ทางคืนเงิน</summary>
    ProposeCreditNote = 2,
}

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "ยกเลิกออเดอร์ที่ชำระแล้ว ทำอะไรกับเอกสาร ERP" (R-07)
/// <para><b>ที่มา</b>: เดิมเส้นยกเลิกเรียก <c>VoidDocumentAsync</c> ทุกกรณี · ใบที่มีการรับเงินผูกถูกด่าน void บล็อก ⇒ ประทับ
/// <c>[ERP-VOID-FAILED]</c> แล้วค้างให้ทำมือ ทั้งที่ทางที่ถูกตามกฎหมายคือ<b>ใบลดหนี้อ้างใบเดิม</b> (§86/10 — ใบกำกับที่ออกแล้วห้ามแก้/ลบ) ·
/// ระบบ<b>ไม่อนุมัติ</b>ใบลดหนี้เอง (DECISION_DOCTRINE R1: เงินยังไม่ได้คืนลูกค้าจริง — พนักงานคืนเงินแล้วค่อยอนุมัติ)</para>
/// </summary>
public static class SiteOrderCancelPlan
{
    public const string RuleCode = "CMS-ORDER-CANCEL-ERP";
    public const string LegalReference = "RD-86/10";
    public const string CreditNoteDraftTag = "[ERP-CN-DRAFT]";

    public static SiteOrderCancelErpAction Decide(bool hasErpDocument, DocumentStatus? status, bool hasLinkedPayments)
    {
        if (!hasErpDocument || status is null) return SiteOrderCancelErpAction.NothingToDo;
        if (status is DocumentStatus.Voided or DocumentStatus.Rejected) return SiteOrderCancelErpAction.NothingToDo;
        if (status is DocumentStatus.Draft or DocumentStatus.WaitingApproval) return SiteOrderCancelErpAction.VoidDocument;
        if (hasLinkedPayments || status is DocumentStatus.Paid or DocumentStatus.PartiallyPaid)
            return SiteOrderCancelErpAction.ProposeCreditNote;
        return SiteOrderCancelErpAction.VoidDocument;
    }

    /// <summary>เหตุผลบนใบลดหนี้ร่าง — ยกเลิกการขายหลังชำระ · <see cref="CreditNoteReason.Adjustment"/> (ยังไม่ส่งของ ⇒ ไม่ใช่ "คืนสินค้า" ที่คืนสต๊อก)</summary>
    public const CreditNoteReason DraftReason = CreditNoteReason.Adjustment;

    public static string DraftNotes(string orderNumber, string invoiceNumber, DateTime invoiceDateUtc, string? cancellationReason)
        => $"ยกเลิกคำสั่งซื้อ {orderNumber} หลังชำระเงิน — ลดหนี้เต็มใบอ้างใบ {invoiceNumber} ลงวันที่ {ThaiDate.CalendarDateUtc(invoiceDateUtc):dd/MM/yyyy}"
           + (string.IsNullOrWhiteSpace(cancellationReason) ? "" : $" · เหตุผล: {cancellationReason.Trim()}")
           + " · ร่างโดยระบบ — คืนเงินลูกค้าแล้วจึงอนุมัติ (§86/10)";

    /// <summary>หมายเหตุบนออเดอร์ (ผู้เปิดดูทีหลังเห็นทางไปต่อครบ) · <paramref name="refundIntentIds"/> = รายการชำระออนไลน์ที่สำเร็จ (คืนที่ gateway)</summary>
    public static string ProposedNote(DateTime atUtc, string creditNoteNumber, string invoiceNumber, decimal amount,
        IReadOnlyCollection<Guid> refundIntentIds, decimal slipPaid)
    {
        var refund = refundIntentIds.Count > 0
            ? $"คืนเงินที่ “ระบบรับชำระเงิน” รายการ {string.Join(", ", refundIntentIds.Select(i => i.ToString("N")[..8]))}"
            : slipPaid > 0.005m ? $"คืนเงินโอน {slipPaid:N2} บาท ให้ลูกค้าด้วยมือแล้วบันทึกที่ใบลดหนี้" : "ไม่พบรายการรับเงินที่ยืนยัน — ตรวจก่อนคืน";
        return $"{CreditNoteDraftTag} [{atUtc:yyyy-MM-dd HH:mm}Z] ยกเลิกออเดอร์หลังชำระ — ใบ {invoiceNumber} มีการชำระผูก ยกเลิกใบไม่ได้ (§86/10) "
               + $"⇒ ระบบร่างใบลดหนี้ {creditNoteNumber} ยอด {amount:N2} ไว้ (ยังไม่อนุมัติ) · {refund} · อนุมัติใบลดหนี้เมื่อคืนเงินแล้ว";
    }

    public static string ProposeFailedNote(DateTime atUtc, string invoiceNumber, string reason)
        => $"[ERP-CN-FAILED {atUtc:yyyy-MM-dd HH:mm}Z] ยกเลิกออเดอร์แล้วแต่ร่างใบลดหนี้อ้างใบ {invoiceNumber} ไม่สำเร็จ: {reason} — ออกใบลดหนี้เองที่หน้าเอกสาร";
}
