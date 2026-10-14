using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ทีม E 2026-10-13 — หน้าตรวจข้อมูล: (1) เอกสารจากสแกนที่ส่วนลด % ซ้อนบนราคาที่ลดแล้ว + ปุ่ม "แก้" (metadata อย่างเดียว · audit)
/// (2) DECISIONS ข้อ 111 รอบเงินเดือนที่ยกเลิกแล้วแต่ 50 ทวิ ยังออกแล้ว (อ่านอย่างเดียว) · ทั้งสองทิศ + tenant isolation (กฎ M)
/// </summary>
[Trait("Category", "Db")]
public class OcrLineDiscountFixDbTests
{
    private readonly ITestOutputHelper _out;
    public OcrLineDiscountFixDbTests(ITestOutputHelper output) => _out = output;

    private static async Task<(Guid CompanyId, Guid ContactId)> SeedCompanyAsync(Accounting.Data.AccountingDbContext db, string name, string taxId)
    {
        var company = new Company { Name = name, TaxId = taxId, IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = company.Id, Name = "ร้านวัสดุทดสอบ", IsSupplier = true, IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return (company.Id, contact.Id);
    }

    /// <summary>ใบซื้อที่ตัวสร้างจากสแกนรุ่นเก่าเขียน: ราคา = ยอดหลังลด 950 + ส่วนลด 5% + บาท 0 · ยอด/VAT ตามกระดาษ</summary>
    private static async Task<Document> BuggyDocAsync(Accounting.Data.AccountingDbContext db, Guid companyId, Guid contactId, string no,
        DocumentStatus status, bool fromScan)
    {
        var d = new Document
        {
            CompanyId = companyId, ContactId = contactId, DocumentType = DocumentType.PurchaseInvoice, DocumentNumber = no, Status = status,
            DocumentDate = DateTime.UtcNow.Date, SubTotal = 950m, VatAmount = 66.50m, TotalAmount = 1016.50m, BalanceDue = 1016.50m,
            Currency = "THB",
        };
        d.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "ปูนกาว (สแกน · ไม่มีราคาต่อหน่วย)", Quantity = 1m, UnitPrice = 950m,
            DiscountPercent = 5m, DiscountAmount = 0m, Amount = 950m, VatRate = 7m, VatAmount = 66.50m,
        });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        if (fromScan)
        {
            db.Set<OcrScanResult>().Add(new OcrScanResult
            {
                CompanyId = companyId, OriginalFileName = no + ".pdf", ScanStatus = "Completed", CreatedDocumentId = d.Id,
            });
            await db.SaveChangesAsync();
        }
        return d;
    }

    [Fact]
    public async Task ส่วนลดบรรทัดจากสแกน_รายงานจับ_กดแก้แล้วเงินไม่เปลี่ยน_มีaudit_ทิศตรงข้ามไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (a, contactA) = await SeedCompanyAsync(db, "บริษัทแก้ส่วนลด ก", "0105556000101");
        var (b, contactB) = await SeedCompanyAsync(db, "บริษัทแก้ส่วนลด ข", "0105556000102");

        var bad = await BuggyDocAsync(db, a, contactA, "PI-OCR-BAD", DocumentStatus.Approved, fromScan: true);
        var manual = await BuggyDocAsync(db, a, contactA, "PI-MANUAL", DocumentStatus.Approved, fromScan: false);   // ไม่ได้มาจากสแกน
        var voided = await BuggyDocAsync(db, a, contactA, "PI-OCR-VOID", DocumentStatus.Voided, fromScan: true);
        var otherCo = await BuggyDocAsync(db, b, contactB, "PI-OCR-B", DocumentStatus.Approved, fromScan: true);

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var report = await new DataIntegrityReportService(req).GetAsync(a);
            var row = Assert.Single(report.OcrLineDiscountWouldChangeOnResave, x => x.Id == bad.Id);
            Assert.Equal(950m, row.Stored);
            Assert.Equal(902.50m, row.Expected);
            Assert.Equal(DataIntegrityReportService.OcrLineDiscountFixPath(bad.Id), row.FixPath);
            Assert.DoesNotContain(report.OcrLineDiscountWouldChangeOnResave, x => x.Id == manual.Id || x.Id == voided.Id || x.Id == otherCo.Id);
        }

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var r = await new DataIntegrityReportService(req).FixOcrLineDiscountAsync(a, bad.Id, null);
            Assert.Equal(1, r.LinesFixed);
        }

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var d = await check.Documents.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Id == bad.Id && x.CompanyId == a);
            var l = d.Lines.Single();
            Assert.Equal((0m, 0m, 950m), (l.DiscountPercent, l.DiscountAmount, l.UnitPrice));
            // เงินไม่เปลี่ยน: ยอดบรรทัด · VAT · หัวเอกสาร
            Assert.Equal((950m, 66.50m), (l.Amount, l.VatAmount));
            Assert.Equal((950m, 66.50m, 1016.50m), (d.SubTotal, d.VatAmount, d.TotalAmount));
            var audit = await check.AuditLogs.AsNoTracking()
                .Where(x => x.CompanyId == a && x.EntityType == "Document" && x.EntityId == bad.Id.ToString()).ToListAsync();
            Assert.Contains(audit, x => (x.NewValues ?? "").Contains(DocumentService.OcrLineDiscountFixRuleCode));
            // ใบอื่นไม่ถูกแตะ
            var m = await check.Documents.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Id == manual.Id && x.CompanyId == a);
            Assert.Equal(5m, m.Lines.Single().DiscountPercent);
            var o = await check.Documents.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Id == otherCo.Id && x.CompanyId == b);
            Assert.Equal(5m, o.Lines.Single().DiscountPercent);

            var after = await new DataIntegrityReportService(check).GetAsync(a);
            Assert.DoesNotContain(after.OcrLineDiscountWouldChangeOnResave, x => x.Id == bad.Id);
        }

        // ซ้ำ = ไม่มีอะไรต้องแก้ (idempotent) · ใบไม่ได้มาจากสแกน/ยกเลิก = ปฏิเสธพร้อมเหตุผล · บริษัทอื่น = ไม่พบ
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var svc = new DataIntegrityReportService(req);
            Assert.Equal(0, (await svc.FixOcrLineDiscountAsync(a, bad.Id, null)).LinesFixed);
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.FixOcrLineDiscountAsync(a, manual.Id, null));
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.FixOcrLineDiscountAsync(a, voided.Id, null));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.FixOcrLineDiscountAsync(a, otherCo.Id, null));
        }
    }

    [Fact]
    public async Task รอบเงินเดือนยกเลิกแล้ว_ยังมี50ทวิออกแล้ว_รายงานอ่านอย่างเดียว_ทิศตรงข้ามไม่ฟ้อง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (a, payeeA) = await SeedCompanyAsync(db, "บริษัทเงินเดือน ก", "0105556000103");
        var (b, payeeB) = await SeedCompanyAsync(db, "บริษัทเงินเดือน ข", "0105556000104");

        async Task<PayrollRun> RunAsync(Guid co, string no, string status)
        {
            var start = new DateTime(2026, 9, 1);
            var run = new PayrollRun
            {
                CompanyId = co, PayrollNumber = no, Name = no, Year = 2026, Month = 9, PeriodStart = start,
                PeriodEnd = start.AddMonths(1).AddDays(-1), PayDate = start.AddMonths(1).AddDays(-1), Status = status,
            };
            db.Set<PayrollRun>().Add(run);
            await db.SaveChangesAsync();
            return run;
        }
        async Task<WithholdingTaxCert> CertAsync(Guid co, Guid payee, Guid runId, string no, WithholdingTaxCertStatus status)
        {
            var c = new WithholdingTaxCert
            {
                CompanyId = co, CertificateNumber = no, PayeeContactId = payee, TaxFormType = TaxType.WithholdingTax1,
                TaxYear = 2026, TaxMonth = 9, Status = status, IssuedDate = new DateTime(2026, 9, 30),
                TotalIncomeAmount = 30000m, TotalTaxAmount = 500m, SourcePayrollRunId = runId,
            };
            db.WithholdingTaxCerts.Add(c);
            await db.SaveChangesAsync();
            return c;
        }

        var voidedRun = await RunAsync(a, "PR-VOID-1", "Voided");
        var paidRun = await RunAsync(a, "PR-PAID-1", "Paid");
        var stale = await CertAsync(a, payeeA, voidedRun.Id, "WHT-VOIDRUN-1", WithholdingTaxCertStatus.Issued);
        var alreadyVoid = await CertAsync(a, payeeA, voidedRun.Id, "WHT-VOIDRUN-2", WithholdingTaxCertStatus.Voided);
        var live = await CertAsync(a, payeeA, paidRun.Id, "WHT-PAID-1", WithholdingTaxCertStatus.Issued);
        var voidedRunB = await RunAsync(b, "PR-VOID-B", "Voided");
        var otherCo = await CertAsync(b, payeeB, voidedRunB.Id, "WHT-VOIDRUN-B", WithholdingTaxCertStatus.Issued);

        using var req = DbTestDatabase.TryCreateContext()!;
        var report = await new DataIntegrityReportService(req).GetAsync(a);
        var row = Assert.Single(report.VoidedPayrollRunsWithIssuedWhtCerts, x => x.Id == stale.Id);
        Assert.Contains("PR-VOID-1", row.Detail);
        Assert.NotNull(row.LinkUrl);
        Assert.Null(row.FixPath);   // อ่านอย่างเดียว — ไม่มีปุ่มแก้
        Assert.DoesNotContain(report.VoidedPayrollRunsWithIssuedWhtCerts,
            x => x.Id == alreadyVoid.Id || x.Id == live.Id || x.Id == otherCo.Id);

        // รายงานไม่ยกเลิกอะไรเอง
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(WithholdingTaxCertStatus.Issued,
            check.WithholdingTaxCerts.AsNoTracking().Single(w => w.Id == stale.Id && w.CompanyId == a).Status);
    }
}
