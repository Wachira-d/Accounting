using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 200 ทีม R · E-03 — นำเข้าทะเบียนสินทรัพย์ต้องเดินด่านที่ดิน/งานระหว่างก่อสร้างเหมือนเส้นสร้างด้วยมือ
/// สองทิศ: ที่ดินไม่ถูกสร้างเป็นเส้นตรง · แถวอุปกรณ์/วิธีคิดที่ถูกต้องได้ผลเดิม</summary>
public class FixedAssetImportMethodTests
{
    [Theory]
    [InlineData("", "ที่ดิน")]
    [InlineData(null, "Land")]
    [InlineData("None", "งานระหว่างก่อสร้าง")]
    [InlineData("ไม่คิดค่าเสื่อม", "CIP")]
    public void ที่ดิน_งานระหว่างก่อสร้าง_ว่างหรือNone_ได้ไม่คิดค่าเสื่อม(string? method, string category)
    {
        var (m, err) = FixedAssetImportMethod.Resolve(method, category);
        Assert.Null(err);
        Assert.Equal(DepreciationMethod.None, m);
    }

    [Theory]
    [InlineData("StraightLine", "ที่ดิน")]
    [InlineData("เส้นตรง", "Land")]
    public void ที่ดิน_กับวิธีคิดค่าเสื่อม_ปฏิเสธแถวพร้อมทางแก้(string method, string category)
    {
        var (m, err) = FixedAssetImportMethod.Resolve(method, category);
        Assert.Null(m);
        Assert.Contains("None", err);
    }

    [Fact]
    public void ข้อความที่ไม่รู้จัก_ปฏิเสธ_ไม่เดาเป็นเส้นตรง()
    {
        var (m, err) = FixedAssetImportMethod.Resolve("straigth", "คอมพิวเตอร์");
        Assert.Null(m);
        Assert.NotNull(err);
    }

    [Theory]
    [InlineData("", DepreciationMethod.StraightLine)]
    [InlineData(null, DepreciationMethod.StraightLine)]
    [InlineData("StraightLine", DepreciationMethod.StraightLine)]
    [InlineData("declining", DepreciationMethod.DecliningBalance)]
    [InlineData("ยอดลดลงทวีคูณ", DepreciationMethod.DoubleDecliningBalance)]
    [InlineData("DoubleDecliningBalance", DepreciationMethod.DoubleDecliningBalance)]
    public void แถวทั่วไป_ได้ผลเดิม(string? method, DepreciationMethod expected)
    {
        var (m, err) = FixedAssetImportMethod.Resolve(method, "อุปกรณ์สำนักงาน");
        Assert.Null(err);
        Assert.Equal(expected, m);
    }

    [Fact]
    public void ปรับปรุงที่ดิน_ไม่ใช่ที่ดิน_คิดค่าเสื่อมได้()
    {
        var (m, err) = FixedAssetImportMethod.Resolve("StraightLine", "ปรับปรุงที่ดิน");
        Assert.Null(err);
        Assert.Equal(DepreciationMethod.StraightLine, m);
    }
}
