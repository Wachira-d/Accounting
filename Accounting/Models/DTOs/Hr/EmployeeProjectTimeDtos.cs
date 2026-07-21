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
    string? ExternalSystem = null,
    // ===== Attendance metadata (optional) =====
    // Drives auto-computed OT pay, per-diem, accommodation, and OT-meal
    // allowances in payroll calc. Default values keep behaviour identical
    // to the pre-attendance world when an external system isn't pushing.
    decimal? OvertimeHours = null,
    bool IsHoliday = false,
    bool HasPerDiem = false,
    bool HasAccommodation = false,
    bool HasOvertimeMeal = false,
    string? AttendanceMetadataJson = null);

public record UpdateEmployeeProjectTimeRequest(
    Guid? ProjectId = null,
    DateTime? WorkDate = null,
    decimal? Hours = null,
    string? Description = null,
    string? Category = null,
    Guid? ProjectTaskId = null,
    decimal? OvertimeHours = null,
    bool? IsHoliday = null,
    bool? HasPerDiem = null,
    bool? HasAccommodation = null,
    bool? HasOvertimeMeal = null,
    string? AttendanceMetadataJson = null);

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
    DateTime? LastSyncedAt,
    decimal? OvertimeHours = null,
    bool IsHoliday = false,
    bool HasPerDiem = false,
    bool HasAccommodation = false,
    bool HasOvertimeMeal = false,
    string? AttendanceMetadataJson = null);

// ===== Compensation profile DTOs =====

public record CompensationProfileRequest(
    decimal? OvertimeRateMultiplierWeekday = null,
    decimal? OvertimeRateMultiplierHoliday = null,
    decimal? PerDiemRate = null,
    decimal? AccommodationAllowance = null,
    decimal? OvertimeMealAllowance = null,
    string? CustomBenefitsJson = null);

public record CompensationProfileResponse(
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    decimal? OvertimeRateMultiplierWeekday,
    decimal? OvertimeRateMultiplierHoliday,
    decimal? PerDiemRate,
    decimal? AccommodationAllowance,
    decimal? OvertimeMealAllowance,
    string? CustomBenefitsJson,
    /// <summary>Effective values after merging with CompanyCompensationDefaults.
    /// What the payroll calc will actually use.</summary>
    CompensationEffectiveRates Effective);

public record CompensationEffectiveRates(
    decimal OvertimeRateMultiplierWeekday,
    decimal OvertimeRateMultiplierHoliday,
    decimal PerDiemRate,
    decimal AccommodationAllowance,
    decimal OvertimeMealAllowance,
    decimal StandardWorkHoursPerDay,
    decimal StandardWorkDaysPerMonth);

public record CompanyCompensationDefaultsRequest(
    decimal? OvertimeRateMultiplierWeekday = null,
    decimal? OvertimeRateMultiplierHoliday = null,
    decimal? PerDiemRate = null,
    decimal? AccommodationAllowance = null,
    decimal? OvertimeMealAllowance = null,
    decimal? DailyMealAllowance = null,
    decimal? StandardWorkHoursPerDay = null,
    decimal? StandardWorkDaysPerMonth = null,
    List<CustomAllowanceItem>? CustomAllowances = null);

public record CompanyCompensationDefaultsResponse(
    decimal OvertimeRateMultiplierWeekday,
    decimal OvertimeRateMultiplierHoliday,
    decimal PerDiemRate,
    decimal AccommodationAllowance,
    decimal OvertimeMealAllowance,
    decimal DailyMealAllowance,
    decimal StandardWorkHoursPerDay,
    decimal StandardWorkDaysPerMonth,
    List<CustomAllowanceItem> CustomAllowances);

/// <summary>Allowance ที่บริษัทตั้งเอง — ค่าโทร ค่าน้ำมัน ค่าเดินทาง ฯลฯ.
/// Code/Name ระบุชนิด, Type "Daily" (คูณวันทำงาน) vs "Monthly" (ก้อนเดียว
/// ต่อรอบ), IsTaxable=true → รวมในฐาน WHT (ปกติ); false → สวัสดิการที่
/// ยกเว้นภาษี (เช่น ค่ารักษาพยาบาล ตามมาตรา 42(17)).</summary>
public record CustomAllowanceItem(
    string Code, string Name, string Type, decimal Amount, bool IsTaxable = true);

/// <summary>Preview the attendance-based pay extras (OT + per-diem +
/// accommodation + OT-meal) for one employee over a date window —
/// reads EmployeeProjectTime rows in the window, applies the
/// employee's effective compensation rates, returns the breakdown.
/// Doesn't post anything; intended for "what would the payroll look
/// like?" reviews before running the actual payroll calc.</summary>
public record AttendancePayPreview(
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    DateTime From,
    DateTime To,
    decimal BaseSalary,
    decimal HourlyRate,                 // base / (workDays × workHours)
    CompensationEffectiveRates Effective,
    decimal RegularHours,
    decimal OvertimeHoursWeekday,
    decimal OvertimeHoursHoliday,
    decimal OvertimePay,
    int PerDiemDays,
    decimal PerDiemTotal,
    int AccommodationNights,
    decimal AccommodationTotal,
    int OvertimeMealDays,
    decimal OvertimeMealTotal,
    decimal TotalExtras,                // OT + per-diem + accommodation + OT-meal
    List<AttendanceDayDetail> Days);

public record AttendanceDayDetail(
    DateTime Date,
    decimal Hours,
    decimal OvertimeHours,
    bool IsHoliday,
    bool HasPerDiem,
    bool HasAccommodation,
    bool HasOvertimeMeal,
    decimal OvertimePay,
    decimal AllowancePay);

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
