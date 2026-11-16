using Accounting.Helpers;
using Accounting.Models.DTOs;
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
/// รอบ 271 — ผูกใบสำคัญจ่ายกับ 50 ทวิ ที่ออกไว้แล้ว ผ่านเมธอดจริงบน PostgreSQL (กฎ #4 G):
/// reproduce F6 (ใบมือ + ใบที่ระบบออก = นับสองใบ) → ผูกแล้วเหลือใบเดียว · ผูกล้มตอนอนุมัติ = ไม่ผูก ไม่ออกใหม่ ล้มดัง 3 ที่ (271-1-4) ·
/// เปลี่ยนการผูก A→B = A ยกเลิก (ไม่ลบ) + SupersededByCertId (271-0-1) · เดือนยื่นแล้ว = 409 เด็ดขาด (271-0-2) · ทิศตรงข้ามเดือนร่าง (271-0-4) ·
/// ผูกครั้งแรกในเดือนยื่นแล้ว = ผูกได้ (271-1-2) · ใบผูกเอกสารอื่น = 409 (271-0-3) · ลบใบมีเลข = 409 (271-1-1) · ตัวกรองฝั่งเซิร์ฟเวอร์ (F2) · อาจนับซ้ำ
/// </summary>
[Trait("Category", "Db")]
public class WhtCertLinkDbTests
{
    private readonly ITestOutputHelper _out;
    public WhtCertLinkDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid VendorId, Guid VendorDupId, Guid OtherId);

    private static readonly DateTime Today = DateTime.UtcNow.Date;

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบผูก 50 ทวิ", TaxId = "0105556000025", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        Contact C(string name, string taxId) => new()
        {
            CompanyId = company.Id, Name = name, TaxId = taxId, ContactType = ContactType.JuristicPerson, IsSupplier = true,
        };
        var vendor = C("บริษัท ผู้รับเหมา จำกัด", "0105556000017");
        var dup = C("บจก. ผู้รับเหมา (ซ้ำ)", "0105556000017");
        var other = C("บริษัท อื่น จำกัด", "0107537000017");
        db.Contacts.AddRange(vendor, dup, other);
        await db.SaveChangesAsync();
        return new Seed(company.Id, vendor.Id, dup.Id, other.Id);
    }

    /// <summary>ใบสำคัญจ่ายที่จ่ายแล้ว ค่าบริการ 1,000 หัก 3% = 30</summary>
    private static async Task<Document> PvAsync(Accounting.Data.AccountingDbContext db, Seed s, string no, Guid? linkCert = null,
        string? ack = null, bool autoIssue = true)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.VendorId, DocumentType = DocumentType.PaymentVoucher, DocumentNumber = no,
            Status = DocumentStatus.Paid, DocumentDate = Today, PaymentDate = Today,
            SubTotal = 1_000m, WithholdingTaxAmount = 30m, TotalAmount = 970m, PaidAmount = 970m, BalanceDue = 0m,
            Currency = "THB", WhtCertAutoIssue = autoIssue, WhtLinkCertId = linkCert, WhtLinkAckCodes = ack,
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

    /// <summary>ใบ 50 ทวิ ที่คีย์มือที่หน้า 50 ทวิ (ไม่ผูกเอกสาร)</summary>
    private static async Task<WithholdingTaxCert> ManualCertAsync(Accounting.Data.AccountingDbContext db, Seed s, string no,
        decimal tax = 30m, decimal income = 1_000m, DateTime? month = null, Guid? payee = null,
        WithholdingTaxCertStatus status = WithholdingTaxCertStatus.Issued, Guid? payrollRun = null)
    {
        var m = month ?? Today;
        var c = new WithholdingTaxCert
        {
            CompanyId = s.CompanyId, CertificateNumber = no, PayeeContactId = payee ?? s.VendorId, TaxFormType = TaxType.WithholdingTax53,
            TaxYear = m.Year, TaxMonth = m.Month, CertificateType = WithholdingTaxCertType.Withhold, Status = status,
            IssuedDate = status == WithholdingTaxCertStatus.Draft ? null : DateTime.UtcNow,
            TotalIncomeAmount = income, TotalTaxAmount = tax, SourcePayrollRunId = payrollRun,
        };
        c.Lines.Add(new WithholdingTaxCertLine
        {
            LineOrder = 1, IncomeTypeCode = "8", IncomeDescription = "ค่าบริการ", PaymentDate = m,
            IncomeAmount = income, TaxRate = 3m, TaxAmount = tax,
        });
        db.WithholdingTaxCerts.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    private static WithholdingTaxCertService Svc(Accounting.Data.AccountingDbContext db)
        => new(db, null!, null!, NullLogger<WithholdingTaxCertService>.Instance);

    private static Task<List<WithholdingTaxCert>> ActiveFiledScopeAsync(Accounting.Data.AccountingDbContext db, Seed s)
        => db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == s.CompanyId && w.TaxFormType == TaxType.WithholdingTax53 && w.TaxYear == Today.Year
                && w.TaxMonth == Today.Month && WhtCertFilingScope.Filed.Contains(w.Status))
            .ToListAsync();

    private static Task<bool> AuditHasAsync(Accounting.Data.AccountingDbContext db, Guid companyId, string ruleCode)
        => db.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == companyId && a.NewValues != null && a.NewValues.Contains(ruleCode));

    private static async Task FileMonthAsync(Accounting.Data.AccountingDbContext db, Seed s, DateTime month, TaxReportStatus status = TaxReportStatus.Filed)
    {
        db.TaxReports.Add(new TaxReport
        {
            CompanyId = s.CompanyId, TaxType = TaxType.WithholdingTax53, Year = month.Year, Month = month.Month,
            Status = status, FiledDate = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // ══════ (ก) ผูกตอนอนุมัติ/จ่าย ══════

    [Fact]
    public async Task F6_ไม่ผูก_นับสองใบ_ผูกแล้ว_เหลือใบเดียว_ไม่ออกใบใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var manual = await ManualCertAsync(db, s, "WHT-M-0001");

        // baseline (ก่อนแก้ = พฤติกรรมของผู้ใช้ที่ไม่ติ๊กผูก): ใบมือ + ใบที่ระบบออก = 2 ใบในแบบเดือนเดียวกัน (60 บาท จากการจ่าย 30)
        var pvNoLink = await PvAsync(db, s, "PV-F6-BASE");
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.False((await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pvNoLink.Id, WhtCertIssueTrigger.Approve, "tester")).Failed);
        using (var chk = DbTestDatabase.TryCreateContext()!)
        {
            var rows = await ActiveFiledScopeAsync(chk, s);
            Assert.Equal(2, rows.Count);
            Assert.Equal(60m, rows.Sum(r => r.TotalTaxAmount));
        }

        // ใบใหม่ติ๊กผูกใบมืออีกใบ ⇒ ผูก ไม่ออกใบใหม่
        var manual2 = await ManualCertAsync(db, s, "WHT-M-0002");
        var pv = await PvAsync(db, s, "PV-F6-LINK", linkCert: manual2.Id);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var o = await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
            Assert.False(o.Failed, o.Message);
            Assert.Equal(WhtCertIssueAction.LinkExisting, o.Action);
            Assert.Equal(manual2.Id, o.CertificateId);
        }
        using (var chk = DbTestDatabase.TryCreateContext()!)
        {
            var mine = await chk.WithholdingTaxCerts.AsNoTracking().Where(w => w.CompanyId == s.CompanyId && w.DocumentId == pv.Id).ToListAsync();
            var only = Assert.Single(mine);
            Assert.Equal(manual2.Id, only.Id);
            Assert.NotNull(only.LinkedAt);
            Assert.Equal(3, (await ActiveFiledScopeAsync(chk, s)).Count);   // 2 จาก baseline + ใบที่ผูก (ไม่มีใบที่สี่)
            Assert.True(await AuditHasAsync(chk, s.CompanyId, WhtCertLinkPlan.RuleCode));
            // ซ้ำ = ไม่ทำอะไร (idempotent)
            var again = await Svc(chk).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
            Assert.Equal(WhtCertIssueAction.AlreadyIssued, again.Action);
        }
        Assert.Null((await db.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == manual.Id)).DocumentId);   // ใบมือเดิมไม่ถูกแตะ
    }

    [Fact]
    public async Task ผูกล้มตอนอนุมัติ_อนุมัติต่อได้_ไม่ผูก_ไม่ออกใหม่_ป้าย_ผลลัพธ์_audit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var wrong = await ManualCertAsync(db, s, "WHT-M-0101", tax: 30.01m);   // ต่างหนึ่งสตางค์ (271-1-3)
        var pv = await PvAsync(db, s, "PV-FAIL", linkCert: wrong.Id);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var o = await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
            Assert.True(o.Failed);                                   // ที่ 2: ผลลัพธ์ถึงผู้เรียก
            Assert.Contains("ไม่ได้ออกใบใหม่", o.FailureReason);
        }
        using var chk = DbTestDatabase.TryCreateContext()!;
        Assert.Empty(await chk.WithholdingTaxCerts.AsNoTracking().Where(w => w.CompanyId == s.CompanyId && w.DocumentId == pv.Id).ToListAsync());
        Assert.Null((await chk.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == wrong.Id)).DocumentId);
        var doc = await chk.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id);
        Assert.NotNull(WhtCertAutoFailedNote.ReasonOf(doc.InternalNotes));   // ที่ 1: ป้ายบนใบสำคัญจ่าย
        Assert.True(await AuditHasAsync(chk, s.CompanyId, WhtCertLinkPlan.FailedRuleCode));   // ที่ 3: audit
        // ทางไปต่อ: ลิสต์รอออกใบยังเห็นใบนี้
        Assert.Contains((await Svc(chk).GetPendingDocumentsAsync(s.CompanyId)), p => p.DocumentId == pv.Id);
    }

    [Fact]
    public async Task คำเตือนยังไม่รับทราบ_ไม่ผูก_รับทราบแล้ว_ผูก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var draft = await ManualCertAsync(db, s, "WHT-M-0201", status: WithholdingTaxCertStatus.Draft);   // R10
        var pvNo = await PvAsync(db, s, "PV-ACK-NO", linkCert: draft.Id);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.True((await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pvNo.Id, WhtCertIssueTrigger.Approve, "tester")).Failed);
        var draft2 = await ManualCertAsync(db, s, "WHT-M-0202", status: WithholdingTaxCertStatus.Draft);
        var pvYes = await PvAsync(db, s, "PV-ACK-YES", linkCert: draft2.Id, ack: WhtCertLinkPlan.CodeDraft);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var o = await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pvYes.Id, WhtCertIssueTrigger.Approve, "tester");
            Assert.False(o.Failed, o.Message);
        }
        using var chk = DbTestDatabase.TryCreateContext()!;
        var linked = await chk.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == draft2.Id);
        Assert.Equal(pvYes.Id, linked.DocumentId);
        Assert.Equal(WithholdingTaxCertStatus.Draft, linked.Status);   // ไม่ออกให้เอง (D9)
    }

    // ══════ (ข) เปลี่ยนการผูกหลังอนุมัติ ══════

    [Fact]
    public async Task เปลี่ยนการผูก_AไปB_Aยกเลิกไม่หาย_แทนด้วยB_audit_เลขถัดไปไม่ชน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-RELINK");
        Guid aId;
        using (var req = DbTestDatabase.TryCreateContext()!)
            aId = (await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester")).CertificateId!.Value;
        var b = await ManualCertAsync(db, s, "WHT-M-0301");

        // เหตุผลบังคับเมื่อจะยกเลิกใบเดิม
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Svc(req).LinkCertToDocumentAsync(s.CompanyId, pv.Id, new WhtLinkRequest(b.Id, null, null, null), null));
            Assert.Equal(WhtCertLinkPlan.RelinkRuleCode, ex.RuleCode);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var r = await Svc(req).LinkCertToDocumentAsync(s.CompanyId, pv.Id, new WhtLinkRequest(b.Id, null, "ออกด้วยมือไว้แล้ว", null), null);
            Assert.Equal(aId, r.VoidedCertId);
        }
        using var chk = DbTestDatabase.TryCreateContext()!;
        var a = await chk.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == aId);
        Assert.Equal(WithholdingTaxCertStatus.Voided, a.Status);   // ยกเลิก ไม่ลบ (271-0-1)
        Assert.Equal(b.Id, a.SupersededByCertId);
        Assert.Equal(pv.Id, a.DocumentId);                         // คงชี้ใบสำคัญจ่ายไว้ (ประวัติ)
        Assert.Equal(pv.Id, (await chk.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == b.Id)).DocumentId);
        var active = await chk.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == s.CompanyId && w.DocumentId == pv.Id && w.Status != WithholdingTaxCertStatus.Voided).ToListAsync();
        Assert.Single(active);
        Assert.True(await AuditHasAsync(chk, s.CompanyId, WhtCertLinkPlan.RelinkRuleCode));
        Assert.True(await AuditHasAsync(chk, s.CompanyId, WhtCertLinkPlan.RuleCode));
        // รายการ: ใบเดิมขึ้น "ยกเลิก · แทนด้วย B"
        var list = await Svc(chk).GetListAsync(s.CompanyId, new WhtCertListFilter(null, null, null, null, null, a.CertificateNumber), new PagedRequest(1, 20));
        Assert.Equal($"ยกเลิก · แทนด้วย {b.CertificateNumber}", Assert.Single(list.Items).LinkLabel);
    }

    [Theory]
    [InlineData(TaxReportStatus.Filed)]
    [InlineData(TaxReportStatus.Submitted)]
    public async Task เปลี่ยนการผูก_เดือนยื่นแล้ว_409เด็ดขาด_ไม่มีอะไรเปลี่ยน(TaxReportStatus filedStatus)
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-FILED-" + filedStatus);
        Guid aId;
        using (var req = DbTestDatabase.TryCreateContext()!)
            aId = (await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester")).CertificateId!.Value;
        var b = await ManualCertAsync(db, s, "WHT-M-0401");
        await FileMonthAsync(db, s, Today, filedStatus);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Svc(req).LinkCertToDocumentAsync(s.CompanyId, pv.Id,
                    new WhtLinkRequest(b.Id, null, "ซ้ำ", new List<string> { WhtCertLinkPlan.CodeFiled, WhtCertLinkPlan.CodeFiledLinkOnly }), null));
            Assert.Equal(WhtCertLinkPlan.FiledRuleCode, ex.RuleCode);
            Assert.Equal(409, ex.StatusCode);
        }
        using var chk = DbTestDatabase.TryCreateContext()!;
        Assert.NotEqual(WithholdingTaxCertStatus.Voided, (await chk.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == aId)).Status);
        Assert.Null((await chk.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == b.Id)).DocumentId);
        Assert.False(await AuditHasAsync(chk, s.CompanyId, WhtCertLinkPlan.RelinkRuleCode));
        // กล่องผู้สมัครขึ้นกล่องแดง (ไม่แสดงตาราง)
        var cands = await Svc(chk).GetLinkCandidatesForDocumentAsync(s.CompanyId, pv.Id, null);
        Assert.NotNull(cands.FiledBlock);
        Assert.Empty(cands.Candidates);
    }

    [Fact]
    public async Task เปลี่ยนการผูก_มีบันทึกนำส่งของงวด_409เด็ดขาด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-REMIT");
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
        var b = await ManualCertAsync(db, s, "WHT-M-0451");
        db.StatutoryRemittances.Add(new StatutoryRemittance
        {
            CompanyId = s.CompanyId, RemittanceType = "WhtPnd53", PeriodYear = Today.Year, PeriodMonth = Today.Month,
            Amount = 30m, PayDate = Today,
        });
        await db.SaveChangesAsync();
        using var req2 = DbTestDatabase.TryCreateContext()!;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Svc(req2).LinkCertToDocumentAsync(s.CompanyId, pv.Id, new WhtLinkRequest(b.Id, null, "ซ้ำ", null), null));
        Assert.Equal(WhtCertLinkPlan.FiledRuleCode, ex.RuleCode);
    }

    [Fact]
    public async Task ทิศตรงข้าม_รายงานเดือนเป็นร่าง_เปลี่ยนการผูกได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-DRAFTRPT");
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
        var b = await ManualCertAsync(db, s, "WHT-M-0501");
        await FileMonthAsync(db, s, Today, TaxReportStatus.Draft);
        using var req2 = DbTestDatabase.TryCreateContext()!;
        var r = await Svc(req2).LinkCertToDocumentAsync(s.CompanyId, pv.Id, new WhtLinkRequest(b.Id, null, "ซ้ำ", null), null);
        Assert.Equal(b.Id, r.LinkedCertId);
        Assert.NotNull(r.VoidedCertId);
    }

    [Fact]
    public async Task ผูกครั้งแรก_ใบอยู่ในเดือนยื่นแล้ว_ต้องรับทราบ_แล้วผูกได้_ตัวเลขไม่เปลี่ยน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-D10", autoIssue: false);   // ไม่มีใบ (ปิดออกอัตโนมัติ)
        var b = await ManualCertAsync(db, s, "WHT-M-0601");
        await FileMonthAsync(db, s, Today);
        decimal before;
        using (var chk = DbTestDatabase.TryCreateContext()!) before = (await ActiveFiledScopeAsync(chk, s)).Sum(x => x.TotalTaxAmount);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Svc(req).LinkCertToDocumentAsync(s.CompanyId, pv.Id, new WhtLinkRequest(b.Id, null, null, null), null));
            Assert.Equal(WhtCertLinkPlan.NeedsAckRuleCode, ex.RuleCode);
            Assert.Contains("ตัวเลขในแบบ ภ.ง.ด. ที่ยื่นแล้วไม่เปลี่ยน", ex.Message);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var r = await Svc(req).LinkCertToDocumentAsync(s.CompanyId, pv.Id,
                new WhtLinkRequest(b.Id, null, null, new List<string> { WhtCertLinkPlan.CodeFiledLinkOnly }), null);
            Assert.Null(r.VoidedCertId);
        }
        using var chk2 = DbTestDatabase.TryCreateContext()!;
        var linked = await chk2.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == b.Id);
        Assert.Equal(pv.Id, linked.DocumentId);
        Assert.Equal((Today.Year, Today.Month, 30m), (linked.TaxYear, linked.TaxMonth, linked.TotalTaxAmount));   // เดือน/ยอดเดิม
        Assert.Equal(before, (await ActiveFiledScopeAsync(chk2, s)).Sum(x => x.TotalTaxAmount));
    }

    [Fact]
    public async Task ใบผูกเอกสารอื่นแล้ว_ผูกกับใบที่สองไม่ได้_409()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var b = await ManualCertAsync(db, s, "WHT-M-0701");
        var pv1 = await PvAsync(db, s, "PV-ONE", autoIssue: false);
        var pv2 = await PvAsync(db, s, "PV-TWO", autoIssue: false);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).LinkCertToDocumentAsync(s.CompanyId, pv1.Id, new WhtLinkRequest(b.Id, null, null, null), null);
        using var req2 = DbTestDatabase.TryCreateContext()!;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Svc(req2).LinkCertToDocumentAsync(s.CompanyId, pv2.Id, new WhtLinkRequest(b.Id, null, null, null), null));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(pv1.Id, (await req2.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == b.Id)).DocumentId);
    }

    [Fact]
    public async Task สองคำขอพร้อมกันผูกใบเดียวกับสองใบสำคัญจ่าย_สำเร็จหนึ่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var b = await ManualCertAsync(db, s, "WHT-M-0801");
        var pv1 = await PvAsync(db, s, "PV-RACE-1", autoIssue: false);
        var pv2 = await PvAsync(db, s, "PV-RACE-2", autoIssue: false);
        async Task<bool> Try(Guid pvId)
        {
            using var c = DbTestDatabase.TryCreateContext()!;
            try { await Svc(c).LinkCertToDocumentAsync(s.CompanyId, pvId, new WhtLinkRequest(b.Id, null, null, null), null); return true; }
            catch (BusinessRuleException) { return false; }
        }
        var results = await Task.WhenAll(Try(pv1.Id), Try(pv2.Id));
        Assert.Equal(1, results.Count(x => x));
        using var chk = DbTestDatabase.TryCreateContext()!;
        Assert.Contains((await chk.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == b.Id)).DocumentId, new Guid?[] { pv1.Id, pv2.Id });
    }

    // ══════ (ค) ด่านลบถาวร (F1 · 271-1-1) ══════

    [Fact]
    public async Task ลบใบมีเลข_409_ใบยังอยู่_ยกเลิกยังทำได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var c = await ManualCertAsync(db, s, "WHT-M-0901");
        var d = await ManualCertAsync(db, s, "WHT-M-0902", status: WithholdingTaxCertStatus.Draft);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).DeleteAsync(s.CompanyId, c.Id, null));
            Assert.Equal(WhtCertLinkPlan.DeleteRuleCode, ex.RuleCode);
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).DeleteAsync(s.CompanyId, d.Id, null));   // ร่างที่มีเลขก็ลบไม่ได้
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).VoidAsync(s.CompanyId, c.Id);
        using var chk = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(WithholdingTaxCertStatus.Voided, (await chk.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == c.Id)).Status);
        Assert.True(await chk.WithholdingTaxCerts.AsNoTracking().AnyAsync(w => w.Id == d.Id));
        var resp = await Svc(chk).GetByIdAsync(s.CompanyId, c.Id);
        Assert.False(resp.DeleteAllowed);
    }

    // ══════ หน้า 50 ทวิ: ตัวกรองฝั่งเซิร์ฟเวอร์ (F2) · อาจนับซ้ำ (อ่านอย่างเดียว) ══════

    [Fact]
    public async Task ตัวกรองยังไม่ผูก_นับทั้งชุด_ไม่ใช่หน้าที่โหลด_payrollไม่นับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        for (var i = 0; i < 25; i++) await ManualCertAsync(db, s, $"WHT-L-{i:0000}", payee: s.OtherId);
        await ManualCertAsync(db, s, "WHT-PAYROLL-1", payee: s.OtherId, payrollRun: Guid.NewGuid());
        using var chk = DbTestDatabase.TryCreateContext()!;
        var page = await Svc(chk).GetListAsync(s.CompanyId, new WhtCertListFilter(null, null, null, null, "unlinked", null), new PagedRequest(1, 20));
        Assert.Equal(25, page.TotalCount);
        Assert.Equal(20, page.Items.Count);
        Assert.All(page.Items, x => Assert.Equal("Unlinked", x.LinkState));
        var sum = await Svc(chk).GetListSummaryAsync(s.CompanyId, new WhtCertListFilter(null, null, null, null, null, null));
        Assert.Equal(25, sum.Unlinked);
        Assert.Equal(26, sum.Total);
        var payroll = await Svc(chk).GetListAsync(s.CompanyId, new WhtCertListFilter(null, null, null, null, null, "WHT-PAYROLL"), new PagedRequest(1, 20));
        Assert.Equal("Payroll", Assert.Single(payroll.Items).LinkState);
    }

    [Fact]
    public async Task อาจนับซ้ำ_ใบมือมีคู่ใบที่ระบบออก_ป้ายแดง_ใบไม่มีคู่_ไม่ขึ้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var manual = await ManualCertAsync(db, s, "WHT-DUP-M");
        var lonely = await ManualCertAsync(db, s, "WHT-LONELY", tax: 45m, income: 1_500m);
        var pv = await PvAsync(db, s, "PV-DUP");
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
        using var chk = DbTestDatabase.TryCreateContext()!;
        var sum = await Svc(chk).GetListSummaryAsync(s.CompanyId, new WhtCertListFilter(null, null, null, null, null, null));
        Assert.Equal(1, sum.DuplicateSuspect);
        var dup = await Svc(chk).GetListAsync(s.CompanyId, new WhtCertListFilter(null, null, null, null, "duplicate", null), new PagedRequest(1, 20));
        var row = Assert.Single(dup.Items);
        Assert.Equal(manual.Id, row.Id);
        Assert.NotNull(row.DuplicateSuspectLabel);
        Assert.Equal(pv.Id, row.DuplicateSuspectDocumentId);
        var lonelyRow = await Svc(chk).GetByIdAsync(s.CompanyId, lonely.Id);
        Assert.Null(lonelyRow.DuplicateSuspectLabel);
        // อ่านอย่างเดียว — ไม่ประทับสถานะใด
        Assert.Null((await chk.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == manual.Id)).DocumentId);
        // ทางแก้: ตรวจคู่ฝั่งใบสำคัญจ่าย ⇒ ผู้สมัครมีใบมือนี้
        var cands = await Svc(chk).GetLinkCandidatesForDocumentAsync(s.CompanyId, pv.Id, null);
        Assert.Contains(cands.Candidates, c => c.CertId == manual.Id && c.Verdict == "Ok");
        // โมดัลกลับทิศจากฝั่งใบ ⇒ เจอใบสำคัญจ่าย
        Assert.Contains(await Svc(chk).GetPvCandidatesForCertAsync(s.CompanyId, manual.Id), p => p.DocumentId == pv.Id);
    }

    [Fact]
    public async Task ยืนยันไม่มีใบสำคัญจ่าย_ป้ายเทา_ไม่นับยังไม่ผูก_ถอนได้_audit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var c = await ManualCertAsync(db, s, "WHT-NOSRC");
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).SetNoSourceDocumentAsync(s.CompanyId, c.Id, new WhtCertNoSourceRequest(" "), null));
            await Svc(req).SetNoSourceDocumentAsync(s.CompanyId, c.Id, new WhtCertNoSourceRequest("จ่ายนอกระบบ"), null);
        }
        using (var chk = DbTestDatabase.TryCreateContext()!)
        {
            var r = await Svc(chk).GetByIdAsync(s.CompanyId, c.Id);
            Assert.Equal("NoSourceConfirmed", r.LinkState);
            Assert.Equal("จ่ายนอกระบบ", r.NoSourceDocumentReason);
            Assert.Equal(0, (await Svc(chk).GetListSummaryAsync(s.CompanyId, new WhtCertListFilter(null, null, null, null, null, null))).Unlinked);
            Assert.True(await AuditHasAsync(chk, s.CompanyId, WhtCertLinkPlan.NoSourceRuleCode));
            await Svc(chk).SetNoSourceDocumentAsync(s.CompanyId, c.Id, new WhtCertNoSourceRequest(null, Clear: true), null);
        }
        using var chk2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal("Unlinked", (await Svc(chk2).GetByIdAsync(s.CompanyId, c.Id)).LinkState);
    }
}
