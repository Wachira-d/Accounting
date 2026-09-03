using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กระทบยอดเงินที่รับผ่าน payment gateway (PAYMENT_GATEWAY_DESIGN.md §4.5 · มุมมอง CPA)
///
/// ═══ ทำไมต้องมี ═══
/// เงินที่ลูกค้าจ่ายผ่าน gateway <b>ไม่ได้เข้าบัญชีธนาคารทันที</b> — ผู้ให้บริการโอนเข้า
/// T+n <b>หลังหักค่าธรรมเนียม</b> ⇒ ถ้าลง Dr ธนาคารตั้งแต่ตอน charge สำเร็จ ยอดธนาคาร
/// ในระบบจะไม่ตรงกับยอดจริง<b>ตลอดเวลา</b> และผู้ทำบัญชีกระทบยอดไม่ได้เลย
///
/// สมการ: <c>Σ charge − Σ คืนเงิน − Σ ค่าธรรมเนียม = Σ ที่โอนเข้าจริง + ที่ยังไม่ถึงรอบโอน</c>
/// </summary>
public class GatewayReconciliationTests
{
    private static GatewayIntentAmounts Settled(decimal amount, decimal fee)
        => new(amount, fee, fee, amount - fee, IsRefundedFully: false, IsSettled: true);

    private static GatewayIntentAmounts Pending(decimal amount, decimal estimatedFee)
        => new(amount, null, estimatedFee, null, IsRefundedFully: false, IsSettled: false);

    [Fact]
    public void ทุกอย่างโอนเข้าครบแล้ว_ต้องสมดุลพอดี()
    {
        var r = GatewayReconciliation.Compute(new[] { Settled(1000m, 20m), Settled(500m, 10m) });
        Assert.Equal(1500m, r.GrossCharged);
        Assert.Equal(30m, r.FeeTotal);
        Assert.Equal(1470m, r.ExpectedNet);
        Assert.Equal(1470m, r.SettledTotal);
        Assert.Equal(0m, r.UnexplainedDifference);
        Assert.True(r.IsBalanced);
        Assert.False(r.FeeIsEstimated);   // มีค่าธรรมเนียมจริงครบทุกแถว
    }

    [Fact]
    public void รายการที่ยังไม่ถึงรอบโอน_ต้องไม่ทำให้รายงานขึ้นแดง()
    {
        // นี่คือสภาพปกติทุกวัน — ถ้านับเป็น "ผิด" รายงานจะแดงตลอดจนไม่มีใครดู
        // แล้วผลต่างของจริงจะถูกกลบ
        var r = GatewayReconciliation.Compute(new[] { Settled(1000m, 20m), Pending(500m, 10m) });
        Assert.Equal(1, r.UnsettledCount);
        Assert.Equal(490m, r.UnsettledAmount);
        Assert.Equal(490m, r.Difference);              // ต่างเท่ากับที่ยังไม่โอนพอดี
        Assert.Equal(0m, r.UnexplainedDifference);
        Assert.True(r.IsBalanced);
    }

    [Fact]
    public void ค่าธรรมเนียมจริงมากกว่าที่ประมาณไว้_ต้องโผล่เป็นผลต่างที่อธิบายไม่ได้()
    {
        // ผู้ให้บริการหัก 35 แต่เราประมาณไว้ 20 · ตัวเลขนี้ต้องเห็น ไม่ใช่ถูกกลืน
        var actual = new GatewayIntentAmounts(1000m, 35m, 20m, 950m, false, true);
        var r = GatewayReconciliation.Compute(new[] { actual });
        Assert.Equal(35m, r.FeeTotal);          // ใช้ตัวจริง ไม่ใช่ตัวประมาณ
        Assert.Equal(965m, r.ExpectedNet);
        Assert.Equal(950m, r.SettledTotal);
        Assert.Equal(15m, r.UnexplainedDifference);
        Assert.False(r.IsBalanced);
    }

    [Fact]
    public void คืนเงินเต็มจำนวน_ต้องหักออกจากยอดที่ควรได้()
    {
        var refunded = new GatewayIntentAmounts(300m, 6m, 6m, null, IsRefundedFully: true, IsSettled: false);
        var r = GatewayReconciliation.Compute(new[] { Settled(1000m, 20m), refunded });
        Assert.Equal(300m, r.RefundedAmount);
        // ยอดที่คืนไปแล้วไม่นับเป็น "ยังไม่ถึงรอบโอน" — มันจะไม่มีวันโอนเข้า
        Assert.Equal(0, r.UnsettledCount);
    }

    [Fact]
    public void ค่าธรรมเนียมที่ยังเป็นตัวประมาณ_ต้องติดป้ายบอก()
    {
        // ตัวเลขประมาณที่ไม่ติดป้ายจะถูกอ่านเป็นตัวจริง แล้วนำไปใช้ตัดสินใจผิด
        var r = GatewayReconciliation.Compute(new[] { Pending(1000m, 20m) });
        Assert.True(r.FeeIsEstimated);
        Assert.Equal(20m, r.FeeTotal);
    }

    [Fact]
    public void ไม่มีรายการเลย_ต้องไม่ระเบิดและถือว่าสมดุล()
    {
        var r = GatewayReconciliation.Compute(Array.Empty<GatewayIntentAmounts>());
        Assert.Equal(0, r.SucceededCount);
        Assert.Equal(0m, r.GrossCharged);
        Assert.True(r.IsBalanced);
    }

    [Fact]
    public void ปัดเงินแบบ_AwayFromZero_ไม่ใช่ธนาคาร()
    {
        // banker's rounding เป็นค่า default ของ .NET และเคยทำให้ยอดเพี้ยนมาแล้ว
        // 3 รายการ ๆ ละ 0.005 ⇒ 0.015 ⇒ 0.02 (ธนาคารจะได้ 0.01)
        var rows = Enumerable.Repeat(new GatewayIntentAmounts(0.005m, 0m, 0m, 0.005m, false, true), 3);
        var r = GatewayReconciliation.Compute(rows);
        Assert.Equal(0.02m, r.GrossCharged);
    }
}
