using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Background;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 232 — งานซ่อมข้อมูลเก่า <see cref="SourceLineBackfillJob"/> ผ่านฐานจริง (กฎ #4 G: control ที่ไม่มีเทสต์ write→verify = ไม่มี control):
/// ใบแจ้งหนี้ซื้อที่ผูกหัวใบสั่งซื้อแต่บรรทัดไม่ชี้ต้นทาง + รายการตรงทุกบรรทัด ⇒ ถูกประทับ · ทิศตรงข้าม: จำนวนไม่ตรง ⇒ ไม่แตะ · รอบสอง = ไม่พิจารณาใบเดิมซ้ำ (watermark) · ใบใหม่/ใบต้นทางถูกแก้หลัง watermark ⇒ ถูกเห็นอีกครั้ง
/// </summary>
[Trait("Category", "Db")]
public class SourceLineBackfillDbTests
{
    private readonly ITestOutputHelper _out;
    public SourceLineBackfillDbTests(ITestOutputHelper output) => _out = output;

    private static async Task<(Guid CompanyId, Guid ContactId)> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบซ่อม SourceLineId", TaxId = "0105556000007", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = company.Id, Name = "ผู้ขายทดสอบ", IsSupplier = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return (company.Id, contact.Id);
    }

    private static async Task<Document> DocAsync(Accounting.Data.AccountingDbContext db, (Guid CompanyId, Guid ContactId) s,
        DocumentType type, string no, Guid? parent, params (string Desc, decimal Qty)[] lines)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = type, DocumentNumber = no, Status = DocumentStatus.Approved,
            DocumentDate = DateTime.UtcNow.Date, SubTotal = 12_164.72m, TotalAmount = 13_016.25m, BalanceDue = 13_016.25m,
            RelatedDocumentId = parent, Currency = "THB",
        };
        var order = 1;
        foreach (var (desc, qty) in lines)
            d.Lines.Add(new DocumentLine { LineOrder = order++, Description = desc, Quantity = qty, UnitPrice = 100m, Amount = 100m * qty });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    [Fact]
    public async Task ใบลูกผูกหัวแต่บรรทัดไม่ชี้ต้นทาง_รายการตรง_ถูกประทับ_จำนวนไม่ตรง_ไม่แตะ_รอบสองไม่ซ่อมซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var po = await DocAsync(db, s, DocumentType.PurchaseOrder, "PO-20261007-0001", null, ("สินค้า A", 2m), ("ค่าขนส่ง", 1m));
        var piExact = await DocAsync(db, s, DocumentType.PurchaseInvoice, "PI-20261007-0004", po.Id, ("สินค้า A", 2m), ("ค่าขนส่ง", 1m));
        var piOther = await DocAsync(db, s, DocumentType.PurchaseInvoice, "PI-20261007-0005", po.Id, ("สินค้า A", 1m), ("ค่าขนส่ง", 1m));
        // ใบที่แปลงมาจริง (บรรทัดชี้ต้นทางอยู่แล้ว) ต้องไม่อยู่ในรายการซ่อม
        var piConverted = await DocAsync(db, s, DocumentType.PurchaseInvoice, "PI-20261007-0006", po.Id, ("สินค้า A", 2m), ("ค่าขนส่ง", 1m));
        var poLines = po.Lines.OrderBy(l => l.LineOrder).ToList();
        foreach (var (l, i) in piConverted.Lines.OrderBy(l => l.LineOrder).Select((l, i) => (l, i))) l.SourceLineId = poLines[i].Id;
        await db.SaveChangesAsync();

        var firstRunAt = DateTime.UtcNow;
        SourceLineBackfillJob.Outcome first;
        using (var run = DbTestDatabase.TryCreateContext()!)
            first = await SourceLineBackfillJob.RunCompanyAsync(run, s.CompanyId, firstRunAt, CancellationToken.None);
        Assert.Equal(2, first.Candidates);          // ใบรายการตรง + ใบจำนวนไม่ตรง (ใบที่แปลงมาจริงไม่อยู่ในรายการ)
        Assert.Equal(1, first.RepairedDocuments);
        Assert.Equal(2, first.RepairedLines);
        Assert.Equal(1, first.Unmatched);

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var exact = await check.DocumentLines.AsNoTracking().Where(l => l.DocumentId == piExact.Id).OrderBy(l => l.LineOrder).ToListAsync();
            Assert.Equal(poLines[0].Id, exact[0].SourceLineId);
            Assert.Equal(poLines[1].Id, exact[1].SourceLineId);
            var other = await check.DocumentLines.AsNoTracking().Where(l => l.DocumentId == piOther.Id).ToListAsync();
            Assert.All(other, l => Assert.Null(l.SourceLineId));   // จำนวนไม่ตรง = ไม่แต่ง
            Assert.True(await check.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityId == piExact.Id.ToString()
                && a.NewValues!.Contains(SourceLineBackfillJob.JobName)));
        }

        using (var again = DbTestDatabase.TryCreateContext()!)
        {
            // ฝ่ายค้าน S4: watermark ต่อบริษัทเลื่อนแล้ว ⇒ รอบสองไม่พิจารณาใบเดิมซ้ำ (แม้ใบจำนวนไม่ตรงที่ไม่ถูกแตะ) · ใบใหม่หลัง watermark เท่านั้น
            var company = await again.Companies.AsNoTracking().SingleAsync(c => c.Id == s.CompanyId);
            Assert.NotNull(company.SourceLineBackfillCheckedAt);
            var second = await SourceLineBackfillJob.RunCompanyAsync(again, s.CompanyId, DateTime.UtcNow, CancellationToken.None);
            Assert.Equal(0, second.Candidates);
            Assert.Equal(0, second.RepairedDocuments);
            Assert.Equal(0, second.Unmatched);
        }
        // ใบลูกใหม่หลัง watermark (ทางเข้าที่ยังหลุด) ⇒ รอบถัดไปเห็นเฉพาะใบนั้น
        using (var late = DbTestDatabase.TryCreateContext()!)
        {
            await DocAsync(late, s, DocumentType.PurchaseInvoice, "PI-20261007-0007", po.Id, ("สินค้า A", 2m), ("ค่าขนส่ง", 1m));
            var third = await SourceLineBackfillJob.RunCompanyAsync(late, s.CompanyId, DateTime.UtcNow.AddSeconds(1), CancellationToken.None);
            Assert.Equal(1, third.Candidates);
            Assert.Equal(1, third.RepairedDocuments);
        }
        // ฝ่ายค้าน S4 (UpdatedAt): ใบต้นทางถูกแก้หลัง watermark (เช่น Remap สดจับไม่ได้ ⇒ dangling) ⇒ รอบถัดไปเห็นใบลูกเดิมของใบต้นทางนั้นอีกครั้ง
        // เฉพาะใบที่ยังไม่มีตัวชี้ใช้ได้ (piOther จำนวนไม่ตรง ⇒ ตรวจแล้วยังไม่แตะ) · ใบที่ซ่อมแล้ว/แปลงมาจริงไม่ถูกนับ
        using (var touchedSource = DbTestDatabase.TryCreateContext()!)
        {
            var poTouchedAt = DateTime.UtcNow.AddSeconds(5);
            await touchedSource.Documents.Where(d => d.Id == po.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.UpdatedAt, (DateTime?)poTouchedAt));
            var fourth = await SourceLineBackfillJob.RunCompanyAsync(touchedSource, s.CompanyId, poTouchedAt.AddSeconds(1), CancellationToken.None);
            Assert.Equal(1, fourth.Candidates);
            Assert.Equal(1, fourth.Unmatched);
            Assert.Equal(0, fourth.RepairedDocuments);
        }
    }
}
