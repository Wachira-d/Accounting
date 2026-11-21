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
/// รอบ 273 (ผู้ใช้รายงาน 2026-11-19): ภ.ง.ด.53 ต.ค. 2569 (ร่าง) ฐาน 9,667.91 ภาษี 178.65 — กด "ยื่นภาษี" แล้วตัวตรวจก่อนยื่นฟ้อง
/// <c>RD-WHT-002 — ผลรวมภาษีหัก ณ ที่จ่ายในรายการย่อย (357.30) ไม่เท่ากับ TotalTaxWithheld (178.65)</c> (×2 พอดี).
/// ต้นเหตุ: ตัวตรวจบวกทุกแถว รวมแถว <c>[สรุป]</c> ต่อผู้ถูกหัก (50 ทวิ ใบเดียวสองประเภทเงินได้ ⇒ สองแถวรายการ + แถวสรุป) และแถวที่ติ๊กออก ·
/// ยอดหัวนับเฉพาะแถวที่ยื่นจริง ⇒ ตอนนี้ทั้งคู่ผ่าน <see cref="WhtReportTotals"/> ตัวเดียว · ผ่านเมธอดจริงบน PostgreSQL
/// </summary>
[Trait("Category", "Db")]
public class WhtPreCheckDoubleCountDbTests
{
    private readonly ITestOutputHelper _out;
    public WhtPreCheckDoubleCountDbTests(ITestOutputHelper output) => _out = output;

    private const int Y = 2026, M = 10;

    private sealed record Seed(Guid CompanyId, Guid PayeeId, Guid OtherPayeeId);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบ ภ.ง.ด.53 นับซ้ำ", TaxId = "0105556000025", IsVatRegistered = true };
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

    private static WithholdingTaxCert Cert(Guid co, Guid payee, string no, WithholdingTaxCertStatus status,
        params (string Desc, decimal Base, decimal Rate, decimal Tax)[] lines)
    {
        var c = new WithholdingTaxCert
        {
            CompanyId = co, CertificateNumber = no, PayeeContactId = payee, TaxFormType = TaxType.WithholdingTax53,
            TaxYear = Y, TaxMonth = M, Status = status, IssuedDate = new DateTime(Y, M, 20, 0, 0, 0, DateTimeKind.Utc),
            CertificateType = WithholdingTaxCertType.Withhold,
            TotalIncomeAmount = lines.Sum(l => l.Base), TotalTaxAmount = lines.Sum(l => l.Tax),
        };
        var i = 1;
        foreach (var l in lines)
            c.Lines.Add(new WithholdingTaxCertLine
            {
                LineOrder = i++, IncomeTypeCode = "40(8)", IncomeDescription = l.Desc,
                PaymentDate = new DateTime(Y, M, 20, 0, 0, 0, DateTimeKind.Utc),
                IncomeAmount = l.Base, TaxRate = l.Rate, TaxAmount = l.Tax,
            });
        return c;
    }

    private static async Task<TaxReportResponse> GenerateAsync(Guid co)
    {
        using var req = DbTestDatabase.TryCreateContext()!;
        return await new TaxService(req).GenerateTaxReportAsync(co, new CreateTaxReportRequest(TaxType.WithholdingTax53, Y, M));
    }

    private static async Task<TaxComplianceReport> PreCheckAsync(Guid co, Guid reportId)
    {
        using var req = DbTestDatabase.TryCreateContext()!;
        return await new TaxComplianceChecker(req).CheckAsync(co, reportId);
    }

    private static async Task<TaxReport> LoadAsync(Guid co, Guid reportId)
    {
        using var read = DbTestDatabase.TryCreateContext()!;
        return await read.TaxReports.AsNoTracking().Include(r => r.Lines)
            .FirstAsync(r => r.Id == reportId && r.CompanyId == co);
    }

