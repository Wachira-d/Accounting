using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// One-shot bulk reconciliation prompt — bundles a month of bank
/// statement lines + every open document + open JE + the company
/// context and asks AI to produce a complete match plan in a single
/// call. Distinct from BankMatchPrompt (the per-transaction flow) in
/// three important ways:
///
///   1. Cross-line reasoning: AI sees that bank txn #4 + bank txn
///      #11 sum to invoice INV-2025-0312 even though neither line
///      alone matches the invoice amount — impossible per-txn.
///   2. Cluster reasoning: AI sees five 850-baht txns to the same
///      vendor in one week → likely a recurring subscription that
///      should map to a single recurring-expense JE.
///   3. Missing-data reporting: when a bank txn cites an invoice
///      number that doesn't exist in the candidate list, AI returns
///      a "missing" flag so the user knows to create that invoice
///      first instead of leaving the line dangling.
///
/// Size guard: caller is responsible for capping inputs (~120 bank
/// txns + ~250 candidates fits comfortably under a 32K-token budget).
/// Beyond that, chunk by week or by amount bucket.
/// </summary>
public static class BulkBankMatchPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting reconciliation expert. You receive a full month of bank statement lines + every open payment + every open journal entry + every open document (as CONTEXT). Produce a complete match plan.

CRITICAL: candidateType must be ""Payment"" or ""JournalEntry"" — NEVER ""Document"". Open documents are CONTEXT to help you identify the right payment (e.g. memo cites invoice INV-2025-0312 → find the Payment whose linked_document.number = INV-2025-0312). If a bank txn matches a document that has NO linked payment, return it in missing_data with missingType=""Payment"" so the user knows to create the payment first.

How to match:
1. Identify EXACT 1:1 matches first (amount + date ± 7 days + same direction).
2. Then identify M:1 splits (one bank txn = sum of multiple payments/JEs — confirm sum matches).
3. Then identify 1:M aggregations (multiple bank txns = one larger payment/JE — confirm sum matches).
4. Bank memos often contain doc numbers (INV-2025-0312, PV/2025/00045) — strong signal. Cross-reference against open_payments[].linked_document and open_documents[].
5. Recurring same-vendor same-amount weekly/monthly = likely subscription / rent / utility.
6. If a memo cites a doc number that's NOT in candidates → flag as missing_data with the cited number.
7. If a bank txn has no plausible match (no candidate within 30 days + 15% amount) → unmatched_reason.

