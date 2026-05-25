using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IAccountingService
{
    // Chart of Accounts
    Task<AccountResponse> CreateAccountAsync(Guid companyId, CreateAccountRequest request);
    Task<List<AccountResponse>> GetAccountsAsync(Guid companyId, AccountType? type = null);
    Task<List<AccountResponse>> GetPaymentChannelAccountsAsync(Guid companyId);
    Task<AccountResponse> UpdateAccountAsync(Guid companyId, Guid accountId, UpdateAccountRequest request);
    Task SeedDefaultAccountsAsync(Guid companyId);
    Task SeedDefaultAccountsAsync(Guid companyId, BusinessType businessType);
    Task SeedDefaultAccountsAsync(Guid companyId, BusinessType businessType, IndustryType industryType);
    /// <summary>ผังบัญชีที่บริษัทใหม่จะได้รับ — รวมการปรับแต่งผังต้นแบบของแอดมินแล้ว
    /// (ใช้ตรรกะเดียวกับการ seed จริง เพื่อให้ตัวอย่างตรงกับของจริงเสมอ)</summary>
    Task<List<Accounting.Services.ChartOfAccountTemplates.AccountTemplate>> GetSeedTemplatePreviewAsync(
        BusinessType businessType, IndustryType industryType);

    // Journal Entries
    Task<JournalEntryResponse> CreateJournalEntryAsync(Guid companyId, CreateJournalEntryRequest request, string createdBy);
    Task<JournalEntryResponse> GetJournalEntryAsync(Guid companyId, Guid entryId);
    Task<PagedResponse<JournalEntryResponse>> GetJournalEntriesAsync(Guid companyId, PagedRequest request, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, string? journalType = null, Guid? dimensionId = null, Guid? branchId = null, Guid? projectId = null, string? tag = null, Guid? sourceDocumentId = null, string? sourceDocumentNumber = null);
    Task<JournalEntryResponse> PostJournalEntryAsync(Guid companyId, Guid entryId);
    Task<JournalEntryResponse> UpdateJournalEntryAsync(Guid companyId, Guid entryId, UpdateJournalEntryRequest request, string updatedBy);
    Task VoidJournalEntryAsync(Guid companyId, Guid entryId);
    Task DeleteJournalEntryAsync(Guid companyId, Guid entryId);
    Task<JournalEntryResponse> ReverseJournalEntryAsync(Guid companyId, Guid entryId, DateTime? reversalDate = null, string? description = null, bool systemTriggered = false);
    Task<CorrectJournalEntryResponse> CorrectJournalEntryAsync(Guid companyId, Guid entryId, string createdBy);
    Task<int> BatchVoidJournalEntriesAsync(Guid companyId, List<Guid> entryIds);
    Task<int> BatchDeleteJournalEntriesAsync(Guid companyId, List<Guid> entryIds);
    Task<int> BatchPostJournalEntriesAsync(Guid companyId);

    // Period closing — Task 1 of ERP upgrade
    Task<Models.Entities.FiscalPeriod> SoftClosePeriodAsync(Guid companyId, Guid periodId, string userId);
    Task<Models.Entities.FiscalPeriod> ReopenPeriodAsync(Guid companyId, Guid periodId, string userId);
    Task<Models.Entities.YearEndClosing> YearEndCloseAsync(Guid companyId, int fiscalYear, Guid retainedEarningsAccountId, DateTime? closingDate, string userId);
    Task<int> RollOpeningBalancesAsync(Guid companyId, int year, string userId);

    // General Ledger
    Task<GeneralLedgerResponse> GetGeneralLedgerAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? accountId = null, Guid? dimensionId = null, Guid? branchId = null, Guid? projectId = null);
    Task<object> GetGlDebugAsync(Guid companyId, DateTime? fromDate = null, DateTime? toDate = null);
    Task<int> RebuildMissingLinesAsync(Guid companyId);
    Task<int> RepairBuddhistDatesAsync(Guid companyId);

    // Reports
    Task<TrialBalanceResponse> GetTrialBalanceAsync(Guid companyId, DateTime asOfDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null);
    Task<BalanceSheetResponse> GetBalanceSheetAsync(Guid companyId, DateTime asOfDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null);
    Task<ProfitAndLossResponse> GetProfitAndLossAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null);
    Task<CashFlowStatementResponse> GetCashFlowStatementAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null);

    // Fiscal Period
    Task<FiscalPeriodResponse> CreateFiscalPeriodAsync(Guid companyId, CreateFiscalPeriodRequest request);
    Task<List<FiscalPeriodResponse>> GetFiscalPeriodsAsync(Guid companyId);
    /// <summary>สร้างงวดบัญชีรายเดือนที่ขาดทั้งปีในคราวเดียว — คืนจำนวนที่สร้าง</summary>
    Task<int> EnsureFiscalYearPeriodsAsync(Guid companyId, int year);
    Task CloseFiscalPeriodAsync(Guid companyId, Guid periodId);
    Task<FiscalPeriodResponse> UpdateFiscalPeriodAsync(Guid companyId, Guid periodId, CreateFiscalPeriodRequest request);
    Task DeleteFiscalPeriodAsync(Guid companyId, Guid periodId);
}
