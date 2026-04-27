using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<CashFlowForecastResponse> GetCashFlowForecastAsync(Guid companyId, int days)
    {
        if (days < 7) days = 30;
        if (days > 180) days = 180;

        var asOf = DateTime.UtcNow.Date;
        var horizon = asOf.AddDays(days);
        var openingCash = await GetCashAndBankAsync(companyId, asOf);

        // Pull receivables and payables outstanding
        var ar = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.Invoice
                     || d.DocumentType == DocumentType.TaxInvoice
                     || d.DocumentType == DocumentType.BillingNote)
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.BalanceDue > 0)
            .Select(d => new { d.DueDate, d.DocumentDate, d.BalanceDue, d.DocumentNumber })
            .ToListAsync();

        var ap = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.PurchaseInvoice
                     || d.DocumentType == DocumentType.Expense)
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.BalanceDue > 0)
            .Select(d => new { d.DueDate, d.DocumentDate, d.BalanceDue, d.DocumentNumber })
            .ToListAsync();

        // Group by week
        var weeks = new List<CashFlowForecastWeek>();
        var balance = openingCash;
        var weekCount = (int)Math.Ceiling(days / 7.0);
        for (int w = 0; w < weekCount; w++)
        {
            var weekStart = asOf.AddDays(w * 7);
            var weekEnd = weekStart.AddDays(6);
            if (weekEnd > horizon) weekEnd = horizon;

            var inflows = ar.Where(d =>
            {
                var date = d.DueDate ?? d.DocumentDate.AddDays(30);
                return date >= weekStart && date <= weekEnd;
            }).ToList();

            var outflows = ap.Where(d =>
            {
                var date = d.DueDate ?? d.DocumentDate.AddDays(30);
                return date >= weekStart && date <= weekEnd;
            }).ToList();

            var inAmt = inflows.Sum(d => d.BalanceDue);
            var outAmt = outflows.Sum(d => d.BalanceDue);
            var net = inAmt - outAmt;
            var opening = balance;
            balance += net;

            var items = new List<CashFlowForecastItem>();
            foreach (var d in inflows)
            {
                var date = d.DueDate ?? d.DocumentDate.AddDays(30);
                items.Add(new CashFlowForecastItem(date, $"AR {d.DocumentNumber}",
                    d.DueDate.HasValue ? "AR" : "Estimate", R2(d.BalanceDue), "In"));
            }
            foreach (var d in outflows)
            {
                var date = d.DueDate ?? d.DocumentDate.AddDays(30);
                items.Add(new CashFlowForecastItem(date, $"AP {d.DocumentNumber}",
                    d.DueDate.HasValue ? "AP" : "Estimate", R2(d.BalanceDue), "Out"));
            }

            var totalCount = inflows.Count + outflows.Count;
            var confirmedCount = inflows.Count(x => x.DueDate.HasValue) + outflows.Count(x => x.DueDate.HasValue);
            var confidence = totalCount == 0 ? 100m : Math.Round((decimal)confirmedCount / totalCount * 100m, 1);

            weeks.Add(new CashFlowForecastWeek(
                w + 1, weekStart, weekEnd,
                R2(opening), R2(inAmt), R2(outAmt), R2(net), R2(balance),
                confidence, items));
        }

        return new CashFlowForecastResponse(asOf, R2(openingCash), R2(balance), days, weeks);
    }
}
