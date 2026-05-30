namespace Accounting.Models.DTOs.Executive;

// ===== Executive Summary (one-page CEO view) =====
public record ExecutiveSummaryResponse(
    DateTime FromDate,
    DateTime ToDate,
    DateTime AsOfDate,
    ExecutiveKpi Kpi,
    FinancialRatios Ratios,
    CashPositionSummary CashPosition,
    List<ExecutiveAlert> Alerts,
    List<TrendPoint> RevenueTrend,
    List<TrendPoint> ExpenseTrend,
    List<TrendPoint> NetIncomeTrend,
    List<TopCustomerRow> TopCustomers,
    List<TopSupplierRow> TopSuppliers,
    BudgetVarianceSummary? BudgetSnapshot);

public record ExecutiveKpi(
    decimal Revenue, decimal RevenueLastPeriod, decimal RevenueGrowthPercent,
    decimal Expense, decimal ExpenseLastPeriod, decimal ExpenseGrowthPercent,
    decimal GrossProfit, decimal GrossProfitMarginPercent,
    decimal OperatingProfit, decimal OperatingMarginPercent,
    decimal NetIncome, decimal NetIncomeLastPeriod, decimal NetIncomeGrowthPercent,
    decimal NetProfitMarginPercent,
    decimal Ebitda, decimal EbitdaMarginPercent,
    decimal CashAndBank, decimal Receivables, decimal Payables,
    decimal WorkingCapital,
    int InvoiceCount, int OverdueInvoiceCount, decimal OverdueReceivables);

public record ExecutiveAlert(
    string Severity,           // info | warning | critical
    string Category,           // cash | ar | ap | margin | budget | inventory
    string Title,
    string Description,
    decimal? Amount = null,
    string? ActionUrl = null);

// ===== Financial Ratios =====
public record FinancialRatiosResponse(
    DateTime AsOfDate,
    DateTime PeriodFrom,
    DateTime PeriodTo,
    FinancialRatios Ratios,
    Dictionary<string, decimal> RawValues);   // raw account totals used

public record FinancialRatios(
    // Liquidity
    decimal CurrentRatio,           // Current Assets / Current Liabilities
    decimal QuickRatio,              // (Current Assets - Inventory) / Current Liabilities
    decimal CashRatio,               // Cash / Current Liabilities

    // Profitability
    decimal GrossProfitMargin,       // (Revenue - COGS) / Revenue * 100
    decimal OperatingMargin,         // Operating Profit / Revenue * 100
    decimal NetProfitMargin,         // Net Income / Revenue * 100
    decimal ReturnOnAssets,          // Net Income / Total Assets * 100
    decimal ReturnOnEquity,          // Net Income / Total Equity * 100

    // Activity / Efficiency
    decimal AssetTurnover,           // Revenue / Total Assets
    decimal InventoryTurnover,       // COGS / Avg Inventory
    decimal ReceivablesTurnover,     // Revenue / AR
    decimal PayablesTurnover,        // Purchases / AP
    decimal DaysSalesOutstanding,    // 365 / ReceivablesTurnover (DSO)
    decimal DaysPayablesOutstanding, // 365 / PayablesTurnover (DPO)
    decimal DaysInventoryOutstanding,// 365 / InventoryTurnover (DIO)
    decimal CashConversionCycle,     // DSO + DIO - DPO

    // Leverage
    decimal DebtToAssets,            // Total Liabilities / Total Assets
    decimal DebtToEquity,            // Total Liabilities / Total Equity
    decimal EquityMultiplier);       // Total Assets / Total Equity

// ===== Trend Analysis =====
public record TrendAnalysisResponse(
    string Granularity,              // monthly | quarterly | yearly
    int Periods,
    List<TrendPoint> Revenue,
    List<TrendPoint> Expense,
    List<TrendPoint> GrossProfit,
    List<TrendPoint> NetIncome,
    List<TrendPoint> Cash,
    decimal CompoundGrowthRevenue,   // CAGR for revenue
    decimal CompoundGrowthExpense);

