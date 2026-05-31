namespace Accounting.Services.Implementations.Forecast;

/// <summary>
/// Holt-Winters triple-exponential smoothing — multiplicative variant.
/// SARIMA's lighter cousin: captures level + trend + seasonal pattern
/// (weekly OR monthly cycle) from a univariate series, with no external
/// dependency. Used by the cash-flow forecaster to project AR/AP inflows
/// instead of "sum of due-dated invoices in week X".
///
/// Why Holt-Winters over plain SMA:
///   • Picks up seasonality (Mondays look different from Saturdays;
///     month-end spike from salary payments).
///   • Adapts to trend (a growing business shouldn't be flat-projected).
///   • Provides α/β/γ smoothing constants — α≈0.3, β≈0.05, γ≈0.1 by
///     default; can be grid-searched per company via SSE minimisation.
///
/// Multiplicative seasonality (vs additive) was chosen because revenue
/// amounts scale with level — a +30% Monday vs the week's average
/// stays a +30% Monday as the business grows, not a fixed +5,000 baht.
///
/// Returns the next-N forecast plus the in-sample SSE so callers can
/// decide whether the model is trustworthy (low SSE → high confidence).
/// </summary>
public static class HoltWintersForecaster
{
    public sealed record Result(
        IReadOnlyList<decimal> Forecast,
        decimal Alpha, decimal Beta, decimal Gamma,
        decimal SumSquaredError,
        decimal MeanAbsolutePercentError);

    /// <summary>Coarse grid search over α/β/γ → re-fit at the best
    /// combo. SSE-minimising; ~125 evaluations (5×5×5 grid). Use when
    /// you have ≥3 seasons of history and the caller wants the model
    /// tuned to this specific company's pattern rather than the
    /// global default. Falls back to fixed defaults for short series.
    ///
    /// Each grid point is cheap (one pass over the series, O(n)) so
    /// the full search on 12 weekly buckets finishes in well under
    /// 1ms. We deliberately do NOT do gradient descent — the SSE
    /// surface for HW is non-convex with multiple local minima and a
    /// dense grid is more robust than getting stuck in a bad valley.</summary>
    public static Result ForecastAuto(
        IReadOnlyList<decimal> series, int seasonLength, int horizon)
    {
        if (series == null || series.Count < 3 * seasonLength || seasonLength < 2)
            return Forecast(series ?? Array.Empty<decimal>(), seasonLength, horizon);

        var aGrid = new[] { 0.1m, 0.2m, 0.3m, 0.5m, 0.7m };
        var bGrid = new[] { 0.01m, 0.05m, 0.1m, 0.2m, 0.4m };
        var gGrid = new[] { 0.05m, 0.1m, 0.2m, 0.4m, 0.6m };

        Result? best = null;
        foreach (var a in aGrid)
            foreach (var b in bGrid)
                foreach (var g in gGrid)
                {
                    var r = Forecast(series, seasonLength, horizon, a, b, g);
                    if (best == null || r.SumSquaredError < best.SumSquaredError) best = r;
                }
        return best!;
    }

    /// <summary>Fit and forecast `horizon` steps ahead. `seasonLength`
    /// is the cycle (7 for weekly, 12 for monthly, etc.). Series must
    /// have at least 2 full seasons (2 × seasonLength points) — short
    /// series fall back to simple exponential smoothing.</summary>
    public static Result Forecast(
        IReadOnlyList<decimal> series,
        int seasonLength,
        int horizon,
        decimal? alpha = null, decimal? beta = null, decimal? gamma = null)
    {
        if (series == null || series.Count == 0)
            return new Result(Array.Empty<decimal>(), 0, 0, 0, 0, 0);
        if (horizon <= 0) horizon = 1;

        // Need ≥2 seasons for the multiplicative variant — fall back
        // to single exponential smoothing (level only) otherwise.
        if (series.Count < 2 * seasonLength || seasonLength < 2)
            return SingleExp(series, horizon, alpha ?? 0.3m);

        var a = alpha ?? 0.3m;
        var b = beta  ?? 0.05m;
        var g = gamma ?? 0.10m;

        // Initial level = mean of first season.
        var level = series.Take(seasonLength).Average();

        // Initial trend = avg(season2 - season1) / seasonLength.
        decimal trend = 0m;
        for (int i = 0; i < seasonLength; i++)
            trend += (series[i + seasonLength] - series[i]) / seasonLength;
        trend /= seasonLength;

        // Initial seasonal indices = series[i] / mean(first season).
        // Guard against zero level (a dormant series); flat 1.0 indices.
        var seasonals = new decimal[seasonLength];
        for (int i = 0; i < seasonLength; i++)
            seasonals[i] = level == 0 ? 1m : series[i] / level;

        // Smoothing pass. Track SSE + MAPE on in-sample one-step-ahead.
        decimal sse = 0m;
        decimal mapeSum = 0m;
        int mapeCount = 0;
        for (int t = 0; t < series.Count; t++)
        {
            var sIdx = t % seasonLength;
            var seasonal = seasonals[sIdx] == 0 ? 1m : seasonals[sIdx];
            var oneStep = (level + trend) * seasonal;

            var err = series[t] - oneStep;
            sse += err * err;
            if (series[t] != 0)
            {
                mapeSum += Math.Abs(err / series[t]);
                mapeCount++;
            }

            var newLevel = a * (series[t] / seasonal) + (1 - a) * (level + trend);
            var newTrend = b * (newLevel - level) + (1 - b) * trend;
            var newSeasonal = newLevel == 0 ? seasonals[sIdx]
                : g * (series[t] / newLevel) + (1 - g) * seasonals[sIdx];

            level = newLevel;
            trend = newTrend;
            seasonals[sIdx] = newSeasonal;
        }

        // Project forward — seasonal index cycles.
        var forecast = new decimal[horizon];
        for (int h = 1; h <= horizon; h++)
        {
            var sIdx = (series.Count - 1 + h) % seasonLength;
            forecast[h - 1] = Math.Max(0m, (level + h * trend) * seasonals[sIdx]);
        }

        var mape = mapeCount > 0 ? mapeSum / mapeCount : 0m;
        return new Result(forecast, a, b, g, sse, mape);
    }

    private static Result SingleExp(IReadOnlyList<decimal> series, int horizon, decimal alpha)
    {
        decimal level = series[0];
        decimal sse = 0m, mapeSum = 0m;
        int mapeCount = 0;
        for (int t = 1; t < series.Count; t++)
        {
            var err = series[t] - level;
            sse += err * err;
            if (series[t] != 0) { mapeSum += Math.Abs(err / series[t]); mapeCount++; }
            level = alpha * series[t] + (1 - alpha) * level;
        }
        var fc = new decimal[horizon];
        for (int i = 0; i < horizon; i++) fc[i] = Math.Max(0m, level);
        var mape = mapeCount > 0 ? mapeSum / mapeCount : 0m;
        return new Result(fc, alpha, 0m, 0m, sse, mape);
    }
}
