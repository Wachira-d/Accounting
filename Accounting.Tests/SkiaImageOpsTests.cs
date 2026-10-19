using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using SkiaSharp;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ย้ายงานพิกเซลจาก SixLabors.ImageSharp → SkiaSharp (2026-10-19) · ล็อกสูตรที่ต้องเท่าของเดิม (กฎเหล็ก #4 §H "ห้ามแย่ลง"):
/// หมุนตาม EXIF · Bt.709 grayscale · contrast(1.15) รอบกึ่งกลาง · กติกาขยาย 3×/2× ของ ImagePreprocessor · ResizeMode.Max/Crop ·
/// metadata ถูกถอดตอน encode · ทิศตรงข้าม: ภาพไม่มี EXIF ต้องไม่ถูกหมุน · ไบต์ขยะต้องคืนของเดิมไม่ throw
/// </summary>
public class SkiaImageOpsTests
{
    private static SKBitmap Make(int w, int h, Func<int, int, SKColor> color)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, color(x, y));
        return bmp;
    }

    private static SKBitmap LeftRedRightBlue(int w, int h)
        => Make(w, h, (x, _) => x < w / 2 ? SKColors.Red : SKColors.Blue);

    /// <summary>APP1 EXIF ขั้นต่ำ (TIFF little-endian · IFD0 มี tag 0x0112 Orientation เพียงตัวเดียว) แทรกหลัง SOI</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, byte orientation)
    {
        Assert.Equal(0xFF, jpeg[0]); Assert.Equal(0xD8, jpeg[1]);
        byte[] app1 =
        [
            0xFF, 0xE1, 0x00, 0x22,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, orientation, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    private static bool Reddish(SKColor c) => c.Red > 150 && c.Blue < 100;
    private static bool Bluish(SKColor c) => c.Blue > 150 && c.Red < 100;

    [Theory]
    [InlineData(SKEncodedOrigin.RightTop, 1, 2, "R", "B")]     // 6: หมุนตามเข็ม 90° — แถว [R B] กลายเป็นคอลัมน์ R บน B ล่าง
    [InlineData(SKEncodedOrigin.LeftBottom, 1, 2, "B", "R")]   // 8: หมุนทวนเข็ม 90°
    [InlineData(SKEncodedOrigin.TopRight, 2, 1, "B", "R")]     // 2: กลับซ้าย-ขวา
    [InlineData(SKEncodedOrigin.BottomRight, 2, 1, "B", "R")]  // 3: หมุน 180°
    [InlineData(SKEncodedOrigin.TopLeft, 2, 1, "R", "B")]      // 1: ไม่หมุน
    public void ApplyOrigin_moves_pixels_like_exif_says(SKEncodedOrigin origin, int w, int h, string first, string second)
    {
        using var src = LeftRedRightBlue(2, 1);
        using var dst = SkiaImageOps.ApplyOrigin(src, origin);
        Assert.Equal((w, h), (dst.Width, dst.Height));
        var a = dst.GetPixel(0, 0);
        var b = w == 2 ? dst.GetPixel(1, 0) : dst.GetPixel(0, 1);
        Assert.Equal(first == "R", Reddish(a));
        Assert.Equal(second == "B", Bluish(b));
    }

    [Fact]
    public void DecodeUpright_applies_exif_orientation_6_and_leaves_untagged_alone()
    {
        using var src = LeftRedRightBlue(16, 8);
        var plain = SkiaImageOps.EncodeJpeg(src, 100);
        var tagged = WithExifOrientation(plain, 6);

        using var upright = SkiaImageOps.DecodeUpright(tagged)!;
        Assert.Equal(6, upright.ExifOrientation);
        Assert.Equal((8, 16), (upright.Bitmap.Width, upright.Bitmap.Height));
        Assert.True(Reddish(upright.Bitmap.GetPixel(4, 2)), "แดงต้องขึ้นบนหลังหมุน 90° ตามเข็ม");
        Assert.True(Bluish(upright.Bitmap.GetPixel(4, 13)), "น้ำเงินต้องลงล่าง");

        // ทิศตรงข้าม: ไม่มี EXIF → ไม่หมุน
        using var asIs = SkiaImageOps.DecodeUpright(plain)!;
        Assert.Equal(1, asIs.ExifOrientation);
        Assert.Equal((16, 8), (asIs.Bitmap.Width, asIs.Bitmap.Height));
        Assert.True(Reddish(asIs.Bitmap.GetPixel(2, 4)));
    }

    [Fact]
    public void DecodeUpright_returns_null_for_garbage()
    {
        Assert.Null(SkiaImageOps.DecodeUpright("ไม่ใช่รูป"u8.ToArray()));
    }

    [Fact]
    public void Grayscale_matrix_is_bt709()
    {
        using var src = Make(2, 2, (_, _) => new SKColor(200, 100, 50));
        using var gray = SkiaImageOps.ApplyColorMatrix(src, SkiaImageOps.GrayscaleBt709Matrix());
        var c = gray.GetPixel(1, 1);
        // 0.2126·200 + 0.7152·100 + 0.0722·50 = 117.65
        Assert.InRange((int)c.Red, 117, 118);
        Assert.Equal(c.Red, c.Green);
        Assert.Equal(c.Red, c.Blue);
        Assert.Equal(255, c.Alpha);
    }

    [Theory]
    [InlineData(200, 211)]   // 200·1.15 − 19.125 = 210.875
    [InlineData(128, 128)]   // กึ่งกลางคงที่
    [InlineData(10, 0)]      // ติดพื้น 0
    [InlineData(250, 255)]   // ติดเพดาน 255
    public void Contrast_matrix_matches_imagesharp_formula(byte input, byte expected)
    {
        using var src = Make(1, 1, (_, _) => new SKColor(input, input, input));
        using var dst = SkiaImageOps.ApplyColorMatrix(src, SkiaImageOps.ContrastMatrix(ImagePreprocessor.ContrastAmount));
        Assert.InRange((int)dst.GetPixel(0, 0).Red, expected - 1, expected + 1);
    }

    [Fact]
    public void ResizeMax_shrinks_long_edge_keeps_aspect_and_never_upscales()
    {
        using var big = Make(3000, 1500, (_, _) => SKColors.White);
        using var shrunk = SkiaImageOps.ResizeMax(big, 1200);
        Assert.Equal((1200, 600), (shrunk.Width, shrunk.Height));

        using var small = Make(800, 600, (_, _) => SKColors.White);
        using var same = SkiaImageOps.ResizeMax(small, 1200);
        Assert.Equal((800, 600), (same.Width, same.Height));
    }

    [Fact]
    public void CropToSquare_scales_short_side_then_center_crops()
    {
        using var src = LeftRedRightBlue(400, 200);
        using var sq = SkiaImageOps.CropToSquare(src, 100);
        Assert.Equal((100, 100), (sq.Width, sq.Height));
        Assert.True(Reddish(sq.GetPixel(25, 50)), "ครึ่งซ้ายของภาพกลางต้องยังเป็นแดง");
        Assert.True(Bluish(sq.GetPixel(75, 50)), "ครึ่งขวาต้องเป็นน้ำเงิน");
    }

    [Fact]
    public void HasTransparency_samples_corners_and_center()
    {
        using var opaque = Make(10, 10, (_, _) => SKColors.White);
        Assert.False(SkiaImageOps.HasTransparency(opaque));
        using var holed = Make(10, 10, (x, y) => x == 0 && y == 0 ? SKColors.Transparent : SKColors.White);
        Assert.True(SkiaImageOps.HasTransparency(holed));
    }

    [Fact]
    public void Reencoding_strips_exif_segment()
    {
        using var src = LeftRedRightBlue(16, 8);
        var tagged = WithExifOrientation(SkiaImageOps.EncodeJpeg(src, 90), 6);
        Assert.True(ContainsSeq(tagged, "Exif\0\0"u8), "ภาพตั้งต้นต้องมี APP1 EXIF");
        using var decoded = SkiaImageOps.DecodeUpright(tagged)!;
        var clean = SkiaImageOps.EncodeJpeg(decoded.Bitmap, 90);
        Assert.False(ContainsSeq(clean, "Exif\0\0"u8), "ภาพที่ encode ใหม่ต้องไม่พก EXIF");
    }

    // ── ImagePreprocessor (OCR) — กติกาเดิมทุกข้อ ──────────────────────────────

    [Theory]
    [InlineData(600, 400, 3, 1800, 1200)]
    [InlineData(1000, 900, 2, 2000, 1800)]
    [InlineData(1300, 1300, 1, 1300, 1300)]
    public void ImagePreprocessor_upscale_rules_3x_2x_none(int w, int h, int factor, int outW, int outH)
    {
        using var src = Make(w, h, (_, _) => SKColors.White);
        var r = ImagePreprocessor.Process(SkiaImageOps.EncodeJpeg(src, 90), "image/jpeg", "t.jpg");
        Assert.Equal("image/jpeg", r.ContentType);
        Assert.Contains("grayscale", r.StepsApplied);
        Assert.Contains("contrast×1.15", r.StepsApplied);
        if (factor > 1)
            Assert.Contains($"upscale {factor}× ({w}×{h} → {outW}×{outH})", r.StepsApplied);
        else
            Assert.DoesNotContain(r.StepsApplied, s => s.StartsWith("upscale", StringComparison.Ordinal));
        using var outBmp = SKBitmap.Decode(r.Bytes);
        Assert.Equal((outW, outH), (outBmp.Width, outBmp.Height));
        Assert.DoesNotContain(r.StepsApplied, s => s.StartsWith("auto-rotate", StringComparison.Ordinal));
    }

    [Fact]
    public void ImagePreprocessor_bakes_exif_rotation_into_pixels()
    {
        using var src = LeftRedRightBlue(1300, 1250);
        var tagged = WithExifOrientation(SkiaImageOps.EncodeJpeg(src, 90), 6);
        var r = ImagePreprocessor.Process(tagged, "image/jpeg", "phone.jpg");
        Assert.Contains("auto-rotate (EXIF orientation=6)", r.StepsApplied);
        using var outBmp = SKBitmap.Decode(r.Bytes);
        Assert.Equal((1250, 1300), (outBmp.Width, outBmp.Height));
    }

    [Fact]
    public void ImagePreprocessor_returns_input_untouched_for_non_image_or_undecodable()
    {
        var pdf = "%PDF-1.4 ..."u8.ToArray();
        var r1 = ImagePreprocessor.Process(pdf, "application/pdf", "a.pdf");
        Assert.Same(pdf, r1.Bytes); Assert.Equal("application/pdf", r1.ContentType); Assert.Empty(r1.StepsApplied);

        var junk = "ไม่ใช่รูปแต่บอกว่าเป็น"u8.ToArray();
        var r2 = ImagePreprocessor.Process(junk, "image/jpeg", "x.jpg");
        Assert.Same(junk, r2.Bytes); Assert.Equal("image/jpeg", r2.ContentType); Assert.Empty(r2.StepsApplied);
    }

    private static bool ContainsSeq(byte[] hay, ReadOnlySpan<byte> needle) => hay.AsSpan().IndexOf(needle) >= 0;
}
