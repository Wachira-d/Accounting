using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
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
/// <param name="Accounts">ผังที่จะลงจริงของทุกขา (รหัส+ชื่อ · รวมผังที่ตั้งทับ/ผังของบัญชีธนาคาร/ผังพัก) — ตัวหาผังตัวเดียวกับการลงจริง (review198-D D-02)</param>
/// <param name="BankAccountLabel">บัญชีธนาคารที่รับเงินของรอบนี้ (ชื่อธนาคาร + เลขบัญชี) · null = ยังไม่ได้เลือก (D-03)</param>
/// <param name="Orphans">ของกำพร้าของช่องทางนี้รายชิ้น (กอง · เหตุ · ผู้/เวลา/เหตุผลที่รับรู้ · ปุ่มรับรู้) — ตัวแยกเดียวกับด่านลงบัญชี
/// (<see cref="SettlementOrphanTriage"/> · รอบ 200 DECISIONS ข้อ 10) · ว่าง = ไม่มีของกำพร้า</param>
public sealed record SettlementPostingPreview(
    Guid BatchId, SettlementBatchStatus Status, bool CanPost, SettlementPostingPlan Plan,
    IReadOnlyList<SettlementPostedItem> ExistingItems,
    SettlementPlanAccounts? Accounts = null,
    string? BankAccountLabel = null,
    IReadOnlyList<SettlementOrphanItem>? Orphans = null);

/// <summary>ผลการลงบัญชี — <c>AlreadyPosted</c> = ลงไว้แล้ว (กดซ้ำไม่ลงซ้ำ) · <c>Ok=false</c> = ถูกบล็อก ดู <c>Plan.Issues</c></summary>
public sealed record SettlementPostingResult(
    bool Ok, bool AlreadyPosted, string Message, SettlementBatchStatus Status, SettlementPostingPlan? Plan,
    Guid? PayoutJournalEntryId, string? PayoutJournalEntryNumber, IReadOnlyList<SettlementPostedItem> Items);

public sealed record SettlementUnpostResult(
    bool Ok, string Message, SettlementBatchStatus Status, Guid? ReversalJournalEntryId,
    IReadOnlyList<Guid> VoidedDocumentIds, IReadOnlyList<Guid> VoidedPaymentIds);

public sealed record SettlementBankMatchResult(bool Ok, string Message, SettlementBatchStatus Status, Guid? BankTransactionId);

public sealed record SettlementChargebackResult(bool Ok, string Message, Guid? JournalEntryId, string? JournalEntryNumber);

