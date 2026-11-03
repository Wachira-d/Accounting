using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 231 — favicon ที่อัปโหลดถูกแปลงเป็น JPG (โปรไฟล์ Avatar เก็บ PNG เฉพาะเมื่อมีความโปร่งใส) ⇒ แท็บเบราว์เซอร์แสดงไม่ถูกต้อง ·
/// โปรไฟล์ Favicon ต้องได้ PNG เสมอ · ทิศตรงข้าม: Avatar ยังเป็น JPG สำหรับรูปทึบตามเดิม</summary>
public class FaviconImageProfileTests
{
    private static byte[] OpaquePng(int w, int h)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) bmp.SetPixel(x, y, new SKColor(20, 40, 160));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static ImageProcessingService Svc() =>
        new(NullLogger<ImageProcessingService>.Instance, FileStorageDefaults.Local);

    [Fact]
    public async Task favicon_รูปทึบ_ได้PNGเสมอ_และเป็นจัตุรัส()
    {
        using var s = new MemoryStream(OpaquePng(300, 200));
        var r = await Svc().ProcessToBytesAsync(s, "image/png", "logo.png", ImageProfile.Favicon);
        Assert.Equal(".png", r.Extension);
        Assert.Equal("image/png", r.ContentType);
        Assert.Equal(r.Width, r.Height);
        Assert.True(r.Width <= 128);
    }

    [Fact]
    public async Task ทิศตรงข้าม_avatar_รูปทึบยังเป็นJPGตามเดิม()
    {
        using var s = new MemoryStream(OpaquePng(300, 300));
        var r = await Svc().ProcessToBytesAsync(s, "image/png", "me.png", ImageProfile.Avatar);
        Assert.Equal(".jpg", r.Extension);
    }
}
