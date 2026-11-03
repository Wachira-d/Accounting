using System.Net;
using System.Text;
using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 230 (erp-review/2026-11-02/PLATFORM_OMISE_AUDIT.md §5.2/§5.3 · §7.3) — บัตรที่บันทึกไว้ · ตัดบัตรอัตโนมัติ · รอบโอนของผู้ให้บริการ → ร่างรอบโอน ·
/// ทุกกลุ่มสองทิศ: เคสที่ต้องถูกกัน (ไม่ยินยอม/ฉบับเก่า · ก่อนครบกำหนด · ตัดซ้อน · แพ็กเกจยกเลิก · ยอดไม่พอดี = ไม่เดา) และเคสที่ต้องผ่าน
/// </summary>
public class PlatformCardBillingRound230Tests
{
    private static readonly DateTime Now = new(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc);   // 10:00 เวลาไทย

    // ═══ ความยินยอม + บัตร ═══

    [Fact]
    public void Consent_ไม่ติ๊ก_ฉบับเก่า_ปฏิเสธ_ฉบับตรง_ผ่าน()
    {
        var v = PlatformSavedCardRules.Version("2.1");
        Assert.Equal("autocharge-1/2.1", v);
        Assert.Equal(PlatformSavedCardRules.MissingMessage, PlatformSavedCardRules.ConsentProblem(false, v, v));
        Assert.Equal(PlatformSavedCardRules.StaleMessage, PlatformSavedCardRules.ConsentProblem(true, "autocharge-1/2.0", v));
        Assert.Null(PlatformSavedCardRules.ConsentProblem(true, v, v));
        // ฝ่ายค้าน S2: ไม่ส่งฉบับที่แสดง ⇒ ปฏิเสธ (หลักฐานต้องผูกข้อความที่แสดงจริง)
        Assert.Equal(PlatformSavedCardRules.VersionMissingMessage, PlatformSavedCardRules.ConsentProblem(true, null, v));
        Assert.Equal(PlatformSavedCardRules.VersionMissingMessage, PlatformSavedCardRules.ConsentProblem(true, " ", v));
        // ข้อความอ้างฉบับนโยบายตรง ๆ + บอกว่าไม่เก็บเลขบัตร + ปิดได้
        var text = PlatformSavedCardRules.Text("2.1");
        Assert.Contains("ฉบับ 2.1", text);
        Assert.Contains("ไม่เก็บเลขบัตร", text);
        Assert.Contains("ยกเลิกการตัดอัตโนมัติได้ทุกเมื่อ", text);
    }

    [Fact]
    public void Card_สี่ตัวท้ายรับเฉพาะตัวเลขสี่หลัก_ไม่เก็บข้อความยาวที่อาจเป็นเลขบัตร()
    {
        Assert.Equal("4242", PlatformSavedCardRules.Last4("4242"));
        Assert.Null(PlatformSavedCardRules.Last4("4242424242424242"));
        Assert.Null(PlatformSavedCardRules.Last4("42a2"));
        Assert.Null(PlatformSavedCardRules.Last4(null));
        Assert.Equal("Visa •••• 4242 (หมดอายุ 12/2030)", PlatformSavedCardRules.Label("Visa", "4242", PlatformSavedCardRules.ValidUntil(12, 2030)));
    }

