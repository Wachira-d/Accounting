using System.Text;

namespace Accounting.Services.Ai.Embedding;

/// <summary>
/// Feature-hashing embedding — no model, no warm-up, language-agnostic.
/// Character n-grams (3- and 4-gram, by Unicode codepoint so Thai +
/// English mix correctly) are hashed into a fixed-dimensional vector
/// with sign-flip from the hash. Then L2-normalised.
///
/// Quality is below a transformer but well above bag-of-words for
/// short strings — handles typos ("กรุงเทพ" vs "กรุงเทพมหานคร" both
/// contain "กรุง" and "งเทพ"). Vendor canon already uses this idea
/// implicitly via the tax-id-prefer fallback; promoting it here gives
/// the orchestrator a real similarity score it can route on.
///
/// Swap-in path: register OnnxSentenceEmbeddingService as
/// IEmbeddingService in DI to upgrade quality with zero call-site
/// changes. This implementation stays as the deterministic fallback.
/// </summary>
public class HashingEmbeddingService : IEmbeddingService
{
    public int Dimensions { get; }

    public HashingEmbeddingService(int dimensions = 256)
    {
        // Power-of-two recommended for cache locality; 256 is enough
        // for short-string discrimination without ballooning row size
        // when persisted alongside Documents.
        Dimensions = dimensions > 0 ? dimensions : 256;
    }

    public float[] Embed(string text)
    {
        var vec = new float[Dimensions];
        if (string.IsNullOrWhiteSpace(text)) return vec;

        var normalised = Normalize(text);
        AddNGrams(normalised, 3, vec);
        AddNGrams(normalised, 4, vec);

        // L2-normalise so dot product == cosine.
        double sumSq = 0;
        for (int i = 0; i < vec.Length; i++) sumSq += vec[i] * vec[i];
        if (sumSq <= 0) return vec;
        var norm = (float)Math.Sqrt(sumSq);
        for (int i = 0; i < vec.Length; i++) vec[i] /= norm;
        return vec;
    }

    public float[][] EmbedBatch(IReadOnlyList<string> texts)
    {
        var result = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++) result[i] = Embed(texts[i]);
        return result;
    }

    /// <summary>Lowercase, strip control chars, collapse whitespace.
    /// Thai characters pass through unchanged (no ToLowerInvariant
    /// effect on them) — preserves linguistic content while removing
    /// formatting noise from OCR output.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var prevSpace = true;
        foreach (var ch in text)
        {
            var c = char.ToLowerInvariant(ch);
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else if (char.IsPunctuation(c) || char.IsSymbol(c))
            {
                // Treat punctuation as soft separator — keeps OCR
                // glitches (extra dots / commas) from creating spurious
                // n-grams that bind across token boundaries.
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    private void AddNGrams(string text, int n, float[] vec)
    {
        if (text.Length < n) return;
        for (int i = 0; i <= text.Length - n; i++)
        {
            var gram = text.AsSpan(i, n);
            var (idx, sign) = HashGram(gram);
            vec[idx % Dimensions] += sign;
        }
    }

    /// <summary>FNV-1a 64-bit hash for speed + good distribution; lower
    /// bit decides the sign so collisions cancel rather than always
    /// reinforcing each other (the textbook sign-trick from Weinberger
    /// et al 2009, "Feature Hashing for Large Scale Multitask Learning").</summary>
    private static (int Bucket, float Sign) HashGram(ReadOnlySpan<char> gram)
    {
        unchecked
        {
            ulong h = 14695981039346656037UL;
            foreach (var ch in gram)
            {
                h ^= ch;
                h *= 1099511628211UL;
            }
            var bucket = (int)(h >> 1) & int.MaxValue;
            var sign = (h & 1) == 0 ? 1f : -1f;
            return (bucket, sign);
        }
    }
}
