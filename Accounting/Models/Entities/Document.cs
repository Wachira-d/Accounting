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

    // Counterparty-side tax-invoice metadata. Required for PurchaseInvoice
    // (and any other doc where the counterparty issues their own tax
    // invoice we then book). SupplierInvoiceNumber is the partner's own
    // running number — distinct from our internal DocumentNumber and
    // needed for VAT-audit reconciliation against the supplier's
    // statement. SupplierTaxInvoiceDate is the date on the partner's
    // tax invoice; it controls which VAT period the input VAT is
    // claimed in (per Revenue Code §82/4 it may differ from our
    // DocumentDate when we book the bill late).
    public string? SupplierInvoiceNumber { get; set; }
    public DateTime? SupplierTaxInvoiceDate { get; set; }

    /// <summary>True เมื่อผู้ใช้ติ๊ก "ใช้งานใบกำกับภาษี" บนใบสำคัญจ่าย —
    /// บอกว่า PV ใบนี้อ้างใบกำกับภาษีซื้อเพื่อขอเครดิตภาษีซื้อ (ภพ.30).
    /// แยกออกจาก PV ที่จ่ายเฉย ๆ ไม่มี VAT (ค่าใช้จ่ายที่กิจการรับเอง).
    /// เมื่อ true → SupplierInvoiceNumber + SupplierTaxInvoiceDate + Contact.TaxId
    /// + SupplierBranchCode + SubTotal + VatAmount ต้องครบ (RD §86/4, §86/14)
    /// และข้อมูลใบนี้จะไหลเข้ารายงานภาษีซื้อ.</summary>
    public bool HasTaxInvoiceReference { get; set; }

    /// <summary>สาขาผู้ขาย ณ ตอนออกใบกำกับภาษี (snapshot) — Contact.BranchCode
    /// อาจถูกแก้ภายหลัง แต่รายงานภาษีซื้อย้อนหลังต้องคงสาขาเดิมตามใบจริง.
    /// "00000" = สำนักงานใหญ่; "00001"+ = สาขา. Null → fallback ใช้
    /// Contact.BranchCode ตอน export (back-compat กับเอกสารเก่า).</summary>
    public string? SupplierBranchCode { get; set; }

    /// <summary>Credit term in days from the document date — used to
    /// auto-fill DueDate when not explicit, and to roll DSO / DPO
    /// reports. Defaulted from Contact.PaymentTermDays on create when
    /// the caller doesn't override.</summary>
    public int? CreditDays { get; set; }

    /// <summary>Free-text payment terms label (e.g. "Net 30", "2/10
    /// Net 30", "EOM+15") — for human readability on printed
    /// documents. Independent of CreditDays which drives auto math.</summary>
    public string? PaymentTerms { get; set; }

    /// <summary>Settlement basis — Cash (จ่าย/รับทันที) vs Credit (เครดิต).
    /// Primarily for Payment Voucher (ใบสำคัญจ่าย): Cash posts straight to
    /// Cash/Bank with no payable + no due date + no aging; Credit posts to
    /// Accounts Payable, carries a due date, and ages until settled. Null =
    /// not specified → the service picks a per-type default (standalone
    /// Payment Voucher defaults to Cash).</summary>
    public PaymentType? PaymentType { get; set; }

    /// <summary>True when the unit prices on the lines were entered VAT-
    /// INCLUSIVE (ราคารวมภาษี) — common in Thai retail. When set, the line
    /// calculator backs the 7% VAT out of the entered price so SubTotal /
    /// VatAmount post the correct ex-VAT base + tax. False = prices are
    /// ex-VAT (the historical default, VAT added on top).</summary>
    public bool PricesIncludeVat { get; set; }

    // Contact (Customer/Supplier)
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = null!;

    // Reference
    public string? Reference { get; set; }
    public Guid? RelatedDocumentId { get; set; }  // e.g. Quotation → Invoice

    /// <summary>Required when DocumentType = CreditNote — distinguishes the
    /// legal/accounting reason per ประมวลรัษฎากร §82/10. Determines whether
    /// the CN restocks goods (Return only) or is a pure financial adjustment
    /// (Discount / Writeoff / OtherAdjustment).</summary>
    public CreditNoteReason? CreditNoteReason { get; set; }

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
    /// <summary>FX rate at the time of document creation (1 unit of Currency = X THB).
    /// 1.0 when Currency = THB. Captured on Create so JE posting uses the same
    /// rate that was shown to the user on the document.</summary>
    public decimal ExchangeRate { get; set; } = 1m;
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

    /// <summary>True when the operator has explicitly DISMISSED this document
    /// from the "waiting to issue WHT cert" list. The source doc still
    /// exists and is unchanged; we just don't pester the user about it any
    /// more. Used when WHT was deducted but no certificate is needed (e.g.,
    /// internal accruals, intra-company reclassifications).</summary>
    public bool WhtCertSkipped { get; set; }

    /// <summary>Access-control classification — None for the regular sales
    /// stream, Payroll/ExecutivePay/HrPersonal for restricted records.
    /// Owner picks which roles can see each kind in CompanySensitivitySettings.</summary>
    public SensitivityKind Sensitivity { get; set; } = SensitivityKind.None;

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

    /// <summary>ซื้อบริการจาก supplier ต่างประเทศที่ไม่ได้จด VAT ในไทย
    /// (ตามมาตรา 83/6 ผู้รับบริการต้อง self-assess VAT 7% ผ่าน ภ.พ.36 ภายใน
    /// วันที่ 7 ของเดือนถัดไป). Default false. ตั้ง true สำหรับ PI/Expense
    /// ที่เป็น cross-border services (Google Ads / AWS / Software license
    /// จาก US, etc.).</summary>
    public bool IsForeignService { get; set; }

    /// <summary>Link ไปยัง EarlyPaymentDiscountTerm ("2/10 net 30") ที่ผูก
    /// กับเอกสารฝั่งขาย. Receipt ตรวจ window → auto-apply discount. Null =
    /// ไม่มีเงื่อนไขส่วนลดเงินสด.</summary>
    public Guid? EarlyPaymentDiscountTermId { get; set; }

    // ===== External preparer signature override =====
    // When a document is created by an integrating system (e.g. TakeTime
    // syncing a payment voucher), the real preparer is a user of THAT system,
    // not a NextAcc User — so the normal CreatedBy(GUID)→User.Signature lookup
    // finds nothing. The partner can ship the preparer's name + signature image
    // inline; we store them here and ResolveSignersAsync stamps them into the
    // "ผู้จัดทำ" slot directly, before any User/Owner fallback.

    /// <summary>Display name of the external preparer ("ผู้จัดทำ"). Set only
    /// when the document originates from an integration that supplied it.</summary>
    public string? PreparerName { get; set; }

    /// <summary>External preparer's signature image — a "data:image/...;base64,"
    /// URI or raw base64. Rendered in the preparer slot when present.</summary>
    public string? PreparerSignatureBase64 { get; set; }

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

    /// <summary>
    /// Input VAT claimability per ประมวลรัษฎากร §82/5.
    /// <para>true (default) = VAT บนบรรทัดนี้ไปเข้าบัญชี "ภาษีซื้อ 116"
    /// ตอน post JE และจะปรากฏใน ภพ.30 ฝั่ง Input VAT.</para>
    /// <para>false = VAT ต้องห้าม (§82/5(1)(3)(4)(6)(7) — เช่น ค่ารับรอง /
    /// น้ำมันรถยนต์นั่ง / ใบกำกับฯ ไม่สมบูรณ์). JE จะรวม VAT เข้ากับ
    /// ค่าใช้จ่ายเลย (Dr expense = ราคา + VAT) ไม่เข้า ภาษีซื้อ. ภพ.30
    /// จะไม่นับเป็น Input VAT.</para>
    /// </summary>
    public bool IsVatClaimable { get; set; } = true;

    /// <summary>เหตุผลที่ VAT บรรทัดนี้เคลมไม่ได้ — ใช้แสดงในรายงานสรรพากร
    /// + audit trail. ค่าที่ใช้บ่อย: "§82/5(3) ค่ารับรอง" / "§82/5(6)
    /// รถยนต์นั่ง" / "§82/5(1) ใบกำกับฯ ไม่สมบูรณ์" / free text. Null เมื่อ
    /// IsVatClaimable = true.</summary>
    public string? VatNonClaimableReason { get; set; }
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
    /// <summary>หมู่ที่ (village number) — common in rural / provincial Thai
    /// addresses, sits between BuildingNumber and StreetName.</summary>
    public string? Moo { get; set; }
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

    // ───── Per-contact GL account overrides ─────
    // Default at the system level is the first level-4+ account starting
    // with "113" (AR) / "212" (AP) / "212305" (IR/GR clearing) — see
    // DocumentService.FindAccountAsync. When a contact has a specific
    // override here, DocumentService uses THAT account on every doc
    // created against this contact. Lets shops with multiple ลูกหนี้
    // (เครดิตการค้า / ลูกหนี้พนักงาน / ลูกหนี้กรรมการ) book each contact
    // straight to the right ledger without manual JE adjustment.
    public Guid? DefaultArAccountId { get; set; }
    public ChartOfAccount? DefaultArAccount { get; set; }

    public Guid? DefaultApAccountId { get; set; }
    public ChartOfAccount? DefaultApAccount { get; set; }

    /// <summary>IR/GR clearing account — used by the goods-received-not-
    /// invoiced and invoice-received-not-goods accruals. Default 212305
    /// "ค่าใช้จ่ายค้างจ่ายอื่น" in the Thai SME template.</summary>
    public Guid? DefaultIrGrAccountId { get; set; }
    public ChartOfAccount? DefaultIrGrAccount { get; set; }

    /// <summary>Loyalty points balance — earned per POS sale, redeemable next visit.
    /// Default earn rate = 1 point per ฿100, set on the company config later.</summary>
    public int LoyaltyPoints { get; set; } = 0;
    public DateTime? LastVisitAt { get; set; }
    public int TotalVisitCount { get; set; } = 0;

    /// <summary>Credit limit (วงเงินเครดิต) สำหรับลูกค้า — null = ไม่จำกัด
    /// (พฤติกรรมเดิม). ระบบใช้ดู AR ค้างต่อลูกค้าเทียบเทียบขีดจำกัด เพื่อ
    /// เตือนตอนสร้าง Invoice ใหม่ที่จะทำให้ยอดค้างเกินวงเงิน. AI suggest
    /// endpoint (/ai/credit-limit/suggest) คำนวณ P75 จากลูกค้าปัจจุบัน
    /// เป็นค่าเริ่มต้น.</summary>
    public decimal? CreditLimit { get; set; }

    public ICollection<Document> Documents { get; set; } = new List<Document>();
}
