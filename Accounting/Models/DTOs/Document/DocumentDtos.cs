using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Document;

public record CreateDocumentRequest(
    DocumentType DocumentType,
    DateTime DocumentDate,
    DateTime? DueDate,
    Guid ContactId,
    string? Reference,
    string? Notes,
    List<DocumentLineRequest> Lines,
    Guid? ProjectId = null,
    Guid? BankAccountId = null,
    Guid? PaymentAccountId = null,
    Guid? ExpenseCategoryId = null,
    string? CustomAppendix = null,
    string? CustomFooterNotes = null,
    string? CustomTermsAndConditions = null,
    Guid? RevenueContractId = null,
    Guid? PerformanceObligationId = null,
    // ===== ใบรับรองแทนใบเสร็จ =====
    string? CertificateReason = null,
    string? CertifierName = null,
    string? CertifierPosition = null,
    string? WitnessName = null,
    string? WitnessPosition = null,
    DateTime? PaymentDate = null,
    // ===== สกุลเงิน + อัตราแลกเปลี่ยน =====
    // Currency defaults to THB; ExchangeRate to 1. For non-THB docs the
    // service auto-fetches the BoT mid-rate at DocumentDate if ExchangeRate
    // is omitted; callers can override with a contracted rate.
    string Currency = "THB",
    decimal? ExchangeRate = null,
    // ===== Sensitivity classification (optional) =====
    // Internal callers (e.g. PayrollService) pass Sensitivity to gate the
    // resulting document behind the matching role. External clients leave it
    // None (the default) and the document is publicly visible within the company.
    SensitivityKind Sensitivity = SensitivityKind.None,
    // CreditNote reason — required when DocumentType=CreditNote. Determines
    // whether stock restocks (Return only) vs pure financial adjustment.
    CreditNoteReason? CreditNoteReason = null,
    // ===== Supplier-side tax invoice metadata (PurchaseInvoice / supplier-issued docs) =====
    // SupplierInvoiceNumber = partner's own running number (distinct from
    // our DocumentNumber) — needed for VAT-audit reconciliation against
    // the supplier statement. SupplierTaxInvoiceDate = the date on the
    // partner's tax invoice; controls the VAT claim period (Revenue
    // Code §82/4) when we book a bill late. CreditDays / PaymentTerms
    // capture the agreed payment window for DSO/DPO + DueDate auto-fill.
    string? SupplierInvoiceNumber = null,
    DateTime? SupplierTaxInvoiceDate = null,
    // ใบสำคัญจ่าย ติ๊ก "ใช้งานใบกำกับภาษี" → flag + snapshot สาขาผู้ขาย
    // (อ้างอิงใบกำกับซื้อ ขอเครดิตภาษีซื้อ — RD §86/4 + §86/14).
    bool HasTaxInvoiceReference = false,
    string? SupplierBranchCode = null,
    int? CreditDays = null,
    string? PaymentTerms = null,
    // Settlement basis (Payment Voucher: เครดิต vs จ่ายทันที). Cash → straight
    // to Cash/Bank, no payable/due/aging. Credit → AP + due date + aging.
    // Null → service infers per type (standalone PV defaults to Cash).
    PaymentType? PaymentType = null,
    // Unit prices entered VAT-inclusive (ราคารวมภาษี). True → back 7% VAT out.
    bool PricesIncludeVat = false,
    // ภ.พ.36 / ภ.ง.ด.54 — flag เมื่อซื้อบริการจากต่างประเทศ (Google Ads /
    // AWS / Facebook ฯลฯ). ผู้รับบริการในไทยต้อง self-assess VAT 7% และ
    // หัก WHT ตาม DTA. Default false. Apply เฉพาะ PI/Expense/PV.
    bool IsForeignService = false,
    // เงินมัดจำ/รับล่วงหน้า — Receipt/ReceiptVoucher ที่รับเงินก่อนส่งมอบ.
    // True → Cr "ขายรอรับรู้" (217xx) แทนรายได้.
    // DepositDeferredAccountCode = ผังพักรายได้ (null → 21712).
    // DepositOutputVatDeferred: false = tax point เกิดแล้ว → Cr ภาษีขาย 21911
    //   เข้า ภ.พ.30 ทันที (§78 รับชำระราคา); true = ยังไม่เกิด tax point
    //   (เงินประกัน/ยังไม่ให้บริการ) → Cr ภาษีขายรอเรียกเก็บ 21913 ยังไม่เข้า
    //   ภ.พ.30 จนกว่าจะรับรู้ (RealizeDeposit).
    bool IsDeposit = false,
    string? DepositDeferredAccountCode = null,
    bool DepositOutputVatDeferred = false);

