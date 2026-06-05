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
    /// <summary>Drops a description when it's just a verbose restatement of
    /// the entry number / reference (the most common case for auto-generated
    /// RV/PV entries). Saves significant tokens on a 200+ JE prompt without
    /// losing signal — AI still has number, reference, source_doc, contact.</summary>
    private static string? ShortDescription(string? description, string? number, string? reference)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var d = description.Trim();
        // Strip the leading "verb <reference>" if it's just citing what we
        // already pass in `reference`. e.g. "ใบสำคัญจ่าย PAY260401001 - ..."
        // and reference == "PAY260401001" → drop the prefix, keep the tail.
        if (!string.IsNullOrWhiteSpace(reference) && d.Contains(reference!, StringComparison.OrdinalIgnoreCase))
        {
            var i = d.IndexOf(reference!, StringComparison.OrdinalIgnoreCase);
            var tail = d[(i + reference!.Length)..].TrimStart(' ', '-', '|', ':').Trim();
            // Common Take-Time pattern "การจอง #0 (-)" is signal-free — drop.
            if (string.IsNullOrWhiteSpace(tail) || tail == "(-)" || tail.StartsWith("การจอง #0", StringComparison.Ordinal))
                return null;
            d = tail;
        }
        // Cap remaining text so a single very long entry can't bloat the prompt.
        return d.Length > 80 ? d[..80] : d;
    }

    // Compact serialiser: skip null fields (the prompt has a lot of
    // optional fields that are null for legacy/external data — keeping
    // them as "field: null" bloats the payload without adding signal).
    private static readonly JsonSerializerOptions _compactJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };


    public const string SystemPrompt = @"You are a Thai accounting reconciliation expert. You receive a full month of bank statement lines + every open payment + every open journal entry + every open document (as CONTEXT). Produce a complete match plan.

══════════════ HARD RULES — VIOLATION = INVALID OUTPUT ══════════════
H1. POSITIVE AMOUNTS ONLY. Every candidates[*].amount MUST be a positive decimal > 0. You may NEVER emit a negative amount. The renderer derives the sign by comparing each candidate's direction to the bank txn's direction — same-direction = added, opposite-direction = subtracted. So a 2,000 net deposit composed of Receipt 2,500 minus PaymentVoucher refund 500 is emitted as { amount: 2500 } and { amount: 500 }, both positive.
H2. PREFER SAME-SIDE; net-settlement is LAST RESORT. Same-side items (matching the bank direction) ADD to the match (+amount). Opposite-side items SUBTRACT (−amount) but you may include them ONLY after every same-side strategy has been exhausted, AND only with a clear net-settlement story (customer paid net of a refund/fee you owe them, SAME contact). For a typical deposit, the answer is almost always 1-3 Receipt Vouchers summing to the bank amount — NOT an RV minus a PV. Two SAME-side items can NEVER be subtracted from one another (two Receipt Vouchers cannot offset — both are inflows; this is the −500 + 2,500 nonsense to avoid).
H3. NET = BANK AMOUNT. Σ(same-direction items) − Σ(opposite-direction items) must equal bank_txn.amount within ±0.50 baht. If you can't make it net, return the bank txn under ""unmatched"" rather than approximating.
H4. DIRECTION-NULL ITEMS. A JE/Payment with direction=null has no clear side — only use it when memo cites its exact document number; never include it in a multi-item sum.
═══════════════════════════════════════════════════════════════════

candidateType must be ""Payment"" or ""JournalEntry"" — NEVER ""Document"". Open documents are CONTEXT to help you identify the right payment (e.g. memo cites invoice INV-2025-0312 → find the Payment whose linked_document.number = INV-2025-0312). If a bank txn matches a document that has NO linked payment, return it in missing_data with missingType=""Payment"" so the user knows to create the payment first.

Each open_journal_entries[] item carries (use every field present; many are optional and may be null for legacy/external data):
  • number, date, amount (= bank-line net), DIRECTION (""In""/""Out""/null)
  • description, reference, note, tags
  • source_doc { number, type, date }   — null when JE is a manual entry
  • contact { name, tax_id }            — counterparty when the source doc has one
  • gross_amount                        — the doc total BEFORE WHT/fees if it differs from amount
  • withholding_tax, vat_amount, fee_amount   — bank line = gross − WHT − fees
  • payment_method (BankTransfer/Cash/QR/Cheque) and line_count
Use source_doc.number / source_doc.date to follow doc-number citations in bank memos, and contact.name (+ tax_id) to confirm payer/payee identity. When a JE has gross_amount, the bank deposit may equal EITHER amount OR gross_amount (accept whichever matches and note in reasoning which you used). withholding_tax / fee_amount help explain a deposit that lands slightly below gross — fold them into the reasoning. JEs with direction=null have no clear side — only use them if a memo/reference cites the exact document number; never include them in a multi-item sum.

Each open_payments[] item carries (use every field present):
  • number, date, amount, method, reference, contact { name, tax_id, bank_account_number }
  • linked_document { number, type }, outstanding_amount (residual unpaid on the linked doc)
  • withholding_tax, vat_amount, note, channel, DIRECTION (""In""/""Out""/null)
