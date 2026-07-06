using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// บัญชีธนาคาร (เทียบเท่า FlowAccount & PEAK: Bank connection)
/// </summary>
public class BankAccount : TenantEntity
{
    public string AccountName { get; set; } = null!;
    public string BankName { get; set; } = null!;
    public string AccountNumber { get; set; } = null!;
    public string? BranchName { get; set; }
    public string AccountType { get; set; } = "Savings"; // Savings, Current, Fixed
    public string Currency { get; set; } = "THB";
    public decimal CurrentBalance { get; set; }
    public bool IsActive { get; set; } = true;

    // ── Snapshot ยอดจริงจาก statement ล่าสุดที่นำเข้า ──
    // แยกจาก CurrentBalance เพื่อให้ UI แสดง "ยอดตามธนาคาร ณ วันที่ X" เทียบกับ
    // ยอดตามบัญชี (GL) ได้ตรงไปตรงมา — ธนาคารคือ source of truth ของเงินจริง
    /// <summary>ยอดคงเหลือจากแถวล่าสุดของ statement ที่นำเข้า (null = ยังไม่เคยนำเข้า)</summary>
    public decimal? StatementBalance { get; set; }
    /// <summary>วันที่ของแถวล่าสุดใน statement (ยอด ณ วันนี้)</summary>
    public DateTime? StatementBalanceDate { get; set; }
    /// <summary>เวลาที่นำเข้า statement ครั้งล่าสุด</summary>
    public DateTime? StatementImportedAt { get; set; }

    // Mapping to Chart of Account
    public Guid? LinkedAccountId { get; set; }
    public ChartOfAccount? LinkedAccount { get; set; }

    public ICollection<BankTransaction> Transactions { get; set; } = new List<BankTransaction>();
}

/// <summary>
/// รายการเคลื่อนไหวธนาคาร (Bank Statement)
/// </summary>
public class BankTransaction : TenantEntity
{
    public Guid BankAccountId { get; set; }
    public BankAccount BankAccount { get; set; } = null!;
    public DateTime TransactionDate { get; set; }
    public BankTransactionType TransactionType { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? Description { get; set; }
    public string? Reference { get; set; }
    public string? Payee { get; set; }

    // Reconciliation
    public ReconciliationStatus ReconciliationStatus { get; set; } = ReconciliationStatus.Unmatched;
    public Guid? MatchedPaymentId { get; set; }
    public Guid? MatchedJournalEntryId { get; set; }
    public DateTime? ReconciledAt { get; set; }
    public string? ReconciledBy { get; set; }

    // AI Reconciliation — group ID for aggregated (many-to-one) matches
    public string? MatchGroupId { get; set; }

    // True M:N reconciliation: when this bank txn is part of a multi-bank /
    // multi-item match (e.g. 2 deposits + 1 fee + 3 receipts net-off vs a
    // PV), the link lives in ReconciliationGroup. The legacy MatchGroupId /
    // MatchedEntryIdsJson fields above keep working for the AI's M:1 path.
    public Guid? ReconciliationGroupId { get; set; }
    public ReconciliationGroup? ReconciliationGroup { get; set; }

    // For many-to-one matches: JSON array of all Payment/JournalEntry IDs
    // matched to this single bank transaction. Format: ["guid1","guid2",...]
    // (MatchedPaymentId / MatchedJournalEntryId still hold the first ID for
    // backwards compatibility / single-match queries.)
    public string? MatchedEntryIdsJson { get; set; }
}

/// <summary>
/// Reconciliation group — header for M:N matching. Multiple bank transactions
/// can share one group, and the group's items can be a mix of inflows
/// (receipts, customer payments) and outflows (vendor payments, payment
/// vouchers, journal entries). The group is "balanced" when the signed sum
/// of bank-side amounts equals the signed sum of item-side amounts (within
/// tolerance). This is what enables the
/// "ใบเสร็จ - ใบสำคัญจ่าย = ยอด statement รายการเดียว" net-off workflow.
/// </summary>
public class ReconciliationGroup : TenantEntity
{
    public Guid BankAccountId { get; set; }
    public BankAccount BankAccount { get; set; } = null!;

    /// <summary>Running number for human-friendly reference, e.g. RG-2025-04-001.</summary>
    public string GroupNumber { get; set; } = null!;

