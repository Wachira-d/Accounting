using System.Text;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Migration;

/// <summary>
/// Chart of Account rows from competitor system. Key = AccountCode.
/// Conflict if same code but different AccountName / AccountType /
/// AccountNameEn / Description.
/// </summary>
internal sealed record IncomingAccount(
    string AccountCode,
    string AccountName,
    string? AccountNameEn,
    AccountType? AccountType,
    string? Description);

public abstract class AccountImportAdapterBase : ICompetitorImportAdapter
{
    protected readonly AccountingDbContext _db;
    protected AccountImportAdapterBase(AccountingDbContext db) { _db = db; }

    public abstract string SourceSystem { get; }
    public string EntityKind => "ChartOfAccounts";
    public abstract bool CanHandle(string fileContent, string filename);

    protected abstract List<IncomingAccount> ParseRows(
        string content,
        out List<string> columns,
        out List<Dictionary<string, string>> rawSample,
        out List<string> warnings);

    public async Task<ImportPreview> PreviewAsync(Guid companyId, string fileContent, CancellationToken ct)
    {
        var rows = ParseRows(fileContent, out var cols, out var rawSample, out var warnings);
        var codes = rows.Select(r => r.AccountCode).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
        var existing = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && codes.Contains(a.AccountCode))
            .ToDictionaryAsync(a => a.AccountCode, ct);

