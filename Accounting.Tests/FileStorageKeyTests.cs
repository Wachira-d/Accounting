using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// สูตร "พาธที่เก็บไว้ → object key" ตัวเดียว (<see cref="FileStorageKey"/> · ทีม FS 2026-11-02) — ฝั่งเขียน · ฝั่งอ่าน · เครื่องมือย้ายไฟล์
/// ต้องได้ key เดียวกันเสมอ มิฉะนั้นไฟล์ที่ย้ายแล้ว "หาย" ในโหมด S3 ทั้งที่อยู่ใน bucket
/// </summary>
public class FileStorageKeyTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "fsk-app");
    private static readonly IReadOnlyList<FileStorageRoot> Roots = FileStorageKey.BuildRoots(
        Path.Combine(Base, "uploads"), Path.Combine(Base, "wwwroot"), Base);

    private static string P(params string[] parts) => Path.Combine(new[] { Base }.Concat(parts).ToArray());

    [Fact]
    public void ไฟล์แนบใต้BasePath_ได้prefix_attachments()
        => Assert.Equal("attachments/c1/Document/a.pdf", FileStorageKey.ToKey(P("uploads", "c1", "Document", "a.pdf"), Roots, Base));

    [Fact]
    public void รากที่ยาวที่สุดชนะ_wwwrootอยู่ใต้contentRootแต่ได้prefix_web()
        => Assert.Equal("web/uploads/ocr/x.jpg", FileStorageKey.ToKey(P("wwwroot", "uploads", "ocr", "x.jpg"), Roots, Base));

    [Fact]
    public void พาธสัมพัทธ์จากworkingDir_กับพาธเต็ม_ได้keyเดียวกัน()
    {
        // FileAttachmentController เก็บ "uploads/attachments/{cid}/f" (สัมพัทธ์) · ตัวอ่านเดิมต่อกับ working dir — key ต้องตรงกัน
        var rel = Path.Combine("uploads", "attachments", "c1", "f.pdf");
        Assert.Equal(FileStorageKey.ToKey(P("uploads", "attachments", "c1", "f.pdf"), Roots, Base), FileStorageKey.ToKey(rel, Roots, Base));
        Assert.Equal("attachments/attachments/c1/f.pdf", FileStorageKey.ToKey(rel, Roots, Base));
    }

    [Fact]
    public void storageของeTax_ได้prefix_app()
        => Assert.Equal("app/storage/etax/abc/INV1.pdf", FileStorageKey.ToKey(P("storage", "etax", "abc", "INV1.pdf"), Roots, Base));

    [Fact]
    public void นอกทุกราก_หรือว่าง_หรืออักขระควบคุม_ได้null()
    {
        Assert.Null(FileStorageKey.ToKey(Path.Combine(Path.GetTempPath(), "elsewhere", "x.pdf"), Roots, Base));
        Assert.Null(FileStorageKey.ToKey("", Roots, Base));
        Assert.Null(FileStorageKey.ToKey(null, Roots, Base));
        Assert.Null(FileStorageKey.ToKey(P("uploads", "a\0b.pdf"), Roots, Base));
        // รากเองไม่ใช่ไฟล์
        Assert.Null(FileStorageKey.ToKey(P("uploads"), Roots, Base));
    }

    [Fact]
    public void dotdotถูกยุบก่อนเทียบราก_หลุดออกนอกรากได้null()
        => Assert.Null(FileStorageKey.ToKey(P("uploads", "..", "..", "..", "etc", "passwd"), Roots, Base));

    [Theory]
    [InlineData("attachments/c1/a.pdf", true)]
    [InlineData("web/uploads/x.png", true)]
    [InlineData("app/storage/etax/a.xml", true)]
    [InlineData("other/x.png", false)]
    [InlineData("attachments", false)]
    [InlineData("attachments/../x", false)]
    [InlineData("attachments//x", false)]
    [InlineData("attachments/a\\b", false)]
    [InlineData("/attachments/x", false)]
    public void IsSafeKey(string key, bool ok) => Assert.Equal(ok, FileStorageKey.IsSafeKey(key));
}
