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

    // ===== e-Filing ACK lifecycle =====
    // After admin downloads the e-Filing text file and submits it to
    // the RD portal, the RD returns an acknowledgement (ACK number +
    // accepted-at timestamp) or a rejection. Tracking these explicitly
    // lets the admin dashboard show "Filed but ACK pending" vs
    // "Filed + accepted" — important for the few-day window where the
    // RD validates the submission server-side.

    /// <summary>RD acknowledgement number returned after a successful
    /// e-Filing submission (typically 14-digit alphanumeric).</summary>
    public string? RdAckNumber { get; set; }

    /// <summary>Timestamp the RD's portal stamps on the ACK page —
    /// this is the legal "received by RD" moment for deadline
    /// compliance.</summary>
    public DateTime? RdAcknowledgedAt { get; set; }

    /// <summary>"Pending" (uploaded, awaiting validation) |
    /// "Accepted" | "Rejected" | "Resubmit-Required".</summary>
    public string? RdSubmissionStatus { get; set; }

    /// <summary>RD's stated reason for rejection — populated when
    /// RdSubmissionStatus = "Rejected" so admin sees what to fix.</summary>
    public string? RdRejectionReason { get; set; }

    /// <summary>Optional URL or file path to the RD-issued PDF
    /// acknowledgement so admin can re-download for audit.</summary>
    public string? RdAcknowledgementDocumentUrl { get; set; }

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

    /// <summary>True = the accountant chose NOT to include this line in the
    /// filed return (e.g. prohibited input VAT, an over-age invoice). The
    /// line is kept for audit but excluded from OutputVat/InputVat/NetVat.</summary>
    public bool IsExcluded { get; set; } = false;
}
