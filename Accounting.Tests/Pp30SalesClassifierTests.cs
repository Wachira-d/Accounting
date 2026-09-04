using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// แยกยอดขายเข้าช่องของ ภ.พ.30 (C-T01)
///
/// ═══ บั๊กจริงที่เทสต์ชุดนี้ล็อกไว้ ═══
/// รายงานคัดเอกสารด้วย <c>VatAmount != 0</c> ⇒ ใบที่ทั้งใบเป็น 0% หรือยกเว้นมี
/// <c>VatAmount == 0</c> จึง<b>ไม่เคยเข้ารายงานเลย</b> — ช่อง 7 (ส่งออก §80/1)
/// หายทั้งช่อง · ช่อง 8 (ยกเว้น §81) นับได้เฉพาะใบที่บังเอิญมีบรรทัด 7% ปนอยู่
/// </summary>
public class Pp30SalesClassifierTests
{
    private static Pp30Line L(decimal rate, decimal amount, decimal vat = 0m)
        => new(rate, amount, vat);

    // ── การแยกช่อง ──

    [Fact]
    public void ใบขาย_7_เปอร์เซ็นต์ล้วน_เข้าช่อง_9_อย่างเดียว()
    {
        var s = Pp30SalesClassifier.Split(new[] { L(7m, 1000m, 70m) });
        Assert.Equal(1000m, s.TaxableBase);
        Assert.Equal(0m, s.ZeroRatedBase);
        Assert.Equal(0m, s.ExemptBase);
        Assert.Equal(70m, s.OutputVat);
    }

    [Fact]
    public void ใบส่งออก_0_เปอร์เซ็นต์ล้วน_ต้องเข้าช่อง_7_ไม่ใช่หายไป()
    {
        // ★ นี่คือบั๊กตัวจริง: ใบแบบนี้ VatAmount = 0 จึงถูกคัดออกตั้งแต่ query
        var s = Pp30SalesClassifier.Split(new[] { L(0m, 500000m) });
        Assert.Equal(500000m, s.ZeroRatedBase);
        Assert.Equal(0m, s.TaxableBase);
        Assert.Equal(0m, s.ExemptBase);
        Assert.True(Pp30SalesClassifier.ShouldReport(s),
            "ใบส่งออกต้องเข้ารายงาน — ไม่งั้นช่อง 7 ว่างทั้งช่อง (§80/1)");
    }

    [Fact]
    public void ใบยกเว้นล้วน_ต้องเข้าช่อง_8_ไม่ใช่หายไป()
    {
        var s = Pp30SalesClassifier.Split(new[] { L(-1m, 12000m) });
        Assert.Equal(12000m, s.ExemptBase);
        Assert.True(Pp30SalesClassifier.ShouldReport(s));
    }

    [Fact]
    public void บิลผสม_7_กับยกเว้น_แยกฐานถูกช่อง()
    {
        // เคสจริงประจำวัน: บิล Makro/BigC — ของสด (ยกเว้น) ปนกับของแห้ง (7%)
        var s = Pp30SalesClassifier.Split(new[]
        {
            L(7m, 1000m, 70m),
            L(-1m, 300m),
        });
        Assert.Equal(1000m, s.TaxableBase);
        Assert.Equal(300m, s.ExemptBase);
        Assert.Equal(0m, s.ZeroRatedBase);
        Assert.Equal(1300m, s.TotalSales);   // ช่อง 6
    }

    [Fact]
    public void บิลผสมสามอัตรา_ช่อง_6_ต้องเท่ากับผลรวมสามช่องเสมอ()
    {
        var s = Pp30SalesClassifier.Split(new[]
        {
            L(7m, 1000m, 70m), L(0m, 2000m), L(-1m, 500m),
        });
        Assert.Equal(1000m, s.TaxableBase);
        Assert.Equal(2000m, s.ZeroRatedBase);
        Assert.Equal(500m, s.ExemptBase);
        Assert.Equal(s.TaxableBase + s.ZeroRatedBase + s.ExemptBase, s.TotalSales);
    }

    [Fact]
    public void ยกเว้นต้องไม่ถูกนับเป็น_0_เปอร์เซ็นต์()
    {
        // คนละช่องและคนละผลทางกฎหมาย: 0% ออกใบกำกับได้+เคลมภาษีซื้อได้ ·
        // ยกเว้นออกใบกำกับไม่ได้และภาษีซื้อลงเป็นต้นทุน
        var zero = Pp30SalesClassifier.Split(new[] { L(0m, 100m) });
        var exempt = Pp30SalesClassifier.Split(new[] { L(-1m, 100m) });
        Assert.Equal(100m, zero.ZeroRatedBase);
        Assert.Equal(0m, zero.ExemptBase);
        Assert.Equal(0m, exempt.ZeroRatedBase);
        Assert.Equal(100m, exempt.ExemptBase);
    }

    [Fact]
    public void อัตราอื่นที่ไม่ใช่_7_ก็ยังเป็นฐานที่ต้องเสียภาษี()
    {
        // เผื่ออนาคตอัตราเปลี่ยน (เคยมี 10% ก่อนลดเหลือ 7%) — ทุกอัตรา > 0
        // ต้องเข้าช่อง 9 ไม่ใช่ตกไปช่องอื่น
        var s = Pp30SalesClassifier.Split(new[] { L(10m, 1000m, 100m) });
        Assert.Equal(1000m, s.TaxableBase);
        Assert.Equal(0m, s.ZeroRatedBase);
    }

    // ── ตัวตัดสิน "ต้องเข้ารายงานไหม" ──

    [Fact]
    public void เอกสารที่ไม่มียอดอะไรเลย_ไม่ต้องเข้ารายงาน()
    {
        Assert.False(Pp30SalesClassifier.ShouldReport(
            Pp30SalesClassifier.Split(System.Array.Empty<Pp30Line>())));
        Assert.False(Pp30SalesClassifier.ShouldReport(
            Pp30SalesClassifier.Split(new[] { L(0m, 0m) })));
    }

    [Fact]
    public void เกณฑ์เดิม_มีภาษีเท่านั้น_ตัดใบที่ต้องรายงานทิ้ง()
    {
        // negative test ของ "เกณฑ์เดิม": พิสูจน์ว่าเงื่อนไข VatAmount != 0
        // ทิ้งใบที่ต้องรายงานจริงไปกี่แบบ
        var mustReport = new[]
        {
            Pp30SalesClassifier.Split(new[] { L(0m, 500000m) }),   // ส่งออกล้วน
            Pp30SalesClassifier.Split(new[] { L(-1m, 12000m) }),   // ยกเว้นล้วน
        };
        foreach (var s in mustReport)
        {
            Assert.Equal(0m, s.OutputVat);                       // เกณฑ์เดิมจะคัดออก
            Assert.True(Pp30SalesClassifier.ShouldReport(s));    // แต่ต้องรายงาน
        }
    }

    [Fact]
    public void ยอดติดลบ_ใบลดหนี้_ยังแยกช่องได้ตามปกติ()
    {
        // ใบลดหนี้ส่งออก: ฐานช่อง 7 ติดลบ ไม่ใช่ถูกโยนทิ้ง
        var s = Pp30SalesClassifier.Split(new[] { L(0m, -20000m) });
        Assert.Equal(-20000m, s.ZeroRatedBase);
        Assert.True(Pp30SalesClassifier.ShouldReport(s));
    }
}
