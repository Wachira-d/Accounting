using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// Approval Workflow (ระบบอนุมัติหลายขั้นตอน)
/// เทียบเท่า PEAK: Approval flow
/// </summary>
public class ApprovalRule : TenantEntity
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    // Trigger: เมื่อไหร่ต้องผ่านการอนุมัติ
    public DocumentType? DocumentType { get; set; }      // null = ทุกประเภท
    public decimal? MinAmount { get; set; }               // จำนวนเงินขั้นต่ำที่ต้องขออนุมัติ
    public decimal? MaxAmount { get; set; }

    // Approvers: ลำดับผู้อนุมัติ
    public ICollection<ApprovalStep> Steps { get; set; } = new List<ApprovalStep>();
}

public class ApprovalStep : BaseEntity
{
    public Guid ApprovalRuleId { get; set; }
    public ApprovalRule ApprovalRule { get; set; } = null!;
    public int StepOrder { get; set; }
    public Guid ApproverUserId { get; set; }
    public User ApproverUser { get; set; } = null!;
    public bool IsRequired { get; set; } = true;
}

/// <summary>
/// Approval Request (คำขออนุมัติ)
/// </summary>
public class ApprovalRequest : TenantEntity
{
    public Guid ApprovalRuleId { get; set; }
    public ApprovalRule ApprovalRule { get; set; } = null!;

    // เอกสารที่ขออนุมัติ
    public string EntityType { get; set; } = null!;  // "Document", "JournalEntry", etc.
    public Guid EntityId { get; set; }

    public Guid RequestedByUserId { get; set; }
    public User RequestedByUser { get; set; } = null!;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    public ApprovalStatus OverallStatus { get; set; } = ApprovalStatus.Pending;
    public int CurrentStep { get; set; } = 1;

    public ICollection<ApprovalAction> Actions { get; set; } = new List<ApprovalAction>();
}

public class ApprovalAction : BaseEntity
{
    public Guid ApprovalRequestId { get; set; }
    public ApprovalRequest ApprovalRequest { get; set; } = null!;
    public int StepOrder { get; set; }
    public Guid ApproverUserId { get; set; }
    public User ApproverUser { get; set; } = null!;
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public DateTime? ActionAt { get; set; }
    public string? Comments { get; set; }
}
