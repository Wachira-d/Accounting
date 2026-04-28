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
    Task<int> ImportBankStatementAsync(Guid companyId, ImportBankStatementRequest request);
}
