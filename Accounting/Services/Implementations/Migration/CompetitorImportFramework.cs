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
///   1. PreviewAsync(companyId, stream) — sniff column shape, return
///      first 50 rows AND detect conflicts (rows whose TaxId matches an
///      existing contact but with different Name/Phone/Email/Address).
///      Admin sees the diff before committing.
///   2. ImportAsync(companyId, stream, options) — actually create
///      Contact rows. For each conflict the caller picks an action
///      (Skip / Overwrite / Merge); rows without an explicit decision
///      fall back to options.DefaultConflictAction.
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

    Task<ImportPreview> PreviewAsync(Guid companyId, string fileContent, CancellationToken ct);

    Task<ImportResult> ImportAsync(Guid companyId, string fileContent,
        ImportOptions options, CancellationToken ct);
}

/// <summary>How to resolve a row whose key (TaxId) matches an existing
/// contact but with different field values.</summary>
public enum ConflictAction
{
    /// <summary>Keep the existing DB row untouched, ignore the import row.</summary>
    Skip = 0,
    /// <summary>Replace existing row's fields with the import row's values
    /// (only non-empty incoming fields overwrite).</summary>
    Overwrite = 1,
    /// <summary>Existing values win; import only fills in blanks (sensible
    /// default — protects manual edits while enriching sparse rows).</summary>
    Merge = 2,
}

/// <summary>One row that already exists in the DB and differs from the
/// import file. UI shows the user a side-by-side diff and asks for a
/// per-row ConflictAction. Generic across entity types — for contacts
/// Key is TaxId, for products it's Code, for accounts it's AccountCode.</summary>
public sealed record RowConflict(
    string Key,                                       // the join key (TaxId / Code / AccountCode)
    string ExistingLabel,                             // human-friendly name of the DB row
    string IncomingLabel,                             // human-friendly name of the file row
    IReadOnlyDictionary<string, string?> Existing,    // field → current value
    IReadOnlyDictionary<string, string?> Incoming,    // field → file value
    IReadOnlyList<string> DiffFields);                // names of fields that differ

public sealed record ImportPreview(
    string SourceSystem,
    string EntityKind,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string>> SampleRows,
    int TotalRows,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RowConflict> Conflicts,         // ← NEW
    int NewRowCount,                                  // ← NEW: count of rows without a DB match
    int DuplicateExactCount);                         // ← NEW: rows whose TaxId matches AND every field is identical (auto-skip, no UI prompt)

public sealed record ImportOptions(
    bool DryRun,
    bool SkipDuplicates,                              // legacy: when true and no per-row decision, behaves like Skip
    string? SourceTag,                                // appended to CreatedBy for audit
    IReadOnlyDictionary<string, ConflictAction>? Resolutions = null,   // key (TaxId) → user's pick
    ConflictAction DefaultConflictAction = ConflictAction.Skip);       // fallback when key not in Resolutions

public sealed record ImportResult(
    string SourceSystem,
    int RowsRead,
    int RowsImported,         // brand-new inserts
    int RowsUpdated,          // matched a conflict and Overwrite/Merge applied
    int RowsSkipped,
    int RowsFailed,
    IReadOnlyList<string> Errors);

/// <summary>
/// Normalised contact row — what every adapter must produce after
/// parsing its own CSV dialect. Conflict detection + write logic
/// lives in the shared base class so we don't repeat ourselves.
/// </summary>
public sealed record IncomingContact(
    string Name,
    string TaxId,
    string? Phone,
    string? Email,
    string? Address);

/// <summary>
/// Base class with the shared contact-import pipeline. Adapters only
/// need to implement <see cref="CanHandle"/> and <see cref="ParseRows"/>.
/// Conflict detection, resolution, dedup, audit tagging — all here.
/// </summary>
public abstract class ContactImportAdapterBase : ICompetitorImportAdapter
{
    protected readonly AccountingDbContext _db;
    protected ContactImportAdapterBase(AccountingDbContext db) { _db = db; }

