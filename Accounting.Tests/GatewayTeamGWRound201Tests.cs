using System.Net;
using System.Text;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Payments.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม GW — Gateway/Integration (BACKLOG §1.1 A-GW1..A-GW12 · C-10 · C-11 · B-1)
///
/// <para>ทุกกลุ่มมีสองทิศ: เคสที่เคยพัง (webhook นิรนามยิงคำขอออกด้วยคีย์ทุกร้าน · batch ที่ลงบัญชีแล้วโชว์ "ยังไม่โอน" · open redirect ·
/// resync กลับ JE เดิมก่อนรู้ว่าสร้างใหม่ได้ · เกณฑ์ค้าง 30 นาทีเป็นสำเนาใน JS …) และเคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ</para>
/// </summary>
public class GatewayTeamGWRound201Tests
{
    // ═══ A-GW1: webhook token ต่อ config — token ผิด = ไม่ยิงคำขอออก (fake handler) · URL เดิมยังทำงาน ═══

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"object\":\"refund\"}}", Encoding.UTF8, "application/json"),
            });
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

    private static PaymentProviderConfig Cfg(string token, DateTime? tokenSeen = null, PaymentProviderMode? seenMode = null,
        PaymentProviderMode mode = PaymentProviderMode.Test)
        => new()
        {
            Id = Guid.NewGuid(), CompanyId = Guid.NewGuid(), ProviderCode = OmisePaymentProvider.Code, Mode = mode,
            TestSecretKeyProtected = "k_test_" + token[..4], WebhookToken = token,
            LastTokenWebhookAt = tokenSeen, LastTokenWebhookMode = seenMode,
        };

    /// <summary>เส้นเดียวกับ controller: ตัวตัดสินเลือก config → ยืนยันกับ adapter เฉพาะชุดที่เลือก</summary>
    private static async Task<int> SimulateWebhookAsync(IReadOnlyList<PaymentProviderConfig> configs, string? token, CapturingHandler h)
    {
        var p = new OmisePaymentProvider(new FakeFactory(h), new PlainSecrets(), NullLogger<OmisePaymentProvider>.Instance);
        var toTry = GatewayWebhookRoute.ConfigsToTry(
            configs.Select(c => new GatewayWebhookConfigFacts(c.Id, c.WebhookToken, c.LastTokenWebhookAt, c.LastTokenWebhookMode, c.Mode)),
            token);
        foreach (var c in configs.Where(c => toTry.Contains(c.Id)))
            await p.VerifyWebhookAsync("{\"id\":\"evnt_test_abc\"}", new Dictionary<string, string>(), c);
        return toTry.Count;
    }

    [Fact]
    public async Task GW1_tokenผิด_ไม่ลองconfigใดและไม่ยิงคำขอออก()
    {
        var a = Cfg(GatewayWebhookRoute.NewToken());
        var b = Cfg(GatewayWebhookRoute.NewToken());
        var h = new CapturingHandler();
        var tried = await SimulateWebhookAsync(new[] { a, b }, GatewayWebhookRoute.NewToken(), h);
        Assert.Equal(0, tried);
        Assert.Empty(h.Requests);
        // รูปผิด (สั้น/อักขระแปลก) ก็ไม่ลอง
        Assert.Empty(GatewayWebhookRoute.ConfigsToTry(new[] { new GatewayWebhookConfigFacts(a.Id, a.WebhookToken, null) }, "../x"));
        Assert.Empty(GatewayWebhookRoute.ConfigsToTry(new[] { new GatewayWebhookConfigFacts(a.Id, a.WebhookToken, null) }, ""));
    }

    [Fact]
    public async Task GW1_tokenถูก_ลองเฉพาะconfigนั้นหนึ่งคำขอ()
    {
        var a = Cfg(GatewayWebhookRoute.NewToken());
        var b = Cfg(GatewayWebhookRoute.NewToken());
        var h = new CapturingHandler();
        var tried = await SimulateWebhookAsync(new[] { a, b }, b.WebhookToken, h);
        Assert.Equal(1, tried);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task GW1_ทิศตรงข้าม_URLเดิมยังลองทุกconfigที่ยังไม่ย้าย()
    {
        var a = Cfg(GatewayWebhookRoute.NewToken());
        var b = Cfg(GatewayWebhookRoute.NewToken());
        var h = new CapturingHandler();
        var tried = await SimulateWebhookAsync(new[] { a, b }, null, h);
        Assert.Equal(2, tried);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public void GW1_URLเดิมเลิกลองconfigที่ย้ายแล้วในโหมดปัจจุบันเท่านั้น()
    {
        var moved = Cfg(GatewayWebhookRoute.NewToken(), DateTime.UtcNow, PaymentProviderMode.Test, PaymentProviderMode.Test);
        // ย้ายในโหมดทดสอบแล้วสลับเป็นใช้จริง — แดชบอร์ดใช้จริงยังเป็น URL เดิม ⇒ ต้องยังรับได้
        var switched = Cfg(GatewayWebhookRoute.NewToken(), DateTime.UtcNow, PaymentProviderMode.Test, PaymentProviderMode.Live);
        var facts = new[] { moved, switched }.Select(c =>
            new GatewayWebhookConfigFacts(c.Id, c.WebhookToken, c.LastTokenWebhookAt, c.LastTokenWebhookMode, c.Mode)).ToList();
        var legacy = GatewayWebhookRoute.ConfigsToTry(facts, null);
        Assert.DoesNotContain(moved.Id, legacy);
        Assert.Contains(switched.Id, legacy);
        // เส้น token ยังลองได้เสมอ
        Assert.Equal(new[] { moved.Id }, GatewayWebhookRoute.ConfigsToTry(facts, moved.WebhookToken));
    }

    [Fact]
    public void GW1_tokenใหม่_รูปถูกและไม่ซ้ำ_เทียบเวลาคงที่()
    {
        var t1 = GatewayWebhookRoute.NewToken();
        var t2 = GatewayWebhookRoute.NewToken();
        Assert.True(GatewayWebhookRoute.IsWellFormedToken(t1));
        Assert.NotEqual(t1, t2);
        Assert.True(GatewayWebhookRoute.TokenMatches(t1, t1));
        Assert.False(GatewayWebhookRoute.TokenMatches(t1, t2));
        Assert.False(GatewayWebhookRoute.TokenMatches(null, t1));
        Assert.False(GatewayWebhookRoute.TokenMatches(t1, null));
        // รูปของ migration (hex 64 ตัว) ใช้ได้
        Assert.True(GatewayWebhookRoute.IsWellFormedToken(new string('a', 64)));
        Assert.Equal("/api/pay/webhooks/x/" + t1, GatewayWebhookRoute.Path("x", t1));
        Assert.Equal("/api/pay/webhooks/x", GatewayWebhookRoute.Path("x", null));
    }

    [Fact]
    public void GW1_คำเตือนURLเดิม_สองทิศ()
    {
        var now = DateTime.UtcNow;
        var notMoved = new GatewayWebhookConfigFacts(Guid.NewGuid(), "t", null);
        Assert.Contains("URL แบบเดิม", GatewayWebhookRoute.LegacyUrlWarning(now, notMoved));
        Assert.Null(GatewayWebhookRoute.LegacyUrlWarning(null, notMoved));
        var moved = new GatewayWebhookConfigFacts(Guid.NewGuid(), "t", now, PaymentProviderMode.Test, PaymentProviderMode.Test);
        Assert.Null(GatewayWebhookRoute.LegacyUrlWarning(now.AddDays(-1), moved));   // เก่ากว่าการย้าย = ไม่เตือน
        Assert.Contains("สองที่", GatewayWebhookRoute.LegacyUrlWarning(now.AddMinutes(5), moved));
    }

    // ═══ A-GW4: รอบโอน batch ที่ลงบัญชีแล้ว = โอนแล้ว (กระทบยอด) ═══

    private static GatewayReconciliationIntentRow BatchRow(decimal amount, decimal fee, bool posted, decimal refunded = 0m)
        => new(amount, fee, 0m, null, false, false, refunded, 0m, 0m, GatewayFeeVatMode.None, null,
            OwnedByBatch: true, BatchPosted: posted);

    [Fact]
    public void GW4_batchลงบัญชีแล้ว_นับว่าโอนแล้ว_สมดุล()
    {
        var lines = new[]
        {
            new GatewayBatchLineFact(SettlementLineType.Sale, 1000m),
            new GatewayBatchLineFact(SettlementLineType.PaymentFee, -36.50m),
        };
        var amounts = GatewayReconciliation.BatchSettled(lines);
        Assert.Equal(963.50m, amounts.SettledAmount);
        Assert.Equal(36.50m, amounts.FeeDeducted);
        var row = GatewayReconciliation.FromIntent(BatchRow(1000m, 36.50m, posted: true), amounts);
        Assert.True(row.IsSettled);
        var r = GatewayReconciliation.Compute(new[] { row });
        Assert.Equal(963.50m, r.SettledTotal);
        Assert.Equal(0m, r.UnsettledAmount);
        Assert.True(r.IsBalanced);
    }

    [Fact]
    public void GW4_batchลงบัญชีแล้ว_คืนเงินหลังรอบ_บรรทัดคืนในรอบถัดไปนับแล้ว()
    {
        var amounts = GatewayReconciliation.BatchSettled(new[]
        {
            new GatewayBatchLineFact(SettlementLineType.Sale, 1000m),
            new GatewayBatchLineFact(SettlementLineType.PaymentFee, -36.50m),
            new GatewayBatchLineFact(SettlementLineType.Refund, -200m),   // รอบถัดไป (ลงบัญชีแล้ว)
        });
        Assert.Equal(200m, amounts.RefundSettledAmount);
        var r = GatewayReconciliation.Compute(new[]
        {
            GatewayReconciliation.FromIntent(new GatewayReconciliationIntentRow(1000m, 36.50m, 0m, null, false, false, 200m, 0m, 0m,
                GatewayFeeVatMode.None, null, OwnedByBatch: true, BatchPosted: true), amounts),
        });
        Assert.Equal(763.50m, r.SettledTotal);
        Assert.True(r.IsBalanced);
    }

    [Fact]
    public void GW4_ทิศตรงข้าม_batchฉบับร่าง_ยังไม่โอน()
    {
        foreach (var st in new[] { SettlementBatchStatus.Imported, SettlementBatchStatus.Classified, SettlementBatchStatus.Matched,
                     SettlementBatchStatus.Voided })
            Assert.False(GatewayReconciliation.IsBatchPosted(st));
        Assert.True(GatewayReconciliation.IsBatchPosted(SettlementBatchStatus.Posted));
        Assert.True(GatewayReconciliation.IsBatchPosted(SettlementBatchStatus.BankMatched));
        Assert.False(GatewayReconciliation.IsBatchPosted(null));

        var row = GatewayReconciliation.FromIntent(BatchRow(1000m, 36.50m, posted: false), null);
        Assert.False(row.IsSettled);
        var r = GatewayReconciliation.Compute(new[] { row });
        Assert.Equal(0m, r.SettledTotal);
        Assert.Equal(963.50m, r.UnsettledAmount);
        Assert.True(r.IsBalanced);
    }

    [Fact]
    public void GW4_ทิศตรงข้าม_เจ้าของเส้นเดิม_ใช้ช่องเดิมทุกช่อง()
    {
        var legacy = new GatewayReconciliationIntentRow(1000m, 36.50m, 0m, 963.50m, false, true, 0m, 0m, 0m,
            GatewayFeeVatMode.None, 36.50m, OwnedByBatch: false, BatchPosted: false);
        var a = GatewayReconciliation.FromIntent(legacy, null);
        Assert.True(a.IsSettled);
        Assert.Equal(963.50m, a.SettledAmount);
        Assert.Equal(36.50m, a.SettledFeeDeducted);
    }

    // ═══ A-GW9: แถวเก่าที่แยกยอดคืนหักรอบหลังไม่ได้ — นับให้เห็น ไม่เติมย้อนหลัง ═══

    [Fact]
    public void GW9_แถวเก่ามียอดคืนหักแล้วแต่ไม่รู้ค่าธรรมเนียมที่หัก_ถูกนับ()
    {
        var old = new GatewayReconciliationIntentRow(1000m, 36.50m, 0m, 763.50m, false, true, 200m, 200m, 0m,
            GatewayFeeVatMode.None, null, false, false);
        Assert.True(GatewayReconciliation.LateRefundSplitUnknown(old));
        Assert.NotNull(GatewayReconciliation.LateRefundSplitUnknownMessage(1));
    }

    [Fact]
    public void GW9_ทิศตรงข้าม_แถวใหม่หรือไม่มีคืน_ไม่ถูกนับ()
    {
        Assert.False(GatewayReconciliation.LateRefundSplitUnknown(new GatewayReconciliationIntentRow(1000m, 36.50m, 0m, 963.50m, false, true,
            0m, 0m, 0m, GatewayFeeVatMode.None, null, false, false)));
        Assert.False(GatewayReconciliation.LateRefundSplitUnknown(new GatewayReconciliationIntentRow(1000m, 36.50m, 0m, 763.50m, false, true,
            200m, 200m, 0m, GatewayFeeVatMode.None, 36.50m, false, false)));
        Assert.Null(GatewayReconciliation.LateRefundSplitUnknownMessage(0));
    }

    // ═══ A-GW2: ป้าย/ปุ่ม/ค้างนาน — เซิร์ฟเวอร์ตัดสิน ═══

    [Fact]
    public void GW2_ค้างนาน_เกณฑ์เดียว30นาที_เฉพาะสถานะเปิด()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.FromMinutes(30), PaymentIntentPolicy.StuckThreshold);
        Assert.True(PaymentIntentPolicy.IsStuck(PaymentIntentStatus.Pending, now.AddMinutes(-31), now));
        Assert.True(PaymentIntentPolicy.IsStuck(PaymentIntentStatus.Created, now.AddHours(-2), now));
        Assert.False(PaymentIntentPolicy.IsStuck(PaymentIntentStatus.Pending, now.AddMinutes(-29), now));
        Assert.False(PaymentIntentPolicy.IsStuck(PaymentIntentStatus.Succeeded, now.AddHours(-5), now));
        Assert.False(PaymentIntentPolicy.IsStuck(PaymentIntentStatus.Expired, now.AddHours(-5), now));
    }

    [Fact]
    public void GW2_ป้ายภาษาไทยครบทุกสถานะและทุกที่มา()
    {
        foreach (var s in Enum.GetValues<PaymentIntentStatus>())
            Assert.NotEqual(s.ToString(), PaymentIntentPolicy.StatusLabel(s));
        foreach (var k in Enum.GetValues<PaymentSourceKind>())
            Assert.NotEqual(k.ToString(), PaymentIntentPolicy.SourceKindLabel(k));
        var opts = PaymentIntentPolicy.StatusOptions();
        Assert.Equal(Enum.GetValues<PaymentIntentStatus>().Length, opts.Count);
        Assert.Contains(opts, o => o.Value == "PartiallyRefunded" && o.Label == "คืนเงินบางส่วน");
    }

    [Fact]
    public void GW2_ปุ่มแก้ค่าธรรมเนียม_ด่านเดียวกับservice_สองทิศ()
    {
        Assert.True(PaymentIntentPolicy.CanEditFee(PaymentIntentStatus.Succeeded, false, false));
        Assert.True(PaymentIntentPolicy.CanEditFee(PaymentIntentStatus.Refunded, false, false));
        Assert.False(PaymentIntentPolicy.CanEditFee(PaymentIntentStatus.Succeeded, true, false));   // ใบสำคัญรอบโอนแล้ว
        Assert.False(PaymentIntentPolicy.CanEditFee(PaymentIntentStatus.Succeeded, false, true));   // อยู่ในรอบโอน batch (แม้ร่าง)
        Assert.False(PaymentIntentPolicy.CanEditFee(PaymentIntentStatus.Pending, false, false));
    }

    // ═══ A-GW3: URL กลับหลังจ่าย — เฉพาะโดเมนของบริษัท ═══

    private static readonly string[] Hosts = { "shop.example.co.th", "booking.myhotel.com" };
    private const string Base = "https://app.nextacc.com";

    [Theory]
    [InlineData("https://evil.com/pay")]
    [InlineData("//evil.com/pay")]
    [InlineData("/\\evil.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://shop.example.co.th@evil.com/")]
    [InlineData("https://shop.example.co.th.evil.com/")]
    [InlineData("ftp://shop.example.co.th/")]
    public void GW3_โดเมนอื่น_ถูกแทนด้วยหน้าแรกของระบบ(string url)
        => Assert.Equal(Base + "/", PaymentIntentPolicy.SafeReturnUrl(url, Hosts, Base));

    [Theory]
    [InlineData("https://shop.example.co.th/order/1?x=2")]
    [InlineData("https://SHOP.example.co.th/order/1")]
    [InlineData("https://booking.myhotel.com/r/abc")]
    [InlineData("https://app.nextacc.com/pages/addons.html")]
    public void GW3_ทิศตรงข้าม_โดเมนของบริษัทหรือระบบ_ผ่านตามเดิม(string url)
        => Assert.Equal(url, PaymentIntentPolicy.SafeReturnUrl(url, Hosts, Base));

    [Fact]
    public void GW3_pathสัมพัทธ์ต่อท้ายโดเมนระบบ_ไม่มีURLคงnull_ไม่รู้โดเมนระบบไม่แต่ง()
    {
        Assert.Equal(Base + "/pages/x.html", PaymentIntentPolicy.SafeReturnUrl("/pages/x.html", Hosts, Base));
        Assert.Null(PaymentIntentPolicy.SafeReturnUrl(null, Hosts, Base));
        Assert.Null(PaymentIntentPolicy.SafeReturnUrl("  ", Hosts, Base));
        Assert.Null(PaymentIntentPolicy.SafeReturnUrl("https://evil.com", Hosts, null));
        Assert.Equal("https://shop.example.co.th/a", PaymentIntentPolicy.SafeReturnUrl("https://shop.example.co.th/a", Hosts, null));
    }

    // ═══ C-10: config ผูกช่องทาง batch ⇒ เส้นเดิมไม่รับรายการใหม่ (คงเส้นคืนเงินภายหลัง) ═══

    [Fact]
    public void C10_มีช่องทางbatchผูก_เส้นเดิมไม่รับรายการใหม่_พร้อมลิงก์หน้ารอบโอน()
    {
        var bound = new[] { "รับชำระออนไลน์" };
        Assert.False(GatewayBatchIntentRules.LegacyAcceptsNewIntents(bound));
        var m = GatewayBatchIntentRules.LegacyNewIntentsMovedMessage(bound, 3);
        Assert.NotNull(m);
        Assert.Contains("3 รายการ", m);
        Assert.Contains(GatewayBatchIntentRules.BatchSettlementPage, m);
        Assert.Contains("คืนเงินภายหลัง", m);
    }

    [Fact]
    public void C10_ทิศตรงข้าม_ไม่มีช่องทางผูก_เส้นเดิมเหมือนเดิม()
    {
        Assert.True(GatewayBatchIntentRules.LegacyAcceptsNewIntents(Array.Empty<string>()));
        Assert.Null(GatewayBatchIntentRules.LegacyNewIntentsMovedMessage(Array.Empty<string>(), 5));
    }

    // ═══ C-11: ผูก config ครั้งแรก ⇒ payment_fee = ผังของเส้นเดิม (แก้ได้) ═══

    [Fact]
    public void C11_ผูกครั้งแรก_เติมpayment_feeจากconfig()
    {
        var fee = Guid.NewGuid();
        var other = Guid.NewGuid();
        var seeded = GatewayBatchIntentRules.SeedFeeAccountMapOnFirstBinding(
            new Dictionary<string, Guid> { [SettlementAccountRoles.Commission] = other }, true, fee);
        Assert.Equal(fee, seeded[SettlementAccountRoles.PaymentFee]);
        Assert.Equal(other, seeded[SettlementAccountRoles.Commission]);
    }

    [Fact]
    public void C11_ทิศตรงข้าม_ผู้ใช้ตั้งเอง_หรือไม่ใช่ครั้งแรก_หรือไม่มีผัง_ไม่แตะ()
    {
        var mine = Guid.NewGuid();
        var req = new Dictionary<string, Guid> { [SettlementAccountRoles.PaymentFee] = mine };
        Assert.Equal(mine, GatewayBatchIntentRules.SeedFeeAccountMapOnFirstBinding(req, true, Guid.NewGuid())[SettlementAccountRoles.PaymentFee]);
        var empty = new Dictionary<string, Guid>();
        Assert.Empty(GatewayBatchIntentRules.SeedFeeAccountMapOnFirstBinding(empty, false, Guid.NewGuid()));
        Assert.Empty(GatewayBatchIntentRules.SeedFeeAccountMapOnFirstBinding(empty, true, null));
    }

    // ═══ A-GW12: resync — สร้างใหม่ได้ก่อนค่อยกลับ JE เดิม ═══

    [Fact]
    public void GW12_สร้างใหม่ไม่ได้_คงJEเดิม_ไม่กลับ()
    {
        Assert.Equal(IntegrationResyncJournalAction.KeepOriginal, IntegrationResyncJournal.Decide(false, 1, rebuildable: false));
        Assert.Equal(IntegrationResyncJournalAction.KeepOriginal, IntegrationResyncJournal.Decide(false, 2, rebuildable: false));
        var note = IntegrationResyncJournal.KeepOriginalNote("ผังปิดใช้งาน");
        Assert.StartsWith(IntegrationResyncJournal.KeepOriginalTag, note);
        Assert.Contains("ผังปิดใช้งาน", note);
        Assert.Contains("resyncUpdate", note);
        Assert.DoesNotContain("จากหน้าเอกสาร", note);   // ไม่มีปุ่มนั้นในระบบ
    }

    [Fact]
    public void GW12_ทิศตรงข้าม_สร้างใหม่ได้กลับแล้วลงใหม่_inplaceสำเร็จ_ไม่มีJEเดิมลงใหม่()
    {
        Assert.Equal(IntegrationResyncJournalAction.ReverseAndRepost, IntegrationResyncJournal.Decide(false, 1, rebuildable: true));
        Assert.Equal(IntegrationResyncJournalAction.InPlace, IntegrationResyncJournal.Decide(true, 1, rebuildable: false));
        Assert.Equal(IntegrationResyncJournalAction.PostFresh, IntegrationResyncJournal.Decide(false, 0, rebuildable: false));
    }

    // ═══ A-GW11: คำเตือนล่วงหน้าบัญชีธนาคารของ integration ═══

    [Fact]
    public void GW11_ไม่มีหรือหลายบัญชี_เตือนพร้อมทางไปต่อ()
    {
        var none = MoneyAccountFallback.IntegrationBankWarning(MoneyAccountFallback.PickBank(Array.Empty<Guid>(), out _));
        var many = MoneyAccountFallback.IntegrationBankWarning(MoneyAccountFallback.PickBank(new[] { Guid.NewGuid(), Guid.NewGuid() }, out _));
        Assert.Contains("ยังไม่มีบัญชีธนาคาร", none);
        Assert.Contains("bankAccountName", many);
        Assert.DoesNotContain("<", many);   // ข้อความล้วน (หน้าเว็บ esc)
    }

    [Fact]
    public void GW11_ทิศตรงข้าม_บัญชีเดียว_ไม่เตือน()
    {
        var g = Guid.NewGuid();
        Assert.Null(MoneyAccountFallback.IntegrationBankWarning(MoneyAccountFallback.PickBank(new[] { g, g }, out _)));
    }

    // ═══ A-GW5: VAT ค่าธรรมเนียมของรอบโอน batch — แสดง/บอกทาง ไม่รวมเข้า 11630 ═══

    [Fact]
    public void GW5_มีส่วนของรอบโอนbatch_ข้อความบอกเคลมที่ใบสำคัญจ่าย_และต่อท้ายด่านยอดเกิน()
    {
        var note = GatewayFeeVatClaim.BatchPortionNote(12.34m, 2);
        Assert.NotNull(note);
        Assert.Contains("12.34", note);
        Assert.Contains("ห้ามเคลมส่วนนั้นซ้ำที่นี่", note);
        var c = GatewayFeeVatClaim.Check(20m, 7.66m, "INV1", new DateTime(2026, 9, 30), new DateTime(2026, 9, 30),
            "ผู้ให้บริการ", "0105536000313", "00000", null, note);
        Assert.False(c.Ok);
        Assert.Contains("ห้ามเคลมส่วนนั้นซ้ำที่นี่", c.Message);
    }

    [Fact]
    public void GW5_ทิศตรงข้าม_ไม่มีส่วนbatch_ข้อความเดิม_และยอดในที่พักผ่าน()
    {
        Assert.Null(GatewayFeeVatClaim.BatchPortionNote(0m, 0));
        Assert.Null(GatewayFeeVatClaim.BatchPortionNote(5m, 0));
        var c = GatewayFeeVatClaim.Check(7.66m, 7.66m, "INV1", new DateTime(2026, 9, 30), new DateTime(2026, 9, 30),
            "ผู้ให้บริการ", "0105536000313", "00000", null, GatewayFeeVatClaim.BatchPortionNote(12.34m, 2));
        Assert.True(c.Ok);
    }

    // ═══ A-GW8: ปรับปรุงเศษ 11630 ภายใต้เกณฑ์ ═══

    [Fact]
    public void GW8_เศษไม่เกินเกณฑ์ต่อใบ_ผ่านทั้งบวกและลบ()
    {
        var sept = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var ok = GatewayFeeVatClaim.ResidueCheck(0.37m, 1, sept, new DateTime(2026, 9, 30));
        Assert.True(ok.Ok);
        Assert.Equal(0.37m, ok.Amount);
        var neg = GatewayFeeVatClaim.ResidueCheck(-0.02m, 1, sept, new DateTime(2026, 10, 1));
        Assert.True(neg.Ok);
        Assert.Equal(-0.02m, neg.Amount);
        Assert.True(GatewayFeeVatClaim.ResidueCheck(1.80m, 2, sept, new DateTime(2026, 9, 30)).Ok);   // 2 ใบ × 1 บาท
    }

    [Fact]
    public void GW8_ทิศตรงข้าม_เกินเกณฑ์_ยังไม่เคลม_ยังมีเดือนที่ไม่ได้รับใบ_ไม่มีเศษ_ปฏิเสธ()
    {
        var sept = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.False(GatewayFeeVatClaim.ResidueCheck(1.01m, 1, sept, new DateTime(2026, 9, 30)).Ok);
        Assert.False(GatewayFeeVatClaim.ResidueCheck(0.37m, 0, sept, null).Ok);
        var late = GatewayFeeVatClaim.ResidueCheck(0.37m, 1, sept, new DateTime(2026, 8, 31));
        Assert.False(late.Ok);
        Assert.Contains("09/2026", late.Message);
        Assert.False(GatewayFeeVatClaim.ResidueCheck(0m, 1, sept, new DateTime(2026, 9, 30)).Ok);
        Assert.False(GatewayFeeVatClaim.ResidueTag("x").StartsWith(GatewayFeeVatClaim.ClaimTagPrefix));   // ไม่เข้าการหาเคลมซ้ำ
    }

    // ═══ A-GW7: บันทึกยอดคืนย้อนหลัง ═══

    private static readonly DateTime Now = new(2026, 10, 1, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GW7_ยอดเกินยอดรับ_ปฏิเสธ_สถานะคืนเต็มยอดต้องเท่า()
    {
        var over = GatewayRefundMath.CheckLegacyRefundEntry(PaymentIntentStatus.PartiallyRefunded, 1000m, 0m, false, 1000.01m,
            Now.AddDays(-3), Now, "rfnd_1", "เห็นในแดชบอร์ด", GatewayLegacyRefundJournal.BookNow);
        Assert.False(over.Ok);
        Assert.False(GatewayRefundMath.CheckLegacyRefundEntry(PaymentIntentStatus.Refunded, 1000m, 0m, false, 400m,
            Now.AddDays(-3), Now, "rfnd_1", "e", GatewayLegacyRefundJournal.BookNow).Ok);
        Assert.False(GatewayRefundMath.CheckLegacyRefundEntry(PaymentIntentStatus.PartiallyRefunded, 1000m, 0m, false, 1000m,
            Now.AddDays(-3), Now, "rfnd_1", "e", GatewayLegacyRefundJournal.BookNow).Ok);
        // มียอดคืนในระบบแล้ว / ยังไม่คืน / ผลไม่แน่ชัด / ไม่เลือกลงบัญชี / ไม่มีหลักฐาน / วันที่อนาคต = ปฏิเสธ
        Assert.False(GatewayRefundMath.CheckLegacyRefundEntry(PaymentIntentStatus.PartiallyRefunded, 1000m, 100m, false, 100m,
            Now.AddDays(-3), Now, "r", "e", GatewayLegacyRefundJournal.BookNow).Ok);
        Assert.False(GatewayRefundMath.CheckLegacyRefundEntry(PaymentIntentStatus.Succeeded, 1000m, 0m, false, 100m,
            Now.AddDays(-3), Now, "r", "e", GatewayLegacyRefundJournal.BookNow).Ok);
        Assert.False(GatewayRefundMath.CheckLegacyRefundEntry(PaymentIntentStatus.Refunded, 1000m, 0m, true, 1000m,
            Now.AddDays(-3), Now, "r", "e", GatewayLegacyRefundJournal.BookNow).Ok);
        Assert.False(GatewayRefundMath.CheckLegacyRefundEntry(PaymentIntentStatus.Refunded, 1000m, 0m, false, 1000m,
            Now.AddDays(-3), Now, "r", "e", GatewayLegacyRefundJournal.Unspecified).Ok);
        Assert.False(GatewayRefundMath.CheckLegacyRefundEntry(PaymentIntentStatus.Refunded, 1000m, 0m, false, 1000m,
            Now.AddDays(-3), Now, "r", " ", GatewayLegacyRefundJournal.BookNow).Ok);
        Assert.False(GatewayRefundMath.CheckLegacyRefundEntry(PaymentIntentStatus.Refunded, 1000m, 0m, false, 1000m,
            Now.AddDays(2), Now, "r", "e", GatewayLegacyRefundJournal.BookNow).Ok);
    }

    [Fact]
    public void GW7_ทิศตรงข้าม_แถวเก่าที่ข้อมูลครบ_ผ่าน()
    {
        var full = GatewayRefundMath.CheckLegacyRefundEntry(PaymentIntentStatus.Refunded, 1000m, 0m, false, 1000m,
            Now.AddDays(-30), Now, "rfnd_1", "แดชบอร์ดแสดงคืนเต็ม", GatewayLegacyRefundJournal.AlreadyBookedManually);
        Assert.True(full.Ok);
        Assert.Equal(1000m, full.AmountToBook);
        var part = GatewayRefundMath.CheckLegacyRefundEntry(PaymentIntentStatus.PartiallyRefunded, 1000m, 0m, false, 250m,
            Now.AddDays(-30), Now, "rfnd_2", "แดชบอร์ดแสดงคืน 250", GatewayLegacyRefundJournal.BookNow);
        Assert.True(part.Ok);
        Assert.Equal(250m, part.AmountToBook);
    }

    // ═══ A-GW10: เมนูรับชำระออนไลน์ผูกสิทธิ์จากตารางเดียว ═══

    [Fact]
    public void GW10_เมนูหน้ารับชำระผูกสิทธิ์ดูธนาคาร_ตรงกับendpoint()
    {
        Assert.Equal(PaymentGatewayPermissionScope.ViewPayments, PaymentGatewayPermissionScope.MenuPermissionKeys["payment-intents"]);
        Assert.Equal(PaymentGatewayPermissionScope.PreviewSettlement, PaymentGatewayPermissionScope.MenuPermissionKeys["payment-settlements"]);
        // ตั้งค่า gateway เป็นงานเจ้าของ (RequireOwner ที่ endpoint) — ไม่อยู่ในตารางนี้ (ทิศตรงข้าม: ไม่ซ่อนเมนูที่ไม่ได้ใช้คีย์ perm:*)
        Assert.False(PaymentGatewayPermissionScope.MenuPermissionKeys.ContainsKey("payment-settings"));
    }

    // ═══ B-1: คำเตือนล่วงหน้าของ adapter (ไม่ต่อสาย fee_vat) ═══

    [Fact]
    public void B1_adapterประกาศคำเตือนล่วงหน้า_สลิปไม่มี()
    {
        var omise = (Accounting.Services.Payments.IPaymentProvider)new OmisePaymentProvider(new FakeFactory(new CapturingHandler()),
            new PlainSecrets(), NullLogger<OmisePaymentProvider>.Instance);
        Assert.Contains(OmisePaymentProvider.ApiVersion, omise.PendingVerificationNotice);
        var slip = (Accounting.Services.Payments.IPaymentProvider)new ManualSlipPaymentProvider();
        Assert.Null(slip.PendingVerificationNotice);
    }
}
