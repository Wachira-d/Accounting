using System.Diagnostics;
using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

public class AiOrchestrator : IAiOrchestrator
{
    private readonly AccountingDbContext _db;
    private readonly IEnumerable<IAiProvider> _providers;
    private readonly IAiResponseCacheService _cache;
    private readonly IAiBudgetGuard _budget;
    private readonly IAiFeedbackRecorder _recorder;
    private readonly IAiPromptSanitizer _sanitizer;
    private readonly IEnumerable<Distillation.ILocalDistillationModel> _distilled;
    private readonly IAiFeatureRoutingResolver _routing;
    private readonly ILogger<AiOrchestrator> _logger;

    /// <summary>Per-instance cache ของ company-context block (key=companyId).
    /// Orchestrator เป็น scoped → cache อยู่ตลอด request เดียว; bulk call
    /// หลายครั้งจึงโหลด context ครั้งเดียว.</summary>
    private readonly Dictionary<Guid, string> _companyContextCache = new();

    public AiOrchestrator(
        AccountingDbContext db,
        IEnumerable<IAiProvider> providers,
        IAiResponseCacheService cache,
        IAiBudgetGuard budget,
        IEnumerable<Distillation.ILocalDistillationModel> distilled,
        IAiFeatureRoutingResolver routing,
        IAiFeedbackRecorder recorder,
        IAiPromptSanitizer sanitizer,
        ILogger<AiOrchestrator> logger)
    {
        _db = db;
        _providers = providers;
        _cache = cache;
        _budget = budget;
        _distilled = distilled;
        _routing = routing;
        _recorder = recorder;
        _sanitizer = sanitizer;
        _logger = logger;
    }

    /// <summary>Shared RNG for the per-feature sampling coin-flip.
    /// Random.Shared is thread-safe for concurrent NextDouble calls
    /// in .NET 6+.</summary>
    private static readonly Random _sampling = Random.Shared;