/// <summary>ผลการรับรู้ของกำพร้า (รอบ 200 · DECISIONS ข้อ 10) — <c>Ok=false</c> = ปฏิเสธพร้อมเหตุ/ทางไปต่อ (ไม่ได้บันทึกอะไร) · <c>Ack</c> = ผู้/เวลา/เหตุผลที่ประทับ</summary>
public sealed record SettlementOrphanAckResult(bool Ok, string Message, SettlementOrphanAck? Ack);

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

    /// <summary>chargeback ที่ปิดผลแล้ว (มี JE ปิดรายการที่ยังไม่ถูกกลับ) ของบรรทัดที่ระบุ — ข้อเท็จจริงชุดเดียวกับด่าน "ปิดไว้แล้ว" ของ
    /// <see cref="ResolveChargebackAsync"/> ให้หน้าจอตัดปุ่มแพ้/ชนะ และแสดงผลที่ปิดไว้ (review198-D D-04) · ไม่เขียนอะไร</summary>
    Task<IReadOnlyList<SettlementClosedChargeback>> ClosedChargebacksAsync(Guid companyId, IReadOnlyCollection<Guid> lineIds,
        CancellationToken ct = default);

    /// <summary>เหตุที่ยกเลิกการลงบัญชีรอบนี้ไม่ได้ (e-Tax ตอบรับ · ภาษีที่ยื่นแล้ว · 50 ทวิ ที่ยื่นแล้ว) — ด่านเดียวกับ <see cref="UnpostAsync"/>
    /// (<c>SettlementUnpostGate.Evaluate</c> ข้อเท็จจริงชุดเดียวกัน) ให้หน้าจอบอกก่อนกด · ไม่เขียนอะไร · รอบที่ยังไม่ลงบัญชี = ว่าง (ฝ่ายค้าน C-2)</summary>
    Task<IReadOnlyList<SettlementUnpostRefusal>> UnpostBlockersAsync(Guid companyId, Guid batchId, CancellationToken ct = default);

    /// <summary>
    /// **รับรู้ของกำพร้า** (รอบ 200 · DECISIONS ข้อ 10) — เอกสาร/การรับชำระของรอบโอนที่ถูกยกเลิกแล้วซึ่ง<b>ยกเลิกไม่ได้จริง</b> (e-Tax ตอบรับ · รายงานล็อก ·
    /// 50 ทวิ ยื่นแล้ว · ใบที่อ้างมันยกเลิกไม่ได้) ⇒ ประทับผู้/เวลา/เหตุผลบนแถว + audit chain ⇒ ไม่บล็อกการลงบัญชีของช่องทางนั้นอีก ·
    /// ต้องมีสิทธิ์ <c>Settlement.Post</c> (ตรวจใน service) + เหตุผล · กองที่ยังยกเลิกได้ (ทันที/เมื่อทำขั้นก่อน) = ปฏิเสธพร้อมทางไปต่อ · รับรู้ไว้แล้ว = ตอบซ้ำไม่บันทึกซ้ำ
    /// </summary>
    Task<SettlementOrphanAckResult> AcknowledgeOrphanAsync(Guid companyId, Guid artifactId, bool isPayment, Guid userId, string? reason,
        Guid? checkedBatchId,
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

    /// <param name="ExpectedTotals">ยอดของเอกสารแต่ละชิ้นที่ตรวจกับแผนแล้วในการลงบัญชีครั้งนี้ (<c>CreateOrAdoptAsync</c>) —
    /// ขั้นสุดท้ายใช้ตรวจความครบ (<see cref="SettlementPostingCompleteness"/> · ฝ่ายค้าน C-5)</param>
    /// <param name="SupplementOf">วันของใบขายสรุป → เลขใบแรกของวันเดียวกันจากรอบโอนอื่น (ใบสรุปเพิ่มเติม · คำตัดสินรอบ 200 ข้อ 15)</param>
    private sealed record Gate(
        Loaded Loaded, SettlementPostingPlan Plan, SettlementAccountResolution Accounts, bool VatRegistered,
        Guid? CounterpartyId, DateTime PayoutDay, List<ExistingDoc> ExistingDocs, List<Payment> SettlementPayments,
        Dictionary<string, decimal> ExpectedTotals, IReadOnlyDictionary<DateTime, string> SupplementOf,
        IReadOnlyList<SettlementOrphanItem>? OrphanItems = null);

    // ═════════════════════════════ พรีวิว ═════════════════════════════

    public async Task<SettlementPostingPreview> PreviewAsync(Guid companyId, Guid batchId, Guid userId, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(companyId, batchId, ct);
        var gate = await BuildGateAsync(companyId, loaded, userId, ct);
        // D-03: บอกผู้กดลงบัญชีว่าขาธนาคารลงบัญชีไหน (ชื่อธนาคาร + เลขบัญชี) — ผังของบัญชีนั้นอยู่ใน Accounts
        string? bankLabel = null;
        if (loaded.Batch.BankAccountId is Guid bankId)
            bankLabel = await _db.BankAccounts.AsNoTracking()
                .Where(b => b.Id == bankId && b.CompanyId == companyId)
                .Select(b => b.BankName + " " + b.AccountNumber + " (" + b.AccountName + ")")
                .FirstOrDefaultAsync(ct);
        return new SettlementPostingPreview(batchId, loaded.Batch.Status, gate.Plan.CanPost, gate.Plan, Items(gate),
            gate.Accounts.Described, bankLabel, gate.OrphanItems ?? Array.Empty<SettlementOrphanItem>());
    }

    // ═════════════════════════════ ลงบัญชี ═════════════════════════════

    public async Task<SettlementPostingResult> PostAsync(Guid companyId, Guid batchId, Guid userId, CancellationToken ct = default)
    {
        var head = await HeadAsync(companyId, batchId, ct);
        SettlementPostingResult? result = null;
        var acquired = await JobLock.RunExclusiveAsync(_db, SettlementChannelLock.Scope, SettlementChannelLock.Part(head.ChannelId),
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
        var docId = await CreateOrAdoptAsync(companyId, gate, component, request, userId, ct);
        await ApproveIfDraftAsync(companyId, docId, userId);

        var cert = SettlementDocumentBuilder.WhtCertificate(fee, gate.CounterpartyId!.Value, docId, gate.PayoutDay, batch.PayoutRef);
        if (cert == null) return;
        // ฝ่ายค้าน C-3: ร่างที่ค้างจากการล้มระหว่างสร้างกับออก ต้องถูกออกตอนทำต่อ (ร่างไม่นับเข้า ภ.ง.ด.53/ยอดนำส่ง) · ยอดไม่ตรงแผน = ล้มดัง
        var existing = await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.DocumentId == docId && !w.IsDeleted && w.Status != WithholdingTaxCertStatus.Voided)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new { w.Id, w.Status, w.TotalTaxAmount, w.CertificateNumber }).FirstOrDefaultAsync(ct);
        var expected = cert.Lines.Sum(l => l.TaxAmount);
        switch (SettlementWhtCertResume.Decide(existing?.Status, existing?.TotalTaxAmount, expected))
        {
            case SettlementWhtCertStep.Create:
                // ท.ป.4/2528: ออกหนังสือรับรองในวันจ่าย · คำตัดสินเจ้าของรอบ 170 (ค): 50 ทวิ ออกเป็น Issued ทุกทางเข้า
                var created = await _whtCerts.CreateAsync(companyId, cert, userId.ToString());
                await _whtCerts.IssueAsync(companyId, created.Id);
                break;
            case SettlementWhtCertStep.IssueDraft:
                await _whtCerts.IssueAsync(companyId, existing!.Id);
                break;
            case SettlementWhtCertStep.Stale:
                throw new BusinessRuleException(
                    $"หนังสือรับรองหัก ณ ที่จ่าย {existing!.CertificateNumber} จากการลงบัญชีครั้งก่อนยอด {existing.TotalTaxAmount:N2} ไม่ตรงแผนปัจจุบัน "
                    + $"{expected:N2} — ยกเลิกหนังสือรับรองนั้นก่อน แล้วกดลงบัญชีอีกครั้ง", "SETTLEMENT-POST-STALE", 409);
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
            gate.Accounts.ClearingAccountId!.Value, batch.PayoutRef, gate.Loaded.Channel.DisplayName,
            gate.SupplementOf.TryGetValue(s.Date, out var firstOfDay) ? firstOfDay : null);
        var docId = await CreateOrAdoptAsync(companyId, gate, component, request, userId, ct);

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
        // T-1 (ฝ่ายค้านรอบ 200 · DECISIONS ข้อ 27): WHT ของใบตัดสินจากข้อเท็จจริงสด ณ ตอนรับชำระ (ตัวเดียวกับด่าน) — ห้ามส่ง 0 เงียบ
        var target = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == r.DocumentId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new { d.WithholdingTaxAmount, d.BalanceDue }).FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("ไม่พบใบขายที่จับคู่ไว้ในบริษัทนี้ — ดูตัวอย่างการลงบัญชีใหม่", "SETTLEMENT-RECEIPT-MISSING", 404);
        var wht = SettlementReceiptWht.Decide(target.WithholdingTaxAmount, target.BalanceDue, r.Amount);
        var request = SettlementDocumentBuilder.ReceiptPayment(r, batch.Id, gate.Accounts.ClearingAccountId!.Value,
            gate.PayoutDay, batch.PayoutRef, gate.Loaded.Channel.DisplayName, wht);
        await _documents.CreatePaymentAsync(companyId, request, userId.ToString());
    }

    /// <summary>หาเอกสารของชิ้นนี้ที่ลงไว้ครั้งก่อน (ป้าย CreatedBy) — ยอดต้องตรงแผนปัจจุบัน · ไม่มี ⇒ สร้างผ่าน <c>IDocumentService</c>
    /// แล้วตรวจยอดที่ได้กับแผน (กัน VAT/ยอดถูกคิดใหม่เพี้ยนจากแผน — review198-A R-A11)</summary>
    private async Task<Guid> CreateOrAdoptAsync(Guid companyId, Gate gate, string component,
        Models.DTOs.Document.CreateDocumentRequest request, Guid userId, CancellationToken ct)
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
            gate.ExpectedTotals[component] = expected;
            return existing.Id;
        }
        // ป้ายของรอบโอนอยู่ใน CreatedBy (กุญแจทำต่อจากที่ค้าง — บันทึกพร้อมเอกสารในคำสั่งเดียว) · ผู้อนุมัติของใบที่อนุมัติทันทีตอนสร้าง
        // (ใบสำคัญจ่ายเงินสด) = คนที่กดลงบัญชี — คำตัดสินเจ้าของข้อ 7 (review198-C C-7 · เดิมเป็นป้ายของระบบ ⇒ SoD ไม่เคยทำงาน)
        var created = await _documents.CreateDocumentAsync(companyId, request,
            SettlementPostingKeys.Creator(gate.Loaded.Batch.Id, component), autoApproveBy: userId.ToString());
        if (Math.Abs(created.TotalAmount - expected) > 0.005m)
            throw new BusinessRuleException(
                $"เอกสาร {created.DocumentNumber} ที่ระบบสร้างมียอด {created.TotalAmount:N2} ไม่เท่าแผน {expected:N2} — ยกเลิกเอกสารนั้น "
                + "(ใบสำคัญจ่ายค่าธรรมเนียมถูกอนุมัติตอนสร้างแล้ว · ใบร่างให้ลบ) แล้วแจ้งผู้ดูแลระบบ (ยอดตามแผนคือยอดที่แพลตฟอร์มหัก/โอนจริง)",
                "SETTLEMENT-POST-AMOUNT", 409);
        gate.ExistingDocs.Add(new ExistingDoc(created.Id, component, created.DocumentNumber, created.DocumentType, created.Status,
            created.TotalAmount));
        gate.ExpectedTotals[component] = expected;
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
            // review198-C C-20: ตัวติดตามว่างทุกครั้งที่เริ่ม — ถ้าวันหนึ่งเปิด retry ของ execution strategy รอบที่สองต้องไม่บันทึกของค้างจากรอบแรกซ้ำ (JE ซ้ำ)
            _db.ChangeTracker.Clear();
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

            // ฝ่ายค้าน C-1(c): คิดแผนใหม่ใต้ล็อกแถว — บรรทัด/หัวรอบโอนถูกแก้ระหว่างลงบัญชี ⇒ ห้ามประทับ Posted ด้วยแผนที่ไม่ตรงบรรทัดแล้ว
            // (ล็อกต่อช่องทางตัวเดียวกันกันไว้แล้ว — ที่นี่คือตาข่ายชั้นที่สอง)
            var channelNow = await _db.SettlementChannels.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == batch.ChannelId && c.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบช่องทางของรอบโอนนี้");
            var linesNow = await _db.SettlementLines.AsNoTracking()
                .Where(l => l.BatchId == batchId && l.CompanyId == companyId && !l.IsDeleted).OrderBy(l => l.Seq).ToListAsync(ct);
            var replanned = SettlementBatchMath.Plan(batch, linesNow, channelNow, gate.VatRegistered);
            if (SettlementPlanFingerprint.Of(replanned) != SettlementPlanFingerprint.Of(gate.Plan))
            {
                await tx.RollbackAsync(ct);
                return new SettlementPostingResult(false, false,
                    "บรรทัดหรือหัวของรอบโอนถูกแก้ระหว่างที่ระบบลงบัญชี — ยังไม่ประทับ \"ลงบัญชีแล้ว\" · ดูตัวอย่างใหม่แล้วกด \"ลงบัญชี\" อีกครั้ง "
                    + "(เอกสารที่สร้างไว้แล้วถูกตรวจกับแผนใหม่ ถ้าไม่ตรงระบบจะบอกให้ยกเลิกใบนั้นก่อน)",
                    batch.Status, gate.Plan, null, null, Items(gate));
            }

            // ฝ่ายค้าน C-5: ทุกชิ้นของแผนต้องมีอยู่จริง ออกแล้ว ยอดตรง — ห้ามประทับ Posted จากแค่ "ของที่เหลืออยู่"
            var docs = await ExistingDocsAsync(companyId, batchId, ct);
            var pays = await SettlementPaymentsAsync(companyId, batchId, ct);
            var missing = SettlementPostingCompleteness.Missing(gate.Plan, gate.ExpectedTotals,
                docs.Select(d => new SettlementPostedDocumentFact(d.Component, d.Number, d.Status, d.Total)).ToList(),
                pays.Select(MarkerOf).ToList());
            if (missing.Count > 0)
            {
                await tx.RollbackAsync(ct);
                return new SettlementPostingResult(false, false,
                    "ยังไม่ประทับ \"ลงบัญชีแล้ว\" — ชิ้นของแผนไม่ครบ: " + string.Join(" · ", missing)
                    + " · กด \"ลงบัญชี\" อีกครั้ง (ระบบสร้างชิ้นที่ขาดให้) หรือยกเลิกชิ้นที่ไม่ตรงก่อน",
                    batch.Status, gate.Plan, null, null, Items(gate));
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
                    // รอบแรกเป็นเจ้าของยอดขาย (กติกาเดียวกับ SyncIntentStampsAsync ของผู้นำเข้า) — บรรทัดคืนเงินภายหลังห้ามย้ายเจ้าของ
                    intent.SettlementBatchId ??= batch.Id;

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

        // ผู้รับเงินค่าธรรมเนียม — ทั้งแถว: ตัวเลือกแบบ ภ.ง.ด. ของ 50 ทวิ ต้องเห็นข้อมูลชุดเดียวกับตอนออกใบรับรอง (C-11)
        var counterparty = channel.CounterpartyContactId is Guid cpId
            ? await _db.Contacts.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == cpId && c.CompanyId == companyId && !c.IsDeleted, ct)
            : null;
        // review198-C C-11: แบบที่ 50 ทวิ ของรอบนี้จะเป็นจริง — ตัวเลือกตัวเดียวกับ WithholdingTaxCertService.CreateAsync (ผู้รับบุคคลธรรมดา ⇒ ภ.ง.ด.3)
        // ฝ่ายค้าน W-1 (ทีม WF): ตัวตั้งเดียวกับด่าน pure — ขา ภ.ง.ด.54 ของแผน (ช่องทางต่างประเทศ) ชนะ · ในประเทศ ⇒ ตัวเลือกของ 50 ทวิ
        var domesticWhtForm = Accounting.Services.Implementations.WithholdingTaxCertService
            .ResolveWhtFormType(counterparty, TaxType.WithholdingTax53).formType;
        var whtForm = SettlementForeignWht.GateWhtForm(plan, domesticWhtForm);

        // เดือนภาษีที่ประกาศว่ายื่นแล้ว (TaxFilingLockPolicy ตัวเดียว) — ภ.พ.30 · แบบ ภ.ง.ด. ของ 50 ทวิ รอบนี้ · ภ.พ.36 (C-11)
        var filed = await _db.TaxReports.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted
                && (t.TaxType == TaxType.VAT || t.TaxType == whtForm || t.TaxType == TaxType.VatPp36)
                && TaxFilingLockPolicy.DeclaredOrFiledStatuses.Contains(t.Status))
            .Select(t => new { t.TaxType, t.Year, t.Month }).ToListAsync(ct);
        var filedVat = filed.Where(f => f.TaxType == TaxType.VAT).Select(f => (f.Year, f.Month)).ToHashSet();
        var filedWht = filed.Where(f => f.TaxType == whtForm).Select(f => (f.Year, f.Month)).ToHashSet();
        var filedPp36 = filed.Where(f => f.TaxType == TaxType.VatPp36).Select(f => (f.Year, f.Month)).ToHashSet();

        // ผังของบริษัทนี้เท่านั้น — id ที่ไม่อยู่ในนี้ = คนละบริษัท ⇒ ตัวหาผังปฏิเสธ
        var chart = new SettlementChartIndex(await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .Select(a => new SettlementChartAccount(a.Id, a.AccountCode, a.IsActive, a.AccountName)).ToListAsync(ct));
        Guid? bankGl = null;
        if (batch.BankAccountId is Guid bankId)
            bankGl = await _db.BankAccounts.AsNoTracking()
                .Where(b => b.Id == bankId && b.CompanyId == companyId && !b.IsDeleted)
                .Select(b => b.LinkedAccountId).FirstOrDefaultAsync(ct);
        var accounts = SettlementAccountResolver.Resolve(plan, chart, bankGl);

        // ของที่ลงไว้ครั้งก่อน (ค้างครึ่งทาง)
        var existingDocs = await ExistingDocsAsync(companyId, batch.Id, ct);
        var payments = await SettlementPaymentsAsync(companyId, batch.Id, ct);

        // ใบขายที่จะรับชำระ + ยอดที่รอบโอนอื่นที่ยังไม่ลงบัญชีจับคู่ใบเดียวกันไว้ (review198-B R-B13)
        var receiptIds = plan.Receipts.Select(r => r.DocumentId).ToList();
        var pendingElsewhere = await PendingReceiptsElsewhereAsync(companyId, batch.Id, receiptIds, ct);
        var receiptDocs = receiptIds.Count == 0
            ? new List<SettlementReceiptTarget>()
            : (await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == companyId && !d.IsDeleted && receiptIds.Contains(d.Id))
                    .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.Status, d.BalanceDue, d.WithholdingTaxAmount }).ToListAsync(ct))
                .Select(d =>
                {
                    pendingElsewhere.TryGetValue(d.Id, out var pend);
                    // T-1 (DECISIONS ข้อ 27): WHT ลูกค้าของใบถึงด่าน — รับบางส่วนของใบที่มี WHT ⇒ บล็อก · รับยอดสุทธิครบ ⇒ บันทึก WHT ของใบ
                    return new SettlementReceiptTarget(d.Id, true, d.DocumentNumber, d.DocumentType, d.Status, d.BalanceDue,
                        payments.Any(p => p.DocumentId == d.Id), pend?.Amount ?? 0m, pend?.PayoutRefs, d.WithholdingTaxAmount);
                })
                .ToList();

        var clearingSources = await ClearingSourcesAsync(companyId, batch, lines, ct);
        var (duplicates, supplementary) = await DuplicateSalesAsync(companyId, batch, channel, plan, ct);
        // T-2 (ฝ่ายค้านรอบ 200): ข้อเท็จจริง "เนื้อหาตรงรอบโอนอื่น" ตัวเดียวกับผู้นำเข้า — ใบเพิ่มเติมที่ทุกบรรทัดตรงรอบที่ออกใบแรก = ไฟล์ซ้ำ ⇒ บล็อก
        var contentHits = await SettlementContentOverlap.ForBatchAsync(_db, companyId, batch.ChannelId, batch.Id, lines, ct);
        var (supDuplicates, supKept) = SettlementSummarySupplement.SplitDuplicates(supplementary, contentHits);
        duplicates.AddRange(supDuplicates);
        supplementary = supKept;
        var wallet = await WalletContinuityAsync(companyId, batch, ct);
        // review198-C C-15: ใบสรุปไม่ตัดสต็อก — ตัวตัดสิน "กิจการถือสต็อกไหม" ตัวเดียวของระบบ (InventoryIndustry)
        var stock = SettlementStockStance.NotChecked;
        if (plan.SummarySales.Count > 0)
            stock = SettlementStock.StanceOf(await _db.Companies.AsNoTracking().Where(c => c.Id == companyId)
                .Select(c => (IndustryType?)c.IndustryType).FirstOrDefaultAsync(ct));
        var orphans = await OrphanArtifactsAsync(companyId, batch.Id, channel.Id, batch.PayoutDate,
            new SettlementOrphanCurrentBatch(batch.Id, batch.PayoutRef, batch.CreatedAt), ct);
        // ฝ่ายค้าน C-4: การรับชำระที่ค้างจากครั้งก่อนต้องตรงแผนปัจจุบัน (ใบเดียวกัน · ยอดเท่ากัน)
        var staleReceipts = SettlementReceiptReconcile.Stale(plan.Receipts, payments.Select(MarkerOf).ToList());

        // ฝ่ายค้าน C-6: หัวของใบขายสรุป (ลูกค้าเงินสด) — ตัวตัดสินสิทธิ์ §86/6 ตัวเดียว ช่องทางเอกสาร (ไม่พบแถว SiteSettings = บังคับ)
        var summaryBlock = AbbreviatedInvoiceBlockReason.None;
        if (vatRegistered && plan.SummarySales.Count > 0)
        {
            var issuer = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId)
                .Select(c => new { c.IsVatRegistered, c.IsRetailApproved, c.PhoR06ApprovedDate }).FirstOrDefaultAsync(ct);
            var requirePhoR06 = await _db.SiteSettings.AsNoTracking()
                .Select(x => (bool?)x.RequirePhoR06ForAbbreviatedTaxInvoice).FirstOrDefaultAsync(ct) ?? true;
            if (issuer != null)
                summaryBlock = AbbreviatedTaxInvoiceRule.Judge(issuer.IsVatRegistered, issuer.IsRetailApproved, issuer.PhoR06ApprovedDate,
                    plan.SummarySales.Min(s => s.Date), requirePhoR06, AbbreviatedInvoiceChannel.Document);
        }

        // คำตัดสินเจ้าของข้อ 7 + SoD: ผู้ทำ (maker) ของเอกสารที่ระบบออกให้ = ผู้นำเข้ารอบโอน (ผู้เตรียมข้อมูล) · ผู้ตรวจ (checker) = คนกดลงบัญชี ⇒
        // บริษัทที่เปิดแยกหน้าที่ + คนเดียวกันทั้งสองบทบาท = บล็อกพร้อมทางไปต่อ (ห้ามข้ามเงียบ — ด่าน SoD ของ DocumentService เทียบกับป้ายระบบจึงไม่เคยทำงาน)
        var sodOn = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => (bool?)s.SodBlockSelfApproval).FirstOrDefaultAsync(ct) ?? false;
        // S3-11 (รอบ 200): ผู้ที่เติมไฟล์เข้ารอบเดิม (ผู้สร้างบรรทัด) เป็นผู้ทำด้วย
        var sodBlocked = SettlementPostingGate.SodSelfApproval(sodOn, batch.CreatedBy, lines.Select(l => l.CreatedBy), userId);

        var saleType = vatRegistered ? DocumentType.TaxInvoice : DocumentType.Receipt;
        var facts = new SettlementPostingFacts(
            batch.Status, payoutDay, today, payoutClosed, summaryClosed, filedVat, filedWht, accounts.Errors,
            counterparty != null, !string.IsNullOrWhiteSpace(counterparty?.TaxId), channel.ClearingAccountId,
            receiptDocs, clearingSources, duplicates,
            await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, DocumentType.PaymentVoucher),
            await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, saleType),
            existingDocs.Count + payments.Count,
            summaryBlock, staleReceipts, orphans.Voidable, sodBlocked, orphans.Unvoidable, orphans.NeedsUserAction, orphans.Acknowledged,
            Wallet: wallet, FiledPp36Periods: filedPp36, WhtFormType: whtForm, Supplementary: supplementary, Stock: stock);
        var gated = SettlementPostingGate.Evaluate(plan, facts);
        // ผู้รับค่าธรรมเนียมอยู่ต่างประเทศ แต่ช่องทางคิด WHT แบบในประเทศ (ภ.ง.ด.53) ⇒ บล็อก — ม.70 ต้องเป็น ภ.ง.ด.54 (ทีม W · R-A5 อีกรูป)
        if (batch.Status is not (SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched)
            && SettlementForeignWht.CounterpartyCountryIssue(channel, plan, counterparty?.CountryCode) is SettlementPlanIssue foreignWht)
            gated = gated with { CanPost = false, Issues = gated.Issues.Append(foreignWht).ToList() };
        // X-1 (รอบ 200 ฝ่ายค้าน · DECISIONS ข้อ 26): ช่องทางผูก config ของ gateway ⇒ โหมดภาษีค่าธรรมเนียมสองที่ต้องให้ผลเท่ากัน "ตอนลงบัญชี" ด้วย —
        // รอบโอนที่นำเข้า/ประกอบก่อนด่านนำเข้ามี หรือก่อนผู้ใช้เปลี่ยนโหมด ไม่เคยถูกถาม (คู่ค่าเริ่มต้น None↔ThaiVat7 = ภาษีซื้อ 7/107 ที่แต่งขึ้น)
        if (batch.Status is not (SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched)
            && channel.PaymentProviderConfigId is Guid boundCfgId)
        {
            var boundCfg = await _db.PaymentProviderConfigs.AsNoTracking()
                .Where(c => c.Id == boundCfgId && c.CompanyId == companyId)
                .Select(c => new { c.FeeVatMode, c.WhtOnFee }).FirstOrDefaultAsync(ct);
            if (boundCfg != null && GatewayBatchIntentRules.PostingIssue(GatewayBatchIntentRules.ModeMismatch(boundCfg.FeeVatMode,
                    boundCfg.WhtOnFee, channel.FeeVatMode, channel.FeeWhtMode, vatRegistered)) is SettlementPlanIssue modeIssue)
                gated = gated with { CanPost = false, Issues = gated.Issues.Append(modeIssue).ToList() };
        }
        // review198-S4 S4-4 (ทีม I รอบ 200): แถวไม่มีเลขรายการที่เนื้อหาตรงกับรอบโอนอื่น — เตือนที่พรีวิว/ลงบัญชีทุกครั้ง (เดิมเตือนครั้งเดียวตอนนำเข้า)
        gated = SettlementContentOverlap.Annotate(gated, contentHits, lines);

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

        // คำตัดสินรอบ 200 ข้อ 15: ใบสรุปของวันที่มีใบแรกแล้ว (ออกเลขแล้ว) = ใบสรุปเพิ่มเติมที่อ้างเลขใบแรก
        var supplementOf = supplementary.Where(s => s.FirstIssued).ToDictionary(s => s.Day, s => s.FirstNumber);
        return new Gate(loaded, gated, accounts, vatRegistered, counterparty?.Id, payoutDay, existingDocs, payments,
            new Dictionary<string, decimal>(StringComparer.Ordinal), supplementOf, orphans.Items);
    }

    /// <summary>ยอด wallet ปลายรอบของรอบโอนก่อนหน้าในช่องทางเดียวกัน (review198-A R-A12) — ไม่นับรอบที่ยกเลิก/ลบ · เรียงวันเงินเข้า แล้วเวลาที่นำเข้า</summary>
    private async Task<SettlementWalletContinuityResult> WalletContinuityAsync(Guid companyId, SettlementBatch batch, CancellationToken ct)
    {
        // T-3 (ฝ่ายค้านรอบ 200): รอบวันเดียวกันทั้งหมด (ไม่ใช่แค่ที่นำเข้าก่อน) + รอบล่าสุดของวันก่อน — ตัวเลือกรอบก่อนตัวเดียวตัดสินจากยอดต่อกัน
        var sameDay = await _db.SettlementBatches.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.ChannelId == batch.ChannelId && b.Id != batch.Id && !b.IsDeleted
                && b.Status != SettlementBatchStatus.Voided && b.PayoutDate == batch.PayoutDate)
            .Select(b => new SettlementWalletCandidate(b.PayoutRef, b.PayoutDate, b.CreatedAt, b.OpeningWalletBalance, b.ClosingWalletBalance))
            .ToListAsync(ct);
        var earlierDay = await _db.SettlementBatches.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.ChannelId == batch.ChannelId && b.Id != batch.Id && !b.IsDeleted
                && b.Status != SettlementBatchStatus.Voided && b.PayoutDate < batch.PayoutDate)
            .OrderByDescending(b => b.PayoutDate).ThenByDescending(b => b.CreatedAt)
            .Select(b => new SettlementWalletCandidate(b.PayoutRef, b.PayoutDate, b.CreatedAt, b.OpeningWalletBalance, b.ClosingWalletBalance))
            .FirstOrDefaultAsync(ct);
        var previous = SettlementWalletContinuity.PickPrevious(batch.CreatedAt, batch.OpeningWalletBalance, batch.ClosingWalletBalance,
            sameDay, earlierDay);
        return SettlementWalletContinuity.Judge(batch.OpeningWalletBalance, previous);
    }

    /// <summary>ยอดที่รอบโอน<b>อื่น</b>ที่ยังไม่ลงบัญชี (ทุกช่องทางของบริษัท — ใบขายเป็นของบริษัท) จับคู่ใบที่รอบนี้จะรับชำระไว้ (review198-B R-B13) ·
    /// รอบที่รับชำระใบนั้นไปแล้ว (ลงค้างครึ่งทาง — ยอดค้างของใบลดแล้ว) ไม่นับซ้ำ · กติกาการนับอยู่ที่ <see cref="SettlementCrossBatchReceipts"/></summary>
    private async Task<IReadOnlyDictionary<Guid, SettlementPendingReceipt>> PendingReceiptsElsewhereAsync(Guid companyId, Guid batchId,
        List<Guid> documentIds, CancellationToken ct)
    {
        if (documentIds.Count == 0) return new Dictionary<Guid, SettlementPendingReceipt>();
        var rows = await (from l in _db.SettlementLines.AsNoTracking()
                          join b in _db.SettlementBatches.AsNoTracking() on l.BatchId equals b.Id
                          where l.CompanyId == companyId && b.CompanyId == companyId && !l.IsDeleted && !b.IsDeleted
                                && l.BatchId != batchId && l.MatchedDocumentId != null && documentIds.Contains(l.MatchedDocumentId.Value)
                                && (b.Status == SettlementBatchStatus.Imported || b.Status == SettlementBatchStatus.Classified
                                    || b.Status == SettlementBatchStatus.Matched)
                          select new
                          {
                              l.BatchId, b.PayoutRef, DocumentId = l.MatchedDocumentId!.Value, l.LineType, l.Amount,
                              InClearing = l.PaymentIntentId != null || l.PaymentId != null, l.MatchStatus,
                          }).ToListAsync(ct);
        if (rows.Count == 0) return new Dictionary<Guid, SettlementPendingReceipt>();
        // รอบที่รับชำระใบนั้นไปแล้ว (ป้ายของรอบโอนใน Notes ของการรับชำระ) — ยอดค้างของใบลดไปแล้ว ห้ามหักซ้ำ
        var otherBatches = rows.Select(r => r.BatchId).Distinct().ToList();
        var markerPays = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && documentIds.Contains(p.DocumentId)
                && p.Notes != null && p.Notes.Contains(SettlementPostingKeys.PaymentMarkerHead))
            .Select(p => new { p.DocumentId, p.Notes }).ToListAsync(ct);
        var received = new HashSet<(Guid BatchId, Guid DocumentId)>();
        foreach (var p in markerPays)
            foreach (var b in otherBatches)
                if (p.Notes!.Contains(SettlementPostingKeys.PaymentMarker(b), StringComparison.Ordinal))
                    received.Add((b, p.DocumentId));
        return SettlementCrossBatchReceipts.PendingElsewhere(
            rows.Select(r => new SettlementOtherBatchSaleLine(r.BatchId, r.PayoutRef, r.DocumentId, r.LineType, r.Amount, r.InClearing,
                r.MatchStatus)),
            received);
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
            var intent = intents.FirstOrDefault(i => i.Id == id);
            var posted = intent == null ? null : await _gateway.ResolveMoneyInAccountAsync(intent, ct);
            // ฝั่งขายกับฝั่งคืนเงินตัดสิน "ถูกล้างไปแล้วด้วยเส้นอื่น" ต่างกัน (R-B2): คืนเงินภายหลังของรายการที่รอบก่อนเป็นเจ้าของยอดขาย ไม่ใช่การนับซ้ำ
            foreach (var refundSide in new[] { false, true })
            {
                var ids = counted.Where(l => l.PaymentIntentId == id
                        && (SettlementLineTypeRules.For(l.LineType).Posting == SettlementPostingKind.Refund) == refundSide)
                    .Select(l => l.Id).ToList();
                if (ids.Count == 0) continue;
                if (intent == null)
                {
                    result.Add(new SettlementClearingSource(ids, "รายการรับชำระออนไลน์ (PaymentIntent)", false, null, false));
                    continue;
                }
                var elsewhere = SettlementSaleMatch.IntentSettledElsewhere(refundSide, intent.SettlementJournalEntryId,
                    intent.SettlementBatchId, batch.Id);
                // review198-E2 E2-10: ผลการคืนเงินยังไม่แน่ชัด (E-2) ⇒ ยอดคืนในผังพักยังไม่รู้ ห้ามลงบัญชีรอบโอนทับ
                result.Add(new SettlementClearingSource(ids, $"รายการรับชำระออนไลน์ {intent.ProviderRef ?? intent.Id.ToString()}",
                    true, posted, elsewhere, intent.RefundOutcomeUnknownSince != null));
            }
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

    /// <summary>หลักฐานรายได้ซ้ำของใบสรุป (review198-A R-A7): (1) ใบสรุปวันเดียวกันของช่องทางนี้จากรอบโอนที่ยกเลิกแล้ว (ใบยังมีผล)
    /// (2) ออเดอร์เดียวกันลงบัญชีไปแล้วในรอบโอนอื่นของช่องทางนี้ (3) ออเดอร์มีเอกสารขายของตัวเอง (อ้างเลขออเดอร์)
    /// <para>คำตัดสินรอบ 200 ข้อ 15 (review198-C C-9 / O-2): ใบสรุปวันเดียวกันจากรอบโอน<b>ที่ยังมีผล</b> ⇒ ไม่บล็อกอีก — รอบนี้ออก "ใบสรุปเพิ่มเติม"
    /// อ้างใบแรกของวัน (<see cref="SettlementSummarySupplement.Judge"/>) · ใบสรุปของรอบโอนที่<b>ยกเลิก/ลบแล้ว</b>ยังเป็นรายได้ซ้ำ (บล็อกเหมือนเดิม) ·
    /// (2)(3) ระดับออเดอร์ยังบล็อก</para></summary>
    private async Task<(List<SettlementDuplicateSale> Duplicates, List<SettlementSupplementarySummary> Supplementary)> DuplicateSalesAsync(
        Guid companyId, SettlementBatch batch, SettlementChannel channel, SettlementPostingPlan plan, CancellationToken ct)
    {
        var result = new List<SettlementDuplicateSale>();
        var supplementary = new List<SettlementSupplementarySummary>();
        if (plan.SummarySales.Count == 0) return (result, supplementary);

        // ฝ่ายค้าน C-1(d): รวมรอบโอนที่ถูกยกเลิก/ลบแล้วของช่องทางนี้ด้วย — ใบสรุปที่ยังไม่ถูกยกเลิกของรอบนั้นคือรายได้ที่ลงไว้แล้วจริง
        var channelBatches = (await _db.SettlementBatches.IgnoreQueryFilters().AsNoTracking()
                .Where(b => b.CompanyId == companyId && b.ChannelId == channel.Id && b.Id != batch.Id)
                .Select(b => new { b.Id, b.PayoutRef, b.IsDeleted, b.Status }).ToListAsync(ct))
            .ToDictionary(b => b.Id.ToString("N"),
                b => (b.PayoutRef, Dead: b.IsDeleted || b.Status == SettlementBatchStatus.Voided));
        foreach (var s in plan.SummarySales)
        {
            var suffix = ":" + SettlementPostingKeys.SummaryComponent(s.Date);
            var same = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status != DocumentStatus.Voided
                    && d.CreatedBy != null && d.CreatedBy.StartsWith("system:settlement:") && d.CreatedBy.EndsWith(suffix))
                .Select(d => new { d.DocumentNumber, d.CreatedBy, d.Status, d.CreatedAt }).ToListAsync(ct);
            var sameDayLive = new List<SettlementSameDaySummary>();
            foreach (var d in same)
            {
                var batchPart = d.CreatedBy!.Length >= 50 ? d.CreatedBy.Substring(18, 32) : "";
                if (!channelBatches.TryGetValue(batchPart, out var other)) continue;
                if (other.Dead)
                    result.Add(new SettlementDuplicateSale(s.LineIds,
                        $"มีใบขายสรุปวันที่ {ThaiDate.ToThaiDisplayString(s.Date)} ของ {channel.DisplayName} แล้ว ({d.DocumentNumber} จากรอบโอน {other.PayoutRef} "
                        + "ที่ยกเลิกแล้ว) — ใบนั้นยังไม่ถูกยกเลิก ออกอีกใบ = รายได้และภาษีขายของวันนั้นซ้ำ"));
                else
                    sameDayLive.Add(new SettlementSameDaySummary(d.DocumentNumber, other.PayoutRef, DocumentStatusRules.IsIssued(d.Status), d.CreatedAt));
            }
            if (SettlementSummarySupplement.Judge(s.Date, s.LineIds, sameDayLive) is { } sup)
                supplementary.Add(sup);
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
        return (result, supplementary);
    }

    /// <summary>ของกำพร้า (ฝ่ายค้าน C-1(d)): เอกสาร/การรับชำระที่ยังไม่ถูกยกเลิกของรอบโอนที่ถูกยกเลิก/ลบแล้วในช่องทางเดียวกัน — เกิดจาก
    /// "ลงบัญชีค้างครึ่งทาง → ยกเลิกรอบโอน" ก่อนรอบนี้ (ตอนนี้ยกเลิกรอบโอนที่มีของแบบนี้ไม่ได้แล้ว) · ใบค่าธรรมเนียมไม่มีตัวกันซ้ำอื่นเลย
    /// <para>review198-S3 S3-6 (ทีม S4): เหตุจากด่านตัวเดียวกับยกเลิกการลงบัญชี <see cref="SettlementUnpostGate"/> · review198-S4 S4-1 (ทีม S5): แยกด้วย
    /// <see cref="SettlementUnpostRefusalKind"/> ที่ตัวแยก <see cref="SettlementOrphanTriage"/> — <b>ยกเลิกไม่ได้จริง</b> (e-Tax ตอบรับ · รายงานล็อก · 50 ทวิ ยื่นแล้ว)
    /// ⇒ เตือน (รอบ 200: บล็อกจนรับรู้) · <b>ต้องให้คนทำก่อน</b> (ภาษีเดือนที่ประกาศว่ายื่นแล้ว · มีเอกสารอ้าง · §78/1) ⇒ บล็อกพร้อมทางไปต่อรายชิ้น · ไม่มีเหตุ ⇒ บล็อก ·
    /// เดิม (S4) ทุกชิ้นที่ด่านปฏิเสธถูกลดเป็นคำเตือน ทั้งที่ด่านนั้นเข้มกว่าการยกเลิกทีละใบโดยตั้งใจ</para>
    /// <para>รอบ 200 ทีม V2 (DECISIONS ข้อ 10): ใบที่อ้างของกำพร้าซึ่ง<b>ตัวเอง</b>ยกเลิกไม่ได้ (<see cref="OrphanChildrenAsync"/>) ⇒ กองยกเลิกไม่ได้จริง ·
    /// การรับรู้ที่ประทับไว้ (ผู้/เวลา/เหตุผล) ส่งเข้าตัวแยก ⇒ รับรู้แล้วไม่บล็อก · S3-11: ไม่ตัด 200 รอบล่าสุดอีก และค้นการรับชำระของทุกรอบด้วยคำค้นเดียว</para></summary>
    /// <param name="excludeBatchId">รอบที่กำลังลงบัญชี (ไม่ใช่ของกำพร้าของตัวเอง) · null = ทุกรอบที่ยกเลิกแล้วของช่องทาง (ปุ่มรับรู้)</param>
    /// <param name="fallbackDate">วันที่ของเอกสารที่หาไม่เจอ (ส่งต่อให้ <see cref="LoadUnpostFactsAsync"/>)</param>
    /// <param name="current">รอบที่กำลังลงบัญชี/ตรวจเทียบ (V2-P1 — การรับรู้ที่ทำก่อนนำเข้ารอบซึ่งใช้เลขรอบโอนเดียวกับรอบเจ้าของ ไม่ครอบรอบนั้น) · null = ไม่เทียบ</param>
    private async Task<SettlementOrphanTriageResult> OrphanArtifactsAsync(Guid companyId, Guid? excludeBatchId, Guid channelId,
        DateTime fallbackDate, SettlementOrphanCurrentBatch? current, CancellationToken ct)
    {
        var none = new SettlementOrphanTriageResult(Array.Empty<string>(), Array.Empty<SettlementOrphanBlock>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<SettlementOrphanItem>());
        var exclude = excludeBatchId ?? Guid.Empty;
        var dead = await _db.SettlementBatches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.ChannelId == channelId && b.Id != exclude
                && (b.IsDeleted || b.Status == SettlementBatchStatus.Voided))
            .Select(b => new { b.Id, b.PayoutRef }).ToListAsync(ct);
        if (dead.Count == 0) return none;
        var parts = dead.Select(b => b.Id.ToString("N")).ToList();
        var refOf = dead.ToDictionary(b => b.Id.ToString("N"), b => b.PayoutRef);
        var prefixLength = SettlementPostingKeys.CreatorPrefix(Guid.Empty).Length;
        var docRows = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status != DocumentStatus.Voided
                && d.CreatedBy != null && d.CreatedBy.StartsWith("system:settlement:")
                && parts.Contains(d.CreatedBy.Substring(18, 32)))
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.CreatedBy, d.DocumentType, d.Status, d.TotalAmount,
                d.SettlementOrphanAckAt, d.SettlementOrphanAckBy, d.SettlementOrphanAckReason,
            }).ToListAsync(ct);
        var docs = docRows.Select(d => (Part: d.CreatedBy!.Substring(18, 32),
                Doc: new ExistingDoc(d.Id, d.CreatedBy!.Length > prefixLength ? d.CreatedBy.Substring(prefixLength) : "", d.DocumentNumber,
                    d.DocumentType, d.Status, d.TotalAmount)))
            .ToList();
        // S3-11: การรับชำระของทุกรอบที่ยกเลิกแล้วด้วยคำค้นเดียว (เดิมวนทีละรอบ + ตัด 200 รอบ) — ป้ายตัวเดียวกับ SettlementArtifactGuard
        var deadIds = dead.Select(b => b.Id).ToHashSet();
        // V2-C4 (ฝ่ายค้านรอบ 200): กรองรอบตายใน SQL — ระบบเขียนป้ายไว้ต้น Notes เสมอ ⇒ id รอบ = 32 ตัวหลังหัวป้าย · แถวที่ป้ายไม่อยู่ต้น Notes (ผู้ใช้แก้ข้อความ)
        // ยังถูกดึงมาให้ตัวอ่านป้ายตัวเดียวตัดสิน (ผลเท่าเดิมทุกแถว) · เดิมดึง Id+Notes ของการรับชำระจากรอบโอนทุกแถวของบริษัททุกครั้ง
        var markerHead = SettlementPostingKeys.PaymentMarkerHead;
        var markerHeadLength = markerHead.Length;
        var markedPays = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.Notes != null && p.Notes.Contains(markerHead)
                && (!p.Notes.StartsWith(markerHead) || parts.Contains(p.Notes.Substring(markerHeadLength, 32))))
            .Select(p => new { p.Id, p.Notes }).ToListAsync(ct);
        var payOwner = markedPays
            .Select(p => (p.Id, Owner: SettlementArtifactGuard.BatchIdFromPaymentNotes(p.Notes)))
            .Where(x => x.Owner is Guid o && deadIds.Contains(o))
            .ToDictionary(x => x.Id, x => x.Owner!.Value);
        var payIds = payOwner.Keys.ToList();
        var payRows = payIds.Count == 0 ? new List<Payment>()
            : await _db.Payments.AsNoTracking().Where(p => p.CompanyId == companyId && payIds.Contains(p.Id)).ToListAsync(ct);
        var pays = payRows.Select(p => (Part: payOwner[p.Id].ToString("N"), Payment: p)).ToList();
        if (docs.Count == 0 && pays.Count == 0) return none;

        // ด่านตัวเดียวกับยกเลิกการลงบัญชี — แต่ "ด่านปฏิเสธ" ≠ "ยกเลิกไม่ได้": ตัวแยกดู Kind ของแต่ละเหตุ (S4-1)
        var (unpostDocs, certs, filed, unpostPays) = await LoadUnpostFactsAsync(companyId, fallbackDate,
            docs.Select(d => d.Doc).ToList(), pays.Select(p => p.Payment).ToList(), ct);
        var refusals = SettlementUnpostGate.Evaluate(unpostDocs, certs, filed, unpostPays);
        // รอบ 200: ใบที่อ้างของกำพร้า — ใบนั้นเองยกเลิกได้ไหม (e-Tax/รายงานล็อก/50 ทวิ/ส่งลูกค้าแล้ว)
        var children = await OrphanChildrenAsync(companyId, docs.Select(d => d.Doc.Id).ToList(), ct);
        // ผู้รับรู้ (สมาชิกบริษัทนี้เท่านั้น — ไม่เจอ = แสดง id ไม่เดาชื่อ)
        var ackUsers = docRows.Select(d => d.SettlementOrphanAckBy).Concat(pays.Select(p => p.Payment.SettlementOrphanAckBy))
            .OfType<Guid>().Distinct().ToList();
        var names = await MemberNamesAsync(companyId, ackUsers, ct);
        SettlementOrphanAck? AckOf(DateTime? at, Guid? by, string? reason)
            => at is DateTime a && by is Guid u ? new SettlementOrphanAck(u, names.GetValueOrDefault(u), a, reason ?? "") : null;
        var ackOfDoc = docRows.ToDictionary(d => d.Id, d => AckOf(d.SettlementOrphanAckAt, d.SettlementOrphanAckBy, d.SettlementOrphanAckReason));
        var artifacts = docs.Select(d => new SettlementOrphanArtifact(d.Doc.Id, Guid.ParseExact(d.Part, "N"), refOf[d.Part], false, d.Doc.Number,
                ackOfDoc.GetValueOrDefault(d.Doc.Id)))
            .Concat(pays.Select(p => new SettlementOrphanArtifact(p.Payment.Id, Guid.ParseExact(p.Part, "N"), refOf[p.Part], true,
                p.Payment.PaymentNumber,
                AckOf(p.Payment.SettlementOrphanAckAt, p.Payment.SettlementOrphanAckBy, p.Payment.SettlementOrphanAckReason))))
            .ToList();
        return SettlementOrphanTriage.Split(artifacts, refusals, unpostDocs, children, current);
    }

    /// <summary>ใบที่อ้างของกำพร้าแต่ละใบ (ชุดเดียวกับที่ <c>VoidDocumentAsync</c> ปฏิเสธเพราะ "มีเอกสารอ้าง" — <see cref="DocumentVoidPreconditions.ChildFactsAsync"/>)
    /// + ข้อเท็จจริงว่าใบนั้นเองยกเลิกได้ไหม: e-Tax ตอบรับ · อยู่ในรายงานที่ล็อก · 50 ทวิ ในแบบที่ยื่นแล้ว (<see cref="WhtCertVoidGuard.CheckDocumentAsync"/>) ·
    /// ส่งลูกค้าแล้ว (รอบ 200 ทีม V2 · review198-S4 S4-1 ความเสี่ยงที่เหลือ) · ใบที่อ้างซ้อนอีกชั้น (หลาน) ไม่ตามต่อ — ข้อจำกัดที่บันทึกไว้ในรายงานทีม</summary>
    private async Task<List<SettlementOrphanChild>> OrphanChildrenAsync(Guid companyId, List<Guid> documentIds, CancellationToken ct)
    {
        var facts = await DocumentVoidPreconditions.ChildFactsAsync(_db, companyId, documentIds, ct);
        var childIds = facts.Where(f => f.ChildId != null).Select(f => f.ChildId!.Value).Distinct().ToList();
        if (childIds.Count == 0) return new List<SettlementOrphanChild>();
        var accepted = (await _db.EtaxInvoices.AsNoTracking()
                .Where(e => e.CompanyId == companyId && childIds.Contains(e.DocumentId) && e.Status == EtaxStatus.Accepted)
                .Select(e => e.DocumentId).ToListAsync(ct)).ToHashSet();
        var locked = (await _db.TaxReports.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.FilingLockedAt != null)
                .SelectMany(r => r.Lines)
                .Where(l => l.DocumentId != null && childIds.Contains(l.DocumentId.Value))
                .Select(l => l.DocumentId!.Value).ToListAsync(ct)).ToHashSet();
        // V2-C1 (ฝ่ายค้านรอบ 200 · DECISIONS ข้อ 25): "ส่งลูกค้าแล้ว" = หลักฐานการส่งจริง (บันทึกอีเมล/e-Tax by email สำเร็จ) ผ่านตัวตัดสินเดียว —
        // เดิมดูสถานะเอกสาร "ส่งแล้ว" ซึ่งไม่มีผู้ประทับ ⇒ ทริกเกอร์ของข้อ 10 ไม่เคยเป็นจริง
        var sent = await DocumentDeliveryEvidence.DeliveredAsync(_db, companyId, childIds, ct);
        var whtFiled = new Dictionary<Guid, string?>();
        foreach (var id in childIds)
            whtFiled[id] = await WhtCertVoidGuard.CheckDocumentAsync(_db, companyId, id, ct);
        return facts.Where(f => f.ChildId != null)
            .Select(f => new SettlementOrphanChild(f.ParentId, f.ChildId!.Value, f.ChildType, f.ChildNumber,
                accepted.Contains(f.ChildId.Value), locked.Contains(f.ChildId.Value), sent.Contains(f.ChildId.Value), whtFiled[f.ChildId.Value]))
            .ToList();
    }

    /// <summary>ชื่อผู้ใช้ที่เป็นสมาชิกบริษัทนี้ (tenant ผ่าน CompanyUsers) — ไม่เป็นสมาชิก/ไม่พบ = ไม่อยู่ในผล</summary>
    private async Task<Dictionary<Guid, string>> MemberNamesAsync(Guid companyId, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return new Dictionary<Guid, string>();
        var ids = userIds.ToList();
        return (await _db.Users.AsNoTracking()
                .Where(u => ids.Contains(u.Id) && _db.CompanyUsers.Any(cu => cu.UserId == u.Id && cu.CompanyId == companyId))
                .Select(u => new { u.Id, u.FullName }).ToListAsync(ct))
            .ToDictionary(u => u.Id, u => u.FullName ?? "");
    }

    // ═════════════════════════════ รับรู้ของกำพร้า (รอบ 200 · DECISIONS ข้อ 10) ═════════════════════════════

    public async Task<SettlementOrphanAckResult> AcknowledgeOrphanAsync(Guid companyId, Guid artifactId, bool isPayment, Guid userId,
        string? reason, Guid? checkedBatchId, CancellationToken ct = default)
    {
        static SettlementOrphanAckResult Fail(string m) => new(false, m, null);
        if (SettlementOrphanTriage.AckReasonProblem(reason) is string badReason) return Fail(badReason);
        // สิทธิ์ตรวจใน service (ไม่ใช่แค่ [RequirePermission] ของ controller — ฝ่ายค้าน C-8: ทางเข้าอื่นต้องเดินด่านเดียวกัน)
        if (!await _perms.HasPermissionAsync(companyId, userId, PermissionKeys.SettlementPost))
            return Fail($"ผู้ใช้นี้ไม่มีสิทธิ์ “{PermissionKeys.LabelOf(PermissionKeys.SettlementPost)}” — ให้ผู้มีสิทธิ์ลงบัญชีรอบโอนเป็นผู้รับรู้ของกำพร้า");
        // รอบโอนเจ้าของจากป้ายชุดเดียวกับตัวหาของกำพร้า (CreatedBy · Payment.Notes)
        Guid? ownerId = isPayment
            ? SettlementArtifactGuard.BatchIdFromPaymentNotes(await _db.Payments.AsNoTracking()
                .Where(p => p.Id == artifactId && p.CompanyId == companyId && !p.IsDeleted).Select(p => p.Notes).FirstOrDefaultAsync(ct))
            : SettlementArtifactGuard.BatchIdFromCreator(await _db.Documents.AsNoTracking()
                .Where(d => d.Id == artifactId && d.CompanyId == companyId && !d.IsDeleted).Select(d => d.CreatedBy).FirstOrDefaultAsync(ct));
        if (ownerId is not Guid ownerBatchId)
            return Fail("ไม่พบเอกสาร/การรับชำระที่การลงบัญชีรอบโอนสร้างในบริษัทนี้ — รับรู้ได้เฉพาะของกำพร้าของรอบโอน");
        var owner = await _db.SettlementBatches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.Id == ownerBatchId && b.CompanyId == companyId)
            .Select(b => new { b.ChannelId, b.PayoutDate }).FirstOrDefaultAsync(ct);
        if (owner == null) return Fail("ไม่พบรอบโอนเจ้าของรายการนี้ในบริษัทนี้");
        // V2-P1 (ฝ่ายค้านรอบ 200): รอบที่ผู้ใช้ตรวจเทียบ (หน้าจอที่กดรับรู้) — ต้องอยู่ช่องทางเดียวกัน · เก็บลง audit ว่า "ตรวจแล้วไม่ซ้ำ" หมายถึงรอบไหน
        SettlementOrphanCurrentBatch? checkedBatch = null;
        if (checkedBatchId is Guid cbId)
        {
            var cb = await _db.SettlementBatches.AsNoTracking()
                .Where(b => b.Id == cbId && b.CompanyId == companyId && !b.IsDeleted)
                .Select(b => new { b.Id, b.PayoutRef, b.CreatedAt, b.ChannelId }).FirstOrDefaultAsync(ct);
            if (cb == null || cb.ChannelId != owner.ChannelId)
                return Fail("รอบโอนที่ตรวจเทียบไม่อยู่ในช่องทางเดียวกับของกำพร้านี้ — เปิดพรีวิวของรอบโอนในช่องทางนั้นแล้วกดรับรู้ใหม่");
            checkedBatch = new SettlementOrphanCurrentBatch(cb.Id, cb.PayoutRef, cb.CreatedAt);
        }
        var why = reason!.Trim();
        SettlementOrphanAckResult? result = null;
        // ล็อกต่อช่องทางตัวเดียวกับลงบัญชี/ยกเลิก — ตัดสิน "ยกเลิกไม่ได้จริง" ด้วยข้อเท็จจริงสดใต้ล็อก ไม่ใช่ของที่หน้าจอเห็นตอนโหลด
        var acquired = await JobLock.RunExclusiveAsync(_db, SettlementChannelLock.Scope, SettlementChannelLock.Part(owner.ChannelId),
            async () =>
            {
                result = await AcknowledgeOrphanCoreAsync(companyId, artifactId, isPayment, userId, why, owner.ChannelId, owner.PayoutDate,
                    ownerBatchId, checkedBatch, ct);
            }, _logger, companyId, ct);
        return acquired && result is not null ? result : Fail(BusyMessage);
    }

    private async Task<SettlementOrphanAckResult> AcknowledgeOrphanCoreAsync(Guid companyId, Guid artifactId, bool isPayment, Guid userId,
        string reason, Guid channelId, DateTime fallbackDate, Guid ownerBatchId, SettlementOrphanCurrentBatch? checkedBatch, CancellationToken ct)
    {
        // ตัวแยกของกำพร้าตัวเดียวกับด่านลงบัญชี — รับรู้ได้เฉพาะกองยกเลิกไม่ได้จริง (กอง NeedsUserAction/ยกเลิกได้ ⇒ ปฏิเสธพร้อมทางไปต่อ) ·
        // V2-P1: เทียบกับรอบที่ผู้ใช้ตรวจ — การรับรู้เดิมที่ไม่ครอบรอบนั้น ⇒ รับรู้ใหม่ได้ (ไม่ใช่ตอบซ้ำ)
        var triage = await OrphanArtifactsAsync(companyId, null, channelId, fallbackDate, checkedBatch, ct);
        if (SettlementOrphanTriage.AckRefusal(triage, artifactId, isPayment) is string refusal)
            return new SettlementOrphanAckResult(false, refusal, null);
        var item = (triage.Items ?? Array.Empty<SettlementOrphanItem>()).First(i => i.Id == artifactId && i.IsPayment == isPayment);
        if (item.Ack is SettlementOrphanAck existing)
            return new SettlementOrphanAckResult(true, $"{item.Number}: {SettlementOrphanTriage.AckLabel(existing)} — ไม่บันทึกซ้ำ", existing);

        var now = DateTime.UtcNow;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // V2-C3 (ฝ่ายค้านรอบ 200 · แบบแผน review198-C C-20): ตัวติดตามว่างต้น lambda — retry ต้องไม่บันทึก audit/ธงของรอบแรกซ้ำ ·
            // ของที่โหลดก่อน lambda (triage) เป็น AsNoTracking ใช้อ่านอย่างเดียว · แถวที่เขียนโหลดใหม่ข้างใน
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            if (isPayment)
            {
                var p = await _db.Payments.FirstOrDefaultAsync(x => x.Id == artifactId && x.CompanyId == companyId && !x.IsDeleted, ct)
                    ?? throw new KeyNotFoundException("ไม่พบการรับชำระ");
                p.SettlementOrphanAckAt = now;
                p.SettlementOrphanAckBy = userId;
                p.SettlementOrphanAckReason = reason;
            }
            else
            {
                var d = await _db.Documents.FirstOrDefaultAsync(x => x.Id == artifactId && x.CompanyId == companyId && !x.IsDeleted, ct)
                    ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
                d.SettlementOrphanAckAt = now;
                d.SettlementOrphanAckBy = userId;
                d.SettlementOrphanAckReason = reason;
            }
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                EntityType = isPayment ? nameof(Payment) : nameof(Document),
                EntityId = artifactId.ToString(),
                Action = AuditAction.Update,
                NewValues = JsonSerializer.Serialize(new
                {
                    action = "settlement-orphan-ack",
                    reason,
                    ownerBatchId,
                    payoutRef = item.PayoutRef,
                    number = item.Number,
                    unvoidableBecause = item.Why,
                    checkedBatchId = checkedBatch?.BatchId,
                    checkedPayoutRef = checkedBatch?.PayoutRef,
                }),
                Timestamp = now,
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        var names = await MemberNamesAsync(companyId, new[] { userId }, ct);
        var ack = new SettlementOrphanAck(userId, names.GetValueOrDefault(userId), now, reason);
        return new SettlementOrphanAckResult(true,
            $"รับรู้ของกำพร้า {item.Number} (รอบโอน {item.PayoutRef}) แล้ว — ระบบไม่บล็อกการลงบัญชีของช่องทางนี้เพราะรายการนี้อีก · "
            + "ตรวจรายการซ้ำกับรอบที่ยกเลิกก่อนกดลงบัญชี", ack);
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
        var acquired = await JobLock.RunExclusiveAsync(_db, SettlementChannelLock.Scope, SettlementChannelLock.Part(head.ChannelId),
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

        // ── สิทธิ์ (ฝ่ายค้าน C-8 — ตรวจใน service ไม่ใช่แค่ controller): ยกเลิกเอกสาร/การรับชำระ = ระดับ Void ของชนิดเอกสาร (ตัวเดียวกับหน้าเอกสาร) ·
        //    ถอนการจับคู่ธนาคาร = สิทธิ์กระทบยอดธนาคาร ──
        var paymentDocIds = payments.Select(p => p.DocumentId).Distinct().ToList();
        var paymentDocTypes = paymentDocIds.Count == 0 ? new List<DocumentType>()
            : await _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && paymentDocIds.Contains(d.Id))
                .Select(d => d.DocumentType).Distinct().ToListAsync(ct);
        foreach (var t in docs.Select(d => d.Type).Concat(paymentDocTypes).Distinct())
            if (!await DocumentPermissionHelper.CanVoidAsync(_perms, companyId, userId, t))
                return Fail($"ผู้ใช้นี้ไม่มีสิทธิ์ยกเลิกเอกสาร/การรับชำระชนิด {t} ที่รอบโอนนี้สร้าง — ให้ผู้มีสิทธิ์ยกเลิกเอกสารเป็นผู้กด");
        if (batch.BankTransactionId is not null
            && !await _perms.HasPermissionAsync(companyId, userId, PermissionKeys.BankReconcile))
            return Fail("รอบโอนนี้จับคู่กับรายการเดินบัญชีแล้ว — ผู้ใช้นี้ไม่มีสิทธิ์กระทบยอดธนาคาร (ถอนการจับคู่) · ให้ผู้มีสิทธิ์เป็นผู้กด");

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
        var docIds = docs.Select(d => d.Id).ToList();
        var docFacts = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && docIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentDate, d.VatAmount, d.IsForeignService }).ToListAsync(ct);
        foreach (var d in docFacts.Select(x => x.DocumentDate)
                     .Append(payoutJe?.EntryDate ?? ThaiDate.CalendarDateUtc(batch.PayoutDate)).Distinct())
            if (await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, d, ct) is string closed)
                return Fail(closed + " — ยกเลิกการลงบัญชีรอบโอนนี้ไม่ได้จนกว่าจะเปิดงวด (หรือบันทึกรายการปรับปรุงในงวดปัจจุบันแทน)");

        // ── ฝ่ายค้าน C-2: ด่านภาษี/e-Tax/50 ทวิ ของ "ทุกชิ้น" ก่อนแตะชิ้นแรก — ถูกปฏิเสธกลางทาง = สมุดครึ่งกลับครึ่งค้าง ──
        var (unpostDocs, certs, filed, unpostPays) = await LoadUnpostFactsAsync(companyId, batch.PayoutDate, docs, payments, ct);
        var refusals = SettlementUnpostGate.Evaluate(unpostDocs, certs, filed, unpostPays);
        if (refusals.Count > 0)
            return Fail("ยกเลิกการลงบัญชีรอบโอนนี้ไม่ได้ (ยังไม่ได้แตะอะไร): "
                + string.Join(" · ", refusals.Select(r => $"{r.Subject}: {r.Reason}")) + " — " + refusals[0].NextStep);

        var voidedPayments = new List<Guid>();
        var voidedDocs = new List<Guid>();
        var etaxFlags = new List<string>();
        try
        {
            // ขอบเขต "กำลังยกเลิกการลงบัญชีรอบโอนนี้" — ด่าน C-5 ในเส้นยกเลิกเอกสาร/การรับชำระปล่อยผ่านเฉพาะเส้นนี้
            using var unposting = SettlementUnpostScope.Enter(batchId);
            // 1) เอกสาร (ลำดับคงที่ — ฝั่งขายที่เสี่ยงถูกปฏิเสธที่สุดก่อน) + 50 ทวิ ของใบนั้นก่อนตัวใบ · เส้นปกติ: reversal JE ลงวันที่ของเอกสาร
            foreach (var d in SettlementUnpostGate.VoidOrder(unpostDocs))
            {
                foreach (var cert in certs.Where(c => c.DocumentId == d.Id)) await _whtCerts.VoidAsync(companyId, cert.Id);
                await _documents.VoidDocumentAsync(companyId, d.Id);
                voidedDocs.Add(d.Id);
            }
            // 2) การรับชำระ (เส้นปกติ: กลับ JE + คืนยอดใบ) — หลังเอกสาร
            foreach (var p in payments)
            {
                // รอบ 200 ทีม V1: ใบเสร็จอัตโนมัติที่ e-Tax ถึงกรมสรรพากรแล้วถูกด่านด้านบนปฏิเสธก่อนแตะชิ้นแรก · รอบ 200 ทีม V1F (V1-P2): ใบที่ถูกส่ง
                // e-Tax ระหว่างด่านกับลูป ⇒ ติดธง "ต้องยกเลิกทาง e-Tax" (ไม่ throw กลางลูป = ไม่ครึ่งกลับครึ่งค้าง) และบอกในผลลัพธ์
                var voidResult = await _documents.VoidPaymentAsync(companyId, p.Id, PaymentVoidCause.SettlementUnpost);
                if (voidResult.EtaxCancellationFlag != null) etaxFlags.Add(voidResult.EtaxCancellationFlag);
                voidedPayments.Add(p.Id);
            }
            // 3) ถอนการจับคู่ธนาคาร (เจ้าของการจับคู่ = IBankService) — ก่อนกลับ JE รอบโอน
            if (batch.BankTransactionId is Guid txnId)
            {
                var txn = await _db.Set<BankTransaction>().AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == txnId && t.CompanyId == companyId, ct);
                if (txn is { ReconciliationStatus: ReconciliationStatus.Matched } && txn.MatchedJournalEntryId == batch.PayoutJournalEntryId)
                    await _bank.UnmatchTransactionAsync(companyId, new UnmatchRequest(txnId));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ยกเลิกการลงบัญชีรอบโอน {Batch} ล้มกลางทาง", batchId);
            throw new BusinessRuleException(
                $"ยกเลิกการลงบัญชีรอบโอน {batch.PayoutRef} ไม่สำเร็จ: {ex.Message} — ยกเลิกไปแล้ว {voidedDocs.Count} เอกสาร · "
                + $"{voidedPayments.Count} การรับชำระ (สถานะรอบโอนยังเป็นลงบัญชีแล้ว) · แก้สาเหตุแล้วกดยกเลิกอีกครั้ง ระบบทำต่อจากที่ค้าง",
                ex, "SETTLEMENT-UNPOST-PARTIAL", 409);
        }

        // 4) ธุรกรรมเดียว: กลับรายการ JE รอบโอน (เส้นกลางของ IAccountingService) + สถานะ + audit
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // V2-C3 (ฝ่ายค้านรอบ 200 · แบบแผน C-20): ของที่โหลดก่อน lambda (loaded/payoutJe/docFacts) เป็น AsNoTracking ใช้อ่านอย่างเดียว ·
            // ขั้น 1–3 (ยกเลิกเอกสาร/การรับชำระ/ถอนจับคู่) commit ธุรกรรมของตัวเองแล้ว · แถวที่เขียนข้างล่างโหลด/สร้างใหม่หลัง Clear
            _db.ChangeTracker.Clear();
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
                $"ยกเลิกการลงบัญชีรอบโอน {tracked.PayoutRef} แล้ว — แก้รายการแล้วลงบัญชีใหม่ได้"
                + (etaxFlags.Count == 0 ? "" : $" · ⚠️ ใบเสร็จ {etaxFlags.Count} ใบถูกส่ง e-Tax ระหว่างทางจึงติดธง “ต้องยกเลิกทาง e-Tax”: "
                    + string.Join(" · ", etaxFlags)),
                tracked.Status, reversalId, voidedDocs, voidedPayments);
        });
    }

    /// <summary>ข้อเท็จจริงของด่านยกเลิกการลงบัญชี (C-2) — ตัวโหลดเดียวของ <see cref="UnpostAsync"/> · <see cref="UnpostBlockersAsync"/> (หน้าจอกับด่านจริง
    /// เห็นชุดเดียวกัน) และตัวแยกของกำพร้าที่ยกเลิกไม่ได้ (<see cref="OrphanArtifactsAsync"/> · review198-S3 S3-6) · ทีม S4 เพิ่ม: ภาษีซื้อของใบค่าธรรมเนียม
    /// (พัก/ถึงกำหนด · S3-2) · เหตุที่ <c>VoidDocumentAsync</c> ปฏิเสธเอง (<see cref="DocumentVoidPreconditions"/> · S3-3) · การรับชำระที่ทำให้ภาษีขาย
    /// ถึงกำหนด §78/1 (S3-7)</summary>
    /// <param name="fallbackDate">วันที่ของเอกสารที่หาไม่เจอ (ไม่ควรเกิด — ใช้วันที่รอบโอน)</param>
    private async Task<UnpostFacts> LoadUnpostFactsAsync(Guid companyId, DateTime fallbackDate, List<ExistingDoc> docs,
        IReadOnlyList<Payment> payments, CancellationToken ct)
    {
        var docIds = docs.Select(d => d.Id).ToList();
        var docFacts = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && docIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentDate, d.VatAmount, d.IsForeignService, d.InputVatPostedAsUndue, d.InputVatBecameClaimableAt,
                d.TaxPointDate })
            .ToListAsync(ct);
        var accepted = (await _db.EtaxInvoices.AsNoTracking()
                .Where(e => e.CompanyId == companyId && docIds.Contains(e.DocumentId) && e.Status == EtaxStatus.Accepted)
                .Select(e => e.DocumentId).ToListAsync(ct)).ToHashSet();
        var locked = (await _db.TaxReports.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.FilingLockedAt != null)
                .SelectMany(r => r.Lines)
                .Where(l => l.DocumentId != null && docIds.Contains(l.DocumentId.Value))
                .Select(l => l.DocumentId!.Value).ToListAsync(ct)).ToHashSet();
        var certs = await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.DocumentId != null && docIds.Contains(w.DocumentId.Value) && !w.IsDeleted
                && w.Status != WithholdingTaxCertStatus.Voided)
            .Select(w => new SettlementUnpostCertificate(w.Id, w.DocumentId!.Value, w.CertificateNumber, w.TaxFormType, w.TaxYear,
                w.TaxMonth, w.Status)).ToListAsync(ct);
        var filed = (await _db.TaxReports.AsNoTracking()
                .Where(t => t.CompanyId == companyId && !t.IsDeleted && TaxFilingLockPolicy.DeclaredOrFiledStatuses.Contains(t.Status))
                .Select(t => new { t.TaxType, t.Year, t.Month }).ToListAsync(ct))
            .Select(t => (t.TaxType, t.Year, t.Month)).ToHashSet();
        // S3-3: เหตุที่ VoidDocumentAsync ปฏิเสธเอง — ตัวตัดสินเดียวกับเส้นยกเลิกเอกสาร (ห้ามสำเนา)
        var childBlocks = await DocumentVoidPreconditions.ChildBlocksAsync(_db, companyId, docIds, ct);
        var unpostDocs = docs.Select(d =>
        {
            var f = docFacts.FirstOrDefault(x => x.Id == d.Id);
            return new SettlementUnpostDocument(d.Id, d.Number, d.Type, d.Component, f?.DocumentDate ?? fallbackDate,
                f?.VatAmount ?? 0m, f?.IsForeignService ?? false, accepted.Contains(d.Id), locked.Contains(d.Id),
                f?.InputVatPostedAsUndue ?? false, f?.InputVatBecameClaimableAt, childBlocks.GetValueOrDefault(d.Id), f?.TaxPointDate);
        }).ToList();

        // S3-7: ใบขายที่รอบโอนรับชำระ — จุดความรับผิด §78/1 ที่เกิดจากการรับเงิน (OutputVatDueAt) + ยอดรับสะสมของใบ
        var payDocIds = payments.Select(p => p.DocumentId).Distinct().ToList();
        var payDocs = payDocIds.Count == 0
            ? new Dictionary<Guid, (string Number, DateTime? DueAt, decimal Paid)>()
            : (await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == companyId && payDocIds.Contains(d.Id))
                    .Select(d => new { d.Id, d.DocumentNumber, d.OutputVatDueAt, d.PaidAmount }).ToListAsync(ct))
                .ToDictionary(d => d.Id, d => (Number: d.DocumentNumber, DueAt: d.OutputVatDueAt, Paid: d.PaidAmount));
        // S4-8: ใบเสร็จอัตโนมัติคู่การรับชำระ — ใบที่ e-Tax ตอบรับแล้ว = ยกเลิกไม่ได้ · รอบ 200 ทีม V1 (คำตัดสินข้อ 11): ใบที่ส่งแล้วแต่ยังไม่ตอบรับ
        // (Submitted) VoidPaymentAsync ปฏิเสธด้วย ⇒ ด่านต้องเห็นก่อนแตะชิ้นแรก (ไม่งั้นล้มกลางทางหลังยกเลิกเอกสารไปแล้ว) — ชุดสถานะจาก
        // DocumentVoidPreconditions.EtaxReachedRdStatuses ตัวเดียวกับ VoidPaymentAsync
        var receiptIds = payments.Where(p => p.ReceiptDocumentId != null).Select(p => p.ReceiptDocumentId!.Value).Distinct().ToList();
        // รอบ 200 ทีม V1F (V1-P1): ตัวโหลดเดียว EffectiveEtaxAsync — e-Tax by Email ที่ประทับเวลาแล้ว = ถึงกรมสรรพากร (ถือเท่า Accepted)
        var receiptEtaxById = await DocumentVoidPreconditions.EffectiveEtaxAsync(_db, companyId, receiptIds, ct);
        var receiptEtax = receiptEtaxById
            .Where(kv => kv.Value is EtaxStatus st && DocumentVoidPreconditions.EtaxReachedRdStatuses.Contains(st))
            .Select(kv => (DocumentId: kv.Key, Status: kv.Value!.Value))
            .ToList();
        var acceptedReceipts = receiptEtax.Where(e => e.Status == EtaxStatus.Accepted).Select(e => e.DocumentId).ToHashSet();
        var submittedReceipts = receiptEtax.Where(e => e.Status != EtaxStatus.Accepted).Select(e => e.DocumentId)
            .Where(id => !acceptedReceipts.Contains(id)).ToHashSet();
        var acceptedReceiptIds = acceptedReceipts.Concat(submittedReceipts).ToList();
        var receiptNumbers = acceptedReceiptIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && acceptedReceiptIds.Contains(d.Id))
                .Select(d => new { d.Id, d.DocumentNumber })
                .ToDictionaryAsync(d => d.Id, d => d.DocumentNumber, ct);
        var unpostPays = payments.Select(p =>
        {
            var found = payDocs.TryGetValue(p.DocumentId, out var pd);
            var receiptAccepted = p.ReceiptDocumentId is Guid rid && acceptedReceipts.Contains(rid);
            var receiptSubmitted = p.ReceiptDocumentId is Guid sid && submittedReceipts.Contains(sid);
            return new SettlementUnpostPayment(p.Id, p.PaymentNumber, p.DocumentId, found ? pd.Number : null, found ? pd.DueAt : null,
                found ? pd.Paid : 0m, payments.Where(x => x.DocumentId == p.DocumentId).Sum(x => x.Amount),
                receiptAccepted, receiptAccepted || receiptSubmitted ? receiptNumbers.GetValueOrDefault(p.ReceiptDocumentId!.Value) : null,
                ReceiptEtaxSubmitted: receiptSubmitted);
        }).ToList();
        return new UnpostFacts(unpostDocs, certs, filed, unpostPays);
    }

    private sealed record UnpostFacts(List<SettlementUnpostDocument> Docs, List<SettlementUnpostCertificate> Certs,
        HashSet<(TaxType TaxType, int Year, int Month)> Filed, List<SettlementUnpostPayment> Payments);

    public async Task<IReadOnlyList<SettlementUnpostRefusal>> UnpostBlockersAsync(Guid companyId, Guid batchId,
        CancellationToken ct = default)
    {
        // S3-11 (รอบ 200): อ่านแค่หัวรอบ — เดิม LoadAsync โหลดบรรทัดทั้งรอบทุก GET และโยน "ไม่พบช่องทาง" ⇒ หน้ารอบโอนเปิดไม่ได้เมื่อช่องทางถูกลบ
        var head = await _db.SettlementBatches.AsNoTracking()
            .Where(b => b.Id == batchId && b.CompanyId == companyId && !b.IsDeleted)
            .Select(b => new { b.Status, b.PayoutDate }).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบรอบโอน");
        if (head.Status is not (SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched))
            return Array.Empty<SettlementUnpostRefusal>();
        var docs = await ExistingDocsAsync(companyId, batchId, ct);
        var payments = await SettlementPaymentsAsync(companyId, batchId, ct);
        var (unpostDocs, certs, filed, unpostPays) = await LoadUnpostFactsAsync(companyId, head.PayoutDate, docs, payments, ct);
        return SettlementUnpostGate.Evaluate(unpostDocs, certs, filed, unpostPays);
    }

    // ═════════════════════════════ จับคู่ธนาคาร ═════════════════════════════

    public async Task<SettlementBankMatchResult> MatchBankTransactionAsync(Guid companyId, Guid batchId, Guid bankTransactionId,
        Guid userId, CancellationToken ct = default)
    {
        var head = await HeadAsync(companyId, batchId, ct);
        SettlementBankMatchResult? result = null;
        var acquired = await JobLock.RunExclusiveAsync(_db, SettlementChannelLock.Scope, SettlementChannelLock.Part(head.ChannelId),
            async () => { result = await MatchCoreAsync(companyId, batchId, bankTransactionId, userId, ct); }, _logger, companyId, ct);
        return acquired && result is not null ? result
            : new SettlementBankMatchResult(false, BusyMessage, head.Status, null);
    }

    private async Task<SettlementBankMatchResult> MatchCoreAsync(Guid companyId, Guid batchId, Guid bankTransactionId,
        Guid userId, CancellationToken ct)
    {
        // ฝ่ายค้าน C-8: สิทธิ์ตรวจใน service (ทุกทางเข้า — controller/LINE/มือถือ/job) — จับคู่ธนาคาร = สิทธิ์กระทบยอดธนาคาร
        if (!await _perms.HasPermissionAsync(companyId, userId, PermissionKeys.BankReconcile))
            return new SettlementBankMatchResult(false,
                "ผู้ใช้นี้ไม่มีสิทธิ์กระทบยอดธนาคาร — ให้ผู้มีสิทธิ์เป็นผู้จับคู่รอบโอนกับรายการเดินบัญชี",
                (await HeadAsync(companyId, batchId, ct)).Status, null);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();   // review198-C C-20 (ดู CommitPostedAsync)
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
        // ฝ่ายค้าน C-8: ปิด chargeback = ลง JE เอง ⇒ สิทธิ์จัดการสมุดรายวัน (ตัวเดียวกับหน้าสมุดรายวัน) ตรวจใน service
        if (!await _perms.HasPermissionAsync(companyId, userId, PermissionKeys.JournalManage))
            return new SettlementChargebackResult(false,
                "ผู้ใช้นี้ไม่มีสิทธิ์บันทึกรายการสมุดรายวัน — ให้ผู้มีสิทธิ์เป็นผู้ปิดรายการ chargeback", null, null);
        var channelId = await _db.SettlementLines.AsNoTracking()
            .Where(l => l.Id == lineId && l.CompanyId == companyId && !l.IsDeleted)
            .Select(l => (Guid?)l.ChannelId).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบบรรทัดของรอบโอน");
        // ฝ่ายค้าน C-14: ล็อกต่อช่องทางตัวเดียวกับลงบัญชี/ยกเลิกการลงบัญชี — ปิด chargeback แทรกระหว่างยกเลิกการลงบัญชีไม่ได้
        SettlementChargebackResult? result = null;
        var acquired = await JobLock.RunExclusiveAsync(_db, SettlementChannelLock.Scope, SettlementChannelLock.Part(channelId),
            async () => { result = await ResolveChargebackCoreAsync(companyId, lineId, won, userId, ct); }, _logger, companyId, ct);
        return acquired && result is not null ? result : new SettlementChargebackResult(false, BusyMessage, null, null);
    }

    /// <summary>JE ปิด chargeback ที่ยังมีผล (ไม่ถูกลบ · ไม่ถูกกลับรายการ) — นิยามเดียวของ "ปิดไว้แล้ว" ทั้งด่านกันลงซ้ำและปุ่มบนหน้าจอ (D-04)</summary>
    private IQueryable<JournalEntry> OpenChargebackEntries(Guid companyId, IReadOnlyCollection<string> references)
    {
        var refs = references.ToList();
        return _db.JournalEntries.Where(j => j.CompanyId == companyId && !j.IsDeleted && j.Reference != null
            && refs.Contains(j.Reference) && j.ReversedByEntryId == null);
    }

    public async Task<IReadOnlyList<SettlementClosedChargeback>> ClosedChargebacksAsync(Guid companyId, IReadOnlyCollection<Guid> lineIds,
        CancellationToken ct = default)
    {
        if (lineIds.Count == 0) return Array.Empty<SettlementClosedChargeback>();
        var byRef = lineIds.Distinct().ToDictionary(id => SettlementPostingKeys.ChargebackReference(id), id => id, StringComparer.Ordinal);
        var refs = byRef.Keys.ToList();
        var rows = await OpenChargebackEntries(companyId, refs).AsNoTracking()
            .Select(j => new { j.Id, j.EntryNumber, j.Reference, j.Description })
            .ToListAsync(ct);
        return rows.Where(r => r.Reference != null && byRef.ContainsKey(r.Reference))
            .GroupBy(r => byRef[r.Reference!])
            .Select(g => g.First())
            .Select(r => new SettlementClosedChargeback(byRef[r.Reference!], r.Id, r.EntryNumber, r.Description))
            .ToList();
    }

    private async Task<SettlementChargebackResult> ResolveChargebackCoreAsync(Guid companyId, Guid lineId, bool won, Guid userId,
        CancellationToken ct)
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
        var done = await OpenChargebackEntries(companyId, new[] { reference }).AsNoTracking()
            .Select(j => new { j.Id, j.EntryNumber }).FirstOrDefaultAsync(ct);
        if (done != null)
            return new SettlementChargebackResult(true, $"ปิดรายการ chargeback นี้ไว้แล้ว (ใบสำคัญ {done.EntryNumber}) — ไม่ลงซ้ำ",
                done.Id, done.EntryNumber);

        var plan = SettlementBatchMath.PlanChargebackResolution(line.Amount, won, loaded.Channel, reference);
        var chart = new SettlementChartIndex(await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .Select(a => new SettlementChartAccount(a.Id, a.AccountCode, a.IsActive, a.AccountName)).ToListAsync(ct));
        var (resolved, errors) = SettlementAccountResolver.ResolveJournal(plan, chart, null);
        if (errors.Count > 0)
            return new SettlementChargebackResult(false, string.Join(" · ", errors), null, null);

        var day = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();   // review198-C C-20 (ดู CommitPostedAsync)
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            // กันกดซ้ำพร้อมกัน — ล็อกต่อบรรทัด แล้วตรวจซ้ำใต้ล็อก
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, SettlementPostingKeys.LockScope, reference) }, ct);
            if (await OpenChargebackEntries(companyId, new[] { reference }).AnyAsync(ct))
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

    /// <summary>ข้อความเดียวกับฝั่งนำเข้า (<see cref="SettlementChannelLock.BusyMessage"/> · review198-S3 S3-9)</summary>
    private const string BusyMessage = SettlementChannelLock.BusyMessage;

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

    private static SettlementMarkerPayment MarkerOf(Payment p) => new(p.Id, p.DocumentId, p.Amount, p.PaymentNumber);

    private static string FirstBlocking(SettlementPostingPlan plan)
    {
        var first = plan.Issues.FirstOrDefault(i => i.Blocking);
        return first == null ? "ลงบัญชีไม่ได้" : $"{first.Message} — {first.NextStep}";
    }
}
