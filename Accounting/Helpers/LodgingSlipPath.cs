namespace Accounting.Helpers;

/// <summary>
/// **แปลง storage key ของสลิป → พาธจริงบนดิสก์ อย่างปลอดภัย** (LDG-P2-06)
///
/// ═══ ที่มา ═══
/// <para>โฟลเดอร์ <c>/uploads/lodging-slips</c> ถูกถอดออกจาก
/// <c>publicUploadPrefixes</c> แล้ว เพราะสลิปโอนเงินมี<b>ชื่อผู้โอน + เลขบัญชี +
/// ยอด</b> = ข้อมูลส่วนบุคคลของแขก · เดิมใครได้ URL ไป (ซึ่งถูกส่งกลับใน API
/// response ของหน้าการจอง) ก็เปิดดูได้ตลอดกาลโดยไม่มีด่านอะไรเลย</para>
///
/// <para><b>ทำไมต้องมีด่านทาง "พาธ" ด้วย ทั้งที่มีด่านสิทธิ์แล้ว</b> — ค่าที่เก็บ
/// ใน DB เขียนโดยโค้ดเราก็จริง แต่การเอาสตริงจากฐานข้อมูลไปต่อเป็นพาธไฟล์ตรง ๆ
/// คือรูปเดียวกับ path traversal ทุกครั้ง (แถวเก่า · migration · import · แถวที่
/// ถูกแก้ด้วย SQL) · ด่านสองชั้น: ตรวจ<b>รูปของสตริง</b>ก่อน แล้วตรวจซ้ำว่า
/// <b>พาธที่ resolve แล้วยังอยู่ใต้รากที่อนุญาต</b></para>
///
/// <para>เป็น <b>ตรรกะล้วน</b> (รับ root มาเป็นพารามิเตอร์ ไม่อ่าน
/// <c>Directory.GetCurrentDirectory()</c> เอง) เพื่อให้เทสต์ยืนยันด่านได้จริง —
/// "control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control"</para>
/// </summary>
public static class LodgingSlipPath
{
    /// <summary>prefix ที่ยอมรับ — ต้องตรงกับที่ <c>SaveSlipFileAsync</c> เขียนลง DB</summary>
    public const string UrlPrefix = "/uploads/lodging-slips/";

    /// <summary>พาธย่อยใต้ wwwroot ของโฟลเดอร์สลิป</summary>
    public const string RelativeRoot = "uploads/lodging-slips";

    /// <summary>
    /// คืนพาธเต็มที่ปลอดภัย หรือ <c>null</c> เมื่อค่าไม่ผ่านด่าน
    /// </summary>
    /// <param name="storedUrl">ค่าที่เก็บใน <c>LodgingReservation.PaymentSlipUrl</c></param>
    /// <param name="webRoot">รากของ wwwroot (ผู้เรียกเป็นคนบอก — ทดสอบได้)</param>
    /// <param name="fileExists">ตัวตรวจว่าไฟล์มีจริง (แทนได้ในเทสต์)</param>
    public static string? Resolve(string? storedUrl, string webRoot, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(storedUrl)) return null;
        // ── ชั้นที่ 1: รูปของสตริง ──
        if (!storedUrl.StartsWith(UrlPrefix, StringComparison.Ordinal)) return null;
        if (storedUrl.Contains("..", StringComparison.Ordinal)) return null;
        // ห้าม NUL / อักขระควบคุม — บาง API ตัดสตริงที่ NUL แล้วชี้ไฟล์คนละตัว
        if (storedUrl.Any(char.IsControl)) return null;
        // ห้าม backslash — บน Windows เป็นตัวคั่นพาธ ⇒ เลี่ยงด่าน `..` ที่ดูแต่ `/` ได้
        if (storedUrl.Contains('\\')) return null;

        // ── ชั้นที่ 2: ตรวจซ้ำหลัง resolve ──
        // สตริงที่ผ่านชั้นแรกยังหลุดออกนอกรากได้ ถ้ามีสัญลักษณ์ที่ OS ตีความ
        // ต่างจากที่เราคิด (เช่น พาธซ้อน `//`, junction, symlink ในบางระบบ)
        var root = Path.GetFullPath(Path.Combine(webRoot, RelativeRoot));
        var full = Path.GetFullPath(Path.Combine(webRoot, storedUrl.TrimStart('/')));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;

        var exists = fileExists ?? File.Exists;
        return exists(full) ? full : null;
    }

    /// <summary>content type จากนามสกุล — สลิปมีแค่รูปกับ PDF
    /// (ห้ามคืน content type ที่เบราว์เซอร์เอาไปรันได้ เช่น text/html)</summary>
    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };
}
