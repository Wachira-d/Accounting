using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Pdf;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 267 แพ็ก P5 (pure · DOCUMENT_FLOW §2.12n-P5 · คำตัดสิน 267-1-9 · 267-1-13 · 267-1-14 · 267-1-43 · 267-1-70…267-1-77):
/// ผู้ขายรับทราบ/ไม่รับใบสั่งซื้อ (S31) · บล็อกผู้ขายบนกระดาษ ×2 · แถวหลักฐานของเส้น API · LINE Login จำข้อมูลผู้ลงนาม (state/ตั๋ว/ความยินยอม — S34) ·
/// ใบวางบิลจ่ายทั้งใบ · อากรแสตมป์เตือนเท่านั้น — ทุกข้อมีทิศตรงข้าม
/// </summary>
public class CustomerDocRound267P5Tests
{
    private static readonly DateTime Now = new(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("ไม่พบรากเรพ");
    }

    private static CustomerDocPage.Facts PoFacts(DateTime? respondStart, string? response = null, DateTime? respondedAt = null,
        CustomerDocPage.Mode mode = CustomerDocPage.Mode.Link, DocumentStatus status = DocumentStatus.Approved)
        => new(DocumentType.PurchaseOrder, status, mode, false, false, null, false, null, respondStart, respondedAt,
            "/api/companies/x/public-pay/documents/t/respond", CustomerResponse: response);

    // ───────── ใบสั่งซื้อ: ปุ่มของผู้ขาย ─────────

    [Fact]
    public void CDP267P5_PoActions_ผู้ซื้อส่งให้ตอบแล้ว_รับทราบและไม่รับเปิด_ยังไม่ส่ง_ปิดพร้อมเหตุ()
    {
        var open = CustomerDocPage.Actions(PoFacts(Now.AddDays(-1)), Now);
        Assert.True(open.Single(a => a.Id == CustomerDocPage.ActionPoAcknowledge).Enabled);
        Assert.True(open.Single(a => a.Id == CustomerDocPage.ActionPoDecline).Enabled);
        Assert.DoesNotContain(open, a => a.Id is CustomerDocPage.ActionAccept or CustomerDocPage.ActionRequestChange or CustomerDocPage.ActionPayOnline);

        // ทิศตรงข้าม: ผู้ซื้อยังไม่ส่งให้ตอบ (ลิงก์ดู ≠ สิทธิ์ตอบ — 267-1-25) ⇒ ปุ่มรับทราบปิดพร้อมเหตุ · ไม่มีปุ่มไม่รับ
        var notSent = CustomerDocPage.Actions(PoFacts(null), Now);
        var ack = notSent.Single(a => a.Id == CustomerDocPage.ActionPoAcknowledge);
        Assert.False(ack.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(ack.DisabledReason));
        Assert.DoesNotContain(notSent, a => a.Id == CustomerDocPage.ActionPoDecline);

        // พ้น 14 วันจากวันส่ง ⇒ ปิด
        var late = CustomerDocPage.Actions(PoFacts(Now.AddDays(-CustomerDocPage.PoAckWindowDays - 1)), Now);
        Assert.False(late.Single(a => a.Id == CustomerDocPage.ActionPoAcknowledge).Enabled);
    }

    [Fact]
    public void CDP267P5_PoAnswered_รับทราบแล้ว_หรือไม่รับแล้ว_ปิดทั้งคู่()
    {
        var acked = CustomerDocPage.Actions(PoFacts(Now.AddDays(-1), nameof(CustomerSignRule.Kind.PoAcknowledged), Now.AddHours(-1)), Now);
        var a1 = acked.Single(a => a.Id == CustomerDocPage.ActionPoAcknowledge);
        Assert.False(a1.Enabled);
        Assert.Contains("รับทราบใบสั่งซื้อนี้แล้ว", a1.DisabledReason);
        Assert.DoesNotContain(acked, a => a.Id == CustomerDocPage.ActionPoDecline);

        var declined = CustomerDocPage.Actions(PoFacts(Now.AddDays(-1), nameof(CustomerSignRule.Kind.PoDeclined)), Now);
        var a2 = declined.Single(a => a.Id == CustomerDocPage.ActionPoAcknowledge);
        Assert.False(a2.Enabled);
        Assert.Contains("ไม่รับใบสั่งซื้อนี้แล้ว", a2.DisabledReason);
        Assert.DoesNotContain(declined, a => a.Id == CustomerDocPage.ActionPoDecline);

        Assert.Equal(Now, CustomerDocPage.RespondedAt(DocumentType.PurchaseOrder, null, null, nameof(CustomerSignRule.Kind.PoAcknowledged), Now));
        Assert.Null(CustomerDocPage.RespondedAt(DocumentType.PurchaseOrder, null, null, nameof(CustomerSignRule.Kind.PoDeclined), Now));
    }

