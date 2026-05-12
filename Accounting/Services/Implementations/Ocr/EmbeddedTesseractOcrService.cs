using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Tesseract;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// In-process OCR engine using Tesseract via the official .NET wrapper.
///
/// Why "embedded":
///   • No external service to install (Python ocr-service, Docker container, etc.)
///   • No internet dependency (Azure DI requires API access)
///   • Always available as a deterministic fallback when Azure fails or
///     the optional Python service isn't running
///
/// Requirements:
///   • Tesseract language data files (eng.traineddata, tha.traineddata) must
///     exist in wwwroot/tessdata. Download with scripts/download-tessdata.sh.
///   • The Tesseract NuGet package bundles native libs for win/linux/osx.
///
/// Thread safety:
///   • TesseractEngine is NOT thread-safe. We use a per-language pool keyed
///     by thread (one engine per language per thread is the documented pattern).
///   • For ASP.NET Core's request-pool model, this gives good throughput
///     without locking.
/// </summary>
public class EmbeddedTesseractOcrService : IDisposable
{
    private readonly ILogger<EmbeddedTesseractOcrService> _logger;
    private readonly string _tessdataPath;
    private readonly bool _isReady;
    private readonly string[] _availableLanguages;

    // One engine per (thread, language-combo). TesseractEngine is not thread-safe;
    // ThreadLocal gives each request thread its own engine without locking.
    private readonly ThreadLocal<TesseractEngine?> _engine;
    private readonly string _languageString;

    public bool IsAvailable => _isReady;
    public IReadOnlyList<string> Languages => _availableLanguages;
    public string TessdataPath => _tessdataPath;

    public EmbeddedTesseractOcrService(IWebHostEnvironment env, ILogger<EmbeddedTesseractOcrService> logger)
    {
        _logger = logger;
        _tessdataPath = Path.Combine(env.WebRootPath ?? "wwwroot", "tessdata");

        if (!Directory.Exists(_tessdataPath))
        {
            _logger.LogWarning("Embedded Tesseract disabled — tessdata folder not found at {Path}", _tessdataPath);
            _isReady = false;
            _availableLanguages = Array.Empty<string>();
            _languageString = "";
            _engine = new ThreadLocal<TesseractEngine?>(() => null);
            return;
        }

        _availableLanguages = Directory.GetFiles(_tessdataPath, "*.traineddata")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name)
            .ToArray();

        if (_availableLanguages.Length == 0)
        {
            _logger.LogWarning("Embedded Tesseract disabled — no .traineddata files in {Path}. " +
                "Run scripts/download-tessdata.sh to install Thai + English.", _tessdataPath);
            _isReady = false;
            _languageString = "";
            _engine = new ThreadLocal<TesseractEngine?>(() => null);
            return;
        }

        // Tesseract takes a "+" separated list of languages. Thai documents
        // typically mix Thai + English (vendor names, amounts), so we use both
        // when available.
        _languageString = string.Join("+", _availableLanguages);
        _isReady = true;

        // Lazy per-thread engine creation. The engine survives for the thread's
        // lifetime — ASP.NET Core reuses request threads so this amortizes well.
        _engine = new ThreadLocal<TesseractEngine?>(() =>
        {
            try
            {
                var engine = new TesseractEngine(_tessdataPath, _languageString, EngineMode.LstmOnly);
                // ─── Thai-tuned engine variables ───
                // 1. preserve_interword_spaces=1 keeps the gaps between
                //    Thai words. Default 0 collapses adjacent characters
                //    aggressively which mangles Thai vowels/tone marks.
                // 2. user_defined_dpi=300 — when the input image has no DPI
                //    metadata Tesseract assumes 70dpi and degrades Thai
                //    recognition. Forcing 300 matches our preprocessor's
                //    upscale target.
                engine.SetVariable("preserve_interword_spaces", "1");
                engine.SetVariable("user_defined_dpi", "300");
                return engine;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Tesseract engine on thread {Tid}", Environment.CurrentManagedThreadId);
                return null;
            }
        }, trackAllValues: true);

