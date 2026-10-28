using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 206 (R-31 · คำตัดสินข้อ 165) — ริบมัดจำเมื่อยกเลิกออเดอร์ตาม % ของร้าน · ไม่ตั้ง = ไม่ริบ (เดิม) · เงินประกัน/VAT รอ ไม่ริบเอง · ริบจากฐานคงเหลือ</summary>
public class SiteOrderDepositForfeitTests
{
    private static LodgingDepositSnapshot Dep(decimal subTotal, decimal vat, decimal realized = 0m, decimal refunded = 0m,
        bool vatPending = false, DepositNature? nature = null)
        => new(Guid.NewGuid(), "DEP-1", Guid.NewGuid(), subTotal, vat, subTotal + vat, vatPending, realized, refunded, null, nature);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void ไม่ตั้งหรือศูนย์_ไม่ริบ_พฤติกรรมเดิม(int? pct)
    {
        Assert.False(SiteOrderDepositForfeit.Enabled(pct));
        Assert.Empty(SiteOrderDepositForfeit.Plan(pct, new[] { Dep(934.58m, 65.42m) }));
    }

    [Fact]
    public void ริบ50เปอร์เซ็นต์_จากฐานคงเหลือ_ที่เหลือรอคืน()
    {
        var lines = SiteOrderDepositForfeit.Plan(50m, new[] { Dep(934.58m, 65.42m) });
        var l = Assert.Single(lines);
        Assert.Equal(467.29m, l.ForfeitBase);
        Assert.Equal(500m, l.ForfeitGross);
        Assert.Equal(500m, l.RefundGross);
    }

    [Fact]
    public void ริบ100_ไม่มีอะไรรอคืน()
    {
        var l = Assert.Single(SiteOrderDepositForfeit.Plan(100m, new[] { Dep(934.58m, 65.42m) }));
        Assert.Equal(934.58m, l.ForfeitBase);
        Assert.Equal(0m, l.RefundGross);
    }

    [Fact]
    public void รับรู้ไปแล้วบางส่วน_ริบจากส่วนที่เหลือเท่านั้น()
    {
        var l = Assert.Single(SiteOrderDepositForfeit.Plan(100m, new[] { Dep(1000m, 0m, realized: 400m) }));
        Assert.Equal(600m, l.ForfeitBase);
    }

    [Fact]
    public void เงินประกันและใบที่VATยังรอ_ไม่ริบเอง()
    {
        var plan = SiteOrderDepositForfeit.Plan(50m, new[]
        {
            Dep(1000m, 0m, nature: DepositNature.RefundableSecurity),
            Dep(1000m, 0m, vatPending: true),
        });
        Assert.Empty(plan);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(12.5, true)]
    [InlineData(101, false)]
    [InlineData(-1, false)]
    [InlineData(10.123, false)]
    public void ValidatePercent(double? pct, bool ok)
        => Assert.Equal(ok, SiteOrderDepositForfeit.ValidatePercent(pct is null ? null : (decimal)pct.Value) == null);

    [Fact]
    public void หมายเหตุ_บอกใบ_ยอดริบ_รอคืน_และว่าระบบไม่คืนเอง()
    {
        var l = SiteOrderDepositForfeit.Plan(50m, new[] { Dep(934.58m, 65.42m) });
        var note = SiteOrderDepositForfeit.Note(l, 50m, DateTime.UtcNow);
        Assert.Contains("DEP-1", note);
        Assert.Contains("500.00", note);
        Assert.Contains("ไม่คืนเอง", note);
    }
}