    [Fact]
    public async Task R273_ใบ50ทวิสองประเภทเงินได้_แถวสรุปไม่ถูกนับซ้ำ_ตัวตรวจก่อนยื่นตรงยอดหัว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        // ตัวเลขเดียวกับที่ผู้ใช้รายงาน: ฐาน 9,667.91 · ภาษี 178.65 (ค่าบริการ 3% + ค่าขนส่ง 1% ในใบเดียว)
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WHT-6910-0001", WithholdingTaxCertStatus.Issued,
            ("ค่าบริการ", 4_098.55m, 3m, 122.96m), ("ค่าขนส่ง", 5_569.36m, 1m, 55.69m)));
        // 50 ทวิ ร่างของงวด = แถวติ๊กออก (ไม่นับ) — ตัวตรวจเดิมบวกแถวนี้ด้วย
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.OtherPayeeId, "WHT-6910-D01", WithholdingTaxCertStatus.Draft,
            ("ค่าที่ปรึกษา", 1_000m, 3m, 30m)));
        await db.SaveChangesAsync();

        var gen = await GenerateAsync(s.CompanyId);
        var report = await LoadAsync(s.CompanyId, gen.Id);
        Assert.Equal(9_667.91m, report.TotalIncome);
        Assert.Equal(178.65m, report.TotalTaxWithheld);
        // รูปของบั๊ก: แถวสรุปของผู้ถูกหักรายเดียวกันมียอดเท่าแถวรายการ ⇒ Σ ทุกแถว = 2× (+ แถวร่างที่ติ๊กออก)
        var summary = Assert.Single(report.Lines, WhtReportTotals.IsSummary);
        Assert.Equal(178.65m, summary.TaxAmount);
        Assert.Contains(report.Lines, l => l.IsExcluded && l.TaxAmount == 30m);
        Assert.Equal(178.65m * 2 + 30m, report.Lines.Sum(l => l.TaxAmount));   // = ตัวเลขที่ตัวตรวจเดิมใช้ (ตัดแถวร่างออก = 357.30)

        // หลังแก้: ตัวตรวจใช้ตัวตั้งเดียวกับยอดหัว
        Assert.Equal(report.TotalTaxWithheld, WhtReportTotals.Of(report.Lines).Tax);
        var check = await PreCheckAsync(s.CompanyId, gen.Id);
        Assert.DoesNotContain(check.Findings, f => f.Code == "RD-WHT-002");
        Assert.DoesNotContain(check.Findings, f => f.Code == "RD-WHT-005");   // แถวสรุป/ติ๊กออกไม่ถูกนับว่า "ไม่มีเลขผู้เสียภาษี"
    }

    [Fact]
    public async Task R273_ทิศตรงข้าม_ยอดหัวค้างไม่ตรงแถวจริง_ตัวตรวจยังฟ้อง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WHT-6910-0101", WithholdingTaxCertStatus.Issued,
            ("ค่าบริการ", 4_098.55m, 3m, 122.96m), ("ค่าขนส่ง", 5_569.36m, 1m, 55.69m)));
        await db.SaveChangesAsync();
        var gen = await GenerateAsync(s.CompanyId);

        // ยอดหัวถูกแก้/ค้าง (เช่นเขียนตรงในฐาน) — ด่านต้องยังจับได้ (ไม่ได้แก้ด้วยการปิดเสียงตัวตรวจ)
        using (var w = DbTestDatabase.TryCreateContext()!)
        {
            var r = await w.TaxReports.FirstAsync(x => x.Id == gen.Id && x.CompanyId == s.CompanyId);
            r.TotalTaxWithheld = 100m;
            await w.SaveChangesAsync();
        }
        var check = await PreCheckAsync(s.CompanyId, gen.Id);
        var f = Assert.Single(check.Findings, x => x.Code == "RD-WHT-002");
        Assert.Contains("178.65", f.Message);
        Assert.False(check.CanSubmit);
    }

    [Fact]
    public async Task R273_รายงานที่ถูกอยู่แล้ว_ไม่มีแถวสรุป_ยอดไม่ขยับ_สร้างใหม่ได้ยอดเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WHT-6910-0201", WithholdingTaxCertStatus.Issued,
            ("ค่าบริการ", 5_955m, 3m, 178.65m)));
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.OtherPayeeId, "WHT-6910-0202", WithholdingTaxCertStatus.Issued,
            ("ค่าโฆษณา", 1_000m, 2m, 20m)));
        await db.SaveChangesAsync();

        var gen = await GenerateAsync(s.CompanyId);
        var report = await LoadAsync(s.CompanyId, gen.Id);
        Assert.DoesNotContain(report.Lines, WhtReportTotals.IsSummary);
        Assert.Equal(6_955m, report.TotalIncome);
        Assert.Equal(198.65m, report.TotalTaxWithheld);
        Assert.Equal(report.Lines.Sum(l => l.TaxAmount), report.TotalTaxWithheld);   // ไม่มีแถวพิเศษ ⇒ สูตรเก่า = สูตรใหม่
        var check = await PreCheckAsync(s.CompanyId, gen.Id);
        Assert.DoesNotContain(check.Findings, f => f.Code == "RD-WHT-002");

        // "สร้างใหม่" บนรายงานที่ถูกอยู่แล้ว ⇒ ยอดเดิมเป๊ะ
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new TaxService(req).RegenerateTaxReportAsync(s.CompanyId, gen.Id);
        var again = await db.TaxReports.AsNoTracking()
            .Where(r => r.CompanyId == s.CompanyId && r.TaxType == TaxType.WithholdingTax53 && r.Year == Y && r.Month == M)
            .OrderByDescending(r => r.CreatedAt).FirstAsync();
        Assert.Equal(6_955m, again.TotalIncome);
        Assert.Equal(198.65m, again.TotalTaxWithheld);
    }
    [Fact]
    public async Task R273_ฝ่ายค้าน2_ติ๊กออกครบทุกแถวแต่ยอดหัวค้าง_ตัวตรวจฟ้อง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WHT-6910-0301", WithholdingTaxCertStatus.Issued,
            ("ค่าบริการ", 5_955m, 3m, 178.65m)));
        await db.SaveChangesAsync();
        var gen = await GenerateAsync(s.CompanyId);
        // ติ๊กออกทุกแถวตรงในฐาน (ไม่ผ่านทางบันทึก) ⇒ Σ ที่นับ = 0 แต่ยอดหัวยัง 178.65 — เดิม `sumWht > 0 &&` ปล่อยผ่าน
        using (var w = DbTestDatabase.TryCreateContext()!)
        {
            var lines = await w.TaxReportLines.Where(l => l.TaxReportId == gen.Id).ToListAsync();
            foreach (var l in lines) l.IsExcluded = true;
            await w.SaveChangesAsync();
        }
        var check = await PreCheckAsync(s.CompanyId, gen.Id);
        var f = Assert.Single(check.Findings, x => x.Code == "RD-WHT-002");
        Assert.Contains("0.00", f.Message);
        Assert.False(check.CanSubmit);
    }

    [Fact]
    public async Task R273_ฝ่ายค้าน1และ3_ติ๊กออกหนึ่งบรรทัด_แถวสรุปคำนวณใหม่_ท้ายตารางเท่ายอดหัว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.WithholdingTaxCerts.Add(Cert(s.CompanyId, s.PayeeId, "WHT-6910-0401", WithholdingTaxCertStatus.Issued,
            ("ค่าบริการ", 4_098.55m, 3m, 122.96m), ("ค่าขนส่ง", 5_569.36m, 1m, 55.69m)));
        await db.SaveChangesAsync();
        var gen = await GenerateAsync(s.CompanyId);
        Assert.Equal(178.65m, gen.LinesTaxTotal);   // ท้ายตาราง = ยอดหัว (ไม่ใช่ 357.30)
        Assert.Equal(gen.TotalTaxWithheld, gen.LinesTaxTotal);

        var transport = gen.Lines.Single(l => l.TaxAmount == 55.69m);
        TaxReportResponse upd;
        using (var req = DbTestDatabase.TryCreateContext()!)
            upd = await new TaxService(req).UpdateTaxReportAsync(s.CompanyId, gen.Id,
                new UpdateTaxReportRequest(null, new List<UpdateTaxReportLineRequest> { new(transport.Id, null, null, null, null, Excluded: true) }));
        Assert.Equal(122.96m, upd.TotalTaxWithheld);
        Assert.Equal(122.96m, upd.LinesTaxTotal);
        // รอบ 284 (T2 L2): ติ๊กออกจนเหลือบรรทัดเดียว ⇒ แถว [สรุป] ถูกลบ (เดิมค้างแถวสรุปของรายที่เหลือหนึ่งบรรทัด) · ยอดหัว/ท้ายตารางเท่าเดิม
        Assert.DoesNotContain(upd.Lines, l => l.Side == "summary");
        using (var req = DbTestDatabase.TryCreateContext()!)
            upd = await new TaxService(req).UpdateTaxReportAsync(s.CompanyId, gen.Id,
                new UpdateTaxReportRequest(null, new List<UpdateTaxReportLineRequest> { new(transport.Id, null, null, null, null, Excluded: false) }));
        var summary = Assert.Single(upd.Lines, l => l.Side == "summary");   // ติ๊กกลับ (1 → 2) ⇒ แถวสรุปกลับมา ยอดตามบรรทัดที่นับ
        Assert.Equal(178.65m, summary.TaxAmount);
        Assert.Equal(178.65m, upd.TotalTaxWithheld);
        var check = await PreCheckAsync(s.CompanyId, gen.Id);
        Assert.DoesNotContain(check.Findings, f => f.Code == "RD-WHT-002");
    }
}
