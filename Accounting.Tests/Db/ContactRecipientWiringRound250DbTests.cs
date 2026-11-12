using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Notification;
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
/// รอบ 250 WP-08 — L1 ต่อสาย (LINE_MULTICHANNEL_DESIGN §3.e/§3.g/§8 L1 · AUDIT_249 WP-08 ข้อ 1–2) บน PostgreSQL จริง:
/// ค่าตั้ง "ช่องทางแจ้งเตือน" ของผู้ติดต่อมีผลกับการส่งจริง — ตารางส่ง (ทวงชำระ) ส่งถึงอีเมลที่สอง · ยกเลิกรับ ⇒ ไม่ส่ง + เหตุบนแถวคิว ·
/// ไม่ตั้งปลายทาง ⇒ Contact.Email เดิม (ไม่ถดถอย) · ใบเสร็จหลังรับชำระใช้เรื่อง "ใบเสร็จ" · ลิงก์เซ็นใช้เรื่อง "ลิงก์เซ็น" ·
/// ผู้ติดต่อ "ไม่ส่งอัตโนมัติ" ⇒ รายการประจำ/ตารางส่งไม่เข้าคิว (พร้อมเหตุ) และทิศตรงข้าม
/// </summary>
[Trait("Category", "Db")]
// รอบ 260-4 (CI db-test run 43151543956): ScanDueSoonAndOverdueAsync กวาดทุกบริษัทในฐานร่วม — สองคลาสพร้อมกันเข้าคิวใบเดียวกันด้วยตัวหาผู้รับคนละตัว
// (แถว line:none + แถวปลายทาง) ⇒ คลาสที่สแกนอยู่ collection เดียวกัน (ไม่รันขนาน)
[Collection("due-soon-overdue-scan")]
public class ContactRecipientWiringRound250DbTests
{
    private readonly ITestOutputHelper _out;
    public ContactRecipientWiringRound250DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("db-test-key-250-wp08");

