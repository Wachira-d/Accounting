using System.Globalization;
using System.Text;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Migration;

/// <summary>
/// Competitor-system import framework — for SMEs migrating from
/// Express / PEAK / FlowAccount. Each adapter handles ONE competitor
/// format and exposes the same canonical pipeline:
///
///   1. ParsePreview(stream) — sniff column shape + return first 50
///      rows + detected ContactType / DocumentType so admin can
///      confirm-or-correct the mapping before commit.
///   2. ImportChunk(stream, options) — actually create Contact /
///      Product / Document rows, idempotent on external_ref so
///      re-running the same file doesn't double-create.
///
/// The adapter interface lets us extend to BeeAccount, AccRevo, etc.
/// later by dropping in another class — no framework changes needed.
/// </summary>
public interface ICompetitorImportAdapter
{
    /// <summary>System name (e.g. "Express", "PEAK", "FlowAccount").
    /// Used as the discriminator + audit tag.</summary>
    string SourceSystem { get; }

    /// <summary>"Contacts" | "Products" | "Documents" — what this
    /// adapter pass produces.</summary>
    string EntityKind { get; }

    /// <summary>Heuristic — looks at the file's first row / header
    /// signature and returns true if THIS adapter can parse it.</summary>
    bool CanHandle(string fileContent, string filename);

    Task<ImportPreview> PreviewAsync(string fileContent, CancellationToken ct);

    Task<ImportResult> ImportAsync(Guid companyId, string fileContent,
        ImportOptions options, CancellationToken ct);
}

public sealed record ImportPreview(
    string SourceSystem,
    string EntityKind,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string>> SampleRows,
    int TotalRows,
    IReadOnlyList<string> Warnings);

public sealed record ImportOptions(
    bool DryRun,
    bool SkipDuplicates,
    string? SourceTag);                 // appended to CreatedBy for audit

public sealed record ImportResult(
    string SourceSystem,
    int RowsRead,
    int RowsImported,
    int RowsSkipped,
    int RowsFailed,
    IReadOnlyList<string> Errors);

/// <summary>
/// Express format — CSV with Thai headers: รหัส, ชื่อ, เลขประจำตัว
/// ผู้เสียภาษี, ที่อยู่. Express exports tab-separated by default;
/// adapter sniffs separator.
/// </summary>
public class ExpressContactsAdapter : ICompetitorImportAdapter
{
    private readonly AccountingDbContext _db;
    public ExpressContactsAdapter(AccountingDbContext db) { _db = db; }

    public string SourceSystem => "Express";
    public string EntityKind => "Contacts";

    public bool CanHandle(string fileContent, string filename)
    {
        if (filename.Contains("express", StringComparison.OrdinalIgnoreCase)) return true;
        var head = fileContent.Length > 1000 ? fileContent[..1000] : fileContent;
        // Express exports usually have "รหัสลูกค้า" or "รหัสผู้จัด" as
        // a distinctive header token.
        return head.Contains("รหัสลูกค้า") || head.Contains("รหัสผู้จัด")
            || head.Contains("เลขประจำตัวผู้เสียภาษี");
    }

    public Task<ImportPreview> PreviewAsync(string fileContent, CancellationToken ct)
    {
        var rows = ParseRows(fileContent, out var cols, out var sep, out var warnings);
        var sample = rows.Take(50).Select(r => (IReadOnlyDictionary<string, string>)r).ToList();
        return Task.FromResult(new ImportPreview(SourceSystem, EntityKind, cols, sample, rows.Count, warnings));
    }

