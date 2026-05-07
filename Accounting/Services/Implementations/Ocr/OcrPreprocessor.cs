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

        // Verify magic bytes match the claimed type — protects against malicious uploads
        // that lie about their MIME and could choke the OCR service.
        if (!MagicBytesMatch(fileBytes, effectiveType))
            return new PreflightResult(false, "ไฟล์เสียหรือชนิดไฟล์ไม่ตรงกับเนื้อหา");

        return new PreflightResult(true, null);
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
