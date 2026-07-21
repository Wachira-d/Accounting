using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using static Accounting.Services.Implementations.Ocr.FieldPatternLibrary;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Multi-stage field extractor: candidate generation → validation → scoring → selection.
/// </summary>
public static class FieldExtractor
{
    public class ExtractionContext
    {
        public string FullText { get; set; } = "";
        public List<DocumentZoneAnalyzer.TextZone> Zones { get; set; } = new();
        public List<OcrLearnedPattern>? LearnedPatterns { get; set; }
        /// <summary>Values previously corrected away from for this vendor — should be DOWN-weighted.</summary>
        public HashSet<string> NegativeExamples { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? OurCompanyTaxId { get; set; }   // distinguishes seller from buyer
    }

    public class ExtractionResult
    {
        public FieldCandidate? Best { get; set; }
        public List<FieldCandidate> AllCandidates { get; set; } = new();
        public double Confidence { get; set; }
        public string? Reasoning { get; set; }
    }

    /// <summary>
    /// Extract a field with zone bias. Returns the best candidate plus all rejected
    /// candidates for transparency/debugging.
    /// </summary>
    public static ExtractionResult ExtractField(
        FieldType fieldType,
        ExtractionContext ctx,
        DocumentZoneAnalyzer.ZoneType? preferredZone = null,
        Func<FieldCandidate, bool>? customFilter = null)
    {
        var candidates = ExtractCandidates(ctx.FullText, fieldType);

        // Apply custom filter (e.g., exclude tax IDs that match our own)
        if (customFilter != null)
            candidates = candidates.Where(customFilter).ToList();

        // Negative examples (user previously corrected away from these)
        foreach (var c in candidates)
        {
            if (ctx.NegativeExamples.Contains(c.NormalizedValue))
            {
                c.Score *= 0.2;
                c.ScoreReasons.Add("negative-example");
            }
        }

        // Zone proximity scoring
        if (preferredZone.HasValue)
        {
            var pz = ctx.Zones.FirstOrDefault(z => z.Type == preferredZone.Value);
            if (pz != null)
            {
                foreach (var c in candidates)
                {
                    if (c.Position >= pz.Start && c.Position <= pz.End)
                    {
                        c.Score *= 1.5;
                        c.ScoreReasons.Add($"zone:{pz.Type}");
                    }
                }
            }
        }

        // Context keyword proximity scoring
        var keywords = GetContextKeywords(fieldType);
        foreach (var c in candidates)
        {
            int bestDist = int.MaxValue;
            string? bestKw = null;
            int searchStart = Math.Max(0, c.Position - 80);
            int searchLen = Math.Min(80, c.Position - searchStart);
            if (searchLen > 0)
            {
                var beforeText = ctx.FullText.Substring(searchStart, searchLen);
                foreach (var kw in keywords)
                {
                    int idx = beforeText.LastIndexOf(kw, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        int dist = beforeText.Length - idx;
                        if (dist < bestDist) { bestDist = dist; bestKw = kw; }
                    }
                }
            }
            if (bestKw != null)
            {
                // Closer keyword = higher boost
                double boost = 1.0 + (1.0 - Math.Min(1.0, bestDist / 80.0)) * 0.6;
                c.Score *= boost;
                c.ScoreReasons.Add($"kw:{bestKw}@{bestDist}");
            }
        }

        // Apply learned patterns (positive examples)
        if (ctx.LearnedPatterns != null)
        {
            foreach (var p in ctx.LearnedPatterns
                .Where(p => p.FieldName == fieldType.ToString() && !p.IsNegativeExample)
                .OrderByDescending(p => p.TimesConfirmed))
            {
                int kwPos = ctx.FullText.IndexOf(p.ContextKeyword, StringComparison.OrdinalIgnoreCase);
                if (kwPos < 0) continue;
                int searchEnd = Math.Min(ctx.FullText.Length, kwPos + p.SearchRadius);
                foreach (var c in candidates)
                {
                    if (c.Position >= kwPos && c.Position <= searchEnd)
                    {
                        c.Score *= 1.3 + Math.Min(0.5, p.TimesConfirmed * 0.05);
                        c.ScoreReasons.Add($"learned:{p.ContextKeyword}×{p.TimesConfirmed}");
                    }
                }
            }
        }

        var ordered = candidates.OrderByDescending(c => c.Score).ToList();
        var best = ordered.FirstOrDefault();

        // Confidence calculation
        double confidence = 0;
        if (best != null)
        {
            confidence = Math.Min(1.0, best.Score / 1.5);
            // Penalty if there are competing candidates with similar scores
            if (ordered.Count >= 2 && ordered[1].Score > best.Score * 0.7)
                confidence *= 0.85;
        }

        return new ExtractionResult
        {
            Best = best,
            AllCandidates = ordered,
            Confidence = confidence,
            Reasoning = best != null
                ? $"selected '{best.Value}' from {candidates.Count} candidates: {string.Join(", ", best.ScoreReasons)}"
                : $"no candidates found for {fieldType}"
        };
    }
}
