using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<FinancialRatiosResponse> GetFinancialRatiosAsync(Guid companyId, DateTime asOfDate, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var from = fromDate ?? new DateTime(asOfDate.Year, 1, 1);
        var to = toDate ?? asOfDate;

        var currentAssets = await GetBalanceAsync(companyId, AccountType.Asset, asOfDate, "11");
        var inventory = await GetBalanceAsync(companyId, AccountType.Asset, asOfDate, "115");
        var cashBank = await GetCashAndBankAsync(companyId, asOfDate);
        var currentLiabilities = await GetBalanceAsync(companyId, AccountType.Liability, asOfDate, "21");
        var totalAssets = await GetBalanceAsync(companyId, AccountType.Asset, asOfDate);
        var totalLiabilities = await GetBalanceAsync(companyId, AccountType.Liability, asOfDate);
        var totalEquity = await GetBalanceAsync(companyId, AccountType.Equity, asOfDate);

        var revenue = await SumRevenueAsync(companyId, from, to);
        var cogs = await SumCogsAsync(companyId, from, to);
        var expense = await SumExpenseAsync(companyId, from, to);
        var netIncome = revenue - expense;
        var operatingProfit = revenue - cogs - (expense - cogs);

        var ar = await GetBalanceAsync(companyId, AccountType.Asset, asOfDate, "113");
        var ap = await GetBalanceAsync(companyId, AccountType.Liability, asOfDate, "212");

        var purchases = await SumPurchasesAsync(companyId, from, to);
        var avgInventory = inventory; // simplified

        var receivablesTurnover = SafeDiv(revenue, ar);
        var payablesTurnover = SafeDiv(purchases, ap);
        var inventoryTurnover = SafeDiv(cogs, avgInventory);

        var dso = receivablesTurnover == 0 ? 0 : 365m / receivablesTurnover;
        var dpo = payablesTurnover == 0 ? 0 : 365m / payablesTurnover;
        var dio = inventoryTurnover == 0 ? 0 : 365m / inventoryTurnover;

        var ratios = new FinancialRatios(
            R2(SafeDiv(currentAssets, currentLiabilities)),
            R2(SafeDiv(currentAssets - inventory, currentLiabilities)),
            R2(SafeDiv(cashBank, currentLiabilities)),
            Pct(revenue - cogs, revenue),
            Pct(operatingProfit, revenue),
            Pct(netIncome, revenue),
            Pct(netIncome, totalAssets),
            Pct(netIncome, totalEquity),
            R2(SafeDiv(revenue, totalAssets)),
            R2(inventoryTurnover),
            R2(receivablesTurnover),
            R2(payablesTurnover),
            R2(dso), R2(dpo), R2(dio),
            R2(dso + dio - dpo),
            R2(SafeDiv(totalLiabilities, totalAssets)),
            R2(SafeDiv(totalLiabilities, totalEquity)),
            R2(SafeDiv(totalAssets, totalEquity)));

        var raw = new Dictionary<string, decimal>
        {
            ["CurrentAssets"] = R2(currentAssets),
            ["Inventory"] = R2(inventory),
            ["CashAndBank"] = R2(cashBank),
            ["CurrentLiabilities"] = R2(currentLiabilities),
            ["TotalAssets"] = R2(totalAssets),
            ["TotalLiabilities"] = R2(totalLiabilities),
            ["TotalEquity"] = R2(totalEquity),
            ["Revenue"] = R2(revenue),
            ["COGS"] = R2(cogs),
            ["Expense"] = R2(expense),
            ["NetIncome"] = R2(netIncome),
            ["OperatingProfit"] = R2(operatingProfit),
            ["AR"] = R2(ar),
            ["AP"] = R2(ap),
            ["Purchases"] = R2(purchases)
        };

        return new FinancialRatiosResponse(asOfDate, from, to, ratios, raw);
    }

    private async Task<decimal> SumPurchasesAsync(Guid companyId, DateTime from, DateTime to)
    {
        return await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense)
            .Where(d => d.DocumentDate >= from && d.DocumentDate <= to)
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .SumAsync(d => (decimal?)d.TotalAmount) ?? 0;
    }
}
