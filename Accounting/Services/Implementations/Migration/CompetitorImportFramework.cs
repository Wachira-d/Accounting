using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Implementations.Migration;

/// <summary>
/// Competitor-system import framework — for SMEs migrating from
/// Express / PEAK / FlowAccount. Each adapter handles ONE competitor
/// format and exposes the same canonical pipeline:
///
///   1. PreviewAsync(companyId, table) — return first 50 rows AND detect conflicts (rows whose key matches an
///      existing record but with different values). Admin sees the diff before committing.
///   2. ImportAsync(companyId, table, options) — actually write rows. For each conflict the caller picks an action
///      (Skip / Overwrite / Merge); rows without an explicit decision fall back to options.DefaultConflictAction.
///
/// <para>รอบ 205 ทีม D (IMPORT_ETL_REVIEW §1 แถว 4): ไฟล์อ่านด้วย <see cref="ImportTabularReader"/> ตัวเดียว (TIS-620/Windows-874 · UTF-16 ·
/// xlsx · ค่าในเครื่องหมายคำพูด) แทน <c>StreamReader(UTF8)</c> + <c>Split(',')</c> ของแต่ละ adapter · <b>ตัดสินชนิดไฟล์ด้วยคะแนนหัวตาราง</b>
/// (<see cref="Score"/>) ไม่ใช่ชื่อไฟล์ — เดิม adapter ผู้ติดต่อของ Express ลงทะเบียนก่อนและจับคำว่า "express" ในชื่อไฟล์ ⇒
/// <c>express_products.csv</c> ถูกนำเข้าเป็นผู้ติดต่อ</para>
/// </summary>
public interface ICompetitorImportAdapter
{
    /// <summary>System name (e.g. "Express", "PEAK", "FlowAccount"). Used as the discriminator + audit tag.</summary>
    string SourceSystem { get; }

    /// <summary>"Contacts" | "Products" | "ChartOfAccounts" — what this adapter pass produces.</summary>
    string EntityKind { get; }

    /// <summary>คะแนนความเหมือนของหัวตาราง (0 = ไม่ใช่ไฟล์ของ adapter นี้) — <see cref="CompetitorHeaderScore"/></summary>
    int Score(IReadOnlyList<string> headers);

    Task<ImportPreview> PreviewAsync(Guid companyId, CompetitorTable table, CancellationToken ct);

    Task<ImportResult> ImportAsync(Guid companyId, CompetitorTable table, ImportOptions options, CancellationToken ct);
}

/// <summary>
/// ตารางของไฟล์จากโปรแกรมอื่น — แถวแรกที่ไม่ว่าง = หัวตาราง · แถวว่างถูกตัด · หัวซ้ำได้ชื่อ "หัว (2)" · ค่าตัดช่องว่างแล้ว
/// </summary>
public sealed record CompetitorTable(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows,
    string? Encoding,
    ImportFileKind Kind)
{
    /// <summary>เปิดไฟล์ด้วยตัวอ่านกลาง — ไฟล์ที่ไม่ใช่ตาราง/อ่านอักขระไม่ได้ ⇒ <see cref="ImportFormatException"/> (ข้อความไทยพร้อมทางไปต่อ)</summary>
    public static CompetitorTable Open(string fileName, byte[] bytes)
    {
        var opened = ImportTabularReader.Open(fileName, bytes);
        IReadOnlyList<string>? headers = null;
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var raw in opened.Rows)
        {
            if (raw.All(c => string.IsNullOrWhiteSpace(c))) continue;
            if (headers == null)
            {
                headers = UniqueHeaders(raw.Select(h => h.Trim().TrimStart('﻿')).ToList());
                continue;
            }
            var d = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headers.Count; c++)
                d[headers[c]] = c < raw.Count ? raw[c].Trim() : "";
            rows.Add(d);
        }
        return new CompetitorTable(headers ?? Array.Empty<string>(), rows, opened.Encoding, opened.Kind);
    }

    private static IReadOnlyList<string> UniqueHeaders(List<string> headers)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<string>(headers.Count);
        foreach (var h0 in headers)
        {
            var h = h0.Length == 0 ? $"คอลัมน์ {outList.Count + 1}" : h0;
            if (seen.TryGetValue(h, out var n)) { seen[h] = n + 1; h = $"{h} ({n + 1})"; }
            else seen[h] = 1;
            outList.Add(h);
        }
        return outList;
    }
}

