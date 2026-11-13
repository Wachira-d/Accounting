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
        // ฝ่ายค้าน H3 (266-1-41): เงินลงวันที่ใบขายสด (วันรับเงินจริง) · จัดสรรลงวันที่ไม่ก่อนใบแจ้งหนี้ · ฝ่ายค้าน M3: ตรวจทั้งงวดบัญชีและงวด ภ.พ.30 ที่ภาษีจะลง
        var allocDate = DuplicateSalePolicy.AllocationDate(cash.DocumentDate, inv.DocumentDate);
        var invPostedVat = inv.VatAmount > 0m && await PostedOutputVatAsync(companyId, inv.Id);
        var invPendingVat = inv.DocumentType == DocumentType.Invoice && inv.VatAmount > 0.005m && inv.OutputVatDueAt == null && !invPostedVat;
        var dateProblem = await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, cash.DocumentDate.Date)
            ?? await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, allocDate);
        // ขั้นลงเงินต้องมีผังพัก 21630 (หรือกลุ่ม 216 ให้สร้าง) — ไม่มี ⇒ บอกก่อนยกเลิกใบใด (ไม่ให้เกิดงานค้างที่รู้ล่วงหน้าว่าทำต่อไม่ได้)
        if (dateProblem == null && !await UnappliedReceiptAccount.IsAvailableAsync(_db, companyId))
            dateProblem = UnappliedReceiptAccount.MissingMessage;
        if (dateProblem == null && invPendingVat)
            dateProblem = await OutputVatPeriodLockReasonAsync(companyId, cash.DocumentDate.Date)
                ?? await CreditAllocationVatLockReasonAsync(companyId, allocDate);
        var existingCredit = await DuplicateSaleCreditPaymentAsync(companyId, cash.Id);
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
            PaymentDateProblem: dateProblem,
            SameTaxIdOtherBranch: sameEntityOtherBranch,
            InvoiceWithholdingTax: inv.WithholdingTaxAmount,
            ResumePending: inv.DuplicateSalePendingFromDocumentId == cash.Id,
            InvoiceGoodsVatNotTaxInvoice: inv.DocumentType == DocumentType.Invoice && invPostedVat);
        return new DuplicateSaleSnapshot(cashFacts, invFacts, ctx, cash.BankAccountId, cash.PaymentAccountId, allocDate,
            existingCredit?.Id, existingCredit?.UnappliedAmount ?? 0m);
    }

    /// <summary>เงินรอจัดสรรที่ตัวช่วยใบซ้ำลงไว้จากใบขายสดนี้ (ยังมีผล) — <c>Payment.DocumentId</c> = ใบขายสด + มีผังพัก · null = ยังไม่ลง</summary>
    private Task<Payment?> DuplicateSaleCreditPaymentAsync(Guid companyId, Guid cashSaleId)
        => _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.DocumentId == cashSaleId && p.UnappliedAccountId != null)
            .OrderBy(p => p.CreatedAt).FirstOrDefaultAsync();

    /// <inheritdoc />
    public async Task<DuplicateSaleCreditResult> RecordDuplicateSaleCreditAsync(Guid companyId, Guid cashSaleId, Guid invoiceId, string actor)
    {
        DuplicateSaleCreditResult? result = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            // ล็อกใบขายสดก่อนอ่าน — กันลงเงินซ้ำจากสองคำขอ (ทำต่อ/กดซ้ำ) · ทำซ้ำได้: ลงไว้แล้ว = คืนรายการเดิม
            var cash = await _db.Documents
                .FromSqlRaw("SELECT * FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", cashSaleId, companyId)
                .IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync()
                ?? throw new KeyNotFoundException("ไม่พบใบขายสด");
            if (cash.Status != DocumentStatus.Voided)
                throw new BusinessRuleException($"ใบ {cash.DocumentNumber} ยังไม่ถูกยกเลิก — ลงเงินของใบนี้เป็นเงินรอจัดสรรไม่ได้ (เงินจะนับสองครั้ง)",
                    DuplicateSalePolicy.StaleCode, 409);
            var existing = await DuplicateSaleCreditPaymentAsync(companyId, cash.Id);
            if (existing != null)
            {
                result = new DuplicateSaleCreditResult(existing.Id, existing.PaymentNumber, existing.UnappliedAmount, Created: false);
                await tx.RollbackAsync();
                return;
            }
            var channel = DocumentMoneyChannel.ForPayment(null, null, cash.BankAccountId, cash.PaymentAccountId);
            // ฝ่ายค้าน L4: วิธีชำระของการรับเงินเดิม (ใบกำกับขายเงินสดที่มีรายการรับชำระ) — ไม่มี = เดาจากช่องทางเงิน (ใบเสร็จไม่มีช่องวิธีชำระ)
            var method = await _db.Payments.AsNoTracking().IgnoreQueryFilters()
                .Where(p => p.CompanyId == companyId && p.DocumentId == cash.Id && p.UnappliedAccountId == null)
                .OrderBy(p => p.CreatedAt).Select(p => (PaymentMethod?)p.PaymentMethod).FirstOrDefaultAsync()
                ?? (channel.BankAccountId != null ? PaymentMethod.BankTransfer : PaymentMethod.Cash);
            var invNumber = await _db.Documents.AsNoTracking().Where(d => d.Id == invoiceId && d.CompanyId == companyId)
                .Select(d => d.DocumentNumber).FirstOrDefaultAsync() ?? "";
            var payment = new Payment
            {
                CompanyId = companyId,
                PaymentNumber = await NextPaymentNumberAsync(companyId),
                // ใบของเงินก้อนนี้ = ใบขายสดที่ยกเลิกแล้ว (ที่มาของเงิน) — ไม่ชี้ใบแจ้งหนี้ (ผู้อ่าน "การชำระของใบ" จะนับซ้ำกับแถวจัดสรร)
                DocumentId = cash.Id,
                ContactId = cash.ContactId,
                PaymentDate = cash.DocumentDate.Date,   // วันรับเงินจริง (ฝ่ายค้าน H3)
                Amount = cash.TotalAmount,
                PaymentMethod = method,
                Reference = cash.DocumentNumber,
                BankAccountId = channel.BankAccountId,
                OverridePaymentAccountId = channel.PaymentAccountId,
                WithholdingTaxAmount = 0m,
                Notes = DuplicateSalePolicy.CreditPaymentNote(cash.DocumentNumber, invNumber),
                CreatedBy = actor,
            };
            _db.Payments.Add(payment);
            await _db.SaveChangesAsync();
            await PostUnappliedCreditAsync(companyId, payment, cash.TotalAmount, UnappliedReason.UninvoicedSupply,
                DuplicateSalePolicy.CreditPaymentNote(cash.DocumentNumber, invNumber), "เงินของใบขายสดที่ยกเลิกเพราะซ้ำ (รอจัดสรร)", actor);
            if (payment.BankAccountId is Guid bankId)
                await _db.BankAccounts.Where(b => b.Id == bankId && b.CompanyId == companyId)
                    .ExecuteUpdateAsync(x => x.SetProperty(b => b.CurrentBalance, b => b.CurrentBalance + cash.TotalAmount));
            await tx.CommitAsync();
            result = new DuplicateSaleCreditResult(payment.Id, payment.PaymentNumber, payment.UnappliedAmount, Created: true);
        });
        return result!;
    }

    /// <inheritdoc />
    public async Task SetDuplicateSalePendingAsync(Guid companyId, Guid invoiceId, Guid? cashSaleId)
        => await _db.Documents.Where(d => d.Id == invoiceId && d.CompanyId == companyId)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.DuplicateSalePendingFromDocumentId, cashSaleId));

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
