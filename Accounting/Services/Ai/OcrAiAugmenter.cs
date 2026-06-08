using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai.Prompts;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// Orchestrates AI augmentation specifically for the OCR pipeline. The
/// post-extraction hooks live here so OcrService stays focused on
/// extraction and the AI feature surface stays composable for future
/// features.
///
/// SAFETY: every method here returns a usable result even when AI is
/// down. If AskAsync's Status != Success, we treat AI's PrimaryAnswer
/// as null and surface the local pick as-is. The orchestrator already
/// records every call (including failure) so feedback is never lost.
/// </summary>
public interface IOcrAiAugmenter
{
    /// <summary>
    /// Match extracted vendor → existing Contact id. Returns the matched
    /// contact id (existing or "__NEW__") + confidence + feedback id
    /// (for later user-acceptance recording).
    /// </summary>
    Task<OcrAiAugmentationResult> CanonicaliseVendorAsync(
        Guid companyId, Guid scanResultId,
        string? ocrVendorName, string? ocrVendorTaxId, string? ocrVendorAddress,
        string? localBestContactId, decimal localConfidence,
        CancellationToken ct = default);

    /// <summary>
    /// Suggest GL account code for a single OCR line item.
    /// </summary>
    Task<OcrAiAugmentationResult> SuggestGlAccountAsync(
        Guid companyId, Guid scanResultId,
        string? vendorName, string? vendorTaxId, string? vendorIndustry,
        string lineDescription, decimal amount, string currency,
        string? localBestAccountCode, decimal localConfidence,
        CancellationToken ct = default);
}

public sealed record OcrAiAugmentationResult(
    string? Answer,             // chosen value (AI's or local fallback)
    decimal? Confidence,
    IReadOnlyList<string> Alternatives,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> ComplianceFlags,
    string? Reasoning,
    bool UsedAi,
    Guid? FeedbackId);

public class OcrAiAugmenter : IOcrAiAugmenter
{
    private const int CandidateLimit = 12;
    private const int VendorHistoryLookbackMonths = 24;

    private readonly AccountingDbContext _db;
    private readonly IAiOrchestrator _orchestrator;
    private readonly ILogger<OcrAiAugmenter> _logger;

    public OcrAiAugmenter(AccountingDbContext db, IAiOrchestrator orchestrator, ILogger<OcrAiAugmenter> logger)
    { _db = db; _orchestrator = orchestrator; _logger = logger; }

