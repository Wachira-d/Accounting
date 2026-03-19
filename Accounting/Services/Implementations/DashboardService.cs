using Accounting.Data;
using Accounting.Models.DTOs.Dashboard;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class DashboardService : IDashboardService
{
    private readonly AccountingDbContext _db;

    public DashboardService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<DashboardResponse> GetDashboardAsync(Guid companyId, DashboardRequest request)
    {
        var toDate = request.ToDate ?? DateTime.UtcNow;
        var fromDate = request.FromDate ?? new DateTime(toDate.Year, toDate.Month, 1);
        var trendMonths = request.TrendMonths > 0 ? request.TrendMonths : 6;

        var kpis = await GetKpisAsync(companyId, fromDate, toDate);
        var cashFlow = await GetCashFlowSummaryAsync(companyId, fromDate, toDate);
        var revenueTrends = await GetRevenueTrendsAsync(companyId, trendMonths);
        var expenseTrends = await GetExpenseTrendsAsync(companyId, trendMonths);
        var topCustomers = await GetTopCustomersAsync(companyId, fromDate, toDate);
        var topExpenses = await GetTopExpenseCategoriesAsync(companyId, fromDate, toDate);
        var overdueInvoices = await GetOverdueInvoicesAsync(companyId);
        var upcomingPayables = await GetUpcomingPayablesAsync(companyId);
        var bankBalances = await GetBankBalancesAsync(companyId);
        var subscription = await GetSubscriptionSummaryAsync(companyId);

        return new DashboardResponse(kpis, cashFlow, revenueTrends, expenseTrends,
            topCustomers, topExpenses, overdueInvoices, upcomingPayables, bankBalances, subscription);
    }

    public async Task<DashboardKpis> GetKpisAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var postedLines = await _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fromDate
                && l.JournalEntry.EntryDate <= toDate)
            .ToListAsync();

        var totalRevenue = postedLines
            .Where(l => l.Account.AccountType == AccountType.Revenue)
            .Sum(l => l.CreditAmount - l.DebitAmount);

        var totalExpenses = postedLines
            .Where(l => l.Account.AccountType == AccountType.Expense)
            .Sum(l => l.DebitAmount - l.CreditAmount);

        // AR: unpaid invoices
        var receivables = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Paid)
            .SumAsync(d => d.BalanceDue);

        // AP: unpaid purchase invoices
        var payables = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && d.DocumentType == DocumentType.PurchaseInvoice
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Paid)
            .SumAsync(d => d.BalanceDue);

        // Bank balance
        var bankBalance = await _db.BankAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive)
            .SumAsync(a => a.CurrentBalance);

        // Cash accounts balance
        var cashBalance = await _db.JournalEntryLines
            .Include(l => l.Account).Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.Account.AccountCode.StartsWith("111"))
            .SumAsync(l => l.DebitAmount - l.CreditAmount);

        var totalInvoices = await _db.Documents.CountAsync(d =>
            d.CompanyId == companyId
            && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
            && d.DocumentDate >= fromDate && d.DocumentDate <= toDate);

        var overdueCount = await _db.Documents.CountAsync(d =>
            d.CompanyId == companyId && d.Status == DocumentStatus.Overdue);

        var pendingApprovals = await _db.Documents.CountAsync(d =>
            d.CompanyId == companyId && d.Status == DocumentStatus.WaitingApproval);

        // Previous period for growth calculation
        var periodLength = (toDate - fromDate).TotalDays;
        var prevFromDate = fromDate.AddDays(-periodLength);
        var prevToDate = fromDate.AddDays(-1);

        var prevLines = await _db.JournalEntryLines
            .Include(l => l.Account).Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= prevFromDate
                && l.JournalEntry.EntryDate <= prevToDate)
            .ToListAsync();

        var prevRevenue = prevLines.Where(l => l.Account.AccountType == AccountType.Revenue)
            .Sum(l => l.CreditAmount - l.DebitAmount);
        var prevExpenses = prevLines.Where(l => l.Account.AccountType == AccountType.Expense)
            .Sum(l => l.DebitAmount - l.CreditAmount);

        var revenueGrowth = prevRevenue != 0 ? ((totalRevenue - prevRevenue) / prevRevenue) * 100 : 0;
        var expenseGrowth = prevExpenses != 0 ? ((totalExpenses - prevExpenses) / prevExpenses) * 100 : 0;

        return new DashboardKpis(totalRevenue, totalExpenses, totalRevenue - totalExpenses,
            receivables, payables, cashBalance, bankBalance,
            totalInvoices, overdueCount, pendingApprovals,
            Math.Round(revenueGrowth, 2), Math.Round(expenseGrowth, 2));
    }

    public async Task<CashFlowSummary> GetCashFlowSummaryAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var bankTxns = await _db.BankTransactions
            .Where(t => t.CompanyId == companyId
                && t.TransactionDate >= fromDate && t.TransactionDate <= toDate)
            .ToListAsync();

        var deposits = bankTxns.Where(t => t.TransactionType == BankTransactionType.Deposit || t.TransactionType == BankTransactionType.Interest);
        var withdrawals = bankTxns.Where(t => t.TransactionType == BankTransactionType.Withdrawal || t.TransactionType == BankTransactionType.Fee || t.TransactionType == BankTransactionType.Transfer);

        var inflows = deposits.GroupBy(t => t.TransactionType.ToString())
            .Select(g => new CashFlowItem(g.Key, g.Sum(t => t.Amount))).ToList();
        var outflows = withdrawals.GroupBy(t => t.TransactionType.ToString())
            .Select(g => new CashFlowItem(g.Key, g.Sum(t => t.Amount))).ToList();

        var totalIn = inflows.Sum(i => i.Amount);
        var totalOut = outflows.Sum(o => o.Amount);

        var openingBalance = await _db.BankAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive)
            .SumAsync(a => a.CurrentBalance) - totalIn + totalOut;

        return new CashFlowSummary(openingBalance, totalIn, totalOut, openingBalance + totalIn - totalOut, inflows, outflows);
    }

    public async Task<List<RevenueTrend>> GetRevenueTrendsAsync(Guid companyId, int months)
    {
        var now = DateTime.UtcNow;
        var startDate = new DateTime(now.Year, now.Month, 1).AddMonths(-months + 1);

        var lines = await _db.JournalEntryLines
            .Include(l => l.Account).Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= startDate
                && l.Account.AccountType == AccountType.Revenue)
            .ToListAsync();

        var trends = new List<RevenueTrend>();
        for (int i = 0; i < months; i++)
        {
            var monthStart = startDate.AddMonths(i);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            var amount = lines.Where(l => l.JournalEntry.EntryDate >= monthStart && l.JournalEntry.EntryDate <= monthEnd)
                .Sum(l => l.CreditAmount - l.DebitAmount);
            trends.Add(new RevenueTrend(monthStart.Year, monthStart.Month, monthStart.ToString("MMM yyyy"), amount));
        }
        return trends;
    }

    public async Task<List<ExpenseTrend>> GetExpenseTrendsAsync(Guid companyId, int months)
    {
        var now = DateTime.UtcNow;
        var startDate = new DateTime(now.Year, now.Month, 1).AddMonths(-months + 1);

        var lines = await _db.JournalEntryLines
            .Include(l => l.Account).Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= startDate
                && l.Account.AccountType == AccountType.Expense)
            .ToListAsync();

        var trends = new List<ExpenseTrend>();
        for (int i = 0; i < months; i++)
        {
            var monthStart = startDate.AddMonths(i);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            var amount = lines.Where(l => l.JournalEntry.EntryDate >= monthStart && l.JournalEntry.EntryDate <= monthEnd)
                .Sum(l => l.DebitAmount - l.CreditAmount);
            trends.Add(new ExpenseTrend(monthStart.Year, monthStart.Month, monthStart.ToString("MMM yyyy"), amount));
        }
        return trends;
    }

    private async Task<List<TopCustomer>> GetTopCustomersAsync(Guid companyId, DateTime fromDate, DateTime toDate, int top = 10)
    {
        return await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.Status != DocumentStatus.Voided
                && d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .GroupBy(d => new { d.ContactId, d.Contact.Name })
            .Select(g => new TopCustomer(g.Key.ContactId, g.Key.Name, g.Sum(d => d.TotalAmount), g.Count()))
            .OrderByDescending(c => c.TotalAmount)
            .Take(top)
            .ToListAsync();
    }

    private async Task<List<TopExpenseCategory>> GetTopExpenseCategoriesAsync(Guid companyId, DateTime fromDate, DateTime toDate, int top = 10)
    {
        var expenseLines = await _db.JournalEntryLines
            .Include(l => l.Account).Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fromDate && l.JournalEntry.EntryDate <= toDate
                && l.Account.AccountType == AccountType.Expense)
            .GroupBy(l => new { l.Account.AccountCode, l.Account.AccountName })
            .Select(g => new { g.Key.AccountCode, g.Key.AccountName, Amount = g.Sum(l => l.DebitAmount - l.CreditAmount) })
            .OrderByDescending(e => e.Amount)
            .Take(top)
            .ToListAsync();

        var totalExpenses = expenseLines.Sum(e => e.Amount);
        return expenseLines.Select(e => new TopExpenseCategory(
            e.AccountCode, e.AccountName, e.Amount,
            totalExpenses > 0 ? Math.Round((e.Amount / totalExpenses) * 100, 2) : 0)).ToList();
    }

    private async Task<List<OverdueInvoice>> GetOverdueInvoicesAsync(Guid companyId, int top = 20)
    {
        var today = DateTime.UtcNow.Date;
        return await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.DueDate < today
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Paid
                && d.BalanceDue > 0)
            .OrderBy(d => d.DueDate)
            .Take(top)
            .Select(d => new OverdueInvoice(d.Id, d.DocumentNumber, d.Contact.Name,
                d.TotalAmount, d.BalanceDue, d.DueDate!.Value, (int)(today - d.DueDate.Value).TotalDays))
            .ToListAsync();
    }

    private async Task<List<UpcomingPayable>> GetUpcomingPayablesAsync(Guid companyId, int daysAhead = 30)
    {
        var today = DateTime.UtcNow.Date;
        var cutoff = today.AddDays(daysAhead);
        return await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && d.DocumentType == DocumentType.PurchaseInvoice
                && d.DueDate >= today && d.DueDate <= cutoff
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Paid
                && d.BalanceDue > 0)
            .OrderBy(d => d.DueDate)
            .Select(d => new UpcomingPayable(d.Id, d.DocumentNumber, d.Contact.Name,
                d.TotalAmount, d.BalanceDue, d.DueDate!.Value, (int)(d.DueDate.Value - today).TotalDays))
            .ToListAsync();
    }

    private async Task<BankBalanceSummary> GetBankBalancesAsync(Guid companyId)
    {
        var accounts = await _db.BankAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive)
            .Select(a => new BankBalanceItem(a.Id, a.AccountName, a.BankName, a.Currency, a.CurrentBalance))
            .ToListAsync();

        return new BankBalanceSummary(accounts.Sum(a => a.Balance), accounts);
    }

    private async Task<SubscriptionSummary?> GetSubscriptionSummaryAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null) return null;

        var usersCount = await _db.CompanyUsers.CountAsync(cu => cu.CompanyId == companyId);

        return new SubscriptionSummary(sub.Plan, sub.Status, sub.EndDate,
            Math.Max(0, (int)(sub.EndDate - DateTime.UtcNow).TotalDays),
            sub.CurrentMonthDocuments, sub.MaxDocumentsPerMonth,
            usersCount, sub.MaxUsers);
    }
}
