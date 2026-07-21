using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<BudgetVarianceResponse> GetBudgetVarianceAsync(Guid companyId, Guid? budgetId, DateTime? fromDate, DateTime? toDate)
    {
        var year = (fromDate ?? DateTime.UtcNow).Year;
        var budgetQ = _db.Budgets.AsNoTracking()
            .Include(b => b.Lines).ThenInclude(l => l.Account)
            .Where(b => b.CompanyId == companyId && !b.IsDeleted);
        if (budgetId.HasValue)
            budgetQ = budgetQ.Where(b => b.Id == budgetId.Value);
        else
            budgetQ = budgetQ.Where(b => b.IsActive && b.FiscalYear == year);

        var budget = await budgetQ.FirstOrDefaultAsync();
        var from = fromDate ?? new DateTime(year, 1, 1);
        var to = toDate ?? new DateTime(year, 12, 31, 23, 59, 59);

        if (budget == null)
        {
            var emptySummary = new BudgetVarianceSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            return new BudgetVarianceResponse(null, null, year, from, to, emptySummary, new List<BudgetVarianceRow>());
        }

        // Determine months in range
        var startMonth = (from.Year == budget.FiscalYear) ? from.Month : 1;
        var endMonth = (to.Year == budget.FiscalYear) ? to.Month : 12;

        var rows = new List<BudgetVarianceRow>();
        decimal revBudget = 0, revActual = 0, expBudget = 0, expActual = 0;

        foreach (var line in budget.Lines)
        {
            // Sum monthly fields based on range
            decimal lineBudget = 0;
            for (int m = startMonth; m <= endMonth; m++)
            {
                lineBudget += GetMonthlyBudget(line, m);
            }

            // Actual: query journal lines for this account in [from, to]
            var actualQ = PostedLinesQuery(companyId, from, to)
                .Where(l => l.AccountId == line.AccountId);
            var totals = await actualQ.GroupBy(_ => 1)
                .Select(g => new { D = g.Sum(x => x.DebitAmount), C = g.Sum(x => x.CreditAmount) })
                .FirstOrDefaultAsync();
            var actual = totals == null ? 0 :
                (line.Account.AccountType == AccountType.Revenue ? (totals.C - totals.D) :
                 line.Account.AccountType == AccountType.Expense ? (totals.D - totals.C) :
                 (totals.D - totals.C));

            var variance = actual - lineBudget;
            var variancePct = Pct(variance, Math.Abs(lineBudget));
            var status = lineBudget == 0 ? "OnTrack" :
                line.Account.AccountType == AccountType.Revenue
                    ? (actual >= lineBudget ? "OnTrack" : "Under")
                    : (actual <= lineBudget ? "OnTrack" : "Over");

            rows.Add(new BudgetVarianceRow(
                line.Account.AccountCode, line.Account.AccountName, line.Account.AccountType.ToString(),
                R2(lineBudget), R2(actual), R2(variance), variancePct, status));

            if (line.Account.AccountType == AccountType.Revenue)
            { revBudget += lineBudget; revActual += actual; }
            else if (line.Account.AccountType == AccountType.Expense)
            { expBudget += lineBudget; expActual += actual; }
        }

        var summary = new BudgetVarianceSummary(
            R2(revBudget), R2(revActual), R2(revActual - revBudget), Pct(revActual - revBudget, Math.Abs(revBudget)),
            R2(expBudget), R2(expActual), R2(expActual - expBudget), Pct(expActual - expBudget, Math.Abs(expBudget)),
            R2(revBudget - expBudget), R2(revActual - expActual), R2((revActual - expActual) - (revBudget - expBudget)));

        return new BudgetVarianceResponse(budget.Id, budget.Name, budget.FiscalYear, from, to, summary, rows);
    }

    private static decimal GetMonthlyBudget(Accounting.Models.Entities.BudgetLine line, int month)
    {
        return month switch
        {
            1 => line.Month1, 2 => line.Month2, 3 => line.Month3,
            4 => line.Month4, 5 => line.Month5, 6 => line.Month6,
            7 => line.Month7, 8 => line.Month8, 9 => line.Month9,
            10 => line.Month10, 11 => line.Month11, 12 => line.Month12,
            _ => 0
        };
    }
}
