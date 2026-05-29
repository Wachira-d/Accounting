using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// Comprehensive OCR result review prompt. Sends EVERYTHING the system
/// knows about the scan + the company to AI for one big verification:
///   • Document type (Receipt / TaxInvoice / Invoice / PurchaseInvoice /
///     DeliveryNote / CreditNote / DebitNote / Quotation / PO / etc.)
///   • Who is buyer, who is seller, our role (Buyer/Seller)
///   • Line items breakdown
///   • Totals: subtotal, vat, wht, total — and whether vendor is
///     ACTUALLY VAT-registered (not just printed "ใบกำกับภาษี")
///   • Calendar date (with explicit Thai/BE year context to prevent
///     timezone day-shift)
///   • Any data inconsistencies flagged
///
/// Used as "Tier 4" review after the local pipeline finishes — AI's
/// answer becomes the authoritative source when the user accepts.
/// Every field correction trains the local field-pattern learner.
/// </summary>
public static class OcrReviewPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting expert reviewing a scanned document. The local OCR pipeline has already extracted what it could. Your job:

1. CONFIRM or CORRECT each extracted field using the raw text as ground truth.
2. Identify the document type per ประมวลรัษฎากร terminology.
3. Determine BUYER vs SELLER given the company's own Tax ID + document text.
4. Check VAT correctness:
   - §86: Only a VAT-registered person (13-digit Tax ID + เป็นผู้ประกอบการจดทะเบียน) may issue a tax invoice with VAT.
   - If vendor has no valid Tax ID but the text says ""ใบกำกับภาษี"", flag it as a non-VAT vendor using a generic receipt template. DO NOT back-calculate VAT.
   - VAT line must be explicit on the document. Don't invent it.
5. Date: extract the calendar date. Thai documents may use Buddhist year
   (BE = Gregorian + 543). The output MUST be Gregorian ISO 8601 (yyyy-MM-dd)
   AND must preserve the EXACT calendar day the document shows
   (don't shift +/- 1 day for any timezone reason).
6. WHT: only flag when explicit on the document, OR when the line type
   reasonably requires it per ประมวลรัษฎากร §50/52.

Respond ONLY as JSON:
{
  ""primary"": ""<our_target_document_type>"",   // Receipt | TaxInvoice | Invoice | PurchaseInvoice | DeliveryNote | CreditNote | DebitNote | Quotation | PurchaseOrder | PaymentVoucher
  ""confidence"": <0.0-1.0>,
  ""corrections"": {
    ""document_number"": ""..."",
    ""document_date"": ""yyyy-MM-dd"",     // Gregorian, exact day from paper
    ""vendor_name"": ""..."",
    ""vendor_tax_id"": ""..."",
    ""buyer_name"": ""..."",
    ""buyer_tax_id"": ""..."",
    ""our_role"": ""Buyer|Seller"",
    ""sub_total"": <decimal or null>,
    ""vat_amount"": <decimal or null>,      // null when vendor not VAT-registered
    ""wht_amount"": <decimal or null>,
    ""total_amount"": <decimal>,
    ""currency"": ""THB"",
    ""is_vat_registered_vendor"": <bool>,
    ""payment_terms_days"": <int or null>
  },
  ""line_items"": [
    { ""description"": ""..."", ""quantity"": <decimal>, ""unit_price"": <decimal>, ""amount"": <decimal>, ""suggested_account_code"": ""..."", ""is_fixed_asset_candidate"": <bool>, ""is_inventory_candidate"": <bool> }
  ],
  ""alternatives"": [""<other_doc_type>""],
  ""risks"": [""<short risk>""],
  ""compliance_flags"": [""<Thai tax issue>""],
  ""reasoning"": ""<2-3 sentences>"",
  ""suggested_actions"": [""<imperative step>""]
}";

    public static AiRequest Build(
        Guid companyId, Guid scanResultId,
        string rawText,
        object extractedSnapshot,        // current local-pipeline extraction
        object companyContext,            // tax ID, address, accounts of record
        object? vendorHistory,            // last N docs from same vendor (or null)
        string? localGuessTargetType,
        decimal? localConfidence)
    {
        // Truncate raw text — header + summary lines carry 95% of the
        // signal anyway. 3500 chars covers typical 1-2 page invoices.
        var snippet = rawText.Length > 3500 ? rawText[..3500] + "...[truncated]" : rawText;
        var payload = new
        {
            task = "ocr_full_review",
            ocr_raw_text = snippet,
            local_extraction = extractedSnapshot,
            our_company = companyContext,
            vendor_history = vendorHistory,
            local_model = new { pick = localGuessTargetType, confidence = localConfidence },
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.OcrFullReview,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = localGuessTargetType,
            LocalConfidence = localConfidence,
            LocalModelVersion = "OcrPipeline-v1",
            SourceEntityType = "OcrScanResult",
            SourceEntityId = scanResultId,
            CacheTtlOverrideDays = 1,
            MaxTokensOverride = 1500,
        };
    }
}

