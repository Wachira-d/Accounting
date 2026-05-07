namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Lightweight pre-flight checks before sending a file to OCR.
/// Azure DI handles rotation/deskew/contrast internally — we only guard
/// against payload issues that would cause the API call to fail outright.
/// </summary>
public static class OcrPreprocessor
{
    public const long MaxFileSize = 50 * 1024 * 1024; // 50MB Azure DI v4.0 hard cap

    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg", "image/jpg", "image/png", "image/bmp", "image/tiff", "image/heif", "image/webp",
    };

    public record PreflightResult(bool Ok, string? ErrorMessage);

    /// <summary>Minimum file size for an image to plausibly contain readable text.
    /// Below this, the OCR will produce noise and waste quota — reject upfront.</summary>
    private const int MinImageBytes = 30 * 1024;   // 30KB

    /// <summary>Minimum image dimension (pixels) — below this OCR consistently fails
    /// on Thai text. Computed from JPEG/PNG header bytes (no ImageSharp dependency).</summary>
    private const int MinImageDimension = 600;

    public static PreflightResult Check(byte[] fileBytes, string contentType, string? fileName)
    {
        if (fileBytes.Length == 0)
            return new PreflightResult(false, "ไฟล์ว่าง");
        if (fileBytes.Length > MaxFileSize)
            return new PreflightResult(false, $"ไฟล์ใหญ่เกิน {MaxFileSize / (1024 * 1024)}MB");

        var effectiveType = string.IsNullOrEmpty(contentType)
            ? GuessContentTypeFromExtension(fileName)
            : contentType;

        if (!SupportedTypes.Contains(effectiveType))
            return new PreflightResult(false, $"ประเภทไฟล์ไม่รองรับ: {effectiveType}");

        if (!MagicBytesMatch(fileBytes, effectiveType))
            return new PreflightResult(false, "ไฟล์เสียหรือชนิดไฟล์ไม่ตรงกับเนื้อหา");

        // Image-only quality heuristics (PDF gets full Azure DI / local processing
        // without these checks since PDFs can have 1-pixel-tall page footers etc.)
        if (effectiveType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            if (fileBytes.Length < MinImageBytes)
                return new PreflightResult(false,
                    $"ไฟล์รูปเล็กเกินไป ({fileBytes.Length / 1024}KB) — โอกาสอ่านสำเร็จต่ำมาก กรุณาถ่ายใหม่ที่ความละเอียดสูงกว่า");

            var (w, h) = ReadImageDimensions(fileBytes, effectiveType);
            if (w > 0 && h > 0)
            {
                if (w < MinImageDimension || h < MinImageDimension)
                    return new PreflightResult(false,
                        $"ความละเอียดต่ำเกินไป ({w}×{h}px) — แนะนำขั้นต่ำ {MinImageDimension}×{MinImageDimension}px เพื่อให้ OCR แม่นยำ");

                // Aspect ratio sanity (10:1 or worse usually = bad scan / partial capture)
                var ratio = (double)Math.Max(w, h) / Math.Min(w, h);
                if (ratio > 10)
                    return new PreflightResult(false,
                        $"สัดส่วนภาพผิดปกติ ({w}×{h}, ratio {ratio:N1}:1) — ภาพอาจถูกครอปไม่ถูกต้อง");
            }
        }

        return new PreflightResult(true, null);
    }

    /// <summary>Read width/height directly from image header bytes (zero-allocation,
    /// no decoding). Returns (0,0) if format unsupported or header malformed.</summary>
    private static (int Width, int Height) ReadImageDimensions(byte[] data, string contentType)
    {
        try
        {
            switch (contentType.ToLowerInvariant())
            {
                case "image/png":
                    if (data.Length >= 24)
                        return (
                            (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19],
                            (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23]);
                    break;
                case "image/jpeg":
                case "image/jpg":
                    return ReadJpegDimensions(data);
                case "image/bmp":
                    if (data.Length >= 26)
                        return (
                            BitConverter.ToInt32(data, 18),
                            BitConverter.ToInt32(data, 22));
                    break;
                case "image/gif":
                    if (data.Length >= 10)
                        return (
                            BitConverter.ToInt16(data, 6),
                            BitConverter.ToInt16(data, 8));
                    break;
            }
        }
        catch { /* malformed header — fall through */ }
        return (0, 0);
    }

    private static (int Width, int Height) ReadJpegDimensions(byte[] data)
    {
        // Walk JPEG markers looking for SOF0/SOF2 (start-of-frame) which contains dims
        var i = 2; // skip 0xFFD8 SOI
        while (i < data.Length - 9)
        {
            if (data[i] != 0xFF) { i++; continue; }
            var marker = data[i + 1];
            // SOF markers: 0xC0-0xC3, 0xC5-0xC7, 0xC9-0xCB, 0xCD-0xCF
            if ((marker >= 0xC0 && marker <= 0xC3) || (marker >= 0xC5 && marker <= 0xC7) ||
                (marker >= 0xC9 && marker <= 0xCB) || (marker >= 0xCD && marker <= 0xCF))
            {
                if (i + 9 < data.Length)
                    return (
                        (data[i + 7] << 8) | data[i + 8],
                        (data[i + 5] << 8) | data[i + 6]);
                break;
            }
            // Skip variable-length segment
            if (i + 3 < data.Length)
                i += 2 + ((data[i + 2] << 8) | data[i + 3]);
            else
                break;
        }
        return (0, 0);
    }

    public static string EffectiveContentType(string contentType, string? fileName)
        => string.IsNullOrEmpty(contentType) ? GuessContentTypeFromExtension(fileName) : contentType;

    private static string GuessContentTypeFromExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".heif" or ".heic" => "image/heif",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };
    }

    private static bool MagicBytesMatch(byte[] data, string contentType)
    {
        if (data.Length < 4) return false;
        return contentType.ToLowerInvariant() switch
        {
            "application/pdf" => data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46,
            "image/jpeg" or "image/jpg" => data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF,
            "image/png" => data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47,
            "image/gif" => data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46,
            "image/bmp" => data[0] == 0x42 && data[1] == 0x4D,
            "image/tiff" => (data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D),
            "image/webp" => data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
                && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50,
            "image/heif" or "image/heic" => data.Length >= 12 && data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70,
            _ => true, // unknown — let Azure DI decide
        };
    }
}
