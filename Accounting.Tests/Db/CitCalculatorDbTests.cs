using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.FinancialManagement;
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
/// รอบ 230 (TAX_PENALTY_AUDIT §6) — เครื่องคำนวณ CIT หน้าบริหารการเงินบน PostgreSQL จริง: บริษัทรอบ เม.ย.–มี.ค. · ทุน 10 ล. (ไม่ใช่ SME) ·
/// รายการก่อนรอบไม่นับ · ค่าใช้จ่ายภาษีเงินได้ 58000 ไม่ถูกหัก · ส่วนเกินค่ารับรองบวกกลับ · ภ.ง.ด.51 = ประมาณการ ÷ 2 ·
/// ตั้งหนี้ลงวันสิ้นงวด (ไม่ใช่วันที่กด) · งวดปิด ⇒ ปฏิเสธพร้อมข้อความ แล้วเปิดงวดลงได้ · SME กำหนดเองต้องมีเหตุผล · รอบไม่ถึง 12 เดือนไม่คิด ภ.ง.ด.51
/// </summary>
[Trait("Category", "Db")]
public class CitCalculatorDbTests
{
    private readonly ITestOutputHelper _out;
    public CitCalculatorDbTests(ITestOutputHelper output) => _out = output;

    /// <summary>รอบบัญชีที่จบแล้วแน่นอน — เม.ย. ปี Y ถึง มี.ค. ปี Y+1</summary>
    private static readonly int Y = DateTime.UtcNow.Year - 2;

