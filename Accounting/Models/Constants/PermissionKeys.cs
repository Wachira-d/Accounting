namespace Accounting.Models.Constants;

/// <summary>
/// Granular permission keys stored in <see cref="Entities.CompanyRolePermission.MenuItemId"/>.
/// All permission keys carry the "perm:" prefix so they coexist with the
/// menu-access IDs the same column already stores. Used by services to
/// gate sensitive actions (e.g. approving a leave) when the standard
/// direct-manager check is insufficient — an admin can grant the
/// matching permission to any company role and that role's holders gain
/// the right to perform the action.
/// </summary>
public static class PermissionKeys
{
    private const string P = "perm:";

    // Broad recipient-resolution keys — consumed by NotificationEngine
    // to find HR / Accounting users when a notification is configured
    // for those logical roles.
    public const string HrAdmin        = P + "HR.Admin";
    public const string AccountingView = P + "Accounting.View";

    // HR
    public const string LeaveApprove   = P + "Leave.Approve";
    public const string LeaveReject    = P + "Leave.Reject";
    public const string AdvanceApprove = P + "Advance.Approve";
    public const string AdvanceReject  = P + "Advance.Reject";
    public const string AdvanceDisburse = P + "Advance.Disburse";

    // Department / Position management
    public const string OrganizationManage = P + "Organization.Manage";

    // Payroll
    public const string PayrollRun      = P + "Payroll.Run";
    public const string PayrollApprove  = P + "Payroll.Approve";
    public const string PayrollPay      = P + "Payroll.Pay";
    /// <summary>Read payroll runs / payslips / payroll-related GL entries.
    /// Without this permission the API returns redacted stubs so integration
    /// targets know the records exist but are hidden.</summary>
    public const string PayrollView     = P + "Payroll.View";

    // Sensitive documents (manager bonus vouchers, executive expense reports, etc.)
    // Owner-configurable per company — default allow list = Owner + Accountant.
    public const string SensitiveDocsView = P + "SensitiveDocs.View";

    // Expense claims
    public const string ExpenseApprove  = P + "Expense.Approve";
    public const string ExpenseReject   = P + "Expense.Reject";
    public const string ExpensePay      = P + "Expense.Pay";

    // ───── POS (Point of Sale) — sub-roles ─────
    // Distinct from the DocumentEngine flag: a Cashier can ring up sales
    // but can't issue refunds or close the day. POS sub-roles let a shop
    // give a junior employee a cash-register login without revealing the
    // accounting back-office.
    public const string PosCashier        = P + "POS.Cashier";        // ring sales + receipts
    public const string PosRefund         = P + "POS.Refund";         // process refunds / voids
    public const string PosCloseDay       = P + "POS.CloseDay";       // X / Z report; reconcile till
    public const string PosManager        = P + "POS.Manager";        // configure menus, items, modifiers
    public const string PosReportsView    = P + "POS.ReportsView";    // sales summary by period / by cashier

    // ───── Inventory / Warehouse ─────
    public const string InventoryView      = P + "Inventory.View";       // see stock levels
    public const string InventoryAdjust    = P + "Inventory.Adjust";     // manual count adjustment
    public const string InventoryTransfer  = P + "Inventory.Transfer";   // move between warehouses
    public const string InventoryReceive   = P + "Inventory.Receive";    // PO receipt / GR
    public const string InventoryIssue     = P + "Inventory.Issue";      // stock-out to production / waste
    public const string InventoryCostEdit  = P + "Inventory.CostEdit";   // edit unit cost / revaluation

    // ───── CMS / Website ─────
    // For tenants using the CMS feature to run a customer-facing site.
    // PageEdit lets a marketing intern update content; Publish is the
    // gate for pushing changes live; LeadView for the sales team; etc.
    public const string CmsPageEdit       = P + "CMS.PageEdit";
    public const string CmsPagePublish    = P + "CMS.PagePublish";
    public const string CmsLeadView       = P + "CMS.LeadView";
    public const string CmsLeadAssign     = P + "CMS.LeadAssign";
    public const string CmsOrderManage    = P + "CMS.OrderManage";
    public const string CmsSiteSettings   = P + "CMS.SiteSettings";

