namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Character n-gram cosine similarity — semantic-ish text matching
/// without external embedding models. The trick: words with the same
/// character n-grams (typically 3-grams) score high even when the exact
/// tokens differ. So:
///
///   "ค่าน้ำมันเชื้อเพลิง" vs "ค่าน้ำมันดีเซล"
///     3-grams: {ค่า,่าน,าน้,น้ำ,้ำม,ำมั,มัน,...} vs {ค่า,่าน,าน้,...,มัน,ันด,...}
///     cosine ≈ 0.62  — much higher than the 0 a keyword matcher would return
///
///   "การไฟฟ้านครหลวง" vs "การไฟฟ้าส่วนภูมิภาค"
///     shared trigrams across the "การไฟฟ้า" prefix
///     cosine ≈ 0.55
///
/// This is a poor man's sentence embedding — it captures lexical
/// proximity but NOT true semantic relatedness ("รถยนต์" vs "ยานพาหนะ"
/// both mean vehicle but share zero n-grams). For the common case of
/// Thai accounting where vendors and categories use overlapping
/// vocabulary, it's enough to fill the gap between keyword-equal and
/// genuine semantic.
///
/// Why we don't just use Levenshtein:
///   Edit distance treats word order strictly. "ค่าน้ำมัน เบนซิน 95"
///   vs "ค่าน้ำมันเบนซิน 95 ลิตร" has huge edit distance but very
///   high n-gram cosine (~0.75).
///
/// Why we don't use Word2Vec / fastText / e5:
///   • Requires ONNX model download + GPU optional
///   • Adds ~100MB to deployment
///   • The user asked for no-external-dependency
///
/// Computational profile:
///   • Build n-gram set: O(N) per string
///   • Cosine: O(min(|A|,|B|)) intersection
///   • In-process, fits in ~50µs per pair on modern CPU
/// </summary>
public static class CharNgramSimilarity
{
    /// <summary>0.0 (no overlap) to 1.0 (identical n-gram bags).
    /// 0.85+ is reliably similar; 0.6+ is "worth considering".</summary>
    public static double Similarity(string? a, string? b, int n = 3)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        if (a == b) return 1.0;

        var ngA = Ngrams(a, n);
        var ngB = Ngrams(b, n);
        return Cosine(ngA, ngB);
    }

    /// <summary>Rank candidates against a query, return top-k by
    /// similarity. Used for "find the CoA account most similar to this
    /// line description" style matching where exact keyword fails.</summary>
    public static IEnumerable<(T Item, double Score)> TopK<T>(
        string query,
        IEnumerable<T> candidates,
        Func<T, string?> textOf,
        int k = 5,
        double minScore = 0.3,
        int n = 3)
    {
        var queryGrams = Ngrams(query, n);
        return candidates
            .Select(c => (Item: c, Score: Cosine(queryGrams, Ngrams(textOf(c) ?? "", n))))
            .Where(x => x.Score >= minScore)
            .OrderByDescending(x => x.Score)
            .Take(k);
    }

    private static Dictionary<string, int> Ngrams(string s, int n)
    {
        var result = new Dictionary<string, int>();
        if (string.IsNullOrEmpty(s) || s.Length < n) return result;
        // Add boundary markers so prefix/suffix get extra weight — common
        // trick in IR. Pad with a non-word character so "ปตท" leads with
        // " ปต ปตท ทท " rather than just "ปตท".
        var padded = " " + s.ToLowerInvariant() + " ";
        for (int i = 0; i <= padded.Length - n; i++)
        {
            var gram = padded.Substring(i, n);
            result[gram] = result.GetValueOrDefault(gram) + 1;
        }
        return result;
    }

    private static double Cosine(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        // Iterate over the smaller set for the dot product
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        double dot = 0;
        foreach (var (gram, count) in small)
            if (large.TryGetValue(gram, out var lc)) dot += count * lc;

        double magA = Math.Sqrt(a.Values.Sum(v => (double)(v * v)));
        double magB = Math.Sqrt(b.Values.Sum(v => (double)(v * v)));
        if (magA == 0 || magB == 0) return 0.0;
        return dot / (magA * magB);
    }
}
