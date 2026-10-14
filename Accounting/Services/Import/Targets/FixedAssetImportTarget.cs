using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using static Accounting.Services.Import.Targets.ImportTargetValues;

namespace Accounting.Services.Import.Targets;

/// <summary>
/// **ปลายทาง "ทะเบียนสินทรัพย์"** ของศูนย์นำเข้า (รอบ 205 ทีม D · IMPORT_ETL_REVIEW §1 แถว 11 · §2.9)
///
/// <para>ลงผ่าน <see cref="IFixedAssetService.ImportAsync"/> เท่านั้น — วิธีคิดค่าเสื่อมตัดสินด้วย <see cref="FixedAssetImportMethod"/> ตัวเดียว
/// (ที่ดิน/งานระหว่างก่อสร้าง = ไม่คิดค่าเสื่อม · พ.ร.ฎ.145) · เดิม owner ที่สอง <c>ImportExportService.ImportFixedAssetAsync</c> ข้ามตัวตัดสินนี้
/// ⇒ ที่ดินผ่านทาง generic ถูกคิดค่าเสื่อม (ถดถอย E-03 รอบ 200)</para>
/// <para>ตรวจก่อน: วิธีคิด/หมวดขัดกัน = บล็อกพร้อมทางแก้ (ตัวตัดสินเดียวกับตอนลง) · รหัสซ้ำของเดิม = คำเตือน + ตั้ง "ข้าม" · ค่าเสื่อมสะสมยกมา
/// ยังไม่รองรับ (รอตัวอย่าง §4 ข้อ 7) = คำเตือนพร้อมทาง "ลงค่าเสื่อมสะสมผ่านงบทดลองยกมา"</para>
/// <para>ย้อน = ลบ (soft) ผ่าน <see cref="IFixedAssetService.DeleteAsync"/> เฉพาะที่ยังไม่ลงค่าเสื่อม · ลงแล้ว = บล็อก + ทาง "จำหน่าย/ตัดจำหน่าย"</para>
/// </summary>
public class FixedAssetImportTarget : IImportTarget
{
    public const string Key = "fixed-assets";
    public const string CommittedEntity = "FixedAsset";

    private readonly AccountingDbContext _db;
    private readonly IFixedAssetService _assets;

    public FixedAssetImportTarget(AccountingDbContext db, IFixedAssetService assets)
    {
        _db = db;
        _assets = assets;
    }

    public string EntityType => Key;
    public string Label => "ทะเบียนสินทรัพย์";
    public ImportCommitStyle CommitStyle => ImportCommitStyle.SharedTransaction;
    public bool SupportsUpdate => false;

    public IReadOnlyList<ImportFieldDto> Schema { get; } = new List<ImportFieldDto>
    {
        new("AssetCode", "รหัสสินทรัพย์", ImportFieldType.Reference, true, null, null, "FA-0001", 50),
        new("Name", "ชื่อสินทรัพย์", ImportFieldType.Text, true, null, null, "คอมพิวเตอร์โน้ตบุ๊ก", 200),
        new("Category", "หมวด", ImportFieldType.Text, false, "ที่ดิน/งานระหว่างก่อสร้าง = ไม่คิดค่าเสื่อม", null, "คอมพิวเตอร์", 100),
        new("PurchaseDate", "วันที่ได้มา", ImportFieldType.Date, true, null, null, "01/03/2566"),
        new("PurchaseCost", "ราคาทุน", ImportFieldType.Amount, true, null, null, "35,000.00"),
        new("SalvageValue", "มูลค่าซาก", ImportFieldType.Amount, false, "เว้นว่าง = 0"),
        new("UsefulLifeMonths", "อายุการใช้งาน (เดือน)", ImportFieldType.Integer, false, "เว้นว่าง = 60 เดือน (ไม่คิดค่าเสื่อม = 0)"),
        new("DepreciationMethod", "วิธีคิดค่าเสื่อม", ImportFieldType.Text, false,
            "StraightLine / DecliningBalance / DoubleDecliningBalance / None — เว้นว่าง = เส้นตรง (ที่ดิน = ไม่คิด)", null, "เส้นตรง", 50),
        new("Location", "สถานที่", ImportFieldType.Text, false, null, null, null, 200),
        new("SerialNumber", "หมายเลขเครื่อง", ImportFieldType.Text, false, null, null, null, 100),
        new("AccumulatedDepreciation", "ค่าเสื่อมสะสมยกมา", ImportFieldType.Amount, false, "ยังไม่รองรับ — ลงผ่านงบทดลองยกมา"),
    };

