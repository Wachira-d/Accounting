using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เพดานพื้นที่ของไฟล์แนบ = เตือน ไม่บล็อก (คำตัดสินเจ้าของ รอบ 193 ข้อ 30)
/// ครึ่งที่ 1: เกินเพดาน/ใกล้เต็ม ต้องมีคำเตือนที่บอกตัวเลขจริง
/// ครึ่งที่ 2: ไม่เกิน → ไม่รบกวน · ไม่รู้เพดาน (ไม่มี subscription/เพดาน 0) → ไม่เตือน และห้ามแสดงเป็น "0 จาก 0"
/// </summary>
public class AttachmentStorageNoticeTests
{
    private const long Mb = 1024 * 1024;

    [Fact]
    public void เกินเพดาน_บันทึกแล้วแต่เตือนพร้อมตัวเลข()
    {
        Assert.Equal(AttachmentStorageLevel.Over, AttachmentStorageNotice.Evaluate(101 * Mb, 100 * Mb));
        var msg = AttachmentStorageNotice.Message(101 * Mb, 100 * Mb);
        Assert.NotNull(msg);
        Assert.Contains("บันทึกไฟล์แล้ว", msg);
        Assert.Contains("101 MB", msg);
        Assert.Contains("100 MB", msg);
    }

    [Fact]
    public void ใกล้เต็ม_เก้าสิบเปอร์เซ็นต์ขึ้นไป_เตือนล่วงหน้า()
    {
        Assert.Equal(AttachmentStorageLevel.Near, AttachmentStorageNotice.Evaluate(90 * Mb, 100 * Mb));
        Assert.Equal(AttachmentStorageLevel.Near, AttachmentStorageNotice.Evaluate(100 * Mb, 100 * Mb)); // เท่าเพดานพอดี = ยังไม่เกิน
        Assert.Contains("ใกล้เต็ม", AttachmentStorageNotice.Message(95 * Mb, 100 * Mb));
    }

    [Fact]
    public void ยังไม่ถึงเกณฑ์_ไม่มีคำเตือน()
    {
        Assert.Equal(AttachmentStorageLevel.Ok, AttachmentStorageNotice.Evaluate(10 * Mb, 100 * Mb));
        Assert.Null(AttachmentStorageNotice.Message(89 * Mb, 100 * Mb));
    }

    [Theory]
    [InlineData(null, 100L)]
    [InlineData(5L, null)]
    [InlineData(5L, 0L)]
    [InlineData(5L, -1L)]
    [InlineData(-1L, 100L)]
    public void ไม่รู้เพดานหรือการใช้งาน_ไม่เตือนและไม่แต่งตัวเลข(long? used, long? max)
    {
        Assert.Equal(AttachmentStorageLevel.Unknown, AttachmentStorageNotice.Evaluate(used, max));
        Assert.Null(AttachmentStorageNotice.Message(used, max));
    }
}
