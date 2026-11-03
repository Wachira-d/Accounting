using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 228 (PLATFORM_OMISE_AUDIT §7.2 · B1 เส้นร้าน) — <b>ขา counter ของใบลดหนี้ขายที่ออกหลังคืนเงินผ่านผู้ให้บริการรับชำระ</b>
/// <para><b>ที่มา (บั๊กจริง):</b> คืนเงินผ่าน gateway ลงใบสำคัญ Dr ลูกหนี้การค้า / Cr บัญชีพัก ("ตั้งยอดรอใบลดหนี้" — <c>GatewayRefundService</c>) แล้วผู้ใช้ออกใบลดหนี้
/// อ้างใบที่ชำระครบ ⇒ <see cref="AdjustmentNoteAccount.CounterLegIsMoney"/> ให้ขา counter = ช่องทางเงินของใบ (เงินสด 111) ⇒ เงินออกสองขา (บัญชีพัก + เงินสด)
/// และลูกหนี้ค้างถาวร · ตอนนี้: ใบต้นทางมีเงินคืนผ่าน gateway ที่ยังไม่มีใบลดหนี้ครอบ ≥ ยอดใบลดหนี้นี้ ⇒ ขา counter = ผังลูกหนี้ของใบสำคัญคืนเงินนั้น (ล้างยอดรอ)</para>
/// <para><b>ไม่แตะ:</b> ใบที่ไม่มีคืนเงินผ่าน gateway (คืนเงินสด/ธนาคารตามเดิม) · ฝั่งซื้อ · ใบที่มีภาษีหัก ณ ที่จ่าย (ขา WHT ของโหมดเงิน — ใช้พฤติกรรมเดิม) ·
/// ยอดคืนที่ยังไม่ครอบน้อยกว่าใบลดหนี้ (ไม่แบ่งขา — พฤติกรรมเดิม) · การ "ครอบแล้ว" ใช้ชุดเดียวกับป้าย <c>GatewayRefundService.CreditNoteStatesAsync</c>
/// (ใบลดหนี้ที่ออกแล้วอ้างใบต้นทาง)</para>
/// </summary>
public static class GatewayRefundCreditNoteCounter
{
    public const string RuleCode = "GW-REFUND-CN-COUNTER";
    private const decimal Tolerance = 0.005m;

    /// <summary>ใช้ผังลูกหนี้ของใบสำคัญคืนเงินเป็นขา counter ไหม · pure</summary>
    internal static bool UsesRefundReceivable(bool salesCreditNote, bool counterIsMoney, decimal withholdingTax,
        decimal refundedUncovered, decimal creditNoteTotal)
        => salesCreditNote && counterIsMoney && withholdingTax <= 0m && creditNoteTotal > 0m
           && refundedUncovered + Tolerance >= creditNoteTotal;

    /// <summary>ยอดคืนผ่าน gateway ที่ยังไม่มีใบลดหนี้ครอบ · pure</summary>
    internal static decimal Uncovered(decimal refundedTotal, decimal creditedBefore) => Math.Max(0m, refundedTotal - creditedBefore);

    /// <summary>ผังลูกหนี้ของใบสำคัญคืนเงินผ่าน gateway ของใบต้นทาง (ใบล่าสุด · ขา Dr) เมื่อใบลดหนี้นี้ควรล้างยอดรอ — null = ใช้พฤติกรรมเดิม ·
    /// ทุก query กรอง <paramref name="companyId"/></summary>
    public static async Task<Guid?> RefundReceivableAccountIdAsync(AccountingDbContext db, Guid companyId, Guid sourceDocumentId,
        Guid creditNoteId, decimal creditNoteTotal, decimal withholdingTax, CancellationToken ct = default)
    {
        if (withholdingTax > 0m || creditNoteTotal <= 0m) return null;
        var refunds = await db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.Document && i.SourceId == sourceDocumentId
                && i.RefundedAmount > 0m && i.LastRefundJournalEntryId != null)
            .Select(i => new { i.RefundedAmount, i.LastRefundJournalEntryId, i.LastRefundedAt })
            .ToListAsync(ct);
        if (refunds.Count == 0) return null;
        var credited = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.DocumentType == DocumentType.CreditNote
                && d.RelatedDocumentId == sourceDocumentId && d.Id != creditNoteId
                && !DocumentStatusRules.NotIssued.Contains(d.Status) && d.Status != DocumentStatus.Voided)
            .SumAsync(d => (decimal?)d.TotalAmount, ct) ?? 0m;
        if (!UsesRefundReceivable(true, true, withholdingTax, Uncovered(refunds.Sum(r => r.RefundedAmount), credited), creditNoteTotal))
            return null;
        var jeId = refunds.OrderByDescending(r => r.LastRefundedAt).First().LastRefundJournalEntryId!.Value;
        return await (from l in db.JournalEntryLines.AsNoTracking()
                      join e in db.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.Id
                      where e.Id == jeId && e.CompanyId == companyId && l.DebitAmount > 0m
                      orderby l.LineOrder
                      select (Guid?)l.AccountId).FirstOrDefaultAsync(ct);
    }
}
