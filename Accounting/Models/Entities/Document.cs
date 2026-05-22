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

    // Project tagging — header default; lines can override per-line.
    // Used to attribute revenue/cost on auto-posted journal entries to a Project,
    // enabling per-project P&L (see ProjectAccountingService.GetGlSummaryAsync).
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    // Bank account link — which bank account money flows in/out of.
    // Used for reconciliation and auto-posting to correct GL bank account.
    public Guid? BankAccountId { get; set; }
    public BankAccount? BankAccount { get; set; }

    // Payment account — direct GL account for non-bank money flow
    // (e.g. เงินสด 111, เงินทดรองกรรมการ 115/219, e-Wallet 11190)
    // Takes precedence over BankAccountId when set.
    public Guid? PaymentAccountId { get; set; }
    public ChartOfAccount? PaymentAccount { get; set; }

    // Expense category (header-level default when all lines share the same category)
    public Guid? ExpenseCategoryId { get; set; }
    public ChartOfAccount? ExpenseCategory { get; set; }

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

    // Per-document overrides for the company's global appendix/footer templates.
    // When null, falls back to CompanySettings.{Type}Notes / {Type}Footer.
    public string? CustomAppendix { get; set; }
    public string? CustomFooterNotes { get; set; }
    public string? CustomTermsAndConditions { get; set; }

    // Optional link to a Revenue Contract — set when this document is invoicing
    // against a recognized contract milestone. Used for ASC 606 / TFRS 15 tracking.
    public Guid? RevenueContractId { get; set; }
    public Guid? PerformanceObligationId { get; set; }

    // ===== ใบรับรองแทนใบเสร็จ (CertificateInLieu) =====
    public string? CertificateReason { get; set; }       // เหตุผลที่ไม่ได้รับใบเสร็จ
    public string? CertifierName { get; set; }            // ชื่อผู้รับรอง
    public string? CertifierPosition { get; set; }        // ตำแหน่งผู้รับรอง
    public string? WitnessName { get; set; }              // ชื่อพยาน
    public string? WitnessPosition { get; set; }          // ตำแหน่งพยาน
    public DateTime? PaymentDate { get; set; }            // วันที่จ่ายเงินจริง

    // ===== OCR Self-Learning =====
    // Watermark set by VendorIntelligenceService.TrainFromDocumentAsync after this
    // document's data has been counted into the per-vendor intelligence cache.
    // Prevents double-counting on re-approval (Draft → Approved → Rejected → Draft → Approved).
    public DateTime? OcrIntelTrainedAt { get; set; }

    // ===== OCR RD Compliance (Task 5 ERP Upgrade) =====

    /// <summary>Average confidence (0..1) across all fields extracted by
    /// Azure Document Intelligence on first OCR pass. Below 0.5 should
    /// surface a "manual review" badge to the operator.</summary>
    public decimal? OcrConfidenceScore { get; set; }

    /// <summary>Result of the post-OCR RD compliance check
    /// (Tax Invoice keyword present, Buyer/Seller Tax IDs valid, branch
    /// code populated, VAT breakdown balances). Drives the UI badge.</summary>
    public Enums.RdComplianceStatus RdComplianceStatus { get; set; } = Enums.RdComplianceStatus.Pending;

    /// <summary>JSON-serialised list of individual issues so the badge can
    /// expand to "3 problems: missing branch code, ..." without a join.</summary>
    public string? RdComplianceIssuesJson { get; set; }

    /// <summary>True when OCR pulled a Buyer Tax ID that doesn't match this
    /// tenant's CompanyTaxId — usually means an invoice for a different
    /// legal entity was uploaded into the wrong company. Hard warning.</summary>
    public bool OcrTenantMismatchFlag { get; set; }

    /// <summary>Aging engine watermark — calendar days since the document
    /// became Pending (Approved-but-unpaid). Refreshed by AgingBackgroundService;
    /// cached here so list views don't recompute on every fetch.</summary>
    public int? AgingDays { get; set; }
    public DateTime? AgingLastEvaluatedAt { get; set; }

    /// <summary>True = an opening-balance subledger document imported during
    /// migration (open AR/AP carried over from a previous system). It is
    /// created already-Approved and is deliberately NEVER auto-posted to the
    /// GL — the control-account total is carried by the GL opening balance
    /// (TrialBalance migration), so posting it would double-count.</summary>
    public bool IsOpeningBalance { get; set; } = false;

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

    // Per-line project override — falls back to Document.ProjectId if null.
    // Allows splitting a single document across multiple projects (e.g. one
    // mixed invoice billing two projects).
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>
    /// When this line was created by converting another document, points at
    /// the source <see cref="DocumentLine"/> it was derived from. Null on
    /// lines entered directly by the user.
    ///
    /// This is the backbone of FLEXIBLE / PARTIAL document composition: one
    /// source line (e.g. a PO line for 100 units) may be carried forward
    /// into several child lines across several documents (DeliveryNote ×2,
    /// Invoice ×3...). The quantity still available to convert is
    ///   <c>source.Quantity − Σ(child line Quantity)</c>
    /// computed per fulfilment axis (delivery vs. billing) — see
    /// DocumentService.ComputeConsumptionAsync.
    /// </summary>
    public Guid? SourceLineId { get; set; }
}

/// <summary>
/// Contact (ลูกค้า / ผู้ขาย) — ที่อยู่เก็บแบบ structured ตามมาตรฐาน ETDA Schematron
/// (ต้องมี BuildingNumber + ตำบล/อำเภอ/จังหวัด/รหัสไปรษณีย์ สำหรับ e-Tax XML).
/// คงฟิลด์ Address ไว้เพื่อ backward compat — ใหม่ใช้ structured fields เป็นหลัก.
/// </summary>
public class Contact : TenantEntity
{
    public string Name { get; set; } = null!;
    public string? TaxId { get; set; }
    public string? BranchCode { get; set; }
    public string? BranchName { get; set; }
    public ContactType ContactType { get; set; } = ContactType.Individual;
    public bool IsCustomer { get; set; }
    public bool IsSupplier { get; set; }

    // === Address fields (structured per ETDA TradePartyType) ===
    /// <summary>Free-text address — kept for backward compat + display.
    /// New code should prefer the structured fields below.</summary>
    public string? Address { get; set; }
    /// <summary>บ้านเลขที่ — required by ETDA Schematron for CountryID=TH</summary>
    public string? BuildingNumber { get; set; }
    /// <summary>ชื่ออาคาร (optional)</summary>
    public string? BuildingName { get; set; }
    /// <summary>ถนน/ซอย</summary>
    public string? StreetName { get; set; }
    public string? SubDistrict { get; set; }   // ตำบล/แขวง
    public string? District { get; set; }      // อำเภอ/เขต
    public string? Province { get; set; }      // จังหวัด
    public string? PostalCode { get; set; }    // 5 digits
    public string CountryCode { get; set; } = "TH";

    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? ContactPerson { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<Document> Documents { get; set; } = new List<Document>();
}
