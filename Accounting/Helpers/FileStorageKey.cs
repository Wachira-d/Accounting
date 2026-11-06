namespace Accounting.Helpers;

/// <summary>รากหนึ่งของที่เก็บไฟล์: ชื่อ prefix ของ key + โฟลเดอร์จริงบนดิสก์ (โหมด LocalDisk)</summary>
public sealed record FileStorageRoot(string Prefix, string AbsolutePath);

/// <summary>
/// **แปลง "พาธที่เก็บไว้ในฐาน" → object key ของที่เก็บไฟล์กลาง — ตัวตั้งตัวเดียว** (ทีม FS · 2026-11-02 · CLAUDE.md กฎเหล็ก #4 D)
///
/// ═══ ทำไมต้องมี ═══
/// <para>ทุกเส้นเขียนไฟล์ลงดิสก์แล้วเก็บ <b>พาธเต็ม</b> (หรือพาธสัมพัทธ์จาก working dir) ไว้ในฐาน — <c>FileAttachment.StoragePath</c> ·
/// <c>CompanySettings.LogoPath</c> · <c>EtaxInvoice.PdfFilePath</c> ฯลฯ · การย้ายไป object storage (S3/R2/MinIO/Spaces)
/// <b>ไม่แก้ค่าที่ persist ไว้</b> (ไม่มี migration ข้อมูล · ไม่มีช่วงที่แถวเก่าอ่านไม่ได้) — key ถูก<b>คำนวณ</b>จากพาธด้วยกติกานี้ทุกครั้ง
/// ทั้งฝั่งเขียน ฝั่งอ่าน และเครื่องมือย้ายไฟล์ ⇒ สามฝั่งได้ key เดียวกันเสมอ (สูตรเดียว ห้ามมีสำเนา)</para>
///
/// <para>กติกา: พาธสัมพัทธ์ resolve กับ <c>relativeBase</c> (working dir เดิมที่ผู้เขียนใช้) → หา<b>รากที่ยาวที่สุด</b>ที่ครอบพาธ →
/// key = <c>{prefix}/{พาธย่อยใต้ราก}</c> (ตัวคั่น <c>/</c>) · พาธนอกทุกราก / มี <c>..</c> หลง resolve / อักขระควบคุม ⇒ <c>null</c>
/// (ผู้เรียกต้องถือว่า "ไม่มีในที่เก็บกลาง" — ห้ามแต่ง key เอง)</para>
/// </summary>
public static class FileStorageKey
{
    /// <summary>ราก <c>FileStorage:BasePath</c> (ค่าเริ่มต้น <c>{cwd}/uploads</c>) — ไฟล์แนบของ <c>FileAttachmentService</c></summary>
    public const string AttachmentsPrefix = "attachments";
    /// <summary>ราก wwwroot — โลโก้ · รูปสินค้า · สื่อ · สลิป (<c>wwwroot/uploads/**</c>) · สแกน OCR</summary>
    public const string WebPrefix = "web";
    /// <summary>ราก content root — <c>storage/etax/**</c> · <c>uploads/attachments/**</c> เมื่อ BasePath ถูกย้ายไปที่อื่น</summary>
    public const string AppPrefix = "app";

    public const int MaxKeyLength = 1024;

    private static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>ประกอบรากทั้งสาม (normalize แล้ว) — ลำดับไม่สำคัญเพราะ <see cref="ToKey"/> เลือกรากที่ยาวที่สุด</summary>
    public static IReadOnlyList<FileStorageRoot> BuildRoots(string attachmentsRoot, string webRoot, string appRoot) => new[]
    {
        new FileStorageRoot(AttachmentsPrefix, Normalize(attachmentsRoot)),
        new FileStorageRoot(WebPrefix, Normalize(webRoot)),
        new FileStorageRoot(AppPrefix, Normalize(appRoot)),
    };

    /// <summary>พาธที่เก็บไว้ → key · <c>null</c> = แปลงไม่ได้ (ว่าง · นอกทุกราก · ไม่ปลอดภัย)</summary>
    public static string? ToKey(string? location, IReadOnlyList<FileStorageRoot> roots, string relativeBase)
    {
        if (string.IsNullOrWhiteSpace(location) || location.Any(char.IsControl)) return null;
        string full;
        try
        {
            full = Normalize(Path.IsPathRooted(location) ? location : Path.Combine(relativeBase, location));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // รากเองไม่ใช่ไฟล์ — รวมถึงรากที่ซ้อนอยู่ใต้รากอื่น (เช่น {contentRoot}/uploads) · เดิมตกไปรากแม่ได้ "app/uploads"
        // ซึ่งไม่ใช่ไฟล์ใด (รอบ 250 WP-01 · FileStorageKeyTests เพิ่งถูกรันครั้งแรก)
        if (roots.Any(r => r.AbsolutePath.TrimEnd('/').Length > 0 && string.Equals(full.TrimEnd('/'), r.AbsolutePath.TrimEnd('/'), PathCmp)))
            return null;

        FileStorageRoot? best = null;
        foreach (var r in roots)
        {
            var root = r.AbsolutePath.TrimEnd('/');
            if (root.Length == 0) continue;
            if (!full.StartsWith(root + "/", PathCmp)) continue;
            if (best == null || root.Length > best.AbsolutePath.TrimEnd('/').Length) best = r;
        }
        if (best == null) return null;
        var rel = full[(best.AbsolutePath.TrimEnd('/').Length + 1)..];
        var key = best.Prefix + "/" + rel;
        return IsSafeKey(key) ? key : null;
    }

    /// <summary>key ที่ยอมรับ: prefix ที่รู้จัก · ไม่มี <c>..</c>/<c>.</c>/ช่วงว่าง · ไม่มี backslash/อักขระควบคุม · ยาวไม่เกิน <see cref="MaxKeyLength"/></summary>
    internal static bool IsSafeKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength) return false;
        if (key.Contains('\\') || key.Any(char.IsControl)) return false;
        var parts = key.Split('/');
        if (parts.Length < 2) return false;
        if (parts[0] is not (AttachmentsPrefix or WebPrefix or AppPrefix)) return false;
        return parts.All(p => p.Length > 0 && p != "." && p != "..");
    }

    /// <summary>พาธเต็ม + ตัวคั่น <c>/</c> (ยุบ <c>..</c> แล้ว) — ใช้ทั้งรากและพาธที่เทียบ</summary>
    public static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/');
}
