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
        return F4Proxy<IEmailSenderFactory>.Create((_, _) => sender);
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
}