Strict JSON output (NO prose outside JSON):
{
  ""matches"": [
    {
      ""bankTxnId"": ""<guid>"",
      ""matchType"": ""OneToOne|OneBankToManyDocs|ManyBanksToOneDoc"",
      ""candidates"": [
        { ""candidateId"": ""<guid>"", ""candidateType"": ""Payment|JournalEntry"", ""amount"": <decimal> }
      ],
      ""confidence"": <0-1>,
      ""reasoning"": ""<short Thai>""
    }
  ],
  ""unmatched"": [
    { ""bankTxnId"": ""<guid>"", ""reason"": ""<short Thai>"", ""suggestedAction"": ""<short Thai>"" }
  ],
  ""missing_data"": [
    { ""bankTxnId"": ""<guid>"", ""missingType"": ""Payment|JournalEntry|Contact"", ""hint"": ""<what cited identifier was looked for>"" }
  ],
  ""warnings"": [""<cross-cutting issue>""]
}";

    public sealed record BankTxnInput(
        string Id, DateTime Date, decimal Amount, string Direction,    // ""In"" | ""Out""
        string? Memo, string? Reference, string? Payee);

    public sealed record OpenDocInput(
        string Id, string Number, string Type, DateTime Date,
        decimal Outstanding, string Direction,                          // ""AR"" | ""AP""
        string? ContactName, string? ContactTaxId);

    public sealed record OpenPaymentInput(
        string Id, string Number, DateTime Date, decimal Amount,
        string Method, string? Reference, string? ContactName,
        string? LinkedDocumentNumber = null,
        string? LinkedDocumentType = null);

    public sealed record OpenJeInput(
        string Id, string Number, DateTime Date, decimal NetAmount,
        string? Description, string? Reference);

    public sealed record CompanyContext(
        string Name, string? TaxId, string BaseCurrency,
        int FiscalYearStartMonth, bool IsVatRegistered);

    public sealed record BankAccountContext(
        string Id, string AccountName, string BankName,
        string AccountNumber, string Currency,
        decimal BankBalance, decimal? GlBalance);

    public static AiRequest Build(
        Guid companyId,
        Guid bankAccountId,
        DateTime fromDate, DateTime toDate,
        CompanyContext company,
        BankAccountContext bankAccount,
        IReadOnlyList<BankTxnInput> bankTxns,
        IReadOnlyList<OpenDocInput> openDocs,
        IReadOnlyList<OpenPaymentInput> openPayments,
        IReadOnlyList<OpenJeInput> openJes)
    {
        var payload = new
        {
            task = "bulk_bank_statement_reconciliation",
            period = new
            {
                from = fromDate.ToString("yyyy-MM-dd"),
                to = toDate.ToString("yyyy-MM-dd"),
            },
            company,
            bank_account = bankAccount,
            counts = new
            {
                bank_txns = bankTxns.Count,
                open_docs = openDocs.Count,
                open_payments = openPayments.Count,
                open_jes = openJes.Count,
            },
            // Sorted oldest-first inside each collection so AI's
            // chronological matching ("settlement on 15th covered
            // invoice from 5th") is easier to express.
            bank_txns = bankTxns.OrderBy(t => t.Date).Select(t => new
            {
                id = t.Id,
                date = t.Date.ToString("yyyy-MM-dd"),
                amount = t.Amount,
                direction = t.Direction,
                memo = t.Memo ?? "",
                memo_hints = BankMatchPrompt.ExtractMemoHints(t.Memo),
                reference = t.Reference,
                payee = t.Payee,
            }),
            open_documents = openDocs.OrderBy(d => d.Date).Select(d => new
            {
                id = d.Id, number = d.Number, type = d.Type,
                date = d.Date.ToString("yyyy-MM-dd"),
                outstanding = d.Outstanding,
                direction = d.Direction,
                contact = d.ContactName,
                contact_tax_id = d.ContactTaxId,
            }),
            open_payments = openPayments.OrderBy(p => p.Date).Select(p => new
            {
                id = p.Id, number = p.Number,
                date = p.Date.ToString("yyyy-MM-dd"),
                amount = p.Amount,
                method = p.Method,
                reference = p.Reference,
                contact = p.ContactName,
                // The linked-document fields are the bridge: when a bank
                // memo cites an invoice number, AI can hunt for it here
                // and return the wrapping Payment.
                linked_document = p.LinkedDocumentNumber == null ? null : new
                {
                    number = p.LinkedDocumentNumber,
                    type = p.LinkedDocumentType,
                },
            }),
            open_journal_entries = openJes.OrderBy(j => j.Date).Select(j => new
            {
                id = j.Id, number = j.Number,
                date = j.Date.ToString("yyyy-MM-dd"),
                amount = j.NetAmount,
                description = j.Description,
                reference = j.Reference,
            }),
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.BulkBankStatementMatch,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            // The bulk call doesn't have a single LocalPrimaryAnswer —
            // it's a plan, not one answer. Caller post-processes the
            // JSON; orchestrator's fallback path returns empty match
            // list when AI is down, which UI shows as "AI unavailable —
            // please match manually".
            LocalPrimaryAnswer = null,
            LocalConfidence = null,
            LocalModelVersion = "BulkHeuristic-v1",
            SourceEntityType = "BankAccount",
            SourceEntityId = bankAccountId,
            CacheTtlOverrideDays = 0,        // never cache — state changes daily
            BypassCache = true,
            MaxTokensOverride = 4000,         // bulk response can be long
        };
    }
}
