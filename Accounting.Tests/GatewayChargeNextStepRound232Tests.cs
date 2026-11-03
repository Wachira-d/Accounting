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
/// รอบ 232 — "ทดลองจ่ายพร้อมเพย์แล้วเด้งกลับหน้าเดิม": ผู้ให้บริการคืน charge ที่มี<b>ทั้ง</b> QR (flow offline) และ <c>authorize_uri</c> ·
/// adapter เก็บทั้งคู่ · หน้าจ่ายเลือก redirect ก่อน QR ⇒ ออกไปหน้า offsite แล้วถูกส่งกลับ return_uri ทันที — ผู้จ่ายไม่เห็น QR
/// ทุกกลุ่มมีสองทิศ: ใบที่พัง (QR + ลิงก์ ⇒ ต้องได้ QR อย่างเดียว) และใบที่ถูกอยู่แล้วต้องไม่ถูกแตะ (บัตร 3-D Secure / mobile banking มีแต่ลิงก์ ⇒ ลิงก์คงเดิม)
/// </summary>
public class GatewayChargeNextStepRound232Tests
{
    // ═══ ตัวตัดสิน pure ═══

    [Fact]
    public void Decide_มีทั้งQRและลิงก์_QRชนะ_ลิงก์ถูกทิ้ง()
    {
        var s = GatewayChargeNextStep.Decide("https://api.example/charges/chrg_1/documents/docu_1/downloads/ABC", "https://pay.example/offsites/ofsp_1/pay");
        Assert.Equal("https://api.example/charges/chrg_1/documents/docu_1/downloads/ABC", s.QrPayload);
        Assert.Null(s.AuthorizeUrl);
    }

    [Fact]
    public void Decide_ทิศตรงข้าม_มีแต่ลิงก์https_ลิงก์คงเดิม_และลิงก์ไม่ใช่https_ถูกทิ้ง()
    {
        var redirect = GatewayChargeNextStep.Decide(null, "https://pay.example/payments/paym_1/authorize");
        Assert.Null(redirect.QrPayload);
        Assert.Equal("https://pay.example/payments/paym_1/authorize", redirect.AuthorizeUrl);

        Assert.Null(GatewayChargeNextStep.Decide(null, "http://pay.example/x").AuthorizeUrl);
        Assert.Null(GatewayChargeNextStep.Decide(null, "javascript:alert(1)").AuthorizeUrl);
        Assert.Null(GatewayChargeNextStep.Decide("  ", null).QrPayload);
    }

    [Theory]
    [InlineData(PaymentMethodKind.PromptPay, "QR พร้อมเพย์")]
    [InlineData(PaymentMethodKind.MobileBanking, "ลิงก์หน้าชำระเงิน")]
    [InlineData(PaymentMethodKind.TrueMoney, "ลิงก์หน้าชำระเงิน")]
    public void MissingReason_รอแต่ไม่มีทั้งคู่_ได้เหตุผลไทยตามวิธี(PaymentMethodKind method, string expectContains)
    {
        var why = GatewayChargeNextStep.MissingReason(method, PaymentIntentStatus.Pending, null, null);
        Assert.NotNull(why);
        Assert.Contains(expectContains, why);
        Assert.DoesNotMatch("[A-Za-z]{6,}", why);   // ไม่มีข้อความอังกฤษหลุดถึงผู้จ่าย
    }

    [Fact]
    public void MissingReason_ทิศตรงข้าม_มีQR_หรือมีลิงก์_หรือสลิป_หรือไม่ใช่Pending_ไม่ตัดสิน()
    {
        Assert.Null(GatewayChargeNextStep.MissingReason(PaymentMethodKind.PromptPay, PaymentIntentStatus.Pending, "https://x/qr", null));
        Assert.Null(GatewayChargeNextStep.MissingReason(PaymentMethodKind.Card, PaymentIntentStatus.Pending, null, "https://x/3ds"));
        Assert.Null(GatewayChargeNextStep.MissingReason(PaymentMethodKind.ManualSlip, PaymentIntentStatus.Pending, null, null));
        // CI รอบ 232: บัตร (รวมบัตรที่บันทึกไว้) รอผลธนาคาร ไม่มีลิงก์ 3DS ⇒ ไม่ใช่ "ไม่มีทางจ่าย" — ต้องคง Pending ให้ webhook ตัดสิน
        Assert.Null(GatewayChargeNextStep.MissingReason(PaymentMethodKind.Card, PaymentIntentStatus.Pending, null, null));
        Assert.False(GatewayChargeNextStep.NeedsPayerStep(PaymentMethodKind.Card));
        Assert.True(GatewayChargeNextStep.NeedsPayerStep(PaymentMethodKind.PromptPay));
        // ฝ่ายค้าน 232 B1: บัตรไม่ 3-D Secure สำเร็จทันที (ไม่มี QR/ลิงก์) — ไม่ใช่ "ขาดขั้นถัดไป" (ไม่งั้นลูกค้าเห็น error แล้วกดจ่ายซ้ำ = ตัดบัตรสองครั้ง)
        Assert.Null(GatewayChargeNextStep.MissingReason(PaymentMethodKind.Card, PaymentIntentStatus.Succeeded, null, null));
        Assert.Null(GatewayChargeNextStep.MissingReason(PaymentMethodKind.PromptPay, PaymentIntentStatus.Succeeded, null, null));
        Assert.Null(GatewayChargeNextStep.MissingReason(PaymentMethodKind.PromptPay, PaymentIntentStatus.Expired, null, null));
        Assert.Null(GatewayChargeNextStep.MissingReason(PaymentMethodKind.Card, PaymentIntentStatus.Refunded, null, null));
        Assert.Null(GatewayChargeNextStep.MissingReason(PaymentMethodKind.PromptPay, PaymentIntentStatus.Failed, null, null));
        // ลิงก์ที่ไม่ใช่ https = ไม่มีลิงก์ (หน้าจ่ายไม่ตาม)
        Assert.NotNull(GatewayChargeNextStep.MissingReason(PaymentMethodKind.PromptPay, PaymentIntentStatus.Pending, null, "http://x/pay"));
    }

