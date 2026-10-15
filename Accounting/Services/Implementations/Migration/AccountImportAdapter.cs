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
public sealed record IncomingAccount(
    string AccountCode,
    string AccountName,
    string? AccountNameEn,
    AccountType? AccountType,
    string? Description);

public abstract class AccountImportAdapterBase : CompetitorAdapterBase
{
    protected readonly AccountingDbContext _db;
    protected AccountImportAdapterBase(AccountingDbContext db) { _db = db; }

    public override string EntityKind => "ChartOfAccounts";

    protected abstract List<IncomingAccount> ParseRows(CompetitorTable table, out List<string> warnings);

    public override async Task<ImportPreview> PreviewAsync(Guid companyId, CompetitorTable table, CancellationToken ct)
    {
        var rows = ParseRows(table, out var warnings);
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

        return new ImportPreview(SourceSystem, EntityKind, table.Headers, Sample(table),
            rows.Count, warnings, conflicts, newCount, exactDupCount);
    }

    public override async Task<ImportResult> ImportAsync(Guid companyId, CompetitorTable table,
        ImportOptions options, CancellationToken ct)
    {
        var rows = ParseRows(table, out var warnings);
        int imported = 0, updated = 0, skipped = 0, failed = 0;
        var errors = new List<string>(warnings);
        var resolutions = options.Resolutions ?? new Dictionary<string, ConflictAction>();

        var codes = rows.Select(r => r.AccountCode).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
        var existingByCode = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && codes.Contains(a.AccountCode))
            .ToDictionaryAsync(a => a.AccountCode, ct);

        int rowIdx = 0;
        var created = new List<ChartOfAccount>();
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
                        var account = new ChartOfAccount
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
                        };
                        _db.ChartOfAccounts.Add(account);
                        created.Add(account);
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
            catch (InvalidOperationException e) { failed++; errors.Add($"แถว {rowIdx} ({row.AccountCode}): {e.Message}"); }
        }

        // ฝ่ายค้านรอบส่งออก 2026-10-15 P1-2: ผูกบัญชีแม่ของผังที่นำเข้า (เดิมไม่ผูก ⇒ หัวบัญชีดู "ไม่มีบัญชีย่อย" แล้วถูกหยิบเป็นผังตั้งต้น) ·
        // ทั้งชุดในครั้งเดียว ⇒ ลำดับแถวในไฟล์ไม่มีผล · ผูกไม่ได้ = คำเตือน (ผังยังนำเข้าได้)
        if (!options.DryRun && created.Count > 0)
            foreach (var w in await Accounting.Services.Implementations.ChartParentLinker.LinkImportedAsync(_db, companyId, created, ct,
                         actor: $"Migrate:{SourceSystem}:{options.SourceTag ?? "manual"}"))
                errors.Add("ผูกบัญชีแม่ไม่ได้ — " + w);
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

    protected override IReadOnlyCollection<string> KeyHeaders { get; } = new[] { "รหัสบัญชี", "ชื่อบัญชี" };
    protected override IReadOnlyCollection<string> OtherHeaders { get; } = new[] { "ชื่ออังกฤษ", "ประเภทบัญชี", "หมายเหตุ", "AccountCode", "AccountName", "Code", "Name", "Type" };

    protected override List<IncomingAccount> ParseRows(CompetitorTable table, out List<string> warnings)
    {
        warnings = new();
        var rows = new List<IncomingAccount>();
        foreach (var d in table.Rows)
        {
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

    protected override IReadOnlyCollection<string> KeyHeaders { get; } = new[] { "accountCode", "accountName" };
    protected override IReadOnlyCollection<string> OtherHeaders { get; } = new[] { "accountNameEn", "accountType", "description", "code", "name", "type" };

    protected override List<IncomingAccount> ParseRows(CompetitorTable table, out List<string> warnings)
    {
        warnings = new();
        var rows = new List<IncomingAccount>();
        foreach (var d in table.Rows)
        {
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

    protected override IReadOnlyCollection<string> KeyHeaders { get; } = new[] { "account code", "account name" };
    protected override IReadOnlyCollection<string> OtherHeaders { get; } = new[] { "account name en", "account type", "description", "code", "name", "type" };

    protected override List<IncomingAccount> ParseRows(CompetitorTable table, out List<string> warnings)
    {
        warnings = new();
        var rows = new List<IncomingAccount>();
        foreach (var d in table.Rows)
        {
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
