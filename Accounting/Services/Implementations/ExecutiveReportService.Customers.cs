using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<CustomerAnalyticsResponse> GetCustomerAnalyticsAsync(Guid companyId, DateTime fromDate, DateTime toDate, int topN = 20)
    {
        var docs = await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice
                     || d.DocumentType == DocumentType.Receipt)
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .Select(d => new
            {
                d.ContactId,
                ContactName = d.Contact.Name,
                d.Contact.TaxId,
                d.TotalAmount,
                d.BalanceDue,
                d.DocumentDate,
                d.DueDate,
                d.PaidAmount
            })
            .ToListAsync();

        var totalRevenue = docs.Sum(d => d.TotalAmount);
        var grouped = docs
            .GroupBy(d => new { d.ContactId, d.ContactName, d.TaxId })
            .Select(g => new CustomerRevenueRow(
                g.Key.ContactId,
                g.Key.ContactName,
                g.Key.TaxId,
                R2(g.Sum(x => x.TotalAmount)),
                Pct(g.Sum(x => x.TotalAmount), totalRevenue),
                g.Count(),
                R2(g.Sum(x => x.BalanceDue)),
                R2(g.Where(x => x.BalanceDue > 0 && x.DueDate.HasValue && x.DueDate.Value < DateTime.UtcNow).Sum(x => x.BalanceDue)),
                g.Max(x => (DateTime?)x.DocumentDate)))
            .OrderByDescending(c => c.TotalRevenue)
            .ToList();

        var customerCount = grouped.Count;
        var activeCount = grouped.Count(c => c.LastInvoiceDate.HasValue && c.LastInvoiceDate.Value >= toDate.AddDays(-90));
        var avgPerCustomer = customerCount > 0 ? totalRevenue / customerCount : 0;

        // ABC Analysis (Pareto)
        var sorted = grouped.OrderByDescending(c => c.TotalRevenue).ToList();
        decimal cum = 0;
        var classA = new List<CustomerRevenueRow>();
        var classB = new List<CustomerRevenueRow>();
        var classC = new List<CustomerRevenueRow>();
        foreach (var c in sorted)
        {
            cum += c.TotalRevenue;
            var share = totalRevenue == 0 ? 0 : cum / totalRevenue;
            if (share <= 0.80m) classA.Add(c);
            else if (share <= 0.95m) classB.Add(c);
            else classC.Add(c);
        }
        var abc = new List<AbcSegment>
        {
            new("A", classA.Count, R2(classA.Sum(x => x.TotalRevenue)), Pct(classA.Sum(x => x.TotalRevenue), totalRevenue)),
            new("B", classB.Count, R2(classB.Sum(x => x.TotalRevenue)), Pct(classB.Sum(x => x.TotalRevenue), totalRevenue)),
            new("C", classC.Count, R2(classC.Sum(x => x.TotalRevenue)), Pct(classC.Sum(x => x.TotalRevenue), totalRevenue)),
        };

        var top = sorted.Take(topN)
            .Select(c => new TopCustomerRow(c.ContactId, c.Name, c.TotalRevenue, c.SharePercent, c.InvoiceCount))
            .ToList();

        // Churn risk: customers historically active but no invoice in last 90 days
        var historicCutoff = toDate.AddYears(-2);
        var allHistory = await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice
                     || d.DocumentType == DocumentType.Receipt)
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.DocumentDate >= historicCutoff && d.DocumentDate <= toDate)
            .Select(d => new { d.ContactId, ContactName = d.Contact.Name, d.DocumentDate, d.TotalAmount })
            .ToListAsync();
        var ninetyAgo = toDate.AddDays(-90);
        var churn = allHistory
            .GroupBy(d => new { d.ContactId, d.ContactName })
            .Select(g => new
            {
                g.Key.ContactId,
                g.Key.ContactName,
                LastInv = g.Max(x => x.DocumentDate),
                HistRev = g.Sum(x => x.TotalAmount)
            })
            .Where(c => c.LastInv < ninetyAgo && c.HistRev > 0)
            .OrderByDescending(c => c.HistRev)
            .Take(20)
            .Select(c => new CustomerChurnRow(c.ContactId, c.ContactName,
                c.LastInv, (int)(toDate - c.LastInv).TotalDays, R2(c.HistRev)))
            .ToList();

        return new CustomerAnalyticsResponse(
            fromDate, toDate, customerCount, activeCount,
            R2(totalRevenue), R2(avgPerCustomer),
            sorted, abc, top, churn);
    }

    internal async Task<List<TopCustomerRow>> BuildTopCustomersAsync(Guid companyId, DateTime fromDate, DateTime toDate, int n)
    {
        var docs = await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice
                     || d.DocumentType == DocumentType.Receipt)
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .Select(d => new { d.ContactId, ContactName = d.Contact.Name, d.TotalAmount })
            .ToListAsync();
        var total = docs.Sum(d => d.TotalAmount);
        return docs.GroupBy(d => new { d.ContactId, d.ContactName })
            .Select(g => new TopCustomerRow(g.Key.ContactId, g.Key.ContactName,
                R2(g.Sum(x => x.TotalAmount)), Pct(g.Sum(x => x.TotalAmount), total), g.Count()))
            .OrderByDescending(x => x.Amount).Take(n).ToList();
    }
}
