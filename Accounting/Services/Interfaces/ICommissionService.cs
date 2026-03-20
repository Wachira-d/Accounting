using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface ICommissionService
{
    // Plans
    Task<CommissionPlanResponse> CreatePlanAsync(Guid companyId, CreateCommissionPlanRequest request);
    Task<List<CommissionPlanResponse>> GetPlansAsync(Guid companyId);
    Task<CommissionPlanResponse> UpdatePlanAsync(Guid companyId, Guid planId, UpdateCommissionPlanRequest request);

    // Assignments
    Task AssignPlanAsync(Guid companyId, Guid planId, AssignCommissionRequest request);
    Task UnassignPlanAsync(Guid companyId, Guid assignmentId);

    // Calculation
    Task<List<CommissionCalcResponse>> CalculateAsync(Guid companyId, int year, int month);
    Task<List<CommissionCalcResponse>> GetCalculationsAsync(Guid companyId, int year, int month);
    Task ApproveCalculationsAsync(Guid companyId, int year, int month, string approvedBy);
}

public record CreateCommissionPlanRequest(string Name, string? Description, string CalculationBasis, string CalculationMethod, decimal? FlatRate, List<CommissionTierRequest>? Tiers);
public record UpdateCommissionPlanRequest(string? Name, string? Description, decimal? FlatRate, bool? IsActive);
public record CommissionTierRequest(decimal FromAmount, decimal? ToAmount, decimal Rate);
public record CommissionPlanResponse(Guid Id, string Name, string? Description, string CalculationBasis, string CalculationMethod, decimal? FlatRate, bool IsActive, List<CommissionTierResponse> Tiers);
public record CommissionTierResponse(decimal FromAmount, decimal? ToAmount, decimal Rate);
public record AssignCommissionRequest(Guid? EmployeeId, Guid? UserId, DateTime StartDate, DateTime? EndDate);
public record CommissionCalcResponse(Guid? EmployeeId, string? EmployeeName, int Year, int Month, decimal BasisAmount, decimal CommissionAmount, string Status);
