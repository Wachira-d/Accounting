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
    private readonly ILogger<AiOrchestrator> _logger;

    public AiOrchestrator(
        AccountingDbContext db,
        IEnumerable<IAiProvider> providers,
        IAiResponseCacheService cache,
        IAiBudgetGuard budget,
        IAiFeedbackRecorder recorder,
        IAiPromptSanitizer sanitizer,
        ILogger<AiOrchestrator> logger)
    {
        _db = db;
        _providers = providers;
        _cache = cache;
        _budget = budget;
        _recorder = recorder;
        _sanitizer = sanitizer;
        _logger = logger;
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
        var sanitizedUserJson = _sanitizer.Sanitize(request.UserPromptJson, settings.AiStripPiiInPrompts);
        var promptHash = _sanitizer.ComputePromptHash(sanitizedUserJson, request.SystemPrompt, providerConfig.Model);

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
            providerCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, providerConfig.RequestTimeoutSeconds)));
            raw = await providerImpl.CompleteAsync(request with { UserPromptJson = sanitizedUserJson },
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
