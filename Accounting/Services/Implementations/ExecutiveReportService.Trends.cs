using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<TrendAnalysisResponse> GetTrendAnalysisAsync(Guid companyId, string granularity, int periods)
    {
        if (periods < 1) periods = 12;
        if (periods > 60) periods = 60;
        var g = (granularity ?? "monthly").ToLowerInvariant();

        var ranges = BuildPeriodRanges(g, periods, DateTime.UtcNow);

        var revenue = new List<TrendPoint>();
        var expense = new List<TrendPoint>();
        var grossProfit = new List<TrendPoint>();
        var netIncome = new List<TrendPoint>();
        var cash = new List<TrendPoint>();

        foreach (var (label, start, end) in ranges)
        {
            var rev = await SumRevenueAsync(companyId, start, end);
            var exp = await SumExpenseAsync(companyId, start, end);
            var cogs = await SumCogsAsync(companyId, start, end);
            var gp = rev - cogs;
            var ni = rev - exp;
            var cs = await GetCashAndBankAsync(companyId, end);
            revenue.Add(new TrendPoint(label, start, end, R2(rev)));
            expense.Add(new TrendPoint(label, start, end, R2(exp)));
            grossProfit.Add(new TrendPoint(label, start, end, R2(gp)));
            netIncome.Add(new TrendPoint(label, start, end, R2(ni)));
            cash.Add(new TrendPoint(label, start, end, R2(cs)));
        }

        AttachGrowth(revenue);
        AttachGrowth(expense);
        AttachGrowth(grossProfit);
        AttachGrowth(netIncome);
        AttachGrowth(cash);

        var cagrRev = ComputeCagr(revenue);
        var cagrExp = ComputeCagr(expense);

        return new TrendAnalysisResponse(g, periods, revenue, expense, grossProfit, netIncome, cash, cagrRev, cagrExp);
    }

    private static List<(string label, DateTime start, DateTime end)> BuildPeriodRanges(string granularity, int periods, DateTime anchor)
    {
        var list = new List<(string, DateTime, DateTime)>();
        if (granularity == "yearly")
        {
            var endYear = anchor.Year;
            for (int i = periods - 1; i >= 0; i--)
            {
                var y = endYear - i;
                var start = new DateTime(y, 1, 1);
                var end = new DateTime(y, 12, 31, 23, 59, 59);
                list.Add(($"{y}", start, end));
            }
        }
        else if (granularity == "quarterly")
        {
            var anchorQ = (anchor.Month - 1) / 3;
            var anchorY = anchor.Year;
            for (int i = periods - 1; i >= 0; i--)
            {
                var totalQ = anchorY * 4 + anchorQ - i;
                var y = totalQ / 4;
                var q = totalQ % 4;
                var startMonth = q * 3 + 1;
                var start = new DateTime(y, startMonth, 1);
                var end = start.AddMonths(3).AddSeconds(-1);
                list.Add(($"Q{q + 1} {y}", start, end));
            }
        }
        else // monthly
        {
            var anchorY = anchor.Year;
            var anchorM = anchor.Month;
            for (int i = periods - 1; i >= 0; i--)
            {
                var totalM = anchorY * 12 + (anchorM - 1) - i;
                var y = totalM / 12;
                var m = (totalM % 12) + 1;
                var start = new DateTime(y, m, 1);
                var end = start.AddMonths(1).AddSeconds(-1);
                list.Add(($"{y:D4}-{m:D2}", start, end));
            }
        }
        return list;
    }

    private static void AttachGrowth(List<TrendPoint> points)
    {
        for (int i = 1; i < points.Count; i++)
        {
            var prev = points[i - 1].Value;
            var curr = points[i].Value;
            var growth = prev == 0 ? 0 : Math.Round((curr - prev) / Math.Abs(prev) * 100m, 2);
            points[i] = points[i] with { PreviousPeriodValue = prev, GrowthPercent = growth };
        }
    }

    private static decimal ComputeCagr(List<TrendPoint> points)
    {
        if (points.Count < 2) return 0;
        var first = points.First().Value;
        var last = points.Last().Value;
        if (first <= 0 || last <= 0) return 0;
        var n = points.Count - 1;
        var ratio = (double)(last / first);
        var cagr = Math.Pow(ratio, 1.0 / n) - 1.0;
        return Math.Round((decimal)cagr * 100m, 2);
    }

    internal async Task<(List<TrendPoint> revenue, List<TrendPoint> expense, List<TrendPoint> netIncome)>
        BuildTrendPointsAsync(Guid companyId, DateTime fromDate, DateTime toDate, string granularity, int periods)
    {
        // Build last N months ending at toDate
        var ranges = BuildPeriodRanges(granularity, periods, toDate);
        var rev = new List<TrendPoint>();
        var exp = new List<TrendPoint>();
        var ni = new List<TrendPoint>();
        foreach (var (label, start, end) in ranges)
        {
            var r = await SumRevenueAsync(companyId, start, end);
            var e = await SumExpenseAsync(companyId, start, end);
            rev.Add(new TrendPoint(label, start, end, R2(r)));
            exp.Add(new TrendPoint(label, start, end, R2(e)));
            ni.Add(new TrendPoint(label, start, end, R2(r - e)));
        }
        AttachGrowth(rev);
        AttachGrowth(exp);
        AttachGrowth(ni);
        return (rev, exp, ni);
    }
}
