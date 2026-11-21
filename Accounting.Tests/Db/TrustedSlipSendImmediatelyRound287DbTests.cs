using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
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
using T286 = Accounting.Tests.Db.TrustedSlipPayerRound286DbTests;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 287 (คำสั่งเจ้าของ "ถ้าเป็นผู้จ่ายที่ไว้ใจได้ อนุญาตให้ส่งใบกำกับภาษี หรือเอกสารอื่นๆอัตโนมัติได้เลย" · คำตัดสิน 287-1…) บน PostgreSQL จริง —
/// ค่าตั้ง "ส่งเอกสารทันที" เปิด ⇒ รับเงินล่วงหน้าแล้วงานหลังรับชำระส่งใบเสร็จ/ใบกำกับถึงผู้ซื้อทันที · ปิด ⇒ กักจนรายการเดินบัญชียืนยันแล้วส่ง (286 MED-1 เดิม) ·
/// ตรวจย้อนล้มหลังส่งไปแล้ว ⇒ แจ้งผู้ขายว่า "ส่งไปแล้ว" + ทางไปต่อ (ระบบไม่ยกเลิกเอง) และกักการส่งที่เหลือ · ไม่มีบริการ AI (kill-switch)
/// </summary>
[Trait("Category", "Db")]
public class TrustedSlipSendImmediatelyRound287DbTests
{
    private readonly ITestOutputHelper _out;
    public TrustedSlipSendImmediatelyRound287DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("trusted-send-287-db");

    private sealed class FakeSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();
        public EmailProvider Provider => EmailProvider.Smtp;
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(EmailSendResult.Ok("mid-287"));
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

    private static PostPaymentAutomationService PostPay(AccountingDbContext db, FakeSender sender)
    {
        var email = new DocumentEmailService(db, Factory(sender), F4Proxy<IEtaxInvoiceService>.Create((_, _) => null),
            NullLogger<DocumentEmailService>.Instance, pdf: null, storage: null, config: Config());
        return new PostPaymentAutomationService(db, F4Proxy<IDocumentService>.Create((_, _) => null), email, Factory(sender),
            NullLogger<PostPaymentAutomationService>.Instance, recipients: new ContactRecipientResolver(db, Secrets, Central()));
    }

    /// <summary>ผู้ซื้อครบข้อมูลใบกำกับ + อีเมลที่รับใบเสร็จ (ช่องที่ผู้ซื้อตั้งไว้ — ไม่เปิดช่องใหม่)</summary>
    private static async Task BuyerReadyAsync(AccountingDbContext db, T286.Seed s)
    {
        await db.Contacts.Where(c => c.Id == s.ContactId).ExecuteUpdateAsync(x => x
            .SetProperty(c => c.TaxId, "0107537000017").SetProperty(c => c.Address, "1 ถนนสุขุมวิท กรุงเทพฯ")
            .SetProperty(c => c.ContactType, ContactType.JuristicPerson));
        var ep = new ContactNotifyEndpointService(db, new ContactRecipientResolver(db, Secrets, Central()));
        await ep.AddEmailAsync(s.CompanyId, s.ContactId, new AddContactNotifyEmailRequest("acct@buyer287.co.th", null, new List<string> { nameof(NotifyTopic.Receipt) }),
            new ContactNotifyActor(null, "tester@r287.local", "127.0.0.1"));
        db.ChangeTracker.Clear();
    }

