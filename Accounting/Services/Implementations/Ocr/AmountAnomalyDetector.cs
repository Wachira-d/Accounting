namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Statistical anomaly detection on monetary amounts using two
/// complementary methods, both robust against the heavy-tailed
/// distribution of real-world business amounts:
///
///   1. z-score on log-amount — assumes log-normal distribution, which
///      empirically fits Thai SME invoice totals far better than raw
///      Gaussian (mean is pulled by occasional 6-figure capex while
///      typical bills are 3–5 figures).
///   2. Modified z-score using MAD (Median Absolute Deviation) — works
///      with small samples where stddev is unreliable. Threshold |z| > 3.5
///      is the common-practice cutoff.
///
/// Returns:
///   • Score in (−∞, +∞); positive means above typical, negative below.
///   • IsAnomaly when |score| exceeds the threshold.
///
/// Used by VendorIntelligenceService.PredictAsync to flag scans where
/// the extracted Total deviates from the vendor's historical pattern —
/// catches OCR misreads ("฿29,040" mis-OCR'd as "฿290,400") AND genuinely
/// unusual purchases that the user should sanity-check.
/// </summary>
public static class AmountAnomalyDetector
{
    public record AnomalyResult(
        decimal Score,
        bool IsAnomaly,
        string Reason);

    /// <summary>Modified z-score using MAD — robust to outliers in the
    /// training data itself.</summary>
    public static AnomalyResult? CheckModifiedZScore(decimal amount, IReadOnlyList<decimal> history, decimal threshold = 3.5m)
    {
        if (amount <= 0 || history == null || history.Count < 3) return null;
        var logs = history.Where(a => a > 0).Select(a => Math.Log((double)a)).OrderBy(x => x).ToList();
        if (logs.Count < 3) return null;

        var median = Median(logs);
        var deviations = logs.Select(x => Math.Abs(x - median)).OrderBy(x => x).ToList();
        var mad = Median(deviations);
        if (mad < 1e-6) mad = 1e-6;  // avoid divide-by-zero on constant history

        var z = 0.6745 * (Math.Log((double)amount) - median) / mad;
        var isAnomaly = Math.Abs(z) > (double)threshold;
        var reason = isAnomaly
            ? (z > 0
                ? $"ยอด {amount:N2} สูงผิดปกติ (modified z-score {z:F2}, ปกติประมาณ {Math.Exp(median):N0})"
                : $"ยอด {amount:N2} ต่ำผิดปกติ (modified z-score {z:F2}, ปกติประมาณ {Math.Exp(median):N0})")
            : "ยอดอยู่ในช่วงปกติของผู้ขายรายนี้";
        return new AnomalyResult((decimal)z, isAnomaly, reason);
    }

    /// <summary>Classic z-score using running mean/stddev — cheap to
    /// compute incrementally so we use it on every train pass to keep
    /// VendorIntel's stored stats up to date.</summary>
    public static AnomalyResult? CheckZScore(decimal amount, decimal? mean, decimal? stddev, decimal threshold = 3m)
    {
        if (!mean.HasValue || !stddev.HasValue || stddev.Value <= 0 || amount <= 0) return null;
        var logAmount = (decimal)Math.Log((double)amount);
        var logMean = (decimal)Math.Log((double)mean.Value);
        var z = (logAmount - logMean) / stddev.Value;
        var isAnomaly = Math.Abs(z) > threshold;
        var reason = isAnomaly
            ? (z > 0
                ? $"ยอด {amount:N2} สูงผิดปกติ (z={z:F2}, ค่าเฉลี่ย {mean:N0})"
                : $"ยอด {amount:N2} ต่ำผิดปกติ (z={z:F2}, ค่าเฉลี่ย {mean:N0})")
            : "ยอดอยู่ในช่วงปกติของผู้ขายรายนี้";
        return new AnomalyResult(z, isAnomaly, reason);
    }

    /// <summary>Welford's online algorithm — update running mean/M2
    /// incrementally when a new sample arrives. Better numerical
    /// stability than the textbook "ΣX, ΣX²" approach.</summary>
    public static (decimal NewMean, decimal NewVariance) UpdateWelford(
        decimal oldMean, decimal oldVariance, int oldN, decimal newSample)
    {
        var n = oldN + 1;
        var delta = newSample - oldMean;
        var newMean = oldMean + delta / n;
        var delta2 = newSample - newMean;
        var newM2 = oldVariance * Math.Max(1, oldN - 1) + delta * delta2;
        var newVariance = n > 1 ? newM2 / (n - 1) : 0m;
        return (newMean, newVariance);
    }

    private static double Median(List<double> sorted)
    {
        int n = sorted.Count;
        return n % 2 == 0 ? (sorted[n / 2 - 1] + sorted[n / 2]) / 2 : sorted[n / 2];
    }
}
