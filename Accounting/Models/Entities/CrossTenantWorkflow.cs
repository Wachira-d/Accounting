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
    /// <summary>รอบ 257 ชุด 3 (ฝ่ายค้าน LOW): เวลาที่ถูกระงับล่าสุด — ช่วงพักก่อนเชิญซ้ำ (<c>PartnerExchangeRules.ReinviteAllowed</c>) · null = ไม่เคย/แถวเก่า ·
    /// ช่องแยกจาก <see cref="RejectedAt"/> (คนละเหตุการณ์ — ห้ามยืม)</summary>
    public DateTime? SuspendedAt { get; set; }

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

    // ── รอบ 264 (คำตัดสิน 264-1-x · Helpers/PartnerLinkRequestRules · Helpers/PartnerLinkRequestMigration) ──
    /// <summary>เวลาที่ส่ง/เปิดคำขอล่าสุด — ฐานของวันหมดอายุ (<c>PartnerLinkRequestRules.ExpiresAt</c>) · null = แถวเก่า (ใช้ CreatedAt)</summary>
    public DateTime? InvitedAt { get; set; }
    /// <summary>outbox ของการแจ้งผู้รับ (กระดิ่ง/อีเมล/LINE ผ่านเครื่องแจ้งเตือน) — ค่า = ส่งได้ตั้งแต่เวลานี้ · null = ไม่มีอะไรต้องส่ง ·
    /// ผู้เขียน: <c>InvitePartnerAsync</c> (ในธุรกรรมคำขอ) · ผู้อ่าน: <c>DispatchPendingLinkRequestNoticesAsync</c> (AutopilotJob)</summary>
    public DateTime? InviteNoticeDueAt { get; set; }
    /// <summary>เวลาที่งานเบื้องหลังตัดสินการแจ้งแล้ว (ส่ง/ข้ามเพราะเพดาน/หมดอายุ/เลิกลอง) · ผลอยู่ใน <see cref="InviteNoticeResult"/></summary>
    public DateTime? InviteNotifiedAt { get; set; }
    /// <summary>ผลการแจ้ง (ชื่อ enum เสมอ: Delivered · NoRecipient · SuppressedByCompany · Failed · SkipCapped · SkipExpired · GiveUp)</summary>
    public string? InviteNoticeResult { get; set; }
    /// <summary>คำขอของผู้ขอ (<see cref="PartnerLinkRequest"/> ในบริษัทผู้ขอ) ที่เปิดแถวนี้ล่าสุด — ยอมรับแล้วประทับกลับที่คำขอนั้น</summary>
    public Guid? LinkRequestId { get; set; }
    /// <summary>ฝ่ายค้าน L8 (คำตัดสิน 264-1-13): บริษัทที่กด "ปฏิเสธและไม่รับคำขอจากบริษัทนี้อีก" · null = ไม่บล็อก ·
    /// ผู้อ่าน: <c>PartnerLinkRequestRules.BlockedForRequester</c> ← <c>InvitePartnerAsync</c> · ผู้บล็อกส่งคำขอเอง ⇒ ปลดบล็อก</summary>
    public Guid? RequestsBlockedByCompanyId { get; set; }
    public DateTime? RequestsBlockedAt { get; set; }
}