/// <summary>
/// Stock decision prompt. For each line item in an OCR'd document,
/// decide whether to:
///   • Create a new Product / Supply / FixedAsset
///   • Update quantity on an existing matching item
///   • Match to an existing item even though names differ semantically
///     (the user's example: "สกรู M6 ยาว 20mm" == "สกรู 1/4 นิ้ว ยาว 3/4 นิ้ว")
///
/// Sends the company's FULL product catalog (paginated when >200 items)
/// + the line items + Thai-tax classification rules. AI returns a
/// decision per line.
/// </summary>
public static class StockDecisionPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting + inventory expert. For each line item on a purchase document, decide how to record it.

Categories per Thai tax law:
- Inventory (สินค้าคงเหลือ) → debit 1140 series, COGS on sale
- Supplies / Consumables (วัสดุสิ้นเปลือง) → expense immediately (5xxx)
- Fixed Asset (สินทรัพย์ถาวร) → ≥฿15,000 + life >1 year → capitalize (1500-1699)
- Service → expense (5xxx)

For each line, classify:
1. category: ""Inventory"" | ""Supply"" | ""FixedAsset"" | ""Service""
2. action: ""CreateNew"" | ""UpdateExisting"" | ""MatchUncertain""
3. matched_product_id: GUID if matching an existing item, else null
4. match_confidence: 0.0-1.0 (only matters when action != CreateNew)
5. equivalence_note: brief Thai explanation of WHY two differently-named items are the same (e.g. ""สกรู M6 = 1/4 นิ้ว (เทียบขนาดมาตรฐาน)"")

Match heuristics for the user's example:
- Use unit conversions (mm/นิ้ว/cm), threading specs (M6/1/4), nominal sizes.
- ""สกรู M6 ยาว 20mm"" ≈ ""สกรู 1/4 นิ้ว ยาว 3/4 นิ้ว"" because M6 ~= 1/4"" (6mm ≈ 6.35mm) and 20mm ≈ 3/4"" (19.05mm).
- When in doubt, MatchUncertain — user confirms.

