using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 204 (คำตัดสินเจ้าของข้อ 148) ผ่านเมธอดจริงบน PostgreSQL (กฎ #4 G — ไม่ใช่แค่ helper):
/// ข้อ 143 ใบสำคัญจ่ายใบเดียวหลายผู้ถูกหัก ⇒ 50 ทวิ แยกใบต่อผู้ถูกหัก แบบ ภ.ง.ด. ต่อผู้ถูกหัก · idempotent ต่อ (เอกสาร, ผู้ถูกหัก) ·
/// ข้อ 142 ใบลดหนี้ลดฐานหลังออกใบ ⇒ ยกเลิก+ออกใหม่เดือนเดิม (ยังไม่ยื่น) / ติดป้ายต้องตรวจ (ยื่นแล้ว) · รับทราบแล้วป้ายหาย + audit
/// </summary>
[Trait("Category", "Db")]
public class WhtMultiPayeeDbTests
{
    private readonly ITestOutputHelper _out;
    public WhtMultiPayeeDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid BrokerId, Guid InsurerId, Guid PersonId);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบหลายผู้ถูกหัก", TaxId = "0105556000025", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        Contact C(string name, string? taxId, ContactType type) => new()
        {
            CompanyId = company.Id, Name = name, TaxId = taxId, ContactType = type, IsSupplier = true,
        };
        var broker = C("บริษัท โบรกเกอร์ประกัน จำกัด", "0105556000017", ContactType.JuristicPerson);
        var insurer = C("บริษัท ประกันภัยทดสอบ จำกัด (มหาชน)", "0107537000017", ContactType.JuristicPerson);
        var person = C("นายสมชาย ใจดี", "3100600123450", ContactType.Individual);
        db.Contacts.AddRange(broker, insurer, person);
        await db.SaveChangesAsync();
        return new Seed(company.Id, broker.Id, insurer.Id, person.Id);
    }

    /// <summary>PV จ่ายแล้ว: บรรทัด 1 เบี้ยประกัน 1,000 หัก 1% (ผู้ถูกหัก = บริษัทประกัน) · บรรทัด 2 ค่าบริการ 500 หัก 3% (ผู้ถูกหัก = บุคคล)</summary>
    private static async Task<Document> MultiPvAsync(Accounting.Data.AccountingDbContext db, Seed s, string no)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.BrokerId, DocumentType = DocumentType.PaymentVoucher, DocumentNumber = no,
            Status = DocumentStatus.Paid, DocumentDate = DateTime.UtcNow.Date, PaymentDate = DateTime.UtcNow.Date,
            SubTotal = 1_500m, WithholdingTaxAmount = 25m, TotalAmount = 1_475m, PaidAmount = 1_475m, BalanceDue = 0m,
            Currency = "THB",
        };
        d.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "ค่าเบี้ยประกันภัย", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m,
            WithholdingTaxRate = 1m, WithholdingTaxAmount = 10m, IncomeTypeCode = "8", WhtPayeeContactId = s.InsurerId,
        });
        d.Lines.Add(new DocumentLine
        {
            LineOrder = 2, Description = "ค่าบริการตรวจสภาพ", Quantity = 1, UnitPrice = 500m, Amount = 500m,
            WithholdingTaxRate = 3m, WithholdingTaxAmount = 15m, IncomeTypeCode = "8", WhtPayeeContactId = s.PersonId,
        });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    private static WithholdingTaxCertService Svc(Accounting.Data.AccountingDbContext db)
        => new(db, null!, null!, NullLogger<WithholdingTaxCertService>.Instance);

    private static Task<List<WithholdingTaxCert>> CertsAsync(Accounting.Data.AccountingDbContext db, Guid companyId, Guid docId)
        => db.WithholdingTaxCerts.AsNoTracking().Include(w => w.Lines)
            .Where(w => w.CompanyId == companyId && w.DocumentId == docId).ToListAsync();

    [Fact]
    public async Task ใบหลายผู้ถูกหัก_ออกใบต่อผู้ถูกหัก_แบบตามผู้ถูกหัก_ไม่ออกซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var pv = await MultiPvAsync(db, s, "PV-MULTI-1");

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var o = await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
            Assert.False(o.Failed, o.Message);
            Assert.Equal(2, o.CertificateIds!.Count);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var again = await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
            Assert.Equal(WhtCertIssueAction.AlreadyIssued, again.Action);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        var certs = await CertsAsync(check, s.CompanyId, pv.Id);
        Assert.Equal(2, certs.Count);
        var ins = Assert.Single(certs, c => c.PayeeContactId == s.InsurerId);
        Assert.Equal(TaxType.WithholdingTax53, ins.TaxFormType);
        Assert.Equal(10m, ins.TotalTaxAmount);
        Assert.Equal("ค่าเบี้ยประกันภัย", Assert.Single(ins.Lines).IncomeDescription);
        var per = Assert.Single(certs, c => c.PayeeContactId == s.PersonId);
        Assert.Equal(TaxType.WithholdingTax3, per.TaxFormType);
        Assert.Equal(15m, per.TotalTaxAmount);
        Assert.DoesNotContain(certs, c => c.PayeeContactId == s.BrokerId);   // ผู้รับเงินไม่ใช่ผู้มีเงินได้ของบรรทัดใด
        Assert.DoesNotContain((await Svc(check).GetPendingDocumentsAsync(s.CompanyId)), p => p.DocumentId == pv.Id);
    }

    [Fact]
    public async Task ใบหลายผู้ถูกหักออกไม่ครบ_อยู่ในลิสต์รายผู้ถูกหัก_สร้างใบต้องเลือกผู้ถูกหัก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await MultiPvAsync(db, s, "PV-MULTI-2");

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var svc = Svc(req);
            // ไม่ระบุผู้ถูกหัก = ล้มดังพร้อมทางไปต่อ (ห้ามออกใบเดียวรวมสองราย)
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                svc.AutoGenerateFromDocumentAsync(s.CompanyId, pv.Id, autoIssue: true, "tester"));
            Assert.Contains("2 ราย", ex.Message);
            await svc.AutoGenerateFromDocumentAsync(s.CompanyId, pv.Id, autoIssue: true, "tester", payeeContactId: s.InsurerId);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var row = Assert.Single((await Svc(req).GetPendingDocumentsAsync(s.CompanyId)).Where(p => p.DocumentId == pv.Id));
            Assert.NotNull(row.Payees);
            Assert.True(row.Payees!.Single(p => p.PayeeContactId == s.InsurerId).HasCertificate);
            Assert.False(row.Payees!.Single(p => p.PayeeContactId == s.PersonId).HasCertificate);
            // ผู้ถูกหักเดิมออกซ้ำไม่ได้ · รายที่เหลือออกได้
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Svc(req).AutoGenerateFromDocumentAsync(s.CompanyId, pv.Id, autoIssue: true, "tester", payeeContactId: s.InsurerId));
            await Svc(req).AutoGenerateFromDocumentAsync(s.CompanyId, pv.Id, autoIssue: true, "tester", payeeContactId: s.PersonId);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(2, (await CertsAsync(check, s.CompanyId, pv.Id)).Count);
        Assert.DoesNotContain((await Svc(check).GetPendingDocumentsAsync(s.CompanyId)), p => p.DocumentId == pv.Id);
    }

    private static async Task<(Document Pv, Document Cn)> PvWithCertAndCnAsync(Accounting.Data.AccountingDbContext db, Seed s, string no)
    {
        var pv = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.BrokerId, DocumentType = DocumentType.PaymentVoucher, DocumentNumber = no,
            Status = DocumentStatus.Paid, DocumentDate = DateTime.UtcNow.Date, PaymentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, WithholdingTaxAmount = 30m, TotalAmount = 970m, PaidAmount = 970m, BalanceDue = 0m, Currency = "THB",
        };
        pv.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m,
            WithholdingTaxRate = 3m, WithholdingTaxAmount = 30m, IncomeTypeCode = "8",
        });
        db.Documents.Add(pv);
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.False((await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester")).Failed);
        var cn = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.BrokerId, DocumentType = DocumentType.CreditNote, DocumentNumber = no + "-CN",
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, RelatedDocumentId = pv.Id,
            SubTotal = 400m, WithholdingTaxAmount = 12m, TotalAmount = 388m, Currency = "THB",
        };
        cn.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "ลดค่าบริการ", Quantity = 1, UnitPrice = 400m, Amount = 400m,
            WithholdingTaxRate = 3m, WithholdingTaxAmount = 12m, IncomeTypeCode = "8", SourceLineId = pv.Lines.First().Id,
        });
        db.Documents.Add(cn);
        await db.SaveChangesAsync();
        return (pv, cn);
    }

    [Fact]
    public async Task ใบลดหนี้ลดฐาน_เดือนยังไม่ยื่น_ยกเลิกใบเดิมออกใหม่ยอดที่ลด_เดือนเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var (pv, cn) = await PvWithCertAndCnAsync(db, s, "PV-CN-1");
        WithholdingTaxCert before;
        using (var req = DbTestDatabase.TryCreateContext()!) before = Assert.Single(await CertsAsync(req, s.CompanyId, pv.Id));

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var notices = await Svc(req).AdjustCertsForCreditNoteAsync(s.CompanyId, cn.Id, "tester");
            Assert.Contains(notices, n => n.Contains("ออกใบใหม่"));
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        var certs = await CertsAsync(check, s.CompanyId, pv.Id);
        Assert.Equal(WithholdingTaxCertStatus.Voided, certs.Single(c => c.Id == before.Id).Status);
        var neo = Assert.Single(certs, c => c.Status != WithholdingTaxCertStatus.Voided);
        Assert.Equal(18m, neo.TotalTaxAmount);
        Assert.Equal(600m, neo.TotalIncomeAmount);
        Assert.Equal((before.TaxYear, before.TaxMonth), (neo.TaxYear, neo.TaxMonth));
        Assert.Equal(before.PayeeContactId, neo.PayeeContactId);
        Assert.True(await check.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == s.CompanyId
            && a.EntityType == "WithholdingTaxCert" && a.EntityId == before.Id.ToString()));
        var doc = await check.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id);
        Assert.Null(WhtCertReviewNote.ReasonOf(doc.InternalNotes));
    }

    [Fact]
    public async Task ใบลดหนี้ลดฐาน_เดือนยื่นแล้ว_ไม่แตะใบเดิม_ติดป้ายต้องตรวจ_รับทราบแล้วหาย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var (pv, cn) = await PvWithCertAndCnAsync(db, s, "PV-CN-2");
        var cert = Assert.Single(await CertsAsync(db, s.CompanyId, pv.Id));
        db.TaxReports.Add(new TaxReport
        {
            CompanyId = s.CompanyId, TaxType = cert.TaxFormType, Year = cert.TaxYear, Month = cert.TaxMonth,
            Status = TaxReportStatus.Filed, FiledDate = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(await Svc(req).AdjustCertsForCreditNoteAsync(s.CompanyId, cn.Id, "tester"), n => n.Contains("ต้องตรวจ"));
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var still = Assert.Single(await CertsAsync(req, s.CompanyId, pv.Id));
            Assert.NotEqual(WithholdingTaxCertStatus.Voided, still.Status);   // ผู้ถูกหักถือฉบับจริง + ยื่นแล้ว ⇒ ระบบไม่แก้เอง
            var row = Assert.Single((await Svc(req).GetPendingDocumentsAsync(s.CompanyId)).Where(p => p.DocumentId == pv.Id));
            Assert.Contains("ยื่นแล้ว", row.ReviewReason);
            // ทิศตรงข้าม: รับทราบต้องมีเหตุผล
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).AcknowledgeReviewAsync(s.CompanyId, pv.Id, " ", null));
            await Svc(req).AcknowledgeReviewAsync(s.CompanyId, pv.Id, "ยื่น ภ.ง.ด.53 เพิ่มเติมแล้ว", null);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.DoesNotContain((await Svc(check).GetPendingDocumentsAsync(s.CompanyId)), p => p.DocumentId == pv.Id);
    }
}
