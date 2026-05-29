using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Company;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface ICompanyService
{
    Task<CompanyResponse> CreateAsync(Guid userId, CreateCompanyRequest request);
    Task<CompanyResponse> GetByIdAsync(Guid companyId, Guid userId);
    Task<List<CompanyResponse>> GetUserCompaniesAsync(Guid userId);
    Task<CompanyResponse> UpdateAsync(Guid companyId, Guid userId, UpdateCompanyRequest request);
    Task<List<CompanyMemberResponse>> GetMembersAsync(Guid companyId, Guid userId);
    Task AddUserAsync(Guid companyId, Guid ownerId, AddCompanyUserRequest request);
    Task RemoveUserAsync(Guid companyId, Guid ownerId, Guid targetUserId);
    Task UpdateUserRoleAsync(Guid companyId, Guid ownerId, Guid targetUserId, UserRole newRole);

    /// <summary>Throws UnauthorizedAccessException if the user is not an Owner of the given
    /// company (or a platform SystemAdmin). Use to guard sensitive endpoints like period close,
    /// API-key creation, webhook registration — operations that should be Owner-only.</summary>
    Task EnsureOwnerAccessAsync(Guid companyId, Guid userId);

    /// <summary>Auto-attach a brand-new company to the creating user's
    /// active AccountSubscription (License) when one exists with free slots.
    /// CreateAsync calls this internally; the signup/SSO paths in AuthService
    /// also need to call it so an existing License-holder who creates a new
    /// company via those paths doesn't end up with an orphaned FreeTrial
    /// subscription instead of riding the License.</summary>
    Task EnsureSubscriptionForNewCompanyAsync(Guid companyId, Guid userId);
}
