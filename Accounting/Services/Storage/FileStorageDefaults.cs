namespace Accounting.Services.Storage;

/// <summary>
/// ที่เก็บ<b>สำรอง</b>สำหรับคลาสที่ถูกสร้างนอก DI (ตัวสร้างในเทสต์ · ตัวเรนเดอร์ PDF ที่มี static helper) — ดิสก์ของเครื่องนี้ด้วยรากเดียวกับค่าเริ่มต้นของ
/// <see cref="FileStorageLocator.FromEnvironment"/> (<c>{cwd}/uploads</c> · <c>{cwd}/wwwroot</c> · <c>{cwd}</c>) = พฤติกรรมเดิมก่อนมีที่เก็บกลาง
///
/// <para>ใน production ทุกคลาสได้ <see cref="IFileStorage"/> จาก DI (Local หรือ S3 ตามค่าตั้ง) — ตัวนี้มีไว้กัน "ไม่มีที่เก็บ ⇒ เงียบ/โยน"
/// (โลโก้หายจาก PDF · อัปโหลดสลิปล้ม) ไม่ใช่ทางเลือกแทนค่าตั้ง (ทีม FS 2026-11-02 · ฝ่ายค้าน B2/B3)</para>
/// </summary>
public static class FileStorageDefaults
{
    private static readonly Lazy<LocalDiskFileStorage> LocalLazy = new(() =>
    {
        var cwd = Directory.GetCurrentDirectory();
        return new LocalDiskFileStorage(new FileStorageLocator(
            Path.Combine(cwd, "uploads"), Path.Combine(cwd, "wwwroot"), cwd, cwd));
    });

    /// <summary>ดิสก์ของเครื่องนี้ที่รากค่าเริ่มต้น</summary>
    public static LocalDiskFileStorage Local => LocalLazy.Value;
}
