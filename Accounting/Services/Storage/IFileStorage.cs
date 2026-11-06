namespace Accounting.Services.Storage;

/// <summary>
/// **ที่เก็บไฟล์กลางตัวเดียวของระบบ** (ทีม FS · 2026-11-02 · CLAUDE.md กฎเหล็ก #4 D "ไฟล์ห้ามเขียน local disk ตรง — ผ่าน abstraction กลาง")
///
/// <para><b>ตำแหน่ง (location)</b> = สตริงเดียวกับที่ระบบเก็บไว้ในฐานมาตลอด (พาธเต็ม หรือพาธสัมพัทธ์จาก working dir) —
/// ผู้เรียกไม่ต้องรู้ว่าไฟล์อยู่บนดิสก์หรือ object storage · key ของ object storage คำนวณจากตำแหน่งด้วย
/// <see cref="Accounting.Helpers.FileStorageKey"/> ตัวเดียว ⇒ ค่าที่ persist ไว้ไม่ต้องแก้ (ไม่มี data migration)</para>
///
/// <para>ผู้ให้บริการ: <see cref="LocalDiskFileStorage"/> (ค่าเริ่มต้น = พฤติกรรมเดิมทุกไบต์) · <see cref="S3FileStorage"/>
/// (AWS S3 / Cloudflare R2 / MinIO / DigitalOcean Spaces) — เลือกด้วย <c>FileStorage:Provider</c></para>
///
/// <para>สัญญา: อ่าน/เปิด/ตรวจ ของที่<b>ไม่มี</b> ⇒ <c>null</c>/<c>false</c> · ที่เก็บ<b>ตอบผิดปกติ</b> (เครือข่าย · 5xx · สิทธิ์) ⇒ โยน
/// <see cref="FileStorageException"/> (ล้มดัง — ไม่แกล้งว่าไฟล์หาย) · เขียนล้ม ⇒ โยนเสมอ</para>
/// </summary>
public interface IFileStorage
{
    /// <summary>"Local" หรือ "S3" (ตาม <see cref="Accounting.Helpers.FileStorageConfigPolicy"/>)</summary>
    string ProviderName { get; }

    /// <summary>true = ไฟล์อยู่บนดิสก์ของเครื่องนี้ (เสิร์ฟด้วย PhysicalFile/static files ได้)</summary>
    bool IsLocal { get; }

    /// <summary>รากและสูตร key ของระบบนี้</summary>
    FileStorageLocator Locator { get; }

    Task WriteAsync(string location, byte[] content, string? contentType = null, CancellationToken ct = default);
    Task WriteAsync(string location, Stream content, string? contentType = null, CancellationToken ct = default);

    /// <summary>ไบต์ทั้งไฟล์ · ไม่มี = null</summary>
    Task<byte[]?> ReadAllBytesAsync(string? location, CancellationToken ct = default);
    /// <summary>แบบ sync — เฉพาะตัวเรนเดอร์ PDF ที่เป็น sync ทั้งเส้น (ฝังโลโก้/ตราประทับ)</summary>
    byte[]? ReadAllBytes(string? location);

    /// <summary>สตรีมอ่าน (ผู้เรียก dispose) · ไม่มี = null</summary>
    Task<Stream?> OpenReadAsync(string? location, CancellationToken ct = default);

    Task<bool> ExistsAsync(string? location, CancellationToken ct = default);
    /// <summary>แบบ sync — เฉพาะด่านพาธที่เป็น pure helper รับ <c>Func&lt;string,bool&gt;</c> (เช่น <c>LodgingSlipPath.Resolve</c>)</summary>
    bool Exists(string? location);

    /// <summary>ลบ · ไม่มีอยู่แล้ว = false (ไม่ใช่ error) · ผู้เรียกต้องตัดสินเรื่องระยะเก็บรักษาก่อนเรียก (พ.ร.บ.การบัญชี ม.14)</summary>
    Task<bool> DeleteAsync(string? location, CancellationToken ct = default);
}

/// <summary>ที่เก็บตอบผิดปกติ (ไม่ใช่ "ไม่พบไฟล์") — ล้มดัง</summary>
public sealed class FileStorageException : IOException
{
    public FileStorageException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>ข้อมูล object ฝั่งปลายทาง — <c>Sha256</c> มาจาก metadata ที่ระบบเขียนไว้ตอน PUT (null = object ที่ไม่ได้เขียนผ่านระบบนี้)</summary>
public sealed record RemoteObjectInfo(long Size, string? Sha256);

/// <summary>
/// คำสั่งตรงถึง object storage <b>โดยไม่ถอยไปดิสก์</b> — ใช้เฉพาะเครื่องมือย้ายไฟล์ (<see cref="FileStorageMigrationService"/>) ซึ่งต้องรู้ว่า
/// "ปลายทางมีจริง" ไม่ใช่ "อ่านได้จากที่ใดที่หนึ่ง" · key = ผลของ <see cref="Accounting.Helpers.FileStorageKey"/> (ยังไม่ต่อ KeyPrefix)
/// </summary>
public interface IRemoteObjectStore
{
    /// <summary>คำอธิบายปลายทางสำหรับรายงาน (endpoint/bucket — ไม่มีความลับ)</summary>
    string Describe { get; }
    Task<RemoteObjectInfo?> HeadAsync(string key, CancellationToken ct = default);
    Task PutAsync(string key, byte[] content, string? contentType, CancellationToken ct = default);
    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);
}
