# Tesseract Language Data Files

This folder must contain `.traineddata` files for the languages you want the
embedded OCR engine to recognize. The files ship with the app and are loaded
on startup by `EmbeddedTesseractOcrService`.

## Required files

| File | Size | Source |
|---|---|---|
| `eng.traineddata` | ~4 MB | English |
| `tha.traineddata` | ~6 MB | Thai |

## How to install

Download the `tessdata_fast` variants (smaller, faster — accuracy is comparable
to the legacy data for invoice OCR):

```bash
cd Accounting/wwwroot/tessdata
curl -L -O https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata
curl -L -O https://github.com/tesseract-ocr/tessdata_fast/raw/main/tha.traineddata
```

Or use the `tessdata_best` variants for better accuracy at ~3-5x file size:

```bash
curl -L -O https://github.com/tesseract-ocr/tessdata_best/raw/main/eng.traineddata
curl -L -O https://github.com/tesseract-ocr/tessdata_best/raw/main/tha.traineddata
```

## Verification

After download, the app will log on startup:

```
[OCR] Embedded Tesseract initialized — languages: eng, tha
```

If you see `[OCR] Embedded Tesseract disabled — no language data found in wwwroot/tessdata`,
the files are missing or unreadable.

## Why not bundled in the repo?

The `.traineddata` files are large binaries (10 MB combined). Bundling them in
git would balloon the repo. They are downloaded once during deployment via
the `download-tessdata.sh` script or this manual command.
