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
        // รอบ 2 (ฝ่ายค้าน 2026-10-15 P2-7): ปิดปี · ยกยอดด้วยมือ · ซ่อมยอดยกมา ใช้ล็อกเดียวกันต่อบริษัท (เดิมล็อกแค่การซ่อม ⇒ เขียนงวดเดียวกันทับกันได้)
        // · ตรวจ "ปิดไปแล้ว" หลังได้ล็อก (สองคนกดพร้อมกันไม่ได้ใบปิดสองใบ)
        await using var tx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", OpeningRoll.LockKey(companyId));

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

        // ═══ ปิดอะไร = ฐานเดียวกับงบทดลอง ═══ (รอบ 2 ฝ่ายค้าน 2026-10-15 P1-2 · OpeningRoll.PnlCloseWindow)
        // เดิมปิดเฉพาะรายการตั้งแต่ต้นรอบ ⇒ บริษัทที่ย้ายระบบกลางปี (ยอดนำเข้า 1 ก.ค. มีกำไรขาดทุน ม.ค.–มิ.ย.) ไม่ถูกปิดส่วนที่นำเข้า
        // แล้วการยกยอดพาแถวรายได้/ค่าใช้จ่ายของฐานไปปีถัดไป + รายการก่อนวันฐานในรอบถูกปิดเข้ากำไรสะสมทั้งที่งบทดลองไม่นับ ⇒ ยอดยกมาไม่ลงตัว
        // ใหม่: ฐานเริ่มในรอบนี้ ⇒ ปิดรายได้/ค่าใช้จ่ายของฐาน + รายการตั้งแต่วันเริ่มฐาน (รายการก่อนวันฐาน = ไม่นับ เหมือนงบทดลอง)
        var anchor = await LatestOpeningAnchorAsync(companyId, fy.EndExclusive);
        var (pnlFrom, includeAnchorPnl) = OpeningRoll.PnlCloseWindow(fy.Start, anchor?.StartDate);
        var ytdRange = new { From = pnlFrom, To = fy.EndExclusive };

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
            group new { line.DebitAmount, line.CreditAmount } by new { AccountId = account.Id, account.AccountCode, account.AccountType } into g
            select new ClosingPnlRow(g.Key.AccountId, g.Key.AccountCode, g.Key.AccountType,
                g.Sum(x => x.DebitAmount), g.Sum(x => x.CreditAmount))
        ).ToListAsync();

        var anchorPnl = new List<ClosingPnlRow>();
        if (includeAnchorPnl && anchor != null)
        {
            var anchorId = anchor.Id;
            anchorPnl = await (
                from o in _db.OpeningBalances.AsNoTracking()
                join account in _db.ChartOfAccounts on o.AccountId equals account.Id
                where o.CompanyId == companyId && o.FiscalPeriodId == anchorId && account.CompanyId == companyId
                    && (account.AccountType == AccountType.Revenue || account.AccountType == AccountType.Expense)
                group new { o.OpeningDebit, o.OpeningCredit } by new { AccountId = account.Id, account.AccountCode, account.AccountType } into g
                select new ClosingPnlRow(g.Key.AccountId, g.Key.AccountCode, g.Key.AccountType,
                    g.Sum(x => x.OpeningDebit), g.Sum(x => x.OpeningCredit))
            ).ToListAsync();
        }

        // Per-account net balance: revenue is naturally Cr-balance, expense Dr-balance.
        var perAccount = ytdLines.Concat(anchorPnl)
            .GroupBy(x => new { x.AccountId, x.AccountCode, x.AccountType })
            .Select(g => new
            {
                g.Key.AccountId,
                g.Key.AccountCode,
                g.Key.AccountType,
                NetCredit = g.Sum(x => x.Credit) - g.Sum(x => x.Debit), // Revenue: positive
                NetDebit = g.Sum(x => x.Debit) - g.Sum(x => x.Credit),  // Expense: positive
            })
            .Where(x => x.NetCredit != 0 || x.NetDebit != 0)
            .OrderBy(x => x.AccountCode, StringComparer.Ordinal)
            .ToList();

        if (perAccount.Count == 0)
            throw new InvalidOperationException(
                $"ไม่พบรายการบัญชีในปี {fiscalYear} — ไม่มียอดที่ต้องปิด");

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

        // Lock every period of the fiscal year — รอบ 2 P2-6: งวดในช่วงรอบบัญชี (รอบ เม.ย.–มี.ค. = เม.ย. ปีนี้ – มี.ค. ปีหน้า)
        // ไม่ใช่ "Year == ปีที่ปิด" (เดิมล็อก ม.ค.–มี.ค. ของรอบก่อน และไม่ล็อก ม.ค.–มี.ค. ของรอบนี้) · ปีปฏิทินได้ชุดเดิม
        var firstKey = fy.Start.Year * 12 + fy.Start.Month;
        var lastMonth = fy.EndExclusive.AddMonths(-1);
        var lastKey = lastMonth.Year * 12 + lastMonth.Month;
        var periods = await _db.FiscalPeriods
            .Where(p => p.CompanyId == companyId && p.Year * 12 + p.Month >= firstKey && p.Year * 12 + p.Month <= lastKey)
            .ToListAsync();
        foreach (var p in periods)
        {
            p.Status = FiscalPeriodStatus.Locked;
            p.LockedAt = DateTime.UtcNow;
            p.LockedBy = userId;
            if (p.Year == lastMonth.Year && p.Month == lastMonth.Month) p.YearEndJournalEntryId = closeJe.Id;
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

        // Roll-over: งวดแรกของรอบบัญชีถัดไป (รอบ 2 P2-6 — เดิม 1 ม.ค. เสมอ ⇒ รอบ เม.ย.–มี.ค. งบทดลองตั้งแต่ ม.ค. ไม่ลงตัว)
        // · งวดเป้าหมายที่มียอดนำเข้า = ไม่เขียนทับ (ยอดที่ผู้ใช้ลงเอง · รายงานตรวจยอดยกมาแสดงเหตุผล)
        await RollIntoNextFiscalYearAsync(companyId, fiscalYear, retainedEarningsAccountId, startMonth, userId, failIfImported: false);

        // Auto-create the remaining monthly periods of the next fiscal year so
        // the user never has to add periods by hand each year.
        var nextFy = Accounting.Helpers.FiscalYear.RangeFor(fiscalYear + 1, startMonth);
        await EnsureFiscalYearPeriodsAsync(companyId, nextFy.Start.Year);
        if (nextFy.EndInclusive.Year != nextFy.Start.Year)
            await EnsureFiscalYearPeriodsAsync(companyId, nextFy.EndInclusive.Year);

        if (tx != null) await tx.CommitAsync();
        return closing;
    }

    private sealed record ClosingPnlRow(Guid AccountId, string AccountCode, AccountType AccountType, decimal Debit, decimal Credit);

    /// <summary>
    /// ยกยอดด้วยมือ (endpoint <c>roll-opening-balances</c>) — ยอดยกมาของรอบบัญชี <paramref name="year"/> จากงบทดลอง ณ สิ้นรอบ
    /// <paramref name="year"/>−1 (<see cref="OpeningRoll"/>) · รอบ 2 P2-7: เดิมข้ามด่านทั้งหมด — ตอนนี้ (1) ต้องปิดรอบ <paramref name="year"/>−1
    /// แล้ว (ผังกำไรสะสมมาจากการปิดปี) (2) ล็อกเดียวกับปิดปี/ซ่อม (3) ไม่เขียนทับงวดที่มียอดนำเข้า (4) audit ทุกบัญชีที่เปลี่ยน
    /// </summary>
    public async Task<int> RollOpeningBalancesAsync(Guid companyId, int year, string userId)
    {
        await using var tx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", OpeningRoll.LockKey(companyId));

        var closing = await _db.YearEndClosings.AsNoTracking()
            .FirstOrDefaultAsync(y => y.CompanyId == companyId && y.FiscalYear == year - 1)
            ?? throw new BusinessRuleException(
                $"ยังไม่ได้ปิดปี {year - 1} — ยกยอดมาปี {year} ได้หลังปิดปีเท่านั้น (ปุ่ม \"ปิดปี\" ยกยอดให้เอง) · "
                + "ยกยอดก่อนปิดปีจะได้ยอดยกมาที่ไม่มีกำไรขาดทุนของปีนั้นในกำไรสะสม", OpeningRoll.RuleCode);
        var startMonth = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => c.FiscalYearStartMonth)
            .FirstOrDefaultAsync();

        var written = await RollIntoNextFiscalYearAsync(companyId, year - 1, closing.RetainedEarningsAccountId, startMonth, userId, failIfImported: true);
        if (tx != null) await tx.CommitAsync();
        return written;
    }

    /// <summary>งวดที่เริ่มเดือน/ปีของ <paramref name="start"/> (งวดแรกของรอบบัญชี) — ไม่มี = สร้าง</summary>
    private async Task<FiscalPeriod> FindOrCreatePeriodStartingAsync(Guid companyId, DateTime start, string userId)
    {
        var period = await _db.FiscalPeriods
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Year == start.Year && p.Month == start.Month);
        if (period != null) return period;
        period = new FiscalPeriod
        {
            CompanyId = companyId,
            Name = $"{start.Year}/{start.Month:D2}",
            Year = start.Year,
            Month = start.Month,
            StartDate = start,
            EndDate = start.AddMonths(1).AddDays(-1),
            Status = FiscalPeriodStatus.Open,
            CreatedBy = userId,
        };
        _db.FiscalPeriods.Add(period);
        await _db.SaveChangesAsync();
        return period;
    }

    /// <summary>
    /// เขียนยอดยกมาของรอบ <paramref name="closedFiscalYear"/>+1 (งวดแรกของรอบนั้น) — ผู้เรียกถือล็อก <see cref="OpeningRoll.LockKey"/> แล้ว ·
    /// งวดที่มียอดนำเข้า: <paramref name="failIfImported"/> ⇒ ล้มดังพร้อมทางไปต่อ · ไม่งั้นข้าม (ปิดปีไม่ล้มเพราะผู้ใช้ลงยอดเอง) ·
    /// แถวที่การยกยอดรอบก่อนเขียนไว้แต่ยอดใหม่เป็นศูนย์ถูกล้าง · แถวซ้ำของบัญชีเดียวถูกรวม · audit ทุกบัญชีที่ยอดเปลี่ยน
    /// </summary>
    private async Task<int> RollIntoNextFiscalYearAsync(Guid companyId, int closedFiscalYear, Guid retainedEarningsAccountId,
        int startMonth, string userId, bool failIfImported)
    {
        var next = Accounting.Helpers.FiscalYear.RangeFor(closedFiscalYear + 1, startMonth);
        var target = await FindOrCreatePeriodStartingAsync(companyId, next.Start, userId);

        var existing = await _db.OpeningBalances
            .Where(o => o.FiscalPeriodId == target.Id && o.CompanyId == companyId)
            .ToListAsync();
        if (existing.Any(o => OpeningRoll.IsImported(o.Notes)))
        {
            if (failIfImported)
                throw new BusinessRuleException(
                    $"งวด {target.Name} มียอดยกมาที่นำเข้าจากระบบเดิม — ระบบไม่เขียนทับยอดที่ผู้ใช้ลงเอง · "
                    + "ตรวจกับงบทดลองแล้วแก้ที่หน้าย้ายระบบ (หรือเปิดรายงาน \"ตรวจยอดยกมาจากการปิดปี\")", OpeningRoll.RuleCode);
            return 0;
        }

        // ═══ ยอดยกมา = งบทดลอง ณ สิ้นรอบ (ยกเฉพาะ สินทรัพย์/หนี้สิน/ทุน · กำไรขาดทุนค้างรวมเข้ากำไรสะสม) ═══
        // (ฝ่ายค้านรอบส่งออก 2026-10-15 P1-1 + รอบ 2 P1-2/P2-6) ตัวคำนวณตัวเดียว ComputeRolledOpeningsAsync (ใช้ร่วมกับรายงานตรวจ/ซ่อม)
        var rolled = await ComputeRolledOpeningsAsync(companyId, next.Start, retainedEarningsAccountId, null);

        Guid? actorId = Guid.TryParse(userId, out var uid) ? uid : null;
        int written = 0;
        // แถวที่การปิดปีรอบก่อนเขียนไว้แต่ยอดใหม่เป็นศูนย์ (เช่น ยกเลิกรายการไปแล้ว) ต้องถูกล้าง — ไม่งั้นค่าเก่าค้าง
        var accountIds = rolled.Keys.Union(existing.Select(o => o.AccountId)).ToList();
        foreach (var accountId in accountIds)
        {
            rolled.TryGetValue(accountId, out var amounts);
            var accRows = existing.Where(o => o.AccountId == accountId).ToList();
            var oldDebit = accRows.Sum(o => o.OpeningDebit);
            var oldCredit = accRows.Sum(o => o.OpeningCredit);
            if (accRows.Count <= 1 && oldDebit == amounts.Debit && oldCredit == amounts.Credit) continue;

            var row = accRows.FirstOrDefault();
            if (row == null)
            {
                row = new OpeningBalance
                {
                    CompanyId = companyId,
                    FiscalPeriodId = target.Id,
                    AccountId = accountId,
                    OpeningDebit = amounts.Debit,
                    OpeningCredit = amounts.Credit,
                    Notes = OpeningRoll.RolledNote(next.Start.Year),
                    CreatedBy = userId,
                };
                _db.OpeningBalances.Add(row);
            }
            else
            {
                row.OpeningDebit = amounts.Debit;
                row.OpeningCredit = amounts.Credit;
                row.UpdatedBy = userId;
                row.UpdatedAt = DateTime.UtcNow;
                foreach (var extra in accRows.Skip(1))
                {
                    extra.OpeningDebit = 0m;
                    extra.OpeningCredit = 0m;
                    extra.UpdatedBy = userId;
                    extra.UpdatedAt = DateTime.UtcNow;
                }
            }
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = actorId,
                EntityType = nameof(OpeningBalance),
                EntityId = row.Id.ToString(),
                Action = accRows.Count == 0 ? AuditAction.Create : AuditAction.Update,
                OldValues = System.Text.Json.JsonSerializer.Serialize(new { period = target.Name, debit = oldDebit, credit = oldCredit, rows = accRows.Count }),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "opening-roll", period = target.Name, closedFiscalYear,
                    debit = amounts.Debit, credit = amounts.Credit, ruleCode = OpeningRoll.RuleCode, by = userId,
                }),
                Timestamp = DateTime.UtcNow,
            });
            written++;
        }

        await _db.SaveChangesAsync();
        return written;
    }

    private sealed record OpeningAnchor(Guid Id, DateTime StartDate);

    /// <summary>ฐานยอดยกมาล่าสุดที่เริ่ม<b>ก่อน</b> <paramref name="asOfExclusive"/> (งวดที่มีแถว OpeningBalances) — เกณฑ์เดียวกับงบทดลอง ณ วันก่อนหน้า ·
    /// ใช้ร่วมกันโดยใบปิดปีและการยกยอด/รายงานตรวจ</summary>
    private async Task<OpeningAnchor?> LatestOpeningAnchorAsync(Guid companyId, DateTime asOfExclusive)
        => await (
            from p in _db.FiscalPeriods.AsNoTracking()
            join o in _db.OpeningBalances.AsNoTracking() on p.Id equals o.FiscalPeriodId
            where p.CompanyId == companyId && o.CompanyId == companyId && p.StartDate < asOfExclusive
            orderby p.StartDate descending
            select new OpeningAnchor(p.Id, p.StartDate)
        ).FirstOrDefaultAsync();

    /// <summary>
    /// ยอดยกมา ณ <paramref name="asOfExclusive"/> (ต้นรอบบัญชีถัดไป) ที่ควรเป็น (นิยามใน <see cref="OpeningRoll"/>) — ตัวคำนวณตัวเดียวของการปิดปี/ยกยอด
    /// และรายงานตรวจ/ซ่อม · = งบทดลอง ณ วันก่อนหน้า (ฐานล่าสุดก่อนวันนั้น + ทุกรายการตั้งแต่วันเริ่มฐาน) แล้ว <see cref="OpeningRoll.CarryForward"/> ·
    /// <paramref name="correctedAnchors"/> = ยอดที่ "ควรเป็น" ของงวดฐานที่รายงานคำนวณไว้แล้วในรอบเดียวกัน
    /// (ปีที่สองขึ้นไปของสายที่ผิด ต้องคิดบนฐานที่แก้แล้ว ไม่ใช่ฐานที่เก็บผิด)
    /// </summary>
    private async Task<Dictionary<Guid, (decimal Debit, decimal Credit)>> ComputeRolledOpeningsAsync(
        Guid companyId, DateTime asOfExclusive, Guid? retainedEarningsAccountId,
        IReadOnlyDictionary<Guid, Dictionary<Guid, (decimal Debit, decimal Credit)>>? correctedAnchors)
    {
        // ฐาน = งวดล่าสุดที่มียอดยกมาและเริ่มก่อนต้นรอบเป้าหมาย — เกณฑ์เดียวกับงบทดลอง ณ วันสิ้นรอบ
        var anchor = await LatestOpeningAnchorAsync(companyId, asOfExclusive);

        var anchorRows = new Dictionary<Guid, (decimal Debit, decimal Credit)>();
        if (anchor != null)
        {
            var anchorId = anchor.Id;
            if (correctedAnchors != null && correctedAnchors.TryGetValue(anchorId, out var corrected))
                anchorRows = new Dictionary<Guid, (decimal Debit, decimal Credit)>(corrected);
            else
                anchorRows = await _db.OpeningBalances.AsNoTracking()
                    .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == anchorId)
                    .GroupBy(o => o.AccountId)
                    .Select(g => new { AccountId = g.Key, Debit = g.Sum(x => x.OpeningDebit), Credit = g.Sum(x => x.OpeningCredit) })
                    .ToDictionaryAsync(x => x.AccountId, x => (x.Debit, x.Credit));
        }
        DateTime? movementStart = anchor?.StartDate;

        // ทุกประเภทบัญชี (รวมใบปิดปี) = งบทดลอง ณ สิ้นรอบ · รายได้/ค่าใช้จ่ายที่เหลือ = ยังไม่ถูกปิด ⇒ CarryForward รวมเข้ากำไรสะสม
        var movements = await (
            from line in _db.JournalEntryLines.AsNoTracking()
            join entry in _db.JournalEntries on line.JournalEntryId equals entry.Id
            join account in _db.ChartOfAccounts on line.AccountId equals account.Id
            where entry.CompanyId == companyId
                && entry.EntryDate < asOfExclusive
                && (movementStart == null || entry.EntryDate >= movementStart.Value)
                && (entry.Status == JournalEntryStatus.Posted || entry.Status == JournalEntryStatus.Reversed)
            group new { line.DebitAmount, line.CreditAmount } by account.Id into g
            select new { AccountId = g.Key, Debit = g.Sum(x => x.DebitAmount), Credit = g.Sum(x => x.CreditAmount) }
        ).ToDictionaryAsync(x => x.AccountId, x => (x.Debit, x.Credit));

        var trialBalance = OpeningRoll.Combine(anchorRows, movements);
        var ids = trialBalance.Keys.ToList();
        var types = ids.Count == 0
            ? new Dictionary<Guid, AccountType>()
            : await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && ids.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.AccountType);
        return OpeningRoll.CarryForward(trialBalance, types, retainedEarningsAccountId);
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
        // ปีที่ปิดแล้ว → ยอดยกมาของรอบถัดไป · ผังกำไรสะสม = ผังที่ใบปิดปีของรอบนั้นใช้ (รายได้/ค่าใช้จ่ายค้างรวมเข้าผังนี้ — รอบ 2 P1-2)
        var closings = (await _db.YearEndClosings.AsNoTracking()
                .Where(y => y.CompanyId == companyId)
                .Select(y => new { y.FiscalYear, y.RetainedEarningsAccountId })
                .ToListAsync())
            .GroupBy(y => y.FiscalYear)
            .Select(g => g.First())
            .OrderBy(y => y.FiscalYear)
            .ToList();
        var startMonth = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => c.FiscalYearStartMonth)
            .FirstOrDefaultAsync();

        var corrected = new Dictionary<Guid, Dictionary<Guid, (decimal Debit, decimal Credit)>>();
        var result = new List<OpeningRollEvaluation>();
        foreach (var closed in closings)
        {
            var year = closed.FiscalYear + 1;
            // งวดเป้าหมาย = งวดแรกของรอบบัญชีถัดไป (รอบ 2 P2-6 — รอบ เม.ย.–มี.ค. = เม.ย. ไม่ใช่ ม.ค.)
            var nextStart = Accounting.Helpers.FiscalYear.RangeFor(year, startMonth).Start;
            var janId = await _db.FiscalPeriods.AsNoTracking()
                .Where(p => p.CompanyId == companyId && p.Year == nextStart.Year && p.Month == nextStart.Month)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync();
            if (janId == null)
            {
                result.Add(new(new OpeningRollCheckYear(year, null, false, "ไม่มีงวดแรกของรอบบัญชีนี้ (ยังไม่เคยยกยอด)", new()), null, null));
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

            var expected = await ComputeRolledOpeningsAsync(companyId, nextStart, closed.RetainedEarningsAccountId, corrected);
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
        // หนึ่งการซ่อมต่อบริษัทในเวลาเดียวกัน (สองคนกดพร้อมกัน = audit ซ้ำ) · ล็อกเดียวกับปิดปี/ยกยอดด้วยมือ (รอบ 2 P2-7)
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", OpeningRoll.LockKey(companyId));

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
                    // รอบ 2 P2-7: แถวซ้ำของบัญชีเดียวที่ถูกตั้งเป็นศูนย์ก็มี audit ของตัวเอง (เดิมเปลี่ยนเงียบ ๆ — ยอดเดิมหายจากประวัติ)
                    foreach (var extra in accRows.Skip(1))
                    {
                        _db.AddChainedAuditLog(new AuditLog
                        {
                            CompanyId = companyId,
                            UserId = actorId,
                            EntityType = nameof(OpeningBalance),
                            EntityId = extra.Id.ToString(),
                            Action = AuditAction.Update,
                            OldValues = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                year = ev.Report.Year, accountCode = m.AccountCode, debit = extra.OpeningDebit, credit = extra.OpeningCredit,
                            }),
                            NewValues = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                action = "opening-roll-dup-zeroed", mergedInto = row.Id,
                                year = ev.Report.Year, accountCode = m.AccountCode, debit = 0m, credit = 0m,
                                ruleCode = OpeningRoll.RuleCode, by = actor,
                            }),
                            Timestamp = DateTime.UtcNow,
                        });
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
