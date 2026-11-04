using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;
using EffectKind = Accounting.Helpers.TestModeLegacyEffectPolicy.EffectKind;
using TestEvidence = Accounting.Helpers.TestModeLegacyEffectPolicy.TestEvidence;

namespace Accounting.Tests;

/// <summary>
/// รอบ 235 (คำตัดสิน 235-X3 + ฝ่ายค้าน B1/S5) — ตัวคัดกรอง "เงินโหมดทดสอบที่ลงบัญชีไปแล้ว" (pure) · ล็อกสองทิศ: ทดสอบ+ได้เงิน+ต้นทางร้าน+ก่อนรอบ 232+มีผล ⇒ รายงาน ·
/// เงินจริง/ค่าบริการแพลตฟอร์ม/ทดลองจ่ายของระบบ/ยังไม่ได้เงิน/ไม่มีผล/หลังรอบ 232 ⇒ ไม่รายงาน · ปุ่มกลับรายการเฉพาะรับชำระเอกสาร <b>ที่มีหลักฐาน</b>
/// (ประทับ true หรือ <c>chrg_test_</c>) — แถวที่มีแค่โหมดของช่องทาง (อาจเป็นเงินจริงที่ช่องทางถูกสลับเป็น Test ทีหลัง) ไม่มีปุ่ม
/// </summary>
public class TestModeLegacyEffectPolicyRound235Tests
{
    private static readonly DateTime Before = TestModeLegacyEffectPolicy.GuardSinceUtc.AddDays(-30);
    private static readonly DateTime After = TestModeLegacyEffectPolicy.GuardSinceUtc.AddDays(1);

    [Theory]
    [InlineData(PaymentSourceKind.Document)]
    [InlineData(PaymentSourceKind.SiteOrder)]
    [InlineData(PaymentSourceKind.PosOrder)]
    [InlineData(PaymentSourceKind.LodgingReservation)]
    [InlineData(PaymentSourceKind.SiteBooking)]
    public void ต้นทางร้าน_ทดสอบ_ได้เงิน_มีผล_ขึ้นรายงาน(PaymentSourceKind kind)
    {
        Assert.True(TestModeLegacyEffectPolicy.ShouldReport(kind, PaymentIntentStatus.Succeeded, TestEvidence.Stamped, Before, 1));
        Assert.True(TestModeLegacyEffectPolicy.ShouldReport(kind, PaymentIntentStatus.PartiallyRefunded, TestEvidence.ConfigOnly, Before, 1));
    }

    [Theory]
    [InlineData(PaymentSourceKind.SubscriptionPayment)]
    [InlineData(PaymentSourceKind.AddOnPurchase)]
    [InlineData(PaymentSourceKind.QuotaAddOnPurchase)]
    [InlineData(PaymentSourceKind.GatewaySelfTest)]
    public void เงินแพลตฟอร์มและทดลองจ่ายของระบบ_ไม่ขึ้น(PaymentSourceKind kind)
        => Assert.False(TestModeLegacyEffectPolicy.ShouldReport(kind, PaymentIntentStatus.Succeeded, TestEvidence.Stamped, Before, 1));

    [Fact]
    public void เงินจริง_ไม่ขึ้นแม้มีผล()
        => Assert.False(TestModeLegacyEffectPolicy.ShouldReport(PaymentSourceKind.Document, PaymentIntentStatus.Succeeded, TestEvidence.None, Before, 3));

    [Fact]
    public void ไม่มีผล_หรือสร้างหลังรอบ_232_ไม่ขึ้น()
    {
        Assert.False(TestModeLegacyEffectPolicy.ShouldReport(PaymentSourceKind.Document, PaymentIntentStatus.Succeeded, TestEvidence.Stamped, Before, 0));
        Assert.False(TestModeLegacyEffectPolicy.ShouldReport(PaymentSourceKind.Document, PaymentIntentStatus.Succeeded, TestEvidence.Stamped, After, 1));
    }

    [Theory]
    [InlineData(PaymentIntentStatus.Pending)]
    [InlineData(PaymentIntentStatus.Failed)]
    [InlineData(PaymentIntentStatus.Created)]
    public void ยังไม่ได้เงิน_ไม่ขึ้น(PaymentIntentStatus status)
        => Assert.False(TestModeLegacyEffectPolicy.ShouldReport(PaymentSourceKind.Document, status, TestEvidence.Stamped, Before, 1));

