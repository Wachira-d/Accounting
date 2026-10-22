using Accounting.Helpers;
using Accounting.Models.DTOs.Lodging;
using Xunit;

namespace Accounting.Tests;

/// <summary>2026-10-22 ทีม D — หน้า "ห้องพัก" สาธารณะ: ป้ายรายละเอียดห้องที่เซิร์ฟเวอร์คำนวณ (กติกาห้อง · เด็ก · นโยบายยกเลิก)
/// + ช่องใหม่ (สูบบุหรี่/สัตว์เลี้ยง/หมายเหตุห้อง) ต้องไหลผ่าน projection สาธารณะ (ไม่หายเงียบ) โดยข้อมูลภายในยังถูกตัด</summary>
public class LodgingRoomDetailPageTests
{
    [Fact]
    public void ป้ายกติกาห้อง_บอกทั้งสองทิศเสมอ()
    {
        Assert.Equal(new[] { "ห้องปลอดบุหรี่ (ห้ามสูบบุหรี่)", "ไม่รับสัตว์เลี้ยง" }, LodgingPublicRoomLabels.HouseRuleLabels(false, false));
        Assert.Equal(new[] { "สูบบุหรี่ได้", "รับสัตว์เลี้ยง" }, LodgingPublicRoomLabels.HouseRuleLabels(true, true));
    }

    [Fact]
    public void ป้ายเด็ก_ศูนย์ไม่แต่งป้าย()
    {
        Assert.Equal("เด็กพักได้สูงสุด 2 คน/ห้อง", LodgingPublicRoomLabels.ChildrenLabel(2));
        Assert.Null(LodgingPublicRoomLabels.ChildrenLabel(0));
    }

    [Fact]
    public void สรุปนโยบายยกเลิก_ตรงกับเครื่องคิดค่าปรับทุกขั้น()
    {
        var rules = new[] { new LodgingCancellationRule(3, 50m), new LodgingCancellationRule(7, 0m), new LodgingCancellationRule(0, 100m) };
        var text = LodgingPublicRoomLabels.CancellationSummary(false, rules);
        Assert.Equal("ยกเลิกล่วงหน้ามากกว่า 7 วัน ไม่มีค่าธรรมเนียม · ยกเลิกล่วงหน้ามากกว่า 3 วัน คิด 50% ของยอดการจอง · ยกเลิกภายใน 3 วันก่อนเข้าพัก คิด 100% ของยอดการจอง", text);

        // ล็อกความหมายกับ engine จริง — ป้ายพูดอย่าง เครื่องคิดเงินทำอีกอย่าง = หลอกแขก
        var checkIn = new DateTime(2026, 11, 20);
        decimal Fee(int daysBefore) => LodgingPricingEngine.CancellationFee(1000m, checkIn, checkIn.AddDays(-daysBefore), rules, false).Fee;
        Assert.Equal(0m, Fee(8));      // มากกว่า 7 วัน ⇒ ฟรี
        Assert.Equal(500m, Fee(7));    // 7 วันพอดี = ไม่ "มากกว่า 7" ⇒ ขั้น 50%
        Assert.Equal(500m, Fee(4));
        Assert.Equal(1000m, Fee(3));   // ภายใน 3 วัน ⇒ 100%
        Assert.Equal(1000m, Fee(0));
    }

    [Fact]
    public void สรุปนโยบายยกเลิก_ไม่คืนเงิน_ขั้นเดียว_และไม่มีกฎ()
    {
        Assert.Equal("ไม่คืนเงินทุกกรณี", LodgingPublicRoomLabels.CancellationSummary(true, new[] { new LodgingCancellationRule(7, 0m) }));
        Assert.Equal("ยกเลิกเมื่อใดก็ตาม คิด 20% ของยอดการจอง", LodgingPublicRoomLabels.CancellationSummary(false, new[] { new LodgingCancellationRule(5, 20m) }));
        Assert.Null(LodgingPublicRoomLabels.CancellationSummary(false, Array.Empty<LodgingCancellationRule>()));
    }

    [Fact]
    public void ช่องรายละเอียดห้องใหม่_ไหลถึงหน้าสาธารณะ_ข้อมูลภายในยังถูกตัด()
    {
        var src = new LodgingRoomTypeDto
        {
            Id = Guid.NewGuid(), Name = "Deluxe", IsActive = true, MaxAdults = 2, MaxChildren = 1,
            SmokingAllowed = false, PetsAllowed = true, RoomNote = "ชั้น 3 ไม่มีลิฟต์", ProductId = Guid.NewGuid(),
            Units = new List<LodgingUnitDto> { new() { Number = "301", Notes = "ภายใน" } },
        };
        var rt = Assert.Single(LodgingPublicProjection.RoomTypes(new[] { src }));
        Assert.False(rt.SmokingAllowed);
        Assert.True(rt.PetsAllowed);
        Assert.Equal("ชั้น 3 ไม่มีลิฟต์", rt.RoomNote);
        Assert.Equal(new[] { "ห้องปลอดบุหรี่ (ห้ามสูบบุหรี่)", "รับสัตว์เลี้ยง" }, rt.HouseRuleLabels);
        Assert.Equal("เด็กพักได้สูงสุด 1 คน/ห้อง", rt.ChildrenLabel);
        Assert.Empty(rt.Units);
        Assert.Null(rt.ProductId);
    }
}