        _logger.LogInformation("Embedded Tesseract initialized — languages: {Langs}", _languageString);
    }

    /// <summary>
    /// Run OCR on raw image bytes. Returns the extracted text and an estimated
    /// confidence score (mean word confidence from Tesseract, 0-1 range).
    /// </summary>
    public async Task<EmbeddedOcrResult> ExtractTextAsync(byte[] imageBytes, string contentType, string? fileName = null)
    {
        if (!_isReady)
            return new EmbeddedOcrResult(false, "", 0m, "Embedded Tesseract not initialized (missing tessdata)");

        if (imageBytes.Length == 0)
            return new EmbeddedOcrResult(false, "", 0m, "Empty file");

        // PDF detection: content-type OR .pdf suffix OR %PDF- magic bytes
        var isPdf = (contentType ?? "").Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
            || (fileName ?? "").EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || (imageBytes.Length >= 4 && imageBytes[0] == 0x25 && imageBytes[1] == 0x50 && imageBytes[2] == 0x44 && imageBytes[3] == 0x46);

        // PDFs are auto-rasterized to PNG pages in-process via PDFtoImage (PDFium).
        // Each page is OCR'd separately and the text is concatenated. This keeps
        // the Embedded tier usable for the most common scanned-receipt format
        // (PDF) without requiring external services.
        if (isPdf)
        {
            return await ExtractTextFromPdfAsync(imageBytes, fileName);
        }

        // Preprocess: convert to grayscale + upscale small images. Tesseract LSTM
        // is sensitive to resolution — at <300 DPI it loses accuracy. We upscale
        // anything narrower than 1500px to give the model more pixels to work with.
        byte[] processedPng;
        try
        {
            processedPng = await PreprocessAsync(imageBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image preprocessing failed for {File} — falling back to raw bytes", fileName ?? "(unnamed)");
            processedPng = imageBytes;
        }

        var engine = _engine.Value;
        if (engine == null)
            return new EmbeddedOcrResult(false, "", 0m, "Tesseract engine unavailable on this thread");

        try
        {
            // Tesseract's CPU work — keep it off the request thread when possible
            return await Task.Run(() =>
            {
                using var pix = Pix.LoadFromMemory(processedPng);
                // First pass: PSM=Auto detects layout + segments paragraphs.
                // Better than default SingleBlock for receipts (multi-column
                // header + line-item table + summary block).
                using var firstPage = engine.Process(pix, PageSegMode.Auto);
                var text = firstPage.GetText() ?? "";
                var confidence = firstPage.GetMeanConfidence();

                // Retry with sparse-text PSM when confidence is low or
                // text came back nearly empty. Receipts with crowded
                // multi-language layouts (Thai vendor block + English
                // amounts) sometimes parse better in SparseText mode,
                // which doesn't assume rectangular regions.
                if (confidence < 0.55f || text.Trim().Length < 40)
                {
                    using var retryPage = engine.Process(pix, PageSegMode.SparseText);
                    var retryText = retryPage.GetText() ?? "";
                    var retryConf = retryPage.GetMeanConfidence();
                    if (retryConf > confidence + 0.05f || retryText.Length > text.Length * 1.3)
                    {
                        text = retryText;
                        confidence = retryConf;
                    }
                }
                return new EmbeddedOcrResult(true, text.Trim(), (decimal)confidence, null);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tesseract OCR failed for {File}", fileName ?? "(unnamed)");
            return new EmbeddedOcrResult(false, "", 0m, $"Tesseract error: {ex.Message}");
        }
    }

    /// <summary>
    /// Preprocess the image to maximize OCR accuracy:
    ///   1. Decode (handles JPEG/PNG/BMP/TIFF/WebP via ImageSharp)
    ///   2. Convert to grayscale — Tesseract LSTM works internally on grayscale
    ///   3. Upscale if too small (LSTM needs ≥30px character height)
    ///   4. Re-encode as PNG (lossless) to feed back to Tesseract
    /// </summary>
    /// <summary>
    /// Rasterize a PDF to PNG pages (via PDFtoImage + PDFium native libs that
    /// ship with the NuGet) and OCR each page with Tesseract. Multi-page output
    /// is concatenated with form-feed separators so downstream rule-based
    /// parsing still treats the document as a single OCR result. Mean confidence
    /// is averaged across pages.
    ///
    /// Why 200 DPI: balances accuracy (Tesseract needs ≥150 DPI for reliable
    /// LSTM recognition) against memory + speed. 200 DPI on A4 ≈ 1654×2339 px.
    /// </summary>
    private async Task<EmbeddedOcrResult> ExtractTextFromPdfAsync(byte[] pdfBytes, string? fileName)
    {
        const int RenderDpi = 200;
        const int MaxPagesPerScan = 10;   // safety cap — refuses humongous PDFs

        List<byte[]> pageImages;
        try
        {
            pageImages = await Task.Run(() => RasterizePdfToPngPages(pdfBytes, RenderDpi, MaxPagesPerScan));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PDF rasterization failed for {File}", fileName ?? "(unnamed)");
            return new EmbeddedOcrResult(false, "", 0m,
                $"แปลง PDF เป็นรูปภาพไม่สำเร็จ: {ex.Message}");
        }

        if (pageImages.Count == 0)
            return new EmbeddedOcrResult(false, "", 0m, "PDF ว่าง — ไม่มีหน้าให้ OCR");

        var engine = _engine.Value;
        if (engine == null)
            return new EmbeddedOcrResult(false, "", 0m, "Tesseract engine unavailable on this thread");

        var combinedText = new System.Text.StringBuilder();
        decimal totalConfidence = 0m;
        int successfulPages = 0;

        for (int i = 0; i < pageImages.Count; i++)
        {
            try
            {
                // Each page goes through the same preprocessing (upscale + grayscale)
                // as a standalone image upload. Lets the Tesseract LSTM see a
                // consistent input distribution regardless of source format.
                var processed = await PreprocessAsync(pageImages[i]);
                var (pageText, pageConfidence) = await Task.Run(() =>
                {
                    using var pix = Pix.LoadFromMemory(processed);
                    using var page = engine.Process(pix);
                    return (page.GetText() ?? "", (decimal)page.GetMeanConfidence());
                });

                if (pageImages.Count > 1)
                    combinedText.AppendLine($"=== หน้า {i + 1} / {pageImages.Count} ===");
                combinedText.AppendLine(pageText.Trim());
                if (i < pageImages.Count - 1) combinedText.AppendLine();

                totalConfidence += pageConfidence;
                successfulPages++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OCR failed on PDF page {Page} of {File}", i + 1, fileName ?? "(unnamed)");
                combinedText.AppendLine($"[หน้า {i + 1}: OCR ล้มเหลว — {ex.Message}]");
            }
        }

        var avgConfidence = successfulPages > 0 ? totalConfidence / successfulPages : 0m;
        var skippedNote = pageImages.Count >= MaxPagesPerScan
            ? $" (เกินขีดจำกัด — สแกนเฉพาะ {MaxPagesPerScan} หน้าแรก)" : "";

        _logger.LogInformation("OCR'd PDF {File}: {Pages} page(s), confidence {Conf:P0}{Skipped}",
            fileName ?? "(unnamed)", successfulPages, avgConfidence, skippedNote);

        return new EmbeddedOcrResult(
            Success: successfulPages > 0,
            Text: combinedText.ToString().Trim(),
            Confidence: avgConfidence,
            Error: successfulPages == 0 ? "ไม่มีหน้าใดผ่าน OCR สำเร็จ" : null);
    }

    /// <summary>
    /// Render PDF pages to PNG byte arrays. Synchronous (CPU-bound) — caller
    /// wraps in Task.Run. Bounded by maxPages to prevent OOM on absurdly large
    /// PDFs (the OCR microservice has the same cap upstream).
    /// </summary>
    private static List<byte[]> RasterizePdfToPngPages(byte[] pdfBytes, int dpi, int maxPages)
    {
        var pages = new List<byte[]>();
        // PDFtoImage.Conversion.ToImages enumerates SKBitmap per page. Each is
        // disposed after encoding to PNG so memory pressure stays bounded by
        // single-page size, not full-doc size. WithAnnotations=true so form-field
        // text (common in scanned PDFs) shows up in the OCR output. Grayscale
        // conversion happens downstream in PreprocessAsync — keeps this code
        // version-agnostic across PDFtoImage 5.x releases where the Grayscale
        // option arrived in different minor versions.
        var renderOptions = new PDFtoImage.RenderOptions(
            Dpi: dpi,
            WithAnnotations: true);
        int pageIndex = 0;
        // CA1416: PDFtoImage's ToImages declares per-platform attributes (Android/iOS/etc)
        // that the analyzer flags conservatively. Our deployment targets — Windows/Linux/
        // macOS — are all explicitly supported by the library, so suppress the noise here.
#pragma warning disable CA1416
        foreach (var bitmap in PDFtoImage.Conversion.ToImages(pdfBytes, options: renderOptions))
        {
            using (bitmap)
            {
                using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, quality: 100);
                pages.Add(data.ToArray());
            }
            pageIndex++;
            if (pageIndex >= maxPages) break;
        }
#pragma warning restore CA1416
        return pages;
    }

    private static async Task<byte[]> PreprocessAsync(byte[] input)
    {
        using var image = await Task.Run(() => Image.Load<Rgba32>(input));

        // Upscale narrow images. 1500px width is a reasonable target for invoices.
        if (image.Width < 1500)
        {
            var scale = 1500.0 / image.Width;
            var newW = 1500;
            var newH = (int)(image.Height * scale);
            image.Mutate(x => x.Resize(newW, newH, KnownResamplers.Lanczos3));
        }

        // Grayscale for Tesseract
        image.Mutate(x => x.Grayscale());

        using var ms = new MemoryStream();
        await image.SaveAsPngAsync(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_engine.IsValueCreated)
        {
            foreach (var eng in _engine.Values)
                eng?.Dispose();
        }
        _engine.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Result of an embedded OCR run. Confidence is mean word confidence (0-1).
/// </summary>
public record EmbeddedOcrResult(
    bool Success,
    string Text,
    decimal Confidence,
    string? Error
);
