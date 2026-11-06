using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Recurring;
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
/// รอบ 248 (AUTOPILOT_BILLING_DESIGN §12 เทสต์ยอมรับ) — ผ่านเมธอดจริงบน PostgreSQL:
/// (1) ทดลองรัน/ตรวจความพร้อม <b>ไม่เขียนอะไรเลย</b> (นับแถวก่อน-หลัง = เท่ากัน) และผลอนุมัติจำลองบอก [AR-OVERDUE] ตรงกับรอบจริง ·
/// (2) นโยบายปล่อย AR-OVERDUE ⇒ ใบประจำอนุมัติ + audit <c>APPROVE-UNATTENDED-POLICY</c> ·
/// ทิศตรงข้าม: ไม่ตั้งนโยบาย ⇒ ค้างร่างเหมือนรอบ 247 · ผู้ตั้งออกจากบริษัท ⇒ ไม่ปล่อย · ตั้งนโยบายปล่อย §86/4 ⇒ ปฏิเสธ
/// </summary>
[Trait("Category", "Db")]
public class RecurringAutopilotRound248DbTests
{
    private readonly ITestOutputHelper _out;
    public RecurringAutopilotRound248DbTests(ITestOutputHelper output) => _out = output;

    private static DocumentService Docs(AccountingDbContext db)
        => new(db, new AccountingService(db), RecurringTransactionDbTests.AllowAll<ISubscriptionService>.Create(), null!, null!,
            NullLogger<DocumentService>.Instance, null!,
            // งานฝึก OCR หลังอนุมัติ (TryTrainAsync) ไม่มีตัวกัน null — ส่งตัวจริงแบบเทสต์อนุมัติอื่น (DI จริงไม่เคยเป็น null)
            new Accounting.Services.Implementations.Ocr.VendorIntelligenceService(db, NullLogger<Accounting.Services.Implementations.Ocr.VendorIntelligenceService>.Instance),
            null!);

    private static RecurringTransactionService Recurring(AccountingDbContext db)
        => new(db, Docs(db), new AccountingService(db), NullLogger<RecurringTransactionService>.Instance,
            new ErrorLogService(db, NullLogger<ErrorLogService>.Instance));

    private static RecurringAutopilotService Autopilot(AccountingDbContext db) => new(db, Docs(db));

    private sealed record Seed(Guid CompanyId, Guid ContactId, Guid SetterId, Guid RecurringId);

    /// <summary>บริษัท + ผังพื้นฐาน + ลูกค้านิติบุคคลครบ §86/4 + ใบค้างชำระเกิน 30 วัน (ทำให้เกิด [AR-OVERDUE]) + ผู้ตั้งนโยบาย (สมาชิก) + รายการประจำอนุมัติอัตโนมัติ</summary>
    private static async Task<Seed> SeedAsync(AccountingDbContext db, bool setterIsMember = true)
    {
        var co = new Company { Name = "บริษัททดสอบ Autopilot", TaxId = "0105556000025", IsVatRegistered = true };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        Guid revenueId = Guid.Empty;
        foreach (var (code, name, type) in new[]
                 {
                     ("111", "เงินสด", AccountType.Asset), ("11310", "ลูกหนี้การค้า", AccountType.Asset),
                     ("41000", "รายได้จากการขาย", AccountType.Revenue), ("21911", "ภาษีขาย", AccountType.Liability),
                 })
        {
            var a = new ChartOfAccount { CompanyId = co.Id, AccountCode = code, AccountName = name, AccountType = type, Level = 4, IsActive = true };
            db.ChartOfAccounts.Add(a);
            if (code == "41000") revenueId = a.Id;
        }
        var contact = new Contact
        {
            CompanyId = co.Id, Name = "บริษัท ลูกค้าค้างชำระ จำกัด", TaxId = "0105556000017", BranchCode = "00000", ContactType = ContactType.JuristicPerson,
            IsCustomer = true, Email = "ap@customer.test", Address = "1 ถนนทดสอบ แขวงทดสอบ เขตทดสอบ กรุงเทพมหานคร 10110",
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        db.Documents.Add(new Document
        {
            CompanyId = co.Id, ContactId = contact.Id, DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved,
            DocumentNumber = "INV-OLD-0001", DocumentDate = DateTime.UtcNow.Date.AddDays(-60), DueDate = DateTime.UtcNow.Date.AddDays(-45),
            SubTotal = 1000m, VatAmount = 70m, TotalAmount = 1070m, BalanceDue = 1070m, AgingDays = 45, Currency = "THB", ExchangeRate = 1m,
        });
        var setter = new User { Email = $"r248-setter-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้ตั้งนโยบาย" };
        db.Users.Add(setter);
        await db.SaveChangesAsync();
        if (setterIsMember)
            db.CompanyUsers.Add(new CompanyUser { UserId = setter.Id, CompanyId = co.Id, Role = UserRole.Staff });
        var rec = new RecurringTransaction
        {
            CompanyId = co.Id, Name = "ค่าบริการรายเดือน", Frequency = RecurringFrequency.Monthly, Status = RecurringStatus.Active,
            StartDate = ThaiDate.CalendarDateUtc(DateTime.UtcNow), NextRunDate = ThaiDate.CalendarDateUtc(DateTime.UtcNow),
            PreferredDay = ThaiDate.CalendarDateUtc(DateTime.UtcNow).Day, TemplateType = "Document", DocumentType = DocumentType.Invoice,
            ContactId = contact.Id, AutoApprove = true, CreatedBy = "tester",
            TemplateData = JsonSerializer.Serialize(new
            {
                lines = new object[]
                {
                    new { description = "ค่าบริการเดือน <<month>>", quantity = 1, unitPrice = 5000m, vatRate = 7m, withholdingTaxRate = 0m, accountId = revenueId },
                    new { description = "ค่าดูแลระบบ", quantity = 2, unitPrice = 250m, vatRate = 7m, withholdingTaxRate = 0m, accountId = revenueId },
                },
            }),
        };
        db.RecurringTransactions.Add(rec);
        await db.SaveChangesAsync();
        return new Seed(co.Id, contact.Id, setter.Id, rec.Id);
    }

    private static async Task<(int Docs, int Queue, int Logs, int Audit, int Links)> CountsAsync(AccountingDbContext db, Guid cid)
        => (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.CompanyId == cid),
            await db.EmailQueues.IgnoreQueryFilters().CountAsync(q => q.CompanyId == cid),
            await db.DocumentEmailLogs.IgnoreQueryFilters().CountAsync(l => l.CompanyId == cid),
            await db.AuditLogs.CountAsync(a => a.CompanyId == cid),
            await db.Documents.IgnoreQueryFilters().CountAsync(d => d.CompanyId == cid && d.PayLinkTokenHash != null));

