using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ฐานเงินสมทบ ปกส. รวมเบี้ยเลี้ยงไหม — ธงต่อรายการ ไม่ใช่กฎเหมารวม**
/// (คำตัดสินเจ้าของ Q1 · DECISION_AUDIT_2026-09-18 §9.2)
///
/// <para>═══ โจทย์ ═══ ม.5 พ.ร.บ.ประกันสังคม นิยาม "ค่าจ้าง" = เงินที่จ่าย
/// <b>ตอบแทนการทำงาน</b> ⇒ เบี้ยขยัน/ค่าตำแหน่ง/ค่าครองชีพที่จ่ายประจำทุกเดือน
/// เป็นค่าจ้าง · ค่าเดินทาง/ที่พัก<b>ตามจ่ายจริง</b>ไม่ใช่ · เส้นแบ่งขึ้นกับ
/// ข้อตกลงการจ้างของแต่ละบริษัท ระบบเดาแทนไม่ได้</para>
///
/// <para>═══ ทิศที่เลือก (G5) ═══ วันนี้ระบบคิดฐานจากเงินเดือนพื้นฐานอย่างเดียว
/// ⇒ บริษัทที่จ่ายค่าตำแหน่งประจำกำลัง<b>นำส่งขาด</b> ซึ่ง สปส. ประเมินย้อนหลัง
/// + เงินเพิ่ม 2%/เดือน (ม.49) และ<b>มองไม่เห็น</b>จนถึงวันตรวจ · ขณะที่หักเกิน
/// ลูกจ้างเห็นในสลิปทันที ⇒ ทิศที่ถูกคือ "ให้รวมได้" · แต่ค่าตั้งต้นต้องเป็น
/// <c>null</c> = ไม่รวม เพื่อไม่ให้ยอดของลูกค้าที่ยื่นไปแล้วขยับเอง</para>
///
/// <para>═══ golden ของโจทย์ ═══ เงินเดือน 14,000 + เบี้ยเลี้ยง 3,000</para>
/// </summary>
public class SsoWageDecisionTests
{
    // ปี 2568 (2025): เพดาน 15,000 · 5% · สมทบสูงสุด 750
    private const decimal Ceiling2025 = 15_000m;
    private const decimal Max2025 = 750m;
    // ปี 2569 (2026): เพดาน 17,500 · 5% · สมทบสูงสุด 875
    private const decimal Ceiling2026 = 17_500m;
    private const decimal Max2026 = 875m;
    private const decimal Rate = 0.05m;

    private static decimal Contribution(decimal salary, decimal allowance, bool? flag,
        decimal ceiling, decimal maxContribution)
    {
        var wage = SsoWageBase.GrossWage(
            salary, SsoWageBase.CountsAsWage(flag) ? allowance : 0m);
        return SsoWageBase.Contribution(SsoWageBase.Clamp(wage, ceiling), Rate, maxContribution);
    }

    // ═══════════ 1. golden 14,000 + 3,000 ═══════════

    [Fact]
    public void ธงเป็น_null_ต้องได้ยอดเท่าพฤติกรรมเดิมเป๊ะ_700()
    {
        Assert.Equal(700m, Contribution(14_000m, 3_000m, null, Ceiling2025, Max2025));
        Assert.Equal(700m, Contribution(14_000m, 3_000m, null, Ceiling2026, Max2026));
    }

    [Fact]
    public void ธงเป็น_false_ต้องเท่ากับ_null_ทุกบาท()
    {
        Assert.Equal(
            Contribution(14_000m, 3_000m, null, Ceiling2026, Max2026),
            Contribution(14_000m, 3_000m, false, Ceiling2026, Max2026));
    }

    [Fact]
    public void ธงเป็น_true_ปี2569_ฐาน17000_ต้องได้_850_ต่อฝ่าย()
        => Assert.Equal(850m, Contribution(14_000m, 3_000m, true, Ceiling2026, Max2026));

