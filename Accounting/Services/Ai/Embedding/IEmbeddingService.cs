namespace Accounting.Services.Ai.Embedding;

/// <summary>
/// Dense vector embedding for short Thai text — vendor names, line
/// descriptions, document subjects. Used by VendorCanon "fuzzy match
/// even when the OCR string doesn't lexically equal a known vendor
/// name" and by document-similarity dedup ("this Expense looks like
/// the one you posted 3 days ago").
///
/// Two implementations planned:
///   • HashingEmbeddingService (default) — feature-hashing trick on
///     character n-grams. No model, no warm-up, ~30µs per call. Lower
///     quality than transformer-based but adequate as a baseline and
///     for the in-loop "does this scan resemble an existing scan" check.
///   • OnnxSentenceEmbeddingService (planned, swappable) — wraps
///     paraphrase-multilingual-MiniLM-L12-v2 or multilingual-e5-small
///     via Microsoft.ML.OnnxRuntime. ~10ms per call, much higher quality
///     for Thai. Loaded once at startup, model file lives under
///     /models. Drop in by re-registering the DI binding.
///
/// Cosine similarity (1 - cosineDistance) is the canonical metric for
/// comparing two embeddings; both implementations produce L2-normalised
/// vectors so a dot-product equals cosine.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Embedding dimension (e.g. 384 for MiniLM, 256 for the
    /// hashing baseline). Stable per implementation — callers can cache
    /// this once.</summary>
    int Dimensions { get; }

    /// <summary>Embed one string. Output is L2-normalised so dot-product
    /// = cosine. Empty input returns the zero vector.</summary>
    float[] Embed(string text);

    /// <summary>Embed many — implementations may batch internally
    /// (ONNX gets a 10× speedup on the model's batch axis).</summary>
    float[][] EmbedBatch(IReadOnlyList<string> texts);

    /// <summary>Cosine similarity in [-1, 1]. Both vectors expected
    /// L2-normalised (the implementations enforce this).</summary>
    static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }
}