/// <summary>
/// คะแนนหัวตาราง — ตัวตัดสินเดียวของการเลือก adapter (pure) · ต้องเจอ "หัวหลัก" อย่างน้อยหนึ่งตัว (ไม่งั้น 0) ·
/// คะแนน = 3 × หัวหลักที่เจอ + หัวรองที่เจอ · เทียบทั้งคำแบบไม่สนตัวพิมพ์ (ไม่ตัดช่องว่าง — "productCode" ของ PEAK ≠ "product code" ของ FlowAccount)
/// </summary>
public static class CompetitorHeaderScore
{
    public static int Score(IReadOnlyList<string> headers, IReadOnlyCollection<string> keyHeaders, IReadOnlyCollection<string> otherHeaders)
    {
        var set = new HashSet<string>(headers.Select(h => h.Trim()), StringComparer.OrdinalIgnoreCase);
        var keys = keyHeaders.Count(set.Contains);
        if (keys == 0) return 0;
        return keys * 3 + otherHeaders.Count(set.Contains);
    }
}

/// <summary>How to resolve a row whose key matches an existing record but with different field values.</summary>
public enum ConflictAction
{
    /// <summary>Keep the existing DB row untouched, ignore the import row.</summary>
    Skip = 0,
    /// <summary>Replace existing row's fields with the import row's values (only non-empty incoming fields overwrite).</summary>
    Overwrite = 1,
    /// <summary>Existing values win; import only fills in blanks (sensible default — protects manual edits while enriching sparse rows).</summary>
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

/// <param name="EntityLabel">ชื่อชนิดข้อมูลภาษาไทย (เซิร์ฟเวอร์ตัดสิน — หน้าเว็บไม่แปลเอง)</param>
/// <param name="Encoding">encoding ที่อ่านได้ ("windows-874" = TIS-620)</param>
/// <param name="DetectionReason">ทำไมเลือก adapter นี้ (คะแนนหัวตาราง)</param>
/// <param name="HubEntityType">ชนิดข้อมูลของศูนย์นำเข้า — ลิงก์ "เปิดในศูนย์นำเข้าข้อมูล"</param>
/// <param name="KeyLabel">ชื่อคีย์จับคู่ของรายการขัดแย้ง (เลขผู้เสียภาษี · รหัสสินค้า · รหัสบัญชี)</param>
public sealed record ImportPreview(
    string SourceSystem,
    string EntityKind,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string>> SampleRows,
    int TotalRows,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RowConflict> Conflicts,
    int NewRowCount,                                  // count of rows without a DB match
    int DuplicateExactCount,                          // rows whose key matches AND every field is identical (auto-skip, no UI prompt)
    string? EntityLabel = null,
    string? Encoding = null,
    string? DetectionReason = null,
    string? HubEntityType = null,
    string? KeyLabel = null);

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

/// <summary>ส่วนร่วมของ adapter ทุกตัว — คะแนนหัวตาราง + อ่านค่าตามชื่อหัว</summary>
public abstract class CompetitorAdapterBase : ICompetitorImportAdapter
{
    public abstract string SourceSystem { get; }
    public abstract string EntityKind { get; }

    /// <summary>หัวที่บอกชนิดไฟล์ได้ชัด (ต้องเจออย่างน้อยหนึ่ง)</summary>
    protected abstract IReadOnlyCollection<string> KeyHeaders { get; }
    /// <summary>หัวอื่นที่ adapter อ่าน (เพิ่มคะแนน)</summary>
    protected abstract IReadOnlyCollection<string> OtherHeaders { get; }

    public int Score(IReadOnlyList<string> headers) => CompetitorHeaderScore.Score(headers, KeyHeaders, OtherHeaders);

