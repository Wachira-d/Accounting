using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Commission;

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