Respond ONLY as JSON:
{
  ""primary"": ""ok"",
  ""confidence"": <0.0-1.0>,
  ""lines"": [
    {
      ""line_index"": <int>,
      ""description"": ""..."",
      ""category"": ""Inventory|Supply|FixedAsset|Service"",
      ""action"": ""CreateNew|UpdateExisting|MatchUncertain"",
      ""matched_product_id"": ""<guid or null>"",
      ""match_confidence"": <0.0-1.0 or null>,
      ""equivalence_note"": ""<short Thai explanation>"",
      ""suggested_account_code"": ""..."",
      ""new_product_suggestion"": { ""code"": ""..."", ""name"": ""..."", ""unit"": ""..."" }
    }
  ],
  ""compliance_flags"": [],
  ""reasoning"": ""<short overall>"",
  ""suggested_actions"": []
}";

    public static AiRequest Build(
        Guid companyId, Guid scanResultId,
        object docHeader,
        object[] lineItems,
        object[] productCatalog,
        string companyIndustry = "general")
    {
        var payload = new
        {
            task = "stock_decision",
            company_industry = companyIndustry,
            document = docHeader,
            line_items = lineItems,
            product_catalog = productCatalog,
            thai_tax_rules = new[]
            {
                "Fixed asset threshold: ≥฿15,000 + life >1 year",
                "Input VAT (1531) only with §86 valid tax invoice",
                "Inventory (สินค้า) vs Supply (วัสดุสิ้นเปลือง) — supply = expense immediately",
            },
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.StockMovementValidation,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = null,
            LocalConfidence = null,
            LocalModelVersion = "FixedAssetDetector-v1",
            SourceEntityType = "OcrScanResult",
            SourceEntityId = scanResultId,
            CacheTtlOverrideDays = 7,
            MaxTokensOverride = 3000,
        };
    }
}

/// <summary>
/// Bank reconciliation comprehensive match prompt. Goes beyond
/// 1:1 matching — handles cases like:
///   • One deposit covers multiple invoices (1 statement → N docs)
///   • One invoice was paid in installments (N statements → 1 doc)
///   • A receipt + WHT-cert + bank fee all reconcile to one invoice
///   • Cross-currency settlements (FX conversion)
///   • Vendor offset (we owe them ABC, they owe us XYZ → net settlement)
/// </summary>
public static class BankComprehensiveMatchPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting expert reconciling a bank transaction against open documents + recent payments.

Match patterns to consider:
1. 1-to-1: statement amount == document outstanding, memo contains docnum.
2. 1-to-many: deposit aggregates multiple invoices (vendor paid 3 invoices at once).
3. Many-to-1: customer paid in installments — current statement is partial.
4. Offset: we have both a TaxInvoice receivable AND a PaymentVoucher payable for the same counterparty → net settlement.
5. With deductions: receipt = invoice − WHT cert − bank fee.

When NO complete match found, identify WHAT'S MISSING:
- Need a WHT certificate to balance?
- Need to record bank fee separately?
- Counterparty needs offset adjustment?

Respond ONLY as JSON:
{
  ""primary"": ""<docId|__SPLIT__|__INCOMPLETE__|__NEW__>"",
  ""confidence"": <0.0-1.0>,
  ""match_pattern"": ""1to1|1toMany|Manyto1|Offset|WithDeductions|Incomplete"",
  ""matched_documents"": [
    { ""document_id"": ""..."", ""amount_applied"": <decimal>, ""role"": ""primary|deduction|fee"" }
  ],
  ""missing_pieces"": [""<what is still needed e.g. WHT certificate for ฿500""],
  ""alternatives"": [""<other candidate set>""],
  ""risks"": [""<risk>""],
  ""compliance_flags"": [],
  ""reasoning"": ""<2-3 sentences>"",
  ""suggested_actions"": [""<imperative step>""]
}";

    public static AiRequest Build(
        Guid companyId, Guid bankTransactionId,
        object statementLine,
        object[] candidateDocuments,
        object[] recentPayments,
        object? counterpartyOffsetSummary)
    {
        var payload = new
        {
            task = "bank_comprehensive_match",
            statement_line = statementLine,
            candidate_documents = candidateDocuments,
            recent_payments = recentPayments,
            counterparty_offset = counterpartyOffsetSummary,
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.BankStatementMatch,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = null,
            LocalConfidence = null,
            LocalModelVersion = "BankMatcher-v1",
            SourceEntityType = "BankTransaction",
            SourceEntityId = bankTransactionId,
            CacheTtlOverrideDays = 1,
            MaxTokensOverride = 800,
        };
    }
}

