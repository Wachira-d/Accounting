namespace Accounting.Models.DTOs.Hr;

public record CreateEmployeeProjectTimeRequest(
    Guid EmployeeId,
    /// <summary>Null = admin / internal time. Routes to overhead pool
    /// instead of a per-project ProjectCostEntry at allocation time.</summary>
    Guid? ProjectId,
    DateTime WorkDate,
    decimal Hours,
    string? Description = null,
    string Category = "Billable",
    Guid? ProjectTaskId = null,
    string? ExternalId = null,
    string? ExternalSystem = null);

public record UpdateEmployeeProjectTimeRequest(
    Guid? ProjectId = null,
    DateTime? WorkDate = null,
    decimal? Hours = null,
    string? Description = null,
    string? Category = null,
    Guid? ProjectTaskId = null);

public record EmployeeProjectTimeResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    Guid? ProjectId,
    string? ProjectNumber,
    string? ProjectName,
    Guid? ProjectTaskId,
    DateTime WorkDate,
    decimal Hours,
    string? Description,
    string Category,
    bool IsAllocated,
    Guid? AllocatedPayrollRunId,
    Guid? ProjectCostEntryId,
    string? ExternalId,
    string? ExternalSystem,
    DateTime? LastSyncedAt);

/// <summary>Bulk-sync envelope. ExternalSystem identifies the source
/// (Jibble, Hubstaff, parent SAP, etc.). Each row is upserted on
/// (CompanyId, ExternalSystem, ExternalId) — existing rows update,
/// new rows insert. Rows without ExternalId are inserted blind.</summary>
public record SyncEmployeeProjectTimesRequest(
    string ExternalSystem,
    List<CreateEmployeeProjectTimeRequest> Rows);

public record SyncEmployeeProjectTimesResponse(
    int Inserted,
    int Updated,
    int Skipped,
    List<string> Errors);

/// <summary>One row per project (or null=admin) for a payroll run after
/// labor cost allocation has run. Sum of Amount equals the
/// employee's gross salary for that period.</summary>
public record PayrollLabourAllocationLine(
    Guid? ProjectId,
    string? ProjectName,
    decimal Hours,
    decimal HoursShare,            // 0..1
    decimal AllocatedAmount,
    string CostBehavior);

public record PayrollLabourAllocationResponse(
    Guid PayrollRunId,
    int Year, int Month,
    int EmployeesAllocated,
    int CostEntriesCreated,
    decimal TotalAllocated,
    decimal UnallocatedAdmin,
    List<PayrollLabourAllocationLine> Summary);

/// <summary>Monthly fix-vs-variable cost report. Pulls from ProjectCostEntry
/// (labor + materials + overhead) using the per-row CostBehavior flag.
/// Filter by project to scope to one project, or leave null for company-
/// wide.</summary>
public record FixVariableCostReport(
    int Year, int Month,
    Guid? ProjectId,
    string? ProjectName,
    decimal FixedCost,
    decimal VariableCost,
    decimal AdminOverhead,
    decimal TotalCost,
    List<FixVariableCostBreakdown> ByCostType);

public record FixVariableCostBreakdown(
    string CostType,             // Labor, Material, Subcontract, Overhead, Travel
    decimal Fixed,
    decimal Variable,
    decimal Total);

public record FixVariableCostMonthlyTrend(
    int Year, int Month,
    decimal FixedCost,
    decimal VariableCost,
    decimal TotalCost);
