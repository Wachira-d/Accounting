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
        var subscription = await GetDashboardSubscriptionSummaryAsync(companyId);

        return new DashboardResponse(kpis, cashFlow, revenueTrends, expenseTrends,
            topCustomers, topExpenses, overdueInvoices, upcomingPayables, bankBalances, subscription);
    }

    public async Task<DashboardKpis> GetKpisAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        // Server-side aggregation by AccountType — avoids loading all lines into memory
        var periodSums = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fromDate
                && l.JournalEntry.EntryDate <= toDate)
            .GroupBy(l => l.Account.AccountType)
            .Select(g => new
            {
                AccountType = g.Key,
                TotalCredit = g.Sum(l => l.CreditAmount),
                TotalDebit = g.Sum(l => l.DebitAmount)
            })
            .ToListAsync();

        var revGroup = periodSums.FirstOrDefault(g => g.AccountType == AccountType.Revenue);
        var expGroup = periodSums.FirstOrDefault(g => g.AccountType == AccountType.Expense);
        var totalRevenue = (revGroup?.TotalCredit ?? 0) - (revGroup?.TotalDebit ?? 0);
        var totalExpenses = (expGroup?.TotalDebit ?? 0) - (expGroup?.TotalCredit ?? 0);

        // Previous period for growth calculation
        var periodLength = (toDate - fromDate).TotalDays;
        var prevFromDate = fromDate.AddDays(-periodLength);
        var prevToDate = fromDate.AddDays(-1);

        var prevSums = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= prevFromDate
                && l.JournalEntry.EntryDate <= prevToDate)
            .GroupBy(l => l.Account.AccountType)
            .Select(g => new
            {
                AccountType = g.Key,
                TotalCredit = g.Sum(l => l.CreditAmount),
                TotalDebit = g.Sum(l => l.DebitAmount)
            })
            .ToListAsync();

        var prevRevGroup = prevSums.FirstOrDefault(g => g.AccountType == AccountType.Revenue);
        var prevExpGroup = prevSums.FirstOrDefault(g => g.AccountType == AccountType.Expense);
        var prevRevenue = (prevRevGroup?.TotalCredit ?? 0) - (prevRevGroup?.TotalDebit ?? 0);
        var prevExpenses = (prevExpGroup?.TotalDebit ?? 0) - (prevExpGroup?.TotalCredit ?? 0);

        // Per Thai GAAP / IFRS, AR/AP is recognized only when the document is issued
        // (Approved or beyond) — Draft documents are internal-only and don't yet
        // create a legal receivable/payable. Excluding Draft prevents inflated balances.
        var arApStatuses = new[] {
            DocumentStatus.Approved, DocumentStatus.Sent,
            DocumentStatus.PartiallyPaid, DocumentStatus.Overdue
        };

        // Sequential queries — DbContext is NOT thread-safe, cannot use Task.WhenAll
        var receivables = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && arApStatuses.Contains(d.Status))
            .SumAsync(d => d.BalanceDue);

        var payables = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && d.DocumentType == DocumentType.PurchaseInvoice
                && arApStatuses.Contains(d.Status))
            .SumAsync(d => d.BalanceDue);

        var bankBalance = await _db.BankAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive)
            .SumAsync(a => a.CurrentBalance);

        // Cash accounts balance — server-side sum, no Include needed
        var cashBalance = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.Account.AccountCode.StartsWith("111"))
            .SumAsync(l => l.DebitAmount - l.CreditAmount);

        // Per Thai law (§86 Revenue Code), ใบแจ้งหนี้ and ใบกำกับภาษี are legally
        // distinct documents. Count separately for transparency, sum for the headline KPI.
        var invoiceCount = await _db.Documents.CountAsync(d =>
            d.CompanyId == companyId
            && d.DocumentType == DocumentType.Invoice
            && d.Status != DocumentStatus.Voided
            && d.DocumentDate >= fromDate && d.DocumentDate <= toDate);

        var taxInvoiceCount = await _db.Documents.CountAsync(d =>
            d.CompanyId == companyId
            && d.DocumentType == DocumentType.TaxInvoice
            && d.Status != DocumentStatus.Voided
            && d.DocumentDate >= fromDate && d.DocumentDate <= toDate);

        var totalInvoices = invoiceCount + taxInvoiceCount;

        // Overdue: documents past due date with balance due — not just docs flagged Overdue
        // (the Overdue status is set by a background job; falling back to date check is more accurate)
        var today = DateTime.UtcNow.Date;
        var overdueCount = await _db.Documents.CountAsync(d =>
            d.CompanyId == companyId
            && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
            && d.DueDate < today
            && d.BalanceDue > 0
            && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Paid && d.Status != DocumentStatus.Draft);

        // Pending approval: include both Draft (this app's flow) and explicit WaitingApproval
        // (multi-step workflow). Draft documents need explicit approval to become AR/AP.
        var pendingApprovals = await _db.Documents.CountAsync(d =>
            d.CompanyId == companyId
            && (d.Status == DocumentStatus.Draft || d.Status == DocumentStatus.WaitingApproval));

        var revenueGrowth = prevRevenue != 0 ? ((totalRevenue - prevRevenue) / prevRevenue) * 100 : 0;
        var expenseGrowth = prevExpenses != 0 ? ((totalExpenses - prevExpenses) / prevExpenses) * 100 : 0;

        return new DashboardKpis(totalRevenue, totalExpenses, totalRevenue - totalExpenses,
            receivables, payables, cashBalance, bankBalance,
            totalInvoices, overdueCount, pendingApprovals,
            Math.Round(revenueGrowth, 2), Math.Round(expenseGrowth, 2),
            invoiceCount, taxInvoiceCount);
    }

    public async Task<CashFlowSummary> GetCashFlowSummaryAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        // Server-side GroupBy — only returns aggregated results, not all rows
        var txnSums = await _db.BankTransactions
            .Where(t => t.CompanyId == companyId
                && t.TransactionDate >= fromDate && t.TransactionDate <= toDate)
            .GroupBy(t => t.TransactionType)
            .Select(g => new { TransactionType = g.Key, Total = g.Sum(t => t.Amount) })
            .ToListAsync();

        var depositTypes = new[] { BankTransactionType.Deposit, BankTransactionType.Interest };
        var withdrawalTypes = new[] { BankTransactionType.Withdrawal, BankTransactionType.Fee, BankTransactionType.Transfer };

        var inflows = txnSums.Where(t => depositTypes.Contains(t.TransactionType))
            .Select(t => new CashFlowItem(t.TransactionType.ToString(), t.Total)).ToList();
        var outflows = txnSums.Where(t => withdrawalTypes.Contains(t.TransactionType))
            .Select(t => new CashFlowItem(t.TransactionType.ToString(), t.Total)).ToList();

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

        // Server-side GROUP BY year/month — single query, no in-memory loop
        var monthlyData = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= startDate
                && l.Account.AccountType == AccountType.Revenue)
            .GroupBy(l => new { l.JournalEntry.EntryDate.Year, l.JournalEntry.EntryDate.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Amount = g.Sum(l => l.CreditAmount - l.DebitAmount)
            })
            .ToListAsync();

        var lookup = monthlyData.ToDictionary(m => (m.Year, m.Month), m => m.Amount);
        var trends = new List<RevenueTrend>();
        for (int i = 0; i < months; i++)
        {
            var monthStart = startDate.AddMonths(i);
            var amount = lookup.GetValueOrDefault((monthStart.Year, monthStart.Month), 0);
            trends.Add(new RevenueTrend(monthStart.Year, monthStart.Month, monthStart.ToString("MMM yyyy"), amount));
        }
        return trends;
    }

    public async Task<List<ExpenseTrend>> GetExpenseTrendsAsync(Guid companyId, int months)
    {
        var now = DateTime.UtcNow;
        var startDate = new DateTime(now.Year, now.Month, 1).AddMonths(-months + 1);

        // Server-side GROUP BY year/month — single query, no in-memory loop
        var monthlyData = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= startDate
                && l.Account.AccountType == AccountType.Expense)
            .GroupBy(l => new { l.JournalEntry.EntryDate.Year, l.JournalEntry.EntryDate.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Amount = g.Sum(l => l.DebitAmount - l.CreditAmount)
            })
            .ToListAsync();

        var lookup = monthlyData.ToDictionary(m => (m.Year, m.Month), m => m.Amount);
        var trends = new List<ExpenseTrend>();
        for (int i = 0; i < months; i++)
        {
            var monthStart = startDate.AddMonths(i);
            var amount = lookup.GetValueOrDefault((monthStart.Year, monthStart.Month), 0);
            trends.Add(new ExpenseTrend(monthStart.Year, monthStart.Month, monthStart.ToString("MMM yyyy"), amount));
        }
        return trends;
    }

    private async Task<List<TopCustomer>> GetTopCustomersAsync(Guid companyId, DateTime fromDate, DateTime toDate, int top = 10)
    {
        // Server-side GroupBy + OrderBy + Take — only top N rows returned
        var rawData = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.Status != DocumentStatus.Voided
                && d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .Select(d => new { d.ContactId, ContactName = d.Contact != null ? d.Contact.Name : null, d.TotalAmount })
            .ToListAsync();

        return rawData
            .GroupBy(d => new { d.ContactId, ContactName = d.ContactName ?? "ไม่ระบุ" })
            .Select(g => new TopCustomer(g.Key.ContactId, g.Key.ContactName, g.Sum(d => d.TotalAmount), g.Count()))
            .OrderByDescending(c => c.TotalAmount)
            .Take(top)
            .ToList();
    }

    private async Task<List<TopExpenseCategory>> GetTopExpenseCategoriesAsync(Guid companyId, DateTime fromDate, DateTime toDate, int top = 10)
    {
        var expenseLines = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.OriginalEntryId == null
                && l.JournalEntry.EntryDate >= fromDate && l.JournalEntry.EntryDate <= toDate
                && l.Account.AccountType == AccountType.Expense)
            .GroupBy(l => new { l.Account.AccountCode, l.Account.AccountName })
            .Select(g => new { g.Key.AccountCode, g.Key.AccountName, Amount = g.Sum(l => l.DebitAmount - l.CreditAmount) })
            .Where(e => e.Amount > 0)
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
        var overdues = await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.DueDate < today
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Paid
                && d.BalanceDue > 0)
            .OrderBy(d => d.DueDate)
            .Take(top)
            .Select(d => new { d.Id, d.DocumentNumber, ContactName = d.Contact.Name, d.TotalAmount, d.BalanceDue, DueDate = d.DueDate!.Value })
            .ToListAsync();

        return overdues.Select(d => new OverdueInvoice(d.Id, d.DocumentNumber, d.ContactName,
            d.TotalAmount, d.BalanceDue, d.DueDate, (int)(today - d.DueDate).TotalDays)).ToList();
    }

    private async Task<List<UpcomingPayable>> GetUpcomingPayablesAsync(Guid companyId, int daysAhead = 30)
    {
        var today = DateTime.UtcNow.Date;
        var cutoff = today.AddDays(daysAhead);
        var payables = await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && d.DocumentType == DocumentType.PurchaseInvoice
                && d.DueDate >= today && d.DueDate <= cutoff
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Paid
                && d.BalanceDue > 0)
            .OrderBy(d => d.DueDate)
            .Select(d => new { d.Id, d.DocumentNumber, ContactName = d.Contact.Name, d.TotalAmount, d.BalanceDue, DueDate = d.DueDate!.Value })
            .ToListAsync();

        return payables.Select(d => new UpcomingPayable(d.Id, d.DocumentNumber, d.ContactName,
            d.TotalAmount, d.BalanceDue, d.DueDate, (int)(d.DueDate - today).TotalDays)).ToList();
    }

    private async Task<BankBalanceSummary> GetBankBalancesAsync(Guid companyId)
    {
        var accounts = await _db.BankAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive)
            .Select(a => new BankBalanceItem(a.Id, a.AccountName, a.BankName, a.Currency, a.CurrentBalance))
            .ToListAsync();

        return new BankBalanceSummary(accounts.Sum(a => a.Balance), accounts);
    }

    private async Task<DashboardSubscriptionSummary?> GetDashboardSubscriptionSummaryAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null) return null;

        var usersCount = await _db.CompanyUsers.CountAsync(cu => cu.CompanyId == companyId);

        return new DashboardSubscriptionSummary(sub.Plan, sub.Status, sub.EndDate,
            Math.Max(0, (int)(sub.EndDate - DateTime.UtcNow).TotalDays),
            sub.CurrentMonthDocuments, sub.MaxDocumentsPerMonth,
            usersCount, sub.MaxUsers);
    }
}
