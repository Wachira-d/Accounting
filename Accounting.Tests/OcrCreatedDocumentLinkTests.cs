using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ป้าย "สร้างแล้ว" บนการ์ดสแกน — ต้องเป็นจริงก็ต่อเมื่อ**ปลายทางยังอยู่**
/// (ผู้ใช้รายงาน 2026-09-18: ลบเอกสารแล้วการ์ดยังขึ้น "สร้างแล้ว" · กดแล้วไม่พบเอกสาร)
/// </summary>
public class OcrCreatedDocumentLinkTests
{
    private static readonly Guid DocId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void มีตัวชี้แต่เอกสารถูกลบ_คือตัวชี้ค้างที่ต้องล้าง()
        => Assert.True(OcrCreatedDocumentLink.IsDangling(DocId, documentStillExists: false));

    [Fact]
    public void มีตัวชี้และเอกสารยังอยู่_ห้ามไปล้างทิ้ง()
        // ทิศตรงข้าม: ถ้าด่านนี้ล้างมั่ว เอกสารที่ยังอยู่จะหลุดการเชื่อมกับสแกน
        // แล้วผู้ใช้สร้างเอกสารซ้ำจากกระดาษใบเดียวได้ (VAT ซ้ำใน ภ.พ.30)
        => Assert.False(OcrCreatedDocumentLink.IsDangling(DocId, documentStillExists: true));

    [Fact]
    public void ไม่มีตัวชี้_ไม่ใช่ตัวชี้ค้าง()
        => Assert.False(OcrCreatedDocumentLink.IsDangling(null, false));

    [Fact]
    public void ตัวชี้เป็น_Guid_ว่าง_ถือว่าไม่มีตัวชี้()
        // แถวเก่าบางแถวเก็บ Guid.Empty แทน null — ต้องไม่ถูกนับเป็นตัวชี้ค้าง
        // ไม่งั้นจะถูกไล่ "ล้าง" ซ้ำทุกครั้งที่เปิดหน้า โดยไม่มีอะไรเปลี่ยน
        => Assert.False(OcrCreatedDocumentLink.IsDangling(Guid.Empty, false));

    [Fact]
    public void ข้อความตอนล้างตัวชี้_ต้องบอกทางไปต่อ()
    {
        var note = OcrCreatedDocumentLink.UnlinkNote("PI-20260918-0001", new DateTime(2026, 9, 18, 3, 0, 0, DateTimeKind.Utc));
        Assert.Contains("PI-20260918-0001", note);
        Assert.Contains("สร้างใหม่ได้", note);
        Assert.Contains("10:00", note);   // แสดงเวลาไทย (UTC+7) ไม่ใช่ UTC ดิบ
    }

    [Fact]
    public void เอกสารไม่มีเลขที่_ข้อความต้องยังอ่านรู้เรื่อง()
        => Assert.Contains("(ไม่มีเลขที่)",
            OcrCreatedDocumentLink.UnlinkNote(null, DateTime.UtcNow));
}