public record DocumentLineRequest(
    string Description,
    decimal Quantity,
    string? Unit,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal VatRate,
    decimal WithholdingTaxRate,
    Guid? AccountId,
    // Optional per-line project override (null → inherits Document.ProjectId)
    Guid? ProjectId = null,
    // Optional product linkage — set when the user picked a product via
    // the line-item typeahead. Stored on DocumentLine.ProductCode so reports
    // can group revenue/cost by product without re-parsing descriptions.
    string? ProductCode = null,
    // Traceability link for flexible/partial conversion — set by the
    // conversion engine, and round-tripped by the edit form so editing a
    // converted document never loses its link to the source line.
    Guid? SourceLineId = null,
    // ภาษีซื้อต้องห้าม (Non-claimable Input VAT) per ประมวลรัษฎากร §82/5.
    // Default true (เคลมได้). UI ติ๊กออก / AI suggest false สำหรับค่ารับรอง
    // / น้ำมันรถยนต์นั่ง / ใบกำกับฯ ไม่สมบูรณ์.
    bool IsVatClaimable = true,
    string? VatNonClaimableReason = null);

public record UpdateDocumentRequest(
    DateTime? DocumentDate,
    DateTime? DueDate,
    Guid? ContactId,
    string? Reference,
    string? Notes,
    List<DocumentLineRequest>? Lines,
    Guid? ProjectId = null,
    Guid? BankAccountId = null,
    Guid? PaymentAccountId = null,
    Guid? ExpenseCategoryId = null,
    string? CustomAppendix = null,
    string? CustomFooterNotes = null,
    string? CustomTermsAndConditions = null,
    Guid? RevenueContractId = null,
    Guid? PerformanceObligationId = null,
    string? CertificateReason = null,
    string? CertifierName = null,
    string? CertifierPosition = null,
    string? WitnessName = null,
    string? WitnessPosition = null,
    DateTime? PaymentDate = null,
    string? SupplierInvoiceNumber = null,
    DateTime? SupplierTaxInvoiceDate = null,
    // PV: ใช้งานใบกำกับภาษี (nullable → omit ไม่แตะค่าเดิม).
    bool? HasTaxInvoiceReference = null,
    string? SupplierBranchCode = null,
    int? CreditDays = null,
    string? PaymentTerms = null,
    PaymentType? PaymentType = null,
    // Nullable on update so omitting it preserves the stored value.
    bool? PricesIncludeVat = null);

/// <summary>เติม/แก้ใบกำกับภาษีซื้อหลังอนุมัติ — trigger reclassify 11640→11610
/// เมื่อข้อมูลครบ §86/4. ทุก field nullable: omit = คงค่าเดิม. ส่งเฉพาะที่แก้.
/// InputVatAccountCodeOverride: ตั้ง "" (empty) เพื่อล้าง override กลับ default;
/// null = ไม่แตะ; ค่าอื่น = pin ผัง VAT ปลายทางใหม่.</summary>
public record CompleteSupplierTaxInvoiceRequest(
    string? SupplierInvoiceNumber = null,
    DateTime? SupplierTaxInvoiceDate = null,
    string? SupplierBranchCode = null,
    string? InputVatAccountCodeOverride = null);

