using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// Global search (Phase M). One endpoint that the header search bar hits;
/// returns matches across Documents / Journal Entries / Contacts so the
/// operator doesn't have to remember WHICH list page contains what they
/// need. Plain ILIKE-style substring matching — no full-text index
/// required (keeps the search instant on Postgres without setup).
/// </summary>
public class GlobalSearchService
{
    private readonly AccountingDbContext _db;
    public GlobalSearchService(AccountingDbContext db) { _db = db; }

    public record DocHit(Guid Id, string DocumentNumber, string DocumentType, DateTime Date,
        decimal TotalAmount, string Status, string? ContactName);
    public record JeHit(Guid Id, string EntryNumber, DateTime EntryDate, string JournalType,
        decimal TotalDebit, string Status, string? Description);
    public record ContactHit(Guid Id, string Name, string? TaxId, string ContactType);

    public record SearchResult(List<DocHit> Documents, List<JeHit> JournalEntries,
        List<ContactHit> Contacts, int TotalHits);

    public async Task<SearchResult> SearchAsync(Guid companyId, string query, int perBucket = 20)
    {
        var q = query.Trim();
        var qLower = q.ToLowerInvariant();

        // Documents — match on number, contact name, reference, notes, OR exact total amount.
        decimal? amountQuery = decimal.TryParse(q.Replace(",", ""), out var amt) ? amt : (decimal?)null;
        // ⚠️ ห้าม project d.Contact.Name ตรง ๆ ใน SQL — Contact มี query filter
        // !IsDeleted → เอกสารที่ contact ถูกลบจะได้ชื่อ null. select ContactId แล้ว
        // resolve ชื่อจาก dict (IgnoreQueryFilters) ให้ครบ.
        var docRows = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && (d.DocumentNumber.ToLower().Contains(qLower)
                    || (d.Reference != null && d.Reference.ToLower().Contains(qLower))
                    || (d.Notes != null && d.Notes.ToLower().Contains(qLower))
                    || (d.Contact != null && d.Contact.Name.ToLower().Contains(qLower))
                    || (amountQuery.HasValue && d.TotalAmount == amountQuery.Value)))
            .OrderByDescending(d => d.DocumentDate)
            .Take(perBucket)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType,
                d.DocumentDate, d.TotalAmount, d.Status, d.ContactId })
            .ToListAsync();
        var docCids = docRows.Select(r => r.ContactId).Distinct().ToList();
        var docCmap = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && docCids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);
        var docs = docRows.Select(r => new DocHit(
            r.Id, r.DocumentNumber, r.DocumentType.ToString(),
            r.DocumentDate, r.TotalAmount, r.Status.ToString(),
            docCmap.GetValueOrDefault(r.ContactId))).ToList();

        // Journal Entries — number / reference / description / amount
        var jes = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId
                && (j.EntryNumber.ToLower().Contains(qLower)
                    || (j.Reference != null && j.Reference.ToLower().Contains(qLower))
                    || (j.Description != null && j.Description.ToLower().Contains(qLower))
                    || (amountQuery.HasValue && j.TotalDebit == amountQuery.Value)))
            .OrderByDescending(j => j.EntryDate)
            .Take(perBucket)
            .Select(j => new JeHit(
                j.Id, j.EntryNumber, j.EntryDate, j.JournalType.ToString(),
                j.TotalDebit, j.Status.ToString(), j.Description))
            .ToListAsync();

        // Contacts — name / tax id / phone / email
        var contacts = await _db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted
                && (c.Name.ToLower().Contains(qLower)
                    || (c.TaxId != null && c.TaxId.Contains(q))
                    || (c.Phone != null && c.Phone.Contains(q))
                    || (c.Email != null && c.Email.ToLower().Contains(qLower))))
            .OrderBy(c => c.Name)
            .Take(perBucket)
            .Select(c => new ContactHit(c.Id, c.Name, c.TaxId, c.ContactType.ToString()))
            .ToListAsync();

        return new SearchResult(docs, jes, contacts, docs.Count + jes.Count + contacts.Count);
    }
}
