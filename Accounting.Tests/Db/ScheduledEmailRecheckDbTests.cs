using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 2026-10-14 ทีม F4 (PAYMENT_CHANNEL_AUDIT ส่วนที่ 2 · ID#12) — คิวอีเมลตั้งเวลาของเอกสารบน PostgreSQL จริง (คำสั่ง claim SKIP LOCKED จริง)
/// <para>(1) ใบที่ถูกยกเลิกหลังเข้าคิว ⇒ ข้าม (Skipped + เหตุผล) · ทิศตรงข้าม: ใบปกติในรอบเดียวกันถูกส่ง (Sent)</para>
/// <para>(2) คำเตือนชำระของใบที่ชำระครบแล้ว ⇒ ข้าม · ใบที่ยังค้าง ⇒ ส่งด้วยยอดค้างชำระ ณ เวลาส่ง (ไม่ใช่ยอดตอนเข้าคิว)</para>
/// <para>(3) สร้าง PDF แนบไม่สำเร็จ ⇒ ไม่ส่งอีเมลเปล่า · ไม่ประทับ Sent · กลับไปรอลองใหม่พร้อมเหตุผล</para>
/// <para>อยู่คลาสเดียวกัน (รันทีละเทสต์) เพราะตัว claim หยิบคิวที่ถึงเวลาทั้งฐาน — เทสต์อื่นของคลาสนี้ไม่ทิ้งแถวที่ถึงเวลาค้างไว้</para>
/// </summary>
[Trait("Category", "Db")]
public class ScheduledEmailRecheckDbTests
{
    private readonly ITestOutputHelper _out;
    public ScheduledEmailRecheckDbTests(ITestOutputHelper output) => _out = output;

    private sealed class Outbox
    {
        public List<EmailMessage> Sent { get; } = new();
    }

    private static IEmailSenderFactory Senders(Outbox box)
    {
        var sender = F4Proxy<IEmailSender>.Create((name, args) =>
        {
            if (name == nameof(IEmailSender.SendAsync)) { box.Sent.Add((EmailMessage)args[0]!); return EmailSendResult.Ok("test"); }
            return null;
        });
        // รอบ 244 (แก้ CI ข้อ 6–8): รอบ 241 ฝ่ายค้าน B2 ให้คิวเลือกผู้ส่งผ่าน DocumentEmailService.ResolveOnBehalfSenderAsync ซึ่งเรียก
        // PlatformFromAddress() (คืน string) — ตัวปลอมเดิมคืน "ผู้ส่ง" ให้ทุกเมธอด ⇒ proxy ถูกแปลงเป็น string ไม่ได้ (InvalidCast) ⇒ คิวล้มทุกฉบับ ·
        // ตัวปลอมต้องตอบตามชนิดของเมธอด (แบบเดียวกับเทสต์รอบ 240/241) · โค้ดจริงไม่ได้ cast ชนิด
        return F4Proxy<IEmailSenderFactory>.Create((m, _) => m == nameof(IEmailSenderFactory.PlatformFromAddress) ? "no-reply@nextacc.net" : sender);
    }

    private static IPdfGenerationService Pdf(bool fail) => F4Proxy<IPdfGenerationService>.Create((name, _) =>
    {
        if (name == nameof(IPdfGenerationService.GenerateDocumentPdfAsync) && fail)
            throw new InvalidOperationException("เทมเพลตเสีย (ทดสอบ)");
        return null;
    });

    private static EmailScheduleService Svc(Accounting.Data.AccountingDbContext db, Outbox box, bool pdfFails = false) =>
        new(db, Senders(box), Pdf(pdfFails), NullLogger<EmailScheduleService>.Instance);

    private sealed record Seed(Guid CompanyId, Guid ContactId);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบอีเมลตั้งเวลา", TaxId = "0105556000021" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้าอีเมล", Email = "customer@test.local", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id);
    }

    private static async Task<Document> DocAsync(Accounting.Data.AccountingDbContext db, Seed s, DocumentStatus status, decimal balanceDue)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Invoice,
            DocumentNumber = "INV-" + Guid.NewGuid().ToString("N")[..8], Status = status, DocumentDate = DateTime.UtcNow.Date,
            DueDate = DateTime.UtcNow.Date.AddDays(-3), SubTotal = 1_000m, TotalAmount = 1_000m, BalanceDue = balanceDue, Currency = "THB",
        };
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    private static async Task<EmailQueue> QueueAsync(Accounting.Data.AccountingDbContext db, Seed s, Document d, string trigger,
        bool attachPdf = false, Guid? ruleId = null)
    {
        var q = new EmailQueue
        {
            CompanyId = s.CompanyId, RuleId = ruleId, EntityType = "Document", EntityId = d.Id, ToEmail = "customer@test.local",
            Subject = "หัวเรื่องตอนเข้าคิว", Body = "ยอดค้างชำระ 1,000.00 บาท (ค่าตอนเข้าคิว)", AttachPdf = attachPdf,
            ScheduledFor = DateTime.UtcNow.AddMinutes(-1), Status = "Pending", IdempotencyKey = $"doc:{d.Id}:{trigger}:test-{Guid.NewGuid():N}",
        };
        db.EmailQueues.Add(q);
        await db.SaveChangesAsync();
        return q;
    }

    private static async Task<EmailQueue> ReloadAsync(Guid id)
    {
        using var check = DbTestDatabase.TryCreateContext()!;
        return await check.EmailQueues.AsNoTracking().SingleAsync(q => q.Id == id);
    }

    [Fact]
    public async Task ใบที่ถูกยกเลิกหลังเข้าคิว_ข้ามพร้อมเหตุผล_ใบปกติถูกส่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var voided = await DocAsync(db, s, DocumentStatus.Voided, 1_000m);
        var normal = await DocAsync(db, s, DocumentStatus.Approved, 1_000m);
        var qVoided = await QueueAsync(db, s, voided, "DocumentApproved");
        var qNormal = await QueueAsync(db, s, normal, "DocumentApproved");
        var box = new Outbox();

        await Svc(db, box).ProcessPendingQueueAsync();

        var v = await ReloadAsync(qVoided.Id);
        Assert.Equal(ScheduledEmailSendCheck.SkippedStatus, v.Status);
        Assert.Contains("ยกเลิก", v.ErrorMessage ?? "");
        Assert.Null(v.SentAt);
        var n = await ReloadAsync(qNormal.Id);
        Assert.Equal("Sent", n.Status);
        Assert.Single(box.Sent.Where(m => m.Subject == "หัวเรื่องตอนเข้าคิว"));   // ใบปกติ 1 ฉบับ · ใบยกเลิก 0 ฉบับ
    }

    [Fact]
    public async Task คำเตือนชำระ_ใบชำระครบแล้วข้าม_ใบที่ยังค้างส่งด้วยยอดปัจจุบัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var rule = new EmailScheduleRule { CompanyId = s.CompanyId, Trigger = "DocumentOverdue", IsActive = true, OffsetDays = 3, SendAtHour = 9 };
        db.EmailScheduleRules.Add(rule);
        await db.SaveChangesAsync();
        var paid = await DocAsync(db, s, DocumentStatus.Paid, 0m);
        var partly = await DocAsync(db, s, DocumentStatus.PartiallyPaid, 400m);   // เข้าคิวตอนค้าง 1,000 · ตอนส่งเหลือ 400
        var qPaid = await QueueAsync(db, s, paid, "DocumentOverdue", ruleId: rule.Id);
        var qPartly = await QueueAsync(db, s, partly, "DocumentOverdue", ruleId: rule.Id);
        var box = new Outbox();

        await Svc(db, box).ProcessPendingQueueAsync();

        var p = await ReloadAsync(qPaid.Id);
        Assert.Equal(ScheduledEmailSendCheck.SkippedStatus, p.Status);
        Assert.Contains("ชำระครบ", p.ErrorMessage ?? "");
        var r = await ReloadAsync(qPartly.Id);
        Assert.Equal("Sent", r.Status);
        Assert.Contains("400.00", r.Body);
        Assert.DoesNotContain("ค่าตอนเข้าคิว", r.Body);
    }

    [Fact]
    public async Task สร้างPDFแนบไม่สำเร็จ_ไม่ส่งอีเมลเปล่า_ไม่ประทับSent_รอลองใหม่พร้อมเหตุผล()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var doc = await DocAsync(db, s, DocumentStatus.Approved, 1_000m);
        var q = await QueueAsync(db, s, doc, "DocumentApproved", attachPdf: true);
        var box = new Outbox();

        await Svc(db, box, pdfFails: true).ProcessPendingQueueAsync();

        var after = await ReloadAsync(q.Id);
        Assert.NotEqual("Sent", after.Status);
        Assert.Equal("Pending", after.Status);
        Assert.Equal(1, after.RetryCount);
        Assert.True(after.ScheduledFor > DateTime.UtcNow);                      // เลื่อนไปลองใหม่ ไม่วนทันที
        Assert.Contains("PDF", after.ErrorMessage ?? "");
        Assert.Null(after.SentAt);
        Assert.Empty(box.Sent.Where(m => m.To.Contains("customer@test.local") && m.Subject == "หัวเรื่องตอนเข้าคิว"));
    }

    // ───── รอบ 258 (B-H2): แถวค้าง "กำลังส่ง" — อยู่คลาสนี้เพราะใช้ตัว claim/ตัวกู้ที่ทำทั้งฐาน (รันทีละเทสต์) ─────

    /// <param name="started">true = ตัวส่งเริ่มส่งแถวนี้แล้ว (SendStartedAt มีค่า) · false = จองแล้วแต่ยังไม่ถึงคิว (รอบ 258 ฝ่ายค้าน R-M3)</param>
    private static async Task MarkSendingAsync(Accounting.Data.AccountingDbContext db, Guid queueId, int minutesAgo, bool started = true)
    {
        var at = DateTime.UtcNow.AddMinutes(-minutesAgo);
        await db.EmailQueues.Where(q => q.Id == queueId)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, "Sending").SetProperty(q => q.UpdatedAt, (DateTime?)at)
                .SetProperty(q => q.SendStartedAt, started ? (DateTime?)at : null).SetProperty(q => q.ClaimToken, (Guid?)Guid.NewGuid()));
    }

    [Fact]
    public async Task แบตช์ตายกลางทาง_แถวที่ยังไม่เคยเริ่มส่งกลับเข้าคิวแล้วถูกส่งเอง_แถวที่เริ่มส่งแล้วล้มรอคน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var doc = await DocAsync(db, s, DocumentStatus.Approved, 1_000m);
        var started = await QueueAsync(db, s, doc, "DocumentApproved");
        var neverA = await QueueAsync(db, s, doc, "DocumentApproved");
        var neverB = await QueueAsync(db, s, doc, "DocumentApproved");
        var liveNever = await QueueAsync(db, s, doc, "DocumentApproved");
        await MarkSendingAsync(db, started.Id, 40, started: true);
        await MarkSendingAsync(db, neverA.Id, 40, started: false);
        await MarkSendingAsync(db, neverB.Id, 40, started: false);
        await MarkSendingAsync(db, liveNever.Id, 3, started: false);   // เครื่องอื่นเพิ่งจอง ยังไม่ถึงคิว — ห้ามแตะ
        db.ChangeTracker.Clear();
        var box = new Outbox();

        await Svc(db, box).ProcessPendingQueueAsync();

        // เริ่มส่งแล้วตาย ⇒ ไม่รู้ว่าถึงหรือไม่ ⇒ Failed รอคน (ไม่ส่งซ้ำเอง)
        var startedAfter = await ReloadAsync(started.Id);
        Assert.Equal("Failed", startedAfter.Status);
        Assert.Equal(EmailQueueStaleSending.StaleReason, startedAfter.ErrorMessage);
        // ยังไม่เคยเริ่มส่ง ⇒ กลับเข้าคิวและถูกส่งในรอบเดียวกัน ไม่ต้องให้คนกดทีละแถว
        foreach (var id in new[] { neverA.Id, neverB.Id })
        {
            var row = await ReloadAsync(id);
            Assert.Equal("Sent", row.Status);
            Assert.NotNull(row.SendStartedAt);
        }
        // ทิศตรงข้าม: แถวที่เครื่องอื่นเพิ่งจอง (ยังไม่ค้าง) ไม่ถูกแตะ
        var live = await ReloadAsync(liveNever.Id);
        Assert.Equal("Sending", live.Status);
        Assert.Null(live.SendStartedAt);
        await db.EmailQueues.Where(q => q.Id == liveNever.Id).ExecuteUpdateAsync(x => x.SetProperty(q => q.Status, "Cancelled"));
    }

    [Fact]
    public async Task แถวค้างกำลังส่งเกิน30นาที_ถูกประทับล้มพร้อมเหตุ_ไม่ถูกส่งซ้ำเอง_แถวที่กำลังส่งจริงไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var doc = await DocAsync(db, s, DocumentStatus.Approved, 1_000m);
        var stale = await QueueAsync(db, s, doc, "DocumentApproved");
        var live = await QueueAsync(db, s, doc, "DocumentApproved");
        await MarkSendingAsync(db, stale.Id, minutesAgo: 31);
        await MarkSendingAsync(db, live.Id, minutesAgo: 5);
        db.ChangeTracker.Clear();
        var box = new Outbox();

        await Svc(db, box).ProcessPendingQueueAsync();

        var staleAfter = await ReloadAsync(stale.Id);
        Assert.Equal("Failed", staleAfter.Status);
        Assert.Equal(EmailQueueStaleSending.StaleReason, staleAfter.ErrorMessage);
        Assert.Null(staleAfter.SentAt);
        // ทิศตรงข้าม: แถวที่ตัวส่งอื่นกำลังส่งอยู่จริงไม่ถูกแตะ
        Assert.Equal("Sending", (await ReloadAsync(live.Id)).Status);
        // รอบถัดไปไม่หยิบแถวที่ล้มกลับไปส่งเอง
        await Svc(db, box).ProcessPendingQueueAsync();
        Assert.Equal("Failed", (await ReloadAsync(stale.Id)).Status);

        // คนกดลองใหม่ ⇒ กลับเข้าคิว แล้วรอบถัดไปส่งจริง
        db.ChangeTracker.Clear();
        await Svc(db, box).RetryFailedAsync(s.CompanyId, stale.Id);
        Assert.Equal("Pending", (await ReloadAsync(stale.Id)).Status);
        db.ChangeTracker.Clear();
        await Svc(db, box).ProcessPendingQueueAsync();
        Assert.Equal("Sent", (await ReloadAsync(stale.Id)).Status);
        // เก็บกวาด: แถวที่ "กำลังส่งจริง" ของเทสต์นี้ไม่ทิ้งไว้ให้เทสต์อื่น
        await db.EmailQueues.Where(q => q.Id == live.Id).ExecuteUpdateAsync(x => x.SetProperty(q => q.Status, "Cancelled"));
    }

    [Fact]
    public async Task แถวค้างกำลังส่ง_ลองใหม่และยกเลิกได้ทันที_แถวที่กำลังส่งจริงถูกปฏิเสธ_และบริษัทอื่นแตะไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var other = await SeedAsync(db);
        var doc = await DocAsync(db, s, DocumentStatus.Approved, 1_000m);
        var staleRetry = await QueueAsync(db, s, doc, "DocumentApproved");
        var staleCancel = await QueueAsync(db, s, doc, "DocumentApproved");
        var live = await QueueAsync(db, s, doc, "DocumentApproved");
        await MarkSendingAsync(db, staleRetry.Id, 45);
        await MarkSendingAsync(db, staleCancel.Id, 45);
        await MarkSendingAsync(db, live.Id, 2);
        db.ChangeTracker.Clear();
        var svc = Svc(db, new Outbox());

        // tenant: บริษัทอื่นสั่งแถวนี้ไม่ได้
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.RetryFailedAsync(other.CompanyId, staleRetry.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.CancelPendingAsync(other.CompanyId, staleCancel.Id));

        // แถวที่กำลังส่งจริง: ลองใหม่/ยกเลิกไม่ได้ (กันส่งซ้ำ/ทับแถวที่ตัวส่งถืออยู่)
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RetryFailedAsync(s.CompanyId, live.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.CancelPendingAsync(s.CompanyId, live.Id));
        Assert.Equal("Sending", (await ReloadAsync(live.Id)).Status);

        // แถวค้าง: ลองใหม่ ⇒ Pending · ยกเลิก ⇒ Cancelled
        db.ChangeTracker.Clear();
        await svc.RetryFailedAsync(s.CompanyId, staleRetry.Id);
        await svc.CancelPendingAsync(s.CompanyId, staleCancel.Id);
        Assert.Equal("Pending", (await ReloadAsync(staleRetry.Id)).Status);
        Assert.Equal("Cancelled", (await ReloadAsync(staleCancel.Id)).Status);
        // เก็บกวาด (แถว Pending/Sending ของเทสต์นี้ไม่ทิ้งไว้ให้ตัว claim ของเทสต์อื่น)
        await db.EmailQueues.Where(q => q.Id == live.Id || q.Id == staleRetry.Id).ExecuteUpdateAsync(x => x.SetProperty(q => q.Status, "Cancelled"));
    }
}
