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

    /// <summary>PDPA ม.26 / ม.37 — เปิดดู PII (CitizenId, Passport, Phone, Email)
    /// แบบ raw (ไม่ mask). default ทุก field mask ตาม PiiMask helper.
    /// allow list = HR_Admin + Owner เท่านั้น. ทุกครั้งที่ field ถูกอ่านแบบ raw
    /// ต้อง log ลง PiiAccessLog (audit retention 1 ปี ตาม ม.37(4)).</summary>
    public const string PiiView         = P + "Pii.View";

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

    // ───── Lodging (ธุรกิจที่พัก) ─────
    // Manage = front desk: จอง/ยืนยัน/เช็คอิน-เอาต์/folio/แม่บ้าน · Settings = ตั้งค่าที่พัก/ห้อง/ราคา/นโยบาย
    public const string LodgingManage     = P + "Lodging.Manage";
    public const string LodgingSettings   = P + "Lodging.Settings";

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

    // ───── e-Tax Invoice (ETDA ขมธอ.3-2560) ─────
    // ⚠️ เดิม `EtaxController` มีแค่ `[Authorize]` ระดับคลาส = "ล็อกอินอยู่ไหม"
    // ⇒ สมาชิกคนไหนก็ **ยื่นเอกสารต่อกรมสรรพากรแทนบริษัทได้** และการยื่นที่ RD
    // ตอบรับแล้ว **ย้อนกลับไม่ได้** (โค้ดเองเขียนไว้ใน VoidAsync) — ผลตรวจรอบ 147
    public const string EtaxIssue  = P + "Etax.Issue";
    public const string EtaxSubmit = P + "Etax.Submit";
    public const string EtaxVoid   = P + "Etax.Void";

    // ───── สินทรัพย์ถาวร ─────
    // ⚠️ เดิม `FixedAssetController` มีแค่ `[Authorize]` และ **ไม่มีคีย์สิทธิ์
    // สินทรัพย์เลยสักตัวในไฟล์นี้** ⇒ ต่อให้อยากเช็คก็ยังไม่มีคีย์ให้เช็ค
    // ⇒ สมาชิกคนไหนก็โพสต์ JE ค่าเสื่อม · จำหน่าย · ตัดจำหน่าย · ตีราคาใหม่ได้
    // (ผลตรวจทีม E · E-04) · ระดับสิทธิ์เลือกจาก **ผลกระทบ** ไม่ใช่ HTTP verb:
    // แก้ทะเบียนเฉย ๆ = Manage · ขยับ GL = Depreciate/Dispose
    public const string AssetManage     = P + "Asset.Manage";
    public const string AssetDepreciate = P + "Asset.Depreciate";
    public const string AssetDispose    = P + "Asset.Dispose";

    // ───── Banking / Reconciliation ─────
    public const string BankView          = P + "Bank.View";
    public const string BankReconcile     = P + "Bank.Reconcile";
    public const string BankPaymentInit   = P + "Bank.PaymentInit";   // initiate outgoing transfer

    // ───── Reporting ─────
    public const string ReportsDashboard  = P + "Reports.Dashboard";   // หน้าแดชบอร์ดภาพรวม + executive-reports + fpa + financial-mgmt
    public const string ReportsExecutive  = P + "Reports.Executive";   // executive / FP&A
    public const string ReportsFinancial  = P + "Reports.Financial";   // P&L, Balance Sheet
    public const string ReportsOperational = P + "Reports.Operational"; // aging, AR/AP
    public const string ReportsExport     = P + "Reports.Export";

    // ───── Tax / RD filing ─────
    public const string TaxFile           = P + "Tax.File";            // submit PND / PP30
    public const string TaxExport         = P + "Tax.Export";          // export e-file

    // ───── Journal / GL manipulation ─────
    /// <summary>สร้าง/post/แก้/ย้าย(reclassify)/void/ลบ/reverse ใบสำคัญ (JE)
    /// แบบ manual ผ่านสมุดรายวัน — การแก้ GL ตรง ๆ ที่กระทบงบ ควรจำกัดเฉพาะ
    /// ผู้ทำบัญชี (Owner/Accountant auto-pass; role อื่นต้องได้รับสิทธิ์นี้).</summary>
    public const string JournalManage     = P + "Journal.Manage";

    // ───── Contact / Master data ─────
    public const string ContactEdit       = P + "Contact.Edit";
    public const string ProductEdit       = P + "Product.Edit";
    public const string ChartOfAccountsEdit = P + "ChartOfAccounts.Edit";

    // ───── System / Settings ─────
    public const string CompanySettingsEdit = P + "CompanySettings.Edit";

    /// <summary>เปิด/ปิดส่วนเสริมที่มีค่าใช้จ่าย · ซื้อโควตาเพิ่ม · ส่งหลักฐานชำระเงิน
    ///
    /// <para><b>การกดเปิด add-on คือการก่อหนี้ให้บริษัท</b> — เดิม
    /// <c>MeteringController</c> มีแค่ <c>[Authorize]</c> ระดับคลาส ⇒ สมาชิกคนไหน
    /// ก็เปิดฟีเจอร์รายเดือน/ซื้อโควตาแทนบริษัทได้ (defect class เดียวกับที่เคย
    /// แก้ไปแล้วใน <c>DocumentController</c>/<c>PayrollController</c>:
    /// "[Authorize] ระดับคลาส = ล็อกอินอยู่ไหม ไม่ใช่ มีสิทธิ์ทำสิ่งนี้ไหม")</para>
    ///
    /// <para>Owner/SystemAdmin ผ่านอัตโนมัติที่ <c>PermissionService</c> ⇒ คนที่
    /// เปิดบริษัทเองไม่กระทบ · กระทบเฉพาะสมาชิกที่ควรต้องได้รับสิทธิ์ก่อน</para></summary>
    public const string BillingManage     = P + "Billing.Manage";
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

        // Lodging / ที่พัก
        new(LodgingManage,    "ที่พัก",   "จัดการการจองที่พัก (front desk)", "จอง · ยืนยันมัดจำ · เช็คอิน/เอาต์ · folio · แม่บ้าน"),
        new(LodgingSettings,  "ที่พัก",   "ตั้งค่าที่พัก",             "ห้อง · ราคา/ฤดูกาล · นโยบายยกเลิก · บริการเสริม"),

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

        // e-Tax Invoice
        new(EtaxIssue,  "e-Tax", "สร้าง/ลงนาม e-Tax Invoice",   "generate XML · ลงนามดิจิทัล · ออก PDF/A-3"),
        new(EtaxSubmit, "e-Tax", "นำส่ง e-Tax ต่อกรมสรรพากร",   "ย้อนกลับไม่ได้เมื่อ RD ตอบรับแล้ว · รวมส่งทางอีเมล"),
        new(EtaxVoid,   "e-Tax", "ยกเลิก e-Tax",                "ทำได้ก่อน RD ตอบรับเท่านั้น"),

        // สินทรัพย์ถาวร
        new(AssetManage,     "สินทรัพย์", "จัดการทะเบียนสินทรัพย์", "ขึ้นทะเบียน · แก้ไข · ลบ · นำเข้า · ทบทวนอายุใช้งาน"),
        new(AssetDepreciate, "สินทรัพย์", "โพสต์ค่าเสื่อมราคา",     "ลง JE ค่าเสื่อมประจำงวด (ย้อนกลับต้องกลับรายการ)"),
        new(AssetDispose,    "สินทรัพย์", "จำหน่าย/ตัดจำหน่าย/ตีราคาใหม่", "ลง JE กำไร-ขาดทุนจากการจำหน่าย"),

        // Banking
        new(BankView,         "ธนาคาร",  "ดูบัญชีธนาคาร",            "ยอดคงเหลือ · transactions"),
        new(BankReconcile,    "ธนาคาร",  "กระทบยอดบัญชี",            "match statement กับเอกสาร"),
        new(BankPaymentInit,  "ธนาคาร",  "สั่งโอนเงิน",              "initiate transfer (open banking)"),

        // Reports
        new(ReportsDashboard,  "รายงาน","ดูแดชบอร์ดภาพรวม",         "หน้าแรก · financial overview · FP&A · executive reports"),
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
        new(JournalManage,    "บัญชี",   "จัดการใบสำคัญ (JE)",       "สร้าง/post/ย้าย/void/ลบ/reverse ใบสำคัญ manual"),

        // System
        new(SensitiveDocsView, "ระบบ",   "ดูเอกสารลับ",              "manager bonus · exec expense"),
        new(AccountingView,   "ระบบ",    "ดูข้อมูลบัญชี",            "GL · journal browser"),
        new(CompanySettingsEdit, "ระบบ", "ตั้งค่าบริษัท",            "logo · template · default GL"),
        new(BillingManage,    "ระบบ",    "จัดการค่าใช้จ่าย/ส่วนเสริม", "เปิด-ปิด add-on · ซื้อโควตา · ส่งหลักฐานชำระเงิน"),
        new(UsersManage,      "ระบบ",    "จัดการผู้ใช้",             "invite · deactivate"),
        new(RolesManage,      "ระบบ",    "จัดการ Role + สิทธิ์",     "กำหนด company role + ติ๊กสิทธิ์"),
    };

    /// <summary>True when the key looks like a permission key (i.e. it
    /// was meant for permission gating, not menu access). Used so the
    /// permission lookup ignores legacy menu rows in the same table.</summary>
    public static bool IsPermissionKey(string? key) => key != null && key.StartsWith(P);

    /// <summary>ชื่อไทยของสิทธิ์จาก <see cref="Catalog"/> (ไม่พบ = ชื่อคีย์ไม่มี prefix)</summary>
    public static string LabelOf(string key)
        => Catalog.FirstOrDefault(m => m.Key == key)?.LabelTh ?? key.Replace(P, "");

    /// <summary>ข้อความ 403 ตัวเดียวของด่านสิทธิ์ — บอกว่าขาดสิทธิ์อะไร และ<b>ขอจากใคร ที่ไหน</b> (ฝ่ายค้านรอบ 193 W-C7 ·
    /// F2 ข้อ 8 "ทางไปต่อของผู้ใช้") · ใช้ทั้ง <c>RequirePermissionAttribute</c> และหน้าตั้งค่าที่ตรวจสิทธิ์ก่อนให้กรอก</summary>
    public static string DeniedMessage(string key)
        => $"ไม่มีสิทธิ์ \u201c{LabelOf(key)}\u201d ({key.Replace(P, "")}) — ขอให้เจ้าของบริษัทเปิดสิทธิ์นี้ให้บทบาทของคุณ"
           + " ที่หน้า \u201cบทบาทและสิทธิ์\u201d (/pages/roles.html)";
}
