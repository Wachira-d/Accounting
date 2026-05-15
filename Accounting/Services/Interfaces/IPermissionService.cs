namespace Accounting.Services.Interfaces;

/// <summary>
/// Granular permission lookup driven by the existing CompanyRole +
/// CompanyRolePermission tables. Services call HasPermissionAsync when a
/// sensitive action (approve / disburse / pay) needs a finer-grained
/// check than UserRole alone. This is additive — it does not replace
/// the existing authorisation middleware that gates login + menu access.
/// </summary>
public interface IPermissionService
{
    /// <summary>True when ANY company-role the user holds in
    /// <paramref name="companyId"/> grants <paramref name="permissionKey"/>
    /// (with CanAccess = true). Always true for Owner / SystemAdmin
    /// company-roles (the documented super-user override).</summary>
    Task<bool> HasPermissionAsync(Guid companyId, Guid userId, string permissionKey);

    /// <summary>List of permission keys granted to the user across all
    /// their company-roles. Useful when the UI wants to render different
    /// buttons / sections based on the user's grant set.</summary>
    Task<List<string>> GetUserPermissionsAsync(Guid companyId, Guid userId);
}
