using System.Net;
using System.Text.Json;
using Accounting.Helpers;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Tests.Db;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 254 WP-11 L2 (AUDIT_249 WP-11 · LINE_MULTICHANNEL_DESIGN §3.d–§3.f · คำตอบเจ้าของ §10 Q1(b)/Q5/Q6/Q7 · คำตัดสิน N8) — ตัวตัดสิน pure ทั้งสองทิศ:
/// จำแนกผล LINE (<see cref="LinePushOutcome"/>) · ช่องทางต่อกฎ/แผนเข้าคิว/ถอยไปอีเมล (<see cref="ContactLineChannel"/>) · ช่อง LINE ต่อขอบเขต OA
/// (<see cref="ContactRecipientRule.LineGate"/>) · กลุ่มกลางของแอดมินไม่มีวันเป็นปลายทาง · การ์ด (<see cref="LineDocumentCard"/>) · ลิงก์ผูก/หยุด (<see cref="ContactLineBindRule"/>) ·
/// <c>X-Line-Retry-Key</c> ทุก push
/// </summary>
public class LineDeliveryRound254Tests
{
    private static readonly Guid Co = Guid.NewGuid();
    private static readonly Guid ContactId = Guid.NewGuid();
    private const string AdminGroup = "Cadmin000000000000000000000000001";
    private const string ConfigGroup = "Cconfig00000000000000000000000002";
    private static readonly string[] Platform = NextAccLineConfigRule.PlatformGroupIds(AdminGroup, ConfigGroup).ToArray();

    // ───── LinePushOutcome (ตาราง §3.e ครบทุกแถว) ─────

    [Fact]
    public void Outcome_200_และ_409_ของ_key_เดิม_คือส่งแล้ว()
    {
        Assert.Equal(LinePushVerdict.Sent, LinePushOutcome.Classify(true, 200, null, null, false).Verdict);
        var dup = LinePushOutcome.Classify(false, 409, "LINE ตอบ 409", null, false);
        Assert.Equal(LinePushVerdict.Sent, dup.Verdict);
        Assert.Equal(LinePushOutcome.CodeAlreadyAccepted, dup.Code);
    }

    [Fact]
    public void Outcome_นโยบายปฏิเสธ_ล้มถาวร_ไม่ลองซ้ำ()
    {
        var c = LinePushOutcome.Classify(false, null, "กลุ่มกลาง", ContactRecipientRule.RuleCodePlatformGroup, false);
        Assert.Equal(LinePushVerdict.Failed, c.Verdict);
        Assert.Equal(LinePushOutcome.CodeDenied, c.Code);
    }

    [Theory]
    [InlineData(400, "The user hasn't added the LINE Official Account as a friend", LinePushVerdict.Failed, LinePushOutcome.CodeNotFriend, true, false)]
    [InlineData(400, "The property, 'messages[0].contents', is invalid", LinePushVerdict.Failed, LinePushOutcome.CodeBadRequest, false, false)]
    [InlineData(401, "Authentication failed", LinePushVerdict.Failed, LinePushOutcome.CodeAuth, false, true)]
    [InlineData(403, "Access to this API is not available", LinePushVerdict.Failed, LinePushOutcome.CodeAuth, false, true)]
    [InlineData(429, "You have reached your monthly limit.", LinePushVerdict.Failed, LinePushOutcome.CodeMonthlyLimit, false, false)]
    [InlineData(429, "Too many requests", LinePushVerdict.Retry, LinePushOutcome.CodeRateLimit, false, false)]
    [InlineData(500, "Internal server error", LinePushVerdict.Retry, LinePushOutcome.CodeServer, false, false)]
    [InlineData(418, "teapot", LinePushVerdict.Failed, LinePushOutcome.CodeUnknown, false, false)]
    public void Outcome_ตามสถานะ_HTTP(int status, string body, LinePushVerdict verdict, string code, bool block, bool broken)
    {
        var c = LinePushOutcome.Classify(false, status, $"LINE ตอบ {status}: {body}", null, false);
        Assert.Equal(verdict, c.Verdict);
        Assert.Equal(code, c.Code);
        Assert.Equal(block, c.BlockEndpoint);
        Assert.Equal(broken, c.ChannelBroken);
    }

