using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ธง <c>ChartOfAccount.InputVatClaimable</c> ต้องบังคับได้ทั้งเส้นคีย์มือและเส้น OCR
/// — เดิมเส้น OCR สร้างเอกสารเองจึงข้ามด่านนี้ (ผลตรวจ 2026-09-06 · T1-05)
/// </summary>
public class InputVatAccountPolicyTests
{
    [Fact]
    public void บัญชีที่ตั้งว่าเคลมไม่ได้_บังคับปิดเคลมพร้อมเหตุผล()
    {
        var r = InputVatAccountPolicy.Apply(requestedClaimable: true, requestedReason: null, accountClaimable: false);
        Assert.False(r.Claimable);
        Assert.Equal(InputVatAccountPolicy.ChartFlagReason, r.Reason);
        Assert.True(r.Changed);
    }

    [Fact]
    public void เหตุผลที่มีอยู่แล้วไม่ถูกเขียนทับ()
    {
        var r = InputVatAccountPolicy.Apply(true, "ใบกำกับอย่างย่อ §82/5(2)", false);
        Assert.False(r.Claimable);
        Assert.Equal("ใบกำกับอย่างย่อ §82/5(2)", r.Reason);
    }

    [Fact]
    public void บัญชีปกติ_ไม่แตะค่าที่ผู้เรียกส่งมา()
    {
        var yes = InputVatAccountPolicy.Apply(true, null, accountClaimable: true);
        Assert.True(yes.Claimable);
        Assert.False(yes.Changed);

        var no = InputVatAccountPolicy.Apply(false, "เอกสารต้นทางเคลมไม่ได้", true);
        Assert.False(no.Claimable);
        Assert.Equal("เอกสารต้นทางเคลมไม่ได้", no.Reason);
        Assert.False(no.Changed);
    }

    [Fact]
    public void ไม่รู้จักบัญชี_ไม่บังคับอะไร()
    {
        var r = InputVatAccountPolicy.Apply(true, null, accountClaimable: null);
        Assert.True(r.Claimable);
        Assert.False(r.Changed);
    }
}
