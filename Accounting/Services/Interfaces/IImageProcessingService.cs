namespace Accounting.Services.Interfaces;

/// <summary>
/// Sizing profile picked per upload context. Each profile is tuned
/// to be just enough resolution + quality for the place it's displayed —
/// product gallery doesn't need a 12 MP source file, a logo doesn't need 4K,
/// and an OCR scan must keep enough detail for text recognition.
/// </summary>
public enum ImageProfile
{
    /// <summary>Product gallery main image. ~1200px long edge, JPEG q82.</summary>
    ProductMain,
    /// <summary>Product card thumbnail. ~400px square, JPEG q78.</summary>
    ProductThumb,
    /// <summary>Logo / favicon — keep transparency. ~512px long edge.</summary>
    Logo,
    /// <summary>Hero / banner. ~1920px wide, JPEG q80.</summary>
    Banner,
    /// <summary>Payment slip / receipt photo. ~1600px long edge, JPEG q86 — must stay readable.</summary>
    Slip,
    /// <summary>Avatar / contact / employee photo. ~256px square.</summary>
    Avatar,
    /// <summary>OCR source — preserve detail, just strip metadata + cap absurd sizes. ~2400px long edge, q92.</summary>
    OcrSource,
    /// <summary>Generic content image. ~1600px long edge, q82.</summary>
    Generic
}

public record ProcessedImageResult(
    string RelativeUrl,
    string AbsolutePath,
    long OriginalBytes,
    long FinalBytes,
    int Width,
    int Height,
    string? ThumbRelativeUrl
);

/// <summary>ผลประมวลผลรูปแบบ<b>ไม่แตะ disk</b> — ผู้เรียกส่งไบต์ต่อให้ที่เก็บไฟล์กลาง (<c>IFileAttachmentService</c>) ·
/// <c>Extension</c>/<c>ContentType</c> สรุปจาก<b>ไบต์จริง</b> (ไม่ใช่ที่ client บอก) · Width/Height = 0 เมื่อเป็นไฟล์ที่เซฟดิบ (PDF/decoder ล้ม)</summary>
public record ProcessedImageBytes(
    byte[] Content,
    string Extension,
    string ContentType,
    long OriginalBytes,
    int Width,
    int Height);

public interface IImageProcessingService
{
    /// <summary>เหมือน <see cref="ProcessAndSaveAsync"/> แต่คืน<b>ไบต์</b>แทนการเขียนไฟล์ — สำหรับเส้นที่เก็บผ่าน
    /// <c>IFileAttachmentService</c> (สื่อ CMS · สลิปออเดอร์ — R-43 กฎเหล็ก #4 D "ไฟล์ห้ามเขียน local disk ตรง") ·
    /// ชนิดไฟล์ตัดสินจากไบต์จริง · ไม่อยู่ใน allow-list ⇒ <c>UnsupportedUploadException</c> เหมือนกัน</summary>
    Task<ProcessedImageBytes> ProcessToBytesAsync(
        Stream input,
        string contentType,
        string originalFileName,
        ImageProfile profile);

    /// <summary>
    /// Reads an uploaded form file, downsizes / recompresses per profile, and saves
    /// to <paramref name="absoluteDirectory"/>. Returns the final web-relative URL
    /// (assumes the directory lives under wwwroot). Pass-through for SVG.
    /// Throws if file is not a supported image. For non-image content (PDF etc.),
    /// caller should save raw — this service is for raster images only.
    /// </summary>
    Task<ProcessedImageResult> ProcessAndSaveAsync(
        Stream input,
        string contentType,
        string originalFileName,
        string absoluteDirectory,
        string webBaseUrl,
        ImageProfile profile,
        bool generateThumb = false);

    /// <summary>True for raster image types we can transform.</summary>
    bool IsProcessableImage(string contentType);
}
