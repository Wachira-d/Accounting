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
}
