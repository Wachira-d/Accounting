using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ภ.ง.ด.1 ที่ไม่มีใบ 50 ทวิ รองรับ ต้องเตือน + บล็อกนำส่ง ห้ามนับเงียบ**
/// (D6-4 · คำตัดสินเจ้าของรอบ 170)
///
/// <para>═══ บั๊กที่ล็อกไว้ ═══ <c>PayrollService.IssueMonthlyPnd1CertsAsync</c>
/// <c>continue</c> ข้ามพนักงานที่ยังไม่กรอกเลขประจำตัวผู้เสียภาษี<b>เงียบ ๆ</b>
/// และ <c>catch → LogWarning</c> เมื่อทั้งงวดล้ม · ขณะที่หน้านำส่งอ่านยอดจาก
/// <c>PayrollRun.TotalWithholdingTax</c> ⇒ กดนำส่งได้ตามยอดที่หักจริง โดยที่
/// ใบรับรอง/ไฟล์ยื่นประกาศน้อยกว่า และพนักงานคนนั้นไม่มี 50 ทวิ ไปยื่นแบบ
/// ของตัวเอง · ด่านแบบนี้มีให้ ภ.ง.ด.3/53/54 มาตั้งแต่รอบ 170 แต่**ไม่มีให้
/// ภ.ง.ด.1</b></para>
///
/// <para>ครึ่งตรงข้าม: งวดที่ออกใบครบต้อง<b>ไม่ถูกบล็อก</b> และงวดที่ไม่มีการ
/// หักภาษีเลยต้องเงียบ — คำเตือนที่ฟ้องทุกงวด = ปิดด่านโดยไม่ตั้งใจ (F2 ข้อ 8)</para>
/// </summary>
public class Pnd1CertCoverageTests
{
    // ═══════════ 1. เคสที่ต้องจับได้ ═══════════

    [Fact]
    public void พนักงานหนึ่งคนไม่มีเลขผู้เสียภาษี_ต้องเห็นช่องโหว่()
    {
        // 5 คนถูกหักภาษีรวม 4,250 · ออกใบได้ 4 คน รวม 3,800
        var gap = Pnd1CertCoverage.Evaluate(5, 4_250m, 4, 3_800m);

        Assert.True(gap.Any);
        Assert.Equal(1, gap.EmployeeCount);
        Assert.Equal(450m, gap.TaxAmount);
    }

    [Fact]
    public void ออกใบล้มทั้งงวด_ต้องเห็นยอดเต็ม()
    {
        var gap = Pnd1CertCoverage.Evaluate(12, 18_500m, 0, 0m);

        Assert.True(gap.Any);
        Assert.Equal(12, gap.EmployeeCount);
        Assert.Equal(18_500m, gap.TaxAmount);
    }

    [Fact]
    public void จำนวนใบครบแต่ยอดขาด_ต้องยังจับได้()
    {
        // HR แก้ยอดบนใบรายคนลง ⇒ จำนวนตรงแต่ยอดไม่ตรง — เกณฑ์ต้องเป็น OR
        var gap = Pnd1CertCoverage.Evaluate(5, 4_250m, 5, 3_000m);

        Assert.True(gap.Any);
        Assert.Equal(0, gap.EmployeeCount);
        Assert.Equal(1_250m, gap.TaxAmount);
    }

    [Fact]
    public void ข้อความต้องบอกทั้งยอดและทางไปต่อ()
    {
        var msg = Pnd1CertCoverage.GapMessage(3, 2026, new Pnd1CoverageGap(1, 450m));

        Assert.Contains("450.00", msg);
        Assert.Contains("03/2026", msg);
        Assert.Contains("เลขประจำตัวผู้เสียภาษี", msg);   // บอกสาเหตุที่พบบ่อย
        Assert.Contains("สร้างเอกสารใหม่", msg);          // บอกว่าต้องกดอะไร
    }

    [Fact]
    public void ใช้รหัสด่านเดียวกับ_ภงด_3_53_54()
        => Assert.Equal(WhtUnissuedCertGate.RuleCode, Pnd1CertCoverage.RuleCode);

    // ═══════════ 2. ครึ่งตรงข้าม — งวดที่ถูกต้องห้ามถูกบล็อก ═══════════

    [Fact]
    public void ออกใบครบ_ต้องไม่มีช่องโหว่()
    {
        var gap = Pnd1CertCoverage.Evaluate(5, 4_250m, 5, 4_250m);

        Assert.False(gap.Any);
        Assert.Equal(0, gap.EmployeeCount);
        Assert.Equal(0m, gap.TaxAmount);
    }

    [Fact]
    public void งวดที่ไม่มีใครถูกหักภาษี_ต้องเงียบ()
    {
        var gap = Pnd1CertCoverage.Evaluate(0, 0m, 0, 0m);
        Assert.False(gap.Any);
    }

    [Fact]
    public void มีใบมากกว่าแถวเงินเดือน_ต้องไม่ฟ้อง()
    {
        // HR ออกใบ ภ.ง.ด.1 ให้ผู้รับเงินได้ ม.40(2) เพิ่มเองในเดือนเดียวกัน
        // ⇒ ยอดประกาศมากกว่ายอดเงินเดือน — ไม่ใช่ช่องโหว่
        var gap = Pnd1CertCoverage.Evaluate(5, 4_250m, 7, 6_000m);

        Assert.False(gap.Any);
        Assert.Equal(0m, gap.TaxAmount);
    }

    [Fact]
    public void ต่างกันแค่เศษสตางค์_ต้องไม่ฟ้อง()
    {
        var gap = Pnd1CertCoverage.Evaluate(3, 1_000.005m, 3, 1_000m);
        Assert.False(gap.Any);
    }
}
