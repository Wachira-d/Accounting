using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Journal;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Settlement;

/// <summary>ของที่การลงบัญชีรอบโอนสร้าง 1 ชิ้น (เอกสาร / การรับชำระ) — ให้หน้าจอลิงก์ไปเปิดได้</summary>
/// <param name="Kind">"document" · "payment"</param>
/// <param name="Component">fee-{กลุ่มภาษี} · sum-yyyyMMdd · receipt</param>
public sealed record SettlementPostedItem(Guid Id, string Kind, string? Number, string Component, string Status, decimal Amount);

/// <summary>พรีวิวการลงบัญชี — แผนของ <c>SettlementBatchMath.Plan</c> + ด่านของผู้ลงบัญชี (ปัญหาทุกข้อมีทางไปต่อ)</summary>
public sealed record SettlementPostingPreview(
    Guid BatchId, SettlementBatchStatus Status, bool CanPost, SettlementPostingPlan Plan,
    IReadOnlyList<SettlementPostedItem> ExistingItems);

/// <summary>ผลการลงบัญชี — <c>AlreadyPosted</c> = ลงไว้แล้ว (กดซ้ำไม่ลงซ้ำ) · <c>Ok=false</c> = ถูกบล็อก ดู <c>Plan.Issues</c></summary>
public sealed record SettlementPostingResult(
    bool Ok, bool AlreadyPosted, string Message, SettlementBatchStatus Status, SettlementPostingPlan? Plan,
    Guid? PayoutJournalEntryId, string? PayoutJournalEntryNumber, IReadOnlyList<SettlementPostedItem> Items);

public sealed record SettlementUnpostResult(
    bool Ok, string Message, SettlementBatchStatus Status, Guid? ReversalJournalEntryId,
    IReadOnlyList<Guid> VoidedDocumentIds, IReadOnlyList<Guid> VoidedPaymentIds);

public sealed record SettlementBankMatchResult(bool Ok, string Message, SettlementBatchStatus Status, Guid? BankTransactionId);

public sealed record SettlementChargebackResult(bool Ok, string Message, Guid? JournalEntryId, string? JournalEntryNumber);

public interface ISettlementPostingService
{
    /// <summary>ดูตัวอย่างการลงบัญชี — ไม่เขียนอะไรเลย · <paramref name="userId"/> ใช้ตรวจสิทธิ์อนุมัติเอกสารที่ระบบจะสร้าง</summary>
    Task<SettlementPostingPreview> PreviewAsync(Guid companyId, Guid batchId, Guid userId, CancellationToken ct = default);

    /// <summary>ลงบัญชีรอบโอนตามแผน (ใบค่าธรรมเนียม · ใบขายสรุปรายวัน · รับชำระเข้าผังพัก · JE รอบโอน) — idempotent · ทำต่อจากขั้นที่ค้างได้</summary>
    Task<SettlementPostingResult> PostAsync(Guid companyId, Guid batchId, Guid userId, CancellationToken ct = default);

    /// <summary>ยกเลิกการลงบัญชี: ถอนการจับคู่ธนาคาร → ยกเลิกการรับชำระ/เอกสาร/50 ทวิ ผ่านเส้นปกติ → กลับรายการ JE รอบโอน (ไม่ลบแถวใด) —
    /// รอบโอนกลับเป็น <c>Matched</c> ให้แก้แล้วลงใหม่ได้ · <c>PaymentIntent.SettlementBatchId</c> คงไว้ (บรรทัดยังอ้าง intent)</summary>
    Task<SettlementUnpostResult> UnpostAsync(Guid companyId, Guid batchId, Guid userId, string reason, CancellationToken ct = default);

    /// <summary>จับคู่รอบโอนกับรายการเดินบัญชีจริง (สถานะ <c>BankMatched</c>) — เฉพาะรายการของบริษัทนี้ บัญชีเดียวกัน ทิศเดียวกัน ยอดเท่ากัน</summary>
    Task<SettlementBankMatchResult> MatchBankTransactionAsync(Guid companyId, Guid batchId, Guid bankTransactionId, Guid userId,
        CancellationToken ct = default);

    /// <summary>ปิดรายการ chargeback ที่พักไว้ (แพ้ = Dr 57140 · ชนะและได้เงินคืนนอกไฟล์ = Dr ผังพัก) ผ่าน <c>SettlementBatchMath.PlanChargebackResolution</c></summary>
    Task<SettlementChargebackResult> ResolveChargebackAsync(Guid companyId, Guid lineId, bool won, Guid userId,
        CancellationToken ct = default);
}

