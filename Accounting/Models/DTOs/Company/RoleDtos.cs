using System.ComponentModel.DataAnnotations;

namespace Accounting.Models.DTOs.Company;

public record CreateCompanyRoleRequest(
    [property: Required, StringLength(100)] string Name,
    string? Description,
    string? Color,
    string? Icon,
    List<string>? AllowedMenuIds);

public record UpdateCompanyRoleRequest(
    [property: StringLength(100)] string? Name,
    string? Description,
    string? Color,
    string? Icon,
    int? SortOrder,
    List<string>? AllowedMenuIds);

public record CompanyRoleResponse(
    Guid Id,
    string Name,
    string? Description,
    string Color,
    string Icon,
    bool IsSystemRole,
    int SortOrder,
    int MemberCount,
    List<string> AllowedMenuIds);

public record AssignCustomRoleRequest(
    Guid RoleId);

public record MyPermissionsResponse(
    string RoleName,
    bool IsOwnerOrAdmin,
    List<string> AllowedMenuIds);