    /// <summary>Date the group was reconciled (operator's "as of" date).</summary>
    public DateTime ReconciledDate { get; set; }

    /// <summary>Signed sum of all bank-transaction AllocatedAmount in this group.</summary>
    public decimal TotalBankAmount { get; set; }

    /// <summary>Signed sum of non-bank-transaction AllocatedAmount in the group
    /// (Payments + Journal Entries + Documents). Net of inflows minus outflows.</summary>
    public decimal TotalMatchedAmount { get; set; }

    /// <summary>True when |TotalBankAmount - TotalMatchedAmount| ≤ tolerance.</summary>
    public bool IsBalanced { get; set; }

    public string? Notes { get; set; }

    public ICollection<ReconciliationGroupItem> Items { get; set; } = new List<ReconciliationGroupItem>();
}

/// <summary>
/// One line in a <see cref="ReconciliationGroup"/>. ItemType tells you which
/// table ItemId points at; AllocatedAmount is signed (positive=inflow on the
/// bank account, negative=outflow).
/// </summary>
public class ReconciliationGroupItem : BaseEntity
{
    public Guid GroupId { get; set; }
    public ReconciliationGroup Group { get; set; } = null!;

    /// <summary>Discriminator: BankTransaction / Payment / JournalEntry / Document.</summary>
    public ReconciliationItemType ItemType { get; set; }

    /// <summary>FK into the table named by ItemType — polymorphic, not a hard FK.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Signed amount allocated to the group. Positive contributes as
    /// inflow on the bank side (deposit / receipt), negative as outflow
    /// (withdrawal / payment voucher).</summary>
    public decimal AllocatedAmount { get; set; }

    public string? Notes { get; set; }
}

/// <summary>Polymorphic discriminator for <see cref="ReconciliationGroupItem"/>.</summary>
public enum ReconciliationItemType
{
    BankTransaction = 1,
    Payment = 2,
    JournalEntry = 3,
    Document = 4,
}

/// <summary>
/// Pattern memory for the AI reconciliation suggester. Every confirmed
/// reconciliation contributes one row per bank-txn↔item pair: signature
/// (tokenised description / payee), amount bucket, and the target's type
/// + payee identity. Next time a bank transaction lands with a matching
/// signature + amount bucket, the suggester boosts items that fit the
/// learned pattern.
///
/// The table is intentionally append-friendly with a small uniqueness key
/// (signature × bucket × target type × contact) so repeated identical
/// matches increment TimesConfirmed instead of creating duplicates.
/// </summary>
public class BankReconciliationPattern : TenantEntity
{
    public Guid BankAccountId { get; set; }

    /// <summary>Tokenised lower-case alphanumeric signature derived from the
    /// bank transaction's description + reference + payee fields, joined
    /// by "|". Example: "transfer|kbank|7889". Stable enough to match
    /// against future txns from the same payer / channel.</summary>
    public string DescriptionSignature { get; set; } = null!;

    /// <summary>Amount magnitude bucket — see BankService.Learning helpers.</summary>
    public string AmountBucket { get; set; } = null!;

    /// <summary>Item type the pattern resolves to (Payment / JE / Document).</summary>
    public ReconciliationItemType TargetType { get; set; }

    /// <summary>Counterparty contact when the matched item has one (Receipt
    /// or PaymentVoucher with a customer/vendor). Lets the suggester pre-rank
    /// items from the same contact.</summary>
    public Guid? ContactId { get; set; }

    /// <summary>GL account that typically appears on the matched JE line —
    /// optional, used to break ties between contacts who use multiple
    /// accounts (e.g. one for sales, one for refund-out).</summary>
    public string? TargetAccountCode { get; set; }

