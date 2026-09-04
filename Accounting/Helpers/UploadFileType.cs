namespace Accounting.Helpers;

/// <summary>ไฟล์ที่อัปโหลดมาไม่อยู่ใน allow-list (ตัดสินจากไบต์จริง) —
/// เป็น <see cref="BusinessRuleException"/> จึงถูก <c>ExceptionMiddleware</c>
/// แปลงเป็น HTTP 400 พร้อมข้อความไทยถึงผู้ใช้อยู่แล้ว ไม่ต้อง catch รายจุด</summary>
public sealed class UnsupportedUploadException : BusinessRuleException
{
    public UnsupportedUploadException(string message)
        : base(message, ruleCode: "UPLOAD-TYPE-NOT-ALLOWED") { }
}

/// <summary>ชนิดไฟล์อัปโหลดที่ระบบยอมรับ — สรุปจาก **ไบต์จริง** ไม่ใช่คำบอกเล่าของ client</summary>
/// <param name="Extension">นามสกุลที่ระบบจะใช้เซฟ (มีจุดนำหน้า) — ห้ามใช้ของ client</param>
/// <param name="ContentType">MIME ที่แท้จริง</param>
/// <param name="IsImage">รูปที่ตัวประมวลผลภาพย่อ/แปลงได้</param>
public readonly record struct UploadFileKind(string Extension, string ContentType, bool IsImage);

/// <summary>
/// **ตัดสินชนิดไฟล์จาก magic bytes — ตัวเดียวของระบบ**
///
/// ═══ ที่มา (บั๊กจริง · ผลตรวจ F-03) ═══
/// เส้นอัปโหลดสื่อ CMS เชื่อ <c>Content-Type</c> ที่ client ส่งมา: ถ้าไม่ใช่ชนิดที่
/// ประมวลผลได้ ก็ <b>เซฟไฟล์ดิบด้วยนามสกุลจากชื่อไฟล์ของ client</b> ลง
/// <c>uploads/cms/{companyId}/{siteId}/</c> ซึ่งอยู่ใน <c>publicUploadPrefixes</c>
/// ⇒ สมาชิกของผู้เช่ารายใดก็ได้อัปโหลด <c>x.html</c> / <c>x.js</c> / <c>x.svg</c>
/// แล้วได้ URL ที่ <b>origin เดียวกับแอป</b> ⇒ stored XSS ที่ผ่าน CSP
/// <c>script-src 'self'</c> ได้อย่างสมบูรณ์ และ JWT อยู่ใน localStorage
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item><b>ไบต์เป็นตัวตัดสิน</b> — <c>Content-Type</c> และนามสกุลจาก client เป็น
///   ข้อความที่ผู้โจมตีพิมพ์เอง ใช้เป็นหลักฐานไม่ได้</item>
/// <item><b>allow-list ไม่ใช่ deny-list</b> — ชนิดที่ไม่รู้จัก = ปฏิเสธ
///   (deny-list ต้องเดาให้ครบทุกสิ่งที่อันตราย ซึ่งเป็นไปไม่ได้)</item>
/// <item><b>ไม่มี SVG</b> — SVG คือ XML ที่ฝัง <c>&lt;script&gt;</c> ได้ ⇒ เป็น
///   HTML ที่ปลอมเป็นรูป. ถ้าวันหนึ่งต้องรองรับจริง ต้อง sanitize แล้วเซฟเป็น
///   ไฟล์ที่ผ่านตัว sanitizer เท่านั้น — ห้ามเปิดกลับมาที่นี่เฉย ๆ</item>
/// </list>
/// </summary>
public static class UploadFileType
{
    /// <summary>จำนวนไบต์หัวไฟล์ที่ต้องอ่านเพื่อสรุปชนิด</summary>
    public const int HeaderBytes = 32;

    /// <summary>สรุปชนิดจากไบต์หัวไฟล์ — คืน <c>null</c> = ไม่อยู่ใน allow-list</summary>
    public static UploadFileKind? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4) return null;

        // JPEG — FF D8 FF
        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return new(".jpg", "image/jpeg", true);

        // PNG — 89 50 4E 47 0D 0A 1A 0A
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
            return new(".png", "image/png", true);

        // GIF — "GIF87a" / "GIF89a"
        if (head.Length >= 6 && head[0] == 'G' && head[1] == 'I' && head[2] == 'F' && head[3] == '8')
            return new(".gif", "image/gif", true);

        // WEBP — "RIFF" .... "WEBP"
        if (head.Length >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
            && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
            return new(".webp", "image/webp", true);

        // BMP — "BM"
        if (head[0] == 'B' && head[1] == 'M')
            return new(".bmp", "image/bmp", true);

        // TIFF — "II*\0" (little-endian) หรือ "MM\0*" (big-endian)
        if ((head[0] == 'I' && head[1] == 'I' && head[2] == 0x2A && head[3] == 0x00)
            || (head[0] == 'M' && head[1] == 'M' && head[2] == 0x00 && head[3] == 0x2A))
            return new(".tif", "image/tiff", true);

        // PDF — "%PDF"
        if (head[0] == '%' && head[1] == 'P' && head[2] == 'D' && head[3] == 'F')
            return new(".pdf", "application/pdf", false);

        // ICO — 00 00 01 00 (ไอคอนของแพลตฟอร์ม)
        if (head[0] == 0x00 && head[1] == 0x00 && head[2] == 0x01 && head[3] == 0x00)
            return new(".ico", "image/x-icon", false);

        return null;
    }

    /// <summary>อ่านหัวไฟล์แล้วสรุปชนิด · คืน stream กลับไปที่ตำแหน่งเดิมให้ผู้เรียก
    /// เขียนต่อได้ (ต้องเป็น stream ที่ seek ได้ — ผู้เรียก buffer มาก่อน)</summary>
    public static async Task<UploadFileKind?> SniffAsync(Stream seekable, CancellationToken ct = default)
    {
        if (!seekable.CanSeek) return null;
        var start = seekable.Position;
        var buf = new byte[HeaderBytes];
        var read = await seekable.ReadAsync(buf.AsMemory(0, HeaderBytes), ct);
        seekable.Position = start;
        return Sniff(buf.AsSpan(0, read));
    }

    /// <summary>MIME ของนามสกุลที่ระบบเซฟไว้ — ใช้ตอนบันทึกลง DB/ตอบ header
    /// เพื่อไม่ต้องเก็บค่าที่ client บอกมา (ซึ่งอาจโกหก)</summary>
    public static string ContentTypeForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".pdf" => "application/pdf",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };

    /// <summary>ข้อความปฏิเสธมาตรฐาน — บอกสิ่งที่รับได้ ไม่ใช่แค่ "ไฟล์ไม่ถูกต้อง"</summary>
    public const string RejectMessage =
        "ไฟล์นี้ไม่ใช่รูปภาพหรือ PDF ที่ระบบรองรับ (ตรวจจากเนื้อไฟล์จริง ไม่ใช่นามสกุล) — " +
        "รองรับ JPG · PNG · GIF · WebP · BMP · TIFF · PDF";
}
