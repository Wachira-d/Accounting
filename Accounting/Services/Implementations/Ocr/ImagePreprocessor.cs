using Accounting.Helpers;
using SkiaSharp;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// In-process image enhancement before OCR, on SkiaSharp (shared with the
/// PDF rasteriser via PDFtoImage, so this adds zero new native packages —
/// replaced SixLabors.ImageSharp 2026-10-19, see <see cref="SkiaImageOps"/>).
/// The goal: lift OCR accuracy on phone-camera photos and small / dark scans
/// where the raw pixels would confuse Tesseract or burn extra Azure DI
/// billable time.
///
/// Pipeline (each step is conditional — only fires when needed):
///   1. EXIF auto-rotate — phone photos commonly arrive with a rotation
///      flag in metadata; downstream OCR may or may not honor it. We
///      bake the rotation into the pixels so every consumer sees the
///      image upright.
///   2. Grayscale convert — most accounting docs are text on paper;
///      colour adds no signal and confuses Tesseract's binarization.
///      Azure DI handles this internally but the conversion shrinks
///      file size by 60-70%, cheaper to ship over the wire.
///   3. Auto-contrast — for under/over-exposed phone shots,
///      stretch the histogram so text rows have crisp edges.
///   4. Upscale (when needed) — Tesseract needs ≥300 DPI for Thai
///      tone-mark accuracy. If shorter side &lt;1200px, upscale 2×
///      bicubic; if &lt;800px, upscale 3×.
///
/// Skips:
///   • PDFs (have their own text layer or PDFtoImage handles raster)
///   • Formats Skia cannot decode (TIFF/HEIC) — returned untouched; Azure DI
///     and Leptonica read TIFF natively so the scan still proceeds
///   • Tiny placeholder / spam images (already rejected by preflight)
///
/// Output: jpeg bytes (always), original content type discarded. The
/// caller writes the preprocessed bytes back and sets contentType =
/// "image/jpeg" before invoking the OCR cascade.
/// </summary>
public static class ImagePreprocessor
{
    public record Result(byte[] Bytes, string ContentType, List<string> StepsApplied);

    /// <summary>ค่า contrast เดิมของ ImageSharp (<c>Contrast(1.15f)</c>) — คงไว้ตามกฎ §H</summary>
    public const float ContrastAmount = 1.15f;
    public const int JpegQuality = 92;

    /// <summary>Run conditional preprocessing. Returns the SAME bytes
    /// when none of the rules trigger (fast path for clean inputs).</summary>
    public static Result Process(byte[] inputBytes, string contentType, string? fileName)
    {
        var steps = new List<string>();
        // Skip non-images outright
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return new Result(inputBytes, contentType, steps);

        try
        {
            // 1. EXIF auto-rotate — bake the rotation flag into pixels
            // so downstream consumers see an upright image regardless of
            // how the source device tagged it.
            using var decoded = SkiaImageOps.DecodeUpright(inputBytes);
            if (decoded == null) return new Result(inputBytes, contentType, steps);
            if (decoded.ExifOrientation > 1 && decoded.ExifOrientation <= 8)
                steps.Add($"auto-rotate (EXIF orientation={decoded.ExifOrientation})");

            int originalW = decoded.Bitmap.Width;
            int originalH = decoded.Bitmap.Height;

            // 2. Grayscale — text on paper has no useful colour info. Bt.709
            // luma matches what Tesseract expects internally.
            SKBitmap image = SkiaImageOps.ApplyColorMatrix(decoded.Bitmap, SkiaImageOps.GrayscaleBt709Matrix());
            steps.Add("grayscale");

            // 3. Auto-contrast — push the histogram toward full dynamic
            // range; for phone shots in dim light this is often the
            // single biggest accuracy lift (binarizer now has crisp
            // black/white separation instead of dim greys).
            using (var gray = image)
                image = SkiaImageOps.ApplyColorMatrix(gray, SkiaImageOps.ContrastMatrix(ContrastAmount));
            steps.Add("contrast×1.15");

            // 4. Upscale when image is too small for reliable Thai OCR.
            // Tesseract for Thai needs ≥300 DPI (~1200px short side for
            // A4 capture). Cubic interpolation preserves stroke shape.
            int shortSide = Math.Min(image.Width, image.Height);
            int factor = shortSide < 800 ? 3 : shortSide < 1200 ? 2 : 1;
            if (factor > 1)
            {
                using (var small = image)
                    image = SkiaImageOps.Resize(small, small.Width * factor, small.Height * factor);
                steps.Add($"upscale {factor}× ({originalW}×{originalH} → {image.Width}×{image.Height})");
            }

            using (image)
            {
                // No-op fast path — if nothing changed materially, return original
                if (steps.Count == 0) return new Result(inputBytes, contentType, steps);

                // JPEG at quality 92 — preserves text edges, modest size
                return new Result(SkiaImageOps.EncodeJpeg(image, JpegQuality), "image/jpeg", steps);
            }
        }
        catch (Exception)
        {
            // Any decode/encode error → return original bytes untouched.
            // Preprocessing is best-effort; we never want to fail the scan.
            return new Result(inputBytes, contentType, steps);
        }
    }
}
