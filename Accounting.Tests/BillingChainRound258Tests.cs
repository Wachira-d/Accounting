using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 258-3 "เส้นบิลอัตโนมัติจนจบ" (erp-review/2026-11-11/team-F-billing-chain.md) — ตัวตัดสิน pure ทั้งสองทิศ:
/// ใบเสร็จหลังรับชำระทาง LINE (<see cref="PostPaymentAutomationPolicy"/> ขั้น LineReceipt · <see cref="ContactLineChannel.LineEndpoints"/>) ·
/// ธงส่งของรายการประจำ ⇒ LINE (<see cref="ContactLineChannel.InlineChannels"/>) · ลิงก์ดู PDF จากการ์ด (<see cref="LineDocumentViewLink"/>) · การ์ดใบเสร็จ
/// </summary>
public class BillingChainRound258Tests
{
    private static PostPaymentAutomationPolicy.Facts F(string? email = "ap@buyer.co.th", int line = 0, bool hasReceipt = false, bool receiptDraft = false,
        bool serves = false, string? autoOff = null)
        => new(SaleSide: true, SourceStatus: DocumentStatus.Paid, PaymentVoided: false, FullyPaid: true, AllocationCount: 0,
            HasReceipt: hasReceipt, ReceiptIsDraft: receiptDraft, ReceiptCarriesVat: false, SourceServesAsReceipt: serves, MayCarryVatIfIssued: false,
            CustomerEmail: email, BuyerBlockingFields: null, PaymentWht: 0m, WhtCreditRegistered: true,
            Source: PaymentEntrySource.Gateway, SupersedingTaxInvoiceNumber: null, AutoSendBlockedReason: autoOff, LineReceiptEndpoints: line);

    private static readonly PostPaymentAutomationPolicy.Settings On = new(EmailReceipt: true, IncludePartial: false);

    [Fact]
    public void ไม่มีอีเมลแต่มี_LINE_ออกใบเสร็จแล้วส่งทาง_LINE_ไม่หยุด()
    {
        var p = PostPaymentAutomationPolicy.Decide(On, F(email: null, line: 1));
        Assert.Equal(new[] { PostPaymentAutomationPolicy.Step.IssueReceipt, PostPaymentAutomationPolicy.Step.LineReceipt }, p.Steps);
        Assert.Empty(p.Blocked);
        Assert.Contains(PostPaymentAutomationPolicy.LineOnlyNote, p.Notes);
    }

