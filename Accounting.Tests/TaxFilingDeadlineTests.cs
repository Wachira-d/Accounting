using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตารางกำหนดยื่นแบบรายเดือน — เดิมเรพมี **3 ชุด** (หน้านำส่ง · ปฏิทินภาษี ·
/// ตัวตรวจก่อนยื่น) และชุดที่สามเหมา ภ.พ.36 ไปรวมกับ ภ.พ.30 ⇒ ได้วันที่ 23
/// แทนวันที่ 15 = เตือนช้ากว่ากำหนดจริง 8 วัน
///
/// เทสต์ล็อก **เลขวันตามกฎหมาย** ไม่ใช่ "ให้สามตัวเท่ากัน" — ถ้ายึดตัวที่ผิด
/// เป็นฐาน ตัวที่ถูกอยู่แล้วจะพังตาม (CLAUDE.md §H)
/// </summary>
public class TaxFilingDeadlineTests
{
    // ก.ย. 2569 → ครบกำหนดในเดือน ต.ค. 2569 (1 ต.ค. = พฤหัสบดี ⇒ ไม่มีวันไหน
    // ในเคสนี้ตรงเสาร์/อาทิตย์ จึงเทียบเลขวันดิบได้ตรง ๆ)
    [Theory]
    [InlineData("VatPp30", 15, 23)]     // §83 วรรคสอง + ขยาย e-Filing 8 วัน
    [InlineData("VatPp36", 7, 15)]      // §83/6 — **ไม่ใช่ 23**
    [InlineData("WhtPnd1", 7, 15)]      // §52/§59
    [InlineData("WhtPnd3", 7, 15)]
    [InlineData("WhtPnd53", 7, 15)]
    [InlineData("WhtPnd54", 7, 15)]     // ม.70
    [InlineData("SsoSps110", 15, 15)]   // พ.ร.บ.ประกันสังคม §47 — ไม่อยู่ในมาตรการขยายของ ก.คลัง
    public void เลขวันครบกำหนดตรงตามกฎหมายรายแบบ(string type, int paperDay, int eFilingDay)
    {
        var (paper, efiling) = TaxFilingDeadline.For(type, 2026, 9);
        Assert.Equal(new DateTime(2026, 10, paperDay), paper);
        Assert.Equal(new DateTime(2026, 10, eFilingDay), efiling);
    }

    [Fact]
    public void ภพ36_ต้องไม่ถูกเหมารวมกับ_ภพ30()
    {
        // ทิศที่เคยพัง: `VAT or VatPp36 => AddDays(22)` ใน TaxComplianceChecker
        Assert.NotEqual(
            TaxFilingDeadline.EFilingFor(TaxType.VAT, 2026, 9),
            TaxFilingDeadline.EFilingFor(TaxType.VatPp36, 2026, 9));
        Assert.Equal(new DateTime(2026, 10, 15), TaxFilingDeadline.EFilingFor(TaxType.VatPp36, 2026, 9));
    }

    // ═══ เลื่อนวันหยุด — ป.พ.พ. §193/8 (เลื่อนไปข้างหน้าเท่านั้น) ═══

    [Fact]
    public void วันครบกำหนดตรงเสาร์อาทิตย์_ต้องเลื่อนไปวันจันทร์()
    {
        // 15 พ.ย. 2569 = วันอาทิตย์ ⇒ ภ.พ.30 ของงวด ต.ค. ต้องเลื่อนเป็น 16 พ.ย.
        Assert.Equal(DayOfWeek.Sunday, new DateTime(2026, 11, 15).DayOfWeek);
        var (paper, _) = TaxFilingDeadline.For("VatPp30", 2026, 10);
        Assert.Equal(new DateTime(2026, 11, 16), paper);
    }

    [Fact]
    public void eFiling_นับจากวันครบกำหนดก่อนเลื่อน_ไม่ใช่หลังเลื่อน()
    {
        // ปฏิทินภาษีเดิมเลื่อนกระดาษก่อนแล้วค่อย +8 ⇒ ได้ช้ากว่ากฎหมายถึง 2 วัน
        // งวด ก.พ. 2569: กระดาษ = 7 มี.ค. (เสาร์) → เลื่อน 9 มี.ค. · e-Filing ต้อง
        // เป็น 15 มี.ค. (7+8) แล้วเลื่อนถ้าจำเป็น — ไม่ใช่ 9+8 = 17
        Assert.Equal(DayOfWeek.Saturday, new DateTime(2026, 3, 7).DayOfWeek);
        var (paper, efiling) = TaxFilingDeadline.For("WhtPnd3", 2026, 2);
        Assert.Equal(new DateTime(2026, 3, 9), paper);
        Assert.Equal(TaxFilingDeadline.RollToBusinessDay(new DateTime(2026, 3, 15)), efiling);
        Assert.NotEqual(new DateTime(2026, 3, 17), efiling);
    }

    [Fact]
    public void เลื่อนได้เฉพาะไปข้างหน้า_ห้ามถอยหลัง()
    {
        foreach (var d in Enumerable.Range(0, 400).Select(i => new DateTime(2026, 1, 1).AddDays(i)))
            Assert.True(TaxFilingDeadline.RollToBusinessDay(d) >= d);
    }

    // ═══ map จาก TaxType ═══

    [Fact]
    public void แบบที่ไม่ใช่รายเดือน_ต้องคืน_null_ไม่ใช่เดาวัน()
    {
        Assert.Null(TaxFilingDeadline.EFilingFor(TaxType.CorporateIncomeTax, 2026, 9));
        Assert.Null(TaxFilingDeadline.EFilingFor(TaxType.StampDuty, 2026, 9));
        Assert.Null(TaxFilingDeadline.EFilingFor(TaxType.VAT, 2017, 9));   // ก่อนช่วงที่รองรับ
        Assert.Null(TaxFilingDeadline.EFilingFor(TaxType.VAT, 2026, 13));  // เดือนไม่ถูกต้อง
    }
}
