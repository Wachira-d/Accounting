using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Organization;

namespace Accounting.Services.Interfaces;

/// <summary>
/// Organisation-structure engine: Departments + Positions + reporting
/// lines. Departments map to the existing accounting cost-centre system
/// (<see cref="Accounting.Models.Entities.AccountingDimension"/>) so the
/// payroll / benefit vouchers built earlier in the session post to the
/// correct departmental ledger automatically.
/// </summary>
public interface IOrganizationService
{
    // ===== Departments =====
    Task<DepartmentResponse> CreateDepartmentAsync(Guid companyId, CreateDepartmentRequest request, string createdBy);
    Task<DepartmentResponse> GetDepartmentAsync(Guid companyId, Guid departmentId);
    Task<List<DepartmentResponse>> GetDepartmentsAsync(Guid companyId, bool includeInactive = false);
    Task<DepartmentResponse> UpdateDepartmentAsync(Guid companyId, Guid departmentId, UpdateDepartmentRequest request);
    Task DeleteDepartmentAsync(Guid companyId, Guid departmentId);

    // ===== Positions =====
    Task<PositionResponse> CreatePositionAsync(Guid companyId, CreatePositionRequest request, string createdBy);
    Task<PositionResponse> GetPositionAsync(Guid companyId, Guid positionId);
    Task<List<PositionResponse>> GetPositionsAsync(Guid companyId, bool includeInactive = false);
    Task<PositionResponse> UpdatePositionAsync(Guid companyId, Guid positionId, UpdatePositionRequest request);
    Task DeletePositionAsync(Guid companyId, Guid positionId);

    // ===== Reporting lines =====
    /// <summary>Resolve the direct manager's UserId for an employee. Used
    /// by the leave / advance / claim workflows to auto-route Level 1
    /// approval to the requester's manager. Returns null when the
    /// employee has no direct manager (top of the chain) or the manager
    /// has no linked User account.</summary>
    Task<Guid?> GetDirectManagerUserIdAsync(Guid companyId, Guid employeeId);

    /// <summary>Full direct-manager info for the UI: includes manager
    /// name, email, UserId, and a department-head fallback when no
    /// direct manager is set.</summary>
    Task<DirectManagerInfo> GetDirectManagerInfoAsync(Guid companyId, Guid employeeId);

    /// <summary>Build the full org-chart tree rooted at employees with no
    /// direct manager. Used by the org-structure UI.</summary>
    Task<List<OrgChartNode>> GetOrgChartAsync(Guid companyId);
}
