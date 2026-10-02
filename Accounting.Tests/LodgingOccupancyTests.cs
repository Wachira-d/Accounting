using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 202 ทีม LO — คำตัดสินเจ้าของข้อ 123 (คนเสริม) / 124 (เด็ก/ทารกไม่นับความจุ) · สองทิศ</summary>
public class LodgingOccupancyTests
{
    [Fact]
    public void ครอบครัว2ผู้ใหญ่3เด็ก_ห้องรับผู้ใหญ่2_จองได้_เด็กไม่นับความจุ()
    {
        Assert.Null(LodgingOccupancy.AdultProblem("Deluxe", adults: 2, maxAdults: 2, allowExtraBed: false, maxExtraBeds: 0, extraBedPrice: 0m));
        Assert.Null(LodgingOccupancy.ExtraBedProblem("Deluxe", extraGuests: 0, allowExtraBed: false, maxExtraBeds: 0, extraBedPrice: 0m));
    }

    [Fact]
    public void ผู้ใหญ่เกิน_ปฏิเสธพร้อมบอกทางซื้อคนเสริม()
    {
        var err = LodgingOccupancy.AdultProblem("Deluxe", 3, 2, true, 1, 600m);
        Assert.NotNull(err);
        Assert.Contains("ผู้ใหญ่สูงสุด 2", err);
        Assert.Contains("คนเสริม", err);
    }

    [Fact]
    public void คนเสริมในเพดาน_ผ่าน()
        => Assert.Null(LodgingOccupancy.ExtraBedProblem("Deluxe", 1, true, 1, 600m));

    [Fact]
    public void คนเสริมเกินเพดาน_ปฏิเสธ_ไม่ตัดทิ้งเงียบ()
        => Assert.Contains("สูงสุด 1 คน", LodgingOccupancy.ExtraBedProblem("Deluxe", 2, true, 1, 600m));

    [Fact]
    public void ห้องไม่รับคนเสริม_ขอมา_ปฏิเสธ()
        => Assert.Contains("ไม่รับคนเสริม", LodgingOccupancy.ExtraBedProblem("Standard", 1, false, 0, 0m));

    [Fact]
    public void รวมผู้เข้าพัก_คนเสริมแยกจากผู้ใหญ่_ไม่ซ้อนนับ()
    {
        var t = LodgingOccupancy.Totals(2, 1, 1, 1);
        Assert.Equal(5, t.Total);
        Assert.Equal("รวม 5 คน (ผู้ใหญ่ 2 · เด็ก 1 · ทารก 1 · คนเสริม 1)", LodgingOccupancy.Summary(t));
        Assert.Equal("รวม 2 คน (ผู้ใหญ่ 2)", LodgingOccupancy.Summary(LodgingOccupancy.Totals(2, 0, 0, 0)));
        Assert.Equal(0, LodgingOccupancy.Totals(-1, -1, -1, -1).Total);
    }

    [Fact]
    public void ผลค้นหา_ผู้ใหญ่สูงสุดรวมคนเสริมที่ซื้อได้()
    {
        Assert.Equal(3, LodgingOccupancy.MaxAdultsWithExtras(2, true, 1, 600m));
        Assert.Equal(2, LodgingOccupancy.MaxAdultsWithExtras(2, false, 1, 600m));   // ตั้งจำนวนไว้แต่ไม่เปิดเตียงเสริม = ไม่นับ
        Assert.Equal(2, LodgingOccupancy.MaxAdultsWithExtras(2, true, 1, null));    // P2-5 ราคาว่าง = ไม่ขายคนเสริม
    }

    [Fact]
    public void บริการต่อคนนับคนเสริมแต่ไม่นับทารก()
        => Assert.Equal(4, LodgingOccupancy.ChargeableGuests(2, 1, 1));

    [Fact]
    public void ราคาคนเสริม_คนละคืน_ผ่านengineเดิม_ไม่คิดซ้ำเป็นแขกเกิน()
    {
        // ห้องราคาต่อห้อง มาตรฐาน 2 คน · แขกเกิน 500/คน/คืน · คนเสริม 800/คน/คืน · 2 ผู้ใหญ่ + คนเสริม 1 · 2 คืน
        var input = new LodgingRoomPricingInput(1500m, LodgingPricingMode.PerUnit, 2, 500m, 800m, 1m, 0,
            Array.Empty<LodgingSeasonInput>(), Array.Empty<LodgingOverrideInput>(), null);
        var mon = new DateTime(2026, 9, 7);
        var q = LodgingPricingEngine.QuoteRoom(mon, mon.AddDays(2), 2, 0, 1, input);
        Assert.Equal(0m, q.ExtraGuestCharge);          // คนเสริมไม่ถูกนับเป็นแขกเกินมาตรฐานซ้ำ
        Assert.Equal(1600m, q.ExtraBedCharge);         // 800 × 1 คน × 2 คืน
    }

    // ── ฝ่ายค้านรอบ 202 P2-5: เปิดเตียงเสริมแต่ราคาว่าง (แถวก่อนด่านบันทึก) = ไม่ขาย ไม่ใช่ฟรี ──

    [Fact]
    public void เปิดเตียงเสริมแต่ราคาว่าง_ไม่ขายคนเสริม()
    {
        Assert.False(LodgingOccupancy.SellsExtraBeds(true, 2, null));
        Assert.NotNull(LodgingOccupancy.ExtraBedProblem("Deluxe", 1, true, 2, null));
    }

    [Fact]
    public void ราคาศูนย์ตั้งใจ_ขายได้_ไม่คิดเงิน()
    {
        Assert.True(LodgingOccupancy.SellsExtraBeds(true, 2, 0m));
        Assert.Null(LodgingOccupancy.ExtraBedProblem("Deluxe", 1, true, 2, 0m));
    }

    [Fact]
    public void ปัญหาผู้ใหญ่กับปัญหาคนเสริมแยกกัน_ไม่มีคนเสริม_ไม่มีปัญหาคนเสริม()
    {
        Assert.Null(LodgingOccupancy.ExtraBedProblem("Standard", 0, false, 0, null));
        Assert.NotNull(LodgingOccupancy.AdultProblem("Standard", 3, 2, false, 0, null));
        Assert.Null(LodgingOccupancy.AdultProblem("Standard", 2, 2, false, 0, null));
    }

    [Fact]
    public void ราคาคนเสริมเดิม_ย้อนจากยอดในsnapshot()
    {
        Assert.Equal(800m, LodgingOccupancy.PerPersonNightPrice(1600m, 1, 2));
        Assert.Equal(333.33m, LodgingOccupancy.PerPersonNightPrice(1000m, 1, 3));
        Assert.Null(LodgingOccupancy.PerPersonNightPrice(1600m, 0, 2));   // ไม่มีคนเสริม = ไม่มีราคาเดิม (ผู้เรียกบล็อก ไม่เดา)
        Assert.Null(LodgingOccupancy.PerPersonNightPrice(1600m, 1, 0));
    }
}