/// <summary>รับรู้รายได้จากเงินมัดจำ (ตัด "ขายรอรับรู้" 217xx → รายได้) เมื่อ
/// ส่งมอบสินค้า/บริการจริง. Amount = ฐานไม่รวม VAT ที่จะรับรู้ (รองรับบางส่วน);
/// RevenueAccountCode = ผังรายได้ปลายทาง (null → default 41000/42000);
/// FinalInvoiceId = ใบแจ้งหนี้/ใบกำกับสุดท้ายที่หักมัดจำนี้ (optional ใช้ link).</summary>
public record RealizeDepositRequest(
    decimal Amount,
    DateTime? RealizeDate = null,
    string? RevenueAccountCode = null,
    Guid? FinalInvoiceId = null);

/// <summary>สรุปเงินมัดจำคงค้างสำหรับหน้าจัดการมัดจำ (ขึ้นงบดุลเป็นหนี้สิน
/// ไม่ใช่เจ้าหนี้การค้า). OutstandingAmount = BaseAmount − RealizedAmount.</summary>
public record DepositSummary(
    Guid Id,
    string DocumentNumber,
    DateTime DocumentDate,
    string ContactName,
    string? ContactTaxId,
    decimal BaseAmount,
    decimal VatAmount,
    decimal TotalAmount,
    decimal RealizedAmount,
    decimal OutstandingAmount,
    DateTime? RealizedAt,
    int AgeDays,
    string Status,
    string? DeferredAccountCode,
    // หมายเลขอ้างอิง (เลขจอง/booking) — ใช้กลับรายการ/กระทบยอด
    string? Reference,
    // ภาษีขาย: false = ถึงกำหนดแล้ว (21911/ภ.พ.30); true = รอเรียกเก็บ (21913)
    bool OutputVatDeferred,
    // วันที่ภาษีขาย deferred ถูกรับรู้เข้า ภ.พ.30 (null = ยังไม่รับรู้)
    DateTime? OutputVatRecognizedAt);

/// <summary>สรุปเอกสารที่ภาษีซื้อค้างอยู่ที่ 11640 "ยังไม่ถึงกำหนด" รอใบกำกับ
/// ครบ §86/4. MonthsLeft = เดือนเหลือก่อนหมดสิทธิเคลม (§82/3 6 เดือนนับจาก
/// เดือนใบกำกับ); IsExpired = เกิน 6 เดือนแล้ว (เคลมไม่ได้ ต้องลงเป็นต้นทุน).</summary>
public record UndueInputVatSummary(
    Guid Id,
    string DocumentNumber,
    DateTime DocumentDate,
    string SupplierName,
    string? SupplierTaxId,
    decimal VatAmount,
    int AgeDays,
    int MonthsLeft,
    bool IsExpired,
    IReadOnlyList<string> MissingFields);