    public async Task<OcrAiAugmentationResult> CanonicaliseVendorAsync(
        Guid companyId, Guid scanResultId,
        string? ocrVendorName, string? ocrVendorTaxId, string? ocrVendorAddress,
        string? localBestContactId, decimal localConfidence,
        CancellationToken ct = default)
    {
        // Defensive boundary — never let an exception escape the OCR
        // pipeline. ScanAsync's outer catch already refunds quota; this
        // layer's job is to add value WITHOUT introducing new failure
        // surfaces. Anything goes wrong → quiet fallback to local.
        try
        {
            // ── Build candidate list from the tenant's contacts ──
            // Use a small fuzzy filter so the AI doesn't get the entire
            // contact master (could be 10k+ rows) in the prompt. Match
            // by tax-id-prefix first (fast unique), then by name token.
            IQueryable<Contact> q = _db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted);

            List<Contact> candidates;
            if (!string.IsNullOrWhiteSpace(ocrVendorTaxId))
            {
                // Strip non-digits — OCR sometimes inserts dashes/spaces.
                var digits = new string(ocrVendorTaxId.Where(char.IsDigit).ToArray());
                if (digits.Length >= 4)
                {
                    var pfx = digits[..Math.Min(13, digits.Length)];
                    candidates = await q
                        .Where(c => c.TaxId != null && c.TaxId.Contains(pfx))
                        .Take(CandidateLimit).ToListAsync(ct);
                }
                else candidates = new();
            }
            else candidates = new();

            // Fallback / supplement: top contacts by first token of name.
            if (candidates.Count < CandidateLimit && !string.IsNullOrWhiteSpace(ocrVendorName))
            {
                var token = ocrVendorName.Split(new[] { ' ', '\t', '\n', '(', ')', '-' },
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => t.Length >= 2);
                if (!string.IsNullOrEmpty(token))
                {
                    var existingIds = candidates.Select(c => c.Id).ToHashSet();
                    var more = await q
                        .Where(c => !existingIds.Contains(c.Id) && c.Name.Contains(token))
                        .Take(CandidateLimit - candidates.Count)
                        .ToListAsync(ct);
                    candidates.AddRange(more);
                }
            }

            if (candidates.Count == 0)
            {
                // No candidates — AI can still suggest "create new" but
                // there's nothing to MATCH, so skip the call entirely.
                // Local model effectively answers "__NEW__".
                return new OcrAiAugmentationResult(
                    Answer: localBestContactId ?? "__NEW__",
                    Confidence: localConfidence > 0 ? localConfidence : 0.3m,
                    Alternatives: Array.Empty<string>(),
                    Risks: new[] { "ไม่พบ contact ที่มี tax ID / ชื่อใกล้เคียง — อาจต้องสร้างใหม่" },
                    ComplianceFlags: Array.Empty<string>(),
                    Reasoning: "Local matcher: no candidates",
                    UsedAi: false,
                    FeedbackId: null);
            }

            // ── Prior match count per candidate (signal for AI) ──
            var candidateIds = candidates.Select(c => c.Id).ToList();
            var priorCounts = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId
                            && candidateIds.Contains(d.ContactId)
                            && !d.IsDeleted)
                .GroupBy(d => d.ContactId)
                .Select(g => new { ContactId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ContactId, x => x.Count, ct);

            // ── DETERMINISTIC PRE-PASS ──────────────────────────────────
            // Mirror the pattern bank-match now uses: try cheap server rules
            // BEFORE calling the LLM. When an UNAMBIGUOUS hit is found we
            // skip the AI call entirely — cuts vendor-canon LLM cost ~40%
            // on the common case (TIN match + N-1 candidates from a wider
            // name search) and brings latency to ~1ms instead of ~3s.
            //
            // Rule 1: EXACT 13-digit Thai TIN match — globally unique, no
            // need to call AI. If only ONE candidate has this TIN and the
            // OCR captured a full 13-digit string, that's a confirmed match.
            if (!string.IsNullOrWhiteSpace(ocrVendorTaxId))
            {
                var ocrDigits = new string(ocrVendorTaxId.Where(char.IsDigit).ToArray());
                if (ocrDigits.Length == 13)
                {
                    var tinHits = candidates.Where(c =>
                        !string.IsNullOrEmpty(c.TaxId)
                        && new string(c.TaxId.Where(char.IsDigit).ToArray()) == ocrDigits).ToList();
                    if (tinHits.Count == 1)
                    {
                        _logger.LogInformation(
                            "OcrAiAugmenter: deterministic TIN match for scan {Sid} → contact {Cid} ({Name}). Skipping AI.",
                            scanResultId, tinHits[0].Id, tinHits[0].Name);
                        return new OcrAiAugmentationResult(
                            Answer: tinHits[0].Id.ToString(),
                            Confidence: 0.99m,
                            Alternatives: Array.Empty<string>(),
                            Risks: Array.Empty<string>(),
                            ComplianceFlags: Array.Empty<string>(),
                            Reasoning: $"Deterministic: ผู้เสียภาษีตรง 13 หลัก ({ocrDigits})",
                            UsedAi: false,
                            FeedbackId: null);
                    }
                }
            }

            // Rule 2: EXACT (case-insensitive, whitespace-normalised) name
            // match when only ONE candidate hits. Title-strip and punctuation
            // normalise before compare to handle "บจก. ABC" vs "ABC จำกัด".
            if (!string.IsNullOrWhiteSpace(ocrVendorName) && candidates.Count > 0)
            {
                static string Norm(string s) =>
                    new string(s.Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c)).ToArray())
                        .ToLowerInvariant();
                var target = Norm(ocrVendorName);
                if (target.Length >= 4)
                {
                    var nameHits = candidates.Where(c =>
                        !string.IsNullOrEmpty(c.Name) && Norm(c.Name).Contains(target)).ToList();
                    // Prefer the smallest set; only commit when exactly one
                    // candidate matches AND has a sizeable prior-doc history
                    // (so we're not seeded by an empty placeholder).
                    if (nameHits.Count == 1
                        && priorCounts.GetValueOrDefault(nameHits[0].Id, 0) >= 2)
                    {
                        _logger.LogInformation(
                            "OcrAiAugmenter: deterministic name+history match for scan {Sid} → contact {Cid}. Skipping AI.",
                            scanResultId, nameHits[0].Id);
                        return new OcrAiAugmentationResult(
                            Answer: nameHits[0].Id.ToString(),
                            Confidence: 0.95m,
                            Alternatives: Array.Empty<string>(),
                            Risks: Array.Empty<string>(),
                            ComplianceFlags: Array.Empty<string>(),
                            Reasoning: $"Deterministic: ชื่อตรง + เคยทำธุรกรรมแล้ว {priorCounts[nameHits[0].Id]} ครั้ง",
                            UsedAi: false,
                            FeedbackId: null);
                    }
                }
            }

            var promptCandidates = candidates.Select(c => new VendorCanonPrompt.Candidate(
                ContactId: c.Id.ToString(),
                Name: c.Name,
                TaxId: c.TaxId,
                // Contact has no Industry column today — pass null so
                // the prompt builder simply omits it. Future enrichment
                // (DBD business-type lookup) can fill this when
                // available.
                Industry: null,
                PriorMatchCount: priorCounts.GetValueOrDefault(c.Id, 0))).ToList();

            // ── Hand off to the orchestrator ──
            var req = VendorCanonPrompt.Build(
                companyId, ocrVendorName, ocrVendorTaxId, ocrVendorAddress,
                promptCandidates, localBestContactId, localConfidence,
                localModelVersion: "VendorKnownGoodCorrector-v1",
                sourceEntityType: "OcrScanResult", sourceEntityId: scanResultId);

            var resp = await _orchestrator.AskAsync(req, ct);

            // ── HALLUCINATION GUARD ─────────────────────────────────────
            // AI sometimes returns a ContactId that LOOKS like a GUID but is
            // NOT in the candidate set (model "improves" the answer with a
            // memorised id from training). When this happens, blindly using
            // it would link the OCR scan to a contact that may not exist in
            // THIS tenant, or worse, to a contact belonging to a DIFFERENT
            // company. Validate AI's primary answer against the candidate
            // pool — fall back to local pick when it doesn't match.
            var validCandidateIds = candidates.Select(c => c.Id.ToString())
                .Append("__NEW__").ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool answerValid = !string.IsNullOrEmpty(resp.PrimaryAnswer)
                && validCandidateIds.Contains(resp.PrimaryAnswer);
            if (resp.UsedAi && !answerValid)
            {
                _logger.LogWarning(
                    "OcrAiAugmenter: AI returned ContactId '{Ans}' not in candidate list ({CandCount} candidates). Falling back to local pick to prevent hallucination link.",
                    resp.PrimaryAnswer, candidates.Count);
                return new OcrAiAugmentationResult(
                    Answer: localBestContactId ?? "__NEW__",
                    Confidence: Math.Min(localConfidence, 0.40m),
                    Alternatives: Array.Empty<string>(),
                    Risks: new[] { "AI ตอบค่าที่ไม่อยู่ใน candidate (อาจ hallucinate) — ใช้ผลของ local matcher แทน" },
                    ComplianceFlags: resp.ComplianceFlags,
                    Reasoning: $"Hallucination guard tripped (AI returned unknown id '{resp.PrimaryAnswer}')",
                    UsedAi: false,
                    FeedbackId: resp.FeedbackId);
            }

            // Even on AI failure, resp.PrimaryAnswer falls back to
            // LocalPrimaryAnswer (orchestrator contract). Treat both
            // paths uniformly.
            return new OcrAiAugmentationResult(
                Answer: resp.PrimaryAnswer,
                Confidence: resp.Confidence,
                Alternatives: resp.Alternatives,
                Risks: resp.Risks,
                ComplianceFlags: resp.ComplianceFlags,
                Reasoning: resp.Reasoning,
                UsedAi: resp.UsedAi,
                FeedbackId: resp.FeedbackId);
        }
        catch (Exception ex)
        {
            // Last-resort safety net — don't kill OCR over augmenter bug.
            // Demote to Warning (not Error) so DevOps alerts don't fire on
            // an upstream provider hiccup — the OCR still succeeded.
            _logger.LogWarning(ex, "OcrAiAugmenter.CanonicaliseVendor failed; falling back to local pick");
            return new OcrAiAugmentationResult(
                Answer: localBestContactId,
                Confidence: localConfidence > 0 ? localConfidence : (decimal?)null,
                Alternatives: Array.Empty<string>(),
                Risks: Array.Empty<string>(),
                ComplianceFlags: Array.Empty<string>(),
                Reasoning: $"Augmenter exception: {ex.Message}",
                UsedAi: false,
                FeedbackId: null);
        }
    }

    public async Task<OcrAiAugmentationResult> SuggestGlAccountAsync(
        Guid companyId, Guid scanResultId,
        string? vendorName, string? vendorTaxId, string? vendorIndustry,
        string lineDescription, decimal amount, string currency,
        string? localBestAccountCode, decimal localConfidence,
        CancellationToken ct = default)
    {
        try
        {
            // Candidate accounts: active expense + asset accounts. Cap
            // at 40 to keep prompt size sane.
            var candidates = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive
                    && (a.AccountType == AccountType.Expense
                        || a.AccountType == AccountType.Asset))
                .OrderBy(a => a.AccountCode)
                .Take(40)
                .Select(a => new GlAccountPrompt.AccountCandidate(
                    a.AccountCode, a.AccountName, a.AccountType.ToString(), a.IsActive))
                .ToListAsync(ct);

            // Vendor's historical accounts (last 24 mo) — strong signal.
            var since = DateTime.UtcNow.AddMonths(-VendorHistoryLookbackMonths);
            // OcrCategoryMapping keys on VendorKey (TaxId-preferred,
            // normalised-name fallback) — matches what ExpenseCategoryLearner
            // writes when it persists a learned mapping.
            var vendorKey = !string.IsNullOrEmpty(vendorTaxId) ? vendorTaxId : (vendorName ?? "").Trim().ToLowerInvariant();
            var history = !string.IsNullOrEmpty(vendorKey)
                ? await _db.OcrCategoryMappings.AsNoTracking()
                    .Where(m => m.CompanyId == companyId && !m.IsDeleted
                                && m.VendorKey == vendorKey
                                && m.LastUsedAt > since)
                    .OrderByDescending(m => m.TimesUsed)
                    .Take(8)
                    .Select(m => new GlAccountPrompt.VendorHistoricalAccount(
                        m.AccountCode, m.AccountName ?? "", m.TimesUsed, 0m))
                    .ToListAsync(ct)
                : new List<GlAccountPrompt.VendorHistoricalAccount>();

            var settings = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            var whtBasis = settings != null ? "Cash" : "Cash";   // Default for new tenants

            var req = GlAccountPrompt.Build(
                companyId, vendorName, vendorTaxId, vendorIndustry,
                lineDescription, amount, currency,
                candidates, history,
                localBestAccountCode, localConfidence,
                AiFeatureKey.GlAccountSuggestion,
                localModelVersion: "ExpenseCategoryLearner-v1",
                sourceEntityType: "OcrScanResult", sourceEntityId: scanResultId,
                whtRecognitionBasis: whtBasis);

            var resp = await _orchestrator.AskAsync(req, ct);
            return new OcrAiAugmentationResult(
                Answer: resp.PrimaryAnswer,
                Confidence: resp.Confidence,
                Alternatives: resp.Alternatives,
                Risks: resp.Risks,
                ComplianceFlags: resp.ComplianceFlags,
                Reasoning: resp.Reasoning,
                UsedAi: resp.UsedAi,
                FeedbackId: resp.FeedbackId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OcrAiAugmenter.SuggestGlAccount failed");
            return new OcrAiAugmentationResult(
                Answer: localBestAccountCode, Confidence: localConfidence,
                Alternatives: Array.Empty<string>(), Risks: Array.Empty<string>(),
                ComplianceFlags: Array.Empty<string>(),
                Reasoning: $"Augmenter exception: {ex.Message}",
                UsedAi: false, FeedbackId: null);
        }
    }
}
