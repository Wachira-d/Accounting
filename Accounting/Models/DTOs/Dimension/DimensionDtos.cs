using Accounting.Models.Enums;
namespace Accounting.Models.DTOs.Dimension;

public record CreateDimensionRequest(
    string Code, string Name, DimensionType DimensionType,
    Guid? ParentId, string? ManagerName, string? ManagerEmail, decimal? AnnualBudget);

public record UpdateDimensionRequest(
    string? Name = null, string? ManagerName = null,
    string? ManagerEmail = null, decimal? AnnualBudget = null, bool? IsActive = null);

public record DimensionResponse(
    Guid Id, string Code, string Name, DimensionType DimensionType,
    Guid? ParentId, string? ParentName, int Level,
    string? ManagerName, string? ManagerEmail, decimal? AnnualBudget,
    bool IsActive, DateTime CreatedAt);

public record CreateBranchRequest(
    string Code, string Name, string? Address, string? Phone,
    string? TaxId, string? ManagerName);

public record BranchResponse(
    Guid Id, string Code, string Name, string? Address,
    string? Phone, string? TaxId, string? ManagerName,
    bool IsActive, DateTime CreatedAt);
