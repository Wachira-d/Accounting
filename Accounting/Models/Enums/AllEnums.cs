namespace Accounting.Models.Enums;

// ==================== User & Role ====================
public enum UserRole
{
    Owner = 1,
    Accountant = 2,
    Staff = 3,
    Auditor = 4,
    Viewer = 5,           // ดูอย่างเดียว
    ExternalAccountant = 6, // นักบัญชีภายนอก / Freelance
    SystemAdmin = 99
}

public enum UserStatus
{
    Active = 1,
    Inactive = 2,
    Suspended = 3,
    PendingVerification = 4
}

// ==================== Company ====================
public enum CompanyStatus
{
    Active = 1,
    Inactive = 2,
    Suspended = 3
}

/// <summary>ประเภทผู้ติดต่อ — ใช้กำหนดแบบ ภ.ง.ด. อัตโนมัติ</summary>
public enum ContactType
{
    Individual = 1,         // บุคคลธรรมดา → ภ.ง.ด.3
    JuristicPerson = 2,     // นิติบุคคล (บริษัท/ห้างหุ้นส่วน) → ภ.ง.ด.53
    GovernmentAgency = 3    // หน่วยงานราชการ → ไม่หัก ณ ที่จ่าย
}

public enum BusinessType
{
    Individual = 1,        // บุคคลธรรมดา
    JuristicPerson = 2,   // นิติบุคคล
    Partnership = 3,       // ห้างหุ้นส่วน
    PublicCompany = 4,     // บริษัทมหาชน
    Foundation = 5,        // มูลนิธิ
    Association = 6,       // สมาคม
    Other = 99
}

/// <summary>
/// ประเภทอุตสาหกรรม/ลักษณะธุรกิจ - กำหนดผังบัญชีเฉพาะทาง
/// </summary>
public enum IndustryType
{
    General = 0,           // ทั่วไป
    Trading = 1,           // ซื้อมาขายไป
    Service = 2,           // ธุรกิจบริการ
    Manufacturing = 3,     // ผลิต/โรงงาน
    Restaurant = 4,        // ร้านอาหาร
    Cafe = 5,              // คาเฟ่/เครื่องดื่ม
    Retail = 6,            // ค้าปลีก
    Construction = 7,      // รับเหมาก่อสร้าง
    RealEstate = 8,        // อสังหาริมทรัพย์
    Technology = 9,        // เทคโนโลยี/ซอฟต์แวร์
    Healthcare = 10,       // สุขภาพ/คลินิก
    Education = 11,        // การศึกษา
    Beauty = 12,           // ความงาม/สปา/ร้านทำเล็บ
    Transportation = 13,   // ขนส่ง/โลจิสติกส์
    Agriculture = 14,      // เกษตร
    Hotel = 15,            // โรงแรม/ที่พัก
    Ecommerce = 16,        // อีคอมเมิร์ซ/ออนไลน์
    Freelance = 17,        // ฟรีแลนซ์
    Other = 99             // อื่นๆ
}

// ==================== Subscription & Trial ====================
public enum SubscriptionPlan
{
    FreeTrial = 0,
    Basic = 1,
    Pro = 2,
    Enterprise = 3
}

public enum SubscriptionStatus
{
    Trial = 0,
    Active = 1,
    PastDue = 2,
    Cancelled = 3,
    Expired = 4,
    Suspended = 5
}

public enum BillingCycle
{
    Monthly = 1,
    Quarterly = 3,
    SemiAnnual = 6,
    Annual = 12
}

public enum TrialStatus
{
    NotStarted = 0,
    Active = 1,
    Extended = 2,
    Expired = 3,
    Converted = 4   // แปลงเป็น paid แล้ว
}

// ==================== Accounting ====================
public enum AccountType
{
    Asset = 1,            // สินทรัพย์
    Liability = 2,        // หนี้สิน
    Equity = 3,           // ส่วนของเจ้าของ
    Revenue = 4,          // รายได้
    Expense = 5           // ค่าใช้จ่าย
}

/// <summary>หมวด Cash Flow Statement (TFRS 7) — per-account override
/// สำหรับผังบัญชี custom ที่ไม่ตามรหัส default. ใช้ในรายงาน Cash Flow.
/// None = fallback ใช้ code-prefix heuristics (operating: 113/115/212/56...,
/// investing: 122/123, financing: 221/31).</summary>
public enum CashFlowSectionType
{
    None = 0,             // ปล่อยให้ engine fallback (code-prefix)
    Operating = 1,        // กิจกรรมดำเนินงาน
    Investing = 2,        // กิจกรรมลงทุน
    Financing = 3         // กิจกรรมจัดหาเงิน
}

public enum JournalEntryStatus
{
    Draft = 0,
    Posted = 1,
    Voided = 2,
    Reversed = 3
}

/// <summary>Post-Dated Check direction.</summary>
public enum PdcDirection
{
    Inbound = 1,    // ลูกค้าจ่ายเช็คให้เรา (receivable)
    Outbound = 2    // เราจ่ายเช็คให้ vendor (payable)
}

/// <summary>Post-Dated Check lifecycle.</summary>
public enum PdcStatus
{
    Held = 1,           // เก็บไว้ ยังไม่ถึงวัน — Inbound: เก็บในตู้เซฟ; Outbound: ออกแล้วแต่ผู้รับยังไม่ขึ้นเงิน
    Deposited = 2,      // ฝากธนาคารแล้ว รอ clearing
    Cleared = 3,        // ธนาคารหักเงินสำเร็จ — final
    Dishonored = 4,     // เด้ง (เงินไม่พอ / บัญชีปิด / สั่งห้ามจ่าย) — ต้องตามจริง
    Cancelled = 5,      // ยกเลิกก่อนฝาก (ลูกค้าขอเปลี่ยนเป็นโอน, etc.)
    Returned = 6        // ส่งคืน Inbound: คืนเช็คให้ลูกค้า; Outbound: vendor คืนเช็คเรา
}

/// <summary>Cash advance request lifecycle.</summary>
public enum CashAdvanceStatus
{
    Requested = 1,        // พนง.ขอเบิก รอ manager อนุมัติ
    Approved = 2,         // อนุมัติแล้ว รอ finance จ่ายเงิน
    Disbursed = 3,        // จ่ายเงินสด/โอนแล้ว รอ clearance
    PendingClearance = 4, // เลยกำหนด clear แล้วยังไม่ส่งใบเสร็จครบ
    Cleared = 5,          // ส่งใบเสร็จครบ + ค่าใช้จ่ายลงบัญชีแล้ว
    Refunded = 6,         // เคลียร์เกิน → พนง.คืนเงิน
    Rejected = 7,         // ปฏิเสธคำขอ
    Cancelled = 8         // พนง.ยกเลิกเอง
}

/// <summary>
/// ประเภทสมุดรายวัน ตามมาตรฐานบัญชีไทย (พ.ร.บ.การบัญชี 2543)
/// </summary>
public enum JournalType
{
    General = 0,        // JV - สมุดรายวันทั่วไป
    Sales = 1,          // SV - สมุดรายวันขาย
    Purchase = 2,       // UV - สมุดรายวันซื้อ
    CashReceipts = 3,   // RV - สมุดรายวันรับ
    CashPayments = 4,   // PV - สมุดรายวันจ่าย
}

public enum FiscalPeriodStatus
{
    Open = 1,
    Closed = 2,
    Locked = 3
}

