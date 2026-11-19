using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests.Db;

/// <summary>รอบ 271 — ชุดแก้ฝ่ายค้าน `erp-review/2026-11-16/review-271.md` (H1–H3 · M1–M6 · L4) ผ่านเมธอดจริงบน PostgreSQL · ทั้งทิศที่ต้องกันและทิศตรงข้าม</summary>
public partial class WhtCertLinkDbTests
{
    private static DocumentService DocSvc(Accounting.Data.AccountingDbContext db) =>
        new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!,
            taxService: new TaxService(db));

    private static async Task SetStatusAsync(Accounting.Data.AccountingDbContext db, Guid docId, DocumentStatus status, decimal paid)
    {
        var d = await db.Documents.FirstAsync(x => x.Id == docId);
        d.Status = status; d.PaidAmount = paid; d.BalanceDue = d.TotalAmount - paid;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task H1_ลบถาวรใบสำคัญจ่ายที่ผูกใบมือ_ปฏิเสธ_ใบมือยังอยู่_ใบไม่มี50ทวิ_ลบได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var b = await ManualCertAsync(db, s, "WHT-H1-0001");
        var pv = await PvAsync(db, s, "PV-H1-LINKED", autoIssue: false);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).LinkCertToDocumentAsync(s.CompanyId, pv.Id, new WhtLinkRequest(b.Id, null, null, null), null);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                DocSvc(req).PurgeDocumentAsync(s.CompanyId, pv.Id, null, true, "ทดสอบ", cascadeDescendants: false));
            Assert.Contains(b.CertificateNumber, ex.Message);
            Assert.Contains("ยกเลิก (Void)", ex.Message);
        }
        using (var chk = DbTestDatabase.TryCreateContext()!)
        {
            Assert.True(await chk.WithholdingTaxCerts.IgnoreQueryFilters().AnyAsync(w => w.Id == b.Id));
            Assert.True(await chk.Documents.IgnoreQueryFilters().AnyAsync(d => d.Id == pv.Id));
        }
        // ทิศตรงข้าม: ใบสำคัญจ่ายที่ไม่มี 50 ทวิ ยังลบถาวรได้ (force + เหตุผล)
        var plain = await PvAsync(db, s, "PV-H1-PLAIN", autoIssue: false);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await DocSvc(req).PurgeDocumentAsync(s.CompanyId, plain.Id, null, true, "ทดสอบ", cascadeDescendants: false);
        using var chk2 = DbTestDatabase.TryCreateContext()!;
        Assert.False(await chk2.Documents.IgnoreQueryFilters().AnyAsync(d => d.Id == plain.Id));
    }

    [Fact]
    public async Task H1_ด่านล็อกการยื่น_เห็นใบที่ผูกครั้งแรกในเดือนที่ล็อก_ใบอื่นไม่ติด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var b = await ManualCertAsync(db, s, "WHT-H1-0101");
        db.TaxReports.Add(new TaxReport
        {
            CompanyId = s.CompanyId, TaxType = TaxType.WithholdingTax53, Year = Today.Year, Month = Today.Month,
            Status = TaxReportStatus.Filed, FiledDate = DateTime.UtcNow, FilingLockedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var pv = await PvAsync(db, s, "PV-H1-LOCK", autoIssue: false);
        var other = await PvAsync(db, s, "PV-H1-OTHER", autoIssue: false);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).LinkCertToDocumentAsync(s.CompanyId, pv.Id,
                new WhtLinkRequest(b.Id, null, null, new List<string> { WhtCertLinkPlan.CodeFiledLinkOnly }), null);
        using var chk = DbTestDatabase.TryCreateContext()!;
        Assert.True(await new TaxService(chk).IsDocumentFilingLockedAsync(s.CompanyId, pv.Id));
        Assert.False(await new TaxService(chk).IsDocumentFilingLockedAsync(s.CompanyId, other.Id));
    }

    [Fact]
    public async Task H2_ผูกใบร่างก่อนจ่าย_บันทึกจ่าย_ไม่ออกใบที่สอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var b = await ManualCertAsync(db, s, "WHT-H2-0001", status: WithholdingTaxCertStatus.Draft);
        var pv = await PvAsync(db, s, "PV-H2");
        await SetStatusAsync(db, pv.Id, DocumentStatus.Approved, 0m);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).LinkCertToDocumentAsync(s.CompanyId, pv.Id,
                new WhtLinkRequest(b.Id, null, null, new List<string> { WhtCertLinkPlan.CodeDraft }), null);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var o = await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Payment, "tester",
                Today, sourcePaymentId: Guid.NewGuid(), paymentWhtAmount: 30m);
            Assert.False(o.Failed, o.Message);
            Assert.Equal(WhtCertIssueAction.LinkExisting, o.Action);
            Assert.Equal(b.Id, o.CertificateId);
        }
        using var chk = DbTestDatabase.TryCreateContext()!;
        var mine = await chk.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == s.CompanyId && w.DocumentId == pv.Id && w.Status != WithholdingTaxCertStatus.Voided).ToListAsync();
        Assert.Equal(b.Id, Assert.Single(mine).Id);
    }

    [Fact]
    public async Task H3_สร้างใบพร้อมระบุเอกสาร_ยอดไม่ตรง_409ไม่มีใบค้าง_ยอดตรง_ผูก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-H3", autoIssue: false);
        CreateWithholdingTaxCertRequest Req(decimal tax) => new(s.VendorId, TaxType.WithholdingTax53, Today.Year, Today.Month,
            WithholdingTaxCertType.Withhold,
            new List<WithholdingTaxCertLineRequest> { new("8", "ค่าบริการ", Today, 1_000m, 3m, tax, null) }, pv.Id);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).CreateLinkedAsync(s.CompanyId, Req(29m), "tester", null));
            Assert.Equal(409, ex.StatusCode);
        }
        using (var chk = DbTestDatabase.TryCreateContext()!)
            Assert.False(await chk.WithholdingTaxCerts.AsNoTracking().AnyAsync(w => w.CompanyId == s.CompanyId && w.TotalTaxAmount == 29m));
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ok = await Svc(req).CreateLinkedAsync(s.CompanyId, Req(30m), "tester", null);
            Assert.Equal(pv.Id, ok.DocumentId);
        }
    }

    [Fact]
    public async Task M1_ลิสต์รอออกใบเคารพความตั้งใจผูก_กดสร้างใบเอง409()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var b = await ManualCertAsync(db, s, "WHT-M1-0001");
        var pv = await PvAsync(db, s, "PV-M1", linkCert: b.Id);
        await SetStatusAsync(db, pv.Id, DocumentStatus.Approved, 0m);
        using var req = DbTestDatabase.TryCreateContext()!;
        var row = Assert.Single((await Svc(req).GetPendingDocumentsAsync(s.CompanyId)).Where(p => p.DocumentId == pv.Id));
        Assert.Equal(b.Id, row.LinkIntentCertId);
        Assert.Contains("ตั้งใจผูก", row.PendingLabel);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Svc(req).AutoGenerateFromDocumentAsync(s.CompanyId, pv.Id, autoIssue: true, "tester", manualRequest: true));
        Assert.Equal(WhtCertLinkPlan.IntentRuleCode, ex.RuleCode);
    }

    [Fact]
    public async Task M2_ยกเลิกใบเดิมในเดือนยื่นแล้วแล้วผูกครั้งแรก_บล็อก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-M2");
        Guid aId;
        using (var req = DbTestDatabase.TryCreateContext()!)
            aId = (await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester")).CertificateId!.Value;
        await FileMonthAsync(db, s, Today);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).VoidAsync(s.CompanyId, aId, new WhtCertVoidRequest(true, "รับทราบ ยื่นเพิ่มเติม"), null, holdsTaxFile: true);
        var b = await ManualCertAsync(db, s, "WHT-M2-0001");
        using var req2 = DbTestDatabase.TryCreateContext()!;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req2).LinkCertToDocumentAsync(s.CompanyId, pv.Id,
            new WhtLinkRequest(b.Id, null, "สองขั้น", new List<string> { WhtCertLinkPlan.CodeFiledLinkOnly }), null));
        Assert.Equal(WhtCertLinkPlan.FiledRuleCode, ex.RuleCode);
        Assert.Null((await req2.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == b.Id)).DocumentId);
    }

    [Fact]
    public async Task M3_ออกใบร่างที่ผูกครั้งแรกในเดือนยื่นแล้ว_บล็อก_ใบร่างไม่ผูกยังออกได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var b = await ManualCertAsync(db, s, "WHT-M3-0001", status: WithholdingTaxCertStatus.Draft);
        var free = await ManualCertAsync(db, s, "WHT-M3-0002", status: WithholdingTaxCertStatus.Draft, payee: s.OtherId, tax: 45m);
        await FileMonthAsync(db, s, Today);
        var pv = await PvAsync(db, s, "PV-M3", autoIssue: false);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).LinkCertToDocumentAsync(s.CompanyId, pv.Id, new WhtLinkRequest(b.Id, null, null,
                new List<string> { WhtCertLinkPlan.CodeDraft, WhtCertLinkPlan.CodeFiledLinkOnly }), null);
        using var req2 = DbTestDatabase.TryCreateContext()!;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req2).IssueAsync(s.CompanyId, b.Id, null, null));
        Assert.Equal(WhtCertLinkPlan.FiledRuleCode, ex.RuleCode);
        // ทิศตรงข้าม: ใบร่างที่ไม่ได้ผูกด้วยมือ — พฤติกรรมเดิม (ไม่แตะ)
        var issued = await Svc(req2).IssueAsync(s.CompanyId, free.Id, null, null);
        Assert.Equal(WithholdingTaxCertStatus.Issued, issued.Status);
    }

    [Fact]
    public async Task M4_ดัชนีกันใบที่สองของการจ่ายเดียว_ใบยกเลิกไม่นับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-M4");
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var dup = await ManualCertAsync(req, s, "WHT-M4-VOID", status: WithholdingTaxCertStatus.Voided);
            dup.DocumentId = pv.Id;
            await req.SaveChangesAsync();   // ใบยกเลิกของการจ่ายเดียวกัน — ไม่อยู่ในดัชนี
        }
        using var req2 = DbTestDatabase.TryCreateContext()!;
        req2.WithholdingTaxCerts.Add(new WithholdingTaxCert
        {
            CompanyId = s.CompanyId, CertificateNumber = "WHT-M4-DUP", PayeeContactId = s.VendorId, TaxFormType = TaxType.WithholdingTax53,
            TaxYear = Today.Year, TaxMonth = Today.Month, CertificateType = WithholdingTaxCertType.Withhold,
            Status = WithholdingTaxCertStatus.Issued, TotalIncomeAmount = 1_000m, TotalTaxAmount = 30m, DocumentId = pv.Id,
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => req2.SaveChangesAsync());
        Assert.Contains(Round271Migration.UniqueActiveCertIndexName, ex.InnerException?.Message ?? ex.Message);
    }

    [Fact]
    public async Task M5_อาจนับซ้ำ_ผู้ติดต่อซ้ำ_เดือนติดกัน_D5ปิดป้าย_สองการจ่ายของจริงไม่ขึ้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-M5");
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester");
        var dupContact = await ManualCertAsync(db, s, "WHT-M5-DUPC", payee: s.VendorDupId);      // ผู้ติดต่อซ้ำ เลขภาษีเดียวกัน
        var prevMonth = await ManualCertAsync(db, s, "WHT-M5-PREV", month: Today.AddMonths(-1));  // เดือนติดกัน
        using (var chk = DbTestDatabase.TryCreateContext()!)
        {
            var dup = await Svc(chk).GetListAsync(s.CompanyId, new WhtCertListFilter(null, null, null, null, "duplicate", null), new PagedRequest(1, 20));
            var ids = dup.Items.Select(i => i.Id).ToHashSet();
            Assert.Contains(dupContact.Id, ids);
            Assert.Contains(prevMonth.Id, ids);
            Assert.All(dup.Items, i => Assert.NotNull(i.DuplicateSuspectLabel));
            await Svc(chk).SetNoSourceDocumentAsync(s.CompanyId, prevMonth.Id, new WhtCertNoSourceRequest("จ่ายนอกระบบอีกครั้ง"), null);
        }
        using (var chk = DbTestDatabase.TryCreateContext()!)
            Assert.Null((await Svc(chk).GetByIdAsync(s.CompanyId, prevMonth.Id)).DuplicateSuspectLabel);   // D5 ปิดป้ายได้

        // ทิศตรงข้าม: การจ่ายครั้งที่สองยอดเท่ากันที่มีใบสำคัญจ่ายของตัวเอง ⇒ ไม่ขึ้นป้าย
        var s2 = await SeedAsync(db);
        var pv1 = await PvAsync(db, s2, "PV-M5-A");
        var pv2 = await PvAsync(db, s2, "PV-M5-B");
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            await Svc(req).TryAutoIssueForDocumentAsync(s2.CompanyId, pv1.Id, WhtCertIssueTrigger.Approve, "tester");
            await Svc(req).TryAutoIssueForDocumentAsync(s2.CompanyId, pv2.Id, WhtCertIssueTrigger.Approve, "tester");
        }
        using var chk2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(0, (await Svc(chk2).GetListSummaryAsync(s2.CompanyId, new WhtCertListFilter(null, null, null, null, null, null))).DuplicateSuspect);
    }

    [Fact]
    public async Task M6_เปลี่ยนการผูกด้วยใบเดิมที่ไม่ตรงของจริง_409ไม่แตะอะไร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pv = await PvAsync(db, s, "PV-M6");
        Guid aId;
        using (var req = DbTestDatabase.TryCreateContext()!)
            aId = (await Svc(req).TryAutoIssueForDocumentAsync(s.CompanyId, pv.Id, WhtCertIssueTrigger.Approve, "tester")).CertificateId!.Value;
        var b = await ManualCertAsync(db, s, "WHT-M6-0001");
        using var req2 = DbTestDatabase.TryCreateContext()!;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req2).LinkCertToDocumentAsync(s.CompanyId, pv.Id,
            new WhtLinkRequest(b.Id, null, "ซ้ำ", null, ExpectedCurrentCertId: Guid.NewGuid()), null));
        Assert.Equal(WhtCertLinkPlan.StaleRuleCode, ex.RuleCode);
        Assert.Equal(409, ex.StatusCode);
        Assert.NotEqual(WithholdingTaxCertStatus.Voided, (await req2.WithholdingTaxCerts.AsNoTracking().FirstAsync(w => w.Id == aId)).Status);
    }

    [Fact]
    public async Task L4_ยกเลิกใบที่ตั้งใจผูก_ล้างความตั้งใจบนเอกสาร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var b = await ManualCertAsync(db, s, "WHT-L4-0001");
        var pv = await PvAsync(db, s, "PV-L4", linkCert: b.Id, ack: WhtCertLinkPlan.CodeMonth);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).VoidAsync(s.CompanyId, b.Id);
        using var chk = DbTestDatabase.TryCreateContext()!;
        var doc = await chk.Documents.AsNoTracking().FirstAsync(d => d.Id == pv.Id);
        Assert.Null(doc.WhtLinkCertId);
        Assert.Null(doc.WhtLinkAckCodes);
    }
}
