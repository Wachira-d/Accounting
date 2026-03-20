using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IAiService
{
    // Auto-categorization
    Task<CategorizationResultResponse> AutoCategorizeAsync(Guid companyId, string entityType, Guid entityId);
    Task<List<CategorizationResultResponse>> BatchCategorizeAsync(Guid companyId, string entityType, List<Guid> entityIds);
    Task AcceptCategorizationAsync(Guid companyId, Guid resultId, Guid userId);
    Task RejectCategorizationAsync(Guid companyId, Guid resultId, Guid userId);

    // Rules management
    Task<AutoCatRuleResponse> CreateRuleAsync(Guid companyId, CreateAutoCatRuleRequest request);
    Task<List<AutoCatRuleResponse>> GetRulesAsync(Guid companyId);
    Task<AutoCatRuleResponse> UpdateRuleAsync(Guid companyId, Guid ruleId, UpdateAutoCatRuleRequest request);
    Task DeleteRuleAsync(Guid companyId, Guid ruleId);
    Task<List<AutoCatRuleResponse>> LearnRulesFromHistoryAsync(Guid companyId);

    // Anomaly detection
    Task<List<AnomalyResponse>> DetectAnomaliesAsync(Guid companyId, DateTime? fromDate = null, DateTime? toDate = null);
    Task<List<AnomalyResponse>> GetAnomaliesAsync(Guid companyId, string? status = null);
    Task AcknowledgeAnomalyAsync(Guid companyId, Guid anomalyId);
    Task ResolveAnomalyAsync(Guid companyId, Guid anomalyId, string resolutionNotes, string resolvedBy);
    Task MarkFalsePositiveAsync(Guid companyId, Guid anomalyId);

    // Cash flow forecasting
    Task<CashFlowForecastResponse> GenerateForecastAsync(Guid companyId, CreateForecastRequest request);
    Task<CashFlowForecastResponse> GetForecastAsync(Guid companyId, Guid forecastId);
    Task<List<CashFlowForecastResponse>> GetForecastsAsync(Guid companyId);
}

// DTOs
public record CategorizationResultResponse(Guid Id, string EntityType, Guid EntityId, Guid? SuggestedAccountId, string? SuggestedAccountName, string? SuggestedCategory, decimal Confidence, bool IsAccepted, bool IsRejected);

public record CreateAutoCatRuleRequest(string RuleName, string MatchType, string MatchField, string? MatchPattern, decimal? MinAmount, decimal? MaxAmount, Guid? TargetAccountId, Guid? TargetDimensionId, string? TargetCategory, int Priority);
public record UpdateAutoCatRuleRequest(string? RuleName, string? MatchPattern, decimal? MinAmount, decimal? MaxAmount, Guid? TargetAccountId, string? TargetCategory, int? Priority, bool? IsActive);
public record AutoCatRuleResponse(Guid Id, string RuleName, string MatchType, string MatchField, string? MatchPattern, Guid? TargetAccountId, string? TargetCategory, int Priority, int TimesApplied, bool IsAiGenerated, bool IsActive);

public record AnomalyResponse(Guid Id, string AnomalyType, string Severity, string EntityType, Guid EntityId, string Description, decimal? ExpectedValue, decimal? ActualValue, decimal? DeviationPercent, string Status, DateTime DetectedAt);

public record CreateForecastRequest(string Name, DateTime PeriodStart, DateTime PeriodEnd, string ForecastMethod);
public record CashFlowForecastResponse(Guid Id, string Name, DateTime PeriodStart, DateTime PeriodEnd, string ForecastMethod, decimal OpeningBalance, decimal ProjectedInflows, decimal ProjectedOutflows, decimal ProjectedClosingBalance, decimal? ActualClosingBalance, decimal AccuracyPercent, List<ForecastLineResponse> Lines);
public record ForecastLineResponse(DateTime PeriodDate, string Category, string FlowType, decimal ProjectedAmount, decimal? ActualAmount, decimal Confidence);
