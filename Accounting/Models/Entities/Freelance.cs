using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// การจัดการ Freelance / นักบัญชีภายนอก
/// ระบบเชิญ Freelance เข้ามาทำงานปิดบัญชี โดยควบคุมสิทธิ์อย่างละเอียด
/// </summary>

/// <summary>
/// คำเชิญ Freelance / External Worker
/// </summary>
public class FreelanceInvitation : BaseEntity
{
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    // ผู้เชิญ
    public Guid InvitedByUserId { get; set; }
    public User InvitedByUser { get; set; } = null!;

    // ผู้ถูกเชิญ
    public string InviteeEmail { get; set; } = null!;
    public string InviteeName { get; set; } = null!;
    public string? InviteePhone { get; set; }

    // Status
    public FreelanceInvitationStatus Status { get; set; } = FreelanceInvitationStatus.Pending;
    public string InvitationToken { get; set; } = null!;  // Token สำหรับ accept
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }

    // Access Configuration
    public Guid? FreelanceAccessId { get; set; }
    public FreelanceAccess? FreelanceAccess { get; set; }
}

/// <summary>
/// การกำหนดสิทธิ์ Freelance ต่อบริษัท (ละเอียดมาก)
/// </summary>
public class FreelanceAccess : BaseEntity
{
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    // Access Period
    public DateTime AccessStartDate { get; set; }
    public DateTime AccessEndDate { get; set; }
    public bool IsActive { get; set; } = true;

    // Access Scope
    public AccessScope GeneralScope { get; set; } = AccessScope.ReadOnly;

    // Module-level permissions (granular)
    public bool CanViewChartOfAccounts { get; set; } = true;
    public bool CanEditChartOfAccounts { get; set; } = false;
    public bool CanViewJournalEntries { get; set; } = true;
    public bool CanCreateJournalEntries { get; set; } = false;
    public bool CanPostJournalEntries { get; set; } = false;
    public bool CanViewDocuments { get; set; } = true;
    public bool CanCreateDocuments { get; set; } = false;
    public bool CanApproveDocuments { get; set; } = false;
    public bool CanViewTaxReports { get; set; } = true;
    public bool CanCreateTaxReports { get; set; } = false;
    public bool CanFileTaxReports { get; set; } = false;
    public bool CanViewBankAccounts { get; set; } = true;
    public bool CanReconcile { get; set; } = false;
    public bool CanViewReports { get; set; } = true;
    public bool CanExportData { get; set; } = false;
    public bool CanCloseFiscalPeriod { get; set; } = false;
    public bool CanManageContacts { get; set; } = false;
    public bool CanViewPayments { get; set; } = true;
    public bool CanCreatePayments { get; set; } = false;
    public bool CanManageFixedAssets { get; set; } = false;

    // Fiscal Period Restrictions
    public int? AllowedFiscalYear { get; set; }      // จำกัดปีที่เข้าถึงได้
    public int? AllowedFiscalMonth { get; set; }      // จำกัดเดือนที่เข้าถึงได้

    // Data Visibility Restrictions
    public bool CanViewSensitiveData { get; set; } = false;  // เช่น เงินเดือน, ข้อมูลผู้ถือหุ้น
    public bool CanViewCostData { get; set; } = false;

    // IP Restriction
    public string? AllowedIpAddresses { get; set; }   // CSV of allowed IPs

    // Time Restriction
    public TimeOnly? AccessStartTime { get; set; }    // เวลาเริ่มเข้าได้
    public TimeOnly? AccessEndTime { get; set; }       // เวลาสิ้นสุด
    public string? AllowedDaysOfWeek { get; set; }    // e.g. "1,2,3,4,5" (Mon-Fri)

    // Rate & Usage Limits
    public int MaxActionsPerDay { get; set; } = 1000;
    public int TodayActionCount { get; set; }
    public DateTime? ActionCountResetDate { get; set; }

    // Navigation
    public ICollection<FreelanceTask> Tasks { get; set; } = new List<FreelanceTask>();
    public ICollection<FreelanceActivityLog> ActivityLogs { get; set; } = new List<FreelanceActivityLog>();
}

/// <summary>
/// งานที่มอบหมายให้ Freelance
/// </summary>
public class FreelanceTask : BaseEntity
{
    public Guid FreelanceAccessId { get; set; }
    public FreelanceAccess FreelanceAccess { get; set; } = null!;
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public FreelanceTaskType TaskType { get; set; }
    public FreelanceTaskStatus Status { get; set; } = FreelanceTaskStatus.Assigned;

    // Schedule
    public DateTime? DueDate { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Scope: ช่วงข้อมูลที่ task นี้ครอบคลุม
    public int? FiscalYear { get; set; }
    public int? FiscalMonth { get; set; }
    public DateTime? DataFromDate { get; set; }
    public DateTime? DataToDate { get; set; }

    // Review
    public Guid? ReviewedByUserId { get; set; }
    public User? ReviewedByUser { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNotes { get; set; }

    // Time Tracking
    public decimal EstimatedHours { get; set; }
    public decimal ActualHours { get; set; }

    // Pricing (ถ้ามีการคิดค่าใช้จ่าย)
    public decimal? AgreedRate { get; set; }
    public string? RateType { get; set; }  // "hourly", "fixed", "monthly"
    public decimal? TotalCost { get; set; }
    public bool IsPaid { get; set; }

    public ICollection<FreelanceTaskComment> Comments { get; set; } = new List<FreelanceTaskComment>();
    public ICollection<FreelanceTimeLog> TimeLogs { get; set; } = new List<FreelanceTimeLog>();
}

/// <summary>
/// ความคิดเห็นใน Task
/// </summary>
public class FreelanceTaskComment : BaseEntity
{
    public Guid FreelanceTaskId { get; set; }
    public FreelanceTask FreelanceTask { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Content { get; set; } = null!;
    public Guid? ParentCommentId { get; set; }
}

/// <summary>
/// บันทึกเวลาการทำงาน Freelance
/// </summary>
public class FreelanceTimeLog : BaseEntity
{
    public Guid FreelanceTaskId { get; set; }
    public FreelanceTask FreelanceTask { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public decimal Hours { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Activity Log สำหรับ Freelance (ติดตามทุกการกระทำ)
/// </summary>
public class FreelanceActivityLog
{
    public long Id { get; set; }
    public Guid FreelanceAccessId { get; set; }
    public FreelanceAccess FreelanceAccess { get; set; } = null!;
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = null!;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}
