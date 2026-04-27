using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<ProjectProfitabilityListResponse> GetProjectProfitabilityAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var projects = await _db.Projects
            .Where(p => p.CompanyId == companyId && !p.IsDeleted)
            .Select(p => new
            {
                p.Id, p.Code, p.Name, p.Status,
                p.BudgetAmount, p.ContractAmount,
                p.ActualCost, p.ActualRevenue,
                p.CompletionPercent
            })
            .ToListAsync();

        // Compute revenue/cost per project from journal entries within range
        var rawLines = await PostedLinesQuery(companyId, fromDate, toDate)
            .Where(l => l.ProjectId != null || l.JournalEntry.ProjectId != null)
            .Select(l => new
            {
                LineProjectId = l.ProjectId,
                HeaderProjectId = l.JournalEntry.ProjectId,
                AccountType = l.Account.AccountType,
                Debit = l.DebitAmount,
                Credit = l.CreditAmount
            })
            .ToListAsync();

        var lines = rawLines.Select(l => new
        {
            ProjectId = l.LineProjectId ?? l.HeaderProjectId!.Value,
            l.AccountType, l.Debit, l.Credit
        }).ToList();

        var grouped = lines
            .GroupBy(l => l.ProjectId)
            .ToDictionary(g => g.Key, g => new
            {
                Revenue = g.Where(x => x.AccountType == AccountType.Revenue).Sum(x => x.Credit - x.Debit),
                Cost = g.Where(x => x.AccountType == AccountType.Expense).Sum(x => x.Debit - x.Credit)
            });

        var rows = projects.Select(p =>
        {
            grouped.TryGetValue(p.Id, out var g);
            var actualRevenue = g != null ? g.Revenue : p.ActualRevenue;
            var actualCost = g != null ? g.Cost : p.ActualCost;
            var profit = actualRevenue - actualCost;
            var margin = Pct(profit, actualRevenue);
            var budgetVariance = actualCost - p.BudgetAmount;
            return new ProjectProfitabilityRow(
                p.Id, p.Code, p.Name, p.Status,
                R2(p.BudgetAmount), R2(actualRevenue), R2(actualCost), R2(profit),
                margin, R2(p.CompletionPercent), R2(budgetVariance));
        }).OrderByDescending(r => r.Profit).ToList();

        var totalRev = rows.Sum(r => r.Revenue);
        var totalCost = rows.Sum(r => r.Cost);
        var totalProfit = rows.Sum(r => r.Profit);

        return new ProjectProfitabilityListResponse(fromDate, toDate, R2(totalRev), R2(totalCost), R2(totalProfit), rows);
    }
}
