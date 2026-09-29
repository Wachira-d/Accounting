using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 200 ทีม R · A12/E-04 — แก้อายุ/ซาก/วิธีคิดค่าเสื่อมของสินทรัพย์ที่ลงทะเบียนแล้ว
/// สองทิศ: ยังไม่มีค่าเสื่อมลงบัญชี = แก้ได้ (เดิมไม่มีผลเงียบ ๆ) · ลงบัญชีแล้ว/ที่ดิน/ค่าผิดรูป = ปฏิเสธพร้อมเหตุผล</summary>
public class FixedAssetValuationEditTests
{
    [Fact]
    public void ยังไม่มีค่าเสื่อมลงบัญชี_แก้อายุและวิธีคิดได้()
        => Assert.Null(FixedAssetValuationEdit.Problem(false, DepreciationMethod.DecliningBalance, 36, 1_000m, 50_000m, false));

    [Fact]
    public void มีค่าเสื่อมลงบัญชีแล้ว_ปฏิเสธพร้อมทางไปต่อ()
    {
        var p = FixedAssetValuationEdit.Problem(true, DepreciationMethod.StraightLine, 60, 0m, 50_000m, false);
        Assert.NotNull(p);
        Assert.Contains("ลงบัญชีแล้ว", p);
    }

    [Fact]
    public void ผังที่ดิน_กับวิธีคิดค่าเสื่อม_ปฏิเสธ()
        => Assert.NotNull(FixedAssetValuationEdit.Problem(false, DepreciationMethod.StraightLine, 60, 0m, 5_000_000m, true));

    [Fact]
    public void ผังที่ดิน_เปลี่ยนเป็นไม่คิดค่าเสื่อม_ได้()
        => Assert.Null(FixedAssetValuationEdit.Problem(false, DepreciationMethod.None, 0, 0m, 5_000_000m, true));

    [Theory]
    [InlineData(0, 0)]           // อายุ 0 กับวิธีที่ต้องคิดค่าเสื่อม
    [InlineData(60, 50_000)]     // ซาก = ราคาทุน
    [InlineData(60, -1)]         // ซากติดลบ
    public void ค่าผิดรูป_ปฏิเสธ(int life, decimal salvage)
        => Assert.NotNull(FixedAssetValuationEdit.Problem(false, DepreciationMethod.StraightLine, life, salvage, 50_000m, false));
}
