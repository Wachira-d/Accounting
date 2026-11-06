using System.Text;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.FileAttachment;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
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
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §3.2 · §4.3 · §6.3 · คำตัดสินเจ้าของ §11 ข้อ 3–4) — เส้นจริงบน PostgreSQL:
/// ลิงก์อัปโหลดสาธารณะ (token ปลอม/ข้ามบริษัท = ไม่เปิดเผยอะไร · ไฟล์ปลอม = ปฏิเสธ · ส่งสำเร็จ = แถวกล่องรับเดียว · ส่งซ้ำ = แถวเดิม · ลิงก์ถูกยกเลิก = 410 ·
/// เกินเพดาน = 429) · ทวงอัตโนมัติ (ขั้น 7 วัน = คิวเดียว · รอบซ้ำไม่ส่งซ้ำ · ผู้ติดต่อไม่ส่งอัตโนมัติ = แถว "ข้าม" พร้อมเหตุ · ได้ใบแล้ว = หยุด · JobLock กันรันซ้อน) ·
/// "ไม่หัก ณ ที่จ่าย" (นิติบุคคล = เป้าหมายยอดเต็ม + แถวหลักฐาน hash ตรง + audit · บุคคลธรรมดาไม่เห็นตัวเลือก)
/// </summary>
[Trait("Category", "Db")]
public class WhtCertRound253DbTests
{
    private readonly ITestOutputHelper _out;
    public WhtCertRound253DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private const string Ours = "0105556123453";
    private const string Payer = "0107556000116";

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = "https://app.nextacc.test", ["Jwt:Secret"] = "wht-253-db-test-secret-0123456789abcdef",
        }).Build();

    private static string TokenOf(string url) => System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["t"]!;

    private sealed record Seed(Guid CompanyId, Guid ContactId, Guid DocumentId, Guid CreditId);

    private static async Task<Seed> SeedAsync(AccountingDbContext db, string tag, int daysAgo = 10, bool reminders = false, bool autoSendOff = false)
    {
        var company = new Company { Name = "บริษัท 253 " + tag, TaxId = Ours, FiscalYearStartMonth = 1 };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id, WhtCertReminderEnabled = reminders });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 253 จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = Payer, Email = "ap@buyer253.co.th", AutoSendDisabled = autoSendOff,
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var doc = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.TaxInvoice,
            DocumentNumber = "INV253-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Paid,
            DocumentDate = DateTime.UtcNow.Date.AddDays(-daysAgo), SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_040m,
            WithholdingTaxAmount = 30m, PaidAmount = 1_040m, BalanceDue = 0m, Currency = "THB",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        var credit = new WhtCreditReceived
        {
            CompanyId = company.Id, TaxYear = DateTime.UtcNow.Year, PayerName = contact.Name, PayerTaxId = Payer, PayerContactId = contact.Id,
            DocumentId = doc.Id, IncomeAmount = 1_000m, WhtRate = 3m, WhtAmount = 30m, Status = WhtCreditStatus.Pending,
            FirstWithheldAt = DateTime.UtcNow.AddDays(-daysAgo),
        };
        db.WhtCreditsReceived.Add(credit);
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id, doc.Id, credit.Id);
    }

    // ─────────── ลิงก์อัปโหลด ───────────

    /// <summary>ไฟล์ที่ "เก็บ" ลงฐานจริง (ไม่แตะดิสก์) — นับจำนวนครั้งที่ถูกเรียก</summary>
    private sealed class FilesProbe
    {
        public int Uploads;
        public IFileAttachmentService For(AccountingDbContext db) => F4Proxy<IFileAttachmentService>.Create((m, a) =>
        {
            if (m != nameof(IFileAttachmentService.UploadAnonymousBytesAsync)) return null;
            Uploads++;
            var att = new FileAttachment
            {
                CompanyId = (Guid)a[0]!, EntityType = (string)a[1]!, EntityId = (Guid)a[2]!, FileName = "f", OriginalFileName = (string)a[3]!,
                ContentType = (string)a[4]!, FileSize = ((byte[])a[5]!).LongLength, StoragePath = "mem/" + Guid.NewGuid(),
            };
            db.FileAttachments.Add(att);
            db.SaveChanges();
            return new FileAttachmentResponse(att.Id, att.FileName, att.OriginalFileName, att.ContentType, att.FileSize, att.StoragePath, null,
                att.EntityType, att.EntityId, "", att.CreatedAt);
        });
    }

    /// <summary>OCR ล่ม — เส้นอัปโหลดต้องยังเข้ากล่องรับ (รอผู้ขายตรวจ) ไม่ทิ้งไฟล์ลูกค้าเงียบ</summary>
    private static IOcrService OcrDown() => F4Proxy<IOcrService>.Create((m, _) =>
        m == nameof(IOcrService.ScanAsync) ? throw new InvalidOperationException("OCR ล่ม (เทสต์)") : (object?)null);

    private static IAiOrchestrator NoAi() => F4Proxy<IAiOrchestrator>.Create((_, _) =>
        new AiResponse { Status = AiCallStatus.NoProvider, UsedAi = false });

    private static (WhtCertUploadLinkService Svc, FilesProbe Files) Links(AccountingDbContext db)
    {
        var intake = new WhtCertIntakeService(db, NoAi(), F4Proxy<IAiFeedbackRecorder>.Create((_, _) => null),
            new WhtCreditService(db, NullLogger<WhtCreditService>.Instance), NullLogger<WhtCertIntakeService>.Instance);
        var files = new FilesProbe();
        return (new WhtCertUploadLinkService(db, intake, OcrDown(), files.For(db), NullLogger<WhtCertUploadLinkService>.Instance, Config()), files);
    }

    private static byte[] Pdf(string tag) => Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\n% " + tag + "\n%%EOF");

    [Fact]
    public async Task token_ปลอมหรือข้ามบริษัท_ไม่เปิดเผยอะไร_และไม่เขียนอะไร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "fake " + Guid.NewGuid().ToString("N")[..6]);
        var (svc, files) = Links(db);
        var issued = await svc.IssueAsync(s.CompanyId, null, s.DocumentId, null, "tester");
        Assert.NotNull(issued.Url);
        var token = TokenOf(issued.Url!);
        // ฐานเก็บเฉพาะ hash
        Assert.Equal(WhtCertUploadLinkPolicy.TokenHash(token),
            (await db.WhtCertUploadLinks.AsNoTracking().SingleAsync(l => l.Id == issued.Id)).TokenHash);

        Assert.Null(await svc.PublicViewAsync(s.CompanyId, WhtCertUploadLinkPolicy.NewToken()));
        Assert.Null(await svc.PublicViewAsync(Guid.NewGuid(), token));                       // token บริษัท A กับ URL บริษัท B
        Assert.Null(await svc.PublicViewAsync(s.CompanyId, WhtCertUploadLinkPolicy.TokenHash(token)));   // ถือ hash ที่รั่ว ≠ ถือลิงก์
        Assert.Null(await svc.PublicUploadAsync(Guid.NewGuid(), token, Pdf("x"), null, null));
        Assert.Equal(0, files.Uploads);
        Assert.False(await db.WhtCertIntakes.AnyAsync(i => i.CompanyId == s.CompanyId));

        // ทิศตรงข้าม: token จริงเห็นข้อมูลออกใบในนามเรา + เอกสารรอใบของลูกค้ารายนี้
        var view = (await svc.PublicViewAsync(s.CompanyId, token))!;
        Assert.True(view.Usable);
        Assert.Equal(Ours, view.CompanyTaxId);
        Assert.Equal("สำนักงานใหญ่", view.CompanyBranchLabel);
        var item = Assert.Single(view.Items);
        Assert.Equal(s.DocumentId, item.DocumentId);
        Assert.Equal(30m, item.Missing);
        Assert.True(item.Preselected);
    }

    [Fact]
    public async Task ไฟล์ไม่ใช่_PDF_หรือรูป_ปฏิเสธก่อนเก็บ_ไฟล์ถูกเข้ากล่องรับแถวเดียว_ส่งซ้ำได้แถวเดิม_ลิงก์ยกเลิกแล้วใช้ไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "upload " + Guid.NewGuid().ToString("N")[..6]);
        var (svc, files) = Links(db);
        var token = TokenOf((await svc.IssueAsync(s.CompanyId, s.ContactId, null, null, "tester")).Url!);

        var exe = Encoding.ASCII.GetBytes("MZ fake exe renamed to .pdf");
        var bad = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.PublicUploadAsync(s.CompanyId, token, exe, null, null));
        Assert.Equal(400, bad.StatusCode);
        Assert.Equal(0, files.Uploads);

        var pdf = Pdf(Guid.NewGuid().ToString("N"));
        var r = (await svc.PublicUploadAsync(s.CompanyId, token, pdf, new[] { s.DocumentId, Guid.NewGuid() }, " ลูกค้า\u0001 "))!;
        Assert.True(r.Received);
        Assert.False(r.Duplicate);
        var intake = await db.WhtCertIntakes.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId);
        Assert.Equal(WhtCertIntakeChannel.UploadLink, intake.Channel);
        Assert.Equal(WhtCertIntakeRecordStatus.AwaitingConfirm, intake.Status);   // OCR ล่ม ⇒ รอผู้ขายตรวจ (ไม่ทิ้งไฟล์)
        Assert.Equal(WhtCertUploadLinkPolicy.Sha256Hex(pdf), intake.FileSha256);
        Assert.Contains("ลิงก์อัปโหลด", intake.SubmittedByName);
        Assert.False(intake.SubmittedByName!.Contains('\u0001'));   // ordinal — DoesNotContain(string) เทียบแบบ culture ⇒ อักขระควบคุมถือว่าพบที่ pos 0
        // ฝ่ายค้าน M2: ใบจากลิงก์ (ไม่ล็อกอิน) ต้องให้คนตรวจเสมอ — ไม่ยืนยันอัตโนมัติ · ไม่มีการจัดสรรที่ยืนยันแล้ว
        Assert.Contains(WhtCertUploadLinkPolicy.AnonymousReviewGate, intake.GateProblems);
        Assert.False(intake.AutoConfirmed);
        Assert.False(await db.WhtCertAllocations.AnyAsync(a => a.CompanyId == s.CompanyId && a.Status == WhtCertAllocationStatus.Confirmed));

        var again = (await svc.PublicUploadAsync(s.CompanyId, token, pdf, null, null))!;
        Assert.True(again.Duplicate);
        Assert.Equal(1, await db.WhtCertIntakes.CountAsync(i => i.CompanyId == s.CompanyId));
        Assert.Equal(1, files.Uploads);   // ไฟล์ซ้ำไม่ถูกเก็บซ้ำ

        var link = await db.WhtCertUploadLinks.AsNoTracking().SingleAsync(l => l.CompanyId == s.CompanyId);
        await svc.RevokeAsync(s.CompanyId, link.Id, "tester");
        var gone = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.PublicUploadAsync(s.CompanyId, token, Pdf("new"), null, null));
        Assert.Equal(410, gone.StatusCode);
        Assert.False((await svc.PublicViewAsync(s.CompanyId, token))!.Usable);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityType == nameof(WhtCertUploadLink)));
    }

    [Fact]
    public async Task เกินเพดาน_ได้_429_ไม่อ่านไฟล์()
    {
        var called = false;
        var links = F4Proxy<IWhtCertUploadLinkService>.Create((_, _) => { called = true; return null; });
        var limiter = F4Proxy<IChatRateLimiter>.Create((_, _) => false);   // เกินเพดาน (TryConsumeAsync = false)
        var ctl = new Accounting.Controllers.WhtCertPublicController(links, limiter)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() },
        };
        var res = await ctl.Upload(Guid.NewGuid(), WhtCertUploadLinkPolicy.NewToken(), null, null, null, default);
        Assert.Equal(429, Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(res.Result).StatusCode);
        Assert.False(called);
    }

    // ─────────── ทวงอัตโนมัติ ───────────

    private static IContactRecipientResolver Recipients(bool available = true, string? reason = null)
        => F4Proxy<IContactRecipientResolver>.Create((m, a) => m == nameof(IContactRecipientResolver.ResolveAsync)
            ? new ContactRecipientRule.RecipientSet((NotifyTopic)a[2]!, new[]
            {
                new ContactRecipientRule.RecipientOption(null, NotifyEndpointKind.Email, "ap@buyer253.co.th", "ap@buyer253.co.th", available,
                    reason, null, false, true),
            })
            : null);

    private static WhtCertReminderService Reminder(AccountingDbContext db, IContactRecipientResolver? r = null)
        => new(db, r ?? Recipients(), NullLogger<WhtCertReminderService>.Instance, Config());

    [Fact]
    public async Task ค้าง_8_วัน_ทวงขั้น_7_หนึ่งฉบับ_รอบซ้ำไม่ส่งซ้ำ_watermark_ขยับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "rem " + Guid.NewGuid().ToString("N")[..6], daysAgo: 8, reminders: true);
        var now = DateTime.UtcNow;
        Assert.Contains(s.CompanyId, await Reminder(db).DueCompaniesAsync(now));
        var r = await Reminder(db).RunCompanyAsync(s.CompanyId, now);
        Assert.Equal(1, r.Queued);
        var q = await db.EmailQueues.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId);
        Assert.Equal(WhtCertReminderCadence.QueueEntityType, q.EntityType);
        Assert.Equal(s.ContactId, q.EntityId);
        Assert.Equal("ap@buyer253.co.th", q.ToEmail);
        Assert.False(q.AttachPdf);
        Assert.Contains(WhtCertReminderCadence.UploadLinkPlaceholder, q.Body);   // คิวไม่เก็บ token
        Assert.DoesNotContain("wht-upload.html?", q.Body);
        var log = await db.WhtCertReminderLogs.AsNoTracking().SingleAsync(l => l.CompanyId == s.CompanyId);
        Assert.Equal((7, "Queued", q.Id), (log.Step, log.Outcome, log.EmailQueueId!.Value));

        Assert.DoesNotContain(s.CompanyId, await Reminder(db).DueCompaniesAsync(now.AddMinutes(1)));   // watermark วันนี้
        Assert.Equal(0, (await Reminder(db).RunCompanyAsync(s.CompanyId, now.AddMinutes(2))).Queued);   // ขั้นเดิมไม่ส่งซ้ำ
        Assert.Equal(1, await db.EmailQueues.CountAsync(x => x.CompanyId == s.CompanyId));

        // ตอนส่ง: ยังค้าง ⇒ ส่งได้ · ได้ใบครบแล้ว ⇒ ข้ามพร้อมเหตุ
        Assert.Null(await WhtCertReminderService.SendTimeSkipReasonAsync(db, Recipients(), q, default));
        // ฝ่ายค้าน M3: ลิงก์ของฉบับนี้สร้างพร้อมแถวคิว · URL ตอนส่งคำนวณซ้ำได้ (ลองใหม่ = URL เดิม) · ไม่เขียนฐาน
        var linkRow = await db.WhtCertUploadLinks.AsNoTracking().SingleAsync(l => l.CompanyId == s.CompanyId && l.EmailQueueId == q.Id);
        Assert.Equal(s.ContactId, linkRow.ContactId);
        var u1 = await WhtCertUploadLinkService.ReminderUrlAsync(db, Config(), s.CompanyId, q.Id, default);
        var u2 = await WhtCertUploadLinkService.ReminderUrlAsync(db, Config(), s.CompanyId, q.Id, default);
        Assert.NotNull(u1.Url);
        Assert.Equal(u1.Url, u2.Url);
        Assert.Equal(linkRow.TokenHash, WhtCertUploadLinkPolicy.TokenHash(TokenOf(u1.Url!)));
        Assert.Equal(1, await db.WhtCertUploadLinks.CountAsync(l => l.CompanyId == s.CompanyId));
        Assert.Null((await WhtCertUploadLinkService.ReminderUrlAsync(db, Config(), s.CompanyId, Guid.NewGuid(), default)).Url);   // ไม่มีแถวของคิวนี้ ⇒ ไม่ส่ง
        // ฝ่ายค้าน L2: เลิกรับเรื่อง 50 ทวิ หลังเข้าคิว ⇒ ข้าม
        Assert.NotNull(await WhtCertReminderService.SendTimeSkipReasonAsync(db, Recipients(available: false, reason: "ยกเลิกรับเรื่องนี้"), q, default));
        await db.WhtCreditsReceived.Where(w => w.Id == s.CreditId)
            .ExecuteUpdateAsync(x => x.SetProperty(w => w.Status, WhtCreditStatus.Received).SetProperty(w => w.ReceivedAmount, 30m));
        Assert.NotNull(await WhtCertReminderService.SendTimeSkipReasonAsync(db, Recipients(), q, default));
    }

    [Fact]
    public async Task ผู้ติดต่อไม่ส่งอัตโนมัติ_บันทึกข้ามพร้อมเหตุครั้งเดียว_ไม่เข้าคิว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "optout " + Guid.NewGuid().ToString("N")[..6], daysAgo: 8, reminders: true, autoSendOff: true);
        var r = await Reminder(db).RunCompanyAsync(s.CompanyId, DateTime.UtcNow);
        Assert.Equal((0, 1), (r.Queued, r.SkippedOptOut));
        Assert.False(await db.EmailQueues.AnyAsync(x => x.CompanyId == s.CompanyId));
        var log = await db.WhtCertReminderLogs.AsNoTracking().SingleAsync(l => l.CompanyId == s.CompanyId);
        Assert.Equal("Skipped", log.Outcome);
        Assert.Equal(AutopilotSettingsResolver.AutoSendDisabledReason, log.Reason);
        Assert.Equal(0, (await Reminder(db).RunCompanyAsync(s.CompanyId, DateTime.UtcNow.AddMinutes(1))).SkippedOptOut);   // ไม่บันทึกซ้ำทุกวัน

        // ฝ่ายค้าน L2: ผู้ติดต่อถูกลบ (soft delete) หลังเข้าคิว ⇒ ตอนส่งข้าม
        await db.Contacts.IgnoreQueryFilters().Where(c => c.Id == s.ContactId).ExecuteUpdateAsync(x => x.SetProperty(c => c.IsDeleted, true));
        Assert.Equal(ContactRecipientRule.ContactDeletedReason, await WhtCertReminderService.SendTimeSkipReasonAsync(db, Recipients(),
            new EmailQueue { CompanyId = s.CompanyId, EntityType = WhtCertReminderCadence.QueueEntityType, EntityId = s.ContactId, ToEmail = "ap@buyer253.co.th" }, default));

        // ยกเลิกรับเรื่อง 50 ทวิ ที่ช่องทางของผู้ติดต่อ (ตัวหาผู้รับเดียว) ⇒ ข้ามเช่นกัน
        var s2 = await SeedAsync(db, "optout2 " + Guid.NewGuid().ToString("N")[..6], daysAgo: 8, reminders: true);
        var r2 = await Reminder(db, Recipients(available: false, reason: "ยกเลิกรับเรื่องนี้")).RunCompanyAsync(s2.CompanyId, DateTime.UtcNow);
        Assert.Equal((0, 1), (r2.Queued, r2.SkippedOptOut));
    }

    [Fact]
    public async Task ปิดค่าตั้ง_ยังไม่ถึงวันที่_7_หรือได้ใบแล้ว_ไม่ทวง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var off = await SeedAsync(db, "off " + Guid.NewGuid().ToString("N")[..6], daysAgo: 8, reminders: false);
        Assert.Equal(0, (await Reminder(db).RunCompanyAsync(off.CompanyId, DateTime.UtcNow)).Queued);
        Assert.DoesNotContain(off.CompanyId, await Reminder(db).DueCompaniesAsync(DateTime.UtcNow));
        var early = await SeedAsync(db, "early " + Guid.NewGuid().ToString("N")[..6], daysAgo: 5, reminders: true);
        Assert.Equal(0, (await Reminder(db).RunCompanyAsync(early.CompanyId, DateTime.UtcNow)).Queued);
        var got = await SeedAsync(db, "got " + Guid.NewGuid().ToString("N")[..6], daysAgo: 30, reminders: true);
        await db.WhtCreditsReceived.Where(w => w.Id == got.CreditId)
            .ExecuteUpdateAsync(x => x.SetProperty(w => w.Status, WhtCreditStatus.Received).SetProperty(w => w.ReceivedAmount, 30m));
        Assert.Equal(0, (await Reminder(db).RunCompanyAsync(got.CompanyId, DateTime.UtcNow)).Queued);
        Assert.False(await db.EmailQueues.AnyAsync(x => x.CompanyId == off.CompanyId || x.CompanyId == early.CompanyId || x.CompanyId == got.CompanyId));
    }

    [Fact]
    public async Task JobLock_สองเครื่องพร้อมกัน_ได้รันเครื่องเดียว()
    {
        using var db1 = DbTestDatabase.TryCreateContext();
        if (db1 == null) { _out.WriteLine(Skip); return; }
        using var db2 = DbTestDatabase.TryCreateContext()!;
        bool? second = null;
        var first = await JobLock.RunExclusiveAsync(db1, AdvisoryLockKey.BackgroundJob, "WhtCertReminderJob", async () =>
        {
            second = await JobLock.RunExclusiveAsync(db2, AdvisoryLockKey.BackgroundJob, "WhtCertReminderJob", () => Task.CompletedTask);
        });
        Assert.True(first);
        Assert.False(second);
        Assert.True(await JobLock.RunExclusiveAsync(db2, AdvisoryLockKey.BackgroundJob, "WhtCertReminderJob", () => Task.CompletedTask));
    }

    // ─────────── ไม่หัก ณ ที่จ่าย ───────────

    private static IPaymentProvider Provider(string code) => F4Proxy<IPaymentProvider>.Create((m, _) => m switch
    {
        "get_ProviderCode" => code,
        "get_Capabilities" => new PaymentCapabilities(new HashSet<PaymentMethodKind> { PaymentMethodKind.PromptPay, PaymentMethodKind.ManualSlip }, false, false, true),
        _ => null,
    });

    private static readonly SecretProtector Secrets = new("wht-253-db");

    private static async Task<(DocumentPayLinkService Links, Guid CompanyId, Guid ContactId, string Token)> PayLinkSeedAsync(AccountingDbContext db,
        ContactType type, string? taxId)
    {
        var company = new Company { Name = "บริษัท 253 nowht " + Guid.NewGuid().ToString("N")[..6], TaxId = Ours };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        var contact = new Contact { CompanyId = company.Id, Name = "ผู้ซื้อ 253", IsCustomer = true, ContactType = type, TaxId = taxId, Email = "b@b253.co.th" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var inv = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Invoice, DocumentNumber = "INV253N-" + Random.Shared.Next(100000, 999999),
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date.AddDays(-1), SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_040m,
            WithholdingTaxAmount = 30m, PaidAmount = 0m, BalanceDue = 1_040m, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        const string code = "gw253";
        db.PaymentProviderConfigs.Add(new PaymentProviderConfig { CompanyId = company.Id, ProviderCode = code, IsActive = true, Mode = PaymentProviderMode.Test });
        await db.SaveChangesAsync();
        var links = new DocumentPayLinkService(db, new[] { Provider(code) }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance);
        var link = await links.EnsureLinkAsync(company.Id, inv.Id, new[] { "PromptPay" });
        Assert.True(link.Ok, link.Refusal);
        return (links, company.Id, contact.Id, TokenOf(link.Url!));
    }

    [Fact]
    public async Task นิติบุคคลขอไม่หัก_ยังไม่เปิด_ไม่มีตัวเลือก_ปฏิเสธไม่มีแถวหลักฐาน_ยอดสุทธิเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (links, cid, _, token) = await PayLinkSeedAsync(db, ContactType.JuristicPerson, Payer);
        var view = (await links.PublicViewAsync(cid, token))!;
        // ฝ่ายค้าน H1 (253-WP10-8): ไม่เสนอ "ไม่หัก" — เส้นลงบัญชีของเงินเต็มยอดยังไม่มี
        Assert.False(view.NoWhtOffered);
        Assert.Null(view.NoWhtNotice);
        Assert.Equal(1_040m, view.Target.AmountDue);   // ทิศตรงข้าม: เส้นยอดสุทธิเดิมไม่ขยับ
        var r = await links.AcceptNoWhtAsync(cid, token, new NoWhtAcceptRequest(true, NoWhtChoicePolicy.NoticeVersion, "คุณสมชาย", "203.0.113.9"));
        Assert.Equal(NoWhtChoicePolicy.NotAvailableMessage, r.Refusal);
        Assert.Null(r.AcceptanceId);
        Assert.False(await db.NoWhtAcceptances.AnyAsync(a => a.CompanyId == cid));   // L1: ไม่มีแถวกำพร้า
    }

    [Fact]
    public async Task StartDocument_เลือกไม่หัก_ปฏิเสธก่อนสร้างรายการชำระ()
    {
        var calls = 0;
        var intents = F4Proxy<IPaymentIntentService>.Create((_, _) => { calls++; return null; });
        var docLinks = F4Proxy<IDocumentPayLinkService>.Create((_, _) => { calls++; return null; });
        var ctl = new Accounting.Controllers.PublicPaymentController(intents, F4Proxy<IPublicPaymentResolver>.Create((_, _) => null),
            Array.Empty<IPaymentProvider>(), null!, NullLogger<Accounting.Controllers.PublicPaymentController>.Instance,
            F4Proxy<ILodgingService>.Create((_, _) => null), docLinks);
        var res = await ctl.StartDocument(Guid.NewGuid(), WhtCertUploadLinkPolicy.NewToken(),
            new Accounting.Controllers.PublicPaymentController.StartRequest(PaymentMethodKind.PromptPay, NoWithholding: true, NoWhtAccepted: true,
                NoWhtNoticeVersion: NoWhtChoicePolicy.NoticeVersion));
        var bad = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(res.Result);
        Assert.Contains("ยอดสุทธิ", ((Accounting.Models.DTOs.ApiResponse<Accounting.Controllers.PublicPaymentController.PublicIntentResponse>)bad.Value!).Message);
        Assert.Equal(0, calls);   // ไม่แตะรายการชำระ/ลิงก์ใด ๆ — ไม่มีเงินเคลื่อน
    }

    [Fact]
    public async Task บุคคลธรรมดาไม่เห็นตัวเลือกไม่หัก_ส่งมาก็ไม่รับ_ไม่มีแถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (links, cid, _, token) = await PayLinkSeedAsync(db, ContactType.Individual, "1101700207030");
        var view = (await links.PublicViewAsync(cid, token))!;
        Assert.False(view.NoWhtOffered);
        Assert.Null(view.NoWhtNotice);
        var r = await links.AcceptNoWhtAsync(cid, token, new NoWhtAcceptRequest(true, NoWhtChoicePolicy.NoticeVersion, null, null));
        Assert.NotNull(r.Refusal);
        Assert.Null(r.AcceptanceId);
        Assert.False(await db.NoWhtAcceptances.AnyAsync(a => a.CompanyId == cid));
    }
}
