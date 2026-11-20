using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 275 แพ็ก C — ตัวโหลด<b>ตัวเดียว</b>ของข้อเท็จจริงที่ <see cref="SettlementTaxInvoiceRole"/> ใช้ตัดสิน "ใบเสร็จของการรับเงินครั้งนี้ถือบทบาทใบกำกับ ณ วันรับเงินไหม"
/// + คิว "รับเงินแล้วยังไม่มีใบกำกับภาษี" (ใบเก่าไม่แก้ย้อนหลัง — ผู้ทำบัญชีกดออกใบกำกับเพิ่มทีละรายการ)
/// <para>ใช้ร่วมกันโดย <c>DocumentService.CreatePaymentAsync</c> · <c>IssueReceiptForPaymentAsync</c> · การย้ายการรับชำระ · ปุ่มออกใบกำกับเพิ่ม · คิวใน Autopilot —
/// รายการตัดยอดจากตัวโหลดเดียวกับแถวยอดใบเสร็จ (<see cref="SettlementReceiptTotalsLoader.LedgerEventsAsync"/>) · ทุก query ผูก CompanyId</para>
/// </summary>
internal static class SettlementTaxInvoiceLoader
{
    /// <summary>ป้ายบน <c>JournalEntry.Note</c> ของ JE ย้ายภาษีขาย 21913 → 21911 <b>ตามส่วนของงวด</b> — ผูกกับการรับชำระ (ตัวกลับตอนยกเลิกการรับงวดนั้นหาเจอจากป้ายนี้)</summary>
    public const string InstallmentReclassTagPrefix = "R275C-INSTALLMENT-PAY:";

    public static string InstallmentReclassTag(Guid paymentId) => InstallmentReclassTagPrefix + paymentId.ToString("N");

    private static readonly DocumentStatus[] DeadReceipt = { DocumentStatus.Voided, DocumentStatus.Rejected };

    /// <summary>การรับครั้งนี้ในรูปของตัวไล่ยอด (เงิน · ค่าธรรมเนียม · บรรทัดปรับ · ภาษีหักของงวดนี้)</summary>
    public static SettlementReceiptTotals.ThisSettlement ThisOf(Payment p)
        => new(p.PaymentDate, p.CreatedAt, p.Amount, p.FeeAmount, p.SettlementAdjustmentAmount, p.WithholdingTaxAmount);

    /// <summary>ภาษีขายของใบนี้พักไว้จนรับเงินไหม (งานบริการ — JE ของใบลง Cr 21913 ตอนอนุมัติ · หรือย้ายไปแล้ว <c>OutputVatDueAt</c>) — ข้อเท็จจริงจาก GL ไม่เดาจากบรรทัด
    /// <para>ใบมัดจำ (<c>IsDeposit</c> — ภาษีพักด้วยกลไกมัดจำ ไม่ใช่งานบริการ) ⇒ false (ไม่ออกใบกำกับรายงวด · ด่านรับบางส่วนเดิม)</para></summary>
    public static async Task<bool> VatParkedUntilPaymentAsync(AccountingDbContext db, Guid companyId, Document source)
        => !source.IsDeposit && (source.OutputVatDueAt != null || await db.JournalEntryLines.AsNoTracking()
            .AnyAsync(l => !l.IsDeleted && l.CreditAmount > 0m && l.Account.AccountCode == "21913"
                && l.JournalEntry.CompanyId == companyId && l.JournalEntry.SourceDocumentId == source.Id
                && l.JournalEntry.OriginalEntryId == null && !l.JournalEntry.IsDeleted));

    /// <summary>ตัดสินบทบาทใบกำกับของการรับครั้งนี้ — ใบต้นทางต้องเป็นสภาพ "หลังรับครั้งนี้" (<c>BalanceDue</c> ปัจจุบัน)</summary>
    /// <param name="thisPaymentId">การรับชำระของครั้งนี้ (ไม่นับเป็นรายการก่อนหน้า · ใบเสร็จของมันไม่นับเป็น "VAT ที่ใบอื่นถือแล้ว")</param>
    /// <param name="excludeAllocationIds">แถวจัดสรรของครั้งนี้ (การรับหลายใบ — รายใบ)</param>
    /// <param name="exceptReceiptId">ใบเสร็จที่ไม่นับ (ใบรับเปล่าเดิมที่กำลังถูกแทน)</param>
    public static async Task<SettlementTaxInvoiceRole.Decision> DecideAsync(AccountingDbContext db, Guid companyId, Document source,
        SettlementReceiptTotals.ThisSettlement me, Guid? thisPaymentId, IReadOnlyCollection<Guid>? excludeAllocationIds, Guid? exceptReceiptId)
        => SettlementTaxInvoiceRole.Decide(await FactsAsync(db, companyId, source, me, thisPaymentId, excludeAllocationIds, exceptReceiptId));

