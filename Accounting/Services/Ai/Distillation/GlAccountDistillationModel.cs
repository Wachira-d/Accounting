using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Ai.Embedding;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// Local distillation model for GL account suggestion (the user-called-
/// out "เลือกผังบัญชีตอนสร้างใบสำคัญจ่าย" case). Mines confirmed
/// GlAccountSuggestion + PaymentVoucherAccountingSuggestion feedback;
/// learns (vendor key + line-description keyword) → account code with
/// Wilson-scored confidence.
///
/// Two feature keys share this model because they ask for the same
/// thing — which account to debit on a Thai-context business expense.
///
/// Embedding fallback: line descriptions vary enormously ("ค่าน้ำมัน
/// ดีเซล" vs "ค่าน้ำมัน-ดีเซล รถบรรทุก") so exact-keyword lookup misses
/// often. The keyword embedding index lets a near-neighbour query
/// match a confirmed pattern and recover at a discounted confidence.
/// Vendor part is matched exactly — we don't blur which vendor it is.
/// </summary>
public class GlAccountDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.GlAccountSuggestion;
    public string Version { get; private set; } = "v0";
    public bool IsReady => _entries.Count > 0 || _companyKeyword.Count > 0 || _industryKeyword.Count > 0;

    private readonly IServiceProvider _services;
    private readonly IEmbeddingService _embedding;
    private readonly ILogger<GlAccountDistillationModel> _logger;

    // (CompanyId, vendorKey, descriptionKeyword) → ranked candidates.
    private readonly Dictionary<(Guid, string, string), List<Score>> _entries = new();

    // Per (CompanyId, vendorKey) → list of (keyword, vector) for the
    // embedding fallback. Vendor stays exact — we only fuzz on keyword.
    private readonly Dictionary<(Guid CompanyId, string VendorKey), List<(string Keyword, float[] Vector)>> _keywordIndex = new();

    // ===== Company-context layers (ใช้ข้อมูลบริษัทร่วมตัดสินใจ) =====
    // (CompanyId, keyword) → scores — บริษัทเดียวกัน keyword เดียว ไม่ว่า vendor
    // ไหน (ซื้อของเดิมจากหลายร้าน → ลงผังเดียวกัน). Fallback ชั้นที่ 3.
    private readonly Dictionary<(Guid CompanyId, string Keyword), List<Score>> _companyKeyword = new();
    // (IndustryType, keyword) → scores — baseline ข้ามบริษัทในอุตสาหกรรมเดียวกัน.
    // ใช้ cold-start: บริษัทใหม่ (ยังไม่มี feedback ตัวเอง) ตอบได้จากแพทเทิร์น
    // ของร้านอาหาร/โรงแรม/ฯลฯ รายอื่น. เก็บแค่ keyword→accountCode (ไม่มี vendor)
    // → ไม่รั่วข้อมูล vendor ข้ามบริษัท. account code = ผังมาตรฐาน ไม่ sensitive.
    private readonly Dictionary<(string Industry, string Keyword), List<Score>> _industryKeyword = new();
    // companyId → industry (cache สำหรับ map ตอน predict)
    private readonly Dictionary<Guid, string> _companyIndustry = new();
    private static DateTime _industryBuiltAtUtc = DateTime.MinValue;
    private static readonly object _industryLock = new();
    /// <summary>industry baseline rebuild ทุก 60 นาที (cross-company query หนัก
    /// — ไม่ต้อง rebuild ทุก company load).</summary>
    private static readonly TimeSpan IndustryRefreshInterval = TimeSpan.FromMinutes(60);
    /// <summary>confidence discount เมื่อ fallback ไปชั้น company-any-vendor /
    /// industry — ต่ำกว่า own-vendor เพราะ generic กว่า.</summary>
    private const decimal CompanyKeywordDiscount = 0.85m;
    private const decimal IndustryDiscount = 0.55m;

    private readonly object _lock = new();

    /// <summary>Cosine threshold for keyword fuzzy match. Line descriptions
    /// are noisier than vendor names so we set this lower (0.70) than
    /// the vendor-canon threshold to catch more variants.</summary>
    private const float FuzzyMatchThreshold = 0.70f;

    public GlAccountDistillationModel(IServiceProvider services,
        IEmbeddingService embedding,
        ILogger<GlAccountDistillationModel> logger)
    { _services = services; _embedding = embedding; _logger = logger; }

    public async Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        var rows = await db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.CompanyId == companyId
                        && (f.FeatureKey == nameof(AiFeatureKey.GlAccountSuggestion)
                            || f.FeatureKey == nameof(AiFeatureKey.PaymentVoucherAccountingSuggestion))
                        && f.UserChosenAt != null
                        && !f.IsDeleted)
            .Select(f => new
            {
                f.PromptJson, f.UserChosenAnswer, f.UserAcceptedAi, f.AiPrimaryAnswer,
            })
            .ToListAsync(ct);

        var counts = new Dictionary<(string, string), Dictionary<string, (int Confirmed, int Overridden)>>();
        // company-level keyword (ไม่แยก vendor) → account counts
        var ckCounts = new Dictionary<string, Dictionary<string, (int Confirmed, int Overridden)>>();
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.UserChosenAnswer)) continue;
            var (vendorKey, keyword) = ExtractKey(r.PromptJson);
            if (string.IsNullOrEmpty(vendorKey) || string.IsNullOrEmpty(keyword)) continue;
            var isConfirm = r.UserAcceptedAi == true
                || (r.AiPrimaryAnswer != null && r.AiPrimaryAnswer == r.UserChosenAnswer);

            var k = (vendorKey, keyword);
            if (!counts.TryGetValue(k, out var perAccount))
            {
                perAccount = new Dictionary<string, (int, int)>();
                counts[k] = perAccount;
            }
            var slot = perAccount.GetValueOrDefault(r.UserChosenAnswer);
            perAccount[r.UserChosenAnswer] = isConfirm
                ? (slot.Item1 + 1, slot.Item2) : (slot.Item1, slot.Item2 + 1);

            // company-any-vendor layer
            if (!ckCounts.TryGetValue(keyword, out var ckAccount))
            {
                ckAccount = new Dictionary<string, (int, int)>();
                ckCounts[keyword] = ckAccount;
            }
            var cslot = ckAccount.GetValueOrDefault(r.UserChosenAnswer);
            ckAccount[r.UserChosenAnswer] = isConfirm
                ? (cslot.Item1 + 1, cslot.Item2) : (cslot.Item1, cslot.Item2 + 1);
        }

        // Build per-vendor keyword embedding index. One Embed call per
        // (vendor, keyword) combination this company has confirmed.
        var keywordIndex = counts.Keys
            .GroupBy(k => k.Item1)
            .ToDictionary(
                g => g.Key,
                g => g.Select(k => (Keyword: k.Item2, Vector: _embedding.Embed(k.Item2))).ToList());

        lock (_lock)
        {
            var stale = _entries.Keys.Where(k => k.Item1 == companyId).ToList();
            foreach (var k in stale) _entries.Remove(k);
            var staleIdx = _keywordIndex.Keys.Where(k => k.CompanyId == companyId).ToList();
            foreach (var k in staleIdx) _keywordIndex.Remove(k);

            foreach (var ((vendorKey, keyword), candidates) in counts)
            {
                var scored = candidates.Select(kv => new Score(
                    AccountCode: kv.Key,
                    Confirmed: kv.Value.Confirmed,
                    Overridden: kv.Value.Overridden,
                    WilsonScore: Wilson(kv.Value.Confirmed, kv.Value.Confirmed + kv.Value.Overridden)))
                    .OrderByDescending(s => s.WilsonScore).ToList();
                _entries[(companyId, vendorKey, keyword)] = scored;
            }
            foreach (var (vendorKey, list) in keywordIndex)
                _keywordIndex[(companyId, vendorKey)] = list;

            // company-any-vendor layer (replace this company's)
            var staleCk = _companyKeyword.Keys.Where(k => k.CompanyId == companyId).ToList();
            foreach (var k in staleCk) _companyKeyword.Remove(k);
            foreach (var (keyword, accs) in ckCounts)
            {
                _companyKeyword[(companyId, keyword)] = accs.Select(kv => new Score(
                    kv.Key, kv.Value.Confirmed, kv.Value.Overridden,
                    Wilson(kv.Value.Confirmed, kv.Value.Confirmed + kv.Value.Overridden)))
                    .OrderByDescending(s => s.WilsonScore).ToList();
            }
        }

        // industry baseline (cross-company) — rebuild แบบ throttled
        await RefreshIndustryBaselineAsync(db, ct);

        Version = "v" + DateTime.UtcNow.ToString("yyyyMMddHHmm");
        _logger.LogInformation(
            "GlAccountDistillationModel reloaded for company {Cid}: {Keys} keys from {Rows} rows",
            companyId, counts.Count, rows.Count);
    }

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        var (vendorKey, keyword) = ExtractKey(inputJson);
        if (string.IsNullOrEmpty(keyword))
            return Task.FromResult<LocalPrediction?>(null);
        List<Score>? candidates = null;
        string matchedKeyword = keyword;
        decimal confidenceMultiplier = 1m;
        string tier = "";
        lock (_lock)
        {
            // ชั้น 1: own vendor + keyword (เจาะจงที่สุด)
            if (!string.IsNullOrEmpty(vendorKey))
            {
                candidates = _entries.GetValueOrDefault((companyId, vendorKey, keyword));
                // ชั้น 2: own vendor + fuzzy keyword
                if ((candidates == null || candidates.Count == 0))
                {
                    var index = _keywordIndex.GetValueOrDefault((companyId, vendorKey));
                    if (index != null && index.Count > 0)
                    {
                        var query = _embedding.Embed(keyword);
                        string? bestKw = null;
                        float bestSim = FuzzyMatchThreshold;
                        foreach (var (kw, vec) in index)
                        {
                            var sim = IEmbeddingService.Cosine(query, vec);
                            if (sim > bestSim) { bestSim = sim; bestKw = kw; }
                        }
                        if (bestKw != null)
                        {
                            candidates = _entries.GetValueOrDefault((companyId, vendorKey, bestKw));
                            matchedKeyword = bestKw; confidenceMultiplier = (decimal)bestSim;
                            tier = "+fuzzy";
                        }
                    }
                }
            }
            // ชั้น 3: own company, keyword ใด ๆ vendor (ใช้บริบทบริษัทเดียวกัน)
            if (candidates == null || candidates.Count == 0)
            {
                candidates = _companyKeyword.GetValueOrDefault((companyId, keyword));
                if (candidates is { Count: > 0 })
                {
                    confidenceMultiplier = CompanyKeywordDiscount;
                    tier = "+companyKw";
                }
            }
            // ชั้น 4: industry baseline (cross-company) — cold-start
            if (candidates == null || candidates.Count == 0)
            {
                var industry = _companyIndustry.GetValueOrDefault(companyId);
                if (!string.IsNullOrEmpty(industry))
                {
                    candidates = _industryKeyword.GetValueOrDefault((industry, keyword));
                    if (candidates is { Count: > 0 })
                    {
                        confidenceMultiplier = IndustryDiscount;
                        tier = "+industry:" + industry;
                    }
                }
            }
        }
        if (candidates == null || candidates.Count == 0)
            return Task.FromResult<LocalPrediction?>(null);
        var top = candidates[0];
        var alts = candidates.Skip(1).Take(3).Select(c => c.AccountCode).ToList();
        return Task.FromResult<LocalPrediction?>(new LocalPrediction(
            top.AccountCode,
            top.WilsonScore * confidenceMultiplier,
            alts,
            top.Confirmed + top.Overridden,
            Version + tier));
    }

    /// <summary>สร้าง industry baseline (cross-company keyword→account) +
    /// company→industry map. throttle 60 นาที — cross-company query หนัก.
    /// เก็บแค่ keyword→accountCode (ไม่มี vendor) → privacy-safe.</summary>
    private async Task RefreshIndustryBaselineAsync(AccountingDbContext db, CancellationToken ct)
    {
        lock (_industryLock)
        {
            if (DateTime.UtcNow - _industryBuiltAtUtc < IndustryRefreshInterval
                && _industryKeyword.Count > 0)
                return;
            _industryBuiltAtUtc = DateTime.UtcNow;   // claim ก่อนกัน rebuild ซ้อน
        }

        // company → industry
        var companies = await db.Companies.AsNoTracking()
            .Select(c => new { c.Id, c.IndustryType })
            .ToListAsync(ct);
        var compIndustry = companies.ToDictionary(c => c.Id, c => c.IndustryType.ToString());

        // confirmed feedback ทุกบริษัท (เฉพาะ keyword→account)
        var rows = await db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => (f.FeatureKey == nameof(AiFeatureKey.GlAccountSuggestion)
                         || f.FeatureKey == nameof(AiFeatureKey.PaymentVoucherAccountingSuggestion))
                        && f.UserChosenAt != null && !f.IsDeleted
                        && f.UserChosenAnswer != null)
            .Select(f => new { f.CompanyId, f.PromptJson, f.UserChosenAnswer, f.UserAcceptedAi, f.AiPrimaryAnswer })
            .ToListAsync(ct);

        var indCounts = new Dictionary<(string, string), Dictionary<string, (int, int)>>();
        foreach (var r in rows)
        {
            if (!compIndustry.TryGetValue(r.CompanyId, out var industry) || string.IsNullOrEmpty(industry)) continue;
            var (_, keyword) = ExtractKey(r.PromptJson);
            if (string.IsNullOrEmpty(keyword) || string.IsNullOrEmpty(r.UserChosenAnswer)) continue;
            var isConfirm = r.UserAcceptedAi == true
                || (r.AiPrimaryAnswer != null && r.AiPrimaryAnswer == r.UserChosenAnswer);
            var key = (industry, keyword);
            if (!indCounts.TryGetValue(key, out var perAcc))
            {
                perAcc = new Dictionary<string, (int, int)>();
                indCounts[key] = perAcc;
            }
            var slot = perAcc.GetValueOrDefault(r.UserChosenAnswer);
            perAcc[r.UserChosenAnswer] = isConfirm
                ? (slot.Item1 + 1, slot.Item2) : (slot.Item1, slot.Item2 + 1);
        }

        lock (_lock)
        {
            _companyIndustry.Clear();
            foreach (var (id, ind) in compIndustry) _companyIndustry[id] = ind;
            _industryKeyword.Clear();
            foreach (var ((industry, keyword), accs) in indCounts)
            {
                _industryKeyword[(industry, keyword)] = accs.Select(kv => new Score(
                    kv.Key, kv.Value.Item1, kv.Value.Item2,
                    Wilson(kv.Value.Item1, kv.Value.Item1 + kv.Value.Item2)))
                    .OrderByDescending(s => s.WilsonScore).ToList();
            }
        }
        _logger.LogInformation("GL industry baseline rebuilt: {Industries} industry-keyword keys from {Rows} rows",
            _industryKeyword.Count, rows.Count);
    }

    private static decimal Wilson(int successes, int n)
    {
        if (n == 0) return 0m;
        const double z = 1.96;
        var p = (double)successes / n;
        var denom = 1 + z * z / n;
        var center = p + z * z / (2 * n);
        var spread = z * Math.Sqrt((p * (1 - p) + z * z / (4 * n)) / n);
        return (decimal)Math.Max(0, (center - spread) / denom);
    }

    private static (string VendorKey, string Keyword) ExtractKey(string promptJson)
    {
        if (string.IsNullOrEmpty(promptJson)) return ("", "");
        try
        {
            using var doc = JsonDocument.Parse(promptJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("vendor", out var v)
                || !root.TryGetProperty("line", out var line))
                return ("", "");
            var vendorTaxId = v.TryGetProperty("tax_id", out var t) ? t.GetString() ?? "" : "";
            var vendorName = v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var description = line.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var digits = new string(vendorTaxId.Where(char.IsDigit).ToArray());
            var vendorKey = digits.Length == 13 ? digits
                : vendorName.Trim().ToLowerInvariant();
            var keyword = description.Trim().ToLowerInvariant();
            if (keyword.Length > 80) keyword = keyword[..80];
            return (vendorKey, keyword);
        }
        catch { return ("", ""); }
    }

    private sealed record Score(
        string AccountCode, int Confirmed, int Overridden, decimal WilsonScore);
}
