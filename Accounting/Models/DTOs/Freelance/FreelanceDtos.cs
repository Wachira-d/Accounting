using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Freelance;

// ===== Invitation =====
public record InviteFreelanceRequest(
    string InviteeEmail,
    string InviteeName,
    string? InviteePhone,
    DateTime AccessStartDate,
    DateTime AccessEndDate,
    FreelanceAccessConfig AccessConfig);

public record FreelanceAccessConfig(
    AccessScope GeneralScope = AccessScope.ReadOnly,
    bool CanViewChartOfAccounts = true,
    bool CanEditChartOfAccounts = false,
    bool CanViewJournalEntries = true,
    bool CanCreateJournalEntries = false,
    bool CanPostJournalEntries = false,
    bool CanViewDocuments = true,
    bool CanCreateDocuments = false,
    bool CanApproveDocuments = false,
    bool CanViewTaxReports = true,
    bool CanCreateTaxReports = false,
    bool CanFileTaxReports = false,
    bool CanViewBankAccounts = true,
    bool CanReconcile = false,
    bool CanViewReports = true,
    bool CanExportData = false,
    bool CanCloseFiscalPeriod = false,
    bool CanManageContacts = false,
    bool CanViewPayments = true,
    bool CanCreatePayments = false,
    bool CanManageFixedAssets = false,
    bool CanViewSensitiveData = false,
    bool CanViewCostData = false,
    string? AllowedIpAddresses = null,
    string? AccessStartTime = null,
    string? AccessEndTime = null,
    string? AllowedDaysOfWeek = null,
    int MaxActionsPerDay = 1000,
    int? AllowedFiscalYear = null,
    int? AllowedFiscalMonth = null);

public record InvitationResponse(
    Guid Id,
    Guid CompanyId,
    string InviteeEmail,
    string InviteeName,
    FreelanceInvitationStatus Status,
    DateTime ExpiresAt,
    DateTime? AcceptedAt,
    DateTime CreatedAt);

public record AcceptInvitationRequest(
    string Token,
    string? Password);  // ถ้ายังไม่มี account

// ===== Freelance Access =====
public record UpdateFreelanceAccessRequest(
    DateTime? AccessEndDate,
    bool? IsActive,
    FreelanceAccessConfig? AccessConfig);

public record FreelanceAccessResponse(
    Guid Id,
    Guid CompanyId,
    string CompanyName,
    Guid UserId,
    string UserName,
    string UserEmail,
    DateTime AccessStartDate,
    DateTime AccessEndDate,
    bool IsActive,
    AccessScope GeneralScope,
    FreelancePermissions Permissions,
    FreelanceRestrictions Restrictions,
    List<FreelanceTaskSummary> ActiveTasks);

public record FreelancePermissions(
    bool CanViewChartOfAccounts,
    bool CanEditChartOfAccounts,
    bool CanViewJournalEntries,
    bool CanCreateJournalEntries,
    bool CanPostJournalEntries,
    bool CanViewDocuments,
    bool CanCreateDocuments,
    bool CanApproveDocuments,
    bool CanViewTaxReports,
    bool CanCreateTaxReports,
    bool CanFileTaxReports,
    bool CanViewBankAccounts,
    bool CanReconcile,
    bool CanViewReports,
    bool CanExportData,
    bool CanCloseFiscalPeriod,
    bool CanManageContacts,
    bool CanViewPayments,
    bool CanCreatePayments,
    bool CanManageFixedAssets);

public record FreelanceRestrictions(
    string? AllowedIpAddresses,
    string? AccessTimeWindow,
    string? AllowedDaysOfWeek,
    int MaxActionsPerDay,
    int TodayActionCount,
    int? AllowedFiscalYear,
    int? AllowedFiscalMonth);

// ===== Task Management =====
public record CreateFreelanceTaskRequest(
    Guid FreelanceAccessId,
    string Title,
    string? Description,
    FreelanceTaskType TaskType,
    DateTime? DueDate,
    int? FiscalYear,
    int? FiscalMonth,
    DateTime? DataFromDate,
    DateTime? DataToDate,
    decimal EstimatedHours,
    decimal? AgreedRate,
    string? RateType);

public record UpdateFreelanceTaskRequest(
    FreelanceTaskStatus? Status,
    DateTime? DueDate,
    string? ReviewNotes,
    decimal? ActualHours);

public record FreelanceTaskSummary(
    Guid Id,
    string Title,
    FreelanceTaskType TaskType,
    FreelanceTaskStatus Status,
    DateTime? DueDate,
    decimal EstimatedHours,
    decimal ActualHours);

public record FreelanceTaskResponse(
    Guid Id,
    Guid FreelanceAccessId,
    string Title,
    string? Description,
    FreelanceTaskType TaskType,
    FreelanceTaskStatus Status,
    DateTime? DueDate,
    DateTime? StartedAt,
    DateTime? SubmittedAt,
    DateTime? CompletedAt,
    int? FiscalYear,
    int? FiscalMonth,
    decimal EstimatedHours,
    decimal ActualHours,
    decimal? AgreedRate,
    string? RateType,
    decimal? TotalCost,
    bool IsPaid,
    string? ReviewNotes,
    List<TaskCommentResponse> Comments,
    List<TimeLogResponse> TimeLogs);

public record TaskCommentResponse(
    Guid Id,
    Guid UserId,
    string UserName,
    string Content,
    DateTime CreatedAt);

public record AddTaskCommentRequest(string Content);

public record TimeLogResponse(
    Guid Id,
    DateTime StartTime,
    DateTime? EndTime,
    decimal Hours,
    string? Description);

public record CreateTimeLogRequest(
    DateTime StartTime,
    DateTime? EndTime,
    decimal Hours,
    string? Description);

// ===== Activity Log =====
public record FreelanceActivityLogResponse(
    long Id,
    Guid UserId,
    string? UserEmail,
    DateTime Timestamp,
    string Action,
    string? EntityType,
    string? EntityId,
    string? Details,
    string? IpAddress);

// ===== Dashboard for Freelance =====
public record FreelanceDashboardResponse(
    List<FreelanceAccessResponse> ActiveCompanies,
    List<FreelanceTaskSummary> PendingTasks,
    List<FreelanceTaskSummary> OverdueTasks,
    decimal TotalHoursThisMonth,
    decimal TotalEarningsThisMonth);
