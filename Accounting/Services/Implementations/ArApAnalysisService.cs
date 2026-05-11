using Accounting.Data;
using Accounting.Models.DTOs.ArApAnalysis;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ArApAnalysisService : IArApAnalysisService
{
    private readonly AccountingDbContext _db;
    private static readonly DocumentType[] ArTypes = { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.BillingNote, DocumentType.DebitNote };
    private static readonly DocumentType[] ApTypes = { DocumentType.PurchaseInvoice, DocumentType.CertificateInLieu };

    public ArApAnalysisService(AccountingDbContext db) => _db = db;

    public async Task<ArApOverviewResponse> GetOverviewAsync(Guid companyId)
    {
        var now = DateTime.UtcNow.Date;
        var docs = await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .ToListAsync();

        var arDocs = docs.Where(d => ArTypes.Contains(d.DocumentType)).ToList();
        var apDocs = docs.Where(d => ApTypes.Contains(d.DocumentType)).ToList();

        var arOpen = arDocs.Where(d => d.BalanceDue > 0).ToList();
        var apOpen = apDocs.Where(d => d.BalanceDue > 0).ToList();

        var totalAr = arOpen.Sum(d => d.BalanceDue);
        var totalAp = apOpen.Sum(d => d.BalanceDue);

        var overdueAr = arOpen.Where(d => d.DueDate.HasValue && d.DueDate.Value < now).ToList();
        var overdueAp = apOpen.Where(d => d.DueDate.HasValue && d.DueDate.Value < now).ToList();

        // DSO = (AR / Revenue last 12 months) * 365
        var yearAgo = now.AddMonths(-12);
        var revenue12 = arDocs.Where(d => d.DocumentDate >= yearAgo).Sum(d => d.TotalAmount);
        var dso = revenue12 > 0 ? (double)totalAr / (double)revenue12 * 365 : 0;

        // DPO = (AP / COGS last 12 months) * 365
        var cogs12 = apDocs.Where(d => d.DocumentDate >= yearAgo).Sum(d => d.TotalAmount);
        var dpo = cogs12 > 0 ? (double)totalAp / (double)cogs12 * 365 : 0;

        // Collection rate: invoices paid within terms / total invoices (last 12m)
        var arPaid12 = arDocs.Where(d => d.DocumentDate >= yearAgo && d.Status == DocumentStatus.Paid).ToList();
        var onTime = arPaid12.Count(d => d.DueDate == null || d.PaidAmount >= d.TotalAmount);
        var collRate = arPaid12.Count > 0 ? (double)onTime / arPaid12.Count * 100 : 100;

        // Monthly trend (last 12 months)
        var trend = new List<ArApTrendItem>();
        for (int i = 11; i >= 0; i--)
        {
            var m = now.AddMonths(-i);
            var mStart = new DateTime(m.Year, m.Month, 1);
            var mEnd = mStart.AddMonths(1);

            var arNew = arDocs.Where(d => d.DocumentDate >= mStart && d.DocumentDate < mEnd).Sum(d => d.TotalAmount);
            var arPaid = arDocs.Where(d => d.DocumentDate >= mStart && d.DocumentDate < mEnd).Sum(d => d.PaidAmount);
            var apNew = apDocs.Where(d => d.DocumentDate >= mStart && d.DocumentDate < mEnd).Sum(d => d.TotalAmount);
            var apPaid = apDocs.Where(d => d.DocumentDate >= mStart && d.DocumentDate < mEnd).Sum(d => d.PaidAmount);

            var arBal = arDocs.Where(d => d.DocumentDate < mEnd).Sum(d => d.BalanceDue);
            var apBal = apDocs.Where(d => d.DocumentDate < mEnd).Sum(d => d.BalanceDue);

            trend.Add(new ArApTrendItem(m.Year, m.Month, $"{m.Year}-{m.Month:D2}",
                arBal, apBal, arNew, arPaid, apNew, apPaid));
        }

        // Top contacts
        var topAr = arOpen.GroupBy(d => new { d.ContactId, d.Contact.Name, d.Contact.TaxId })
            .Select(g => new ArApTopContact(g.Key.ContactId, g.Key.Name, g.Key.TaxId,
                g.Sum(d => d.BalanceDue),
                g.Where(d => d.DueDate.HasValue && d.DueDate.Value < now).Sum(d => d.BalanceDue),
                g.Count(),
                g.Count(d => d.DueDate.HasValue && d.DueDate.Value < now),
                g.Max(d => d.DueDate.HasValue ? Math.Max(0, (int)(now - d.DueDate.Value).TotalDays) : 0)))
            .OrderByDescending(c => c.TotalBalance).Take(10).ToList();

        var topAp = apOpen.GroupBy(d => new { d.ContactId, d.Contact.Name, d.Contact.TaxId })
            .Select(g => new ArApTopContact(g.Key.ContactId, g.Key.Name, g.Key.TaxId,
                g.Sum(d => d.BalanceDue),
                g.Where(d => d.DueDate.HasValue && d.DueDate.Value < now).Sum(d => d.BalanceDue),
                g.Count(),
                g.Count(d => d.DueDate.HasValue && d.DueDate.Value < now),
                g.Max(d => d.DueDate.HasValue ? Math.Max(0, (int)(now - d.DueDate.Value).TotalDays) : 0)))
            .OrderByDescending(c => c.TotalBalance).Take(10).ToList();

        return new ArApOverviewResponse(
            totalAr, totalAp, totalAr - totalAp,
            (decimal)Math.Round(dso, 1), (decimal)Math.Round(dpo, 1),
            overdueAr.Count, overdueAp.Count,
            overdueAr.Sum(d => d.BalanceDue), overdueAp.Sum(d => d.BalanceDue),
            (decimal)Math.Round(collRate, 1),
            trend, topAr, topAp,
            BuildAgingSummary(arOpen, now),
            BuildAgingSummary(apOpen, now));
    }

    public async Task<ContactArApDetailResponse> GetContactDetailAsync(Guid companyId, Guid contactId, string type)
    {
        var now = DateTime.UtcNow.Date;
        var isAr = type.Equals("ar", StringComparison.OrdinalIgnoreCase);
        var docTypes = isAr ? ArTypes : ApTypes;

        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");

        var allDocs = await _db.Documents
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId
                && docTypes.Contains(d.DocumentType) && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .OrderByDescending(d => d.DocumentDate)
            .ToListAsync();

        var openDocs = allDocs.Where(d => d.BalanceDue > 0).ToList();
        var paidDocs = allDocs.Where(d => d.Status == DocumentStatus.Paid).ToList();

        var totalOutstanding = openDocs.Sum(d => d.BalanceDue);
        var overdueDocs = openDocs.Where(d => d.DueDate.HasValue && d.DueDate.Value < now).ToList();
        var totalOverdue = overdueDocs.Sum(d => d.BalanceDue);

        // Avg payment days (from paid invoices)
        var payDays = paidDocs
            .Where(d => d.DueDate.HasValue)
            .Select(d => (d.DocumentDate - d.DueDate!.Value).TotalDays)
            .ToList();
        var avgPayDays = payDays.Count > 0 ? payDays.Average() : 0;

        // DSO for this contact
        var yearAgo = now.AddMonths(-12);
        var revenue12 = allDocs.Where(d => d.DocumentDate >= yearAgo).Sum(d => d.TotalAmount);
        var contactDso = revenue12 > 0 ? (double)totalOutstanding / (double)revenue12 * 365 : 0;
        var paid12 = paidDocs.Where(d => d.DocumentDate >= yearAgo).Sum(d => d.TotalAmount);

        // Credit info
        var creditSetting = await _db.Set<Models.Entities.ContactCreditSetting>()
            .FirstOrDefaultAsync(c => c.ContactId == contactId);

        // Outstanding documents
        var outstandingItems = openDocs.Select(d => new ContactDocumentItem(
            d.Id, d.DocumentNumber, d.DocumentType.ToString(), d.DocumentDate, d.DueDate,
            d.TotalAmount, d.PaidAmount, d.BalanceDue,
            d.DueDate.HasValue ? Math.Max(0, (int)(now - d.DueDate.Value).TotalDays) : 0,
            d.Status.ToString())).ToList();

        var recentPaid = paidDocs.Take(20).Select(d => new ContactDocumentItem(
            d.Id, d.DocumentNumber, d.DocumentType.ToString(), d.DocumentDate, d.DueDate,
            d.TotalAmount, d.PaidAmount, d.BalanceDue, 0, d.Status.ToString())).ToList();

        // Payments
        var payments = await _db.Payments
            .Include(p => p.Document)
            .Where(p => p.CompanyId == companyId && p.Document != null && p.Document.ContactId == contactId
                && docTypes.Contains(p.Document.DocumentType))
            .OrderByDescending(p => p.PaymentDate)
            .Take(30)
            .ToListAsync();

        var paymentItems = payments.Select(p => new ContactPaymentItem(
            p.Id, p.PaymentNumber, p.PaymentDate, p.Amount,
            p.Document?.DocumentNumber, p.PaymentMethod.ToString())).ToList();

        // Monthly history (last 12 months)
        var monthly = new List<ContactMonthlyItem>();
        for (int i = 11; i >= 0; i--)
        {
            var m = now.AddMonths(-i);
            var mStart = new DateTime(m.Year, m.Month, 1);
            var mEnd = mStart.AddMonths(1);
            var invoiced = allDocs.Where(d => d.DocumentDate >= mStart && d.DocumentDate < mEnd).Sum(d => d.TotalAmount);
            var paidM = allDocs.Where(d => d.DocumentDate >= mStart && d.DocumentDate < mEnd).Sum(d => d.PaidAmount);
            var bal = allDocs.Where(d => d.DocumentDate < mEnd).Sum(d => d.BalanceDue);
            monthly.Add(new ContactMonthlyItem(m.Year, m.Month, invoiced, paidM, bal));
        }

        return new ContactArApDetailResponse(
            contactId, contact.Name, contact.TaxId, contact.Phone, contact.Email,
            totalOutstanding, totalOverdue, paid12,
            (decimal)Math.Round(Math.Abs(avgPayDays), 1),
            (decimal)Math.Round(contactDso, 1),
            allDocs.Count, overdueDocs.Count,
            creditSetting?.CreditLimit, creditSetting?.AvailableCredit,
            creditSetting?.IsOnHold ?? false,
            BuildAgingSummary(openDocs, now),
            outstandingItems, recentPaid, paymentItems, monthly);
    }

    public async Task<BadDebtAnalysisResponse> GetBadDebtAnalysisAsync(Guid companyId)
    {
        var now = DateTime.UtcNow.Date;
        var arOpen = await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId && ArTypes.Contains(d.DocumentType)
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                && d.BalanceDue > 0)
            .ToListAsync();

        var totalAr = arOpen.Sum(d => d.BalanceDue);
        var totalOverdue = arOpen.Where(d => d.DueDate.HasValue && d.DueDate.Value < now).Sum(d => d.BalanceDue);

        // Thai accounting standard provision rates
        var bucketDefs = new[] {
            ("ปัจจุบัน (ยังไม่ครบกำหนด)", 0, 0, 0.01m),
            ("ค้างชำระ 1-30 วัน", 1, 30, 0.02m),
            ("ค้างชำระ 31-60 วัน", 31, 60, 0.05m),
            ("ค้างชำระ 61-90 วัน", 61, 90, 0.10m),
            ("ค้างชำระ 91-180 วัน", 91, 180, 0.25m),
            ("ค้างชำระ 181-365 วัน", 181, 365, 0.50m),
            ("ค้างชำระมากกว่า 365 วัน", 366, 9999, 1.00m),
        };

        var buckets = new List<BadDebtBucket>();
        var totalEstLoss = 0m;

        foreach (var (label, from, to, rate) in bucketDefs)
        {
            var matching = arOpen.Where(d =>
            {
                var ageDays = d.DueDate.HasValue ? Math.Max(0, (int)(now - d.DueDate.Value).TotalDays) : 0;
                return ageDays >= from && ageDays <= to;
            }).ToList();

            var amount = matching.Sum(d => d.BalanceDue);
            var estLoss = amount * rate;
            totalEstLoss += estLoss;

            buckets.Add(new BadDebtBucket(label, amount, rate * 100, estLoss, matching.Count));
        }

        // Risk contacts (overdue > 60 days)
        var riskContacts = arOpen
            .Where(d => d.DueDate.HasValue && (now - d.DueDate.Value).TotalDays > 60)
            .GroupBy(d => new { d.ContactId, d.Contact.Name })
            .Select(g =>
            {
                var maxDays = g.Max(d => (int)(now - d.DueDate!.Value).TotalDays);
                var totalOd = g.Sum(d => d.BalanceDue);
                var riskLevel = maxDays switch
                {
                    > 365 => "Critical",
                    > 180 => "High",
                    > 90 => "Medium",
                    _ => "Low"
                };
                var provRate = maxDays switch
                {
                    > 365 => 1.00m,
                    > 180 => 0.50m,
                    > 90 => 0.25m,
                    _ => 0.10m
                };
                return new BadDebtContactItem(g.Key.ContactId, g.Key.Name, totalOd, maxDays, totalOd * provRate, riskLevel);
            })
            .OrderByDescending(c => c.EstimatedLoss)
            .ToList();

        return new BadDebtAnalysisResponse(totalAr, totalOverdue, totalEstLoss, buckets, riskContacts);
    }

    private static ArApAgingSummary BuildAgingSummary(List<Models.Entities.Document> docs, DateTime now)
    {
        decimal current = 0, d1 = 0, d31 = 0, d61 = 0, d90 = 0;
        foreach (var d in docs)
        {
            var days = d.DueDate.HasValue ? Math.Max(0, (int)(now - d.DueDate.Value).TotalDays) : 0;
            if (days == 0) current += d.BalanceDue;
            else if (days <= 30) d1 += d.BalanceDue;
            else if (days <= 60) d31 += d.BalanceDue;
            else if (days <= 90) d61 += d.BalanceDue;
            else d90 += d.BalanceDue;
        }
        return new ArApAgingSummary(current, d1, d31, d61, d90, current + d1 + d31 + d61 + d90);
    }
}
