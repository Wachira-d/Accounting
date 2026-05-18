using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// Document completeness check (Phase K) — for a given period detects:
///   1. Sequential gaps in document numbering per DocumentType (e.g. INV-001,
///      INV-003 with INV-002 missing — RD audit red flag).
///   2. Approved documents whose auto-post JE never landed.
///
/// Helps catch silent failures BEFORE the accountant files VAT, when
/// catching them later means an amendment.
/// </summary>
public class DocumentCompletenessService
{
    private readonly AccountingDbContext _db;
    public DocumentCompletenessService(AccountingDbContext db) { _db = db; }

    public record GapIssue(string DocumentType, string ExpectedNumber, string Before, string After);
    public record MissingJeIssue(Guid DocumentId, string DocumentNumber, DocumentType DocumentType, DateTime DocumentDate, decimal TotalAmount);

    public record CompletenessResult(
        int Year, int Month,
        int DocumentCount,
        int SequentialGapsCount,
        int MissingJeCount,
        List<GapIssue> Gaps,
        List<MissingJeIssue> MissingJes);

    public async Task<CompletenessResult> AnalyzeAsync(Guid companyId, int year, int month)
    {
        var periodStart = new DateTime(year, month, 1);
        var periodEnd = periodStart.AddMonths(1);

        // Load every non-void document in the period.
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.DocumentDate >= periodStart && d.DocumentDate < periodEnd
                && d.Status != DocumentStatus.Voided)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.TotalAmount, d.Status })
            .ToListAsync();

        // 1. Sequential gap detection per (DocumentType, prefix).
        // Document numbers typically look like "INV-202604-0001". We extract
        // the trailing numeric run after the LAST '-' and check for gaps in
        // that integer within a (Type, NumberPrefix) bucket.
        var gaps = new List<GapIssue>();
        var byBucket = docs.GroupBy(d => new {
            d.DocumentType,
            Prefix = ExtractPrefix(d.DocumentNumber),
        });
        foreach (var bucket in byBucket)
        {
            var ordered = bucket
                .Select(d => new { d.DocumentNumber, Serial = ExtractSerial(d.DocumentNumber) })
                .Where(x => x.Serial.HasValue)
                .OrderBy(x => x.Serial!.Value)
                .ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                var cur = ordered[i];
                if (cur.Serial!.Value - prev.Serial!.Value > 1)
                {
                    // Report each missing serial in the gap (cap at 10 per gap).
                    for (var missing = prev.Serial!.Value + 1; missing < cur.Serial!.Value && missing < prev.Serial!.Value + 11; missing++)
                    {
                        gaps.Add(new GapIssue(
                            bucket.Key.DocumentType.ToString(),
                            $"{bucket.Key.Prefix}{missing.ToString("D" + GetSerialPadWidth(prev.DocumentNumber))}",
                            prev.DocumentNumber,
                            cur.DocumentNumber));
                    }
                }
            }
        }

        // 2. Approved-but-no-JE detection.
        // For documents that should auto-post (Invoice/TaxInvoice/Receipt/PV
        // when Approved), find ones with no JournalEntry pointing back.
        var approvedDocIds = docs.Where(d =>
            (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Sent
             || d.Status == DocumentStatus.PartiallyPaid || d.Status == DocumentStatus.Paid)
            && (d.DocumentType == DocumentType.Invoice
                || d.DocumentType == DocumentType.TaxInvoice
                || d.DocumentType == DocumentType.Receipt
                || d.DocumentType == DocumentType.PaymentVoucher
                || d.DocumentType == DocumentType.PurchaseInvoice
                || d.DocumentType == DocumentType.Expense
                || d.DocumentType == DocumentType.DebitNote
                || d.DocumentType == DocumentType.CreditNote))
            .Select(d => d.Id).ToList();
        var jeDocLinks = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId
                && j.SourceDocumentId != null
                && approvedDocIds.Contains(j.SourceDocumentId.Value)
                && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed))
            .Select(j => j.SourceDocumentId!.Value)
            .Distinct()
            .ToListAsync();
        var jeDocSet = jeDocLinks.ToHashSet();
        var missingJes = docs
            .Where(d => approvedDocIds.Contains(d.Id) && !jeDocSet.Contains(d.Id))
            .Select(d => new MissingJeIssue(d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.TotalAmount))
            .ToList();

        return new CompletenessResult(year, month, docs.Count,
            gaps.Count, missingJes.Count, gaps, missingJes);
    }

    /// <summary>Strip the trailing numeric run and return the leading
    /// non-numeric prefix. "INV-202604-0001" → "INV-202604-".</summary>
    private static string ExtractPrefix(string docNumber)
    {
        var i = docNumber.Length - 1;
        while (i >= 0 && char.IsDigit(docNumber[i])) i--;
        return docNumber.Substring(0, i + 1);
    }

    private static int? ExtractSerial(string docNumber)
    {
        var i = docNumber.Length;
        var start = i;
        while (start > 0 && char.IsDigit(docNumber[start - 1])) start--;
        if (start == i) return null;
        return int.TryParse(docNumber.Substring(start), out var v) ? v : null;
    }

    private static int GetSerialPadWidth(string docNumber)
    {
        var i = docNumber.Length;
        var start = i;
        while (start > 0 && char.IsDigit(docNumber[start - 1])) start--;
        return Math.Max(1, i - start);
    }
}
