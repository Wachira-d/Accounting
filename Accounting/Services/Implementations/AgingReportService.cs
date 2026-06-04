using Accounting.Data;
using Accounting.Models.DTOs.Aging;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class AgingReportService : IAgingReportService
{
    private readonly AccountingDbContext _db;

    public AgingReportService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<AgingReportResponse> GetReceivableAgingAsync(Guid companyId, AgingReportRequest request)
    {
        return await BuildAgingReportAsync(companyId, AgingReportType.AccountsReceivable, request);
    }

    public async Task<AgingReportResponse> GetPayableAgingAsync(Guid companyId, AgingReportRequest request)
    {
        return await BuildAgingReportAsync(companyId, AgingReportType.AccountsPayable, request);
    }

    public async Task<AgingContactDetail> GetContactAgingDetailAsync(Guid companyId, Guid contactId, AgingReportType reportType, DateTime? asOfDate = null)
    {
        var report = await BuildAgingReportAsync(companyId, reportType, new AgingReportRequest(asOfDate, contactId));
        return report.Details.FirstOrDefault()
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลลูกค้า/เจ้าหนี้");
    }

    private async Task<AgingReportResponse> BuildAgingReportAsync(Guid companyId, AgingReportType reportType, AgingReportRequest request)
    {
        var asOfDate = request.AsOfDate ?? DateTime.UtcNow.Date;

        // Determine document types based on report type. AR adds DebitNote
        // (ใบเพิ่มหนี้ raises a receivable); AP adds Expense + PaymentVoucher
        // so a voucher-based shop's CREDIT payables actually age (cash ones
        // have BalanceDue 0 and are filtered out below, so no false aging).
        var documentTypes = reportType == AgingReportType.AccountsReceivable
            ? new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.BillingNote, DocumentType.DebitNote }
            : new[] { DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.PaymentVoucher, DocumentType.CertificateInLieu };

        var query = _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && documentTypes.Contains(d.DocumentType)
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Paid
                && d.BalanceDue > 0
                && d.DocumentDate <= asOfDate);

        if (request.ContactId.HasValue)
            query = query.Where(d => d.ContactId == request.ContactId.Value);

        if (request.ProjectId.HasValue)
            query = query.Where(d => d.ProjectId == request.ProjectId.Value);

        var documents = await query.ToListAsync();

        // Build contact details with aging
        var contactGroups = documents.GroupBy(d => new { d.ContactId, d.Contact.Name, d.Contact.TaxId });

        var details = new List<AgingContactDetail>();
        foreach (var group in contactGroups)
        {
            var docs = new List<AgingDocumentDetail>();
            decimal current = 0, d1to30 = 0, d31to60 = 0, d61to90 = 0, over90 = 0;

            foreach (var doc in group)
            {
                var dueDate = doc.DueDate ?? doc.DocumentDate;
                var agingDays = Math.Max(0, (int)(asOfDate - dueDate).TotalDays);
                var bucket = GetAgingBucket(agingDays);

                switch (bucket)
                {
                    case "Current": current += doc.BalanceDue; break;
                    case "1-30 วัน": d1to30 += doc.BalanceDue; break;
                    case "31-60 วัน": d31to60 += doc.BalanceDue; break;
                    case "61-90 วัน": d61to90 += doc.BalanceDue; break;
                    default: over90 += doc.BalanceDue; break;
                }

                docs.Add(new AgingDocumentDetail(doc.Id, doc.DocumentNumber, doc.DocumentType,
                    doc.DocumentDate, doc.DueDate, doc.TotalAmount, doc.BalanceDue, agingDays, bucket));
            }

            details.Add(new AgingContactDetail(group.Key.ContactId, group.Key.Name, group.Key.TaxId,
                current, d1to30, d31to60, d61to90, over90,
                current + d1to30 + d31to60 + d61to90 + over90, docs.OrderByDescending(d => d.AgingDays).ToList()));
        }

        var totals = new AgingTotals(
            details.Sum(d => d.Current),
            details.Sum(d => d.Days1To30),
            details.Sum(d => d.Days31To60),
            details.Sum(d => d.Days61To90),
            details.Sum(d => d.Over90Days),
            details.Sum(d => d.TotalBalance));

        var buckets = new List<AgingBucket>
        {
            new("Current", 0, 0, totals.Current, documents.Count(d => GetAgingDays(d, asOfDate) == 0)),
            new("1-30 วัน", 1, 30, totals.Days1To30, documents.Count(d => { var days = GetAgingDays(d, asOfDate); return days >= 1 && days <= 30; })),
            new("31-60 วัน", 31, 60, totals.Days31To60, documents.Count(d => { var days = GetAgingDays(d, asOfDate); return days >= 31 && days <= 60; })),
            new("61-90 วัน", 61, 90, totals.Days61To90, documents.Count(d => { var days = GetAgingDays(d, asOfDate); return days >= 61 && days <= 90; })),
            new("มากกว่า 90 วัน", 91, null, totals.Over90Days, documents.Count(d => GetAgingDays(d, asOfDate) > 90))
        };

        return new AgingReportResponse(reportType, asOfDate, buckets, details.OrderByDescending(d => d.TotalBalance).ToList(), totals);
    }

    private static int GetAgingDays(Models.Entities.Document doc, DateTime asOfDate)
    {
        var dueDate = doc.DueDate ?? doc.DocumentDate;
        return Math.Max(0, (int)(asOfDate - dueDate).TotalDays);
    }

    private static string GetAgingBucket(int days) => days switch
    {
        0 => "Current",
        <= 30 => "1-30 วัน",
        <= 60 => "31-60 วัน",
        <= 90 => "61-90 วัน",
        _ => "มากกว่า 90 วัน"
    };
}
