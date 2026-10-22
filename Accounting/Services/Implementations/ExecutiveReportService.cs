using Accounting.Data;
using Accounting.Models.DTOs.Executive;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService : IExecutiveReportService
{
    private readonly AccountingDbContext _db;
    public ExecutiveReportService(AccountingDbContext db) { _db = db; }

    // ===== Helpers =====
    private static decimal SafeDiv(decimal num, decimal den) => den == 0 ? 0 : num / den;
    private static decimal Pct(decimal num, decimal den) => den == 0 ? 0 : Math.Round(num / den * 100m, 2);
    private static decimal R2(decimal v) => Math.Round(v, 2);

    private IQueryable<JournalEntryLine> PostedLinesQuery(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        return _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Include(l => l.Account)
            .Where(l => !l.IsDeleted && !l.JournalEntry.IsDeleted)
            .Where(l => l.JournalEntry.CompanyId == companyId)
            .Where(l => l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed)
            // ★ C-T02 (ทีม M 2026-10-22): ผู้เรียกทุกตัวของ query นี้เป็น "ยอดเคลื่อนไหวในช่วง" ของรายได้/ค่าใช้จ่าย/ต้นทุน/ค่าเสื่อม/
            // งบประมาณเทียบจริง ⇒ ใบปิดบัญชีต้องไม่นับ (เดิมนับ ⇒ หลังปิดปี รายได้/ค่าใช้จ่ายของเดือนสิ้นรอบ + ทั้งปี = 0 ในรายงานผู้บริหาร
            // ขณะที่งบกำไรขาดทุน/แดชบอร์ดตัดออกแล้ว — ตัวเลขสองชุดสำหรับคำถามเดียว) · ยอดคงเหลือ (GetBalanceAsync) ไม่ใช้ query นี้
            .Where(l => !l.JournalEntry.IsClosingEntry)
            .Where(l => l.JournalEntry.EntryDate >= fromDate && l.JournalEntry.EntryDate <= toDate);
    }

    /// <summary>Sum credit-debit (revenue side) for revenue accounts (AccountType.Revenue).</summary>
    private async Task<decimal> SumRevenueAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var lines = PostedLinesQuery(companyId, fromDate, toDate)
            .Where(l => l.Account.AccountType == AccountType.Revenue);
        var totals = await lines.GroupBy(_ => 1)
            .Select(g => new { D = g.Sum(x => x.DebitAmount), C = g.Sum(x => x.CreditAmount) })
            .FirstOrDefaultAsync();
        return totals == null ? 0 : (totals.C - totals.D);
    }

    /// <summary>Sum debit-credit for expense accounts (AccountType.Expense).</summary>
    private async Task<decimal> SumExpenseAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var lines = PostedLinesQuery(companyId, fromDate, toDate)
            .Where(l => l.Account.AccountType == AccountType.Expense);
        var totals = await lines.GroupBy(_ => 1)
            .Select(g => new { D = g.Sum(x => x.DebitAmount), C = g.Sum(x => x.CreditAmount) })
            .FirstOrDefaultAsync();
        return totals == null ? 0 : (totals.D - totals.C);
    }

    /// <summary>Sum expense for COGS accounts (code starts with "51").</summary>
    private async Task<decimal> SumCogsAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var lines = PostedLinesQuery(companyId, fromDate, toDate)
            .Where(l => l.Account.AccountType == AccountType.Expense)
            .Where(l => l.Account.AccountCode.StartsWith("51"));
        var totals = await lines.GroupBy(_ => 1)
            .Select(g => new { D = g.Sum(x => x.DebitAmount), C = g.Sum(x => x.CreditAmount) })
            .FirstOrDefaultAsync();
        return totals == null ? 0 : (totals.D - totals.C);
    }

    /// <summary>Get balance for an account-type as-of date (sign-corrected).</summary>
    private async Task<decimal> GetBalanceAsync(Guid companyId, AccountType accountType, DateTime asOfDate, string? codePrefix = null)
    {
        var q = _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Include(l => l.Account)
            .Where(l => !l.IsDeleted && !l.JournalEntry.IsDeleted)
            .Where(l => l.JournalEntry.CompanyId == companyId)
            .Where(l => l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed)
            .Where(l => l.JournalEntry.EntryDate <= asOfDate)
            .Where(l => l.Account.AccountType == accountType);
        if (!string.IsNullOrEmpty(codePrefix))
            q = q.Where(l => l.Account.AccountCode.StartsWith(codePrefix));
        var totals = await q.GroupBy(_ => 1)
            .Select(g => new { D = g.Sum(x => x.DebitAmount), C = g.Sum(x => x.CreditAmount) })
            .FirstOrDefaultAsync();
        if (totals == null) return 0;
        // Asset, Expense → Debit normal (D - C)
        // Liability, Equity, Revenue → Credit normal (C - D)
        return accountType switch
        {
            AccountType.Asset or AccountType.Expense => totals.D - totals.C,
            _ => totals.C - totals.D
        };
    }

    private async Task<decimal> GetCashAndBankAsync(Guid companyId, DateTime asOfDate)
    {
        var lines = _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Include(l => l.Account)
            .Where(l => !l.IsDeleted && !l.JournalEntry.IsDeleted)
            .Where(l => l.JournalEntry.CompanyId == companyId)
            .Where(l => l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed)
            .Where(l => l.JournalEntry.EntryDate <= asOfDate)
            .Where(l => l.Account.AccountType == AccountType.Asset)
            .Where(l => l.Account.AccountCode.StartsWith("111"));
        var totals = await lines.GroupBy(_ => 1)
            .Select(g => new { D = g.Sum(x => x.DebitAmount), C = g.Sum(x => x.CreditAmount) })
            .FirstOrDefaultAsync();
        return totals == null ? 0 : (totals.D - totals.C);
    }
}
