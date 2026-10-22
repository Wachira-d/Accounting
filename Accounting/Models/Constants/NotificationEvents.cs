namespace Accounting.Models.Constants;

/// <summary>
/// Canonical event keys for the notification engine. Stored as strings
/// in NotificationSettings.EventKey so new events can be added without
/// a schema migration (unlike the NotificationType enum which is
/// reserved for legacy in-app notification rows). One event key per
/// fire site — services dispatch via
/// <see cref="Services.Interfaces.INotificationEngine"/>.
/// </summary>
public static class NotificationEvents
{
    // ===== HR — Leave =====
    public const string LeaveSubmitted = "leave.submitted";
    public const string LeaveApproved  = "leave.approved";
    public const string LeaveRejected  = "leave.rejected";
    public const string LeaveCancelled = "leave.cancelled";

    // ===== HR — Salary Advance =====
    public const string AdvanceSubmitted = "advance.submitted";
    public const string AdvanceApproved  = "advance.approved";
    public const string AdvanceRejected  = "advance.rejected";
    public const string AdvanceDisbursed = "advance.disbursed";
    public const string AdvanceCleared   = "advance.cleared";

    // ===== HR — Expense Claim =====
    public const string ExpenseSubmitted = "expense.submitted";
    public const string ExpenseApproved  = "expense.approved";
    public const string ExpenseRejected  = "expense.rejected";
    public const string ExpensePaid      = "expense.paid";

    // ===== HR — Payroll =====
    public const string PayrollGenerated = "payroll.generated";   // run calculated
    public const string PayrollApproved  = "payroll.approved";
    public const string PayrollPaid      = "payroll.paid";
    /// <summary>Fires once per employee when payroll moves to Paid —
    /// recipient is the employee themselves (Requester role resolves to
    /// Employee.UserId or Employee.LineId/Email). Used to broadcast
    /// "เงินเดือนโอนแล้ว / ดาวน์โหลดสลิปที่นี่" via LINE/Email.</summary>
    public const string PayrollSlipReady = "payroll.slip_ready";

    // ===== Accounting =====
    public const string OcrAssetDetected = "ocr.asset_detected";
    public const string PaymentVoucherGenerated = "document.pv_generated";
    public const string DocumentApproved        = "document.approved";
    public const string DocumentVoided          = "document.voided";
    /// <summary>รับชำระออนไลน์ค้างนานผิดปกติในโหมดใช้งานจริง — อาการที่ผู้ใช้เจอคือ
    /// "ลูกค้าจ่ายแล้วแต่ออเดอร์ยังค้าง" ซึ่งเจ้าของร้านไม่รู้จนลูกค้าโทรมา ·
    /// สาเหตุที่พบบ่อยสุดคือยังไม่ได้ตั้ง webhook URL ในแดชบอร์ดผู้ให้บริการ</summary>
    public const string GatewayPaymentStuck     = "gateway.payment_stuck";
    /// <summary>รอบ 205 ทีม E: เงินออนไลน์เข้าแล้วแต่ต้นทาง (คำสั่งซื้อ/เอกสาร) บันทึกรับเงินไม่สำเร็จ — ไม่มีใบ/JE จนกว่าจะมีคนทำมือ</summary>
    public const string GatewayPaymentUnfulfilled = "gateway.payment_unfulfilled";
    /// <summary>รอบ 205 ทีม E: โควตาเอกสารเดือนนี้ข้าม 80% / เต็ม — แจ้งเจ้าของครั้งเดียวต่อระดับต่อเดือน (เอกสารที่กฎหมายบังคับยังออกได้ = ส่วนเกิน)</summary>
    public const string DocumentQuotaLevel = "quota.documents_level";
    public const string DepreciationPosted      = "depreciation.posted";
    /// <summary>2026-10-14 (ID#13): งานลงค่าเสื่อมราคาอัตโนมัติรายเดือนล้ม (เช่น ผังค่าเสื่อมถูกปิดใช้/งวดปิด) —
    /// เดิมลงแค่ log ⇒ เจ้าของไม่รู้จนปิดงบ</summary>
    public const string DepreciationFailed      = "depreciation.failed";
    /// <summary>คำตัดสินข้อ 114 Q1: รายการประจำอนุมัติอัตโนมัติพร้อมข้อสังเกต §65 ตรี (ไม่บล็อก) — แจ้งผู้ตั้งรายการ</summary>
    public const string RecurringApprovedWithNotes = "recurring.approved_with_notes";
    /// <summary>ทีมตรวจรายการประจำ 2026-10-14: รอบอัตโนมัติสร้างเอกสารไม่สำเร็จ (อ้างบัญชี/ผู้ติดต่อที่ถูกลบ · งวดปิด ฯลฯ) — แจ้งผู้ตั้งรายการ
    /// ครั้งแรกที่ล้ม/เมื่อสาเหตุเปลี่ยน (ไม่ใช่ทุก 15 นาที) · เหตุผลอยู่บนแถว (<c>RecurringTransaction.LastRunError</c>) ในหน้ารายการประจำ</summary>
    public const string RecurringRunFailed = "recurring.run_failed";
    /// <summary>F14 audit hash chain ตรวจรายสัปดาห์เจอ tamper (มี
    /// row ที่ RowHash/PrevHash ไม่ตรง). ส่งหา Owner + Accounting role
    /// เพื่อสอบ DBA / raw SQL.</summary>
    public const string AuditChainTampered      = "audit.chain_tampered";
    /// <summary>หนังสือทวงหนี้ AR ที่ใบเกิน 30/60/90 วัน — ส่งผ่าน
    /// OverdueDunningJob ตามลำดับขั้น. กัน spam: ส่งซ้ำได้เฉพาะข้ามขั้น +
    /// ≥7 วันจาก LastDunningSentAt. recipient = Accounting + Owner role</summary>
    public const string OverdueDunningReminder  = "ar.dunning.reminder";   // 30d
    public const string OverdueDunningFirst     = "ar.dunning.first";      // 60d
    public const string OverdueDunningFinal     = "ar.dunning.final";      // 90d

