using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// เอกสารทางธุรกิจ (ใบเสนอราคา, ใบแจ้งหนี้, ใบเสร็จ, ใบกำกับภาษี)
/// </summary>
public class Document : TenantEntity
{
    public string DocumentNumber { get; set; } = null!;    // running number
    public DocumentType DocumentType { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;
    public DateTime DocumentDate { get; set; }
    public DateTime? DueDate { get; set; }

    // Contact (Customer/Supplier)
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = null!;

    // Reference
    public string? Reference { get; set; }
    public Guid? RelatedDocumentId { get; set; }  // e.g. Quotation → Invoice

    // Amounts
    public string Currency { get; set; } = "THB";
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal WithholdingTaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal BalanceDue { get; set; }

    // Notes
    public string? Notes { get; set; }
    public string? InternalNotes { get; set; }

    // Navigation
    public ICollection<DocumentLine> Lines { get; set; } = new List<DocumentLine>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}

/// <summary>
/// รายการย่อยในเอกสาร
/// </summary>
public class DocumentLine : BaseEntity
{
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;

    public int LineOrder { get; set; }
    public string? ProductCode { get; set; }
    public string Description { get; set; } = null!;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "ชิ้น";
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal Amount { get; set; }
    public decimal VatRate { get; set; } = 7;
    public decimal VatAmount { get; set; }
    public decimal WithholdingTaxRate { get; set; }
    public decimal WithholdingTaxAmount { get; set; }
    public string? IncomeTypeCode { get; set; }  // รหัสประเภทเงินได้ สำหรับภาษีหัก ณ ที่จ่าย

    // Account mapping for auto-posting
    public Guid? AccountId { get; set; }
    public ChartOfAccount? Account { get; set; }
}

/// <summary>
/// Contact (ลูกค้า / ผู้ขาย)
/// </summary>
public class Contact : TenantEntity
{
    public string Name { get; set; } = null!;
    public string? TaxId { get; set; }
    public string? BranchCode { get; set; }
    public ContactType ContactType { get; set; } = ContactType.Individual;
    public bool IsCustomer { get; set; }
    public bool IsSupplier { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? ContactPerson { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<Document> Documents { get; set; } = new List<Document>();
}
