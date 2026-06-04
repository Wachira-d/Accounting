using Accounting.Models.DTOs.Executive;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<SalesPerformanceResponse> GetSalesPerformanceAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var docs = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .Select(d => new
            {
                d.DocumentType,
                d.Status,
                d.TotalAmount,
                d.PaidAmount,
                d.DocumentDate
            })
            .ToListAsync();

        var revDocs = docs.Where(d =>
            d.DocumentType == DocumentType.Invoice
            || d.DocumentType == DocumentType.TaxInvoice
            || d.DocumentType == DocumentType.Receipt
            || d.DocumentType == DocumentType.ReceiptVoucher).ToList();
        var billable = revDocs.Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft).ToList();

        var totalRevenue = billable.Sum(d => d.TotalAmount);
        var invoiceCount = billable.Count;
        var avgInvoice = invoiceCount > 0 ? totalRevenue / invoiceCount : 0;

        var quotes = docs.Count(d => d.DocumentType == DocumentType.Quotation);
        var convertedQuotes = docs.Count(d => d.DocumentType == DocumentType.Quotation && d.Status == DocumentStatus.Approved);
        var qiRatio = Pct(convertedQuotes, quotes);

        var totalDue = billable.Sum(d => d.TotalAmount);
        var totalPaid = billable.Sum(d => d.PaidAmount);
        var collectionEff = Pct(totalPaid, totalDue);

        var byMonth = billable
            .GroupBy(d => new { d.DocumentDate.Year, d.DocumentDate.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new MonthlyRevenueRow(
                g.Key.Year, g.Key.Month,
                CultureInfo.GetCultureInfo("th-TH").DateTimeFormat.GetMonthName(g.Key.Month),
                R2(g.Sum(x => x.TotalAmount)),
                0, // cost not computed per-month here
                R2(g.Sum(x => x.TotalAmount)),
                g.Count(),
                R2(g.Sum(x => x.TotalAmount) / Math.Max(g.Count(), 1))))
            .ToList();

        var byDocType = docs
            .Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .GroupBy(d => d.DocumentType)
            .Select(g => new DocumentTypeBreakdown(
                g.Key.ToString(),
                DocLabel(g.Key),
                g.Count(),
                R2(g.Sum(x => x.TotalAmount)),
                Pct(g.Sum(x => x.TotalAmount), docs.Sum(x => x.TotalAmount))))
            .OrderByDescending(x => x.Amount)
            .ToList();

        var daily = billable
            .GroupBy(d => d.DocumentDate.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DailySalesPoint(g.Key, R2(g.Sum(x => x.TotalAmount)), g.Count()))
            .ToList();

        return new SalesPerformanceResponse(
            fromDate, toDate,
            R2(totalRevenue), R2(avgInvoice), invoiceCount,
            qiRatio, collectionEff,
            byMonth, byDocType, daily);
    }

    private static string DocLabel(DocumentType t) => t switch
    {
        DocumentType.Quotation => "ใบเสนอราคา",
        DocumentType.Invoice => "ใบแจ้งหนี้",
        DocumentType.Receipt => "ใบเสร็จ",
        DocumentType.TaxInvoice => "ใบกำกับภาษี",
        DocumentType.DebitNote => "ใบเพิ่มหนี้",
        DocumentType.CreditNote => "ใบลดหนี้",
        DocumentType.PurchaseOrder => "ใบสั่งซื้อ",
        DocumentType.PurchaseInvoice => "ใบแจ้งหนี้ซื้อ",
        DocumentType.Expense => "ใบบันทึกค่าใช้จ่าย",
        DocumentType.DeliveryNote => "ใบส่งของ",
        DocumentType.BillingNote => "ใบวางบิล",
        DocumentType.ReceiptVoucher => "ใบสำคัญรับ",
        DocumentType.PaymentVoucher => "ใบสำคัญจ่าย",
        DocumentType.PurchaseRequisition => "ใบขอซื้อ",
        DocumentType.CertificateInLieu => "ใบรับรองแทนใบเสร็จ",
        _ => t.ToString()
    };
}
