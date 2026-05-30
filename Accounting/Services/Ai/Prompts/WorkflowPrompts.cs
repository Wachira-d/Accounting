using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// Suggest a concrete fix for each soft-warn surfaced by
/// DocumentService.CollectApprovalWarningsAsync (7 categories: date
/// drift, missing VAT tax-id, unusual WHT rate, sticker-shock,
/// foreign currency, stock-negative, customer overdue).
///
/// AI receives the warning + full document snapshot + similar past
/// resolutions and proposes a specific action. Returns "Acknowledge"
/// when no action is needed (data is fine as-is).
/// </summary>
public static class ApprovalWarningFixPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting expert. The system flagged a soft warning on a document that's about to be approved. For each warning, propose a concrete fix or confirm that acknowledgement is correct.

Rules:
1. ""primary"" must be one of: ""Acknowledge"" (proceed as-is), ""Edit"" (change a field), ""Block"" (don't approve — would create wrong books).
2. ""suggested_actions"" should be SHORT imperative Thai sentences — what the bookkeeper should do step-by-step.
3. ""compliance_flags"" lists Thai tax-law issues (e.g. ""§86 requires vendor tax ID for input VAT claim"").
4. Be specific. ""Check with vendor"" is too vague — say ""โทรหา vendor ขอใบกำกับภาษีฉบับถูกต้อง"".
5. For sticker-shock warnings, compare to vendor's historical avg/max and either reassure or flag.

Respond ONLY as JSON:
{
  ""primary"": ""Acknowledge|Edit|Block"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<other primary value>""],
  ""risks"": [""<risk if user proceeds anyway>""],
  ""compliance_flags"": [""<Thai tax issue>""],
  ""reasoning"": ""<1-2 sentences>"",
  ""suggested_actions"": [""<imperative step 1>"", ""<step 2>""]
}";

    public static AiRequest Build(
        Guid companyId, Guid documentId,
        string warningText,
        object documentSnapshot,
        object? vendorHistorySnapshot,
        string? localFix,
        decimal? localConfidence)
    {
        var payload = new
        {
            task = "approval_warning_fix",
            warning = warningText,
            document = documentSnapshot,
            vendor_history = vendorHistorySnapshot,
            local_model = new { pick = localFix, confidence = localConfidence },
            thai_context = new
            {
                rules = new[]
                {
                    "§86: ใบกำกับภาษีต้องมี Tax ID ของผู้ขาย (13 หลัก) จึงจะใช้ภาษีซื้อได้",
                    "§50/52: WHT cash basis — recognize at payment, not at invoice",
                    "Stock negative = back-order. ห้ามอนุมัติถ้ายังไม่มี PO รองรับ",
                },
            },
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.ApprovalWarningFixSuggestion,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = localFix,
            LocalConfidence = localConfidence,
            LocalModelVersion = "ApprovalWarningCollector-v1",
            SourceEntityType = "Document",
            SourceEntityId = documentId,
            CacheTtlOverrideDays = 1,   // Doc state changes; short cache
            MaxTokensOverride = 400,
        };
    }
}

/// <summary>
/// Classify a CreditNote's reason — Return / Discount / Adjustment /
/// Writeoff (per ประมวลรัษฎากร §82/10). Drives Stock cascade + VAT
/// reversal logic.
/// </summary>
public static class CreditNoteReasonPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting expert classifying a credit note's reason per ประมวลรัษฎากร §82/10.

The four reasons:
- Return     : คืนสินค้า (sales return). Stock + VAT-sales BOTH reverse.
- Discount   : ส่วนลด/ลดราคา (price discount post-invoice). VAT reverses, stock unchanged.
- Adjustment : ปรับยอด (under-delivered / quality issue). VAT reverses, stock unchanged.
- Writeoff   : ตัดหนี้สูญบางส่วน (bad debt partial). VAT reverses, stock unchanged.

Decision heuristics:
- If CN line descriptions MATCH the original invoice lines → likely Return.
- If CN total ≤ 10% of original invoice AND lines describe ""ส่วนลด"" / ""ลดราคา"" → Discount.
- If CN references a specific quality issue (""ชำรุด"" / ""ไม่ครบ"") → Adjustment.
- If vendor flagged bad-debt / customer closed → Writeoff.