    public async Task<IReadOnlyList<ImportRowValidation>> ValidateAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> rows, CancellationToken ct)
    {
        var codes = rows.Select(r => Text(r, "AssetCode")).Where(c => c != null).Distinct().ToList();
        var existing = (await _db.FixedAssets.AsNoTracking()
            .Where(a => a.CompanyId == ctx.CompanyId && codes.Contains(a.AssetCode))
            .Select(a => a.AssetCode).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var result = new List<ImportRowValidation>(rows.Count);
        foreach (var r in rows)
        {
            var issues = new List<ImportIssueDto>();
            ImportRowAction? action = null;
            var code = Text(r, "AssetCode");
            var category = Text(r, "Category");
            var (method, methodError) = FixedAssetImportMethod.Resolve(Text(r, "DepreciationMethod"), category);
            if (methodError != null)
                issues.Add(Error("DepreciationMethod", "fa-method-invalid", methodError,
                    method == null && methodError.Contains("พ.ร.ฎ.145") ? "None" : null, "RD-PRD145", "พ.ร.ฎ.145"));
            else if (method == DepreciationMethod.None)
                issues.Add(Info("DepreciationMethod", "fa-not-depreciated",
                    $"หมวด \"{category}\" ไม่คิดค่าเสื่อมราคา (ที่ดิน/งานระหว่างก่อสร้าง — พ.ร.ฎ.145)"));
            if (Amount(r, "PurchaseCost") is not > 0m)
                issues.Add(Error("PurchaseCost", "fa-cost-invalid", "ราคาทุนต้องมากกว่า 0"));
            if (code != null && existing.Contains(code))
            {
                issues.Add(Warning("AssetCode", "fa-code-exists", $"มีสินทรัพย์รหัส {code} อยู่แล้ว — ระบบตั้งเป็น \"ข้าม\" (แก้รหัสถ้าเป็นคนละชิ้น)"));
                action = ImportRowAction.Skip;
            }
            else if (code != null && !seen.Add(code))
            {
                issues.Add(Warning("AssetCode", "duplicate-in-file", $"รหัส {code} ซ้ำในไฟล์ — แถวหลังถูกตั้งเป็น \"ข้าม\""));
                action = ImportRowAction.Skip;
            }
            if (Amount(r, "AccumulatedDepreciation") is > 0m)
                issues.Add(Warning("AccumulatedDepreciation", "fa-accum-dep-unsupported",
                    "ยังไม่รองรับค่าเสื่อมสะสมยกมารายสินทรัพย์ — ระบบลงราคาทุนเต็ม · ลงค่าเสื่อมสะสมยกมาผ่านงบทดลองยกมา (บัญชีค่าเสื่อมราคาสะสม) แล้วกระทบยอดที่หน้าย้ายระบบ"));
            result.Add(new ImportRowValidation(r.RowId, issues, Action: action, DisplayKey: $"{code} {Text(r, "Name")}".Trim()));
        }
        return result;
    }

    public async Task<IReadOnlyList<ImportRowCommitResult>> CommitGroupAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> group, CancellationToken ct)
    {
        var results = new List<ImportRowCommitResult>(group.Count);
        foreach (var r in group.OrderBy(x => x.RowNo))
        {
            if (r.Action == ImportRowAction.Skip) { results.Add(new ImportRowCommitResult(r.RowId, true, Skipped: true)); continue; }
            var code = Text(r, "AssetCode");
            var date = Date(r, "PurchaseDate");
            if (code == null || date == null)
            {
                results.Add(new ImportRowCommitResult(r.RowId, false, Error: "ต้องมีรหัสสินทรัพย์และวันที่ได้มา"));
                continue;
            }
            var life = Number(r, "UsefulLifeMonths");
            var row = new ImportFixedAssetRow(code, Text(r, "Name") ?? "", Text(r, "Category"), Text(r, "Location"), Text(r, "SerialNumber"),
                date.Value, Amount(r, "PurchaseCost") ?? 0m, Amount(r, "SalvageValue") ?? 0m,
                life is { } l ? (int)Math.Round(l, MidpointRounding.AwayFromZero) : 0,
                Text(r, "DepreciationMethod") ?? "");
            // ทีละแถว — ImportAsync ตัดสินวิธีคิดด้วย FixedAssetImportMethod + ตรวจรหัสซ้ำ + สร้างตารางค่าเสื่อม (เส้นเดียวกับหน้าทะเบียน)
            var res = await _assets.ImportAsync(ctx.CompanyId, new List<ImportFixedAssetRow> { row }, CreatedBy(ctx));
            if (res.SuccessCount != 1)
            {
                results.Add(new ImportRowCommitResult(r.RowId, false,
                    Error: res.Errors.FirstOrDefault()?.Replace("แถวที่ 1: ", "") ?? "นำเข้าไม่สำเร็จ"));
                continue;
            }
            var id = await _db.FixedAssets.AsNoTracking()
                .Where(a => a.CompanyId == ctx.CompanyId && a.AssetCode == code && !a.IsDeleted)
                .OrderByDescending(a => a.CreatedAt).Select(a => a.Id).FirstAsync(ct);
            results.Add(new ImportRowCommitResult(r.RowId, true, CommittedEntity, id));
        }
        return results;
    }

    public async Task<IReadOnlyList<ImportUndoCheck>> CanUndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.EntityId).ToList();
        var assets = await _db.FixedAssets.AsNoTracking()
            .Where(a => a.CompanyId == ctx.CompanyId && ids.Contains(a.Id))
            .Select(a => new { a.Id, a.IsDeleted, a.Status, a.AccumulatedDepreciation, a.AssetCode })
            .ToDictionaryAsync(a => a.Id, ct);
        var posted = (await _db.AssetDepreciations.AsNoTracking()
            .Where(d => ids.Contains(d.FixedAssetId) && d.IsPosted && !d.IsDeleted && d.CompanyId == ctx.CompanyId)
            .Select(d => d.FixedAssetId).Distinct().ToListAsync(ct)).ToHashSet();
        return rows.Select(r =>
        {
            if (!assets.TryGetValue(r.EntityId, out var a) || a.IsDeleted)
                return new ImportUndoCheck(r.RowId, false, ImportUndoKind.None, "สินทรัพย์ถูกลบไปแล้ว", null);
            if (posted.Contains(a.Id) || a.AccumulatedDepreciation > 0m || a.Status != AssetStatus.Active)
                return new ImportUndoCheck(r.RowId, false, ImportUndoKind.None, $"สินทรัพย์ {a.AssetCode} ลงค่าเสื่อม/จำหน่ายแล้ว",
                    "ใช้ \"จำหน่าย\" หรือ \"ตัดจำหน่าย\" ที่หน้าทะเบียนสินทรัพย์แทนการย้อน");
            return new ImportUndoCheck(r.RowId, true, ImportUndoKind.SoftDelete);
        }).ToList();
    }

    public async Task<IReadOnlyList<ImportRowUndoResult>> UndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, string reason, CancellationToken ct)
    {
        var results = new List<ImportRowUndoResult>(rows.Count);
        foreach (var r in rows)
        {
            try
            {
                await _assets.DeleteAsync(ctx.CompanyId, r.EntityId);
                results.Add(new ImportRowUndoResult(r.RowId, true));
            }
            catch (Exception ex) when (ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException)
            {
                results.Add(new ImportRowUndoResult(r.RowId, false, ex.Message));
            }
        }
        return results;
    }
}
