using System.Net;
using System.Text;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 227 — ตรวจ Omise ฝั่งแอดมินแพลตฟอร์ม (erp-review/2026-11-02/PLATFORM_OMISE_AUDIT.md) · ทุกกลุ่มมีสองทิศ:
/// เคสที่เคยพัง (ข้อความอังกฤษถึงผู้จ่าย · QR ไม่รู้วันหมดอายุ · คีย์ผิดโหมดหลังสลับ · บัตร/วอลเล็ทโผล่ให้ผู้เช่าทั้งที่ทำไม่จบ ·
/// เปิด live ได้ทั้งที่ URL ระบบเป็นค่าที่มากับซอร์ส · เงินออนไลน์ลงธนาคาร) และเคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ
/// </summary>
public class PlatformOmiseRound227Tests
{
    // ═══ A-10: ข้อความไทยของเหตุล้มเหลว ═══

    [Fact]
    public void A10_รหัสที่รู้จัก_เป็นไทย_ไม่รู้จัก_คงข้อความต้นทางพร้อมรหัส()
    {
        Assert.Equal("ยอดเงินในบัญชี/วงเงินบัตรไม่พอ", OmiseFailureText.Describe("insufficient_fund", "insufficient funds in the account"));
        Assert.Contains("CVV", OmiseFailureText.Describe("invalid_security_code", null));
        // ไม่รู้จัก ⇒ ไม่เดาสาเหตุแทน (ข้อความต้นทาง + รหัส)
        Assert.Equal("something new (รหัส brand_new_code)", OmiseFailureText.Describe("brand_new_code", "something new"));
        Assert.Equal("ผู้ให้บริการปฏิเสธรายการ (รหัส x_y)", OmiseFailureText.Describe("x_y", null));
        // ไม่มีอะไรเลย = null (ไม่ใช่ข้อความแต่ง) — charge ที่สำเร็จไม่มีข้อความล้มเหลว
        Assert.Null(OmiseFailureText.Describe(null, null));
        Assert.Equal("plain", OmiseFailureText.Describe(null, "plain"));
        Assert.True(OmiseFailureText.IsKnown("payment_rejected"));
        Assert.False(OmiseFailureText.IsKnown(""));
    }

    // ═══ adapter ผ่าน HttpMessageHandler ปลอม ═══

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly string _body;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public FixedHandler(string body) => _body = body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public FakeFactory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    private sealed class PlainSecrets : ISecretProtector
    {
        public string? Protect(string? plaintext) => plaintext;
        public string? Unprotect(string? value) => value;
        public bool IsUsable(string? value) => !string.IsNullOrEmpty(value);
    }

    private static OmisePaymentProvider Provider(FixedHandler h)
        => new(new FakeFactory(h), new PlainSecrets(), NullLogger<OmisePaymentProvider>.Instance);

    private static PaymentProviderConfig Cfg(PaymentProviderMode mode = PaymentProviderMode.Test) => new()
    {
        Id = Guid.NewGuid(), CompanyId = Guid.NewGuid(), ProviderCode = OmisePaymentProvider.Code, Mode = mode,
        TestSecretKeyProtected = "skey_test_aaaa", LiveSecretKeyProtected = "skey_live_bbbb",
        TestPublicKey = "pkey_test_aaaa", LivePublicKey = "pkey_live_bbbb",
    };

    private static string BasicUser(HttpRequestMessage r)
        => Encoding.UTF8.GetString(Convert.FromBase64String(r.Headers.Authorization!.Parameter!)).TrimEnd(':');

