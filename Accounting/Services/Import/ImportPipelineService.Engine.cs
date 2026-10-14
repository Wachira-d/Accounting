using System.Globalization;
using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Import;

/// <summary>คีย์ใน <see cref="ImportTargetContext.BatchOptions"/> ที่ pipeline เติมเอง (นอกเหนือจาก Options ที่ผู้ใช้ส่งตอนสร้าง batch)</summary>
public static class ImportBatchOptionKeys
{
    /// <summary>"false" = ห้ามถาม AI ในรอบนี้ (worker ไฟล์ใหญ่ · ผู้ใช้ปิด) — ทุกชั้นใต้ AI ยังทำงานเหมือนเดิม (kill-switch)</summary>
    public const string AllowAi = "allowAi";
}

/// <summary>
/// สถานะภายในของ batch — เก็บใน <c>SmartImportSession.OptionsJson</c> ผ่าน <see cref="ImportJson"/> (owner: pipeline ทีม B) ·
/// แผนจับคู่คอลัมน์ · แถวหัวตาราง · คำสั่ง commit ที่รอ worker · หมายเหตุที่ผู้ใช้เห็น
/// </summary>
internal sealed class ImportBatchState
{
    public ImportDateOrder DateOrder { get; set; } = ImportDateOrder.Auto;
    public Guid? TemplateId { get; set; }
    public Dictionary<string, string?> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HeaderChosenByUser { get; set; }
    public int HeaderRowIndex { get; set; }
    public int HeaderRowSpan { get; set; } = 1;
    public decimal HeaderConfidence { get; set; }
    public string? HeaderReason { get; set; }
    public bool ColumnsChosenByUser { get; set; }
    public List<string> Headers { get; set; } = new();
    public List<ImportColumnPlanDto> Columns { get; set; } = new();
    public int DataStartIndex { get; set; }
    public int DataRowCount { get; set; }
    public int SummaryRows { get; set; }
    public ImportCommitOptions? PendingCommit { get; set; }
    public ImportActor? PendingActor { get; set; }
    public List<string> Notes { get; set; } = new();
}

/// <summary>สรุปทั้งชุด (ReviewSummaryJson) — คงไว้แม้แถวพักถูกล้างหลัง 90 วัน</summary>
internal sealed class ImportReviewSnapshot
{
    public Dictionary<string, int> StatusCounts { get; set; } = new();
    public int SkipActionCount { get; set; }
    public List<ImportIssueCountDto> IssueCounts { get; set; } = new();
    public List<ImportWarningAckDto> Warnings { get; set; } = new();
    public int Count(ImportRowStatus s) => StatusCounts.TryGetValue(s.ToString(), out var n) ? n : 0;
}

public partial class ImportPipelineService
{
    private sealed record SourceRow(int RowNo, IReadOnlyList<string> Raw, bool Summary);

    private sealed record ColumnMapping(List<ImportColumnPlanDto> Columns, bool UsedAi, Guid? FeedbackId, Guid? TemplateId, string? SourceSystem);

    // ══════════════════════════════ โหลด / สถานะ ══════════════════════════════

    private async Task<SmartImportSession> LoadAsync(Guid companyId, Guid batchId, CancellationToken ct, bool tracked = true)
    {
        var q = _db.SmartImportSessions.Where(s => s.Id == batchId && s.CompanyId == companyId);
        if (!tracked) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException("ไม่พบชุดนำเข้านี้");
    }

