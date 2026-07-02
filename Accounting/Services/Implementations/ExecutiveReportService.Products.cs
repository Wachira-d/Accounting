using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<ProductAnalyticsResponse> GetProductAnalyticsAsync(Guid companyId, DateTime fromDate, DateTime toDate, int topN = 20)
    {
        var revenueLines = await (
            from line in _db.DocumentLines
            join doc in _db.Documents on line.DocumentId equals doc.Id
            where !line.IsDeleted && !doc.IsDeleted
                && doc.CompanyId == companyId
                && (doc.DocumentType == DocumentType.Invoice
                    || doc.DocumentType == DocumentType.TaxInvoice
                    // ใบเสร็จที่อ้างใบแจ้งหนี้ = ตัดชำระ (line สินค้าซ้ำกับใบแจ้งหนี้)
                    // → นับเฉพาะขายสด standalone กันยอด/จำนวนสินค้าเบิ้ล 2
                    || (doc.DocumentType == DocumentType.Receipt && doc.RelatedDocumentId == null))
                && doc.Status != DocumentStatus.Voided && doc.Status != DocumentStatus.Draft
                && doc.DocumentDate >= fromDate && doc.DocumentDate <= toDate
            select new
            {
                line.ProductCode,
                line.Description,
                line.Quantity,
                line.Amount,
                doc.Id
            }).ToListAsync();

        var totalRevenue = revenueLines.Sum(l => l.Amount);
        var totalUnits = revenueLines.Sum(l => l.Quantity);

        var products = revenueLines
            .GroupBy(l => new { Code = l.ProductCode ?? "", Name = l.Description })
            .Select(g => new
            {
                Code = g.Key.Code,
                Name = g.Key.Name,
                Qty = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => x.Amount),
                InvCount = g.Select(x => x.Id).Distinct().Count()
            })
            .ToList();

        // Cost approximation from product master
        var codes = products.Where(p => !string.IsNullOrEmpty(p.Code)).Select(p => p.Code).ToList();
        var productCosts = await _db.Products
            .Where(p => p.CompanyId == companyId && !p.IsDeleted)
            .Where(p => codes.Contains(p.Code))
            .Select(p => new { p.Code, Cost = p.CostPrice })
            .ToListAsync();
        var costMap = productCosts.ToDictionary(p => p.Code, p => p.Cost);

        var rows = products.Select(p =>
        {
            var unitCost = string.IsNullOrEmpty(p.Code) ? 0 : (costMap.TryGetValue(p.Code, out var c) ? c : 0);
            var costOfSales = unitCost * p.Qty;
            var grossProfit = p.Revenue - costOfSales;
            var margin = Pct(grossProfit, p.Revenue);
            return new ProductSalesRow(
                p.Code, p.Name,
                R2(p.Qty), R2(p.Revenue),
                R2(costOfSales), R2(grossProfit), margin,
                p.InvCount, Pct(p.Revenue, totalRevenue));
        }).ToList();

        var topRevenue = rows.OrderByDescending(r => r.Revenue).Take(topN).ToList();
        var topQty = rows.OrderByDescending(r => r.QuantitySold).Take(topN).ToList();
        var topMargin = rows.Where(r => r.Revenue > 0).OrderByDescending(r => r.MarginPercent).Take(topN).ToList();
        var slowMovers = rows.OrderBy(r => r.QuantitySold).Take(topN).ToList();

        return new ProductAnalyticsResponse(
            fromDate, toDate,
            R2(totalRevenue), R2(totalUnits),
            rows.Count,
            rows, topRevenue, topQty, topMargin, slowMovers);
    }
}
