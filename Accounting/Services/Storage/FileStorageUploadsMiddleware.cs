using Microsoft.AspNetCore.StaticFiles;

namespace Accounting.Services.Storage;

/// <summary>
/// เสิร์ฟ <c>/uploads/**</c> สาธารณะ (โลโก้ · รูปสินค้า · สื่อ CMS · สื่อศูนย์ช่วยเหลือ …) จาก object storage เมื่อ <c>FileStorage:Provider=S3</c>
///
/// <para>วางหลัง static file handler ทั้งสองตัวใน Program.cs — ไฟล์ที่ยังอยู่บนดิสก์ static handler ตอบไปก่อนแล้ว ·
/// ตัวกรองสิทธิ์ (<c>publicUploadPrefixes</c> allow-list) ทำงาน<b>ก่อน</b>ตัวนี้เสมอ ⇒ ตัวนี้ไม่เปิดโฟลเดอร์ที่ allow-list ปิดไว้ (สลิป/ไฟล์แนบ) ·
/// โหมด Local ⇒ ส่งต่อทันที (static handler คือผู้เสิร์ฟตามเดิม)</para>
///
/// <para>ตำแหน่งที่ลอง (ตรงกับ static handler สองตัวเดิม): <c>{wwwroot}/uploads/…</c> แล้ว <c>{cwd}/uploads/…</c> · header เดียวกับ static
/// handler ของ <c>/uploads</c> (nosniff + Content-Disposition: attachment — F-03 · cache 7 วัน) · ชนิดไฟล์ที่ไม่รู้จัก ⇒ ไม่เสิร์ฟ (เหมือน static handler) ·
/// Range/If-None-Match ส่งต่อถึง S3 (วิดีโอ seek ได้ · 206/304) · ETag/Last-Modified จาก object (ฝ่ายค้าน S4)</para>
/// </summary>
public sealed class FileStorageUploadsMiddleware
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private readonly RequestDelegate _next;

    public FileStorageUploadsMiddleware(RequestDelegate next) => _next = next;

    /// <summary>
    /// ตำแหน่งบนที่เก็บของ URL <c>/uploads/{relative}</c> — ปฏิเสธ backslash/อักขระควบคุม · พาธที่ resolve แล้วต้องอยู่ใต้โฟลเดอร์ uploads นั้น
    /// (กัน <c>/uploads/../appsettings.json</c> — โหมด S3 ถอยไปอ่านดิสก์ได้) · ต้องแปลงเป็น key ได้ · คืนว่าง = ไม่เสิร์ฟ
    /// </summary>
    public static IReadOnlyList<string> CandidateLocations(FileStorageLocator locator, string relative)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Any(char.IsControl)) return result;
        relative = relative.TrimStart('/');
        foreach (var uploadsDir in new[] { Path.Combine(locator.WebRoot, "uploads"), Path.Combine(locator.RelativeBase, "uploads") })
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(uploadsDir, relative)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            var root = Accounting.Helpers.FileStorageKey.Normalize(uploadsDir).TrimEnd('/') + "/";
            if (!Accounting.Helpers.FileStorageKey.Normalize(candidate).StartsWith(root, StringComparison.Ordinal)) continue;
            if (locator.ToKey(candidate) == null) continue;
            if (!result.Contains(candidate)) result.Add(candidate);
        }
        return result;
    }

    public async Task InvokeAsync(HttpContext ctx, IFileStorage storage)
    {
        var path = ctx.Request.Path;
        var isHead = HttpMethods.IsHead(ctx.Request.Method);
        if (storage.IsLocal
            || !(HttpMethods.IsGet(ctx.Request.Method) || isHead)
            || !path.StartsWithSegments("/uploads", out var rest)
            || !rest.HasValue
            || !ContentTypes.TryGetContentType(rest.Value!, out var contentType))
        {
            await _next(ctx);
            return;
        }

        foreach (var candidate in CandidateLocations(storage.Locator, rest.Value!))
        {
            if (storage is IServableObjectStore servable)
            {
                using var obj = await servable.ServeAsync(candidate, ctx.Request.Headers.Range.ToString(),
                    ctx.Request.Headers.IfNoneMatch.ToString(), ctx.RequestAborted);
                if (obj == null) continue;
                WriteHeaders(ctx, contentType);
                ctx.Response.StatusCode = obj.StatusCode;
                if (obj.ETag != null) ctx.Response.Headers.ETag = obj.ETag;
                if (obj.LastModified is { } lm) ctx.Response.Headers.LastModified = lm.ToString("R");
                if (obj.ContentRange != null) ctx.Response.Headers.ContentRange = obj.ContentRange;
                if (obj.ContentLength is { } len && obj.Body != null) ctx.Response.ContentLength = len;
                if (!isHead && obj.Body != null) await obj.Body.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                return;
            }
            var stream = await storage.OpenReadAsync(candidate, ctx.RequestAborted);
            if (stream == null) continue;
            await using (stream)
            {
                WriteHeaders(ctx, contentType);
                if (!isHead) await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
            return;
        }
        await _next(ctx);
    }

    private static void WriteHeaders(HttpContext ctx, string contentType)
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers.CacheControl = "public, max-age=604800";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers.ContentDisposition = "attachment";
        ctx.Response.Headers.AcceptRanges = "bytes";
    }
}
