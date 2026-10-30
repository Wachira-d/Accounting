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
    bool CancelAtPeriodEnd, DateTime? CancelEffectiveAt, bool CanCancel, bool CanReactivate, string? CancelNote);

public sealed record BillingCenterInvoice(
    Guid Id, string DocumentNumber, string Kind, string KindLabel, DateTime DocumentDate, DateTime? DueDate,
    decimal TotalAmount, decimal BalanceDue, string State, string StateLabel, int? DaysPastDue,
    bool CanPay, string? CannotPayReason, string? PendingPaymentNumber, List<string> LineSummary);

public sealed record BillingCenterPayment(
    Guid Id, string PaymentNumber, decimal Amount, DateTime PaymentDate, string Status, string StatusLabel,
    string Purpose, string? InvoiceNumber, string? ReceiptNumber, bool ReceiptIsTaxInvoice, bool HasReceipt, bool SlipAttached,
    string? RejectionReason);

public sealed record BillingCenterAddOn(
    string FeatureCode, string Name, string PaymentStatus, string PaymentStatusLabel, string BillingChoice, string BillingChoiceLabel,
    DateTime? TrialUntil, DateTime? GraceUntil, bool Usable, decimal? MonthlyPrice);

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
    bool CanManagePlan);
