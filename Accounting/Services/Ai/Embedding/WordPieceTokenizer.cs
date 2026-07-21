using System.Text;

namespace Accounting.Services.Ai.Embedding;

/// <summary>
/// BERT WordPiece tokenizer — minimum-viable implementation that reads
/// the standard HuggingFace-format `vocab.txt` (one token per line,
/// index = line number) and produces the token-id sequence + attention
/// mask the MiniLM ONNX model expects.
///
/// Why we own this instead of depending on Microsoft.ML.Tokenizers:
///   • That package has shipped only preview versions for the BERT path;
///     the API has changed three times in a year. A 100-LOC dependency-
///     free implementation is more stable.
///   • We only need encode (input → ids); decode is irrelevant for
///     embeddings. WordPiece encode is well-defined: greedy longest-
///     match-first against the vocabulary, "##" prefix on subword
///     pieces, [UNK] when nothing matches.
///   • Thai text uses a special-token path — BERT-multilingual vocab
///     covers most Thai syllables; rare ones fall to [UNK] which still
///     produces a usable embedding (rare-token information is in the
///     surrounding context).
///
/// Encoded sequence layout for MiniLM (sentence-transformers convention):
///   [CLS] tok1 tok2 ... tokN [SEP] [PAD]* — capped at maxLen=128.
/// </summary>
public sealed class WordPieceTokenizer
{
    public const int MaxSequenceLength = 128;
    public const string ClsToken = "[CLS]";
    public const string SepToken = "[SEP]";
    public const string PadToken = "[PAD]";
    public const string UnkToken = "[UNK]";

    private readonly Dictionary<string, long> _vocab;
    private readonly long _clsId, _sepId, _padId, _unkId;
    private readonly int _maxWordChars;

    public WordPieceTokenizer(IDictionary<string, long> vocab, int maxWordChars = 100)
    {
        _vocab = new Dictionary<string, long>(vocab);
        _clsId = _vocab.GetValueOrDefault(ClsToken, 101);
        _sepId = _vocab.GetValueOrDefault(SepToken, 102);
        _padId = _vocab.GetValueOrDefault(PadToken, 0);
        _unkId = _vocab.GetValueOrDefault(UnkToken, 100);
        _maxWordChars = maxWordChars;
    }

    /// <summary>Load a HuggingFace-format `vocab.txt` file from disk.
    /// One token per line; line index becomes the token id. Returns
    /// null if the file is missing or LFS-pointer-stub size (≤200 B).</summary>
    public static WordPieceTokenizer? TryLoad(string vocabPath)
    {
        if (!File.Exists(vocabPath)) return null;
        var info = new FileInfo(vocabPath);
        if (info.Length < 1024) return null;        // LFS pointer ≈ 134 B
        var lines = File.ReadAllLines(vocabPath);
        var vocab = new Dictionary<string, long>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length > 0) vocab[t] = i;
        }
        return new WordPieceTokenizer(vocab);
    }

    public sealed record Encoded(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds);

    /// <summary>Tokenise one string into MiniLM input tensors. Truncates
    /// at MaxSequenceLength - 2 to leave room for [CLS] and [SEP];
    /// pads with [PAD] up to MaxSequenceLength (attention_mask=0 on the
    /// padded slots so the model ignores them).</summary>
    public Encoded Encode(string text)
    {
        var ids = new List<long>(MaxSequenceLength);
        ids.Add(_clsId);

        foreach (var word in BasicTokenize(text))
        {
            if (ids.Count >= MaxSequenceLength - 1) break;
            foreach (var piece in WordPieceSplit(word))
            {
                if (ids.Count >= MaxSequenceLength - 1) break;
                ids.Add(piece);
            }
        }
        ids.Add(_sepId);

        var inputIds = new long[MaxSequenceLength];
        var attentionMask = new long[MaxSequenceLength];
        for (int i = 0; i < ids.Count; i++)
        {
            inputIds[i] = ids[i];
            attentionMask[i] = 1;
        }
        for (int i = ids.Count; i < MaxSequenceLength; i++)
            inputIds[i] = _padId;

        return new Encoded(inputIds, attentionMask, new long[MaxSequenceLength]);
    }

    /// <summary>Whitespace + punctuation split, lowercase. Matches the
    /// BERT-uncased "BasicTokenizer" preprocessing.</summary>
    private static IEnumerable<string> BasicTokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            var ch = char.ToLowerInvariant(c);
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else if (char.IsPunctuation(ch))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                yield return ch.ToString();
            }
            else
            {
                sb.Append(ch);
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>Greedy longest-match WordPiece. Walk from `start` forward
    /// trying ever-shorter substrings until one is in the vocab; emit
    /// it, advance `start`. Subsequent pieces get "##" prefix. Words
    /// longer than `_maxWordChars` go straight to [UNK].</summary>
    private IEnumerable<long> WordPieceSplit(string word)
    {
        if (word.Length > _maxWordChars) { yield return _unkId; yield break; }

        var start = 0;
        var firstPiece = true;
        while (start < word.Length)
        {
            var end = word.Length;
            string? match = null;
            while (start < end)
            {
                var sub = word.Substring(start, end - start);
                if (!firstPiece) sub = "##" + sub;
                if (_vocab.ContainsKey(sub)) { match = sub; break; }
                end--;
            }
            if (match == null) { yield return _unkId; yield break; }
            yield return _vocab[match];
            start = end;
            firstPiece = false;
        }
    }
}
