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
/// รอบ 275 แพ็ก E — บน PostgreSQL จริง:
/// (1) H-3 + คำตัดสิน 4.2: พนักงานต่างชาติไม่มีเลข 13 หลัก มีภาษีหัก ⇒ ก่อนรับทราบ 50 ทวิ ไม่ออก + ไฟล์ ภ.ง.ด.1 กันแถว ·
///     รับทราบ (audit) ⇒ "สร้างเอกสารใหม่" ออกใบได้ (ผู้ติดต่อไม่มีเลข ผูกกับพนักงาน) · ข้อความประกอบมีพาสปอร์ต · ไฟล์ลงเลข 0 ·
///     ทิศตรงข้าม: เลข checksum ผิดรับทราบไม่ได้ · คนที่มีเลขถูกได้ใบเลขของตัวเองเหมือนเดิม
/// (2) H-2: ผู้ติดต่อเลขขยะจากเงินเดือน ⇒ รายงานเห็น · ภ.ง.ด.3 ใช้ไม่ได้ (ด่าน SaveChanges) · ผู้ขายจริงเลขไม่ครบไม่ถูกกันที่นี่ ·
///     ใบในงวดที่ยื่นแล้ว: ซ่อมไม่ได้จนกว่าจะยืนยันยื่นเพิ่มเติม · ยืนยันแล้ว: เลขถูก + สำเนาบนใบถูก + audit · กรณีมีผู้ติดต่อเลขถูกอยู่แล้ว: ย้ายใบ + ปิดใช้ตัวขยะ
/// </summary>
[Trait("Category", "Db")]
public class PayrollCleanupRound275EDbTests
{
    private readonly ITestOutputHelper _out;
    public PayrollCleanupRound275EDbTests(ITestOutputHelper output) => _out = output;

