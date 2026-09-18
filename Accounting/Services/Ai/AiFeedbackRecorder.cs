using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// Writes the per-call AiSuggestionFeedback row + updates AiUsageDaily
/// rollup. EVERY orchestrator path goes through here including the
/// failure / skipped paths — the row is the audit trail for "AI was
/// asked but said no / was unreachable" so the admin widget shows the
/// true call volume not just successes.
///
/// User-acceptance updates (UserChosenAnswer, UserAcceptedAi) are
/// written by the call-site via RecordUserChoiceAsync after the user
/// confirms in the UI.
/// </summary>
public interface IAiFeedbackRecorder
{
    Task<Guid> RecordCallAsync(AiFeedbackRecord record, CancellationToken ct);
    /// <param name="source">คำยืนยันนี้ตั้งใจแค่ไหน — ค่าตั้งต้นคือ
    /// <see cref="UserChoiceSource.Implicit"/> (อ่อนที่สุด) จุดที่รู้แน่ว่าผู้ใช้ลงมือ
    /// เลือกเองต้องส่ง <see cref="UserChoiceSource.Explicit"/> มาเอง</param>
    Task RecordUserChoiceAsync(Guid feedbackId, string chosenAnswer, bool acceptedAi,
        CancellationToken ct, UserChoiceSource source = UserChoiceSource.Implicit);

    /// <summary>Bulk-insert synthetic CHILD feedback rows (e.g. one per match in
    /// a bulk-bank-match plan) in a SINGLE SaveChanges instead of N round-trips.
    /// These carry zero tokens/cost (they are not real provider calls), so the
    /// daily usage rollup + budget cache are intentionally skipped. Returns the
    /// generated ids in the same order as the input; a row that fails to save
    /// yields Guid.Empty for the whole batch (best-effort — never throws).</summary>
    Task<IReadOnlyList<Guid>> RecordChildBatchAsync(IReadOnlyList<AiFeedbackRecord> records, CancellationToken ct);
}

public sealed record AiFeedbackRecord(
    Guid CompanyId,
    AiFeatureKey FeatureKey,
    string PromptHash,
    string PromptJson,
    string? ResponseJson,
    string? AiPrimaryAnswer,
    decimal? AiConfidence,
    string? LocalModelAnswer,
    decimal? LocalModelConfidence,
    string? LocalModelVersion,
    string? SourceEntityType,
    Guid? SourceEntityId,
    AiCallStatus Status,
    AiProviderType ProviderUsed,
    string? ModelVersion,
    int? LatencyMs,
    int? InputTokens,
    int? OutputTokens,
    decimal? CostUsd,
    Guid? CacheHitOfFeedbackId,
    string? ErrorMessage);

