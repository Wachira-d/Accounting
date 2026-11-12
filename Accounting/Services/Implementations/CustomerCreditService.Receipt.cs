using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 265 (คำตัดสิน 265-1-1…6) — <b>ออกใบเสร็จรับเงิน/ใบกำกับภาษีจากเงินรอจัดสรร โดยไม่ต้องมีใบแจ้งหนี้</b>
/// <para>เคสเจ้าของ: ออก INV-001 5,000 (เดือน 1) ลูกค้าโอน 10,000 ปิดเดือน 1 + 2 — ส่วนเดือน 2 ไม่ต้องสร้างใบแจ้งหนี้เพิ่ม: ผู้ใช้กรอกรายการ
/// ("ค่าบริการเดือน ต.ค." · จำนวน · ราคา · VAT ต่อบรรทัด · หรือเลือกรายการประจำ) แล้วระบบออกใบเสร็จขายเงินสดที่ขาเงินเข้า = ผังพักที่รับไว้จริง</para>
/// <para>ลำดับ (แบบเดียวกับ "ย้ายเป็นเงินมัดจำ" ที่ผ่านฝ่ายค้านรอบ 259 H2/M1/M2/L4): (1) ตรวจบรรทัด/รายการประจำ (<see cref="CreditReceiptPlan"/>) ก่อนเขียนอะไร →
/// (2) จองยอดใต้ล็อกแถวการชำระ (<c>ToReceipt</c> ตัดยอดทันที · <see cref="CustomerCreditMovementRules.Check"/> · งวดบัญชีของวันที่บนใบ) →
/// (3) สร้าง + อนุมัติใบ (<c>DocumentService</c> เส้นเดิม: เลขที่ตอนอนุมัติ §86/4 · รูปแบบกระดาษ <see cref="ReceiptFormRule"/> · JE Dr 21630 / Cr รายได้ + ภาษีขาย ·
/// ภาษีถูกหักต่อบรรทัด Dr 11910) — ยอดสุทธิไม่ตรงยอดที่ใช้ = ลบร่าง + ปล่อยการจอง + 409 · ล้มหลังอนุมัติ = ห้ามปล่อย ผูกใบ + บอกผู้ใช้ →
/// (4) งวดภาษีตัดสินก่อนจอง (<c>DecideCreditReceiptTaxPointAsync</c>) แล้วประทับร่างก่อนอนุมัติ (265-1-8) · ยกเลิกใบที่หน้าเอกสาร ⇒ <see cref="CustomerCreditLedger.ReleaseForVoidedDocumentAsync"/> คืนเงิน</para>
/// </summary>
public partial class CustomerCreditService
{
    private async Task<(bool VatRegistered, decimal VatRate)> CompanyVatAsync(Guid companyId, CancellationToken ct)
    {
        var vatRegistered = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.IsVatRegistered).FirstOrDefaultAsync(ct);
        var rate = vatRegistered
            ? await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId).Select(s => (decimal?)s.DefaultVatRate).FirstOrDefaultAsync(ct) ?? 7m
            : 0m;
        return (vatRegistered, rate);
    }

    /// <summary>ใบที่เงินก้อนนี้ชำระไปแล้ว (พร้อมรับเงิน/ทีหลัง) — พิมพ์อ้างบนใบเสร็จ (คำตัดสิน 265-1-2)</summary>
    private async Task<List<string>> SettledDocumentNumbersAsync(Guid companyId, Guid paymentId, CancellationToken ct)
        => await (from a in _db.PaymentAllocations.AsNoTracking()
                  join d in _db.Documents.AsNoTracking() on a.DocumentId equals d.Id
                  where a.CompanyId == companyId && d.CompanyId == companyId && a.PaymentId == paymentId && !a.IsDeleted
                  orderby d.DocumentNumber
                  select d.DocumentNumber).Distinct().ToListAsync(ct);

    private async Task<(CreditReceiptRecurringFacts Facts, RecurringTransaction? Row, string? Key)> RecurringFactsAsync(Guid companyId, Guid recurringId,
        CancellationToken ct)
    {
        var r = await _db.RecurringTransactions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == recurringId && x.CompanyId == companyId && !x.IsDeleted, ct);
        if (r == null) return (new CreditReceiptRecurringFacts(false, null, null, null, null, null, null), null, null);
        var key = RecurringRunKey.ForPeriod(r.Id, r.NextRunDate);
        var existing = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.CompanyId == companyId && d.RecurringRunKey == key && !d.IsDeleted)
            .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct);
        return (new CreditReceiptRecurringFacts(true, r.ContactId, r.Status, r.TemplateType, r.DocumentType, r.NextRunDate, existing), r, key);
    }

    public async Task<CustomerCreditReceiptOptions> ReceiptOptionsAsync(Guid companyId, Guid paymentId, CancellationToken ct = default)
    {
        var p = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == paymentId && x.CompanyId == companyId && !x.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบการรับชำระ (อาจถูกยกเลิกแล้ว)");
        var today = Today();
        var facts = await AvailableFactsAsync(companyId, p, null, ct);
        var (vatRegistered, rate) = await CompanyVatAsync(companyId, ct);
        var paperDate = CreditReceiptPlan.PaperDate(p.UnappliedReason, p.PaymentDate, today);
        var note = LateReceiptTaxPoint.ReceiptDrivesTaxPoint(p.UnappliedReason)
            ? $"ใบลงวันที่รับเงิน {paperDate:dd/MM/yyyy} (เงิน “ค้างออกใบ” — ใบรับลงวันที่ที่รับเงิน ม.105)"
            : $"ใบลงวันที่วันนี้ {paperDate:dd/MM/yyyy} (เงินเหตุผล “{PaymentExcessPlan.LabelOf(p.UnappliedReason)}” — วันที่ตกลงนำมาเป็นค่าสินค้า/บริการ)";
        var blocked = CustomerCreditMovementRules.Check(PaymentCreditMovementKind.ToReceipt, facts, facts.UnappliedAmount,
            Math.Max(facts.UnappliedAmount, 0.01m), paperDate, today)?.Message;
        var recurring = new List<CustomerCreditRecurringOption>();
        if (p.ContactId is Guid contactId)
        {
            var rows = await _db.RecurringTransactions.AsNoTracking()
                .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.ContactId == contactId && r.Status == RecurringStatus.Active
                    && r.TemplateType == "Document")
                .OrderBy(r => r.NextRunDate).Take(20).ToListAsync(ct);
            foreach (var r in rows)
            {
                var (rf, _, _) = await RecurringFactsAsync(companyId, r.Id, ct);
                var summary = RecurringTemplate.Summary(r.TemplateData);
                // ฝ่ายค้าน M3: ป้ายบอกชัดว่าเป็น "งวดที่กำลังจะถึง" (NextRunDate) — งวดที่รอบประทับผ่านไปแล้วไม่ถูกเสนอ
                var label = CreditReceiptPlan.PeriodLabel(r.Name, r.NextRunDate);
                recurring.Add(new CustomerCreditRecurringOption(r.Id, r.Name, r.NextRunDate, label,
                    summary.Total > 0m ? summary.Total : null,
                    (summary.FirstDescription ?? r.Name) + " " + CreditReceiptPlan.PeriodText(r.NextRunDate),
                    CreditReceiptPlan.RecurringProblem(rf, p.ContactId)));
            }
        }
        return new CustomerCreditReceiptOptions(p.Id, p.PaymentNumber, facts.UnappliedAmount, paperDate, note, vatRegistered,
            vatRegistered ? new List<decimal> { rate, 0m } : new List<decimal> { 0m }, rate,
            await SettledDocumentNumbersAsync(companyId, p.Id, ct), recurring, blocked);
    }

    public async Task<CustomerCreditMovementResult> IssueReceiptAsync(Guid companyId, Guid paymentId, CustomerCreditIssueReceiptRequest request, string actor,
        CancellationToken ct = default)
    {
        var kind = PaymentCreditMovementKind.ToReceipt;
        const string rule = CustomerCreditMovementRules.ToReceiptRuleCode;
        if (await ExistingByKeyAsync(companyId, request.IdempotencyKey, paymentId, kind, ct) is { } dup) return dup;
        var today = Today();
        var amount = request.Amount;
        var (vatRegistered, vatRate) = await CompanyVatAsync(companyId, ct);
        var lineFacts = (request.Lines ?? new List<CustomerCreditReceiptLineRequest>())
            .Select(l => new CreditReceiptLineFact(l.Description, l.Quantity, l.UnitPrice, l.VatRate, l.WithholdingTaxRate)).ToList();
        if (CreditReceiptPlan.LinesProblem(lineFacts, vatRegistered, vatRate) is string lineProblem)
            throw new BusinessRuleException(lineProblem, rule, 400);
        var pricesIncludeVat = request.PricesIncludeVat ?? true;
        var docLines = request.Lines!.Select(l => new DocumentLineRequest(
            Description: l.Description.Trim(), Quantity: l.Quantity, Unit: string.IsNullOrWhiteSpace(l.Unit) ? "รายการ" : l.Unit.Trim(),
            UnitPrice: l.UnitPrice, DiscountPercent: 0m, VatRate: vatRegistered ? l.VatRate : 0m, WithholdingTaxRate: l.WithholdingTaxRate,
            AccountId: l.AccountId, ProductCode: string.IsNullOrWhiteSpace(l.ProductCode) ? null : l.ProductCode.Trim())).ToList();
        // ฝ่ายค้าน L1: ยอดสุทธิด้วยสูตรเดียวกับ CreateDocumentAsync (DocumentService.PreviewTotals) ก่อนจอง/สร้างร่าง — ไม่ตรง = ปฏิเสธโดยไม่มีร่าง/webhook
        var preview = DocumentService.PreviewTotals(docLines, pricesIncludeVat, 0m);
        if (CreditReceiptPlan.TotalMismatch(preview.Total, amount) is string earlyMismatch)
            throw new BusinessRuleException(earlyMismatch, rule, 409);

        // ฝ่ายค้าน L3: ด่านอนุมัติของบริษัท (วงเงิน/แบ่งแยกหน้าที่) ตัวตัดสินเดียวกับตอนอนุมัติ — ตรวจก่อนจองแล้วบอกทางที่มีอยู่จริง
        var approvalSettings = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => new { s.RequireApprovalForDocuments, s.ApprovalThresholdAmount, s.SodBlockSelfApproval }).FirstOrDefaultAsync(ct);
        if (approvalSettings != null && CreditReceiptPlan.ApprovalControlProblem(
                ApprovalControlPolicy.NeedsSignatureFlow(approvalSettings.RequireApprovalForDocuments, approvalSettings.ApprovalThresholdAmount, preview.Total),
                ApprovalControlPolicy.SelfApprovalBlocked(approvalSettings.SodBlockSelfApproval, actor, actor)) is string approvalProblem)
            throw new BusinessRuleException(approvalProblem, rule, 409);

        var probe = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId && p.CompanyId == companyId && !p.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบการรับชำระ (อาจถูกยกเลิกแล้ว)");
        var drivesDate = LateReceiptTaxPoint.ReceiptDrivesTaxPoint(probe.UnappliedReason);
        var paperDate = CreditReceiptPlan.PaperDate(probe.UnappliedReason, probe.PaymentDate, today);
        // เงินเหตุผลอื่น (ลงวันที่วันนี้) + วันนี้อยู่ในงวด ภ.พ.30 ที่ยื่น/ล็อก ⇒ ปฏิเสธ (ด่านเดียวกับย้ายเป็นมัดจำ)
        if (vatRegistered && !drivesDate && await _documents.CreditAllocationVatLockReasonAsync(companyId, paperDate) is string vatLocked)
            throw new BusinessRuleException($"ออกใบเสร็จลงวันที่วันนี้ไม่ได้ — วันนี้อยู่ใน{vatLocked}", rule, 409);

        // ฝ่ายค้าน H1/M2 (คำตัดสิน 265-1-8): ตัดสินงวดภาษี "ก่อนจอง" — งวดรับเงินยื่น/ปิด/เลยกำหนด ⇒ ใบพักภาษี 21913 ที่วันที่ใบ แล้วย้ายเข้า 21911 ที่วันที่ถึงกำหนด
        // ในธุรกรรมอนุมัติ (Document.CreditReceiptVatDueAt) ⇒ เดือนใน GL = เดือน ภ.พ.30 · ไม่มีขั้นหลัง commit
        var decision = await _documents.DecideCreditReceiptTaxPointAsync(companyId, probe.UnappliedReason, probe.PaymentDate);
        var vatDueAt = CreditReceiptPlan.ReportingTaxPoint(decision, paperDate, preview.Vat);
        if (vatDueAt is DateTime dueCheck && await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, dueCheck, ct) is string dueClosed)
            throw new BusinessRuleException($"ย้ายภาษีขายของใบนี้เข้างวด {dueCheck:MM/yyyy} ไม่ได้ — {dueClosed}", rule, 409);
        var draftNotes = new List<string>();
        if (vatDueAt is DateTime lateDue) draftNotes.Add(CreditReceiptPlan.LateVatNote(paperDate, lateDue));
        if (CreditReceiptPlan.IssuedLateNote(paperDate, today, preview.Vat) is string issuedLate) draftNotes.Add(issuedLate);

        RecurringRunOrigin? origin = null;
        string? periodLabel = null;
        if (request.RecurringTransactionId is Guid rid && rid != Guid.Empty)
        {
            var (rf, row, key) = await RecurringFactsAsync(companyId, rid, ct);
            if (CreditReceiptPlan.RecurringProblem(rf, probe.ContactId) is string recurringProblem)
                throw new BusinessRuleException(recurringProblem, rule, 409);
            origin = new RecurringRunOrigin(rid, key);
            periodLabel = CreditReceiptPlan.PeriodLabel(row!.Name, row.NextRunDate);
        }

        // (1) จองยอดใต้ล็อก — ขาออกยังไม่มีใบปลายทาง ตัดยอดทันที (กันใช้ซ้อนระหว่างออกใบ)
        Payment? payment = null;
        PaymentCreditMovement? movement = null;
        try
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                payment = await _db.Payments
                    .FromSqlRaw("""SELECT * FROM "Payments" WHERE "Id" = {0} AND "CompanyId" = {1} FOR UPDATE""", paymentId, companyId)
                    .FirstOrDefaultAsync(ct)
                    ?? throw new KeyNotFoundException("ไม่พบการรับชำระ (อาจถูกยกเลิกแล้ว)");
                await _db.Entry(payment).ReloadAsync(ct);
                if (CustomerCreditAllocation.StaleCheck(request.ExpectedUnappliedBefore, payment.UnappliedAmount, payment.PaymentNumber) is { } stale)
                    throw new BusinessRuleException(stale.Message, stale.RuleCode, stale.StatusCode);
                var credit = await AvailableFactsAsync(companyId, payment, null, ct);
                if (CustomerCreditMovementRules.Check(kind, credit, credit.UnappliedAmount, amount, paperDate, today) is { } refusal)
                    throw new BusinessRuleException(refusal.Message, refusal.RuleCode, refusal.StatusCode);
                if (await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, paperDate, ct) is string closed)
                    throw new BusinessRuleException(CreditReceiptPlan.ClosedPeriodMessage(closed, paperDate, drivesDate), rule, 409);
                movement = new PaymentCreditMovement
                {
                    CompanyId = companyId,
                    PaymentId = payment.Id,
                    Kind = kind.ToString(),
                    Amount = amount,
                    MovedAt = paperDate,
                    Note = periodLabel,
                    IdempotencyKey = NormKey(request.IdempotencyKey),
                    CreatedBy = actor,
                };
                _db.PaymentCreditMovements.Add(movement);
                await _db.SaveChangesAsync(ct);
                await RecomputeUnappliedAsync(companyId, payment, ct);
                AuditMovement(companyId, actor, movement, payment, rule, null, "จองยอดก่อนออกใบเสร็จจากเงินรอจัดสรร");
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex) && NormKey(request.IdempotencyKey) != null)
        {
            // ฝ่ายค้าน L4: สองคำขอกุญแจเดียวกันพร้อมกัน — ตัวที่สองชน unique index ⇒ คืน "บันทึกไปแล้ว" ของรายการแรก (ไม่ใช่ 500)
            _db.ChangeTracker.Clear();
            if (await ExistingByKeyAsync(companyId, request.IdempotencyKey, paymentId, kind, ct) is { } raced) return raced;
            throw;
        }

        // (2) ใบเสร็จขายเงินสด — ขาเงินเข้า = ผังพักที่รับไว้จริง (Dr 21630 แทนธนาคาร · ไม่ใช่เงินเข้าซ้ำ) · ระบบอนุมัติให้ (ไม่มีช่วงแก้ยอด/แหล่งเงินในร่าง)
        // ฝ่ายค้านรอบ 259 M1: หลังจองแล้วห้ามถูกยกเลิกกลางทาง — เดินจนจบด้วย CancellationToken.None
        var nc = CancellationToken.None;
        Guid? receiptId = null;
        string receiptNo = "";
        var notices = new List<string>();
        var settled = await SettledDocumentNumbersAsync(companyId, payment!.Id, nc);
        var internalNote = CreditReceiptPlan.InternalNote(payment.PaymentNumber, amount, periodLabel);
        try
        {
            var created = await _documents.CreateDocumentAsync(companyId, new CreateDocumentRequest(
                DocumentType: DocumentType.Receipt,
                DocumentDate: paperDate,
                DueDate: paperDate,
                ContactId: payment.ContactId!.Value,
                Reference: payment.PaymentNumber,
                Notes: CreditReceiptPlan.ReceiptNotes(payment.PaymentNumber, payment.PaymentDate, settled),
                Lines: docLines,
                PaymentAccountId: payment.UnappliedAccountId,
                PricesIncludeVat: pricesIncludeVat), actor, recurringRun: origin);
            receiptId = created.Id;
            receiptNo = created.DocumentNumber;
            if (CreditReceiptPlan.TotalMismatch(created.TotalAmount, amount) is string mismatch)
                throw new BusinessRuleException(mismatch, rule, 409);
            // ร่าง: งวดภาษีที่ตัดสินแล้ว + หมายเหตุภายใน (ก่อนอนุมัติ — ชั้นอนุมัติอ่านค่านี้ในธุรกรรมเดียวกับการลงบัญชี) + audit (ฝ่ายค้าน L4)
            await StampDraftAsync(companyId, created.Id, vatDueAt, new[] { internalNote }.Concat(draftNotes).ToList(), decision, actor, nc);
            var approved = await _documents.ApproveDocumentAsync(companyId, created.Id, actor, ApprovalAckSource.SystemWorkflow, withAiHints: false);
            receiptNo = approved.DocumentNumber;
            notices.AddRange(draftNotes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ออกใบเสร็จจากเงินรอจัดสรร {Pay} ไม่สำเร็จ", payment.PaymentNumber);
            _db.ChangeTracker.Clear();
            DocumentStatus? st = null;
            if (receiptId is Guid sid)
                st = await _db.Documents.AsNoTracking().Where(d => d.Id == sid && d.CompanyId == companyId && !d.IsDeleted)
                    .Select(d => (DocumentStatus?)d.Status).FirstOrDefaultAsync(nc);
            if (CustomerCreditMovementRules.DepositIssued(st))
            {
                // อนุมัติแล้ว (JE Dr ผังพัก commit แล้ว) แต่ขั้นหลังอนุมัติล้ม ⇒ ห้ามปล่อยการจอง (เงินจะถูกนับสองที่) — ผูกใบ + บอกผู้ใช้ (หลักเดียวกับ 259 H2)
                receiptNo = await _db.Documents.AsNoTracking().Where(d => d.Id == receiptId!.Value && d.CompanyId == companyId)
                    .Select(d => d.DocumentNumber).FirstAsync(nc);
                notices.AddRange(draftNotes);
                notices.Add($"ออกใบเสร็จ {receiptNo} และอนุมัติแล้ว แต่ขั้นหลังอนุมัติล้ม: {ex.Message} — ตรวจใบ (e-Tax/การส่ง) ที่หน้าเอกสาร");
            }
            else
            {
                string? orphan = null;
                if (receiptId is Guid did && st is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
                {
                    try { await _documents.DeleteDocumentAsync(companyId, did); }
                    catch (Exception delEx)
                    {
                        _logger.LogError(delEx, "ลบใบเสร็จร่าง {Doc} หลังออกจากเงินรอจัดสรรไม่สำเร็จไม่ได้", did);
                        _db.ChangeTracker.Clear();
                        orphan = await MarkOrphanDraftAsync(companyId, did, payment.PaymentNumber, actor);
                    }
                }
                await ReleaseMovementAsync(companyId, payment.Id, movement!.Id, "ออกใบเสร็จไม่สำเร็จ: " + ex.Message, actor, nc);
                var why = ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException or DocumentApprovalWarningsException
                    ? ex.Message : "ข้อผิดพลาดของระบบ";
                throw new BusinessRuleException($"ออกใบเสร็จจากเงินรอจัดสรรไม่สำเร็จ — {why} · เงินยังอยู่ในเงินรอจัดสรรเท่าเดิม" + (orphan == null ? "" : " · " + orphan),
                    rule, 409);
            }
        }

        // (3) ผูกใบกับรายการ (audit)
        var movementId = movement!.Id;
        var targetId = receiptId!.Value;
        await StampTargetAsync(companyId, movementId, targetId, receiptNo, actor, rule);
        if (periodLabel != null)
            notices.Add($"ใบนี้ปิด{periodLabel} แล้ว — รอบอัตโนมัติของรายการประจำจะไม่ออกใบงวดนี้ซ้ำ");
        var fresh = await _db.Payments.AsNoTracking().FirstAsync(p => p.Id == paymentId && p.CompanyId == companyId, nc);
        _logger.LogInformation("เงินรอจัดสรร {Pay}: ออกใบเสร็จ {Doc} {Amount:N2} (คงเหลือ {Left:N2})", fresh.PaymentNumber, receiptNo, amount, fresh.UnappliedAmount);
        return new CustomerCreditMovementResult(fresh.Id, fresh.PaymentNumber, movementId, kind.ToString(), amount, fresh.UnappliedAmount,
            targetId, receiptNo, notices);
    }

    /// <summary>ประทับร่างก่อนอนุมัติ: งวดภาษีที่ตัดสินแล้ว (<c>CreditReceiptVatDueAt</c>) + หมายเหตุภายใน + audit chain (ฝ่ายค้าน H1/M2/L4)</summary>
    private async Task StampDraftAsync(Guid companyId, Guid draftId, DateTime? vatDueAt, IReadOnlyList<string> notes, LateReceiptTaxPointDecision decision,
        string actor, CancellationToken ct)
    {
        var draft = await _db.Documents.FirstAsync(d => d.Id == draftId && d.CompanyId == companyId, ct);
        foreach (var n in notes) draft.InternalNotes = DepositPolicyResolver.AppendNoteOnce(draft.InternalNotes, n);
        draft.CreditReceiptVatDueAt = vatDueAt;
        draft.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = Guid.TryParse(actor, out var uid) ? uid : null,
            Action = AuditAction.Update,
            EntityType = "Document",
            EntityId = draftId.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                RuleCode = CreditReceiptPlan.RuleCode,
                LegalReference = CreditReceiptPlan.LegalReference,
                CreditReceiptVatDueAt = vatDueAt,
                DecidedTaxPoint = decision.TaxPointDate,
                decision.LateFlag,
                Notes = notes,
            }),
        });
        await _db.SaveChangesAsync(ct);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.GetType().Name == "PostgresException"
           && (ex.InnerException.Message.Contains("23505") || ex.InnerException.Message.Contains("duplicate key value"));
}
