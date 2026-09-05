using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ERP_REVIEW_2026-09-05 H-07 — ทิป POS เดิม fallback prefix "216" ⇒ ผังมาตรฐานได้ 21610 "เงินมัดจำรับ"
/// ตอนนี้ตั้งค่าได้ + ค่าแนะนำเป็นบัญชีค้างจ่ายพนักงาน · ลำดับรหัสที่ลองต้องไม่มี 216xx เลย
/// </summary>
public class TipAccountResolverTests
{
    [Fact]
    public void ไม่ตั้งค่า_ใช้ค่าแนะนำตามลำดับ()
    {
        var c = TipAccountResolver.CodeCandidates(null);
        Assert.Equal(new[] { "21814", "21819" }, c);
    }

    [Fact]
    public void ตั้งค่าแล้ว_รหัสที่ตั้งมาก่อนค่าแนะนำ()
    {
        var c = TipAccountResolver.CodeCandidates(" 21819 ");
        Assert.Equal("21819", c[0]);
        Assert.Equal(2, c.Count);   // ไม่ซ้ำกับ default
    }

    [Fact]
    public void ค่าว่าง_เท่ากับไม่ตั้งค่า()
        => Assert.Equal(TipAccountResolver.CodeCandidates(null), TipAccountResolver.CodeCandidates("   "));

    [Fact]
    public void บั๊กเดิม_H07_ไม่มีรหัสเงินมัดจำในลำดับที่ลอง()
    {
        foreach (var code in TipAccountResolver.CodeCandidates(null))
            Assert.False(code.StartsWith("216"), $"{code} เป็นบัญชีเงินมัดจำ — ทิปไม่ใช่มัดจำ");
    }
}