public record DocumentResponse(
    Guid Id,
    string DocumentNumber,
    DocumentType DocumentType,
    DocumentStatus Status,
    DateTime DocumentDate,
    DateTime? DueDate,
    ContactBrief Contact,
    decimal SubTotal,
    decimal DiscountAmount,
    decimal VatAmount,
    decimal WithholdingTaxAmount,
    decimal TotalAmount,
    decimal PaidAmount,
    decimal BalanceDue,
    string? Reference,
    string? Notes,
    List<DocumentLineResponse> Lines,
    DateTime CreatedAt,
    // e-Tax info — set when this document has been processed via the e-Tax pipeline.
    // The UI uses these to surface the "Download PDF/A-3 (with embedded XML)" action,
    // which is required for e-Tax by Email compliance — printing strips the XML payload.
    Guid? EtaxInvoiceId = null,
    EtaxStatus? EtaxStatus = null,
    Guid? ProjectId = null,
    string? ProjectCode = null,
    string? ProjectName = null,
    Guid? BankAccountId = null,
    string? BankAccountName = null,
    Guid? PaymentAccountId = null,
    string? PaymentAccountName = null,
    Guid? ExpenseCategoryId = null,
    string? ExpenseCategoryName = null,
    string? CustomAppendix = null,
    string? CustomFooterNotes = null,
    string? CustomTermsAndConditions = null,
    Guid? RevenueContractId = null,
    Guid? PerformanceObligationId = null,
    Guid? RelatedDocumentId = null,
    string? CertificateReason = null,
    string? CertifierName = null,
    string? CertifierPosition = null,
    string? WitnessName = null,
    string? WitnessPosition = null,
    DateTime? PaymentDate = null,
    // ERP upgrade — OCR RD compliance + aging cache
    decimal? OcrConfidenceScore = null,
    RdComplianceStatus RdComplianceStatus = RdComplianceStatus.Pending,
    string? RdComplianceIssuesJson = null,
    bool OcrTenantMismatchFlag = false,
    int? AgingDays = null,
    // Days a document has sat in a non-terminal status (Draft/WaitingApproval/
    // Approved/Sent/Partially-paid) beyond the stale threshold — null when not
    // stale. Surfaces "forgotten" documents (e.g. a PO left 3 months).
    int? StaleDays = null,
    // Multi-currency — Currency is doc's denomination; ExchangeRate is THB per
    // 1 unit of Currency captured at Create. Both default to ("THB", 1).
    string Currency = "THB",
    decimal ExchangeRate = 1m,
    // Sensitivity — None for the regular sales/purchase stream. Payroll vouchers
    // and other restricted records stamp this. When the requesting user lacks
    // the matching permission the API returns a stub with IsRedacted=true and
    // amounts/contact/notes blanked out so integration targets know the record
    // exists but is hidden — they should not 404 or pretend it isn't there.
    SensitivityKind Sensitivity = SensitivityKind.None,
    bool IsRedacted = false,
    string? RedactedReason = null,
    // CreditNote reason — set when DocumentType=CreditNote so the UI can
    // display "ลดราคา" / "คืนสินค้า" etc. Drives whether ApplyStockMovements
    // restocks on approval (only Return does).
    CreditNoteReason? CreditNoteReason = null,
    // Supplier-side tax invoice metadata for PurchaseInvoice rows.
    string? SupplierInvoiceNumber = null,
    DateTime? SupplierTaxInvoiceDate = null,
    // PV flag + supplier branch snapshot (RD §86/4 + §86/14).
    bool HasTaxInvoiceReference = false,
    string? SupplierBranchCode = null,
    int? CreditDays = null,
    string? PaymentTerms = null,
    // Settlement basis — Cash (จ่ายทันที) vs Credit (เครดิต). Drives whether
    // the UI shows a due date / outstanding balance for a Payment Voucher.
    PaymentType? PaymentType = null,
    bool PricesIncludeVat = false,
    // ===== Conversion lineage =====
    // Source-side view (this doc was converted from another): RelatedDocumentId
    // already carries the upstream id; the populated brief lets the UI render
    // "แปลงมาจาก QT-0042" without a second round-trip.
    DocumentBrief? RelatedDocument = null,
    // Target-side view (other docs created from this one): list of children
    // spawned via ConvertCoreAsync — populated server-side from the
    // (CompanyId, RelatedDocumentId) index so the source doc can render
    // "ใบที่ออกต่อจากเอกสารนี้" without N+1.
    List<DocumentBrief>? ConvertedToDocuments = null,
    // 0..100 — share of source quantity consumed by child docs across
    // all axes (Delivery + Billing). Null when this doc has no source
    // lines. Used to badge "✓ Fully converted" / "◐ 60% converted".
    decimal? ConversionCompletionPercent = null,
    // Convenience aggregate of the above — "None" / "Partial" / "Full" — so
    // the UI can pick a badge color without computing thresholds itself.
    string? ConversionStatus = null,
    // ===== Project-cost booking summary =====
    // Populated by GetDocumentAsync from the (DocumentId, DocumentLineId)
    // links on ProjectCostEntry — lets the UI show "🏗️ ลงโครงการแล้ว
    // 3 รายการ / ฿15,400" and the per-project breakdown without an extra
    // round-trip.
    bool HasProjectCostEntries = false,
    int ProjectCostEntryCount = 0,
    decimal ProjectCostBookedAmount = 0,
    List<ProjectCostBrief>? BookedProjects = null,
    // ===== Lifecycle =====
    // Unified "what's the state of this doc's purpose?" view, derived
    // from Status + ConversionStatus + BalanceDue. Lets the UI show a
    // single clear badge per doc instead of asking the user to mentally
    // combine 3 signals. Values:
    //   • "Open"            — still has work to do
    //   • "PartiallyDone"   — converted or settled in part
    //   • "Done"            — purpose fulfilled (paid / fully converted / approved one-shot)
    //   • "Cancelled"       — voided / rejected
    string? LifecycleStatus = null,
    // Short Thai phrase explaining the lifecycle state in context, e.g.
    // "✓ จ่ายแล้ว", "✓ แปลงเป็น PI-001", "◐ แปลงไป 60%", "× ยกเลิก".
    // Picked up directly by the badge tooltip + list column.
    string? LifecycleReason = null,
    // ===== Undue Input VAT (§82/3) =====
    // True เมื่อตอน approve ใบกำกับยังไม่ครบ §86/4 → VAT ลง 11640 "ภาษีซื้อ
    // ยังไม่ถึงกำหนด" แทน 11610. UI โชว์ป้าย "⏳ ภาษีซื้อรอใบกำกับครบ" + ปุ่ม
    // "เติมข้อมูลใบกำกับ" (เรียก CompleteSupplierTaxInvoiceAsync).
    bool InputVatPostedAsUndue = false,
    // เมื่อ != null = ระบบ reclassify 11640→11610 แล้ว (ใบกำกับครบ) ณ วันนี้ —
    // ภ.พ.30 ใช้เดือนนี้เป็น tax point. null + InputVatPostedAsUndue=true =
    // ยังค้าง 11640 รอเติมข้อมูล.
    DateTime? InputVatBecameClaimableAt = null,
    string? InputVatAccountCodeOverride = null,
    // ===== เงินมัดจำ/รับล่วงหน้า =====
    bool IsDeposit = false,
    decimal DepositRealizedAmount = 0m,
    DateTime? DepositRealizedAt = null,
    string? DepositDeferredAccountCode = null,
    bool DepositOutputVatDeferred = false,
    DateTime? DepositOutputVatRecognizedAt = null);