/// <summary>
/// รอบ 264 — "คำขอที่เราส่ง" ของบริษัทผู้ขอ (<see cref="TenantEntity.CompanyId"/> = ผู้ขอ) · <b>เกิดทุกครั้งที่กดส่ง</b> ไม่ว่าเลขภาษีนั้นจะมีในระบบหรือไม่
/// (แถวรูปเดียวกันทุกกรณี ⇒ ไม่รั่วสมาชิกภาพ · คำตัดสิน 264-1-2) · สถานะที่ผู้ขอเห็นคำนวณจาก <see cref="AcceptedAt"/> + <see cref="ExpiresAt"/> เท่านั้น
/// (<c>PartnerLinkRequestRules.OutgoingStateOf</c>) — การปฏิเสธไม่ถูกบันทึกที่นี่
/// </summary>
public class PartnerLinkRequest : TenantEntity
{
    /// <summary>เลขภาษีที่ผู้ขอกรอก (13 หลักที่ normalize แล้ว)</summary>
    public string PartnerTaxId { get; set; } = "";
    /// <summary>สาขาที่ผู้ขอกรอก (5 หลัก) · null = ไม่ระบุ</summary>
    public string? PartnerBranchCode { get; set; }
    public string? Message { get; set; }
    public Guid RequestedByUserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    /// <summary>ปลายทางยอมรับแล้ว (ประทับโดย <c>AcceptPartnershipAsync</c>) · null = ยังไม่มีผู้ยอมรับ</summary>
    public DateTime? AcceptedAt { get; set; }
    public Guid? AcceptedPartnershipId { get; set; }
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

/// <summary>
/// รอบ 258-3 (F-H3 · คำตัดสิน 258-3-7) — <b>การแจ้งในบริษัทผู้ซื้อ</b>: ผู้ขายคู่ค้า (NextAcc) รับเงินของใบแจ้งหนี้ผ่านลิงก์ชำระ/สลิปแล้ว ⇒ ผู้ซื้อกด
/// "บันทึกจ่ายตามที่ผู้ขายรับแล้ว" ครั้งเดียวที่ใบซื้อของตัวเอง · <see cref="TenantEntity.CompanyId"/> = <b>บริษัทผู้ซื้อ</b> (ผู้รับการแจ้ง · ผู้กระทำ) ·
/// ระบบไม่บันทึกการจ่ายแทน — ยอด/ภาษีหักตาม <c>Helpers/PartnerPaymentNoticeRule</c> ของผู้ซื้อ · หนึ่งแถวต่อรายการชำระของผู้ขาย (unique)
/// </summary>
public class PartnerPaymentNotice : TenantEntity
{
    public Guid TradingPartnershipId { get; set; }
    public Guid LinkId { get; set; }
    public Guid SourceCompanyId { get; set; }
    public Guid SourceDocumentId { get; set; }
    public string SourceDocumentNumber { get; set; } = "";
    /// <summary>ใบซื้อของผู้ซื้อ (จาก <c>CrossTenantDocumentLink.TargetDocumentId</c>)</summary>
    public Guid TargetDocumentId { get; set; }
    /// <summary>รายการชำระผ่านลิงก์ของผู้ขาย · รอบ 264 P3 (คำตัดสิน 264-3-6): null ได้ — การแจ้งเกิดจากทุกทางเข้ารับชำระของผู้ขาย (มือ/สลิป/รายการเดินบัญชี/นำเข้า) ไม่ใช่แค่ลิงก์</summary>
    public Guid? PaymentIntentId { get; set; }
    /// <summary>รอบ 264 P3 (264-3-6): <b>กุญแจกันซ้ำ</b> = การรับชำระของผู้ขาย (<c>Payment.Id</c> ในบริษัทผู้ขาย) · แถวเดิมเติมจาก <c>Payments.Reference = PAY-INTENT-{intent}</c> (migration) · null = แถวเก่าที่หาการชำระไม่พบ</summary>
    public Guid? SellerPaymentId { get; set; }
    /// <summary>รอบ 268 (คำตัดสิน 268-1-6 · backlog D8): การรับชำระหลายใบของผู้ขาย ⇒ <b>หนึ่งการแจ้งต่อแถวจัดสรร</b> (<c>PaymentAllocation.Id</c>) — กุญแจกันซ้ำแทน
    /// <see cref="SellerPaymentId"/> (การชำระเดียวกันมีหลายแถวแจ้งได้) · null = การชำระใบเดียว (เดิม)</summary>
    public Guid? SellerAllocationId { get; set; }
    /// <summary>รอบ 264 P3: ผู้ขายยกเลิกการรับชำระนี้แล้ว (เวลา) — Open ⇒ <c>Withdrawn</c> · Recorded ⇒ ป้ายบนใบซื้อ + แจ้ง (ไม่ยกเลิกการจ่ายของผู้ซื้อแทน · E6)</summary>
    public DateTime? SellerVoidedAt { get; set; }
    /// <summary>รอบ 264 P3: เหตุล่าสุดที่ท่ออัตโนมัติไม่บันทึกจ่ายเอง (ข้อความไทยจาก <c>PartnerAutoRecordPolicy</c>) · null = ยังไม่เคยประเมิน/ผ่าน</summary>
    public string? AutoBlockedReason { get; set; }
    /// <summary>รหัสเหตุ (ชื่อคงที่) — audit <c>PartnerPaymentAutoBlocked</c> ครั้งแรกต่อเหตุ (ไม่สแปม)</summary>
    public string? AutoBlockedCode { get; set; }
    /// <summary>ท่อประเมินแถวนี้ล่าสุดเมื่อไร (UTC)</summary>
    public DateTime? AutoEvaluatedAt { get; set; }
    /// <summary>เงินที่ผู้ขายรับจริง (หลังผู้ซื้อหัก ณ ที่จ่าย)</summary>
    public decimal CashAmount { get; set; }
    /// <summary>ภาษีหักที่ผู้ขายบันทึกไว้ฝั่งตัวเอง (แสดงเทียบ — ไม่ใช้คำนวณแทนกติกาของผู้ซื้อ)</summary>
    public decimal SellerRecordedWht { get; set; }
    public DateTime PaidAt { get; set; }
    /// <summary>ฝ่ายค้าน M5: ช่องทางที่ผู้ขายรับเงิน (ชื่อ <c>PaymentMethodKind</c>) · null = แถวก่อนมีคอลัมน์</summary>
    public string? SourceChannel { get; set; }
    public string Status { get; set; } = "Open";
    public Guid? RecordedPaymentId { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? Note { get; set; }
    /// <summary>รอบ 264 P2 (คำตัดสิน 264-4-7): ธนาคารของ<b>ผู้โอน</b>บนสลิปที่ผู้ขายได้รับ (รหัส 3 หลัก <c>ThaiBankCodes</c>) — บัญชีของผู้ซื้อเอง ·
    /// ผู้อ่าน: <c>PartnerPayablePipeline.SourceDecisionAsync</c> ⇒ <c>SlipAccountMatch</c> ⇒ ชั้น <c>SlipUnique</c> · null = ไม่มีสลิป/อ่านไม่ได้</summary>
    public string? SlipPayerBankCode { get; set; }
    /// <summary>รอบ 264 P2: เลขบัญชีผู้โอนแบบปิดบาง<b>ตามที่ธนาคารปิดมาแล้ว</b> (เช่น <c>xxx-x-x0621-x</c>) · ไม่มีชื่อ/รูปสลิป (ข้อมูลน้อยที่สุดที่ใช้จับคู่)</summary>
    public string? SlipPayerAccountMask { get; set; }
}
