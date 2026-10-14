using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Import;

/// <summary>
/// คลังแม่แบบจับคู่คอลัมน์ต่อบริษัท (รอบ 205 ทีม A · สัญญาที่ <see cref="IImportMappingTemplateStore"/>) + ที่มาเดียวของ "คู่ที่เรียนไว้"
///
/// <para><b>Tenant</b>: ทุก query กรอง <c>CompanyId</c> (กฎ M) · <b>คีย์</b>: <c>EntityType</c> เก็บแบบ normalize
/// (<see cref="ImportExportPermissionScope.NormalizeEntityType"/>) + <see cref="ImportHeaderSignature.Compute"/> ⇒ "chart-of-accounts" กับ
/// "ChartOfAccounts" เป็นแม่แบบเดียวกัน · <b>AutoCommitWhenClean</b> เปลี่ยนได้ทาง <see cref="UpdateAsync"/> เท่านั้น (opt-in · คำตัดสินข้อ 151 (3))</para>
///
/// <para><b>เรียนรู้ (กฎเหล็ก #1)</b>: <see cref="GetLearnedHeadersAsync"/> รวมแม่แบบ + การยืนยันการจับคู่ของผู้ใช้ ·
/// <see cref="RecordColumnMatchChoiceAsync"/> ปิดลูป feedback ของ ImportColumnMatch ด้วยคำตอบรูปมาตรฐาน (<see cref="ImportColumnMapper.CanonicalAnswer"/>)
/// ⇒ นักเรียน <c>ImportColumnMatchDistillationModel</c> เรียนจากที่เดียวกัน</para>
/// </summary>
public class ImportMappingTemplateStore : IImportMappingTemplateStore
{
    private readonly AccountingDbContext _db;
    private readonly IAiFeedbackRecorder _feedback;

    /// <summary>เพดานแถว feedback ที่อ่านต่อครั้ง (ล่าสุดก่อน) — กันการเรียนช้าเมื่อบริษัทนำเข้ามาก</summary>
    private const int FeedbackScan = 500;

    public ImportMappingTemplateStore(AccountingDbContext db, IAiFeedbackRecorder feedback)
    {
        _db = db;
        _feedback = feedback;
    }

    private static string Key(string? entityType) => ImportExportPermissionScope.NormalizeEntityType(entityType);

