using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ลบไฟล์แนบ = ลบไฟล์จริง หรือถอดจากรายการแต่เก็บไฟล์ (พ.ร.บ.การบัญชี ม.10 · §87/3) — ฝ่ายค้านรอบ 190
/// สองครึ่ง: หลักฐานของรายการบัญชีต้องไม่หาย · ของที่ไม่ใช่หลักฐาน (ใบร่าง · ผู้ติดต่อ · สินค้า) ยังลบจริงได้เหมือนเดิม
/// </summary>
public class AttachmentRetentionTests
{
    [Theory]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Paid)]
    [InlineData(DocumentStatus.Voided)]
    public void ใบที่ออกไปแล้ว_ต้องเก็บไฟล์จริง(DocumentStatus status)
        => Assert.True(AttachmentRetention.MustKeepPhysicalFile("Document", status));

    [Theory]
    [InlineData("JournalEntry")]
    [InlineData("PayrollRun")]
    [InlineData("ExpenseClaim")]
    [InlineData("BankTransaction")]
    [InlineData("ชนิดที่ยังไม่มีใครรู้จัก")]
    [InlineData(null)]
    public void หลักฐานบัญชีชนิดอื่น_และชนิดที่ไม่รู้จัก_ต้องเก็บ(string? entityType)
        => Assert.True(AttachmentRetention.MustKeepPhysicalFile(entityType, null));

    [Fact]
    public void เอกสารที่หาไม่เจอ_ไม่รู้สถานะ_ต้องเก็บ()
        => Assert.True(AttachmentRetention.MustKeepPhysicalFile("Document", null));

    [Fact]
    public void ทิศตรงข้าม_ใบร่าง_ลบไฟล์จริงได้เหมือนเดิม()
        => Assert.False(AttachmentRetention.MustKeepPhysicalFile("Document", DocumentStatus.Draft));

    [Theory]
    [InlineData("contacts")]
    [InlineData("products")]
    public void ทิศตรงข้าม_ข้อมูลหลักที่ไม่ใช่หลักฐานบัญชี_ลบไฟล์จริงได้(string entityType)
        => Assert.False(AttachmentRetention.MustKeepPhysicalFile(entityType, null));
}
