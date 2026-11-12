using System.Text.Json;
using Accounting.Data;
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
/// รอบ 263 (คำร้องผู้ใช้) — ผ่านเมธอดจริงบน PostgreSQL: รอบอนุมัติอัตโนมัติไม่ผ่าน (ค้างร่าง) → ผู้ใช้อนุมัติเอง → ใบรับชำระครบ ⇒
/// หน้ารายละเอียดรายการประจำต้อง<b>ไม่</b>ขึ้น "อนุมัติ…ไม่สำเร็จ / ยังเป็นร่าง / ไม่ได้ส่งอีเมล" อีก (เดิมแสดงข้อความแช่แข็งของแถวเก่าตลอดไป) ·
/// หมายเหตุ "แก้แล้ว" บนใบบอกชื่อผู้อนุมัติ ไม่ใช่ id ผู้ใช้ · งวดที่ข้ามค้างจนผู้ใช้ปิดเรื่อง (คำตัดสิน 263-1-3) แล้วกล่องหาย
/// </summary>
[Collection("recurring-due-sweep")]
[Trait("Category", "Db")]
public class RecurringRunLiveRound263DbTests
{
    private readonly ITestOutputHelper _out;
    public RecurringRunLiveRound263DbTests(ITestOutputHelper output) => _out = output;

    private static DocumentService Docs(AccountingDbContext db)
        => new(db, new AccountingService(db), RecurringTransactionDbTests.AllowAll<ISubscriptionService>.Create(), null!, null!,
            NullLogger<DocumentService>.Instance, null!,
            new Accounting.Services.Implementations.Ocr.VendorIntelligenceService(db, NullLogger<Accounting.Services.Implementations.Ocr.VendorIntelligenceService>.Instance),
            null!);

    private static RecurringTransactionService Recurring(AccountingDbContext db)
        => new(db, Docs(db), new AccountingService(db), NullLogger<RecurringTransactionService>.Instance,
            new ErrorLogService(db, NullLogger<ErrorLogService>.Instance));

    private const string CreditWarning = "ผู้ติดต่อนี้ยังค้างชำระ 3 ใบ รวม 9,750.00 THB — ตรวจวงเงินเครดิตก่อนอนุมัติเอกสารใหม่";