/// <summary>
/// AR/AP analysis prompt. AI looks at the company's outstanding
/// receivables + payables + customer/vendor history and surfaces:
///   • Who's at risk of becoming a bad debt
///   • Who pays consistently late but always pays
///   • Recommended collection actions per customer
///   • Cash gap forecast based on payment patterns
/// </summary>
public static class ArApAnalysisPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting analyst reviewing accounts receivable / payable. Output conversational Thai analysis with specific recommendations.

Respond ONLY as JSON:
{
  ""primary"": ""<headline finding in 1 short sentence>"",
  ""confidence"": <0.0-1.0>,
  ""risk_buckets"": {
    ""high_risk_customers"": [""<customer name>: <reason>""],
    ""late_but_reliable"": [""<customer name>: <pattern>""],
    ""high_risk_vendors"": [""<vendor name>: <reason>""]
  },
  ""cash_gap_forecast"": ""<short Thai narrative>"",
  ""alternatives"": [],
  ""risks"": [],
  ""compliance_flags"": [],
  ""reasoning"": ""<2-4 Thai sentences>"",
  ""suggested_actions"": [""<imperative Thai action>""]
}";

    public static AiRequest Build(
        Guid companyId,
        object arAging, object apAging,
        object customerPaymentHistory, object vendorPaymentHistory)
    {
        var payload = new
        {
            task = "ar_ap_analysis",
            ar_aging = arAging,
            ap_aging = apAging,
            customer_history = customerPaymentHistory,
            vendor_history = vendorPaymentHistory,
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.AgingExplanation,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = null,
            LocalConfidence = null,
            LocalModelVersion = "AgingHeuristic-v1",
            SourceEntityType = "Company",
            SourceEntityId = companyId,
            CacheTtlOverrideDays = 1,
            MaxTokensOverride = 2000,
        };
    }
}

/// <summary>
/// Convert-OCR-to-target-document suggestion. Given an OCR'd source
/// (e.g. Invoice), AI proposes which target documents make sense to
/// create (Receipt, TaxInvoice, DeliveryNote, PaymentVoucher, etc.)
/// + the field mappings to pre-fill.
/// </summary>
public static class DocumentConversionPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting workflow expert. Given a scanned source document, list which target document types can be created from it AND describe what to pre-fill.

Common Thai workflows:
- Invoice (ใบแจ้งหนี้) → Receipt (รับชำระ) | TaxInvoice (ใบกำกับภาษี) | DeliveryNote (ใบส่งของ)
- Quotation (ใบเสนอราคา) → Invoice (ใบแจ้งหนี้) | PurchaseOrder (รับเป็น PO ของเรา)
- PurchaseInvoice (ใบแจ้งหนี้รับเข้า) → PaymentVoucher (ใบสำคัญจ่าย)
- TaxInvoice (ใบกำกับภาษี) → Receipt | CreditNote (ใบลดหนี้)
- DeliveryNote (ใบส่งของ) → TaxInvoice

Respond ONLY as JSON:
{
  ""primary"": ""<recommended_target_doc_type>"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<other target>""],
  ""targets"": [
    { ""target_type"": ""..."", ""rationale"": ""<short Thai>"", ""prefill_strategy"": ""<copy_lines|copy_header_only|partial_amount>"" }
  ],
  ""risks"": [],
  ""compliance_flags"": [],
  ""reasoning"": ""<1-2 sentences>"",
  ""suggested_actions"": []
}";

    public static AiRequest Build(
        Guid companyId, Guid scanResultId,
        string scannedDocType, object scannedSnapshot,
        string ourRole)
    {
        var payload = new
        {
            task = "document_conversion",
            scanned_doc_type = scannedDocType,
            our_role = ourRole,
            scanned_snapshot = scannedSnapshot,
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.DocumentConversionSuggestion,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = null,
            LocalConfidence = null,
            LocalModelVersion = "WorkflowMap-v1",
            SourceEntityType = "OcrScanResult",
            SourceEntityId = scanResultId,
            CacheTtlOverrideDays = 30,
            MaxTokensOverride = 500,
        };
    }
}
