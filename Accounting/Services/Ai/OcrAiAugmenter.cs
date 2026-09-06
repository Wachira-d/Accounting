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

/// <summary>
/// บริบทที่ตัวจำแนกชนิดเอกสารต้องเห็นเพื่อจะตอบได้จริง
///
/// <para>คำถามหลักคือ "เราเป็นผู้ซื้อหรือผู้ขาย" ซึ่งตอบไม่ได้เลยถ้าไม่รู้ว่า
/// เลข/ชื่อบนกระดาษฝั่งไหนคือเรา — payload เดิมส่งแค่ข้อความ + ชื่อผู้ขาย +
/// ยอดรวม ⇒ โมเดลต้องเดา แล้วผู้เรียกก็ทิ้งคำตอบที่ข้ามฝั่ง (จ่าย token
/// แล้วโยนทิ้ง)</para>
/// </summary>
public sealed record OcrDocTypeContext(
    string? OurRole = null,
    decimal? RoleConfidence = null,
    string? ScannedDocumentType = null,
    string? VendorTaxId = null,
    string? BuyerName = null,
    string? BuyerTaxId = null,
    DateTime? DocumentDate = null,
    decimal? VatAmount = null,
    IReadOnlyList<string>? TopLineDescriptions = null);

/// <summary>
/// บริบทของบรรทัดที่กฎในพรอมป์ต์จัดผังบัญชีต้องใช้ แต่เดิมไม่เคยถูกส่ง
///
/// <para>กฎข้อ 8(b) ของ <c>GlAccountPrompt</c> ทั้งข้อพูดเรื่อง<b>หน่วยนับ</b>
/// ("L/ลิตร → น้ำมัน · kWh → ค่าไฟ · ลบ.ม. → ค่าน้ำ") แต่ payload มีแค่
/// {description, amount, currency} ⇒ กฎที่พรอมป์ต์พึ่งมากที่สุดไม่มีอินพุตให้ใช้
/// ทั้งที่ <c>ExtractedItemsJson</c> เก็บค่าเหล่านี้ไว้อยู่แล้ว</para>
/// </summary>
public sealed record GlLineContext(
    string? Unit = null,
    decimal? Quantity = null,
    decimal? UnitPrice = null,
    DateTime? DocumentDate = null,
    string? OurRole = null,
    bool? InputVatClaimable = null);

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
    /// จำแนกชนิดเอกสารจากกระดาษ — เรียกเฉพาะตอนกติกาไม่มั่นใจ
    /// (ดูรายละเอียดที่ implementation)
    /// </summary>
    Task<OcrAiAugmentationResult> ClassifyDocumentTypeAsync(
        Guid companyId, Guid scanResultId,
        string rawText, string? documentNumber, string? vendorName, decimal? totalAmount,
        string? localGuess, decimal localConfidence,
        OcrDocTypeContext? context = null,
        CancellationToken ct = default);

    /// <summary>
    /// Suggest GL account code for a single OCR line item.
    /// </summary>
    Task<OcrAiAugmentationResult> SuggestGlAccountAsync(
        Guid companyId, Guid scanResultId,
        string? vendorName, string? vendorTaxId, string? vendorIndustry,
        string lineDescription, decimal amount, string currency,
        string? localBestAccountCode, decimal localConfidence,
        GlLineContext? lineContext = null,
        CancellationToken ct = default);

    /// <summary>
    /// Match one OCR'd invoice line → the project it belongs to, using the
    /// candidate projects (each with sample ordered materials) derived from the
    /// external-system metadata. Only called by OcrMetadataProjectMatcher when
    /// the deterministic pass left the line unresolved AND ≥2 projects are in
    /// play. Answer is a Project id string (one of the candidates) or null.
    /// </summary>
    /// <summary>
    /// แตกรายการสินค้า/บริการจากข้อความดิบ เมื่อ engine ไม่คืนตารางมาให้เลย
    ///
    /// <para>คืน JSON ดิบของโมเดลผ่าน <c>Answer</c> — ผู้เรียกต้อง parse เอง
    /// และ<b>ต้องตรวจว่าผลรวมลงตัวกับยอดหัวกระดาษก่อนรับไปใช้</b>
    /// (ดู <c>OcrService.TrySplitLineItemsWithAiAsync</c>)</para>
    ///
    /// <para>AI ปิด/ล่ม → <c>UsedAi=false</c>, <c>Answer=null</c> ⇒ ผู้เรียก
    /// คงพฤติกรรมเดิม (บรรทัดสรุปใบเดียวจากยอดหัวกระดาษ) — kill-switch ผ่าน</para>
    /// </summary>
    Task<OcrAiAugmentationResult> SplitLineItemsAsync(
        Guid companyId, Guid scanResultId,
        string rawText, string? documentType, string? vendorName,
        decimal? subTotal, decimal? vatAmount, decimal? totalAmount,
        CancellationToken ct = default);

    Task<OcrAiAugmentationResult> MatchLineProjectAsync(
        Guid companyId, Guid scanResultId,
        string lineDescription, decimal? amount,
        IReadOnlyList<ProjectMatchCandidate> candidates,
        CancellationToken ct = default);
}