// ==================== Tax ====================
public enum TaxType
{
    VAT = 1,              // ภาษีมูลค่าเพิ่ม
    WithholdingTax3 = 2,  // ภงด.3
    WithholdingTax53 = 3, // ภงด.53
    WithholdingTax1 = 4,  // ภงด.1 (เงินเดือน)
    SocialSecurity = 5,   // ประกันสังคม
    CorporateIncomeTax = 6, // ภงด.50/51
    PersonalIncomeTax91 = 7, // ภงด.91 (ภาษีเงินได้บุคคลธรรมดา)
    WithholdingTax54 = 8, // ภงด.54 (Foreign WHT — บริการต่างประเทศ)
    VatPp36 = 9,          // ภพ.36 (Foreign service VAT)
    StampDuty = 10,       // อากรแสตมป์ (Code §103-105 ประมวลรัษฎากร)
}

/// <summary>
/// Inventory costing method per product. Determines how COGS is
/// calculated on outbound stock movements + how period-end snapshot
/// values are computed.
/// </summary>
public enum CostingMethod
{
    /// <summary>Default for Thai SME — running weighted average
    /// recomputed on every receipt: newAvg = (oldStock×oldAvg +
    /// receivedQty×receivedCost) / (oldStock + receivedQty).</summary>
    WeightedAverage = 0,

    /// <summary>FIFO — oldest stock layer consumed first. Requires
    /// tracking individual cost layers via StockMovement history.</summary>
    Fifo = 1,

    /// <summary>Standard cost — uses Product.CostPrice fixed value
    /// regardless of receipt prices; variances posted separately.</summary>
    Standard = 2,
}

/// <summary>
/// Cheque lifecycle states — Thai SMEs still use cheques heavily for
/// vendor payments + customer collections. Tracking these states lets
/// AP/AR teams know which cheques are outstanding and need follow-up.
/// </summary>
public enum ChequeStatus
{
    /// <summary>Cheque written + handed to payee, not yet cashed.</summary>
    Issued = 0,

    /// <summary>Cashed at the bank — cleared the account.</summary>
    Cleared = 1,

    /// <summary>Stop-payment requested OR cheque returned (bounced).
    /// Need to issue replacement.</summary>
    Bounced = 2,

    /// <summary>Voided before issuing — torn out of the book or
    /// admin-cancelled. Used to keep the chequebook number sequence
    /// honest.</summary>
    Voided = 3,

    /// <summary>Cheque received from a customer, deposited but not
    /// yet cleared. Funds in transit.</summary>
    DepositedPending = 4,
}

// ==================== Migration Wizard ====================
public enum MigrationType
{
    ChartOfAccounts = 1,
    TrialBalance = 2,
    SubLedgerAR = 3,
    SubLedgerAP = 4,
    SubLedgerFixedAssets = 5,
    SubLedgerInventory = 6,
    OpeningBalances = 7,
}

public enum MigrationStatus
{
    Draft = 0,         // session created, mapping/upload in progress
    Mapping = 1,       // COA mapping in progress
    Validating = 2,    // sub-ledger ↔ control-account validation
    ReadyToCommit = 3, // all validations passed
    Committed = 4,     // data persisted
    Failed = 5,        // commit aborted on integrity error
    Cancelled = 6,
    RolledBack = 7,    // a Committed session whose entries were undone
}

// ==================== RD Compliance ====================
public enum RdComplianceStatus
{
    Pending = 0,       // not yet evaluated
    Valid = 1,         // all checks passed
    Warning = 2,       // soft issues — operator can override
    Failed = 3,        // hard issues — must be fixed before posting
    ManuallyOverridden = 4, // operator overrode a warning/failure
    Approved = 5,      // accepted into the system
}

public enum VatRate
{
    Zero = 0,
    Seven = 7,
    Exempt = -1
}

public enum TaxReportStatus
{
    Draft = 0,
    Filed = 1,
    Submitted = 2
}

// ==================== Document ====================
public enum DocumentType
{
    // ===== ฝั่งรายรับ (Revenue/Sales) =====
    Quotation = 1,           // ใบเสนอราคา
    Invoice = 2,             // ใบแจ้งหนี้
    Receipt = 3,             // ใบเสร็จรับเงิน
    TaxInvoice = 4,          // ใบกำกับภาษี
    DebitNote = 5,            // ใบเพิ่มหนี้
    CreditNote = 6,           // ใบลดหนี้
    DeliveryNote = 10,        // ใบส่งของ
    BillingNote = 11,         // ใบวางบิล
    ReceiptVoucher = 14,      // ใบสำคัญรับ

    // ===== ฝั่งรายจ่าย (Expense/Purchase) =====
    PurchaseRequisition = 12, // ใบขอซื้อ
    PurchaseOrder = 7,        // ใบสั่งซื้อ
    GoodsReceiptNote = 16,    // ใบรับสินค้า (GRN) — รับของจริงจาก vendor; 3-way match: PO ↔ GRN ↔ Invoice
    PurchaseInvoice = 8,      // ใบแจ้งหนี้ซื้อ
    Expense = 9,              // ใบบันทึกค่าใช้จ่าย
    PaymentVoucher = 13,      // ใบสำคัญจ่าย
    CertificateInLieu = 15,   // ใบรับรองแทนใบเสร็จ
}

/// <summary>
/// Sensitivity classification for documents, JEs, and payroll records.
/// Owner configures, per company, which roles can see each kind. The API
/// returns redacted stubs (not 404) so integration targets know the record
/// exists but is hidden.
/// </summary>
public enum SensitivityKind
{
    None = 0,        // เปิดเผยปกติ
    Payroll = 1,     // ข้อมูลเงินเดือน / ใบจ่ายเงินเดือน / สลิป / JE เงินเดือน
    ExecutivePay = 2,// โบนัส / เงินพิเศษผู้บริหาร
    HrPersonal = 3,  // ข้อมูลส่วนตัวพนักงาน
    Confidential = 9 // เอกสารลับอื่นๆ — กำหนดสิทธิ์เอง
}

public enum DocumentStatus
{
    Draft = 0,
    WaitingApproval = 1,
    Approved = 2,
    Sent = 3,
    PartiallyPaid = 4,
    Paid = 5,
    Voided = 6,
    Overdue = 7,
    Rejected = 8
}

public enum PaymentMethod
{
    Cash = 1,
    BankTransfer = 2,
    CreditCard = 3,
    Cheque = 4,
    PromptPay = 5,
    DirectDebit = 6,
    EWallet = 7,
    Other = 99
}

/// <summary>
/// Settlement basis for a money-movement document (primarily ใบสำคัญจ่าย /
/// Payment Voucher, but reusable). Decides the GL posting + whether the
/// document carries an outstanding balance:
///   Cash   = จ่าย/รับทันที — posts straight to Cash/Bank, no payable/
///            receivable, BalanceDue = 0, no due date, never ages.
///   Credit = เครดิต — posts to Accounts Payable (or Receivable), carries a
///            due date + outstanding balance, and ages until settled.
/// Null on a document = legacy/not-applicable; the service infers a sensible
/// default per document type (standalone Payment Voucher → Cash).
/// </summary>
public enum PaymentType
{
    Cash = 1,
    Credit = 2
}


// ==================== Audit ====================
public enum AuditAction
{
    Create = 1,
    Update = 2,
    Delete = 3,
    Login = 4,
    Logout = 5,
    Export = 6,
    Print = 7,
    View = 8,
    Approve = 9,
    Reject = 10,
    ApiAccess = 12
}

// ==================== Product/Service ====================
public enum ProductType
{
    Product = 1,      // สินค้า
    Service = 2,      // บริการ
    NonStock = 3,     // ไม่ติดตามสต็อก
    Supplies = 4      // วัสดุสิ้นเปลือง (ผ้าปู, ปลอกหมอน, สบู่, กระดาษ ฯลฯ)
}