    [Fact]
    public async Task A10_A7_charge_ล้มเหลว_ได้ข้อความไทย_และวันหมดอายุQRจากระดับcharge()
    {
        var h = new FixedHandler("""
            {"object":"charge","id":"chrg_test_1","status":"failed","paid":false,"amount":2000,"livemode":false,
             "failure_code":"insufficient_fund","failure_message":"insufficient funds in the account",
             "expires_at":"2026-11-03T05:00:00Z",
             "source":{"type":"promptpay","scannable_code":{"image":{"download_uri":"https://x/qr.png"}}}}
            """);
        var intent = new PaymentIntent { Id = Guid.NewGuid(), ProviderRef = "chrg_test_1", Amount = 20m, ProviderCode = OmisePaymentProvider.Code };
        var c = await Provider(h).GetChargeAsync(intent, Cfg());
        Assert.Equal(PaymentIntentStatus.Failed, c.Status);
        Assert.Equal("insufficient_fund", c.FailureCode);
        Assert.Equal("ยอดเงินในบัญชี/วงเงินบัตรไม่พอ", c.FailureMessage);
        Assert.Equal(new DateTime(2026, 11, 3, 5, 0, 0, DateTimeKind.Utc), c.QrExpiresAt);
        Assert.Equal("https://x/qr.png", c.QrPayload);
    }

    [Fact]
    public async Task A7_ทิศตรงข้าม_บัตรไม่มีsource_ไม่ประทับวันหมดอายุ_และcharge_สำเร็จไม่มีข้อความล้มเหลว()
    {
        var h = new FixedHandler("""
            {"object":"charge","id":"chrg_test_2","status":"successful","paid":true,"amount":2000,"fee":73,"expires_at":"2026-11-10T00:00:00Z"}
            """);
        var c = await Provider(h).GetChargeAsync(new PaymentIntent { ProviderRef = "chrg_test_2", Amount = 20m, ProviderCode = "omise" }, Cfg());
        Assert.Equal(PaymentIntentStatus.Succeeded, c.Status);
        Assert.Null(c.QrExpiresAt);           // บัตร (ไม่มี source) — expires_at ของ charge ไม่ใช่อายุ QR
        Assert.Null(c.FailureMessage);
        Assert.Equal(0.73m, c.Fee);
    }

    // ═══ A-12: คีย์ของโหมดที่รายการถูกสร้าง ═══

    [Fact]
    public void A12_โหมดของรายการชนะโหมดปัจจุบันของช่องทาง_แถวเดิมใช้โหมดช่องทาง()
    {
        Assert.Equal(PaymentProviderMode.Live, PaymentConfigForIntent.ModeFor(false, PaymentProviderMode.Test));
        Assert.Equal(PaymentProviderMode.Test, PaymentConfigForIntent.ModeFor(true, PaymentProviderMode.Live));
        Assert.Equal(PaymentProviderMode.Live, PaymentConfigForIntent.ModeFor(null, PaymentProviderMode.Live));   // แถวก่อนมีคอลัมน์ = เดิม
        var cfg = Cfg(PaymentProviderMode.Live);
        Assert.Same(cfg, PaymentConfigForIntent.For(cfg, false));   // ตรงอยู่แล้ว ⇒ ตัวเดิม (ไม่คัดลอก)
        var test = PaymentConfigForIntent.For(cfg, true);
        Assert.NotSame(cfg, test);
        Assert.Equal(PaymentProviderMode.Test, test.Mode);
        Assert.Equal(PaymentProviderMode.Live, cfg.Mode);           // ไม่แตะ entity จริง
        Assert.Equal(cfg.Id, test.Id);
    }

    [Fact]
    public async Task A12_ถอยกลับโหมดทดสอบแล้ว_รายการเงินจริงยังถามด้วยคีย์จริง()
    {
        var h = new FixedHandler("""{"object":"charge","id":"chrg_1","status":"pending","paid":false,"amount":99000,"livemode":true}""");
        var cfgNowTest = Cfg(PaymentProviderMode.Test);   // แอดมินเพิ่งกด "กลับสู่โหมดทดสอบ"
        var liveIntent = new PaymentIntent { ProviderRef = "chrg_1", Amount = 990m, ProviderCode = "omise", IsTestMode = false };
        await Provider(h).GetChargeAsync(liveIntent, PaymentConfigForIntent.For(cfgNowTest, liveIntent.IsTestMode));
        Assert.Equal("skey_live_bbbb", BasicUser(h.Requests.Single()));
        // ทิศตรงข้าม: รายการทดสอบยังใช้คีย์ทดสอบ
        var h2 = new FixedHandler("""{"object":"charge","id":"chrg_2","status":"pending","amount":2000,"livemode":false}""");
        await Provider(h2).GetChargeAsync(new PaymentIntent { ProviderRef = "chrg_2", Amount = 20m, ProviderCode = "omise", IsTestMode = true },
            PaymentConfigForIntent.For(cfgNowTest, true));
        Assert.Equal("skey_test_aaaa", BasicUser(h2.Requests.Single()));
    }

