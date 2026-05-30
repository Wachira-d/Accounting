using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

public class Payment : TenantEntity
{
    public string PaymentNumber { get; set; } = null!;
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public DateTime PaymentDate { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public string? Reference { get; set; }
    public string? BankAccount { get; set; }
    public string? Notes { get; set; }

    // Proper FK to bank account (replaces text BankAccount field).
    // When OverrideBankAccountId is set, the GL hit + bank-balance update
    // follow this value; BankAccountId mirrors it for convenience. When
    // OverrideBankAccountId is null, BankAccountId inherits from doc.
    public Guid? BankAccountId { get; set; }
    public BankAccount? BankAccountEntity { get; set; }

    /// <summary>Per-payment override of the source document's BankAccountId.
    /// Lets operators record "the cheque actually cleared at Kasikorn even
    /// though the invoice was targeted at Bangkok Bank". Null = use doc's
    /// channel as-is. Audit-visible so we can trace overrides explicitly.</summary>
    public Guid? OverrideBankAccountId { get; set; }

    /// <summary>WHT withheld by the customer (revenue side) / by us (purchase
    /// side) on THIS installment. Per Thai practice + ประมวลรัษฎากร §50, when
    /// the source invoice is paid in installments the customer withholds
    /// proportionally per installment. Default at CreatePaymentAsync time is
    /// proportional (Amount / Document.TotalAmount × Document.Withholding
    /// TaxAmount); operator can override on the modal. Sum of WHT across
    /// all payments of a document must not exceed Document.WithholdingTax
    /// Amount — enforced inline. Drives the per-receipt Dr WHT-Asset / Cr
    /// WHT-Payable line on the cash-basis GL.</summary>
    public decimal WithholdingTaxAmount { get; set; }

    /// <summary>Project allocation — when set, overrides the parent
    /// document's project for THIS payment. Useful when one document
    /// is split across multiple project payments (advance on Project A
    /// followed by final on Project B). Null = inherit from
    /// Document.ProjectId.</summary>
    public Guid? ProjectId { get; set; }
}
