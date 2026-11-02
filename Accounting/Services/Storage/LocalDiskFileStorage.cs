namespace Accounting.Services.Storage;

/// <summary>
/// ผู้ให้บริการ "ดิสก์ของเครื่องนี้" — <b>พฤติกรรมเดิมทุกไบต์</b> (ไฟล์อยู่ที่พาธเดิม · ค่าที่ persist เหมือนเดิม) · ค่าเริ่มต้นของ dev/เครื่องเดียว
///
/// <para>ไฟล์นี้ (และ <see cref="FileStorageResults"/>) คือ<b>ที่เดียว</b>ที่แตะ <c>System.IO.File</c>/<c>Directory</c> เพื่อเก็บไฟล์ของผู้ใช้ —
/// <c>tools/local_disk_write_check.py</c> ยกเว้นโฟลเดอร์ <c>Services/Storage</c> ตัวเดียว</para>
///
/// <para>ด่านเขียน: ตำแหน่งต้องอยู่ใต้รากที่รู้จัก (<see cref="FileStorageLocator.ToKey"/> ไม่ null) — ตำแหน่งนอกราก (โค้ดประกอบพาธผิด) ล้มทันที
/// แทนที่จะเขียนไฟล์ที่โหมด S3 จะหาไม่เจอ · ด่านอ่าน: ยอมพาธเก่านอกราก (แถวที่เขียนก่อนย้าย BasePath) เพื่อไม่ให้ไฟล์เดิมหาย</para>
/// </summary>
public sealed class LocalDiskFileStorage : IFileStorage
{
    public LocalDiskFileStorage(FileStorageLocator locator) => Locator = locator;

    public string ProviderName => Accounting.Helpers.FileStorageConfigPolicy.ProviderLocal;
    public bool IsLocal => true;
    public FileStorageLocator Locator { get; }

    /// <summary>พาธเต็มของตำแหน่ง (ไม่ตรวจว่ามีไฟล์) · ว่าง/รูปผิด = null</summary>
    public string? ResolveFullPath(string? location)
    {
        if (string.IsNullOrWhiteSpace(location) || location.Any(char.IsControl)) return null;
        try { return Locator.FullPath(location); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private string WritablePath(string location)
    {
        var full = ResolveFullPath(location)
            ?? throw new FileStorageException($"ตำแหน่งไฟล์ไม่ถูกต้อง: \"{location}\"");
        if (Locator.ToKey(full) == null)
            throw new FileStorageException(
                $"ตำแหน่งไฟล์ \"{full}\" อยู่นอกรากที่เก็บไฟล์ (FileStorage:BasePath / wwwroot / content root) — ประกอบพาธผ่านรากของ FileStorageLocator");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    public async Task WriteAsync(string location, byte[] content, string? contentType = null, CancellationToken ct = default)
        => await File.WriteAllBytesAsync(WritablePath(location), content, ct);

    public async Task WriteAsync(string location, Stream content, string? contentType = null, CancellationToken ct = default)
    {
        var full = WritablePath(location);
        await using var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(fs, ct);
    }

    public async Task<byte[]?> ReadAllBytesAsync(string? location, CancellationToken ct = default)
    {
        var full = ResolveFullPath(location);
        if (full == null || !File.Exists(full)) return null;
        return await File.ReadAllBytesAsync(full, ct);
    }

    public byte[]? ReadAllBytes(string? location)
    {
        var full = ResolveFullPath(location);
        return full == null || !File.Exists(full) ? null : File.ReadAllBytes(full);
    }

    public Task<Stream?> OpenReadAsync(string? location, CancellationToken ct = default)
    {
        var full = ResolveFullPath(location);
        if (full == null || !File.Exists(full)) return Task.FromResult<Stream?>(null);
        return Task.FromResult<Stream?>(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }

    public Task<bool> ExistsAsync(string? location, CancellationToken ct = default) => Task.FromResult(Exists(location));

    public bool Exists(string? location)
    {
        var full = ResolveFullPath(location);
        return full != null && File.Exists(full);
    }

    public Task<bool> DeleteAsync(string? location, CancellationToken ct = default)
    {
        var full = ResolveFullPath(location);
        if (full == null || !File.Exists(full)) return Task.FromResult(false);
        File.Delete(full);
        return Task.FromResult(true);
    }

    /// <summary>ขนาดไฟล์ (ไบต์) · ไม่มี = null — เครื่องมือย้ายเทียบขนาดกับปลายทางก่อนอ่านทั้งไฟล์</summary>
    public long? Length(string? location)
    {
        var full = ResolveFullPath(location);
        return full != null && File.Exists(full) ? new FileInfo(full).Length : null;
    }

    /// <summary>ไฟล์ทุกตัวใต้โฟลเดอร์ (เรียงตามพาธ — ผลซ้ำได้ทุกรอบ) · ใช้เฉพาะเครื่องมือย้ายไฟล์</summary>
    public IEnumerable<string> EnumerateFiles(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            : Enumerable.Empty<string>();
}
