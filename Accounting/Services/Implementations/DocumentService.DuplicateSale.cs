using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Journal;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 266 แพ็ก C — ตัวอ่านข้อเท็จจริงของ "ตัวช่วยใบซ้ำ" (<see cref="DuplicateSalePolicy"/>) และโหนด "เงิน" ของสายเอกสาร (<see cref="DocumentChainMoney"/>)
/// <para><b>ไม่มีกติกาใหม่ที่นี่</b>: ตัวตัดสินอยู่ที่ <c>Helpers/DuplicateSalePolicy</c> · ด่านยกเลิกใช้ตัวโหลด/ตัวตัดสินเดิม (e-Tax ถึงกรมสรรพากร
/// <c>DocumentVoidPreconditions</c> · เอกสารลูก · 50 ทวิ ในแบบที่ยื่น · งวดภาษีขาย <see cref="OutputVatPeriodLockReasonAsync"/>) · อ่านอย่างเดียว (AsNoTracking) · tenant ทุก query</para>
/// </summary>
public partial class DocumentService
{
    /// <inheritdoc />
    public async Task<DuplicateSaleSnapshot> LoadDuplicateSaleFactsAsync(Guid companyId, Guid cashSaleId, Guid invoiceId)
    {
        var cash = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                       .FirstOrDefaultAsync(d => d.Id == cashSaleId && d.CompanyId == companyId)
                   ?? throw new KeyNotFoundException("ไม่พบใบขายสด");
        var inv = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                      .FirstOrDefaultAsync(d => d.Id == invoiceId && d.CompanyId == companyId)
                  ?? throw new KeyNotFoundException("ไม่พบใบแจ้งหนี้");

        var cashFacts = await DuplicateSaleDocFactsAsync(companyId, cash);
        var invFacts = await DuplicateSaleDocFactsAsync(companyId, inv);

        var invLive = await _db.Payments.AsNoTracking()
                          .AnyAsync(p => p.CompanyId == companyId && !p.IsDeleted && (p.MovedToDocumentId ?? p.DocumentId) == inv.Id)
                      || await _db.PaymentAllocations.AsNoTracking()
                          .AnyAsync(a => a.CompanyId == companyId && !a.IsDeleted && a.DocumentId == inv.Id)
                      || await _db.Documents.AsNoTracking()
                          .AnyAsync(d => d.CompanyId == companyId && !d.IsDeleted && d.IsDeposit && d.DepositAppliedToDocumentId == inv.Id
                              && d.Status != DocumentStatus.Voided);
        var liveCn = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.DocumentType == DocumentType.CreditNote
                && d.RelatedDocumentId == inv.Id && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
            .Select(d => d.DocumentNumber).FirstOrDefaultAsync();
        var payDate = DuplicateSalePolicy.PaymentDate(cash.DocumentDate, inv.DocumentDate);
        var sameEntityOtherBranch = cash.ContactId != inv.ContactId && cash.ContactId != Guid.Empty && inv.ContactId != Guid.Empty
            && (await ContactTaxBranchKey.SameEntityIdsAsync(_db.Contacts.AsNoTracking(), companyId, cash.ContactId)).Contains(inv.ContactId);

        var ctx = new DuplicateSaleContext(
            CashSaleKind: ReceiptLinkPolicy.Classify(ReceiptLinkFacts.Source(cash)),
            CashSaleFromUnallocatedCredit: (await ReceiptLinkService.CreditReceiptIdsAsync(_db, companyId, new[] { cash.Id }, default)).Contains(cash.Id),
            CashSaleWithholdingTax: cash.WithholdingTaxAmount,
            InvoiceServesAsReceipt: ReceiptLinkFacts.Invoice(inv).ServesAsReceipt,
            InvoiceHasLivePayments: invLive,
            InvoiceLiveCreditNoteNumber: liveCn,
            InvoiceCreditNoteAllowed: GetValidConversionTargets(inv.DocumentType).Contains(DocumentType.CreditNote),
            PaymentDateProblem: await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, payDate),
            SameTaxIdOtherBranch: sameEntityOtherBranch,
            InvoiceWithholdingTax: inv.WithholdingTaxAmount);
        return new DuplicateSaleSnapshot(cashFacts, invFacts, ctx, cash.BankAccountId, cash.PaymentAccountId, payDate);
    }

    /// <summary>ข้อเท็จจริงของใบหนึ่งใบ — เหตุที่ยกเลิกไม่ได้เพราะภาษี (แยกจากเหตุอื่น: ภาษีติด ⇒ ใบลดหนี้งวดปัจจุบัน · เหตุอื่น ⇒ ผู้ทำบัญชีตัดสิน)</summary>
    private async Task<DuplicateSaleDocFacts> DuplicateSaleDocFactsAsync(Guid companyId, Document doc)
    {
        // ── ภาษี: รายงานภาษีที่ยื่น/ล็อก (ตัวเดียวกับด่านยกเลิก) · e-Tax ถึงกรมสรรพากร (ตัวโหลด/ตัวตัดสินเดียวกับ VoidDocumentAsync) · ภ.พ.30 ของเดือนที่ภาษีขายของใบลงไว้ ──
        string? taxLock = null;
        if (_taxService != null && await _taxService.IsDocumentFilingLockedAsync(companyId, doc.Id))
            taxLock = "รายงานภาษีที่ยื่น/ล็อกแล้ว";
        if (taxLock == null)
        {
            var etax = (await DocumentVoidPreconditions.EffectiveEtaxAsync(_db, companyId, new[] { doc.Id })).GetValueOrDefault(doc.Id);
            taxLock = DocumentVoidPreconditions.DocumentVoidEtaxBlock(etax, doc.DocumentNumber, restore: false);
        }
        if (taxLock == null && doc.VatAmount > 0m && await PostedOutputVatAsync(companyId, doc.Id))
            taxLock = await OutputVatPeriodLockReasonAsync(companyId, doc.OutputVatDueAt ?? doc.DocumentDate);

        // ── เหตุอื่น (ตัวตัดสินเดิมของ VoidDocumentAsync — ตัวนั้นตรวจซ้ำทุกด่านตอนยกเลิกจริงอยู่แล้ว) ──
        string? block = null;
        if ((await DocumentVoidPreconditions.ChildBlocksAsync(_db, companyId, new[] { doc.Id })).TryGetValue(doc.Id, out var child))
            block = child;
        else if (await WhtCertVoidGuard.CheckDocumentAsync(_db, companyId, doc.Id) is string whtFiled)
            block = whtFiled;

        return new DuplicateSaleDocFacts(doc.Id, doc.DocumentNumber, doc.DocumentType, doc.Status, doc.IsDeleted, doc.ReplacedByDocumentId != null,
            doc.ContactId, doc.Currency, doc.ExchangeRate, doc.TotalAmount, doc.PaidAmount, doc.BalanceDue, doc.DocumentDate.Date, taxLock, block);
    }

    /// <summary>ใบนี้ลงภาษีขาย (Cr 21911) ไว้แล้วจริงใน GL — ใบแจ้งหนี้บริการที่ยังพักภาษี (21913) ยังไม่เข้า ภ.พ.30 จึงไม่ติดงวดภาษี (อ่านจาก GL ไม่เดาจากชนิด)</summary>
    private Task<bool> PostedOutputVatAsync(Guid companyId, Guid documentId)
        => (from j in _db.JournalEntries.AsNoTracking()
            join l in _db.JournalEntryLines.AsNoTracking() on j.Id equals l.JournalEntryId
            join a in _db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
            where j.CompanyId == companyId && j.SourceDocumentId == documentId && j.Status == JournalEntryStatus.Posted
                  && j.OriginalEntryId == null && j.ReversedByEntryId == null && !j.IsDeleted && !l.IsDeleted
                  && a.CompanyId == companyId && a.AccountCode == "21911" && l.CreditAmount > 0
            select l.Id).AnyAsync();

    /// <summary>โหนด "เงิน" ของใบในสาย (266-1-40): แถวจัดสรรเงินรอจัดสรรทีหลัง/การย้ายการรับชำระที่ยังมีผล + ใบมัดจำที่ถูกหักเข้า · tenant · อ่านอย่างเดียว</summary>
    private async Task<IReadOnlyList<ChainMoneyNode>> LoadChainMoneyNodesAsync(Guid companyId, IReadOnlyCollection<Guid> documentIds)
    {
        if (documentIds.Count == 0) return Array.Empty<ChainMoneyNode>();
        var ids = documentIds.ToList();
        var allocations = await (
                from a in _db.PaymentAllocations.AsNoTracking()
                join p in _db.Payments.AsNoTracking() on a.PaymentId equals p.Id
                where a.CompanyId == companyId && p.CompanyId == companyId && !a.IsDeleted && !p.IsDeleted && a.ReleasedAt == null
                      && ids.Contains(a.DocumentId) && (a.IsLateAllocation || a.MovedFromAllocationId != null)
                select new { a.Id, a.DocumentId, a.PaymentId, p.PaymentNumber, a.AppliedAt, a.CreatedAt, a.AllocatedAmount, a.IsLateAllocation, a.MovedFromAllocationId })
            .ToListAsync();
        // ใบเดิมของการย้าย — แถวที่ถูกปล่อยถูกซ่อนด้วย query filter (IsDeleted) ⇒ อ่านข้ามตัวกรอง
        var fromIds = allocations.Where(x => x.MovedFromAllocationId != null).Select(x => x.MovedFromAllocationId!.Value).Distinct().ToList();
        var fromNumbers = fromIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await (from a in _db.PaymentAllocations.IgnoreQueryFilters().AsNoTracking()
                     join d in _db.Documents.IgnoreQueryFilters().AsNoTracking() on a.DocumentId equals d.Id
                     where a.CompanyId == companyId && d.CompanyId == companyId && fromIds.Contains(a.Id)
                     select new { a.Id, d.DocumentNumber }).ToDictionaryAsync(x => x.Id, x => x.DocumentNumber);
        var deposits = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.IsDeposit && d.Status != DocumentStatus.Voided
                && d.DepositAppliedToDocumentId != null && ids.Contains(d.DepositAppliedToDocumentId.Value))
            .Select(d => new { d.Id, Target = d.DepositAppliedToDocumentId!.Value, d.DocumentNumber, d.DocumentDate, d.TotalAmount })
            .ToListAsync();

        var facts = allocations.Select(a => new ChainMoneyFact(
                a.MovedFromAllocationId != null ? ChainMoneyKind.MovedPayment : ChainMoneyKind.CreditAllocation,
                a.Id, a.DocumentId, a.PaymentNumber, (a.AppliedAt ?? a.CreatedAt).Date, a.AllocatedAmount,
                PaymentId: a.PaymentId, AllocationId: a.Id,
                FromDocumentNumber: a.MovedFromAllocationId is Guid f && fromNumbers.TryGetValue(f, out var no) ? no : null))
            .Concat(deposits.Select(d => new ChainMoneyFact(ChainMoneyKind.DepositApplied, d.Id, d.Target, d.DocumentNumber, d.DocumentDate.Date,
                d.TotalAmount, OpenDocumentId: d.Id)));
        return DocumentChainMoney.Nodes(facts, documentIds);
    }
}
