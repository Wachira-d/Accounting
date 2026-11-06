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

    /// <summary>รอบ 250 WP-04 ข้อ 9 (E §3): ใบค้างชำระเกิน 30 วัน — วันครบกำหนดเป็นตัวตั้ง · AgingDays คำนวณจากวันครบกำหนด (เดิมพิมพ์ 45 ตายตัว
    /// แยกจาก DueDate ⇒ สองค่าคลาดกันได้เงียบ ๆ เมื่อใครแก้อีกตัว)</summary>
    private static DateTime OverdueDue() => DateTime.UtcNow.Date.AddDays(-45);

    private static int AgingOf(DateTime due) => (int)(DateTime.UtcNow.Date - due.Date).TotalDays;

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
            DocumentNumber = "INV-OLD-0001", DocumentDate = DateTime.UtcNow.Date.AddDays(-60), DueDate = OverdueDue(),
            SubTotal = 1000m, VatAmount = 70m, TotalAmount = 1070m, BalanceDue = 1070m, AgingDays = AgingOf(OverdueDue()), Currency = "THB", ExchangeRate = 1m,
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
                DocumentNumber = "INV-OLD-0002", DocumentDate = DateTime.UtcNow.Date.AddDays(-60), DueDate = OverdueDue(), SubTotal = 1000m, VatAmount = 70m,
                TotalAmount = 1070m, BalanceDue = 1070m, AgingDays = AgingOf(OverdueDue()), Currency = "THB", ExchangeRate = 1m,
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

    private static async Task<Document> NewDocAsync(AccountingDbContext check, Guid companyId, Guid contactId)
        => await check.Documents.AsNoTracking().SingleAsync(d => d.CompanyId == companyId && d.ContactId == contactId && !d.DocumentNumber.StartsWith("INV-OLD"));

    [Fact]
    public async Task R248_ฝ่ายค้าน_แก้ยอดหลังตั้งนโยบาย_ไม่ปล่อย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Autopilot(req).SetRecurringPolicyAsync(s.CompanyId, s.RecurringId,
                new SetAutoApprovePolicyRequest(new List<string> { ArOverdueApprovalWarning.Code }), s.SetterId);
        // สมาชิกที่ไม่มีสิทธิ์อนุมัติแก้ยอดเป็น 900,000 (แก้รายการไม่ต้องมีสิทธิ์อนุมัติ)
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var rec = await req.RecurringTransactions.SingleAsync(r => r.Id == s.RecurringId);
            rec.TemplateData = rec.TemplateData.Replace("5000", "900000");
            await req.SaveChangesAsync();
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Recurring(req).RunNowAsync(s.CompanyId, s.RecurringId, "tester");
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(DocumentStatus.Draft, (await NewDocAsync(check, s.CompanyId, s.ContactId)).Status);
        Assert.Contains("หยุดมีผล", (await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == s.RecurringId)).LastRunNotice ?? "");
    }

    [Fact]
    public async Task R248_ฝ่ายค้าน_นโยบายผู้ติดต่อ_ไม่มีผลกับรายการที่ไม่ได้รับรอง_รับรองแล้วปล่อย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Autopilot(req).SetContactPolicyAsync(s.CompanyId, s.ContactId,
                new SetAutoApprovePolicyRequest(new List<string> { ArOverdueApprovalWarning.Code }), s.SetterId);
        // รายการที่สมาชิกไม่มีสิทธิ์สร้าง (ไม่มีใบรับรอง) ⇒ ไม่ปล่อย
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Recurring(req).RunNowAsync(s.CompanyId, s.RecurringId, "tester");
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(DocumentStatus.Draft, (await NewDocAsync(check, s.CompanyId, s.ContactId)).Status);
        // ผู้มีสิทธิ์รับรองรายการ ⇒ งวดถัดไปปล่อย
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Autopilot(req).SetRecurringPolicyAsync(s.CompanyId, s.RecurringId,
                new SetAutoApprovePolicyRequest(null, Inherit: true), s.SetterId);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Recurring(req).RunNowAsync(s.CompanyId, s.RecurringId, "tester");
        using var check2 = DbTestDatabase.TryCreateContext()!;
        var docs = await check2.Documents.AsNoTracking()
            .Where(d => d.CompanyId == s.CompanyId && d.ContactId == s.ContactId && !d.DocumentNumber.StartsWith("INV-OLD"))
            .OrderBy(d => d.CreatedAt).ToListAsync();
        Assert.Equal(2, docs.Count);
        Assert.NotEqual(DocumentStatus.Draft, docs[1].Status);
    }

    [Fact]
    public async Task R248_ฝ่ายค้าน_LOW7_ชื่อผู้ติดต่อขึ้นต้นด้วยป้ายAR_คำเตือนอื่นไม่ถูกปล่อย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var c = await req.Contacts.SingleAsync(x => x.Id == s.ContactId);
            c.Name = "[AR-OVERDUE] บริษัทปลอมป้าย จำกัด";
            c.Address = null;   // ⇒ คำเตือนที่อยู่ผู้ซื้อ (มีชื่อผู้ติดต่ออยู่กลางข้อความ) บนใบกำกับภาษี
            var rec = await req.RecurringTransactions.SingleAsync(r => r.Id == s.RecurringId);
            rec.DocumentType = DocumentType.TaxInvoice;
            rec.TemplateData = rec.TemplateData.Replace("ค่าบริการเดือน <<month>>", "[AR-OVERDUE] ค่าบริการ");
            await req.SaveChangesAsync();
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Autopilot(req).SetRecurringPolicyAsync(s.CompanyId, s.RecurringId,
                new SetAutoApprovePolicyRequest(new List<string> { ArOverdueApprovalWarning.Code }), s.SetterId);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var dry = await Autopilot(req).DryRunAsync(s.CompanyId, s.RecurringId);
            Assert.All(dry.ApprovalReleased, w => Assert.StartsWith(ArOverdueApprovalWarning.Prefix + " ผู้ติดต่อนี้ยังค้างชำระ", w));
            Assert.NotEmpty(dry.ApprovalBlocking);   // คำเตือนที่อยู่ผู้ซื้อ (§86/4) ยังหยุด
        }
    }

    [Fact]
    public async Task R248_ฝ่ายค้าน_MED4_ทดลองรันใบค่าใช้จ่าย_ค่าเช่า_ไม่ถามAI()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var rec = await req.RecurringTransactions.SingleAsync(r => r.Id == s.RecurringId);
            rec.DocumentType = DocumentType.Expense;
            rec.TemplateData = JsonSerializer.Serialize(new
            {
                lines = new[] { new { description = "ค่าเช่าสำนักงานเดือน <<month>>", quantity = 1, unitPrice = 30000m, vatRate = 0m, withholdingTaxRate = 0m } },
            });
            await req.SaveChangesAsync();
        }
        var aiCalls = 0;
        var before = await CountsAsync(db, s.CompanyId);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var augmenter = F4Proxy<Accounting.Services.Ai.IDocumentAiAugmenter>.Create((m, _) => { aiCalls++; return null; });
            var docs = new DocumentService(req, new AccountingService(req), RecurringTransactionDbTests.AllowAll<ISubscriptionService>.Create(), null!, null!,
                NullLogger<DocumentService>.Instance, null!,
                new Accounting.Services.Implementations.Ocr.VendorIntelligenceService(req, NullLogger<Accounting.Services.Implementations.Ocr.VendorIntelligenceService>.Instance),
                null!, aiAugmenter: augmenter);
            await new RecurringAutopilotService(req, docs).DryRunAsync(s.CompanyId, s.RecurringId);
            await new RecurringAutopilotService(req, docs).ReadinessAsync(s.CompanyId, s.RecurringId);
            await req.SaveChangesAsync();
        }
        Assert.Equal(0, aiCalls);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(before, await CountsAsync(check, s.CompanyId));
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
                new SetAutoApprovePolicyRequest(new List<string> { AutoApprovePolicy.NeverBuyerTaxCode }), s.SetterId));
            Assert.Contains("86/4", ex.Message);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Null((await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == s.RecurringId)).AutoApprovePolicyJson);
    }

    // ═══════════════ รอบ 251 (WP-07 ข้อ 5 · A B-19): MED-5 ผ่านเส้น "เข้าคิว → ตรวจซ้ำตอนส่ง" ตัวจริง ═══════════════

    /// <summary>ตัวแทนบริการลิงก์ชำระ — จดค่าชั้น "แนบลิงก์ไหม" ของกฎที่เส้นคิวส่งเข้ามา (ตอนเข้าคิว + ตอนส่ง) · บริษัทเปิด gateway (PromptPay · บัตร · โอน)</summary>
    private static IDocumentPayLinkService CapturingLinks(List<(bool? Attach, string? Channels)> seen)
        => F4Proxy<IDocumentPayLinkService>.Create((m, a) => m switch
        {
            nameof(IDocumentPayLinkService.CompanyFactsAsync) => new DocumentPayLinkPolicy.CompanyFacts(true, new[] { "PromptPay", "Card" }, true),
            nameof(IDocumentPayLinkService.ForScheduledEmailAsync) => Record(seen, (EmailScheduleRule)a[0]!),
            _ => null,
        });

    private static object? Record(List<(bool? Attach, string? Channels)> seen, EmailScheduleRule rule)
    {
        seen.Add((rule.AttachPayLink, rule.PayLinkChannelsJson));
        return null;   // ไม่ออกลิงก์จริง — เทสต์นี้ล็อก "ชั้นที่ส่งเข้าตัวออกลิงก์" ซึ่งเป็นจุดที่ MED-5 เคยหลุด
    }

    /// <summary>ใบแจ้งหนี้ที่รายการประจำสร้างแล้วอนุมัติ + กฎ "เมื่อรายการประจำสร้างเอกสาร" ที่ไม่ได้ตั้งแนบลิงก์เอง (null = ตามชั้นล่าง)</summary>
    private static async Task<Guid> RecurringInvoiceWithRuleAsync(AccountingDbContext db, Seed s, string? recurringMode)
    {
        var rec = await db.RecurringTransactions.SingleAsync(r => r.Id == s.RecurringId);
        rec.PaymentMode = recurringMode;
        var doc = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved,
            DocumentNumber = "INV-R251-" + Guid.NewGuid().ToString("N")[..6], DocumentDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(30),
            SubTotal = 1000m, VatAmount = 70m, TotalAmount = 1070m, BalanceDue = 1070m, Currency = "THB", ExchangeRate = 1m,
            RecurringTransactionId = s.RecurringId,
        };
        db.Documents.Add(doc);
        db.EmailScheduleRules.Add(new EmailScheduleRule { CompanyId = s.CompanyId, Trigger = "RecurringInvoiceCreated", IsActive = true, AttachPayLink = null });
        await db.SaveChangesAsync();
        return doc.Id;
    }

    private static EmailScheduleService Schedule(AccountingDbContext db, IDocumentPayLinkService links)
        => new(db, F4Proxy<IEmailSenderFactory>.Create((_, _) => null), F4Proxy<IPdfGenerationService>.Create((_, _) => null),
            NullLogger<EmailScheduleService>.Instance, links);

    [Fact]
    public async Task R248_MED5_TransferOnly_รายการ_อีเมลที่ส่งไม่มีลิงก์บัตร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var docId = await RecurringInvoiceWithRuleAsync(db, s, AutopilotSettingsResolver.TransferOnly);
        var seen = new List<(bool? Attach, string? Channels)>();
        var sched = Schedule(db, CapturingLinks(seen));

        var outcome = await sched.OnRecurringDocumentCreatedAsync(s.CompanyId, docId, s.RecurringId);
        Assert.True(outcome.Kind == EmailEnqueueKind.Enqueued, outcome.Reason);
        var atEnqueue = Assert.Single(seen);
        Assert.False(atEnqueue.Attach);   // บริษัทเปิด gateway แต่รายการตั้งโอนเข้าบัญชีอย่างเดียว ⇒ ไม่แนบลิงก์ (ไม่มีบัตร)

        // ตอนส่ง: กฎถูกโหลดใหม่จากฐาน (ไม่มีชั้นรายการ) ⇒ ต้องต่อชั้นรายการจากกุญแจคิวอีกครั้ง (ฝ่ายค้าน MED-5)
        seen.Clear();
        var q = await db.EmailQueues.SingleAsync(x => x.CompanyId == s.CompanyId && x.EntityId == docId);
        Assert.Null(await sched.RecheckDocumentAtSendAsync(q, CancellationToken.None));
        var atSend = Assert.Single(seen);
        Assert.False(atSend.Attach);
    }

    [Fact]
    public async Task R248_MED5_ทิศตรงข้าม_Inherit_ตอนส่งตามค่าบริษัท_มีลิงก์ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var docId = await RecurringInvoiceWithRuleAsync(db, s, null);
        var seen = new List<(bool? Attach, string? Channels)>();
        var sched = Schedule(db, CapturingLinks(seen));

        Assert.Equal(EmailEnqueueKind.Enqueued, (await sched.OnRecurringDocumentCreatedAsync(s.CompanyId, docId, s.RecurringId)).Kind);
        seen.Clear();
        var q = await db.EmailQueues.SingleAsync(x => x.CompanyId == s.CompanyId && x.EntityId == docId);
        Assert.Null(await sched.RecheckDocumentAtSendAsync(q, CancellationToken.None));
        var atSend = Assert.Single(seen);
        Assert.Null(atSend.Attach);       // ไม่ตั้งที่รายการ ⇒ ไม่มีชั้นรายการ ⇒ ตัวออกลิงก์ใช้ผู้ติดต่อ › บริษัท (บริษัทเปิด = แนบได้) — พฤติกรรมเดิม
        Assert.Null(atSend.Channels);
    }

    // ═══════════════ รอบ 251 (WP-07 ข้อ 4 · A B-16): ส่งทดสอบถึงอีเมลฉัน ═══════════════

    private sealed class CaptureSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();
        public EmailProvider Provider => EmailProvider.Smtp;
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(EmailSendResult.Ok("r251-test"));
        }
    }

    private static IEmailSenderFactory Senders(CaptureSender s) => F4Proxy<IEmailSenderFactory>.Create((m, _) => m switch
    {
        nameof(IEmailSenderFactory.PlatformFromAddress) => "no-reply@nextacc.net",
        _ => s,
    });

    [Fact]
    public async Task R251_ส่งทดสอบ_ถึงผู้ใช้เท่านั้น_ไม่สร้างDocumentEmailLog_ไม่นับเพดาน_มีaudit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var setter = await db.Users.SingleAsync(u => u.Id == s.SetterId);
        setter.EmailVerified = true;   // ฝ่ายค้านข้อ 2a: ส่งได้เฉพาะอีเมลที่ยืนยันแล้ว
        await db.SaveChangesAsync();
        var userEmail = setter.Email;
        var before = await CountsAsync(db, s.CompanyId);
        var quotaBefore = await PlatformEmailQuota.ForCompanyAsync(db, null, s.CompanyId, DateTime.UtcNow);
        var sender = new CaptureSender();

        RecurringSendTestResponse res;
        using (var req = DbTestDatabase.TryCreateContext()!)
            res = await new RecurringAutopilotService(req, Docs(req), senders: Senders(sender)).SendTestAsync(s.CompanyId, s.RecurringId, s.SetterId);

        var msg = Assert.Single(sender.Sent);
        Assert.Equal(userEmail, Assert.Single(msg.To));       // ผู้ใช้ที่กดเท่านั้น — ไม่ใช่ผู้ติดต่อ (ap@customer.test)
        Assert.Empty(msg.Cc);
        Assert.Empty(msg.Bcc);
        Assert.StartsWith(RecurringTestEmail.SubjectPrefix, msg.Subject);
        Assert.DoesNotContain("pay-document", msg.HtmlBody);   // ไม่มีลิงก์ชำระจริง
        Assert.DoesNotContain("ap@customer.test", msg.HtmlBody);   // ฝ่ายค้านข้อ 2: อีเมลลูกค้าไม่ไปกล่องของพนักงาน
        Assert.DoesNotContain("ap@customer.test", msg.PlainTextBody ?? "");
        Assert.Contains("NextAcc", msg.HtmlBody);                 // เส้นแพลตฟอร์มห่อด้วยหัวเปิดเผย (คำตัดสิน 240-9)
        Assert.Equal(userEmail, res.To);

        using var check = DbTestDatabase.TryCreateContext()!;
        var after = await CountsAsync(check, s.CompanyId);
        Assert.Equal(before.Docs, after.Docs);
        Assert.Equal(before.Queue, after.Queue);
        Assert.Equal(before.Logs, after.Logs);                // ไม่บันทึกเป็นการส่งเอกสาร
        Assert.Equal(before.Audit + 1, after.Audit);          // ร่องรอยว่าใครกด (hash chain)
        Assert.Equal(quotaBefore.SentToday, (await PlatformEmailQuota.ForCompanyAsync(check, null, s.CompanyId, DateTime.UtcNow)).SentToday);
    }

    [Fact]
    public async Task R251_ทิศตรงข้าม_ผู้ใช้ไม่มีอีเมล_409_ไม่ส่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var noMail = new User { Email = $"r251-no-mail-{Guid.NewGuid():N}", PasswordHash = "x", FullName = "ผู้ใช้ไม่มีอีเมล", EmailVerified = true };
        db.Users.Add(noMail);
        await db.SaveChangesAsync();
        var sender = new CaptureSender();

        using var req = DbTestDatabase.TryCreateContext()!;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new RecurringAutopilotService(req, Docs(req), senders: Senders(sender)).SendTestAsync(s.CompanyId, s.RecurringId, noMail.Id));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(RecurringTestEmail.RuleCode, ex.RuleCode);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task R251_ทิศตรงข้าม_อีเมลยังไม่ยืนยัน_409_ไม่ส่ง_ไม่มีaudit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);   // ผู้ตั้งนโยบายใน seed ยังไม่ยืนยันอีเมล (ค่าตั้งต้น)
        var before = await CountsAsync(db, s.CompanyId);
        var sender = new CaptureSender();

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new RecurringAutopilotService(req, Docs(req), senders: Senders(sender)).SendTestAsync(s.CompanyId, s.RecurringId, s.SetterId));
            Assert.Equal(409, ex.StatusCode);
            Assert.Equal(RecurringTestEmail.UnverifiedRefusal(false), ex.Message);
        }
        Assert.Empty(sender.Sent);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(before, await CountsAsync(check, s.CompanyId));
    }

    [Fact]
    public async Task R251_เพดานส่งทดสอบ_ต่อผู้ใช้ต่อบริษัท_ข้ามรายการ_429()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var setter = await db.Users.SingleAsync(u => u.Id == s.SetterId);
        setter.EmailVerified = true;
        // ส่งไปแล้วครบเพดานวันนี้ — กระจายหลายรายการ (ฝ่ายค้านข้อ 2c: เดิมนับต่อรายการ ⇒ N × 10)
        for (var i = 0; i < RecurringTestEmail.MaxPerUserPerDay; i++)
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = s.CompanyId, UserId = s.SetterId, Action = AuditAction.SendEmail, EntityType = RecurringTestEmail.AuditEntityType,
                EntityId = Guid.NewGuid().ToString(), NewValues = "{\"action\":\"" + RecurringTestEmail.AuditActionName + "\"}",
            });
        await db.SaveChangesAsync();
        var sender = new CaptureSender();

        using var req = DbTestDatabase.TryCreateContext()!;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new RecurringAutopilotService(req, Docs(req), senders: Senders(sender)).SendTestAsync(s.CompanyId, s.RecurringId, s.SetterId));
        Assert.Equal(429, ex.StatusCode);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task R251_ทดลองรัน_บอกช่องทางeTax_และงวด_ยังไม่เขียนอะไร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        // บริษัทโหมด e-Tax by Email แต่ยังไม่ได้ตั้ง SMTP ⇒ รายการใบกำกับภาษีที่ส่งอีเมลอัตโนมัติต้อง "จะล้มที่ด่านผู้ส่ง"
        db.CompanySettings.Add(new CompanySettings { CompanyId = s.CompanyId, EtaxEnabled = true, EtaxMode = EtaxMode.ByEmail });
        var rec = await db.RecurringTransactions.SingleAsync(r => r.Id == s.RecurringId);
        rec.DocumentType = DocumentType.TaxInvoice;
        rec.AutoSendEmail = true;
        await db.SaveChangesAsync();

        // ผู้ติดต่อมีใบค้างชำระ ⇒ [AR-OVERDUE] ค้างรับทราบ ⇒ ใบจะหยุดเป็นร่าง ⇒ ยังไม่ส่งอะไร (ก่อนถึงด่านผู้ส่ง) — ทำนายตามจริง (CI 42232967230:
        // เทสต์เดิมไม่ได้ตั้งนโยบาย จึงได้ "ใบยังเป็นร่าง" ซึ่งถูกต้อง) · ทิศตรงข้าม: ปล่อย AR-OVERDUE ตามนโยบาย ⇒ ใบอนุมัติ ⇒ ไปถึงด่านผู้ส่ง e-Tax
        RecurringDryRunResponse draftDry;
        using (var req = DbTestDatabase.TryCreateContext()!)
            draftDry = await Autopilot(req).DryRunAsync(s.CompanyId, s.RecurringId);
        Assert.Contains("ใบยังเป็นร่าง", draftDry.EmailPlan);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Autopilot(req).SetRecurringPolicyAsync(s.CompanyId, s.RecurringId,
                new SetAutoApprovePolicyRequest(new List<string> { ArOverdueApprovalWarning.Code }), s.SetterId);
        var before = await CountsAsync(db, s.CompanyId);

        RecurringDryRunResponse dry;
        using (var req = DbTestDatabase.TryCreateContext()!)
            dry = await Autopilot(req).DryRunAsync(s.CompanyId, s.RecurringId);

        Assert.Contains(dry.Readiness.Items, i => i.Code == "ETAX-SENDER" && i.Verdict == AutopilotReadiness.Fail);
        Assert.DoesNotContain(dry.Readiness.Items, i => i.Code == "SMTP");   // ไม่บอกว่า "ส่งผ่าน NextAcc" กับใบที่ห้ามถอย
        Assert.NotEqual(AutopilotReadiness.LevelFull, dry.Readiness.Level);
        Assert.Contains(dry.Readiness.Items, i => i.Code == "PERIOD" && i.Verdict == AutopilotReadiness.Ok);
        Assert.NotNull(dry.EtaxPlan);
        Assert.Contains("ด่านผู้ส่ง", dry.EmailPlan);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(before, await CountsAsync(check, s.CompanyId));
    }

    [Fact]
    public async Task R251_ไม่ติ๊กส่ง_แต่มีกฎเมื่ออนุมัติเอกสาร_ยังตรวจeTax_ทิศตรงข้ามไม่มีกฎไม่ตรวจ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.CompanySettings.Add(new CompanySettings { CompanyId = s.CompanyId, EtaxEnabled = true, EtaxMode = EtaxMode.ByEmail });
        var rec = await db.RecurringTransactions.SingleAsync(r => r.Id == s.RecurringId);
        rec.DocumentType = DocumentType.TaxInvoice;
        rec.AutoSendEmail = false;
        await db.SaveChangesAsync();

        RecurringDryRunResponse noRule;
        using (var req = DbTestDatabase.TryCreateContext()!)
            noRule = await Autopilot(req).DryRunAsync(s.CompanyId, s.RecurringId);
        Assert.DoesNotContain(noRule.Readiness.Items, i => i.Code == "ETAX-SENDER");   // ไม่ส่งอีเมล ⇒ ไม่มีด่านให้ล้ม

        db.EmailScheduleRules.Add(new EmailScheduleRule { CompanyId = s.CompanyId, Trigger = "DocumentApproved", IsActive = true });
        await db.SaveChangesAsync();
        RecurringDryRunResponse withRule;
        using (var req = DbTestDatabase.TryCreateContext()!)
            withRule = await Autopilot(req).DryRunAsync(s.CompanyId, s.RecurringId);
        Assert.Contains(withRule.Readiness.Items, i => i.Code == "ETAX-SENDER" && i.Verdict == AutopilotReadiness.Fail);
        Assert.Contains("ตามกฎ", withRule.EmailPlan);
    }
}