public record TrendPoint(
    string Label,                    // "2026-04" or "Q1 2026"
    DateTime PeriodStart,
    DateTime PeriodEnd,
    decimal Value,
    decimal? PreviousPeriodValue = null,
    decimal? GrowthPercent = null);

// ===== Customer Analytics =====
public record CustomerAnalyticsResponse(
    DateTime FromDate,
    DateTime ToDate,
    int CustomerCount,
    int ActiveCustomerCount,
    decimal TotalRevenue,
    decimal AverageRevenuePerCustomer,
    List<CustomerRevenueRow> Customers,
    List<AbcSegment> AbcSegments,    // ABC analysis (Pareto 80/20)
    List<TopCustomerRow> TopCustomers,
    List<CustomerChurnRow> ChurnRisk);

public record CustomerRevenueRow(
    Guid ContactId, string Name, string? TaxId,
    decimal TotalRevenue, decimal SharePercent, int InvoiceCount,
    decimal OutstandingBalance, decimal OverdueBalance,
    DateTime? LastInvoiceDate);

public record TopCustomerRow(
    Guid ContactId, string Name,
    decimal Amount, decimal SharePercent, int Count);

public record TopSupplierRow(
    Guid ContactId, string Name,
    decimal Amount, decimal SharePercent, int Count);

public record AbcSegment(
    string Class,                    // A | B | C
    int CustomerCount,
    decimal TotalRevenue,
    decimal SharePercent);

public record CustomerChurnRow(
    Guid ContactId, string Name,
    DateTime? LastInvoiceDate,
    int DaysSinceLastInvoice,
    decimal HistoricRevenue);

// ===== Supplier Analytics =====
public record SupplierAnalyticsResponse(
    DateTime FromDate,
    DateTime ToDate,
    int SupplierCount,
    int ActiveSupplierCount,
    decimal TotalPurchases,
    decimal AveragePurchasePerSupplier,
    decimal SupplierConcentrationPercent, // Top-3 supplier share
    List<SupplierPurchaseRow> Suppliers,
    List<TopSupplierRow> TopSuppliers);

public record SupplierPurchaseRow(
    Guid ContactId, string Name, string? TaxId,
    decimal TotalPurchases, decimal SharePercent, int InvoiceCount,
    decimal OutstandingPayables,
    DateTime? LastPurchaseDate);

// ===== Product / Service Analytics =====
public record ProductAnalyticsResponse(
    DateTime FromDate,
    DateTime ToDate,
    decimal TotalRevenue,
    decimal TotalUnitsSold,
    int UniqueProductsSold,
    List<ProductSalesRow> Products,
    List<ProductSalesRow> TopByRevenue,
    List<ProductSalesRow> TopByQuantity,
    List<ProductSalesRow> TopByMargin,
    List<ProductSalesRow> SlowMovers);

public record ProductSalesRow(
    string ProductCode, string ProductName,
    decimal QuantitySold, decimal Revenue,
    decimal CostOfSales, decimal GrossProfit, decimal MarginPercent,
    int InvoiceCount, decimal SharePercent);

// ===== Budget vs Actual =====
public record BudgetVarianceResponse(
    Guid? BudgetId,
    string? BudgetName,
    int FiscalYear,
    DateTime FromDate,
    DateTime ToDate,
    BudgetVarianceSummary Summary,
    List<BudgetVarianceRow> Lines);

public record BudgetVarianceSummary(
    decimal RevenueBudget, decimal RevenueActual, decimal RevenueVariance, decimal RevenueVariancePercent,
    decimal ExpenseBudget, decimal ExpenseActual, decimal ExpenseVariance, decimal ExpenseVariancePercent,
    decimal NetIncomeBudget, decimal NetIncomeActual, decimal NetIncomeVariance);

public record BudgetVarianceRow(
    string AccountCode, string AccountName, string AccountType,
    decimal Budget, decimal Actual, decimal Variance, decimal VariancePercent,
    string Status);                  // "OnTrack" | "Over" | "Under"

