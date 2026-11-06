using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// B2B trading relationship between two tenant Companies on this platform.
/// Both sides must explicitly accept before documents can flow
/// automatically between them — this is the trust anchor that prevents
/// Company A from injecting fake documents into Company B's inbox by
/// just guessing B's tax ID.
///
/// Lifecycle:
///   Pending  → either side invited but the other hasn't responded
///   Accepted → both sides confirmed; cross-tenant document flow active
///   Suspended → one side temporarily paused (recoverable)
///   Rejected → invitation declined; resend requires fresh invite
///
/// Single row per (CompanyA, CompanyB) pair, ordered by Id so A < B in
/// the database — keeps the relationship canonical regardless of who
/// invited whom.
/// </summary>
public class TradingPartnership : BaseEntity
{
    // Ordered so CompanyAId is always the smaller GUID — eliminates the
    // (A,B)/(B,A) duplicate-row problem at the schema level.
    public Guid CompanyAId { get; set; }
    public Company CompanyA { get; set; } = null!;
    public Guid CompanyBId { get; set; }
    public Company CompanyB { get; set; } = null!;

    public TradingPartnershipStatus Status { get; set; } = TradingPartnershipStatus.Pending;

    // Who started the relationship — needed so the OTHER side sees the
    // pending invite and can accept it. Always CompanyAId or CompanyBId.
    public Guid InvitedByCompanyId { get; set; }
    public Guid InvitedByUserId { get; set; }
    public string? InvitationMessage { get; set; }

    public DateTime? AcceptedAt { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }

    /// <summary>Optional credit limit — when set, incoming docs from this
    /// partner above the limit require manual approval even if the
    /// company has auto-approve enabled.</summary>
    public decimal? AutoApproveAmountLimit { get; set; }

    /// <summary>Email used to notify the partner about new docs awaiting
    /// approval. Defaults to the partner's company billing email when
    /// not specified here.</summary>
    public string? NotifyEmail { get; set; }

    // ── รอบ 255 WP-12B ข้อ 7 (N3): เพดานยอดใบซื้อร่างที่ "ฝั่งผู้รับ" ตั้งต่อคู่ (แถวเดียวต่อคู่ เรียง A < B) · null = ไม่จำกัด ·
    // ผู้อ่าน: PartnerExchangeRules.ReceiverLimit ← CrossTenantWorkflowService.TryCreatePurchaseDraftAsync · ผู้ตั้ง: PUT …/trading-partners/{id}/purchase-draft-limit (audit)
    /// <summary>เพดานเมื่อ <see cref="CompanyAId"/> เป็นผู้รับ</summary>
    public decimal? PurchaseDraftLimitA { get; set; }
    /// <summary>เพดานเมื่อ <see cref="CompanyBId"/> เป็นผู้รับ</summary>
    public decimal? PurchaseDraftLimitB { get; set; }
}

/// <summary>
/// A single cross-tenant document flow event — A's Quotation routed to
/// B for approval, B's PO routed back to A, A's Invoice routed forward
/// to B, etc. One row per direction per step.
///
/// SnapshotJson preserves the source document's content at the moment
/// of approval — so when A later edits / voids the Quotation, what B
/// approved is still recoverable for audit.
/// </summary>
public class CrossTenantDocumentLink : BaseEntity
{
    public Guid TradingPartnershipId { get; set; }
    public TradingPartnership TradingPartnership { get; set; } = null!;

    public Guid SourceCompanyId { get; set; }   // who issued
    public Guid SourceDocumentId { get; set; }
    public Document SourceDocument { get; set; } = null!;

    public Guid TargetCompanyId { get; set; }   // who receives / approves
    /// <summary>Set when this flow auto-creates a downstream document
    /// at the target side (e.g. PO created at B after B approves A's
    /// Quotation). Null when the flow is purely a sign-off.</summary>
    public Guid? TargetDocumentId { get; set; }
    public Document? TargetDocument { get; set; }

    public CrossTenantLinkType LinkType { get; set; }
    public CrossTenantLinkStatus Status { get; set; } = CrossTenantLinkStatus.PendingApproval;

    public Guid? ApproverUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid? DocumentApprovalId { get; set; }   // → DocumentApproval row on source side
    public string? RejectionReason { get; set; }

    /// <summary>Immutable JSON snapshot of the source document at
    /// approval time. Keeps "what was approved" decoupled from "what
    /// the source doc looks like now" for audit purposes.</summary>
    public string? SnapshotJson { get; set; }

    public string? Comment { get; set; }
}

/// <summary>
/// Per-company toggles controlling how auto-routing flows behave.
/// Defaults are conservative (everything off) — tenants opt in to the
/// automation that fits their internal control standards.
/// </summary>
public class WorkflowAutomationConfig : BaseEntity
{
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    // ─── Incoming approval (we're the buyer reviewing partner quotations) ───
    public bool AutoApproveIncomingQuotations { get; set; } = false;
    public decimal? AutoApproveMinAmount { get; set; }      // null = no min
    public decimal? AutoApproveMaxAmount { get; set; }      // null = no max

    // ─── Downstream document creation after approval ───
    public bool AutoCreatePoOnQuotationApproval { get; set; } = false;
    public bool AutoCreateInvoiceFromIncomingPo { get; set; } = false;
    public bool AutoCreateReceiptFromIncomingPayment { get; set; } = false;

    // ─── Signature stamping ───
    public bool AutoStampSignature { get; set; } = false;
    public Guid? DefaultApproverUserId { get; set; }
    public User? DefaultApproverUser { get; set; }
    public Guid? DefaultSignatureId { get; set; }
    public UserSignature? DefaultSignature { get; set; }

    // ─── Notifications ───
    public bool NotifyOnIncomingDocument { get; set; } = true;
    public string? NotifyEmail { get; set; }

    // ─── รอบ 255 WP-12B (AUDIT_249 §WP-12 ข้อ 7–8) — ค่าตั้งต้นปิดทั้งคู่ ───
    /// <summary>ใบแจ้งหนี้/ใบกำกับที่คู่ค้าส่งถึงเรา ⇒ สร้างใบซื้อ<b>ร่าง</b> (N3 · ไม่อนุมัติ/ไม่ลงภาษีซื้อเอง) · ผู้อ่าน: <c>CrossTenantWorkflowService.TryCreatePurchaseDraftAsync</c></summary>
    public bool CreatePurchaseDraftsFromPartners { get; set; } = false;
    /// <summary>ส่ง 50 ทวิ ที่เราออกให้คู่ค้า NextAcc เข้ากล่องรับของเขาอัตโนมัติ · ผู้อ่าน: <c>PartnerWhtCertExchangeService.RunOnceAsync</c></summary>
    public bool SendWhtCertsToPartners { get; set; } = false;
    /// <summary>ระบบประทับตอนเปิด <see cref="SendWhtCertsToPartners"/> — ส่งเฉพาะใบที่ออกหลังจากนี้ (ไม่ส่งย้อนหลัง) · ปิดแล้วเปิดใหม่ = ประทับใหม่</summary>
    public DateTime? SendWhtCertsToPartnersSince { get; set; }
}