    // ===== Approval workflow (multi-step) =====
    /// <summary>มีรายการรอการอนุมัติของผู้รับ — recipient = ApproverUserId
    /// แบบ user-specific (NotificationEngine resolver รองรับ UserId override)</summary>
    public const string ApprovalRequired   = "approval.required";
    public const string ApprovalGranted    = "approval.granted";
    public const string ApprovalRejected   = "approval.rejected";

    // ===== Subscription / billing =====
    public const string SubscriptionTrialStarted    = "subscription.trial_started";
    public const string SubscriptionTrialEnding     = "subscription.trial_ending";
    public const string SubscriptionPaymentSucceeded = "subscription.payment_succeeded";
    public const string SubscriptionPaymentFailed   = "subscription.payment_failed";
    public const string SubscriptionExpired         = "subscription.expired";
    public const string SubscriptionLimitReached    = "subscription.limit_reached";

    // ===== ที่พัก =====
    /// <summary>รอบ 202 ทีม LO (คำตัดสินข้อ 127): เงินออนไลน์เข้า/แขกส่งสลิปแล้ว แต่ระบบยืนยันห้องไม่ได้ (ห้องเต็ม/hold หมด) —
    /// ระบบไม่คืนเงินเอง ต้องมีคนตัดสิน (ติดธงบนการจองด้วย · ตัวกรอง "เงินเข้าแต่ยืนยันไม่ได้" ในหน้าที่พัก)</summary>
    public const string LodgingPaymentUnconfirmed = "lodging.payment_unconfirmed";

    // ===== CMS =====
    public const string SitePublishSucceeded = "site.publish_succeeded";
    public const string SitePublishFailed    = "site.publish_failed";