    public async Task<ImportResult> ImportAsync(Guid companyId, string fileContent,
        ImportOptions options, CancellationToken ct)
    {
        var rows = ParseRows(fileContent, out _, out _, out var warnings);
        int imported = 0, skipped = 0, failed = 0;
        var errors = new List<string>(warnings);
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) break;
            var taxId = FirstNonEmpty(row, "เลขประจำตัวผู้เสียภาษี", "TaxId", "TAX_ID");
            var name = FirstNonEmpty(row, "ชื่อลูกค้า", "ชื่อผู้จัดจำหน่าย", "ชื่อ", "Name");
            if (string.IsNullOrEmpty(name)) { skipped++; continue; }
            // Idempotency: skip if taxId-based contact already exists.
            if (!string.IsNullOrEmpty(taxId))
            {
                var exists = await _db.Contacts.AnyAsync(
                    c => c.CompanyId == companyId && c.TaxId == taxId && !c.IsDeleted, ct);
                if (exists) { skipped++; continue; }
            }
            try
            {
                if (!options.DryRun)
                {
                    _db.Contacts.Add(new Contact
                    {
                        CompanyId = companyId,
                        Name = name,
                        TaxId = taxId ?? "",
                        ContactType = ContactType.JuristicPerson,
                        IsCustomer = true,
                        IsSupplier = true,
                        Address = FirstNonEmpty(row, "ที่อยู่", "Address"),
                        Phone = FirstNonEmpty(row, "โทรศัพท์", "Phone"),
                        Email = FirstNonEmpty(row, "อีเมล", "Email"),
                        CreatedBy = $"Migrate:Express:{options.SourceTag ?? "manual"}",
                    });
                }
                imported++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Row {imported + skipped + failed}: {ex.Message}");
            }
        }
        if (!options.DryRun && imported > 0) await _db.SaveChangesAsync(ct);
        return new ImportResult(SourceSystem, rows.Count, imported, skipped, failed, errors);
    }

    private static List<Dictionary<string, string>> ParseRows(string content,
        out List<string> columns, out char separator, out List<string> warnings)
    {
        warnings = new();
        columns = new();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) { separator = ','; return new(); }
        // Sniff: try tab first (Express default), fall back to comma.
        separator = lines[0].Contains('\t') ? '\t' : ',';
        columns = lines[0].TrimEnd('\r').Split(separator).Select(c => c.Trim()).ToList();
        var rows = new List<Dictionary<string, string>>(lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = raw.Split(separator);
            var d = new Dictionary<string, string>(columns.Count);
            for (int c = 0; c < columns.Count && c < cells.Length; c++)
                d[columns[c]] = cells[c].Trim();
            rows.Add(d);
        }
        return rows;
    }

    private static string? FirstNonEmpty(IDictionary<string, string> row, params string[] keys)
    {
        foreach (var k in keys)
            if (row.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }
}

/// <summary>
/// PEAK format — exports as CSV with English camelCase headers:
/// "contactCode", "contactName", "taxNumber", "address", "phone".
/// </summary>
public class PeakContactsAdapter : ICompetitorImportAdapter
{
    private readonly AccountingDbContext _db;
    public PeakContactsAdapter(AccountingDbContext db) { _db = db; }

    public string SourceSystem => "PEAK";
    public string EntityKind => "Contacts";

    public bool CanHandle(string fileContent, string filename)
    {
        if (filename.Contains("peak", StringComparison.OrdinalIgnoreCase)) return true;
        var head = fileContent.Length > 1000 ? fileContent[..1000] : fileContent;
        return head.Contains("contactCode") || head.Contains("taxNumber");
    }

    public async Task<ImportPreview> PreviewAsync(string fileContent, CancellationToken ct)
    {
        var rows = ParseCsv(fileContent, out var cols);
        return await Task.FromResult(new ImportPreview(SourceSystem, EntityKind, cols,
            rows.Take(50).Select(r => (IReadOnlyDictionary<string, string>)r).ToList(),
            rows.Count, Array.Empty<string>()));
    }