    [Fact]
    public void มีทั้งอีเมลและ_LINE_LINE_มาก่อนอีเมล_ไม่มีหมายเหตุ_LINE_อย่างเดียว()
    {
        var p = PostPaymentAutomationPolicy.Decide(On, F(line: 2));
        Assert.Equal(new[] { PostPaymentAutomationPolicy.Step.IssueReceipt, PostPaymentAutomationPolicy.Step.LineReceipt, PostPaymentAutomationPolicy.Step.EmailReceipt }, p.Steps);
        Assert.DoesNotContain(PostPaymentAutomationPolicy.LineOnlyNote, p.Notes);
        Assert.Equal(new[] { PostPaymentAutomationPolicy.Step.LineReceipt, PostPaymentAutomationPolicy.Step.EmailReceipt },
            PostPaymentAutomationPolicy.Decide(On, F(line: 1, hasReceipt: true)).Steps);
        Assert.Equal(new[] { PostPaymentAutomationPolicy.Step.LineReceipt, PostPaymentAutomationPolicy.Step.EmailSourceAsReceipt },
            PostPaymentAutomationPolicy.Decide(On, F(line: 1, serves: true)).Steps);
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่มีอีเมลไม่มี_LINE_หยุดเหมือนเดิม_และไม่มี_LINE_ไม่เพิ่มขั้น()
    {
        var none = PostPaymentAutomationPolicy.Decide(On, F(email: null, line: 0));
        Assert.Empty(none.Steps);
        Assert.Contains(none.Blocked, b => b.StartsWith("ลูกค้าไม่มีอีเมลที่ใช้ได้", StringComparison.Ordinal));
        Assert.Equal(new[] { PostPaymentAutomationPolicy.Step.IssueReceipt, PostPaymentAutomationPolicy.Step.EmailReceipt },
            PostPaymentAutomationPolicy.Decide(On, F()).Steps);
    }

    [Fact]
    public void ผู้ติดต่อไม่ส่งอัตโนมัติ_ตัด_LINE_ด้วย_ใบเสร็จร่าง_ไม่ส่ง_LINE()
    {
        var off = PostPaymentAutomationPolicy.Decide(On, F(line: 1, autoOff: "ผู้ติดต่อตั้งไม่ส่งอัตโนมัติ"));
        Assert.DoesNotContain(PostPaymentAutomationPolicy.Step.LineReceipt, off.Steps);
        Assert.Contains(off.Blocked, b => b.StartsWith(PostPaymentAutomationPolicy.AutoSendBlockedPrefix, StringComparison.Ordinal));
        var draft = PostPaymentAutomationPolicy.Decide(On, F(line: 1, hasReceipt: true, receiptDraft: true));
        Assert.Empty(draft.Steps);
        Assert.NotEmpty(draft.Blocked);
        Assert.Empty(PostPaymentAutomationPolicy.Decide(new PostPaymentAutomationPolicy.Settings(false, false), F(line: 1)).Steps);
    }

    private static ContactRecipientRule.RecipientOption Opt(string kind, bool available, Guid? id = null)
        => new(id ?? Guid.NewGuid(), kind, kind == NotifyEndpointKind.Email ? "a@b.co" : null, kind, available, available ? null : "ไม่ได้ติ๊กเรื่องนี้", null, true, false);

    [Fact]
    public void ปลายทาง_LINE_นับเฉพาะที่ใช้ได้และเป็น_LINE()
    {
        var opts = new[] { Opt(NotifyEndpointKind.Email, true), Opt(NotifyEndpointKind.LineUser, true), Opt(NotifyEndpointKind.LineGroup, false) };
        var eps = ContactLineChannel.LineEndpoints(opts);
        Assert.Single(eps);
        Assert.Equal(NotifyEndpointKind.LineUser, eps[0].Kind);
        Assert.Equal((true, false), ContactLineChannel.InlineChannels(opts));
        Assert.Equal((false, false), ContactLineChannel.InlineChannels(new[] { Opt(NotifyEndpointKind.Email, true), Opt(NotifyEndpointKind.LineUser, false) }));
    }

    [Fact]
    public void trigger_ใบเสร็จ_เรื่องใบเสร็จ_ผู้ส่งรับ_แต่ฟอร์มกฎตารางส่งไม่เปิด()
    {
        Assert.Equal(NotifyTopic.Receipt, NotifyTopics.ForTrigger(ContactLineChannel.ReceiptTrigger));
        Assert.True(ContactLineChannel.SenderSupports(ContactLineChannel.ReceiptTrigger));
        Assert.False(ContactLineChannel.SupportsLine(ContactLineChannel.ReceiptTrigger));
        Assert.Contains(NotifyTopic.Receipt, NotifyTopics.DefaultsFor(NotifyEndpointKind.LineUser));
        var id = Guid.NewGuid();
        Assert.Equal(ContactLineChannel.ReceiptBaseKey(id), ContactLineChannel.ReceiptBaseKey(id));
        Assert.NotEqual(ContactLineChannel.QueueKey(ContactLineChannel.ReceiptBaseKey(id), Guid.NewGuid()),
            ContactLineChannel.QueueKey(ContactLineChannel.ReceiptBaseKey(id), Guid.NewGuid()));
    }

    // ───── ลิงก์ดู PDF ─────

    [Fact]
    public void ลิงก์ดู_PDF_ตรวจคืนได้_ผูกบริษัทและแถวคิว()
    {
        var key = LineDocumentViewLink.DeriveKey("server-secret-for-tests-0123456789");
        var c = Guid.NewGuid();
        var q = Guid.NewGuid();
        var t = LineDocumentViewLink.Token(key, c, q);
        Assert.True(LineDocumentViewLink.LooksValid(t));
        Assert.True(LineDocumentViewLink.Matches(key, c, q, t));
        Assert.False(LineDocumentViewLink.Matches(key, Guid.NewGuid(), q, t));
        Assert.False(LineDocumentViewLink.Matches(key, c, Guid.NewGuid(), t));
        Assert.False(LineDocumentViewLink.Matches(LineDocumentViewLink.DeriveKey("another-secret-xxxxxxxxxxxxxxx"), c, q, t));
        Assert.False(LineDocumentViewLink.Matches(key, c, q, t.ToUpperInvariant()));
        Assert.False(LineDocumentViewLink.LooksValid("../etc"));
        Assert.Throws<InvalidOperationException>(() => LineDocumentViewLink.DeriveKey(""));
        Assert.Null(LineDocumentViewLink.Url(null, c, q, t));
        Assert.StartsWith("https://app.test/api/companies/", LineDocumentViewLink.Url("https://app.test/", c, q, t), StringComparison.Ordinal);
    }

    [Fact]
    public void ลิงก์ดู_PDF_หมดอายุและแถวที่ยกเลิก_ข้าม_ใช้ไม่ได้()
    {
        var now = new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(LineDocumentViewLink.Fresh(now.AddDays(-10), now));
        Assert.False(LineDocumentViewLink.Fresh(now.AddDays(-(LineDocumentViewLink.TtlDays + 1)), now));
        Assert.True(LineDocumentViewLink.RowStatusAllows(ContactLineChannel.StatusSent));
        Assert.False(LineDocumentViewLink.RowStatusAllows(ContactLineChannel.StatusCancelled));
        Assert.False(LineDocumentViewLink.RowStatusAllows(ContactLineChannel.StatusSkipped));
    }

    // ───── การ์ดใบเสร็จ ─────

    private static LineDocumentCard.Input Card(string? view = "https://app.test/api/companies/x/line-doc/y/z", bool received = true, string lang = "th")
        => new("บริษัท ร้านเรา จำกัด", false, lang, "ใบเสร็จรับเงิน", "RC-2026-0001", 1070m, "THB", null, "ลูกค้า", null, false,
            AmountIsOutstanding: false, ViewUrl: view, PaymentReceived: received);

    private static string Json(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [Fact]
    public void การ์ดใบเสร็จ_มีบรรทัดได้รับชำระแล้ว_และปุ่มดู_PDF_เฉพาะ_https()
    {
        var c = LineDocumentCard.Build(Card());
        var json = Json(c.Contents);
        Assert.Contains("ได้รับชำระเงินแล้ว", json);
        Assert.Contains("ดู/ดาวน์โหลด PDF", json);
        Assert.Contains("ได้รับชำระเงินแล้ว", c.AltText);
        Assert.Contains("Payment received", Json(LineDocumentCard.Build(Card(lang: "en")).Contents));
        Assert.DoesNotContain("ดู/ดาวน์โหลด PDF", Json(LineDocumentCard.Build(Card(view: "http://insecure.test/x")).Contents));
        // ทิศตรงข้าม: การ์ดเอกสารทั่วไป (ไม่ใช่ใบเสร็จ) ไม่มีบรรทัดรับเงิน
        Assert.DoesNotContain("ได้รับชำระเงินแล้ว", Json(LineDocumentCard.Build(Card(received: false)).Contents));
    }

    // ───── F-M2 ป้ายออกใบเสร็จล้ม ─────

    [Fact]
    public void ป้ายออกใบเสร็จล้ม_ต่อท้ายไม่ทับ_แทนป้ายเก่า_ล้างแล้วเหลือหมายเหตุเดิม()
    {
        var once = SettlementReceiptFailedNote.Append("หมายเหตุเดิม", "PAY-001", "เลขเอกสารชน\nบรรทัดสอง");
        Assert.True(SettlementReceiptFailedNote.Has(once));
        Assert.StartsWith("หมายเหตุเดิม", once, StringComparison.Ordinal);
        Assert.DoesNotContain("\nบรรทัดสอง", once);
        var twice = SettlementReceiptFailedNote.Append(once, "PAY-002", "อีกเหตุ");
        Assert.Single(twice.Split(SettlementReceiptFailedNote.Marker).Skip(1));
        Assert.Contains("PAY-002", twice);
        Assert.Equal("หมายเหตุเดิม", SettlementReceiptFailedNote.Clear(twice));
        Assert.Null(SettlementReceiptFailedNote.Clear(SettlementReceiptFailedNote.Append(null, "PAY-1", "x")));
        // ทิศตรงข้าม: หมายเหตุที่ไม่มีป้าย ไม่ถูกแตะ · ป้ายคู่ค้าไม่ถูกนับเป็นป้ายใบเสร็จ
        Assert.Equal("ปกติ", SettlementReceiptFailedNote.Clear("ปกติ"));
        Assert.False(SettlementReceiptFailedNote.Has(PartnerRoutingFailedNote.Build("x")));
    }

    // ───── F-H3 ผู้ขายคู่ค้ารับเงินแล้ว ⇒ ผู้ซื้อบันทึกจ่ายเอง ─────

    [Fact]
    public void แจ้งผู้ขายรับเงิน_ยอดรับบวกภาษีหักของเราเท่ายอดค้าง_จ่ายครบพร้อมหัก()
    {
        // ใบ 1,070 · หัก 3% ของ 1,000 = 30 · ผู้ขายรับ 1,040
        var p = PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1070m, 30m, 1040m, "PI-001");
        Assert.Null(p.Problem);
        Assert.Equal(1040m, p.Amount);
        Assert.Equal(30m, p.Wht);
        // ผู้ซื้อไม่หัก (บุคคลธรรมดา/ไม่มีหน้าที่) ⇒ ผู้ขายรับเต็ม = จ่ายครบไม่หัก
        var full = PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1070m, 0m, 1070m, "PI-001");
        Assert.Equal((1070m, 0m), (full.Amount!.Value, full.Wht!.Value));
    }