    [Fact]
    public void Outcome_เครือข่ายล้ม_ลองใหม่_ส่วนไม่มี_token_ล้มถาวร()
    {
        Assert.Equal(LinePushVerdict.Retry, LinePushOutcome.Classify(false, null, "เรียก LINE ไม่สำเร็จ (TaskCanceledException)", null, true).Verdict);
        var noToken = LinePushOutcome.Classify(false, null, "ยังไม่ได้ตั้งค่า Channel Access Token ของ LINE OA", null, false);
        Assert.Equal(LinePushVerdict.Failed, noToken.Verdict);
        Assert.True(noToken.ChannelBroken);
        Assert.Equal(TimeSpan.FromMinutes(1), LinePushOutcome.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(5), LinePushOutcome.Backoff(2));
        Assert.Equal(TimeSpan.FromMinutes(30), LinePushOutcome.Backoff(3));
    }

    // ───── ด่านบันทึกกฎ (ทั้งสองทิศ) ─────

    [Fact]
    public void RuleProblem_ไม่มีช่องทางเลย_ปฏิเสธ_อีเมลอย่างเดียว_ผ่านแม้ไม่มี_OA()
    {
        Assert.NotNull(ContactLineChannel.RuleProblem("DocumentApproved", false, false, false, false, null));
        Assert.Null(ContactLineChannel.RuleProblem("DocumentApproved", true, false, false, false, null));   // ทิศตรงข้าม: กฎเดิมไม่ถูกแตะ
        Assert.Null(ContactLineChannel.RuleProblem("PayrollPaid", true, false, false, false, null));
    }

    [Theory]
    [InlineData("PayrollPaid", "สลิป")]
    [InlineData("MonthlyStatement", "ยังส่งทาง LINE ไม่ได้")]
    [InlineData("WhtCertIssued", "ยังส่งทาง LINE ไม่ได้")]
    public void RuleProblem_LINE_กับ_trigger_ที่ไม่รองรับ_ปฏิเสธพร้อมเหตุ(string trigger, string expected)
        => Assert.Contains(expected, ContactLineChannel.RuleProblem(trigger, true, true, false, true, null));

    [Fact]
    public void RuleProblem_ไม่มี_OA_บริษัท_ปฏิเสธพร้อมทางไปต่อ_มี_OA_ผ่าน()
    {
        var p = ContactLineChannel.RuleProblem("DocumentDueSoon", false, true, false, false, "ยังไม่ได้เปิดใช้ LINE OA ของบริษัท");
        Assert.Contains("ตั้งค่า → แจ้งเตือนทาง LINE", p);
        Assert.Null(ContactLineChannel.RuleProblem("DocumentDueSoon", false, true, true, true, null));
    }

    // ───── แผนเข้าคิว + ถอยไปอีเมล (Q7) ─────

    private static ContactRecipientRule.RecipientOption Opt(string kind, bool available, string? reason = null)
        => new(Guid.NewGuid(), kind, null, kind, available, reason, null, true, false);

    [Fact]
    public void Plan_กฎอีเมลอย่างเดียว_ไม่มีแถว_LINE_และไม่ถอย()
    {
        var plan = ContactLineChannel.Plan(true, false, false, true, new[] { Opt(NotifyEndpointKind.LineUser, true) }, null);
        Assert.Empty(plan.Endpoints);
        Assert.False(plan.FallbackNow);
        Assert.Null(plan.NotSent);
    }

    [Fact]
    public void Plan_เลือกเฉพาะชนิดที่ติ๊กและใช้ได้()
    {
        var user = Opt(NotifyEndpointKind.LineUser, true);
        var group = Opt(NotifyEndpointKind.LineGroup, true);
        var blocked = Opt(NotifyEndpointKind.LineUser, false, "ผู้รับบล็อก");
        var plan = ContactLineChannel.Plan(false, true, false, true, new[] { user, group, blocked }, null);
        Assert.Equal(user.EndpointId, Assert.Single(plan.Endpoints).EndpointId);
        Assert.False(plan.FallbackNow);
    }

    [Fact]
    public void Plan_ไม่มีปลายทาง_ไม่ติ๊กอีเมล_ถอยไปอีเมล_ทิศตรงข้ามติ๊กอีเมลหรือปิดถอย_ไม่ถอย()
    {
        var none = new[] { Opt(NotifyEndpointKind.LineUser, false, "ไม่มีความยินยอม") };
        var fb = ContactLineChannel.Plan(false, true, false, true, none, null);
        Assert.True(fb.FallbackNow);
        Assert.Contains("ส่งอีเมลแทน", fb.NotSent);
        Assert.False(ContactLineChannel.Plan(true, true, false, true, none, null).FallbackNow);   // อีเมลออกอยู่แล้ว
        var off = ContactLineChannel.Plan(false, true, false, false, none, null);
        Assert.False(off.FallbackNow);
        Assert.Contains("ไม่มีความยินยอม", off.NotSent);
    }

