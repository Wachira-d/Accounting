using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ERP_REVIEW_2026-09-05 H-01 — Void รอบที่นำส่ง สปส. แล้วต้องถูกบล็อกด้วยกติกาเดียวกับ Reopen
/// (เดิม VoidPayrollAsync ตรวจแค่ "Voided ซ้ำ" ⇒ กลับ JE จ่ายแต่ JE นำส่งอยู่ ⇒ 21815 ติดลบถาวร)
/// </summary>
public class PayrollRunVoidPolicyTests
{
    [Theory]
    [InlineData(PayrollRunEditPolicy.Draft)]
    [InlineData(PayrollRunEditPolicy.Calculated)]
    [InlineData(PayrollRunEditPolicy.Approved)]
    [InlineData(PayrollRunEditPolicy.Paid)]
    public void ยังไม่นำส่ง_สปส_ยกเลิกได้ทุกสถานะที่ไม่ใช่_Voided(string status)
    {
        var (can, reason) = PayrollRunEditPolicy.CanVoid(status, ssoSettledAt: null);
        Assert.True(can);
        Assert.Null(reason);
    }

    [Fact]
    public void ยกเลิกซ้ำไม่ได้()
    {
        var (can, reason) = PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Voided, null);
        Assert.False(can);
        Assert.Contains("ถูกยกเลิกแล้ว", reason);
    }

    [Fact]
    public void นำส่ง_สปส_แล้ว_ยกเลิกไม่ได้_และบอกทางไปต่อ()
    {
        var settled = new DateTime(2026, 9, 15, 3, 0, 0, DateTimeKind.Utc);
        var (can, reason) = PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Paid, settled);
        Assert.False(can);
        Assert.Contains("15/09/2026", reason);          // ค.ศ. เสมอ (InvariantCulture)
        Assert.Contains("กลับรายการนำส่ง สปส. ก่อน", reason);
    }

    [Fact]
    public void กติกา_สปส_ของ_Void_และ_Reopen_ต้องตรงกัน()
    {
        var settled = new DateTime(2026, 9, 15);
        var (canVoid, _) = PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Paid, settled);
        var (canReopen, _) = PayrollRunEditPolicy.CanReopen(PayrollRunEditPolicy.Paid, settled);
        Assert.Equal(canReopen, canVoid);   // ด่านครอบทางเดียว = ด่านที่ไม่มี — ล็อกให้เท่ากันเสมอ
    }
}