    [Fact]
    public void แจ้งผู้ขายรับเงิน_ทิศตรงข้าม_ร่าง_ยกเลิก_ชำระครบ_ยอดไม่ตรง_ไม่บันทึกและบอกทางไปต่อ()
    {
        var draft = PartnerPaymentNoticeRule.For(DocumentStatus.Draft, 1070m, 30m, 1040m, "PI-001");
        Assert.Null(draft.Amount);
        Assert.Contains("อนุมัติใบซื้อก่อน", draft.Problem);
        Assert.Null(PartnerPaymentNoticeRule.For(DocumentStatus.Voided, 1070m, 30m, 1040m, "PI-001").Amount);
        Assert.Contains("ชำระครบแล้ว", PartnerPaymentNoticeRule.For(DocumentStatus.Paid, 0m, 0m, 1040m, "PI-001").Problem);
        var mismatch = PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1070m, 10m, 1040m, "PI-001");
        Assert.Null(mismatch.Amount);
        Assert.Contains("ระบบไม่เดา", mismatch.Problem);
        Assert.Equal("PARTNER-PAID-" + Guid.Empty.ToString("N"), PartnerPaymentNoticeRule.PaymentReference(Guid.Empty));
    }

    // ───── F-M1 ความพร้อมทั้งเส้น ─────

    [Fact]
    public void ความพร้อมทั้งเส้น_แถว_LINE_gateway_คู่ค้า_ตามข้อเท็จจริง_ไม่ตรวจไม่มีแถว()
    {
        AutopilotReadiness.Item? Row(AutopilotReadiness.Report r, string code) => r.Items.FirstOrDefault(i => i.Code == code);
        var on = AutopilotReadiness.Evaluate(new AutopilotReadiness.Facts
        {
            SaleSide = true, DocumentType = DocumentType.Invoice, PostPayEmailReceipt = true,
            LineDocumentEndpoints = 1, LineReceiptEndpoints = 1, GatewayLive = true, PartnerLinked = true,
        });
        Assert.Equal(AutopilotReadiness.Ok, Row(on, "LINE-DOC")!.Verdict);
        Assert.Equal(AutopilotReadiness.Ok, Row(on, "LINE-RECEIPT")!.Verdict);
        Assert.Equal(AutopilotReadiness.Ok, Row(on, "GATEWAY")!.Verdict);
        Assert.Equal(AutopilotReadiness.Ok, Row(on, "PARTNER")!.Verdict);
        var off = AutopilotReadiness.Evaluate(new AutopilotReadiness.Facts
        {
            SaleSide = true, DocumentType = DocumentType.Invoice, PostPayEmailReceipt = true,
            LineDocumentEndpoints = 0, LineReceiptEndpoints = 0, GatewayLive = false, PartnerLinked = false, LineOaProblem = "ยังไม่ได้ตั้ง LINE OA",
        });
        Assert.Equal(AutopilotReadiness.Info, Row(off, "LINE-DOC")!.Verdict);
        Assert.Equal("ยังไม่ได้ตั้ง LINE OA", Row(off, "LINE-DOC")!.Why);
        Assert.Equal(AutopilotReadiness.Info, Row(off, "GATEWAY")!.Verdict);
        Assert.Null(Row(off, "PARTNER"));
        // ทิศตรงข้าม: ไม่ได้ตรวจ (null) ⇒ ไม่มีแถวใหม่ · แถวข้อมูลประกอบไม่นับเป็นคำเตือน
        var unchecked_ = AutopilotReadiness.Evaluate(new AutopilotReadiness.Facts { SaleSide = true, DocumentType = DocumentType.Invoice });
        Assert.DoesNotContain(unchecked_.Items, i => AutopilotReadiness.ChainInfoCodes.Contains(i.Code));
        Assert.Equal(unchecked_.WarnCount, off.WarnCount);
    }
}
