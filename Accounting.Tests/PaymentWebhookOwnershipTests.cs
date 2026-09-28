using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 198 ฝ่ายค้าน R-E1 (P0) — webhook ที่ลายเซ็นผ่านด้วยคีย์ของบริษัทหนึ่ง ต้องไม่ปิดหนี้ของอีกบริษัท
/// (เคสโจมตี: ผู้ไม่หวังดีเปิด tenant เอง + คีย์ทดสอบ Omise ฟรี · ใส่รหัสรายการของร้าน X ใน metadata · charge 0 บาท
/// ⇒ ใบแจ้งหนี้ 10,000 ของร้าน X กลายเป็น "ชำระแล้ว")
/// </summary>
public class PaymentWebhookOwnershipTests
{
    private static readonly Guid ShopX = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Attacker = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CfgX = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CfgAttacker = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static PaymentWebhookOwnership.IntentFacts XIntent(decimal amount = 10_000m, Guid? cfg = null)
        => new(ShopX, cfg ?? CfgX, "omise", amount);

    [Fact]
    public void คีย์ของบริษัทอื่น_รายการของร้าน_X_ถูกปฏิเสธ()
    {
        // controller โหลดรายการ "ในบริษัทของ config" ⇒ บริษัทผู้โจมตีหาไม่เจอ = null
        Assert.NotNull(PaymentWebhookOwnership.RejectReason(null, Attacker, CfgAttacker, "omise",
            PaymentIntentStatus.Succeeded, 0m));
        // ชั้นที่สอง: ต่อให้ส่งข้อเท็จจริงของรายการ X มา บริษัทไม่ตรงก็ปฏิเสธ
        Assert.NotNull(PaymentWebhookOwnership.RejectReason(XIntent(), Attacker, CfgAttacker, "omise",
            PaymentIntentStatus.Succeeded, 10_000m));
    }

    [Fact]
    public void บริษัทเดียวกัน_แต่ยอดสำเร็จไม่เท่ารายการ_ถูกปฏิเสธ()
        => Assert.Contains("ไม่เท่ายอด", PaymentWebhookOwnership.RejectReason(XIntent(), ShopX, CfgX, "omise",
            PaymentIntentStatus.Succeeded, 0m));

    [Fact]
    public void บริษัทเดียวกัน_คนละชุดตั้งค่า_หรือคนละช่องทาง_ถูกปฏิเสธ()
    {
        Assert.NotNull(PaymentWebhookOwnership.RejectReason(XIntent(), ShopX, Guid.NewGuid(), "omise",
            PaymentIntentStatus.Succeeded, 10_000m));
        Assert.NotNull(PaymentWebhookOwnership.RejectReason(XIntent(), ShopX, CfgX, "2c2p",
            PaymentIntentStatus.Succeeded, 10_000m));
    }

    // ── ทิศตรงข้าม: webhook จริงของร้านเองต้องผ่าน ──
    [Fact]
    public void ทิศตรงข้าม_webhook_จริงของร้าน_ยอดตรง_ผ่าน()
        => Assert.Null(PaymentWebhookOwnership.RejectReason(XIntent(), ShopX, CfgX, "omise",
            PaymentIntentStatus.Succeeded, 10_000m));

    [Fact]
    public void ทิศตรงข้าม_รายการเก่าที่ไม่ได้ผูกชุดตั้งค่า_บริษัทตรง_ผ่าน()
        => Assert.Null(PaymentWebhookOwnership.RejectReason(
            new PaymentWebhookOwnership.IntentFacts(ShopX, null, "OMISE", 10_000m), ShopX, CfgX, "omise",
            PaymentIntentStatus.Succeeded, 10_000.004m));

    [Fact]
    public void ทิศตรงข้าม_event_ไม่สำเร็จ_ไม่ตรวจยอด()
        => Assert.Null(PaymentWebhookOwnership.RejectReason(XIntent(), ShopX, CfgX, "omise",
            PaymentIntentStatus.Failed, 0m));
}
