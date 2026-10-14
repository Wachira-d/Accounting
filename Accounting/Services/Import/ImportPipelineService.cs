using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Import;

/// <summary>
/// **pipeline ของศูนย์นำเข้าข้อมูล** (รอบ 205 ทีม B · สัญญาที่ <see cref="IImportPipelineService"/> · IMPORT_ETL_REVIEW §2.1–§2.5)
/// Extract (<see cref="ImportSourceInspector"/> · <see cref="ImportHeaderDetector"/>) → Transform (จับคู่คอลัมน์ · <see cref="ImportCellNormalizer"/> ·
/// <see cref="IImportTarget.ValidateAsync"/>) → Stage (<c>ImportStagedRows</c>) → Review (แก้/ยืนยัน) → Commit (writer/domain service เดิมผ่าน target ·
/// idempotent) → Learn (แม่แบบ · <c>RecordUserChoiceAsync</c>) → Undo
///
/// <para>กติกา: <b>กรอง CompanyId ทุกคำสั่ง</b> · ค่าผู้ใช้ชนะเสมอ (เซลล์/การเลือกที่ผู้ใช้แก้ไม่ถูกวิเคราะห์ทับ) · บล็อกเฉพาะ §2.5 (Error) นอกนั้นคำเตือนที่ต้อง
/// รับทราบตอน commit · ไฟล์ ≤ <see cref="InlineRowLimit"/> แถวทำในคำขอ มากกว่านั้น <see cref="ImportBatchWorker"/> ทำต่อ · ไม่มี static state ·
/// ล้มดัง (สถานะ batch + หมายเหตุ + log) ไม่ใช่ HTTP 200 กับของว่าง</para>
/// </summary>
public partial class ImportPipelineService : IImportPipelineService
{
    /// <summary>แถวข้อมูลสูงสุดที่ทำในคำขอเดียว (วิเคราะห์/ลงข้อมูล) — มากกว่านี้เข้าคิว worker</summary>
    public const int InlineRowLimit = 2000;
    /// <summary>savepoint ต่อกี่แถว (SharedTransaction) — ไม่ตัดกลางกลุ่ม (ใบสำคัญ/เอกสารหลายบรรทัด)</summary>
    public const int SharedChunkRows = 200;
    /// <summary>worker ทำทีละกี่แถวต่อรอบ (heartbeat ระหว่างรอบ)</summary>
    public const int WorkerChunkRows = 500;
    /// <summary>ค่ารูปมาตรฐานของ "ยกเว้นภาษีมูลค่าเพิ่ม" (ม.81) ในช่องอัตรา VAT — ไม่ใช่ 0% (ม.80/1) · writer เดิมอ่านเป็น 0 + target เตือน</summary>
    public const string VatExemptToken = "ยกเว้น";

    private readonly AccountingDbContext _db;
    private readonly IFileAttachmentService _files;
    private readonly IImportTargetRegistry _targets;
    private readonly IImportMappingTemplateStore _templates;
    /// <summary>จับคู่คอลัมน์ (ทีม A) — null = ชั้น local ล้วนของตัวจับคู่ตัวเดียวกัน (เทสต์/ไม่ได้ลงทะเบียน)</summary>
    private readonly IImportColumnMatchService? _columnMatch;
    /// <summary>ปิดลูปคำตอบเดี่ยว ImportEntityMatch (ทีม A) — null = ไม่มีแถว feedback ให้ปิด</summary>
    private readonly IImportAiAugmenter? _ai;
    private readonly IPermissionService? _permissions;
    private readonly ILogger<ImportPipelineService>? _logger;

    public ImportPipelineService(AccountingDbContext db, IFileAttachmentService files, IImportTargetRegistry targets,
        IImportMappingTemplateStore templates, IImportColumnMatchService? columnMatch = null, IImportAiAugmenter? ai = null,
        IPermissionService? permissions = null, ILogger<ImportPipelineService>? logger = null)
    {
        _db = db;
        _files = files;
        _targets = targets;
        _templates = templates;
        _columnMatch = columnMatch;
        _ai = ai;
        _permissions = permissions;
        _logger = logger;
    }

    // ══════════════════════════════ สร้าง / วิเคราะห์ ══════════════════════════════