Respond ONLY as JSON:
{
  ""primary"": ""Return|Discount|Adjustment|Writeoff"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<other reason>""],
  ""risks"": [],
  ""compliance_flags"": [""<Thai tax issue if classification wrong>""],
  ""reasoning"": ""<1-2 sentences>"",
  ""suggested_actions"": []
}";

    public static AiRequest Build(
        Guid companyId, Guid creditNoteId,
        object creditNoteSnapshot, object? originalInvoiceSnapshot,
        string? localGuess, decimal? localConfidence)
    {
        var payload = new
        {
            task = "credit_note_reason",
            credit_note = creditNoteSnapshot,
            original_invoice = originalInvoiceSnapshot,
            local_model = new { pick = localGuess, confidence = localConfidence },
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.CreditNoteReasonClassification,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = localGuess,
            LocalConfidence = localConfidence,
            LocalAlternatives = new[] { "Return", "Discount", "Adjustment", "Writeoff" },
            LocalModelVersion = "heuristic-v1",
            SourceEntityType = "Document",
            SourceEntityId = creditNoteId,
            CacheTtlOverrideDays = 7,
            MaxTokensOverride = 250,
        };
    }
}

/// <summary>
/// Receipt vs TaxInvoice vs Invoice vs DeliveryNote vs PaymentVoucher
/// vs PurchaseInvoice etc. Used when local TfIdfNaiveBayesClassifier
/// confidence is below threshold OR when sampling fires.
/// </summary>
public static class DocumentTypeClassifyPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting expert. Classify what TYPE of document was scanned (the physical paper) AND what document type to CREATE in the books (perspective shift — a vendor's receipt becomes our PaymentVoucher).

Physical types (พิจารณาจาก wording บนเอกสาร):
- TaxInvoice            : ""ใบกำกับภาษี"" — มี VAT, มี Tax ID ทั้งคู่
- Invoice               : ""ใบแจ้งหนี้"" — ยังไม่ได้รับเงิน
- Receipt               : ""ใบเสร็จรับเงิน"" — รับเงินแล้ว
- DeliveryNote          : ""ใบส่งของ"" — ส่งของแล้ว, ยังไม่ออกใบกำกับภาษี
- CreditNote            : ""ใบลดหนี้"" — ลดยอด
- DebitNote             : ""ใบเพิ่มหนี้""
- Quotation             : ""ใบเสนอราคา""
- PurchaseOrder         : ""ใบสั่งซื้อ""

Respond ONLY as JSON:
{
  ""primary"": ""<physicalType>"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<other type>""],
  ""risks"": [],
  ""compliance_flags"": [""<§86 / §86/4 issue if mismatch>""],
  ""reasoning"": ""<1-2 sentences>"",
  ""suggested_actions"": [""<target_doc_type if our role differs>""]
}";

    public static AiRequest Build(
        Guid companyId, Guid scanResultId,
        string rawTextSample, string? extractedDocNumber, string? extractedVendorName,
        decimal? extractedTotal, string? localGuess, decimal? localConfidence)
    {
        // Truncate raw text — keep it under 1500 chars to control cost.
        // The header + summary lines carry the docType signal anyway.
        var snippet = rawTextSample.Length > 1500 ? rawTextSample[..1500] : rawTextSample;
        var payload = new
        {
            task = "document_type_classify",
            ocr_text_snippet = snippet,
            extracted = new
            {
                document_number = extractedDocNumber,
                vendor_name = extractedVendorName,
                total = extractedTotal,
            },
            local_model = new { pick = localGuess, confidence = localConfidence },
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.DocumentTypeClassification,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = localGuess,
            LocalConfidence = localConfidence,
            LocalModelVersion = "TfIdfNaiveBayes-v1",
            SourceEntityType = "OcrScanResult",
            SourceEntityId = scanResultId,
            CacheTtlOverrideDays = 14,
            MaxTokensOverride = 200,
        };
    }
}

