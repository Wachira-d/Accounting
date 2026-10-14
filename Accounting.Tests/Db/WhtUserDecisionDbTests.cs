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
/// รอบ 204 ทีม D — คำสั่งเจ้าของ 2026-10-13 "ระบบห้ามล็อกตาย · ผู้ใช้ตัดสิน · ระบบแนะนำ/อำนวย" ผ่านเมธอดจริงบน PostgreSQL (กฎ #4 G):
/// ทุกการแปลงล็อกเป็นทางเลือกมีเทสต์สองทิศ — <b>ก่อนรับทราบยังถูกกั้น (ไม่มีอะไรถูกเขียน)</b> · <b>รับทราบแล้วไปต่อได้ + ป้าย + audit</b> ·
/// <b>ทางไม่มีคน (อัตโนมัติ) คงพฤติกรรมเดิม</b>
/// </summary>
[Trait("Category", "Db")]
public class WhtUserDecisionDbTests
{
    private readonly ITestOutputHelper _out;
    public WhtUserDecisionDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid BrokerId, Guid InsurerId, Guid PersonId, Guid NoTaxIdId, Guid Insurer2Id);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบผู้ใช้ตัดสิน", TaxId = "0105556000025", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        Contact C(string name, string? taxId, ContactType type) => new()
        {
            CompanyId = company.Id, Name = name, TaxId = taxId, ContactType = type, IsSupplier = true,
        };
        var broker = C("บริษัท โบรกเกอร์ประกัน จำกัด", "0105556000017", ContactType.JuristicPerson);
        var insurer = C("บริษัท ประกันภัยทดสอบ จำกัด (มหาชน)", "0107537000017", ContactType.JuristicPerson);
        var person = C("นายสมชาย ใจดี", "3100600123450", ContactType.Individual);
        var noTaxId = C("ร้านไม่มีเลขผู้เสียภาษี", null, ContactType.Individual);
        var insurer2 = C("บริษัท ประกันชีวิตทดสอบ จำกัด (มหาชน)", "0107536000056", ContactType.JuristicPerson);
        db.Contacts.AddRange(broker, insurer, person, noTaxId, insurer2);
        foreach (var (code, name) in new[] { ("21916", "ภาษีหัก ณ ที่จ่ายค้างจ่าย ภ.ง.ด.3"), ("21917", "ภาษีหัก ณ ที่จ่ายค้างจ่าย ภ.ง.ด.53") })
            db.ChartOfAccounts.Add(new ChartOfAccount
            {
                CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = AccountType.Liability, Level = 4, IsActive = true,
            });
        await db.SaveChangesAsync();
        return new Seed(company.Id, broker.Id, insurer.Id, person.Id, noTaxId.Id, insurer2.Id);
    }

    private static async Task<Document> SinglePvAsync(Accounting.Data.AccountingDbContext db, Seed s, string no, Guid contactId)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = contactId, DocumentType = DocumentType.PaymentVoucher, DocumentNumber = no,
            Status = DocumentStatus.Paid, DocumentDate = DateTime.UtcNow.Date, PaymentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, WithholdingTaxAmount = 30m, TotalAmount = 970m, PaidAmount = 970m, BalanceDue = 0m, Currency = "THB",
        };
        d.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m,
            WithholdingTaxRate = 3m, WithholdingTaxAmount = 30m, IncomeTypeCode = "8",
        });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    private static WithholdingTaxCertService Svc(Accounting.Data.AccountingDbContext db)
        => new(db, null!, null!, NullLogger<WithholdingTaxCertService>.Instance);

    private static DocumentService DocSvc(Accounting.Data.AccountingDbContext db)
        => new(db, new AccountingService(db), null!, Svc(db), null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static Task<List<WithholdingTaxCert>> CertsAsync(Accounting.Data.AccountingDbContext db, Guid companyId, Guid docId)
        => db.WithholdingTaxCerts.AsNoTracking().Include(w => w.Lines)
            .Where(w => w.CompanyId == companyId && w.DocumentId == docId).ToListAsync();

    private static Task<bool> AuditHasAsync(Accounting.Data.AccountingDbContext db, Guid companyId, string ruleCode)
        => db.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == companyId && a.NewValues != null && a.NewValues.Contains(ruleCode));

    /// <summary>PV ที่ออก 50 ทวิ (ออกแล้ว) แล้ว + รายงาน ภ.ง.ด. ของเดือนนั้นยื่นแล้ว</summary>
    private static async Task<(Document Pv, WithholdingTaxCert Cert)> FiledPvAsync(Accounting.Data.AccountingDbContext db, Seed s, string no)
    {
        var pv = await SinglePvAsync(db, s, no, s.BrokerId);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.False((await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester")).Failed);
        var cert = Assert.Single(await CertsAsync(db, s.CompanyId, pv.Id));
        db.TaxReports.Add(new TaxReport
        {
            CompanyId = s.CompanyId, TaxType = cert.TaxFormType, Year = cert.TaxYear, Month = cert.TaxMonth,
            Status = TaxReportStatus.Filed, FiledDate = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (pv, cert);
    }

    // ══════ #23 ยกเลิก 50 ทวิ ที่อยู่ในแบบที่ยื่นแล้ว ══════

    [Fact]
    public async Task ยกเลิก50ทวิที่ยื่นแล้ว_ไม่รับทราบ409_ไม่มีสิทธิ์403_รับทราบแล้วยกเลิกได้_ป้ายยื่นเพิ่มเติม_audit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var (pv, cert) = await FiledPvAsync(db, s, "PV-D-VOID");

        // ทางเดิม (ไม่มีคนรับทราบ — ยกเลิกเอกสาร/รอบโอน): 409 รหัสเดิม พร้อมทางไปต่อ
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).VoidAsync(s.CompanyId, cert.Id));
            Assert.Equal(409, ex.StatusCode);
            Assert.Equal("RD-50TWI-FILED", ex.RuleCode);
            Assert.Contains("รับทราบ", ex.Message);
        }
        // รับทราบแต่ไม่ถือ Tax.File ⇒ 403 · ไม่มีเหตุผล ⇒ 400
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Svc(req).VoidAsync(s.CompanyId, cert.Id, new WhtCertVoidRequest(true, "ออกผิด"), null, holdsTaxFile: false));
            Assert.Equal(403, ex.StatusCode);
            var ex2 = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Svc(req).VoidAsync(s.CompanyId, cert.Id, new WhtCertVoidRequest(true, " "), null, holdsTaxFile: true));
            Assert.Equal(400, ex2.StatusCode);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.NotEqual(WithholdingTaxCertStatus.Voided, (await req.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == cert.Id)).Status);

        // ทิศที่ไปต่อได้: ผู้ถือ Tax.File รับทราบ + เหตุผล
        string? label;
        using (var req = DbTestDatabase.TryCreateContext()!)
            label = await Svc(req).VoidAsync(s.CompanyId, cert.Id, new WhtCertVoidRequest(true, "ออกผิดผู้ถูกหัก — ยื่นเพิ่มเติม"), null, holdsTaxFile: true);
        Assert.Equal(WhtFiledPeriodAck.SupplementaryFilingLabel(cert.TaxFormType, cert.TaxYear, cert.TaxMonth), label);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(WithholdingTaxCertStatus.Voided, (await check.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == cert.Id)).Status);
        Assert.True(await AuditHasAsync(check, s.CompanyId, WhtFiledPeriodAck.VoidAckRuleCode));
        var doc = await check.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id);
        Assert.Contains(label!, WhtCertReviewNote.ReasonOf(doc.InternalNotes));
    }

    [Fact]
    public async Task ยกเลิก50ทวิที่ยังไม่ยื่น_ไม่ต้องรับทราบ_ไม่มีป้าย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await SinglePvAsync(db, s, "PV-D-VOID-OPEN", s.BrokerId);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.False((await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester")).Failed);
        var cert = Assert.Single(await CertsAsync(db, s.CompanyId, pv.Id));
        string? label;
        using (var req = DbTestDatabase.TryCreateContext()!)
            label = await Svc(req).VoidAsync(s.CompanyId, cert.Id, null);
        Assert.Null(label);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(WithholdingTaxCertStatus.Voided, (await check.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == cert.Id)).Status);
        Assert.Null(WhtCertReviewNote.ReasonOf((await check.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id)).InternalNotes));
    }

    // ══════ #8c เปลี่ยนผู้ถูกหักแตะงวดที่ยื่นแล้ว ══════

    [Fact]
    public async Task เปลี่ยนผู้ถูกหักงวดยื่นแล้ว_ไม่รับทราบ409ไม่มีอะไรถูกเขียน_ไม่มีสิทธิ์403_รับทราบแล้วไปต่อ_ป้าย_audit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var (pv, cert) = await FiledPvAsync(db, s, "PV-D-CHG-FILED");
        var request = new ChangeWhtPayeeRequest(s.InsurerId, null, "ผู้มีเงินได้คือบริษัทประกัน");

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                DocSvc(req).ChangeWhtPayeeAsync(s.CompanyId, pv.Id, request, "tester", null));
            Assert.Equal(409, ex.StatusCode);
            Assert.Equal(WhtFiledPeriodAck.PayeeChangeNeedsAckRuleCode, ex.RuleCode);
            Assert.Contains("รับทราบ", ex.Message);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => DocSvc(req).ChangeWhtPayeeAsync(s.CompanyId, pv.Id,
                request with { AcknowledgeFiledPeriod = true }, "tester", null, holdsTaxFile: false));
            Assert.Equal(403, ex.StatusCode);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Null((await req.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id)).WhtPayeeContactId);
            Assert.NotEqual(WithholdingTaxCertStatus.Voided, (await req.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == cert.Id)).Status);
        }

        ChangeWhtPayeeResponse res;
        using (var req = DbTestDatabase.TryCreateContext()!)
            res = await DocSvc(req).ChangeWhtPayeeAsync(s.CompanyId, pv.Id, request with { AcknowledgeFiledPeriod = true }, "tester", null,
                holdsTaxFile: true);
        var label = WhtFiledPeriodAck.SupplementaryFilingLabel(cert.TaxFormType, cert.TaxYear, cert.TaxMonth);
        Assert.Contains(res.Notices, n => n.Contains(label));
        Assert.Contains(cert.CertificateNumber, res.VoidedCertificates);
        using var check = DbTestDatabase.TryCreateContext()!;
        var doc = await check.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id);
        Assert.Equal(s.InsurerId, doc.WhtPayeeContactId);
        Assert.Contains(label, WhtCertReviewNote.ReasonOf(doc.InternalNotes));
        Assert.True(await AuditHasAsync(check, s.CompanyId, WhtFiledPeriodAck.PayeeChangeAckRuleCode));
        Assert.Contains(await CertsAsync(check, s.CompanyId, pv.Id),
            c => c.Status != WithholdingTaxCertStatus.Voided && c.PayeeContactId == s.InsurerId);
    }

    [Fact]
    public async Task เปลี่ยนผู้ถูกหักงวดยังไม่ยื่น_ไม่ต้องรับทราบ_ไม่มีป้ายยื่นเพิ่มเติม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await SinglePvAsync(db, s, "PV-D-CHG-OPEN", s.BrokerId);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.False((await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester")).Failed);
        ChangeWhtPayeeResponse res;
        using (var req = DbTestDatabase.TryCreateContext()!)
            res = await DocSvc(req).ChangeWhtPayeeAsync(s.CompanyId, pv.Id, new ChangeWhtPayeeRequest(s.InsurerId, null, "แก้ผู้ถูกหัก"),
                "tester", null);
        Assert.DoesNotContain(res.Notices, n => n.Contains("เพิ่มเติม งวด"));
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Null(WhtCertReviewNote.ReasonOf((await check.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id)).InternalNotes));
        Assert.False(await AuditHasAsync(check, s.CompanyId, WhtFiledPeriodAck.PayeeChangeAckRuleCode));
    }

    // ══════ #7 หัวเอกสารขัดกับรายบรรทัด ══════

    [Fact]
    public async Task เปลี่ยนผู้ถูกหัก_หัวขัดกับรายบรรทัด_รหัสแยกพร้อมสองฝั่ง_เลือกหัวทั้งใบแล้วผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await SinglePvAsync(db, s, "PV-D-HDR", s.BrokerId);
        var lineId = pv.Lines.Single().Id;
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => DocSvc(req).ChangeWhtPayeeAsync(s.CompanyId, pv.Id,
                new ChangeWhtPayeeRequest(s.PersonId, new List<ChangeWhtPayeeLine> { new(lineId, s.InsurerId) }, "แก้ผู้ถูกหัก"), "tester", null));
            Assert.Equal(400, ex.StatusCode);
            Assert.Equal(WhtPayeeSplit.HeaderConflictRuleCode, ex.RuleCode);
            var det = Assert.IsType<Dictionary<string, object?>>(ex.Data[WhtPayeeSplit.DetailsKey]);
            Assert.Equal(s.PersonId, det["headerPayeeId"]);
            Assert.Equal(s.InsurerId, det["linePayeeId"]);
        }
        // ผู้ใช้เลือก "ใช้หัวเอกสารทั้งใบ (ล้างรายบรรทัด)" ⇒ ส่งใหม่ผ่าน
        using (var req = DbTestDatabase.TryCreateContext()!)
            await DocSvc(req).ChangeWhtPayeeAsync(s.CompanyId, pv.Id,
                new ChangeWhtPayeeRequest(s.PersonId, new List<ChangeWhtPayeeLine> { new(lineId, Guid.Empty) }, "แก้ผู้ถูกหัก"), "tester", null);
        using var check = DbTestDatabase.TryCreateContext()!;
        var after = await check.Documents.AsNoTracking().Include(d => d.Lines).FirstAsync(d => d.Id == pv.Id);
        Assert.Equal(s.PersonId, after.WhtPayeeContactId);
        Assert.Null(after.Lines.Single().WhtPayeeContactId);
    }

    // ══════ #20 ผู้ถูกหักไม่มีเลขผู้เสียภาษี ══════

    [Fact]
    public async Task ไม่มีเลขผู้เสียภาษี_คนกดสร้างใบได้ร่างพร้อมคำเตือน_ทางอัตโนมัติยังล้มเข้าลิสต์()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var autoPv = await SinglePvAsync(db, s, "PV-D-NOTAX-AUTO", s.NoTaxIdId);
        var manualPv = await SinglePvAsync(db, s, "PV-D-NOTAX-MANUAL", s.NoTaxIdId);

        // ทางอัตโนมัติ (อนุมัติ) — พฤติกรรมเดิม: ไม่ออกใบ · ล้มดัง (ป้าย + ลิสต์รอออกใบ)
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.True((await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, autoPv.Id, WhtCertIssueTrigger.Approve, "tester")).Failed);
        // คนขอออกทันที — ยังปฏิเสธ (ใบที่ออกแล้วเข้าแบบยื่น ต้องมีเลข)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).AutoGenerateFromDocumentAsync(s.CompanyId, manualPv.Id,
                autoIssue: true, "tester", manualRequest: true));
        // คนกด "สร้างใบ" (ไม่ออกทันที) — ได้ร่าง + คำเตือน + audit
        WithholdingTaxCertResponse created;
        using (var req = DbTestDatabase.TryCreateContext()!)
            created = await Svc(req).AutoGenerateFromDocumentAsync(s.CompanyId, manualPv.Id, autoIssue: false, "tester", manualRequest: true);
        Assert.Equal(WithholdingTaxCertStatus.Draft, created.Status);
        Assert.Contains(created.Warnings!, w => w.StartsWith(WhtCertPayeeTaxIdDraft.Warning));

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Empty(await CertsAsync(check, s.CompanyId, autoPv.Id));
        Assert.Equal(WithholdingTaxCertStatus.Draft, Assert.Single(await CertsAsync(check, s.CompanyId, manualPv.Id)).Status);
        Assert.True(await check.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityType == "WithholdingTaxCert"
            && a.EntityId == created.Id.ToString() && a.NewValues != null && a.NewValues.Contains(WhtCertIssuePlan.PayeeRuleCode)));
    }

    // ══════ #21 แบบ ภ.ง.ด. ที่ระบบแก้ — แจ้งในคำตอบ ══════

    [Fact]
    public async Task สร้างใบมือเลือกแบบผิด_ระบบแก้แบบและบอก_เลือกถูก_ไม่มีข้อความ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var line = new WithholdingTaxCertLineRequest("8", "ค่าบริการ", DateTime.UtcNow.Date, 1_000m, 3m, 30m, null);
        WithholdingTaxCertResponse wrong, right;
        using (var req = DbTestDatabase.TryCreateContext()!)
            wrong = await Svc(req).CreateAsync(s.CompanyId, new CreateWithholdingTaxCertRequest(s.InsurerId, TaxType.WithholdingTax3,
                DateTime.UtcNow.Year, DateTime.UtcNow.Month, WithholdingTaxCertType.Withhold, new List<WithholdingTaxCertLineRequest> { line }), "tester");
        Assert.Equal(TaxType.WithholdingTax53, wrong.TaxFormType);
        Assert.Contains("ภ.ง.ด.53", wrong.FormCorrectionNotice);
        using (var req = DbTestDatabase.TryCreateContext()!)
            right = await Svc(req).CreateAsync(s.CompanyId, new CreateWithholdingTaxCertRequest(s.InsurerId, TaxType.WithholdingTax53,
                DateTime.UtcNow.Year, DateTime.UtcNow.Month, WithholdingTaxCertType.Withhold, new List<WithholdingTaxCertLineRequest> { line }), "tester");
        Assert.Null(right.FormCorrectionNotice);
    }

    // ══════ #10 ใบลดหนี้ไม่อ้างบรรทัดของใบหลายผู้ถูกหักที่ลงผังเดียวกัน ══════

    [Fact]
    public async Task ใบลดหนี้ไม่อ้างบรรทัด_ผู้ถูกหักทุกรายผังเดียวกัน_ไม่บล็อก_ใบต้นทางติดป้ายต้องตรวจ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        // ใบต้นทางหลายผู้ถูกหัก — ทั้งสองรายเป็นนิติบุคคล (ภ.ง.ด.53 · 21917) และหัวเอกสาร (โบรกเกอร์) ก็นิติบุคคล
        var pv = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.BrokerId, DocumentType = DocumentType.PaymentVoucher, DocumentNumber = "PV-D-SAME",
            Status = DocumentStatus.Paid, DocumentDate = DateTime.UtcNow.Date, PaymentDate = DateTime.UtcNow.Date,
            SubTotal = 2_000m, WithholdingTaxAmount = 20m, TotalAmount = 1_980m, PaidAmount = 1_980m, BalanceDue = 0m, Currency = "THB",
        };
        pv.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "เบี้ยประกันภัย", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m,
            WithholdingTaxRate = 1m, WithholdingTaxAmount = 10m, IncomeTypeCode = "8", WhtPayeeContactId = s.InsurerId,
        });
        pv.Lines.Add(new DocumentLine
        {
            LineOrder = 2, Description = "เบี้ยประกันชีวิต", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m,
            WithholdingTaxRate = 1m, WithholdingTaxAmount = 10m, IncomeTypeCode = "8", WhtPayeeContactId = s.Insurer2Id,
        });
        db.Documents.Add(pv);
        var cn = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.BrokerId, DocumentType = DocumentType.CreditNote, DocumentNumber = "CN-D-SAME",
            Status = DocumentStatus.Draft, DocumentDate = DateTime.UtcNow.Date, RelatedDocumentId = pv.Id,
            SubTotal = 100m, WithholdingTaxAmount = 1m, TotalAmount = 99m, Currency = "THB",
        };
        cn.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "ลดเบี้ย", Quantity = 1, UnitPrice = 100m, Amount = 100m,
            WithholdingTaxRate = 1m, WithholdingTaxAmount = 1m, IncomeTypeCode = "8",
        });
        db.Documents.Add(cn);
        await db.SaveChangesAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var portions = await DocSvc(req).WhtPayeePortionsAsync(s.CompanyId, cn);   // เดิม: โยน "แปลง" แม้ผังเดียวกัน
            Assert.Equal(s.BrokerId, Assert.Single(portions).PayeeContactId);         // บรรทัดไม่อ้าง ⇒ ผู้ถูกหักหัวเอกสารต้นทาง (ผังเดียวกัน)
            await req.SaveChangesAsync();
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        var src = await check.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id);
        Assert.Contains("21917", WhtCertReviewNote.ReasonOf(src.InternalNotes));
        Assert.Contains("CN-D-SAME", WhtCertReviewNote.ReasonOf(src.InternalNotes));
    }
}
