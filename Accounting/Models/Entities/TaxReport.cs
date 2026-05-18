using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// รายงานภาษี (ภพ.30, ภงด.3, ภงด.53)
/// </summary>
public class TaxReport : TenantEntity
{
    public TaxType TaxType { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public TaxReportStatus Status { get; set; } = TaxReportStatus.Draft;
    public DateTime? FiledDate { get; set; }

    // VAT (ภพ.30)
    public decimal OutputVat { get; set; }       // ภาษีขาย
    public decimal InputVat { get; set; }        // ภาษีซื้อ
    public decimal NetVat { get; set; }          // ภาษีที่ต้องชำระ/ขอคืน

    // WHT (ภงด.3/53)
    public decimal TotalIncome { get; set; }
    public decimal TotalTaxWithheld { get; set; }

    public string? Notes { get; set; }

    // ===== Filing lock + audit (Task 4 RD compliance) =====

    /// <summary>Once set, the report and every linked document/JE is locked
    /// for edits — protects the integrity of what was actually submitted
    /// to the RD. Cleared by an explicit unlock flow only.</summary>
    public DateTime? FilingLockedAt { get; set; }
    public string? FilingLockedBy { get; set; }

    /// <summary>Timestamp of last e-Filing text export (pipe-delimited).</summary>
    public DateTime? EFilingExportedAt { get; set; }
    public string? EFilingReferenceNumber { get; set; }

    /// <summary>Set when RD rejects the filing — captured here so
    /// downstream "Cancel &amp; Reverse" workflow can record the reason
    /// in the auto-generated reversal JE description.</summary>
    public string? RejectionReason { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectedBy { get; set; }
    public Guid? ReversalJournalEntryId { get; set; }

    public ICollection<TaxReportLine> Lines { get; set; } = new List<TaxReportLine>();
}

public class TaxReportLine : BaseEntity
{
    public Guid TaxReportId { get; set; }
    public TaxReport TaxReport { get; set; } = null!;

    public int LineOrder { get; set; }
    public string? TaxPayerId { get; set; }      // เลขผู้เสียภาษี
    public string? TaxPayerName { get; set; }
    public DateTime TransactionDate { get; set; }
    public string? Description { get; set; }
    public decimal IncomeAmount { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public string? IncomeTypeCode { get; set; }  // รหัสประเภทเงินได้
    public Guid? DocumentId { get; set; }
}
