namespace Accounting.Models.DTOs.FinancialManagement;

// ===== 1. Prepaid Expense =====
public record CreatePrepaidExpenseRequest(
    string Description, DateTime StartDate, DateTime EndDate, int TotalPeriods,
    decimal TotalAmount, Guid PrepaidAccountId, Guid ExpenseAccountId);

public record PrepaidExpenseResponse(
    Guid Id, string ReferenceNo, string Description,
    DateTime StartDate, DateTime EndDate, int TotalPeriods,
    decimal TotalAmount, decimal AmortizedAmount, decimal RemainingAmount,
    string Status, Guid PrepaidAccountId, string PrepaidAccountName,
    Guid ExpenseAccountId, string ExpenseAccountName,
    List<PrepaidScheduleResponse>? Schedules);

public record PrepaidScheduleResponse(
    Guid Id, int PeriodNumber, DateTime ScheduledDate,
    decimal Amount, bool IsProcessed, Guid? JournalEntryId);

// ===== 2. Deposit Management =====
public record CreateDepositRequest(
    string Description, string Direction, string DepositType,
    decimal Amount, DateTime TransactionDate, DateTime? ExpectedReturnDate,
    Guid? ContactId, string? ContactName,
    Guid DepositAccountId, Guid? CashAccountId);

public record DepositRefundRequest(decimal Amount, string? Notes);

public record DepositResponse(
    Guid Id, string ReferenceNo, string Description, string Direction,
    string DepositType, decimal Amount, decimal RefundedAmount,
    decimal RemainingAmount, string Status,
    DateTime TransactionDate, DateTime? ExpectedReturnDate,
    string? ContactName, Guid DepositAccountId, string DepositAccountName,
    List<DepositRefundResponse>? Refunds);

public record DepositRefundResponse(
    Guid Id, DateTime RefundDate, decimal Amount, string? Notes, Guid? JournalEntryId);

// ===== 3. Bad Debt Allowance =====
public record CreateBadDebtAllowanceRequest(
    string Method, string? Notes,
    List<BadDebtAllowanceLineInput>? Lines);

public record BadDebtAllowanceLineInput(
    Guid? ContactId, string? ContactName, string AgingBucket,
    decimal OutstandingAmount, decimal AllowancePercentage);

public record BadDebtAllowanceResponse(
    Guid Id, string ReferenceNo, DateTime AllowanceDate, string Method,
    decimal TotalReceivable, decimal AllowanceAmount,
    decimal PreviousAllowance, decimal AdjustmentAmount,
    string Status, string? Notes, Guid? JournalEntryId,
    List<BadDebtAllowanceLineResponse>? Lines);

public record BadDebtAllowanceLineResponse(
    Guid? ContactId, string? ContactName, string AgingBucket,
    decimal OutstandingAmount, decimal AllowancePercentage, decimal AllowanceAmount);

// ===== 4. Inventory Obsolescence =====
public record CreateInventoryObsolescenceRequest(string Method, string? Notes);

public record InventoryObsolescenceResponse(
    Guid Id, string ReferenceNo, DateTime AllowanceDate, string Method,
    decimal TotalInventoryValue, decimal AllowanceAmount,
    decimal PreviousAllowance, decimal AdjustmentAmount,
    string Status, string? Notes, Guid? JournalEntryId,
    List<InventoryObsolescenceLineResponse>? Lines);

public record InventoryObsolescenceLineResponse(
    Guid ProductId, string ProductCode, string ProductName,
    string AgingBucket, decimal CurrentStock, decimal StockValue,
    decimal AllowancePercentage, decimal AllowanceAmount);

// ===== 5. Accrued Expense =====
public record CreateAccruedExpenseRequest(
    string Description, string ExpenseType, decimal Amount,
    Guid ExpenseAccountId, Guid AccruedAccountId,
    bool IsRecurring = false, string? RecurringFrequency = null);

public record PayAccruedExpenseRequest(decimal Amount, Guid? CashAccountId);

public record AccruedExpenseResponse(
    Guid Id, string ReferenceNo, string Description, string ExpenseType,
    DateTime AccrualDate, decimal Amount, decimal PaidAmount,
    decimal RemainingAmount, string Status, bool IsRecurring,
    string? RecurringFrequency,
    Guid ExpenseAccountId, string ExpenseAccountName,
    Guid AccruedAccountId, string AccruedAccountName,
    Guid? AccrualJournalId, Guid? PaymentJournalId);

// ===== 6. Corporate Income Tax =====
public record CalculateCITRequest(
    string TaxYear, string TaxPeriod,
    decimal? AddBackItems, decimal? DeductionItems,
    decimal? WithholdingTaxCredit, decimal? PrepaidTaxCredit,
    string? Notes);

public record CITResponse(
    Guid Id, string TaxYear, string TaxPeriod,
    decimal TotalRevenue, decimal TotalExpenses, decimal AccountingProfit,
    decimal AddBackItems, decimal DeductionItems, decimal TaxableProfit,
    decimal TaxRate, decimal TaxAmount,
    decimal WithholdingTaxCredit, decimal PrepaidTaxCredit, decimal NetTaxPayable,
    string Status, string? Notes, Guid? JournalEntryId);

// ===== 7. Profit Appropriation =====
public record CreateProfitAppropriationRequest(
    string FiscalYear, decimal LegalReserve,
    decimal DividendAmount, decimal DividendPerShare, string? Notes);

public record ProfitAppropriationResponse(
    Guid Id, string ReferenceNo, DateTime ApprovalDate, string FiscalYear,
    decimal NetProfit, decimal LegalReserve, decimal DividendAmount,
    decimal RetainedAmount, decimal DividendPerShare,
    string Status, string? Notes,
    Guid? ReserveJournalId, Guid? DividendJournalId);

// ===== 8. Capital Transaction =====
public record CreateCapitalTransactionRequest(
    string TransactionType, DateTime? TransactionDate,
    decimal ShareQuantity, decimal ParValue,
    decimal PaidAmount, string? BoardResolutionRef,
    string? DbrRegistrationRef, string? Notes);

public record CapitalTransactionResponse(
    Guid Id, string ReferenceNo, DateTime TransactionDate,
    string TransactionType, decimal ShareQuantity, decimal ParValue,
    decimal PaidAmount, decimal SharePremium,
    string Status, string? BoardResolutionRef, string? DbrRegistrationRef,
    string? Notes, Guid? JournalEntryId);

// ===== 9. Short-term Investment =====
public record CreateInvestmentRequest(
    string InvestmentType, string Description,
    DateTime PurchaseDate, DateTime? MaturityDate,
    decimal PurchaseCost, decimal InterestRate,
    Guid InvestmentAccountId,
    string? InstitutionName, string? AccountNumber);

public record SellInvestmentRequest(decimal SaleProceeds, DateTime? SaleDate = null);

public record InvestmentResponse(
    Guid Id, string ReferenceNo, string InvestmentType, string Description,
    DateTime PurchaseDate, DateTime? MaturityDate, DateTime? SaleDate,
    decimal PurchaseCost, decimal CurrentValue,
    decimal? SaleProceeds, decimal? GainLoss, decimal InterestRate,
    string Status, string? InstitutionName, string? AccountNumber,
    Guid InvestmentAccountId, string InvestmentAccountName,
    Guid? PurchaseJournalId, Guid? SaleJournalId);