    public static async Task<SettlementTaxInvoiceRole.Facts> FactsAsync(AccountingDbContext db, Guid companyId, Document source,
        SettlementReceiptTotals.ThisSettlement me, Guid? thisPaymentId, IReadOnlyCollection<Guid>? excludeAllocationIds, Guid? exceptReceiptId)
    {
        var quick = new SettlementTaxInvoiceRole.Facts(source.DocumentType, source.SubTotal, source.VatAmount, source.TotalAmount,
            false, false, 0m, me, Array.Empty<SettlementReceiptTotals.LedgerEvent>(), source.BalanceDue);
        // ไม่ใช่ใบแจ้งหนี้ที่มี VAT ⇒ ตัวตัดสินตอบ None โดยไม่ต้องอ่านอะไร
        if (!SettlementTaxInvoiceRole.MayCarryVat(source.DocumentType, source.VatAmount)) return quick;

        var events = await SettlementReceiptTotalsLoader.LedgerEventsAsync(db, companyId, exceptReceiptId, source.Id, thisPaymentId,
            excludeAllocationIds ?? Array.Empty<Guid>());
        var superseded = await db.Documents.AsNoTracking()
            .AnyAsync(d => d.CompanyId == companyId && d.RelatedDocumentId == source.Id && d.DocumentType == DocumentType.TaxInvoice
                && !d.IsDeleted && !DeadReceipt.Contains(d.Status));
        // VAT ที่ใบเสร็จอื่นถือแล้ว — นับร่าง/รออนุมัติด้วย (ใบเสร็จรายงวดที่รอผู้มีสิทธิ์อนุมัติถือ VAT ของงวดนั้นแล้ว — ไม่นับ = งวดถัดไปถือซ้ำ)
        var otherVat = await db.Documents.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.RelatedDocumentId == source.Id && !r.IsDeleted
                && (r.DocumentType == DocumentType.Receipt || r.DocumentType == DocumentType.ReceiptVoucher)
                && r.VatAmount > 0.005m && !DeadReceipt.Contains(r.Status)
                && (exceptReceiptId == null || r.Id != exceptReceiptId)
                && (thisPaymentId == null || r.SettlementPaymentId == null || r.SettlementPaymentId != thisPaymentId))
            .SumAsync(r => (decimal?)r.VatAmount) ?? 0m;
        var parked = await VatParkedUntilPaymentAsync(db, companyId, source);
        return quick with
        {
            SupersededByTaxInvoice = superseded,
            VatParkedUntilPayment = parked,
            OtherLiveReceiptVat = otherVat,
            Events = events,
            VatReportedWholesale = parked && await VatReportedWholesaleAsync(db, companyId, source),
        };
    }

    /// <summary>ยอดภาษีขายที่ยังพัก 21913 ของใบ (Cr − Dr ของ JE ที่ใบเป็นต้นทาง · Posted/Reversed — ตัวนับเดียวกับตัวย้ายภาษี)</summary>
    public static async Task<decimal> UndueVatRemainingAsync(AccountingDbContext db, Guid companyId, Guid sourceId)
        => await db.JournalEntryLines.AsNoTracking()
            .Where(l => !l.IsDeleted && l.Account.AccountCode == "21913" && l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.SourceDocumentId == sourceId && !l.JournalEntry.IsDeleted
                && (l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed))
            .SumAsync(l => (decimal?)(l.CreditAmount - l.DebitAmount)) ?? 0m;

    /// <summary>ใบเก่า: ภาษีขายทั้งใบย้ายไปแล้วครั้งเดียว (ถึงกำหนดแล้ว · ไม่เหลือพัก · ไม่มี JE ย้ายตามส่วนของงวดที่ยังมีผล) — ใบแจ้งหนี้เป็นเจ้าของภาษีใน ภ.พ.30</summary>
    public static async Task<bool> VatReportedWholesaleAsync(AccountingDbContext db, Guid companyId, Document source)
        => source.OutputVatDueAt != null
           && await UndueVatRemainingAsync(db, companyId, source.Id) <= 0.005m
           && !await db.JournalEntries.AsNoTracking().AnyAsync(j => j.CompanyId == companyId && j.SourceDocumentId == source.Id && !j.IsDeleted
               && j.Note != null && j.Note.StartsWith(InstallmentReclassTagPrefix) && j.Status == JournalEntryStatus.Posted && j.ReversedByEntryId == null);

    /// <summary>ตัวจำแนก "ใบนี้มีบรรทัดสินค้าที่ตัดสต๊อก" ตัวเดียว (ตัวเดียวกับตอนเลือกบัญชีภาษีขาย 21913/21911 ตอนอนุมัติ — <c>DocumentService.InvoiceHasTrackedGoodsAsync</c> เรียกตัวนี้)</summary>
    public static async Task<bool> HasTrackedGoodsAsync(AccountingDbContext db, Guid companyId, IEnumerable<string?> productCodes)
    {
        var codes = productCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).Distinct().ToList();
        return codes.Count > 0 && await db.Products.AsNoTracking()
            .AnyAsync(p => p.CompanyId == companyId && codes.Contains(p.Code) && !p.IsDeleted && p.TrackStock);
    }

    /// <summary>แถวคิว "รับเงินแล้วยังไม่มีใบกำกับภาษี" — หนึ่งแถวต่อ (ใบแจ้งหนี้, การรับชำระ)</summary>
    /// <param name="PlainReceiptId">ใบรับเปล่า (VAT 0) ที่ออกให้การรับนี้แล้ว — ปุ่มออกใบกำกับจะยกเลิกใบนี้แล้วออกใบแทน</param>
    /// <param name="IssuesAllInstallments">ใบเก่าที่ภาษีทั้งใบรายงานแล้ว — ปุ่มออกใบกำกับให้<b>ทุกงวดที่ยังไม่มีของใบนี้พร้อมกัน</b> (ธุรกรรมเดียว · ภ.พ.30 ไม่เปลี่ยน)</param>
    public sealed record QueueRow(Guid DocumentId, string DocumentNumber, string? ContactName, Guid PaymentId, string PaymentNumber,
        DateTime PaymentDate, decimal Amount, Guid? PlainReceiptId, string? PlainReceiptNumber, SettlementTaxInvoiceRole.Decision Decision,
        bool IssuesAllInstallments = false);

    /// <summary>คิว (อ่านอย่างเดียว) — ใบแจ้งหนี้ที่มี VAT · ไม่ถูกใบกำกับแทน · มีการรับเงิน · VAT ที่ใบเสร็จถือยังไม่ครบ ⇒ การรับแต่ละครั้งที่ไม่มีใบเสร็จถือ VAT ·
    /// ขายสินค้า (ภาษีไม่พัก + มีบรรทัดตัดสต๊อก) ไม่เข้า (คำตัดสิน 4.1: ขายสินค้าคงเดิม) · tenant ทุก query</summary>
    public static Task<List<QueueRow>> QueueAsync(AccountingDbContext db, Guid companyId, int maxInvoices, CancellationToken ct)
        => QueueCoreAsync(db, companyId, null, maxInvoices, ct);

    /// <summary>คิวของใบแจ้งหนี้ใบเดียว (ปุ่มออกทุกงวดพร้อมกันของใบเก่า)</summary>
    public static Task<List<QueueRow>> QueueAsync(AccountingDbContext db, Guid companyId, Guid invoiceId, CancellationToken ct)
        => QueueCoreAsync(db, companyId, invoiceId, 1, ct);

    private static async Task<List<QueueRow>> QueueCoreAsync(AccountingDbContext db, Guid companyId, Guid? onlyInvoiceId, int maxInvoices, CancellationToken ct)
    {
        var live = new[] { DocumentStatus.Approved, DocumentStatus.Sent, DocumentStatus.PartiallyPaid, DocumentStatus.Paid, DocumentStatus.Overdue };
        var invoices = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.DocumentType == DocumentType.Invoice && d.VatAmount > 0.005m
                && (onlyInvoiceId == null || d.Id == onlyInvoiceId)
                && live.Contains(d.Status) && d.PaidAmount > 0.005m && !d.IsOpeningBalance
                && !db.Documents.Any(t => t.CompanyId == companyId && t.RelatedDocumentId == d.Id && t.DocumentType == DocumentType.TaxInvoice
                    && !t.IsDeleted && t.Status != DocumentStatus.Voided && t.Status != DocumentStatus.Rejected)
                && d.VatAmount - (db.Documents.Where(r => r.CompanyId == companyId && r.RelatedDocumentId == d.Id && !r.IsDeleted
                        && (r.DocumentType == DocumentType.Receipt || r.DocumentType == DocumentType.ReceiptVoucher)
                        && r.Status != DocumentStatus.Voided && r.Status != DocumentStatus.Rejected)
                    .Sum(r => (decimal?)r.VatAmount) ?? 0m) > 0.005m)
            .OrderByDescending(d => d.DocumentDate).Take(maxInvoices)
            .ToListAsync(ct);
        var rows = new List<QueueRow>();
        foreach (var inv in invoices)
        {
            var contact = inv.ContactId == null ? null : await db.Contacts.AsNoTracking()
                .Where(c => c.Id == inv.ContactId && c.CompanyId == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
            var pays = await db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted
                    && ((p.MovedToDocumentId ?? p.DocumentId) == inv.Id
                        || db.PaymentAllocations.Any(a => a.PaymentId == p.Id && a.DocumentId == inv.Id && a.CompanyId == companyId && a.ReleasedAt == null)))
                .OrderBy(p => p.PaymentDate).ThenBy(p => p.CreatedAt)
                .ToListAsync(ct);
            bool? goods = null;
            var wholesale = await VatReportedWholesaleAsync(db, companyId, inv);
            // ใบเก่าที่ภาษีทั้งใบรายงานแล้ว: งวดถัดไปตัดสินโดยถือว่างวดก่อนในคิวได้ใบกำกับแล้ว (ปุ่มออกทุกงวดพร้อมกันตามลำดับ)
            var issuedAhead = 0m;
            foreach (var p in pays)
            {
                var receipts = await db.Documents.AsNoTracking()
                    .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.RelatedDocumentId == inv.Id && r.SettlementPaymentId == p.Id
                        && r.Status != DocumentStatus.Voided && r.Status != DocumentStatus.Rejected)
                    .Select(r => new { r.Id, r.DocumentNumber, r.VatAmount })
                    .ToListAsync(ct);
                if (receipts.Any(r => r.VatAmount > 0.005m)) continue;
                var plain = receipts.FirstOrDefault();
                var (me, allocIds) = await ThisForDocumentAsync(db, companyId, p, inv.Id, ct);
                var facts = await FactsAsync(db, companyId, inv, me, p.Id, allocIds, plain?.Id);
                var decision = SettlementTaxInvoiceRole.Decide(wholesale ? facts with { OtherLiveReceiptVat = facts.OtherLiveReceiptVat + issuedAhead } : facts);
                if (wholesale && decision.CarriesRole) issuedAhead += decision.VatAmount;
                if (decision.Reason is SettlementTaxInvoiceRole.Why.NotInvoiceWithVat or SettlementTaxInvoiceRole.Why.SupersededByTaxInvoice
                    or SettlementTaxInvoiceRole.Why.FullVatAlreadyIssued or SettlementTaxInvoiceRole.Why.NothingSettled)
                    continue;
                if (decision.Reason == SettlementTaxInvoiceRole.Why.VatNotParked)
                {
                    goods ??= await HasTrackedGoodsAsync(db, companyId,
                        await db.DocumentLines.AsNoTracking().Where(l => l.DocumentId == inv.Id && !l.IsDeleted).Select(l => l.ProductCode).ToListAsync(ct));
                    if (goods == true) continue;   // ขายสินค้า — คงเดิม
                }
                rows.Add(new QueueRow(inv.Id, inv.DocumentNumber, contact, p.Id, p.PaymentNumber, p.PaymentDate, me.Settled,
                    plain?.Id, plain?.DocumentNumber, decision, IssuesAllInstallments: wholesale && decision.CarriesRole));
            }
        }
        return rows;
    }

    /// <summary>การรับครั้งนี้ "ของใบนี้": การชำระที่จัดสรรหลายใบ = แถวจัดสรรให้ใบนี้ (ตัวเดียวกับใบเสร็จรายใบ) · การชำระใบเดียว = ตัวการชำระ</summary>
    public static async Task<(SettlementReceiptTotals.ThisSettlement Me, List<Guid> AllocationIds)> ThisForDocumentAsync(
        AccountingDbContext db, Guid companyId, Payment p, Guid documentId, CancellationToken ct)
    {
        var mine = await db.PaymentAllocations.AsNoTracking()
            .Where(a => a.PaymentId == p.Id && a.DocumentId == documentId && a.CompanyId == companyId && a.ReleasedAt == null)
            .Select(a => new { a.Id, a.AllocatedAmount, a.WithholdingTaxAmount, a.CreatedAt, a.AppliedAt })
            .ToListAsync(ct);
        if (mine.Count == 0) return (ThisOf(p), new List<Guid>());
        var date = mine.Where(a => a.AppliedAt != null).Select(a => a.AppliedAt!.Value).DefaultIfEmpty(p.PaymentDate).Max();
        return (new SettlementReceiptTotals.ThisSettlement(date, mine.Min(a => a.CreatedAt), mine.Sum(a => a.AllocatedAmount), 0m, 0m,
            mine.Sum(a => a.WithholdingTaxAmount)), mine.Select(a => a.Id).ToList());
    }
}