    public abstract string SourceSystem { get; }
    public string EntityKind => "Contacts";
    public abstract bool CanHandle(string fileContent, string filename);

    /// <summary>Parse raw CSV/TSV into normalised contact rows. Also
    /// fill <paramref name="columns"/> for preview display and
    /// <paramref name="rawSample"/> with up to 50 raw rows (for the
    /// UI's preview table).</summary>
    protected abstract List<IncomingContact> ParseRows(
        string content,
        out List<string> columns,
        out List<Dictionary<string, string>> rawSample,
        out List<string> warnings);

    public async Task<ImportPreview> PreviewAsync(Guid companyId, string fileContent, CancellationToken ct)
    {
        var rows = ParseRows(fileContent, out var cols, out var rawSample, out var warnings);
        var (conflicts, newCount, exactDupCount) = await DetectConflictsAsync(companyId, rows, ct);
        var sample = rawSample.Take(50).Select(r => (IReadOnlyDictionary<string, string>)r).ToList();
        return new ImportPreview(SourceSystem, EntityKind, cols, sample,
            rows.Count, warnings, conflicts, newCount, exactDupCount);
    }

    public async Task<ImportResult> ImportAsync(Guid companyId, string fileContent,
        ImportOptions options, CancellationToken ct)
    {
        var rows = ParseRows(fileContent, out _, out _, out var warnings);
        int imported = 0, updated = 0, skipped = 0, failed = 0;
        var errors = new List<string>(warnings);
        var resolutions = options.Resolutions ?? new Dictionary<string, ConflictAction>();

        // Preload all existing contacts that might match — avoids N round-trips.
        // รอบ 193 ทีม C3: คีย์เลขภาษี + สาขา (Helpers/ContactTaxBranchKey) — เดิม ToDictionaryAsync(c => c.TaxId)
        // โยน ArgumentException ทั้งไฟล์ทันทีที่เลขเดียวกันมีผู้ติดต่อสองสาขา (ซึ่งถูกต้องตามประกาศอธิบดีฯ 199) ·
        // ไฟล์ของระบบคู่แข่งไม่มีคอลัมน์สาขา ⇒ "ไม่ระบุ" = แถวสำนักงานใหญ่ก่อน
        var existingRows = await Accounting.Helpers.ContactTaxBranchKey.LoadByTaxIdsAsync(
            _db.Contacts.Where(c => !c.IsDeleted), companyId, rows.Select(r => r.TaxId), ct);

        int rowIdx = 0;
        foreach (var row in rows)
        {
            rowIdx++;
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(row.Name)) { skipped++; continue; }

            try
            {
                var existing = Accounting.Helpers.ContactTaxBranchKey.PickContact(existingRows, row.TaxId, branchCode: null);

                if (existing == null)
                {
                    // Brand-new contact.
                    if (!options.DryRun)
                    {
                        _db.Contacts.Add(new Contact
                        {
                            CompanyId = companyId,
                            Name = row.Name,
                            TaxId = row.TaxId,
                            ContactType = ContactType.JuristicPerson,
                            IsCustomer = true,
                            IsSupplier = true,
                            Phone = row.Phone,
                            Email = row.Email,
                            Address = row.Address,
                            CreatedBy = $"Migrate:{SourceSystem}:{options.SourceTag ?? "manual"}",
                        });
                    }
                    imported++;
                    continue;
                }

                // Existing match — is it an exact dup (skip silently) or a real conflict?
                if (IsExactMatch(existing, row)) { skipped++; continue; }

                // Pick an action: explicit per-row > default > legacy SkipDuplicates flag.
                var key = row.TaxId ?? "";
                var action = resolutions.TryGetValue(key, out var picked)
                    ? picked
                    : options.DefaultConflictAction;

                if (options.SkipDuplicates && action == ConflictAction.Skip)
                {
                    skipped++; continue;
                }

                switch (action)
                {
                    case ConflictAction.Skip:
                        skipped++;
                        break;

                    case ConflictAction.Overwrite:
                        if (!options.DryRun) ApplyOverwrite(existing, row, options.SourceTag);
                        updated++;
                        break;

                    case ConflictAction.Merge:
                        if (!options.DryRun) ApplyMerge(existing, row, options.SourceTag);
                        updated++;
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Row {rowIdx} ({row.Name}): {ex.Message}");
            }
        }

        if (!options.DryRun && (imported > 0 || updated > 0))
            await _db.SaveChangesAsync(ct);

        return new ImportResult(SourceSystem, rows.Count, imported, updated, skipped, failed, errors);
    }