    public async Task<ImportResult> ImportAsync(Guid companyId, string fileContent,
        ImportOptions options, CancellationToken ct)
    {
        var rows = ParseCsv(fileContent, out _);
        int imported = 0, skipped = 0, failed = 0;
        var errors = new List<string>();
        foreach (var row in rows)
        {
            var taxId = row.GetValueOrDefault("taxNumber") ?? row.GetValueOrDefault("TaxNumber");
            var name = row.GetValueOrDefault("contactName") ?? row.GetValueOrDefault("ContactName");
            if (string.IsNullOrEmpty(name)) { skipped++; continue; }
            if (!string.IsNullOrEmpty(taxId))
            {
                var exists = await _db.Contacts.AnyAsync(
                    c => c.CompanyId == companyId && c.TaxId == taxId && !c.IsDeleted, ct);
                if (exists) { skipped++; continue; }
            }
            try
            {
                if (!options.DryRun)
                {
                    _db.Contacts.Add(new Contact
                    {
                        CompanyId = companyId,
                        Name = name,
                        TaxId = taxId ?? "",
                        ContactType = ContactType.JuristicPerson,
                        IsCustomer = true,
                        IsSupplier = true,
                        Address = row.GetValueOrDefault("address"),
                        Phone = row.GetValueOrDefault("phone"),
                        Email = row.GetValueOrDefault("email"),
                        CreatedBy = $"Migrate:PEAK:{options.SourceTag ?? "manual"}",
                    });
                }
                imported++;
            }
            catch (Exception ex) { failed++; errors.Add(ex.Message); }
        }
        if (!options.DryRun && imported > 0) await _db.SaveChangesAsync(ct);
        return new ImportResult(SourceSystem, rows.Count, imported, skipped, failed, errors);
    }

    private static List<Dictionary<string, string>> ParseCsv(string content, out List<string> columns)
    {
        columns = new();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new();
        columns = lines[0].TrimEnd('\r').Split(',').Select(c => c.Trim('"').Trim()).ToList();
        var rows = new List<Dictionary<string, string>>(lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = SplitCsvLine(raw);
            var d = new Dictionary<string, string>(columns.Count);
            for (int c = 0; c < columns.Count && c < cells.Count; c++)
                d[columns[c]] = cells[c].Trim('"').Trim();
            rows.Add(d);
        }
        return rows;
    }

    private static List<string> SplitCsvLine(string line)
    {
        // RFC4180-lite — honour quotes for embedded commas.
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == ',' && !inQuotes) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }
}

/// <summary>
/// FlowAccount format — CSV with Thai-English mixed headers:
/// "code", "name (TH)", "name (EN)", "tax id".
/// </summary>
public class FlowAccountContactsAdapter : ICompetitorImportAdapter
{
    private readonly AccountingDbContext _db;
    public FlowAccountContactsAdapter(AccountingDbContext db) { _db = db; }

    public string SourceSystem => "FlowAccount";
    public string EntityKind => "Contacts";

    public bool CanHandle(string fileContent, string filename)
    {
        if (filename.Contains("flow", StringComparison.OrdinalIgnoreCase)) return true;
        var head = fileContent.Length > 1000 ? fileContent[..1000] : fileContent;
        return (head.Contains("name (TH)") && head.Contains("tax id"))
            || head.Contains("FlowAccount");
    }

    public Task<ImportPreview> PreviewAsync(string fileContent, CancellationToken ct)
        => PeakContactsAdapter_PreviewHelper(fileContent, SourceSystem, EntityKind);

