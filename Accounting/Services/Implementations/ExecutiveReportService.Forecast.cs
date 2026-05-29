using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Forecast;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<CashFlowForecastResponse> GetCashFlowForecastAsync(Guid companyId, int days)
    {
        if (days < 7) days = 30;
        if (days > 180) days = 180;

        var asOf = DateTime.UtcNow.Date;
        var horizon = asOf.AddDays(days);
        var openingCash = await GetCashAndBankAsync(companyId, asOf);

        // ── Explicit dated AR/AP (current behaviour) ──────────────────
        // These are receivables/payables we already know about. Holt-
        // Winters only fills in the "what about recurring revenue /
        // recurring expense we don't have a document for yet" gap.
        var ar = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.Invoice
                     || d.DocumentType == DocumentType.TaxInvoice
                     || d.DocumentType == DocumentType.BillingNote)
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.BalanceDue > 0)
            .Select(d => new { d.DueDate, d.DocumentDate, d.BalanceDue, d.DocumentNumber })
            .ToListAsync();

        var ap = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.PurchaseInvoice
                     || d.DocumentType == DocumentType.Expense)
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.BalanceDue > 0)
            .Select(d => new { d.DueDate, d.DocumentDate, d.BalanceDue, d.DocumentNumber })
            .ToListAsync();

        // ── Holt-Winters projection from settled-payment history ─────
        // Last 12 weeks of Payment by direction. Weekly buckets +
        // seasonLength=4 captures the monthly cycle (rent / salary /
        // utilities tend to land in the same week-of-month). Falls back
        // to single-exp smoothing when history is thin (<8 weeks).
        var historyWeeks = 12;
        var historyStart = asOf.AddDays(-7 * historyWeeks);
        var paymentHistory = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                        && p.PaymentDate >= historyStart && p.PaymentDate < asOf)
            .Select(p => new { p.PaymentDate, p.Amount, p.Document.DocumentType })
            .ToListAsync();

        var inflowSeries = BucketWeekly(paymentHistory
            .Where(p => p.DocumentType == DocumentType.Invoice
                     || p.DocumentType == DocumentType.TaxInvoice
                     || p.DocumentType == DocumentType.BillingNote
                     || p.DocumentType == DocumentType.Receipt)
            .Select(p => (p.PaymentDate, p.Amount)),
            historyStart, historyWeeks);
        var outflowSeries = BucketWeekly(paymentHistory
            .Where(p => p.DocumentType == DocumentType.PurchaseInvoice
                     || p.DocumentType == DocumentType.Expense)
            .Select(p => (p.PaymentDate, p.Amount)),
            historyStart, historyWeeks);

        var weekCount = (int)Math.Ceiling(days / 7.0);
        // ForecastAuto grid-searches α/β/γ per series so each company
        // gets parameters tuned to its own seasonality strength. Cheap
        // (~125 fits × O(n)) for our 12-week buckets.
        var inflowFc = HoltWintersForecaster.ForecastAuto(inflowSeries, seasonLength: 4, horizon: weekCount);
        var outflowFc = HoltWintersForecaster.ForecastAuto(outflowSeries, seasonLength: 4, horizon: weekCount);

        // Confidence label on the model — MAPE under 20% is "good
        // enough to trust the recurring forecast", over 50% means too
        // noisy and we'll show it but flag it.
        var modelConfidence = ModelConfidence(inflowFc, outflowFc);

        var weeks = new List<CashFlowForecastWeek>();
        var balance = openingCash;
        for (int w = 0; w < weekCount; w++)
        {
            var weekStart = asOf.AddDays(w * 7);
            var weekEnd = weekStart.AddDays(6);
            if (weekEnd > horizon) weekEnd = horizon;

            var inflows = ar.Where(d =>
            {
                var date = d.DueDate ?? d.DocumentDate.AddDays(30);
                return date >= weekStart && date <= weekEnd;
            }).ToList();

            var outflows = ap.Where(d =>
            {
                var date = d.DueDate ?? d.DocumentDate.AddDays(30);
                return date >= weekStart && date <= weekEnd;
            }).ToList();

            var inAmt = inflows.Sum(d => d.BalanceDue);
            var outAmt = outflows.Sum(d => d.BalanceDue);

            // Model-projected recurring activity for THIS week — added
            // as a synthetic "Recurring" item so the UI breaks it out
            // visually. Skip when MAPE is awful or projection is zero.
            var projectedIn = w < inflowFc.Forecast.Count ? inflowFc.Forecast[w] : 0m;
            var projectedOut = w < outflowFc.Forecast.Count ? outflowFc.Forecast[w] : 0m;

            var items = new List<CashFlowForecastItem>();
            foreach (var d in inflows)
            {
                var date = d.DueDate ?? d.DocumentDate.AddDays(30);
                items.Add(new CashFlowForecastItem(date, $"AR {d.DocumentNumber}",
                    d.DueDate.HasValue ? "AR" : "Estimate", R2(d.BalanceDue), "In"));
            }
            foreach (var d in outflows)
            {
                var date = d.DueDate ?? d.DocumentDate.AddDays(30);
                items.Add(new CashFlowForecastItem(date, $"AP {d.DocumentNumber}",
                    d.DueDate.HasValue ? "AP" : "Estimate", R2(d.BalanceDue), "Out"));
            }
            if (projectedIn > 0)
            {
                items.Add(new CashFlowForecastItem(weekStart, "Recurring inflows (model)",
                    "Recurring", R2(projectedIn), "In"));
                inAmt += projectedIn;
            }
            if (projectedOut > 0)
            {
                items.Add(new CashFlowForecastItem(weekStart, "Recurring outflows (model)",
                    "Recurring", R2(projectedOut), "Out"));
                outAmt += projectedOut;
            }

            var net = inAmt - outAmt;
            var opening = balance;
            balance += net;

            // Per-week confidence: average of "% dated AR/AP" and the
            // Holt-Winters model confidence weighted by share of total.
            var totalCount = inflows.Count + outflows.Count;
            var confirmedCount = inflows.Count(x => x.DueDate.HasValue) + outflows.Count(x => x.DueDate.HasValue);
            var datedConf = totalCount == 0 ? 100m : (decimal)confirmedCount / totalCount * 100m;
            var datedAmt = inflows.Sum(x => x.BalanceDue) + outflows.Sum(x => x.BalanceDue);
            var totalAmt = datedAmt + projectedIn + projectedOut;
            var blended = totalAmt == 0 ? 100m
                : (datedConf * datedAmt + modelConfidence * (projectedIn + projectedOut)) / totalAmt;

            weeks.Add(new CashFlowForecastWeek(
                w + 1, weekStart, weekEnd,
                R2(opening), R2(inAmt), R2(outAmt), R2(net), R2(balance),
                Math.Round(blended, 1), items));
        }

        return new CashFlowForecastResponse(asOf, R2(openingCash), R2(balance), days, weeks);
    }

    /// <summary>Aggregate payment events into weekly totals aligned to
    /// the same week-window the forecast loop will iterate. Empty weeks
    /// stay as 0 — Holt-Winters needs evenly spaced points.</summary>
    private static List<decimal> BucketWeekly(
        IEnumerable<(DateTime Date, decimal Amount)> events,
        DateTime historyStart, int weekCount)
    {
        var buckets = new decimal[weekCount];
        foreach (var (date, amount) in events)
        {
            var idx = (int)((date.Date - historyStart).TotalDays / 7);
            if (idx >= 0 && idx < weekCount) buckets[idx] += amount;
        }
        return buckets.ToList();
    }

    /// <summary>Convert MAPE → 0-100 confidence. <20% MAPE ≈ 90 conf;
    /// >50% MAPE drops to 30. Uses the worse of the two series so a
    /// noisy outflow projection doesn't hide behind a clean inflow.</summary>
    private static decimal ModelConfidence(HoltWintersForecaster.Result inFc, HoltWintersForecaster.Result outFc)
    {
        var worst = Math.Max(inFc.MeanAbsolutePercentError, outFc.MeanAbsolutePercentError);
        if (worst <= 0.20m) return 90m;
        if (worst <= 0.35m) return 70m;
        if (worst <= 0.50m) return 50m;
        return 30m;
    }
}