    public abstract Task<ImportPreview> PreviewAsync(Guid companyId, CompetitorTable table, CancellationToken ct);
    public abstract Task<ImportResult> ImportAsync(Guid companyId, CompetitorTable table, ImportOptions options, CancellationToken ct);

    protected static string? FirstNonEmpty(IReadOnlyDictionary<string, string> row, params string[] keys)
    {
        foreach (var k in keys)
            if (row.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        return null;
    }

    /// <summary>ยอดเงิน/ราคา — ตัวแปลงกลาง (คั่นหลักพัน · เลขไทย · ฿) · อ่านไม่ได้ = null (ไม่เดา)</summary>
    protected static decimal? ParseDec(string? s)
    {
        var v = ImportValueNormalizer.Amount(s);
        return v.Ok ? v.Value : null;
    }

    /// <summary>อัตรา VAT ("7" · "7%" · "ยกเว้น") — ตัวแปลงกลาง</summary>
    protected static decimal? ParseRate(string? s)
    {
        var v = ImportValueNormalizer.VatRate(s);
        return v.Ok ? v.Value : null;
    }

    protected static IReadOnlyList<IReadOnlyDictionary<string, string>> Sample(CompetitorTable t) => t.Rows.Take(50).ToList();
}

/// <summary>
/// Normalised contact row — what every adapter must produce after parsing its own dialect. Conflict detection + write
/// logic lives in the shared base class so we don't repeat ourselves.
/// </summary>
/// <param name="DeclaredType">ชนิดที่ไฟล์ประกาศ (ถ้ามีคอลัมน์) — ป้อน <see cref="ContactTypeResolver"/></param>
/// <param name="IsCustomer">null = ไฟล์ไม่บอก</param>
public sealed record IncomingContact(
    string Name,
    string TaxId,
    string? Phone,
    string? Email,
    string? Address,
    string? BranchCode = null,
    string? DeclaredType = null,
    bool? IsCustomer = null,
    bool? IsSupplier = null);

/// <summary>
/// Base class with the shared contact-import pipeline. Adapters only need to declare their headers and implement
/// <see cref="ParseRows"/>. Conflict detection, resolution, dedup, audit tagging — all here.
///
/// <para>รอบ 205 ทีม D: ชนิดผู้ติดต่อ + สาขาผ่าน <see cref="ContactTypeResolver.ResolveWithBranch"/> (เดิมฮาร์ดโค้ด JuristicPerson ทุกแถว ⇒
/// บุคคลธรรมดาถูกยื่นเป็น ภ.ง.ด.53) · เลขผู้เสียภาษีผ่าน <see cref="ThaiTaxIdValidator"/> (ผิด checksum = ปฏิเสธแถวพร้อมทางแก้ ·
/// เส้นเดียวกับการนำเข้าทั่วไป) · ลูกค้า/ผู้ขายจากคอลัมน์หรือหัวตาราง (เดิมตั้ง IsCustomer=IsSupplier=true ทุกแถว) · ไม่รู้ = ทั้งสอง + คำเตือน</para>
/// </summary>
public abstract class ContactImportAdapterBase : CompetitorAdapterBase
{
    protected readonly AccountingDbContext _db;
    protected ContactImportAdapterBase(AccountingDbContext db) { _db = db; }

    public override string EntityKind => "Contacts";

    /// <summary>Parse table rows into normalised contacts.</summary>
    protected abstract List<IncomingContact> ParseRows(CompetitorTable table, out List<string> warnings);

    /// <summary>บทบาทจากข้อความในไฟล์ ("ลูกค้า" · "ผู้ขาย" · "ทั้งสอง") — null = ไม่รู้จัก</summary>
    public static (bool Customer, bool Supplier)? ParseRole(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().ToLowerInvariant();
        var c = s.Contains("ลูกค้า") || s.Contains("customer") || s.Contains("client");
        var v = s.Contains("ผู้ขาย") || s.Contains("ผู้จำหน่าย") || s.Contains("ผู้จัดจำหน่าย") || s.Contains("supplier")
                || s.Contains("vendor") || s.Contains("เจ้าหนี้");
        if (s.Contains("ทั้งสอง") || s.Contains("both")) return (true, true);
        return c || v ? (c, v) : null;
    }