    public async Task<ImportResult> ImportAsync(Guid companyId, string fileContent,
        ImportOptions options, CancellationToken ct)
    {
        var rows = PeakContactsAdapter_ParseCsv(fileContent);
        int imported = 0, skipped = 0, failed = 0;
        var errors = new List<string>();
        foreach (var row in rows)
        {
            var taxId = row.GetValueOrDefault("tax id") ?? row.GetValueOrDefault("Tax ID");
            var name = row.GetValueOrDefault("name (TH)")
                ?? row.GetValueOrDefault("name (EN)")
                ?? row.GetValueOrDefault("Name");
            if (string.IsNullOrEmpty(name)) { skipped++; continue; }
            if (!string.IsNullOrEmpty(taxId))
            {
                var exists = await _db.Contacts.AnyAsync(
                    c => c.CompanyId == companyId && c.TaxId == taxId && !c.IsDeleted, ct);
                if (exists) { skipped++; continue; }
            }
            try
            {
                if (!options.DryRun)
                {
                    _db.Contacts.Add(new Contact
                    {
                        CompanyId = companyId,
                        Name = name,
                        TaxId = taxId ?? "",
                        ContactType = ContactType.JuristicPerson,
                        IsCustomer = true,
                        IsSupplier = true,
                        Phone = row.GetValueOrDefault("phone"),
                        Email = row.GetValueOrDefault("email"),
                        CreatedBy = $"Migrate:FlowAccount:{options.SourceTag ?? "manual"}",
                    });
                }
                imported++;
            }
            catch (Exception ex) { failed++; errors.Add(ex.Message); }
        }
        if (!options.DryRun && imported > 0) await _db.SaveChangesAsync(ct);
        return new ImportResult(SourceSystem, rows.Count, imported, skipped, failed, errors);
    }

    // Shared CSV helpers reused from the PEAK adapter — kept private static
    // so a future refactor can extract to a base class.
    internal static Task<ImportPreview> PeakContactsAdapter_PreviewHelper(string content,
        string system, string kind)
    {
        var rows = PeakContactsAdapter_ParseCsv(content);
        var cols = rows.Count > 0 ? rows[0].Keys.ToList() : new List<string>();
        return Task.FromResult(new ImportPreview(system, kind, cols,
            rows.Take(50).Select(r => (IReadOnlyDictionary<string, string>)r).ToList(),
            rows.Count, Array.Empty<string>()));
    }

    internal static List<Dictionary<string, string>> PeakContactsAdapter_ParseCsv(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new();
        var cols = lines[0].TrimEnd('\r').Split(',').Select(c => c.Trim('"').Trim()).ToList();
        var rows = new List<Dictionary<string, string>>(lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = SplitCsvLine(raw);
            var d = new Dictionary<string, string>(cols.Count);
            for (int c = 0; c < cols.Count && c < cells.Count; c++)
                d[cols[c]] = cells[c].Trim('"').Trim();
            rows.Add(d);
        }
        return rows;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == ',' && !inQuotes) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }
}

/// <summary>
/// Coordinator — receives a file + filename, sniffs the right adapter,
/// returns preview or runs the import.
/// </summary>
public interface ICompetitorImportCoordinator
{
    Task<(ICompetitorImportAdapter? Adapter, ImportPreview? Preview)> PreviewAsync(
        string fileContent, string filename, CancellationToken ct);

    Task<ImportResult?> ImportAsync(Guid companyId, string fileContent, string filename,
        ImportOptions options, CancellationToken ct);
}

public class CompetitorImportCoordinator : ICompetitorImportCoordinator
{
    private readonly IEnumerable<ICompetitorImportAdapter> _adapters;
    private readonly ILogger<CompetitorImportCoordinator> _logger;

    public CompetitorImportCoordinator(IEnumerable<ICompetitorImportAdapter> adapters,
        ILogger<CompetitorImportCoordinator> logger)
    { _adapters = adapters; _logger = logger; }

    public async Task<(ICompetitorImportAdapter? Adapter, ImportPreview? Preview)> PreviewAsync(
        string fileContent, string filename, CancellationToken ct)
    {
        var adapter = _adapters.FirstOrDefault(a => a.CanHandle(fileContent, filename));
        if (adapter == null) return (null, null);
        var preview = await adapter.PreviewAsync(fileContent, ct);
        return (adapter, preview);
    }

    public async Task<ImportResult?> ImportAsync(Guid companyId, string fileContent, string filename,
        ImportOptions options, CancellationToken ct)
    {
        var adapter = _adapters.FirstOrDefault(a => a.CanHandle(fileContent, filename));
        if (adapter == null)
        {
            _logger.LogWarning("No adapter found for filename {File}", filename);
            return null;
        }
        return await adapter.ImportAsync(companyId, fileContent, options, ct);
    }
}
