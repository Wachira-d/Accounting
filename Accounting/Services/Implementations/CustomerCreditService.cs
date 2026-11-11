using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Journal;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 258 "เงินรอจัดสรร" (DECISIONS 258-1-x · erp-review/2026-11-11/team-D-lump-payment.md §3.4) — ครึ่งหลังของเงินส่วนเกินที่
/// <c>DocumentService.CreateMultiDocPaymentAsync</c> ลงผังพัก 21630 ไว้: รายการ · จัดสรรทีหลัง · ยกเลิกการจัดสรร
/// <para>กติกาทั้งหมดอยู่ที่ตัวตัดสิน pure (<see cref="CustomerCreditAllocation"/> · <see cref="PaymentExcessPlan"/> · <see cref="UnappliedCreditBalance"/>) —
/// คลาสนี้ถือธุรกรรม + ล็อกแถว (ใบก่อน แล้วการชำระ — ลำดับเดียวกับ <c>VoidPaymentAsync</c>/<c>CreateMultiDocPaymentAsync</c>) + ลง JE ผ่าน
/// <see cref="JournalEntryBuilder"/> · ขั้นภาษีหลังจัดสรรเรียกเมธอดเดิมของ <see cref="IDocumentService"/> (ไม่คัดลอกลำดับ)</para>
/// <para>ทุก query มี <c>CompanyId == companyId</c> · ทุกการเขียนลง audit ผ่าน <c>AddChainedAuditLog</c> พร้อม RuleCode + LegalReference</para>
/// </summary>
public class CustomerCreditService : ICustomerCreditService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _documents;
    private readonly IAccountingService _accounting;
    private readonly ILogger<CustomerCreditService> _logger;

    /// <summary>เพดานจำนวนแถวของรายการ (เก่าสุดก่อน — เงินที่ค้างนานต้องเห็นก่อน)</summary>
    public const int ListCap = 500;

    public CustomerCreditService(AccountingDbContext db, IDocumentService documents, IAccountingService accounting,
        ILogger<CustomerCreditService> logger)
    {
        _db = db;
        _documents = documents;
        _accounting = accounting;
        _logger = logger;
    }

    private static DateTime Today() => ThaiDate.CalendarDateUtc(DateTime.UtcNow).Date;

    // ───────────────────────── อ่าน ─────────────────────────

    public async Task<CustomerCreditListResponse> ListAsync(Guid companyId, Guid? contactId, bool includeResolved, CancellationToken ct = default)
    {
        var query = _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.UnappliedAccountId != null);
        if (!includeResolved) query = query.Where(p => p.UnappliedAmount > UnappliedCreditBalance.Tolerance);
        if (contactId is Guid cid) query = query.Where(p => p.ContactId == cid);
        var pays = await query
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.PaymentNumber)
            .Take(ListCap)
            .Select(p => new
            {
                p.Id, p.PaymentNumber, p.ContactId, p.PaymentDate, p.Amount, p.UnappliedAmount,
                p.UnappliedReason, p.UnappliedNote, p.Reference,
            })
            .ToListAsync(ct);

        var payIds = pays.Select(p => p.Id).ToList();
        var contactIds = pays.Where(p => p.ContactId != null).Select(p => p.ContactId!.Value).Distinct().ToList();
        var contactNames = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var allocRows = await (
            from a in _db.PaymentAllocations.AsNoTracking()
            join d in _db.Documents.AsNoTracking() on a.DocumentId equals d.Id
            where a.CompanyId == companyId && d.CompanyId == companyId && !a.IsDeleted && payIds.Contains(a.PaymentId)
            orderby a.CreatedAt
            select new { a.Id, a.PaymentId, a.DocumentId, d.DocumentNumber, a.AllocatedAmount, a.IsLateAllocation, a.AppliedAt })
            .ToListAsync(ct);
        var allocByPayment = allocRows.GroupBy(a => a.PaymentId).ToDictionary(g => g.Key, g => g.ToList());

        var today = Today();
        var items = pays.Select(p => new CustomerCreditRow(
            p.Id, p.PaymentNumber, p.ContactId,
            p.ContactId is Guid c && contactNames.TryGetValue(c, out var name) ? name : null,
            p.PaymentDate, p.Amount, p.UnappliedAmount,
            string.IsNullOrWhiteSpace(p.UnappliedReason) ? nameof(UnappliedReason.Unknown) : p.UnappliedReason!,
            PaymentExcessPlan.LabelOf(p.UnappliedReason), p.UnappliedNote,
            Math.Max(0, (today - p.PaymentDate.Date).Days), p.Reference,
            allocByPayment.TryGetValue(p.Id, out var rows)
                ? rows.Select(a => new CustomerCreditAllocationRow(a.Id, a.DocumentId, a.DocumentNumber, a.AllocatedAmount,
                    a.IsLateAllocation, a.AppliedAt, CanUndo: a.IsLateAllocation)).ToList()
                : new List<CustomerCreditAllocationRow>())).ToList();

        // ยอดรวมทั้งบริษัท/ทั้งลูกค้า (ไม่ใช่แค่หน้าที่ตัดด้วยเพดาน)
        var totalQuery = _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.UnappliedAccountId != null
                && p.UnappliedAmount > UnappliedCreditBalance.Tolerance);
        if (contactId is Guid tc) totalQuery = totalQuery.Where(p => p.ContactId == tc);
        var total = await totalQuery.SumAsync(p => (decimal?)p.UnappliedAmount, ct) ?? 0m;

        // เทียบกับบัญชีแยกประเภทของผัง 21630 (ทั้งบริษัทเท่านั้น — กรองรายลูกค้าเทียบไม่ได้ เพราะ JE ไม่มีผู้ติดต่อ)
        decimal? glBalance = null;
        var glDifference = 0m;
        string? glNote = null;
        if (contactId == null)
        {
            var accountId = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && a.AccountCode == UnappliedReceiptAccount.Code)
                .Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
            if (accountId is Guid accId)
            {
                glBalance = await (
                    from l in _db.JournalEntryLines.AsNoTracking()
                    join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                    where j.CompanyId == companyId && !j.IsDeleted && !l.IsDeleted && l.AccountId == accId
                          && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                    select (decimal?)(l.CreditAmount - l.DebitAmount)).SumAsync(ct) ?? 0m;
                var subledger = await _db.Payments.AsNoTracking()
                    .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.UnappliedAccountId == accId)
                    .SumAsync(p => (decimal?)p.UnappliedAmount, ct) ?? 0m;
                glDifference = Math.Round(glBalance.Value - subledger, 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(glDifference) > UnappliedCreditBalance.Tolerance)
                    glNote = $"ยอดผัง {UnappliedReceiptAccount.Code} ในบัญชีแยกประเภท ({glBalance.Value:N2}) ไม่เท่ากับยอดรวมของรายการเงินรอจัดสรร ({subledger:N2}) "
                        + $"ต่างกัน {glDifference:N2} — ตรวจสมุดรายวันที่ลงผังนี้ด้วยมือ (ผังนี้ควรถูกลงจากการรับชำระ/การจัดสรรเท่านั้น)";
            }
            var legacy = await _db.Payments.AsNoTracking()
                .CountAsync(p => p.CompanyId == companyId && !p.IsDeleted && p.UnappliedAccountId != null
                    && p.UnappliedAmount > UnappliedCreditBalance.Tolerance
                    && (accountId == null || p.UnappliedAccountId != accountId), ct);
            if (legacy > 0)
            {
                var legacyNote = $"มี {legacy} รายการก่อนรอบ 258 ที่เงินส่วนเกินลงผังอื่น (ไม่ใช่ {UnappliedReceiptAccount.Code}) — จัดสรรได้ตามปกติ (ตัดจากผังเดิมที่ลงไว้) แต่ไม่รวมในการเทียบยอดข้างต้น";
                glNote = glNote == null ? legacyNote : glNote + " · " + legacyNote;
            }
        }
        return new CustomerCreditListResponse(items, total, glBalance, glDifference, glNote);
    }

    public async Task<List<CustomerCreditTargetRow>> TargetsAsync(Guid companyId, Guid paymentId, CancellationToken ct = default)
    {
        var payment = await _db.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.CompanyId == companyId && !p.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบการรับชำระ (อาจถูกยกเลิกแล้ว)");
        var credit = await FactsAsync(companyId, payment, ct);
        if (payment.ContactId is not Guid contactId) return new List<CustomerCreditTargetRow>();
        var open = await OpenDocumentsAsync(companyId, contactId, exceptDocumentId: null, ct);
        var today = Today();
        var suggestions = PaymentExcessPlan.SuggestSplit(payment.UnappliedAmount, open.Select(d => (d.Id, d.BalanceDue)))
            .ToDictionary(r => r.DocumentId, r => r.Suggested);
        return open.Select(d =>
        {
            var probe = Math.Min(d.BalanceDue, payment.UnappliedAmount);
            var refusal = CustomerCreditAllocation.Check(credit, TargetFacts(d), probe, today, today);
            return new CustomerCreditTargetRow(d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.DueDate, d.BalanceDue,
                refusal == null && suggestions.TryGetValue(d.Id, out var s) ? s : 0m, refusal?.Message);
        }).ToList();
    }

    public async Task<CustomerCreditForDocumentResponse> ForDocumentAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var empty = new CustomerCreditForDocumentResponse(doc.Id, doc.DocumentNumber, doc.BalanceDue, 0m, 0m, new List<CustomerCreditUsableRow>());
        // ใบที่รับการจัดสรรไม่ได้โดยชนิด/สถานะ/ไม่มียอดค้าง ⇒ ไม่มีปุ่ม (ไม่ใช่ปุ่มที่กดแล้วถูกปฏิเสธ)
        if (!CustomerCreditAllocation.TargetTypes.Contains(doc.DocumentType) || !CustomerCreditAllocation.OpenStatuses.Contains(doc.Status)
            || doc.BalanceDue <= UnappliedCreditBalance.Tolerance)
            return empty;
        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.ContactId == doc.ContactId
                && p.UnappliedAccountId != null && p.UnappliedAmount > UnappliedCreditBalance.Tolerance)
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.PaymentNumber)
            .Take(ListCap)
            .ToListAsync(ct);
        if (payments.Count == 0) return empty;
        var today = Today();
        var target = TargetFacts(doc);
        var remaining = doc.BalanceDue;
        var rows = new List<CustomerCreditUsableRow>();
        foreach (var p in payments)
        {
            var credit = await FactsAsync(companyId, p, ct);
            var refusal = CustomerCreditAllocation.Check(credit, target, Math.Min(p.UnappliedAmount, doc.BalanceDue), today, today);
            var usable = refusal == null ? Math.Min(p.UnappliedAmount, Math.Max(0m, remaining)) : 0m;
            remaining -= usable;
            rows.Add(new CustomerCreditUsableRow(p.Id, p.PaymentNumber, p.PaymentDate, p.UnappliedAmount,
                PaymentExcessPlan.LabelOf(p.UnappliedReason), p.UnappliedNote, usable, refusal?.Message));
        }
        return new CustomerCreditForDocumentResponse(doc.Id, doc.DocumentNumber, doc.BalanceDue,
            payments.Sum(p => p.UnappliedAmount), rows.Sum(r => r.Usable), rows);
    }

    public async Task<PaymentExcessPlanResponse> ExcessPlanAsync(Guid companyId, Guid documentId, decimal amount, decimal fee, CancellationToken ct = default)
    {
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var excess = PaymentExcessPlan.Excess(amount, fee, doc.BalanceDue);
        if (excess <= 0m)
            return new PaymentExcessPlanResponse(doc.Id, doc.BalanceDue, 0m, null, null,
                Array.Empty<PaymentExcessOption>(), new List<PaymentExcessOpenDocument>());
        var vatRegistered = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.IsVatRegistered).FirstOrDefaultAsync(ct);
        // เหตุปฏิเสธเชิงโครงสร้าง (ฝั่งจ่าย · สกุลต่างประเทศ · มีค่าธรรมเนียม) — เหตุผลยังไม่ถูกเลือก ⇒ ส่ง Unknown (ไม่มีวันถูกปฏิเสธด้วยเหตุผลเอง)
        var refusal = PaymentExcessPlan.RefuseExcess(
            receivableSide: CustomerCreditAllocation.TargetTypes.Contains(doc.DocumentType),
            isThb: CustomerCreditAllocation.IsThbDocument(doc.Currency, doc.ExchangeRate),
            hasFee: fee > 0m, vatRegistered: vatRegistered, reason: UnappliedReason.Unknown);
        var others = new List<PaymentExcessOpenDocument>();
        if (refusal == null)
        {
            var open = await OpenDocumentsAsync(companyId, doc.ContactId, exceptDocumentId: doc.Id, ct);
            var suggestions = PaymentExcessPlan.SuggestSplit(excess, open.Select(d => (d.Id, d.BalanceDue)))
                .ToDictionary(r => r.DocumentId, r => r.Suggested);
            others = open.Select(d => new PaymentExcessOpenDocument(d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.DueDate,
                d.BalanceDue, suggestions.TryGetValue(d.Id, out var s) ? s : 0m)).ToList();
        }
        return new PaymentExcessPlanResponse(doc.Id, doc.BalanceDue, excess, PaymentExcessPlan.Headline(excess), refusal,
            refusal == null ? PaymentExcessPlan.ReasonOptions(vatRegistered) : Array.Empty<PaymentExcessOption>(), others);
    }

    /// <summary>ใบขายค้างชำระสกุลบาทของลูกค้า (เก่าสุดก่อน — กำหนดชำระ แล้ววันที่ใบ) — ชุดชนิด/สถานะจาก <see cref="CustomerCreditAllocation"/> ตัวเดียว</summary>
    private async Task<List<Document>> OpenDocumentsAsync(Guid companyId, Guid contactId, Guid? exceptDocumentId, CancellationToken ct)
    {
        var types = CustomerCreditAllocation.TargetTypes.ToList();
        var statuses = CustomerCreditAllocation.OpenStatuses.ToList();
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.ContactId == contactId
                && types.Contains(d.DocumentType) && statuses.Contains(d.Status) && d.BalanceDue > 0.005m
                && (exceptDocumentId == null || d.Id != exceptDocumentId))
            .OrderBy(d => d.DueDate ?? d.DocumentDate).ThenBy(d => d.DocumentDate).ThenBy(d => d.DocumentNumber)
            .Take(ListCap)
            .ToListAsync(ct);
        return docs.Where(d => CustomerCreditAllocation.IsThbDocument(d.Currency, d.ExchangeRate)).ToList();
    }

    private static CustomerCreditTargetFacts TargetFacts(Document d)
        => new(d.CompanyId, d.ContactId, d.DocumentNumber, d.DocumentType, d.Status, d.DocumentDate, d.BalanceDue, d.Currency, d.ExchangeRate);

    /// <summary>ข้อเท็จจริงของเงินก้อนนี้สำหรับตัวตัดสิน — สกุลบาท = ใบแรกของการชำระเป็นบาทและไม่มีอัตรา ณ วันชำระ</summary>
    private async Task<CustomerCreditFacts> FactsAsync(Guid companyId, Payment payment, CancellationToken ct)
    {
        var first = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.Id == payment.DocumentId && d.CompanyId == companyId)
            .Select(d => new { d.Currency, d.ExchangeRate })
            .FirstOrDefaultAsync(ct);
        var isThb = (payment.ExchangeRate == null || payment.ExchangeRate == 1m)
            && first != null && CustomerCreditAllocation.IsThbDocument(first.Currency, first.ExchangeRate);
        return new CustomerCreditFacts(payment.CompanyId, payment.ContactId, payment.PaymentNumber, payment.PaymentDate,
            payment.UnappliedAmount, payment.UnappliedAccountId != null, payment.IsDeleted, payment.SettlementBatchId != null, isThb);
    }

    // ───────────────────────── เขียน ─────────────────────────

    public async Task<CustomerCreditAllocationResult> AllocateAsync(Guid companyId, Guid paymentId, AllocateCustomerCreditRequest request,
        string actor, CancellationToken ct = default)
    {
        var today = Today();
        var applyDate = (request.ApplyDate ?? today).Date;
        var amount = request.Amount;
        Payment? payment = null;
        Document? doc = null;
        var journalReference = "";

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            // ล็อกใบก่อน แล้วจึงล็อกการชำระ (ลำดับเดียวกับ VoidPaymentAsync / CreateMultiDocPaymentAsync — กัน deadlock) · อ่านค่าใหม่ใต้ล็อก
            doc = await _db.Documents
                .FromSqlRaw("""SELECT * FROM "Documents" WHERE "Id" = {0} AND "CompanyId" = {1} FOR UPDATE""", request.DocumentId, companyId)
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("ไม่พบเอกสารปลายทาง");
            await _db.Entry(doc).ReloadAsync(ct);
            payment = await _db.Payments
                .FromSqlRaw("""SELECT * FROM "Payments" WHERE "Id" = {0} AND "CompanyId" = {1} FOR UPDATE""", paymentId, companyId)
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("ไม่พบการรับชำระ (อาจถูกยกเลิกแล้ว)");
            await _db.Entry(payment).ReloadAsync(ct);

            // ── ด่านเดียวของการจัดสรร (ก่อนลง JE) ──
            var credit = await FactsAsync(companyId, payment, ct);
            if (CustomerCreditAllocation.Check(credit, TargetFacts(doc), amount, applyDate, today) is { } refusal)
                throw new BusinessRuleException(refusal.Message, refusal.RuleCode, refusal.StatusCode);
            // งวดบัญชีของวันที่จัดสรรต้องเปิด (ตัวเดียวกับ JournalEntryBuilder.PostAsync — ตรวจก่อนเพื่อให้ข้อความถึงผู้ใช้)
            if (await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, applyDate, ct) is string closed)
                throw new BusinessRuleException(closed, CustomerCreditAllocation.RuleCode, 409);
            // ใบแจ้งหนี้ที่ยังพักภาษีขาย (21913): การจัดสรร = ได้รับชำระ ⇒ ภาษีจะถูกย้ายเข้างวด — วันที่จัดสรรห้ามอยู่ในงวด ภ.พ.30 ที่ยื่น/ล็อกแล้ว
            if (doc.DocumentType == DocumentType.Invoice && doc.OutputVatDueAt == null && doc.VatAmount > 0.005m
                && await _documents.CreditAllocationVatLockReasonAsync(companyId, applyDate) is string vatLocked)
                throw new BusinessRuleException(
                    $"จัดสรรลงวันที่ {applyDate:dd/MM/yyyy} ไม่ได้ — ใบ {doc.DocumentNumber} ยังพักภาษีขายไว้ และวันที่นี้อยู่ใน{vatLocked} · "
                    + "เลือกวันที่จัดสรรในงวดที่ยังไม่ปิด/ยังไม่ยื่น", LateReceiptTaxPoint.RuleCode, 409);
            // ลูกหนี้ที่ตัด = ผังที่ใบนี้ตั้งไว้จริง (JE ตอนออกใบ) — ไม่เดาผังตั้งต้น
            var arAccountId = await OriginalArApAccount.LoadAsync(_db, companyId, doc.Id, ArApSide.Receivable, ct)
                ?? throw new BusinessRuleException(
                    $"ไม่พบรายการบัญชีตั้งลูกหนี้ของใบ {doc.DocumentNumber} (ใบนี้ไม่มีสมุดรายวันตอนออกใบ) — จัดสรรเงินรอจัดสรรให้ใบนี้ไม่ได้ · "
                    + "ให้ผู้ทำบัญชีตรวจการลงบัญชีของใบนี้ก่อน", CustomerCreditAllocation.RuleCode, 409);

            var sequence = await _db.PaymentAllocations.IgnoreQueryFilters()
                .CountAsync(a => a.PaymentId == payment.Id && a.CompanyId == companyId && a.IsLateAllocation, ct) + 1;
            journalReference = CustomerCreditAllocation.JournalReference(payment.PaymentNumber, sequence);
            var je = await JournalEntryBuilder.For(_db, companyId, applyDate)
                .Description($"จัดสรรเงินรอจัดสรร {payment.PaymentNumber} ให้ {doc.DocumentNumber}")
                .Reference(journalReference)
                .SourceDocument(doc.Id)
                .Project(doc.ProjectId)
                .Debit(payment.UnappliedAccountId!.Value, amount, $"ตัดเงินรับจากลูกค้ารอจัดสรร - {payment.PaymentNumber}")
                .Credit(arAccountId, amount, $"ตัดลูกหนี้ - {doc.DocumentNumber}")
                .PostAsync(actor, ct);

            var allocation = new PaymentAllocation
            {
                CompanyId = companyId,
                PaymentId = payment.Id,
                DocumentId = doc.Id,
                AllocatedAmount = amount,
                WithholdingTaxAmount = 0m,
                Note = "จัดสรรจากเงินรอจัดสรร",
                IsLateAllocation = true,
                AppliedAt = applyDate,
                JournalEntryId = je.Id,
                CreatedBy = actor,
            };
            // ใบที่ออกหลังเงินเข้า: วันที่รับชำระของใบ = วันรับเงินจริง (ม.78/1 — ผู้ใช้ไม่ต้องรู้/กรอกเอง) · ไม่ทับค่าที่ผู้ใช้กรอกไว้
            if (doc.PaymentDate == null && payment.PaymentDate.Date <= doc.DocumentDate.Date)
            {
                doc.PaymentDate = payment.PaymentDate.Date;
                allocation.StampedDocumentPaymentDate = true;
            }
            _db.PaymentAllocations.Add(allocation);

            doc.PaidAmount += amount;
            var settle = DocumentSettlementState.Apply(ForeignServiceVat.PayeeAmount(doc), doc.PaidAmount, doc.Status);
            doc.BalanceDue = settle.BalanceDue;
            doc.Status = settle.Status;
            if (doc.Status == DocumentStatus.Paid)
            {
                doc.AgingDays = null;
                doc.AgingLastEvaluatedAt = DateTime.UtcNow;
            }
            doc.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await RecomputeUnappliedAsync(companyId, payment, ct);
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = Guid.TryParse(actor, out var actorId) ? actorId : null,
                Action = AuditAction.Create,
                EntityType = "PaymentAllocation",
                EntityId = allocation.Id.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    RuleCode = CustomerCreditAllocation.RuleCode,
                    LegalReference = CustomerCreditAllocation.LegalReference,
                    payment.PaymentNumber,
                    doc.DocumentNumber,
                    Amount = amount,
                    ApplyDate = applyDate,
                    ReceiptDate = payment.PaymentDate.Date,
                    JournalReference = journalReference,
                    RemainingUnapplied = payment.UnappliedAmount,
                    DocumentBalanceDue = doc.BalanceDue,
                    allocation.StampedDocumentPaymentDate,
                }),
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        // ขั้นภาษีหลังจัดสรร (หลัง commit — การจัดสรรที่สำเร็จแล้วต้องไม่ถูกย้อนเพราะขั้นนี้ · ล้ม = ป้ายบนตัวเอกสาร ตามเส้นรับชำระเดิม)
        var notices = await _documents.AfterCreditAllocatedAsync(companyId, doc!.Id, payment!.PaymentDate, applyDate, journalReference, actor);
        _logger.LogInformation("จัดสรรเงินรอจัดสรร {Pay} → {Doc} {Amount:N2} (คงเหลือ {Left:N2})",
            payment.PaymentNumber, doc.DocumentNumber, amount, payment.UnappliedAmount);
        return new CustomerCreditAllocationResult(payment.Id, payment.PaymentNumber, doc.Id, doc.DocumentNumber, amount,
            payment.UnappliedAmount, doc.BalanceDue, doc.Status, notices);
    }

    public async Task<CustomerCreditAllocationResult> UndoAllocationAsync(Guid companyId, Guid paymentId, Guid allocationId, string actor,
        CancellationToken ct = default)
    {
        var today = Today();
        Payment? payment = null;
        Document? doc = null;
        var undone = 0m;
        string? vatNotice = null;

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var allocation = await _db.PaymentAllocations
                .FirstOrDefaultAsync(a => a.Id == allocationId && a.PaymentId == paymentId && a.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบการจัดสรร (อาจถูกยกเลิกไปแล้ว)");
            doc = await _db.Documents
                .FromSqlRaw("""SELECT * FROM "Documents" WHERE "Id" = {0} AND "CompanyId" = {1} FOR UPDATE""", allocation.DocumentId, companyId)
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("ไม่พบเอกสารของการจัดสรร");
            await _db.Entry(doc).ReloadAsync(ct);
            payment = await _db.Payments
                .FromSqlRaw("""SELECT * FROM "Payments" WHERE "Id" = {0} AND "CompanyId" = {1} FOR UPDATE""", paymentId, companyId)
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("ไม่พบการรับชำระ (อาจถูกยกเลิกแล้ว)");
            await _db.Entry(payment).ReloadAsync(ct);
            await _db.Entry(allocation).ReloadAsync(ct);   // ใต้ล็อก: คำขอซ้อนที่ยกเลิกไปแล้วต้องเห็น
            if (allocation.IsDeleted)
                throw new BusinessRuleException("การจัดสรรนี้ถูกยกเลิกไปแล้ว", CustomerCreditAllocation.RuleCode, 409);
            if (!allocation.IsLateAllocation)
                throw new BusinessRuleException(
                    $"การจัดสรรให้ {doc.DocumentNumber} เกิดพร้อมการรับเงิน {payment.PaymentNumber} — ยกเลิกเฉพาะส่วนนี้ไม่ได้ · "
                    + "ยกเลิกการรับชำระทั้งรายการแล้วบันทึกใหม่", CustomerCreditAllocation.RuleCode, 409);
            // ใบกำกับ/ใบเสร็จของใบนี้ที่ยืนยันทาง e-Tax ว่า "ใบเดิมยังใช้ได้" อาศัยยอดรับชำระครอบ — ยกเลิกการจัดสรรทำให้ยอดครอบหาย ⇒ ให้จัดการใบนั้นก่อน
            var keptReceipt = await _db.Documents.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.RelatedDocumentId == doc.Id && !r.IsDeleted
                    && r.Status != DocumentStatus.Voided && r.EtaxKeptOriginalAt != null)
                .Select(r => r.DocumentNumber).FirstOrDefaultAsync(ct);
            if (keptReceipt != null)
                throw new BusinessRuleException(
                    $"ยกเลิกการจัดสรรไม่ได้ — ใบ {keptReceipt} ของ {doc.DocumentNumber} ยืนยันทาง e-Tax ว่าใบกำกับเดิมยังใช้ได้โดยอาศัยยอดรับชำระนี้ · "
                    + "ยกเลิก/ออกใหม่ใบนั้นก่อน", CustomerCreditAllocation.RuleCode, 409);
            if (await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, today, ct) is string closed)
                throw new BusinessRuleException(closed, CustomerCreditAllocation.RuleCode, 409);

            // กลับ JE ของการจัดสรรนี้ทั้งชุด (ตัดลูกหนี้ + ภาษีถูกหัก ณ ที่จ่ายตอนปิดยอด) — อ้างเลขเดียวกัน
            var reference = allocation.JournalEntryId is Guid jeId
                ? await _db.JournalEntries.AsNoTracking()
                    .Where(j => j.Id == jeId && j.CompanyId == companyId).Select(j => j.Reference).FirstOrDefaultAsync(ct)
                : null;
            if (string.IsNullOrEmpty(reference))
                throw new BusinessRuleException(
                    "ไม่พบสมุดรายวันของการจัดสรรนี้ — ยกเลิกอัตโนมัติไม่ได้ ให้ผู้ทำบัญชีตรวจรายการ", CustomerCreditAllocation.RuleCode, 409);
            var journalIds = await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && j.Reference == reference
                    && j.Status == JournalEntryStatus.Posted && j.OriginalEntryId == null && j.ReversedByEntryId == null)
                .Select(j => j.Id)
                .ToListAsync(ct);
            foreach (var id in journalIds)
                await _accounting.ReverseJournalEntryAsync(companyId, id, reversalDate: today,
                    description: $"ยกเลิกการจัดสรรเงินรอจัดสรร - {reference}", systemTriggered: true);

            undone = allocation.AllocatedAmount;
            doc.PaidAmount = Math.Max(0m, doc.PaidAmount - undone);
            var settle = DocumentSettlementState.Apply(ForeignServiceVat.PayeeAmount(doc), doc.PaidAmount, doc.Status);
            doc.BalanceDue = settle.BalanceDue;
            doc.Status = settle.Status;
            if (allocation.StampedDocumentPaymentDate) doc.PaymentDate = null;
            doc.UpdatedAt = DateTime.UtcNow;
            allocation.IsDeleted = true;
            allocation.UpdatedAt = DateTime.UtcNow;
            allocation.UpdatedBy = actor;
            await _db.SaveChangesAsync(ct);

            await RecomputeUnappliedAsync(companyId, payment, ct);
            // ถอยภาษีขายถึงกำหนดถ้าใบไม่เหลือเงินรับ (ตัวเดียวกับยกเลิกการรับชำระ) — งวดปิด/ยื่นแล้ว = throw ⇒ ทั้งธุรกรรม rollback
            vatNotice = await _documents.UndoCreditAllocationTaxEffectsAsync(companyId, doc.Id, payment.PaymentNumber);
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = Guid.TryParse(actor, out var actorId) ? actorId : null,
                Action = AuditAction.Delete,
                EntityType = "PaymentAllocation",
                EntityId = allocation.Id.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    RuleCode = CustomerCreditAllocation.RuleCode + "-UNDO",
                    LegalReference = CustomerCreditAllocation.LegalReference,
                    payment.PaymentNumber,
                    doc.DocumentNumber,
                    Amount = undone,
                    JournalReference = reference,
                    RemainingUnapplied = payment.UnappliedAmount,
                    DocumentBalanceDue = doc.BalanceDue,
                    VatNotice = vatNotice,
                }),
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        await _documents.SyncWhtCreditAfterCreditUndoAsync(companyId, doc!.Id);
        _logger.LogInformation("ยกเลิกการจัดสรรเงินรอจัดสรร {Pay} → {Doc} {Amount:N2}", payment!.PaymentNumber, doc.DocumentNumber, undone);
        return new CustomerCreditAllocationResult(payment.Id, payment.PaymentNumber, doc.Id, doc.DocumentNumber, undone,
            payment.UnappliedAmount, doc.BalanceDue, doc.Status,
            vatNotice == null ? Array.Empty<string>() : new[] { vatNotice });
    }

    /// <summary>คิดยอดคงเหลือใหม่จากแถวจัดสรรที่ยังมีผล (สูตรเดียว <see cref="UnappliedCreditBalance.Compute"/> — ไม่บวก/ลบสะสม)</summary>
    private async Task RecomputeUnappliedAsync(Guid companyId, Payment payment, CancellationToken ct)
    {
        var live = await _db.PaymentAllocations.AsNoTracking()
            .Where(a => a.PaymentId == payment.Id && a.CompanyId == companyId && !a.IsDeleted)
            .SumAsync(a => (decimal?)a.AllocatedAmount, ct) ?? 0m;
        payment.UnappliedAmount = UnappliedCreditBalance.Compute(payment.Amount, live);
        payment.UnappliedResolvedAt = UnappliedCreditBalance.HasBalance(payment.UnappliedAmount) ? null : DateTime.UtcNow;
        payment.UpdatedAt = DateTime.UtcNow;
    }
}
