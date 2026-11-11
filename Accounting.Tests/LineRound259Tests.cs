using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 259 แพ็ก B "ส่งทาง LINE ให้ครบ" — ตัวตัดสิน pure ทั้งสองทิศ:
/// เรื่องที่ LINE ไม่มีเส้นส่ง (A-M4) · ทวง 50 ทวิ ทาง LINE (258-3-6) · ส่งทาง LINE เองจากหน้าเอกสาร (E-H3) · สุขภาพ LINE OA (A-M5/M6/L3) ·
/// ประกาศผูกกลุ่ม (A-M7) · รูป/ไฟล์ในแชต (A-M8) · ส่งซ้ำแถวที่ถอยไปอีเมลแล้ว (A-L5)
/// </summary>
public class LineRound259Tests
{
    // ─────────── A-M4 ───────────

    [Fact]
    public void ลิงก์เซ็น_ติ๊กให้_LINE_ไม่ได้_แต่อีเมลได้_และ_50ทวิ_ติ๊กให้_LINE_ได้()
    {
        Assert.NotNull(NotifyTopics.LineUnsupportedReason(NotifyTopic.Sign));
        Assert.NotNull(NotifyTopics.LineUnsupportedReason(NotifyTopic.Other));
        Assert.Null(NotifyTopics.LineUnsupportedReason(NotifyTopic.Wht));
        Assert.Null(NotifyTopics.LineUnsupportedReason(NotifyTopic.Receipt));

        var (lineSign, lineProblem) = NotifyTopics.FromRequest(new[] { "Document", "Sign" }, allowEmpty: false, NotifyEndpointKind.LineUser);
        Assert.Null(lineSign);
        Assert.Contains("ลิงก์เซ็น", lineProblem);
        var (groupWht, groupProblem) = NotifyTopics.FromRequest(new[] { "Document", "Wht" }, allowEmpty: false, NotifyEndpointKind.LineGroup);
        Assert.Null(groupProblem);
        Assert.Equal(new[] { NotifyTopic.Document, NotifyTopic.Wht }, groupWht);
        // ทิศตรงข้าม: อีเมล (และผู้เรียกเดิมที่ไม่ส่งชนิด) ยังรับลิงก์เซ็นได้
        Assert.Null(NotifyTopics.FromRequest(new[] { "Sign" }, allowEmpty: false, NotifyEndpointKind.Email).Problem);
        Assert.Null(NotifyTopics.FromRequest(new[] { "Sign" }, allowEmpty: false).Problem);
    }

    [Fact]
    public void ค่าตั้งต้นผู้ใช้_LINE_ไม่มีลิงก์เซ็น_และตัดเรื่องที่ไม่ส่งออกจากคำเชิญเก่า()
    {
        Assert.DoesNotContain(NotifyTopic.Sign, NotifyTopics.DefaultsFor(NotifyEndpointKind.LineUser));
        Assert.Contains(NotifyTopic.Receipt, NotifyTopics.DefaultsFor(NotifyEndpointKind.LineUser));
        Assert.Contains(NotifyTopic.Sign, NotifyTopics.DefaultsFor(NotifyEndpointKind.Email));
        Assert.Equal(new[] { NotifyTopic.Document, NotifyTopic.Receipt },
            NotifyTopics.SupportedFor(NotifyEndpointKind.LineUser, new[] { NotifyTopic.Document, NotifyTopic.Receipt, NotifyTopic.Sign }));
        Assert.Equal(3, NotifyTopics.SupportedFor(NotifyEndpointKind.Email, new[] { NotifyTopic.Document, NotifyTopic.Receipt, NotifyTopic.Sign }).Count);
    }

