namespace Accounting.Models.DTOs.Organization;

// ===== Department =====

public record CreateDepartmentRequest(
    string Code,
    string Name,
    string? NameEn,
    string? Description,
    Guid? ParentDepartmentId,
    Guid? BranchId,
    // Cost-centre linkage — points at the existing AccountingDimension table
    // so payroll and benefit vouchers post to the correct departmental ledger.
    Guid? DimensionId,
    // Optional default GL account for documents raised against this dept.
    Guid? DefaultExpenseAccountId,
    Guid? ManagerEmployeeId);

public record UpdateDepartmentRequest(
    string? Code,
    string? Name,
    string? NameEn,
    string? Description,
    bool? IsActive,
    Guid? ParentDepartmentId,
    Guid? BranchId,
    Guid? DimensionId,
    Guid? DefaultExpenseAccountId,
    Guid? ManagerEmployeeId);

public record DepartmentResponse(
    Guid Id,
    string Code,
    string Name,
    string? NameEn,
    string? Description,
    bool IsActive,
    Guid? ParentDepartmentId,
    string? ParentDepartmentName,
    Guid? BranchId,
    string? BranchName,
    Guid? DimensionId,
    string? DimensionCode,
    string? DimensionName,
    Guid? DefaultExpenseAccountId,
    string? DefaultExpenseAccountCode,
    Guid? ManagerEmployeeId,
    string? ManagerName,
    int EmployeeCount,
    DateTime CreatedAt);

// ===== Position / Job title =====

public record CreatePositionRequest(
    string Code,
    string Title,
    string? TitleEn,
    string? Description,
    string? Band,
    decimal? MinSalary,
    decimal? MaxSalary);

public record UpdatePositionRequest(
    string? Code,
    string? Title,
    string? TitleEn,
    string? Description,
    string? Band,
    bool? IsActive,
    decimal? MinSalary,
    decimal? MaxSalary);

public record PositionResponse(
    Guid Id,
    string Code,
    string Title,
    string? TitleEn,
    string? Description,
    string? Band,
    bool IsActive,
    decimal? MinSalary,
    decimal? MaxSalary,
    int EmployeeCount,
    DateTime CreatedAt);

// ===== Reporting line lookup =====

public record DirectManagerInfo(
    Guid EmployeeId,
    string EmployeeName,
    Guid? ManagerEmployeeId,
    string? ManagerName,
    Guid? ManagerUserId,
    string? ManagerEmail,
    // Department head fallback when the employee has no direct manager —
    // approval can route to this person at Level 1 instead.
    Guid? DepartmentHeadEmployeeId,
    string? DepartmentHeadName);

// ===== Org chart =====

public record OrgChartNode(
    Guid EmployeeId,
    string EmployeeCode,
    string FullName,
    string? PositionTitle,
    string? DepartmentName,
    bool IsActive,
    List<OrgChartNode> DirectReports);