public class AiFeedbackRecorder : IAiFeedbackRecorder
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<AiFeedbackRecorder> _logger;
    private readonly IAiUsageAttributionResolver? _attribution;

    public AiFeedbackRecorder(AccountingDbContext db, ILogger<AiFeedbackRecorder> logger,
        IAiUsageAttributionResolver? attribution = null)
    { _db = db; _logger = logger; _attribution = attribution; }

    public async Task<Guid> RecordCallAsync(AiFeedbackRecord record, CancellationToken ct)
    {
        AiSuggestionFeedback? row = null;
        // ป้ายกำกับ "ใครเรียก" — resolve ก่อนสร้างแถว เพื่อให้ทั้งแถวและ rollup
        // ใช้ค่าชุดเดียวกัน (ไม่งั้นสองที่จะไม่ตรงกันเวลากระทบยอด)
        var who = _attribution == null
            ? AiUsageAttribution.Unknown
            : await _attribution.ResolveAsync(record.CompanyId, ct);
        try
        {
            row = new AiSuggestionFeedback
            {
                CompanyId = record.CompanyId,
                Channel = who.Channel,
                BillingAccountId = who.BillingAccountId,
                BranchId = who.BranchId,
                ApiClientId = who.ApiClientId,
                UserId = who.UserId,
                IsSandbox = who.IsSandbox,
                FeatureKey = record.FeatureKey.ToString(),
                PromptHash = record.PromptHash,
                // Both columns are jsonb in Postgres — any non-JSON string here
                // throws 22P02 and leaves the entity stuck in the change tracker,
                // which then poisons every later SaveChanges in the request
                // (including AuditMiddleware's, observed in production). Coerce
                // anything that isn't already valid JSON into {"raw":"..."} so
                // the row always saves.
                PromptJson = CoerceJsonNonNull(record.PromptJson),
                ResponseJson = CoerceJsonNullable(record.ResponseJson),
                AiPrimaryAnswer = record.AiPrimaryAnswer,
                AiConfidence = record.AiConfidence,
                LocalModelAnswer = record.LocalModelAnswer,
                LocalModelConfidence = record.LocalModelConfidence,
                LocalModelVersion = record.LocalModelVersion,
                SourceEntityType = record.SourceEntityType,
                SourceEntityId = record.SourceEntityId,
                Status = record.Status,
                ProviderUsed = record.ProviderUsed,
                ModelVersion = record.ModelVersion,
                LatencyMs = record.LatencyMs,
                InputTokens = record.InputTokens,
                OutputTokens = record.OutputTokens,
                CostUsd = record.CostUsd,
                CacheHitOfFeedbackId = record.CacheHitOfFeedbackId,
                ErrorMessage = record.ErrorMessage,
            };
            _db.AiSuggestionFeedbacks.Add(row);
            await _db.SaveChangesAsync(ct);

            // Bump the daily rollup. Tolerant upsert — if two parallel
            // calls race we accept the small over-count rather than
            // serialising every AI call through a row lock.
            await UpsertDailyRollupAsync(record, ct);
            // สรุปรายวัน "แยกลูกค้า + ช่องทาง" — ตารางที่รายงานแยกรายลูกค้าอ่าน
            await UpsertTenantRollupAsync(record, who, ct);

            // Invalidate budget guard's 30s cache so the next call
            // reflects this call's contribution.
            AiBudgetGuard.InvalidateCache();
            return row.Id;
        }
        catch (Exception ex)
        {
            // Recording failure must NEVER bring the call site down — worst
            // case the admin widget under-counts. DETACH the failed entity
            // so it doesn't sit Added in the change tracker and re-throw on
            // the next SaveChangesAsync in this scope (e.g. AuditMiddleware).
            if (row != null)
            {
                try { _db.Entry(row).State = Microsoft.EntityFrameworkCore.EntityState.Detached; }
                catch { /* nothing else to do */ }
            }
            _logger.LogError(ex, "AiFeedback record failed for {Feature}", record.FeatureKey);
            return Guid.Empty;
        }
    }

    public async Task<IReadOnlyList<Guid>> RecordChildBatchAsync(
        IReadOnlyList<AiFeedbackRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return Array.Empty<Guid>();
        var rows = new List<AiSuggestionFeedback>(records.Count);
        // แถวลูกก็ต้องติดป้ายเหมือนกัน ไม่งั้นการเจาะดูรายคีย์/รายช่องทางจะเห็น
        // งาน bulk (จับคู่ธนาคารทั้งงวด) เป็น Unknown ทั้งกอง. resolve ครั้งเดียว
        // ต่อบริษัท — ทั้ง batch มักเป็นบริษัทเดียวกัน
        var whoByCompany = new Dictionary<Guid, AiUsageAttribution>();
        foreach (var cid in records.Select(r => r.CompanyId).Distinct())
        {
            whoByCompany[cid] = _attribution == null
                ? AiUsageAttribution.Unknown
                : await _attribution.ResolveAsync(cid, ct);
        }
        try
        {
            foreach (var record in records)
            {
                var childWho = whoByCompany.GetValueOrDefault(record.CompanyId, AiUsageAttribution.Unknown);
                var row = new AiSuggestionFeedback
                {
                    CompanyId = record.CompanyId,
                    Channel = childWho.Channel,
                    BillingAccountId = childWho.BillingAccountId,
                    BranchId = childWho.BranchId,
                    ApiClientId = childWho.ApiClientId,
                    UserId = childWho.UserId,
                    IsSandbox = childWho.IsSandbox,
                    FeatureKey = record.FeatureKey.ToString(),
                    PromptHash = record.PromptHash,
                    PromptJson = CoerceJsonNonNull(record.PromptJson),
                    ResponseJson = CoerceJsonNullable(record.ResponseJson),
                    AiPrimaryAnswer = record.AiPrimaryAnswer,
                    AiConfidence = record.AiConfidence,
                    LocalModelAnswer = record.LocalModelAnswer,
                    LocalModelConfidence = record.LocalModelConfidence,
                    LocalModelVersion = record.LocalModelVersion,
                    SourceEntityType = record.SourceEntityType,
                    SourceEntityId = record.SourceEntityId,
                    Status = record.Status,
                    ProviderUsed = record.ProviderUsed,
                    ModelVersion = record.ModelVersion,
                    LatencyMs = record.LatencyMs,
                    InputTokens = record.InputTokens,
                    OutputTokens = record.OutputTokens,
                    CostUsd = record.CostUsd,
                    CacheHitOfFeedbackId = record.CacheHitOfFeedbackId,
                    ErrorMessage = record.ErrorMessage,
                };
                _db.AiSuggestionFeedbacks.Add(row);
                rows.Add(row);
            }
            // ONE round-trip for the whole batch (vs N). No rollup / budget
            // bump — these synthetic child rows carry zero tokens + cost.
            await _db.SaveChangesAsync(ct);
            return rows.Select(r => r.Id).ToList();
        }
        catch (Exception ex)
        {
            foreach (var r in rows)
            {
                try { _db.Entry(r).State = Microsoft.EntityFrameworkCore.EntityState.Detached; }
                catch { /* nothing else to do */ }
            }
            _logger.LogWarning(ex, "AiFeedback child batch failed ({Count} rows)", records.Count);
            return records.Select(_ => Guid.Empty).ToList();
        }
    }

    /// <summary>Return s when it parses as JSON; otherwise wrap it as a JSON
    /// object {"raw":"..."} so the jsonb column accepts it. Used because the
    /// AI provider's raw text isn't always JSON-formatted but the column is
    /// jsonb-typed; without this we hit Postgres 22P02 and the failed entity
    /// poisons later SaveChanges in the same request.</summary>
    private static string CoerceJsonNonNull(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "{}";
        try { using var _ = System.Text.Json.JsonDocument.Parse(s); return s; }
        catch { return System.Text.Json.JsonSerializer.Serialize(new { raw = s }); }
    }

    private static string? CoerceJsonNullable(string? s)
    {
        if (s == null) return null;
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { using var _ = System.Text.Json.JsonDocument.Parse(s); return s; }
        catch { return System.Text.Json.JsonSerializer.Serialize(new { raw = s }); }
    }

    public async Task RecordUserChoiceAsync(Guid feedbackId, string chosenAnswer, bool acceptedAi,
        CancellationToken ct, UserChoiceSource source = UserChoiceSource.Implicit)
    {
        if (feedbackId == Guid.Empty) return;
        try
        {
            var row = await _db.AiSuggestionFeedbacks.FirstOrDefaultAsync(f => f.Id == feedbackId, ct);
            if (row == null) return;
            // นับ "ตัดสินใจแล้ว" เพิ่มเฉพาะครั้งแรก — ผู้ใช้เปลี่ยนใจแก้ซ้ำได้
            // ถ้านับทุกครั้งอัตรายอมรับจะเพี้ยน (ตัวหารโตกว่าจำนวน call จริง)
            var firstDecision = row.UserChosenAt == null;
            var wasAccepted = row.UserAcceptedAi == true;
            row.UserChosenAnswer = chosenAnswer;
            row.UserChosenAt = DateTime.UtcNow;
            row.UserAcceptedAi = acceptedAi;
            row.UserChoiceOrigin = source;
            await _db.SaveChangesAsync(ct);

            // สะท้อนเข้าสรุปรายวันของลูกค้ารายนั้น — อัตรา "AI แม่นในสายตา
            // ผู้ใช้จริง" คำนวณจาก UserAcceptedAi / UserReviewed
            await BumpTenantReviewAsync(row, firstDecision, wasAccepted, acceptedAi, ct);

            // ── ONLINE LEARNING (train ไปเลย) ───────────────────────────
            // The moment a user confirms or overrides, fold the ground
            // truth into AiSuggestionMemory so the very next suggestion for
            // the same input returns the learned answer — no nightly job,
            // no manual "train" click. Keyed by the feedback row's
            // PromptHash, which suggestion endpoints set to a stable
            // business key (contactId, normalised name, …). Skipped when
            // the key is empty (legacy rows) or the answer is blank.
            await LearnInlineAsync(row.CompanyId, row.FeatureKey, row.PromptHash, chosenAnswer, source, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiFeedback user-choice update failed for {Id}", feedbackId);
        }
    }

    /// <summary>Upsert the per-(company, feature, input) learned answer.
    /// First confirmation already counts — a single explicit user choice
    /// for an exact input is authoritative, so the model "learns ไปเลย".
    /// A persistent override (Override &gt; Accept) flips the stored answer
    /// to the new value. Confidence = Accept / (Accept + Override).</summary>
    private async Task LearnInlineAsync(Guid companyId, string featureKey, string inputKey,
        string chosenAnswer, UserChoiceSource source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputKey) || string.IsNullOrWhiteSpace(chosenAnswer)) return;
        if (inputKey.Length > 256) inputKey = inputKey[..256];
        try
        {
            var mem = await _db.AiSuggestionMemories.FirstOrDefaultAsync(
                m => m.CompanyId == companyId && m.FeatureKey == featureKey && m.InputKey == inputKey, ct);
            if (mem == null)
            {
                mem = new AiSuggestionMemory
                {
                    CompanyId = companyId, FeatureKey = featureKey, InputKey = inputKey,
                    LearnedAnswer = chosenAnswer, AcceptCount = 1, OverrideCount = 0,
                    ExplicitAcceptCount = source == UserChoiceSource.Explicit ? 1 : 0,
                    Confidence = 1m, LastLearnedAt = DateTime.UtcNow,
                };
                _db.AiSuggestionMemories.Add(mem);
            }
            else if (string.Equals(mem.LearnedAnswer, chosenAnswer, StringComparison.Ordinal))
            {
                mem.AcceptCount++;
                if (source == UserChoiceSource.Explicit) mem.ExplicitAcceptCount++;
            }
            else
            {
                mem.OverrideCount++;
                // A challenger that has now out-voted the incumbent takes over.
                if (mem.OverrideCount > mem.AcceptCount)
                {
                    mem.LearnedAnswer = chosenAnswer;
                    mem.AcceptCount = 1;
                    // คำตอบใหม่เริ่มนับของตัวเอง — ยอด "ตั้งใจ" ของคำตอบเก่าใช้แทนกันไม่ได้
                    mem.ExplicitAcceptCount = source == UserChoiceSource.Explicit ? 1 : 0;
                    mem.OverrideCount = 0;
                }
            }
            var total = mem.AcceptCount + mem.OverrideCount;
            mem.Confidence = total > 0 ? Math.Round((decimal)mem.AcceptCount / total, 4) : 1m;
            mem.LastLearnedAt = DateTime.UtcNow;
            mem.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException dup) when (IsUniqueViolation(dup))
        {
            // แข่งกันเขียน: สองคำขอ (เช่นผู้ใช้ยืนยันหลายช่องรวดเดียว) หา null
            // พร้อมกันแล้ว insert ทั้งคู่ → ชน unique index (company, feature, input).
            // ทิ้งตัวที่ insert ไม่สำเร็จ แล้วรวมยอดเข้าแถวที่มีอยู่จริงแทน
            DetachPendingMemories();
            try
            {
                var existing = await _db.AiSuggestionMemories.FirstOrDefaultAsync(
                    m => m.CompanyId == companyId && m.FeatureKey == featureKey && m.InputKey == inputKey, ct);
                if (existing != null)
                {
                    if (string.Equals(existing.LearnedAnswer, chosenAnswer, StringComparison.Ordinal))
                    {
                        existing.AcceptCount++;
                        if (source == UserChoiceSource.Explicit) existing.ExplicitAcceptCount++;
                    }
                    else
                    {
                        existing.OverrideCount++;
                    }
                    var t = existing.AcceptCount + existing.OverrideCount;
                    existing.Confidence = t > 0 ? Math.Round((decimal)existing.AcceptCount / t, 4) : 1m;
                    existing.LastLearnedAt = DateTime.UtcNow;
                    existing.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
            }
            catch (Exception retryEx)
            {
                _logger.LogWarning(retryEx, "Online-learning merge-after-race failed feature={Feature}", featureKey);
                DetachPendingMemories();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Online-learning upsert failed feature={Feature} key={Key}", featureKey, inputKey);
            DetachPendingMemories();
        }
    }

    /// <summary>SaveChanges ที่ล้มเหลว **ไม่** ย้อนสถานะ ChangeTracker ให้ — entity ที่
    /// insert/update ไม่สำเร็จยังค้างเป็น Added/Modified อยู่ ทำให้ SaveChanges ครั้ง
    /// ถัดไปในคำขอเดียวกัน (เช่น AuditMiddleware ตอนจบ request) พยายามเขียนซ้ำแล้ว
    /// พังเป็น 500 ให้ผู้ใช้ ทั้งที่การเรียนรู้ของ AI เป็นงานเบื้องหลังที่ล้มเหลวได้.
    /// ตัดออกจาก tracker เสมอเมื่อ save ไม่ผ่าน.</summary>
    private void DetachPendingMemories()
    {
        foreach (var e in _db.ChangeTracker.Entries<AiSuggestionMemory>().ToList())
            if (e.State is EntityState.Added or EntityState.Modified)
                e.State = EntityState.Detached;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.GetType().Name == "PostgresException"
           && (ex.InnerException.Message.Contains("23505")
               || ex.InnerException.Message.Contains("duplicate key value"));

    /// <summary>สรุปรายวัน "แยกลูกค้า + ช่องทาง" — ตารางที่รายงานรายลูกค้าอ่าน
    /// (แถวระดับ call โตเป็นล้าน สแกนทั้งปีไม่ไหว).
    /// best-effort เหมือน rollup รวม: พลาดแล้วรายงานขาดไปนิด ดีกว่าทำให้
    /// AI call ที่สำเร็จแล้วล้ม</summary>
    private async Task UpsertTenantRollupAsync(
        AiFeedbackRecord record, AiUsageAttribution who, CancellationToken ct)
    {
        if (record.CompanyId == Guid.Empty) return;
        try
        {
            var today = DateTime.UtcNow.Date;
            var featureKey = record.FeatureKey.ToString();
            var row = await _db.AiUsageDailyTenants.FirstOrDefaultAsync(
                u => u.UsageDate == today
                     && u.CompanyId == record.CompanyId
                     && u.ProviderType == record.ProviderUsed
                     && u.FeatureKey == featureKey
                     && u.Channel == who.Channel, ct);
            if (row == null)
            {
                row = new AiUsageDailyTenant
                {
                    UsageDate = today,
                    CompanyId = record.CompanyId,
                    BillingAccountId = who.BillingAccountId,
                    ProviderType = record.ProviderUsed,
                    FeatureKey = featureKey,
                    Channel = who.Channel,
                    IsSandbox = who.IsSandbox,
                };
                _db.AiUsageDailyTenants.Add(row);
            }

            row.CallsTotal++;
            switch (record.Status)
            {
                case AiCallStatus.Success: row.CallsAi++; break;
                case AiCallStatus.Cached: row.CallsCached++; break;
                case AiCallStatus.Failed:
                case AiCallStatus.InvalidResponse: row.CallsFailed++; break;
                case AiCallStatus.BudgetExceeded: row.CallsBudgetBlocked++; break;
                case AiCallStatus.NoProvider: row.CallsNoProvider++; break;
                // ไม่เคยยิง provider เลย = ประหยัดเต็ม ๆ (ไม่ต้องมีเงื่อนไข
                // LocalModelAnswer เพราะสถานะนี้แปลว่ามีคำตอบจากในบ้านแน่นอน)
                case AiCallStatus.LocalServed: row.CallsLocalServed++; break;
                case AiCallStatus.Skipped:
                    // "ข้าม" ที่ local ตอบแทนได้จริง = ครั้งที่ประหยัดเงินไป
                    // (ตัวชี้วัด sovereignty ตามกฎเหล็ก #1). ข้ามที่ไม่มีคำตอบ
                    // local เลย ไม่ใช่การประหยัด — เป็นฟีเจอร์ที่ยังไม่มีนักเรียน
                    if (!string.IsNullOrEmpty(record.LocalModelAnswer)) row.CallsLocalServed++;
                    break;
            }

            row.InputTokensTotal += record.InputTokens ?? 0;
            row.OutputTokensTotal += record.OutputTokens ?? 0;
            row.CostUsdTotal += record.CostUsd ?? 0m;
            // เก็บผลรวม + ตัวหาร ไม่เก็บค่าเฉลี่ย — รายงานต้องรวมข้าม feature/
            // ช่องทาง/วัน ตลอดเวลา และ "เฉลี่ยของเฉลี่ย" ผิดเมื่อจำนวน call ต่างกัน
            if (record.LatencyMs is > 0)
            {
                row.LatencySumMs += record.LatencyMs.Value;
                row.LatencySamples++;
            }
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AiUsageDailyTenant upsert failed (non-fatal)");
        }
    }

    /// <summary>อัปเดตสถิติ "ผู้ใช้ตัดสินใจแล้ว" กลับเข้าสรุปรายวันของวันที่
    /// **เกิด call** (ไม่ใช่วันที่กดยืนยัน) — ไม่งั้นอัตรายอมรับของเดือนหนึ่ง
    /// จะไปโผล่อีกเดือนเมื่อผู้ใช้มายืนยันช้า</summary>
    private async Task BumpTenantReviewAsync(
        AiSuggestionFeedback row, bool firstDecision, bool wasAccepted, bool acceptedAi,
        CancellationToken ct)
    {
        if (!firstDecision && wasAccepted == acceptedAi) return;   // ไม่มีอะไรเปลี่ยน
        try
        {
            var day = row.CreatedAt.Date;
            var target = await _db.AiUsageDailyTenants.FirstOrDefaultAsync(
                u => u.UsageDate == day
                     && u.CompanyId == row.CompanyId
                     && u.ProviderType == row.ProviderUsed
                     && u.FeatureKey == row.FeatureKey
                     && u.Channel == row.Channel, ct);
            if (target == null) return;   // แถวเก่าก่อนมี rollup — ข้ามเงียบ

            if (firstDecision)
            {
                target.UserReviewed++;
                if (acceptedAi) target.UserAcceptedAi++;
            }
            else if (wasAccepted && !acceptedAi) target.UserAcceptedAi--;
            else if (!wasAccepted && acceptedAi) target.UserAcceptedAi++;

            if (target.UserAcceptedAi < 0) target.UserAcceptedAi = 0;
            target.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AiUsageDailyTenant review bump failed (non-fatal)");
        }
    }

    private async Task UpsertDailyRollupAsync(AiFeedbackRecord record, CancellationToken ct)
    {
        try
        {
            var today = DateTime.UtcNow.Date;
            var featureKey = record.FeatureKey.ToString();
            var row = await _db.AiUsageDailies.FirstOrDefaultAsync(
                u => u.UsageDate == today && u.ProviderType == record.ProviderUsed && u.FeatureKey == featureKey, ct);
            if (row == null)
            {
                row = new AiUsageDaily
                {
                    UsageDate = today,
                    ProviderType = record.ProviderUsed,
                    FeatureKey = featureKey,
                };
                _db.AiUsageDailies.Add(row);
            }
            row.CallsAttempted++;
            switch (record.Status)
            {
                case AiCallStatus.Success: row.CallsSuccessful++; break;
                case AiCallStatus.Cached: row.CallsCached++; break;
                case AiCallStatus.Failed:
                case AiCallStatus.InvalidResponse: row.CallsFailed++; break;
                case AiCallStatus.BudgetExceeded: row.CallsBudgetBlocked++; break;
            }
            row.InputTokensTotal += record.InputTokens ?? 0;
            row.OutputTokensTotal += record.OutputTokens ?? 0;
            row.CostUsdTotal += record.CostUsd ?? 0m;
            if (record.LatencyMs.HasValue && row.CallsSuccessful > 0)
            {
                row.AvgLatencyMs = (int)(((long)row.AvgLatencyMs * (row.CallsSuccessful - 1) + record.LatencyMs.Value)
                                          / row.CallsSuccessful);
            }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AiUsageDaily upsert failed (non-fatal)");
        }
    }
}
