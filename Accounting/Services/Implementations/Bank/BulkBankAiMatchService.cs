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
    Guid PerMatchFeedbackId);                 // child feedback row id — UI passes back when user accepts/rejects

public sealed record MatchCandidate(
    Guid CandidateId,
    string CandidateType,                     // "Document" | "Payment" | "JournalEntry"
    decimal Amount);

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
    private async Task<Guid> SynthesisePerMatchFeedbackAsync(Guid companyId, Guid bankTxnId,
        BankTransaction txn, string answerJson, decimal confidence,
        Guid parentFeedbackId, CancellationToken ct)
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
        var record = new AiFeedbackRecord(
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
        try { return await _recorder.RecordCallAsync(record, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Per-match feedback record failed for txn {Id}", bankTxnId);
            return Guid.Empty;
        }
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
        var windowStart = fromDate.AddDays(-90);
        var windowEnd = toDate.AddDays(30);
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
                DocNumber = p.Document.DocumentNumber,
                DocType = p.Document.DocumentType,
            })
            .ToListAsync(ct);
        var truncatedPayments = payments.Count > MaxCandidatesPerKind;
        if (truncatedPayments) payments = payments.Take(MaxCandidatesPerKind).ToList();
        var openPayments = payments.Select(p => new BulkBankMatchPrompt.OpenPaymentInput(
            p.Id.ToString(), p.PaymentNumber, p.PaymentDate, p.Amount,
            p.PaymentMethod.ToString(), p.Reference, p.ContactName,
            LinkedDocumentNumber: p.DocNumber,
            LinkedDocumentType: p.DocType.ToString())).ToList();

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
        var jeRows = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && !j.IsDeleted
                        && j.Status == JournalEntryStatus.Posted
                        && j.EntryDate >= jeWindowStart && j.EntryDate <= jeWindowEnd)
            .Where(j => !matchedJeIds.Contains(j.Id))
            .OrderByDescending(j => j.EntryDate)
            .Select(j => new
            {
                j.Id, j.EntryNumber, j.EntryDate, j.Description, j.Reference,
                // Amount on the bank-linked line (signed: +debit = money in,
                // −credit = money out). Zero when the JE doesn't touch the
                // bank account or no account is linked.
                BankLineNet = j.Lines.Where(l => l.AccountId == linkedAcct)
                             .Sum(l => l.DebitAmount - l.CreditAmount),
                // Gross transaction size — total debits (= total credits).
                GrossAmount = j.Lines.Sum(l => l.DebitAmount),
            })
            .ToListAsync(ct);

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
            var amount = j.BankLineNet != 0m ? Math.Abs(j.BankLineNet) : j.GrossAmount;
            return new BulkBankMatchPrompt.OpenJeInput(
                j.Id.ToString(), j.EntryNumber, j.EntryDate, amount,
                j.Description, j.Reference);
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
        var parsed = ParseResponse(resp.RawResponseJson ?? resp.PrimaryAnswer);

        // ── 8b. Per-match child feedback rows — so when user accepts /
        // ── edits each row in the modal, BankMatchDistillationModel can
        // ── learn per-signature instead of one big undifferentiated row.
        // ── Need the BankTransaction entities again (with description /
        // ── reference / payee) to populate the single-match shape.
        var bankTxnLookup = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.BankAccountId == bankAccountId && !t.IsDeleted
                        && parsed.Matches.Select(m => m.BankTxnId).Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t, ct);
        var matchesWithFids = new List<ProposedMatch>(parsed.Matches.Count);
        foreach (var m in parsed.Matches)
        {
            if (!bankTxnLookup.TryGetValue(m.BankTxnId, out var bt))
            {
                matchesWithFids.Add(m with { PerMatchFeedbackId = Guid.Empty });
                continue;
            }
            var answerJson = JsonSerializer.Serialize(m.Candidates.Select(c => new
            {
                type = c.CandidateType, id = c.CandidateId.ToString(), amount = c.Amount,
            }));
            var perFid = await SynthesisePerMatchFeedbackAsync(
                companyId, m.BankTxnId, bt, answerJson, m.Confidence,
                resp.FeedbackId ?? Guid.Empty, ct);
            matchesWithFids.Add(m with { PerMatchFeedbackId = perFid });
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

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
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
            _logger.LogWarning(ex, "Bulk AI match: JSON parse failed; output kept as warning");
            warnings.Add("AI ตอบกลับเป็น JSON ที่ไม่ valid — กรุณา match ด้วยมือสำหรับรอบนี้");
        }
        return new(matches, unmatched, missing, warnings);
    }

    private static Guid? TryGuid(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var p)) return null;
        var s = p.GetString();
        return Guid.TryParse(s, out var g) ? g : null;
    }
}
