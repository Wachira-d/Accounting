using System.Globalization;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Journal;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 259 แพ็ก P4/P5 "เงินรอจัดสรร ต่อจนจบ" (DECISIONS 259-1-x · erp-review/2026-11-11/team-D-lump-payment.md §3.2 ขั้น 3–5 · §3.6) —
/// ทางออกของเงินรอจัดสรรที่ไม่ใช่การจัดสรรให้ใบที่ออกแล้ว: ออกใบแจ้งหนี้ส่วนที่ขาด (จอง → อนุมัติแล้วจัดสรรเอง) · ย้ายเป็นเงินมัดจำ ·
/// คืนเงินลูกค้า · ตัดเศษ · ยกเลิกขาออก · ข้อเสนอของบรรทัดเงินเข้า
/// <para>กติกาอยู่ที่ <see cref="CustomerCreditMovementRules"/> (pure) · ยอดคงเหลือคิดใหม่ที่ <see cref="CustomerCreditLedger"/> ตัวเดียว ·
/// ทุกการเขียนอยู่ใต้ล็อกแถวการชำระ (<c>FOR UPDATE</c>) + ด่านงวดบัญชีปิด + audit ผ่าน <c>AddChainedAuditLog</c> · กดซ้ำ = กุญแจ <c>IdempotencyKey</c></para>
/// </summary>
public partial class CustomerCreditService
{
    private static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    // ───────────────────────── อ่าน ─────────────────────────

