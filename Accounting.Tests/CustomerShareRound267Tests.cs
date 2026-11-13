using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 267 แพ็ก P3 — "📤 ส่งให้ลูกค้า" + ไทม์ไลน์ + LINE เซ็น (pure · คำตัดสิน 267-1-61…67 · ดีไซน์ §17 S21 S23 S36) ·
/// ตัวตัดสินเดียวของลิงก์หน้าเอกสารบนการ์ด LINE (<see cref="DocumentLineSendRule.PageLinkAllowed"/>) · ผลแยกต่อช่องทาง · ตัวระบุแหล่งที่มา s ·
/// ทุกข้อมีทิศตรงข้าม (ใบ/ปลายทางที่ถูกอยู่แล้วไม่ถูกแตะ)
/// </summary>
public class CustomerShareRound267Tests
{
    private static string Json(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static ContactRecipientRule.RecipientOption Opt(Guid id, string kind, bool available = true, bool showAmounts = true, string display = "ปลายทาง")
        => new(id, kind, null, display, available, available ? null : "ไม่ได้ติ๊กเรื่องนี้", null, showAmounts, false);

    // ─────────── S21 · กลุ่ม LINE ซ่อนยอด ───────────

    [Fact]
    public void CDP267_HiddenAmountGroupNoPageLink()
    {
        var problem = DocumentLineSendRule.PageLinkProblem(NotifyEndpointKind.LineGroup, showAmounts: false, DocumentType.Invoice);
        Assert.Contains("ซ่อนยอด", problem);
        Assert.False(DocumentLineSendRule.PageLinkAllowed(NotifyEndpointKind.LineGroup, false, DocumentType.Invoice));
        var g = Guid.NewGuid();
        var t = Assert.Single(DocumentLineSendRule.Targets(new[] { Opt(g, NotifyEndpointKind.LineGroup, showAmounts: false) }, DocumentType.Invoice));
        Assert.False(t.PageLink);
        Assert.Equal(problem, t.PageLinkNote);
        Assert.False(t.PdfLink);   // การ์ดไม่มีปุ่มเลย (ดู PDF ใช้ตัวตัดสินเดียวกัน)

        // การ์ดของกลุ่มซ่อนยอด: ผู้ส่งคิวไม่ส่ง PageUrl/PayUrl/ViewUrl ⇒ ไม่มีปุ่ม uri · ไม่มียอด
        var card = LineDocumentCard.Build(new LineDocumentCard.Input("บริษัท ก", false, "th", "ใบแจ้งหนี้", "INV-1", null, "THB", null, "ลูกค้า",
            null, IsGroup: true));
        Assert.DoesNotContain("\"uri\"", Json(card.Contents));

        // ทิศตรงข้าม: ผู้ใช้ 1:1 + กลุ่มที่แสดงยอด ได้ลิงก์หน้าเอกสารของใบแจ้งหนี้
        Assert.True(DocumentLineSendRule.PageLinkAllowed(NotifyEndpointKind.LineUser, false, DocumentType.Invoice));
        Assert.True(DocumentLineSendRule.PageLinkAllowed(NotifyEndpointKind.LineGroup, true, DocumentType.Invoice));
        Assert.False(DocumentLineSendRule.PageLinkAllowed(NotifyEndpointKind.Email, true, DocumentType.Invoice));
    }

    // ─────────── S23 · กลุ่ม LINE ของใบที่ตอบรับได้ (267-1-7) ───────────

    [Fact]
    public void CDP267_GroupSignPolicy()
    {
        foreach (var type in new[] { DocumentType.Quotation, DocumentType.DeliveryNote })
        {
            Assert.Contains("ปุ่มดูเท่านั้น", DocumentLineSendRule.PageLinkProblem(NotifyEndpointKind.LineGroup, true, type));
            Assert.True(DocumentLineSendRule.PageLinkAllowed(NotifyEndpointKind.LineUser, true, type));   // ทิศตรงข้าม: 1:1 ได้ปุ่ม "ดู/ยอมรับ"
        }
        // เรื่อง "ลิงก์เซ็น": 1:1 ติ๊กได้ · กลุ่มไม่ได้ (ไม่รู้ชนิด = เข้มสุด)
        Assert.Null(NotifyTopics.LineUnsupportedReason(NotifyTopic.Sign, NotifyEndpointKind.LineUser));
        Assert.NotNull(NotifyTopics.LineUnsupportedReason(NotifyTopic.Sign, NotifyEndpointKind.LineGroup));
        Assert.NotNull(NotifyTopics.LineUnsupportedReason(NotifyTopic.Sign, null));
        // กลุ่มของใบแจ้งหนี้ (ไม่มีปุ่มตอบรับ) ไม่ถูกแตะ
        Assert.Null(DocumentLineSendRule.PageLinkProblem(NotifyEndpointKind.LineGroup, true, DocumentType.Invoice));
    }

    [Fact]
    public void ลิงก์หน้าเอกสารของใบที่ตอบรับได้_รวมผู้ใช้11ที่ติ๊กลิงก์เซ็น_ไม่รวมกลุ่ม()
    {
        Guid user = Guid.NewGuid(), userSignOnly = Guid.NewGuid(), group = Guid.NewGuid();
        var doc = new[] { Opt(user, NotifyEndpointKind.LineUser), Opt(userSignOnly, NotifyEndpointKind.LineUser, available: false), Opt(group, NotifyEndpointKind.LineGroup, available: false) };
        var sign = new[] { Opt(user, NotifyEndpointKind.LineUser), Opt(userSignOnly, NotifyEndpointKind.LineUser), Opt(group, NotifyEndpointKind.LineGroup) };
        var merged = DocumentLineSendRule.MergeSignTopic(doc, sign);
        Assert.Equal((true, NotifyTopic.Document), (merged.Single(m => m.Option.EndpointId == user).Option.Available, merged.Single(m => m.Option.EndpointId == user).Topic));
        Assert.Equal((true, NotifyTopic.Sign), (merged.Single(m => m.Option.EndpointId == userSignOnly).Option.Available, merged.Single(m => m.Option.EndpointId == userSignOnly).Topic));
        Assert.False(merged.Single(m => m.Option.EndpointId == group).Option.Available);   // กลุ่มไม่ได้สิทธิ์จากเรื่อง "ลิงก์เซ็น"
        // ทิศตรงข้าม: ไม่ส่งลิงก์หน้าเอกสาร (sign = null) ⇒ ผลเท่าเดิม
        Assert.False(DocumentLineSendRule.MergeSignTopic(doc, null).Single(m => m.Option.EndpointId == userSignOnly).Option.Available);
    }

    // ─────────── S36 · พรีวิวของพนักงาน ───────────

    [Fact]
    public void CDP267_PreviewNoSideEffects()
    {
        var now = DateTime.UtcNow;
        foreach (var type in new[] { DocumentType.Quotation, DocumentType.Invoice, DocumentType.DeliveryNote })
        {
            var actions = CustomerDocPage.Actions(new CustomerDocPage.Facts(type, DocumentStatus.Approved, CustomerDocPage.Mode.Preview, false,
                PayOffered: true, null, SlipOffered: true, now.AddDays(10), now.AddDays(-1), null, "/respond"), now);
            Assert.NotEmpty(actions);
            Assert.All(actions, a => { Assert.False(a.Enabled); Assert.NotNull(a.DisabledReason); Assert.Null(a.Href); });
            // ทิศตรงข้าม: โหมดลิงก์ ปุ่มดาวน์โหลดเปิด (ภาพเดียวกัน แต่ใช้งานได้)
            var live = CustomerDocPage.Actions(new CustomerDocPage.Facts(type, DocumentStatus.Approved, CustomerDocPage.Mode.Link, false,
                true, null, true, now.AddDays(10), now.AddDays(-1), null, "/respond"), now);
            Assert.Equal(actions.Select(a => a.Id).Where(id => id != CustomerDocPage.ActionDecline && id != CustomerDocPage.ActionRequestChange),
                live.Select(a => a.Id).Where(id => id != CustomerDocPage.ActionDecline && id != CustomerDocPage.ActionRequestChange));
            Assert.Contains(live, a => a.Id == CustomerDocPage.ActionDownloadPdf && a.Enabled);
        }
    }

    // ─────────── ผลแยกต่อช่องทาง (อีเมลล้มไม่ลาก LINE) ───────────

    [Fact]
    public void ผลต่อช่องทาง_อีเมลล้ม_LINEเข้าคิว_คำตอบไม่สำเร็จแต่บอกทุกช่องทาง()
    {
        var outcomes = new[]
        {
            CustomerShareRule.Ok(CustomerShareRule.ChannelCopy, "ลิงก์พร้อมคัดลอก"),
            CustomerShareRule.Failed(CustomerShareRule.ChannelEmail, "ส่งอีเมลไม่สำเร็จ — SMTP ไม่ตอบ"),
            CustomerShareRule.Queued(CustomerShareRule.ChannelLine, "เข้าคิว LINE แล้ว 1 ปลายทาง"),
        };
        var (ok, msg) = CustomerShareRule.Summary(outcomes);
        Assert.False(ok);
        Assert.Contains("1 จาก 3", msg);
        Assert.Contains("SMTP ไม่ตอบ", msg);
        Assert.Contains("เข้าคิว LINE", msg);
        // ทิศตรงข้าม: ทุกช่องทางผ่าน (LINE = เข้าคิว ไม่ใช่ล้ม) ⇒ สำเร็จ
        Assert.True(CustomerShareRule.Summary(new[] { outcomes[0], outcomes[2] }).AllOk);
        Assert.False(CustomerShareRule.Summary(Array.Empty<CustomerShareRule.ChannelOutcome>()).AllOk);
    }

    [Fact]
    public void ช่องทางที่รับ_ไม่ซ้ำตามลำดับมาตรฐาน_ไม่รู้จักหรือว่างปฏิเสธทั้งคำขอ()
    {
        var (list, problem) = CustomerShareRule.ParseChannels(new[] { "LINE", "email", "line", " " });
        Assert.Null(problem);
        Assert.Equal(new[] { CustomerShareRule.ChannelEmail, CustomerShareRule.ChannelLine }, list);
        Assert.NotNull(CustomerShareRule.ParseChannels(new[] { "fax" }).Problem);
        Assert.NotNull(CustomerShareRule.ParseChannels(null).Problem);
    }

    [Fact]
    public void ปุ่มส่งให้ลูกค้า_ด่านเดียวกับตัวออกลิงก์หน้าเอกสาร()
    {
        Assert.True(CustomerShareRule.ButtonOffered(DocumentType.Quotation, DocumentStatus.Approved));
        Assert.True(CustomerShareRule.ButtonOffered(DocumentType.Invoice, DocumentStatus.Paid));
        Assert.True(CustomerShareRule.ButtonOffered(DocumentType.PurchaseOrder, DocumentStatus.Approved));
        Assert.False(CustomerShareRule.ButtonOffered(DocumentType.Invoice, DocumentStatus.Draft));
        Assert.False(CustomerShareRule.ButtonOffered(DocumentType.Invoice, DocumentStatus.Voided));
        Assert.False(CustomerShareRule.ButtonOffered(DocumentType.PurchaseInvoice, DocumentStatus.Approved));
    }

    // ─────────── ตัวระบุแหล่งที่มา s (267-1-63) ───────────

    [Fact]
    public void ตัวระบุแหล่งที่มา_อ่านกลับได้_รูปแบบผิดไม่มีผล_ใช้ได้เฉพาะการ์ด11ของใบเดียวกันที่LINEรับแล้ว()
    {
        var q = Guid.NewGuid();
        var s = CustomerShareRule.LineSource(q);
        Assert.Equal(q, CustomerShareRule.ParseLineSource(s));
        Assert.Null(CustomerShareRule.ParseLineSource(q.ToString("N")));
        Assert.Null(CustomerShareRule.ParseLineSource("l" + Guid.Empty.ToString("N")));
        Assert.Null(CustomerShareRule.ParseLineSource(null));
        Assert.Equal("https://x/p?c=1&t=2&s=" + s, CustomerShareRule.WithSource("https://x/p?c=1&t=2", s));

        var doc = Guid.NewGuid();
        Assert.True(CustomerShareRule.LineSourceValid(true, "Document", doc, doc, NotifyEndpointKind.LineUser, true, ContactLineChannel.StatusSent));
        Assert.False(CustomerShareRule.LineSourceValid(true, "Document", Guid.NewGuid(), doc, NotifyEndpointKind.LineUser, true, ContactLineChannel.StatusSent));
        Assert.False(CustomerShareRule.LineSourceValid(true, "Document", doc, doc, NotifyEndpointKind.LineGroup, true, ContactLineChannel.StatusSent));
        Assert.False(CustomerShareRule.LineSourceValid(true, "Document", doc, doc, NotifyEndpointKind.LineUser, null, ContactLineChannel.StatusSent));
        Assert.False(CustomerShareRule.LineSourceValid(true, "Document", doc, doc, NotifyEndpointKind.LineUser, true, ContactLineChannel.StatusFailed));
        Assert.False(CustomerShareRule.LineSourceValid(false, "Document", doc, doc, NotifyEndpointKind.LineUser, true, ContactLineChannel.StatusSent));
    }

    // ─────────── การ์ด LINE · อีเมล ───────────

    [Fact]
    public void การ์ดลิงก์หน้าเอกสาร_ปุ่มตามสิ่งที่ลูกค้าทำได้_ปุ่มชำระที่ติ๊กอยู่คู่กัน_การ์ดฉบับเซ็นมีปุ่มฉบับเซ็น()
    {
        const string page = "https://app.test/pages/pay-document.html?c=1&t=2&s=l1";
        var respond = CustomerShareRule.PageButtonLabel("th", DocumentType.Quotation, CustomerShareRule.PageButtonKind.Respond);
        var accept = LineDocumentCard.Build(new LineDocumentCard.Input("บริษัท ก", false, "th", "ใบเสนอราคา", "QT-1", 1000m, "THB", null, "ลูกค้า",
            "https://pay.test/x", false, ViewUrl: "https://view.test/x", PageUrl: page, PageButton: respond));
        var json = Json(accept.Contents);
        Assert.Contains("ดู/ยอมรับ", json);
        Assert.Contains("pay.test", json);          // ฝ่ายค้าน MED-2: ปุ่มชำระที่ผู้ใช้ติ๊กอยู่คู่กับปุ่มหน้าเอกสาร
        Assert.DoesNotContain("view.test", json);   // ไม่ซ้อนปุ่มดู PDF (หน้ามีดาวน์โหลด)
        Assert.Equal("ดู/เซ็นรับ", CustomerShareRule.PageButtonLabel("th", DocumentType.DeliveryNote, CustomerShareRule.PageButtonKind.Respond));
        Assert.Equal("ดู/ตอบรับ", CustomerShareRule.PageButtonLabel("th", DocumentType.PurchaseOrder, CustomerShareRule.PageButtonKind.Respond));
        Assert.Equal("ดู/ชำระเงิน", CustomerShareRule.PageButtonLabel("th", DocumentType.Invoice, CustomerShareRule.PageButtonKind.Pay));
        Assert.Equal("View document", CustomerShareRule.PageButtonLabel("en", DocumentType.Receipt, CustomerShareRule.PageButtonKind.View));
        // ทิศตรงข้าม: ไม่มีลิงก์ชำระ ⇒ ไม่มีปุ่มชำระ
        Assert.DoesNotContain("pay.test", Json(LineDocumentCard.Build(new LineDocumentCard.Input("บริษัท ก", false, "th", "ใบเสนอราคา", "QT-1", 1000m, "THB", null,
            "ลูกค้า", null, false, PageUrl: page, PageButton: respond)).Contents));

        var signed = LineDocumentCard.Build(new LineDocumentCard.Input("บริษัท ก", false, "th", "ใบเสนอราคา", "QT-1", 1000m, "THB", null, "ลูกค้า",
            null, false, PageUrl: page, SignedCopy: true));
        Assert.Contains("ฉบับเซ็น", Json(signed.Contents));
        Assert.Contains("ฉบับเซ็น", signed.AltText);
        Assert.DoesNotContain("ของท่าน", Json(signed.Contents));   // ฝ่ายค้าน LOW-1: ถ้อยคำกลาง (ผู้รับต้นทางอาจไม่ใช่ผู้ลงนาม)

        // ทิศตรงข้าม: การ์ดเดิม (ไม่มีหน้า) คงปุ่มชำระ + ดู PDF
        var legacy = Json(LineDocumentCard.Build(new LineDocumentCard.Input("บริษัท ก", false, "th", "ใบแจ้งหนี้", "INV-1", 1000m, "THB", null, "ลูกค้า",
            "https://pay.test/x", false, ViewUrl: "https://view.test/x")).Contents);
        Assert.Contains("pay.test", legacy);
        Assert.Contains("view.test", legacy);
    }

    [Fact]
    public void บล็อกลิงก์หน้าเอกสารในอีเมล_หนีค่า_ป้ายตามช่วงตอบรับ_eTaxไม่มีบล็อก()
    {
        var block = CustomerShareRule.EmailPageBlock("https://x/p?c=1&t=<b>", "<script>", false);
        Assert.DoesNotContain("<script>", block);
        Assert.DoesNotContain("t=<b>", block);
        Assert.StartsWith(CustomerShareRule.PageBlockStart, block);
        Assert.False(CustomerShareRule.EmailCarriesPageLink(EtaxEmailChannel.EtaxByEmail));
        Assert.True(CustomerShareRule.EmailCarriesPageLink(EtaxEmailChannel.PlainEmail));
        Assert.True(CustomerShareRule.EmailCarriesPageLink(EtaxEmailChannel.PlainCopyAfterEtax));
    }

    [Fact]
    public void ตัวเลือกเปิดให้ลูกค้าตอบรับ_เฉพาะใบที่ตอบรับได้_ตอบแล้วหรือยกเลิกปิดพร้อมเหตุ()
    {
        Assert.False(CustomerShareRule.Respond(DocumentType.Invoice, DocumentStatus.Approved, false, null).Applies);
        var q = CustomerShareRule.Respond(DocumentType.Quotation, DocumentStatus.Approved, false, null);
        Assert.True(q.Applies && q.Offered && !q.Open);
        Assert.True(CustomerShareRule.Respond(DocumentType.Quotation, DocumentStatus.Approved, true, null).Open);
        var signed = CustomerShareRule.Respond(DocumentType.DeliveryNote, DocumentStatus.Approved, true, DateTime.UtcNow);
        Assert.False(signed.Offered);
        Assert.NotNull(signed.Note);
        Assert.False(CustomerShareRule.Respond(DocumentType.Quotation, DocumentStatus.Voided, false, null).Offered);
    }

    [Fact]
    public void ไทม์ไลน์_สถานะคิวตามจริง_ล้มมีทางไปต่อ_ไม่ประทับส่งแล้วเอง()
    {
        var at = DateTime.UtcNow;
        Assert.Equal(CustomerShareRule.ToneInfo, CustomerShareRule.LineItem(at, ContactLineChannel.StatusPending, NotifyEndpointKind.LineUser, null, false).Tone);
        Assert.Contains("รอส่ง", CustomerShareRule.LineItem(at, ContactLineChannel.StatusSending, NotifyEndpointKind.LineUser, null, false).Label);
        var failed = CustomerShareRule.LineItem(at, ContactLineChannel.StatusFailed, NotifyEndpointKind.LineUser, "บล็อก", false);
        Assert.Equal((CustomerShareRule.ToneBad, CustomerShareRule.ActionReshare), (failed.Tone, failed.ActionId));
        Assert.Contains("รับแล้ว", CustomerShareRule.LineItem(at, ContactLineChannel.StatusSent, NotifyEndpointKind.LineUser, null, true).Label);
        var mail = CustomerShareRule.EmailItem(at, false, "a@b.co", "SMTP 550", false);
        Assert.Equal(CustomerShareRule.ActionReshare, mail.ActionId);
        Assert.Contains("SMTP 550", mail.Detail);
        Assert.Null(CustomerShareRule.EmailItem(at, true, "a@b.co", null, false).ActionId);
        Assert.Contains("ไม่รับ", CustomerShareRule.CustomerActionItem(at, nameof(CustomerSignRule.Kind.Declined), "สมชาย ใจดี", "ผู้จัดการ", 2).Label);
    }

    // ─────────── ฝ่ายค้าน 267-P3 ───────────

    private static Accounting.Models.Entities.Document Quote(DateTime now, bool windowOpen, string? response = null, DateTime? acceptedAt = null) => new()
    {
        DocumentType = DocumentType.Quotation, Status = DocumentStatus.Approved, DueDate = now.AddDays(20), BalanceDue = 1_070m,
        PayLinkTokenHash = "h", PayLinkExpiresAt = now.AddDays(300), SignLinkIssuedAt = windowOpen ? now.AddMinutes(-5) : null,
        CustomerResponse = response, QuotationAcceptedAt = acceptedAt,
    };

    [Fact]
    public void MED1_ป้ายปุ่มตัวตัดสินเดียว_ช่วงตอบรับปิดหรือตอบแล้ว_ดูเอกสารเท่านั้น()
    {
        var now = DateTime.UtcNow;
        Assert.Equal("ดู/ยอมรับ", CustomerShareRule.PageButtonLabel("th", Quote(now, windowOpen: true), now));
        // ทิศตรงข้าม: ไม่ได้เปิดให้ตอบรับ · ยอมรับแล้ว · ไม่รับ/ขอแก้แล้ว ⇒ "ดูเอกสาร" (ไม่ชวนกดปุ่มที่หน้าไม่มี)
        Assert.Equal("ดูเอกสาร", CustomerShareRule.PageButtonLabel("th", Quote(now, windowOpen: false), now));
        Assert.Equal("ดูเอกสาร", CustomerShareRule.PageButtonLabel("th", Quote(now, true, acceptedAt: now.AddMinutes(-1)), now));
        Assert.Equal("ดูเอกสาร", CustomerShareRule.PageButtonLabel("th", Quote(now, true, response: nameof(CustomerSignRule.Kind.Declined)), now));
        Assert.Equal("ดูเอกสาร", CustomerShareRule.PageButtonLabel("th", Quote(now, true, response: nameof(CustomerSignRule.Kind.ChangeRequested)), now));
        // ใบแจ้งหนี้: ยอดค้าง ⇒ ดู/ชำระ · มีปุ่มชำระแยกบนการ์ด ⇒ ดูเอกสาร · จ่ายครบ ⇒ ดูเอกสาร
        var inv = new Accounting.Models.Entities.Document { DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved, BalanceDue = 500m };
        Assert.Equal("ดู/ชำระเงิน", CustomerShareRule.PageButtonLabel("th", inv, now));
        Assert.Equal("ดูเอกสาร", CustomerShareRule.PageButtonLabel("th", inv, now, separatePayButton: true));
        inv.BalanceDue = 0m;
        Assert.Equal("ดูเอกสาร", CustomerShareRule.PageButtonLabel("th", inv, now));
    }

    [Fact]
    public void MED2_ปุ่มชำระต่อปลายทาง_กลุ่มซ่อนยอดล็อกพร้อมเหตุ_ผู้ใช้11ได้()
    {
        Guid u = Guid.NewGuid(), g = Guid.NewGuid();
        var t = DocumentLineSendRule.Targets(new[] { Opt(u, NotifyEndpointKind.LineUser), Opt(g, NotifyEndpointKind.LineGroup, showAmounts: false) }, DocumentType.Invoice);
        var user = t.Single(x => x.EndpointId == u);
        var group = t.Single(x => x.EndpointId == g);
        Assert.True(user.PayLink);
        Assert.Null(user.PayLinkNote);
        Assert.False(group.PayLink);
        Assert.Equal(DocumentLineSendRule.PayLinkHiddenGroupNote, group.PayLinkNote);
    }

    [Fact]
    public void MED3_ไม่มีลิงก์หน้าเอกสาร_บอกเหตุ_ปุ่มขึ้นสำหรับใบที่ส่งแบบเดิมได้()
    {
        Assert.Equal("ครั้งนี้ไม่มีลิงก์หน้าเอกสาร เพราะ ใบนี้เป็นใบค่าบริการ", CustomerShareRule.NoPageLinkNote("ใบนี้เป็นใบค่าบริการ."));
        Assert.True(CustomerShareRule.ButtonOffered(DocumentType.CreditNote, DocumentStatus.Approved));
        Assert.True(CustomerShareRule.ButtonOffered(DocumentType.BillingNote, DocumentStatus.Approved));
        Assert.False(CustomerShareRule.ButtonOffered(DocumentType.CreditNote, DocumentStatus.Draft));
    }

    [Fact]
    public void LOW2_eTaxโหมดทดสอบ_บอกตามจริง()
    {
        Assert.Contains("โหมดทดสอบ", CustomerShareRule.EmailSentMessage(true, includedRdTimestamp: false));
        Assert.DoesNotContain("รอประทับเวลา", CustomerShareRule.EmailSentMessage(true, false));
        Assert.Contains("รอประทับเวลา", CustomerShareRule.EmailSentMessage(true, true));
        Assert.Equal("ส่งอีเมลแล้ว", CustomerShareRule.EmailSentMessage(false, false));
    }

    [Fact]
    public void LOW7_ซ่อมเรื่องLINE_ผูกรุ่นด้วยmarker_ไม่ตัดลิงก์เซ็นของผู้ใช้11()
    {
        var sql = string.Join("\n", Round259LineMigration.Statements());
        Assert.Contains($"NOT EXISTS (SELECT 1 FROM \"OneShotDataFixes\" WHERE \"Key\" = '{Round259LineMigration.TopicStripMarkerKey}')", sql);
        Assert.Contains($"INSERT INTO \"OneShotDataFixes\" (\"Key\", \"AppliedAt\") VALUES ('{Round259LineMigration.TopicStripMarkerKey}'", sql);
        var userBlock = sql[sql.IndexOf("WHERE \"Kind\" = 'LineGroup'", StringComparison.Ordinal)..];
        Assert.DoesNotContain("Sign", userBlock[userBlock.IndexOf("UPDATE", StringComparison.Ordinal)..userBlock.IndexOf("INSERT", StringComparison.Ordinal)]);
    }
}
