using System.Security.Cryptography;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §3.2 ช่อง ① · §6.5 · §9 เฟส 244b) — <b>ลิงก์อัปโหลดหนังสือรับรอง 50 ทวิ แบบไม่ต้องล็อกอิน: ตัวตั้งเดียว</b>
///
/// <para>ตอบทุกคำถามที่ฝั่งออกลิงก์ (หน้าเอกสาร/ทะเบียน · อีเมลใบเสร็จ · อีเมลทวง) และฝั่งรับไฟล์ (<c>WhtCertPublicController</c>) ต้องตอบเหมือนกัน:
/// token (ตัวสร้าง/hash/เทียบเวลาคงที่ <b>ตัวเดียวกับลิงก์ชำระ</b> — <see cref="DocumentPayLinkPolicy"/> · ฐานเก็บเฉพาะ hash) · อายุ · ใช้ได้ไหม ·
/// URL · ไฟล์ที่รับ (ไบต์ตัดสิน — <see cref="UploadFileType.Sniff"/> · PDF ที่มีสคริปต์/ไฟล์ฝัง = ปฏิเสธ) · ชื่อผู้ส่ง · เอกสารที่ผู้ส่งติ๊ก (กรองให้อยู่ในชุดของลิงก์)</para>
/// </summary>
public static class WhtCertUploadLinkPolicy
{
    public const string RuleCode = "WHT-UPLOAD-LINK";

    /// <summary>อายุลิงก์ (วัน) — AUTO_SEND §3.2 ค่าตั้งต้น 180 วัน · อีเมลทวงรอบใหม่ = ลิงก์ใหม่ (อายุนับใหม่)</summary>
    public const int TtlDays = 180;

    /// <summary>หน้าสาธารณะ (ผู้อ่าน: <see cref="Url"/>)</summary>
    public const string PagePath = "/wht-upload.html";

    /// <summary>ขนาดไฟล์สูงสุด (10 MB · §3.2)</summary>
    public const long MaxBytes = 10L * 1024 * 1024;

    /// <summary>เพดานต่อ IP (DB-backed <c>IChatRateLimiter</c> · กฎ #4 D ห้าม static dict)</summary>
    public const int UploadsPerMinute = 6;
    public const int UploadsPerDay = 60;
    /// <summary>เพดานต่อ token ต่อวัน — ลิงก์รั่วไปแล้วถูกยิงไฟล์ขยะไม่เกินนี้</summary>
    public const int UploadsPerTokenPerDay = 30;
    public const int ViewsPerMinute = 30;
    public const int ViewsPerDay = 600;

    public const int MaxSenderNameLength = 100;

    public const string NotFoundMessage = "ลิงก์ไม่ถูกต้อง — กรุณาติดต่อผู้ขายเพื่อขอลิงก์ใหม่";
    public const string UnusableMessage = "ลิงก์นี้หมดอายุหรือถูกยกเลิกแล้ว — กรุณาติดต่อผู้ขายเพื่อขอลิงก์ใหม่";
    public const string RateLimitedMessage = "ส่งไฟล์ถี่เกินไป — รอสักครู่แล้วลองใหม่";
    public const string RejectTypeMessage = "รับเฉพาะไฟล์ PDF, JPG หรือ PNG (ตรวจจากเนื้อไฟล์จริง) — ถ่ายรูปหนังสือรับรอง หรือบันทึกเป็น PDF แล้วลองใหม่";

    /// <summary>token ใหม่ (256 บิต CSPRNG) — ตัวเดียวกับลิงก์ชำระ</summary>
    public static string NewToken() => DocumentPayLinkPolicy.NewToken();

    /// <summary>hash ที่เก็บในฐาน (ฝั่งเขียน = ฝั่งค้น) — ฟังก์ชันเดียวกับลิงก์ชำระ (กฎ #4 C: hash มี canonical function เดียว)</summary>
    public static string TokenHash(string token) => DocumentPayLinkPolicy.TokenHash(token);

    /// <summary>รูป token ที่รับก่อนแตะฐาน (ไม่ผ่าน = "ไม่พบ" เหมือน token ผิด)</summary>
    public static bool LooksValid(string? token) => DocumentPayLinkPolicy.LooksValid(token);

