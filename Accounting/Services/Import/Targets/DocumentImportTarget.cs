using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using static Accounting.Services.Import.Targets.ImportTargetValues;

namespace Accounting.Services.Import.Targets;

/// <summary>
/// **ปลายทาง "เอกสาร (นำเข้าเป็นร่าง)"** ของศูนย์นำเข้า (รอบ 205 ทีม D · IMPORT_ETL_REVIEW §2.4/§2.9/§2.10 · คำตัดสินข้อ 151 (1))
///
/// <para>แถวหลายบรรทัดของเอกสารเดียว = กลุ่มเดียว (ชนิด + เลขเดิม) · ประกอบคำขอด้วย <see cref="IDocumentImportBuilder"/> แล้วสร้างผ่าน
/// <see cref="IDocumentService.CreateDocumentAsync"/> เท่านั้น (เลข DRAFT · ด่านงวดปิด · ยอดคำนวณโดย DocumentService) — แทนทางเดิม
/// <c>ImportExportService.ImportDocumentAsync</c> ที่เขียนตาราง Documents ตรง ใส่เลขจากไฟล์บนร่าง และคิดยอดเอง (§1)</para>
/// <para>ตรวจก่อน: ชนิดที่นำเข้าได้ · วันที่อยู่ในงวดเปิด (งวดปิด = บล็อก + ทางไปต่อ) · นำเข้าซ้ำ (อ้างอิงเลขเดิมเดียวกัน) ⇒ ตั้งเป็น "ข้าม" + คำเตือน ·
/// บรรทัดไม่ครบ §86/4 · หัวเอกสารในกลุ่มไม่ตรงกัน = คำเตือน (ใช้แถวแรก)</para>
/// <para>ย้อน: ร่างที่ยังเป็นร่าง ⇒ ลบผ่าน <see cref="IDocumentService.DeleteDocumentAsync"/> · อนุมัติแล้ว ⇒ บล็อก + ทาง void/ใบลดหนี้</para>
/// </summary>
public class DocumentImportTarget : IImportTarget
{
    public const string Key = "documents";
    /// <summary>ตัวเลือกของชุด: ชนิดเอกสารตั้งต้นเมื่อไฟล์ไม่มีคอลัมน์ชนิด</summary>
    public const string OptionDocumentType = "documentType";
    public const string CommittedEntity = "Document";

    private readonly AccountingDbContext _db;
    private readonly IDocumentImportBuilder _builder;
    private readonly IDocumentService _documents;

    public DocumentImportTarget(AccountingDbContext db, IDocumentImportBuilder builder, IDocumentService documents)
    {
        _db = db;
        _builder = builder;
        _documents = documents;
    }

    public string EntityType => Key;
    public string Label => "เอกสาร (นำเข้าเป็นร่าง)";
    public ImportCommitStyle CommitStyle => ImportCommitStyle.OwnTransactionPerGroup;
    public bool SupportsUpdate => false;