// ==================== Bank ====================
public enum BankTransactionType
{
    Deposit = 1,
    Withdrawal = 2,
    Transfer = 3,
    Fee = 4,
    Interest = 5
}

public enum ReconciliationStatus
{
    Unmatched = 0,
    Matched = 1,
    Excluded = 2,
    /// <summary>AI-suggested match — user must confirm in the bank
    /// reconciliation UI before it counts as Matched. Used when local
    /// strict-match misses but AI re-ranking finds a likely candidate.</summary>
    Suggested = 3,
}

// ==================== Recurring ====================
public enum RecurringFrequency
{
    Daily = 1,
    Weekly = 7,
    BiWeekly = 14,
    Monthly = 30,
    Quarterly = 90,
    SemiAnnual = 180,
    Annual = 365
}

public enum RecurringStatus
{
    Active = 1,
    Paused = 2,
    Completed = 3,
    Cancelled = 4
}

// ==================== Fixed Asset ====================
public enum DepreciationMethod
{
    None = 0,                   // ไม่คิดค่าเสื่อม (ที่ดิน, งานระหว่างก่อสร้าง — TFRS บทที่ 10/§65)
    StraightLine = 1,           // เส้นตรง
    DecliningBalance = 2,       // ยอมลดลง
    DoubleDecliningBalance = 3  // ยอดลดลงสองเท่า
}

public enum AssetStatus
{
    Active = 1,
    Disposed = 2,
    FullyDepreciated = 3,
    WrittenOff = 4
}

/// <summary>
/// Classifies an asset by accounting treatment so the same module can hold
/// the four broad categories of non-current assets defined by TFRS:
///   • Tangible       → ที่ดิน อาคาร อุปกรณ์ — straight-line / declining-balance depreciation
///   • Intangible     → ซอฟต์แวร์ ลิขสิทธิ์ — amortization (terminology + GL accounts differ)
///   • RightOfUse     → สินทรัพย์สิทธิการใช้ (TFRS 16 lease) — amortization over lease term
///   • InvestmentProperty → อสังหาริมทรัพย์เพื่อการลงทุน — cost or fair-value model
/// Existing rows migrate to Tangible by default so the rename is non-breaking.
/// </summary>
public enum AssetType
{
    Tangible = 1,            // ที่ดิน อาคาร และอุปกรณ์ (Property, Plant, Equipment)
    Intangible = 2,          // สินทรัพย์ไม่มีตัวตน — software, patents, trademarks
    RightOfUse = 3,          // สินทรัพย์สิทธิการใช้ (TFRS 16 lease)
    InvestmentProperty = 4   // อสังหาริมทรัพย์เพื่อการลงทุน
}

// ==================== Approval Workflow ====================
public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Recalled = 3
}

// ==================== Cross-tenant trading partnership ====================
public enum TradingPartnershipStatus
{
    Pending = 0,      // invitation sent, awaiting partner acceptance
    Accepted = 1,     // both sides confirmed, docs may flow
    Suspended = 2,    // either side paused (recoverable)
    Rejected = 3,     // invitation declined / link severed
}

public enum CrossTenantLinkType
{
    /// <summary>A's Quotation routed to B for approval.</summary>
    QuotationFlow = 0,
    /// <summary>B's Purchase Order routed back to A.</summary>
    PoFlow = 1,
    /// <summary>A's Invoice routed to B.</summary>
    InvoiceFlow = 2,
    /// <summary>A's Receipt routed to B.</summary>
    ReceiptFlow = 3,
    /// <summary>B's Payment notification routed to A.</summary>
    PaymentFlow = 4,
}

public enum CrossTenantLinkStatus
{
    PendingApproval = 0,
    Approved = 1,
    Rejected = 2,
    AutoChained = 3,    // approved and downstream doc auto-created
    Withdrawn = 4,      // source side voided / withdrew
}

// ==================== Subscription Payment ====================
public enum SubscriptionPaymentStatus
{
    Pending = 0,           // รอตรวจสอบ
    UnderReview = 1,       // กำลังตรวจสอบ
    Approved = 2,          // อนุมัติแล้ว
    Rejected = 3,          // ปฏิเสธ
    Cancelled = 4          // ยกเลิก
}

/// <summary>ที่มาของ payment record — แยกการรับเงินปกติ (ลูกค้าอัปสลิป)
/// ออกจากการบันทึกโดย admin เพื่อ audit + รายงานรายรับ (WP-C1).</summary>
public enum SubscriptionPaymentKind
{
    Normal = 0,            // ลูกค้าชำระ + อัปสลิป → admin review
    ManualByAdmin = 1,     // admin บันทึกรับเงินเอง (เงินสด/โอน นอกระบบ) → อนุมัติทันที
    Waived = 2             // ยกเว้น/ต่ออายุให้ฟรี ยอด 0 (Goodwill/Compensation/Correction)
}

/// <summary>เหตุผลการต่ออายุแบบไม่เก็บเงิน (Waived) — บังคับเลือกเพื่อ audit.</summary>
public enum SubscriptionWaiveReason
{
    Goodwill = 1,          // ไมตรีจิต/ชดเชยความไม่สะดวก
    Compensation = 2,      // ชดเชยเหตุขัดข้องระบบ
    Correction = 3         // แก้ไขข้อผิดพลาดการบันทึก
}

// ==================== Notification ====================
public enum NotificationType
{
    System = 1,
    TrialExpiry = 2,
    InvoiceDue = 3,
    PaymentReceived = 4,
    ApprovalRequired = 5,
    TaskAssigned = 6,
    SecurityAlert = 8,
    DocumentCreated = 9,
    MonthEndReminder = 10,
    SubscriptionExpiring = 11,     // แจ้งเตือนก่อนหมดอายุ
    SubscriptionExpired = 12,      // แจ้งเตือนหมดอายุแล้ว
    SubscriptionDeactivation = 13, // แจ้งเตือนก่อนตัดบัญชี/ระงับ
    SubscriptionPaymentPending = 14,  // มีการชำระเงินรอตรวจสอบ
    SubscriptionPaymentApproved = 15, // ชำระเงินอนุมัติแล้ว
    SubscriptionPaymentRejected = 16, // ชำระเงินถูกปฏิเสธ
    SubscriptionRenewed = 17          // ต่ออายุสำเร็จ
}

public enum NotificationChannel
{
    InApp = 1,
    Email = 2,
    Both = 3
}

// ==================== Expense Claim ====================
public enum ExpenseClaimStatus
{
    Draft = 0,
    Submitted = 1,
    Approved = 2,
    Rejected = 3,
    Paid = 4,
    Voided = 5
}

// ==================== API Key ====================
public enum ApiKeyStatus
{
    Active = 1,
    Revoked = 2,
    Expired = 3
}

// ==================== Feature Flags for Trial ====================
[Flags]
public enum FeatureFlags : long
{
    None = 0,
    BasicAccounting = 1 << 0,
    AdvancedReporting = 1 << 1,
    TaxManagement = 1 << 2,
    DocumentEngine = 1 << 3,
    MultiCompany = 1 << 4,
    APIAccess = 1 << 5,
    BulkImport = 1 << 6,
    CustomChartOfAccounts = 1 << 7,
    AutoPosting = 1 << 8,
    EtaxInvoice = 1 << 9,
    WorkflowEngine = 1 << 10,
    AuditLog = 1 << 11,
    EmailNotification = 1 << 12,
    MultiUser = 1 << 13,
    BankReconciliation = 1 << 14,
    Inventory = 1 << 15,
    FixedAssets = 1 << 16,
    RecurringTransactions = 1 << 17,
    MultiCurrency = 1 << 18,
    ApprovalWorkflow = 1 << 20,
    FileAttachments = 1 << 21,
    PurchaseOrders = 1 << 22,
    ExpenseManagement = 1 << 23,
    Dashboard = 1 << 24,
    BudgetManagement = 1 << 25,
    AgingReport = 1 << 26,