// ===== Cash Flow Forecast =====
public record CashFlowForecastResponse(
    DateTime AsOfDate,
    decimal OpeningCash,
    decimal ProjectedClosingCash,
    int Days,                        // 30 / 60 / 90
    List<CashFlowForecastWeek> Weeks,
    CashFlowScenarioSummary? Scenarios = null);

public record CashFlowScenarioSummary(
    string OverallRisk,              // "Low" | "Moderate" | "High" | "Critical"
    decimal MinClosingBalance,
    DateTime? MinClosingDate,
    decimal MaxClosingBalance,
    List<string> KeyRiskFactors,
    List<CashFlowScenarioWeek> Weeks);

public record CashFlowScenarioWeek(
    int WeekNumber,
    decimal Best,
    decimal Expected,
    decimal Worst,
    decimal RiskIndex);

public record CashFlowForecastWeek(
    int WeekNumber,
    DateTime WeekStart,
    DateTime WeekEnd,
    decimal OpeningBalance,
    decimal ExpectedInflows,         // upcoming receivables due
    decimal ExpectedOutflows,        // upcoming payables due
    decimal NetChange,
    decimal ClosingBalance,
    decimal ConfidenceLevel,         // % of items with confirmed dates
    List<CashFlowForecastItem> Items);

public record CashFlowForecastItem(
    DateTime Date,
    string Description,
    string Source,                   // "AR" | "AP" | "Recurring" | "Estimate"
    decimal Amount,
    string Direction);               // "In" | "Out"

// ===== Break-Even Analysis =====
public record BreakEvenAnalysisResponse(
    DateTime FromDate,
    DateTime ToDate,
    decimal TotalRevenue,
    decimal TotalVariableCost,
    decimal TotalFixedCost,
    decimal ContributionMargin,
    decimal ContributionMarginRatio, // (Revenue - VC) / Revenue
    decimal BreakEvenSales,          // FC / CM Ratio (in baht)
    decimal BreakEvenUnits,          // FC / contribution per unit (if applicable)
    decimal MarginOfSafety,          // Revenue - BreakEven
    decimal MarginOfSafetyPercent,
    decimal OperatingLeverage,       // CM / Operating Profit
    string Assumption);              // explanation of fixed/variable split logic

// ===== Cash Position Summary =====
public record CashPositionSummary(
    decimal CashOnHand,              // 1111 - cash account
    decimal BankBalance,             // sum of bank accounts
    decimal Total,
    decimal AvailableNet,            // cash + bank - overdue payables
    int CashRunwayDays,              // cash / avg daily expense
    decimal AverageDailyExpense);

// ===== Sales Performance =====
public record SalesPerformanceResponse(
    DateTime FromDate,
    DateTime ToDate,
    decimal TotalRevenue,
    decimal AverageInvoiceValue,
    int InvoiceCount,
    decimal QuotationToInvoiceRatio, // # quotes converted / total quotes
    decimal CollectionEfficiencyPercent, // PaidAmount / TotalAmount
    List<MonthlyRevenueRow> ByMonth,
    List<DocumentTypeBreakdown> ByDocumentType,
    List<DailySalesPoint> Daily);

public record MonthlyRevenueRow(
    int Year, int Month, string MonthName,
    decimal Revenue, decimal Cost, decimal GrossProfit,
    int InvoiceCount, decimal AverageInvoice);

public record DocumentTypeBreakdown(
    string DocumentType, string Label,
    int Count, decimal Amount, decimal SharePercent);

public record DailySalesPoint(
    DateTime Date, decimal Revenue, int Count);

// ===== Profitability by Project =====
public record ProjectProfitabilityListResponse(
    DateTime FromDate,
    DateTime ToDate,
    decimal TotalProjectRevenue,
    decimal TotalProjectCost,
    decimal TotalProjectProfit,
    List<ProjectProfitabilityRow> Projects);

public record ProjectProfitabilityRow(
    Guid ProjectId, string Code, string Name, string Status,
    decimal Budget, decimal Revenue, decimal Cost, decimal Profit,
    decimal MarginPercent, decimal CompletionPercent, decimal BudgetVariance);