        var conflicts = new List<RowConflict>();
        int newCount = 0, exactDupCount = 0;
        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.AccountCode)) continue;
            if (!existing.TryGetValue(row.AccountCode, out var ex)) { newCount++; continue; }
            if (IsExactMatch(ex, row)) { exactDupCount++; continue; }
            if (!seen.Add(row.AccountCode)) continue;

            var diff = new List<string>();
            void cmp(string label, string? a, string? b) { if (!StringEquals(a, b)) diff.Add(label); }
            cmp("AccountName", ex.AccountName, row.AccountName);
            cmp("AccountNameEn", ex.AccountNameEn, row.AccountNameEn);
            cmp("AccountType", ex.AccountType.ToString(), row.AccountType?.ToString());
            cmp("Description", ex.Description, row.Description);

            conflicts.Add(new RowConflict(
                Key: row.AccountCode,
                ExistingLabel: ex.AccountName,
                IncomingLabel: row.AccountName,
                Existing: new Dictionary<string, string?>
                {
                    ["AccountName"] = ex.AccountName,
                    ["AccountNameEn"] = ex.AccountNameEn,
                    ["AccountType"] = ex.AccountType.ToString(),
                    ["Description"] = ex.Description,
                },
                Incoming: new Dictionary<string, string?>
                {
                    ["AccountName"] = row.AccountName,
                    ["AccountNameEn"] = row.AccountNameEn,
                    ["AccountType"] = row.AccountType?.ToString(),
                    ["Description"] = row.Description,
                },
                DiffFields: diff));
        }

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

        var codes = rows.Select(r => r.AccountCode).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
        var existingByCode = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && codes.Contains(a.AccountCode))
            .ToDictionaryAsync(a => a.AccountCode, ct);

        int rowIdx = 0;
        foreach (var row in rows)
        {
            rowIdx++;
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(row.AccountCode) || string.IsNullOrWhiteSpace(row.AccountName))
            { skipped++; continue; }

            try
            {
                if (!existingByCode.TryGetValue(row.AccountCode, out var ex))
                {
                    if (row.AccountType == null)
                    {
                        warnings.Add($"Row {rowIdx} ({row.AccountCode}): ไม่มี AccountType — ข้าม");
                        skipped++; continue;
                    }
                    if (!options.DryRun)
                    {
                        _db.ChartOfAccounts.Add(new ChartOfAccount
                        {
                            CompanyId = companyId,
                            AccountCode = row.AccountCode,
                            AccountName = row.AccountName,
                            AccountNameEn = row.AccountNameEn,
                            AccountType = row.AccountType.Value,
                            Description = row.Description,
                            Level = row.AccountCode.Length <= 4 ? 1 : 2,
                            IsActive = true,
                            CreatedBy = $"Migrate:{SourceSystem}:{options.SourceTag ?? "manual"}",
                        });
                    }
                    imported++;
                    continue;
                }

                if (IsExactMatch(ex, row)) { skipped++; continue; }

                var action = resolutions.TryGetValue(row.AccountCode, out var picked)
                    ? picked : options.DefaultConflictAction;
                if (options.SkipDuplicates && action == ConflictAction.Skip) { skipped++; continue; }

                switch (action)
                {
                    case ConflictAction.Skip: skipped++; break;
                    case ConflictAction.Overwrite:
                        if (!options.DryRun) ApplyOverwrite(ex, row, options.SourceTag);
                        updated++; break;
                    case ConflictAction.Merge:
                        if (!options.DryRun) ApplyMerge(ex, row, options.SourceTag);
                        updated++; break;
                }
            }
            catch (Exception e) { failed++; errors.Add($"Row {rowIdx} ({row.AccountCode}): {e.Message}"); }
        }

        if (!options.DryRun && (imported > 0 || updated > 0))
            await _db.SaveChangesAsync(ct);
        return new ImportResult(SourceSystem, rows.Count, imported, updated, skipped, failed, errors);
    }

    private static bool IsExactMatch(ChartOfAccount ex, IncomingAccount row)
        => StringEquals(ex.AccountName, row.AccountName)
        && StringEquals(ex.AccountNameEn, row.AccountNameEn)
        && (row.AccountType == null || ex.AccountType == row.AccountType.Value)
        && StringEquals(ex.Description, row.Description);

    private static bool StringEquals(string? a, string? b)
    {
        var na = string.IsNullOrWhiteSpace(a) ? "" : a.Trim();
        var nb = string.IsNullOrWhiteSpace(b) ? "" : b.Trim();
        return string.Equals(na, nb, StringComparison.Ordinal);
    }

    private void ApplyOverwrite(ChartOfAccount ex, IncomingAccount row, string? tag)
    {
        if (!string.IsNullOrWhiteSpace(row.AccountName)) ex.AccountName = row.AccountName;
        if (!string.IsNullOrWhiteSpace(row.AccountNameEn)) ex.AccountNameEn = row.AccountNameEn;
        if (row.AccountType.HasValue) ex.AccountType = row.AccountType.Value;
        if (!string.IsNullOrWhiteSpace(row.Description)) ex.Description = row.Description;
        ex.UpdatedAt = DateTime.UtcNow;
        ex.UpdatedBy = $"Migrate:{SourceSystem}:{tag ?? "manual"}:overwrite";
    }

    private void ApplyMerge(ChartOfAccount ex, IncomingAccount row, string? tag)
    {
        // Existing wins; new fills blanks only. AccountType never overwritten in merge
        // — it's structurally significant, accidental flip can corrupt reports.
        if (string.IsNullOrWhiteSpace(ex.AccountNameEn) && !string.IsNullOrWhiteSpace(row.AccountNameEn))
            ex.AccountNameEn = row.AccountNameEn;
        if (string.IsNullOrWhiteSpace(ex.Description) && !string.IsNullOrWhiteSpace(row.Description))
            ex.Description = row.Description;
        ex.UpdatedAt = DateTime.UtcNow;
        ex.UpdatedBy = $"Migrate:{SourceSystem}:{tag ?? "manual"}:merge";
    }

    // ─────────── shared helpers ───────────

    protected static List<string> SplitCsvLine(string line)
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

    protected static string? FirstNonEmpty(IDictionary<string, string> row, params string[] keys)
    {
        foreach (var k in keys)
            if (row.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    protected static AccountType? ParseType(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (Enum.TryParse<AccountType>(s, ignoreCase: true, out var t)) return t;
        // Thai → enum
        return s.Trim() switch
        {
            "สินทรัพย์" => Models.Enums.AccountType.Asset,
            "หนี้สิน" => Models.Enums.AccountType.Liability,
            "ส่วนของเจ้าของ" or "ทุน" => Models.Enums.AccountType.Equity,
            "รายได้" => Models.Enums.AccountType.Revenue,
            "ค่าใช้จ่าย" => Models.Enums.AccountType.Expense,
            _ => null,
        };
    }
}

public class ExpressAccountsAdapter : AccountImportAdapterBase
{
    public ExpressAccountsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "Express";

    public override bool CanHandle(string fileContent, string filename)
    {
        if (filename.Contains("express", StringComparison.OrdinalIgnoreCase)
            && (filename.Contains("account", StringComparison.OrdinalIgnoreCase)
                || filename.Contains("coa", StringComparison.OrdinalIgnoreCase)
                || filename.Contains("ผังบัญชี", StringComparison.OrdinalIgnoreCase))) return true;
        var head = fileContent.Length > 1500 ? fileContent[..1500] : fileContent;
        return head.Contains("รหัสบัญชี") && head.Contains("ชื่อบัญชี");
    }

    protected override List<IncomingAccount> ParseRows(string content,
        out List<string> columns, out List<Dictionary<string, string>> rawSample, out List<string> warnings)
    {
        warnings = new(); rawSample = new(); columns = new();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new();
        var sep = lines[0].Contains('\t') ? '\t' : ',';
        columns = lines[0].TrimEnd('\r').Split(sep).Select(c => c.Trim()).ToList();
        var rows = new List<IncomingAccount>();
        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = raw.Split(sep);
            var d = new Dictionary<string, string>(columns.Count);
            for (int c = 0; c < columns.Count && c < cells.Length; c++) d[columns[c]] = cells[c].Trim();
            if (rawSample.Count < 50) rawSample.Add(d);

            var code = FirstNonEmpty(d, "รหัสบัญชี", "AccountCode", "Code");
            var name = FirstNonEmpty(d, "ชื่อบัญชี", "AccountName", "Name");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name)) continue;
            rows.Add(new IncomingAccount(
                AccountCode: code!, AccountName: name!,
                AccountNameEn: FirstNonEmpty(d, "ชื่ออังกฤษ", "AccountNameEn", "NameEn"),
                AccountType: ParseType(FirstNonEmpty(d, "ประเภทบัญชี", "AccountType", "Type")),
                Description: FirstNonEmpty(d, "หมายเหตุ", "Description")));
        }
        return rows;
    }
}