    [Fact]
    public void CDP267P5_KindFor_ใบสั่งซื้อเท่านั้น()
    {
        Assert.Equal(CustomerSignRule.Kind.PoAcknowledged, CustomerSignRule.KindFor(CustomerDocPage.ActionPoAcknowledge, DocumentType.PurchaseOrder));
        Assert.Equal(CustomerSignRule.Kind.PoDeclined, CustomerSignRule.KindFor(CustomerDocPage.ActionPoDecline, DocumentType.PurchaseOrder));
        Assert.Null(CustomerSignRule.KindFor(CustomerDocPage.ActionPoAcknowledge, DocumentType.Quotation));
        Assert.Null(CustomerSignRule.KindFor(CustomerDocPage.ActionAccept, DocumentType.PurchaseOrder));
        Assert.True(CustomerSignRule.IsSigningKind(CustomerSignRule.Kind.PoAcknowledged));
        Assert.False(CustomerSignRule.IsSigningKind(CustomerSignRule.Kind.PoDeclined));
    }

    /// <summary>S31 — วันส่งที่ผู้ขายยืนยันก่อนวันที่ใบสั่งซื้อ = ปฏิเสธ · วันเดียวกัน/หลัง/ว่าง = ผ่าน</summary>
    [Fact]
    public void CDP267_PoAckDateNotBeforePo()
    {
        var poDate = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        var (d1, e1) = CustomerSignRule.DecidePromisedDate(poDate.AddDays(-1), poDate, false);
        Assert.Null(d1);
        Assert.Contains("ไม่ก่อนวันที่ใบสั่งซื้อ", e1);
        Assert.Contains("before the purchase order date", CustomerSignRule.DecidePromisedDate(poDate.AddDays(-1), poDate, true).Error);

        var sameDay = CustomerSignRule.DecidePromisedDate(poDate.AddHours(15), poDate, false);
        Assert.Null(sameDay.Error);
        Assert.Equal(poDate, sameDay.Date);
        Assert.Equal(poDate.AddDays(5), CustomerSignRule.DecidePromisedDate(poDate.AddDays(5), poDate, false).Date);
        var none = CustomerSignRule.DecidePromisedDate(null, poDate, false);
        Assert.Null(none.Date);
        Assert.Null(none.Error);
        Assert.NotNull(CustomerSignRule.DecidePromisedDate(poDate.AddYears(4), poDate, false).Error);   // ปีผิด
    }

    [Fact]
    public void CDP267P5_PoDeclineต้องมีเหตุ_รับทราบไม่ต้อง()
    {
        Assert.NotNull(CustomerSignRule.DecideReason(CustomerSignRule.Kind.PoDeclined, null, null, false).Error);
        Assert.Equal("ของหมด", CustomerSignRule.DecideReason(CustomerSignRule.Kind.PoDeclined, null, "ของหมด", false).Message);
        Assert.Null(CustomerSignRule.DecideReason(CustomerSignRule.Kind.PoAcknowledged, null, null, false).Error);
        // ทิศตรงข้าม: ขอแก้ใบเสนอราคายังต้องมีข้อความ (ข้อความเดิม)
        Assert.Contains("สิ่งที่ต้องการให้แก้ไข", CustomerSignRule.DecideReason(CustomerSignRule.Kind.ChangeRequested, null, null, false).Error);
    }

