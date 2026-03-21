using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Ai;

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
