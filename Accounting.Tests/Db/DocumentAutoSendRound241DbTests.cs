using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Integration;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 241 — เส้นจริงบน PostgreSQL: งานหลังรับชำระ 1 แถวต่อการชำระ (เข้าคิวซ้ำ/webhook ซ้ำ = ไม่เพิ่ม) · มีใบเสร็จแล้ว ⇒ ส่ง 1 ฉบับ + บันทึกบนเอกสาร + ประมวลผลซ้ำไม่ส่งซ้ำ ·
/// ไม่มีใบเสร็จ ⇒ ออกด้วยเส้นเดิม (IssueReceiptForPaymentAsync) แล้วส่ง · ผู้ซื้อขาดข้อมูลใบกำกับ ⇒ ไม่ออก ไม่ส่ง + แจ้งเตือน · ใบต้นทางยกเลิก ⇒ ข้าม ·
/// ลิงก์ชำระ: หนึ่งลิงก์ต่อใบ · ช่องทางเฉพาะที่อนุญาต · ยอดสุทธิหลังหัก ณ ที่จ่าย · เพิกถอน/ยกเลิกใบ ⇒ จ่ายไม่ได้ · ตารางส่งอีเมลแนบลิงก์ช่องทางที่เลือก
/// </summary>
[Trait("Category", "Db")]
public class DocumentAutoSendRound241DbTests
{
    private readonly ITestOutputHelper _out;
    public DocumentAutoSendRound241DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private sealed class FakeSender : IEmailSender
    {
        public bool Succeed { get; set; } = true;
        public List<EmailMessage> Sent { get; } = new();
        public EmailProvider Provider => EmailProvider.Smtp;
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(Succeed ? EmailSendResult.Ok("mid-241") : EmailSendResult.Fail("SMTP 451 ลองใหม่ภายหลัง"));
        }
    }

    private sealed class FakeNotify : INotificationEngine
    {
        public List<(string Key, NotificationContext Ctx)> Calls { get; } = new();
        public Task DispatchAsync(Guid companyId, string eventKey, NotificationContext context)
        {
            Calls.Add((eventKey, context));
            return Task.CompletedTask;
        }
    }

    private static IEmailSenderFactory Factory(FakeSender s) => F4Proxy<IEmailSenderFactory>.Create((m, _) => m switch
    {
        nameof(IEmailSenderFactory.PlatformFromAddress) => "no-reply@nextacc.net",
        _ => s,
    });

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();

    private sealed record Seed(Guid CompanyId, Guid ContactId, Document Invoice, Payment Payment);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, string tag, bool buyerComplete = true,
        bool emailReceipt = true, DocumentStatus invoiceStatus = DocumentStatus.Paid, decimal wht = 0m, decimal balance = 0m)
    {
        var company = new Company { Name = "บริษัท 241 " + tag, TaxId = "0105556000007", Email = "office@seller241.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id, PostPayEmailReceipt = emailReceipt });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 241 จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = "0107537000017", Email = "ap@buyer241.co.th", Address = buyerComplete ? "1 ถนนสุขุมวิท กรุงเทพฯ" : null,
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var inv = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Invoice,
            DocumentNumber = "INV241-" + Random.Shared.Next(100000, 999999), Status = invoiceStatus,
            // รอบ 244: TotalAmount = SubTotal + VAT − WHT (WhtGrossUp.PayeeTotal — กติกาของตัวสร้างเอกสารจริง) · เดิมตั้ง 1,070 แม้มี WHT (ใบที่ระบบสร้างไม่ได้)
            DocumentDate = DateTime.UtcNow.Date.AddDays(-3), SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m - wht,
            WithholdingTaxAmount = wht, PaidAmount = 1_070m - wht - balance, BalanceDue = balance, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        var pay = new Payment
        {
            CompanyId = company.Id, PaymentNumber = "PAY241-" + Random.Shared.Next(100000, 999999), DocumentId = inv.Id,
            PaymentDate = DateTime.UtcNow.Date, Amount = 1_070m, PaymentMethod = PaymentMethod.BankTransfer,
        };
        db.Payments.Add(pay);
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id, inv, pay);
    }

    private static async Task<Document> AddReceiptAsync(Accounting.Data.AccountingDbContext db, Seed s)
    {
        var rec = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Receipt,
            DocumentNumber = "REC241-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Paid,
            DocumentDate = s.Payment.PaymentDate, SubTotal = 1_070m, TotalAmount = 1_070m, PaidAmount = 1_070m, Currency = "THB",
            RelatedDocumentId = s.Invoice.Id, IsSettlementReceipt = true, SettlementPaymentId = s.Payment.Id, IsTaxInvoiceByLaw = false,
        };
        db.Documents.Add(rec);
        await db.SaveChangesAsync();
        return rec;
    }

    /// <summary>IDocumentService ปลอม — บันทึกการเรียก · ออกใบเสร็จ = เขียนใบจริงลงฐาน (แทนเส้นเดิมที่มีเทสต์ของตัวเองแล้ว)</summary>
    private static IDocumentService Docs(List<string> calls, Seed s)
        => F4Proxy<IDocumentService>.Create((m, args) =>
        {
            calls.Add(m);
            if (m == nameof(IDocumentService.IssueReceiptForPaymentAsync))
                return IssueAsync(s);
            return null;   // GetDocumentAsync ⇒ null (ใบไม่ใช่ใบเสร็จในตัว)
        });

    private static async Task<IssuedReceiptResult> IssueAsync(Seed s)
    {
        using var other = DbTestDatabase.TryCreateContext()!;
        var rec = await AddReceiptAsync(other, s);
        await other.Payments.Where(p => p.Id == s.Payment.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.ReceiptDocumentId, (Guid?)rec.Id));
        return new IssuedReceiptResult(rec.Id, rec.DocumentNumber, rec.DocumentDate, IsDraft: false, AlreadyExisted: false);
    }

    private static (PostPaymentAutomationService Svc, FakeSender Sender, FakeNotify Notify, List<string> Calls) Automation(
        Accounting.Data.AccountingDbContext db, Seed s)
    {
        var sender = new FakeSender();
        var notify = new FakeNotify();
        var calls = new List<string>();
        var email = new DocumentEmailService(db, Factory(sender), F4Proxy<IEtaxInvoiceService>.Create((_, _) => null),
            NullLogger<DocumentEmailService>.Instance, pdf: null, storage: null, config: Config());
        return (new PostPaymentAutomationService(db, Docs(calls, s), email, Factory(sender), NullLogger<PostPaymentAutomationService>.Instance, notify),
            sender, notify, calls);
    }

    private static async Task<PostPaymentAutomationRun> RunOfAsync(Accounting.Data.AccountingDbContext db, Seed s)
        => await db.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.CompanyId == s.CompanyId && r.PaymentId == s.Payment.Id);

    [Fact]
    public async Task มีใบเสร็จแล้ว_เข้าคิวซ้ำได้แถวเดียว_ส่งหนึ่งฉบับ_บันทึกบนเอกสาร_ประมวลผลซ้ำไม่ส่งซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "has-receipt " + Guid.NewGuid().ToString("N")[..6]);
        var rec = await AddReceiptAsync(db, s);
        var (svc, sender, notify, calls) = Automation(db, s);

        Assert.True(await svc.EnqueueAsync(s.CompanyId, s.Payment.Id, PaymentEntrySource.Gateway, "payment-gateway"));
        Assert.False(await svc.EnqueueAsync(s.CompanyId, s.Payment.Id, PaymentEntrySource.Gateway, "payment-gateway"));   // webhook ซ้ำ
        Assert.False(await svc.EnqueueAsync(s.CompanyId, s.Payment.Id, PaymentEntrySource.Manual, null));
        var run = await RunOfAsync(db, s);

        await svc.ProcessRunAsync(run.Id, default);
        db.ChangeTracker.Clear();
        run = await RunOfAsync(db, s);
        Assert.Equal(PostPaymentAutomationPolicy.StatusDone, run.Status);
        Assert.Equal(rec.Id, run.ReceiptDocumentId);
        Assert.DoesNotContain(nameof(IDocumentService.IssueReceiptForPaymentAsync), calls);   // มีใบแล้ว ไม่ออกซ้ำ
        var msg = Assert.Single(sender.Sent);
        Assert.Contains("ap@buyer241.co.th", msg.To);
        var log = Assert.Single(await db.DocumentEmailLogs.AsNoTracking().Where(l => l.CompanyId == s.CompanyId).ToListAsync());
        Assert.Equal(rec.Id, log.DocumentId);
        Assert.Equal(EmailLogStatus.Sent, log.Status);
        Assert.Empty(notify.Calls);

        // ประมวลผลซ้ำ (เครื่องสองเครื่อง/ลองใหม่) — แถวไม่ใช่ Pending แล้ว ⇒ ไม่ส่งซ้ำ
        await svc.ProcessRunAsync(run.Id, default);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task ไม่มีใบเสร็จ_ออกด้วยเส้นเดิมแล้วส่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "issue " + Guid.NewGuid().ToString("N")[..6]);
        var (svc, sender, _, calls) = Automation(db, s);
        await svc.EnqueueAsync(s.CompanyId, s.Payment.Id, PaymentEntrySource.Manual, Guid.NewGuid().ToString());
        await svc.ProcessRunAsync((await RunOfAsync(db, s)).Id, default);
        db.ChangeTracker.Clear();
        var run = await RunOfAsync(db, s);
        Assert.Equal(PostPaymentAutomationPolicy.StatusDone, run.Status);
        Assert.Single(calls, c => c == nameof(IDocumentService.IssueReceiptForPaymentAsync));
        Assert.Contains("IssueReceipt", run.StepsDone);
        Assert.Contains("EmailReceipt", run.StepsDone);
        Assert.Single(sender.Sent);
        var receiptId = Assert.IsType<Guid>(run.ReceiptDocumentId);
        Assert.True(await db.Documents.AnyAsync(d => d.Id == receiptId && d.SettlementPaymentId == s.Payment.Id));
    }

    [Fact]
    public async Task ผู้ซื้อขาดข้อมูลใบกำกับ_ไม่ออก_ไม่ส่ง_และแจ้งเตือน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "buyer " + Guid.NewGuid().ToString("N")[..6], buyerComplete: false);
        var (svc, sender, notify, calls) = Automation(db, s);
        await svc.EnqueueAsync(s.CompanyId, s.Payment.Id, PaymentEntrySource.Gateway, null);
        await svc.ProcessRunAsync((await RunOfAsync(db, s)).Id, default);
        db.ChangeTracker.Clear();
        var run = await RunOfAsync(db, s);
        Assert.Equal(PostPaymentAutomationPolicy.StatusBlocked, run.Status);
        Assert.Contains("ที่อยู่ผู้ซื้อ", run.Message);
        Assert.DoesNotContain(nameof(IDocumentService.IssueReceiptForPaymentAsync), calls);
        Assert.Empty(sender.Sent);
        Assert.False(await db.Documents.AnyAsync(d => d.CompanyId == s.CompanyId && d.DocumentType == DocumentType.Receipt));
        var n = Assert.Single(notify.Calls);
        Assert.Equal(Accounting.Models.Constants.NotificationEvents.PostPaymentAutomationBlocked, n.Key);
        // ทางไปต่อ: ลองใหม่ได้ (หลังแก้ข้อมูล) — แถวกลับเป็น Pending
        await svc.RetryAsync(s.CompanyId, run.Id);
        Assert.Equal(PostPaymentAutomationPolicy.StatusPending, (await RunOfAsync(db, s)).Status);
    }

    [Fact]
    public async Task ใบต้นทางยกเลิก_ข้าม_และปิดค่าตั้ง_ไม่สร้างงาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "void " + Guid.NewGuid().ToString("N")[..6], invoiceStatus: DocumentStatus.Voided);
        var (svc, sender, _, _) = Automation(db, s);
        await svc.EnqueueAsync(s.CompanyId, s.Payment.Id, PaymentEntrySource.Gateway, null);
        await svc.ProcessRunAsync((await RunOfAsync(db, s)).Id, default);
        db.ChangeTracker.Clear();
        Assert.Equal(PostPaymentAutomationPolicy.StatusSkipped, (await RunOfAsync(db, s)).Status);
        Assert.Empty(sender.Sent);

        var off = await SeedAsync(db, "off " + Guid.NewGuid().ToString("N")[..6], emailReceipt: false);
        var (svcOff, _, _, _) = Automation(db, off);
        Assert.False(await svcOff.EnqueueAsync(off.CompanyId, off.Payment.Id, PaymentEntrySource.Gateway, null));
        Assert.False(await db.PostPaymentAutomationRuns.AnyAsync(r => r.CompanyId == off.CompanyId));
    }

    [Fact]
    public async Task จุดต่อ_WHT_ไม่สร้างแถวรอ_50ทวิ_เอง_มีแถวจากเส้นเดิม_1_แถวคงเดิม_ไม่มี_บอกในงาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        // (ก) เส้นบันทึกรับชำระ (SyncWhtCreditReceivedAsync) สร้างแถว Pending ไว้แล้ว 1 แถว ⇒ งานหลังรับชำระไม่เพิ่ม
        var s = await SeedAsync(db, "wht " + Guid.NewGuid().ToString("N")[..6], wht: 30m);
        await AddReceiptAsync(db, s);
        await db.Payments.Where(p => p.Id == s.Payment.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.WithholdingTaxAmount, 30m));
        db.WhtCreditsReceived.Add(new WhtCreditReceived
        {
            CompanyId = s.CompanyId, TaxYear = DateTime.UtcNow.Year, PayerContactId = s.ContactId, PayerName = "บริษัท ลูกค้า 241 จำกัด",
            IncomeAmount = 1_000m, WhtRate = 3m, WhtAmount = 30m, Status = WhtCreditStatus.Pending, DocumentId = s.Invoice.Id, PaymentId = s.Payment.Id,
        });
        await db.SaveChangesAsync();
        var (svc, _, _, _) = Automation(db, s);
        await svc.EnqueueAsync(s.CompanyId, s.Payment.Id, PaymentEntrySource.Gateway, null);
        Assert.False(await svc.EnqueueAsync(s.CompanyId, s.Payment.Id, PaymentEntrySource.Gateway, null));   // webhook ซ้ำ
        await svc.ProcessRunAsync((await RunOfAsync(db, s)).Id, default);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.WhtCreditsReceived.CountAsync(w => w.CompanyId == s.CompanyId && w.DocumentId == s.Invoice.Id));
        Assert.DoesNotContain(PostPaymentAutomationPolicy.WhtCreditMissingNote, (await RunOfAsync(db, s)).Message ?? "");

        // (ข) ไม่มีแถว (ทะเบียนเดิมไม่ได้สร้าง) ⇒ งานไม่สร้างเอง (0 แถว) แต่บอกในงาน — ทางไปต่อคือหน้าทะเบียนภาษีถูกหัก
        var s2 = await SeedAsync(db, "wht-miss " + Guid.NewGuid().ToString("N")[..6], wht: 30m);
        await AddReceiptAsync(db, s2);
        await db.Payments.Where(p => p.Id == s2.Payment.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.WithholdingTaxAmount, 30m));
        var (svc2, sender2, _, _) = Automation(db, s2);
        await svc2.EnqueueAsync(s2.CompanyId, s2.Payment.Id, PaymentEntrySource.Gateway, null);
        await svc2.ProcessRunAsync((await RunOfAsync(db, s2)).Id, default);
        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.WhtCreditsReceived.CountAsync(w => w.CompanyId == s2.CompanyId));
        var run2 = await RunOfAsync(db, s2);
        Assert.Contains(PostPaymentAutomationPolicy.WhtCreditMissingNote, run2.Message);
        Assert.Equal(PostPaymentAutomationPolicy.StatusDone, run2.Status);   // ใบเสร็จยังส่ง
        Assert.Single(sender2.Sent);
    }

    // ───────── ลิงก์ชำระเงิน ─────────

    private static IPaymentProvider Provider(string code) => F4Proxy<IPaymentProvider>.Create((m, _) => m switch
    {
        "get_ProviderCode" => code,
        "get_Capabilities" => new PaymentCapabilities(new HashSet<PaymentMethodKind> { PaymentMethodKind.PromptPay, PaymentMethodKind.ManualSlip }, false, false, true),
        _ => null,
    });

    private static async Task<(DocumentPayLinkService Links, Seed S)> LinkSeedAsync(Accounting.Data.AccountingDbContext db, string tag)
    {
        var s = await SeedAsync(db, tag, invoiceStatus: DocumentStatus.Approved, wht: 30m, balance: 1_040m);
        const string code = "gw241";
        db.PaymentProviderConfigs.Add(new PaymentProviderConfig { CompanyId = s.CompanyId, ProviderCode = code, IsActive = true, Mode = PaymentProviderMode.Test });
        await db.SaveChangesAsync();
        return (new DocumentPayLinkService(db, new[] { Provider(code) }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance), s);
    }

    private static readonly SecretProtector Secrets = new("doc-autosend-241-db");

    private static string TokenOf(string url) => System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["t"]!;

    [Fact]
    public async Task ลิงก์หนึ่งต่อใบ_ช่องทางเฉพาะที่อนุญาต_ยอดสุทธิหลังหักภาษี_เพิกถอนแล้วจ่ายไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (links, s) = await LinkSeedAsync(db, "link " + Guid.NewGuid().ToString("N")[..6]);

        // ช่องทางที่บริษัทไม่ได้เปิด (บัตร) ⇒ ไม่ออกลิงก์ + เหตุผล
        var refused = await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, new[] { "Card" });
        Assert.False(refused.Ok);

        var a = await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, new[] { "PromptPay" });
        Assert.True(a.Ok, a.Refusal);
        Assert.Contains("/pages/pay-document.html?c=" + s.CompanyId.ToString("D"), a.Url);
        Assert.Equal((1_070m, 30m, 1_040m), (a.Amounts!.Gross, a.Amounts.Wht, a.Amounts.Net));
        var b = await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, null);
        Assert.Equal(a.Url, b.Url);   // หนึ่งลิงก์ต่อใบ

        var token = TokenOf(a.Url!);
        // ฐานไม่มี token ตรง — มีแต่ hash + ฉบับเข้ารหัส (คำตัดสิน 241-10)
        var stored = await db.Documents.AsNoTracking().Where(d => d.Id == s.Invoice.Id)
            .Select(d => new { d.PayLinkTokenHash, d.PayLinkTokenProtected }).SingleAsync();
        Assert.Equal(DocumentPayLinkPolicy.TokenHash(token), stored.PayLinkTokenHash);
        Assert.DoesNotContain(token, stored.PayLinkTokenProtected);
        Assert.Null(await links.PublicViewAsync(s.CompanyId, stored.PayLinkTokenHash!));   // ถือ hash ที่รั่ว ≠ ถือลิงก์
        var view = (await links.PublicViewAsync(s.CompanyId, token))!;
        Assert.True(view.CanPayOnline, view.BlockedNote);
        Assert.Equal(1_040m, view.Target.AmountDue);
        Assert.Equal(new[] { "PromptPay" }, view.Target.AllowedMethods);
        Assert.Equal(PaymentSourceKind.Document, view.Target.SourceKind);
        Assert.Null(await links.PublicViewAsync(Guid.NewGuid(), token!));   // ข้ามบริษัทไม่ได้

        await links.RevokeAsync(s.CompanyId, s.Invoice.Id, "tester");
        var revoked = (await links.PublicViewAsync(s.CompanyId, token!))!;
        Assert.False(revoked.Target.CanPayNow);
        Assert.Contains("ยกเลิก", revoked.BlockedNote);
        // รอบ 259 (259-2-8): ผู้เรียกที่ไม่ได้ตั้งใจออกใหม่ (หน้าต่างส่งเอง/พอร์ทัล) ⇒ ไม่ออกทับ
        await Assert.ThrowsAsync<PayLinkRevokedException>(() => links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, null));
        // อีเมลรอบใหม่หลังเพิกถอน (ตารางส่ง = ผู้เรียกที่ตั้งใจ) ⇒ ลิงก์ใหม่ (token เดิมยังใช้ไม่ได้)
        var c = await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, null, default, allowReissueRevoked: true, actor: "tester");
        Assert.NotEqual(a.Url, c.Url);
    }

    [Fact]
    public async Task ใบยกเลิก_ลิงก์เดิมจ่ายไม่ได้ทันที()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (links, s) = await LinkSeedAsync(db, "voidlink " + Guid.NewGuid().ToString("N")[..6]);
        var link = await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, null);
        Assert.True(link.Ok, link.Refusal);
        var token = TokenOf(link.Url!);
        await db.Documents.Where(d => d.Id == s.Invoice.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Voided));
        var view = (await links.PublicViewAsync(s.CompanyId, token!))!;
        Assert.False(view.Target.CanPayNow);
        Assert.Contains("ยกเลิก", view.BlockedNote);
        Assert.Empty(view.OnlineMethods);
    }

    [Fact]
    public async Task ตารางส่งอีเมลแนบลิงก์_ช่องทางที่ตารางเลือก_และจำตารางที่ส่งใบนี้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (links, s) = await LinkSeedAsync(db, "sched " + Guid.NewGuid().ToString("N")[..6]);
        var rule = new EmailScheduleRule
        {
            CompanyId = s.CompanyId, Trigger = "DocumentApproved", IsActive = true, AttachPayLink = true,
            PayLinkChannelsJson = DocumentPayLinkPolicy.SerializeChannels(new[] { "PromptPay" }), PostPayEmailReceipt = false,
        };
        db.EmailScheduleRules.Add(rule);
        await db.SaveChangesAsync();
        var sched = new EmailScheduleService(db, Factory(new FakeSender()), F4Proxy<IPdfGenerationService>.Create((_, _) => null),
            NullLogger<EmailScheduleService>.Instance, links);
        await sched.OnDocumentApprovedAsync(s.CompanyId, s.Invoice.Id);

        var q = Assert.Single(await db.EmailQueues.AsNoTracking().Where(x => x.CompanyId == s.CompanyId).ToListAsync());
        // รอบ 244 (แก้ CI): คิวเก็บที่หมาย {PayLink} ไม่ใช่ URL ตั้งแต่ฝ่ายค้าน S1 ของรอบ 241 (ไม่มี token ตรงในฐาน) — บรรทัดเดิม Contains("pay-document.html", q.Body)
        // ค้างจากก่อน S1 · URL จริงตรวจที่ข้อความที่ประกอบตอนส่งด้านล่าง
        Assert.Contains("ชำระเงินออนไลน์", q.Body);
        Assert.Contains("1,040.00", q.Body);
        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == s.Invoice.Id);
        Assert.Equal(new[] { "PromptPay" }, DocumentPayLinkPolicy.ParseChannels(doc.PayLinkChannelsJson));
        Assert.Equal(rule.Id, doc.AutoSendRuleId);
        // ตารางนี้ทับค่าบริษัท: ไม่ส่งใบเสร็จหลังชำระ ⇒ ไม่สร้างงาน
        var (svc, _, _, _) = Automation(db, s);
        Assert.False(await svc.EnqueueAsync(s.CompanyId, s.Payment.Id, PaymentEntrySource.Gateway, null));
        // ฝ่ายค้าน S1: คิวไม่มี token ตรง (เก็บที่หมาย {PayLink}) — URL ประกอบตอนส่ง
        Assert.DoesNotContain("t=", q.Body);
        Assert.Contains(DocumentPayLinkPolicy.QueuePlaceholder, q.Body);
        var tracked = await db.EmailQueues.SingleAsync(x => x.Id == q.Id);
        var composed = await sched.ComposeQueueMessageAsync(tracked, default);
        Assert.Null(composed.Refusal);
        Assert.Contains("pay-document.html?c=", composed.Message!.HtmlBody);
        Assert.DoesNotContain(DocumentPayLinkPolicy.QueuePlaceholder, composed.Message.HtmlBody);
        Assert.DoesNotContain("t=", composed.Log?.Body ?? "");   // บันทึกการส่งก็ไม่มี token
        // ใบชำระครบหลังเข้าคิว ⇒ ตอนส่งไม่มีลิงก์/ปุ่ม (ไม่ออกลิงก์ใหม่)
        await db.Documents.Where(d => d.Id == s.Invoice.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.BalanceDue, 0m).SetProperty(d => d.Status, DocumentStatus.Paid));
        var paid = await sched.ComposeQueueMessageAsync(tracked, default);
        Assert.DoesNotContain("pay-document.html", paid.Message!.HtmlBody);
        Assert.DoesNotContain(DocumentPayLinkPolicy.QueuePlaceholder, paid.Message.HtmlBody);
    }

    // ───────── ฝ่ายค้านรอบ 241 ─────────

    private static DocumentService DocSvc(Accounting.Data.AccountingDbContext db)
        => new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static async Task<(Guid CompanyId, Document Invoice)> SaleSeedAsync(Accounting.Data.AccountingDbContext db, string tag,
        decimal priorReceiptWht = 0m)
    {
        var company = new Company { Name = "บริษัท 241 handler " + tag, TaxId = "0105556000007", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id, PostPayEmailReceipt = true });
        foreach (var (code, name, type) in new[]
                 {
                     ("11111", "เงินสด", AccountType.Asset), ("11310", "ลูกหนี้การค้า", AccountType.Asset),
                     ("11910", "ภาษีเงินได้ถูกหัก ณ ที่จ่าย", AccountType.Asset),
                     ("21911", "ภาษีขาย", AccountType.Liability), ("21913", "ภาษีขายยังไม่ถึงกำหนด", AccountType.Liability),
                 })
            db.ChartOfAccounts.Add(new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = type, Level = 4, IsActive = true });
        var buyer = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ผู้ซื้อ 241 จำกัด", TaxId = "0107537000017", ContactType = ContactType.JuristicPerson,
            IsCustomer = true, Address = "1 ถนนสีลม กรุงเทพฯ", Email = "ap@buyer241h.co.th",
        };
        db.Contacts.Add(buyer);
        await db.SaveChangesAsync();
        // รอบ 244 (แก้ CI ข้อ 1–2): ใบที่ระบบสร้างจริงมี TotalAmount = 1,000 + 70 − 30 = 1,040 (WhtGrossUp.PayeeTotal) และ PaidAmount นับเฉพาะเงินเข้า ·
        // เดิมตั้ง TotalAmount 1,070 + จ่าย 1,040 ⇒ ยอดค้าง 30 (ใบที่ระบบสร้างไม่ได้ — ซ่อนบั๊กลิงก์ลบ WHT ซ้ำ) · งวดก่อน: เงินเข้า 520 + ลูกค้าหัก 15
        var paidBefore = priorReceiptWht > 0 ? 520m : 0m;
        var inv = new Document
        {
            CompanyId = company.Id, ContactId = buyer.Id, DocumentType = DocumentType.Invoice, DocumentNumber = "INVH-" + tag,
            Status = paidBefore > 0 ? DocumentStatus.PartiallyPaid : DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date.AddDays(-2),
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_040m, WithholdingTaxAmount = 30m,
            PaidAmount = paidBefore, BalanceDue = 1_040m - paidBefore, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m,
            VatRate = 7m, VatAmount = 70m, WithholdingTaxRate = 3m, WithholdingTaxAmount = 30m, IncomeTypeCode = "8" });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        if (priorReceiptWht > 0)
        {
            // งวดก่อนแปลงเป็นใบเสร็จที่ลูกค้าหักไว้แล้ว (ไม่ใช่ Payments) — ตัวนับเดียวต้องเห็น (ฝ่ายค้าน B1)
            db.Documents.Add(new Document
            {
                CompanyId = company.Id, ContactId = buyer.Id, DocumentType = DocumentType.Receipt, DocumentNumber = "RECH-" + tag,
                Status = DocumentStatus.Paid, DocumentDate = DateTime.UtcNow.Date.AddDays(-1), RelatedDocumentId = inv.Id,
                SubTotal = 500m, VatAmount = 35m, TotalAmount = 520m, WithholdingTaxAmount = priorReceiptWht, PaidAmount = 520m, Currency = "THB",
            });
            await db.SaveChangesAsync();
        }
        return (company.Id, inv);
    }

    private static PaymentIntent NetIntent(Guid companyId, Guid docId, decimal amount) => new()
    {
        CompanyId = companyId, ProviderCode = "gw241", SourceKind = PaymentSourceKind.Document, SourceId = docId, Amount = amount,
        Currency = "THB", MethodKind = PaymentMethodKind.PromptPay, Status = PaymentIntentStatus.Succeeded, IsTestMode = false,
        ConfirmedAt = DateTime.UtcNow, IdempotencyKey = "idem-" + Guid.NewGuid().ToString("N"),
    };

    // รอบ 250 (WP-02 §2.1): handler ไม่รับ IPostPaymentAutomationService แล้ว — งานหลังรับชำระเข้าคิวในแกน CreatePaymentAsync (source: Gateway)
    private static Accounting.Services.Payments.Handlers.DocumentPaymentHandler Handler(Accounting.Data.AccountingDbContext db)
        => new(db, DocSvc(db), F4Proxy<IGatewayAccountResolver>.Create((_, _) => null),
            NullLogger<Accounting.Services.Payments.Handlers.DocumentPaymentHandler>.Instance);

    [Fact]
    public async Task handler_เส้นเต็ม_ยอดสุทธิ_บันทึก_WHT_ถูก_ปิดหนี้_JE_สมดุล_ทะเบียนรอ50ทวิ_1_แถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, inv) = await SaleSeedAsync(db, Guid.NewGuid().ToString("N")[..6]);
        var intent = NetIntent(cid, inv.Id, 1_040m);
        await Handler(db).HandleSucceededAsync(intent);

        using var check = DbTestDatabase.TryCreateContext()!;
        var pay = await check.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == cid && p.DocumentId == inv.Id);
        Assert.Equal(1_040m, pay.Amount);
        Assert.Equal(30m, pay.WithholdingTaxAmount);
        Assert.Equal(0m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv.Id)).BalanceDue);
        var lines = await (from l in check.JournalEntryLines.AsNoTracking()
                           join j in check.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                           where j.CompanyId == cid && j.SourceDocumentId == inv.Id && !j.IsDeleted
                           select l).ToListAsync();
        Assert.NotEmpty(lines);
        Assert.Equal(lines.Sum(l => l.DebitAmount), lines.Sum(l => l.CreditAmount));
        Assert.Equal(1, await check.WhtCreditsReceived.CountAsync(w => w.CompanyId == cid && w.DocumentId == inv.Id));
        // ฝ่ายค้าน WP-02 ข้อ 9: ทางเข้ารับชำระ (CreatePaymentAsync โหลดใบด้วย FOR UPDATE ไม่มี Contact) — ชื่อ/เลขผู้หักภาษีเราต้องไม่ว่าง
        var credit = await check.WhtCreditsReceived.AsNoTracking().SingleAsync(w => w.CompanyId == cid && w.DocumentId == inv.Id);
        Assert.Equal("บริษัท ผู้ซื้อ 241 จำกัด", credit.PayerName);
        Assert.Equal("0107537000017", credit.PayerTaxId);
        // webhook ซ้ำ ⇒ ไม่บันทึกซ้ำ · ไม่เพิ่มแถวรอ 50 ทวิ
        await Handler(check).HandleSucceededAsync(intent);
        Assert.Equal(1, await check.Payments.CountAsync(p => p.CompanyId == cid && p.DocumentId == inv.Id));
        Assert.Equal(1, await check.WhtCreditsReceived.CountAsync(w => w.CompanyId == cid && w.DocumentId == inv.Id));
    }

    [Fact]
    public async Task ใบเสร็จงวดก่อนหัก_WHT_บางส่วน_ลิงก์คิดยอดสุทธิถูก_และบันทึกรับชำระได้ไม่ชนเพดาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, inv) = await SaleSeedAsync(db, Guid.NewGuid().ToString("N")[..6], priorReceiptWht: 15m);
        const string code = "gw241";
        db.PaymentProviderConfigs.Add(new PaymentProviderConfig { CompanyId = cid, ProviderCode = code, IsActive = true, Mode = PaymentProviderMode.Test });
        await db.SaveChangesAsync();
        var links = new DocumentPayLinkService(db, new[] { Provider(code) }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance);
        var link = await links.EnsureLinkAsync(cid, inv.Id, null);
        Assert.True(link.Ok, link.Refusal);
        Assert.Equal((535m, 15m, 520m), (link.Amounts!.Gross, link.Amounts.Wht, link.Amounts.Net));   // หักไปแล้ว 15 จากใบเสร็จ (ไม่ใช่ Payments)

        await Handler(db).HandleSucceededAsync(NetIntent(cid, inv.Id, 520m));   // ไม่โยน GrossCap
        using var check = DbTestDatabase.TryCreateContext()!;
        var pay = await check.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == cid && p.DocumentId == inv.Id);
        Assert.Equal(15m, pay.WithholdingTaxAmount);
        Assert.Equal(0m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv.Id)).BalanceDue);
    }

    [Fact]
    public async Task ผู้ซื้อบุคคลธรรมดา_ไม่มีหน้าที่หัก_ลิงก์เรียกยอดเต็ม_และหน้าเอกสารเตือน_นิติบุคคลยังหัก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (links, s) = await LinkSeedAsync(db, "s3 " + Guid.NewGuid().ToString("N")[..6]);
        // ทิศหนึ่ง: นิติบุคคล ⇒ หัก 30
        Assert.Equal(1_040m, (await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, null)).Amounts!.Net);
        Assert.Null((await links.StatusAsync(s.CompanyId, s.Invoice.Id)).WhtNote);
        // อีกทิศ: บุคคลธรรมดา (เลขบัตรขึ้นต้น 1) ⇒ ยอดเต็ม + เตือน
        await db.Contacts.Where(c => c.Id == s.ContactId).ExecuteUpdateAsync(x => x
            .SetProperty(c => c.ContactType, ContactType.Individual).SetProperty(c => c.TaxId, "1101700207030"));
        var a = (await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, null)).Amounts!;
        // รอบ 244: ยอดค้างของใบเป็นยอดหลังหักแล้ว (1,040) — ลิงก์เรียกยอดค้างตามใบ ไม่บวก WHT กลับเอง (เกินยอดค้าง = ตัวบันทึกรับชำระปฏิเสธ) · คำเตือนบอกให้แก้ใบ
        Assert.Equal((0m, 1_040m), (a.Wht, a.Net));
        Assert.Contains("ไม่มีหน้าที่หัก", (await links.StatusAsync(s.CompanyId, s.Invoice.Id)).WhtNote);
    }

    // รอบ 244 ฝ่ายค้าน S1: ผู้ซื้อไม่มีหน้าที่หัก ⇒ handler ส่ง WHT 0 ชัด ๆ (เดิม null ⇒ ตัวบันทึกรับชำระหยิบยอดแนะนำ = บันทึก WHT ที่ลูกค้าไม่ได้หัก)
    [Fact]
    public async Task R244_ผู้ซื้อบุคคลธรรมดา_เงินเข้าผ่านลิงก์_บันทึกWHTศูนย์_ไม่ใช้ยอดแนะนำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, inv) = await SaleSeedAsync(db, Guid.NewGuid().ToString("N")[..6]);
        await db.Contacts.Where(c => c.Id == inv.ContactId).ExecuteUpdateAsync(x => x
            .SetProperty(c => c.ContactType, ContactType.Individual).SetProperty(c => c.TaxId, "1101700207030"));
        await Handler(db).HandleSucceededAsync(NetIntent(cid, inv.Id, 1_040m));
        using var check = DbTestDatabase.TryCreateContext()!;
        var pay = await check.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == cid && p.DocumentId == inv.Id);
        Assert.Equal(0m, pay.WithholdingTaxAmount);
        Assert.Equal(0, await check.WhtCreditsReceived.CountAsync(w => w.CompanyId == cid && w.DocumentId == inv.Id));
        // ทิศตรงข้าม: นิติบุคคล ⇒ WHT 30 (เทสต์ handler_เส้นเต็ม ด้านบน)
    }

    // รอบ 244 ฝ่ายค้าน S2: WHT ของลิงก์ = ตัวคิดเดียวกับตัวบันทึกรับชำระ (NetCap ของ GetPaymentWhtSuggestionAsync) แม้มีใบลดหนี้อ้างใบ
    [Fact]
    public async Task R244_มีใบลดหนี้อ้างใบ_WHTของลิงก์เท่ากับยอดที่ตัวบันทึกรับชำระคิด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, inv) = await SaleSeedAsync(db, Guid.NewGuid().ToString("N")[..6]);
        db.Documents.Add(new Document
        {
            CompanyId = cid, ContactId = inv.ContactId, DocumentType = DocumentType.CreditNote, DocumentNumber = "CNH-" + Guid.NewGuid().ToString("N")[..6],
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, RelatedDocumentId = inv.Id,
            SubTotal = 100m, VatAmount = 7m, WithholdingTaxAmount = 3m, TotalAmount = 104m, Currency = "THB",
        });
        const string code = "gw241";
        db.PaymentProviderConfigs.Add(new PaymentProviderConfig { CompanyId = cid, ProviderCode = code, IsActive = true, Mode = PaymentProviderMode.Test });
        await db.SaveChangesAsync();
        var links = new DocumentPayLinkService(db, new[] { Provider(code) }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance);
        var link = await links.EnsureLinkAsync(cid, inv.Id, null);
        Assert.True(link.Ok, link.Refusal);
        using var req = DbTestDatabase.TryCreateContext()!;
        var balance = (await req.Documents.AsNoTracking().SingleAsync(d => d.Id == inv.Id)).BalanceDue;
        var recorder = await DocSvc(req).GetPaymentWhtSuggestionAsync(cid, inv.Id, balance);
        Assert.Equal(recorder.NetCap, link.Amounts!.Wht);
        Assert.Equal(balance, link.Amounts.Net);
    }

    [Fact]
    public async Task ลิงก์ใช้ไม่ได้_ไม่เปิดเผยข้อมูลของใบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (links, s) = await LinkSeedAsync(db, "s2 " + Guid.NewGuid().ToString("N")[..6]);
        var token = TokenOf((await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, null)).Url!);
        await links.RevokeAsync(s.CompanyId, s.Invoice.Id, "tester");
        var v = (await links.PublicViewAsync(s.CompanyId, token))!;
        Assert.Equal(DocumentPayLinkService.UnusableLinkNote, v.BlockedNote);
        Assert.Equal("", v.DocumentNumber);
        Assert.Null(v.CustomerName);
        Assert.Equal((0m, 0m, 0m), (v.Gross, v.Wht, v.Net));
        Assert.Null(v.Transfer);
        Assert.Equal("", v.CompanyName);
        var json = System.Text.Json.JsonSerializer.Serialize(v);
        Assert.DoesNotContain(s.Invoice.DocumentNumber, json);
        Assert.DoesNotContain("ลูกค้า 241", json);
    }

    [Fact]
    public async Task หน้าชำระสาธารณะ_ปฏิเสธวิธีที่ใบไม่ได้เปิด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (links, s) = await LinkSeedAsync(db, "ctl " + Guid.NewGuid().ToString("N")[..6]);
        var token = TokenOf((await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, new[] { "PromptPay" })).Url!);
        var started = false;
        var ctl = new Accounting.Controllers.PublicPaymentController(
            F4Proxy<IPaymentIntentService>.Create((_, _) => { started = true; return null; }),
            F4Proxy<IPublicPaymentResolver>.Create((_, _) => null), new[] { Provider("gw241") }, db,
            NullLogger<Accounting.Controllers.PublicPaymentController>.Instance, F4Proxy<ILodgingService>.Create((_, _) => null), links);
        var r = await ctl.StartDocument(s.CompanyId, token, new Accounting.Controllers.PublicPaymentController.StartRequest(PaymentMethodKind.Card), default);
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(r.Result);
        Assert.False(started);   // ไม่ถึงผู้ให้บริการ
    }

    [Fact]
    public async Task อีเมลล้มหลังออกใบเสร็จแล้ว_ลองใหม่_ไม่ออกซ้ำ_ไม่ส่งซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "retry " + Guid.NewGuid().ToString("N")[..6]);
        var (svc, sender, _, calls) = Automation(db, s);
        sender.Succeed = false;
        await svc.EnqueueAsync(s.CompanyId, s.Payment.Id, PaymentEntrySource.Gateway, null);
        var runId = (await RunOfAsync(db, s)).Id;
        await svc.ProcessRunAsync(runId, default);
        db.ChangeTracker.Clear();
        var run = await RunOfAsync(db, s);
        Assert.Equal(PostPaymentAutomationPolicy.StatusPending, run.Status);   // ลองใหม่อัตโนมัติ
        Assert.Contains("IssueReceipt", run.StepsDone);
        Assert.DoesNotContain("EmailReceipt", run.StepsDone);

        sender.Succeed = true;
        await svc.ProcessRunAsync(runId, default);
        db.ChangeTracker.Clear();
        run = await RunOfAsync(db, s);
        Assert.Equal(PostPaymentAutomationPolicy.StatusDone, run.Status);
        Assert.Single(calls, c => c == nameof(IDocumentService.IssueReceiptForPaymentAsync));   // ออกครั้งเดียว
        Assert.Equal(1, await db.Documents.CountAsync(d => d.CompanyId == s.CompanyId && d.DocumentType == DocumentType.Receipt));
        Assert.Equal(1, await db.DocumentEmailLogs.CountAsync(l => l.CompanyId == s.CompanyId && l.Status == EmailLogStatus.Sent));
    }

    // ───────── ฝ่ายค้าน B2: คิวอีเมลตามตารางผ่านเส้นแพลตฟอร์ม ─────────

    private static EmailScheduleService Sched(Accounting.Data.AccountingDbContext db, FakeSender sender, int? cap = null)
    {
        var d = new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" };
        if (cap != null) d[SignEmailThrottle.ConfigKey] = cap.ToString();
        return new EmailScheduleService(db, Factory(sender), F4Proxy<IPdfGenerationService>.Create((_, _) => null),
            NullLogger<EmailScheduleService>.Instance, payLinks: null, config: new ConfigurationBuilder().AddInMemoryCollection(d).Build());
    }

    private static async Task<EmailQueue> QueueAsync(Accounting.Data.AccountingDbContext db, Seed s, string? bcc = null, Guid? ruleId = null,
        string body = "<p>แนบเอกสาร</p>")
    {
        var q = new EmailQueue
        {
            CompanyId = s.CompanyId, RuleId = ruleId, EntityType = "Document", EntityId = s.Invoice.Id, ToEmail = "ap@buyer241.co.th",
            BccEmail = bcc, Subject = "ใบแจ้งหนี้ " + s.Invoice.DocumentNumber, Body = body, AttachPdf = false,
            ScheduledFor = DateTime.UtcNow, Status = "Sending", IdempotencyKey = "q241-" + Guid.NewGuid().ToString("N"),
        };
        db.EmailQueues.Add(q);
        await db.SaveChangesAsync();
        return q;
    }

    [Fact]
    public async Task คิวผ่านแพลตฟอร์ม_เกินเพดานไม่ส่งพร้อมเหตุ_BCC_ถูกนับ_HTML_ของผู้ใช้ถูกหนี()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "b2 " + Guid.NewGuid().ToString("N")[..6], invoiceStatus: DocumentStatus.Approved, balance: 1_070m);

        // BCC นับรวม: To 1 + BCC 5 = 6 > 5 ⇒ ไม่ส่ง
        var many = await QueueAsync(db, s, bcc: "a1@x241.co.th,a2@x241.co.th,a3@x241.co.th,a4@x241.co.th,a5@x241.co.th");
        var refused = await Sched(db, new FakeSender()).ComposeQueueMessageAsync(many, default);
        Assert.Contains("ไม่เกิน 5 ผู้รับ", refused.Refusal);
        Assert.Null(refused.Message);

        // เทมเพลตที่ผู้ใช้เขียนเอง = ข้อความล้วน (HTML ถูกหนี) · ห่อด้วยหัวเปิดเผยในนามบริษัท · From แพลตฟอร์ม
        var rule = new EmailScheduleRule { CompanyId = s.CompanyId, Trigger = "DocumentApproved", BodyTemplate = "<script>x</script> สวัสดี" };
        db.EmailScheduleRules.Add(rule);
        await db.SaveChangesAsync();
        var userQ = await QueueAsync(db, s, ruleId: rule.Id, body: "<script>x</script> สวัสดี");
        var ok = await Sched(db, new FakeSender()).ComposeQueueMessageAsync(userQ, default);
        Assert.Null(ok.Refusal);
        Assert.DoesNotContain("<script>", ok.Message!.HtmlBody);
        Assert.Contains("&lt;script&gt;", ok.Message.HtmlBody);
        Assert.Equal("no-reply@nextacc.net", ok.Message.FromAddress);
        Assert.NotNull(ok.Log);

        // เพดานบริษัท/วัน: ส่งไปแล้ว 1 ฉบับ + เพดาน 1 ⇒ ไม่ส่ง
        db.DocumentEmailLogs.Add(new DocumentEmailLog
        {
            CompanyId = s.CompanyId, ToEmail = "other@x241.co.th", Subject = "s", Body = "b", Status = EmailLogStatus.Sent,
            SenderMode = OnBehalfEmailSender.ModePlatform, SentAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var capped = await Sched(db, new FakeSender(), cap: 1).ComposeQueueMessageAsync(await QueueAsync(db, s), default);
        Assert.Contains("ครบเพดาน", capped.Refusal);
    }

    [Fact]
    public async Task คิวผ่าน_SMTP_บริษัท_พฤติกรรมเดิม_ไม่ห่อ_ไม่จำกัด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "b2co " + Guid.NewGuid().ToString("N")[..6], invoiceStatus: DocumentStatus.Approved, balance: 1_070m);
        await db.CompanySettings.Where(x => x.CompanyId == s.CompanyId).ExecuteUpdateAsync(x => x
            .SetProperty(c => c.EmailConfigured, true).SetProperty(c => c.EmailProvider, EmailProvider.Smtp)
            .SetProperty(c => c.EmailSmtpHost, "smtp.seller241.co.th").SetProperty(c => c.EmailFromAddress, "billing@seller241.co.th"));
        var q = await QueueAsync(db, s, bcc: "a1@x241.co.th,a2@x241.co.th,a3@x241.co.th,a4@x241.co.th,a5@x241.co.th", body: "<b>เนื้อเดิม</b>");
        var r = await Sched(db, new FakeSender()).ComposeQueueMessageAsync(q, default);
        Assert.Null(r.Refusal);
        Assert.Null(r.Log);
        Assert.Equal("<b>เนื้อเดิม</b>", r.Message!.HtmlBody);
        Assert.Equal(5, r.Message.Bcc.Count);
    }

    // ═══════════════ รอบ 250 (WP-02 · AUDIT_249_FOR_OPUS §4) ═══════════════

    private static async Task<PostPaymentAutomationRun?> RunOrNullAsync(Accounting.Data.AccountingDbContext db, Seed s)
        => await db.PostPaymentAutomationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.CompanyId == s.CompanyId && r.PaymentId == s.Payment.Id);

    [Fact]
    public async Task นำเข้ารับชำระCSV_มีแถวงานหลังรับชำระ_Settlement_มีแถวงาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        foreach (var source in new[] { PaymentEntrySource.ImportSendReceipt, PaymentEntrySource.Settlement, PaymentEntrySource.Integration })
        {
            var s = await SeedAsync(db, "r250-" + source + " " + Guid.NewGuid().ToString("N")[..6]);
            Assert.True(await PostPaymentAutomationService.EnqueueCoreAsync(db, NullLogger.Instance, s.CompanyId, s.Payment.Id, source, null, default));
            var run = await RunOrNullAsync(db, s);
            Assert.NotNull(run);
            Assert.Equal(PostPaymentAutomationPolicy.StatusPending, run!.Status);
            Assert.Equal(source.ToString(), run.Source);
        }
    }

    [Fact]
    public async Task CMS_มีแถวSkippedพร้อมเหตุ_ไม่ออกใบเสร็จซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "r250-cms " + Guid.NewGuid().ToString("N")[..6]);
        Assert.True(await PostPaymentAutomationService.EnqueueCoreAsync(db, NullLogger.Instance, s.CompanyId, s.Payment.Id, PaymentEntrySource.Cms, null, default));
        var run = (await RunOrNullAsync(db, s))!;
        Assert.Equal(PostPaymentAutomationPolicy.StatusSkipped, run.Status);
        Assert.Contains("มีเส้นออกใบเสร็จของตัวเอง", run.Message);
        Assert.NotNull(run.CompletedAt);
        // ตัวประมวลผลไม่หยิบแถว Skipped ⇒ ไม่ออก/ไม่ส่ง
        var (svc, sender, _, calls) = Automation(db, s);
        await svc.ProcessRunAsync(run.Id, default);
        Assert.Empty(sender.Sent);
        Assert.DoesNotContain(nameof(IDocumentService.IssueReceiptForPaymentAsync), calls);
    }

    [Fact]
    public async Task MultiDoc_ไม่สร้างแถว_ปิดค่าตั้ง_ไม่สร้างแถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "r250-multi " + Guid.NewGuid().ToString("N")[..6]);
        Assert.False(await PostPaymentAutomationService.EnqueueCoreAsync(db, NullLogger.Instance, s.CompanyId, s.Payment.Id, PaymentEntrySource.MultiDoc, null, default));
        Assert.Null(await RunOrNullAsync(db, s));
        var off = await SeedAsync(db, "r250-off " + Guid.NewGuid().ToString("N")[..6], emailReceipt: false);
        Assert.False(await PostPaymentAutomationService.EnqueueCoreAsync(db, NullLogger.Instance, off.CompanyId, off.Payment.Id, PaymentEntrySource.Cms, null, default));
        Assert.Null(await RunOrNullAsync(db, off));
    }

    private static async Task<Guid> RecurringOfAsync(Accounting.Data.AccountingDbContext db, Seed s, bool? postPayEmail)
    {
        var rec = new RecurringTransaction
        {
            CompanyId = s.CompanyId, Name = "รายการประจำ 250", Frequency = RecurringFrequency.Monthly, StartDate = DateTime.UtcNow.Date,
            NextRunDate = DateTime.UtcNow.Date.AddMonths(1), TemplateType = "Document", DocumentType = DocumentType.Invoice, ContactId = s.ContactId,
            TemplateData = "{}", PostPayEmailReceipt = postPayEmail,
        };
        db.RecurringTransactions.Add(rec);
        await db.SaveChangesAsync();
        await db.Documents.Where(d => d.Id == s.Invoice.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.RecurringTransactionId, (Guid?)rec.Id));
        return rec.Id;
    }

    [Fact]
    public async Task R248_ค่าตั้งต่อรายการปิด_ไม่ออกใบเสร็จแม้บริษัทเปิด_ทิศตรงข้ามInheritตามบริษัท()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var off = await SeedAsync(db, "r250-recoff " + Guid.NewGuid().ToString("N")[..6], emailReceipt: true);
        await RecurringOfAsync(db, off, postPayEmail: false);
        Assert.False((await PostPaymentAutomationService.SettingsForAsync(db, off.CompanyId, off.Invoice.Id, default)).EmailReceipt);
        Assert.False(await PostPaymentAutomationService.EnqueueCoreAsync(db, NullLogger.Instance, off.CompanyId, off.Payment.Id, PaymentEntrySource.Manual, null, default));
        Assert.Null(await RunOrNullAsync(db, off));

        var inherit = await SeedAsync(db, "r250-recinh " + Guid.NewGuid().ToString("N")[..6], emailReceipt: true);
        await RecurringOfAsync(db, inherit, postPayEmail: null);
        Assert.True(await PostPaymentAutomationService.EnqueueCoreAsync(db, NullLogger.Instance, inherit.CompanyId, inherit.Payment.Id, PaymentEntrySource.Manual, null, default));
        // ผู้ติดต่อปิด ⇒ ทับบริษัท (รายการไม่ได้ตั้ง)
        var contactOff = await SeedAsync(db, "r250-ctoff " + Guid.NewGuid().ToString("N")[..6], emailReceipt: true);
        await db.Contacts.Where(c => c.Id == contactOff.ContactId).ExecuteUpdateAsync(x => x.SetProperty(c => c.PostPayEmailReceipt, (bool?)false));
        Assert.False((await PostPaymentAutomationService.SettingsForAsync(db, contactOff.CompanyId, contactOff.Invoice.Id, default)).EmailReceipt);
    }

    [Fact]
    public async Task handler_เข้าคิวงานหลังรับชำระในแกนCreatePaymentAsync_แหล่งGateway_webhookซ้ำไม่เพิ่ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, inv) = await SaleSeedAsync(db, Guid.NewGuid().ToString("N")[..6]);
        var intent = NetIntent(cid, inv.Id, 1_040m);
        await Handler(db).HandleSucceededAsync(intent);
        using var check = DbTestDatabase.TryCreateContext()!;
        var run = await check.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.CompanyId == cid && r.DocumentId == inv.Id);
        Assert.Equal(nameof(PaymentEntrySource.Gateway), run.Source);
        Assert.Equal(PostPaymentAutomationPolicy.StatusPending, run.Status);
        // ทะเบียนรอ 50 ทวิ มีก่อนแถวงาน (Sync ในธุรกรรม → เข้าคิวหลัง commit)
        Assert.Equal(1, await check.WhtCreditsReceived.CountAsync(w => w.CompanyId == cid && w.DocumentId == inv.Id));
        await Handler(check).HandleSucceededAsync(intent);
        Assert.Equal(1, await check.PostPaymentAutomationRuns.CountAsync(r => r.CompanyId == cid && r.DocumentId == inv.Id));
    }

    [Fact]
    public async Task PublicView_INVที่ออกใบกำกับแล้ว_ลิงก์เดิมใช้ไม่ได้_บอกเลขใบกำกับ_ยกเลิกใบกำกับแล้วใช้ได้อีก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (links, s) = await LinkSeedAsync(db, "r250-tiv " + Guid.NewGuid().ToString("N")[..6]);
        var token = TokenOf((await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, null)).Url!);
        Assert.True((await links.PublicViewAsync(s.CompanyId, token))!.CanPayOnline);
        var tiv = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.TaxInvoice, DocumentNumber = "TIV250-" + Random.Shared.Next(100000, 999999),
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, RelatedDocumentId = s.Invoice.Id,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_040m, WithholdingTaxAmount = 30m, BalanceDue = 1_040m, Currency = "THB",
        };
        db.Documents.Add(tiv);
        await db.SaveChangesAsync();
        var v = (await links.PublicViewAsync(s.CompanyId, token))!;
        Assert.False(v.CanPayOnline);
        Assert.Contains(tiv.DocumentNumber, v.BlockedNote);
        Assert.Equal(0m, v.Target.AmountDue);
        Assert.Contains(tiv.DocumentNumber, (await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, null)).Refusal);
        // ทิศตรงข้าม: ยกเลิกใบกำกับ ⇒ ลิงก์ของใบแจ้งหนี้กลับมาใช้ได้ (ตัดสินสด)
        await db.Documents.Where(d => d.Id == tiv.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Voided));
        Assert.True((await links.PublicViewAsync(s.CompanyId, token))!.CanPayOnline);
    }

    [Fact]
    public async Task ผู้ซื้อบุคคลธรรมดาและใบมีWHT_มีคำเตือนตอนอนุมัติ_นิติบุคคลไม่เตือน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, inv) = await SaleSeedAsync(db, Guid.NewGuid().ToString("N")[..6]);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.DoesNotContain(await DocSvc(req).PreviewApprovalWarningsAsync(cid, inv.Id), w => w.Contains("ตรวจประเภทเงินได้"));
        await db.Contacts.Where(c => c.Id == inv.ContactId).ExecuteUpdateAsync(x => x
            .SetProperty(c => c.ContactType, ContactType.Individual).SetProperty(c => c.TaxId, "1101700207030"));
        using var req2 = DbTestDatabase.TryCreateContext()!;
        var warn = Assert.Single(await DocSvc(req2).PreviewApprovalWarningsAsync(cid, inv.Id), w => w.Contains("ตรวจประเภทเงินได้"));
        Assert.DoesNotContain("(ไม่มีหน้าที่หัก)", warn);   // ฝ่ายค้าน WP-02 ข้อ 7: ไม่ฟันธง
        Assert.StartsWith(WhtCertIssuePlan.ApprovalWarningPrefix, warn);
    }

    // AUDIT_249_COVERAGE M-3: ทางเข้าที่ 9 — integration บันทึกการชำระเอง แล้วเรียกขั้นหลัง commit ตัวเดียวกับ CreatePaymentAsync
    [Fact]
    public async Task Integration_รับชำระ_มีแถวงานหลังรับชำระ_แหล่งIntegration_และทะเบียน50ทวิ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, inv) = await SaleSeedAsync(db, Guid.NewGuid().ToString("N")[..6]);
        var integration = new ExternalIntegration { CompanyId = cid, SystemName = "TakeTime", SystemType = "PMS" };
        db.Set<ExternalIntegration>().Add(integration);
        await db.SaveChangesAsync();
        InboundSyncResponse res;
        using (var req = DbTestDatabase.TryCreateContext()!)
            res = await new IntegrationService(req, null!, NullLogger<IntegrationService>.Instance, null!, null!, null!, documentService: DocSvc(req))
                .ProcessPaymentAsync(cid, integration.Id, new InboundPaymentRequest(
                    ExternalId: "EXT-250", ExternalRef: "REF-250", InvoiceExternalRef: null, DocumentId: inv.Id, CustomerExternalId: null, CustomerName: null,
                    PaymentDate: DateTime.UtcNow.Date, Amount: 1_040m, PaymentMethod: "Cash", BankAccountName: null, ReferenceNo: "REF-250-" + Guid.NewGuid().ToString("N")[..6],
                    SlipUrl: null, Notes: null));
        Assert.True(res.Success, res.Message);
        using var check = DbTestDatabase.TryCreateContext()!;
        var run = await check.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.CompanyId == cid && r.DocumentId == inv.Id);
        Assert.Equal(nameof(PaymentEntrySource.Integration), run.Source);
    }

    // ───────── รอบ 250 WP-04 (AUDIT_249 A B-19 · ฝ่ายค้าน 248 MED-5): เส้น enqueue → send ของรายการประจำ ใช้ช่องทางรับเงินของ "รายการ" ตอนส่งด้วย ─────────

    /// <summary>บริษัทเปิด "แนบลิงก์ชำระเป็นค่าตั้งต้น" + กฎ "เมื่อรายการประจำสร้างเอกสาร" (ไม่ได้ตั้งแนบ/ไม่แนบ = ตามชั้นถัดไป) + รายการประจำโหมด
    /// <paramref name="paymentMode"/> ⇒ เข้าคิวผ่านเมธอดจริง แล้วตรวจซ้ำตอนส่ง (RecheckDocumentAtSendAsync — internal ของรอบส่งคิว เรียกตรง
    /// เฉพาะแถวนี้ ไม่กวาดคิวทั้งฐาน) ⇒ คืนแถวคิวหลังเขียนเนื้อใหม่ ณ เวลาส่ง</summary>
    private async Task<(EmailQueue Queued, EmailQueue AtSend, Guid RecurringId, Guid InvoiceId)?> Med5Async(string? paymentMode)
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return null; }
        var (links, s) = await LinkSeedAsync(db, "med5 " + Guid.NewGuid().ToString("N")[..6]);
        await db.CompanySettings.Where(x => x.CompanyId == s.CompanyId).ExecuteUpdateAsync(x => x.SetProperty(c => c.PayLinkDefaultEnabled, true));
        db.ChangeTracker.Clear();
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var rec = new RecurringTransaction
        {
            CompanyId = s.CompanyId, Name = "ค่าบริการรายเดือน (MED-5)", Frequency = RecurringFrequency.Monthly, Status = RecurringStatus.Active,
            StartDate = today, NextRunDate = today, PreferredDay = today.Day, TemplateType = "Document", DocumentType = DocumentType.Invoice,
            ContactId = s.ContactId, AutoApprove = true, CreatedBy = "tester", TemplateData = "{\"lines\":[]}", PaymentMode = paymentMode,
        };
        db.RecurringTransactions.Add(rec);
        db.EmailScheduleRules.Add(new EmailScheduleRule { CompanyId = s.CompanyId, Trigger = "RecurringInvoiceCreated", IsActive = true, AttachPayLink = null });
        await db.SaveChangesAsync();

        var outcome = await new EmailScheduleService(db, Factory(new FakeSender()), F4Proxy<IPdfGenerationService>.Create((_, _) => null),
            NullLogger<EmailScheduleService>.Instance, links, Config()).OnRecurringDocumentCreatedAsync(s.CompanyId, s.Invoice.Id, rec.Id);
        Assert.Equal(EmailEnqueueKind.Enqueued, outcome.Kind);
        var queued = await db.EmailQueues.AsNoTracking().SingleAsync(q => q.CompanyId == s.CompanyId && q.EntityId == s.Invoice.Id);
        Assert.Equal(rec.Id, ScheduledEmailSendCheck.RecurringIdFromIdempotencyKey(queued.IdempotencyKey));   // กุญแจพกรายการประจำไปถึงตอนส่ง

        // ตอนส่ง: context ใหม่ (รอบส่งคิวเป็นคำขอแยก) · กฎถูกโหลดใหม่จากฐาน (AttachPayLink = null) ⇒ ต้องต่อชั้นรายการประจำเอง
        using var send = DbTestDatabase.TryCreateContext()!;
        var sendLinks = new DocumentPayLinkService(send, new[] { Provider("gw241") }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance);
        var sched = new EmailScheduleService(send, Factory(new FakeSender()), F4Proxy<IPdfGenerationService>.Create((_, _) => null),
            NullLogger<EmailScheduleService>.Instance, sendLinks, Config());
        var item = await send.EmailQueues.SingleAsync(q => q.Id == queued.Id);
        var skip = await sched.RecheckDocumentAtSendAsync(item, CancellationToken.None);   // internal (ฝ่ายค้าน WP-04 ข้อ 8 — ไม่ใช้ reflection)
        Assert.Null(skip);
        return (queued, item, rec.Id, s.Invoice.Id);
    }

    [Fact]
    public async Task R248_MED5_TransferOnly_รายการ_อีเมลที่ส่งไม่มีลิงก์บัตร()
    {
        var r = await Med5Async(AutopilotSettingsResolver.TransferOnly);
        if (r == null) return;
        var (queued, atSend, _, invoiceId) = r.Value;
        // บริษัทแนบลิงก์เป็นค่าตั้งต้น แต่รายการตั้ง "โอนเข้าบัญชีอย่างเดียว" ⇒ ทั้งตอนเข้าคิวและตอนส่ง (เนื้อที่เขียนใหม่) ไม่มีลิงก์/ปุ่มชำระออนไลน์
        Assert.DoesNotContain(DocumentPayLinkPolicy.QueuePlaceholder, queued.Body);
        Assert.DoesNotContain(DocumentPayLinkPolicy.QueuePlaceholder, atSend.Body);   // เดิม (ก่อน MED-5): ตอนส่งได้ลิงก์ตามบริษัท
        Assert.DoesNotContain("ชำระเงินออนไลน์", atSend.Body);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Null((await check.Documents.AsNoTracking().SingleAsync(d => d.Id == invoiceId)).PayLinkTokenHash);   // ไม่มีการออกลิงก์เลย
    }

    [Fact]
    public async Task R248_MED5_Inherit_รายการไม่ได้ตั้ง_อีเมลที่ส่งมีลิงก์ตามบริษัท()
    {
        var r = await Med5Async(null);
        if (r == null) return;
        var (queued, atSend, _, invoiceId) = r.Value;
        // ทิศตรงข้าม: รายการไม่ได้ตั้งช่องทาง ⇒ ตามบริษัท (แนบ) ทั้งตอนเข้าคิวและตอนส่ง
        Assert.Contains(DocumentPayLinkPolicy.QueuePlaceholder, queued.Body);
        Assert.Contains(DocumentPayLinkPolicy.QueuePlaceholder, atSend.Body);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.NotNull((await check.Documents.AsNoTracking().SingleAsync(d => d.Id == invoiceId)).PayLinkTokenHash);
    }

    /// <summary>รอบ 259 (ฝ่ายค้าน 259 B-L5 · C-M1 · คำตัดสิน 259-2-8): ผู้เรียกที่ไม่ได้ตั้งใจออกใหม่ (พอร์ทัล/หน้าต่างส่งเอง — ค่าตั้งต้น) ·
    /// ไม่เคยออก ⇒ ได้ลิงก์ · ถูกเพิกถอน ⇒ <see cref="PayLinkRevokedException"/> (ไม่ล้างการเพิกถอน) · ปุ่มของพนักงานออกใหม่ได้ + audit ·
    /// ลิงก์ที่ยังใช้ได้ไม่ถูกเขียนทับช่องทาง</summary>
    [Fact]
    public async Task R259_ไม่เคยออกได้ลิงก์_เพิกถอนแล้วผู้เรียกทั่วไปถูกปฏิเสธ_พนักงานออกใหม่ได้พร้อม_audit_ช่องทางลิงก์ที่ใช้อยู่ไม่ถูกทับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (links, s) = await LinkSeedAsync(db, "r259 " + Guid.NewGuid().ToString("N")[..6]);

        var first = await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, new[] { "PromptPay" });   // ไม่เคยออก ⇒ ได้ลิงก์ (ทางของพอร์ทัล)
        Assert.True(first.Ok, first.Refusal);
        var again = await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, new[] { "PromptPay", DocumentPayLinkPolicy.BankTransferCode });
        Assert.Equal(first.Url, again.Url);
        var stored = await db.Documents.AsNoTracking().Where(d => d.Id == s.Invoice.Id).Select(d => d.PayLinkChannelsJson).SingleAsync();
        Assert.Equal(new[] { "PromptPay" }, DocumentPayLinkPolicy.ParseChannels(stored));   // ช่องทางของลิงก์ที่ใช้อยู่ไม่ถูกทับ

        await links.RevokeAsync(s.CompanyId, s.Invoice.Id, "tester");
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<PayLinkRevokedException>(() => links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, null));
        Assert.NotNull(await db.Documents.AsNoTracking().Where(d => d.Id == s.Invoice.Id).Select(d => d.PayLinkRevokedAt).SingleAsync());   // ไม่ล้างการเพิกถอน
        Assert.Equal(0, await db.AuditLogs.CountAsync(a => a.CompanyId == s.CompanyId && a.EntityType == "DocumentPayLink"));

        var staff = await links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, null, default, allowReissueRevoked: true, actor: "owner@seller241.co.th");
        Assert.True(staff.Ok, staff.Refusal);
        Assert.NotEqual(first.Url, staff.Url);
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.EntityType == "DocumentPayLink");
        Assert.Contains("pay-link-reissued", audit.NewValues);
        Assert.Equal("owner@seller241.co.th", audit.UserEmail);
    }
}
