using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

public class ExpenseClaim : TenantEntity
{
    public string ClaimNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DateTime ExpenseDate { get; set; }
    public ExpenseClaimStatus Status { get; set; } = ExpenseClaimStatus.Draft;
    public decimal SubTotal { get; set; }
    public decimal VatAmount { get; set; }
    public decimal WithholdingTaxAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public Guid SubmittedByUserId { get; set; }
    public User SubmittedByUser { get; set; } = null!;

    public Guid? ApprovedByUserId { get; set; }
    public User? ApprovedByUser { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovalNotes { get; set; }
    public string? RejectionReason { get; set; }

    public DateTime? PaidAt { get; set; }
    public PaymentMethod? PaidMethod { get; set; }
    public string? PaidReference { get; set; }

    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    // Auto-generated PaymentVoucher Document — created when MarkAsPaidAsync
    // runs. The Document owns the GL posting via the central DocumentService,
    // so claims show up as standard PVs (with the same PDF and UI) instead
    // of a raw journal entry.
    public Guid? PaymentVoucherDocumentId { get; set; }
    public Document? PaymentVoucherDocument { get; set; }

    // ───── No-receipt claim (§65 ทวิ) ─────
    // When true, the claim is for an expense where the vendor could not
    // issue a receipt (purchased at ตลาดสด, taxi, paid a person who is
    // not VAT-registered, original receipt lost, etc.). On Approve, the
    // service auto-generates a Document(CertificateInLieu) carrying the
    // §65 ทวิ legal fields (CertificateReason + CertifierName +
    // optional Witness) so the expense can still be claimed as a
    // company tax-deductible expense.
    //
    // The Pay step still creates a PaymentVoucher just like a receipted
    // claim — the certificate proves the expense; the PV settles cash.
    public bool NoReceipt { get; set; } = false;

    /// <summary>เหตุผลที่ไม่มีใบเสร็จ — required when NoReceipt = true.
    /// e.g. "ผู้ขายไม่ออกใบเสร็จ (ตลาดสด)", "ใบเสร็จสูญหาย", "ค่าโดยสาร taxi"
    /// Stored verbatim onto the CertificateInLieu's CertificateReason field.</summary>
    public string? NoReceiptReason { get; set; }

    /// <summary>ชื่อพยาน (optional) — บางบริษัทเข้มกว่ามาตรา §65 ทวิ
    /// ขอให้มีพยาน 2 คนเพื่อ audit. Copied to the CertificateInLieu's
    /// WitnessName field when set.</summary>
    public string? WitnessName { get; set; }
    public string? WitnessPosition { get; set; }

    /// <summary>FK to the auto-generated Document(CertificateInLieu) that
    /// was created when this claim was approved. Null when the claim
    /// was not a no-receipt claim, or when approval ran before the
    /// auto-gen feature shipped (grandfathered rows). UI links to this
    /// document from the claim detail view so the bookkeeper can pull
    /// up the PDF for an audit.</summary>
    public Guid? CertificateInLieuDocumentId { get; set; }
    public Document? CertificateInLieuDocument { get; set; }

    public ICollection<ExpenseClaimLine> Lines { get; set; } = new List<ExpenseClaimLine>();
}

public class ExpenseClaimLine : BaseEntity
{
    public Guid ExpenseClaimId { get; set; }
    public ExpenseClaim ExpenseClaim { get; set; } = null!;

    public int LineOrder { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal VatRate { get; set; }
    public decimal VatAmount { get; set; }
    public decimal WithholdingTaxRate { get; set; }
    public decimal WithholdingTaxAmount { get; set; }
    public decimal NetAmount { get; set; }

    public Guid? AccountId { get; set; }
    public ChartOfAccount? Account { get; set; }
    public string? Category { get; set; }
    public string? Reference { get; set; }
}
