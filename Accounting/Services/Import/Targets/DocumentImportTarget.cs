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
public class DocumentImportTarget : IImportTarget, IImportGroupIntegrityTarget
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

    /// <summary>field ปลายทาง — ตัวเดียวที่ <see cref="ImportSchemas"/> (ทีม A · owner เดียวของแคตตาล็อก) · ทีม D ปรับรายการของชนิดนี้ที่นั่น</summary>
    public IReadOnlyList<ImportFieldDto> Schema => ImportSchemas.For(Key)!;

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

        var allowAi = AllowAi(ctx);
        var result = new List<ImportRowValidation>(rows.Count);
        foreach (var g in rows.GroupBy(r => GroupKeyOf(r, defaultType)))
        {
            var head = g.OrderBy(r => r.RowNo).First();
            // ฝ่ายค้านรอบ ETL (P1-2): ตัดสินผู้ติดต่อ + ผังแนะนำตั้งแต่ขั้นตรวจ (เดิมตอนลงข้อมูล ⇒ หน้าตรวจขึ้น "พร้อมลง" แล้วลงไม่สำเร็จ · ถาม AI ตอนลง)
            var prep = await _builder.PrepareAsync(ctx.CompanyId, g.Select(r => defaultType == null || Text(r, "DocumentType") != null ? r
                : r with { Values = new Dictionary<string, string?>(r.Values, StringComparer.OrdinalIgnoreCase) { ["DocumentType"] = defaultType } })
                .ToList(), allowAi, ct);
            var contact = prep.Contact;
            var lineHints = prep.LineHints.ToDictionary(h => h.RowId);
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
            groupIssues.AddRange(contact.Issues);

            foreach (var r in g)
            {
                var issues = new List<ImportIssueDto>(groupIssues);
                var qty = Number(r, "LineQuantity");
                var price = Amount(r, "LineUnitPrice");
                IReadOnlyDictionary<string, string?>? filled = null;
                if (Text(r, "LineDescription") == null)
                    issues.Add(Error("LineDescription", "line-description-missing", "บรรทัดต้องมีรายการ (§86/4)", null, "RD-86/4", "ป.รัษฎากร ม.86/4"));
                if (qty is <= 0m)
                    issues.Add(Error("LineQuantity", "line-negative", "จำนวนต้องมากกว่า 0 — ยอดลด/คืนให้ออกใบลดหนี้อ้างใบเดิม", null, "RD-86/4"));
                if (price is < 0m)
                    issues.Add(Error("LineUnitPrice", "line-negative", "ราคาต่อหน่วยติดลบ — ยอดลด/คืนให้ออกใบลดหนี้อ้างใบเดิม", null, "RD-86/4"));
                var fill = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (qty == null) fill["LineQuantity"] = "1";   // ฟ้า "ระบบเติม" — ไม่มีจำนวน = 1 หน่วย
                Dictionary<string, string?>? hints = null;
                if (lineHints.TryGetValue(r.RowId, out var gl) && gl.SuggestedCode != null)
                {
                    // ผังที่ระบบแนะนำ (นักเรียน/AI) เติมเป็นค่า "ระบบเติม" ให้ผู้ใช้เห็น/เปลี่ยนได้ + ฝาก feedback ไว้ให้บรรทัดเอกสารปิดลูปตอนอนุมัติ
                    fill["LineAccountCode"] = gl.SuggestedCode;
                    hints = new Dictionary<string, string?>
                    {
                        [DocumentImportBuilder.HintGlCode] = gl.SuggestedCode,
                        [DocumentImportBuilder.HintGlFeedback] = gl.GlFeedbackId?.ToString(),
                        [DocumentImportBuilder.HintGlUsedAi] = gl.UsedAi ? "1" : "0",
                    };
                    issues.Add(Info("LineAccountCode", "line-account-suggested",
                        (gl.UsedAi ? "🤖 AI แนะนำ" : "⚙️ ระบบแนะนำ") + $" ผังบัญชี {gl.SuggestedCode} — ตรวจแล้วเปลี่ยนได้ในแถวนี้"));
                }
                filled = fill.Count > 0 ? fill : null;
                result.Add(new ImportRowValidation(r.RowId, issues, Action: action,
                    MatchedEntityId: contact.PickedContactId,
                    MatchKind: contact.MatchKind,
                    MatchScore: contact.MatchScore,
                    DisplayKey: original == null ? Text(r, "LineDescription") : $"{original} · {Text(r, "LineDescription")}",
                    GroupKey: GroupKeyOf(r, defaultType), FilledValues: filled,
                    MatchCandidates: contact.Candidates.Count > 0 ? contact.Candidates : null,
                    MatchAutoApplied: contact.AutoApplied,
                    MatchUsedAi: contact.UsedAi,
                    MatchAiFeedbackId: contact.AiFeedbackId,
                    MatchAiAnswer: contact.AiAnswer,
                    MatchAnswerSpace: ImportMatchAnswerSpace.Link,
                    // null = คงค่าที่ฝากไว้เดิม (feedback ของผังที่แนะนำไว้ยังต้องไปปิดลูปแม้ผู้ใช้พิมพ์ผังเอง)
                    Hints: hints));
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
        // ฝ่ายค้านรอบ ETL (P2): DocumentService commit ธุรกรรมของตัวเอง — ถ้าเครื่องล่มหลังสร้างใบแต่ก่อนประทับผลแถว ทำต่อแล้วต้องไม่สร้างใบซ้ำ
        if (await CreatedEarlierInBatchAsync(ctx, build.Request, ct) is Guid already)
            return rows.Select(r => new ImportRowCommitResult(r.RowId, true, CommittedEntity, already)).Concat(skipped).ToList();
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

    /// <summary>
    /// ใบที่ชุดนี้สร้างไปแล้วแต่แถวยังไม่ถูกประทับผล (เครื่องล่มระหว่าง domain commit กับการประทับ) — ร่างจากศูนย์นำเข้า (<c>OriginModule = "Import"</c>) ·
    /// ผู้สร้างคนเดียวกัน · หลังเวลาสร้างชุด · ชนิด/ผู้ติดต่อ/เลขเดิมตรงกัน · <b>ยังไม่มีแถวใดของชุดนี้อ้างถึง</b> · ทางเดิม (ไม่มีชุดจริง) = ไม่ตรวจ
    /// </summary>
    private async Task<Guid?> CreatedEarlierInBatchAsync(ImportTargetContext ctx, Accounting.Models.DTOs.Document.CreateDocumentRequest request,
        CancellationToken ct)
    {
        var since = await _db.SmartImportSessions.AsNoTracking()
            .Where(s => s.Id == ctx.BatchId && s.CompanyId == ctx.CompanyId).Select(s => (DateTime?)s.CreatedAt).FirstOrDefaultAsync(ct);
        if (since is not DateTime from) return null;
        var by = CreatedBy(ctx);
        var claimed = _db.ImportStagedRows.Where(r => r.SessionId == ctx.BatchId && r.CompanyId == ctx.CompanyId && r.CommittedEntityId != null)
            .Select(r => r.CommittedEntityId!.Value);
        return await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == ctx.CompanyId && !d.IsDeleted && d.OriginModule == "Import" && d.CreatedBy == by && d.CreatedAt >= from
                && d.DocumentType == request.DocumentType && d.ContactId == request.ContactId && d.Reference == request.Reference
                && !claimed.Contains(d.Id))
            .OrderBy(d => d.CreatedAt).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
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
