using System.Text;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// allow-list ของไฟล์แนบเอกสาร — <see cref="UploadFileType.SniffAttachment"/>
///
/// <para>ที่มา: รอบ 190 ข้อ 7 — <c>FileAttachmentController.Upload</c> เชื่อนามสกุล + Content-Type
/// ที่ client ส่งมา แล้วเก็บ Content-Type นั้นไว้ตอบตอนดาวน์โหลด ⇒ HTML ที่ตั้งชื่อ .csv ผ่านได้</para>
///
/// <para>สองทิศ: ไฟล์หลักฐานที่ผู้ใช้แนบจริง (PDF · รูป · Excel · Word · CSV · ZIP) ต้อง<b>ผ่าน</b> ·
/// ของปลอม (HTML/SVG ในชื่อ .csv · ไบต์ข้อความในชื่อ .pdf · OLE อื่น) ต้อง<b>ถูกปฏิเสธ</b> ·
/// และ <see cref="UploadFileType.Sniff"/> ของเส้น CMS/โลโก้ต้อง<b>ไม่ถูกแตะ</b> (ยังไม่รับ ZIP)</para>
/// </summary>
public class UploadFileTypeAttachmentTests
{
    private static byte[] B(params int[] b) => b.Select(x => (byte)x).ToArray();
    private static byte[] T(string s) => Encoding.UTF8.GetBytes(s);

    private static readonly byte[] Pdf = T("%PDF-1.7\n%âãÏÓ");
    private static readonly byte[] Png = B(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D);
    private static readonly byte[] Zip = B(0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00);
    private static readonly byte[] Ole = B(0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0);
    private static readonly byte[] Ico = B(0x00, 0x00, 0x01, 0x00, 0x01, 0x00);

    // ───────── ทิศที่ 1: หลักฐานจริงต้องผ่าน ─────────

    [Fact] public void Pdf_Accepted() => Assert.Equal(".pdf", UploadFileType.SniffAttachment(Pdf, "ใบเสร็จ.pdf")?.Extension);

    [Fact]
    public void Image_Accepted_AsImage()
    {
        var k = UploadFileType.SniffAttachment(Png, "slip.PNG");
        Assert.Equal(".png", k?.Extension);
        Assert.True(k?.IsImage);
    }

    [Theory]
    [InlineData("statement.xlsx", ".xlsx")]
    [InlineData("สัญญา.DOCX", ".docx")]
    [InlineData("evidence.zip", ".zip")]
    public void ZipFamily_SubtypeFromName(string name, string ext)
        => Assert.Equal(ext, UploadFileType.SniffAttachment(Zip, name)?.Extension);

    [Theory]
    [InlineData("old.xls", ".xls")]
    [InlineData("old.doc", ".doc")]
    public void LegacyOffice_Accepted(string name, string ext)
        => Assert.Equal(ext, UploadFileType.SniffAttachment(Ole, name)?.Extension);

    [Fact]
    public void Csv_WithBom_Accepted_AsTextCsv()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(T("วันที่,ยอด\n2026-09-01,1705.00")).ToArray();
        var k = UploadFileType.SniffAttachment(bytes.Take(UploadFileType.HeaderBytes).ToArray(), "bank.csv");
        Assert.Equal(".csv", k?.Extension);
        Assert.Equal("text/csv", k?.ContentType);
    }

    [Fact] public void Rar_Accepted() => Assert.Equal(".rar", UploadFileType.SniffAttachment(B('R', 'a', 'r', '!', 0x1A, 0x07, 0x01, 0x00), "a.rar")?.Extension);

    [Fact] public void SevenZip_Accepted() => Assert.Equal(".7z", UploadFileType.SniffAttachment(B(0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4), "a.7z")?.Extension);

    // ───────── ทิศที่ 2: ของปลอมต้องถูกปฏิเสธ ─────────

    [Theory]
    [InlineData("<html><script>alert(1)</script>", "evil.csv")]
    [InlineData("  \n<svg onload=alert(1)>", "evil.txt")]
    [InlineData("<?xml version=\"1.0\"?>", "data.csv")]
    public void Markup_DisguisedAsText_Rejected(string content, string name)
        => Assert.Null(UploadFileType.SniffAttachment(T(content), name));

    [Fact] public void TextBytes_NamedPdf_Rejected() => Assert.Null(UploadFileType.SniffAttachment(T("hello world"), "invoice.pdf"));

    [Fact] public void Csv_NamedHtml_Rejected() => Assert.Null(UploadFileType.SniffAttachment(T("a,b,c"), "page.html"));

    [Fact] public void BinaryWithNul_NamedTxt_Rejected() => Assert.Null(UploadFileType.SniffAttachment(B('M', 'Z', 0x90, 0x00, 0x03, 0x00), "readme.txt"));

    [Fact] public void OtherOle_Rejected() => Assert.Null(UploadFileType.SniffAttachment(Ole, "setup.msi"));

    [Fact] public void Icon_NotEvidence_Rejected() => Assert.Null(UploadFileType.SniffAttachment(Ico, "favicon.ico"));

    [Fact] public void Empty_Rejected() => Assert.Null(UploadFileType.SniffAttachment(Array.Empty<byte>(), "x.csv"));

    [Fact]
    public void ZipWithForeignName_StoredAsZip_NotClientExtension()
    {
        // ไบต์ยืนยันว่าเป็น ZIP แต่ชื่อบอก .exe — เก็บเป็น .zip ห้ามใช้นามสกุลของ client
        Assert.Equal(".zip", UploadFileType.SniffAttachment(Zip, "setup.exe")?.Extension);
    }

    // ───────── ทิศที่ 3: เส้นเดิม (CMS/โลโก้) ต้องไม่ถูกแตะ ─────────

    [Fact]
    public void Sniff_ForCms_StillRejectsZipAndText()
    {
        Assert.Null(UploadFileType.Sniff(Zip));
        Assert.Null(UploadFileType.Sniff(T("a,b,c,d")));
        Assert.Equal(".ico", UploadFileType.Sniff(Ico)?.Extension);   // โลโก้แพลตฟอร์มยังรับ ICO
    }

    [Theory]
    [InlineData(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData(".csv", "text/csv")]
    [InlineData(".pdf", "application/pdf")]
    [InlineData(".exe", "application/octet-stream")]
    public void ContentType_FromExtension(string ext, string mime)
        => Assert.Equal(mime, UploadFileType.ContentTypeForExtension(ext));
}
