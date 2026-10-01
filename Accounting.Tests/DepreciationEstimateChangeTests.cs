using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม IN — A-IN3 (เปลี่ยนประมาณการค่าเสื่อมไปข้างหน้า · TFRS for NPAEs บทที่ 10 · คำตัดสินข้อ 38) +
/// C-6 (รายการตรวจปิดปีเตือนสินทรัพย์ที่ยังไม่ทบทวนอายุใช้งาน · คำตัดสินข้อ 79)
///
/// <para>สองทิศ: เคสที่เคยไม่มีทางไป (เปลี่ยนวิธีคิดหลังเริ่มคิดค่าเสื่อม) ทำได้และมีผลจากมูลค่าคงเหลือ · การยืนยันค่าเดิม
/// ไม่สร้างฐานใหม่ · สินทรัพย์ที่ทบทวนแล้ว/ซื้อในรอบนี้/ที่ดิน ไม่ถูกเตือน</para>
/// </summary>
public class DepreciationEstimateChangeTests
{
    // ── A-IN3 ────────────────────────────────────────────────────────────

    [Fact]
    public void เปลี่ยนวิธีจากเส้นตรงเป็นยอดลดลงสองเท่าหลังคิดไป24งวด_ทำได้()
        => Assert.Null(DepreciationEstimateChange.Problem(DepreciationMethod.StraightLine, DepreciationMethod.DoubleDecliningBalance,
            newLifeMonths: 60, newSalvage: 0m, elapsedMonths: 24, netBookValue: 72_000m, accountNonDepreciable: false));

    [Fact]
    public void Golden_ทุน120000_เส้นตรง60เดือน_คิดไป24งวด_เปลี่ยนเป็นยอดลดลงสองเท่า_งวดถัดไปคิดจากมูลค่าคงเหลือ()
    {
        // NBV หลัง 24 งวด = 120,000 − 24×2,000 = 72,000 · อายุคงเหลือ 36 · ฐาน 72,000
        var remainingBase = DepreciationEstimateChange.RemainingBase(72_000m, 0m);
        Assert.Equal(72_000m, remainingBase);
        // ยอดลดลงสองเท่าจากฐานคงเหลือ: 72,000 × 2/36 = 4,000 (ไม่ใช่ 120,000 × 2/60 = 4,000 โดยบังเอิญ — ดูงวดที่สอง)
        var rows = DepreciationSchedule.Build(DepreciationMethod.DoubleDecliningBalance, remainingBase, 0m, 36, new DateTime(2028, 1, 1));
        Assert.Equal(4_000m, rows[0].Amount);
        Assert.Equal(Math.Round(68_000m * 2m / 36m, 2, MidpointRounding.AwayFromZero), rows[1].Amount);
        // รวมทุกงวด = ฐานคงเหลือพอดี (งวดที่ลงไปแล้วไม่ถูกแตะ)
        Assert.Equal(72_000m, rows.Sum(r => r.Amount));
    }

    [Fact]
    public void ไม่มีค่าใดเปลี่ยน_คือยืนยันการทบทวน_ไม่ใช่การเปลี่ยนประมาณการ()
    {
        Assert.False(DepreciationEstimateChange.IsChange(DepreciationMethod.StraightLine, 60, 1_000m,
            DepreciationMethod.StraightLine, 60, 1_000m));
        Assert.True(DepreciationEstimateChange.IsChange(DepreciationMethod.StraightLine, 60, 1_000m,
            DepreciationMethod.DecliningBalance, 60, 1_000m));
    }

    [Fact]
    public void หยุดคิดค่าเสื่อม_ไม่ใช่การเปลี่ยนประมาณการ_ชี้ทางจำหน่าย()
        => Assert.Contains("จำหน่าย", DepreciationEstimateChange.Problem(DepreciationMethod.StraightLine, DepreciationMethod.None,
            60, 0m, 10, 50_000m, false));

    [Fact]
    public void ที่ดิน_ไม่มีอายุให้ทบทวน()
        => Assert.NotNull(DepreciationEstimateChange.Problem(DepreciationMethod.None, DepreciationMethod.None, 1, 0m, 0, 5_000_000m, true));

    [Fact]
    public void ผังที่ดินแต่ตั้งวิธีคิดค่าเสื่อมไว้_ปฏิเสธ()
        => Assert.NotNull(DepreciationEstimateChange.Problem(DepreciationMethod.StraightLine, DepreciationMethod.StraightLine,
            60, 0m, 5, 4_000_000m, true));

