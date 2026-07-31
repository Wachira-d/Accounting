using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// Two-line defence against runaway AI cost:
///   1. Daily call cap (count of calls so far today vs AiProviderConfig.DailyCallCap)
///   2. Monthly USD budget (sum of CostUsd so far this month vs MonthlyBudgetUsd)
///
/// Returns Allowed/Blocked/Warning so the orchestrator can either skip
/// the call entirely or proceed-but-flag for the admin widget. Result
/// is cached for 30 seconds — the guard runs on every call so a tight
/// loop of 1000 calls shouldn't hit the DB 1000 times for the same
/// monthly sum.
/// </summary>
public interface IAiBudgetGuard
{
    Task<BudgetDecision> EvaluateAsync(Guid activeProviderConfigId, int? dailyCallCap,
        decimal? monthlyBudgetUsd, CancellationToken ct);
}

public sealed record BudgetDecision(
    bool Allowed,
    bool NearLimit,           // ≥ 80% — surface warning on admin widget
    string? BlockedReason,
    int CallsToday,
    decimal CostThisMonth);

public class AiBudgetGuard : IAiBudgetGuard
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<AiBudgetGuard> _logger;

    // Tight in-memory cache to avoid 1000 DB queries on a tight loop.
    // Refresh every 30s; that's accurate enough for budget enforcement.
    private static readonly object _lock = new();
    private static DateTime _cacheExpiresAt = DateTime.MinValue;
    private static int _cachedCallsToday;
    private static decimal _cachedCostThisMonth;

    public AiBudgetGuard(AccountingDbContext db, ILogger<AiBudgetGuard> logger)
    { _db = db; _logger = logger; }

    public async Task<BudgetDecision> EvaluateAsync(Guid activeProviderConfigId, int? dailyCallCap,
        decimal? monthlyBudgetUsd, CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            int callsToday;
            decimal costMonth;

            lock (_lock)
            {
                if (_cacheExpiresAt > now)
                {
                    callsToday = _cachedCallsToday;
                    costMonth = _cachedCostThisMonth;
                }
                else
                {
                    callsToday = -1;
                    costMonth = -1m;
                }
            }

            if (callsToday < 0)
            {
                var todayUtc = now.Date;
                var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

                // นับเฉพาะ call ที่ "ยิง provider จริง" — ทุก path ของ orchestrator
                // เขียนแถว feedback หมด (local short-circuit, cache hit, skip,
                // budget-exceeded, synthetic child rows ของ bulk match) ถ้านับทั้งหมด
                // cap จะถูกกินโดยงานที่ไม่ได้เสียเงิน และยิ่ง local model แม่นขึ้น
                // (ยิง provider น้อยลง) cap ยิ่งเต็มเร็วขึ้น — ตรงข้ามกับเจตนา
                var billableStatuses = new[]
                {
                    AiCallStatus.Success, AiCallStatus.Failed, AiCallStatus.InvalidResponse,
                };
                callsToday = await _db.AiSuggestionFeedbacks
                    .Where(f => f.CreatedAt >= todayUtc && billableStatuses.Contains(f.Status))
                    .CountAsync(ct);
                costMonth = await _db.AiSuggestionFeedbacks
                    .Where(f => f.CreatedAt >= monthStart && f.CostUsd != null)
                    .SumAsync(f => f.CostUsd ?? 0m, ct);

                lock (_lock)
                {
                    _cachedCallsToday = callsToday;
                    _cachedCostThisMonth = costMonth;
                    _cacheExpiresAt = now.AddSeconds(30);
                }
            }

            if (dailyCallCap.HasValue && callsToday >= dailyCallCap.Value)
                return new BudgetDecision(false, false,
                    $"แตะ daily call cap แล้ว ({callsToday}/{dailyCallCap})", callsToday, costMonth);

            if (monthlyBudgetUsd.HasValue && costMonth >= monthlyBudgetUsd.Value * 0.95m)
            {
                if (costMonth >= monthlyBudgetUsd.Value)
                    return new BudgetDecision(false, true,
                        $"แตะ monthly USD budget แล้ว ({costMonth:F2}/{monthlyBudgetUsd:F2})",
                        callsToday, costMonth);
                // 95-100%: still allow but flag.
                return new BudgetDecision(true, true, null, callsToday, costMonth);
            }

            var nearDaily = dailyCallCap.HasValue && callsToday >= dailyCallCap.Value * 0.80;
            var nearMonthly = monthlyBudgetUsd.HasValue && costMonth >= monthlyBudgetUsd.Value * 0.80m;

            return new BudgetDecision(true, nearDaily || nearMonthly, null, callsToday, costMonth);
        }
        catch (Exception ex)
        {
            // If the budget guard ITSELF errors, fail open (allow the
            // call) so a transient DB hiccup doesn't disable AI for the
            // whole tenant. The actual provider may still reject it.
            _logger.LogError(ex, "AiBudgetGuard evaluation failed — failing open");
            return new BudgetDecision(true, false, null, 0, 0m);
        }
    }

    /// <summary>Invalidates the 30s cache. Called immediately after a
    /// feedback row is recorded so the next call sees the updated count.</summary>
    public static void InvalidateCache()
    {
        lock (_lock) { _cacheExpiresAt = DateTime.MinValue; }
    }
}