    public async Task<ImportBatchDetailDto> CreateAsync(Guid companyId, ImportUploadedFile file, ImportCreateBatchRequest request, ImportActor actor,
        CancellationToken ct = default)
    {
        if (file.Content == null || file.Content.Length == 0)
            throw new BusinessRuleException("ไฟล์ว่างเปล่า — ส่งออกข้อมูลจากโปรแกรมต้นทางใหม่แล้วอัปโหลดอีกครั้ง");
        var fileName = string.IsNullOrWhiteSpace(file.FileName) ? "import.csv" : Path.GetFileName(file.FileName.Trim());

        // Extract: ชนิด/ชีต/encoding จากเนื้อไฟล์ · ไฟล์ที่ไม่ใช่ตาราง (.xls/XML/PDF/OFX/QIF/JSON) ล้มดังพร้อมทางไปต่อ <b>ก่อน</b>สร้างอะไร
        var preview = ImportSourceInspector.Inspect(fileName, file.Content, request.SheetName);
        var opened = ImportTabularReader.Open(fileName, file.Content, preview.SheetName);
        var rows = opened.Rows.ToList();

        var target = request.EntityType != null
            ? _targets.Find(request.EntityType) ?? throw new BusinessRuleException(
                $"ยังไม่รองรับการนำเข้าข้อมูลชนิด \"{request.EntityType}\" ที่ศูนย์นำเข้า — เลือกชนิดข้อมูลจากรายการ")
            : GuessTarget(rows);

        var batchId = Guid.NewGuid();
        var state = new ImportBatchState
        {
            DateOrder = request.DateOrder,
            TemplateId = request.TemplateId,
            // ค่าเพิ่มเติมของชุด (บัญชีธนาคาร · วันที่ยกมา · คลัง ฯลฯ) — ส่งต่อให้ target ทาง ImportTargetContext.BatchOptions
            Options = request.Options?.Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
                .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
        };
        if (request.HeaderRowIndex is int hi) { state.HeaderChosenByUser = true; state.HeaderRowIndex = Math.Max(0, hi); state.HeaderRowSpan = 1; }
        var session = new SmartImportSession
        {
            Id = batchId,
            CompanyId = companyId,
            EntityType = target.EntityType,
            FileName = Trunc(fileName, 255)!,
            FileFormat = preview.Kind.ToString().ToLowerInvariant(),
            HasHeaderRow = true,
            Status = ImportSessionStatus.Analyzing,
            Phase = ImportBatchPhase.Extract,
            FileSha256 = preview.Sha256,
            SheetName = Trunc(preview.SheetName, 200),
            SheetNamesJson = preview.SheetNames.Count > 0 ? ImportJson.Serialize(preview.SheetNames) : null,
            Encoding = Trunc(preview.Encoding, 32),
            Delimiter = Trunc(preview.Delimiter, 4),
            SourceSystem = Trunc(request.SourceSystem, 64),
            OptionsJson = ImportJson.Serialize(state),
            CreatedBy = actor.UserId.ToString(),
        };
        _db.SmartImportSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        // ไฟล์ต้นทางเก็บผ่าน attachment abstraction ตัวเดียว (ห้ามเขียน disk เอง · CLAUDE.md D) — ดาวน์โหลดซ้ำ/วิเคราะห์ใหม่/worker อ่านจากที่นี่
        var stored = await _files.UploadBytesAsync(companyId, "ImportBatch", batchId, StorageFileName(fileName, preview.Kind),
            StorageContentType(file.ContentType, preview.Kind), file.Content, actor.UserId);
        session.SourceFileAttachmentId = stored.Id;
        await _db.SaveChangesAsync(ct);

        await PrepareAndStageAsync(session, target, rows, structural: true, allowAi: true, actor, ct);
        return (await GetAsync(companyId, batchId, ct))!;
    }

    public async Task<ImportBatchDetailDto> AnalyzeAsync(Guid companyId, Guid batchId, ImportAnalyzeRequest request, ImportActor actor,
        CancellationToken ct = default)
    {
        var session = await LoadAsync(companyId, batchId, ct);
        EnsureEditable(session, "วิเคราะห์ใหม่");
        if (await CommittedCountAsync(session, ct) > 0)
            throw new BusinessRuleException("ชุดนี้ลงข้อมูลไปแล้วบางแถว จึงวิเคราะห์ใหม่ไม่ได้ — ย้อนการนำเข้าก่อน หรืออัปโหลดเป็นชุดใหม่");
        var state = State(session);
        var target = Target(session);
        var structural = false;
        if (request.EntityType != null)
        {
            var t = _targets.Find(request.EntityType) ?? throw new BusinessRuleException(
                $"ยังไม่รองรับการนำเข้าข้อมูลชนิด \"{request.EntityType}\" ที่ศูนย์นำเข้า — เลือกชนิดข้อมูลจากรายการ");
            if (t.EntityType != target.EntityType) { target = t; session.EntityType = t.EntityType; structural = true; }
        }
        if (request.SheetName != null && !string.Equals(request.SheetName, session.SheetName, StringComparison.OrdinalIgnoreCase))
        {
            session.SheetName = Trunc(request.SheetName, 200);
            structural = true;
        }
        if (request.HeaderRowIndex is int hi)
        {
            state.HeaderChosenByUser = true;
            state.HeaderRowIndex = Math.Max(0, hi);
            state.HeaderRowSpan = Math.Clamp(request.HeaderRowSpan ?? 1, 1, 2);
            structural = true;
        }
        else if (request.HeaderRowSpan is int span && state.HeaderChosenByUser)
        {
            state.HeaderRowSpan = Math.Clamp(span, 1, 2);
            structural = true;
        }
        if (request.TemplateId is Guid tid) { state.TemplateId = tid; state.ColumnsChosenByUser = false; }
        if (request.SourceSystem != null) session.SourceSystem = Trunc(request.SourceSystem, 64);
        if (request.DateOrder is ImportDateOrder order) state.DateOrder = order;
        if (structural) state.ColumnsChosenByUser = false;
        session.OptionsJson = ImportJson.Serialize(state);

        var rows = await ReadRowsAsync(session, ct);
        await PrepareAndStageAsync(session, target, rows, structural, allowAi: true, actor, ct);
        return (await GetAsync(companyId, batchId, ct))!;
    }

    // ══════════════════════════════ อ่าน ══════════════════════════════

