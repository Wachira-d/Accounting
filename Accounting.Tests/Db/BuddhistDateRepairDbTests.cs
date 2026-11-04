using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 235 (K-5 + ฝ่ายค้าน S2) — งานซ่อมวันที่ศักราชปนบน PostgreSQL จริง: ปี ≥ 2400 (พ.ศ. ไม่แปลง) และ &lt; 1900 (ลบซ้ำ) ถูกแก้ทั้งสองทิศ ·
/// ทิศตรงข้าม: งวดปิด · เอกสารออกเลขแล้ว ⇒ ข้าม (ไม่แตะ · ไม่แก้เลข) · ปีปกติไม่ถูกแตะ · audit chain หนึ่งแถว
/// </summary>
[Trait("Category", "Db")]
public class BuddhistDateRepairDbTests
{
    private readonly ITestOutputHelper _out;
    public BuddhistDateRepairDbTests(ITestOutputHelper output) => _out = output;

    private static JournalEntry Je(Guid cid, DateTime d) => new()
    {
        CompanyId = cid, EntryNumber = "JV-T235-" + Guid.NewGuid().ToString("N")[..8], EntryDate = d, Status = JournalEntryStatus.Draft,
    };

    [Fact]
    public async Task แก้สองทิศ_ข้ามงวดปิดและเอกสารออกเลขแล้ว_ปีปกติไม่แตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var co = new Company { Name = "ร้าน T235 ซ่อมวันที่", TaxId = "0105556000025" };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        var cid = co.Id;
        var contact = new Contact { CompanyId = cid, Name = "ลูกค้า", IsCustomer = true };
        db.Contacts.Add(contact);
        db.FiscalPeriods.Add(new FiscalPeriod { CompanyId = cid, Name = "2567/03", Year = 2024, Month = 3,
            StartDate = new DateTime(2024, 3, 1), EndDate = new DateTime(2024, 3, 31), Status = FiscalPeriodStatus.Closed });

        var over = Je(cid, new DateTime(2569, 6, 1));       // พ.ศ. ไม่แปลง ⇒ 2026-06-01
        var under = Je(cid, new DateTime(1483, 6, 2));      // ลบซ้ำ ⇒ 2026-06-02
        var closed = Je(cid, new DateTime(2567, 3, 15));    // ⇒ 2024-03-15 ในงวดปิด ⇒ ข้าม
        var normal = Je(cid, new DateTime(2026, 6, 3));     // ไม่แตะ
        db.JournalEntries.AddRange(over, under, closed, normal);
        var issued = new Document { CompanyId = cid, ContactId = contact.Id, DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved,
            DocumentNumber = "INV-T235-" + Guid.NewGuid().ToString("N")[..6], DocumentDate = new DateTime(2569, 6, 1), Currency = "THB", ExchangeRate = 1m };
        var draftNo = "DRAFT-" + Guid.NewGuid();
        var draft = new Document { CompanyId = cid, ContactId = contact.Id, DocumentType = DocumentType.Invoice, Status = DocumentStatus.Draft,
            DocumentNumber = draftNo, DocumentDate = new DateTime(2569, 6, 1), Currency = "THB", ExchangeRate = 1m };
        db.Documents.AddRange(issued, draft);
        await db.SaveChangesAsync();

        var svc = new AccountingService(db);
        var r = await svc.RepairBuddhistDatesAsync(cid, "tester@example.com", "127.0.0.1");
        Assert.Equal(3, r.Fixed);        // over · under · draft
        Assert.Equal(2, r.Skipped);      // closed · issued
        Assert.Equal(0, r.Unresolved);

        using var read = DbTestDatabase.TryCreateContext()!;
        DateTime EntryDate(Guid id) => read.JournalEntries.AsNoTracking().Single(j => j.Id == id).EntryDate.Date;
        Assert.Equal(new DateTime(2026, 6, 1), EntryDate(over.Id));
        Assert.Equal(new DateTime(2026, 6, 2), EntryDate(under.Id));
        Assert.Equal(new DateTime(2567, 3, 15), EntryDate(closed.Id));   // ไม่แตะ
        Assert.Equal(new DateTime(2026, 6, 3), EntryDate(normal.Id));    // ไม่แตะ
        var issuedNow = read.Documents.AsNoTracking().Single(d => d.Id == issued.Id);
        Assert.Equal(2569, issuedNow.DocumentDate.Year);                  // ไม่แตะ
        Assert.Equal(issued.DocumentNumber, issuedNow.DocumentNumber);    // ไม่แก้เลข
        var draftNow = read.Documents.AsNoTracking().Single(d => d.Id == draft.Id);
        Assert.Equal(2026, draftNow.DocumentDate.Year);
        Assert.Equal(draftNo, draftNow.DocumentNumber);
        Assert.Equal(1, read.AuditLogs.AsNoTracking().Count(a => a.CompanyId == cid && a.EntityType == "BuddhistDateRepair"));
    }
}
