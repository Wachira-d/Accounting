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
                if (hit != null && answer.Confidence >= AiMinConfidence && (answer.UsedAi || answer.FromStudent))
                {
                    // ป้ายซื่อสัตย์: AI ตอบจริง = "Ai" (🤖) · นักเรียนตอบแทน = "Learned" (🧠 ระบบเรียนรู้แล้ว)
                    var kind = answer.UsedAi ? "Ai" : "Learned";
                    candidates.Insert(0, new ImportMatchCandidateDto(hit.AccountId, $"{hit.Code} {hit.Name}", answer.Confidence, kind));
                    results.Add(new ImportAccountMatchResult(q.Key, hit.AccountId, hit.Code, hit.Name, kind,
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

    /// <summary>คำตอบชั้นโมเดลต่อแถว (ยังไม่ผ่านด่านผัง — ผู้เรียกตรวจกับผังเอง) · <see cref="UsedAi"/> = AI ตอบจริง · <see cref="FromStudent"/> = นักเรียนตอบแทน</summary>
    private sealed record AccountMapAiAnswer(string? AccountCode, decimal Confidence, bool UsedAi, bool FromStudent, Guid? FeedbackId);

    /// <summary>แถวสูงสุดต่อรอบที่ถามชั้นโมเดล (ไฟล์งบทดลองใหญ่ — แถวที่เกินใช้ผล local + ผู้ใช้เลือก · งบ token ไม่บานตามขนาดไฟล์)</summary>
    public const int MaxModelRowsPerCall = 50;

    /// <summary>
    /// <b>ช่องเดียวที่ถามชั้นโมเดล</b> ของการจับคู่บัญชี (<c>AiFeatureKey.ImportAccountMap</c> · 58) — ผ่าน <c>IImportAiAugmenter.SuggestAccountMapAsync</c>
    /// (ทีม A: orchestrator student-first · ด่าน ∈ <c>allowedCodes</c> = รหัสบัญชีลงยอดได้ของบริษัท) · ส่งคำตอบ local อันดับหนึ่งเป็น hint ·
    /// คำตอบที่เป็นแค่ "local สะท้อนกลับ" (ไม่ใช่ AI/นักเรียน) ไม่นับ — ชั้น local ตัดสินไปแล้วว่าไม่ถึงเกณฑ์ · augmenter ล้ม/ปิด AI ⇒ คืน local ⇒
    /// ไม่มีคำแนะนำเพิ่ม (kill-switch: ทุกชั้น local ยังทำงานครบ) · ปิดลูปที่ <c>MigrationWizardService.ApplyMappingsAsync</c> (<c>RecordPickChoiceAsync</c>)
    /// </summary>
    private async Task<IReadOnlyDictionary<string, AccountMapAiAnswer>> AskAccountMapAiAsync(Guid companyId,
        IReadOnlyList<(ImportAccountQuery Query, ImportAccountResolution Local)> leftovers, ImportAccountChart chart, CancellationToken ct)
    {
        var result = new Dictionary<string, AccountMapAiAnswer>();
        var postable = chart.Accounts.Where(a => a.IsPostable).ToList();
        if (_importAi == null || leftovers.Count == 0 || postable.Count == 0) return result;
        var allowed = postable.Select(a => a.Code).ToList();
        foreach (var (q, local) in leftovers.Take(MaxModelRowsPerCall))
        {
            var cls = ImportAccountCodeResolver.ClassFromCode(q.Code);
            var fromLocal = local.Candidates.Select(c => postable.FirstOrDefault(a => a.AccountId == c.AccountId)).OfType<ImportChartAccount>();
            var candidates = fromLocal.Concat(postable.Where(a => cls == null || a.Type == cls)).Distinct().Take(40)
                .Select(a => new Accounting.Services.Ai.Prompts.ImportPrompts.AccountCandidate(a.Code, a.Name, a.Type.ToString())).ToList();
            var top = local.Candidates.FirstOrDefault();
            var pick = await _importAi.SuggestAccountMapAsync(companyId, null, q.Code, q.Name, q.Hint, candidates, allowed,
                top?.Code, top?.Score, ct);
            if (!pick.UsedAi && !pick.FromLocalModel && pick.FeedbackId == null) continue;   // แค่ local สะท้อนกลับ / AI ดับ
            result[q.Key] = new AccountMapAiAnswer(pick.Rejected ? null : pick.Answer, pick.Confidence, pick.UsedAi, pick.FromLocalModel,
                pick.FeedbackId);
        }
        _logger?.LogDebug("ImportAccountMap: ถามชั้นโมเดล {Asked}/{Total} แถว ได้คำตอบ {Answered} บริษัท {Company}",
            Math.Min(leftovers.Count, MaxModelRowsPerCall), leftovers.Count, result.Count, companyId);
        return result;
    }
}
