# ML model files

This directory holds the ONNX sentence-embedding model + WordPiece
vocab used by `OnnxSentenceEmbeddingService`. Both files are tracked
via Git LFS — clones without `git lfs install` + `git lfs pull` will
see 134-byte pointer stubs instead of the real binaries, and the app
will transparently fall back to `HashingEmbeddingService` at startup.

## Files expected

| Filename | Source | Approx size |
|---|---|---|
| `sentence-encoder.onnx` | `sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2` exported via `optimum-cli export onnx` | ~470 MB |
| `vocab.txt` | HuggingFace BERT WordPiece vocab from the same model card | ~1 MB |

## One-time setup

```bash
# Install LFS hooks once per machine
git lfs install

# Pull the actual binaries (without this you get pointer stubs)
git lfs pull

# Export the model from HuggingFace (one-time, by whoever commits)
pip install optimum[exporters] sentence-transformers
optimum-cli export onnx \
    --model sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2 \
    --opset 14 --task feature-extraction \
    ./out
mv ./out/model.onnx Accounting/models/sentence-encoder.onnx
mv ./out/vocab.txt   Accounting/models/vocab.txt
git add Accounting/models/sentence-encoder.onnx Accounting/models/vocab.txt
git commit -m "models: sentence-encoder MiniLM-L12-v2 ONNX export"
```

## Why these files are not in this commit

The model is a 470 MB binary; checking it in requires the LFS server
quota + network bandwidth to actually upload. The code paths that load
it are committed and tested with the fall-back path. Once an
authorized maintainer runs the export above, the live deployment
automatically picks up the higher-quality embedding without any code
change.
