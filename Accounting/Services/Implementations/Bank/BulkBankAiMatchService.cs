using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Ai.Prompts;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Bank;

/// <summary>
/// One-shot bulk bank reconciliation — bundles a month of unmatched
/// bank statement lines + every open document + open JE + open payment
/// + company context and hands them to AI in a single call. Returns a
/// complete match plan (1:1, M:1, 1:M), an unmatched list with reasons,
/// and a "missing data" list when AI sees a memo citing a doc number
/// that isn't in the candidate set.
///
/// User experience: ONE button — "AI match ทั้งเดือน". Output is shown
/// in a review modal where user can accept all, accept selectively, or
/// reject and fall back to manual matching.
///
/// Safety: applying matches is delegated to existing
/// IBankService.BatchReconcileAsync — battle-tested atomic write path.
/// This service only PRODUCES the plan.
/// </summary>
public interface IBulkBankAiMatchService
{
    Task<BulkAiMatchPlan> ProposeAsync(Guid companyId, Guid bankAccountId,
        DateTime fromDate, DateTime toDate, CancellationToken ct);

    /// <summary>Record user's per-match accept/reject after the bulk
    /// modal action — feeds the BankMatchDistillationModel training
    /// corpus. Called by the controller right after batch-reconcile
    /// applies, for each match the user accepted (or rejected).</summary>
    Task RecordMatchOutcomesAsync(Guid companyId,
        IReadOnlyList<BulkMatchOutcome> outcomes, CancellationToken ct);
}

public sealed record BulkMatchOutcome(
    Guid PerMatchFeedbackId,
    string ChosenCandidateJson,    // JSON of the chosen Payment/JE id (same shape as predicted)
    bool AcceptedAi);              // true = accepted AI's suggestion as-is; false = picked alternative or rejected

public sealed record BulkAiMatchPlan(
    Guid? FeedbackId,
    string Status,                            // "Success" | "AiUnavailable" | "EmptyInput" | "Truncated"
    int BankTxnsConsidered,
    int CandidatesConsidered,
    IReadOnlyList<ProposedMatch> Matches,
    IReadOnlyList<UnmatchedTxn> Unmatched,
    IReadOnlyList<MissingDataHint> MissingData,
    IReadOnlyList<string> Warnings,
    string? Reasoning,
    string? ProviderModel);

public sealed record ProposedMatch(
    Guid BankTxnId,
    string MatchType,                         // "OneToOne" | "OneBankToManyDocs" | "ManyBanksToOneDoc"
    IReadOnlyList<MatchCandidate> Candidates,
    decimal Confidence,
    string? Reasoning,
    Guid PerMatchFeedbackId,                  // child feedback row id — UI passes back when user accepts/rejects
    Guid? MatchGroupId = null,                // set on ManyBanksToOneDoc rows: all bank lines sharing it
                                              // settle ONE document via the M:N group path, not BatchReconcile
    // Bank-line details carried IN the plan so the UI never depends on the
    // (paged/filtered) transaction list being loaded to show the amount.
    decimal BankAmount = 0m,
    DateTime? BankDate = null,
    string? BankMemo = null,
    string? BankDirection = null);            // "In" | "Out"

public sealed record MatchCandidate(
    Guid CandidateId,
    string CandidateType,                     // "Document" | "Payment" | "JournalEntry"
    decimal Amount,
    string? Label = null);                    // human doc number (RV-…/PV-…) resolved for the UI

public sealed record UnmatchedTxn(Guid BankTxnId, string Reason, string? SuggestedAction);
public sealed record MissingDataHint(Guid BankTxnId, string MissingType, string Hint);

public class BulkBankAiMatchService : IBulkBankAiMatchService
{
    private const int MaxBankTxns = 150;
    // Bumped from 80 → 200 per kind (docs / payments / JEs). The cap exists so
    // the prompt fits the provider's context window; 200 × 3 kinds + 150 txns
    // is still well within DeepSeek-V3's 128k limit, and 80 was truncating
    // real Thai SME books mid-month. Tune down if a provider with a smaller
    // context is selected.
    private const int MaxCandidatesPerKind = 200;

    private readonly AccountingDbContext _db;
    private readonly IAiOrchestrator _orchestrator;
    private readonly IAiFeedbackRecorder _recorder;
    private readonly ILogger<BulkBankAiMatchService> _logger;

    public BulkBankAiMatchService(AccountingDbContext db, IAiOrchestrator orchestrator,
        IAiFeedbackRecorder recorder,
        ILogger<BulkBankAiMatchService> logger)
    { _db = db; _orchestrator = orchestrator; _recorder = recorder; _logger = logger; }

    /// <summary>Per-match feedback synthesis — for each proposed match
    /// we write a child feedback row in the SINGLE-MATCH shape that
    /// BankMatchDistillationModel knows how to parse. Cost stays
    /// attributed to the parent bulk call.</summary>
    private static AiFeedbackRecord BuildPerMatchFeedbackRecord(Guid companyId, Guid bankTxnId,
        BankTransaction txn, string answerJson, decimal confidence, Guid parentFeedbackId)
    {
        // Single-match shape that BankMatchDistillationModel.ExtractKey
        // recognises (description signature + amount bucket).
        var perMatchJson = JsonSerializer.Serialize(new
        {
            bankTxn = new
            {
                description = txn.Description,
                reference = txn.Reference,
                payee = txn.Payee,
                amount = Math.Abs(txn.Amount),
                direction = txn.Amount >= 0 ? "In" : "Out",
            },
        });
        return new AiFeedbackRecord(
            CompanyId: companyId, FeatureKey: AiFeatureKey.BankStatementMatch,
            PromptHash: "", PromptJson: perMatchJson,
            ResponseJson: null,
            AiPrimaryAnswer: answerJson, AiConfidence: confidence,
            LocalModelAnswer: null, LocalModelConfidence: null, LocalModelVersion: null,
            SourceEntityType: "BankTransaction", SourceEntityId: bankTxnId,
            Status: AiCallStatus.Success, ProviderUsed: AiProviderType.DeepSeek,
            ModelVersion: null,
            LatencyMs: 0, InputTokens: 0, OutputTokens: 0, CostUsd: 0m,
            CacheHitOfFeedbackId: parentFeedbackId == Guid.Empty ? null : parentFeedbackId,
            ErrorMessage: null);
    }

    public async Task RecordMatchOutcomesAsync(Guid companyId,
        IReadOnlyList<BulkMatchOutcome> outcomes, CancellationToken ct)
    {
        foreach (var o in outcomes)
        {
            if (o.PerMatchFeedbackId == Guid.Empty) continue;
            try
            {
                await _recorder.RecordUserChoiceAsync(o.PerMatchFeedbackId,
                    o.ChosenCandidateJson, o.AcceptedAi, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bulk match outcome record failed for fid {Id}", o.PerMatchFeedbackId);
            }
        }
    }

    public async Task<BulkAiMatchPlan> ProposeAsync(Guid companyId, Guid bankAccountId,
        DateTime fromDate, DateTime toDate, CancellationToken ct)
    {
        // Normalise the window to WHOLE days. The UI sends date-only values
        // (e.g. 2026-06-30) which model-bind to midnight, so a plain
        // "TransactionDate <= toDate" would drop every transaction on the
        // last day that carries a time component (imported rows often do).
        // Snap fromDate to start-of-day and toDate to the last tick of its
        // day so the range is inclusive of the entire last day.
        fromDate = fromDate.Date;
        toDate = toDate.Date.AddDays(1).AddTicks(-1);

        // ── 1. Company + bank account context ──────────────────────────
        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new BulkBankMatchPrompt.CompanyContext(
                c.Name, c.TaxId, c.BaseCurrency,
                c.FiscalYearStartMonth, c.IsVatRegistered))
            .FirstOrDefaultAsync(ct);
        if (company == null)
            return Empty("Company not found");

        var bank = await _db.BankAccounts.AsNoTracking()
            .Where(b => b.Id == bankAccountId && b.CompanyId == companyId)
            .Select(b => new
            {
                b.AccountName, b.BankName, b.AccountNumber, b.Currency,
                b.CurrentBalance, b.LinkedAccountId,
            })
            .FirstOrDefaultAsync(ct);
        if (bank == null)
            return Empty("Bank account not found");

        // ── 2. GL balance for the linked account (provides "off-by"
        // ── signal so AI can spot discrepancies the bank balance alone
        // ── doesn't reveal) ─────────────────────────────────────────────
        decimal? glBalance = null;
        if (bank.LinkedAccountId.HasValue)
        {
            var balRows = await _db.JournalEntryLines.AsNoTracking()
                .Where(l => l.AccountId == bank.LinkedAccountId.Value
                            && !l.IsDeleted
                            && l.JournalEntry.Status == JournalEntryStatus.Posted
                            && l.JournalEntry.EntryDate <= toDate)
                .Select(l => new { l.DebitAmount, l.CreditAmount })
                .ToListAsync(ct);
            glBalance = balRows.Sum(r => r.DebitAmount - r.CreditAmount);
        }

        // ── 3. Unmatched bank transactions in the window ────────────────
        var txns = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.BankAccountId == bankAccountId
                        && t.CompanyId == companyId
                        && !t.IsDeleted
                        && t.TransactionDate >= fromDate
                        && t.TransactionDate <= toDate
                        && t.ReconciliationStatus != ReconciliationStatus.Matched
                        && t.ReconciliationStatus != ReconciliationStatus.Excluded)
            .OrderBy(t => t.TransactionDate)
            .Take(MaxBankTxns + 1)               // detect truncation
            .Select(t => new BulkBankMatchPrompt.BankTxnInput(
                t.Id.ToString(),
                t.TransactionDate,
                Math.Abs(t.Amount),
                t.Amount >= 0 ? "In" : "Out",
                t.Description, t.Reference, t.Payee))
            .ToListAsync(ct);
        if (txns.Count == 0)
            return Empty($"ไม่พบรายการเดินบัญชีที่ยังไม่กระทบยอดในช่วง {fromDate:d MMM yyyy} – {toDate:d MMM yyyy} " +
                "— ตรวจสอบว่าได้นำเข้า statement ของเดือนนี้แล้ว หรือรายการอาจกระทบยอดครบแล้ว/อยู่เดือนอื่น");
        var truncatedBankTxns = txns.Count > MaxBankTxns;
        if (truncatedBankTxns) txns = txns.Take(MaxBankTxns).ToList();

