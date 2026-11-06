using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 241 — ส่งเอกสารอัตโนมัติ (pure): ช่องทางที่บริษัทเปิดจริง/เหตุที่เปิดไม่ได้ · ช่องทางต่อใบ (ตาราง ∩ บริษัท) · ใบไหนออกลิงก์ได้ ·
/// ยอดสุทธิหลังลูกค้าหัก ณ ที่จ่าย + WHT ที่ต้องบันทึกเมื่อเงินเข้า · ลิงก์ใช้ได้/เพิกถอน · ตัวตัดสินงานหลังรับชำระ (ออก/ส่งใบเสร็จ · หยุดเมื่อข้อมูลไม่ครบ) ·
/// จุดต่อบล็อกเพิ่มในอีเมลใบเสร็จ (DOCUMENT_FLOW §2.12g · DECISIONS 241-x)
/// </summary>
public class DocumentAutoSendRound241Tests
{
    private static DocumentPayLinkPolicy.CompanyFacts Facts(bool online, bool bank, params string[] methods)
        => new(online, methods, bank);

    // ───────── ช่องทาง ─────────

    [Fact]
    public void ไม่ได้ตั้งรับชำระออนไลน์_แนบลิงก์ไม่ได้พร้อมเหตุผล_แม้มีบัญชีรับโอน()
    {
        var f = Facts(online: false, bank: true);
        Assert.Contains("ยังไม่ได้ตั้งค่ารับชำระเงินออนไลน์", DocumentPayLinkPolicy.UnavailableReason(f));
        Assert.Equal(new[] { "ManualSlip" }, DocumentPayLinkPolicy.CompanyAvailable(f));
    }

    [Fact]
    public void ตั้งออนไลน์แต่ไม่มีวิธีที่หน้าชำระทำจบได้_ยังเปิดไม่ได้()
    {
        Assert.NotNull(DocumentPayLinkPolicy.UnavailableReason(Facts(true, true)));
        // ทิศตรงข้าม: มีพร้อมเพย์ ⇒ เปิดได้ · ลำดับ ออนไลน์ก่อน แล้วโอนเข้าบัญชี
        var ok = Facts(true, true, "PromptPay", "Card");
        Assert.Null(DocumentPayLinkPolicy.UnavailableReason(ok));
        Assert.Equal(new[] { "PromptPay", "Card", "ManualSlip" }, DocumentPayLinkPolicy.CompanyAvailable(ok));
    }

    [Fact]
    public void ช่องทางต่อใบ_ตารางทับค่าบริษัท_ตัดช่องทางที่บริษัทไม่ได้เปิด()
    {
        var all = new[] { "PromptPay", "Card", "ManualSlip" };
        Assert.Equal(all, DocumentPayLinkPolicy.Allowed(all, null, null));                                   // ไม่เลือก = ทุกช่องทาง
        Assert.Equal(new[] { "Card" }, DocumentPayLinkPolicy.Allowed(all, null, new[] { "card" }));          // ค่าบริษัท (ไม่สนตัวพิมพ์)
        Assert.Equal(new[] { "PromptPay" }, DocumentPayLinkPolicy.Allowed(all, new[] { "PromptPay", "TrueMoney" }, new[] { "Card" }));   // ตารางชนะ · TrueMoney ไม่เปิด = ตัด
        Assert.Equal(all, DocumentPayLinkPolicy.Allowed(all, Array.Empty<string>(), null));                  // ตารางว่าง = ตามบริษัท
        Assert.Equal(new[] { "PromptPay", "Card" }, DocumentPayLinkPolicy.OnlineOnly(all));
        Assert.Equal(new[] { "TrueMoney" }, DocumentPayLinkPolicy.Unknown(all, new[] { "promptpay", "TrueMoney", " " }));
    }

    [Fact]
    public void ช่องทาง_json_ไปกลับ_และค่าว่างเป็น_null()
    {
        var json = DocumentPayLinkPolicy.SerializeChannels(new[] { "PromptPay", "promptpay", " Card " });
        Assert.Equal(new[] { "PromptPay", "Card" }, DocumentPayLinkPolicy.ParseChannels(json));
        Assert.Null(DocumentPayLinkPolicy.SerializeChannels(Array.Empty<string>()));
        Assert.Null(DocumentPayLinkPolicy.ParseChannels("{ไม่ใช่ json"));
        Assert.Equal("โอนเข้าบัญชีธนาคาร / พร้อมเพย์ของบริษัท", DocumentPayLinkPolicy.Label("ManualSlip"));
    }

