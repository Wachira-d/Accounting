using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Dimension;

public record CreateDimensionRequest(
    string Code, string Name, string? NameEn,
    DimensionType DimensionType, Guid? ParentId,
    string? Description, string? ManagerName,
    string? ManagerEmail, decimal? AnnualBudget);

public record UpdateDimensionRequest(
    string? Name, string? NameEn, string? Description,
    string? ManagerName, string? ManagerEmail,
    decimal? AnnualBudget, bool? IsActive, int? SortOrder);

public record DimensionResponse(
    Guid Id, string Code, string Name, string? NameEn,
    DimensionType DimensionType, Guid? ParentId, int Level,
    string? Description, string? ManagerName,
    decimal? AnnualBudget, bool IsActive,
    List<DimensionResponse>? Children);

public record DimensionAllocationRequest(
    Guid DimensionId, decimal? Amount, decimal? Percent);

public record DimensionAllocationResponse(
    Guid DimensionId, string DimensionCode, string DimensionName,
    DimensionType DimensionType, decimal? AllocatedAmount,
    decimal? AllocatedPercent);

public record DimensionPnLResponse(
    Guid DimensionId, string DimensionName,
    DateTime FromDate, DateTime ToDate,
    decimal TotalRevenue, decimal TotalExpenses,
    decimal NetIncome, List<DimensionPnLLine> Lines);

public record DimensionPnLLine(
    string AccountCode, string AccountName, decimal Amount);

public record DimensionSummaryResponse(
    Guid DimensionId, string Code, string Name,
    DimensionType DimensionType, decimal TotalRevenue,
    decimal TotalExpenses, decimal NetIncome,
    decimal? BudgetAmount, decimal? Variance);

public record CreateBranchRequest(
    string Code, string Name, string? NameEn,
    string? Address, string? SubDistrict, string? District,
    string? Province, string? PostalCode, string? Phone,
    string? Email, string? TaxBranchCode, bool IsHeadOffice,
    string? ManagerName);

public record UpdateBranchRequest(
    string? Name, string? NameEn, string? Address,
    string? Phone, string? Email, string? TaxBranchCode,
    string? ManagerName, bool? IsActive);

public record BranchResponse(
    Guid Id, string Code, string Name, string? NameEn,
    string? Address, string? Province, string? TaxBranchCode,
    bool IsHeadOffice, bool IsActive, string? ManagerName);
