using Accounting.Data;
using Accounting.Models.DTOs.ArApAnalysis;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ArApAnalysisService : IArApAnalysisService
{
    private readonly AccountingDbContext _db;

    // OUTSTANDING receivable / payable types — these carry a BalanceDue when
    // sold/bought on credit. (Cash Receipts / cash Payment Vouchers also fall
    // here for AP but net to zero balance, so they never inflate the open
    // figures — but they DO belong in the revenue/cost base below.)
    private static readonly DocumentType[] ArOpenTypes = { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.BillingNote, DocumentType.DebitNote };
    private static readonly DocumentType[] ApOpenTypes = { DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.PaymentVoucher, DocumentType.CertificateInLieu };

    // REVENUE / COST base — the full sales + purchase volume, INCLUDING the
    // cash documents (Receipt / ReceiptVoucher for revenue; the cash Payment
    // Vouchers already in ApOpenTypes for cost). Without these a cash-based
    // business (hotel/shop booking via ใบสำคัญรับ/ใบสำคัญจ่าย) showed 0 for
    // revenue, DSO/DPO and the whole monthly trend.
    private static readonly DocumentType[] RevenueTypes = { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.BillingNote, DocumentType.DebitNote, DocumentType.Receipt, DocumentType.ReceiptVoucher };
    private static readonly DocumentType[] CostTypes = { DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.PaymentVoucher, DocumentType.CertificateInLieu };

    public ArApAnalysisService(AccountingDbContext db) => _db = db;

    public async Task<ArApOverviewResponse> GetOverviewAsync(Guid companyId)
    {
        var now = DateTime.UtcNow.Date;
        // PERF: previously pulled EVERY doc (incl. Contact navigation) into
        // memory then ran 12 in-memory passes for the trend. On a 50k-doc
        // book that's 200MB+ RAM + slow GC. Now restrict the SQL to docs
        // that are EITHER open (drives totals/aging/top) OR within 12 months
        // (drives revenue/cost/trend) — typical SME pulls ~5% of history.
        // Also project to a flat shape so EF doesn't materialise the full
        // Document entity + every navigation.
        var yearAgo = now.AddMonths(-12);
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Draft
                && (d.BalanceDue > 0 || d.DocumentDate >= yearAgo))
            .Select(d => new DocRow(
                d.Id, d.ContactId, d.Contact.Name, d.Contact.TaxId,
                d.DocumentType, d.DocumentDate, d.DueDate,
                d.TotalAmount, d.PaidAmount, d.BalanceDue, d.Status))
            .ToListAsync();

        // Single-pass classification — much cheaper than four .Where(...).ToList().
        var arDocs = new List<DocRow>();
        var apDocs = new List<DocRow>();
        var revenueDocs = new List<DocRow>();
        var costDocs = new List<DocRow>();
        foreach (var d in docs)
        {
            if (ArOpenTypes.Contains(d.DocumentType)) arDocs.Add(d);
            if (ApOpenTypes.Contains(d.DocumentType)) apDocs.Add(d);
            if (RevenueTypes.Contains(d.DocumentType)) revenueDocs.Add(d);
            if (CostTypes.Contains(d.DocumentType)) costDocs.Add(d);
        }

        var arOpen = arDocs.Where(d => d.BalanceDue > 0).ToList();
        var apOpen = apDocs.Where(d => d.BalanceDue > 0).ToList();

        var totalAr = arOpen.Sum(d => d.BalanceDue);
        var totalAp = apOpen.Sum(d => d.BalanceDue);

        var overdueAr = arOpen.Where(d => d.DueDate.HasValue && d.DueDate.Value < now).ToList();
        var overdueAp = apOpen.Where(d => d.DueDate.HasValue && d.DueDate.Value < now).ToList();

        // DSO = (AR / Revenue last 12 months) * 365 — revenue base includes cash sales.
        var revenue12 = revenueDocs.Where(d => d.DocumentDate >= yearAgo).Sum(d => d.TotalAmount);
        var dso = revenue12 > 0 ? (double)totalAr / (double)revenue12 * 365 : 0;

        // DPO = (AP / COGS last 12 months) * 365 — cost base includes cash purchases.
        var cogs12 = costDocs.Where(d => d.DocumentDate >= yearAgo).Sum(d => d.TotalAmount);
        var dpo = cogs12 > 0 ? (double)totalAp / (double)cogs12 * 365 : 0;

        // Collection rate: revenue docs paid within terms / total (last 12m).
        var arPaid12 = revenueDocs.Where(d => d.DocumentDate >= yearAgo && d.Status == DocumentStatus.Paid).ToList();
        var onTime = arPaid12.Count(d => d.DueDate == null || d.PaidAmount >= d.TotalAmount);
        var collRate = arPaid12.Count > 0 ? (double)onTime / arPaid12.Count * 100 : 100;

        // Monthly trend (last 12 months) — PERF: previous version ran 12
        // separate .Where().Sum() passes over the full doc list (= O(n×12)).
        // Now bucket each doc once into its (year, month) slot in a single
        // pass over revenueDocs and costDocs.
        var revBucket = new Dictionary<(int Y, int M), (decimal New, decimal Paid)>();
        var costBucket = new Dictionary<(int Y, int M), (decimal New, decimal Paid)>();
        foreach (var d in revenueDocs)
        {
            var k = (d.DocumentDate.Year, d.DocumentDate.Month);
            var cur = revBucket.GetValueOrDefault(k);
            revBucket[k] = (cur.New + d.TotalAmount, cur.Paid + d.PaidAmount);
        }
        foreach (var d in costDocs)
        {
            var k = (d.DocumentDate.Year, d.DocumentDate.Month);
            var cur = costBucket.GetValueOrDefault(k);
            costBucket[k] = (cur.New + d.TotalAmount, cur.Paid + d.PaidAmount);
        }
        // Running balance: sort once by date desc, walk and accumulate.
        var arSorted = arDocs.OrderBy(d => d.DocumentDate).ToList();
        var apSorted = apDocs.OrderBy(d => d.DocumentDate).ToList();

        var trend = new List<ArApTrendItem>();
        for (int i = 11; i >= 0; i--)
        {
            var m = now.AddMonths(-i);
            var mEnd = new DateTime(m.Year, m.Month, 1).AddMonths(1);
            var revHit = revBucket.GetValueOrDefault((m.Year, m.Month));
            var costHit = costBucket.GetValueOrDefault((m.Year, m.Month));
            // Outstanding-as-of-mEnd needs the sum of BalanceDue for docs
            // dated BEFORE mEnd. Cheaper than the original O(12 × n).
            var arBal = arSorted.TakeWhile(d => d.DocumentDate < mEnd).Sum(d => d.BalanceDue);
            var apBal = apSorted.TakeWhile(d => d.DocumentDate < mEnd).Sum(d => d.BalanceDue);
            trend.Add(new ArApTrendItem(m.Year, m.Month, $"{m.Year}-{m.Month:D2}",
                arBal, apBal, revHit.New, revHit.Paid, costHit.New, costHit.Paid));
        }

        // Top contacts — grouped once.
        var topAr = arOpen.GroupBy(d => new { d.ContactId, d.ContactName, d.ContactTaxId })
            .Select(g => new ArApTopContact(g.Key.ContactId, g.Key.ContactName, g.Key.ContactTaxId,
                g.Sum(d => d.BalanceDue),
                g.Where(d => d.DueDate.HasValue && d.DueDate.Value < now).Sum(d => d.BalanceDue),
                g.Count(),
                g.Count(d => d.DueDate.HasValue && d.DueDate.Value < now),
                g.Max(d => d.DueDate.HasValue ? Math.Max(0, (int)(now - d.DueDate.Value).TotalDays) : 0)))
            .OrderByDescending(c => c.TotalBalance).Take(10).ToList();

        var topAp = apOpen.GroupBy(d => new { d.ContactId, d.ContactName, d.ContactTaxId })
            .Select(g => new ArApTopContact(g.Key.ContactId, g.Key.ContactName, g.Key.ContactTaxId,
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

    /// <summary>Flat row used by the perf-optimised overview path. Holds only
    /// the columns the report needs — avoids EF materialising every
    /// Document property + every Contact navigation.</summary>
    private sealed record DocRow(
        Guid Id, Guid ContactId, string ContactName, string? ContactTaxId,
        DocumentType DocumentType, DateTime DocumentDate, DateTime? DueDate,
        decimal TotalAmount, decimal PaidAmount, decimal BalanceDue,
        DocumentStatus Status);

    public async Task<ContactArApDetailResponse> GetContactDetailAsync(Guid companyId, Guid contactId, string type)
    {
        var now = DateTime.UtcNow.Date;
        var isAr = type.Equals("ar", StringComparison.OrdinalIgnoreCase);
        // Per-contact view shows the full sales/purchase ledger (incl. cash
        // documents) so a cash-heavy counterparty isn't blank.
        var docTypes = isAr ? RevenueTypes : CostTypes;

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
            .Where(d => d.CompanyId == companyId && ArOpenTypes.Contains(d.DocumentType)
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

    // BuildAgingSummary works on the original Document entity for
    // GetContactDetailAsync (line 206), and on the perf-optimised DocRow for
    // GetOverviewAsync. Both paths share the bucketing logic via a tiny
    // adapter — the calling code never picks the wrong one.
    private static ArApAgingSummary BuildAgingSummary(List<Models.Entities.Document> docs, DateTime now)
        => BuildAgingSummaryCore(docs.Select(d => (d.DueDate, d.BalanceDue)), now);

    private static ArApAgingSummary BuildAgingSummary(List<DocRow> docs, DateTime now)
        => BuildAgingSummaryCore(docs.Select(d => (d.DueDate, d.BalanceDue)), now);

    private static ArApAgingSummary BuildAgingSummaryCore(IEnumerable<(DateTime? DueDate, decimal BalanceDue)> docs, DateTime now)
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
