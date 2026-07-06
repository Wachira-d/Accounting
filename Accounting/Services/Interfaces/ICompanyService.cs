using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Company;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public record AddUserResult(bool WasInvited, string Email, Guid? InvitationId,
    // True when the invitation email was actually dispatched. False when the
    // invitation row was created but system email isn't configured — the UI
    // should then show InviteLink for the owner to share manually instead of
    // claiming the mail was sent.
    bool EmailSent = false,
    string? InviteLink = null);

public interface ICompanyService
{
    Task<CompanyResponse> CreateAsync(Guid userId, CreateCompanyRequest request);
    Task<CompanyResponse> GetByIdAsync(Guid companyId, Guid userId);
    Task<List<CompanyResponse>> GetUserCompaniesAsync(Guid userId);
    Task<CompanyResponse> UpdateAsync(Guid companyId, Guid userId, UpdateCompanyRequest request);
    Task<List<CompanyMemberResponse>> GetMembersAsync(Guid companyId, Guid userId);
    /// <summary>Add (or invite) a user to a company by email. When the email
    /// matches an existing user the link is created directly. When it
    /// doesn't, a CompanyInvitation row is created and an email goes out
    /// with a signup-and-accept link. Result.WasInvited distinguishes the
    /// two outcomes so the controller can surface the right toast.</summary>
    Task<AddUserResult> AddUserAsync(Guid companyId, Guid ownerId, AddCompanyUserRequest request);
    Task RemoveUserAsync(Guid companyId, Guid ownerId, Guid targetUserId);
    Task UpdateUserRoleAsync(Guid companyId, Guid ownerId, Guid targetUserId, UserRole newRole);
    Task UpdateMemberNameAsync(Guid companyId, Guid ownerId, Guid targetUserId, string newFullName);

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
