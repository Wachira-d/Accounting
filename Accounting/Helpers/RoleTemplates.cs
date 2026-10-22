using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>
/// บทบาทสำเร็จรูป (template) ที่เจ้าของบริษัทกดสร้างเป็นบทบาทกำหนดเองได้ในคลิกเดียว — <b>ตัวตั้งตัวเดียว</b> (รอบ 205 ทีม I ย้ายจาก
/// <c>PermissionCatalogController</c> เพื่อให้เทสต์ล็อกได้ว่าทุกคีย์ <c>perm:*</c> อยู่ในแคตตาล็อก) · รายการที่ไม่ขึ้นต้น <c>perm:</c> = id เมนูใน
/// <c>Layout.navItems</c> (layout.js) — รอบ 205 แก้ id ที่ไม่มีอยู่จริง (inventory/warehouses/chart-of-accounts/vat/cms-edit/executive-dashboard)
/// ซึ่งทำให้สมาชิกที่ได้บทบาทจากแม่แบบ (โหมดเข้มงวด) ไม่เห็นเมนูนั้นเลย
/// </summary>
public static class RoleTemplates
{
    public sealed record RoleTemplate(string Key, string Name, string Icon, string Description, List<string> Permissions);

    public static RoleTemplate? Find(string? key) => All.FirstOrDefault(t => t.Key == key);

