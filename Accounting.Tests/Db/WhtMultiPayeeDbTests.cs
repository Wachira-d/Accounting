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

    // ══════ ฝ่ายค้านรอบ 204 (หลัง 778e95f9/c3f47b4d) — ผ่านเมธอดจริง ══════

    private static DocumentService DocSvc(Accounting.Data.AccountingDbContext db)
        => new(db, new AccountingService(db), null!, Svc(db), null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    /// <summary>PV จ่ายแล้วบรรทัดเดียว ค่าบริการ 1,000 หัก 3% — <paramref name="linePayee"/> = ผู้ถูกหักรายบรรทัด (null = ตามหัวเอกสาร)</summary>
    private static async Task<Document> SinglePvAsync(Accounting.Data.AccountingDbContext db, Seed s, string no, Guid? linePayee)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.BrokerId, DocumentType = DocumentType.PaymentVoucher, DocumentNumber = no,
            Status = DocumentStatus.Paid, DocumentDate = DateTime.UtcNow.Date, PaymentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, WithholdingTaxAmount = 30m, TotalAmount = 970m, PaidAmount = 970m, BalanceDue = 0m, Currency = "THB",
        };
        d.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m,
            WithholdingTaxRate = 3m, WithholdingTaxAmount = 30m, IncomeTypeCode = "8", WhtPayeeContactId = linePayee,
        });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    [Fact]
    public async Task P1_ทุกบรรทัดตั้งผู้ถูกหักรายบรรทัดรายเดียว_50ทวิในนามรายนั้น_ตรงกับJE_ไม่ใช่หัวเอกสาร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        // ข้อมูลที่บันทึกก่อนแก้ (ใบร่างรูป "หัว = นายหน้า · ทุกบรรทัด = บริษัทประกัน") — ตัวอ่านต้องได้คนเดียวกับ JE (WhtPayeeSplit.Portions)
        var pv = await SinglePvAsync(db, s, "PV-P1-1", s.InsurerId);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.False((await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester")).Failed);
        using var check = DbTestDatabase.TryCreateContext()!;
        var cert = Assert.Single(await CertsAsync(check, s.CompanyId, pv.Id));
        Assert.Equal(s.InsurerId, cert.PayeeContactId);
        Assert.Equal(TaxType.WithholdingTax53, cert.TaxFormType);
        Assert.DoesNotContain((await Svc(check).GetPendingDocumentsAsync(s.CompanyId)), p => p.DocumentId == pv.Id);
    }

    [Fact]
    public async Task P1_ทิศตรงข้าม_ใบรายเดียวปกติ_50ทวิในนามหัวเอกสารเหมือนเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await SinglePvAsync(db, s, "PV-P1-2", null);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.False((await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester")).Failed);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(s.BrokerId, Assert.Single(await CertsAsync(check, s.CompanyId, pv.Id)).PayeeContactId);
    }

    [Fact]
    public async Task P3_7_ใบรายเดียว_ระบุผู้ถูกหักไม่ตรง_ปฏิเสธ_ไม่ออกใบ_ระบุตรง_ออกได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await SinglePvAsync(db, s, "PV-P37", null);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Svc(req).AutoGenerateFromDocumentAsync(s.CompanyId, pv.Id, autoIssue: true, "tester", payeeContactId: s.PersonId));
            Assert.Equal(400, ex.StatusCode);
            Assert.Contains("ผู้ถูกหักภาษีรายเดียว", ex.Message);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Empty(await CertsAsync(req, s.CompanyId, pv.Id));
            await Svc(req).AutoGenerateFromDocumentAsync(s.CompanyId, pv.Id, autoIssue: true, "tester", payeeContactId: s.BrokerId);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(s.BrokerId, Assert.Single(await CertsAsync(check, s.CompanyId, pv.Id)).PayeeContactId);
    }

    [Fact]
    public async Task P3_1_ใบลดหนี้ลดฐาน_ใบเดิมเป็นร่าง_ใบที่ออกแทนยังเป็นร่าง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await SinglePvAsync(db, s, "PV-P31", null);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).AutoGenerateFromDocumentAsync(s.CompanyId, pv.Id, autoIssue: false, "tester");
        var cn = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.BrokerId, DocumentType = DocumentType.CreditNote, DocumentNumber = "PV-P31-CN",
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
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(await Svc(req).AdjustCertsForCreditNoteAsync(s.CompanyId, cn.Id, "tester"), n => n.Contains("ออกใบใหม่"));
        using var check = DbTestDatabase.TryCreateContext()!;
        var neo = Assert.Single(await CertsAsync(check, s.CompanyId, pv.Id), c => c.Status != WithholdingTaxCertStatus.Voided);
        Assert.Equal(WithholdingTaxCertStatus.Draft, neo.Status);   // เดิม: "ออกแล้ว" (เข้าแบบยื่นทันทีทั้งที่ผู้ใช้ยังไม่ออกใบ)
        Assert.Equal(18m, neo.TotalTaxAmount);
    }

    [Fact]
    public async Task P2a_มีใบลดหนี้อนุมัติแล้วอ้างอยู่_เปลี่ยนผู้ถูกหัก409_ไม่มีอะไรถูกเขียน_ยกเลิกใบลดหนี้แล้วเปลี่ยนได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        foreach (var (code, name) in new[] { ("21916", "ภาษีหัก ณ ที่จ่ายค้างจ่าย ภ.ง.ด.3"), ("21917", "ภาษีหัก ณ ที่จ่ายค้างจ่าย ภ.ง.ด.53") })
            db.ChartOfAccounts.Add(new ChartOfAccount
            {
                CompanyId = s.CompanyId, AccountCode = code, AccountName = name, AccountType = AccountType.Liability, Level = 4, IsActive = true,
            });
        var pv = await SinglePvAsync(db, s, "PV-P2A", null);
        var cn = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.BrokerId, DocumentType = DocumentType.CreditNote, DocumentNumber = "CN-P2A",
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, RelatedDocumentId = pv.Id,
            SubTotal = 400m, WithholdingTaxAmount = 12m, TotalAmount = 388m, Currency = "THB",
        };
        db.Documents.Add(cn);
        await db.SaveChangesAsync();
        var request = new ChangeWhtPayeeRequest(s.InsurerId, null, "ผู้มีเงินได้คือบริษัทประกัน");

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                DocSvc(req).ChangeWhtPayeeAsync(s.CompanyId, pv.Id, request, "tester", null));
            Assert.Equal(409, ex.StatusCode);
            Assert.Contains("CN-P2A", ex.Message);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Null((await req.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id)).WhtPayeeContactId);

        // ทิศตรงข้าม / ทางไปต่อ: ยกเลิกใบลดหนี้แล้ว ⇒ เปลี่ยนได้ (ด่านไม่ฟ้องใบที่ไม่มีใบลดหนี้มีผล)
        cn.Status = DocumentStatus.Voided;
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
            await DocSvc(req).ChangeWhtPayeeAsync(s.CompanyId, pv.Id, request, "tester", null);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(s.InsurerId, (await check.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id)).WhtPayeeContactId);
    }

    [Fact]
    public async Task P2c_ใบลดหนี้ของใบหลายผู้ถูกหัก_บรรทัดหักไม่อ้างบรรทัดต้นทาง_ล้มดัง_อ้างแล้วผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await MultiPvAsync(db, s, "PV-P2C");
        Document Cn(string no, Guid? sourceLineId)
        {
            var cn = new Document
            {
                CompanyId = s.CompanyId, ContactId = s.BrokerId, DocumentType = DocumentType.CreditNote, DocumentNumber = no,
                Status = DocumentStatus.Draft, DocumentDate = DateTime.UtcNow.Date, RelatedDocumentId = pv.Id,
                SubTotal = 100m, WithholdingTaxAmount = 1m, TotalAmount = 99m, Currency = "THB",
            };
            cn.Lines.Add(new DocumentLine
            {
                LineOrder = 1, Description = "ลดเบี้ยประกัน", Quantity = 1, UnitPrice = 100m, Amount = 100m,
                WithholdingTaxRate = 1m, WithholdingTaxAmount = 1m, IncomeTypeCode = "8", SourceLineId = sourceLineId,
            });
            return cn;
        }
        var unmapped = Cn("CN-P2C-1", null);
        var mapped = Cn("CN-P2C-2", pv.Lines.OrderBy(l => l.LineOrder).First().Id);
        db.Documents.AddRange(unmapped, mapped);
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = DocSvc(req);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.WhtPayeePortionsAsync(s.CompanyId, unmapped));
        Assert.Contains("แปลง", ex.Message);
        var portion = Assert.Single(await svc.WhtPayeePortionsAsync(s.CompanyId, mapped));
        Assert.Equal(s.InsurerId, portion.PayeeContactId);   // ผู้ถูกหักของบรรทัดต้นทาง
    }

    // ══════ ฝ่ายค้านรอบสอง (หลัง 0f428969) — ผ่านเมธอดจริง ══════

    private static async Task SeedWhtAccountsAsync(Accounting.Data.AccountingDbContext db, Guid companyId)
    {
        foreach (var (code, name) in new[] { ("21916", "ภาษีหัก ณ ที่จ่ายค้างจ่าย ภ.ง.ด.3"), ("21917", "ภาษีหัก ณ ที่จ่ายค้างจ่าย ภ.ง.ด.53") })
            db.ChartOfAccounts.Add(new ChartOfAccount
            {
                CompanyId = companyId, AccountCode = code, AccountName = name, AccountType = AccountType.Liability, Level = 4, IsActive = true,
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ผู้ถูกหักสืบทอดถูกตัด_ป้ายต้องตรวจ_อนุมัติไม่ออก50ทวิอัตโนมัติ_อยู่ในลิสต์_รับทราบแล้วออกได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        // ใบที่แปลง/คัดลอก/รายการประจำสร้างหลังผู้ถูกหัก (บริษัทประกัน) ถูกลบ ⇒ ใช้ผู้ติดต่อ (โบรกเกอร์) แทน + ป้าย (WhtPayeeSplit.MarkDroppedInheritedPayees)
        var pv = await SinglePvAsync(db, s, "PV-DROP-1", null);
        pv.InternalNotes = WhtPayeeSplit.MarkDroppedInheritedPayees(pv.InternalNotes, "PI-DROP-1", 1);
        await db.SaveChangesAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var o = await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
            Assert.Equal(WhtCertIssueAction.ManualPending, o.Action);   // เดิม: ออกในนามโบรกเกอร์เงียบ ๆ
            Assert.False(o.Failed);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Empty(await CertsAsync(req, s.CompanyId, pv.Id));
            var row = Assert.Single((await Svc(req).GetPendingDocumentsAsync(s.CompanyId)).Where(p => p.DocumentId == pv.Id));
            Assert.Contains(WhtPayeeSplit.DroppedPayeeTag, row.ReviewReason);
            Assert.StartsWith("50 ทวิ ต้องตรวจ", row.PendingLabel);
            await Svc(req).AcknowledgeReviewAsync(s.CompanyId, pv.Id, "ตรวจแล้ว ผู้ถูกหักคือโบรกเกอร์จริง", null);
        }
        // ทิศตรงข้าม/ทางไปต่อ: รับทราบแล้ว ⇒ ทางอัตโนมัติออกตามเดิม
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.False((await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester")).Failed);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(s.BrokerId, Assert.Single(await CertsAsync(check, s.CompanyId, pv.Id)).PayeeContactId);
    }

    [Fact]
    public async Task เปลี่ยนผู้ถูกหัก_ใบเดิมเป็นร่าง_ใบที่ออกแทนยังเป็นร่าง_ใบเดิมออกแล้วได้ใบออกแล้ว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        await SeedWhtAccountsAsync(db, s.CompanyId);
        var draftPv = await SinglePvAsync(db, s, "PV-CHG-DRAFT", null);
        var issuedPv = await SinglePvAsync(db, s, "PV-CHG-ISSUED", null);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            await Svc(req).AutoGenerateFromDocumentAsync(s.CompanyId, draftPv.Id, autoIssue: false, "tester");
            await Svc(req).AutoGenerateFromDocumentAsync(s.CompanyId, issuedPv.Id, autoIssue: true, "tester");
        }
        var request = new ChangeWhtPayeeRequest(s.InsurerId, null, "ผู้มีเงินได้คือบริษัทประกัน");
        using (var req = DbTestDatabase.TryCreateContext()!)
            await DocSvc(req).ChangeWhtPayeeAsync(s.CompanyId, draftPv.Id, request, "tester", null);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await DocSvc(req).ChangeWhtPayeeAsync(s.CompanyId, issuedPv.Id, request, "tester", null);

        using var check = DbTestDatabase.TryCreateContext()!;
        var neoDraft = Assert.Single(await CertsAsync(check, s.CompanyId, draftPv.Id), c => c.Status != WithholdingTaxCertStatus.Voided);
        Assert.Equal(s.InsurerId, neoDraft.PayeeContactId);
        Assert.Equal(WithholdingTaxCertStatus.Draft, neoDraft.Status);   // เดิม: "ออกแล้ว" ทั้งที่ผู้ใช้ยังไม่ออกใบเดิม
        var neoIssued = Assert.Single(await CertsAsync(check, s.CompanyId, issuedPv.Id), c => c.Status != WithholdingTaxCertStatus.Voided);
        Assert.Equal(s.InsurerId, neoIssued.PayeeContactId);
        Assert.Equal(WithholdingTaxCertStatus.Issued, neoIssued.Status);
    }

    [Fact]
    public async Task เปลี่ยนผู้ถูกหัก_ระบุหัวเอกสารขัดกับผู้ถูกหักรายบรรทัด_400ไม่มีอะไรถูกเขียน_ไม่ระบุหัวยกตามบรรทัด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        await SeedWhtAccountsAsync(db, s.CompanyId);
        var pv = await SinglePvAsync(db, s, "PV-CHG-CONFLICT", null);
        var lineId = pv.Lines.Single().Id;

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => DocSvc(req).ChangeWhtPayeeAsync(s.CompanyId, pv.Id,
                new ChangeWhtPayeeRequest(s.PersonId, new List<ChangeWhtPayeeLine> { new(lineId, s.InsurerId) }, "แก้ผู้ถูกหัก"), "tester", null));
            Assert.Equal(400, ex.StatusCode);
            Assert.Contains("ไม่ตรงกับผู้ถูกหักรายบรรทัด", ex.Message);   // เดิม: หัวเอกสาร (บุคคล) ถูกทับเป็นบริษัทประกันเงียบ ๆ
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var stored = await req.Documents.AsNoTracking().Include(d => d.Lines).FirstAsync(d => d.Id == pv.Id);
            Assert.Null(stored.WhtPayeeContactId);
            Assert.Null(stored.Lines.Single().WhtPayeeContactId);
        }
        // ทิศตรงข้าม: ไม่ระบุหัวเอกสาร (Guid.Empty) + รายบรรทัดรายเดียว ⇒ ยกขึ้นหัวเอกสารตามเดิม
        using (var req = DbTestDatabase.TryCreateContext()!)
            await DocSvc(req).ChangeWhtPayeeAsync(s.CompanyId, pv.Id,
                new ChangeWhtPayeeRequest(Guid.Empty, new List<ChangeWhtPayeeLine> { new(lineId, s.InsurerId) }, "แก้ผู้ถูกหัก"), "tester", null);
        using var check = DbTestDatabase.TryCreateContext()!;
        var after = await check.Documents.AsNoTracking().Include(d => d.Lines).FirstAsync(d => d.Id == pv.Id);
        Assert.Equal(s.InsurerId, after.WhtPayeeContactId);
        Assert.Null(after.Lines.Single().WhtPayeeContactId);
    }
}