/// <summary>
/// **ผู้ลงบัญชีรอบโอน settlement** (รอบ 198 เฟส 1 ทีม C · DOCUMENT_FLOW §2.10) — ทำตาม <see cref="SettlementBatchMath.Plan"/> ทุกตัวอักษร
///
/// <para>═══ ทำไมไม่ใช่ "ธุรกรรมเดียวทั้งรอบ" ═══
/// เส้นสร้าง/อนุมัติ/รับชำระ/ยกเลิกของ <c>DocumentService</c> เปิดธุรกรรมของตัวเองทุกเมธอด (เรียกจากในธุรกรรมอื่นแล้ว EF โยน — ดูหมายเหตุใน
/// <c>ExpenseClaimService.MarkAsPaidAsync</c> · <c>IssueForfeitTaxInvoiceAsync</c>) และห้ามขยาย DocumentService (CLAUDE.md F4 ข้อ 7) ⇒
/// การลงบัญชีเป็น <b>ขั้น ๆ ที่ทำซ้ำได้</b>:
/// <list type="number">
/// <item>ล็อกต่อช่องทางแบบ session (<see cref="JobLock"/> · คีย์ <see cref="AdvisoryLockKey"/>) ครอบทุกขั้น — สองคนกดพร้อมกันไม่ได้ของซ้ำ</item>
/// <item>แผน + ด่าน (<see cref="SettlementPostingGate"/>) — <c>CanPost</c> เป็นเท็จ = ไม่แตะอะไรเลย</item>
/// <item>ของแต่ละชิ้นมีป้ายที่<b>บันทึกพร้อมตัวมันในคำสั่งเดียว</b> (<c>Document.CreatedBy</c> = <see cref="SettlementPostingKeys.Creator"/> ·
/// <c>Payment.Notes</c> มี <see cref="SettlementPostingKeys.PaymentMarker"/>) ⇒ ล้มกลางทางแล้วกดใหม่ = หาเจอแล้วทำต่อ ไม่สร้างซ้ำ</item>
/// <item>ขั้นสุดท้ายอยู่ในธุรกรรมเดียว (ล็อกแถวรอบโอน): JE รอบโอนผ่าน <see cref="JournalEntryBuilder"/> (Dr=Cr · ด่านงวดปิด) +
/// สถานะ <c>Posted</c> + ผูก PaymentIntent + audit (hash chain) — <c>Posted</c> ประทับเมื่อทุกชิ้นครบแล้วเท่านั้น</item>
/// </list>
/// ล้มกลางทาง = <see cref="BusinessRuleException"/> 409 ที่บอกขั้นที่ล้ม + ว่ากดใหม่ได้ · พรีวิวแสดง <c>PartialProgress</c> ⇒ ล้มดัง 3 ที่ (F2 ข้อ 7)</para>
///
/// <para>ห้าม <c>new JournalEntry</c> ใน <c>Services/Settlement/**</c> — JE ผ่าน builder (รอบโอน · chargeback) หรือเส้นกลับรายการของ
/// <c>IAccountingService</c> เท่านั้น (<c>tools/required_call_site_check.py</c>)</para>
/// </summary>
public class SettlementPostingService : ISettlementPostingService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _documents;
    private readonly IWithholdingTaxCertService _whtCerts;
    private readonly IAccountingService _accounting;
    private readonly IBankService _bank;
    private readonly IPermissionService _perms;
    private readonly IGatewayAccountResolver _gateway;
    private readonly ILogger<SettlementPostingService> _logger;

    public SettlementPostingService(AccountingDbContext db, IDocumentService documents, IWithholdingTaxCertService whtCerts,
        IAccountingService accounting, IBankService bank, IPermissionService perms, IGatewayAccountResolver gateway,
        ILogger<SettlementPostingService> logger)
    {
        _db = db; _documents = documents; _whtCerts = whtCerts; _accounting = accounting; _bank = bank; _perms = perms;
        _gateway = gateway; _logger = logger;
    }

    private sealed record Loaded(SettlementBatch Batch, SettlementChannel Channel, List<SettlementLine> Lines);

    private sealed record ExistingDoc(Guid Id, string Component, string Number, DocumentType Type, DocumentStatus Status,
        decimal Total);

    private sealed record Gate(
        Loaded Loaded, SettlementPostingPlan Plan, SettlementAccountResolution Accounts, bool VatRegistered,
        Guid? CounterpartyId, DateTime PayoutDay, List<ExistingDoc> ExistingDocs, List<Payment> SettlementPayments);

    // ═════════════════════════════ พรีวิว ═════════════════════════════

    public async Task<SettlementPostingPreview> PreviewAsync(Guid companyId, Guid batchId, Guid userId, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(companyId, batchId, ct);
        var gate = await BuildGateAsync(companyId, loaded, userId, ct);
        return new SettlementPostingPreview(batchId, loaded.Batch.Status, gate.Plan.CanPost, gate.Plan, Items(gate));
    }

    // ═════════════════════════════ ลงบัญชี ═════════════════════════════

    public async Task<SettlementPostingResult> PostAsync(Guid companyId, Guid batchId, Guid userId, CancellationToken ct = default)
    {
        var head = await HeadAsync(companyId, batchId, ct);
        SettlementPostingResult? result = null;
        var acquired = await JobLock.RunExclusiveAsync(_db, SettlementPostingKeys.LockScope, head.ChannelId.ToString("N"),
            async () => { result = await PostCoreAsync(companyId, batchId, userId, ct); }, _logger, companyId, ct);
        if (!acquired || result is null)
            return new SettlementPostingResult(false, false, BusyMessage, head.Status, null, null, null, Array.Empty<SettlementPostedItem>());
        return result;
    }

    private async Task<SettlementPostingResult> PostCoreAsync(Guid companyId, Guid batchId, Guid userId, CancellationToken ct)
    {
        var loaded = await LoadAsync(companyId, batchId, ct);
        if (loaded.Batch.Status is SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched)
            return await AlreadyPostedAsync(companyId, loaded.Batch, ct);

        var gate = await BuildGateAsync(companyId, loaded, userId, ct);
        if (!gate.Plan.CanPost)
            return new SettlementPostingResult(false, false, FirstBlocking(gate.Plan), loaded.Batch.Status, gate.Plan,
                null, null, Items(gate));

        var step = "ใบค่าธรรมเนียม";
        try
        {
            for (var i = 0; i < gate.Plan.FeeDocuments.Count; i++)
                await EnsureFeeDocumentAsync(companyId, gate, i, userId, ct);
            step = "ใบขายสรุปรายวัน";
            Guid? walkInId = null;
            foreach (var s in gate.Plan.SummarySales)
                walkInId = await EnsureSummaryDocumentAsync(companyId, gate, s, walkInId, userId, ct);
            step = "รับชำระใบขายเข้าผังพัก";
            foreach (var r in gate.Plan.Receipts)
                await EnsureReceiptAsync(companyId, gate, r, userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ล้มดัง ไม่กลืน: ของที่ทำไปแล้วยังอยู่ (มีป้ายของรอบโอน) · กดใหม่ = ทำต่อจากขั้นที่ค้าง
            _logger.LogError(ex, "ลงบัญชีรอบโอน {Batch} ล้มที่ขั้น {Step}", batchId, step);
            throw new BusinessRuleException(
                $"ลงบัญชีรอบโอน {loaded.Batch.PayoutRef} ไม่สำเร็จที่ขั้น \"{step}\": {ex.Message} — เอกสาร/การรับชำระที่ทำไปแล้วยังอยู่ "
                + "(สถานะรอบโอนยังไม่เป็น \"ลงบัญชีแล้ว\") · แก้สาเหตุแล้วกด \"ลงบัญชี\" อีกครั้ง ระบบจะทำต่อจากขั้นที่ค้าง ไม่สร้างซ้ำ",
                ex, "SETTLEMENT-POST-PARTIAL", 409);
        }

        return await CommitPostedAsync(companyId, batchId, gate, userId, ct);
    }

    /// <summary>ใบสำคัญจ่ายค่าธรรมเนียม 1 ใบต่อกลุ่มภาษี (จ่ายจากผังพัก) + 50 ทวิ ของ WHT ที่ JE รอบโอนตั้งไว้ — ห้ามหัก WHT บนใบ/ตอนจ่ายซ้ำ</summary>
    private async Task EnsureFeeDocumentAsync(Guid companyId, Gate gate, int index, Guid userId, CancellationToken ct)
    {
        var batch = gate.Loaded.Batch;
        var fee = gate.Plan.FeeDocuments[index];
        var component = SettlementPostingKeys.FeeComponent(fee.VatTreatment);
        var request = SettlementDocumentBuilder.FeeDocument(fee, gate.Accounts.FeeLineAccounts[index], gate.CounterpartyId!.Value,
            gate.Accounts.ClearingAccountId!.Value, gate.PayoutDay, batch.PayoutRef, gate.Loaded.Channel.DisplayName);
        var docId = await CreateOrAdoptAsync(companyId, gate, component, request, ct);
        await ApproveIfDraftAsync(companyId, docId, userId);

        var cert = SettlementDocumentBuilder.WhtCertificate(fee, gate.CounterpartyId!.Value, docId, gate.PayoutDay, batch.PayoutRef);
        if (cert != null && !await _db.WithholdingTaxCerts.AnyAsync(w => w.CompanyId == companyId && w.DocumentId == docId
                && !w.IsDeleted && w.Status != WithholdingTaxCertStatus.Voided, ct))
        {
            // ท.ป.4/2528: ออกหนังสือรับรองในวันจ่าย · คำตัดสินเจ้าของรอบ 170 (ค): 50 ทวิ ออกเป็น Issued ทุกทางเข้า
            var created = await _whtCerts.CreateAsync(companyId, cert, userId.ToString());
            await _whtCerts.IssueAsync(companyId, created.Id);
        }
    }

    /// <summary>ใบขายสรุปรายวัน (DECISIONS ข้อ 2–3) — สร้าง + ติดป้ายให้ตรวจ + อนุมัติ (เงินเข้าผังพักในใบเดียว)</summary>
    private async Task<Guid?> EnsureSummaryDocumentAsync(Guid companyId, Gate gate, SettlementSummarySalePlan s, Guid? walkInId,
        Guid userId, CancellationToken ct)
    {
        var batch = gate.Loaded.Batch;
        var component = SettlementPostingKeys.SummaryComponent(s.Date);
        walkInId ??= (await WalkInCustomerContact.GetOrCreateAsync(_db, companyId, ct)).Id;
        var request = SettlementDocumentBuilder.SummaryDocument(s, gate.VatRegistered, walkInId.Value,
            gate.Accounts.ClearingAccountId!.Value, batch.PayoutRef, gate.Loaded.Channel.DisplayName);
        var docId = await CreateOrAdoptAsync(companyId, gate, component, request, ct);

        // ธงให้ตรวจรายได้ซ้ำ — ต่อท้าย InternalNotes (ไม่พิมพ์ลงกระดาษ · ห้ามเขียนทับของเดิม) ก่อนอนุมัติ
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == docId && d.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบใบขายสรุปที่เพิ่งสร้าง");
        if (!(doc.InternalNotes ?? "").Contains(SettlementDocumentBuilder.SummaryReviewTag, StringComparison.Ordinal))
        {
            var note = SettlementDocumentBuilder.SummaryReviewNote(s, batch.PayoutRef, gate.Loaded.Channel.DisplayName);
            doc.InternalNotes = string.IsNullOrWhiteSpace(doc.InternalNotes) ? note : doc.InternalNotes.TrimEnd() + "\n\n" + note;
            await _db.SaveChangesAsync(ct);
        }
        await ApproveIfDraftAsync(companyId, docId, userId);
        return walkInId;
    }

    private async Task EnsureReceiptAsync(Guid companyId, Gate gate, SettlementReceiptPlan r, Guid userId, CancellationToken ct)
    {
        if (gate.SettlementPayments.Any(p => p.DocumentId == r.DocumentId)) return;   // ลงไว้แล้วครั้งก่อน
        var batch = gate.Loaded.Batch;
        var request = SettlementDocumentBuilder.ReceiptPayment(r, batch.Id, gate.Accounts.ClearingAccountId!.Value,
            gate.PayoutDay, batch.PayoutRef, gate.Loaded.Channel.DisplayName);
        await _documents.CreatePaymentAsync(companyId, request, userId.ToString());
    }

    /// <summary>หาเอกสารของชิ้นนี้ที่ลงไว้ครั้งก่อน (ป้าย CreatedBy) — ยอดต้องตรงแผนปัจจุบัน · ไม่มี ⇒ สร้างผ่าน <c>IDocumentService</c>
    /// แล้วตรวจยอดที่ได้กับแผน (กัน VAT/ยอดถูกคิดใหม่เพี้ยนจากแผน — review198-A R-A11)</summary>
    private async Task<Guid> CreateOrAdoptAsync(Guid companyId, Gate gate, string component,
        Models.DTOs.Document.CreateDocumentRequest request, CancellationToken ct)
    {
        var expected = request.Lines.Sum(l => Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero)
            + (l.VatAmountOverride ?? 0m));
        var existing = gate.ExistingDocs.FirstOrDefault(d => d.Component == component);
        if (existing != null)
        {
            if (Math.Abs(existing.Total - expected) > 0.005m)
                throw new BusinessRuleException(
                    $"เอกสาร {existing.Number} ที่สร้างจากการลงบัญชีครั้งก่อนมียอด {existing.Total:N2} ไม่ตรงแผนปัจจุบัน {expected:N2} "
                    + "(บรรทัดของรอบโอนถูกแก้ระหว่างนั้น) — ยกเลิกเอกสารนั้นก่อน แล้วลงบัญชีใหม่", "SETTLEMENT-POST-STALE", 409);
            return existing.Id;
        }
        var created = await _documents.CreateDocumentAsync(companyId, request,
            SettlementPostingKeys.Creator(gate.Loaded.Batch.Id, component));
        if (Math.Abs(created.TotalAmount - expected) > 0.005m)
            throw new BusinessRuleException(
                $"เอกสาร {created.DocumentNumber} ที่ระบบสร้างมียอด {created.TotalAmount:N2} ไม่เท่าแผน {expected:N2} — ยกเลิก/ลบเอกสารร่างนั้น "
                + "แล้วแจ้งผู้ดูแลระบบ (ยอดตามแผนคือยอดที่แพลตฟอร์มหัก/โอนจริง)", "SETTLEMENT-POST-AMOUNT", 409);
        gate.ExistingDocs.Add(new ExistingDoc(created.Id, component, created.DocumentNumber, created.DocumentType, created.Status,
            created.TotalAmount));
        return created.Id;
    }

    /// <summary>อนุมัติผ่าน <c>ApproveDocumentAsync</c> ในนามผู้กดลงบัญชี — คำเตือนผ่านแบบ "ระบบส่งผ่าน" (ไม่ประทับว่าคนรับทราบ ·
    /// ApprovalAcknowledgement) · สิทธิ์อนุมัติตรวจแล้วที่ด่าน (<c>DocumentPermissionHelper.CanApproveAsync</c>)</summary>
    private async Task ApproveIfDraftAsync(Guid companyId, Guid docId, Guid userId)
    {
        var doc = await _documents.GetDocumentAsync(companyId, docId);
        if (doc.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
            doc = await _documents.ApproveDocumentAsync(companyId, docId, userId.ToString(), ApprovalAckSource.SystemWorkflow, false);
        if (!DocumentStatusRules.IsIssued(doc.Status) || doc.Status == DocumentStatus.Voided)
            throw new BusinessRuleException($"เอกสาร {doc.DocumentNumber} อยู่สถานะ {doc.Status} — อนุมัติไม่สำเร็จ", "SETTLEMENT-POST-APPROVE", 409);
    }

    /// <summary>ขั้นสุดท้าย (ธุรกรรมเดียว · ล็อกแถวรอบโอน): JE รอบโอน + Posted + ผูก PaymentIntent + audit</summary>
    private async Task<SettlementPostingResult> CommitPostedAsync(Guid companyId, Guid batchId, Gate gate, Guid userId,
        CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"SettlementBatches\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE",
                new object[] { batchId, companyId }, ct);
            var batch = await _db.SettlementBatches
                .FirstOrDefaultAsync(b => b.Id == batchId && b.CompanyId == companyId && !b.IsDeleted, ct)
                ?? throw new KeyNotFoundException("ไม่พบรอบโอน");
            if (batch.Status is SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched)
            {
                await tx.RollbackAsync(ct);
                return await AlreadyPostedAsync(companyId, batch, ct);
            }

            var net = batch.NetPayout;
            JournalEntry? je = null;
            if (gate.Accounts.Journal.Count > 0)
            {
                var builder = JournalEntryBuilder.For(_db, companyId, gate.PayoutDay)
                    .Type(net > 0m ? JournalType.CashReceipts : net < 0m ? JournalType.CashPayments : JournalType.General)
                    .NumberPrefix(net > 0m ? "RV" : net < 0m ? "PV" : "JV")
                    .Description($"รอบโอน {batch.PayoutRef} — {gate.Loaded.Channel.DisplayName}")
                    .Reference(batch.PayoutRef)
                    .CreatedBy(userId.ToString());
                foreach (var l in gate.Accounts.Journal)
                {
                    builder.Debit(l.AccountId, l.Debit, l.Description);
                    builder.Credit(l.AccountId, l.Credit, l.Description);
                }
                try
                {
                    je = await builder.PostAsync(userId.ToString(), ct);   // Dr=Cr + ด่านงวดปิด (ตัวเดียวกับพรีวิว)
                }
                catch (InvalidOperationException ex)
                {
                    await tx.RollbackAsync(ct);
                    return new SettlementPostingResult(false, false,
                        ex.Message + " — เอกสารของรอบโอนที่สร้างไว้แล้วยังอยู่ · แก้แล้วกด \"ลงบัญชี\" อีกครั้งเพื่อลง JE รอบโอนต่อ",
                        batch.Status, gate.Plan, null, null, Items(gate));
                }
            }

            var docs = await ExistingDocsAsync(companyId, batchId, ct);
            var pays = await SettlementPaymentsAsync(companyId, batchId, ct);
            var docIds = docs.Select(d => d.Id).ToList();
            batch.Status = SettlementBatchStatus.Posted;
            batch.PayoutJournalEntryId = je?.Id;
            batch.PostedAt = DateTime.UtcNow;
            batch.PostedBy = userId.ToString();
            batch.FeeDocumentIdsJson = JsonSerializer.Serialize(docIds);
            batch.UpdatedAt = DateTime.UtcNow;
            batch.UpdatedBy = userId.ToString();

            // บรรทัดขายที่ไปอยู่ในใบสรุป — ติดสถานะ (ไม่แตะ MatchedDocumentId: แผนใช้ช่องนั้นแยก "รับชำระ" ออกจาก "ใบสรุป")
            var summaryLineIds = gate.Plan.SummarySales.SelectMany(s => s.LineIds).ToHashSet();
            if (summaryLineIds.Count > 0)
                foreach (var line in await _db.SettlementLines
                             .Where(l => l.CompanyId == companyId && l.BatchId == batchId && summaryLineIds.Contains(l.Id))
                             .ToListAsync(ct))
                    line.MatchStatus = SettlementMatchStatus.AutoSummary;

            var intentIds = gate.Loaded.Lines.Where(l => l.PaymentIntentId is not null).Select(l => l.PaymentIntentId!.Value)
                .Distinct().ToList();
            if (intentIds.Count > 0)
                foreach (var intent in await _db.PaymentIntents
                             .Where(i => i.CompanyId == companyId && intentIds.Contains(i.Id)).ToListAsync(ct))
                    intent.SettlementBatchId = batch.Id;

            var paymentIds = pays.Select(p => p.Id).ToList();
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                EntityType = nameof(SettlementBatch),
                EntityId = batch.Id.ToString(),
                Action = AuditAction.Approve,
                NewValues = JsonSerializer.Serialize(new
                {
                    action = "settlement-post",
                    payoutRef = batch.PayoutRef,
                    netPayout = batch.NetPayout,
                    journalEntry = je?.EntryNumber,
                    documents = docs.Select(d => new { d.Id, d.Number, d.Component }),
                    payments = paymentIds,
                    warnings = gate.Plan.Issues.Where(i => !i.Blocking).Select(i => i.Code.ToString()),
                }),
                Timestamp = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _logger.LogInformation("ลงบัญชีรอบโอน {Ref} ({Channel}) บริษัท {Company}: JE {Je} · เอกสาร {Docs} · รับชำระ {Pays}",
                batch.PayoutRef, gate.Loaded.Channel.DisplayName, companyId, je?.EntryNumber, docIds.Count, paymentIds.Count);
            return new SettlementPostingResult(true, false,
                $"ลงบัญชีรอบโอน {batch.PayoutRef} แล้ว" + (je != null ? $" — ใบสำคัญ {je.EntryNumber}" : ""),
                batch.Status, gate.Plan, je?.Id, je?.EntryNumber,
                docs.Select(x => ItemOf(x)).Concat(pays.Select(x => ItemOf(x))).ToList());
        });
    }

    private async Task<SettlementPostingResult> AlreadyPostedAsync(Guid companyId, SettlementBatch batch, CancellationToken ct)
    {
        string? jeNo = null;
        if (batch.PayoutJournalEntryId is Guid jeId)
            jeNo = await _db.JournalEntries.AsNoTracking()
                .Where(j => j.Id == jeId && j.CompanyId == companyId).Select(j => j.EntryNumber).FirstOrDefaultAsync(ct);
        var docs = await ExistingDocsAsync(companyId, batch.Id, ct);
        var pays = await SettlementPaymentsAsync(companyId, batch.Id, ct);
        var items = docs.Select(x => ItemOf(x)).Concat(pays.Select(x => ItemOf(x))).ToList();
        return new SettlementPostingResult(true, true,
            $"รอบโอน {batch.PayoutRef} ลงบัญชีไว้แล้ว — ไม่ลงซ้ำ" + (jeNo != null ? $" (ใบสำคัญ {jeNo})" : ""),
            batch.Status, null, batch.PayoutJournalEntryId, jeNo, items);
    }

    // ═════════════════════════════ ด่าน (ข้อเท็จจริงจากฐาน → ตัวตัดสินบริสุทธิ์) ═════════════════════════════

    private async Task<Gate> BuildGateAsync(Guid companyId, Loaded loaded, Guid userId, CancellationToken ct)
    {
        var (batch, channel, lines) = loaded;
        var vatRegistered = await CompanyVatStatus.IsRegisteredAsync(_db, companyId, ct);
        var plan = SettlementBatchMath.Plan(batch, lines, channel, vatRegistered);
        var payoutDay = ThaiDate.CalendarDateUtc(batch.PayoutDate);
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);

        // งวดบัญชี — ตัวเดียวกับที่ JournalEntryBuilder.PostAsync ใช้บล็อก
        var payoutClosed = await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, payoutDay, ct);
        var summaryClosed = new Dictionary<DateTime, string>();
        foreach (var s in plan.SummarySales)
            if (await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, s.Date, ct) is string r)
                summaryClosed[s.Date] = r;

        // เดือนภาษีที่ประกาศว่ายื่นแล้ว (TaxFilingLockPolicy ตัวเดียว)
        var filed = await _db.TaxReports.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted
                && (t.TaxType == TaxType.VAT || t.TaxType == TaxType.WithholdingTax53)
                && TaxFilingLockPolicy.DeclaredOrFiledStatuses.Contains(t.Status))
            .Select(t => new { t.TaxType, t.Year, t.Month }).ToListAsync(ct);
        var filedVat = filed.Where(f => f.TaxType == TaxType.VAT).Select(f => (f.Year, f.Month)).ToHashSet();
        var filedWht = filed.Where(f => f.TaxType == TaxType.WithholdingTax53).Select(f => (f.Year, f.Month)).ToHashSet();

        // ผังของบริษัทนี้เท่านั้น — id ที่ไม่อยู่ในนี้ = คนละบริษัท ⇒ ตัวหาผังปฏิเสธ
        var chart = new SettlementChartIndex(await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .Select(a => new SettlementChartAccount(a.Id, a.AccountCode, a.IsActive)).ToListAsync(ct));
        Guid? bankGl = null;
        if (batch.BankAccountId is Guid bankId)
            bankGl = await _db.BankAccounts.AsNoTracking()
                .Where(b => b.Id == bankId && b.CompanyId == companyId && !b.IsDeleted)
                .Select(b => b.LinkedAccountId).FirstOrDefaultAsync(ct);
        var accounts = SettlementAccountResolver.Resolve(plan, chart, bankGl);

        // ผู้รับเงินค่าธรรมเนียม
        var counterparty = channel.CounterpartyContactId is Guid cpId
            ? await _db.Contacts.AsNoTracking()
                .Where(c => c.Id == cpId && c.CompanyId == companyId && !c.IsDeleted)
                .Select(c => new { c.Id, c.TaxId }).FirstOrDefaultAsync(ct)
            : null;

        // ของที่ลงไว้ครั้งก่อน (ค้างครึ่งทาง)
        var existingDocs = await ExistingDocsAsync(companyId, batch.Id, ct);
        var payments = await SettlementPaymentsAsync(companyId, batch.Id, ct);

        // ใบขายที่จะรับชำระ
        var receiptIds = plan.Receipts.Select(r => r.DocumentId).ToList();
        var receiptDocs = receiptIds.Count == 0
            ? new List<SettlementReceiptTarget>()
            : (await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == companyId && !d.IsDeleted && receiptIds.Contains(d.Id))
                    .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.Status, d.BalanceDue }).ToListAsync(ct))
                .Select(d => new SettlementReceiptTarget(d.Id, true, d.DocumentNumber, d.DocumentType, d.Status, d.BalanceDue,
                    payments.Any(p => p.DocumentId == d.Id)))
                .ToList();

        var clearingSources = await ClearingSourcesAsync(companyId, batch, lines, ct);
        var duplicates = await DuplicateSalesAsync(companyId, batch, channel, plan, ct);

        var saleType = vatRegistered ? DocumentType.TaxInvoice : DocumentType.Receipt;
        var facts = new SettlementPostingFacts(
            batch.Status, payoutDay, today, payoutClosed, summaryClosed, filedVat, filedWht, accounts.Errors,
            counterparty != null, !string.IsNullOrWhiteSpace(counterparty?.TaxId), channel.ClearingAccountId,
            receiptDocs, clearingSources, duplicates,
            await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, DocumentType.PaymentVoucher),
            await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, saleType),
            existingDocs.Count + payments.Count);
        var gated = SettlementPostingGate.Evaluate(plan, facts);

        // เอกสารจากการลงบัญชีครั้งก่อนที่ไม่อยู่ในแผนปัจจุบัน (บรรทัดถูกแก้ระหว่างนั้น) — ห้ามปล่อยค้างเงียบ
        var planned = gated.FeeDocuments.Select(f => SettlementPostingKeys.FeeComponent(f.VatTreatment))
            .Concat(gated.SummarySales.Select(s => SettlementPostingKeys.SummaryComponent(s.Date))).ToHashSet();
        var stale = existingDocs.Where(d => !planned.Contains(d.Component)).ToList();
        if (stale.Count > 0 && batch.Status is not (SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched))
            gated = gated with
            {
                CanPost = false,
                Issues = gated.Issues.Append(new SettlementPlanIssue(SettlementPlanIssueCode.StaleDocument, true,
                    $"เอกสารจากการลงบัญชีครั้งก่อนไม่อยู่ในแผนปัจจุบัน: {string.Join(", ", stale.Select(d => d.Number))}",
                    "ยกเลิกเอกสารเหล่านั้นก่อน (บรรทัดของรอบโอนถูกแก้หลังลงบัญชีค้างครึ่งทาง) แล้วดูตัวอย่างใหม่",
                    Array.Empty<Guid>(), null)).ToList(),
            };

        return new Gate(loaded, gated, accounts, vatRegistered, counterparty?.Id, payoutDay, existingDocs, payments);
    }

    /// <summary>บรรทัดที่แผนนับว่าอยู่ในผังพักแล้ว — เงินก้อนนั้นลงไว้ที่ผังไหนจริง (review198-A R-A1: gateway ลง 11340 ผ่าน
    /// <c>IGatewayAccountResolver</c> · ช่องทางที่ผูกผังพักอื่นจะทำให้ 11340 ค้างตลอดไป) · ถูกล้างด้วยเส้นอื่นแล้วหรือยัง</summary>
    private async Task<List<SettlementClearingSource>> ClearingSourcesAsync(Guid companyId, SettlementBatch batch,
        List<SettlementLine> lines, CancellationToken ct)
    {
        var result = new List<SettlementClearingSource>();
        var counted = lines.Where(l => l.Amount != 0m
            && SettlementLineTypeRules.For(l.LineType).Posting is SettlementPostingKind.SaleComponent or SettlementPostingKind.Refund
            && (l.PaymentIntentId is not null || l.PaymentId is not null)).ToList();
        if (counted.Count == 0) return result;

        var intentIds = counted.Where(l => l.PaymentIntentId is not null).Select(l => l.PaymentIntentId!.Value).Distinct().ToList();
        var intents = intentIds.Count == 0 ? new List<PaymentIntent>()
            : await _db.PaymentIntents.AsNoTracking()
                .Where(i => i.CompanyId == companyId && intentIds.Contains(i.Id)).ToListAsync(ct);
        foreach (var id in intentIds)
        {
            var ids = counted.Where(l => l.PaymentIntentId == id).Select(l => l.Id).ToList();
            var intent = intents.FirstOrDefault(i => i.Id == id);
            if (intent == null)
            {
                result.Add(new SettlementClearingSource(ids, "รายการรับชำระออนไลน์ (PaymentIntent)", false, null, false));
                continue;
            }
            var posted = await _gateway.ResolveMoneyInAccountAsync(intent, ct);
            var elsewhere = intent.SettlementJournalEntryId is not null
                || (intent.SettlementBatchId is Guid b && b != batch.Id);
            result.Add(new SettlementClearingSource(ids, $"รายการรับชำระออนไลน์ {intent.ProviderRef ?? intent.Id.ToString()}",
                true, posted, elsewhere));
        }

        var paymentIds = counted.Where(l => l.PaymentIntentId is null && l.PaymentId is not null)
            .Select(l => l.PaymentId!.Value).Distinct().ToList();
        if (paymentIds.Count > 0)
        {
            var pays = await _db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && paymentIds.Contains(p.Id))
                .Select(p => new { p.Id, p.PaymentNumber, p.OverridePaymentAccountId }).ToListAsync(ct);
            var usedElsewhere = (await (from l in _db.SettlementLines.AsNoTracking()
                                        join b in _db.SettlementBatches.AsNoTracking() on l.BatchId equals b.Id
                                        where l.CompanyId == companyId && b.CompanyId == companyId && !l.IsDeleted && !b.IsDeleted
                                              && l.BatchId != batch.Id && l.PaymentId != null && paymentIds.Contains(l.PaymentId.Value)
                                              && b.Status != SettlementBatchStatus.Voided
                                        select l.PaymentId!.Value).ToListAsync(ct)).ToHashSet();
            foreach (var id in paymentIds)
            {
                var ids = counted.Where(l => l.PaymentIntentId is null && l.PaymentId == id).Select(l => l.Id).ToList();
                var p = pays.FirstOrDefault(x => x.Id == id);
                result.Add(p == null
                    ? new SettlementClearingSource(ids, "การรับ/จ่ายชำระ", false, null, false)
                    : new SettlementClearingSource(ids, $"การรับ/จ่ายชำระ {p.PaymentNumber}", true, p.OverridePaymentAccountId,
                        usedElsewhere.Contains(id)));
            }
        }
        return result;
    }

    /// <summary>หลักฐานรายได้ซ้ำของใบสรุป (review198-A R-A7): (1) ใบสรุปวันเดียวกันของช่องทางนี้จากรอบโอนอื่น (DECISIONS ข้อ 2 = 1 ใบ/วัน/แพลตฟอร์ม)
    /// (2) ออเดอร์เดียวกันลงบัญชีไปแล้วในรอบโอนอื่นของช่องทางนี้ (3) ออเดอร์มีเอกสารขายของตัวเอง (อ้างเลขออเดอร์)</summary>
    private async Task<List<SettlementDuplicateSale>> DuplicateSalesAsync(Guid companyId, SettlementBatch batch,
        SettlementChannel channel, SettlementPostingPlan plan, CancellationToken ct)
    {
        var result = new List<SettlementDuplicateSale>();
        if (plan.SummarySales.Count == 0) return result;

        var channelBatches = (await _db.SettlementBatches.AsNoTracking()
                .Where(b => b.CompanyId == companyId && b.ChannelId == channel.Id && !b.IsDeleted && b.Id != batch.Id
                    && b.Status != SettlementBatchStatus.Voided)
                .Select(b => new { b.Id, b.PayoutRef }).ToListAsync(ct))
            .ToDictionary(b => b.Id.ToString("N"), b => b.PayoutRef);
        foreach (var s in plan.SummarySales)
        {
            var suffix = ":" + SettlementPostingKeys.SummaryComponent(s.Date);
            var same = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status != DocumentStatus.Voided
                    && d.CreatedBy != null && d.CreatedBy.StartsWith("system:settlement:") && d.CreatedBy.EndsWith(suffix))
                .Select(d => new { d.DocumentNumber, d.CreatedBy }).ToListAsync(ct);
            foreach (var d in same)
            {
                var batchPart = d.CreatedBy!.Length >= 50 ? d.CreatedBy.Substring(18, 32) : "";
                if (channelBatches.TryGetValue(batchPart, out var otherRef))
                    result.Add(new SettlementDuplicateSale(s.LineIds,
                        $"มีใบขายสรุปวันที่ {ThaiDate.ToThaiDisplayString(s.Date)} ของ {channel.DisplayName} แล้ว ({d.DocumentNumber} จากรอบโอน {otherRef}) — "
                        + "ออกอีกใบ = รายได้และภาษีขายของวันนั้นซ้ำ"));
            }
        }

        var orderLines = plan.SummarySales.SelectMany(s => s.LineIds).ToHashSet();
        var orders = (await _db.SettlementLines.AsNoTracking()
                .Where(l => l.CompanyId == companyId && l.BatchId == batch.Id && l.ExternalOrderId != null)
                .Select(l => new { l.Id, l.ExternalOrderId }).ToListAsync(ct))
            .Where(l => orderLines.Contains(l.Id) && !string.IsNullOrWhiteSpace(l.ExternalOrderId))
            .GroupBy(l => l.ExternalOrderId!.Trim()).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
        foreach (var chunk in orders.Keys.Chunk(500))
        {
            var keys = chunk.ToList();
            var postedElsewhere = await (from l in _db.SettlementLines.AsNoTracking()
                                         join b in _db.SettlementBatches.AsNoTracking() on l.BatchId equals b.Id
                                         where l.CompanyId == companyId && b.CompanyId == companyId && !l.IsDeleted && !b.IsDeleted
                                               && b.ChannelId == channel.Id && b.Id != batch.Id
                                               && (b.Status == SettlementBatchStatus.Posted || b.Status == SettlementBatchStatus.BankMatched)
                                               && l.LineType == SettlementLineType.Sale && l.ExternalOrderId != null
                                               && keys.Contains(l.ExternalOrderId)
                                         select new { l.ExternalOrderId, b.PayoutRef }).ToListAsync(ct);
            foreach (var g in postedElsewhere.GroupBy(x => x.ExternalOrderId!))
                result.Add(new SettlementDuplicateSale(orders[g.Key],
                    $"ออเดอร์ {g.Key} ลงรายได้ไปแล้วในรอบโอน {string.Join(", ", g.Select(x => x.PayoutRef).Distinct())} ของช่องทางนี้"));
            var invoiced = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status != DocumentStatus.Voided
                    && (d.DocumentType == DocumentType.TaxInvoice || d.DocumentType == DocumentType.Invoice
                        || d.DocumentType == DocumentType.Receipt)
                    && d.Reference != null && keys.Contains(d.Reference))
                .Select(d => new { d.Reference, d.DocumentNumber }).ToListAsync(ct);
            foreach (var d in invoiced)
                result.Add(new SettlementDuplicateSale(orders[d.Reference!],
                    $"ออเดอร์ {d.Reference} มีเอกสารขาย {d.DocumentNumber} ของตัวเองแล้ว (เช่นลูกค้าขอใบกำกับเต็มรูป) — รวมเข้าใบสรุปอีก = รายได้ซ้ำ"));
        }
        return result;
    }

    // ═════════════════════════════ ยกเลิกการลงบัญชี ═════════════════════════════

    public async Task<SettlementUnpostResult> UnpostAsync(Guid companyId, Guid batchId, Guid userId, string reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new SettlementUnpostResult(false, "กรุณาระบุเหตุผลที่ยกเลิกการลงบัญชี (ผู้สอบบัญชีต้องเห็นว่าแก้เพราะอะไร)",
                (await HeadAsync(companyId, batchId, ct)).Status, null, Array.Empty<Guid>(), Array.Empty<Guid>());
        var head = await HeadAsync(companyId, batchId, ct);
        SettlementUnpostResult? result = null;
        var acquired = await JobLock.RunExclusiveAsync(_db, SettlementPostingKeys.LockScope, head.ChannelId.ToString("N"),
            async () => { result = await UnpostCoreAsync(companyId, batchId, userId, reason.Trim(), ct); }, _logger, companyId, ct);
        return acquired && result is not null ? result
            : new SettlementUnpostResult(false, BusyMessage, head.Status, null, Array.Empty<Guid>(), Array.Empty<Guid>());
    }

    private async Task<SettlementUnpostResult> UnpostCoreAsync(Guid companyId, Guid batchId, Guid userId, string reason,
        CancellationToken ct)
    {
        var loaded = await LoadAsync(companyId, batchId, ct);
        var batch = loaded.Batch;
        SettlementUnpostResult Fail(string m) => new(false, m, batch.Status, null, Array.Empty<Guid>(), Array.Empty<Guid>());
        if (batch.Status is not (SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched))
            return Fail("รอบโอนนี้ยังไม่ได้ลงบัญชี — ไม่มีอะไรให้ยกเลิก");

        var docs = await ExistingDocsAsync(companyId, batchId, ct);
        var payments = await SettlementPaymentsAsync(companyId, batchId, ct);
        foreach (var t in docs.Select(d => d.Type).Distinct())
            if (!await DocumentPermissionHelper.CanVoidAsync(_perms, companyId, userId, t))
                return Fail($"ผู้ใช้นี้ไม่มีสิทธิ์ยกเลิกเอกสารชนิด {t} ที่รอบโอนนี้สร้าง — ให้ผู้มีสิทธิ์ยกเลิกเอกสารเป็นผู้กด");

        // chargeback ที่ปิดไปแล้วหลังลงบัญชี — ต้องกลับรายการก่อน (ไม่งั้นผังพัก/57140 ค้าง)
        var cbRefs = loaded.Lines.Where(l => l.LineType == SettlementLineType.Chargeback)
            .Select(l => SettlementPostingKeys.ChargebackReference(l.Id)).ToList();
        if (cbRefs.Count > 0)
        {
            var cb = await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && !j.IsDeleted && j.ReversedByEntryId == null
                    && j.Reference != null && cbRefs.Contains(j.Reference))
                .Select(j => j.EntryNumber).ToListAsync(ct);
            if (cb.Count > 0)
                return Fail($"มี JE ปิดรายการ chargeback ของรอบโอนนี้อยู่ ({string.Join(", ", cb)}) — กลับรายการ JE นั้นที่หน้าสมุดรายวันก่อน");
        }

        // ด่านงวดก่อนแตะอะไร — ยกเลิกเอกสาร/กลับ JE ลงวันที่ของต้นฉบับ
        JournalEntry? payoutJe = batch.PayoutJournalEntryId is Guid jeId
            ? await _db.JournalEntries.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jeId && j.CompanyId == companyId, ct)
            : null;
        var dates = docs.Select(d => d.Id).ToList();
        var docDates = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && dates.Contains(d.Id)).Select(d => d.DocumentDate).ToListAsync(ct);
        foreach (var d in docDates.Append(payoutJe?.EntryDate ?? ThaiDate.CalendarDateUtc(batch.PayoutDate)).Distinct())
            if (await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, d, ct) is string closed)
                return Fail(closed + " — ยกเลิกการลงบัญชีรอบโอนนี้ไม่ได้จนกว่าจะเปิดงวด (หรือบันทึกรายการปรับปรุงในงวดปัจจุบันแทน)");

        var voidedPayments = new List<Guid>();
        var voidedDocs = new List<Guid>();
        try
        {
            // 1) ถอนการจับคู่ธนาคาร (เจ้าของการจับคู่ = IBankService)
            if (batch.BankTransactionId is Guid txnId)
            {
                var txn = await _db.Set<BankTransaction>().AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == txnId && t.CompanyId == companyId, ct);
                if (txn is { ReconciliationStatus: ReconciliationStatus.Matched } && txn.MatchedJournalEntryId == batch.PayoutJournalEntryId)
                    await _bank.UnmatchTransactionAsync(companyId, new UnmatchRequest(txnId));
            }
            // 2) ยกเลิกการรับชำระ (เส้นปกติ: กลับ JE + คืนยอดใบ)
            foreach (var p in payments)
            {
                await _documents.VoidPaymentAsync(companyId, p.Id);
                voidedPayments.Add(p.Id);
            }
            // 3) ยกเลิก 50 ทวิ + เอกสาร (เส้นปกติ: reversal JE ลงวันที่ของเอกสาร · ด่านรายงานภาษีที่ยื่นแล้วอยู่ในเส้นนั้น)
            foreach (var d in docs)
            {
                var certIds = await _db.WithholdingTaxCerts.AsNoTracking()
                    .Where(w => w.CompanyId == companyId && w.DocumentId == d.Id && !w.IsDeleted
                        && w.Status != WithholdingTaxCertStatus.Voided)
                    .Select(w => w.Id).ToListAsync(ct);
                foreach (var certId in certIds) await _whtCerts.VoidAsync(companyId, certId);
                await _documents.VoidDocumentAsync(companyId, d.Id);
                voidedDocs.Add(d.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ยกเลิกการลงบัญชีรอบโอน {Batch} ล้มกลางทาง", batchId);
            throw new BusinessRuleException(
                $"ยกเลิกการลงบัญชีรอบโอน {batch.PayoutRef} ไม่สำเร็จ: {ex.Message} — ยกเลิกไปแล้ว {voidedPayments.Count} การรับชำระ · "
                + $"{voidedDocs.Count} เอกสาร (สถานะรอบโอนยังเป็นลงบัญชีแล้ว) · แก้สาเหตุแล้วกดยกเลิกอีกครั้ง ระบบทำต่อจากที่ค้าง",
                ex, "SETTLEMENT-UNPOST-PARTIAL", 409);
        }

        // 4) ธุรกรรมเดียว: กลับรายการ JE รอบโอน (เส้นกลางของ IAccountingService) + สถานะ + audit
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"SettlementBatches\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE",
                new object[] { batchId, companyId }, ct);
            var tracked = await _db.SettlementBatches
                .FirstOrDefaultAsync(b => b.Id == batchId && b.CompanyId == companyId && !b.IsDeleted, ct)
                ?? throw new KeyNotFoundException("ไม่พบรอบโอน");
            Guid? reversalId = null;
            if (payoutJe != null && payoutJe.ReversedByEntryId == null)
            {
                var rev = await _accounting.ReverseJournalEntryAsync(companyId, payoutJe.Id, payoutJe.EntryDate,
                    $"กลับรายการรอบโอน {tracked.PayoutRef} — {reason}", systemTriggered: true);
                reversalId = rev.Id;
            }
            var oldJe = tracked.PayoutJournalEntryId;
            var oldDocs = tracked.FeeDocumentIdsJson;
            tracked.Status = SettlementBatchStatus.Matched;
            tracked.PayoutJournalEntryId = null;
            tracked.BankTransactionId = null;
            tracked.PostedAt = null;
            tracked.PostedBy = null;
            tracked.FeeDocumentIdsJson = null;
            tracked.UpdatedAt = DateTime.UtcNow;
            tracked.UpdatedBy = userId.ToString();
            // PaymentIntent.SettlementBatchId คงไว้: บรรทัดของรอบโอนยังอ้าง intent อยู่ (ทีม B ผูกตั้งแต่นำเข้า) — ปล่อยคืน = เส้นรอบโอน gateway เดิม
            // หยิบ intent เดียวกันไปล้างผังพักซ้ำ (ธนาคารเกินสองเท่า) · ปล่อยคืนได้เมื่อรอบโอนถูกยกเลิก/ลบ (เจ้าของ = ผู้นำเข้า)
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                EntityType = nameof(SettlementBatch),
                EntityId = batchId.ToString(),
                Action = AuditAction.Update,
                OldValues = JsonSerializer.Serialize(new { payoutJournalEntryId = oldJe, feeDocumentIds = oldDocs }),
                NewValues = JsonSerializer.Serialize(new
                {
                    action = "settlement-unpost",
                    reason,
                    reversalJournalEntryId = reversalId,
                    voidedDocuments = voidedDocs,
                    voidedPayments,
                }),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new SettlementUnpostResult(true,
                $"ยกเลิกการลงบัญชีรอบโอน {tracked.PayoutRef} แล้ว — แก้รายการแล้วลงบัญชีใหม่ได้", tracked.Status, reversalId,
                voidedDocs, voidedPayments);
        });
    }

    // ═════════════════════════════ จับคู่ธนาคาร ═════════════════════════════

    public async Task<SettlementBankMatchResult> MatchBankTransactionAsync(Guid companyId, Guid batchId, Guid bankTransactionId,
        Guid userId, CancellationToken ct = default)
    {
        var head = await HeadAsync(companyId, batchId, ct);
        SettlementBankMatchResult? result = null;
        var acquired = await JobLock.RunExclusiveAsync(_db, SettlementPostingKeys.LockScope, head.ChannelId.ToString("N"),
            async () => { result = await MatchCoreAsync(companyId, batchId, bankTransactionId, userId, ct); }, _logger, companyId, ct);
        return acquired && result is not null ? result
            : new SettlementBankMatchResult(false, BusyMessage, head.Status, null);
    }

    private async Task<SettlementBankMatchResult> MatchCoreAsync(Guid companyId, Guid batchId, Guid bankTransactionId,
        Guid userId, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"SettlementBatches\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE",
                new object[] { batchId, companyId }, ct);
            var batch = await _db.SettlementBatches
                .FirstOrDefaultAsync(b => b.Id == batchId && b.CompanyId == companyId && !b.IsDeleted, ct)
                ?? throw new KeyNotFoundException("ไม่พบรอบโอน");

            var txn = await _db.Set<BankTransaction>().AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == bankTransactionId && t.CompanyId == companyId && !t.IsDeleted, ct);
            var linkedBatch = await _db.SettlementBatches.AsNoTracking()
                .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.BankTransactionId == bankTransactionId && b.Id != batchId)
                .Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
            var facts = txn == null
                ? new SettlementBankTxnFacts(false, Guid.Empty, BankTransactionType.Deposit, 0m, ReconciliationStatus.Unmatched, null, linkedBatch)
                : new SettlementBankTxnFacts(true, txn.BankAccountId, txn.TransactionType, txn.Amount, txn.ReconciliationStatus,
                    txn.MatchedJournalEntryId, linkedBatch);
            var decision = SettlementBankMatch.Check(batch.Status, batch.Id, batch.NetPayout, batch.BankAccountId,
                batch.PayoutJournalEntryId, batch.BankTransactionId, bankTransactionId, facts);
            if (!decision.Ok || decision.AlreadyDone)
            {
                await tx.RollbackAsync(ct);
                return new SettlementBankMatchResult(decision.Ok, decision.Message, batch.Status, batch.BankTransactionId);
            }

            // ฝั่งรายการเดินบัญชี: เจ้าของการจับคู่ตัวเดียว (ตรวจงวด · ยอดกับขาธนาคารของ JE · กันจับซ้ำ · เก็บคลังเรียนรู้)
            if (decision.NeedsReconcile)
                await _bank.ReconcileAsync(companyId, new ReconcileRequest(bankTransactionId, null, batch.PayoutJournalEntryId));

            batch.BankTransactionId = bankTransactionId;
            batch.Status = SettlementBatchStatus.BankMatched;
            batch.UpdatedAt = DateTime.UtcNow;
            batch.UpdatedBy = userId.ToString();
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                EntityType = nameof(SettlementBatch),
                EntityId = batchId.ToString(),
                Action = AuditAction.Update,
                NewValues = JsonSerializer.Serialize(new
                {
                    action = "settlement-bank-match",
                    bankTransactionId,
                    amount = batch.NetPayout,
                    journalEntryId = batch.PayoutJournalEntryId,
                }),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new SettlementBankMatchResult(true, "จับคู่รอบโอนกับรายการเดินบัญชีแล้ว", batch.Status, bankTransactionId);
        });
    }

    // ═════════════════════════════ chargeback ═════════════════════════════

    public async Task<SettlementChargebackResult> ResolveChargebackAsync(Guid companyId, Guid lineId, bool won, Guid userId,
        CancellationToken ct = default)
    {
        var line = await _db.SettlementLines.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == lineId && l.CompanyId == companyId && !l.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบบรรทัดของรอบโอน");
        var loaded = await LoadAsync(companyId, line.BatchId, ct);
        if (line.LineType != SettlementLineType.Chargeback)
            return new SettlementChargebackResult(false, "บรรทัดนี้ไม่ใช่รายการ chargeback", null, null);
        if (loaded.Batch.Status is not (SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched))
            return new SettlementChargebackResult(false,
                "ลงบัญชีรอบโอนก่อน — ยอด chargeback ถูกพักไว้ที่ผังพัก dispute ตอนลงบัญชีรอบโอน", null, null);

        var reference = SettlementPostingKeys.ChargebackReference(lineId);
        var done = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && !j.IsDeleted && j.Reference == reference && j.ReversedByEntryId == null)
            .Select(j => new { j.Id, j.EntryNumber }).FirstOrDefaultAsync(ct);
        if (done != null)
            return new SettlementChargebackResult(true, $"ปิดรายการ chargeback นี้ไว้แล้ว (ใบสำคัญ {done.EntryNumber}) — ไม่ลงซ้ำ",
                done.Id, done.EntryNumber);

        var plan = SettlementBatchMath.PlanChargebackResolution(line.Amount, won, loaded.Channel, reference);
        var chart = new SettlementChartIndex(await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .Select(a => new SettlementChartAccount(a.Id, a.AccountCode, a.IsActive)).ToListAsync(ct));
        var (resolved, errors) = SettlementAccountResolver.ResolveJournal(plan, chart, null);
        if (errors.Count > 0)
            return new SettlementChargebackResult(false, string.Join(" · ", errors), null, null);

        var day = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            // กันกดซ้ำพร้อมกัน — ล็อกต่อบรรทัด แล้วตรวจซ้ำใต้ล็อก
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, SettlementPostingKeys.LockScope, reference) }, ct);
            if (await _db.JournalEntries.AnyAsync(j => j.CompanyId == companyId && !j.IsDeleted && j.Reference == reference
                    && j.ReversedByEntryId == null, ct))
            {
                await tx.RollbackAsync(ct);
                return new SettlementChargebackResult(true, "ปิดรายการ chargeback นี้ไว้แล้ว — ไม่ลงซ้ำ", null, null);
            }
            var builder = JournalEntryBuilder.For(_db, companyId, day)
                .Type(JournalType.General)
                .Description($"{(won ? "ชนะ" : "แพ้")} chargeback — {loaded.Channel.DisplayName} รอบโอน {loaded.Batch.PayoutRef}")
                .Reference(reference)
                .CreatedBy(userId.ToString());
            foreach (var l in resolved)
            {
                builder.Debit(l.AccountId, l.Debit, l.Description);
                builder.Credit(l.AccountId, l.Credit, l.Description);
            }
            JournalEntry je;
            try
            {
                je = await builder.PostAsync(userId.ToString(), ct);
            }
            catch (InvalidOperationException ex)
            {
                await tx.RollbackAsync(ct);
                return new SettlementChargebackResult(false, ex.Message, null, null);
            }
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                EntityType = nameof(SettlementLine),
                EntityId = lineId.ToString(),
                Action = AuditAction.Update,
                NewValues = JsonSerializer.Serialize(new
                {
                    action = "settlement-chargeback-resolve",
                    won,
                    amount = Math.Abs(line.Amount),
                    journalEntry = je.EntryNumber,
                }),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new SettlementChargebackResult(true,
                $"ปิดรายการ chargeback ({(won ? "ชนะ — ได้เงินคืนเข้าผังพัก" : "แพ้ — ลงขาดทุนจาก chargeback")}) แล้ว — ใบสำคัญ {je.EntryNumber}"
                + (won ? " · ถ้าแพลตฟอร์มคืนเงินในไฟล์รอบโอนถัดไปด้วย ให้จัดประเภทบรรทัดนั้นเป็น \"ปรับปรุงอื่น\" ไม่ใช่ \"ชนะ chargeback\" (กันนับซ้ำ)" : ""),
                je.Id, je.EntryNumber);
        });
    }

    // ═════════════════════════════ ตัวช่วยอ่าน ═════════════════════════════

    private const string BusyMessage =
        "มีการลงบัญชี/ยกเลิก/จับคู่ของช่องทางนี้กำลังทำอยู่ — รอสักครู่แล้วกดใหม่ (ระบบไม่ทำซ้อนกันเพื่อกันลงบัญชีซ้ำ)";

    private sealed record Head(Guid ChannelId, SettlementBatchStatus Status);

    private async Task<Head> HeadAsync(Guid companyId, Guid batchId, CancellationToken ct)
        => await _db.SettlementBatches.AsNoTracking()
               .Where(b => b.Id == batchId && b.CompanyId == companyId && !b.IsDeleted)
               .Select(b => new Head(b.ChannelId, b.Status)).FirstOrDefaultAsync(ct)
           ?? throw new KeyNotFoundException("ไม่พบรอบโอน");

    private async Task<Loaded> LoadAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var batch = await _db.SettlementBatches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == batchId && b.CompanyId == companyId && !b.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบรอบโอน");
        var channel = await _db.SettlementChannels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == batch.ChannelId && c.CompanyId == companyId && !c.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบช่องทางของรอบโอนนี้");
        var lines = await _db.SettlementLines.AsNoTracking()
            .Where(l => l.BatchId == batchId && l.CompanyId == companyId && !l.IsDeleted)
            .OrderBy(l => l.Seq).ToListAsync(ct);
        return new Loaded(batch, channel, lines);
    }

    /// <summary>เอกสารที่รอบโอนนี้สร้าง (ป้าย CreatedBy · ยังไม่ถูกยกเลิก)</summary>
    private async Task<List<ExistingDoc>> ExistingDocsAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var prefix = SettlementPostingKeys.CreatorPrefix(batchId);
        return (await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status != DocumentStatus.Voided
                    && d.CreatedBy != null && d.CreatedBy.StartsWith(prefix))
                .Select(d => new { d.Id, d.CreatedBy, d.DocumentNumber, d.DocumentType, d.Status, d.TotalAmount })
                .ToListAsync(ct))
            .Select(d => new ExistingDoc(d.Id, d.CreatedBy!.Substring(prefix.Length), d.DocumentNumber, d.DocumentType, d.Status,
                d.TotalAmount))
            .ToList();
    }

    /// <summary>การรับชำระที่รอบโอนนี้บันทึก (ป้ายใน Notes · ยังไม่ถูกยกเลิก)</summary>
    private Task<List<Payment>> SettlementPaymentsAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var marker = SettlementPostingKeys.PaymentMarker(batchId);
        return _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.Notes != null && p.Notes.Contains(marker))
            .ToListAsync(ct);
    }

    private static IReadOnlyList<SettlementPostedItem> Items(Gate gate)
        => gate.ExistingDocs.Select(x => ItemOf(x)).Concat(gate.SettlementPayments.Select(x => ItemOf(x))).ToList();

    private static SettlementPostedItem ItemOf(ExistingDoc d)
        => new(d.Id, "document", d.Number, d.Component, d.Status.ToString(), d.Total);

    private static SettlementPostedItem ItemOf(Payment p)
        => new(p.Id, "payment", p.PaymentNumber, "receipt", "Recorded", p.Amount);

    private static string FirstBlocking(SettlementPostingPlan plan)
    {
        var first = plan.Issues.FirstOrDefault(i => i.Blocking);
        return first == null ? "ลงบัญชีไม่ได้" : $"{first.Message} — {first.NextStep}";
    }
}
