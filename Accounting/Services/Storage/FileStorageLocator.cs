using Accounting.Helpers;

namespace Accounting.Services.Storage;

/// <summary>
/// รากของที่เก็บไฟล์ของ process นี้ + สูตร key (<see cref="FileStorageKey"/>) — <b>ที่เดียว</b>ที่อ่าน <c>FileStorage:BasePath</c> /
/// <c>Directory.GetCurrentDirectory()</c> เพื่อหาโฟลเดอร์เก็บไฟล์ (เดิมกระจาย 12 ไฟล์ · <c>tools/local_disk_write_check.py</c>)
/// </summary>
public sealed class FileStorageLocator
{
    public FileStorageLocator(string attachmentsRoot, string webRoot, string appRoot, string relativeBase)
    {
        AttachmentsRoot = Path.GetFullPath(attachmentsRoot);
        WebRoot = Path.GetFullPath(webRoot);
        AppRoot = Path.GetFullPath(appRoot);
        RelativeBase = Path.GetFullPath(relativeBase);
        Roots = FileStorageKey.BuildRoots(AttachmentsRoot, WebRoot, AppRoot);
    }

    /// <summary><c>FileStorage:BasePath</c> (ค่าเริ่มต้น <c>{cwd}/uploads</c>) — โฟลเดอร์ของ <c>FileAttachmentService</c></summary>
    public string AttachmentsRoot { get; }
    /// <summary>wwwroot (ไม่มี ⇒ <c>{contentRoot}/wwwroot</c>)</summary>
    public string WebRoot { get; }
    public string AppRoot { get; }
    /// <summary>ฐานของพาธสัมพัทธ์ที่เก็บไว้ในฐาน (working dir เดิมของผู้เขียน)</summary>
    public string RelativeBase { get; }
    public IReadOnlyList<FileStorageRoot> Roots { get; }

    /// <summary>ตำแหน่ง → key · null = นอกทุกราก/ไม่ปลอดภัย</summary>
    public string? ToKey(string? location) => FileStorageKey.ToKey(location, Roots, RelativeBase);

    /// <summary>ตำแหน่ง → พาธเต็มบนดิสก์ (สัมพัทธ์ ⇒ ต่อกับ <see cref="RelativeBase"/>)</summary>
    public string FullPath(string location)
        => Path.GetFullPath(Path.IsPathRooted(location) ? location : Path.Combine(RelativeBase, location));

    /// <summary>โฟลเดอร์ที่เครื่องมือย้ายไฟล์เดินหา (ไม่เดินทั้ง content root — มีไฟล์โปรแกรม) · ซ้ำ/ซ้อนกันถูกตัดแล้ว</summary>
    public IReadOnlyList<string> MigrationDirectories()
    {
        var candidates = new[]
        {
            AttachmentsRoot,
            Path.Combine(WebRoot, "uploads"),
            Path.Combine(AppRoot, "uploads"),
            Path.Combine(AppRoot, "storage"),
        }.Select(Path.GetFullPath).Distinct().ToList();
        // ตัดโฟลเดอร์ที่อยู่ใต้โฟลเดอร์อื่นในรายการ (กันนับไฟล์ซ้ำ)
        return candidates
            .Where(c => !candidates.Any(o => o != c && FileStorageKey.Normalize(c).StartsWith(FileStorageKey.Normalize(o).TrimEnd('/') + "/", StringComparison.Ordinal)))
            .ToList();
    }

    public static FileStorageLocator FromEnvironment(IConfiguration config, IWebHostEnvironment env)
    {
        var cwd = Directory.GetCurrentDirectory();
        var attachments = config["FileStorage:BasePath"];
        return new FileStorageLocator(
            string.IsNullOrWhiteSpace(attachments) ? Path.Combine(cwd, "uploads") : attachments,
            env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"),
            env.ContentRootPath,
            cwd);
    }
}
