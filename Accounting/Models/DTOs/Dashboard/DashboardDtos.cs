using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Dashboard;

// ===== Dashboard KPIs =====
public record DashboardResponse(
    DashboardKpis Kpis,
    CashFlowSummary CashFlow,
    List<RevenueTrend> RevenueTrends,
    List<ExpenseTrend> ExpenseTrends,
    List<TopCustomer> TopCustomers,
    List<TopExpenseCategory> TopExpenseCategories,
    List<OverdueInvoice> OverdueInvoices,
    List<UpcomingPayable> UpcomingPayables,
    BankBalanceSummary BankBalances,
    DashboardSubscriptionSummary? Subscription,
    VatWhtSummary? TaxSummary = null);

public record VatWhtSummary(
    decimal OutputVat,
    decimal InputVat,
    decimal NetVat,
    decimal TotalWht,
    int WhtCertificateCount,
    string CurrentPeriod);

public record DashboardKpis(
    decimal TotalRevenue,
    decimal TotalExpenses,
    decimal NetIncome,
    decimal TotalReceivables,
    decimal TotalPayables,
    decimal CashBalance,
    decimal BankBalance,
    int TotalInvoices,
    int OverdueInvoices,
    int PendingApprovals,
    decimal RevenueGrowthPercent,
    decimal ExpenseGrowthPercent,
    // Per-type breakdown — required by Thai law: ใบแจ้งหนี้ ≠ ใบกำกับภาษี
    // (Revenue Code §86 governs ใบกำกับภาษี with strict format/issuance rules)
    int InvoiceCount = 0,
    int TaxInvoiceCount = 0);

public record CashFlowSummary(
    decimal OpeningBalance,
    decimal TotalInflows,
    decimal TotalOutflows,
    decimal ClosingBalance,
    List<CashFlowItem> Inflows,
    List<CashFlowItem> Outflows);

public record CashFlowItem(
    string Category,
    decimal Amount);

public record RevenueTrend(
    int Year,
    int Month,
    string MonthName,
    decimal Amount);

public record ExpenseTrend(
    int Year,
    int Month,
    string MonthName,
    decimal Amount);

public record TopCustomer(
    Guid ContactId,
    string ContactName,
    decimal TotalAmount,
    int InvoiceCount);

public record TopExpenseCategory(
    string AccountCode,
    string AccountName,
    decimal Amount,
    decimal Percentage);

public record OverdueInvoice(
    Guid DocumentId,
    string DocumentNumber,
    string ContactName,
    decimal TotalAmount,
    decimal BalanceDue,
    DateTime DueDate,
    int DaysOverdue);

public record UpcomingPayable(
    Guid DocumentId,
    string DocumentNumber,
    string ContactName,
    decimal TotalAmount,
    decimal BalanceDue,
    DateTime DueDate,
    int DaysUntilDue);

public record BankBalanceSummary(
    decimal TotalBalance,
    List<BankBalanceItem> Accounts);

public record BankBalanceItem(
    Guid BankAccountId,
    string AccountName,
    string BankName,
    string Currency,
    decimal Balance);

public record DashboardSubscriptionSummary(
    SubscriptionPlan Plan,
    SubscriptionStatus Status,
    DateTime? EndDate,
    int DaysRemaining,
    int DocumentsUsed,
    int DocumentsLimit,
    int UsersCount,
    int UsersLimit);

public record DashboardRequest(
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int TrendMonths = 6);
