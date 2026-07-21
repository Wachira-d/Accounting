using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตรวจสูตร pure ของการนำมัดจำมาหักแบบขับ JE (driveDeposit) — เป็น single source
/// of truth ของสูตรที่ DocumentService.AutoPostToJournalAsync ใช้ (เส้น GL-driven /
/// fallback / journal-ref). โค้ด GL จริงผูก DbContext + FOR UPDATE จึง test ตรงไม่ได้
/// ใน CI — จึงพิสูจน์ "ความถูกต้องเชิงเลข" ที่ helper นี้แทน.
///
/// invariant สำคัญ: Base + Vat = appliedGross เป๊ะเสมอ (ไม่ปัดหาย/เกิน) เพราะ
/// Vat คิดจากผลต่าง (appliedGross − Base) → JE ที่ Dr กลับ 217xx + 21913 รวมแล้ว
/// = ยอดมัดจำที่หัก พอดี → สมดุลกับ Cr ลูกหนี้/เงินสด.
/// </summary>
public class DepositReversalMathTests
{
    // ── SplitBaseVat ──

    [Fact]
    public void Gross_deposit_no_vat_leg_all_goes_to_base()
    {
        // มัดจำ gross (Cr 217xx เต็ม ไม่มีขา VAT) → ratio 0 → ฐานเต็ม, VAT 0
        var (b, v) = DepositReversalMath.SplitBaseVat(3000m, deferredCr: 3000m, vatCr: 0m);
        Assert.Equal(3000m, b);
        Assert.Equal(0m, v);
        Assert.Equal(3000m, b + v);
    }

    [Fact]
    public void Net_plus_vat_splits_proportionally_7pct()
    {
        // มัดจำ 3,210 = ฐาน 3,000 + VAT 210 (7%) → หักเต็ม 3,210
        var (b, v) = DepositReversalMath.SplitBaseVat(3210m, deferredCr: 3000m, vatCr: 210m);
        Assert.Equal(3000m, b);
        Assert.Equal(210m, v);
        Assert.Equal(3210m, b + v);
    }

    [Fact]
    public void Partial_application_keeps_ratio()
    {
        // ใบมัดจำ gross 3,210 (ฐาน 3,000 + VAT 210) แต่หักแค่ 1,070
        //   ratio = 210/3210 = 0.0654..; base = 1070 × (1−ratio) = 999.99.. → 1000.00
        //   vat = 1070 − 1000 = 70.00
        var (b, v) = DepositReversalMath.SplitBaseVat(1070m, deferredCr: 3000m, vatCr: 210m);
        Assert.Equal(1000m, b);
        Assert.Equal(70m, v);
        Assert.Equal(1070m, b + v);
    }

    [Fact]
    public void SplitBaseVat_common_cases()
    {
        // ปกติ 7%: 107 = 100 + 7
        Assert.Equal((100m, 7m), DepositReversalMath.SplitBaseVat(107m, 100m, 7m));
        // gross (ไม่มีขา VAT): 500 → ฐานเต็ม
        Assert.Equal((500m, 0m), DepositReversalMath.SplitBaseVat(500m, 500m, 0m));
        // หัก 0 → 0/0 (ไม่ throw)
        Assert.Equal((0m, 0m), DepositReversalMath.SplitBaseVat(0m, 3000m, 210m));
    }

    [Fact]
    public void Base_plus_vat_always_equals_gross_even_with_odd_rounding()
    {
        // ยอด/สัดส่วนแปลก ๆ — invariant Base+Vat=gross ต้องคงเสมอ (Vat คิดจากผลต่าง)
        foreach (var gross in new[] { 33.33m, 999.99m, 1234.57m, 0.01m, 7777.77m })
        foreach (var vatCr in new[] { 0m, 1m, 7m, 210m, 99.99m })
        {
            var (b, v) = DepositReversalMath.SplitBaseVat(gross, deferredCr: 100m, vatCr: vatCr);
            Assert.Equal(gross, b + v);      // ไม่มีเศษหาย/เกิน
            Assert.True(b >= 0m);
        }
    }

    [Fact]
    public void Zero_gross_deferred_and_vat_no_divide_by_zero()
    {
        var (b, v) = DepositReversalMath.SplitBaseVat(500m, deferredCr: 0m, vatCr: 0m);
        Assert.Equal(500m, b);   // ratio 0 (กัน div/0) → ฐานเต็ม
        Assert.Equal(0m, v);
    }

    // ── ParseDepositRefs ──

    [Fact]
    public void ParseRefs_single()
    {
        var r = DepositReversalMath.ParseDepositRefs("REC-20260718-0009");
        Assert.Single(r);
        Assert.Equal("REC-20260718-0009", r[0]);
    }

    [Fact]
    public void ParseRefs_multiple_comma_and_spaces_trimmed()
    {
        var r = DepositReversalMath.ParseDepositRefs("REC-0009, REC-0010 ,REC-0011");
        Assert.Equal(3, r.Length);
        Assert.Equal(new[] { "REC-0009", "REC-0010", "REC-0011" }, r);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    [InlineData(" , , ")]
    public void ParseRefs_empty_or_blank_yields_empty(string? input)
    {
        Assert.Empty(DepositReversalMath.ParseDepositRefs(input));
    }

    [Fact]
    public void ParseRefs_multi_sum_matches_total_applied()
    {
        // เคสโรงแรม: 2 ใบ (3,000 + 500) — parse ได้ 2 เลข, ผลรวมที่ TakeTime ส่ง = 3,500
        var refs = DepositReversalMath.ParseDepositRefs("REC-0009, REC-0010");
        Assert.Equal(2, refs.Length);
        // แต่ละใบ full base+vat จาก GL — จำลอง: ใบ1 3,000(+0 VAT gross), ใบ2 500(+0)
        var sum = 0m;
        var (b1, v1) = DepositReversalMath.SplitBaseVat(3000m, 3000m, 0m); sum += b1 + v1;
        var (b2, v2) = DepositReversalMath.SplitBaseVat(500m, 500m, 0m); sum += b2 + v2;
        Assert.Equal(3500m, sum);   // รวม Dr มัดจำ = depositAppliedAmount → JE สมดุล
    }
}