    // ─────────── conflict detection ───────────

    private async Task<(List<RowConflict>, int newCount, int exactDupCount)>
        DetectConflictsAsync(Guid companyId, List<IncomingContact> rows, CancellationToken ct)
    {
        // รอบ 193 ทีม C3: ตัวจับคู่กลางเดียวกับ ImportAsync (เดิม ToDictionaryAsync(c => c.TaxId) พังเมื่อเลขเดียวมีหลายสาขา)
        var existingRows = await Accounting.Helpers.ContactTaxBranchKey.LoadByTaxIdsAsync(
            _db.Contacts.Where(c => !c.IsDeleted), companyId, rows.Select(r => r.TaxId), ct);

        var conflicts = new List<RowConflict>();
        int newCount = 0, exactDupCount = 0;
        var seenKeys = new HashSet<string>();

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Name)) continue;
            var ex = Accounting.Helpers.ContactTaxBranchKey.PickContact(existingRows, row.TaxId, branchCode: null);
            if (ex == null)
            {
                newCount++;
                continue;
            }

            if (IsExactMatch(ex, row)) { exactDupCount++; continue; }

            // Avoid listing the same TaxId twice if it appears multiple times in the file.
            if (!seenKeys.Add(row.TaxId)) continue;

            var diff = new List<string>();
            void cmp(string label, string? a, string? b)
            {
                if (!StringEquals(a, b)) diff.Add(label);
            }
            cmp("Name", ex.Name, row.Name);
            cmp("Phone", ex.Phone, row.Phone);
            cmp("Email", ex.Email, row.Email);
            cmp("Address", ex.Address, row.Address);

            conflicts.Add(new RowConflict(
                Key: row.TaxId,
                ExistingLabel: ex.Name,
                IncomingLabel: row.Name,
                Existing: new Dictionary<string, string?>
                {
                    ["Name"] = ex.Name,
                    ["Phone"] = ex.Phone,
                    ["Email"] = ex.Email,
                    ["Address"] = ex.Address,
                },
                Incoming: new Dictionary<string, string?>
                {
                    ["Name"] = row.Name,
                    ["Phone"] = row.Phone,
                    ["Email"] = row.Email,
                    ["Address"] = row.Address,
                },
                DiffFields: diff));
        }

        return (conflicts, newCount, exactDupCount);
    }

    private static bool IsExactMatch(Contact ex, IncomingContact row)
        => StringEquals(ex.Name, row.Name)
        && StringEquals(ex.Phone, row.Phone)
        && StringEquals(ex.Email, row.Email)
        && StringEquals(ex.Address, row.Address);

    private static bool StringEquals(string? a, string? b)
    {
        var na = string.IsNullOrWhiteSpace(a) ? "" : a.Trim();
        var nb = string.IsNullOrWhiteSpace(b) ? "" : b.Trim();
        return string.Equals(na, nb, StringComparison.Ordinal);
    }

    private void ApplyOverwrite(Contact existing, IncomingContact row, string? sourceTag)
    {
        // Only non-empty incoming fields overwrite — protects user from
        // a file with blank columns wiping out real data.
        if (!string.IsNullOrWhiteSpace(row.Name)) existing.Name = row.Name;
        if (!string.IsNullOrWhiteSpace(row.Phone)) existing.Phone = row.Phone;
        if (!string.IsNullOrWhiteSpace(row.Email)) existing.Email = row.Email;
        if (!string.IsNullOrWhiteSpace(row.Address)) existing.Address = row.Address;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedBy = $"Migrate:{SourceSystem}:{sourceTag ?? "manual"}:overwrite";
    }

    private void ApplyMerge(Contact existing, IncomingContact row, string? sourceTag)
    {
        // Existing wins; new fills blanks only.
        if (string.IsNullOrWhiteSpace(existing.Phone) && !string.IsNullOrWhiteSpace(row.Phone))
            existing.Phone = row.Phone;
        if (string.IsNullOrWhiteSpace(existing.Email) && !string.IsNullOrWhiteSpace(row.Email))
            existing.Email = row.Email;
        if (string.IsNullOrWhiteSpace(existing.Address) && !string.IsNullOrWhiteSpace(row.Address))
            existing.Address = row.Address;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedBy = $"Migrate:{SourceSystem}:{sourceTag ?? "manual"}:merge";
    }

    // ─────────── shared CSV helpers ───────────

    protected static List<string> SplitCsvLine(string line)
    {
        // RFC4180-lite — honours quotes for embedded commas.
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

    protected static string? FirstNonEmpty(IDictionary<string, string> row, params string[] keys)
    {
        foreach (var k in keys)
            if (row.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }
}

/// <summary>
/// Express format — CSV/TSV with Thai headers: รหัส, ชื่อ, เลขประจำตัว
/// ผู้เสียภาษี, ที่อยู่. Express exports tab-separated by default;
/// adapter sniffs separator.
/// </summary>
public class ExpressContactsAdapter : ContactImportAdapterBase
{
    public ExpressContactsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "Express";

    public override bool CanHandle(string fileContent, string filename)
    {
        if (filename.Contains("express", StringComparison.OrdinalIgnoreCase)) return true;
        var head = fileContent.Length > 1000 ? fileContent[..1000] : fileContent;
        return head.Contains("รหัสลูกค้า") || head.Contains("รหัสผู้จัด")
            || head.Contains("เลขประจำตัวผู้เสียภาษี");
    }

    protected override List<IncomingContact> ParseRows(
        string content,
        out List<string> columns,
        out List<Dictionary<string, string>> rawSample,
        out List<string> warnings)
    {
        warnings = new();
        rawSample = new();
        columns = new();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new();
        var sep = lines[0].Contains('\t') ? '\t' : ',';
        columns = lines[0].TrimEnd('\r').Split(sep).Select(c => c.Trim()).ToList();
        var rows = new List<IncomingContact>(lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = raw.Split(sep);
            var d = new Dictionary<string, string>(columns.Count);
            for (int c = 0; c < columns.Count && c < cells.Length; c++)
                d[columns[c]] = cells[c].Trim();
            if (rawSample.Count < 50) rawSample.Add(d);

            var name = FirstNonEmpty(d, "ชื่อลูกค้า", "ชื่อผู้จัดจำหน่าย", "ชื่อ", "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            rows.Add(new IncomingContact(
                Name: name!,
                TaxId: FirstNonEmpty(d, "เลขประจำตัวผู้เสียภาษี", "TaxId", "TAX_ID") ?? "",
                Phone: FirstNonEmpty(d, "โทรศัพท์", "Phone"),
                Email: FirstNonEmpty(d, "อีเมล", "Email"),
                Address: FirstNonEmpty(d, "ที่อยู่", "Address")));
        }
        return rows;
    }
}

