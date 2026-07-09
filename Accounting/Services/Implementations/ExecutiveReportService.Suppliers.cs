using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<SupplierAnalyticsResponse> GetSupplierAnalyticsAsync(Guid companyId, DateTime fromDate, DateTime toDate, int topN = 20)
    {
        // ห้าม project d.Contact.Name/TaxId ตรง ๆ — Contact query filter !IsDeleted
        // → เอกสารที่ contact ถูกลบจะถูก INNER JOIN ตัดทิ้ง (under-report). select
        // ContactId แล้ว resolve จาก dict (IgnoreQueryFilters).
        var rows = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense || d.DocumentType == DocumentType.CertificateInLieu)
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .Select(d => new { d.ContactId, d.TotalAmount, d.BalanceDue, d.DocumentDate })
            .ToListAsync();
        var supCids = rows.Select(r => r.ContactId).Distinct().ToList();
        var supCmap = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && supCids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => new { c.Name, c.TaxId });
        var docs = rows.Select(r =>
        {
            var c = supCmap.GetValueOrDefault(r.ContactId);
            return new
            {
                r.ContactId,
                ContactName = c?.Name ?? "-",
                TaxId = c?.TaxId,
                r.TotalAmount,
                r.BalanceDue,
                r.DocumentDate
            };
        }).ToList();

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
        // select ContactId แล้ว resolve ชื่อจาก dict (IgnoreQueryFilters) — กัน INNER
        // JOIN ตัดใบที่ contact ถูกลบ (under-report)
        var rows = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense || d.DocumentType == DocumentType.CertificateInLieu)
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .Select(d => new { d.ContactId, d.TotalAmount })
            .ToListAsync();
        var topCids = rows.Select(r => r.ContactId).Distinct().ToList();
        var topCmap = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && topCids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);
        var docs = rows.Select(r => new { r.ContactId, ContactName = topCmap.GetValueOrDefault(r.ContactId) ?? "-", r.TotalAmount }).ToList();
        var total = docs.Sum(d => d.TotalAmount);
        return docs.GroupBy(d => new { d.ContactId, d.ContactName })
            .Select(g => new TopSupplierRow(g.Key.ContactId, g.Key.ContactName,
                R2(g.Sum(x => x.TotalAmount)), Pct(g.Sum(x => x.TotalAmount), total), g.Count()))
            .OrderByDescending(x => x.Amount).Take(n).ToList();
    }
}
