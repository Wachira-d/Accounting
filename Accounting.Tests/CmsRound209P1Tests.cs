using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 209 · P1 (หน้าร้านฝั่งลูกค้า) — ตัวตัดสิน pure: หน้าสถานะออเดอร์ของแขก (A1/B3) · ผู้ขนส่ง/ลิงก์ติดตาม (B4) · กันสั่งซ้ำ/ตะกร้าว่าง (A9) ·
/// โทเคนลูกค้าหน้าร้าน + ด่านจองด้วยบัตร 3 ทิศ (E1/C3) · QR พร้อมเพย์ฝั่งเซิร์ฟเวอร์ (ฑ) · ทุกกลุ่มล็อกทั้ง "เคสใหม่" และ "เคสเดิมไม่ถูกแตะ"
/// </summary>
public class CmsRound209P1Tests
{
    private static readonly Guid SiteA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SiteB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid CustA = Guid.Parse("cccccccc-0000-0000-0000-00000000000a");
    private static readonly Guid CustB = Guid.Parse("cccccccc-0000-0000-0000-00000000000b");

    private static IConfiguration Config(string audience = "nextacc-test") => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "test-secret-that-is-long-enough-for-hmac-sha256-signing",
            ["Jwt:Issuer"] = "nextacc-test",
            ["Jwt:Audience"] = audience,
        }).Build();

    // ═════════ A1/B3: โทเคน + มุมมองจำกัดฟิลด์ ═════════

    [Fact]
    public void CMS209_P1_01_Public_token_is_random_url_safe_and_not_a_guid()
    {
        var a = SiteOrderPublic.NewToken();
        var b = SiteOrderPublic.NewToken();
        Assert.NotEqual(a, b);
        Assert.Equal(32, a.Length);
        Assert.True(SiteOrderPublic.IsWellFormedToken(a));
        Assert.False(Guid.TryParse(a, out _));
        // backfill (hex 64) ยังรับ · รูปผิด/สั้น/อักขระแปลก ⇒ ไม่ถามฐานข้อมูล
        Assert.True(SiteOrderPublic.IsWellFormedToken(new string('a', 64)));
        Assert.False(SiteOrderPublic.IsWellFormedToken(null));
        Assert.False(SiteOrderPublic.IsWellFormedToken("short"));
        Assert.False(SiteOrderPublic.IsWellFormedToken(Guid.NewGuid().ToString("D")));   // ลิงก์ order-success เดิม (GUID) ⇒ ไม่พบ ไม่ใช่ QR ฿0
        Assert.False(SiteOrderPublic.IsWellFormedToken("abc/def'or'1=1-----------"));
    }

    [Fact]
    public void CMS209_P1_02_Public_dto_has_no_pii_or_internal_fields()
    {
        var names = typeof(PublicOrderResponse).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var banned in new[] { "InternalNotes", "ShippingAddress", "ShippingPhone", "ShippingEmail", "ShippingName", "CustomerName",
                     "CustomerEmail", "CustomerId", "BillingTaxId", "BillingAddress", "CustomerNotes", "Payments", "ErpDocumentId", "CouponCode", "PublicToken" })
            Assert.DoesNotContain(banned, names);
    }

    [Fact]
    public void CMS209_P1_03_ToPublic_copies_server_computed_amounts_and_drops_private_fields()
    {
        var full = new OrderResponse
        {
            Id = Guid.NewGuid(), OrderNumber = "WEB-0001", Status = SiteOrderStatus.Shipped,
            TotalAmount = 752m, PaidAmount = 752m, ShippingAmount = 50m, NextPayableAmount = 0m,
            InternalNotes = "ต้นทุน 300", ShippingAddress = "บ้านเลขที่ 1", CustomerEmail = "a@b.c",
            ShippingCarrier = "ไปรษณีย์ไทย EMS", TrackingNumber = "EF123456789TH", FulfillmentMethod = "delivery",
            Lines = { new OrderLineResponse { ProductName = "เสื้อ M ดำ", Quantity = 1, UnitPrice = 390m, TotalAmount = 390m, ProductSku = "SKU-1" } },
            DepositDocuments = { new OrderDepositDocumentResponse { DocumentNumber = "DEP-01" } },
        };
        var pub = SiteOrderPublic.ToPublic(full, promptPayAvailable: true);
        Assert.Equal("WEB-0001", pub.OrderNumber);
        Assert.Equal(752m, pub.TotalAmount);
        Assert.Equal("จัดส่งแล้ว", pub.StatusLabel);
        Assert.Equal("https://track.thailandpost.co.th/?trackNumber=EF123456789TH", pub.TrackingUrl);
        Assert.False(pub.PromptPayQrAvailable);                       // จ่ายครบแล้ว ⇒ ไม่มี QR (ไม่แสดง QR ฿0)
        Assert.Single(pub.Lines);
        Assert.Equal(new[] { "DEP-01" }, pub.DepositDocumentNumbers);
        // ทิศตรงข้าม: ยอดค้าง + ร้านมีพร้อมเพย์ ⇒ มี QR · ร้านไม่มีพร้อมเพย์ ⇒ ไม่มี
        full.NextPayableAmount = 200m;
        Assert.True(SiteOrderPublic.ToPublic(full, true).PromptPayQrAvailable);
        Assert.False(SiteOrderPublic.ToPublic(full, false).PromptPayQrAvailable);
        // คำตอบเต็มของหลังบ้านไม่ถูกแตะ (หน้าพนักงานยังเห็นครบ)
        Assert.Equal("ต้นทุน 300", full.InternalNotes);
    }

    [Theory]
    [InlineData(SiteOrderStatus.Pending, "delivery", "รอร้านยืนยัน")]
    [InlineData(SiteOrderStatus.Shipped, "pickup", "พร้อมให้มารับ")]
    [InlineData(SiteOrderStatus.Delivered, "pickup", "รับสินค้าแล้ว")]
    [InlineData(SiteOrderStatus.Delivered, "delivery", "ส่งถึงแล้ว")]
    [InlineData(SiteOrderStatus.Cancelled, null, "ยกเลิกแล้ว")]
    public void CMS209_P1_04_Customer_status_labels(SiteOrderStatus s, string? fm, string expected)
        => Assert.Equal(expected, SiteOrderPublic.StatusLabel(s, fm));

    // ═════════ B4: ผู้ขนส่ง ═════════

    [Fact]
    public void CMS209_P1_05_Carrier_from_rate_and_staff_edit()
    {
        Assert.Equal("Kerry", SiteOrderPublic.CarrierAtOrder("delivery", "  Kerry "));
        Assert.Null(SiteOrderPublic.CarrierAtOrder("pickup", "Kerry"));      // มารับเอง ⇒ ไม่มีผู้ขนส่ง
        Assert.Null(SiteOrderPublic.CarrierAtOrder("delivery", "  "));
        Assert.Equal("Flash", SiteOrderPublic.CarrierEdit("Kerry", "Flash"));
        Assert.Equal("Kerry", SiteOrderPublic.CarrierEdit("Kerry", null));   // ไม่ส่ง = ไม่แก้ (ปุ่มเปลี่ยนสถานะเดิมไม่ล้างค่า)
        Assert.Null(SiteOrderPublic.CarrierEdit("Kerry", ""));               // "" = ล้าง
        Assert.Equal(100, SiteOrderPublic.CarrierEdit(null, new string('x', 150))!.Length);
    }

    [Fact]
    public void CMS209_P1_06_Tracking_url_only_for_known_carriers()
    {
        Assert.StartsWith("https://th.kex-express.com/", SiteOrderPublic.TrackingUrl("Kerry Express", "KEX1"));
        Assert.StartsWith("https://www.flashexpress.co.th/", SiteOrderPublic.TrackingUrl("Flash", "TH1"));
        Assert.Null(SiteOrderPublic.TrackingUrl("ส่งเองโดยร้าน", "123"));   // ไม่รู้จัก ⇒ ไม่แต่งลิงก์
        Assert.Null(SiteOrderPublic.TrackingUrl("Kerry", null));
        Assert.EndsWith("A%20B", SiteOrderPublic.TrackingUrl("EMS", "A B"));   // encode เลขพัสดุ
    }

    // ═════════ A9: กันสั่งซ้ำ + ตะกร้าว่าง ═════════

    [Fact]
    public void CMS209_P1_07_Idempotency_key_normalization()
    {
        foreach (var none in new string?[] { null, "   " })
        {
            var (k, problem) = SiteOrderPublic.NormalizeIdempotencyKey(none);   // ผู้เรียกเดิม (ไม่ส่ง) ⇒ ไม่กัน ไม่ล้ม
            Assert.Null(k);
            Assert.Null(problem);
        }
        var ok = SiteOrderPublic.NormalizeIdempotencyKey(" 0f8fad5bd9cb469fa16570867728950e ");
        Assert.Equal("0f8fad5bd9cb469fa16570867728950e", ok.Key);
        Assert.Null(ok.Problem);
        Assert.NotNull(SiteOrderPublic.NormalizeIdempotencyKey("1").Problem);             // สั้นเกิน ⇒ ชนข้ามลูกค้าได้
        Assert.NotNull(SiteOrderPublic.NormalizeIdempotencyKey(new string('a', 81)).Problem);
        Assert.NotNull(SiteOrderPublic.NormalizeIdempotencyKey("aaaaaaaaaaaaaaaa<script>").Problem);
        Assert.Equal("body-key", SiteOrderPublic.PickIdempotencyKey("body-key", "hdr"));
        Assert.Equal("hdr", SiteOrderPublic.PickIdempotencyKey(null, "hdr"));
        Assert.Null(SiteOrderPublic.PickIdempotencyKey(" ", null));
    }

    [Fact]
    public void CMS209_P1_08_Empty_order_rejected_non_empty_passes()
    {
        Assert.NotNull(SiteOrderPublic.EmptyOrderProblem(0));
        Assert.Null(SiteOrderPublic.EmptyOrderProblem(1));
    }

    // ═════════ E1/C3: โทเคนลูกค้าหน้าร้าน ═════════

    [Fact]
    public void CMS209_P1_09_Storefront_token_round_trip_and_rejections()
    {
        var cfg = Config();
        var token = JwtHelper.GenerateStorefrontCustomerToken(CustA, SiteA, "a@shop.test", "ลูกค้า A", cfg);
        var id = StorefrontCustomerAuth.Read(token, cfg);
        Assert.NotNull(id);
        Assert.Equal(CustA, id!.CustomerId);
        Assert.Equal(SiteA, id.SiteId);
        // ทิศตรงข้าม: โทเคนพนักงาน ERP (audience ต่าง) ⇒ ไม่ใช่ลูกค้าหน้าร้าน
        Assert.Null(StorefrontCustomerAuth.Read(JwtHelper.GenerateToken(CustA, "s@x", "staff", cfg), cfg));
        // ลายเซ็นจากกุญแจอื่น · ตัดหาง · ว่าง ⇒ null
        var other = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "another-secret-that-is-long-enough-for-hmac-sha256", ["Jwt:Issuer"] = "nextacc-test", ["Jwt:Audience"] = "nextacc-test",
        }).Build();
        Assert.Null(StorefrontCustomerAuth.Read(JwtHelper.GenerateStorefrontCustomerToken(CustA, SiteA, "a@x", "A", other), cfg));
        Assert.Null(StorefrontCustomerAuth.Read(token[..^4], cfg));
        Assert.Null(StorefrontCustomerAuth.Read(null, cfg));
        // โทเคนหน้าร้านของเว็บ A ใช้กับเว็บ B ไม่ได้
        Assert.NotNull(StorefrontCustomerAuth.ForSite(id, SiteA));
        Assert.Null(StorefrontCustomerAuth.ForSite(id, SiteB));
    }

    [Fact]
    public void CMS209_P1_10_Order_customer_comes_from_token_not_body()
    {
        var me = new StorefrontCustomerIdentity(CustA, SiteA);
        Assert.Equal(CustA, StorefrontCustomerAuth.OrderCustomerId(CustB, false, me, SiteA));     // body อ้างคนอื่น ⇒ ใช้ id ในโทเคน
        Assert.Null(StorefrontCustomerAuth.OrderCustomerId(CustB, false, null, SiteA));           // แขกนิรนามยัดออเดอร์เข้าบัญชีคนอื่นไม่ได้
        Assert.Null(StorefrontCustomerAuth.OrderCustomerId(CustB, false, me, SiteB));             // โทเคนเว็บอื่น
        Assert.Equal(CustB, StorefrontCustomerAuth.OrderCustomerId(CustB, true, null, SiteA));    // พนักงานสร้างแทนลูกค้า (ทางเดิม)
        Assert.Null(StorefrontCustomerAuth.OrderCustomerId(null, false, null, SiteA));            // แขกปกติ (ทางเดิม)
    }

    // ═════════ C3: ด่านจองด้วยบัตร 3 ทิศ ═════════

    [Fact]
    public void CMS209_P1_11_Credit_gate_staff_allowed_unchanged()
        => Assert.Null(CreditBookingLink.PayWithCreditGate(staffAuthorized: true, null, null, SiteA, CustB));

    [Fact]
    public void CMS209_P1_12_Credit_gate_own_storefront_customer_allowed()
        => Assert.Null(CreditBookingLink.PayWithCreditGate(false, CustA, SiteA, SiteA, CustA));

    [Fact]
    public void CMS209_P1_13_Credit_gate_rejects_anonymous_other_customer_and_other_site()
    {
        Assert.Equal(CreditBookingLink.AnonymousCreditProblem, CreditBookingLink.PayWithCreditGate(false, null, null, SiteA, CustA));
        Assert.NotNull(CreditBookingLink.PayWithCreditGate(false, CustA, SiteA, SiteA, CustB));   // ใช้บัตรของคนอื่น
        Assert.NotNull(CreditBookingLink.PayWithCreditGate(false, CustA, SiteA, SiteA, null));    // ไม่ระบุลูกค้า
        Assert.NotNull(CreditBookingLink.PayWithCreditGate(false, CustA, SiteB, SiteA, CustA));   // โทเคนเว็บอื่น
    }

    [Fact]
    public void CMS209_P1_14_Service_actor_check_defense_in_depth()
    {
        Assert.NotNull(CreditBookingLink.ActorPayWithCreditProblem(null, CustA));
        Assert.Null(CreditBookingLink.ActorPayWithCreditProblem("3f2b0c1e-0000-0000-0000-000000000001", CustB));   // พนักงาน (controller ตรวจสิทธิ์แล้ว)
        Assert.Null(CreditBookingLink.ActorPayWithCreditProblem(CreditBookingLink.StorefrontActor(CustA), CustA));
        Assert.NotNull(CreditBookingLink.ActorPayWithCreditProblem(CreditBookingLink.StorefrontActor(CustA), CustB));
        Assert.NotNull(CreditBookingLink.ActorPayWithCreditProblem(CreditBookingLink.StorefrontActor(CustA), null));
        Assert.False(CreditBookingLink.IsCustomerActor(CreditBookingLink.StorefrontActor(CustA)));   // ไม่ปนกับ "customer:{token}" ของการยกเลิกผ่านลิงก์
    }

    // ═════════ ฑ: QR พร้อมเพย์ฝั่งเซิร์ฟเวอร์ + บัตรของฉัน ═════════

    [Theory]
    [InlineData(null, null)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("10000001", null)]
    [InlineData("752.005", "752.01")]   // AwayFromZero (ไม่ใช่ banker's)
    [InlineData("200", "200")]
    public void CMS209_P1_15_Qr_amount(string? input, string? expected)
        => Assert.Equal(expected == null ? (decimal?)null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            SiteOrderPublic.QrAmount(input == null ? (decimal?)null : decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void CMS209_P1_16_Credit_usable_only_active_unexpired_with_balance()
    {
        Assert.True(SiteOrderPublic.CreditUsable(CreditPassStatus.Active, false, 2m));
        Assert.False(SiteOrderPublic.CreditUsable(CreditPassStatus.Active, true, 2m));
        Assert.False(SiteOrderPublic.CreditUsable(CreditPassStatus.Active, false, 0m));
        Assert.False(SiteOrderPublic.CreditUsable(CreditPassStatus.Exhausted, false, 2m));
        Assert.Equal("ชั่วโมง", SiteOrderPublic.CreditUnitLabel(CreditUnit.Hour));
        Assert.Equal("ครั้ง", SiteOrderPublic.CreditUnitLabel(CreditUnit.Session));
    }
}
