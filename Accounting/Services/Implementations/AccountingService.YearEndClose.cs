using Accounting.Helpers;
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

        // ═══ ยอดยกมาของปีใหม่ = ฐานยอดยกมาล่าสุดก่อน 1 ม.ค. + รายการตั้งแต่วันเริ่มฐานนั้น ═══ (ฝ่ายค้านรอบส่งออก 2026-10-15 P1-1)
        // เดิม = รายการ "ทั้งหมดตั้งแต่เปิดบริษัท" + ยอดยกมา ม.ค. ปีก่อน (ซึ่งรวมทุกปีก่อนหน้าไว้แล้ว) ⇒ ตั้งแต่ปิดปีครั้งที่สอง
        // ปีเก่าถูกนับซ้ำ และงบทดลอง/บัญชีแยกประเภท/แดชบอร์ด/ยอด ณ วันที่ อ่านฐานเดียวกันจึงผิดตรงกันหมด · ยอดนำเข้ากลางปี
        // (ย้ายระบบ ก.ค.) ก็ไม่ถูกนับเลย · ตัวคำนวณตัวเดียว ComputeRolledOpeningsAsync (ใช้ร่วมกับรายงานตรวจ/ซ่อม)
        var rolled = await ComputeRolledOpeningsAsync(companyId, year, null);

        var existing = await _db.OpeningBalances
            .Where(o => o.FiscalPeriodId == jan.Id && o.CompanyId == companyId)
            .ToListAsync();

        int written = 0;
        // แถวที่การปิดปีรอบก่อนเขียนไว้แต่ยอดใหม่เป็นศูนย์ (เช่น ยกเลิกรายการไปแล้ว) ต้องถูกล้าง — ไม่งั้นค่าเก่าค้าง ·
        // แถวนำเข้า (OpeningRoll.IsImported) ไม่ถูกล้าง (เป็นยอดที่ผู้ใช้ลงเอง)
        var accountIds = rolled.Keys
            .Union(existing.Where(o => !OpeningRoll.IsImported(o.Notes)).Select(o => o.AccountId))
            .ToList();
        foreach (var accountId in accountIds)
        {
            rolled.TryGetValue(accountId, out var amounts);
            var debit = amounts.Debit;
            var credit = amounts.Credit;
            var row = existing.FirstOrDefault(o => o.AccountId == accountId);
            if (debit == 0 && credit == 0 && (row == null || (row.OpeningDebit == 0 && row.OpeningCredit == 0))) continue;

            if (row == null)
            {
                _db.OpeningBalances.Add(new OpeningBalance
                {
                    CompanyId = companyId,
                    FiscalPeriodId = jan.Id,
                    AccountId = accountId,
                    OpeningDebit = debit,
                    OpeningCredit = credit,
                    Notes = OpeningRoll.RolledNote(year),
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

    /// <summary>
    /// ยอดยกมา 1 ม.ค. ของ <paramref name="year"/> ที่ควรเป็น (นิยามใน <see cref="OpeningRoll"/>) — ตัวคำนวณตัวเดียวของการปิดปี
    /// และรายงานตรวจ/ซ่อม · <paramref name="correctedAnchors"/> = ยอดที่ "ควรเป็น" ของงวดฐานที่รายงานคำนวณไว้แล้วในรอบเดียวกัน
    /// (ปีที่สองขึ้นไปของสายที่ผิด ต้องคิดบนฐานที่แก้แล้ว ไม่ใช่ฐานที่เก็บผิด)
    /// </summary>
    private async Task<Dictionary<Guid, (decimal Debit, decimal Credit)>> ComputeRolledOpeningsAsync(
        Guid companyId, int year, IReadOnlyDictionary<Guid, Dictionary<Guid, (decimal Debit, decimal Credit)>>? correctedAnchors)
    {
        var asOfExclusive = new DateTime(year, 1, 1);

        // ฐาน = งวดล่าสุดที่มียอดยกมาและเริ่มก่อน 1 ม.ค. ปีเป้าหมาย — เกณฑ์เดียวกับงบทดลอง ณ 31 ธ.ค. ปีก่อน
        var anchor = await (
            from p in _db.FiscalPeriods.AsNoTracking()
            join o in _db.OpeningBalances.AsNoTracking() on p.Id equals o.FiscalPeriodId
            where p.CompanyId == companyId && o.CompanyId == companyId && p.StartDate < asOfExclusive
            orderby p.StartDate descending
            select new { p.Id, p.StartDate }
        ).FirstOrDefaultAsync();

        var anchorRows = new Dictionary<Guid, (decimal Debit, decimal Credit)>();
        if (anchor != null)
        {
            if (correctedAnchors != null && correctedAnchors.TryGetValue(anchor.Id, out var corrected))
                anchorRows = new Dictionary<Guid, (decimal Debit, decimal Credit)>(corrected);
            else
                anchorRows = await _db.OpeningBalances.AsNoTracking()
                    .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == anchor.Id)
                    .GroupBy(o => o.AccountId)
                    .Select(g => new { AccountId = g.Key, Debit = g.Sum(x => x.OpeningDebit), Credit = g.Sum(x => x.OpeningCredit) })
                    .ToDictionaryAsync(x => x.AccountId, x => (x.Debit, x.Credit));
        }
        DateTime? movementStart = anchor?.StartDate;

        var movements = await (
            from line in _db.JournalEntryLines.AsNoTracking()
            join entry in _db.JournalEntries on line.JournalEntryId equals entry.Id
            join account in _db.ChartOfAccounts on line.AccountId equals account.Id
            where entry.CompanyId == companyId
                && entry.EntryDate < asOfExclusive
                && (movementStart == null || entry.EntryDate >= movementStart.Value)
                && (entry.Status == JournalEntryStatus.Posted || entry.Status == JournalEntryStatus.Reversed)
                && (account.AccountType == AccountType.Asset
                    || account.AccountType == AccountType.Liability
                    || account.AccountType == AccountType.Equity)
            group new { line.DebitAmount, line.CreditAmount } by account.Id into g
            select new { AccountId = g.Key, Debit = g.Sum(x => x.DebitAmount), Credit = g.Sum(x => x.CreditAmount) }
        ).ToDictionaryAsync(x => x.AccountId, x => (x.Debit, x.Credit));

        return OpeningRoll.Combine(anchorRows, movements);
    }

    // ═══ ตรวจ/ซ่อมยอดยกมาจากการปิดปี — เครื่องมือเจ้าของบริษัท (ฝ่ายค้านรอบส่งออก 2026-10-15 P1-1 · F2 ข้อ 9) ═══
    // รายงานก่อน (อ่านอย่างเดียว) → ซ่อมเฉพาะปีที่ผู้ใช้เลือกเมื่อกด + audit chain ทุกบัญชี · ไม่มีงานไหนซ่อมเองตอนเปิดระบบ
    // (แบบเดียวกับ FindProductTotalMismatchesAsync → RepairProductTotalsAsync)

    public async Task<List<OpeningRollCheckYear>> FindOpeningRollMismatchesAsync(Guid companyId)
        => (await EvaluateOpeningRollChainAsync(companyId)).Select(x => x.Report).ToList();

    private sealed record OpeningRollEvaluation(
        OpeningRollCheckYear Report, Guid? PeriodId, Dictionary<Guid, (decimal Debit, decimal Credit)>? Expected);

    /// <summary>เดินทุกปีที่ปิดแล้วจากเก่าไปใหม่ · ปีถัดไปคิดบนฐานที่แก้แล้วของปีก่อน (ไม่ใช่ฐานที่เก็บผิด)</summary>
    private async Task<List<OpeningRollEvaluation>> EvaluateOpeningRollChainAsync(Guid companyId)
    {
        var targetYears = await _db.YearEndClosings.AsNoTracking()
            .Where(y => y.CompanyId == companyId)
            .Select(y => y.FiscalYear + 1)
            .Distinct()
            .OrderBy(y => y)
            .ToListAsync();

        var corrected = new Dictionary<Guid, Dictionary<Guid, (decimal Debit, decimal Credit)>>();
        var result = new List<OpeningRollEvaluation>();
        foreach (var year in targetYears)
        {
            var janId = await _db.FiscalPeriods.AsNoTracking()
                .Where(p => p.CompanyId == companyId && p.Year == year && p.Month == 1)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync();
            if (janId == null)
            {
                result.Add(new(new OpeningRollCheckYear(year, null, false, "ไม่มีงวด ม.ค. ของปีนี้ (ยังไม่เคยยกยอด)", new()), null, null));
                continue;
            }
            var storedRows = await _db.OpeningBalances.AsNoTracking()
                .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == janId.Value)
                .Select(o => new { o.AccountId, o.OpeningDebit, o.OpeningCredit, o.Notes })
                .ToListAsync();
            if (storedRows.Any(o => OpeningRoll.IsImported(o.Notes)))
            {
                // ผู้ใช้ลงยอดยกมาจากระบบเดิมในงวดนี้เอง — ยอดที่เก็บคือความตั้งใจของผู้ใช้ (ปีถัดไปใช้ยอดที่เก็บเป็นฐาน)
                result.Add(new(new OpeningRollCheckYear(year, janId, true,
                    "งวดนี้มียอดยกมาที่นำเข้าจากระบบเดิม — ไม่ซ่อมอัตโนมัติ (ตรวจกับงบทดลองแล้วแก้ที่หน้าย้ายระบบ)", new()), janId, null));
                continue;
            }

            var expected = await ComputeRolledOpeningsAsync(companyId, year, corrected);
            corrected[janId.Value] = expected;
            var stored = storedRows.GroupBy(o => o.AccountId)
                .ToDictionary(g => g.Key, g => (Debit: g.Sum(x => x.OpeningDebit), Credit: g.Sum(x => x.OpeningCredit)));
            var diff = OpeningRoll.Diff(stored, expected);

            var ids = diff.Select(d => d.AccountId).ToList();
            var accounts = new Dictionary<Guid, (string Code, string Name)>();
            if (ids.Count > 0)
                foreach (var acct in await _db.ChartOfAccounts.AsNoTracking()
                             .Where(a => a.CompanyId == companyId && ids.Contains(a.Id))
                             .Select(a => new { a.Id, a.AccountCode, a.AccountName })
                             .ToListAsync())
                    accounts[acct.Id] = (acct.AccountCode, acct.AccountName);
            var mismatches = diff
                .Select(d =>
                {
                    var found = accounts.TryGetValue(d.AccountId, out var acc);
                    return new OpeningRollMismatch(d.AccountId, found ? acc.Code : "", found ? acc.Name : "",
                        d.Stored.Debit, d.Stored.Credit, d.Expected.Debit, d.Expected.Credit);
                })
                .OrderBy(m => m.AccountCode, StringComparer.Ordinal)
                .ToList();
            result.Add(new(new OpeningRollCheckYear(year, janId, false, null, mismatches), janId, expected));
        }
        return result;
    }

    public async Task<OpeningRollRepairResult> RepairOpeningRollAsync(Guid companyId, IReadOnlyCollection<int> years, string actor)
    {
        if (years == null || years.Count == 0)
            throw new BusinessRuleException("ยังไม่ได้เลือกปีที่จะซ่อม — เปิดรายงานตรวจยอดยกมาจากการปิดปีแล้วเลือกปีก่อน", OpeningRoll.RuleCode);

        await using var tx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        // หนึ่งการซ่อมต่อบริษัทในเวลาเดียวกัน (สองคนกดพร้อมกัน = audit ซ้ำ)
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            AdvisoryLockKey.For(companyId, AdvisoryLockKey.OpeningRoll, "repair"));

        var chain = await EvaluateOpeningRollChainAsync(companyId);
        var wanted = years.ToHashSet();
        Guid? actorId = Guid.TryParse(actor, out var uid) ? uid : null;
        var repairedYears = 0;
        var repairedRows = 0;
        var skipped = new List<string>();
        foreach (var y in wanted.Where(w => chain.All(c => c.Report.Year != w)).OrderBy(w => w))
            skipped.Add($"{y}: ไม่ใช่ปีที่เกิดจากการปิดปี");

        foreach (var ev in chain.Where(c => wanted.Contains(c.Report.Year)))
        {
            if (ev.Expected == null || ev.PeriodId == null)
            {
                skipped.Add($"{ev.Report.Year}: {ev.Report.SkipReason}");
                continue;
            }
            if (ev.Report.Mismatches.Count == 0)
            {
                skipped.Add($"{ev.Report.Year}: ยอดถูกอยู่แล้ว");
                continue;
            }
            var periodId = ev.PeriodId.Value;
            var rows = await _db.OpeningBalances
                .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == periodId)
                .ToListAsync();
            foreach (var m in ev.Report.Mismatches)
            {
                var accRows = rows.Where(o => o.AccountId == m.AccountId).ToList();
                var row = accRows.FirstOrDefault();
                if (row == null)
                {
                    row = new OpeningBalance
                    {
                        CompanyId = companyId, FiscalPeriodId = periodId, AccountId = m.AccountId,
                        OpeningDebit = m.ExpectedDebit, OpeningCredit = m.ExpectedCredit,
                        Notes = OpeningRoll.RolledNote(ev.Report.Year), CreatedBy = actor,
                    };
                    _db.OpeningBalances.Add(row);
                }
                else
                {
                    row.OpeningDebit = m.ExpectedDebit;
                    row.OpeningCredit = m.ExpectedCredit;
                    row.Notes ??= OpeningRoll.RolledNote(ev.Report.Year);
                    row.UpdatedBy = actor;
                    row.UpdatedAt = DateTime.UtcNow;
                    foreach (var extra in accRows.Skip(1))
                    {
                        extra.OpeningDebit = 0m;
                        extra.OpeningCredit = 0m;
                        extra.UpdatedBy = actor;
                        extra.UpdatedAt = DateTime.UtcNow;
                    }
                }
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = actorId,
                    EntityType = nameof(OpeningBalance),
                    EntityId = row.Id.ToString(),
                    Action = AuditAction.Update,
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        year = ev.Report.Year, accountCode = m.AccountCode, debit = m.StoredDebit, credit = m.StoredCredit,
                    }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "opening-roll-repair",
                        year = ev.Report.Year, accountCode = m.AccountCode, debit = m.ExpectedDebit, credit = m.ExpectedCredit,
                        ruleCode = OpeningRoll.RuleCode,
                        by = actor,
                    }),
                    Timestamp = DateTime.UtcNow,
                });
                repairedRows++;
            }
            repairedYears++;
        }

        await _db.SaveChangesAsync();
        if (tx != null) await tx.CommitAsync();
        return new OpeningRollRepairResult(repairedYears, repairedRows, skipped);
    }
}