    public int TimesConfirmed { get; set; } = 1;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public decimal AvgAmount { get; set; }
    public decimal MinAmount { get; set; }
    public decimal MaxAmount { get; set; }
}

/// <summary>
/// Pairs of (BankTransaction, candidate) the user explicitly REJECTED — so
/// the bulk matcher doesn't keep proposing the same wrong pairing on every
/// rerun. The matcher subtracts these pairs from the candidate pool when
/// building the next proposal; the user can clear an exclusion if they
/// change their mind (currently via re-matching, which auto-removes the
/// row). Storing the rejection reason lets the calibration job downweight
/// the signals that led to the rejected pairing.
/// </summary>
public class BankMatchExclusion : TenantEntity
{
    public Guid BankTransactionId { get; set; }
    public Guid CandidateId { get; set; }
    public string CandidateType { get; set; } = null!;   // "Payment" | "JournalEntry" | "Document"
    public DateTime RejectedAt { get; set; } = DateTime.UtcNow;
    public string? RejectionReason { get; set; }
    public string? RejectedByUserId { get; set; }
}

/// <summary>
/// Per-match audit row inserted at apply time so a disputed reconciliation
/// can be traced back to who clicked Confirm, when, what other candidates
/// the system had shown them, and what confidence score the system gave at
/// the moment of acceptance. Separate from AiFeedbackRecord because that
/// captures the AI/local interaction; THIS captures the human decision.
/// </summary>
public class BankMatchAuditLog : TenantEntity
{
    public Guid BankTransactionId { get; set; }
    public Guid AppliedByUserId { get; set; }
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
    public decimal ConfidenceAtApply { get; set; }
    public string? OutcomeJson { get; set; }         // chosen candidates (id, type, amount)
    public string? AlternativesJson { get; set; }    // top-3 alternatives that were displayed
    public bool WasAiValidated { get; set; }
}

/// <summary>
/// Cheque book — a roll of pre-numbered cheques tied to a bank
/// account. Thai SMEs typically order cheque books in batches of
/// 25 / 50 / 100 numbered sequentially. Tracking the book lets
/// the system enforce sequence integrity (no skipped cheques without
/// a Voided record + flag missing returns).
/// </summary>
public class ChequeBook : TenantEntity
{
    public Guid BankAccountId { get; set; }
    public BankAccount BankAccount { get; set; } = null!;

    /// <summary>Cheque book identifier — typically a 4-6 digit serial
    /// the bank prints on the cover (separate from per-cheque number).</summary>
    public string BookNumber { get; set; } = null!;

    /// <summary>First cheque number in the book (e.g. 1001).</summary>
    public long StartChequeNumber { get; set; }

    /// <summary>Last cheque number in the book (e.g. 1050 for a 50-leaf book).</summary>
    public long EndChequeNumber { get; set; }

    /// <summary>Next number the issuer should use — bumps as Cheques
    /// are written. When NextNumber > EndChequeNumber, book is
    /// exhausted and a new book must be opened.</summary>
    public long NextNumber { get; set; }

    public DateTime ReceivedFromBankAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExhaustedAt { get; set; }
    public string? Notes { get; set; }

    public ICollection<Cheque> Cheques { get; set; } = new List<Cheque>();
}

/// <summary>
/// Individual cheque — either issued by us (outbound, AP-side) or
/// received from a customer (inbound, AR-side). Outbound cheques are
/// linked to ChequeBook (so we can verify they're in sequence);
/// inbound cheques are not (the issuing bank owns the sequence).
///
/// State machine: Issued → Cleared | Bounced | Voided.
/// Inbound: DepositedPending → Cleared | Bounced.
/// </summary>
public class Cheque : TenantEntity
{
    /// <summary>Outbound (issued by us): non-null. Inbound (received
    /// from customer): null + IssuingBankName populated instead.</summary>
    public Guid? ChequeBookId { get; set; }
    public ChequeBook? ChequeBook { get; set; }

    /// <summary>Cheque serial as printed (long because some Thai
    /// banks use 10-digit serials).</summary>
    public long ChequeNumber { get; set; }

    public DateTime ChequeDate { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "THB";

    /// <summary>Outbound: counterparty we wrote the cheque to.
    /// Inbound: customer who handed us the cheque.</summary>
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }

    /// <summary>Inbound only — name of the bank that issued the cheque
    /// to the customer (since it's NOT our bank).</summary>
    public string? IssuingBankName { get; set; }

    /// <summary>Optional link to the Payment record that recognises
    /// the cheque in the ledger. Outbound: a PaymentVoucher's payment.
    /// Inbound: a Receipt's payment.</summary>
    public Guid? PaymentId { get; set; }
    public Payment? Payment { get; set; }

    public ChequeStatus Status { get; set; } = ChequeStatus.Issued;

