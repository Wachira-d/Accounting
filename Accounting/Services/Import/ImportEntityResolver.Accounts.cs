using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Import;

/// <summary>หาบัญชีในผังบริษัทให้แถวนำเข้า — เจ้าของ: <b>ทีม D</b> (สัญญาที่ <see cref="IImportEntityResolver.ResolveAccountsAsync"/>) ·
/// ตัวทำงานจริงคือ <see cref="ImportAccountMatcher"/> (ฉีดผ่าน constructor ใน <c>ImportEntityResolver.cs</c> — ทีม B รวม constructor เป็นตัวเดียว)</summary>
public partial class ImportEntityResolver
{
    private readonly ImportAccountMatcher? _accountMatcher;

    public Task<IReadOnlyList<ImportAccountMatchResult>> ResolveAccountsAsync(Guid companyId, IReadOnlyList<ImportAccountQuery> queries,
        ImportResolveOptions options, CancellationToken ct = default)
        => (_accountMatcher ?? throw new InvalidOperationException(
                "ImportEntityResolver ไม่ได้รับ ImportAccountMatcher — ต่อสาย constructor (รอบ 205 ทีม D) ให้ครบ"))
            .ResolveAsync(companyId, queries, options, ct);
}

/// <summary>ผังบัญชีของบริษัท + ความจำการจับคู่ (โหลดครั้งเดียวต่องาน)</summary>
public sealed record ImportAccountChart(IReadOnlyList<ImportChartAccount> Accounts, IReadOnlyList<ImportAccountMemory> Memory)
{
    public ImportChartAccount? Find(Guid id) => Accounts.FirstOrDefault(a => a.AccountId == id);
}

/// <summary>
/// **หาบัญชีปลายทางของรหัส/ชื่อบัญชีจากไฟล์นำเข้า** (รอบ 205 ทีม D · IMPORT_ETL_REVIEW §2.4/§2.6 · <c>AiFeatureKey.ImportAccountMap</c> = 58)
///
/// <para>ลำดับ: <see cref="ImportAccountCodeResolver"/> (local — รหัสตรง → ความจำ AccountMappings ของบริษัท → ชื่อคล้าย → หมวดเลขหน้า) ก่อนเสมอ ·
/// เหลือแถวที่ local ไม่ถึงเกณฑ์ ⇒ ถาม AI ผ่านช่องเดียว <see cref="AskAccountMapAiAsync"/> (กฎเหล็ก #1: orchestrator · student-first) ·
/// ด่านคำตอบ AI: รหัสต้องเป็นบัญชีลงยอดได้ในผังของบริษัทนี้ + ความมั่นใจ ≥ 0.70 · <b>kill-switch</b>: ปิด AI ทุกตัว ⇒ ผลเท่ากับชั้น local ทุกแถว ·
/// ปิดลูป (<c>RecordUserChoiceAsync</c>) ทำตอนผู้ใช้ยืนยันการจับคู่ (<c>MigrationWizardService.ApplyMappingsAsync</c>)</para>
/// <para>tenant: ผังกรอง <c>CompanyId</c> · ความจำ = AccountMappings ที่ session เป็นของบริษัทนี้ (AccountMapping ไม่มี CompanyId ⇒ join ผ่าน MigrationSession)</para>
/// </summary>
public class ImportAccountMatcher
{
    /// <summary>ความมั่นใจขั้นต่ำของคำตอบ AI ที่รับไปใช้ (CLAUDE.md กฎเหล็ก #1 ตัวอย่าง ✅)</summary>
    public const decimal AiMinConfidence = 0.70m;

    private readonly AccountingDbContext _db;
    private readonly Accounting.Services.Ai.IImportAiAugmenter? _importAi;
    private readonly ILogger<ImportAccountMatcher>? _logger;

    public ImportAccountMatcher(AccountingDbContext db, Accounting.Services.Ai.IImportAiAugmenter? importAi = null,
        ILogger<ImportAccountMatcher>? logger = null)
    {
        _db = db;
        _importAi = importAi;
        _logger = logger;
    }

    /// <summary>ผังที่ใช้งานอยู่ของบริษัท (ไม่ลบ · เปิดใช้) + ความจำจาก session ที่ commit แล้ว · <paramref name="excludeSessionId"/> = session ที่กำลังทำ (ไม่จำตัวเอง)</summary>
    public async Task<ImportAccountChart> LoadChartAsync(Guid companyId, CancellationToken ct = default, Guid? excludeSessionId = null)
    {
        var rows = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive)
            .Select(a => new { a.Id, a.AccountCode, a.AccountName, a.AccountType, a.ParentAccountId })
            .ToListAsync(ct);
        var parents = rows.Where(r => r.ParentAccountId.HasValue).Select(r => r.ParentAccountId!.Value).ToHashSet();
        var chart = rows.Select(r => new ImportChartAccount(r.Id, r.AccountCode, r.AccountName, r.AccountType, !parents.Contains(r.Id)))
            .OrderBy(a => a.Code, StringComparer.Ordinal).ToList();

