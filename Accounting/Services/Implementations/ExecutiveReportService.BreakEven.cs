using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<BreakEvenAnalysisResponse> GetBreakEvenAnalysisAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var revenue = await SumRevenueAsync(companyId, fromDate, toDate);
        var cogs = await SumCogsAsync(companyId, fromDate, toDate);
        var totalExpense = await SumExpenseAsync(companyId, fromDate, toDate);
        var operatingExpense = totalExpense - cogs;

        // Variable Cost approximation:
        //  - COGS is variable
        //  - Other operating expenses split: 30% variable, 70% fixed (industry rule of thumb)
        var variableCost = cogs + (operatingExpense * 0.30m);
        var fixedCost = operatingExpense * 0.70m;

        var contributionMargin = revenue - variableCost;
        var cmRatio = SafeDiv(contributionMargin, revenue);
        var breakEvenSales = cmRatio == 0 ? 0 : fixedCost / cmRatio;
        var marginOfSafety = revenue - breakEvenSales;
        var marginOfSafetyPct = Pct(marginOfSafety, revenue);
        var operatingProfit = contributionMargin - fixedCost;
        var operatingLeverage = SafeDiv(contributionMargin, operatingProfit);

        return new BreakEvenAnalysisResponse(
            fromDate, toDate,
            R2(revenue), R2(variableCost), R2(fixedCost),
            R2(contributionMargin), R2(Math.Round(cmRatio * 100m, 2)),
            R2(breakEvenSales), 0,
            R2(marginOfSafety), marginOfSafetyPct,
            R2(operatingLeverage),
            "ค่าใช้จ่ายผันแปร = COGS + 30% ของค่าใช้จ่ายดำเนินงาน; ค่าใช้จ่ายคงที่ = 70% ของค่าใช้จ่ายดำเนินงาน");
    }
}
