namespace Accounting.Models.DTOs.Subscription;

// รอบ 219 เฟส 2 (คำตัดสินข้อ 180) — ศูนย์บิลของผู้เช่า: ทุกค่าที่หน้าแสดง (สถานะ · ป้าย · จ่ายได้ไหม · เหตุที่จ่ายไม่ได้) มาจากเซิร์ฟเวอร์ ·
// enum ออกเป็น "ชื่อ" เสมอ (F2 ข้อ 5)

/// <summary>แจ้งชำระใบแจ้งหนี้ค่าบริการด้วยการโอน (ยอด = ยอดค้างของใบ — เซิร์ฟเวอร์คำนวณ ไม่รับยอดจาก body)</summary>
public sealed record SubmitInvoicePaymentRequest(
    DateTime? PaymentDate,
    string? TransferReference,
    string? FromBankName,
    string? CustomerNotes,
    /// <summary>ฝ่ายค้านรอบ 219 เฟส 2 (S4): ภาษีหัก ณ ที่จ่ายที่ผู้เช่านิติบุคคลหัก (บาท) — null/0 = ไม่หัก · เงินโอน = ยอดค้าง − ค่านี้ ·
    /// ตัวตรวจเดียว <c>PlatformInvoiceRules.WithholdingProblem</c> (นิติบุคคลเท่านั้น · ไม่เกินอัตรา ม.40(2) ของตาราง ThaiWhtRateTable)</summary>
    decimal? WithholdingTaxAmount = null);

public sealed record BillingCenterPlan(
    string Plan, string? PlanName, string Status, string StatusLabel, string BillingCycle, decimal PricePerCycle,
    DateTime? EndDate, bool IsPermanentFree, DateTime? NextRenewalInvoiceAt,
    string? ScheduledPlan, DateTime? ScheduledPlanEffectiveAt,
    bool CancelAtPeriodEnd, DateTime? CancelEffectiveAt, bool CanCancel, bool CanReactivate, string? CancelNote,
    /// <summary>รอบ 232: "ไม่หมดอายุ" ตัดสินจาก <c>SubscriptionExpiry</c> (ธงฟรีถาวร <b>หรือ</b> ค่าแทนในฐาน) — หน้าเว็บแสดง "ไม่หมดอายุ" ไม่ใช่ปี 2669</summary>
    bool IsOpenEnded = false);

public sealed record BillingCenterInvoice(
    Guid Id, string DocumentNumber, string Kind, string KindLabel, DateTime DocumentDate, DateTime? DueDate,
    decimal TotalAmount, decimal BalanceDue, string State, string StateLabel, int? DaysPastDue,
    bool CanPay, string? CannotPayReason, string? PendingPaymentNumber, List<string> LineSummary,
    /// <summary>รอบ 234 (คำตัดสิน 234-1): ภาษีหัก ณ ที่จ่ายที่ระบบเสนอสำหรับยอดค้างของใบ (<c>PlatformWhtSuggestion</c>) — หน้าเว็บเติมยอดในช่อง (แก้ได้ในกรอบ
    /// <c>PlatformInvoiceRules.WithholdingProblem</c>) · null = ใบไม่ค้าง</summary>
    global::Accounting.Helpers.PlatformWhtSuggestion.Result? Withholding = null);

public sealed record BillingCenterPayment(
    Guid Id, string PaymentNumber, decimal Amount, DateTime PaymentDate, string Status, string StatusLabel,
    string Purpose, string? InvoiceNumber, string? ReceiptNumber, bool ReceiptIsTaxInvoice, bool HasReceipt, bool SlipAttached,
    string? RejectionReason,
    /// <summary>ฝ่ายค้านรอบสุดท้าย 4a: หักภาษี ณ ที่จ่ายแล้วยังไม่ส่งหนังสือรับรอง 50 ทวิ (หน้าเว็บแสดงปุ่มส่ง) — เซิร์ฟเวอร์ตัดสิน</summary>
    bool NeedsWhtCertificate = false,
    decimal WithholdingTaxAmount = 0m,
    /// <summary>รอบ 230: รายการชำระค่าแพ็กเกจ/ส่วนต่างอัปเกรดที่ยังไม่จ่าย (ไม่มีสลิป · ไม่ผูกใบแจ้งหนี้) ⇒ หน้าเว็บแสดงปุ่ม "ชำระ" — เซิร์ฟเวอร์ตัดสิน</summary>
    bool CanPayNow = false,
    /// <summary>รอบ 230: ส่วนลดคูปองของรายการ (0 = ไม่มี) · ยอดก่อนคูปอง</summary>
    decimal CouponDiscountAmount = 0m,
    string? CouponCode = null,
    /// <summary>รอบ 234 (คำตัดสิน 234-W2): สถานะ 50 ทวิ (<c>PlatformWhtCertificateStatus</c> — รอส่ง/รอตรวจ/ยืนยันแล้ว/ตีกลับ) · ป้าย · เหตุตีกลับ · ส่งได้ไหม</summary>
    string? WhtCertificateState = null,
    string? WhtCertificateStateLabel = null,
    string? WhtCertificateRejectedReason = null,
    bool CanUploadWhtCertificate = false);