    // ───────── ใบไหนออกลิงก์ได้ ─────────

    [Theory]
    [InlineData(DocumentType.Invoice, DocumentStatus.Approved, 1070, true, "THB", null)]
    [InlineData(DocumentType.TaxInvoice, DocumentStatus.PartiallyPaid, 10, true, null, null)]
    [InlineData(DocumentType.Invoice, DocumentStatus.Draft, 1070, true, "THB", "อนุมัติ")]
    [InlineData(DocumentType.Invoice, DocumentStatus.WaitingApproval, 1070, true, "THB", "อนุมัติ")]
    [InlineData(DocumentType.Invoice, DocumentStatus.Voided, 1070, true, "THB", "ยกเลิก")]
    [InlineData(DocumentType.Invoice, DocumentStatus.Paid, 0, true, "THB", "ชำระครบ")]
    [InlineData(DocumentType.Invoice, DocumentStatus.Approved, 1070, true, "USD", "THB")]
    [InlineData(DocumentType.PurchaseInvoice, DocumentStatus.Approved, 1070, false, "THB", "ฝั่งขาย")]
    [InlineData(DocumentType.DebitNote, DocumentStatus.Approved, 100, false, "THB", "ฝั่งขาย")]
    [InlineData(DocumentType.Quotation, DocumentStatus.Approved, 100, true, "THB", "ฝั่งขาย")]
    public void ใบที่ออกลิงก์ได้(DocumentType type, DocumentStatus status, double balance, bool sale, string? cur, string? expect)
    {
        var why = DocumentPayLinkPolicy.IssueRefusal(type, status, (decimal)balance, sale, cur, null);
        if (expect == null) Assert.Null(why); else Assert.Contains(expect, why);
    }

    [Fact]
    public void ใบเพิ่มหนี้_ดูฝั่งจากใบต้นทาง()
    {
        Assert.True(DocumentPayLinkPolicy.IsSaleSide(DocumentType.DebitNote, DocumentType.Invoice));
        Assert.True(DocumentPayLinkPolicy.IsSaleSide(DocumentType.DebitNote, null));
        Assert.False(DocumentPayLinkPolicy.IsSaleSide(DocumentType.DebitNote, DocumentType.PurchaseInvoice));
        Assert.True(DocumentPayLinkPolicy.IsSaleSide(DocumentType.Invoice, null));
        Assert.False(DocumentPayLinkPolicy.IsSaleSide(DocumentType.PurchaseInvoice, null));
    }

    // ───────── ยอด + WHT ─────────

    [Fact]
    public void ยอดสุทธิ_หักภาษีที่ลูกค้าจะหัก_แสดงทั้งยอดเต็มและยอดสุทธิ()
    {
        // รอบ 244 (แก้ CI รอบ 241): ยอดค้างของใบเป็นยอดสุทธิหลังหักแล้ว — ใบ 1,000 + VAT 70 − WHT 30 ⇒ TotalAmount/ยอดค้าง 1,040 (WhtGrossUp.PayeeTotal)
        // ⇒ โอน 1,040 · ยอดก่อนหัก 1,070 · เดิมลบ WHT ซ้ำ (ลิงก์เรียก 1,010 ใบค้าง 30 ตลอดกาล)
        var a = DocumentPayLinkPolicy.AmountsFor(1040m, 30m, 0m);
        Assert.Equal((1070m, 30m, 1040m), (a.Gross, a.Wht, a.Net));
        // งวดก่อนหักไปแล้ว 15 (จ่ายสุทธิ 520) ⇒ ค้าง 520 · เหลือหัก 15
        var b = DocumentPayLinkPolicy.AmountsFor(520m, 30m, 15m);
        Assert.Equal((535m, 15m, 520m), (b.Gross, b.Wht, b.Net));
        // ทิศตรงข้าม: ไม่มี WHT ⇒ ยอดค้างเดิม · หักครบแล้ว ⇒ ไม่ติดลบ · ปิดหนี้แล้ว ⇒ ศูนย์ทั้งหมด
        var n = DocumentPayLinkPolicy.AmountsFor(1070m, 0m, 0m);
        Assert.Equal((1070m, 0m, 1070m), (n.Gross, n.Wht, n.Net));
        Assert.Equal(0m, DocumentPayLinkPolicy.AmountsFor(100m, 30m, 40m).Wht);
        var z = DocumentPayLinkPolicy.AmountsFor(0m, 30m, 0m);
        Assert.Equal((0m, 0m, 0m), (z.Gross, z.Wht, z.Net));
    }