    // ───── Documents ─────
    // Blanket keys — historical, still honoured. A user holding any blanket
    // key bypasses the per-direction split below (so existing role templates
    // and deployments continue to work without re-grant).
    public const string DocumentCreate    = P + "Document.Create";
    public const string DocumentApprove   = P + "Document.Approve";
    public const string DocumentVoid      = P + "Document.Void";
    public const string DocumentViewAll   = P + "Document.ViewAll";   // pass row-level scope, see all
    public const string DocumentExport    = P + "Document.Export";

    // Per-direction keys — split the blanket above into revenue (sales-side:
    // Invoice / TaxInvoice / Receipt / Quotation / DeliveryNote / BillingNote
    // / DebitNote / CreditNote) vs purchase (cost-side: PurchaseRequisition /
    // PurchaseOrder / GoodsReceiptNote / PurchaseInvoice / Expense /
    // PaymentVoucher / CertificateInLieu). Enables roles like "Sales Rep can
    // create invoices but not payment vouchers" and "AP Clerk can create
    // payment vouchers but not invoices". Direction is decided by
    // DocumentPermissionHelper.IsRevenue / IsPurchase.
    public const string DocumentRevenueView    = P + "Document.Revenue.View";
    public const string DocumentRevenueCreate  = P + "Document.Revenue.Create";
    public const string DocumentRevenueApprove = P + "Document.Revenue.Approve";
    public const string DocumentRevenueVoid    = P + "Document.Revenue.Void";
    public const string DocumentPurchaseView    = P + "Document.Purchase.View";
    public const string DocumentPurchaseCreate  = P + "Document.Purchase.Create";
    public const string DocumentPurchaseApprove = P + "Document.Purchase.Approve";
    public const string DocumentPurchaseVoid    = P + "Document.Purchase.Void";

    // ───── Banking / Reconciliation ─────
    public const string BankView          = P + "Bank.View";
    public const string BankReconcile     = P + "Bank.Reconcile";
    public const string BankPaymentInit   = P + "Bank.PaymentInit";   // initiate outgoing transfer

    // ───── Reporting ─────
    public const string ReportsExecutive  = P + "Reports.Executive";   // executive / FP&A
    public const string ReportsFinancial  = P + "Reports.Financial";   // P&L, Balance Sheet
    public const string ReportsOperational = P + "Reports.Operational"; // aging, AR/AP
    public const string ReportsExport     = P + "Reports.Export";

    // ───── Tax / RD filing ─────
    public const string TaxFile           = P + "Tax.File";            // submit PND / PP30
    public const string TaxExport         = P + "Tax.Export";          // export e-file

    // ───── Contact / Master data ─────
    public const string ContactEdit       = P + "Contact.Edit";
    public const string ProductEdit       = P + "Product.Edit";
    public const string ChartOfAccountsEdit = P + "ChartOfAccounts.Edit";

    // ───── System / Settings ─────
    public const string CompanySettingsEdit = P + "CompanySettings.Edit";
    public const string UsersManage         = P + "Users.Manage";       // invite, deactivate
    public const string RolesManage         = P + "Roles.Manage";       // define CompanyRole + grants

    // ───── Metadata for the role-permission picker UI ─────
    // (Category, Label, Description, RecommendedRoles) tuple so the
    // frontend can group permissions cleanly into a 2-tier checklist
    // ("📦 POS" → "Cashier · Refund · ..."). Keep in sync with the keys
    // above; consumers iterate via Catalog.
    public sealed record PermissionMeta(
        string Key, string Category, string LabelTh, string DescriptionTh);

