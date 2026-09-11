using System.Text.RegularExpressions;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// regex ของแพตเทิร์นที่เรียนไว้ต้องตามรูปทรงของค่า — ไม่ใช่กวาดทุกอย่าง
///
/// ที่มา (สแกนจริง 2026-09-11): เลขที่เอกสารถูกเรียนด้วย <c>([A-Za-z0-9\-/]+)</c> ⇒ สแกน
/// รอบถัดไปคว้า token แรกในรัศมีค้น ("CASHSALE" หัวแบบฟอร์ม) มาเป็นเลขที่เอกสาร
/// </summary>
public class LearnedPatternShapeRegexTests
{
    [Fact]
    public void เลขที่แบบINV_ต้องจับเฉพาะรูปทรงเดียวกัน()
    {
        var rx = new Regex(DocumentZoneAnalyzer.BuildExtractionRegex("INV-2026-0042"));
        Assert.True(rx.IsMatch("เลขที่ INV-2026-0043"));       // ใบถัดไปของผู้ขายเดิม
        Assert.True(rx.IsMatch("เลขที่ INV-2026-10042"));      // เลขรันเกินหลัก (ยอมยาวขึ้น 1)
        Assert.False(rx.IsMatch("CASHSALE"));                  // คำบนแบบฟอร์ม
        Assert.False(rx.IsMatch("เลขที่ 177/18 ม.5"));          // เลขที่บ้าน
        Assert.False(rx.IsMatch("REC-2026-0042"));             // คนละชุด (ตัวอักษรต่าง)
    }

    [Fact]
    public void เลขที่บ้านรูปทับ_regexที่ได้ต้องไม่กวาดคำอื่น()
    {
        var rx = new Regex(DocumentZoneAnalyzer.BuildExtractionRegex("177/18"));
        Assert.True(rx.IsMatch("177/18 ม.5"));
        Assert.False(rx.IsMatch("CASHSALE"));
        Assert.False(rx.IsMatch("A177/18"));                   // ต้องมีขอบคำ
    }

    [Fact]
    public void เลขภาษี13หลัก_ยังใช้แพตเทิร์นกลางของThaiTaxId()
        => Assert.Equal(Accounting.Helpers.ThaiTaxId.Pattern,
            DocumentZoneAnalyzer.BuildExtractionRegex("0105550027134"));
}
