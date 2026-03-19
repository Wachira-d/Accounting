using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;

namespace Accounting.Services.Interfaces;

public interface IAccountingService
{
    // Chart of Accounts
    Task<AccountResponse> CreateAccountAsync(Guid companyId, CreateAccountRequest request);
    Task<List<AccountResponse>> GetAccountsAsync(Guid companyId);
    Task<AccountResponse> UpdateAccountAsync(Guid companyId, Guid accountId, UpdateAccountRequest request);
    Task SeedDefaultAccountsAsync(Guid companyId);

    // Journal Entries
    Task<JournalEntryResponse> CreateJournalEntryAsync(Guid companyId, CreateJournalEntryRequest request, string createdBy);
    Task<JournalEntryResponse> GetJournalEntryAsync(Guid companyId, Guid entryId);
    Task<PagedResponse<JournalEntryResponse>> GetJournalEntriesAsync(Guid companyId, PagedRequest request);
    Task<JournalEntryResponse> PostJournalEntryAsync(Guid companyId, Guid entryId);
    Task VoidJournalEntryAsync(Guid companyId, Guid entryId);

    // Reports
    Task<TrialBalanceResponse> GetTrialBalanceAsync(Guid companyId, DateTime asOfDate);
    Task<BalanceSheetResponse> GetBalanceSheetAsync(Guid companyId, DateTime asOfDate);
    Task<ProfitAndLossResponse> GetProfitAndLossAsync(Guid companyId, DateTime fromDate, DateTime toDate);
    Task<CashFlowStatementResponse> GetCashFlowStatementAsync(Guid companyId, DateTime fromDate, DateTime toDate);

    // Fiscal Period
    Task<FiscalPeriodResponse> CreateFiscalPeriodAsync(Guid companyId, CreateFiscalPeriodRequest request);
    Task<List<FiscalPeriodResponse>> GetFiscalPeriodsAsync(Guid companyId);
    Task CloseFiscalPeriodAsync(Guid companyId, Guid periodId);
}
