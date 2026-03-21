namespace Accounting.Models.DTOs.Ai;

public record CategorizationResultResponse(
    Guid Id, string EntityType, Guid EntityId,
    Guid? SuggestedAccountId, string? SuggestedAccountName,
    string? SuggestedCategory, decimal Confidence,
    bool IsAccepted, bool IsRejected);

public record CreateAutoCatRuleRequest(
    string RuleName, string MatchType, string MatchField,
    string? MatchPattern, decimal? MinAmount, decimal? MaxAmount,
    Guid? TargetAccountId, Guid? TargetDimensionId,
    string? TargetCategory, int Priority);

public record UpdateAutoCatRuleRequest(
    string? RuleName, string? MatchPattern,
    decimal? MinAmount, decimal? MaxAmount,
    Guid? TargetAccountId, string? TargetCategory,
    int? Priority, bool? IsActive);

public record AutoCatRuleResponse(
    Guid Id, string RuleName, string MatchType, string MatchField,
    string? MatchPattern, Guid? TargetAccountId,
    string? TargetCategory, int Priority, int TimesApplied,
    bool IsAiGenerated, bool IsActive);

public record AnomalyResponse(
    Guid Id, string AnomalyType, string Severity,
    string EntityType, Guid EntityId, string Description,
    decimal? ExpectedValue, decimal? ActualValue,
    decimal? DeviationPercent, string Status, DateTime DetectedAt);

public record CreateForecastRequest(
    string Name, DateTime PeriodStart, DateTime PeriodEnd,
    string ForecastMethod);

public record CashFlowForecastResponse(
    Guid Id, string Name, DateTime PeriodStart, DateTime PeriodEnd,
    string ForecastMethod, decimal OpeningBalance,
    decimal ProjectedInflows, decimal ProjectedOutflows,
    decimal ProjectedClosingBalance, decimal? ActualClosingBalance,
    decimal AccuracyPercent, List<ForecastLineResponse> Lines);

public record ForecastLineResponse(
    DateTime PeriodDate, string Category, string FlowType,
    decimal ProjectedAmount, decimal? ActualAmount, decimal Confidence);
