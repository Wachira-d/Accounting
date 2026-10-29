using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Commission;

namespace Accounting.Services.Interfaces;

public interface ICommissionService
{
    // Plans
    Task<CommissionPlanResponse> CreatePlanAsync(Guid companyId, CreateCommissionPlanRequest request);
    Task<CommissionPlanResponse> GetPlanByIdAsync(Guid companyId, Guid planId);
    Task<List<CommissionPlanResponse>> GetPlansAsync(Guid companyId);
    Task<CommissionPlanResponse> UpdatePlanAsync(Guid companyId, Guid planId, UpdateCommissionPlanRequest request);
    Task DeletePlanAsync(Guid companyId, Guid planId);

    // Assignments
    Task AssignPlanAsync(Guid companyId, Guid planId, AssignCommissionRequest request);
    Task UnassignPlanAsync(Guid companyId, Guid assignmentId);

    // Calculation
    Task<List<CommissionCalcResponse>> CalculateAsync(Guid companyId, int year, int month);
    Task<List<CommissionCalcResponse>> GetCalculationsAsync(Guid companyId, int year, int month);
    Task ApproveCalculationsAsync(Guid companyId, int year, int month, string approvedBy);

    // รอบ 209 P5 (R-209-C6 · คำตัดสินข้อ 168 ฏ) — ค่าตอบแทนผู้ให้บริการของระบบจอง (CommissionService.Bookings.cs)
    Task<Accounting.Models.DTOs.Commission.ResourcePaySettingsResponse> GetResourcePayAsync(Guid companyId, Guid siteId);
    Task<Accounting.Models.DTOs.Commission.ResourcePayResponse> SaveResourcePayAsync(Guid companyId, Guid siteId, Guid resourceId,
        Accounting.Models.DTOs.Commission.SaveResourcePayRequest request, string actor);
    Task<List<Accounting.Helpers.InstructorPayLine>> GetInstructorPayAsync(Guid companyId, Guid siteId, DateTime from, DateTime to);
}
