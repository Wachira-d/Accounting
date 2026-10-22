using Accounting.Data;
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
/// ทีม O 2026-10-22 — ภ.ง.ด.50 บนฐานจริง: ใบที่ถูกกลับรายการ (Reversed) + ใบกลับรายการ (Posted) หักล้างกัน ⇒ ฐานรายได้ไม่ถูกหักซ้ำ ·
/// ตัวเทียบความสดใช้ฐานเดียวกับตอนสร้าง (รายงานใหม่ = ตรงกัน) · รายงานที่เก็บยอดเพี้ยนไว้ก่อนแก้: ยื่นแล้ว ⇒ รายงานทบทวนแนะนำยื่นเพิ่มเติม
/// (ไม่แตะตัวเลข) · ร่าง ⇒ สร้างใหม่ได้ยอดถูก + แถว audit ยอดเดิม→ใหม่ · บริษัทอื่นไม่ปน
/// </summary>
[Trait("Category", "Db")]
public class CitReversalDbTests
{
    private readonly ITestOutputHelper _out;
    public CitReversalDbTests(ITestOutputHelper output) => _out = output;

    private static DateTime U(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<Guid> SeedAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบ ภ.ง.ด.50 กลับรายการ", TaxId = "0105556000028", FiscalYearStartMonth = 1 };
        var other = new Company { Name = "บริษัทอื่น (ห้ามปน)", TaxId = "0105556000036", FiscalYearStartMonth = 1 };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();

        ChartOfAccount A(Guid cid, string code, string name, AccountType t)
        {
            var a = new ChartOfAccount { CompanyId = cid, AccountCode = code, AccountName = name, AccountType = t, Level = 4, InputVatClaimable = true };
            db.ChartOfAccounts.Add(a);
            return a;
        }
        var cash = A(company.Id, "11111", "เงินสด", AccountType.Asset);
        var re = A(company.Id, "32000", "กำไรสะสม", AccountType.Equity);
        var rev = A(company.Id, "41000", "รายได้จากการขาย", AccountType.Revenue);
        var exp = A(company.Id, "53000", "ค่าใช้จ่ายบริหาร", AccountType.Expense);
        var oCash = A(other.Id, "11111", "เงินสด", AccountType.Asset);
        var oRev = A(other.Id, "41000", "รายได้จากการขาย", AccountType.Revenue);
        await db.SaveChangesAsync();

        var n = 0;
        JournalEntry Je(Guid cid, DateTime date, Guid dr, Guid cr, decimal amt, JournalEntryStatus st = JournalEntryStatus.Posted,
            bool closing = false, Guid? original = null)
        {
            n++;
            var je = new JournalEntry
            {
                CompanyId = cid, EntryNumber = $"JV-O-{n:D4}", EntryDate = date, Status = st, Description = $"รายการ {n}",
                IsClosingEntry = closing, OriginalEntryId = original, TotalDebit = amt, TotalCredit = amt,
            };
            je.Lines.Add(new JournalEntryLine { AccountId = dr, DebitAmount = amt, LineOrder = 1 });
            je.Lines.Add(new JournalEntryLine { AccountId = cr, CreditAmount = amt, LineOrder = 2 });
            db.JournalEntries.Add(je);
            return je;
        }
        Je(company.Id, U(2025, 3, 10), cash.Id, rev.Id, 5_000m);
        Je(company.Id, U(2025, 4, 10), exp.Id, cash.Id, 2_000.25m);
        Je(company.Id, U(2025, 4, 11), cash.Id, rev.Id, 9_999m, st: JournalEntryStatus.Draft);     // ร่าง — ไม่นับ
        var orig = Je(company.Id, U(2025, 5, 1), cash.Id, rev.Id, 1_000m, st: JournalEntryStatus.Reversed);
        await db.SaveChangesAsync();
        var reversal = Je(company.Id, U(2025, 6, 1), rev.Id, cash.Id, 1_000m, original: orig.Id);   // ใบกลับรายการ
        Je(company.Id, U(2025, 12, 31), rev.Id, re.Id, 5_000m, closing: true);                      // ใบปิดบัญชี — ไม่นับ
        Je(other.Id, U(2025, 6, 15), oCash.Id, oRev.Id, 7_777m);                                    // บริษัทอื่น
        await db.SaveChangesAsync();
        orig.ReversedByEntryId = reversal.Id;
        await db.SaveChangesAsync();
        return company.Id;
    }

