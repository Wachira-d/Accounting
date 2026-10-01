using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม IN — A-IN4 (คำตัดสินข้อ 35): เล่มเลขเอกสารต่อสาขา · ส่วนเครื่องออกเลข
///
/// <para>สองทิศ: ไม่ส่งสาขา/สำนักงานใหญ่ = เล่มเดิมของบริษัททุกประการ (เลขเดิม) · สาขาอื่น = เล่มแยกที่ไม่ทับเล่มบริษัท ·
/// รหัสสาขาผิดรูปห้ามตกไปเล่มสำนักงานใหญ่เงียบ ๆ</para>
/// </summary>
public class DocumentNumberBookTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("00000")]
    public void ไม่ส่งสาขาหรือสำนักงานใหญ่_เล่มเดิมของบริษัท(string? branch)
        => Assert.Equal("TIV", DocumentNumberBook.BookPrefix("TIV", branch));

    [Fact]
    public void สาขาอื่น_เล่มแยก_และไม่ทับคำนำหน้าเลขของเล่มบริษัท()
    {
        var book = DocumentNumberBook.BookPrefix("TIV", " 00002 ");
        Assert.Equal("TIV-00002", book);
        var hqDay = "TIV-20260615-";
        var branchDay = $"{book}-20260615-";
        Assert.False(branchDay.StartsWith(hqDay, StringComparison.Ordinal));
        Assert.False(hqDay.StartsWith(branchDay, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("0000a")]
    [InlineData("000001")]
    [InlineData("สาขา1")]
    public void รหัสสาขาผิดรูป_ปฏิเสธดัง(string branch)
    {
        var ex = Assert.Throws<BusinessRuleException>(() => DocumentNumberBook.BookPrefix("TIV", branch));
        Assert.Equal("DOC-NUMBER-BRANCH", ex.RuleCode);
    }
}