    private sealed class FakeSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();
        public EmailProvider Provider => EmailProvider.Smtp;
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(EmailSendResult.Ok("mid-250-wp08"));
        }
    }

    private static IEmailSenderFactory Factory(FakeSender s) => F4Proxy<IEmailSenderFactory>.Create((m, _) => m switch
    {
        nameof(IEmailSenderFactory.PlatformFromAddress) => "no-reply@nextacc.net",
        _ => s,
    });

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();

    private static INextAccLineConfig Central() => F4Proxy<INextAccLineConfig>.Create((m, _) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
        ? Task.FromResult(NextAccLineConfigRule.PlatformGroupIds(null, null))
        : Task.FromResult(new NextAccLineSettings("T", "S", "@nextacc", null, null, NextAccLineConfigRule.SourceAdmin)));

    private static ContactRecipientResolver Resolver(AccountingDbContext db) => new(db, Secrets, Central());
    private static ContactNotifyEndpointService Endpoints(AccountingDbContext db) => new(db, Resolver(db));
    private static readonly ContactNotifyActor Actor = new(null, "tester@r250.local", "127.0.0.1");

    private static EmailScheduleService Sched(AccountingDbContext db, FakeSender sender)
        => new(db, Factory(sender), F4Proxy<IPdfGenerationService>.Create((_, _) => null), NullLogger<EmailScheduleService>.Instance,
            payLinks: null, config: Config(), recipients: Resolver(db));

    private static DateTime TodayBkk() => DateTime.UtcNow.AddHours(7).Date;

    private sealed record Seed(Guid CompanyId, Guid ContactId, Guid InvoiceId);

    /// <summary>บริษัท + ผู้ติดต่อ (อีเมลหลัก ap@…) + ใบแจ้งหนี้อนุมัติแล้วครบกำหนดอีก 3 วัน (ค้างชำระ) + กฎ "ใกล้ครบกำหนด −3 วัน"</summary>
    private static async Task<Seed> SeedAsync(AccountingDbContext db, string tag, bool autoSendDisabled = false, bool dueSoonRule = true)
    {
        var company = new Company { Name = "บริษัท WP08 " + tag, TaxId = "", Email = "office@seller250.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "ลูกค้า WP08 " + tag, IsCustomer = true, TaxId = "", Email = "ap@buyer250.co.th",
            AutoSendDisabled = autoSendDisabled,
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var inv = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Invoice,
            DocumentNumber = "INV250W8-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            DocumentDate = TodayBkk().AddDays(-27), DueDate = TodayBkk().AddDays(3),
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        if (dueSoonRule)
            db.EmailScheduleRules.Add(new EmailScheduleRule
            {
                CompanyId = company.Id, Trigger = "DocumentDueSoon", OffsetDays = -3, SendAtHour = 9, IsActive = true,
                DocumentType = DocumentType.Invoice.ToString(),
            });
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id, inv.Id);
    }

    private static Task<List<EmailQueue>> QueueRowsAsync(AccountingDbContext db, Seed s)
        => db.EmailQueues.AsNoTracking().Where(q => q.CompanyId == s.CompanyId && q.EntityId == s.InvoiceId && !q.IsDeleted).ToListAsync();

    [Fact]
    public async Task DueSoon_ส่งถึงอีเมลที่สองของผู้ติดต่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "second " + Guid.NewGuid().ToString("N")[..6]);
        await Endpoints(db).AddEmailAsync(s.CompanyId, s.ContactId,
            new AddContactNotifyEmailRequest("finance@buyer250.co.th", "การเงิน", new List<string> { nameof(NotifyTopic.Dunning) }), Actor);
        db.ChangeTracker.Clear();

        await Sched(db, new FakeSender()).ScanDueSoonAndOverdueAsync();

        var row = Assert.Single(await QueueRowsAsync(db, s));
        Assert.Equal("Pending", row.Status);
        Assert.Contains("ap@buyer250.co.th", row.ToEmail);
        Assert.Contains("finance@buyer250.co.th", row.ToEmail);
        Assert.Null(row.ErrorMessage);
    }

    [Fact]
    public async Task ยกเลิกรับ_ไม่ส่ง_บันทึกเหตุบนคิว_สแกนซ้ำไม่เพิ่ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "optout " + Guid.NewGuid().ToString("N")[..6]);
        // อีเมลหลักรับเฉพาะใบเสร็จ ⇒ เรื่องทวงชำระไม่มีปลายทางใดรับ
        await Endpoints(db).UpdatePrimaryAsync(s.CompanyId, s.ContactId,
            new UpdateContactNotifyPrimaryRequest(new List<string> { nameof(NotifyTopic.Receipt) }), Actor);
        db.ChangeTracker.Clear();

        var sched = Sched(db, new FakeSender());
        await sched.ScanDueSoonAndOverdueAsync();
        await sched.ScanDueSoonAndOverdueAsync();

        var row = Assert.Single(await QueueRowsAsync(db, s));
        Assert.Equal(ScheduledEmailSendCheck.SkippedStatus, row.Status);   // ผู้ส่งคิวไม่หยิบ (หยิบเฉพาะ Pending)
        Assert.Equal("", row.ToEmail);
        Assert.Contains("ไม่ส่งอีเมลเรื่อง", row.ErrorMessage);
        Assert.Contains("ทวงชำระ", row.ErrorMessage);
    }

    [Fact]
    public async Task ไม่ตั้งปลายทาง_ตกContactEmailเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "legacy " + Guid.NewGuid().ToString("N")[..6]);
        Assert.False(await db.ContactNotifyEndpoints.AnyAsync(e => e.CompanyId == s.CompanyId));

        await Sched(db, new FakeSender()).ScanDueSoonAndOverdueAsync();

        var row = Assert.Single(await QueueRowsAsync(db, s));
        Assert.Equal("Pending", row.Status);
        Assert.Equal("ap@buyer250.co.th", row.ToEmail);
    }

    [Fact]
    public async Task ผู้ติดต่อไม่ส่งอัตโนมัติ_ตารางส่งไม่เข้าคิว_เหตุบนคิว_ทิศตรงข้ามปิดแล้วส่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var off = await SeedAsync(db, "autosend-off " + Guid.NewGuid().ToString("N")[..6], autoSendDisabled: true);
        var on = await SeedAsync(db, "autosend-on " + Guid.NewGuid().ToString("N")[..6], autoSendDisabled: false);

        await Sched(db, new FakeSender()).ScanDueSoonAndOverdueAsync();

        var offRow = Assert.Single(await QueueRowsAsync(db, off));
        Assert.Equal(ScheduledEmailSendCheck.SkippedStatus, offRow.Status);
        Assert.Equal(AutopilotSettingsResolver.AutoSendDisabledReason, offRow.ErrorMessage);
        var onRow = Assert.Single(await QueueRowsAsync(db, on));
        Assert.Equal("Pending", onRow.Status);
    }

    [Fact]
    public async Task AutoSendDisabled_ผู้ติดต่อ_รายการประจำไม่เข้าคิว_บอกเหตุ_ทิศตรงข้ามเข้าคิว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        foreach (var disabled in new[] { true, false })
        {
            var s = await SeedAsync(db, "recurring " + disabled + " " + Guid.NewGuid().ToString("N")[..6], autoSendDisabled: disabled, dueSoonRule: false);
            var rec = new RecurringTransaction
            {
                CompanyId = s.CompanyId, Name = "ค่าบริการรายเดือน WP08", ContactId = s.ContactId, AutoSendEmail = true,
                Frequency = RecurringFrequency.Monthly, DocumentType = DocumentType.Invoice,
                TemplateType = "Document", TemplateData = "{}", StartDate = DateTime.UtcNow.Date, NextRunDate = DateTime.UtcNow.Date.AddMonths(1),
            };
            db.RecurringTransactions.Add(rec);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var outcome = await Sched(db, new FakeSender()).OnRecurringDocumentCreatedAsync(s.CompanyId, s.InvoiceId, rec.Id);

            var rows = await QueueRowsAsync(db, s);
            if (disabled)
            {
                Assert.Equal(EmailEnqueueKind.Skipped, outcome.Kind);
                Assert.True(outcome.TellUser);
                Assert.Equal(AutopilotSettingsResolver.AutoSendDisabledReason, outcome.Reason);
                Assert.Empty(rows);
            }
            else
            {
                Assert.Equal(EmailEnqueueKind.Enqueued, outcome.Kind);
                var row = Assert.Single(rows);
                Assert.Equal("ap@buyer250.co.th", row.ToEmail);
            }
        }
    }

    [Fact]
    public async Task SignRequest_ใช้resolverเรื่องsign_ผู้รับที่กรอกเองยังชนะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "sign " + Guid.NewGuid().ToString("N")[..6], dueSoonRule: false);
        var qt = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Quotation,
            DocumentNumber = "QT250W8-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, Currency = "THB",
        };
        SignRequestKinds.SetNewToken(qt, Accounting.Controllers.PublicQuotationController.NewToken(), DateTime.UtcNow);
        qt.Lines.Add(new DocumentLine { LineOrder = 1, Description = "งานติดตั้ง", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m });
        db.Documents.Add(qt);
        await db.SaveChangesAsync();
        // อีเมลหลักไม่รับลิงก์เซ็น · อีเมลจัดซื้อรับเฉพาะลิงก์เซ็น
        var ep = Endpoints(db);
        await ep.UpdatePrimaryAsync(s.CompanyId, s.ContactId, new UpdateContactNotifyPrimaryRequest(new List<string> { nameof(NotifyTopic.Document) }), Actor);
        await ep.AddEmailAsync(s.CompanyId, s.ContactId,
            new AddContactNotifyEmailRequest("purchasing@buyer250.co.th", null, new List<string> { nameof(NotifyTopic.Sign) }), Actor);
        db.ChangeTracker.Clear();

        var svc = new SignRequestService(db, Factory(new FakeSender()), Secrets, NullLogger<SignRequestService>.Instance, Config(), Resolver(db));
        var preview = await svc.PreviewEmailAsync(s.CompanyId, qt.Id, "https://app.nextacc.test", null);
        Assert.Equal(new[] { "purchasing@buyer250.co.th" }, preview.SuggestedRecipients);

        // ทิศตรงข้าม: ผู้รับที่ผู้ใช้กรอกครั้งก่อนชนะตัวหาผู้รับ
        await db.Contacts.Where(c => c.Id == s.ContactId).ExecuteUpdateAsync(x => x.SetProperty(c => c.SignLinkLastRecipients, "boss@buyer250.co.th"));
        db.ChangeTracker.Clear();
        var preview2 = await svc.PreviewEmailAsync(s.CompanyId, qt.Id, "https://app.nextacc.test", null);
        Assert.Equal(new[] { "boss@buyer250.co.th" }, preview2.SuggestedRecipients);
    }

    [Fact]
    public async Task PostPayment_ใช้resolverเรื่องreceipt_ส่งถึงปลายทางที่รับใบเสร็จเท่านั้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "receipt " + Guid.NewGuid().ToString("N")[..6], dueSoonRule: false);
        await db.CompanySettings.Where(x => x.CompanyId == s.CompanyId).ExecuteUpdateAsync(x => x.SetProperty(c => c.PostPayEmailReceipt, true));
        await db.Documents.Where(d => d.Id == s.InvoiceId)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Paid).SetProperty(d => d.BalanceDue, 0m).SetProperty(d => d.PaidAmount, 1_070m));
        // ผู้ซื้อครบข้อมูลใบกำกับ (ไม่ใช่เรื่องที่ทดสอบ — กันด่านผู้ซื้อหยุดงานก่อนถึงขั้นส่ง)
        await db.Contacts.Where(c => c.Id == s.ContactId).ExecuteUpdateAsync(x => x
            .SetProperty(c => c.TaxId, "0107537000017").SetProperty(c => c.Address, "1 ถนนสุขุมวิท กรุงเทพฯ")
            .SetProperty(c => c.ContactType, ContactType.JuristicPerson));
        db.ChangeTracker.Clear();
        var pay = new Payment
        {
            CompanyId = s.CompanyId, PaymentNumber = "PAY250W8-" + Random.Shared.Next(100000, 999999), DocumentId = s.InvoiceId,
            PaymentDate = DateTime.UtcNow.Date, Amount = 1_070m, PaymentMethod = PaymentMethod.BankTransfer,
        };
        db.Payments.Add(pay);
        await db.SaveChangesAsync();
        db.Documents.Add(new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Receipt,
            DocumentNumber = "REC250W8-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Paid,
            DocumentDate = pay.PaymentDate, SubTotal = 1_070m, TotalAmount = 1_070m, PaidAmount = 1_070m, Currency = "THB",
            RelatedDocumentId = s.InvoiceId, IsSettlementReceipt = true, SettlementPaymentId = pay.Id, IsTaxInvoiceByLaw = false,
        });
        await db.SaveChangesAsync();
        // อีเมลหลักไม่รับใบเสร็จ · อีเมลบัญชีรับใบเสร็จ
        var ep = Endpoints(db);
        await ep.UpdatePrimaryAsync(s.CompanyId, s.ContactId, new UpdateContactNotifyPrimaryRequest(new List<string> { nameof(NotifyTopic.Document) }), Actor);
        await ep.AddEmailAsync(s.CompanyId, s.ContactId,
            new AddContactNotifyEmailRequest("acct@buyer250.co.th", null, new List<string> { nameof(NotifyTopic.Receipt) }), Actor);
        db.ChangeTracker.Clear();

        var sender = new FakeSender();
        var email = new DocumentEmailService(db, Factory(sender), F4Proxy<IEtaxInvoiceService>.Create((_, _) => null),
            NullLogger<DocumentEmailService>.Instance, pdf: null, storage: null, config: Config());
        var svc = new PostPaymentAutomationService(db, F4Proxy<IDocumentService>.Create((_, _) => null), email, Factory(sender),
            NullLogger<PostPaymentAutomationService>.Instance, recipients: Resolver(db));
        Assert.True(await svc.EnqueueAsync(s.CompanyId, pay.Id, PaymentEntrySource.Gateway, "payment-gateway"));
        var run = await db.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.CompanyId == s.CompanyId && r.PaymentId == pay.Id);
        await svc.ProcessRunAsync(run.Id, default);

        var msg = Assert.Single(sender.Sent);
        Assert.Contains("acct@buyer250.co.th", msg.To);
        Assert.DoesNotContain("ap@buyer250.co.th", msg.To);
    }

    // ═══ ฝ่ายค้าน WP-08 (HIGH-1/HIGH-2/MED-2/MED-6) ═══

    /// <summary>บริษัทใช้ e-Tax by Email (SMTP บริษัทผ่าน) + ผู้ซื้อนิติบุคคลครบข้อมูล + ใบกำกับภาษีอนุมัติแล้ว</summary>
    private static async Task<Seed> SeedEtaxAsync(AccountingDbContext db, string tag)
    {
        var company = new Company { Name = "บริษัท WP08 e-Tax " + tag, TaxId = "0105556000007", Email = "office@seller250.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings
        {
            CompanyId = company.Id, EtaxEnabled = true, EtaxMode = EtaxMode.ByEmail,
            EtaxByEmailRdRegistered = true, EtaxByEmailSenderEmail = "etax@seller250.co.th",
            EmailProvider = EmailProvider.Smtp, EmailSmtpHost = "smtp.seller250.co.th", EmailSmtpUsername = "etax@seller250.co.th",
            EmailFromAddress = "etax@seller250.co.th", EmailConfigured = true, PostPayEmailReceipt = true,
        });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า e-Tax 250 จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = "0107537000017", Email = "ap@buyer250.co.th", Address = "1 ถนนสุขุมวิท กรุงเทพฯ",
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var doc = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.TaxInvoice,
            DocumentNumber = "IV250W8-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB", IsTaxInvoiceByLaw = true,
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(doc);
        db.EmailScheduleRules.Add(new EmailScheduleRule { CompanyId = company.Id, Trigger = "DocumentApproved", OffsetDays = 0, SendAtHour = 9, IsActive = true });
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id, doc.Id);
    }

    [Fact]
    public async Task eTax_คิวส่งถึงอีเมลหลักเท่านั้น_อีเมลเสริมบันทึกว่าไม่ได้ส่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedEtaxAsync(db, "queue " + Guid.NewGuid().ToString("N")[..6]);
        await Endpoints(db).AddEmailAsync(s.CompanyId, s.ContactId,
            new AddContactNotifyEmailRequest("finance@buyer250.co.th", null, new List<string> { nameof(NotifyTopic.Document) }), Actor);
        db.ChangeTracker.Clear();

        await Sched(db, new FakeSender()).OnDocumentApprovedAsync(s.CompanyId, s.InvoiceId);

        var row = Assert.Single(await QueueRowsAsync(db, s));
        Assert.Equal("Pending", row.Status);
        Assert.Equal("ap@buyer250.co.th", row.ToEmail);                        // ผ่านตัวตรวจผู้รับ e-Tax (To 1 อีเมล)
        Assert.StartsWith(ContactRecipientRule.EtaxPrimaryOnlyPrefix, row.ErrorMessage);
        Assert.Contains("finance@buyer250.co.th", row.ErrorMessage);
        Assert.NotNull(EmailScheduleService.AfterSentNote(row.ErrorMessage));  // หมายเหตุคงไว้หลังส่ง
    }

    [Fact]
    public async Task eTax_อีเมลหลักปิดรับเอกสาร_อีเมลเสริมรับ_ไม่ส่งใบกำกับพร้อมเหตุ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedEtaxAsync(db, "primary-off " + Guid.NewGuid().ToString("N")[..6]);
        var ep = Endpoints(db);
        await ep.UpdatePrimaryAsync(s.CompanyId, s.ContactId, new UpdateContactNotifyPrimaryRequest(new List<string> { nameof(NotifyTopic.Receipt) }), Actor);
        await ep.AddEmailAsync(s.CompanyId, s.ContactId,
            new AddContactNotifyEmailRequest("finance@buyer250.co.th", null, new List<string> { nameof(NotifyTopic.Document) }), Actor);
        db.ChangeTracker.Clear();

        await Sched(db, new FakeSender()).OnDocumentApprovedAsync(s.CompanyId, s.InvoiceId);

        var row = Assert.Single(await QueueRowsAsync(db, s));
        Assert.Equal(ScheduledEmailSendCheck.SkippedStatus, row.Status);
        Assert.Contains("อีเมลหลัก", row.ErrorMessage);
        Assert.Contains("finance@buyer250.co.th", row.ErrorMessage);
    }

    [Fact]
    public async Task eTax_หลังรับชำระส่งใบเสร็จถึงอีเมลหลักเท่านั้น_บันทึกอีเมลเสริมในงาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedEtaxAsync(db, "postpay " + Guid.NewGuid().ToString("N")[..6]);
        var (pay, _) = await AddPaymentWithReceiptAsync(db, s, etaxRow: true);
        await Endpoints(db).AddEmailAsync(s.CompanyId, s.ContactId,
            new AddContactNotifyEmailRequest("acct@buyer250.co.th", null, new List<string> { nameof(NotifyTopic.Receipt) }), Actor);
        db.ChangeTracker.Clear();

        var sender = new FakeSender();
        var (svc, _) = PostPay(db, sender);
        Assert.True(await svc.EnqueueAsync(s.CompanyId, pay.Id, PaymentEntrySource.Manual, null));
        var runId = (await db.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.CompanyId == s.CompanyId && r.PaymentId == pay.Id)).Id;
        await svc.ProcessRunAsync(runId, default);
        db.ChangeTracker.Clear();

        var run = await db.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        Assert.Equal(PostPaymentAutomationPolicy.StatusDone, run.Status);
        var msg = Assert.Single(sender.Sent);
        Assert.Equal(new[] { "ap@buyer250.co.th" }, msg.To);
        Assert.Contains("csemail@etax.teda.th", msg.Cc);
        Assert.Contains(ContactRecipientRule.EtaxPrimaryOnlyPrefix, run.Message);
        Assert.Contains("acct@buyer250.co.th", run.Message);
    }

    [Fact]
    public async Task ผู้ติดต่อปิดส่งอัตโนมัติ_ยังได้ใบเสร็จออก_ไม่ได้อีเมล_ทิศตรงข้ามเปิดส่งได้อีเมล()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        foreach (var off in new[] { true, false })
        {
            var s = await SeedAsync(db, "postpay-auto " + off + " " + Guid.NewGuid().ToString("N")[..6], autoSendDisabled: off, dueSoonRule: false);
            await db.CompanySettings.Where(x => x.CompanyId == s.CompanyId).ExecuteUpdateAsync(x => x.SetProperty(c => c.PostPayEmailReceipt, true));
            await db.Documents.Where(d => d.Id == s.InvoiceId).ExecuteUpdateAsync(x => x
                .SetProperty(d => d.Status, DocumentStatus.Paid).SetProperty(d => d.BalanceDue, 0m).SetProperty(d => d.PaidAmount, 1_070m)
                .SetProperty(d => d.VatAmount, 0m).SetProperty(d => d.TotalAmount, 1_000m));
            db.ChangeTracker.Clear();
            var pay = new Payment
            {
                CompanyId = s.CompanyId, PaymentNumber = "PAY250W8-" + Random.Shared.Next(100000, 999999), DocumentId = s.InvoiceId,
                PaymentDate = DateTime.UtcNow.Date, Amount = 1_000m, PaymentMethod = PaymentMethod.BankTransfer,
            };
            db.Payments.Add(pay);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var calls = new List<string>();
            var sender = new FakeSender();
            var (svc, _) = PostPay(db, sender, IssuingDocs(calls, s, pay));
            Assert.True(await svc.EnqueueAsync(s.CompanyId, pay.Id, PaymentEntrySource.Manual, null));
            var runId = (await db.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.CompanyId == s.CompanyId && r.PaymentId == pay.Id)).Id;
            await svc.ProcessRunAsync(runId, default);
            db.ChangeTracker.Clear();
            var run = await db.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.Id == runId);

            Assert.Contains(nameof(IDocumentService.IssueReceiptForPaymentAsync), calls);   // ออกใบเสร็จทั้งสองทิศ
            if (off)
            {
                Assert.Equal(PostPaymentAutomationPolicy.StatusBlocked, run.Status);
                Assert.Contains(AutopilotSettingsResolver.AutoSendDisabledReason, run.Message);
                Assert.Empty(sender.Sent);
            }
            else
            {
                Assert.Equal(PostPaymentAutomationPolicy.StatusDone, run.Status);
                Assert.Single(sender.Sent);
            }
        }
    }

    [Fact]
    public async Task ผู้ติดต่อถูกลบ_ทวงชำระเป็นแถวข้ามพร้อมเหตุ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "deleted " + Guid.NewGuid().ToString("N")[..6]);
        await db.Contacts.IgnoreQueryFilters().Where(c => c.Id == s.ContactId).ExecuteUpdateAsync(x => x.SetProperty(c => c.IsDeleted, true));
        db.ChangeTracker.Clear();

        await Sched(db, new FakeSender()).ScanDueSoonAndOverdueAsync();

        var row = Assert.Single(await QueueRowsAsync(db, s));
        Assert.Equal(ScheduledEmailSendCheck.SkippedStatus, row.Status);
        Assert.Equal(ContactRecipientRule.ContactDeletedReason, row.ErrorMessage);
    }

    [Fact]
    public async Task แถวกฎเมื่ออนุมัติที่ข้าม_ไม่นับว่าครอบแล้ว_ทิศตรงข้ามแถวรอส่งนับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        foreach (var status in new[] { ScheduledEmailSendCheck.SkippedStatus, "Pending" })
        {
            var s = await SeedAsync(db, "covered " + status + " " + Guid.NewGuid().ToString("N")[..6], dueSoonRule: false);
            db.EmailScheduleRules.Add(new EmailScheduleRule { CompanyId = s.CompanyId, Trigger = "DocumentApproved", OffsetDays = 0, SendAtHour = 9, IsActive = true });
            db.EmailQueues.Add(new EmailQueue
            {
                CompanyId = s.CompanyId, EntityType = "Document", EntityId = s.InvoiceId, ToEmail = "", Subject = "x", Body = "",
                ScheduledFor = DateTime.UtcNow, Status = status, IdempotencyKey = EmailScheduleService.DocumentQueueKey(s.InvoiceId, "DocumentApproved", "approved"),
            });
            var rec = new RecurringTransaction
            {
                CompanyId = s.CompanyId, Name = "รายการประจำ MED-6", ContactId = s.ContactId, AutoSendEmail = true,
                Frequency = RecurringFrequency.Monthly, DocumentType = DocumentType.Invoice,
                TemplateType = "Document", TemplateData = "{}", StartDate = DateTime.UtcNow.Date, NextRunDate = DateTime.UtcNow.Date.AddMonths(1),
            };
            db.RecurringTransactions.Add(rec);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var outcome = await Sched(db, new FakeSender()).OnRecurringDocumentCreatedAsync(s.CompanyId, s.InvoiceId, rec.Id);
            if (status == ScheduledEmailSendCheck.SkippedStatus)
                Assert.Equal(EmailEnqueueKind.Enqueued, outcome.Kind);   // แถวข้ามไม่ใช่การส่ง ⇒ รายการประจำส่งเอง
            else
                Assert.Equal(EmailEnqueueOutcome.CoveredByApprovedRule, outcome.Reason);
        }
    }

    private static async Task<(Payment Pay, Document Receipt)> AddPaymentWithReceiptAsync(AccountingDbContext db, Seed s, bool etaxRow)
    {
        await db.Documents.Where(d => d.Id == s.InvoiceId).ExecuteUpdateAsync(x => x
            .SetProperty(d => d.Status, DocumentStatus.Paid).SetProperty(d => d.BalanceDue, 0m).SetProperty(d => d.PaidAmount, 1_070m));
        // ใบเสร็จที่เป็นใบกำกับภาษี (ไปทาง e-Tax) ต้องรับชำระ "ใบแจ้งหนี้" — ใบเสร็จของใบกำกับภาษีไม่ใช่ใบกำกับซ้ำ
        // (EtaxAutoIssueScope.Judge ⇒ ReceiptSettlesTaxInvoice ⇒ อีเมลธรรมดาถึงทุกผู้รับ = ถูกตามกฎ · CI 42232967230)
        if (etaxRow)
            await db.Documents.Where(d => d.Id == s.InvoiceId).ExecuteUpdateAsync(x => x
                .SetProperty(d => d.DocumentType, DocumentType.Invoice).SetProperty(d => d.IsTaxInvoiceByLaw, (bool?)false));
        db.ChangeTracker.Clear();
        var pay = new Payment
        {
            CompanyId = s.CompanyId, PaymentNumber = "PAY250W8-" + Random.Shared.Next(100000, 999999), DocumentId = s.InvoiceId,
            PaymentDate = DateTime.UtcNow.Date, Amount = 1_070m, PaymentMethod = PaymentMethod.BankTransfer,
        };
        db.Payments.Add(pay);
        await db.SaveChangesAsync();
        var rec = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Receipt,
            DocumentNumber = "RE250W8-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Paid, DocumentDate = pay.PaymentDate,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, PaidAmount = 1_070m, Currency = "THB",
            RelatedDocumentId = s.InvoiceId, IsSettlementReceipt = true, SettlementPaymentId = pay.Id, IsTaxInvoiceByLaw = true,
        };
        db.Documents.Add(rec);
        await db.SaveChangesAsync();
        pay.ReceiptDocumentId = rec.Id;
        if (etaxRow)
            db.EtaxInvoices.Add(new EtaxInvoice
            {
                CompanyId = s.CompanyId, DocumentId = rec.Id, EtaxRefNumber = rec.DocumentNumber, XmlContent = "<x/>", Status = EtaxStatus.Generated,
                SellerName = "ผู้ขาย", SellerTaxId = "0105556000007", BuyerName = "ผู้ซื้อ",
            });
        await db.SaveChangesAsync();
        return (pay, rec);
    }

    /// <summary>IDocumentService ปลอม — ออกใบเสร็จ = เขียนใบจริงลงฐาน (เส้นเดิมมีเทสต์ของตัวเองแล้ว)</summary>
    private static IDocumentService IssuingDocs(List<string> calls, Seed s, Payment pay)
        => F4Proxy<IDocumentService>.Create((m, _) =>
        {
            calls.Add(m);
            return m == nameof(IDocumentService.IssueReceiptForPaymentAsync) ? IssueAsync(s, pay) : null;
        });

    private static async Task<Accounting.Models.DTOs.Document.IssuedReceiptResult> IssueAsync(Seed s, Payment pay)
    {
        using var other = DbTestDatabase.TryCreateContext()!;
        var rec = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Receipt,
            DocumentNumber = "RE250W8-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Paid, DocumentDate = pay.PaymentDate,
            SubTotal = 1_000m, TotalAmount = 1_000m, PaidAmount = 1_000m, Currency = "THB",
            RelatedDocumentId = s.InvoiceId, IsSettlementReceipt = true, SettlementPaymentId = pay.Id, IsTaxInvoiceByLaw = false,
        };
        other.Documents.Add(rec);
        await other.SaveChangesAsync();
        await other.Payments.Where(p => p.Id == pay.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.ReceiptDocumentId, (Guid?)rec.Id));
        return new Accounting.Models.DTOs.Document.IssuedReceiptResult(rec.Id, rec.DocumentNumber, rec.DocumentDate, IsDraft: false, AlreadyExisted: false);
    }

    private static (PostPaymentAutomationService Svc, DocumentEmailService Email) PostPay(AccountingDbContext db, FakeSender sender, IDocumentService? docs = null)
    {
        var etax = F4Proxy<IEtaxInvoiceService>.Create((m, _) => m == nameof(IEtaxInvoiceService.GeneratePdfA3Async) ? (new byte[20_000], "etax.pdf") : null);
        var email = new DocumentEmailService(db, Factory(sender), etax, NullLogger<DocumentEmailService>.Instance, pdf: null, storage: null, config: Config());
        return (new PostPaymentAutomationService(db, docs ?? F4Proxy<IDocumentService>.Create((_, _) => null), email, Factory(sender),
            NullLogger<PostPaymentAutomationService>.Instance, recipients: Resolver(db)), email);
    }

    /// <summary>รอบ 250 WP-04 ฝ่ายค้าน M3: กฎ "เกินกำหนด" ที่ไม่ระบุชนิดเอกสาร ทวงเฉพาะเอกสารฝั่งลูกหนี้ — ใบแจ้งหนี้ซื้อที่เราค้างจ่ายผู้ขายไม่ถูกส่งอีเมลทวง ·
    /// ทิศตรงข้าม: ใบแจ้งหนี้ขายที่เกินกำหนดวันเดียวกันถูกเข้าคิว</summary>
    [Fact]
    public async Task Overdue_กฎไม่ระบุชนิด_ทวงเฉพาะลูกหนี้_ไม่ทวงผู้ขาย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "m3 " + Guid.NewGuid().ToString("N")[..6], dueSoonRule: false);
        await db.Documents.Where(d => d.Id == s.InvoiceId).ExecuteUpdateAsync(x => x.SetProperty(d => d.DueDate, (DateTime?)TodayBkk().AddDays(-5)));
        var pi = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.PurchaseInvoice,
            DocumentNumber = "PI250M3-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            DocumentDate = TodayBkk().AddDays(-35), DueDate = TodayBkk().AddDays(-5),
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        db.Documents.Add(pi);
        db.EmailScheduleRules.Add(new EmailScheduleRule
        {
            CompanyId = s.CompanyId, Trigger = "DocumentOverdue", OffsetDays = 5, SendAtHour = 9, IsActive = true, DocumentType = null,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Sched(db, new FakeSender()).ScanDueSoonAndOverdueAsync();

        Assert.Single(await QueueRowsAsync(db, s));                                                     // ลูกหนี้ ⇒ ทวง
        Assert.False(await db.EmailQueues.AsNoTracking().AnyAsync(q => q.CompanyId == s.CompanyId && q.EntityId == pi.Id));   // ผู้ขาย ⇒ ไม่ทวง
    }
}