    [Theory]
    [InlineData("[\"Document\",\"Dunning\",\"Receipt\",\"Sign\"]", "[\"Document\",\"Dunning\",\"Receipt\"]")]
    [InlineData("[\"Sign\",\"Document\"]", "[\"Document\"]")]
    [InlineData("[\"Sign\"]", "[]")]
    [InlineData("[\"Document\",\"Other\",\"Wht\"]", "[\"Document\",\"Wht\"]")]
    [InlineData("[\"Document\",\"Dunning\"]", "[\"Document\",\"Dunning\"]")]   // ใบที่ถูกอยู่แล้วไม่ถูกแตะ
    public void ซ่อมค่าที่เก็บไว้_ตัดเฉพาะเรื่องที่_LINE_ไม่ส่ง(string before, string after)
    {
        var fixedJson = Round259LineMigration.StripLineUnsupported(before);
        Assert.Equal(after, fixedJson);
        Assert.NotNull(NotifyTopics.Parse(fixedJson));
    }

    // ─────────── 258-3-6 ทวง 50 ทวิ ทาง LINE ───────────

    [Fact]
    public void ช่องทางทวง_ไม่ส่งอัตโนมัติชนะ_ไม่มีอีเมลแต่มี_LINE_ส่ง_LINE_ไม่มีทั้งสอง_ข้ามพร้อมเหตุ()
    {
        Assert.Equal(new LineWhtCertCard.ReminderChannels(false, false, "block"), LineWhtCertCard.Channels("block", false, true, null, 2));
        Assert.Equal(ContactRecipientRule.ContactDeletedReason, LineWhtCertCard.Channels(null, true, false, null, 1).SkipReason);
        Assert.Equal(new LineWhtCertCard.ReminderChannels(false, true, null), LineWhtCertCard.Channels(null, false, false, "ไม่รับ", 1));
        Assert.Equal(new LineWhtCertCard.ReminderChannels(true, false, null), LineWhtCertCard.Channels(null, false, true, null, 0));
        Assert.Equal(new LineWhtCertCard.ReminderChannels(true, true, null), LineWhtCertCard.Channels(null, false, true, null, 1));
        Assert.Equal(LineWhtCertCard.NoChannelReason, LineWhtCertCard.Channels(null, false, false, null, 0).SkipReason);
        Assert.Equal("ไม่รับ", LineWhtCertCard.Channels(null, false, false, "ไม่รับ", 0).SkipReason);
    }

    private static LineWhtCertCard.Input Card(bool showAmounts, string? url, bool group, int items = 1) => new(
        "บริษัท เรา จำกัด", "0105556123453", "สำนักงานใหญ่", "ลูกค้า", 21,
        Enumerable.Range(1, items).Select(i => new WhtCertReminderCadence.Item("INV-" + i, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 30m)).ToList(),
        showAmounts, url, group);

