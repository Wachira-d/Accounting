namespace Accounting.Models.DTOs.Commission;

public record CreateCommissionPlanRequest(
    string Name, string? Description, string CalculationBasis,
    string CalculationMethod, bool IsActive, List<CommissionTierRequest> Tiers);

public record CommissionTierRequest(
    decimal FromAmount, decimal? ToAmount, decimal Rate);

public record UpdateCommissionPlanRequest(
    string? Name = null, string? Description = null, bool? IsActive = null);

public record CommissionPlanResponse(
    Guid Id, string Name, string? Description, string CalculationBasis,
    string CalculationMethod, bool IsActive,
    List<CommissionTierResponse> Tiers, DateTime CreatedAt);

public record CommissionTierResponse(
    Guid Id, decimal FromAmount, decimal? ToAmount, decimal Rate);

public record CreateCommissionAssignmentRequest(
    Guid CommissionPlanId, Guid EmployeeId, DateTime EffectiveFrom, DateTime? EffectiveTo);

public record CommissionAssignmentResponse(
    Guid Id, Guid CommissionPlanId, string PlanName,
    Guid EmployeeId, string EmployeeName,
    DateTime EffectiveFrom, DateTime? EffectiveTo, bool IsActive);

public record CommissionCalculationResponse(
    Guid Id, Guid EmployeeId, string EmployeeName, Guid CommissionPlanId,
    string PlanName, int Year, int Month,
    decimal BaseAmount, decimal CommissionAmount, string Status, DateTime CreatedAt);

public record AssignCommissionRequest(Guid? EmployeeId, Guid? UserId, DateTime StartDate, DateTime? EndDate);

public record CommissionCalcResponse(Guid? EmployeeId, string? EmployeeName, int Year, int Month, decimal BasisAmount, decimal CommissionAmount, string Status);
