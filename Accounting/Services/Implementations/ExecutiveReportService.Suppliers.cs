using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<SupplierAnalyticsResponse> GetSupplierAnalyticsAsync(Guid companyId, DateTime fromDate, DateTime toDate, int topN = 20)
    {
        var docs = await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense)
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .Select(d => new
            {
                d.ContactId,
                ContactName = d.Contact.Name,
                d.Contact.TaxId,
                d.TotalAmount,
                d.BalanceDue,
                d.DocumentDate
            })
            .ToListAsync();

        var totalPurchases = docs.Sum(d => d.TotalAmount);
        var grouped = docs
            .GroupBy(d => new { d.ContactId, d.ContactName, d.TaxId })
            .Select(g => new SupplierPurchaseRow(
                g.Key.ContactId, g.Key.ContactName, g.Key.TaxId,
                R2(g.Sum(x => x.TotalAmount)),
                Pct(g.Sum(x => x.TotalAmount), totalPurchases),
                g.Count(),
                R2(g.Sum(x => x.BalanceDue)),
                g.Max(x => (DateTime?)x.DocumentDate)))
            .OrderByDescending(s => s.TotalPurchases)
            .ToList();

        var supplierCount = grouped.Count;
        var activeCount = grouped.Count(s => s.LastPurchaseDate.HasValue && s.LastPurchaseDate.Value >= toDate.AddDays(-90));
        var avgPer = supplierCount > 0 ? totalPurchases / supplierCount : 0;
        var concentration = grouped.Take(3).Sum(s => s.SharePercent);

        var top = grouped.Take(topN)
            .Select(s => new TopSupplierRow(s.ContactId, s.Name, s.TotalPurchases, s.SharePercent, s.InvoiceCount))
            .ToList();

        return new SupplierAnalyticsResponse(
            fromDate, toDate, supplierCount, activeCount,
            R2(totalPurchases), R2(avgPer), R2(concentration),
            grouped, top);
    }

    internal async Task<List<TopSupplierRow>> BuildTopSuppliersAsync(Guid companyId, DateTime fromDate, DateTime toDate, int n)
    {
        var docs = await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense)
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .Select(d => new { d.ContactId, ContactName = d.Contact.Name, d.TotalAmount })
            .ToListAsync();
        var total = docs.Sum(d => d.TotalAmount);
        return docs.GroupBy(d => new { d.ContactId, d.ContactName })
            .Select(g => new TopSupplierRow(g.Key.ContactId, g.Key.ContactName,
                R2(g.Sum(x => x.TotalAmount)), Pct(g.Sum(x => x.TotalAmount), total), g.Count()))
            .OrderByDescending(x => x.Amount).Take(n).ToList();
    }
}
