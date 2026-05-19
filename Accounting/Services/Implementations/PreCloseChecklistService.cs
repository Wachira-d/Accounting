using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// Pre-Close Checklist (Phase J of accountant-tools). Aggregates 8 health
/// checks every Thai accountant runs before closing a month — surfaces
/// them as red/green items on a single dashboard so they don't have to
/// hop across 6 pages every period-end.
/// </summary>
public class PreCloseChecklistService
{
    private readonly AccountingDbContext _db;
    public PreCloseChecklistService(AccountingDbContext db) { _db = db; }

    public record CheckItem(string Code, string Label, bool Passed, string Severity,
        string Message, int Count, string? ActionUrl);

    public record ChecklistResult(int Year, int Month, bool ReadyToClose,
        int PassedCount, int FailedCount, int WarningCount, List<CheckItem> Items);

    public async Task<ChecklistResult> RunAsync(Guid companyId, int year, int month)
    {
        var periodStart = new DateTime(year, month, 1);
        var periodEnd = periodStart.AddMonths(1);
        var items = new List<CheckItem>();

        // 1. No Draft JEs left in period
        var draftJeCount = await _db.JournalEntries.AsNoTracking()
            .CountAsync(j => j.CompanyId == companyId
                && j.EntryDate >= periodStart && j.EntryDate < periodEnd
                && j.Status == JournalEntryStatus.Draft);
        items.Add(new("DRAFT_JE", "ใบสำคัญที่ยัง Draft", draftJeCount == 0,
            draftJeCount == 0 ? "Info" : "Error",
            draftJeCount == 0 ? "ไม่มี Draft JE ค้าง" : $"พบ Draft JE ค้าง {draftJeCount} ใบ — ต้อง Post หรือ Void ก่อนปิดงวด",
            draftJeCount, "/pages/journals.html?status=Draft"));

        // 2. Posted JE balance check (Dr=Cr per entry — should always be true but verify)
        var unbalancedJeCount = await _db.JournalEntries.AsNoTracking()
            .CountAsync(j => j.CompanyId == companyId
                && j.EntryDate >= periodStart && j.EntryDate < periodEnd
                && j.Status == JournalEntryStatus.Posted
                && j.TotalDebit != j.TotalCredit);
        items.Add(new("JE_BALANCE", "ใบสำคัญสมดุล Dr=Cr", unbalancedJeCount == 0,
            unbalancedJeCount == 0 ? "Info" : "Error",
            unbalancedJeCount == 0 ? "ใบสำคัญทุกใบสมดุล" : $"⚠️ พบใบสำคัญไม่สมดุล {unbalancedJeCount} ใบ — เร่งตรวจสอบ",
            unbalancedJeCount, "/pages/journals.html"));

        // 3. Unreconciled bank transactions in period
        var unrecBankCount = await _db.Set<BankTransaction>().AsNoTracking()
            .CountAsync(t => t.CompanyId == companyId && !t.IsDeleted
                && t.TransactionDate >= periodStart && t.TransactionDate < periodEnd
                && t.ReconciliationStatus == ReconciliationStatus.Unmatched);
        items.Add(new("BANK_UNRECONCILED", "Bank transaction ยังไม่กระทบยอด", unrecBankCount == 0,
            unrecBankCount == 0 ? "Info" : (unrecBankCount < 5 ? "Warning" : "Error"),
            unrecBankCount == 0 ? "Bank reconciled ครบทุกรายการ" : $"พบ {unrecBankCount} รายการที่ยังไม่กระทบยอด",
            unrecBankCount, "/pages/bank.html"));

        // 4. Documents pending approval that should have been actioned
        var pendingDocCount = await _db.Documents.AsNoTracking()
            .CountAsync(d => d.CompanyId == companyId && !d.IsDeleted
                && d.DocumentDate >= periodStart && d.DocumentDate < periodEnd
                && d.Status == DocumentStatus.WaitingApproval);
        items.Add(new("DOC_PENDING_APPROVAL", "เอกสารรออนุมัติ", pendingDocCount == 0,
            pendingDocCount == 0 ? "Info" : "Warning",
            pendingDocCount == 0 ? "เอกสารอนุมัติครบ" : $"พบเอกสารรออนุมัติ {pendingDocCount} ฉบับ",
            pendingDocCount, "/pages/approval.html"));

        // 5. VAT report exists for this period
        var vatExists = await _db.TaxReports.AsNoTracking()
            .AnyAsync(r => r.CompanyId == companyId && r.TaxType == TaxType.VAT
                && r.Year == year && r.Month == month);
        items.Add(new("VAT_REPORT", "รายงาน ภพ.30 ของงวด", vatExists,
            vatExists ? "Info" : "Warning",
            vatExists ? "สร้างรายงาน ภพ.30 แล้ว" : "ยังไม่ได้สร้างรายงาน ภพ.30",
            0, "/pages/tax.html"));

        // 6. WHT reports exist
        var whtExists = await _db.TaxReports.AsNoTracking()
            .CountAsync(r => r.CompanyId == companyId
                && (r.TaxType == TaxType.WithholdingTax3 || r.TaxType == TaxType.WithholdingTax53
                    || r.TaxType == TaxType.WithholdingTax1)
                && r.Year == year && r.Month == month);
        items.Add(new("WHT_REPORTS", "รายงาน ภงด. (1/3/53)", whtExists >= 1,
            whtExists >= 1 ? "Info" : "Warning",
            whtExists >= 1 ? $"สร้างรายงาน WHT {whtExists} ฉบับ" : "ยังไม่ได้สร้างรายงาน WHT",
            whtExists, "/pages/wht.html"));

        // 7. Fixed Assets depreciation run for the month
        // Heuristic: any auto-generated JE in the period that references a
        // depreciation expense account (51x or 53x with "เสื่อม" in name).
        var depJeCount = await (
            from j in _db.JournalEntries
            join l in _db.JournalEntryLines on j.Id equals l.JournalEntryId
            join a in _db.ChartOfAccounts on l.AccountId equals a.Id
            where j.CompanyId == companyId
                && j.EntryDate >= periodStart && j.EntryDate < periodEnd
                && j.Status == JournalEntryStatus.Posted
                && a.AccountName.Contains("ค่าเสื่อม")
            select j.Id).Distinct().CountAsync();
        var hasFa = await _db.FixedAssets.AsNoTracking()
            .AnyAsync(a => a.CompanyId == companyId && !a.IsDeleted && a.Status == AssetStatus.Active);
        var depPassed = !hasFa || depJeCount > 0;
        items.Add(new("DEPRECIATION_RUN", "รันค่าเสื่อมสินทรัพย์ถาวร", depPassed,
            depPassed ? "Info" : "Warning",
            !hasFa ? "ไม่มีสินทรัพย์ถาวร — ข้ามได้"
                : (depJeCount > 0 ? $"พบ JE ค่าเสื่อมในงวด {depJeCount} ใบ"
                                  : "⚠️ ยังไม่ได้รันค่าเสื่อมในงวดนี้"),
            depJeCount, "/pages/fixed-assets.html"));

        // 8. Period not already closed
        var period = await _db.FiscalPeriods.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Year == year && p.Month == month);
        var notClosed = period == null || period.Status == FiscalPeriodStatus.Open;
        items.Add(new("PERIOD_OPEN", "สถานะงวดบัญชี", notClosed,
            notClosed ? "Info" : "Warning",
            period == null ? "ยังไม่มีงวดบัญชีของเดือนนี้ — ระบบจะสร้างให้อัตโนมัติเมื่อปิด"
                : (notClosed ? "งวด Open — พร้อมปิด"
                    : $"งวดนี้สถานะ {period.Status} อยู่แล้ว"),
            0, "/pages/fiscal.html"));

        var failed = items.Count(i => !i.Passed && i.Severity == "Error");
        var warning = items.Count(i => !i.Passed && i.Severity == "Warning");
        var passed = items.Count(i => i.Passed);
        return new ChecklistResult(year, month, failed == 0, passed, failed, warning, items);
    }
}
