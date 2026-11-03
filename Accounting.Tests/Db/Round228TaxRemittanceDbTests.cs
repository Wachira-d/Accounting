using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 228 ทีมภาษี (ฝ่ายค้าน N7) — บน PostgreSQL จริง: (1) ภ.ง.ด.51 → ชำระ → ภ.ง.ด.50 → ชำระ ⇒ 11910 · 11920 · 21920 กลับเป็นศูนย์ (S1) ·
/// ตั้งหนี้รอบเดียวกันซ้ำถูกปฏิเสธ (S2) · (2) ชำระนอกระบบ (ใบสำคัญ Dr 21920 หลังวันตั้งหนี้) ไม่ขึ้นค้าง (S3) ·
/// (3) จ่ายอากรพร้อมเงินเพิ่มอากร ⇒ บวกกลับ §65 ตรี(6) เฉพาะเงินเพิ่ม (ไม่นับค่าอากร 54810) · ยกเลิก ⇒ อากรกลับเป็นค้างชำระ
/// </summary>
[Trait("Category", "Db")]
public class Round228TaxRemittanceDbTests
{
    private readonly ITestOutputHelper _out;
    public Round228TaxRemittanceDbTests(ITestOutputHelper output) => _out = output;

    private static readonly int Year = DateTime.UtcNow.Year - 1;

