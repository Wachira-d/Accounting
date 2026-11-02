using Microsoft.AspNetCore.Mvc;

namespace Accounting.Services.Storage;

/// <summary>
/// เสิร์ฟไฟล์จากที่เก็บกลางเป็นผลของ controller — แทน <c>PhysicalFile(path, …)</c> ที่ใช้ได้เฉพาะไฟล์บนดิสก์ของเครื่องนี้
///
/// <para>Local ⇒ <see cref="PhysicalFileResult"/> ตัวเดิม (ไม่โหลดเข้าหน่วยความจำ · พฤติกรรมเดิม) · S3 ⇒ <see cref="FileStreamResult"/> จากสตรีมของ object ·
/// <c>downloadName</c> = null ⇒ ไม่ตั้ง Content-Disposition (inline ตามเดิมของ <c>PhysicalFile(path, type)</c>) · ไม่พบไฟล์ ⇒ null (ผู้เรียกตอบ 404 ข้อความของตัวเอง)</para>
/// </summary>
public static class FileStorageResults
{
    public static async Task<IActionResult?> FileResultAsync(this IFileStorage storage, string? location, string contentType,
        string? downloadName = null, CancellationToken ct = default)
    {
        if (storage is LocalDiskFileStorage local)
        {
            var full = local.ResolveFullPath(location);
            if (full == null || !File.Exists(full)) return null;
            return new PhysicalFileResult(full, contentType) { FileDownloadName = downloadName };
        }
        var stream = await storage.OpenReadAsync(location, ct);
        if (stream == null) return null;
        return new FileStreamResult(stream, contentType) { FileDownloadName = downloadName };
    }
}
