using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Search;

/// <summary>
/// Cross-entity quick search — the Cmd/Ctrl-K palette. Searches
/// Contacts (name/tax id), Products (sku/name), Documents
/// (number/notes), and JournalEntries (entry number) with substring
/// ILIKE. Returns mixed result list ranked by entity kind + last
/// activity.
///
/// Performance: each kind is a separate query bounded to 8 rows so
/// the palette feels snappy even on large tenants. With pg_trgm
/// indexes (already migrated via DatabaseMigrationHelper) the ILIKE
/// queries run against trigram indexes — sub-50ms on 100k row tables.
/// </summary>
public interface IQuickSearchService
{
    Task<IReadOnlyList<QuickSearchHit>> SearchAsync(Guid companyId,
        string query, int perKindLimit = 8, CancellationToken ct = default);
}

public sealed record QuickSearchHit(
    string Kind,                // "Contact" | "Product" | "Document" | "JournalEntry"
    string Id,
    string Title,
    string? Subtitle,
    string? Detail,
    string DeepLink);            // /pages/<page>?id=<id>

public class QuickSearchService : IQuickSearchService
{
    private readonly AccountingDbContext _db;

    public QuickSearchService(AccountingDbContext db) { _db = db; }

    public async Task<IReadOnlyList<QuickSearchHit>> SearchAsync(Guid companyId,
        string query, int perKindLimit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return Array.Empty<QuickSearchHit>();

        var pattern = $"%{query.Trim()}%";
        var results = new List<QuickSearchHit>();

        var contacts = await _db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted
                && (EF.Functions.ILike(c.Name, pattern)
                    || (c.TaxId != null && EF.Functions.ILike(c.TaxId, pattern))))
            .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt)
            .Take(perKindLimit)
            .Select(c => new { c.Id, c.Name, c.TaxId, c.ContactType })
            .ToListAsync(ct);
        foreach (var c in contacts)
            results.Add(new QuickSearchHit("Contact", c.Id.ToString(),
                Title: c.Name,
                Subtitle: c.ContactType.ToString(),
                Detail: c.TaxId,
                DeepLink: $"/pages/contacts.html?id={c.Id}"));

        var products = await _db.Products.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                && (EF.Functions.ILike(p.Name, pattern)
                    || (p.SKU != null && EF.Functions.ILike(p.SKU, pattern))))
            .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt)
            .Take(perKindLimit)
            .Select(p => new { p.Id, p.Name, p.SKU, p.CurrentStock })
            .ToListAsync(ct);
        foreach (var p in products)
            results.Add(new QuickSearchHit("Product", p.Id.ToString(),
                Title: p.Name,
                Subtitle: p.SKU,
                Detail: $"คงเหลือ {p.CurrentStock:N0}",
                DeepLink: $"/pages/products.html?id={p.Id}"));

        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && (EF.Functions.ILike(d.DocumentNumber, pattern)
                    || (d.Notes != null && EF.Functions.ILike(d.Notes, pattern))))
            .OrderByDescending(d => d.DocumentDate)
            .Take(perKindLimit)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.TotalAmount })
            .ToListAsync(ct);
        foreach (var d in docs)
            results.Add(new QuickSearchHit("Document", d.Id.ToString(),
                Title: d.DocumentNumber,
                Subtitle: d.DocumentType.ToString(),
                Detail: $"{d.DocumentDate:yyyy-MM-dd} • {d.TotalAmount:N2}",
                DeepLink: $"/pages/documents.html?id={d.Id}"));

        var jes = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && !j.IsDeleted
                && (EF.Functions.ILike(j.EntryNumber, pattern)
                    || (j.Description != null && EF.Functions.ILike(j.Description, pattern))
                    || (j.Reference != null && EF.Functions.ILike(j.Reference, pattern))))
            .OrderByDescending(j => j.EntryDate)
            .Take(perKindLimit)
            .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.Description })
            .ToListAsync(ct);
        foreach (var j in jes)
            results.Add(new QuickSearchHit("JournalEntry", j.Id.ToString(),
                Title: j.EntryNumber,
                Subtitle: $"{j.EntryDate:yyyy-MM-dd}",
                Detail: j.Description,
                DeepLink: $"/pages/journal-entries.html?id={j.Id}"));

        return results;
    }
}