    [Fact]
    public void CDP267P5_SignRequestKinds_ใบสั่งซื้อ_เหตุการณ์_ป้าย_เปิดตอบใหม่()
    {
        Assert.Same(SignRequestKinds.PurchaseOrderAck, SignRequestKinds.For(DocumentType.PurchaseOrder));
        Assert.Equal("purchase_order.acknowledged", SignRequestKinds.EventFor(CustomerSignRule.Kind.PoAcknowledged));
        Assert.Equal("purchase_order.declined", SignRequestKinds.EventFor(CustomerSignRule.Kind.PoDeclined));
        Assert.Null(SignRequestKinds.IssueRefusal(DocumentType.PurchaseOrder, DocumentStatus.Approved));
        Assert.NotNull(SignRequestKinds.IssueRefusal(DocumentType.PurchaseOrder, DocumentStatus.Draft));

        var declined = new Document { DocumentType = DocumentType.PurchaseOrder, CustomerResponse = nameof(CustomerSignRule.Kind.PoDeclined), CustomerResponseAt = Now };
        Assert.Equal("declined", SignRequestKinds.Status(declined, Now)!.Code);
        SignRequestKinds.MarkIssued(declined, Now);   // ผู้ซื้อส่งให้ตอบใหม่ ⇒ ล้าง "ไม่รับ"
        Assert.Null(declined.CustomerResponse);

        var acked = new Document { DocumentType = DocumentType.PurchaseOrder, CustomerResponse = nameof(CustomerSignRule.Kind.PoAcknowledged), CustomerResponseAt = Now };
        Assert.Equal(Now, SignRequestKinds.Read(acked).SignedAt);
        SignRequestKinds.MarkIssued(acked, Now);      // ทิศตรงข้าม: "รับทราบ" คงอยู่ (ล้างได้ด้วยการออก Rev เท่านั้น)
        Assert.Equal(nameof(CustomerSignRule.Kind.PoAcknowledged), acked.CustomerResponse);
        Assert.Equal("signed", SignRequestKinds.Status(acked, Now)!.Code);
    }

    [Fact]
    public void CDP267P5_ข้อความรับรองของผู้ขาย_ยินยอม_ป้าย()
    {
        var att = CustomerSignText.Attestation(false, CustomerSignRule.Kind.PoAcknowledged, "บริษัท ผู้ขาย จำกัด", true);
        Assert.Contains("เป็นบุคคลของ บริษัท ผู้ขาย จำกัด", att);
        Assert.Contains("รับทราบและยืนยันใบสั่งซื้อ", att);
        Assert.Contains("มาตรา 9", CustomerSignText.Consent(false, CustomerSignRule.Kind.PoAcknowledged));
        Assert.Contains("พิมพ์บนเอกสาร", CustomerSignText.Consent(false, CustomerSignRule.Kind.PoAcknowledged));
        Assert.Contains("ไม่มีการพิมพ์ชื่อ", CustomerSignText.Consent(false, CustomerSignRule.Kind.PoDeclined));   // ทิศตรงข้าม: ไม่รับ ไม่พิมพ์ชื่อ
        Assert.Equal("ผู้ขายรับทราบใบสั่งซื้อ", CustomerSignText.KindLabel(false, CustomerSignRule.Kind.PoAcknowledged));
        Assert.DoesNotContain("ม.26", att + CustomerSignText.Consent(false, CustomerSignRule.Kind.PoAcknowledged));   // 267-1-18
    }

    // ───────── กระดาษ ×2: ผู้ขายรับทราบใบสั่งซื้อ ─────────

    [Fact]
    public void CDP267P5_BothRenderersPrintSupplierAck_ป้ายจากDocumentLabels_HtmlEncode_ไม่พิมพ์IP()
    {
        var s = new PdfGenerationService.DocumentSigner(null, null, "สมชาย <script>x</script>", "ผู้จัดการขาย", OnBehalfOf: "บริษัท ผู้ขาย จำกัด",
            EvidenceRef: "ABCD1234", SupplierAckAt: Now, SupplierPromisedDate: new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc));
        foreach (var lang in new[] { "th", "en" })
        {
            var L = DocumentLabels.For(lang);
            var lines = PdfGenerationService.SupplierAckLines(s, L);
            Assert.Equal(new[]
            {
                $"{L.PoAckBy}: สมชาย <script>x</script>", L.SignerPosition("ผู้จัดการขาย"), L.SignedOnBehalfOf("บริษัท ผู้ขาย จำกัด"),
                L.PoAckedOnline(Now), L.PoAckPromisedDate(new DateTime(2026, 11, 20)), L.SignedOnlineEvidenceRef("ABCD1234"),
            }, lines);
            var html = PdfGenerationService.PoAcknowledgementBlockHtml(s, L);
            Assert.Contains(System.Net.WebUtility.HtmlEncode(L.PoAckHeading), html);
            Assert.All(lines, line => Assert.Contains(System.Net.WebUtility.HtmlEncode(line), html));
            Assert.DoesNotContain("<script>", html);          // S30 คลาสเดียวกัน — ชื่อที่ผู้ขายพิมพ์ถูก encode
        }
        Assert.Equal("รับทราบออนไลน์ 13/11/2569 10:00 น.", DocumentLabels.For("th").PoAckedOnline(Now));
        Assert.Equal("กำหนดส่งที่ผู้ขายยืนยัน 20/11/2569", DocumentLabels.For("th").PoAckPromisedDate(new DateTime(2026, 11, 20)));
        Assert.Equal("Acknowledged online 13 Nov 2026 10:00 (GMT+7)", DocumentLabels.For("en").PoAckedOnline(Now));

