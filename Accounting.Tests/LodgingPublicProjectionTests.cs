using Accounting.Helpers;
using Accounting.Models.DTOs.Lodging;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 200 ทีม R · F-05 — ข้อมูลที่พักบนหน้าจองสาธารณะต้องไม่มีข้อมูลภายในรายห้อง
/// สองทิศ: เลขห้อง/หมายเหตุ/สถานะแม่บ้านหาย · ข้อมูลที่แขกต้องใช้คิดราคา/เลือกห้องยังครบ</summary>
public class LodgingPublicProjectionTests
{
    private static LodgingRoomTypeDto Deluxe() => new()
    {
        Id = Guid.NewGuid(), Name = "Deluxe", BaseRate = 1800m, MaxAdults = 2, MaxChildren = 1,
        PricingMode = Accounting.Models.Enums.LodgingPricingMode.PerUnit, ExtraGuestPrice = 400m, UnitCount = 2,
        ProductId = Guid.NewGuid(), IsActive = true,
        Units = new List<LodgingUnitDto>
        {
            new() { Number = "305", Notes = "แอร์เสีย ลูกค้าร้องเรียน" },
            new() { Number = "306" },
        },
    };

    [Fact]
    public void รายการห้องและข้อมูลภายใน_ไม่หลุดถึงหน้าสาธารณะ()
    {
        var pub = LodgingPublicProjection.RoomTypes(new[] { Deluxe() });
        var rt = Assert.Single(pub);
        Assert.Empty(rt.Units);
        Assert.Null(rt.ProductId);
    }

    [Fact]
    public void ประเภทห้องที่ปิดขาย_ไม่ถูกส่ง()
    {
        var closed = Deluxe(); closed.IsActive = false; closed.Name = "Old";
        var pub = LodgingPublicProjection.RoomTypes(new[] { Deluxe(), closed });
        Assert.DoesNotContain(pub, r => r.Name == "Old");
    }

    [Fact]
    public void ข้อมูลที่แขกใช้คิดราคาและเลือกห้อง_ยังครบ()
    {
        var src = Deluxe();
        var rt = Assert.Single(LodgingPublicProjection.RoomTypes(new[] { src }));
        Assert.Equal(src.Id, rt.Id);
        Assert.Equal(1800m, rt.BaseRate);
        Assert.Equal(400m, rt.ExtraGuestPrice);
        Assert.Equal(2, rt.MaxAdults);
        Assert.Equal(src.PricingMode, rt.PricingMode);
        Assert.Equal(2, rt.UnitCount);
        // ต้นทางไม่ถูกแก้ (หน้าตั้งค่าใช้ DTO ชุดเดิม)
        Assert.Equal(2, src.Units.Count);
    }
}
