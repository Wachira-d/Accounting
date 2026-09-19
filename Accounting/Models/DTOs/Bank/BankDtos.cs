using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Bank;

public record CreateBankAccountRequest(
    string AccountName,
    string BankName,
    string AccountNumber,
    string? BranchName,
    string AccountType,
    string Currency,
    decimal OpeningBalance,
    Guid? LinkedAccountId);

public record UpdateBankAccountRequest(
    string? AccountName,
    string? BranchName,
    bool? IsActive,
    Guid? LinkedAccountId,
    string? BankName = null,
    string? AccountNumber = null,
    string? AccountType = null,   // Savings / Current / Fixed — เปลี่ยนแล้วระบบย้ายบัญชีย่อยในผังให้ตรงกลุ่ม
    string? Currency = null);

public record BankAccountResponse(
    Guid Id,
    string AccountName,
    string BankName,
    string AccountNumber,
    string? BranchName,
    string AccountType,
    string Currency,
    decimal CurrentBalance,
    Guid? LinkedAccountId,
    string? LinkedAccountCode,
    string? LinkedAccountName,
    bool IsActive,
    // ── ยอดจริงจาก statement ล่าสุด vs ยอดตามบัญชี (GL) ──
    decimal? StatementBalance = null,      // ยอดคงเหลือแถวล่าสุดของ statement ที่นำเข้า
    DateTime? StatementBalanceDate = null, // ยอด ณ วันที่
    DateTime? StatementImportedAt = null,  // นำเข้าเมื่อ
    decimal? BookBalance = null,           // ยอดตามบัญชี (GL จาก JE ที่ post แล้ว)
    decimal? StatementDiff = null);        // StatementBalance − BookBalance (null = ยังไม่เคยนำเข้า)

public record CreateBankTransactionRequest(
    Guid BankAccountId,
    DateTime TransactionDate,
    BankTransactionType TransactionType,
    decimal Amount,
    string? Description,
    string? Reference,
    string? Payee);

public record BankTransactionResponse(
    Guid Id,
    Guid BankAccountId,
    DateTime TransactionDate,
    BankTransactionType TransactionType,
    decimal Amount,
    decimal BalanceAfter,
    string? Description,
    string? Reference,
    string? Payee,
    ReconciliationStatus ReconciliationStatus,
    Guid? MatchedPaymentId,
    // ── "ใครจับคู่ให้ · เพราะอะไร" ต้องเดินทางถึงจอ ────────────────────
    // (`DECISION_AUDIT_2026-09-18.md` §3 D4-8) เดิม DTO ไม่มีช่องเหล่านี้เลย
    // ⇒ หน้าจอบอกไม่ได้ว่าระบบ/AI/คน เป็นคนจับ และบอกไม่ได้ว่าทำไม
    // `ReconciledByLabel` คำนวณที่เซิร์ฟเวอร์ (`Helpers/BankMatchAttribution`)
    // — JS แสดงอย่างเดียว ห้ามถือสำเนาตารางป้าย (F2 ข้อ 5)
    Guid? MatchedJournalEntryId = null,
    // เอกสารที่ถูกเสนอไว้เมื่อยังไม่มีรายการชำระให้ผูก
    Guid? SuggestedDocumentId = null,
    // ค่าดิบในฐาน ("AutoMatch" / GUID ผู้ใช้ / …) — เผื่อ debug/ส่งออก
    string? ReconciledBy = null,
    DateTime? ReconciledAt = null,
    // "System" | "Ai" | "Person" | "Api" | "Unknown" — enum ออกเป็น **ชื่อ** เสมอ
    string? ReconciledByKind = null,
    // ป้ายพร้อมแสดง เช่น "⚙️ ระบบ (จับคู่อัตโนมัติ)" · null = ไม่มีข้อมูล
    string? ReconciledByLabel = null,
    string? MatchRuleCode = null,
    string? MatchReason = null);

public record ReconcileRequest(
    Guid BankTransactionId,
    Guid? MatchedPaymentId,
    Guid? MatchedJournalEntryId);

public record ImportBankStatementRequest(
    Guid BankAccountId,
    string FileFormat,  // "CSV", "EXCEL"/"XLSX"
    string Base64Content,
    bool ForceOverwrite = false);