    [Fact]
    public void A12_webhook_ลองอีกโหมดเฉพาะเมื่ออีกโหมดมีคีย์ลับ()
    {
        var cfg = Cfg(PaymentProviderMode.Live);
        Assert.Equal(PaymentProviderMode.Test, PaymentConfigForIntent.OtherMode(cfg)!.Mode);
        cfg.TestSecretKeyProtected = null;
        Assert.Null(PaymentConfigForIntent.OtherMode(cfg));   // ไม่มีคีย์ ⇒ ไม่มีคำขอออก
    }

    // ═══ A-4: วอลเล็ทส่งเบอร์ผู้จ่าย ═══

    [Fact]
    public async Task A4_TrueMoney_ส่งเบอร์ผู้จ่าย_PromptPay_ไม่ส่ง()
    {
        var h = new FixedHandler("""{"object":"charge","id":"chrg_t","status":"pending","amount":10000}""");
        var p = Provider(h);
        var intent = new PaymentIntent { Id = Guid.NewGuid(), CompanyId = Guid.NewGuid(), Amount = 100m, ProviderCode = "omise" };
        await p.CreateChargeAsync(intent, new ChargeRequest(PaymentMethodKind.TrueMoney, null, null, null, "081-234 5678", "x"), Cfg());   // N6: ตัวเลขล้วน
        Assert.Contains("source%5Bphone_number%5D=0812345678", h.Bodies[0]);
        await p.CreateChargeAsync(intent, new ChargeRequest(PaymentMethodKind.PromptPay, null, null, null, "0812345678", "x"), Cfg());
        Assert.DoesNotContain("phone_number", h.Bodies[1]);
        Assert.Contains("amount=10000", h.Bodies[1]);   // สตางค์
    }

    // ═══ A-3/B-2: วิธีที่หน้าชำระของ NextAcc ทำจบได้ ═══

    private static readonly PaymentMethodKind[] OmiseMethods =
    {
        PaymentMethodKind.PromptPay, PaymentMethodKind.Card, PaymentMethodKind.MobileBanking, PaymentMethodKind.InternetBanking, PaymentMethodKind.TrueMoney,
    };

    [Fact]
    public void B2_ไม่ติ๊ก_ผู้เช่าเห็นแค่วิธีที่ทำจบได้_ติ๊กแต่บัตร_ไม่มีวิธีให้จ่าย()
    {
        Assert.Equal(new[] { "PromptPay" }, PlatformPayMethods.Effective(OmiseMethods, Array.Empty<string>()));
        Assert.Empty(PlatformPayMethods.Effective(OmiseMethods, new[] { "Card" }));   // เดิม: ศูนย์บิลเลือก "Card" แล้วล้มทุกครั้ง
        Assert.Equal(new[] { "PromptPay" }, PlatformPayMethods.Effective(OmiseMethods, new[] { "card", "promptpay" }));
        Assert.True(PlatformPayMethods.IsPayable(PaymentMethodKind.PromptPay));
        Assert.NotNull(PlatformPayMethods.NotPayableReason(PaymentMethodKind.Card));
        Assert.NotNull(PlatformPayMethods.NotPayableReason(PaymentMethodKind.TrueMoney));
        Assert.Equal("พร้อมเพย์ (QR)", PlatformPayMethods.Label(PaymentMethodKind.PromptPay));
    }

    [Fact]
    public void B3_ชื่อวิธีที่ไม่รู้จัก_ถูกปฏิเสธตอนบันทึก_ชื่อถูก_ผ่าน()
    {
        Assert.Equal(new[] { "Bitcoin" }, PlatformPayMethods.Unknown(OmiseMethods, new[] { "PromptPay", "Bitcoin" }));
        Assert.Equal(new[] { "ManualSlip" }, PlatformPayMethods.Unknown(OmiseMethods, new[] { "ManualSlip" }));
        Assert.Empty(PlatformPayMethods.Unknown(OmiseMethods, new[] { "promptpay", "Card" }));
    }

