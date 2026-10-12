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
/// คำขอเจ้าของ 2026-10-12 — 50 ทวิ อัตโนมัติผ่านเมธอดจริง (ไม่ใช่แค่ helper · กฎ #4 G) บน PostgreSQL:
/// ค่าตั้งต้นเปิด = ออกใบ · ปิดติ๊ก = ไม่ออก + อยู่ในลิสต์รอออกใบ · ประกันผ่านโบรกเกอร์ = ใบในนามบริษัทประกัน (ภ.ง.ด.53) ·
/// ผู้ถูกหักบุคคลธรรมดา = ภ.ง.ด.3 · ล้ม = ป้ายบนเอกสาร + ลิสต์รอออกใบ + งานของผู้เรียกในธุรกรรมเดียวกันไม่หาย · ซ้ำ = ไม่ออกซ้ำ
/// </summary>
[Trait("Category", "Db")]
public class WhtCertAutoIssueDbTests
{
    private readonly ITestOutputHelper _out;
    public WhtCertAutoIssueDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid BrokerId, Guid InsurerId, Guid PersonId, Guid NoTaxIdId);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบ 50 ทวิ", TaxId = "0105556000025", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        Contact C(string name, string? taxId, ContactType type) => new()
        {
            CompanyId = company.Id, Name = name, TaxId = taxId, ContactType = type, IsSupplier = true,
        };
        var broker = C("บริษัท โบรกเกอร์ประกัน จำกัด", "0105556000017", ContactType.JuristicPerson);
        var insurer = C("บริษัท ประกันภัยทดสอบ จำกัด (มหาชน)", "0107537000017", ContactType.JuristicPerson);
        var person = C("นายสมชาย ใจดี", "3100600123450", ContactType.Individual);
        var noTax = C("ร้านไม่มีเลข", null, ContactType.Unknown);
        db.Contacts.AddRange(broker, insurer, person, noTax);
        await db.SaveChangesAsync();
        return new Seed(company.Id, broker.Id, insurer.Id, person.Id, noTax.Id);
    }

    /// <summary>ใบสำคัญจ่ายที่จ่ายแล้ว ค่าบริการ 1,000 หัก 3% = 30</summary>
    private static async Task<Document> PvAsync(Accounting.Data.AccountingDbContext db, Seed s, string no,
        Guid contactId, Guid? payee = null, bool autoIssue = true)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = contactId, DocumentType = DocumentType.PaymentVoucher, DocumentNumber = no,
            Status = DocumentStatus.Paid, DocumentDate = DateTime.UtcNow.Date, PaymentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, WithholdingTaxAmount = 30m, TotalAmount = 970m, PaidAmount = 970m, BalanceDue = 0m,
            Currency = "THB", WhtCertAutoIssue = autoIssue, WhtPayeeContactId = payee,
        };
        d.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "ค่าเบี้ยประกันภัย", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m,
            WithholdingTaxRate = 3m, WithholdingTaxAmount = 30m, IncomeTypeCode = "8",
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
    public async Task ค่าตั้งต้นเปิด_ออกใบในนามผู้ติดต่อ_และไม่ออกซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-WHT-DEF", s.BrokerId);

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var o = await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
            Assert.False(o.Failed, o.Message);
            Assert.NotNull(o.CertificateId);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var again = await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
            Assert.Equal(WhtCertIssueAction.AlreadyIssued, again.Action);
            Assert.False(again.Failed);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        var cert = Assert.Single(await CertsAsync(check, s.CompanyId, pv.Id));
        Assert.Equal(s.BrokerId, cert.PayeeContactId);
        Assert.Equal(TaxType.WithholdingTax53, cert.TaxFormType);
        Assert.Equal(WithholdingTaxCertStatus.Issued, cert.Status);
        Assert.Equal(30m, cert.TotalTaxAmount);
    }

    [Fact]
    public async Task ประกันผ่านโบรกเกอร์_ใบในนามบริษัทประกัน_ภงด53()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-WHT-BRK", s.BrokerId, payee: s.InsurerId);

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var o = await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
            Assert.False(o.Failed, o.Message);
            var resp = await Svc(req).GetByIdAsync(s.CompanyId, o.CertificateId!.Value);
            Assert.Equal("0107537000017", resp.PayeeTaxId);   // เลขบนใบ = บริษัทประกัน ไม่ใช่โบรกเกอร์
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        var cert = Assert.Single(await CertsAsync(check, s.CompanyId, pv.Id));
        Assert.Equal(s.InsurerId, cert.PayeeContactId);
        Assert.NotEqual(s.BrokerId, cert.PayeeContactId);
        Assert.Equal(TaxType.WithholdingTax53, cert.TaxFormType);
    }

    [Fact]
    public async Task ผู้ถูกหักเป็นบุคคลธรรมดา_ภงด3_ตัดสินจากผู้ถูกหักไม่ใช่ผู้รับเงิน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        // ผู้รับเงิน = บริษัท (โบรกเกอร์) แต่ผู้มีเงินได้ = บุคคลธรรมดา ⇒ ภ.ง.ด.3 (ถ้าดูผู้รับเงินจะได้ 53 ผิดแบบ)
        var pv = await PvAsync(db, s, "PV-WHT-IND", s.BrokerId, payee: s.PersonId);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.False((await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Payment, "tester")).Failed);
        using var check = DbTestDatabase.TryCreateContext()!;
        var cert = Assert.Single(await CertsAsync(check, s.CompanyId, pv.Id));
        Assert.Equal(TaxType.WithholdingTax3, cert.TaxFormType);
        Assert.Equal(s.PersonId, cert.PayeeContactId);
    }

    [Fact]
    public async Task ปิดติ๊ก_ไม่ออกใบ_อยู่ในลิสต์รอออกใบ_กดเองแล้วออกได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-WHT-OFF", s.BrokerId, payee: s.InsurerId, autoIssue: false);

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var svc = Svc(req);
            var o = await svc.TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
            Assert.Equal(WhtCertIssueAction.ManualPending, o.Action);
            Assert.NotNull(o.UserNotice);
            var bulk = await svc.BulkGenerateAsync(s.CompanyId, new BulkGenerateWhtRequest(pv.DocumentDate.Year, pv.DocumentDate.Month, true), "tester");
            Assert.DoesNotContain(bulk.Certificates, c => c.DocumentId == pv.Id);   // "สร้างทั้งเดือน" ไม่กวาดใบที่ตั้งให้ออกเอง
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Empty(await CertsAsync(req, s.CompanyId, pv.Id));
            var row = Assert.Single((await Svc(req).GetPendingDocumentsAsync(s.CompanyId)).Where(p => p.DocumentId == pv.Id));
            Assert.False(row.AutoIssueEnabled);
            Assert.True(row.PayeeDiffersFromContact);
            Assert.Equal(s.InsurerId, row.PayeeContactId);
            Assert.Contains("ออกใบเอง", row.PendingLabel);
        }
        // ทิศตรงข้าม: ปิดอัตโนมัติ ≠ ห้ามออก — ผู้ใช้กด "สร้างใบ" แล้วได้ใบในนามบริษัทประกัน
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var cert = await Svc(req).AutoGenerateFromDocumentAsync(s.CompanyId, pv.Id, autoIssue: true, "tester");
            Assert.Equal(s.InsurerId, cert.PayeeContactId);
        }
    }

    [Fact]
    public async Task ออกไม่สำเร็จ_ป้ายบนเอกสาร_ลิสต์รอออกใบ_งานผู้เรียกในธุรกรรมเดียวกันไม่หาย_แก้แล้วป้ายหาย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-WHT-FAIL", s.NoTaxIdId);

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            await using var tx = await req.Database.BeginTransactionAsync();
            var tracked = await req.Documents.FirstAsync(d => d.Id == pv.Id);
            tracked.Notes = "งานของการอนุมัติ";   // งานค้างของผู้เรียก (ยังไม่ SaveChanges)
            var o = await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
            Assert.True(o.Failed);
            Assert.Contains("13 หลัก", o.FailureReason);
            await req.SaveChangesAsync();
            await tx.CommitAsync();
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var doc = await req.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id);
            Assert.Equal("งานของการอนุมัติ", doc.Notes);                     // ไม่ถูกย้อนทิ้งไปกับ savepoint
            Assert.NotNull(WhtCertAutoFailedNote.ReasonOf(doc.InternalNotes));  // ล้มดังที่ตัวเอกสาร
            Assert.Empty(await CertsAsync(req, s.CompanyId, pv.Id));
            var row = Assert.Single((await Svc(req).GetPendingDocumentsAsync(s.CompanyId)).Where(p => p.DocumentId == pv.Id));
            Assert.NotNull(row.AutoIssueFailedReason);
        }
        // ทางไปต่อของผู้ใช้: เติมเลขที่ผู้ติดต่อ แล้วกด "สร้างใบ" ⇒ ได้ใบ + ป้ายหาย
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var c = await req.Contacts.FirstAsync(x => x.Id == s.NoTaxIdId);
            c.TaxId = "0105556000017";
            await req.SaveChangesAsync();
            await Svc(req).AutoGenerateFromDocumentAsync(s.CompanyId, pv.Id, autoIssue: true, "tester");
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Single(await CertsAsync(check, s.CompanyId, pv.Id));
        var after = await check.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id);
        Assert.Null(WhtCertAutoFailedNote.ReasonOf(after.InternalNotes));
    }
}