        var memory = await (
                from m in _db.AccountMappings.AsNoTracking()
                join s in _db.MigrationSessions.AsNoTracking() on m.MigrationSessionId equals s.Id
                where s.CompanyId == companyId && !s.IsDeleted && !m.IsDeleted
                      && s.Status == MigrationStatus.Committed
                      && m.MappedAccountId != null
                      && (excludeSessionId == null || s.Id != excludeSessionId)
                group m by new { m.LegacyCode, m.MappedAccountId } into g
                select new { g.Key.LegacyCode, AccountId = g.Key.MappedAccountId!.Value, Times = g.Count(), Name = g.Max(x => x.LegacyName) })
            .ToListAsync(ct);
        return new ImportAccountChart(chart,
            memory.Select(m => new ImportAccountMemory(m.LegacyCode, m.Name, m.AccountId, m.Times)).ToList());
    }

    /// <summary>หาบัญชีของหลายแถวในครั้งเดียว — คืนตามลำดับ <paramref name="queries"/></summary>
    public async Task<IReadOnlyList<ImportAccountMatchResult>> ResolveAsync(Guid companyId, IReadOnlyList<ImportAccountQuery> queries,
        ImportResolveOptions options, CancellationToken ct = default, ImportAccountChart? chart = null)
    {
        if (queries.Count == 0) return Array.Empty<ImportAccountMatchResult>();
        chart ??= await LoadChartAsync(companyId, ct);
        var local = queries.Select(q => (Query: q, Local: ImportAccountCodeResolver.Resolve(q.Code, q.Name, chart.Accounts, chart.Memory))).ToList();

        IReadOnlyDictionary<string, AccountMapAiAnswer> ai = new Dictionary<string, AccountMapAiAnswer>();
        var leftovers = local.Where(x => !x.Local.AutoApplied).ToList();
        if (options.AllowAi && leftovers.Count > 0)
            ai = await AskAccountMapAiAsync(companyId, leftovers, chart, ct);

        var results = new List<ImportAccountMatchResult>(local.Count);
        foreach (var (q, r) in local)
        {
            var candidates = r.Candidates.Select(c => new ImportMatchCandidateDto(c.AccountId, $"{c.Code} {c.Name}", c.Score, c.Kind)).ToList();
            if (!r.AutoApplied && ai.TryGetValue(q.Key, out var answer))
            {
                // ด่าน anti-hallucination: รหัสต้องเป็นบัญชีลงยอดได้ในผังของบริษัทนี้ + มั่นใจพอ
                var hit = answer.AccountCode == null ? null
                    : chart.Accounts.FirstOrDefault(a => a.IsPostable
                        && ImportAccountCodeResolver.NormalizeCode(a.Code) == ImportAccountCodeResolver.NormalizeCode(answer.AccountCode));
                if (hit != null && answer.Confidence >= AiMinConfidence)
                {
                    candidates.Insert(0, new ImportMatchCandidateDto(hit.AccountId, $"{hit.Code} {hit.Name}", answer.Confidence, "Ai"));
                    results.Add(new ImportAccountMatchResult(q.Key, hit.AccountId, hit.Code, hit.Name, "Ai",
                        Math.Round(answer.Confidence, 4, MidpointRounding.AwayFromZero), answer.UsedAi, answer.FeedbackId,
                        candidates.GroupBy(c => c.EntityId).Select(g => g.First()).ToList()));
                    continue;
                }
                // คำตอบไม่ผ่านด่าน — คงผล local แต่เก็บ FeedbackId ไว้ปิดลูปตอนผู้ใช้เลือก (สอนนักเรียนจากคำตอบที่ผู้ใช้ยืนยัน)
                results.Add(new ImportAccountMatchResult(q.Key, null, null, null, r.Kind, r.Score, answer.UsedAi, answer.FeedbackId, candidates));
                continue;
            }
            results.Add(new ImportAccountMatchResult(q.Key, r.AccountId, r.AccountCode, r.AccountName, r.Kind, r.Score, false, null, candidates));
        }
        return results;
    }

    /// <summary>คำตอบ AI ต่อแถว (ยังไม่ผ่านด่าน — ผู้เรียกตรวจกับผังเอง)</summary>
    private sealed record AccountMapAiAnswer(string? AccountCode, decimal Confidence, bool UsedAi, Guid? FeedbackId);

    /// <summary>
    /// <b>ช่องเดียวที่ถาม AI</b> ของการจับคู่บัญชี (<c>AiFeatureKey.ImportAccountMap</c>) — ทีม A เพิ่ม <c>IImportAiAugmenter.SuggestAccountMapAsync</c>
    /// (orchestrator · prompt · student <c>GenericFeedbackDistillationModel</c>) ขนานกับทีมนี้ · ในฐานของทีม D ยังไม่มีเมธอดนั้น ⇒ คืน "ไม่มีคำแนะนำ"
    /// (ผลเท่ากับ kill-switch — ทุกชั้น local ยังทำงานครบ) · <b>ต่อสายหนึ่งบรรทัดเมื่อรวมทีม A</b>: แทน <c>return Empty</c> ด้วยการเรียก
    /// <c>_importAi.SuggestAccountMapAsync(companyId, leftovers, chart, ct)</c> แล้วแปลงผลเป็น <see cref="AccountMapAiAnswer"/> ต่อ <c>Key</c> ·
    /// ล้ม/timeout ⇒ คืนว่าง (กฎเหล็ก #1 ข้อ 4 — ไม่ล้มดังถึงผู้ใช้ เพราะชั้น local ตอบแล้ว)
    /// </summary>
    private Task<IReadOnlyDictionary<string, AccountMapAiAnswer>> AskAccountMapAiAsync(Guid companyId,
        IReadOnlyList<(ImportAccountQuery Query, ImportAccountResolution Local)> leftovers, ImportAccountChart chart, CancellationToken ct)
    {
        IReadOnlyDictionary<string, AccountMapAiAnswer> empty = new Dictionary<string, AccountMapAiAnswer>();
        if (_importAi == null || leftovers.Count == 0 || chart.Accounts.Count == 0) return Task.FromResult(empty);
        _logger?.LogDebug("ImportAccountMap: {Count} แถวรอ AI (ยังไม่ต่อสาย SuggestAccountMapAsync) บริษัท {Company}", leftovers.Count, companyId);
        return Task.FromResult(empty);
    }
}
