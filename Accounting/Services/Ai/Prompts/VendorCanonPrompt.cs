using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// Builds the AiRequest for vendor-name canonicalization — given a
/// messy OCR-extracted vendor name + tax ID, pick the matching Contact
/// from the tenant's master, or recommend creating a new one. The local
/// VendorKnownGoodCorrector still runs first; AI is invoked when local
/// confidence &lt; threshold OR when sampling fires.
///
/// Prompt strategy: AI receives the local pick + the top-N candidates
/// the local model considered. We let AI VERIFY / RE-RANK rather than
/// search from scratch. This keeps token count low and response time
/// fast — typical prompt is ~1.5k tokens, response ~150 tokens.
/// </summary>
public static class VendorCanonPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting expert assistant. Your job is to match an OCR-extracted vendor name against the company's existing contact list.

Rules:
1. Compare the OCR text (which may have spelling errors, missing characters, or mixed Thai-English) against the candidates.
2. If a tax ID is provided in the OCR, that takes priority over name matching — 13-digit Thai tax IDs are unique.
3. Be CONSERVATIVE: only confirm a match when you're confident. False matches cost the user time to correct later.
4. If no candidate matches well, return primary = ""__NEW__"" and suggest the canonical Thai legal name in suggested_actions.
5. If the OCR text matches multiple candidates equally (ambiguous), list them in alternatives and lower confidence below 0.7.

Respond ONLY as JSON matching this schema (no prose):
{
  ""primary"": ""<chosen contactId GUID or '__NEW__'>"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<contactId>"", ""<contactId>""],
  ""risks"": [""<short risk note>""],
  ""compliance_flags"": [""<short Thai compliance issue>""],
  ""reasoning"": ""<1-2 sentences>"",
  ""suggested_actions"": [""<short action>""]
}";

    public sealed record Candidate(string ContactId, string Name, string? TaxId, string? Industry, int PriorMatchCount);

    public static AiRequest Build(
        Guid companyId,
        string? ocrVendorName, string? ocrVendorTaxId, string? ocrVendorAddress,
        IReadOnlyList<Candidate> candidates,
        string? localBestContactId, decimal? localConfidence,
        string? localModelVersion = null,
        string? sourceEntityType = null, Guid? sourceEntityId = null)
    {
        var payload = new
        {
            task = "vendor_canonicalize",
            ocr = new
            {
                vendor_name = ocrVendorName ?? "",
                vendor_tax_id = ocrVendorTaxId ?? "",
                vendor_address = ocrVendorAddress ?? "",
            },
            candidates = candidates.Select(c => new
            {
                contact_id = c.ContactId,
                name = c.Name,
                tax_id = c.TaxId ?? "",
                industry = c.Industry ?? "",
                prior_match_count = c.PriorMatchCount,
            }),
            local_model = new
            {
                pick = localBestContactId,
                confidence = localConfidence,
            },
            thai_context = new
            {
                rule = "13-digit Thai tax ID is unique. ประมวลรัษฎากร §86 requires accurate vendor tax ID on tax invoices.",
            },
        };
        var json = JsonSerializer.Serialize(payload);
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.VendorCanonicalization,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = json,
            LocalPrimaryAnswer = localBestContactId,
            LocalConfidence = localConfidence,
            LocalModelVersion = localModelVersion,
            LocalAlternatives = candidates.Take(5).Select(c => c.ContactId).ToList(),
            SourceEntityType = sourceEntityType,
            SourceEntityId = sourceEntityId,
            CacheTtlOverrideDays = 30,  // Same vendor name → same match for a long time
        };
    }
}