    // New feature flags
    Payroll = 1 << 27,
    ProjectAccounting = 1 << 28,
    CostCenter = 1 << 29,
    Consolidation = 1L << 30,
    WarehouseManagement = 1L << 31,
    LoanManagement = 1L << 32,
    Commission = 1L << 33,
    AI_Features = 1L << 34,
    DocumentOCR = 1L << 35,
    ReportBuilder = 1L << 36,
    CustomerPortal = 1L << 37,
    TimeBilling = 1L << 38,
    OpenBanking = 1L << 39,
    Webhook = 1L << 40,
    RevenueRecognition = 1L << 41,
    FPA = 1L << 42,

    // eTax modes (split from EtaxInvoice for granular control)
    EtaxByEmail = 1L << 43,   // PDF/A-3 with embedded XML, sent to customer + RD csemail timestamp
    EtaxDirect = 1L << 44,    // XML submitted directly to RD API

    // CMS & Multi-Site
    CmsWebsiteBuilder = 1L << 45,
    CmsEcommerce = 1L << 46,
    CmsBooking = 1L << 47,

    // Preset combos
    TrialFeatures = BasicAccounting | DocumentEngine | TaxManagement | Dashboard,
    BasicFeatures = TrialFeatures | AuditLog | EmailNotification | FileAttachments | AgingReport,
    ProFeatures = BasicFeatures | AdvancedReporting | MultiCompany | APIAccess | BulkImport
        | CustomChartOfAccounts | AutoPosting | MultiUser | BankReconciliation | Inventory
        | RecurringTransactions | ApprovalWorkflow | PurchaseOrders | ExpenseManagement | BudgetManagement
        | CostCenter | EtaxByEmail,
    EnterpriseFeatures = ProFeatures | EtaxInvoice | EtaxDirect | WorkflowEngine | FixedAssets
        | MultiCurrency | Payroll | ProjectAccounting | Consolidation
        | WarehouseManagement | LoanManagement | Commission | AI_Features | DocumentOCR
        | ReportBuilder | CustomerPortal | TimeBilling | OpenBanking | Webhook
        | RevenueRecognition | FPA
        | CmsWebsiteBuilder | CmsEcommerce | CmsBooking
}

// ==================== Dimensional Accounting ====================
public enum DimensionType
{
    CostCenter = 1,
    ProfitCenter = 2,
    Department = 3,
    /// <summary>ใช้แท็บ "สาขา" (Branch entity — ผูกรหัสสาขาสรรพากร) แทน;
    /// คงค่าไว้เพื่อข้อมูลเก่า/ลิงก์ Branch.DimensionId</summary>
    Branch = 4,
    /// <summary>LEGACY — ห้ามสร้างใหม่: ซ้ำกับ Project entity (เมนูโครงการ) ที่มี
    /// งบ/สัญญา/POC/กำไรต่องานเต็มรูป. UI ตัดตัวเลือกนี้ออกแล้ว คงค่าไว้เพื่อ
    /// ข้อมูลเก่าเท่านั้น</summary>
    Project = 5,
    Fund = 6,
    Segment = 7,
    Custom = 99
}

// ==================== Intercompany ====================
public enum IntercompanyStatus
{
    Pending = 0,
    ConfirmedBySource = 1,
    ConfirmedByTarget = 2,
    Completed = 3,
    Rejected = 4,
    Voided = 5
}

// ==================== Consolidation ====================
public enum ConsolidationMethod
{
    Full = 1,             // > 50% ownership - งบรวมเต็ม
    Proportionate = 2,    // Joint venture - ตามสัดส่วน
    Equity = 3,           // 20-50% - วิธีส่วนได้เสีย
    Cost = 4              // < 20% - วิธีราคาทุน
}

// ==================== Smart Import ====================
public enum ImportSessionStatus
{
    Uploading = 0,         // กำลังอัพโหลด
    Analyzing = 1,         // AI กำลังวิเคราะห์
    MappingRequired = 2,   // ต้องการ Manual Mapping
    MappingCompleted = 3,  // Mapping เสร็จแล้ว
    Validating = 4,        // กำลังตรวจสอบ
    Ready = 5,             // พร้อม Import
    Importing = 6,         // กำลัง Import
    Completed = 7,         // Import สำเร็จ
    Failed = 8,            // Import ล้มเหลว
    AwaitingDuplicateResolution = 9   // ตรวจพบของซ้ำ — รอ user เลือก
}

/// <summary>ทางเลือกของ user เมื่อ import detect ของซ้ำ.</summary>
public enum ImportConflictResolution
{
    Pending = 0,        // ยังไม่ได้ตัดสิน
    UseExisting = 1,    // ใช้ข้อมูลเดิม ทิ้งของใหม่
    UseNew = 2,         // ใช้ข้อมูลใหม่ ทับของเดิม (update)
    Skip = 3,           // ข้ามแถวนี้ ไม่ทำอะไร
    Merge = 4,          // รวม field-by-field (เก็บใน MergeChoicesJson)
    CreateAnyway = 5    // สร้างใหม่ทั้งสองอย่าง (allow duplicate — vendor มี 2 บัญชี)
}

public enum ColumnMatchType
{
    ExactMatch = 1,        // ชื่อตรงกันเป๊ะ
    AiMatched = 2,         // AI วิเคราะห์จับคู่
    ManualMatch = 3,       // User จับคู่เอง
    Unmapped = 4           // ยังไม่ได้จับคู่
}

public enum ColumnMatchConfidence
{
    High = 1,              // ≥ 90% มั่นใจสูง
    Medium = 2,            // 60-89% มั่นใจปานกลาง
    Low = 3,               // < 60% มั่นใจต่ำ - ควร Manual
    None = 4               // จับคู่ไม่ได้
}

// ==================== POS (Point of Sale) ====================
public enum PosBusinessMode
{
    Trading = 1,           // ซื้อมาขายไป
    Restaurant = 2,        // ร้านอาหาร
    Cafe = 3,              // คาเฟ่/เครื่องดื่ม
    Service = 4,           // ธุรกิจบริการ (ร้านทำเล็บ, สปา, ฯลฯ)
    Mixed = 5              // ผสม (สินค้า+บริการ)
}

public enum PosOrderType
{
    WalkIn = 1,            // ลูกค้า walk-in
    DineIn = 2,            // นั่งทานในร้าน
    TakeAway = 3,          // ซื้อกลับ
    Delivery = 4,          // เดลิเวอรี่
    Appointment = 5,       // นัดหมาย (สำหรับบริการ)
    Online = 6             // สั่งออนไลน์
}

public enum PosOrderStatus
{
    Open = 0,              // เปิดบิล
    InProgress = 1,        // กำลังดำเนินการ
    ReadyToServe = 2,      // พร้อมเสิร์ฟ/ส่งมอบ
    Completed = 3,         // จ่ายเงินแล้ว/เสร็จสิ้น
    Voided = 4,            // ยกเลิก
    Refunded = 5,          // คืนเงิน
    OnHold = 6             // พักบิล
}

