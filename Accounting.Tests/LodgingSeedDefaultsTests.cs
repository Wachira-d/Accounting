using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Cms;
using Xunit;

namespace Accounting.Tests;

/// <summary>หน้าเว็บที่ seed ให้เว็บที่พัก (HotelPlan) กับที่พักที่จองได้จริง (LodgingSeeder) ต้องเล่าเรื่องเดียวกัน.
/// ที่มา: รอบ 158 พบว่าหน้าเว็บบอก "Junior Suite ฿3,800 · 4 ประเภท · เช็คอิน 15:00 · ยกเลิกฟรีก่อน 3 วัน ·
/// ราคารวมอาหารเช้า" ขณะที่ที่พักจริงมี 3 ประเภท · 14:00 · 7 วัน · อาหารเช้าเป็นแผนราคาแยก —
/// เทสต์นี้อ่านผลจริงของ BuildSeed แล้วเทียบกับ LodgingSeedDefaults ทั้งสองทิศ (ต้องมี · ต้องไม่มี)</summary>
public class LodgingSeedDefaultsTests
{
    private static string HotelText()
    {
        var pages = CmsSiteTemplateSeeder.BuildSeed(Guid.NewGuid(), Guid.NewGuid(), IndustryType.Hotel, "test", SiteType.Booking);
        return string.Join("\n", pages.SelectMany(p => p.Blocks).Select(b => b.ConfigJson));
    }

    [Fact]
    public void ค่าตั้งต้น_3_ประเภทห้อง_11_ห้อง()
    {
        Assert.Equal(3, LodgingSeedDefaults.RoomTypes.Length);
        Assert.Equal(11, LodgingSeedDefaults.TotalUnits);
        Assert.Equal(LodgingSeedDefaults.RoomTypes.Length, LodgingSeedDefaults.RoomTypes.Select(r => r.Code).Distinct().Count());
    }

    [Fact]
    public void หน้าเว็บที่พัก_ต้องพิมพ์ทุกประเภทห้องพร้อมราคาจากค่าตั้งต้น()
    {
        var text = HotelText();
        foreach (var r in LodgingSeedDefaults.RoomTypes)
        {
            Assert.Contains(r.Name, text);
            Assert.Contains(LodgingSeedDefaults.Baht(r.Rate), text);
        }
        Assert.Contains(LodgingSeedDefaults.CheckIn, text);
        Assert.Contains(LodgingSeedDefaults.CheckOut, text);
        Assert.Contains($"{LodgingSeedDefaults.DepositPercent}%", text);
        Assert.Contains($"{LodgingSeedDefaults.FreeCancelDaysBefore} วัน", text);
    }

    [Fact]
    public void หน้าเว็บที่พัก_ต้องไม่มีตัวเลขที่ที่พักจริงไม่มี()
    {
        var text = HotelText();
        Assert.DoesNotContain("Junior Suite", text);
        Assert.DoesNotContain("3,800", text);
        Assert.DoesNotContain("15:00", text);
        Assert.DoesNotContain("4 ประเภท", text);
        Assert.DoesNotContain("รวมอาหารเช้า)", text);     // ราคามาตรฐานไม่รวมอาหารเช้า (BB เป็นแผนแยก)
        Assert.DoesNotContain("จองตรงรับส่วนลด", text);    // ส่วนลด 10% เป็นแผน "ไม่คืนเงิน" ไม่ใช่โปรจองตรง
        Assert.DoesNotContain("placehold.co", text);      // รูปปลอมของห้องที่ไม่มีจริง
    }

    [Fact]
    public void Baht_ใช้_InvariantCulture_เสมอ()
    {
        Assert.Equal("฿1,500", LodgingSeedDefaults.Baht(1500));
        Assert.Equal("฿4,500", LodgingSeedDefaults.Baht(4500m));
    }

    [Fact]
    public void นโยบายยกเลิก_JSON_ต้องเป็นขั้นบันไดตามค่าตั้งต้น()
    {
        using var doc = JsonDocument.Parse(LodgingSeedDefaults.CancellationRulesJson);
        var rules = doc.RootElement.EnumerateArray().Select(e => (e.GetProperty("daysBefore").GetInt32(), e.GetProperty("penaltyPercent").GetInt32())).ToList();
        Assert.Equal(new[] { (LodgingSeedDefaults.FreeCancelDaysBefore, 0), (LodgingSeedDefaults.HalfPenaltyDaysBefore, 50), (0, 100) }, rules);
    }

    [Fact]
    public void ส่วนลดแผนราคา_คิดจากเปอร์เซ็นต์เดียวกับที่หน้าเว็บพิมพ์()
    {
        Assert.Equal(0.90m, 1m - LodgingSeedDefaults.NonRefundableDiscountPercent / 100m);
        Assert.Equal(0.85m, 1m - LodgingSeedDefaults.LongStayDiscountPercent / 100m);
    }
}