/// <summary>A candidate project the AI may assign an OCR line to — id + name +
/// a few sample materials that were ordered for it (the matching signal).</summary>
public sealed record ProjectMatchCandidate(
    string ProjectId,
    string ProjectName,
    string? ProjectCode,
    IReadOnlyList<string> SampleMaterials);

public sealed record OcrAiAugmentationResult(
    string? Answer,             // chosen value (AI's or local fallback)
    decimal? Confidence,
    IReadOnlyList<string> Alternatives,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> ComplianceFlags,
    string? Reasoning,
    bool UsedAi,
    Guid? FeedbackId,
    // ★ กฎเหล็ก #1 (ผลตรวจ 2026-09-05 T3-01): orchestrator short-circuit ด้วย
    // "นักเรียน" (local distillation model มั่นใจ ≥ threshold) คืน UsedAi=false
    // พร้อมคำตอบของนักเรียน — เดิม call site ทุกจุดมี `UsedAi &&` เป็นด่านแรก
    // ⇒ คำตอบนักเรียนถูกทิ้งทุกครั้ง = ระบบ "เรียนแล้วไม่เคยใช้ที่เรียน".
    // FromStudent=true แปลว่า Answer มาจากนักเรียนที่ผ่านเกณฑ์ routing แล้ว
    // — call site ใช้ได้เหมือนคำตอบ AI (ผ่าน anti-hallucination guard เดิม)
    // แต่ป้าย UI ต้องเป็น "⚙️ ระบบเรียนรู้แล้ว" ไม่ใช่ "🤖 AI"
    bool FromStudent = false)
{
    /// <summary>มีคำตอบที่ใช้ได้จาก "ครู" (AI) หรือ "นักเรียน" (local model) —
    /// ใช้แทน <c>UsedAi</c> ที่ call site เมื่อต้องการนำคำตอบไป apply</summary>
    public bool HasModelAnswer => (UsedAi || FromStudent) && !string.IsNullOrWhiteSpace(Answer);

    /// <summary>ป้ายแหล่งคำตอบสำหรับ reasoning trace</summary>
    public string SourceLabel => UsedAi ? "AI" : FromStudent ? "Student" : "Local";
}

