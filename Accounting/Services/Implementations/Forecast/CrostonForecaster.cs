namespace Accounting.Services.Implementations.Forecast;

/// <summary>
/// Croston's method for intermittent demand — the inventory-management
/// counterpart to Holt-Winters. Sales of any single SKU look like long
/// zero runs broken by occasional spikes ("sold 3 units on day 4, 0 for
/// 12 days, sold 5"); Holt-Winters can't handle that shape because the
/// seasonal component breaks when most observations are zero.
///
/// Croston decomposes the series into:
///   • Size  series Y_t — the non-zero demand magnitudes
///   • Inter-arrival τ_t — the gaps (in periods) between non-zero events
/// Each gets its own single-exp-smoothing forecast; the joint forecast
/// is `mean_size / mean_interval` = expected units per period.
///
/// This is THE textbook approach for slow-moving / spare parts; for
/// fast-moving SKUs (sales every day) the result collapses gracefully
/// to single-exp on the raw series — no separate code path needed.
///
/// Reference: Croston 1972, "Forecasting and stock control for
/// intermittent demands". Smoothing α defaults to 0.1 (industry-
/// standard for SKU forecasts where stability beats responsiveness).
/// </summary>
public static class CrostonForecaster
{
    public sealed record Result(
        /// <summary>Forecast units per period (the same unit the input
        /// was bucketed in — daily, weekly, etc.).</summary>
        decimal MeanDemandPerPeriod,
        /// <summary>Smoothed estimate of average gap between non-zero
        /// events. 1.0 = sold every period; ∞ when no history.</summary>
        decimal MeanInterArrival,
        /// <summary>Smoothed average non-zero quantity. Equal to
        /// MeanDemandPerPeriod × MeanInterArrival.</summary>
        decimal MeanNonZeroSize,
        /// <summary>0–1. 1 = sold every period (fast-mover); 0 = no
        /// non-zero observations in the input window.</summary>
        decimal Density);

    /// <summary>Forecast from a non-negative quantity series. Series
    /// elements represent units sold in equal-length periods. Returns
    /// zero forecast when there are no non-zero observations.</summary>
    public static Result Forecast(IReadOnlyList<decimal> series, decimal alpha = 0.1m)
    {
        if (series == null || series.Count == 0)
            return new Result(0m, 0m, 0m, 0m);

        // Walk the series. First non-zero seeds both smoothers.
        decimal? smoothedSize = null;
        decimal? smoothedInterval = null;
        int gapSincePrevHit = 0;
        int nonZeroCount = 0;

        foreach (var q in series)
        {
            gapSincePrevHit++;
            if (q <= 0) continue;

            nonZeroCount++;
            if (smoothedSize == null)
            {
                // Initialise on first hit.
                smoothedSize = q;
                smoothedInterval = gapSincePrevHit;
            }
            else
            {
                // smoothedSize / smoothedInterval are initialised together
                // on the first hit (above), so by this branch both are
                // non-null. Local copy quiets the NRT analysis without a
                // bang operator.
                var prevSize = smoothedSize ?? 0m;
                var prevInterval = smoothedInterval ?? 0m;
                smoothedSize = alpha * q + (1 - alpha) * prevSize;
                smoothedInterval = alpha * gapSincePrevHit + (1 - alpha) * prevInterval;
            }
            gapSincePrevHit = 0;
        }

        if (smoothedSize == null || smoothedInterval == null || smoothedInterval.Value == 0)
            return new Result(0m, 0m, 0m, 0m);

        var perPeriod = smoothedSize.Value / smoothedInterval.Value;
        var density = (decimal)nonZeroCount / series.Count;
        return new Result(perPeriod, smoothedInterval.Value, smoothedSize.Value, density);
    }

    /// <summary>Reorder point = lead-time demand + safety stock.
    /// Safety stock uses the standard "σ × z" formula where σ is
    /// std-dev of period demand, z is the service-level multiplier
    /// (1.645 → 95%, 1.96 → 97.5%, 2.33 → 99%). For Croston-style
    /// intermittent demand the variance is dominated by the size
    /// component; a coefficient-of-variation cap of 2.0 prevents the
    /// formula running away on very lumpy SKUs.</summary>
    public static decimal ReorderPoint(Result fc, decimal leadTimePeriods, decimal serviceLevelZ = 1.645m)
    {
        if (fc.MeanDemandPerPeriod <= 0 || leadTimePeriods <= 0) return 0m;
        var leadDemand = fc.MeanDemandPerPeriod * leadTimePeriods;
        // Approximate σ ≈ size_std × √leadTime / interval. We don't
        // track the unbiased σ here — use mean as a robust proxy
        // capped at CV=2 so the buffer doesn't dwarf the lead-time
        // demand on very intermittent SKUs.
        var sigmaPerPeriod = Math.Min(fc.MeanNonZeroSize / Math.Max(fc.MeanInterArrival, 1m),
            fc.MeanDemandPerPeriod * 2m);
        var sigmaLead = sigmaPerPeriod * (decimal)Math.Sqrt((double)leadTimePeriods);
        return Math.Ceiling(leadDemand + serviceLevelZ * sigmaLead);
    }
}