    public IReadOnlyList<ImportFieldDto> Schema { get; } = new List<ImportFieldDto>
    {
        new("DocumentType", "ชนิดเอกสาร", ImportFieldType.Enum, false, "เว้นว่างได้ถ้าเลือกชนิดตั้งต้นของชุด",
            DocumentImportTypes.Allowed.Select(t => DocumentTypeNames.Title(t, "th")).ToList(), "ใบแจ้งหนี้ซื้อ"),
        new("DocumentNumber", "เลขที่เอกสารเดิม", ImportFieldType.Reference, true, "เลขจากโปรแกรมเดิม — เก็บเป็นอ้างอิง (เลขใหม่ออกตอนอนุมัติ) · แถวเลขเดียวกัน = เอกสารเดียว", null, "IV6701-0012"),
        new("DocumentDate", "วันที่เอกสาร", ImportFieldType.Date, true, null, null, "15/01/2567"),
        new("DueDate", "วันครบกำหนด", ImportFieldType.Date, false),
        new("ContactName", "ชื่อลูกค้า/ผู้ขาย", ImportFieldType.Text, false, "ต้องมีชื่อหรือเลขผู้เสียภาษี", null, "บริษัท ตัวอย่าง จำกัด", 200),
        new("ContactTaxId", "เลขผู้เสียภาษีคู่ค้า", ImportFieldType.TaxId, false, "จับคู่ผู้ติดต่อได้แม่นที่สุด"),
        new("ContactBranchCode", "สาขาคู่ค้า", ImportFieldType.BranchCode, false, "00000 = สำนักงานใหญ่"),
        new("Description", "รายการ", ImportFieldType.Text, true, null, null, "ค่าบริการทำบัญชี", 500),
        new("Quantity", "จำนวน", ImportFieldType.Decimal, false, "เว้นว่าง = 1"),
        new("Unit", "หน่วย", ImportFieldType.Text, false, null, null, "ครั้ง", 50),
        new("UnitPrice", "ราคาต่อหน่วย", ImportFieldType.Amount, true, null, null, "1,500.00"),
        new("DiscountPercent", "ส่วนลด %", ImportFieldType.Decimal, false),
        new("VatRate", "อัตรา VAT", ImportFieldType.VatRate, false, "เว้นว่าง = 7% (กิจการจด VAT) · 0 = อัตราศูนย์"),
        new("WithholdingTaxRate", "หัก ณ ที่จ่าย %", ImportFieldType.Decimal, false, null, null, "3"),
        new("AccountCode", "ผังบัญชี", ImportFieldType.AccountCode, false, "ฝั่งซื้อเว้นว่างได้ — ระบบแนะนำผังให้"),
        new("ProductCode", "รหัสสินค้า", ImportFieldType.Text, false, null, null, null, 50),
        new("PricesIncludeVat", "ราคารวม VAT", ImportFieldType.Flag, false),
        new("Notes", "หมายเหตุ", ImportFieldType.Text, false, null, null, null, 1000),
    };

    /// <summary>คีย์กลุ่ม = ชนิด + เลขเดิม (ไม่มีเลข = แถวละเอกสาร)</summary>
    public static string GroupKeyOf(ImportTargetRow row, string? defaultType)
    {
        var no = Text(row, "DocumentNumber");
        var type = DocumentImportTypes.Parse(Text(row, "DocumentType") ?? defaultType);
        return no == null ? $"row-{row.RowNo}" : $"{(type?.ToString() ?? "?")}|{no}";
    }

