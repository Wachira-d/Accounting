using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// คำตัดสินเจ้าของ 2026-10-08 "Add DN + GRN" (รุ่นสองของข้อ 139) — ผูกภายหลังผ่านเมธอดจริง (ไม่ใช่แค่ helper · กฎ #4 G):
/// ใบแจ้งหนี้ → ใบส่งของ · ใบแจ้งหนี้ซื้อร่างที่ผูกใบสั่งซื้อ (ใบจากสแกน) → ย้ายไปใบรับสินค้าของใบสั่งซื้อเดียวกัน (ทางซ่อม PI-PO-HAS-GRN)
/// · ทิศตรงข้าม: ใบแจ้งหนี้ซื้อที่อนุมัติแล้วผูกไม่ได้ · ใบรับสินค้าของใบสั่งซื้ออื่นไม่ได้
/// </summary>
[Trait("Category", "Db")]
public class DocumentLinkDbTests
{
    private readonly ITestOutputHelper _out;
    public DocumentLinkDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid ContactId);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบผูกเอกสาร", TaxId = "0105556000007", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = company.Id, Name = "คู่ค้าผูกเอกสาร", IsCustomer = true, IsSupplier = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id);
    }

    private static async Task<Document> DocAsync(Accounting.Data.AccountingDbContext db, Seed s, DocumentType type, string no,
        Guid? parent, DocumentStatus status = DocumentStatus.Approved, Guid? lineSourceId = null)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = type, DocumentNumber = no, Status = status,
            DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, TotalAmount = 1_000m, BalanceDue = 1_000m,
            RelatedDocumentId = parent, Currency = "THB",
        };
        d.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "สินค้า A", Quantity = 10, UnitPrice = 100m, Amount = 1_000m, SourceLineId = lineSourceId,
        });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    /// <summary>ใบรับสินค้าที่ลงบัญชีแล้ว (ตั้ง 21240) — หลักฐานที่ด่านผูกและตอนอนุมัติใช้ร่วมกัน</summary>
    private static async Task PostJeAsync(Accounting.Data.AccountingDbContext db, Seed s, Document doc)
    {
        db.JournalEntries.Add(new JournalEntry
        {
            CompanyId = s.CompanyId, EntryNumber = "JV-" + doc.DocumentNumber, EntryDate = doc.DocumentDate,
            Status = JournalEntryStatus.Posted, SourceDocumentId = doc.Id, TotalDebit = 1_000m, TotalCredit = 1_000m,
        });
        await db.SaveChangesAsync();
    }

    private static DocumentService Svc(Accounting.Data.AccountingDbContext db) =>
        new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    [Fact]
    public async Task ใบแจ้งหนี้ซื้อร่างที่ผูกใบสั่งซื้อ_ย้ายไปใบรับสินค้าของใบสั่งซื้อเดียวกัน_บรรทัดชี้ใบรับสินค้า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var po = await DocAsync(db, s, DocumentType.PurchaseOrder, "PO-LK-1", null);
        var grn = await DocAsync(db, s, DocumentType.GoodsReceiptNote, "GRN-LK-1", po.Id, lineSourceId: po.Lines.Single().Id);
        var otherPo = await DocAsync(db, s, DocumentType.PurchaseOrder, "PO-LK-2", null);
        var otherGrn = await DocAsync(db, s, DocumentType.GoodsReceiptNote, "GRN-LK-2", otherPo.Id, lineSourceId: otherPo.Lines.Single().Id);
        await PostJeAsync(db, s, grn);
        await PostJeAsync(db, s, otherGrn);
        // ใบจากสแกนที่ผูกใบสั่งซื้อตรง (ทั้งหัวและบรรทัด) — ด่าน PI-PO-HAS-GRN กันตอนอนุมัติ
        var pi = await DocAsync(db, s, DocumentType.PurchaseInvoice, "DRAFT-LK-1", po.Id, DocumentStatus.Draft, po.Lines.Single().Id);
        var piLineId = pi.Lines.Single().Id;

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var cands = await Svc(req).GetLinkCandidatesAsync(s.CompanyId, pi.Id);
            Assert.Null(cands.BlockedReason);
            Assert.Contains(cands.Candidates, c => c.Id == grn.Id);
            Assert.DoesNotContain(cands.Candidates, c => c.Id == otherGrn.Id);   // ใบรับสินค้าของใบสั่งซื้ออื่นไม่ถูกเสนอ
        }

        // ทิศตรงข้าม: ส่งใบรับสินค้าของใบสั่งซื้ออื่นมาตรง ๆ ⇒ ถูกกัน
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).LinkToSourceAsync(s.CompanyId, pi.Id,
                new LinkSourceRequest(otherGrn.Id, new() { new LinkLineMap(piLineId, otherGrn.Lines.Single().Id) }), "db-test"));

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).LinkToSourceAsync(s.CompanyId, pi.Id,
                new LinkSourceRequest(grn.Id, new() { new LinkLineMap(piLineId, grn.Lines.Single().Id) }), "db-test");

        using var check = DbTestDatabase.TryCreateContext()!;
        var after = await check.Documents.AsNoTracking().Include(d => d.Lines)
            .SingleAsync(d => d.Id == pi.Id && d.CompanyId == s.CompanyId);
        Assert.Equal(grn.Id, after.RelatedDocumentId);                         // ตอนอนุมัติจะล้าง 21240 ไม่รับสต็อกซ้ำ
        Assert.Equal(grn.Lines.Single().Id, after.Lines.Single().SourceLineId);
        Assert.NotNull(after.SourceLinkedAt);
        Assert.Equal(DocumentStatus.Draft, after.Status);                      // ผูกไม่อนุมัติ/ไม่ลงบัญชีเอง

        // ถอดไม่ได้ — ถอดแล้วจะเป็นใบเดี่ยวที่ด่าน PI-PO-HAS-GRN มองไม่เห็น ⇒ อนุมัติแล้วรับสต็อกซ้ำ (ฝ่ายค้าน 2026-10-09)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).UnlinkSourceAsync(s.CompanyId, pi.Id, "db-test"));
    }

    [Fact]
    public async Task ทิศตรงข้าม_ใบรับสินค้าไม่มีรายการบัญชี_หรือจับคู่ไม่ครบทุกบรรทัด_ผูกไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var po = await DocAsync(db, s, DocumentType.PurchaseOrder, "PO-LK-4", null);
        var grnNoJe = await DocAsync(db, s, DocumentType.GoodsReceiptNote, "GRN-LK-4", po.Id, lineSourceId: po.Lines.Single().Id);
        var pi = await DocAsync(db, s, DocumentType.PurchaseInvoice, "DRAFT-LK-4", null, DocumentStatus.Draft);

        // (ก) อนุมัติแล้วแต่ไม่มี JE ตั้ง 21240 ⇒ ตอนอนุมัติจะถอยไปรับสต็อกเอง = ซ้ำกับใบรับสินค้า ⇒ ไม่เสนอ + ส่งตรงถูกกัน
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.DoesNotContain((await Svc(req).GetLinkCandidatesAsync(s.CompanyId, pi.Id)).Candidates, c => c.Id == grnNoJe.Id);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).LinkToSourceAsync(s.CompanyId, pi.Id,
                new LinkSourceRequest(grnNoJe.Id, new() { new LinkLineMap(pi.Lines.Single().Id, grnNoJe.Lines.Single().Id) }), "db-test"));

        // (ข) มี JE แล้ว แต่ใบแจ้งหนี้ซื้อมีบรรทัดค่าขนส่งที่ไม่ได้รับผ่านใบรับสินค้า ⇒ ล้าง 21240 ทั้งใบผิด ⇒ ต้องจับคู่ครบ
        await PostJeAsync(db, s, grnNoJe);
        db.DocumentLines.Add(new DocumentLine
        {
            DocumentId = pi.Id, LineOrder = 2, Description = "ค่าขนส่ง", Quantity = 1, UnitPrice = 50m, Amount = 50m,
        });
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).LinkToSourceAsync(s.CompanyId, pi.Id,
                new LinkSourceRequest(grnNoJe.Id, new() { new LinkLineMap(pi.Lines.Single(l => l.LineOrder == 1).Id, grnNoJe.Lines.Single().Id) }), "db-test"));

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Null((await check.Documents.AsNoTracking().SingleAsync(d => d.Id == pi.Id && d.CompanyId == s.CompanyId)).RelatedDocumentId);
    }

    [Fact]
    public async Task ทิศตรงข้าม_ใบแจ้งหนี้ซื้อที่อนุมัติแล้ว_ผูกใบรับสินค้าไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var po = await DocAsync(db, s, DocumentType.PurchaseOrder, "PO-LK-3", null);
        var grn = await DocAsync(db, s, DocumentType.GoodsReceiptNote, "GRN-LK-3", po.Id, lineSourceId: po.Lines.Single().Id);
        var pi = await DocAsync(db, s, DocumentType.PurchaseInvoice, "PI-LK-3", null, DocumentStatus.Approved);

        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.NotNull((await Svc(req).GetLinkCandidatesAsync(s.CompanyId, pi.Id)).BlockedReason);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).LinkToSourceAsync(s.CompanyId, pi.Id,
                new LinkSourceRequest(grn.Id, new() { new LinkLineMap(pi.Lines.Single().Id, grn.Lines.Single().Id) }), "db-test"));
    }

    [Fact]
    public async Task ใบแจ้งหนี้ที่อนุมัติแล้ว_ผูกใบส่งของได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var dn = await DocAsync(db, s, DocumentType.DeliveryNote, "DN-LK-1", null);
        var inv = await DocAsync(db, s, DocumentType.Invoice, "INV-LK-1", null);

        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Contains((await Svc(req).GetLinkCandidatesAsync(s.CompanyId, inv.Id)).Candidates, c => c.Id == dn.Id);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).LinkToSourceAsync(s.CompanyId, inv.Id,
                new LinkSourceRequest(dn.Id, new() { new LinkLineMap(inv.Lines.Single().Id, dn.Lines.Single().Id) }), "db-test");

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(dn.Id, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv.Id && d.CompanyId == s.CompanyId)).RelatedDocumentId);
    }
}
