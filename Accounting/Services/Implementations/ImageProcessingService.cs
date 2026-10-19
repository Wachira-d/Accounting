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
            return await SaveRawAsync(src, originalFileName, kind.Extension, absoluteDirectory, webBaseUrl);

        // SkiaSharp (ย้ายจาก ImageSharp 2026-10-19 — สูตร/ขนาดคงเดิม ดู SkiaImageOps):
        // decode + หมุนตาม EXIF ในขั้นเดียว · ไฟล์ที่ encode ออกมาไม่พก EXIF/IPTC/XMP ของ
        // ต้นฉบับ (พิกัด GPS · รุ่นอุปกรณ์) เพราะ Skia เขียนแต่พิกเซล
        SkiaImageOps.Decoded? decoded;
        try
        {
            var bytes = src.ToArray();
            decoded = await Task.Run(() => SkiaImageOps.DecodeUpright(bytes));
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
            src.Position = 0;
            return await SaveRawAsync(src, originalFileName, kind.Extension, absoluteDirectory, webBaseUrl);
        }

        using (decoded)
        {
            var spec = _specs.TryGetValue(profile, out var s) ? s : _specs[ImageProfile.Generic];

            using SKBitmap image = spec.ForceSquareCrop
                ? SkiaImageOps.CropToSquare(decoded.Bitmap, spec.MaxLongEdge)
                : SkiaImageOps.ResizeMax(decoded.Bitmap, spec.MaxLongEdge);

            // Choose output format: keep PNG when transparency matters, otherwise JPEG for smaller files
            var keepPng = spec.KeepTransparency && SkiaImageOps.HasTransparency(image);
            var ext = keepPng ? ".png" : ".jpg";
            var fileName = $"{Guid.NewGuid():N}{ext}";
            var absPath = Path.Combine(absoluteDirectory, fileName);

            var encoded = keepPng ? SkiaImageOps.EncodePng(image) : SkiaImageOps.EncodeJpeg(image, spec.Quality);
            await File.WriteAllBytesAsync(absPath, encoded);

            var finalBytes = new FileInfo(absPath).Length;
            var url = $"{webBaseUrl.TrimEnd('/')}/{fileName}";

            string? thumbUrl = null;
            if (generateThumb)
            {
                var tSpec = _specs[ImageProfile.ProductThumb];
                using var thumb = SkiaImageOps.CropToSquare(image, tSpec.MaxLongEdge);
                var thumbName = $"{Path.GetFileNameWithoutExtension(fileName)}_thumb.jpg";
                var thumbPath = Path.Combine(absoluteDirectory, thumbName);
                await File.WriteAllBytesAsync(thumbPath, SkiaImageOps.EncodeJpeg(thumb, tSpec.Quality));
                thumbUrl = $"{webBaseUrl.TrimEnd('/')}/{thumbName}";
            }

            _logger.LogInformation(
                "Image {File}: {OrigKB}KB -> {NewKB}KB ({Pct}% saved) profile={Profile} {W}x{H}",
                originalFileName, originalBytes / 1024, finalBytes / 1024,
                originalBytes == 0 ? 0 : 100 - (finalBytes * 100 / originalBytes),
                profile, image.Width, image.Height);

            return new ProcessedImageResult(url, absPath, originalBytes, finalBytes, image.Width, image.Height, thumbUrl);
        }
    }

    private static async Task<ProcessedImageResult> SaveRawAsync(
        Stream input, string originalName, string ext, string absoluteDirectory, string webBaseUrl)
    {
        var name = $"{Guid.NewGuid():N}{ext}";
        var abs = Path.Combine(absoluteDirectory, name);
        await using (var fs = File.Create(abs))
        {
            if (input.CanSeek) input.Position = 0;
            await input.CopyToAsync(fs);
        }
        var size = new FileInfo(abs).Length;
        return new ProcessedImageResult($"{webBaseUrl.TrimEnd('/')}/{name}", abs, size, size, 0, 0, null);
    }
}