        // ── 4. Open documents — AR (Invoice/TaxInvoice/BillingNote) +
        // ── AP (PurchaseInvoice/Expense) with BalanceDue > 0 ────────────
        // Look BACK 90 days (an invoice raised in Jan can be paid in Apr) but
        // only a few days FORWARD past the selected period — a deposit on the
        // last day of the month may settle a receipt dated 1-2 days into the
        // next month, but pulling a full +30 days dragged genuinely-next-month
        // documents into the picture and confused the operator (April scope
        // showing May docs). +5 days covers settlement lag without the bleed.
        var windowStart = fromDate.AddDays(-90);
        var windowEnd = toDate.AddDays(5);
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                        && d.Status != DocumentStatus.Voided
                        && d.Status != DocumentStatus.Draft
                        && d.BalanceDue > 0
                        && d.DocumentDate >= windowStart
                        && d.DocumentDate <= windowEnd
                        && (d.DocumentType == DocumentType.Invoice
                            || d.DocumentType == DocumentType.TaxInvoice
                            || d.DocumentType == DocumentType.BillingNote
                            || d.DocumentType == DocumentType.PurchaseInvoice
                            || d.DocumentType == DocumentType.Expense))
            .OrderByDescending(d => d.DocumentDate)
            .Take(MaxCandidatesPerKind + 1)
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate,
                d.BalanceDue, ContactName = d.Contact.Name, d.Contact.TaxId,
            })
            .ToListAsync(ct);
        var truncatedDocs = docs.Count > MaxCandidatesPerKind;
        if (truncatedDocs) docs = docs.Take(MaxCandidatesPerKind).ToList();
        var openDocs = docs.Select(d => new BulkBankMatchPrompt.OpenDocInput(
            d.Id.ToString(), d.DocumentNumber, d.DocumentType.ToString(),
            d.DocumentDate, d.BalanceDue,
            (d.DocumentType == DocumentType.PurchaseInvoice
                || d.DocumentType == DocumentType.Expense) ? "AP" : "AR",
            d.ContactName, d.TaxId)).ToList();

        // ── 5. Open payments not yet linked to a bank txn ──────────────
        // No payment-method filter: a customer can pay cash / cheque / e-wallet
        // and the shop deposits it, so the Payment.PaymentMethod won't always be
        // BankTransfer even though it shows as a bank deposit. Matching on
        // amount + date + unmatched status (not method) maximises recall;
        // AI + the user's confirm step filter out false positives.
        var matchedPaymentIdsRaw = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.BankAccountId == bankAccountId
                        && t.MatchedPaymentId != null)
            .Select(t => t.MatchedPaymentId!.Value)
            .ToListAsync(ct);
        var matchedPaymentIds = matchedPaymentIdsRaw.ToHashSet();
        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                        && p.PaymentDate >= windowStart
                        && p.PaymentDate <= windowEnd)
            .Where(p => !matchedPaymentIds.Contains(p.Id))
            .OrderByDescending(p => p.PaymentDate)
            .Take(MaxCandidatesPerKind + 1)
            .Select(p => new
            {
                p.Id, p.PaymentNumber, p.PaymentDate, p.Amount,
                p.PaymentMethod, p.Reference,
                ContactName = p.Document.Contact.Name,
                ContactTaxId = p.Document.Contact.TaxId,
                BankAccountStr = p.BankAccount,
                p.WithholdingTaxAmount,
                p.Notes,
                DocNumber = p.Document.DocumentNumber,
                DocType = p.Document.DocumentType,
                Outstanding = p.Document.BalanceDue,
                DocVat = p.Document.VatAmount,
            })
            .ToListAsync(ct);
        var truncatedPayments = payments.Count > MaxCandidatesPerKind;
        if (truncatedPayments) payments = payments.Take(MaxCandidatesPerKind).ToList();
        var openPayments = payments.Select(p =>
        {
            string? dir = p.DocType switch
            {
                Models.Enums.DocumentType.Receipt or
                Models.Enums.DocumentType.ReceiptVoucher or
                Models.Enums.DocumentType.Invoice or
                Models.Enums.DocumentType.TaxInvoice or
                Models.Enums.DocumentType.BillingNote or
                Models.Enums.DocumentType.DebitNote => "In",
                Models.Enums.DocumentType.PaymentVoucher or
                Models.Enums.DocumentType.Expense or
                Models.Enums.DocumentType.PurchaseInvoice or
                Models.Enums.DocumentType.CertificateInLieu => "Out",
                _ => null,
            };
            return new BulkBankMatchPrompt.OpenPaymentInput(
                p.Id.ToString(), p.PaymentNumber, p.PaymentDate, p.Amount,
                p.PaymentMethod.ToString(), p.Reference, p.ContactName,
                LinkedDocumentNumber: p.DocNumber,
                LinkedDocumentType: p.DocType.ToString(),
                Direction: dir,
                ContactTaxId: p.ContactTaxId,
                BankAccountNumber: p.BankAccountStr,
                OutstandingAmount: p.Outstanding > 0 ? p.Outstanding : null,
                WithholdingTax: p.WithholdingTaxAmount > 0 ? p.WithholdingTaxAmount : null,
                VatAmount: p.DocVat > 0 ? p.DocVat : null,
                Note: p.Notes,
                Channel: p.PaymentMethod.ToString());
        }).ToList();

        // ── 6. Open journal entries (Posted, not yet linked to a bank
        // ── txn) — BOTH directions (เงินรับ + เงินจ่าย). Per user request
        // send EVERY JE in a tight ±5-day window around the selected period
        // (not the wider -90/+30 doc window) so the AI sees the complete set
        // and does the matching itself instead of us pre-filtering by amount.
        //
        // CRITICAL bug fix: the amount must NOT be Sum(Debit − Credit) — a
        // posted JE is always balanced so that sum is ALWAYS 0, which made
        // every JE look like a zero-amount entry and get dropped by the old
        // amount prefilter (→ 0 JE candidates every run). Use the amount on
        // the line that hits the bank's linked GL account (the precise match
        // target); fall back to the gross debit total (= credit total = the
        // entry's transaction size) when the bank account isn't linked.
        var jeWindowStart = fromDate.AddDays(-5);
        var jeWindowEnd = toDate.AddDays(5);
        var matchedJeIdsRaw = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.BankAccountId == bankAccountId
                        && t.MatchedJournalEntryId != null)
            .Select(t => t.MatchedJournalEntryId!.Value)
            .ToListAsync(ct);
        var matchedJeIds = matchedJeIdsRaw.ToHashSet();
        // Guid.Empty when no bank account is linked → the AccountId equality
        // below matches no line → BankLineNet = 0 → falls back to GrossAmount.
        // (Comparing to a captured Guid translates to SQL cleanly; a nullable
        // .HasValue ternary inside the projection does not.)
        var linkedAcct = bank.LinkedAccountId ?? Guid.Empty;
        // Pull the JE + (when it originated from a Document) the source
        // document's number/type/date and the counterparty contact. AI was
        // previously seeing only entry number + amount, so it could not do
        // payee-based 1:1 matching ('bank Payee=Take Time Nature Resort →
        // JE whose source Receipt was for that contact'). Joining via
        // SourceDocumentId fixes that without an extra round-trip.
        var jeRows = await (
            from j in _db.JournalEntries.AsNoTracking()
            where j.CompanyId == companyId && !j.IsDeleted
                && j.Status == JournalEntryStatus.Posted
                // Exclude entries that were later reversed (superseded) — a
                // reversed RV/PV must never be offered as a live candidate, or
                // the matcher could pick the cancelled original over the truth.
                && j.ReversedByEntryId == null
                && j.EntryDate >= jeWindowStart && j.EntryDate <= jeWindowEnd
                && !matchedJeIds.Contains(j.Id)
            from src in _db.Documents.AsNoTracking()
                .Where(d => j.SourceDocumentId != null && d.Id == j.SourceDocumentId).DefaultIfEmpty()
            from c in _db.Contacts.AsNoTracking()
                .Where(co => src != null && co.Id == src.ContactId).DefaultIfEmpty()
            orderby j.EntryDate descending
            select new
            {
                j.Id, j.EntryNumber, j.EntryDate, j.Description, j.Reference,
                BankLineNet = j.Lines.Where(l => l.AccountId == linkedAcct)
                             .Sum(l => l.DebitAmount - l.CreditAmount),
                GrossAmount = j.Lines.Sum(l => l.DebitAmount),
                LineCount = j.Lines.Count,
                Note = j.Note,
                Tags = j.Tags,
                SourceDocNumber = src != null ? src.DocumentNumber : null,
                SourceDocType = src != null ? (DocumentType?)src.DocumentType : null,
                SourceDocDate = src != null ? (DateTime?)src.DocumentDate : null,
                SrcWht = src != null ? (decimal?)src.WithholdingTaxAmount : null,
                SrcVat = src != null ? (decimal?)src.VatAmount : null,
                ContactName = c != null ? c.Name : null,
                ContactTaxId = c != null ? c.TaxId : null,
            }).ToListAsync(ct);

        // Hard ceiling so a pathological dataset can't exceed a sane prompt
        // size (DeepSeek-V3 context = 128k tokens). With a ±5-day window the
        // count is normally well under this.
        const int HardJeCeiling = 600;
        var truncatedJes = jeRows.Count > HardJeCeiling;
        if (truncatedJes) jeRows = jeRows.Take(HardJeCeiling).ToList();
        var openJes = jeRows.Select(j =>
        {
            // Prefer the bank-line amount (exact match target); fall back to
            // gross. abs() because the prompt compares magnitudes — direction
            // is conveyed separately by the bank txn's In/Out.
            // Primary amount = bank-line net when the JE touches the linked
            // bank GL account (the precise figure that hit the statement),
            // else the gross transaction size. BUT also pass gross_amount
            // separately so AI can match a deposit against EITHER — a
            // withholding-tax receipt nets the bank line below gross, while
            // a plain receipt has bank-line == gross. Sending only one was
            // why an exact same-day 5,000 receipt got missed when its JE
            // bank line differed from the gross.
            var bankLine = j.BankLineNet;                                  // SIGNED
            var gross = j.GrossAmount;
            var amount = bankLine != 0m ? Math.Abs(bankLine) : gross;
            // SourceDocType is the authoritative direction hint when present;
            // otherwise we infer from the bank-line sign (Debit > Credit = In).
            string? dir = j.SourceDocType switch
            {
                Models.Enums.DocumentType.Receipt or
                Models.Enums.DocumentType.ReceiptVoucher or
                Models.Enums.DocumentType.Invoice or
                Models.Enums.DocumentType.TaxInvoice or
                Models.Enums.DocumentType.BillingNote or
                Models.Enums.DocumentType.DebitNote => "In",
                Models.Enums.DocumentType.PaymentVoucher or
                Models.Enums.DocumentType.Expense or
                Models.Enums.DocumentType.PurchaseInvoice or
                Models.Enums.DocumentType.CertificateInLieu => "Out",
                _ => bankLine > 0 ? "In" : (bankLine < 0 ? "Out" : null),
            };
            return new BulkBankMatchPrompt.OpenJeInput(
                j.Id.ToString(), j.EntryNumber, j.EntryDate, amount,
                j.Description, j.Reference,
                j.SourceDocNumber,
                j.SourceDocType?.ToString(),
                j.SourceDocDate,
                j.ContactName,
                j.ContactTaxId,
                gross != amount ? gross : (decimal?)null,
                dir,
                WithholdingTax: j.SrcWht is > 0 ? j.SrcWht : null,
                VatAmount: j.SrcVat is > 0 ? j.SrcVat : null,
                FeeAmount: null,
                LineCount: j.LineCount,
                PaymentMethod: null,
                Note: j.Note,
                Tags: j.Tags);
        }).ToList();

        // ── 6b. Zero-candidate guard ───────────────────────────────────
        // If nothing made it through the AR/AP-doc + payment + JE filters,
        // calling AI is pointless (it can only reply "nothing to match" —
        // exactly the wasted call the user just saw). Instead diagnose WHY
        // by counting what exists in the window IGNORING our filters, so
        // the message is actionable ("you have 60 docs but all are fully
        // paid" vs "you've recorded 0 payments — import/create them first").
        if (openDocs.Count + openPayments.Count + openJes.Count == 0)
        {
            var docsInWindowAny = await _db.Documents.AsNoTracking()
                .CountAsync(d => d.CompanyId == companyId && !d.IsDeleted
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                    && d.DocumentDate >= windowStart && d.DocumentDate <= windowEnd, ct);
            var docsOpenAny = await _db.Documents.AsNoTracking()
                .CountAsync(d => d.CompanyId == companyId && !d.IsDeleted
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                    && d.BalanceDue > 0
                    && d.DocumentDate >= windowStart && d.DocumentDate <= windowEnd, ct);
            var paymentsInWindow = await _db.Payments.AsNoTracking()
                .CountAsync(p => p.CompanyId == companyId && !p.IsDeleted
                    && p.PaymentDate >= windowStart && p.PaymentDate <= windowEnd, ct);
            // JEs are loaded from the tighter ±5-day window — count there so
            // the diagnostic reason matches the actual filter.
            var jesInWindow = await _db.JournalEntries.AsNoTracking()
                .CountAsync(j => j.CompanyId == companyId && !j.IsDeleted
                    && j.Status == JournalEntryStatus.Posted
                    && j.EntryDate >= jeWindowStart && j.EntryDate <= jeWindowEnd, ct);
            var jesAllMatched = await _db.JournalEntries.AsNoTracking()
                .CountAsync(j => j.CompanyId == companyId && !j.IsDeleted
                    && j.Status == JournalEntryStatus.Posted
                    && j.EntryDate >= jeWindowStart && j.EntryDate <= jeWindowEnd
                    && matchedJeIds.Contains(j.Id), ct);

            var diag = new List<string>
            {
                $"พบ bank txn ที่ยังไม่กระทบยอด {txns.Count} รายการ แต่ไม่มีรายการในระบบให้จับคู่เลย " +
                $"(ค้นในช่วง {windowStart:d MMM yyyy} – {windowEnd:d MMM yyyy})",
            };
            // Tell them exactly which bucket is empty + the likely fix.
            if (docsInWindowAny == 0 && paymentsInWindow == 0 && jesInWindow == 0)
                diag.Add("• ยังไม่มีเอกสาร / การชำระเงิน / รายการบัญชีในช่วงนี้เลย — ถ้านี่คือเงินรับจากลูกค้า ให้สร้าง Invoice/ใบเสร็จ หรือบันทึก Payment/Journal Entry ก่อน แล้วค่อย match");
            else
            {
                if (docsInWindowAny > 0)
                    diag.Add($"• มีเอกสาร {docsInWindowAny} ฉบับในช่วงนี้ แต่ {(docsOpenAny == 0 ? "ทั้งหมดชำระครบแล้ว (ยอดคงค้าง = 0)" : $"มีเพียง {docsOpenAny} ฉบับที่ยังค้างชำระ และไม่ใช่ประเภท Invoice/TaxInvoice/BillingNote/PurchaseInvoice/Expense")} — เงินที่รับอาจถูกบันทึกเป็นใบเสร็จ (Receipt) ที่ปิดยอดแล้ว");
                if (paymentsInWindow > 0)
                    diag.Add($"• มี Payment {paymentsInWindow} รายการในช่วงนี้ แต่ทั้งหมดถูกจับคู่กับ bank txn อื่นไปแล้ว");
                if (jesInWindow > 0)
                    diag.Add($"• มี Journal Entry {jesInWindow} รายการในช่วง ±5 วัน แต่{(jesAllMatched >= jesInWindow ? "ทั้งหมดถูกจับคู่กับ bank txn อื่นไปแล้ว" : "ถูกจับคู่ไปแล้วบางส่วน")}");
                diag.Add("→ ตรวจว่าได้สร้าง Invoice/Payment/JE สำหรับเงินรับเหล่านี้แล้วหรือยัง");
            }

            return new BulkAiMatchPlan(
                FeedbackId: null,
                Status: "NoCandidates",
                BankTxnsConsidered: txns.Count,
                CandidatesConsidered: 0,
                Matches: Array.Empty<ProposedMatch>(),
                Unmatched: txns.Select(t => new UnmatchedTxn(
                    Guid.Parse(t.Id),
                    "ไม่มีรายการในระบบให้จับคู่",
                    "สร้าง Invoice/ใบเสร็จ หรือบันทึก Payment/Journal Entry สำหรับเงินจำนวนนี้")).ToList(),
                MissingData: Array.Empty<MissingDataHint>(),
                Warnings: diag,
                Reasoning: null,
                ProviderModel: null);
        }

        var bankContext = new BulkBankMatchPrompt.BankAccountContext(
            bankAccountId.ToString(), bank.AccountName, bank.BankName,
            bank.AccountNumber, bank.Currency,
            bank.CurrentBalance, glBalance);

        // ── 7. Build prompt + ask orchestrator ─────────────────────────
        var req = BulkBankMatchPrompt.Build(companyId, bankAccountId, fromDate, toDate,
            company, bankContext, txns, openDocs, openPayments, openJes);
        var resp = await _orchestrator.AskAsync(req, ct);

        // ── 8. Parse — defensive because AI output is JSON-but-fallible ─
        // Prefer RawResponseJson when present (RawPlanResponse path); fall
        // back to PrimaryAnswer for older code paths.
        var rawOut = !string.IsNullOrWhiteSpace(resp.RawResponseJson) ? resp.RawResponseJson : resp.PrimaryAnswer;
        var parsed = ParseResponse(rawOut);

        // Diagnostic: when AI ran successfully but produced ZERO outputs of
        // every kind (no matches, no unmatched, no missing_data), surface the
        // actual content so the operator isn't staring at a silent "0/0/0".
        // Common causes: AI emitted a different top-level key, returned an
        // empty {} object, or replied with prose instead of strict JSON.
        var aiSucceeded = resp.Status == AiCallStatus.Success || resp.Status == AiCallStatus.Cached;
        var allEmpty = parsed.Matches.Count == 0 && parsed.Unmatched.Count == 0 && parsed.MissingData.Count == 0;
        if (aiSucceeded && allEmpty)
        {
            _logger.LogWarning(
                "Bulk AI match returned empty result. Status={Status} Provider={Provider} BankTxns={Txns} Candidates={Cands} (docs={D}/payments={P}/jes={J}) RawLen={Len}\nRaw response:\n{Raw}",
                resp.Status, resp.ProviderModel, txns.Count,
                openDocs.Count + openPayments.Count + openJes.Count,
                openDocs.Count, openPayments.Count, openJes.Count,
                rawOut?.Length ?? 0, rawOut ?? "(null)");

            if (string.IsNullOrWhiteSpace(rawOut))
                parsed = parsed with { Warnings = parsed.Warnings.Concat(new[] {
                    "AI ตอบกลับเป็นว่าง (status=" + resp.Status + ") — ลองอีกครั้ง หรือเช็คการตั้งค่า AI provider"
                }).ToList() };
            else
            {
                var preview = rawOut.Length > 600 ? rawOut[..600] + "..." : rawOut;
                parsed = parsed with { Warnings = parsed.Warnings.Concat(new[] {
                    $"AI ตอบมาแล้ว ({rawOut.Length} chars) แต่ parse ไม่เจอ matches/unmatched/missing_data — ตัวอย่างคำตอบ: " + preview
                }).ToList() };
            }
        }

        // ── 8a. Post-AI subset-sum sweep — DeepSeek consistently leaves
        // ── 'one bank deposit = many JEs' style lumps in the unmatched
        // ── 8a. Server-side 1:1 sweep — ALWAYS try exact one-to-one first.
        // ── User: "จับคู่ one to one ก่อนเสมอ ... ระบบไปจับ one to many ก่อน
        // ── รึเปล่า". For every still-unmatched bank txn, look for a single
        // ── still-unconsumed Payment OR JE whose amount (or gross) matches
        // ── the bank line within ±0.50 baht AND the bank-line direction
        // ── matches AND |date diff| ≤ 7 days. A 1:1 hit ALWAYS beats any
        // ── M:1 combination, so this runs before TryCombineLumpedJes.
        parsed = TryExactOneToOne(parsed, txns, openPayments, openJes);

        // ── 8a-ii. Greedy pairing of EQUAL-amount, same-day candidates. When
        // ── K unmatched bank lines of amount X share a date with exactly K
        // ── open candidates of amount X (and none carries an identity signal),
        // ── pairing any-to-any reconciles the total correctly — common with
        // ── the hotel's many identical 800-baht 'การจอง #0' RVs.
        parsed = TryGreedyEqualAmount(parsed, txns, openPayments, openJes);

        // ── 8a-iii. Many-banks-to-one: several deposits SUM to ONE document.
        // ── Emitted with a shared MatchGroupId so the UI routes them through
        // ── the ReconciliationGroup (M:N) apply path (partial allocation),
        // ── never BatchReconcile.
        parsed = TryManyBanksToOne(parsed, txns, openPayments, openJes);

        // ── 8b. Multi-JE combination fallback (the lumped-deposit case).
        parsed = TryCombineLumpedJes(parsed, txns, openJes);

        // ── 8c. DEDUPLICATE — the AI (and mixed AI+server passes) can reference
        // ── the SAME document/payment in two different matches, or the same
        // ── bank line twice. Keep the best owner of each and demote the rest to
        // ── unmatched so a candidate/bank line is never double-applied.
        parsed = DeduplicateMatches(parsed);

        // ── 8b. Per-match child feedback rows — so when user accepts /
        // ── edits each row in the modal, BankMatchDistillationModel can
        // ── learn per-signature instead of one big undifferentiated row.
        // ── Need the BankTransaction entities again (with description /
        // ── reference / payee) to populate the single-match shape.
        var bankTxnLookup = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.BankAccountId == bankAccountId && !t.IsDeleted
                        && parsed.Matches.Select(m => m.BankTxnId).Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t, ct);
        // Doc-number + REAL amount lookup. The UI shows "RV-202604-0500 (500)"
        // AND — critically — we OVERRIDE the candidate amount with the true
        // figure from our DB. The AI sometimes HALLUCINATES the amount (returns
        // RV-0570 with amount 2,000 to fake a match against a 2,000 deposit when
        // RV-0570 is really 950), which then sailed past CalibrateConfidence
        // because that summed the AI's claimed amounts. Using the real amount
        // exposes the lie (sum no longer matches → low confidence + flagged).
        var labelById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var realAmountById = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in openJes)
        {
            labelById[j.Id] = j.Number;
            realAmountById[j.Id] = j.GrossAmount ?? j.NetAmount;   // document face value
        }
        foreach (var p in openPayments) { labelById[p.Id] = p.Number; realAmountById[p.Id] = p.Amount; }
        foreach (var d in openDocs) { labelById[d.Id] = d.Number; realAmountById[d.Id] = d.Outstanding; }

        // Identity + source-date lookup so a CONFIRMED 1:1 can be upgraded to
        // 0.99 even when the AI proposed it (the server sweep skips bank lines
        // the AI already claimed, so the boost there alone wouldn't reach them).
        var idInfoById = new Dictionary<string, MatchIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in openJes)
            idInfoById[j.Id] = new(j.ContactName, j.Reference, j.SourceDocNumber, j.ContactTaxId, null, j.Date);
        foreach (var p in openPayments)
            idInfoById[p.Id] = new(p.ContactName, p.Reference, p.LinkedDocumentNumber, p.ContactTaxId, p.BankAccountNumber, p.Date);
        foreach (var d in openDocs)
            idInfoById[d.Id] = new(d.ContactName, null, d.Number, d.ContactTaxId, null, d.Date);

        // First pass: calibrate every match (CPU only) and build its feedback
        // record. Second pass: ONE batch insert for all records (was N separate
        // SaveChanges — 100 matches = 100 DB round-trips blocking the response).
        var calibrated = new List<ProposedMatch>(parsed.Matches.Count);
        var feedbackRecords = new List<AiFeedbackRecord>();
        var recordIndexOfMatch = new int[parsed.Matches.Count];   // -1 = no feedback row
        int mi = -1;
        foreach (var m0 in parsed.Matches)
        {
            mi++;
            var m = m0;
            if (!bankTxnLookup.TryGetValue(m.BankTxnId, out var bt))
            {
                calibrated.Add(m with { PerMatchFeedbackId = Guid.Empty });
                recordIndexOfMatch[mi] = -1;
                continue;
            }

            // OVERRIDE each candidate's amount + label with the TRUE values from
            // our DB BEFORE prune/calibrate, so an AI-hallucinated amount can't
            // pass the confidence check. (Server-built matches already carry the
            // real amount; the override is a no-op for them.)
            m = m with
            {
                Candidates = m.Candidates.Select(c =>
                {
                    var key = c.CandidateId.ToString();
                    var amt = realAmountById.TryGetValue(key, out var ra) ? ra : c.Amount;
                    return c with { Amount = amt, Label = labelById.GetValueOrDefault(key) };
                }).ToList(),
            };

            // Trust-but-verify: if the proposed candidates sum off-by but a
            // SUBSET of them sums to the bank amount EXACTLY (the AI added
            // one extra item — common pattern: 7 RVs sum to 26,550 exactly,
            // AI added an 8th 80-baht RV making sum 26,630), drop the extras.
            // This is what fixes the user's "8 items = พอดี + ยอดต่าง 80" case.
            m = TryPruneToExactSubset(bt, m);

            // Trust-but-verify the confidence AI returned: it routinely
            // reports 0.95 in the same row whose own 'reasoning' admits the
            // amounts don't match. Cap confidence using OBJECTIVE signals
            // computed from the actual numbers — amount delta vs the bank txn
            // and date proximity to the candidate's source row when known. AI
            // confidence then becomes a ceiling; we never push it higher.
            var (calibratedConf, mismatchNote) = CalibrateConfidence(bt, m);

            // CONFIRMED 1:1 → 0.99. A single-candidate match whose amount agrees
            // to the satang, sits within a day of its source document, and whose
            // IDENTITY (contact name / reference / doc-no / tax id / bank-account
            // tail) is quoted in the bank memo is as certain as reconciliation
            // gets — the bank line is merely net of a small fee/WHT. Works for
            // BOTH AI- and server-proposed matches so it sorts + auto-checks
            // first. Only when calibration found no amount mismatch.
            if (string.IsNullOrEmpty(mismatchNote) && m.MatchGroupId is null
                && m.Candidates.Count == 1
                && idInfoById.TryGetValue(m.Candidates[0].CandidateId.ToString(), out var idInfo))
            {
                var bankAmt = Math.Abs(bt.Amount);
                bool exactToSatang = Math.Abs(bankAmt - m.Candidates[0].Amount) <= 0.01m;
                bool nearDay = Math.Abs((idInfo.Date.Date - bt.TransactionDate.Date).TotalDays) <= 1;
                var memoBlob = (bt.Description ?? "") + " " + (bt.Reference ?? "") + " " + (bt.Payee ?? "");
                if (exactToSatang && nearDay && MemoConfirmsIdentity(memoBlob, idInfo))
                    calibratedConf = Math.Max(calibratedConf, 0.99m);
            }
            // AI sometimes writes contradictory reasoning ("พอดี" alongside
            // "ยอดต่าง 80") — strip the AI's misleading sum claim when our
            // computed delta says otherwise, then append the honest note.
            var aiReasoning = mismatchNote.Length > 0 ? ScrubMisleadingSumClaim(m.Reasoning) : m.Reasoning;
            var calReason = string.IsNullOrEmpty(mismatchNote)
                ? aiReasoning
                : (string.IsNullOrEmpty(aiReasoning) ? mismatchNote : aiReasoning + " · " + mismatchNote);
            // Candidates already carry real amounts + labels (set above).
            var calibratedMatch = m with
            {
                Confidence = calibratedConf,
                Reasoning = calReason,
                BankAmount = Math.Abs(bt.Amount),
                BankDate = bt.TransactionDate,
                BankMemo = bt.Description ?? bt.Payee,
                BankDirection = bt.TransactionType is BankTransactionType.Deposit or BankTransactionType.Interest ? "In" : "Out",
            };

            var answerJson = JsonSerializer.Serialize(calibratedMatch.Candidates.Select(c => new
            {
                type = c.CandidateType, id = c.CandidateId.ToString(), amount = c.Amount,
            }));
            recordIndexOfMatch[mi] = feedbackRecords.Count;
            feedbackRecords.Add(BuildPerMatchFeedbackRecord(
                companyId, calibratedMatch.BankTxnId, bt, answerJson, calibratedMatch.Confidence,
                resp.FeedbackId ?? Guid.Empty));
            calibrated.Add(calibratedMatch);   // PerMatchFeedbackId filled in after the batch insert
        }

        // ── Single batch insert for all per-match feedback rows ──────────
        var fids = await _recorder.RecordChildBatchAsync(feedbackRecords, ct);
        var matchesWithFids = new List<ProposedMatch>(calibrated.Count);
        for (int i = 0; i < calibrated.Count; i++)
        {
            var ri = recordIndexOfMatch[i];
            var fid = ri >= 0 && ri < fids.Count ? fids[ri] : Guid.Empty;
            matchesWithFids.Add(calibrated[i] with { PerMatchFeedbackId = fid });
        }

        // Collect truncation + AI-side warnings into one cohesive list.
        var warnings = new List<string>(parsed.Warnings);
        if (truncatedBankTxns)
            warnings.Add($"จำกัด bank txn ที่ {MaxBankTxns} รายการ — สัปดาห์ที่เก่ากว่าไม่ได้ส่งให้ AI");
        if (truncatedDocs)
            warnings.Add($"จำกัดเอกสารที่ {MaxCandidatesPerKind} รายการ — เอกสารเก่ากว่า 90 วันถูกตัด");
        if (truncatedPayments)
            warnings.Add($"จำกัด open payments ที่ {MaxCandidatesPerKind} รายการ");
        if (truncatedJes)
            warnings.Add($"JE ในช่วง ±5 วันมีมากกว่า {HardJeCeiling} รายการ — ส่งให้ AI แค่ {HardJeCeiling} ตัวล่าสุด");
        if (resp.Status == AiCallStatus.Failed || resp.Status == AiCallStatus.NoProvider
            || resp.Status == AiCallStatus.BudgetExceeded)
            warnings.Add($"AI ไม่ทำงาน ({resp.Status}): {resp.Reasoning ?? "—"}. กรุณา match ด้วยมือ");

        var status = resp.Status == AiCallStatus.Success || resp.Status == AiCallStatus.Cached
            ? (truncatedBankTxns || truncatedDocs || truncatedPayments || truncatedJes
                ? "Truncated" : "Success")
            : "AiUnavailable";

        return new BulkAiMatchPlan(
            FeedbackId: resp.FeedbackId,
            Status: status,
            BankTxnsConsidered: txns.Count,
            CandidatesConsidered: openDocs.Count + openPayments.Count + openJes.Count,
            Matches: matchesWithFids,
            Unmatched: parsed.Unmatched,
            MissingData: parsed.MissingData,
            Warnings: warnings,
            Reasoning: resp.Reasoning,
            ProviderModel: resp.ProviderModel);
    }

    private static BulkAiMatchPlan Empty(string reason) =>
        new(null, "EmptyInput", 0, 0,
            Array.Empty<ProposedMatch>(), Array.Empty<UnmatchedTxn>(),
            Array.Empty<MissingDataHint>(), new[] { reason }, null, null);

    private sealed record ParsedResponse(
        IReadOnlyList<ProposedMatch> Matches,
        IReadOnlyList<UnmatchedTxn> Unmatched,
        IReadOnlyList<MissingDataHint> MissingData,
        IReadOnlyList<string> Warnings);

    /// <summary>Best-effort JSON parse — never throws. AI hallucinations
    /// or partial JSON degrade to empty lists + a warning rather than
    /// crashing the endpoint.</summary>
    private ParsedResponse ParseResponse(string? raw)
    {
        var matches = new List<ProposedMatch>();
        var unmatched = new List<UnmatchedTxn>();
        var missing = new List<MissingDataHint>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return new(matches, unmatched, missing, warnings);

        // AI may wrap in markdown — strip ```json fences before parse.
        var json = raw.Trim();
        if (json.StartsWith("```"))
        {
            var nl = json.IndexOf('\n');
            if (nl > 0) json = json[(nl + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }

        // AI sometimes prefixes or suffixes the JSON with prose ("Here is
        // the plan: { ... }" or "{ ... } Notes: ..."). JsonDocument.Parse
        // fails strict on any character outside the document, so extract
        // just the outermost {...} block (first '{' through the matching
        // last '}'). Brace-counting tolerates nested objects + braces
        // inside string values reliably for well-formed JSON.
        var startBrace = json.IndexOf('{');
        if (startBrace >= 0)
        {
            int depth = 0, endBrace = -1; bool inString = false, esc = false;
            for (int i = startBrace; i < json.Length; i++)
            {
                var ch = json[i];
                if (esc) { esc = false; continue; }
                if (inString)
                {
                    if (ch == '\\') esc = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; continue; }
                if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0) { endBrace = i; break; }
                }
            }
            if (endBrace > startBrace) json = json[startBrace..(endBrace + 1)];
        }

        try
        {
            // Tolerant parse options for AI-generated JSON:
            //   • AllowTrailingCommas: DeepSeek frequently emits "[…,]" / "{…,}"
            //     which strict Json.NET-style parsing rejects.
            //   • CommentHandling=Skip: AI sometimes inlines "// note" comments.
            //   • MaxDepth bump: nested candidate arrays don't go deep but
            //     stays defensive.
            JsonDocumentOptions opts = new()
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64,
            };
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json, opts); }
            catch (JsonException)
            {
                // Last-ditch: strip raw control chars that escaped into string
                // values (unescaped newlines / tabs in 'reasoning' text are the
                // common offender from chat-model outputs).
                doc = JsonDocument.Parse(StripControlCharsInsideStrings(json), opts);
            }
            using var _docDispose = doc;
            var root = doc.RootElement;
            // DeepSeek occasionally wraps the plan under "result" / "response" /
            // "data" / "output" or a leading "plan" key. Unwrap one level when
            // none of the expected top-level keys are present at the root.
            if (root.ValueKind == JsonValueKind.Object
                && !root.TryGetProperty("matches", out _)
                && !root.TryGetProperty("unmatched", out _)
                && !root.TryGetProperty("missing_data", out _))
            {
                foreach (var wrap in new[] { "result", "response", "data", "output", "plan", "match_plan" })
                {
                    if (root.TryGetProperty(wrap, out var inner) && inner.ValueKind == JsonValueKind.Object)
                    {
                        root = inner;
                        break;
                    }
                }
            }
            if (root.TryGetProperty("matches", out var ms) && ms.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in ms.EnumerateArray())
                {
                    var bid = TryGuid(m, "bankTxnId");
                    if (bid == null) continue;
                    var type = m.TryGetProperty("matchType", out var tEl) ? tEl.GetString() ?? "OneToOne" : "OneToOne";
                    var conf = m.TryGetProperty("confidence", out var cEl) && cEl.TryGetDecimal(out var c) ? c : 0m;
                    var reasoning = m.TryGetProperty("reasoning", out var rEl) ? rEl.GetString() : null;
                    var cands = new List<MatchCandidate>();
                    if (m.TryGetProperty("candidates", out var cs) && cs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ce in cs.EnumerateArray())
                        {
                            var cid = TryGuid(ce, "candidateId");
                            if (cid == null) continue;
                            var ctype = ce.TryGetProperty("candidateType", out var ctEl) ? ctEl.GetString() ?? "Document" : "Document";
                            var amt = ce.TryGetProperty("amount", out var aEl) && aEl.TryGetDecimal(out var a) ? a : 0m;
                            // Hard rule H2 (prompt): candidates must be positive.
                            // Negative emissions = invalid signal (the −500 + 2,500
                            // bug). Convert to magnitude and let the downstream
                            // sum-vs-bank validator catch the wrong total.
                            if (amt < 0) amt = Math.Abs(amt);
                            cands.Add(new MatchCandidate(cid.Value, ctype, amt));
                        }
                    }
                    if (cands.Count > 0)
                        matches.Add(new ProposedMatch(bid.Value, type, cands, conf, reasoning, Guid.Empty));
                }
            }
            if (root.TryGetProperty("unmatched", out var us) && us.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in us.EnumerateArray())
                {
                    var bid = TryGuid(u, "bankTxnId");
                    if (bid == null) continue;
                    var reason = u.TryGetProperty("reason", out var rEl) ? rEl.GetString() ?? "" : "";
                    var action = u.TryGetProperty("suggestedAction", out var aEl) ? aEl.GetString() : null;
                    unmatched.Add(new UnmatchedTxn(bid.Value, reason, action));
                }
            }
            if (root.TryGetProperty("missing_data", out var md) && md.ValueKind == JsonValueKind.Array)
            {
                foreach (var dEl in md.EnumerateArray())
                {
                    var bid = TryGuid(dEl, "bankTxnId");
                    if (bid == null) continue;
                    var mtype = dEl.TryGetProperty("missingType", out var mtEl) ? mtEl.GetString() ?? "" : "";
                    var hint = dEl.TryGetProperty("hint", out var hEl) ? hEl.GetString() ?? "" : "";
                    missing.Add(new MissingDataHint(bid.Value, mtype, hint));
                }
            }
            if (root.TryGetProperty("warnings", out var ws) && ws.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in ws.EnumerateArray())
                {
                    var s = w.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) warnings.Add(s);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Bulk AI match: JSON parse failed at {Path} (line {Line} col {Col}); raw length {Len}",
                ex.Path, ex.LineNumber, ex.BytePositionInLine, json.Length);

            // Salvage path — AI ran out of tokens and the response was
            // truncated mid-string. The complete match objects BEFORE the
            // truncation are still parseable individually. Walk the
            // "matches" array, find each complete {...} object inside,
            // try to parse each, accumulate the successful ones. This
            // recovers most of the value even when the tail is gone.
            int salvaged = TrySalvageMatches(json, matches);
            warnings.Add(salvaged > 0
                ? $"AI ตอบยาวเกินจำกัด token ระบบกู้ match ได้ {salvaged} รายการ — ส่วนที่เหลือถูกตัดทิ้ง (parse error: {ex.Message} line={ex.LineNumber} pos={ex.BytePositionInLine})"
                : $"AI JSON parse error: {ex.Message} (path={ex.Path} line={ex.LineNumber} pos={ex.BytePositionInLine}) — ลองอีกครั้ง");
        }
        return new(matches, unmatched, missing, warnings);
    }

    /// <summary>Rewrite literal control characters (newline, tab, carriage
    /// return) that sit INSIDE JSON string literals into their JSON-escape
    /// form (\n, \t, \r). Chat-model outputs sometimes embed real newlines
    /// in 'reasoning' strings, which is invalid JSON. Control chars OUTSIDE
    /// strings are left alone (JsonDocument tolerates whitespace).</summary>
    private static string StripControlCharsInsideStrings(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 32);
        bool inString = false, esc = false;
        foreach (var ch in s)
        {
            if (esc) { sb.Append(ch); esc = false; continue; }
            if (inString)
            {
                if (ch == '\\') { sb.Append(ch); esc = true; continue; }
                if (ch == '"')  { sb.Append(ch); inString = false; continue; }
                if (ch == '\n') { sb.Append("\\n"); continue; }
                if (ch == '\r') { sb.Append("\\r"); continue; }
                if (ch == '\t') { sb.Append("\\t"); continue; }
                if (ch < 0x20)  { sb.Append("\\u").Append(((int)ch).ToString("X4")); continue; }
                sb.Append(ch);
                continue;
            }
            if (ch == '"') { sb.Append(ch); inString = true; continue; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Recover complete match objects from a TRUNCATED bulk response.
    /// When DeepSeek hits the output-token ceiling mid-string, the overall
    /// JSON is unparseable, but every match object emitted BEFORE the cut is
    /// itself a well-formed {...} block. Walk the text after the "matches": [
    /// marker and try parsing each balanced {...} block individually; append
    /// the parseable ones to <paramref name="matches"/>. Returns the count
    /// recovered.</summary>
    private static int TrySalvageMatches(string json, List<ProposedMatch> matches)
    {
        var marker = json.IndexOf("\"matches\"", StringComparison.Ordinal);
        if (marker < 0) return 0;
        var arrayStart = json.IndexOf('[', marker);
        if (arrayStart < 0) return 0;

        int recovered = 0;
        int i = arrayStart + 1;
        var opts = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        while (i < json.Length)
        {
            // Skip whitespace + commas between elements.
            while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
            if (i >= json.Length || json[i] == ']') break;
            if (json[i] != '{') { i++; continue; }

            // Walk to the matching '}' with string-aware brace counting.
            int depth = 0, end = -1;
            bool inString = false, esc = false;
            for (int k = i; k < json.Length; k++)
            {
                var ch = json[k];
                if (esc) { esc = false; continue; }
                if (inString)
                {
                    if (ch == '\\') esc = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; continue; }
                if (ch == '{') depth++;
                else if (ch == '}') { depth--; if (depth == 0) { end = k; break; } }
            }
            if (end < 0) break;   // ran off the end → truncation point reached.

            var block = json[i..(end + 1)];
            try
            {
                using var doc = JsonDocument.Parse(StripControlCharsInsideStrings(block), opts);
                var m = doc.RootElement;
                var bid = TryGuid(m, "bankTxnId");
                if (bid != null)
                {
                    var type = m.TryGetProperty("matchType", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() ?? "OneToOne" : "OneToOne";
                    var conf = m.TryGetProperty("confidence", out var cEl) && cEl.TryGetDecimal(out var c) ? c : 0m;
                    var reasoning = m.TryGetProperty("reasoning", out var rEl) && rEl.ValueKind == JsonValueKind.String ? rEl.GetString() : null;
                    var cands = new List<MatchCandidate>();
                    if (m.TryGetProperty("candidates", out var cs) && cs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ce in cs.EnumerateArray())
                        {
                            var cid = TryGuid(ce, "candidateId");
                            if (cid == null) continue;
                            var ctype = ce.TryGetProperty("candidateType", out var ctEl) && ctEl.ValueKind == JsonValueKind.String ? ctEl.GetString() ?? "Document" : "Document";
                            var amt = ce.TryGetProperty("amount", out var aEl) && aEl.TryGetDecimal(out var a) ? a : 0m;
                            if (amt < 0) amt = Math.Abs(amt);   // hard rule H2
                            cands.Add(new MatchCandidate(cid.Value, ctype, amt));
                        }
                    }
                    if (cands.Count > 0)
                    {
                        matches.Add(new ProposedMatch(bid.Value, type, cands, conf, reasoning, Guid.Empty));
                        recovered++;
                    }
                }
            }
            catch { /* skip this block, try the next */ }
            i = end + 1;
        }
        return recovered;
    }

    /// <summary>Server-side EXACT 1:1 sweep that runs BEFORE the multi-item
    /// combination search. For every unmatched bank txn it looks for ONE still-
    /// unconsumed Payment or JE with same direction + same amount (±0.50) +
    /// |date diff| ≤ 7 days. A contact / reference signal boosts confidence but
    /// is not required when the amount + direction + date triangle is tight.
    /// This guarantees that legitimate 1:1 matches that the AI happened to skip
    /// are picked up FIRST — they always beat any M:1 combination using the
    /// same JE as one of multiple items.</summary>
    private static ParsedResponse TryExactOneToOne(
        ParsedResponse parsed,
        IReadOnlyList<BulkBankMatchPrompt.BankTxnInput> bankTxns,
        IReadOnlyList<BulkBankMatchPrompt.OpenPaymentInput> payments,
        IReadOnlyList<BulkBankMatchPrompt.OpenJeInput> jes)
    {
        const decimal Tol = 0.50m;
        var matchedBankIds = parsed.Matches.Select(m => m.BankTxnId).ToHashSet();
        var consumed = parsed.Matches.SelectMany(m => m.Candidates)
            .Select(c => c.CandidateId).ToHashSet();

        // Index payments + JEs by direction so the per-txn scan is O(window).
        // TaxId + bank-account suffix travel with each candidate so Consider()
        // can use them as extra identity signals (a memo citing the payer's
        // account tail "X3349" or tax id is a near-certain customer match).
        var payIn = new List<(Guid Id, DateTime Date, decimal Amt, string? Contact, string? Ref, string? DocNo, string? TaxId, string? Acct)>();
        var payOut = new List<(Guid Id, DateTime Date, decimal Amt, string? Contact, string? Ref, string? DocNo, string? TaxId, string? Acct)>();
        foreach (var p in payments)
        {
            if (!Guid.TryParse(p.Id, out var pid) || consumed.Contains(pid)) continue;
            var tup = (pid, p.Date, p.Amount, p.ContactName, p.Reference, p.LinkedDocumentNumber, p.ContactTaxId, p.BankAccountNumber);
            if (p.Direction == "Out") payOut.Add(tup);
            else if (p.Direction == "In") payIn.Add(tup);
        }
        var jeIn = new List<(Guid Id, DateTime Date, decimal Amt, decimal? Gross, string? Contact, string? Ref, string? DocNo, string? TaxId, string? Acct)>();
        var jeOut = new List<(Guid Id, DateTime Date, decimal Amt, decimal? Gross, string? Contact, string? Ref, string? DocNo, string? TaxId, string? Acct)>();
        foreach (var j in jes)
        {
            if (!Guid.TryParse(j.Id, out var jid) || consumed.Contains(jid)) continue;
            var tup = (jid, j.Date, j.NetAmount, j.GrossAmount, j.ContactName, j.Reference, j.SourceDocNumber, j.ContactTaxId, (string?)null);
            if (j.Direction == "Out") jeOut.Add(tup);
            else if (j.Direction == "In") jeIn.Add(tup);
        }

        var added = new List<ProposedMatch>();
        var clearedBankIds = new HashSet<Guid>();

        foreach (var bt in bankTxns)
        {
            if (!Guid.TryParse(bt.Id, out var bid)) continue;
            if (matchedBankIds.Contains(bid)) continue;
            var target = Math.Abs(bt.Amount);
            var dir = bt.Direction;

            // Directional date window — receipts/RVs are dated AT or BEFORE the
            // bank settlement, so the window looks mostly backward (a KSHOP
            // deposit on 2 Apr matches RVs from 1 Apr-late + 2 Apr, never 3 Apr).
            var win = CandidateWindow(bt.Memo, bt.Payee, bt.Reference);
            var memoBlob = ((bt.Memo ?? "") + " " + (bt.Reference ?? "") + " " + (bt.Payee ?? ""));
            var memoLc = memoBlob.ToLowerInvariant();
            // Document/reference codes the bank memo cites (REC.../PAY.../INV...
            // /BILL...). A candidate whose ref/doc matches one of these is a
            // near-certain 1:1 regardless of name.
            var memoRefs = ExtractRefCodes(memoBlob);

            // Score by (signal desc, amount delta asc, date gap asc). Also
            // count how many in-window candidates share the exact amount —
            // when >1 and NONE carries an identity signal, the pick is
            // ambiguous and confidence must drop (Gap 1.4: two same-amount
            // same-day receipts from different customers).
            (Guid Id, string Kind, decimal Amt, int DateGap, int Signal)? best = null;
            int exactAmountInWindow = 0;
            void Consider(Guid id, string kind, decimal amt, DateTime date, string? contact, string? rf, string? docNo, string? taxId = null, string? acct = null)
            {
                if (Math.Abs(amt - target) > Tol) return;
                if (!InWindow(date, bt.Date, win)) return;
                var gap = (int)Math.Abs((date - bt.Date).TotalDays);   // magnitude for scoring
                if (clearedBankIds.Contains(bid)) return;       // already taken in this loop
                exactAmountInWindow++;
                int signal = 0;
                // Strong: the candidate's own reference/doc code is cited in memo
                // (memoRefs is already a case-insensitive set).
                if (!string.IsNullOrWhiteSpace(rf) && memoRefs.Contains(rf!.Replace(" ", ""))) signal += 4;
                if (!string.IsNullOrWhiteSpace(docNo) && memoRefs.Contains(docNo!.Replace(" ", ""))) signal += 4;
                // Medium: substring of the code appears anywhere in the memo.
                if (signal == 0 && !string.IsNullOrWhiteSpace(docNo) && memoLc.Contains(docNo!.ToLowerInvariant())) signal += 3;
                if (signal == 0 && !string.IsNullOrWhiteSpace(rf) && memoLc.Contains(rf!.ToLowerInvariant())) signal += 1;
                // Contact identity: exact substring or fuzzy token overlap with
                // the masked name in the memo ("จาก X3349 VEENA SRIKA++").
                if (!string.IsNullOrWhiteSpace(contact) && ContactMatchesMemo(contact!, memoLc)) signal += 2;
                // Tax id quoted in the memo (13-digit Thai TIN) — unambiguous.
                if (!string.IsNullOrWhiteSpace(taxId))
                {
                    var tid = new string(taxId!.Where(char.IsDigit).ToArray());
                    if (tid.Length >= 10 && memoBlob.Replace(" ", "").Replace("-", "").Contains(tid)) signal += 4;
                }
                // Payer/payee bank-account tail in the memo ("จาก KTB X3349").
                if (!string.IsNullOrWhiteSpace(acct))
                {
                    var digits = new string(acct!.Where(char.IsDigit).ToArray());
                    if (digits.Length >= 3)
                    {
                        var tail = digits.Substring(digits.Length - Math.Min(4, digits.Length));
                        if (memoLc.Replace(" ", "").Contains(tail.ToLowerInvariant())) signal += 3;
                    }
                }
                if (best is null
                    || signal > best.Value.Signal
                    || (signal == best.Value.Signal && Math.Abs(amt - target) < Math.Abs(best.Value.Amt - target))
                    || (signal == best.Value.Signal && amt == best.Value.Amt && gap < best.Value.DateGap))
                    best = (id, kind, amt, gap, signal);
            }

            foreach (var p in (dir == "Out" ? payOut : payIn))
                Consider(p.Id, "Payment", p.Amt, p.Date, p.Contact, p.Ref, p.DocNo, p.TaxId, p.Acct);
            foreach (var j in (dir == "Out" ? jeOut : jeIn))
            {
                Consider(j.Id, "JournalEntry", j.Amt, j.Date, j.Contact, j.Ref, j.DocNo, j.TaxId, j.Acct);
                if (j.Gross.HasValue) Consider(j.Id, "JournalEntry", j.Gross.Value, j.Date, j.Contact, j.Ref, j.DocNo, j.TaxId, j.Acct);
            }
            if (best is null) continue;

            // AMBIGUITY GUARD: several candidates share the exact amount but the
            // chosen one has NO identity signal → we can't be sure it's the
            // right customer. Skip it (leave for AI / manual) rather than risk
            // pairing the wrong receipt.
            bool ambiguous = exactAmountInWindow > 1 && best.Value.Signal == 0;
            if (ambiguous) continue;

            // Confidence: identity signal dominates; same-day + unique amount
            // add. A signalled match (ref/doc/contact) can reach 0.95; a bare
            // amount+date unique match caps lower (0.80) since names weren't
            // confirmed.
            // After the ambiguity guard above, a no-signal match is GUARANTEED to
            // be the sole in-window candidate at this amount (exactAmountInWindow
            // == 1) — that is itself a strong 1:1 signal, so it earns the auto-
            // check floor (0.85). Any identity signal lifts it further.
            decimal conf;
            if (best.Value.Signal >= 4) conf = 0.95m;        // ref/doc/taxid cited
            else if (best.Value.Signal >= 2) conf = 0.90m;   // contact/doc substring / acct tail
            else if (best.Value.Signal >= 1) conf = 0.87m;   // weak ref substring + unique amount
            else conf = 0.85m;                               // sole candidate at this amount, no name
            if (best.Value.DateGap == 0) conf += 0.02m;
            if (conf > 0.98m) conf = 0.98m;

            // FULLY-CONFIRMED 1:1 → 0.99. When the candidate's IDENTITY is
            // confirmed (name / ref / doc-no / taxid / account-tail, signal ≥ 2)
            // AND the amount agrees to the satang AND it sits on the same or
            // adjacent day, the pairing is certain — the bank line may merely be
            // net of a small fee/WHT. This is the strongest case in the whole
            // book, so it gets the top score and (via the best-first dedup) is
            // applied before anything else.
            bool identityConfirmed = best.Value.Signal >= 2;
            bool exactToSatang = Math.Abs(best.Value.Amt - target) <= 0.01m;
            if (identityConfirmed && exactToSatang && best.Value.DateGap <= 1)
                conf = 0.99m;

            added.Add(new ProposedMatch(bid, "OneToOne",
                new List<MatchCandidate> { new(best.Value.Id, best.Value.Kind, best.Value.Amt) },
                conf,
                $"1:1 (server sweep, {dir}, ห่าง {best.Value.DateGap} วัน, signal={best.Value.Signal}{(conf >= 0.99m ? ", ยืนยันชื่อ+ยอด+วันตรง" : "")}{(exactAmountInWindow > 1 ? $", {exactAmountInWindow} ตัวยอดเท่ากัน" : "")})",
                Guid.Empty));
            clearedBankIds.Add(bid);
            consumed.Add(best.Value.Id);
        }

        if (added.Count == 0) return parsed;
        var newMatches = parsed.Matches.Concat(added).ToList();
        var newUnmatched = parsed.Unmatched.Where(u => !clearedBankIds.Contains(u.BankTxnId)).ToList();
        var newWarnings = parsed.Warnings.Concat(new[]
        {
            $"พบเพิ่ม {added.Count} รายการที่จับคู่ 1:1 ได้ตรงๆ (server-side exact sweep)"
        }).ToList();
        return parsed with { Matches = newMatches, Unmatched = newUnmatched, Warnings = newWarnings };
    }

    /// <summary>Guarantee NO double-use: each bank line and each candidate
    /// (Payment / JE / Document) ends up in at most ONE accepted match. The AI
    /// regularly proposes the same RV both as a standalone 1:1 and as part of a
    /// nearby QR-aggregator lump; without this the same receipt would be applied
    /// twice (or the second apply would fail).
    ///
    /// Resolution: process non-group matches best-first (higher confidence, then
    /// FEWER candidates so a clean 1:1 beats a lump that merely included the same
    /// doc), claiming each match's bank line + candidate ids. A later match that
    /// reuses any claimed id is demoted to 'unmatched'. M:N groups (one doc
    /// settled by several deposits) legitimately share the doc across their rows,
    /// so each group is accepted/dropped as a unit and only clashes against ids
    /// claimed OUTSIDE the group.</summary>
    private static ParsedResponse DeduplicateMatches(ParsedResponse parsed)
    {
        if (parsed.Matches.Count < 2) return parsed;

        var usedBank = new HashSet<Guid>();
        var usedCand = new HashSet<Guid>();
        var kept = new List<ProposedMatch>();
        var demoted = new List<UnmatchedTxn>();

        var nonGroup = parsed.Matches.Where(m => !m.MatchGroupId.HasValue)
            .OrderByDescending(m => m.Confidence)
            .ThenBy(m => m.Candidates.Count)
            .ToList();
        var groups = parsed.Matches.Where(m => m.MatchGroupId.HasValue)
            .GroupBy(m => m.MatchGroupId!.Value)
            .OrderByDescending(g => g.Max(m => m.Confidence))
            .ToList();

        // 1) Non-group matches.
        foreach (var m in nonGroup)
        {
            var candIds = m.Candidates.Select(c => c.CandidateId).ToList();
            if (usedBank.Contains(m.BankTxnId) || candIds.Any(usedCand.Contains))
            {
                demoted.Add(new UnmatchedTxn(m.BankTxnId,
                    "ใช้เอกสาร/รายการซ้ำกับการจับคู่อื่นที่มั่นใจกว่า",
                    "ตรวจแล้วเลือกคู่ที่ถูกต้องด้วยมือ"));
                continue;
            }
            kept.Add(m);
            usedBank.Add(m.BankTxnId);
            foreach (var id in candIds) usedCand.Add(id);
        }

        // 2) M:N groups — accept the whole group only if every bank line + the
        //    shared doc are still free.
        foreach (var g in groups)
        {
            var rows = g.ToList();
            var banks = rows.Select(r => r.BankTxnId).ToList();
            var docIds = rows.SelectMany(r => r.Candidates.Select(c => c.CandidateId)).Distinct().ToList();
            if (banks.Any(usedBank.Contains) || docIds.Any(usedCand.Contains))
            {
                foreach (var r in rows)
                    demoted.Add(new UnmatchedTxn(r.BankTxnId,
                        "กลุ่มนี้ใช้เอกสารซ้ำกับการจับคู่อื่น", "ตรวจด้วยมือ"));
                continue;
            }
            kept.AddRange(rows);
            foreach (var b in banks) usedBank.Add(b);
            foreach (var d in docIds) usedCand.Add(d);
        }

        if (demoted.Count == 0) return parsed;
        // Don't re-list a bank txn as unmatched if another KEPT match owns it.
        var keptBanks = kept.Select(m => m.BankTxnId).ToHashSet();
        var newUnmatched = parsed.Unmatched
            .Concat(demoted.Where(u => !keptBanks.Contains(u.BankTxnId)))
            .GroupBy(u => u.BankTxnId).Select(g => g.First()).ToList();
        var newWarnings = parsed.Warnings.Concat(new[]
        {
            $"ตัดการจับคู่ซ้ำออก {demoted.Count} รายการ (เอกสาร/รายการธนาคารถูกใช้ซ้ำ)"
        }).ToList();
        return parsed with { Matches = kept, Unmatched = newUnmatched, Warnings = newWarnings };
    }

    /// <summary>Greedy 1:1 pairing for groups of EQUAL-amount candidates that
    /// the ambiguity guard would otherwise leave entirely unmatched. Only fires
    /// when, for a given (direction, amount, date-bucket), the number of still-
    /// unmatched bank lines EQUALS the number of open candidates — then every
    /// candidate is consumed regardless of pairing order, so the total
    /// reconciles correctly even though per-row customer attribution is
    /// uncertain. Confidence is deliberately moderate (0.62) with a clear note,
    /// since which specific receipt maps to which deposit can't be proven.</summary>
    private static ParsedResponse TryGreedyEqualAmount(
        ParsedResponse parsed,
        IReadOnlyList<BulkBankMatchPrompt.BankTxnInput> bankTxns,
        IReadOnlyList<BulkBankMatchPrompt.OpenPaymentInput> payments,
        IReadOnlyList<BulkBankMatchPrompt.OpenJeInput> jes)
    {
        var matchedBankIds = parsed.Matches.Select(m => m.BankTxnId).ToHashSet();
        var consumed = parsed.Matches.SelectMany(m => m.Candidates).Select(c => c.CandidateId).ToHashSet();

        // Unmatched bank lines grouped by (direction, amount rounded to satang).
        var bankGroups = new Dictionary<(string Dir, decimal Amt), List<(Guid Id, DateTime Date)>>();
        foreach (var t in bankTxns)
        {
            if (!Guid.TryParse(t.Id, out var bid) || matchedBankIds.Contains(bid)) continue;
            var key = (t.Direction, Math.Round(Math.Abs(t.Amount), 2));
            (bankGroups.TryGetValue(key, out var l) ? l : bankGroups[key] = new()).Add((bid, t.Date));
        }
        if (bankGroups.Count == 0) return parsed;

        // Candidate pool indexed the same way (In = receipts/RV, Out = PV).
        var candByKey = new Dictionary<(string Dir, decimal Amt), List<(Guid Id, string Kind, DateTime Date)>>();
        void Add(string dir, decimal amt, Guid id, string kind, DateTime date)
        {
            if (consumed.Contains(id)) return;
            var key = (dir, Math.Round(Math.Abs(amt), 2));
            (candByKey.TryGetValue(key, out var l) ? l : candByKey[key] = new()).Add((id, kind, date));
        }
        foreach (var p in payments)
            if (Guid.TryParse(p.Id, out var pid) && p.Direction != null) Add(p.Direction, p.Amount, pid, "Payment", p.Date);
        foreach (var j in jes)
            if (Guid.TryParse(j.Id, out var jid) && j.Direction != null) Add(j.Direction, j.NetAmount, jid, "JournalEntry", j.Date);

        var added = new List<ProposedMatch>();
        var cleared = new HashSet<Guid>();
        foreach (var (key, bankList) in bankGroups)
        {
            if (bankList.Count < 2) continue;                       // 1 item is handled by the 1:1 sweep
            if (!candByKey.TryGetValue(key, out var cands)) continue;
            // RELAXED: fire whenever there are AT LEAST as many candidates as
            // bank lines (surplus allowed). With identical amounts the only open
            // question is which customers — confidence stays at review level
            // (0.62, below the auto-check gate) so a human still confirms.
            if (cands.Count < bankList.Count) continue;

            // Greedy nearest-in-window pairing: for each bank line (earliest
            // first) claim the closest still-free candidate dated within
            // [T−7..T+1]. Every bank line MUST find one, else skip the group.
            var bSorted = bankList.OrderBy(b => b.Date).ToList();
            var pool = cands.OrderBy(c => c.Date).ToList();
            var usedIdx = new HashSet<int>();
            var picks = new List<(Guid Bank, Guid Cand, string Kind)>();
            bool ok = true;
            foreach (var b in bSorted)
            {
                int bestIdx = -1; double bestGap = double.MaxValue;
                for (int ci = 0; ci < pool.Count; ci++)
                {
                    if (usedIdx.Contains(ci)) continue;
                    if (!InWindow(pool[ci].Date, b.Date, (Back: 7, Fwd: 1))) continue;
                    var g = Math.Abs((pool[ci].Date - b.Date).TotalDays);
                    if (g < bestGap) { bestGap = g; bestIdx = ci; }
                }
                if (bestIdx < 0) { ok = false; break; }
                usedIdx.Add(bestIdx);
                picks.Add((b.Id, pool[bestIdx].Id, pool[bestIdx].Kind));
            }
            if (!ok) continue;

            var surplusNote = cands.Count > bankList.Count
                ? $" (มีตัวเลือก {cands.Count} ใบ มากกว่าจำนวนรายการ — ตรวจว่าตรงลูกค้า)"
                : " (โปรดตรวจว่าตรงลูกค้า)";
            foreach (var pick in picks)
            {
                added.Add(new ProposedMatch(pick.Bank, "OneToOne",
                    new List<MatchCandidate> { new(pick.Cand, pick.Kind, key.Amt) },
                    0.62m,
                    $"จับคู่อัตโนมัติแบบกลุ่ม: มี {bankList.Count} รายการยอด {key.Amt:N2} เท่ากัน{surplusNote}",
                    Guid.Empty));
                cleared.Add(pick.Bank);
                consumed.Add(pick.Cand);
            }
        }

        if (added.Count == 0) return parsed;
        return parsed with
        {
            Matches = parsed.Matches.Concat(added).ToList(),
            Unmatched = parsed.Unmatched.Where(u => !cleared.Contains(u.BankTxnId)).ToList(),
            Warnings = parsed.Warnings.Concat(new[] { $"จับคู่กลุ่มยอดเท่ากัน {added.Count} รายการ (ความเชื่อมั่นปานกลาง — ตรวจลูกค้าก่อนยืนยัน)" }).ToList(),
        };
    }

    /// <summary>Many-banks-to-one: several still-unmatched bank lines that SUM
    /// to a single open Payment / JE (one sale settled by 2-3 separate
    /// transfers on different days). Each participating bank line is emitted as
    /// a ManyBanksToOneDoc match pointing at the same candidate and sharing a
    /// MatchGroupId, so the UI applies the whole group through the M:N
    /// ReconciliationGroup path (which allocates each deposit partially against
    /// the one document) instead of BatchReconcile.</summary>
    private static ParsedResponse TryManyBanksToOne(
        ParsedResponse parsed,
        IReadOnlyList<BulkBankMatchPrompt.BankTxnInput> bankTxns,
        IReadOnlyList<BulkBankMatchPrompt.OpenPaymentInput> payments,
        IReadOnlyList<BulkBankMatchPrompt.OpenJeInput> jes)
    {
        const decimal Tol = 0.01m;
        var matchedBankIds = parsed.Matches.Select(m => m.BankTxnId).ToHashSet();
        var consumed = parsed.Matches.SelectMany(m => m.Candidates).Select(c => c.CandidateId).ToHashSet();

        var unmatched = new List<(Guid Id, DateTime Date, decimal Amt, string Dir)>();
        foreach (var t in bankTxns)
        {
            if (!Guid.TryParse(t.Id, out var bid) || matchedBankIds.Contains(bid)) continue;
            unmatched.Add((bid, t.Date, Math.Round(Math.Abs(t.Amount), 2), t.Direction));
        }
        if (unmatched.Count < 2) return parsed;

        var cands = new List<(Guid Id, string Kind, DateTime Date, decimal Amt, string Dir)>();
        foreach (var p in payments)
            if (Guid.TryParse(p.Id, out var pid) && !consumed.Contains(pid) && p.Direction != null)
                cands.Add((pid, "Payment", p.Date, Math.Round(p.Amount, 2), p.Direction!));
        foreach (var j in jes)
            if (Guid.TryParse(j.Id, out var jid) && !consumed.Contains(jid) && j.Direction != null)
                cands.Add((jid, "JournalEntry", j.Date, Math.Round(Math.Abs(j.NetAmount), 2), j.Direction!));

        var added = new List<ProposedMatch>();
        var cleared = new HashSet<Guid>();
        var usedBank = new HashSet<Guid>();

        // Largest documents first (most likely to be the split-settled ones).
        foreach (var c in cands.OrderByDescending(x => x.Amt))
        {
            // Same-direction unmatched lines each SMALLER than the document,
            // within ±45 days (split settlements straddle the doc date).
            var pool = unmatched
                .Where(b => b.Dir == c.Dir && !usedBank.Contains(b.Id)
                    && b.Amt < c.Amt - Tol
                    && Math.Abs((b.Date.Date - c.Date.Date).TotalDays) <= 45)
                .OrderByDescending(b => b.Amt).ToList();
            if (pool.Count < 2) continue;

            // Exact 2- or 3-subset summing to the document amount.
            List<(Guid Id, DateTime Date, decimal Amt, string Dir)>? hit = null;
            for (int i = 0; i < pool.Count && hit == null; i++)
            for (int j = i + 1; j < pool.Count && hit == null; j++)
            {
                if (Math.Abs(pool[i].Amt + pool[j].Amt - c.Amt) <= Tol)
                    hit = new() { pool[i], pool[j] };
                else
                    for (int k = j + 1; k < pool.Count; k++)
                        if (Math.Abs(pool[i].Amt + pool[j].Amt + pool[k].Amt - c.Amt) <= Tol)
                        { hit = new() { pool[i], pool[j], pool[k] }; break; }
            }
            if (hit == null) continue;

            var groupId = Guid.NewGuid();
            foreach (var b in hit)
            {
                added.Add(new ProposedMatch(b.Id, "ManyBanksToOneDoc",
                    new List<MatchCandidate> { new(c.Id, c.Kind, b.Amt) },
                    0.72m,
                    $"หลายโอนรวมเป็นเอกสารเดียว: {hit.Count} รายการ รวม {hit.Sum(x => x.Amt):N2} = {c.Amt:N2} (กระทบยอดแบบกลุ่ม M:N)",
                    Guid.Empty, groupId));
                cleared.Add(b.Id);
                usedBank.Add(b.Id);
            }
            consumed.Add(c.Id);
        }

        if (added.Count == 0) return parsed;
        return parsed with
        {
            Matches = parsed.Matches.Concat(added).ToList(),
            Unmatched = parsed.Unmatched.Where(u => !cleared.Contains(u.BankTxnId)).ToList(),
            Warnings = parsed.Warnings.Concat(new[] { $"พบ {added.Count} รายการธนาคารที่รวมกันเป็นเอกสารเดียว (กระทบยอดแบบกลุ่ม)" }).ToList(),
        };
    }

    /// <summary>Post-AI sweep that promotes lumped-deposit unmatched lines
    /// into M:1 matches. ONLY runs after the 1:1 sweep, so a clean 1:1
    /// always beats any combination using one of its JEs as a lumped part.
    /// For each still-unmatched bank txn it searches the pool of still-
    /// unmatched JEs within ±7 days for a 2-4 JE subset whose net (same-side
    /// add, opposite-side subtract) equals the bank amount within ±0.50 baht.
    /// Same-side combinations are tried before any cross-side fallback per
    /// FindBestCombination. Pure server-side — no extra AI call.</summary>
    private static ParsedResponse TryCombineLumpedJes(
        ParsedResponse parsed,
        IReadOnlyList<BulkBankMatchPrompt.BankTxnInput> bankTxns,
        IReadOnlyList<BulkBankMatchPrompt.OpenJeInput> jes)
    {
        // Bank ids AI already matched + JE ids it already consumed.
        var matchedBankIds = parsed.Matches.Select(m => m.BankTxnId).ToHashSet();
        var consumedJeIds = parsed.Matches.SelectMany(m => m.Candidates)
            .Where(c => c.CandidateType.Equals("JournalEntry", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.CandidateId).ToHashSet();

        // Resolve the unmatched bank txns + remaining JE pool to Guid+decimal.
        var unmatchedBank = new List<(Guid Id, DateTime Date, decimal Amount, string? Memo, string Direction)>();
        foreach (var t in bankTxns)
        {
            if (!Guid.TryParse(t.Id, out var bid)) continue;
            if (matchedBankIds.Contains(bid)) continue;
            unmatchedBank.Add((bid, t.Date, Math.Abs(t.Amount), t.Memo, t.Direction));
        }
        // Split the JE pool by direction so the search per bank txn only
        // considers JEs that ACTUALLY hit the bank account on the matching
        // side. This is what blocks the "2,000 = −500 + 2,500 with two RVs"
        // bug at the source: an In bank txn can never see Out JEs at all.
        var poolIn = new List<(Guid Id, DateTime Date, decimal Amount, string Number)>();
        var poolOut = new List<(Guid Id, DateTime Date, decimal Amount, string Number)>();
        foreach (var j in jes)
        {
            if (!Guid.TryParse(j.Id, out var jid)) continue;
            if (consumedJeIds.Contains(jid)) continue;
            var item = (jid, j.Date, Math.Abs(j.NetAmount), j.Number);
            if (j.Direction == "In") poolIn.Add(item);
            else if (j.Direction == "Out") poolOut.Add(item);
            // JEs with no known direction are excluded from server-side combo
            // search (we'd be guessing which side they belong to).
        }
        if (unmatchedBank.Count == 0 || (poolIn.Count + poolOut.Count) < 2)
            return parsed;

        var added = new List<ProposedMatch>();
        var clearedBankIds = new HashSet<Guid>();

        foreach (var bt in unmatchedBank)
        {
            // Date-window pre-filter — typical lumped deposits land within a
            // week of the underlying receipts. Reduces n from ~200 to ~30 for
            // the size-3 / size-4 inner loops.
            // Per-category date window + M:1 eligibility. Aggregator/CardSettle
            // are the ONLY flows where lumping many items into one bank line
            // makes business sense; person-to-person Transfer / AutoCredit /
            // Cheque etc. are 1:1 by nature, so the M:1 combo search must skip
            // them and let the 1:1 sweep handle them.
            var cat = ClassifyBankMemo(bt.Memo);
            if (!IsAggregatorFlow(cat)) continue;   // 1:1-only flow — let TryExactOneToOne handle it
            var win = CandidateWindow(bt.Memo);
            bool W(DateTime d) => InWindow(d, bt.Date, win);
            // Amount pre-filter: no single same-side item can exceed the target
            // (all-positive sum), so drop them before the O(n^4) subset search.
            // Opposite-side (subtract) items can be any size. Cuts the same-side
            // pool dramatically on a busy day (Gap 1.5 perf).
            var same = (bt.Direction == "Out" ? poolOut : poolIn)
                .Where(p => W(p.Date) && !consumedJeIds.Contains(p.Id) && p.Amount <= bt.Amount + 0.50m).ToList();
            var opp  = (bt.Direction == "Out" ? poolIn  : poolOut)
                .Where(p => W(p.Date) && !consumedJeIds.Contains(p.Id)).ToList();
            if (same.Count + opp.Count < 2) continue;

            var best = FindBestCombination(bt.Amount, same, opp);
            if (best == null) continue;

            // Store ABS amount on each candidate; the renderer shows the sign
            // by comparing the candidate's direction to the bank line direction.
            var cands = best.Items
                .Select(i => new MatchCandidate(i.Je.Id, "JournalEntry", i.Je.Amount))
                .ToList();
            var parts = string.Join(" ", best.Items
                .Select((i, idx) =>
                {
                    var sign = i.Sign >= 0 ? (idx == 0 ? "" : "+ ") : "− ";
                    return $"{sign}{i.Je.Number}({i.Je.Amount:N2})";
                }));
            var reason = $"รวม {best.Items.Count} JE ({bt.Direction}): {parts} = {best.Items.Sum(i => i.Sign * i.Je.Amount):N2}";
            // Confidence starts lower than AI's 1:1 picks (bigger guess space)
            // and decays with subset size; calibrator runs next and may lower
            // further if delta is non-zero.
            var baseConf = best.Items.Count switch { 2 => 0.80m, 3 => 0.70m, _ => 0.60m };
            added.Add(new ProposedMatch(bt.Id, "OneBankToManyDocs", cands, baseConf, reason, Guid.Empty));
            clearedBankIds.Add(bt.Id);
            // Mark these JEs as taken so the next bank txn can't claim them.
            foreach (var item in best.Items) consumedJeIds.Add(item.Je.Id);
        }

        if (added.Count == 0) return parsed;

        var newMatches = parsed.Matches.Concat(added).ToList();
        var newUnmatched = parsed.Unmatched.Where(u => !clearedBankIds.Contains(u.BankTxnId)).ToList();
        var newWarnings = parsed.Warnings.Concat(new[]
        {
            $"พบเพิ่ม {added.Count} รายการที่เป็นการรวม JE หลายตัวเป็นยอดเดียว (server-side combination search)"
        }).ToList();
        return parsed with { Matches = newMatches, Unmatched = newUnmatched, Warnings = newWarnings };
    }

    private sealed record JeCombo(IReadOnlyList<(JeCand Je, int Sign)> Items, decimal Delta);
    private sealed record JeCand(Guid Id, DateTime Date, decimal Amount, string Number);

    /// <summary>Subset-sum search for the best (smallest delta, smallest size)
    /// JE combination matching the bank target ±0.50. Strategy: try ALL same-
    /// direction combinations first (sizes 2 → 3 → 4); only if NOTHING is found
    /// fall back to cross-direction net-settlement (Receipt − PaymentVoucher),
    /// since net-settlement is a rare real-world pattern that should never be
    /// preferred over a clean same-side match. Same-side subtraction (RV − RV)
    /// is structurally impossible because the pools are pre-split by direction.</summary>
    private static JeCombo? FindBestCombination(decimal target,
        List<(Guid Id, DateTime Date, decimal Amount, string Number)> sameDirPool,
        List<(Guid Id, DateTime Date, decimal Amount, string Number)> oppDirPool)
    {
        const decimal Tol = 0.50m;
        var sm = sameDirPool.Select(x => new JeCand(x.Id, x.Date, x.Amount, x.Number)).ToList();
        var op = oppDirPool.Select(x => new JeCand(x.Id, x.Date, x.Amount, x.Number)).ToList();
        JeCombo? best = null;

        bool Improves(JeCombo? cur, decimal delta, int size)
            => cur == null || delta < cur.Delta || (delta == cur.Delta && size < cur.Items.Count);

        // ── Pass 1: SAME-SIDE ONLY (the normal case) ────────────────────
        // Size 2.
        for (int i = 0; i < sm.Count; i++)
        for (int j = i + 1; j < sm.Count; j++)
        {
            var s = sm[i].Amount + sm[j].Amount;
            var d = Math.Abs(s - target);
            if (d <= Tol && Improves(best, d, 2))
                best = new JeCombo(new (JeCand, int)[] { (sm[i], +1), (sm[j], +1) }, d);
        }
        if (best is { Delta: <= 0.01m }) return best;

        // Size 3.
        for (int i = 0; i < sm.Count; i++)
        for (int j = i + 1; j < sm.Count; j++)
        for (int k = j + 1; k < sm.Count; k++)
        {
            var s = sm[i].Amount + sm[j].Amount + sm[k].Amount;
            var d = Math.Abs(s - target);
            if (d <= Tol && Improves(best, d, 3))
                best = new JeCombo(new (JeCand, int)[] { (sm[i], +1), (sm[j], +1), (sm[k], +1) }, d);
        }
        if (best is { Delta: <= 0.01m }) return best;

        // Size 4.
        for (int a = 0; a < sm.Count; a++)
        for (int b = a + 1; b < sm.Count; b++)
        for (int c = b + 1; c < sm.Count; c++)
        for (int e = c + 1; e < sm.Count; e++)
        {
            var s = sm[a].Amount + sm[b].Amount + sm[c].Amount + sm[e].Amount;
            var d = Math.Abs(s - target);
            if (d <= Tol && Improves(best, d, 4))
                best = new JeCombo(new (JeCand, int)[] { (sm[a], +1), (sm[b], +1), (sm[c], +1), (sm[e], +1) }, d);
        }
        if (best is { Delta: <= 0.01m }) return best;

        // Size 5 — only for modest pools (O(n^5)); a daily KSHOP rollup can
        // bundle five receipts. Guard keeps the worst case bounded (~5M iters).
        if (sm.Count <= 30)
        {
            for (int a = 0; a < sm.Count; a++)
            for (int b = a + 1; b < sm.Count; b++)
            for (int c = b + 1; c < sm.Count; c++)
            for (int e = c + 1; e < sm.Count; e++)
            for (int f = e + 1; f < sm.Count; f++)
            {
                var s = sm[a].Amount + sm[b].Amount + sm[c].Amount + sm[e].Amount + sm[f].Amount;
                var d = Math.Abs(s - target);
                if (d <= Tol && Improves(best, d, 5))
                    best = new JeCombo(new (JeCand, int)[] { (sm[a], +1), (sm[b], +1), (sm[c], +1), (sm[e], +1), (sm[f], +1) }, d);
            }
        }
        if (best != null) return best;   // any same-side match wins over net-settlement

        // ── Pass 2: NET-SETTLEMENT FALLBACK (rare) ──────────────────────
        // Only reached when no all-same-side combination of size 2-4 fits.
        // Size 2: one same + one opposite (Receipt 2,500 − PV 500 = 2,000).
        for (int i = 0; i < sm.Count; i++)
        for (int j = 0; j < op.Count; j++)
        {
            var s = sm[i].Amount - op[j].Amount;
            if (s <= 0) continue;
            var d = Math.Abs(s - target);
            if (d <= Tol && Improves(best, d, 2))
                best = new JeCombo(new (JeCand, int)[] { (sm[i], +1), (op[j], -1) }, d);
        }
        if (best is { Delta: <= 0.01m }) return best;

        // Size 3: two same + one opposite (รับ 2 ใบ − refund 1 ใบ).
        for (int i = 0; i < sm.Count; i++)
        for (int j = i + 1; j < sm.Count; j++)
        for (int k = 0; k < op.Count; k++)
        {
            var s = sm[i].Amount + sm[j].Amount - op[k].Amount;
            if (s <= 0) continue;
            var d = Math.Abs(s - target);
            if (d <= Tol && Improves(best, d, 3))
                best = new JeCombo(new (JeCand, int)[] { (sm[i], +1), (sm[j], +1), (op[k], -1) }, d);
        }
        return best;
    }

    /// <summary>Derive an objective confidence ceiling for a proposed match
    /// from the actual numbers, instead of blindly trusting AI's stated value.
    /// The observed failure: AI returned <c>confidence: 0.95</c> on a row whose
    /// own <c>reasoning</c> admitted a 210 baht delta between bank txn and
    /// candidate. Rules:
    ///   • Amount delta within 0.50 baht                  → no penalty
    ///   • Within 1% (covers small bank fees / WHT rounding) → cap 0.85
    ///   • Within 5%                                       → cap 0.55
    ///   • Otherwise                                       → cap 0.30 + warn
    /// For M:1 matches the candidate amounts are summed before comparison.
    /// The returned confidence is min(AI confidence, computed ceiling) so AI
    /// can only lower it, never inflate it; mismatchNote describes the delta
    /// in plain Thai and is appended to the reasoning shown in the UI.</summary>
    /// <summary>Thai-bank deposit memo categories. Each carries the realistic
    /// candidate window in days + whether the deposit is a daily rollup (M:1)
    /// or a single 1:1 movement. These are derived from observed memo formats
    /// across KBank / SCB / KTB / BBL / BAY for both KShop / Internet / Mobile
    /// / SMART / ATS / Cheque / Bill Payment / Inward TT / Interest /
    /// Reversal / Loan flows. </summary>
    // Flow classification + directional windows live in the shared
    // BankFlowClassifier so the bulk sweep and AutoMatch can't drift apart.
    // Thin aliases keep the existing call sites unchanged.
    private static BankFlowCategory ClassifyBankMemo(string? memo, string? payee = null, string? reference = null)
        => BankFlowClassifier.Classify(memo, payee, reference);
    private static (int Back, int Fwd) CandidateWindow(string? memo, string? payee = null, string? reference = null)
        => BankFlowClassifier.Window(memo, payee, reference);
    private static bool InWindow(DateTime candidateDate, DateTime bankDate, (int Back, int Fwd) w)
        => BankFlowClassifier.InWindow(candidateDate, bankDate, w);
    private static bool IsAggregatorFlow(BankFlowCategory c) => BankFlowClassifier.IsAggregatorFlow(c);

    private static readonly System.Text.RegularExpressions.Regex _refCodeRx =
        new(@"\b(REC|PAY|INV|BILL|PV|RV|JV|DN|CN|TI|BN)[-\s]?\d{2,}[-\d]*\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Pull document/reference codes (REC260401001, PAY-202604-0001,
    /// INV2025..., RV-..., PV-...) out of a bank memo so a candidate whose own
    /// number/reference equals one of them is a near-certain 1:1 hit.</summary>
    private static HashSet<string> ExtractRefCodes(string? memo)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(memo)) return set;
        foreach (System.Text.RegularExpressions.Match m in _refCodeRx.Matches(memo))
            set.Add(m.Value.Replace(" ", "").Trim());
        return set;
    }

    /// <summary>Loose name match against a masked bank memo. Bank memos mask
    /// names like "จาก X3349 VEENA SRIKA++" — we match when ANY name token of
    /// length ≥ 3 (e.g. "VEENA", "SRIKA") appears in the memo, which is enough
    /// signal to break a tie but deliberately not enough to force a match on
    /// its own (the ambiguity guard still applies). Account suffix tokens like
    /// "X3349" also count when present in both.</summary>
    private static bool ContactMatchesMemo(string contact, string memoLc)
    {
        if (string.IsNullOrWhiteSpace(contact)) return false;
        var c = contact.ToLowerInvariant();
        if (memoLc.Contains(c)) return true;
        foreach (var tok in c.Split(new[] { ' ', '.', ',', '(', ')', '-' }, StringSplitOptions.RemoveEmptyEntries))
            if (tok.Length >= 3 && memoLc.Contains(tok)) return true;
        return false;
    }

    /// <summary>Candidate identity fields used to confirm a 1:1 against a bank
    /// memo (name / reference / doc-no / tax id / payer-account tail + the
    /// source-document date).</summary>
    private readonly record struct MatchIdentity(
        string? Contact, string? Reference, string? DocNo,
        string? TaxId, string? Account, DateTime Date);

    /// <summary>True when the bank memo quotes ANY of the candidate's identity
    /// fields — the same signals the 1:1 sweep scores, reused so an AI-proposed
    /// match can be upgraded to a confirmed 0.99.</summary>
    private static bool MemoConfirmsIdentity(string memoBlob, MatchIdentity info)
    {
        if (string.IsNullOrWhiteSpace(memoBlob)) return false;
        var memoLc = memoBlob.ToLowerInvariant();
        var refs = ExtractRefCodes(memoBlob);
        if (!string.IsNullOrWhiteSpace(info.Reference)
            && (refs.Contains(info.Reference!.Replace(" ", "")) || memoLc.Contains(info.Reference!.ToLowerInvariant())))
            return true;
        if (!string.IsNullOrWhiteSpace(info.DocNo)
            && (refs.Contains(info.DocNo!.Replace(" ", "")) || memoLc.Contains(info.DocNo!.ToLowerInvariant())))
            return true;
        if (!string.IsNullOrWhiteSpace(info.Contact) && ContactMatchesMemo(info.Contact!, memoLc))
            return true;
        if (!string.IsNullOrWhiteSpace(info.TaxId))
        {
            var tid = new string(info.TaxId!.Where(char.IsDigit).ToArray());
            if (tid.Length >= 10 && memoBlob.Replace(" ", "").Replace("-", "").Contains(tid)) return true;
        }
        if (!string.IsNullOrWhiteSpace(info.Account))
        {
            var digits = new string(info.Account!.Where(char.IsDigit).ToArray());
            if (digits.Length >= 3)
            {
                var tail = digits.Substring(digits.Length - Math.Min(4, digits.Length));
                if (memoLc.Replace(" ", "").Contains(tail.ToLowerInvariant())) return true;
            }
        }
        return false;
    }

    private static (decimal Confidence, string MismatchNote) CalibrateConfidence(
        Models.Entities.BankTransaction bankTxn, ProposedMatch match)
    {
        var bankAmt = Math.Abs(bankTxn.Amount);
        var sumCand = match.Candidates.Sum(c => c.Amount);
        var delta = Math.Abs(bankAmt - sumCand);
        var pct = bankAmt > 0 ? delta / bankAmt : 0m;

        decimal ceiling;
        string note;
        // Sums of 2-decimal documents are exact — a true match has delta 0.
        // The SAVE-time guard (ValidateMatchAmountAsync) rejects anything over
        // 0.01 baht, so ONLY a ≤0.01 match may be presented as ready-to-confirm
        // (high confidence). Anything 0.01-0.50 is a fee/rounding case that
        // CANNOT be saved as-is (needs a fee/diff line) → cap confidence + warn
        // so the operator isn't sent into a confirm-then-error loop.
        if (delta <= 0.01m)
        {
            ceiling = 1.00m;
            note = "";
        }
        else if (delta <= 0.50m)          // satang / tiny slip — still NOT saveable as exact
        {
            ceiling = 0.55m;
            note = $"⚠ ยอดต่าง {delta:N2} บาท — ต้องเพิ่มรายการส่วนต่าง/ค่าธรรมเนียมก่อนจึงจะบันทึกได้";
        }
        else if (pct <= 0.01m)            // ≤ 1% — aggregator fee / WHT, needs an adjustment line
        {
            ceiling = 0.45m;
            note = $"⚠ ยอดต่าง {delta:N2} บาท ({pct:P1}) — อาจเป็นค่าธรรมเนียม/หัก ณ ที่จ่าย ต้องเพิ่มรายการส่วนต่างก่อนบันทึก";
        }
        else if (pct <= 0.05m)
        {
            ceiling = 0.30m;
            note = $"⚠ ยอดต่าง {delta:N2} บาท ({pct:P1}) — ไม่ควรยืนยันโดยไม่ตรวจ";
        }
        else
        {
            ceiling = 0.15m;
            note = $"⚠ ยอดต่างมาก {delta:N2} บาท ({pct:P1}) — bank {bankAmt:N2} vs candidate {sumCand:N2} — น่าจะคนละคู่";
        }
        var calibrated = Math.Min(match.Confidence, ceiling);
        return (calibrated, note);
    }

    /// <summary>If a match's candidates sum off-by but a SUBSET of them sums
    /// to the bank amount exactly (±0.50 baht), drop the extras. Tries
    /// removing 1 then 2 items (covers the most common case: AI tossed in an
    /// extra small item). The original match is returned unchanged when no
    /// subset improves.</summary>
    private static ProposedMatch TryPruneToExactSubset(
        Models.Entities.BankTransaction bankTxn, ProposedMatch match)
    {
        if (match.Candidates.Count < 3) return match;     // nothing meaningful to prune
        var bankAmt = Math.Abs(bankTxn.Amount);
        var cs = match.Candidates;
        var total = cs.Sum(c => c.Amount);
        if (Math.Abs(total - bankAmt) <= 0.50m) return match;   // already exact

        // Try dropping 1.
        int? dropI = null;
        decimal bestDelta = Math.Abs(total - bankAmt);
        for (int i = 0; i < cs.Count; i++)
        {
            var d = Math.Abs(total - cs[i].Amount - bankAmt);
            if (d <= 0.50m && d < bestDelta) { bestDelta = d; dropI = i; }
        }
        if (dropI.HasValue)
        {
            var pruned = cs.Where((_, k) => k != dropI.Value).ToList();
            var dropped = cs[dropI.Value];
            return match with
            {
                Candidates = pruned,
                Reasoning = $"ตัด {dropped.CandidateType}:{dropped.CandidateId.ToString("N")[..8]} ({dropped.Amount:N2}) ออก — รวมที่เหลือตรงยอดธนาคารพอดี",
            };
        }

        // Try dropping 2 (small pool only to keep it cheap).
        if (cs.Count >= 4 && cs.Count <= 8)
        {
            for (int i = 0; i < cs.Count; i++)
            for (int j = i + 1; j < cs.Count; j++)
            {
                var d = Math.Abs(total - cs[i].Amount - cs[j].Amount - bankAmt);
                if (d <= 0.50m)
                {
                    var pruned = cs.Where((_, k) => k != i && k != j).ToList();
                    return match with
                    {
                        Candidates = pruned,
                        Reasoning = $"ตัด 2 รายการออก — รวมที่เหลือตรงยอดธนาคารพอดี",
                    };
                }
            }
        }
        return match;
    }

    /// <summary>Strip phrases like "รวม ... = X บาทพอดี" or "เท่ากันพอดี" from AI
    /// reasoning when the computed delta says otherwise. Keeps the AI's
    /// intent but removes the contradiction users called out.</summary>
    private static string? ScrubMisleadingSumClaim(string? reasoning)
    {
        if (string.IsNullOrWhiteSpace(reasoning)) return reasoning;
        var s = reasoning;
        // Remove "= <number> บาทพอดี" / "= <number> พอดี" segments.
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"=\s*[\d,\.]+\s*(บาท)?\s*พอดี", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"(ตรงพอดี|พอดี|equal\s+exactly)", "(ยอดไม่ตรง)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return s.Trim();
    }


    private static Guid? TryGuid(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var p)) return null;
        // GetString() throws on non-string nodes (e.g. AI returned a number
        // or null); guard so a malformed item doesn't kill the whole parse.
        if (p.ValueKind != JsonValueKind.String) return null;
        var s = p.GetString();
        return Guid.TryParse(s, out var g) ? g : null;
    }
}
