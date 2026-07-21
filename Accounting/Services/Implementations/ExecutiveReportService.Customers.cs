using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<CustomerAnalyticsResponse> GetCustomerAnalyticsAsync(Guid companyId, DateTime fromDate, DateTime toDate, int topN = 20)
    {
        // ห้าม project d.Contact.Name/TaxId ตรง ๆ — Contact query filter !IsDeleted
        // → เอกสารที่ contact ถูกลบจะถูก INNER JOIN ตัดทิ้ง (รายได้ under-report).
        // select ContactId แล้ว resolve จาก dict (IgnoreQueryFilters).
        var rows = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            // ใบเสร็จ/ใบสำคัญรับที่อ้างใบแจ้งหนี้ (RelatedDocumentId) = การตัดชำระ
            // ไม่ใช่รายได้ใหม่ → นับเฉพาะขายสด standalone กันรายได้ต่อลูกค้าเบิ้ล 2
            .Where(d => d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice
                     || ((d.DocumentType == DocumentType.Receipt || d.DocumentType == DocumentType.ReceiptVoucher)
                         && d.RelatedDocumentId == null))
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .Select(d => new
            {
                d.ContactId,
                d.TotalAmount,
                d.BalanceDue,
                d.DocumentDate,
                d.DueDate,
                d.PaidAmount
            })
            .ToListAsync();
        var custCids = rows.Select(r => r.ContactId).Distinct().ToList();
        var custCmap = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && custCids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => new { c.Name, c.TaxId });
        var docs = rows.Select(r =>
        {
            var c = custCmap.GetValueOrDefault(r.ContactId);
            return new
            {
                r.ContactId,
                ContactName = c?.Name ?? "-",
                TaxId = c?.TaxId,
                r.TotalAmount,
                r.BalanceDue,
                r.DocumentDate,
                r.DueDate,
                r.PaidAmount
            };
        }).ToList();

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
        var historyRows = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            // ใบเสร็จ/ใบสำคัญรับที่อ้างใบแจ้งหนี้ (RelatedDocumentId) = การตัดชำระ
            // ไม่ใช่รายได้ใหม่ → นับเฉพาะขายสด standalone กันรายได้ต่อลูกค้าเบิ้ล 2
            .Where(d => d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice
                     || ((d.DocumentType == DocumentType.Receipt || d.DocumentType == DocumentType.ReceiptVoucher)
                         && d.RelatedDocumentId == null))
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Where(d => d.DocumentDate >= historicCutoff && d.DocumentDate <= toDate)
            .Select(d => new { d.ContactId, d.DocumentDate, d.TotalAmount })
            .ToListAsync();
        // resolve ชื่อจาก dict (IgnoreQueryFilters) — กัน INNER JOIN ตัดใบที่ contact ถูกลบ
        var histCids = historyRows.Select(r => r.ContactId).Distinct().ToList();
        var histCmap = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && histCids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);
        var allHistory = historyRows
            .Select(r => new { r.ContactId, ContactName = histCmap.GetValueOrDefault(r.ContactId) ?? "-", r.DocumentDate, r.TotalAmount })
            .ToList();
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
        // select ContactId แล้ว resolve ชื่อจาก dict (IgnoreQueryFilters) — กัน INNER
        // JOIN ตัดใบที่ contact ถูกลบ (รายได้ under-report)
        var rows = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            // ใบเสร็จ/ใบสำคัญรับที่อ้างใบแจ้งหนี้ (RelatedDocumentId) = การตัดชำระ
            // ไม่ใช่รายได้ใหม่ → นับเฉพาะขายสด standalone กันรายได้ต่อลูกค้าเบิ้ล 2
            .Where(d => d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice
                     || ((d.DocumentType == DocumentType.Receipt || d.DocumentType == DocumentType.ReceiptVoucher)
                         && d.RelatedDocumentId == null))
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
            .Select(g => new TopCustomerRow(g.Key.ContactId, g.Key.ContactName,
                R2(g.Sum(x => x.TotalAmount)), Pct(g.Sum(x => x.TotalAmount), total), g.Count()))
            .OrderByDescending(x => x.Amount).Take(n).ToList();
    }
}
