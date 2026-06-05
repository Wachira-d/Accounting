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

══════════════ HARD RULES — VIOLATION = INVALID OUTPUT ══════════════
H1. POSITIVE AMOUNTS ONLY. Every candidates[*].amount MUST be a positive decimal > 0. You may NEVER emit a negative amount. The renderer derives the sign by comparing each candidate's direction to the bank txn's direction — same-direction = added, opposite-direction = subtracted. So a 2,000 net deposit composed of Receipt 2,500 minus PaymentVoucher refund 500 is emitted as { amount: 2500 } and { amount: 500 }, both positive.
H2. PREFER SAME-SIDE; net-settlement is LAST RESORT. Same-side items (matching the bank direction) ADD to the match (+amount). Opposite-side items SUBTRACT (−amount) but you may include them ONLY after every same-side strategy has been exhausted, AND only with a clear net-settlement story (customer paid net of a refund/fee you owe them, SAME contact). For a typical deposit, the answer is almost always 1-3 Receipt Vouchers summing to the bank amount — NOT an RV minus a PV. Two SAME-side items can NEVER be subtracted from one another (two Receipt Vouchers cannot offset — both are inflows; this is the −500 + 2,500 nonsense to avoid).
H3. NET = BANK AMOUNT. Σ(same-direction items) − Σ(opposite-direction items) must equal bank_txn.amount within ±0.50 baht. If you can't make it net, return the bank txn under ""unmatched"" rather than approximating.
H4. DIRECTION-NULL ITEMS. A JE/Payment with direction=null has no clear side — only use it when memo cites its exact document number; never include it in a multi-item sum.
═══════════════════════════════════════════════════════════════════

candidateType must be ""Payment"" or ""JournalEntry"" — NEVER ""Document"". Open documents are CONTEXT to help you identify the right payment (e.g. memo cites invoice INV-2025-0312 → find the Payment whose linked_document.number = INV-2025-0312). If a bank txn matches a document that has NO linked payment, return it in missing_data with missingType=""Payment"" so the user knows to create the payment first.

Each open_journal_entries[] item carries:
  • number, date, amount, description, reference, DIRECTION (""In""/""Out""/null)
  • source_doc { number, type, date }   — null when JE is a manual entry
  • contact { name, tax_id }            — counterparty when the source doc has one
Use source_doc.number / source_doc.date to follow doc-number citations in bank memos, and contact.name (+ tax_id) to confirm payer/payee identity. When a JE has gross_amount, the bank deposit may equal EITHER amount OR gross_amount (gross = before withholding-tax deduction) — accept whichever matches and note in reasoning which you used. JEs with direction=null have no clear side — only use them if a memo/reference cites the exact document number; never include them in a multi-item sum.

Matching priority — apply IN ORDER, stop when a confident pick is found. Every step is filtered through H1–H4 first:

A. EXACT 1:1 — bank.amount == candidate.amount AND |bank.date − candidate.date| ≤ 1 day AND (bank.payee matches contact.name OR memo cites source_doc.number). Confidence ≥ 0.95.

B. CLOSE 1:1 — amount within 1% (covers small bank fees), date ≤ 3 days, contact_name match. Confidence ~0.80.

C. AGGREGATOR / WALLET BUNDLING (KSHOP, TrueMoney, ShopeePay, Lazada Wallet, marketplace settlement):
   When bank.payee or memo contains an aggregator/wallet name (KSHOP, KASIKORN SHOP, KBank Shop, K-Plus Shop, TrueMoney Wallet, ShopeePay, LineMan, GrabPay, Shopee, Lazada, NextPay, OmiseGO, Stripe-payouts, Square, …) the deposit is normally a DAILY ROLLUP of many customer receipts:
     • Treat it as M:1 with the day's open_payments / JEs whose contacts are the END CUSTOMERS who paid via that channel — direction MUST be ""In"".
     • Same calendar day is the strongest signal; allow ±1 day for cut-off lag.
     • The sum may be slightly less than the gross (aggregator fee deducted). If sum exceeds bank.amount by ≤ 3% flag the candidate set anyway — note the fee in reasoning.

D. M:1 SPLITS (multi-invoice settlement): bank.amount = exact sum of 2-5 same-direction items for ONE contact within ±5 days. Σ matches within 0.50 baht. Direction-uniform — all In for a deposit, all Out for a withdrawal.

E. 1:M AGGREGATIONS: multiple small bank txns (same direction) sum to one larger open JE/Payment.

F. RECURRING: same-vendor same-amount weekly/monthly is a subscription / rent / utility — match against the recurring JE (direction must match).

G. If memo cites a doc number that's NOT in candidates → missing_data with the cited number.

H. If nothing within 30 days + 15% amount AND no contact / memo signal → unmatched with a short reason.