    private static async Task<(Guid Cid, Dictionary<string, Guid> Acc)> SeedAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบ CIT 230", TaxId = "0105556000044", FiscalYearStartMonth = 4, PaidUpCapital = 10_000_000m,
            CreatedAt = new DateTime(Y - 3, 1, 1) };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var acc = new Dictionary<string, Guid>();
        foreach (var (code, name, type) in new (string, string, AccountType)[]
        {
            ("11120", "เงินฝากธนาคาร", AccountType.Asset),
            ("11910", "ภาษีถูกหัก ณ ที่จ่าย", AccountType.Asset),
            ("11920", "ภาษีเงินได้นิติบุคคลจ่ายล่วงหน้า", AccountType.Asset),
            ("21920", "ภาษีเงินได้นิติบุคคลค้างจ่าย", AccountType.Liability),
            ("41000", "รายได้จากการขาย", AccountType.Revenue),
            ("53000", "ค่าใช้จ่ายบริหาร", AccountType.Expense),
            ("53100", "ค่ารับรอง", AccountType.Expense),
            ("58000", "ภาษีเงินได้นิติบุคคล", AccountType.Expense),
        })
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = type, Level = 4, IsActive = true };
            db.ChartOfAccounts.Add(a);
            acc[code] = a.Id;
        }
        await db.SaveChangesAsync();
        var n = 0;
        void Je(DateTime date, string dr, string cr, decimal amt)
        {
            n++;
            var je = new JournalEntry { CompanyId = company.Id, EntryNumber = $"JV-CIT230-{n:D3}", EntryDate = date, Status = JournalEntryStatus.Posted,
                JournalType = JournalType.General, TotalDebit = amt, TotalCredit = amt, Description = $"ทดสอบ {n}" };
            je.Lines.Add(new JournalEntryLine { AccountId = acc[dr], DebitAmount = amt, LineOrder = 1 });
            je.Lines.Add(new JournalEntryLine { AccountId = acc[cr], CreditAmount = amt, LineOrder = 2 });
            db.JournalEntries.Add(je);
        }
        Je(new DateTime(Y, 2, 1), "11120", "41000", 500_000m);     // ก่อนรอบ (ก.พ. ปี Y) — ไม่นับ (เดิม ม.ค.–ธ.ค. นับ)
        Je(new DateTime(Y, 6, 1), "11120", "41000", 1_000_000m);
        Je(new DateTime(Y, 7, 1), "53000", "11120", 300_000m);
        Je(new DateTime(Y, 7, 15), "53100", "11120", 40_000m);     // ค่ารับรอง — เพดาน 0.3% × ทุน 10 ล. = 30,000
        Je(new DateTime(Y, 8, 1), "58000", "21920", 50_000m);     // ภาษีเงินได้ปีก่อน — §65 ตรี(6 ทวิ) ห้ามหัก
        await db.SaveChangesAsync();
        return (company.Id, acc);
    }

    private static CalculateCITRequest Req(string period, decimal? estimate = null, bool? sme = null, string? smeReason = null, bool? shortYear = null)
        => new((Y + 543).ToString(), period, null, null, null, null, null,
            EstimatedAnnualProfit: estimate, SmeOverride: sme, SmeOverrideReason: smeReason, ShortFiscalYear: shortYear);

    [Fact]
    public async Task ภงด50_รอบบัญชีบริษัท_ไม่หัก580_บวกกลับค่ารับรอง_ไม่ใช่SME_ตั้งหนี้วันสิ้นรอบ_งวดปิดปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var fm = new FinancialManagementService(db);

        var r = await fm.CalculateCITAsync(cid, Req(CitCalculation.Annual), "tester");
        Assert.Equal(1_000_000m, r.TotalRevenue);                 // ไม่รวม 500,000 ของ ก.พ. ปี Y
        Assert.Equal(340_000m, r.TotalExpenses);                  // ไม่รวม 58000
        Assert.Equal(50_000m, r.CitExpenseExcluded);
        Assert.Equal(10_000m, r.AutoAddBack);                     // ส่วนเกินค่ารับรอง 40,000 − 30,000
        Assert.Equal(670_000m, r.TaxableProfit);
        Assert.False(r.IsSme);
        Assert.Equal(134_000m, r.TaxAmount);                      // 20% (เดิมขั้นบันได SME ทุกบริษัท = 55,500)
        Assert.Equal(new DateTime(Y, 4, 1), r.PeriodStart);
        Assert.Equal(new DateTime(Y + 1, 3, 31), r.PeriodEnd);

        // งวดบัญชีของวันสิ้นรอบปิดแล้ว ⇒ ปฏิเสธ (ไม่ย้ายวันที่เอง) · เปิดงวด ⇒ ลงได้ ณ วันสิ้นรอบ
        var fp = new FiscalPeriod { CompanyId = cid, Name = $"{Y + 1}/03", Year = Y + 1, Month = 3,
            StartDate = new DateTime(Y + 1, 3, 1), EndDate = new DateTime(Y + 1, 3, 31), Status = FiscalPeriodStatus.Closed };
        db.FiscalPeriods.Add(fp);
        await db.SaveChangesAsync();
        var closed = await Assert.ThrowsAsync<BusinessRuleException>(() => fm.PostCITAsync(cid, r.Id, "tester"));
        Assert.Equal(CitCalculation.PeriodClosedRuleCode, closed.RuleCode);
        Assert.Contains($"{Y + 1}/03", closed.Message);
        fp.Status = FiscalPeriodStatus.Open;
        await db.SaveChangesAsync();

        var posted = await fm.PostCITAsync(cid, r.Id, "tester");
        using var check = DbTestDatabase.TryCreateContext()!;
        var je = await check.JournalEntries.AsNoTracking().Include(j => j.Lines).SingleAsync(j => j.Id == posted.JournalEntryId);
        Assert.Equal(new DateTime(Y + 1, 3, 31), je.EntryDate.Date);   // วันสิ้นรอบ ไม่ใช่วันที่กด
        Assert.Equal(134_000m, je.Lines.Where(l => l.AccountId == acc["58000"]).Sum(l => l.DebitAmount));
        Assert.Equal(134_000m, je.Lines.Where(l => l.AccountId == acc["21920"]).Sum(l => l.CreditAmount));
    }

    [Fact]
    public async Task ภงด51_ประมาณการหารสอง_ตั้งหนี้วันสิ้นครึ่งปี_ภงด50หักภงด51ที่ตั้งหนี้แล้ว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var fm = new FinancialManagementService(db);

        // ครึ่งปี เม.ย.–ก.ย.: กำไร 660,000 + ส่วนเกินค่ารับรอง (เพดานครึ่งปี 15,000) 25,000 = 685,000 · ประมาณการ 1,370,000 × 20% ÷ 2
        var half = await fm.CalculateCITAsync(cid, Req(CitCalculation.HalfYear), "tester");
        Assert.Equal(685_000m, half.TaxableProfit);
        Assert.Equal(1_370_000m, half.EstimatedAnnualProfit);
        Assert.Equal(CitCalculation.BasisEstimate, half.HalfYearBasis);
        Assert.Equal(137_000m, half.TaxAmount);
        Assert.Equal(new DateTime(Y, 9, 30), half.PeriodEnd);

        var custom = await fm.CalculateCITAsync(cid, Req(CitCalculation.HalfYear, estimate: 2_000_000m), "tester");
        Assert.Equal(200_000m, custom.TaxAmount);

        var posted = await fm.PostCITAsync(cid, half.Id, "tester");
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var je = await check.JournalEntries.AsNoTracking().Include(j => j.Lines).SingleAsync(j => j.Id == posted.JournalEntryId);
            Assert.Equal(new DateTime(Y, 9, 30), je.EntryDate.Date);
            Assert.Equal(137_000m, je.Lines.Where(l => l.AccountId == acc["11920"]).Sum(l => l.DebitAmount));
        }

        // ภ.ง.ด.50 ช่องภาษีจ่ายล่วงหน้าว่าง ⇒ ระบบใช้ ภ.ง.ด.51 ที่ตั้งหนี้แล้ว (ไม่หักแล้วจ่ายครึ่งปีซ้ำ)
        var annual = await fm.CalculateCITAsync(cid, Req(CitCalculation.Annual), "tester");
        Assert.Equal(137_000m, annual.PrepaidTaxCredit);
        Assert.Equal(0m, annual.NetTaxPayable);                   // ภาษีทั้งปี 134,000 ≤ เครดิต 137,000
    }

    [Fact]
    public async Task SMEกำหนดเอง_ต้องมีเหตุผล_มีเหตุผลแล้วใช้ขั้นบันได_รอบไม่ถึง12เดือนไม่คิดภงด51()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _) = await SeedAsync(db);
        var fm = new FinancialManagementService(db);

        var noReason = await Assert.ThrowsAsync<BusinessRuleException>(() => fm.CalculateCITAsync(cid, Req(CitCalculation.Annual, sme: true), "tester"));
        Assert.Equal(CitCalculation.SmeOverrideRuleCode, noReason.RuleCode);
        var sme = await fm.CalculateCITAsync(cid, Req(CitCalculation.Annual, sme: true, smeReason: "ทุนชำระ ณ วันสิ้นรอบ 4 ล้าน"), "tester");
        Assert.True(sme.IsSme);
        Assert.Equal("ทุนชำระ ณ วันสิ้นรอบ 4 ล้าน", sme.SmeOverrideReason);
        Assert.Equal(55_500m, sme.TaxAmount);                     // (670,000 − 300,000) × 15%

        var exempt = await Assert.ThrowsAsync<BusinessRuleException>(() => fm.CalculateCITAsync(cid, Req(CitCalculation.HalfYear, shortYear: true), "tester"));
        Assert.Equal(CitCalculation.Pnd51ExemptRuleCode, exempt.RuleCode);
        Assert.Equal(0, await db.CorporateIncomeTaxes.CountAsync(c => c.CompanyId == cid && c.TaxPeriod == CitCalculation.HalfYear));
    }

    // ══ ฝ่ายค้านรอบ 230 ══

    [Fact]
    public async Task S3_แถวเดิมไม่มีวันสิ้นงวด_ลงบัญชีไม่ได้_ต้องคำนวณใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _) = await SeedAsync(db);
        var legacy = new CorporateIncomeTax { CompanyId = cid, TaxYear = (Y + 543).ToString(), TaxPeriod = CitCalculation.Annual,
            TaxAmount = 10_000m, NetTaxPayable = 10_000m, TaxableProfit = 50_000m, Status = "Draft" };
        db.CorporateIncomeTaxes.Add(legacy);
        await db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new FinancialManagementService(db).PostCITAsync(cid, legacy.Id, "tester"));
        Assert.Equal(CitCalculation.RecalculateRuleCode, ex.RuleCode);
        Assert.Null((await db.CorporateIncomeTaxes.AsNoTracking().SingleAsync(c => c.Id == legacy.Id)).JournalEntryId);
    }

    [Fact]
    public async Task S8_ภาษีศูนย์_ตรวจงวดก่อน_แล้วสถานะไม่มีภาษีต้องชำระ_ไม่ใช่Filed()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _) = await SeedAsync(db);
        var fm = new FinancialManagementService(db);
        // ขาดทุน: หักเพิ่มเกินกำไร ⇒ ภาษี 0
        var r = await fm.CalculateCITAsync(cid, new CalculateCITRequest((Y + 543).ToString(), CitCalculation.Annual, null, 5_000_000m, null, null, null), "tester");
        Assert.Equal(0m, r.TaxAmount);
        var fp = new FiscalPeriod { CompanyId = cid, Name = $"{Y + 1}/03", Year = Y + 1, Month = 3,
            StartDate = new DateTime(Y + 1, 3, 1), EndDate = new DateTime(Y + 1, 3, 31), Status = FiscalPeriodStatus.Locked };
        db.FiscalPeriods.Add(fp);
        await db.SaveChangesAsync();
        var closed = await Assert.ThrowsAsync<BusinessRuleException>(() => fm.PostCITAsync(cid, r.Id, "tester"));
        Assert.Equal(CitCalculation.PeriodClosedRuleCode, closed.RuleCode);
        Assert.Contains("เปิดงวด", closed.Message);
        fp.Status = FiscalPeriodStatus.Open;
        await db.SaveChangesAsync();
        var done = await fm.PostCITAsync(cid, r.Id, "tester");
        Assert.Equal(CitCalculation.NoTaxDueStatus, done.Status);
        Assert.Null(done.JournalEntryId);
        var again = await Assert.ThrowsAsync<BusinessRuleException>(() => fm.PostCITAsync(cid, r.Id, "tester"));
        Assert.Equal("CIT-POST-DONE", again.RuleCode);
    }

    [Fact]
    public async Task S1_ไฟล์ภงด51_ใช้รายการที่ตั้งหนี้แล้ว_ไม่มีก็คำนวณด้วยกติกาเดียวกัน_กำหนดยื่นสิ้นเดือนที่สอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _) = await SeedAsync(db);
        var export = new TaxFilingExportService(db, new TaxService(db));

        // ยังไม่มีรายการ ⇒ คำนวณสด = ตัวเลขเดียวกับเครื่องคำนวณ (685,000 → 1,370,000 → 137,000) · ไม่ยกเว้นเพราะ CreatedAt
        var live = await export.ExportPnd51Async(cid, Y);
        Assert.Equal(137_000m, live.TotalTax);
        Assert.Contains("คำนวณสด", live.Summary);
        Assert.Contains("|685000.00|1370000.00|", System.Text.Encoding.UTF8.GetString(live.FileData));
        // ครึ่งปี เม.ย.–ก.ย. ⇒ กำหนดยื่นไม่ก่อน 30 พ.ย. (เดิม halfEnd.AddMonths(2) = 30 ส.ค./31 ส.ค. ผิด)
        Assert.Contains("|due=" + Y + "-1", System.Text.Encoding.UTF8.GetString(live.FileData));

        // ตั้งหนี้ด้วยประมาณการ 2,000,000 ⇒ ไฟล์ใช้ตัวเลขชุดนั้น (ไฟล์ = บัญชี)
        var fm = new FinancialManagementService(db);
        var half = await fm.CalculateCITAsync(cid, Req(CitCalculation.HalfYear, estimate: 2_000_000m), "tester");
        await fm.PostCITAsync(cid, half.Id, "tester");
        var fromPosted = await export.ExportPnd51Async(cid, Y);
        Assert.Equal(200_000m, fromPosted.TotalTax);
        Assert.Contains("ตั้งหนี้แล้ว", fromPosted.Summary);
        Assert.Contains("|2000000.00|", System.Text.Encoding.UTF8.GetString(fromPosted.FileData));
    }

    [Fact]
    public async Task S2_หน้านำส่งภงด51_รอบเมษายน_ครบกำหนดจากวันสิ้นครึ่งปีจริง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _) = await SeedAsync(db);
        var fm = new FinancialManagementService(db);
        var half = await fm.CalculateCITAsync(cid, Req(CitCalculation.HalfYear), "tester");
        await fm.PostCITAsync(cid, half.Id, "tester");
        var svc = new StatutoryRemittanceService(db, new AccountingService(db), NullLogger<StatutoryRemittanceService>.Instance, new TaxService(db));
        var dash = await svc.GetDashboardAsync(cid, 60, includeOldAnnual: true);
        var item = dash.Pending.Single(p => p.RemittanceType == CitPayment.Pnd51Type);
        Assert.Equal((Y, 6), (item.PeriodYear, item.PeriodMonth));          // คีย์ตัวตน (ปีรอบบัญชี, 6) คงเดิม
        Assert.True(item.PaperDueDate.Date >= new DateTime(Y, 11, 30));      // สิ้นเดือนที่ 2 หลัง 30 ก.ย. (เดิม 31 ส.ค.)
        Assert.True(item.PaperDueDate.Date < new DateTime(Y, 12, 5));
    }

    [Fact]
    public async Task ค่าเสื่อม_ลงบัญชีแล้วไม่บวกซ้ำ_ยังไม่ลงบัญชีบวก_ภงด50กับเครื่องคำนวณตรงกัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var accum = new ChartOfAccount { CompanyId = cid, AccountCode = "12290", AccountName = "ค่าเสื่อมราคาสะสม", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        var depExp = new ChartOfAccount { CompanyId = cid, AccountCode = "53500", AccountName = "ค่าเสื่อมราคา", AccountType = AccountType.Expense, Level = 4, IsActive = true };
        db.ChartOfAccounts.AddRange(accum, depExp);
        var asset = new FixedAsset { CompanyId = cid, AssetCode = "FA-230", Name = "เครื่องจักร", PurchaseDate = new DateTime(Y, 4, 1),
            PurchaseCost = 120_000m, NetBookValue = 117_000m, UsefulLifeMonths = 60, Status = AssetStatus.Active };
        db.FixedAssets.Add(asset);
        await db.SaveChangesAsync();
        // พ.ค.: ลงบัญชีแล้ว (เส้นเดียวกับ FixedAssetService — JE วันสิ้นเดือน Dr ค่าเสื่อม / Cr ค่าเสื่อมสะสม + IsPosted + JournalEntryId) ⇒ อยู่ใน GL แล้ว
        var je = new JournalEntry { CompanyId = cid, EntryNumber = "JV-DEP-230", EntryDate = new DateTime(Y, 5, 31), Status = JournalEntryStatus.Posted,
            JournalType = JournalType.General, TotalDebit = 1_000m, TotalCredit = 1_000m, IsAutoGenerated = true };
        je.Lines.Add(new JournalEntryLine { AccountId = depExp.Id, DebitAmount = 1_000m, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = accum.Id, CreditAmount = 1_000m, LineOrder = 2 });
        db.JournalEntries.Add(je);
        await db.SaveChangesAsync();
        db.AssetDepreciations.AddRange(
            new AssetDepreciation { CompanyId = cid, FixedAssetId = asset.Id, Year = Y, Month = 5, Amount = 1_000m, IsPosted = true, JournalEntryId = je.Id },
            new AssetDepreciation { CompanyId = cid, FixedAssetId = asset.Id, Year = Y, Month = 6, Amount = 2_000m, IsPosted = false });   // ยังไม่ลงบัญชี
        await db.SaveChangesAsync();

        // เครื่องคำนวณ: GL 340,000 + ค่าเสื่อมที่ลงแล้ว 1,000 (ใน GL) + ยังไม่ลง 2,000 = 343,000 (ไม่ใช่ 344,000)
        var r = await new FinancialManagementService(db).CalculateCITAsync(cid, Req(CitCalculation.Annual), "tester");
        Assert.Equal(343_000m, r.TotalExpenses);

        // ภ.ง.ด.50 หน้ารายงาน: บรรทัดค่าเสื่อมเพิ่ม = 2,000 เท่านั้น · ค่าใช้จ่ายทั้งปี (GL) = 341,000
        var report = await new TaxService(db).GenerateTaxReportAsync(cid, new CreateTaxReportRequest(TaxType.CorporateIncomeTax, Y, 1));
        Assert.Contains(report.Lines, l => l.Description == "ค่าใช้จ่ายทั้งปี" && l.IncomeAmount == 341_000m);
        Assert.Contains(report.Lines, l => l.Description != null && l.Description.StartsWith("ค่าเสื่อมราคาที่ยังไม่ลงบัญชี") && l.IncomeAmount == 2_000m);
        Assert.DoesNotContain(report.Lines, l => l.Description != null && l.Description.StartsWith("ค่าเสื่อม") && l.IncomeAmount == 3_000m);
    }
}