    [Fact]
    public void Plan_ผู้ติดต่อไม่ส่งอัตโนมัติ_ไม่ส่ง_LINE_และไม่ถอย()
    {
        var plan = ContactLineChannel.Plan(false, true, true, true, new[] { Opt(NotifyEndpointKind.LineUser, true) }, "ผู้ติดต่อปิดส่งอัตโนมัติ");
        Assert.Empty(plan.Endpoints);
        Assert.False(plan.FallbackNow);
        Assert.Equal("ผู้ติดต่อปิดส่งอัตโนมัติ", plan.NotSent);
    }

    [Fact]
    public void EmailWanted_ติ๊กอีเมล_หรือถอย_หรือบังคับ()
    {
        Assert.True(ContactLineChannel.EmailWanted(true, false, false));
        Assert.True(ContactLineChannel.EmailWanted(false, true, false));
        Assert.True(ContactLineChannel.EmailWanted(false, false, true));
        Assert.False(ContactLineChannel.EmailWanted(false, false, false));
    }

    [Fact]
    public void RetryKey_คงที่ต่อเหตุการณ์และผู้รับ_ต่างกันเมื่อผู้รับต่าง_และเป็น_UUID_v4()
    {
        var k1 = ContactLineChannel.QueueKey("doc:x:DocumentApproved:approved", Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var k2 = ContactLineChannel.QueueKey("doc:x:DocumentApproved:approved", Guid.Parse("22222222-2222-2222-2222-222222222222"));
        Assert.Equal(ContactLineChannel.RetryKey(Co, k1), ContactLineChannel.RetryKey(Co, k1));
        Assert.NotEqual(ContactLineChannel.RetryKey(Co, k1), ContactLineChannel.RetryKey(Co, k2));
        Assert.NotEqual(ContactLineChannel.RetryKey(Co, k1), ContactLineChannel.RetryKey(Guid.NewGuid(), k1));
        Assert.Equal('4', ContactLineChannel.RetryKey(Co, k1).ToString("D")[14]);
    }

    [Fact]
    public void FallbackSuffix_ต่อท้ายกุญแจเดิม_ตัวแยกเดิมยังอ่านได้_กุญแจของใบอื่น_null()
    {
        var doc = Guid.NewGuid();
        var rec = Guid.NewGuid();
        var baseKey = $"doc:{doc}:RecurringInvoiceCreated:recurring-{rec}";
        var suffix = ContactLineChannel.FallbackSuffix(baseKey, doc, "RecurringInvoiceCreated");
        Assert.Equal($"recurring-{rec}:line-fallback", suffix);
        var fbKey = $"doc:{doc}:RecurringInvoiceCreated:{suffix}";
        Assert.Equal("RecurringInvoiceCreated", ScheduledEmailSendCheck.TriggerFromIdempotencyKey(fbKey));
        Assert.Equal(rec, ScheduledEmailSendCheck.RecurringIdFromIdempotencyKey(fbKey));
        Assert.Null(ContactLineChannel.FallbackSuffix(baseKey, Guid.NewGuid(), "RecurringInvoiceCreated"));
    }

    [Fact]
    public void FallbackNeeded_ถอยครั้งเดียว_ไม่ถอยเมื่อปลายทางอื่นส่งแล้ว_หรือรอส่ง()
    {
        Assert.True(ContactLineChannel.FallbackNeeded(true, true, false, new[] { ContactLineChannel.StatusFailed }));
        Assert.False(ContactLineChannel.FallbackNeeded(true, true, false, new[] { ContactLineChannel.StatusSent }));
        Assert.False(ContactLineChannel.FallbackNeeded(true, true, false, new[] { ContactLineChannel.StatusPending }));
        Assert.False(ContactLineChannel.FallbackNeeded(true, true, true, Array.Empty<string>()));
        Assert.False(ContactLineChannel.FallbackNeeded(false, true, false, Array.Empty<string>()));
        Assert.False(ContactLineChannel.FallbackNeeded(true, false, false, Array.Empty<string>()));
    }

    // ───── ช่อง LINE ต่อขอบเขต OA (N8) + กลุ่มกลางของแอดมิน (Q1(b)) ─────

    private static ContactRecipientRule.EndpointFacts Ep(string kind, string scope, string? groupId = null, string? dest = "Ubot1",
        bool verified = true, int? members = 5)
        => new(Guid.NewGuid(), Co, ContactId, kind, null, null, groupId, scope, null, members,
            NotifyTopics.Serialize(NotifyTopics.All), true, false, verified, NotifyEndpointKind.SourceManual, Guid.NewGuid(), null,
            null, null, null, false, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)) { OaDestination = dest };

