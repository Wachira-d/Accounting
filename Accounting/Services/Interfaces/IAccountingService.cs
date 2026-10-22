using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IAccountingService
{
    // Chart of Accounts
    Task<AccountResponse> CreateAccountAsync(Guid companyId, CreateAccountRequest request);
    Task<List<AccountResponse>> GetAccountsAsync(Guid companyId, AccountType? type = null);
    Task<List<PaymentChannelAccountResponse>> GetPaymentChannelAccountsAsync(Guid companyId, bool includeBank = false);
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
    /// <summary>ด่านเข้มของใบสำคัญที่ผู้ใช้บันทึกเอง (ผังเปิดใช้/ไม่ใช่บัญชีคุม + สาขา/มิติ/โครงการของบริษัทนี้) — endpoint เรียกก่อน Create</summary>
    Task ValidateManualJournalAsync(Guid companyId, CreateJournalEntryRequest request);
    Task<JournalEntryResponse> GetJournalEntryAsync(Guid companyId, Guid entryId);
    /// <summary>Sensitivity-aware GET — redacts the payload when the user can't see the JE.</summary>
    Task<JournalEntryResponse> GetJournalEntryForUserAsync(Guid companyId, Guid entryId, Guid userId);
    Task<PagedResponse<JournalEntryResponse>> GetJournalEntriesAsync(Guid companyId, PagedRequest request, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, string? journalType = null, Guid? dimensionId = null, Guid? branchId = null, Guid? projectId = null, string? tag = null, Guid? sourceDocumentId = null, string? sourceDocumentNumber = null);
    /// <summary>Sensitivity-aware list — redacts entries the user can't see, preserves paging counts.</summary>
    Task<PagedResponse<JournalEntryResponse>> GetJournalEntriesForUserAsync(Guid companyId, Guid userId, PagedRequest request, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, string? journalType = null, Guid? dimensionId = null, Guid? branchId = null, Guid? projectId = null, string? tag = null, Guid? sourceDocumentId = null, string? sourceDocumentNumber = null);
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
    /// <summary>ยกเลิกการปิดปี (เจ้าของบริษัท — ด่านที่ controller) · ตัวตัดสินเดียว <c>Helpers/YearEndReopenRule</c> · ล็อกเดียวกับปิดปี/ยกยอด/ซ่อม ·
    /// ไม่ผ่าน = <c>YearEndReopenException</c> ก่อนเขียนอะไร</summary>
    Task<YearEndReopenResult> ReopenYearEndAsync(Guid companyId, int fiscalYear, string? reason, bool acknowledgeFiled, string userId);
    /// <summary>ปีที่ปิดแล้ว (ใหม่ → เก่า) + ปีไหนยกเลิกการปิดได้ (ปีล่าสุด)</summary>
    Task<List<YearEndClosingSummary>> GetYearEndClosingsAsync(Guid companyId);
    /// <summary>รายงานอ่านอย่างเดียว: ยอดยกมาจากการปิดปีที่เก็บไว้ ≠ ยอดที่ควรเป็น (บั๊กนับซ้ำก่อน 2026-10-15 · Helpers/OpeningRoll)</summary>
    Task<List<OpeningRollCheckYear>> FindOpeningRollMismatchesAsync(Guid companyId);
    /// <summary>ซ่อมเฉพาะปีที่เลือก + audit chain ทุกบัญชี · ไม่มีงานไหนเรียกอัตโนมัติ</summary>
    Task<OpeningRollRepairResult> RepairOpeningRollAsync(Guid companyId, IReadOnlyCollection<int> years, string actor);
    /// <summary>รายงานอ่านอย่างเดียว: ผังที่นำเข้าแล้วยังไม่มีบัญชีแม่ (Helpers/ChartParentLink)</summary>
    Task<List<Accounting.Services.Implementations.ChartParentLinkSuspect>> FindUnlinkedAccountsAsync(Guid companyId);
    /// <summary>ผูกบัญชีแม่เฉพาะผังที่เลือก + audit chain</summary>
    Task<Accounting.Services.Implementations.ChartParentLinkRepairResult> RepairAccountParentLinksAsync(Guid companyId, IReadOnlyCollection<Guid> accountIds, string actor);
    /// <summary>รายงานอ่านอย่างเดียว: ผังของผู้ใช้ที่อยู่ใต้ผังลงรายการของแม่แบบ (ผลของการผูกรอบแรก — Helpers/ChartParentLink.PlanTemplateLeafRescue)</summary>
    Task<List<Accounting.Services.Implementations.TemplateLeafChildSuspect>> FindTemplateLeafChildrenAsync(Guid companyId);
    /// <summary>ย้ายผังที่เลือกไปหัวบัญชีถัดไป + ปรับระดับลูกหลาน + audit chain · เจ้าของบริษัทเท่านั้น (ผู้เรียกตรวจ)</summary>
    Task<Accounting.Services.Implementations.ChartParentLinkRepairResult> RescueTemplateLeafChildrenAsync(Guid companyId, IReadOnlyCollection<Guid> accountIds, string actor);
    /// <summary>กู้คืนผังที่ถูกลบแบบ soft (รอบ 4 P2-b — ทางไปต่อของ "ผังที่ถูกลบยังมียอด") · เจ้าของบริษัทเท่านั้น (ผู้เรียกตรวจสิทธิ์) · audit chain</summary>
    Task<AccountResponse> RestoreAccountAsync(Guid companyId, Guid accountId, string actor, Guid? newParentId = null);
    /// <summary>ฝ่ายค้านรอบส่งออก รอบ 5 P2-3 — กู้คืนไว้ใต้ผังแม่เดิมได้ไหม + หัวบัญชีที่เสนอ (อ่านอย่างเดียว · ตัวเดียวกับที่ RestoreAccountAsync ใช้)</summary>
    Task<AccountRestorePlan> PlanAccountRestoreAsync(Guid companyId, Guid accountId);

    // General Ledger
    Task<GeneralLedgerResponse> GetGeneralLedgerAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? accountId = null, Guid? dimensionId = null, Guid? branchId = null, Guid? projectId = null);
    /// <summary>บัญชีแยกประเภทตามสิทธิ์ผู้ใช้ — ใบที่สมุดรายวันปิดบัง ถูกรวม/ปิดบังแบบเดียวกัน (Helpers/LedgerRedaction) · หน้า + ไฟล์ใช้ตัวนี้</summary>
    Task<GeneralLedgerResponse> GetGeneralLedgerForUserAsync(Guid companyId, Guid userId, DateTime fromDate, DateTime toDate, Guid? accountId = null, Guid? dimensionId = null, Guid? branchId = null, Guid? projectId = null);
    Task<object> GetGlDebugAsync(Guid companyId, DateTime? fromDate = null, DateTime? toDate = null);
    Task<int> RebuildMissingLinesAsync(Guid companyId);
    Task<int> RepairBuddhistDatesAsync(Guid companyId);

    // Reports
    Task<TrialBalanceResponse> GetTrialBalanceAsync(Guid companyId, DateTime asOfDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null);
    /// <summary>คืน Dictionary&lt;AccountId, signed net balance&gt; ตามวันที่
    /// ใช้ OpeningBalance baseline เดียวกับ TrialBalance — Dashboard /
    /// CashFlow / ExecutiveReports เรียกตัวนี้แทนการ sum JE จาก inception
    /// เพื่อให้ทุกหน้าตรงกันสำหรับบริษัทที่ migrate มา.</summary>
    Task<Dictionary<Guid, decimal>> GetAccountBalanceAsOfAsync(Guid companyId, DateTime asOfDate);
    Task<BalanceSheetResponse> GetBalanceSheetAsync(Guid companyId, DateTime asOfDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null);
    Task<ProfitAndLossResponse> GetProfitAndLossAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null);

    /// <summary>งบกำไรขาดทุนเปรียบเทียบ (ทีม M) — คอลัมน์ต่อเดือน/ไตรมาส/ปีตามรอบบัญชี · ทุกคอลัมน์ = <see cref="GetProfitAndLossAsync"/> ของช่วงนั้น ·
    /// ผลรวมคอลัมน์ = ยอดทั้งงวด · ไม่ระบุช่วง = ต้นรอบบัญชีปัจจุบันถึงวันนี้ · <paramref name="comparePriorYear"/> = เพิ่มช่วงเดียวกันปีก่อน + % เปลี่ยนแปลง</summary>
    Task<PnlComparisonResponse> GetProfitAndLossComparisonAsync(Guid companyId, DateTime? from, DateTime? to,
        Accounting.Helpers.PnlCompareGranularity granularity, bool comparePriorYear, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null);

    /// <summary>drill-down ช่องหนึ่งของงบกำไรขาดทุน — รายการ JE ของผังในช่วง (ตัวกรอง/สิทธิ์เดียวกับบัญชีแยกประเภท · ไม่นับใบปิดบัญชี)
    /// + ลิงก์เอกสารต้นทาง · ผลรวม = ตัวเลขในช่อง</summary>
    Task<PnlDrilldownResponse> GetProfitAndLossDrilldownAsync(Guid companyId, Guid userId, Guid accountId,
        DateTime fromDate, DateTime toDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null,
        int page = 1, int pageSize = 100);

    /// <summary>Lean (Revenue, Expense) totals for a date window — one SQL
    /// GROUP BY, no Lines / Account hydration. Used by the Simple Mode
    /// home KPI cards where the full P&L call was visibly slow.</summary>
    Task<(decimal Revenue, decimal Expense)> GetSnapshotTotalsAsync(Guid companyId, DateTime fromDate, DateTime toDate);

    Task<CashFlowStatementResponse> GetCashFlowStatementAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null);

    // Fiscal Period
    Task<FiscalPeriodResponse> CreateFiscalPeriodAsync(Guid companyId, CreateFiscalPeriodRequest request);
    Task<List<FiscalPeriodResponse>> GetFiscalPeriodsAsync(Guid companyId);
    /// <summary>สร้างงวดบัญชีรายเดือนที่ขาดทั้งปีในคราวเดียว — คืนจำนวนที่สร้าง</summary>
    Task<int> EnsureFiscalYearPeriodsAsync(Guid companyId, int year);
    Task CloseFiscalPeriodAsync(Guid companyId, Guid periodId);
    Task<FiscalPeriodResponse> UpdateFiscalPeriodAsync(Guid companyId, Guid periodId, CreateFiscalPeriodRequest request, bool forceReassignEntries = false);
    Task DeleteFiscalPeriodAsync(Guid companyId, Guid periodId);
}
