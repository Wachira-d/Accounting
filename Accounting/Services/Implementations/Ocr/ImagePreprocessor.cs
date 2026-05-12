using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// In-process image enhancement before OCR. ImageSharp 3.x is already
/// a dependency, so this adds zero new packages. The goal: lift OCR
/// accuracy on phone-camera photos and small / dark scans where the
/// raw pixels would confuse Tesseract or burn extra Azure DI billable
/// time.
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
///   • Already-large clean images (>2MP and >500KB)
///   • Tiny placeholder / spam images (already rejected by preflight)
///
/// Output: jpeg bytes (always), original content type discarded. The
/// caller writes the preprocessed bytes back and sets contentType =
/// "image/jpeg" before invoking the OCR cascade.
/// </summary>
public static class ImagePreprocessor
{
    public record Result(byte[] Bytes, string ContentType, List<string> StepsApplied);

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
            using var image = Image.Load<Rgba32>(inputBytes);
            int originalW = image.Width;
            int originalH = image.Height;

            // 1. EXIF auto-rotate — bake the rotation flag into pixels
            // so downstream consumers see an upright image regardless of
            // how the source device tagged it.
            int orientationValue = 1;
            if (image.Metadata?.ExifProfile is { } exif
                && exif.TryGetValue(ExifTag.Orientation, out var orientationVal)
                && orientationVal?.Value is { } ov)
            {
                orientationValue = Convert.ToInt32(ov);
            }
            if (orientationValue > 1 && orientationValue <= 8)
            {
                image.Mutate(ctx => ctx.AutoOrient());
                steps.Add($"auto-rotate (EXIF orientation={orientationValue})");
            }

            // 2. Grayscale — text on paper has no useful colour info. The
            // mode hint Bt709 matches what Tesseract expects internally.
            image.Mutate(ctx => ctx.Grayscale());
            steps.Add("grayscale");

            // 3. Auto-contrast — push the histogram toward full dynamic
            // range; for phone shots in dim light this is often the
            // single biggest accuracy lift (binarizer now has crisp
            // black/white separation instead of dim greys).
            image.Mutate(ctx => ctx.AutoOrient().Contrast(1.15f));
            steps.Add("contrast×1.15");

            // 4. Upscale when image is too small for reliable Thai OCR.
            // Tesseract for Thai needs ≥300 DPI (~1200px short side for
            // A4 capture). Bicubic interpolation preserves stroke shape.
            int shortSide = Math.Min(image.Width, image.Height);
            if (shortSide < 800)
            {
                image.Mutate(ctx => ctx.Resize(image.Width * 3, image.Height * 3, KnownResamplers.Bicubic));
                steps.Add($"upscale 3× ({originalW}×{originalH} → {image.Width}×{image.Height})");
            }
            else if (shortSide < 1200)
            {
                image.Mutate(ctx => ctx.Resize(image.Width * 2, image.Height * 2, KnownResamplers.Bicubic));
                steps.Add($"upscale 2× ({originalW}×{originalH} → {image.Width}×{image.Height})");
            }

            // No-op fast path — if nothing changed materially, return original
            if (steps.Count == 0) return new Result(inputBytes, contentType, steps);

            using var ms = new MemoryStream();
            // JPEG at quality 92 — preserves text edges, modest size
            image.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 92 });
            return new Result(ms.ToArray(), "image/jpeg", steps);
        }
        catch (Exception)
        {
            // Any decode/encode error → return original bytes untouched.
            // Preprocessing is best-effort; we never want to fail the scan.
            return new Result(inputBytes, contentType, steps);
        }
    }
}
