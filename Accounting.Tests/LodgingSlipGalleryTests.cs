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

    // ═══ รอบ 205 W5 (R-52): ยอดที่แขกกรอกตอนส่งสลิป — ไม่ใช่หลักฐาน · แสดงคู่ยอดเซิร์ฟเวอร์ · ไม่แต่งค่า ═══

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, null)]            // 0 = ไม่ได้กรอก (ไม่รู้ ≠ 0)
    [InlineData(2500.004, 2500.0)]
    [InlineData(2500.005, 2500.01)]    // AwayFromZero
    public void ยอดที่แขกกรอก_ไม่กรอกเป็นnull_ปัด2ตำแหน่ง(double? input, double? expected)
    {
        var (amount, problem) = LodgingSlipGallery.NormalizeDeclaredAmount((decimal?)input);
        Assert.Null(problem);
        Assert.Equal((decimal?)expected, amount);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(10_000_000.01)]
    public void ยอดที่แขกกรอก_ติดลบหรือเกินเพดาน_ปฏิเสธไม่เก็บ(double input)
    {
        var (amount, problem) = LodgingSlipGallery.NormalizeDeclaredAmount((decimal)input);
        Assert.Null(amount);
        Assert.NotNull(problem);
    }

    [Fact]
    public void ยอดที่แขกกรอก_สลิปปัจจุบันจากแถวการจอง_ใบก่อนจากaudit_ไม่มีคือnull()
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
            // a: แถว audit รุ่นก่อน (ไม่มี declaredAmount) · b: แขกกรอก 1,900
            new LodgingSlipEvent(LodgingSlipGallery.UploadedAction, "/uploads/lodging-slips/2026-10/a.jpg", null, "REF-A", T0),
            new LodgingSlipEvent(LodgingSlipGallery.UploadedAction, "/uploads/lodging-slips/2026-10/b.jpg", null, "REF-B", T0.AddHours(1), 1900m),
        };
        var items = LodgingSlipGallery.Build(files, "/uploads/lodging-slips/2026-10/c.jpg", T0.AddHours(2), "REF-C", events, currentDeclaredAmount: 2500m);
        Assert.Equal(2500m, items[0].DeclaredAmount);   // ปัจจุบัน — จากแถวการจอง
        Assert.Equal(1900m, items[1].DeclaredAmount);   // ใบก่อน — จาก audit ของใบนั้น
        Assert.Null(items[2].DeclaredAmount);           // รุ่นก่อน — ไม่แต่งค่า (ไม่ยกยอดของใบอื่นมา)
    }

    [Fact]
    public void ยอดที่แขกกรอก_ไม่ส่งมา_ทุกใบเป็นnull_ทิศตรงข้าม()
    {
        var key = "/uploads/lodging-slips/2026-10/a.jpg";
        var files = new[] { new LodgingSlipFileRow(Guid.NewGuid(), "/w" + key, "a.jpg", T0) };
        var only = Assert.Single(LodgingSlipGallery.Build(files, key, T0, null, Array.Empty<LodgingSlipEvent>()));
        Assert.Null(only.DeclaredAmount);
    }
}