    private static async Task<(Guid CompanyId, Dictionary<string, Guid> Acc)> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบ CIT 228", TaxId = "0105556000002", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var acc = new Dictionary<string, Guid>();
        foreach (var (code, name, type) in new (string, string, AccountType)[]
        {
            ("11120", "เงินฝากธนาคาร", AccountType.Asset),
            ("11910", "ภาษีถูกหัก ณ ที่จ่าย", AccountType.Asset),
            ("11920", "ภาษีเงินได้นิติบุคคลจ่ายล่วงหน้า", AccountType.Asset),
            ("21920", "ภาษีเงินได้นิติบุคคลค้างจ่าย", AccountType.Liability),
            ("41100", "รายได้ค่าบริการ", AccountType.Revenue),
            ("58000", "ภาษีเงินได้นิติบุคคล", AccountType.Expense),
            ("548", "ค่าธรรมเนียมและค่าปรับ", AccountType.Expense),
            ("54810", "ค่าธรรมเนียมราชการ", AccountType.Expense),
            ("54820", "ค่าปรับ/เบี้ยปรับ", AccountType.Expense),
        })
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = type,
                Level = code.Length == 3 ? 3 : 4, IsActive = true };
            db.ChartOfAccounts.Add(a);
            acc[code] = a.Id;
        }
        await db.SaveChangesAsync();
        return (company.Id, acc);
    }

    private static StatutoryRemittanceService Svc(Accounting.Data.AccountingDbContext db)
        => new(db, new AccountingService(db), NullLogger<StatutoryRemittanceService>.Instance, new TaxService(db));

    private static async Task<decimal> BalanceAsync(Accounting.Data.AccountingDbContext db, Guid accountId)
        => await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.AccountId == accountId && !l.IsDeleted && !l.JournalEntry.IsDeleted
                && (l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed))
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;

    private static CorporateIncomeTax Cit(Guid companyId, string period, decimal tax, decimal prepaid, decimal wht, decimal profit)
        => new()
        {
            CompanyId = companyId, TaxYear = (Year + 543).ToString(), TaxPeriod = period, TaxableProfit = profit,
            TaxAmount = tax, PrepaidTaxCredit = prepaid, WithholdingTaxCredit = wht,
            NetTaxPayable = CitPayment.Split(tax, prepaid, wht).Payable, Status = "Calculated",
        };

    [Fact]
    public async Task ภงด51ถึงภงด50_ครบวงจร_11910_11920_21920_เป็นศูนย์_และตั้งหนี้ซ้ำถูกปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        // ลูกค้าหัก ณ ที่จ่ายทั้งปี 10,000 (Dr 11910)
        var whtJe = new JournalEntry { CompanyId = cid, EntryNumber = "JV-WHT-228", EntryDate = new DateTime(Year, 3, 1), Status = JournalEntryStatus.Posted,
            JournalType = JournalType.General, TotalDebit = 10_000m, TotalCredit = 10_000m };
        whtJe.Lines.Add(new JournalEntryLine { AccountId = acc["11910"], DebitAmount = 10_000m, LineOrder = 1 });
        whtJe.Lines.Add(new JournalEntryLine { AccountId = acc["41100"], CreditAmount = 10_000m, LineOrder = 2 });
        db.JournalEntries.Add(whtJe);
        var half = Cit(cid, "HalfYear", 50_000m, 0m, 5_000m, 400_000m);
        db.CorporateIncomeTaxes.Add(half);
        await db.SaveChangesAsync();

        var fm = new FinancialManagementService(db);
        await fm.PostCITAsync(cid, half.Id, "tester");
        // S2: รอบเดียวกันตั้งหนี้ซ้ำไม่ได้ขณะ JE เดิมยังมีผล
        var dupRec = Cit(cid, "HalfYear", 50_000m, 0m, 5_000m, 400_000m);
        db.CorporateIncomeTaxes.Add(dupRec);
        await db.SaveChangesAsync();
        var dup = await Assert.ThrowsAsync<BusinessRuleException>(() => fm.PostCITAsync(cid, dupRec.Id, "tester"));
        Assert.Equal("CIT-POST-DUP", dup.RuleCode);

        var pay = DateTime.UtcNow.Date;
        var r51 = await Svc(db).RemitAsync(cid, new RemitRequest(CitPayment.Pnd51Type, Year, 6, pay, BankGlAccountId: acc["11120"]), "tester");
        Assert.Equal(45_000m, r51.Amount);

        var annual = Cit(cid, "Annual", 200_000m, 45_000m, 10_000m, 1_000_000m);
        db.CorporateIncomeTaxes.Add(annual);
        await db.SaveChangesAsync();
        await fm.PostCITAsync(cid, annual.Id, "tester");
        var dash = await Svc(db).GetDashboardAsync(cid, 24);
        var item = dash.Pending.Single(p => p.RemittanceType == CitPayment.Pnd50Type);
        Assert.Equal(145_000m, item.Amount);
        var r50 = await Svc(db).RemitAsync(cid, new RemitRequest(CitPayment.Pnd50Type, Year, 12, pay, BankGlAccountId: acc["11120"]), "tester");
        Assert.Equal(145_000m, r50.Amount);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(0m, await BalanceAsync(check, acc["11910"]));
        Assert.Equal(0m, await BalanceAsync(check, acc["11920"]));
        Assert.Equal(0m, await BalanceAsync(check, acc["21920"]));
        Assert.Equal(200_000m, await BalanceAsync(check, acc["58000"]));
        Assert.DoesNotContain((await Svc(check).GetDashboardAsync(cid, 24)).Pending, p => CitPayment.IsCit(p.RemittanceType));
    }

    [Fact]
    public async Task ชำระนอกระบบด้วยใบสำคัญหลังตั้งหนี้_ไม่ขึ้นค้าง_ใบก่อนตั้งหนี้ไม่นับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var half = Cit(cid, "HalfYear", 30_000m, 0m, 0m, 300_000m);
        db.CorporateIncomeTaxes.Add(half);
        await db.SaveChangesAsync();
        await new FinancialManagementService(db).PostCITAsync(cid, half.Id, "tester");
        Assert.Contains((await Svc(db).GetDashboardAsync(cid, 24)).Pending, p => p.RemittanceType == CitPayment.Pnd51Type && p.Amount == 30_000m);

        // ใบสำคัญที่ลงวันที่ก่อนตั้งหนี้ ⇒ ไม่นับเป็นการชำระงวดนี้
        var early = new JournalEntry { CompanyId = cid, EntryNumber = "JV-EARLY-228", EntryDate = DateTime.UtcNow.Date.AddDays(-30),
            Status = JournalEntryStatus.Posted, JournalType = JournalType.General, TotalDebit = 30_000m, TotalCredit = 30_000m };
        early.Lines.Add(new JournalEntryLine { AccountId = acc["21920"], DebitAmount = 30_000m, LineOrder = 1 });
        early.Lines.Add(new JournalEntryLine { AccountId = acc["11120"], CreditAmount = 30_000m, LineOrder = 2 });
        db.JournalEntries.Add(early);
        await db.SaveChangesAsync();
        Assert.Contains((await Svc(db).GetDashboardAsync(cid, 24)).Pending, p => p.RemittanceType == CitPayment.Pnd51Type);

        // ใบสำคัญหลังตั้งหนี้ Dr 21920 เต็มก้อน ⇒ ถือว่าชำระนอกเส้นนี้แล้ว
        var later = new JournalEntry { CompanyId = cid, EntryNumber = "JV-LATER-228", EntryDate = DateTime.UtcNow.Date.AddDays(1),
            Status = JournalEntryStatus.Posted, JournalType = JournalType.General, TotalDebit = 30_000m, TotalCredit = 30_000m };
        later.Lines.Add(new JournalEntryLine { AccountId = acc["21920"], DebitAmount = 30_000m, LineOrder = 1 });
        later.Lines.Add(new JournalEntryLine { AccountId = acc["11120"], CreditAmount = 30_000m, LineOrder = 2 });
        db.JournalEntries.Add(later);
        await db.SaveChangesAsync();
        Assert.DoesNotContain((await Svc(db).GetDashboardAsync(cid, 24)).Pending, p => p.RemittanceType == CitPayment.Pnd51Type);
    }

    [Fact]
    public async Task จ่ายอากรพร้อมเงินเพิ่มอากร_บวกกลับเฉพาะเงินเพิ่ม_ยกเลิกแล้วอากรกลับเป็นค้างชำระ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var instrument = DateTime.UtcNow.Date.AddDays(-40);
        var sd = new StampDutyRecord { CompanyId = cid, Reference = "HW-228", RdScheduleNumber = 4, InstrumentType = "HireOfWork",
            InstrumentValue = 1_500m, DutyAmount = StampDutySchedule.Compute(4, 1_500m), InstrumentDate = instrument, PaymentMethod = "ESD" };
        db.StampDutyRecords.Add(sd);
        await db.SaveChangesAsync();
        Assert.Equal(2m, sd.DutyAmount);

        var res = await Svc(db).RemitStampDutyAsync(cid, sd.Id, new StampDutyRemitRequest(DateTime.UtcNow.Date, "ES-0001",
            BankGlAccountId: acc["11120"], Surcharge: 4m), "tester");
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var rec = await check.StampDutyRecords.AsNoTracking().SingleAsync(x => x.Id == sd.Id);
            Assert.Equal(res.Id, rec.StatutoryRemittanceId);
            Assert.NotNull(rec.PaidAt);
            Assert.Equal(2m, await BalanceAsync(check, acc["54810"]));
            // บวกกลับ §65 ตรี(6) = เงินเพิ่มอากร 4 เท่านั้น (ค่าอากร 54810 หักได้)
            Assert.Equal(4m, await new TaxService(check).ComputeSection65TerAddBackAsync(cid, instrument.AddDays(-1), DateTime.UtcNow.Date.AddDays(1)));
        }

        await Svc(db).VoidRemittanceAsync(cid, res.Id, "บันทึกผิด", Guid.NewGuid().ToString());
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var rec = await check.StampDutyRecords.AsNoTracking().SingleAsync(x => x.Id == sd.Id);
            Assert.Null(rec.StatutoryRemittanceId);
            Assert.Null(rec.PaidAt);
            Assert.Equal(0m, await BalanceAsync(check, acc["54810"]));
        }
    }
}