    [Fact]
    public async Task ภงด50_ใบที่กลับรายการหักล้างกัน_ฐานรายได้ไม่ถูกหักซ้ำ_ตัวเทียบความสดตรงกัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedAsync(db);
        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new TaxService(req);

        var report = await svc.GenerateTaxReportAsync(cid, new CreateTaxReportRequest(TaxType.CorporateIncomeTax, 2025, 1));
        Assert.Equal(5_000m, report.TotalIncome);          // เดิม (Posted อย่างเดียว) = 4,000 — ใบกลับรายการ −1,000 ถูกนับ ใบเดิมไม่ถูกนับ
        Assert.Contains(report.Lines, l => l.Description == "ค่าใช้จ่ายทั้งปี" && l.IncomeAmount == 2_000.25m);   // ทิศตรงข้าม: ทุกสตางค์เท่าเดิม

        using var read = DbTestDatabase.TryCreateContext()!;
        var opened = await new TaxService(read).GetTaxReportAsync(cid, report.Id);
        Assert.NotNull(opened.Freshness);
        Assert.False(opened.Freshness!.NeedsAttention);
        Assert.Equal(0m, opened.Freshness.RevenueDelta);
        Assert.Equal(0m, opened.Freshness.ExpenseDelta);
        Assert.Empty(await new TaxService(read).GetCitRecomputeReviewAsync(cid));
    }

    [Fact]
    public async Task รายงานที่เก็บยอดเพี้ยนไว้_ยื่นแล้วแนะนำยื่นเพิ่มเติม_ร่างสร้างใหม่ได้ยอดถูกพร้อม_audit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedAsync(db);
        using (var gen = DbTestDatabase.TryCreateContext()!)
            await new TaxService(gen).GenerateTaxReportAsync(cid, new CreateTaxReportRequest(TaxType.CorporateIncomeTax, 2025, 1));

        // จำลองรายงานที่สร้างก่อนแก้ (ฐานหักซ้ำ 1,000) แล้วบันทึกว่ายื่นแล้ว
        var stored = await db.TaxReports.FirstAsync(r => r.CompanyId == cid && r.TaxType == TaxType.CorporateIncomeTax);
        stored.TotalIncome = 4_000m;
        stored.Status = TaxReportStatus.Filed;
        await db.SaveChangesAsync();

        using (var read = DbTestDatabase.TryCreateContext()!)
        {
            var rows = await new TaxService(read).GetCitRecomputeReviewAsync(cid);
            var row = Assert.Single(rows);
            Assert.Equal(2025, row.Year);
            Assert.True(row.AlreadyFiled);
            Assert.Equal(4_000m, row.StoredRevenue);
            Assert.Equal(5_000m, row.FreshRevenue);
            Assert.Equal(1_000m, row.RevenueDelta);
            Assert.False(row.CanRegenerate);
            Assert.Equal(TaxReportRecompute.ActionConsiderAmendedFiling, row.SuggestedAction);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new TaxService(read).RegenerateTaxReportAsync(cid, row.ReportId));
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(4_000m, (await check.TaxReports.AsNoTracking().FirstAsync(r => r.Id == stored.Id)).TotalIncome);   // ยื่นแล้ว ⇒ ไม่แตะ

        // ร่าง ⇒ สร้างใหม่ได้ยอดถูก + แถว audit
        stored.Status = TaxReportStatus.Draft;
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var svc = new TaxService(req);
            var draftRow = Assert.Single(await svc.GetCitRecomputeReviewAsync(cid));
            Assert.Equal(TaxReportRecompute.ActionRegenerate, draftRow.SuggestedAction);
            var regenerated = await svc.RegenerateTaxReportAsync(cid, draftRow.ReportId);
            Assert.Equal(5_000m, regenerated.TotalIncome);
        }
        using var after = DbTestDatabase.TryCreateContext()!;
        Assert.Empty(await new TaxService(after).GetCitRecomputeReviewAsync(cid));
        var audit = await after.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == cid && a.EntityType == nameof(TaxReport))
            .OrderByDescending(a => a.Id).FirstAsync();
        Assert.Contains("\"totalIncome\":4000", audit.OldValues);
        Assert.Contains("\"totalIncome\":5000", audit.NewValues);
        Assert.Contains(TaxReportRecompute.AuditAction, audit.NewValues);
    }
}
