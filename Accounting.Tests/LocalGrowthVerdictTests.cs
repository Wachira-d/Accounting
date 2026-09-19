using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวชี้วัดคู่ — "โตจริง" ต้องแยกออกจาก "เงียบลง" (DECISION_DOCTRINE §3.2)
///
/// <para>สองสถานการณ์ด้านล่างมี <c>UsedAi</c> ต่ำเท่ากัน แต่ความหมายตรงข้ามกัน —
/// ถ้าตัวตัดสินตอบเหมือนกันเมื่อไร แปลว่าตัวชี้วัดนี้ไร้ค่า</para>
/// </summary>
public class LocalGrowthVerdictTests
{
    [Fact]
    public void นักเรียนตอบเกือบทุกเคสและแม่น_คือโตจริง()
    {
        var v = LocalGrowthVerdict.Judge(500, 460, 20, 0.93m, 120);
        Assert.Equal(LocalGrowthState.Growing, v.State);
    }

    [Fact]
    public void ไม่มีใครถามครูและนักเรียนก็ไม่ตอบ_คือเงียบลง_ไม่ใช่โต()
    {
        var v = LocalGrowthVerdict.Judge(500, 50, 2, 0.99m, 120);
        Assert.Equal(LocalGrowthState.GoingQuiet, v.State);
        Assert.Contains("หยุดตอบ", v.Reason);
    }

    [Fact]
    public void สองสถานการณ์ที่_UsedAi_ต่ำเท่ากัน_ต้องได้คำตอบต่างกัน()
    {
        var grown = LocalGrowthVerdict.Judge(500, 460, 20, 0.93m, 120);
        var quiet = LocalGrowthVerdict.Judge(500, 50, 20, 0.93m, 120);
        Assert.NotEqual(grown.State, quiet.State);
    }

    [Fact]
    public void ไม่มี_label_ที่คนตั้งใจให้_ต้องเป็นตาบอด_แม้ความแม่นจะร้อยเปอร์เซ็นต์()
    {
        var v = LocalGrowthVerdict.Judge(1000, 1000, 0, 1.0m, explicitLabels30d: 0);
        Assert.Equal(LocalGrowthState.Blind, v.State);
    }

    [Fact]
    public void ตัวอย่างน้อย_ต้องเป็นข้อมูลไม่พอ_ไม่ใช่ปกติ()
    {
        var v = LocalGrowthVerdict.Judge(5, 5, 5, 1.0m, 5);
        Assert.Equal(LocalGrowthState.NotEnoughData, v.State);
    }

    [Fact]
    public void ตอบเยอะแต่ผิดบ่อย_คือถอยหลัง()
    {
        var v = LocalGrowthVerdict.Judge(300, 280, 100, 0.55m, 90);
        Assert.Equal(LocalGrowthState.Regressing, v.State);
    }

    [Fact]
    public void ยังพึ่งครูอยู่มาก_คือทรงตัว()
    {
        var v = LocalGrowthVerdict.Judge(300, 150, 200, 0.90m, 90);
        Assert.Equal(LocalGrowthState.Stalled, v.State);
    }

    [Theory]
    [InlineData(LocalGrowthState.NotEnoughData)]
    [InlineData(LocalGrowthState.Blind)]
    [InlineData(LocalGrowthState.Growing)]
    [InlineData(LocalGrowthState.GoingQuiet)]
    [InlineData(LocalGrowthState.Regressing)]
    [InlineData(LocalGrowthState.Stalled)]
    public void ทุกสถานะมีป้ายไทย(LocalGrowthState s)
        => Assert.False(string.IsNullOrWhiteSpace(LocalGrowthVerdict.Label(s)));

    [Fact]
    public void เหตุผลต้องไม่ว่าง_เพราะหน้าจอเอาไปแสดงตรง_ๆ()
        => Assert.All(
            new[]
            {
                LocalGrowthVerdict.Judge(500, 460, 20, 0.93m, 120),
                LocalGrowthVerdict.Judge(500, 50, 2, 0.99m, 120),
                LocalGrowthVerdict.Judge(5, 5, 5, 1m, 5),
            },
            j => Assert.False(string.IsNullOrWhiteSpace(j.Reason)));
}
