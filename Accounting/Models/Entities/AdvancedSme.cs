using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ═══════════════════════════════════════════════════════════════════
//  Advanced Thai SME entities — เคสที่ระบบเดิมไม่รองรับ
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Post-Dated Check (เช็คล่วงหน้า, PDC) — ของลูกค้าที่จ่ายให้เรา หรือของเรา
/// ที่จ่ายให้ vendor. Thai B2B แทบ 100% ใช้ — ต้อง track วันที่ฝาก / สถานะ
/// (Held / Deposited / Cleared / Dishonored) + แนบรูปหน้าเช็ค (LINE-snap).
///
/// Direction:
///   Inbound (ลูกค้า → เรา)  → link receivable doc (Invoice/Billing)
///   Outbound (เรา → vendor) → link payable doc (PI/Expense)
///
/// JE timing:
///   Held       → ยังไม่ post (เก็บเป็น contingent — show in note only)
///   Deposited  → Dr Bank / Cr AR (inbound)  หรือ Dr AP / Cr Bank (outbound)
///   Dishonored → reverse the deposited entry + alert AR/AP team
///   Cleared    → final (ไม่มี JE เพิ่ม — กระทบยอด bank ตามจริงแล้ว)
/// </summary>
public class PostDatedCheck : TenantEntity
{
    public PdcDirection Direction { get; set; }
    public string CheckNumber { get; set; } = "";
    public string BankName { get; set; } = "";
    public string? BankBranch { get; set; }
    public decimal Amount { get; set; }
    /// <summary>วันที่ลงบนเช็ค (date on the face of the check).</summary>
    public DateTime CheckDate { get; set; }
    /// <summary>วันที่รับเช็ค / วันที่ออกเช็ค (จาก operational standpoint).</summary>
    public DateTime IssueDate { get; set; }
    /// <summary>วันที่ตั้งใจจะฝาก (=CheckDate ปกติ แต่ HR/Finance อาจปรับ).</summary>
    public DateTime ScheduledDepositDate { get; set; }
    public DateTime? DepositedAt { get; set; }
    public DateTime? ClearedAt { get; set; }
    public DateTime? DishonoredAt { get; set; }
    public string? DishonorReason { get; set; }       // "เงินไม่พอ" / "บัญชีปิด" / ...

    public PdcStatus Status { get; set; } = PdcStatus.Held;

    /// <summary>Counterparty — ลูกค้า (inbound) หรือ vendor (outbound).</summary>
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }

    /// <summary>Link ไปยัง source document (Invoice/PI/...). 1 PDC → 1 source
    /// ปกติ (partial payment กรณีเช็คตัดบางส่วนของบิล).</summary>
    public Guid? SourceDocumentId { get; set; }
    public Document? SourceDocument { get; set; }

    /// <summary>Bank account ที่เก็บเช็ค (inbound: เก็บที่ตู้เซฟ → ฝากที่ Bank ID นี้
    /// outbound: ออกจากบัญชีนี้).</summary>
    public Guid? BankAccountId { get; set; }
    public BankAccount? BankAccount { get; set; }

    /// <summary>Receipt voucher หรือ payment voucher ที่ generate ตอน Deposit.</summary>
    public Guid? RelatedVoucherId { get; set; }

    /// <summary>Attachment: รูปหน้าเช็ค (LINE snap) — ผ่าน FileAttachment.</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// Customer Cash Advance (ECA / เบิกเงินสดล่วงหน้า). พนง.ขอเบิก → manager
/// อนุมัติ → จ่ายเงินสด → กลับมา clear ด้วยใบเสร็จ. ระบบ track วงเงินคงค้าง
/// + ส่ง reminder อัตโนมัติเมื่อใกล้ครบกำหนด clear.
///
/// Workflow:
///   Requested → Approved → Disbursed → PendingClearance → Cleared/Refunded
///
/// JE timing:
///   Disbursed  → Dr "ลูกหนี้พนง.-เงินยืม" (115xx) / Cr Cash/Bank
///   Cleared    → Dr Expense (per receipts) + Dr Cash (refund) / Cr ลูกหนี้พนง.
/// </summary>
public class CashAdvanceRequest : TenantEntity
{
    public string RequestNumber { get; set; } = "";   // CA-YYYYMM-NNNN
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public decimal RequestedAmount { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public decimal DisbursedAmount { get; set; }
    public decimal ClearedAmount { get; set; }
    public decimal RefundAmount { get; set; }

    public DateTime RequestDate { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? DisbursedAt { get; set; }
    public DateTime? ClearanceDueDate { get; set; }   // = DisbursedAt + ClearanceDays
    public DateTime? ClearedAt { get; set; }

    public CashAdvanceStatus Status { get; set; } = CashAdvanceStatus.Requested;

    public string Purpose { get; set; } = "";          // "ค่าทำธุระจัดส่งของลูกค้า A"
    public string? ApproverNote { get; set; }
    public string? RejectionReason { get; set; }

    public Guid? ApproverUserId { get; set; }

    /// <summary>Payment voucher ที่ generate ตอน Disburse.</summary>
    public Guid? DisbursementVoucherId { get; set; }

    /// <summary>Documents ที่ใช้เคลียร์ (expense / certificate-in-lieu).
    /// JSON array of Guid strings — parse ใน service layer.</summary>
    public string ClearanceDocumentIdsJson { get; set; } = "[]";
}

/// <summary>
/// Early Payment Discount (ส่วนลดเงินสด 2/10 net 30) — ตั้งบนสัญญาขายให้ลูกค้า
/// + auto-apply เมื่อ Receipt มา within early-payment window. AR/Finance ไม่ต้อง
/// คิดเอง — ระบบดูจากวันที่ receipt vs invoice + early days threshold.
///
/// ตัวอย่าง: "2/10 net 30" → ลด 2% ถ้าจ่ายภายใน 10 วัน, ครบกำหนด 30 วัน.
/// </summary>
public class EarlyPaymentDiscountTerm : TenantEntity
{
    public string Code { get; set; } = "";             // "2/10-N30" / "5/5-N15"
    public string DisplayName { get; set; } = "";      // "2% ภายใน 10 วัน, ครบกำหนด 30 วัน"
    public int DiscountWindowDays { get; set; }        // 10
    public decimal DiscountPercent { get; set; }       // 2
    public int NetTermDays { get; set; }               // 30
    public bool IsActive { get; set; } = true;
}

// ═══════════════════════════════════════════════════════════════════
//  Enums (place here to keep entity file self-contained)
// ═══════════════════════════════════════════════════════════════════