    public async Task<ImportMappingTemplate?> FindAsync(Guid companyId, string entityType, string headerSignature, CancellationToken ct = default)
    {
        var key = Key(entityType);
        var sig = (headerSignature ?? "").Trim().ToLowerInvariant();
        return await _db.ImportMappingTemplates.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.EntityType == key && t.HeaderSignature == sig)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ImportTemplateDto>> ListAsync(Guid companyId, string? entityType, CancellationToken ct = default)
    {
        var q = _db.ImportMappingTemplates.AsNoTracking().Where(t => t.CompanyId == companyId && !t.IsDeleted);
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            var key = Key(entityType);
            q = q.Where(t => t.EntityType == key);
        }
        var rows = await q.OrderByDescending(t => t.LastUsedAt ?? t.CreatedAt).ThenBy(t => t.Name).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ImportTemplateDto?> GetAsync(Guid companyId, Guid templateId, CancellationToken ct = default)
    {
        var t = await _db.ImportMappingTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == templateId && x.CompanyId == companyId && !x.IsDeleted, ct);
        return t == null ? null : ToDto(t);
    }

    public async Task<ImportTemplateDto> SaveFromBatchAsync(Guid companyId, Guid batchId, string? name, bool confirmedByUser, ImportActor actor,
        CancellationToken ct = default)
    {
        var batch = await _db.SmartImportSessions
            .FirstOrDefaultAsync(s => s.Id == batchId && s.CompanyId == companyId, ct)
            ?? throw new BusinessRuleException("ไม่พบชุดนำเข้านี้ — อาจถูกลบหรือเป็นของบริษัทอื่น");
        var maps = await _db.SmartImportColumnMappings.AsNoTracking()
            .Where(m => m.SessionId == batchId && !m.IsDeleted)
            .OrderBy(m => m.SourceIndex)
            .Select(m => new { m.SourceIndex, m.SourceHeader, m.TargetField })
            .ToListAsync(ct);
        if (maps.Count == 0)
            throw new BusinessRuleException("ชุดนำเข้านี้ยังไม่มีการจับคู่คอลัมน์ — จับคู่คอลัมน์ก่อนแล้วค่อยจำเป็นแม่แบบ");

        var headers = maps.Select(m => m.SourceHeader).ToList();
        var columns = maps.Select(m => new ImportTemplateColumnDto(m.SourceHeader, string.IsNullOrWhiteSpace(m.TargetField) ? null : m.TargetField))
            .ToList();
        var dto = await SaveColumnsAsync(companyId, batch.EntityType, headers, columns, batch.SourceSystem,
            name ?? DefaultName(batch.EntityType, batch.SourceSystem, batch.FileName), confirmedByUser, actor, ct);

        batch.MappingTemplateId = dto.Id;
        batch.HeaderSignature ??= dto.HeaderSignature;
        await _db.SaveChangesAsync(ct);
        return dto;
    }

    public async Task<ImportTemplateDto> SaveColumnsAsync(Guid companyId, string entityType, IReadOnlyList<string> headers,
        IReadOnlyList<ImportTemplateColumnDto> columns, string? sourceSystem, string? name, bool confirmedByUser, ImportActor actor,
        CancellationToken ct = default)
    {
        var key = Key(entityType);
        if (key.Length == 0) throw new BusinessRuleException("ไม่ระบุชนิดข้อมูลของแม่แบบ");
        var schema = ImportSchemas.For(entityType);
        var sig = ImportHeaderSignature.Compute(headers);
        var cleanCols = NormalizeColumns(columns, schema);
        var existing = await _db.ImportMappingTemplates
            .FirstOrDefaultAsync(t => t.CompanyId == companyId && !t.IsDeleted && t.EntityType == key && t.HeaderSignature == sig, ct);
        var now = DateTime.UtcNow;
        if (existing == null)
        {
            existing = new ImportMappingTemplate
            {
                CompanyId = companyId,
                EntityType = key,
                HeaderSignature = sig,
                Name = Trunc(string.IsNullOrWhiteSpace(name) ? DefaultName(entityType, sourceSystem, null) : name.Trim(), 200),
                CreatedBy = actor.Name,
            };
            _db.ImportMappingTemplates.Add(existing);
        }
        else
        {
            // transform ที่แม่แบบเดิมมี และคอลัมน์ยังไปที่ field เดิม ⇒ คงไว้ (ผู้เรียกที่ไม่มี transform ห้ามล้างของผู้ใช้เงียบ ๆ)
            var old = ReadColumns(existing.ColumnMapJson).ToDictionary(c => c.Header, StringComparer.Ordinal);
            cleanCols = cleanCols.Select(c => c.Transform == null && old.TryGetValue(c.Header, out var o)
                    && string.Equals(o.TargetField, c.TargetField, StringComparison.OrdinalIgnoreCase) && o.Transform != null
                ? c with { Transform = o.Transform } : c).ToList();
            if (!string.IsNullOrWhiteSpace(name)) existing.Name = Trunc(name.Trim(), 200);
            existing.UpdatedAt = now;
            existing.UpdatedBy = actor.Name;
        }
        existing.HeadersJson = ImportJson.Serialize(headers.Where(h => !string.IsNullOrWhiteSpace(h)).ToList());
        existing.ColumnMapJson = ImportJson.Serialize(cleanCols);
        existing.SourceSystem = string.IsNullOrWhiteSpace(sourceSystem) ? existing.SourceSystem : Trunc(sourceSystem.Trim(), 64);
        existing.ConfirmedByUser |= confirmedByUser;
        existing.UseCount += 1;
        existing.LastUsedAt = now;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (_db.Entry(existing).State == EntityState.Added)
        {
            // สองคำขอ commit ไฟล์หน้าตาเดียวกันพร้อมกัน ⇒ ชน unique (CompanyId, EntityType, HeaderSignature) — อีกฝั่งสร้างไปแล้ว:
            // ถอนแถวใหม่ทิ้งแล้วบันทึกลงแถวที่มีอยู่ (ครั้งเดียว · ล้มอีก = ล้มดังถึงผู้เรียก)
            _db.Entry(existing).State = EntityState.Detached;
            var winner = await _db.ImportMappingTemplates
                .FirstAsync(t => t.CompanyId == companyId && !t.IsDeleted && t.EntityType == key && t.HeaderSignature == sig, ct);
            winner.ColumnMapJson = existing.ColumnMapJson;
            winner.HeadersJson = existing.HeadersJson;
            winner.SourceSystem ??= existing.SourceSystem;
            winner.ConfirmedByUser |= confirmedByUser;
            winner.UseCount += 1;
            winner.LastUsedAt = now;
            winner.UpdatedAt = now;
            winner.UpdatedBy = actor.Name;
            await _db.SaveChangesAsync(ct);
            return ToDto(winner);
        }
        return ToDto(existing);
    }

    public async Task<ImportTemplateDto?> UpdateAsync(Guid companyId, Guid templateId, ImportTemplateUpsertRequest request, ImportActor actor,
        CancellationToken ct = default)
    {
        var t = await _db.ImportMappingTemplates
            .FirstOrDefaultAsync(x => x.Id == templateId && x.CompanyId == companyId && !x.IsDeleted, ct);
        if (t == null) return null;
        if (request.Name != null)
        {
            if (string.IsNullOrWhiteSpace(request.Name)) throw new BusinessRuleException("ชื่อแม่แบบว่างไม่ได้ — ตั้งชื่อที่จำได้ เช่น \"ผู้ติดต่อจาก PEAK\"");
            t.Name = Trunc(request.Name.Trim(), 200);
        }
        // ฝ่ายค้านรอบ ETL (F2 ข้อ 2 "เก็บได้ ≠ มีผล"): ยังไม่มีผู้อ่านค่านี้ (worker ยังไม่ลงให้อัตโนมัติ) ⇒ ห้ามรับ "เปิด" แล้วเงียบ —
        // ปฏิเสธพร้อมเหตุผล · หน้าเว็บล็อกช่องนี้ "ยังไม่เปิดใช้" · ค่าเดิมที่เคยเปิดไว้ถูกปิดเมื่อบันทึกแม่แบบครั้งถัดไป
        if (request.AutoCommitWhenClean)
            throw new BusinessRuleException("การลงข้อมูลอัตโนมัติเมื่อไฟล์ไม่มีปัญหายังไม่เปิดใช้ — ตอนนี้ทุกชุดต้องกด \"ยืนยันนำเข้า\" เอง (แม่แบบยังจำการจับคู่คอลัมน์ให้ตามเดิม)");
        t.AutoCommitWhenClean = false;
        if (request.SourceSystem != null) t.SourceSystem = string.IsNullOrWhiteSpace(request.SourceSystem) ? null : Trunc(request.SourceSystem.Trim(), 64);
        if (request.Columns != null)
        {
            // แก้ได้เฉพาะ field/transform ของหัวที่มีอยู่ในแม่แบบ (รวมหัวที่เดิม "ไม่ใช้") — ชุดหัว = คีย์ (signature) ของแม่แบบ เปลี่ยนไม่ได้ที่นี่ ·
            // คอลัมน์ที่ไม่ได้ส่งมา = คงเดิม (ส่งบางคอลัมน์ต้องไม่ล้างการจับคู่ที่เหลือเงียบ ๆ)
            var current = ReadColumns(t.ColumnMapJson).ToList();
            var headerKeys = new HashSet<string>(current.Select(c => c.Header), StringComparer.Ordinal);
            foreach (var h in ReadHeaders(t.HeadersJson)) headerKeys.Add(ImportHeaderSignature.Normalize(h));
            var unknown = request.Columns.Select(c => c.Header).Where(h => !headerKeys.Contains(ImportHeaderSignature.Normalize(h))).ToList();
            if (unknown.Count > 0)
                throw new BusinessRuleException($"หัวคอลัมน์ \"{string.Join("\", \"", unknown.Take(3))}\" ไม่อยู่ในแม่แบบนี้ — แม่แบบผูกกับชุดหัวตารางของไฟล์ " +
                                                "ถ้าไฟล์เปลี่ยนหัวตาราง ให้นำเข้าไฟล์ใหม่แล้วจำเป็นแม่แบบใหม่");
            var changes = NormalizeColumns(request.Columns, ImportSchemas.For(t.EntityType)).ToDictionary(c => c.Header, StringComparer.Ordinal);
            var merged = current.Select(c => changes.TryGetValue(c.Header, out var n) ? n : c).ToList();
            merged.AddRange(changes.Values.Where(n => current.All(c => c.Header != n.Header)));
            t.ColumnMapJson = ImportJson.Serialize(merged);
            t.ConfirmedByUser = true;
        }
        t.UpdatedAt = DateTime.UtcNow;
        t.UpdatedBy = actor.Name;
        await _db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    public async Task<bool> DeleteAsync(Guid companyId, Guid templateId, ImportActor actor, CancellationToken ct = default)
    {
        var t = await _db.ImportMappingTemplates
            .FirstOrDefaultAsync(x => x.Id == templateId && x.CompanyId == companyId && !x.IsDeleted, ct);
        if (t == null) return false;
        t.IsDeleted = true;
        t.UpdatedAt = DateTime.UtcNow;
        t.UpdatedBy = actor.Name;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task MarkUsedAsync(Guid companyId, Guid templateId, CancellationToken ct = default)
    {
        var t = await _db.ImportMappingTemplates
            .FirstOrDefaultAsync(x => x.Id == templateId && x.CompanyId == companyId && !x.IsDeleted, ct);
        if (t == null) return;
        t.UseCount += 1;
        t.LastUsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ImportLearnedHeader>> GetLearnedHeadersAsync(Guid companyId, string? entityType, CancellationToken ct = default)
    {
        var key = string.IsNullOrWhiteSpace(entityType) ? null : Key(entityType);
        var votes = new Dictionary<(string Entity, string Header), Dictionary<string, int>>();
        void Vote(string entity, string header, string field, int w)
        {
            if (entity.Length == 0 || header.Length == 0 || string.IsNullOrWhiteSpace(field)) return;
            if (key != null && entity != key) return;
            if (!votes.TryGetValue((entity, header), out var d)) votes[(entity, header)] = d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            d[field.Trim()] = d.GetValueOrDefault(field.Trim()) + w;
        }

        // ── แม่แบบ (ยืนยันโดยผู้ใช้ = 3 · จำจาก commit = 1) + ความถี่การใช้ (≤ 5) ──
        var tq = _db.ImportMappingTemplates.AsNoTracking().Where(t => t.CompanyId == companyId && !t.IsDeleted);
        if (key != null) tq = tq.Where(t => t.EntityType == key);
        var templates = await tq.Select(t => new { t.EntityType, t.ColumnMapJson, t.ConfirmedByUser, t.UseCount }).ToListAsync(ct);
        foreach (var t in templates)
            foreach (var c in ReadColumns(t.ColumnMapJson))
                if (c.TargetField != null)
                    Vote(Key(t.EntityType), c.Header, c.TargetField, (t.ConfirmedByUser ? 3 : 1) + Math.Min(5, Math.Max(0, t.UseCount - 1)));

        // ── การยืนยันการจับคู่คอลัมน์ (ImportColumnMatch) — คำถาม = PromptJson (หัว) · คำตอบผู้ใช้ = UserChosenAnswer (field) ──
        var featureKey = nameof(AiFeatureKey.ImportColumnMatch);
        var rows = await _db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.CompanyId == companyId && f.FeatureKey == featureKey && f.UserChosenAnswer != null)
            .OrderByDescending(f => f.CreatedAt)
            .Take(FeedbackScan)
            .Select(f => new { f.PromptJson, f.UserChosenAnswer })
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            var (entity, headers) = ReadPromptColumns(r.PromptJson);
            if (entity == null) continue;
            foreach (var (idx, field) in ReadCanonical(r.UserChosenAnswer))
                if (field != null && headers.TryGetValue(idx, out var h))
                    Vote(Key(entity), ImportHeaderSignature.Normalize(h), field, 2);
        }

        var result = new List<ImportLearnedHeader>();
        foreach (var ((entity, header), d) in votes)
        {
            var ranked = d.OrderByDescending(kv => kv.Value).ToList();
            // ขัดกันน้ำหนักเท่ากัน = ยังไม่รู้ ⇒ ไม่เรียน (ไม่เดา — F2 ข้อ 3)
            if (ranked.Count > 1 && ranked[0].Value == ranked[1].Value) continue;
            result.Add(new ImportLearnedHeader(entity, header, ranked[0].Key, ranked[0].Value));
        }
        return result.OrderBy(r => r.EntityType, StringComparer.Ordinal).ThenBy(r => r.Header, StringComparer.Ordinal).ToList();
    }

    public async Task<bool> RecordColumnMatchChoiceAsync(Guid companyId, Guid feedbackId, IReadOnlyList<ImportColumnPlanDto> finalColumns,
        CancellationToken ct = default)
    {
        if (feedbackId == Guid.Empty) return false;
        var featureKey = nameof(AiFeatureKey.ImportColumnMatch);
        var row = await _db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.Id == feedbackId && f.CompanyId == companyId && f.FeatureKey == featureKey)
            .Select(f => new { f.PromptJson, f.ResponseJson, f.LocalModelAnswer })
            .FirstOrDefaultAsync(ct);
        if (row == null) return false;

        // เทียบเฉพาะคอลัมน์ที่ถูกถามในรอบนั้น (prompt) — คอลัมน์อื่นชั้น local ตัดสินไปแล้ว ไม่ใช่คำตอบของครู/นักเรียน
        var (_, asked) = ReadPromptColumns(row.PromptJson);
        var askedIdx = asked.Count > 0 ? asked.Keys.ToHashSet() : finalColumns.Select(c => c.SourceIndex).ToHashSet();
        var final = ImportColumnMapper.CanonicalAnswer(finalColumns.Where(c => askedIdx.Contains(c.SourceIndex))
            .Select(c => (c.SourceIndex, c.TargetField)));

        // สิ่งที่ระบบเสนอ: คำตอบครู (ResponseJson ของ provider) ถ้ามี ไม่งั้นคำตอบนักเรียน (LocalModelAnswer รูปมาตรฐานเดียวกัน)
        string? shown = null;
        var aiPairs = ReadAiMappings(row.ResponseJson);
        if (aiPairs != null)
            shown = ImportColumnMapper.CanonicalAnswer(askedIdx.Select(i => (i, aiPairs.TryGetValue(i, out var f) ? f : null)));
        else if (!string.IsNullOrWhiteSpace(row.LocalModelAnswer))
            shown = row.LocalModelAnswer;
        var accepted = shown != null && string.Equals(shown, final, StringComparison.Ordinal);
        await _feedback.RecordUserChoiceAsync(feedbackId, final, accepted, ct, UserChoiceSource.Explicit);
        return true;
    }

    // ═══════════════════════ helpers ═══════════════════════

    private static string DefaultName(string? entityType, string? sourceSystem, string? fileName)
    {
        var src = ImportSourceSeed.Label(sourceSystem);
        var tail = src ?? (string.IsNullOrWhiteSpace(fileName) ? "ไฟล์ของบริษัท" : Path.GetFileNameWithoutExtension(fileName.Trim()));
        return Trunc($"{ImportSchemas.EntityLabel(entityType)} — {tail}", 200);
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>header → normalize · field นอก schema ⇒ ล้มดัง (แม่แบบที่ชี้ field ที่ไม่มีจริง = แผนที่เสีย) · หัวซ้ำ ⇒ ตัวแรก</summary>
    private static List<ImportTemplateColumnDto> NormalizeColumns(IEnumerable<ImportTemplateColumnDto> columns, IReadOnlyList<ImportFieldDto>? schema)
    {
        var canonical = schema?.ToDictionary(f => f.Field, f => f.Field, StringComparer.OrdinalIgnoreCase);
        var list = new List<ImportTemplateColumnDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in columns)
        {
            var h = ImportHeaderSignature.Normalize(c.Header);
            if (h.Length == 0 || !seen.Add(h)) continue;
            string? field = string.IsNullOrWhiteSpace(c.TargetField) ? null : c.TargetField.Trim();
            if (field != null && canonical != null)
            {
                if (!canonical.TryGetValue(field, out var canon))
                    throw new BusinessRuleException($"คอลัมน์ \"{c.Header}\" ชี้ไปที่ \"{field}\" ซึ่งไม่ใช่ช่องของข้อมูลชนิดนี้ — เลือกช่องจากรายการ หรือเลือก \"ไม่ใช้คอลัมน์นี้\"");
                field = canon;
            }
            list.Add(new ImportTemplateColumnDto(h, field, c.Transform));
        }
        return list;
    }

    private static IReadOnlyList<ImportTemplateColumnDto> ReadColumns(string? json)
    {
        try { return ImportJson.Deserialize<List<ImportTemplateColumnDto>>(json) ?? new List<ImportTemplateColumnDto>(); }
        catch (JsonException) { return Array.Empty<ImportTemplateColumnDto>(); }
    }

    private static IReadOnlyList<string> ReadHeaders(string? json)
    {
        try { return ImportJson.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    private static ImportTemplateDto ToDto(ImportMappingTemplate t)
        => new(t.Id, ImportSchemas.CanonicalEntityType(t.EntityType) ?? t.EntityType, t.Name, t.HeaderSignature, t.SourceSystem,
            ReadHeaders(t.HeadersJson), ReadColumns(t.ColumnMapJson), t.UseCount, t.LastUsedAt, t.ConfirmedByUser, t.AutoCommitWhenClean, t.CreatedAt);

    /// <summary>prompt ของ ImportColumnMatch (<c>ImportPrompts.BuildColumnMatch</c>) → (entity_type, source_index → source_header)</summary>
    internal static (string? Entity, Dictionary<int, string> Headers) ReadPromptColumns(string? promptJson)
    {
        var map = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(promptJson)) return (null, map);
        try
        {
            using var doc = JsonDocument.Parse(promptJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, map);
            var entity = root.TryGetProperty("entity_type", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (root.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
                foreach (var c in cols.EnumerateArray())
                    if (c.TryGetProperty("source_index", out var si) && si.TryGetInt32(out var idx)
                        && c.TryGetProperty("source_header", out var sh) && sh.ValueKind == JsonValueKind.String)
                        map.TryAdd(idx, sh.GetString() ?? "");
            return (entity, map);
        }
        catch (JsonException) { return (null, map); }
    }

    /// <summary>คำตอบรูปมาตรฐาน (<see cref="ImportColumnMapper.CanonicalAnswer"/>) → (index, field)</summary>
    internal static IEnumerable<(int Index, string? Field)> ReadCanonical(string? json)
    {
        var map = ReadAiMappings(json);
        return map == null ? Array.Empty<(int, string?)>() : map.Select(kv => (kv.Key, kv.Value));
    }

    /// <summary><c>{"mappings":[{"sourceIndex":…,"targetField":…}]}</c> (ครูหรือรูปมาตรฐาน) → index → field · อ่านไม่ได้ ⇒ null</summary>
    internal static Dictionary<int, string?>? ReadAiMappings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("mappings", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var d = new Dictionary<int, string?>();
            foreach (var m in arr.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("sourceIndex", out var si) || !si.TryGetInt32(out var idx)) continue;
                var f = m.TryGetProperty("targetField", out var tf) && tf.ValueKind == JsonValueKind.String ? tf.GetString() : null;
                d.TryAdd(idx, string.IsNullOrWhiteSpace(f) ? null : f.Trim());
            }
            return d;
        }
        catch (JsonException) { return null; }
    }
}