    /// <summary>When the cheque cleared the bank (outbound) or
    /// the deposit cleared into our account (inbound). Stamps the
    /// audit trail + drives "cheques outstanding" report.</summary>
    public DateTime? ClearedAt { get; set; }

    /// <summary>Free-text Bounce reason from the bank (e.g.
    /// "เงินในบัญชีไม่พอ", "Stop payment").</summary>
    public string? BounceReason { get; set; }

    /// <summary>Cheque this one replaces (when an earlier cheque
    /// was bounced + we re-issued). Lets AP team trace the chain.</summary>
    public Guid? ReplacesChequeId { get; set; }
    public Cheque? ReplacesCheque { get; set; }

    public bool IsInbound { get; set; }       // true = received from customer; false = issued by us
    public string? Notes { get; set; }

    /// <summary>Inbound only — our bank account that the customer's
    /// cheque is deposited into. Drives the bank-balance credit when
    /// the deposit clears (outbound cheques use ChequeBook.BankAccount
    /// instead). Null = bank-balance side intentionally skipped (rare,
    /// e.g. cheque endorsed forward without depositing).</summary>
    public Guid? DepositBankAccountId { get; set; }
    public BankAccount? DepositBankAccount { get; set; }
    /// <summary>Project the cheque is associated with (cost project
    /// on outbound; revenue project on inbound).</summary>
    public Guid? ProjectId { get; set; }
}

/// <summary>
/// Stamp duty (อากรแสตมป์) record per ประมวลรัษฎากร §103-105.
/// Common dutiable instruments for Thai SMEs:
///   • Rental contract — 0.1% (กลุ่ม 1)
///   • Loan agreement — 0.05% (กลุ่ม 5, cap 10,000)
///   • Hire-of-work contract — 0.1% (กลุ่ม 4, cap 10,000)
///   • Power of attorney — 30 baht flat
/// One row per dutiable instrument; status tracks payment to RD.
/// </summary>
public class StampDutyRecord : TenantEntity
{
    /// <summary>Internal reference number for tracking.</summary>
    public string Reference { get; set; } = null!;

    /// <summary>Counterparty on the instrument (e.g. lessor on a
    /// rental contract). Optional — some duties are unilateral.</summary>
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }

    /// <summary>Optional link to the contract document this duty
    /// covers — typically a RevenueContract or a draft Document.</summary>
    public string? InstrumentEntityType { get; set; }
    public Guid? InstrumentEntityId { get; set; }

    /// <summary>Which RD schedule group this instrument falls under
    /// (1=Rental, 4=Hire-of-work, 5=Loan, 7=PoA, etc.).</summary>
    public int RdScheduleNumber { get; set; }

    public string InstrumentType { get; set; } = null!;        // "Rental", "Loan", "HireOfWork", etc.

    /// <summary>Underlying instrument value — drives ad-valorem rate.</summary>
    public decimal InstrumentValue { get; set; }

    /// <summary>Computed duty amount (after applying rate + cap).
    /// Mutable — admin can override when RD assesses differently.</summary>
    public decimal DutyAmount { get; set; }

    public DateTime InstrumentDate { get; set; }

    /// <summary>"Cash" (paid by Or.Sor.6 / Or.Sor.4 voucher),
    /// "Stamp" (physical stamps affixed), "ESD" (electronic via
    /// RD e-Stamp system). ESD is the modern default for SMEs.</summary>
    public string PaymentMethod { get; set; } = "ESD";

    public DateTime? PaidAt { get; set; }
    public string? RdReceiptNumber { get; set; }                // for ESD / Or.Sor.4

    public string? Notes { get; set; }

    /// <summary>Project this duty is incurred on (rental contract
    /// for project X, loan for project Y). Flows to the cost JE.</summary>
    public Guid? ProjectId { get; set; }
}

/// <summary>
/// Petty cash float — small cash kept on hand for ad-hoc expenses
/// (postage, taxi, courier). Each tin has a custodian + an imprest
/// amount. Reimbursements top it back to the imprest.
/// </summary>
public class PettyCashFund : TenantEntity
{
    public string Name { get; set; } = null!;
    public Guid? CustodianUserId { get; set; }
    public User? CustodianUser { get; set; }
    public decimal ImprestAmount { get; set; }
    public decimal CurrentBalance { get; set; }
    public Guid? LinkedAccountId { get; set; }
    public ChartOfAccount? LinkedAccount { get; set; }
    public bool IsActive { get; set; } = true;
}