    /// <summary>ลูกค้า/ผู้ขายจากหัวตาราง (Express แยกไฟล์ลูกค้ากับผู้จำหน่าย) — null = หัวไม่บอก</summary>
    protected static (bool Customer, bool Supplier)? RoleFromHeaders(IReadOnlyList<string> headers)
    {
        var c = headers.Any(h => h.Contains("ลูกค้า"));
        var s = headers.Any(h => h.Contains("ผู้จำหน่าย") || h.Contains("ผู้จัดจำหน่าย") || h.Contains("ผู้ขาย"));
        return c || s ? (c, s) : null;
    }

    private sealed record Prepared(IncomingContact Row, string? TaxId, ContactIdentityVerdict Identity, bool IsCustomer, bool IsSupplier,
        string? Error);

    /// <summary>ตรวจ/แปลงแถวด้วยตัวตัดสินกลาง — เลขภาษีผิด checksum = Error (แถวไม่ถูกนำเข้า) · บทบาทไม่รู้ = ทั้งสอง + คำเตือนรวม</summary>
    private static List<Prepared> Prepare(List<IncomingContact> rows, List<string> warnings)
    {
        var unknownRole = 0;
        var outList = new List<Prepared>(rows.Count);
        foreach (var r in rows)
        {
            string? error = null;
            string? tax = null;
            if (!string.IsNullOrWhiteSpace(r.TaxId))
            {
                var check = ThaiTaxIdValidator.Check(r.TaxId);
                if (check.IsValid) tax = ThaiTaxIdValidator.Normalize(r.TaxId);
                else error = $"{r.Name}: เลขประจำตัวผู้เสียภาษี \"{r.TaxId}\" ไม่ถูกต้อง ({check.Reason}) — แก้ในไฟล์หรือเว้นว่าง แล้วนำเข้าแถวนี้อีกครั้ง";
            }
            var identity = ContactTypeResolver.ResolveWithBranch(r.DeclaredType, tax, r.BranchCode, r.Name);
            var known = r.IsCustomer.HasValue || r.IsSupplier.HasValue;
            if (!known) unknownRole++;
            var isC = known ? r.IsCustomer ?? false : true;
            var isS = known ? r.IsSupplier ?? false : true;
            if (!isC && !isS) { isC = true; isS = true; }
            outList.Add(new Prepared(r, tax, identity, isC, isS, error));
        }
        if (unknownRole > 0)
            warnings.Add($"ไฟล์ไม่บอกว่าเป็นลูกค้าหรือผู้ขาย {unknownRole} แถว — ตั้งเป็นทั้งลูกค้าและผู้ขาย (แก้ได้ที่หน้าผู้ติดต่อ)");
        var unknownType = outList.Count(p => p.Error == null && !p.Identity.IsResolved);
        if (unknownType > 0)
            warnings.Add($"ระบุชนิดผู้ติดต่อไม่ได้ {unknownType} แถว (ไม่มีเลขผู้เสียภาษี/คำบ่งชี้) — บันทึกเป็น \"ยังไม่ระบุ\" ให้เลือกก่อนออกเอกสารภาษี");
        return outList;
    }

    public override async Task<ImportPreview> PreviewAsync(Guid companyId, CompetitorTable table, CancellationToken ct)
    {
        var rows = ParseRows(table, out var warnings);
        var prepared = Prepare(rows, warnings);
        warnings.AddRange(prepared.Where(p => p.Error != null).Select(p => p.Error!));
        var (conflicts, newCount, exactDupCount) = await DetectConflictsAsync(companyId, prepared.Where(p => p.Error == null).ToList(), ct);
        return new ImportPreview(SourceSystem, EntityKind, table.Headers, Sample(table),
            rows.Count, warnings, conflicts, newCount, exactDupCount);
    }

