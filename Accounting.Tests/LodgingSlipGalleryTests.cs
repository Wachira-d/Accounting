using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// แกลเลอรีสลิปในโมดัลการจอง (ผู้ใช้รายงาน 2026-10-22 "ไม่มีที่ให้กดดูสลิปเลย") — ล็อก
/// (1) แปลงพาธดิสก์ของไฟล์แนบเป็น storage key แล้วยังต้องผ่านด่านพาธเดิม · (2) สลิปทุกใบแสดงครบพร้อมสถานะที่ถูก ·
/// (3) ทิศตรงข้าม: ใบที่ไม่ใช่สลิป/พาธแปลก ไม่หลุดเข้ารายการ และสลิปปัจจุบันไม่ถูกติดป้าย "ถูกปฏิเสธ"
/// </summary>
public class LodgingSlipGalleryTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 3, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("/app/wwwroot/uploads/lodging-slips/2026-10/a.jpg", "/uploads/lodging-slips/2026-10/a.jpg")]
    [InlineData("C:\\site\\wwwroot\\uploads\\lodging-slips\\2026-10\\b.webp", "/uploads/lodging-slips/2026-10/b.webp")]
    public void StorageKey_แปลงพาธดิสก์เป็นkey(string storage, string expected)
        => Assert.Equal(expected, LodgingSlipPath.StorageKey(storage));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/app/wwwroot/uploads/attachments/x.pdf")]
    [InlineData("/app/wwwroot/uploads/lodging-slips/")]
    public void StorageKey_ไม่ใช่ไฟล์สลิป_คืนnull(string? storage) => Assert.Null(LodgingSlipPath.StorageKey(storage));

    [Fact]
    public void StorageKey_ที่มีการไต่พาธ_ยังถูกด่านResolveปฏิเสธ()
    {
        var key = LodgingSlipPath.StorageKey("/app/wwwroot/uploads/lodging-slips/../../appsettings.json");
        Assert.NotNull(key);   // แปลงได้ — แต่ด่านพาธตัวเดียวต้องตีกลับ
        Assert.Null(LodgingSlipPath.Resolve(key, "/app/wwwroot", _ => true));
    }

    [Fact]
    public void สลิปสามใบ_ล่าสุดอยู่บน_ถูกปฏิเสธมีเหตุผล_ใบที่ถูกทับเป็นก่อนหน้า()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var files = new[]
        {
            new LodgingSlipFileRow(a, "/w/uploads/lodging-slips/2026-10/a.jpg", "a.jpg", T0),
            new LodgingSlipFileRow(b, "/w/uploads/lodging-slips/2026-10/b.jpg", "b.jpg", T0.AddHours(1)),
            new LodgingSlipFileRow(c, "/w/uploads/lodging-slips/2026-10/c.jpg", "c.jpg", T0.AddHours(2)),
        };
        var events = new[]
        {
            new LodgingSlipEvent(LodgingSlipGallery.RejectedAction, "/uploads/lodging-slips/2026-10/a.jpg", "ยอดไม่ตรง", null, T0.AddMinutes(30)),
            new LodgingSlipEvent(LodgingSlipGallery.UploadedAction, "/uploads/lodging-slips/2026-10/b.jpg", null, "REF-B", T0.AddHours(1)),
        };
        var items = LodgingSlipGallery.Build(files, "/uploads/lodging-slips/2026-10/c.jpg", T0.AddHours(2), "REF-C", events);

        Assert.Equal(3, items.Count);
        Assert.Equal(c, items[0].AttachmentId);
        Assert.Equal(LodgingSlipItemState.Current, items[0].State);
        Assert.Equal("REF-C", items[0].Reference);
        Assert.Equal(LodgingSlipItemState.Superseded, items[1].State);
        Assert.Equal("REF-B", items[1].Reference);
        Assert.Equal(LodgingSlipItemState.Rejected, items[2].State);
        Assert.Equal("ยอดไม่ตรง", items[2].RejectedReason);
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.StateLabel)));
    }

    [Fact]
    public void สลิปปัจจุบันของแถวรุ่นก่อนที่ไม่มีไฟล์แนบ_ยังแสดง_ผ่านเส้นเดิม()
    {
        var items = LodgingSlipGallery.Build(Array.Empty<LodgingSlipFileRow>(),
            "/uploads/lodging-slips/2026-01/old.jpg", T0, "R1", Array.Empty<LodgingSlipEvent>());
        var only = Assert.Single(items);
        Assert.Null(only.AttachmentId);
        Assert.Equal(LodgingSlipItemState.Current, only.State);
    }

    [Fact]
    public void ไฟล์แนบที่ไม่ใช่สลิป_และไฟล์ซ้ำkey_ไม่เข้ารายการ()
    {
        var files = new[]
        {
            new LodgingSlipFileRow(Guid.NewGuid(), "/w/uploads/attachments/contract.pdf", "contract.pdf", T0),
            new LodgingSlipFileRow(Guid.NewGuid(), "/w/uploads/lodging-slips/2026-10/a.jpg", "a.jpg", T0),
            new LodgingSlipFileRow(Guid.NewGuid(), "/w/uploads/lodging-slips/2026-10/a.jpg", "a.jpg", T0.AddSeconds(1)),
        };
        var items = LodgingSlipGallery.Build(files, null, null, null, Array.Empty<LodgingSlipEvent>());
        var only = Assert.Single(items);
        Assert.Equal("/uploads/lodging-slips/2026-10/a.jpg", only.StorageKey);
        Assert.Equal(LodgingSlipItemState.Superseded, only.State);
    }

    [Fact]
    public void สลิปปัจจุบันไม่ถูกติดป้ายปฏิเสธ_แม้key_เคยถูกปฏิเสธ()
    {
        // ทิศตรงข้าม: แขกส่งไฟล์ชื่อเดิมกลับมา (หรือแถว audit ชี้ key ผิด) — ใบที่การจองอ้างอยู่ต้องเป็น "ล่าสุด" ไม่ใช่ "ถูกปฏิเสธ"
        var key = "/uploads/lodging-slips/2026-10/a.jpg";
        var files = new[] { new LodgingSlipFileRow(Guid.NewGuid(), "/w" + key, "a.jpg", T0) };
        var events = new[] { new LodgingSlipEvent(LodgingSlipGallery.RejectedAction, key, "เก่า", null, T0) };
        var only = Assert.Single(LodgingSlipGallery.Build(files, key, T0, null, events));
        Assert.Equal(LodgingSlipItemState.Current, only.State);
        Assert.Null(only.RejectedReason);
    }
}
