using Accounting.Helpers;
using Accounting.Services.Interfaces;
using SkiaSharp;

namespace Accounting.Services.Implementations;

public class ImageProcessingService : IImageProcessingService
{
    private readonly ILogger<ImageProcessingService> _logger;

    public ImageProcessingService(ILogger<ImageProcessingService> logger)
    {
        _logger = logger;
    }

    private record ProfileSpec(int MaxLongEdge, int Quality, bool KeepTransparency, bool ForceSquareCrop);

    private static readonly Dictionary<ImageProfile, ProfileSpec> _specs = new()
    {
        [ImageProfile.ProductMain]  = new(1200, 82, false, false),
        [ImageProfile.ProductThumb] = new(400,  78, false, true),
        [ImageProfile.Logo]         = new(512,  90, true,  false),
        [ImageProfile.Banner]       = new(1920, 80, false, false),
        [ImageProfile.Slip]         = new(1600, 86, false, false),
        [ImageProfile.Avatar]       = new(256,  82, false, true),
        [ImageProfile.OcrSource]    = new(2400, 92, false, false),
        [ImageProfile.Generic]      = new(1600, 82, false, false),
    };

    public bool IsProcessableImage(string contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var ct = contentType.ToLowerInvariant();
        return ct is "image/jpeg" or "image/jpg" or "image/png" or "image/webp" or "image/gif" or "image/bmp" or "image/tiff";
    }

    public async Task<ProcessedImageResult> ProcessAndSaveAsync(
        Stream input,
        string contentType,
        string originalFileName,
        string absoluteDirectory,
        string webBaseUrl,
        ImageProfile profile,
        bool generateThumb = false)
    {
        Directory.CreateDirectory(absoluteDirectory);

        // ตัวแปลง (sniff ไบต์ → decode → resize/encode) อยู่ที่ TransformAsync ตัวเดียว — ใช้ร่วมกับ ProcessToBytesAsync (R-43)
        using var t = await TransformAsync(input, contentType, originalFileName, profile);

        var fileName = $"{Guid.NewGuid():N}{t.Extension}";
        var absPath = Path.Combine(absoluteDirectory, fileName);
        await File.WriteAllBytesAsync(absPath, t.Bytes);
        var url = $"{webBaseUrl.TrimEnd('/')}/{fileName}";

        if (t.IsRaw || t.Image == null)
            return new ProcessedImageResult(url, absPath, t.OriginalBytes, t.Bytes.LongLength, 0, 0, null);

        string? thumbUrl = null;
        if (generateThumb)
        {
            var tSpec = _specs[ImageProfile.ProductThumb];
            using var thumb = SkiaImageOps.CropToSquare(t.Image, tSpec.MaxLongEdge);
            var thumbName = $"{Path.GetFileNameWithoutExtension(fileName)}_thumb.jpg";
            var thumbPath = Path.Combine(absoluteDirectory, thumbName);
            await File.WriteAllBytesAsync(thumbPath, SkiaImageOps.EncodeJpeg(thumb, tSpec.Quality));
            thumbUrl = $"{webBaseUrl.TrimEnd('/')}/{thumbName}";
        }

        LogSaved(originalFileName, t, profile);
        return new ProcessedImageResult(url, absPath, t.OriginalBytes, t.Bytes.LongLength, t.Width, t.Height, thumbUrl);
    }

    public async Task<ProcessedImageBytes> ProcessToBytesAsync(
        Stream input,
        string contentType,
        string originalFileName,
        ImageProfile profile)
    {
        using var t = await TransformAsync(input, contentType, originalFileName, profile);
        if (!t.IsRaw) LogSaved(originalFileName, t, profile);
        return new ProcessedImageBytes(t.Bytes, t.Extension,
            Accounting.Helpers.UploadFileType.ContentTypeForExtension(t.Extension),
            t.OriginalBytes, t.Width, t.Height);
    }

    private void LogSaved(string originalFileName, Transformed t, ImageProfile profile) =>
        _logger.LogInformation(
            "Image {File}: {OrigKB}KB -> {NewKB}KB ({Pct}% saved) profile={Profile} {W}x{H}",
            originalFileName, t.OriginalBytes / 1024, t.Bytes.LongLength / 1024,
            t.OriginalBytes == 0 ? 0 : 100 - (t.Bytes.LongLength * 100 / t.OriginalBytes),
            profile, t.Width, t.Height);

    /// <summary>ผลแปลงในหน่วยความจำ — <c>Image</c> ถือไว้ให้ผู้เรียกทำ thumbnail ต่อ (dispose พร้อมกัน) · <c>IsRaw</c> = เซฟดิบ (PDF/ICO · decoder ล้ม)</summary>
    private sealed record Transformed(byte[] Bytes, string Extension, int Width, int Height, long OriginalBytes, bool IsRaw, SKBitmap? Image) : IDisposable
    {
        public void Dispose() => Image?.Dispose();
    }