    [Fact]
    public void TransportFailureText_หมดเวลา_เครือข่าย_ผู้เรียกยกเลิก_เป็นไทย_ข้อผิดพลาดธุรกิจคงข้อความเดิม()
    {
        Assert.Contains("ไม่ตอบภายในเวลา", GatewayChargeNextStep.TransportFailureText(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 20 seconds elapsing."), callerCancelled: false));
        Assert.Contains("ยกเลิกคำขอ", GatewayChargeNextStep.TransportFailureText(new OperationCanceledException(), callerCancelled: true));
        Assert.Contains("เชื่อมต่อผู้ให้บริการรับชำระเงินไม่ได้", GatewayChargeNextStep.TransportFailureText(new HttpRequestException("No such host is known"), false));
        Assert.Equal("คีย์ไม่ถูกต้องหรือหมดอายุ", GatewayChargeNextStep.TransportFailureText(new InvalidOperationException("คีย์ไม่ถูกต้องหรือหมดอายุ"), false));
        Assert.Equal("เริ่มการชำระเงินไม่สำเร็จ", GatewayChargeNextStep.TransportFailureText(new InvalidOperationException(""), false));
    }

    // ═══ adapter — charge จริงตามรูปที่ผู้ให้บริการส่ง ═══

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly string _body;
        public FixedHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
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

    private static OmisePaymentProvider Provider(string body)
        => new(new FakeFactory(new FixedHandler(body)), new PlainSecrets(), NullLogger<OmisePaymentProvider>.Instance);

    private static PaymentProviderConfig Cfg() => new()
    {
        Id = Guid.NewGuid(), CompanyId = Guid.NewGuid(), ProviderCode = OmisePaymentProvider.Code, Mode = PaymentProviderMode.Test,
        TestSecretKeyProtected = "skey_test_aaaa", TestPublicKey = "pkey_test_aaaa",
    };

    private static PaymentIntent Intent(PaymentMethodKind kind) => new()
    {
        Id = Guid.NewGuid(), CompanyId = Guid.NewGuid(), Amount = 20m, Currency = "THB", MethodKind = kind, ProviderCode = OmisePaymentProvider.Code,
    };

    /// <summary>ใบที่พัง: พร้อมเพย์ (flow offline) มาพร้อม QR + authorize_uri ⇒ ต้องได้ QR เท่านั้น (ไม่พาออกจากหน้า)</summary>
    [Fact]
    public async Task สร้างcharge_พร้อมเพย์มีทั้งQRและauthorize_uri_ได้QRอย่างเดียว()
    {
        var p = Provider("""
            {"object":"charge","id":"chrg_test_pp","status":"pending","paid":false,"amount":2000,"livemode":false,
             "authorize_uri":"https://pay.omise.co/offsites/ofsp_test_1/pay","return_uri":"https://erp.example/platform-pay-test.html?payReturn=1",
             "expires_at":"2026-11-03T06:00:00Z",
             "source":{"object":"source","type":"promptpay","flow":"offline",
                       "scannable_code":{"type":"qr","image":{"download_uri":"https://api.omise.co/charges/chrg_test_pp/documents/docu_1/downloads/AB12"}}}}
            """);
        var c = await p.CreateChargeAsync(Intent(PaymentMethodKind.PromptPay),
            new ChargeRequest(PaymentMethodKind.PromptPay, null, "https://erp.example/platform-pay-test.html?payReturn=1", null, null, "ทดลองจ่าย"), Cfg());
        Assert.Equal(PaymentIntentStatus.Pending, c.Status);
        Assert.Equal("https://api.omise.co/charges/chrg_test_pp/documents/docu_1/downloads/AB12", c.QrPayload);
        Assert.Null(c.AuthorizeUrl);
        Assert.Equal(new DateTime(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc), c.QrExpiresAt);
        Assert.Null(GatewayChargeNextStep.MissingReason(PaymentMethodKind.PromptPay, c.Status, c.QrPayload, c.AuthorizeUrl));
    }

    /// <summary>ทิศตรงข้าม: บัตร 3-D Secure / mobile banking (flow redirect) ไม่มี QR ⇒ ลิงก์ต้องคงอยู่ (ไม่งั้นผู้จ่ายไม่มีทางไปต่อ)</summary>
    [Theory]
    [InlineData(PaymentMethodKind.Card, """{"object":"charge","id":"chrg_test_3ds","status":"pending","paid":false,"amount":2000,"livemode":false,"authorize_uri":"https://api.omise.co/payments/paym_test_1/authorize","card":{"object":"card","last_digits":"4242"}}""")]
    [InlineData(PaymentMethodKind.MobileBanking, """{"object":"charge","id":"chrg_test_mb","status":"pending","paid":false,"amount":2000,"livemode":false,"authorize_uri":"https://pay.omise.co/payments/paym_test_2/authorize","source":{"object":"source","type":"mobile_banking_kbank","flow":"redirect"}}""")]
    public async Task สร้างcharge_flow_redirect_ไม่มีQR_ลิงก์คงเดิม(PaymentMethodKind kind, string body)
    {
        var c = await Provider(body).CreateChargeAsync(Intent(kind),
            new ChargeRequest(kind, kind == PaymentMethodKind.Card ? "tokn_test_1" : null, "https://erp.example/return", null, null, "x"), Cfg());
        Assert.Null(c.QrPayload);
        Assert.StartsWith("https://", c.AuthorizeUrl);
        Assert.Null(GatewayChargeNextStep.MissingReason(kind, c.Status, c.QrPayload, c.AuthorizeUrl));
    }

    /// <summary>charge ที่รอแต่ไม่มีอะไรให้ผู้จ่ายทำ (เช่น บัญชีผู้ให้บริการยังไม่เปิดพร้อมเพย์แต่ไม่ปฏิเสธ) ⇒ ตัวตัดสินบอกให้ปิดเป็นล้มเหลวพร้อมเหตุผล</summary>
    [Fact]
    public async Task สร้างcharge_พร้อมเพย์ไม่มีทั้งQRและลิงก์_ต้องมีเหตุผลให้ปิดรายการ()
    {
        var c = await Provider("""{"object":"charge","id":"chrg_test_empty","status":"pending","paid":false,"amount":2000,"livemode":false,"authorize_uri":null,"source":{"object":"source","type":"promptpay","flow":"offline"}}""")
            .CreateChargeAsync(Intent(PaymentMethodKind.PromptPay), new ChargeRequest(PaymentMethodKind.PromptPay, null, null, null, null, "x"), Cfg());
        Assert.Equal(PaymentIntentStatus.Pending, c.Status);
        var why = GatewayChargeNextStep.MissingReason(PaymentMethodKind.PromptPay, c.Status, c.QrPayload, c.AuthorizeUrl);
        Assert.NotNull(why);
        Assert.Contains("QR", why);
    }

    // ═══ URL แจ้งเตือน (webhook) ที่ผู้ให้บริการเรียกถึงจริง ═══

    [Fact]
    public void PublicOrigin_ตั้งURLระบบแล้ว_ใช้originนั้น_ไม่ตั้ง_ใช้คำขอ()
    {
        Assert.Equal("https://erp.example.com", SystemBaseUrl.PublicOrigin("https://erp.example.com/", "http", "10.0.0.5:5000"));
        // ฝ่ายค้าน 232 B2: ค่าที่มากับซอร์ส (app.nextacc.com — โดเมนที่ไม่ใช่ของเรา) ห้ามเป็น origin ของ webhook — ตกไปใช้คำขอ
        Assert.Equal("https://erp.local", SystemBaseUrl.PublicOrigin(SystemBaseUrl.ShippedDefault, "https", "erp.local"));
        Assert.Equal("https://erp.local", SystemBaseUrl.PublicOrigin(SystemBaseUrl.ShippedDefault + "/", "https", "erp.local"));
        Assert.Equal("http://localhost:5000", SystemBaseUrl.PublicOrigin("https://APP.nextacc.com", "http", "localhost:5000"));
        Assert.Equal("https://erp.example.com", SystemBaseUrl.PublicOrigin("https://erp.example.com/some/path", "http", "localhost:5000"));
        Assert.Equal("http://localhost:5000", SystemBaseUrl.PublicOrigin(null, "http", "localhost:5000"));
        Assert.Equal("https://erp.local", SystemBaseUrl.PublicOrigin("not a url", "https", "erp.local"));
        Assert.Equal("https://erp.local", SystemBaseUrl.PublicOrigin("ftp://x", "https", "erp.local"));
    }
}
