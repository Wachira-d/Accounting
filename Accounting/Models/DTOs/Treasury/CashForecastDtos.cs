namespace Accounting.Models.DTOs.Treasury;

public record CashForecastRequest(
    int HorizonDays = 30,
    Guid? ProjectId = null,
    bool IncludePayroll = true,
    decimal? MinimumCashFloor = null);

public record CashForecastResponse(
    DateTime AsOfDate,
    DateTime HorizonEndDate,
    Guid? ProjectId,
    string? ProjectName,
    /// <summary>Total bank + cash on hand right now (sum of all
    /// BankAccount balances). Project-scoped forecast still uses the
    /// company-wide opening since cash isn't bucketed per project.</summary>
    decimal OpeningCash,
    decimal ProjectedClosingCash,
    decimal MinimumCashInPeriod,
    DateTime? FirstNegativeDate,
    decimal TotalExpectedIn,
    decimal TotalExpectedOut,
    List<CashForecastDay> ByDay,
    List<CashForecastItem> UpcomingReceipts,   // AR due within window
    List<CashForecastItem> UpcomingPayments,   // AP + payroll due
    List<ArContactSummary> TopDebtors,         // who owes us the most
    List<ApContactSummary> TopCreditors,       // who we owe the most
    List<CashRiskAlert> Risks);

public record CashForecastDay(
    DateTime Date,
    decimal ExpectedIn,
    decimal ExpectedOut,
    decimal NetChange,
    decimal RunningBalance,
    int InflowItemCount,
    int OutflowItemCount);

public record CashForecastItem(
    string Source,                   // "AR" | "AP" | "Payroll" | "Cheque"
    DateTime ExpectedDate,
    decimal Amount,
    Guid? DocumentId,
    string? DocumentNumber,
    Guid? ContactId,
    string? ContactName,
    Guid? ProjectId,
    string? ProjectName,
    string? Note,
    /// <summary>"OnTime" when within terms, "Overdue" when past
    /// DueDate. Lets the UI flag overdue receipts as lower-confidence
    /// cashflow.</summary>
    string TimingStatus,
    int DaysFromToday);

public record ArContactSummary(
    Guid ContactId,
    string ContactName,
    string? TaxId,
    decimal CurrentBalance,          // not yet due
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90Days,
    decimal TotalOutstanding,
    DateTime? OldestDocumentDate,
    int OpenDocumentCount);

public record ApContactSummary(
    Guid ContactId,
    string ContactName,
    string? TaxId,
    decimal CurrentBalance,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90Days,
    decimal TotalOutstanding,
    DateTime? OldestDocumentDate,
    int OpenDocumentCount);

public record CashRiskAlert(
    /// <summary>"Critical" | "Warning" | "Info". Critical = projected
    /// negative cash; Warning = falls below MinimumCashFloor;
    /// Info = concentration risk (one debtor &gt; 50% of receivables).</summary>
    string Level,
    string Title,
    string Message,
    DateTime? RelevantDate,
    decimal? Amount,
    Guid? RelatedId);