/// <summary>
/// Infer WHT category (Revenue code 40, 50, 50bis, 53 etc.) given the
/// vendor / line description / amount. The rate (1%, 2%, 3%, 5%, 10%)
/// follows from the code. Thai compliance baseline:
///   • บริการทั่วไป         → 3%  (40(8))
///   • ค่าเช่า              → 5%  (40(5))
///   • ค่าโฆษณา / รับเหมา   → 2%  (40(8))
///   • Sales (วัตถุดิบ)     → 0%
///   • Royalty / ลิขสิทธิ์   → 3-15%
///   • บุคคลธรรมดา          → 1% (50)
/// </summary>
public static class WhtCategoryPrompt
{
    public const string SystemPrompt = @"You are a Thai tax expert. Given a vendor + line description + amount, propose the correct WHT (ภาษีหัก ณ ที่จ่าย) code + rate per Thai Revenue Department.

Common Thai WHT codes (รหัสประเภทเงินได้):
- 40(8) บริการทั่วไป (services)   : 3% (juristic) / 1% (individual via §50)
- 40(5) ค่าเช่า (rent)            : 5%
- 40(8) ค่าโฆษณา                  : 2%
- 40(8) รับเหมาก่อสร้าง            : 3% (juristic) / 1% (individual)
- 40(3) ค่าลิขสิทธิ์               : 3-15%
- 40(2) ค่าวิชาชีพ                 : 3%
- §50 บุคคลธรรมดา                  : 1%
- Sales of goods                  : ไม่ต้องหัก WHT

Rules:
1. If vendor is บุคคลธรรมดา (Personal) → likely 1% per §50bis.
2. If pure sale of goods → primary = ""None"" (no WHT).
3. If amount < ฿1,000 → primary = ""Skip"" (under threshold).

Respond ONLY as JSON:
{
  ""primary"": ""<code|None|Skip>"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<other code>""],
  ""risks"": [],
  ""compliance_flags"": [],
  ""reasoning"": ""<1-2 sentences>"",
  ""suggested_actions"": [""<rate_percent: 1|2|3|5|10>""]
}";

    public sealed record VendorWhtHistory(
        string IncomeCode,
        decimal Rate,
        int TimesUsed,
        decimal AvgAmount,
        DateTime LastUsedAt);

    public static AiRequest Build(
        Guid companyId, Guid? documentId,
        string? vendorName, string? vendorTaxId, string? vendorType,
        string lineDescription, decimal amount,
        string? localGuess, decimal? localConfidence,
        // New optional context — when the orchestrator has past PND.3/53
        // entries for this vendor, AI gets a "what did this vendor get
        // taxed at last time + most recent month" anchor that resolves
        // most ambiguity between 40(8) vs 40(5) vs 40(2).
        IReadOnlyList<VendorWhtHistory>? vendorWhtHistory = null,
        string? vendorIndustry = null,
        decimal? vendorAvg6Months = null,
        decimal? wht3ThresholdReached = null)
    {
        var payload = new
        {
            task = "wht_category_inference",
            vendor = new
            {
                name = vendorName,
                tax_id = vendorTaxId,
                type = vendorType,                  // "JuristicPerson" | "Personal" | null
                industry = vendorIndustry,
                avg_amount_6mo = vendorAvg6Months,  // baseline for "is this contract scale unusual?"
            },
            // Vendor-specific WHT history is the highest-leverage signal —
            // a vendor we've taxed at 40(8) 3% 12 months running is far
            // more likely to be the same again than a fresh inference.
            vendor_wht_history = vendorWhtHistory?.Select(h => new
            {
                income_code = h.IncomeCode,
                rate_percent = h.Rate * 100,
                times_used = h.TimesUsed,
                avg_amount = h.AvgAmount,
                last_used = h.LastUsedAt.ToString("yyyy-MM-dd"),
            }),
            line = new
            {
                description = lineDescription,
                amount,
                // Threshold flags for the §3 / §2 rules. The 1000-baht
                // cumulative-per-year threshold is the most common
                // mistake; surface it upfront.
                under_1000_threshold = amount < 1000m,
                wht3_year_to_date = wht3ThresholdReached,
            },
            local_model = new { pick = localGuess, confidence = localConfidence },
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.WhtCategoryInference,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = localGuess,
            LocalConfidence = localConfidence,
            LocalModelVersion = "WhtHeuristic-v2",
            SourceEntityType = "Document",
            SourceEntityId = documentId,
            CacheTtlOverrideDays = 30,
            MaxTokensOverride = 280,
        };
    }
}
