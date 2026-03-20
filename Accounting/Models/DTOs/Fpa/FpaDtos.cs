namespace Accounting.Models.DTOs.Fpa;

public record CreateScenarioRequest(
    string Name, string? Description, string ScenarioType,
    string BaselineType, Guid? BaselineScenarioId,
    List<ScenarioAssumptionRequest> Assumptions);

public record ScenarioAssumptionRequest(
    Guid AccountId, string AdjustmentType, decimal AdjustmentValue,
    int? Month, string? Notes);

public record UpdateScenarioRequest(
    string? Name = null, string? Description = null, bool? IsActive = null);

public record ScenarioResponse(
    Guid Id, string Name, string? Description, string ScenarioType,
    string BaselineType, bool IsActive,
    List<ScenarioAssumptionResponse> Assumptions,
    List<ScenarioResultResponse>? Results, DateTime CreatedAt);

public record ScenarioAssumptionResponse(
    Guid Id, Guid AccountId, string AccountCode, string AccountName,
    string AdjustmentType, decimal AdjustmentValue, int? Month, string? Notes);

public record ScenarioResultResponse(
    Guid Id, Guid AccountId, string AccountCode, string AccountName,
    int Month, decimal BaselineAmount, decimal AdjustedAmount,
    decimal VarianceAmount, decimal VariancePercent);

public record CreateKpiRequest(
    string Name, string? Description, string Formula,
    string Category, string Unit, decimal? TargetValue, string? TargetDirection);

public record KpiResponse(
    Guid Id, string Name, string? Description, string Formula,
    string Category, string Unit, decimal? TargetValue,
    string? TargetDirection, bool IsActive, DateTime CreatedAt);

public record KpiSnapshotResponse(
    Guid Id, Guid KpiId, string KpiName, int Year, int Month,
    decimal Value, decimal? TargetValue, decimal? VariancePercent, DateTime CalculatedAt);