    private static string Json(LineDocumentCard.Card c) => System.Text.Json.JsonSerializer.Serialize(c.Contents,
        new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [Fact]
    public void การ์ดทวง_มีปุ่มอัปโหลด_แสดงภาษีที่หักถึงผู้ใช้_กลุ่มซ่อนยอดไม่มียอดและไม่มีปุ่ม()
    {
        var user = Json(LineWhtCertCard.Build(Card(true, "https://app.nextacc.test/wht-upload.html?c=x&t=abc", group: false)));
        Assert.Contains("อัปโหลดหนังสือรับรอง", user);
        Assert.Contains("30.00", user);
        Assert.Contains("INV-1", user);
        Assert.True(LineWhtCertCard.UploadLinkAllowed(isGroup: false, showAmounts: false));
        Assert.False(LineWhtCertCard.UploadLinkAllowed(isGroup: true, showAmounts: false));
        Assert.True(LineWhtCertCard.UploadLinkAllowed(isGroup: true, showAmounts: true));
        var hidden = Json(LineWhtCertCard.Build(Card(false, null, group: true)));
        Assert.DoesNotContain("30.00", hidden);
        Assert.DoesNotContain("อัปโหลดหนังสือรับรอง", hidden);
        Assert.Contains("INV-1", hidden);
        // ลิงก์ http (ไม่ใช่ https) ⇒ ไม่มีปุ่ม (LINE ปฏิเสธทั้งการ์ด)
        Assert.DoesNotContain("อัปโหลดหนังสือรับรอง", Json(LineWhtCertCard.Build(Card(true, "http://x.test/u", group: false))));
        var many = Json(LineWhtCertCard.Build(Card(true, null, group: false, items: LineWhtCertCard.MaxItems + 3)));
        Assert.Contains("และอีก 3 รายการ", many);
    }

    [Fact]
    public void token_ลิงก์ของการ์ด_LINE_คนละค่ากับอีเมลแม้_id_เดียวกัน_และคำนวณซ้ำได้()
    {
        var key = WhtCertUploadLinkPolicy.DeriveQueueKey("secret-259");
        var company = Guid.NewGuid();
        var id = Guid.NewGuid();
        Assert.NotEqual(WhtCertUploadLinkPolicy.TokenForQueue(key, company, id), WhtCertUploadLinkPolicy.TokenForLineQueue(key, company, id));
        Assert.Equal(WhtCertUploadLinkPolicy.TokenForLineQueue(key, company, id), WhtCertUploadLinkPolicy.TokenForLineQueue(key, company, id));
        Assert.True(WhtCertUploadLinkPolicy.LooksValid(WhtCertUploadLinkPolicy.TokenForLineQueue(key, company, id)));
    }

    [Fact]
    public void ขั้นอ่านกลับจากกุญแจทวง_รูปไม่ตรงได้_null()
    {
        var contact = Guid.NewGuid();
        Assert.Equal(45, WhtCertReminderCadence.StepFromQueueKey(WhtCertReminderCadence.QueueKey(contact, 45, new DateTime(2026, 9, 1))));
        Assert.Null(WhtCertReminderCadence.StepFromQueueKey("doc:x:DocumentApproved:rule"));
        Assert.Null(WhtCertReminderCadence.StepFromQueueKey($"whtrem:{contact:N}:8:20260901"));   // ไม่ใช่ขั้นจริง
        Assert.Null(WhtCertReminderCadence.StepFromQueueKey(null));
    }

    // ─────────── E-H3 ส่งทาง LINE เอง ───────────

    [Fact]
    public void ชนิดที่ส่งได้_ใบร่างยกเลิก_และใบซื้อ_ส่งไม่ได้()
    {
        Assert.True(DocumentLineSendRule.Sendable(DocumentType.TaxInvoice));
        Assert.True(DocumentLineSendRule.Sendable(DocumentType.PurchaseOrder));
        Assert.False(DocumentLineSendRule.Sendable(DocumentType.PurchaseInvoice));
        Assert.Null(DocumentLineSendRule.DocumentProblem(true, DocumentType.Invoice, DocumentStatus.Approved));
        Assert.Null(DocumentLineSendRule.DocumentProblem(true, DocumentType.Receipt, DocumentStatus.Paid));
        Assert.Contains("อนุมัติ", DocumentLineSendRule.DocumentProblem(true, DocumentType.Invoice, DocumentStatus.Draft));
        Assert.Contains("ยกเลิก", DocumentLineSendRule.DocumentProblem(true, DocumentType.Invoice, DocumentStatus.Voided));
        Assert.NotNull(DocumentLineSendRule.DocumentProblem(true, DocumentType.PurchaseInvoice, DocumentStatus.Approved));
        Assert.NotNull(DocumentLineSendRule.DocumentProblem(false, DocumentType.Invoice, DocumentStatus.Approved));
    }

    private static ContactRecipientRule.RecipientOption Opt(Guid? id, string kind, bool available, bool showAmounts = true, string? reason = null)
        => new(id, kind, kind == NotifyEndpointKind.Email ? "a@b.co" : null, kind + "-" + id, available, reason, null, showAmounts, false);

    [Fact]
    public void เลือกปลายทาง_ว่าง_ไม่ใช่ของผู้ติดต่อ_ใช้ไม่ได้_ปฏิเสธทั้งคำขอ_ถูกต้องผ่าน()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), mail = Guid.NewGuid();
        var options = new[]
        {
            Opt(a, NotifyEndpointKind.LineUser, true), Opt(b, NotifyEndpointKind.LineGroup, false, reason: "บล็อก"),
            Opt(mail, NotifyEndpointKind.Email, true),
        };
        Assert.NotNull(DocumentLineSendRule.Pick(options, Array.Empty<Guid>()).Problem);
        Assert.NotNull(DocumentLineSendRule.Pick(options, new[] { mail }).Problem);   // อีเมลไม่ใช่ปลายทาง LINE
        Assert.Contains("บล็อก", DocumentLineSendRule.Pick(options, new[] { a, b }).Problem);
        var (chosen, problem) = DocumentLineSendRule.Pick(options, new[] { a, a });
        Assert.Null(problem);
        Assert.Single(chosen);
    }

    [Fact]
    public void ปลายทางบนหน้าต่าง_กลุ่มซ่อนยอดไม่มีปุ่ม_PDF_อีเมลไม่อยู่ในรายการ()
    {
        Guid a = Guid.NewGuid(), g = Guid.NewGuid();
        var targets = DocumentLineSendRule.Targets(new[]
        {
            Opt(a, NotifyEndpointKind.LineUser, true), Opt(g, NotifyEndpointKind.LineGroup, true, showAmounts: false),
            Opt(Guid.NewGuid(), NotifyEndpointKind.Email, true),
        });
        Assert.Equal(2, targets.Count);
        Assert.True(targets.Single(t => t.EndpointId == a).PdfLink);
        var grp = targets.Single(t => t.EndpointId == g);
        Assert.False(grp.PdfLink);
        Assert.False(grp.ShowAmounts);
    }

    [Fact]
    public void กุญแจส่งเอง_นาทีเดียวกันเท่ากัน_ต่างนาทีต่างกัน_และผู้ส่งคิวรับ_trigger_นี้()
    {
        var doc = Guid.NewGuid();
        var t = new DateTime(2026, 11, 11, 3, 4, 5, DateTimeKind.Utc);
        Assert.Equal(DocumentLineSendRule.BaseKey(doc, t), DocumentLineSendRule.BaseKey(doc, t.AddSeconds(40)));
        Assert.NotEqual(DocumentLineSendRule.BaseKey(doc, t), DocumentLineSendRule.BaseKey(doc, t.AddMinutes(1)));
        Assert.True(ContactLineChannel.SenderSupports(ContactLineChannel.ManualTrigger));
        Assert.False(ContactLineChannel.SupportsLine(ContactLineChannel.ManualTrigger));   // ฟอร์มกฎตารางส่งไม่เปิดเรื่องนี้
        Assert.DoesNotContain("ส่งแล้ว", DocumentLineSendRule.QueuedMessage(1, 0));   // เข้าคิว ≠ ส่งแล้ว
    }

    // ─────────── ฝ่ายค้าน 259 M1 / L3 / L5 ───────────

    [Fact]
    public void ปุ่มชำระ_กลุ่มซ่อนยอดไม่ได้_กลุ่มแสดงยอดและผู้ใช้ได้_ติ๊กแล้วเลือกกลุ่มซ่อนยอดปฏิเสธ()
    {
        Assert.False(DocumentLineSendRule.PayLinkAllowed(NotifyEndpointKind.LineGroup, showAmounts: false));
        Assert.True(DocumentLineSendRule.PayLinkAllowed(NotifyEndpointKind.LineGroup, showAmounts: true));
        Assert.True(DocumentLineSendRule.PayLinkAllowed(NotifyEndpointKind.LineUser, showAmounts: false));
        var hidden = Opt(Guid.NewGuid(), NotifyEndpointKind.LineGroup, true, showAmounts: false);
        var user = Opt(Guid.NewGuid(), NotifyEndpointKind.LineUser, true);
        Assert.Contains("ซ่อนยอด", DocumentLineSendRule.PayLinkProblem(true, new[] { user, hidden }));
        Assert.Null(DocumentLineSendRule.PayLinkProblem(false, new[] { user, hidden }));   // ไม่ติ๊ก ⇒ ส่งได้ (การ์ดไม่มีปุ่มอยู่แล้ว)
        Assert.Null(DocumentLineSendRule.PayLinkProblem(true, new[] { user, Opt(Guid.NewGuid(), NotifyEndpointKind.LineGroup, true, showAmounts: true) }));
        Assert.False(DocumentLineSendRule.Targets(new[] { hidden }).Single().PayLink);
    }

    [Fact]
    public void ช่องลิงก์ชำระตัดสินตอนเปิดหน้าต่าง_เหตุปฏิเสธของตัวออกลิงก์_เพิกถอน_ไม่มียอดค้าง()
    {
        Assert.Equal((true, (string?)null), DocumentLineSendRule.PayLinkOffer(true, 100m, null, null));
        Assert.False(DocumentLineSendRule.PayLinkOffer(true, 0m, null, null).Offered);
        Assert.Equal((false, (string?)"ใบสั่งซื้อไม่มีลิงก์ชำระ"), DocumentLineSendRule.PayLinkOffer(true, 100m, "ใบสั่งซื้อไม่มีลิงก์ชำระ", null));
        Assert.Equal(PayLinkRevokedException.StaffMessage, DocumentLineSendRule.PayLinkOffer(true, 100m, null, DateTime.UtcNow).Note);
        Assert.False(DocumentLineSendRule.PayLinkOffer(false, 100m, null, null).Offered);
        Assert.Equal(409, new PayLinkRevokedException().StatusCode);
        Assert.DoesNotContain("เพิกถอน", PayLinkRevokedException.CustomerMessage);   // ลูกค้าไม่เห็นรายละเอียดภายใน
    }

    [Fact]
    public void ลิงก์อัปโหลดของฉบับทวงใช้ไม่ได้_บอกทางไปต่อจริง_ไม่สัญญาว่ารอบหน้าเข้าคิวใหม่()
    {
        Assert.DoesNotContain("รอบหน้าจะเข้าคิวใหม่", WhtCertUploadLinkPolicy.ReminderLinkUnusable);
        Assert.Contains("หน้า 50 ทวิ", WhtCertUploadLinkPolicy.ReminderLinkUnusable);
        Assert.Equal(new[] { "reminder-email", "reminder-line" }, WhtCertUploadLinkPolicy.ReminderSources);
    }

    // ─────────── A-M5 / A-M6 / A-L3 ───────────

    [Fact]
    public void ความพร้อม_ยังไม่เคยได้_webhook_เตือน_ได้แล้ว_ok_ไม่มี_BasicId_เตือน()
    {
        var never = LineOaHealth.Rows(true, true, true, null, false, "@shop", null, null, false);
        Assert.Equal(LineOaHealth.LevelWarn, never.Single(r => r.Code == "webhook").Level);
        Assert.Contains("ยังไม่เคยได้รับ event จาก LINE", never.Single(r => r.Code == "webhook").Label);
        var seen = LineOaHealth.Rows(true, true, true, new DateTime(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc), false, "@shop", null, null, false);
        Assert.Equal(LineOaHealth.LevelOk, seen.Single(r => r.Code == "webhook").Level);
        Assert.DoesNotContain(seen, r => r.Code == "basic-id");
        var noBasic = LineOaHealth.Rows(true, true, true, DateTime.UtcNow, false, null, null, null, false);
        Assert.Equal(LineOaHealth.LevelWarn, noBasic.Single(r => r.Code == "basic-id").Level);
        var mismatch = LineOaHealth.Rows(true, true, true, DateTime.UtcNow, true, "@shop", null, null, false);
        Assert.Equal(LineOaHealth.LevelError, mismatch.Single(r => r.Code == "webhook").Level);
        Assert.Empty(LineOaHealth.Rows(true, true, true, null, false, null, null, null, isPlatformCompany: true));
        // ยังไม่เปิดใช้ ⇒ ไม่ฟ้องเรื่อง webhook/Basic ID (ยังไม่ใช่ปัญหา)
        var off = LineOaHealth.Rows(false, false, false, null, false, null, null, null, false);
        Assert.Single(off);
        Assert.Equal(LineOaHealth.LevelWarn, off[0].Level);
    }

    [Fact]
    public void ผลทดสอบตามสถานะทดสอบ_ไม่ใช่ธงที่รีเซ็ตตอนบันทึก()
    {
        var at = DateTime.UtcNow;
        Assert.Equal(LineOaHealth.LevelOk, LineOaHealth.Rows(true, true, true, at, false, "@s", at, "OK", false).Single(r => r.Code == "test").Level);
        var failed = LineOaHealth.Rows(true, true, true, at, false, "@s", at, "ส่งไม่สำเร็จ — LINE ตอบ 400", false).Single(r => r.Code == "test");
        Assert.Equal(LineOaHealth.LevelError, failed.Level);
        Assert.Contains("400", failed.Detail);
        Assert.DoesNotContain(LineOaHealth.Rows(true, true, true, at, false, "@s", null, null, false), r => r.Code == "test");
    }

    [Fact]
    public void ทดสอบส่ง_เลือกกลุ่มทีมหรือพิมพ์เอง_ล้มบอกเหตุจริงของ_LINE()
    {
        Assert.Equal(("Cteam", (string?)null), LineOaHealth.TestTarget(LineOaHealth.TeamGroupTargetKey, "Uignored", " Cteam "));
        Assert.NotNull(LineOaHealth.TestTarget(LineOaHealth.TeamGroupTargetKey, null, null).Problem);
        Assert.NotNull(LineOaHealth.TestTarget("endpoint:x", null, "C1").Problem);
        Assert.Equal(("Utyped", (string?)null), LineOaHealth.TestTarget(null, " Utyped ", "C1"));
        Assert.NotNull(LineOaHealth.TestTarget(null, "  ", "C1").Problem);
        var msg = LineOaHealth.TestFailureMessage(400, "LINE ตอบ 400: {\"message\":\"The property, 'to', in the request body is invalid\"}", false);
        Assert.Contains("ตอบ 400", msg);
        Assert.Contains("ขึ้นต้นด้วย U", msg);
        Assert.DoesNotContain("ดู log", msg);
        Assert.Contains("Token", LineOaHealth.TestFailureMessage(401, "LINE ตอบ 401", false));
    }

    // ─────────── A-M7 / A-M8 / A-L5 ───────────

    [Fact]
    public void ผูกกลุ่ม_ประกาศล้มบอกผู้ใช้_ประกาศได้ข้อความสำเร็จเดิม()
    {
        Assert.Null(LineGroupBindRule.AnnounceOutcome(true, null));
        var note = LineGroupBindRule.AnnounceOutcome(false, "บอทไม่อยู่ในกลุ่ม");
        Assert.Contains("ไม่สำเร็จ", note);
        Assert.Contains("บอทไม่อยู่ในกลุ่ม", note);
        Assert.Equal(note, LineGroupBindRule.ConfirmMessage(note));
        Assert.Contains("ประกาศในกลุ่มแล้ว", LineGroupBindRule.ConfirmMessage(null));
    }

    [Fact]
    public void รูปและไฟล์ในแชตตอบรับ_ข้อความตัวอักษรไม่เข้าเส้นนี้_คีย์ไม่มี_userId_ดิบ()
    {
        Assert.True(LineOaHealth.IsChatMedia("image"));
        Assert.True(LineOaHealth.IsChatMedia("file"));
        Assert.False(LineOaHealth.IsChatMedia("text"));
        Assert.False(LineOaHealth.IsChatMedia("sticker"));
        var key = LineOaHealth.ChatMediaRateKey("Ubot", "Uabc123");
        Assert.DoesNotContain("Uabc123", key);
        Assert.Equal(key, LineOaHealth.ChatMediaRateKey("Ubot", "Uabc123"));
        Assert.NotEqual(key, LineOaHealth.ChatMediaRateKey("Ubot2", "Uabc123"));
        Assert.Contains("ชำระเงิน", LineOaHealth.ChatMediaReply);
    }

    [Fact]
    public void ส่งซ้ำแถวที่ถอยไปอีเมลแล้ว_บอกก่อนยืนยัน_แถวอื่นไม่มีหมายเหตุ()
    {
        Assert.Null(ContactLineChannel.RetryNote(null));
        Assert.Contains("อีเมล", ContactLineChannel.RetryNote(DateTime.UtcNow));
    }
}