/// <summary>Table reservation lifecycle. Pending → Confirmed → Seated →
/// Completed is the happy path; Cancelled / NoShow are tracked separately
/// so the host can report on no-show rates.</summary>
public enum ReservationStatus
{
    Pending = 0,           // ลูกค้าจองแต่ยังไม่ยืนยัน
    Confirmed = 1,         // ยืนยันแล้ว (โทรกลับ / LINE)
    Seated = 2,            // ลูกค้ามาถึงและนั่งโต๊ะแล้ว — ลิงก์กับ PosOrder
    Completed = 3,         // จบบิลแล้ว
    Cancelled = 4,         // ลูกค้ายกเลิก
    NoShow = 5             // ไม่มาตามนัด
}

public enum PosItemStatus
{
    Pending = 0,           // รอดำเนินการ
    Preparing = 1,         // กำลังเตรียม (ครัว/บาร์)
    Ready = 2,             // พร้อมเสิร์ฟ
    Served = 3,            // เสิร์ฟแล้ว
    Cancelled = 4          // ยกเลิก
}

public enum PosSessionStatus
{
    Open = 1,              // เปิดกะ
    Closed = 2             // ปิดกะ
}

public enum ServiceActivityStatus
{
    Pending = 0,           // รอดำเนินการ
    InProgress = 1,        // กำลังทำ
    Completed = 2,         // เสร็จแล้ว
    Skipped = 3            // ข้าม
}

public enum CommissionType
{
    Fixed = 1,             // จำนวนเงินคงที่
    Percentage = 2         // เปอร์เซ็นต์จากราคาบริการ
}

// ==================== E-Tax Invoice ====================
public enum EtaxStatus
{
    Generated = 0,
    Signed = 1,
    Submitted = 2,
    Accepted = 3,
    Rejected = 4,
    Error = 5,
    Voided = 6      // ยกเลิก: เก็บ XML ไว้เพื่อ audit trail (ไม่ลบจริง)
}

// e-Tax delivery mode chosen by the company
public enum EtaxMode
{
    None = 0,        // ไม่ใช้ e-Tax
    ByEmail = 1,     // e-Tax Invoice & Receipt by Email (รายได้ < 30 ล้าน, ส่งทางอีเมลพร้อม CC csemail@etax.teda.th)
    Direct = 2,      // e-Tax Invoice & Receipt (Full) ยิง XML เข้าระบบสรรพากรโดยตรง ต้องมี CA cert
    Both = 3         // เปิดทั้งสอง โหมด ให้ user เลือกตอนสร้างเอกสาร
}

// e-Tax delivery channel for a single send action (logged per send)
public enum EtaxDeliveryChannel
{
    Email = 0,       // ส่งทางอีเมล (รวม CC RD timestamp)
    RdApi = 1        // ยิง XML เข้า RD API
}

// ==================== Email Provider ====================
public enum EmailProvider
{
    Smtp = 0,            // Generic SMTP (Gmail SMTP, Office365 SMTP, etc.)
    MicrosoftGraph = 1,  // Microsoft Graph API (OAuth2 client credentials)
    GmailApi = 2,        // Gmail API (OAuth2 refresh token)
    SendGrid = 3         // SendGrid HTTP API
}

public enum EmailLogStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    Bounced = 3
}

// ==================== CMS & Multi-Site ====================

public enum SiteStatus
{
    Draft = 0,
    Published = 1,
    Maintenance = 2,
    Suspended = 3
}

public enum SiteType
{
    Corporate = 1,
    Ecommerce = 2,
    Booking = 3,
    ServiceCatalog = 4,
    Hybrid = 5
}

public enum SiteRenderMode
{
    ServerRendered = 1,
    Headless = 2,
    Hybrid = 3
}

public enum DomainType
{
    Subdomain = 1,
    CustomDomain = 2
}

public enum DomainVerificationStatus
{
    Pending = 0,
    Verifying = 1,
    Verified = 2,
    Failed = 3
}

public enum DomainApprovalStatus
{
    PendingApproval = 0,
    Approved = 1,
    Rejected = 2
}

public enum PageStatus
{
    Draft = 0,
    Published = 1,
    Scheduled = 2,
    Archived = 3
}

public enum PageType
{
    Standard = 1,
    Landing = 2,
    Blog = 3,
    Product = 4,
    Category = 5,
    Checkout = 6,
    Custom = 99
}

public enum CmsBlockType
{
    Hero = 1,
    RichText = 2,
    Image = 3,
    Gallery = 4,
    Video = 5,
    ProductGrid = 6,
    ProductDetail = 7,
    BookingCalendar = 8,
    ContactForm = 9,
    RfqForm = 10,
    Map = 11,
    Testimonials = 12,
    Faq = 13,
    PricingTable = 14,
    CallToAction = 15,
    SocialFeed = 16,
    Newsletter = 17,
    Countdown = 18,
    Divider = 19,
    Html = 20,
    NavigationBlock = 21,
    FooterBlock = 22,
    CartSummary = 23,
    SearchResults = 24,
    BlogList = 25,
    CategoryList = 26,
    Custom = 99
}

public enum StockBehavior
{
    InStockOnly = 1,
    AllowBackorder = 2,
    AllowPreorder = 3
}

public enum SiteOrderStatus
{
    Pending = 0,
    Confirmed = 1,
    Processing = 2,
    Shipped = 3,
    Delivered = 4,
    Cancelled = 5,
    Refunded = 6,
    PartiallyRefunded = 7
}

public enum SitePaymentStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    Refunded = 4,
    Cancelled = 5
}

public enum BookingType
{
    Lead = 1,
    Appointment = 2,
    Guaranteed = 3,
    PrePayment = 4
}

public enum BookingStatus
{
    Pending = 0,
    Confirmed = 1,
    InProgress = 2,
    Completed = 3,
    Cancelled = 4,
    NoShow = 5
}

public enum ErpBookingDocumentType
{
    DraftServiceOrder = 1,
    AdvanceReceipt = 2,
    TaxInvoice = 3,
    Quotation = 4
}

public enum SiteMediaType
{
    Image = 1,
    Video = 2,
    Document = 3,
    Audio = 4
}

public enum CookieCategory
{
    Necessary = 1,
    Analytics = 2,
    Marketing = 3,
    Functional = 4
}

public enum SiteStaffRole
{
    Admin = 1,
    Editor = 2,
    ContentWriter = 3,
    OrderManager = 4,
    Viewer = 5
}

public enum SiteAddressType
{
    Shipping = 1,
    Billing = 2,
    Both = 3
}

public enum SiteFormType
{
    Contact = 1,
    Rfq = 2,
    Support = 3,
    Feedback = 4,
    Custom = 99
}

public enum FormFieldType
{
    Text = 1,
    Email = 2,
    Phone = 3,
    Number = 4,
    TextArea = 5,
    Select = 6,
    MultiSelect = 7,
    Checkbox = 8,
    Radio = 9,
    Date = 10,
    DateTime = 11,
    File = 12
}

public enum FormSubmissionStatus
{
    New = 0,
    Read = 1,
    Replied = 2,
    ConvertedToQuotation = 3,
    Archived = 4
}

public enum PaymentGatewayType
{
    BankTransfer = 1,
    PromptPay = 2,
    CreditCard = 3,
    PayPal = 4,
    Stripe = 5,
    TwoC2P = 6,
    Omise = 7,
    Custom = 99
}

public enum CouponDiscountType
{
    Percentage = 1,
    FixedAmount = 2,
    FreeShipping = 3
}