    private const string ValidA = "1101700001237";
    private const string ValidB = "3100500123458";
    private static readonly DateTime Period = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);

    private static async Task<(Guid Cid, Guid RunId, Employee Foreign, Employee Thai)> SeedAsync(Accounting.Data.AccountingDbContext db, string? foreignCitizen = null)
    {
        var company = new Company { Name = "บริษัททดสอบ 275E", TaxId = "0105561012346", BranchCode = "00000" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cid = company.Id;
        var foreign = new Employee { CompanyId = cid, EmployeeCode = "F001", FirstNameTh = "", LastNameTh = "", FirstNameEn = "John", LastNameEn = "Smith",
            Nationality = "British", PassportNumber = "GB7654321", CitizenId = foreignCitizen, StartDate = Period.AddYears(-1),
            Address = "1 ถนนสุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพมหานคร 10110" };
        var thai = new Employee { CompanyId = cid, EmployeeCode = "E001", TitleTh = "นาย", FirstNameTh = "สมชาย", LastNameTh = "ใจดี",
            CitizenId = ValidA, StartDate = Period.AddYears(-1), Address = "2 ถนนพหลโยธิน แขวงจตุจักร เขตจตุจักร กรุงเทพมหานคร 10900" };
        db.Set<Employee>().AddRange(foreign, thai);
        var end = Period.AddMonths(1).AddDays(-1);
        var run = new PayrollRun { CompanyId = cid, PayrollNumber = "PR-R275E", Name = "275E", Year = Period.Year, Month = Period.Month,
            PeriodStart = Period, PeriodEnd = end, PayDate = end, Status = PayrollRunFilingScope.Paid };
        foreach (var e in new[] { foreign, thai })
            run.Details.Add(new PayrollDetail { CompanyId = cid, EmployeeId = e.Id, BaseSalary = 80000m, GrossIncome = 80000m, TaxableGross = 80000m,
                WithholdingTax = 5000m, NetPay = 75000m });
        PayrollDetailAmounts.RecomputeRunTotals(run);
        db.Set<PayrollRun>().Add(run);
        await db.SaveChangesAsync();
        return (cid, run.Id, foreign, thai);
    }

    [Fact]
    public async Task ไม่มีเลข13หลัก_รับทราบรายคน_แล้ว50ทวิออกได้_ไฟล์ลงเลขศูนย์_ทิศตรงข้ามเลขผิดรับทราบไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, runId, foreign, thai) = await SeedAsync(db);
        var user = new User { Email = $"r275e-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้ทำบัญชี 275E" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // ── ก่อนรับทราบ: คนไทยได้ใบ · ต่างชาติไม่ได้ · ไฟล์กันแถวต่างชาติ (ตัวตัดสินเดียวกัน) ──
        using (var r0 = DbTestDatabase.TryCreateContext()!)
            await new PayrollService(r0).GeneratePostPaymentArtifactsAsync(cid, runId, user.Id.ToString());
        using (var c0 = DbTestDatabase.TryCreateContext()!)
        {
            var certs = await c0.WithholdingTaxCerts.Include(w => w.PayeeContact).Where(w => w.CompanyId == cid && w.Status != WithholdingTaxCertStatus.Voided).ToListAsync();
            var only = Assert.Single(certs);
            Assert.Equal(ValidA, only.PayeeContact.TaxId);
            var file = await Pnd1FilingData.LoadMonthAsync(c0, cid, Period.Year, Period.Month);
            Assert.Equal("F001", Assert.Single(file.BlockedRows).SourceRef);
            var list = await EmployeeTaxIdWaiver.ListAsync(c0, cid);
            var row = Assert.Single(list);
            Assert.Equal(nameof(EmployeeTaxIdentity.FilingKind.NeedsAcknowledgement), row.Kind);
            Assert.True(row.CanAcknowledge);
            Assert.DoesNotContain("7654", row.PassportMasked ?? "");   // ปิดบัง
        }

        // ── รับทราบ (เหตุบังคับ + audit) ──
        using (var a = DbTestDatabase.TryCreateContext()!)
        {
            await Assert.ThrowsAsync<BusinessRuleException>(() => EmployeeTaxIdWaiver.AcknowledgeAsync(a, cid, foreign.Id, "สั้น", user.Id));
            await EmployeeTaxIdWaiver.AcknowledgeAsync(a, cid, foreign.Id, "ต่างชาติ รอเลขจากกรมสรรพากร", user.Id);
            Assert.True(await a.AuditLogs.AnyAsync(l => l.CompanyId == cid && l.EntityType == "Employee.TaxIdWaiver" && l.EntityId == foreign.Id.ToString()));
        }

        // ── "สร้างเอกสารใหม่" ⇒ ใบของต่างชาติออก · ผู้ติดต่อไม่มีเลข (ชื่ออังกฤษ) ผูกกับพนักงาน · ข้อความประกอบมีพาสปอร์ต ──
        using (var r1 = DbTestDatabase.TryCreateContext()!)
            await new PayrollService(r1).GeneratePostPaymentArtifactsAsync(cid, runId, user.Id.ToString());
        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            var certs = await c1.WithholdingTaxCerts.Include(w => w.PayeeContact)
                .Where(w => w.CompanyId == cid && w.Status != WithholdingTaxCertStatus.Voided).ToListAsync();
            Assert.Equal(2, certs.Count);
            var fc = certs.Single(w => w.CertificateNumber.Contains("F001"));
            Assert.Null(fc.PayeeContact.TaxId);
            Assert.Equal("John Smith", fc.PayeeContact.Name);
            var emp = await c1.Set<Employee>().AsNoTracking().SingleAsync(e => e.Id == foreign.Id);
            Assert.Equal(fc.PayeeContactId, emp.ContactId);
            // ข้อความประกอบจากสำเนา ณ วันออก (LOW (ข)(ฉ)) — เต็มบน PDF · ปิดบังบน DTO
            Assert.Contains("GB7654321", WhtCertPayeeNote.Of(fc, includePii: true));
            Assert.DoesNotContain("7654", WhtCertPayeeNote.Of(fc, includePii: false));
            var tc = certs.Single(w => w.CertificateNumber.Contains("E001"));
            Assert.Null(WhtCertPayeeNote.Of(tc, includePii: true));   // ทิศตรงข้าม: คนมีเลขไม่มีข้อความประกอบ
            var file = await Pnd1FilingData.LoadMonthAsync(c1, cid, Period.Year, Period.Month);
            Assert.Empty(file.BlockedRows);
            Assert.Equal(2, file.Rows.Count);
            Assert.Contains(file.Rows, r => r.PayeeTaxId == null && r.FirstName == "John");
            Assert.Contains(file.Rows, r => r.PayeeTaxId == ValidA);
            // ด่านนำส่ง: ใบครบตามแถวที่หักภาษี (Pnd1CertCoverage ไม่มีช่องโหว่)
            var gap = Pnd1CertCoverage.Evaluate(2, 10000m, certs.Count, certs.Sum(c => c.TotalTaxAmount));
            Assert.False(gap.Any);
        }

        // ── ทิศตรงข้าม: เลข checksum ผิด รับทราบไม่ได้ ──
        using (var b = DbTestDatabase.TryCreateContext()!)
        {
            var bad = new Employee { CompanyId = cid, EmployeeCode = "E002", FirstNameTh = "เลข", LastNameTh = "ผิด", CitizenId = "1101700001238", StartDate = Period };
            b.Set<Employee>().Add(bad);
            await b.SaveChangesAsync();
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => EmployeeTaxIdWaiver.AcknowledgeAsync(b, cid, bad.Id, "พยายามข้ามเลขผิด", user.Id));
            Assert.Contains("checksum", ex.Message);
        }

        // ── LOW (จ): ถอนต้องมีเหตุ · LOW (ง): ได้เลขถูกแล้วการรับทราบถูกล้าง (ไม่กลับมามีผลถ้าช่องว่างภายหลัง) ──
        using (var r = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => EmployeeTaxIdWaiver.RevokeAsync(r, cid, foreign.Id, "", user.Id));
        using (var u = DbTestDatabase.TryCreateContext()!)
        {
            var e = await u.Set<Employee>().SingleAsync(x => x.Id == foreign.Id);
            e.TaxId = ValidB;
            await u.SaveChangesAsync();
            e.TaxId = null;
            await u.SaveChangesAsync();
        }
        using (var w = DbTestDatabase.TryCreateContext()!)
        {
            var e = await w.Set<Employee>().AsNoTracking().SingleAsync(x => x.Id == foreign.Id);
            Assert.Null(e.TaxIdWaiverAcknowledgedAt);
            Assert.Equal(EmployeeTaxIdentity.FilingKind.NeedsAcknowledgement, EmployeeTaxIdentity.ForFiling(e).Kind);
            Assert.True(await w.AuditLogs.AnyAsync(l => l.CompanyId == cid && l.EntityType == "Employee.TaxIdWaiver" && l.NewValues!.Contains("cleared")));
        }
    }

    private static async Task<(Guid Cid, Guid Junk, Guid CertId)> SeedJunkAsync(Accounting.Data.AccountingDbContext db, bool filed, bool goodContactExists)
    {
        var (cid, runId, _, thai) = await SeedAsync(db, foreignCitizen: ValidB);
        // ของเสียก่อนรอบ 274: ผู้ติดต่อเลข 7 หลักที่ตัดจาก ciphertext + 50 ทวิ ของรอบเงินเดือนที่ชี้ไปตัวนั้น (สำเนา ณ วันออกเป็นเลขขยะ)
        var junk = new Contact { CompanyId = cid, Name = "นาย สมชาย ใจดี", TaxId = "1234567", ContactType = ContactType.Individual, IsSupplier = true };
        db.Contacts.Add(junk);
        if (goodContactExists)
            db.Contacts.Add(new Contact { CompanyId = cid, Name = "นาย สมชาย ใจดี", TaxId = ValidA, ContactType = ContactType.Individual, IsSupplier = true });
        await db.SaveChangesAsync();
        var cert = new WithholdingTaxCert
        {
            CompanyId = cid, CertificateNumber = $"PND1-{Period.Year}{Period.Month:D2}-E001", PayeeContactId = junk.Id, TaxFormType = TaxType.WithholdingTax1,
            TaxYear = Period.Year, TaxMonth = Period.Month, CertificateType = WithholdingTaxCertType.Withhold, Status = WithholdingTaxCertStatus.Issued,
            TotalIncomeAmount = 80000m, TotalTaxAmount = 5000m, IssuedDate = DateTime.UtcNow.AddDays(-20), SourcePayrollRunId = runId,
        };
        db.WithholdingTaxCerts.Add(cert);
        if (filed)
            db.TaxReports.Add(new TaxReport { CompanyId = cid, TaxType = TaxType.WithholdingTax1, Year = Period.Year, Month = Period.Month, Status = TaxReportStatus.Filed });
        await db.SaveChangesAsync();
        Assert.Contains("1234567", cert.PayeeSnapshotJson);
        return (cid, junk.Id, cert.Id);
    }

    [Fact]
    public async Task ผู้ติดต่อเลขขยะจากเงินเดือน_กันภงด3_งวดยื่นแล้วต้องยืนยันยื่นเพิ่มเติม_แล้วซ่อมเลขและสำเนาบนใบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        var (cid, junkId, certId) = await SeedJunkAsync(db, filed: true, goodContactExists: false);

        // ── รายงานเห็น + แผน FixTaxId + งวดที่ยื่นแล้ว ──
        var rows = await PayrollJunkPayee.FindAsync(db, cid);
        var row = Assert.Single(rows);
        Assert.Equal(junkId, row.ContactId);
        Assert.Equal(PayrollJunkPayee.Plan.FixTaxId, row.Plan);
        Assert.Equal("E001", row.EmployeeCode);
        Assert.Single(row.FiledPeriods);
        Assert.Contains("ยื่น ภ.ง.ด.1 เพิ่มเติม", row.Action);

        // ── รายงานสุขภาพ: ทางไปต่อของใบในงวดที่ยื่นแล้ว = เครื่องมือซ่อม (ไม่ใช่ "สร้างเอกสารใหม่" ที่ระบบปฏิเสธ) ──
        var health = await EmployeeIdentityHealth.BuildAsync(db, cid);
        var hr = Assert.Single(health.Rows, r => r.Source == "หนังสือรับรอง 50 ทวิ");
        Assert.Equal(EmployeeIdentityHealth.Issue.PayeeContactInvalid, hr.Issue);
        Assert.Contains("ถูกปฏิเสธ", hr.Action);

        // ── ด่านกัน: ภ.ง.ด.3 ด้วยผู้ติดต่อขยะ ⇒ ปฏิเสธ · ทิศตรงข้าม: ผู้ขายจริงเลขไม่ครบ (ไม่ได้มาจากเงินเดือน) ไม่ถูกกันที่นี่ ──
        using (var g = DbTestDatabase.TryCreateContext()!)
        {
            g.WithholdingTaxCerts.Add(new WithholdingTaxCert { CompanyId = cid, CertificateNumber = "WHT-275E-0001", PayeeContactId = junkId,
                TaxFormType = TaxType.WithholdingTax3, TaxYear = Period.Year, TaxMonth = Period.Month, Status = WithholdingTaxCertStatus.Draft });
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => g.SaveChangesAsync());
            Assert.Equal(PayrollJunkPayee.QuarantineRuleCode, ex.RuleCode);
        }
        using (var g2 = DbTestDatabase.TryCreateContext()!)
        {
            var vendor = new Contact { CompanyId = cid, Name = "ร้านเลขไม่ครบ", TaxId = "12345", ContactType = ContactType.Individual, IsSupplier = true };
            g2.Contacts.Add(vendor);
            await g2.SaveChangesAsync();
            g2.WithholdingTaxCerts.Add(new WithholdingTaxCert { CompanyId = cid, CertificateNumber = "WHT-275E-0002", PayeeContactId = vendor.Id,
                TaxFormType = TaxType.WithholdingTax3, TaxYear = Period.Year, TaxMonth = Period.Month, Status = WithholdingTaxCertStatus.Draft });
            await g2.SaveChangesAsync();
            Assert.Empty((await PayrollJunkPayee.FindAsync(g2, cid)).Where(r => r.ContactId == vendor.Id));
        }

        // ── ซ่อมโดยไม่ยืนยันยื่นเพิ่มเติม ⇒ ไม่แตะ ──
        using (var r1 = DbTestDatabase.TryCreateContext()!)
        {
            var o = Assert.Single(await PayrollJunkPayee.RepairAsync(r1, cid, new[] { junkId }, confirmedPeriods: new[] { "1999-01" }, userId: null));
            Assert.False(o.Repaired);
            Assert.Contains("ยืนยัน", o.Message);
            Assert.Equal("1234567", (await r1.Contacts.AsNoTracking().SingleAsync(c => c.Id == junkId)).TaxId);
        }
        // ── ยืนยันงวดนั้น ⇒ เลขถูก · MED-3: ใบเดิมไม่ถูกเขียนทับ (สำเนาเดิมคงอยู่ · ยกเลิก-แทนด้วยใบใหม่) · ใบแทนเลขที่ใหม่ยอดเดิม · รายการค้างยื่นเพิ่มเติม · audit ──
        var period = $"{Period.Year:D4}-{Period.Month:D2}";
        using (var r2 = DbTestDatabase.TryCreateContext()!)
        {
            var o = Assert.Single(await PayrollJunkPayee.RepairAsync(r2, cid, new[] { junkId }, confirmedPeriods: new[] { period }, userId: null));
            Assert.True(o.Repaired, o.Message);
            Assert.Equal(new[] { period }, o.AmendmentPeriods);
        }
        using (var v = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(ValidA, (await v.Contacts.AsNoTracking().SingleAsync(c => c.Id == junkId)).TaxId);
            var old = await v.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.Id == certId);
            Assert.Contains("1234567", old.PayeeSnapshotJson);   // ใบที่ยื่นแล้วไม่ถูกเขียนทับ
            Assert.Equal(WithholdingTaxCertStatus.Voided, old.Status);
            var neo = await v.WithholdingTaxCerts.Include(w => w.Lines).AsNoTracking().SingleAsync(w => w.Id == old.SupersededByCertId);
            Assert.Contains(ValidA, neo.PayeeSnapshotJson);
            Assert.Equal(5000m, neo.TotalTaxAmount);
            Assert.Equal(WithholdingTaxCertStatus.Issued, neo.Status);
            Assert.Equal(old.SourcePayrollRunId, neo.SourcePayrollRunId);
            Assert.EndsWith("-E001-2", neo.CertificateNumber);
            var am = Assert.Single(await Pnd1AmendmentQueue.ListAsync(v, cid));
            Assert.True(am.Pending);
            Assert.Contains(neo.CertificateNumber, am.CertificateNumbers);
            await Assert.ThrowsAsync<BusinessRuleException>(() => Pnd1AmendmentQueue.ResolveAsync(v, cid, am.Id, "", null));
            await Pnd1AmendmentQueue.ResolveAsync(v, cid, am.Id, "ยื่นเพิ่มเติม ref 123", null);
            Assert.DoesNotContain(await Pnd1AmendmentQueue.ListAsync(v, cid), a => a.Pending);
            Assert.True(await v.AuditLogs.AnyAsync(l => l.CompanyId == cid && l.EntityType == "Contact" && l.EntityId == junkId.ToString()
                && l.NewValues!.Contains(PayrollJunkPayee.RepairRuleCode)));
            // แถว audit ของเครื่องมือซ่อมเก็บเลขปิดบังเท่านั้น (แถว audit อัตโนมัติของ Contact เป็นพฤติกรรมเดิม — ไม่ใช่ของแพ็กนี้)
            Assert.DoesNotContain(ValidA, (await v.AuditLogs.AsNoTracking()
                .Where(l => l.EntityId == junkId.ToString() && l.NewValues!.Contains(PayrollJunkPayee.RepairRuleCode)).Select(l => l.NewValues).SingleAsync())!);
            Assert.Empty(await PayrollJunkPayee.FindAsync(v, cid));
        }
    }

    [Fact]
    public async Task ผู้ติดต่อเลขขยะ_มีผู้ติดต่อเลขถูกอยู่แล้ว_ย้ายใบแล้วปิดใช้ตัวขยะ_งวดยังไม่ยื่นไม่ต้องยืนยัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        var (cid, junkId, certId) = await SeedJunkAsync(db, filed: false, goodContactExists: true);
        var row = Assert.Single(await PayrollJunkPayee.FindAsync(db, cid));
        Assert.Equal(PayrollJunkPayee.Plan.MoveToExistingContact, row.Plan);
        Assert.Empty(row.FiledPeriods);
        using (var r = DbTestDatabase.TryCreateContext()!)
            Assert.True(Assert.Single(await PayrollJunkPayee.RepairAsync(r, cid, new[] { junkId }, confirmedPeriods: null, userId: null)).Repaired);
        using var v = DbTestDatabase.TryCreateContext()!;
        var cert = await v.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.Id == certId);
        Assert.Equal(row.TargetContactId, cert.PayeeContactId);
        Assert.Contains(ValidA, cert.PayeeSnapshotJson);
        Assert.False((await v.Contacts.AsNoTracking().SingleAsync(c => c.Id == junkId)).IsActive);
    }
}
