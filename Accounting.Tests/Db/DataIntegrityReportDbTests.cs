using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ทีมตรวจงานค้าง 2026-10-08 (คำตัดสินเจ้าของ "รายงานอ่านอย่างเดียวก่อน"): รายงานข้อมูลที่อาจบันทึกผิดจากบั๊กที่แก้แล้ว ·
/// ต้องเจอใบที่ผิดจริง · ใบที่ถูกอยู่แล้วต้องไม่ถูกฟ้อง (ทิศตรงข้าม) · ห้ามเห็นข้อมูลบริษัทอื่น (กฎ M)
/// </summary>
[Trait("Category", "Db")]
public class DataIntegrityReportDbTests
{
    private readonly ITestOutputHelper _out;
    public DataIntegrityReportDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid ContactId);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, string name, string taxId)
    {
        var company = new Company { Name = name, TaxId = taxId, IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = company.Id, Name = "คู่ค้าทดสอบ", IsCustomer = true, IsSupplier = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id);
    }

    private static async Task<Document> DocAsync(Accounting.Data.AccountingDbContext db, Seed s, DocumentType type, string no,
        Guid? parent, Guid? lineSourceDocumentId = null)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = type, DocumentNumber = no, Status = DocumentStatus.Approved,
            DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, TotalAmount = 1_000m, BalanceDue = 1_000m,
            RelatedDocumentId = parent, Currency = "THB",
        };
        d.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "รายการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m,
            SourceDocumentId = lineSourceDocumentId,
        });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    [Fact]
    public async Task เจอใบที่ผิด_ไม่ฟ้องใบที่ถูก_ไม่เห็นบริษัทอื่น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var a = await SeedAsync(db, "บริษัทตรวจข้อมูล ก", "0105556000005");
        var b = await SeedAsync(db, "บริษัทตรวจข้อมูล ข", "0105556000006");

        // ── ใบแจ้งหนี้ซื้อผูก PO ตรงทั้งที่ PO มี GRN (ผิด) vs ผูก GRN (ถูก)
        var po = await DocAsync(db, a, DocumentType.PurchaseOrder, "PO-DI-1", null);
        var grn = await DocAsync(db, a, DocumentType.GoodsReceiptNote, "GRN-DI-1", po.Id);
        var piBad = await DocAsync(db, a, DocumentType.PurchaseInvoice, "PI-DI-BAD", po.Id);
        var piOk = await DocAsync(db, a, DocumentType.PurchaseInvoice, "PI-DI-OK", grn.Id);

        // ── ใบเสร็จจากใบวางบิลรวมใบแจ้งหนี้ (ผิด) vs ใบเสร็จจากใบวางบิลของใบเสนอราคา (ถูก)
        var inv = await DocAsync(db, a, DocumentType.Invoice, "INV-DI-1", null);
        var rollupBn = await DocAsync(db, a, DocumentType.BillingNote, "BN-DI-ROLL", null, lineSourceDocumentId: inv.Id);
        var rcBad = await DocAsync(db, a, DocumentType.Receipt, "RC-DI-BAD", rollupBn.Id);
        var qt = await DocAsync(db, a, DocumentType.Quotation, "QT-DI-1", null);
        var qtBn = await DocAsync(db, a, DocumentType.BillingNote, "BN-DI-QT", qt.Id);
        var rcOk = await DocAsync(db, a, DocumentType.Receipt, "RC-DI-OK", qtBn.Id);

        // ── บริษัท ข มีใบผิดชนิดเดียวกัน — ต้องไม่โผล่ในรายงานของ ก
        var poB = await DocAsync(db, b, DocumentType.PurchaseOrder, "PO-DI-B", null);
        await DocAsync(db, b, DocumentType.GoodsReceiptNote, "GRN-DI-B", poB.Id);
        var piB = await DocAsync(db, b, DocumentType.PurchaseInvoice, "PI-DI-B", poB.Id);

        using var req = DbTestDatabase.TryCreateContext()!;
        var report = await new DataIntegrityReportService(req).GetAsync(a.CompanyId);

        Assert.Contains(report.PurchaseInvoicesBilledFromPoWithGrn, x => x.Id == piBad.Id);
        Assert.DoesNotContain(report.PurchaseInvoicesBilledFromPoWithGrn, x => x.Id == piOk.Id);
        Assert.DoesNotContain(report.PurchaseInvoicesBilledFromPoWithGrn, x => x.Id == piB.Id);

        Assert.Contains(report.RollupBillingNoteChildren, x => x.Id == rcBad.Id);
        Assert.DoesNotContain(report.RollupBillingNoteChildren, x => x.Id == rcOk.Id);

        // รายงานไม่เขียนอะไร — ใบที่ฟ้องยังอยู่สถานะเดิม
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(DocumentStatus.Approved, check.Documents.Single(d => d.Id == piBad.Id && d.CompanyId == a.CompanyId).Status);
    }
}
