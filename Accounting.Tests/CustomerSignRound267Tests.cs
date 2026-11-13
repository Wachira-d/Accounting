using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Pdf;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 267 แพ็ก P2 — ผู้ลงนาม + บัญชีหลักฐาน + ฉบับที่ลูกค้าเซ็น (pure · คำตัดสิน 267-1-4…6 · 267-1-11 · 267-1-14 · 267-1-17 · 267-1-18 · 267-1-33… ·
/// DOCUMENT_FLOW §2.12n-P2): ด่านผู้ลงนาม (ครบ/ขาดทีละช่อง) · ข้อความรับรอง · เหตุผลปฏิเสธ · ปุ่มของหน้า (ไม่รับ/ขอแก้ · หนึ่งคำตอบต่อ Rev) ·
/// รหัสยืนยันอีเมล · ไฟล์ฉบับที่ลงนาม (ตำแหน่ง/SHA-256) · สอง renderer พิมพ์ผู้ลงนามชุดเดียวกัน · ไม่อ้างกฎหมายเกิน ม.9/ม.11 · ล้างข้อมูลตามคำขอ ·
/// คู่บนฐานจริง: <c>Db/CustomerSignRound267DbTests</c>
/// </summary>
public class CustomerSignRound267Tests
{
    private static readonly DateTime Now = new(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);

    private static CustomerSignRule.SignerInput In(string? first = "สมหญิง", string? last = "ใจดี", string? title = "ผู้จัดการฝ่ายจัดซื้อ",
        bool tick = true, string? version = CustomerSignText.Version, string? email = null)
        => new(first, last, title, tick, version, email);

    // ───────── ด่านผู้ลงนาม (ครบ = ผ่าน · ขาดทีละช่อง = ปฏิเสธ) ─────────

    [Fact]
    public void CSR267_SignerFullIdentity_ครบทุกช่อง_ผ่าน_และชื่อเต็มคือชื่อเว้นวรรคนามสกุล()
    {
        var (s, err) = CustomerSignRule.DecideSigner(In(email: "  Somying@Buyer.co.th "), buyerIsJuristic: true, emailRequired: false, en: false);
        Assert.Null(err);
        Assert.Equal("สมหญิง", s!.FirstName);
        Assert.Equal("ใจดี", s.LastName);
        Assert.Equal("ผู้จัดการฝ่ายจัดซื้อ", s.Title);
        Assert.Equal("สมหญิง ใจดี", s.FullName);
        Assert.Equal("somying@buyer.co.th", s.Email);                                    // ตัดช่องว่าง + ตัวพิมพ์เล็ก
    }

    [Theory]
    [InlineData(null, "ใจดี", "ผจก.", true, CustomerSignText.Version, "ชื่อ")]
    [InlineData("   ", "ใจดี", "ผจก.", true, CustomerSignText.Version, "ชื่อ")]
    [InlineData("สมหญิง", null, "ผจก.", true, CustomerSignText.Version, "นามสกุล")]
    [InlineData("สมหญิง", "ใจดี", null, true, CustomerSignText.Version, "ตำแหน่ง")]
    [InlineData("สมหญิง", "ใจดี", "ผจก.", false, CustomerSignText.Version, "ติ๊กรับรอง")]
    [InlineData("สมหญิง", "ใจดี", "ผจก.", true, "รุ่นเก่า", "ข้อความรับรองถูกปรับปรุง")]
    [InlineData("สมหญิง", "ใจดี", "ผจก.", true, null, "ข้อความรับรองถูกปรับปรุง")]
    public void CSR267_SignerMissingField_ขาดช่องใดช่องหนึ่ง_ปฏิเสธพร้อมบอกช่อง(string? first, string? last, string? title, bool tick, string? version, string expect)
    {
        var (s, err) = CustomerSignRule.DecideSigner(In(first, last, title, tick, version), buyerIsJuristic: true, emailRequired: false, en: false);
        Assert.Null(s);
        Assert.Contains(expect, err);
    }

    [Fact]
    public void CSR267_SignerNullInput_ไม่ส่งผู้ลงนามเลย_ปฏิเสธ()
    {
        var (s, err) = CustomerSignRule.DecideSigner(null, true, false, false);
        Assert.Null(s);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void CSR267_IndividualBuyer_บุคคลธรรมดาเว้นตำแหน่งได้_นิติบุคคลเว้นไม่ได้()
    {
        var (person, e1) = CustomerSignRule.DecideSigner(In(title: null), buyerIsJuristic: false, emailRequired: false, en: false);
        Assert.Null(e1);
        Assert.Equal(CustomerSignText.IndividualTitle(false), person!.Title);
        var (_, e2) = CustomerSignRule.DecideSigner(In(title: null), buyerIsJuristic: true, emailRequired: false, en: false);
        Assert.NotNull(e2);                                                               // ทิศตรงข้าม
    }

    [Theory]
    [InlineData("a@b.co", true)]
    [InlineData("first.last+tag@sub.example.co.th", true)]
    [InlineData("no-at-sign", false)]
    [InlineData("a@b", false)]
    [InlineData("a b@c.com", false)]
    [InlineData("a@b..com", false)]
    [InlineData("<script>@x.com", false)]
    public void CSR267_Email_รูปแบบ(string raw, bool ok)
    {
        var (email, err) = CustomerSignRule.CleanEmail(raw, false);
        Assert.Equal(ok, err == null && email != null);
    }

    [Fact]
    public void CSR267_Email_ว่างไม่ผิด_แต่ถ้าบังคับต้องมี_และผิดรูป_ปฏิเสธ()
    {
        var blank = CustomerSignRule.CleanEmail("   ", false);
        Assert.Null(blank.Email);
        Assert.Null(blank.Error);
        Assert.Null(CustomerSignRule.DecideSigner(In(email: null), true, emailRequired: false, false).Error);
        Assert.Contains("อีเมล", CustomerSignRule.DecideSigner(In(email: null), true, emailRequired: true, false).Error);
        Assert.Contains("อีเมล", CustomerSignRule.DecideSigner(In(email: "ไม่ใช่อีเมล"), true, emailRequired: false, false).Error);
    }

    [Fact]
    public void CSR267_SignerTextCleaned_ตัดอักขระควบคุมและความยาว()
    {
        var (s, err) = CustomerSignRule.DecideSigner(In(first: "สม\nหญิง\t", last: new string('ก', 300)), true, false, false);
        Assert.Null(err);
        Assert.Equal("สม หญิง", s!.FirstName);
        Assert.Equal(CustomerSignRule.NamePartMax, s.LastName.Length);
    }

    // ───────── ข้อความรับรอง (267-1-5) ─────────

    [Fact]
    public void CSR267_Attestation_นิติบุคคลยืนยันทั้งสังกัดและอำนาจ_บุคคลยืนยันตัวหรือผู้รับมอบ()
    {
        var juristic = CustomerSignText.Attestation(false, CustomerSignRule.Kind.Accepted, "บริษัท ลูกค้า จำกัด", true);
        Assert.Contains("เป็นบุคคลของ บริษัท ลูกค้า จำกัด", juristic);
        Assert.Contains("มีอำนาจ", juristic);
        Assert.Contains("ยอมรับใบเสนอราคา", juristic);
        var person = CustomerSignText.Attestation(false, CustomerSignRule.Kind.Accepted, "นาย ก", false);
        Assert.Contains("ข้าพเจ้าคือ นาย ก", person);
        Assert.Contains("ได้รับมอบอำนาจ", person);
        Assert.Contains("รับสินค้า", CustomerSignText.Attestation(false, CustomerSignRule.Kind.DeliverySigned, "บริษัท ลูกค้า จำกัด", true));
        Assert.Contains("authorised", CustomerSignText.Attestation(true, CustomerSignRule.Kind.Accepted, "Buyer Co., Ltd.", true));
        // ไม่มีชื่อผู้ซื้อ ⇒ ไม่พิมพ์ช่องว่าง
        Assert.Contains("ผู้ซื้อตามเอกสารนี้", CustomerSignText.Attestation(false, CustomerSignRule.Kind.Accepted, "  ", true));
    }

    [Fact]
    public void CSR267_AttestationHash_คงที่ต่อข้อความ_ต่างเมื่อข้อความต่าง()
    {
        var a = CustomerSignText.Attestation(false, CustomerSignRule.Kind.Accepted, "บริษัท ก", true);
        Assert.Equal(CustomerSignText.TextHash(a), CustomerSignText.TextHash(a));
        Assert.Equal(64, CustomerSignText.TextHash(a).Length);
        Assert.NotEqual(CustomerSignText.TextHash(a), CustomerSignText.TextHash(CustomerSignText.Attestation(false, CustomerSignRule.Kind.Declined, "บริษัท ก", true)));
        Assert.NotEqual(CustomerSignText.TextHash(a), CustomerSignText.TextHash(CustomerSignText.Attestation(false, CustomerSignRule.Kind.Accepted, "บริษัท ข", true)));
    }

    // ───────── กฎหมายที่อ้างได้ (267-1-18) ─────────

    [Fact]
    public void CSR267_LegalBasisOnlySection9And11_ไม่อ้างเกินจริงทุกข้อความ()
    {
        Assert.Contains("ม.9", CustomerSignText.LegalReference);
        Assert.Contains("ม.11", CustomerSignText.LegalReference);
        Assert.Equal("ETA-9", CustomerSignText.RuleCode);
        var banned = new[] { "ม.26", "มาตรา 26", "section 26", "ที่เชื่อถือได้", "reliable electronic", "ม.28" };
        var texts = AllSignTexts().ToList();
        foreach (var lang in new[] { "th", "en" })
        {
            var L = DocumentLabels.For(lang);
            texts.Add(L.SignerPosition("x"));
            texts.Add(L.SignedOnBehalfOf("x"));
            texts.Add(L.SignedOnlineEvidenceRef("x"));
            texts.Add(L.DnSignedOnline(Now));
            texts.Add(L.QtAcceptHeading);
        }
        Assert.All(texts, t => Assert.All(banned, b => Assert.DoesNotContain(b, t, StringComparison.OrdinalIgnoreCase)));
        // ข้อความยินยอมอ้าง ม.9 + ม.11 จริง และบอกสิ่งที่เก็บ (PDPA ม.23)
        var consent = CustomerSignText.Consent(false, CustomerSignRule.Kind.Accepted);
        Assert.Contains("มาตรา 9", consent);
        Assert.Contains("มาตรา 11", consent);
        Assert.Contains("ที่อยู่ IP", consent);
        // ไฟล์ OWNER ของข้อความ + ป้ายบนกระดาษ ไม่มีคำต้องห้ามแม้ในคอมเมนต์ (กันคนลอกไปใช้)
        var root = RepoRoot();
        foreach (var f in new[] { "Accounting/Helpers/CustomerSignText.cs", "Accounting/Services/Implementations/Pdf/DocumentLabels.cs", "Accounting/wwwroot/pages/pay-document.html" })
        {
            var src = File.ReadAllText(Path.Combine(root, f));
            Assert.DoesNotContain("ม.26", src);
            Assert.DoesNotContain("มาตรา 26", src);
        }
    }

    // ───────── เหตุผลปฏิเสธ / ข้อความขอแก้ ─────────

    [Fact]
    public void CSR267_DeclineReason_รายการปิด_อื่นๆต้องมีข้อความ()
    {
        var price = CustomerSignRule.DecideReason(CustomerSignRule.Kind.Declined, "price", null, false);
        Assert.Equal("price", price.ReasonCode);
        Assert.Null(price.Message);
        Assert.Null(price.Error);
        Assert.NotNull(CustomerSignRule.DecideReason(CustomerSignRule.Kind.Declined, null, "แพง", false).Error);
        Assert.NotNull(CustomerSignRule.DecideReason(CustomerSignRule.Kind.Declined, "ไม่มีในรายการ", "แพง", false).Error);
        Assert.NotNull(CustomerSignRule.DecideReason(CustomerSignRule.Kind.Declined, CustomerSignText.OtherReasonCode, "  ", false).Error);
        var other = CustomerSignRule.DecideReason(CustomerSignRule.Kind.Declined, CustomerSignText.OtherReasonCode, "รอประชุมกรรมการ", false);
        Assert.Null(other.Error);
        Assert.Equal("รอประชุมกรรมการ", other.Message);
    }

    [Fact]
    public void CSR267_ChangeRequest_ต้องมีข้อความ_และตัดความยาว()
    {
        Assert.NotNull(CustomerSignRule.DecideReason(CustomerSignRule.Kind.ChangeRequested, null, null, false).Error);
        var ok = CustomerSignRule.DecideReason(CustomerSignRule.Kind.ChangeRequested, "price", new string('ข', 1500), false);
        Assert.Null(ok.Error);
        Assert.Null(ok.ReasonCode);                                                       // ขอแก้ไม่ใช้รหัสเหตุผล
        Assert.Equal(CustomerSignRule.MessageMax, ok.Message!.Length);
        // ยอมรับ/เซ็นรับ: ข้อความไม่บังคับ
        Assert.Null(CustomerSignRule.DecideReason(CustomerSignRule.Kind.Accepted, null, null, false).Error);
    }

    [Fact]
    public void CSR267_KindFor_รหัสปุ่มกับชนิดเอกสาร()
    {
        Assert.Equal(CustomerSignRule.Kind.Accepted, CustomerSignRule.KindFor(CustomerDocPage.ActionAccept, DocumentType.Quotation));
        Assert.Equal(CustomerSignRule.Kind.Declined, CustomerSignRule.KindFor(CustomerDocPage.ActionDecline, DocumentType.Quotation));
        Assert.Equal(CustomerSignRule.Kind.ChangeRequested, CustomerSignRule.KindFor(CustomerDocPage.ActionRequestChange, DocumentType.Quotation));
        Assert.Equal(CustomerSignRule.Kind.DeliverySigned, CustomerSignRule.KindFor(CustomerDocPage.ActionSignDelivery, DocumentType.DeliveryNote));
        Assert.Null(CustomerSignRule.KindFor(CustomerDocPage.ActionAccept, DocumentType.DeliveryNote));       // ชนิดไม่ตรง
        Assert.Null(CustomerSignRule.KindFor(CustomerDocPage.ActionDecline, DocumentType.Invoice));
        Assert.Null(CustomerSignRule.KindFor(CustomerDocPage.ActionPayOnline, DocumentType.Quotation));
        Assert.Null(CustomerSignRule.KindFor("อะไรก็ได้", DocumentType.Quotation));
        Assert.Null(CustomerSignRule.KindFor(null, DocumentType.Quotation));
    }

    // ───────── ปุ่มของหน้า: ไม่รับ / ขอแก้ · หนึ่งคำตอบต่อ Rev (267-1-11) ─────────

    private const string Href = "/api/companies/c/public-pay/documents/t/respond";

    private static CustomerDocPage.Facts Q(DateTime? due = null, DateTime? start = null, bool started = true, string? response = null, DateTime? respondedAt = null,
        CustomerDocPage.Mode mode = CustomerDocPage.Mode.Link, DocumentType type = DocumentType.Quotation, string? href = Href)
        => new(type, DocumentStatus.Approved, mode, false, false, null, false, due, started ? start ?? Now.AddDays(-1) : null, respondedAt, href,
            CustomerResponse: response);

    private static CustomerDocPage.PageAction? Act(IReadOnlyList<CustomerDocPage.PageAction> list, string id) => list.FirstOrDefault(a => a.Id == id);

    [Fact]
    public void CSR267_Actions_เปิดให้ตอบรับ_มียอมรับ_ไม่รับ_ขอแก้_ชี้เส้นเดียว()
    {
        var actions = CustomerDocPage.Actions(Q(due: Now.Date.AddDays(5)), Now);
        foreach (var id in new[] { CustomerDocPage.ActionAccept, CustomerDocPage.ActionDecline, CustomerDocPage.ActionRequestChange })
        {
            var a = Act(actions, id)!;
            Assert.True(a.Enabled, a.DisabledReason);
            Assert.Equal(Href, a.Href);
        }
        Assert.Null(Act(actions, CustomerDocPage.ActionRequestLink));
    }

    [Fact]
    public void CSR267_Actions_ยังไม่ถูกเปิดให้ตอบรับ_ไม่มีปุ่มไม่รับและขอแก้()
    {
        var actions = CustomerDocPage.Actions(Q(due: Now.Date.AddDays(5), started: false), Now);
        Assert.False(Act(actions, CustomerDocPage.ActionAccept)!.Enabled);
        Assert.Null(Act(actions, CustomerDocPage.ActionDecline));
        Assert.Null(Act(actions, CustomerDocPage.ActionRequestChange));
    }

    [Fact]
    public void CSR267_Actions_พ้นวันยืนราคา_ยอมรับปิด_ไม่รับไม่แสดง_แต่ขอแก้ยังเปิด()
    {
        var actions = CustomerDocPage.Actions(Q(due: Now.Date.AddDays(-3)), Now);
        Assert.False(Act(actions, CustomerDocPage.ActionAccept)!.Enabled);
        Assert.Null(Act(actions, CustomerDocPage.ActionDecline));
        Assert.True(Act(actions, CustomerDocPage.ActionRequestChange)!.Enabled);          // ทางไปต่อ: ขอให้ผู้ขายออกฉบับใหม่
        Assert.True(Act(actions, CustomerDocPage.ActionRequestLink)!.Enabled);
    }

    [Theory]
    [InlineData(nameof(CustomerSignRule.Kind.Declined), "ไม่รับ")]
    [InlineData(nameof(CustomerSignRule.Kind.ChangeRequested), "คำขอแก้ไข")]
    public void CSR267_OneResponsePerRevision_ตอบแล้วปุ่มตอบรับปิดพร้อมเหตุ_มีทางขอให้ผู้ขายเปิดใหม่(string response, string expect)
    {
        var actions = CustomerDocPage.Actions(Q(due: Now.Date.AddDays(5), response: response), Now);
        var accept = Act(actions, CustomerDocPage.ActionAccept)!;
        Assert.False(accept.Enabled);
        Assert.Contains(expect, accept.DisabledReason);
        Assert.Null(accept.Href);
        Assert.Null(Act(actions, CustomerDocPage.ActionDecline));
        Assert.Null(Act(actions, CustomerDocPage.ActionRequestChange));
        Assert.True(Act(actions, CustomerDocPage.ActionRequestLink)!.Enabled);
        // ทิศตรงข้าม: ยังไม่เคยตอบ ⇒ เปิด
        Assert.True(Act(CustomerDocPage.Actions(Q(due: Now.Date.AddDays(5)), Now), CustomerDocPage.ActionAccept)!.Enabled);
    }

    [Fact]
    public void CSR267_Actions_ยอมรับแล้ว_ไม่มีปุ่มตอบรับใดเปิด()
    {
        var actions = CustomerDocPage.Actions(Q(due: Now.Date.AddDays(5), response: nameof(CustomerSignRule.Kind.Accepted), respondedAt: Now.AddHours(-1)), Now);
        Assert.False(Act(actions, CustomerDocPage.ActionAccept)!.Enabled);
        Assert.Null(Act(actions, CustomerDocPage.ActionDecline));
        Assert.Null(Act(actions, CustomerDocPage.ActionRequestChange));
        Assert.Null(Act(actions, CustomerDocPage.ActionRequestLink));
    }

    [Theory]
    [InlineData(CustomerDocPage.Mode.Preview)]
    [InlineData(CustomerDocPage.Mode.LineView)]
    [InlineData(CustomerDocPage.Mode.Portal)]
    public void CSR267_Actions_โหมดที่ไม่ใช่ลิงก์ของใบ_ไม่มีไม่รับและขอแก้(CustomerDocPage.Mode mode)
    {
        var actions = CustomerDocPage.Actions(Q(due: Now.Date.AddDays(5), mode: mode, href: null), Now);
        Assert.Null(Act(actions, CustomerDocPage.ActionDecline));
        Assert.Null(Act(actions, CustomerDocPage.ActionRequestChange));
        Assert.False(Act(actions, CustomerDocPage.ActionAccept)!.Enabled);
    }

    [Fact]
    public void CSR267_Actions_ใบส่งของ_มีแต่เซ็นรับ()
    {
        var actions = CustomerDocPage.Actions(Q(type: DocumentType.DeliveryNote), Now);
        Assert.True(Act(actions, CustomerDocPage.ActionSignDelivery)!.Enabled);
        Assert.Null(Act(actions, CustomerDocPage.ActionDecline));
        Assert.Null(Act(actions, CustomerDocPage.ActionRequestChange));
    }

    [Fact]
    public void CSR267_ReopenAndRevision_ส่งให้ตอบรับใหม่ล้างไม่รับ_คงยอมรับ_ออกRevล้างทั้งหมด_ป้ายบนรายการ()
    {
        var d = new Document { DocumentType = DocumentType.Quotation, Status = DocumentStatus.Approved, PayLinkTokenHash = "h", PayLinkExpiresAt = Now.AddDays(100),
            SignLinkIssuedAt = Now.AddDays(-2), CustomerResponse = nameof(CustomerSignRule.Kind.Declined), CustomerResponseAt = Now.AddDays(-1) };
        Assert.Equal("declined", SignRequestKinds.Status(d, Now)!.Code);
        d.CustomerResponse = nameof(CustomerSignRule.Kind.ChangeRequested);
        Assert.Equal("change_requested", SignRequestKinds.Status(d, Now)!.Code);
        SignRequestKinds.MarkIssued(d, Now);                                              // ผู้ขายส่งให้ตอบรับใหม่
        Assert.Null(d.CustomerResponse);
        Assert.Null(d.CustomerResponseAt);
        Assert.Equal("issued", SignRequestKinds.Status(d, Now)!.Code);

        d.CustomerResponse = nameof(CustomerSignRule.Kind.Accepted);
        d.CustomerResponseAt = Now;
        SignRequestKinds.MarkIssued(d, Now);                                              // ยอมรับแล้ว: ส่งซ้ำไม่ล้างคำตอบ
        Assert.Equal(nameof(CustomerSignRule.Kind.Accepted), d.CustomerResponse);
        SignRequestKinds.CloseRespondOnRevision(d);                                       // ออก Rev ใหม่ = คำตอบเป็นของ Rev เดิม
        Assert.Null(d.CustomerResponse);
        Assert.Null(d.CustomerResponseAt);
        Assert.Null(d.SignLinkIssuedAt);
    }

    [Fact]
    public void CSR267_PriorResponseBlock_ข้อความต่อคำตอบ()
    {
        Assert.Null(CustomerSignRule.PriorResponseBlock(false, null));
        Assert.Null(CustomerSignRule.PriorResponseBlock(false, ""));
        Assert.Contains("ยอมรับ", CustomerSignRule.PriorResponseBlock(false, nameof(CustomerSignRule.Kind.Accepted)));
        Assert.Contains("declined", CustomerSignRule.PriorResponseBlock(true, nameof(CustomerSignRule.Kind.Declined)));
    }

    // ───────── รหัสยืนยันอีเมล (267-1-6 — ค่าตั้งต้น = ไม่ใช้) ─────────

    [Fact]
    public void CSR267_OtpRequired_ไม่ตั้งเกณฑ์ไม่ใช้_ถึงเกณฑ์ใช้_เฉพาะยอมรับ()
    {
        Assert.False(CustomerSignRule.OtpRequired(null, 9_999_999m, CustomerSignRule.Kind.Accepted));          // ค่าตั้งต้น
        Assert.False(CustomerSignRule.OtpRequired(0m, 9_999_999m, CustomerSignRule.Kind.Accepted));
        Assert.False(CustomerSignRule.OtpRequired(100_000m, 99_999.99m, CustomerSignRule.Kind.Accepted));
        Assert.True(CustomerSignRule.OtpRequired(100_000m, 100_000m, CustomerSignRule.Kind.Accepted));          // ขอบ = ใช้
        Assert.True(CustomerSignRule.OtpRequired(100_000m, 250_000m, CustomerSignRule.Kind.Accepted));
        Assert.False(CustomerSignRule.OtpRequired(100_000m, 250_000m, CustomerSignRule.Kind.Declined));
        Assert.False(CustomerSignRule.OtpRequired(100_000m, 250_000m, CustomerSignRule.Kind.ChangeRequested));
        Assert.False(CustomerSignRule.OtpRequired(100_000m, 250_000m, CustomerSignRule.Kind.DeliverySigned));
    }

    [Fact]
    public void CSR267_Otp_รหัสหกหลัก_hashผูกใบและอีเมล_ตรวจผลทุกแบบ()
    {
        var code = CustomerSignRule.NewOtpCode();
        Assert.Matches("^[0-9]{6}$", code);
        var doc = Guid.NewGuid();
        const string email = "a@buyer.co.th";
        var hash = CustomerSignRule.OtpHash(doc, email, code);
        Assert.NotEqual(hash, CustomerSignRule.OtpHash(Guid.NewGuid(), email, code));     // รหัสของใบอื่นใช้ไม่ได้
        Assert.NotEqual(hash, CustomerSignRule.OtpHash(doc, "b@buyer.co.th", code));       // อีเมลอื่นใช้ไม่ได้
        Assert.DoesNotContain(code, hash);
        var exp = Now.AddMinutes(CustomerSignRule.OtpTtlMinutes);
        Assert.Equal(CustomerSignRule.OtpOutcome.Ok, CustomerSignRule.VerifyOtp(hash, exp, 0, null, doc, email, " " + code + " ", Now));
        var wrong = code == "000000" ? "000001" : "000000";
        Assert.Equal(CustomerSignRule.OtpOutcome.Wrong, CustomerSignRule.VerifyOtp(hash, exp, 0, null, doc, email, wrong, Now));
        Assert.Equal(CustomerSignRule.OtpOutcome.Wrong, CustomerSignRule.VerifyOtp(hash, exp, 0, null, doc, email, null, Now));
        Assert.Equal(CustomerSignRule.OtpOutcome.Expired, CustomerSignRule.VerifyOtp(hash, Now, 0, null, doc, email, code, Now));
        Assert.Equal(CustomerSignRule.OtpOutcome.TooManyAttempts, CustomerSignRule.VerifyOtp(hash, exp, CustomerSignRule.OtpMaxAttempts, null, doc, email, code, Now));
        Assert.Equal(CustomerSignRule.OtpOutcome.Missing, CustomerSignRule.VerifyOtp(hash, exp, 0, Now.AddMinutes(-1), doc, email, code, Now));   // ใช้แล้ว
        Assert.Equal(CustomerSignRule.OtpOutcome.Missing, CustomerSignRule.VerifyOtp(null, null, 0, null, doc, email, code, Now));                // ไม่เคยขอ
        Assert.All(Enum.GetValues<CustomerSignRule.OtpOutcome>().Where(o => o != CustomerSignRule.OtpOutcome.Ok),
            o => Assert.False(string.IsNullOrWhiteSpace(CustomerSignRule.OtpMessage(false, o))));
    }

    // ───────── ฉบับที่ลูกค้าเซ็น: ตำแหน่งไฟล์ + SHA-256 ─────────

    [Fact]
    public void CSR267_SignedCopyLocation_อยู่ใต้โฟลเดอร์ของบริษัท_ไฟล์ของบริษัทหรือใบอื่นไม่ผ่าน()
    {
        var root = Path.Combine(Path.GetTempPath(), "csr267-root");
        var (cid, did, aid) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var path = CustomerSignRule.SignedCopyLocation(root, cid, did, aid);
        Assert.Contains(Path.Combine(root, CustomerSignRule.SignedCopyFolder, cid.ToString("N")), path);
        Assert.EndsWith($"{did:N}-{aid:N}.pdf", path);
        Assert.True(CustomerSignRule.IsOwnSignedCopyLocation(path, root, cid, did, aid));
        Assert.False(CustomerSignRule.IsOwnSignedCopyLocation(path, root, Guid.NewGuid(), did, aid));           // บริษัทอื่น
        Assert.False(CustomerSignRule.IsOwnSignedCopyLocation(path, root, cid, Guid.NewGuid(), aid));           // ใบอื่น
        Assert.False(CustomerSignRule.IsOwnSignedCopyLocation(path, root, cid, did, Guid.NewGuid()));           // แถวหลักฐานอื่น
        Assert.False(CustomerSignRule.IsOwnSignedCopyLocation(Path.Combine(root, "uploads", "x.pdf"), root, cid, did, aid));
        Assert.False(CustomerSignRule.IsOwnSignedCopyLocation(path + "/../../x.pdf", root, cid, did, aid));
        Assert.False(CustomerSignRule.IsOwnSignedCopyLocation(null, root, cid, did, aid));
        Assert.False(CustomerSignRule.IsOwnSignedCopyLocation(path, "", cid, did, aid));
        // ไม่อยู่ใน allow-list ของ /uploads (ไม่มี URL สาธารณะ)
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting", "Program.cs"));
        Assert.DoesNotContain("\"/uploads/" + CustomerSignRule.SignedCopyFolder, program);
    }

    [Fact]
    public void CSR267_SignedPdfHashStable_ไฟล์เดิมhashเดิม_แก้หนึ่งไบต์ไม่ผ่าน()
    {
        var pdf = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 1, 2, 3, 4 };
        var sha = CustomerSignRule.FileSha256(pdf);
        Assert.Equal(64, sha.Length);
        Assert.Equal(sha, CustomerSignRule.FileSha256((byte[])pdf.Clone()));               // คงที่
        Assert.True(CustomerSignRule.SignedCopyIntact(pdf, sha));
        var tampered = (byte[])pdf.Clone();
        tampered[^1] ^= 0x01;
        Assert.False(CustomerSignRule.SignedCopyIntact(tampered, sha));                    // ถูกแก้ = ไม่ส่งออก
        Assert.False(CustomerSignRule.SignedCopyIntact(pdf, null));
        Assert.False(CustomerSignRule.SignedCopyIntact(null, sha));
        var notPdf = new byte[] { 1, 2, 3, 4, 5, 6 };
        Assert.False(CustomerSignRule.SignedCopyIntact(notPdf, CustomerSignRule.FileSha256(notPdf)));   // hash ตรงแต่ไม่ใช่ PDF
    }

    [Fact]
    public void CSR267_SnapshotStillValid_สร้างได้เฉพาะRevและเนื้อหาเดียวกับตอนลงนาม()
    {
        Assert.True(CustomerSignRule.SnapshotStillValid(1, "v2:abc", 1, "v2:abc"));
        Assert.False(CustomerSignRule.SnapshotStillValid(1, "v2:abc", 2, "v2:abc"));        // Rev ใหม่
        Assert.False(CustomerSignRule.SnapshotStillValid(1, "v2:abc", 1, "v2:abd"));        // เนื้อหาเปลี่ยน
    }

    [Fact]
    public void CSR267_EvidenceRef_แปดตัวแรกของhashเนื้อหา_ไม่มีข้อมูลบุคคล()
    {
        Assert.Equal("0123ABCD", CustomerSignRule.EvidenceRef("v2:0123abcd9999"));
        Assert.Equal("0123ABCD", CustomerSignRule.EvidenceRef("0123abcd9999"));
        Assert.Null(CustomerSignRule.EvidenceRef(null));
        Assert.Null(CustomerSignRule.EvidenceRef("v2:abc"));
    }

    // ───────── กระดาษ: สอง renderer พิมพ์ผู้ลงนามชุดเดียวกัน (กฎ #4 A · 267-1-17) ─────────

    private static CustomerDocumentAction Evidence(string title = "ผู้จัดการฝ่ายจัดซื้อ", string org = "บริษัท ลูกค้า จำกัด") => new()
    {
        Action = nameof(CustomerSignRule.Kind.Accepted), IdentityLevel = CustomerSignRule.IdentityFull, SignerFirstName = "สมหญิง", SignerLastName = "ใจดี",
        SignerTitle = title, SignerOrgName = org, ContentHash = "v2:0123abcd9999", TokenHash = "t", Ip = "203.0.113.77",
    };

    [Fact]
    public void CSR267_BothRenderersPrintSigner_ชื่อตำแหน่งบริษัทรหัสอ้างอิง_ป้ายจากDocumentLabels_ไม่พิมพ์IP()
    {
        var doc = new Document { DocumentType = DocumentType.Quotation, QuotationAcceptedAt = Now, QuotationAcceptedBy = "สมหญิง ใจดี" };
        var signer = PdfGenerationService.QuotationAcceptanceSigner(doc, null, null, Evidence());
        Assert.Equal("สมหญิง ใจดี", signer.Name);
        Assert.Equal("ผู้จัดการฝ่ายจัดซื้อ", signer.Title);
        Assert.Equal("บริษัท ลูกค้า จำกัด", signer.OnBehalfOf);
        Assert.Equal("0123ABCD", signer.EvidenceRef);

        foreach (var lang in new[] { "th", "en" })
        {
            var L = DocumentLabels.For(lang);
            // ช่องลายเซ็น (Title พิมพ์เองอยู่แล้ว) vs บล็อกยอมรับ (ต้องมีบรรทัดตำแหน่ง)
            var box = PdfGenerationService.CustomerSignerLines(signer, L);
            Assert.Equal(new[] { L.SignedOnBehalfOf("บริษัท ลูกค้า จำกัด"), L.SignedOnlineEvidenceRef("0123ABCD") }, box);
            var block = PdfGenerationService.CustomerSignerLines(signer, L, includeTitle: true);
            Assert.Equal(new[] { L.SignerPosition("ผู้จัดการฝ่ายจัดซื้อ"), L.SignedOnBehalfOf("บริษัท ลูกค้า จำกัด"), L.SignedOnlineEvidenceRef("0123ABCD") }, block);
            Assert.All(block, line => Assert.DoesNotContain("203.0.113.77", line));           // 267-1-17 ไม่พิมพ์ IP

            var html = PdfGenerationService.QuotationAcceptanceBlockHtml(signer, Now, L);
            Assert.Contains("สมหญิง ใจดี", html);
            Assert.All(block, line => Assert.Contains(System.Net.WebUtility.HtmlEncode(line), html));
            Assert.DoesNotContain("203.0.113.77", html);
        }
        Assert.Contains("ตำแหน่ง", DocumentLabels.For("th").SignerPosition("x"));
        Assert.Contains("Position", DocumentLabels.For("en").SignerPosition("x"));
        Assert.Equal("เซ็นรับออนไลน์ 12/11/2569 10:00 น.", DocumentLabels.For("th").DnSignedOnline(Now));
        Assert.Equal("Signed online 12 Nov 2026 10:00 (GMT+7)", DocumentLabels.For("en").DnSignedOnline(Now));

        // ทั้งสอง renderer เรียกตัวตั้งเดียวกัน (ไม่มีสำเนาบรรทัด/literal) — ช่องลายเซ็น + บล็อกยอมรับ ครบทั้งสองฝั่ง
        var root = RepoRoot();
        var html1 = File.ReadAllText(Path.Combine(root, "Accounting/Services/Implementations/PdfGenerationService.cs"));
        var stamp = File.ReadAllText(Path.Combine(root, "Accounting/Services/Implementations/PdfGenerationService.Stamp.cs"));
        var quest = File.ReadAllText(Path.Combine(root, "Accounting/Services/Implementations/PdfGenerationService.DocumentRenderer.cs"));
        Assert.Contains("foreach (var signerLine in CustomerSignerLines(s, L))", html1);
        Assert.Contains("foreach (var signerLine in CustomerSignerLines(s, L, includeTitle: true))", stamp);
        Assert.Contains("foreach (var signerLine in CustomerSignerLines(s, L))", quest);
        Assert.Contains("foreach (var signerLine in CustomerSignerLines(s, L, includeTitle: true))", quest);
        foreach (var src in new[] { html1, stamp, quest })
        {
            Assert.DoesNotContain("\"ลงนามในนามของ", src);
            Assert.DoesNotContain("\"Signed on behalf of", src);
        }
    }

    [Fact]
    public void CSR267_SignerNameEncodedBothRenderers_ชื่อตำแหน่งบริษัทที่เป็นแท็ก_พิมพ์เป็นข้อความ()
    {
        var doc = new Document { DocumentType = DocumentType.Quotation, QuotationAcceptedAt = Now, QuotationAcceptedBy = "<script>alert(1)</script>" };
        var signer = PdfGenerationService.QuotationAcceptanceSigner(doc, null, null, Evidence("<img src=x onerror=alert(2)>", "<b>บริษัท</b>"));
        var html = PdfGenerationService.QuotationAcceptanceBlockHtml(signer, Now, DocumentLabels.For("th"));
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("<b>บริษัท</b>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=alert(2)&gt;", html);
        // ตัวตั้งร่วมคืนข้อความดิบ (QuestPDF วาดเป็นข้อความ — ไม่ตีความ HTML) · ฝั่ง HTML encode ที่จุดต่อ
        Assert.Contains(CustomerSignerLine(signer), l => l.Contains("<b>บริษัท</b>"));
    }

    private static IReadOnlyList<string> CustomerSignerLine(PdfGenerationService.DocumentSigner s)
        => PdfGenerationService.CustomerSignerLines(s, DocumentLabels.For("th"), includeTitle: true);

    [Fact]
    public void CSR267_NoEvidence_ยอมรับก่อนรอบ267_กระดาษพิมพ์ตามเดิม()
    {
        var doc = new Document { DocumentType = DocumentType.Quotation, QuotationAcceptedAt = Now, QuotationAcceptedBy = "คุณเอ" };
        var signer = PdfGenerationService.QuotationAcceptanceSigner(doc, null, null);
        Assert.Null(signer.Title);
        Assert.Empty(PdfGenerationService.CustomerSignerLines(signer, DocumentLabels.For("th"), includeTitle: true));
        Assert.Empty(PdfGenerationService.CustomerSignerLines(null, DocumentLabels.For("th")));
    }

    // ───────── PDPA: ล้างข้อมูลผู้ลงนามตามคำขอ (หลังพ้นอายุเก็บ) ─────────

    [Fact]
    public void CSR267_EraseEvidencePii_ล้างข้อมูลบุคคล_คงhashและเวลา_คืนไฟล์ที่ต้องลบ_ทำซ้ำไม่มีผล()
    {
        var created = Now.AddYears(-6);
        var row = new CustomerDocumentAction
        {
            Action = nameof(CustomerSignRule.Kind.Accepted), RevisionNumber = 2, SignerFirstName = "สมหญิง", SignerLastName = "ใจดี", SignerTitle = "ผจก.",
            SignerOrgName = "บริษัท ลูกค้า จำกัด", SignerEmail = "a@b.co", Message = "ส่งวันจันทร์", Ip = "203.0.113.5", UserAgentHash = "ua",
            SignatureFilePath = "/att/sig.png", SignatureSha256 = "sigsha", SignedPdfPath = "/att/signed.pdf", SignedPdfSha256 = "pdfsha",
            ContentHash = "v2:abc", TokenHash = "tok", AttestationTextHash = "ath", CreatedAt = created,
        };
        var files = QuotationAcceptance.EraseEvidencePii(row, Now);
        Assert.Equal(new[] { "/att/sig.png", "/att/signed.pdf" }, files);
        Assert.Equal(QuotationAcceptance.Anonymized, row.SignerFirstName);
        Assert.Null(row.SignerLastName);
        Assert.Null(row.SignerTitle);
        Assert.Null(row.SignerOrgName);
        Assert.Null(row.SignerEmail);
        Assert.Null(row.Message);
        Assert.Null(row.Ip);
        Assert.Null(row.UserAgentHash);
        Assert.Null(row.SignatureFilePath);
        Assert.Null(row.SignedPdfPath);
        Assert.Equal(Now, row.ErasedAt);
        // คงสิ่งที่ไม่ระบุตัวบุคคล (แถวยังพิสูจน์ได้ว่ามีการตอบรับฉบับไหน เมื่อไร)
        Assert.Equal("v2:abc", row.ContentHash);
        Assert.Equal("tok", row.TokenHash);
        Assert.Equal("pdfsha", row.SignedPdfSha256);
        Assert.Equal("sigsha", row.SignatureSha256);
        Assert.Equal("ath", row.AttestationTextHash);
        Assert.Equal(created, row.CreatedAt);
        Assert.Equal(2, row.RevisionNumber);
        Assert.Empty(QuotationAcceptance.EraseEvidencePii(row, Now.AddDays(1)));           // ทำซ้ำ = ไม่มีผล
        Assert.Equal(Now, row.ErasedAt);
    }

    [Fact]
    public void CSR267_LegalHold_ใบในอายุเก็บลบไม่ได้_พ้นแล้วลบได้_สูตรเดียวกับของเดิม()
    {
        var date = Now.AddYears(-3);
        Assert.True(QuotationAcceptance.UnderLegalHold(date, null, Now));                   // ยังไม่ครบ 5 ปี
        Assert.False(QuotationAcceptance.UnderLegalHold(Now.AddYears(-6), null, Now));      // พ้น 5 ปี
        Assert.True(QuotationAcceptance.UnderLegalHold(Now.AddYears(-6), Now.AddYears(1), Now));   // RetentionUntil ยาวกว่า = MAX
        var doc = new Document { DocumentDate = date, RetentionUntil = null };
        Assert.Equal(QuotationAcceptance.UnderLegalHold(doc, Now), QuotationAcceptance.UnderLegalHold(date, null, Now));
    }

    [Fact]
    public void CSR267_Ropa_มีวัตถุประสงค์_ฐาน_อายุเก็บ_ผู้รับ()
    {
        Assert.Contains("ลงนาม", CustomerSignText.RopaPurpose);
        Assert.Equal("Contract", CustomerSignText.RopaLegalBasis);
        Assert.Contains("IP", CustomerSignText.RopaDataCategories);
        Assert.Contains("5 ปี", CustomerSignText.RopaRetention);
        Assert.Contains("ม.14", CustomerSignText.RopaRetention);
        Assert.False(string.IsNullOrWhiteSpace(CustomerSignText.RopaRecipients));
    }

    // ───────── หลังยอมรับ: แนะนำแปลง + คำเตือนอากรแสตมป์ (267-1-14 · 267-1-15) ─────────

    [Fact]
    public void CSR267_StampDutyWarn_เฉพาะยอมรับใบเสนอราคาที่มีงานบริการ_เตือนเท่านั้น()
    {
        Assert.True(CustomerSignRule.StampDutyWarn(DocumentType.Quotation, CustomerSignRule.Kind.Accepted, hasServiceLine: true));
        Assert.False(CustomerSignRule.StampDutyWarn(DocumentType.Quotation, CustomerSignRule.Kind.Accepted, hasServiceLine: false));
        Assert.False(CustomerSignRule.StampDutyWarn(DocumentType.Quotation, CustomerSignRule.Kind.Declined, true));
        Assert.False(CustomerSignRule.StampDutyWarn(DocumentType.DeliveryNote, CustomerSignRule.Kind.DeliverySigned, true));
        Assert.Contains("อาจ", CustomerSignText.StampDutyWarning);                            // ไม่ตัดสินแทนผู้ทำบัญชี
        Assert.Contains("ระบบไม่ได้ตัดสิน", CustomerSignText.StampDutyWarning);
        Assert.Equal("/pages/stamp-duty.html", CustomerSignText.StampDutyPage);
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "Accounting", "wwwroot", "pages", "stamp-duty.html")));
    }

    [Fact]
    public void CSR267_ConvertSuggestions_ไม่มีชนิดใบสั่งขาย_แนะนำใบส่งของใบแจ้งหนี้ใบกำกับ()
    {
        Assert.Equal(new[] { DocumentType.DeliveryNote, DocumentType.Invoice, DocumentType.TaxInvoice }, CustomerSignText.ConvertSuggestions);
        Assert.DoesNotContain(Enum.GetNames<DocumentType>(), n => n == "SalesOrder");      // 267-1-15: ระบบไม่มีชนิดนี้ — ถ้าเพิ่มต้องกลับมาทบทวนคำแนะนำ
        Assert.Contains("มัดจำ", CustomerSignText.ConvertSuggestionText);
    }

    // ───────── เหตุการณ์ outbox ─────────

    [Fact]
    public void CSR267_Events_ชื่อเหตุการณ์ต่อชนิด_กุญแจกันซ้ำต่อแถวหลักฐาน()
    {
        Assert.Equal("quotation.accepted", SignRequestKinds.EventFor(CustomerSignRule.Kind.Accepted));
        Assert.Equal("delivery.signed", SignRequestKinds.EventFor(CustomerSignRule.Kind.DeliverySigned));
        Assert.Equal("quotation.declined", SignRequestKinds.EventFor(CustomerSignRule.Kind.Declined));
        Assert.Equal("quotation.change_requested", SignRequestKinds.EventFor(CustomerSignRule.Kind.ChangeRequested));
        var doc = Guid.NewGuid();
        // token ของหน้าเอกสารอยู่ข้าม Rev ⇒ ยอมรับ Rev ถัดไปต้องได้กุญแจใหม่ (ผูกกับแถวหลักฐาน)
        Assert.NotEqual(SignEventOutbox.EventKey("quotation.accepted", doc, "tok:" + Guid.NewGuid().ToString("N")),
            SignEventOutbox.EventKey("quotation.accepted", doc, "tok:" + Guid.NewGuid().ToString("N")));
        Assert.True(SignEventOutbox.EventKey("quotation.change_requested", doc, new string('a', 16) + ":" + Guid.NewGuid().ToString("N")).Length <= 200);
    }

    // ───────── schema: ตารางหลักฐาน append-only + ตรงกับ entity ─────────

    [Fact]
    public void CSR267_Migration_ตารางครบทุกช่องของentity_มีtriggerกันแก้และกันลบ()
    {
        var sql = string.Join("\n", CustomerSignMigration.Statements());
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"CustomerDocumentActions\"", sql);
        foreach (var prop in typeof(CustomerDocumentAction).GetProperties().Where(p => p.Name != nameof(CustomerDocumentAction.Company)))
            Assert.Contains($"\"{prop.Name}\"", sql);
        foreach (var prop in typeof(CustomerSignOtp).GetProperties().Where(p => p.Name != nameof(CustomerSignOtp.Company)))
            Assert.Contains($"\"{prop.Name}\"", sql);
        Assert.Contains("BEFORE UPDATE ON \"CustomerDocumentActions\"", sql);
        Assert.Contains("BEFORE DELETE ON \"CustomerDocumentActions\"", sql);
        Assert.Contains("append-only", sql);
        Assert.Contains("\"CustomerResponse\"", sql);
        Assert.Contains("\"CustomerSignOtpMinAmount\"", sql);
        // ช่องของ outbox อยู่กับเจ้าของตาราง (SignRequestMigration — เทสต์รอบ 240 ตรวจทุกคอลัมน์ของโมเดลกับไฟล์นั้น)
        Assert.Contains("\"SignedCopyDoneAt\"", string.Join("\n", SignRequestMigration.Statements()));
        // รันตอนบูต (ไม่ใช่ EF Migrations)
        var helper = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting", "Data", "DatabaseMigrationHelper.cs"));
        Assert.Contains("CustomerSignMigration.Statements()", helper);
    }

    // ───────── หน้าเว็บ: ไม่มีสำเนาข้อความ/กติกา · จำข้อมูลบนเครื่องเท่านั้น ─────────

    [Fact]
    public void CSR267_Page_ไม่มีข้อความรับรองของตัวเอง_ส่งRevและhashที่เห็น_จำข้อมูลในเครื่อง()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting", "wwwroot", "pages", "pay-document.html"));
        Assert.DoesNotContain("ข้าพเจ้าเป็นบุคคลของ", page);                                 // ข้อความรับรองมาจากเซิร์ฟเวอร์
        Assert.DoesNotContain("quotation-accept.html", page);                              // ไม่พาไปหน้ายอมรับรุ่นก่อนแล้ว
        Assert.DoesNotContain("delivery-sign.html", page);
        Assert.Contains("attestationVersion: view.page.respond.attestationVersion", page);
        Assert.Contains("revisionNumber: view.page.respond.revisionNumber, contentHash: view.page.respond.contentHash", page);
        Assert.Contains("localStorage.setItem(SIGNER_KEY", page);
        Assert.Contains("localStorage.removeItem(SIGNER_KEY)", page);
        Assert.DoesNotContain("id=\"sgRemember\" checked", page);                           // ไม่ติ๊กไว้ก่อน
    }

    // ───────── ชุดแก้ฝ่ายค้าน 267-P2 (erp-review/2026-11-13/review-267-P2.md · คำตัดสิน 267-1-51…) ─────────

    [Fact]
    public void CSR267_H1_AuditRedaction_แถวหลักฐานและรหัสยืนยัน_ปิดค่าผู้ลงนามทั้งตอนเขียนและตอนแสดง()
    {
        foreach (var f in new[] { "SignerFirstName", "SignerLastName", "SignerTitle", "SignerOrgName", "SignerEmail", "Message", "Ip", "UserAgentHash", "SignatureFilePath", "SignedPdfPath" })
            Assert.Equal(AuditRedaction.PiiMask, AuditRedaction.Value(nameof(CustomerDocumentAction), f, "ค่า"));
        Assert.Equal(AuditRedaction.PiiMask, AuditRedaction.Value(nameof(CustomerSignOtp), "CodeHash", "abc"));
        Assert.Equal(AuditRedaction.PiiMask, AuditRedaction.Value(nameof(CustomerSignOtp), "EmailHash", "abc"));
        // ทิศตรงข้าม: hash เนื้อหา / ชนิด / Rev ไม่ใช่ข้อมูลส่วนบุคคล — audit ยังพิสูจน์การตอบรับได้
        Assert.Equal("v2:abc", AuditRedaction.Value(nameof(CustomerDocumentAction), "ContentHash", "v2:abc"));
        Assert.Equal("Accepted", AuditRedaction.Value(nameof(CustomerDocumentAction), "Action", "Accepted"));
        // แถวเก่าที่เก็บค่าไปแล้ว: ปิดตอนแสดง
        var shown = AuditRedaction.RedactJson("{\"SignerFirstName\":\"สมหญิง\",\"Ip\":\"203.0.113.9\",\"ContentHash\":\"v2:x\"}", nameof(CustomerDocumentAction))!;
        Assert.DoesNotContain("สมหญิง", shown);
        Assert.DoesNotContain("203.0.113.9", shown);
        Assert.Contains("v2:x", shown);
        // audit ที่เขียนเองในตัวเขียนเดียวไม่คัดลอกชื่อ/ตำแหน่ง/IP (ถอดออกแล้วเทสต์นี้ต้องล้ม)
        var svc = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting", "Services", "Implementations", "CustomerDocumentResponseService.cs"));
        Assert.DoesNotContain("acceptedBy = name, acceptedAt = now", svc);
        Assert.DoesNotContain("respondedBy = signer.FullName, respondedAt", svc);
        Assert.DoesNotContain("signerTitle = evidence.SignerTitle", svc);
        Assert.DoesNotContain("IpAddress = ip,", svc);
        Assert.DoesNotContain("IpAddress = caller.Ip", svc);
    }

    [Fact]
    public void CSR267_M1_OtpHash_เป็นHMACด้วยกุญแจเซิร์ฟเวอร์_ไม่ใช่SHA256เปล่า()
    {
        var doc = Guid.NewGuid();
        var plain = QuotationAcceptance.Sha256Hex($"{doc:N}|a@b.co|123456");
        var h = CustomerSignRule.OtpHash(doc, "a@b.co", "123456");
        Assert.NotEqual(plain, h);
        Assert.Matches("^[0-9a-f]{64}$", h);
        Assert.Equal(h, CustomerSignRule.OtpHash(doc, "a@b.co", " 123456 "));
        Assert.Throws<InvalidOperationException>(() => CustomerSignRule.ConfigureOtpKey(" "));   // ห้ามกุญแจว่าง
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting", "Program.cs"));
        Assert.Contains("CustomerSignRule.ConfigureOtpKey(Accounting.Helpers.EncryptionKeyResolver.RequireUsable(", program);
    }

    [Fact]
    public void CSR267_M2_LegacyLink_ตอบแล้วหรือถึงเกณฑ์รหัส_ปิดพร้อมทางไปต่อ_ทิศตรงข้ามเปิด()
    {
        Assert.NotNull(CustomerSignRule.LegacyAcceptClosedReason(nameof(CustomerSignRule.Kind.Declined), false));
        Assert.NotNull(CustomerSignRule.LegacyAcceptClosedReason(nameof(CustomerSignRule.Kind.ChangeRequested), false));
        Assert.Contains("ลิงก์", CustomerSignRule.LegacyAcceptClosedReason(null, true));
        Assert.Null(CustomerSignRule.LegacyAcceptClosedReason(null, false));
        Assert.Null(CustomerSignRule.LegacyAcceptClosedReason(nameof(CustomerSignRule.Kind.Accepted), false));   // ยอมรับแล้ว = ตอบสถานะเดิม (idempotent) ที่ด่านก่อนหน้า
    }

    [Fact]
    public void CSR267_L4L8M3_ข้อความยินยอมต่อชนิด_ไม่อ้างว่าแจ้งแล้ว_ดาวน์โหลดเฉพาะเมื่อพร้อม()
    {
        foreach (var en in new[] { false, true })
        {
            var accept = CustomerSignText.Consent(en, CustomerSignRule.Kind.Accepted);
            var decline = CustomerSignText.Consent(en, CustomerSignRule.Kind.Declined);
            Assert.Contains(en ? "copy of the document" : "สำเนาเอกสารฉบับที่ท่านเห็น", accept);
            Assert.DoesNotContain(en ? "copy of the document" : "สำเนาเอกสารฉบับที่ท่านเห็น", decline);
            Assert.DoesNotContain(en ? "are printed on this document" : "จะพิมพ์บนเอกสาร", decline);
            Assert.DoesNotContain(en ? "copy of the document" : "สำเนาเอกสารฉบับที่ท่านเห็น", CustomerSignText.Consent(en, CustomerSignRule.Kind.ChangeRequested));
            foreach (var k in Enum.GetValues<CustomerSignRule.Kind>())
                Assert.DoesNotContain(en ? "has been notified" : "ได้รับแจ้งแล้ว", CustomerSignText.Done(en, k, true));
            Assert.DoesNotContain(en ? "download" : "ดาวน์โหลด", CustomerSignText.Done(en, CustomerSignRule.Kind.Accepted, false));
            Assert.Contains(en ? "download" : "ดาวน์โหลด", CustomerSignText.Done(en, CustomerSignRule.Kind.Accepted, true));
            Assert.DoesNotContain(en ? "download" : "ดาวน์โหลด", CustomerSignText.Done(en, CustomerSignRule.Kind.DeliverySigned, false));
        }
        Assert.NotEqual("267-1", CustomerSignText.Version);   // ข้อความเปลี่ยน ⇒ รุ่นขยับ
    }

    [Fact]
    public void CSR267_L6L7_อีเมลปิดบัง_อีเมลรหัสไม่มีเลขเอกสาร_มีเพดานต่อใบ()
    {
        Assert.Equal("s***@buyer.co.th", CustomerSignRule.MaskEmail("somying@buyer.co.th"));
        Assert.Null(CustomerSignRule.MaskEmail(null));
        Assert.Null(CustomerSignRule.MaskEmail("ไม่ใช่อีเมล"));
        Assert.DoesNotContain("QT-", CustomerSignText.OtpSubject(false, "บริษัท ผู้ขาย"));
        Assert.Contains("&lt;b&gt;", CustomerSignText.OtpBody(false, "<b>", "123456", 10));   // ชื่อผู้ขาย encode
        Assert.True(CustomerSignRule.OtpMaxPerDocumentPerDay is > 0 and <= 10);
    }

    [Fact]
    public void CSR267_L2L9_triggerลบล้างค่าเท่านั้น_กันTRUNCATE_รายการช่องใบแทนไม่ซ้ำ()
    {
        var sql = string.Join("\n", CustomerSignMigration.Statements());
        Assert.Contains("BEFORE TRUNCATE ON \"CustomerDocumentActions\"", sql);
        Assert.Contains("NEW.\"CreatedBy\" IS DISTINCT FROM OLD.\"CreatedBy\"", sql);
        Assert.Contains("IS DISTINCT FROM '" + QuotationAcceptance.Anonymized + "'", sql);
        var notCarried = SettlementPaidReissue.DocumentNotCarriedFields;
        Assert.Equal(notCarried.Count, notCarried.Distinct().Count());
        Assert.Equal(SettlementPaidReissue.DocumentCarriedFields.Count, SettlementPaidReissue.DocumentCarriedFields.Distinct().Count());
    }

    [Fact]
    public void CSR267_M3_ออกRevสร้างฉบับที่ลงนามก่อน_ล็อกลำดับในเส้นแก้เอกสาร()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting", "Services", "Implementations", "DocumentService.cs"));
        var ensure = src.IndexOf("EnsureSnapshotsBeforeRevisionAsync(", StringComparison.Ordinal);
        var bump = src.IndexOf("doc.RevisionNumber++;", StringComparison.Ordinal);
        Assert.True(ensure > 0 && bump > ensure, "ต้องสร้างฉบับที่ลงนามก่อน RevisionNumber++");
    }

    /// <summary>ทุกข้อความที่หน้าลูกค้า/กระดาษ/อีเมล/แจ้งเตือนใช้จาก <c>CustomerSignText</c> (ไทย + อังกฤษ · ทุกชนิดคำตอบ)</summary>
    private static IEnumerable<string> AllSignTexts()
    {
        foreach (var en in new[] { false, true })
        {
            foreach (var k in Enum.GetValues<CustomerSignRule.Kind>())
            {
                yield return CustomerSignText.Attestation(en, k, "บริษัท ตัวอย่าง จำกัด", true);
                yield return CustomerSignText.Attestation(en, k, "นาย ตัวอย่าง", false);
                yield return CustomerSignText.Consent(en, k);
                yield return CustomerSignText.KindLabel(en, k);
                yield return CustomerSignText.Done(en, k);
                yield return CustomerSignText.Done(en, k, signedCopyReady: true);
            }
            yield return CustomerSignText.OtpBody(en, "ผู้ขาย", "123456", 10);
            yield return CustomerSignText.PrivacyNotice(en, "ผู้ขาย");
            yield return CustomerSignText.IndividualTitle(en);
            foreach (var v in CustomerSignText.FormLabels(en).Values) yield return v;
            foreach (var r in CustomerSignText.DeclineReasons) yield return en ? r.En : r.Th;
            yield return CustomerSignText.OtpSubject(en, "QT-1");
            yield return CustomerSignText.SignedCopySubject(en, "ใบเสนอราคา", "QT-1");
        }
        yield return CustomerSignText.LegalReference;
        yield return CustomerSignText.ErasureAuditNote;
        yield return CustomerSignRule.LegacyAcceptClosedReason(nameof(CustomerSignRule.Kind.Declined), false)!;
        yield return CustomerSignRule.LegacyAcceptClosedReason(null, true)!;
        yield return CustomerSignText.StampDutyWarning;
        yield return CustomerSignText.ConvertSuggestionText;
        yield return CustomerSignText.RopaPurpose;
        yield return CustomerSignText.RopaDataCategories;
        yield return CustomerSignText.RopaRetention;
        yield return CustomerSignText.RopaNote;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Accounting.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("ไม่พบรากเรพ");
    }
}