/// <summary>
/// PEAK format — CSV with English camelCase headers:
/// "contactCode", "contactName", "taxNumber", "address", "phone".
/// </summary>
public class PeakContactsAdapter : ContactImportAdapterBase
{
    public PeakContactsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "PEAK";

    public override bool CanHandle(string fileContent, string filename)
    {
        if (filename.Contains("peak", StringComparison.OrdinalIgnoreCase)) return true;
        var head = fileContent.Length > 1000 ? fileContent[..1000] : fileContent;
        return head.Contains("contactCode") || head.Contains("taxNumber");
    }

    protected override List<IncomingContact> ParseRows(
        string content,
        out List<string> columns,
        out List<Dictionary<string, string>> rawSample,
        out List<string> warnings)
    {
        warnings = new();
        rawSample = new();
        columns = new();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new();
        columns = lines[0].TrimEnd('\r').Split(',').Select(c => c.Trim('"').Trim()).ToList();
        var rows = new List<IncomingContact>(lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = SplitCsvLine(raw);
            var d = new Dictionary<string, string>(columns.Count);
            for (int c = 0; c < columns.Count && c < cells.Count; c++)
                d[columns[c]] = cells[c].Trim('"').Trim();
            if (rawSample.Count < 50) rawSample.Add(d);

            var name = FirstNonEmpty(d, "contactName", "ContactName");
            if (string.IsNullOrWhiteSpace(name)) continue;
            rows.Add(new IncomingContact(
                Name: name!,
                TaxId: FirstNonEmpty(d, "taxNumber", "TaxNumber") ?? "",
                Phone: FirstNonEmpty(d, "phone", "Phone"),
                Email: FirstNonEmpty(d, "email", "Email"),
                Address: FirstNonEmpty(d, "address", "Address")));
        }
        return rows;
    }
}

