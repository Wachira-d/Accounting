using Accounting.Models.DTOs.Company;

namespace Accounting.Services.Interfaces;

public interface IRolePermissionService
{
    Task<List<CompanyRoleResponse>> GetRolesAsync(Guid companyId, Guid userId);
    Task<CompanyRoleResponse> GetRoleByIdAsync(Guid companyId, Guid roleId, Guid userId);
    Task<CompanyRoleResponse> CreateRoleAsync(Guid companyId, Guid userId, CreateCompanyRoleRequest request);
    Task<CompanyRoleResponse> UpdateRoleAsync(Guid companyId, Guid roleId, Guid userId, UpdateCompanyRoleRequest request);
    Task DeleteRoleAsync(Guid companyId, Guid roleId, Guid userId);
    Task AssignRoleAsync(Guid companyId, Guid targetUserId, Guid actingUserId, AssignCustomRoleRequest request);
    Task<MyPermissionsResponse> GetMyPermissionsAsync(Guid companyId, Guid userId);
    Task SeedDefaultRolesAsync(Guid companyId);
}
