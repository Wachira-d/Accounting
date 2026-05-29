using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Enums;
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
/// </summary>
public class GlAccountDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.GlAccountSuggestion;
    public string Version { get; private set; } = "v0";
    public bool IsReady => _entries.Count > 0;

    private readonly IServiceProvider _services;
    private readonly ILogger<GlAccountDistillationModel> _logger;

    // (CompanyId, vendorKey, descriptionKeyword) → ranked candidates.
    private readonly Dictionary<(Guid, string, string), List<Score>> _entries = new();
    private readonly object _lock = new();

    public GlAccountDistillationModel(IServiceProvider services,
        ILogger<GlAccountDistillationModel> logger)
    { _services = services; _logger = logger; }

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
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.UserChosenAnswer)) continue;
            var (vendorKey, keyword) = ExtractKey(r.PromptJson);
            if (string.IsNullOrEmpty(vendorKey) || string.IsNullOrEmpty(keyword)) continue;
            var k = (vendorKey, keyword);
            if (!counts.TryGetValue(k, out var perAccount))
            {
                perAccount = new Dictionary<string, (int, int)>();
                counts[k] = perAccount;
            }
            var slot = perAccount.GetValueOrDefault(r.UserChosenAnswer);
            var isConfirm = r.UserAcceptedAi == true
                || (r.AiPrimaryAnswer != null && r.AiPrimaryAnswer == r.UserChosenAnswer);
            perAccount[r.UserChosenAnswer] = isConfirm
                ? (slot.Item1 + 1, slot.Item2) : (slot.Item1, slot.Item2 + 1);
        }

        lock (_lock)
        {
            var stale = _entries.Keys.Where(k => k.Item1 == companyId).ToList();
            foreach (var k in stale) _entries.Remove(k);
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
        }
        Version = "v" + DateTime.UtcNow.ToString("yyyyMMddHHmm");
        _logger.LogInformation(
            "GlAccountDistillationModel reloaded for company {Cid}: {Keys} keys from {Rows} rows",
            companyId, counts.Count, rows.Count);
    }

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        var (vendorKey, keyword) = ExtractKey(inputJson);
        if (string.IsNullOrEmpty(vendorKey) || string.IsNullOrEmpty(keyword))
            return Task.FromResult<LocalPrediction?>(null);
        List<Score>? candidates;
        lock (_lock) candidates = _entries.GetValueOrDefault((companyId, vendorKey, keyword));
        if (candidates == null || candidates.Count == 0)
            return Task.FromResult<LocalPrediction?>(null);
        var top = candidates[0];
        var alts = candidates.Skip(1).Take(3).Select(c => c.AccountCode).ToList();
        return Task.FromResult<LocalPrediction?>(new LocalPrediction(
            top.AccountCode, top.WilsonScore, alts,
            top.Confirmed + top.Overridden, Version));
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
