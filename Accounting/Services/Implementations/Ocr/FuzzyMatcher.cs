namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Pure-static Thai-aware fuzzy string matching utilities. Used to:
///   • Find an existing Contact whose name "almost matches" an OCR'd
///     vendor name (after stripping common Thai entity suffixes), so we
///     don't create a duplicate Contact each time.
///   • Suggest contact merges in the admin UI.
///
/// Strategy combines:
///   1. Normalization — lowercase, strip whitespace/punctuation, drop
///      Thai entity suffixes (บริษัท / จำกัด / มหาชน / หจก / co., ltd.)
///   2. Substring containment after normalization (cheap, catches most
///      "บริษัท ABC จำกัด" vs "ABC" cases)
///   3. Damerau-Levenshtein edit distance on the normalized form, with
///      a similarity ratio = 1 - distance / max(len). This catches OCR
///      typos like "บริษัท เอบีซี" vs "บ.เอบีซี".
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>0.0 (no match) to 1.0 (exact). 0.85+ is a reliable
    /// duplicate; 0.7+ is "worth asking the user".</summary>
    public static double Similarity(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return 0.0;
        if (na == nb) return 1.0;

        // Cheap substring boost — when one contains the other entirely,
        // we already know they share core tokens.
        var subBoost = 0.0;
        if (na.Contains(nb) || nb.Contains(na))
            subBoost = 0.25;

        var distance = LevenshteinDistance(na, nb);
        var max = Math.Max(na.Length, nb.Length);
        var ratio = 1.0 - (double)distance / max;
        return Math.Min(1.0, ratio + subBoost);
    }

    /// <summary>Strip noise so comparison focuses on the discriminative
    /// part of the name. "บริษัท เอบีซี เซอร์วิส จำกัด (มหาชน)" → "เอบีซีเซอร์วิส"</summary>
    public static string Normalize(string s)
    {
        var lowered = s.ToLowerInvariant();
        // Remove Thai entity prefix/suffix words
        string[] noise = {
            "บริษัท", "ห้างหุ้นส่วนจำกัด", "ห้างหุ้นส่วน", "หจก.", "หจก",
            "จำกัด", "มหาชน", "ร้าน", "สำนักงาน",
            "co.,ltd.", "co., ltd.", "co.ltd.", "co. ltd.", "co ltd",
            "ltd.", "limited", "company", "corp", "corporation", "inc."
        };
        foreach (var w in noise) lowered = lowered.Replace(w, " ");
        // Drop punctuation + whitespace
        return new string(lowered.Where(c => !char.IsWhiteSpace(c)
            && c != '.' && c != ',' && c != '(' && c != ')'
            && c != '-' && c != '_' && c != '/' && c != '\\').ToArray());
    }

    /// <summary>Standard Levenshtein O(m×n). Adequate for ≤200-char names;
    /// we never compare two strings longer than that.</summary>
    public static int LevenshteinDistance(string a, string b)
    {
        if (a == b) return 0;
        if (string.IsNullOrEmpty(a)) return b.Length;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var n = a.Length;
        var m = b.Length;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }
}
