namespace Accounting.Models.Enums;

// ==================== User & Role ====================
public enum UserRole
{
    Owner = 1,
    Accountant = 2,
    Staff = 3,
    Auditor = 4,
    SystemAdmin = 99
}

public enum UserStatus
{
    Active = 1,
    Inactive = 2,
    Suspended = 3
}

// ==================== Company ====================
public enum CompanyStatus
{
    Active = 1,
    Inactive = 2,
    Suspended = 3
}

public enum BusinessType
{
    Individual = 1,        // บุคคลธรรมดา
    JuristicPerson = 2,   // นิติบุคคล
    Partnership = 3,       // ห้างหุ้นส่วน
    PublicCompany = 4,     // บริษัทมหาชน
    Other = 99
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
    Voided = 2
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
    WithholdingTax53 = 3  // ภงด.53
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
    Quotation = 1,        // ใบเสนอราคา
    Invoice = 2,          // ใบแจ้งหนี้
    Receipt = 3,          // ใบเสร็จรับเงิน
    TaxInvoice = 4,       // ใบกำกับภาษี
    DebitNote = 5,        // ใบเพิ่มหนี้
    CreditNote = 6        // ใบลดหนี้
}

public enum DocumentStatus
{
    Draft = 0,
    Approved = 1,
    Sent = 2,
    PartiallyPaid = 3,
    Paid = 4,
    Voided = 5,
    Overdue = 6
}

public enum PaymentMethod
{
    Cash = 1,
    BankTransfer = 2,
    CreditCard = 3,
    Cheque = 4,
    PromptPay = 5,
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
    Print = 7
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

    // Preset combos
    TrialFeatures = BasicAccounting | DocumentEngine | TaxManagement,
    BasicFeatures = BasicAccounting | DocumentEngine | TaxManagement | AuditLog | EmailNotification,
    ProFeatures = BasicFeatures | AdvancedReporting | MultiCompany | APIAccess | BulkImport | CustomChartOfAccounts | AutoPosting | MultiUser,
    EnterpriseFeatures = ProFeatures | EtaxInvoice | WorkflowEngine | BankReconciliation
}
