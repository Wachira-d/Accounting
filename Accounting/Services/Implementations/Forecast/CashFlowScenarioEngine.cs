namespace Accounting.Services.Implementations.Forecast;

/// <summary>
/// Best/expected/worst scenario engine layered on top of the
/// Holt-Winters cash-flow forecast. The Forecast endpoint produces a
/// SINGLE point estimate per week; this engine widens that into a
/// confidence band using MAPE-derived multipliers + collection-delay
/// sensitivity.
///
/// Why this is more than ±X% bands:
///   • Best case = AR comes in slightly early + AP holds for full term.
///   • Worst case = AR collection slips by N days, AP can't be delayed.
/// The asymmetry matters — businesses lose cash more often than they
/// gain it, so symmetric bands underestimate downside risk.
///
/// Output feeds AiFeatureKey.ForecastNarrative — the local engine
/// supplies numerical scenarios; DeepSeek writes the prose summary
/// ("ถ้า client A จ่ายช้า 30 วัน ยอดเงินสดติด -X บาท ในสัปดาห์ที่ 3").
/// </summary>
public static class CashFlowScenarioEngine
{
    public sealed record WeekScenarios(
        int WeekNumber,
        DateTime WeekStart, DateTime WeekEnd,
        decimal Best,
        decimal Expected,
        decimal Worst,
        decimal RiskIndex);             // 0 (no risk) to 100 (cash crisis)

    public sealed record ScenarioReport(
        IReadOnlyList<WeekScenarios> Weeks,
        decimal MinClosingBalance,      // worst-case low point
        DateTime? MinClosingDate,       // when the low point hits
        decimal MaxClosingBalance,
        string OverallRisk,             // "Low" | "Moderate" | "High" | "Critical"
        IReadOnlyList<string> KeyRiskFactors);

    /// <summary>Generate scenarios from a base forecast.
    /// `weeks` is the (week, opening, expectedInflow, expectedOutflow)
    /// projection. `inflowMape` and `outflowMape` are the in-sample
    /// MAPE values from Holt-Winters — drive the band width.
    /// `collectionDelayDays` (typical AR drag) and `paymentAccelDays`
    /// (AP can't usually be deferred) flow into asymmetry.</summary>
    public static ScenarioReport Build(
        IReadOnlyList<(int Week, DateTime Start, DateTime End,
            decimal Opening, decimal ExpectedIn, decimal ExpectedOut)> baseWeeks,
        decimal inflowMape,
        decimal outflowMape,
        int collectionDelayDays = 14,
        int paymentAccelDays = 0)
    {
        if (baseWeeks.Count == 0)
            return new ScenarioReport(Array.Empty<WeekScenarios>(),
                0m, null, 0m, "Low", Array.Empty<string>());

        // Multipliers from MAPE — convert "average error" into
        // "1-σ band". Empirically MAPE × √2 ≈ 1σ for log-normal-ish
        // cash flow distributions. Cap at 50% so a horrible model
        // doesn't produce silly bands.
        var inBand = Math.Min(0.5m, inflowMape * 1.4m);
        var outBand = Math.Min(0.5m, outflowMape * 1.4m);

        // Collection-delay sensitivity: shift `collectionDelayDays / 7`
        // weeks of expected inflow into LATER weeks in the worst case.
        // Express as a "next-week-only" approximation since week
        // re-distribution gets complex; the magnitude is what matters
        // for narrative.
        var delayShare = Math.Min(1m, (decimal)collectionDelayDays / 7m / baseWeeks.Count);

        var weeks = new List<WeekScenarios>(baseWeeks.Count);
        decimal bestBal = baseWeeks[0].Opening;
        decimal expBal  = baseWeeks[0].Opening;
        decimal worstBal = baseWeeks[0].Opening;
        decimal minWorst = worstBal;
        DateTime? minWorstDate = null;
        decimal maxBest = bestBal;

        for (int i = 0; i < baseWeeks.Count; i++)
        {
            var (wk, start, end, _, eIn, eOut) = baseWeeks[i];

            // Best case: higher inflow + lower outflow (AP held).
            var bIn  = eIn  * (1 + inBand);
            var bOut = eOut * (1 - outBand) * (1 - (decimal)paymentAccelDays / 7m);

            // Worst case: lower inflow (delay-shifted) + higher outflow.
            var wIn  = eIn  * (1 - inBand) * (1 - delayShare);
            var wOut = eOut * (1 + outBand);

            bestBal  += bIn  - bOut;
            expBal   += eIn  - eOut;
            worstBal += wIn  - wOut;
            if (worstBal < minWorst) { minWorst = worstBal; minWorstDate = end; }
            if (bestBal > maxBest)   { maxBest = bestBal; }

            var riskIdx = ComputeRiskIndex(expBal, worstBal);
            weeks.Add(new WeekScenarios(wk, start, end,
                Best: Math.Round(bestBal, 0),
                Expected: Math.Round(expBal, 0),
                Worst: Math.Round(worstBal, 0),
                RiskIndex: Math.Round(riskIdx, 1)));
        }

        var overall = minWorst < 0 ? "Critical"
                    : minWorst < baseWeeks[0].Opening * 0.2m ? "High"
                    : minWorst < baseWeeks[0].Opening * 0.5m ? "Moderate"
                    : "Low";

        var risks = new List<string>();
        if (minWorst < 0)
            risks.Add($"Cash goes negative in worst case ({minWorstDate:yyyy-MM-dd}, low {minWorst:N0}).");
        if (inflowMape > 0.35m)
            risks.Add($"Inflow forecast volatile (MAPE {inflowMape:P0}); confidence low.");
        if (outflowMape > 0.35m)
            risks.Add($"Outflow forecast volatile (MAPE {outflowMape:P0}); large irregular payments.");
        if (collectionDelayDays > 30)
            risks.Add($"Collection delay buffer is {collectionDelayDays}d — typical AR is slow.");

        return new ScenarioReport(
            Weeks: weeks,
            MinClosingBalance: Math.Round(minWorst, 0),
            MinClosingDate: minWorstDate,
            MaxClosingBalance: Math.Round(maxBest, 0),
            OverallRisk: overall,
            KeyRiskFactors: risks);
    }

    private static decimal ComputeRiskIndex(decimal expected, decimal worst)
    {
        // Risk = how close worst-case is to zero, weighted against
        // expected balance. 0 = comfortably positive; 100 = worst < 0.
        if (worst >= expected) return 0m;
        if (worst <= 0) return 100m;
        if (expected <= 0) return 100m;
        return Math.Min(100m, Math.Max(0m, (1m - worst / expected) * 100m));
    }
}
