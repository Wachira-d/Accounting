#!/usr/bin/env bash
# Download Tesseract language data files needed for the embedded OCR engine.
# Run once after cloning the repo (or in CI before publishing).
#
# Usage:  ./scripts/download-tessdata.sh [fast|best]
#   fast  (default) — tessdata_fast: smaller files, comparable accuracy for invoices
#   best            — tessdata_best: ~3-5x larger, slightly higher accuracy

set -euo pipefail

VARIANT="${1:-fast}"
case "$VARIANT" in
  fast) REPO="tessdata_fast" ;;
  best) REPO="tessdata_best" ;;
  *)
    echo "Usage: $0 [fast|best]" >&2
    exit 1
    ;;
esac

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
TARGET_DIR="$SCRIPT_DIR/../Accounting/wwwroot/tessdata"
mkdir -p "$TARGET_DIR"

for LANG in eng tha; do
  URL="https://github.com/tesseract-ocr/${REPO}/raw/main/${LANG}.traineddata"
  echo "Downloading ${LANG}.traineddata from ${REPO}..."
  curl -L -f -o "${TARGET_DIR}/${LANG}.traineddata" "$URL"
done

echo "Done. Files installed to: $TARGET_DIR"
ls -lh "$TARGET_DIR" | grep traineddata