    // ═══ B-4: ด่านเปิดรับเงินจริงของแพลตฟอร์ม ═══

    private static PaymentLiveGate.Facts Ready() => new(DateTime.UtcNow, DateTime.UtcNow, true, true);

    [Fact]
    public void B4_URLระบบยังเป็นค่าที่มากับซอร์ส_หรือไม่มีวิธีที่ใช้ได้_เปิดliveไม่ได้()
    {
        var r = PaymentLiveGate.PlatformRefusal(Ready(), true, PaymentLiveGate.ConfirmPhrase, systemBaseUrlSet: false);
        Assert.Contains(PaymentLiveGate.SystemBaseUrlMissing, r);
        Assert.Contains(PaymentLiveGate.NoPayableMethod, PaymentLiveGate.PlatformRefusal(Ready(), true, PaymentLiveGate.ConfirmPhrase, payableMethodCount: 0));
        // ทิศตรงข้าม: ครบ ⇒ ผ่าน · ผู้เรียกเดิม (ไม่ส่งพารามิเตอร์ใหม่) ผลไม่เปลี่ยน
        Assert.Null(PaymentLiveGate.PlatformRefusal(Ready(), true, PaymentLiveGate.ConfirmPhrase, systemBaseUrlSet: true, payableMethodCount: 1));
        Assert.Null(PaymentLiveGate.PlatformRefusal(Ready(), true, PaymentLiveGate.ConfirmPhrase));
    }

    [Fact]
    public void B4_URLระบบ_ค่าที่มากับซอร์สไม่นับว่าตั้งแล้ว()
    {
        Assert.False(SystemBaseUrl.IsConfigured(null, "https://app.nextacc.com"));
        Assert.False(SystemBaseUrl.IsConfigured("", "https://app.nextacc.com/"));
        Assert.True(SystemBaseUrl.IsConfigured("https://www.nextacc.net", "https://app.nextacc.com"));
        Assert.True(SystemBaseUrl.IsConfigured(null, "https://erp.example.co.th"));
        Assert.False(SystemBaseUrl.IsConfigured(null, null));
    }

    // ═══ C: ความพร้อมเชื่อมโยงบัญชี ═══

    private static PlatformGatewayReadinessFacts Facts(string? baseUrl = "https://www.nextacc.net", bool clearing = true, int payable = 1,
        bool vat = true, bool taxOk = true, bool inclVat = true)
        => new(baseUrl, clearing, true, "41000", true, "11120", true, vat, taxOk, true, payable, inclVat);