    /// <summary>All event keys for the admin matrix UI. Listed in the
    /// order they should appear, grouped by domain.</summary>
    public static readonly IReadOnlyList<(string Domain, string Key, string Label)> All = new[]
    {
        ("Leave",    LeaveSubmitted, "ส่งคำขอลา"),
        ("Leave",    LeaveApproved,  "อนุมัติคำขอลา"),
        ("Leave",    LeaveRejected,  "ปฏิเสธคำขอลา"),
        ("Leave",    LeaveCancelled, "ยกเลิกคำขอลา"),
        ("Advance",  AdvanceSubmitted, "ส่งคำขอเงินทดรอง"),
        ("Advance",  AdvanceApproved,  "อนุมัติเงินทดรอง"),
        ("Advance",  AdvanceRejected,  "ปฏิเสธเงินทดรอง"),
        ("Advance",  AdvanceDisbursed, "จ่ายเงินทดรอง"),
        ("Advance",  AdvanceCleared,   "เคลียร์เงินทดรองครบ"),
        ("Expense",  ExpenseSubmitted, "ส่งใบเบิกค่าใช้จ่าย"),
        ("Expense",  ExpenseApproved,  "อนุมัติใบเบิก"),
        ("Expense",  ExpenseRejected,  "ปฏิเสธใบเบิก"),
        ("Expense",  ExpensePaid,      "จ่ายเงินใบเบิก"),
        ("Payroll",  PayrollGenerated, "คำนวณรอบเงินเดือน"),
        ("Payroll",  PayrollApproved,  "อนุมัติรอบเงินเดือน"),
        ("Payroll",  PayrollPaid,      "จ่ายเงินเดือน"),
        ("Payroll",  PayrollSlipReady, "แจ้งพนักงาน: สลิปพร้อมรับ (LINE/Email)"),
        ("Accounting", OcrAssetDetected, "OCR พบสินทรัพย์น่าจะลงทะเบียน"),
        ("Accounting", PaymentVoucherGenerated, "สร้างใบสำคัญจ่ายอัตโนมัติ"),
        ("Accounting", DocumentApproved, "อนุมัติเอกสาร"),
        ("Accounting", DocumentVoided, "ยกเลิกเอกสาร"),
        ("Accounting", GatewayPaymentStuck, "รับชำระออนไลน์ค้างนานผิดปกติ (ตรวจ webhook)"),
        ("Accounting", GatewayPaymentUnfulfilled, "เงินออนไลน์เข้าแล้วแต่ระบบบันทึกรับเงิน/ออกเอกสารไม่สำเร็จ (ต้องทำมือ)"),
        ("Accounting", DocumentQuotaLevel, "โควตาเอกสารเดือนนี้ใกล้เต็ม (80%) / เต็มแล้ว"),
        ("Accounting", DepreciationPosted, "ลงค่าเสื่อมราคาประจำเดือนอัตโนมัติ"),
        ("Accounting", DepreciationFailed, "ลงค่าเสื่อมราคาอัตโนมัติไม่สำเร็จ (ต้องแก้แล้วกดคำนวณค่าเสื่อมเอง)"),
        ("Accounting", RecurringApprovedWithNotes, "รายการประจำอนุมัติอัตโนมัติพร้อมข้อสังเกตรายจ่ายต้องห้าม §65 ตรี"),
        ("Accounting", RecurringRunFailed, "รายการประจำสร้างเอกสารอัตโนมัติไม่สำเร็จ (ต้องแก้ที่หน้ารายการประจำ)"),
        ("Accounting", AuditChainTampered, "🚨 Audit log ถูกแก้ไข (hash chain ไม่ตรง)"),
        ("Accounting", OverdueDunningReminder, "AR เกิน 30 วัน — ส่งหนังสือทวงหนี้รอบที่ 1"),
        ("Accounting", OverdueDunningFirst, "AR เกิน 60 วัน — ส่งหนังสือทวงหนี้รอบที่ 2"),
        ("Accounting", OverdueDunningFinal, "AR เกิน 90 วัน — ส่งหนังสือทวงหนี้ครั้งสุดท้าย"),
        ("Approval", ApprovalRequired, "มีรายการรอการอนุมัติ"),
        ("Approval", ApprovalGranted, "อนุมัติคำขอแล้ว"),
        ("Approval", ApprovalRejected, "ปฏิเสธคำขอ"),
        ("Lodging",  LodgingPaymentUnconfirmed, "ที่พัก: เงินเข้า/ส่งสลิปแล้วแต่ยืนยันห้องไม่ได้ (ต้องตัดสิน)"),
        ("CMS",      SitePublishSucceeded, "เผยแพร่เว็บไซต์สำเร็จ"),
        ("CMS",      SitePublishFailed,    "เผยแพร่เว็บไซต์ล้มเหลว"),
    };
}

/// <summary>Logical recipient role for a notification event — resolved
/// to concrete user IDs by the recipient resolver. Stored as the
/// string name in NotificationSettings.RecipientRole.</summary>
public static class NotificationRecipientRoles
{
    /// <summary>The employee who submitted the request (Leave / Advance / Claim).</summary>
    public const string Requester = "Requester";
    /// <summary>The requester's direct manager (Employee.DirectManagerId → User).</summary>
    public const string DirectManager = "DirectManager";
    /// <summary>The requester's department head — fallback when no direct manager.</summary>
    public const string DepartmentHead = "DepartmentHead";
    /// <summary>Users with the HR_Admin permission key (any role granted "perm:HR.Admin").</summary>
    public const string HrAdmin = "HR_Admin";
    /// <summary>Users with the Accounting permission key (any role granted "perm:Accounting.View").</summary>
    public const string Accounting = "Accounting";
    /// <summary>Company Owner users.</summary>
    public const string Owner = "Owner";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Requester, DirectManager, DepartmentHead, HrAdmin, Accounting, Owner
    };
}