public record ImportBankStatementResponse(
    int Imported,
    int Skipped,
    int Conflicts,
    List<ImportConflict>? ConflictDetails = null,
    // คำเตือนคุณภาพไฟล์ (เช่น ยอดคงเหลือไม่ต่อเนื่อง = ไฟล์ขาดรายการ) —
    // นำเข้าสำเร็จแต่ผู้ใช้ควรรู้ว่ายอดอาจยังไม่ตรงธนาคาร
    List<string>? Warnings = null);

public record ImportConflict(
    int RowNumber,
    DateTime TransactionDate,
    decimal Amount,
    string? NewDescription,
    string? ExistingDescription,
    Guid ExistingTransactionId);

// ==================== AI Reconciliation ====================

public record AiReconciliationRequest(
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    bool IncludeAggregated = true,
    bool IncludeCashBankCheck = true,
    decimal MinConfidence = 0.5m);

public record AiMatchSuggestion(
    string MatchId,
    string MatchType,  // "OneToOne", "ManyToOne", "OneToMany"
    decimal Confidence,
    string Reasoning,
    BankTransactionResponse BankTransaction,
    List<MatchedAccountingEntry> AccountingEntries,
    decimal BankAmount,
    decimal AccountingTotal,
    decimal Difference);

public record MatchedAccountingEntry(
    string EntryType,  // "Payment", "JournalEntry", "Document"
    Guid EntryId,
    string? EntryNumber,
    DateTime EntryDate,
    decimal Amount,
    string? Description,
    string? ContactName);

public record AiReconciliationResult(
    int TotalBankTransactions,
    int TotalUnmatched,
    int SuggestionsFound,
    int OneToOneMatches,
    int AggregatedMatches,
    List<AiMatchSuggestion> Suggestions,
    List<CashBankDiscrepancy> Discrepancies,
    List<ReconciliationWarning> Warnings,
    ReconciliationSummaryDto Summary);

public record CashBankDiscrepancy(
    Guid JournalEntryId,
    string? JournalNumber,
    DateTime EntryDate,
    decimal Amount,
    string? Description,
    string BookedTo,       // "Cash" or account name
    string SuggestedFix,   // "Should be Bank"
    Guid? SuggestedAccountId,
    string? SuggestedAccountName);

public record ReconciliationWarning(
    string WarningType,  // "Duplicate", "AmountMismatch", "DateGap", "MissingEntry"
    string Message,
    Guid? RelatedTransactionId,
    Guid? RelatedEntryId);

public record ReconciliationSummaryDto(
    decimal BankBalance,
    decimal BookBalance,
    decimal Difference,
    int TotalTransactions,
    int MatchedCount,
    int UnmatchedCount,
    int ExcludedCount,
    decimal UnmatchedDeposits,
    decimal UnmatchedWithdrawals,
    decimal DocumentBalance = 0,
    decimal ReceiptTotal = 0,
    decimal PaymentVoucherTotal = 0);

public record BatchReconcileRequest(
    [property: MaxLength(500)] List<BatchReconcileItem> Items);

public record BatchReconcileItem(
    Guid BankTransactionId,
    string MatchType,  // "Payment", "JournalEntry", "Multiple"
    Guid? MatchedPaymentId,
    Guid? MatchedJournalEntryId,
    List<Guid>? MatchedEntryIds,
    // Audit context — frontend passes the confidence + whether AI validated
    // the row + a serialised list of alternatives that were displayed, so the
    // resulting BankMatchAuditLog row tells a future investigator exactly
    // what the user saw at the moment of acceptance.
    decimal? ConfidenceAtApply = null,
    bool WasAiValidated = false,
    string? AlternativesJson = null);

public record UnmatchRequest(Guid BankTransactionId);

/// <summary>
/// Bulk delete bank transactions. Supply at least one filter:
/// - TransactionIds: explicit list of transaction IDs to delete
/// - BankAccountId + (DateFrom..DateTo): delete all transactions in range
/// - DeleteReconciled: if false (default), reconciled transactions are skipped
/// </summary>
public record DeleteTransactionsRequest(
    List<Guid>? TransactionIds = null,
    Guid? BankAccountId = null,
    DateTime? DateFrom = null,
    DateTime? DateTo = null,
    bool DeleteReconciled = false);

