using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 (ทีม F · สัญญาฟอร์ม↔API) — ล็อกตัวตรวจกลางที่ถูกดึงออกมาจากบั๊ก "กดสร้างแล้วไม่มีทางสำเร็จ"
/// (A01 โครงการ · A04 แผนคอมมิชชัน). ทุกกลุ่มมีสองครึ่ง: ครึ่งที่พิสูจน์ว่า payload ของหน้าเว็บจริง
/// <b>ผ่าน</b> และครึ่งที่พิสูจน์ว่าค่าผิด/นอกชุด<b>ถูกปฏิเสธเป็นไทย</b> (ไม่ใช่เก็บเงียบ ๆ)
/// </summary>
public class FormContractRound193Tests
{
    // ───────────── A01 · ProjectContractMethods ─────────────

    [Fact]
    public void Project_NotSent_FallsBackToEntityDefaults()
    {
        // API/partner ที่ไม่ส่งสองช่องนี้ ต้องสร้างได้ (เดิม [Required] โดยปริยาย ⇒ 400 ทุกครั้ง)
        Assert.Equal("FixedPrice", ProjectContractMethods.NormalizeBilling(null));
        Assert.Equal("FixedPrice", ProjectContractMethods.NormalizeBilling("  "));
        Assert.Equal("PercentageOfCompletion", ProjectContractMethods.NormalizeRevenueRecognition(null));
    }

    [Theory]
    [InlineData("Milestone", "Milestone")]
    [InlineData("timeandmaterial", "TimeAndMaterial")]
    [InlineData(" FixedPrice ", "FixedPrice")]
    public void Project_Billing_InSet_IsCanonicalised(string input, string expected)
        => Assert.Equal(expected, ProjectContractMethods.NormalizeBilling(input));

    [Fact]
    public void Project_UnknownValues_AreRejectedInThai_NotStored()
    {
        // ทิศตรงข้าม: ค่านอกชุด (เช่นจากไฟล์นำเข้า) เดิมถูกเก็บเป็นข้อความอะไรก็ได้
        var ex = Assert.Throws<BusinessRuleException>(() => ProjectContractMethods.NormalizeBilling("Monthly"));
        Assert.Contains("วิธีเรียกเก็บเงิน", ex.Message);
        // "รับรู้เมื่องานเสร็จ" ไม่อยู่ในชุดโดยตั้งใจ (TFRS for NPAEs บทที่ 6)
        Assert.Throws<BusinessRuleException>(() => ProjectContractMethods.NormalizeRevenueRecognition("CompletedContract"));
    }

    [Fact]
    public void Project_Defaults_AreMembersOfTheirSets()
    {
        Assert.Contains(ProjectContractMethods.Billing, x => x.Value == ProjectContractMethods.DefaultBilling);
        Assert.Contains(ProjectContractMethods.RevenueRecognition, x => x.Value == ProjectContractMethods.DefaultRevenueRecognition);
    }

    // ───────────── A04 · CommissionPlanRules ─────────────

    [Fact]
    public void Commission_PercentagePayloadFromPage_Passes()
    {
        // payload ที่ commission.html ส่งจริงหลังแก้: {name, calculationBasis, calculationMethod, flatRate, tiers: []}
        var spec = CommissionPlanRules.Validate(" ทีมขาย ", "Revenue", "Percentage", 5m, new List<CommissionTierSpec>());
        Assert.Equal("ทีมขาย", spec.Name);
        Assert.Equal("Percentage", spec.CalculationMethod);
        Assert.Equal(5m, spec.FlatRate);
        Assert.Empty(spec.Tiers);
    }

    [Fact]
    public void Commission_TieredPayload_SortsTiers_AndClearsFlatRate()
    {
        var spec = CommissionPlanRules.Validate("ขั้นบันได", "collectedamount", "Tiered", 7m, new[]
        {
            new CommissionTierSpec(100_000m, null, 5m),
            new CommissionTierSpec(0m, 100_000m, 3m),
        });
        Assert.Equal("CollectedAmount", spec.CalculationBasis);
        Assert.Null(spec.FlatRate);              // อัตราเดี่ยวไม่ถูกใช้กับขั้นบันได — ห้ามเก็บค้างให้เข้าใจผิด
        Assert.Equal(0m, spec.Tiers[0].FromAmount);
        Assert.Null(spec.Tiers[1].ToAmount);
    }

    [Fact]
    public void Commission_SwitchFromTieredToFixed_DropsTiers()
    {
        var spec = CommissionPlanRules.Validate("x", null, "Fixed", 1500m,
            new[] { new CommissionTierSpec(0m, null, 3m) });
        Assert.Equal("Revenue", spec.CalculationBasis);   // ว่าง = ค่าเริ่มต้นเดียวกับ entity
        Assert.Empty(spec.Tiers);
    }

    [Theory]
    [InlineData("Percentage", null)]
    [InlineData("Percentage", "0")]
    [InlineData("Percentage", "100.01")]
    [InlineData("Fixed", "0")]
    [InlineData("Fixed", null)]
    public void Commission_MissingOrOutOfRangeRate_IsRejected(string method, string? rate)
    {
        // เดิม: แผนอัตรา null ถูกสร้างได้ (ถ้าผ่าน required) แล้วคำนวณได้ 0 ตลอดกาลโดยไม่มีใครรู้
        decimal? r = rate == null ? null : decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<BusinessRuleException>(() => CommissionPlanRules.Validate("x", "Revenue", method, r, null));
    }

    [Fact]
    public void Commission_Tiered_OverlapOrOpenMiddle_IsRejected()
    {
        // ทับกัน = ยอดช่วงเดียวถูกคิดคอมซ้ำสองขั้น
        Assert.Throws<BusinessRuleException>(() => CommissionPlanRules.Validate("x", "Revenue", "Tiered", null, new[]
        {
            new CommissionTierSpec(0m, 120_000m, 3m),
            new CommissionTierSpec(100_000m, null, 5m),
        }));
        // ขั้นกลางไม่มีเพดาน
        Assert.Throws<BusinessRuleException>(() => CommissionPlanRules.Validate("x", "Revenue", "Tiered", null, new[]
        {
            new CommissionTierSpec(0m, null, 3m),
            new CommissionTierSpec(100_000m, null, 5m),
        }));
        // ไม่มีขั้นเลย
        Assert.Throws<BusinessRuleException>(() => CommissionPlanRules.Validate("x", "Revenue", "Tiered", null, null));
    }

    [Fact]
    public void Commission_UnknownBasisOrMethod_AndBlankName_AreRejected()
    {
        // "Quantity" อยู่ในคอมเมนต์ entity แต่ไม่มีเส้นคำนวณ — เลือกได้ = ได้ 0 เงียบ ๆ จึงไม่อยู่ในชุด
        Assert.Throws<BusinessRuleException>(() => CommissionPlanRules.Validate("x", "Quantity", "Percentage", 5m, null));
        // ค่าที่หน้าเว็บเก่าส่ง (commissionType) ไม่ใช่วิธีคำนวณ
        Assert.Throws<BusinessRuleException>(() => CommissionPlanRules.Validate("x", "Revenue", "Tier", 5m, null));
        Assert.Throws<BusinessRuleException>(() => CommissionPlanRules.Validate("   ", "Revenue", "Percentage", 5m, null));
    }
}