    /// <summary>อ่าน session ใหม่จากฐาน (ทิ้งตัวที่ติดตามอยู่ — ค่าที่ ExecuteUpdate/ธุรกรรมอื่นเปลี่ยนไปต้องเห็น) · ผู้เรียกบันทึกของค้างก่อนเสมอ</summary>
    private async Task<SmartImportSession> ReloadAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        foreach (var e in _db.ChangeTracker.Entries<SmartImportSession>().Where(e => e.Entity.Id == batchId).ToList())
            e.State = EntityState.Detached;
        return await LoadAsync(companyId, batchId, ct);
    }

    private static ImportBatchState State(SmartImportSession s)
        => ImportJson.Deserialize<ImportBatchState>(s.OptionsJson) ?? new ImportBatchState();

    private IImportTarget Target(SmartImportSession s)
        => _targets.Find(s.EntityType) ?? throw new BusinessRuleException(
            $"ยังไม่รองรับการนำเข้าข้อมูลชนิด \"{s.EntityType}\" ที่ศูนย์นำเข้า — ติดต่อผู้ดูแลระบบ");

    private static Dictionary<string, ImportFieldDto> FieldMap(IImportTarget t)
        => t.Schema.GroupBy(f => f.Field, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static void EnsureEditable(SmartImportSession s, string what)
    {
        switch (s.Status)
        {
            case ImportSessionStatus.Queued:
                throw new BusinessRuleException($"ระบบยังประมวลผลชุดนี้อยู่ — รอให้เสร็จก่อน{what}");
            case ImportSessionStatus.Committing:
                throw new BusinessRuleException($"ชุดนี้กำลังลงข้อมูลอยู่ — {what}ไม่ได้ระหว่างนี้");
            case ImportSessionStatus.Completed:
            case ImportSessionStatus.Undone:
            case ImportSessionStatus.Cancelled:
                throw new BusinessRuleException($"ชุดนี้ปิดแล้ว ({SessionStatusLabel(s.Status)}) — อัปโหลดเป็นชุดใหม่ถ้าต้องการ{what}");
        }
    }

    private Task<int> CommittedCountAsync(SmartImportSession s, CancellationToken ct)
        => _db.ImportStagedRows.CountAsync(r => r.SessionId == s.Id && r.CompanyId == s.CompanyId && r.Status == ImportRowStatus.Committed, ct);

    private static string? Trunc(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];

    private ImportActor SystemActor(SmartImportSession s) => State(s).PendingActor ?? new ImportActor(Guid.Empty, "ระบบนำเข้า");

    private ImportTargetContext Context(SmartImportSession s, ImportActor actor, bool allowAi)
    {
        var state = State(s);
        var opts = new Dictionary<string, string?>(state.Options, StringComparer.OrdinalIgnoreCase)
        {
            [ImportBatchOptionKeys.AllowAi] = allowAi ? "true" : "false",
        };
        return new ImportTargetContext(s.CompanyId, s.Id, s.EntityType, actor, state.PendingCommit ?? new ImportCommitOptions(),
            s.SourceSystem, opts);
    }

    // ══════════════════════════════ Extract ══════════════════════════════

    private async Task<List<IReadOnlyList<string>>> ReadRowsAsync(SmartImportSession s, CancellationToken ct)
    {
        if (s.SourceFileAttachmentId is not Guid fileId)
            throw new BusinessRuleException("ชุดนี้ไม่มีไฟล์ต้นทาง — อัปโหลดไฟล์ใหม่");
        var bytes = await _files.ReadBytesAsync(s.CompanyId, fileId, ct)
            ?? throw new BusinessRuleException("ไม่พบไฟล์ต้นทางของชุดนี้ (อาจถูกลบ) — อัปโหลดไฟล์ใหม่");
        return ImportTabularReader.Open(s.FileName, bytes, s.SheetName).Rows.ToList();
    }

    /// <summary>ไฟล์ที่ที่เก็บไฟล์รับ (รายการนามสกุลของ FileAttachmentService) — .tsv/.ofx/.json ฯลฯ เก็บเป็น .txt (ชื่อจริงอยู่ที่ batch · ดาวน์โหลดได้ชื่อเดิม)</summary>
    private static string StorageFileName(string fileName, ImportFileKind kind)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (kind == ImportFileKind.Xlsx) return ext == ".xlsx" ? fileName : Path.GetFileNameWithoutExtension(fileName) + ".xlsx";
        return ext is ".csv" or ".txt" ? fileName : fileName + ".txt";
    }

    private static string StorageContentType(string? given, ImportFileKind kind)
        => kind == ImportFileKind.Xlsx ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            : given != null && given.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? given
            : "text/plain";

    private static List<SourceRow> DataRows(IReadOnlyList<IReadOnlyList<string>> rows, ImportBatchState state)
    {
        var list = new List<SourceRow>();
        for (var i = state.DataStartIndex; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            list.Add(new SourceRow(i - state.DataStartIndex + 1, r, ImportHeaderDetector.IsSummaryRow(r)));
        }
        return list;
    }

    /// <summary>คำพ้องของทุก field ของ target — ตัวตั้งเดียว <see cref="ImportFieldSynonymSeed"/> (ทีม A · ใช้หาแถวหัวตาราง)</summary>
    private static Dictionary<string, IReadOnlyList<string>> Synonyms(IImportTarget t)
        => t.Schema.ToDictionary(f => f.Field, f => ImportFieldSynonymSeed.For(t.EntityType, f.Field), StringComparer.OrdinalIgnoreCase);

    /// <summary>คะแนนเดาชนิดข้อมูลขั้นต่ำ + ระยะห่างจากอันดับสอง — ต่ำกว่านี้ให้ผู้ใช้เลือก (ไม่เดาส่ง)</summary>
    private const decimal GuessMinScore = 0.6m;
    private const decimal GuessMinGap = 0.05m;

    /// <summary>ไม่ได้เลือกชนิดข้อมูล ⇒ เดาจากหัวตารางด้วย <see cref="ImportColumnMapper.GuessEntityType"/> (ทีม A · ตัวเดียวกับ chip บนหน้า) เฉพาะชนิดที่
    /// ศูนย์นำเข้ามีปลายทาง · ต้องชนะชัดเจน — เดาไม่ได้ = ให้ผู้ใช้เลือก (ไม่เดาส่ง)</summary>
    private IImportTarget GuessTarget(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        // แถวหัวตาราง: ผลที่มั่นใจสุดข้ามทุกชนิด (เสมอ = แถวบนสุด — ไม่ขึ้นกับลำดับลงทะเบียน target)
        var det = _targets.All.Select(t => ImportHeaderDetector.Detect(rows, t.Schema, Synonyms(t)))
            .OrderByDescending(d => d.Confidence).ThenBy(d => d.HeaderRowIndex).FirstOrDefault();
        var guesses = new List<(ImportEntityGuess Guess, IImportTarget Target)>();
        if (det != null)
            foreach (var g in ImportColumnMapper.GuessEntityType(ColumnInputs(det.Headers,
                         DataRows(rows, new ImportBatchState { DataStartIndex = det.HeaderRowIndex + det.HeaderRowSpan }))))
                if (_targets.Find(g.EntityType) is { } t)
                    guesses.Add((g, t));
        if (guesses.Count == 0 || guesses[0].Guess.Score < GuessMinScore
            || (guesses.Count > 1 && guesses[0].Guess.Score - guesses[1].Guess.Score < GuessMinGap))
            throw new BusinessRuleException("ระบบเดาชนิดข้อมูลของไฟล์นี้ไม่ได้ — เลือกชนิดข้อมูล (เช่น ผู้ติดต่อ/สินค้า/ผังบัญชี) แล้วอัปโหลดอีกครั้ง");
        return guesses[0].Target;
    }

    /// <summary>คอลัมน์ของไฟล์ในรูปที่ตัวจับคู่ของทีม A รับ — ตัวอย่างค่า 5 ค่าแรกที่ไม่ว่าง (ไม่นับแถวยอดรวม)</summary>
    private static List<ImportColumnInput> ColumnInputs(IReadOnlyList<string> headers, IReadOnlyList<SourceRow> data)
        => headers.Select((h, j) => new ImportColumnInput(j, h, data.Where(d => !d.Summary)
                .Select(d => j < d.Raw.Count ? d.Raw[j] : "").Where(v => !string.IsNullOrWhiteSpace(v)).Take(5).ToList()))
            .ToList();

    // ══════════════════════════════ Transform + Stage ══════════════════════════════

    /// <summary>หาแถวหัวตาราง → จับคู่คอลัมน์ → (ไฟล์เล็ก) แปลง+ตรวจ+พักทุกแถว · (ไฟล์ใหญ่) เข้าคิว worker</summary>
    /// <param name="structural">เปลี่ยนชนิดข้อมูล/ชีต/แถวหัวตาราง ⇒ แถวพักเดิมใช้ไม่ได้ (ล้าง · บอกผู้ใช้ถ้ามีแถวที่แก้ไว้)</param>
    /// <param name="keepColumns">ผู้ใช้เพิ่งจับคู่คอลัมน์เอง (RemapAsync) — ไม่จับคู่ใหม่</param>
    private async Task PrepareAndStageAsync(SmartImportSession session, IImportTarget target, IReadOnlyList<IReadOnlyList<string>> rows,
        bool structural, bool allowAi, ImportActor actor, CancellationToken ct, bool keepColumns = false)
    {
        var state = State(session);
        HeaderDetection det;
        if (state.HeaderChosenByUser && rows.Count > 0)
        {
            var idx = Math.Min(state.HeaderRowIndex, rows.Count - 1);
            det = new HeaderDetection(idx, state.HeaderRowSpan, ImportHeaderDetector.MergeHeaders(rows, idx, state.HeaderRowSpan), 1m,
                $"ผู้ใช้เลือกแถวที่ {idx + 1} เป็นหัวตาราง");
        }
        else det = ImportHeaderDetector.Detect(rows, target.Schema, Synonyms(target));
        state.HeaderRowIndex = det.HeaderRowIndex;
        state.HeaderRowSpan = det.HeaderRowSpan;
        state.HeaderConfidence = det.Confidence;
        state.HeaderReason = det.Reason;
        state.Headers = det.Headers.ToList();
        state.DataStartIndex = det.HeaderRowIndex + det.HeaderRowSpan;
        var data = DataRows(rows, state);
        state.DataRowCount = data.Count;
        state.SummaryRows = data.Count(d => d.Summary);
        session.HeaderRowIndex = det.HeaderRowIndex;
        session.HeaderRowSpan = det.HeaderRowSpan;
        session.HeaderSignature = ImportHeaderSignature.Compute(state.Headers);

        if (!keepColumns && (structural || !state.ColumnsChosenByUser || state.Columns.Count == 0))
        {
            var mapped = await MapColumnsAsync(session, target, state, data, allowAi, ct);
            state.Columns = mapped.Columns;
            state.ColumnsChosenByUser = false;
            session.ColumnMatchUsedAi = mapped.UsedAi;
            session.ColumnMatchAiFeedbackId = mapped.FeedbackId;
            session.MappingTemplateId = mapped.TemplateId;
            // โปรแกรมต้นทาง: ผู้ใช้เลือก ชนะ · ไม่เลือก = โปรไฟล์ที่ตัวจับคู่ตรวจพบ (ImportSourceSeed — ป้าย "รูปแบบ X" บนหน้า)
            session.SourceSystem ??= Trunc(mapped.SourceSystem, 64);
        }

        if (structural)
        {
            var edited = await _db.ImportStagedRows.CountAsync(r => r.SessionId == session.Id && r.CompanyId == session.CompanyId && r.UserEdited, ct);
            if (edited > 0)
                state.Notes.Add($"เปลี่ยนชนิดข้อมูล/ชีต/แถวหัวตาราง ⇒ แถวที่แก้ไว้ {edited} แถวถูกล้าง (อ่านจากไฟล์ใหม่ทั้งหมด)");
            await _db.ImportStagedRows.Where(r => r.SessionId == session.Id && r.CompanyId == session.CompanyId).ExecuteDeleteAsync(ct);
        }

        session.TotalRows = data.Count;
        session.ProcessedRows = 0;
        session.OptionsJson = ImportJson.Serialize(state);
        await SyncColumnMappingRowsAsync(session, state, ct);
        if (data.Count <= InlineRowLimit)
        {
            session.Status = ImportSessionStatus.Analyzing;
            session.Phase = ImportBatchPhase.Transform;
            await _db.SaveChangesAsync(ct);
            foreach (var chunk in data.Chunk(WorkerChunkRows))
            {
                await StageChunkAsync(session, target, state, chunk, allowAi, actor, ct);
                session.ProcessedRows += chunk.Length;
            }
            await FinishStagingAsync(session, ct);
        }
        else
        {
            // ไฟล์ใหญ่: worker แปลง/ตรวจทีละ 500 แถว (ไม่ถาม AI — ทุกชั้นใต้ AI ทำงานครบ) · ผู้ใช้ดูความคืบหน้าได้
            session.Status = ImportSessionStatus.Queued;
            session.Phase = ImportBatchPhase.Transform;
            session.ClaimedBy = null;
            session.HeartbeatAt = null;
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task<bool> StageQueuedChunkAsync(SmartImportSession session, string workerId, CancellationToken ct)
    {
        var state = State(session);
        var target = Target(session);
        var data = DataRows(await ReadRowsAsync(session, ct), state);
        var chunk = data.Skip(session.ProcessedRows).Take(WorkerChunkRows).ToList();
        if (chunk.Count > 0)
            await StageChunkAsync(session, target, state, chunk, allowAi: false, SystemActor(session), ct);
        session.ProcessedRows += chunk.Count;
        session.HeartbeatAt = DateTime.UtcNow;
        session.ClaimedBy = Trunc(workerId, 100);
        if (session.ProcessedRows >= data.Count)
        {
            await FinishStagingAsync(session, ct);
            return true;
        }
        await _db.SaveChangesAsync(ct);
        return false;
    }

    private async Task FinishStagingAsync(SmartImportSession session, CancellationToken ct)
    {
        session.Status = ImportSessionStatus.Staged;
        session.Phase = ImportBatchPhase.Review;
        session.ProcessedRows = session.TotalRows;
        session.ClaimedBy = null;
        session.HeartbeatAt = null;
        await _db.SaveChangesAsync(ct);
        await RefreshSnapshotAsync(session, persist: true, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task StageChunkAsync(SmartImportSession session, IImportTarget target, ImportBatchState state, IReadOnlyList<SourceRow> chunk,
        bool allowAi, ImportActor actor, CancellationToken ct)
    {
        var rowNos = chunk.Select(c => c.RowNo).ToList();
        var existing = await _db.ImportStagedRows
            .Where(r => r.SessionId == session.Id && r.CompanyId == session.CompanyId && rowNos.Contains(r.RowNo))
            .ToDictionaryAsync(r => r.RowNo, ct);
        var items = new List<(SourceRow Src, ImportStagedRow Row)>();
        foreach (var src in chunk)
        {
            if (!existing.TryGetValue(src.RowNo, out var row))
            {
                row = new ImportStagedRow
                {
                    CompanyId = session.CompanyId,
                    SessionId = session.Id,
                    RowNo = src.RowNo,
                    RawJson = ImportJson.Serialize(src.Raw),
                    CreatedBy = actor.UserId.ToString(),
                };
                _db.ImportStagedRows.Add(row);
            }
            else if (row.Status is ImportRowStatus.Committed or ImportRowStatus.Undone) continue;
            items.Add((src, row));
        }
        await TransformValidateAsync(session, target, state, items, allowAi, actor, ct);
    }

    /// <summary>ตรวจแถวที่พักแล้วใหม่ (หลังผู้ใช้แก้) — อ่านค่าดิบจาก RawJson แล้วเดินเส้นเดียวกับตอนพัก (ค่าที่ผู้ใช้แก้ชนะ)</summary>
    private Task RevalidateAsync(SmartImportSession session, IImportTarget target, ImportBatchState state, IReadOnlyList<ImportStagedRow> rows,
        ImportActor actor, CancellationToken ct)
        => TransformValidateAsync(session, target, state,
            rows.Select(r => (new SourceRow(r.RowNo, RawCells(r), ReadMeta(r.AiFeedbackIdsJson).ContainsKey(MetaSummary)), r)).ToList(),
            allowAi: true, actor, ct);

    /// <summary>หัวใจของขั้น Transform: แปลงค่า (ImportCellNormalizer) → ค่าที่ผู้ใช้แก้ทับ → target ตรวจกฎธุรกิจ + จับคู่ข้อมูลเดิม → คะแนน/สถานะ</summary>
    private async Task TransformValidateAsync(SmartImportSession session, IImportTarget target, ImportBatchState state,
        IReadOnlyList<(SourceRow Src, ImportStagedRow Row)> items, bool allowAi, ImportActor actor, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var fields = FieldMap(target);
        var work = new List<(ImportStagedRow Row, Dictionary<string, string?> Values, List<ImportIssueDto> Issues, Dictionary<string, string?> Meta, bool Summary)>();
        foreach (var (src, row) in items)
        {
            var meta = ReadMeta(row.AiFeedbackIdsJson);
            var (values, issues) = Transform(state, fields, src.Raw);
            var edited = MetaSet(meta, MetaEdited);
            if (edited.Count > 0)
            {
                var prev = ImportJson.ReadValues(row.MappedJson);
                foreach (var f in edited)
                {
                    values[f] = prev.GetValueOrDefault(f);
                    issues.RemoveAll(i => string.Equals(i.Field, f, StringComparison.OrdinalIgnoreCase));
                }
            }
            if (src.Summary) meta[MetaSummary] = "1"; else meta.Remove(MetaSummary);
            work.Add((row, values, issues, meta, src.Summary));
        }

        var targetRows = work.Select(w => new ImportTargetRow(w.Row.Id, w.Row.RowNo, w.Values, w.Row.Action, w.Row.MatchedEntityId,
            w.Row.GroupKey, w.Row.DisplayKey)).ToList();
        var validations = (await target.ValidateAsync(Context(session, actor, allowAi), targetRows, ct))
            .GroupBy(v => v.RowId).ToDictionary(g => g.Key, g => g.First());

        foreach (var w in work)
        {
            var issues = w.Issues;
            var meta = w.Meta;
            var edited = MetaSet(meta, MetaEdited);
            var filled = new List<string>();
            var userAction = meta.ContainsKey(MetaUserAction);
            if (validations.TryGetValue(w.Row.Id, out var v))
            {
                if (v.FilledValues != null)
                    foreach (var kv in v.FilledValues)
                    {
                        if (edited.Contains(kv.Key) || !string.IsNullOrWhiteSpace(w.Values.GetValueOrDefault(kv.Key))) continue;
                        w.Values[kv.Key] = kv.Value;
                        filled.Add(kv.Key);
                        // target รับช่วงช่องนี้แล้ว (เติมค่าให้) ⇒ ปัญหาจากตัวแปลงของช่องนี้ไม่ใช่ปัญหาอีก — target แจ้งคำเตือนของตัวเองแทน
                        issues.RemoveAll(i => i.Source == ImportIssueSource.Normalizer && string.Equals(i.Field, kv.Key, StringComparison.OrdinalIgnoreCase));
                    }
                issues.AddRange(v.Issues);
                if (!userAction)
                {
                    w.Row.Action = v.Action ?? ImportRowAction.Create;
                    w.Row.MatchedEntityId = v.MatchedEntityId;
                    w.Row.MatchKind = Trunc(v.MatchKind, 32);
                    w.Row.MatchScore = v.MatchScore is decimal ms ? Math.Round(Math.Clamp(ms, 0m, 1m), 4, MidpointRounding.AwayFromZero) : null;
                    if (v.MatchAutoApplied) meta[MetaMatchAuto] = "1"; else meta.Remove(MetaMatchAuto);
                }
                if (v.MatchUsedAi) meta[MetaMatchUsedAi] = "1"; else meta.Remove(MetaMatchUsedAi);
                if (v.MatchAiFeedbackId is Guid fb && !meta.ContainsKey(MetaMatchFeedback))
                {
                    meta[MetaMatchFeedback] = fb.ToString();
                    meta[MetaMatchAi] = v.MatchedEntityId?.ToString() ?? ImportEntityResolver.NewEntityAnswer;
                }
                if (v.MatchCandidates is { Count: > 0 } cands) meta[MetaCandidates] = ImportJson.Serialize(cands);
                else meta.Remove(MetaCandidates);
                w.Row.DisplayKey = Trunc(v.DisplayKey, 300);
                w.Row.GroupKey = Trunc(v.GroupKey, 200);
            }
            if (w.Summary)
                issues.Add(new ImportIssueDto(null, "summary-row", ImportIssueSeverity.Info,
                    "ระบบตัดแถวนี้ออกเพราะดูเหมือนแถวยอดรวม/ยกมา-ยกไปของรายงาน — กด \"นำกลับ\" ถ้าเป็นข้อมูลจริง", Source: ImportIssueSource.Rule));
            var rejected = MetaSet(meta, MetaRejected);
            if (rejected.Count > 0)
                for (var i = 0; i < issues.Count; i++)
                    if (issues[i].Suggested != null && issues[i].Field != null && rejected.Contains(issues[i].Field!))
                        issues[i] = issues[i] with { Suggested = null };

            SetMetaSet(meta, MetaFilled, filled);
            w.Row.MappedJson = ImportJson.Serialize(w.Values);
            if (!w.Row.UserEdited) w.Row.OriginalMappedJson = w.Row.MappedJson;
            w.Row.IssuesJson = ImportJson.Serialize(issues);
            var matchForScore = meta.ContainsKey(MetaMatchAuto) ? null : (w.Row.Action is ImportRowAction.Update or ImportRowAction.Merge ? w.Row.MatchScore : null);
            w.Row.Confidence = ImportRowConfidence.Compute(MappingConfidence(state, w.Values), issues, matchForScore, filled.Count);
            w.Row.Status = DeriveStatus(issues, meta, w.Summary);
            w.Row.AiFeedbackIdsJson = WriteMeta(meta);
            w.Row.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>ค่าดิบหนึ่งแถว → ค่ารูปมาตรฐานต่อ field + issue ของตัวแปลง (ตามแผนจับคู่คอลัมน์ · transform ต่อคอลัมน์)</summary>
    private static (Dictionary<string, string?> Values, List<ImportIssueDto> Issues) Transform(ImportBatchState state,
        IReadOnlyDictionary<string, ImportFieldDto> fields, IReadOnlyList<string> raw)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<ImportIssueDto>();
        foreach (var col in state.Columns)
        {
            if (col.TargetField == null || !fields.TryGetValue(col.TargetField, out var f)) continue;
            var rawVal = col.SourceIndex < raw.Count ? raw[col.SourceIndex] : null;
            var order = col.Transform?.DateOrder ?? state.DateOrder;
            var r = ImportCellNormalizer.Normalize(f, rawVal, order, col.Transform?.ValueMap);
            var value = r.Cell.Value;
            if (col.Transform?.Negate == true && value != null && f.Type is ImportFieldType.Amount or ImportFieldType.Decimal
                && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var num))
                value = (-num).ToString(CultureInfo.InvariantCulture);
            if (f.Type == ImportFieldType.VatRate && r.Issue?.Code == "vat-exempt") value = VatExemptToken;
            values[f.Field] = value;
            if (r.Issue != null) issues.Add(r.Issue);
        }
        foreach (var f in fields.Values.Where(f => f.Required && !values.ContainsKey(f.Field)))
        {
            values[f.Field] = null;
            issues.Add(new ImportIssueDto(f.Field, "required-missing", ImportIssueSeverity.Error,
                $"ไฟล์ไม่มีคอลัมน์ \"{f.Label}\" ซึ่งต้องมี — จับคู่คอลัมน์ให้ช่องนี้ หรือกรอกค่าในแถว", Source: ImportIssueSource.Normalizer));
        }
        return (values, issues);
    }

    private static decimal MappingConfidence(ImportBatchState state, IReadOnlyDictionary<string, string?> values)
    {
        var used = state.Columns.Where(c => c.TargetField != null && !string.IsNullOrWhiteSpace(values.GetValueOrDefault(c.TargetField))).ToList();
        return used.Count == 0 ? 1m : used.Average(c => c.Confidence);
    }

    private static ImportRowStatus DeriveStatus(IReadOnlyList<ImportIssueDto> issues, IReadOnlyDictionary<string, string?> meta, bool summary)
    {
        if (meta.ContainsKey(MetaExcluded)) return ImportRowStatus.Excluded;
        if (summary && !meta.ContainsKey(MetaIncluded)) return ImportRowStatus.Excluded;
        if (issues.Any(i => i.Severity == ImportIssueSeverity.Error)) return ImportRowStatus.Error;
        if (issues.Any(i => i.Severity == ImportIssueSeverity.Warning)) return ImportRowStatus.Warning;
        return ImportRowStatus.Valid;
    }

    // ══════════════════════════════ จับคู่คอลัมน์ ══════════════════════════════

    /// <summary>
    /// จับคู่คอลัมน์ทั้งไฟล์ — ทางเข้าเดียว <see cref="IImportColumnMatchService.MapAsync"/> (ทีม A: แม่แบบ → ชื่อตรง → คำพ้อง/โปรไฟล์โปรแกรมต้นทาง →
    /// คู่ที่บริษัทยืนยันแล้ว → รูปข้อมูล → AI <c>ImportColumnMatch</c> ผ่าน orchestrator เฉพาะคอลัมน์ที่ยังไม่มั่นใจ) · schema = ของ target จริง
    /// (<see cref="ImportColumnMatchOptions.Targets"/> — ปลายทางบางตัวมี field ต่างจากแคตตาล็อกกลาง) · ไม่มีบริการ (เทสต์/ไม่ได้ลงทะเบียน) ⇒
    /// ชั้น local ล้วนของตัวจับคู่ตัวเดียวกัน (<see cref="ImportColumnMapper.Resolve"/>) — ผลเหมือน AI ปิด (kill-switch)
    /// </summary>
    private async Task<ColumnMapping> MapColumnsAsync(SmartImportSession session, IImportTarget target, ImportBatchState state,
        IReadOnlyList<SourceRow> data, bool allowAi, CancellationToken ct)
    {
        var inputs = ColumnInputs(state.Headers, data);
        if (_columnMatch == null)
        {
            var local = ImportColumnMapper.Resolve(new ImportColumnMapRequest(target.EntityType, target.Schema, inputs,
                SourceSystem: session.SourceSystem));
            return new ColumnMapping(local.Columns.ToList(), UsedAi: false, FeedbackId: null, TemplateId: null,
                local.SourceProfile?.Profile.SourceSystem);
        }
        var outcome = await _columnMatch.MapAsync(session.CompanyId, target.EntityType, inputs,
            new ImportColumnMatchOptions(session.Id, state.TemplateId, session.SourceSystem, allowAi, Targets: target.Schema), ct);
        return new ColumnMapping(outcome.Plan.Columns.ToList(), outcome.UsedAi, outcome.AiFeedbackId, outcome.TemplateId,
            outcome.Plan.SourceProfile?.Profile.SourceSystem);
    }

    /// <summary>ตารางจับคู่เดิมของ Smart Import (SmartImportColumnMappings) ตามแผนปัจจุบัน — แปลงแถวด้วยตัวเดียวกับ Smart Import
    /// (<see cref="ImportExportService.ToColumnMappingRow"/> · ป้าย MatchType ซื่อสัตย์) · คลังแม่แบบ (<c>SaveFromBatchAsync</c>) อ่านจากตารางนี้</summary>
    private async Task SyncColumnMappingRowsAsync(SmartImportSession session, ImportBatchState state, CancellationToken ct)
    {
        await _db.SmartImportColumnMappings.Where(m => m.SessionId == session.Id).ExecuteDeleteAsync(ct);
        _db.SmartImportColumnMappings.AddRange(state.Columns.Select(c => ImportExportService.ToColumnMappingRow(session.Id, c)));
    }

    /// <summary>ปิดลูป ImportColumnMatch (กฎเหล็ก #1) — แผนสุดท้ายของผู้ใช้ (remap/commit) ⇒ <see cref="IImportColumnMatchService.RecordConfirmedAsync"/>
    /// (ทีม A: บันทึกคำตอบรูปมาตรฐาน + acceptedAi + ให้นักเรียนเรียนทันที) · ไม่มีแถว feedback = ไม่มีอะไรต้องปิด</summary>
    private async Task RecordColumnChoiceAsync(SmartImportSession session, ImportBatchState state, CancellationToken ct)
    {
        if (_columnMatch == null || session.ColumnMatchAiFeedbackId is not Guid fb) return;
        await _columnMatch.RecordConfirmedAsync(session.CompanyId, fb, state.Columns, ct);
    }

    // ══════════════════════════════ ผู้ใช้แก้แถว ══════════════════════════════

    private static void ApplyRowEdit(ImportStagedRow row, ImportEditRowRequest request, IReadOnlyDictionary<string, ImportFieldDto> fields,
        ImportBatchState state)
    {
        var values = ImportJson.ReadValues(row.MappedJson);
        var issues = Issues(row);
        var meta = ReadMeta(row.AiFeedbackIdsJson);
        var edited = MetaSet(meta, MetaEdited);
        var rejected = MetaSet(meta, MetaRejected);

        void SetValue(ImportFieldDto field, string? input)
        {
            string? canonical = null;
            if (!string.IsNullOrWhiteSpace(input))
            {
                var r = ImportCellNormalizer.Normalize(field, input, state.DateOrder);
                canonical = r.Cell.Value
                    ?? (r.Issue?.Code == "vat-exempt" ? VatExemptToken
                        : throw new BusinessRuleException($"{field.Label}: {r.Issue?.Message ?? "ค่าไม่ถูกต้อง"}"));
            }
            values[field.Field] = canonical;
            edited.Add(field.Field);
        }

        if (request.Cells != null)
            foreach (var kv in request.Cells)
            {
                if (!fields.TryGetValue(kv.Key, out var field))
                    throw new BusinessRuleException($"ไม่มีช่อง \"{kv.Key}\" ในข้อมูลชนิดนี้");
                SetValue(field, kv.Value);
            }
        foreach (var f in request.AcceptSuggestionFields ?? Array.Empty<string>())
        {
            var sug = issues.FirstOrDefault(i => string.Equals(i.Field, f, StringComparison.OrdinalIgnoreCase) && i.Suggested != null);
            if (sug == null || !fields.TryGetValue(f, out var field)) continue;
            SetValue(field, sug.Suggested);
        }
        foreach (var f in request.RejectSuggestionFields ?? Array.Empty<string>())
            rejected.Add(f);
        if (request.Action is ImportRowAction a)
        {
            row.Action = a;
            meta[MetaUserAction] = "1";
        }
        if (request.MatchedEntityId is Guid m)
        {
            meta[MetaUserAction] = "1";
            meta.Remove(MetaMatchAuto);
            if (m == Guid.Empty)
            {
                // Guid.Empty = ไม่ใช่รายเดิม (สร้างใหม่)
                row.MatchedEntityId = null;
                row.MatchKind = "User";
                row.MatchScore = null;
                if (request.Action == null) row.Action = ImportRowAction.Create;
            }
            else
            {
                row.MatchedEntityId = m;
                row.MatchKind = "User";
                row.MatchScore = 1m;
                if (request.Action == null && row.Action == ImportRowAction.Create) row.Action = ImportRowAction.Merge;
            }
        }
        if (request.Excluded == true) { meta[MetaExcluded] = "1"; meta.Remove(MetaIncluded); }
        else if (request.Excluded == false) { meta.Remove(MetaExcluded); meta[MetaIncluded] = "1"; }

        SetMetaSet(meta, MetaEdited, edited);
        SetMetaSet(meta, MetaRejected, rejected);
        row.MappedJson = ImportJson.Serialize(values);
        row.AiFeedbackIdsJson = WriteMeta(meta);
    }

    // ══════════════════════════════ ลงข้อมูล ══════════════════════════════

    private static bool Eligible(IReadOnlyList<ImportIssueDto> issues, IReadOnlySet<string> ack)
        => !issues.Any(i => i.Severity == ImportIssueSeverity.Error)
            && issues.Where(i => i.Severity == ImportIssueSeverity.Warning).All(i => ack.Contains(i.Code));

    private static ImportTargetRow ToTargetRow(ImportStagedRow r)
        => new(r.Id, r.RowNo, ImportJson.ReadValues(r.MappedJson), r.Action, r.MatchedEntityId, r.GroupKey, r.DisplayKey);

    /// <summary>
    /// ลงแถวที่พร้อม (ผ่าน + รับทราบคำเตือน) ไม่เกิน <paramref name="limit"/> แถว (ปัดขึ้นให้ครบกลุ่ม) — แถวที่ Committed แล้วไม่ถูกส่งซ้ำ (idempotent ·
    /// resume ได้) · หมดงาน ⇒ สรุปผล + เรียนรู้ · ยังเหลือ ⇒ คืน Queued ให้ worker ทำต่อ · ผู้เรียกถือ JobLock ของ batch แล้ว
    /// </summary>
    private async Task<ImportCommitResultDto> CommitPendingAsync(Guid companyId, Guid batchId, IImportTarget target, int limit, CancellationToken ct)
    {
        var session = await ReloadAsync(companyId, batchId, ct);
        var state = State(session);
        var options = state.PendingCommit ?? new ImportCommitOptions();
        var actor = state.PendingActor ?? SystemActor(session);
        var ack = new HashSet<string>(options.AcknowledgedWarningCodes ?? Array.Empty<string>(), StringComparer.Ordinal);
        session.Status = ImportSessionStatus.Committing;
        session.Phase = ImportBatchPhase.Commit;
        session.HeartbeatAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        var ctx = Context(session, actor, allowAi: false);
        var fileName = session.FileName;
        var sha = session.FileSha256;

        var open = await _db.ImportStagedRows.AsNoTracking()
            .Where(r => r.SessionId == batchId && r.CompanyId == companyId
                && r.Status != ImportRowStatus.Committed && r.Status != ImportRowStatus.Undone && r.Status != ImportRowStatus.Excluded)
            .OrderBy(r => r.RowNo).ToListAsync(ct);
        var skipped = open.Count(r => r.Action == ImportRowAction.Skip);
        var eligible = open.Where(r => r.Action != ImportRowAction.Skip && Eligible(Issues(r), ack)).ToList();
        skipped += open.Count - skipped - eligible.Count;
        // กลุ่ม (ใบสำคัญ/เอกสารหลายบรรทัด) ห้ามแยก — ตัด chunk ที่ขอบกลุ่ม
        var units = eligible.GroupBy(r => r.GroupKey ?? ("#" + r.Id)).Select(g => g.ToList()).ToList();
        var batch = new List<List<ImportStagedRow>>();
        var taken = 0;
        foreach (var u in units)
        {
            if (taken >= limit) break;
            batch.Add(u);
            taken += u.Count;
        }

        var (results, rolledBack) = target.CommitStyle == ImportCommitStyle.SharedTransaction
            ? await CommitSharedAsync(companyId, batchId, target, ctx, batch, options, actor, fileName, sha, ct)
            : await CommitOwnAsync(companyId, batchId, target, ctx, batch, options, actor, fileName, sha, ct);

        if (rolledBack)
        {
            // AllOrNothing: ไม่มีอะไรลง — ประทับเหตุผลของแถวที่ล้ม (นอกธุรกรรมที่ถูก rollback) ให้ผู้ใช้แก้แล้วลองใหม่
            await PersistResultsAsync(companyId, batchId, results.Where(r => !r.Success).ToList(), ct);
            var s2 = await ReloadAsync(companyId, batchId, ct);
            var st2 = State(s2);
            st2.PendingCommit = null;
            st2.PendingActor = null;
            st2.Notes.Add("โหมด \"ลงทั้งหมดหรือไม่ลงเลย\": มีแถวที่ลงไม่สำเร็จ ⇒ ไม่ลงข้อมูลเลยสักแถว (ย้อนทั้งชุดแล้ว) — แก้แถวที่ล้มแล้วยืนยันอีกครั้ง");
            s2.OptionsJson = ImportJson.Serialize(st2);
            s2.Status = ImportSessionStatus.Staged;
            s2.Phase = ImportBatchPhase.Review;
            s2.ClaimedBy = null;
            s2.HeartbeatAt = null;
            await _db.SaveChangesAsync(ct);
            await RefreshSnapshotAsync(s2, persist: true, ct);
            await _db.SaveChangesAsync(ct);
            return new ImportCommitResultDto(batchId, s2.Status, SessionStatusLabel(s2.Status), 0, results.Count(r => !r.Success), skipped,
                false, Failures(results, eligible), null);
        }

        var committed = results.Count(r => r.Success && !r.Skipped);
        var writerSkipped = results.Count(r => r.Success && r.Skipped);
        if (taken < eligible.Count)
        {
            var s3 = await ReloadAsync(companyId, batchId, ct);
            s3.Status = ImportSessionStatus.Queued;
            s3.Phase = ImportBatchPhase.Commit;
            s3.SkippedCount += writerSkipped;
            await _db.SaveChangesAsync(ct);
            return new ImportCommitResultDto(batchId, s3.Status, SessionStatusLabel(s3.Status), committed, results.Count(r => !r.Success),
                skipped + writerSkipped, true, Failures(results, eligible), null);
        }
        var (final, templateId) = await FinishCommitAsync(companyId, batchId, target, ct, writerSkipped + skipped);
        return new ImportCommitResultDto(batchId, final.Status, SessionStatusLabel(final.Status), committed, results.Count(r => !r.Success),
            skipped + writerSkipped, false, Failures(results, eligible), templateId);
    }

    private static IReadOnlyList<ImportRowFailureDto> Failures(IEnumerable<ImportRowCommitResult> results, IReadOnlyList<ImportStagedRow> rows)
    {
        var byId = rows.ToDictionary(r => r.Id);
        return results.Where(r => !r.Success)
            .Select(r => byId.TryGetValue(r.RowId, out var row)
                ? new ImportRowFailureDto(r.RowId, row.RowNo, row.DisplayKey, r.Error ?? "ลงไม่สำเร็จ")
                : new ImportRowFailureDto(r.RowId, 0, null, r.Error ?? "ลงไม่สำเร็จ"))
            .OrderBy(f => f.RowNo).ToList();
    }

    private static IEnumerable<List<List<ImportStagedRow>>> ChunkUnits(IReadOnlyList<List<ImportStagedRow>> units, int rowsPerChunk)
    {
        var chunk = new List<List<ImportStagedRow>>();
        var n = 0;
        foreach (var u in units)
        {
            chunk.Add(u);
            n += u.Count;
            if (n >= rowsPerChunk)
            {
                yield return chunk;
                chunk = new List<List<ImportStagedRow>>();
                n = 0;
            }
        }
        if (chunk.Count > 0) yield return chunk;
    }

    /// <summary>
    /// SharedTransaction (§2.4): ธุรกรรมเดียว · savepoint ต่อ chunk ~200 แถว (ไม่ตัดกลางกลุ่ม) · chunk ล้ม ⇒ rollback savepoint + ล้าง tracker + ลงซ้ำทีละกลุ่ม
    /// (savepoint ต่อกลุ่ม — แถวที่ผิดไม่ทิ้งของครึ่ง ๆ) · ผลต่อแถวประทับในธุรกรรมเดียวกัน (commit แล้วผลกับข้อมูลไปด้วยกัน — resume ไม่ลงซ้ำ) ·
    /// AllOrNothing + มีแถวล้ม ⇒ rollback ทั้งธุรกรรม
    /// </summary>
    private async Task<(List<ImportRowCommitResult> Results, bool RolledBack)> CommitSharedAsync(Guid companyId, Guid batchId, IImportTarget target,
        ImportTargetContext ctx, IReadOnlyList<List<ImportStagedRow>> units, ImportCommitOptions options, ImportActor actor,
        string fileName, string? sha, CancellationToken ct)
    {
        var all = new List<ImportRowCommitResult>();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var chunkNo = 0;
        foreach (var chunk in ChunkUnits(units, SharedChunkRows))
        {
            var sp = "imp_chunk_" + (chunkNo++).ToString(CultureInfo.InvariantCulture);
            await tx.CreateSavepointAsync(sp, ct);
            var mark = _db.DeferredAuditMark;
            var rows = chunk.SelectMany(u => u).Select(ToTargetRow).ToList();
            IReadOnlyList<ImportRowCommitResult>? res;
            try
            {
                res = await target.CommitGroupAsync(ctx, rows, ct);
                if (res.Count != rows.Count || res.Any(r => !r.Success)) res = null;
                else await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogInformation(ex, "ImportBatch {BatchId}: chunk {Chunk} ล้ม — ลงทีละกลุ่มเพื่อแยกแถวที่ผิด", batchId, chunkNo);
                res = null;
            }
            if (res == null)
            {
                await tx.RollbackToSavepointAsync(sp, ct);
                _db.RollbackDeferredAuditTo(mark);
                _db.ChangeTracker.Clear();
                res = await ReplayUnitsAsync(tx, target, ctx, chunk, batchId, ct);
            }
            all.AddRange(res);
            await PersistResultsAsync(companyId, batchId, res, ct);
            await HeartbeatAsync(companyId, batchId, rows.Count, ct);
        }
        if (options.Mode == ImportCommitMode.AllOrNothing && all.Any(r => !r.Success))
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return (all, true);
        }
        AddAudit(companyId, batchId, actor, "IMPORT-COMMIT", AuditAction.Create, new
        {
            ruleCode = "IMPORT-COMMIT", entityType = ctx.EntityType, fileName, fileSha256 = sha, mode = options.Mode.ToString(),
            committed = all.Count(r => r.Success && !r.Skipped), skipped = all.Count(r => r.Success && r.Skipped), failed = all.Count(r => !r.Success),
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (all, false);
    }

    private async Task<List<ImportRowCommitResult>> ReplayUnitsAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, IImportTarget target,
        ImportTargetContext ctx, IReadOnlyList<List<ImportStagedRow>> units, Guid batchId, CancellationToken ct)
    {
        var results = new List<ImportRowCommitResult>();
        var n = 0;
        foreach (var unit in units)
        {
            var sp = "imp_unit_" + (n++).ToString(CultureInfo.InvariantCulture);
            await tx.CreateSavepointAsync(sp, ct);
            var mark = _db.DeferredAuditMark;
            var rows = unit.Select(ToTargetRow).ToList();
            string? error = null;
            IReadOnlyList<ImportRowCommitResult>? res = null;
            try
            {
                res = await target.CommitGroupAsync(ctx, rows, ct);
                if (res.Count != rows.Count) error = "ปลายทางตอบผลไม่ครบทุกแถว — ลองใหม่อีกครั้ง";
                else if (res.FirstOrDefault(r => !r.Success) is { } bad) error = bad.Error ?? "ลงไม่สำเร็จ";
                else await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = RowError(ex, batchId);
            }
            if (error != null || res == null)
            {
                await tx.RollbackToSavepointAsync(sp, ct);
                _db.RollbackDeferredAuditTo(mark);
                _db.ChangeTracker.Clear();
                // ทั้งกลุ่มล้มด้วยกัน (เอกสาร/ใบสำคัญครึ่งใบไม่ถูกลง) — แถวที่มีเหตุของตัวเองได้เหตุนั้น
                var own = res?.Where(r => !r.Success).ToDictionary(r => r.RowId, r => r.Error) ?? new Dictionary<Guid, string?>();
                foreach (var r in rows)
                    results.Add(new ImportRowCommitResult(r.RowId, false,
                        Error: own.TryGetValue(r.RowId, out var e) && e != null ? e : $"ลงไม่สำเร็จเพราะแถวอื่นในกลุ่มเดียวกันผิด: {error ?? "ลงไม่สำเร็จ"}"));
                continue;
            }
            results.AddRange(res);
        }
        return results;
    }

    /// <summary>OwnTransactionPerGroup (§2.4): domain service เปิดธุรกรรมเอง ⇒ pipeline ไม่ห่อ · ลงทีละกลุ่ม · ผลต่อแถวประทับทันที (resume/ลงซ้ำไม่ซ้ำ) ·
    /// AllOrNothing ย้อนกลุ่มที่ลงแล้วไม่ได้ ⇒ หยุดที่กลุ่มแรกที่ล้ม (ผู้ใช้ "ย้อนการนำเข้า" ส่วนที่ลงแล้วได้)</summary>
    private async Task<(List<ImportRowCommitResult> Results, bool RolledBack)> CommitOwnAsync(Guid companyId, Guid batchId, IImportTarget target,
        ImportTargetContext ctx, IReadOnlyList<List<ImportStagedRow>> units, ImportCommitOptions options, ImportActor actor,
        string fileName, string? sha, CancellationToken ct)
    {
        var all = new List<ImportRowCommitResult>();
        foreach (var unit in units)
        {
            var rows = unit.Select(ToTargetRow).ToList();
            IReadOnlyList<ImportRowCommitResult> res;
            try
            {
                res = await target.CommitGroupAsync(ctx, rows, ct);
                if (res.Count != rows.Count)
                    res = rows.Select(r => res.FirstOrDefault(x => x.RowId == r.RowId)
                        ?? new ImportRowCommitResult(r.RowId, false, Error: "ปลายทางไม่ได้ตอบผลของแถวนี้ — ตรวจที่หน้าข้อมูลแล้วลองใหม่")).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                var msg = RowError(ex, batchId);
                res = rows.Select(r => new ImportRowCommitResult(r.RowId, false, Error: msg)).ToList();
            }
            all.AddRange(res);
            await PersistResultsAsync(companyId, batchId, res, ct);
            await HeartbeatAsync(companyId, batchId, rows.Count, ct);
            if (options.Mode == ImportCommitMode.AllOrNothing && res.Any(r => !r.Success)) break;
        }
        AddAudit(companyId, batchId, actor, "IMPORT-COMMIT", AuditAction.Create, new
        {
            ruleCode = "IMPORT-COMMIT", entityType = ctx.EntityType, fileName, fileSha256 = sha, mode = options.Mode.ToString(),
            committed = all.Count(r => r.Success && !r.Skipped), skipped = all.Count(r => r.Success && r.Skipped), failed = all.Count(r => !r.Success),
        });
        await _db.SaveChangesAsync(ct);
        return (all, false);
    }

    private string RowError(Exception ex, Guid batchId)
    {
        if (ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException or ArgumentException) return ex.Message;
        if (ex is DbUpdateException db)
            return "บันทึกไม่ได้ — ข้อมูลไม่ผ่านข้อจำกัดของฐานข้อมูล (เช่น ยาวเกิน/ซ้ำกับที่มีอยู่): " + (db.InnerException?.Message ?? db.Message);
        _logger?.LogError(ex, "ImportBatch {BatchId}: ระบบขัดข้องขณะลงข้อมูล", batchId);
        return "ระบบขัดข้องขณะลงแถวนี้ — ลองใหม่อีกครั้ง ถ้ายังล้มให้แจ้งผู้ดูแลพร้อมเลขชุดนำเข้า: " + ex.Message;
    }

    /// <summary>ประทับผลต่อแถว — สำเร็จ = Committed + entity (idempotent/ย้อน) · writer ข้ามเพราะมีอยู่แล้ว = action ข้าม (ไม่ส่งซ้ำ) · ล้ม = Failed + เหตุผล</summary>
    private async Task PersistResultsAsync(Guid companyId, Guid batchId, IReadOnlyList<ImportRowCommitResult> results, CancellationToken ct)
    {
        if (results.Count == 0) return;
        var map = results.GroupBy(r => r.RowId).ToDictionary(g => g.Key, g => g.Last());
        var ids = map.Keys.ToList();
        var rows = await _db.ImportStagedRows.Where(r => ids.Contains(r.Id) && r.SessionId == batchId && r.CompanyId == companyId).ToListAsync(ct);
        foreach (var row in rows)
        {
            var res = map[row.Id];
            if (res.Success && res.Skipped)
            {
                row.Action = ImportRowAction.Skip;
                var issues = Issues(row);
                issues.Add(new ImportIssueDto(null, "skipped-existing", ImportIssueSeverity.Info,
                    "ข้ามตอนลงข้อมูล — มีรายการเดียวกันอยู่ในระบบแล้ว (นำเข้าซ้ำไม่สร้างซ้ำ)", Source: ImportIssueSource.Target));
                row.IssuesJson = ImportJson.Serialize(issues);
                row.CommitError = null;
            }
            else if (res.Success)
            {
                row.Status = ImportRowStatus.Committed;
                row.CommittedEntityType = Trunc(res.EntityType, 64);
                row.CommittedEntityId = res.EntityId;
                row.BeforeJson = res.BeforeJson;
                row.CommitError = null;
            }
            else
            {
                row.Status = ImportRowStatus.Failed;
                row.CommitError = res.Error;
            }
            row.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    private Task HeartbeatAsync(Guid companyId, Guid batchId, int processed, CancellationToken ct)
        => _db.SmartImportSessions.Where(s => s.Id == batchId && s.CompanyId == companyId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.HeartbeatAt, DateTime.UtcNow)
                .SetProperty(x => x.ProcessedRows, x => x.ProcessedRows + processed), ct);

    /// <summary>สรุปผลหลังลงครบ: สถานะซื่อสัตย์ (ลงบางแถว = PartiallyCompleted) · เรียนรู้ (แม่แบบ · ปิดลูป AI) · สรุปทั้งชุด</summary>
    private async Task<(SmartImportSession Session, Guid? TemplateId)> FinishCommitAsync(Guid companyId, Guid batchId, IImportTarget target,
        CancellationToken ct, int skippedThisRound = 0)
    {
        var session = await ReloadAsync(companyId, batchId, ct);
        var state = State(session);
        var counts = await _db.ImportStagedRows.AsNoTracking()
            .Where(r => r.SessionId == batchId && r.CompanyId == companyId)
            .GroupBy(r => r.Status).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct);
        int C(ImportRowStatus s) => counts.FirstOrDefault(c => c.Key == s)?.C ?? 0;
        var committed = C(ImportRowStatus.Committed);
        var failed = C(ImportRowStatus.Failed);
        var open = await _db.ImportStagedRows.CountAsync(r => r.SessionId == batchId && r.CompanyId == companyId
            && r.Action != ImportRowAction.Skip
            && (r.Status == ImportRowStatus.Valid || r.Status == ImportRowStatus.Warning || r.Status == ImportRowStatus.Error
                || r.Status == ImportRowStatus.Pending), ct);
        session.SuccessCount = committed;
        session.ErrorCount = failed;
        session.SkippedCount += skippedThisRound;
        session.Status = committed == 0
            ? (failed > 0 ? ImportSessionStatus.Failed : open > 0 ? ImportSessionStatus.Staged : ImportSessionStatus.Completed)
            : (failed == 0 && open == 0 ? ImportSessionStatus.Completed : ImportSessionStatus.PartiallyCompleted);
        session.Phase = ImportBatchPhase.Review;
        session.CompletedAt = DateTime.UtcNow;
        session.ClaimedBy = null;
        session.HeartbeatAt = null;

        // ── Learn (§2.1): แม่แบบ "จำรูปแบบไฟล์นี้" + ปิดลูป AI (กฎเหล็ก #1) ──
        var options = state.PendingCommit;
        var actor = state.PendingActor ?? SystemActor(session);
        Guid? templateId = null;
        state.PendingCommit = null;
        state.PendingActor = null;
        session.OptionsJson = ImportJson.Serialize(state);
        await _db.SaveChangesAsync(ct);
        if (committed > 0 && options is { SaveTemplate: true })
        {
            try
            {
                // คอลัมน์มี transform (กลับเครื่องหมาย ฯลฯ) ⇒ ต้องส่งคอลัมน์ตรง (SmartImportColumnMappings ไม่เก็บ transform) · ไม่มี ⇒ จำจากตารางจับคู่ของชุด
                var tpl = state.Columns.Any(c => c.Transform != null)
                    ? await _templates.SaveColumnsAsync(companyId, session.EntityType, state.Headers,
                        state.Columns.Select(c => new ImportTemplateColumnDto(c.SourceHeader, c.TargetField, c.Transform)).ToList(),
                        session.SourceSystem, options.TemplateName, confirmedByUser: true, actor, ct)
                    : await _templates.SaveFromBatchAsync(companyId, batchId, options.TemplateName, confirmedByUser: true, actor, ct);
                templateId = tpl.Id;
                session.MappingTemplateId = tpl.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ข้อมูลลงแล้ว — แม่แบบพลาดต้องไม่ทำให้ผู้ใช้คิดว่าการลงข้อมูลล้ม · ล้มดังที่หมายเหตุของชุด + log
                _logger?.LogError(ex, "ImportBatch {BatchId}: จำรูปแบบไฟล์ไม่สำเร็จ", batchId);
                state.Notes.Add("จำรูปแบบไฟล์ไม่สำเร็จ: " + (ex is BusinessRuleException ? ex.Message : "ระบบขัดข้อง")
                    + " — ข้อมูลลงครบแล้ว ครั้งหน้าจับคู่คอลัมน์ใหม่");
                session.OptionsJson = ImportJson.Serialize(state);
            }
        }
        await RecordColumnChoiceAsync(session, state, ct);
        await RecordMatchChoicesAsync(companyId, batchId, ct);
        await RefreshSnapshotAsync(session, persist: true, ct);
        await _db.SaveChangesAsync(ct);
        return (session, templateId);
    }

    /// <summary>ปิดลูป ImportEntityMatch ต่อแถวที่ลงแล้ว (<see cref="IImportAiAugmenter.RecordPickChoiceAsync"/> — feedback ต้องเป็นของบริษัทนี้) —
    /// คำตอบสุดท้าย = id ที่ผู้ใช้ยืนยัน หรือ "NEW" · acceptedAi = ตรงกับที่ AI ตอบ · ไม่บันทึกซ้ำ</summary>
    private async Task RecordMatchChoicesAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        if (_ai == null) return;
        var rows = await _db.ImportStagedRows
            .Where(r => r.SessionId == batchId && r.CompanyId == companyId && r.Status == ImportRowStatus.Committed
                && r.AiFeedbackIdsJson != null && r.AiFeedbackIdsJson.Contains(MetaMatchFeedback))
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            var meta = ReadMeta(r.AiFeedbackIdsJson);
            if (meta.ContainsKey(MetaMatchRecorded) || !Guid.TryParse(meta.GetValueOrDefault(MetaMatchFeedback), out var fb)) continue;
            var chosen = r.Action == ImportRowAction.Create || r.MatchedEntityId == null
                ? ImportEntityResolver.NewEntityAnswer : r.MatchedEntityId.Value.ToString();
            await _ai.RecordPickChoiceAsync(companyId, fb, chosen, meta.GetValueOrDefault(MetaMatchAi), ct);
            meta[MetaMatchRecorded] = "1";
            r.AiFeedbackIdsJson = WriteMeta(meta);
        }
        if (rows.Count > 0) await _db.SaveChangesAsync(ct);
    }

    private void AddAudit(Guid companyId, Guid batchId, ImportActor actor, string ruleCode, AuditAction action, object payload)
        => _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actor.UserId == Guid.Empty ? null : actor.UserId,
            UserEmail = actor.Name,
            Action = action,
            EntityType = "ImportBatch",
            EntityId = batchId.ToString(),
            NewValues = JsonSerializer.Serialize(payload),
            IpAddress = actor.Ip,
            Timestamp = DateTime.UtcNow,
        });

    // ══════════════════════════════ ย้อน ══════════════════════════════

    private async Task<List<ImportCommittedRow>> CommittedRowsAsync(SmartImportSession s, CancellationToken ct)
        => (await _db.ImportStagedRows.AsNoTracking()
                .Where(r => r.SessionId == s.Id && r.CompanyId == s.CompanyId && r.Status == ImportRowStatus.Committed && r.CommittedEntityId != null)
                .OrderBy(r => r.RowNo).ToListAsync(ct))
            .Select(r => new ImportCommittedRow(r.Id, r.RowNo, r.CommittedEntityType ?? s.EntityType, r.CommittedEntityId!.Value, r.Action,
                r.BeforeJson, r.DisplayKey))
            .ToList();

    private async Task<ImportUndoResultDto> UndoCoreAsync(Guid companyId, Guid batchId, string reason, ImportActor actor, CancellationToken ct)
    {
        var session = await ReloadAsync(companyId, batchId, ct);
        if (session.Status is ImportSessionStatus.Committing or ImportSessionStatus.Queued)
            throw new BusinessRuleException("ชุดนี้ยังประมวลผล/ลงข้อมูลอยู่ — รอให้เสร็จก่อนย้อน");
        var target = Target(session);
        var committed = await CommittedRowsAsync(session, ct);
        if (committed.Count == 0)
            throw new BusinessRuleException("ไม่มีแถวที่ลงข้อมูลแล้วให้ย้อน");
        var ctx = Context(session, actor, allowAi: false);
        var checks = (await target.CanUndoAsync(ctx, committed, ct)).ToDictionary(c => c.RowId);
        var reversible = committed.Where(c => checks.TryGetValue(c.RowId, out var k) && k.CanUndo).ToList();
        IReadOnlyList<ImportRowUndoResult> results = Array.Empty<ImportRowUndoResult>();
        if (reversible.Count > 0)
        {
            if (target.CommitStyle == ImportCommitStyle.SharedTransaction)
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                results = await target.UndoAsync(ctx, reversible, reason, ct);
                await PersistUndoAsync(companyId, batchId, results, ct);
                AddAudit(companyId, batchId, actor, "IMPORT-UNDO", AuditAction.Delete, UndoPayload(target, reason, results, committed.Count));
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            else
            {
                results = await target.UndoAsync(ctx, reversible, reason, ct);
                await PersistUndoAsync(companyId, batchId, results, ct);
                AddAudit(companyId, batchId, actor, "IMPORT-UNDO", AuditAction.Delete, UndoPayload(target, reason, results, committed.Count));
                await _db.SaveChangesAsync(ct);
            }
        }
        var s = await ReloadAsync(companyId, batchId, ct);
        var remaining = await CommittedCountAsync(s, ct);
        var undone = results.Count(r => r.Success);
        s.SuccessCount = remaining;
        if (remaining == 0)
        {
            s.Status = ImportSessionStatus.Undone;
            s.UndoneAt = DateTime.UtcNow;
            s.UndoneBy = actor.UserId.ToString();
        }
        else
        {
            s.Status = ImportSessionStatus.PartiallyCompleted;
            var st = State(s);
            st.Notes.Add($"ย้อนแล้ว {undone} แถว · ยังคงอยู่ {remaining} แถว (ย้อนไม่ได้ — ดูเหตุผลรายแถวที่ตัวอย่างการย้อน)");
            s.OptionsJson = ImportJson.Serialize(st);
        }
        await _db.SaveChangesAsync(ct);
        await RefreshSnapshotAsync(s, persist: true, ct);
        await _db.SaveChangesAsync(ct);
        var byId = committed.ToDictionary(c => c.RowId);
        var failures = results.Where(r => !r.Success)
            .Select(r => new ImportRowFailureDto(r.RowId, byId.TryGetValue(r.RowId, out var c) ? c.RowNo : 0, c?.DisplayKey, r.Error ?? "ย้อนไม่สำเร็จ"))
            .Concat(committed.Where(c => !reversible.Contains(c)).Select(c => new ImportRowFailureDto(c.RowId, c.RowNo, c.DisplayKey,
                checks.TryGetValue(c.RowId, out var k) ? $"{k.BlockReason} — {k.WayForward}" : "ย้อนไม่ได้")))
            .OrderBy(f => f.RowNo).ToList();
        return new ImportUndoResultDto(batchId, s.Status, undone, committed.Count - undone, failures);
    }

    private static object UndoPayload(IImportTarget target, string reason, IReadOnlyList<ImportRowUndoResult> results, int committed)
        => new { ruleCode = "IMPORT-UNDO", entityType = target.EntityType, reason, undone = results.Count(r => r.Success), failed = results.Count(r => !r.Success), committed };

    private async Task PersistUndoAsync(Guid companyId, Guid batchId, IReadOnlyList<ImportRowUndoResult> results, CancellationToken ct)
    {
        var ok = results.Where(r => r.Success).Select(r => r.RowId).ToList();
        var bad = results.Where(r => !r.Success).ToDictionary(r => r.RowId, r => r.Error);
        var ids = ok.Concat(bad.Keys).ToList();
        var rows = await _db.ImportStagedRows.Where(r => ids.Contains(r.Id) && r.SessionId == batchId && r.CompanyId == companyId).ToListAsync(ct);
        foreach (var r in rows)
        {
            if (bad.TryGetValue(r.Id, out var e)) { r.CommitError = "ย้อนไม่สำเร็จ: " + (e ?? ""); continue; }
            r.Status = ImportRowStatus.Undone;
            r.CommitError = null;
            r.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    // ══════════════════════════════ สรุป / DTO ══════════════════════════════

    private static ImportReviewSnapshot? Snapshot(SmartImportSession s)
    {
        try { return ImportJson.Deserialize<ImportReviewSnapshot>(s.ReviewSummaryJson); }
        catch (JsonException) { return null; }   // สรุปรูปเก่า/เสีย — คำนวณใหม่จากแถวพัก (ผู้เรียก)
    }

    /// <summary>คำนวณสรุปทั้งชุดจากแถวพัก (จำนวนต่อสถานะ · chip ปัญหา · คำเตือนที่ต้องรับทราบ) · แถวถูกล้างแล้ว ⇒ คืนสรุปที่เก็บไว้ ·
    /// <paramref name="persist"/> = เขียนลง ReviewSummaryJson (session ต้องถูกติดตาม — ผู้เรียก SaveChanges)</summary>
    private async Task<ImportReviewSnapshot> RefreshSnapshotAsync(SmartImportSession session, bool persist, CancellationToken ct)
    {
        var rows = await _db.ImportStagedRows.AsNoTracking()
            .Where(r => r.SessionId == session.Id && r.CompanyId == session.CompanyId)
            .Select(r => new { r.Status, r.Action, r.IssuesJson })
            .ToListAsync(ct);
        if (rows.Count == 0 && Snapshot(session) is { } kept) return kept;
        var snap = new ImportReviewSnapshot();
        foreach (var g in rows.GroupBy(r => r.Status)) snap.StatusCounts[g.Key.ToString()] = g.Count();
        var openRows = rows.Where(r => r.Status is not (ImportRowStatus.Committed or ImportRowStatus.Undone)).ToList();
        snap.SkipActionCount = openRows.Count(r => r.Action == ImportRowAction.Skip && r.Status != ImportRowStatus.Excluded);
        var issueRows = openRows.Select(r => (r.Status, r.Action, Issues: ParseIssues(r.IssuesJson))).ToList();
        snap.IssueCounts = issueRows.SelectMany(r => r.Issues.Select(i => i).DistinctBy(i => i.Code))
            .GroupBy(i => i.Code)
            .Select(g => new ImportIssueCountDto(g.Key, g.Max(i => i.Severity), IssueLabel(g.Key, g.First().Message), g.Count(),
                g.Any(i => i.Suggested != null)))
            .OrderByDescending(c => c.Severity).ThenByDescending(c => c.Count).ToList();
        snap.Warnings = issueRows.Where(r => r.Status != ImportRowStatus.Excluded && r.Action != ImportRowAction.Skip)
            .SelectMany(r => r.Issues.Where(i => i.Severity == ImportIssueSeverity.Warning).DistinctBy(i => i.Code))
            .GroupBy(i => i.Code)
            .Select(g => new ImportWarningAckDto(g.Key, IssueLabel(g.Key, g.First().Message), g.Count(), g.First().LegalRef))
            .OrderByDescending(w => w.RowCount).ToList();
        if (persist) session.ReviewSummaryJson = ImportJson.Serialize(snap);
        return snap;
    }

    private async Task<ImportBatchSummaryDto> SummaryAsync(SmartImportSession s, bool live, CancellationToken ct)
    {
        var snap = live ? await RefreshSnapshotAsync(s, persist: false, ct) : Snapshot(s) ?? await RefreshSnapshotAsync(s, persist: false, ct);
        var committed = snap.Count(ImportRowStatus.Committed);
        var open = snap.Count(ImportRowStatus.Valid) + snap.Count(ImportRowStatus.Warning) + snap.Count(ImportRowStatus.Failed);
        var canCommit = s.Status is ImportSessionStatus.Staged or ImportSessionStatus.PartiallyCompleted or ImportSessionStatus.Failed
            && open - snap.SkipActionCount > 0;
        var canUndo = committed > 0 && s.Status is not (ImportSessionStatus.Committing or ImportSessionStatus.Queued or ImportSessionStatus.Undone);
        var canCancel = committed == 0
            && s.Status is not (ImportSessionStatus.Committing or ImportSessionStatus.Cancelled or ImportSessionStatus.Undone or ImportSessionStatus.Completed);
        string? createdBy = s.CreatedBy;
        if (Guid.TryParse(s.CreatedBy, out var uid))
            createdBy = await _db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "ผู้ใช้ที่ถูกลบ";
        return new ImportBatchSummaryDto(s.Id, s.EntityType, _targets.Find(s.EntityType)?.Label ?? s.EntityType, s.FileName, s.SourceSystem,
            s.Status, SessionStatusLabel(s.Status), s.Phase, s.TotalRows,
            snap.Count(ImportRowStatus.Valid), snap.Count(ImportRowStatus.Warning), snap.Count(ImportRowStatus.Error),
            snap.Count(ImportRowStatus.Excluded), committed, snap.Count(ImportRowStatus.Failed), s.SkippedCount,
            s.CreatedAt, createdBy, s.CompletedAt, s.UndoneAt, canCommit, canUndo, canCancel);
    }

    private async Task<ImportBatchDetailDto> DetailAsync(SmartImportSession s, CancellationToken ct)
    {
        var state = State(s);
        var target = _targets.Find(s.EntityType);
        var summary = await SummaryAsync(s, live: true, ct);
        var snap = await RefreshSnapshotAsync(s, persist: false, ct);
        var percent = s.TotalRows == 0 ? 100m : Math.Round(Math.Min(100m, s.ProcessedRows * 100m / s.TotalRows), 1, MidpointRounding.AwayFromZero);
        var progress = new ImportBatchProgressDto(s.Phase, PhaseLabel(s.Phase), s.ProcessedRows, s.TotalRows, percent,
            s.Status == ImportSessionStatus.Queued, s.HeartbeatAt);
        var sheets = ImportJson.Deserialize<List<string>>(s.SheetNamesJson) ?? new List<string>();
        var source = new ImportSourceInfoDto(s.FileName, Enum.TryParse<ImportFileKind>(s.FileFormat, true, out var kind) ? kind : ImportFileKind.Unknown,
            s.Encoding, s.Delimiter, s.SheetName, sheets, s.HeaderRowIndex, s.HeaderRowSpan, s.HeaderSignature, s.SourceSystem, null,
            s.SourceFileAttachmentId, s.FileSha256);
        ImportTemplateRefDto? tpl = null;
        if (s.MappingTemplateId is Guid tid)
            tpl = await _db.ImportMappingTemplates.AsNoTracking()
                .Where(t => t.Id == tid && t.CompanyId == s.CompanyId && !t.IsDeleted)
                .Select(t => new ImportTemplateRefDto(t.Id, t.Name, t.ConfirmedByUser, t.AutoCommitWhenClean, t.UseCount))
                .FirstOrDefaultAsync(ct);
        var dups = s.FileSha256 == null ? new List<ImportDuplicateFileDto>() : await _db.SmartImportSessions.AsNoTracking()
            .Where(o => o.CompanyId == s.CompanyId && o.Id != s.Id && o.FileSha256 == s.FileSha256 && o.SuccessCount > 0)
            .OrderByDescending(o => o.CreatedAt).Take(5)
            .Select(o => new ImportDuplicateFileDto(o.Id, o.FileName, o.CreatedAt, o.Status, o.SuccessCount))
            .ToListAsync(ct);
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.HeaderReason)) notes.Add(state.HeaderReason!);
        if (state.SummaryRows > 0) notes.Add($"ตัดแถวยอดรวม/ยกมา-ยกไป {state.SummaryRows} แถวออกให้ (นำกลับได้รายแถว)");
        if (dups.Count > 0) notes.Add($"ไฟล์นี้ (เนื้อไฟล์ตรงกันทุกไบต์) เคยนำเข้าแล้ว {dups.Count} ครั้ง — ตรวจก่อนยืนยันเพื่อไม่ลงซ้ำ");
        var lowConfidence = await _db.ImportStagedRows.CountAsync(r => r.SessionId == s.Id && r.CompanyId == s.CompanyId
            && (r.Status == ImportRowStatus.Valid || r.Status == ImportRowStatus.Warning) && r.Confidence < ImportRowConfidence.ReviewThreshold, ct);
        if (lowConfidence > 0) notes.Add($"{lowConfidence} แถวที่ระบบมั่นใจไม่ถึง {ImportRowConfidence.ReviewThreshold:0.00} (แถบเหลือง) — ตรวจก่อนยืนยัน");
        notes.AddRange(state.Notes);
        return new ImportBatchDetailDto(summary, progress, source, state.Columns, target?.Schema ?? Array.Empty<ImportFieldDto>(), tpl,
            s.ColumnMatchUsedAi, snap.IssueCounts, snap.Warnings, dups, notes);
    }

    private static ImportRowDto ToRowDto(ImportStagedRow row, ImportBatchState state, IReadOnlyDictionary<string, ImportFieldDto> fields)
    {
        var values = ImportJson.ReadValues(row.MappedJson);
        var raw = RawCells(row);
        var issues = Issues(row);
        var meta = ReadMeta(row.AiFeedbackIdsJson);
        var edited = MetaSet(meta, MetaEdited);
        var filled = MetaSet(meta, MetaFilled);
        var cells = new List<ImportCellDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ImportCellDto Cell(string field, ImportColumnPlanDto? col)
        {
            var value = values.GetValueOrDefault(field);
            var rawV = col != null && col.SourceIndex < raw.Count ? raw[col.SourceIndex] : null;
            var fi = issues.Where(i => string.Equals(i.Field, field, StringComparison.OrdinalIgnoreCase)).ToList();
            var st = edited.Contains(field) ? ImportCellState.UserEdited
                : filled.Contains(field) ? ImportCellState.AutoFilled
                : fi.Any(i => i.Severity == ImportIssueSeverity.Error) ? ImportCellState.Error
                : fi.Any(i => i.Severity == ImportIssueSeverity.Warning) ? ImportCellState.LowConfidence
                : !string.Equals(value ?? "", (rawV ?? "").Trim(), StringComparison.Ordinal) ? ImportCellState.Normalized
                : col != null && ImportRowConfidence.BelowReviewThreshold(col.Confidence) ? ImportCellState.LowConfidence
                : ImportCellState.Original;
            var label = st switch
            {
                ImportCellState.UserEdited => "✋ ผู้ใช้แก้",
                ImportCellState.AutoFilled => "⚙️ ระบบเติม",
                ImportCellState.Normalized => "⚙️ ระบบแปลงรูปแบบ",
                _ => "ตามไฟล์",
            };
            return new ImportCellDto(field, value, rawV, st, col?.Confidence, col?.UsedAi ?? false, label);
        }

        foreach (var col in state.Columns.Where(c => c.TargetField != null))
            if (seen.Add(col.TargetField!)) cells.Add(Cell(col.TargetField!, col));
        foreach (var kv in values)
            if (seen.Add(kv.Key)) cells.Add(Cell(kv.Key, null));

        var candidates = ImportJson.Deserialize<List<ImportMatchCandidateDto>>(meta.GetValueOrDefault(MetaCandidates)) ?? new List<ImportMatchCandidateDto>();
        var usedAi = meta.ContainsKey(MetaMatchUsedAi);
        var auto = meta.ContainsKey(MetaMatchAuto);
        ImportRowMatchDto? match = null;
        if (row.MatchedEntityId != null || candidates.Count > 0)
        {
            var srcLabel = meta.ContainsKey(MetaUserAction) && row.MatchKind == "User" ? "✋ ผู้ใช้เลือก"
                : usedAi ? "🤖 AI แนะนำ"
                : auto ? "⚙️ ระบบจับคู่ (เลขภาษี/รหัสตรง)"
                : "⚙️ ระบบแนะนำ";
            match = new ImportRowMatchDto(row.MatchedEntityId, candidates.FirstOrDefault(c => c.EntityId == row.MatchedEntityId)?.Label,
                row.MatchKind ?? "None", row.MatchScore ?? 0m, auto, usedAi, srcLabel, candidates);
        }
        return new ImportRowDto(row.Id, row.RowNo, row.DisplayKey, row.Status, RowStatusLabel(row.Status), row.Action, ActionLabel(row.Action),
            row.Confidence, cells, issues, match, row.UserEdited, usedAi, row.GroupKey, row.CommittedEntityType, row.CommittedEntityId, row.CommitError);
    }

    // ══════════════════════════════ JSON ของแถว ══════════════════════════════

    private const string MetaMatchFeedback = "match";
    private const string MetaMatchAi = "$matchAi";
    private const string MetaMatchUsedAi = "$matchUsedAi";
    private const string MetaMatchAuto = "$matchAuto";
    private const string MetaMatchRecorded = "$matchRecorded";
    private const string MetaCandidates = "$candidates";
    private const string MetaEdited = "$edited";
    private const string MetaFilled = "$filled";
    private const string MetaRejected = "$rejected";
    private const string MetaUserAction = "$userAction";
    private const string MetaExcluded = "$excluded";
    private const string MetaIncluded = "$included";
    private const string MetaSummary = "$summary";

    /// <summary><c>AiFeedbackIdsJson</c> — คีย์ "match" = feedback ของ ImportEntityMatch (สัญญา T0) · คีย์ขึ้นต้น "$" = ข้อมูลประกอบของแถวที่ pipeline เป็นเจ้าของ</summary>
    private static Dictionary<string, string?> ReadMeta(string? json)
        => ImportJson.Deserialize<Dictionary<string, string?>>(json) is { } d
            ? new Dictionary<string, string?>(d, StringComparer.Ordinal)
            : new Dictionary<string, string?>(StringComparer.Ordinal);

    private static string? WriteMeta(Dictionary<string, string?> meta) => meta.Count == 0 ? null : ImportJson.Serialize(meta);

    private static HashSet<string> MetaSet(IReadOnlyDictionary<string, string?> meta, string key)
        => new((meta.GetValueOrDefault(key) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    private static void SetMetaSet(Dictionary<string, string?> meta, string key, IEnumerable<string> values)
    {
        var v = string.Join("|", values.Distinct(StringComparer.OrdinalIgnoreCase));
        if (v.Length == 0) meta.Remove(key); else meta[key] = v;
    }

    private static List<ImportIssueDto> Issues(ImportStagedRow r) => ParseIssues(r.IssuesJson);

    private static List<ImportIssueDto> ParseIssues(string? json) => ImportJson.Deserialize<List<ImportIssueDto>>(json) ?? new List<ImportIssueDto>();

    private static List<string> RawCells(ImportStagedRow r) => ImportJson.Deserialize<List<string>>(r.RawJson) ?? new List<string>();

    // ══════════════════════════════ ป้าย (เซิร์ฟเวอร์เป็นเจ้าของ · F2 ข้อ 5) ══════════════════════════════

    internal static string SessionStatusLabel(ImportSessionStatus s) => s switch
    {
        ImportSessionStatus.Uploading => "กำลังอัปโหลด",
        ImportSessionStatus.Analyzing => "กำลังวิเคราะห์",
        ImportSessionStatus.MappingRequired => "ต้องจับคู่คอลัมน์",
        ImportSessionStatus.MappingCompleted => "จับคู่คอลัมน์แล้ว",
        ImportSessionStatus.Validating => "กำลังตรวจ",
        ImportSessionStatus.Ready => "พร้อมลงข้อมูล",
        ImportSessionStatus.Importing => "กำลังลงข้อมูล",
        ImportSessionStatus.Completed => "ลงข้อมูลครบแล้ว",
        ImportSessionStatus.Failed => "ลงไม่สำเร็จ",
        ImportSessionStatus.AwaitingDuplicateResolution => "รอตัดสินข้อมูลซ้ำ",
        ImportSessionStatus.Queued => "รอคิวประมวลผล",
        ImportSessionStatus.Staged => "รอตรวจและยืนยัน",
        ImportSessionStatus.Committing => "กำลังลงข้อมูล",
        ImportSessionStatus.PartiallyCompleted => "ลงได้บางแถว",
        ImportSessionStatus.Undone => "ย้อนการนำเข้าแล้ว",
        ImportSessionStatus.Cancelled => "ยกเลิกแล้ว",
        _ => "ไม่ทราบสถานะ",
    };

    private static string PhaseLabel(ImportBatchPhase p) => p switch
    {
        ImportBatchPhase.Extract => "อ่านไฟล์",
        ImportBatchPhase.Transform => "แปลงและตรวจข้อมูล",
        ImportBatchPhase.Stage => "พักข้อมูล",
        ImportBatchPhase.Review => "รอตรวจ",
        ImportBatchPhase.Commit => "ลงข้อมูล",
        ImportBatchPhase.Learn => "จำรูปแบบไฟล์",
        ImportBatchPhase.Undo => "ย้อนการนำเข้า",
        _ => "เริ่มต้น",
    };

    private static string RowStatusLabel(ImportRowStatus s) => s switch
    {
        ImportRowStatus.Pending => "รอตรวจ",
        ImportRowStatus.Valid => "พร้อมลง",
        ImportRowStatus.Warning => "มีคำเตือน",
        ImportRowStatus.Error => "ต้องแก้",
        ImportRowStatus.Excluded => "ตัดออก",
        ImportRowStatus.Committed => "ลงแล้ว",
        ImportRowStatus.Failed => "ลงไม่สำเร็จ",
        ImportRowStatus.Undone => "ย้อนแล้ว",
        _ => "ไม่ทราบสถานะ",
    };

    private static string ActionLabel(ImportRowAction a) => a switch
    {
        ImportRowAction.Create => "สร้างใหม่",
        ImportRowAction.Update => "อัปเดตรายเดิม",
        ImportRowAction.Merge => "เติมช่องว่างของรายเดิม",
        ImportRowAction.Skip => "ข้าม",
        _ => "ไม่ทราบ",
    };

    private static string UndoKindLabel(ImportUndoKind k) => k switch
    {
        ImportUndoKind.SoftDelete => "ลบรายการที่สร้าง",
        ImportUndoKind.RestoreBefore => "คืนค่าเดิม",
        ImportUndoKind.DeleteDraft => "ลบเอกสารร่าง",
        ImportUndoKind.ReverseEntry => "กลับรายการ",
        ImportUndoKind.CompensatingMovement => "ปรับสต็อกชดเชย",
        ImportUndoKind.Rollback => "คืนยอดยกมาเดิม",
        _ => "ย้อนไม่ได้",
    };

    internal static string MapSourceLabel(ImportMapSource s, bool usedAi) => s switch
    {
        ImportMapSource.Template => "📌 แม่แบบ",
        ImportMapSource.Exact => "ชื่อตรงกัน",
        ImportMapSource.Synonym => "📚 คำพ้อง",
        ImportMapSource.Learned => "🧠 เรียนรู้",
        ImportMapSource.DataPattern => "⚙️ รูปข้อมูล",
        ImportMapSource.Ai => usedAi ? "🤖 AI แนะนำ" : "⚙️ ระบบแนะนำ",
        ImportMapSource.User => "✋ ผู้ใช้เลือก",
        _ => "ยังไม่จับคู่",
    };

    private static readonly Dictionary<string, string> IssueLabels = new(StringComparer.Ordinal)
    {
        ["required-missing"] = "ช่องบังคับว่าง",
        ["date-order-assumed"] = "วันที่อ่านได้ทั้งวัน/เดือนและเดือน/วัน",
        ["date-invalid"] = "วันที่อ่านไม่ได้",
        ["amount-invalid"] = "จำนวนเงินอ่านไม่ได้",
        ["number-invalid"] = "ตัวเลขอ่านไม่ได้",
        ["integer-invalid"] = "ต้องเป็นจำนวนเต็ม",
        ["flag-invalid"] = "ค่าใช่/ไม่ใช่อ่านไม่ได้",
        ["taxid-invalid"] = "เลขผู้เสียภาษีไม่ถูกต้อง",
        ["taxid-leading-zero"] = "เลขผู้เสียภาษีขาดศูนย์นำหน้า",
        ["taxid-lost-precision"] = "เลขเสียหลักจาก Excel",
        ["reference-lost-precision"] = "เลขอ้างอิงเสียหลักจาก Excel",
        ["branch-invalid"] = "รหัสสาขาไม่ถูกต้อง",
        ["vat-rate-invalid"] = "อัตรา VAT ไม่ถูกต้อง",
        ["vat-rate-unusual"] = "อัตรา VAT ไม่ปกติ",
        ["vat-exempt"] = "ยกเว้น VAT",
        ["vat-exempt-as-zero"] = "สินค้ายกเว้น VAT เก็บเป็น 0%",
        ["enum-invalid"] = "ค่านอกตัวเลือก",
        ["text-truncated"] = "ข้อความยาวเกิน",
        ["duplicate-in-file"] = "ซ้ำกันในไฟล์",
        ["matched-existing"] = "มีอยู่แล้วในระบบ",
        ["match-name-only"] = "ชื่อคล้ายข้อมูลเดิม (ไม่มีเลขยืนยัน)",
        ["match-ai-new"] = "น่าจะเป็นรายใหม่",
        ["summary-row"] = "แถวยอดรวม (ตัดออก)",
        ["je-unbalanced"] = "สมุดรายวันไม่ลงตัว",
        ["je-no-amount"] = "บรรทัดไม่มียอด",
        ["je-both-sides"] = "บรรทัดมีทั้งเดบิตและเครดิต",
        ["citizenid-checksum"] = "เลขบัตรประชาชนไม่ผ่านการตรวจ",
        ["account-type-from-code"] = "ประเภทบัญชีเติมจากรหัส",
        ["contact-role-missing"] = "ไม่ได้ระบุลูกค้า/ผู้ขาย",
        ["skipped-existing"] = "ข้าม (มีอยู่แล้ว)",
    };

    /// <summary>ป้าย chip ของรหัสปัญหา — รหัสที่ยังไม่มีป้ายใช้ข้อความไทยของปัญหาแรก (ไม่โชว์รหัสอังกฤษให้ผู้ใช้)</summary>
    private static string IssueLabel(string code, string firstMessage)
        => IssueLabels.TryGetValue(code, out var l) ? l : firstMessage.Length <= 60 ? firstMessage : firstMessage[..60] + "…";
}
