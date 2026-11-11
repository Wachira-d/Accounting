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
/// รอบ 259 (คำตัดสิน 259-2-7 · ที่มา CI db-test `LineQueueRound254DbTests` ล้ม 23505 บน <c>IX_LineQueues_Company_Idempotency</c> กุญแจ <c>…:due--3:line:none</c>) —
/// รอบสแกนใกล้ครบ/เกินกำหนดบันทึกแถวคิวแบบ idempotent: (ก) สองกฎ trigger/offset เดียวกัน (รวมกฎที่ย้อนเก็บวันที่พลาด) ได้กุญแจเดียวกันในรอบเดียว ⇒
/// แถวเดียว ไม่ล้มทั้งรอบ · (ข) ผู้สแกนอีกตัวบันทึกกุญแจไปก่อน (หลายเครื่อง) ⇒ ถอดแถวซ้ำ บันทึกแถวอื่นของรอบได้ · ทิศตรงข้าม: ชน unique อื่น ⇒ ยังโยน
/// </summary>
[Trait("Category", "Db")]
public class QueueRowSaveRound259DbTests
{
    private readonly ITestOutputHelper _out;
    public QueueRowSaveRound259DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("db-test-key-259-queue-save");

    private static INextAccLineConfig Central() => F4Proxy<INextAccLineConfig>.Create((m, _) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
        ? NextAccLineConfigRule.PlatformGroupIds("Cadmin259", null)
        : new NextAccLineSettings("T", "S", "@nextacc", null, "Cadmin259", NextAccLineConfigRule.SourceAdmin));

    private sealed class FakeSender : IEmailSender
    {
        public EmailProvider Provider => EmailProvider.Smtp;
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default) => Task.FromResult(EmailSendResult.Ok("mid-259"));
    }

    private static EmailScheduleService Sched(AccountingDbContext db)
        => new(db, F4Proxy<IEmailSenderFactory>.Create((m, _) => m == nameof(IEmailSenderFactory.PlatformFromAddress) ? "no-reply@nextacc.net" : new FakeSender()),
            F4Proxy<IPdfGenerationService>.Create((_, _) => null), NullLogger<EmailScheduleService>.Instance,
            payLinks: null, config: new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build(),
            recipients: new ContactRecipientResolver(db, Secrets, Central()));

    private static DateTime TodayBkk() => DateTime.UtcNow.AddHours(7).Date;

    /// <summary>บริษัทที่ไม่มี LINE OA (แผน LINE = "ไม่มีปลายทาง" ⇒ แถวข้าม line:none) + ใบครบกำหนดอีก 3 วัน + สองกฎ "ใกล้ครบกำหนด −3 วัน" ติ๊ก LINE ·
    /// กฎที่สองมีลายน้ำเมื่อ 2 วันก่อน (รอบนี้ย้อนเก็บวันที่พลาดด้วย)</summary>
    private static async Task<(Guid CompanyId, Guid InvoiceId)> SeedAsync(AccountingDbContext db, string tag)
    {
        var company = new Company { Name = "บริษัท Q259 " + tag, TaxId = "", Email = "office@seller259.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id, LineEnabled = false });
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้า Q259 " + tag, IsCustomer = true, TaxId = "", Email = "ap@buyer259.co.th" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var inv = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Invoice,
            DocumentNumber = "INVQ259-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            DocumentDate = TodayBkk().AddDays(-27), DueDate = TodayBkk().AddDays(3),
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        foreach (var watermark in new DateTime?[] { null, TodayBkk().AddDays(-2) })
            db.EmailScheduleRules.Add(new EmailScheduleRule
            {
                CompanyId = company.Id, Trigger = "DocumentDueSoon", OffsetDays = -3, SendAtHour = 0, IsActive = true,
                DocumentType = DocumentType.Invoice.ToString(), ChannelEmail = false, ChannelLineUser = true, LineFallbackToEmail = true,
                LastDayScanBkk = watermark,
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (company.Id, inv.Id);
    }

    [Fact]
    public async Task สองกฎกุญแจเดียวกันรวมวันย้อนเก็บ_รอบเดียวได้แถวเดียว_ไม่ล้มทั้งรอบ_สแกนซ้ำไม่เพิ่ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, invoiceId) = await SeedAsync(db, Guid.NewGuid().ToString("N")[..6]);

        await Sched(db).ScanDueSoonAndOverdueAsync();   // เดิม: 23505 บน IX_LineQueues_Company_Idempotency
        db.ChangeTracker.Clear();
        await Sched(db).ScanDueSoonAndOverdueAsync();   // สแกนซ้ำ (รวมย้อนเก็บ) ไม่เพิ่มแถว

        var lineRows = await db.LineQueues.AsNoTracking().Where(q => q.CompanyId == cid && q.EntityId == invoiceId).ToListAsync();
        var none = Assert.Single(lineRows);
        Assert.Equal(ContactLineChannel.StatusSkipped, none.Status);
        Assert.EndsWith(":line:none", none.IdempotencyKey);
        // ถอยไปอีเมล (กุญแจเดียวกันของสองกฎ) ⇒ อีเมลฉบับเดียว
        Assert.True(await db.EmailQueues.CountAsync(q => q.CompanyId == cid && q.EntityId == invoiceId && !q.IsDeleted) <= 1);
    }

    [Fact]
    public async Task ผู้สแกนอีกตัวบันทึกกุญแจไปก่อน_ถอดแถวซ้ำแล้วบันทึกแถวอื่นของรอบ_ทิศตรงข้ามชน_unique_อื่นยังโยน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, invoiceId) = await SeedAsync(db, Guid.NewGuid().ToString("N")[..6]);
        var contactId = await db.Documents.AsNoTracking().Where(d => d.Id == invoiceId && d.CompanyId == cid).Select(d => d.ContactId).FirstAsync();
        LineQueue Row(string key) => new()
        {
            CompanyId = cid, EntityType = "Document", EntityId = invoiceId, ContactId = contactId, EndpointKind = "", Topic = "Dunning",
            Trigger = "DocumentDueSoon", ScheduledFor = DateTime.UtcNow, Status = ContactLineChannel.StatusSkipped, RetryKey = Guid.NewGuid(),
            IdempotencyKey = key, BaseKey = key,
        };
        var taken = "q259:" + Guid.NewGuid().ToString("N");
        var fresh = "q259:" + Guid.NewGuid().ToString("N");
        db.LineQueues.Add(Row(taken));   // "อีกเครื่อง" บันทึกไปก่อน
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.LineQueues.Add(Row(taken));
        db.LineQueues.Add(Row(fresh));
        db.LineQueues.Add(Row(fresh));   // ซ้ำกันเองในรอบเดียว
        var dropped = await QueueRowSave.SaveSkippingExistingKeysAsync(db, default);
        Assert.Equal(2, dropped);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.LineQueues.CountAsync(q => q.CompanyId == cid && q.IdempotencyKey == taken));
        Assert.Equal(1, await db.LineQueues.CountAsync(q => q.CompanyId == cid && q.IdempotencyKey == fresh));

        // ทิศตรงข้าม: ชน unique ที่ไม่ใช่กุญแจคิว (บริษัทซ้ำ id) ⇒ ไม่กลืน
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == cid);
        db.Companies.Add(new Company { Id = company.Id, Name = "ซ้ำ", TaxId = "" });
        await Assert.ThrowsAsync<DbUpdateException>(() => QueueRowSave.SaveSkippingExistingKeysAsync(db, default));
    }
}
