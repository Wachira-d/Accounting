using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 200 ทีม R · D-06 — เลขประกันสังคมในไฟล์ สปส. (.txt)
/// สองทิศ: ช่องว่าง + เลขบัตรถูกต้อง = ใช้เลขบัตร (ไฟล์อัปโหลดผ่าน) · มีเลขประกันสังคมอยู่แล้ว = ใช้ค่าเดิม · ไม่มีทั้งคู่ = ว่าง + เตือน (ไม่แต่งเลข)</summary>
public class SsoInsuredNumberTests
{
    [Fact]
    public void ช่องว่าง_ใช้เลขบัตรประชาชนที่ถูกต้อง()
    {
        var (n, fromCid) = SsoInsuredNumber.Resolve(null, "1-1017-00203-59-0");
        Assert.Equal("1101700203590", n);
        Assert.True(fromCid);
    }

    [Fact]
    public void มีเลขประกันสังคมอยู่แล้ว_ไม่ถูกแตะ()
    {
        var (n, fromCid) = SsoInsuredNumber.Resolve("3101500123459", "1101700203590");
        Assert.Equal("3101500123459", n);
        Assert.False(fromCid);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "1101700203591")]   // checksum ผิด
    [InlineData(" ", "AB1234567")]      // พาสปอร์ต
    public void ไม่มีเลขที่ใช้ได้_ว่างและไม่แต่ง(string? ssn, string? cid)
    {
        var (n, fromCid) = SsoInsuredNumber.Resolve(ssn, cid);
        Assert.Equal("", n);
        Assert.False(fromCid);
    }

    [Fact]
    public void ข้อความเตือน_มีเฉพาะเมื่อมีแถวขาด()
    {
        Assert.Null(SsoInsuredNumber.MissingNotice(0));
        Assert.Contains("2 คน", SsoInsuredNumber.MissingNotice(2));
    }
}