public record ProjectCostBrief(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    int EntryCount,
    decimal Amount);

public record DocumentBrief(
    Guid Id,
    string DocumentNumber,
    DocumentType DocumentType,
    DocumentStatus Status,
    DateTime DocumentDate,
    decimal TotalAmount);

public record DocumentLineResponse(
    Guid Id,
    int LineOrder,
    string Description,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal DiscountAmount,
    decimal Amount,
    decimal VatRate,
    decimal VatAmount,
    decimal WithholdingTaxRate,
    decimal WithholdingTaxAmount,
    Guid? AccountId = null,
    Guid? ProjectId = null,
    string? ProductCode = null,
    Guid? SourceLineId = null,
    string? ProjectCode = null,
    string? ProjectName = null,
    // Set when this line has been auto-spawned into a ProjectCostEntry on
    // approval (via SyncProjectCostEntriesAsync). UI flags the line
    // "🏗️ ลงโครงการแล้ว" so the user knows the cost has been booked.
    Guid? ProjectCostEntryId = null,
    bool HasProjectCostEntry = false,
    // ภาษีซื้อต้องห้าม flag + เหตุผล — UI แสดง checkbox + tooltip
    bool IsVatClaimable = true,
    string? VatNonClaimableReason = null);

// ===== Flexible / partial document conversion =====

