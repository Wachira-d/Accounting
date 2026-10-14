using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IBankService
{
    // Bank Accounts
    Task<BankAccountResponse> CreateBankAccountAsync(Guid companyId, CreateBankAccountRequest request);
    /// <param name="activeOnly">true = เฉพาะบัญชีที่เปิดใช้ (ตัวเลือกของฟอร์มเอกสาร/รับ-จ่ายเงิน — PC#18) · false = ทุกบัญชี (หน้าจัดการบัญชีธนาคาร)</param>
    Task<List<BankAccountResponse>> GetBankAccountsAsync(Guid companyId, bool activeOnly = false);
    Task<BankAccountResponse> UpdateBankAccountAsync(Guid companyId, Guid accountId, UpdateBankAccountRequest request);

    // Transactions
    Task<BankTransactionResponse> CreateTransactionAsync(Guid companyId, CreateBankTransactionRequest request);
    Task<PagedResponse<BankTransactionResponse>> GetTransactionsAsync(Guid companyId, Guid bankAccountId, PagedRequest request);

    // Reconciliation
    Task<BankTransactionResponse> ReconcileAsync(Guid companyId, ReconcileRequest request);
    Task<List<BankTransactionResponse>> GetUnreconciledAsync(Guid companyId, Guid bankAccountId);
    Task<List<BankTransactionResponse>> AutoMatchAsync(Guid companyId, Guid bankAccountId);

    // Shared match resolution — single source of truth for "what matched to what".
    Task<ResolvedMatchDto?> ResolveMatchAsync(Guid companyId, Guid txnId);
    Task<List<ResolvedMatchDto>> ResolveMatchesAsync(Guid companyId, Guid bankAccountId);

    // AI Reconciliation
    Task<AiReconciliationResult> AiSmartMatchAsync(Guid companyId, Guid bankAccountId, AiReconciliationRequest request);
    Task<ReconciliationSummaryDto> GetReconciliationSummaryAsync(Guid companyId, Guid bankAccountId);
    Task<List<BankTransactionResponse>> BatchReconcileAsync(Guid companyId, BatchReconcileRequest request);
    Task<BankTransactionResponse> UnmatchTransactionAsync(Guid companyId, UnmatchRequest request);

    // Bank Statement Import
    Task<ImportBankStatementResponse> ImportBankStatementAsync(Guid companyId, ImportBankStatementRequest request);

    /// <summary>
    /// **ตัวเขียน statement ตัวเดียว** (รอบ 205 ทีม C) — ทุกทางเข้าที่สร้างรายการธนาคารจาก statement (หน้า "ธนาคาร" · open-banking file ·
    /// ศูนย์นำเข้าข้อมูล · นำเข้าทั่วไป "banktransactions" · API v1) เรียกที่นี่: กันซ้ำ · ยอดคงเหลือ = ยอดตามธนาคาร · ธุรกรรมเดียว ·
    /// เตือนยอดไม่ต่อเนื่อง · เก็บไฟล์ต้นทาง · จับคู่อัตโนมัติหลังบันทึก · ผลต่อบรรทัดในลำดับเดียวกับ <paramref name="lines"/>
    /// </summary>
    Task<BankStatementWriteResult> ImportParsedStatementRowsAsync(Guid companyId, Guid bankAccountId,
        IReadOnlyList<BankStatementLineInput> lines, BankStatementWriteOptions? options = null, CancellationToken ct = default);

    // Delete Transactions (soft-delete via TenantEntity.IsDeleted)
    Task<int> DeleteTransactionAsync(Guid companyId, Guid transactionId);
    Task<int> DeleteTransactionsAsync(Guid companyId, DeleteTransactionsRequest request);

    // Match candidate picker
    Task<MatchCandidatesResponse> GetMatchCandidatesAsync(Guid companyId, Guid bankTransactionId);
    Task<AiMatchSuggestionResponse> SuggestMatchAsync(Guid companyId, Guid bankTransactionId);

    // M:N reconciliation + net-off (Receipt vs Payment Voucher cancellation)
    Task<ReconciliationGroupResponse> CreateReconciliationGroupAsync(Guid companyId, CreateReconciliationGroupRequest request, string userId);
    Task<ReconciliationGroupResponse> GetReconciliationGroupAsync(Guid companyId, Guid groupId);
    Task<PagedResponse<ReconciliationGroupListItem>> GetReconciliationGroupsAsync(Guid companyId, Guid bankAccountId, PagedRequest request);
    Task UnreconcileGroupAsync(Guid companyId, Guid groupId);
    Task<UnmatchedItemsResponse> GetUnmatchedItemsAsync(Guid companyId, Guid bankAccountId, string? search, DateTime? fromDate, DateTime? toDate);

    // AI learning — pattern memory built from confirmed reconciliations
    Task<LearnedSuggestionsResponse> GetLearnedSuggestionsAsync(Guid companyId, Guid bankTransactionId);
    Task RecordReconciliationPatternsAsync(Guid companyId, Guid groupId,
        IReadOnlyDictionary<Guid, UserChoiceSource>? sourceByItemId = null);

    /// <summary>
    /// Cascade cleanup: when an item that's a member of one or more
    /// ReconciliationGroups becomes invalid (JE reversed, Payment voided,
    /// Document voided/purged), unwind every group it belongs to —
    /// release the bank txns back to Unmatched and soft-delete the group +
    /// its items. Idempotent: if the item isn't in any group, no-op.
    /// </summary>
    Task UnwindGroupsContainingItemAsync(Guid companyId, Models.Entities.ReconciliationItemType itemType, Guid itemId);

    // Reconciliation report — Excel export
    Task<byte[]> ExportReconciliationReportAsync(Guid companyId, Guid bankAccountId, DateTime? fromDate, DateTime? toDate);
}
