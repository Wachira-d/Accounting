using Accounting.Data;
using Accounting.Helpers;
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
/// รอบ 258 (B-M7) บน PostgreSQL จริง (trait Db) — กฎ "ใกล้ครบกำหนด" ที่พลาดวันเป้าหมายเพราะระบบไม่ได้สแกน:
/// <para>(1) ลายน้ำของกฎอยู่ 2 วันก่อน + ใบที่ควรถูกเตือนเมื่อวาน ⇒ สแกนวันนี้เก็บย้อนหลัง 1 แถว · บันทึกบนกฎ · ลายน้ำ = วันนี้ · สแกนซ้ำไม่เพิ่มแถว</para>
/// <para>(2) ทิศตรงข้าม: กฎไม่มีลายน้ำ (กฎใหม่/วันแรกหลังอัปเดต) ⇒ ใบเดียวกันไม่ถูกเข้าคิว (ไม่ย้อนของเก่า) แต่ลายน้ำถูกประทับ ·
/// ใบที่ครบเป้าหมายวันนี้ยังเข้าคิวตามปกติ และไม่มีบันทึกย้อนหลัง</para>
/// <para>(3) แก้ระยะของกฎ ⇒ ลายน้ำถูกล้าง (ไม่ย้อนเก็บตามตารางเก่า)</para>
/// </summary>
[Trait("Category", "Db")]
public class ScheduleCatchUpRound258DbTests
{
    private readonly ITestOutputHelper _out;
    public ScheduleCatchUpRound258DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static readonly SecretProtector Secrets = new("db-test-key-258-catchup");

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();

    private static INextAccLineConfig Central() => F4Proxy<INextAccLineConfig>.Create((m, _) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
        ? Task.FromResult(NextAccLineConfigRule.PlatformGroupIds(null, null))
        : Task.FromResult(new NextAccLineSettings("T", "S", "@nextacc", null, null, NextAccLineConfigRule.SourceAdmin)));

    private static EmailScheduleService Sched(AccountingDbContext db)
    {
        var sender = F4Proxy<IEmailSender>.Create((name, _) => name == nameof(IEmailSender.SendAsync) ? EmailSendResult.Ok("mid-258") : null);
        var factory = F4Proxy<IEmailSenderFactory>.Create((m, _) => m == nameof(IEmailSenderFactory.PlatformFromAddress) ? "no-reply@nextacc.net" : sender);
        return new EmailScheduleService(db, factory, F4Proxy<IPdfGenerationService>.Create((_, _) => null), NullLogger<EmailScheduleService>.Instance,
            payLinks: null, config: Config(), recipients: new ContactRecipientResolver(db, Secrets, Central()));
    }

    private static DateTime TodayBkk() => DateTime.UtcNow.AddHours(7).Date;

    private sealed record Seed(Guid CompanyId, Guid RuleId, Guid MissedInvoiceId, Guid TodayInvoiceId);

    /// <summary>กฎเตือนล่วงหน้า 3 วัน + ใบที่ครบกำหนดอีก 2 วัน (ควรถูกเตือนเมื่อวาน) + ใบที่ครบกำหนดอีก 3 วัน (เป้าหมายของวันนี้)</summary>
    private static async Task<Seed> SeedAsync(AccountingDbContext db, DateTime? watermark)
    {
        var company = new Company { Name = "บริษัททดสอบย้อนเก็บ 258 " + Guid.NewGuid().ToString("N")[..6], TaxId = "", Email = "office@seller258.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้า 258", IsCustomer = true, TaxId = "", Email = "ap@buyer258.co.th" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        Document Inv(int dueInDays)
        {
            var inv = new Document
            {
                CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Invoice,
                DocumentNumber = "INV258-" + Guid.NewGuid().ToString("N")[..8], Status = DocumentStatus.Approved,
                DocumentDate = TodayBkk().AddDays(-20), DueDate = TodayBkk().AddDays(dueInDays),
                SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
            };
            inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
            db.Documents.Add(inv);
            return inv;
        }
        var missed = Inv(2);
        var today = Inv(3);
        var rule = new EmailScheduleRule
        {
            CompanyId = company.Id, Trigger = "DocumentDueSoon", OffsetDays = -3, SendAtHour = 9, IsActive = true,
            DocumentType = DocumentType.Invoice.ToString(), LastDayScanBkk = watermark,
        };
        db.EmailScheduleRules.Add(rule);
        await db.SaveChangesAsync();
        return new Seed(company.Id, rule.Id, missed.Id, today.Id);
    }

    private static async Task<int> RowsAsync(Guid companyId, Guid documentId)
    {
        using var check = DbTestDatabase.TryCreateContext()!;
        return await check.EmailQueues.AsNoTracking().CountAsync(q => q.CompanyId == companyId && q.EntityId == documentId && !q.IsDeleted);
    }

    private static async Task<EmailScheduleRule> RuleAsync(Guid companyId, Guid ruleId)
    {
        using var check = DbTestDatabase.TryCreateContext()!;
        return await check.EmailScheduleRules.AsNoTracking().SingleAsync(r => r.CompanyId == companyId && r.Id == ruleId);
    }

    [Fact]
    public async Task ระบบไม่ได้สแกนเมื่อวาน_ใบที่พลาดถูกเก็บย้อนหลัง_มีบันทึกบนกฎ_สแกนซ้ำไม่เพิ่ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, watermark: TodayBkk().AddDays(-2));
        db.ChangeTracker.Clear();

