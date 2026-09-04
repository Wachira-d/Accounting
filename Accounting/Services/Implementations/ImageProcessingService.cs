using Accounting.Services.Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
        if (!kind.Value.IsImage)
            return await SaveRawAsync(src, originalFileName, kind.Value.Extension, absoluteDirectory, webBaseUrl);

        Image<Rgba32> image;
        try
        {
            image = await Image.LoadAsync<Rgba32>(src);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ImageSharp failed to decode {File} ({ContentType}); saving raw", originalFileName, contentType);
            src.Position = 0;
            // ⚠️ นามสกุลมาจาก **ผลตรวจไบต์** ไม่ใช่ชื่อไฟล์ของ client — decoder ล้ม
            // ไม่ได้แปลว่าไบต์โกหก (ไฟล์รูปเสียบางส่วนก็ decode ไม่ได้) แต่ถ้าปล่อยให้
            // client เลือกนามสกุลตรงนี้ ก็เท่ากับเปิดช่องเดิมกลับมาทางประตูหลัง
            return await SaveRawAsync(src, originalFileName, kind.Value.Extension, absoluteDirectory, webBaseUrl);
        }

        using (image)
        {
            var spec = _specs.TryGetValue(profile, out var s) ? s : _specs[ImageProfile.Generic];

            image.Mutate(ctx =>
            {
                // Respect EXIF orientation so phone photos aren't sideways,
                // then drop EXIF (location, device) before saving.
                ctx.AutoOrient();
            });
            image.Metadata.ExifProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;

            if (spec.ForceSquareCrop)
            {
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(spec.MaxLongEdge, spec.MaxLongEdge),
                    Mode = ResizeMode.Crop
                }));
            }
            else if (Math.Max(image.Width, image.Height) > spec.MaxLongEdge)
            {
                var (w, h) = image.Width >= image.Height
                    ? (spec.MaxLongEdge, 0)
                    : (0, spec.MaxLongEdge);
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(w, h),
                    Mode = ResizeMode.Max,
                    Sampler = KnownResamplers.Lanczos3
                }));
            }

            // Choose output format: keep PNG when transparency matters, otherwise JPEG for smaller files
            var keepPng = spec.KeepTransparency && HasTransparency(image);
            var ext = keepPng ? ".png" : ".jpg";
            var fileName = $"{Guid.NewGuid():N}{ext}";
            var absPath = Path.Combine(absoluteDirectory, fileName);

            if (keepPng)
            {
                await image.SaveAsPngAsync(absPath, new PngEncoder
                {
                    CompressionLevel = PngCompressionLevel.BestCompression,
                    ColorType = PngColorType.RgbWithAlpha
                });
            }
            else
            {
                await image.SaveAsJpegAsync(absPath, new JpegEncoder
                {
                    Quality = spec.Quality
                });
            }

            var finalBytes = new FileInfo(absPath).Length;
            var url = $"{webBaseUrl.TrimEnd('/')}/{fileName}";

            string? thumbUrl = null;
            if (generateThumb)
            {
                var tSpec = _specs[ImageProfile.ProductThumb];
                using var thumb = image.Clone(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(tSpec.MaxLongEdge, tSpec.MaxLongEdge),
                    Mode = ResizeMode.Crop,
                    Sampler = KnownResamplers.Lanczos3
                }));
                var thumbName = $"{Path.GetFileNameWithoutExtension(fileName)}_thumb.jpg";
                var thumbPath = Path.Combine(absoluteDirectory, thumbName);
                await thumb.SaveAsJpegAsync(thumbPath, new JpegEncoder { Quality = tSpec.Quality });
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

    private static bool HasTransparency(Image<Rgba32> image)
    {
        // Sample corners + center — full pixel scan is overkill for a heuristic.
        // If any sampled alpha < 255 we keep PNG.
        var sx = new[] { 0, image.Width / 2, image.Width - 1 };
        var sy = new[] { 0, image.Height / 2, image.Height - 1 };
        foreach (var x in sx)
            foreach (var y in sy)
                if (image[x, y].A < 255) return true;
        return false;
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
