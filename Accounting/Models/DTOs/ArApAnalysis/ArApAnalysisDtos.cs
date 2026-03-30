namespace Accounting.Models.DTOs.ArApAnalysis;

// ===== Overview Dashboard =====
public record ArApOverviewResponse(
    // KPIs
    decimal TotalReceivables,
    decimal TotalPayables,
    decimal NetPosition, // AR - AP
    decimal Dso, // Days Sales Outstanding
    decimal Dpo, // Days Payable Outstanding
    int OverdueArCount,
    int OverdueApCount,
    decimal OverdueArAmount,
    decimal OverdueApAmount,
    decimal CollectionRate, // % collected within terms
    // Summaries
    List<ArApTrendItem> MonthlyTrend,
    List<ArApTopContact> TopReceivables,
    List<ArApTopContact> TopPayables,
    ArApAgingSummary ArAging,
    ArApAgingSummary ApAging);

public record ArApTrendItem(
    int Year,
    int Month,
    string Period, // "2024-01"
    decimal ArBalance,
    decimal ApBalance,
    decimal ArNew,    // new invoices
    decimal ArPaid,   // collected
    decimal ApNew,    // new purchases
    decimal ApPaid);  // paid out

public record ArApTopContact(
    Guid ContactId,
    string ContactName,
    string? TaxId,
    decimal TotalBalance,
    decimal OverdueAmount,
    int DocumentCount,
    int OverdueCount,
    int MaxAgingDays);

public record ArApAgingSummary(
    decimal Current,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90,
    decimal Total);

// ===== Per-Contact Detail =====
public record ContactArApDetailResponse(
    Guid ContactId,
    string ContactName,
    string? TaxId,
    string? Phone,
    string? Email,
    // Summary KPIs
    decimal TotalOutstanding,
    decimal TotalOverdue,
    decimal TotalPaidLast12Months,
    decimal AvgPaymentDays,
    decimal Dso,
    int TotalDocuments,
    int OverdueDocuments,
    // Credit info
    decimal? CreditLimit,
    decimal? AvailableCredit,
    bool IsOnHold,
    // Aging breakdown
    ArApAgingSummary Aging,
    // Document lists
    List<ContactDocumentItem> OutstandingDocuments,
    List<ContactDocumentItem> RecentPaidDocuments,
    // Payment history
    List<ContactPaymentItem> RecentPayments,
    // Monthly trend
    List<ContactMonthlyItem> MonthlyHistory);

public record ContactDocumentItem(
    Guid DocumentId,
    string DocumentNumber,
    string DocumentType,
    DateTime DocumentDate,
    DateTime? DueDate,
    decimal TotalAmount,
    decimal PaidAmount,
    decimal BalanceDue,
    int AgingDays,
    string Status);

public record ContactPaymentItem(
    Guid PaymentId,
    string PaymentNumber,
    DateTime PaymentDate,
    decimal Amount,
    string? DocumentNumber,
    string? PaymentMethod);

public record ContactMonthlyItem(
    int Year,
    int Month,
    decimal Invoiced,
    decimal Paid,
    decimal Balance);

// ===== Bad Debt Analysis =====
public record BadDebtAnalysisResponse(
    decimal TotalReceivables,
    decimal TotalOverdue,
    decimal EstimatedBadDebt,
    List<BadDebtBucket> Buckets,
    List<BadDebtContactItem> RiskContacts);

public record BadDebtBucket(
    string Label,
    decimal Amount,
    decimal ProvisionRate, // % estimated uncollectible
    decimal EstimatedLoss,
    int DocumentCount);

public record BadDebtContactItem(
    Guid ContactId,
    string ContactName,
    decimal TotalOverdue,
    int MaxAgingDays,
    decimal EstimatedLoss,
    string RiskLevel); // Low, Medium, High, Critical
