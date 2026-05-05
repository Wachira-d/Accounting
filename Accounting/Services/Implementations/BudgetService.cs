using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Budget;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class BudgetService : IBudgetService
{
    private readonly AccountingDbContext _db;

    public BudgetService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<BudgetResponse> CreateAsync(Guid companyId, CreateBudgetRequest request, string createdBy)
    {
        var budget = new Budget
        {
            CompanyId = companyId,
            Name = request.Name,
            FiscalYear = request.FiscalYear,
            CreatedBy = createdBy
        };

        _db.Budgets.Add(budget);

        foreach (var line in request.Lines)
        {
            var total = line.Month1 + line.Month2 + line.Month3 + line.Month4 +
                        line.Month5 + line.Month6 + line.Month7 + line.Month8 +
                        line.Month9 + line.Month10 + line.Month11 + line.Month12;

            _db.BudgetLines.Add(new BudgetLine
            {
                BudgetId = budget.Id,
                AccountId = line.AccountId,
                Month1 = line.Month1, Month2 = line.Month2, Month3 = line.Month3,
                Month4 = line.Month4, Month5 = line.Month5, Month6 = line.Month6,
                Month7 = line.Month7, Month8 = line.Month8, Month9 = line.Month9,
                Month10 = line.Month10, Month11 = line.Month11, Month12 = line.Month12,
                TotalBudget = total
            });
        }

        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, budget.Id);
    }

    public async Task<BudgetResponse> GetByIdAsync(Guid companyId, Guid budgetId)
    {
        var budget = await _db.Budgets
            .Include(b => b.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(b => b.Id == budgetId && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงบประมาณ");

        return MapToResponse(budget);
    }

    public async Task<List<BudgetResponse>> GetAllAsync(Guid companyId, int? fiscalYear = null)
    {
        var query = _db.Budgets
            .Include(b => b.Lines).ThenInclude(l => l.Account)
            .Where(b => b.CompanyId == companyId);

        if (fiscalYear.HasValue)
            query = query.Where(b => b.FiscalYear == fiscalYear.Value);

        var budgets = await query.OrderByDescending(b => b.FiscalYear).ToListAsync();
        return budgets.Select(MapToResponse).ToList();
    }

    public async Task<PagedResponse<BudgetResponse>> GetAllPagedAsync(Guid companyId, int? fiscalYear, PagedRequest request)
    {
        var query = _db.Budgets
            .Include(b => b.Lines).ThenInclude(l => l.Account)
            .Where(b => b.CompanyId == companyId);

        if (fiscalYear.HasValue)
            query = query.Where(b => b.FiscalYear == fiscalYear.Value);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(b => b.Name.Contains(request.Search));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(b => b.FiscalYear)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<BudgetResponse>(
            items.Select(MapToResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<BudgetResponse> UpdateAsync(Guid companyId, Guid budgetId, UpdateBudgetRequest request)
    {
        var budget = await _db.Budgets
            .Include(b => b.Lines)
            .FirstOrDefaultAsync(b => b.Id == budgetId && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงบประมาณ");

        if (request.Name != null) budget.Name = request.Name;
        if (request.IsActive.HasValue) budget.IsActive = request.IsActive.Value;

        if (request.Lines != null)
        {
            _db.BudgetLines.RemoveRange(budget.Lines);

            foreach (var line in request.Lines)
            {
                var total = line.Month1 + line.Month2 + line.Month3 + line.Month4 +
                            line.Month5 + line.Month6 + line.Month7 + line.Month8 +
                            line.Month9 + line.Month10 + line.Month11 + line.Month12;

                _db.BudgetLines.Add(new BudgetLine
                {
                    BudgetId = budget.Id,
                    AccountId = line.AccountId,
                    Month1 = line.Month1, Month2 = line.Month2, Month3 = line.Month3,
                    Month4 = line.Month4, Month5 = line.Month5, Month6 = line.Month6,
                    Month7 = line.Month7, Month8 = line.Month8, Month9 = line.Month9,
                    Month10 = line.Month10, Month11 = line.Month11, Month12 = line.Month12,
                    TotalBudget = total
                });
            }
        }

        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, budgetId);
    }

    public async Task DeleteAsync(Guid companyId, Guid budgetId)
    {
        var budget = await _db.Budgets
            .FirstOrDefaultAsync(b => b.Id == budgetId && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงบประมาณ");

        budget.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    public async Task<BudgetVsActualResponse> GetBudgetVsActualAsync(Guid companyId, Guid budgetId)
    {
        var budget = await _db.Budgets
            .Include(b => b.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(b => b.Id == budgetId && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงบประมาณ");

        var startDate = new DateTime(budget.FiscalYear, 1, 1);
        var endDate = new DateTime(budget.FiscalYear, 12, 31);

        // Calculate actual amounts per month from journal entries
        var actualAmounts = await _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && (l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed)
                && l.JournalEntry.EntryDate >= startDate
                && l.JournalEntry.EntryDate <= endDate)
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Actual = g.Sum(l => l.DebitAmount - l.CreditAmount) })
            .ToListAsync();

        var actualDict = actualAmounts.ToDictionary(a => a.AccountId, a => a.Actual);

        var lines = budget.Lines.Select(line =>
        {
            var budgetAmount = line.TotalBudget;
            var rawActual = actualDict.GetValueOrDefault(line.AccountId);
            // Revenue (4xxxx) and Liability (2xxxx) accounts are credit-normal: actual = Cr - Dr
            var isRevOrLiab = line.Account?.AccountCode?.StartsWith("4") == true
                || line.Account?.AccountCode?.StartsWith("2") == true
                || line.Account?.AccountCode?.StartsWith("3") == true;
            var actualAmount = isRevOrLiab ? -rawActual : rawActual;
            var variance = budgetAmount - actualAmount;
            var variancePercent = budgetAmount != 0 ? (variance / budgetAmount) * 100 : 0;

            return new BudgetVsActualLine(
                line.AccountId,
                line.Account?.AccountCode ?? "",
                line.Account?.AccountName ?? "",
                budgetAmount, actualAmount, variance, variancePercent);
        }).ToList();

        // Calculate summary with variance alerts
        var totalBudget = lines.Sum(l => l.BudgetAmount);
        var totalActual = lines.Sum(l => l.ActualAmount);

        var alerts = lines
            .Where(l => l.ActualAmount > l.BudgetAmount && l.BudgetAmount > 0)
            .Select(l =>
            {
                var overPercent = ((l.ActualAmount - l.BudgetAmount) / l.BudgetAmount) * 100;
                var level = overPercent switch
                {
                    > 50 => "Critical",
                    > 20 => "Warning",
                    _ => "Info"
                };
                return new BudgetAlertResponse(
                    l.AccountId, l.AccountCode, l.AccountName,
                    level, l.BudgetAmount, l.ActualAmount, overPercent,
                    $"{l.AccountName} เกินงบประมาณ {overPercent:F1}% (งบ {l.BudgetAmount:N2} / จริง {l.ActualAmount:N2})");
            })
            .OrderByDescending(a => a.OverBudgetPercent)
            .ToList();

        return new BudgetVsActualResponse(budget.Id, budget.Name, budget.FiscalYear, lines, alerts, totalBudget, totalActual);
    }

    private static BudgetResponse MapToResponse(Budget b) =>
        new(b.Id, b.Name, b.FiscalYear, b.IsActive,
            b.Lines.Sum(l => l.TotalBudget),
            b.Lines.Select(l => new BudgetLineResponse(
                l.Id, l.AccountId, l.Account?.AccountCode ?? "", l.Account?.AccountName ?? "",
                l.Month1, l.Month2, l.Month3, l.Month4, l.Month5, l.Month6,
                l.Month7, l.Month8, l.Month9, l.Month10, l.Month11, l.Month12,
                l.TotalBudget)).ToList(),
            b.CreatedAt);
}