    public async Task<IReadOnlyList<ImportRowValidation>> ValidateAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> rows, CancellationToken ct)
    {
        var defaultType = Option(ctx, OptionDocumentType);
        var periods = await _db.FiscalPeriods.AsNoTracking()
            .Where(p => p.CompanyId == ctx.CompanyId && p.Status != FiscalPeriodStatus.Open)
            .Select(p => new { p.Name, p.StartDate, p.EndDate })
            .ToListAsync(ct);
        var originals = rows.Select(r => Text(r, "DocumentNumber")).Where(n => n != null).Distinct().ToList();
        var already = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == ctx.CompanyId && !d.IsDeleted && d.Reference != null && originals.Contains(d.Reference))
            .Select(d => new { d.Reference, d.DocumentType, d.DocumentNumber })
            .ToListAsync(ct);

        var result = new List<ImportRowValidation>(rows.Count);
        foreach (var g in rows.GroupBy(r => GroupKeyOf(r, defaultType)))
        {
            var head = g.OrderBy(r => r.RowNo).First();
            var typeText = Text(head, "DocumentType") ?? defaultType;
            var type = DocumentImportTypes.Parse(typeText);
            var date = Date(head, "DocumentDate");
            var original = Text(head, "DocumentNumber");
            var groupIssues = new List<ImportIssueDto>();
            ImportRowAction? action = null;

            if (type == null)
                groupIssues.Add(Error("DocumentType", "doc-type-missing",
                    typeText == null ? "ไม่ระบุชนิดเอกสาร — เติมคอลัมน์ชนิด หรือเลือกชนิดตั้งต้นของชุด" : $"ไม่รู้จักชนิดเอกสาร \"{typeText}\"",
                    null));
            else if (!DocumentImportTypes.Allowed.Contains(type.Value))
                groupIssues.Add(Error("DocumentType", "doc-type-unsupported",
                    $"ยังไม่รองรับการนำเข้า{DocumentTypeNames.Title(type.Value, "th")} — {DocumentImportTypes.UnsupportedWayForward(type.Value)}"));

            if (date is { } dd && periods.FirstOrDefault(p => p.StartDate <= dd && p.EndDate >= dd) is { } closed)
                groupIssues.Add(Error("DocumentDate", "period-closed",
                    $"วันที่ {dd:dd/MM/yyyy} อยู่ในงวด {closed.Name} ที่ปิดแล้ว — เปิดงวดที่หน้า งวดบัญชี ก่อน หรือแก้วันที่", null, "PERIOD-LOCK"));

            if (original != null && type != null && already.FirstOrDefault(a => a.Reference == original && a.DocumentType == type.Value) is { } dup)
            {
                groupIssues.Add(Warning("DocumentNumber", "doc-already-imported",
                    $"มีเอกสารอ้างอิงเลขเดิม {original} อยู่แล้ว ({dup.DocumentNumber}) — ระบบตั้งเป็น \"ข้าม\" · เปลี่ยนเป็นสร้างใหม่ได้ถ้าตั้งใจ"));
                action = ImportRowAction.Skip;
            }

            var contactKeys = g.Select(r => (Text(r, "ContactTaxId") ?? "") + "|" + (Text(r, "ContactName") ?? "")).Distinct().Count();
            var dates = g.Select(r => Text(r, "DocumentDate")).Distinct().Count();
            if (contactKeys > 1 || dates > 1)
                groupIssues.Add(Warning(null, "doc-header-mismatch",
                    $"แถวของเอกสาร {original} มีผู้ติดต่อ/วันที่ไม่ตรงกัน — ระบบใช้ค่าของแถวแรก (แถว {head.RowNo})"));
            if (Text(head, "ContactName") == null && Text(head, "ContactTaxId") == null && GuidValue(head, "ContactId") == null)
                groupIssues.Add(Error("ContactName", "contact-missing", "ไม่มีชื่อหรือเลขผู้เสียภาษีของคู่ค้า — เติมคอลัมน์ผู้ติดต่อ"));

            foreach (var r in g)
            {
                var issues = new List<ImportIssueDto>(groupIssues);
                var qty = Number(r, "Quantity");
                var price = Amount(r, "UnitPrice");
                IReadOnlyDictionary<string, string?>? filled = null;
                if (Text(r, "Description") == null)
                    issues.Add(Error("Description", "line-description-missing", "บรรทัดต้องมีรายการ (§86/4)", null, "RD-86/4", "ป.รัษฎากร ม.86/4"));
                if (qty is <= 0m)
                    issues.Add(Error("Quantity", "line-negative", "จำนวนต้องมากกว่า 0 — ยอดลด/คืนให้ออกใบลดหนี้อ้างใบเดิม", null, "RD-86/4"));
                if (price is < 0m)
                    issues.Add(Error("UnitPrice", "line-negative", "ราคาต่อหน่วยติดลบ — ยอดลด/คืนให้ออกใบลดหนี้อ้างใบเดิม", null, "RD-86/4"));
                if (qty == null)
                    filled = new Dictionary<string, string?> { ["Quantity"] = "1" };   // ฟ้า "ระบบเติม" — ไม่มีจำนวน = 1 หน่วย
                result.Add(new ImportRowValidation(r.RowId, issues, Action: action,
                    DisplayKey: original == null ? Text(r, "Description") : $"{original} · {Text(r, "Description")}",
                    GroupKey: GroupKeyOf(r, defaultType), FilledValues: filled));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<ImportRowCommitResult>> CommitGroupAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> group, CancellationToken ct)
    {
        var skipped = group.Where(r => r.Action == ImportRowAction.Skip)
            .Select(r => new ImportRowCommitResult(r.RowId, true, Skipped: true)).ToList();
        var rows = group.Where(r => r.Action != ImportRowAction.Skip).ToList();
        if (rows.Count == 0) return skipped;

        // ชนิดตั้งต้นของชุด ⇒ เติมให้แถวที่ไม่มีคอลัมน์ชนิด (ตัวประกอบอ่านจากแถวอย่างเดียว)
        var defaultType = Option(ctx, OptionDocumentType);
        if (defaultType != null)
            rows = rows.Select(r => Text(r, "DocumentType") != null ? r
                : r with { Values = new Dictionary<string, string?>(r.Values, StringComparer.OrdinalIgnoreCase) { ["DocumentType"] = defaultType } })
                .ToList();

        var build = await _builder.BuildAsync(ctx.CompanyId, ctx.EntityType, rows, ctx.Options, ct);
        if (build.Request == null)
            return FailAll(rows, string.Join(" · ", build.Issues.Where(i => i.Severity == ImportIssueSeverity.Error).Select(i => i.Message)))
                .Concat(skipped).ToList();
        try
        {
            var doc = await _documents.CreateDocumentAsync(ctx.CompanyId, build.Request, CreatedBy(ctx), originModule: "Import");
            return rows.Select(r => new ImportRowCommitResult(r.RowId, true, CommittedEntity, doc.Id)).Concat(skipped).ToList();
        }
        catch (Exception ex) when (ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException)
        {
            // DocumentService ตอบเหตุเป็นภาษาไทย (งวดปิด · ผู้ติดต่อ · โควตา) — ล้มดังที่ผลของทุกแถวในเอกสารนี้
            return FailAll(rows, ex.Message).Concat(skipped).ToList();
        }
    }

    public async Task<IReadOnlyList<ImportUndoCheck>> CanUndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.EntityId).Distinct().ToList();
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == ctx.CompanyId && ids.Contains(d.Id))
            .Select(d => new { d.Id, d.Status, d.IsDeleted, d.DocumentNumber })
            .ToDictionaryAsync(d => d.Id, ct);
        return rows.Select(r =>
        {
            if (!docs.TryGetValue(r.EntityId, out var d) || d.IsDeleted)
                return new ImportUndoCheck(r.RowId, false, ImportUndoKind.None, "เอกสารถูกลบไปแล้ว", null);
            if (d.Status != DocumentStatus.Draft)
                return new ImportUndoCheck(r.RowId, false, ImportUndoKind.None, $"เอกสาร {d.DocumentNumber} ไม่ใช่ร่างแล้ว (อนุมัติ/ออกเลขแล้ว)",
                    "ยกเลิกเอกสาร (void) หรือออกใบลดหนี้ที่หน้าเอกสาร — เลขที่ออกแล้วลบไม่ได้ (§86/4)");
            return new ImportUndoCheck(r.RowId, true, ImportUndoKind.DeleteDraft);
        }).ToList();
    }

    public async Task<IReadOnlyList<ImportRowUndoResult>> UndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, string reason, CancellationToken ct)
    {
        var results = new List<ImportRowUndoResult>(rows.Count);
        foreach (var g in rows.GroupBy(r => r.EntityId))
        {
            try
            {
                await _documents.DeleteDocumentAsync(ctx.CompanyId, g.Key);
                results.AddRange(g.Select(r => new ImportRowUndoResult(r.RowId, true)));
            }
            catch (Exception ex) when (ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException)
            {
                results.AddRange(g.Select(r => new ImportRowUndoResult(r.RowId, false, ex.Message)));
            }
        }
        return results;
    }
}