    public async Task<ImportBatchListDto> ListAsync(Guid companyId, ImportBatchListQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);
        var q = _db.SmartImportSessions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.OptionsJson != null && s.SourceFileAttachmentId != null);
        if (query.EntityType != null)
        {
            var et = _targets.Find(query.EntityType)?.EntityType ?? query.EntityType;
            q = q.Where(s => s.EntityType == et);
        }
        if (query.Status is ImportSessionStatus st) q = q.Where(s => s.Status == st);
        var total = await q.CountAsync(ct);
        var sessions = await q.OrderByDescending(s => s.CreatedAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var items = new List<ImportBatchSummaryDto>(sessions.Count);
        foreach (var s in sessions) items.Add(await SummaryAsync(s, live: false, ct));
        return new ImportBatchListDto(page, size, total, items);
    }

    public async Task<ImportBatchDetailDto?> GetAsync(Guid companyId, Guid batchId, CancellationToken ct = default)
    {
        var session = await _db.SmartImportSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == batchId && s.CompanyId == companyId, ct);
        if (session == null) return null;
        return await DetailAsync(session, ct);
    }

    public async Task<ImportRowPageDto> GetRowsAsync(Guid companyId, Guid batchId, ImportRowQuery query, CancellationToken ct = default,
        bool revealPii = false)
    {
        var session = await LoadAsync(companyId, batchId, ct, tracked: false);
        var state = State(session);
        var target = _targets.Find(session.EntityType);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 500);
        var q = _db.ImportStagedRows.AsNoTracking().Where(r => r.SessionId == batchId && r.CompanyId == companyId);
        if (query.Status is ImportRowStatus st) q = q.Where(r => r.Status == st);
        if (!string.IsNullOrWhiteSpace(query.IssueCode))
        {
            // IssuesJson ไม่เข้ารหัส (รูป camelCase ของ ImportJson) — ค้นรหัสด้วยข้อความ ไม่แตะคอลัมน์ PII
            var needle = "%\"code\":\"" + query.IssueCode.Trim().Replace("%", "").Replace("_", "\\_") + "\"%";
            q = q.Where(r => r.IssuesJson != null && EF.Functions.Like(r.IssuesJson, needle));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = "%" + query.Search.Trim().Replace("%", "").Replace("_", "\\_") + "%";
            q = q.Where(r => r.DisplayKey != null && EF.Functions.ILike(r.DisplayKey, s));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(r => r.RowNo).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var fields = target == null ? new Dictionary<string, ImportFieldDto>(StringComparer.OrdinalIgnoreCase) : FieldMap(target);
        var snapshot = await RefreshSnapshotAsync(session, persist: false, ct);
        return new ImportRowPageDto(page, size, total, rows.Select(r => ToRowDto(r, state, fields, session.EntityType, revealPii)).ToList(),
            snapshot.IssueCounts);
    }

    // ══════════════════════════════ ผู้ใช้ตรวจ/แก้ ══════════════════════════════

    public async Task<ImportBatchDetailDto> RemapAsync(Guid companyId, Guid batchId, ImportRemapRequest request, ImportActor actor,
        CancellationToken ct = default)
    {
        var session = await LoadAsync(companyId, batchId, ct);
        EnsureEditable(session, "จับคู่คอลัมน์");
        if (await CommittedCountAsync(session, ct) > 0)
            throw new BusinessRuleException("ชุดนี้ลงข้อมูลไปแล้วบางแถว จึงเปลี่ยนการจับคู่คอลัมน์ไม่ได้ — ย้อนการนำเข้าก่อน หรืออัปโหลดเป็นชุดใหม่");
        var target = Target(session);
        var fields = FieldMap(target);
        var state = State(session);
        var assigned = request.Columns.Where(c => !string.IsNullOrWhiteSpace(c.TargetField)).ToList();
        foreach (var dup in assigned.GroupBy(c => c.TargetField!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            throw new BusinessRuleException(
                $"เลือกช่อง \"{(fields.TryGetValue(dup.Key, out var df) ? df.Label : dup.Key)}\" ให้หลายคอลัมน์ — หนึ่งช่องรับได้คอลัมน์เดียว");
        foreach (var a in request.Columns)
        {
            var col = state.Columns.FirstOrDefault(c => c.SourceIndex == a.SourceIndex)
                ?? throw new BusinessRuleException($"ไม่มีคอลัมน์ลำดับที่ {a.SourceIndex + 1} ในไฟล์นี้");
            ImportFieldDto? field = null;
            if (!string.IsNullOrWhiteSpace(a.TargetField) && !fields.TryGetValue(a.TargetField, out field))
                throw new BusinessRuleException($"ช่อง \"{a.TargetField}\" ไม่มีในข้อมูลชนิด{target.Label} — เลือกจากรายการ");
            // ค่าผู้ใช้ชนะ: คอลัมน์อื่นที่เคยจับคู่ช่องนี้ถูกปลด
            if (field != null)
                for (var i = 0; i < state.Columns.Count; i++)
                    if (state.Columns[i].SourceIndex != a.SourceIndex
                        && string.Equals(state.Columns[i].TargetField, field.Field, StringComparison.OrdinalIgnoreCase)
                        && !request.Columns.Any(x => x.SourceIndex == state.Columns[i].SourceIndex))
                        state.Columns[i] = state.Columns[i] with
                        {
                            TargetField = null, TargetLabel = null, Source = ImportMapSource.None,
                            SourceLabel = MapSourceLabel(ImportMapSource.None, false), Confidence = 0m, UsedAi = false,
                        };
            var idx = state.Columns.IndexOf(col);
            state.Columns[idx] = col with
            {
                TargetField = field?.Field,
                TargetLabel = field?.Label,
                Source = field == null ? ImportMapSource.None : ImportMapSource.User,
                SourceLabel = MapSourceLabel(field == null ? ImportMapSource.None : ImportMapSource.User, false),
                Confidence = field == null ? 0m : 1m,
                UsedAi = false,
                Transform = a.Transform ?? col.Transform,
            };
        }
        state.ColumnsChosenByUser = true;
        session.OptionsJson = ImportJson.Serialize(state);
        await SyncColumnMappingRowsAsync(session, state, ct);
        await _db.SaveChangesAsync(ct);
        await RecordColumnChoiceAsync(session, state, ct);

        var rows = await ReadRowsAsync(session, ct);
        await PrepareAndStageAsync(session, target, rows, structural: false, allowAi: true, actor, ct, keepColumns: true);
        return (await GetAsync(companyId, batchId, ct))!;
    }

    public async Task<ImportRowDto> EditRowAsync(Guid companyId, Guid batchId, Guid rowId, ImportEditRowRequest request, ImportActor actor,
        CancellationToken ct = default, bool revealPii = false)
    {
        var session = await LoadAsync(companyId, batchId, ct);
        EnsureEditable(session, "แก้แถว");
        var target = Target(session);
        var state = State(session);
        var fields = FieldMap(target);
        var row = await _db.ImportStagedRows.FirstOrDefaultAsync(r => r.Id == rowId && r.SessionId == batchId && r.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบแถวนี้ในชุดนำเข้า");
        if (row.Status is ImportRowStatus.Committed or ImportRowStatus.Undone)
            throw new BusinessRuleException("แถวนี้ลงข้อมูลแล้ว — ย้อนการนำเข้าก่อนถ้าต้องการแก้");
        ApplyRowEdit(row, request, fields, state, target.SupportsUpdate);
        row.UserEdited = true;
        row.UpdatedBy = actor.UserId.ToString();
        await RevalidateAsync(session, target, state, new[] { row }, actor, ct);
        await RefreshSnapshotAsync(session, persist: true, ct);
        await _db.SaveChangesAsync(ct);
        return ToRowDto(row, state, fields, session.EntityType, revealPii);
    }

    public async Task<ImportBulkResultDto> BulkActionAsync(Guid companyId, Guid batchId, ImportBulkActionRequest request, ImportActor actor,
        CancellationToken ct = default)
    {
        var session = await LoadAsync(companyId, batchId, ct);
        EnsureEditable(session, "แก้หลายแถว");
        var target = Target(session);
        var state = State(session);
        var fields = FieldMap(target);
        if ((request.RowIds == null || request.RowIds.Count == 0) && request.WhereStatus == null && string.IsNullOrWhiteSpace(request.WhereIssueCode))
            throw new BusinessRuleException("เลือกแถวก่อน (ติ๊กแถว หรือกรองตามสถานะ/ประเภทปัญหา) แล้วสั่งอีกครั้ง");
        if (request.Op == ImportBulkOp.SetAction && request.Action == null)
            throw new BusinessRuleException("เลือกการกระทำ (สร้างใหม่/อัปเดต/ข้าม) ก่อน");
        if (request.Op == ImportBulkOp.SetField && (string.IsNullOrWhiteSpace(request.Field) || !fields.ContainsKey(request.Field)))
            throw new BusinessRuleException("เลือกช่องที่จะตั้งค่าก่อน");

        var q = _db.ImportStagedRows.Where(r => r.SessionId == batchId && r.CompanyId == companyId
            && r.Status != ImportRowStatus.Committed && r.Status != ImportRowStatus.Undone);
        if (request.RowIds is { Count: > 0 } ids) q = q.Where(r => ids.Contains(r.Id));
        if (request.WhereStatus is ImportRowStatus ws) q = q.Where(r => r.Status == ws);
        var rows = await q.OrderBy(r => r.RowNo).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(request.WhereIssueCode))
            rows = rows.Where(r => Issues(r).Any(i => i.Code == request.WhereIssueCode)).ToList();

        var affected = new List<ImportStagedRow>();
        foreach (var r in rows)
        {
            var edit = request.Op switch
            {
                ImportBulkOp.ApplySuggestions => new ImportEditRowRequest(AcceptSuggestionFields: Issues(r)
                    .Where(i => i.Suggested != null && i.Field != null && (request.WhereIssueCode == null || i.Code == request.WhereIssueCode))
                    .Select(i => i.Field!).Distinct().ToList()),
                ImportBulkOp.RejectSuggestions => new ImportEditRowRequest(RejectSuggestionFields: Issues(r)
                    .Where(i => i.Suggested != null && i.Field != null && (request.WhereIssueCode == null || i.Code == request.WhereIssueCode))
                    .Select(i => i.Field!).Distinct().ToList()),
                ImportBulkOp.SetAction => new ImportEditRowRequest(Action: request.Action),
                ImportBulkOp.Exclude => new ImportEditRowRequest(Excluded: true),
                ImportBulkOp.Include => new ImportEditRowRequest(Excluded: false),
                ImportBulkOp.SetField => new ImportEditRowRequest(Cells: new Dictionary<string, string?> { [request.Field!] = request.Value }),
                _ => null,
            };
            if (edit == null) continue;
            if (request.Op is ImportBulkOp.ApplySuggestions or ImportBulkOp.RejectSuggestions
                && (edit.AcceptSuggestionFields?.Count ?? 0) + (edit.RejectSuggestionFields?.Count ?? 0) == 0) continue;
            ApplyRowEdit(r, edit, fields, state, target.SupportsUpdate);
            r.UserEdited = true;
            r.UpdatedBy = actor.UserId.ToString();
            affected.Add(r);
        }
        foreach (var chunk in affected.Chunk(WorkerChunkRows))
            await RevalidateAsync(session, target, state, chunk, actor, ct);
        await RefreshSnapshotAsync(session, persist: true, ct);
        await _db.SaveChangesAsync(ct);
        return new ImportBulkResultDto(affected.Count, await SummaryAsync(session, live: true, ct));
    }

    // ══════════════════════════════ ลงข้อมูล ══════════════════════════════

    public async Task<ImportCommitResultDto> CommitAsync(Guid companyId, Guid batchId, ImportCommitOptions options, ImportActor actor,
        CancellationToken ct = default)
    {
        var session = await LoadAsync(companyId, batchId, ct);
        switch (session.Status)
        {
            case ImportSessionStatus.Queued:
                throw new BusinessRuleException(State(session).PendingCommit != null
                    ? "ชุดนี้อยู่ในคิวลงข้อมูลแล้ว — รอระบบทำงานเบื้องหลังเสร็จ (ดูความคืบหน้าที่หน้านี้)"
                    : "ระบบยังวิเคราะห์ไฟล์ไม่เสร็จ — รอให้ความคืบหน้าครบ 100% แล้วยืนยันอีกครั้ง");
            case ImportSessionStatus.Committing:
                throw new BusinessRuleException("ชุดนี้กำลังลงข้อมูลอยู่ — รอสักครู่แล้วเปิดดูผล");
            case ImportSessionStatus.Completed:
                throw new BusinessRuleException("ชุดนี้ลงข้อมูลครบแล้ว");
            case ImportSessionStatus.Undone:
            case ImportSessionStatus.Cancelled:
                throw new BusinessRuleException("ชุดนี้ถูกย้อน/ยกเลิกแล้ว — อัปโหลดไฟล์เป็นชุดใหม่ถ้าต้องการนำเข้าอีกครั้ง");
        }
        var target = Target(session);
        var ack = new HashSet<string>(options.AcknowledgedWarningCodes ?? Array.Empty<string>(), StringComparer.Ordinal);
        var rows = await _db.ImportStagedRows.AsNoTracking()
            .Where(r => r.SessionId == batchId && r.CompanyId == companyId
                && r.Status != ImportRowStatus.Committed && r.Status != ImportRowStatus.Undone && r.Status != ImportRowStatus.Excluded)
            .Select(r => new { r.Id, r.Action, r.IssuesJson })
            .ToListAsync(ct);
        var blocked = rows.Where(r => r.Action != ImportRowAction.Skip && !Eligible(ParseIssues(r.IssuesJson), ack)).ToList();
        var eligible = rows.Count(r => r.Action != ImportRowAction.Skip && Eligible(ParseIssues(r.IssuesJson), ack));
        if (options.Mode == ImportCommitMode.AllOrNothing && blocked.Count > 0)
            throw new BusinessRuleException(
                $"โหมด \"ลงทั้งหมดหรือไม่ลงเลย\": ยังมี {blocked.Count} แถวที่ต้องแก้/ตัดออก/รับทราบคำเตือน — แก้แถวเหล่านั้นก่อน "
                + "หรือเลือกโหมด \"ลงเฉพาะแถวที่ผ่าน\"");
        if (eligible == 0)
            throw new BusinessRuleException(blocked.Count > 0
                ? $"ยังไม่มีแถวที่พร้อมลง — {blocked.Count} แถวต้องแก้ หรือต้องติ๊กรับทราบคำเตือนก่อน"
                : "ไม่มีแถวที่ต้องลงข้อมูล (ทุกแถวถูกตัดออก/ข้าม/ลงไปแล้ว)");

        // ฝ่ายค้านรอบ ETL (P0-1): ผู้ใช้สั่งลงใหม่ = ลองแถวที่ล้มรอบก่อนอีกครั้ง — คืนสถานะตามผลตรวจ (ระหว่างรอบลงเดียวกัน แถว Failed ไม่ถูกหยิบซ้ำ)
        var failedBefore = await _db.ImportStagedRows
            .Where(r => r.SessionId == batchId && r.CompanyId == companyId && r.Status == ImportRowStatus.Failed).ToListAsync(ct);
        foreach (var r in failedBefore)
        {
            var meta = ReadMeta(r.AiFeedbackIdsJson);
            r.Status = DeriveStatus(Issues(r), meta, meta.ContainsKey(MetaSummary));
            r.CommitError = null;
            r.UpdatedAt = DateTime.UtcNow;
        }

        var state = State(session);
        state.PendingCommit = options;
        state.PendingActor = actor;
        session.CommitMode = options.Mode;
        session.OptionsJson = ImportJson.Serialize(state);
        // ความคืบหน้าของขั้นลงข้อมูลเริ่มที่ 0 ทั้งทางในคำขอและทาง worker (เดิมทางในคำขอค้างค่าจากขั้นพัก ⇒ เกิน 100%)
        session.ProcessedRows = 0;
        if (eligible > InlineRowLimit)
        {
            // ไฟล์ใหญ่: worker ลงต่อ (claim + JobLock + heartbeat) — ผลดูได้จาก GET batches/{id}
            session.Status = ImportSessionStatus.Queued;
            session.Phase = ImportBatchPhase.Commit;
            session.HeartbeatAt = null;
            session.ClaimedBy = null;
            await _db.SaveChangesAsync(ct);
            return new ImportCommitResultDto(batchId, session.Status, SessionStatusLabel(session.Status), 0, 0, 0, Queued: true,
                Array.Empty<ImportRowFailureDto>(), null);
        }
        await _db.SaveChangesAsync(ct);

        ImportCommitResultDto? result = null;
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.ImportBatch, batchId.ToString(), async () =>
        {
            result = await CommitPendingAsync(companyId, batchId, target, int.MaxValue, ct);
        }, _logger, companyId, ct);
        if (!ran)
            throw new BusinessRuleException("ชุดนี้กำลังถูกลงข้อมูลโดยอีกงานหนึ่ง — รอสักครู่แล้วเปิดดูผลอีกครั้ง");
        return result!;
    }

    // ══════════════════════════════ ย้อน / ยกเลิก ══════════════════════════════

    public async Task<ImportUndoPreviewDto> PreviewUndoAsync(Guid companyId, Guid batchId, CancellationToken ct = default)
    {
        var session = await LoadAsync(companyId, batchId, ct, tracked: false);
        var target = Target(session);
        var committed = await CommittedRowsAsync(session, ct);
        var notes = new List<string>();
        if (committed.Count == 0)
        {
            notes.Add(session.SuccessCount > 0
                ? "รายละเอียดแถวของชุดนี้ถูกล้างตามระยะเก็บ 90 วันแล้ว — ย้อนรายการที่หน้าข้อมูลนั้นแทน"
                : "ชุดนี้ยังไม่ได้ลงข้อมูล — ไม่มีอะไรให้ย้อน (ใช้ \"ยกเลิก\" แทน)");
            return new ImportUndoPreviewDto(batchId, false, 0, 0, Array.Empty<ImportUndoItemDto>(), notes);
        }
        var checks = (await target.CanUndoAsync(Context(session, SystemActor(session), allowAi: false), committed, ct))
            .ToDictionary(c => c.RowId);
        var items = committed.Select(c =>
        {
            var chk = checks.GetValueOrDefault(c.RowId)
                ?? new ImportUndoCheck(c.RowId, false, ImportUndoKind.None, "ปลายทางนี้ไม่ได้ตอบว่าย้อนได้ไหม", "ย้อนที่หน้าข้อมูลนั้นแทน");
            return new ImportUndoItemDto(c.RowId, c.RowNo, c.DisplayKey, c.EntityType, c.EntityId,
                chk.Kind, UndoKindLabel(chk.Kind), !chk.CanUndo, chk.BlockReason, chk.WayForward);
        }).ToList();
        var reversible = items.Count(i => !i.Blocked);
        if (reversible < items.Count)
            notes.Add($"{items.Count - reversible} แถวย้อนไม่ได้ (ดูเหตุผลและทางไปต่อรายแถว) — ย้อนเฉพาะแถวที่ย้อนได้");
        return new ImportUndoPreviewDto(batchId, reversible > 0, reversible, items.Count - reversible, items, notes);
    }

    public async Task<ImportUndoResultDto> UndoAsync(Guid companyId, Guid batchId, string? reason, ImportActor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new BusinessRuleException("ระบุเหตุผลของการย้อนการนำเข้า (บันทึกลงประวัติการแก้ไข)");
        await LoadAsync(companyId, batchId, ct, tracked: false);
        ImportUndoResultDto? result = null;
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.ImportBatch, batchId.ToString(), async () =>
        {
            result = await UndoCoreAsync(companyId, batchId, reason.Trim(), actor, ct);
        }, _logger, companyId, ct);
        if (!ran)
            throw new BusinessRuleException("ชุดนี้กำลังถูกลงข้อมูล/ย้อนโดยอีกงานหนึ่ง — รอสักครู่แล้วลองใหม่");
        return result!;
    }

    public async Task<ImportBatchSummaryDto> CancelAsync(Guid companyId, Guid batchId, ImportActor actor, CancellationToken ct = default)
    {
        await LoadAsync(companyId, batchId, ct, tracked: false);
        // ฝ่ายค้านรอบ ETL: ยกเลิกต้องถือล็อกของ batch ตัวเดียวกับ worker/ลงข้อมูล/ย้อน แล้วอ่านสถานะใหม่ใต้ล็อก — เดิมยกเลิกแข่งกับ worker ที่กำลังลงข้อมูล
        // (ผู้ใช้เห็น "ยกเลิกแล้ว" ทั้งที่แถวยังลงต่อ)
        ImportBatchSummaryDto? result = null;
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.ImportBatch, batchId.ToString(), async () =>
        {
            result = await CancelCoreAsync(companyId, batchId, actor, ct);
        }, _logger, companyId, ct);
        if (!ran)
            throw new BusinessRuleException("ชุดนี้กำลังถูกประมวลผล/ลงข้อมูลโดยอีกงานหนึ่ง — รอสักครู่ แล้วกดยกเลิกอีกครั้ง (ถ้าลงไปแล้วให้ใช้ \"ย้อนการนำเข้า\")");
        return result!;
    }

    private async Task<ImportBatchSummaryDto> CancelCoreAsync(Guid companyId, Guid batchId, ImportActor actor, CancellationToken ct)
    {
        var session = await ReloadAsync(companyId, batchId, ct);
        if (session.Status is ImportSessionStatus.Committing)
            throw new BusinessRuleException("ชุดนี้กำลังลงข้อมูลอยู่ — รอให้เสร็จ แล้วใช้ \"ย้อนการนำเข้า\" ถ้าไม่ต้องการ");
        var committed = await CommittedCountAsync(session, ct);
        if (committed > 0)
            throw new BusinessRuleException($"ชุดนี้ลงข้อมูลไปแล้ว {committed} แถว — ใช้ \"ย้อนการนำเข้า\" แทนการยกเลิก");
        if (session.Status is ImportSessionStatus.Cancelled) return await SummaryAsync(session, live: true, ct);
        session.Status = ImportSessionStatus.Cancelled;
        session.CompletedAt = DateTime.UtcNow;
        session.ClaimedBy = null;
        session.UpdatedBy = actor.UserId.ToString();
        await _db.SaveChangesAsync(ct);
        return await SummaryAsync(session, live: true, ct);
    }

    // ══════════════════════════════ ไฟล์ ══════════════════════════════

    public async Task<byte[]> ExportErrorRowsCsvAsync(Guid companyId, Guid batchId, CancellationToken ct = default, bool revealPii = false)
    {
        var session = await LoadAsync(companyId, batchId, ct, tracked: false);
        var state = State(session);
        // PDPA ม.26 (ฝ่ายค้านรอบ ETL P1-5): ไม่มี Pii.View ⇒ ค่าดิบ/ข้อความปัญหาของช่องอ่อนไหวถูกปิดบัง (แก้ช่องอื่นแล้วนำเข้าได้ — ช่องที่ปิดไว้กรอกใหม่จากต้นฉบับ)
        var mask = !revealPii && ImportPiiFields.HasPii(session.EntityType);
        var colField = state.Columns.Where(c => c.SourceIndex >= 0).GroupBy(c => c.SourceIndex).ToDictionary(g => g.Key, g => g.First().TargetField);
        var rows = await _db.ImportStagedRows.AsNoTracking()
            .Where(r => r.SessionId == batchId && r.CompanyId == companyId
                && (r.Status == ImportRowStatus.Error || r.Status == ImportRowStatus.Failed))
            .OrderBy(r => r.RowNo).ToListAsync(ct);
        var width = Math.Max(state.Headers.Count, rows.Select(r => RawCells(r).Count).DefaultIfEmpty(0).Max());
        var headers = Enumerable.Range(0, width).Select(i => i < state.Headers.Count ? state.Headers[i] : $"คอลัมน์ {i + 1}").ToList();
        var sb = new StringBuilder();
        sb.AppendLine(CsvFieldSafety.Row(headers.Prepend("แถวที่").Append("ปัญหา")));
        foreach (var r in rows)
        {
            var raw = RawCells(r);
            var rowIssues = Issues(r);
            if (mask)
            {
                var values = ImportJson.ReadValues(r.MappedJson);
                rowIssues = rowIssues.Select(i => MaskIssue(i, session.EntityType, values, raw, colField)).ToList();
                raw = raw.Select((v, j) => ImportPiiFields.Mask(session.EntityType, colField.GetValueOrDefault(j), v) ?? "").ToList();
            }
            var problems = rowIssues.Where(i => i.Severity != ImportIssueSeverity.Info).Select(i => i.Message).ToList();
            if (!string.IsNullOrWhiteSpace(r.CommitError)) problems.Add(r.CommitError!);
            sb.AppendLine(CsvFieldSafety.Row(
                Enumerable.Range(0, width).Select(i => i < raw.Count ? raw[i] : "")
                    .Prepend(r.RowNo.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(string.Join(" · ", problems))));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public async Task<ImportSourceFileDto?> GetSourceFileAsync(Guid companyId, Guid batchId, CancellationToken ct = default)
    {
        // tenant isolation: CompanyId ทั้งตอนหา batch และตอนอ่านไฟล์ (ReadBytesAsync กรองซ้ำอีกชั้น)
        var batch = await _db.SmartImportSessions.AsNoTracking()
            .Where(s => s.Id == batchId && s.CompanyId == companyId)
            .Select(s => new { s.FileName, s.SourceFileAttachmentId })
            .FirstOrDefaultAsync(ct);
        if (batch?.SourceFileAttachmentId is not { } fileId) return null;
        var meta = await _files.GetByIdAsync(companyId, fileId);
        var bytes = await _files.ReadBytesAsync(companyId, fileId, ct);
        if (meta == null || bytes == null) return null;
        // ชื่อตามที่ผู้ใช้อัปโหลด (ไฟล์ .tsv/.ofx/.json ถูกเก็บด้วยนามสกุล .txt ตามรายการที่ที่เก็บไฟล์อนุญาต)
        return new ImportSourceFileDto(string.IsNullOrWhiteSpace(batch.FileName) ? meta.OriginalFileName : batch.FileName,
            string.IsNullOrWhiteSpace(meta.ContentType) ? "application/octet-stream" : meta.ContentType, bytes);
    }

    public async Task<ImportEntityCatalogueDto> GetEntityCatalogueAsync(Guid companyId, Guid userId, CancellationToken ct = default)
    {
        var entities = new List<ImportEntityInfoDto>();
        foreach (var t in OrderedTargets())
        {
            var key = ImportExportPermissionScope.ForImport(t.EntityType);
            // ด่านจริงอยู่ที่ controller — ที่นี่บอกหน้าเว็บให้ล็อกปุ่ม + บอกเหตุผล (ห้าม silent no-op) · ไม่มีบริการสิทธิ์ (เทสต์) = ไม่ล็อก
            var can = _permissions == null || await _permissions.HasPermissionAsync(companyId, userId, key);
            entities.Add(new ImportEntityInfoDto(t.EntityType, t.Label, null, key, can, t.Schema, t.CommitStyle, t.SupportsUpdate,
                SupportsUndo: true, SourceSystems: ImportSourceSeed.All
                    .Where(sp => ImportExportPermissionScope.NormalizeEntityType(sp.EntityType) == ImportExportPermissionScope.NormalizeEntityType(t.EntityType))
                    .Select(sp => sp.SourceSystem).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }
        var doneTypes = await _db.SmartImportSessions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && (s.Status == ImportSessionStatus.Completed || s.Status == ImportSessionStatus.PartiallyCompleted))
            .Select(s => s.EntityType).Distinct().ToListAsync(ct);
        var steps = new List<ImportMigrationStepDto>();
        var order = 1;
        foreach (var key in MigrationOrder)
        {
            var t = _targets.Find(key);
            if (t == null) continue;
            steps.Add(new ImportMigrationStepDto(order++, t.EntityType, t.Label, doneTypes.Contains(t.EntityType),
                doneTypes.Contains(t.EntityType) ? "นำเข้าแล้ว" : null));
        }
        steps.Add(new ImportMigrationStepDto(order, "reconciliation", "กระทบยอด (ลูกหนี้/เจ้าหนี้ · สต็อก · สินทรัพย์ กับงบทดลองยกมา)", false,
            "ทำหลังนำเข้าครบทุกขั้น — ยอดย่อยต้องตรงยอดในงบทดลอง"));
        return new ImportEntityCatalogueDto(entities, steps);
    }

    /// <summary>ลำดับย้ายระบบ (§2.10) — คีย์ที่ไม่มี target ลงทะเบียน (ทีมอื่นยังไม่รวม) ถูกข้าม</summary>
    private static readonly string[] MigrationOrder =
    {
        "chart-of-accounts", "contacts", "products", "opening-balances", "opening-ar", "opening-ap", "stock-opening", "fixed-assets",
    };

    private IEnumerable<IImportTarget> OrderedTargets()
    {
        var ordered = MigrationOrder.Select(k => _targets.Find(k)).Where(t => t != null).Select(t => t!).ToList();
        return ordered.Concat(_targets.All.Where(t => !ordered.Contains(t)).OrderBy(t => t.Label, StringComparer.Ordinal));
    }

    // ══════════════════════════════ worker ══════════════════════════════

    public async Task<bool> ProcessQueuedAsync(Guid companyId, Guid batchId, string workerId, CancellationToken ct = default)
    {
        var session = await LoadAsync(companyId, batchId, ct);
        var state = State(session);
        try
        {
            if (session.Status == ImportSessionStatus.Queued && session.Phase != ImportBatchPhase.Commit)
                return await StageQueuedChunkAsync(session, workerId, ct);
            if ((session.Status == ImportSessionStatus.Queued && session.Phase == ImportBatchPhase.Commit)
                || session.Status == ImportSessionStatus.Committing)
            {
                if (state.PendingCommit == null)
                {
                    await FinishCommitAsync(companyId, batchId, Target(session), ct);
                    return true;
                }
                // AllOrNothing ต้องอยู่ธุรกรรมเดียว ⇒ ทำทั้งชุดในรอบเดียว · GoodRowsOnly ทีละ chunk (heartbeat ระหว่างรอบ)
                var limit = state.PendingCommit.Mode == ImportCommitMode.AllOrNothing ? int.MaxValue : WorkerChunkRows;
                var res = await CommitPendingAsync(companyId, batchId, Target(session), limit, ct);
                return !res.Queued;
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ล้มดัง 3 ที่: log + สถานะ batch + หมายเหตุที่ผู้ใช้เห็น (F2 ข้อ 7)
            _logger?.LogError(ex, "ImportBatch {BatchId}: งานเบื้องหลังล้ม", batchId);
            _db.ChangeTracker.Clear();
            var s = await LoadAsync(companyId, batchId, ct);
            var st = State(s);
            st.Notes.Add($"งานเบื้องหลังล้ม ({DateTime.UtcNow.AddHours(7):dd/MM HH:mm}): {ex.Message}");
            st.PendingCommit = null;
            s.OptionsJson = ImportJson.Serialize(st);
            s.Status = await CommittedCountAsync(s, ct) > 0 ? ImportSessionStatus.PartiallyCompleted : ImportSessionStatus.Failed;
            s.Phase = ImportBatchPhase.Review;
            s.ClaimedBy = null;
            s.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return true;
        }
    }
}