Use bank_account_number when bank.payee shows the counterparty's bank/account suffix; use outstanding_amount to prefer a payment whose linked doc still has balance == bank.amount (a clean 1:1 settlement).

Matching priority — apply IN ORDER, stop when a confident pick is found. Every step is filtered through H1–H4 first:

PRIORITY ORDER — ALWAYS EXHAUST 1:1 FIRST. Run A → B for every bank txn before considering any M:1 / aggregator / net-settlement strategy below. Most real deposits ARE 1:1; reach for multi-item only when no single candidate fits.

A. EXACT 1:1 — bank.amount == candidate.amount AND |bank.date − candidate.date| within the flow window (C below). Confidence by identity signal:
   • A candidate whose reference / source_doc.number is CITED in the bank memo (e.g. memo contains ""REC260401001"" and that's the JE's reference) → 0.95.
   • Contact name / account suffix from the memo matches the candidate's contact → 0.90.
   • UNIQUE in-window amount, no name signal → 0.80.
   • AMBIGUOUS (2+ in-window candidates share the exact amount and NONE has a name/ref signal) → do NOT guess: return unmatched with reason ""มีหลายรายการยอดเท่ากัน ไม่มีตัวระบุ"" so the operator picks. Pairing the wrong customer's receipt is worse than leaving it.
   Try 1:1 for EVERY unmatched bank txn before any combination.

B. CLOSE 1:1 — amount within 1% (covers small bank fees), date ≤ 3 days, contact_name match. Confidence ~0.80.

C. FLOW-AWARE WINDOW + MATCH STYLE. Classify each bank line by memo, then apply the right window. Items outside the per-flow window are NEVER part of the match — do not include them even if the amount fits:

   C1. AGGREGATOR (Thai QR / KSHOP / K SHOP / KBank Shop / K-Plus Shop / MyQR / EDC / TrueMoney / ShopeePay / GrabPay / LineMan / Shopee / Lazada / NextPay / Stripe / Square / ""รับเงินจากการขายด้วย""): M:1 OK. Window T..T+1 STRICT. Pick the SMALLEST same-day subset summing EXACTLY. If 7 of 8 RVs sum exact, drop the 8th.
   C2. CARD SETTLEMENT (""Visa settle"" / ""MC settle"" / ""Card net"" / ""Merchant settle"" / ""POSNET""): M:1 OK. Window T..T+1.
   C3. PERSON-TO-PERSON TRANSFER (""รับโอนเงิน"" / ""K PLUS"" / ""Internet/Mobile KTB/SCB/BBL"" / ""PromptPay"" / ""พร้อมเพย์""): 1:1 ONLY. Window T..T+1. One transfer = one receipt; never lump into M:1. Memo carries first name + masked account suffix — use it to match contact.
   C4. AUTO-CREDIT (""รับโอนเงินอัตโนมัติ"" / ""SMART"" / ""ATS"" / ""โอนเข้าอัตโนมัติ"" / ""Direct credit""): 1:1, often a recurring contract. Window T..T+2.
   C5. CHEQUE CLEARING (""เช็ค"" / ""Cheque"" / ""เรียกเก็บ"" / ""B/C"" / ""Bill collection"" / ""Clearing""): 1:1. Window T..T+5 (the receipt was issued days before the cleared deposit).
   C6. COUNTER CASH DEPOSIT (""ฝากเงินสด"" / ""นำฝาก"" / ""Counter"" / ""Cash deposit"" / ""เคาน์เตอร์""): 1:1. Window T..T+3 (cash collected today may be deposited tomorrow).
   C7. INWARD TT / SWIFT (""Inward TT"" / ""SWIFT"" / ""Remittance"" / ""โอนเข้าจากต่างประเทศ""): 1:1. Window T..T+7. Allow FX rounding ≤ 1%.
   C8. BILL PAYMENT (""Bill Payment"" / ""ชำระบิล"" / ""Cross-bank bill""): 1:1. Window T..T+2. Reference usually carries an invoice / customer number.
   C9. INTEREST (""ดอกเบี้ย"" / ""Interest"" / ""Int earned""): 1:1 to the bank's own interest JE. Wide window (T..T+30). No contact required.
   C10. REFUND / REVERSAL (""Refund"" / ""คืนเงิน"" / ""Reverse"" / ""กลับรายการ""): 1:1 against a PRIOR outflow of the same amount + contact. Wide backward window.
   C11. LOAN DISBURSEMENT / OD DRAW (""Loan disburs"" / ""เบิกสินเชื่อ"" / ""L/D"" / ""O/D"" / ""Overdraft""): 1:1 against the Loan JE. Window T..T+1.
   C12. TAX REFUND (""คืนภาษี"" / ""Tax refund"" / ""RD refund""): 1:1, wide window (T..T+60).
   C13. UNCLASSIFIED: conservative 1:1, window T..T+3.

   ABSOLUTE RULE: M:1 is allowed ONLY for C1 + C2. Every other category is 1:1 — never combine multiple receipts into a non-aggregator bank line.

