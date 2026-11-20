using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Tax;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 275 แพ็ก B (erp-review/2026-11-20/audit-T2-wht-pnd-vat.md) — ทำซ้ำตัวเลขของผลตรวจบน PostgreSQL ผ่านเมธอดจริง:
/// H1 "สร้างใหม่" คืนติ๊กให้แถวใบร่าง (150/300) และไม่คำนวณยอดหัว (178.65/357.65) · H2 ยกเลิก/ออก/เปลี่ยนผูก 50 ทวิ หลังสร้างร่าง
/// ไม่ถึงรายงาน (3,178.65/178.65) · M1 ยื่นผ่าน API โดยไม่ผ่านตัวตรวจ · M3 แถบล้าสมัยไม่ครอบ ภ.ง.ด.2/54/1 · M5 ด่านยังไม่ออกใบ
/// </summary>
[Trait("Category", "Db")]
public class WhtReportRound275DbTests
{
    private readonly ITestOutputHelper _out;
    public WhtReportRound275DbTests(ITestOutputHelper output) => _out = output;

    // งวด = เดือนปัจจุบัน (แถบร่างล้าสมัยตรวจ N เดือนล่าสุดจากวันนี้)
    private static readonly int Y = DateTime.UtcNow.Year, M = DateTime.UtcNow.Month;
    private static DateTime Day(int d) => new(Y, M, Math.Min(d, 28), 0, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(Guid CompanyId, Guid PayeeId, Guid OtherPayeeId);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบ ภ.ง.ด. รอบ 275", TaxId = "0105556000025", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var payee = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ขนส่งและบริการ จำกัด", TaxId = "0105556000017",
            ContactType = ContactType.JuristicPerson, IsSupplier = true,
        };
        var other = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ที่ปรึกษาทดสอบ จำกัด", TaxId = "0107537000017",
            ContactType = ContactType.JuristicPerson, IsSupplier = true,
        };
        db.Contacts.AddRange(payee, other);
        await db.SaveChangesAsync();
        return new Seed(company.Id, payee.Id, other.Id);
    }

    private static WithholdingTaxCert Cert(Guid co, Guid payee, string no, WithholdingTaxCertStatus status, decimal @base, decimal tax,
        Guid? documentId = null, TaxType form = TaxType.WithholdingTax53, string code = "40(8)", Guid? paymentId = null)
    {
        var c = new WithholdingTaxCert
        {
            CompanyId = co, CertificateNumber = no, PayeeContactId = payee, TaxFormType = form,
            TaxYear = Y, TaxMonth = M, Status = status, IssuedDate = status == WithholdingTaxCertStatus.Draft ? null : Day(20),
            CertificateType = WithholdingTaxCertType.Withhold, DocumentId = documentId, SourcePaymentId = paymentId,
            TotalIncomeAmount = @base, TotalTaxAmount = tax,
        };
        c.Lines.Add(new WithholdingTaxCertLine
        {
            LineOrder = 1, IncomeTypeCode = code, IncomeDescription = "ค่าบริการ", PaymentDate = Day(20),
            IncomeAmount = @base, TaxRate = @base == 0 ? 0 : Math.Round(tax / @base * 100m, 2, MidpointRounding.AwayFromZero), TaxAmount = tax,
        });
        return c;
    }

    private static Document Pv(Guid co, Guid payee, string no, decimal @base, decimal wht)
    {
        var d = new Document
        {
            CompanyId = co, ContactId = payee, DocumentType = DocumentType.PaymentVoucher, DocumentNumber = no,
            Status = DocumentStatus.Paid, DocumentDate = Day(15), PaymentDate = Day(15),
            SubTotal = @base, WithholdingTaxAmount = wht, TotalAmount = @base - wht, PaidAmount = @base - wht, BalanceDue = 0m,
            Currency = "THB",
        };
        d.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "ค่าขนส่ง", Quantity = 1, UnitPrice = @base, Amount = @base,
            WithholdingTaxRate = Math.Round(wht / @base * 100m, 2, MidpointRounding.AwayFromZero), WithholdingTaxAmount = wht, IncomeTypeCode = "8",
        });
        return d;
    }

    private static async Task<TaxReportResponse> GenerateAsync(Guid co, TaxType t = TaxType.WithholdingTax53)
    {
        using var req = DbTestDatabase.TryCreateContext()!;
        return await new TaxService(req).GenerateTaxReportAsync(co, new CreateTaxReportRequest(t, Y, M));
    }

    private static async Task<TaxReport> LatestAsync(Guid co, TaxType t = TaxType.WithholdingTax53)
    {
        using var read = DbTestDatabase.TryCreateContext()!;
        return await read.TaxReports.AsNoTracking().Include(r => r.Lines)
            .Where(r => r.CompanyId == co && r.TaxType == t && r.Year == Y && r.Month == M)
            .OrderByDescending(r => r.CreatedAt).FirstAsync();
    }

    private static async Task<TaxReportResponse> RegenerateAsync(Guid co, Guid reportId)
    {
        using var req = DbTestDatabase.TryCreateContext()!;
        return await new TaxService(req).RegenerateTaxReportAsync(co, reportId);
    }

    private static async Task<List<StaleDraftReportRow>> StaleAsync(Guid co)
    {
        using var req = DbTestDatabase.TryCreateContext()!;
        return await new TaxService(req).GetStaleDraftReportsAsync(co, 2);
    }

    private static async Task<TaxReportResponse> FileAsync(Guid co, Guid reportId)
    {
        using var req = DbTestDatabase.TryCreateContext()!;
        return await new TaxService(req).FileTaxReportAsync(co, reportId);
    }

    private static async Task MutateCertAsync(Guid co, string no, Action<WithholdingTaxCert> change)
    {
        using var w = DbTestDatabase.TryCreateContext()!;
        var c = await w.WithholdingTaxCerts.FirstAsync(x => x.CompanyId == co && x.CertificateNumber == no);
        change(c);
        await w.SaveChangesAsync();
    }

    // ───────────────────────── H1 ─────────────────────────

    [Fact]
    public async Task R275B_H1_PVสองงวด_ใบงวด1ออกแล้ว_ใบงวด2ร่าง_สร้างใหม่_แถวร่างยังติ๊กออก_ยอดหัว150()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var pv = Pv(s.CompanyId, s.PayeeId, "PV-275-H1", 10_000m, 300m);
        db.Documents.Add(pv);
        await db.SaveChangesAsync();
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WT-275-H1-1", WithholdingTaxCertStatus.Issued, 5_000m, 150m, pv.Id, paymentId: Guid.NewGuid()));
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WT-275-H1-2", WithholdingTaxCertStatus.Draft, 5_000m, 150m, pv.Id, paymentId: Guid.NewGuid()));   // งวดจ่ายที่สอง (ดัชนี 1 ใบต่องวดจ่าย)
        await db.SaveChangesAsync();

        var gen = await GenerateAsync(s.CompanyId);
        Assert.Equal(150m, gen.TotalTaxWithheld);
        Assert.Contains(gen.Lines, l => l.IsExcluded && (l.Description ?? "").StartsWith(WhtUnissuedCertGate.DraftCertMarker));

        await RegenerateAsync(s.CompanyId, gen.Id);
        var after = await LatestAsync(s.CompanyId);
        // เดิม: แถวใบร่างถูกติ๊กเข้าเอง (DocumentId เดียวกับใบงวด 1) ⇒ Σบรรทัด 300 · ยอดหัวค้าง 150 · ด่านยังไม่ออกใบไม่บล็อก
        var draftRow = Assert.Single(after.Lines, l => (l.Description ?? "").StartsWith(WhtUnissuedCertGate.DraftCertMarker));
        Assert.True(draftRow.IsExcluded);
        Assert.Equal(150m, after.TotalTaxWithheld);
        Assert.Equal(after.TotalTaxWithheld, WhtReportTotals.Of(after.Lines).Tax);
        Assert.True(WhtUnissuedCertGate.Evaluate(after.Lines.Select(l => (l.Description, l.IsExcluded, l.TaxAmount))).Any);
        // ยื่นยังถูกบล็อกจนออกใบ (ท.ป.4/2528)
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => FileAsync(s.CompanyId, after.Id));
        Assert.Equal(WhtUnissuedCertGate.RuleCode, ex.RuleCode);
    }

    [Fact]
    public async Task R275B_H1_M5_ติ๊กแถวเตือนเข้าพร้อมเหตุผล_สร้างใหม่คืนติ๊ก_ยอดหัวคำนวณใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.OtherPayeeId, "WT-275-H1B", WithholdingTaxCertStatus.Issued, 5_955m, 178.65m));
        db.Documents.Add(Pv(s.CompanyId, s.PayeeId, "PV-275-H1B", 17_900m, 179m));   // ยังไม่ออก 50 ทวิ
        await db.SaveChangesAsync();

        var gen = await GenerateAsync(s.CompanyId);
        Assert.Equal(178.65m, gen.TotalTaxWithheld);
        var warn = Assert.Single(gen.Lines, l => (l.Description ?? "").StartsWith(WhtUnissuedCertGate.UnissuedCertMarker));
        Assert.True(warn.IsExcluded);
        Assert.True(warn.IncludeNeedsReason);
        Assert.True(warn.DescriptionLocked);

        // M5: ติ๊กเข้าโดยไม่มีเหตุผล ⇒ ปฏิเสธพร้อมทางไปต่อ
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new TaxService(req).UpdateTaxReportAsync(s.CompanyId, gen.Id,
                new UpdateTaxReportRequest(null, new List<UpdateTaxReportLineRequest> { new(warn.Id, null, null, null, null, Excluded: false) })));
            Assert.Equal(WhtReportLineRule.IncludeRuleCode, ex.RuleCode);
        }
        // M5: แก้คำนำหน้าของระบบ ⇒ ปฏิเสธ (เดิมลบ marker แล้วด่านมองไม่เห็น)
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new TaxService(req).UpdateTaxReportAsync(s.CompanyId, gen.Id,
                new UpdateTaxReportRequest(null, new List<UpdateTaxReportLineRequest> { new(warn.Id, null, null, null, "ค่าขนส่ง") })));
            Assert.Equal(WhtReportLineRule.EditLockRuleCode, ex.RuleCode);
        }
        var actor = Guid.NewGuid();
        TaxReportResponse upd;
        using (var req = DbTestDatabase.TryCreateContext()!)
            upd = await new TaxService(req).UpdateTaxReportAsync(s.CompanyId, gen.Id,
                new UpdateTaxReportRequest(null, new List<UpdateTaxReportLineRequest>
                    { new(warn.Id, null, null, null, warn.Description, Excluded: false, IncludeReason: "นำส่งก่อนออกใบ — ผู้ถูกหักยังไม่ส่งที่อยู่") }),
                actorId: actor, actorEmail: "acct@test");
        Assert.Equal(357.65m, upd.TotalTaxWithheld);
        using (var read = DbTestDatabase.TryCreateContext()!)
        {
            // ตัวจับ audit อัตโนมัติของ SaveChanges ก็เขียนแถวของบรรทัดนี้ด้วย — แถวเฉพาะคือแถวที่มีรหัสกติกา
            var audit = await read.AuditLogs.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId
                && a.EntityType == nameof(TaxReportLine) && a.EntityId == warn.Id.ToString()
                && a.NewValues!.Contains(WhtReportLineRule.IncludeRuleCode));
            Assert.Equal(actor, audit.UserId);
            Assert.Contains(WhtReportLineRule.IncludeRuleCode, audit.NewValues);
            using var json = System.Text.Json.JsonDocument.Parse(audit.NewValues!);   // ตัวเขียน escape อักษรไทยเป็น \uXXXX
            Assert.Equal("นำส่งก่อนออกใบ — ผู้ถูกหักยังไม่ส่งที่อยู่", json.RootElement.GetProperty("Reason").GetString());
        }

        await RegenerateAsync(s.CompanyId, gen.Id);
        var after = await LatestAsync(s.CompanyId);
        var warnAfter = Assert.Single(after.Lines, l => (l.Description ?? "").StartsWith(WhtUnissuedCertGate.UnissuedCertMarker));
        Assert.False(warnAfter.IsExcluded);
        // เดิม: ยอดหัวค้าง 178.65 · Σบรรทัด 357.65 ⇒ RD-WHT-002 วนไม่จบ
        Assert.Equal(357.65m, after.TotalTaxWithheld);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var check = await new TaxComplianceChecker(req).CheckAsync(s.CompanyId, after.Id);
            Assert.DoesNotContain(check.Findings, f => f.Code == "RD-WHT-002");
        }
    }

    [Fact]
    public async Task R275B_M5_ทิศตรงข้าม_บันทึกทั้งหน้าค่าเดิม_และติ๊กบรรทัด50ทวิออกเข้า_ไม่ต้องมีเหตุผล_แก้ยอดบรรทัด50ทวิถูกปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WT-275-M5", WithholdingTaxCertStatus.Issued, 5_955m, 178.65m));
        await db.SaveChangesAsync();
        var gen = await GenerateAsync(s.CompanyId);
        var row = Assert.Single(gen.Lines);
        Assert.True(row.AmountsLocked);
        Assert.NotNull(row.WhtCertId);

        // หน้าเว็บส่งทุกช่องของทุกบรรทัด (ค่าเดิม) ⇒ ไม่ใช่การแก้
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new TaxService(req).UpdateTaxReportAsync(s.CompanyId, gen.Id, new UpdateTaxReportRequest("หมายเหตุ",
                new List<UpdateTaxReportLineRequest> { new(row.Id, row.IncomeAmount, row.TaxRate, row.TaxAmount, row.Description, Excluded: true) }));
        TaxReportResponse back;
        using (var req = DbTestDatabase.TryCreateContext()!)
            back = await new TaxService(req).UpdateTaxReportAsync(s.CompanyId, gen.Id, new UpdateTaxReportRequest(null,
                new List<UpdateTaxReportLineRequest> { new(row.Id, null, null, null, null, Excluded: false) }));
        Assert.Equal(178.65m, back.TotalTaxWithheld);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new TaxService(req).UpdateTaxReportAsync(s.CompanyId, gen.Id,
                new UpdateTaxReportRequest(null, new List<UpdateTaxReportLineRequest> { new(row.Id, null, null, 100m, null) })));
            Assert.Equal(WhtReportLineRule.EditLockRuleCode, ex.RuleCode);
        }
    }

    // ───────────────────────── H2 ─────────────────────────

    [Fact]
    public async Task R275B_H2_ยกเลิก50ทวิหลังสร้างร่าง_แถบขึ้น_ยื่นและไฟล์ยื่นถูกบล็อก_สร้างใหม่แล้วยื่นได้ยอดทะเบียน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        // ใบมือไม่มีเอกสาร (กลุ่ม "ยังไม่ผูก" รอบ 271) — ลายเซ็นเดิมข้ามบรรทัดที่ไม่มี DocumentId
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WT-275-A", WithholdingTaxCertStatus.Issued, 100_000m, 3_000m));
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.OtherPayeeId, "WT-275-B", WithholdingTaxCertStatus.Issued, 5_955m, 178.65m));
        await db.SaveChangesAsync();
        var gen = await GenerateAsync(s.CompanyId);
        Assert.Equal(3_178.65m, gen.TotalTaxWithheld);
        Assert.DoesNotContain(await StaleAsync(s.CompanyId), r => r.ReportId == gen.Id);   // ทิศตรงข้าม: ยังไม่มีอะไรเปลี่ยน

        await MutateCertAsync(s.CompanyId, "WT-275-A", c => c.Status = WithholdingTaxCertStatus.Voided);
        var stale = Assert.Single(await StaleAsync(s.CompanyId), r => r.ReportId == gen.Id);
        Assert.Equal(1, stale.RemovedDocuments);
        Assert.True(stale.CanRegenerate);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => FileAsync(s.CompanyId, gen.Id));
        Assert.Equal(TaxReportDraftFreshness.FileRuleCode, ex.RuleCode);
        Assert.Equal(409, ex.StatusCode);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex2 = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new TaxService(req).GenerateEFilingAsync(s.CompanyId, "PND.53", Y, M, "db-test"));
            Assert.Equal(TaxReportDraftFreshness.FileRuleCode, ex2.RuleCode);
        }

        await RegenerateAsync(s.CompanyId, gen.Id);
        var fresh = await LatestAsync(s.CompanyId);
        Assert.Equal(178.65m, fresh.TotalTaxWithheld);
        Assert.DoesNotContain(await StaleAsync(s.CompanyId), r => r.ReportId == fresh.Id);
        var filed = await FileAsync(s.CompanyId, fresh.Id);
        Assert.Equal(TaxReportStatus.Submitted, filed.Status);
        Assert.Equal(178.65m, filed.TotalTaxWithheld);
    }

    [Fact]
    public async Task R275B_H2_ออกใบร่างเป็นออกแล้ว_และออกใบมือเพิ่ม_หลังสร้างร่าง_แถบขึ้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WT-275-D", WithholdingTaxCertStatus.Draft, 5_000m, 150m));
        await db.SaveChangesAsync();
        var gen = await GenerateAsync(s.CompanyId);
        Assert.Equal(0m, gen.TotalTaxWithheld);

        await MutateCertAsync(s.CompanyId, "WT-275-D", c => { c.Status = WithholdingTaxCertStatus.Issued; c.IssuedDate = Day(20); });
        var st = Assert.Single(await StaleAsync(s.CompanyId), r => r.ReportId == gen.Id);
        Assert.Equal(1, st.ChangedDocuments);   // ร่าง → ออกแล้ว ยอดเท่าเดิม (ลายเซ็นต่อเอกสารเดิมมองไม่เห็น)

        using (var w = DbTestDatabase.TryCreateContext()!)
        {
            w.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.OtherPayeeId, "WT-275-E", WithholdingTaxCertStatus.Issued, 1_000m, 30m));
            await w.SaveChangesAsync();
        }
        var st2 = Assert.Single(await StaleAsync(s.CompanyId), r => r.ReportId == gen.Id);
        Assert.Equal(1, st2.AddedDocuments);   // ทิศกลับ: ยื่นขาด ⇒ เงินเพิ่ม 1.5%/เดือน
    }

    [Fact]
    public async Task R275B_M4_แก้เลขผู้เสียภาษีผู้ถูกหักหลังสร้างร่าง_ใบร่าง_แถบขึ้น_สร้างใหม่ได้เลขใหม่()
    {
        // ใบที่ออกแล้วใช้ตัวตน ณ วันออก (275-B-6 (ข) · เทสต์ R275BF_B6ข_…) ⇒ กรณีที่ผู้ติดต่อสดมีผลคือใบร่าง/แถวเอกสาร
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WT-275-M4", WithholdingTaxCertStatus.Draft, 1_000m, 30m));
        await db.SaveChangesAsync();
        var gen = await GenerateAsync(s.CompanyId);
        using (var w = DbTestDatabase.TryCreateContext()!)
        {
            var c = await w.Contacts.FirstAsync(x => x.Id == s.PayeeId && x.CompanyId == s.CompanyId);
            c.TaxId = "0105556000033";
            await w.SaveChangesAsync();
        }
        var st = Assert.Single(await StaleAsync(s.CompanyId), r => r.ReportId == gen.Id);
        Assert.Equal(1, st.ChangedDocuments);
        await RegenerateAsync(s.CompanyId, gen.Id);
        var after = await LatestAsync(s.CompanyId);
        Assert.Equal("0105556000033", Assert.Single(after.Lines).TaxPayerId);
    }

    // ───────────────────────── M1 / M2 / M3 ─────────────────────────

    [Fact]
    public async Task R275B_M1_ยื่นผ่านAPIเมื่อยอดหัวไม่ตรงบรรทัด_เซิร์ฟเวอร์บล็อกเอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WT-275-M1", WithholdingTaxCertStatus.Issued, 5_955m, 178.65m));
        await db.SaveChangesAsync();
        var gen = await GenerateAsync(s.CompanyId);
        using (var w = DbTestDatabase.TryCreateContext()!)
        {
            var r = await w.TaxReports.FirstAsync(x => x.Id == gen.Id && x.CompanyId == s.CompanyId);
            r.TotalTaxWithheld = 178.64m;   // ต่าง 0.01 — เดิมผ่านด้วยค่าคลาดเคลื่อน 0.50 (L1)
            await w.SaveChangesAsync();
        }
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => FileAsync(s.CompanyId, gen.Id));
        Assert.Equal(TaxService.PreFileCheckRuleCode, ex.RuleCode);
        Assert.Contains("RD-WHT-002", ex.Message);
        using (var read = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(TaxReportStatus.Draft, (await read.TaxReports.AsNoTracking().FirstAsync(x => x.Id == gen.Id)).Status);
    }

    [Fact]
    public async Task R275B_M2_M3_ภงด54_ยกเลิกใบหลังสร้างร่าง_แถบขึ้น_ผู้รับตปทไม่มีเลขไม่ฟ้อง_ยอดหัวไม่ตรงฟ้อง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var foreign = new Contact { CompanyId = s.CompanyId, Name = "Foreign Platform Ltd", ContactType = ContactType.JuristicPerson, IsSupplier = true };
        db.Contacts.Add(foreign);
        await db.SaveChangesAsync();
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, foreign.Id, "WT-275-54", WithholdingTaxCertStatus.Issued, 10_000m, 1_500m,
            form: TaxType.WithholdingTax54, code: "40(2)"));
        await db.SaveChangesAsync();
        var gen = await GenerateAsync(s.CompanyId, TaxType.WithholdingTax54);
        Assert.Equal(1_500m, gen.TotalTaxWithheld);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var check = await new TaxComplianceChecker(req).CheckAsync(s.CompanyId, gen.Id);
            Assert.DoesNotContain(check.Findings, f => f.Code == "RD-WHT-005");   // L4: ผู้รับต่างประเทศไม่มีเลขไทยโดยธรรมชาติ
            Assert.DoesNotContain(check.Findings, f => f.Code == "RD-WHT-002");
        }
        await MutateCertAsync(s.CompanyId, "WT-275-54", c => c.Status = WithholdingTaxCertStatus.Voided);
        Assert.Single(await StaleAsync(s.CompanyId), r => r.ReportId == gen.Id);   // M3: เดิมไม่ตรวจ ภ.ง.ด.54 เลย
        using (var w = DbTestDatabase.TryCreateContext()!)
        {
            var r = await w.TaxReports.FirstAsync(x => x.Id == gen.Id && x.CompanyId == s.CompanyId);
            r.TotalTaxWithheld = 1_000m;
            await w.SaveChangesAsync();
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var check = await new TaxComplianceChecker(req).CheckAsync(s.CompanyId, gen.Id);
            Assert.Contains(check.Findings, f => f.Code == "RD-WHT-002");   // M2: เดิมไม่มีการเทียบยอดหัวของ ภ.ง.ด.54
        }
    }

    [Fact]
    public async Task R275B_M2_ภพ36_ยอดหัวไม่ตรงบรรทัด_ฟ้อง_ตรงไม่ฟ้อง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        TaxReport Pp36(int month, decimal outputVat)
        {
            var r = new TaxReport { CompanyId = s.CompanyId, TaxType = TaxType.VatPp36, Year = Y, Month = month, Status = TaxReportStatus.Draft };
            r.Lines.Add(new TaxReportLine { TaxReportId = r.Id, LineOrder = 1, Description = "PV-1 — บริการจากต่างประเทศ (§83/6)",
                IncomeAmount = 1_000m, TaxRate = 7m, TaxAmount = 70m, IncomeTypeCode = "PP36", TransactionDate = Day(10) });
            r.TotalIncome = 1_000m; r.OutputVat = outputVat; r.NetVat = outputVat;
            return r;
        }
        var bad = Pp36(M, 69m);
        var good = Pp36(M == 1 ? 2 : M - 1, 70m);
        db.TaxReports.AddRange(bad, good);
        await db.SaveChangesAsync();
        using var req = DbTestDatabase.TryCreateContext()!;
        var checker = new TaxComplianceChecker(req);
        Assert.Contains((await checker.CheckAsync(s.CompanyId, bad.Id)).Findings, f => f.Code == "RD-PP36-001");
        Assert.DoesNotContain((await checker.CheckAsync(s.CompanyId, good.Id)).Findings, f => f.Code == "RD-PP36-001");
    }

    [Fact]
    public async Task R275B_ภงด1รุ่นเก่า_แถบบอกทางไปต่อ_ไม่มีปุ่มสร้างใหม่_ยื่นถูกบล็อก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var r = new TaxReport { CompanyId = s.CompanyId, TaxType = TaxType.WithholdingTax1, Year = Y, Month = M, Status = TaxReportStatus.Draft,
            TotalIncome = 30_000m, TotalTaxWithheld = 500m };
        r.Lines.Add(new TaxReportLine { TaxReportId = r.Id, LineOrder = 1, Description = "เงินเดือน", IncomeAmount = 30_000m, TaxAmount = 500m,
            TaxPayerId = "1101700230708", IncomeTypeCode = "40(1)", TransactionDate = Day(25) });
        db.TaxReports.Add(r);
        await db.SaveChangesAsync();
        var row = Assert.Single(await StaleAsync(s.CompanyId), x => x.ReportId == r.Id);
        Assert.False(row.CanRegenerate);
        Assert.Equal(TaxReportDraftFreshness.LegacyPnd1RuleCode, row.RuleCode);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => FileAsync(s.CompanyId, r.Id));
        Assert.Equal(TaxReportDraftFreshness.LegacyPnd1RuleCode, ex.RuleCode);
        Assert.Contains("Export ยื่นภาษี", ex.Message);
    }

    // ═════ ชุดแก้ฝ่ายค้านรอบ 275 แพ็ก B ═════

    [Fact]
    public async Task R275BF_HIGH1_รายงานยื่นแล้วรุ่นเก่า_ไม่มีตัวอ้างใบ_สร้างไฟล์ได้_ทะเบียนขยับเตือนไม่บล็อก_backfillแล้วไม่เตือน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WT-275F-H1", WithholdingTaxCertStatus.Issued, 5_955m, 178.65m));
        await db.SaveChangesAsync();
        var gen = await GenerateAsync(s.CompanyId);
        await FileAsync(s.CompanyId, gen.Id);
        // จำลองรายงานที่ยื่นก่อนรอบ 275: บรรทัดไม่มีตัวอ้างใบ (คีย์เอกสาร ≠ คีย์ใบ ⇒ ลายเซ็นไม่ตรงตลอดไป)
        await db.Database.ExecuteSqlRawAsync("UPDATE \"TaxReportLines\" SET \"WhtCertId\" = NULL WHERE \"TaxReportId\" = {0}", gen.Id);
        Accounting.Models.Entities.EFilingExport ex1;
        using (var req = DbTestDatabase.TryCreateContext()!)
            ex1 = await new TaxService(req).GenerateEFilingAsync(s.CompanyId, "PND.53", Y, M, "db-test");   // เดิม 409 ตลอดไป
        Assert.Contains("178.65", ex1.FileContent);
        Assert.NotNull(ex1.ReviewNote);
        Assert.Contains("ยื่นแบบเพิ่มเติม", ex1.ReviewNote);
        // migration เติมตัวอ้างใบที่จับคู่ได้แน่นอน ⇒ ลายเซ็นตรง ⇒ ไม่เตือน
        var n = await db.Database.ExecuteSqlRawAsync(Round275BMigration.BackfillLineCertSql);
        Assert.True(n >= 1);
        using (var read = DbTestDatabase.TryCreateContext()!)
            Assert.All(await read.TaxReportLines.AsNoTracking().Where(l => l.TaxReportId == gen.Id).ToListAsync(), l => Assert.NotNull(l.WhtCertId));
        Accounting.Models.Entities.EFilingExport ex2;
        using (var req = DbTestDatabase.TryCreateContext()!)
            ex2 = await new TaxService(req).GenerateEFilingAsync(s.CompanyId, "PND.53", Y, M, "db-test");
        Assert.Null(ex2.ReviewNote);
        // ทะเบียนขยับหลังยื่น ⇒ ยังสร้างไฟล์จากยอดที่ยื่นได้ + เตือน
        await MutateCertAsync(s.CompanyId, "WT-275F-H1", c => c.Status = WithholdingTaxCertStatus.Voided);
        Accounting.Models.Entities.EFilingExport ex3;
        using (var req = DbTestDatabase.TryCreateContext()!)
            ex3 = await new TaxService(req).GenerateEFilingAsync(s.CompanyId, "PND.53", Y, M, "db-test");
        Assert.Contains("178.65", ex3.FileContent);
        Assert.NotNull(ex3.ReviewNote);
    }

    [Fact]
    public async Task R275BF_MED3_ภพ30ยอดภาษีขายติดลบ_ใบลดหนี้มากกว่ายอดขาย_ยื่นได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var r = new TaxReport { CompanyId = s.CompanyId, TaxType = TaxType.VAT, Year = Y, Month = M, Status = TaxReportStatus.Draft };
        r.Lines.Add(new TaxReportLine { TaxReportId = r.Id, LineOrder = 1, Description = "ลดหนี้ขาย CN-1", IncomeAmount = -1_000m, TaxRate = 7, TaxAmount = -70m,
            IncomeTypeCode = "OUTPUT", TransactionDate = Day(5) });
        TaxService.RecalcVatTotals(r);
        db.TaxReports.Add(r);
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var check = await new TaxComplianceChecker(req).CheckAsync(s.CompanyId, r.Id);
            Assert.Contains(check.Findings, f => f.Code == "RD-VAT-002");   // หน้าตรวจยังแสดง
        }
        var filed = await FileAsync(s.CompanyId, r.Id);                       // เซิร์ฟเวอร์ไม่บล็อก (ไม่อยู่ใน ServerBlockingCodes)
        Assert.Equal(TaxReportStatus.Submitted, filed.Status);
    }

    [Fact]
    public async Task R275BF_MED4_ติ๊กบรรทัด50ทวิออก_สร้างใหม่_ยังติ๊กออก_ไม่กลับเข้าไฟล์()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WT-275F-M4a", WithholdingTaxCertStatus.Issued, 5_955m, 178.65m));
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.OtherPayeeId, "WT-275F-M4b", WithholdingTaxCertStatus.Issued, 1_000m, 30m));
        await db.SaveChangesAsync();
        var gen = await GenerateAsync(s.CompanyId);
        var b = gen.Lines.Single(l => (l.Description ?? "").StartsWith("WT-275F-M4b"));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new TaxService(req).UpdateTaxReportAsync(s.CompanyId, gen.Id,
                new UpdateTaxReportRequest(null, new List<UpdateTaxReportLineRequest> { new(b.Id, null, null, null, null, Excluded: true) }));
        await RegenerateAsync(s.CompanyId, gen.Id);
        var after = await LatestAsync(s.CompanyId);
        Assert.True(after.Lines.Single(l => (l.Description ?? "").StartsWith("WT-275F-M4b")).IsExcluded);   // เดิมกลับเข้าเงียบ ๆ
        Assert.False(after.Lines.Single(l => (l.Description ?? "").StartsWith("WT-275F-M4a")).IsExcluded);   // ทิศตรงข้าม: ใบอื่นไม่ถูกแตะ
        Assert.Equal(178.65m, after.TotalTaxWithheld);
    }

    [Fact]
    public async Task R275BF_MED2_แก้ยอดแถวยังไม่ออกใบถูกปฏิเสธ_ร่างไม่ล้าสมัยถาวร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.Documents.Add(Pv(s.CompanyId, s.PayeeId, "PV-275F-M2", 17_900m, 179m));
        await db.SaveChangesAsync();
        var gen = await GenerateAsync(s.CompanyId);
        var warn = Assert.Single(gen.Lines, l => (l.Description ?? "").StartsWith(WhtUnissuedCertGate.UnissuedCertMarker));
        Assert.True(warn.AmountsLocked);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new TaxService(req).UpdateTaxReportAsync(s.CompanyId, gen.Id,
                new UpdateTaxReportRequest(null, new List<UpdateTaxReportLineRequest> { new(warn.Id, null, null, 150m, null) })));
            Assert.Equal(WhtReportLineRule.EditLockRuleCode, ex.RuleCode);
        }
        Assert.DoesNotContain(await StaleAsync(s.CompanyId), r => r.ReportId == gen.Id);
    }

    [Fact]
    public async Task R275BF_B6ข_ออกใบแล้วแก้ผู้ติดต่อ_รายงานใช้ตัวตนณวันออก_ใบร่างใช้ผู้ติดต่อสด_backfillใบเก่า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WT-275F-SN", WithholdingTaxCertStatus.Issued, 1_000m, 30m));
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WT-275F-SD", WithholdingTaxCertStatus.Draft, 1_000m, 30m));
        await db.SaveChangesAsync();
        using (var read = DbTestDatabase.TryCreateContext()!)
        {
            var issued = await read.WithholdingTaxCerts.AsNoTracking().FirstAsync(c => c.CompanyId == s.CompanyId && c.CertificateNumber == "WT-275F-SN");
            Assert.Contains("0105556000017", issued.PayeeSnapshotJson);   // ประทับตอนบันทึก (ทางสร้างใบใดก็ได้)
            Assert.Null((await read.WithholdingTaxCerts.AsNoTracking().FirstAsync(c => c.CompanyId == s.CompanyId && c.CertificateNumber == "WT-275F-SD")).PayeeSnapshotJson);
        }
        using (var w = DbTestDatabase.TryCreateContext()!)
        {
            var c = await w.Contacts.FirstAsync(x => x.Id == s.PayeeId && x.CompanyId == s.CompanyId);
            c.TaxId = "0105556000033"; c.Name = "บริษัท ชื่อใหม่ จำกัด";
            await w.SaveChangesAsync();
        }
        var gen = await GenerateAsync(s.CompanyId);
        var issuedRow = Assert.Single(gen.Lines, l => (l.Description ?? "").StartsWith("WT-275F-SN"));
        Assert.Equal("0105556000017", issuedRow.TaxPayerId);                 // ใบที่ออกแล้ว: ตัวตน ณ วันออก
        Assert.Equal("บริษัท ขนส่งและบริการ จำกัด", issuedRow.TaxPayerName);
        var draftRow = Assert.Single(gen.Lines, l => (l.Description ?? "").Contains("WT-275F-SD"));
        Assert.Equal("0105556000033", draftRow.TaxPayerId);                  // ร่าง: ผู้ติดต่อสด
        // ใบเก่าก่อนมีคอลัมน์ ⇒ migration เติมจากผู้ติดต่อ (ใบร่างไม่ถูกเติม)
        await db.Database.ExecuteSqlRawAsync("UPDATE \"WithholdingTaxCerts\" SET \"PayeeSnapshotJson\" = NULL WHERE \"CompanyId\" = {0}", s.CompanyId);
        await db.Database.ExecuteSqlRawAsync(WhtPayeeSnapshot.BackfillSql);
        using (var read = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Contains("0105556000033", (await read.WithholdingTaxCerts.AsNoTracking().FirstAsync(c => c.CompanyId == s.CompanyId && c.CertificateNumber == "WT-275F-SN")).PayeeSnapshotJson);
            Assert.Null((await read.WithholdingTaxCerts.AsNoTracking().FirstAsync(c => c.CompanyId == s.CompanyId && c.CertificateNumber == "WT-275F-SD")).PayeeSnapshotJson);
        }
    }
}