public class OcrAiAugmenter : IOcrAiAugmenter
{
    /// <summary>คำตอบนี้มาจากนักเรียน (short-circuit ของ orchestrator) หรือไม่ —
    /// <see cref="AiOrchestrator"/> ตั้ง <c>ProviderModel = "local:{version}"</c> เฉพาะเส้นนี้</summary>
    internal static bool IsStudentAnswer(AiResponse resp) =>
        !resp.UsedAi
        && resp.Status == AiCallStatus.Skipped
        && !string.IsNullOrEmpty(resp.PrimaryAnswer)
        && resp.ProviderModel?.StartsWith("local:", StringComparison.Ordinal) == true;

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
                FeedbackId: resp.FeedbackId,
                FromStudent: IsStudentAnswer(resp));
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
        GlLineContext? lineContext = null,
        CancellationToken ct = default)
    {
        try
        {
            // Candidate accounts — เรียงตามที่บริษัทใช้ล่าสุด/บ่อย + ส่ง
            // Description + ครอบ Expense+Asset ครบ (แก้ bug เดิมที่ตัดบัญชี
            // 5xxxx ค่าใช้จ่ายทิ้งเพราะ OrderBy(code).Take(40)).
            var candRows = await GlCandidateBuilder.LoadAsync(
                // ⚠️ เดิม hardcode true = ส่งเฉพาะผังรายจ่าย+สินทรัพย์เสมอ
                // แต่ IntegrationService เรียกเมธอดนี้จาก ProcessInvoiceAsync
                // ซึ่งสร้าง **ใบกำกับภาษีขาย** ⇒ ผังรายได้ (4xxxx) ไม่เคยอยู่ใน
                // candidate_accounts เลย และกฎข้อ 5 ของพรอมป์ต์บังคับว่า
                // "ต้องเลือกจาก candidate_accounts เท่านั้น" ⇒ บรรทัดฝั่งขาย
                // **ไม่มีทางถูกลงเป็นรายได้ได้เลยโดยโครงสร้าง**
                _db, companyId,
                expenseAssetOnly: !string.Equals(lineContext?.OurRole, "Seller", StringComparison.OrdinalIgnoreCase),
                cap: 150, ct);
            var candidates = candRows
                .Select(c => new GlAccountPrompt.AccountCandidate(
                    c.Code, c.Name, c.Type, c.IsActive, c.Description))
                .ToList();

            // ⭐ Deterministic Fixed-Asset/Supplies prior — กันเคสที่ AI
            // เคยพลาด: "เครื่องปริ้นท์" → 54420 ค่าวัสดุสิ้นเปลือง (ผิด!).
            // ตรวจ keyword durable goods → bias ไปทาง 12xxx (Fixed Asset)
            // และ keyword consumables → 5xxxx. ตั้งเป็น hint local เพื่อให้
            // orchestrator short-circuit ที่ ≥0.85 confidence (ไม่ต้องเรียก AI).
            // ถ้าผัง 12xxx ไม่มีจริงในผังบริษัท → ตกไปใช้ AI ตามเดิม.
            var (priorCode, priorConf) = DurableGoodsHeuristic.Predict(
                lineDescription, amount, candidates);
            if (priorCode != null && (localBestAccountCode == null || localConfidence < priorConf))
            {
                localBestAccountCode = priorCode;
                localConfidence = priorConf;
            }

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

            // โหลด business context (ชื่อบริษัท, ประเภทธุรกิจ, industry, top
            // accounts ใช้บ่อย) — AI ใช้ตัดสินใจตามสายธุรกิจ (โรงแรม vs
            // ร้านอาหาร vs ที่ปรึกษา ผังไม่เหมือนกัน). กฎเหล็ก #1: ส่งบริบท
            // ที่เกี่ยวข้องให้ครบที่สุด.
            var bizCtx = await Accounting.Services.Ai.Prompts.CompanyBusinessContextLoader
                .LoadAsync(_db, companyId, ct);

            var req = GlAccountPrompt.Build(
                companyId, vendorName, vendorTaxId, vendorIndustry,
                lineDescription, amount, currency,
                candidates, history,
                localBestAccountCode, localConfidence,
                AiFeatureKey.GlAccountSuggestion,
                localModelVersion: "ExpenseCategoryLearner-v1",
                sourceEntityType: "OcrScanResult", sourceEntityId: scanResultId,
                whtRecognitionBasis: whtBasis,
                businessContext: bizCtx,
                // บริบทของบรรทัดที่กฎ decode ในพรอมป์ต์ต้องใช้ (หน่วย/จำนวน/
                // ราคาต่อหน่วย/วันที่/ฝั่ง/สิทธิเคลม VAT) — เดิมไม่เคยส่งเลย
                lineUnit: lineContext?.Unit,
                lineQuantity: lineContext?.Quantity,
                lineUnitPrice: lineContext?.UnitPrice,
                documentDate: lineContext?.DocumentDate,
                ourRole: lineContext?.OurRole,
                inputVatClaimable: lineContext?.InputVatClaimable);

            var resp = await _orchestrator.AskAsync(req, ct);
            return new OcrAiAugmentationResult(
                Answer: resp.PrimaryAnswer,
                Confidence: resp.Confidence,
                Alternatives: resp.Alternatives,
                Risks: resp.Risks,
                ComplianceFlags: resp.ComplianceFlags,
                Reasoning: resp.Reasoning,
                UsedAi: resp.UsedAi,
                FeedbackId: resp.FeedbackId,
                FromStudent: IsStudentAnswer(resp));
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

    /// <summary>
    /// "กระดาษใบนี้คือเอกสารชนิดไหน" — เรียกเฉพาะตอน<b>กติกาไม่มั่นใจ</b>
    ///
    /// ═══ ทำไมถึงเพิ่งมาต่อสาย ═══
    /// <c>AiFeatureKey.DocumentTypeClassification</c> (#3) มีครบทุกอย่างมาแล้ว
    /// — enum, prompt (<c>DocumentTypeClassifyPrompt</c>), และ student ที่
    /// register ไว้ใน Program.cs — <b>แต่ไม่เคยมีใครเรียกเลยสักครั้ง</b>
    /// ⇒ prompt ตายอยู่ในไฟล์ และ student อดอาหารถาวร (ไม่มี feedback row
    /// เกิดขึ้นเลย จึงไม่มีวัน IsReady)
    ///
    /// การจำแนกชนิดเอกสารคือคำถามที่ <b>พลาดแล้วแพงที่สุด</b> ในทั้งไปป์ไลน์ —
    /// ผิดชนิด = บัญชีคู่ผิดทั้งใบ + เข้ารายงานภาษีผิดฝั่ง จึงคุ้มที่จะจ่าย
    /// token เฉพาะเคสที่กติกาเดาไม่ลง
    ///
    /// ═══ ด่านกันมั่ว (anti-hallucination) ═══
    /// คำตอบต้องเป็นค่าใน <c>DocumentType</c> จริง และต้อง<b>อยู่ฝั่งเดียวกับ
    /// บทบาทที่ยืนยันแล้ว</b> — AI มองไม่เห็นว่าเราเป็นผู้ซื้อหรือผู้ขาย
    /// ผู้เรียกจึงต้องกรองอีกชั้น (ดู DocumentSide.MatchesRole)
    /// </summary>
    public async Task<OcrAiAugmentationResult> ClassifyDocumentTypeAsync(
        Guid companyId, Guid scanResultId,
        string rawText, string? documentNumber, string? vendorName, decimal? totalAmount,
        string? localGuess, decimal localConfidence,
        OcrDocTypeContext? context = null,
        CancellationToken ct = default)
    {
        try
        {
            // "เราคือใคร" ต้องมาจากฐานข้อมูล ไม่ใช่จากกระดาษ — ผู้เรียกอาจไม่มี
            // ค่านี้ในมือ จึงโหลดเองที่นี่เพื่อให้ทุก call site ได้บริบทครบเท่ากัน
            var me = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId)
                .Select(c => new { c.Name, c.TaxId })
                .FirstOrDefaultAsync(ct);

            var req = Prompts.DocumentTypeClassifyPrompt.Build(
                companyId, scanResultId,
                rawTextSample: rawText ?? "",
                extractedDocNumber: documentNumber,
                extractedVendorName: vendorName,
                extractedTotal: totalAmount,
                localGuess: localGuess,
                localConfidence: localConfidence,
                ourCompanyName: me?.Name,
                ourTaxId: me?.TaxId,
                ourRoleFromRules: context?.OurRole,
                roleConfidence: context?.RoleConfidence,
                vendorTaxId: context?.VendorTaxId,
                buyerName: context?.BuyerName,
                buyerTaxId: context?.BuyerTaxId,
                documentDate: context?.DocumentDate,
                vatAmount: context?.VatAmount,
                topLineDescriptions: context?.TopLineDescriptions,
                scannedTypeFromRules: context?.ScannedDocumentType);

            var resp = await _orchestrator.AskAsync(req, ct);
            return new OcrAiAugmentationResult(
                Answer: resp.PrimaryAnswer,
                Confidence: resp.Confidence,
                Alternatives: resp.Alternatives,
                Risks: resp.Risks,
                ComplianceFlags: resp.ComplianceFlags,
                Reasoning: resp.Reasoning,
                UsedAi: resp.UsedAi,
                FeedbackId: resp.FeedbackId,
                FromStudent: IsStudentAnswer(resp));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OcrAiAugmenter.ClassifyDocumentType failed");
            // AI ล้ม = ใช้คำตอบของกติกาต่อ (kill-switch: feature ยังทำงานครบ)
            return new OcrAiAugmentationResult(
                Answer: localGuess, Confidence: localConfidence,
                Alternatives: Array.Empty<string>(), Risks: Array.Empty<string>(),
                ComplianceFlags: Array.Empty<string>(),
                Reasoning: $"Augmenter exception: {ex.Message}",
                UsedAi: false, FeedbackId: null);
        }
    }

    public async Task<OcrAiAugmentationResult> MatchLineProjectAsync(
        Guid companyId, Guid scanResultId,
        string lineDescription, decimal? amount,
        IReadOnlyList<ProjectMatchCandidate> candidates,
        CancellationToken ct = default)
    {
        // Nothing to disambiguate with fewer than two candidates.
        if (candidates == null || candidates.Count < 2)
            return Empty("no candidates");
        try
        {
            var req = ProjectMatchPrompt.Build(companyId, scanResultId, lineDescription, amount, candidates);
            var resp = await _orchestrator.AskAsync(req, ct);

            // Hallucination guard — AI must return one of the candidate ids.
            var valid = candidates.Select(c => c.ProjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var projFromStudent = IsStudentAnswer(resp);
            if (!(resp.UsedAi || projFromStudent) || string.IsNullOrEmpty(resp.PrimaryAnswer) || !valid.Contains(resp.PrimaryAnswer))
            {
                if (resp.UsedAi || projFromStudent)
                    _logger.LogWarning(
                        "OcrAiAugmenter.MatchLineProject: AI returned project '{Ans}' not in candidates ({N}). Leaving line unassigned.",
                        resp.PrimaryAnswer, candidates.Count);
                return Empty("AI gave no usable project");
            }

            return new OcrAiAugmentationResult(
                Answer: resp.PrimaryAnswer,
                Confidence: resp.Confidence,
                Alternatives: resp.Alternatives,
                Risks: resp.Risks,
                ComplianceFlags: resp.ComplianceFlags,
                Reasoning: resp.Reasoning,
                UsedAi: resp.UsedAi,
                FeedbackId: resp.FeedbackId,
                FromStudent: projFromStudent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OcrAiAugmenter.MatchLineProject failed");
            return Empty($"exception: {ex.Message}");
        }

        static OcrAiAugmentationResult Empty(string why) => new(
            Answer: null, Confidence: null,
            Alternatives: Array.Empty<string>(), Risks: Array.Empty<string>(),
            ComplianceFlags: Array.Empty<string>(),
            Reasoning: why, UsedAi: false, FeedbackId: null);
    }

    public async Task<OcrAiAugmentationResult> SplitLineItemsAsync(
        Guid companyId, Guid scanResultId,
        string rawText, string? documentType, string? vendorName,
        decimal? subTotal, decimal? vatAmount, decimal? totalAmount,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return Empty("no raw text");
        // ไม่มียอดให้ตรวจสอบผลลัพธ์ = ไม่มีทางรู้ว่าที่ AI แตกมาถูกไหม → ไม่เรียก
        if (subTotal is not > 0m && totalAmount is not > 0m) return Empty("no totals to reconcile against");

        try
        {
            var req = Prompts.OcrLineSplitPrompt.Build(
                companyId, scanResultId, rawText, documentType, vendorName,
                subTotal, vatAmount, totalAmount);
            var resp = await _orchestrator.AskAsync(req, ct);
            var splitFromStudent = IsStudentAnswer(resp) && !string.IsNullOrWhiteSpace(resp.RawResponseJson);
            if (!(resp.UsedAi || splitFromStudent) || string.IsNullOrWhiteSpace(resp.RawResponseJson))
                return Empty("ai unavailable");

            return new OcrAiAugmentationResult(
                Answer: resp.RawResponseJson,
                Confidence: resp.Confidence,
                Alternatives: resp.Alternatives,
                Risks: resp.Risks,
                ComplianceFlags: resp.ComplianceFlags,
                Reasoning: resp.Reasoning,
                UsedAi: resp.UsedAi,
                FeedbackId: resp.FeedbackId,
                FromStudent: splitFromStudent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OcrAiAugmenter.SplitLineItems failed");
            return Empty($"exception: {ex.Message}");
        }

        static OcrAiAugmentationResult Empty(string why) => new(
            Answer: null, Confidence: null,
            Alternatives: Array.Empty<string>(), Risks: Array.Empty<string>(),
            ComplianceFlags: Array.Empty<string>(),
            Reasoning: why, UsedAi: false, FeedbackId: null);
    }
}
