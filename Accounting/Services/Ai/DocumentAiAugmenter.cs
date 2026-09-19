using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai.Prompts;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// AI augmentation for document-workflow decisions:
///   • SuggestApprovalWarningFixAsync — turn a soft-warn into a
///     specific action recommendation.
///   • ClassifyCreditNoteReasonAsync — pick Return/Discount/Adjustment/
///     Writeoff per ประมวลรัษฎากร §82/10.
///   • InferWhtCategoryAsync — pick the right WHT revenue code + rate.
///   • SuggestPaymentVoucherAccountingAsync — the user's explicit
///     example: เลือกผังบัญชีตอนสร้างใบสำคัญจ่ายจากใบกำกับภาษี.
///
/// Same safety contract as OcrAiAugmenter — every method returns a
/// usable result even when AI is down. Outer try/catch in each method;
/// orchestrator's safety net is a second line of defence.
/// </summary>
public interface IDocumentAiAugmenter
{
    Task<DocumentAiSuggestion> SuggestApprovalWarningFixAsync(
        Guid companyId, Guid documentId, string warningText,
        object documentSnapshot, object? vendorHistory,
        CancellationToken ct = default);

    Task<DocumentAiSuggestion> ClassifyCreditNoteReasonAsync(
        Guid companyId, Guid creditNoteId,
        object creditNoteSnapshot, object? originalInvoice,
        string? localGuess, decimal? localConfidence,
        CancellationToken ct = default);

    Task<DocumentAiSuggestion> InferWhtCategoryAsync(
        Guid companyId, Guid? documentId,
        string? vendorName, string? vendorTaxId, string? vendorType,
        string lineDescription, decimal amount,
        string? localGuess, decimal? localConfidence,
        CancellationToken ct = default);

    /// <summary>
    /// Pick GL accounts for a Payment Voucher being created from a Tax
    /// Invoice. AI sees the invoice + vendor history + the tenant's
    /// chart of accounts + Thai WHT/VAT booking rules, and proposes
    /// the debit-side accounts for each line. This is the call site
    /// the user specifically asked for.
    /// </summary>
    Task<DocumentAiSuggestion> SuggestPaymentVoucherAccountingAsync(
        Guid companyId, Guid sourceInvoiceId,
        string? vendorName, string? vendorTaxId, string? vendorIndustry,
        string lineDescription, decimal amount, string currency,
        string? localBestAccountCode, decimal? localConfidence,
        CancellationToken ct = default);

    /// <summary>Bulk variant — one AI call covers every line of the PV.
    /// AI sees the full source invoice + every line + cross-line
    /// patterns (cluster detection, odd-line-out) so accuracy beats the
    /// per-line fan-out AND cost drops to 1× regardless of line count.
    /// Returns one DocumentAiSuggestion per input lineId.</summary>
    Task<BulkPvAccountingResult> SuggestAllPaymentVoucherAccountingAsync(
        Guid companyId, Guid sourceInvoiceId,
        string? vendorName, string? vendorTaxId, string? vendorIndustry,
        IReadOnlyList<(Guid LineId, string Description, decimal Amount, string? CurrentAccountCode)> lines,
        string currency,
        CancellationToken ct = default);

    /// <summary>Bulk variant for ApprovalWarningFix — one call for the
    /// whole warning set so AI can detect "warning 1 and warning 2 have
    /// the same root cause" patterns the per-warning loop can't see.</summary>
    Task<BulkApprovalWarningFixResult> SuggestApprovalWarningFixesBulkAsync(
        Guid companyId, Guid documentId,
        IReadOnlyList<string> warnings,
        object documentSnapshot, object? vendorHistory,
        CancellationToken ct = default);

    /// <summary>Probe for a near-duplicate of the supplied draft —
    /// routes through the orchestrator using FuzzyDuplicateDetection
    /// so the local DuplicateDocumentDistillationModel runs first and
    /// short-circuits the call when its cosine + amount + date score
    /// exceeds the 0.70 floor. Returns null when no duplicate is
    /// likely. Caller surfaces the warning in the save UI.</summary>
    Task<DocumentAiSuggestion?> CheckDuplicateAsync(Guid companyId,
        Guid contactId, decimal amount, DateTime documentDate,
        string? subject, CancellationToken ct = default);
}

public sealed record BulkPvAccountingResult(
    IReadOnlyDictionary<Guid, DocumentAiSuggestion> ByLineId,
    IReadOnlyList<string> CrossLineObservations,
    IReadOnlyList<string> Warnings,
    bool UsedAi);

public sealed record BulkApprovalWarningFixResult(
    IReadOnlyList<DocumentAiSuggestion> Hints,    // index-aligned with input warnings
    string? RootCause,                            // AI-detected common root cause across warnings
    IReadOnlyList<string> Warnings,
    bool UsedAi);

public sealed record DocumentAiSuggestion(
    string? Answer,
    decimal? Confidence,
    IReadOnlyList<string> Alternatives,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> ComplianceFlags,
    string? Reasoning,
    IReadOnlyList<string> SuggestedActions,
    bool UsedAi,
    Guid? FeedbackId);

public class DocumentAiAugmenter : IDocumentAiAugmenter
{
    private readonly AccountingDbContext _db;
    private readonly IAiOrchestrator _orchestrator;
    private readonly IAiFeedbackRecorder _recorder;
    private readonly ILogger<DocumentAiAugmenter> _logger;

