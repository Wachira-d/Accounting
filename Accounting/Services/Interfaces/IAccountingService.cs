using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IAccountingService
{
    // Chart of Accounts
    Task<AccountResponse> CreateAccountAsync(Guid companyId, CreateAccountRequest request);
    Task<List<AccountResponse>> GetAccountsAsync(Guid companyId);
    Task<AccountResponse> UpdateAccountAsync(Guid companyId, Guid accountId, UpdateAccountRequest request);
    Task SeedDefaultAccountsAsync(Guid companyId);
    Task SeedDefaultAccountsAsync(Guid companyId, BusinessType businessType);
    Task SeedDefaultAccountsAsync(Guid companyId, BusinessType businessType, IndustryType industryType);

    // Journal Entries
    Task<JournalEntryResponse> CreateJournalEntryAsync(Guid companyId, CreateJournalEntryRequest request, string createdBy);
    Task<JournalEntryResponse> GetJournalEntryAsync(Guid companyId, Guid entryId);
    Task<PagedResponse<JournalEntryResponse>> GetJournalEntriesAsync(Guid companyId, PagedRequest request, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, string? journalType = null, Guid? dimensionId = null, Guid? branchId = null);
    Task<JournalEntryResponse> PostJournalEntryAsync(Guid companyId, Guid entryId);
    Task VoidJournalEntryAsync(Guid companyId, Guid entryId);
    Task DeleteJournalEntryAsync(Guid companyId, Guid entryId);
    Task<JournalEntryResponse> ReverseJournalEntryAsync(Guid companyId, Guid entryId, DateTime? reversalDate = null, string? description = null);
    Task<int> BatchVoidJournalEntriesAsync(Guid companyId, List<Guid> entryIds);
    Task<int> BatchDeleteJournalEntriesAsync(Guid companyId, List<Guid> entryIds);
    Task<int> BatchPostJournalEntriesAsync(Guid companyId);

    // General Ledger
    Task<GeneralLedgerResponse> GetGeneralLedgerAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? accountId = null, Guid? dimensionId = null, Guid? branchId = null);
    Task<object> GetGlDebugAsync(Guid companyId, DateTime? fromDate = null, DateTime? toDate = null);
    Task<int> RebuildMissingLinesAsync(Guid companyId);
    Task<int> RepairBuddhistDatesAsync(Guid companyId);

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