        // ทิศตรงข้าม: ไม่มีการรับทราบ ⇒ ไม่มีบรรทัด/ไม่มีผู้รับทราบใน signers
        Assert.Empty(PdfGenerationService.SupplierAckLines(new PdfGenerationService.DocumentSigner(null, null, "x", null), DocumentLabels.For("th")));
        var none = new List<PdfGenerationService.DocumentSigner> { new(null, null, "a", null), new(null, null, "b", null) };
        Assert.Null(PdfGenerationService.SupplierAckOf(none));
        none.Add(new(null, null, null, null));
        none.Add(s);
        Assert.Same(s, PdfGenerationService.SupplierAckOf(none));

        // สอง renderer เรียกตัวตั้งเดียว (ไม่มี literal ของป้าย)
        var root = RepoRoot();
        var stamp = File.ReadAllText(Path.Combine(root, "Accounting/Services/Implementations/PdfGenerationService.Stamp.cs"));
        var quest = File.ReadAllText(Path.Combine(root, "Accounting/Services/Implementations/PdfGenerationService.DocumentRenderer.cs"));
        var html1 = File.ReadAllText(Path.Combine(root, "Accounting/Services/Implementations/PdfGenerationService.cs"));
        Assert.Contains("foreach (var line in SupplierAckLines(s, L))", stamp);
        Assert.Contains("foreach (var line in SupplierAckLines(s, L))", quest);
        Assert.Contains("PoAcknowledgementBlockHtml(supplierAck, L)", html1);
        Assert.Contains("ComposePoAcknowledgementBlock(col, signers, lang)", quest);
        foreach (var src in new[] { stamp, quest }) Assert.DoesNotContain("Text(\"ผู้ขายรับทราบ", src);
    }

    // ───────── เส้น API (ExternalApprovalController) ลงแถวหลักฐานชุดเดียวกัน ─────────

    [Fact]
    public void CDP267P5_ApiEvidence_ช่องทางApi_ตัวตนที่ระบบภายนอกแจ้ง_ไม่แต่งว่าติ๊กรับรอง()
    {
        var doc = new Document { Id = Guid.NewGuid(), CompanyId = Guid.NewGuid(), DocumentType = DocumentType.Quotation, RevisionNumber = 2 };
        var ev = CustomerDocumentResponseService.ApiEvidence(doc, "บริษัท ลูกค้า จำกัด", "v2:abc", "  สมศรี  ", "ผู้อำนวยการ", "S@Buyer.co.th", "ok",
            "sigsha", "198.51.100.7", "user-1", Now);
        Assert.Equal(nameof(CustomerSignRule.Kind.Accepted), ev.Action);
        Assert.Equal(CustomerSignRule.ChannelApi, ev.Channel);
        Assert.Equal(CustomerSignRule.IdentityApiReported, ev.IdentityLevel);
        Assert.Equal("สมศรี", ev.SignerFirstName);
        Assert.Equal("s@buyer.co.th", ev.SignerEmail);
        Assert.Equal(2, ev.RevisionNumber);
        Assert.Null(ev.AttestationVersion);          // ไม่มีการติ๊กบนหน้าของเรา
        Assert.Null(ev.AttestationTextHash);
        Assert.Equal(64, ev.TokenHash.Length);
        Assert.NotEqual(CustomerSignRule.IdentityFull, ev.IdentityLevel);   // ทิศตรงข้าม: กระดาษพิมพ์ "ผู้ลงนามครบ" เฉพาะ IdentityFull
    }

    // ───────── LINE Login จำข้อมูลผู้ลงนาม (267-1-13) ─────────

    [Fact]
    public void CDP267P5_SignerState_ผูกบริษัทและใบ_หมดอายุ_แก้ไม่ได้()
    {
        var cid = Guid.NewGuid();
        var state = CustomerSignerProfileRule.NewState(cid, "hashA", Now);
        Assert.True(CustomerSignerProfileRule.StateValid(state, cid, "hashA", Now.AddMinutes(5)));
        Assert.False(CustomerSignerProfileRule.StateValid(state, cid, "hashB", Now));                 // ใบอื่น
        Assert.False(CustomerSignerProfileRule.StateValid(state, Guid.NewGuid(), "hashA", Now));      // บริษัทอื่น
        Assert.False(CustomerSignerProfileRule.StateValid(state, cid, "hashA", Now.AddMinutes(CustomerSignerProfileRule.StateTtlMinutes + 1)));
        Assert.False(CustomerSignerProfileRule.StateValid(state[..^1] + (state[^1] == 'a' ? 'b' : 'a'), cid, "hashA", Now));
        Assert.False(CustomerSignerProfileRule.StateValid(null, cid, "hashA", Now));
        Assert.NotEqual(state, CustomerSignerProfileRule.NewState(cid, "hashA", Now));               // nonce สุ่ม
    }

    [Fact]
    public void CDP267P5_SignerTicket_ไปกลับ_ผิดใบ_หมดอายุ()
    {
        var cid = Guid.NewGuid();
        var subject = CustomerSignerProfileRule.SubjectKey("1650000000", "U1234");
        Assert.Equal(64, subject.Length);
        Assert.Equal(subject, CustomerSignerProfileRule.SubjectKey("1650000000", "U1234"));
        Assert.NotEqual(subject, CustomerSignerProfileRule.SubjectKey("1650000001", "U1234"));      // ช่องอื่น = คนละกุญแจ
        Assert.DoesNotContain("U1234", subject);                                                     // ไม่เก็บ LINE userId ตรง
        var ticket = CustomerSignerProfileRule.IssueTicket(cid, "hashA", subject, Now);
        Assert.Equal(subject, CustomerSignerProfileRule.ReadTicket(ticket, cid, "hashA", Now.AddMinutes(10)));
        Assert.Null(CustomerSignerProfileRule.ReadTicket(ticket, cid, "hashB", Now));
        Assert.Null(CustomerSignerProfileRule.ReadTicket(ticket, Guid.NewGuid(), "hashA", Now));
        Assert.Null(CustomerSignerProfileRule.ReadTicket(ticket, cid, "hashA", Now.AddMinutes(CustomerSignerProfileRule.TicketTtlMinutes + 1)));
        var forged = ticket.Split('.');
        Assert.Null(CustomerSignerProfileRule.ReadTicket($"{forged[0]}.{CustomerSignerProfileRule.SubjectKey("x", "y")}.{forged[2]}", cid, "hashA", Now));
    }

    [Fact]
    public void CDP267P5_ค่าที่เติมไม่มีช่องรับรอง_ความยินยอมบอกว่าต้องติ๊กเอง_ลบเมื่อไม่ใช้24เดือน()
    {
        // คำสั่งเจ้าของ: เติม ≠ ลงนาม — ค่าที่เติมมีแค่ชื่อ/นามสกุล/ตำแหน่ง/อีเมล
        var props = typeof(CustomerSignerProfileRule.Prefill).GetProperties().Select(p => p.Name).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "Email", "FirstName", "LastName", "Title" }, props);
        Assert.DoesNotContain(typeof(CustomerSignerProfile).GetProperties(), p => p.Name.Contains("Attest", StringComparison.OrdinalIgnoreCase));
        var text = CustomerSignerProfileRule.ConsentText(false, "บริษัท ผู้ขาย จำกัด");
        Assert.Contains("ติ๊กรับรองเองทุกครั้ง", text);
        Assert.Contains("ถอนความยินยอมได้ทุกเมื่อ", text);
        Assert.Contains("24 เดือน", text);
        Assert.Contains("tick the declaration myself", CustomerSignerProfileRule.ConsentText(true, null));

        Assert.True(CustomerSignerProfileRule.ShouldPurge(Now.AddMonths(-25), consentActive: true, Now));
        Assert.True(CustomerSignerProfileRule.ShouldPurge(Now, consentActive: false, Now));
        Assert.False(CustomerSignerProfileRule.ShouldPurge(Now.AddMonths(-23), consentActive: true, Now));   // ทิศตรงข้าม

        var url = CustomerSignerProfileRule.AuthorizeUrl("1650", "https://app.nextacc.test/pages/pay-document.html", "s.t.u");
        Assert.StartsWith("https://access.line.me/oauth2/v2.1/authorize?", url);
        Assert.Contains("scope=openid", url);
        Assert.DoesNotContain("profile", url);       // ไม่ขอชื่อ/อีเมลจาก LINE (§14.1)
        Assert.Contains(Uri.EscapeDataString("https://app.nextacc.test/pages/pay-document.html"), url);

        var h1 = CustomerSignerProfileRule.ConsentEvidenceHash(Guid.Empty, "k", text, Now);
        Assert.Equal(h1, CustomerSignerProfileRule.ConsentEvidenceHash(Guid.Empty, "k", text, Now));
        Assert.NotEqual(h1, CustomerSignerProfileRule.ConsentEvidenceHash(Guid.Empty, "k", text + ".", Now));
    }

    [Fact]
    public void CDP267P5_ConfigureKey_ว่างโยน()
        => Assert.Throws<InvalidOperationException>(() => CustomerSignerProfileRule.ConfigureKey(" "));

    // ───────── ใบวางบิลจ่ายทั้งใบ (267-1-9 v2) ─────────

    private static readonly Guid Buyer = Guid.NewGuid();

    private static BillingNoteCombinedPay.Child Inv(string no, decimal balance, decimal wht = 0m, string? refusal = null, Guid? contact = null,
        string? currency = "THB", bool superseded = false, DocumentStatus status = DocumentStatus.Approved)
        => new(Guid.NewGuid(), no, DocumentType.Invoice, status, contact ?? Buyer, currency, balance, wht, refusal, superseded);

    [Fact]
    public void CDP267P5_BillingNotePlan_จ่ายรวมใบที่ค้าง_ข้ามใบที่จ่ายแล้ว()
    {
        var a = Inv("INV-1", 1_070m);
        var paid = Inv("INV-2", 0m, refusal: "ชำระครบแล้ว");
        var c = Inv("INV-3", 535.005m);
        var voided = Inv("INV-4", 100m, status: DocumentStatus.Voided, refusal: "ยกเลิก");
        var plan = BillingNoteCombinedPay.Decide(Buyer, new[] { a, paid, c, voided });
        Assert.True(plan.Payable);
        Assert.Null(plan.Refusal);
        Assert.Equal(new[] { a.Id, c.Id }, plan.Allocations.Select(x => x.DocumentId));
        Assert.Equal(535.01m, plan.Allocations[1].Amount);   // AwayFromZero
        Assert.Equal(1_605.01m, plan.Total);
        Assert.Null(BillingNoteCombinedPay.SettleRefusal(1_605.01m, plan, "BN-1"));
        Assert.NotNull(BillingNoteCombinedPay.SettleRefusal(1_605.02m + 0.01m, plan, "BN-1"));   // เงินเข้าไม่ตรงยอดรวม = ล้มดัง
        Assert.NotNull(BillingNoteCombinedPay.SettleRefusal(1_000m, plan, "BN-1"));

        var allPaid = BillingNoteCombinedPay.Decide(Buyer, new[] { paid });
        Assert.False(allPaid.Payable);
        Assert.Null(allPaid.Refusal);
        Assert.NotNull(BillingNoteCombinedPay.SettleRefusal(10m, allPaid, "BN-1"));
    }

    [Theory]
    [InlineData("wht")]
    [InlineData("contact")]
    [InlineData("currency")]
    [InlineData("superseded")]
    [InlineData("refusal")]
    public void CDP267P5_BillingNotePlan_ชุดใบที่จ่ายรวมไม่ได้_เหตุพร้อมทางไปต่อรายใบ(string why)
    {
        var bad = why switch
        {
            "wht" => Inv("INV-9", 1_000m, wht: 30m),
            "contact" => Inv("INV-9", 1_000m, contact: Guid.NewGuid()),
            "currency" => Inv("INV-9", 1_000m, currency: "USD"),
            "superseded" => Inv("INV-9", 1_000m, superseded: true),
            _ => Inv("INV-9", 1_000m, refusal: "ผู้ขายยังไม่เปิดรับชำระ"),
        };
        var plan = BillingNoteCombinedPay.Decide(Buyer, new[] { Inv("INV-1", 500m), bad });
        Assert.False(plan.Payable);
        Assert.Contains("INV-9", plan.Refusal);
        Assert.EndsWith(BillingNoteCombinedPay.PayEachHint, plan.Refusal);
        Assert.Empty(plan.Allocations);
        Assert.NotNull(BillingNoteCombinedPay.SettleRefusal(1_500m, plan, "BN-1"));
    }

    [Fact]
    public void CDP267P5_BillingNotePage_ปุ่มจ่ายทั้งใบ_ไม่มีฟอร์มสลิปรวม_ใบแจ้งหนี้ยังมีสลิป()
    {
        var bn = CustomerDocPage.Actions(new CustomerDocPage.Facts(DocumentType.BillingNote, DocumentStatus.Approved, CustomerDocPage.Mode.Link, false,
            true, null, false, null, null, null, null), Now);
        var pay = bn.Single(a => a.Id == CustomerDocPage.ActionPayOnline);
        Assert.True(pay.Enabled);
        Assert.Equal("ชำระทั้งใบวางบิล", pay.Label);
        Assert.DoesNotContain(bn, a => a.Id == CustomerDocPage.ActionSubmitSlip);

        // ทิศตรงข้าม: ใบแจ้งหนี้ยังมีปุ่มจ่าย + แจ้งโอน (ป้ายเดิม) · ใบวางบิลจ่ายรวมไม่ได้ ⇒ ปุ่มปิดพร้อมเหตุ
        var inv = CustomerDocPage.Actions(new CustomerDocPage.Facts(DocumentType.Invoice, DocumentStatus.Approved, CustomerDocPage.Mode.Link, false,
            true, null, true, null, null, null, null), Now);
        Assert.Equal("ชำระเงินออนไลน์", inv.Single(a => a.Id == CustomerDocPage.ActionPayOnline).Label);
        Assert.True(inv.Single(a => a.Id == CustomerDocPage.ActionSubmitSlip).Enabled);
        var refused = CustomerDocPage.Actions(new CustomerDocPage.Facts(DocumentType.BillingNote, DocumentStatus.Approved, CustomerDocPage.Mode.Link, false,
            false, "ใบ INV-9 มีภาษีหัก ณ ที่จ่าย" + BillingNoteCombinedPay.PayEachHint, false, null, null, null, null), Now);
        var p2 = refused.Single(a => a.Id == CustomerDocPage.ActionPayOnline);
        Assert.False(p2.Enabled);
        Assert.Contains("ชำระรายใบ", p2.DisabledReason);
    }

    // ───────── อากรแสตมป์ (267-1-14) — เตือนเท่านั้น ─────────

    [Fact]
    public void CDP267P5_StampDuty_เตือนเท่านั้น_ไม่ใช่ด่าน()
    {
        Assert.True(CustomerSignRule.StampDutyWarn(DocumentType.Quotation, CustomerSignRule.Kind.Accepted, true));
        Assert.False(CustomerSignRule.StampDutyWarn(DocumentType.PurchaseOrder, CustomerSignRule.Kind.PoAcknowledged, true));
        // ตัวเขียนคำตอบไม่อ้างคำเตือนนี้ในเส้นยอมรับ (ไม่มีทางกลายเป็นด่าน) — อ้างเฉพาะแผงพนักงาน/การแจ้งเตือน
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/Services/Implementations/CustomerDocumentResponseService.cs"));
        var accept = src[src.IndexOf("public async Task<Outcome> Accept(", StringComparison.Ordinal)..src.IndexOf("public async Task<Outcome> DeclineOrRequestChange(", StringComparison.Ordinal)];
        Assert.DoesNotContain("StampDuty", accept);
    }
}
