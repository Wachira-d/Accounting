using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Tax;

/// <summary>
/// <b>ตัวเลข GL ของงวดภาษีเงินได้นิติบุคคล — ตัวโหลดตัวเดียว</b> (รอบ 230 ฝ่ายค้าน S1) ของเครื่องคำนวณหน้าบริหารการเงิน (<c>CalculateCITAsync</c>)
/// และไฟล์ ภ.ง.ด.51 (<c>TaxFilingExportService.ExportPnd51Async</c> เมื่อยังไม่มีรายการคำนวณที่ตั้งหนี้แล้ว) — เดิมสองเส้นประกอบสูตรเองคนละแบบ
/// (ไฟล์ ภ.ง.ด.51 ไม่มีส่วนเกินค่ารับรอง · หัก 580xx)
///
/// <para>ฐาน: <see cref="ReportableJournal"/> (Posted + Reversed · ไม่นับใบปิดบัญชี) · ช่วง [Start, EndInclusive + 1 วัน) (N1 ขอบเดียวกับบวกกลับ) ·
/// ไม่หักค่าใช้จ่ายภาษีเงินได้ (<see cref="CitCalculation.ExceptCitExpense"/>) · บวกกลับ §65 ตรี (<c>ITaxService.ComputeSection65TerAddBackAsync</c>) ·
/// ส่วนเกินค่ารับรอง (<see cref="CitCalculation.EntertainmentExcess"/>) · ทุกคิวรีกรอง <c>CompanyId</c></para>
/// </summary>
public static class CitPeriodFigures
{
    /// <param name="Expenses">ค่าใช้จ่ายจาก GL (ไม่รวม 580xx) <b>+ ค่าเสื่อมที่ยังไม่ลงบัญชี</b> (<see cref="UnbookedDepreciation"/>) — เท่ากับ ภ.ง.ด.50</param>
    public sealed record Figures(decimal Revenue, decimal Expenses, decimal CitExpenseExcluded, decimal Section65TerAddBack,
        decimal EntertainmentExpense, decimal EntertainmentExcess, decimal UnbookedDepreciation = 0m)
    {
        public decimal AccountingProfit => Revenue - Expenses;
        public decimal AutoAddBack => Section65TerAddBack + EntertainmentExcess;
    }

    public static async Task<Figures> LoadAsync(AccountingDbContext db, ITaxService tax, Guid companyId,
        CitCalculation.TaxPeriodRange range, decimal paidUpCapital)
    {
        var start = range.Start;
        var endExclusive = range.EndInclusive.Date.AddDays(1);
        var lines = db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntry.CompanyId == companyId && l.JournalEntry.EntryDate >= start && l.JournalEntry.EntryDate < endExclusive)
            .WherePeriodActivity();
        var revenue = await lines.Where(l => l.Account!.AccountType == AccountType.Revenue)
            .SumAsync(l => (decimal?)(l.CreditAmount - l.DebitAmount)) ?? 0m;
        var expensesAll = await lines.Where(l => l.Account!.AccountType == AccountType.Expense)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
        var expenseRows = await lines.ExceptCitExpense().Where(l => l.Account!.AccountType == AccountType.Expense)
            .Select(l => new { Code = l.Account!.AccountCode, Name = l.Account.AccountName, Amt = l.DebitAmount - l.CreditAmount })
            .ToListAsync();
        var expenses = expenseRows.Sum(x => x.Amt);
        var entertainment = expenseRows.Where(x => CitCalculation.IsEntertainmentAccount(x.Code, x.Name)).Sum(x => x.Amt);
        var section65 = await tax.ComputeSection65TerAddBackAsync(companyId, start, range.EndInclusive);
        var unbookedDep = await UnbookedDepreciationAsync(db, companyId, start, range.EndInclusive);
        return new Figures(revenue, expenses + unbookedDep, expensesAll - expenses, section65, entertainment,
            CitCalculation.EntertainmentExcess(entertainment, revenue, paidUpCapital, range.IsHalfYear), unbookedDep);
    }

    /// <summary>
    /// ค่าเสื่อมของสินทรัพย์ที่ใช้งานอยู่ ในเดือนของงวด [<paramref name="start"/>, <paramref name="endInclusive"/>] ที่<b>ยังไม่ลงบัญชี</b>
    /// (<see cref="CitCalculation.Unbooked"/>) — ตัวเดียวของ ภ.ง.ด.50 (<c>TaxService.GenerateCitReport</c>) และเครื่องคำนวณ/ไฟล์ ภ.ง.ด.51 ·
    /// แถวที่ลงบัญชีแล้วอยู่ใน GL แล้ว (ไม่นับซ้ำ) · (ปี, เดือน) ตามช่วงรอบจริง (B8)
    /// </summary>
    public static async Task<decimal> UnbookedDepreciationAsync(AccountingDbContext db, Guid companyId, DateTime start, DateTime endInclusive)
    {
        int fy = start.Year, fm = start.Month, ty = endInclusive.Year, tm = endInclusive.Month;
        var activeAssets = db.Set<FixedAsset>().Where(a => a.CompanyId == companyId && a.Status == AssetStatus.Active).Select(a => a.Id);
        return await db.AssetDepreciations.AsNoTracking()
            .Where(d => d.CompanyId == companyId && activeAssets.Contains(d.FixedAssetId)
                && (d.Year > fy || (d.Year == fy && d.Month >= fm))
                && (d.Year < ty || (d.Year == ty && d.Month <= tm)))
            .Unbooked()
            .SumAsync(d => (decimal?)d.Amount) ?? 0m;
    }

    /// <summary>รายการคำนวณ ภ.ง.ด.51 ล่าสุดของรอบบัญชี <paramref name="ceFiscalYear"/> ที่ตั้งหนี้แล้วและ JE ยังมีผล (ไม่ลบ · Posted · ไม่ถูกกลับรายการ) — null = ไม่มี ·
    /// ตัวเดียวของไฟล์ ภ.ง.ด.51 และค่าตั้งต้น "ภาษีจ่ายล่วงหน้า" ของ ภ.ง.ด.50</summary>
    public static async Task<CorporateIncomeTax?> LivePostedHalfYearAsync(AccountingDbContext db, Guid companyId, int ceFiscalYear)
    {
        var halves = (await db.CorporateIncomeTaxes.AsNoTracking()
                .Where(c => c.CompanyId == companyId && c.TaxPeriod == CitCalculation.HalfYear && c.JournalEntryId != null)
                .OrderByDescending(c => c.CreatedAt).ToListAsync())
            .Where(c => CitPayment.CeYear(c.TaxYear) == ceFiscalYear).ToList();
        foreach (var h in halves)
        {
            var live = await db.JournalEntries.AsNoTracking().AnyAsync(j => j.CompanyId == companyId && j.Id == h.JournalEntryId
                && !j.IsDeleted && j.Status == JournalEntryStatus.Posted && j.ReversedByEntryId == null);
            if (live) return h;
        }
        return null;
    }
}