        await Sched(db).ScanDueSoonAndOverdueAsync();

        Assert.Equal(1, await RowsAsync(s.CompanyId, s.MissedInvoiceId));
        Assert.Equal(1, await RowsAsync(s.CompanyId, s.TodayInvoiceId));
        var rule = await RuleAsync(s.CompanyId, s.RuleId);
        Assert.Equal(TodayBkk(), rule.LastDayScanBkk!.Value.Date);
        Assert.NotNull(rule.LastCatchUpNote);
        Assert.Contains(TodayBkk().AddDays(-1).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture), rule.LastCatchUpNote!, StringComparison.Ordinal);

        // สแกนรอบถัดไปของวันเดียวกัน (ทุกชั่วโมง) ⇒ ไม่เพิ่มแถว ไม่ย้อนอีก
        db.ChangeTracker.Clear();
        await Sched(db).ScanDueSoonAndOverdueAsync();
        Assert.Equal(1, await RowsAsync(s.CompanyId, s.MissedInvoiceId));
        Assert.Equal(1, await RowsAsync(s.CompanyId, s.TodayInvoiceId));
    }

    [Fact]
    public async Task ทิศตรงข้าม_กฎไม่มีลายน้ำ_ไม่ย้อนของเก่า_ใบของวันนี้เข้าคิวตามปกติ_ไม่มีบันทึกย้อนหลัง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, watermark: null);
        db.ChangeTracker.Clear();

        await Sched(db).ScanDueSoonAndOverdueAsync();

        Assert.Equal(0, await RowsAsync(s.CompanyId, s.MissedInvoiceId));
        Assert.Equal(1, await RowsAsync(s.CompanyId, s.TodayInvoiceId));
        var rule = await RuleAsync(s.CompanyId, s.RuleId);
        Assert.Equal(TodayBkk(), rule.LastDayScanBkk!.Value.Date);
        Assert.Null(rule.LastCatchUpNote);
    }

    [Fact]
    public async Task แก้ระยะของกฎ_ลายน้ำถูกล้าง_แก้หัวเรื่องอย่างเดียวคงลายน้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, watermark: TodayBkk().AddDays(-1));
        db.ChangeTracker.Clear();
        var svc = Sched(db);
        var current = await RuleAsync(s.CompanyId, s.RuleId);

        // แก้หัวเรื่องอย่างเดียว ⇒ ลายน้ำคงเดิม
        current.SubjectTemplate = "เตือนใกล้ครบกำหนด {DocumentNumber}";
        await svc.UpsertRuleAsync(s.CompanyId, current, "tester");
        Assert.Equal(TodayBkk().AddDays(-1), (await RuleAsync(s.CompanyId, s.RuleId)).LastDayScanBkk!.Value.Date);

        // เปลี่ยนระยะ ⇒ ล้าง
        db.ChangeTracker.Clear();
        var edited = await RuleAsync(s.CompanyId, s.RuleId);
        edited.OffsetDays = -5;
        await svc.UpsertRuleAsync(s.CompanyId, edited, "tester");
        Assert.Null((await RuleAsync(s.CompanyId, s.RuleId)).LastDayScanBkk);
    }
}
