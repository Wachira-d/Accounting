using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Accounting.Services.Ai.Embedding;

/// <summary>
/// Sentence-embedding service backed by a sentence-transformers MiniLM
/// ONNX model. Quality jump over HashingEmbeddingService is significant
/// for Thai: semantic match between "ค่าน้ำมัน" and "เติมน้ำมันรถ"
/// works because the model was trained on multilingual paraphrase pairs.
///
/// Pipeline:
///   text → WordPiece tokenize → InferenceSession.Run → mean-pool
///   over attention mask → L2 normalise.
///
/// Files (loaded once at startup):
///   • Accounting/models/sentence-encoder.onnx — paraphrase-multilingual-
///     MiniLM-L12-v2 exported via optimum-cli, output = last_hidden_state.
///   • Accounting/models/vocab.txt — matching WordPiece vocab.
/// Both tracked via Git LFS. Missing-or-pointer-stub → throws at DI
/// resolution and the bootstrap code falls back to HashingEmbeddingService.
///
/// Concurrency: one InferenceSession instance shared across all calls.
/// ONNX Runtime's session.Run IS thread-safe for inference; we don't
/// need per-call locking. The session is disposed only on app shutdown.
/// </summary>
public sealed class OnnxSentenceEmbeddingService : IEmbeddingService, IDisposable
{
    public int Dimensions { get; }

    private readonly InferenceSession _session;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly string _inputIdsName;
    private readonly string _attentionMaskName;
    private readonly string? _tokenTypeIdsName;
    private readonly string _outputName;

    public OnnxSentenceEmbeddingService(string modelPath, string vocabPath, int dimensions = 384)
    {
        var tok = WordPieceTokenizer.TryLoad(vocabPath)
            ?? throw new FileNotFoundException(
                $"WordPiece vocab not loadable at {vocabPath}. " +
                "Run `git lfs pull` and ensure the file is >1 KB (not a pointer stub).");
        var info = new FileInfo(modelPath);
        if (!info.Exists || info.Length < 1_000_000)
            throw new FileNotFoundException(
                $"ONNX model not loadable at {modelPath} (size={info.Length}). " +
                "Run `git lfs pull`.");

        _tokenizer = tok;
        Dimensions = dimensions;

        // SessionOptions: graph-opt level 99 = ORT_ENABLE_ALL. CPU
        // execution provider is the default; on machines with multiple
        // cores, intra-op threads = core count gives us batch parallelism
        // for free without configuration.
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Min(4, Environment.ProcessorCount),
        };
        _session = new InferenceSession(modelPath, opts);

        // Resolve input/output names dynamically — different sentence-
        // transformer exports name them slightly differently
        // ("input_ids" vs "input"; "last_hidden_state" vs "output_0").
        var inputs = _session.InputMetadata.Keys.ToList();
        _inputIdsName = inputs.FirstOrDefault(n => n.Contains("input_ids", StringComparison.OrdinalIgnoreCase))
            ?? inputs[0];
        _attentionMaskName = inputs.FirstOrDefault(n => n.Contains("attention_mask", StringComparison.OrdinalIgnoreCase))
            ?? (inputs.Count > 1 ? inputs[1] : _inputIdsName);
        _tokenTypeIdsName = inputs.FirstOrDefault(n => n.Contains("token_type_ids", StringComparison.OrdinalIgnoreCase));
        _outputName = _session.OutputMetadata.Keys.First();
    }

    public float[] Embed(string text)
    {
        var vec = new float[Dimensions];
        if (string.IsNullOrWhiteSpace(text)) return vec;
        var encoded = _tokenizer.Encode(text);
        RunAndPool(encoded, vec);
        return vec;
    }

    public float[][] EmbedBatch(IReadOnlyList<string> texts)
    {
        var result = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++) result[i] = Embed(texts[i]);
        return result;
    }

    private void RunAndPool(WordPieceTokenizer.Encoded enc, float[] outVec)
    {
        var seqLen = enc.InputIds.Length;
        var idsTensor = new DenseTensor<long>(enc.InputIds, new[] { 1, seqLen });
        var maskTensor = new DenseTensor<long>(enc.AttentionMask, new[] { 1, seqLen });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, idsTensor),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName, maskTensor),
        };
        if (_tokenTypeIdsName != null)
        {
            var typesTensor = new DenseTensor<long>(enc.TokenTypeIds, new[] { 1, seqLen });
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIdsName, typesTensor));
        }

        using var results = _session.Run(inputs);
        var hidden = results.First(r => r.Name == _outputName).AsTensor<float>();
        // hidden shape: [1, seqLen, dim]. Mean-pool over the seq axis
        // ignoring padded positions (attention_mask == 0).
        var dim = hidden.Dimensions[2];
        if (dim != Dimensions)
            throw new InvalidOperationException(
                $"Embedding dim mismatch: model={dim} configured={Dimensions}");

        long maskSum = 0;
        for (int t = 0; t < seqLen; t++)
        {
            if (enc.AttentionMask[t] == 0) continue;
            maskSum++;
            for (int d = 0; d < dim; d++) outVec[d] += hidden[0, t, d];
        }
        if (maskSum == 0) return;
        var inv = 1f / maskSum;
        for (int d = 0; d < dim; d++) outVec[d] *= inv;

        // L2 normalise so dot product == cosine.
        double sumSq = 0;
        for (int d = 0; d < dim; d++) sumSq += outVec[d] * outVec[d];
        if (sumSq <= 0) return;
        var norm = (float)Math.Sqrt(sumSq);
        for (int d = 0; d < dim; d++) outVec[d] /= norm;
    }

    public void Dispose() => _session.Dispose();
}