public class PettyCashTransaction : TenantEntity
{
    public Guid FundId { get; set; }
    public PettyCashFund Fund { get; set; } = null!;
    public DateTime TransactionDate { get; set; }
    public decimal Amount { get; set; }
    /// <summary>"Disbursement" | "Replenishment" | "Adjustment".</summary>
    public string Type { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string? ReceiptReference { get; set; }
    public Guid? ExpenseAccountId { get; set; }
    public ChartOfAccount? ExpenseAccount { get; set; }
    public Guid? JournalEntryId { get; set; }
    /// <summary>Project the petty-cash disbursement was charged to.
    /// Flows to the auto-generated JE line's ProjectId so project
    /// cost reports pick it up.</summary>
    public Guid? ProjectId { get; set; }
}

/// <summary>
/// PDPA data subject request — per Personal Data Protection Act
/// B.E. 2562 (พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล). Any data subject (user
/// or customer) can request: Access (download their data), Erasure
/// (right to be forgotten), Rectification (correction), Portability
/// (machine-readable export), Restriction (pause processing).
///
/// The system has 30 days to respond per §32. Tracking the request
/// gives the DPO a queue to action + an audit trail for the OIC.
/// </summary>
public class PdpaDataSubjectRequest : TenantEntity
{
    public string RequestNumber { get; set; } = null!;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Email / phone of the requester — they may not have
    /// an account, so we store the identifier independently.</summary>
    public string RequesterContact { get; set; } = null!;
    public string? RequesterName { get; set; }
    public Guid? LinkedUserId { get; set; }
    public Guid? LinkedContactId { get; set; }