    [Fact]
    public void Card_หมดอายุเทียบวันไทย_ไม่ทราบวันหมดอายุ_ไม่ถือว่าหมด()
    {
        var oct = PlatformSavedCardRules.ValidUntil(10, 2026);
        Assert.Equal(new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc), oct);
        Assert.True(PlatformSavedCardRules.Expired(oct, Now));
        Assert.False(PlatformSavedCardRules.Expired(PlatformSavedCardRules.ValidUntil(11, 2026), Now));
        Assert.Null(PlatformSavedCardRules.ValidUntil(13, 2026));
        Assert.False(PlatformSavedCardRules.Expired(null, Now));
    }

    [Fact]
    public void UseRefusal_บัตรของบริษัทอื่น_คนละโหมด_ไม่ใช่วิธีบัตร_ปฏิเสธ()
    {
        Assert.NotNull(PlatformSavedCardRules.UseRefusal(false, true, true, true));
        Assert.NotNull(PlatformSavedCardRules.UseRefusal(true, false, true, true));
        Assert.NotNull(PlatformSavedCardRules.UseRefusal(true, true, false, true));
        Assert.NotNull(PlatformSavedCardRules.UseRefusal(true, true, true, false));
        Assert.Null(PlatformSavedCardRules.UseRefusal(true, true, true, true));
    }

    [Fact]
    public void AutoChargeBlock_สวิตช์ปิด_แอดมินระงับ_นโยบายเปลี่ยน_บัตรยังไม่ยืนยัน_ทุกข้อหยุด_ครบ_ตัดได้()
    {
        const string v = "autocharge-1/2.1";
        Assert.Contains("ยังไม่เปิด", PlatformSavedCardRules.AutoChargeBlockReason(false, true, false, v, v, null));
        Assert.Contains("ระงับ", PlatformSavedCardRules.AutoChargeBlockReason(true, true, true, v, v, null));
        Assert.Contains("ยังไม่ได้เปิด", PlatformSavedCardRules.AutoChargeBlockReason(true, false, false, v, v, null));
        Assert.Contains("ฉบับใหม่", PlatformSavedCardRules.AutoChargeBlockReason(true, true, false, "autocharge-1/2.0", v, null));
        var unverified = PlatformSavedCardRules.CardUnusableReason(false, false, null, true, Now);
        Assert.Contains("ยังไม่ผ่านการชำระครั้งแรก", PlatformSavedCardRules.AutoChargeBlockReason(true, true, false, v, v, unverified));
        Assert.Null(PlatformSavedCardRules.AutoChargeBlockReason(true, true, false, v, v, null));
        Assert.Null(PlatformSavedCardRules.CardUnusableReason(false, true, PlatformSavedCardRules.ValidUntil(1, 2030), true, Now));
    }

    // ═══ ตัวตัดสินตัดบัตรอัตโนมัติ ═══

    private static readonly IReadOnlyList<int> Retry = PlatformAutoChargePolicy.RetryDays(new[] { 3, 1, 7, 1, 0 });
    private static DateTime DueDaysAgo(int d) => PlatformInvoiceRules.ThaiDate(Now).AddDays(-d).AddHours(-7);   // เที่ยงคืนไทยของวันครบกำหนด (เก็บเป็น UTC)

    [Fact]
    public void Policy_วันลองใหม่จากนโยบายทวงหนี้_เรียง_ไม่ซ้ำ_ตัดศูนย์()
    {
        Assert.Equal(new[] { 1, 3, 7 }, Retry);
        Assert.Equal(4, PlatformAutoChargePolicy.MaxAttempts(Retry));
    }

    [Fact]
    public void Policy_ก่อนครบกำหนด_ไม่ตัด_วันครบกำหนด_ตัดครั้งแรก()
    {
        Assert.Null(PlatformAutoChargePolicy.Decide(DueDaysAgo(-2), Now, 990m, false, Array.Empty<AutoChargeAttemptFact>(), Retry).AttemptNo);
        Assert.Equal(1, PlatformAutoChargePolicy.Decide(DueDaysAgo(0), Now, 990m, false, Array.Empty<AutoChargeAttemptFact>(), Retry).AttemptNo);
        // ไม่มียอดค้าง/ใบที่ทวงหนี้ไม่นับ/ไม่มีวันครบกำหนด ⇒ ไม่ตัด
        Assert.Null(PlatformAutoChargePolicy.Decide(DueDaysAgo(0), Now, 0m, false, Array.Empty<AutoChargeAttemptFact>(), Retry).AttemptNo);
        Assert.Null(PlatformAutoChargePolicy.Decide(DueDaysAgo(0), Now, 990m, true, Array.Empty<AutoChargeAttemptFact>(), Retry).AttemptNo);
        Assert.Null(PlatformAutoChargePolicy.Decide(null, Now, 990m, false, Array.Empty<AutoChargeAttemptFact>(), Retry).AttemptNo);
    }

    [Fact]
    public void Policy_ล้มแล้วลองใหม่ตามวันเกินกำหนด_วันละครั้ง_ครบจำนวนหยุด()
    {
        var failedYesterday = new[] { new AutoChargeAttemptFact(1, Now.AddDays(-1), PlatformAutoChargePolicy.OutcomeFailed) };
        Assert.Equal(2, PlatformAutoChargePolicy.Decide(DueDaysAgo(1), Now, 990m, false, failedYesterday, Retry).AttemptNo);
        // ครั้งที่ 2 ต้องรอเกินกำหนด 1 วัน — วันครบกำหนดเองยังไม่ลองซ้ำ
        var failedToday = new[] { new AutoChargeAttemptFact(1, Now.AddHours(-2), PlatformAutoChargePolicy.OutcomeFailed) };
        Assert.Null(PlatformAutoChargePolicy.Decide(DueDaysAgo(0), Now, 990m, false, failedToday, Retry).AttemptNo);
        // งานหยุดไปหลายวันแล้วกลับมา ⇒ ครั้งถัดไปครั้งเดียว (ไม่ไล่ทุกครั้งที่พลาด)
        Assert.Equal(2, PlatformAutoChargePolicy.Decide(DueDaysAgo(10), Now, 990m, false, failedYesterday, Retry).AttemptNo);
        // ครบ 4 ครั้งแล้ว ⇒ หยุด (เหลือทวงหนี้/ระงับตามเดิม)
        var four = Enumerable.Range(1, 4).Select(i => new AutoChargeAttemptFact(i, Now.AddDays(-5 + i - 1), PlatformAutoChargePolicy.OutcomeFailed)).ToList();
        Assert.Null(PlatformAutoChargePolicy.Decide(DueDaysAgo(10), Now, 990m, false, four, Retry).AttemptNo);
    }

    [Fact]
    public void Policy_ตัดที่ยังรอผล_หรือสำเร็จแล้ว_ห้ามตัดซ้อน()
    {
        foreach (var o in new[] { PlatformAutoChargePolicy.OutcomeClaimed, PlatformAutoChargePolicy.OutcomePending, PlatformAutoChargePolicy.OutcomeRequiresAction,
                     PlatformAutoChargePolicy.OutcomeSucceeded })
            Assert.Null(PlatformAutoChargePolicy.Decide(DueDaysAgo(5), Now, 990m, false,
                new[] { new AutoChargeAttemptFact(1, Now.AddDays(-4), o) }, Retry).AttemptNo);
    }

    [Fact]
    public void Policy_สถานะแพ็กเกจ_ยกเลิก_หมดอายุ_ระงับโดยแอดมิน_ไม่ตัด_ระงับเพราะค้างชำระ_ตัดได้()
    {
        Assert.False(PlatformAutoChargePolicy.SubscriptionChargeable(SubscriptionStatus.Cancelled, false));
        Assert.False(PlatformAutoChargePolicy.SubscriptionChargeable(SubscriptionStatus.Expired, false));
        Assert.False(PlatformAutoChargePolicy.SubscriptionChargeable(SubscriptionStatus.Suspended, false));
        Assert.True(PlatformAutoChargePolicy.SubscriptionChargeable(SubscriptionStatus.Suspended, true));
        Assert.True(PlatformAutoChargePolicy.SubscriptionChargeable(SubscriptionStatus.Active, false));
        Assert.True(PlatformAutoChargePolicy.SubscriptionChargeable(SubscriptionStatus.PastDue, false));
    }

    [Fact]
    public void Policy_ผลจากสถานะรายการชำระ()
    {
        Assert.Equal(PlatformAutoChargePolicy.OutcomeSucceeded, PlatformAutoChargePolicy.OutcomeOf(PaymentIntentStatus.Succeeded, false));
        Assert.Equal(PlatformAutoChargePolicy.OutcomeFailed, PlatformAutoChargePolicy.OutcomeOf(PaymentIntentStatus.Expired, false));
        Assert.Equal(PlatformAutoChargePolicy.OutcomeRequiresAction, PlatformAutoChargePolicy.OutcomeOf(PaymentIntentStatus.Pending, true));
        Assert.Equal(PlatformAutoChargePolicy.OutcomePending, PlatformAutoChargePolicy.OutcomeOf(PaymentIntentStatus.Pending, false));
        Assert.NotEqual(PlatformAutoChargePolicy.NoticeKey(Guid.Empty, 1, "Failed"), PlatformAutoChargePolicy.NoticeKey(Guid.Empty, 2, "Failed"));
    }

    // ═══ จับคู่รอบโอน ═══

    private static TransferMatchCandidate C(int minutesAgo, decimal net, string? reff = null)
        => new(Guid.NewGuid(), reff, Now.AddMinutes(-minutesAgo), net);

    [Fact]
    public void Match_ผู้ให้บริการระบุcharge_ใช้รายการที่ตรง_ขาดบางรายการติดเรื่อง()
    {
        var a = C(300, 96.15m, "chrg_a"); var b = C(200, 48.07m, "chrg_b"); var other = C(100, 10m, "chrg_x");
        var ok = PlatformTransferMatch.Match(new[] { "chrg_a", "chrg_b" }, 144.22m, Now, new[] { a, b, other });
        Assert.Equal(PlatformTransferMatch.MethodChargeList, ok.Method);
        Assert.Equal(new[] { a.IntentId, b.IntentId }, ok.IntentIds);
        Assert.Null(ok.Problem);
        var partial = PlatformTransferMatch.Match(new[] { "chrg_a", "chrg_zz" }, 144.22m, Now, new[] { a, b });
        Assert.Single(partial.IntentIds);
        Assert.Contains("ไม่พบ 1 รายการ", partial.Problem);
        Assert.Empty(PlatformTransferMatch.Match(new[] { "chrg_zz" }, 1m, Now, new[] { a }).IntentIds);
    }

    [Fact]
    public void Match_ไม่ระบุcharge_ชุดเก่าสุดที่รวมพอดี_ไม่พอดี_ไม่เดา()
    {
        var a = C(300, 96.15m); var b = C(200, 48.07m); var c = C(100, 30m); var after = C(-60, 5m);
        var ok = PlatformTransferMatch.Match(null, 144.22m, Now, new[] { c, b, a, after });
        Assert.Equal(PlatformTransferMatch.MethodAmountWindow, ok.Method);
        Assert.Equal(new[] { a.IntentId, b.IntentId }, ok.IntentIds);
        Assert.Contains("ตรวจรายการก่อนลงบัญชี", ok.Problem);
        // ยอดที่ต้องใช้รายการหลังรอบโอนถูกสร้าง/ข้ามรายการกลาง ⇒ ไม่ร่าง
        var none = PlatformTransferMatch.Match(null, 126.15m, Now, new[] { a, b, c, after });
        Assert.Equal(PlatformTransferMatch.MethodNone, none.Method);
        Assert.Empty(none.IntentIds);
        Assert.Empty(PlatformTransferMatch.Match(null, 179.22m, Now, new[] { a, b, c, after }).IntentIds);
    }

    // ═══ adapter ผ่าน HttpMessageHandler ปลอม (ลำดับคำตอบ) ═══

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> _replies;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public QueueHandler(params (HttpStatusCode, string)[] replies) => _replies = new(replies);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
            var (code, body) = _replies.Count > 0 ? _replies.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
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

    private static OmisePaymentProvider Provider(HttpMessageHandler h)
        => new(new FakeFactory(h), new PlainSecrets(), NullLogger<OmisePaymentProvider>.Instance);

    private static PaymentProviderConfig Cfg() => new()
    {
        Id = Guid.NewGuid(), CompanyId = Guid.NewGuid(), ProviderCode = OmisePaymentProvider.Code, Mode = PaymentProviderMode.Live,
        LiveSecretKeyProtected = "skey_live_bbbb", LivePublicKey = "pkey_live_bbbb",
    };

    private const string SuccessCharge = """{"object":"charge","id":"chrg_1","status":"successful","paid":true,"amount":99000,"fee":3000,"livemode":true}""";

    [Fact]
    public async Task Charge_บัตรที่บันทึก_ส่งcustomerและcard_ไม่ส่งโทเคน_พร้อมหัวกันซ้ำ()
    {
        var h = new QueueHandler((HttpStatusCode.OK, SuccessCharge));
        var intent = new PaymentIntent { Id = Guid.NewGuid(), CompanyId = Guid.NewGuid(), Amount = 990m, ProviderCode = OmisePaymentProvider.Code };
        var c = await Provider(h).CreateChargeAsync(intent, new ChargeRequest(PaymentMethodKind.Card, null, null, null, null, "ค่าแพ็กเกจ",
            CustomerRef: "cust_abc123", SavedCardRef: "card_xyz789", IdempotencyKey: "SP:1:990.00:1"), Cfg());
        Assert.Equal(PaymentIntentStatus.Succeeded, c.Status);
        var body = h.Bodies.Single();
        Assert.Contains("customer=cust_abc123", body);
        Assert.Contains("card=card_xyz789", body);
        Assert.DoesNotContain("tokn_", body);
        Assert.True(h.Requests.Single().Headers.TryGetValues(OmisePaymentProvider.IdempotencyHeader, out var key));
        Assert.Equal("SP:1:990.00:1", key!.Single());
    }

    [Fact]
    public async Task Charge_ทิศตรงข้าม_โทเคนตามเดิม_ไม่มีหัวกันซ้ำ_และเลขอ้างอิงรูปผิดไม่ยิงออก()
    {
        var h = new QueueHandler((HttpStatusCode.OK, SuccessCharge));
        var intent = new PaymentIntent { Id = Guid.NewGuid(), CompanyId = Guid.NewGuid(), Amount = 990m, ProviderCode = OmisePaymentProvider.Code };
        await Provider(h).CreateChargeAsync(intent, new ChargeRequest(PaymentMethodKind.Card, "tokn_test_1", null, null, null, "x"), Cfg());
        Assert.Contains("card=tokn_test_1", h.Bodies.Single());
        Assert.DoesNotContain("customer=", h.Bodies.Single());
        Assert.False(h.Requests.Single().Headers.Contains(OmisePaymentProvider.IdempotencyHeader));

        var h2 = new QueueHandler();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(h2).CreateChargeAsync(intent,
            new ChargeRequest(PaymentMethodKind.Card, null, null, null, null, "x", CustomerRef: "../charges"), Cfg()));
        Assert.Empty(h2.Requests);
    }

    [Fact]
    public async Task SaveCard_อ่านบัตรในโทเคน_สร้างลูกค้า_ได้เลขอ้างอิงไม่ใช่เลขบัตร()
    {
        var h = new QueueHandler(
            (HttpStatusCode.OK, """{"object":"token","id":"tokn_1","card":{"id":"card_new","fingerprint":"fp1","brand":"Visa","last_digits":"4242"}}"""),
            (HttpStatusCode.OK, """{"object":"customer","id":"cust_9","livemode":true,"cards":{"data":[
                {"id":"card_old","fingerprint":"fp0","brand":"MasterCard","last_digits":"5555","expiration_month":1,"expiration_year":2027},
                {"id":"card_new","fingerprint":"fp1","brand":"Visa","last_digits":"4242","expiration_month":12,"expiration_year":2030,"livemode":true}]}}"""));
        var s = await Provider(h).SaveCardAsync(Cfg(), "tokn_1", null, "NextAcc tenant x", "a@b.co");
        Assert.Equal("cust_9", s.CustomerRef);
        Assert.Equal("card_new", s.CardRef);
        Assert.Equal("4242", s.Last4);
        Assert.Equal(12, s.ValidMonth);
        Assert.Equal(2030, s.ValidYear);
        Assert.True(s.LiveMode);
        Assert.Equal(HttpMethod.Get, h.Requests[0].Method);
        Assert.EndsWith("/tokens/tokn_1", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, h.Requests[1].Method);
        Assert.EndsWith("/customers", h.Requests[1].RequestUri!.AbsolutePath);
        Assert.Contains("card=tokn_1", h.Bodies[1]);
    }

    [Fact]
    public void SaveCard_เลขบัตรไม่ตรง_ใช้ลายนิ้วมือ_ไม่ตรงทั้งคู่_ไม่เดา()
    {
        using var doc = JsonDocument.Parse("""{"id":"cust_9","cards":{"data":[{"id":"card_a","fingerprint":"fp1","created_at":"2026-11-01T00:00:00Z"},
            {"id":"card_b","fingerprint":"fp1","created_at":"2026-11-03T00:00:00Z"},{"id":"card_c","fingerprint":"fp2"}]}}""");
        Assert.Equal("card_b", OmisePaymentProvider.ParseSavedCustomerCard(doc.RootElement, "card_zzz", "fp1")!.CardRef);
        Assert.Null(OmisePaymentProvider.ParseSavedCustomerCard(doc.RootElement, "card_zzz", "fp9"));
        Assert.Null(OmisePaymentProvider.ParseSavedCustomerCard(doc.RootElement, null, null));
    }

    [Fact]
    public async Task RemoveCard_ไม่พบที่ผู้ให้บริการ_ถือว่าลบแล้ว_ปฏิเสธ_คืนข้อความ()
    {
        var gone = await Provider(new QueueHandler((HttpStatusCode.NotFound, """{"code":"not_found"}""")))
            .RemoveSavedCardAsync(Cfg(), "cust_9", "card_b");
        Assert.True(gone.Removed);
        var refused = await Provider(new QueueHandler((HttpStatusCode.BadRequest, """{"code":"bad_request","message":"nope"}""")))
            .RemoveSavedCardAsync(Cfg(), "cust_9", "card_b");
        Assert.False(refused.Removed);
        Assert.NotNull(refused.Problem);
        var h = new QueueHandler();
        Assert.False((await Provider(h).RemoveSavedCardAsync(Cfg(), "cust_9/../x", "card_b")).Removed);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void Transfer_ไม่มีnet_ค่าธรรมเนียมโอนหักเพิ่ม_มีnet_ใช้net()
    {
        using var noNet = JsonDocument.Parse("""{"object":"transfer","id":"trsf_1","amount":100000,"fee":3000,"fee_vat":210,"paid":true,"livemode":true,
            "created_at":"2026-11-02T01:00:00Z","paid_at":"2026-11-03T02:00:00Z","transactions":[{"id":"trxn_1","origin":"chrg_a"},"chrg_b","trxn_2"]}""");
        var a = OmisePaymentProvider.ParseTransfer(noNet.RootElement)!;
        Assert.Equal(1032.10m, a.WalletDebit);
        Assert.Equal(1000m, a.NetToBank);
        Assert.Equal(30m, a.TransferFee);
        Assert.Equal(2.10m, a.TransferFeeVat);
        Assert.True(a.Paid);
        Assert.Equal(new[] { "chrg_a", "chrg_b" }, a.ChargeRefs);
        Assert.NotNull(a.ShapeNote);

        using var withNet = JsonDocument.Parse("""{"id":"trsf_2","amount":100000,"fee":3000,"fee_vat":210,"net":96790,"paid":false,"sent":true}""");
        var b = OmisePaymentProvider.ParseTransfer(withNet.RootElement)!;
        Assert.Equal(1000m, b.WalletDebit);
        Assert.Equal(967.90m, b.NetToBank);
        Assert.Null(b.ChargeRefs);   // ไม่ระบุ = null (ห้ามเดา) ⇒ ผู้เรียกจับคู่ด้วยยอด
        Assert.Equal("sent", b.RawStatus);
        Assert.Null(OmisePaymentProvider.ParseTransfer(JsonDocument.Parse("""{"id":"../x","amount":1}""").RootElement));
    }

    // ═══ ฝ่ายค้านรอบ 230 ═══

    [Fact]
    public void S1_นิติบุคคลต้องรับทราบภาษีหักณที่จ่าย_บุคคลธรรมดาไม่ต้อง_หลักฐานต่อท้ายฉบับ()
    {
        Assert.Equal(PlatformSavedCardRules.WhtAckMissingMessage, PlatformSavedCardRules.WhtAckProblem(true, false));
        Assert.Null(PlatformSavedCardRules.WhtAckProblem(true, true));
        Assert.Null(PlatformSavedCardRules.WhtAckProblem(false, false));
        Assert.Equal("autocharge-1/2.1+wht-ack", PlatformSavedCardRules.EvidenceVersion("autocharge-1/2.1", true));
        Assert.Equal("autocharge-1/2.1", PlatformSavedCardRules.EvidenceVersion("autocharge-1/2.1", false));
        const string v = "autocharge-1/2.1";
        // นิติบุคคลที่ยังไม่รับทราบ ⇒ ไม่ตัด (ค่าตั้งต้นปิด) · รับทราบแล้ว ⇒ ตัดได้ · ฉบับในคำสั่งมีเครื่องหมายต่อท้าย = ฉบับเดียวกัน
        Assert.Equal(PlatformSavedCardRules.WhtAckMissingMessage, PlatformSavedCardRules.AutoChargeBlockReason(true, true, false, v, v, null, true, false));
        Assert.Null(PlatformSavedCardRules.AutoChargeBlockReason(true, true, false, v + PlatformSavedCardRules.WhtAckVersionSuffix, v, null, true, true));
    }

    [Fact]
    public void B2_รายการเป็นหลักฐานของบัตรเฉพาะที่สร้างในคำขอนี้_ตัดด้วยบัตรใบนี้_ผู้จ่ายเดียวกัน()
    {
        var payer = Guid.NewGuid();
        Assert.True(PlatformSavedCardRules.IntentProvesCard(Now, Now.AddSeconds(-2), "card_a", "card_a", payer, payer));
        Assert.False(PlatformSavedCardRules.IntentProvesCard(Now.AddMinutes(-10), Now.AddSeconds(-2), "card_a", "card_a", payer, payer));   // ใบเปิดเดิมที่ถูกใช้ซ้ำ
        Assert.False(PlatformSavedCardRules.IntentProvesCard(Now, Now.AddSeconds(-2), "card_b", "card_a", payer, payer));
        Assert.False(PlatformSavedCardRules.IntentProvesCard(Now, Now.AddSeconds(-2), null, "card_a", payer, payer));
        Assert.False(PlatformSavedCardRules.IntentProvesCard(Now, Now.AddSeconds(-2), "card_a", "card_a", Guid.NewGuid(), payer));
    }

    [Fact]
    public void B2_ใช้รายการบัตรที่เปิดอยู่ซ้ำ_โทเคนใหม่ห้าม_บัตรคนละใบห้าม_ใบเดียวกันได้_พร้อมเพย์เดิม()
    {
        Assert.False(SavedCardChargeOutcome.ReuseOpenIntent(PaymentMethodKind.Card, true, null, null));
        Assert.False(SavedCardChargeOutcome.ReuseOpenIntent(PaymentMethodKind.Card, false, "card_b", "card_a"));
        Assert.False(SavedCardChargeOutcome.ReuseOpenIntent(PaymentMethodKind.Card, false, null, null));
        Assert.True(SavedCardChargeOutcome.ReuseOpenIntent(PaymentMethodKind.Card, false, "card_a", "card_a"));
        Assert.True(SavedCardChargeOutcome.ReuseOpenIntent(PaymentMethodKind.PromptPay, false, null, null));
    }

    [Fact]
    public void B1_ผลไม่แน่ชัด_5xx_เครือข่าย_หมดเวลา_ไม่ใช่ผู้เรียกยกเลิก_ทิศตรงข้าม_ปฏิเสธชัดเจน_ผู้เรียกยกเลิก()
    {
        Assert.True(SavedCardChargeOutcome.IsUnknown(new ProviderOutcomeUnknownException("503"), false));
        Assert.True(SavedCardChargeOutcome.IsUnknown(new HttpRequestException("reset"), false));
        Assert.True(SavedCardChargeOutcome.IsUnknown(new TaskCanceledException("timeout"), false));
        Assert.False(SavedCardChargeOutcome.IsUnknown(new TaskCanceledException("caller"), true));
        Assert.False(SavedCardChargeOutcome.IsUnknown(new InvalidOperationException("ผู้ให้บริการปฏิเสธ"), false));
        Assert.False(SavedCardChargeOutcome.IsUnknown(new ProviderActionRequiredException("3ds"), false));
        Assert.False(SavedCardChargeOutcome.GiveUp(Now.AddMinutes(-30), Now));
        Assert.True(SavedCardChargeOutcome.GiveUp(Now.AddHours(-3), Now));
    }

    [Fact]
    public async Task B1_adapter_5xx_ผลไม่แน่ชัด_400ขอreturn_uriตอนตัดบัตรที่บันทึก_ต้องยืนยันตัวตน_400อื่น_ปฏิเสธ()
    {
        var intent = new PaymentIntent { Id = Guid.NewGuid(), CompanyId = Guid.NewGuid(), Amount = 990m, ProviderCode = OmisePaymentProvider.Code };
        var saved = new ChargeRequest(PaymentMethodKind.Card, null, null, null, null, "x", CustomerRef: "cust_abc123", SavedCardRef: "card_xyz789");
        await Assert.ThrowsAsync<ProviderOutcomeUnknownException>(() => Provider(new QueueHandler((HttpStatusCode.BadGateway, "{}")))
            .CreateChargeAsync(intent, saved, Cfg()));
        await Assert.ThrowsAsync<ProviderActionRequiredException>(() => Provider(new QueueHandler((HttpStatusCode.BadRequest,
            """{"code":"invalid_card","message":"return_uri is required for this charge"}"""))).CreateChargeAsync(intent, saved, Cfg()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(new QueueHandler((HttpStatusCode.BadRequest,
            """{"code":"invalid_card","message":"card declined"}"""))).CreateChargeAsync(intent, saved, Cfg()));
        // โทเคน (มีผู้ใช้อยู่) ขอ return_uri ⇒ ปฏิเสธตามเดิม (ไม่ใช่เส้นตัดอัตโนมัติ)
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(new QueueHandler((HttpStatusCode.BadRequest,
            """{"code":"invalid_card","message":"return_uri is required"}"""))).CreateChargeAsync(intent,
            new ChargeRequest(PaymentMethodKind.Card, "tokn_1", null, null, null, "x"), Cfg()));
    }

    [Fact]
    public void B1_หาchargeจากmetadata_พบเฉพาะรายการตรง()
    {
        var id = Guid.NewGuid();
        using var doc = JsonDocument.Parse($$"""{"object":"list","data":[{"id":"chrg_other","status":"successful","paid":true,"amount":100,"metadata":{"intentId":"{{Guid.NewGuid()}}"}},
            {"id":"chrg_mine","status":"successful","paid":true,"amount":99000,"metadata":{"intentId":"{{id}}"}}]}""");
        var hit = OmisePaymentProvider.FindChargeInList(doc.RootElement, id);
        Assert.Equal("chrg_mine", hit!.ProviderRef);
        Assert.Equal(PaymentIntentStatus.Succeeded, hit.Status);
        Assert.Null(OmisePaymentProvider.FindChargeInList(doc.RootElement, Guid.NewGuid()));
    }

    [Fact]
    public void S7_ระงับโดยทวงหนี้ต้องมีทั้งธงและประวัติล่าสุดของทวงหนี้_แอดมินระงับทับไม่ตัด()
    {
        Assert.True(PlatformAutoChargePolicy.SuspendedByDunning(true, PlatformAutoChargePolicy.DunningSuspendAction));
        Assert.False(PlatformAutoChargePolicy.SuspendedByDunning(true, "AdminSuspended"));
        Assert.False(PlatformAutoChargePolicy.SuspendedByDunning(true, null));
        Assert.False(PlatformAutoChargePolicy.SuspendedByDunning(false, PlatformAutoChargePolicy.DunningSuspendAction));
    }

    [Fact]
    public void Nice_ต้องยืนยันตัวตน_จบและหยุดลองใหม่_ล้มธรรมดายังลองใหม่()
    {
        Assert.Equal(PlatformAutoChargePolicy.OutcomeActionRequired,
            PlatformAutoChargePolicy.OutcomeOf(PaymentIntentStatus.Failed, false, SavedCardChargeOutcome.ActionRequiredCode));
        Assert.Equal(PlatformAutoChargePolicy.OutcomeFailed, PlatformAutoChargePolicy.OutcomeOf(PaymentIntentStatus.Failed, false, "card_declined"));
        Assert.False(PlatformAutoChargePolicy.InFlight(PlatformAutoChargePolicy.OutcomeActionRequired));
        Assert.Null(PlatformAutoChargePolicy.Decide(DueDaysAgo(5), Now, 990m, false,
            new[] { new AutoChargeAttemptFact(1, Now.AddDays(-4), PlatformAutoChargePolicy.OutcomeActionRequired) }, Retry).AttemptNo);
        Assert.Equal(2, PlatformAutoChargePolicy.Decide(DueDaysAgo(5), Now, 990m, false,
            new[] { new AutoChargeAttemptFact(1, Now.AddDays(-4), PlatformAutoChargePolicy.OutcomeFailed) }, Retry).AttemptNo);
    }

    [Fact]
    public void Nice_net_ไม่ตรงผลรวม_คงบรรทัดVATค่าธรรมเนียม()
    {
        using var d = JsonDocument.Parse("""{"id":"trsf_9","amount":100000,"fee":3000,"fee_vat":210,"net":96500,"paid":true}""");
        var t = OmisePaymentProvider.ParseTransfer(d.RootElement)!;
        Assert.Equal(965m, t.NetToBank);
        Assert.Equal(2.10m, t.TransferFeeVat);
        Assert.Equal(32.90m, t.TransferFee);   // 35.00 − 2.10
    }
}