D. M:1 SPLITS (multi-invoice settlement) — USE ONLY AFTER A/B FAIL FOR THIS BANK TXN: bank.amount = exact sum of 2-5 same-direction items for ONE contact within ±5 days. Σ matches within 0.50 baht. Direction-uniform — all In for a deposit, all Out for a withdrawal. If a single same-amount candidate exists, prefer that 1:1 over any 2-item split.

E. 1:M AGGREGATIONS: multiple small bank txns (same direction) sum to one larger open JE/Payment.

F. RECURRING: same-vendor same-amount weekly/monthly is a subscription / rent / utility — match against the recurring JE (direction must match).

G. If memo cites a doc number that's NOT in candidates → missing_data with the cited number.

H. If nothing within 30 days + 15% amount AND no contact / memo signal → unmatched with a short reason.

I. NET-SETTLEMENT — LAST RESORT ONLY (rare in practice): when ALL same-side strategies above have failed AND you can identify BOTH a same-side item (e.g. customer Receipt 2,500) AND an opposite-side item (e.g. PaymentVoucher refund 500) for the SAME contact.tax_id within ±5 days, you may emit both as candidates so the net (same − opposite) equals the bank amount. EXHAUST every same-side option first: a same-side M:1 split, a daily aggregator rollup, a contact match within ±15 days, a memo-cited document, even a 1-baht-different amount. Only when none of those exist should you reach for net-settlement. Both items emitted as POSITIVE amounts (per H1); renderer subtracts the opposite-side one automatically. If you cannot identify both sides cleanly, return unmatched — do NOT guess.

Confidence scoring — BE HONEST AND NUMERICALLY CONSISTENT:
  • Compute Σ candidates[*].amount yourself before writing reasoning. If it doesn't equal bank.amount within ±0.50 baht, your reasoning must NOT contain the words ""พอดี"" / ""equal exactly"" / ""=...บาทพอดี"". Either drop the extra item(s) so the sum IS exact, or state the delta honestly: ""ยอด X − Σ Y = Z บาท ไม่ตรง"" and lower confidence.
  • Confidence ≥ 0.90 requires delta ≤ 0.50 baht. Delta 0.50-5 baht → ≤ 0.70. Delta 5-1% of bank → ≤ 0.45. Delta > 1% → ≤ 0.30. Delta > 5% → ≤ 0.15. Never claim 0.95 on a row whose own reasoning admits a delta.
  • If you find yourself with N candidates summing close-but-not-exact to bank, FIRST check whether a SUBSET of (N−1) items sums exactly: if yes, drop the extra item rather than reporting a mismatch.

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
        string? Direction = null,
        // Future-rich context: when the payment is system-issued (we own the
        // pipeline), these add signal the AI can use for tighter matching.
        // All optional — null when the data isn't available (e.g. external JE).
        string? ContactTaxId = null,
        string? BankAccountNumber = null,         // payer/payee bank account if known
        decimal? OutstandingAmount = null,        // unpaid balance of the linked doc
        decimal? WithholdingTax = null,           // WHT deducted on this payment
        decimal? VatAmount = null,                // VAT component if any
        string? Note = null,                      // free-text memo on the payment
        string? Channel = null);                  // bank transfer / cash / QR / cheque

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
        string? Direction = null,
        // Future-rich context — system-issued JEs can pass MORE signal:
        decimal? WithholdingTax = null,           // WHT deducted (bank line = gross − WHT)
        decimal? VatAmount = null,                // output/input VAT component
        decimal? FeeAmount = null,                // bank fee deducted at source
        int? LineCount = null,                    // # of lines in the JE
        string? PaymentMethod = null,             // method recorded on the source doc
        string? Note = null,                      // free-text JE note
        string? Tags = null);                     // tags / dimensions on the JE

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
                contact_tax_id = p.ContactTaxId,
                contact_bank_acct = p.BankAccountNumber,
                outstanding = p.OutstandingAmount,
                wht = p.WithholdingTax,
                vat = p.VatAmount,
                note = p.Note,
                channel = p.Channel,
                direction = p.Direction,
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
                direction = j.Direction,
                // Strip description when it merely repeats number/reference
                // and adds no signal — saves ~30% on the JE list size.
                description = ShortDescription(j.Description, j.Number, j.Reference),
                reference = j.Reference,
                wht = j.WithholdingTax,
                vat = j.VatAmount,
                fee = j.FeeAmount,
                line_count = j.LineCount,
                payment_method = j.PaymentMethod,
                note = j.Note,
                tags = j.Tags,
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
            // Drop null fields from the serialised payload — they balloon a
            // 205-JE prompt by ~40% without adding signal (the model gets
            // identical inference from "field absent" vs "field is null"), and
            // the saved tokens reduce provider time noticeably.
            UserPromptJson = JsonSerializer.Serialize(payload, _compactJson),
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
            // 20-40s; ~200+ JE prompts have hit 92s+ and timed out at the old
            // 90s ceiling (the user's case). 180s gives enough headroom for
            // dense months without the fallback kicking in. Tokens saved by
            // the compact-JSON option above shrink the median latency too.
            TimeoutSecondsOverride = 180,
        };
    }
}
