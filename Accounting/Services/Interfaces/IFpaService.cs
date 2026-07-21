using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Fpa;

namespace Accounting.Services.Interfaces;

/// <summary>
/// Financial Planning & Analysis
/// </summary>
public interface IFpaService
{
    // Scenarios
    Task<ScenarioResponse> CreateScenarioAsync(Guid companyId, CreateScenarioRequest request);
    Task<ScenarioResponse> GetScenarioAsync(Guid companyId, Guid scenarioId);
    Task<List<ScenarioResponse>> GetScenariosAsync(Guid companyId, int? fiscalYear = null);
    Task<ScenarioResponse> UpdateScenarioAsync(Guid companyId, Guid scenarioId, UpdateScenarioRequest request);
    Task DeleteScenarioAsync(Guid companyId, Guid scenarioId);

    // Assumptions
    Task<ScenarioResponse> AddAssumptionAsync(Guid companyId, Guid scenarioId, CreateAssumptionRequest request);
    Task RemoveAssumptionAsync(Guid companyId, Guid assumptionId);

    // Calculate & Compare
    Task<ScenarioResultsResponse> CalculateScenarioAsync(Guid companyId, Guid scenarioId);
    Task<ScenarioComparisonResponse> CompareAsync(Guid companyId, List<Guid> scenarioIds);

    // Financial KPIs
    Task<FinancialKpiResponse> CreateKpiAsync(Guid companyId, CreateFinancialKpiRequest request);
    Task<List<FinancialKpiResponse>> GetKpisAsync(Guid companyId);
    Task<List<KpiSnapshotResponse>> GetKpiHistoryAsync(Guid companyId, Guid kpiId, int months = 12);
    Task CalculateKpiSnapshotsAsync(Guid companyId, int year, int month);

    // Financial ratios
    Task<FinancialRatiosResponse> CalculateRatiosAsync(Guid companyId, DateTime asOfDate);
    Task<BreakEvenResponse> CalculateBreakEvenAsync(Guid companyId, int fiscalYear);
}
