using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// รายการที่เกิดซ้ำ (Recurring Invoice, Expense, Journal)
/// เทียบเท่า FlowAccount: Recurring invoices / PEAK: Recurring
/// </summary>
public class RecurringTransaction : TenantEntity
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public RecurringFrequency Frequency { get; set; }
    public RecurringStatus Status { get; set; } = RecurringStatus.Active;

    // Schedule
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime NextRunDate { get; set; }
    public DateTime? LastRunDate { get; set; }
    public int TotalRuns { get; set; }
    public int? MaxRuns { get; set; }

    // Template: which document/journal to create
    public string TemplateType { get; set; } = null!; // "Document" or "Journal"
    public DocumentType? DocumentType { get; set; }
    public Guid? ContactId { get; set; }

    // Template data (JSON)
    public string TemplateData { get; set; } = null!; // JSON of CreateDocumentRequest or CreateJournalEntryRequest

    // Notification
    public bool NotifyBeforeRun { get; set; } = true;
    public int NotifyDaysBefore { get; set; } = 1;
    public bool AutoApprove { get; set; } = false;
}