    [Fact]
    public async Task R248_ทดลองรันและตรวจความพร้อม_ไม่เขียนอะไรเลย_และบอกว่าจะหยุดเพราะค้างชำระ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var before = await CountsAsync(db, s.CompanyId);

        RecurringDryRunResponse dry;
        AutopilotReadinessResponse ready;
        RecurringDryRunResponse dryDraft;
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ap = Autopilot(req);
            dry = await ap.DryRunAsync(s.CompanyId, s.RecurringId);
            ready = await ap.ReadinessAsync(s.CompanyId, s.RecurringId);
            var rec = await req.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == s.RecurringId);
            dryDraft = await ap.DryRunDraftAsync(s.CompanyId, new CreateRecurringTransactionRequest(rec.Name, null, rec.Frequency, rec.StartDate,
                null, null, "Document", DocumentType.Invoice, s.ContactId, rec.TemplateData, AutoApprove: true, PaymentMode: "TransferOnly"), s.RecurringId);
            // ตาข่าย: ถ้ามีอะไรค้างใน context แล้วผู้เรียกบันทึกต่อ ต้องไม่มีแถวใหม่ (ตัวรวบรวมคำเตือนบางเส้น Add audit ลง context)
            await req.SaveChangesAsync();
        }

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(before, await CountsAsync(check, s.CompanyId));
        var after = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == s.RecurringId);
        Assert.Equal(0, after.TotalRuns);
        Assert.Null(after.LastRunDate);

        Assert.Equal(2, dry.Lines.Count);
        Assert.Equal(5500m, dry.SubTotal);
        Assert.Contains(dry.ApprovalBlocking, w => w.StartsWith(ArOverdueApprovalWarning.Prefix, StringComparison.Ordinal));
        Assert.Equal(AutopilotReadiness.LevelDraftOnly, ready.Level);
        Assert.Contains(ready.Items, i => i.Code == "APPROVE" && i.Verdict == AutopilotReadiness.Fail);
        Assert.Contains(dryDraft.Readiness.Items, i => i.Code == "GATEWAY" && i.Title.Contains("โอนเข้าบัญชี"));
    }

    [Fact]
    public async Task R248_ทิศตรงข้าม_ไม่ตั้งนโยบาย_ใบประจำค้างร่างเพราะค้างชำระ_เหมือนรอบ247()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Recurring(req).RunNowAsync(s.CompanyId, s.RecurringId, "tester");
        using var check = DbTestDatabase.TryCreateContext()!;
        var doc = await check.Documents.AsNoTracking().SingleAsync(d => d.CompanyId == s.CompanyId && d.DocumentNumber != "INV-OLD-0001");
        Assert.Equal(DocumentStatus.Draft, doc.Status);
        var rec = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == s.RecurringId);
        Assert.Contains(ArOverdueApprovalWarning.Prefix, rec.LastRunOutcome ?? "");
        Assert.False(await check.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.NewValues!.Contains(AutoApprovePolicy.RuleCode)));
    }

    [Fact]
    public async Task R248_นโยบายปล่อยค้างชำระ_ใบประจำอนุมัติ_พร้อมaudit_และหมายเหตุบนใบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Autopilot(req).SetRecurringPolicyAsync(s.CompanyId, s.RecurringId,
                new SetAutoApprovePolicyRequest(new List<string> { ArOverdueApprovalWarning.Code }), s.SetterId);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Recurring(req).RunNowAsync(s.CompanyId, s.RecurringId, "tester");

        using var check = DbTestDatabase.TryCreateContext()!;
        var doc = await check.Documents.AsNoTracking().SingleAsync(d => d.CompanyId == s.CompanyId && d.DocumentNumber != "INV-OLD-0001");
        var rec = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == s.RecurringId);
        _out.WriteLine($"status={doc.Status} notice={rec.LastRunNotice}");
        Assert.NotEqual(DocumentStatus.Draft, doc.Status);
        Assert.Contains("ปล่อยตามนโยบายอนุมัติ", doc.InternalNotes ?? "");
        var audits = await check.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.CompanyId && a.EntityId == doc.Id.ToString()).ToListAsync();
        Assert.Contains(audits, a => (a.NewValues ?? "").Contains(AutoApprovePolicy.RuleCode) && (a.NewValues ?? "").Contains(ArOverdueApprovalWarning.Code));
        // การตั้งนโยบายเองก็มีร่องรอย (หลักการ #10)
        Assert.True(await check.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityId == s.RecurringId.ToString()
            && a.NewValues!.Contains("SetAutoApprovePolicy")));
    }

    [Fact]
    public async Task R248_ทิศตรงข้าม_ผู้ตั้งนโยบายไม่ได้เป็นสมาชิก_ไม่ปล่อย_ใบค้างร่าง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, setterIsMember: false);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Autopilot(req).SetRecurringPolicyAsync(s.CompanyId, s.RecurringId,
                new SetAutoApprovePolicyRequest(new List<string> { ArOverdueApprovalWarning.Code }), s.SetterId);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Recurring(req).RunNowAsync(s.CompanyId, s.RecurringId, "tester");
        using var check = DbTestDatabase.TryCreateContext()!;
        var doc = await check.Documents.AsNoTracking().SingleAsync(d => d.CompanyId == s.CompanyId && d.DocumentNumber != "INV-OLD-0001");
        Assert.Equal(DocumentStatus.Draft, doc.Status);
        var rec = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == s.RecurringId);
        Assert.Contains("หยุดมีผล", rec.LastRunNotice ?? "");
    }

    [Fact]
    public async Task R248_ฝ่ายค้าน_เปลี่ยนผู้ติดต่อของรายการหลังตั้งนโยบาย_ไม่ปล่อยใบของลูกค้าใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Autopilot(req).SetRecurringPolicyAsync(s.CompanyId, s.RecurringId,
                new SetAutoApprovePolicyRequest(new List<string> { ArOverdueApprovalWarning.Code }), s.SetterId);
        // ลูกค้าอีกรายที่ค้างชำระเหมือนกัน — ผู้แก้รายการ (ไม่ต้องมีสิทธิ์อนุมัติ) เปลี่ยนผู้ติดต่อของรายการไปหาลูกค้ารายนี้
        Guid otherId;
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var other = new Contact
            {
                CompanyId = s.CompanyId, Name = "บริษัท ลูกค้าอีกราย จำกัด", TaxId = "0105556000033", BranchCode = "00000", ContactType = ContactType.JuristicPerson,
                IsCustomer = true, Email = "b@customer.test", Address = "2 ถนนทดสอบ แขวงทดสอบ เขตทดสอบ กรุงเทพมหานคร 10110",
            };
            req.Contacts.Add(other);
            await req.SaveChangesAsync();
            otherId = other.Id;
            req.Documents.Add(new Document
            {
                CompanyId = s.CompanyId, ContactId = other.Id, DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved,
                DocumentNumber = "INV-OLD-0002", DocumentDate = DateTime.UtcNow.Date.AddDays(-60), SubTotal = 1000m, VatAmount = 70m,
                TotalAmount = 1070m, BalanceDue = 1070m, AgingDays = 45, Currency = "THB", ExchangeRate = 1m,
            });
            var rec = await req.RecurringTransactions.SingleAsync(r => r.Id == s.RecurringId);
            rec.ContactId = other.Id;
            await req.SaveChangesAsync();
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Recurring(req).RunNowAsync(s.CompanyId, s.RecurringId, "tester");
        using var check = DbTestDatabase.TryCreateContext()!;
        var doc = await check.Documents.AsNoTracking().SingleAsync(d => d.CompanyId == s.CompanyId && d.ContactId == otherId && d.DocumentNumber != "INV-OLD-0002");
        Assert.Equal(DocumentStatus.Draft, doc.Status);
        var after = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == s.RecurringId);
        Assert.Contains("หยุดมีผล", after.LastRunNotice ?? "");
    }

    [Fact]
    public async Task R248_ตั้งนโยบายปล่อยผู้ซื้อ86_4_ถูกปฏิเสธ_ไม่บันทึก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Autopilot(req).SetRecurringPolicyAsync(s.CompanyId, s.RecurringId,
                new SetAutoApprovePolicyRequest(new List<string> { "BUYER-86/4" }), s.SetterId));
            Assert.Contains("86/4", ex.Message);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Null((await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == s.RecurringId)).AutoApprovePolicyJson);
    }
}