    /// <summary>Resolve the per-feature local model + run prediction.
    /// Returns null when no model is registered, not ready, or threw.
    /// Caller decides what to DO with the prediction based on routing.</summary>
    private async Task<Distillation.LocalPrediction?> TryPredictLocalAsync(AiRequest request, CancellationToken ct)
    {
        var local = _distilled.FirstOrDefault(m => m.FeatureKey == request.FeatureKey);
        if (local == null || !local.IsReady) return null;
        try
        {
            return await local.PredictAsync(request.CompanyId, request.UserPromptJson, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local model PredictAsync threw for {Feature}", request.FeatureKey);
            return null;
        }
    }

    /// <summary>Short-circuit return: record a Skipped feedback row
    /// (LocalModelAnswer populated for accuracy attribution + admin
    /// audit) and emit a synthetic AiResponse with UsedAi=false.</summary>
    private async Task<AiResponse> ReturnLocalAsync(AiRequest request,
        Distillation.LocalPrediction localPred, string reason, CancellationToken ct)
    {
        var fid = await _recorder.RecordCallAsync(new AiFeedbackRecord(
            request.CompanyId, request.FeatureKey, "", request.UserPromptJson, null,
            AiPrimaryAnswer: null, AiConfidence: null,
            LocalModelAnswer: localPred.PrimaryAnswer,
            LocalModelConfidence: localPred.Confidence,
            LocalModelVersion: localPred.ModelVersion,
            request.SourceEntityType, request.SourceEntityId,
            AiCallStatus.Skipped, AiProviderType.DeepSeek, ModelVersion: null,
            LatencyMs: 0, InputTokens: 0, OutputTokens: 0, CostUsd: 0m,
            CacheHitOfFeedbackId: null,
            ErrorMessage: reason), ct);
        return new AiResponse
        {
            Status = AiCallStatus.Skipped,
            PrimaryAnswer = localPred.PrimaryAnswer,
            Confidence = localPred.Confidence,
            Alternatives = localPred.Alternatives,
            Risks = Array.Empty<string>(),
            ComplianceFlags = Array.Empty<string>(),
            Reasoning = reason,
            SuggestedActions = Array.Empty<string>(),
            UsedAi = false,
            UsedCache = false,
            FeedbackId = fid,
            ProviderModel = "local:" + localPred.ModelVersion,
        };
    }

    public async Task<AiResponse> AskAsync(AiRequest request, CancellationToken ct = default)
    {
        // Outer try/catch is the ABSOLUTE safety net: under no
        // circumstances may the caller see an exception from here. The
        // local model's prediction is always returned in the response
        // so call sites can use PrimaryAnswer unconditionally.
        try
        {
            return await AskInternalAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AiOrchestrator outer-catch — feature {Feature}", request.FeatureKey);
            return FallbackToLocal(request, AiCallStatus.Failed, error: ex.Message, feedbackId: null);
        }
    }

    private async Task<AiResponse> AskInternalAsync(AiRequest request, CancellationToken ct)
    {
        var settings = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(ct);

        // ── Step 0: per-feature routing decision ──────────────────────
        // Admin policy from AiFeatureRoutingConfigs decides whether to
        // consult the local distilled model, the provider, or both.
        // The five modes:
        //   Disabled     → orchestrator returns local fallback (no call)
        //   LocalOnly    → student answers; provider never billed
        //   ProviderOnly → ignore student; always call provider
        //   Hybrid       → student short-circuits ≥ threshold; sample for drift
        //   AlwaysTeach  → always call provider, record local head-to-head
        // ForceProviderCall (admin UI "ขอความเห็น AI" button) overrides
        // all modes except Disabled.
        var routing = await _routing.ResolveAsync(request.FeatureKey, ct);
        var localPred = await TryPredictLocalAsync(request, ct);
        if (localPred != null)
        {
            request = request with
            {
                LocalPrimaryAnswer = localPred.PrimaryAnswer,
                LocalConfidence = localPred.Confidence,
                LocalModelVersion = localPred.ModelVersion,
            };
        }

        // Disabled ต้องชนะทุก override (ForceProviderCall/BypassCache) — เดิม
        // เงื่อนไขครอบ switch ทั้งก้อน ทำให้ feature ที่ admin ปิดแล้วยังยิง
        // provider ได้ = kill-switch ระดับ feature ใช้ไม่ได้จริง
        if (routing.Mode == AiFeatureRoutingMode.Disabled)
        {
            var fidDisabled = await RecordSkip(request, AiCallStatus.Skipped,
                "Feature disabled by admin routing policy", ct);
            return FallbackToLocal(request, AiCallStatus.Skipped, null, fidDisabled);
        }

        if (!request.ForceProviderCall && !request.BypassCache)
        {
            switch (routing.Mode)
            {
                case AiFeatureRoutingMode.Disabled:
                {
                    var fid = await RecordSkip(request, AiCallStatus.Skipped,
                        "Feature disabled by admin routing policy", ct);
                    return FallbackToLocal(request, AiCallStatus.Skipped, null, fid);
                }

                case AiFeatureRoutingMode.LocalOnly:
                    if (localPred != null)
                        return await ReturnLocalAsync(request, localPred,
                            $"LocalOnly mode (v{localPred.ModelVersion})", ct);
                    // No local — degrade gracefully to LocalFallback with skipped status.
                    var fidLocal = await RecordSkip(request, AiCallStatus.Skipped,
                        "LocalOnly mode but no local prediction available", ct);
                    return FallbackToLocal(request, AiCallStatus.Skipped, null, fidLocal);

                case AiFeatureRoutingMode.Hybrid:
                    if (localPred != null && localPred.Confidence >= routing.LocalConfidenceThreshold)
                    {
                        // High-confidence local — sample N% through provider
                        // for drift calibration; otherwise short-circuit.
                        if (_sampling.NextDouble() >= (double)routing.ProviderSamplingRate)
                            return await ReturnLocalAsync(request, localPred,
                                $"Hybrid: local wins ({localPred.Confidence:P0} >= {routing.LocalConfidenceThreshold:P0})",
                                ct);
                        // Falls through to provider call with local attached.
                    }
                    break;

                case AiFeatureRoutingMode.AlwaysTeach:
                    // Always hit provider — but if ProviderSamplingRate is set,
                    // honour it as a cost dial (e.g. 0.5 = teach on half of calls,
                    // others short-circuit even if low-confidence). This lets
                    // admins lower the bill while keeping a steady training stream.
                    if (routing.ProviderSamplingRate < 1m
                        && _sampling.NextDouble() >= (double)routing.ProviderSamplingRate
                        && localPred != null)
                    {
                        return await ReturnLocalAsync(request, localPred,
                            $"AlwaysTeach: budget-sampled to local ({routing.ProviderSamplingRate:P0} sample rate)",
                            ct);
                    }
                    break;

                case AiFeatureRoutingMode.ProviderOnly:
                    // Fall through to provider call; local prediction stays
                    // attached to the request so feedback row records both.
                    break;
            }
        }

        // ── Step 1: master switch ─────────────────────────────────────
        if (settings == null || !settings.AiAugmentationEnabled)
        {
            var fid = await RecordSkip(request, AiCallStatus.Skipped, "AiAugmentationEnabled=false", ct);
            return FallbackToLocal(request, AiCallStatus.Skipped, error: null, feedbackId: fid);
        }

        // ── Step 2: locate active provider ────────────────────────────
        var providerConfig = await _db.AiProviderConfigs.AsNoTracking()
            .FirstOrDefaultAsync(p => p.IsActive && p.IsEnabled, ct);
        if (providerConfig == null)
        {
            var fid = await RecordSkip(request, AiCallStatus.NoProvider, "No active AI provider configured", ct);
            return FallbackToLocal(request, AiCallStatus.NoProvider, error: null, feedbackId: fid);
        }
        var providerImpl = _providers.FirstOrDefault(p => p.Kind == providerConfig.ProviderType);
        if (providerImpl == null)
        {
            var fid = await RecordSkip(request, AiCallStatus.NoProvider,
                $"Provider {providerConfig.ProviderType} not registered", ct);
            return FallbackToLocal(request, AiCallStatus.NoProvider, error: null, feedbackId: fid);
        }

        // ── Step 3: budget check (skippable when ForceProviderCall) ──
        if (!request.ForceProviderCall)
        {
            var budget = await _budget.EvaluateAsync(providerConfig.Id,
                providerConfig.DailyCallCap, providerConfig.MonthlyBudgetUsd, ct);
            if (!budget.Allowed)
            {
                var fid = await RecordSkip(request, AiCallStatus.BudgetExceeded, budget.BlockedReason, ct);
                return FallbackToLocal(request, AiCallStatus.BudgetExceeded,
                    error: budget.BlockedReason, feedbackId: fid);
            }
        }

        // ── Step 4: sanitise + hash ───────────────────────────────────
        // กฎเหล็ก #1: ส่งบริบทธุรกิจให้ AI ทุก call (ชื่อบริษัท, ประเภทธุรกิจ,
        // industry, ผังที่ใช้บ่อย) — รวมศูนย์ที่นี่เพื่อให้ทุก prompt (GL, WHT,
        // CN reason, DocType ฯลฯ) ได้ context เดียวกันโดยไม่ต้องแก้ทุก Build.
        // append เข้า SystemPrompt ก่อนคิด hash → cache key สะท้อน context.
        var enrichedSystemPrompt = await EnrichSystemPromptWithCompanyAsync(
            request.CompanyId, request.SystemPrompt, ct);
        var effectiveRequest = request with { SystemPrompt = enrichedSystemPrompt };

        var sanitizedUserJson = _sanitizer.Sanitize(request.UserPromptJson, settings.AiStripPiiInPrompts);
        var promptHash = _sanitizer.ComputePromptHash(sanitizedUserJson, enrichedSystemPrompt, providerConfig.Model);

        // ── Step 5: cache lookup ──────────────────────────────────────
        if (!request.BypassCache)
        {
            var (hit, content, originalId, ver, conf) = await _cache.TryGetAsync(promptHash, request.CompanyId, ct);
            if (hit && !string.IsNullOrEmpty(content))
            {
                var parsed = TryParseFeatureResponse(content);
                var fid = await _recorder.RecordCallAsync(new AiFeedbackRecord(
                    request.CompanyId, request.FeatureKey, promptHash, sanitizedUserJson, content,
                    parsed.PrimaryAnswer, parsed.Confidence,
                    request.LocalPrimaryAnswer, request.LocalConfidence, request.LocalModelVersion,
                    request.SourceEntityType, request.SourceEntityId,
                    AiCallStatus.Cached, providerConfig.ProviderType, ver,
                    LatencyMs: 0, InputTokens: 0, OutputTokens: 0, CostUsd: 0m,
                    CacheHitOfFeedbackId: originalId, ErrorMessage: null), ct);
                return new AiResponse
                {
                    Status = AiCallStatus.Cached,
                    PrimaryAnswer = parsed.PrimaryAnswer ?? request.LocalPrimaryAnswer,
                    Confidence = parsed.Confidence ?? conf ?? request.LocalConfidence,
                    Alternatives = parsed.Alternatives,
                    Risks = parsed.Risks,
                    ComplianceFlags = parsed.ComplianceFlags,
                    Reasoning = parsed.Reasoning,
                    SuggestedActions = parsed.SuggestedActions,
                    UsedAi = true,
                    UsedCache = true,
                    FeedbackId = fid,
                    ProviderModel = ver,
                    RawResponseJson = content,
                    LatencyMs = 0,
                };
            }
        }

        // ── Step 6: call provider ─────────────────────────────────────
        var sw = Stopwatch.StartNew();
        AiProviderRawResponse raw;
        using (var providerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // Hard ceiling — defends the request thread/HttpClient connection
            // from a misconfigured TimeoutSecondsOverride pinning resources for
            // far longer than any sane AI call should take. 300s = 5 min, well
            // above the largest legitimate prompt (bulk bank match at 180s).
            const int MaxTimeoutSec = 300;
            var requested = request.TimeoutSecondsOverride ?? providerConfig.RequestTimeoutSeconds;
            var timeoutSec = Math.Clamp(requested, 2, MaxTimeoutSec);
            providerCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            raw = await providerImpl.CompleteAsync(effectiveRequest with { UserPromptJson = sanitizedUserJson },
                providerConfig, providerCts.Token);
        }
        sw.Stop();

        if (!raw.Success)
        {
            var fid = await _recorder.RecordCallAsync(new AiFeedbackRecord(
                request.CompanyId, request.FeatureKey, promptHash, sanitizedUserJson, null,
                AiPrimaryAnswer: null, AiConfidence: null,
                request.LocalPrimaryAnswer, request.LocalConfidence, request.LocalModelVersion,
                request.SourceEntityType, request.SourceEntityId,
                AiCallStatus.Failed, providerConfig.ProviderType, raw.ModelVersion,
                LatencyMs: (int)sw.ElapsedMilliseconds,
                InputTokens: raw.InputTokens, OutputTokens: raw.OutputTokens, CostUsd: 0m,
                CacheHitOfFeedbackId: null, ErrorMessage: raw.Error), ct);
            _logger.LogWarning("AI provider call failed ({Feature}): {Err}", request.FeatureKey, raw.Error);
            return FallbackToLocal(request, AiCallStatus.Failed, error: raw.Error, feedbackId: fid);
        }

        // ── Step 7: parse provider content ────────────────────────────
        // Plan-style features (bulk bank reconciliation) return a structured
        // JSON document, not the primaryAnswer/alternatives shape. Skip the
        // schema check and hand the raw content back as Success so the caller
        // can parse its own schema. Only require non-empty content.
        if (request.RawPlanResponse)
        {
            if (string.IsNullOrWhiteSpace(raw.Content))
            {
                var efid = await _recorder.RecordCallAsync(new AiFeedbackRecord(
                    request.CompanyId, request.FeatureKey, promptHash, sanitizedUserJson, raw.Content,
                    AiPrimaryAnswer: null, AiConfidence: null,
                    request.LocalPrimaryAnswer, request.LocalConfidence, request.LocalModelVersion,
                    request.SourceEntityType, request.SourceEntityId,
                    AiCallStatus.InvalidResponse, providerConfig.ProviderType, raw.ModelVersion,
                    (int)sw.ElapsedMilliseconds, raw.InputTokens, raw.OutputTokens,
                    ComputeCost(raw, providerConfig), CacheHitOfFeedbackId: null,
                    ErrorMessage: "Empty plan response"), ct);
                return FallbackToLocal(request, AiCallStatus.InvalidResponse, error: "Empty plan response", feedbackId: efid);
            }

            var planFid = await _recorder.RecordCallAsync(new AiFeedbackRecord(
                request.CompanyId, request.FeatureKey, promptHash, sanitizedUserJson, raw.Content,
                AiPrimaryAnswer: null, AiConfidence: null,
                request.LocalPrimaryAnswer, request.LocalConfidence, request.LocalModelVersion,
                request.SourceEntityType, request.SourceEntityId,
                AiCallStatus.Success, providerConfig.ProviderType, raw.ModelVersion,
                (int)sw.ElapsedMilliseconds, raw.InputTokens, raw.OutputTokens,
                ComputeCost(raw, providerConfig), CacheHitOfFeedbackId: null, ErrorMessage: null), ct);

            return new AiResponse
            {
                Status = AiCallStatus.Success,
                PrimaryAnswer = null,
                Confidence = null,
                UsedAi = true,
                UsedCache = false,
                FeedbackId = planFid,
                ProviderModel = raw.ModelVersion,
                RawResponseJson = raw.Content,
                LatencyMs = (int)sw.ElapsedMilliseconds,
            };
        }

        var featureResp = TryParseFeatureResponse(raw.Content);
        if (featureResp.PrimaryAnswer == null && featureResp.Alternatives.Count == 0)
        {
            // Provider returned valid JSON but wrong schema. Treat as
            // InvalidResponse — record, fall through to local.
            var fid = await _recorder.RecordCallAsync(new AiFeedbackRecord(
                request.CompanyId, request.FeatureKey, promptHash, sanitizedUserJson, raw.Content,
                AiPrimaryAnswer: null, AiConfidence: null,
                request.LocalPrimaryAnswer, request.LocalConfidence, request.LocalModelVersion,
                request.SourceEntityType, request.SourceEntityId,
                AiCallStatus.InvalidResponse, providerConfig.ProviderType, raw.ModelVersion,
                (int)sw.ElapsedMilliseconds,
                raw.InputTokens, raw.OutputTokens,
                ComputeCost(raw, providerConfig),
                CacheHitOfFeedbackId: null,
                ErrorMessage: "Schema mismatch: no primary answer or alternatives"), ct);
            return FallbackToLocal(request, AiCallStatus.InvalidResponse, error: "Schema mismatch", feedbackId: fid);
        }

        var cost = ComputeCost(raw, providerConfig);

        // ── Step 8: write feedback row (Success path) ─────────────────
        var feedbackId = await _recorder.RecordCallAsync(new AiFeedbackRecord(
            request.CompanyId, request.FeatureKey, promptHash, sanitizedUserJson, raw.Content,
            featureResp.PrimaryAnswer, featureResp.Confidence,
            request.LocalPrimaryAnswer, request.LocalConfidence, request.LocalModelVersion,
            request.SourceEntityType, request.SourceEntityId,
            AiCallStatus.Success, providerConfig.ProviderType, raw.ModelVersion,
            (int)sw.ElapsedMilliseconds,
            raw.InputTokens, raw.OutputTokens, cost,
            CacheHitOfFeedbackId: null, ErrorMessage: null), ct);

        // ── Step 9: cache write (fire-and-forget, no await needed but
        // we do await for predictability — cheap insert).
        var ttl = request.CacheTtlOverrideDays ?? settings.AiDefaultCacheTtlDays;
        await _cache.PutAsync(promptHash, request.FeatureKey.ToString(), raw.Content,
            providerConfig.ProviderType, raw.ModelVersion, featureResp.Confidence,
            request.CompanyId, ttl, ct);

        return new AiResponse
        {
            Status = AiCallStatus.Success,
            PrimaryAnswer = featureResp.PrimaryAnswer ?? request.LocalPrimaryAnswer,
            Confidence = featureResp.Confidence ?? request.LocalConfidence,
            Alternatives = featureResp.Alternatives,
            Risks = featureResp.Risks,
            ComplianceFlags = featureResp.ComplianceFlags,
            Reasoning = featureResp.Reasoning,
            SuggestedActions = featureResp.SuggestedActions,
            UsedAi = true,
            UsedCache = false,
            FeedbackId = feedbackId,
            ProviderModel = raw.ModelVersion,
            RawResponseJson = raw.Content,
            LatencyMs = (int)sw.ElapsedMilliseconds,
        };
    }

    public async Task<AiResponse> AskAndRecordChoiceAsync(AiRequest request, string userChoice, CancellationToken ct = default)
    {
        var resp = await AskAsync(request, ct);
        if (resp.FeedbackId.HasValue && resp.FeedbackId.Value != Guid.Empty)
        {
            var acceptedAi = resp.UsedAi && resp.PrimaryAnswer == userChoice;
            await _recorder.RecordUserChoiceAsync(resp.FeedbackId.Value, userChoice, acceptedAi, ct);
        }
        return resp;
    }

    public Task RecordUserChoiceAsync(Guid feedbackId, string chosenAnswer, bool acceptedAi, CancellationToken ct = default)
        => _recorder.RecordUserChoiceAsync(feedbackId, chosenAnswer, acceptedAi, ct);

    /// <summary>เติม business context (ชื่อ/ประเภทธุรกิจ/industry/top accounts)
    /// ต่อท้าย system prompt — ทำให้ AI ทุก feature ตัดสินใจตามสายธุรกิจของ
    /// tenant. ทนต่อ DB error (คืน system prompt เดิม — ไม่ทำให้ AI flow พัง).</summary>
    private async Task<string> EnrichSystemPromptWithCompanyAsync(
        Guid companyId, string systemPrompt, CancellationToken ct)
    {
        try
        {
            if (!_companyContextCache.TryGetValue(companyId, out var block))
            {
                var ctx = await Prompts.CompanyBusinessContextLoader.LoadAsync(_db, companyId, ct);
                var top = ctx.TopAccountsUsed.Count > 0
                    ? string.Join(", ", ctx.TopAccountsUsed.Take(8).Select(a => $"{a.Code} {a.Name}"))
                    : "(ยังไม่มีประวัติการลงบัญชี)";
                block =
                    "\n\n--- BUSINESS CONTEXT (ใช้บริบทนี้ตัดสินใจให้ตรงสายธุรกิจ) ---\n" +
                    $"Company: {ctx.Name}\n" +
                    $"BusinessType: {ctx.BusinessType}\n" +
                    $"Industry: {ctx.IndustryType}\n" +
                    $"VAT-registered: {(ctx.IsVatRegistered ? $"yes ({ctx.VatRate}%)" : "no")}\n" +
                    $"WHT basis: {ctx.WhtRecognitionBasis ?? "Cash"}\n" +
                    $"Most-used accounts (6mo): {top}\n" +
                    "Prefer answers consistent with this company's industry + historical account pattern.";
                _companyContextCache[companyId] = block;
            }
            return systemPrompt + block;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EnrichSystemPromptWithCompany failed for {CompanyId}", companyId);
            return systemPrompt;
        }
    }

    private async Task<Guid?> RecordSkip(AiRequest req, AiCallStatus status, string? reason, CancellationToken ct)
    {
        try
        {
            return await _recorder.RecordCallAsync(new AiFeedbackRecord(
                req.CompanyId, req.FeatureKey, "", req.UserPromptJson, null,
                AiPrimaryAnswer: null, AiConfidence: null,
                req.LocalPrimaryAnswer, req.LocalConfidence, req.LocalModelVersion,
                req.SourceEntityType, req.SourceEntityId,
                status, AiProviderType.DeepSeek, ModelVersion: null,
                LatencyMs: 0, InputTokens: 0, OutputTokens: 0, CostUsd: 0m,
                CacheHitOfFeedbackId: null, ErrorMessage: reason), ct);
        }
        catch { return null; }
    }

    private static AiResponse FallbackToLocal(AiRequest req, AiCallStatus status, string? error, Guid? feedbackId)
        => new()
        {
            Status = status,
            PrimaryAnswer = req.LocalPrimaryAnswer,
            Confidence = req.LocalConfidence,
            Alternatives = req.LocalAlternatives,
            Risks = Array.Empty<string>(),
            ComplianceFlags = Array.Empty<string>(),
            Reasoning = error == null ? null : "AI ไม่พร้อม — ใช้คำตอบจาก local model: " + error,
            SuggestedActions = Array.Empty<string>(),
            UsedAi = false,
            UsedCache = false,
            FeedbackId = feedbackId,
            ProviderModel = null,
            RawResponseJson = null,
        };

    private static decimal ComputeCost(AiProviderRawResponse raw, AiProviderConfig config)
    {
        if (!raw.Success || raw.InputTokens == null || raw.OutputTokens == null) return 0m;
        var inCost = (raw.InputTokens.Value / 1_000_000m) * (config.PricePerInputTokenUsd1M ?? 0m);
        var outCost = (raw.OutputTokens.Value / 1_000_000m) * (config.PricePerOutputTokenUsd1M ?? 0m);
        return Math.Round(inCost + outCost, 8, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Best-effort parse of the provider's JSON content into the
    /// canonical AiResponse fields. Per-feature schemas vary but the
    /// orchestrator looks for these field names. Builders are
    /// instructed (via system prompt) to emit this shape.
    /// </summary>
    private static (string? PrimaryAnswer, decimal? Confidence,
                    IReadOnlyList<string> Alternatives, IReadOnlyList<string> Risks,
                    IReadOnlyList<string> ComplianceFlags, string? Reasoning,
                    IReadOnlyList<string> SuggestedActions)
        TryParseFeatureResponse(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            string? primary = TryString(root, "primary", "primary_answer", "answer", "value");
            decimal? confidence = TryDecimal(root, "confidence");
            var alternatives = TryStringList(root, "alternatives", "candidates");
            var risks = TryStringList(root, "risks", "warnings");
            var compliance = TryStringList(root, "compliance_flags", "thai_compliance_flags", "compliance");
            var reasoning = TryString(root, "reasoning", "rationale", "explanation");
            var actions = TryStringList(root, "suggested_actions", "actions");
            return (primary, confidence, alternatives, risks, compliance, reasoning, actions);
        }
        catch
        {
            return (null, null, Array.Empty<string>(), Array.Empty<string>(),
                    Array.Empty<string>(), null, Array.Empty<string>());
        }
    }

    private static string? TryString(JsonElement root, params string[] keys)
    {
        foreach (var k in keys)
            if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static decimal? TryDecimal(JsonElement root, params string[] keys)
    {
        foreach (var k in keys)
            if (root.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
                if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2)) return d2;
            }
        return null;
    }

    private static IReadOnlyList<string> TryStringList(JsonElement root, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        list.Add(item.GetString() ?? "");
                    else if (item.ValueKind == JsonValueKind.Object)
                        list.Add(item.GetRawText());   // structured alt — caller parses
                }
                return list;
            }
        }
        return Array.Empty<string>();
    }
}
