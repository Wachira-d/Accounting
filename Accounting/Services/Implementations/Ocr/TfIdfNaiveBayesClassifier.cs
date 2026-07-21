using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Multinomial Naive Bayes classifier with TF-IDF feature weighting,
/// trained incrementally from OcrCategoryMapping rows. Predicts the most
/// likely account code (and confidence) for a (vendor, description) pair.
///
/// Why NB + TF-IDF on top of the keyword/learner stack:
///   • The static ExpenseCategoryResolver uses a hand-curated keyword list
///     — it can miss vocabulary the company actually uses.
///   • The ExpenseCategoryLearner does exact + token-overlap matching but
///     all tokens are weighted equally — "ค่า" counts as much as "เบนซิน95",
///     which dilutes signal.
///   • TF-IDF weighting boosts discriminative tokens; NB combines them
///     probabilistically rather than by simple overlap count.
///   • The math is light enough to run per-scan against in-memory data
///     (the training corpus is the same OcrCategoryMapping rows we already
///     have, so no new table needed).
///
/// Algorithm:
///   • TF(t,c)  = count(t in c's descriptions)
///   • IDF(t)   = log( |classes| / |classes containing t| )
///   • W(t,c)   = TF(t,c) × IDF(t)
///   • P(c|d)   ∝ P(c) × Π exp(W(t,c) / Σ W) with Laplace smoothing
///
/// In practice we score classes with a log-sum and return the top class
/// when its margin over the runner-up exceeds 1 nat (i.e. ~2.7× more
/// likely). Below that margin we return null so a less-uncertain signal
/// upstream wins.
/// </summary>
public class TfIdfNaiveBayesClassifier
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<TfIdfNaiveBayesClassifier> _logger;

    public TfIdfNaiveBayesClassifier(AccountingDbContext db, ILogger<TfIdfNaiveBayesClassifier> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record Prediction(string AccountCode, string? AccountName, decimal Confidence, string Reason);

    public async Task<Prediction?> PredictAsync(Guid companyId, string? vendorTaxId, string? vendorName, string? description)
    {
        var docTokens = Tokenize(description ?? "")
            .Concat(Tokenize(vendorName ?? ""))
            .Distinct().ToList();
        if (docTokens.Count == 0) return null;

        // Pull all mappings for this tenant (bounded — typically <10k rows
        // even on large companies; the table is per-vendor not per-line).
        var rows = await _db.OcrCategoryMappings.AsNoTracking()
            .Where(m => m.CompanyId == companyId && !m.IsDeleted)
            .Select(m => new { m.VendorKey, m.DescriptionKeyword, m.AccountCode, m.AccountName, m.TimesUsed })
            .ToListAsync();
        if (rows.Count == 0) return null;

        // Build per-class (account) token frequencies + class priors
        var classes = rows.GroupBy(r => r.AccountCode);
        var classData = new Dictionary<string, ClassStats>();
        foreach (var g in classes)
        {
            var freq = new Dictionary<string, int>();
            int totalTokens = 0;
            int totalUsed = 0;
            string? name = null;
            foreach (var r in g)
            {
                name ??= r.AccountName;
                totalUsed += r.TimesUsed;
                foreach (var t in Tokenize(r.DescriptionKeyword))
                {
                    freq[t] = freq.GetValueOrDefault(t) + r.TimesUsed;
                    totalTokens += r.TimesUsed;
                }
            }
            classData[g.Key] = new ClassStats(name, freq, totalTokens, totalUsed);
        }
        if (classData.Count == 0) return null;

        // IDF: classes-document frequency in this corpus
        int numClasses = classData.Count;
        var idf = new Dictionary<string, double>();
        foreach (var token in classData.Values.SelectMany(c => c.TokenFreq.Keys).Distinct())
        {
            int classesContaining = classData.Values.Count(c => c.TokenFreq.ContainsKey(token));
            idf[token] = Math.Log((double)numClasses / Math.Max(1, classesContaining));
        }

        // Score each class: log P(c) + Σ log P(t|c) weighted by IDF
        int corpusTotalUsed = classData.Values.Sum(c => c.TotalUsed);
        const double smoothing = 1.0;
        var ranked = new List<(string Code, string? Name, double LogScore)>();
        foreach (var (code, stats) in classData)
        {
            double prior = Math.Log((double)stats.TotalUsed / corpusTotalUsed);
            double logLikelihood = 0;
            foreach (var t in docTokens)
            {
                if (!idf.TryGetValue(t, out var idfVal)) continue;
                var tokenFreq = stats.TokenFreq.GetValueOrDefault(t, 0);
                // Laplace-smoothed log-probability, weighted by IDF
                var p = (tokenFreq + smoothing) / (stats.TotalTokens + smoothing * idf.Count);
                logLikelihood += idfVal * Math.Log(p);
            }
            ranked.Add((code, stats.Name, prior + logLikelihood));
        }

        var sorted = ranked.OrderByDescending(x => x.LogScore).ToList();
        if (sorted.Count == 0) return null;
        var top = sorted[0];
        var runnerUp = sorted.Count > 1 ? sorted[1].LogScore : double.NegativeInfinity;
        var margin = top.LogScore - runnerUp;

        // Margin < 1 nat (~ e:1 odds) means the call is too close — don't
        // commit. This is the classic "abstain when unsure" pattern that
        // lets the rule-based stack handle ambiguous cases.
        if (margin < 1.0) return null;

        // Convert margin to a 0–1 confidence via squashed logistic:
        //   margin =  1 → ~0.73
        //   margin =  3 → ~0.95
        //   margin = 10 → ~0.99
        var conf = (decimal)(1.0 / (1.0 + Math.Exp(-margin / 2.0)));
        var reason = $"NB+TFIDF margin={margin:F2}, top vs runner-up";
        return new Prediction(top.Code, top.Name, conf, reason);
    }

    private record ClassStats(string? Name, Dictionary<string, int> TokenFreq, int TotalTokens, int TotalUsed);

    private static IEnumerable<string> Tokenize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        var raw = s.ToLowerInvariant()
            .Replace(",", " ").Replace(".", " ").Replace("-", " ")
            .Replace("(", " ").Replace(")", " ").Replace("/", " ")
            .Replace(":", " ").Replace(";", " ");
        foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 2) continue;
            if (part.All(char.IsDigit)) continue;
            yield return part;
        }
    }
}
