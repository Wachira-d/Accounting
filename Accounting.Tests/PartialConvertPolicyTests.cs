using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คำตัดสินข้อ 138 (2026-10-05): แปลงเอกสารบางส่วน <b>ห้ามล็อกจำนวน</b> — จำนวนเกินคงเหลือเป็นคำเตือน ·
/// ยอดเงินสะสมเกินใบต้นทางเป็นด่านเดียวที่หยุดถาม (ยืนยันแล้วไปต่อได้) · เท่ากันพอดีต้องไม่ถูกถาม
/// (เคสจริง: ใบเสนอราคา 1 ชิ้น 535 บาท แบ่งเก็บ 2 งวด — เดิมงวดสองถูกล็อกที่ 0 ชิ้น)
/// </summary>
public class PartialConvertPolicyTests
{
    [Fact]
    public void ขอเกินคงเหลือ_เป็นคำเตือน_ไม่ใช่ข้อผิดพลาด()
    {
        var warnings = PartialConvertPolicy.QuantityWarnings(new[]
        {
            new PartialConvertPolicy.LineAsk("Test", "ชิ้น", Requested: 1m, Remaining: 0m),
            new PartialConvertPolicy.LineAsk("งานติดตั้ง", "งาน", Requested: 3m, Remaining: 2m),
        }, "วางบิล");

        Assert.Equal(2, warnings.Count);
        Assert.Contains("ครบจำนวนแล้ว", warnings[0]);
        Assert.Contains("ขอวางบิล 3 แต่คงเหลือ 2", warnings[1]);
    }

    [Fact]
    public void ทิศตรงข้าม_อยู่ในคงเหลือ_ไม่มีคำเตือน()
    {
        var warnings = PartialConvertPolicy.QuantityWarnings(new[]
        {
            new PartialConvertPolicy.LineAsk("Test", "ชิ้น", Requested: 1m, Remaining: 1m),
            new PartialConvertPolicy.LineAsk("Test2", "ชิ้น", Requested: 0.5m, Remaining: 1m),
        }, "วางบิล");
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(500, 1, 0.5, 250)]      // ครึ่งชิ้น = ครึ่งยอด (แบ่งงวด 50/50)
    [InlineData(500, 1, 1, 500)]
    [InlineData(500, 4, 1, 125)]
    [InlineData(1000, 3, 1, 333.33)]    // ปัดครึ่งสตางค์ออกจากศูนย์ตามกฎ E
    [InlineData(500, 0, 1, 500)]        // บรรทัดเหมา (จำนวน 0) → ขอ ≥ 1 ได้ยอดเต็ม
    [InlineData(500, 1, 0, 0)]
    public void ยอดตามสัดส่วนจาก_Amount_ของบรรทัดต้นทาง(decimal lineAmount, decimal lineQty, decimal qty, decimal expected)
        => Assert.Equal(expected, PartialConvertPolicy.ProRataBase(lineAmount, lineQty, qty));

    [Theory]
    [InlineData(250, 250, 500, false)]  // งวดสองพอดียอด — ต้องไม่ถูกถาม (เคสผู้ใช้)
    [InlineData(0, 500, 500, false)]
    [InlineData(250, 250.004, 500, false)] // ภายใน epsilon ครึ่งสตางค์
    [InlineData(250, 251, 500, true)]
    [InlineData(500, 0.01, 500, true)]  // แปลงครบแล้วขอเพิ่มอีก 1 สตางค์ → ถาม
    public void ยอดสะสมเกินใบต้นทาง_หยุดถามครั้งเดียว(decimal before, decimal now, decimal source, bool expected)
        => Assert.Equal(expected, PartialConvertPolicy.IsOverAmount(before, now, source));

    [Fact]
    public void เศษปัดต่อบรรทัด_แบ่งครบพอดีต้องไม่ถูกถาม()
    {
        // ฝ่ายค้านรอบ 138 P2: บรรทัด 3 × 10.03 ลด 15% = 25.58 · แบ่ง 3 ใบ ใบละ 1 ชิ้น = 8.53 × 3 = 25.59 (ปัดส่วนลดแยกใบ)
        // ใบที่ 3 ซึ่ง "ครบพอดี" เกิน 0.01 จากเศษปัด — ต้องไม่ถามผู้ใช้ (บรรทัดลูกเดิม 2 + ครั้งนี้ 1 = 3 บรรทัด)
        Assert.False(PartialConvertPolicy.IsOverAmount(17.06m, 8.53m, 25.58m, roundingLines: 3));
        // เกินจริง (เพิ่มอีก 1 ชิ้นเต็ม) ต้องยังถาม แม้มีเผื่อเศษปัด
        Assert.True(PartialConvertPolicy.IsOverAmount(25.59m, 8.53m, 25.58m, roundingLines: 4));
    }

    [Fact]
    public void ข้อความเกินยอด_บอกตัวเลขครบสามตัว()
    {
        var m = PartialConvertPolicy.OverAmountMessage("QT-2026-0001", "วางบิล", 500m, 535m, 535m);
        Assert.Contains("1,035.00", m);
        Assert.Contains("ก่อนหน้า 500.00", m);
        Assert.Contains("ครั้งนี้ 535.00", m);
        Assert.Contains("ใบต้นทาง 535.00", m);
        Assert.Contains("อยู่ 500.00 — ตรวจยอด", m);
        Assert.DoesNotContain("บาท", m);   // ใบสกุลต่างประเทศ — ยอดเป็นสกุลของเอกสาร ไม่ใช่บาท
    }
}
