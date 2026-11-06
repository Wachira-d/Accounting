using System.Buffers.Binary;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 246 QR — <b>ตัวตรวจรูป QR ชำระเงินที่ผู้ใช้อัปโหลดให้บัญชีธนาคาร — ตัวเดียวของทางเข้าอัปโหลด</b> (คำตัดสิน 246-QR-6)
///
/// <para>กติกา: (1) ไม่ว่าง · ≤ <see cref="MaxBytes"/> (1 MB) (2) ชนิดจากไบต์จริงเท่านั้น (<see cref="ImageMagicBytes"/>) — PNG หรือ JPEG ·
/// Content-Type/นามสกุลที่ client ส่งไม่ถูกเชื่อ (ไฟล์ HTML ที่ตั้งชื่อ .png = ปฏิเสธ) (3) อ่านกว้าง/สูงจากหัวไฟล์ (PNG: IHDR ต้องเป็น chunk แรก ·
/// JPEG: ส่วน SOF) อยู่ใน <see cref="MinSide"/>..<see cref="MaxSide"/> และสัดส่วนไม่เกิน <see cref="MaxAspect"/> (ภาพหน้าจอแอปธนาคารแนวตั้งผ่าน ·
/// แถบยาวผิดรูปไม่ผ่าน) · จำนวนพิกเซลไม่เกิน <see cref="MaxPixels"/> (กันระเบิดหน่วยความจำตอนถอดรูป)</para>
///
/// <para>ผ่านแล้ว ผู้เรียก (<c>PaymentQrService</c>) ยังถอดรูปจริงแล้วเข้ารหัส PNG ใหม่ (ตัด EXIF/ข้อมูลแฝง · ไฟล์ polyglot ไม่รอด) — เก็บ/พิมพ์เฉพาะไบต์ที่เข้ารหัสใหม่</para>
/// </summary>
public static class PaymentQrImageValidator
{
    public const int MaxBytes = 1024 * 1024;
    public const int MinSide = 120;
    public const int MaxSide = 4096;
    public const double MaxAspect = 3.0;
    public const long MaxPixels = 12_000_000;

    public enum Code { Ok, Empty, TooLarge, NotImage, BadStructure, BadDimensions }

    public sealed record Result(Code Code, string? Message, string? ContentType, int Width, int Height)
    {
        public bool Ok => Code == Code.Ok;
    }

    private static Result Fail(Code c, string m) => new(c, m, null, 0, 0);

    public static Result Validate(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return Fail(Code.Empty, "กรุณาเลือกไฟล์รูป QR");
        if (bytes.Length > MaxBytes) return Fail(Code.TooLarge, $"รูป QR ใหญ่เกิน {MaxBytes / 1024 / 1024} MB — ลดขนาดหรือครอปเฉพาะส่วน QR");
        var type = ImageMagicBytes.Detect(bytes);
        if (type is not ("image/png" or "image/jpeg"))
            return Fail(Code.NotImage, "รองรับเฉพาะรูป PNG หรือ JPEG (ตรวจจากเนื้อไฟล์ — เปลี่ยนนามสกุลไม่ช่วย)");
        var dims = type == "image/png" ? PngSize(bytes) : JpegSize(bytes);
        if (dims is not (int w, int h)) return Fail(Code.BadStructure, "อ่านขนาดรูปไม่ได้ — ไฟล์เสียหรือไม่ใช่รูปจริง");
        if (w < MinSide || h < MinSide || w > MaxSide || h > MaxSide || (long)w * h > MaxPixels)
            return new(Code.BadDimensions, $"ขนาดรูปต้องอยู่ระหว่าง {MinSide}–{MaxSide} พิกเซลต่อด้าน (รูปนี้ {w}×{h})", type, w, h);
        var aspect = (double)Math.Max(w, h) / Math.Min(w, h);
        if (aspect > MaxAspect)
            return new(Code.BadDimensions, $"สัดส่วนรูปยาวเกินไป ({w}×{h}) — ครอปให้เหลือส่วน QR", type, w, h);
        return new(Code.Ok, null, type, w, h);
    }

    /// <summary>PNG: signature 8 ไบต์ → chunk แรกต้องเป็น IHDR ยาว 13 · กว้าง/สูง big-endian</summary>
    internal static (int, int)? PngSize(ReadOnlySpan<byte> b)
    {
        if (b.Length < 24) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(b.Slice(8, 4)) != 13) return null;
        if (b[12] != (byte)'I' || b[13] != (byte)'H' || b[14] != (byte)'D' || b[15] != (byte)'R') return null;
        var w = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(16, 4));
        var h = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(20, 4));
        const uint max = int.MaxValue;
        if (w == 0 || h == 0 || w > max || h > max) return null;
        return ((int)w, (int)h);
    }

    /// <summary>JPEG: เดินส่วน (segment) จาก SOI จนเจอ SOFn (C0–CF ยกเว้น C4/C8/CC) — ความยาวส่วนผิด/หมดไฟล์ก่อนเจอ = null</summary>
    internal static (int, int)? JpegSize(ReadOnlySpan<byte> b)
    {
        var i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF) return null;
            var m = b[i + 1];
            if (m == 0xFF) { i++; continue; }                      // fill byte
            if (m is 0xD8 or 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }   // ไม่มีความยาว
            if (m is 0xD9 or 0xDA) return null;                     // จบ/เริ่มข้อมูลภาพก่อนเจอ SOF
            var len = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 2, 2));
            if (len < 2) return null;
            var isSof = m >= 0xC0 && m <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC);
            if (isSof)
            {
                if (i + 9 > b.Length || len < 7) return null;
                int h = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 5, 2));
                int w = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 7, 2));
                return w == 0 || h == 0 ? null : (w, h);
            }
            i += 2 + len;
        }
        return null;
    }
}
