using System.Net;
using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using EntDocument = Accounting.Models.Entities.Document;

namespace Accounting.Tests;

/// <summary>
/// รอบ 240 — ลิงก์ให้ลูกค้าเซ็น: webhook ต่อเอกสาร (กติกา URL · SSRF · ลายเซ็น HMAC canonical · outbox) · อีเมลขอเซ็นในนามบริษัท
/// (ตัวตัดสินผู้ส่ง · ไม่ปลอมโดเมน · Reply-To · ข้อความเปิดเผย · HtmlEncode) · เพดาน/กันส่งถี่ · ชนิด/สถานะ/ป้ายของลิงก์
/// (คำตัดสิน 240-1..240-8 · ทุกด่านมีเทสต์ทิศตรงข้าม)
/// </summary>
public class SignRequestRound240Tests
{
    // ───────────── ตัวตัดสินผู้ส่ง (OnBehalfEmailSender) ─────────────

    private static OnBehalfEmailSender.Input In(bool verified, string? from = "billing@acme.co.th", string? replyTo = null,
        string? contact = "info@acme.co.th", string? platform = "no-reply@nextacc.net", string? name = "บริษัท แอคมี จำกัด")
        => new(verified, from, "ฝ่ายบัญชี Acme", replyTo, contact, name, platform);

    [Fact]
    public void บริษัทตั้งและทดสอบผ่าน_ส่งจากที่อยู่ของบริษัท()
    {
        var p = OnBehalfEmailSender.Resolve(In(true, replyTo: "ar@acme.co.th"));
        Assert.Equal(OnBehalfEmailSender.ModeCompany, p.Mode);
        Assert.False(p.OnBehalf);
        Assert.Equal("billing@acme.co.th", p.FromAddress);
        Assert.Equal("ฝ่ายบัญชี Acme", p.FromName);
        Assert.Equal("ar@acme.co.th", p.ReplyTo);
    }

