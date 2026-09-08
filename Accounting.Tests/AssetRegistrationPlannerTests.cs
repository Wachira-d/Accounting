using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ล็อกกติกา "หนึ่งใบซื้อ = กี่สินทรัพย์" — invariant ที่จับบั๊กคลาสนี้ได้คือ
/// **Σ ต้นทุนที่ขึ้นทะเบียน = Σ ยอดบรรทัดในกลุ่ม** (ยอดรายตัวดูสมเหตุสมผลเสมอ
/// แม้ตอนที่การจัดกลุ่มพัง)
/// </summary>
public class AssetRegistrationPlannerTests
{
    private static AssetRegistrationPlanner.Line L(string? d, decimal amt)
        => new(Guid.NewGuid(), d, amt);

    [Fact]
    public void แอร์สามเครื่องในผังเดียวกัน_ต้องได้สามสินทรัพย์()
    {
        // เคสที่ผู้ใช้เจอ: ใบเดียวซื้อของหลายชิ้นบนผัง 12210 —
        // เดิมจัดกลุ่มด้วย AccountId ⇒ ได้ทะเบียนแถวเดียวราคารวม
        var lines = new[] { L("แอร์ตัวที่ 1", 10_000m), L("แอร์ตัวที่ 2", 10_000m),
                            L("แอร์ตัวที่ 3", 10_000m) };
        var plan = AssetRegistrationPlanner.Plan(lines);

        Assert.Equal(3, plan.Count);
        Assert.All(plan, p => Assert.Equal(10_000m, p.Cost));
        Assert.Equal(30_000m, plan.Sum(p => p.Cost));
    }

    [Fact]
    public void ค่าขนส่งเฉลี่ยตามสัดส่วน_และผลรวมต้องเท่ายอดในกลุ่มเป๊ะ()
    {
        var lines = new[] { L("เครื่องจักร A", 60_000m), L("เครื่องจักร B", 40_000m),
                            L("ค่าขนส่ง", 1_000m) };
        var plan = AssetRegistrationPlanner.Plan(lines);

        Assert.Equal(2, plan.Count);
        Assert.Equal(60_600m, plan[0].Cost);   // 60% ของ 1,000
        Assert.Equal(40_400m, plan[1].Cost);
        Assert.Equal(101_000m, plan.Sum(p => p.Cost));   // ← invariant
    }

    [Fact]
    public void เศษจากการปัด_บรรทัดสุดท้ายรับไป_ผลรวมไม่เพี้ยน()
    {
        // 3 ชิ้นเท่ากัน + ค่าติดตั้ง 10 ⇒ 3.33 + 3.33 + 3.34
        var lines = new[] { L("โต๊ะ 1", 1_000m), L("โต๊ะ 2", 1_000m), L("โต๊ะ 3", 1_000m),
                            L("ค่าติดตั้ง", 10m) };
        var plan = AssetRegistrationPlanner.Plan(lines);

        Assert.Equal(3, plan.Count);
        Assert.Equal(3_010m, plan.Sum(p => p.Cost));
        Assert.Equal(10m, plan.Sum(p => p.AllocatedAuxiliary));
    }

    [Fact]
    public void ของชิ้นเดียวบวกค่าขนส่ง_ยังได้สินทรัพย์ตัวเดียว_พฤติกรรมเดิม()
    {
        // ทิศตรงข้าม — การแก้ต้องไม่ทำให้เคสที่พบบ่อยที่สุดแตกเป็นหลายตัว
        var lines = new[] { L("เครื่องปรับอากาศ", 20_000m), L("ค่าจัดส่ง", 500m),
                            L("ค่าติดตั้ง", 1_500m) };
        var plan = AssetRegistrationPlanner.Plan(lines);

        Assert.Single(plan);
        Assert.Equal(22_000m, plan[0].Cost);
        Assert.Equal(2_000m, plan[0].AllocatedAuxiliary);
    }

    [Fact]
    public void ทุกบรรทัดเป็นค่าใช้จ่ายประกอบ_ยังต้องขึ้นทะเบียนหนึ่งตัว()
    {
        // ใบค่าติดตั้งเดี่ยว ๆ ที่ผังลง PPE — ถ้าคืนว่าง Dr 12xxx จะลอยไม่มีคู่
        var lines = new[] { L("ค่าติดตั้งระบบไฟฟ้า", 5_000m), L("ค่าขนส่งอุปกรณ์", 800m) };
        var plan = AssetRegistrationPlanner.Plan(lines);

        Assert.Single(plan);
        Assert.Equal(5_800m, plan[0].Cost);
    }

    [Fact]
    public void บรรทัดยอดศูนย์หรือติดลบ_ไม่นับเป็นสินทรัพย์()
    {
        var lines = new[] { L("จอมอนิเตอร์", 8_000m), L("ของแถม", 0m) };
        var plan = AssetRegistrationPlanner.Plan(lines);

        Assert.Single(plan);
        Assert.Equal(8_000m, plan[0].Cost);
    }

    [Theory]
    [InlineData("ค่าขนส่งสินค้า", true)]
    [InlineData("Shipping Fee", true)]
    [InlineData("ค่าติดตั้งเครื่อง", true)]
    [InlineData("เครื่องปรับอากาศ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ตัวจำแนกค่าใช้จ่ายประกอบ(string? desc, bool expected)
        => Assert.Equal(expected, AssetRegistrationPlanner.IsAuxiliary(desc));
}