/// <summary>Convert only a chosen subset of a source document's lines, each
/// at a chosen quantity — e.g. split one PO into several delivery notes /
/// invoices. <see cref="Lines"/> with quantity 0 are ignored.</summary>
public record PartialConvertRequest(
    List<PartialConvertLineRequest> Lines,
    DateTime? DocumentDate = null,
    DateTime? DueDate = null);

public record PartialConvertLineRequest(Guid SourceLineId, decimal Quantity);

/// <summary>Per-line fulfilment snapshot of a source document — how much of
/// each line has already been carried forward into delivery notes vs.
/// billing documents, and how much remains.</summary>
public record DocumentFulfillmentResponse(
    Guid DocumentId,
    string DocumentNumber,
    DocumentType DocumentType,
    bool SupportsDelivery,
    bool SupportsBilling,
    List<DocumentLineFulfillmentResponse> Lines);

public record DocumentLineFulfillmentResponse(
    Guid LineId,
    int LineOrder,
    string Description,
    string Unit,
    decimal OrderedQuantity,
    decimal DeliveredQuantity,
    decimal DeliveryRemaining,
    decimal BilledQuantity,
    decimal BillingRemaining,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal VatRate,
    decimal WithholdingTaxRate,
    Guid? AccountId,
    Guid? ProjectId,
    string? ProductCode);

public record ContactBrief(Guid Id, string Name, string? TaxId);

public record ApproveDocumentRequest(string? Notes, bool AcknowledgeWarnings = false);

/// <summary>Returned on the first approve attempt when pre-approval checks
/// produced soft warnings (legal/correct but unusual). Operator reviews the
/// list and retries with AcknowledgeWarnings=true to proceed. Hard errors
/// (data corruption / illegal state) still throw inline — they're never
/// surfaced as warnings.</summary>
public record ApprovalWarningsResponse(
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ApprovalWarningAiHintDto>? AiHints = null);

/// <summary>
/// AI-generated hint for one approval warning. Paired with Warnings[i]
/// by index. Surfaced in the UI alongside the warning text — operator
/// sees Primary recommendation + actions + risks without needing to
/// think through the warning from scratch. NULL when AI is disabled
/// or unreachable.
/// </summary>
public record ApprovalWarningAiHintDto(
    string Primary,
    decimal Confidence,
    string? Reasoning,
    IReadOnlyList<string> SuggestedActions,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> ComplianceFlags,
    Guid? FeedbackId,
    bool UsedAi);

// ===== Contact =====
public record CreateContactRequest(
    [property: Required, StringLength(200)] string Name,
    [property: RegularExpression(@"^\d{13}$", ErrorMessage = "เลขประจำตัวผู้เสียภาษีต้องเป็นตัวเลข 13 หลัก")] string? TaxId,
    [property: RegularExpression(@"^\d{5}$", ErrorMessage = "รหัสสาขาต้องเป็นตัวเลข 5 หลัก")] string? BranchCode,
    ContactType? ContactType,
    bool IsCustomer,
    bool IsSupplier,
    string? Address,
    string? Phone,
    string? Email,
    string? ContactPerson,
    // Structured address (optional — recommended for e-Tax compliance)
    string? BranchName = null,
    string? BuildingNumber = null,
    string? BuildingName = null,
    string? Moo = null,
    string? StreetName = null,
    string? SubDistrict = null,
    string? District = null,
    string? Province = null,
    string? PostalCode = null,
    string? CountryCode = null,
    // ตั้งค่าการบันทึกบัญชี — per-contact GL overrides. Null leaves the
    // system default in effect (FindAccountAsync "113" / "212" prefix).
    Guid? DefaultArAccountId = null,
    Guid? DefaultApAccountId = null,
    Guid? DefaultIrGrAccountId = null,
    decimal? CreditLimit = null);

