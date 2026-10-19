using SkiaSharp;

namespace Accounting.Helpers;

/// <summary>
/// OWNER: งานพิกเซลทุกอย่างในระบบ (decode ตาม EXIF · grayscale/contrast · ย่อ-ขยาย · crop สี่เหลี่ยม · encode)
/// รวมที่นี่ที่เดียวบน SkiaSharp — แทน SixLabors.ImageSharp 3.1.x ที่มี advisory เปิดอยู่ 5 รายการ
/// (GHSA-gwg2-r3hj-4w44 · GHSA-j3p4-wp97-rph4 · GHSA-j9gm-c75j-xc9q · GHSA-jjfr-hcj7-qf5w · GHSA-wmxv-xphr-5c9g)
/// และรุ่น 4.x ต้องซื้อ license · SkiaSharp อยู่ในระบบอยู่แล้วผ่าน PDFtoImage (เรนเดอร์ PDF ให้ Tesseract)
/// จึงไม่เพิ่ม native dependency ใหม่ · คำตัดสินเจ้าของ 2026-10-19 ("2")
///
/// สูตรทุกตัวตั้งใจให้ตรงกับของเดิมใน ImageSharp (ผู้เรียก OCR อยู่ใต้กฎเหล็ก #4 §H "ห้ามแย่ลง"):
///  • grayscale = Bt.709 (0.2126 R + 0.7152 G + 0.0722 B) เท่า <c>Grayscale()</c> เดิม
///  • contrast(a) = a·c + (1−a)/2 รอบกึ่งกลาง เท่า <c>Contrast(a)</c> เดิม (ImageSharp ใช้ color-matrix ใน sRGB เช่นกัน)
///  • "Bicubic"/"Lanczos3" เดิม → Catmull-Rom (B=0,C=½) ซึ่งเป็น cubic ที่ ImageSharp ใช้ชื่อ Bicubic อยู่แล้ว
///    (Skia ไม่มี Lanczos — Catmull-Rom คมที่สุดใน cubic family · ผลบนกระดาษจริงดู CHANGELOG 2026-10-19)
/// ผู้เรียก: ImagePreprocessor (OCR ก่อน engine) · EmbeddedTesseractOcrService (ก่อน Tesseract) ·
/// ImageProcessingService (รูปอัปโหลดทุกชนิด) · AdminController.TestAzureDiFull (ภาพสังเคราะห์)
/// </summary>
public static class SkiaImageOps
{
    /// <summary>ภาพที่ decode แล้วและหมุนตาม EXIF ให้ตั้งตรง · <see cref="ExifOrientation"/> = ค่า 1–8 ที่อ่านได้ (1 = ไม่ต้องหมุน)</summary>
    public sealed record Decoded(SKBitmap Bitmap, int ExifOrientation) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    /// <summary>cubic ที่ใช้แทน Bicubic/Lanczos3 ของเดิมทุกจุด — ตัวตั้งตัวเดียว ห้ามเลือกเองที่ผู้เรียก</summary>
    public static readonly SKSamplingOptions HighQuality = new(SKCubicResampler.CatmullRom);

    /// <summary>
    /// decode + หมุนตาม EXIF orientation ให้พิกเซลตั้งตรง (Skia ไม่หมุนให้เอง — ต่างจาก ImageSharp ที่เก็บ flag ไว้ใน metadata)
    /// คืน null เมื่อไบต์ไม่ใช่รูปที่ Skia อ่านได้ (เช่น TIFF/HEIC) — ผู้เรียกตัดสินเองว่าจะส่งต่อดิบหรือปฏิเสธ
    /// </summary>
    public static Decoded? DecodeUpright(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        if (codec == null) return null;
        var origin = codec.EncodedOrigin;
        var raw = SKBitmap.Decode(codec);
        if (raw == null) return null;
        if (origin == SKEncodedOrigin.TopLeft) return new Decoded(raw, 1);
        using (raw)
            return new Decoded(ApplyOrigin(raw, origin), (int)origin);
    }

