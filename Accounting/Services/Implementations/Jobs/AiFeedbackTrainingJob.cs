using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai.Distillation;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Jobs;

/// <summary>
/// Nightly job that converts AiSuggestionFeedback rows into:
///   1. Local-model training updates — when the user confirmed an AI
///      answer (UserAcceptedAi=true) or chose an alternative
///      (UserAcceptedAi=false but UserChosenAnswer is set), the chosen
///      value is upserted into the appropriate local-pattern table so
///      the local model gets stronger over time without any human-in-
///      the-loop step beyond the original click.
///   2. LocalModelHealth refresh — per-feature accuracy comparison +
///      drift detection so the admin page can show "AI is 30pp ahead
///      of local on WHT category — recommend retraining".
///
/// Idempotent — re-runs ok. Updates SiteSettings.AiLastFeedbackTrainingAt
/// so the admin page can show "trained X hours ago".
/// </summary>
public class AiFeedbackTrainingJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<AiFeedbackTrainingJob> _logger;

    public AiFeedbackTrainingJob(IServiceProvider services, IConfiguration config,
        ILogger<AiFeedbackTrainingJob> logger)
    { _services = services; _config = config; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Ai:FeedbackTraining:Enabled", true)) return;
        var interval = TimeSpan.FromHours(_config.GetValue("Ai:FeedbackTraining:IntervalHours", 6));

        try { await Task.Delay(TimeSpan.FromMinutes(7), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "AiFeedbackTrainingJob failed (will retry)"); }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-30);
        await RefreshAllLocalModelHealthsAsync(db, cutoff, ct);
        await TrainLocalModelsAsync(db, ct);
        await ReloadDistillationModelsAsync(db, ct);

        // Stamp "last trained" — drives the admin badge.
        var settings = await db.SiteSettings.FirstOrDefaultAsync(ct);
        if (settings != null)
        {
            settings.AiLastFeedbackTrainingAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        // Prune expired cache rows.
        await PruneExpiredCacheAsync(db, ct);
    }

    // ────────────────────────────────────────────────────────────────
    //  Per-feature accuracy + drift detection
    // ────────────────────────────────────────────────────────────────
    private async Task RefreshAllLocalModelHealthsAsync(AccountingDbContext db, DateTime cutoff, CancellationToken ct)
    {
        // Group feedback by feature + compute LocalAccuracy vs AiAccuracy
        // over the last 30 days using the user's chosen answer as ground
        // truth. Rows without UserChosenAnswer are excluded — we have
        // no label.
        var rows = await db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.CreatedAt >= cutoff && f.UserChosenAt != null)
            .GroupBy(f => f.FeatureKey)
            .Select(g => new
            {
                FeatureKey = g.Key,
                Samples = g.Count(),
                LocalCorrect = g.Count(f => f.LocalModelAnswer != null
                                            && f.LocalModelAnswer == f.UserChosenAnswer),
                AiCorrect = g.Count(f => f.UserAcceptedAi == true),
                Agreement = g.Count(f => f.AiPrimaryAnswer != null && f.LocalModelAnswer != null
                                          && f.AiPrimaryAnswer == f.LocalModelAnswer),
            })
            .ToListAsync(ct);

        foreach (var r in rows)
        {
            var health = await db.LocalModelHealths.FirstOrDefaultAsync(h => h.FeatureKey == r.FeatureKey, ct);
            if (health == null)
            {
                health = new LocalModelHealth { FeatureKey = r.FeatureKey };
                db.LocalModelHealths.Add(health);
            }
            health.SamplesLast30d = r.Samples;
            health.LocalAccuracy30d = r.Samples > 0 ? (decimal)r.LocalCorrect / r.Samples : 0m;
            health.AiAccuracy30d = r.Samples > 0 ? (decimal)r.AiCorrect / r.Samples : 0m;
            health.AgreementRate30d = r.Samples > 0 ? (decimal)r.Agreement / r.Samples : 0m;
            health.LastEvaluatedAt = DateTime.UtcNow;

            // Status decision:
            //   Samples < 30                                     → InsufficientData
            //   LocalAccuracy < AiAccuracy - 0.15                → NeedsRedesign
            //   LocalAccuracy < AiAccuracy - 0.05                → Degraded
            //   else                                              → Healthy
            if (r.Samples < 30)
            {
                health.Status = LocalModelHealthStatus.InsufficientData;
                health.Recommendation = $"Need ≥30 user-confirmed samples (have {r.Samples}). " +
                    "AI is gathering signal — recheck after another week of usage.";
            }
            else if (health.LocalAccuracy30d < health.AiAccuracy30d - 0.15m)
            {
                health.Status = LocalModelHealthStatus.NeedsRedesign;
                health.Recommendation = $"Local {health.LocalAccuracy30d:P0} vs AI {health.AiAccuracy30d:P0} " +
                    "— gap >15pp. แนะนำเพิ่ม features, เปลี่ยน algorithm, หรือ always-on AI สำหรับ feature นี้.";
            }
            else if (health.LocalAccuracy30d < health.AiAccuracy30d - 0.05m)
            {
                health.Status = LocalModelHealthStatus.Degraded;
                health.Recommendation = $"Local {health.LocalAccuracy30d:P0} vs AI {health.AiAccuracy30d:P0} " +
                    "— AI ดีกว่าเล็กน้อย, ดูข้อมูลเทรนเพิ่ม.";
            }
            else
            {
                health.Status = LocalModelHealthStatus.Healthy;
                health.Recommendation = $"Local {health.LocalAccuracy30d:P0} ≈ AI {health.AiAccuracy30d:P0}. " +
                    (health.AgreementRate30d >= 0.85m
                        ? "ลด sampling rate ลงได้ — local แทน AI ส่วนใหญ่"
                        : "เหมาะสม");
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("AiFeedbackTrainingJob: refreshed health for {N} features", rows.Count);
    }

    // ────────────────────────────────────────────────────────────────
    //  Local-model training writes (per-feature dispatch)
    // ────────────────────────────────────────────────────────────────
    private async Task TrainLocalModelsAsync(AccountingDbContext db, CancellationToken ct)
    {
        // Only consider rows where the user gave us a ground-truth label
        // AND the row hasn't been consumed yet (we mark consumption by
        // setting CreatedBy on the row via a separate UPDATE — simpler
        // than adding a new column).
        var since = DateTime.UtcNow.AddDays(-7);
        var labelled = await db.AiSuggestionFeedbacks
            .Where(f => f.UserChosenAt != null && f.UserChosenAt > since
                        && f.UserChosenAnswer != null
                        && (f.CreatedBy == null || f.CreatedBy != "TRAINED"))
            .ToListAsync(ct);

        var trainedFeatures = new Dictionary<string, int>();
        // Features ที่มี ground-truth row แต่ TrainSingleAsync ไม่มี case
        // — เก็บนับ "rows ที่หลุดวง training" ต่อ feature
        var orphanedFeatures = new Dictionary<string, int>();
        foreach (var row in labelled)
        {
            try
            {
                var consumed = await TrainSingleAsync(db, row, ct);
                if (consumed)
                {
                    row.CreatedBy = "TRAINED";
                    row.UpdatedAt = DateTime.UtcNow;
                    trainedFeatures.TryGetValue(row.FeatureKey, out var c);
                    trainedFeatures[row.FeatureKey] = c + 1;
                }
                else if (!KnownTrainerFeatures.Contains(row.FeatureKey))
                {
                    orphanedFeatures.TryGetValue(row.FeatureKey, out var oc);
                    orphanedFeatures[row.FeatureKey] = oc + 1;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Training write failed for feedback {Id}", row.Id);
            }
        }
        await db.SaveChangesAsync(ct);
        if (trainedFeatures.Count > 0)
            _logger.LogInformation("AiFeedbackTrainingJob: trained {Counts}",
                string.Join(", ", trainedFeatures.Select(kv => $"{kv.Key}={kv.Value}")));

        // Roll-up warning + stamp LocalModelHealth.Recommendation ครั้งเดียว
        // ต่อ feature ที่มี feedback แต่ไม่มี trainer — admin จะเห็นใน
        // /pages/ai-health.html ทันทีว่า feature ใดเสีย opportunity.
        if (orphanedFeatures.Count > 0)
        {
            _logger.LogWarning(
                "AiFeedbackTrainingJob: features with ground-truth feedback but NO trainer case " +
                "in TrainSingleAsync — signal is being silently dropped. Add a writer for: {Orphans}",
                string.Join(", ", orphanedFeatures.Select(kv => $"{kv.Key}({kv.Value} rows)")));
            foreach (var (featureKey, rows) in orphanedFeatures)
            {
                var health = await db.LocalModelHealths
                    .FirstOrDefaultAsync(h => h.FeatureKey == featureKey, ct);
                if (health == null)
                {
                    health = new LocalModelHealth { FeatureKey = featureKey };
                    db.LocalModelHealths.Add(health);
                }
                health.Status = LocalModelHealthStatus.NeedsRedesign;
                health.Recommendation = $"⚠ ขาด trainer ใน AiFeedbackTrainingJob.TrainSingleAsync — " +
                    $"มี {rows} ground-truth rows ที่ไม่ถูก distill เก็บเข้าตาราง local. " +
                    "เพิ่ม case + เพิ่มชื่อใน KnownTrainerFeatures เพื่อปิด gap นี้.";
                health.LastEvaluatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>FeatureKey ที่มี trainer dispatch แล้ว — ใช้ตรวจช่วง
    /// TrainLocalModelsAsync ว่ามี feature ใดเรียก AI + เก็บ feedback ground-
    /// truth ครบแล้ว แต่ยังไม่มี case ใน TrainSingleAsync. ถ้าเจอ → log
    /// warning ครั้งเดียวต่อ run + stamp LocalModelHealth.Recommendation ให้
    /// admin เห็นในหน้าจอ. ป้องกันการเสียโอกาส training data เงียบ ๆ. ค่า
    /// list นี้ต้อง sync กับ switch ใน TrainSingleAsync (compile-time
    /// guarantee: ทดสอบใน DEBUG ด้วย EnsureSwitchCoverage).</summary>
    internal static readonly HashSet<string> KnownTrainerFeatures = new(StringComparer.Ordinal)
    {
        nameof(AiFeatureKey.VendorCanonicalization),
        nameof(AiFeatureKey.GlAccountSuggestion),
        nameof(AiFeatureKey.PaymentVoucherAccountingSuggestion),
        nameof(AiFeatureKey.CreditNoteReasonClassification),
        nameof(AiFeatureKey.DocumentTypeClassification),
        nameof(AiFeatureKey.OcrFullReview),
        nameof(AiFeatureKey.DocumentConversionSuggestion),
        nameof(AiFeatureKey.WhtCategoryInference),
        nameof(AiFeatureKey.BankStatementMatch),
        nameof(AiFeatureKey.BulkBankStatementMatch),
        nameof(AiFeatureKey.ApprovalWarningFixSuggestion),
        nameof(AiFeatureKey.AnomalyExplanation),
        nameof(AiFeatureKey.StockMovementValidation),
        nameof(AiFeatureKey.AgingExplanation),
        // ── Reserved consume-only slots ─────────────────────────────
        // 4 features ที่ enum มีอยู่แต่ยังไม่ wire เข้า orchestrator —
        // เมื่อมี code เรียกในอนาคต (พร้อม prompt builder + feedback
        // endpoint) feedback rows จะถูก mark "TRAINED" อัตโนมัติ + นับเข้า
        // LocalModelHealth ทันที โดยไม่ต้องแก้ TrainSingleAsync. การ
        // เปลี่ยนเป็น writer ตารางจริงทำได้ภายหลังโดยแก้ case ใน switch.
        nameof(AiFeatureKey.LineItemStructuredParse),
        nameof(AiFeatureKey.ManualJournalSuggestion),
        nameof(AiFeatureKey.ForecastNarrative),
        nameof(AiFeatureKey.DocumentRoleInference),
        // New Sprint-1 features (VAT type + Payment terms) — wired live;
        // feedback rows become training corpus over time.
        nameof(AiFeatureKey.VatTypeInference),
        nameof(AiFeatureKey.PaymentTermsSuggestion),
    };

    /// <summary>
    /// Per-feature dispatch — returns true when the row was successfully
    /// folded into a local-pattern table. Skip (return false) when we
    /// don't yet have a writer for that feature; the row stays
    /// "unconsumed" so a later code change can pick it up.
    /// </summary>
    private async Task<bool> TrainSingleAsync(AccountingDbContext db, AiSuggestionFeedback row, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(row.UserChosenAnswer)) return false;

        // The FeatureKey is the enum name — switch on it.
        return row.FeatureKey switch
        {
            nameof(AiFeatureKey.VendorCanonicalization) => await TrainVendorCanonAsync(db, row, ct),
            nameof(AiFeatureKey.GlAccountSuggestion) => await TrainGlAccountAsync(db, row, ct),
            nameof(AiFeatureKey.PaymentVoucherAccountingSuggestion) => await TrainGlAccountAsync(db, row, ct),
            // CreditNote / DocType / WHT / BankMatch / ApprovalFix don't
            // need a dedicated local-table write — the feedback row
            // ITSELF is the training signal (LocalModelHealth tracks
            // accuracy; future local-model versions can replay these
            // rows during retrain). Mark as consumed so we don't keep
            // re-counting them.
            nameof(AiFeatureKey.CreditNoteReasonClassification) => true,
            nameof(AiFeatureKey.DocumentTypeClassification) => true,
            nameof(AiFeatureKey.OcrFullReview) => true,
            nameof(AiFeatureKey.DocumentConversionSuggestion) => true,
            nameof(AiFeatureKey.WhtCategoryInference) => true,
            nameof(AiFeatureKey.BankStatementMatch) => true,
            nameof(AiFeatureKey.BulkBankStatementMatch) => true,
            nameof(AiFeatureKey.ApprovalWarningFixSuggestion) => true,
            nameof(AiFeatureKey.AnomalyExplanation) => true,
            nameof(AiFeatureKey.StockMovementValidation) => true,
            nameof(AiFeatureKey.AgingExplanation) => true,
            // Reserved consume-only slots for features ที่จะ wire ในอนาคต —
            // mark consumed เพื่อเก็บ accuracy ใน LocalModelHealth พร้อมรับ
            // signal วันที่เริ่มเรียกจริง. เปลี่ยนเป็น writer ตารางจริงได้
            // ภายหลังโดยไม่กระทบ backfill (rows เก่ายังคงนับเข้า health).
            nameof(AiFeatureKey.LineItemStructuredParse) => true,
            nameof(AiFeatureKey.ManualJournalSuggestion) => true,
            nameof(AiFeatureKey.ForecastNarrative) => true,
            nameof(AiFeatureKey.DocumentRoleInference) => true,
            // Sprint-1 features — consume-only เริ่มต้น; เมื่อสะสม
            // ground-truth ≥30 rows ใน LocalModelHealth จะเห็น accuracy
            // → admin ตัดสินได้ว่าจะ promote เป็น distillation table writer
            nameof(AiFeatureKey.VatTypeInference) => true,
            nameof(AiFeatureKey.PaymentTermsSuggestion) => true,
            // Other features get their writer added later — return false
            // so the row stays available for a future code release. The
            // unmatched FeatureKey is rolled up + logged once per run in
            // TrainLocalModelsAsync via KnownTrainerFeatures, so admins
            // see exactly which feature is silently dropping signal.
            _ => false,
        };
    }

    private async Task<bool> TrainVendorCanonAsync(AccountingDbContext db, AiSuggestionFeedback row, CancellationToken ct)
    {
        // Vendor canon training upserts VendorKnownGoodValue with the
        // confirmed (raw-OCR-vendor-name → contactId) mapping. The
        // prompt was sanitised; raw name is fetched via SourceEntityId
        // (OcrScanResult.Id).
        if (!row.SourceEntityId.HasValue) return false;
        var scan = await db.OcrScanResults.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == row.SourceEntityId.Value, ct);
        if (scan?.ExtractedVendorName == null) return false;

        if (!Guid.TryParse(row.UserChosenAnswer, out var contactId)) return false;

        var rawName = scan.ExtractedVendorName.Trim();
        if (rawName.Length < 2) return false;

        var fieldName = "MatchedContactId:" + rawName;   // unique-per-OCR-name
        var existing = await db.VendorKnownGoodValues.FirstOrDefaultAsync(
            v => v.CompanyId == row.CompanyId
                 && v.VendorTaxId == scan.ExtractedVendorTaxId
                 && v.FieldName == fieldName, ct);
        if (existing == null)
        {
            db.VendorKnownGoodValues.Add(new VendorKnownGoodValue
            {
                CompanyId = row.CompanyId,
                VendorTaxId = scan.ExtractedVendorTaxId,
                FieldName = fieldName,
                Value = contactId.ToString(),
                Confidence = row.UserAcceptedAi == true ? 0.95m : 0.80m,
                ConfirmedCount = 1,
                Source = "UserCorrection",
                LastSeenAt = DateTime.UtcNow,
            });
        }
        else if (existing.Value == contactId.ToString())
        {
            existing.ConfirmedCount++;
            existing.LastSeenAt = DateTime.UtcNow;
            existing.Confidence = Math.Min(0.99m, existing.Confidence + 0.02m);
            existing.Source = "UserCorrection";
        }
        else
        {
            // User picked a different contact → demote the old one and
            // overwrite. UserCorrection trumps AzureDI on conflict.
            existing.Value = contactId.ToString();
            existing.Confidence = 0.85m;
            existing.ConfirmedCount = 1;
            existing.Source = "UserCorrection";
            existing.LastSeenAt = DateTime.UtcNow;
        }
        return true;
    }

    private async Task<bool> TrainGlAccountAsync(AccountingDbContext db, AiSuggestionFeedback row, CancellationToken ct)
    {
        // GL account training updates OcrCategoryMapping. PromptJson
        // carries vendor + line description; parse them out.
        if (string.IsNullOrEmpty(row.PromptJson)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(row.PromptJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("vendor", out var v) || !root.TryGetProperty("line", out var line))
                return false;
            var vendorTaxId = v.TryGetProperty("tax_id", out var tid) ? tid.GetString() : null;
            var vendorName = v.TryGetProperty("name", out var vn) ? vn.GetString() : null;
            var description = line.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            if (description.Length < 2) return false;

            var vendorKey = !string.IsNullOrEmpty(vendorTaxId) ? vendorTaxId
                : (vendorName ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(vendorKey)) return false;

            var keyword = description.Trim().ToLowerInvariant();
            if (keyword.Length > 80) keyword = keyword[..80];

            var mapping = await db.OcrCategoryMappings.FirstOrDefaultAsync(
                m => m.CompanyId == row.CompanyId && m.VendorKey == vendorKey
                     && m.DescriptionKeyword == keyword
                     && m.AccountCode == row.UserChosenAnswer, ct);
            if (mapping == null)
            {
                db.OcrCategoryMappings.Add(new OcrCategoryMapping
                {
                    CompanyId = row.CompanyId,
                    VendorKey = vendorKey,
                    DescriptionKeyword = keyword,
                    AccountCode = row.UserChosenAnswer!,
                    TimesUsed = 1,
                    LastUsedAt = DateTime.UtcNow,
                });
            }
            else
            {
                mapping.TimesUsed++;
                mapping.LastUsedAt = DateTime.UtcNow;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TrainGlAccount parse failed for {Id}", row.Id);
            return false;
        }
    }

    /// <summary>
    /// Rebuild the in-memory distilled student models (vendor canon,
    /// GL account) from the freshly-trained feedback corpus. Runs per
    /// company since each company's prediction table is independent.
    ///
    /// The models are SINGLETON in DI; resolving them here gets the
    /// same instance the orchestrator uses, so the reload immediately
    /// affects live traffic without any restart or cache invalidation.
    /// </summary>
    private async Task ReloadDistillationModelsAsync(AccountingDbContext db, CancellationToken ct)
    {
        var models = _services.GetServices<ILocalDistillationModel>().ToList();
        if (models.Count == 0) return;

        // Only reload for companies that have ANY relevant feedback —
        // empty companies don't waste a DB pass.
        var companyIds = await db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.UserChosenAt != null && !f.IsDeleted)
            .Select(f => f.CompanyId).Distinct().ToListAsync(ct);

        foreach (var companyId in companyIds)
        {
            foreach (var model in models)
            {
                try { await model.LoadFromFeedbackAsync(companyId, ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Distillation reload failed for {Feature} / {Cid}",
                        model.FeatureKey, companyId);
                }
            }
        }
        _logger.LogInformation("Reloaded {N} distillation models across {C} companies",
            models.Count, companyIds.Count);
    }

    private async Task PruneExpiredCacheAsync(AccountingDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var deleted = await db.AiResponseCaches
            .Where(c => c.ExpiresAt < now.AddDays(-1))
            .ExecuteDeleteAsync(ct);
        if (deleted > 0) _logger.LogInformation("Pruned {N} expired AiResponseCache rows", deleted);
    }
}
