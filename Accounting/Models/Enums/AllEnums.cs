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

public enum JournalEntryStatus
{
    Draft = 0,
    Posted = 1,
    Voided = 2,
    Reversed = 3
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
    PersonalIncomeTax91 = 7 // ภงด.91 (ภาษีเงินได้บุคคลธรรมดา)
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
    PurchaseInvoice = 8,      // ใบแจ้งหนี้ซื้อ
    Expense = 9,              // ใบบันทึกค่าใช้จ่าย
    PaymentVoucher = 13,      // ใบสำคัญจ่าย
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
    Excluded = 2
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

// ==================== Approval Workflow ====================
public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Recalled = 3
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
    Branch = 4,
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
    Failed = 8             // Import ล้มเหลว
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