    [Fact]
    public void เงินเข้ายอดสุทธิพอดี_บันทึก_WHT_ที่เหลือทั้งก้อน_ไม่ตรง_ให้ตัวเดิมตัดสิน()
    {
        Assert.Equal(30m, DocumentPayLinkPolicy.WhtForPayment(1040m, 1040m, 30m));
        Assert.Equal(30m, DocumentPayLinkPolicy.WhtForPayment(1040.01m, 1040m, 30m));   // ปัดเศษ ±0.01
        Assert.Null(DocumentPayLinkPolicy.WhtForPayment(500m, 1040m, 30m));            // จ่ายบางส่วน (เส้น staff) = พฤติกรรมเดิม
        Assert.Null(DocumentPayLinkPolicy.WhtForPayment(1010m, 1040m, 30m));           // รูปเดิมของรอบ 241 (ยอดค้าง − WHT) ≠ ยอดค้าง ⇒ ไม่บันทึกทั้งก้อน
        Assert.Null(DocumentPayLinkPolicy.WhtForPayment(1040m, 1040m, 0m));
    }

    // ───────── ลิงก์ ─────────

    [Fact]
    public void ลิงก์ใช้ได้_เฉพาะมี_token_ไม่เพิกถอน_ไม่หมดอายุ_และ_token_สุ่มไม่ซ้ำ()
    {
        var now = new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);
        Assert.True(DocumentPayLinkPolicy.LinkUsable("t", now.AddDays(1), null, now, null));
        Assert.False(DocumentPayLinkPolicy.LinkUsable("t", now.AddDays(1), now, now, null));   // เพิกถอน
        Assert.False(DocumentPayLinkPolicy.LinkUsable("t", now, null, now, null));             // หมดอายุ
        Assert.False(DocumentPayLinkPolicy.LinkUsable(null, null, null, now, null));
        var t1 = DocumentPayLinkPolicy.NewToken();
        Assert.Equal(64, t1.Length);
        Assert.NotEqual(t1, DocumentPayLinkPolicy.NewToken());
        var cid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("https://app.x/pages/pay-document.html?c=11111111-2222-3333-4444-555555555555&t=ab",
            DocumentPayLinkPolicy.Url("https://app.x/", cid, "ab"));
    }

    [Fact]
    public void ฐานเก็บ_hash_ของ_token_ตัวเดียวกับลิงก์ค่าบริการ_เทียบเวลาคงที่()
    {
        var t = DocumentPayLinkPolicy.NewToken();
        var h = DocumentPayLinkPolicy.TokenHash(t);
        Assert.Equal(PlatformPaymentLinkRules.Hash(t), h);        // ฟังก์ชันเดียว (ไม่มีสูตรที่สอง)
        Assert.NotEqual(t, h);
        Assert.True(DocumentPayLinkPolicy.TokenMatches(t, h));
        Assert.False(DocumentPayLinkPolicy.TokenMatches(h, h));   // ถือ hash ≠ ถือ token
        Assert.False(DocumentPayLinkPolicy.TokenMatches(t, null));
        Assert.False(DocumentPayLinkPolicy.TokenMatches(null, h));
        Assert.True(DocumentPayLinkPolicy.LooksValid(t));
        Assert.False(DocumentPayLinkPolicy.LooksValid("../../etc"));
    }

    [Fact]
    public void บล็อกอีเมลลิงก์_หนีค่า_และแสดงยอดหักภาษีเมื่อมี()
    {
        var a = DocumentPayLinkPolicy.AmountsFor(1040m, 30m, 0m);
        var html = DocumentPayLinkPolicy.EmailBlock("https://x/p?c=1&t=<b>", a, isEnglish: false);
        Assert.Contains("&lt;b&gt;", html);
        Assert.DoesNotContain("<b>'", html);
        Assert.Contains("1,040.00", html);
        Assert.Contains("ยอดก่อนหักภาษี ณ ที่จ่าย 1,070.00", html);
        Assert.Contains("50 ทวิ", html);
        var noWht = DocumentPayLinkPolicy.EmailBlock("https://x", DocumentPayLinkPolicy.AmountsFor(1070m, 0m, 0m), isEnglish: true);
        Assert.DoesNotContain("withholding", noWht);
        Assert.Contains("Pay online", noWht);
    }

    [Fact]
    public void ผู้ซื้อมีหน้าที่หักไหม_นิติบุคคล_รัฐ_หัก_บุคคล_ไม่หัก_ไม่รู้_เชื่อใบ()
    {
        Assert.True(DocumentPayLinkPolicy.BuyerWithholds(ContactType.JuristicPerson, null));
        Assert.True(DocumentPayLinkPolicy.BuyerWithholds(ContactType.GovernmentAgency, null));
        Assert.False(DocumentPayLinkPolicy.BuyerWithholds(ContactType.Individual, "0107537000017"));   // ชนิดที่ประกาศชนะ
        Assert.True(DocumentPayLinkPolicy.BuyerWithholds(ContactType.Unknown, "0107537000017"));       // ไม่รู้ชนิด ⇒ รูปเลข
        Assert.Null(DocumentPayLinkPolicy.BuyerWithholds(ContactType.Unknown, null));
        Assert.Equal(0m, DocumentPayLinkPolicy.ExpectedWht(30m, false));
        Assert.Equal(30m, DocumentPayLinkPolicy.ExpectedWht(30m, true));
        Assert.Equal(30m, DocumentPayLinkPolicy.ExpectedWht(30m, null));
        Assert.Contains("ไม่มีหน้าที่หัก", DocumentPayLinkPolicy.WhtMismatchNote(30m, false));
        Assert.Null(DocumentPayLinkPolicy.WhtMismatchNote(30m, true));
        Assert.Null(DocumentPayLinkPolicy.WhtMismatchNote(0m, false));
    }

    [Fact]
    public void คิวเก็บที่หมาย_ลิงก์จริงประกอบตอนส่ง_ลิงก์ใช้ไม่ได้ถอดบล็อกทิ้ง()
    {
        var a = DocumentPayLinkPolicy.AmountsFor(1040m, 30m, 0m);
        var body = "<p>เนื้อ {PayLink}</p>" + DocumentPayLinkPolicy.EmailBlock(DocumentPayLinkPolicy.QueuePlaceholder, a, false);
        Assert.True(DocumentPayLinkPolicy.HasPlaceholder(body));
        Assert.DoesNotContain("t=", body);
        var (rest, block) = DocumentPayLinkPolicy.SplitBlock(body);
        Assert.Equal("<p>เนื้อ {PayLink}</p>", rest);
        Assert.NotNull(block);
        var sent = DocumentPayLinkPolicy.ResolvePlaceholder(body, "https://x/p?c=1&t=ab", html: true);
        Assert.Contains("https://x/p?c=1&amp;t=ab", sent);
        Assert.False(DocumentPayLinkPolicy.HasPlaceholder(sent));
        var gone = DocumentPayLinkPolicy.ResolvePlaceholder(body, null, html: true);
        Assert.Equal("<p>เนื้อ </p>", gone);   // ไม่มีปุ่มที่ไปไหนไม่ได้
        Assert.Equal("ชำระที่ https://x", DocumentPayLinkPolicy.ResolvePlaceholder("ชำระที่ {PayLink}", "https://x", html: false));
    }

    // ───────── งานหลังรับชำระ ─────────

    private static PostPaymentAutomationPolicy.Facts F(
        bool sale = true, DocumentStatus status = DocumentStatus.Paid, bool voided = false, bool full = true, int alloc = 0,
        bool hasReceipt = false, bool receiptDraft = false, bool receiptVat = false, bool serves = false, bool mayVat = false,
        string? email = "ap@buyer.co.th", IReadOnlyList<string>? buyerMissing = null, decimal wht = 0m, bool whtRegistered = true,
        PaymentEntrySource source = PaymentEntrySource.Manual, string? taxInvoice = null)
        => new(sale, status, voided, full, alloc, hasReceipt, receiptDraft, receiptVat, serves, mayVat, email, buyerMissing, wht, whtRegistered,
            source, taxInvoice);

    private static readonly PostPaymentAutomationPolicy.Settings On = new(EmailReceipt: true, IncludePartial: false);

    [Fact]
    public void ไม่มีใบเสร็จ_ออกด้วยเส้นเดิมแล้วส่ง()
    {
        var p = PostPaymentAutomationPolicy.Decide(On, F());
        Assert.Equal(new[] { PostPaymentAutomationPolicy.Step.IssueReceipt, PostPaymentAutomationPolicy.Step.EmailReceipt }, p.Steps);
        Assert.Empty(p.Blocked);
        Assert.Null(p.Skipped);
    }

    [Fact]
    public void มีใบเสร็จแล้ว_ส่งอย่างเดียว_ใบต้นทางเป็นใบเสร็จในตัว_ส่งใบต้นทาง()
    {
        Assert.Equal(new[] { PostPaymentAutomationPolicy.Step.EmailReceipt }, PostPaymentAutomationPolicy.Decide(On, F(hasReceipt: true)).Steps);
        Assert.Equal(new[] { PostPaymentAutomationPolicy.Step.EmailSourceAsReceipt }, PostPaymentAutomationPolicy.Decide(On, F(serves: true)).Steps);
    }

    [Fact]
    public void ข้อมูลผู้ซื้อไม่ครบสำหรับใบกำกับ_ไม่ออก_ไม่ส่ง_และบอกช่องที่ขาด()
    {
        var missing = new[] { "ที่อยู่ผู้ซื้อ" };
        var p = PostPaymentAutomationPolicy.Decide(On, F(mayVat: true, buyerMissing: missing));
        Assert.Empty(p.Steps);
        Assert.Contains("ที่อยู่ผู้ซื้อ", Assert.Single(p.Blocked));
        // ใบเสร็จถือ VAT ที่ออกแล้วแต่ผู้ซื้อไม่ครบ ⇒ ไม่ส่ง
        Assert.Empty(PostPaymentAutomationPolicy.Decide(On, F(hasReceipt: true, receiptVat: true, buyerMissing: missing)).Steps);
        // ทิศตรงข้าม: ใบเสร็จเปล่า (ไม่ถือ VAT — ใบกำกับออกไปแล้วที่ต้นทาง) ⇒ ผู้ซื้อขาดก็ส่งได้
        Assert.Equal(new[] { PostPaymentAutomationPolicy.Step.EmailReceipt },
            PostPaymentAutomationPolicy.Decide(On, F(hasReceipt: true, receiptVat: false, buyerMissing: missing)).Steps);
        // ทิศตรงข้าม: ออกใหม่ที่ไม่ถือ VAT (ยกเว้น VAT / ใบกำกับต้นทาง) ⇒ ออกได้
        Assert.Contains(PostPaymentAutomationPolicy.Step.IssueReceipt,
            PostPaymentAutomationPolicy.Decide(On, F(mayVat: false, buyerMissing: missing)).Steps);
    }

    [Fact]
    public void หยุดพร้อมเหตุผล_ไม่มีอีเมล_กระจายหลายใบ_ใบเสร็จเป็นร่าง()
    {
        Assert.Contains("อีเมล", Assert.Single(PostPaymentAutomationPolicy.Decide(On, F(email: "ไม่ใช่อีเมล")).Blocked));
        Assert.Contains("หลายเอกสาร", Assert.Single(PostPaymentAutomationPolicy.Decide(On, F(alloc: 2)).Blocked));
        Assert.Contains("ฉบับร่าง", Assert.Single(PostPaymentAutomationPolicy.Decide(On, F(hasReceipt: true, receiptDraft: true)).Blocked));
    }

    [Fact]
    public void ข้ามเมื่อไม่มีอะไรต้องทำ()
    {
        Assert.NotNull(PostPaymentAutomationPolicy.Decide(new(false, false), F()).Skipped);       // ปิด
        Assert.NotNull(PostPaymentAutomationPolicy.Decide(On, F(voided: true)).Skipped);          // ยกเลิกการชำระ
        Assert.NotNull(PostPaymentAutomationPolicy.Decide(On, F(sale: false)).Skipped);           // ฝั่งซื้อ
        Assert.NotNull(PostPaymentAutomationPolicy.Decide(On, F(status: DocumentStatus.Voided)).Skipped);   // ใบยกเลิก
        Assert.NotNull(PostPaymentAutomationPolicy.Decide(On, F(full: false)).Skipped);           // ชำระบางส่วน (ค่าตั้งเฉพาะครบ)
        // ทิศตรงข้าม: เปิดส่งทุกงวด ⇒ งวดบางส่วนก็ส่ง
        Assert.NotEmpty(PostPaymentAutomationPolicy.Decide(new(true, true), F(full: false)).Steps);
    }

    [Fact]
    public void จุดต่อ_WHT_ขั้นต่ำ_ตรวจทะเบียนรอ_50ทวิ_ไม่หยุดงาน()
    {
        var p = PostPaymentAutomationPolicy.Decide(On, F(wht: 30m, whtRegistered: false));
        Assert.Contains(PostPaymentAutomationPolicy.WhtCreditMissingNote, p.Notes);
        Assert.NotEmpty(p.Steps);    // ยังส่งใบเสร็จ
        Assert.Empty(PostPaymentAutomationPolicy.Decide(On, F(wht: 30m, whtRegistered: true)).Notes);
        Assert.Empty(PostPaymentAutomationPolicy.Decide(On, F(wht: 0m, whtRegistered: false)).Notes);
    }

    [Fact]
    public void ค่าตารางทับค่าบริษัททีละช่อง_และคีย์กันซ้ำคงที่ต่อการชำระ()
    {
        var company = new PostPaymentAutomationPolicy.Settings(true, false);
        Assert.Equal(company, PostPaymentAutomationPolicy.Effective(company, null));
        Assert.Equal(new PostPaymentAutomationPolicy.Settings(false, false),
            PostPaymentAutomationPolicy.Effective(company, new(EmailReceipt: false, IncludePartial: null)));
        Assert.Equal(new PostPaymentAutomationPolicy.Settings(true, true),
            PostPaymentAutomationPolicy.Effective(company, new(EmailReceipt: null, IncludePartial: true)));
        var pid = Guid.NewGuid();
        Assert.Equal(PostPaymentAutomationPolicy.IdempotencyKey(pid), PostPaymentAutomationPolicy.IdempotencyKey(pid));
        Assert.NotEqual(PostPaymentAutomationPolicy.IdempotencyKey(pid), PostPaymentAutomationPolicy.IdempotencyKey(Guid.NewGuid()));
        Assert.Equal("หยุด — ต้องให้คนดำเนินการ", PostPaymentAutomationPolicy.StatusLabel(PostPaymentAutomationPolicy.StatusBlocked));
        Assert.True(PostPaymentAutomationPolicy.RetryDelay(1) < PostPaymentAutomationPolicy.RetryDelay(3));
    }

    // ───────── จุดต่อบล็อกเพิ่มในอีเมลใบเสร็จ ─────────

    [Fact]
    public void บล็อกเพิ่ม_ไม่มี_คืน_null_เทมเพลตเดิม_มีแล้วหนีค่า_ตัดลิงก์ที่ไม่ใช่_https()
    {
        Assert.Null(ReceiptEmailSections.ComposeBody(false, "<p>x</p>", "lead", null));
        Assert.Null(ReceiptEmailSections.ComposeBody(true, "<p>x</p>", "lead", Array.Empty<ReceiptEmailSections.Section>()));
        var s = new[] { new ReceiptEmailSections.Section("หัว <i>", "บรรทัด1\nบรรทัด2", "javascript:alert(1)") };
        var html = ReceiptEmailSections.ComposeBody(false, "<p>x</p>", "lead", s)!;
        Assert.StartsWith("<p>x</p>", html);
        Assert.Contains("หัว &lt;i&gt;", html);
        Assert.Contains("บรรทัด1<br>บรรทัด2", html);
        Assert.DoesNotContain("javascript", html);
        var plain = ReceiptEmailSections.ComposeBody(true, "<p>x</p>", "แนบใบเสร็จ", new[] { new ReceiptEmailSections.Section("ลิงก์", "เปิด", "https://a.b/c") })!;
        Assert.StartsWith("แนบใบเสร็จ", plain);
        Assert.Contains("https://a.b/c", plain);
        Assert.DoesNotContain("<", plain);
    }
}