public enum CouponScope
{
    AllProducts = 1,
    SpecificCategories = 2,
    SpecificProducts = 3,
    CustomerGroup = 4
}

public enum ShippingRateType
{
    Flat = 1,
    ByWeight = 2,
    Free = 3,
    Calculated = 4
}

public enum CheckoutMode
{
    PaymentRequired = 1,
    QuotationOnly = 2,
    QuotationThenPayment = 3,
    InquiryOnly = 4
}

public enum InvitationStatus
{
    Pending   = 0,
    Accepted  = 1,
    Expired   = 2,
    Cancelled = 3,
}

public enum CreditNoteReason
{
    /// <summary>คืนสินค้า (sales return) — ลด VAT ขาย + คืนสต๊อก</summary>
    Return = 1,
    /// <summary>ส่วนลด / ลดราคา (price discount) — ลด VAT ขาย เท่านั้น สต๊อกไม่กระทบ</summary>
    Discount = 2,
    /// <summary>ค่าสินค้าน้อยกว่าที่ตกลง / ปรับยอด (adjustment) — สต๊อกไม่กระทบ</summary>
    Adjustment = 3,
    /// <summary>ตัดยอด / ตัดหนี้สูญบางส่วน (write-off) — สต๊อกไม่กระทบ</summary>
    Writeoff = 4,
}

public enum WhtRecognitionBasis
{
    /// <summary>Recognize WHT-Asset / WHT-Payable at the moment of payment
    /// (Receipt / PaymentVoucher). Strict reading of ประมวลรัษฎากร §50/§52 —
    /// payer withholds 'ณ ที่จ่าย', recipient recognises when receiving
    /// payment. Default for new tenants.</summary>
    Cash = 1,
    /// <summary>Recognize WHT-Asset / WHT-Payable at the moment of invoice
    /// approval (Invoice / PurchaseInvoice). Common practice in many Thai
    /// SMBs; the auditor accepts it. Stays as an opt-in for tenants that
    /// already book this way in their existing GL.</summary>
    Accrual = 2,
}

// ─────────────────────────────────────────────────────────────────────────
//  AI integration enums — keep numeric values STABLE because they're
//  persisted directly in AiSuggestionFeedback.ProviderUsed and the daily
//  rollup table. Append-only when adding a new provider.
// ─────────────────────────────────────────────────────────────────────────

public enum AiProviderType
{
    /// <summary>DeepSeek chat API (https://api.deepseek.com/v1/chat/completions).
    /// First and default provider — pricing ~10× cheaper than GPT-4o-mini
    /// for similar quality on accounting-domain prompts.</summary>
    DeepSeek = 1,

    /// <summary>OpenAI chat completion (https://api.openai.com/v1).
    /// Standby slot for tenants that already have an OpenAI agreement.</summary>
    OpenAi = 2,

    /// <summary>Anthropic Messages API. Different request shape than
    /// OpenAI-compatible providers — the AnthropicProvider implementation
    /// translates the canonical AiRequest into the Anthropic schema.</summary>
    Anthropic = 3,

    /// <summary>Google Gemini. OpenAI-compat endpoint reserved for parity.</summary>
    Gemini = 4,

    /// <summary>Locally hosted llama.cpp / Ollama instance. Same OpenAI-
    /// compat schema as the cloud providers; switches the orchestrator into
    /// "always-on, no budget" mode.</summary>
    LocalLlama = 5,

    /// <summary>Custom OpenAI-compatible endpoint (Azure OpenAI, proxy,
    /// gateway). Uses the OpenAI request shape; admin sets Endpoint + Model
    /// freely.</summary>
    Custom = 99,
}

/// <summary>
/// Stable identifier for each call site — every AI integration point
/// declares one and uses it for AiSuggestionFeedback.FeatureKey,
/// AiResponseCache.FeatureKey, AiUsageDaily.FeatureKey, and LocalModelHealth.
/// New features append to the bottom without breaking existing rows.
/// </summary>
public enum AiFeatureKey
{
    /// <summary>OCR vendor name → existing Contact id matching.</summary>
    VendorCanonicalization = 1,

    /// <summary>Suggest which GL account a line item should debit.</summary>
    GlAccountSuggestion = 2,

    /// <summary>Receipt vs TaxInvoice vs DeliveryNote classification.</summary>
    DocumentTypeClassification = 3,

    /// <summary>Buyer / Seller role inference from headers.</summary>
    DocumentRoleInference = 4,

    /// <summary>WHT category (revenue code 50, 50bis, 53) from vendor
    /// industry + line description.</summary>
    WhtCategoryInference = 5,

    /// <summary>Parse table region into structured line items when regex
    /// fails on irregular formatting.</summary>
    LineItemStructuredParse = 6,

    /// <summary>For each approval warning, propose a concrete fix.</summary>
    ApprovalWarningFixSuggestion = 7,

    /// <summary>Match a bank statement line → outstanding invoice(s).</summary>
    BankStatementMatch = 8,

    /// <summary>Credit note reason classification (Return / Discount /
    /// Adjustment / Writeoff).</summary>
    CreditNoteReasonClassification = 9,

    /// <summary>Fuzzy duplicate document detection.</summary>
    FuzzyDuplicateDetection = 10,

    /// <summary>Explain why an anomaly was flagged + suggest action.</summary>
    AnomalyExplanation = 11,

    /// <summary>Narrate a cashflow forecast + suggest scenarios.</summary>
    ForecastNarrative = 12,

    /// <summary>Match a free-text product name → existing Product.</summary>
    ProductMatch = 13,

    /// <summary>Match a free-text contact name → existing Contact.</summary>
    ContactMatch = 14,

    /// <summary>Suggest payment method given vendor history + amount.</summary>
    PaymentMethodSuggestion = 15,

    /// <summary>Suggest currency + FX rate sanity-check.</summary>
    CurrencyAndFxSuggestion = 16,

    /// <summary>Aging-receivable explanation per customer.</summary>
    AgingExplanation = 17,

    /// <summary>Tax filing pre-check narrative (PND.3 / PND.53 / PP.30).</summary>
    TaxFilingPreCheck = 18,

    /// <summary>Stock movement validation + suggested action when
    /// quantity-on-hand would go negative.</summary>
    StockMovementValidation = 19,

    /// <summary>Suggest debit / credit split when creating a Payment
    /// Voucher from a TaxInvoice (the "ใบสำคัญจ่าย" workflow the
    /// user called out explicitly).</summary>
    PaymentVoucherAccountingSuggestion = 20,

    /// <summary>Suggest JournalEntry lines when user is composing a
    /// freeform manual JE.</summary>
    ManualJournalSuggestion = 21,

    /// <summary>OCR Tier-4 comprehensive review — distinct from the
    /// simpler DocumentTypeClassification because this returns
    /// corrections for every field, not just the doc-type label.
    /// Tracked separately in LocalModelHealth so accuracy of the
    /// review can be measured independently.</summary>
    OcrFullReview = 22,

    /// <summary>"What target doc should I create from this scanned
    /// source?" — outputs a list of viable targets + prefill strategy.
    /// Separate from DocumentTypeClassification (which just labels the
    /// scanned paper) so the conversion-suggestion accuracy can be
    /// tracked separately.</summary>
    DocumentConversionSuggestion = 23,

    /// <summary>Per-SKU demand forecast + reorder point recommendation.
    /// Local: Croston / Holt-Winters per product. AI: narrative
    /// + scenario suggestions.</summary>
    ReorderForecast = 24,