    /// <summary>สลิปของผู้จ่ายที่ไว้ใจได้ (ไม่มีรายการเดินบัญชี) ⇒ การชำระล่วงหน้า + ใบเสร็จของการชำระนั้น (ออกในระบบแล้ว) · คืนการชำระ</summary>
    private static async Task<Payment> ProvisionalAsync(AccountingDbContext db, T286.Seed s, T286.Svcs v, string qrRef)
    {
        var inv = await T286.InvoiceAsync(db, s);
        await v.Line.HandleForwardedAsync(T286.Fwd(s, T286.Slip(1_070m, qrRef)));
        db.ChangeTracker.Clear();
        var pay = await db.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == inv.Id && !p.IsDeleted);
        var intent = await db.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.SourceId == inv.Id);
        Assert.NotNull(intent.TrustProvisionalAt);
        Assert.Null(intent.TrustVerifiedAt);
        if (!await db.Documents.AnyAsync(d => d.CompanyId == s.CompanyId && d.SettlementPaymentId == pay.Id))
        {
            db.Documents.Add(new Document
            {
                CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Receipt,
                DocumentNumber = "REC287-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Paid,
                DocumentDate = pay.PaymentDate, SubTotal = 1_070m, TotalAmount = 1_070m, PaidAmount = 1_070m, Currency = "THB",
                RelatedDocumentId = inv.Id, IsSettlementReceipt = true, SettlementPaymentId = pay.Id, IsTaxInvoiceByLaw = false,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        return pay;
    }

    private static async Task<PostPaymentAutomationRun> RunOfAsync(AccountingDbContext db, Guid companyId, Guid paymentId)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await c.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.CompanyId == companyId && r.PaymentId == paymentId);
    }

    [Fact]
    public async Task T287_01_ค่าตั้งเปิด_รับล่วงหน้าแล้ว_ส่งใบเสร็จถึงผู้ซื้อทันที()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await T286.SeedAsync(db, sendImmediately: true, postPayEmail: true);
        var v = T286.Build(db, s);
        await T286.FirstTrustedAsync(db, s, v);
        await BuyerReadyAsync(db, s);
        var sender = new FakeSender();
        await PostPay(db, sender).ProcessDueAsync();   // งานของการชำระแรก (ยืนยันแล้ว) — ไม่ใช่เรื่องที่ทดสอบ
        sender.Sent.Clear();

        var pay = await ProvisionalAsync(db, s, v, "287SENDNOWREF01");
        Assert.False(await TrustedSlipVerifyService.DeliveryHeldAsync(db, s.CompanyId, pay.Id));
        var run = await RunOfAsync(db, s.CompanyId, pay.Id);
        await PostPay(db, sender).ProcessRunAsync(run.Id, default);

        var msg = Assert.Single(sender.Sent);
        Assert.Contains("acct@buyer287.co.th", msg.To);
        run = await RunOfAsync(db, s.CompanyId, pay.Id);
        Assert.Equal(PostPaymentAutomationPolicy.StatusDone, run.Status);
        Assert.DoesNotContain(TrustedSlipPayerRule.DeliveryHeldNote, run.Message ?? "");
        Assert.True(PostPaymentAutomationPolicy.DeliveredToBuyer(run.StepsDone));
    }

    [Fact]
    public async Task T287_02_ค่าตั้งปิด_ไม่ส่งจนธนาคารยืนยัน_แล้วส่งเมื่อยืนยัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await T286.SeedAsync(db, sendImmediately: false, postPayEmail: true);
        var v = T286.Build(db, s);
        await T286.FirstTrustedAsync(db, s, v);
        await BuyerReadyAsync(db, s);
        var sender = new FakeSender();
        await PostPay(db, sender).ProcessDueAsync();
        sender.Sent.Clear();

        var pay = await ProvisionalAsync(db, s, v, "287HOLDREF00002");
        Assert.True(await TrustedSlipVerifyService.DeliveryHeldAsync(db, s.CompanyId, pay.Id));
        var run = await RunOfAsync(db, s.CompanyId, pay.Id);
        await PostPay(db, sender).ProcessRunAsync(run.Id, default);
        Assert.Empty(sender.Sent);
        run = await RunOfAsync(db, s.CompanyId, pay.Id);
        Assert.Equal(PostPaymentAutomationPolicy.StatusPending, run.Status);
        Assert.Contains(TrustedSlipPayerRule.DeliveryHeldNote, run.Message ?? "");
        Assert.False(PostPaymentAutomationPolicy.DeliveredToBuyer(run.StepsDone));

        // รายการเดินบัญชีตามมา ⇒ ยืนยัน ⇒ ปล่อยงาน (NextAttemptAt = ตอนนี้) ⇒ รอบถัดไปส่ง
        var lineId = await T286.AddLineAsync(db, s, 1_070m, "287HOLDREF00002");
        var before = DateTime.UtcNow;
        await v.Bank.EvaluateLineAsync(s.CompanyId, lineId, true, DateTime.UtcNow);
        Assert.False(await TrustedSlipVerifyService.DeliveryHeldAsync(db, s.CompanyId, pay.Id));
        run = await RunOfAsync(db, s.CompanyId, pay.Id);
        Assert.True(run.NextAttemptAt <= DateTime.UtcNow && run.NextAttemptAt >= before.AddSeconds(-5));
        db.ChangeTracker.Clear();
        await PostPay(db, sender).ProcessRunAsync(run.Id, default);
        var msg = Assert.Single(sender.Sent);
        Assert.Contains("acct@buyer287.co.th", msg.To);
        Assert.Equal(PostPaymentAutomationPolicy.StatusDone, (await RunOfAsync(db, s.CompanyId, pay.Id)).Status);
    }

    [Fact]
    public async Task T287_03_ส่งไปแล้ว_ตรวจย้อนล้ม_แจ้งว่าส่งไปแล้ว_ทางไปต่อ_ไม่ยกเลิกเอง_กักต่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var sent = new List<(Guid Company, string Key, NotificationContext Ctx)>();
        var notify = F4Proxy<INotificationEngine>.Create((m, args) =>
        {
            if (m == nameof(INotificationEngine.DispatchAsync)) sent.Add(((Guid)args[0]!, (string)args[1]!, (NotificationContext)args[2]!));
            return null;
        });
        var s = await T286.SeedAsync(db, sendImmediately: true, postPayEmail: true);
        var v = T286.Build(db, s, notify);
        await T286.FirstTrustedAsync(db, s, v);
        await BuyerReadyAsync(db, s);
        var sender = new FakeSender();
        await PostPay(db, sender).ProcessDueAsync();
        sender.Sent.Clear();

        var pay = await ProvisionalAsync(db, s, v, "287FAILAFTER003");
        var run = await RunOfAsync(db, s.CompanyId, pay.Id);
        await PostPay(db, sender).ProcessRunAsync(run.Id, default);
        Assert.Single(sender.Sent);

        DateTime due;
        using (var c = DbTestDatabase.TryCreateContext()!)
            due = (await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.TrustProvisionalAt != null && i.TrustVerifiedAt == null))
                .TrustReviewDueAt!.Value;
        await v.Verify.SweepAsync(due.AddHours(1));

        var fail = Assert.Single(sent, x => x.Company == s.CompanyId && x.Key == NotificationEvents.AutopilotSlipTrustedUnverified);
        Assert.Contains("ส่งใบเสร็จ/ใบกำกับถึงผู้ซื้อไปแล้ว", fail.Ctx.Message);
        Assert.Contains("ใบลดหนี้", fail.Ctx.Message);
        Assert.Contains("ระบบไม่ยกเลิกเอง", fail.Ctx.Message);
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.True(await c.Payments.AnyAsync(p => p.Id == pay.Id && !p.IsDeleted));   // ผู้ขายเลือกยกเลิกเอง
        Assert.True(await TrustedSlipVerifyService.DeliveryHeldAsync(db, s.CompanyId, pay.Id));   // ล้มแล้วกักเสมอ (แม้ค่าตั้งเปิด)
    }

    [Fact]
    public async Task T287_04_ทิศตรงข้าม_ยังไม่ส่ง_ตรวจย้อนล้ม_แจ้งว่ายังไม่ถูกส่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var sent = new List<(Guid Company, string Key, NotificationContext Ctx)>();
        var notify = F4Proxy<INotificationEngine>.Create((m, args) =>
        {
            if (m == nameof(INotificationEngine.DispatchAsync)) sent.Add(((Guid)args[0]!, (string)args[1]!, (NotificationContext)args[2]!));
            return null;
        });
        var s = await T286.SeedAsync(db, sendImmediately: false, postPayEmail: true);
        var v = T286.Build(db, s, notify);
        await T286.FirstTrustedAsync(db, s, v);
        var pay = await ProvisionalAsync(db, s, v, "287NOTSENT00004");
        DateTime due;
        using (var c = DbTestDatabase.TryCreateContext()!)
            due = (await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.TrustProvisionalAt != null && i.TrustVerifiedAt == null))
                .TrustReviewDueAt!.Value;
        await v.Verify.SweepAsync(due.AddHours(1));
        var fail = Assert.Single(sent, x => x.Company == s.CompanyId && x.Key == NotificationEvents.AutopilotSlipTrustedUnverified);
        Assert.Contains("ยังไม่ถูกส่งถึงผู้ซื้อ", fail.Ctx.Message);
        Assert.DoesNotContain("ไปแล้ว", fail.Ctx.Message);
        Assert.True(await TrustedSlipVerifyService.DeliveryHeldAsync(db, s.CompanyId, pay.Id));
    }

    /// <summary>ฝ่ายค้าน 287 MED: งานที่กักไว้ (ค่าตั้งปิด) ต้องไม่ค้าง 30 วัน — ตรวจซ้ำตามระยะสั้น · ผู้ขายเปิดค่าตั้งภายหลัง ⇒ ปลุก ⇒ รอบถัดไปส่ง ·
    /// แถวที่ 286 ตั้งไว้ +30 วันก่อน deploy ⇒ migration ปลุก</summary>
    [Fact]
    public async Task T287_05_งานกักอยู่_เปิดค่าตั้งภายหลัง_ส่งรอบถัดไป()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await T286.SeedAsync(db, sendImmediately: false, postPayEmail: true);
        var v = T286.Build(db, s);
        await T286.FirstTrustedAsync(db, s, v);
        await BuyerReadyAsync(db, s);
        var sender = new FakeSender();
        await PostPay(db, sender).ProcessDueAsync();
        sender.Sent.Clear();

        var pay = await ProvisionalAsync(db, s, v, "287WAKELATER005");
        var run = await RunOfAsync(db, s.CompanyId, pay.Id);
        await PostPay(db, sender).ProcessRunAsync(run.Id, default);
        Assert.Empty(sender.Sent);
        run = await RunOfAsync(db, s.CompanyId, pay.Id);
        Assert.Equal(PostPaymentAutomationPolicy.StatusPending, run.Status);
        Assert.True(run.NextAttemptAt <= DateTime.UtcNow.AddHours(1).AddMinutes(1), "งานที่กักต้องตรวจซ้ำภายใน 1 ชม. (เดิม 30 วัน)");

        // จำลองแถวที่ 286 ตั้งไว้ +30 วันก่อน deploy ⇒ migration ปลุก
        await db.PostPaymentAutomationRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.NextAttemptAt, DateTime.UtcNow.AddDays(30)));
        foreach (var sql in Round287Migration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        Assert.True((await RunOfAsync(db, s.CompanyId, pay.Id)).NextAttemptAt <= DateTime.UtcNow);

        // ผู้ขายเปิดค่าตั้ง ⇒ ปลุกงานของบริษัท (ไม่รอระยะตรวจซ้ำ) ⇒ รอบถัดไปส่ง
        await db.PostPaymentAutomationRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.NextAttemptAt, DateTime.UtcNow.AddHours(1)));
        await db.CompanySettings.Where(x => x.CompanyId == s.CompanyId).ExecuteUpdateAsync(u => u.SetProperty(x => x.SlipTrustedSendImmediately, true));
        Assert.Equal(1, await TrustedSlipVerifyService.WakeHeldDeliveriesAsync(db, s.CompanyId, DateTime.UtcNow));
        db.ChangeTracker.Clear();
        await PostPay(db, sender).ProcessRunAsync(run.Id, default);
        var msg = Assert.Single(sender.Sent);
        Assert.Contains("acct@buyer287.co.th", msg.To);
        Assert.Equal(PostPaymentAutomationPolicy.StatusDone, (await RunOfAsync(db, s.CompanyId, pay.Id)).Status);
    }
}