    /// <summary>
    /// map EXIF orientation 1–8 → เมทริกซ์ที่ส่งพิกเซลต้นทาง (x,y) ไปตำแหน่งตั้งตรง · 5–8 สลับกว้าง/สูง
    /// (เลข = EXIF Tag 0x0112 · 6 = หมุนตามเข็ม 90° คือเคสกล้องมือถือถ่ายแนวตั้งที่พบบ่อยสุด)
    /// </summary>
    internal static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight     => new SKMatrix(-1, 0, w,  0, 1, 0,  0, 0, 1),
        SKEncodedOrigin.BottomRight  => new SKMatrix(-1, 0, w,  0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft   => new SKMatrix(1, 0, 0,   0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop      => new SKMatrix(0, 1, 0,   1, 0, 0,  0, 0, 1),
        SKEncodedOrigin.RightTop     => new SKMatrix(0, -1, h,  1, 0, 0,  0, 0, 1),
        SKEncodedOrigin.RightBottom  => new SKMatrix(0, -1, h, -1, 0, w,  0, 0, 1),
        SKEncodedOrigin.LeftBottom   => new SKMatrix(0, 1, 0,  -1, 0, w,  0, 0, 1),
        _ => SKMatrix.CreateIdentity(),
    };

    internal static bool SwapsAxes(SKEncodedOrigin origin) => (int)origin >= 5;

    internal static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return src.Copy();
        var (ow, oh) = SwapsAxes(origin) ? (src.Height, src.Width) : (src.Width, src.Height);
        var dst = new SKBitmap(new SKImageInfo(ow, oh, src.ColorType, src.AlphaType, src.ColorSpace));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(OrientationMatrix(origin, src.Width, src.Height));
        // หมุนเป็นมุมฉากล้วน — ไม่ต้อง resample (Nearest) พิกเซลตรงตัวเดิมทุกจุด
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    /// <summary>Bt.709 luma — ตรงกับ ImageSharp <c>Grayscale()</c> (ค่าปริยาย GrayscaleMode.Bt709)</summary>
    public static float[] GrayscaleBt709Matrix() =>
    [
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0,       0,       0,       1, 0,
    ];

    /// <summary>
    /// c' = amount·c + (1−amount)/2 — สูตรเดียวกับ ImageSharp <c>Contrast(amount)</c>
    /// (⚠️ SkiaSharp 3.x รับคอลัมน์เลื่อน (ที่ 5) ในสเกล 0–1 ไม่ใช่ 0–255 อย่างที่เอกสาร Skia เดิมบอก —
    /// วัดจริง 2026-10-19: ใส่ −19.125 แล้วทุกพิกเซลเป็น 0 · เทสต์ Contrast_matrix_matches_imagesharp_formula ล็อกไว้)
    /// </summary>
    public static float[] ContrastMatrix(float amount)
    {
        var shift = (1f - amount) / 2f;
        return
        [
            amount, 0, 0, 0, shift,
            0, amount, 0, 0, shift,
            0, 0, amount, 0, shift,
            0, 0, 0,      1, 0,
        ];
    }

    /// <summary>วาดภาพผ่าน color-matrix ลงบิตแมปใหม่ขนาดเดิม (ไม่แตะต้นฉบับ)</summary>
    public static SKBitmap ApplyColorMatrix(SKBitmap src, float[] matrix)
    {
        var dst = new SKBitmap(src.Info);
        using var canvas = new SKCanvas(dst);
        using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(matrix) };
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(src, 0, 0, paint);
        return dst;
    }

    /// <summary>ย่อ/ขยายเป็นขนาดที่ระบุตรง ๆ (ผู้เรียกคำนวณอัตราส่วนเอง) ด้วย <see cref="HighQuality"/></summary>
    public static SKBitmap Resize(SKBitmap src, int width, int height)
    {
        var dst = src.Resize(new SKImageInfo(Math.Max(1, width), Math.Max(1, height), src.ColorType, src.AlphaType, src.ColorSpace), HighQuality);
        return dst ?? throw new InvalidOperationException($"SkiaSharp resize {src.Width}×{src.Height} → {width}×{height} ล้มเหลว");
    }

    /// <summary>
    /// ด้านยาวไม่เกิน <paramref name="maxLongEdge"/> คงอัตราส่วน — เท่า ImageSharp <c>ResizeMode.Max</c>
    /// ถ้าเล็กกว่าอยู่แล้วคืนสำเนาเดิม (ไม่ขยาย)
    /// </summary>
    public static SKBitmap ResizeMax(SKBitmap src, int maxLongEdge)
    {
        var longEdge = Math.Max(src.Width, src.Height);
        if (longEdge <= maxLongEdge) return src.Copy();
        var scale = (double)maxLongEdge / longEdge;
        return Resize(src, (int)Math.Round(src.Width * scale), (int)Math.Round(src.Height * scale));
    }

    /// <summary>
    /// ย่อให้ด้านสั้นเท่า <paramref name="size"/> แล้วตัดกลางเป็นสี่เหลี่ยมจัตุรัส — เท่า ImageSharp <c>ResizeMode.Crop</c> + Size(n,n)
    /// (ภาพเล็กกว่า n จะถูกขยายขึ้นเหมือนของเดิม)
    /// </summary>
    public static SKBitmap CropToSquare(SKBitmap src, int size)
    {
        var scale = (double)size / Math.Min(src.Width, src.Height);
        var scaledW = (float)(src.Width * scale);
        var scaledH = (float)(src.Height * scale);
        var dst = new SKBitmap(new SKImageInfo(size, size, src.ColorType, src.AlphaType, src.ColorSpace));
        using var canvas = new SKCanvas(dst);
        using var image = SKImage.FromBitmap(src);
        canvas.Clear(SKColors.Transparent);
        var left = (size - scaledW) / 2f;
        var top = (size - scaledH) / 2f;
        canvas.DrawImage(image, new SKRect(0, 0, src.Width, src.Height),
            new SKRect(left, top, left + scaledW, top + scaledH), HighQuality);
        return dst;
    }

    /// <summary>สุ่มมุม 4 + กึ่งกลางด้าน 4 + กลางภาพ — ถ้าพบ alpha &lt; 255 ถือว่ามีความโปร่งใส (heuristic เดิมของ ImageProcessingService)</summary>
    public static bool HasTransparency(SKBitmap bmp)
    {
        if (bmp.AlphaType == SKAlphaType.Opaque) return false;
        var xs = new[] { 0, bmp.Width / 2, bmp.Width - 1 };
        var ys = new[] { 0, bmp.Height / 2, bmp.Height - 1 };
        foreach (var x in xs)
            foreach (var y in ys)
                if (bmp.GetPixel(x, y).Alpha < 255) return true;
        return false;
    }

    /// <summary>JPEG ไม่พก EXIF/IPTC/XMP ของต้นฉบับ (Skia เขียนแต่พิกเซล) — จึงเป็นการ strip metadata ไปในตัว</summary>
    public static byte[] EncodeJpeg(SKBitmap bmp, int quality)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality)
            ?? throw new InvalidOperationException("SkiaSharp เข้ารหัส JPEG ไม่สำเร็จ");
        return data.ToArray();
    }

    public static byte[] EncodePng(SKBitmap bmp)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp เข้ารหัส PNG ไม่สำเร็จ");
        return data.ToArray();
    }
}
