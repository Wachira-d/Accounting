namespace Accounting.Helpers;

/// <summary>
/// **ชนิดรูปจากไบต์จริงของไฟล์** (ไม่เชื่อ <c>Content-Type</c>/นามสกุลที่ client ส่งมา) — ทีม K ฝ่ายค้านข้อ 4:
/// ลูกค้าหน้าร้าน (ไม่ได้ล็อกอิน) แนบรูปตัวอย่างของสินค้าตามสั่ง · ไฟล์ HTML/สคริปต์ที่ตั้ง Content-Type เป็น image/png ต้องถูกปฏิเสธ
/// <para>รองรับ JPEG (FF D8 FF) · PNG (89 50 4E 47 0D 0A 1A 0A) · WEBP (RIFF....WEBP) · อย่างอื่น = null</para>
/// </summary>
public static class ImageMagicBytes
{
    /// <summary>content-type ที่ไบต์บอก ("image/jpeg" / "image/png" / "image/webp") · ไม่รู้จัก/สั้นเกิน = null</summary>
    public static string? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "image/jpeg";
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A) return "image/png";
        if (head.Length >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
            && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P') return "image/webp";
        return null;
    }
}
