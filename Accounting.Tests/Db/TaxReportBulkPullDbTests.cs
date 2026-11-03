using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 225: ดึงเอกสารเข้ารายงาน ภ.พ.30 หลายใบในครั้งเดียว — เส้นจริงบน PostgreSQL (ธุรกรรม + pg_advisory_xact_lock + audit ประทับตอน commit) ·
/// partial (ผ่านเข้า/ไม่ผ่านข้ามพร้อม RuleCode) · ส่งซ้ำไม่เกิดบรรทัดซ้ำ · ลำดับ §87 ตามวันที่ · ยอดรวมรายงานตรงบรรทัด ·
/// ใบต่างบริษัทถูกข้ามแบบ "ไม่พบ" · ดึงทีละใบเดินแกนเดียวกัน (ล้มด้วยข้อความเดิม)
/// </summary>
[Trait("Category", "Db")]
public class TaxReportBulkPullDbTests
{
    private readonly ITestOutputHelper _out;
    public TaxReportBulkPullDbTests(ITestOutputHelper output) => _out = output;

    private static Document Purchase(Guid companyId, Guid contactId, string no, DateTime date, decimal baseAmt,
        DocumentType type = DocumentType.PurchaseInvoice)
    {
        var vat = Math.Round(baseAmt * 0.07m, 2, MidpointRounding.AwayFromZero);
        var d = new Document
        {
            CompanyId = companyId, ContactId = contactId, DocumentNumber = no, DocumentType = type,
            Status = DocumentStatus.Approved, DocumentDate = date, SubTotal = baseAmt, VatAmount = vat, TotalAmount = baseAmt + vat,
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "สินค้า", Quantity = 1, UnitPrice = baseAmt, Amount = baseAmt,
            VatRate = 7, VatAmount = vat, IsVatClaimable = true });
        return d;
    }

    [Fact]
    public async Task ดึงหลายใบ_partial_ส่งซ้ำไม่ซ้ำ_เรียงตามวันที่_ยอดรวมตรง_audit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }

        var company = new Company { Name = "บริษัททดสอบดึงหลายใบ", TaxId = "0105556000003" };
        var other = new Company { Name = "บริษัทอื่น", TaxId = "0105556000004" };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();
        var vendor = new Contact { CompanyId = company.Id, Name = "ผู้ขาย ก", TaxId = "0105556000011", IsSupplier = true };
        var vendorB = new Contact { CompanyId = other.Id, Name = "ผู้ขายของบริษัทอื่น", IsSupplier = true };
        db.Contacts.AddRange(vendor, vendorB);
        await db.SaveChangesAsync();

        // รายงาน ต.ค. 2026 (ร่าง) + รายงาน ส.ค. ที่ใช้ใบหนึ่งไปแล้ว
        var oct = new TaxReport { CompanyId = company.Id, TaxType = TaxType.VAT, Year = 2026, Month = 10, Status = TaxReportStatus.Draft };
        var aug = new TaxReport { CompanyId = company.Id, TaxType = TaxType.VAT, Year = 2026, Month = 8, Status = TaxReportStatus.Draft };
        var late = Purchase(company.Id, vendor.Id, "PI-LATE", new DateTime(2026, 9, 20), 2000m);   // วันที่หลัง
        var early = Purchase(company.Id, vendor.Id, "PI-EARLY", new DateTime(2026, 8, 5), 1000m);   // วันที่ก่อน
        var used = Purchase(company.Id, vendor.Id, "PI-USED", new DateTime(2026, 8, 10), 500m);
        var tooOld = Purchase(company.Id, vendor.Id, "PI-OLD", new DateTime(2026, 2, 10), 300m);    // เกิน §82/3
        var cil = Purchase(company.Id, vendor.Id, "CIL-1", new DateTime(2026, 9, 1), 100m, DocumentType.CertificateInLieu);
        var foreign = Purchase(other.Id, vendorB.Id, "PI-OTHER-CO", new DateTime(2026, 9, 1), 900m);
        // ภาษีขายเดือนก่อน (ก.ย.) — คำตัดสินผู้ทำบัญชี: ดึงเข้า ต.ค. ไม่ได้ ต้องยื่นเพิ่มเติม
        var saleSep = Purchase(company.Id, vendor.Id, "TI-SEP", new DateTime(2026, 9, 25), 400m, DocumentType.TaxInvoice);
        db.Documents.AddRange(late, early, used, tooOld, cil, foreign, saleSep);
        db.TaxReports.AddRange(oct, aug);
        await db.SaveChangesAsync();
        db.TaxReportLines.Add(new TaxReportLine { TaxReportId = aug.Id, LineOrder = 1, Description = "ใช้แล้ว", IncomeAmount = 500m,
            TaxRate = 7, TaxAmount = 35m, IncomeTypeCode = "INPUT", DocumentId = used.Id, TransactionDate = used.DocumentDate });
        await db.SaveChangesAsync();

        var ids = new List<Guid> { late.Id, early.Id, used.Id, tooOld.Id, cil.Id, foreign.Id, late.Id, saleSep.Id };
        Accounting.Models.DTOs.Tax.PullDocumentsResult r1;
        using (var req = DbTestDatabase.TryCreateContext()!)
            r1 = await new TaxService(req).PullDocumentsIntoReportAsync(company.Id, oct.Id, ids, actorId: Guid.NewGuid(), actorEmail: "db@test");

        Assert.Equal(2, r1.AddedCount);
        Assert.Equal(5, r1.SkippedCount);
        Assert.Equal(TaxReportPullEligibility.RuleOutputLate, r1.Items.Single(i => i.DocumentId == saleSep.Id).RuleCode);
        Assert.Equal(TaxReportPullEligibility.RuleClaimedInOtherReport, r1.Items.Single(i => i.DocumentId == used.Id).RuleCode);
        Assert.Equal(TaxReportPullEligibility.RuleClaimWindow, r1.Items.Single(i => i.DocumentId == tooOld.Id).RuleCode);
        Assert.Equal(TaxReportPullEligibility.RuleCertificateInLieu, r1.Items.Single(i => i.DocumentId == cil.Id).RuleCode);
        var f = r1.Items.Single(i => i.DocumentId == foreign.Id);
        Assert.Equal(TaxReportPullEligibility.RuleNotFound, f.RuleCode);
        Assert.Null(f.DocumentNumber);

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var rep = await check.TaxReports.Include(x => x.Lines).AsNoTracking().SingleAsync(x => x.Id == oct.Id && x.CompanyId == company.Id);
            var lines = rep.Lines.Where(l => l.DocumentId != null).OrderBy(l => l.LineOrder).ToList();
            Assert.Equal(new[] { early.Id, late.Id }, lines.Select(l => l.DocumentId!.Value));   // §87 ตามวันที่ ไม่ใช่ตามคำขอ
            Assert.Equal(70m + 140m, rep.InputVat);
            Assert.Equal(rep.Lines.Where(l => !l.IsExcluded && l.IncomeTypeCode == "INPUT").Sum(l => l.TaxAmount), rep.InputVat);
            Assert.True(await check.AuditLogs.AnyAsync(a => a.CompanyId == company.Id && a.EntityId == oct.Id.ToString()
                && a.NewValues!.Contains("TaxReportPullDocuments")));
        }

        // ส่งซ้ำ (ดับเบิลคลิก/สองแท็บ) — ไม่มีบรรทัดเพิ่ม · ใบที่ดึงแล้วถูกข้ามด้วยเหตุผลซ้ำในงวดนี้
        Accounting.Models.DTOs.Tax.PullDocumentsResult r2;
        using (var req = DbTestDatabase.TryCreateContext()!)
            r2 = await new TaxService(req).PullDocumentsIntoReportAsync(company.Id, oct.Id, new[] { late.Id, early.Id });
        Assert.Equal(0, r2.AddedCount);
        Assert.All(r2.Items, i => Assert.Equal(TaxReportPullEligibility.RuleAlreadyInThisReport, i.RuleCode));
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(2, await check.TaxReportLines.CountAsync(l => l.TaxReportId == oct.Id && l.DocumentId != null));

        // ดึงทีละใบเดินแกนเดียวกัน — ใบที่ไม่ผ่านล้มด้วยข้อความเดิม (ไม่ใช่เพิ่มบรรทัด)
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new TaxService(req).PullDocumentIntoReportAsync(company.Id, oct.Id, used.Id));
            Assert.Contains("08/2026", ex.Message);
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => new TaxService(req).PullDocumentIntoReportAsync(company.Id, oct.Id, foreign.Id));
        }

        // รายการ "ดึงเอกสาร": ใบที่ดึงแล้ว/ห้ามดึงไม่โผล่ · Total = ทั้งชุด
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var list = await new TaxService(req).GetPullableDocumentsAsync(company.Id, oct.Id, null,
                new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
            Assert.Equal(0, list.Total);
            Assert.DoesNotContain(list.Items, i => i.Id == cil.Id);
        }
    }

    [Fact]
    public async Task ดึงใบเดียวกันเข้าสองงวดพร้อมกัน_ผ่านได้งวดเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบดึงพร้อมกัน", TaxId = "0105556000005" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var vendor = new Contact { CompanyId = company.Id, Name = "ผู้ขาย ข", IsSupplier = true };
        db.Contacts.Add(vendor);
        await db.SaveChangesAsync();
        var sep = new TaxReport { CompanyId = company.Id, TaxType = TaxType.VAT, Year = 2026, Month = 9, Status = TaxReportStatus.Draft };
        var oct = new TaxReport { CompanyId = company.Id, TaxType = TaxType.VAT, Year = 2026, Month = 10, Status = TaxReportStatus.Draft };
        var doc = Purchase(company.Id, vendor.Id, "PI-RACE", new DateTime(2026, 8, 20), 1000m);
        db.TaxReports.AddRange(sep, oct);
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        // สองคำขอ (สอง DbContext = สอง connection) ยิงพร้อมกัน — ล็อกงานเขียน ภ.พ.30 ต่อบริษัททำให้ตัวที่สองเห็นบรรทัดของตัวแรก
        using var a = DbTestDatabase.TryCreateContext()!;
        using var b = DbTestDatabase.TryCreateContext()!;
        var results = await Task.WhenAll(
            new TaxService(a).PullDocumentsIntoReportAsync(company.Id, sep.Id, new[] { doc.Id }),
            new TaxService(b).PullDocumentsIntoReportAsync(company.Id, oct.Id, new[] { doc.Id }));
        Assert.Equal(1, results.Sum(r => r.AddedCount));
        Assert.Equal(TaxReportPullEligibility.RuleClaimedInOtherReport, results.Single(r => r.AddedCount == 0).Items.Single().RuleCode);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await check.TaxReportLines.CountAsync(l => l.DocumentId == doc.Id && !l.IsExcluded
            && l.TaxReport.CompanyId == company.Id));
    }
}