/// <summary>
/// A potential match candidate for a bank transaction (a Payment or a JournalEntry).
/// Returned by the picker UI's "find candidates" endpoint, ranked by Score (0..100).
/// </summary>
public record MatchCandidate(
    string Type,                   // "Payment" or "JournalEntry"
    Guid Id,
    string Number,                 // PaymentNumber or EntryNumber
    DateTime Date,
    decimal Amount,
    string Description,
    string? CounterpartyName,      // customer/supplier name
    string? Reference,
    int DateDiffDays,              // |bankDate - candidateDate|
    decimal AmountDiff,            // |bankAmount - candidateAmount|
    int Score,                     // 0-100 confidence
    string? ScoreReason,
    // Cash/Bank booking: where the money was originally posted in the GL.
    // Helps user see whether the candidate is a cash receipt (won't appear on
    // bank statement) or a bank receipt/transfer (matches a bank txn directly).
    string? DepositLabel = null,   // e.g. "💵 เงินสด" / "🏦 KBANK 064-1-70621-3" / "💵 → 🏦"
    string? DepositCategory = null,  // "Cash" / "Bank" / "Mixed" / "Other"
    // ── สกุลเงิน (`DECISION_AUDIT_2026-09-18.md` §3 D4-8 "FX") ──────────
    // `Amount` คือยอดในสกุลของเอกสาร (สิ่งที่ผู้ใช้เห็นบนใบ) ส่วน
    // `BankCurrencyAmount` คือยอดเดียวกันในสกุลของบัญชีธนาคาร = ตัวที่เอาไป
    // เทียบจริง. null = แปลงไม่ได้ (ต้องโชว์เหตุผลใน `CurrencyNote` แทน
    // **ห้ามซ่อนแถว** ไม่งั้นผู้ใช้ไม่มีทางไปต่อ)
    string? Currency = null,
    decimal? BankCurrencyAmount = null,
    string? CurrencyNote = null);

public record MatchCandidatesResponse(
    Guid BankTransactionId,
    DateTime BankTransactionDate,
    decimal BankTransactionAmount,
    string BankTransactionDescription,
    List<MatchCandidate> Payments,
    List<MatchCandidate> JournalEntries,
    // Diagnostics: how many candidates were excluded because they're already matched
    // to a sibling bank transaction. Useful for the UI to show transparency.
    int ExcludedAlreadyMatchedCount = 0,
    // Total **unmatched** rows that survived the date-window + voided/deleted filters,
    // BEFORE the display cap. The picker may cap displayed rows for performance, so the
    // UI uses these to show "showing N of M" and let the user know if the cap was hit.
    int TotalPaymentsInWindow = 0,
    int TotalJournalEntriesInWindow = 0);

/// <summary>
/// AI auto-suggestion for many-to-one match — finds the subset of candidates whose
/// amounts sum to the bank transaction's amount.
/// </summary>
public record AiMatchSuggestionResponse(
    bool Found,                  // true if a sum-matching subset was found
    string? Type,                // "Payment" or "JournalEntry" — null if Found=false
    List<Guid> SuggestedIds,
    decimal SuggestedTotal,
    decimal BankTransactionAmount,
    decimal Difference,
    string Message);             // Thai-language explanation

public record BankTransactionDetailResponse(
    Guid Id,
    Guid BankAccountId,
    DateTime TransactionDate,
    BankTransactionType TransactionType,
    decimal Amount,
    decimal BalanceAfter,
    string? Description,
    string? Reference,
    string? Payee,
    ReconciliationStatus ReconciliationStatus,
    Guid? MatchedPaymentId,
    Guid? MatchedJournalEntryId,
    string? MatchedPaymentNumber,
    string? MatchedJournalNumber,
    DateTime? ReconciledAt,
    string? ReconciledBy);

// ===== Reconciliation Group (M:N + net-off) =====