    /// <summary>"Access" | "Erasure" | "Rectification" | "Portability"
    /// | "Restriction" | "Objection".</summary>
    public string RequestType { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>"Pending" → "InProgress" → "Completed" | "Rejected".</summary>
    public string Status { get; set; } = "Pending";

    public DateTime DueBy { get; set; }                  // RequestedAt + 30d per §32
    public DateTime? CompletedAt { get; set; }
    public string? CompletionNote { get; set; }
    public Guid? AssignedDpoUserId { get; set; }
    public string? RejectionReason { get; set; }
}

// ===== PDPA Wave 3: RoPA, Consent, PiiAccessLog, Breach =====

/// <summary>RoPA (Record of Processing Activity) ตาม PDPA ม.39 — บันทึก
/// กิจกรรมการประมวลผลข้อมูลส่วนบุคคลของบริษัท. ต้องมีไว้แสดงเมื่อ PDPC
/// ขอตรวจ. แต่ละ row = 1 กิจกรรม (เช่น "จัดเก็บข้อมูลพนักงานเพื่อจ่ายเงินเดือน",
/// "เก็บลูกค้าเพื่อออกใบเสร็จ"). ต้องระบุ: วัตถุประสงค์, ฐานทางกฎหมาย,
/// ประเภทข้อมูล, ผู้รับ, ระยะเวลาเก็บ.</summary>
public class PdpaProcessingActivity : TenantEntity
{
    /// <summary>วัตถุประสงค์ของการประมวลผล (เช่น "จ่ายเงินเดือนพนักงาน")</summary>
    public string Purpose { get; set; } = null!;
    /// <summary>ฐานทางกฎหมาย — Contract / LegalObligation / LegitimateInterest /
    /// Consent / VitalInterest / PublicInterest (ม.24)</summary>
    public string LegalBasis { get; set; } = null!;
    /// <summary>ประเภทข้อมูล (comma-separated): Name, CitizenId, Email, Phone, BankAccount, Salary</summary>
    public string DataCategories { get; set; } = null!;
    /// <summary>ระยะเก็บ (เช่น "5 ปีหลังจากออกจากงาน")</summary>
    public string RetentionPeriod { get; set; } = null!;
    /// <summary>ผู้รับ/แชร์ข้อมูล (comma-separated): สรรพากร, ประกันสังคม, ธนาคาร, บริษัทแม่</summary>
    public string? Recipients { get; set; }
    public bool TransfersOutsideThailand { get; set; }
    public string? TransferSafeguards { get; set; }
    public string? Notes { get; set; }
    public DateTime LastReviewedAt { get; set; } = DateTime.UtcNow;
    public string? LastReviewedBy { get; set; }
}

/// <summary>Consent record (ม.19, 22) — เก็บความยินยอมการใช้ข้อมูล. ต้อง
/// บันทึก purpose + version + evidence (hash ของหลักฐาน) เพื่อพิสูจน์ตอน
/// PDPC ตรวจ. ถอนความยินยอม → WithdrawnAt มีค่า → หยุดใช้ข้อมูลทันที.</summary>
public class PdpaConsentRecord : TenantEntity
{
    /// <summary>เจ้าของข้อมูล (User หรือ Contact); ระบบ link อย่างใดอย่างหนึ่ง</summary>
    public Guid? SubjectUserId { get; set; }
    public Guid? SubjectContactId { get; set; }
    public string? SubjectContact { get; set; }        // email/phone fallback
    public string Purpose { get; set; } = null!;       // ส่งโฆษณา / รับข่าวสาร / แชร์ไปบริษัทแม่
    public string PolicyVersion { get; set; } = "1.0";
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    public DateTime? WithdrawnAt { get; set; }
    public string? WithdrawnReason { get; set; }
    /// <summary>SHA-256 hash ของหลักฐาน (HTML ฟอร์ม / SMS confirmation / email)</summary>
    public string? EvidenceHash { get; set; }
    public string? Channel { get; set; }               // web-form / email / sms / paper
    public string? IpAddress { get; set; }
}

/// <summary>PiiAccessLog (ม.37(4)) — บันทึก who-accessed-what PII event-style.
/// AuditLog เก็บแค่ "การเปลี่ยนแปลง" (write); PiiAccessLog เก็บการ "อ่าน"
/// PII (เช่น HR เปิดดูเลขบัตรประชาชนพนักงาน). เก็บ ≥ 1 ปี.</summary>
public class PdpaPiiAccessLog : TenantEntity
{
    public Guid ActorUserId { get; set; }
    public string ActorEmail { get; set; } = null!;
    /// <summary>Subject entity: "Employee", "Contact", "User"</summary>
    public string SubjectType { get; set; } = null!;
    public Guid SubjectId { get; set; }
    /// <summary>Field name ที่อ่าน (เช่น "CitizenId", "BankAccountNumber") —
    /// comma-separated ถ้าหลาย field ในการเปิดเดียวกัน</summary>
    public string FieldName { get; set; } = null!;
    public string Operation { get; set; } = "Read";    // Read | Export | View-Masked
    public string Purpose { get; set; } = null!;       // เหตุผลการเข้าถึง
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

/// <summary>PDPA breach incident (ม.37(4)) — บันทึกเหตุการณ์ข้อมูลรั่ว/
/// ถูกเข้าถึงโดยมิชอบ. ต้องแจ้ง PDPC ภายใน 72 ชม. นับจากตรวจพบ; ระบบ
/// คำนวณ NotifyPdpcDueBy = DetectedAt + 72h + alert ถ้าใกล้/เกินกำหนด.</summary>
public class PdpaBreachIncident : TenantEntity
{
    public string IncidentNumber { get; set; } = null!;
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public DateTime NotifyPdpcDueBy { get; set; }      // = DetectedAt + 72h
    public string Severity { get; set; } = "Medium";   // Low / Medium / High / Critical
    public string Description { get; set; } = null!;
    /// <summary>ประเภทข้อมูลที่รั่ว (comma): Name, CitizenId, BankAccount, Salary</summary>
    public string AffectedDataCategories { get; set; } = null!;
    public int? AffectedSubjectsCount { get; set; }
    /// <summary>"Detected" → "Investigating" → "NotifiedPdpc" → "NotifiedSubjects" → "Closed"</summary>
    public string Status { get; set; } = "Detected";
    public DateTime? PdpcNotifiedAt { get; set; }
    public string? PdpcReferenceNumber { get; set; }
    public DateTime? SubjectsNotifiedAt { get; set; }
    public string? Mitigation { get; set; }
    public string? RootCause { get; set; }
    public Guid? ReportedByUserId { get; set; }
    public DateTime? ClosedAt { get; set; }
}

/// <summary>
/// Bill of Materials (BOM) — for SMEs that manufacture or assemble.
/// One parent Product (the finished good) maps to N component products
/// with per-unit quantities. Used by production orders to:
///   • Backflush components on completion (consume components,
///     produce 1 parent at the cumulative cost).
///   • Forward-flush check (does on-hand inventory have enough
///     components to build N units of the parent?).
/// </summary>
public class BillOfMaterials : TenantEntity
{
    public Guid ParentProductId { get; set; }
    public Product ParentProduct { get; set; } = null!;
    public string Version { get; set; } = "v1";
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public ICollection<BomLine> Lines { get; set; } = new List<BomLine>();
}

public class BomLine : BaseEntity
{
    public Guid BomId { get; set; }
    public BillOfMaterials Bom { get; set; } = null!;
    public Guid ComponentProductId { get; set; }
    public Product ComponentProduct { get; set; } = null!;
    /// <summary>Quantity of THIS component to make 1 unit of parent.</summary>
    public decimal QuantityPerParent { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Vendor portal access token — gives a counterparty (vendor or
/// customer) a magic-link to log into a minimal "view your POs +
/// upload invoices + check payment status" UI WITHOUT requiring
/// them to be a full User in the tenant.
///
/// Token rotation: TokenHash stored (SHA-256), raw token shown to
/// vendor exactly once at issuance. ExpiresAt + RevokedAt let the AP
/// admin invalidate without deleting the audit trail.
/// </summary>
public class VendorPortalToken : TenantEntity
{
    /// <summary>SHA-256 of the raw token. We never store the raw value.</summary>
    public string TokenHash { get; set; } = null!;

    /// <summary>The counterparty this token grants access for —
    /// scopes every read/write to documents involving this contact.</summary>
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = null!;

    /// <summary>"Vendor" (sees their POs + bills + payment status)
    /// or "Customer" (sees their invoices + receipts).</summary>
    public string Role { get; set; } = "Vendor";

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public Guid? IssuedByUserId { get; set; }
    public User? IssuedByUser { get; set; }

    /// <summary>Optional email/phone vendor uses to claim the
    /// magic-link — also displays on audit log.</summary>
    public string? RecipientEmail { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Consignment stock — inventory we hold but don't own (vendor's
/// goods on our shelves; we pay only when sold) OR inventory at
/// a customer's location that we still own (we recognise the sale
/// only on consumption). Tracking who owns the goods lets the
/// balance sheet exclude consigned stock from our assets +
/// pay-on-consumption avoids upfront AP.
/// </summary>
public class ConsignmentRecord : TenantEntity
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid ContactId { get; set; }                    // consignor or consignee
    public Contact Contact { get; set; } = null!;
    /// <summary>"Inbound" = vendor's goods at our location.
    /// "Outbound" = our goods at customer's location.</summary>
    public string Direction { get; set; } = null!;
    public decimal QuantityOnHand { get; set; }
    public decimal? AgreedUnitPrice { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ReturnedOrSettledAt { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Production order — drives the backflush workflow:
///   1. PlannedQty units of ParentProduct ordered.
///   2. System checks BOM + on-hand component stock; flags if any
///      component would go negative.
///   3. On completion: consume components × PlannedQty per the BOM
///      (outbound stock movements), produce CompletedQty parents at
///      the cumulative-component-cost (inbound to parent at WAC).
///   4. JE: Debit Finished Goods, Credit Raw Materials.
/// </summary>
public class ProductionOrder : TenantEntity
{
    public string OrderNumber { get; set; } = null!;
    public Guid ParentProductId { get; set; }
    public Product ParentProduct { get; set; } = null!;
    public Guid BomId { get; set; }
    public BillOfMaterials Bom { get; set; } = null!;
    public decimal PlannedQty { get; set; }
    public decimal CompletedQty { get; set; }
    public DateTime PlannedStartAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>"Planned" → "Released" → "Completed" → "Closed" |
    /// "Cancelled".</summary>
    public string Status { get; set; } = "Planned";
    public decimal CumulativeComponentCost { get; set; }   // total cost backflushed
    public Guid? JournalEntryId { get; set; }              // FG / RM posting
    public string? Notes { get; set; }
}
