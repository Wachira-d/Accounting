using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class AccountingService
{
    // ============================================================
    // Period closing — soft / hard / year-end (Task 1 of ERP upgrade)
    // ------------------------------------------------------------
    // Soft close: routine month-end. FiscalPeriod.Status flips to
    //   Closed and FiscalPeriod.ClosedAt/By stamped. Validations still
    //   reject new postings but admin can roll back.
    //
    // Hard close (Year-End): January–December of a fiscal year are
    //   sealed. Auto-generates a closing journal entry that:
    //     1. Debits every Revenue account back to zero (Cr balance)
    //     2. Credits every Expense account back to zero (Dr balance)
    //     3. Credits Retained Earnings for net income (or Debits
    //        for net loss).
    //   Then it rolls Asset/Liability/Equity ending balances forward
    //   into the next year's January as OpeningBalance rows.
    // ============================================================

    public async Task<FiscalPeriod> SoftClosePeriodAsync(Guid companyId, Guid periodId, string userId)
    {
        var period = await _db.FiscalPeriods
            .FirstOrDefaultAsync(p => p.Id == periodId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงวดบัญชี");

        if (period.Status == FiscalPeriodStatus.Locked)
            throw new InvalidOperationException("งวดถูกปิดถาวรแล้ว (Locked) — ไม่สามารถ soft close ทับได้");

        // Reject if there are non-Posted JE entries — they'd be left dangling
        var draftCount = await _db.JournalEntries
            .CountAsync(j => j.CompanyId == companyId
                && j.FiscalPeriodId == periodId
                && j.Status == JournalEntryStatus.Draft);
        if (draftCount > 0)
            throw new InvalidOperationException(
                $"ยังมีใบสำคัญ Draft ค้างอยู่ในงวดนี้ {draftCount} รายการ — กรุณา Post หรือ Void ก่อน Close");

        period.Status = FiscalPeriodStatus.Closed;
        period.ClosedAt = DateTime.UtcNow;
        period.ClosedBy = userId;
        period.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return period;
    }

    public async Task<FiscalPeriod> ReopenPeriodAsync(Guid companyId, Guid periodId, string userId)
    {
        var period = await _db.FiscalPeriods
            .FirstOrDefaultAsync(p => p.Id == periodId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงวดบัญชี");
        if (period.Status == FiscalPeriodStatus.Locked)
            throw new InvalidOperationException(
                "งวดถูก lock จาก year-end close — ต้องยกเลิก year-end ก่อนถึงจะเปิดงวดได้");
        period.Status = FiscalPeriodStatus.Open;
        period.ClosedAt = null;
        period.ClosedBy = null;
        period.UpdatedBy = userId;
        period.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return period;
    }

    /// <summary>
    /// Year-end close — seals every month of the fiscal year and
    /// transfers net P&amp;L to Retained Earnings via an auto-generated
    /// JE. Idempotent on a per-year basis (uniq index on
    /// (CompanyId, FiscalYear)).
    /// </summary>
    public async Task<YearEndClosing> YearEndCloseAsync(
        Guid companyId, int fiscalYear, Guid retainedEarningsAccountId,
        DateTime? closingDate, string userId)
    {
        var existing = await _db.YearEndClosings
            .FirstOrDefaultAsync(y => y.CompanyId == companyId && y.FiscalYear == fiscalYear);
        if (existing != null)
            throw new InvalidOperationException(
                $"ปีบัญชี {fiscalYear} ถูกปิดงบไปแล้ว (เลขที่อ้างอิง: {existing.Id})");

        var reAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.Id == retainedEarningsAccountId
                && a.CompanyId == companyId
                && a.AccountType == AccountType.Equity)
            ?? throw new InvalidOperationException(
                "ไม่พบบัญชีกำไรสะสม (Retained Earnings) ที่ระบุ — ต้องเป็นบัญชีประเภท Equity");

        // ── ช่วงของรอบบัญชี (C-T03) ──
        // เดิมตรึง 1 ม.ค. – 31 ธ.ค. ตายตัว ⇒ บริษัทรอบ เม.ย.–มี.ค. ปิดบัญชีด้วย
        // ตัวเลขคนละช่วงกับที่ยื่น DBD/สรรพากร (ซึ่งอ่าน FiscalYearStartMonth ถูก)
        // ⇒ กำไรสะสมผิด + มีตัวเลขสองชุดในระบบเดียว
        var startMonth = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => c.FiscalYearStartMonth)
            .FirstOrDefaultAsync();
        var fy = Accounting.Helpers.FiscalYear.RangeFor(fiscalYear, startMonth);

        var effectiveDate = NormalizeDate(closingDate ?? fy.EndInclusive);

        // Sum every Posted line in revenue + expense accounts for the year.
        var ytdRange = new { From = fy.Start, To = fy.EndExclusive };

        var ytdLines = await (
            from line in _db.JournalEntryLines.AsNoTracking()
            join entry in _db.JournalEntries on line.JournalEntryId equals entry.Id
            join account in _db.ChartOfAccounts on line.AccountId equals account.Id
            where entry.CompanyId == companyId
                && (entry.Status == JournalEntryStatus.Posted || entry.Status == JournalEntryStatus.Reversed)
                // ★ C-T02: ถ้ามีใบปิดของรอบก่อน (หรือใบปิดรายเดือนของเวอร์ชันเก่า)
                // ค้างอยู่ในช่วงนี้ การนับเข้ามาจะทำให้ยอดที่ต้องปิดเป็น 0
                // ⇒ ปิดปีแล้วกำไรสะสมไม่ขยับ (ปิดของที่ถูกปิดไปแล้ว)
                && !entry.IsClosingEntry
                && entry.EntryDate >= ytdRange.From && entry.EntryDate < ytdRange.To
                && (account.AccountType == AccountType.Revenue || account.AccountType == AccountType.Expense)
            select new
            {
                AccountId = account.Id,
                account.AccountCode,
                account.AccountType,
                line.DebitAmount,
                line.CreditAmount,
            }
        ).ToListAsync();

        if (ytdLines.Count == 0)
            throw new InvalidOperationException(
                $"ไม่พบรายการบัญชีในปี {fiscalYear} — ไม่มียอดที่ต้องปิด");

        // Per-account net balance: revenue is naturally Cr-balance, expense Dr-balance.
        var perAccount = ytdLines
            .GroupBy(x => new { x.AccountId, x.AccountCode, x.AccountType })
            .Select(g => new
            {
                g.Key.AccountId,
                g.Key.AccountCode,
                g.Key.AccountType,
                NetCredit = g.Sum(x => x.CreditAmount) - g.Sum(x => x.DebitAmount), // Revenue: positive
                NetDebit = g.Sum(x => x.DebitAmount) - g.Sum(x => x.CreditAmount),  // Expense: positive
            })
            .Where(x => x.NetCredit != 0 || x.NetDebit != 0)
            .ToList();

        decimal totalRevenue = perAccount.Where(x => x.AccountType == AccountType.Revenue).Sum(x => x.NetCredit);
        decimal totalExpense = perAccount.Where(x => x.AccountType == AccountType.Expense).Sum(x => x.NetDebit);
        decimal netIncome = totalRevenue - totalExpense;   // positive = profit

        // Build the closing JE lines: zero out each revenue/expense account,
        // post the net to RE. We use the revenue/expense's natural balance
        // direction reversed so the account ends at 0 after this entry.
        var lines = new List<JournalLineRequest>();
        foreach (var acc in perAccount)
        {
            if (acc.AccountType == AccountType.Revenue && acc.NetCredit > 0)
                lines.Add(new JournalLineRequest(acc.AccountId, acc.NetCredit, 0,
                    $"ปิดบัญชีรายได้สิ้นปี {fiscalYear} — {acc.AccountCode}"));
            else if (acc.AccountType == AccountType.Revenue && acc.NetCredit < 0)
                lines.Add(new JournalLineRequest(acc.AccountId, 0, -acc.NetCredit,
                    $"ปิดบัญชีรายได้สิ้นปี {fiscalYear} — {acc.AccountCode} (negative)"));
            else if (acc.AccountType == AccountType.Expense && acc.NetDebit > 0)
                lines.Add(new JournalLineRequest(acc.AccountId, 0, acc.NetDebit,
                    $"ปิดบัญชีค่าใช้จ่ายสิ้นปี {fiscalYear} — {acc.AccountCode}"));
            else if (acc.AccountType == AccountType.Expense && acc.NetDebit < 0)
                lines.Add(new JournalLineRequest(acc.AccountId, -acc.NetDebit, 0,
                    $"ปิดบัญชีค่าใช้จ่ายสิ้นปี {fiscalYear} — {acc.AccountCode} (negative)"));
        }

        // Balance to Retained Earnings: positive netIncome → Cr RE.
        if (netIncome > 0)
            lines.Add(new JournalLineRequest(reAccount.Id, 0, netIncome,
                $"โอนกำไรสะสมประจำปี {fiscalYear}"));
        else if (netIncome < 0)
            lines.Add(new JournalLineRequest(reAccount.Id, -netIncome, 0,
                $"โอนขาดทุนสะสมประจำปี {fiscalYear}"));

        if (lines.Count < 2)
            throw new InvalidOperationException("ไม่มีรายการที่ต้องปิดสิ้นปี — ตรวจสอบข้อมูลก่อน");

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Create the closing JE — IsAutoGenerated so it's protected
            // from manual edit; the year-end audit row points at this JE.
            var closeRequest = new CreateJournalEntryRequest(
                EntryDate: effectiveDate,
                Description: $"ปิดบัญชีสิ้นปี {fiscalYear} — โอน P&L ไป Retained Earnings",
                Reference: $"YE-{fiscalYear}",
                Lines: lines,
                JournalType: JournalType.General);
            var closeJe = await CreateJournalEntryAsync(companyId, closeRequest, userId);

            // Mark the entry IsAutoGenerated so Update/Delete guards trigger
            var closeEntity = await _db.JournalEntries.FindAsync(closeJe.Id);
            if (closeEntity != null)
            {
                closeEntity.IsAutoGenerated = true;
                // ★ C-T02: ธงนี้ทำให้งบกำไรขาดทุนคัดใบปิดออก — ถ้าไม่ติด
                // งบของรอบที่ปิดแล้วจะเป็น 0 เพราะใบปิดกลับด้านทุกบรรทัดพอดี
                closeEntity.IsClosingEntry = true;
                closeEntity.Status = JournalEntryStatus.Posted;
            }

            // Lock every period in the year
            var periods = await _db.FiscalPeriods
                .Where(p => p.CompanyId == companyId && p.Year == fiscalYear)
                .ToListAsync();
            foreach (var p in periods)
            {
                p.Status = FiscalPeriodStatus.Locked;
                p.LockedAt = DateTime.UtcNow;
                p.LockedBy = userId;
                if (p.Month == 12) p.YearEndJournalEntryId = closeJe.Id;
                p.UpdatedAt = DateTime.UtcNow;
            }

            // Audit row
            var closing = new YearEndClosing
            {
                CompanyId = companyId,
                FiscalYear = fiscalYear,
                RetainedEarningsAccountId = retainedEarningsAccountId,
                TransferredAmount = netIncome,
                ClosingJournalEntryId = closeJe.Id,
                LockedFiscalPeriods = true,
                CreatedBy = userId,
            };
            _db.YearEndClosings.Add(closing);

            await _db.SaveChangesAsync();

            // Roll-over: seed January of (fiscalYear + 1) with opening
            // balances from current ending balances of Asset/Liability/Equity.
            await RollOpeningBalancesAsync(companyId, fiscalYear + 1, userId);

            // Auto-create the remaining 12 monthly periods of the new year so
            // the user never has to add periods by hand each year.
            await EnsureFiscalYearPeriodsAsync(companyId, fiscalYear + 1);

            await tx.CommitAsync();
            return closing;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Compute ending balances for every Asset/Liability/Equity account at
    /// the end of (year-1) and write them as OpeningBalance rows attached
    /// to January of (year). Idempotent — overwrites any existing rows
    /// for the same (company, period, account).
    /// </summary>
    public async Task<int> RollOpeningBalancesAsync(Guid companyId, int year, string userId)
    {
        // Find or create the Jan target period.
        var jan = await _db.FiscalPeriods
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Year == year && p.Month == 1);
        if (jan == null)
        {
            jan = new FiscalPeriod
            {
                CompanyId = companyId,
                Name = $"{year}/01",
                Year = year,
                Month = 1,
                StartDate = new DateTime(year, 1, 1),
                EndDate = new DateTime(year, 1, 31),
                Status = FiscalPeriodStatus.Open,
                CreatedBy = userId,
            };
            _db.FiscalPeriods.Add(jan);
            await _db.SaveChangesAsync();
        }

        var asOfExclusive = new DateTime(year, 1, 1);
        var perAccount = await (
            from line in _db.JournalEntryLines.AsNoTracking()
            join entry in _db.JournalEntries on line.JournalEntryId equals entry.Id
            join account in _db.ChartOfAccounts on line.AccountId equals account.Id
            where entry.CompanyId == companyId
                && entry.EntryDate < asOfExclusive
                && (entry.Status == JournalEntryStatus.Posted || entry.Status == JournalEntryStatus.Reversed)
                && (account.AccountType == AccountType.Asset
                    || account.AccountType == AccountType.Liability
                    || account.AccountType == AccountType.Equity)
            group new { line.DebitAmount, line.CreditAmount } by account.Id into g
            select new
            {
                AccountId = g.Key,
                NetDebit = g.Sum(x => x.DebitAmount),
                NetCredit = g.Sum(x => x.CreditAmount),
            }
        ).ToListAsync();

        // Add prior-year opening balances on top (the chain continues).
        var prevPeriodId = await _db.FiscalPeriods
            .Where(p => p.CompanyId == companyId && p.Year == year - 1 && p.Month == 1)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync();
        var prevOpenings = prevPeriodId.HasValue
            ? await _db.OpeningBalances
                .Where(o => o.FiscalPeriodId == prevPeriodId.Value && o.CompanyId == companyId)
                .Select(o => new { o.AccountId, o.OpeningDebit, o.OpeningCredit })
                .ToListAsync()
            : new();

        var existing = await _db.OpeningBalances
            .Where(o => o.FiscalPeriodId == jan.Id && o.CompanyId == companyId)
            .ToListAsync();

        int written = 0;
        var allAccountIds = perAccount.Select(p => p.AccountId)
            .Union(prevOpenings.Select(p => p.AccountId)).Distinct();
        foreach (var accountId in allAccountIds)
        {
            var movements = perAccount.FirstOrDefault(p => p.AccountId == accountId);
            var prior = prevOpenings.FirstOrDefault(p => p.AccountId == accountId);
            var debit = (movements?.NetDebit ?? 0) + (prior?.OpeningDebit ?? 0);
            var credit = (movements?.NetCredit ?? 0) + (prior?.OpeningCredit ?? 0);
            if (debit == 0 && credit == 0) continue;

            var row = existing.FirstOrDefault(o => o.AccountId == accountId);
            if (row == null)
            {
                _db.OpeningBalances.Add(new OpeningBalance
                {
                    CompanyId = companyId,
                    FiscalPeriodId = jan.Id,
                    AccountId = accountId,
                    OpeningDebit = debit,
                    OpeningCredit = credit,
                    CreatedBy = userId,
                });
            }
            else
            {
                row.OpeningDebit = debit;
                row.OpeningCredit = credit;
                row.UpdatedBy = userId;
                row.UpdatedAt = DateTime.UtcNow;
            }
            written++;
        }

        await _db.SaveChangesAsync();
        return written;
    }
}
