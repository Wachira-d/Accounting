using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ภาษีเงินได้นิติบุคคลต้องใช้อัตราของบริษัทนั้นจริง ๆ** (ผลตรวจรอบ 181 · D2-B2a)
///
/// ═══ บั๊กที่ล็อกไว้ ═══
/// <para><c>TaxService.CalculateThaiCit(netProfit)</c> เคยใช้ <b>ขั้นบันได SME
/// กับทุกบริษัท</b> โดยไม่ดูทุน/รายได้เลย และผู้เรียกคือเส้น <b>ภ.ง.ด.50</b>
/// ⇒ บริษัททั่วไป (ทุน &gt; 5 ล. หรือรายได้ &gt; 30 ล.) เสียภาษีต่ำกว่ากฎหมาย
/// บนแบบที่ยื่นจริง — golden ของผลตรวจ: ทุน 10 ล. กำไร 1,000,000 ต้องเป็น
/// <b>200,000</b> แต่ระบบให้ 105,000 (ขาดไป 95,000 ต่อบริษัทต่อปี)</para>
///
/// <para>ครึ่งหลังของไฟล์คือ <b>ทิศตรงข้าม</b> — บริษัท SME ที่คำนวณถูกอยู่แล้ว
/// ผลลัพธ์ต้อง <b>ไม่เปลี่ยน</b> หลังการแก้ (กันไม่ให้ใบที่เคยถูกกลับมาผิด)</para>
/// </summary>
public class CitRateTableTests
{
    // ───────────────────────── golden จากผลตรวจ ─────────────────────────

    [Fact]
    public void บริษัททั่วไปทุน10ล้าน_กำไร1ล้าน_ต้องเสีย200000_ไม่ใช่105000()
    {
        var isSme = CitRateTable.IsSme(paidUpCapital: 10_000_000m, annualRevenue: 20_000_000m);
        Assert.False(isSme);                                   // ทุนเกิน 5 ล. แม้รายได้ไม่เกิน 30 ล.
        Assert.Equal(200_000m, CitRateTable.Compute(1_000_000m, isSme));
        // ยอดเดิมที่บั๊กให้ — ต้องไม่ใช่คำตอบอีกต่อไป
        Assert.NotEqual(105_000m, CitRateTable.Compute(1_000_000m, isSme));
    }

    [Fact]
    public void บริษัทรายได้เกิน30ล้าน_แม้ทุนน้อย_ก็ไม่ใช่SME()
    {
        var isSme = CitRateTable.IsSme(paidUpCapital: 1_000_000m, annualRevenue: 45_000_000m);
        Assert.False(isSme);
        Assert.Equal(200_000m, CitRateTable.Compute(1_000_000m, isSme));
    }

    // ───────────── ทิศตรงข้าม: SME ที่เคยถูก ต้องไม่เปลี่ยน ─────────────

    [Fact]
    public void SMEทุน5ล้านรายได้20ล้าน_กำไร1ล้าน_ยังได้105000เท่าเดิม()
    {
        var isSme = CitRateTable.IsSme(paidUpCapital: 5_000_000m, annualRevenue: 20_000_000m);
        Assert.True(isSme);
        // 0% × 300,000 + 15% × 700,000 = 105,000 — ค่าเดิมก่อนการแก้
        Assert.Equal(105_000m, CitRateTable.Compute(1_000_000m, isSme));
    }