    /// <summary>Whole-month bank reconciliation in ONE call. Bundles
    /// every unmatched bank txn + every open AR/AP/JE/Payment + the
    /// company + bank-account context and asks AI to produce a complete
    /// match plan, surface unmatched lines with explicit "missing data"
    /// reasons, and detect cross-line patterns (split payments, lumped
    /// settlements) the per-txn flow misses. Distinct from
    /// BankStatementMatch because the input shape + output shape are
    /// batch-orientated; accuracy tracked separately.</summary>
    BulkBankStatementMatch = 25,

    /// <summary>CSV/Excel column-header normalization for files from
    /// other systems (PEAK/Express/FlowAccount + arbitrary client
    /// spreadsheets). Fires only when the heuristic AnalyzeColumnMatch
    /// confidence is &lt; 0.7 on at least one column — keeps happy-path
    /// imports AI-free. AI sees source headers + 5 sample values per
    /// column + the entity's template field catalogue and re-ranks
    /// suggestions, especially for Thai abbreviations / vendor-specific
    /// conventions the alias table misses.</summary>
    ImportColumnMatch = 26,

    /// <summary>One-shot per-import data review: type-coercion
    /// normalizations (Buddhist year → western, "1.234,50" → 1234.50,
    /// "ใช่/Y/✓" → true), fuzzy-duplicate suggestions beyond exact-key
    /// match ("บจก. ABC" vs "บริษัท เอบีซี จำกัด"), per-row quality
    /// flags ("price 10× supplier average"), semantic field validation
    /// (TaxId checksum, email domain plausibility), and batch-level
    /// pattern detection ("appears to be a price list" vs "regional
    /// customer dump"). One AI call per import session covers all five
    /// concerns since they need the same input context (sample rows +
    /// target schema + a slice of existing entities). Surfaced to the
    /// operator BEFORE the conflict step so issues are caught while
    /// the data is still mutable.</summary>
    ImportDataReview = 27,

    /// <summary>Suggest the settlement basis (Cash จ่ายทันที vs Credit
    /// เครดิต) for a Payment Voucher, from the supplier's own history +
    /// open payables + agreed credit terms. Local model learns
    /// per-supplier from confirmed choices; the heuristic cold-starts it
    /// so it's useful from day one.</summary>
    PaymentTypeSuggestion = 28,

    /// <summary>Match an OCR'd invoice line → the originating project from
    /// the external-system metadata uploaded alongside the file, so cost is
    /// allocated to the right job automatically. Deterministic matcher runs
    /// first; AI only adjudicates lines that span ≥2 candidate projects.</summary>
    OcrProjectMatch = 29,

    /// <summary>VAT type per document line: 7%, 0% (export / international
    /// services), or Exempt (ยกเว้น — food, medicine, books per VAT-act §81).
    /// Heuristic: line description keywords + vendor type (domestic /
    /// foreign) cold-starts the picker; user override trains
    /// OcrCategoryMapping per (vendor + keyword) → vat-rate so future
    /// invoices auto-fill the same value. Per-line, not per-document.</summary>
    VatTypeInference = 30,

    /// <summary>Credit-term days (Net 7/15/30/60/90) auto-suggested when
    /// the operator picks a contact on a new document. Resolution
    /// priority: Contact.CreditDays (explicit per-contact setting) →
    /// OcrVendorIntelligence.TypicalPaymentTermsDays (learned from doc
    /// history) → CompanySettings.DefaultPaymentDueDays. Pure lookup,
    /// no ML required for day one — but logged through the orchestrator
    /// so per-vendor accuracy is tracked + drift detected.</summary>
    PaymentTermsSuggestion = 31,

    /// <summary>Bank account / payment channel suggestion for a new
    /// PaymentVoucher — picks the bank account this supplier is most
    /// commonly paid from based on the last 12 PVs to them. Falls
    /// back to (a) the bank's LinkedAccount when a previous PV
    /// touched a specific bank, or (b) the company's first active
    /// bank account, or (c) generic cash. Helps avoid the operator
    /// digging through the dropdown when a supplier always settles
    /// from one channel.</summary>
    PaymentChannelSuggestion = 32,

    /// <summary>Project allocation per document line — suggests the
    /// project this line should be tagged with, based on (a) which
    /// project the supplier was most recently linked to within the
    /// same fiscal period, and (b) project-vendor history (which
    /// project usually buys from this supplier). Cold-starts from
    /// the active-project picker; ML version eventually consumes
    /// the OcrProjectMatch feedback corpus.</summary>
    ProjectAllocationSuggestion = 33,

    /// <summary>Fuzzy contact match — when the operator types a name
    /// on a new document or contact form, surface the top 5 existing
    /// contacts that match by name + tax-id. Uses the existing
    /// SimilarityIndex / CharNgramSimilarity helpers. Avoids duplicate
    /// contact creation (the #1 source of long-term data hygiene
    /// problems in SMB accounting).</summary>
    ContactFuzzyMatch = 34,

    /// <summary>Manual Journal Entry account suggestion — for each
    /// JE line, AI proposes the Dr/Cr account + amount split based on
    /// (a) the JE description / memo, (b) historical similar JEs
    /// (same type + similar memo), (c) the company's chart of accounts
    /// shape. Power-user feature; reuses the GlAccountDistillation
    /// pattern from OCR. Per-line suggestion (not per-JE) so a single
    /// JE can mix categories.</summary>
    ManualJeAccountSuggestion = 35,

    /// <summary>Cost center / Branch dimension allocation per document
    /// line. Resolves the AccountingDimension this supplier is usually
    /// charged to (Mode of DocumentLine.DimensionId across recent
    /// docs). Mirrors the Project-allocation flow but for
    /// org / cost-centre dimensions instead of project dimensions.</summary>
    DimensionAllocationSuggestion = 36,

    /// <summary>Asset category + useful-life-months + depreciation
    /// method suggestion when an operator creates a FixedAsset. Pure
    /// keyword heuristic over the asset name + cost (e.g. "รถยนต์" →
    /// Vehicles, 60mo, StraightLine; "Computer" → IT Equipment, 36mo).
    /// Aligned with the Thai Revenue-Department-accepted standard
    /// useful-life table.</summary>
    AssetCategorySuggestion = 37,

    /// <summary>Payroll component → §40 income-type code mapping —
    /// salary/wage → 40(1), service-fee → 40(2), royalty → 40(3),
    /// interest → 40(4), rental → 40(5), professional → 40(6),
    /// contractor → 40(7), business → 40(8). Drives the ภ.ง.ด.1
    /// per-employee withholding cert categorisation. Pure keyword
    /// heuristic over PayrollItem.Name on creation.</summary>
    PayrollIncomeTypeSuggestion = 38,

    /// <summary>FX rate auto-fill on multi-currency document creation —
    /// returns the latest CurrencyRate.MidRate for (from, THB) closest
    /// to the document date. Falls back to today's rate. Pure lookup;
    /// confidence reflects how stale the latest rate is (1.00 same day
    /// → 0.50 a week old). Never calls cloud AI.</summary>
    FxRateSuggestion = 39,

    /// <summary>Price drift detection per (product, vendor) — when an
    /// operator enters a unit price that differs from the recent
    /// 12-document average by more than ±20%, flag a warning. Pure
    /// statistics (mean + std-dev over DocumentLine.UnitPrice history),
    /// no cloud call. Catches typos (1500 vs 15000) + supplier price
    /// changes worth a second look before approval.</summary>
    PriceDriftDetection = 40,