public record UpdateContactRequest(
    [property: StringLength(200)] string? Name,
    [property: RegularExpression(@"^\d{13}$", ErrorMessage = "เลขประจำตัวผู้เสียภาษีต้องเป็นตัวเลข 13 หลัก")] string? TaxId,
    [property: RegularExpression(@"^\d{5}$", ErrorMessage = "รหัสสาขาต้องเป็นตัวเลข 5 หลัก")] string? BranchCode,
    ContactType? ContactType,
    bool? IsCustomer,
    bool? IsSupplier,
    string? Address,
    string? Phone,
    string? Email,
    string? ContactPerson,
    bool? IsActive,
    string? BranchName = null,
    string? BuildingNumber = null,
    string? BuildingName = null,
    string? Moo = null,
    string? StreetName = null,
    string? SubDistrict = null,
    string? District = null,
    string? Province = null,
    string? PostalCode = null,
    string? CountryCode = null,
    Guid? DefaultArAccountId = null,
    Guid? DefaultApAccountId = null,
    Guid? DefaultIrGrAccountId = null,
    decimal? CreditLimit = null);

/// <summary>
/// Result of attempting to delete a contact. May be a hard delete or
/// a soft deactivation if the contact has linked accounting records.
/// </summary>
public record ContactDeleteResult(
    bool Deleted,            // true = removed; false = deactivated only
    bool Deactivated,        // true if the contact was set to inactive
    int LinkedDocumentsCount,
    int LinkedWhtCount,
    string Message);

public record ContactResponse(
    Guid Id,
    string Name,
    string? TaxId,
    string? BranchCode,
    ContactType ContactType,
    bool IsCustomer,
    bool IsSupplier,
    string? Address,
    string? Phone,
    string? Email,
    string? ContactPerson,
    bool IsActive,
    string? BranchName = null,
    string? BuildingNumber = null,
    string? BuildingName = null,
    string? Moo = null,
    string? StreetName = null,
    string? SubDistrict = null,
    string? District = null,
    string? Province = null,
    string? PostalCode = null,
    string? CountryCode = "TH",
    int LoyaltyPoints = 0,
    DateTime? LastVisitAt = null,
    int TotalVisitCount = 0,
    // Per-contact GL overrides — null means "use system default
    // (113/212 prefix)". UI shows the account labels too for display.
    Guid? DefaultArAccountId = null,
    string? DefaultArAccountCode = null,
    string? DefaultArAccountName = null,
    Guid? DefaultApAccountId = null,
    string? DefaultApAccountCode = null,
    string? DefaultApAccountName = null,
    Guid? DefaultIrGrAccountId = null,
    string? DefaultIrGrAccountCode = null,
    string? DefaultIrGrAccountName = null,
    decimal? CreditLimit = null);

/// <summary>Request body for the smart-parse endpoint — paste address text, get structured fields.</summary>
public record ParseAddressRequest(string Address);

public record ParsedAddressResponse(
    string? BuildingNumber,
    string? BuildingName,
    string? StreetName,
    string? SubDistrict,
    string? District,
    string? Province,
    string? PostalCode,
    string? Moo = null);

/// <summary>ค่าเริ่มต้นอัตโนมัติ ระบบวิเคราะห์จากข้อมูลผู้ติดต่อ</summary>
public record ContactSmartDefaults(
    ContactType ContactType,
    string ContactTypeLabel,
    TaxType SuggestedTaxFormType,
    string SuggestedTaxFormLabel,
    DocumentType? SuggestedDocumentType,
    string? SuggestedDocumentTypeLabel,
    decimal DefaultWhtRate,
    string DefaultIncomeTypeCode,
    string DefaultIncomeTypeLabel);