    [Theory]
    // (กำไรสุทธิ, ภาษีที่ต้องได้) — ชุดเดิมของ SME ทั้งชุด ห้ามขยับแม้แต่สตางค์เดียว
    // รับเป็น int แล้วแปลงเอง — xUnit InlineData ส่ง decimal ตรง ๆ ไม่ได้
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(300_000, 0)]                 // ขอบล่างของขั้นยกเว้น
    [InlineData(400_000, 15_000)]            // 15% × 100,000
    [InlineData(3_000_000, 405_000)]         // 15% × 2,700,000
    [InlineData(5_000_000, 805_000)]         // 405,000 + 20% × 2,000,000
    public void ขั้นบันไดSMEต้องคงเดิมทุกขั้น(int netProfit, int expected)
        => Assert.Equal((decimal)expected, CitRateTable.Compute(netProfit, isSme: true));

    [Fact]
    public void SMEกำไรเกิน300000บาทเดียว_เสียเฉพาะส่วนที่เกิน()
        // 300,001 → 15% × 1 = 0.15 (ไม่ใช่ 15% ของทั้งก้อน)
        => Assert.Equal(0.15m, CitRateTable.Compute(300_001m, isSme: true));

    // ───────────────────────── ขาดทุน / ศูนย์ ─────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ขาดทุนไม่เสียภาษี(bool isSme)
    {
        Assert.Equal(0m, CitRateTable.Compute(-1_500_000m, isSme));
        Assert.Equal(0m, CitRateTable.Compute(0m, isSme));
    }

    // ───────────────────────── ขอบของเกณฑ์ SME ─────────────────────────

    [Theory]
    // (ทุน, รายได้ทั้งรอบ, เป็น SME ไหม) — "≤" ทั้งสองข้าง ต้องเข้า **ทั้งคู่**
    [InlineData(5_000_000, 30_000_000, true)]    // พอดีเพดานทั้งสองข้าง = ยังเป็น SME
    [InlineData(5_000_001, 30_000_000, false)]   // ทุนเกิน 1 บาท
    [InlineData(5_000_000, 30_000_001, false)]   // รายได้เกิน 1 บาท
    [InlineData(5_000_001, 30_000_001, false)]
    [InlineData(0, 0, true)]                     // บริษัทใหม่ยังไม่มีรายได้
    public void เกณฑ์SMEต้องเข้าทั้งทุนและรายได้(
        int paidUpCapital, int annualRevenue, bool expected)
        => Assert.Equal(expected, CitRateTable.IsSme(paidUpCapital, annualRevenue));

    // ───────────────── ตารางต้องเป็นตัวตั้งตัวเดียว ─────────────────

    [Fact]
    public void เพดานและอัตราต้องอ่านจากตารางกลาง_ไม่ใช่เลขที่พิมพ์ซ้ำ()
    {
        Assert.Equal(5_000_000m, CitRateTable.SmePaidUpCapitalCeiling);
        Assert.Equal(30_000_000m, CitRateTable.SmeAnnualRevenueCeiling);
        Assert.Equal(0.20m, CitRateTable.StandardRate);
        Assert.Equal(3, CitRateTable.SmeBrackets.Count);
        Assert.Equal(0m, CitRateTable.SmeBrackets[0].Rate);
        Assert.Equal(300_000m, CitRateTable.SmeBrackets[0].UpTo);
        Assert.Equal(0.15m, CitRateTable.SmeBrackets[1].Rate);
        Assert.Equal(3_000_000m, CitRateTable.SmeBrackets[1].UpTo);
        Assert.Equal(0.20m, CitRateTable.SmeBrackets[2].Rate);
        Assert.Equal(decimal.MaxValue, CitRateTable.SmeBrackets[2].UpTo);
    }

    [Fact]
    public void ปัดเศษต้องเป็นAwayFromZero_ไม่ใช่bankersRounding()
    {
        // 15% ของยอดที่สตางค์ ≡ 10 (mod 20) ตกจุดกึ่งกลางจริง:
        // (300,000.70 − 300,000) × 0.15 = 0.105 → ต้องได้ 0.11 (banker's = 0.10)
        Assert.Equal(0.11m, CitRateTable.Compute(300_000.70m, isSme: true));
    }

    [Fact]
    public void บริษัททั่วไปคิด20เปอร์เซ็นต์ทั้งก้อน_ไม่มีขั้นยกเว้น()
    {
        // กำไร 300,000 — SME ได้ยกเว้น แต่บริษัททั่วไปเสีย 60,000
        Assert.Equal(0m, CitRateTable.Compute(300_000m, isSme: true));
        Assert.Equal(60_000m, CitRateTable.Compute(300_000m, isSme: false));
    }
}