/// <summary>
/// Request to create a reconciliation group. Mix bank-transaction IDs with
/// match items (Payment / JournalEntry / Document); each item carries a
/// signed AllocatedAmount so the engine can net-off receipts vs payment
/// vouchers and confirm the group balances.
/// </summary>
public record CreateReconciliationGroupRequest(
    Guid BankAccountId,
    DateTime ReconciledDate,
    List<ReconciliationGroupItemRequest> BankTransactions,   // ItemType implicit = BankTransaction
    List<ReconciliationGroupItemRequest> MatchItems,         // Payment / JournalEntry / Document
    string? Notes = null,
    decimal Tolerance = 0.01m);

public record ReconciliationGroupItemRequest(
    string ItemType,         // "BankTransaction" / "Payment" / "JournalEntry" / "Document"
    Guid ItemId,
    decimal AllocatedAmount, // signed: + inflow, - outflow
    string? Notes = null);

public record ReconciliationGroupResponse(
    Guid Id,
    Guid BankAccountId,
    string GroupNumber,
    DateTime ReconciledDate,
    decimal TotalBankAmount,
    decimal TotalMatchedAmount,
    decimal Difference,
    bool IsBalanced,
    string? Notes,
    string? CreatedBy,
    DateTime CreatedAt,
    List<ReconciliationGroupItemResponse> Items);

public record ReconciliationGroupItemResponse(
    Guid Id,
    string ItemType,
    Guid ItemId,
    string? ItemNumber,         // e.g. PaymentNumber / EntryNumber / DocumentNumber
    string? ItemDescription,
    DateTime? ItemDate,
    decimal AllocatedAmount,
    string? Notes);

public record ReconciliationGroupListItem(
    Guid Id,
    string GroupNumber,
    DateTime ReconciledDate,
    int BankTransactionCount,
    int MatchItemCount,
    decimal TotalBankAmount,
    decimal TotalMatchedAmount,
    bool IsBalanced);

/// <summary>
/// Flat list of every Payment + JournalEntry + Document that's eligible to be
/// pulled into a reconciliation group — i.e. NOT already bound to any of the
/// legacy 1:1 / M:1 fields AND NOT in any existing ReconciliationGroup. Used
/// by the M:N workbench so the operator can see the whole pool, not just
/// candidates near a single bank transaction.
/// </summary>
public record UnmatchedItemsResponse(
    List<UnmatchedItem> Payments,
    List<UnmatchedItem> JournalEntries,
    List<UnmatchedItem> Documents);

public record UnmatchedItem(
    string ItemType,        // "Payment" / "JournalEntry" / "Document"
    Guid Id,
    string Number,
    DateTime Date,
    string? Description,
    decimal Amount,         // unsigned magnitude; sign convention up to the workbench
    string? ContactName,
    /// <summary>ยอด "ขาที่วิ่งผ่านบัญชีธนาคารนี้" ของรายการ แบบมีเครื่องหมาย
    /// (+ เงินเข้า / − เงินออก). สำคัญกับ JE หลายขา: JV เงินเดือน footing
    /// 77,678 แต่ขาที่ออกจากธนาคารจริงคือ 70,110 (ที่เหลือคือ ปกส./ภงด.1
    /// ค้างจ่ายที่ยังไม่ได้จ่ายออก) — ตัวเลขที่ต้องตรงกับสเตทเมนต์คือขาธนาคาร
    /// ไม่ใช่ footing. null = รายการนี้ไม่มีขาที่แตะผังบัญชีของธนาคารนี้เลย
    /// (จับคู่ได้แต่ต้องระวัง — ระบบเตือนใน UI)</summary>
    decimal? BankLegAmount = null);

// ===== Learning-backed suggestion =====

public record LearnedSuggestion(
    string ItemType,
    Guid Id,
    string Number,
    DateTime Date,
    string? Description,
    decimal Amount,
    string? ContactName,
    double Confidence,           // 0..1
    string Reason);              // human-readable, e.g. "พบรูปแบบนี้ 3 ครั้ง · ใช้ล่าสุด 12/04/2025"

public record LearnedSuggestionsResponse(
    List<LearnedSuggestion> Suggestions,
    string Signature,
    string AmountBucket,
    int CandidatePatternCount);
