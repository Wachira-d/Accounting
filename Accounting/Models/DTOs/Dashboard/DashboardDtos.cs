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
    string CurrentPeriod,
    // true = ยังไม่ได้สร้างรายงาน ภ.พ.30 ของงวด ⇒ ตัวเลขข้างบนคำนวณดิบจากเอกสาร
    // (ไม่ผ่าน tax point/§82/3/§82/5/undue 11640/CN-DN) — UI ต้องติดป้ายให้ชัด
    // ห้ามปล่อยให้ผู้ใช้เข้าใจว่าเป็นยอดที่จะยื่นจริง
    bool IsEstimate = false);

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
    int UsersLimit,
    /// <summary>ชื่อแพ็กเกจจาก PlanTemplate (แอดมินตั้ง) — `app.html` เคยแปล enum เองด้วยคีย์ที่ไม่ตรง enum เลย</summary>
    string? PlanName = null);

public record DashboardRequest(
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int TrendMonths = 6);
