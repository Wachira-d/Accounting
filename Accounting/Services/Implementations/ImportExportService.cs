using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Ai.Prompts;
using Accounting.Services.Import;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ImportExportService : IImportExportService
{
    private readonly AccountingDbContext _db;
    private readonly IErrorLogService _errorLogService;
    private readonly IImportAiAugmenter? _ai;

    // _ai is optional so the service still works in tests / environments
    // where AI isn't registered. Production wires it up in Program.cs.
    /// <summary>ผู้เขียนสต็อกตัวเดียวของระบบ (POS_MULTI_BRANCH_ANALYSIS เฟส 0)</summary>
    private readonly IStockLedger? _stock;

    /// <summary>รอบ 205 ทีม B: รับชำระจากไฟล์ผ่าน domain service ตัวเดียว (JE · ธนาคาร · ภ.พ.36 · ใบเสร็จ) — เดิมแก้ PaidAmount ตรง (AR ลดแต่ GL ไม่ขยับ)</summary>
    private readonly IDocumentService? _documents;
    /// <summary>รอบ 205 ทีม B: ทะเบียนสินทรัพย์จากไฟล์ผ่าน <c>FixedAssetService.ImportAsync</c> (ตัวตัดสิน FixedAssetImportMethod — ที่ดินไม่คิดค่าเสื่อม)</summary>
    private readonly IFixedAssetService? _fixedAssets;
    /// <summary>รอบ 205 ทีม B: ประกอบคำขอสร้างเอกสารจากแถว (ทีม D) → <c>DocumentService.CreateDocumentAsync</c> (ร่าง + เลข DRAFT · คำตัดสินข้อ 151)</summary>
    private readonly IDocumentImportBuilder? _documentBuilder;
    /// <summary>รอบ 205 ทีม B: รายการธนาคารจากไฟล์ผ่านตัวเขียน statement ตัวเดียว (ทีม C) — เดิมเดินยอด <c>CurrentBalance +=</c> เอง</summary>
    private readonly IBankService? _bank;
    /// <summary>รอบ 205 ทีม B: จับคู่คอลัมน์ของ Smart Import ผ่านทางเข้าเดียวของทีม A (แม่แบบ/คำพ้อง/เรียนรู้/AI + FeedbackId) — null = ชั้น local ล้วน</summary>
    private readonly IImportColumnMatchService? _columnMatch;

    public ImportExportService(AccountingDbContext db, IErrorLogService errorLogService,
        IImportAiAugmenter? ai = null, IStockLedger? stock = null,
        IDocumentService? documents = null, IFixedAssetService? fixedAssets = null,
        IDocumentImportBuilder? documentBuilder = null, IBankService? bank = null, IImportColumnMatchService? columnMatch = null)
    {
        _db = db;
        _errorLogService = errorLogService;
        _ai = ai;
        _stock = stock;
        _documents = documents;
        _fixedAssets = fixedAssets;
        _documentBuilder = documentBuilder;
        _bank = bank;
        _columnMatch = columnMatch;
    }

    /// <summary>ชนิดข้อมูลที่ writer เดิมเขียนได้ (คีย์ <see cref="ImportExportPermissionScope.NormalizeEntityType"/>) — ชุดเดียวกับ switch ใน
    /// <see cref="WriteUnitAsync"/> (รอบ 205 ทีม B: เดิม switch ซ้ำสองชุดใน ImportAsync + ConfirmAndImportAsync)</summary>
    internal static readonly IReadOnlySet<string> WritableEntityKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "contacts", "products", "chartofaccounts", "banktransactions", "journalentries", "stockopening", "openingar", "openingap",
        "stockadjustments", "fixedassets", "payments", "projects", "employees", "budgets", "documents",
    };

    public async Task<ImportResult> ImportAsync(Guid companyId, ImportRequest request, string performedBy)
    {
        var validation = await ValidateImportAsync(companyId, request);
        if (validation.ErrorCount > 0 && validation.SuccessCount == 0)
            return validation;

        var rows = request.Data.Select((r, i) => (RowNo: i + 1, Row: r)).ToList();
        var run = await RunLegacyRowsAsync(companyId, request.EntityType, rows, request.Resolutions,
            request.DefaultConflictAction, performedBy, ImportDateOrder.Auto, decimalComma: false);

        return new ImportResult(request.EntityType, request.Data.Count, run.Success,
            run.Errors.Count, run.Skipped, run.Errors, DateTime.UtcNow);
    }

    // ══════════════════════ ตัวเขียนแถวตัวเดียว (รอบ 205 ทีม B · IMPORT_ETL_REVIEW §2.4) ══════════════════════

    /// <summary>ผลเขียนของ writer เดิมหนึ่งหน่วย — <see cref="EntityId"/> ใช้ทำ idempotent/ย้อน · <see cref="BeforeJson"/> = ค่าเดิมก่อนทับ (ย้อนแบบ RestoreBefore)</summary>
    private sealed record RowWrite(string EntityType, Guid? EntityId, bool Skipped = false, string? BeforeJson = null);

    private sealed record LegacyRun(int Success, int Skipped, List<ImportError> Errors);

    /// <summary>
    /// ทางเดินเดียวของเส้นนำเข้าเดิม (<see cref="ImportAsync"/> · <see cref="ConfirmAndImportAsync"/>) — <b>บันทึกทีละหน่วย</b> (แถว · เอกสารหนึ่งใบ):
    /// แถวที่ล้มถูกทิ้งจาก change tracker ทั้งแถว (เดิม SaveChanges ครั้งเดียวท้ายไฟล์ ⇒ แถวที่ล้มกลางทางบันทึกครึ่ง ๆ ไปกับแถวอื่น) ·
    /// ค่าวันที่/ตัวเลขอ่านผ่าน <see cref="ImportValueNormalizer"/> (ไม่ขึ้นกับ culture ของเครื่อง)
    /// </summary>
    /// <param name="dateOrder">ลำดับวัน/เดือนของไฟล์ (Smart Import: <c>DateFormat</c> ที่ผู้ใช้เลือก) — แปลงช่องวันที่เป็นรูปมาตรฐานก่อนเข้า writer</param>
    /// <param name="decimalComma">ไฟล์ใช้จุลภาคเป็นทศนิยม (<c>DecimalSeparator = ","</c>) — แปลงช่องตัวเลขก่อนเข้า writer</param>
    private async Task<LegacyRun> RunLegacyRowsAsync(Guid companyId, string entityType,
        IReadOnlyList<(int RowNo, Dictionary<string, string> Row)> rows, Dictionary<string, string>? resolutions, string? defaultAction,
        string performedBy, ImportDateOrder dateOrder, bool decimalComma)
    {
        var key = ImportExportPermissionScope.NormalizeEntityType(entityType);
        var errors = new List<ImportError>();
        if (!WritableEntityKeys.Contains(key))
        {
            foreach (var (rowNo, _) in rows)
                errors.Add(new ImportError(rowNo, "EntityType", entityType, $"ไม่รองรับการนำเข้า {entityType}"));
            return new LegacyRun(0, 0, errors);
        }

        var fields = GetTemplateFields(entityType);
        var fallbackAction = defaultAction ?? DefaultConflictAction(key);
        int success = 0, skipped = 0;
        foreach (var unit in LegacyUnits(key, rows))
        {
            var targetRows = unit
                .Select(u => new ImportTargetRow(Guid.NewGuid(), u.RowNo, ToValues(CanonicalizeLegacyRow(u.Row, fields, dateOrder, decimalComma)),
                    ImportRowAction.Create))
                .ToList();
            var action = ResolveAction(resolutions, LegacyConflictKey(key, unit[0].Row), fallbackAction);
            try
            {
                var results = await WriteUnitAsync(companyId, key, targetRows, new ImportRowWriteOptions(action, SaveChanges: true),
                    performedBy, CancellationToken.None);
                foreach (var (res, tr) in results.Zip(targetRows))
                {
                    if (!res.Success) errors.Add(new ImportError(tr.RowNo, "", "", res.Error ?? "นำเข้าไม่สำเร็จ"));
                    else if (res.Skipped) skipped++;
                    else success++;
                }
            }
            catch (Exception ex)
            {
                // ข้อผิดพลาดของระบบ (ไม่ใช่กฎธุรกิจ) — แถวนี้ล้มดัง (ผลลัพธ์ + log) แถวอื่นเดินต่อ · ทิ้งค่าที่ค้างใน tracker ของแถวนี้
                DiscardPendingChanges();
                foreach (var tr in targetRows) errors.Add(new ImportError(tr.RowNo, "", "", ex.Message));
                await _errorLogService.LogErrorAsync(ex, $"ImportExport.ImportRow/{targetRows[0].RowNo}");
            }
        }
        return new LegacyRun(success, skipped, errors);
    }

    /// <summary>หน่วยการเขียนของเส้นเดิม — เอกสาร = แถวที่เลขเอกสารเดียวกัน (หนึ่งใบหลายบรรทัด) · เลขว่าง = แถวละหน่วย · ชนิดอื่น = แถวละหน่วย</summary>
    private static IEnumerable<List<(int RowNo, Dictionary<string, string> Row)>> LegacyUnits(string key,
        IReadOnlyList<(int RowNo, Dictionary<string, string> Row)> rows)
    {
        if (key != "documents")
        {
            foreach (var r in rows) yield return new List<(int RowNo, Dictionary<string, string> Row)> { r };
            yield break;
        }
        foreach (var g in rows.GroupBy(r =>
                     string.IsNullOrWhiteSpace(r.Row.GetValueOrDefault("DocumentNumber"))
                         ? "#row-" + r.RowNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
                         : r.Row.GetValueOrDefault("DocumentNumber")!.Trim(),
                     StringComparer.Ordinal))
            yield return g.ToList();
    }

    /// <summary>คีย์ของ "การตัดสินใจต่อแถว" (Resolutions จากหน้าพรีวิวข้อมูลซ้ำ) — สูตรเดียวกับ PreviewConflictsAsync</summary>
    private static string LegacyConflictKey(string key, Dictionary<string, string> row) => key switch
    {
        "contacts" => ThaiTaxIdValidator.Normalize(row.GetValueOrDefault("TaxId")) ?? row.GetValueOrDefault("Email") ?? "",
        "products" => row.GetValueOrDefault("Code") ?? "",
        "chartofaccounts" => row.GetValueOrDefault("AccountCode") ?? "",
        _ => "",
    };

    /// <summary>ค่าเริ่มเมื่อชนของเดิม — ผู้ติดต่อ = เติมช่องว่าง (Merge) · สินค้า/ผังบัญชี = ข้าม (ทับราคา/ประเภทบัญชีต้องให้ผู้ใช้เลือกเอง)</summary>
    private static string DefaultConflictAction(string key) => key == "contacts" ? "Merge" : "Skip";

    private static IReadOnlyDictionary<string, string?> ToValues(Dictionary<string, string> row)
        => new Dictionary<string, string?>(row.ToDictionary(kv => kv.Key, kv => (string?)kv.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>ค่าใน <see cref="ImportTargetRow.Values"/> → dictionary ของ writer เดิม (คีย์ไม่สนตัวพิมพ์ · null = ไม่มีช่อง)</summary>
    private static Dictionary<string, string> ToLegacyRow(IReadOnlyDictionary<string, string?> values)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in values)
            if (kv.Value != null) d[kv.Key] = kv.Value;
        return d;
    }

    /// <summary>
    /// ทำให้ <c>DateFormat</c>/<c>DecimalSeparator</c> ของ Smart Import มีผลจริง (เดิมรับแล้วไม่ใช้ = silent no-op) — ช่องวันที่ที่อ่านตามลำดับที่ผู้ใช้เลือกได้
    /// ถูกแปลงเป็น yyyy-MM-dd · ไฟล์ทศนิยมจุลภาค ("1.234,50") ถูกแปลงเป็น invariant · ค่าที่อ่านไม่ได้คงตามไฟล์ให้ writer ล้มดังพร้อมเหตุผล
    /// </summary>
    private static Dictionary<string, string> CanonicalizeLegacyRow(Dictionary<string, string> row, List<ImportField>? fields,
        ImportDateOrder dateOrder, bool decimalComma)
    {
        if (fields == null || (dateOrder == ImportDateOrder.Auto && !decimalComma)) return row;
        var copy = new Dictionary<string, string>(row, StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields)
        {
            if (!copy.TryGetValue(f.FieldName, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
            if (f.DataType == "date" && dateOrder != ImportDateOrder.Auto)
            {
                var d = ImportValueNormalizer.Date(raw, dateOrder);
                if (d.Ok && d.Value is DateTime dv) copy[f.FieldName] = ImportValueNormalizer.CanonicalDate(dv);
            }
            else if (f.DataType == "decimal" && decimalComma)
            {
                // 1.234,50 → 1234.50 · 1234,5 → 1234.5 (จุด = คั่นหลักพัน · จุลภาค = ทศนิยม)
                var swapped = raw.Trim().Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
                var n = ImportValueNormalizer.Number(swapped);
                if (n.Ok && n.Value is decimal nv) copy[f.FieldName] = ImportValueNormalizer.CanonicalAmount(nv);
            }
        }
        return copy;
    }

    /// <summary>
    /// <see cref="IImportRowWriter"/> ของศูนย์นำเข้า (ผ่าน <c>ImportRowWriter</c>) — เขียนหนึ่งแถวด้วย writer เดิม · ค่าใน <paramref name="row"/> เป็นรูปมาตรฐานแล้ว ·
    /// กฎธุรกิจล้ม ⇒ <c>Success = false</c> + ข้อความไทย (ไม่ throw) · <see cref="ImportRowWriteOptions.SaveChanges"/> = บันทึกแถวนี้เลย (ล้ม = ทิ้งค่าค้างของแถว)
    /// </summary>
    public async Task<ImportRowCommitResult> WriteRowAsync(Guid companyId, string entityType, ImportTargetRow row, ImportRowWriteOptions options,
        string performedBy, CancellationToken ct = default)
    {
        var key = ImportExportPermissionScope.NormalizeEntityType(entityType);
        if (!WritableEntityKeys.Contains(key))
            return new ImportRowCommitResult(row.RowId, false, Error: $"ไม่รองรับการนำเข้า {entityType}");
        var results = await WriteUnitAsync(companyId, key, new[] { row }, options, performedBy, ct);
        return results[0];
    }

    /// <summary>switch เดียวของ writer เดิม — ทุกทางเข้า (ไฟล์เดิม · Smart Import · ศูนย์นำเข้า) ผ่านที่นี่</summary>
    private async Task<IReadOnlyList<ImportRowCommitResult>> WriteUnitAsync(Guid companyId, string key, IReadOnlyList<ImportTargetRow> rows,
        ImportRowWriteOptions options, string performedBy, CancellationToken ct)
    {
        if (rows.All(r => r.Action == ImportRowAction.Skip))
            return rows.Select(r => new ImportRowCommitResult(r.RowId, true, Skipped: true)).ToList();

        var first = rows[0];
        var action = ResolveAction(null, "", options.ConflictAction ?? first.Action switch
        {
            ImportRowAction.Update => "Overwrite",
            ImportRowAction.Merge => "Merge",
            _ => DefaultConflictAction(key),
        });
        try
        {
            RowWrite w;
            if (key == "documents")
                w = await WriteDocumentGroupAsync(companyId, rows, performedBy, ct);
            else
            {
                var row = ToLegacyRow(first.Values);
                // คู่ที่ผู้ใช้เลือกมีผลเฉพาะ "อัปเดต/เติม" — "สร้างใหม่" ห้ามไปแตะแถวที่ระบบแค่แนะนำ (ชื่อคล้าย ≠ รายเดียวกัน)
                var matched = first.Action is ImportRowAction.Update or ImportRowAction.Merge ? first.MatchedEntityId : null;
                w = key switch
                {
                    "contacts" => await ImportContactAsync(companyId, row, action, matched),
                    "products" => await ImportProductAsync(companyId, row, action, matched),
                    "chartofaccounts" => await ImportAccountAsync(companyId, row, action, matched),
                    "banktransactions" => await ImportBankTransactionAsync(companyId, row, performedBy),
                    "journalentries" => await ImportJournalEntryAsync(companyId, row, performedBy),
                    "stockopening" => await ImportStockOpeningAsync(companyId, row, performedBy),
                    "openingar" => await ImportOpeningSubledgerAsync(companyId, row, performedBy, isReceivable: true),
                    "openingap" => await ImportOpeningSubledgerAsync(companyId, row, performedBy, isReceivable: false),
                    "stockadjustments" => await ImportStockAdjustmentAsync(companyId, row, performedBy),
                    "fixedassets" => await ImportFixedAssetAsync(companyId, row, performedBy),
                    "payments" => await ImportPaymentAsync(companyId, row, performedBy),
                    "projects" => await ImportProjectAsync(companyId, row, performedBy),
                    "employees" => await ImportEmployeeAsync(companyId, row, performedBy),
                    "budgets" => await ImportBudgetAsync(companyId, row, performedBy),
                    _ => throw new BusinessRuleException($"ไม่รองรับการนำเข้า {key}"),
                };
            }
            if (options.SaveChanges && !w.Skipped) await _db.SaveChangesAsync(ct);
            return rows.Select(r => w.Skipped
                    ? new ImportRowCommitResult(r.RowId, true, w.EntityType, w.EntityId, Skipped: true)
                    : new ImportRowCommitResult(r.RowId, true, w.EntityType, w.EntityId, BeforeJson: w.BeforeJson))
                .ToList();
        }
        catch (Exception ex) when (IsRowError(ex))
        {
            if (options.SaveChanges) DiscardPendingChanges();
            var msg = RowErrorMessage(ex);
            return rows.Select(r => new ImportRowCommitResult(r.RowId, false, Error: msg)).ToList();
        }
    }

    /// <summary>ข้อผิดพลาดที่เป็น "แถวนี้ลงไม่ได้" (กฎธุรกิจของ writer/domain service · ข้อมูลไม่ผ่านข้อจำกัดของฐาน) — ไม่ใช่ระบบล่ม</summary>
    private static bool IsRowError(Exception ex) => ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException
        or ArgumentException or FormatException or DbUpdateException or NotImplementedException;

    private static string RowErrorMessage(Exception ex) => ex is DbUpdateException db
        ? "บันทึกแถวนี้ไม่ได้ — ข้อมูลไม่ผ่านข้อจำกัดของฐานข้อมูล (เช่น ยาวเกิน/ซ้ำกับที่มีอยู่): " + (db.InnerException?.Message ?? db.Message)
        : ex.Message;

    /// <summary>ทิ้งค่าที่ค้างใน change tracker ของหน่วยที่ล้ม — หน่วยก่อนหน้าบันทึกไปแล้ว (บันทึกทีละหน่วย) ⇒ ไม่มีอะไรดีหาย</summary>
    private void DiscardPendingChanges() => _db.ChangeTracker.Clear();

    // ── ตัวอ่านค่า (รอบ 205 ทีม B · P0): แทน DateTime/decimal/bool.TryParse ที่ขึ้นกับ culture — ค่าว่าง = ไม่มีค่า · ค่าที่อ่านไม่ได้ = ล้มดังพร้อมเหตุผล
    //    (เดิม "อ่านไม่ได้ ⇒ วันนี้/0/false" เงียบ ๆ) · รูปมาตรฐานจากศูนย์นำเข้า (yyyy-MM-dd · invariant) อ่านได้ตรงตัว ──

    private static string? Val(Dictionary<string, string> row, string key)
        => row.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static DateTime? OptDate(Dictionary<string, string> row, string key, string label)
    {
        var raw = Val(row, key);
        if (raw == null) return null;
        var v = ImportValueNormalizer.Date(raw);
        if (!v.Ok || v.Value is not DateTime d)
            throw new BusinessRuleException($"{label}: {v.Message ?? $"อ่านวันที่ \"{raw}\" ไม่ได้"}");
        return d;
    }

    private static DateTime ReqDate(Dictionary<string, string> row, string key, string label)
        => OptDate(row, key, label) ?? throw new BusinessRuleException($"ต้องระบุ{label}");

    private static decimal? OptAmount(Dictionary<string, string> row, string key, string label)
    {
        var raw = Val(row, key);
        if (raw == null) return null;
        var v = ImportValueNormalizer.Amount(raw);
        if (!v.Ok) throw new BusinessRuleException($"{label}: {v.Message}");
        return v.Value;
    }

    private static decimal? OptNumber(Dictionary<string, string> row, string key, string label)
    {
        var raw = Val(row, key);
        if (raw == null) return null;
        var v = ImportValueNormalizer.Number(raw);
        if (!v.Ok) throw new BusinessRuleException($"{label}: {v.Message}");
        return v.Value;
    }

    private static int? OptInt(Dictionary<string, string> row, string key, string label)
    {
        var n = OptNumber(row, key, label);
        if (n is not decimal x) return null;
        if (x != decimal.Truncate(x) || x > int.MaxValue || x < int.MinValue)
            throw new BusinessRuleException($"{label}: \"{x}\" ต้องเป็นจำนวนเต็ม");
        return (int)x;
    }

    /// <summary>อัตรา VAT — "7%"/"7"/"0.07" ⇒ 7 · "ยกเว้น" ⇒ 0 (สินค้ายังไม่มีช่องยกเว้นแยก — ศูนย์นำเข้าเตือนให้ตรวจ) · ว่าง = null</summary>
    private static decimal? OptVatRate(Dictionary<string, string> row, string key, string label)
    {
        var raw = Val(row, key);
        if (raw == null) return null;
        var v = ImportValueNormalizer.VatRate(raw);
        if (!v.Ok) throw new BusinessRuleException($"{label}: {v.Message}");
        return v.Value ?? (v.IssueCode == "vat-exempt" ? 0m : null);
    }

    /// <summary>ใช่/ไม่ใช่ — "ใช่"/"Y"/"1"/"✓" = true (เดิม bool.TryParse ⇒ "ใช่"/"Y" = false เงียบ ๆ) · ว่าง = false</summary>
    private static bool OptFlag(Dictionary<string, string> row, string key, string label)
    {
        var raw = Val(row, key);
        if (raw == null) return false;
        var v = ImportValueNormalizer.Flag(raw);
        if (!v.Ok) throw new BusinessRuleException($"{label}: {v.Message}");
        return v.Value ?? false;
    }

    /// <summary>ค่าเดิมก่อนทับ (ย้อนแบบ RestoreBefore) — serialize ผ่านตัวเดียวของศูนย์นำเข้า</summary>
    private static string Before(object snapshot) => ImportJson.Serialize(snapshot);

    /// <summary>Dry-run dedup check: scan request.Data, return rows whose
    /// natural key already exists in DB with different field values. The
    /// client uses this to render a conflict-resolution UI before commit.
    /// Supported: contacts (key=TaxId), products (key=Code),
    /// chartofaccounts (key=AccountCode). Other entity types return an
    /// empty conflict list.</summary>
    public async Task<ConflictPreviewResponse> PreviewConflictsAsync(Guid companyId, ImportRequest request)
    {
        var warnings = new List<string>();
        var conflicts = new List<ConflictRow>();
        int newCount = 0, exactDup = 0;
        var seen = new HashSet<string>();

        switch (request.EntityType.ToLower())
        {
            case "contacts":
            {
                // รอบ 193 ทีม C3: คีย์เลขภาษี + สาขา — ตัวตัดสินเดียวกับ ImportContactAsync (Helpers/ContactTaxBranchKey)
                // เดิม ToDictionaryAsync(c => c.TaxId) โยน ArgumentException ทั้งหน้าพรีวิวทันทีที่เลขเดียวกันมีสองสาขา
                // และตัวชี้ "ซ้ำ/ขัด" ต้องตรงกับแถวที่การนำเข้าจริงจะไปแตะ (คอลัมน์ BranchCode ว่าง = "ไม่ระบุ")
                var existingRows = await Accounting.Helpers.ContactTaxBranchKey.LoadByTaxIdsAsync(
                    _db.Contacts.Where(c => !c.IsDeleted), companyId,
                    request.Data.Select(r => r.GetValueOrDefault("TaxId")));
                foreach (var row in request.Data)
                {
                    var taxId = row.GetValueOrDefault("TaxId");
                    if (string.IsNullOrWhiteSpace(taxId)) { newCount++; continue; }
                    var ex = Accounting.Helpers.ContactTaxBranchKey.PickContact(existingRows, taxId, row.GetValueOrDefault("BranchCode"));
                    if (ex == null)
                    { newCount++; continue; }
                    var name = row.GetValueOrDefault("Name");
                    var phone = row.GetValueOrDefault("Phone");
                    var email = row.GetValueOrDefault("Email");
                    var address = row.GetValueOrDefault("Address");
                    if (EqStr(ex.Name, name) && EqStr(ex.Phone, phone) && EqStr(ex.Email, email) && EqStr(ex.Address, address))
                    { exactDup++; continue; }
                    if (!seen.Add(taxId)) continue;
                    var diff = new List<string>();
                    if (!EqStr(ex.Name, name)) diff.Add("Name");
                    if (!EqStr(ex.Phone, phone)) diff.Add("Phone");
                    if (!EqStr(ex.Email, email)) diff.Add("Email");
                    if (!EqStr(ex.Address, address)) diff.Add("Address");
                    conflicts.Add(new ConflictRow(taxId, ex.Name, name ?? "",
                        new() { ["Name"] = ex.Name, ["Phone"] = ex.Phone, ["Email"] = ex.Email, ["Address"] = ex.Address },
                        new() { ["Name"] = name, ["Phone"] = phone, ["Email"] = email, ["Address"] = address },
                        diff));
                }
                break;
            }
            case "products":
            {
                var codes = request.Data.Select(r => r.GetValueOrDefault("Code"))
                    .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
                var existing = await _db.Products
                    .Where(p => p.CompanyId == companyId && !p.IsDeleted && codes.Contains(p.Code))
                    .ToDictionaryAsync(p => p.Code);
                foreach (var row in request.Data)
                {
                    var code = row.GetValueOrDefault("Code");
                    if (string.IsNullOrWhiteSpace(code) || !existing.TryGetValue(code, out var ex))
                    { newCount++; continue; }
                    var name = row.GetValueOrDefault("Name");
                    var unit = row.GetValueOrDefault("Unit");
                    // รอบ 205 ทีม B (P0): ตัวแปลงเดียวกับตอนเขียนจริง — ไม่ขึ้นกับ culture ("1,234.50" · เลขไทย)
                    var sp = ImportValueNormalizer.Amount(row.GetValueOrDefault("SellingPrice")).Value ?? 0m;
                    var cp = ImportValueNormalizer.Amount(row.GetValueOrDefault("CostPrice")).Value ?? 0m;
                    var vr = ImportValueNormalizer.VatRate(row.GetValueOrDefault("VatRate")).Value ?? 0m;
                    var category = row.GetValueOrDefault("Category");
                    bool isDup = EqStr(ex.Name, name) && EqStr(ex.Unit, unit)
                        && ex.SellingPrice == sp && ex.CostPrice == cp && ex.VatRate == vr
                        && EqStr(ex.Category, category);
                    if (isDup) { exactDup++; continue; }
                    if (!seen.Add(code)) continue;
                    var diff = new List<string>();
                    if (!EqStr(ex.Name, name)) diff.Add("Name");
                    if (!EqStr(ex.Unit, unit)) diff.Add("Unit");
                    if (ex.SellingPrice != sp) diff.Add("SellingPrice");
                    if (ex.CostPrice != cp) diff.Add("CostPrice");
                    if (ex.VatRate != vr) diff.Add("VatRate");
                    if (!EqStr(ex.Category, category)) diff.Add("Category");
                    conflicts.Add(new ConflictRow(code, ex.Name, name ?? "",
                        new() { ["Name"] = ex.Name, ["Unit"] = ex.Unit,
                                ["SellingPrice"] = ex.SellingPrice.ToString("N2"),
                                ["CostPrice"] = ex.CostPrice.ToString("N2"),
                                ["VatRate"] = ex.VatRate.ToString("N2"),
                                ["Category"] = ex.Category },
                        new() { ["Name"] = name, ["Unit"] = unit,
                                ["SellingPrice"] = sp.ToString("N2"),
                                ["CostPrice"] = cp.ToString("N2"),
                                ["VatRate"] = vr.ToString("N2"),
                                ["Category"] = category },
                        diff));
                }
                break;
            }
            case "chartofaccounts":
            case "chart-of-accounts":
            {
                var codes = request.Data.Select(r => r.GetValueOrDefault("AccountCode"))
                    .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
                var existing = await _db.ChartOfAccounts
                    .Where(a => a.CompanyId == companyId && !a.IsDeleted && codes.Contains(a.AccountCode))
                    .ToDictionaryAsync(a => a.AccountCode);
                foreach (var row in request.Data)
                {
                    var code = row.GetValueOrDefault("AccountCode");
                    if (string.IsNullOrWhiteSpace(code) || !existing.TryGetValue(code, out var ex))
                    { newCount++; continue; }
                    var name = row.GetValueOrDefault("AccountName");
                    var nameEn = row.GetValueOrDefault("AccountNameEn");
                    var typeStr = row.GetValueOrDefault("AccountType");
                    var desc = row.GetValueOrDefault("Description");
                    bool isDup = EqStr(ex.AccountName, name) && EqStr(ex.AccountNameEn, nameEn)
                        && EqStr(ex.AccountType.ToString(), typeStr) && EqStr(ex.Description, desc);
                    if (isDup) { exactDup++; continue; }
                    if (!seen.Add(code)) continue;
                    var diff = new List<string>();
                    if (!EqStr(ex.AccountName, name)) diff.Add("AccountName");
                    if (!EqStr(ex.AccountNameEn, nameEn)) diff.Add("AccountNameEn");
                    if (!EqStr(ex.AccountType.ToString(), typeStr)) diff.Add("AccountType");
                    if (!EqStr(ex.Description, desc)) diff.Add("Description");
                    conflicts.Add(new ConflictRow(code, ex.AccountName, name ?? "",
                        new() { ["AccountName"] = ex.AccountName, ["AccountNameEn"] = ex.AccountNameEn,
                                ["AccountType"] = ex.AccountType.ToString(), ["Description"] = ex.Description },
                        new() { ["AccountName"] = name, ["AccountNameEn"] = nameEn,
                                ["AccountType"] = typeStr, ["Description"] = desc },
                        diff));
                }
                break;
            }
            default:
                warnings.Add($"Conflict preview ยังไม่รองรับ entity type \"{request.EntityType}\" — รองรับเฉพาะ contacts / products / chartofaccounts");
                newCount = request.Data.Count;
                break;
        }

        return new ConflictPreviewResponse(request.EntityType, request.Data.Count, newCount, exactDup, conflicts, warnings);
    }

    private static bool EqStr(string? a, string? b)
    {
        var na = string.IsNullOrWhiteSpace(a) ? "" : a.Trim();
        var nb = string.IsNullOrWhiteSpace(b) ? "" : b.Trim();
        return string.Equals(na, nb, StringComparison.Ordinal);
    }

    public Task<ImportTemplateResponse> GetImportTemplateAsync(string entityType)
    {
        var template = entityType.ToLower() switch
        {
            "contacts" => new ImportTemplateResponse("contacts", new List<ImportField>
            {
                new("Name", "ชื่อ", "string", true, "ชื่อลูกค้า/ผู้ขาย", null),
                new("TaxId", "เลขผู้เสียภาษี", "string", false, "เลขประจำตัวผู้เสียภาษี 13 หลัก (ตรวจ checksum — เลขผิดจะถูกปฏิเสธทั้งแถว)", null),
                // ช่องนี้ตัดสิน ภ.ง.ด.3 vs ภ.ง.ด.53 และ scheme ของ e-Tax (NIDN/TXID)
                // — เดิมไม่มีในแม่แบบเลย ⇒ ผู้ใช้ไม่มีทางระบุได้ แล้วทุกแถวตกค่า default
                new("ContactType", "ประเภทผู้ติดต่อ", "enum", false,
                    "Individual (บุคคลธรรมดา → ภ.ง.ด.3) · JuristicPerson (นิติบุคคล → ภ.ง.ด.53) · GovernmentAgency — เว้นว่างได้ถ้ามีเลขผู้เสียภาษีที่ถูกต้อง ระบบจะอ่านจากเลขให้",
                    new List<string> { "Individual", "JuristicPerson", "GovernmentAgency" }),
                new("BranchCode", "รหัสสาขา", "string", false,
                    "5 หลักตามประกาศอธิบดีฯ 199 — 00000 = สำนักงานใหญ่ (เก็บเฉพาะนิติบุคคล/ราชการ)", null),
                new("IsCustomer", "เป็นลูกค้า", "bool", false, "true/false", new List<string> { "true", "false" }),
                new("IsSupplier", "เป็นผู้ขาย", "bool", false, "true/false", new List<string> { "true", "false" }),
                new("Email", "อีเมล", "string", false, null, null),
                new("Phone", "โทรศัพท์", "string", false, null, null),
                new("Address", "ที่อยู่", "string", false, null, null),
                new("ContactPerson", "ผู้ติดต่อ", "string", false, null, null),
            }, new List<Dictionary<string, string>>
            {
                new() { ["Name"] = "บริษัท ตัวอย่าง จำกัด", ["TaxId"] = "0123456789012", ["IsCustomer"] = "true", ["Email"] = "info@example.com" }
            }),

            "products" => new ImportTemplateResponse("products", new List<ImportField>
            {
                new("Code", "รหัสสินค้า", "string", true, null, null),
                new("Name", "ชื่อสินค้า", "string", true, null, null),
                new("ProductType", "ประเภท", "enum", false, null, new List<string> { "Product", "Service", "NonStock" }),
                new("Unit", "หน่วยนับ", "string", true, null, null),
                new("SellingPrice", "ราคาขาย", "decimal", false, null, null),
                new("CostPrice", "ราคาทุน", "decimal", false, null, null),
                new("VatRate", "อัตราภาษี", "decimal", false, null, null),
                new("Category", "หมวดหมู่", "string", false, null, null),
            }, new List<Dictionary<string, string>>
            {
                new() { ["Code"] = "P001", ["Name"] = "สินค้าตัวอย่าง", ["Unit"] = "ชิ้น", ["SellingPrice"] = "100.00", ["CostPrice"] = "60.00" }
            }),

            "chartofaccounts" or "chart-of-accounts" => new ImportTemplateResponse("chart-of-accounts", new List<ImportField>
            {
                new("AccountCode", "รหัสบัญชี", "string", true, null, null),
                new("AccountName", "ชื่อบัญชี", "string", true, null, null),
                new("AccountNameEn", "ชื่อบัญชี (อังกฤษ)", "string", false, null, null),
                new("AccountType", "ประเภทบัญชี", "enum", true, null, new List<string> { "Asset", "Liability", "Equity", "Revenue", "Expense" }),
                new("Description", "คำอธิบาย", "string", false, null, null),
            }, new List<Dictionary<string, string>>
            {
                new() { ["AccountCode"] = "1110", ["AccountName"] = "เงินสด", ["AccountNameEn"] = "Cash", ["AccountType"] = "Asset" }
            }),

            "journalentries" or "journal-entries" => new ImportTemplateResponse("journal-entries", new List<ImportField>
            {
                new("Date", "วันที่", "date", true, "yyyy-MM-dd", null),
                new("Description", "คำอธิบาย", "string", true, null, null),
                new("AccountCode", "รหัสบัญชี", "string", true, null, null),
                new("DebitAmount", "เดบิต", "decimal", false, null, null),
                new("CreditAmount", "เครดิต", "decimal", false, null, null),
                new("Reference", "อ้างอิง", "string", false, null, null),
                new("ContactName", "ผู้ติดต่อ", "string", false, null, null),
            }, new List<Dictionary<string, string>>
            {
                new() { ["Date"] = "2026-01-15", ["Description"] = "ค่าเช่าสำนักงาน", ["AccountCode"] = "5210", ["DebitAmount"] = "15000.00", ["CreditAmount"] = "" },
                new() { ["Date"] = "2026-01-15", ["Description"] = "ค่าเช่าสำนักงาน", ["AccountCode"] = "1120", ["DebitAmount"] = "", ["CreditAmount"] = "15000.00" }
            }),

            "banktransactions" => new ImportTemplateResponse("banktransactions", new List<ImportField>
            {
                new("BankAccountId", "รหัสบัญชีธนาคาร", "guid", true, null, null),
                new("TransactionDate", "วันที่", "date", true, "yyyy-MM-dd", null),
                new("TransactionType", "ประเภท", "enum", true, null, new List<string> { "Deposit", "Withdrawal", "Transfer", "Fee", "Interest" }),
                new("Amount", "จำนวนเงิน", "decimal", true, null, null),
                new("Description", "คำอธิบาย", "string", false, null, null),
                new("Reference", "อ้างอิง", "string", false, null, null),
            }, new List<Dictionary<string, string>>
            {
                new() { ["TransactionDate"] = "2026-01-15", ["TransactionType"] = "Deposit", ["Amount"] = "50000.00", ["Description"] = "รับชำระเงิน" }
            }),

            "stock-opening" => new ImportTemplateResponse("stock-opening", GetTemplateFields("stock-opening")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["ProductCode"] = "P001", ["Quantity"] = "100", ["UnitCost"] = "60.00", ["OpeningDate"] = "2026-01-01", ["Notes"] = "ยกมาจากระบบเดิม" }
                }),

            "opening-ar" => new ImportTemplateResponse("opening-ar", GetTemplateFields("opening-ar")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["ContactName"] = "บริษัท ลูกค้า จำกัด", ["ContactTaxId"] = "0123456789012", ["InvoiceNumber"] = "INV-2025-001", ["InvoiceDate"] = "2025-11-20", ["DueDate"] = "2025-12-20", ["Amount"] = "53500.00", ["Description"] = "ลูกหนี้ยกมา" }
                }),

            "opening-ap" => new ImportTemplateResponse("opening-ap", GetTemplateFields("opening-ap")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["ContactName"] = "บริษัท ผู้ขาย จำกัด", ["ContactTaxId"] = "0987654321098", ["InvoiceNumber"] = "PINV-2025-044", ["InvoiceDate"] = "2025-11-25", ["DueDate"] = "2025-12-25", ["Amount"] = "21400.00", ["Description"] = "เจ้าหนี้ยกมา" }
                }),

            "stock-adjustments" => new ImportTemplateResponse("stock-adjustments", GetTemplateFields("stock-adjustments")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["ProductCode"] = "P001", ["AdjustmentDate"] = "2026-01-15", ["MovementType"] = "ADJUST", ["Quantity"] = "-2", ["Notes"] = "ของเสียหาย" }
                }),

            "fixed-assets" => new ImportTemplateResponse("fixed-assets", GetTemplateFields("fixed-assets")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["AssetCode"] = "FA-001", ["Name"] = "คอมพิวเตอร์โน้ตบุ๊ก", ["Category"] = "เครื่องใช้สำนักงาน",
                        ["PurchaseDate"] = "2024-03-15", ["PurchaseCost"] = "35000.00", ["SalvageValue"] = "0",
                        ["UsefulLifeMonths"] = "60", ["DepreciationMethod"] = "StraightLine", ["AccumulatedDepreciation"] = "0" }
                }),

            "payments" => new ImportTemplateResponse("payments", GetTemplateFields("payments")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["DocumentNumber"] = "INV-202601-0001", ["PaymentDate"] = "2026-01-20",
                        ["Amount"] = "10700.00", ["PaymentMethod"] = "BankTransfer", ["Reference"] = "TXN-12345" }
                }),

            "projects" => new ImportTemplateResponse("projects", GetTemplateFields("projects")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["Code"] = "PRJ-001", ["Name"] = "โปรเจกต์ตัวอย่าง", ["StartDate"] = "2026-01-01",
                        ["BudgetAmount"] = "500000.00", ["Status"] = "Active", ["BillingMethod"] = "FixedPrice" }
                }),

            "employees" => new ImportTemplateResponse("employees", GetTemplateFields("employees")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["EmployeeCode"] = "EMP-001", ["TitleTh"] = "นาย", ["FirstNameTh"] = "สมชาย",
                        ["LastNameTh"] = "ใจดี", ["StartDate"] = "2024-01-15", ["BaseSalary"] = "25000.00",
                        ["SalaryType"] = "Monthly", ["Department"] = "ฝ่ายขาย" }
                }),

            "budgets" => new ImportTemplateResponse("budgets", GetTemplateFields("budgets")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["BudgetName"] = "งบ 2026", ["FiscalYear"] = "2026", ["AccountCode"] = "5210",
                        ["Month1"] = "10000", ["Month2"] = "10000", ["Month3"] = "10000",
                        ["Month4"] = "12000", ["Month5"] = "12000", ["Month6"] = "12000",
                        ["Month7"] = "12000", ["Month8"] = "12000", ["Month9"] = "12000",
                        ["Month10"] = "15000", ["Month11"] = "15000", ["Month12"] = "15000" }
                }),

            "documents" => new ImportTemplateResponse("documents", GetTemplateFields("documents")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["DocumentNumber"] = "INV-202601-0001", ["DocumentType"] = "Invoice",
                        ["DocumentDate"] = "2026-01-15", ["DueDate"] = "2026-02-14",
                        ["ContactName"] = "บริษัท ลูกค้า จำกัด", ["ContactTaxId"] = "0123456789012",
                        ["LineDescription"] = "บริการที่ปรึกษา", ["LineQuantity"] = "1",
                        ["LineUnitPrice"] = "10000.00", ["LineVatRate"] = "7", ["LineWhtRate"] = "3" },
                    new() { ["DocumentNumber"] = "INV-202601-0001", ["DocumentType"] = "Invoice",
                        ["DocumentDate"] = "2026-01-15", ["DueDate"] = "2026-02-14",
                        ["ContactName"] = "บริษัท ลูกค้า จำกัด", ["ContactTaxId"] = "0123456789012",
                        ["LineDescription"] = "ค่าเดินทาง", ["LineQuantity"] = "1",
                        ["LineUnitPrice"] = "500.00", ["LineVatRate"] = "7" }
                }),

            _ => throw new InvalidOperationException($"ไม่รองรับ template สำหรับ {entityType}")
        };

        return Task.FromResult(template);
    }

    public async Task<ImportResult> ValidateImportAsync(Guid companyId, ImportRequest request)
    {
        var errors = new List<ImportError>();
        var validCount = 0;

        for (int i = 0; i < request.Data.Count; i++)
        {
            var row = request.Data[i];
            var rowErrors = ValidateRow(request.EntityType, row, i + 1);
            if (rowErrors.Any())
                errors.AddRange(rowErrors);
            else
                validCount++;
        }

        return await Task.FromResult(new ImportResult(request.EntityType, request.Data.Count, validCount,
            errors.Count, 0, errors, DateTime.UtcNow));
    }

    public async Task<ExportResult> ExportAsync(Guid companyId, ExportRequest request)
    {
        var data = request.EntityType.ToLower() switch
        {
            "contacts" => await ExportContactsAsync(companyId, request),
            "products" => await ExportProductsAsync(companyId, request),
            "chartofaccounts" or "chart-of-accounts" => await ExportAccountsAsync(companyId, request),
            "journalentries" or "journal-entries" => await ExportJournalEntriesAsync(companyId, request),
            "documents" => await ExportDocumentsAsync(companyId, request),
            "banktransactions" => await ExportBankTransactionsAsync(companyId, request),
            "stock-movements" => await ExportStockMovementsAsync(companyId, request),
            "stock-balances" => await ExportStockBalancesAsync(companyId, request),
            "fixed-assets" => await ExportFixedAssetsAsync(companyId, request),
            "payments" => await ExportPaymentsAsync(companyId, request),
            "projects" => await ExportProjectsAsync(companyId, request),
            "employees" => await ExportEmployeesAsync(companyId, request),
            "budgets" => await ExportBudgetsAsync(companyId, request),
            _ => throw new InvalidOperationException($"ไม่รองรับการส่งออก {request.EntityType}")
        };

        var csv = BuildCsv(data);
        var fileName = $"{request.EntityType}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";

        return new ExportResult(request.EntityType, "csv", data.Count,
            fileName, "text/csv", Encoding.UTF8.GetBytes(csv));
    }

    public Task<List<string>> GetExportableEntitiesAsync()
    {
        return Task.FromResult(new List<string>
        {
            "contacts", "products", "chartofaccounts", "journalentries", "documents",
            "banktransactions", "stock-movements", "stock-balances", "fixed-assets",
            "payments", "projects", "employees", "budgets"
        });
    }

    // ===== Import Helpers =====

    /// <param name="action">"Skip" · "Overwrite" · "Merge" (ผ่าน <see cref="ResolveAction"/> แล้ว)</param>
    /// <param name="matchedId">ศูนย์นำเข้า: ผู้ติดต่อเดิมที่ผู้ใช้เลือกจับคู่เอง (ชื่อคล้าย = แนะนำ ผู้ใช้ยืนยัน · คำตัดสินข้อ 151 (5)) — ต้องเป็นของบริษัทนี้</param>
    private async Task<RowWrite> ImportContactAsync(Guid companyId, Dictionary<string, string> row,
        string action = "Merge", Guid? matchedId = null)
    {
        // ช่องว่าง/ช่องว่างล้วนต้องถูกปฏิเสธเหมือนไม่มีช่อง — เดิมผ่านแล้ว Overwrite ล้างชื่อผู้ติดต่อเดิมเป็น "" (2026-10-08)
        var name = row.GetValueOrDefault("Name");
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleException("ชื่อผู้ติดต่อ (Name) เว้นว่างไม่ได้");
        name = name.Trim();
        var email = Val(row, "Email");

        // ── เลขผู้เสียภาษีจากไฟล์ต้องผ่านด่านเดียวกับ /api/v1 ──
        // เดิมเส้นนำเข้าไฟล์**ไม่ตรวจอะไรเลย** ⇒ "1234", "N/A", เลขที่ checksum ไม่ผ่าน
        // ถูกเก็บลงทะเบียนแล้วไปโผล่บนใบกำกับเต็มรูปที่ออกภายหลัง (§86/4 ไม่ครบ ⇒
        // ผู้ซื้อเคลมภาษีซื้อไม่ได้ §82/5(1)) — ราก R5 "ทางเข้าอื่นไม่เดินด่าน"
        var rawTaxId = row.GetValueOrDefault("TaxId");
        string? taxId = null;
        if (!string.IsNullOrWhiteSpace(rawTaxId))
        {
            var check = Accounting.Helpers.ThaiTaxIdValidator.Check(rawTaxId);
            if (!check.IsValid)
                throw new BusinessRuleException(
                    $"เลขประจำตัวผู้เสียภาษี \"{rawTaxId}\" ของ \"{name}\" ไม่ถูกต้อง ({check.Reason}) — "
                    + "แก้ในไฟล์แล้วนำเข้าใหม่ หรือลบคอลัมน์ TaxId ออกถ้ายังไม่มีเลข "
                    + "(เลขที่ผิดจะทำให้ใบกำกับภาษีที่ออกภายหลังขาดรายการตาม §86/4)");
            taxId = Accounting.Helpers.ThaiTaxIdValidator.Normalize(rawTaxId);
        }

        // Idempotent on re-import: match by (TaxId + BranchCode), then Email — รอบ 193 ข้อ 20: คีย์เลขภาษี + สาขา
        // (Helpers/ContactTaxBranchKey) · คอลัมน์ BranchCode ว่าง = "ไม่ระบุ" (แถว สนญ. ก่อน → แถวเดียวที่มี) ไม่ใช่ 00000
        // ⇒ นำเข้าไฟล์เดิมซ้ำไม่สร้างแถวใหม่ · มีเลขนี้แต่คนละสาขา ⇒ ห้ามถอยไปจับด้วยอีเมล (จะได้แถวสาขาอื่น)
        Contact? existing = null;
        var taxKey = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(
            _db.Contacts, companyId, taxId, row.GetValueOrDefault("BranchCode"));
        var matchedBy = Accounting.Helpers.ContactMatchKind.TaxKey;
        if (matchedId is Guid userPick)
        {
            // รอบ 205 ทีม B: ผู้ใช้เลือกแถวเดิมเองที่ศูนย์นำเข้า — ต้องเป็นของบริษัทนี้ · เลขในไฟล์ต่างจากเลขของแถวที่เลือก = คนละนิติบุคคล ⇒ ไม่ทับ
            existing = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == userPick && c.CompanyId == companyId && !c.IsDeleted)
                ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อที่เลือกจับคู่ (อาจถูกลบไปแล้ว) — เลือกใหม่ หรือเปลี่ยนเป็น \"สร้างใหม่\"");
            if (taxId != null && Accounting.Helpers.ContactTaxBranchKey.HasTaxId(existing.TaxId)
                && Accounting.Helpers.ThaiTaxIdValidator.Normalize(existing.TaxId) != taxId)
                throw new BusinessRuleException(
                    $"ผู้ติดต่อที่เลือก \"{existing.Name}\" มีเลขผู้เสียภาษีอื่นอยู่แล้ว แต่แถวนี้มีเลข {taxId} — เป็นคนละนิติบุคคล: "
                    + "เปลี่ยนเป็น \"สร้างใหม่\" หรือเลือกผู้ติดต่อที่เลขตรงกัน");
            matchedBy = Accounting.Helpers.ContactMatchKind.ExternalKey;
        }
        else if (taxKey.ContactId is Guid keyId)
            existing = await _db.Contacts.FirstOrDefaultAsync(c =>
                c.Id == keyId && c.CompanyId == companyId && !c.IsDeleted);
        // ฝ่ายค้าน C-6: อีเมลจับได้เฉพาะชุด SoftScope — เลขใหม่ ⇒ เฉพาะแถวที่ยังไม่มีเลข (โหมด Overwrite จะทับข้อมูลคนละนิติบุคคล)
        var softScope = Accounting.Helpers.ContactTaxBranchKey.SoftScope(_db.Contacts, companyId, taxId, taxKey);
        if (existing == null && softScope != null && !string.IsNullOrWhiteSpace(email))
        {
            existing = await softScope.FirstOrDefaultAsync(c => !c.IsDeleted && c.Email == email);
            matchedBy = Accounting.Helpers.ContactMatchKind.Email;
        }

        var isCustomer = OptFlag(row, "IsCustomer", "เป็นลูกค้า");
        var isSupplier = OptFlag(row, "IsSupplier", "เป็นผู้ขาย");

        if (existing != null)
        {
            if (action == "Skip") return new RowWrite("Contact", existing.Id, Skipped: true);
            // ฝ่ายค้านรอบสอง R2-C5: แถวที่จับได้ด้วยอีเมล (ยังไม่มีเลข) รับเลข + สาขาจากไฟล์ — เดิมทั้ง Merge/Overwrite ไม่เขียน TaxId
            // แต่ ContactType ถูกคำนวณจากเลขในไฟล์ ⇒ แถวเป็น "นิติบุคคลที่ไม่มีเลข" และนำเข้าซ้ำก็ไม่ติด ·
            // รอบสาม: แถวลูกค้าทั่วไป (walk-in) ไม่รับเลข ⇒ Reject = สร้างแถวใหม่ของรายนี้ (ด้านล่าง)
            var beforeSnapshot = ContactSnapshot(existing);
            if (Accounting.Helpers.ContactTaxBranchKey.AdoptTaxId(existing, taxId, row.GetValueOrDefault("BranchCode"), matchedBy)
                == Accounting.Helpers.ContactAdoptOutcome.Reject)
                existing = null;
            else
            {
                // Merge (default): existing wins; new fills blanks + OR-merges flags.
                // Overwrite: non-empty incoming overrides existing.
                if (action == "Overwrite")
                {
                    existing.Name = name;
                    if (!string.IsNullOrWhiteSpace(row.GetValueOrDefault("Phone"))) existing.Phone = row.GetValueOrDefault("Phone");
                    if (!string.IsNullOrWhiteSpace(row.GetValueOrDefault("Address"))) existing.Address = row.GetValueOrDefault("Address");
                    if (!string.IsNullOrWhiteSpace(row.GetValueOrDefault("ContactPerson"))) existing.ContactPerson = row.GetValueOrDefault("ContactPerson");
                    if (!string.IsNullOrWhiteSpace(email)) existing.Email = email;
                }
                else // Merge
                {
                    if (string.IsNullOrWhiteSpace(existing.Phone)) existing.Phone = row.GetValueOrDefault("Phone");
                    if (string.IsNullOrWhiteSpace(existing.Address)) existing.Address = row.GetValueOrDefault("Address");
                    if (string.IsNullOrWhiteSpace(existing.ContactPerson)) existing.ContactPerson = row.GetValueOrDefault("ContactPerson");
                    if (string.IsNullOrWhiteSpace(existing.Email) && !string.IsNullOrWhiteSpace(email)) existing.Email = email;
                }
                existing.IsCustomer = existing.IsCustomer || isCustomer;
                existing.IsSupplier = existing.IsSupplier || isSupplier;
                // ชนิดผู้ติดต่อ: ตัวตัดสินตัวเดียว + "ค่าเดิมที่คนตั้งไว้ชนะการอนุมาน"
                // (ApplyToExisting) ⇒ ไฟล์นำเข้าไม่ลดระดับคู่ค้าที่ตั้งไว้ถูกแล้ว
                existing.ContactType = Accounting.Helpers.ContactTypeResolver.ApplyToExisting(
                    existing.ContactType, row.GetValueOrDefault("ContactType"),
                    taxId ?? existing.TaxId, existing.Name).Type;
                // รอบ 193 ทีม C3: รหัสสาขาในไฟล์เขียนทับได้เฉพาะแถวที่ "ตรงสาขาแล้ว" — รหัสผิดรูป ("8A") ได้แถว สนญ. จากกติกา
                // "ไม่ระบุ" แต่ BranchCodeFor ดึงเลขเป็น 00008 ⇒ ห้ามเขียน มิฉะนั้นแถว สนญ. กลายเป็นสาขา 8
                var branchIn = string.IsNullOrWhiteSpace(row.GetValueOrDefault("BranchCode")) || !taxKey.MayOverwriteBranch
                    ? existing.BranchCode : row.GetValueOrDefault("BranchCode");
                existing.BranchCode = Accounting.Helpers.ContactTypeResolver.BranchCodeFor(
                    existing.ContactType, branchIn);
                existing.UpdatedAt = DateTime.UtcNow;
                return new RowWrite("Contact", existing.Id, BeforeJson: Before(beforeSnapshot));
            }
        }

        // ── ชนิดผู้ติดต่อ + รหัสสาขา จากตัวตัดสินตัวเดียว (Helpers/ContactTypeResolver) ──
        // เดิมเส้นนำเข้าไฟล์**ไม่เคยตั้ง ContactType เลย** ⇒ ตกค่า default ของ entity
        // (เดิม Individual) ทุกแถว ⇒ คู่ค้านิติบุคคลที่นำเข้าจากไฟล์ถูกยื่น ภ.ง.ด.3
        // แทน ภ.ง.ด.53 และ e-Tax ได้ scheme NIDN แทน TXID
        var identity = Accounting.Helpers.ContactTypeResolver.ResolveWithBranch(
            declared: row.GetValueOrDefault("ContactType"),
            taxId: taxId,
            branchCode: row.GetValueOrDefault("BranchCode"),
            name: name);

        var contact = new Contact
        {
            CompanyId = companyId,
            Name = name,
            TaxId = taxId,
            ContactType = identity.Type,
            BranchCode = identity.BranchCode,
            IsCustomer = isCustomer,
            IsSupplier = isSupplier,
            Email = email,
            Phone = row.GetValueOrDefault("Phone"),
            Address = row.GetValueOrDefault("Address"),
            ContactPerson = row.GetValueOrDefault("ContactPerson")
        };
        _db.Contacts.Add(contact);
        return new RowWrite("Contact", contact.Id);
    }

    /// <summary>ค่าเดิมของผู้ติดต่อก่อนทับ — ชุดเดียวกับที่ writer แตะ (ย้อนแบบ RestoreBefore ที่ศูนย์นำเข้า)</summary>
    internal static Dictionary<string, string?> ContactSnapshot(Contact c) => new()
    {
        ["Name"] = c.Name, ["TaxId"] = c.TaxId, ["BranchCode"] = c.BranchCode, ["ContactType"] = c.ContactType.ToString(),
        ["Phone"] = c.Phone, ["Address"] = c.Address, ["ContactPerson"] = c.ContactPerson, ["Email"] = c.Email,
        ["IsCustomer"] = c.IsCustomer ? "true" : "false", ["IsSupplier"] = c.IsSupplier ? "true" : "false",
    };

    private async Task<RowWrite> ImportProductAsync(Guid companyId, Dictionary<string, string> row,
        string action = "Skip", Guid? matchedId = null)
    {
        var code = Val(row, "Code") ?? throw new BusinessRuleException("ต้องระบุรหัสสินค้า (Code)");
        var existing = matchedId is Guid userPick
            ? await _db.Products.FirstOrDefaultAsync(p => p.Id == userPick && p.CompanyId == companyId && !p.IsDeleted)
                ?? throw new KeyNotFoundException("ไม่พบสินค้าที่เลือกจับคู่ (อาจถูกลบไปแล้ว) — เลือกใหม่ หรือเปลี่ยนเป็น \"สร้างใหม่\"")
            : await _db.Products.FirstOrDefaultAsync(p =>
                p.CompanyId == companyId && !p.IsDeleted && p.Code == code);
        var sellingPrice = OptAmount(row, "SellingPrice", "ราคาขาย");
        var costPrice = OptAmount(row, "CostPrice", "ราคาทุน");
        var vatRate = OptVatRate(row, "VatRate", "อัตราภาษี");

        if (existing != null)
        {
            // รอบ 205 ทีม B (P0): ข้าม = ข้าม (นับเป็น "ข้าม") — เดิม throw ⇒ นับเป็นแถวผิดทั้งที่ผู้ใช้ไม่ได้เลือกทับ
            if (action == "Skip") return new RowWrite("Product", existing.Id, Skipped: true);
            var before = new Dictionary<string, string?>
            {
                ["Name"] = existing.Name, ["Unit"] = existing.Unit, ["Category"] = existing.Category,
                ["SellingPrice"] = existing.SellingPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CostPrice"] = existing.CostPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["VatRate"] = existing.VatRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            if (action == "Overwrite")
            {
                if (Val(row, "Name") is { } n1) existing.Name = n1;
                if (Val(row, "Unit") is { } u1) existing.Unit = u1;
                if (sellingPrice is decimal sp1) existing.SellingPrice = sp1;
                if (costPrice is decimal cp1) existing.CostPrice = cp1;
                if (vatRate is decimal vr1) existing.VatRate = vr1;
                if (Val(row, "Category") is { } c1) existing.Category = c1;
            }
            else if (action == "Merge")
            {
                if (existing.SellingPrice == 0 && sellingPrice is decimal sp2) existing.SellingPrice = sp2;
                if (existing.CostPrice == 0 && costPrice is decimal cp2) existing.CostPrice = cp2;
                if (string.IsNullOrWhiteSpace(existing.Category)) existing.Category = Val(row, "Category");
            }
            existing.UpdatedAt = DateTime.UtcNow;
            return new RowWrite("Product", existing.Id, BeforeJson: Before(before));
        }

        var productType = ProductType.Product;
        if (Val(row, "ProductType") is { } ptRaw)
        {
            var pt = ImportValueNormalizer.Enum(ptRaw, ProductTypeAliases);
            if (!pt.Ok || pt.Value is not ProductType ptv) throw new BusinessRuleException($"ประเภทสินค้า: {pt.Message}");
            productType = ptv;
        }
        var product = new Product
        {
            CompanyId = companyId,
            Code = code,
            Name = Val(row, "Name") ?? throw new BusinessRuleException("ต้องระบุชื่อสินค้า (Name)"),
            Unit = Val(row, "Unit") ?? "ชิ้น",
            SellingPrice = sellingPrice ?? 0,
            CostPrice = costPrice ?? 0,
            VatRate = vatRate ?? 7,
            Category = Val(row, "Category"),
            ProductType = productType
        };
        _db.Products.Add(product);
        return new RowWrite("Product", product.Id);
    }

    /// <summary>คำไทยของประเภทสินค้าที่ไฟล์จากโปรแกรมไทยใช้ — เดิมค่าที่ไม่ใช่ชื่อ enum ตกเป็น "สินค้า" เงียบ ๆ</summary>
    private static readonly IReadOnlyDictionary<string, ProductType> ProductTypeAliases = new Dictionary<string, ProductType>(StringComparer.OrdinalIgnoreCase)
    {
        ["สินค้า"] = ProductType.Product,
        ["บริการ"] = ProductType.Service,
        ["ไม่นับสต็อก"] = ProductType.NonStock,
        ["ไม่ติดตามสต็อก"] = ProductType.NonStock,
        ["วัสดุสิ้นเปลือง"] = ProductType.Supplies,
    };

    private async Task<RowWrite> ImportAccountAsync(Guid companyId, Dictionary<string, string> row,
        string action = "Skip", Guid? matchedId = null)
    {
        var code = Val(row, "AccountCode") ?? throw new BusinessRuleException("ต้องระบุรหัสบัญชี (AccountCode)");
        var existing = matchedId is Guid userPick
            ? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.Id == userPick && a.CompanyId == companyId && !a.IsDeleted)
                ?? throw new KeyNotFoundException("ไม่พบบัญชีที่เลือกจับคู่ (อาจถูกลบไปแล้ว) — เลือกใหม่ หรือเปลี่ยนเป็น \"สร้างใหม่\"")
            : await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && !a.IsDeleted && a.AccountCode == code);

        if (existing != null)
        {
            // รอบ 205 ทีม B (P0): ข้าม = ข้าม — เดิม throw ⇒ นับเป็นแถวผิด
            if (action == "Skip") return new RowWrite("ChartOfAccount", existing.Id, Skipped: true);
            var before = new Dictionary<string, string?>
            {
                ["AccountName"] = existing.AccountName, ["AccountNameEn"] = existing.AccountNameEn,
                ["AccountType"] = existing.AccountType.ToString(), ["Description"] = existing.Description,
            };

            if (action == "Overwrite")
            {
                if (Val(row, "AccountName") is { } n1) existing.AccountName = n1;
                if (Val(row, "AccountNameEn") is { } ne) existing.AccountNameEn = ne;
                if (Enum.TryParse<AccountType>(row.GetValueOrDefault("AccountType"), true, out var at))
                    existing.AccountType = at;
                if (Val(row, "Description") is { } d1) existing.Description = d1;
            }
            else if (action == "Merge")
            {
                if (string.IsNullOrWhiteSpace(existing.AccountNameEn))
                    existing.AccountNameEn = Val(row, "AccountNameEn");
                if (string.IsNullOrWhiteSpace(existing.Description))
                    existing.Description = Val(row, "Description");
            }
            existing.UpdatedAt = DateTime.UtcNow;
            return new RowWrite("ChartOfAccount", existing.Id, BeforeJson: Before(before));
        }

        if (!Enum.TryParse<AccountType>(row.GetValueOrDefault("AccountType"), true, out var acctType))
            throw new BusinessRuleException("ประเภทบัญชี (AccountType) ไม่ถูกต้อง — ใช้ Asset / Liability / Equity / Revenue / Expense");

        var account = new ChartOfAccount
        {
            CompanyId = companyId,
            AccountCode = code,
            AccountName = Val(row, "AccountName") ?? throw new BusinessRuleException("ต้องระบุชื่อบัญชี (AccountName)"),
            AccountNameEn = Val(row, "AccountNameEn"),
            AccountType = acctType,
            Description = Val(row, "Description"),
            Level = code.Length <= 4 ? 1 : 2
        };
        _db.ChartOfAccounts.Add(account);
        return new RowWrite("ChartOfAccount", account.Id);
    }

    /// <summary>Look up the per-row action set by the user in the preview
    /// UI; fall back to defaultAction. Accepts case-insensitive values.</summary>
    private static string ResolveAction(Dictionary<string, string>? resolutions, string key, string defaultAction)
    {
        if (resolutions != null && !string.IsNullOrEmpty(key) && resolutions.TryGetValue(key, out var picked))
            return Normalize(picked);
        return Normalize(defaultAction);

        static string Normalize(string a) => a?.Trim().ToLowerInvariant() switch
        {
            "overwrite" => "Overwrite",
            "merge" => "Merge",
            _ => "Skip",
        };
    }

    /// <summary>รายการธนาคารจากไฟล์ (Smart Import/นำเข้าเดิม) — รอบ 205 ทีม B: ลงผ่านตัวเขียนตัวเดียว
    /// <see cref="IBankService.ImportParsedStatementRowsAsync"/> (ทีม C) · เดิม <c>CurrentBalance +=</c> เอง (ยอดการ์ดบัญชีเดินตามไฟล์ ไม่ใช่ตามธนาคาร ·
    /// ความหมายยอดคงเหลือคนละแบบกับหน้า "ธนาคาร") · กันซ้ำแบบ statement ของตัวเขียน (วัน+ยอด+ทิศ+คำอธิบายตรง = ข้าม) ·
    /// คำอธิบายต่าง = ลงเป็นรายการใหม่ + คำเตือน (ความหมายเดิมของทางนี้: ซ้ำเฉพาะเมื่อทุกช่องตรง)</summary>
    private async Task<RowWrite> ImportBankTransactionAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        if (_bank == null)
            throw new BusinessRuleException("ระบบธนาคารไม่พร้อม — นำเข้าที่หน้า \"ธนาคาร → นำเข้า statement\" แทน");
        if (!Guid.TryParse(row.GetValueOrDefault("BankAccountId"), out var bankAccountId))
            throw new BusinessRuleException("BankAccountId ไม่ถูกต้อง — เลือกบัญชีธนาคารปลายทางก่อนนำเข้า");
        if (!await _db.BankAccounts.AsNoTracking().AnyAsync(a => a.Id == bankAccountId && a.CompanyId == companyId))
            throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

        if (!Enum.TryParse<BankTransactionType>(row.GetValueOrDefault("TransactionType"), true, out var txnType))
            throw new BusinessRuleException("TransactionType ไม่ถูกต้อง (Deposit/Withdrawal/Transfer/Fee/Interest)");

        var amount = Math.Abs(OptAmount(row, "Amount", "จำนวนเงิน") ?? throw new BusinessRuleException("ต้องระบุจำนวนเงิน (Amount)"));
        var txnDate = OptDate(row, "TransactionDate", "วันที่ทำรายการ") ?? Accounting.Helpers.ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var outgoing = txnType == BankTransactionType.Withdrawal || txnType == BankTransactionType.Fee;
        var line = new Accounting.Models.DTOs.Bank.BankStatementLineInput(txnDate, Val(row, "Description"),
            Withdrawal: outgoing ? amount : 0m, Deposit: outgoing ? 0m : amount, Reference: Val(row, "Reference"));

        var written = await _bank.ImportParsedStatementRowsAsync(companyId, bankAccountId, new[] { line },
            new Accounting.Models.DTOs.Bank.BankStatementWriteOptions(
                OnConflict: Accounting.Models.DTOs.Bank.BankStatementConflictPolicy.InsertAsNew,
                CreatedBy: performedBy));
        var r = written.Lines.Count > 0 ? written.Lines[0]
            : throw new BusinessRuleException("บันทึกรายการธนาคารไม่สำเร็จ — ตรวจวันที่และจำนวนเงินของแถวนี้");
        return r.Outcome switch
        {
            Accounting.Models.DTOs.Bank.BankStatementLineOutcome.Created or Accounting.Models.DTOs.Bank.BankStatementLineOutcome.Updated
                => new RowWrite("BankTransaction", r.TransactionId),
            Accounting.Models.DTOs.Bank.BankStatementLineOutcome.Duplicate or Accounting.Models.DTOs.Bank.BankStatementLineOutcome.Conflict
                => new RowWrite("BankTransaction", r.ExistingTransactionId, Skipped: true),
            _ => throw new BusinessRuleException(r.Message ??"บันทึกรายการธนาคารไม่สำเร็จ — ตรวจวันที่และจำนวนเงินของแถวนี้"),
        };
    }

    private async Task<RowWrite> ImportJournalEntryAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var date = ReqDate(row, "Date", "วันที่");
        var accountCode = Val(row, "AccountCode") ?? throw new BusinessRuleException("ต้องระบุรหัสบัญชี (AccountCode)");
        var debitAmount = OptAmount(row, "DebitAmount", "เดบิต") ?? 0;
        var creditAmount = OptAmount(row, "CreditAmount", "เครดิต") ?? 0;
        var description = row.GetValueOrDefault("Description") ?? "";
        var reference = row.GetValueOrDefault("Reference");

        // รอบ 205 ทีม B (P0): ผังที่ถูกลบห้ามรับบรรทัด JE ใหม่ (เดิมไม่กรอง IsDeleted)
        var account = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && !a.IsDeleted && a.AccountCode == accountCode)
            ?? throw new KeyNotFoundException($"ไม่พบบัญชีรหัส {accountCode} ในผังบัญชี — เพิ่มผังก่อน หรือแก้รหัสในไฟล์");

        Guid? contactId = null;
        var contactName = Val(row, "ContactName");
        if (contactName != null)
        {
            // รอบ 205 ทีม B (P0): ชื่อตรงตัวเท่านั้น — เดิม Name.Contains ไม่เรียง ⇒ "ABC" ได้ "ABC Trading" หรือ "ABC Holdings" แล้วแต่ลำดับจากฐาน
            // (ชื่อซ้ำหลายแถว = ไม่ผูก · ผู้ใช้ผูกเองที่หน้าใบสำคัญ)
            var hits = await _db.Contacts
                .Where(c => c.CompanyId == companyId && !c.IsDeleted && c.Name == contactName)
                .Select(c => c.Id).Take(2).ToListAsync();
            contactId = hits.Count == 1 ? hits[0] : null;
        }

        // สแกน ChangeTracker ก่อน DB — แถว Dr/Cr ของ JE เดียวกันในไฟล์เดียวกันอาจยังไม่อยู่ใน DB
        // (ศูนย์นำเข้าบันทึกทีละแถว · เส้นเดิมเคยบันทึกครั้งเดียวท้ายไฟล์): ถ้า query DB
        // อย่างเดียวจะหากันไม่เจอ → แตกเป็น JE บรรทัดเดียวหลายใบ (ไม่ balance
        // สักใบ) และรันไฟล์เดิมซ้ำจะ append บรรทัดเงาเข้าใบเดิม (pattern เดียวกับ
        // ImportOpeningSubledgerAsync/ImportDocumentAsync ที่แก้ไว้แล้ว)
        var existing = _db.ChangeTracker.Entries<JournalEntry>().Select(e => e.Entity)
                .FirstOrDefault(j => j.CompanyId == companyId
                    && j.EntryDate == date && j.Description == description && j.Reference == reference)
            ?? await _db.JournalEntries
                .Include(j => j.Lines)
                .FirstOrDefaultAsync(j => j.CompanyId == companyId && !j.IsDeleted
                    && j.EntryDate == date
                    && j.Description == description
                    && j.Reference == reference);

        if (existing != null)
        {
            // ใบที่ลงบัญชีแล้วห้ามเติมบรรทัดจากไฟล์ (ยอดใน GL เปลี่ยนใต้มือผู้อนุมัติ) — ทางไปต่อ: ใช้คำอธิบาย/อ้างอิงใหม่ = ใบใหม่
            if (existing.Status != JournalEntryStatus.Draft)
                throw new BusinessRuleException(
                    $"สมุดรายวัน {existing.EntryNumber} (วันที่/คำอธิบาย/อ้างอิงเดียวกับแถวนี้) ลงบัญชีแล้ว จึงเติมบรรทัดจากไฟล์ไม่ได้ — "
                    + "แก้คำอธิบายหรือเลขอ้างอิงในไฟล์ให้เป็นใบใหม่ หรือกลับรายการใบเดิมก่อน");
            // กันรันไฟล์เดิมซ้ำ: บรรทัดเดิม (บัญชี+ยอดเดียวกัน) มีแล้ว = ข้าม
            var dupLine = existing.Lines.Any(l => l.AccountId == account.Id
                && l.DebitAmount == debitAmount && l.CreditAmount == creditAmount);
            if (dupLine) return new RowWrite("JournalEntry", existing.Id, Skipped: true);
            // แถวใหม่ใต้ parent ที่ติดตามอยู่ — ผ่าน EfNewChild ตัวเดียว (คอลเลกชันอย่างเดียว ⇒ Modified ⇒ UPDATE 0 แถว · Add สองทาง ⇒ แถวเบิ้ลในคอลเลกชัน ยอดเบิ้ล)
            _db.AddNewChild(existing.Lines, new JournalEntryLine
            {
                AccountId = account.Id,
                DebitAmount = debitAmount,
                CreditAmount = creditAmount,
                Description = description
            });
            existing.TotalDebit = existing.Lines.Sum(l => l.DebitAmount);
            existing.TotalCredit = existing.Lines.Sum(l => l.CreditAmount);
            return new RowWrite("JournalEntry", existing.Id);
        }

        // ⚠️ เดิมอ่านเลขรันจากแถว NumberSeries ที่ Prefix == "JV" ซึ่ง
        // **ไม่มีใครสร้างเลยทั้งระบบ** (ไม่มี seed + ไม่มี UI สร้าง) ⇒
        // `series` เป็น null ทุกครั้ง ⇒ nextNum = 1 เสมอ ⇒ ทุก JE ที่ import
        // ได้เลข "JV-000001" ซ้ำกันหมดตั้งแต่วันแรก. ใช้ผู้ออกเลข JE ตัวเดียว
        // ของระบบแทน (มี advisory lock ข้าม instance + นับต่อเดือนจริง)
        var entryNumber = await Journal.JournalEntryBuilder.NextJournalNumberAsync(
            _db, companyId, "JV", date);

        var entry = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = date,
            Description = description,
            Reference = reference,
            Note = contactId.HasValue ? $"ContactId:{contactId}" : null,
            Status = JournalEntryStatus.Draft,
            CreatedBy = performedBy,
            TotalDebit = debitAmount,
            TotalCredit = creditAmount,
            Lines = new List<JournalEntryLine>
            {
                new()
                {
                    AccountId = account.Id,
                    DebitAmount = debitAmount,
                    CreditAmount = creditAmount,
                    Description = description
                }
            }
        };
        _db.JournalEntries.Add(entry);
        return new RowWrite("JournalEntry", entry.Id);
    }

    // ===== Export Helpers =====

    private async Task<List<Dictionary<string, string>>> ExportContactsAsync(Guid companyId, ExportRequest request)
    {
        var contacts = await _db.Contacts.Where(c => c.CompanyId == companyId).OrderBy(c => c.Name).ToListAsync();
        return contacts.Select(c => new Dictionary<string, string>
        {
            ["Name"] = c.Name, ["TaxId"] = c.TaxId ?? "",
            // ส่งออกเป็น **ชื่อ** เสมอ (ไม่ใช่เลข) และต้องอยู่ในไฟล์ด้วย มิฉะนั้น
            // export → import กลับ จะทำให้ชนิดผู้ติดต่อของทั้งทะเบียนหายไป
            ["ContactType"] = c.ContactType.ToString(),
            ["BranchCode"] = c.BranchCode ?? "",
            ["IsCustomer"] = c.IsCustomer.ToString(),
            ["IsSupplier"] = c.IsSupplier.ToString(), ["Email"] = c.Email ?? "", ["Phone"] = c.Phone ?? "",
            ["Address"] = c.Address ?? "", ["ContactPerson"] = c.ContactPerson ?? ""
        }).ToList();
    }

    private async Task<List<Dictionary<string, string>>> ExportProductsAsync(Guid companyId, ExportRequest request)
    {
        var products = await _db.Products.Where(p => p.CompanyId == companyId).OrderBy(p => p.Code).ToListAsync();
        return products.Select(p => new Dictionary<string, string>
        {
            ["Code"] = p.Code, ["Name"] = p.Name, ["ProductType"] = p.ProductType.ToString(),
            ["Unit"] = p.Unit, ["SellingPrice"] = p.SellingPrice.ToString("F2"),
            ["CostPrice"] = p.CostPrice.ToString("F2"), ["VatRate"] = p.VatRate.ToString("F2"),
            ["Category"] = p.Category ?? "", ["CurrentStock"] = p.CurrentStock.ToString("F4")
        }).ToList();
    }

    private async Task<List<Dictionary<string, string>>> ExportAccountsAsync(Guid companyId, ExportRequest request)
    {
        var accounts = await _db.ChartOfAccounts.Where(a => a.CompanyId == companyId).OrderBy(a => a.AccountCode).ToListAsync();
        return accounts.Select(a => new Dictionary<string, string>
        {
            ["AccountCode"] = a.AccountCode, ["AccountName"] = a.AccountName,
            ["AccountNameEn"] = a.AccountNameEn ?? "", ["AccountType"] = a.AccountType.ToString(),
            ["Level"] = a.Level.ToString(), ["IsActive"] = a.IsActive.ToString()
        }).ToList();
    }

    private async Task<List<Dictionary<string, string>>> ExportJournalEntriesAsync(Guid companyId, ExportRequest request)
    {
        var query = _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Where(j => j.CompanyId == companyId);

        if (request.FromDate.HasValue) query = query.Where(j => j.EntryDate >= request.FromDate);
        if (request.ToDate.HasValue) query = query.Where(j => j.EntryDate <= request.ToDate);

        var entries = await query.OrderBy(j => j.EntryDate).ToListAsync();
        var rows = new List<Dictionary<string, string>>();
        foreach (var entry in entries)
        {
            foreach (var line in entry.Lines.OrderBy(l => l.LineOrder))
            {
                rows.Add(new Dictionary<string, string>
                {
                    ["EntryNumber"] = entry.EntryNumber, ["EntryDate"] = entry.EntryDate.ToString("yyyy-MM-dd"),
                    ["Status"] = entry.Status.ToString(), ["Description"] = entry.Description ?? "",
                    ["AccountCode"] = line.Account.AccountCode, ["AccountName"] = line.Account.AccountName,
                    ["Debit"] = line.DebitAmount.ToString("F2"), ["Credit"] = line.CreditAmount.ToString("F2"),
                    ["LineDescription"] = line.Description ?? ""
                });
            }
        }
        return rows;
    }

    private async Task<List<Dictionary<string, string>>> ExportDocumentsAsync(Guid companyId, ExportRequest request)
    {
        // เอกสาร sensitivity (payroll) ไม่ออก CSV รวม (PDPA audit E3) — ใช้
        // รายงานฝั่ง payroll ที่คุมสิทธิ์แทน
        var query = _db.Documents.Where(d => d.CompanyId == companyId
            && d.Sensitivity == Models.Enums.SensitivityKind.None);
        if (request.FromDate.HasValue) query = query.Where(d => d.DocumentDate >= request.FromDate);
        if (request.ToDate.HasValue) query = query.Where(d => d.DocumentDate <= request.ToDate);

        var docs = await query.OrderBy(d => d.DocumentDate).ToListAsync();
        await _db.HydrateContactsAsync(companyId, docs);  // กัน INNER JOIN ตัดใบที่ contact ถูกลบ
        return docs.Select(d => new Dictionary<string, string>
        {
            ["DocumentNumber"] = d.DocumentNumber, ["DocumentType"] = d.DocumentType.ToString(),
            ["DocumentDate"] = d.DocumentDate.ToString("yyyy-MM-dd"), ["Status"] = d.Status.ToString(),
            ["ContactName"] = d.Contact.Name, ["SubTotal"] = d.SubTotal.ToString("F2"),
            ["VatAmount"] = d.VatAmount.ToString("F2"), ["TotalAmount"] = d.TotalAmount.ToString("F2"),
            ["PaidAmount"] = d.PaidAmount.ToString("F2"), ["BalanceDue"] = d.BalanceDue.ToString("F2")
        }).ToList();
    }

    // ===== Stock import =====

    private async Task<RowWrite> ImportStockOpeningAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var code = Val(row, "ProductCode") ?? throw new BusinessRuleException("ต้องระบุรหัสสินค้า (ProductCode)");
        var product = await _db.Products.FirstOrDefaultAsync(p => p.CompanyId == companyId && !p.IsDeleted && p.Code == code)
            ?? throw new KeyNotFoundException($"ไม่พบสินค้ารหัส {code}");

        var qty = OptNumber(row, "Quantity", "ยอดยกมา") ?? throw new BusinessRuleException("ต้องระบุจำนวนยอดยกมา (Quantity)");
        var unitCost = OptAmount(row, "UnitCost", "ต้นทุน/หน่วย") ?? product.CostPrice;
        var openingDate = OptDate(row, "OpeningDate", "วันยกมา") ?? DateTime.UtcNow;

        // Replace any previously imported opening balance so re-running the
        // file twice doesn't double-count. The MovementType OPENING is the
        // unique signal we use to identify migration-time entries.
        var existingOpening = await _db.StockMovements.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.ProductId == product.Id && m.MovementType == "OPENING" && !m.IsDeleted)
            .OrderBy(m => m.MovementDate).ThenBy(m => m.CreatedAt)
            .Select(m => new { m.WarehouseId, m.Quantity, m.UnitCost })
            .ToListAsync();
        if (_stock == null)
            throw new BusinessRuleException("ระบบสต็อกไม่พร้อม — ไม่สามารถนำเข้าสต็อกยกมาได้");

        // ล้างยอดยกมาชุดก่อนผ่าน ledger (ไม่ใช่ `CurrentStock -=` เอง) เพื่อให้ `WarehouseStock` ถูกล้างตามไปพร้อมกัน
        // รอบ 201 ฝ่ายค้าน X4: เดิม (1) ลบแถว OPENING ทิ้งจริง (ประวัติหาย · ยอดจากประวัติไม่ตรงคลัง) และ (2) แถวล้างเป็น OUT/IN ⇒ คิว FIFO
        // นับเป็นการขายที่กินล็อตเก่าสุด/ล็อตใหม่ · ตอนนี้: ไม่ลบอะไร · แถวล้างเป็น OPENING จำนวนติดลบ (คู่หักล้างของยอดยกมาเดิม ·
        // InventoryCostFlow.CancelReversals จับคู่แถว OPENING ขนาดเท่ากันตัดออกจากคิว) ที่ต้นทุนของยอดยกมาเดิม (ถัวเฉลี่ยติดตามมูลค่าถอดได้ตรง)
        foreach (var ex in existingOpening.GroupBy(m => m.WarehouseId))
        {
            var net = ex.Sum(m => m.Quantity);
            if (net == 0m) continue;
            var originalCost = ex.LastOrDefault(m => m.Quantity > 0m && m.UnitCost > 0m)?.UnitCost ?? unitCost;
            await _stock.MoveAsync(new StockMoveRequest(
                CompanyId: companyId, ProductId: product.Id, Quantity: -net,
                MovementType: "OPENING",
                Reference: "OPENING-RESET", WarehouseId: ex.Key,
                UnitCostOverride: originalCost,
                Notes: "ล้างสต็อกยกมาชุดก่อน (นำเข้าซ้ำ)", CreatedBy: performedBy));
        }

        product.CostPrice = unitCost;
        await _stock.MoveAsync(new StockMoveRequest(
            CompanyId: companyId,
            ProductId: product.Id,
            Quantity: qty,
            MovementType: "OPENING",
            WarehouseId: null,                // ยกมาเข้าคลังหลัก
            MovementDate: openingDate,
            UnitCostOverride: unitCost,
            Notes: row.GetValueOrDefault("Notes") ?? "สต็อกยกมา (Import)",
            CreatedBy: performedBy));
        return new RowWrite("Product", product.Id);
    }

    /// <summary>
    /// Import one opening AR (receivable) or AP (payable) subledger item.
    /// Creates an already-Approved Document flagged IsOpeningBalance so the
    /// item shows up in AR/AP aging + the contact ledger — but it is NEVER
    /// auto-posted to the GL: opening docs are created directly (not via
    /// ApproveDocumentAsync) so no journal entry is generated. The control
    /// account total is carried by the TrialBalance migration's GL opening
    /// balance; posting here too would double-count. Idempotent by a
    /// deterministic DocumentNumber ("OB-AR:"/"OB-AP:" + InvoiceNumber).
    /// </summary>
    private async Task<RowWrite> ImportOpeningSubledgerAsync(
        Guid companyId, Dictionary<string, string> row, string performedBy, bool isReceivable)
    {
        var contactName = (row.GetValueOrDefault("ContactName") ?? "").Trim();
        var contactTaxId = row.GetValueOrDefault("ContactTaxId")?.Trim();
        if (string.IsNullOrWhiteSpace(contactName) && string.IsNullOrWhiteSpace(contactTaxId))
            throw new BusinessRuleException("ต้องระบุชื่อ (ContactName) หรือเลขผู้เสียภาษี (ContactTaxId) ของคู่ค้า");

        var invoiceNumber = (row.GetValueOrDefault("InvoiceNumber") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            throw new BusinessRuleException("ต้องระบุเลขที่เอกสารเดิม (InvoiceNumber)");

        var amount = OptAmount(row, "Amount", "ยอดคงค้าง") ?? 0m;
        if (amount <= 0)
            throw new BusinessRuleException("ยอดคงค้าง (Amount) ต้องเป็นตัวเลขมากกว่า 0");

        var invoiceDate = ReqDate(row, "InvoiceDate", "วันที่เอกสาร");
        DateTime? dueDate = OptDate(row, "DueDate", "วันครบกำหนด");

        // Resolve or create the contact (migration convenience). The import
        // runs many rows then SaveChanges once at the end — so also scan the
        // change tracker, else two rows for the same new contact would each
        // insert a duplicate.
        Contact? contact = null;
        // รอบ 193 ข้อ 20: คีย์เลขภาษี + สาขา (Helpers/ContactTaxBranchKey) — ไฟล์ยกมาไม่มีคอลัมน์สาขา ⇒ "ไม่ระบุ"
        // = แถว สนญ. ก่อน (เดิม FirstOrDefault หยิบแถวไหนก็ได้ของเลขนั้น)
        var taxKey = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(_db.Contacts, companyId, contactTaxId, branchCode: null);
        if (taxKey.ContactId is Guid keyId)
            contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == keyId && c.CompanyId == companyId);
        // ฝ่ายค้าน C-6: ชื่อจับได้เฉพาะชุด SoftScope (เลขใหม่ ⇒ ห้ามได้แถวที่ถือเลขอื่น — ยอดยกมาจะไปลงลูกหนี้คนละราย)
        var softScope = Accounting.Helpers.ContactTaxBranchKey.SoftScope(_db.Contacts, companyId, contactTaxId, taxKey);
        if (contact == null && softScope != null && !string.IsNullOrWhiteSpace(contactName))
            contact = await softScope.FirstOrDefaultAsync(c => c.Name == contactName);
        // แถวที่เพิ่งสร้างในชุดเดียวกัน (ยังไม่ SaveChanges) — เลขภาษีผ่านตัวจับคู่กลางตัวเดียว (รอบ 193 ทีม C3 ·
        // ไฟล์ยกมาไม่มีคอลัมน์สาขา ⇒ "ไม่ระบุ") แล้วค่อยชื่อ
        var pending = _db.ChangeTracker.Entries<Contact>().Select(e => e.Entity)
            .Where(c => c.CompanyId == companyId).ToList();
        contact ??= Accounting.Helpers.ContactTaxBranchKey.PickContact(pending, contactTaxId, branchCode: null);
        contact ??= string.IsNullOrWhiteSpace(contactName) ? null
            : Accounting.Helpers.ContactTaxBranchKey.SoftScope(pending, contactTaxId, taxKey).FirstOrDefault(c => c.Name == contactName);
        // ฝ่ายค้านรอบสอง R2-C5: แถวที่จับได้ด้วยชื่อ (ยังไม่มีเลข) รับเลขจากไฟล์ (บันทึกพร้อม SaveChanges ของชุดนำเข้า) ·
        // ทุกทางด้านบนจับด้วยเลขหรือชื่อ "==" ตรงตัว (ExactName) · รอบสาม: แถวลูกค้าทั่วไปไม่รับเลข ⇒ Reject = สร้างแถวใหม่ (ด้านล่าง)
        if (Accounting.Helpers.ContactTaxBranchKey.AdoptTaxId(contact, contactTaxId, branchCode: null,
                Accounting.Helpers.ContactMatchKind.ExactName) == Accounting.Helpers.ContactAdoptOutcome.Reject)
            contact = null;
        if (contact == null)
        {
            var newName = string.IsNullOrWhiteSpace(contactName) ? contactTaxId! : contactName;
            // ชนิดผู้ติดต่อ + รหัสสาขา จากตัวตัดสินตัวเดียว — เดิมทางเข้านี้ไม่ตั้ง
            // ContactType เลย ⇒ ลูกหนี้/เจ้าหนี้ยกมาทุกรายตกค่า default
            var identity = Accounting.Helpers.ContactTypeResolver.ResolveWithBranch(
                declared: null, taxId: contactTaxId, branchCode: null, name: newName);
            contact = new Contact
            {
                CompanyId = companyId,
                Name = newName,
                TaxId = string.IsNullOrWhiteSpace(contactTaxId) ? null : contactTaxId,
                ContactType = identity.Type,
                BranchCode = identity.BranchCode,
                IsCustomer = isReceivable,
                IsSupplier = !isReceivable,
                IsActive = true,
                CreatedBy = performedBy,
            };
            Accounting.Helpers.ContactTaxBranchKey.StampTaxIdWarning(contact);   // ฝ่ายค้านรอบสี่ P4-5: เลขไม่ผ่าน checksum ⇒ ป้ายบนผู้ติดต่อ
            _db.Contacts.Add(contact);
        }
        else if (isReceivable) { contact.IsCustomer = true; }
        else { contact.IsSupplier = true; }

        var docNumber = (isReceivable ? "OB-AR:" : "OB-AP:") + invoiceNumber;
        var docType = isReceivable ? DocumentType.Invoice : DocumentType.PurchaseInvoice;
        var notes = row.GetValueOrDefault("Description")
            ?? $"ยอดยกมา (Opening {(isReceivable ? "ลูกหนี้" : "เจ้าหนี้")}) — Import";

        var doc = _db.ChangeTracker.Entries<Document>().Select(e => e.Entity)
            .FirstOrDefault(d => d.CompanyId == companyId && d.DocumentNumber == docNumber)
            ?? await _db.Documents.Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.DocumentNumber == docNumber);

        if (doc == null)
        {
            doc = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = docType,
                DocumentDate = invoiceDate,
                DueDate = dueDate,
                ContactId = contact.Id,
                Contact = contact,
                Status = DocumentStatus.Approved,
                IsOpeningBalance = true,
                Reference = invoiceNumber,
                Notes = notes,
                CreatedBy = performedBy,
            };
            _db.Documents.Add(doc);
        }
        else
        {
            // Re-import — refresh in place so re-running the file is safe.
            doc.DocumentDate = invoiceDate;
            doc.DueDate = dueDate;
            doc.ContactId = contact.Id;
            doc.IsOpeningBalance = true;
            doc.Notes = notes;
            _db.DocumentLines.RemoveRange(doc.Lines);
            doc.Lines.Clear();
        }

        // Opening AR/AP is a pure receivable/payable carry-over — single line,
        // no new VAT/WHT (that belonged to the original invoice's period).
        // แถวใหม่ใต้ parent ที่ติดตามอยู่ — ผ่าน EfNewChild ตัวเดียว (คอลเลกชันอย่างเดียว ⇒ Modified ⇒ UPDATE 0 แถว · Add สองทาง ⇒ แถวเบิ้ลในคอลเลกชัน ยอดเบิ้ล)
        _db.AddNewChild(doc.Lines, new DocumentLine
        {
            LineOrder = 1,
            Description = notes,
            Quantity = 1,
            Unit = "งวด",
            UnitPrice = amount,
            Amount = amount,
            VatRate = 0,
            VatAmount = 0,
        });
        doc.SubTotal = amount;
        doc.VatAmount = 0;
        doc.WithholdingTaxAmount = 0;
        doc.TotalAmount = amount;
        doc.PaidAmount = 0;
        doc.BalanceDue = amount;
        return new RowWrite("Document", doc.Id);
    }

    private async Task<RowWrite> ImportStockAdjustmentAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var code = Val(row, "ProductCode") ?? throw new BusinessRuleException("ต้องระบุรหัสสินค้า (ProductCode)");
        var product = await _db.Products.FirstOrDefaultAsync(p => p.CompanyId == companyId && !p.IsDeleted && p.Code == code)
            ?? throw new KeyNotFoundException($"ไม่พบสินค้ารหัส {code}");

        var rawType = (row.GetValueOrDefault("MovementType") ?? "ADJUST").Trim().ToUpperInvariant();
        if (rawType != "IN" && rawType != "OUT" && rawType != "ADJUST")
            throw new BusinessRuleException($"ประเภทการปรับ (MovementType) ไม่ถูกต้อง: {rawType} — ใช้ IN / OUT / ADJUST");

        var qty = OptNumber(row, "Quantity", "จำนวน") ?? throw new BusinessRuleException("ต้องระบุจำนวน (Quantity)");
        // Normalize sign — IN is always positive, OUT always negative; ADJUST
        // honours the sign the user gave (allowing both +/− variance).
        var signed = rawType switch
        {
            "IN" => Math.Abs(qty),
            "OUT" => -Math.Abs(qty),
            _ => qty
        };

        var unitCost = OptAmount(row, "UnitCost", "ต้นทุน/หน่วย") ?? product.CostPrice;
        var date = OptDate(row, "AdjustmentDate", "วันที่ปรับ") ?? DateTime.UtcNow;

        if (_stock == null)
            throw new BusinessRuleException("ระบบสต็อกไม่พร้อม — ไม่สามารถนำเข้ารายการปรับสต็อกได้");

        await _stock.MoveAsync(new StockMoveRequest(
            CompanyId: companyId,
            ProductId: product.Id,
            Quantity: signed,
            MovementType: rawType,
            Reference: row.GetValueOrDefault("Reference"),
            MovementDate: date,
            UnitCostOverride: unitCost,
            Notes: row.GetValueOrDefault("Notes"),
            CreatedBy: performedBy));
        return new RowWrite("Product", product.Id);
    }

    // ===== Fixed Asset import =====

    /// <summary>
    /// รอบ 205 ทีม B: ผ่าน <c>IFixedAssetService.ImportAsync</c> (owner เดียวของการนำเข้าสินทรัพย์ — ตัวตัดสิน <c>FixedAssetImportMethod</c>: ที่ดิน/งานระหว่าง
    /// ก่อสร้างไม่คิดค่าเสื่อม · วิธีคิดที่ไม่รู้จัก = ปฏิเสธพร้อมทางแก้ · ตารางค่าเสื่อมเหมือนสร้างด้วยมือ) — เดิมเส้นนี้สร้าง FixedAsset เองแล้วตั้งวิธีเป็น
    /// "เส้นตรง" ⇒ ที่ดินที่นำเข้าทางนี้ถูกคิดค่าเสื่อม (ถดถอย E-03 รอบ 200)
    /// </summary>
    private async Task<RowWrite> ImportFixedAssetAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        if (_fixedAssets == null)
            throw new BusinessRuleException("ระบบทะเบียนสินทรัพย์ไม่พร้อม — นำเข้าที่หน้า \"สินทรัพย์ถาวร → นำเข้า\" แทน");
        var code = Val(row, "AssetCode") ?? throw new BusinessRuleException("ต้องระบุรหัสสินทรัพย์ (AssetCode)");
        var purchaseDate = ReqDate(row, "PurchaseDate", "วันที่ซื้อ");
        var purchaseCost = OptAmount(row, "PurchaseCost", "ราคาซื้อ") ?? throw new BusinessRuleException("ต้องระบุราคาซื้อ (PurchaseCost)");
        var salvage = OptAmount(row, "SalvageValue", "มูลค่าซาก") ?? 0m;
        var life = OptInt(row, "UsefulLifeMonths", "อายุการใช้งาน (เดือน)") ?? 0;
        var accDep = OptAmount(row, "AccumulatedDepreciation", "ค่าเสื่อมสะสมยกมา") ?? 0m;
        if (accDep < 0m || accDep > purchaseCost)
            throw new BusinessRuleException($"ค่าเสื่อมสะสมยกมา ({accDep:N2}) ต้องอยู่ระหว่าง 0 ถึงราคาซื้อ ({purchaseCost:N2}) ของ {code}");

        var result = await _fixedAssets.ImportAsync(companyId, new List<Accounting.Models.DTOs.FixedAsset.ImportFixedAssetRow>
        {
            new(code, Val(row, "Name") ?? "", Val(row, "Category"), Val(row, "Location"), Val(row, "SerialNumber"),
                purchaseDate, purchaseCost, salvage, life, Val(row, "DepreciationMethod") ?? ""),
        }, performedBy);
        if (result.SuccessCount == 0)
            throw new BusinessRuleException(result.Errors.FirstOrDefault()?.Replace("แถวที่ 1: ", "", StringComparison.Ordinal)
                ?? "นำเข้าสินทรัพย์ไม่สำเร็จ");

        var asset = await _db.FixedAssets.FirstAsync(a => a.CompanyId == companyId && a.AssetCode == code && !a.IsDeleted);
        if (accDep > 0m)
        {
            // ค่าเสื่อมสะสมยกมา (ย้ายระบบกลางปี) — พฤติกรรมเดิมของไฟล์นำเข้า: เก็บยอดสะสม + มูลค่าตามบัญชีสุทธิ ·
            // ตารางค่าเสื่อมช่วงที่เหลือ = รอตัวอย่างไฟล์จริง (IMPORT_ETL_REVIEW §4 ข้อ 7)
            asset.AccumulatedDepreciation = accDep;
            asset.NetBookValue = asset.PurchaseCost - accDep;
        }
        return new RowWrite("FixedAsset", asset.Id);
    }

    // ===== Stock + Asset + Bank exports =====

    private async Task<List<Dictionary<string, string>>> ExportStockMovementsAsync(Guid companyId, ExportRequest request)
    {
        var query = _db.StockMovements
            .Include(m => m.Product)
            .Where(m => m.CompanyId == companyId);
        if (request.FromDate.HasValue) query = query.Where(m => m.MovementDate >= request.FromDate);
        if (request.ToDate.HasValue) query = query.Where(m => m.MovementDate <= request.ToDate);
        var movements = await query.OrderBy(m => m.MovementDate).ToListAsync();

        return movements.Select(m => new Dictionary<string, string>
        {
            ["MovementDate"] = m.MovementDate.ToString("yyyy-MM-dd"),
            ["ProductCode"] = m.Product.Code,
            ["ProductName"] = m.Product.Name,
            ["MovementType"] = m.MovementType,
            ["Quantity"] = m.Quantity.ToString("F4"),
            ["UnitCost"] = m.UnitCost.ToString("F2"),
            ["BalanceAfter"] = m.BalanceAfter.ToString("F4"),
            ["Reference"] = m.Reference ?? "",
            ["Notes"] = m.Notes ?? ""
        }).ToList();
    }

    private async Task<List<Dictionary<string, string>>> ExportStockBalancesAsync(Guid companyId, ExportRequest request)
    {
        var products = await _db.Products
            .Where(p => p.CompanyId == companyId && p.TrackStock && p.IsActive)
            .OrderBy(p => p.Code)
            .ToListAsync();
        return products.Select(p => new Dictionary<string, string>
        {
            ["Code"] = p.Code,
            ["Name"] = p.Name,
            ["Category"] = p.Category ?? "",
            ["Unit"] = p.Unit,
            ["CurrentStock"] = p.CurrentStock.ToString("F4"),
            ["CostPrice"] = p.CostPrice.ToString("F2"),
            ["StockValue"] = (p.CurrentStock * p.CostPrice).ToString("F2"),
            ["MinimumStock"] = p.MinimumStock.ToString("F4"),
            ["BelowMinimum"] = (p.CurrentStock < p.MinimumStock).ToString()
        }).ToList();
    }

    private async Task<List<Dictionary<string, string>>> ExportFixedAssetsAsync(Guid companyId, ExportRequest request)
    {
        var assets = await _db.FixedAssets
            .Where(a => a.CompanyId == companyId)
            .OrderBy(a => a.AssetCode)
            .ToListAsync();
        return assets.Select(a => new Dictionary<string, string>
        {
            ["AssetCode"] = a.AssetCode,
            ["Name"] = a.Name,
            ["Category"] = a.Category ?? "",
            ["Location"] = a.Location ?? "",
            ["SerialNumber"] = a.SerialNumber ?? "",
            ["PurchaseDate"] = a.PurchaseDate.ToString("yyyy-MM-dd"),
            ["PurchaseCost"] = a.PurchaseCost.ToString("F2"),
            ["SalvageValue"] = a.SalvageValue.ToString("F2"),
            ["UsefulLifeMonths"] = a.UsefulLifeMonths.ToString(),
            ["DepreciationMethod"] = a.DepreciationMethod.ToString(),
            ["AccumulatedDepreciation"] = a.AccumulatedDepreciation.ToString("F2"),
            ["NetBookValue"] = a.NetBookValue.ToString("F2"),
            ["Status"] = a.Status.ToString()
        }).ToList();
    }

    private async Task<List<Dictionary<string, string>>> ExportBankTransactionsAsync(Guid companyId, ExportRequest request)
    {
        var query = _db.BankTransactions
            .Include(t => t.BankAccount)
            .Where(t => t.CompanyId == companyId);
        if (request.FromDate.HasValue) query = query.Where(t => t.TransactionDate >= request.FromDate);
        if (request.ToDate.HasValue) query = query.Where(t => t.TransactionDate <= request.ToDate);
        var txns = await query.OrderBy(t => t.TransactionDate).ToListAsync();

        return txns.Select(t => new Dictionary<string, string>
        {
            ["TransactionDate"] = t.TransactionDate.ToString("yyyy-MM-dd"),
            ["BankAccount"] = t.BankAccount?.AccountName ?? "",
            ["BankAccountId"] = t.BankAccountId.ToString(),
            ["TransactionType"] = t.TransactionType.ToString(),
            ["Amount"] = t.Amount.ToString("F2"),
            ["BalanceAfter"] = t.BalanceAfter.ToString("F2"),
            ["Description"] = t.Description ?? "",
            ["Reference"] = t.Reference ?? ""
        }).ToList();
    }

    // ===== Payments =====

    /// <summary>
    /// รอบ 205 ทีม B (P0 · IMPORT_ETL_REVIEW §1): รับ/จ่ายชำระจากไฟล์ผ่าน <c>IDocumentService.CreatePaymentAsync</c> — ทางเดียวกับหน้าเว็บ
    /// (JE ตัดลูกหนี้/เจ้าหนี้ · ยอดธนาคาร · หัก ณ ที่จ่าย · งวด ภ.พ.36 · ด่านจ่ายเกิน · ธุรกรรมของตัวเอง) · เดิมแก้ <c>PaidAmount</c> ตรง ⇒ AR ลดใน
    /// subledger แต่ GL ไม่ขยับ · ไฟล์นำเข้า = ประวัติจากระบบเดิม ⇒ ไม่ออกใบเสร็จแยกอัตโนมัติ (ใบเสร็จออกไปแล้วในระบบเดิม)
    /// </summary>
    private async Task<RowWrite> ImportPaymentAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        if (_documents == null)
            throw new BusinessRuleException("ระบบเอกสารไม่พร้อม — บันทึกรับ/จ่ายชำระที่หน้าเอกสารแทน");
        var docNum = Val(row, "DocumentNumber") ?? throw new BusinessRuleException("ต้องระบุเลขเอกสาร (DocumentNumber)");
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.DocumentNumber == docNum)
            .Select(d => new { d.Id, d.BalanceDue })
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException($"ไม่พบเอกสาร {docNum}");

        var amount = OptAmount(row, "Amount", "จำนวนเงิน") ?? throw new BusinessRuleException("ต้องระบุจำนวนเงิน (Amount)");
        var date = ReqDate(row, "PaymentDate", "วันที่ชำระ");
        var method = PaymentMethod.Cash;
        if (Val(row, "PaymentMethod") is { } pmRaw)
        {
            var pm = ImportValueNormalizer.Enum<PaymentMethod>(pmRaw);
            if (!pm.Ok || pm.Value is not PaymentMethod pmv) throw new BusinessRuleException($"วิธีชำระ: {pm.Message}");
            method = pmv;
        }

        // ── ด่านจ่ายเกิน: ทางเข้า "นำเข้าไฟล์" ต้องเดินด่านเดียวกับเว็บ (D4-3) — ข้อความบอกทางไปต่อของไฟล์นำเข้า (ยอดผิด/แถวซ้ำ) ──
        if (Accounting.Helpers.DocumentSettlementState.WouldOverpay(doc.BalanceDue, amount))
            throw new BusinessRuleException(
                $"ยอดชำระ ({amount:N2}) เกินยอดค้างชำระ ({doc.BalanceDue:N2}) ของเอกสาร {docNum} — "
                + "ตรวจไฟล์นำเข้า (ยอดผิด หรือแถวนี้ถูกนำเข้าไปแล้ว) ก่อนนำเข้าใหม่");

        // domain service เปิดธุรกรรมเอง ⇒ ต้องไม่มีของค้างใน tracker ของผู้เรียก (เส้นเดิมบันทึกทีละหน่วยแล้ว)
        var payment = await _documents.CreatePaymentAsync(companyId, new Accounting.Models.DTOs.Document.CreatePaymentRequest(
            DocumentId: doc.Id,
            PaymentDate: date,
            Amount: amount,
            PaymentMethod: method,
            Reference: Val(row, "Reference"),
            BankAccount: Val(row, "BankAccount"),
            Notes: Val(row, "Notes"),
            IssueReceiptDocument: false), performedBy);
        return new RowWrite("Payment", payment.Id);
    }

    private async Task<List<Dictionary<string, string>>> ExportPaymentsAsync(Guid companyId, ExportRequest request)
    {
        var query = _db.Payments.Include(p => p.Document).Where(p => p.CompanyId == companyId);
        if (request.FromDate.HasValue) query = query.Where(p => p.PaymentDate >= request.FromDate);
        if (request.ToDate.HasValue) query = query.Where(p => p.PaymentDate <= request.ToDate);
        var payments = await query.OrderBy(p => p.PaymentDate).ToListAsync();

        return payments.Select(p => new Dictionary<string, string>
        {
            ["PaymentNumber"] = p.PaymentNumber,
            ["PaymentDate"] = p.PaymentDate.ToString("yyyy-MM-dd"),
            ["DocumentNumber"] = p.Document?.DocumentNumber ?? "",
            ["Amount"] = p.Amount.ToString("F2"),
            ["PaymentMethod"] = p.PaymentMethod.ToString(),
            ["Reference"] = p.Reference ?? "",
            ["BankAccount"] = p.BankAccount ?? "",
            ["Notes"] = p.Notes ?? ""
        }).ToList();
    }

    // ===== Projects =====

    private async Task<RowWrite> ImportProjectAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var code = Val(row, "Code") ?? throw new BusinessRuleException("ต้องระบุรหัสโปรเจกต์ (Code)");
        if (await _db.Projects.AnyAsync(p => p.CompanyId == companyId && !p.IsDeleted && p.Code == code))
            throw new BusinessRuleException($"รหัสโปรเจกต์ {code} ซ้ำกับที่มีอยู่ — แก้รหัสในไฟล์ หรือข้ามแถวนี้");

        var project = new Project
        {
            CompanyId = companyId,
            Code = code,
            Name = Val(row, "Name") ?? throw new BusinessRuleException("ต้องระบุชื่อโปรเจกต์ (Name)"),
            NameEn = row.GetValueOrDefault("NameEn"),
            Description = row.GetValueOrDefault("Description"),
            CustomerName = row.GetValueOrDefault("CustomerName"),
            ProjectManagerName = row.GetValueOrDefault("ProjectManagerName"),
            StartDate = ReqDate(row, "StartDate", "วันเริ่มต้น"),
            EndDate = OptDate(row, "EndDate", "วันสิ้นสุด"),
            Status = Val(row, "Status") ?? "Active",
            BudgetAmount = OptAmount(row, "BudgetAmount", "งบประมาณ") ?? 0,
            ContractAmount = OptAmount(row, "ContractAmount", "มูลค่าสัญญา") ?? 0,
            // ชุดค่าเดียวกับฟอร์ม/API (ProjectContractMethods) — เดิมเก็บข้อความอะไรก็ได้ที่อยู่ในไฟล์
            BillingMethod = Accounting.Helpers.ProjectContractMethods.NormalizeBilling(row.GetValueOrDefault("BillingMethod")),
            CreatedBy = performedBy
        };
        _db.Projects.Add(project);
        return new RowWrite("Project", project.Id);
    }

    private async Task<List<Dictionary<string, string>>> ExportProjectsAsync(Guid companyId, ExportRequest request)
    {
        var projects = await _db.Projects.Where(p => p.CompanyId == companyId).OrderBy(p => p.Code).ToListAsync();
        return projects.Select(p => new Dictionary<string, string>
        {
            ["Code"] = p.Code,
            ["Name"] = p.Name,
            ["NameEn"] = p.NameEn ?? "",
            ["CustomerName"] = p.CustomerName ?? "",
            ["ProjectManagerName"] = p.ProjectManagerName ?? "",
            ["StartDate"] = p.StartDate.ToString("yyyy-MM-dd"),
            ["EndDate"] = p.EndDate?.ToString("yyyy-MM-dd") ?? "",
            ["Status"] = p.Status,
            ["BudgetAmount"] = p.BudgetAmount.ToString("F2"),
            ["ContractAmount"] = p.ContractAmount.ToString("F2"),
            ["ActualRevenue"] = p.ActualRevenue.ToString("F2"),
            ["ActualCost"] = p.ActualCost.ToString("F2"),
            ["BilledAmount"] = p.BilledAmount.ToString("F2"),
            ["BillingMethod"] = p.BillingMethod
        }).ToList();
    }

    // ===== Employees =====

    private async Task<RowWrite> ImportEmployeeAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var code = Val(row, "EmployeeCode") ?? throw new BusinessRuleException("ต้องระบุรหัสพนักงาน (EmployeeCode)");
        if (await _db.Employees.AnyAsync(e => e.CompanyId == companyId && e.EmployeeCode == code))
            throw new BusinessRuleException($"รหัสพนักงาน {code} ซ้ำกับที่มีอยู่ — แก้รหัสในไฟล์ หรือข้ามแถวนี้");
        // รอบ 201 ทีม PR2 (ฝ่ายค้าน Q4 · แตะไฟล์ทีมอื่นให้เล็กที่สุด): เลขประกันสังคมผ่านตัวตัดสินเดียวกับหน้าพนักงาน
        // (13 หลัก · เก็บตัวเลขล้วน) — รูปไม่ถูก ⇒ แถวนี้ล้มพร้อมเหตุผล แบบเดียวกับ StartDate/BaseSalary
        var ssoNoEdit = Accounting.Helpers.EmployeeRecordEdit.SsoInsuredNumber(row.GetValueOrDefault("SocialSecurityNumber"), null);
        if (ssoNoEdit.Error != null) throw new BusinessRuleException(ssoNoEdit.Error);

        // รอบ 205 ทีม B: เลขบัตรประชาชนเก็บเป็นตัวเลขล้วน (ขีด/ช่องว่างจากไฟล์ตัดออก) — checksum ไม่ผ่าน = คำเตือนที่ศูนย์นำเข้า
        // (EmployeesImportTarget · ไม่บล็อก — บัตรต่างด้าว/เลขชั่วคราวมีจริง · คำตัดสินข้อ 150)
        var citizenRaw = Val(row, "CitizenId");
        var citizenDigits = citizenRaw == null ? null : new string(citizenRaw.Where(char.IsDigit).ToArray());

        var employee = new Employee
        {
            CompanyId = companyId,
            EmployeeCode = code,
            TitleTh = Accounting.Helpers.ThaiTitleHelper.Normalize(row.GetValueOrDefault("TitleTh")),
            FirstNameTh = Val(row, "FirstNameTh") ?? throw new BusinessRuleException("ต้องระบุชื่อ (FirstNameTh)"),
            LastNameTh = Val(row, "LastNameTh") ?? throw new BusinessRuleException("ต้องระบุนามสกุล (LastNameTh)"),
            FirstNameEn = row.GetValueOrDefault("FirstNameEn"),
            LastNameEn = row.GetValueOrDefault("LastNameEn"),
            CitizenId = string.IsNullOrEmpty(citizenDigits) ? citizenRaw : citizenDigits,
            DateOfBirth = OptDate(row, "DateOfBirth", "วันเกิด"),
            Phone = row.GetValueOrDefault("Phone"),
            Email = row.GetValueOrDefault("Email"),
            Department = row.GetValueOrDefault("Department"),
            Position = row.GetValueOrDefault("Position"),
            StartDate = ReqDate(row, "StartDate", "วันเริ่มงาน"),
            BaseSalary = OptAmount(row, "BaseSalary", "เงินเดือนพื้นฐาน") ?? throw new BusinessRuleException("ต้องระบุเงินเดือนพื้นฐาน (BaseSalary)"),
            SalaryType = Val(row, "SalaryType") ?? "Monthly",
            BankName = row.GetValueOrDefault("BankName"),
            BankAccountNumber = row.GetValueOrDefault("BankAccountNumber"),
            SocialSecurityNumber = ssoNoEdit.Value,
            CreatedBy = performedBy
        };
        _db.Employees.Add(employee);
        return new RowWrite("Employee", employee.Id);
    }

    private async Task<List<Dictionary<string, string>>> ExportEmployeesAsync(Guid companyId, ExportRequest request)
    {
        var emps = await _db.Employees.Where(e => e.CompanyId == companyId).OrderBy(e => e.EmployeeCode).ToListAsync();
        return emps.Select(e => new Dictionary<string, string>
        {
            ["EmployeeCode"] = e.EmployeeCode,
            ["TitleTh"] = e.TitleTh,
            ["FirstNameTh"] = e.FirstNameTh,
            ["LastNameTh"] = e.LastNameTh,
            ["FirstNameEn"] = e.FirstNameEn ?? "",
            ["LastNameEn"] = e.LastNameEn ?? "",
            ["CitizenId"] = e.CitizenId ?? "",
            ["DateOfBirth"] = e.DateOfBirth?.ToString("yyyy-MM-dd") ?? "",
            ["Phone"] = e.Phone ?? "",
            ["Email"] = e.Email ?? "",
            ["Department"] = e.Department ?? "",
            ["Position"] = e.Position ?? "",
            ["StartDate"] = e.StartDate.ToString("yyyy-MM-dd"),
            ["BaseSalary"] = e.BaseSalary.ToString("F2"),
            ["SalaryType"] = e.SalaryType,
            ["BankName"] = e.BankName ?? "",
            ["BankAccountNumber"] = e.BankAccountNumber ?? "",
            ["SocialSecurityNumber"] = e.SocialSecurityNumber ?? "",
            ["IsActive"] = e.IsActive.ToString()
        }).ToList();
    }

    // ===== Budgets =====
    // Rows are grouped by (BudgetName + FiscalYear). First row creates the
    // header; subsequent rows append BudgetLine entries. Re-running with the
    // same BudgetName + FiscalYear adds lines to the existing budget rather
    // than spawning duplicates.

    private async Task<RowWrite> ImportBudgetAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var name = Val(row, "BudgetName") ?? throw new BusinessRuleException("ต้องระบุชื่องบประมาณ (BudgetName)");
        var year = OptInt(row, "FiscalYear", "ปีงบประมาณ") ?? throw new BusinessRuleException("ต้องระบุปีงบประมาณ (FiscalYear)");
        // ปีจากไฟล์ไทยมักเป็น พ.ศ. (2569) — ตัวแปลงปีตัวเดียวของระบบ
        year = Accounting.Helpers.ThaiDate.NormalizeYear(year);
        var acctCode = Val(row, "AccountCode") ?? throw new BusinessRuleException("ต้องระบุรหัสบัญชี (AccountCode)");
        var account = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && !a.IsDeleted && a.AccountCode == acctCode)
            ?? throw new KeyNotFoundException($"ไม่พบบัญชี {acctCode} ในผังบัญชี");

        var budget = await _db.Budgets
            .Include(b => b.Lines)
            .FirstOrDefaultAsync(b => b.CompanyId == companyId && b.Name == name && b.FiscalYear == year);
        if (budget == null)
        {
            budget = new Budget { CompanyId = companyId, Name = name, FiscalYear = year, IsActive = true, CreatedBy = performedBy };
            _db.Budgets.Add(budget);
        }

        decimal M(string key) => OptAmount(row, key, key) ?? 0;
        // แถวใหม่ใต้ parent ที่ติดตามอยู่ — ผ่าน EfNewChild ตัวเดียว (คอลเลกชันอย่างเดียว ⇒ Modified ⇒ UPDATE 0 แถว · Add สองทาง ⇒ แถวเบิ้ลในคอลเลกชัน ยอดเบิ้ล)
        _db.AddNewChild(budget.Lines, new BudgetLine
        {
            AccountId = account.Id,
            Month1 = M("Month1"), Month2 = M("Month2"), Month3 = M("Month3"), Month4 = M("Month4"),
            Month5 = M("Month5"), Month6 = M("Month6"), Month7 = M("Month7"), Month8 = M("Month8"),
            Month9 = M("Month9"), Month10 = M("Month10"), Month11 = M("Month11"), Month12 = M("Month12"),
        });
        return new RowWrite("Budget", budget.Id);
    }

    private async Task<List<Dictionary<string, string>>> ExportBudgetsAsync(Guid companyId, ExportRequest request)
    {
        var budgets = await _db.Budgets
            .Include(b => b.Lines).ThenInclude(l => l.Account)
            .Where(b => b.CompanyId == companyId)
            .OrderBy(b => b.FiscalYear).ThenBy(b => b.Name)
            .ToListAsync();
        var rows = new List<Dictionary<string, string>>();
        foreach (var b in budgets)
            foreach (var l in b.Lines)
                rows.Add(new Dictionary<string, string>
                {
                    ["BudgetName"] = b.Name, ["FiscalYear"] = b.FiscalYear.ToString(),
                    ["AccountCode"] = l.Account.AccountCode, ["AccountName"] = l.Account.AccountName,
                    ["Month1"] = l.Month1.ToString("F2"), ["Month2"] = l.Month2.ToString("F2"),
                    ["Month3"] = l.Month3.ToString("F2"), ["Month4"] = l.Month4.ToString("F2"),
                    ["Month5"] = l.Month5.ToString("F2"), ["Month6"] = l.Month6.ToString("F2"),
                    ["Month7"] = l.Month7.ToString("F2"), ["Month8"] = l.Month8.ToString("F2"),
                    ["Month9"] = l.Month9.ToString("F2"), ["Month10"] = l.Month10.ToString("F2"),
                    ["Month11"] = l.Month11.ToString("F2"), ["Month12"] = l.Month12.ToString("F2"),
                });
        return rows;
    }

    // ===== Documents (legacy migration) =====
    // 1 row = 1 line in a document. Rows with the same DocumentNumber form one
    // document (grouped by RunLegacyRowsAsync before reaching here).

    /// <summary>
    /// รอบ 205 ทีม B (P0 · คำตัดสินข้อ 151 (1)): หนึ่งใบ = แถวที่เลขเอกสารเดียวกัน → <see cref="IDocumentImportBuilder"/> (ทีม D: จับคู่ผู้ติดต่อ + ประกอบหัว/บรรทัด)
    /// → <c>IDocumentService.CreateDocumentAsync</c> — <b>ร่าง + เลข DRAFT</b> · เลขเดิมจากไฟล์ไป <c>Reference</c> · ยอด/VAT/WHT คำนวณโดย DocumentService ·
    /// ด่านงวดปิด/§86/4 ของ service เดินครบ · เดิมเขียน <c>Document</c> ตรง ใส่เลขจากไฟล์บนร่าง และคำนวณยอดเอง (ข้ามกฎเลข DRAFT + period lock) ·
    /// นำเข้าไฟล์เดิมซ้ำ: ใบชนิดเดียวกัน ผู้ติดต่อเดียวกัน และเลขเดิมเดียวกันใน Reference ที่ยังไม่ถูกลบ ⇒ ข้าม (idempotent)
    /// </summary>
    private async Task<RowWrite> WriteDocumentGroupAsync(Guid companyId, IReadOnlyList<ImportTargetRow> rows, string performedBy,
        CancellationToken ct)
    {
        if (_documentBuilder == null || _documents == null)
            throw new BusinessRuleException("ระบบเอกสารไม่พร้อม — สร้างเอกสารที่หน้าเอกสารแทน");
        var build = await _documentBuilder.BuildAsync(companyId, "documents", rows, new ImportCommitOptions(), ct);
        if (build.Request is not { } request)
        {
            var why = build.Issues.Where(i => i.Severity == ImportIssueSeverity.Error).Select(i => i.Message).FirstOrDefault()
                ?? build.Issues.Select(i => i.Message).FirstOrDefault()
                ?? "ประกอบเอกสารจากแถวนี้ไม่ได้ — ตรวจเลขเอกสาร/ประเภท/วันที่/ผู้ติดต่อในไฟล์";
            throw new BusinessRuleException(why);
        }
        var original = string.IsNullOrWhiteSpace(build.OriginalNumber) ? request.Reference : build.OriginalNumber;
        if (!string.IsNullOrWhiteSpace(original))
        {
            var dup = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.DocumentType == request.DocumentType
                    && d.ContactId == request.ContactId && d.Reference == original)
                .Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
            if (dup != null) return new RowWrite("Document", dup, Skipped: true);
        }
        var created = await _documents.CreateDocumentAsync(companyId, request, performedBy, originModule: "Import");
        return new RowWrite("Document", created.Id);
    }

    // ===== Validation =====

    // ตัวตรวจรูปค่า (รอบ 205 ทีม B · P0) — ตัวแปลงเดียวกับตอนเขียนจริง (ImportValueNormalizer) ⇒ "ตรวจผ่าน" กับ "ลงได้" ตรงกัน ไม่ขึ้นกับ culture
    private static bool IsDate(string? v) => ImportValueNormalizer.Date(v) is { Ok: true, Value: not null };
    private static bool IsAmount(string? v) => ImportValueNormalizer.Amount(v) is { Ok: true, Value: not null };
    private static bool IsNumber(string? v) => ImportValueNormalizer.Number(v) is { Ok: true, Value: not null };
    private static bool IsInteger(string? v) => ImportValueNormalizer.Number(v) is { Ok: true, Value: decimal n } && n == decimal.Truncate(n);
    private static decimal AmountOrZero(string? v) => ImportValueNormalizer.Amount(v) is { Ok: true, Value: decimal a } ? a : 0m;

    private static List<ImportError> ValidateRow(string entityType, Dictionary<string, string> row, int rowNumber)
    {
        var errors = new List<ImportError>();

        switch (ImportExportPermissionScope.NormalizeEntityType(entityType))
        {
            case "contacts":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("Name")))
                    errors.Add(new ImportError(rowNumber, "Name", "", "จำเป็นต้องระบุชื่อ"));
                // ด่านเดียวกับตอนนำเข้าจริง (ImportContactAsync) — ถ้าตรวจคนละชุด
                // หน้า "ตรวจก่อนนำเข้า" จะบอกว่าผ่าน แล้วไปล้มตอนกดนำเข้าจริง
                var cTaxId = row.GetValueOrDefault("TaxId");
                if (!string.IsNullOrWhiteSpace(cTaxId))
                {
                    var cCheck = Accounting.Helpers.ThaiTaxIdValidator.Check(cTaxId);
                    if (!cCheck.IsValid)
                        errors.Add(new ImportError(rowNumber, "TaxId", cTaxId,
                            $"เลขประจำตัวผู้เสียภาษีไม่ถูกต้อง ({cCheck.Reason}) — แก้ในไฟล์ หรือเว้นว่างไว้ก่อน"));
                }
                var cType = row.GetValueOrDefault("ContactType");
                if (!string.IsNullOrWhiteSpace(cType)
                    && Accounting.Helpers.ContactTypeResolver.ParseDeclared(cType) == null)
                    errors.Add(new ImportError(rowNumber, "ContactType", cType,
                        "ประเภทผู้ติดต่อไม่ถูกต้อง — ใช้ Individual / JuristicPerson / GovernmentAgency "
                        + "(หรือเว้นว่างให้ระบบอ่านจากเลขผู้เสียภาษี)"));
                break;
            case "products":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("Code")))
                    errors.Add(new ImportError(rowNumber, "Code", "", "จำเป็นต้องระบุรหัสสินค้า"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("Name")))
                    errors.Add(new ImportError(rowNumber, "Name", "", "จำเป็นต้องระบุชื่อสินค้า"));
                break;
            case "chartofaccounts":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("AccountCode")))
                    errors.Add(new ImportError(rowNumber, "AccountCode", "", "จำเป็นต้องระบุรหัสบัญชี"));
                if (!Enum.TryParse<AccountType>(row.GetValueOrDefault("AccountType"), true, out _))
                    errors.Add(new ImportError(rowNumber, "AccountType", row.GetValueOrDefault("AccountType") ?? "", "ประเภทบัญชีไม่ถูกต้อง"));
                break;
            case "journalentries":
                if (!IsDate(row.GetValueOrDefault("Date")))
                    errors.Add(new ImportError(rowNumber, "Date", row.GetValueOrDefault("Date") ?? "", "วันที่ไม่ถูกต้อง"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("AccountCode")))
                    errors.Add(new ImportError(rowNumber, "AccountCode", "", "จำเป็นต้องระบุรหัสบัญชี"));
                if (AmountOrZero(row.GetValueOrDefault("DebitAmount")) == 0 && AmountOrZero(row.GetValueOrDefault("CreditAmount")) == 0)
                    errors.Add(new ImportError(rowNumber, "Amount", "", "จำเป็นต้องระบุยอดเดบิตหรือเครดิต"));
                break;
            case "stockopening":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("ProductCode")))
                    errors.Add(new ImportError(rowNumber, "ProductCode", "", "จำเป็นต้องระบุรหัสสินค้า"));
                if (!IsNumber(row.GetValueOrDefault("Quantity")))
                    errors.Add(new ImportError(rowNumber, "Quantity", row.GetValueOrDefault("Quantity") ?? "", "จำนวนไม่ถูกต้อง"));
                break;
            case "openingar":
            case "openingap":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("ContactName"))
                    && string.IsNullOrWhiteSpace(row.GetValueOrDefault("ContactTaxId")))
                    errors.Add(new ImportError(rowNumber, "ContactName", "", "ต้องระบุ ContactName หรือ ContactTaxId"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("InvoiceNumber")))
                    errors.Add(new ImportError(rowNumber, "InvoiceNumber", "", "จำเป็นต้องระบุเลขที่เอกสาร"));
                if (!IsDate(row.GetValueOrDefault("InvoiceDate")))
                    errors.Add(new ImportError(rowNumber, "InvoiceDate", row.GetValueOrDefault("InvoiceDate") ?? "", "วันที่เอกสารไม่ถูกต้อง"));
                if (AmountOrZero(row.GetValueOrDefault("Amount")) <= 0)
                    errors.Add(new ImportError(rowNumber, "Amount", row.GetValueOrDefault("Amount") ?? "", "ยอดคงค้างต้องมากกว่า 0"));
                break;
            case "stockadjustments":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("ProductCode")))
                    errors.Add(new ImportError(rowNumber, "ProductCode", "", "จำเป็นต้องระบุรหัสสินค้า"));
                var mt = (row.GetValueOrDefault("MovementType") ?? "").Trim().ToUpperInvariant();
                if (mt != "IN" && mt != "OUT" && mt != "ADJUST")
                    errors.Add(new ImportError(rowNumber, "MovementType", mt, "ต้องเป็น IN / OUT / ADJUST"));
                if (!IsNumber(row.GetValueOrDefault("Quantity")))
                    errors.Add(new ImportError(rowNumber, "Quantity", row.GetValueOrDefault("Quantity") ?? "", "จำนวนไม่ถูกต้อง"));
                if (!IsDate(row.GetValueOrDefault("AdjustmentDate")))
                    errors.Add(new ImportError(rowNumber, "AdjustmentDate", row.GetValueOrDefault("AdjustmentDate") ?? "", "วันที่ไม่ถูกต้อง"));
                break;
            case "fixedassets":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("AssetCode")))
                    errors.Add(new ImportError(rowNumber, "AssetCode", "", "จำเป็นต้องระบุรหัสสินทรัพย์"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("Name")))
                    errors.Add(new ImportError(rowNumber, "Name", "", "จำเป็นต้องระบุชื่อสินทรัพย์"));
                if (!IsDate(row.GetValueOrDefault("PurchaseDate")))
                    errors.Add(new ImportError(rowNumber, "PurchaseDate", row.GetValueOrDefault("PurchaseDate") ?? "", "วันที่ซื้อไม่ถูกต้อง"));
                if (!IsAmount(row.GetValueOrDefault("PurchaseCost")))
                    errors.Add(new ImportError(rowNumber, "PurchaseCost", row.GetValueOrDefault("PurchaseCost") ?? "", "ราคาซื้อไม่ถูกต้อง"));
                // อายุการใช้งานเว้นว่างได้ — ที่ดิน/งานระหว่างก่อสร้างไม่คิดค่าเสื่อม (FixedAssetImportMethod ตัดสิน) · มีค่าต้องเป็นจำนวนเต็มไม่ติดลบ
                var lifeRaw = row.GetValueOrDefault("UsefulLifeMonths");
                if (!string.IsNullOrWhiteSpace(lifeRaw)
                    && !(IsInteger(lifeRaw) && ImportValueNormalizer.Number(lifeRaw).Value >= 0))
                    errors.Add(new ImportError(rowNumber, "UsefulLifeMonths", lifeRaw, "อายุการใช้งานไม่ถูกต้อง"));
                break;
            case "payments":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("DocumentNumber")))
                    errors.Add(new ImportError(rowNumber, "DocumentNumber", "", "จำเป็นต้องระบุเลขเอกสาร"));
                if (!IsAmount(row.GetValueOrDefault("Amount")))
                    errors.Add(new ImportError(rowNumber, "Amount", row.GetValueOrDefault("Amount") ?? "", "จำนวนเงินไม่ถูกต้อง"));
                if (!IsDate(row.GetValueOrDefault("PaymentDate")))
                    errors.Add(new ImportError(rowNumber, "PaymentDate", row.GetValueOrDefault("PaymentDate") ?? "", "วันที่ชำระไม่ถูกต้อง"));
                if (!Enum.TryParse<PaymentMethod>(row.GetValueOrDefault("PaymentMethod"), true, out _))
                    errors.Add(new ImportError(rowNumber, "PaymentMethod", row.GetValueOrDefault("PaymentMethod") ?? "", "วิธีชำระไม่ถูกต้อง"));
                break;
            case "projects":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("Code")))
                    errors.Add(new ImportError(rowNumber, "Code", "", "จำเป็นต้องระบุรหัสโปรเจกต์"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("Name")))
                    errors.Add(new ImportError(rowNumber, "Name", "", "จำเป็นต้องระบุชื่อโปรเจกต์"));
                if (!IsDate(row.GetValueOrDefault("StartDate")))
                    errors.Add(new ImportError(rowNumber, "StartDate", row.GetValueOrDefault("StartDate") ?? "", "วันเริ่มต้นไม่ถูกต้อง"));
                break;
            case "employees":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("EmployeeCode")))
                    errors.Add(new ImportError(rowNumber, "EmployeeCode", "", "จำเป็นต้องระบุรหัสพนักงาน"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("FirstNameTh")))
                    errors.Add(new ImportError(rowNumber, "FirstNameTh", "", "จำเป็นต้องระบุชื่อ"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("LastNameTh")))
                    errors.Add(new ImportError(rowNumber, "LastNameTh", "", "จำเป็นต้องระบุนามสกุล"));
                if (!IsDate(row.GetValueOrDefault("StartDate")))
                    errors.Add(new ImportError(rowNumber, "StartDate", row.GetValueOrDefault("StartDate") ?? "", "วันเริ่มงานไม่ถูกต้อง"));
                if (!IsAmount(row.GetValueOrDefault("BaseSalary")))
                    errors.Add(new ImportError(rowNumber, "BaseSalary", row.GetValueOrDefault("BaseSalary") ?? "", "เงินเดือนไม่ถูกต้อง"));
                break;
            case "budgets":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("BudgetName")))
                    errors.Add(new ImportError(rowNumber, "BudgetName", "", "จำเป็นต้องระบุชื่องบประมาณ"));
                if (!IsInteger(row.GetValueOrDefault("FiscalYear")))
                    errors.Add(new ImportError(rowNumber, "FiscalYear", row.GetValueOrDefault("FiscalYear") ?? "", "ปีงบประมาณไม่ถูกต้อง"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("AccountCode")))
                    errors.Add(new ImportError(rowNumber, "AccountCode", "", "จำเป็นต้องระบุรหัสบัญชี"));
                break;
            case "documents":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("DocumentNumber")))
                    errors.Add(new ImportError(rowNumber, "DocumentNumber", "", "จำเป็นต้องระบุเลขเอกสาร"));
                if (!Enum.TryParse<DocumentType>(row.GetValueOrDefault("DocumentType"), true, out _))
                    errors.Add(new ImportError(rowNumber, "DocumentType", row.GetValueOrDefault("DocumentType") ?? "", "ประเภทเอกสารไม่ถูกต้อง"));
                if (!IsDate(row.GetValueOrDefault("DocumentDate")))
                    errors.Add(new ImportError(rowNumber, "DocumentDate", row.GetValueOrDefault("DocumentDate") ?? "", "วันที่เอกสารไม่ถูกต้อง"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("ContactName"))
                    && string.IsNullOrWhiteSpace(row.GetValueOrDefault("ContactTaxId")))
                    errors.Add(new ImportError(rowNumber, "ContactName", "", "ต้องระบุชื่อหรือเลขผู้เสียภาษีของผู้ติดต่อ"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("LineDescription")))
                    errors.Add(new ImportError(rowNumber, "LineDescription", "", "จำเป็นต้องระบุรายละเอียดของรายการ"));
                break;
        }

        return errors;
    }

    /// <summary>ไฟล์ CSV ที่ส่งออก — ทุกช่องผ่าน <see cref="Accounting.Helpers.CsvFieldSafety"/>
    /// ตัวเดียว (เดิมตัวหนีในไฟล์นี้ดูแค่ <c>,</c> <c>"</c> <c>\n</c> ⇒ ชื่อคู่ค้า/
    /// คำอธิบายที่ขึ้นต้นด้วย <c>=</c> <c>+</c> <c>-</c> <c>@</c> กลายเป็น**สูตร**
    /// บนเครื่องนักบัญชีที่เปิดไฟล์ และ <c>\r</c> เดี่ยวทำให้แถวแตกกลางคัน)</summary>
    private static string BuildCsv(List<Dictionary<string, string>> data)
    {
        if (data.Count == 0) return "";

        var sb = new StringBuilder();
        var headers = data[0].Keys.ToList();
        sb.AppendLine(Accounting.Helpers.CsvFieldSafety.Row(headers));

        foreach (var row in data)
            sb.AppendLine(Accounting.Helpers.CsvFieldSafety.Row(
                headers.Select(h => row.GetValueOrDefault(h) ?? "")));

        return sb.ToString();
    }

    // ===== Smart Import - AI Column Matching =====

    public async Task<SmartImportSessionResponse> UploadAndAnalyzeAsync(Guid companyId, SmartImportUploadRequest request, string performedBy)
    {
        var templateFields = GetTemplateFields(request.EntityType);
        if (templateFields == null)
            throw new InvalidOperationException($"ไม่รองรับการนำเข้า {request.EntityType}");

        // สร้าง session
        var session = new SmartImportSession
        {
            CompanyId = companyId,
            EntityType = request.EntityType,
            FileName = request.FileName,
            FileFormat = request.FileFormat,
            HasHeaderRow = request.HasHeaderRow,
            TotalRows = request.HasHeaderRow ? request.RawData.Count - 1 : request.RawData.Count,
            Status = ImportSessionStatus.Analyzing,
            RawDataJson = JsonSerializer.Serialize(request.RawData),
            CreatedBy = performedBy
        };
        _db.SmartImportSessions.Add(session);

        // ดึง header row
        var headers = request.HasHeaderRow && request.RawData.Count > 0
            ? request.RawData[0]
            : Enumerable.Range(0, request.RawData.Count > 0 ? request.RawData[0].Count : 0)
                .Select(i => $"Column{i + 1}").ToList();

        // ดึง sample data (สูงสุด 5 แถว)
        var dataRows = request.HasHeaderRow ? request.RawData.Skip(1).ToList() : request.RawData;
        var sampleRows = dataRows.Take(5).ToList();

        // รอบ 205 ทีม B: จับคู่คอลัมน์ผ่านตัวจับคู่ตัวเดียวของระบบ (ทีม A · ImportColumnMatchService) — เดิมมีตัวจับคู่ชุดที่สองที่นี่
        // (AnalyzeColumnMatch + AI ที่ทิ้ง FeedbackId — ไม่เคยเรียนจากผู้ใช้ · ป้าย "AiMatched" บนการจับคู่ด้วยกฎ)
        var columns = headers.Select((h, i) => new ImportColumnInput(i, h ?? "", sampleRows
                .Where(r => i < r.Count).Select(r => r[i]).Where(v => !string.IsNullOrWhiteSpace(v)).Take(5).ToList()))
            .ToList();
        ImportColumnPlan plan;
        if (_columnMatch != null)
        {
            var outcome = await _columnMatch.MapAsync(companyId, request.EntityType, columns,
                new ImportColumnMatchOptions(BatchId: session.Id, AllowAi: true));
            plan = outcome.Plan;
            session.ColumnMatchAiFeedbackId = outcome.AiFeedbackId;
            session.ColumnMatchUsedAi = outcome.UsedAi;
            session.HeaderSignature = outcome.HeaderSignature;
            session.MappingTemplateId = outcome.TemplateId;
        }
        else
            plan = ImportColumnMapper.Resolve(new ImportColumnMapRequest(request.EntityType, ImportSchemas.For(request.EntityType)!, columns));
        session.SourceSystem ??= plan.SourceProfile?.Profile.SourceSystem;

        var mappings = plan.Columns.Select(c => ToColumnMappingRow(session.Id, c)).ToList();
        var unmappedCount = mappings.Count(m => m.MatchType == ColumnMatchType.Unmapped
            || m.Confidence == ColumnMatchConfidence.Low || m.Confidence == ColumnMatchConfidence.None);

        _db.SmartImportColumnMappings.AddRange(mappings);

        // อัพเดตสถานะ session
        session.Status = unmappedCount > 0 ? ImportSessionStatus.MappingRequired : ImportSessionStatus.MappingCompleted;
        await _db.SaveChangesAsync();

        return MapToSessionResponse(session, mappings, sampleRows);
    }

    /// <summary>
    /// แถวตารางจับคู่ของ Smart Import (SmartImportColumnMappings) จากแผนของตัวจับคู่ — <b>ตัวเดียว</b>ที่ทั้ง Smart Import และ pipeline ศูนย์นำเข้าใช้ ·
    /// ป้าย MatchType ซื่อสัตย์: <c>AiMatched</c> เฉพาะคำตอบที่ AI ตอบจริง · ผู้ใช้เลือก = Manual · ชื่อตรง/แม่แบบ = Exact · คำพ้อง/เรียนรู้/รูปข้อมูล/นักเรียน = Heuristic
    /// </summary>
    internal static SmartImportColumnMapping ToColumnMappingRow(Guid sessionId, ImportColumnPlanDto c) => new()
    {
        SessionId = sessionId,
        SourceIndex = c.SourceIndex,
        SourceHeader = c.SourceHeader.Length <= 255 ? c.SourceHeader : c.SourceHeader[..255],
        TargetField = c.TargetField == null ? null : c.TargetField.Length <= 100 ? c.TargetField : c.TargetField[..100],
        MatchType = c.TargetField == null ? ColumnMatchType.Unmapped
            : c.Source == ImportMapSource.User ? ColumnMatchType.ManualMatch
            : c.Source == ImportMapSource.Ai && c.UsedAi ? ColumnMatchType.AiMatched
            : c.Source is ImportMapSource.Exact or ImportMapSource.Template ? ColumnMatchType.ExactMatch
            : ColumnMatchType.HeuristicMatched,
        Confidence = c.TargetField == null ? ColumnMatchConfidence.None
            : c.Confidence >= 0.9m ? ColumnMatchConfidence.High : c.Confidence >= 0.7m ? ColumnMatchConfidence.Medium
            : c.Confidence >= 0.5m ? ColumnMatchConfidence.Low : ColumnMatchConfidence.None,
        ConfidenceScore = (double)c.Confidence,
        SampleValuesJson = JsonSerializer.Serialize(c.SampleValues),
        SuggestionsJson = JsonSerializer.Serialize(c.Alternatives.Select(a =>
            new SuggestedMappingDto(a.TargetField, a.TargetLabel, (double)a.Score, a.Reason)).ToList()),
    };

    /// <summary>ปิดลูป ImportColumnMatch ของ Smart Import (กฎเหล็ก #1) — ผู้ใช้แก้ (SubmitManualMapping) / ยืนยัน (ConfirmAndImport) การจับคู่ ⇒
    /// <see cref="IImportColumnMatchService.RecordConfirmedAsync"/> ด้วยการจับคู่สุดท้าย · ไม่มีแถว feedback = ไม่มีอะไรต้องปิด</summary>
    private async Task RecordSmartColumnChoiceAsync(Guid companyId, SmartImportSession session)
    {
        if (_columnMatch == null || session.ColumnMatchAiFeedbackId is not Guid fb) return;
        var final = session.ColumnMappings.OrderBy(m => m.SourceIndex).Select(m => new ImportColumnPlanDto(m.SourceIndex, m.SourceHeader,
            string.IsNullOrWhiteSpace(m.TargetField) ? null : m.TargetField, null,
            m.MatchType == ColumnMatchType.ManualMatch ? ImportMapSource.User : ImportMapSource.None, "", (decimal)m.ConfidenceScore, false,
            Array.Empty<string>(), Array.Empty<ImportColumnCandidateDto>())).ToList();
        await _columnMatch.RecordConfirmedAsync(companyId, fb, final);
    }

    /// <summary>One-shot review of the staged data BEFORE commit.
    /// Caller (controller) invokes after the operator finishes column
    /// mapping. Returns normalizations + dup suggestions + quality
    /// flags + field validation + batch patterns in a single payload
    /// the frontend can render as a review screen.
    /// <para>รอบ 205 ทีม B (กฎเหล็ก #1 kill-switch): ไม่มีตัวช่วย AI ลงทะเบียน ⇒ ใช้ตัวตรวจ rule-based ตัวเดียวกับที่ augmenter ใช้ตอน AI ล่ม
    /// (<see cref="ImportReviewHeuristics"/>) — เดิมคืนผลว่าง ⇒ หน้าเว็บแยก "ไม่มีปัญหา" กับ "ไม่ได้ตรวจ" ไม่ออก · ป้าย UsedAi = false</para></summary>
    public async Task<ImportAiReviewResponse> AiReviewSessionAsync(Guid companyId, Guid sessionId)
    {
        var session = await _db.SmartImportSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ Import Session");

        var mappings = await _db.SmartImportColumnMappings.AsNoTracking()
            .Where(m => m.SessionId == sessionId && !string.IsNullOrEmpty(m.TargetField))
            .OrderBy(m => m.SourceIndex)
            .ToListAsync();

        var templateFields = GetTemplateFields(session.EntityType)
            ?? throw new BusinessRuleException($"ไม่รองรับการนำเข้า {session.EntityType}");

        // Materialize the staged file into target-field-keyed rows so
        // the reviewer sees domain field names (Name / TaxId / Email …) instead
        // of the raw source headers. 50-row cap keeps token usage bounded
        // for big imports — the AI still spots batch-wide patterns from
        // the sample + sees the totalRows count in the prompt.
        var rawRows = JsonSerializer.Deserialize<List<List<string>>>(session.RawDataJson ?? "[]") ?? new();
        var dataRows = session.HasHeaderRow ? rawRows.Skip(1).ToList() : rawRows;
        var sampleSize = Math.Min(50, dataRows.Count);
        var sampleRows = new List<Dictionary<string, string?>>(sampleSize);
        for (int i = 0; i < sampleSize; i++)
        {
            var row = dataRows[i];
            var dict = new Dictionary<string, string?>();
            foreach (var m in mappings)
            {
                if (m.SourceIndex >= row.Count) continue;
                var v = row[m.SourceIndex];
                dict[m.TargetField!] = string.IsNullOrWhiteSpace(v) ? null : v;
            }
            sampleRows.Add(dict);
        }

        var existingSlice = await LoadExistingEntitySliceAsync(companyId, session.EntityType);
        var targets = templateFields.Select(f => new ImportPrompts.TargetField(
            f.FieldName, f.DisplayName, f.DataType, f.IsRequired, f.Description)).ToList();
        var mappedOrder = mappings.Select(m => m.TargetField!).ToList();

        var review = _ai != null
            ? await _ai.ReviewImportAsync(companyId, sessionId, session.EntityType,
                targets, mappedOrder, sampleRows, existingSlice,
                totalRowCount: dataRows.Count)   // จำนวนแถวจริงของไฟล์ ไม่ใช่ sample 50
            : ImportReviewHeuristics.Review(session.EntityType, targets, mappedOrder, sampleRows, existingSlice);

        return new ImportAiReviewResponse(sessionId, review.UsedAi, review.Summary, review.OverallQualityScore,
            review.Normalizations.Select(n => new ImportAiNormalizationDto(n.RowIndex, n.Field, n.Original, n.Normalized, n.Reason)).ToList(),
            review.FuzzyDuplicates.Select(d => new ImportAiFuzzyDuplicateDto(d.RowIndex, d.IncomingKey, d.ExistingId, d.ExistingLabel, d.Similarity, d.Reasoning)).ToList(),
            review.QualityFlags.Select(q => new ImportAiQualityFlagDto(q.RowIndex, q.Severity, q.Message)).ToList(),
            review.FieldValidations.Select(v => new ImportAiFieldValidationDto(v.RowIndex, v.Field, v.Value, v.Issue, v.SuggestedFix)).ToList(),
            review.BatchPatterns.ToList());
    }

    /// <summary>Load a representative slice of existing entities for
    /// the AI to fuzzy-match incoming rows against. Capped at 100 rows
    /// per entity type — enough context for "บจก.ABC vs บริษัท เอบีซี
    /// จำกัด" recognition without blowing the token budget on companies
    /// with 10k+ contacts.</summary>
    private async Task<List<ImportPrompts.ExistingEntityRef>> LoadExistingEntitySliceAsync(
        Guid companyId, string entityType)
    {
        const int Cap = 100;
        var et = entityType.ToLowerInvariant();
        if (et == "contacts")
        {
            return await _db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted)
                .OrderByDescending(c => c.UpdatedAt)
                .Take(Cap)
                .Select(c => new ImportPrompts.ExistingEntityRef(
                    c.Id.ToString(), c.Name, c.TaxId))
                .ToListAsync();
        }
        if (et == "products")
        {
            return await _db.Products.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted)
                .OrderByDescending(p => p.UpdatedAt)
                .Take(Cap)
                .Select(p => new ImportPrompts.ExistingEntityRef(
                    p.Id.ToString(), p.Name, p.Code))
                .ToListAsync();
        }
        if (et == "chartofaccounts" || et == "chart-of-accounts")
        {
            return await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && !a.IsDeleted)
                .OrderBy(a => a.AccountCode)
                .Take(Cap)
                .Select(a => new ImportPrompts.ExistingEntityRef(
                    a.Id.ToString(), a.AccountName, a.AccountCode))
                .ToListAsync();
        }
        return new List<ImportPrompts.ExistingEntityRef>();
    }

    /// <summary>Materialise a Smart Import session into the same flat
    /// {fieldName: value} row shape the standard ImportRequest uses, then
    /// run PreviewConflictsAsync on it. Lets the UI show the same
    /// Skip/Merge/Overwrite picker BEFORE calling /smart-import/confirm.</summary>
    public async Task<ConflictPreviewResponse> PreviewSmartConflictsAsync(Guid companyId, Guid sessionId)
    {
        var session = await _db.SmartImportSessions
            .Include(s => s.ColumnMappings)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ Import Session");

        var rawData = !string.IsNullOrEmpty(session.RawDataJson)
            ? JsonSerializer.Deserialize<List<List<string>>>(session.RawDataJson) ?? new()
            : new List<List<string>>();
        var dataRows = session.HasHeaderRow ? rawData.Skip(1).ToList() : rawData;
        var activeMappings = session.ColumnMappings
            .Where(m => !string.IsNullOrEmpty(m.TargetField))
            .OrderBy(m => m.SourceIndex)
            .ToList();

        var materialised = new List<Dictionary<string, string>>(dataRows.Count);
        foreach (var rowData in dataRows)
        {
            var mappedRow = new Dictionary<string, string>();
            foreach (var mapping in activeMappings)
            {
                if (mapping.SourceIndex < rowData.Count && !string.IsNullOrEmpty(mapping.TargetField))
                    mappedRow[mapping.TargetField] = rowData[mapping.SourceIndex]?.Trim() ?? "";
            }
            materialised.Add(mappedRow);
        }

        var fakeRequest = new ImportRequest(session.EntityType, "csv", true, null, materialised);
        return await PreviewConflictsAsync(companyId, fakeRequest);
    }

    public async Task<SmartImportSessionResponse> GetSessionAsync(Guid companyId, Guid sessionId)
    {
        var session = await _db.SmartImportSessions
            .Include(s => s.ColumnMappings)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ Import Session");

        var rawData = !string.IsNullOrEmpty(session.RawDataJson)
            ? JsonSerializer.Deserialize<List<List<string>>>(session.RawDataJson) ?? new()
            : new List<List<string>>();

        var dataRows = session.HasHeaderRow ? rawData.Skip(1).Take(5).ToList() : rawData.Take(5).ToList();

        return MapToSessionResponse(session, session.ColumnMappings.OrderBy(m => m.SourceIndex).ToList(), dataRows);
    }

    public async Task<SmartImportSessionResponse> SubmitManualMappingAsync(Guid companyId, ManualMappingRequest request, string performedBy)
    {
        var session = await _db.SmartImportSessions
            .Include(s => s.ColumnMappings)
            .FirstOrDefaultAsync(s => s.Id == request.SessionId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ Import Session");

        if (session.Status != ImportSessionStatus.MappingRequired &&
            session.Status != ImportSessionStatus.MappingCompleted)
            throw new InvalidOperationException("Session ไม่อยู่ในสถานะที่สามารถแก้ไข Mapping ได้");

        var templateFields = GetTemplateFields(session.EntityType)!;

        foreach (var entry in request.Mappings)
        {
            var mapping = session.ColumnMappings.FirstOrDefault(m => m.SourceIndex == entry.SourceIndex);
            if (mapping == null) continue;

            if (string.IsNullOrEmpty(entry.TargetField))
            {
                mapping.TargetField = null;
                mapping.MatchType = ColumnMatchType.Unmapped;
                mapping.Confidence = ColumnMatchConfidence.None;
                mapping.ConfidenceScore = 0;
            }
            else
            {
                var field = templateFields.FirstOrDefault(f =>
                    f.FieldName.Equals(entry.TargetField, StringComparison.OrdinalIgnoreCase));
                if (field == null)
                    throw new InvalidOperationException($"Target field '{entry.TargetField}' ไม่ถูกต้องสำหรับ {session.EntityType}");

                mapping.TargetField = field.FieldName;
                mapping.MatchType = ColumnMatchType.ManualMatch;
                mapping.Confidence = ColumnMatchConfidence.High;
                mapping.ConfidenceScore = 1.0;
            }
        }

        // ตรวจสอบว่ายังมี required field ที่ยังไม่ได้ map หรือไม่
        var mappedTargets = session.ColumnMappings
            .Where(m => !string.IsNullOrEmpty(m.TargetField))
            .Select(m => m.TargetField!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingRequired = templateFields
            .Where(f => f.IsRequired && !mappedTargets.Contains(f.FieldName))
            .ToList();

        session.Status = missingRequired.Count > 0
            ? ImportSessionStatus.MappingRequired
            : ImportSessionStatus.MappingCompleted;
        session.UpdatedBy = performedBy;

        await _db.SaveChangesAsync();
        await RecordSmartColumnChoiceAsync(companyId, session);

        var rawData = !string.IsNullOrEmpty(session.RawDataJson)
            ? JsonSerializer.Deserialize<List<List<string>>>(session.RawDataJson) ?? new()
            : new List<List<string>>();
        var dataRows = session.HasHeaderRow ? rawData.Skip(1).Take(5).ToList() : rawData.Take(5).ToList();

        return MapToSessionResponse(session, session.ColumnMappings.OrderBy(m => m.SourceIndex).ToList(), dataRows);
    }

    public async Task<SmartImportResult> ConfirmAndImportAsync(Guid companyId, SmartImportConfirmRequest request, string performedBy)
    {
        var session = await _db.SmartImportSessions
            .Include(s => s.ColumnMappings)
            .FirstOrDefaultAsync(s => s.Id == request.SessionId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ Import Session");

        // รอบ 205 ทีม B (P0): ชุดที่ลงได้บางแถว/ล้ม ลงซ้ำได้ — เฉพาะแถวที่ล้มรอบก่อน (ข้อมูลดิบถูกเก็บไว้จนกว่าจะลงครบ · writer idempotent)
        var retry = session.Status is ImportSessionStatus.PartiallyCompleted or ImportSessionStatus.Failed;
        if (!retry && session.Status != ImportSessionStatus.MappingCompleted && session.Status != ImportSessionStatus.Ready)
            throw new BusinessRuleException("Session ยังไม่พร้อม Import — กรุณาตรวจสอบ Column Mapping");
        if (retry && string.IsNullOrEmpty(session.RawDataJson))
            throw new BusinessRuleException("ข้อมูลดิบของชุดนี้ถูกล้างแล้ว — อัปโหลดไฟล์ใหม่เพื่อนำเข้าแถวที่ล้ม");
        HashSet<int>? retryRows = null;
        if (retry)
            retryRows = (JsonSerializer.Deserialize<List<ImportError>>(session.ErrorsJson ?? "[]") ?? new())
                .Select(e => e.RowNumber).ToHashSet();

        session.Status = ImportSessionStatus.Importing;
        await _db.SaveChangesAsync();
        if (!retry) await RecordSmartColumnChoiceAsync(companyId, session);

        var rawData = !string.IsNullOrEmpty(session.RawDataJson)
            ? JsonSerializer.Deserialize<List<List<string>>>(session.RawDataJson) ?? new()
            : new List<List<string>>();

        var dataRows = session.HasHeaderRow ? rawData.Skip(1).ToList() : rawData;
        var activeMappings = session.ColumnMappings
            .Where(m => !string.IsNullOrEmpty(m.TargetField))
            .OrderBy(m => m.SourceIndex)
            .ToList();

        var rows = new List<(int RowNo, Dictionary<string, string> Row)>();
        for (int i = 0; i < dataRows.Count; i++)
        {
            if (retryRows != null && !retryRows.Contains(i + 1)) continue;
            var rowData = dataRows[i];
            var mappedRow = new Dictionary<string, string>();
            foreach (var mapping in activeMappings)
            {
                if (mapping.SourceIndex < rowData.Count && !string.IsNullOrEmpty(mapping.TargetField))
                    mappedRow[mapping.TargetField] = rowData[mapping.SourceIndex]?.Trim() ?? "";
            }
            rows.Add((i + 1, mappedRow));
        }

        // รอบ 205 ทีม B (P0): DateFormat/DecimalSeparator มีผลจริง (เดิมรับแล้วไม่ใช้ — silent no-op)
        var run = await RunLegacyRowsAsync(companyId, session.EntityType, rows, request.Resolutions,
            request.DefaultConflictAction, performedBy, DateOrderFromFormat(request.DateFormat),
            decimalComma: (request.DecimalSeparator ?? "").Trim() == ",");

        // แถวที่ล้มล้าง change tracker ไปแล้ว ⇒ อ่าน session ใหม่ก่อนประทับผล
        var done = await _db.SmartImportSessions
            .FirstAsync(s => s.Id == request.SessionId && s.CompanyId == companyId);
        done.SuccessCount = (retry ? done.SuccessCount : 0) + run.Success;
        done.ErrorCount = run.Errors.Count;
        done.SkippedCount = (retry ? done.SkippedCount : 0) + run.Skipped;
        // สถานะซื่อสัตย์: ลงได้บางแถว = PartiallyCompleted (เดิม Failed ทั้งที่แถวอื่นลงไปแล้ว)
        done.Status = run.Errors.Count == 0 ? ImportSessionStatus.Completed
            : done.SuccessCount > 0 ? ImportSessionStatus.PartiallyCompleted
            : ImportSessionStatus.Failed;
        done.CompletedAt = DateTime.UtcNow;
        done.ErrorsJson = run.Errors.Count > 0 ? JsonSerializer.Serialize(run.Errors) : null;
        done.UpdatedBy = performedBy;

        // เก็บข้อมูลดิบไว้ให้ลงแถวที่ล้มซ้ำได้ — ล้างเมื่อลงครบแล้วเท่านั้น (PDPA: ไม่เก็บเกินจำเป็น · เดิมล้างเสมอ ⇒ ลองใหม่ไม่ได้)
        if (run.Errors.Count == 0) done.RawDataJson = null;

        await _db.SaveChangesAsync();

        return new SmartImportResult(done.Id, done.EntityType, done.Status.ToString(),
            rows.Count, run.Success, run.Errors.Count, run.Skipped, run.Errors, DateTime.UtcNow);
    }

    /// <summary>รูปแบบวันที่ที่ผู้ใช้เลือก (Smart Import) → ลำดับวัน/เดือน — "MM/dd/yyyy" = เดือนก่อน · "dd/MM/yyyy" = วันก่อน · ISO/ว่าง = อัตโนมัติ (ไทย)</summary>
    internal static ImportDateOrder DateOrderFromFormat(string? format)
    {
        var f = (format ?? "").Trim();
        if (f.Length == 0 || f.StartsWith("y", StringComparison.OrdinalIgnoreCase)) return ImportDateOrder.Auto;
        if (f.StartsWith("M", StringComparison.Ordinal)) return ImportDateOrder.MonthDayYear;
        if (f.StartsWith("d", StringComparison.OrdinalIgnoreCase)) return ImportDateOrder.DayMonthYear;
        return ImportDateOrder.Auto;
    }

    // ===== Template Download =====

    public Task<ImportTemplateDownloadResponse> DownloadTemplateAsync(string entityType, string format)
    {
        var fields = GetTemplateFields(entityType)
            ?? throw new InvalidOperationException($"ไม่รองรับ template สำหรับ {entityType}");

        var csv = new StringBuilder();

        // Header row
        csv.AppendLine(string.Join(",", fields.Select(f => Accounting.Helpers.CsvFieldSafety.Escape(f.FieldName))));

        // Sample data row
        var sampleRow = fields.Select(f => Accounting.Helpers.CsvFieldSafety.Escape(GetSampleValue(entityType, f))).ToList();
        csv.AppendLine(string.Join(",", sampleRow));

        // ส่ง description row (comment)
        var descRow = fields.Select(f => Accounting.Helpers.CsvFieldSafety.Escape(
            $"{f.DisplayName}{(f.IsRequired ? " *จำเป็น" : "")}{(f.Description != null ? $" ({f.Description})" : "")}" +
            $"{(f.AllowedValues != null ? $" [{string.Join("/", f.AllowedValues)}]" : "")}")).ToList();
        csv.AppendLine(string.Join(",", descRow));

        var fileName = $"import_template_{entityType}.csv";
        var fileData = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();

        return Task.FromResult(new ImportTemplateDownloadResponse(
            entityType, fileName, "text/csv", fileData, fields));
    }

    public Task<List<ImportableEntityInfo>> GetImportableEntitiesAsync()
    {
        var entities = new List<ImportableEntityInfo>
        {
            new("contacts", "ผู้ติดต่อ", "นำเข้ารายชื่อลูกค้าและผู้ขาย",
                GetTemplateFields("contacts")!),
            new("products", "สินค้า/บริการ", "นำเข้ารายการสินค้าและบริการ",
                GetTemplateFields("products")!),
            new("chartofaccounts", "ผังบัญชี", "นำเข้าผังบัญชี (Chart of Accounts)",
                GetTemplateFields("chartofaccounts")!),
            new("banktransactions", "รายการธนาคาร", "นำเข้ารายการเคลื่อนไหวบัญชีธนาคาร",
                GetTemplateFields("banktransactions")!),
            new("journal-entries", "บันทึกบัญชี", "นำเข้ารายการบันทึกบัญชี (Journal Entries)",
                GetTemplateFields("journal-entries")!),
            new("stock-opening", "สต็อกยกมา (Opening Stock)",
                "นำเข้ายอดสต็อกตั้งต้น — ใช้ตอน migrate จากระบบเดิม",
                GetTemplateFields("stock-opening")!),
            new("opening-ar", "ลูกหนี้ยกมา (Opening AR)",
                "นำเข้าใบแจ้งหนี้ค้างรับรายตัว — เข้ารายงานอายุลูกหนี้ + บัญชีแยกลูกหนี้ (ไม่ลง GL ซ้ำ)",
                GetTemplateFields("opening-ar")!),
            new("opening-ap", "เจ้าหนี้ยกมา (Opening AP)",
                "นำเข้าใบแจ้งหนี้ค้างจ่ายรายตัว — เข้ารายงานอายุเจ้าหนี้ + บัญชีแยกเจ้าหนี้ (ไม่ลง GL ซ้ำ)",
                GetTemplateFields("opening-ap")!),
            new("stock-adjustments", "ปรับสต็อก (Stock Adjustments)",
                "เพิ่ม/ลดสต็อก พร้อมเหตุผล (เสียหาย, ปรับนับ, ฯลฯ)",
                GetTemplateFields("stock-adjustments")!),
            new("fixed-assets", "สินทรัพย์ถาวร (Fixed Assets)",
                "นำเข้าทะเบียนสินทรัพย์ถาวร พร้อมข้อมูลค่าเสื่อม",
                GetTemplateFields("fixed-assets")!),
            new("payments", "การชำระเงิน (Payments)",
                "นำเข้ารายการรับ-จ่ายชำระ ผูกกับเลขเอกสาร",
                GetTemplateFields("payments")!),
            new("projects", "โปรเจกต์ (Projects)",
                "นำเข้าทะเบียนโปรเจกต์ พร้อมงบประมาณ + วันเริ่ม-สิ้นสุด",
                GetTemplateFields("projects")!),
            new("employees", "พนักงาน (Employees)",
                "นำเข้าทะเบียนพนักงาน + เงินเดือนพื้นฐาน",
                GetTemplateFields("employees")!),
            new("budgets", "งบประมาณ (Budgets)",
                "นำเข้างบประมาณรายเดือน (Month1–Month12) ต่อบัญชี",
                GetTemplateFields("budgets")!),
            new("documents", "เอกสาร (Documents)",
                "นำเข้าเอกสารเก่า (Invoice / Receipt / TaxInvoice / ฯลฯ) — 1 แถว = 1 line ของเอกสาร, group ด้วย DocumentNumber",
                GetTemplateFields("documents")!)
        };

        return Task.FromResult(entities);
    }

    /// <summary>
    /// field ของชนิดข้อมูล (รูปเดิม <see cref="ImportField"/>) — รอบ 205 ทีม B: มอบให้แคตตาล็อกตัวเดียว <see cref="ImportSchemas.For"/> (ทีม A ย้ายเนื้อจากที่นี่ไปครบ
    /// ทุกชนิด · ชื่อ field สะกดเดิม) แล้วแปลงชนิดกลับเป็น <c>DataType</c> เดิม · เดิมเป็นสำเนาที่สอง (journal-entries ไม่มี ⇒ null แล้ว <c>!</c> ทับ)
    /// </summary>
    internal static List<ImportField>? GetTemplateFields(string entityType)
        => ImportSchemas.For(entityType)?.Select(f => new ImportField(f.Field, f.Label, LegacyDataType(f.Type), f.Required, f.Description,
            f.AllowedValues?.ToList())).ToList();

    /// <summary>ชนิดละเอียด (<see cref="ImportFieldType"/>) → ชนิดเดิมของ <c>ImportField.DataType</c> ("date"/"bool"/"enum"/"decimal"/"string")</summary>
    internal static string LegacyDataType(ImportFieldType type) => type switch
    {
        ImportFieldType.Date => "date",
        ImportFieldType.Flag => "bool",
        ImportFieldType.Enum => "enum",
        ImportFieldType.Amount or ImportFieldType.Decimal or ImportFieldType.Integer or ImportFieldType.VatRate => "decimal",
        _ => "string",
    };

    /// <summary>ค่าตัวอย่างในไฟล์แม่แบบ — จาก <see cref="ImportFieldDto.Example"/> ของแคตตาล็อกเดียวกัน (เดิมตาราง switch ซ้ำ)</summary>
    private static string GetSampleValue(string entityType, ImportField field)
        => ImportSchemas.For(entityType)?.FirstOrDefault(f => string.Equals(f.Field, field.FieldName, StringComparison.OrdinalIgnoreCase))?.Example ?? "";

    private static SmartImportSessionResponse MapToSessionResponse(
        SmartImportSession session, IList<SmartImportColumnMapping> mappings, List<List<string>> previewData)
    {
        var unmappedCount = mappings.Count(m =>
            m.MatchType == ColumnMatchType.Unmapped ||
            m.Confidence == ColumnMatchConfidence.Low ||
            m.Confidence == ColumnMatchConfidence.None);

        var columnDtos = mappings.Select(m =>
        {
            var sampleValues = !string.IsNullOrEmpty(m.SampleValuesJson)
                ? JsonSerializer.Deserialize<List<string>>(m.SampleValuesJson) ?? new()
                : new List<string>();

            var suggestions = !string.IsNullOrEmpty(m.SuggestionsJson)
                ? JsonSerializer.Deserialize<List<SuggestedMappingDto>>(m.SuggestionsJson) ?? new()
                : new List<SuggestedMappingDto>();

            var targetDisplay = "";
            if (!string.IsNullOrEmpty(m.TargetField))
            {
                var fields = GetTemplateFields(session.EntityType);
                targetDisplay = fields?.FirstOrDefault(f =>
                    f.FieldName.Equals(m.TargetField, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? m.TargetField;
            }

            return new SmartColumnMappingDto(
                m.SourceIndex,
                m.SourceHeader,
                m.TargetField,
                targetDisplay,
                m.MatchType.ToString(),
                m.Confidence.ToString(),
                m.ConfidenceScore,
                sampleValues,
                suggestions);
        }).ToList();

        return new SmartImportSessionResponse(
            session.Id,
            session.EntityType,
            session.Status.ToString(),
            columnDtos,
            session.TotalRows,
            unmappedCount,
            unmappedCount > 0,
            previewData);
    }
}
