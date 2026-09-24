namespace Accounting.Models.DTOs.Fpa;

/// <summary>รอบ 193 · A10: <c>BaselineType</c>/<c>BaselineScenarioId</c> เดิมบังคับ (non-nullable/ไม่มี default)
/// แต่ฟอร์มไม่เคยส่ง ⇒ สร้าง Scenario ไม่ได้เลย. ค่านี้ derive ได้: <c>FpaService.CalculateScenarioAsync</c> คำนวณฐานจาก
/// ข้อมูลบัญชีจริง (GL) เท่านั้น ⇒ null = "Actual" · ค่าอื่นถูกปฏิเสธเป็นไทย (รับไว้ = ป้ายโกหก)</summary>
public record CreateScenarioRequest(
    string Name, string? Description, string ScenarioType,
    int FiscalYear, string? BaselineType = null, Guid? BaselineScenarioId = null);

public record UpdateScenarioRequest(
    string? Name, string? Description, string? Status);

public record ScenarioResponse(
    Guid Id, string Name, string? Description, string ScenarioType,
    int FiscalYear, string BaselineType, string Status,
    int AssumptionCount, DateTime CreatedAt);

public record CreateAssumptionRequest(
    string Category, string Description, string AdjustmentType,
    decimal AdjustmentValue, Guid? AccountId, Guid? DimensionId,
    int? ApplyToMonth);

public record ScenarioResultsResponse(
    Guid ScenarioId, string ScenarioName,
    List<ScenarioMonthResult> MonthlyResults,
    decimal TotalBaselineRevenue, decimal TotalScenarioRevenue,
    decimal TotalBaselineExpenses, decimal TotalScenarioExpenses,
    decimal BaselineNetIncome, decimal ScenarioNetIncome);

public record ScenarioMonthResult(
    int Month, decimal BaselineRevenue, decimal ScenarioRevenue,
    decimal BaselineExpenses, decimal ScenarioExpenses,
    decimal BaselineNetIncome, decimal ScenarioNetIncome);

public record ScenarioComparisonResponse(
    List<ScenarioResultsResponse> Scenarios,
    string ComparisonSummaryJson);

public record CreateFinancialKpiRequest(
    string Name, string Code, string Category, string Formula,
    decimal? TargetValue, decimal? WarningThreshold,
    decimal? CriticalThreshold);

public record FinancialKpiResponse(
    Guid Id, string Name, string Code, string Category,
    decimal? TargetValue, decimal? LatestValue,
    string? LatestStatus, bool IsActive);

public record KpiSnapshotResponse(
    int Year, int Month, decimal Value, string? Status);

public record FinancialRatiosResponse(
    DateTime AsOfDate, decimal CurrentRatio, decimal QuickRatio,
    decimal DebtToEquity, decimal ReturnOnEquity, decimal ReturnOnAssets,
    decimal GrossProfitMargin, decimal NetProfitMargin,
    decimal AssetTurnover, decimal ReceivableTurnover,
    decimal PayableTurnover, decimal InventoryTurnover,
    decimal DaysSalesOutstanding, decimal DaysPayableOutstanding,
    decimal DaysInventoryOutstanding, decimal CashConversionCycle,
    decimal WorkingCapital, decimal InterestCoverage);

public record BreakEvenResponse(
    int FiscalYear, decimal TotalFixedCosts,
    decimal AverageContributionMarginPercent,
    decimal BreakEvenRevenue, decimal CurrentRevenue,
    decimal MarginOfSafety, decimal MarginOfSafetyPercent);
