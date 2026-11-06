using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Email;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 244 — เส้นจริงบน PostgreSQL ของ e-Tax by Email (ETAX_BY_EMAIL_AUDIT): บริษัทตั้งครบ + SMTP ของตัวเองทดสอบผ่าน ⇒ ส่งแบบ e-Tax
/// (หัวเรื่อง [ddMMyyyy พ.ศ.][INV][เลขที่] · To 1 · CC ETDA · แนบ 1 ไฟล์ · ส่งแล้วรอประทับเวลา) · ส่งซ้ำต้องยืนยัน · SMTP ยังไม่ทดสอบ ⇒ ปฏิเสธ
/// และไม่เรียกผู้ส่งของแพลตฟอร์ม · โหมดทดสอบ ⇒ UAT · ไฟล์เกิน 3 MB ⇒ ปฏิเสธ · ใบยกเลิก ⇒ ปฏิเสธ · ทิศตรงข้าม: บริษัทไม่ใช้ by Email /
/// ใบเสนอราคา ⇒ อีเมลธรรมดา · คิวตามตาราง + งานหลังรับชำระเดินทาง e-Tax · บันทึกการประทับเวลา ⇒ ยกเลิกเงียบไม่ได้ (Accepted)
/// </summary>
[Trait("Category", "Db")]
public class EtaxByEmailRound244DbTests
{
    private readonly ITestOutputHelper _out;
    public EtaxByEmailRound244DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly DateTime DocDate = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);

    private sealed class FakeSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();
        public EmailProvider Provider => EmailProvider.Smtp;
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(EmailSendResult.Ok("mid-244"));
        }
    }

    /// <summary>ผู้ส่งของบริษัท + ผู้ส่งของแพลตฟอร์มแยกกัน · บันทึกทุกเมธอดที่ถูกเรียก (พิสูจน์ว่าไม่ถอยไปแพลตฟอร์ม)</summary>
    private sealed class Senders
    {
        public FakeSender Company { get; } = new();
        public FakeSender Platform { get; } = new();
        public List<string> Calls { get; } = new();
        public IEmailSenderFactory Factory() => F4Proxy<IEmailSenderFactory>.Create((m, _) =>
        {
            Calls.Add(m);
            return m switch
            {
                nameof(IEmailSenderFactory.PlatformFromAddress) => "no-reply@nextacc.net",
                nameof(IEmailSenderFactory.GetSenderForSettings) => Company,
                _ => Platform,
            };
        });
    }

    private static IEtaxInvoiceService Etax(int pdfBytes = 20_000)
        => F4Proxy<IEtaxInvoiceService>.Create((m, _) => m == nameof(IEtaxInvoiceService.GeneratePdfA3Async)
            ? (new byte[pdfBytes], "etax.pdf")
            : null);

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();

    private static DocumentEmailService Svc(Accounting.Data.AccountingDbContext db, Senders s, int pdfBytes = 20_000)
        => new(db, s.Factory(), Etax(pdfBytes), NullLogger<DocumentEmailService>.Instance, pdf: null, storage: null, config: Config());

    private sealed record Seed(Guid CompanyId, Document Doc, EtaxInvoice? EtaxRow);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, string tag, EtaxMode mode = EtaxMode.ByEmail,
        bool smtpTested = true, bool useUat = false, DocumentType type = DocumentType.TaxInvoice, DocumentStatus status = DocumentStatus.Approved,
        bool withEtaxRow = true)
    {
        var company = new Company { Name = "บริษัท 244 " + tag, TaxId = "0105556000007", Email = "office@seller244.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings
        {
            CompanyId = company.Id, EtaxEnabled = true, EtaxMode = mode,
            // ฝ่ายค้าน B1: EtaxTestMode (ระบบเต็ม) คงค่าตั้งต้นของ entity (true) — ต้องไม่มีผลกับที่อยู่ CC · ทดสอบ by Email ใช้ธงแยก
            EtaxByEmailUseUat = useUat,
            EtaxByEmailRdRegistered = true, EtaxByEmailSenderEmail = "etax@seller244.co.th",
            EmailProvider = EmailProvider.Smtp, EmailSmtpHost = "smtp.seller244.co.th", EmailSmtpUsername = "etax@seller244.co.th",
            EmailFromAddress = "ETAX@seller244.co.th", EmailConfigured = smtpTested,
        });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 244 จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = "0107537000017", Email = "ap@buyer244.co.th", Address = "1 ถนนสุขุมวิท กรุงเทพฯ",
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var prefix = type switch { DocumentType.Quotation => "QT244-", DocumentType.Receipt => "RE244-", _ => "IV244-" };
        var doc = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = type,
            DocumentNumber = prefix + Random.Shared.Next(100000, 999999), Status = status, DocumentDate = DocDate,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB", IsTaxInvoiceByLaw = true,
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        EtaxInvoice? row = null;
        if (withEtaxRow)
        {
            row = new EtaxInvoice
            {
                CompanyId = company.Id, DocumentId = doc.Id, EtaxRefNumber = doc.DocumentNumber, XmlContent = "<x/>",
                Status = EtaxStatus.Generated, SellerName = company.Name, SellerTaxId = company.TaxId, BuyerName = contact.Name,
            };
            db.EtaxInvoices.Add(row);
            await db.SaveChangesAsync();
        }
        return new Seed(company.Id, doc, row);
    }

    private static SendDocumentEmailRequest Req(string to = "ap@buyer244.co.th", string? cc = null, string? subject = "หัวเรื่องที่ผู้ใช้พิมพ์")
        => new(to, cc, null, subject, null, AttachPdf: true);

    [Fact]
    public async Task ตั้งครบ_SMTPทดสอบผ่าน_ส่งแบบeTax_หัวเรื่องตามคู่มือ_To1_CCเอทีดีเอ_แนบ1ไฟล์_ส่งซ้ำต้องยืนยัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "ok " + Guid.NewGuid().ToString("N")[..6]);
        var senders = new Senders();
        var svc = Svc(db, senders);

        var log = await svc.SendDocumentRoutedAsync(s.CompanyId, s.Doc.Id, Req());
        Assert.Equal(EmailLogStatus.Sent, log.Status);
        Assert.True(log.IsEtaxByEmail);
        Assert.True(log.IncludedRdTimestamp);
        var msg = Assert.Single(senders.Company.Sent);
        Assert.Empty(senders.Platform.Sent);
        Assert.DoesNotContain(nameof(IEmailSenderFactory.GetGlobalFallbackSender), senders.Calls);
        Assert.Equal($"[06112569][INV][{s.Doc.DocumentNumber}]", msg.Subject);   // หัวเรื่องของผู้ใช้ไม่ถูกใช้
        Assert.Equal(new[] { "ap@buyer244.co.th" }, msg.To);
        Assert.Equal(new[] { "csemail@etax.teda.th" }, msg.Cc);
        Assert.Empty(msg.Bcc);
        Assert.Equal("etax@seller244.co.th", msg.FromAddress);
        var att = Assert.Single(msg.Attachments);
        Assert.Equal("application/pdf", att.ContentType);

        db.ChangeTracker.Clear();
        var row = await db.EtaxInvoices.AsNoTracking().SingleAsync(e => e.Id == s.EtaxRow!.Id);
        Assert.NotNull(row.ByEmailSentAt);
        Assert.Null(row.EtdaStampedAt);                          // ไม่ประทับ "ประทับเวลาแล้ว" เอง
        Assert.Equal(EtaxStatus.Generated, row.Status);
        var eff = await DocumentVoidPreconditions.EffectiveEtaxAsync(db, s.CompanyId, new[] { s.Doc.Id });
        Assert.Equal(EtaxStatus.Accepted, eff[s.Doc.Id]);        // ฝ่ายค้าน B2: ด่านยกเลิกถือส่งพร้อม CC production = ตอบรับ (ป้าย "รอประทับเวลา" แสดงผลเท่านั้น)

        // ส่งซ้ำผ่านหน้า e-Tax โดยไม่ยืนยัน ⇒ 409 · ยืนยัน ⇒ ส่ง + audit
        var resend = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SendEtaxByEmailAsync(s.CompanyId, s.EtaxRow!.Id,
            new SendEtaxByEmailRequest("ap@buyer244.co.th", null, null, null, null)));
        Assert.Equal(EtaxByEmailFormat.ResendRuleCode, resend.RuleCode);
        Assert.Single(senders.Company.Sent);
        await svc.SendEtaxByEmailAsync(s.CompanyId, s.EtaxRow!.Id, new SendEtaxByEmailRequest("ap@buyer244.co.th", null, null, null, null, ConfirmResend: true));
        Assert.Equal(2, senders.Company.Sent.Count);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.NewValues!.Contains("etax-by-email-resend-confirmed")));

        // หลังส่ง e-Tax แล้ว การส่งจากหน้าเอกสาร (ทวง/สำเนา) = อีเมลธรรมดา ไม่ CC ETDA ซ้ำ
        Assert.Equal(EtaxEmailChannel.PlainCopyAfterEtax, (await svc.RouteAsync(s.CompanyId, s.Doc.Id)).Channel);
        var copy = await svc.SendDocumentRoutedAsync(s.CompanyId, s.Doc.Id, Req(subject: null) with { AttachPdf = false });
        Assert.False(copy.IsEtaxByEmail);
        Assert.DoesNotContain("csemail@etax.teda.th", senders.Company.Sent.Last().Cc);
    }

    [Fact]
    public async Task SMTPยังไม่ทดสอบ_ปฏิเสธพร้อมคำแนะนำ_ไม่เรียกผู้ส่งของแพลตฟอร์ม_บันทึกFailedบนเอกสาร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "untested " + Guid.NewGuid().ToString("N")[..6], smtpTested: false);
        var senders = new Senders();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db, senders).SendDocumentRoutedAsync(s.CompanyId, s.Doc.Id, Req()));
        Assert.Equal(EtaxByEmailSenderGate.RuleCode, ex.RuleCode);
        Assert.Contains("ทดสอบ", ex.Message);
        Assert.Empty(senders.Company.Sent);
        Assert.Empty(senders.Platform.Sent);
        Assert.DoesNotContain(nameof(IEmailSenderFactory.GetGlobalFallbackSender), senders.Calls);
        Assert.DoesNotContain(nameof(IEmailSenderFactory.GetSenderAsync), senders.Calls);
        var log = Assert.Single(await db.DocumentEmailLogs.AsNoTracking().Where(l => l.CompanyId == s.CompanyId).ToListAsync());
        Assert.Equal(EmailLogStatus.Failed, log.Status);
        Assert.False(log.IncludedRdTimestamp);
    }

    [Fact]
    public async Task ทิศตรงข้าม_บริษัทไม่ใช้ByEmail_และใบเสนอราคา_ส่งอีเมลธรรมดา()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var direct = await SeedAsync(db, "direct " + Guid.NewGuid().ToString("N")[..6], mode: EtaxMode.Direct);
        var senders = new Senders();
        var log = await Svc(db, senders).SendDocumentRoutedAsync(direct.CompanyId, direct.Doc.Id, Req() with { AttachPdf = false });
        Assert.False(log.IsEtaxByEmail);
        Assert.Equal("หัวเรื่องที่ผู้ใช้พิมพ์", senders.Company.Sent.Single().Subject);
        Assert.DoesNotContain("csemail@etax.teda.th", senders.Company.Sent.Single().Cc);

        var quote = await SeedAsync(db, "quote " + Guid.NewGuid().ToString("N")[..6], type: DocumentType.Quotation, withEtaxRow: false);
        var qs = new Senders();
        var qlog = await Svc(db, qs).SendDocumentRoutedAsync(quote.CompanyId, quote.Doc.Id, Req() with { AttachPdf = false });
        Assert.False(qlog.IsEtaxByEmail);
        Assert.DoesNotContain("csemail@etax.teda.th", qs.Company.Sent.Single().Cc);
    }

    [Fact]
    public async Task ทดสอบByEmail_CCไปUAT_ไม่นับว่าถึงผู้ประทับเวลา()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "uat " + Guid.NewGuid().ToString("N")[..6], useUat: true);
        var senders = new Senders();
        var log = await Svc(db, senders).SendDocumentRoutedAsync(s.CompanyId, s.Doc.Id, Req());
        Assert.Equal(new[] { "csemail@uat.etax.teda.th" }, senders.Company.Sent.Single().Cc);
        Assert.False(log.IncludedRdTimestamp);
        db.ChangeTracker.Clear();
        Assert.Null((await db.EtaxInvoices.AsNoTracking().SingleAsync(e => e.Id == s.EtaxRow!.Id)).ByEmailSentAt);
        Assert.True((await Svc(db, new Senders()).PreviewEtaxEmailAsync(s.CompanyId, s.Doc.Id)).UsesUat);   // หน้าส่งถามยืนยัน
    }

    // ฝ่ายค้าน B1 ทิศตรงข้าม: บริษัทเดิมที่ EtaxTestMode=true (ค่าตั้งต้นของ entity · ระบบเต็ม) แต่ไม่ได้ติ๊กทดสอบ by Email ⇒ ยังส่ง CC production
    [Fact]
    public async Task บริษัทเดิมEtaxTestModeเปิด_ไม่ติ๊กทดสอบByEmail_ยังส่งCCไปproduction()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "legacy-test " + Guid.NewGuid().ToString("N")[..6]);
        var settings = await db.CompanySettings.FirstAsync(x => x.CompanyId == s.CompanyId);
        settings.EtaxTestMode = true;
        await db.SaveChangesAsync();
        var senders = new Senders();
        var log = await Svc(db, senders).SendDocumentRoutedAsync(s.CompanyId, s.Doc.Id, Req());
        Assert.Equal(new[] { "csemail@etax.teda.th" }, senders.Company.Sent.Single().Cc);
        Assert.True(log.IncludedRdTimestamp);
    }

    [Fact]
    public async Task ไฟล์เกิน3MB_ปฏิเสธ_ใบยกเลิก_ปฏิเสธ_CCอื่น_ปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var big = await SeedAsync(db, "big " + Guid.NewGuid().ToString("N")[..6]);
        var bs = new Senders();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Svc(db, bs, pdfBytes: (int)EtaxByEmailFormat.MaxAttachmentBytes + 1).SendDocumentRoutedAsync(big.CompanyId, big.Doc.Id, Req()));
        Assert.Contains("3 MB", ex.Message);
        Assert.Empty(bs.Company.Sent);

        var voided = await SeedAsync(db, "void " + Guid.NewGuid().ToString("N")[..6], status: DocumentStatus.Voided);
        var vs = new Senders();
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db, vs).SendEtaxByEmailAsync(voided.CompanyId, voided.EtaxRow!.Id,
            new SendEtaxByEmailRequest("ap@buyer244.co.th", null, null, null, null)));
        Assert.Empty(vs.Company.Sent);

        var cc = await SeedAsync(db, "cc " + Guid.NewGuid().ToString("N")[..6]);
        var cs = new Senders();
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db, cs).SendDocumentRoutedAsync(cc.CompanyId, cc.Doc.Id, Req(cc: "boss@seller244.co.th")));
        Assert.Empty(cs.Company.Sent);
    }

    [Fact]
    public async Task คิวตามตาราง_ใบกำกับของบริษัทByEmail_ไปทางeTax_ด่านปฏิเสธ_คิวFailedพร้อมเหตุ_บริษัทอื่นเดินเส้นเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "queue " + Guid.NewGuid().ToString("N")[..6]);
        var senders = new Senders();
        var sched = new EmailScheduleService(db, senders.Factory(), F4Proxy<IPdfGenerationService>.Create((_, _) => null),
            NullLogger<EmailScheduleService>.Instance, payLinks: null, config: Config(), docEmail: Svc(db, senders));
        EmailQueue Q(Guid cid, Guid docId, string? bcc = null) => new()
        {
            CompanyId = cid, EntityType = "Document", EntityId = docId, ToEmail = "ap@buyer244.co.th", BccEmail = bcc,
            Subject = "ใบกำกับภาษีจากตาราง", Body = "<p>แนบเอกสาร</p>", AttachPdf = true, ScheduledFor = DateTime.UtcNow, Status = "Sending",
            IdempotencyKey = "q244-" + Guid.NewGuid().ToString("N"),
        };
        var ok = Q(s.CompanyId, s.Doc.Id);
        db.EmailQueues.Add(ok);
        await db.SaveChangesAsync();
        Assert.True(await sched.SendViaEtaxIfRequiredAsync(ok, default));
        Assert.Equal("Sent", ok.Status);
        var msg = senders.Company.Sent.Single();
        Assert.Equal($"[06112569][INV][{s.Doc.DocumentNumber}]", msg.Subject);
        Assert.Contains("csemail@etax.teda.th", msg.Cc);

        // ตารางตั้ง BCC ⇒ e-Tax by Email ไม่รับ ⇒ Failed พร้อมเหตุ (ไม่ส่งเป็นอีเมลธรรมดาแทน)
        var s2 = await SeedAsync(db, "queue-bcc " + Guid.NewGuid().ToString("N")[..6]);
        var bcc = Q(s2.CompanyId, s2.Doc.Id, bcc: "me@seller244.co.th");
        db.EmailQueues.Add(bcc);
        await db.SaveChangesAsync();
        Assert.False(await sched.SendViaEtaxIfRequiredAsync(bcc, default));
        Assert.Equal("Failed", bcc.Status);
        Assert.Contains("e-Tax by Email", bcc.ErrorMessage);
        Assert.Single(senders.Company.Sent);
        Assert.Empty(senders.Platform.Sent);

        // ทิศตรงข้าม: บริษัทไม่ใช้ by Email ⇒ null (เดินเส้นคิวเดิม)
        var plain = await SeedAsync(db, "queue-plain " + Guid.NewGuid().ToString("N")[..6], mode: EtaxMode.None);
        Assert.Null(await sched.SendViaEtaxIfRequiredAsync(Q(plain.CompanyId, plain.Doc.Id), default));
    }

    /// <summary>ใบแจ้งหนี้ชำระครบ + ใบเสร็จรับเงิน/ใบกำกับภาษี (ถือ VAT) ของการชำระนั้น + แถว e-Tax ของใบเสร็จ — สำหรับงานหลังรับชำระ</summary>
    private static async Task<(Guid CompanyId, Payment Pay, Document Receipt)> SeedPaidAsync(Accounting.Data.AccountingDbContext db, string tag, bool smtpTested)
    {
        var s = await SeedAsync(db, tag, smtpTested: smtpTested, type: DocumentType.Invoice, status: DocumentStatus.Paid, withEtaxRow: false);
        var settings = await db.CompanySettings.FirstAsync(x => x.CompanyId == s.CompanyId);
        settings.PostPayEmailReceipt = true;
        var inv = await db.Documents.FirstAsync(d => d.Id == s.Doc.Id);
        inv.PaidAmount = 1_070m;
        inv.BalanceDue = 0m;
        var pay = new Payment
        {
            CompanyId = s.CompanyId, PaymentNumber = "PAY244-" + Random.Shared.Next(100000, 999999), DocumentId = inv.Id,
            PaymentDate = DocDate, Amount = 1_070m, PaymentMethod = PaymentMethod.BankTransfer,
        };
        db.Payments.Add(pay);
        await db.SaveChangesAsync();
        var rec = new Document
        {
            CompanyId = s.CompanyId, ContactId = inv.ContactId, DocumentType = DocumentType.Receipt,
            DocumentNumber = "RE244-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Paid, DocumentDate = DocDate,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, PaidAmount = 1_070m, Currency = "THB",
            RelatedDocumentId = inv.Id, IsSettlementReceipt = true, SettlementPaymentId = pay.Id, IsTaxInvoiceByLaw = true,
        };
        db.Documents.Add(rec);
        await db.SaveChangesAsync();
        pay.ReceiptDocumentId = rec.Id;
        db.EtaxInvoices.Add(new EtaxInvoice
        {
            CompanyId = s.CompanyId, DocumentId = rec.Id, EtaxRefNumber = rec.DocumentNumber, XmlContent = "<x/>", Status = EtaxStatus.Generated,
            SellerName = "ผู้ขาย", SellerTaxId = "0105556000007", BuyerName = "ผู้ซื้อ",
        });
        await db.SaveChangesAsync();
        return (s.CompanyId, pay, rec);
    }

    [Fact]
    public async Task หลังรับชำระ_ใบเสร็จถือVATของบริษัทByEmail_ส่งแบบeTax_ด่านปฏิเสธ_งานหยุดแจ้งเตือน_ไม่ส่งอีเมลธรรมดา()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        async Task<(PostPaymentAutomationRun Run, Senders Senders, List<string> Notified, Document Receipt)> RunAsync(bool smtpTested)
        {
            var (cid, pay, rec) = await SeedPaidAsync(db, "postpay " + Guid.NewGuid().ToString("N")[..6], smtpTested);
            var senders = new Senders();
            var notified = new List<string>();
            var notify = F4Proxy<INotificationEngine>.Create((m, a) => { if (m == nameof(INotificationEngine.DispatchAsync)) notified.Add((string)a[1]!); return null; });
            var svc = new PostPaymentAutomationService(db, F4Proxy<IDocumentService>.Create((_, _) => null), Svc(db, senders), senders.Factory(),
                NullLogger<PostPaymentAutomationService>.Instance, notify);
            Assert.True(await svc.EnqueueAsync(cid, pay.Id, PaymentEntrySource.Manual, null));
            var runId = (await db.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.CompanyId == cid && r.PaymentId == pay.Id)).Id;
            await svc.ProcessRunAsync(runId, default);
            db.ChangeTracker.Clear();
            return (await db.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.Id == runId), senders, notified, rec);
        }

        var ok = await RunAsync(smtpTested: true);
        Assert.Equal(PostPaymentAutomationPolicy.StatusDone, ok.Run.Status);
        var msg = ok.Senders.Company.Sent.Single();
        Assert.Equal($"[06112569][RCT][{ok.Receipt.DocumentNumber}]", msg.Subject);
        Assert.Contains("csemail@etax.teda.th", msg.Cc);

        var blocked = await RunAsync(smtpTested: false);
        Assert.Equal(PostPaymentAutomationPolicy.StatusBlocked, blocked.Run.Status);
        Assert.Contains("e-Tax by Email", blocked.Run.Message);
        Assert.Empty(blocked.Senders.Company.Sent);
        Assert.Empty(blocked.Senders.Platform.Sent);                 // ไม่ส่งเป็นอีเมลธรรมดาผ่านแพลตฟอร์มแทน
        Assert.Contains(NotificationEvents.PostPaymentAutomationBlocked, blocked.Notified);
    }

    [Fact]
    public async Task บันทึกการประทับเวลา_ต้องส่งก่อน_ไฟล์หลังส่ง_แล้วถือเป็นตอบรับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "stamp " + Guid.NewGuid().ToString("N")[..6]);
        var senders = new Senders();
        var svc = Svc(db, senders);
        // ยังไม่ส่ง ⇒ บันทึกไม่ได้
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RecordEtdaStampAsync(s.CompanyId, s.EtaxRow!.Id,
            new RecordEtdaStampRequest(DateTime.UtcNow, Guid.NewGuid(), null), null, "owner@seller244.co.th"));
        await svc.SendDocumentRoutedAsync(s.CompanyId, s.Doc.Id, Req());
        // ไฟล์ไม่ใช่ของเอกสารนี้ ⇒ ปฏิเสธ
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RecordEtdaStampAsync(s.CompanyId, s.EtaxRow!.Id,
            new RecordEtdaStampRequest(DateTime.UtcNow, Guid.NewGuid(), null), null, "owner@seller244.co.th"));
        var file = new FileAttachment
        {
            CompanyId = s.CompanyId, EntityType = "Document", EntityId = s.Doc.Id, FileName = "stamped.pdf", OriginalFileName = "stamped.pdf",
            ContentType = "application/pdf", FileSize = 10, StoragePath = "x/stamped.pdf", CreatedAt = DateTime.UtcNow.AddMinutes(1),
        };
        db.FileAttachments.Add(file);
        await db.SaveChangesAsync();
        var row = await svc.RecordEtdaStampAsync(s.CompanyId, s.EtaxRow!.Id,
            new RecordEtdaStampRequest(DateTime.UtcNow, file.Id, "TS-244-001"), null, "owner@seller244.co.th");
        Assert.NotNull(row.EtdaStampedAt);
        Assert.Equal("TS-244-001", row.EtdaTimestampToken);
        db.ChangeTracker.Clear();
        var eff = await DocumentVoidPreconditions.EffectiveEtaxAsync(db, s.CompanyId, new[] { s.Doc.Id });
        Assert.Equal(EtaxStatus.Accepted, eff[s.Doc.Id]);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.NewValues!.Contains("etda-timestamp-recorded")));
        // บันทึกซ้ำไม่ได้
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RecordEtdaStampAsync(s.CompanyId, s.EtaxRow!.Id,
            new RecordEtdaStampRequest(DateTime.UtcNow, file.Id, null), null, "owner@seller244.co.th"));
    }
}