    private async Task<Dictionary<Guid, List<CustomerCreditMovementRow>>> MovementRowsAsync(Guid companyId, List<Guid> paymentIds, CancellationToken ct)
    {
        if (paymentIds.Count == 0) return new Dictionary<Guid, List<CustomerCreditMovementRow>>();
        var rows = await _db.PaymentCreditMovements.AsNoTracking()
            .Where(m => m.CompanyId == companyId && paymentIds.Contains(m.PaymentId) && !m.IsDeleted)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
        var targetIds = rows.Where(r => r.TargetDocumentId != null).Select(r => r.TargetDocumentId!.Value).Distinct().ToList();
        var targets = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.CompanyId == companyId && targetIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentNumber, d.Status, d.IsDeleted })
            .ToDictionaryAsync(d => d.Id, ct);
        return rows.GroupBy(r => r.PaymentId).ToDictionary(g => g.Key, g => g.Select(m =>
        {
            var kind = CustomerCreditMovementRules.Parse(m.Kind);
            var t = m.TargetDocumentId is Guid tid && targets.TryGetValue(tid, out var tt) ? tt : null;
            var live = CustomerCreditMovementRules.IsLive(m.IsDeleted, m.ReleasedAt)
                && (kind != PaymentCreditMovementKind.ToInvoice
                    || CustomerCreditMovementRules.HoldsUsable(kind.Value, m.IsDeleted, m.ReleasedAt, t == null || t.IsDeleted ? (DocumentStatus?)null : t.Status));
            var canUndo = live && kind is PaymentCreditMovementKind.Refund or PaymentCreditMovementKind.WriteOff or PaymentCreditMovementKind.ToInvoice;
            return new CustomerCreditMovementRow(m.Id, m.Kind, kind is { } k ? CustomerCreditMovementRules.LabelOf(k) : m.Kind, m.Amount, m.MovedAt,
                m.TargetDocumentId, t?.DocumentNumber, m.Evidence, m.Note, live, canUndo, m.ReleaseNote);
        }).ToList());
    }

    private async Task<decimal> WriteOffCapAsync(Guid companyId, CancellationToken ct)
        => await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => (decimal?)s.CustomerCreditWriteOffCap)
            .FirstOrDefaultAsync(ct) ?? CustomerCreditMovementRules.DefaultWriteOffCap;

    /// <summary>ปุ่มของแถว (เซิร์ฟเวอร์ตัดสิน) — ด่านจริงอยู่ที่ <see cref="CustomerCreditMovementRules.Check"/> ตอนกด</summary>
    private static CustomerCreditActions ActionsFor(bool needsReview, bool hasContact, decimal available, decimal cap)
    {
        var usable = !needsReview && available > UnappliedCreditBalance.Tolerance;
        string? writeOffWhy = null;
        if (usable && available > cap + UnappliedCreditBalance.Tolerance)
            writeOffWhy = $"คงเหลือ {Money(available)} เกินเพดานตัดเศษ {Money(cap)}";
        return new CustomerCreditActions(usable && hasContact, usable && hasContact, usable && hasContact, usable && writeOffWhy == null, writeOffWhy,
            CanIssueReceipt: usable && hasContact);
    }

    // ───────────────────────── ตั้งค่า ─────────────────────────

    public async Task<decimal> SetWriteOffCapAsync(Guid companyId, decimal cap, string actor, CancellationToken ct = default)
    {
        if (CustomerCreditMovementRules.CapProblem(cap) is string problem)
            throw new BusinessRuleException(problem, CustomerCreditMovementRules.WriteOffRuleCode, 400);
        var settings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ยังไม่มีการตั้งค่าของบริษัท — เปิดหน้าตั้งค่าบริษัทแล้วบันทึกครั้งหนึ่งก่อน");
        var before = settings.CustomerCreditWriteOffCap;
        settings.CustomerCreditWriteOffCap = cap;
        settings.UpdatedAt = DateTime.UtcNow;
        settings.UpdatedBy = actor;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = Guid.TryParse(actor, out var uid) ? uid : null,
            Action = AuditAction.Update,
            EntityType = "CompanySettings",
            EntityId = settings.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                RuleCode = CustomerCreditMovementRules.WriteOffRuleCode,
                Field = nameof(CompanySettings.CustomerCreditWriteOffCap),
                Before = before,
                After = cap,
            }),
        });
        await _db.SaveChangesAsync(ct);
        return cap;
    }

    // ───────────────────────── ขาออกที่ลง JE เอง (คืนเงิน · ตัดเศษ) ─────────────────────────

    public Task<CustomerCreditMovementResult> RefundAsync(Guid companyId, Guid paymentId, CustomerCreditRefundRequest request, string actor,
        CancellationToken ct = default)
        => MoveWithJournalAsync(companyId, paymentId, PaymentCreditMovementKind.Refund, request.Amount, request.Date, request.BankAccountId,
            request.Evidence, request.Note, request.IdempotencyKey, request.ExpectedUnappliedBefore, actor, ct);

    public Task<CustomerCreditMovementResult> WriteOffAsync(Guid companyId, Guid paymentId, CustomerCreditWriteOffRequest request, string actor,
        CancellationToken ct = default)
        => MoveWithJournalAsync(companyId, paymentId, PaymentCreditMovementKind.WriteOff, request.Amount, request.Date, null,
            null, request.Note, request.IdempotencyKey, request.ExpectedUnappliedBefore, actor, ct);

    /// <summary>รายการเดิมของกุญแจกันกดซ้ำ (ซ้ำ = คืนผลเดิม · กุญแจเดียวกันแต่คนละรายการ = ปฏิเสธ)</summary>
    private async Task<CustomerCreditMovementResult?> ExistingByKeyAsync(Guid companyId, string? key, Guid paymentId, PaymentCreditMovementKind kind,
        CancellationToken ct)
    {
        var k = NormKey(key);   // ฝ่ายค้านรอบ 259 L5: ตัวทำคีย์เดียวกับตอนบันทึก
        if (k == null) return null;
        var m = await _db.PaymentCreditMovements.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.IdempotencyKey == k, ct);
        if (m == null) return null;
        if (m.ReleasedAt != null && m.TargetDocumentId == null && m.JournalEntryId == null) return null;   // M2: รายการที่ล้มแล้วถูกถอย = ไม่มี
        if (m.PaymentId != paymentId || m.Kind != kind.ToString())
            throw new BusinessRuleException("กุญแจกันกดซ้ำนี้ถูกใช้กับรายการอื่นแล้ว — ปิดหน้าต่างแล้วเปิดใหม่", CustomerCreditMovementRules.UndoRuleCode, 409);
        var p = await _db.Payments.AsNoTracking().FirstAsync(x => x.Id == paymentId && x.CompanyId == companyId, ct);
        var target = m.TargetDocumentId is Guid tid
            ? await _db.Documents.AsNoTracking().Where(d => d.Id == tid && d.CompanyId == companyId).Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct)
            : null;
        return new CustomerCreditMovementResult(p.Id, p.PaymentNumber, m.Id, m.Kind, m.Amount, p.UnappliedAmount, m.TargetDocumentId, target,
            new[] { "รายการนี้บันทึกไปแล้ว (กดซ้ำ) — ไม่ได้ทำซ้ำ" });
    }

    private static string? NormKey(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim()[..Math.Min(80, key.Trim().Length)];

    private async Task<CustomerCreditMovementResult> MoveWithJournalAsync(Guid companyId, Guid paymentId, PaymentCreditMovementKind kind,
        decimal amount, DateTime? requestedDate, Guid? bankAccountId, string? evidence, string? note, string? idempotencyKey,
        decimal? expectedUnappliedBefore, string actor, CancellationToken ct)
    {
        if (await ExistingByKeyAsync(companyId, idempotencyKey, paymentId, kind, ct) is { } dup) return dup;
        var today = Today();
        var date = (requestedDate ?? today).Date;
        var cap = await WriteOffCapAsync(companyId, ct);
        Payment? payment = null;
        PaymentCreditMovement? movement = null;
        var ruleCode = kind == PaymentCreditMovementKind.Refund ? CustomerCreditMovementRules.RefundRuleCode : CustomerCreditMovementRules.WriteOffRuleCode;

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            payment = await _db.Payments
                .FromSqlRaw("""SELECT * FROM "Payments" WHERE "Id" = {0} AND "CompanyId" = {1} FOR UPDATE""", paymentId, companyId)
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("ไม่พบการรับชำระ (อาจถูกยกเลิกแล้ว)");
            await _db.Entry(payment).ReloadAsync(ct);
            if (CustomerCreditAllocation.StaleCheck(expectedUnappliedBefore, payment.UnappliedAmount, payment.PaymentNumber) is { } stale)
                throw new BusinessRuleException(stale.Message, stale.RuleCode, stale.StatusCode);
            var credit = await AvailableFactsAsync(companyId, payment, null, ct);

            // คืนเงิน: บัญชีธนาคาร → ผังที่ผูก (ตัวตัดสินแหล่งเงินตัวเดียว DocumentMoneyChannel — ไม่ตกเงินสดเงียบ ๆ)
            Guid? bankGl = null;
            string? bankProblem = null;
            if (kind == PaymentCreditMovementKind.Refund && bankAccountId is Guid bid && bid != Guid.Empty)
            {
                var channel = await DocumentMoneyChannel.EvaluateAsync(_db, companyId, bid, null, MoneyChannelCheck.Save,
                    "เลือกบัญชีธนาคารที่จ่ายคืนใหม่", ct);
                bankGl = channel.GlAccountId;
                bankProblem = channel.Problem;
            }
            if (CustomerCreditMovementRules.Check(kind, credit, credit.UnappliedAmount, amount, date, today, cap,
                    hasBankAccount: bankGl != null || bankProblem != null, evidence: evidence, reason: note) is { } refusal)
                throw new BusinessRuleException(refusal.Message, refusal.RuleCode, refusal.StatusCode);
            if (bankProblem != null)
                throw new BusinessRuleException(bankProblem, ruleCode, 409);
            if (await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, date, ct) is string closed)
                throw new BusinessRuleException(closed, ruleCode, 409);

            var counterAccount = kind == PaymentCreditMovementKind.Refund
                ? bankGl!.Value
                : await SettlementChartSeed.EnsureStandardAccountAsync(_db, companyId, DocumentRounding.AccountCode, ct)
                    ?? throw new BusinessRuleException(
                        $"ไม่พบผัง {DocumentRounding.AccountCode} “ผลต่างจากการปัดเศษ” — เพิ่มผังนี้ที่หน้า “ผังบัญชี” แล้วลองใหม่", ruleCode, 409);
            var kindName = kind.ToString();
            var sequence = await _db.PaymentCreditMovements.IgnoreQueryFilters()
                .CountAsync(m => m.CompanyId == companyId && m.PaymentId == payment.Id && m.Kind == kindName, ct) + 1;
            var reference = CustomerCreditMovementRules.JournalReference(payment.PaymentNumber, kind, sequence);
            var builder = JournalEntryBuilder.For(_db, companyId, date)
                .Type(kind == PaymentCreditMovementKind.Refund ? JournalType.CashPayments : JournalType.General)
                .Description(kind == PaymentCreditMovementKind.Refund
                    ? $"คืนเงินลูกค้าจากเงินรอจัดสรร {payment.PaymentNumber} ({(evidence ?? "").Trim()})"
                    : $"ตัดเศษเงินรอจัดสรร {payment.PaymentNumber}")
                .Reference(reference)
                .Debit(payment.UnappliedAccountId!.Value, amount, $"ตัดเงินรับจากลูกค้ารอจัดสรร - {payment.PaymentNumber}");
            builder = kind == PaymentCreditMovementKind.Refund
                ? builder.Credit(counterAccount, amount, $"จ่ายคืนลูกค้า - {payment.PaymentNumber}")
                : builder.Credit(counterAccount, amount, $"ผลต่างจากการปัดเศษ (เศษเงินรอจัดสรร) - {payment.PaymentNumber}");
            var je = await builder.PostAsync(actor, ct);

            movement = new PaymentCreditMovement
            {
                CompanyId = companyId,
                PaymentId = payment.Id,
                Kind = kindName,
                Amount = amount,
                MovedAt = date,
                JournalEntryId = je.Id,
                BankAccountId = kind == PaymentCreditMovementKind.Refund ? bankAccountId : null,
                Evidence = string.IsNullOrWhiteSpace(evidence) ? null : evidence.Trim(),
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                IdempotencyKey = NormKey(idempotencyKey),
                CreatedBy = actor,
            };
            _db.PaymentCreditMovements.Add(movement);
            await _db.SaveChangesAsync(ct);
            if (kind == PaymentCreditMovementKind.Refund)
                await _db.BankAccounts.Where(b => b.Id == bankAccountId!.Value && b.CompanyId == companyId)
                    .ExecuteUpdateAsync(u => u.SetProperty(b => b.CurrentBalance, b => b.CurrentBalance - amount), ct);
            await RecomputeUnappliedAsync(companyId, payment, ct);
            AuditMovement(companyId, actor, movement, payment, ruleCode, reference, null);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        _logger.LogInformation("เงินรอจัดสรร {Pay}: {Kind} {Amount:N2} (คงเหลือ {Left:N2})", payment!.PaymentNumber, kind, amount, payment.UnappliedAmount);
        return new CustomerCreditMovementResult(payment.Id, payment.PaymentNumber, movement!.Id, movement.Kind, amount, payment.UnappliedAmount,
            null, null, Array.Empty<string>());
    }

    private void AuditMovement(Guid companyId, string actor, PaymentCreditMovement m, Payment payment, string ruleCode, string? journalReference,
        string? message)
        => _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = Guid.TryParse(actor, out var uid) ? uid : null,
            Action = AuditAction.Create,
            EntityType = "PaymentCreditMovement",
            EntityId = m.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                RuleCode = ruleCode,
                LegalReference = CustomerCreditMovementRules.LegalReference,
                payment.PaymentNumber,
                m.Kind,
                m.Amount,
                m.MovedAt,
                m.BankAccountId,
                m.Evidence,
                m.TargetDocumentId,
                JournalReference = journalReference,
                RemainingUnapplied = payment.UnappliedAmount,
                Message = message,
            }),
        });

    // ───────────────────────── ยกเลิกขาออก ─────────────────────────

    public async Task<CustomerCreditMovementResult> UndoMovementAsync(Guid companyId, Guid paymentId, Guid movementId, string? reason, string actor,
        CancellationToken ct = default)
    {
        var today = Today();
        var why = (reason ?? "").Trim();
        Payment? payment = null;
        PaymentCreditMovement? movement = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            payment = await _db.Payments
                .FromSqlRaw("""SELECT * FROM "Payments" WHERE "Id" = {0} AND "CompanyId" = {1} FOR UPDATE""", paymentId, companyId)
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("ไม่พบการรับชำระ (อาจถูกยกเลิกแล้ว)");
            await _db.Entry(payment).ReloadAsync(ct);
            movement = await _db.PaymentCreditMovements
                .FirstOrDefaultAsync(m => m.Id == movementId && m.PaymentId == paymentId && m.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบรายการ (อาจถูกยกเลิกไปแล้ว)");
            await _db.Entry(movement).ReloadAsync(ct);   // ใต้ล็อก: คำขอซ้อนที่ยกเลิกไปแล้วต้องเห็น
            if (!CustomerCreditMovementRules.IsLive(movement.IsDeleted, movement.ReleasedAt))
                throw new BusinessRuleException("รายการนี้ถูกยกเลิก/ปล่อยไปแล้ว", CustomerCreditMovementRules.UndoRuleCode, 409);
            var kind = CustomerCreditMovementRules.Parse(movement.Kind)
                ?? throw new BusinessRuleException("ชนิดรายการไม่รู้จัก — ให้ผู้ดูแลระบบตรวจ", CustomerCreditMovementRules.UndoRuleCode, 409);
            // ฝ่ายค้านรอบ 259 L1: ยกเลิกคืนเงิน/ตัดเศษ (กลับ JE + ยอดธนาคาร) ต้องมีเหตุผล
            if (kind is PaymentCreditMovementKind.Refund or PaymentCreditMovementKind.WriteOff && why.Length < 5)
                throw new BusinessRuleException("ระบุเหตุผลของการยกเลิก (อย่างน้อย 5 ตัวอักษร) — รายการนี้กลับรายการบัญชี", CustomerCreditMovementRules.UndoRuleCode, 400);
            if (CustomerCreditMovementRules.IssuesApprovedDocument(kind))
            {
                // ฝ่ายค้านรอบ 259 M1: ขาออกที่ยังไม่ได้ผูกใบมัดจำ (ล้มกลางทาง) — หาใบมัดจำของเงินก้อนนี้ก่อน: มี = ผูก + ให้ยกเลิกใบนั้น · ไม่มี = ปล่อยได้
                // รอบ 265: ใบเสร็จจากเงินรอจัดสรร (ToReceipt) เดินด่านเดียวกัน — ใบเสร็จ standalone (ไม่ใช่มัดจำ) ที่ขาเงินเข้าเป็นผังพักของเงินก้อนนี้
                var isDepositKind = kind == PaymentCreditMovementKind.ToDeposit;
                if (movement.TargetDocumentId != null)
                    throw new BusinessRuleException(isDepositKind
                        ? "ยกเลิกการย้ายเป็นมัดจำที่นี่ไม่ได้ — ยกเลิกใบมัดจำที่หน้าเอกสาร (ระบบคืนเงินกลับเป็นเงินรอจัดสรรให้เอง)"
                        : "ยกเลิกใบเสร็จที่ออกจากเงินรอจัดสรรที่นี่ไม่ได้ — ยกเลิกใบเสร็จนั้นที่หน้าเอกสาร (ระบบคืนเงินกลับเป็นเงินรอจัดสรรให้เอง)",
                        CustomerCreditMovementRules.UndoRuleCode, 409);
                var since = movement.CreatedAt.AddMinutes(-5);
                var thisMovementId = movement.Id;
                var thisAmount = movement.Amount;
                var orphanDeposit = await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.IsDeposit == isDepositKind && d.DocumentType == DocumentType.Receipt
                        && d.Reference == payment.PaymentNumber
                        && d.PaymentAccountId == payment.UnappliedAccountId && d.CreatedAt >= since
                        && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                        // รอบ 265 ฝ่ายค้าน M1: ใบที่เป็นปลายทางของขาออกอื่นที่ยังมีผลอยู่แล้ว ห้ามถูกผูกซ้ำ (เงินก้อนเดียวหลายใบ) · ยอดต้องเท่ารายการนี้
                        && !_db.PaymentCreditMovements.Any(m => m.CompanyId == companyId && m.TargetDocumentId == d.Id && m.Id != thisMovementId
                            && !m.IsDeleted && m.ReleasedAt == null)
                        && d.TotalAmount == thisAmount)
                    .OrderBy(d => d.CreatedAt).Select(d => new { d.Id, d.DocumentNumber, d.Status }).FirstOrDefaultAsync(ct);
                if (orphanDeposit != null)
                {
                    if (CustomerCreditMovementRules.DepositIssued(orphanDeposit.Status))
                    {
                        movement.TargetDocumentId = orphanDeposit.Id;
                        movement.UpdatedAt = DateTime.UtcNow;
                        AuditMovement(companyId, actor, movement, payment, isDepositKind ? CustomerCreditMovementRules.ToDepositRuleCode
                                : CustomerCreditMovementRules.ToReceiptRuleCode, null,
                            "ผูกเอกสารที่ออกแล้วกับรายการที่ค้าง " + orphanDeposit.DocumentNumber);
                        await _db.SaveChangesAsync(ct);
                        await tx.CommitAsync(ct);
                        throw new BusinessRuleException(
                            $"{(isDepositKind ? "ใบมัดจำ" : "ใบเสร็จ")} {orphanDeposit.DocumentNumber} ของรายการนี้ออกแล้ว — ผูกกับรายการให้แล้ว · ยกเลิกใบนั้นที่หน้าเอกสารถ้าต้องการคืนเงินเป็นเงินรอจัดสรร",
                            CustomerCreditMovementRules.UndoRuleCode, 409);
                    }
                    throw new BusinessRuleException(
                        $"มีร่าง {orphanDeposit.DocumentNumber} ของรายการนี้ค้างอยู่ — ลบร่างนั้นที่หน้าเอกสารก่อน แล้วกดยกเลิกอีกครั้ง",
                        CustomerCreditMovementRules.UndoRuleCode, 409);
                }
            }
            if (kind != PaymentCreditMovementKind.ToInvoice)
            {
                if (await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, today, ct) is string closed)
                    throw new BusinessRuleException(closed, CustomerCreditMovementRules.UndoRuleCode, 409);
                if (movement.JournalEntryId is Guid jeId)
                    await _accounting.ReverseJournalEntryAsync(companyId, jeId, reversalDate: today,
                        description: $"ยกเลิก{CustomerCreditMovementRules.LabelOf(kind)} - {payment.PaymentNumber}", systemTriggered: true);
                if (kind == PaymentCreditMovementKind.Refund && movement.BankAccountId is Guid bank)
                {
                    var amt = movement.Amount;
                    await _db.BankAccounts.Where(b => b.Id == bank && b.CompanyId == companyId)
                        .ExecuteUpdateAsync(u => u.SetProperty(b => b.CurrentBalance, b => b.CurrentBalance + amt), ct);
                }
            }
            movement.ReleasedAt = DateTime.UtcNow;
            movement.ReleaseNote = kind switch
            {
                PaymentCreditMovementKind.ToInvoice => "ยกเลิกการจองโดยผู้ใช้",
                PaymentCreditMovementKind.ToDeposit => "ยกเลิกรายการที่ค้าง (ไม่มีใบมัดจำ)",
                PaymentCreditMovementKind.ToReceipt => "ยกเลิกรายการที่ค้าง (ไม่มีใบเสร็จ)",
                _ => "ยกเลิกโดยผู้ใช้ (กลับรายการบัญชีแล้ว): " + why,
            };
            movement.UpdatedAt = DateTime.UtcNow;
            movement.UpdatedBy = actor;
            await _db.SaveChangesAsync(ct);
            await RecomputeUnappliedAsync(companyId, payment, ct);
            AuditMovement(companyId, actor, movement, payment, CustomerCreditMovementRules.UndoRuleCode, null, movement.ReleaseNote);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return new CustomerCreditMovementResult(payment!.Id, payment.PaymentNumber, movement!.Id, movement.Kind, movement.Amount,
            payment.UnappliedAmount, movement.TargetDocumentId, null, Array.Empty<string>());
    }

    // ───────────────────────── ย้ายเป็นเงินมัดจำ ─────────────────────────

    public async Task<CustomerCreditMovementResult> ToDepositAsync(Guid companyId, Guid paymentId, CustomerCreditToDepositRequest request, string actor,
        CancellationToken ct = default)
    {
        var kind = PaymentCreditMovementKind.ToDeposit;
        if (await ExistingByKeyAsync(companyId, request.IdempotencyKey, paymentId, kind, ct) is { } dup) return dup;
        var today = Today();
        var amount = request.Amount;
        var vatRegistered = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.IsVatRegistered).FirstOrDefaultAsync(ct);
        if (vatRegistered && await _documents.CreditAllocationVatLockReasonAsync(companyId, today) is string vatLocked)
            throw new BusinessRuleException($"ออกใบมัดจำลงวันที่วันนี้ไม่ได้ — วันนี้อยู่ใน{vatLocked}", CustomerCreditMovementRules.ToDepositRuleCode, 409);

        // (1) จองยอดใต้ล็อก (ขาออกยังไม่มีใบปลายทาง — นับตัดยอดทันที กันใช้ซ้อนระหว่างออกใบ)
        Payment? payment = null;
        PaymentCreditMovement? movement = null;
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
            if (CustomerCreditMovementRules.Check(kind, credit, credit.UnappliedAmount, amount, today, today) is { } refusal)
                throw new BusinessRuleException(refusal.Message, refusal.RuleCode, refusal.StatusCode);
            if (await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, today, ct) is string closed)
                throw new BusinessRuleException(closed, CustomerCreditMovementRules.ToDepositRuleCode, 409);
            movement = new PaymentCreditMovement
            {
                CompanyId = companyId,
                PaymentId = payment.Id,
                Kind = kind.ToString(),
                Amount = amount,
                MovedAt = today,
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                IdempotencyKey = NormKey(request.IdempotencyKey),
                CreatedBy = actor,
            };
            _db.PaymentCreditMovements.Add(movement);
            await _db.SaveChangesAsync(ct);
            await RecomputeUnappliedAsync(companyId, payment, ct);
            AuditMovement(companyId, actor, movement, payment, CustomerCreditMovementRules.ToDepositRuleCode, null, "จองยอดก่อนออกใบมัดจำ");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        // (2) ใบมัดจำในระบบมัดจำเดิม — ขาเงินเข้า = ผังพักที่รับไว้จริง (Dr 21630 แทน Dr ธนาคาร) · ระบบอนุมัติให้ทันที (ไม่มีช่วงแก้ยอด/แหล่งเงินในร่าง)
        // ฝ่ายค้านรอบ 259 M1: หลังจองแล้วห้ามถูกยกเลิกกลางทาง (ปิดแท็บ = RequestAborted) — ขั้นออกใบ/ชดเชย/ผูกใบต้องเดินจนจบ
        var nc = CancellationToken.None;
        Guid? depositId = null;
        string depositNo = "";
        var notices = new List<string>();
        try
        {
            var vatRate = vatRegistered
                ? await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId).Select(s => (decimal?)s.DefaultVatRate).FirstOrDefaultAsync(nc) ?? 7m
                : 0m;
            var created = await _documents.CreateDocumentAsync(companyId, new CreateDocumentRequest(
                DocumentType: DocumentType.Receipt,
                DocumentDate: today,
                DueDate: today,
                ContactId: payment!.ContactId!.Value,
                Reference: payment.PaymentNumber,
                Notes: null,
                Lines: new List<DocumentLineRequest>
                {
                    new(Description: CustomerCreditMovementRules.DepositLineDescription(payment.PaymentNumber, payment.PaymentDate),
                        Quantity: 1m, Unit: "รายการ", UnitPrice: amount, DiscountPercent: 0m, VatRate: vatRate, WithholdingTaxRate: 0m, AccountId: null),
                },
                PaymentAccountId: payment.UnappliedAccountId,
                PricesIncludeVat: true,
                IsDeposit: true,
                DepositKindId: request.DepositKindId), actor);
            depositId = created.Id;
            depositNo = created.DocumentNumber;
            if (Math.Abs(created.TotalAmount - amount) > UnappliedCreditBalance.Tolerance)
                throw new BusinessRuleException(
                    $"ยอดใบมัดจำที่ระบบคำนวณ ({Money(created.TotalAmount)}) ไม่เท่ายอดที่ย้าย ({Money(amount)}) — ยกเลิกการย้ายแล้ว · ออกใบมัดจำเองที่หน้าเอกสาร",
                    CustomerCreditMovementRules.ToDepositRuleCode, 409);
            var approved = await _documents.ApproveDocumentAsync(companyId, created.Id, actor, ApprovalAckSource.SystemWorkflow, withAiHints: false);
            depositNo = approved.DocumentNumber;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ย้ายเงินรอจัดสรร {Pay} เป็นมัดจำไม่สำเร็จ", payment!.PaymentNumber);
            _db.ChangeTracker.Clear();
            DocumentStatus? st = null;
            if (depositId is Guid sid)
                st = await _db.Documents.AsNoTracking().Where(d => d.Id == sid && d.CompanyId == companyId && !d.IsDeleted)
                    .Select(d => (DocumentStatus?)d.Status).FirstOrDefaultAsync(nc);
            if (CustomerCreditMovementRules.DepositIssued(st))
            {
                // ฝ่ายค้านรอบ 259 H2: ใบมัดจำอนุมัติแล้ว (JE Dr ผังพัก commit แล้ว) แต่ขั้นหลังอนุมัติล้ม ⇒ ห้ามปล่อยการจอง (เงินจะถูกนับสองที่) —
                // ผูกใบกับรายการตามปกติ แล้วบอกผู้ใช้ว่าขั้นหลังอนุมัติล้ม
                depositNo = await _db.Documents.AsNoTracking().Where(d => d.Id == depositId!.Value && d.CompanyId == companyId)
                    .Select(d => d.DocumentNumber).FirstAsync(nc);
                notices.Add($"ออกใบมัดจำ {depositNo} และอนุมัติแล้ว แต่ขั้นหลังอนุมัติล้ม: {ex.Message} — ตรวจใบมัดจำ (e-Tax/การส่ง) ที่หน้าเอกสาร");
            }
            else
            {
                // ล้มก่อนอนุมัติ: ลบร่าง (ถ้ามี) + ปล่อยการจอง ⇒ เงินกลับเป็นเงินรอจัดสรร · ลบร่างไม่ได้ = ล้มดัง (ฝ่ายค้าน L4)
                string? orphan = null;
                if (depositId is Guid did && st is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
                {
                    try { await _documents.DeleteDocumentAsync(companyId, did); }
                    catch (Exception delEx)
                    {
                        _logger.LogError(delEx, "ลบใบมัดจำร่าง {Doc} หลังย้ายไม่สำเร็จไม่ได้", did);
                        _db.ChangeTracker.Clear();
                        orphan = await MarkOrphanDraftAsync(companyId, did, payment.PaymentNumber, actor);
                    }
                }
                await ReleaseMovementAsync(companyId, payment.Id, movement!.Id, "ออกใบมัดจำไม่สำเร็จ: " + ex.Message, actor, nc);
                var why = ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException or DocumentApprovalWarningsException
                    ? ex.Message : "ข้อผิดพลาดของระบบ";
                throw new BusinessRuleException($"ย้ายเป็นเงินมัดจำไม่สำเร็จ — {why} · เงินยังอยู่ในเงินรอจัดสรรเท่าเดิม" + (orphan == null ? "" : " · " + orphan),
                    CustomerCreditMovementRules.ToDepositRuleCode, 409);
            }
        }

        // (3) ผูกใบมัดจำกับรายการ (audit — ฝ่ายค้าน L2) + ธงภาษีขายล่าช้า (เงินล่วงหน้าตั้งแต่วันรับแต่ออกใบเดือนนี้ — ระบบไม่ย้ายงวดเอง · ม.78/1)
        var movementId = movement!.Id;
        var targetId = depositId!.Value;
        await StampTargetAsync(companyId, movementId, targetId, depositNo, actor);
        var ct2 = nc;
        var dep = await _db.Documents.FirstOrDefaultAsync(d => d.Id == targetId && d.CompanyId == companyId, ct2);
        if (dep != null && CustomerCreditMovementRules.DepositLateVatNote(payment!.UnappliedReason, payment.PaymentDate, dep.DocumentDate,
                dep.VatAmount, dep.DepositOutputVatDeferred) is string lateNote)
        {
            dep.InternalNotes = DepositPolicyResolver.AppendNoteOnce(dep.InternalNotes, lateNote);
            dep.UpdatedAt = DateTime.UtcNow;
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = Guid.TryParse(actor, out var uid) ? uid : null,
                Action = AuditAction.Update,
                EntityType = "Document",
                EntityId = dep.Id.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    RuleCode = LateReceiptTaxPoint.RuleCode,
                    LegalReference = LateReceiptTaxPoint.LegalReference,
                    dep.DocumentNumber,
                    payment.PaymentNumber,
                    ReceiptDate = payment.PaymentDate.Date,
                    Message = lateNote,
                }),
            });
            await _db.SaveChangesAsync(ct2);
            notices.Add(lateNote);
        }
        var fresh = await _db.Payments.AsNoTracking().FirstAsync(p => p.Id == paymentId && p.CompanyId == companyId, ct2);
        return new CustomerCreditMovementResult(fresh.Id, fresh.PaymentNumber, movementId, kind.ToString(), amount, fresh.UnappliedAmount,
            targetId, depositNo, notices);
    }

    /// <summary>ผูกใบปลายทางกับขาออก + audit (ฝ่ายค้านรอบ 259 L2 — ทุกการเขียนลง audit chain)</summary>
    private async Task StampTargetAsync(Guid companyId, Guid movementId, Guid targetId, string targetNumber, string actor,
        string ruleCode = CustomerCreditMovementRules.ToDepositRuleCode)
    {
        var m = await _db.PaymentCreditMovements.FirstAsync(x => x.Id == movementId && x.CompanyId == companyId);
        m.TargetDocumentId = targetId;
        m.UpdatedAt = DateTime.UtcNow;
        m.UpdatedBy = actor;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = Guid.TryParse(actor, out var uid) ? uid : null,
            Action = AuditAction.Update,
            EntityType = "PaymentCreditMovement",
            EntityId = m.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                RuleCode = ruleCode,
                m.Kind,
                m.Amount,
                TargetDocumentId = targetId,
                TargetDocumentNumber = targetNumber,
            }),
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>ฝ่ายค้านรอบ 259 L4: ร่างที่ลบไม่ได้หลังเส้นล้ม — ติดป้ายบนร่าง (ห้ามอนุมัติ — การจองถูกปล่อยแล้ว) · คืนข้อความถึงผู้ใช้</summary>
    private async Task<string> MarkOrphanDraftAsync(Guid companyId, Guid draftId, string paymentNumber, string actor)
    {
        var note = $"{CustomerCreditMovementRules.OrphanDraftMarker} ร่างนี้เหลือจากการทำรายการเงินรอจัดสรร {paymentNumber} ที่ไม่สำเร็จ — ห้ามอนุมัติ ให้ลบทิ้ง";
        var current = await _db.Documents.AsNoTracking().Where(d => d.Id == draftId && d.CompanyId == companyId)
            .Select(d => new { d.InternalNotes, d.DocumentNumber }).FirstOrDefaultAsync();
        if (current == null) return "";
        var merged = DepositPolicyResolver.AppendNoteOnce(current.InternalNotes, note);
        await _db.Documents.Where(d => d.Id == draftId && d.CompanyId == companyId)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.InternalNotes, merged));
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = Guid.TryParse(actor, out var uid) ? uid : null,
            Action = AuditAction.Update,
            EntityType = "Document",
            EntityId = draftId.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { RuleCode = CustomerCreditMovementRules.UndoRuleCode, Message = note }),
        });
        await _db.SaveChangesAsync();
        return $"ลบร่าง {current.DocumentNumber} ไม่สำเร็จ — ติดป้ายห้ามอนุมัติไว้แล้ว ให้ลบร่างนั้นที่หน้าเอกสาร";
    }

    /// <summary>ปล่อยขาออก (ถอยการจอง) + คิดยอดคงเหลือใหม่ใต้ล็อก · audit</summary>
    private async Task ReleaseMovementAsync(Guid companyId, Guid paymentId, Guid movementId, string reason, string actor, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var payment = await _db.Payments
                .FromSqlRaw("""SELECT * FROM "Payments" WHERE "Id" = {0} AND "CompanyId" = {1} FOR UPDATE""", paymentId, companyId)
                .FirstAsync(ct);
            await _db.Entry(payment).ReloadAsync(ct);
            var m = await _db.PaymentCreditMovements.FirstAsync(x => x.Id == movementId && x.CompanyId == companyId, ct);
            m.ReleasedAt = DateTime.UtcNow;
            m.ReleaseNote = reason.Length > 500 ? reason[..500] : reason;
            m.IdempotencyKey = null;   // ฝ่ายค้านรอบ 259 M2: เส้นล้มที่ถูกถอย — กดใหม่ด้วยคีย์เดิมต้องทำงานจริง ไม่ใช่ "บันทึกไปแล้ว" ปลอม
            m.UpdatedAt = DateTime.UtcNow;
            m.UpdatedBy = actor;
            await _db.SaveChangesAsync(ct);
            await RecomputeUnappliedAsync(companyId, payment, ct);
            AuditMovement(companyId, actor, m, payment, CustomerCreditMovementRules.UndoRuleCode, null, m.ReleaseNote);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    // ───────────────────────── ออกใบแจ้งหนี้ส่วนที่ขาด ─────────────────────────

    public async Task<CustomerCreditMovementResult> IssueInvoiceAsync(Guid companyId, Guid paymentId, CustomerCreditIssueInvoiceRequest request, string actor,
        CancellationToken ct = default)
    {
        var kind = PaymentCreditMovementKind.ToInvoice;
        if (await ExistingByKeyAsync(companyId, request.IdempotencyKey, paymentId, kind, ct) is { } dup) return dup;
        var today = Today();
        var probe = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId && p.CompanyId == companyId && !p.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบการรับชำระ (อาจถูกยกเลิกแล้ว)");
        if (CustomerCreditAllocation.StaleCheck(request.ExpectedUnappliedBefore, probe.UnappliedAmount, probe.PaymentNumber) is { } stale)
            throw new BusinessRuleException(stale.Message, stale.RuleCode, stale.StatusCode);
        var probeFacts = await AvailableFactsAsync(companyId, probe, null, ct);
        var amount = request.Amount ?? probeFacts.UnappliedAmount;
        if (CustomerCreditMovementRules.Check(kind, probeFacts, probeFacts.UnappliedAmount, amount, today, today) is { } refusal)
            throw new BusinessRuleException(refusal.Message, refusal.RuleCode, refusal.StatusCode);
        var vatRegistered = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.IsVatRegistered).FirstOrDefaultAsync(ct);
        var vatRate = vatRegistered
            ? await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId).Select(s => (decimal?)s.DefaultVatRate).FirstOrDefaultAsync(ct) ?? 7m
            : 0m;
        var type = request.DocumentType == DocumentType.TaxInvoice ? DocumentType.TaxInvoice : DocumentType.Invoice;

        // ร่าง (เลขเอกสารออกตอนอนุมัติ §86/4) · วันที่ใบ = วันนี้ (ห้ามย้อน) · ไม่ประทับวันรับชำระ (ระบบประทับเฉพาะเงิน "ค้างออกใบ" ตอนจัดสรร — H1/M3)
        var created = await _documents.CreateDocumentAsync(companyId, new CreateDocumentRequest(
            DocumentType: type,
            DocumentDate: today,
            DueDate: today,
            ContactId: probe.ContactId!.Value,
            Reference: probe.PaymentNumber,
            Notes: null,
            Lines: new List<DocumentLineRequest>
            {
                new(Description: CustomerCreditMovementRules.InvoiceLineDescription(probe.PaymentNumber, probe.PaymentDate),
                    Quantity: 1m, Unit: "รายการ", UnitPrice: amount, DiscountPercent: 0m, VatRate: vatRate, WithholdingTaxRate: 0m, AccountId: null),
            },
            PricesIncludeVat: true), actor);

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
                var credit = await AvailableFactsAsync(companyId, payment, null, ct);
                var hold = Math.Min(amount, created.TotalAmount);
                if (CustomerCreditMovementRules.Check(kind, credit, credit.UnappliedAmount, hold, today, today) is { } again)
                    throw new BusinessRuleException(again.Message, again.RuleCode, again.StatusCode);
                movement = new PaymentCreditMovement
                {
                    CompanyId = companyId,
                    PaymentId = payment.Id,
                    Kind = kind.ToString(),
                    Amount = hold,
                    MovedAt = today,
                    TargetDocumentId = created.Id,
                    IdempotencyKey = NormKey(request.IdempotencyKey),
                    CreatedBy = actor,
                };
                _db.PaymentCreditMovements.Add(movement);
                var draft = await _db.Documents.FirstAsync(d => d.Id == created.Id && d.CompanyId == companyId, ct);
                draft.InternalNotes = DepositPolicyResolver.AppendNoteOnce(draft.InternalNotes,
                    CustomerCreditMovementRules.InvoiceHoldNote(payment.PaymentNumber, hold));
                draft.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                AuditMovement(companyId, actor, movement, payment, CustomerCreditMovementRules.ToInvoiceRuleCode, null, "จองเงินให้ใบแจ้งหนี้ร่าง");
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            try { await _documents.DeleteDocumentAsync(companyId, created.Id); }
            catch (Exception delEx) when (delEx is not OperationCanceledException)
            { _logger.LogError(delEx, "ลบใบแจ้งหนี้ร่าง {Doc} หลังจองเงินไม่สำเร็จไม่ได้", created.Id); }
            throw;
        }
        return new CustomerCreditMovementResult(payment!.Id, payment.PaymentNumber, movement!.Id, movement.Kind, movement.Amount,
            payment.UnappliedAmount, created.Id, created.DocumentNumber,
            new[] { "สร้างใบแจ้งหนี้ร่างแล้ว — แก้รายการให้ตรงงานจริงแล้วอนุมัติ ระบบจัดสรรเงินก้อนนี้ให้ใบเองเมื่ออนุมัติ" });
    }

    /// <summary>
    /// หลังอนุมัติใบ (ผู้เรียก: <c>DocumentController.Approve</c> หลัง commit): ใบที่ออกจากเงินรอจัดสรร (มีการจอง ToInvoice) ⇒ จัดสรรให้เองผ่าน
    /// <see cref="AllocateAsync"/> (ด่านเดียวกับการกดเอง · tax point ตามเหตุผลของเงิน — <see cref="LateReceiptTaxPoint"/>) · ล้ม = ข้อความ + ป้ายบนใบ
    /// (การจองหมดผลเองเมื่อใบไม่ใช่ร่าง ⇒ ผู้ใช้กด “ใช้เงินรอจัดสรร” ต่อได้ — ไม่ค้างเงินเงียบ ๆ)
    /// </summary>
    public async Task<IReadOnlyList<string>> AfterDocumentApprovedAsync(Guid companyId, Guid documentId, string actor, CancellationToken ct = default)
    {
        var toInvoice = nameof(PaymentCreditMovementKind.ToInvoice);
        var holds = await _db.PaymentCreditMovements.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.TargetDocumentId == documentId && m.Kind == toInvoice && !m.IsDeleted && m.ReleasedAt == null)
            .OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var notices = new List<string>();
        foreach (var h in holds)
        {
            var doc = await _db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId, ct);
            if (doc == null) break;
            string release;
            try
            {
                var p = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == h.PaymentId && x.CompanyId == companyId, ct)
                    ?? throw new KeyNotFoundException("การรับชำระที่จองเงินไว้ถูกยกเลิกแล้ว");
                var available = (await AvailableFactsAsync(companyId, p, documentId, ct)).UnappliedAmount;
                var amount = Math.Round(Math.Min(h.Amount, Math.Min(doc.BalanceDue, available)), 2, MidpointRounding.AwayFromZero);
                if (amount <= UnappliedCreditBalance.Tolerance)
                    throw new BusinessRuleException("ไม่มีเงินรอจัดสรรเหลือให้ใบนี้แล้ว", CustomerCreditMovementRules.ToInvoiceRuleCode, 409);
                var r = await AllocateAsync(companyId, h.PaymentId, new AllocateCustomerCreditRequest(documentId, amount), actor, ct);
                notices.Add($"จัดสรรเงินรอจัดสรร {r.PaymentNumber} {Money(r.AllocatedAmount)} ให้ {r.DocumentNumber} แล้ว (ใบออกจากเงินก้อนนี้)");
                notices.AddRange(r.Notices);
                release = $"อนุมัติแล้ว — จัดสรร {Money(r.AllocatedAmount)} ให้ใบ";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)   // ฝ่ายค้าน L3: ทุกความล้มเหลว = ป้ายบนใบ (ใบอนุมัติ commit แล้ว)
            {
                if (ex is not (BusinessRuleException or KeyNotFoundException or InvalidOperationException))
                    _logger.LogError(ex, "จัดสรรเงินรอจัดสรรให้ใบ {Doc} หลังอนุมัติไม่สำเร็จ", documentId);
                _db.ChangeTracker.Clear();
                var msg = $"[CC-HOLD-FAILED] จัดสรรเงินรอจัดสรรให้ {doc.DocumentNumber} อัตโนมัติไม่สำเร็จ — {ex.Message} · กด “ใช้เงินรอจัดสรร” ที่ใบนี้";
                notices.Add(msg);
                release = "อนุมัติแล้วแต่จัดสรรไม่สำเร็จ: " + ex.Message;
                var current = await _db.Documents.AsNoTracking().Where(d => d.Id == documentId && d.CompanyId == companyId)
                    .Select(d => d.InternalNotes).FirstOrDefaultAsync(ct);
                var merged = DepositPolicyResolver.AppendNoteOnce(current, msg);
                await _db.Documents.Where(d => d.Id == documentId && d.CompanyId == companyId)
                    .ExecuteUpdateAsync(u => u.SetProperty(d => d.InternalNotes, merged), ct);
            }
            var hid = h.Id;
            var note = release.Length > 500 ? release[..500] : release;
            var tracked = await _db.PaymentCreditMovements.FirstOrDefaultAsync(m => m.Id == hid && m.CompanyId == companyId && m.ReleasedAt == null, ct);
            if (tracked != null)
            {
                tracked.ReleasedAt = DateTime.UtcNow;
                tracked.ReleaseNote = note;
                tracked.UpdatedAt = DateTime.UtcNow;
                tracked.UpdatedBy = actor;
                var holder = await _db.Payments.AsNoTracking().IgnoreQueryFilters().FirstAsync(p => p.Id == tracked.PaymentId && p.CompanyId == companyId, ct);
                AuditMovement(companyId, actor, tracked, holder, CustomerCreditMovementRules.ToInvoiceRuleCode, null, note);   // ฝ่ายค้าน L2
                await _db.SaveChangesAsync(ct);
            }
        }
        return notices;
    }

    // ───────────────────────── P5: ข้อเสนอของบรรทัดเงินเข้า ─────────────────────────

    public async Task<BankLineCreditSuggestionResponse> BankLineSuggestionsAsync(Guid companyId, Guid lineId, CancellationToken ct = default)
    {
        var line = await _db.BankTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == lineId && t.CompanyId == companyId && !t.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบรายการเดินบัญชี");
        var amount = Math.Abs(line.Amount);
        BankLineCreditSuggestionResponse Empty(string note)
            => new(line.Id, line.TransactionDate, amount, line.Description, new List<BankLineCreditSuggestionRow>(), note);
        if (line.TransactionType != BankTransactionType.Deposit)
            return Empty("ไม่ใช่เงินเข้า");
        if (line.ReconciliationStatus != ReconciliationStatus.Unmatched || line.MatchedPaymentId != null || line.MatchedJournalEntryId != null)
            return Empty("รายการนี้จับคู่แล้ว");

        var types = CustomerCreditAllocation.TargetTypes.ToList();
        var statuses = CustomerCreditAllocation.OpenStatuses.ToList();
        var latest = line.TransactionDate.Date.AddDays(BankReceiptAutoSettle.DocumentAfterLineDays + 1);
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && types.Contains(d.DocumentType) && statuses.Contains(d.Status)
                && d.BalanceDue > 0.005m && d.DocumentDate < latest)
            .OrderByDescending(d => d.DocumentDate).Take(2000)   // ฝ่ายค้าน L6: ใบล่าสุดก่อน (บริษัทใบเยอะต้องเห็นใบใหม่)
            .ToListAsync(ct);
        docs = docs.Where(d => CustomerCreditAllocation.IsThbDocument(d.Currency, d.ExchangeRate)).ToList();
        if (docs.Count == 0) return Empty("ไม่มีใบค้างชำระสกุลบาท");
        var lineText = $"{line.Description} {line.Reference} {line.Payee}";
        var contactIds = docs.Select(d => d.ContactId).Distinct().ToList();
        var names = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var groups = new List<BankReceiptSplitSuggestion.ContactDocs>();
        foreach (var g in docs.GroupBy(d => d.ContactId))
        {
            var name = names.GetValueOrDefault(g.Key);
            var first = g.First();
            var nameConfident = BankMatchScorer.Score(new BankMatchScorer.Input(first.BalanceDue, amount, first.DocumentDate, line.TransactionDate,
                CandidateDocNumber: first.DocumentNumber, CandidateName: name,
                BankDescription: line.Description, BankReference: line.Reference, BankPayee: line.Payee)).NameConfident;
            var cited = g.Where(d => BankReceiptAutoSettle.CitesDocumentNumber(lineText, d.DocumentNumber)).Select(d => d.Id).ToHashSet();
            if (!nameConfident && cited.Count == 0) continue;   // ไม่มีตัวระบุตัว ⇒ ไม่คำนวณยอดของใบลูกค้ารายนี้ (ตัวตัดสินไม่เสนออยู่แล้ว)
            var list = new List<BankReceiptSplitSuggestion.Doc>();
            foreach (var d in g.Take(BankReceiptSplitSuggestion.MaxDocsPerContact))
            {
                var fig = await PayLinkTransferWht.ForAsync(_db, companyId, d, d.BalanceDue, ct);
                list.Add(new BankReceiptSplitSuggestion.Doc(d.Id, d.DocumentNumber, d.DocumentDate, fig.ExpectedNet, cited.Contains(d.Id)));
            }
            groups.Add(new BankReceiptSplitSuggestion.ContactDocs(g.Key, name, nameConfident, list));
        }
        var result = BankReceiptSplitSuggestion.Suggest(amount, groups);
        var rows = result.Suggestions.Select(s => new BankLineCreditSuggestionRow(s.Kind.ToString(), s.ContactId, s.ContactName,
            s.Allocations.Select(a => new BankLineCreditAllocationRow(a.DocumentId, a.DocumentNumber, a.Amount)).ToList(),
            s.Excess, s.Reason, PayHref(s.Allocations[0].DocumentId,
                s.Kind == BankReceiptSplitSuggestion.Kind.Split ? s.Allocations[0].Amount : amount, line))).ToList();
        return new BankLineCreditSuggestionResponse(line.Id, line.TransactionDate, amount, line.Description, rows, result.Note);
    }

    /// <summary>ลิงก์เปิดหน้ารับชำระของใบแรกที่เติมยอด/วันที่/อ้างอิง/บัญชีธนาคารของบรรทัดไว้แล้ว (ผู้ใช้ตรวจแล้วกดบันทึกเอง — ไม่มีการลงบัญชีอัตโนมัติ)</summary>
    private static string PayHref(Guid documentId, decimal amount, BankTransaction line)
    {
        var reference = ((line.Reference ?? line.Description) ?? "").Trim();
        if (reference.Length > 60) reference = reference[..60];
        return "/pages/documents.html?openDoc=" + documentId
            + "&pay=1&payAmount=" + amount.ToString("0.00", CultureInfo.InvariantCulture)
            + "&payDate=" + line.TransactionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + "&payBank=" + line.BankAccountId
            + "&payRef=" + Uri.EscapeDataString(reference);
    }
}
