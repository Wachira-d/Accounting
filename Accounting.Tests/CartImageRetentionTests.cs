using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// อายุรูปตัวอย่างของตะกร้าที่ไม่ได้สั่ง (<see cref="CartImageRetention"/> · ทีม W4 · R-25) — ตัวตัดสินของ <c>CartImageCleanupJob</c>
///
/// <para>ทิศตรงข้าม: รูปของตะกร้าที่กลายเป็นออเดอร์ไม่ถูกลบไม่ว่าอายุเท่าไร · รูปที่ยังไม่ครบอายุไม่ถูกลบ · ค่าตั้งผิดช่วงตกเป็นค่าเริ่มต้น 14 วัน</para>
/// </summary>
public class CartImageRetentionTests
{
    private static readonly System.DateTime Now = new(2026, 10, 26, 3, 0, 0, System.DateTimeKind.Utc);

    [Theory]
    [InlineData(null, 14)]
    [InlineData(0, 14)]
    [InlineData(-3, 14)]
    [InlineData(366, 14)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(365, 365)]
    public void NormalizeDays_ค่าตั้งผิดช่วงตกเป็น14(int? configured, int expected) =>
        Assert.Equal(expected, CartImageRetention.NormalizeDays(configured));

    [Fact]
    public void Cutoff_ย้อนตามจำนวนวัน()
    {
        Assert.Equal(Now.AddDays(-14), CartImageRetention.Cutoff(Now, 14));
        Assert.Equal(Now.AddDays(-14), CartImageRetention.Cutoff(Now, 0)); // ค่าผิด ⇒ 14 ไม่ใช่ลบทุกรูปทันที
    }

    [Fact]
    public void รูปตะกร้าเก่ากว่าเวลาตัด_ลบได้()
    {
        var cutoff = CartImageRetention.Cutoff(Now, 14);
        Assert.True(CartImageRetention.ShouldDelete(Now.AddDays(-15), belongsToOrder: false, cutoff));
    }

    [Fact]
    public void ทิศตรงข้าม_รูปของออเดอร์_ไม่ถูกลบแม้เก่ามาก()
    {
        var cutoff = CartImageRetention.Cutoff(Now, 14);
        Assert.False(CartImageRetention.ShouldDelete(Now.AddYears(-3), belongsToOrder: true, cutoff));
    }

    [Fact]
    public void ทิศตรงข้าม_รูปตะกร้าที่ยังไม่ครบอายุ_ไม่ถูกลบ()
    {
        var cutoff = CartImageRetention.Cutoff(Now, 14);
        Assert.False(CartImageRetention.ShouldDelete(Now.AddDays(-13), belongsToOrder: false, cutoff));
        Assert.False(CartImageRetention.ShouldDelete(cutoff, belongsToOrder: false, cutoff)); // เท่าเวลาตัดพอดี = ยังไม่หมดอายุ
    }
}
