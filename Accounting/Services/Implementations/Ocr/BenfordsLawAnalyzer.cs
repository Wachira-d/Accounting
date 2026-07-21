namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Benford's Law fraud / data-entry-error detector for monetary amounts.
///
/// Benford's Law observes that in many real-world numeric datasets, the
/// leading digit follows a logarithmic distribution: digit 1 appears as
/// the leading digit ~30.1% of the time, 2 ~17.6%, ..., 9 ~4.6%. Amounts
/// that humans fabricate (or that systems generate with hard caps /
/// rounding rules) tend to deviate substantially from this pattern.
///
/// Practical applications in this system:
///   • Audit per-vendor amount distribution: a supplier whose invoices
///     start with "9" 30% of the time (vs expected ~5%) is suspicious.
///   • Detect data-entry rounding habits: lots of "5xxx" / "1xxxx" that
///     never occur naturally → user is rounding instead of entering
///     actual amounts.
///   • Per-employee expense-claim screening for fraud risk.
///
/// We compute the chi-squared statistic against the Benford distribution.
/// Convention:
///   χ² ≤ 9   → conforms well
///   χ² ≤ 15  → mild deviation
///   χ² > 15  → notable deviation, worth investigating
/// </summary>
public static class BenfordsLawAnalyzer
{
    public record BenfordResult(
        decimal[] Observed,        // observed frequency of digits 1..9 (sums to 1.0)
        decimal[] Expected,        // Benford expected frequency 1..9
        decimal ChiSquared,
        bool ConformsWell,         // χ² ≤ 9
        bool NotableDeviation,     // χ² > 15
        int SampleSize,
        string Interpretation);

    // Benford expected leading-digit distribution
    public static readonly decimal[] Expected = new[]
    {
        0.3010m, 0.1761m, 0.1249m, 0.0969m, 0.0792m,
        0.0669m, 0.0580m, 0.0512m, 0.0458m
    };

    /// <summary>Analyze a list of amounts. Returns null when n < 30 —
    /// Benford's Law is only meaningful at that sample size.</summary>
    public static BenfordResult? Analyze(IReadOnlyList<decimal> amounts)
    {
        if (amounts == null) return null;
        var positive = amounts.Where(a => a > 0).ToList();
        if (positive.Count < 30) return null;

        var counts = new int[10];   // index 0 unused, 1..9 are digits
        foreach (var a in positive)
        {
            var first = LeadingDigit(a);
            if (first >= 1 && first <= 9) counts[first]++;
        }

        var n = (decimal)positive.Count;
        var observed = new decimal[9];
        decimal chi2 = 0m;
        for (int d = 1; d <= 9; d++)
        {
            observed[d - 1] = counts[d] / n;
            var exp = Expected[d - 1] * (decimal)positive.Count;
            var diff = counts[d] - (double)exp;
            chi2 += (decimal)(diff * diff / Math.Max((double)exp, 1));
        }

        var conforms = chi2 <= 9m;
        var notable = chi2 > 15m;
        var interp = notable
            ? $"⚠️ χ² = {chi2:F1} เกิน 15 — distribution ผิดปกติจาก Benford มาก ควรตรวจ data integrity / fraud"
            : conforms
                ? $"✓ χ² = {chi2:F1} อยู่ในช่วงปกติ (≤9) — amounts ผ่าน Benford's Law"
                : $"χ² = {chi2:F1} เบี่ยงเบนเล็กน้อย (9-15) — อาจมีรูปแบบการกรอกข้อมูล/round";
        return new BenfordResult(observed, Expected, chi2, conforms, notable, positive.Count, interp);
    }

    private static int LeadingDigit(decimal amount)
    {
        var s = amount.ToString("0.###############").TrimStart('-').TrimStart('0').TrimStart('.');
        foreach (var c in s)
        {
            if (char.IsDigit(c) && c != '0') return c - '0';
        }
        return 0;
    }
}