    [Theory]
    [InlineData(24, 0, 24)]      // อายุใหม่เท่ากับงวดที่คิดไปแล้ว
    [InlineData(0, 0, 0)]        // อายุ 0
    [InlineData(60, -1, 10)]     // ซากติดลบ
    [InlineData(60, 80_000, 10)] // ซากเกินมูลค่าคงเหลือ
    public void ค่าผิดรูป_ปฏิเสธ(int life, decimal salvage, int elapsed)
        => Assert.NotNull(DepreciationEstimateChange.Problem(DepreciationMethod.StraightLine, DepreciationMethod.StraightLine,
            life, salvage, elapsed, 72_000m, false));

    [Fact]
    public void ค่าตัวเลขนอกenum_ปฏิเสธ()
        => Assert.NotNull(DepreciationEstimateChange.Problem(DepreciationMethod.StraightLine, (DepreciationMethod)9,
            60, 0m, 10, 72_000m, false));

    [Fact]
    public void หน้าแก้ไข_ปฏิเสธพร้อมชี้ปุ่มปรับอายุ()
        => Assert.Contains("ปรับอายุการใช้งาน",
            FixedAssetValuationEdit.Problem(true, DepreciationMethod.StraightLine, 60, 0m, 50_000m, false));

    // ── C-6 ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 12)]
    [InlineData(4, 3)]
    [InlineData(10, 9)]
    [InlineData(0, 12)]
    [InlineData(13, 12)]
    public void เดือนสุดท้ายของรอบบัญชี(int startMonth, int endMonth)
    {
        Assert.True(UsefulLifeReview.IsFiscalYearEndMonth(endMonth, startMonth));
        Assert.False(UsefulLifeReview.IsFiscalYearEndMonth(endMonth == 12 ? 11 : endMonth + 1, startMonth));
    }

    [Fact]
    public void วันเริ่มรอบบัญชี_รอบเมษายน_เดือนมีนาคมเป็นของรอบที่เริ่มปีก่อน()
    {
        Assert.Equal(new DateTime(2025, 4, 1), UsefulLifeReview.FiscalYearStart(2026, 3, 4));
        Assert.Equal(new DateTime(2026, 1, 1), UsefulLifeReview.FiscalYearStart(2026, 12, 1));
    }

    private static readonly DateTime Fy2026 = new(2026, 1, 1);

    [Fact]
    public void ไม่เคยทบทวน_ซื้อก่อนรอบนี้_ต้องเตือน()
        => Assert.True(UsefulLifeReview.NeedsReview(AssetStatus.Active, DepreciationMethod.StraightLine, 60,
            new DateTime(2024, 5, 1), null, Fy2026));

    [Fact]
    public void ทบทวนในรอบก่อน_ต้องเตือนอีกในรอบนี้()
        => Assert.True(UsefulLifeReview.NeedsReview(AssetStatus.Active, DepreciationMethod.StraightLine, 60,
            new DateTime(2024, 5, 1), new DateTime(2025, 12, 20, 3, 0, 0, DateTimeKind.Utc), Fy2026));

    [Fact]
    public void ทบทวนในรอบนี้แล้ว_ไม่เตือน()
        => Assert.False(UsefulLifeReview.NeedsReview(AssetStatus.Active, DepreciationMethod.StraightLine, 60,
            new DateTime(2024, 5, 1), new DateTime(2026, 12, 30, 3, 0, 0, DateTimeKind.Utc), Fy2026));

    [Fact]
    public void ทบทวนตอนตีหนึ่งวันที่1มกราตามเวลาไทย_นับเป็นรอบใหม่()
        // 31 ธ.ค. 18:30 UTC = 1 ม.ค. 01:30 น. เวลาไทย
        => Assert.False(UsefulLifeReview.NeedsReview(AssetStatus.Active, DepreciationMethod.StraightLine, 60,
            new DateTime(2024, 5, 1), new DateTime(2025, 12, 31, 18, 30, 0, DateTimeKind.Utc), Fy2026));

    [Fact]
    public void ซื้อในรอบนี้_ไม่เตือน()
        => Assert.False(UsefulLifeReview.NeedsReview(AssetStatus.Active, DepreciationMethod.StraightLine, 60,
            new DateTime(2026, 3, 1), null, Fy2026));

    [Theory]
    [InlineData(AssetStatus.Disposed, DepreciationMethod.StraightLine, 60)]
    [InlineData(AssetStatus.WrittenOff, DepreciationMethod.StraightLine, 60)]
    [InlineData(AssetStatus.Active, DepreciationMethod.None, 0)]
    public void จำหน่ายแล้ว_หรือที่ดิน_ไม่เตือน(AssetStatus status, DepreciationMethod method, int life)
        => Assert.False(UsefulLifeReview.NeedsReview(status, method, life, new DateTime(2020, 1, 1), null, Fy2026));
}