    [Fact]
    public void ธงเป็น_true_ปี2568_ฐานชนเพดาน15000_ต้องได้_750_ไม่ใช่_850()
        => Assert.Equal(750m, Contribution(14_000m, 3_000m, true, Ceiling2025, Max2025));

    // ═══════════ 2. ตัวตัดสิน + คำเตือน ═══════════

    [Theory]
    [InlineData(null, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void CountsAsWage_เฉพาะ_true_เท่านั้นที่เข้าฐาน(bool? flag, bool expected)
        => Assert.Equal(expected, SsoWageBase.CountsAsWage(flag));

    [Theory]
    [InlineData(null, true)]     // ★ ยังไม่ตัดสิน = ต้องเตือน
    [InlineData(true, false)]
    [InlineData(false, false)]   // ★ ตัดสินแล้วว่าไม่ใช่ = ห้ามเตือนซ้ำ (G3b)
    public void NeedsWageDecision_แยก_ยังไม่ตัดสิน_ออกจาก_ตัดสินแล้วว่าไม่ใช่(
        bool? flag, bool expected)
        => Assert.Equal(expected, SsoWageBase.NeedsWageDecision(flag));

    // ═══════════ 3. สามสถานะต้องเดินทางกลับได้ (กันบั๊ก silent no-op) ═══════════

    [Theory]
    [InlineData("true", true, true)]
    [InlineData("false", true, false)]
    [InlineData("unset", true, null)]
    [InlineData("null", true, null)]
    public void แปลงค่าจากฟอร์ม_ต้องกลับไป_ยังไม่ระบุ_ได้(
        string raw, bool expectedHandled, bool? expectedValue)
    {
        var handled = SsoWageBase.TryParseWageDecision(raw, out var v);
        Assert.Equal(expectedHandled, handled);
        Assert.Equal(expectedValue, v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("maybe")]
    public void ไม่ส่งค่ามา_ต้องแปลว่า_ห้ามแตะของเดิม(string? raw)
        => Assert.False(SsoWageBase.TryParseWageDecision(raw, out _));

    // ═══════════ 4. ทิศตรงข้าม — ของเดิมห้ามขยับ ═══════════

    [Fact]
    public void ไม่มีเบี้ยเลี้ยงเลย_ธงอะไรก็ต้องได้ยอดเดิม()
    {
        foreach (var flag in new bool?[] { null, true, false })
            Assert.Equal(700m, Contribution(14_000m, 0m, flag, Ceiling2026, Max2026));
    }

    [Fact]
    public void เงินเดือนเกินเพดานอยู่แล้ว_ติ๊กธงก็ไม่ทำให้ยอดเพิ่ม()
    {
        // 30,000 ชนเพดาน 17,500 อยู่แล้ว ⇒ เบี้ยเลี้ยงไม่มีผล (สมทบสูงสุด 875)
        Assert.Equal(875m, Contribution(30_000m, 5_000m, null, Ceiling2026, Max2026));
        Assert.Equal(875m, Contribution(30_000m, 5_000m, true, Ceiling2026, Max2026));
    }

    [Fact]
    public void ฐานขั้นต่ำ_1650_ยังบังคับอยู่()
        => Assert.Equal(82.50m, Contribution(900m, 0m, null, Ceiling2026, Max2026));

    [Fact]
    public void GrossWage_ติดลบต้องเป็นศูนย์_ไม่ใช่ค่าติดลบ()
        => Assert.Equal(0m, SsoWageBase.GrossWage(-5_000m, 1_000m));

    [Fact]
    public void คำอธิบายต้องมีทั้งสามสถานะและไม่ว่าง()
    {
        foreach (var flag in new bool?[] { null, true, false })
            Assert.False(string.IsNullOrWhiteSpace(SsoWageBase.DescribeWageDecision(flag)));
        // สถานะ "ยังไม่ระบุ" ต้องบอกความเสี่ยงตามกฎหมาย ไม่ใช่แค่ "ไม่รวม"
        Assert.Contains("ม.49", SsoWageBase.DescribeWageDecision(null));
    }
}
