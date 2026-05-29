using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Admin endpoints for AI provider configuration + usage telemetry.
/// All routes prefixed /api/admin/ai/ and gated by SystemAdmin so only
/// platform operators can configure DeepSeek API keys / switch providers
/// / monitor cost.
///
/// Sibling to AdminController — kept separate so the AI-specific
/// dependencies (IAiOrchestrator, IAiProvider impls) don't bloat the
/// catch-all AdminController constructor.
/// </summary>
[ApiController]
[Route("api/admin/ai")]
[Authorize(Roles = "SystemAdmin")]
public class AiAdminController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IEnumerable<IAiProvider> _providers;
    private readonly ILogger<AiAdminController> _logger;

    public AiAdminController(AccountingDbContext db, IEnumerable<IAiProvider> providers,
        ILogger<AiAdminController> logger)
    { _db = db; _providers = providers; _logger = logger; }

    // ────────────────────────────────────────────────────────────────
    //  Provider registry CRUD
    // ────────────────────────────────────────────────────────────────

    [HttpGet("providers")]
    public async Task<ActionResult<ApiResponse<object>>> ListProviders()
    {
        var rows = await _db.AiProviderConfigs.AsNoTracking()
            .OrderByDescending(p => p.IsActive)
            .ThenByDescending(p => p.UpdatedAt ?? p.CreatedAt)
            .ToListAsync();
        var redacted = rows.Select(r => new
        {
            r.Id,
            r.ProviderType,
            ProviderTypeName = r.ProviderType.ToString(),
            r.DisplayName,
            r.IsActive,
            r.IsEnabled,
            r.Endpoint,
            // Mask key — show only the last 4 chars. Admin who needs
            // to verify they pasted the right key can re-paste.
            ApiKeyMasked = MaskKey(r.ApiKey),
            HasApiKey = !string.IsNullOrEmpty(r.ApiKey),
            r.Model,
            r.Temperature,
            r.MaxOutputTokens,
            r.RequestTimeoutSeconds,
            r.DailyCallCap,
            r.MonthlyBudgetUsd,
            r.PricePerInputTokenUsd1M,
            r.PricePerOutputTokenUsd1M,
            r.LastTestedAt,
            r.LastTestStatus,
            r.CreatedAt,
            r.UpdatedAt,
        });
        return Ok(new ApiResponse<object>(true, new
        {
            providers = redacted,
            supportedTypes = Enum.GetValues<AiProviderType>()
                .Select(t => new
                {
                    value = (int)t,
                    name = t.ToString(),
                    available = _providers.Any(p => p.Kind == t),
                }),
        }));
    }

    public sealed record UpsertProviderRequest(
        Guid? Id, AiProviderType ProviderType, string DisplayName,
        string? Endpoint, string? ApiKey, string Model,
        decimal Temperature, int MaxOutputTokens, int RequestTimeoutSeconds,
        int? DailyCallCap, decimal? MonthlyBudgetUsd,
        decimal? PricePerInputTokenUsd1M, decimal? PricePerOutputTokenUsd1M,
        bool IsEnabled);

    [HttpPost("providers")]
    public async Task<ActionResult<ApiResponse<object>>> UpsertProvider([FromBody] UpsertProviderRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DisplayName))
            return BadRequest(new ApiResponse<object>(false, null, "DisplayName ห้ามว่าง"));
        if (string.IsNullOrWhiteSpace(req.Model))
            return BadRequest(new ApiResponse<object>(false, null, "Model ห้ามว่าง"));

        AiProviderConfig? row;
        if (req.Id.HasValue)
        {
            row = await _db.AiProviderConfigs.FirstOrDefaultAsync(p => p.Id == req.Id.Value);
            if (row == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ provider"));
        }
        else
        {
            row = new AiProviderConfig();
            _db.AiProviderConfigs.Add(row);
        }

        row.ProviderType = req.ProviderType;
        row.DisplayName = req.DisplayName.Trim();
        row.Endpoint = string.IsNullOrWhiteSpace(req.Endpoint) ? null : req.Endpoint.Trim();
        // Empty ApiKey on update = keep existing (so admin doesn't have
        // to re-paste every time they tweak temperature). Only overwrite
        // when a fresh non-empty key is supplied.
        if (!string.IsNullOrWhiteSpace(req.ApiKey))
            row.ApiKey = req.ApiKey.Trim();
        row.Model = req.Model.Trim();
        row.Temperature = Math.Clamp(req.Temperature, 0m, 2m);
        row.MaxOutputTokens = Math.Clamp(req.MaxOutputTokens, 16, 32_768);
        row.RequestTimeoutSeconds = Math.Clamp(req.RequestTimeoutSeconds, 2, 120);
        row.DailyCallCap = req.DailyCallCap;
        row.MonthlyBudgetUsd = req.MonthlyBudgetUsd;
        row.PricePerInputTokenUsd1M = req.PricePerInputTokenUsd1M;
        row.PricePerOutputTokenUsd1M = req.PricePerOutputTokenUsd1M;
        row.IsEnabled = req.IsEnabled;
        row.UpdatedBy = User.Identity?.Name;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { id = row.Id }, "บันทึก provider สำเร็จ"));
    }

    [HttpPost("providers/{id:guid}/activate")]
    public async Task<ActionResult<ApiResponse<object>>> Activate(Guid id)
    {
        var target = await _db.AiProviderConfigs.FirstOrDefaultAsync(p => p.Id == id);
        if (target == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ provider"));
        if (!target.IsEnabled)
            return BadRequest(new ApiResponse<object>(false, null,
                "Provider ถูก disabled อยู่ — กด enable ก่อน"));

        // Two-step within a transaction so the partial unique index on
        // IsActive=true never sees two rows at once.
        using var tx = await _db.Database.BeginTransactionAsync();
        await _db.AiProviderConfigs.Where(p => p.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false));
        target.IsActive = true;
        target.UpdatedAt = DateTime.UtcNow;
        target.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return Ok(new ApiResponse<object>(true, null, $"ตั้ง '{target.DisplayName}' เป็น active provider แล้ว"));
    }

    [HttpDelete("providers/{id:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id)
    {
        var row = await _db.AiProviderConfigs.FirstOrDefaultAsync(p => p.Id == id);
        if (row == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ provider"));
        if (row.IsActive)
            return BadRequest(new ApiResponse<object>(false, null,
                "ไม่สามารถลบ active provider — กรุณา activate ตัวอื่นก่อน"));
        row.IsDeleted = true;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, "ลบ provider แล้ว"));
    }

    /// <summary>Pings the provider with a 20-token round-trip. Cost is
    /// rounding error; result stored on LastTestedAt / LastTestStatus
    /// so the admin UI shows status badge.</summary>
    [HttpPost("providers/{id:guid}/test")]
    public async Task<ActionResult<ApiResponse<object>>> TestProvider(Guid id, CancellationToken ct)
    {
        var row = await _db.AiProviderConfigs.FirstOrDefaultAsync(p => p.Id == id);
        if (row == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ provider"));
        var impl = _providers.FirstOrDefault(p => p.Kind == row.ProviderType);
        if (impl == null)
            return BadRequest(new ApiResponse<object>(false, null,
                $"Provider type {row.ProviderType} ไม่ได้ register ในระบบ"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var (ok, error) = await impl.TestConnectionAsync(row, cts.Token);

        row.LastTestedAt = DateTime.UtcNow;
        row.LastTestStatus = ok ? "OK" : (error ?? "FAIL");
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, new { ok, error },
            ok ? "เชื่อมต่อสำเร็จ" : $"ไม่สำเร็จ: {error}"));
    }

    // ────────────────────────────────────────────────────────────────
    //  Master settings (SiteSettings AI fields)
    // ────────────────────────────────────────────────────────────────

    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<object>>> GetSettings()
    {
        var s = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync();
        return Ok(new ApiResponse<object>(true, new
        {
            aiAugmentationEnabled = s?.AiAugmentationEnabled ?? false,
            aiReviewConfidenceThreshold = s?.AiReviewConfidenceThreshold ?? 0.65m,
            aiSamplingRate = s?.AiSamplingRate ?? 0.10m,
            aiDefaultCacheTtlDays = s?.AiDefaultCacheTtlDays ?? 30,
            aiStripPiiInPrompts = s?.AiStripPiiInPrompts ?? true,
            aiVerifyAgainstThaiComplianceRules = s?.AiVerifyAgainstThaiComplianceRules ?? true,
            aiLastFeedbackTrainingAt = s?.AiLastFeedbackTrainingAt,
        }));
    }

    public sealed record UpdateSettingsRequest(
        bool AiAugmentationEnabled,
        decimal AiReviewConfidenceThreshold,
        decimal AiSamplingRate,
        int AiDefaultCacheTtlDays,
        bool AiStripPiiInPrompts,
        bool AiVerifyAgainstThaiComplianceRules);

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<object>>> UpdateSettings([FromBody] UpdateSettingsRequest req)
    {
        var s = await _db.SiteSettings.FirstOrDefaultAsync();
        if (s == null) { s = new SiteSettings(); _db.SiteSettings.Add(s); }
        s.AiAugmentationEnabled = req.AiAugmentationEnabled;
        s.AiReviewConfidenceThreshold = Math.Clamp(req.AiReviewConfidenceThreshold, 0m, 1m);
        s.AiSamplingRate = Math.Clamp(req.AiSamplingRate, 0m, 1m);
        s.AiDefaultCacheTtlDays = Math.Clamp(req.AiDefaultCacheTtlDays, 1, 365);
        s.AiStripPiiInPrompts = req.AiStripPiiInPrompts;
        s.AiVerifyAgainstThaiComplianceRules = req.AiVerifyAgainstThaiComplianceRules;
        s.UpdatedAt = DateTime.UtcNow;
        s.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, "บันทึก settings สำเร็จ"));
    }

    // ────────────────────────────────────────────────────────────────
    //  Usage + accuracy widgets
    // ────────────────────────────────────────────────────────────────

    /// <summary>Mirrors /admin/ocr/azure-usage — system-wide AI burn for
    /// the current calendar month with linear-extrapolation forecast.</summary>
    [HttpGet("usage")]
    public async Task<ActionResult<ApiResponse<object>>> GetUsage([FromQuery] decimal? usdToThb = 36.5m)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEnd = monthStart.AddMonths(1);
        var daysInMonth = (monthEnd - monthStart).TotalDays;
        var daysElapsed = Math.Max(1, (now - monthStart).TotalDays);

        var monthRows = await _db.AiUsageDailies.AsNoTracking()
            .Where(u => u.UsageDate >= monthStart)
            .ToListAsync();
        var calls = monthRows.Sum(r => r.CallsAttempted);
        var success = monthRows.Sum(r => r.CallsSuccessful);
        var cached = monthRows.Sum(r => r.CallsCached);
        var failed = monthRows.Sum(r => r.CallsFailed);
        var blocked = monthRows.Sum(r => r.CallsBudgetBlocked);
        var inTok = monthRows.Sum(r => r.InputTokensTotal);
        var outTok = monthRows.Sum(r => r.OutputTokensTotal);
        var costUsd = monthRows.Sum(r => r.CostUsdTotal);
        var fx = usdToThb ?? 36.5m;
        var costThb = Math.Round(costUsd * fx, 2);

        var forecastCalls = (int)Math.Ceiling(calls * daysInMonth / daysElapsed);
        var forecastUsd = Math.Round(costUsd * (decimal)daysInMonth / (decimal)daysElapsed, 4);
        var forecastThb = Math.Round(forecastUsd * fx, 2);

        var byFeature = monthRows
            .GroupBy(r => r.FeatureKey)
            .Select(g => new
            {
                feature = g.Key,
                calls = g.Sum(x => x.CallsAttempted),
                successful = g.Sum(x => x.CallsSuccessful),
                cached = g.Sum(x => x.CallsCached),
                failed = g.Sum(x => x.CallsFailed),
                cost = g.Sum(x => x.CostUsdTotal),
                avgLatency = g.Any(x => x.CallsSuccessful > 0) ? g.Average(x => x.AvgLatencyMs) : 0,
            })
            .OrderByDescending(x => x.calls)
            .ToList();

        var cacheHitRatio = (calls + cached) > 0 ? Math.Round((double)cached / (calls + cached) * 100, 1) : 0;
        var successRate = calls > 0 ? Math.Round((double)success / calls * 100, 1) : 0;

        var activeProvider = await _db.AiProviderConfigs.AsNoTracking()
            .Where(p => p.IsActive && p.IsEnabled)
            .Select(p => new { p.DisplayName, p.ProviderType, p.Model, p.MonthlyBudgetUsd, p.DailyCallCap })
            .FirstOrDefaultAsync();

        return Ok(new ApiResponse<object>(true, new
        {
            month = monthStart.ToString("yyyy-MM"),
            daysElapsed = (int)daysElapsed,
            daysInMonth = (int)daysInMonth,
            activeProvider,
            totals = new
            {
                calls,
                successful = success,
                cached,
                failed,
                budgetBlocked = blocked,
                inputTokens = inTok,
                outputTokens = outTok,
                cacheHitRatio,
                successRate,
            },
            cost = new
            {
                currentMonthUsd = costUsd,
                currentMonthThb = costThb,
                forecastFullMonthUsd = forecastUsd,
                forecastFullMonthThb = forecastThb,
                forecastFullMonthCalls = forecastCalls,
                usdToThb = fx,
            },
            byFeature,
        }));
    }

    /// <summary>Per-feature accuracy comparison (Local vs AI vs User
    /// agreement). Drives the LocalModelHealth admin table.</summary>
    [HttpGet("accuracy")]
    public async Task<ActionResult<ApiResponse<object>>> GetAccuracy([FromQuery] int days = 30)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(7, Math.Min(days, 180)));
        var rows = await _db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.CreatedAt >= cutoff && f.UserChosenAt != null)
            .GroupBy(f => f.FeatureKey)
            .Select(g => new
            {
                feature = g.Key,
                samples = g.Count(),
                aiCorrect = g.Count(f => f.UserAcceptedAi == true),
                localCorrect = g.Count(f => f.LocalModelAnswer != null && f.LocalModelAnswer == f.UserChosenAnswer),
                aiAgreesLocal = g.Count(f => f.AiPrimaryAnswer != null && f.LocalModelAnswer != null
                                              && f.AiPrimaryAnswer == f.LocalModelAnswer),
                avgAiConfidence = g.Average(f => f.AiConfidence) ?? 0m,
                avgLocalConfidence = g.Average(f => f.LocalModelConfidence) ?? 0m,
            })
            .OrderByDescending(x => x.samples)
            .ToListAsync();

        var enriched = rows.Select(r => new
        {
            r.feature,
            r.samples,
            aiAccuracy = r.samples > 0 ? Math.Round(100.0 * r.aiCorrect / r.samples, 1) : 0,
            localAccuracy = r.samples > 0 ? Math.Round(100.0 * r.localCorrect / r.samples, 1) : 0,
            agreementRate = r.samples > 0 ? Math.Round(100.0 * r.aiAgreesLocal / r.samples, 1) : 0,
            r.avgAiConfidence,
            r.avgLocalConfidence,
        });

        var health = await _db.LocalModelHealths.AsNoTracking().ToListAsync();
        return Ok(new ApiResponse<object>(true, new
        {
            days,
            features = enriched,
            health = health.Select(h => new
            {
                h.FeatureKey,
                h.LocalModelVersion,
                h.SamplesLast30d,
                h.LocalAccuracy30d,
                h.AiAccuracy30d,
                h.AgreementRate30d,
                h.LastEvaluatedAt,
                Status = h.Status.ToString(),
                h.Recommendation,
            }),
        }));
    }

    /// <summary>Recent feedback rows for debugging — admin can see what
    /// the AI actually returned for a given doc and how the user voted.</summary>
    [HttpGet("feedback")]
    public async Task<ActionResult<ApiResponse<object>>> GetRecentFeedback(
        [FromQuery] int limit = 50, [FromQuery] string? feature = null)
    {
        var q = _db.AiSuggestionFeedbacks.AsNoTracking();
        if (!string.IsNullOrEmpty(feature)) q = q.Where(f => f.FeatureKey == feature);
        var rows = await q.OrderByDescending(f => f.CreatedAt).Take(Math.Clamp(limit, 1, 200))
            .Select(f => new
            {
                f.Id,
                f.CompanyId,
                f.FeatureKey,
                Status = f.Status.ToString(),
                Provider = f.ProviderUsed.ToString(),
                f.ModelVersion,
                f.AiPrimaryAnswer,
                f.AiConfidence,
                f.LocalModelAnswer,
                f.LocalModelConfidence,
                f.UserChosenAnswer,
                f.UserAcceptedAi,
                f.LatencyMs,
                f.CostUsd,
                f.SourceEntityType,
                f.SourceEntityId,
                f.ErrorMessage,
                f.CreatedAt,
            })
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, rows));
    }

    private static string MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (key.Length <= 8) return new string('*', key.Length);
        return new string('*', key.Length - 4) + key[^4..];
    }
}
