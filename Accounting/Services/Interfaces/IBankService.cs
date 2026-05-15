using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Bank;

namespace Accounting.Services.Interfaces;

public interface IBankService
{
    // Bank Accounts
    Task<BankAccountResponse> CreateBankAccountAsync(Guid companyId, CreateBankAccountRequest request);
    Task<List<BankAccountResponse>> GetBankAccountsAsync(Guid companyId);
    Task<BankAccountResponse> UpdateBankAccountAsync(Guid companyId, Guid accountId, UpdateBankAccountRequest request);

    // Transactions
    Task<BankTransactionResponse> CreateTransactionAsync(Guid companyId, CreateBankTransactionRequest request);
    Task<PagedResponse<BankTransactionResponse>> GetTransactionsAsync(Guid companyId, Guid bankAccountId, PagedRequest request);

    // Reconciliation
    Task<BankTransactionResponse> ReconcileAsync(Guid companyId, ReconcileRequest request);
    Task<List<BankTransactionResponse>> GetUnreconciledAsync(Guid companyId, Guid bankAccountId);
    Task<List<BankTransactionResponse>> AutoMatchAsync(Guid companyId, Guid bankAccountId);

    // AI Reconciliation
    Task<AiReconciliationResult> AiSmartMatchAsync(Guid companyId, Guid bankAccountId, AiReconciliationRequest request);
    Task<ReconciliationSummaryDto> GetReconciliationSummaryAsync(Guid companyId, Guid bankAccountId);
    Task<List<BankTransactionResponse>> BatchReconcileAsync(Guid companyId, BatchReconcileRequest request);
    Task<BankTransactionResponse> UnmatchTransactionAsync(Guid companyId, UnmatchRequest request);

    // Bank Statement Import
    Task<ImportBankStatementResponse> ImportBankStatementAsync(Guid companyId, ImportBankStatementRequest request);

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
    Task RecordReconciliationPatternsAsync(Guid companyId, Guid groupId);

    // Reconciliation report — Excel export
    Task<byte[]> ExportReconciliationReportAsync(Guid companyId, Guid bankAccountId, DateTime? fromDate, DateTime? toDate);
}
