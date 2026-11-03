using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 232 (คำตัดสิน 232-Omise-2) — เงินโหมดทดสอบของร้านค้าห้ามมีผลทางธุรกิจ · สองทิศ: ทดสอบ ⇒ กัน (ทุกต้นทางยกเว้นทดลองจ่ายของระบบ) ·
/// เงินจริง ⇒ ไม่แตะ (ทุกต้นทาง) · ป้ายสถานะ/ข้อความไทย
/// </summary>
public class TestModePaymentPolicyRound232Tests
{
    public static IEnumerable<object[]> ShopKinds() => new[]
    {
        PaymentSourceKind.SiteOrder, PaymentSourceKind.Document, PaymentSourceKind.LodgingReservation, PaymentSourceKind.PosOrder,
        PaymentSourceKind.SiteBooking, PaymentSourceKind.SubscriptionPayment, PaymentSourceKind.AddOnPurchase, PaymentSourceKind.QuotaAddOnPurchase,
    }.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(ShopKinds))]
    public void โหมดทดสอบ_ทุกต้นทางของร้านและแพลตฟอร์ม_ไม่มีผลทางธุรกิจ(PaymentSourceKind kind)
    {
        Assert.False(TestModePaymentPolicy.AllowsBusinessEffects(isTestMode: true, kind));
        var msg = TestModePaymentPolicy.BlockMessage(kind);
        Assert.StartsWith(TestModePaymentPolicy.NotePrefix, msg);
        Assert.Contains(PaymentIntentPolicy.SourceKindLabel(kind), msg);
        Assert.Contains("คงรอชำระ", msg);
        Assert.DoesNotMatch("[A-Za-z]{7,}", msg);   // ป้ายต้นทางมี "portal"/"POS" ได้ · ไม่มีประโยคอังกฤษ
    }

    [Theory]
    [MemberData(nameof(ShopKinds))]
    public void ทิศตรงข้าม_เงินจริง_ทุกต้นทางทำงานตามเดิม(PaymentSourceKind kind)
        => Assert.True(TestModePaymentPolicy.AllowsBusinessEffects(isTestMode: false, kind));

    [Fact]
    public void ทดลองจ่ายของระบบ_โหมดทดสอบยังผ่าน_เพื่อประทับLastSelfTestPaidAt()
    {
        Assert.True(TestModePaymentPolicy.AllowsBusinessEffects(true, PaymentSourceKind.GatewaySelfTest));
        Assert.True(TestModePaymentPolicy.AllowsBusinessEffects(false, PaymentSourceKind.GatewaySelfTest));
    }

    [Fact]
    public void คำนำหน้าเหตุการณ์_ไม่ใช่งานค้างส่งต่อใหม่()
    {
        Assert.False(TestModePaymentPolicy.NotePrefix.StartsWith(PaymentRedispatchPolicy.UnfulfilledNotePrefix, StringComparison.Ordinal));
        Assert.False(TestModePaymentPolicy.BlockMessage(PaymentSourceKind.SiteOrder).StartsWith(PaymentRedispatchPolicy.UnfulfilledNotePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void หมายเหตุถึงผู้จ่าย_S3_เฉพาะเงินทดสอบที่สำเร็จ_ทดลองจ่ายของระบบข้อความต่างกัน()
    {
        Assert.Contains("ยังรอชำระจริง", TestModePaymentPolicy.PayerNote(PaymentIntentStatus.Succeeded, true, PaymentSourceKind.SiteOrder));
        Assert.DoesNotContain("ต้นทาง", TestModePaymentPolicy.PayerNote(PaymentIntentStatus.Succeeded, true, PaymentSourceKind.GatewaySelfTest)!);
        Assert.Null(TestModePaymentPolicy.PayerNote(PaymentIntentStatus.Succeeded, false, PaymentSourceKind.SiteOrder));
        Assert.Null(TestModePaymentPolicy.PayerNote(PaymentIntentStatus.Pending, true, PaymentSourceKind.SiteOrder));
    }

    [Fact]
    public void S2_เงินทดสอบขาคืน_IsNoMoney_กับ_AllowsBusinessEffects_ต่างกันเฉพาะทดลองจ่ายของระบบ()
    {
        // ทดลองจ่ายของระบบ: ไม่มีเงิน (ไม่ลง JE ขาเข้า/ขาคืน) แต่ยัง "มีผล" (ประทับ LastSelfTestPaidAt)
        Assert.True(TestModePaymentPolicy.IsNoMoney(PaymentSourceKind.GatewaySelfTest, false));
        Assert.True(TestModePaymentPolicy.AllowsBusinessEffects(true, PaymentSourceKind.GatewaySelfTest));
        Assert.True(TestModePaymentPolicy.IsNoMoney(PaymentSourceKind.SiteOrder, true));
        Assert.False(TestModePaymentPolicy.IsNoMoney(PaymentSourceKind.SiteOrder, false));
        Assert.NotNull(TestModePaymentPolicy.RefundRefusal(PaymentSourceKind.GatewaySelfTest));
        Assert.Null(TestModePaymentPolicy.RefundRefusal(PaymentSourceKind.Document));
    }

    [Fact]
    public void ป้ายสถานะ_ทดสอบสำเร็จ_ติดท้าย_อื่นคงเดิม()
    {
        Assert.Equal("สำเร็จ (ทดสอบ — ไม่ลงบัญชี)", TestModePaymentPolicy.StatusLabel(PaymentIntentStatus.Succeeded, true));
        Assert.Equal("คืนเงินแล้ว (ทดสอบ — ไม่ลงบัญชี)", TestModePaymentPolicy.StatusLabel(PaymentIntentStatus.Refunded, true));
        Assert.Equal("สำเร็จ", TestModePaymentPolicy.StatusLabel(PaymentIntentStatus.Succeeded, false));
        Assert.Equal("รอชำระ", TestModePaymentPolicy.StatusLabel(PaymentIntentStatus.Pending, true));
        Assert.Equal("ล้มเหลว", TestModePaymentPolicy.StatusLabel(PaymentIntentStatus.Failed, true));
    }
}