    /// <summary>ตัวแปลงตัวเดียว: buffer → sniff ไบต์ → (ไม่ใช่รูป/decode ไม่ได้ ⇒ ดิบด้วยนามสกุลที่ระบบสรุป) → resize + encode ตาม profile</summary>
    private async Task<Transformed> TransformAsync(Stream input, string contentType, string originalFileName, ImageProfile profile)
    {
        // ═══ ไบต์จริงเป็นตัวตัดสินชนิดไฟล์ — ไม่ใช่ Content-Type/นามสกุลจาก client (F-03) ═══
        //
        // เดิม: ชนิดที่ "ประมวลผลไม่ได้" ถูกเซฟดิบ **ด้วยนามสกุลจากชื่อไฟล์ของ client**
        // ลงโฟลเดอร์ที่เสิร์ฟสาธารณะ ⇒ อัปโหลด x.html / x.js / x.svg แล้วได้ URL ที่
        // origin เดียวกับแอป ⇒ stored XSS ที่ผ่าน CSP `script-src 'self'` สมบูรณ์
        // (JWT อยู่ใน localStorage) · SVG ก็คือ XML ที่ฝัง <script> ได้ = HTML
        // ที่ปลอมเป็นรูป จึงถูกถอดออกจาก allow-list ทั้งชนิด
        //
        // buffer ก่อนเสมอ เพื่อ (ก) อ่านหัวไฟล์ได้ (ข) วัดขนาด (ค) retry ตอน decoder ล้ม
        using var src = new MemoryStream();
        await input.CopyToAsync(src);
        var originalBytes = src.Length;
        src.Position = 0;

        var kind = Accounting.Helpers.UploadFileType.Sniff(
            src.GetBuffer().AsSpan(0, (int)Math.Min(src.Length, Accounting.Helpers.UploadFileType.HeaderBytes)))
            ?? throw new Accounting.Helpers.UnsupportedUploadException(
                Accounting.Helpers.UploadFileType.RejectMessage);
        src.Position = 0;

        // ไม่ใช่รูป (PDF/ICO) — เซฟดิบด้วยนามสกุล **ที่ระบบสรุปเอง**
        if (!kind.IsImage)
            return new Transformed(src.ToArray(), kind.Extension, 0, 0, originalBytes, true, null);

        // SkiaSharp (ย้ายจาก ImageSharp 2026-10-19 — สูตร/ขนาดคงเดิม ดู SkiaImageOps):
        // decode + หมุนตาม EXIF ในขั้นเดียว · ไฟล์ที่ encode ออกมาไม่พก EXIF/IPTC/XMP ของ
        // ต้นฉบับ (พิกัด GPS · รุ่นอุปกรณ์) เพราะ Skia เขียนแต่พิกเซล
        SkiaImageOps.Decoded? decoded;
        var raw = src.ToArray();
        try
        {
            decoded = await Task.Run(() => SkiaImageOps.DecodeUpright(raw));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SkiaSharp failed to decode {File} ({ContentType}); saving raw", originalFileName, contentType);
            decoded = null;
        }
        if (decoded == null)
        {
            // decoder ล้ม หรือเป็นรูปแบบที่ Skia ไม่อ่าน (TIFF/HEIC) — เซฟดิบด้วยนามสกุล **จากผลตรวจไบต์**
            // ไม่ใช่ชื่อไฟล์ของ client: decoder ล้มไม่ได้แปลว่าไบต์โกหก (ไฟล์รูปเสียบางส่วนก็ decode
            // ไม่ได้) แต่ถ้าปล่อยให้ client เลือกนามสกุลตรงนี้ ก็เท่ากับเปิดช่องเดิมกลับมาทางประตูหลัง
            _logger.LogWarning("SkiaSharp cannot decode {File} ({ContentType}, sniffed {Kind}); saving raw", originalFileName, contentType, kind.Extension);
            return new Transformed(raw, kind.Extension, 0, 0, originalBytes, true, null);
        }

        using (decoded)
        {
            var spec = _specs.TryGetValue(profile, out var s) ? s : _specs[ImageProfile.Generic];

            SKBitmap image = spec.ForceSquareCrop
                ? SkiaImageOps.CropToSquare(decoded.Bitmap, spec.MaxLongEdge)
                : SkiaImageOps.ResizeMax(decoded.Bitmap, spec.MaxLongEdge);

            // Choose output format: keep PNG when transparency matters, otherwise JPEG for smaller files
            var keepPng = spec.KeepTransparency && SkiaImageOps.HasTransparency(image);
            var ext = keepPng ? ".png" : ".jpg";
            var encoded = keepPng ? SkiaImageOps.EncodePng(image) : SkiaImageOps.EncodeJpeg(image, spec.Quality);
            return new Transformed(encoded, ext, image.Width, image.Height, originalBytes, false, image);
        }
    }
}