    public static readonly IReadOnlyList<RoleTemplate> All = new List<RoleTemplate>
    {
        new("pos-cashier", "POS Cashier", "🛒",
            "พนักงานขายหน้าร้าน — รับเงิน + ออกใบเสร็จ. ไม่สามารถ refund / ปิดยอดวัน / เปลี่ยนเมนูได้",
            new() {
                PermissionKeys.PosCashier,
                // Menu access — limit to POS-related screens only.
                "pos",
            }),
        new("pos-manager", "POS Manager", "👔",
            "ดูแลรอบขาย หน้าร้าน — ปิดยอด, อนุมัติ refund, ดูรายงานยอดขาย, ตั้งค่า menu/modifier",
            new() {
                PermissionKeys.PosCashier, PermissionKeys.PosRefund,
                PermissionKeys.PosCloseDay, PermissionKeys.PosManager,
                PermissionKeys.PosReportsView,
                "pos", "pos-packages", "pos-modifiers", "pos-reports",
            }),
        new("inventory-clerk", "Inventory Clerk", "📦",
            "เจ้าหน้าที่คลัง — รับสินค้า, ปรับสต๊อก, โอนระหว่างคลัง. ไม่เห็นยอดเงิน / GL",
            new() {
                PermissionKeys.InventoryView, PermissionKeys.InventoryAdjust,
                PermissionKeys.InventoryReceive, PermissionKeys.InventoryTransfer,
                PermissionKeys.InventoryIssue,
                "warehouse", "products", "inventory-reports",
            }),
        new("web-editor", "Web Editor", "🖌️",
            "ทีมการตลาด — แก้เนื้อหาหน้าเว็บ + ดู Lead. ไม่มีสิทธิ์ publish (ต้องรอ supervisor)",
            new() {
                PermissionKeys.CmsPageEdit, PermissionKeys.CmsLeadView,
                "cms-sites", "cms-leads",
            }),
        new("web-publisher", "Web Publisher", "🌐",
            "ผู้ดูแลเว็บ — แก้เนื้อหา + publish + จัดการ order + ตั้งค่าเว็บ",
            new() {
                PermissionKeys.CmsPageEdit, PermissionKeys.CmsPagePublish,
                PermissionKeys.CmsLeadView, PermissionKeys.CmsLeadAssign,
                PermissionKeys.CmsOrderManage, PermissionKeys.CmsSiteSettings,
                "cms-sites", "cms-leads", "cms-bookings", "cms-orders",
            }),
        new("accountant", "Accountant", "🧮",
            "นักบัญชี — ทุกเอกสาร + รายงานการเงิน + ภาษี + บัญชีธนาคาร",
            new() {
                PermissionKeys.DocumentCreate, PermissionKeys.DocumentApprove,
                PermissionKeys.DocumentViewAll, PermissionKeys.DocumentExport,
                PermissionKeys.BankView, PermissionKeys.BankReconcile,
                PermissionKeys.SettlementView, PermissionKeys.SettlementImport,
                PermissionKeys.SettlementPost, PermissionKeys.SettlementChannels,
                PermissionKeys.ReportsDashboard,
                PermissionKeys.ReportsFinancial, PermissionKeys.ReportsOperational,
                PermissionKeys.ReportsExport,
                PermissionKeys.TaxFile, PermissionKeys.TaxExport,
                PermissionKeys.ContactEdit, PermissionKeys.ProductEdit,
                PermissionKeys.ChartOfAccountsEdit, PermissionKeys.AccountingView,
                "accountant", "documents", "expense-docs", "payments",
                "purchases", "bank", "settlements", "settlement-channels", "accounts", "journals",
                "general-ledger", "reports", "tax", "wht", "aging",
            }),
        new("sales-rep", "Sales Rep", "💼",
            "พนักงานขาย — ออกใบเสนอราคา/ใบแจ้งหนี้/ใบเสร็จ + ดูเฉพาะเอกสารฝั่งรายรับ ไม่เห็น PV/PI",
            new() {
                PermissionKeys.DocumentRevenueView, PermissionKeys.DocumentRevenueCreate,
                PermissionKeys.DocumentRevenueApprove, PermissionKeys.DocumentRevenueVoid,
                PermissionKeys.DocumentExport,
                PermissionKeys.ContactEdit,
                "documents", "payments",
            }),
        new("ap-clerk", "AP Clerk", "📥",
            "พนักงานบัญชีเจ้าหนี้ — ออก PI/PO/PV + ดูเฉพาะเอกสารฝั่งรายจ่าย ไม่เห็นใบกำกับฝั่งรายรับ",
            new() {
                PermissionKeys.DocumentPurchaseView, PermissionKeys.DocumentPurchaseCreate,
                PermissionKeys.DocumentPurchaseApprove, PermissionKeys.DocumentPurchaseVoid,
                PermissionKeys.DocumentExport,
                PermissionKeys.ContactEdit,
                "expense-docs", "purchases", "payments",
            }),
        new("hr-manager", "HR Manager", "👤",
            "HR — ทุกฟังก์ชันลา/payroll/เงินทดรอง + เห็นข้อมูลพนักงานทุกคน",
            new() {
                PermissionKeys.HrAdmin, PermissionKeys.OrganizationManage,
                PermissionKeys.LeaveApprove, PermissionKeys.LeaveReject,
                PermissionKeys.AdvanceApprove, PermissionKeys.AdvanceReject,
                PermissionKeys.AdvanceDisburse,
                PermissionKeys.ExpenseApprove, PermissionKeys.ExpenseReject,
                PermissionKeys.ExpensePay,
                PermissionKeys.PayrollRun, PermissionKeys.PayrollApprove,
                PermissionKeys.PayrollPay, PermissionKeys.PayrollView,
                "organization", "payroll", "leave-types", "leave-calendar",
                "salary-advance", "commission", "expense", "expense-no-receipt",
            }),
        new("employee-self-service", "Employee Self-Service", "🙋",
            "พนักงานทั่วไป — เห็นเฉพาะของตัวเอง: เบิก, ลา. ไม่เห็นข้อมูลคนอื่น / ไม่เข้าระบบบัญชี",
            new() {
                // No grant on the "View All" or HR-side permissions — the
                // employee's own row-level scope at /me endpoints does
                // the filtering.
                "expense", "expense-no-receipt", "leave-my",
            }),
        new("executive-viewer", "Executive Viewer", "👔",
            "ผู้บริหาร — ดูได้อย่างเดียว ทุกรายงาน + dashboard + KPI",
            new() {
                PermissionKeys.ReportsDashboard,
                PermissionKeys.ReportsExecutive, PermissionKeys.ReportsFinancial,
                PermissionKeys.ReportsOperational, PermissionKeys.ReportsExport,
                PermissionKeys.SensitiveDocsView, PermissionKeys.DocumentViewAll,
                PermissionKeys.AccountingView,
                "dashboard", "executive-reports", "reports",
                "aging", "arap-analysis", "fpa",
            }),
            // ── รอบ 205 ทีม I: บทบาทสำเร็จรูปภาษาไทยที่เจ้าของขอ (ปรับเพิ่ม-ลดต่อได้ในหน้าบทบาท) ──
        new("shop-technician", "ช่างประจำร้าน", "🔧",
            "ช่าง/พนักงานบริการ — ดูสินค้าและสต๊อก เบิกอะไหล่ออก เปิดร่างใบเสนอราคา/ใบส่งของให้ลูกค้า · ไม่อนุมัติ/ไม่ยกเลิกเอกสาร ไม่เห็นบัญชี/เงินเดือน",
            new() {
                PermissionKeys.InventoryView, PermissionKeys.InventoryIssue,
                PermissionKeys.DocumentRevenueView, PermissionKeys.DocumentRevenueCreate,
                "products", "warehouse", "documents", "contacts",
            }),
        new("cashier-th", "แคชเชียร์", "💵",
            "รับเงินหน้าร้าน + ออกใบเสร็จ/บันทึกรับชำระ · ไม่คืนเงิน ไม่ปิดยอดวัน ไม่ยกเลิกเอกสาร",
            new() {
                PermissionKeys.PosCashier,
                PermissionKeys.DocumentRevenueView, PermissionKeys.DocumentRevenueCreate,
                "pos", "payments", "documents",
            }),
        new("external-accountant", "นักบัญชีภายนอก", "🤝",
            "สำนักงานบัญชี/ผู้ทำบัญชีจากภายนอก — ดู/บันทึก/อนุมัติเอกสาร ลงบัญชี ยื่นภาษี ดูรายงาน · ไม่เห็นเงินเดือน/ข้อมูลส่วนบุคคล ไม่สั่งโอนเงิน ไม่ยกเลิกเอกสาร",
            new() {
                PermissionKeys.DocumentViewAll, PermissionKeys.DocumentCreate, PermissionKeys.DocumentApprove,
                PermissionKeys.DocumentExport,
                PermissionKeys.AccountingView, PermissionKeys.JournalManage, PermissionKeys.ChartOfAccountsEdit,
                PermissionKeys.BankView, PermissionKeys.BankReconcile,
                PermissionKeys.ReportsFinancial, PermissionKeys.ReportsOperational, PermissionKeys.ReportsExport,
                PermissionKeys.TaxFile, PermissionKeys.TaxExport,
                "accountant", "documents", "purchases", "expense-docs", "payments", "bank", "accounts", "journals",
                "general-ledger", "reports", "tax", "wht", "aging",
            }),
        new("support-staff", "ฝ่ายสนับสนุน", "🎧",
            "ดูแลลูกค้า — ดูเอกสารฝั่งขายเพื่อตอบคำถาม แก้ข้อมูลผู้ติดต่อ ดู Lead จากเว็บไซต์ · ไม่สร้าง/อนุมัติ/ยกเลิกเอกสาร ไม่เห็นบัญชี",
            new() {
                PermissionKeys.DocumentRevenueView, PermissionKeys.ContactEdit, PermissionKeys.CmsLeadView,
                "documents", "contacts", "cms-leads",
            }),
    };
}