    // ── B1: หลักฐาน ──
    [Fact]
    public void Evidence_ประทับชนะ_เลขอ้างอิงทดสอบ_ก่อนโหมดของช่องทาง()
    {
        Assert.Equal(TestEvidence.Stamped, TestModeLegacyEffectPolicy.Evidence(true, true, "omise", "chrg_5abc"));
        Assert.Equal(TestEvidence.None, TestModeLegacyEffectPolicy.Evidence(false, false, "omise", "chrg_test_5abc"));   // ประทับเงินจริงชนะ
        Assert.Equal(TestEvidence.ProviderTestRef, TestModeLegacyEffectPolicy.Evidence(null, false, "omise", "chrg_test_5abc"));
        Assert.Equal(TestEvidence.ConfigOnly, TestModeLegacyEffectPolicy.Evidence(null, true, "omise", "chrg_5abc"));
        Assert.Equal(TestEvidence.None, TestModeLegacyEffectPolicy.Evidence(null, false, "omise", "chrg_5abc"));
    }

    [Theory]
    [InlineData("omise", "chrg_test_5xyz", true)]
    [InlineData("OMISE", "chrg_test_5xyz", true)]
    [InlineData("omise", "chrg_5xyz", false)]          // live charge
    [InlineData("omise", null, false)]
    [InlineData("manual-slip", "chrg_test_5xyz", false)] // ผู้ให้บริการอื่นไม่ใช้รูปนี้
    public void หลักฐานจากเลข_charge_ทดสอบของ_Omise(string code, string? reference, bool expected)
        => Assert.Equal(expected ? TestEvidence.ProviderTestRef : TestEvidence.None,
            TestModeLegacyEffectPolicy.Evidence(null, false, code, reference));

    [Fact]
    public void ปุ่มกลับรายการ_เฉพาะรับชำระเอกสารที่มีหลักฐาน()
    {
        Assert.True(TestModeLegacyEffectPolicy.CanReverseHere(EffectKind.DocumentPayment, TestEvidence.Stamped));
        Assert.True(TestModeLegacyEffectPolicy.CanReverseHere(EffectKind.DocumentPayment, TestEvidence.ProviderTestRef));
        Assert.False(TestModeLegacyEffectPolicy.CanReverseHere(EffectKind.DocumentPayment, TestEvidence.ConfigOnly));   // B1 ทิศตรงข้าม
        foreach (var k in new[] { EffectKind.SiteOrderPayment, EffectKind.PosPayment, EffectKind.LodgingDeposit, EffectKind.BookingDeposit })
        {
            Assert.False(TestModeLegacyEffectPolicy.CanReverseHere(k, TestEvidence.Stamped));
            Assert.False(string.IsNullOrWhiteSpace(TestModeLegacyEffectPolicy.ReverseHint(k, TestEvidence.Stamped)));   // ทางไปต่อ
        }
        Assert.Equal(TestModeLegacyEffectPolicy.UnprovenMessage, TestModeLegacyEffectPolicy.ReverseHint(EffectKind.DocumentPayment, TestEvidence.ConfigOnly));
    }

    [Fact]
    public void ป้ายแถวพิสูจน์ไม่ได้_ห้ามยืนยันว่าไม่มีเงินจริง()
    {
        Assert.DoesNotContain("ไม่มีเงินจริง", TestModeLegacyEffectPolicy.EvidenceLabel(TestEvidence.ConfigOnly));
        Assert.Contains("ไม่มีเงินจริง", TestModeLegacyEffectPolicy.EvidenceLabel(TestEvidence.Stamped));
    }

    [Fact]
    public void ReverseRefusal_ตัดสินซ้ำฝั่งเซิร์ฟเวอร์()
    {
        Assert.Null(TestModeLegacyEffectPolicy.ReverseRefusal(true, true, EffectKind.DocumentPayment, TestEvidence.Stamped));
        Assert.NotNull(TestModeLegacyEffectPolicy.ReverseRefusal(false, true, EffectKind.DocumentPayment, TestEvidence.Stamped));
        Assert.Equal(TestModeLegacyEffectPolicy.UnprovenMessage,
            TestModeLegacyEffectPolicy.ReverseRefusal(true, false, null, TestEvidence.ConfigOnly));
        Assert.NotNull(TestModeLegacyEffectPolicy.ReverseRefusal(true, false, null, TestEvidence.Stamped));
        Assert.Equal(TestModeLegacyEffectPolicy.ReverseHint(EffectKind.PosPayment, TestEvidence.Stamped),
            TestModeLegacyEffectPolicy.ReverseRefusal(true, true, EffectKind.PosPayment, TestEvidence.Stamped));
    }
}