public class PeakAccountsAdapter : AccountImportAdapterBase
{
    public PeakAccountsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "PEAK";

    public override bool CanHandle(string fileContent, string filename)
    {
        if (filename.Contains("peak", StringComparison.OrdinalIgnoreCase)
            && (filename.Contains("account", StringComparison.OrdinalIgnoreCase)
                || filename.Contains("coa", StringComparison.OrdinalIgnoreCase))) return true;
        var head = fileContent.Length > 1500 ? fileContent[..1500] : fileContent;
        return head.Contains("accountCode") && head.Contains("accountName");
    }

    protected override List<IncomingAccount> ParseRows(string content,
        out List<string> columns, out List<Dictionary<string, string>> rawSample, out List<string> warnings)
    {
        warnings = new(); rawSample = new(); columns = new();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new();
        columns = lines[0].TrimEnd('\r').Split(',').Select(c => c.Trim('"').Trim()).ToList();
        var rows = new List<IncomingAccount>();
        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = SplitCsvLine(raw);
            var d = new Dictionary<string, string>(columns.Count);
            for (int c = 0; c < columns.Count && c < cells.Count; c++) d[columns[c]] = cells[c].Trim('"').Trim();
            if (rawSample.Count < 50) rawSample.Add(d);

            var code = FirstNonEmpty(d, "accountCode", "AccountCode", "code");
            var name = FirstNonEmpty(d, "accountName", "AccountName", "name");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name)) continue;
            rows.Add(new IncomingAccount(
                AccountCode: code!, AccountName: name!,
                AccountNameEn: FirstNonEmpty(d, "accountNameEn", "AccountNameEn"),
                AccountType: ParseType(FirstNonEmpty(d, "accountType", "AccountType", "type")),
                Description: FirstNonEmpty(d, "description", "Description")));
        }
        return rows;
    }
}

public class FlowAccountAccountsAdapter : AccountImportAdapterBase
{
    public FlowAccountAccountsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "FlowAccount";

    public override bool CanHandle(string fileContent, string filename)
    {
        if (filename.Contains("flow", StringComparison.OrdinalIgnoreCase)
            && (filename.Contains("account", StringComparison.OrdinalIgnoreCase)
                || filename.Contains("coa", StringComparison.OrdinalIgnoreCase))) return true;
        var head = fileContent.Length > 1500 ? fileContent[..1500] : fileContent;
        return head.Contains("account code") && head.Contains("account name");
    }

    protected override List<IncomingAccount> ParseRows(string content,
        out List<string> columns, out List<Dictionary<string, string>> rawSample, out List<string> warnings)
    {
        warnings = new(); rawSample = new(); columns = new();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new();
        columns = lines[0].TrimEnd('\r').Split(',').Select(c => c.Trim('"').Trim()).ToList();
        var rows = new List<IncomingAccount>();
        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = SplitCsvLine(raw);
            var d = new Dictionary<string, string>(columns.Count);
            for (int c = 0; c < columns.Count && c < cells.Count; c++) d[columns[c]] = cells[c].Trim('"').Trim();
            if (rawSample.Count < 50) rawSample.Add(d);

            var code = FirstNonEmpty(d, "account code", "accountCode", "code");
            var name = FirstNonEmpty(d, "account name", "accountName", "name");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name)) continue;
            rows.Add(new IncomingAccount(
                AccountCode: code!, AccountName: name!,
                AccountNameEn: FirstNonEmpty(d, "account name en", "accountNameEn"),
                AccountType: ParseType(FirstNonEmpty(d, "account type", "accountType", "type")),
                Description: FirstNonEmpty(d, "description", "Description")));
        }
        return rows;
    }
}