    public static readonly IReadOnlyList<PermissionMeta> Catalog = new List<PermissionMeta>
    {
        // HR / People
        new(HrAdmin,          "HR",       "HR Admin",                "เข้าถึงทุกฟังก์ชัน HR (พนักงาน, ลา, ยกยอด)"),
        new(OrganizationManage, "HR",     "จัดการองค์กร",            "แผนก · ตำแหน่ง · org chart"),
        new(LeaveApprove,     "HR",       "อนุมัติลา",                "อนุมัติคำขอลาของพนักงาน"),
        new(LeaveReject,      "HR",       "ปฏิเสธลา",                "ปฏิเสธคำขอลา"),
        new(AdvanceApprove,   "HR",       "อนุมัติเงินทดรอง",        "อนุมัติคำขอเงินทดรอง"),
        new(AdvanceReject,    "HR",       "ปฏิเสธเงินทดรอง",         "ปฏิเสธคำขอเงินทดรอง"),
        new(AdvanceDisburse,  "HR",       "จ่ายเงินทดรอง",           "ปุ่ม 'จ่าย' หลังอนุมัติ"),
        new(ExpenseApprove,   "HR",       "อนุมัติเบิก",              "อนุมัติใบเบิกค่าใช้จ่าย"),
        new(ExpenseReject,    "HR",       "ปฏิเสธเบิก",              "ปฏิเสธใบเบิก"),
        new(ExpensePay,       "HR",       "จ่ายเบิก",                "ปุ่ม 'จ่าย' หลังอนุมัติ"),
        new(PayrollRun,       "HR",       "รัน Payroll",              "สร้าง payroll run รอบใหม่"),
        new(PayrollApprove,   "HR",       "อนุมัติ Payroll",          "อนุมัติ payroll run"),
        new(PayrollPay,       "HR",       "จ่าย Payroll",             "ปุ่มจ่ายเงินเดือนจริง"),
        new(PayrollView,      "HR",       "ดู Payroll",               "เห็น payslip + payroll history"),

        // POS
        new(PosCashier,       "POS",      "Cashier (รับเงิน)",        "ขายและออกใบเสร็จที่ POS"),
        new(PosRefund,        "POS",      "Refund / Void",            "คืนเงิน · ยกเลิกใบเสร็จ"),
        new(PosCloseDay,      "POS",      "ปิดยอดวัน (Z-Report)",     "X-Report · Z-Report · กระทบยอดเงินสด"),
        new(PosManager,       "POS",      "POS Manager",              "ตั้งค่า menu · สินค้า · modifier"),
        new(PosReportsView,   "POS",      "ดูรายงาน POS",             "สรุปยอดขายต่อรอบ/cashier"),

        // Inventory
        new(InventoryView,    "Inventory","ดูคลังสินค้า",             "ระดับสต๊อก · ราคาทุน"),
        new(InventoryAdjust,  "Inventory","ปรับสต๊อก",                "manual adjustment (cycle count)"),
        new(InventoryTransfer,"Inventory","โอนระหว่างคลัง",          "warehouse transfer"),
        new(InventoryReceive, "Inventory","รับสินค้า (GR)",           "รับ PO + GR เข้าคลัง"),
        new(InventoryIssue,   "Inventory","เบิกออก",                  "เบิกใช้ในผลิต · เสียหาย"),
        new(InventoryCostEdit,"Inventory","แก้ราคาทุน",                "edit unit cost · revaluation"),

        // CMS / Website
        new(CmsPageEdit,      "CMS",      "แก้เนื้อหาหน้าเว็บ",       "page builder"),
        new(CmsPagePublish,   "CMS",      "เผยแพร่หน้าเว็บ",         "publish / unpublish"),
        new(CmsLeadView,      "CMS",      "ดู Lead จากเว็บ",          "RFQ · ขอใบเสนอราคา"),
        new(CmsLeadAssign,    "CMS",      "กระจาย Lead",              "assign ให้ทีมขาย"),
        new(CmsOrderManage,   "CMS",      "จัดการ Order ที่ลูกค้าสั่งจากเว็บ", "shopping cart orders"),
        new(CmsSiteSettings,  "CMS",      "ตั้งค่าเว็บไซต์",          "subdomain · theme · SEO"),

        // Documents — blanket (ทำได้ทุกประเภท)
        new(DocumentCreate,   "เอกสาร",  "สร้างเอกสาร (ทุกประเภท)",   "blanket — invoice + PI + PV + etc."),
        new(DocumentApprove,  "เอกสาร",  "อนุมัติเอกสาร (ทุกประเภท)", "blanket — Draft → Approve ได้ทุกประเภท"),
        new(DocumentVoid,     "เอกสาร",  "ยกเลิกเอกสาร (ทุกประเภท)",  "blanket — void approved doc"),
        new(DocumentViewAll,  "เอกสาร",  "ดูเอกสารทุกคน",            "bypass row-level filter (เห็นของคนอื่น)"),
        new(DocumentExport,   "เอกสาร",  "ส่งออกเอกสาร",             "export Excel · PDF · CSV"),

        // Documents — split by direction (เลือกเฉพาะรายรับ/รายจ่าย)
        new(DocumentRevenueView,    "เอกสาร", "ดูเอกสารฝั่งรายรับ",      "Invoice · Tax · Receipt · Quotation"),
        new(DocumentRevenueCreate,  "เอกสาร", "สร้างเอกสารฝั่งรายรับ",   "ออก Invoice/Quotation/Receipt"),
        new(DocumentRevenueApprove, "เอกสาร", "อนุมัติเอกสารฝั่งรายรับ",  "อนุมัติเฉพาะเอกสารขาย"),
        new(DocumentRevenueVoid,    "เอกสาร", "ยกเลิกเอกสารฝั่งรายรับ",   "void เฉพาะเอกสารขาย"),
        new(DocumentPurchaseView,   "เอกสาร", "ดูเอกสารฝั่งรายจ่าย",     "PI · PV · Expense · PO"),
        new(DocumentPurchaseCreate, "เอกสาร", "สร้างเอกสารฝั่งรายจ่าย",  "ออก PI/PO/PV/Expense"),
        new(DocumentPurchaseApprove,"เอกสาร", "อนุมัติเอกสารฝั่งรายจ่าย", "อนุมัติเฉพาะเอกสารซื้อ/จ่าย"),
        new(DocumentPurchaseVoid,   "เอกสาร", "ยกเลิกเอกสารฝั่งรายจ่าย",  "void เฉพาะเอกสารซื้อ/จ่าย"),

        // Banking
        new(BankView,         "ธนาคาร",  "ดูบัญชีธนาคาร",            "ยอดคงเหลือ · transactions"),
        new(BankReconcile,    "ธนาคาร",  "กระทบยอดบัญชี",            "match statement กับเอกสาร"),
        new(BankPaymentInit,  "ธนาคาร",  "สั่งโอนเงิน",              "initiate transfer (open banking)"),

        // Reports
        new(ReportsExecutive,  "รายงาน","รายงานผู้บริหาร",          "KPI · benchmark · trend"),
        new(ReportsFinancial,  "รายงาน","งบการเงิน",                "งบดุล · งบกำไรขาดทุน · กระแสเงินสด"),
        new(ReportsOperational,"รายงาน","รายงานปฏิบัติการ",         "aging · AR/AP · stock report"),
        new(ReportsExport,     "รายงาน","ส่งออกรายงาน",             "Excel · PDF"),

        // Tax
        new(TaxFile,          "ภาษี",    "ยื่นภาษี",                  "ยืนยันยื่น ภพ.30 · ภงด.1/3/53"),
        new(TaxExport,        "ภาษี",    "ส่งออกไฟล์ภาษี",            "RD e-file · CSV"),

        // Master data
        new(ContactEdit,      "Master",  "แก้ผู้ติดต่อ",             "contact CRUD"),
        new(ProductEdit,      "Master",  "แก้สินค้า",                "product / service CRUD"),
        new(ChartOfAccountsEdit, "Master", "แก้ผังบัญชี",           "ChartOfAccounts CRUD"),

        // System
        new(SensitiveDocsView, "ระบบ",   "ดูเอกสารลับ",              "manager bonus · exec expense"),
        new(AccountingView,   "ระบบ",    "ดูข้อมูลบัญชี",            "GL · journal browser"),
        new(CompanySettingsEdit, "ระบบ", "ตั้งค่าบริษัท",            "logo · template · default GL"),
        new(UsersManage,      "ระบบ",    "จัดการผู้ใช้",             "invite · deactivate"),
        new(RolesManage,      "ระบบ",    "จัดการ Role + สิทธิ์",     "กำหนด company role + ติ๊กสิทธิ์"),
    };

    /// <summary>True when the key looks like a permission key (i.e. it
    /// was meant for permission gating, not menu access). Used so the
    /// permission lookup ignores legacy menu rows in the same table.</summary>
    public static bool IsPermissionKey(string? key) => key != null && key.StartsWith(P);
}
