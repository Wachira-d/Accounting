using Accounting.Models.DTOs.FinancialManagement;

namespace Accounting.Services.Interfaces;

public interface IFinancialManagementService
{
    // 1. Prepaid Expenses
    Task<PrepaidExpenseResponse> CreatePrepaidExpenseAsync(Guid companyId, CreatePrepaidExpenseRequest request, string userId);
    Task<List<PrepaidExpenseResponse>> GetPrepaidExpensesAsync(Guid companyId);
    Task<PrepaidExpenseResponse> GetPrepaidExpenseDetailAsync(Guid companyId, Guid id);
    Task<int> ProcessPrepaidAmortizationAsync(Guid companyId, DateTime asOfDate, string userId);

    // 2. Deposit Management
    Task<DepositResponse> CreateDepositAsync(Guid companyId, CreateDepositRequest request, string userId);
    Task<List<DepositResponse>> GetDepositsAsync(Guid companyId, string? direction);
    Task<DepositResponse> RefundDepositAsync(Guid companyId, Guid depositId, DepositRefundRequest request, string userId);

    // 3. Bad Debt Allowance
    Task<BadDebtAllowanceResponse> CreateBadDebtAllowanceAsync(Guid companyId, CreateBadDebtAllowanceRequest request, string userId);
    Task<List<BadDebtAllowanceResponse>> GetBadDebtAllowancesAsync(Guid companyId);
    Task<BadDebtAllowanceResponse> PostBadDebtAllowanceAsync(Guid companyId, Guid id, string userId);

    // 4. Inventory Obsolescence
    Task<InventoryObsolescenceResponse> CreateInventoryObsolescenceAsync(Guid companyId, CreateInventoryObsolescenceRequest request, string userId);
    Task<List<InventoryObsolescenceResponse>> GetInventoryObsolescencesAsync(Guid companyId);
    Task<InventoryObsolescenceResponse> PostInventoryObsolescenceAsync(Guid companyId, Guid id, string userId);

    // 5. Accrued Expenses
    Task<AccruedExpenseResponse> CreateAccruedExpenseAsync(Guid companyId, CreateAccruedExpenseRequest request, string userId);
    Task<List<AccruedExpenseResponse>> GetAccruedExpensesAsync(Guid companyId);
    Task<AccruedExpenseResponse> PayAccruedExpenseAsync(Guid companyId, Guid id, PayAccruedExpenseRequest request, string userId);

    // 6. Corporate Income Tax
    Task<CITResponse> CalculateCITAsync(Guid companyId, CalculateCITRequest request, string userId);
    Task<List<CITResponse>> GetCITListAsync(Guid companyId);
    Task<CITResponse> PostCITAsync(Guid companyId, Guid id, string userId);

    // 7. Profit Appropriation
    Task<ProfitAppropriationResponse> CreateProfitAppropriationAsync(Guid companyId, CreateProfitAppropriationRequest request, string userId);
    Task<List<ProfitAppropriationResponse>> GetProfitAppropriationsAsync(Guid companyId);
    Task<ProfitAppropriationResponse> ApproveProfitAppropriationAsync(Guid companyId, Guid id, string userId);

    // 8. Capital Transactions
    Task<CapitalTransactionResponse> CreateCapitalTransactionAsync(Guid companyId, CreateCapitalTransactionRequest request, string userId);
    Task<List<CapitalTransactionResponse>> GetCapitalTransactionsAsync(Guid companyId);
    Task<CapitalTransactionResponse> CompleteCapitalTransactionAsync(Guid companyId, Guid id, string userId);

    // 9. Short-term Investment
    Task<InvestmentResponse> CreateInvestmentAsync(Guid companyId, CreateInvestmentRequest request, string userId);
    Task<List<InvestmentResponse>> GetInvestmentsAsync(Guid companyId);
    Task<InvestmentResponse> SellInvestmentAsync(Guid companyId, Guid id, SellInvestmentRequest request, string userId);
}
