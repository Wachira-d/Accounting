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
        // ชนิดของไฟล์แนบเอกสาร (SniffAttachment) — ไม่กระทบเส้นสื่อ CMS/โลโก้
        // เพราะเส้นนั้นได้นามสกุลจาก Sniff() ซึ่งไม่เคยคืนชนิดเหล่านี้
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".doc" => "application/msword",
        ".xls" => "application/vnd.ms-excel",
        ".zip" => "application/zip",
        ".rar" => "application/vnd.rar",
        ".7z" => "application/x-7z-compressed",
        ".csv" => "text/csv",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// **allow-list ของ "ไฟล์แนบเอกสาร"** (ใบเสร็จ/สัญญา/สลิป ที่แนบกับเอกสาร ผู้ติดต่อ ฯลฯ)
    /// — กว้างกว่า <see cref="Sniff"/> เพราะหลักฐานประกอบรายการบัญชีมักเป็น Excel/Word/ZIP
    ///
    /// <para>═══ ที่มา (รอบ 190 ข้อ 7) ═══ <c>FileAttachmentController.Upload</c> เชื่อ
    /// <c>Content-Type</c> และนามสกุลที่ client บอก (allow-list นามสกุลอย่างเดียว) ⇒ ไฟล์
    /// HTML ที่ตั้งชื่อ <c>x.csv</c> หรือ Content-Type ปลอมผ่านได้ และค่า Content-Type ปลอม
    /// ถูกเก็บแล้วตอบกลับตอนดาวน์โหลด</para>
    ///
    /// <para>กติกาเดียวกับ <see cref="Sniff"/>: <b>ไบต์ตัดสิน</b> · ชื่อไฟล์ของ client ใช้
    /// ได้แค่ "แยกชนิดย่อย" ในกรณีที่ไบต์ยืนยันตระกูลแล้ว (ZIP → docx/xlsx · OLE2 → doc/xls)
    /// และข้อความล้วน (csv/txt) ต้องไม่มีไบต์ 0 และไม่ขึ้นต้นด้วย <c>&lt;</c> (กัน HTML/SVG/XML
    /// ที่ปลอมเป็น CSV) · ICO ไม่ใช่หลักฐานบัญชี จึงไม่รับ</para>
    /// </summary>
    /// <returns><c>null</c> = ไม่อยู่ใน allow-list — ผู้เรียกต้องปฏิเสธด้วย <see cref="AttachmentRejectMessage"/></returns>
    public static UploadFileKind? SniffAttachment(ReadOnlySpan<byte> head, string? clientFileName)
    {
        var ext = (Path.GetExtension(clientFileName ?? "") ?? "").ToLowerInvariant();

        if (Sniff(head) is UploadFileKind k)
            return k.Extension == ".ico" ? (UploadFileKind?)null : k;

        // ZIP — "PK\x03\x04" (docx/xlsx คือ ZIP ที่มีโครง OOXML ข้างใน)
        if (head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 0x03 && head[3] == 0x04)
            return ext switch
            {
                ".docx" => new UploadFileKind(".docx", ContentTypeForExtension(".docx"), false),
                ".xlsx" => new UploadFileKind(".xlsx", ContentTypeForExtension(".xlsx"), false),
                _ => new UploadFileKind(".zip", "application/zip", false),
            };

        // OLE2 Compound File — D0 CF 11 E0 A1 B1 1A E1 (Word/Excel รุ่นเก่า)
        if (head.Length >= 8 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0
            && head[4] == 0xA1 && head[5] == 0xB1 && head[6] == 0x1A && head[7] == 0xE1)
            return ext switch
            {
                ".doc" => new UploadFileKind(".doc", ContentTypeForExtension(".doc"), false),
                ".xls" => new UploadFileKind(".xls", ContentTypeForExtension(".xls"), false),
                _ => (UploadFileKind?)null,   // OLE2 อื่น (msi/msg ฯลฯ) ไม่ใช่เอกสารที่รองรับ
            };

        // RAR — "Rar!\x1A\x07"
        if (head.Length >= 6 && head[0] == 'R' && head[1] == 'a' && head[2] == 'r' && head[3] == '!'
            && head[4] == 0x1A && head[5] == 0x07)
            return new UploadFileKind(".rar", "application/vnd.rar", false);

        // 7z — 37 7A BC AF 27 1C
        if (head.Length >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF
            && head[4] == 0x27 && head[5] == 0x1C)
            return new UploadFileKind(".7z", "application/x-7z-compressed", false);

        // ข้อความล้วน — ไม่มี magic bytes จึงรับเฉพาะเมื่อชื่อบอกว่าเป็น csv/txt **และ** ไบต์ดูเป็นข้อความ
        if ((ext == ".csv" || ext == ".txt") && LooksLikePlainText(head))
            return new UploadFileKind(ext, ContentTypeForExtension(ext), false);

        return null;
    }

    /// <summary>ไบต์หัวไฟล์ดูเป็นข้อความธรรมดา (ไม่ใช่ไบนารี · ไม่ใช่ markup)</summary>
    private static bool LooksLikePlainText(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0) return false;
        var i = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) i = 3;   // UTF-8 BOM
        for (var j = i; j < head.Length; j++)
            if (head[j] == 0x00) return false;   // ไบนารี (หรือ UTF-16 ที่เราไม่รองรับ)
        while (i < head.Length && (head[i] == ' ' || head[i] == '\t' || head[i] == '\r' || head[i] == '\n')) i++;
        return i < head.Length && head[i] != '<';   // HTML/SVG/XML ที่ปลอมเป็น .csv/.txt
    }

    /// <summary>ข้อความปฏิเสธของไฟล์แนบ — บอกสิ่งที่รับได้</summary>
    public const string AttachmentRejectMessage =
        "แนบไฟล์นี้ไม่ได้ — ระบบตรวจจากเนื้อไฟล์จริง (ไม่ใช่นามสกุล) แล้วไม่ใช่ชนิดที่รองรับ · " +
        "รองรับ PDF · รูปภาพ (JPG PNG GIF WebP BMP TIFF) · Word (doc docx) · Excel (xls xlsx) · CSV · TXT · ZIP · RAR · 7z";

    /// <summary>ข้อความปฏิเสธมาตรฐาน — บอกสิ่งที่รับได้ ไม่ใช่แค่ "ไฟล์ไม่ถูกต้อง"</summary>
    public const string RejectMessage =
        "ไฟล์นี้ไม่ใช่รูปภาพหรือ PDF ที่ระบบรองรับ (ตรวจจากเนื้อไฟล์จริง ไม่ใช่นามสกุล) — " +
        "รองรับ JPG · PNG · GIF · WebP · BMP · TIFF · PDF";
}