    public DocumentAiAugmenter(AccountingDbContext db, IAiOrchestrator orchestrator,
        IAiFeedbackRecorder recorder,
        ILogger<DocumentAiAugmenter> logger)
    { _db = db; _orchestrator = orchestrator; _recorder = recorder; _logger = logger; }

    // ────────────────────────────────────────────────────────────────
    //  Per-line / per-warning feedback synthesis
    //  ─────────────────────────────────────────
    //  Bulk AI calls return ONE FeedbackId from the orchestrator
    //  because the orchestrator records ONE row per provider call.
    //  But distillation models learn per-(vendor, line-keyword) or
    //  per-(warning template) — they need ONE feedback row per child
    //  item with the SINGLE-ITEM prompt shape they know how to parse.
    //
    //  We synthesise those child rows immediately after parsing the
    //  bulk response, linking each back to the parent via
    //  CacheHitOfFeedbackId so the cost stays attributed to the
    //  parent (child rows have LatencyMs=0, Cost=0). The UI then
    //  calls RecordUserChoice with the CHILD's FeedbackId when the
    //  user accepts/edits a single line, and distillation training
    //  picks up the per-line label naturally.
    // ────────────────────────────────────────────────────────────────
    private async Task<Guid> SynthesiseChildFeedbackAsync(
        Guid companyId, AiFeatureKey feature,
        string syntheticPromptJson, string? aiAnswer, decimal? aiConfidence,
        string? sourceEntityType, Guid? sourceEntityId,
        Guid parentFeedbackId, CancellationToken ct)
    {
        var record = new AiFeedbackRecord(
            CompanyId: companyId, FeatureKey: feature,
            PromptHash: "", PromptJson: syntheticPromptJson,
            ResponseJson: null,
            AiPrimaryAnswer: aiAnswer, AiConfidence: aiConfidence,
            LocalModelAnswer: null, LocalModelConfidence: null, LocalModelVersion: null,
            SourceEntityType: sourceEntityType, SourceEntityId: sourceEntityId,
            Status: AiCallStatus.Success, ProviderUsed: AiProviderType.DeepSeek,
            ModelVersion: null,
            LatencyMs: 0, InputTokens: 0, OutputTokens: 0, CostUsd: 0m,
            CacheHitOfFeedbackId: parentFeedbackId == Guid.Empty ? null : parentFeedbackId,
            ErrorMessage: null);
        try { return await _recorder.RecordCallAsync(record, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Child feedback record failed for {Feature}", feature);
            return Guid.Empty;
        }
    }

    public async Task<DocumentAiSuggestion> SuggestApprovalWarningFixAsync(
        Guid companyId, Guid documentId, string warningText,
        object documentSnapshot, object? vendorHistory,
        CancellationToken ct = default)
    {
        try
        {
            // ⚠️ **ห้ามส่ง "Acknowledge" เป็นคำตอบของนักเรียน** (ผลตรวจ D1-11):
            // ตัวเรียกนี้ไม่มีนักเรียนอยู่ในมือ — `ApprovalWarningDistillationModel`
            // เป็นคนตอบผ่าน orchestrator เอง. การยัด localFix="Acknowledge" ลงไป
            // ทำให้แถว feedback บันทึก `LocalModelAnswer="Acknowledge"` ทุกครั้ง
            // (คำตอบที่ไม่มีใครคิด) แล้วงานกลางคืนเอาไปนับเป็นหลักฐานว่านักเรียน
            // "เคยตอบแบบนี้" ⇒ คลังเอียงไปทาง "ปล่อยผ่าน" ตั้งแต่ยังไม่มีข้อมูลจริง
            var req = ApprovalWarningFixPrompt.Build(
                companyId, documentId, warningText,
                documentSnapshot, vendorHistory,
                localFix: null, localConfidence: null);
            var resp = await _orchestrator.AskAsync(req, ct);
            return Convert(resp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Approval warning fix augmenter failed");
            // ไม่มีใครตอบ = **ไม่มีคำตอบ** (null) ไม่ใช่ "Acknowledge" ที่แต่งขึ้น
            return Fallback(localAnswer: null, confidence: null);
        }
    }

    public async Task<DocumentAiSuggestion> ClassifyCreditNoteReasonAsync(
        Guid companyId, Guid creditNoteId,
        object creditNoteSnapshot, object? originalInvoice,
        string? localGuess, decimal? localConfidence,
        CancellationToken ct = default)
    {
        try
        {
            var req = CreditNoteReasonPrompt.Build(
                companyId, creditNoteId, creditNoteSnapshot, originalInvoice,
                localGuess ?? "Adjustment", localConfidence ?? 0.40m);
            var resp = await _orchestrator.AskAsync(req, ct);
            return Convert(resp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CN reason augmenter failed");
            return Fallback(localGuess ?? "Adjustment", localConfidence ?? 0.40m);
        }
    }

    public async Task<DocumentAiSuggestion> InferWhtCategoryAsync(
        Guid companyId, Guid? documentId,
        string? vendorName, string? vendorTaxId, string? vendorType,
        string lineDescription, decimal amount,
        string? localGuess, decimal? localConfidence,
        CancellationToken ct = default)
    {
        try
        {
            // ── บริบทจริงจากฐานข้อมูล ──────────────────────────────────
            // เดิมเรียก Build() ด้วยพารามิเตอร์ 8 ตัวแรกเท่านั้น ⇒ ช่องที่
            // prompt เตรียมไว้ (ประวัติหักภาษีของผู้ขายรายนี้ · ยอดสะสมปีนี้ ·
            // ค่าเฉลี่ย 6 เดือน · บัญชีที่ลงบ่อย) **ว่างทุกครั้งตั้งแต่วันแรก**
            // — defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้"
            var (history, ytd, avg6, dominantAcct) =
                await LoadWhtVendorContextAsync(companyId, vendorTaxId, vendorName, ct);

            var req = WhtCategoryPrompt.Build(
                companyId, documentId, vendorName, vendorTaxId, vendorType,
                lineDescription, amount, localGuess, localConfidence,
                vendorWhtHistory: history,
                vendorIndustry: null,            // ไม่มีข้อมูลจริง — ห้ามแต่งค่า
                vendorAvg6Months: avg6,
                wht3ThresholdReached: ytd,
                vendorDominantGlAccount: dominantAcct,
                contractTotalKnown: false);
            var resp = await _orchestrator.AskAsync(req, ct);
            return Convert(resp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WHT category augmenter failed");
            return Fallback(localGuess, localConfidence);
        }
    }

    /// <summary>
    /// รวบรวมบริบทของผู้ขายสำหรับ WhtCategoryPrompt จากข้อมูลจริงในระบบ:
    ///   • ประวัติรหัสเงินได้/อัตราที่เคยหักให้ผู้ขายรายนี้ (50 ทวิ ย้อน 24 เดือน)
    ///   • ยอดจ่ายสะสมปีภาษีนี้ — ใช้ตัดสินด่าน ฿1,000 แบบสะสม (ท.ป.4/2528 ข้อ 12)
    ///   • ค่าเฉลี่ยยอดต่อครั้งย้อน 6 เดือน — ไว้ดูว่าครั้งนี้ผิดสเกลไหม
    ///   • บัญชีที่ลงให้ผู้ขายรายนี้บ่อยสุด — บอกลักษณะรายจ่าย
    /// ทุก query มี CompanyId == companyId. ล้มเหลว = คืนค่าว่าง ไม่ throw
    /// (AI ยังตอบได้จากคำอธิบายบรรทัดเหมือนเดิม).
    /// </summary>
    private async Task<(IReadOnlyList<WhtCategoryPrompt.VendorWhtHistory> History,
                       decimal? PaidThisYear, decimal? Avg6Months, string? DominantAccount)>
        LoadWhtVendorContextAsync(Guid companyId, string? vendorTaxId, string? vendorName,
            CancellationToken ct)
    {
        var empty = (IReadOnlyList<WhtCategoryPrompt.VendorWhtHistory>)
            Array.Empty<WhtCategoryPrompt.VendorWhtHistory>();
        try
        {
            var taxId = Accounting.Helpers.ThaiTaxId.Normalize(vendorTaxId);
            var nameKey = (vendorName ?? "").Trim();
            if (string.IsNullOrEmpty(taxId) && string.IsNullOrEmpty(nameKey))
                return (empty, null, null, null);

            // หา contact ของผู้ขาย — เลขผู้เสียภาษีชนะชื่อเสมอ (ชื่อซ้ำกันได้)
            var payeeQuery = _db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted);
            // TaxId ในฐานเก็บทั้งแบบมีขีดและไม่มี (แล้วแต่ทางเข้า) — เทียบทั้ง
            // ค่าที่ normalize แล้วและค่าดิบที่ผู้เรียกส่งมา
            var rawTaxId = (vendorTaxId ?? "").Trim();
            payeeQuery = !string.IsNullOrEmpty(taxId)
                ? payeeQuery.Where(c => c.TaxId == taxId || c.TaxId == rawTaxId)
                : payeeQuery.Where(c => c.Name == nameKey);
            var payeeIds = await payeeQuery.Select(c => c.Id).ToListAsync(ct);

            decimal? paidThisYear = null, avg6 = null;
            var history = empty;

            if (payeeIds.Count > 0)
            {
                var since24 = DateTime.UtcNow.AddMonths(-24);
                var lines = await (from l in _db.WithholdingTaxCertLines.AsNoTracking()
                                   join c in _db.WithholdingTaxCerts.AsNoTracking()
                                        on l.WithholdingTaxCertId equals c.Id
                                   where c.CompanyId == companyId && !c.IsDeleted
                                         && payeeIds.Contains(c.PayeeContactId)
                                         && c.Status != Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus.Voided
                                         && l.PaymentDate >= since24
                                   select new { l.IncomeTypeCode, l.TaxRate, l.IncomeAmount, l.PaymentDate })
                                  .ToListAsync(ct);

                if (lines.Count > 0)
                {
                    history = lines
                        .GroupBy(l => new { l.IncomeTypeCode, l.TaxRate })
                        .OrderByDescending(g => g.Count())
                        .Take(5)
                        .Select(g => new WhtCategoryPrompt.VendorWhtHistory(
                            g.Key.IncomeTypeCode,
                            g.Key.TaxRate,
                            g.Count(),
                            Math.Round(g.Average(x => x.IncomeAmount), 2, MidpointRounding.AwayFromZero),
                            g.Max(x => x.PaymentDate)))
                        .ToList();

                    // ปีภาษีไทย = ปีปฏิทิน (ค.ศ.) ตามวันที่จ่าย — ด่าน ฿1,000 สะสม
                    var yearStart = new DateTime(DateTime.UtcNow.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    var thisYear = lines.Where(l => l.PaymentDate >= yearStart).ToList();
                    if (thisYear.Count > 0)
                        paidThisYear = Math.Round(thisYear.Sum(l => l.IncomeAmount), 2,
                            MidpointRounding.AwayFromZero);

                    var since6 = DateTime.UtcNow.AddMonths(-6);
                    var recent = lines.Where(l => l.PaymentDate >= since6).ToList();
                    if (recent.Count > 0)
                        avg6 = Math.Round(recent.Average(l => l.IncomeAmount), 2,
                            MidpointRounding.AwayFromZero);
                }
            }

            // บัญชีที่ลงให้ผู้ขายรายนี้บ่อยสุด — VendorKey ใช้กติกาเดียวกับ
            // SuggestPaymentVoucherAccountingAsync (เลขภาษี ถ้าไม่มีใช้ชื่อ lower)
            var vendorKey = !string.IsNullOrEmpty(rawTaxId) ? rawTaxId : nameKey.ToLowerInvariant();
            var dominant = await _db.OcrCategoryMappings.AsNoTracking()
                .Where(m => m.CompanyId == companyId && !m.IsDeleted && m.VendorKey == vendorKey)
                .OrderByDescending(m => m.TimesUsed)
                .Select(m => m.AccountCode + " " + (m.AccountName ?? ""))
                .FirstOrDefaultAsync(ct);

            return (history, paidThisYear, avg6, string.IsNullOrWhiteSpace(dominant) ? null : dominant.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "โหลดบริบท WHT ของผู้ขายไม่สำเร็จ — ส่ง prompt แบบไม่มีประวัติแทน");
            return (empty, null, null, null);
        }
    }

    public async Task<DocumentAiSuggestion> SuggestPaymentVoucherAccountingAsync(
        Guid companyId, Guid sourceInvoiceId,
        string? vendorName, string? vendorTaxId, string? vendorIndustry,
        string lineDescription, decimal amount, string currency,
        string? localBestAccountCode, decimal? localConfidence,
        CancellationToken ct = default)
    {
        try
        {
            // Candidate accounts — เรียงตามที่ใช้ล่าสุด + Description + ครบทุกหมวด
            var candRows0 = await Prompts.GlCandidateBuilder.LoadAsync(
                _db, companyId, expenseAssetOnly: false, cap: 150, ct);
            var candidates = candRows0
                .Select(c => new GlAccountPrompt.AccountCandidate(
                    c.Code, c.Name, c.Type, c.IsActive, c.Description))
                .ToList();

            var vendorKey = !string.IsNullOrEmpty(vendorTaxId)
                ? vendorTaxId
                : (vendorName ?? "").Trim().ToLowerInvariant();
            var since = DateTime.UtcNow.AddMonths(-24);
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

            // Source-invoice context — the user is paying ONE line of a
            // larger invoice. AI's job gets much easier when it can see
            // the OTHER lines (was the invoice mostly rent + a small
            // service fee? → service fee is probably 5306 not 5102), the
            // total + balance due (partial payment?), and whether WHT
            // was already withheld upstream (so we don't double-debit).
            var sourceContext = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == sourceInvoiceId)
                .Select(d => new
                {
                    d.DocumentNumber,
                    d.DocumentType,
                    d.TotalAmount,
                    d.BalanceDue,
                    d.WithholdingTaxAmount,
                    d.VatAmount,
                    OtherLines = d.Lines
                        .Where(l => !l.IsDeleted)
                        .Select(l => new {
                            l.Description, l.Amount, AccountCode = l.Account != null ? l.Account.AccountCode : null,
                        }).Take(6).ToList(),
                })
                .FirstOrDefaultAsync(ct);

            var enrichedDescription = sourceContext == null ? lineDescription
                : $"{lineDescription}\nSource invoice: {sourceContext.DocumentNumber} " +
                  $"(total {sourceContext.TotalAmount:N2}, balance {sourceContext.BalanceDue:N2}" +
                  (sourceContext.WithholdingTaxAmount > 0
                      ? $", WHT already {sourceContext.WithholdingTaxAmount:N2})"
                      : ")") +
                  (sourceContext.OtherLines.Count > 1
                      ? "\nSibling lines: " + string.Join("; ",
                          sourceContext.OtherLines.Take(5).Select(l =>
                              $"{l.Description} {l.Amount:N0}" +
                              (l.AccountCode != null ? $" → {l.AccountCode}" : "")))
                      : "");

            // โหลด business context — กฎเหล็ก #1: ส่งบริบทที่เกี่ยวข้องให้ครบ
            var bizCtx = await Prompts.CompanyBusinessContextLoader.LoadAsync(_db, companyId, ct);

            // Durable-goods/consumables deterministic prior — ดู
            // DurableGoodsHeuristic + GlAccountPrompt rule 7. เครื่องปริ้นท์
            // → 12210 (Asset) ไม่ใช่ 54420 (วัสดุสิ้นเปลือง)
            var (priorCode, priorConf) = DurableGoodsHeuristic.Predict(lineDescription, amount, candidates);
            if (priorCode != null && (localBestAccountCode == null || (localConfidence ?? 0m) < priorConf))
            {
                localBestAccountCode = priorCode;
                localConfidence = priorConf;
            }

            var req = GlAccountPrompt.Build(
                companyId, vendorName, vendorTaxId, vendorIndustry,
                enrichedDescription, amount, currency,
                candidates, history,
                localBestAccountCode, localConfidence,
                featureKey: AiFeatureKey.PaymentVoucherAccountingSuggestion,
                localModelVersion: "ExpenseCategoryLearner-v1",
                sourceEntityType: "Document", sourceEntityId: sourceInvoiceId,
                whtRecognitionBasis: "Cash",
                businessContext: bizCtx);

            var resp = await _orchestrator.AskAsync(req, ct);
            return Convert(resp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PV accounting augmenter failed");
            return Fallback(localBestAccountCode, localConfidence);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Bulk: PV accounting (every line in one call)
    // ────────────────────────────────────────────────────────────────
    public async Task<BulkPvAccountingResult> SuggestAllPaymentVoucherAccountingAsync(
        Guid companyId, Guid sourceInvoiceId,
        string? vendorName, string? vendorTaxId, string? vendorIndustry,
        IReadOnlyList<(Guid LineId, string Description, decimal Amount, string? CurrentAccountCode)> lines,
        string currency,
        CancellationToken ct = default)
    {
        if (lines.Count == 0)
            return new BulkPvAccountingResult(
                new Dictionary<Guid, DocumentAiSuggestion>(),
                Array.Empty<string>(), Array.Empty<string>(), false);

        try
        {
            // ทุกหมวด (PV อาจ Dr liability เช่น คืนเงินกู้กรรมการ) — เรียงตามที่
            // ใช้ล่าสุด + ส่ง Description (แก้ bug Take(80) ตัดบัญชีค่าใช้จ่ายทิ้ง)
            var candRows = await Prompts.GlCandidateBuilder.LoadAsync(
                _db, companyId, expenseAssetOnly: false, cap: 150, ct);
            var candidates = candRows
                .Select(c => new BulkPvAccountingPrompt.AccountCandidate(
                    c.Code, c.Name, c.Type, c.Description))
                .ToList();

            var vendorKey = !string.IsNullOrEmpty(vendorTaxId)
                ? vendorTaxId
                : (vendorName ?? "").Trim().ToLowerInvariant();
            var since = DateTime.UtcNow.AddMonths(-24);
            var history = !string.IsNullOrEmpty(vendorKey)
                ? await _db.OcrCategoryMappings.AsNoTracking()
                    .Where(m => m.CompanyId == companyId && !m.IsDeleted
                                && m.VendorKey == vendorKey && m.LastUsedAt > since)
                    .OrderByDescending(m => m.TimesUsed).Take(10)
                    .Select(m => new BulkPvAccountingPrompt.VendorHistoricalAccount(
                        m.AccountCode, m.AccountName ?? "", m.TimesUsed))
                    .ToListAsync(ct)
                : new List<BulkPvAccountingPrompt.VendorHistoricalAccount>();

            var src = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == sourceInvoiceId && d.CompanyId == companyId)
                .Select(d => new BulkPvAccountingPrompt.SourceInvoiceContext(
                    d.DocumentNumber, d.DocumentType.ToString(),
                    d.TotalAmount, d.VatAmount, d.WithholdingTaxAmount,
                    d.BalanceDue))
                .FirstOrDefaultAsync(ct);
            if (src == null)
            {
                _logger.LogWarning("Bulk PV: source invoice not found {Id}", sourceInvoiceId);
                src = new BulkPvAccountingPrompt.SourceInvoiceContext(
                    "?", "?", lines.Sum(l => l.Amount), null, null, 0m);
            }

            decimal? avg6 = null;
            if (!string.IsNullOrEmpty(vendorKey))
            {
                var since6 = DateTime.UtcNow.AddMonths(-6);
                var amounts = await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == companyId && !d.IsDeleted
                                && d.DocumentDate > since6
                                && d.Contact.TaxId == vendorTaxId)
                    .Select(d => d.TotalAmount)
                    .ToListAsync(ct);
                if (amounts.Count > 0) avg6 = amounts.Average();
            }
            var vendor = new BulkPvAccountingPrompt.VendorContext(
                vendorName, vendorTaxId, null, vendorIndustry, avg6);

            var lineInputs = lines.Select(l => new BulkPvAccountingPrompt.LineInput(
                l.LineId.ToString(), l.Description, l.Amount, l.CurrentAccountCode)).ToList();

            // โหลด business context (ดูคำอธิบายใน CompanyBusinessContextLoader)
            // — AI ใช้ตัดสินใจตามสายธุรกิจ + pattern จริงของ tenant.
            var bizCtx = await Prompts.CompanyBusinessContextLoader.LoadAsync(_db, companyId, ct);

            var req = BulkPvAccountingPrompt.Build(companyId, sourceInvoiceId,
                vendor, src, lineInputs, candidates, history, currency,
                businessContext: bizCtx);
            var resp = await _orchestrator.AskAsync(req, ct);

            var parsed = ParseBulkPvResponse(resp, lines);

            // Synthesise per-line child feedback rows so the GL-account
            // distillation model has the single-line shape it knows how
            // to parse + so the UI's per-line "user picked X" can flow
            // into per-line training labels.
            var withChildIds = new Dictionary<Guid, DocumentAiSuggestion>(parsed.ByLineId.Count);
            // Pre-build candidate prompt list for heuristic override
            var heuristicCandidates = candidates.Select(c =>
                new Prompts.GlAccountPrompt.AccountCandidate(c.Code, c.Name, c.Type, true)).ToList();
            // Mutable working dict — เริ่มจากผล AI แล้ว override ตรงไหนผิด
            var byLineWorking = parsed.ByLineId.ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var l in lines)
            {
                // ⭐ Override AI ถ้า heuristic durable-goods ชัด — เครื่องปริ้นท์/
                // คอมพิวเตอร์/... → 12xxx (Asset). กัน AI ตอบ "วัสดุสิ้นเปลือง"
                // ผิด. ถ้า heuristic confidence ≥0.85 และต่างจาก AI answer →
                // override (heuristic deterministic, AI สามารถผิดได้)
                var (priorCode, priorConf) = DurableGoodsHeuristic.Predict(
                    l.Description, l.Amount, heuristicCandidates);
                if (byLineWorking.TryGetValue(l.LineId, out var aiSugg)
                    && priorCode != null && priorConf >= 0.85m
                    && !string.Equals(aiSugg.Answer, priorCode, StringComparison.OrdinalIgnoreCase))
                {
                    byLineWorking[l.LineId] = aiSugg with {
                        Answer = priorCode,
                        Confidence = priorConf,
                        Reasoning = $"[heuristic override §65 ตรี (5)] {priorCode} (เครื่องใช้ทน/durable goods). เดิม AI แนะนำ {aiSugg.Answer}",
                    };
                }
                if (!byLineWorking.TryGetValue(l.LineId, out var sugg))
                {
                    withChildIds[l.LineId] = Fallback(null, null);
                    continue;
                }
                var perLineJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    vendor = new { name = vendorName, tax_id = vendorTaxId, industry = vendorIndustry },
                    line = new { description = l.Description, amount = l.Amount },
                });
                var childFid = await SynthesiseChildFeedbackAsync(
                    companyId,
                    AiFeatureKey.PaymentVoucherAccountingSuggestion,
                    perLineJson,
                    aiAnswer: sugg.Answer,
                    aiConfidence: sugg.Confidence,
                    sourceEntityType: "DocumentLine",
                    sourceEntityId: l.LineId,
                    parentFeedbackId: resp.FeedbackId ?? Guid.Empty,
                    ct);
                withChildIds[l.LineId] = sugg with { FeedbackId = childFid };
            }
            return parsed with { ByLineId = withChildIds };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bulk PV accounting augmenter failed");
            return new BulkPvAccountingResult(
                lines.ToDictionary(l => l.LineId, _ => Fallback(null, null)),
                Array.Empty<string>(),
                new[] { $"Bulk PV exception: {ex.Message}" },
                UsedAi: false);
        }
    }

    /// <summary>Parse the bulk PV response. Defensive — AI may omit a
    /// line; we fall back to local for any missing lineId and record
    /// a warning. Each line entry also writes a per-line feedback row
    /// via the orchestrator so GlAccountDistillationModel still learns
    /// from each prediction.</summary>
    private BulkPvAccountingResult ParseBulkPvResponse(AiResponse resp,
        IReadOnlyList<(Guid LineId, string Description, decimal Amount, string? CurrentAccountCode)> lines)
    {
        var byId = new Dictionary<Guid, DocumentAiSuggestion>();
        var observations = new List<string>();
        var warnings = new List<string>();
        var raw = resp.RawResponseJson ?? resp.PrimaryAnswer;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var json = raw.Trim();
                if (json.StartsWith("```"))
                {
                    var nl = json.IndexOf('\n');
                    if (nl > 0) json = json[(nl + 1)..];
                    if (json.EndsWith("```")) json = json[..^3];
                    json = json.Trim();
                }
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("lines", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (!el.TryGetProperty("lineId", out var idEl)) continue;
                        if (!Guid.TryParse(idEl.GetString(), out var lid)) continue;
                        var acc = el.TryGetProperty("accountCode", out var aEl) ? aEl.GetString() : null;
                        var conf = el.TryGetProperty("confidence", out var cEl) && cEl.TryGetDecimal(out var c) ? c : 0.5m;
                        var alts = new List<string>();
                        if (el.TryGetProperty("alternatives", out var altEl) && altEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var a in altEl.EnumerateArray()) if (a.GetString() is { } s) alts.Add(s);
                        var reasoning = el.TryGetProperty("reasoning", out var rEl) ? rEl.GetString() : null;
                        // ภาษีซื้อต้องห้าม §82/5: pack เป็น ComplianceFlags
                        // "VAT_NON_CLAIMABLE:<reason>" → UI parse แล้ว set
                        // checkbox + reason field. ทำผ่าน flag ไม่ต้องเปลี่ยน
                        // record schema กระทบ feature อื่น.
                        var flags = new List<string>();
                        if (el.TryGetProperty("isVatClaimable", out var vEl) && vEl.ValueKind == System.Text.Json.JsonValueKind.False)
                        {
                            var reason = el.TryGetProperty("vatNonClaimableReason", out var vrEl) ? vrEl.GetString() : null;
                            flags.Add($"VAT_NON_CLAIMABLE:{reason ?? "§82/5 ภาษีต้องห้าม"}");
                        }
                        byId[lid] = new DocumentAiSuggestion(
                            Answer: acc, Confidence: conf, Alternatives: alts,
                            Risks: Array.Empty<string>(),
                            ComplianceFlags: flags,
                            Reasoning: reasoning,
                            SuggestedActions: Array.Empty<string>(),
                            UsedAi: resp.UsedAi, FeedbackId: resp.FeedbackId);
                    }
                }
                if (root.TryGetProperty("cross_line_observations", out var obsEl)
                    && obsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var o in obsEl.EnumerateArray()) if (o.GetString() is { } s) observations.Add(s);
                if (root.TryGetProperty("warnings", out var wEl)
                    && wEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var w in wEl.EnumerateArray()) if (w.GetString() is { } s) warnings.Add(s);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "Bulk PV parse failed; falling back to local for all lines");
                warnings.Add("AI ตอบกลับ JSON ไม่ valid — ใช้ local สำหรับทุกบรรทัด");
            }
        }
        // Fill missing lines from local fallback.
        foreach (var l in lines)
        {
            if (byId.ContainsKey(l.LineId)) continue;
            byId[l.LineId] = Fallback(l.CurrentAccountCode, null);
            warnings.Add($"AI ไม่ตอบบรรทัด {l.LineId.ToString()[..8]}… — ใช้ local");
        }
        return new BulkPvAccountingResult(byId, observations, warnings, resp.UsedAi);
    }

    // ────────────────────────────────────────────────────────────────
    //  Bulk: approval warning fixes (all warnings in one call)
    // ────────────────────────────────────────────────────────────────
    public async Task<BulkApprovalWarningFixResult> SuggestApprovalWarningFixesBulkAsync(
        Guid companyId, Guid documentId,
        IReadOnlyList<string> warnings,
        object documentSnapshot, object? vendorHistory,
        CancellationToken ct = default)
    {
        if (warnings.Count == 0)
            return new BulkApprovalWarningFixResult(
                Array.Empty<DocumentAiSuggestion>(), null, Array.Empty<string>(), false);
        if (warnings.Count == 1)
        {
            // Single warning — no bulk advantage; route through existing path.
            var single = await SuggestApprovalWarningFixAsync(
                companyId, documentId, warnings[0], documentSnapshot, vendorHistory, ct);
            return new BulkApprovalWarningFixResult(
                new[] { single }, null, Array.Empty<string>(), single.UsedAi);
        }

        try
        {
            var req = BulkApprovalWarningFixPrompt.Build(
                companyId, documentId, warnings, documentSnapshot, vendorHistory);
            var resp = await _orchestrator.AskAsync(req, ct);
            var parsed = ParseBulkApprovalResponse(resp, warnings);

            // Synthesise per-warning child rows so ApprovalWarningDistillationModel
            // can mine each warning_template → fix mapping individually.
            // UI uses these per-warning FeedbackIds when user confirms.
            var enriched = new List<DocumentAiSuggestion>(parsed.Hints.Count);
            for (int i = 0; i < parsed.Hints.Count; i++)
            {
                var hint = parsed.Hints[i];
                // คำเตือนที่ไม่มีใครตอบ: ไม่สร้างแถว feedback ให้เลย (D1-11) —
                // แถวที่ไม่มีคำตอบของโมเดลแต่ผู้ใช้กด "รับทราบ" จะกลายเป็นหลักฐาน
                // ว่า "โมเดลแนะนำให้ปล่อยผ่านแล้วผู้ใช้เห็นด้วย" ซึ่งไม่เคยเกิดขึ้น
                if (string.IsNullOrWhiteSpace(hint.Answer))
                {
                    enriched.Add(hint with { FeedbackId = null });
                    continue;
                }
                var perWarnJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    warning = warnings[i],
                    document = documentSnapshot,
                });
                var childFid = await SynthesiseChildFeedbackAsync(
                    companyId,
                    AiFeatureKey.ApprovalWarningFixSuggestion,
                    perWarnJson,
                    aiAnswer: hint.Answer,
                    aiConfidence: hint.Confidence,
                    sourceEntityType: "Document",
                    sourceEntityId: documentId,
                    parentFeedbackId: resp.FeedbackId ?? Guid.Empty,
                    ct);
                enriched.Add(hint with { FeedbackId = childFid });
            }
            return parsed with { Hints = enriched };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bulk approval warning augmenter failed");
            // ไม่มีคำตอบ ≠ "Acknowledge" — ดูเหตุผลใน Helpers/AiHintAnswer (D1-11)
            return new BulkApprovalWarningFixResult(
                warnings.Select(_ => Fallback(null, null)).ToList(),
                null,
                new[] { $"Bulk exception: {ex.Message}" },
                UsedAi: false);
        }
    }

    private BulkApprovalWarningFixResult ParseBulkApprovalResponse(AiResponse resp,
        IReadOnlyList<string> warnings)
    {
        var byIdx = new Dictionary<int, DocumentAiSuggestion>();
        string? rootCause = null;
        var parseWarnings = new List<string>();
        var raw = resp.RawResponseJson ?? resp.PrimaryAnswer;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var json = raw.Trim();
                if (json.StartsWith("```"))
                {
                    var nl = json.IndexOf('\n');
                    if (nl > 0) json = json[(nl + 1)..];
                    if (json.EndsWith("```")) json = json[..^3];
                    json = json.Trim();
                }
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("root_cause", out var rcEl)) rootCause = rcEl.GetString();
                if (root.TryGetProperty("fixes", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (!el.TryGetProperty("warningIndex", out var iEl)) continue;
                        if (!iEl.TryGetInt32(out var idx) || idx < 0 || idx >= warnings.Count) continue;
                        var primary = el.TryGetProperty("primary", out var pEl) ? pEl.GetString() : null;
                        var conf = el.TryGetProperty("confidence", out var cEl) && cEl.TryGetDecimal(out var c) ? c : 0.5m;
                        var reasoning = el.TryGetProperty("reasoning", out var rEl) ? rEl.GetString() : null;
                        var actions = new List<string>();
                        if (el.TryGetProperty("suggestedActions", out var sEl) && sEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var a in sEl.EnumerateArray()) if (a.GetString() is { } s) actions.Add(s);
                        // AI ตอบมาแต่ไม่มีช่อง "primary" = ตอบไม่ครบ ⇒ ถือว่าไม่มีคำตอบ
                        // สำหรับคำเตือนข้อนั้น (เดิมแปลงเป็น "Acknowledge" เงียบ ๆ)
                        byIdx[idx] = new DocumentAiSuggestion(
                            Answer: primary, Confidence: primary != null ? conf : (decimal?)null,
                            Alternatives: Array.Empty<string>(),
                            Risks: Array.Empty<string>(),
                            ComplianceFlags: Array.Empty<string>(),
                            Reasoning: reasoning,
                            SuggestedActions: actions,
                            UsedAi: resp.UsedAi, FeedbackId: resp.FeedbackId);
                    }
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "Bulk approval parse failed");
                parseWarnings.Add("AI ตอบกลับ JSON ไม่ valid — ใช้ local");
            }
        }
        var hints = new List<DocumentAiSuggestion>(warnings.Count);
        for (int i = 0; i < warnings.Count; i++)
            // คำเตือนที่ AI ไม่ได้ตอบถึง = ไม่มีคำตอบ (ห้ามเติมคำแนะนำแทนมัน)
            hints.Add(byIdx.TryGetValue(i, out var h) ? h : Fallback(null, null));
        return new BulkApprovalWarningFixResult(hints, rootCause, parseWarnings, resp.UsedAi);
    }

    public async Task<DocumentAiSuggestion?> CheckDuplicateAsync(Guid companyId,
        Guid contactId, decimal amount, DateTime documentDate,
        string? subject, CancellationToken ct = default)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                document = new
                {
                    contactId = contactId.ToString(),
                    amount,
                    documentDate = documentDate.ToString("yyyy-MM-dd"),
                    subject = subject ?? "",
                },
            });
            var req = new AiRequest
            {
                FeatureKey = AiFeatureKey.FuzzyDuplicateDetection,
                CompanyId = companyId,
                SystemPrompt = "Detect near-duplicate documents based on contact + amount + date + subject similarity.",
                UserPromptJson = payload,
                SourceEntityType = "DocumentDraft",
                CacheTtlOverrideDays = 0,
                BypassCache = true,
                MaxTokensOverride = 300,
            };
            var resp = await _orchestrator.AskAsync(req, ct);
            // Local model returns the matched Document id JSON when
            // confidence ≥ 0.70 (its floor); below that, both local and
            // AI may decline — Convert handles null PrimaryAnswer.
            if (string.IsNullOrWhiteSpace(resp.PrimaryAnswer)) return null;

            // 🛡️ anti-hallucination guard (กฎเหล็ก #1) — AI/local อาจคืน document id
            // ที่ "แต่งขึ้น" หรือของ tenant อื่น. ตรวจว่า id ที่ตอบมีจริง + เป็นของ
            // บริษัทนี้ + contact เดียวกัน ก่อนบอกผู้ใช้ว่า "ซ้ำกับใบนี้". ถ้า id ไม่ผ่าน
            // → ไม่ยืนยันว่าซ้ำ (คืน null) กันเตือนผิด/ลิงก์ไปเอกสารที่ไม่มีอยู่.
            // PrimaryAnswer มี 2 รูป: GUID เปล่า (คำตอบจาก AI) หรือ JSON blob
            // {"id":...,"documentNumber":...,"score":...} (จาก
            // DuplicateDocumentDistillationModel) — เดิม Guid.TryParse กับ blob
            // ไม่ผ่านเสมอ → local เจอใบซ้ำแต่ระบบไม่เคยเตือนเลย
            var answerText = resp.PrimaryAnswer?.Trim() ?? "";
            if (answerText.StartsWith('{'))
            {
                try
                {
                    using var blob = System.Text.Json.JsonDocument.Parse(answerText);
                    if (blob.RootElement.TryGetProperty("id", out var idEl))
                        answerText = idEl.GetString() ?? idEl.GetRawText().Trim('"');
                }
                catch (System.Text.Json.JsonException) { return null; }
            }
            if (!Guid.TryParse(answerText, out var matchedId))
                return null;
            var exists = await _db.Documents.AsNoTracking().AnyAsync(d =>
                d.Id == matchedId && d.CompanyId == companyId
                && d.ContactId == contactId && !d.IsDeleted, ct);
            if (!exists)
            {
                _logger.LogWarning("Duplicate detection returned id {Id} not valid for company {Company}/contact {Contact} — declining",
                    matchedId, companyId, contactId);
                return null;
            }
            return Convert(resp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Duplicate detection failed");
            return null;
        }
    }

    private static DocumentAiSuggestion Convert(AiResponse resp) => new(
        Answer: resp.PrimaryAnswer,
        Confidence: resp.Confidence,
        Alternatives: resp.Alternatives,
        Risks: resp.Risks,
        ComplianceFlags: resp.ComplianceFlags,
        Reasoning: resp.Reasoning,
        SuggestedActions: resp.SuggestedActions,
        UsedAi: resp.UsedAi,
        FeedbackId: resp.FeedbackId);

    private static DocumentAiSuggestion Fallback(string? localAnswer, decimal? confidence) => new(
        Answer: localAnswer,
        Confidence: confidence,
        Alternatives: Array.Empty<string>(),
        Risks: Array.Empty<string>(),
        ComplianceFlags: Array.Empty<string>(),
        Reasoning: null,
        SuggestedActions: Array.Empty<string>(),
        UsedAi: false,
        FeedbackId: null);
}
