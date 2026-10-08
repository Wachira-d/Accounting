using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ทีมตรวจงานค้าง C-01 + ฝ่ายค้าน: "รายได้รวมของใบเสนอราคา" ถูกต่อสายจริงในคำเตือนก่อนอนุมัติ (ไม่ใช่แค่ helper — กฎ #4 G) ·
/// เส้นจริง: QT → ใบส่งของ → ใบแจ้งหนี้ แล้วมีใบแจ้งหนี้ใบที่สองใต้ QT ตรง · ใบส่งของถูกยกเลิกภายหลังก็ยังนับ · แบ่งพอดี 100% ไม่เตือน
/// </summary>
[Trait("Category", "Db")]
public class RootRevenueLedgerDbTests
{
    private readonly ITestOutputHelper _out;
    public RootRevenueLedgerDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid ContactId);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบรายได้ใบเสนอราคา", TaxId = "0105556000004", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้าทดสอบ", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id);
    }

    private static async Task<Document> DocAsync(Accounting.Data.AccountingDbContext db, Seed s, DocumentType type, string no,
        decimal amount, Guid? parent, DocumentStatus status = DocumentStatus.Approved)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = type, DocumentNumber = no, Status = status,
            DocumentDate = DateTime.UtcNow.Date, SubTotal = amount, TotalAmount = amount, BalanceDue = amount,
            RelatedDocumentId = parent, Currency = "THB",
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "งานติดตั้ง", Quantity = 1, UnitPrice = amount, Amount = amount });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    private static DocumentService Svc(Accounting.Data.AccountingDbContext db) =>
        new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private const string Marker = "รายได้รวมของใบเสนอราคา";

    [Fact]
    public async Task ใบแจ้งหนี้ผ่านใบส่งของครบแล้ว_ใบแจ้งหนี้ตรงอีกใบ_เตือนตอนอนุมัติ_แม้ใบส่งของถูกยกเลิก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var qt = await DocAsync(db, s, DocumentType.Quotation, "QT-R-1", 10_000m, null);
        var dn = await DocAsync(db, s, DocumentType.DeliveryNote, "DN-R-1", 10_000m, qt.Id);
        await DocAsync(db, s, DocumentType.Invoice, "INV-R-1", 10_000m, dn.Id);
        var second = await DocAsync(db, s, DocumentType.Invoice, "DRAFT-R-2", 5_000m, qt.Id, DocumentStatus.Draft);

        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(await Svc(req).PreviewApprovalWarningsAsync(s.CompanyId, second.Id), w => w.Contains(Marker));

        // ใบส่งของถูกยกเลิกภายหลัง — ใบแจ้งหนี้ที่ออกจากมันยังมีผล ⇒ ยังนับ (ฝ่ายค้าน C-01 ข้อ 2)
        dn.Status = DocumentStatus.Voided;
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(await Svc(req).PreviewApprovalWarningsAsync(s.CompanyId, second.Id), w => w.Contains(Marker));
    }

    [Fact]
    public async Task ทิศตรงข้าม_แบ่งพอดี100เปอร์เซ็นต์_ตรงครึ่งหนึ่งผ่านใบส่งของครึ่งหนึ่ง_ไม่เตือน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var qt = await DocAsync(db, s, DocumentType.Quotation, "QT-R-2", 10_000m, null);
        await DocAsync(db, s, DocumentType.Invoice, "INV-R-21", 5_000m, qt.Id);
        var dn = await DocAsync(db, s, DocumentType.DeliveryNote, "DN-R-2", 5_000m, qt.Id);
        var last = await DocAsync(db, s, DocumentType.Invoice, "DRAFT-R-22", 5_000m, dn.Id, DocumentStatus.Draft);

        using var req = DbTestDatabase.TryCreateContext()!;
        Assert.DoesNotContain(await Svc(req).PreviewApprovalWarningsAsync(s.CompanyId, last.Id), w => w.Contains(Marker));
    }
}