    private static ContactRecipientRule.LineOpenness Ready(string? dest = "Ubot1")
        => new(true, true, dest, null, false);

    [Fact]
    public void LineGate_OA_บริษัทพร้อม_ใช้ได้_ทิศตรงข้าม_ไม่พร้อม_OA_เปลี่ยน_OA_กลาง_ใช้ไม่ได้()
    {
        Assert.True(ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineUser, NotifyEndpointKind.ScopeCompany), Platform, Ready()).Usable);
        var notReady = ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineUser, NotifyEndpointKind.ScopeCompany), Platform,
            new ContactRecipientRule.LineOpenness(true, false, "Ubot1", "ยังไม่ได้เปิดใช้ LINE OA ของบริษัท", false));
        Assert.Equal("company-oa-not-ready", notReady.Code);
        var changed = ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineUser, NotifyEndpointKind.ScopeCompany, dest: "Ubot1"), Platform, Ready("Ubot2"));
        Assert.Equal(ContactRecipientRule.RuleCodeOaChanged, changed.RuleCode);
        var central = ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineUser, NotifyEndpointKind.ScopeNextAcc), Platform, Ready());
        Assert.False(central.Usable);
        Assert.Equal(ContactRecipientRule.RuleCodeNextAccNotOpen, central.RuleCode);
        Assert.Equal("line-not-open", ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineUser, NotifyEndpointKind.ScopeCompany), Platform,
            ContactRecipientRule.LineOpenness.Closed).Code);
    }

    [Theory]
    [InlineData(AdminGroup)]
    [InlineData(ConfigGroup)]
    public void กลุ่มกลางของแอดมิน_ไม่เป็นปลายทางแม้ผ่าน_OA_บริษัทที่พร้อม(string groupId)
    {
        var st = ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineGroup, NotifyEndpointKind.ScopeCompany, groupId: groupId), Platform, Ready());
        Assert.False(st.Usable);
        Assert.Equal(ContactRecipientRule.RuleCodePlatformGroup, st.RuleCode);
        var own = ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineGroup, NotifyEndpointKind.ScopeCompany, groupId: "Ccustomer000000000000000000000009"), Platform, Ready());
        Assert.True(own.Usable);   // ทิศตรงข้าม: กลุ่มของผู้ติดต่อเองผ่าน OA บริษัทส่งได้
    }

    [Fact]
    public void LineRoute_กลุ่มผู้ติดต่อผ่าน_OA_กลาง_ปิดเป็นค่าตั้งต้น_เปิดได้ด้วยสวิตช์_กลุ่มแพลตฟอร์มปิดเสมอ()
    {
        var off = NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.NextAccOa, LineTargetKind.ContactGroup, false);
        Assert.False(off.Allowed);
        Assert.Equal(NotificationAudiencePolicy.RuleCentralOaGroupOff, off.RuleCode);
        Assert.True(NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.NextAccOa, LineTargetKind.ContactGroup, false,
            centralOaGroupDeliveryEnabled: true).Allowed);
        Assert.False(NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.NextAccOa, LineTargetKind.PlatformGroup, false,
            centralOaGroupDeliveryEnabled: true).Allowed);
        Assert.True(NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.CompanyOa, LineTargetKind.ContactGroup, true).Allowed);
        Assert.False(NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.CompanyOa, LineTargetKind.ContactGroup, false).Allowed);
    }

    // ───── การ์ด ─────

    /// <summary>JSON ที่อ่านไทยได้ (ค่าตั้งต้นของ System.Text.Json หนีอักษรไทยเป็น \uXXXX)</summary>
    private static string Json(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    });

    private static LineDocumentCard.Input Card(string lang = "th", decimal? amount = 12840m, string? pay = "https://app.nextacc.test/pay/x",
        bool group = false, bool viaNextAcc = false, string title = "ใบแจ้งหนี้")
        => new("บริษัท ร้านเรา จำกัด", viaNextAcc, lang, title, "INV-2026-11-0042", amount, "THB",
            new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc), "บริษัท เอบีซี จำกัด", pay, group);

    [Fact]
    public void การ์ด_ไทยแสดง_พศ_อังกฤษแสดง_คศ_ป้ายผู้ส่งและช่องทางยกเลิกมีเสมอ()
    {
        var th = Json(LineDocumentCard.Build(Card()).Contents);
        Assert.Contains("20/11/2569", th);
        Assert.Contains("จาก บริษัท ร้านเรา จำกัด", th);
        Assert.Contains("หยุด", th);
        Assert.Contains("12,840.00 THB", th);
        var en = Json(LineDocumentCard.Build(Card(lang: "en", title: "Invoice")).Contents);
        Assert.Contains("2026", en);
        Assert.DoesNotContain("2569", en);
        Assert.Contains("STOP", en);
        var central = LineDocumentCard.Build(Card(viaNextAcc: true));
        Assert.Contains("ในนาม บริษัท ร้านเรา จำกัด · ผ่าน NextAcc", central.AltText);
    }

    [Fact]
    public void การ์ด_ซ่อนยอดได้_ปุ่มเฉพาะ_https_altText_ไม่เกิน_400_ตัดอักขระควบคุม()
    {
        var hidden = LineDocumentCard.Build(Card(amount: null, group: true));
        var json = Json(hidden.Contents);
        Assert.DoesNotContain("12,840", json);
        Assert.DoesNotContain("12,840", hidden.AltText);
        Assert.Contains("ยกเลิกการผูกกลุ่ม", json);
        Assert.DoesNotContain("\"uri\"", Json(LineDocumentCard.Build(Card(pay: "http://insecure.test/pay")).Contents));
        Assert.Contains("https://app.nextacc.test/pay/x", Json(LineDocumentCard.Build(Card()).Contents));
        var longCard = LineDocumentCard.Build(Card(title: new string('ก', 1000) + "\u0007"));
        Assert.True(longCard.AltText.Length <= LineDocumentCard.MaxAltText);
        Assert.DoesNotContain("\\u0007", Json(longCard.Contents));
    }

    // ───── ลิงก์ผูก / หยุด ─────

    [Theory]
    [InlineData("หยุด", true)]
    [InlineData("  STOP ", true)]
    [InlineData("stop.", true)]
    [InlineData("unsubscribe", true)]
    [InlineData("หยุดก่อนนะ", false)]
    [InlineData("ยกเลิก", false)]
    [InlineData("ผูก LN-ABCDEFGH", false)]
    [InlineData("", false)]
    public void IsStop_ทั้งข้อความเท่านั้น(string text, bool expected) => Assert.Equal(expected, ContactLineBindRule.IsStop(text));

    [Fact]
    public void Token_รูปแบบ_และคำเชิญใช้ไม่ได้เมื่อหมดอายุ_ใช้แล้ว_ยกเลิก()
    {
        var t = ContactLineBindRule.NewToken();
        Assert.True(ContactLineBindRule.LooksLikeToken(t));
        Assert.False(ContactLineBindRule.LooksLikeToken("short"));
        Assert.False(ContactLineBindRule.LooksLikeToken(t + "<script>"));
        Assert.Equal(ContactLineBindRule.TokenHash(t), ContactLineBindRule.TokenHash(" " + t + " "));
        var now = DateTime.UtcNow;
        Assert.Null(ContactLineBindRule.InviteProblem(true, now.AddDays(1), null, null, now));
        Assert.NotNull(ContactLineBindRule.InviteProblem(true, now.AddDays(-1), null, null, now));
        Assert.NotNull(ContactLineBindRule.InviteProblem(true, now.AddDays(1), now, null, now));
        Assert.NotNull(ContactLineBindRule.InviteProblem(true, now.AddDays(1), null, now, now));
        Assert.NotNull(ContactLineBindRule.InviteProblem(false, now.AddDays(1), null, null, now));
        Assert.Contains("LY Corporation", ContactLineBindRule.ConsentText("ร้าน", "ABC"));
    }

    [Fact]
    public void ข้อความผ่าน_OA_กลาง_มีป้ายผู้ส่งและช่องทางยกเลิก()
    {
        var t = LineOnBehalfText.ForNextAcc("ร้านเรา", "การจองของคุณยืนยันแล้ว");
        Assert.StartsWith("ในนาม ร้านเรา · ผ่าน NextAcc", t);
        Assert.Contains("การจองของคุณยืนยันแล้ว", t);
        Assert.EndsWith(LineOnBehalfText.OptOutLine, t);
    }

    [Fact]
    public void CompanyLineReadiness_ต้องเปิด_token_และ_secret()
    {
        Assert.True(CompanyLineReadiness.Evaluate(true, true, true).Ready);
        Assert.False(CompanyLineReadiness.Evaluate(false, true, true).Ready);
        Assert.False(CompanyLineReadiness.Evaluate(true, false, true).Ready);
        Assert.Contains("Secret", CompanyLineReadiness.Evaluate(true, true, false).Problem);
    }

    // ───── LineNotifyService: X-Line-Retry-Key ทุก push · ด่านปลายทางผู้ติดต่อ (ไม่มี HTTP ออก) ─────

    private sealed class HeaderCapture : HttpMessageHandler
    {
        public List<string?> RetryKeys { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RetryKeys.Add(request.Headers.TryGetValues("X-Line-Retry-Key", out var v) ? v.FirstOrDefault() : null);
            var resp = new HttpResponseMessage(Status) { Content = new StringContent("{}") };
            resp.Headers.Add("x-line-request-id", "req-254");
            return Task.FromResult(resp);
        }
    }

    private static LineNotifyService Line(HeaderCapture h)
    {
        var factory = F4Proxy<IHttpClientFactory>.Create((m, a) => m == nameof(IHttpClientFactory.CreateClient) ? new HttpClient(h, disposeHandler: false) : null);
        var secrets = F4Proxy<ISecretProtector>.Create((m, a) => a.Length > 0 ? a[0] : null);
        var central = new NextAccLineSettings("CENTRAL-TOKEN", "S", "@nextacc", null, AdminGroup, NextAccLineConfigRule.SourceAdmin);
        var resolver = F4Proxy<INextAccLineConfig>.Create((m, a) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
            ? (object)NextAccLineConfigRule.PlatformGroupIds(AdminGroup, ConfigGroup)
            : central);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new LineNotifyService(factory, cfg, NullLogger<LineNotifyService>.Instance, null!, secrets, resolver);
    }

    [Fact]
    public async Task ทุก_push_ส่ง_X_Line_Retry_Key_และคืนสถานะ_HTTP_กับ_request_id()
    {
        var h = new HeaderCapture();
        var r = await Line(h).TryPushViaNextAccAsync("Ustaff1234567890", new[] { "สวัสดี" });
        Assert.True(r.Ok);
        Assert.Equal(200, r.HttpStatus);
        Assert.Equal("req-254", r.LineRequestId);
        var key = Assert.Single(h.RetryKeys);
        Assert.True(Guid.TryParse(key, out _));
    }

    [Theory]
    [InlineData(AdminGroup)]
    [InlineData(ConfigGroup)]
    public async Task ปลายทางผู้ติดต่อเป็นกลุ่มกลางของแอดมิน_ปฏิเสธเสมอ_ไม่มี_HTTP(string groupId)
    {
        var h = new HeaderCapture();
        var svc = Line(h);
        foreach (var scope in new[] { NotifyEndpointKind.ScopeCompany, NotifyEndpointKind.ScopeNextAcc })
        {
            var r = await svc.TryPushContactAsync(Co, scope, LineTargetKind.ContactGroup, groupId, "alt", new { type = "bubble" }, Guid.NewGuid());
            Assert.False(r.Ok);
            Assert.Equal(ContactRecipientRule.RuleCodePlatformGroup, r.RuleCode);
        }
        Assert.Empty(h.RetryKeys);
    }

    [Fact]
    public async Task ปลายทางผู้ติดต่อผ่าน_OA_กลาง_ปฏิเสธ_N8_และ_OA_บริษัทที่ไม่มี_ปฏิเสธไม่ถอยไป_OA_กลาง()
    {
        var h = new HeaderCapture();
        var svc = Line(h);
        var central = await svc.TryPushContactAsync(Co, NotifyEndpointKind.ScopeNextAcc, LineTargetKind.TenantMember, "Ucustomer00000001", "alt", new { type = "bubble" }, Guid.NewGuid());
        Assert.Equal(ContactRecipientRule.RuleCodeNextAccNotOpen, central.RuleCode);
        var company = await svc.TryPushContactAsync(Co, NotifyEndpointKind.ScopeCompany, LineTargetKind.TenantMember, "Ucustomer00000001", "alt", new { type = "bubble" }, Guid.NewGuid());
        Assert.Equal(NotificationAudiencePolicy.RuleTenantNoCompanyOa, company.RuleCode);
        Assert.Empty(h.RetryKeys);
    }
}