    /// <summary>Document memo / description auto-generate from doc-type
    /// + contact + lines. Pure-template renderer for the common case
    /// "ขาย <product> ให้ <customer> งวด <month/year>" — saves typing
    /// the same memo template repeatedly. AI cloud is invoked only
    /// when ≥3 distinct product categories on the doc (template
    /// fallback fails).</summary>
    DocumentMemoGeneration = 41,

    /// <summary>Credit-limit suggestion for a new customer Contact —
    /// statistical: median + p75 of the company's existing customers'
    /// peak AR balance. Cold-starts to 50,000 THB (SMB Thai default).
    /// Helps avoid both under-limit (lost sales) and over-limit (bad
    /// debt) on day-one customer setup.</summary>
    CreditLimitSuggestion = 42,

    /// <summary>Product category tagging when creating a new Product —
    /// keyword heuristic over the product name + description. Maps to
    /// any existing ProductCategory the company has, or surfaces the
    /// closest standard Thai SMB category ("เสื้อผ้า/อาหาร/วัสดุ/
    /// บริการ/อิเล็กทรอนิกส์" + more). Saves the operator from
    /// scrolling the category dropdown.</summary>
    ProductCategoryTagging = 43,

    /// <summary>Bad-debt risk score per customer — combines (a) max
    /// days-overdue across open invoices, (b) overdue/total invoice
    /// ratio, (c) historical write-offs. Returns 0-100 score + Low /
    /// Medium / High classification. Used in the contact list and
    /// in invoice-creation review to flag risky customers BEFORE
    /// extending more credit.</summary>
    BadDebtRiskDetection = 44,

    /// <summary>Discount % suggestion when creating a Quotation /
    /// Invoice — based on (a) the contact's lifetime sales value
    /// (volume customer), (b) repeat-customer count, (c) the
    /// company-wide median discount given. Heuristic only; surfaces
    /// "give 5% — repeat customer with ฿2M lifetime" type guidance.</summary>
    DiscountSuggestion = 45,

    /// <summary>Approver routing suggestion — when an operator submits
    /// a document for approval, pick the approver who most often
    /// approved similar (doc type + amount band) documents in the past
    /// 90 days. Falls back to ApprovalRule + DirectManager when no
    /// learned signal. Reduces "which manager handles this?" indecision
    /// in larger companies with multiple approvers.</summary>
    ApprovalRoutingSuggestion = 46,

    /// <summary>Inventory reorder point per product — calculates
    /// minimum stock level from (a) average daily consumption over
    /// last 90 days, (b) typical lead time (configurable, defaults
    /// to 14 days), (c) safety stock buffer (default 1.5× lead-time
    /// demand). Lets the operator avoid stockouts without manually
    /// computing min levels for every SKU.</summary>
    InventoryReorderPointSuggestion = 47,

    /// <summary>Period-close anomaly detection — scans the period
    /// being closed for: (a) Draft JEs still open, (b) Document.
    /// Approved without posted JE, (c) trial-balance not zero,
    /// (d) missing monthly depreciation, (e) AR/AP aging not
    /// reconciled to GL. Returns a checklist of issues + suggested
    /// fix. Pure rule scan; AI cloud not used.</summary>
    PeriodCloseAnomalyCheck = 48,

    /// <summary>Dead-stock detection — surfaces products with positive
    /// stock balance but no OUT movement in N days (default 90). Lets
    /// the operator clear slow-moving SKUs before they tie up working
    /// capital. Pure statistics; runs over StockMovement history.</summary>
    DeadStockDetection = 49,

    /// <summary>Book-tax difference detection — scans approved
    /// expense documents in the period for line descriptions matching
    /// non-deductible categories per §65ตรี (รับรอง / น้ำมันรถส่วนตัว /
    /// ค่าปรับ / เงินบริจาคเกิน) and calculates the tax-adjustment
    /// addback. Used to feed the corporate income-tax filing.</summary>
    BookTaxDifferenceDetection = 50,

    /// <summary>Customer segmentation by RFM (Recency / Frequency /
    /// Monetary value) — scores each customer 1-5 on each axis and
    /// classifies into actionable segments: Champion / Loyal /
    /// AtRisk / Lost / NewCustomer. Drives targeted marketing +
    /// credit-limit reviews. Pure stats; no AI cloud.</summary>
    CustomerRfmSegmentation = 51,

    /// <summary>Inventory ABC analysis — Pareto classification of
    /// SKUs by revenue contribution. A-class (top 70-80% revenue) =
    /// stockout watch. B-class (15-20%) = normal review.
    /// C-class (5-10%) = candidate for SKU rationalisation. Helps
    /// SMB owners focus inventory management effort.</summary>
    InventoryAbcAnalysis = 52,

    /// <summary>Generic GL-account-slot suggestion — fills any
    /// chart-of-accounts dropdown (contact AR/AP/GR, fixed-asset
    /// asset/dep/accum, department/budget/petty-cash expense) from
    /// (a) per-tenant learned memory keyed by slot+context, then
    /// (b) the most-used account for that slot in history, then
    /// (c) the standard code-prefix default. One feature key serves
    /// every slot so all COA pickers learn through the same loop.</summary>
    GlAccountSlotSuggestion = 53,

    /// <summary>Catch-all for ad-hoc admin queries.</summary>
    AdHocAnalysis = 99,
}

public enum AiCallStatus
{
    Success = 1,
    /// <summary>Served from AiResponseCache — no provider call.</summary>
    Cached = 2,
    Failed = 3,
    /// <summary>Skipped because feature is disabled or tenant opted out.</summary>
    Skipped = 4,
    /// <summary>Refused by AiBudgetGuard (daily or monthly cap).</summary>
    BudgetExceeded = 5,
    /// <summary>Refused because no provider is configured / enabled.</summary>
    NoProvider = 6,
    /// <summary>Provider returned a response that failed schema validation
    /// or Thai-compliance double-check.</summary>
    InvalidResponse = 7,
}

public enum LocalModelHealthStatus
{
    Healthy = 1,
    /// <summary>Accuracy trending down — admin should look at training data.</summary>
    Degraded = 2,
    /// <summary>Local model consistently loses to AI; recommend redesign
    /// or always-on AI for this feature.</summary>
    NeedsRedesign = 3,
    /// <summary>Insufficient samples to evaluate.</summary>
    InsufficientData = 4,
}

/// <summary>
/// Per-feature routing mode set by admin in /admin/ai-models.html.
/// Drives AiOrchestrator.AskInternalAsync Step 0 — pick local, pick
/// provider, or mix them.
/// </summary>
public enum AiFeatureRoutingMode
{
    /// <summary>Feature off — orchestrator returns the local-fallback
    /// answer (or a Skipped status if no local was supplied). No
    /// provider tokens spent, no learning happens.</summary>
    Disabled = 0,

    /// <summary>Local distilled model answers; provider is never called.
    /// Use once the student plateaus at user-acceptable accuracy and
    /// the cost saving outweighs occasional drift.</summary>
    LocalOnly = 1,

    /// <summary>Always call the provider; ignore any local prediction.
    /// The default for features without a local student yet, or where
    /// the admin wants pure-AI behaviour while debugging.</summary>
    ProviderOnly = 2,

    /// <summary>Default smart routing: local short-circuit when ≥
    /// threshold; provider sampled at ProviderSamplingRate for drift
    /// calibration. Cheapest balanced mode.</summary>
    Hybrid = 3,

    /// <summary>"Teach me" mode — every call hits the provider AND
    /// the local prediction is recorded head-to-head for training.
    /// Used to grow the corpus quickly for an immature student.
    /// Costs as much as ProviderOnly but generates the maximum
    /// supervision signal per call.</summary>
    AlwaysTeach = 4,
}
