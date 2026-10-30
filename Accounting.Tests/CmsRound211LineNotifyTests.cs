using System.Security.Cryptography;
using System.Text;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Cms;
using Accounting.Services.Interfaces;
using Accounting.Tests.Db;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 211 (คำตัดสินข้อ 170) — ลูกค้าปลายทางผูก LINE เองเพื่อรับแจ้งเตือน: รหัสผูก (<see cref="CustomerLineLinkRule"/>) ·
/// ช่องทาง/ลำดับ (<see cref="CustomerNotifyChannel"/>) · ลายเซ็น webhook ตัวเดียว (<see cref="LineWebhookSignature"/>) ·
/// ตัวส่ง <see cref="BookingNotifier"/> (LINE ที่ผูกเองก่อน · ไม่ผูก = อีเมลก่อนเหมือนเดิม) · ทุกกลุ่มล็อกทั้งทิศ "ใหม่" และ "เดิมไม่เปลี่ยน"
/// </summary>
public class CmsRound211LineNotifyTests
{
    // ═════════ 1. รหัสผูก: สุ่ม · รูปแบบ · hash · อายุ/ใช้ครั้งเดียว ═════════

    [Fact]
    public void NewCode_is_8_chars_from_alphabet_and_random()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 500; i++)
        {
            var c = CustomerLineLinkRule.NewCode();
            Assert.Equal(CustomerLineLinkRule.CodeLength, c.Length);
            Assert.All(c, ch => Assert.Contains(ch, CustomerLineLinkRule.Alphabet));
            seen.Add(c);
        }
        Assert.True(seen.Count >= 499);   // 32^8 — ชนกันใน 500 ครั้งแทบเป็นไปไม่ได้
        Assert.DoesNotContain('0', CustomerLineLinkRule.Alphabet);
        Assert.DoesNotContain('O', CustomerLineLinkRule.Alphabet);
        Assert.DoesNotContain('1', CustomerLineLinkRule.Alphabet);
        Assert.DoesNotContain('I', CustomerLineLinkRule.Alphabet);
    }

    [Fact]
    public void Message_round_trips_through_parser()
    {
        var code = CustomerLineLinkRule.NewCode();
        var msg = CustomerLineLinkRule.MessageText(code);
        Assert.StartsWith("ผูก LN-", msg);
        Assert.True(CustomerLineLinkRule.IsLinkAttempt(msg));
        Assert.Equal(code, CustomerLineLinkRule.TryParse(msg));
    }

    [Theory]
    [InlineData("ผูก LN-ABCD2345", "ABCD2345")]
    [InlineData("ผูก ln-abcd2345", "ABCD2345")]
    [InlineData("  ผูกLNABCD2345 ", "ABCD2345")]
    [InlineData("bind LN-XYZW9876", "XYZW9876")]
    [InlineData("link LN ABCD2345", "ABCD2345")]
    public void Parser_accepts_customer_codes(string text, string expected)
    {
        Assert.True(CustomerLineLinkRule.IsLinkAttempt(text));
        Assert.Equal(expected, CustomerLineLinkRule.TryParse(text));
    }

    [Theory]
    [InlineData("ผูก 123456")]          // รหัสผูกบัญชีผู้ใช้ ERP — ห้ามแตะ
    [InlineData("bind 654321")]
    [InlineData("สลิป 123456")]         // ผูกรับสลิปพนักงาน — ห้ามแตะ
    [InlineData("ผูกสลิป 123456")]
    [InlineData("ดูยอด")]
    [InlineData("")]
    public void Erp_and_payslip_commands_are_not_customer_link_attempts(string text)
    {
        Assert.False(CustomerLineLinkRule.IsLinkAttempt(text));
        Assert.Null(CustomerLineLinkRule.TryParse(text));
    }

    [Theory]
    [InlineData("ผูก LN-ABCD234")]      // 7 ตัว
    [InlineData("ผูก LN-ABCD23456")]    // 9 ตัว
    [InlineData("ผูก LN-ABCD2341")]     // มี 1 (นอก alphabet)
    [InlineData("ผูก LN-ABCDO345")]     // มี O
    [InlineData("ผูก LN-ABCD2345 ครับ")]
    public void Malformed_attempts_are_detected_but_not_parsed(string text)
    {
        Assert.True(CustomerLineLinkRule.IsLinkAttempt(text));   // ⇒ ตอบ "รูปแบบไม่ถูก" ไม่ตกไปเส้น ERP
        Assert.Null(CustomerLineLinkRule.TryParse(text));
    }

    [Fact]
    public void Hash_is_stable_case_insensitive_and_not_the_code()
    {
        var h = CustomerLineLinkRule.Hash("ABCD2345");
        Assert.Equal(64, h.Length);
        Assert.Equal(h, CustomerLineLinkRule.Hash("abcd2345"));
        Assert.Equal(h, CustomerLineLinkRule.Hash(" ABCD2345 "));
        Assert.NotEqual(h, CustomerLineLinkRule.Hash("ABCD2346"));
        Assert.DoesNotContain("ABCD2345", h, StringComparison.OrdinalIgnoreCase);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("LN-ABCD2345"))).ToLowerInvariant();
        Assert.Equal(expected, h);
    }

    [Fact]
    public void Redeemable_only_unused_and_unexpired()
    {
        var now = new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
        Assert.True(CustomerLineLinkRule.IsRedeemable(now.AddMinutes(1), null, now));
        Assert.False(CustomerLineLinkRule.IsRedeemable(now, null, now));               // หมดพอดี
        Assert.False(CustomerLineLinkRule.IsRedeemable(now.AddMinutes(-1), null, now));
        Assert.False(CustomerLineLinkRule.IsRedeemable(now.AddMinutes(10), now.AddMinutes(-2), now));   // ใช้แล้ว
        Assert.Equal(TimeSpan.FromMinutes(30), CustomerLineLinkRule.CodeLifetime);
    }

    // ═════════ 2. ลิงก์เพิ่มเพื่อน/ส่งรหัส ═════════

    [Theory]
    [InlineData("@myshop", "@myshop")]
    [InlineData("myshop", "@myshop")]
    [InlineData(" @abc.def-1 ", "@abc.def-1")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("@my shop", null)]
    [InlineData("https://line.me/R/ti/p/@x", null)]
    public void BasicId_normalisation(string? input, string? expected)
        => Assert.Equal(expected, CustomerLineLinkRule.NormalizeBasicId(input));

    [Fact]
    public void Urls_are_built_server_side_and_encoded()
    {
        Assert.Equal("https://line.me/R/ti/p/%40myshop", CustomerLineLinkRule.AddFriendUrl("myshop"));
        var send = CustomerLineLinkRule.SendCodeUrl("@myshop", "ABCD2345")!;
        Assert.StartsWith("https://line.me/R/oaMessage/%40myshop/?", send);
        Assert.Contains(Uri.EscapeDataString("ผูก LN-ABCD2345"), send);
        Assert.Null(CustomerLineLinkRule.AddFriendUrl(null));
        Assert.Null(CustomerLineLinkRule.SendCodeUrl("bad id!", "ABCD2345"));
    }

    [Fact]
    public void Mask_never_reveals_full_user_id()
    {
        var id = "U1234567890abcdef1234567890abcdef";
        var m = CustomerLineLinkRule.Mask(id);
        Assert.Equal("U123••••ef", m);
        Assert.DoesNotContain("567890", m);
        Assert.Equal("", CustomerLineLinkRule.Mask(null));
    }

    // ═════════ 3. ช่องทาง — ค่าเริ่มต้น = พฤติกรรม push เดิม (บริษัทเปิด+มี token ⇒ OA บริษัท · ไม่งั้น NextAcc) ═════════

    private static CustomerLineChannel Resolve(bool enabled, bool token, bool secret, string? basic,
        bool nToken = true, bool nSecret = true, string? nBasic = "@nextacc", string? cDest = null, string? nDest = null)
        => CustomerNotifyChannel.ResolveLine(enabled, token, secret, basic, cDest, nToken, nSecret, nBasic, nDest);

    [Fact]
    public void Default_company_uses_nextacc_oa()
    {
        var ch = Resolve(enabled: false, token: false, secret: false, basic: null);
        Assert.Equal(CustomerNotifyChannel.ScopeNextAcc, ch.Scope);
        Assert.True(ch.CanPush);
        Assert.True(ch.CanLink);
        Assert.Equal("@nextacc", ch.BasicId);
        Assert.Null(ch.Problem);
    }

    [Theory]
    // enabled, token ⇒ company?
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]   // เปิดแต่ไม่มี token ⇒ NextAcc (ตรงกับ LineNotifyService.ResolveConfigAsync)
    [InlineData(false, true, false)]   // มี token แต่ปิด ⇒ NextAcc
    [InlineData(false, false, false)]
    public void Scope_matrix_matches_push_resolution(bool enabled, bool token, bool company)
        => Assert.Equal(company ? CustomerNotifyChannel.ScopeCompany : CustomerNotifyChannel.ScopeNextAcc,
            Resolve(enabled, token, secret: true, basic: "@shop").Scope);

    [Fact]
    public void Company_oa_needs_secret_and_basic_id_to_link_but_still_pushes()
    {
        var noSecret = Resolve(true, true, secret: false, basic: "@shop");
        Assert.Equal(CustomerNotifyChannel.ScopeCompany, noSecret.Scope);
        Assert.True(noSecret.CanPush);
        Assert.False(noSecret.CanLink);
        Assert.Contains("Channel Secret", noSecret.Problem);
        var noBasic = Resolve(true, true, secret: true, basic: null);
        Assert.False(noBasic.CanLink);
        Assert.Contains("Basic ID", noBasic.Problem);
        Assert.True(Resolve(true, true, true, "@shop").CanLink);
    }

    [Fact]
    public void Nextacc_without_config_cannot_link_or_push()
    {
        var ch = Resolve(false, false, false, null, nToken: false, nSecret: false, nBasic: null);
        Assert.False(ch.CanPush);
        Assert.False(ch.CanLink);
        Assert.NotNull(ch.Problem);
        var noBasic = Resolve(false, false, false, null, nToken: true, nSecret: true, nBasic: null);
        Assert.True(noBasic.CanPush);
        Assert.False(noBasic.CanLink);
    }

    [Theory]
    [InlineData("nextacc", null, "nextacc", null, true)]
    [InlineData("nextacc", null, "company", null, false)]      // ร้านเปลี่ยนไปใช้ OA ตัวเอง ⇒ ลิงก์ของ OA กลางใช้ไม่ได้
    [InlineData("company", "Ubot1", "nextacc", null, false)]   // ร้านกลับมาใช้ OA กลาง
    [InlineData("company", "Ubot1", "company", "Ubot1", true)]
    [InlineData("company", "Ubot1", "company", "Ubot2", false)] // ร้านเปลี่ยน OA ของตัวเองเป็นตัวใหม่
    [InlineData("company", null, "company", "Ubot2", true)]     // ไม่รู้ปลายทางของลิงก์ ⇒ ตัดสินด้วย scope
    [InlineData("company", "Ubot1", "company", null, true)]     // ยังไม่ได้ webhook ใหม่ ⇒ ตัดสินด้วย scope
    public void Link_usable_matrix(string linkScope, string? linkDest, string curScope, string? curDest, bool usable)
        => Assert.Equal(usable, CustomerNotifyChannel.LinkUsable(linkScope, linkDest, curScope, curDest));

    [Fact]
    public void Order_keeps_email_first_without_opt_in()
    {
        Assert.Equal(new[] { "email", "line" }, CustomerNotifyChannel.Order(hasEmail: true, hasLine: true, lineOptedIn: false));
        Assert.Equal(new[] { "email" }, CustomerNotifyChannel.Order(true, false, false));
        Assert.Equal(new[] { "line" }, CustomerNotifyChannel.Order(false, true, false));
        Assert.Empty(CustomerNotifyChannel.Order(false, false, false));
    }

    [Fact]
    public void Order_puts_line_first_when_customer_opted_in()
    {
        Assert.Equal(new[] { "line", "email" }, CustomerNotifyChannel.Order(true, true, lineOptedIn: true));
        Assert.Equal(new[] { "line" }, CustomerNotifyChannel.Order(false, true, true));
    }

    [Fact]
    public void Email_plan_tenant_then_system_fallback_only_when_tested()
    {
        Assert.Equal(new[] { "tenant", "system" }, CustomerNotifyChannel.EmailPlan(true));
        Assert.Equal(new[] { "system" }, CustomerNotifyChannel.EmailPlan(false));   // เดิม
    }

    // ═════════ 4. ลายเซ็น webhook — สูตรเดียว ═════════

    [Fact]
    public void Signature_matches_independent_hmac_and_rejects_tampering()
    {
        const string secret = "s3cr3t";
        const string body = "{\"destination\":\"Ubot\",\"events\":[]}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
        Assert.Equal(expected, LineWebhookSignature.Compute(secret, body));
        Assert.True(LineWebhookSignature.Verify(secret, body, expected));
        Assert.False(LineWebhookSignature.Verify(secret, body + " ", expected));
        Assert.False(LineWebhookSignature.Verify("other", body, expected));
        Assert.False(LineWebhookSignature.Verify(null, body, expected));      // ไม่มี secret ⇒ ไม่ผ่านเสมอ
        Assert.False(LineWebhookSignature.Verify(secret, body, null));
        Assert.False(LineWebhookSignature.Verify(secret, body, ""));
    }

    // ═════════ 5. ปิดลิงก์ (ยกเลิก/ผูกใหม่/DSR ลบ) — เส้นเดียว · ล้าง LINE userId ═════════

    [Theory]
    [InlineData("user")]
    [InlineData("relinked")]
    [InlineData("erase")]
    public void Close_clears_line_user_and_closes_link_once(string reason)
    {
        var now = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        var l = new CustomerLineLink { LineUserId = "Uabc1234567", OaScope = "nextacc", TargetKind = "booking" };
        Assert.Equal(2, CustomerLineLinkRule.Close(l, reason, now));
        Assert.Equal("", l.LineUserId);
        Assert.Equal(now, l.UnlinkedAt);
        Assert.Equal(reason, l.UnlinkReason);
        Assert.Equal(0, CustomerLineLinkRule.Close(l, "erase", now.AddDays(1)));
        Assert.Equal(now, l.UnlinkedAt);   // ไม่ทับเวลา/เหตุเดิม
        Assert.Equal(reason, l.UnlinkReason);
    }

    [Fact]
    public void Close_clears_legacy_unlinked_row_that_still_holds_user_id()
    {
        var at = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var l = new CustomerLineLink { LineUserId = "Uold", UnlinkedAt = at, UnlinkReason = "user" };
        Assert.Equal(1, CustomerLineLinkRule.Close(l, "erase", at.AddDays(3)));
        Assert.Equal("", l.LineUserId);
        Assert.Equal(at, l.UnlinkedAt);
    }

    [Fact]
    public void Code_purge_cutoff_never_touches_live_codes()
    {
        var now = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        var cutoff = CustomerLineLinkRule.CodePurgeCutoff(now);
        Assert.Equal(now.AddDays(-7), cutoff);
        Assert.False(now.Add(CustomerLineLinkRule.CodeLifetime) < cutoff);   // รหัสที่เพิ่งออกไม่ถูกลบ
        Assert.True(now.AddDays(-8) < cutoff);                               // หมดอายุเกิน 7 วัน = ลบ
    }

    // ═════════ 6. ฝ่ายค้าน: ยินยอมไม่มีอีเมล/เบอร์ · ปลายทาง OA กลาง · log ═════════

    [Fact]
    public void Consent_subject_is_hashed_line_id()
    {
        var a = CustomerLineLinkRule.ConsentSubject("U1234567890abcdef1234567890abcdef");
        Assert.StartsWith("line:", a);
        Assert.Equal(5 + 32, a.Length);
        Assert.DoesNotContain("1234567890abcdef", a);
        Assert.Equal(a, CustomerLineLinkRule.ConsentSubject(" U1234567890abcdef1234567890abcdef "));
        Assert.NotEqual(a, CustomerLineLinkRule.ConsentSubject("U0000000000abcdef1234567890abcdef"));
    }

    [Theory]
    [InlineData(null, "Ubot", true)]       // ไม่ได้ตั้ง Line:BotUserId ⇒ ตัดสินด้วย scope (เดิม)
    [InlineData("", "Ubot", true)]
    [InlineData("Ubot", null, true)]       // webhook ไม่มี destination
    [InlineData("Ubot", "Ubot", true)]
    [InlineData("Ubot", "Uother", false)]  // ตั้งไว้แล้วไม่ตรง ⇒ ปฏิเสธ (ค่าตั้งผิด)
    public void Destination_check_against_configured_bot(string? configured, string? received, bool ok)
        => Assert.Equal(ok, CustomerNotifyChannel.AcceptDestination(configured, received));

    [Fact]
    public void Redact_for_log_masks_line_ids_and_truncates()
    {
        var id = "U" + new string('a', 32);
        var r = CustomerLineLinkRule.RedactForLog($"{{\"message\":\"bad to {id}\"}}");
        Assert.DoesNotContain(id, r);
        Assert.Contains("Uaaa••••aa", r);
        Assert.Equal("", CustomerLineLinkRule.RedactForLog(null));
        Assert.True(CustomerLineLinkRule.RedactForLog(new string('x', 2000)).Length <= 501);
    }

    [Fact]
    public void Failure_note_distinguishes_fallback_from_failure()
    {
        var at = new DateTime(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc);
        var warn = CustomerNotifyChannel.TenantEmailFallbackWarning("535 auth");
        Assert.StartsWith(CmsNotificationText.FallbackTag, warn);
        var fb = CmsNotificationText.FailureNote("ลูกค้า", "ยืนยันการจอง", warn, at);
        Assert.StartsWith(CmsNotificationText.FallbackTag, fb);
        Assert.DoesNotContain(CmsNotificationText.FailureTag, fb);
        Assert.Contains("535 auth", fb);
        var fail = CmsNotificationText.FailureNote("ลูกค้า", "ยืนยันการจอง", "อีเมล: ส่งไม่สำเร็จ", at);
        Assert.StartsWith(CmsNotificationText.FailureTag, fail);   // เดิม
    }

    // ═════════ 6. ตัวส่ง — LINE ที่ผูกเองก่อน · ไม่ผูก = เดิม ═════════

    private sealed class Calls { public List<string> Order { get; } = new(); }

    private static BookingNotifier Notifier(Calls calls, string? optedLine, bool emailOk, bool lineOk)
    {
        var email = F4Proxy<IEmailService>.Create((m, a) =>
        {
            if (m == nameof(IEmailService.TrySendAsync)) { calls.Order.Add("email:" + a[0]); return emailOk; }
            return null;
        });
        var line = F4Proxy<ILineNotifyService>.Create((m, a) =>
        {
            if (m == nameof(ILineNotifyService.TryPushToUserAsync)) { calls.Order.Add("line:" + a[1]); return lineOk; }
            return null;
        });
        var links = F4Proxy<ICustomerLineLinkService>.Create((m, a) =>
            m == nameof(ICustomerLineLinkService.ResolveLineUserIdAsync) ? optedLine : null);
        return new BookingNotifier(NullLogger<BookingNotifier>.Instance, email, line, links);
    }

    [Fact]
    public async Task No_link_keeps_email_first_like_before()
    {
        var calls = new Calls();
        var n = Notifier(calls, optedLine: null, emailOk: true, lineOk: true);
        var r = await n.SendAsync(Guid.NewGuid(), new BookingContact("a@b.co", null) { LineTarget = CustomerLineTarget.ForBooking(Guid.NewGuid(), null) }, "s", "t");
        Assert.True(r.Sent);
        Assert.Equal("email", r.Channel);
        Assert.Equal(new[] { "email:a@b.co" }, calls.Order);
    }

    [Fact]
    public async Task Opted_in_line_goes_first_then_email_on_failure()
    {
        var calls = new Calls();
        var ok = await Notifier(calls, "Ulinked", emailOk: true, lineOk: true)
            .SendAsync(Guid.NewGuid(), new BookingContact("a@b.co", null) { LineTarget = CustomerLineTarget.ForOrder(Guid.NewGuid(), null) }, "s", "t");
        Assert.Equal("line", ok.Channel);
        Assert.Equal(new[] { "line:Ulinked" }, calls.Order);

        var calls2 = new Calls();
        var fb = await Notifier(calls2, "Ulinked", emailOk: true, lineOk: false)
            .SendAsync(Guid.NewGuid(), new BookingContact("a@b.co", null) { LineTarget = CustomerLineTarget.ForOrder(Guid.NewGuid(), null) }, "s", "t");
        Assert.True(fb.Sent);
        Assert.Equal("email", fb.Channel);
        Assert.Equal(new[] { "line:Ulinked", "email:a@b.co" }, calls2.Order);
    }

    [Fact]
    public async Task Linked_line_without_email_is_enough_and_failure_is_reported()
    {
        var calls = new Calls();
        var r = await Notifier(calls, "Ulinked", emailOk: false, lineOk: false)
            .SendAsync(Guid.NewGuid(), new BookingContact(null, null) { LineTarget = CustomerLineTarget.ForBooking(Guid.NewGuid(), null) }, "s", "t");
        Assert.False(r.Sent);
        Assert.Contains("LINE", r.Error);
        var none = await Notifier(new Calls(), null, true, true)
            .SendAsync(Guid.NewGuid(), new BookingContact(null, null) { LineTarget = CustomerLineTarget.ForBooking(Guid.NewGuid(), null) }, "s", "t");
        Assert.False(none.Sent);
        Assert.Contains("ไม่มีช่องทาง", none.Error);
    }

    private sealed class TenantNotifier : BookingNotifier
    {
        private readonly CompanySettings? _s;
        public TenantNotifier(CompanySettings? s, IEmailService email, IEmailSenderFactory senders)
            : base(NullLogger<BookingNotifier>.Instance, email, null, null, senders) => _s = s;
        protected override Task<CompanySettings?> LoadEmailSettingsAsync(Guid companyId, CancellationToken ct) => Task.FromResult(_s);
    }

    private static (TenantNotifier N, Calls C) Tenant(bool configured, bool tenantOk, bool systemOk)
    {
        var calls = new Calls();
        var settings = new CompanySettings { EmailConfigured = configured, EmailSmtpHost = "smtp.shop.test", EmailFromAddress = "shop@shop.test" };
        var sender = F4Proxy<IEmailSender>.Create((m, a) =>
        {
            if (m != nameof(IEmailSender.SendAsync)) return null;
            calls.Order.Add("tenant:" + ((EmailMessage)a[0]!).FromAddress);
            return tenantOk ? EmailSendResult.Ok("id") : EmailSendResult.Fail("535 auth");
        });
        var factory = F4Proxy<IEmailSenderFactory>.Create((m, a) => m == nameof(IEmailSenderFactory.GetSenderForSettings) ? sender : null);
        var system = F4Proxy<IEmailService>.Create((m, a) =>
        {
            if (m != nameof(IEmailService.TrySendAsync)) return null;
            calls.Order.Add("system");
            return systemOk;
        });
        return (new TenantNotifier(settings, system, factory), calls);
    }

    [Fact]
    public async Task Tenant_email_ok_uses_tenant_only()
    {
        var (n, c) = Tenant(configured: true, tenantOk: true, systemOk: true);
        var r = await n.SendAsync(Guid.NewGuid(), new BookingContact("a@b.co", null), "s", "t");
        Assert.True(r.Sent);
        Assert.Null(r.Warning);
        Assert.Equal(new[] { "tenant:shop@shop.test" }, c.Order);
    }

    [Fact]
    public async Task Tenant_email_failure_falls_back_to_system_and_warns()
    {
        var (n, c) = Tenant(configured: true, tenantOk: false, systemOk: true);
        var r = await n.SendAsync(Guid.NewGuid(), new BookingContact("a@b.co", null), "s", "t");
        Assert.True(r.Sent);                                   // ลูกค้าได้รับ (ไม่ถดถอย)
        Assert.Equal(new[] { "tenant:shop@shop.test", "system" }, c.Order);
        Assert.NotNull(r.Warning);
        Assert.StartsWith(CmsNotificationText.FallbackTag, r.Warning);
        Assert.Contains("535 auth", r.Warning);
    }

    [Fact]
    public async Task Tenant_and_system_both_fail_reports_both()
    {
        var (n, _) = Tenant(configured: true, tenantOk: false, systemOk: false);
        var r = await n.SendAsync(Guid.NewGuid(), new BookingContact("a@b.co", null), "s", "t");
        Assert.False(r.Sent);
        Assert.Contains("535 auth", r.Error);
        Assert.Contains("SMTP", r.Error);
    }

    [Fact]
    public async Task Tenant_not_configured_uses_system_like_before()
    {
        var (n, c) = Tenant(configured: false, tenantOk: true, systemOk: true);
        var r = await n.SendAsync(Guid.NewGuid(), new BookingContact("a@b.co", null), "s", "t");
        Assert.True(r.Sent);
        Assert.Null(r.Warning);
        Assert.Equal(new[] { "system" }, c.Order);
    }

    [Fact]
    public void Contact_defaults_unchanged()
    {
        var c = new BookingContact("a@b.co", null);
        Assert.Null(c.LineTarget);
        Assert.True(c.HasChannel);
        Assert.False(new BookingContact(null, null) { LineTarget = CustomerLineTarget.ForContact(Guid.NewGuid()) }.HasChannel);
        var t = CustomerLineTarget.ForBooking(Guid.Empty, null);
        Assert.Equal(CustomerLineLinkRule.KindBooking, t.RecordKind);
        Assert.Equal(CustomerLineLinkRule.KindOrder, CustomerLineTarget.ForOrder(Guid.Empty, null).RecordKind);
    }
}