    [Fact]
    public void C1_รายการตรวจ_ข้อที่ด่านใช้_กับข้อเตือน()
    {
        var all = PlatformGatewayReadiness.Checks(Facts());
        Assert.All(all, i => Assert.True(i.Ok));
        Assert.Empty(PlatformGatewayReadiness.BlockingMissing(all));

        var bad = PlatformGatewayReadiness.Checks(Facts(baseUrl: null, clearing: false, payable: 0, taxOk: false, inclVat: false));
        var blocking = PlatformGatewayReadiness.BlockingMissing(bad);
        Assert.Equal(4, blocking.Count);   // URL ระบบ + วิธีชำระ + บัญชีพัก (S4) + ราคาไม่รวม VAT (S2)
        Assert.Contains(PaymentLiveGate.SystemBaseUrlMissing, blocking);
        Assert.Contains(bad, i => i.Key == "clearingAccount" && !i.Ok && i.Blocking && i.Text.Contains("11340"));
        Assert.Contains(bad, i => i.Key == "priceIncludesVat" && !i.Ok && i.Blocking);
        // ด่านเปิด live ใช้ข้อเหล่านี้ (ไม่ซ้ำข้อความ)
        var refusal = PaymentLiveGate.PlatformRefusal(Ready(), true, PaymentLiveGate.ConfirmPhrase, systemBaseUrlSet: false, readinessMissing: blocking)!;
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(refusal, System.Text.RegularExpressions.Regex.Escape(PaymentLiveGate.SystemBaseUrlMissing)).Count);
        Assert.Contains("11340", refusal);
        // บัญชีเงินสดหาย = เตือนเท่านั้น (ทิศตรงข้าม: ไม่บล็อก)
        Assert.DoesNotContain(PlatformGatewayReadiness.Checks(Facts()), i => !i.Ok);
        Assert.Contains(bad, i => i.Key == "sellerTaxId" && !i.Ok);
        // ไม่จด VAT ⇒ ไม่มีข้อเลขผู้เสียภาษี
        Assert.DoesNotContain(PlatformGatewayReadiness.Checks(Facts(vat: false)), i => i.Key == "sellerTaxId");
    }

    [Fact]
    public void C2_ขาเงินเข้า_gatewayชนะ_ไม่มีผังพักตกไปธนาคาร()
    {
        var clearing = Guid.NewGuid();
        var cash = Guid.NewGuid();
        Assert.Equal(clearing, PlatformMoneyInAccount.Pick(clearing, cash));
        Assert.Equal(cash, PlatformMoneyInAccount.Pick(null, cash));   // สลิป/ไม่มีผังพัก = เดิม
        Assert.Null(PlatformMoneyInAccount.Pick(null, null));
        Assert.Equal((925.23m, 64.77m), PlatformMoneyInAccount.SplitReceived(990m, true));
        Assert.Equal((990m, 0m), PlatformMoneyInAccount.SplitReceived(990m, false));
    }

    [Fact]
    public void C3_เลขอ้างอิงใบเสร็จส่วนเสริม_ออนไลน์ต่อintent_สลิปต่อไฟล์()
    {
        var row = Guid.NewGuid();
        var intent = Guid.NewGuid();
        Assert.Equal($"ADDON-{intent:N}", AddOnPurchaseService.ReceiptReference(row, intent, null));
        var at = new DateTime(2026, 11, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.Equal(AddOnPurchaseService.ReceiptReference(row, null, at, "2026-11"), AddOnPurchaseService.ReceiptReference(row, null, at, "2026-11"));   // อนุมัติซ้ำ = ใบเดิม
        Assert.NotEqual(AddOnPurchaseService.ReceiptReference(row, null, at), AddOnPurchaseService.ReceiptReference(row, null, at.AddDays(30)));   // สลิปงวดใหม่ = ใบใหม่
        // ฝ่ายค้าน S5: ไม่มีเวลาแนบสลิป (แอดมินอนุมัติตรง) งวดต่างกัน ⇒ คนละใบ
        Assert.NotEqual(AddOnPurchaseService.ReceiptReference(row, null, null, "2026-11"), AddOnPurchaseService.ReceiptReference(row, null, null, "2026-12"));
    }

    // ═══ ฝ่ายค้านรอบ 227 ═══

    [Theory]
    [InlineData("0.99", "0.93", "0.06")]
    [InlineData("1.76", "1.64", "0.12")]
    [InlineData("300", "280.37", "19.63")]
    public void S2_ใบเสร็จเท่าเงินที่รับพอดี_ไม่มีเศษ(string received, string expBase, string expVat)
    {
        var a = decimal.Parse(received, System.Globalization.CultureInfo.InvariantCulture);
        var (b, v) = PlatformMoneyInAccount.SplitReceived(a, true);
        Assert.Equal(decimal.Parse(expBase, System.Globalization.CultureInfo.InvariantCulture), b);
        Assert.Equal(decimal.Parse(expVat, System.Globalization.CultureInfo.InvariantCulture), v);
        Assert.Equal(a, b + v);
    }

    [Fact]
    public void S2_สูตรเดิม_ฐานคูณเจ็ดเปอร์เซ็นต์_เหลือเศษ_ยืนยันว่าเป็นบั๊กจริง()
    {
        // เดิม: เอกสารคิด VAT = ฐาน × 7% ⇒ 0.93+0.07 = 1.00 (รับ 0.99) · 1.64+0.11 = 1.75 (รับ 1.76) — จึงต้องบังคับ VAT = ยอด − ฐาน
        static decimal Old(decimal a) { var b = PlatformMoneyInAccount.SplitReceived(a, true).Base; return b + decimal.Round(b * 0.07m, 2, MidpointRounding.AwayFromZero); }
        Assert.Equal(1.00m, Old(0.99m));
        Assert.Equal(1.75m, Old(1.76m));
    }

    private static readonly DateTime T0 = new(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void B1_หมดอายุ_มีเลขผู้ให้บริการต้องถามก่อน_ถามไม่ได้ปิดเองหลังระยะผ่อนเท่านั้น()
    {
        var exp = T0.AddMinutes(-1);
        Assert.False(GatewayExpiryPolicy.ExpireWithoutAsking(exp, hasProviderRef: true, T0));   // มี charge ⇒ ถามก่อน
        Assert.True(GatewayExpiryPolicy.ExpireWithoutAsking(exp, hasProviderRef: false, T0));   // ไม่เคยมี charge ⇒ เดิม
        Assert.False(GatewayExpiryPolicy.ExpireWithoutAsking(T0.AddMinutes(5), false, T0));
        Assert.False(GatewayExpiryPolicy.ExpireWithoutAsking(null, false, T0));
        Assert.False(GatewayExpiryPolicy.ExpireAfterProviderUnreachable(exp, T0));              // ยังในระยะผ่อน
        Assert.True(GatewayExpiryPolicy.ExpireAfterProviderUnreachable(T0 - GatewayExpiryPolicy.LocalExpiryGrace, T0));
        Assert.False(GatewayExpiryPolicy.ExpireAfterProviderUnreachable(null, T0));
    }

    [Fact]
    public void B1_หน้าต่างถือ_รอเงิน_ค่าตั้งต้นเดิมเป็นเพดาน()
    {
        var w = TimeSpan.FromMinutes(30);
        Assert.Equal(T0 + w, GatewayExpiryPolicy.WindowEnd(T0.AddHours(24), T0, w));       // พร้อมเพย์ 24 ชม. ไม่ยืด
        Assert.Equal(T0.AddMinutes(10), GatewayExpiryPolicy.WindowEnd(T0.AddMinutes(10), T0, w));
        Assert.Equal(T0 + w, GatewayExpiryPolicy.WindowEnd(null, T0, w));                  // ไม่รู้ = เดิม
        // การจองนัดหมาย: QR 24 ชม. ไม่ทำให้ "กำลังจ่าย" ค้างเกิน 30 + 10 นาทีเดิม
        Assert.False(BookingPaymentPolicy.GatewayPaymentInProgress(true, T0.AddHours(24), T0.AddMinutes(-41), T0));
        Assert.True(BookingPaymentPolicy.GatewayPaymentInProgress(true, T0.AddHours(24), T0.AddMinutes(-39), T0));
    }

    [Fact]
    public void S3_เงินจริงเข้าแล้วไม่มีใบ_ติดธง_ทดสอบ_มีใบ_ยังไม่สำเร็จ_ไม่ติด()
    {
        Assert.NotNull(PlatformReceiptGap.Reason(PaymentSourceKind.AddOnPurchase, PaymentIntentStatus.Succeeded, false, false));
        Assert.NotNull(PlatformReceiptGap.Reason(PaymentSourceKind.SubscriptionPayment, PaymentIntentStatus.PartiallyRefunded, false, false));
        Assert.Null(PlatformReceiptGap.Reason(PaymentSourceKind.AddOnPurchase, PaymentIntentStatus.Succeeded, true, false));
        Assert.Null(PlatformReceiptGap.Reason(PaymentSourceKind.AddOnPurchase, PaymentIntentStatus.Succeeded, false, true));
        Assert.Null(PlatformReceiptGap.Reason(PaymentSourceKind.SubscriptionPayment, PaymentIntentStatus.Pending, false, false));
        Assert.Null(PlatformReceiptGap.Reason(PaymentSourceKind.GatewaySelfTest, PaymentIntentStatus.Succeeded, false, false));
    }
}