// ===== Payment =====
public record CreatePaymentRequest(
    Guid DocumentId,
    DateTime PaymentDate,
    decimal Amount,
    PaymentMethod PaymentMethod,
    string? Reference,
    string? BankAccount,
    string? Notes,
    /// <summary>Optional — overrides the source document's BankAccountId for
    /// THIS payment only. Use when the cheque actually cleared through a
    /// different bank than the invoice originally targeted; the GL hit and
    /// bank-balance update follow this override, not doc.BankAccountId.</summary>
    Guid? OverrideBankAccountId = null,
    /// <summary>Optional — fund this payment from a specific GL account that
    /// isn't a bank (เงินทดรองกรรมการ / เงินสดย่อย / clearing). When set, the
    /// auto-posted JE's cash side hits this account. Overrides both the bank's
    /// linked GL and the default cash account.</summary>
    Guid? OverridePaymentAccountId = null,
    /// <summary>Optional — WHT withheld on THIS installment. Null = the
    /// service computes a proportional default: Amount / Document.TotalAmount
    /// × Document.WithholdingTaxAmount. Use the override when the customer's
    /// WHT certificate shows a different amount than the proportional split
    /// (e.g. they withhold the full amount on the first installment).
    /// Cumulative WHT across all payments must not exceed the source's
    /// WithholdingTaxAmount.</summary>
    decimal? WithholdingTaxAmount = null,
    /// <summary>Optional — overrides the source document's ProjectId
    /// for THIS payment. Used when one document is split across
    /// project payments (advance booked to Project A; final to
    /// Project B). The auto-posted JE picks this up first; falls
    /// back to Document.ProjectId.</summary>
    Guid? ProjectId = null,
    /// <summary>Multi-document allocation — when set with 1+ rows,
    /// the legacy DocumentId field is ignored and the payment is
    /// split across these target documents. SUM(AllocatedAmount) must
    /// be ≤ Amount; the remainder lands as UnappliedCredit on the
    /// response. WHT is allocated proportionally when individual
    /// rows omit WithholdingTaxAmount. Each AllocatedAmount must be
    /// ≤ the target document's current BalanceDue.</summary>
    List<PaymentAllocationRequest>? Allocations = null,
    /// <summary>Optional — base64 signature image (data-url or bare) to print
    /// in the "ผู้จ่ายเงิน" slot of the PV PDF. Overrides the CreatedBy user's
    /// stored signature for THIS payment. Used by integrations whose service
    /// account has no signature on file. Null = use User.SignatureImageBase64
    /// of CreatedBy.</summary>
    string? PayerSignatureBase64 = null,
    /// <summary>Optional — display name printed under the payer signature
    /// image. Defaults to CreatedBy user's FullName when null.</summary>
    string? PayerSignatureName = null);

public record PaymentAllocationRequest(
    Guid DocumentId,
    decimal AllocatedAmount,
    decimal? WithholdingTaxAmount = null,
    string? Note = null);

public record PaymentAllocationResponse(
    Guid Id,
    Guid PaymentId,
    Guid DocumentId,
    string DocumentNumber,
    DocumentType DocumentType,
    decimal AllocatedAmount,
    decimal WithholdingTaxAmount,
    string? Note);

public record PaymentResponse(
    Guid Id,
    string PaymentNumber,
    Guid DocumentId,
    DateTime PaymentDate,
    decimal Amount,
    PaymentMethod PaymentMethod,
    string? Reference,
    string? BankAccount,
    Guid? BankAccountId,
    string? Notes,
    DateTime CreatedAt,
    /// <summary>Multi-document allocation rows. Empty for legacy
    /// single-doc payments (and the legacy DocumentId then carries
    /// the settled doc id).</summary>
    List<PaymentAllocationResponse>? Allocations = null,
    /// <summary>Amount − SUM(Allocations.AllocatedAmount). Positive
    /// when the customer overpaid (carry-forward credit); zero
    /// otherwise. Doesn't itself create a credit-note; the operator
    /// can later attach the unapplied amount to a new invoice via
    /// /payments/{id}/allocations.</summary>
    decimal UnappliedCredit = 0,
    /// <summary>True when this payment carries a per-request payer signature
    /// override (Payment.PayerSignatureBase64). The blob itself isn't echoed
    /// in the response — only its presence — to keep payloads compact and
    /// avoid leaking signature images to clients that don't render them.</summary>
    bool HasPayerSignature = false,
    string? PayerSignatureName = null);


public record WriteOffBadDebtRequest(string? Reason);
public record BatchConvertRequest(List<Guid> DocumentIds);