    public override async Task<ImportResult> ImportAsync(Guid companyId, CompetitorTable table, ImportOptions options, CancellationToken ct)
    {
        var rows = ParseRows(table, out var warnings);
        var prepared = Prepare(rows, warnings);
        int imported = 0, updated = 0, skipped = 0, failed = 0;
        var errors = new List<string>(warnings);
        var resolutions = options.Resolutions ?? new Dictionary<string, ConflictAction>();

        // Preload all existing contacts that might match — avoids N round-trips.
        // รอบ 193 ทีม C3: คีย์เลขภาษี + สาขา (Helpers/ContactTaxBranchKey)
        var existingRows = await ContactTaxBranchKey.LoadByTaxIdsAsync(
            _db.Contacts.Where(c => !c.IsDeleted), companyId, prepared.Select(p => p.TaxId ?? ""), ct);

        int rowIdx = 0;
        foreach (var p in prepared)
        {
            rowIdx++;
            if (ct.IsCancellationRequested) break;
            var row = p.Row;
            if (string.IsNullOrWhiteSpace(row.Name)) { skipped++; continue; }
            if (p.Error != null) { failed++; errors.Add($"แถว {rowIdx}: {p.Error}"); continue; }

            try
            {
                var existing = p.TaxId == null ? null : ContactTaxBranchKey.PickContact(existingRows, p.TaxId, p.Identity.BranchCode);
                if (existing == null)
                {
                    if (!options.DryRun)
                    {
                        _db.Contacts.Add(new Contact
                        {
                            CompanyId = companyId,
                            Name = row.Name,
                            TaxId = p.TaxId,
                            BranchCode = p.Identity.BranchCode,
                            ContactType = p.Identity.Type,
                            IsCustomer = p.IsCustomer,
                            IsSupplier = p.IsSupplier,
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

                var key = p.TaxId ?? "";
                var action = resolutions.TryGetValue(key, out var picked) ? picked : options.DefaultConflictAction;
                if (options.SkipDuplicates && action == ConflictAction.Skip) { skipped++; continue; }

                switch (action)
                {
                    case ConflictAction.Skip: skipped++; break;
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
            catch (InvalidOperationException ex)
            {
                failed++;
                errors.Add($"แถว {rowIdx} ({row.Name}): {ex.Message}");
            }
        }

        if (!options.DryRun && (imported > 0 || updated > 0))
            await _db.SaveChangesAsync(ct);

        return new ImportResult(SourceSystem, rows.Count, imported, updated, skipped, failed, errors);
    }

    // ─────────── conflict detection ───────────

    private async Task<(List<RowConflict>, int newCount, int exactDupCount)>
        DetectConflictsAsync(Guid companyId, List<Prepared> rows, CancellationToken ct)
    {
        var existingRows = await ContactTaxBranchKey.LoadByTaxIdsAsync(
            _db.Contacts.Where(c => !c.IsDeleted), companyId, rows.Select(r => r.TaxId ?? ""), ct);

        var conflicts = new List<RowConflict>();
        int newCount = 0, exactDupCount = 0;
        var seenKeys = new HashSet<string>();

        foreach (var p in rows)
        {
            var row = p.Row;
            if (string.IsNullOrWhiteSpace(row.Name)) continue;
            var ex = p.TaxId == null ? null : ContactTaxBranchKey.PickContact(existingRows, p.TaxId, p.Identity.BranchCode);
            if (ex == null) { newCount++; continue; }
            if (IsExactMatch(ex, row)) { exactDupCount++; continue; }
            if (!seenKeys.Add(p.TaxId!)) continue;

            var diff = new List<string>();
            void cmp(string label, string? a, string? b) { if (!StringEquals(a, b)) diff.Add(label); }
            cmp("Name", ex.Name, row.Name);
            cmp("Phone", ex.Phone, row.Phone);
            cmp("Email", ex.Email, row.Email);
            cmp("Address", ex.Address, row.Address);

            conflicts.Add(new RowConflict(
                Key: p.TaxId!,
                ExistingLabel: ex.Name,
                IncomingLabel: row.Name,
                Existing: new Dictionary<string, string?> { ["Name"] = ex.Name, ["Phone"] = ex.Phone, ["Email"] = ex.Email, ["Address"] = ex.Address },
                Incoming: new Dictionary<string, string?> { ["Name"] = row.Name, ["Phone"] = row.Phone, ["Email"] = row.Email, ["Address"] = row.Address },
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
        // Only non-empty incoming fields overwrite — protects user from a file with blank columns wiping out real data.
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
        if (string.IsNullOrWhiteSpace(existing.Phone) && !string.IsNullOrWhiteSpace(row.Phone)) existing.Phone = row.Phone;
        if (string.IsNullOrWhiteSpace(existing.Email) && !string.IsNullOrWhiteSpace(row.Email)) existing.Email = row.Email;
        if (string.IsNullOrWhiteSpace(existing.Address) && !string.IsNullOrWhiteSpace(row.Address)) existing.Address = row.Address;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedBy = $"Migrate:{SourceSystem}:{sourceTag ?? "manual"}:merge";
    }

    /// <summary>บทบาทของแถว: คอลัมน์บทบาท/ธง → หัวตาราง → ไม่รู้ (null, null)</summary>
    protected static (bool? Customer, bool? Supplier) RoleOf(IReadOnlyDictionary<string, string> d, (bool Customer, bool Supplier)? fromHeaders,
        params string[] roleColumns)
    {
        var parsed = ParseRole(FirstNonEmpty(d, roleColumns));
        if (parsed is { } p) return (p.Customer, p.Supplier);
        return fromHeaders is { } h ? (h.Customer, h.Supplier) : (null, null);
    }
}

/// <summary>
/// Express format — CSV/TSV with Thai headers: รหัสลูกค้า/รหัสผู้จำหน่าย, ชื่อ, เลขประจำตัวผู้เสียภาษี, ที่อยู่ ·
/// มักเป็น TIS-620 (ถอดที่ <see cref="ImportTabularReader"/>)
/// </summary>
public class ExpressContactsAdapter : ContactImportAdapterBase
{
    public ExpressContactsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "Express";
    protected override IReadOnlyCollection<string> KeyHeaders { get; } = new[]
        { "รหัสลูกค้า", "ชื่อลูกค้า", "รหัสผู้จำหน่าย", "ชื่อผู้จำหน่าย", "รหัสผู้จัดจำหน่าย", "ชื่อผู้จัดจำหน่าย", "เลขประจำตัวผู้เสียภาษี" };
    protected override IReadOnlyCollection<string> OtherHeaders { get; } = new[]
        { "ชื่อ", "ที่อยู่", "โทรศัพท์", "อีเมล", "สาขา", "ประเภท", "ประเภทผู้ติดต่อ" };

    protected override List<IncomingContact> ParseRows(CompetitorTable table, out List<string> warnings)
    {
        warnings = new();
        var role = RoleFromHeaders(table.Headers);
        var rows = new List<IncomingContact>(table.Rows.Count);
        foreach (var d in table.Rows)
        {
            var name = FirstNonEmpty(d, "ชื่อลูกค้า", "ชื่อผู้จำหน่าย", "ชื่อผู้จัดจำหน่าย", "ชื่อ", "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var (c, s) = RoleOf(d, role, "ประเภท", "บทบาท");
            rows.Add(new IncomingContact(
                Name: name,
                TaxId: FirstNonEmpty(d, "เลขประจำตัวผู้เสียภาษี", "TaxId", "TAX_ID") ?? "",
                Phone: FirstNonEmpty(d, "โทรศัพท์", "Phone"),
                Email: FirstNonEmpty(d, "อีเมล", "Email"),
                Address: FirstNonEmpty(d, "ที่อยู่", "Address"),
                BranchCode: FirstNonEmpty(d, "สาขา", "รหัสสาขา"),
                DeclaredType: FirstNonEmpty(d, "ประเภทผู้ติดต่อ"),
                IsCustomer: c, IsSupplier: s));
        }
        return rows;
    }
}

/// <summary>PEAK format — CSV with English camelCase headers: "contactCode", "contactName", "taxNumber", "address", "phone".</summary>
public class PeakContactsAdapter : ContactImportAdapterBase
{
    public PeakContactsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "PEAK";
    protected override IReadOnlyCollection<string> KeyHeaders { get; } = new[] { "contactCode", "contactName", "taxNumber" };
    protected override IReadOnlyCollection<string> OtherHeaders { get; } = new[]
        { "address", "phone", "email", "contactType", "branchCode", "branch", "contactGroup", "isCustomer", "isSupplier" };

    protected override List<IncomingContact> ParseRows(CompetitorTable table, out List<string> warnings)
    {
        warnings = new();
        var rows = new List<IncomingContact>(table.Rows.Count);
        foreach (var d in table.Rows)
        {
            var name = FirstNonEmpty(d, "contactName", "ContactName");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var c = ImportValueNormalizer.Flag(FirstNonEmpty(d, "isCustomer")).Value;
            var s = ImportValueNormalizer.Flag(FirstNonEmpty(d, "isSupplier")).Value;
            if (c == null && s == null) (c, s) = RoleOf(d, null, "contactGroup", "role");
            rows.Add(new IncomingContact(
                Name: name,
                TaxId: FirstNonEmpty(d, "taxNumber", "TaxNumber") ?? "",
                Phone: FirstNonEmpty(d, "phone", "Phone"),
                Email: FirstNonEmpty(d, "email", "Email"),
                Address: FirstNonEmpty(d, "address", "Address"),
                BranchCode: FirstNonEmpty(d, "branchCode", "branch"),
                DeclaredType: FirstNonEmpty(d, "contactType"),
                IsCustomer: c, IsSupplier: s));
        }
        return rows;
    }
}

/// <summary>FlowAccount format — CSV with Thai-English mixed headers: "code", "name (TH)", "name (EN)", "tax id".</summary>
public class FlowAccountContactsAdapter : ContactImportAdapterBase
{
    public FlowAccountContactsAdapter(AccountingDbContext db) : base(db) { }
    public override string SourceSystem => "FlowAccount";
    protected override IReadOnlyCollection<string> KeyHeaders { get; } = new[] { "name (TH)", "name (EN)", "tax id" };
    protected override IReadOnlyCollection<string> OtherHeaders { get; } = new[] { "code", "phone", "email", "address", "branch", "contact type", "type" };

    protected override List<IncomingContact> ParseRows(CompetitorTable table, out List<string> warnings)
    {
        warnings = new();
        var rows = new List<IncomingContact>(table.Rows.Count);
        foreach (var d in table.Rows)
        {
            var name = FirstNonEmpty(d, "name (TH)", "name (EN)", "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var (c, s) = RoleOf(d, null, "contact type", "type");
            rows.Add(new IncomingContact(
                Name: name,
                TaxId: FirstNonEmpty(d, "tax id", "Tax ID", "taxId") ?? "",
                Phone: FirstNonEmpty(d, "phone", "Phone"),
                Email: FirstNonEmpty(d, "email", "Email"),
                Address: FirstNonEmpty(d, "address", "Address"),
                BranchCode: FirstNonEmpty(d, "branch"),
                IsCustomer: c, IsSupplier: s));
        }
        return rows;
    }
}

/// <summary>ผลตัดสินว่าไฟล์เป็นของ adapter ไหน — <see cref="Adapter"/> null = ไม่รู้จัก/กำกวม (ดู <see cref="Reason"/>)</summary>
public sealed record CompetitorDetection(ICompetitorImportAdapter? Adapter, int Score, string Reason);

/// <summary>Coordinator — receives a parsed table, picks the right adapter by header score, returns preview or runs the import.</summary>
public interface ICompetitorImportCoordinator
{
    /// <summary>ไฟล์นี้เป็นของ adapter ตัวไหน (ยังไม่อ่าน/เขียนฐาน) — ผู้เรียกใช้ <c>EntityKind</c> ตัดสินสิทธิ์
    /// <b>ก่อน</b> preview (ซึ่งเปิดเผยแถวเดิมที่ขัดแย้ง) หรือ import (ซึ่งเขียนผังบัญชี/สินค้า/ผู้ติดต่อ) — รอบ 193 S2</summary>
    CompetitorDetection Detect(CompetitorTable table);

    Task<(ICompetitorImportAdapter? Adapter, ImportPreview? Preview)> PreviewAsync(Guid companyId, CompetitorTable table, CancellationToken ct);

    Task<ImportResult?> ImportAsync(Guid companyId, CompetitorTable table, ImportOptions options, CancellationToken ct);
}

public class CompetitorImportCoordinator : ICompetitorImportCoordinator
{
    private readonly IEnumerable<ICompetitorImportAdapter> _adapters;
    private readonly ILogger<CompetitorImportCoordinator> _logger;

    public CompetitorImportCoordinator(IEnumerable<ICompetitorImportAdapter> adapters, ILogger<CompetitorImportCoordinator> logger)
    { _adapters = adapters; _logger = logger; }

    /// <summary>ชื่อชนิดข้อมูลภาษาไทย + ชนิดของศูนย์นำเข้า (ตัวเดียว — หน้าเว็บไม่ถือตาราง)</summary>
    public static (string Label, string HubEntity, string KeyLabel) EntityInfo(string entityKind) => entityKind switch
    {
        "Contacts" => ("ผู้ติดต่อ", "contacts", "เลขผู้เสียภาษี"),
        "Products" => ("สินค้า/บริการ", "products", "รหัสสินค้า"),
        "ChartOfAccounts" => ("ผังบัญชี", "chart-of-accounts", "รหัสบัญชี"),
        _ => (entityKind, entityKind.ToLowerInvariant(), "คีย์"),
    };

    public CompetitorDetection Detect(CompetitorTable table)
    {
        var ranked = _adapters.Select(a => (Adapter: a, Score: a.Score(table.Headers)))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();
        if (ranked.Count == 0)
            return new CompetitorDetection(null, 0,
                "หัวตารางไม่ตรงกับไฟล์ส่งออกของ Express / PEAK / FlowAccount ที่รู้จัก — ใช้ศูนย์นำเข้าข้อมูล (จับคู่คอลัมน์เองได้)");
        var best = ranked[0];
        if (ranked.Count > 1 && ranked[1].Score == best.Score && ranked[1].Adapter.EntityKind != best.Adapter.EntityKind)
            return new CompetitorDetection(null, best.Score,
                $"หัวตารางกำกวมระหว่าง{EntityInfo(best.Adapter.EntityKind).Label}กับ{EntityInfo(ranked[1].Adapter.EntityKind).Label} — ใช้ศูนย์นำเข้าข้อมูลแล้วเลือกชนิดเอง");
        return new CompetitorDetection(best.Adapter, best.Score,
            $"หัวตารางตรงกับไฟล์{EntityInfo(best.Adapter.EntityKind).Label}ของ {best.Adapter.SourceSystem} (คะแนน {best.Score})");
    }

    public async Task<(ICompetitorImportAdapter? Adapter, ImportPreview? Preview)> PreviewAsync(Guid companyId, CompetitorTable table, CancellationToken ct)
    {
        var det = Detect(table);
        if (det.Adapter == null) return (null, null);
        var preview = await det.Adapter.PreviewAsync(companyId, table, ct);
        var info = EntityInfo(det.Adapter.EntityKind);
        return (det.Adapter, preview with
        {
            EntityLabel = info.Label,
            Encoding = table.Encoding,
            DetectionReason = det.Reason,
            HubEntityType = info.HubEntity,
            KeyLabel = info.KeyLabel,
        });
    }

    public async Task<ImportResult?> ImportAsync(Guid companyId, CompetitorTable table, ImportOptions options, CancellationToken ct)
    {
        var det = Detect(table);
        if (det.Adapter == null)
        {
            _logger.LogWarning("No adapter found: {Reason}", det.Reason);
            return null;
        }
        return await det.Adapter.ImportAsync(companyId, table, options, ct);
    }
}
