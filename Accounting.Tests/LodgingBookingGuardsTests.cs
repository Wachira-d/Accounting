using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 202 ทีม LO — ด่านการจองที่ไม่ใช่ราคา (O-P1-3 · O-P1-6/คำตัดสินข้อ 122 · P2 PromoCode/เลื่อนวัน · คำตัดสินข้อ 126) · สองทิศทุกด่าน</summary>
public class LodgingBookingGuardsTests
{
    private static readonly DateTime Today = new(2026, 10, 2);   // ศุกร์

    // ── O-P1-3 แผนราคา ──

    [Fact]
    public void แผนโปรนอกช่วง_แขกใช้ไม่ได้_พนักงานใช้ได้()
    {
        var applies = LodgingBookingGuards.RatePlanApplies(new DateTime(2026, 11, 1), new DateTime(2026, 11, 30), null, null, null, null, 0,
            Today.AddDays(5), 2, Today);
        Assert.False(applies);
        Assert.NotNull(LodgingBookingGuards.ExplicitRatePlanProblem(isStaff: false, applies, "Early Bird"));
        Assert.Null(LodgingBookingGuards.ExplicitRatePlanProblem(isStaff: true, applies, "Early Bird"));
    }

    [Fact]
    public void แผนในช่วงตรงเงื่อนไข_แขกใช้ได้()
    {
        var applies = LodgingBookingGuards.RatePlanApplies(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), 2, 7, 3, 60, 0,
            Today.AddDays(5), 2, Today);
        Assert.True(applies);
        Assert.Null(LodgingBookingGuards.ExplicitRatePlanProblem(false, applies, "Early Bird"));
    }

    [Theory]
    [InlineData(1, 2)]   // คืนน้อยกว่าขั้นต่ำ
    [InlineData(10, 2)]  // คืนเกินสูงสุด
    [InlineData(3, 1)]   // จองล่วงหน้าน้อยกว่าขั้นต่ำ (lead 1 < 3)
    public void เงื่อนไขคืนหรือการจองล่วงหน้าไม่ผ่าน_ไม่ใช้ได้(int nights, int leadDays)
        => Assert.False(LodgingBookingGuards.RatePlanApplies(null, null, 2, 7, 3, null, 0, Today.AddDays(leadDays), nights, Today));

    [Fact]
    public void แผนเฉพาะวันในสัปดาห์()
    {
        var satOnly = LodgingPricingEngine.DayMask(DayOfWeek.Saturday);
        Assert.True(LodgingBookingGuards.RatePlanApplies(null, null, null, null, null, null, satOnly, Today.AddDays(1), 1, Today));    // เสาร์
        Assert.False(LodgingBookingGuards.RatePlanApplies(null, null, null, null, null, null, satOnly, Today.AddDays(2), 1, Today));   // อาทิตย์
    }

    // ── P2 PromoCode ──

    [Fact]
    public void โค้ดส่วนลด_ไม่ว่าง_ปฏิเสธพร้อมข้อความ_ว่าง_ผ่าน()
    {
        Assert.NotNull(LodgingBookingGuards.PromoCodeProblem("SUMMER10"));
        Assert.Null(LodgingBookingGuards.PromoCodeProblem(null));
        Assert.Null(LodgingBookingGuards.PromoCodeProblem("  "));
    }

    // ── O-P1-6 · ข้อ 122 เพดานการจองที่ยังรอชำระ ──

    [Fact]
    public void เบอร์รูปแบบต่างกันนับเป็นคนเดียว_หลบเพดานไม่ได้()
    {
        var pending = new (string?, string?)[] { ("0812345678", null), ("081 234 5678", null), (null, "A@x.com"), ("0899999999", "b@x.com") };
        Assert.Equal(2, LodgingBookingGuards.CountPendingForGuest(pending, "+66812345678", null));
        Assert.Equal(1, LodgingBookingGuards.CountPendingForGuest(pending, null, " a@X.com "));
        Assert.Equal(3, LodgingBookingGuards.CountPendingForGuest(pending, "081-234-5678", "a@x.com"));
        Assert.Equal(0, LodgingBookingGuards.CountPendingForGuest(pending, null, null));
        Assert.Equal(0, LodgingBookingGuards.CountPendingForGuest(pending, " - ", null));   // เบอร์ที่ไม่มีตัวเลข = ไม่มีเบอร์ (ไม่จับทุกแถวที่ไม่มีเบอร์)
    }

    [Fact]
    public void ครบสามใบ_ปฏิเสธ_น้อยกว่าสาม_จองต่อได้()
    {
        Assert.Null(LodgingBookingGuards.PendingCapProblem(LodgingBookingGuards.MaxPendingPerGuest - 1));
        Assert.NotNull(LodgingBookingGuards.PendingCapProblem(LodgingBookingGuards.MaxPendingPerGuest));
        Assert.Equal(3, LodgingBookingGuards.MaxPendingPerGuest);   // คำตัดสินข้อ 122
    }

    [Fact]
    public void คีย์เพดานต่อIP_ไม่เก็บIPดิบ_ต่อเว็บ()
    {
        var site = Guid.NewGuid();
        var k1 = LodgingBookingGuards.PublicCreateRateKey(site, "203.0.113.7");
        Assert.DoesNotContain("203.0.113.7", k1);
        Assert.Equal(k1, LodgingBookingGuards.PublicCreateRateKey(site, "203.0.113.7"));
        Assert.NotEqual(k1, LodgingBookingGuards.PublicCreateRateKey(site, "203.0.113.8"));
        Assert.NotEqual(k1, LodgingBookingGuards.PublicCreateRateKey(Guid.NewGuid(), "203.0.113.7"));
    }

    // ── P2 เลื่อนวัน: จับคู่ด้วยกุญแจ ไม่ใช่ลำดับ ──

    [Fact]
    public void ห้องสลับลำดับเพราะตัวคิดราคาจัดกลุ่ม_จับคู่ถูกบรรทัด()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        // จองไว้ A,B,A — ตัวคิดราคาคืน A,A,B
        var pairs = LodgingBookingGuards.PairByKey(new[] { a, b, a }, new[] { a, a, b });
        Assert.Equal(new int?[] { 0, 2, 1 }, pairs);
    }

    [Fact]
    public void บริการเสริมหายจากรายการคิดราคา_ไม่เลื่อนไปเอาบรรทัดถัดไป()
    {
        var x = Guid.NewGuid(); var y = Guid.NewGuid();
        var pairs = LodgingBookingGuards.PairByKey(new[] { x, y }, new[] { y });
        Assert.Null(pairs[0]);
        Assert.Equal(0, pairs[1]);
    }

    // ── คำตัดสินข้อ 126 ค่าปรับไม่เกินมัดจำ ──

    [Fact]
    public void ค่าปรับเกินมัดจำ_จำกัดที่มัดจำ()
        => Assert.Equal(1000m, LodgingBookingGuards.CappedCancellationFee(6450m, 1000m));

    [Fact]
    public void ค่าปรับไม่เกินมัดจำ_คงเดิม_และไม่มีมัดจำ_ค่าปรับศูนย์()
    {
        Assert.Equal(400m, LodgingBookingGuards.CappedCancellationFee(400m, 1000m));
        Assert.Equal(0m, LodgingBookingGuards.CappedCancellationFee(400m, 0m));
        Assert.Equal(0m, LodgingBookingGuards.CappedCancellationFee(-5m, 1000m));
    }
}