    /// <summary>ยืนยัน token กับ hash ที่เก็บแบบเวลาคงที่</summary>
    public static bool TokenMatches(string? token, string? storedHash) => DocumentPayLinkPolicy.TokenMatches(token, storedHash);

    public static DateTime ExpiresAt(DateTime issuedUtc) => issuedUtc.AddDays(TtlDays);

    /// <summary>ฝ่ายค้าน WP-10 M3: กุญแจของ token ลิงก์อีเมลทวง — อนุพันธ์จากความลับของเซิร์ฟเวอร์ (JWT secret · fail-fast ใน production อยู่แล้ว) ด้วยป้ายของเรื่องนี้ ·
    /// ว่าง ⇒ โยน (กฎ #4 C ห้ามถอยไปกุญแจ dev เงียบ ๆ)</summary>
    public static byte[] DeriveQueueKey(string? serverSecret)
    {
        if (string.IsNullOrWhiteSpace(serverSecret))
            throw new InvalidOperationException("ไม่ได้ตั้งความลับของเซิร์ฟเวอร์ (Jwt:Secret) — ออกลิงก์อัปโหลดของอีเมลทวง 50 ทวิ ไม่ได้");
        return HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(serverSecret), System.Text.Encoding.UTF8.GetBytes("wht-upload-queue-token-v1"));
    }

    /// <summary>ฝ่ายค้าน WP-10 M3: token ของลิงก์ในอีเมลทวงหนึ่งฉบับ — HMAC(กุญแจ, บริษัท + แถวคิว) hex 64 ตัว · คำนวณซ้ำได้ ⇒ ส่งซ้ำ/ลองใหม่ได้ URL เดิม
    /// และผู้ส่งคิวไม่ต้องเขียนฐานกลางทางส่ง (แถวลิงก์สร้างพร้อมแถวคิว) · ฐานยังเก็บเฉพาะ <see cref="TokenHash"/></summary>
    public static string TokenForQueue(byte[] key, Guid companyId, Guid emailQueueId)
        => Convert.ToHexString(HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes($"whtq:{companyId:N}:{emailQueueId:N}"))).ToLowerInvariant();

    /// <summary>รอบ 259 (258-3-6): token ของลิงก์บนการ์ด LINE ทวงหนึ่งใบ — HMAC(กุญแจเดียวกัน, ป้าย "whtl" + บริษัท + แถวคิว LINE) ·
    /// คนละป้ายกับอีเมล (<see cref="TokenForQueue"/>) ⇒ id ของคิวสองตารางไม่มีทางได้ token เดียวกัน · ลองส่งใหม่ = URL เดิม</summary>
    public static string TokenForLineQueue(byte[] key, Guid companyId, Guid lineQueueId)
        => Convert.ToHexString(HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes($"whtl:{companyId:N}:{lineQueueId:N}"))).ToLowerInvariant();

    /// <summary>ลิงก์ใช้ได้ไหม (ตัดสินสดทุกครั้ง) — ไม่ถูกเพิกถอน · ยังไม่หมดอายุ</summary>
    public static bool Usable(DateTime expiresAtUtc, DateTime? revokedAtUtc, DateTime nowUtc)
        => revokedAtUtc == null && expiresAtUtc > nowUtc;

    /// <summary>URL สาธารณะ — origin จาก <c>SystemBaseUrl</c> (ไม่ประกอบโดเมนเอง)</summary>
    public static string Url(string origin, Guid companyId, string token)
        => $"{origin.TrimEnd('/')}{PagePath}?c={companyId:D}&t={token}";

    /// <summary>ผลตรวจไฟล์ — <paramref name="Kind"/> null = ปฏิเสธ (<paramref name="Problem"/> = ข้อความไทยถึงผู้ส่ง)</summary>
    public sealed record FileVerdict(UploadFileKind? Kind, string? Problem);

    /// <summary>
    /// ตรวจไฟล์ที่ลูกค้าส่ง — <b>ไบต์ตัดสิน</b> (ไม่เชื่อ Content-Type/นามสกุล): ว่าง/เกิน 10 MB ⇒ ปฏิเสธ · รับเฉพาะ PDF/JPEG/PNG ·
    /// PDF ที่มี <c>/JavaScript</c> · <c>/JS</c> · <c>/Launch</c> · <c>/EmbeddedFile</c> · <c>/RichMedia</c> ⇒ ปฏิเสธ (AUTO_SEND §3.2 —
    /// หนังสือรับรองไม่มีเหตุต้องมีสคริปต์/ไฟล์ฝัง · ไฟล์จะถูกเปิดโดยพนักงานบัญชี)
    /// </summary>
    public static FileVerdict Inspect(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0) return new FileVerdict(null, "ไม่พบไฟล์ — เลือกไฟล์หนังสือรับรองแล้วลองใหม่");
        if (content.Length > MaxBytes) return new FileVerdict(null, "ไฟล์ใหญ่เกิน 10 MB — ย่อรูปหรือบันทึกเป็น PDF ขนาดเล็กลงแล้วลองใหม่");
        var kind = UploadFileType.Sniff(content[..Math.Min(content.Length, UploadFileType.HeaderBytes)]);
        if (kind is not { } k || k.ContentType is not ("application/pdf" or "image/jpeg" or "image/png"))
            return new FileVerdict(null, RejectTypeMessage);
        if (k.ContentType == "application/pdf" && PdfActiveContent(content) is { } marker)
            return new FileVerdict(null, $"ไฟล์ PDF นี้มีส่วนที่ทำงานได้เอง ({marker}) — บันทึกหนังสือรับรองเป็น PDF ธรรมดา หรือถ่ายรูปแล้วลองใหม่");
        return new FileVerdict(k, null);
    }

    private static readonly string[] ActiveMarkers = { "/JavaScript", "/JS", "/Launch", "/EmbeddedFile", "/RichMedia" };

    /// <summary>คำสั่ง PDF ที่ทำงานเอง/ฝังไฟล์ (ชื่อแรกที่พบ) · null = ไม่พบ — ค้นแบบไบต์ตรงตัว (ชื่อ PDF เป็น ASCII) · <c>/JS</c> ต้องไม่ตามด้วยตัวอักษร
    /// (กันชนกับชื่ออื่นที่ขึ้นต้นด้วย JS) · ฝ่ายค้าน WP-10 L3: ถอด escape <c>#xx</c> ในชื่อก่อนค้น (<c>/Java#53cript</c> = <c>/JavaScript</c>)
    /// <para><b>ด่านเสริม (defence-in-depth) ไม่ใช่ตัวตรวจ PDF เต็มรูป</b>: ชื่อที่อยู่ใน object stream ที่บีบอัดไว้ไม่ถูกเห็น · ด่านหลักคือ ไฟล์ไม่ถูกแสดงในต้นทางของแอป
    /// (เก็บผ่านที่เก็บกลาง · ดาวน์โหลดด้วยสิทธิ์ภาษี + ชนิดจากไบต์) และ OCR อ่านเป็นภาพ/ข้อความเท่านั้น</para></summary>
    private static string? PdfActiveContent(ReadOnlySpan<byte> raw)
    {
        ReadOnlySpan<byte> content = DecodeNameEscapes(raw);
        foreach (var m in ActiveMarkers)
        {
            ReadOnlySpan<byte> needle = System.Text.Encoding.ASCII.GetBytes(m);
            var span = content;
            int idx;
            while ((idx = span.IndexOf(needle)) >= 0)
            {
                var after = idx + needle.Length;
                var nextIsLetter = after < span.Length && char.IsAsciiLetter((char)span[after]);
                if (m != "/JS" || !nextIsLetter) return m;
                span = span[after..];
            }
        }
        return null;
    }

    /// <summary>ถอด <c>#xx</c> (เลขฐานสิบหกสองหลัก) ทั้งไฟล์ — สำเนาสำหรับค้นชื่อเท่านั้น (ไฟล์ที่เก็บคือไบต์เดิม)</summary>
    private static byte[] DecodeNameEscapes(ReadOnlySpan<byte> raw)
    {
        static int Hex(byte b) => b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 : b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;
        var outp = new byte[raw.Length];
        var n = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'#' && i + 2 < raw.Length && Hex(raw[i + 1]) >= 0 && Hex(raw[i + 2]) >= 0)
            {
                outp[n++] = (byte)(Hex(raw[i + 1]) * 16 + Hex(raw[i + 2]));
                i += 2;
            }
            else outp[n++] = raw[i];
        }
        return outp[..n];
    }

    /// <summary>SHA-256 hex ตัวเล็กของไฟล์ — กันไฟล์ซ้ำในกล่องรับ (ตัวเดียวกับที่ <c>WhtCertIntakeService</c> เทียบ · hex 64 ตัว)</summary>
    public static string Sha256Hex(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>ชื่อผู้ส่ง (ไม่บังคับ) — ตัดช่องว่าง · ตัดอักขระควบคุม · ยาวไม่เกิน <see cref="MaxSenderNameLength"/> · ว่าง ⇒ null</summary>
    public static string? SanitizeSenderName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > MaxSenderNameLength ? clean[..MaxSenderNameLength] : clean;
    }

    /// <summary>เอกสารที่ผู้ส่งติ๊กว่าไฟล์นี้ครอบ — เหลือเฉพาะที่อยู่ในชุดของลิงก์ (เอกสารรอใบของลูกค้ารายนี้) · id อื่นถูกทิ้งเงียบ ๆ
    /// (ไม่บอกว่ามีอยู่จริงไหม — กันไล่เดา) · ไม่ติ๊ก ⇒ null (ประตูเดียวจับคู่เอง)</summary>
    public static IReadOnlyList<Guid>? FilterHints(IEnumerable<Guid>? requested, IReadOnlyCollection<Guid> allowed)
    {
        var list = (requested ?? Array.Empty<Guid>()).Where(g => allowed.Contains(g)).Distinct().ToList();
        return list.Count == 0 ? null : list;
    }

    /// <summary>ฝ่ายค้าน WP-10 M2: ใบจากลิงก์อัปโหลด (ไม่ล็อกอิน) ต้องให้คนตรวจเสมอ — ข้อความด่านคงที่ (ใบไม่มีวันยืนยันอัตโนมัติ)</summary>
    public const string AnonymousReviewGate = "ใบนี้ส่งผ่านลิงก์อัปโหลด (ผู้ส่งไม่ได้ล็อกอิน) — ต้องให้พนักงานตรวจแล้วกดยืนยันเสมอ";

    /// <summary>
    /// ฝ่ายค้าน WP-10 M2 — ด่านเพิ่มของช่องลิงก์อัปโหลด: (1) <see cref="AnonymousReviewGate"/> เสมอ (ยืนยันอัตโนมัติไม่ได้ · การจัดสรรค้างเป็น "เสนอ") ·
    /// (2) ผู้จ่ายบนใบต้องเป็นลูกค้าของลิงก์ — เลขผู้เสียภาษีบนใบกับของผู้ติดต่อของลิงก์ (ทั้งคู่ถูก checksum) ไม่ตรง ⇒ ด่าน ·
    /// รายการที่ระบบเสนอเป็นของผู้ติดต่ออื่น ⇒ ด่าน (ผู้เรียกตัดรายการเหล่านั้นทิ้ง — ไม่เสนอแถวของลูกค้ารายอื่นให้ใบจากลิงก์นี้)
    /// </summary>
    public static IReadOnlyList<string> LinkGates(Guid linkContactId, string? certPayerTaxId, string? linkContactTaxId, IEnumerable<Guid?> proposedPayerContacts)
    {
        var gates = new List<string> { AnonymousReviewGate };
        if (ThaiTaxId.IsValid(certPayerTaxId) && ThaiTaxId.IsValid(linkContactTaxId) && !ThaiTaxId.Same(certPayerTaxId, linkContactTaxId))
            gates.Add("ผู้จ่ายบนใบไม่ใช่ลูกค้าที่ได้รับลิงก์นี้ (เลขผู้เสียภาษีไม่ตรง) — ตรวจว่าเป็นใบของลูกค้ารายใดก่อนยืนยัน");
        if (proposedPayerContacts.Any(p => p != linkContactId))
            gates.Add("รายการที่จับคู่ได้บางรายการไม่ใช่ของลูกค้าที่ได้รับลิงก์นี้ — ระบบไม่เสนอรายการเหล่านั้น");
        return gates;
    }

    /// <summary>ผู้ส่งที่บันทึกบนใบในกล่องรับ — ระบุช่องทางเสมอ (ผู้ตรวจรู้ว่าใบมาจากลูกค้าผ่านลิงก์ ไม่ใช่พนักงาน)</summary>
    public static string SubmittedBy(string? senderName)
        => senderName == null ? "ลูกค้า (ลิงก์อัปโหลด)" : $"ลูกค้า (ลิงก์อัปโหลด) · {senderName}";
}