    [Fact]
    public void ยังไม่ได้ตั้งหรือไม่ผ่าน_ส่งผ่านแพลตฟอร์ม_From_ไม่ใช่โดเมนบริษัทเด็ดขาด_ReplyTo_ถึงบริษัท()
    {
        var p = OnBehalfEmailSender.Resolve(In(false));
        Assert.Equal(OnBehalfEmailSender.ModePlatform, p.Mode);
        Assert.True(p.OnBehalf);
        Assert.Equal("no-reply@nextacc.net", p.FromAddress);
        Assert.False(p.FromAddress.EndsWith("@acme.co.th", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("บริษัท แอคมี จำกัด ผ่าน NextAcc", p.FromName);
        Assert.Equal("billing@acme.co.th", p.ReplyTo);   // ลำดับ: ReplyTo → From ของบริษัท → อีเมลติดต่อ
        // ไม่มีที่อยู่ใดของบริษัท ⇒ ไม่มี Reply-To (ไม่แต่งขึ้นเอง)
        Assert.Null(OnBehalfEmailSender.Resolve(In(false, from: null, contact: null)).ReplyTo);
        // ตั้งไว้แต่ "ตรวจไม่ผ่าน" (verified=false) ⇒ ยังเป็นแพลตฟอร์ม — ทิศตรงข้ามของเทสต์ข้างบน
        Assert.Equal(OnBehalfEmailSender.ModePlatform, OnBehalfEmailSender.Resolve(In(false, replyTo: "ar@acme.co.th")).Mode);
    }

    [Fact]
    public void ที่อยู่แพลตฟอร์มที่ตั้งผิดเป็นโดเมนบริษัท_หรือว่าง_ถอยไปค่าเริ่มต้นของ_NextAcc()
    {
        Assert.Equal(OnBehalfEmailSender.DefaultPlatformFrom, OnBehalfEmailSender.Resolve(In(false, platform: "noreply@acme.co.th")).FromAddress);
        Assert.Equal(OnBehalfEmailSender.DefaultPlatformFrom, OnBehalfEmailSender.Resolve(In(false, platform: null)).FromAddress);
        Assert.Equal(OnBehalfEmailSender.DefaultPlatformFrom, OnBehalfEmailSender.Resolve(In(false, platform: "not an email")).FromAddress);
        Assert.Equal("mailer@nextacc.net", OnBehalfEmailSender.Resolve(In(false, platform: "mailer@nextacc.net")).FromAddress);
    }

    [Fact]
    public void ชื่อผู้ส่งตัดอักขระที่ใช้ปลอม_header_หรือหน้าตาเป็นอีเมลคนอื่น()
    {
        var p = OnBehalfEmailSender.Resolve(In(false, name: "Evil\r\nBcc: x@y.com <ceo@bank.com>"));
        Assert.DoesNotContain("\r", p.FromName);
        Assert.DoesNotContain("\n", p.FromName);
        Assert.DoesNotContain("@", p.FromName);
        Assert.DoesNotContain("<", p.FromName);
        Assert.Equal("ผู้ขาย", OnBehalfEmailSender.SafeDisplayName("  "));
        Assert.True(OnBehalfEmailSender.SafeDisplayName(new string('ก', 200)).Length <= OnBehalfEmailSender.DisplayNameMax);
        Assert.False(OnBehalfEmailSender.IsPlausibleEmail("a@b.com, c@d.com"));
        Assert.False(OnBehalfEmailSender.IsPlausibleEmail("a@b\r\n.com"));
        Assert.True(OnBehalfEmailSender.IsPlausibleEmail("a.b+c@sub.example.co.th"));
    }

    // ───────────── URL callback + SSRF ─────────────

    [Theory]
    [InlineData("https://erp.example.com/hooks/sign", true)]
    [InlineData("https://erp.example.com:8443/hooks", true)]
    [InlineData("http://erp.example.com/hooks", false)]
    [InlineData("https://10.0.0.5/hooks", false)]
    [InlineData("https://127.0.0.1/hooks", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://[::1]/hooks", false)]
    [InlineData("https://[::ffff:127.0.0.1]/hooks", false)]
    [InlineData("https://[fc00::1]/hooks", false)]
    [InlineData("https://user:pass@erp.example.com/hooks", false)]
    [InlineData("https://erp.example.com:22/hooks", false)]
    [InlineData("ftp://erp.example.com/x", false)]
    public void กติกา_callbackUrl(string url, bool ok)
        => Assert.Equal(ok, SignCallbackUrlRule.Problem(url, null) == null);

    [Fact]
    public void callbackUrl_ว่าง_คือไม่มี_และยาวเกินถูกปฏิเสธ()
    {
        Assert.Null(SignCallbackUrlRule.Problem(null, null));
        Assert.Null(SignCallbackUrlRule.Problem("  ", null));
        Assert.NotNull(SignCallbackUrlRule.Problem("https://erp.example.com/" + new string('a', 600), null));
    }

    [Fact]
    public void รายชื่อโฮสต์ที่อนุญาต_ตรงตัวและโดเมนย่อย_ไม่นับโดเมนที่ลงท้ายคล้ายกัน()
    {
        const string hosts = "erp.example.com, *.partner.co.th";
        Assert.Null(SignCallbackUrlRule.Problem("https://erp.example.com/h", hosts));
        Assert.Null(SignCallbackUrlRule.Problem("https://a.partner.co.th/h", hosts));
        Assert.NotNull(SignCallbackUrlRule.Problem("https://partner.co.th/h", hosts));          // ตัว apex ไม่อยู่ใน *.
        Assert.NotNull(SignCallbackUrlRule.Problem("https://evilpartner.co.th/h", hosts));      // ลงท้ายคล้าย
        Assert.NotNull(SignCallbackUrlRule.Problem("https://other.example.com/h", hosts));
        Assert.Null(SignCallbackUrlRule.Problem("https://other.example.com/h", ""));             // ว่าง = ทุกโฮสต์สาธารณะ
        Assert.NotNull(SignCallbackUrlRule.HostsProblem("https://x.com/path"));
        Assert.NotNull(SignCallbackUrlRule.HostsProblem("localhost"));
        Assert.Null(SignCallbackUrlRule.HostsProblem("erp.example.com,*.example.org"));
        Assert.Equal("erp.example.com,*.example.org", SignCallbackUrlRule.NormalizeHosts(" ERP.example.com ; *.example.org, erp.example.com"));
    }

    [Theory]
    [InlineData("169.254.169.254", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2001:4860:4860::8888", true)]
    // ฝ่ายค้าน N1: ช่วง IPv6 ที่ห่อ/แปล IPv4 ⇒ ไม่อนุญาต
    [InlineData("64:ff9b::7f00:1", false)]
    [InlineData("2002:7f00:1::1", false)]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", false)]
    [InlineData("::7f00:1", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    public void ตาราง_IP_สาธารณะ_ของด่าน_SSRF(string ip, bool isPublic)
        => Assert.Equal(isPublic, OutboundUrlGuard.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("169.254.169.254")]
    [InlineData("[::1]")]
    public async Task ตัวต่อจริงปฏิเสธปลายทางภายใน_ก่อนเปิด_socket(string host)
        => await Assert.ThrowsAsync<HttpRequestException>(() => OutboundUrlGuard.ResolvePublicOnlyAsync(host, CancellationToken.None));

    [Fact]
    public async Task ตัวต่อจริงรับ_IP_สาธารณะตรง_และ_handler_ตั้งค่าครบ()
    {
        Assert.Single(await OutboundUrlGuard.ResolvePublicOnlyAsync("8.8.8.8", CancellationToken.None));
        using var h = OutboundUrlGuard.CreatePublicOnlyHandler();
        Assert.False(h.UseProxy);
        Assert.False(h.AllowAutoRedirect);
        Assert.NotNull(h.ConnectCallback);
        Assert.True(h.ConnectTimeout <= TimeSpan.FromSeconds(10));
    }

    // ───────────── ลายเซ็น HMAC canonical ─────────────

    [Fact]
    public void ลายเซ็น_ไปกลับผ่าน_และทุกการดัดแปลงไม่ผ่าน()
    {
        const string secret = "whsec_test";
        const string body = "{\"eventId\":\"x\",\"type\":\"quotation.accepted\"}";
        var now = 1_800_000_000L;
        var header = WebhookSignature.Sign(secret, now, body);
        Assert.StartsWith($"t={now},v1=", header);
        Assert.True(WebhookSignature.Verify(secret, header, body, now));
        Assert.True(WebhookSignature.Verify(secret, header, body, now + 299));
        Assert.False(WebhookSignature.Verify(secret, header, body + " ", now));                 // body ถูกแก้
        Assert.False(WebhookSignature.Verify("whsec_other", header, body, now));              // รหัสลับผิด
        Assert.False(WebhookSignature.Verify(secret, header, body, now + 301));               // ยิงซ้ำของเก่า
        Assert.False(WebhookSignature.Verify(secret, header.Replace($"t={now}", $"t={now + 5}"), body, now)); // เปลี่ยนเวลาโดยไม่เซ็นใหม่
        Assert.False(WebhookSignature.Verify(secret, null, body, now));
        Assert.False(WebhookSignature.Verify(secret, "garbage", body, now));
        // ช่วงเปลี่ยนรหัส: มี v1 สองตัว ตัวใดตรงก็ผ่าน
        var both = header + ",v1=" + new string('0', 64);
        Assert.True(WebhookSignature.Verify(secret, both, body, now));
        // หัวเดิม (body ล้วน) = HMAC เดียวกันของ body
        using (var h = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret)))
            Assert.Equal("sha256=" + Convert.ToHexString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant(),
                WebhookSignature.LegacySign(secret, body));
        Assert.StartsWith("whsec_", WebhookSignature.NewSecret());
        Assert.NotEqual(WebhookSignature.NewSecret(), WebhookSignature.NewSecret());
    }

    // ───────────── outbox: กุญแจกันซ้ำ · payload · ถอยเวลา ─────────────

    [Fact]
    public void กุญแจกันซ้ำ_ต่อชนิด_เอกสาร_ลิงก์()
    {
        var doc = Guid.NewGuid();
        var key = SignEventOutbox.EventKey("quotation.accepted", doc, "abc");
        Assert.Equal(key, SignEventOutbox.EventKey("quotation.accepted", doc, "abc"));
        Assert.NotEqual(key, SignEventOutbox.EventKey("quotation.accepted", doc, "def"));   // ลิงก์ใหม่ = เหตุการณ์ใหม่
        Assert.NotEqual(key, SignEventOutbox.EventKey("delivery.signed", doc, "abc"));
        Assert.EndsWith(":-", SignEventOutbox.EventKey("quotation.accepted", doc, null));
    }

    [Fact]
    public void payload_มีเฉพาะที่จำเป็น_ไม่มีรูปลายเซ็น_IP_token_และเวลาเป็น_0700()
    {
        var json = SignEventOutbox.BuildPayload(new SignEventOutbox.PayloadInput(Guid.NewGuid(), "quotation.accepted",
            new DateTime(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc), Guid.NewGuid(), Guid.NewGuid(), "QT-001", "Quotation", 2,
            "สมชาย", true, "hash", "ERP-1"));
        using var d = JsonDocument.Parse(json);
        var root = d.RootElement;
        Assert.Equal("2026-11-05T10:00:00+07:00", root.GetProperty("occurredAt").GetString());
        Assert.Equal("ERP-1", root.GetProperty("data").GetProperty("callbackRef").GetString());
        Assert.True(root.GetProperty("data").GetProperty("hasSignature").GetBoolean());
        Assert.StartsWith("/api/v1/documents/", root.GetProperty("links").GetProperty("pdf").GetString());
        foreach (var banned in new[] { "signature\"", "ip", "userAgent", "token", "base64" })
            Assert.DoesNotContain("\"" + banned, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ถอยเวลาแบบยกกำลัง_และเลิกลองเมื่อครบ_8_ครั้ง()
    {
        var now = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal((SignEventStatus.Delivered, now), SignEventOutbox.AfterAttempt(true, 3, now));
        Assert.Equal(SignEventStatus.Pending, SignEventOutbox.AfterAttempt(false, 1, now).Status);
        Assert.Equal(now.AddMinutes(1), SignEventOutbox.AfterAttempt(false, 1, now).NextAttemptAt);
        Assert.Equal(now.AddHours(12), SignEventOutbox.AfterAttempt(false, 7, now).NextAttemptAt);
        Assert.Equal(SignEventStatus.Dead, SignEventOutbox.AfterAttempt(false, SignEventOutbox.MaxAttempts, now).Status);
        for (var i = 1; i < SignEventOutbox.MaxAttempts - 1; i++)
            Assert.True(SignEventOutbox.AfterAttempt(false, i + 1, now).NextAttemptAt > SignEventOutbox.AfterAttempt(false, i, now).NextAttemptAt);
    }

    // ───────────── เพดาน / กันส่งถี่ ─────────────

    [Fact]
    public void เพดานรายวันใช้เฉพาะอีเมลของแพลตฟอร์ม_ตัวกันผู้รับใช้ทุกโหมด()
    {
        var cap = SignEmailThrottle.DefaultPlatformDailyCap;
        Assert.NotNull(SignEmailThrottle.Refusal(true, cap, cap, 0, 0));
        Assert.Null(SignEmailThrottle.Refusal(true, cap - 1, cap, 0, 0));
        Assert.Null(SignEmailThrottle.Refusal(false, cap * 5, cap, 0, 0));                  // ผู้ส่งของบริษัท = ไม่ติดเพดานแพลตฟอร์ม
        Assert.NotNull(SignEmailThrottle.Refusal(false, 0, cap, SignEmailThrottle.PerRecipientPerDocPerDay, 0));
        Assert.NotNull(SignEmailThrottle.Refusal(false, 0, cap, 0, SignEmailThrottle.PerRecipientPerDay));
        Assert.Null(SignEmailThrottle.Refusal(false, 0, cap, SignEmailThrottle.PerRecipientPerDocPerDay - 1, SignEmailThrottle.PerRecipientPerDay - 1));
        Assert.Equal(50, SignEmailThrottle.EffectiveCap("50"));
        Assert.Equal(cap, SignEmailThrottle.EffectiveCap("0"));
        Assert.Equal(cap, SignEmailThrottle.EffectiveCap(null));
    }

    [Fact]
    public void แยกผู้รับ_ตัดซ้ำ_แยกที่ใช้ไม่ได้_และต้นวันตามเวลาไทย()
    {
        var (valid, invalid) = SignEmailThrottle.ParseRecipients(new[] { "A@x.com; b@y.co.th", "a@x.com", "bad", null });
        Assert.Equal(new[] { "a@x.com", "b@y.co.th" }, valid);
        Assert.Equal(new[] { "bad" }, invalid);
        // 23:30 UTC = 06:30 ของวันถัดไปที่ไทย ⇒ ต้นวัน = 17:00 UTC ของวันนั้น
        Assert.Equal(new DateTime(2026, 11, 5, 17, 0, 0, DateTimeKind.Utc),
            SignEmailThrottle.BangkokDayStartUtc(new DateTime(2026, 11, 5, 23, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 11, 4, 17, 0, 0, DateTimeKind.Utc),
            SignEmailThrottle.BangkokDayStartUtc(new DateTime(2026, 11, 5, 16, 59, 0, DateTimeKind.Utc)));
    }

    // ───────────── ชนิด/สถานะ/ป้าย ─────────────

    [Fact]
    public void ส่งได้เฉพาะชนิดที่เซ็นได้และออกเลขจริงแล้ว()
    {
        Assert.Null(SignRequestKinds.IssueRefusal(DocumentType.Quotation, DocumentStatus.Approved));
        Assert.Null(SignRequestKinds.IssueRefusal(DocumentType.DeliveryNote, DocumentStatus.Sent));
        Assert.NotNull(SignRequestKinds.IssueRefusal(DocumentType.Quotation, DocumentStatus.Draft));
        Assert.NotNull(SignRequestKinds.IssueRefusal(DocumentType.Quotation, DocumentStatus.WaitingApproval));   // เดิมหลุด (เลข DRAFT)
        Assert.NotNull(SignRequestKinds.IssueRefusal(DocumentType.Quotation, DocumentStatus.Voided));
        Assert.NotNull(SignRequestKinds.IssueRefusal(DocumentType.Invoice, DocumentStatus.Approved));
        Assert.Equal("quotation.accepted", SignRequestKinds.For(DocumentType.Quotation)!.EventType);
        Assert.Equal("delivery.signed", SignRequestKinds.For(DocumentType.DeliveryNote)!.EventType);
        Assert.Null(SignRequestKinds.For(DocumentType.PurchaseOrder));
    }

    [Fact]
    public void ป้ายสถานะ_ออก_ส่ง_เปิด_เซ็น_หมดอายุ_และออกใหม่ล้างการติดตาม()
    {
        var now = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        var d = new EntDocument { DocumentType = DocumentType.Quotation, Status = DocumentStatus.Approved };
        Assert.Null(SignRequestKinds.Status(d, now));
        SignRequestKinds.SetNewToken(d, "aa", now);
        Assert.Equal("issued", SignRequestKinds.Status(d, now)!.Code);
        Assert.Equal(now.AddDays(30), d.QuotationAcceptTokenExpiresAt);
        d.SignLinkEmailedAt = now;
        Assert.Equal("emailed", SignRequestKinds.Status(d, now)!.Code);
        d.SignLinkOpenedAt = now;
        Assert.Equal("opened", SignRequestKinds.Status(d, now)!.Code);
        Assert.Equal("expired", SignRequestKinds.Status(d, now.AddDays(31))!.Code);
        SignRequestKinds.SetNewToken(d, "bb", now);
        Assert.Null(d.SignLinkOpenedAt);
        Assert.Null(d.SignLinkEmailedAt);
        Assert.Equal("issued", SignRequestKinds.Status(d, now)!.Code);
        d.QuotationAcceptedAt = now;
        Assert.Equal("signed", SignRequestKinds.Status(d, now.AddDays(90))!.Code);   // เซ็นแล้วชนะหมดอายุ

        var dn = new EntDocument { DocumentType = DocumentType.DeliveryNote };
        SignRequestKinds.SetNewToken(dn, "cc", now);
        Assert.Equal(now.AddDays(14), dn.DeliverySignTokenExpiresAt);
        Assert.Null(dn.QuotationAcceptToken);
    }

    // ───────────── ข้อความอีเมล ─────────────

    private static SignRequestEmailText.Input Mail(bool onBehalf, string company = "บริษัท ก <script>alert(1)</script>", string? message = "<img src=x onerror=alert(1)>",
        bool english = false)
        => new(SignRequestKinds.QuotationAccept, english, "ใบเสนอราคา", "QT-<b>1</b>", new DateTime(2026, 11, 5), 1070m, "THB",
            "ลูกค้า \"x\"", company, "0105556000007", "ar@acme.co.th", "https://app.nextacc.net/pages/quotation-accept.html?token=ab'cd",
            new DateTime(2026, 12, 5, 0, 0, 0, DateTimeKind.Utc), message, Reminder: false, onBehalf, "https://app.nextacc.net/contact.html?topic=email-abuse");

    [Fact]
    public void อีเมลผ่านแพลตฟอร์มมีบรรทัดเปิดเผยตัวตน_ผู้ให้บริการ_ลิงก์แจ้งใช้ผิด_ของบริษัทเองไม่มี()
    {
        var p = SignRequestEmailText.Compose(Mail(onBehalf: true));
        Assert.Contains("อีเมลนี้ส่งในนาม", p.Html);
        Assert.Contains("0105556000007", p.Html);
        Assert.Contains("ผ่านระบบ NextAcc", p.Html);
        Assert.Contains("ผู้ให้บริการระบบ", p.Html);
        Assert.Contains("email-abuse", p.Html);
        Assert.Contains("ไม่ใช่โฆษณา", p.Html);

        var c = SignRequestEmailText.Compose(Mail(onBehalf: false));
        Assert.DoesNotContain("อีเมลนี้ส่งในนาม", c.Html);
        Assert.DoesNotContain("email-abuse", c.Html);
        Assert.Contains("ไม่ใช่โฆษณา", c.Html);   // ท้ายอีเมลบอกเหตุที่ได้รับเสมอ
    }

    [Fact]
    public void ทุกค่าที่ผู้ใช้คุมได้ถูก_HtmlEncode_และหัวเรื่องไม่มีขึ้นบรรทัด()
    {
        var p = SignRequestEmailText.Compose(Mail(onBehalf: true, company: "Evil\r\nBcc: x@y.com"));
        Assert.DoesNotContain("<script>", p.Html);
        Assert.DoesNotContain("<img src=x", p.Html);
        Assert.DoesNotContain("<b>1</b>", p.Html);
        Assert.DoesNotContain("token=ab'cd", p.Html);   // ' ถูกหนีใน attribute
        Assert.DoesNotContain("\n", p.Subject);
        Assert.DoesNotContain("\r", p.Subject);
        var s = SignRequestEmailText.Compose(Mail(onBehalf: true));
        Assert.Contains("&lt;script&gt;", s.Html);
    }

    [Fact]
    public void ภาษาอังกฤษตามหัวเอกสาร_และอีเมลเตือนมีคำนำหน้า()
    {
        var en = SignRequestEmailText.Compose(Mail(onBehalf: true, english: true) with { DocumentTitle = "Quotation" });
        Assert.StartsWith("Please accept the quotation", en.Subject);
        Assert.Contains("on behalf of", en.Html);
        Assert.DoesNotMatch(@"[฀-๿]", en.Subject.Replace("บริษัท ก", ""));
        var rem = SignRequestEmailText.Compose(Mail(onBehalf: false) with { Reminder = true });
        Assert.StartsWith("เตือน:", rem.Subject);
    }

    // ───────────── ฝ่ายค้าน B1 (240-9): อีเมลเอกสารบนเส้นแพลตฟอร์ม ─────────────

    [Fact]
    public void ข้อความผู้ใช้เป็นข้อความล้วน_HTML_ดิบถูกหนี_ขึ้นบรรทัดเป็น_br()
    {
        var h = SignRequestEmailText.PlainTextToHtml("สวัสดี <a href='https://evil'>คลิก</a>\r\nบรรทัดสอง <script>x</script>");
        Assert.DoesNotContain("<a href", h);
        Assert.DoesNotContain("<script>", h);
        Assert.Contains("&lt;a href=", h);
        Assert.Contains("<br>", h);
        Assert.Equal("", SignRequestEmailText.PlainTextToHtml("   "));
        Assert.True(SignRequestEmailText.PlainTextToHtml(new string('ก', 20_000)).Length < 20_000 + 200);
    }

    [Fact]
    public void เทมเพลตคงที่ของเส้นแพลตฟอร์ม_มีหัวเปิดเผย_เลขภาษี_ท้ายอีเมล_และลิงก์แจ้งใช้ผิด()
    {
        var w = SignRequestEmailText.WrapPlatformDocumentEmail(false, "บริษัท ก จำกัด", "0105556000007", "ar@acme.co.th",
            "ใบแจ้งหนี้", "INV-1", SignRequestEmailText.PlainTextToHtml("ข้อความ"), "https://app.nextacc.net/contact.html?topic=email-abuse");
        Assert.Contains("อีเมลนี้ส่งในนาม", w);
        Assert.Contains("0105556000007", w);
        Assert.Contains("ไม่ใช่โฆษณา", w);
        Assert.Contains("ar@acme.co.th", w);
        Assert.Contains("email-abuse", w);
        Assert.Equal("หัว แตก บรรทัด", SignRequestEmailText.SafeSubject("หัว\r\nแตก\nบรรทัด").Replace("  ", " "));
    }

    [Fact]
    public void บริษัทยังไม่ยืนยันตัวตน_เพดานต่ำกว่า_และนับผู้รับรวมสำเนา()
    {
        Assert.True(SignEmailThrottle.IdentityVerified(taxIdValid: true, everPaid: true));
        Assert.False(SignEmailThrottle.IdentityVerified(taxIdValid: true, everPaid: false));    // ทดลองที่ไม่เคยจ่าย
        Assert.False(SignEmailThrottle.IdentityVerified(taxIdValid: false, everPaid: true));
        Assert.Equal(200, SignEmailThrottle.PlatformCapFor(200, true));
        Assert.Equal(SignEmailThrottle.UnverifiedPlatformDailyCap, SignEmailThrottle.PlatformCapFor(200, false));
        Assert.Equal(5, SignEmailThrottle.PlatformCapFor(5, false));                             // ผู้ดูแลตั้งต่ำกว่าอยู่แล้ว = ค่าที่ตั้ง
        Assert.Null(SignEmailThrottle.RecipientCountRefusal(SignEmailThrottle.MaxRecipients));
        Assert.NotNull(SignEmailThrottle.RecipientCountRefusal(SignEmailThrottle.MaxRecipients + 1));
        Assert.Contains("ยังไม่ยืนยันตัวตน", SignEmailThrottle.Refusal(true, 20, 20, 0, 0));
    }

    [Fact]
    public void ป้ายผู้ส่งบนหน้าต่างอีเมล_มาจากเซิร์ฟเวอร์()
    {
        Assert.Contains("ผ่านอีเมลของระบบ NextAcc", OnBehalfEmailSender.Describe(OnBehalfEmailSender.Resolve(In(false))));
        Assert.StartsWith("ส่งจากอีเมลของบริษัท", OnBehalfEmailSender.Describe(OnBehalfEmailSender.Resolve(In(true))));
    }

    // ───────────── ฝ่ายค้าน N4: Rev ใหม่ล้างสถานะติดตามลิงก์ ─────────────

    [Fact]
    public void ออก_Rev_ใหม่ล้างสถานะส่ง_เปิด_เตือน_แต่คง_token_และ_callback()
    {
        var now = DateTime.UtcNow;
        var d = new EntDocument
        {
            DocumentType = DocumentType.Quotation, QuotationAcceptToken = "keep", SignCallbackUrl = "https://erp.example.com/h",
            SignLinkEmailedAt = now, SignLinkEmailedTo = "a@b.co", SignLinkOpenedAt = now, SignReminderSentAt = now,
        };
        QuotationAcceptance.ClearOn(d);
        Assert.Null(d.SignLinkEmailedAt);
        Assert.Null(d.SignLinkEmailedTo);
        Assert.Null(d.SignLinkOpenedAt);
        Assert.Null(d.SignReminderSentAt);
        Assert.Equal("keep", d.QuotationAcceptToken);
        Assert.Equal("https://erp.example.com/h", d.SignCallbackUrl);
    }

    // ───────────── migration ตรงโมเดล ─────────────

    [Fact]
    public void migration_ครบทุกคอลัมน์ของโมเดล_outbox_และช่องใหม่()
    {
        using var db = new Accounting.Data.AccountingDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Accounting.Data.AccountingDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none").Options);
        var migration = string.Join("\n", SignRequestMigration.Statements());
        var et = db.Model.FindEntityType(typeof(SignEventOutboxEntry))!;
        Assert.Contains($"CREATE TABLE IF NOT EXISTS \"{et.GetTableName()}\"", migration);
        foreach (var p in et.GetProperties()) Assert.Contains($"\"{p.GetColumnName()}\"", migration);
        foreach (var ix in et.GetIndexes()) Assert.Contains($"\"{ix.GetDatabaseName()}\"", migration);
        foreach (var col in new[] { "SignCallbackUrl", "SignCallbackRef", "SignLinkIssuedAt", "SignLinkEmailedAt", "SignLinkEmailedTo",
                     "SignLinkOpenedAt", "SignReminderSentAt", "SignLinkLastRecipients", "SignLinkAutoEmailOnApprove", "SignLinkAutoEmailSince",
                     "SignLinkReminderDays", "SignCallbackSecret", "SignCallbackAllowedHosts", "Purpose", "SenderMode" })
            Assert.Contains($"\"{col}\"", migration);
    }

    // ───────────── callback ต่อเอกสาร (ตัวตั้งค่าที่ทุกทางเข้าเรียก) ─────────────

    [Fact]
    public void callback_ต้องมีรหัสลับของบริษัทก่อน_และโฮสต์ตามรายชื่อ_ล้างได้()
    {
        var d = new EntDocument { DocumentType = DocumentType.Quotation };
        var noSecret = new CompanySettings();
        var ex = Assert.Throws<BusinessRuleException>(() => SignRequestService.ApplyCallback(d, noSecret, "https://erp.example.com/h", "R1", false));
        Assert.Equal("SIGN-CALLBACK-SECRET", ex.RuleCode);
        Assert.Null(d.SignCallbackUrl);

        var withSecret = new CompanySettings { SignCallbackSecret = "protected", SignCallbackAllowedHosts = "erp.example.com" };
        Assert.Throws<BusinessRuleException>(() => SignRequestService.ApplyCallback(d, withSecret, "https://other.example.com/h", null, false));
        Assert.Throws<BusinessRuleException>(() => SignRequestService.ApplyCallback(d, withSecret, "https://127.0.0.1/h", null, false));
        SignRequestService.ApplyCallback(d, withSecret, " https://erp.example.com/h ", "  ERP-9\n", false);
        Assert.Equal("https://erp.example.com/h", d.SignCallbackUrl);
        Assert.Equal("ERP-9", d.SignCallbackRef);
        SignRequestService.ApplyCallback(d, withSecret, null, null, clear: true);
        Assert.Null(d.SignCallbackUrl);
        Assert.Null(d.SignCallbackRef);
    }
}
