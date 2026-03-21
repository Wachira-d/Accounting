using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Fpa;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class FpaService : IFpaService
{
    private readonly AccountingDbContext _db;

    public FpaService(AccountingDbContext db)
    {
        _db = db;
    }

    // ===== Scenarios =====

    public async Task<ScenarioResponse> CreateScenarioAsync(Guid companyId, CreateScenarioRequest request)
    {
        var scenario = new FinancialScenario
        {
            CompanyId = companyId,
            Name = request.Name,
            Description = request.Description,
            ScenarioType = request.ScenarioType,
            FiscalYear = request.FiscalYear,
            BaselineType = request.BaselineType,
            BaselineScenarioId = request.BaselineScenarioId,
            Status = "Draft"
        };

        _db.Set<FinancialScenario>().Add(scenario);
        await _db.SaveChangesAsync();

        return MapToScenarioResponse(scenario);
    }

    public async Task<ScenarioResponse> GetScenarioAsync(Guid companyId, Guid scenarioId)
    {
        var scenario = await _db.Set<FinancialScenario>()
            .Include(s => s.Assumptions)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Id == scenarioId)
            ?? throw new InvalidOperationException("Scenario not found.");

        return MapToScenarioResponse(scenario);
    }

    public async Task<List<ScenarioResponse>> GetScenariosAsync(Guid companyId, int? fiscalYear = null)
    {
        var query = _db.Set<FinancialScenario>()
            .Include(s => s.Assumptions)
            .Where(s => s.CompanyId == companyId);

        if (fiscalYear.HasValue)
            query = query.Where(s => s.FiscalYear == fiscalYear.Value);

        return await query
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => MapToScenarioResponse(s))
            .ToListAsync();
    }

    public async Task<ScenarioResponse> UpdateScenarioAsync(Guid companyId, Guid scenarioId, UpdateScenarioRequest request)
    {
        var scenario = await _db.Set<FinancialScenario>()
            .Include(s => s.Assumptions)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Id == scenarioId)
            ?? throw new InvalidOperationException("Scenario not found.");

        if (request.Name != null) scenario.Name = request.Name;
        if (request.Description != null) scenario.Description = request.Description;
        if (request.Status != null) scenario.Status = request.Status;

        await _db.SaveChangesAsync();

        return MapToScenarioResponse(scenario);
    }

    public async Task DeleteScenarioAsync(Guid companyId, Guid scenarioId)
    {
        var scenario = await _db.Set<FinancialScenario>()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Id == scenarioId)
            ?? throw new InvalidOperationException("Scenario not found.");

        scenario.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ===== Assumptions =====

    public async Task<ScenarioResponse> AddAssumptionAsync(Guid companyId, Guid scenarioId, CreateAssumptionRequest request)
    {
        var scenario = await _db.Set<FinancialScenario>()
            .Include(s => s.Assumptions)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Id == scenarioId)
            ?? throw new InvalidOperationException("Scenario not found.");

        var assumption = new ScenarioAssumption
        {
            CompanyId = companyId,
            FinancialScenarioId = scenarioId,
            Category = request.Category,
            Description = request.Description,
            AdjustmentType = request.AdjustmentType,
            AdjustmentValue = request.AdjustmentValue,
            AccountId = request.AccountId,
            DimensionId = request.DimensionId,
            ApplyToMonth = request.ApplyToMonth
        };

        _db.Set<ScenarioAssumption>().Add(assumption);
        await _db.SaveChangesAsync();

        // Reload assumptions
        await _db.Entry(scenario).Collection(s => s.Assumptions).LoadAsync();
        return MapToScenarioResponse(scenario);
    }

    public async Task RemoveAssumptionAsync(Guid companyId, Guid assumptionId)
    {
        var assumption = await _db.Set<ScenarioAssumption>()
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.Id == assumptionId)
            ?? throw new InvalidOperationException("Assumption not found.");

        assumption.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ===== Calculate & Compare =====

    public async Task<ScenarioResultsResponse> CalculateScenarioAsync(Guid companyId, Guid scenarioId)
    {
        var scenario = await _db.Set<FinancialScenario>()
            .Include(s => s.Assumptions)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Id == scenarioId)
            ?? throw new InvalidOperationException("Scenario not found.");

        // Get baseline actuals from GL for the fiscal year
        var baselineData = await GetMonthlyGlDataAsync(companyId, scenario.FiscalYear);

        // Remove old results
        var oldResults = await _db.Set<ScenarioResult>()
            .Where(r => r.FinancialScenarioId == scenarioId)
            .ToListAsync();
        _db.Set<ScenarioResult>().RemoveRange(oldResults);

        var monthlyResults = new List<ScenarioMonthResult>();

        for (int month = 1; month <= 12; month++)
        {
            var monthData = baselineData.Where(d => d.Month == month).ToList();

            var baselineRevenue = monthData
                .Where(d => d.AccountType == AccountType.Revenue)
                .Sum(d => d.CreditAmount - d.DebitAmount);

            var baselineExpenses = monthData
                .Where(d => d.AccountType == AccountType.Expense)
                .Sum(d => d.DebitAmount - d.CreditAmount);

            var scenarioRevenue = baselineRevenue;
            var scenarioExpenses = baselineExpenses;

            // Apply assumptions
            foreach (var assumption in scenario.Assumptions.Where(a => !a.IsDeleted))
            {
                if (assumption.ApplyToMonth.HasValue && assumption.ApplyToMonth.Value != month)
                    continue;

                if (assumption.Category is "Revenue" or "Pricing")
                {
                    scenarioRevenue = ApplyAdjustment(scenarioRevenue, assumption);
                }
                else if (assumption.Category is "COGS" or "OpEx" or "Headcount")
                {
                    scenarioExpenses = ApplyAdjustment(scenarioExpenses, assumption);
                }
                else if (assumption.Category == "Tax")
                {
                    // Tax adjustments affect expenses
                    scenarioExpenses = ApplyAdjustment(scenarioExpenses, assumption);
                }
            }

            // Store results per account
            foreach (var accountGroup in monthData.GroupBy(d => d.AccountId))
            {
                var baseAmount = accountGroup.Sum(d => d.DebitAmount - d.CreditAmount);
                var acctType = accountGroup.First().AccountType;
                var scenarioAmount = baseAmount;

                foreach (var assumption in scenario.Assumptions.Where(a => !a.IsDeleted))
                {
                    if (assumption.ApplyToMonth.HasValue && assumption.ApplyToMonth.Value != month)
                        continue;

                    if (assumption.AccountId.HasValue && assumption.AccountId.Value != accountGroup.Key)
                        continue;

                    var isRelevant = (assumption.Category is "Revenue" or "Pricing" && acctType == AccountType.Revenue)
                                  || (assumption.Category is "COGS" or "OpEx" or "Headcount" && acctType == AccountType.Expense);

                    if (isRelevant || (!assumption.AccountId.HasValue && assumption.Category == "Tax"))
                    {
                        scenarioAmount = ApplyAdjustment(scenarioAmount, assumption);
                    }
                }

                _db.Set<ScenarioResult>().Add(new ScenarioResult
                {
                    CompanyId = companyId,
                    FinancialScenarioId = scenarioId,
                    Month = month,
                    AccountId = accountGroup.Key,
                    BaselineAmount = baseAmount,
                    ScenarioAmount = scenarioAmount,
                    Variance = scenarioAmount - baseAmount,
                    VariancePercent = baseAmount != 0 ? (scenarioAmount - baseAmount) / Math.Abs(baseAmount) * 100 : 0
                });
            }

            monthlyResults.Add(new ScenarioMonthResult(
                month, baselineRevenue, scenarioRevenue, baselineExpenses, scenarioExpenses,
                baselineRevenue - baselineExpenses, scenarioRevenue - scenarioExpenses));
        }

        await _db.SaveChangesAsync();

        return new ScenarioResultsResponse(
            scenarioId, scenario.Name, monthlyResults,
            monthlyResults.Sum(m => m.BaselineRevenue),
            monthlyResults.Sum(m => m.ScenarioRevenue),
            monthlyResults.Sum(m => m.BaselineExpenses),
            monthlyResults.Sum(m => m.ScenarioExpenses),
            monthlyResults.Sum(m => m.BaselineNetIncome),
            monthlyResults.Sum(m => m.ScenarioNetIncome));
    }

    public async Task<ScenarioComparisonResponse> CompareAsync(Guid companyId, List<Guid> scenarioIds)
    {
        var results = new List<ScenarioResultsResponse>();

        foreach (var scenarioId in scenarioIds)
        {
            var result = await CalculateScenarioAsync(companyId, scenarioId);
            results.Add(result);
        }

        // Build a comparison summary
        var summaryParts = results.Select(r =>
            $"{{\"scenario\":\"{r.ScenarioName}\",\"netIncome\":{r.ScenarioNetIncome},\"revenueChange\":{(r.TotalBaselineRevenue != 0 ? (r.TotalScenarioRevenue - r.TotalBaselineRevenue) / r.TotalBaselineRevenue * 100 : 0):F2}}}");

        var summaryJson = $"[{string.Join(",", summaryParts)}]";

        return new ScenarioComparisonResponse(results, summaryJson);
    }

    // ===== Financial KPIs =====

    public async Task<FinancialKpiResponse> CreateKpiAsync(Guid companyId, CreateFinancialKpiRequest request)
    {
        var kpi = new FinancialKpi
        {
            CompanyId = companyId,
            Name = request.Name,
            Code = request.Code,
            Category = request.Category,
            Formula = request.Formula,
            TargetValue = request.TargetValue,
            WarningThreshold = request.WarningThreshold,
            CriticalThreshold = request.CriticalThreshold,
            IsActive = true
        };

        _db.Set<FinancialKpi>().Add(kpi);
        await _db.SaveChangesAsync();

        return new FinancialKpiResponse(
            kpi.Id, kpi.Name, kpi.Code, kpi.Category,
            kpi.TargetValue, null, null, kpi.IsActive);
    }

    public async Task<List<FinancialKpiResponse>> GetKpisAsync(Guid companyId)
    {
        var kpis = await _db.Set<FinancialKpi>()
            .Where(k => k.CompanyId == companyId && k.IsActive)
            .ToListAsync();

        var result = new List<FinancialKpiResponse>();

        foreach (var kpi in kpis)
        {
            var latestSnapshot = await _db.Set<KpiSnapshot>()
                .Where(s => s.FinancialKpiId == kpi.Id)
                .OrderByDescending(s => s.Year)
                .ThenByDescending(s => s.Month)
                .FirstOrDefaultAsync();

            result.Add(new FinancialKpiResponse(
                kpi.Id, kpi.Name, kpi.Code, kpi.Category,
                kpi.TargetValue, latestSnapshot?.Value, latestSnapshot?.Status, kpi.IsActive));
        }

        return result;
    }

    public async Task<List<KpiSnapshotResponse>> GetKpiHistoryAsync(Guid companyId, Guid kpiId, int months = 12)
    {
        return await _db.Set<KpiSnapshot>()
            .Where(s => s.FinancialKpiId == kpiId)
            .OrderByDescending(s => s.Year)
            .ThenByDescending(s => s.Month)
            .Take(months)
            .Select(s => new KpiSnapshotResponse(s.Year, s.Month, s.Value, s.Status))
            .ToListAsync();
    }

    public async Task CalculateKpiSnapshotsAsync(Guid companyId, int year, int month)
    {
        var kpis = await _db.Set<FinancialKpi>()
            .Where(k => k.CompanyId == companyId && k.IsActive)
            .ToListAsync();

        var ratios = await CalculateRatiosAsync(companyId, new DateTime(year, month, DateTime.DaysInMonth(year, month)));

        foreach (var kpi in kpis)
        {
            var value = kpi.Code switch
            {
                "CurrentRatio" => ratios.CurrentRatio,
                "QuickRatio" => ratios.QuickRatio,
                "DebtToEquity" => ratios.DebtToEquity,
                "ROE" => ratios.ReturnOnEquity,
                "ROA" => ratios.ReturnOnAssets,
                "GrossProfitMargin" => ratios.GrossProfitMargin,
                "NetProfitMargin" => ratios.NetProfitMargin,
                "AssetTurnover" => ratios.AssetTurnover,
                "ReceivableTurnover" => ratios.ReceivableTurnover,
                "PayableTurnover" => ratios.PayableTurnover,
                "InventoryTurnover" => ratios.InventoryTurnover,
                "DSO" => ratios.DaysSalesOutstanding,
                "DPO" => ratios.DaysPayableOutstanding,
                "DIO" => ratios.DaysInventoryOutstanding,
                "CCC" => ratios.CashConversionCycle,
                "WorkingCapital" => ratios.WorkingCapital,
                "InterestCoverage" => ratios.InterestCoverage,
                _ => 0m
            };

            // Determine status based on thresholds
            string? status = null;
            if (kpi.CriticalThreshold.HasValue && value <= kpi.CriticalThreshold.Value)
                status = "Critical";
            else if (kpi.WarningThreshold.HasValue && value <= kpi.WarningThreshold.Value)
                status = "Warning";
            else if (kpi.TargetValue.HasValue && value >= kpi.TargetValue.Value)
                status = "Good";
            else
                status = "Good";

            // Upsert snapshot
            var existing = await _db.Set<KpiSnapshot>()
                .FirstOrDefaultAsync(s => s.FinancialKpiId == kpi.Id && s.Year == year && s.Month == month);

            if (existing != null)
            {
                existing.Value = value;
                existing.Status = status;
            }
            else
            {
                _db.Set<KpiSnapshot>().Add(new KpiSnapshot
                {
                    CompanyId = companyId,
                    FinancialKpiId = kpi.Id,
                    Year = year,
                    Month = month,
                    Value = value,
                    Status = status
                });
            }
        }

        await _db.SaveChangesAsync();
    }

    // ===== Financial Ratios =====

    public async Task<FinancialRatiosResponse> CalculateRatiosAsync(Guid companyId, DateTime asOfDate)
    {
        // Gather all posted journal entry lines up to asOfDate with account types
        var glBalances = await _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Include(l => l.Account)
            .Where(l => l.JournalEntry.CompanyId == companyId
                     && l.JournalEntry.Status == JournalEntryStatus.Posted
                     && l.JournalEntry.EntryDate <= asOfDate)
            .GroupBy(l => new { l.AccountId, l.Account.AccountCode, l.Account.AccountType })
            .Select(g => new
            {
                g.Key.AccountId,
                g.Key.AccountCode,
                g.Key.AccountType,
                TotalDebit = g.Sum(l => l.DebitAmount),
                TotalCredit = g.Sum(l => l.CreditAmount)
            })
            .ToListAsync();

        // Balance sheet amounts (debit-normal for assets/expenses, credit-normal for liabilities/equity/revenue)
        decimal BalanceOf(AccountType type) => glBalances
            .Where(b => b.AccountType == type)
            .Sum(b => type == AccountType.Asset || type == AccountType.Expense
                ? b.TotalDebit - b.TotalCredit
                : b.TotalCredit - b.TotalDebit);

        // Account-code-based classification for more granular analysis
        // Current assets: codes starting with 11xx (cash, receivables, inventory, prepaid)
        decimal CurrentAssets() => glBalances
            .Where(b => b.AccountType == AccountType.Asset && b.AccountCode.StartsWith("1")
                      && b.AccountCode.Length >= 2 && b.AccountCode[1] == '1')
            .Sum(b => b.TotalDebit - b.TotalCredit);

        // Inventory: codes starting with 115x or 14xx
        decimal Inventory() => glBalances
            .Where(b => b.AccountType == AccountType.Asset
                      && (b.AccountCode.StartsWith("115") || b.AccountCode.StartsWith("14")))
            .Sum(b => b.TotalDebit - b.TotalCredit);

        // Accounts receivable: codes starting with 112x or 11200-11299
        decimal AccountsReceivable() => glBalances
            .Where(b => b.AccountType == AccountType.Asset && b.AccountCode.StartsWith("112"))
            .Sum(b => b.TotalDebit - b.TotalCredit);

        // Current liabilities: codes starting with 21xx
        decimal CurrentLiabilities() => glBalances
            .Where(b => b.AccountType == AccountType.Liability && b.AccountCode.StartsWith("2")
                      && b.AccountCode.Length >= 2 && b.AccountCode[1] == '1')
            .Sum(b => b.TotalCredit - b.TotalDebit);

        // Accounts payable: codes starting with 211x
        decimal AccountsPayable() => glBalances
            .Where(b => b.AccountType == AccountType.Liability && b.AccountCode.StartsWith("211"))
            .Sum(b => b.TotalCredit - b.TotalDebit);

        // Interest expense: codes starting with 53xx or containing "interest"
        decimal InterestExpense() => glBalances
            .Where(b => b.AccountType == AccountType.Expense && b.AccountCode.StartsWith("53"))
            .Sum(b => b.TotalDebit - b.TotalCredit);

        // COGS: codes starting with 5100 or 51xx
        decimal CostOfGoodsSold() => glBalances
            .Where(b => b.AccountType == AccountType.Expense && b.AccountCode.StartsWith("51"))
            .Sum(b => b.TotalDebit - b.TotalCredit);

        var totalAssets = BalanceOf(AccountType.Asset);
        var totalLiabilities = BalanceOf(AccountType.Liability);
        var totalEquity = BalanceOf(AccountType.Equity);
        var totalRevenue = BalanceOf(AccountType.Revenue);
        var totalExpenses = BalanceOf(AccountType.Expense);

        var currentAssets = CurrentAssets();
        var inventory = Inventory();
        var accountsReceivable = AccountsReceivable();
        var currentLiabilities = CurrentLiabilities();
        var accountsPayable = AccountsPayable();
        var interestExpense = InterestExpense();
        var cogs = CostOfGoodsSold();

        // If current asset/liability codes are not found, estimate from totals
        if (currentAssets == 0 && totalAssets > 0) currentAssets = totalAssets * 0.6m;
        if (currentLiabilities == 0 && totalLiabilities > 0) currentLiabilities = totalLiabilities * 0.5m;

        var netIncome = totalRevenue - totalExpenses;
        var grossProfit = totalRevenue - cogs;
        var ebit = netIncome + interestExpense;

        // Liquidity Ratios
        var currentRatio = currentLiabilities != 0 ? currentAssets / currentLiabilities : 0m;
        var quickRatio = currentLiabilities != 0 ? (currentAssets - inventory) / currentLiabilities : 0m;

        // Leverage Ratios
        var debtToEquity = totalEquity != 0 ? totalLiabilities / totalEquity : 0m;

        // Profitability Ratios
        var returnOnEquity = totalEquity != 0 ? netIncome / totalEquity * 100 : 0m;
        var returnOnAssets = totalAssets != 0 ? netIncome / totalAssets * 100 : 0m;
        var grossProfitMargin = totalRevenue != 0 ? grossProfit / totalRevenue * 100 : 0m;
        var netProfitMargin = totalRevenue != 0 ? netIncome / totalRevenue * 100 : 0m;

        // Efficiency / Activity Ratios
        var assetTurnover = totalAssets != 0 ? totalRevenue / totalAssets : 0m;
        var receivableTurnover = accountsReceivable != 0 ? totalRevenue / accountsReceivable : 0m;
        var payableTurnover = accountsPayable != 0 ? cogs / accountsPayable : 0m;
        var inventoryTurnover = inventory != 0 ? cogs / inventory : 0m;

        // Days ratios
        var daysSalesOutstanding = receivableTurnover != 0 ? 365m / receivableTurnover : 0m;
        var daysPayableOutstanding = payableTurnover != 0 ? 365m / payableTurnover : 0m;
        var daysInventoryOutstanding = inventoryTurnover != 0 ? 365m / inventoryTurnover : 0m;

        // Cash Conversion Cycle = DIO + DSO - DPO
        var cashConversionCycle = daysInventoryOutstanding + daysSalesOutstanding - daysPayableOutstanding;

        // Working Capital
        var workingCapital = currentAssets - currentLiabilities;

        // Interest Coverage Ratio = EBIT / Interest Expense
        var interestCoverage = interestExpense != 0 ? ebit / interestExpense : 0m;

        return new FinancialRatiosResponse(
            asOfDate,
            Math.Round(currentRatio, 4),
            Math.Round(quickRatio, 4),
            Math.Round(debtToEquity, 4),
            Math.Round(returnOnEquity, 4),
            Math.Round(returnOnAssets, 4),
            Math.Round(grossProfitMargin, 4),
            Math.Round(netProfitMargin, 4),
            Math.Round(assetTurnover, 4),
            Math.Round(receivableTurnover, 4),
            Math.Round(payableTurnover, 4),
            Math.Round(inventoryTurnover, 4),
            Math.Round(daysSalesOutstanding, 2),
            Math.Round(daysPayableOutstanding, 2),
            Math.Round(daysInventoryOutstanding, 2),
            Math.Round(cashConversionCycle, 2),
            Math.Round(workingCapital, 2),
            Math.Round(interestCoverage, 4));
    }

    public async Task<BreakEvenResponse> CalculateBreakEvenAsync(Guid companyId, int fiscalYear)
    {
        // Gather GL data for the fiscal year
        var yearStart = new DateTime(fiscalYear, 1, 1);
        var yearEnd = new DateTime(fiscalYear, 12, 31);

        var glData = await _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Include(l => l.Account)
            .Where(l => l.JournalEntry.CompanyId == companyId
                     && l.JournalEntry.Status == JournalEntryStatus.Posted
                     && l.JournalEntry.EntryDate >= yearStart
                     && l.JournalEntry.EntryDate <= yearEnd)
            .ToListAsync();

        var totalRevenue = glData
            .Where(l => l.Account.AccountType == AccountType.Revenue)
            .Sum(l => l.CreditAmount - l.DebitAmount);

        // Variable costs: COGS (account codes starting with 51xx)
        var variableCosts = glData
            .Where(l => l.Account.AccountType == AccountType.Expense && l.Account.AccountCode.StartsWith("51"))
            .Sum(l => l.DebitAmount - l.CreditAmount);

        // Fixed costs: all other expenses
        var fixedCosts = glData
            .Where(l => l.Account.AccountType == AccountType.Expense && !l.Account.AccountCode.StartsWith("51"))
            .Sum(l => l.DebitAmount - l.CreditAmount);

        var contributionMarginPercent = totalRevenue != 0
            ? (totalRevenue - variableCosts) / totalRevenue * 100
            : 0m;

        var breakEvenRevenue = contributionMarginPercent != 0
            ? fixedCosts / (contributionMarginPercent / 100)
            : 0m;

        var marginOfSafety = totalRevenue - breakEvenRevenue;
        var marginOfSafetyPercent = totalRevenue != 0 ? marginOfSafety / totalRevenue * 100 : 0m;

        return new BreakEvenResponse(
            fiscalYear,
            Math.Round(fixedCosts, 2),
            Math.Round(contributionMarginPercent, 4),
            Math.Round(breakEvenRevenue, 2),
            Math.Round(totalRevenue, 2),
            Math.Round(marginOfSafety, 2),
            Math.Round(marginOfSafetyPercent, 4));
    }

    // ===== Private Helpers =====

    private async Task<List<MonthlyGlRecord>> GetMonthlyGlDataAsync(Guid companyId, int fiscalYear)
    {
        var yearStart = new DateTime(fiscalYear, 1, 1);
        var yearEnd = new DateTime(fiscalYear, 12, 31);

        return await _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Include(l => l.Account)
            .Where(l => l.JournalEntry.CompanyId == companyId
                     && l.JournalEntry.Status == JournalEntryStatus.Posted
                     && l.JournalEntry.EntryDate >= yearStart
                     && l.JournalEntry.EntryDate <= yearEnd)
            .Select(l => new MonthlyGlRecord
            {
                Month = l.JournalEntry.EntryDate.Month,
                AccountId = l.AccountId,
                AccountType = l.Account.AccountType,
                DebitAmount = l.DebitAmount,
                CreditAmount = l.CreditAmount
            })
            .ToListAsync();
    }

    private static decimal ApplyAdjustment(decimal baseValue, ScenarioAssumption assumption)
    {
        return assumption.AdjustmentType switch
        {
            "Percentage" => baseValue * (1 + assumption.AdjustmentValue / 100),
            "Absolute" => baseValue + assumption.AdjustmentValue,
            _ => baseValue
        };
    }

    private static ScenarioResponse MapToScenarioResponse(FinancialScenario s) => new(
        s.Id, s.Name, s.Description, s.ScenarioType, s.FiscalYear,
        s.BaselineType, s.Status, s.Assumptions.Count(a => !a.IsDeleted), s.CreatedAt);

    private class MonthlyGlRecord
    {
        public int Month { get; set; }
        public Guid AccountId { get; set; }
        public AccountType AccountType { get; set; }
        public decimal DebitAmount { get; set; }
        public decimal CreditAmount { get; set; }
    }
}