I. NET-SETTLEMENT — LAST RESORT ONLY (rare in practice): when ALL same-side strategies above have failed AND you can identify BOTH a same-side item (e.g. customer Receipt 2,500) AND an opposite-side item (e.g. PaymentVoucher refund 500) for the SAME contact.tax_id within ±5 days, you may emit both as candidates so the net (same − opposite) equals the bank amount. EXHAUST every same-side option first: a same-side M:1 split, a daily aggregator rollup, a contact match within ±15 days, a memo-cited document, even a 1-baht-different amount. Only when none of those exist should you reach for net-settlement. Both items emitted as POSITIVE amounts (per H1); renderer subtracts the opposite-side one automatically. If you cannot identify both sides cleanly, return unmatched — do NOT guess.

Confidence scoring — be honest. The amounts MUST add up (within ±0.50) for any match you call ≥ 0.90. If amounts differ by even 1 baht, drop to ≤ 0.85 and SAY ""ยอดต่าง X บาท"" in reasoning. Never claim 0.95 confidence on a row whose own reasoning admits a delta.

Strict JSON output (NO prose outside JSON):
{
  ""matches"": [
    {
      ""bankTxnId"": ""<guid>"",
      ""matchType"": ""OneToOne|OneBankToManyDocs|ManyBanksToOneDoc"",
      ""candidates"": [
        { ""candidateId"": ""<guid>"", ""candidateType"": ""Payment|JournalEntry"", ""amount"": <positive decimal> }
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
        string? LinkedDocumentType = null,
        // "In" when this payment is a customer receipt (AR — money into our
        // bank), "Out" when it's a vendor disbursement (AP — money out). Tells
        // the model an "In" bank txn can ONLY be matched to "In" payments.
        string? Direction = null);

    public sealed record OpenJeInput(
        string Id, string Number, DateTime Date, decimal NetAmount,
        string? Description, string? Reference,
        // Source-document context — populated when the JE came from a sale /
        // purchase / receipt / payment-voucher. NULL on manual JEs. Lets AI
        // do contact-aware 1:1 matching ("bank payee = Take Time Nature
        // Resort → JE whose source Receipt belongs to that contact") and
        // aggregator detection ("KSHOP deposit = sum of JEs for customers
        // who paid via KSHOP on the same day").
        string? SourceDocNumber = null,
        string? SourceDocType = null,
        DateTime? SourceDocDate = null,
        string? ContactName = null,
        string? ContactTaxId = null,
        // The entry's GROSS amount when it differs from `amount` (e.g. a
        // withholding-tax receipt whose bank line is net of WHT). AI may
        // match the bank deposit against EITHER figure.
        decimal? GrossAmount = null,
        // "In" when this JE posts a DEBIT to the bank account (deposit-side
        // movement), "Out" when it posts a CREDIT (withdrawal-side). NULL when
        // the JE doesn't touch the bank account at all. An "In" bank txn must
        // only be matched to "In" JEs (you cannot subtract one receipt from
        // another to fake a smaller deposit).
        string? Direction = null);

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
                // gross_amount present only when ≠ amount — match the bank
                // deposit against EITHER (handles WHT-netted receipts).
                gross_amount = j.GrossAmount,
                description = j.Description,
                reference = j.Reference,
                // Source document + contact — drives contact-aware matching.
                source_doc = j.SourceDocNumber == null ? null : new
                {
                    number = j.SourceDocNumber,
                    type = j.SourceDocType,
                    date = j.SourceDocDate?.ToString("yyyy-MM-dd"),
                },
                contact = j.ContactName == null ? null : new
                {
                    name = j.ContactName,
                    tax_id = j.ContactTaxId,
                },
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
            // Bulk cross-matching is beyond the local heuristic model — always
            // use the configured provider (DeepSeek). Without this the
            // orchestrator fell back to the local model, which returned
            // "Provider timeout after 10s" / no usable matches.
            ForceProviderCall = true,
            // The response is a match-plan JSON ({matches,unmatched,...}) — NOT
            // the standard primaryAnswer shape. Tell the orchestrator to hand
            // the raw content back as Success instead of rejecting it as a
            // "Schema mismatch" and falling back to the local model.
            RawPlanResponse = true,
            // Thai reasoning text on every match (~60-100 chars each) plus
            // long candidate arrays adds up: 4000 tokens truncated mid-
            // response on a typical month (44 txns), leaving the JSON
            // incomplete and unparseable ("Expected end of string..."). 16k
            // covers a month with ~150 txns; if we still hit the ceiling
            // the salvage parser recovers whatever match objects already
            // arrived complete.
            MaxTokensOverride = 16000,
            // DeepSeek with a ~150-txn + ~240-candidate prompt commonly takes
            // 20-40s. The provider-wide default (8s) was guaranteed to time
            // out for this feature — raise just this one call to 90s for the
            // bigger token budget.
            TimeoutSecondsOverride = 90,
        };
    }
}