public sealed record BillingCenterAddOn(
    string FeatureCode, string Name, string PaymentStatus, string PaymentStatusLabel, string BillingChoice, string BillingChoiceLabel,
    DateTime? TrialUntil, DateTime? GraceUntil, bool Usable, decimal? MonthlyPrice,
    /// <summary>รอบ 228 (§5.7): ใบเสร็จ/ใบกำกับของการชำระล่วงหน้าครั้งล่าสุด (ดาวน์โหลดที่ <c>billing-center/receipts/{id}/pdf</c>) · null = ยังไม่มีใบ</summary>
    Guid? ReceiptDocumentId = null, string? ReceiptNumber = null);

/// <summary>รอบ 228 (§5.7): เอกสารที่บริษัทนี้เป็นผู้ซื้อใน tenant ผู้ให้บริการ (ใบเสร็จ · ใบกำกับภาษี · ใบลดหนี้) — ป้ายจากเซิร์ฟเวอร์</summary>
public sealed record BillingCenterReceipt(
    Guid Id, string DocumentNumber, string DocumentType, string TypeLabel, DateTime DocumentDate, decimal TotalAmount,
    string? Reference, bool IsVoided);

public sealed record BillingCenterSummary(
    bool PlatformTenantConfigured,
    string? Notice,
    BillingCenterPlan Plan,
    decimal OutstandingTotal,
    decimal OverdueTotal,
    int OverdueCount,
    List<BillingCenterInvoice> Invoices,
    List<BillingCenterPayment> Payments,
    List<BillingCenterAddOn> AddOns,
    /// <summary>ช่องทางชำระ + นโยบายที่ต้องติ๊ก — ตัวเดียวกับหน้าแพ็กเกจ/ส่วนเสริม (<c>PlatformPayInfo.BuildAsync</c> ของ part B)</summary>
    object PayOptions,
    string SuspensionPolicy,
    bool CanPay,
    bool CanManagePlan,
    /// <summary>รอบ 228 (§5.7): ใบเสร็จ/ใบกำกับภาษี/ใบลดหนี้ค่าบริการ (ล่าสุดก่อน) — null = ยังไม่ตั้งบริษัทผู้ให้บริการ</summary>
    List<BillingCenterReceipt>? Receipts = null,
    /// <summary>รอบ 234 (คำตัดสิน 234-4): การ์ด "ข้อมูลบริษัทสำหรับใบกำกับภาษี" — ชนิดผู้ซื้อ/สิ่งที่ขาด/คำเตือน (<c>PlatformPayerProfileGate</c>)</summary>
    global::Accounting.Helpers.PlatformPayerProfileGate.Result? PayerProfile = null,
    /// <summary>รอบ 234 (คำตัดสิน 234-3): ค่าตั้ง "หัก ณ ที่จ่ายทุกครั้ง" (null = ยังไม่เลือก — หน้าเว็บติ๊กให้เมื่อเข้าเกณฑ์) · echo ของ <c>Subscription.WithholdTaxPreference</c></summary>
    bool? WithholdTaxPreference = null);

/// <summary>รอบ 234 (คำตัดสิน 234-3): ตั้งค่า "หักภาษี ณ ที่จ่ายทุกครั้งที่ชำระค่าบริการ" (รวมตัดบัตรอัตโนมัติ)</summary>
public sealed record WithholdingPreferenceRequest(bool WithholdTax);
