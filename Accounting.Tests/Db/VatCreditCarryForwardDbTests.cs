using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// 2026-10-08: เครดิตภาษีซื้อยกไปงวดถัดไปตอนยื่น ภ.พ.30 — เดิมอยู่ใน <c>catch {}</c> และชน EF (แถวใหม่ผ่านคอลเลกชัน ⇒ UPDATE 0 แถว)
/// ทุกครั้ง ⇒ ไม่เคยยกไปเลย · ฝ่ายค้านรอบสาม: เมื่อเขียนได้แล้ว "เติมเฉพาะเมื่อยังไม่มี" ⇒ ปลดล็อก-แก้-ยื่นใหม่ งวดถัดไปค้างเครดิตเก่า
/// ⇒ <c>SyncNextPeriodVatCreditAsync</c> ตัวเดียวจาก ยื่น/ปลดล็อก · เทสต์เส้นจริงบน PostgreSQL
/// </summary>
[Trait("Category", "Db")]
public class VatCreditCarryForwardDbTests
{
    private readonly ITestOutputHelper _out;
    public VatCreditCarryForwardDbTests(ITestOutputHelper output) => _out = output;

    private static TaxReport Vat(Guid companyId, int month, decimal outputVat, decimal inputVat)
    {
        var r = new TaxReport { CompanyId = companyId, TaxType = TaxType.VAT, Year = 2026, Month = month, Status = TaxReportStatus.Draft };
        r.Lines.Add(new TaxReportLine { TaxReportId = r.Id, LineOrder = 1, Description = "ขาย", IncomeAmount = outputVat / 0.07m,
            TaxRate = 7, TaxAmount = outputVat, IncomeTypeCode = "OUTPUT" });
        r.Lines.Add(new TaxReportLine { TaxReportId = r.Id, LineOrder = 2, Description = "ซื้อ", IncomeAmount = inputVat / 0.07m,
            TaxRate = 7, TaxAmount = inputVat, IncomeTypeCode = "INPUT" });
        TaxService.RecalcVatTotals(r);
        return r;
    }

    [Fact]
    public async Task ยื่นงวดที่มีเครดิต_งวดถัดไปได้บรรทัดยกมา_ปลดล็อกแล้วถอน_ยื่นใหม่ได้ยอดใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var company = new Company { Name = "บริษัททดสอบเครดิตยกไป", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var sep = Vat(company.Id, 9, outputVat: 700m, inputVat: 1_200m);    // NetVat −500 = เครดิต
        var oct = Vat(company.Id, 10, outputVat: 2_100m, inputVat: 700m);   // NetVat 1,400 ก่อนหักเครดิต
        db.TaxReports.AddRange(sep, oct);
        await db.SaveChangesAsync();
        Assert.Equal(-500m, sep.NetVat);

        // ยื่น ก.ย. (คำขอใหม่ = DbContext ใหม่)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new TaxService(req).FileTaxReportAsync(company.Id, sep.Id);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var o = await check.TaxReports.Include(r => r.Lines).AsNoTracking().SingleAsync(r => r.Id == oct.Id && r.CompanyId == company.Id);
            var cf = Assert.Single(o.Lines, l => l.IncomeTypeCode == "VAT_CREDIT_CF");
            Assert.Equal(-500m, cf.TaxAmount);
            Assert.Equal(900m, o.NetVat);                                     // 2,100 − 700 − 500
        }

        // ทิศตรงข้าม: ปลดล็อก ก.ย. ⇒ ต.ค. (ร่าง) ต้องไม่ถือเครดิตของการยื่นที่ถูกถอนแล้ว
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new TaxService(req).UnlockTaxFilingAsync(company.Id, sep.Id, "db-test", "แก้ยอดภาษีซื้อ");
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var o = await check.TaxReports.Include(r => r.Lines).AsNoTracking().SingleAsync(r => r.Id == oct.Id && r.CompanyId == company.Id);
            Assert.DoesNotContain(o.Lines, l => l.IncomeTypeCode == "VAT_CREDIT_CF" && !l.IsDeleted);
            Assert.Equal(1_400m, o.NetVat);
        }

        // แก้ ก.ย. ให้เครดิตเหลือ 200 แล้วยื่นใหม่ ⇒ ต.ค. ได้ 200 (ไม่ใช่ 500 ค้าง · ไม่ใช่สองบรรทัด)
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var s = await req.TaxReports.Include(r => r.Lines).SingleAsync(r => r.Id == sep.Id && r.CompanyId == company.Id);
            s.Lines.Single(l => l.IncomeTypeCode == "INPUT").TaxAmount = 900m;
            TaxService.RecalcVatTotals(s);
            await req.SaveChangesAsync();
            await new TaxService(req).FileTaxReportAsync(company.Id, sep.Id);
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var o = await check.TaxReports.Include(r => r.Lines).AsNoTracking().SingleAsync(r => r.Id == oct.Id && r.CompanyId == company.Id);
            var cf = Assert.Single(o.Lines, l => l.IncomeTypeCode == "VAT_CREDIT_CF" && !l.IsDeleted);
            Assert.Equal(-200m, cf.TaxAmount);
            Assert.Equal(1_200m, o.NetVat);
        }
    }
}