/// <summary>
/// FlowAccount format — CSV with Thai-English mixed headers:
/// "code", "name (TH)", "name (EN)", "tax id".
/// </summary>
public class FlowAccountContactsAdapter : ContactImportAdapterBase
{
    public FlowAccountContactsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "FlowAccount";

    public override bool CanHandle(string fileContent, string filename)
    {
        if (filename.Contains("flow", StringComparison.OrdinalIgnoreCase)) return true;
        var head = fileContent.Length > 1000 ? fileContent[..1000] : fileContent;
        return (head.Contains("name (TH)") && head.Contains("tax id"))
            || head.Contains("FlowAccount");
    }

    protected override List<IncomingContact> ParseRows(
        string content,
        out List<string> columns,
        out List<Dictionary<string, string>> rawSample,
        out List<string> warnings)
    {
        warnings = new();
        rawSample = new();
        columns = new();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new();
        columns = lines[0].TrimEnd('\r').Split(',').Select(c => c.Trim('"').Trim()).ToList();
        var rows = new List<IncomingContact>(lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = SplitCsvLine(raw);
            var d = new Dictionary<string, string>(columns.Count);
            for (int c = 0; c < columns.Count && c < cells.Count; c++)
                d[columns[c]] = cells[c].Trim('"').Trim();
            if (rawSample.Count < 50) rawSample.Add(d);

            var name = FirstNonEmpty(d, "name (TH)", "name (EN)", "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            rows.Add(new IncomingContact(
                Name: name!,
                TaxId: FirstNonEmpty(d, "tax id", "Tax ID", "taxId") ?? "",
                Phone: FirstNonEmpty(d, "phone", "Phone"),
                Email: FirstNonEmpty(d, "email", "Email"),
                Address: FirstNonEmpty(d, "address", "Address")));
        }
        return rows;
    }
}

/// <summary>
/// Coordinator — receives a file + filename, sniffs the right adapter,
/// returns preview or runs the import.
/// </summary>
public interface ICompetitorImportCoordinator
{
    Task<(ICompetitorImportAdapter? Adapter, ImportPreview? Preview)> PreviewAsync(
        Guid companyId, string fileContent, string filename, CancellationToken ct);

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
        Guid companyId, string fileContent, string filename, CancellationToken ct)
    {
        var adapter = _adapters.FirstOrDefault(a => a.CanHandle(fileContent, filename));
        if (adapter == null) return (null, null);
        var preview = await adapter.PreviewAsync(companyId, fileContent, ct);
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