    [Fact]
    public async Task R263_อนุมัติเองแล้วรับชำระครบ_หน้ารายการประจำไม่เตือนอนุมัติ_ชื่อผู้อนุมัติไม่ใช่id_ปิดงวดที่ข้ามแล้วกล่องหาย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }

        var co = new Company { Name = "บริษัททดสอบรอบ 263", TaxId = "0105556000025", IsVatRegistered = true };
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
            CompanyId = co.Id, Name = "บริษัท ลูกค้ารอบ 263 จำกัด", TaxId = "0105556000017", BranchCode = "00000", ContactType = ContactType.JuristicPerson,
            IsCustomer = true, Email = "ap@customer.test", Address = "1 ถนนทดสอบ แขวงทดสอบ เขตทดสอบ กรุงเทพมหานคร 10110",
        };
        db.Contacts.Add(contact);
        var approver = new User { Email = $"r263-approver-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้อนุมัติรอบ263" };
        db.Users.Add(approver);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = approver.Id, CompanyId = co.Id, Role = UserRole.Accountant });
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var rec = new RecurringTransaction
        {
            CompanyId = co.Id, Name = "ค่าบริการรายเดือน", Frequency = RecurringFrequency.Monthly, Status = RecurringStatus.Active,
            StartDate = today, NextRunDate = today, PreferredDay = today.Day, TemplateType = "Document", DocumentType = DocumentType.Invoice,
            ContactId = contact.Id, AutoApprove = true, CreatedBy = "tester",   // ผู้สร้างไม่ใช่สมาชิก ⇒ อนุมัติอัตโนมัติไม่ผ่าน (ค้างร่าง) แบบรอบ 257 L4
            TemplateData = JsonSerializer.Serialize(new
            {
                lines = new object[] { new { description = "ค่าบริการเดือน <<month>>", quantity = 1, unitPrice = 5000m, vatRate = 7m, withholdingTaxRate = 0m, accountId = revenueId } },
            }),
        };
        db.RecurringTransactions.Add(rec);
        await db.SaveChangesAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Recurring(req).RunNowAsync(co.Id, rec.Id, "tester");

        Guid docId; string draftNo;
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var d = await check.Documents.AsNoTracking().SingleAsync(x => x.CompanyId == co.Id);
            Assert.Equal(DocumentStatus.Draft, d.Status);
            docId = d.Id; draftNo = d.DocumentNumber!;
            // ทิศเดิม: ยังเป็นร่างจริง ⇒ ยังต้องเตือนให้อนุมัติ
            var before = (await Recurring(check).GetByIdAsync(co.Id, rec.Id)).LastRunOutcome!;
            Assert.False(before.AllResolved);
            Assert.Contains(before.Actions, a => a.Kind == "openDocument");
        }

        // แถวแบบในรายงาน (ก่อนรอบ 247 — มีแต่ข้อความ): อนุมัติไม่ผ่านเพราะค้างชำระ · ไม่ได้ส่งอีเมล · ข้าม 2 งวด
        var legacy = string.Join(" · ", new[]
        {
            AutoApproveFailure.Message("รายการประจำ (อนุมัติอัตโนมัติ)", draftNo, AutoApproveFailureKind.StillDraft,
                "เอกสารมีจุดที่ต้องตรวจก่อนยืนยันการอนุมัติ (1 รายการ): " + CreditWarning, DocumentStatus.Draft),
            "ยังไม่ได้ส่งอีเมล — เอกสารไม่ได้ถูกอนุมัติ (ใบร่างยังไม่มีเลขที่จริงตาม §86/4) อนุมัติที่หน้าเอกสารแล้วส่งจากที่นั่น",
            "ข้าม 2 งวดที่ค้าง (ตั้งแต่งวด 26/08/2569) ระหว่างที่ระบบสร้างเอกสารไม่ได้ — ออกเฉพาะงวดล่าสุด · ถ้างวดที่ข้ามยังไม่ได้ออกเอง กด \"สร้างทันที\" เพื่อออกเพิ่มทีละใบ",
        });
        using (var req = DbTestDatabase.TryCreateContext()!)
            await req.RecurringTransactions.Where(r => r.Id == rec.Id && r.CompanyId == co.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.LastRunOutcome, (string?)null).SetProperty(r => r.LastRunNotice, legacy));

        // ผู้ใช้อนุมัติเอง (รับทราบคำเตือน) → ลูกค้าชำระครบ
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Docs(req).ApproveDocumentAsync(co.Id, docId, approver.Id.ToString(), acknowledgeWarnings: true);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await req.Documents.Where(d => d.Id == docId && d.CompanyId == co.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DocumentStatus.Paid).SetProperty(d => d.BalanceDue, 0m));

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var doc = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == docId && d.CompanyId == co.Id);
            _out.WriteLine($"notes={doc.InternalNotes}");
            // ข้อ 3: หมายเหตุ "แก้แล้ว" ประทับชื่อ ไม่ใช่ id ผู้ใช้
            Assert.Contains("แก้แล้ว", doc.InternalNotes ?? "");
            Assert.Contains("โดย ผู้อนุมัติรอบ263", doc.InternalNotes ?? "");
            Assert.DoesNotContain(approver.Id.ToString(), doc.InternalNotes ?? "");

            var view = (await Recurring(check).GetByIdAsync(co.Id, rec.Id)).LastRunOutcome!;
            _out.WriteLine($"headline={view.Headline} steps={string.Join(" | ", view.Steps)} resolved={string.Join(" | ", view.Resolved ?? Array.Empty<string>())}");
            Assert.DoesNotContain("รอบล่าสุดสร้างเอกสารแล้ว แต่มีเรื่องต้องทำต่อ", view.Headline);
            Assert.DoesNotContain(view.Steps, s => s.Contains(draftNo) || s.Contains("ค้างชำระ") || s.StartsWith("อีเมล"));
            var ap = Assert.Single(view.Resolved!, r => r.StartsWith("อนุมัติแล้ว"));
            Assert.Contains("ผู้อนุมัติรอบ263", ap);
            Assert.Contains("รับชำระครบแล้ว", ap);
            // งวดที่ข้ามยังค้าง (คำตัดสิน 263-1-3) — ทางไปต่อคือปิดเรื่อง
            Assert.Contains(view.Steps, s => s.Contains("ข้าม 2 งวด"));
            Assert.Contains(view.Actions, a => a.Kind == "dismissSkipped");
        }

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var after = (await Recurring(req).DismissSkippedPeriodsAsync(co.Id, rec.Id, approver.Id.ToString())).LastRunOutcome!;
            Assert.True(after.AllResolved);
            Assert.Empty(after.Steps);
            Assert.Contains(after.Resolved!, r => r.Contains("ปิดเรื่องแล้ว") && r.Contains("ผู้อนุมัติรอบ263"));
        }
        // ทิศตรงข้าม: ปิดซ้ำ = ปฏิเสธพร้อมเหตุ (ไม่ใช่สำเร็จเงียบ)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Recurring(req).DismissSkippedPeriodsAsync(co.Id, rec.Id, approver.Id.ToString()));
    }
}
