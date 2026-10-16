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
            throw new BusinessRuleException(YearEndReopenRule.LockedPeriodMessage(period.Name), YearEndReopenRule.RuleCode);
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

        // รอบ 4 P2-a: งวดยกยอดรุ่นเก่าไป ม.ค. ที่ยังไม่ซ่อม ⇒ ล้มดังพร้อมทางไปต่อก่อนเขียนอะไร (ตัวตรวจเดียวกับรายงาน/ซ่อม) ·
        // งวดรุ่นเก่าที่ยังไม่อยู่ในปีที่ปิดแล้ว (ซ่อมไม่ได้จนกว่าปีนั้นจะปิด) ไม่ใช้เป็นฐาน — เกณฑ์เดียวกับยอดที่ควรเป็นของรายงาน
        var legacyAnchorIds = await EnsureNoUnrepairedLegacyRollAsync(companyId, startMonth, fiscalYear);

        var effectiveDate = NormalizeDate(closingDate ?? fy.EndInclusive);

        // ═══ ปิดอะไร = ฐานเดียวกับงบทดลอง ═══ (รอบ 2 ฝ่ายค้าน 2026-10-15 P1-2 · OpeningRoll.PnlCloseWindow)
        // เดิมปิดเฉพาะรายการตั้งแต่ต้นรอบ ⇒ บริษัทที่ย้ายระบบกลางปี (ยอดนำเข้า 1 ก.ค. มีกำไรขาดทุน ม.ค.–มิ.ย.) ไม่ถูกปิดส่วนที่นำเข้า
        // แล้วการยกยอดพาแถวรายได้/ค่าใช้จ่ายของฐานไปปีถัดไป + รายการก่อนวันฐานในรอบถูกปิดเข้ากำไรสะสมทั้งที่งบทดลองไม่นับ ⇒ ยอดยกมาไม่ลงตัว
        // ใหม่: ฐานเริ่มในรอบนี้ ⇒ ปิดรายได้/ค่าใช้จ่ายของฐาน + รายการตั้งแต่วันเริ่มฐาน (รายการก่อนวันฐาน = ไม่นับ เหมือนงบทดลอง)
        var anchor = await LatestOpeningAnchorAsync(companyId, fy.EndExclusive, legacyAnchorIds);
        // รอบ 3 P2-3: ฐานที่เริ่มตรงต้นรอบ (งบทดลองก่อนปิดบัญชี 31 ธ.ค. ที่นำเข้า 1 ม.ค.) = กำไรขาดทุนของปีก่อน — ปิดเข้ากำไรสะสมแยกบรรทัด ไม่นับในกำไรของปี
        var (pnlFrom, anchorScope) = OpeningRoll.PnlCloseWindow(fy.Start, anchor?.StartDate);
        var ytdRange = new { From = pnlFrom, To = fy.EndExclusive };

        var ytdLines = await (
            from line in _db.JournalEntryLines.AsNoTracking()
            join entry in _db.JournalEntries on line.JournalEntryId equals entry.Id
            // รอบ 4 P2-b: รวมผังที่ถูกลบแบบ soft ของบริษัทนี้ — เดิมตัวกรองตัดบรรทัดของผังรายได้/ค่าใช้จ่ายที่ลบทิ้ง ⇒ ไม่ถูกล้าง ⇒ ปิดปีถัดไปล้มทุกครั้ง
            join account in _db.ChartOfAccounts.IgnoreQueryFilters() on line.AccountId equals account.Id
            where entry.CompanyId == companyId && account.CompanyId == companyId
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
        if (anchorScope != AnchorPnlScope.None && anchor != null)
        {
            var anchorId = anchor.Id;
            anchorPnl = await (
                from o in _db.OpeningBalances.AsNoTracking()
                join account in _db.ChartOfAccounts.IgnoreQueryFilters() on o.AccountId equals account.Id
                where o.CompanyId == companyId && o.FiscalPeriodId == anchorId && account.CompanyId == companyId
                    && (account.AccountType == AccountType.Revenue || account.AccountType == AccountType.Expense)
                group new { o.OpeningDebit, o.OpeningCredit } by new { AccountId = account.Id, account.AccountCode, account.AccountType } into g
                select new ClosingPnlRow(g.Key.AccountId, g.Key.AccountCode, g.Key.AccountType,
                    g.Sum(x => x.OpeningDebit), g.Sum(x => x.OpeningCredit))
            ).ToListAsync();
        }

        // Per-account net balance: revenue is naturally Cr-balance, expense Dr-balance.
        // กำไรของปี = รายการในรอบ (+ ฐานที่เริ่มกลางรอบ) · กำไรขาดทุนของปีก่อนที่ค้างในฐาน ณ ต้นรอบ = แยกชุด (รอบ 3 P2-3)
        var perAccount = NetClosingRows(anchorScope == AnchorPnlScope.CurrentYear ? ytdLines.Concat(anchorPnl) : ytdLines);
        var priorPerAccount = NetClosingRows(anchorScope == AnchorPnlScope.PriorYears ? anchorPnl : Enumerable.Empty<ClosingPnlRow>());

        if (perAccount.Count == 0 && priorPerAccount.Count == 0)
            throw new InvalidOperationException(
                $"ไม่พบรายการบัญชีในปี {fiscalYear} — ไม่มียอดที่ต้องปิด");

        decimal totalRevenue = perAccount.Where(x => x.AccountType == AccountType.Revenue).Sum(x => x.NetCredit);
        decimal totalExpense = perAccount.Where(x => x.AccountType == AccountType.Expense).Sum(x => x.NetDebit);
        decimal netIncome = totalRevenue - totalExpense;   // positive = profit (ของปีนี้เท่านั้น)

        // Build the closing JE lines: zero out each revenue/expense account,
        // post the net to RE. We use the revenue/expense's natural balance
        // direction reversed so the account ends at 0 after this entry.
        var lines = new List<JournalLineRequest>();
        AppendClosingLines(lines, perAccount, (kind, code) => $"ปิดบัญชี{kind}สิ้นปี {fiscalYear} — {code}");

        // Balance to Retained Earnings: positive netIncome → Cr RE.
        if (netIncome > 0)
            lines.Add(new JournalLineRequest(reAccount.Id, 0, netIncome,
                $"โอนกำไรสะสมประจำปี {fiscalYear}"));
        else if (netIncome < 0)
            lines.Add(new JournalLineRequest(reAccount.Id, -netIncome, 0,
                $"โอนขาดทุนสะสมประจำปี {fiscalYear}"));

        // กำไรขาดทุนของปีก่อนที่ค้างในยอดยกมา ณ ต้นรอบ — ปิดเข้ากำไรสะสม (งบทดลองสิ้นปีต้องไม่มียอดรายได้/ค่าใช้จ่ายค้าง) แต่แยกบรรทัด
        // และไม่อยู่ใน TransferredAmount (กำไรของปี) · กำไรสะสมรวมถูกเท่าเดิม
        if (priorPerAccount.Count > 0)
        {
            var anchorLabel = ThaiDate.ToThaiDisplayString(fy.Start);
            AppendClosingLines(lines, priorPerAccount, (kind, code) => $"ปิดยอด{kind}ของปีก่อนที่ค้างในยอดยกมา {anchorLabel} — {code}");
            var priorNet = priorPerAccount.Where(x => x.AccountType == AccountType.Revenue).Sum(x => x.NetCredit)
                - priorPerAccount.Where(x => x.AccountType == AccountType.Expense).Sum(x => x.NetDebit);
            if (priorNet > 0)
                lines.Add(new JournalLineRequest(reAccount.Id, 0, priorNet,
                    $"{OpeningRoll.PriorYearsPnlLabel}: โอนกำไรที่ค้างในยอดยกมา {anchorLabel} เข้ากำไรสะสม (ไม่นับในกำไรประจำปี {fiscalYear})"));
            else if (priorNet < 0)
                lines.Add(new JournalLineRequest(reAccount.Id, -priorNet, 0,
                    $"{OpeningRoll.PriorYearsPnlLabel}: โอนขาดทุนที่ค้างในยอดยกมา {anchorLabel} เข้ากำไรสะสม (ไม่นับในกำไรประจำปี {fiscalYear})"));
        }

        if (lines.Count < 2)
            throw new InvalidOperationException("ไม่มีรายการที่ต้องปิดสิ้นปี — ตรวจสอบข้อมูลก่อน");

        // รอบ 4 P2-b: ผังสินทรัพย์/หนี้สิน/ทุนที่ถูกลบแต่ยังมียอด ⇒ ล้มดัง<b>ก่อน</b>สร้างใบปิดปี (ข้อความบอก "ยังไม่มีอะไรถูกบันทึก" — ต้องจริง) ·
        // ใบปิดปีไม่แตะผังเหล่านั้น (ล้างเฉพาะรายได้/ค่าใช้จ่าย → กำไรสะสมที่ยังใช้งาน) ⇒ งบทดลองก่อนใบปิดให้คำตอบเดียวกับตอนยกยอด ·
        // งวดเป้าหมายที่มียอดนำเข้า = ไม่ยกยอด (เดิม) ⇒ ไม่ตรวจ
        var next = Accounting.Helpers.FiscalYear.RangeFor(fiscalYear + 1, startMonth);
        if (!await PeriodStartingHasImportedRowsAsync(companyId, next.Start))
            await LoadRollTrialBalanceAsync(companyId, next.Start, null, legacyAnchorIds);

        // ผังรายได้/ค่าใช้จ่ายที่ถูกลบแต่มีรายการในรอบ — ใบปิดปีต้องล้าง (ด่านผังของการสร้างใบยอมเฉพาะชุดนี้ · ตรวจแล้วว่าเป็นของบริษัทนี้)
        var closingAccountIds = lines.Select(l => l.AccountId).Distinct().ToList();
        var deletedClosingAccounts = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.IsDeleted && closingAccountIds.Contains(a.Id)
                && (a.AccountType == AccountType.Revenue || a.AccountType == AccountType.Expense))
            .Select(a => a.Id)
            .ToListAsync();

        // งวดทุกเดือนของรอบ (ล็อกหลังสร้างใบ) — รอบ 2 P2-6: งวดในช่วงรอบบัญชี (รอบ เม.ย.–มี.ค. = เม.ย. ปีนี้ – มี.ค. ปีหน้)
        // ไม่ใช่ "Year == ปีที่ปิด" (เดิมล็อก ม.ค.–มี.ค. ของรอบก่อน และไม่ล็อก ม.ค.–มี.ค. ของรอบนี้) · ปีปฏิทินได้ชุดเดิม
        var firstKey = fy.Start.Year * 12 + fy.Start.Month;
        var lastMonth = fy.EndExclusive.AddMonths(-1);
        var lastKey = lastMonth.Year * 12 + lastMonth.Month;
        var periods = await _db.FiscalPeriods
            .Where(p => p.CompanyId == companyId && p.Year * 12 + p.Month >= firstKey && p.Year * 12 + p.Month <= lastKey)
            .ToListAsync();

        // ยกเลิกการปิดปี (2026-10-16) — บั๊กข้างเคียง: งวดของวันที่ใบปิดปีถูกปิดชั่วคราว (soft close ธ.ค. ตามปกติของการปิดงวดรายเดือน) ⇒ ตัวสร้างใบปฏิเสธ
        // "งวดถูกปิดแล้ว" ⇒ ปิดปีไม่ได้เลยโดยไม่มีทางไปต่อ · งวดนั้นอยู่ในรอบที่กำลังปิดและถูกล็อกต่อในธุรกรรมเดียวกัน ⇒ เปิดชั่วคราวใต้ล็อกเดียวกัน + audit
        // (งวดนอกรอบ — ผู้ใช้เลือกวันปิดนอกรอบ — ไม่แตะ: ตัวสร้างใบยังปฏิเสธตามเดิม)
        var closingDatePeriod = periods.FirstOrDefault(p => p.StartDate.Date <= effectiveDate.Date && p.EndDate.Date >= effectiveDate.Date);
        if (closingDatePeriod is { Status: FiscalPeriodStatus.Closed })
        {
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = Guid.TryParse(userId, out var closeActor) ? closeActor : null,
                EntityType = nameof(FiscalPeriod),
                EntityId = closingDatePeriod.Id.ToString(),
                Action = AuditAction.Update,
                OldValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    period = closingDatePeriod.Name, status = closingDatePeriod.Status.ToString(), closingDatePeriod.ClosedAt, closingDatePeriod.ClosedBy,
                }),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = YearEndCloseThroughSoftClosedAction, fiscalYear, period = closingDatePeriod.Name,
                    status = FiscalPeriodStatus.Locked.ToString(), note = "เปิดชั่วคราวเพื่อลงใบปิดปี แล้วล็อกในธุรกรรมเดียวกัน", by = userId,
                }),
                Timestamp = DateTime.UtcNow,
            });
            closingDatePeriod.Status = FiscalPeriodStatus.Open;
            closingDatePeriod.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        // Create the closing JE — IsAutoGenerated so it's protected
        // from manual edit; the year-end audit row points at this JE.
        var closeRequest = new CreateJournalEntryRequest(
            EntryDate: effectiveDate,
            Description: $"ปิดบัญชีสิ้นปี {fiscalYear} — โอน P&L ไป Retained Earnings",
            Reference: $"YE-{fiscalYear}",
            Lines: lines,
            JournalType: JournalType.General);
        var closeJeId = await CreateJournalEntryCoreAsync(companyId, closeRequest, userId, deletedClosingAccounts);

        // Mark the entry IsAutoGenerated so Update/Delete guards trigger
        var closeEntity = await _db.JournalEntries.FindAsync(closeJeId);
        if (closeEntity != null)
        {
            closeEntity.IsAutoGenerated = true;
            // ★ C-T02: ธงนี้ทำให้งบกำไรขาดทุนคัดใบปิดออก — ถ้าไม่ติด
            // งบของรอบที่ปิดแล้วจะเป็น 0 เพราะใบปิดกลับด้านทุกบรรทัดพอดี
            closeEntity.IsClosingEntry = true;
            closeEntity.Status = JournalEntryStatus.Posted;
        }

        // Lock every period of the fiscal year (ชุดที่โหลดไว้ก่อนสร้างใบ)
        foreach (var p in periods)
        {
            p.Status = FiscalPeriodStatus.Locked;
            p.LockedAt = DateTime.UtcNow;
            p.LockedBy = userId;
            if (p.Year == lastMonth.Year && p.Month == lastMonth.Month) p.YearEndJournalEntryId = closeJeId;
            p.UpdatedAt = DateTime.UtcNow;
        }

        // Audit row
        var closing = new YearEndClosing
        {
            CompanyId = companyId,
            FiscalYear = fiscalYear,
            RetainedEarningsAccountId = retainedEarningsAccountId,
            TransferredAmount = netIncome,
            ClosingJournalEntryId = closeJeId,
            LockedFiscalPeriods = true,
            CreatedBy = userId,
        };
        _db.YearEndClosings.Add(closing);

        await _db.SaveChangesAsync();

        // Roll-over: งวดแรกของรอบบัญชีถัดไป (รอบ 2 P2-6 — เดิม 1 ม.ค. เสมอ ⇒ รอบ เม.ย.–มี.ค. งบทดลองตั้งแต่ ม.ค. ไม่ลงตัว)
        // · งวดเป้าหมายที่มียอดนำเข้า = ไม่เขียนทับ (ยอดที่ผู้ใช้ลงเอง · รายงานตรวจยอดยกมาแสดงเหตุผล)
        await RollIntoNextFiscalYearAsync(companyId, fiscalYear, retainedEarningsAccountId, startMonth, userId, failIfImported: false, legacyAnchorIds);

        // Auto-create the remaining monthly periods of the next fiscal year so
        // the user never has to add periods by hand each year.
        await EnsureFiscalYearPeriodsAsync(companyId, next.Start.Year);
        if (next.EndInclusive.Year != next.Start.Year)
            await EnsureFiscalYearPeriodsAsync(companyId, next.EndInclusive.Year);

        if (tx != null) await tx.CommitAsync();
        return closing;
    }

    /// <summary>action ของ audit เมื่อปิดปีผ่านงวดของวันที่ใบปิดปีที่ปิดชั่วคราว (เปิดแล้วล็อกในธุรกรรมเดียวกัน)</summary>
    public const string YearEndCloseThroughSoftClosedAction = "year-end-close-through-soft-closed-period";

    private sealed record ClosingPnlRow(Guid AccountId, string AccountCode, AccountType AccountType, decimal Debit, decimal Credit);

    /// <summary>ยอดสุทธิต่อบัญชีรายได้/ค่าใช้จ่ายที่ใบปิดปีต้องล้าง (ตัดบัญชีที่สุทธิศูนย์ · เรียงรหัส)</summary>
    private sealed record ClosingNet(Guid AccountId, string AccountCode, AccountType AccountType, decimal NetCredit, decimal NetDebit);

    private static List<ClosingNet> NetClosingRows(IEnumerable<ClosingPnlRow> rows)
        => rows
            .GroupBy(x => new { x.AccountId, x.AccountCode, x.AccountType })
            .Select(g => new ClosingNet(g.Key.AccountId, g.Key.AccountCode, g.Key.AccountType,
                g.Sum(x => x.Credit) - g.Sum(x => x.Debit),    // Revenue: positive
                g.Sum(x => x.Debit) - g.Sum(x => x.Credit)))   // Expense: positive
            .Where(x => x.NetCredit != 0 || x.NetDebit != 0)
            .OrderBy(x => x.AccountCode, StringComparer.Ordinal)
            .ToList();

    /// <summary>บรรทัดล้างยอดรายได้/ค่าใช้จ่ายต่อบัญชี (กลับด้านยอดคงเหลือ ⇒ บัญชีเป็นศูนย์หลังใบปิด) · <paramref name="describe"/>(ชนิด, รหัส)</summary>
    private static void AppendClosingLines(List<JournalLineRequest> lines, IEnumerable<ClosingNet> rows, Func<string, string, string> describe)
    {
        foreach (var acc in rows)
        {
            if (acc.AccountType == AccountType.Revenue && acc.NetCredit > 0)
                lines.Add(new JournalLineRequest(acc.AccountId, acc.NetCredit, 0, describe("รายได้", acc.AccountCode)));
            else if (acc.AccountType == AccountType.Revenue && acc.NetCredit < 0)
                lines.Add(new JournalLineRequest(acc.AccountId, 0, -acc.NetCredit, describe("รายได้", acc.AccountCode) + " (negative)"));
            else if (acc.AccountType == AccountType.Expense && acc.NetDebit > 0)
                lines.Add(new JournalLineRequest(acc.AccountId, 0, acc.NetDebit, describe("ค่าใช้จ่าย", acc.AccountCode)));
            else if (acc.AccountType == AccountType.Expense && acc.NetDebit < 0)
                lines.Add(new JournalLineRequest(acc.AccountId, -acc.NetDebit, 0, describe("ค่าใช้จ่าย", acc.AccountCode) + " (negative)"));
        }
    }

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

        // รอบ 4 P2-a: ด่านเดียวกับปิดปี — งวดยกยอดรุ่นเก่าไป ม.ค. ที่ยังไม่ซ่อม ⇒ ล้มดังพร้อมทางไปต่อ (ซ่อมก่อน)
        var legacyAnchorIds = await EnsureNoUnrepairedLegacyRollAsync(companyId, startMonth, year - 1);
        var written = await RollIntoNextFiscalYearAsync(companyId, year - 1, closing.RetainedEarningsAccountId, startMonth, userId, failIfImported: true,
            legacyAnchorIds);
        if (tx != null) await tx.CommitAsync();
        return written;
    }

    // ═══ ยกเลิกการปิดปี (คำสั่งเจ้าของ 2026-10-16 · DECISIONS ข้อ 152 · ตัวตัดสินเดียว Helpers/YearEndReopenRule) ═══
    // ทุกอย่างในธุรกรรมเดียวใต้ล็อกเดียวกับปิดปี/ยกยอด/ซ่อม (OpeningRoll.LockKey) · ด่านตัดสินก่อนเขียนอะไร ·
    // กลับใบปิดปีด้วยใบกลับรายการลงวันเดียวกัน (ติดธง IsClosingEntry ⇒ ClosingEntryLock คุมทั้งคู่ · งบกำไรขาดทุนคัดออกทั้งคู่ · งบทดลองนับทั้งคู่ = ศูนย์) ·
    // ยกเลิกแถวยกยอดที่การปิดปีเขียน (OpeningRoll.IsRolled — แถวนำเข้าไม่ถูกแตะ) · ทุกงวดของปีกลับเป็นเปิด · แถวปิดปี soft delete + ผู้/เวลา/เหตุผล · audit ทุกแถว

    public async Task<YearEndReopenResult> ReopenYearEndAsync(Guid companyId, int fiscalYear, string? reason, bool acknowledgeFiled, string userId)
    {
        await using var tx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", OpeningRoll.LockKey(companyId));

        // ── ข้อเท็จจริง (อ่านหลังได้ล็อก — สองคนกดพร้อมกัน คนที่สองเห็น "ยังไม่ได้ปิด") ──
        var closing = await _db.YearEndClosings
            .FirstOrDefaultAsync(y => y.CompanyId == companyId && y.FiscalYear == fiscalYear);
        var laterClosed = await _db.YearEndClosings.AsNoTracking()
            .Where(y => y.CompanyId == companyId && y.FiscalYear > fiscalYear)
            .Select(y => y.FiscalYear)
            .ToListAsync();
        var startMonth = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => c.FiscalYearStartMonth)
            .FirstOrDefaultAsync();
        // งวด ม.ค. รุ่นเก่าที่การปิดปีนี้เขียน (ซ่อมได้ที่ปี fiscalYear + 1 — ต้องมีปีนี้ปิดอยู่) — ตัวตรวจเดียวกับรายงาน/ซ่อม/ด่านปิดปี
        var legacyRows = closing == null ? 0
            : (await FindLegacyCalendarRollPeriodsAsync(companyId, startMonth))
                .Where(l => OpeningRoll.LegacyRepairYear(l.Start, startMonth) - 1 == fiscalYear)
                .Sum(l => l.RowIds.Count);
        var filingTypes = YearEndReopenRule.FilingTypes.ToList();
        var filed = (await _db.Set<ComplianceFiling>().AsNoTracking()
                .Where(f => f.CompanyId == companyId && !f.IsDeleted && f.Year == fiscalYear && filingTypes.Contains(f.FilingType))
                .OrderBy(f => f.FilingType).ThenBy(f => f.FormCode)
                .ToListAsync())
            .Where(f => YearEndReopenRule.IsFiledReturn(f.FilingType, f.Status))
            .Select(f => new YearEndFiledReturn(f.Id, f.FilingType, f.FormCode, f.Year, f.Month, f.Status, f.FiledDate,
                f.SubmissionReference, f.ConfirmationNumber))
            .ToList();

        var decision = YearEndReopenRule.Evaluate(new YearEndReopenFacts(
            fiscalYear, closing != null, laterClosed, legacyRows, filed, reason, acknowledgeFiled));
        if (!decision.Allowed) throw new YearEndReopenException(decision);
        var reasonText = YearEndReopenRule.NormalizeReason(reason)!;
        Guid? actorId = Guid.TryParse(userId, out var uid) ? uid : null;
        var now = DateTime.UtcNow;
        var filedRefs = filed.Select(f => new
        {
            filingId = f.FilingId, form = YearEndReopenRule.FilingLabel(f), f.FilingType, f.FormCode, f.Status, f.FiledDate,
            reference = YearEndReopenRule.FilingRef(f),
        }).ToList();

        // ── ใบปิดปี: ต้องยังเป็นใบ Posted ที่ไม่ถูกกลับ (บรรทัดทั้งหมด — ไม่ ThenInclude ผัง: INNER JOIN ทิ้งบรรทัดผังที่ลบ) ──
        var original = await _db.JournalEntries
            .Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.Id == closing!.ClosingJournalEntryId && j.CompanyId == companyId)
            ?? throw new BusinessRuleException(
                $"ไม่พบใบปิดบัญชีสิ้นปี {fiscalYear + 543} ที่การปิดปีอ้างถึง — ข้อมูลไม่ครบ ยกเลิกอัตโนมัติไม่ได้ · ยังไม่มีอะไรถูกบันทึก · "
                + "ทางไปต่อ: ติดต่อผู้ดูแลระบบพร้อมปีบัญชีนี้", YearEndReopenRule.RuleCode);
        if (original.Status != JournalEntryStatus.Posted || original.ReversedByEntryId.HasValue)
            throw new BusinessRuleException(
                $"ใบปิดบัญชีสิ้นปี {original.EntryNumber} ไม่อยู่ในสถานะบันทึกแล้ว (สถานะ {original.Status}) — ยกเลิกการปิดปีอัตโนมัติไม่ได้ · ยังไม่มีอะไรถูกบันทึก · "
                + "ทางไปต่อ: ติดต่อผู้ดูแลระบบพร้อมเลขที่ใบนี้", YearEndReopenRule.RuleCode);
        var persisted = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntryId == original.Id)
            .GroupBy(l => 1)
            .Select(g => new { Count = g.Count(), Debit = g.Sum(l => l.DebitAmount), Credit = g.Sum(l => l.CreditAmount) })
            .FirstOrDefaultAsync();
        var mismatch = JournalLineAccounts.ReversalMismatch(original.EntryNumber,
            original.Lines.Count, original.Lines.Sum(l => l.DebitAmount), original.Lines.Sum(l => l.CreditAmount),
            persisted?.Count ?? 0, persisted?.Debit ?? 0m, persisted?.Credit ?? 0m);
        if (mismatch != null) throw new BusinessRuleException(mismatch, JournalLineAccounts.ReversalRuleCode);

        // ── 1) ทุกงวดของปี → เปิด (ก่อนสร้างใบกลับ — ตัวสร้างใบปฏิเสธงวดที่ไม่เปิด) ── ชุดงวดเดียวกับที่ปิดปีล็อก (ปี×12+เดือน ในช่วงรอบ)
        var fy = Accounting.Helpers.FiscalYear.RangeFor(fiscalYear, startMonth);
        var firstKey = fy.Start.Year * 12 + fy.Start.Month;
        var lastMonth = fy.EndExclusive.AddMonths(-1);
        var lastKey = lastMonth.Year * 12 + lastMonth.Month;
        var periods = await _db.FiscalPeriods
            .Where(p => p.CompanyId == companyId && p.Year * 12 + p.Month >= firstKey && p.Year * 12 + p.Month <= lastKey)
            .OrderBy(p => p.Year).ThenBy(p => p.Month)
            .ToListAsync();
        foreach (var p in periods)
        {
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = actorId,
                EntityType = nameof(FiscalPeriod),
                EntityId = p.Id.ToString(),
                Action = AuditAction.Update,
                OldValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    period = p.Name, status = p.Status.ToString(), p.ClosedAt, p.ClosedBy, p.LockedAt, p.LockedBy, p.YearEndJournalEntryId,
                }),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = YearEndReopenRule.AuditAction, fiscalYear, period = p.Name, status = FiscalPeriodStatus.Open.ToString(),
                    reason = reasonText, ruleCode = YearEndReopenRule.RuleCode, by = userId,
                }),
                Timestamp = now,
            });
            p.Status = FiscalPeriodStatus.Open;
            p.ClosedAt = null;
            p.ClosedBy = null;
            p.LockedAt = null;
            p.LockedBy = null;
            p.YearEndJournalEntryId = null;
            p.UpdatedBy = userId;
            p.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();

        // ── 2) ใบกลับรายการใบปิดปี — ทางเฉพาะผ่านตัวสร้างใบ (ผังที่ลบในใบเดิมยอมเฉพาะชุดนี้ ของบริษัทนี้) · บรรทัดกระจกครบ · วันเดียวกับใบเดิม ──
        var lineAccountIds = original.Lines.Select(l => l.AccountId).Distinct().ToList();
        var deletedClosingAccounts = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.IsDeleted && lineAccountIds.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync();
        var reversalRequest = new CreateJournalEntryRequest(
            EntryDate: original.EntryDate,
            Description: $"ยกเลิกการปิดปี {fiscalYear} — กลับรายการ {original.EntryNumber} (เหตุผลอยู่ในประวัติการแก้ไข)",
            Reference: $"YE-{fiscalYear}-REOPEN",
            Lines: original.Lines.OrderBy(l => l.LineOrder)
                .Select(l => new JournalLineRequest(l.AccountId, l.CreditAmount, l.DebitAmount,
                    $"กลับรายการปิดปี {fiscalYear}: {l.Description}", l.ProjectId, l.BranchId, l.DimensionId, l.Tags))
                .ToList(),
            JournalType: original.JournalType);
        var reversalId = await CreateJournalEntryCoreAsync(companyId, reversalRequest, userId, deletedClosingAccounts);
        var reversal = await _db.JournalEntries.FirstAsync(j => j.Id == reversalId && j.CompanyId == companyId);
        reversal.IsAutoGenerated = true;
        reversal.IsClosingEntry = true;      // งบกำไรขาดทุนคัดออก (คู่กับใบเดิม) · ClosingEntryLock คุม
        reversal.Status = JournalEntryStatus.Posted;
        reversal.OriginalEntryId = original.Id;
        original.Status = JournalEntryStatus.Reversed;
        original.ReversedByEntryId = reversalId;
        original.UpdatedBy = userId;
        original.UpdatedAt = now;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorId,
            EntityType = nameof(JournalEntry),
            EntityId = original.Id.ToString(),
            Action = AuditAction.Update,
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { entryNumber = original.EntryNumber, status = JournalEntryStatus.Posted.ToString() }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = YearEndReopenRule.AuditAction, fiscalYear, entryNumber = original.EntryNumber, status = JournalEntryStatus.Reversed.ToString(),
                reversedBy = reversalId, reversalEntryNumber = reversal.EntryNumber, reason = reasonText, ruleCode = YearEndReopenRule.RuleCode, by = userId,
            }),
            Timestamp = now,
        });
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorId,
            EntityType = nameof(JournalEntry),
            EntityId = reversalId.ToString(),
            Action = AuditAction.Create,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = YearEndReopenRule.AuditAction, fiscalYear, entryNumber = reversal.EntryNumber, entryDate = reversal.EntryDate,
                reverses = original.EntryNumber, totalDebit = reversal.TotalDebit, totalCredit = reversal.TotalCredit, isClosingEntry = true,
                reason = reasonText, ruleCode = YearEndReopenRule.RuleCode, by = userId,
            }),
            Timestamp = now,
        });

        // ── 3) ยอดยกมาที่การปิดปีเขียนในงวดแรกของรอบถัดไป — soft delete เฉพาะแถวยกยอด (OpeningRoll.IsRolled) · แถวนำเข้าไม่ถูกแตะ ──
        var next = Accounting.Helpers.FiscalYear.RangeFor(fiscalYear + 1, startMonth);
        var nextFirst = await _db.FiscalPeriods.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.Year == next.Start.Year && p.Month == next.Start.Month)
            .Select(p => new { p.Id, p.Name })
            .FirstOrDefaultAsync();
        var removed = 0;
        if (nextFirst != null)
        {
            var nextRows = await _db.OpeningBalances
                .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == nextFirst.Id)
                .ToListAsync();
            foreach (var row in nextRows.Where(o => OpeningRoll.IsRolled(o.Notes)))
            {
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = actorId,
                    EntityType = nameof(OpeningBalance),
                    EntityId = row.Id.ToString(),
                    Action = AuditAction.Delete,
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        period = nextFirst.Name, accountId = row.AccountId, debit = row.OpeningDebit, credit = row.OpeningCredit, notes = row.Notes,
                    }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = YearEndReopenRule.AuditAction, fiscalYear, isDeleted = true, reason = reasonText,
                        ruleCode = YearEndReopenRule.RuleCode, by = userId,
                    }),
                    Timestamp = now,
                });
                row.IsDeleted = true;
                row.UpdatedBy = userId;
                row.UpdatedAt = now;
                removed++;
            }
        }

        // ── 4) แถวปิดปี: soft delete + ผู้/เวลา/เหตุผล/ใบกลับ (คงไว้เป็นหลักฐาน · index partial ⇒ ปิดปีซ้ำได้) · audit รวมเลขอ้างอิงแบบที่ยื่นแล้ว ──
        closing!.IsDeleted = true;
        closing.ReopenedAt = now;
        closing.ReopenedBy = userId;
        closing.ReopenReason = reasonText;
        closing.ReopenJournalEntryId = reversalId;
        closing.UpdatedBy = userId;
        closing.UpdatedAt = now;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorId,
            EntityType = nameof(YearEndClosing),
            EntityId = closing.Id.ToString(),
            Action = AuditAction.Delete,
            OldValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                fiscalYear, closing.TransferredAmount, closing.ClosingJournalEntryId, closedAt = closing.CreatedAt, closedBy = closing.CreatedBy,
            }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = YearEndReopenRule.AuditAction, fiscalYear, reason = reasonText, reopenJournalEntryId = reversalId,
                reversalEntryNumber = reversal.EntryNumber, periodsReopened = periods.Count, rolledRowsRemoved = removed,
                acknowledgeFiled, filedReturns = filedRefs,
                ruleCode = YearEndReopenRule.RuleCode, legalReference = YearEndReopenRule.LegalReference, by = userId,
            }),
            Timestamp = now,
        });

        await _db.SaveChangesAsync();
        if (tx != null) await tx.CommitAsync();
        return new YearEndReopenResult(fiscalYear, original.Id, reversalId, reversal.EntryNumber, periods.Count, removed, filed);
    }

    public async Task<List<YearEndClosingSummary>> GetYearEndClosingsAsync(Guid companyId)
    {
        var rows = await (
            from y in _db.YearEndClosings.AsNoTracking()
            join j in _db.JournalEntries.IgnoreQueryFilters().AsNoTracking() on y.ClosingJournalEntryId equals j.Id into jj
            from j in jj.DefaultIfEmpty()
            where y.CompanyId == companyId
            orderby y.FiscalYear descending
            select new { y.Id, y.FiscalYear, y.ClosingJournalEntryId, EntryNumber = j != null && j.CompanyId == companyId ? j.EntryNumber : null,
                y.TransferredAmount, y.CreatedAt, y.CreatedBy }
        ).ToListAsync();
        var years = rows.Select(r => r.FiscalYear).ToList();
        return rows.Select(r =>
        {
            var later = years.Where(y => y > r.FiscalYear).ToList();
            return new YearEndClosingSummary(r.Id, r.FiscalYear, r.ClosingJournalEntryId, r.EntryNumber, r.TransferredAmount, r.CreatedAt, r.CreatedBy,
                later.Count == 0, later.Count == 0 ? null : YearEndReopenRule.LaterYearClosedMessage(r.FiscalYear, later),
                YearEndReopenRule.Effects(r.FiscalYear));
        }).ToList();
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
        int startMonth, string userId, bool failIfImported, IReadOnlyCollection<Guid> legacyAnchorIds)
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
        var rolled = await ComputeRolledOpeningsAsync(companyId, next.Start, retainedEarningsAccountId, null, legacyAnchorIds);

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
    /// ใช้ร่วมกันโดยใบปิดปีและการยกยอด/รายงานตรวจ · <paramref name="ignoredPeriodIds"/> = งวดยกยอดรุ่นเก่าที่รายงานตรวจ/ซ่อมถือว่าไม่ใช่ฐาน
    /// (<see cref="OpeningRoll.IsLegacyCalendarRollPeriod"/> — ซ่อมแล้วแถวถูกยกเลิก งบทดลองก็ไม่ใช้เป็นฐานเช่นกัน)</summary>
    private async Task<OpeningAnchor?> LatestOpeningAnchorAsync(Guid companyId, DateTime asOfExclusive, IReadOnlyCollection<Guid>? ignoredPeriodIds = null)
    {
        var ignored = ignoredPeriodIds?.ToList() ?? new List<Guid>();
        return await (
            from p in _db.FiscalPeriods.AsNoTracking()
            join o in _db.OpeningBalances.AsNoTracking() on p.Id equals o.FiscalPeriodId
            where p.CompanyId == companyId && o.CompanyId == companyId && p.StartDate < asOfExclusive
                && !ignored.Contains(p.Id)
            orderby p.StartDate descending
            select new OpeningAnchor(p.Id, p.StartDate)
        ).FirstOrDefaultAsync();
    }

    /// <summary>
    /// ยอดยกมา ณ <paramref name="asOfExclusive"/> (ต้นรอบบัญชีถัดไป) ที่ควรเป็น (นิยามใน <see cref="OpeningRoll"/>) — ตัวคำนวณตัวเดียวของการปิดปี/ยกยอด
    /// และรายงานตรวจ/ซ่อม · = งบทดลอง ณ วันก่อนหน้า (<see cref="LoadRollTrialBalanceAsync"/>) แล้ว <see cref="OpeningRoll.CarryForward"/> ·
    /// <paramref name="correctedAnchors"/> = ยอดที่ "ควรเป็น" ของงวดฐานที่รายงานคำนวณไว้แล้วในรอบเดียวกัน
    /// (ปีที่สองขึ้นไปของสายที่ผิด ต้องคิดบนฐานที่แก้แล้ว ไม่ใช่ฐานที่เก็บผิด)
    /// </summary>
    private async Task<Dictionary<Guid, (decimal Debit, decimal Credit)>> ComputeRolledOpeningsAsync(
        Guid companyId, DateTime asOfExclusive, Guid? retainedEarningsAccountId,
        IReadOnlyDictionary<Guid, Dictionary<Guid, (decimal Debit, decimal Credit)>>? correctedAnchors,
        IReadOnlyCollection<Guid>? ignoredAnchorPeriodIds = null)
    {
        var tb = await LoadRollTrialBalanceAsync(companyId, asOfExclusive, correctedAnchors, ignoredAnchorPeriodIds);
        return OpeningRoll.CarryForward(tb.TrialBalance, tb.Types, retainedEarningsAccountId, tb.DeletedIds);
    }

    private sealed record RollTrialBalance(
        Dictionary<Guid, (decimal Debit, decimal Credit)> TrialBalance, Dictionary<Guid, AccountType> Types, HashSet<Guid> DeletedIds);

    /// <summary>
    /// งบทดลอง ณ วันก่อน <paramref name="asOfExclusive"/> สำหรับยกยอด (ฐานล่าสุดก่อนวันนั้น + ทุกรายการตั้งแต่วันเริ่มฐาน) + ประเภทผัง (รวมผังที่ถูกลบ) ·
    /// <b>ผังสินทรัพย์/หนี้สิน/ทุนที่ถูกลบแต่ยังมียอด ⇒ ล้มดังพร้อมชื่อผัง + ทางไปต่อ</b> (<see cref="OpeningRoll.UnknownAccountsWithBalance"/> ·
    /// รอบ 3 P2-6 · รอบ 4 P2-b: ผังรายได้/ค่าใช้จ่ายที่ถูกลบรู้ประเภทแล้ว ⇒ รวมเข้ากำไรสะสมได้ ไม่ล้ม) · ใบปิดปีเรียกตัวนี้<b>ก่อน</b>สร้างใบ
    /// </summary>
    private async Task<RollTrialBalance> LoadRollTrialBalanceAsync(
        Guid companyId, DateTime asOfExclusive,
        IReadOnlyDictionary<Guid, Dictionary<Guid, (decimal Debit, decimal Credit)>>? correctedAnchors,
        IReadOnlyCollection<Guid>? ignoredAnchorPeriodIds)
    {
        // ฐาน = งวดล่าสุดที่มียอดยกมาและเริ่มก่อนต้นรอบเป้าหมาย — เกณฑ์เดียวกับงบทดลอง ณ วันสิ้นรอบ
        var anchor = await LatestOpeningAnchorAsync(companyId, asOfExclusive, ignoredAnchorPeriodIds);

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
        // รอบ 3 P2-6: ไม่ join ผังบัญชี (ตัวกรอง soft-delete ของผังเคยตัดบรรทัดของผังที่ถูกลบทิ้งเงียบ ๆ ⇒ ยอดยกมาไม่ลงตัว) — ผังที่ถูกลบแต่มียอด = ล้มดังข้างล่าง
        var movements = await (
            from line in _db.JournalEntryLines.AsNoTracking()
            join entry in _db.JournalEntries on line.JournalEntryId equals entry.Id
            where entry.CompanyId == companyId
                && entry.EntryDate < asOfExclusive
                && (movementStart == null || entry.EntryDate >= movementStart.Value)
                && (entry.Status == JournalEntryStatus.Posted || entry.Status == JournalEntryStatus.Reversed)
            group new { line.DebitAmount, line.CreditAmount } by line.AccountId into g
            select new { AccountId = g.Key, Debit = g.Sum(x => x.DebitAmount), Credit = g.Sum(x => x.CreditAmount) }
        ).ToDictionaryAsync(x => x.AccountId, x => (x.Debit, x.Credit));

        var trialBalance = OpeningRoll.Combine(anchorRows, movements);
        var ids = trialBalance.Keys.ToList();
        // รอบ 4 P2-b: ประเภทของผังที่ถูกลบด้วย (ผังรายได้/ค่าใช้จ่ายที่ลบ = รวมเข้ากำไรสะสม · ผังสินทรัพย์/หนี้สิน/ทุนที่ลบแต่มียอด = ล้มดัง)
        var charts = ids.Count == 0
            ? new List<RollChartRow>()
            : await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.CompanyId == companyId && ids.Contains(a.Id))
                .Select(a => new RollChartRow(a.Id, a.AccountType, a.IsDeleted, a.AccountCode, a.AccountName))
                .ToListAsync();
        var types = charts.ToDictionary(a => a.Id, a => a.AccountType);
        var deletedIds = charts.Where(a => a.IsDeleted).Select(a => a.Id).ToHashSet();
        var deletedWithBalance = OpeningRoll.UnknownAccountsWithBalance(trialBalance, types, deletedIds);
        if (deletedWithBalance.Count > 0)
        {
            var byId = charts.ToDictionary(a => a.Id);
            var accounts = deletedWithBalance
                .Select(id => byId.TryGetValue(id, out var c)
                    ? new OpeningRollDeletedAccount(id, c.AccountCode, c.AccountName, trialBalance[id].Debit - trialBalance[id].Credit)
                    : new OpeningRollDeletedAccount(id, "", id.ToString(), trialBalance[id].Debit - trialBalance[id].Credit))
                .OrderBy(a => a.AccountCode, StringComparer.Ordinal)
                .ToList();
            throw new DeletedAccountsWithBalanceException(
                OpeningRoll.DeletedWithBalanceMessage(asOfExclusive, accounts.Select(a => (a.AccountCode, a.AccountName, a.NetDebit))),
                accounts);
        }
        return new RollTrialBalance(trialBalance, types, deletedIds);
    }

    private sealed record RollChartRow(Guid Id, AccountType AccountType, bool IsDeleted, string AccountCode, string AccountName);

    /// <summary>งวดที่เริ่มเดือน/ปีของ <paramref name="start"/> มีแถวยอดยกมาที่นำเข้า (<see cref="OpeningRoll.IsImported"/>) — การยกยอดไม่เขียนทับงวดนี้</summary>
    private async Task<bool> PeriodStartingHasImportedRowsAsync(Guid companyId, DateTime start)
    {
        var notes = await (
            from o in _db.OpeningBalances.AsNoTracking()
            join p in _db.FiscalPeriods.AsNoTracking() on o.FiscalPeriodId equals p.Id
            where o.CompanyId == companyId && p.CompanyId == companyId && p.Year == start.Year && p.Month == start.Month
            select o.Notes
        ).ToListAsync();
        return notes.Any(OpeningRoll.IsImported);
    }

    /// <summary>งวดยกยอดรุ่นเก่าไป ม.ค. (หนึ่งงวด) — <c>RowIds</c> = แถวที่การซ่อมยกเลิก</summary>
    private sealed record LegacyRollPeriod(Guid PeriodId, DateTime Start, List<Guid> RowIds);

    /// <summary>
    /// งวดยกยอดรุ่นเก่าไป ม.ค. ของบริษัทที่รอบไม่ใช่ปีปฏิทิน (การปิดปีก่อน de823512 ยกไป 1 ม.ค. เสมอ · <see cref="OpeningRoll.IsLegacyCalendarRollPeriod"/>) —
    /// <b>ตัวตรวจตัวเดียว</b> ของรายงาน/ซ่อม (<see cref="EvaluateOpeningRollChainAsync"/>) และด่านปิดปี/ยกยอดด้วยมือ (<see cref="EnsureNoUnrepairedLegacyRollAsync"/>) ·
    /// แถวที่ซ่อมแล้ว (soft delete) ไม่ถูกนับ
    /// </summary>
    private async Task<List<LegacyRollPeriod>> FindLegacyCalendarRollPeriodsAsync(Guid companyId, int startMonth)
    {
        var legacy = new List<LegacyRollPeriod>();
        if (Accounting.Helpers.FiscalYear.NormalizeStartMonth(startMonth) == 1) return legacy;
        var janRows = await (
            from p in _db.FiscalPeriods.AsNoTracking()
            join o in _db.OpeningBalances.AsNoTracking() on p.Id equals o.FiscalPeriodId
            where p.CompanyId == companyId && o.CompanyId == companyId && p.Month == 1
            select new { PeriodId = p.Id, p.StartDate, p.Month, RowId = o.Id, o.Notes, o.CreatedAt }
        ).ToListAsync();
        if (janRows.Count == 0) return legacy;

        // ═══ รอบ 5 P2-2: ยกตอนที่บริษัทใช้รอบปีปฏิทินอยู่ ≠ ของรุ่นเก่า ═══
        // บริษัทปีปฏิทินที่ปิดปีแล้วค่อยเปลี่ยนเดือนเริ่มรอบ (ก่อนรอบ 5 เปลี่ยนได้เสมอ) — แถว ม.ค. ของปีนั้นถูกต้อง · เดิมถูกรายงาน/บล็อกว่ารุ่นเก่า และปุ่มซ่อม
        // จะยกเลิกยอดยกมาที่ถูก (เขียนประวัติใหม่) · หลักฐาน: เวลาที่ยก (ใบปิดปีของรอบที่เขียนงวดนั้น · ไม่มีใบปิดปี = เวลาที่แถวถูกเขียน) + audit การเปลี่ยนเดือนเริ่มรอบ
        var closedAt = (await _db.YearEndClosings.AsNoTracking()
                .Where(y => y.CompanyId == companyId)
                .Select(y => new { y.FiscalYear, y.CreatedAt })
                .ToListAsync())
            .GroupBy(y => y.FiscalYear)
            .ToDictionary(g => g.Key, g => g.Min(x => x.CreatedAt));
        var changes = await LoadFiscalYearStartMonthChangesAsync(companyId);

        foreach (var g in janRows.GroupBy(r => new { r.PeriodId, r.StartDate, r.Month }))
        {
            if (!OpeningRoll.IsLegacyCalendarRollPeriod(g.Key.Month, startMonth, g.Select(r => r.Notes))) continue;
            // ใบปิดปีที่การยกยอดรุ่นเก่าเขียนงวด ม.ค. นี้ = รอบก่อนปีที่ซ่อม (ตัวตั้งเดียวกับด่าน: OpeningRoll.LegacyRepairYear)
            var rolledAt = closedAt.TryGetValue(OpeningRoll.LegacyRepairYear(g.Key.StartDate, startMonth) - 1, out var at) ? at : g.Min(r => r.CreatedAt);
            if (!OpeningRoll.IsLegacyCalendarRoll(rolledAt, startMonth, changes)) continue;
            legacy.Add(new LegacyRollPeriod(g.Key.PeriodId, g.Key.StartDate, g.Select(r => r.RowId).ToList()));
        }
        return legacy.OrderBy(l => l.Start).ToList();
    }

    /// <summary>ประวัติการเปลี่ยนเดือนเริ่มรอบบัญชีของบริษัท (audit <see cref="OpeningRoll.FiscalYearStartMonthChangeAction"/> — มีตั้งแต่รอบ 5) ·
    /// แถวที่อ่านค่าเดิมไม่ได้ถูกข้าม (ไม่เดา)</summary>
    private async Task<List<(DateTime ChangedAtUtc, int OldMonth)>> LoadFiscalYearStartMonthChangesAsync(Guid companyId)
    {
        var marker = "\"action\":\"" + OpeningRoll.FiscalYearStartMonthChangeAction + "\"";
        var rows = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.EntityType == "Company" && a.NewValues != null && a.NewValues.Contains(marker))
            .Select(a => new { a.Timestamp, a.OldValues })
            .ToListAsync();
        var result = new List<(DateTime, int)>();
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.OldValues)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(r.OldValues);
                if (doc.RootElement.TryGetProperty("fiscalYearStartMonth", out var m) && m.TryGetInt32(out var month))
                    result.Add((r.Timestamp, month));
            }
            catch (System.Text.Json.JsonException)
            {
                // แถว audit ที่ไม่ใช่ JSON — ไม่ใช่หลักฐาน (ไม่เดาค่าเดิม)
            }
        }
        return result;
    }

    /// <summary>
    /// ด่านปิดปี/ยกยอดด้วยมือ (รอบ 4 P2-a): ปิดรอบ <paramref name="closingFiscalYear"/> (ยกยอดไปต้นรอบถัดไป) ขณะยังมีงวดยกยอดรุ่นเก่าไป ม.ค. ที่<b>ซ่อมได้แล้ว</b>
    /// (อยู่ในรอบที่ปิดแล้ว — <see cref="OpeningRoll.LegacyRepairYear"/>) ก่อนต้นรอบถัดไป ⇒ ล้มดังพร้อมทางไปต่อ (<see cref="OpeningRoll.LegacyRollBlockMessage"/>) ·
    /// เดิมปิดปี/ยกยอดใช้งวดนั้นเป็นฐาน (รายงาน/ซ่อมไม่ใช้) ⇒ บริษัทรอบ เม.ย.–มี.ค. ที่ปิดปีด้วยโค้ดรุ่นเก่าแล้วปิดปีถัดไปโดยยังไม่ซ่อม ได้ยอดยกมาคลาด
    /// เท่ากำไรขาดทุน เม.ย.–ธ.ค. ของปีก่อน · คืนงวดรุ่นเก่า<b>ทั้งหมด</b> ให้ผู้เรียกส่งเป็นงวดที่ไม่ใช้เป็นฐาน (งวดที่ยังไม่อยู่ในรอบที่ปิด = ซ่อมได้หลังปิดรอบนั้น
    /// · เกณฑ์เดียวกับยอดที่ควรเป็นของรายงาน)
    /// </summary>
    private async Task<List<Guid>> EnsureNoUnrepairedLegacyRollAsync(Guid companyId, int startMonth, int closingFiscalYear)
    {
        var legacy = await FindLegacyCalendarRollPeriodsAsync(companyId, startMonth);
        if (legacy.Count == 0) return new List<Guid>();
        var nextStart = Accounting.Helpers.FiscalYear.RangeFor(closingFiscalYear + 1, startMonth).Start;
        var closedYears = (await _db.YearEndClosings.AsNoTracking()
                .Where(y => y.CompanyId == companyId)
                .Select(y => y.FiscalYear)
                .ToListAsync())
            .ToHashSet();
        var blocking = legacy
            .Where(l => l.Start < nextStart && closedYears.Contains(OpeningRoll.LegacyRepairYear(l.Start, startMonth) - 1))
            .ToList();
        if (blocking.Count > 0)
            throw new BusinessRuleException(OpeningRoll.LegacyRollBlockMessage(
                blocking.Select(l => (l.Start, OpeningRoll.LegacyRepairYear(l.Start, startMonth), l.RowIds.Count))), OpeningRoll.RuleCode);
        return legacy.Select(l => l.PeriodId).ToList();
    }

    // ═══ ตรวจ/ซ่อมยอดยกมาจากการปิดปี — เครื่องมือเจ้าของบริษัท (ฝ่ายค้านรอบส่งออก 2026-10-15 P1-1 · F2 ข้อ 9) ═══
    // รายงานก่อน (อ่านอย่างเดียว) → ซ่อมเฉพาะปีที่ผู้ใช้เลือกเมื่อกด + audit chain ทุกบัญชี · ไม่มีงานไหนซ่อมเองตอนเปิดระบบ
    // (แบบเดียวกับ FindProductTotalMismatchesAsync → RepairProductTotalsAsync)

    public async Task<List<OpeningRollCheckYear>> FindOpeningRollMismatchesAsync(Guid companyId)
        => (await EvaluateOpeningRollChainAsync(companyId)).Select(x => x.Report).ToList();

    /// <summary>ผลตรวจหนึ่งปี + สิ่งที่การซ่อมต้องใช้ · <c>LegacyRowIds</c> = แถวยกยอดรุ่นเก่าไป ม.ค. (รอบไม่ใช่ปีปฏิทิน) ที่ซ่อมแล้วต้องยกเลิก</summary>
    private sealed record OpeningRollEvaluation(
        OpeningRollCheckYear Report, Guid? PeriodId, Dictionary<Guid, (decimal Debit, decimal Credit)>? Expected,
        DateTime TargetStart, IReadOnlyList<Guid> LegacyRowIds);

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

        // ═══ รอบ 3 P2-4: งวดยกยอดรุ่นเก่าไป ม.ค. ของบริษัทที่รอบไม่ใช่ปีปฏิทิน ═══ (การปิดปีก่อน de823512 ยกไป 1 ม.ค. เสมอ)
        // ไม่ใช้เป็นฐานของยอดที่ควรเป็น (ยอดในงวดนั้นคิดแบบปีปฏิทิน) · ซ่อมปีที่งวดนั้นอยู่ในรอบ ⇒ ยกเลิกแถว (soft delete + audit) ⇒ งบทดลองไม่อ่านอีก
        var legacy = await FindLegacyCalendarRollPeriodsAsync(companyId, startMonth);
        var legacyPeriodIds = legacy.Select(l => l.PeriodId).ToList();

        var corrected = new Dictionary<Guid, Dictionary<Guid, (decimal Debit, decimal Credit)>>();
        var result = new List<OpeningRollEvaluation>();
        foreach (var closed in closings)
        {
            var year = closed.FiscalYear + 1;
            // งวดเป้าหมาย = งวดแรกของรอบบัญชีถัดไป (รอบ 2 P2-6 — รอบ เม.ย.–มี.ค. = เม.ย. ไม่ใช่ ม.ค.)
            var nextStart = Accounting.Helpers.FiscalYear.RangeFor(year, startMonth).Start;
            // งวด ม.ค. รุ่นเก่าที่อยู่ในรอบที่ปิด — ซ่อมปีนี้แล้วยกเลิก (ตัวตั้งเดียวกับด่านปิดปี: OpeningRoll.LegacyRepairYear)
            var legacyHere = legacy.Where(l => OpeningRoll.LegacyRepairYear(l.Start, startMonth) == year).ToList();
            var legacyRowIds = legacyHere.SelectMany(l => l.RowIds).ToList();
            string? legacyNote = legacyHere.Count == 0 ? null
                : $"พบยอดยกมารุ่นเก่าที่การปิดปี {closed.FiscalYear} เขียนไว้งวด ม.ค. {legacyHere[0].Start.Year + 543} ({legacyRowIds.Count} แถว) ทั้งที่รอบบัญชีเริ่มเดือน "
                    + $"{Accounting.Helpers.FiscalYear.NormalizeStartMonth(startMonth)} — งบทดลอง ม.ค.–ก่อนต้นรอบอ่านยอดนี้ (คิดแบบปีปฏิทิน) · ยอดที่ควรเป็นของงวดแรกของรอบ "
                    + $"({ThaiDate.ToThaiDisplayString(nextStart)}) คิดโดยไม่ใช้งวด ม.ค. นั้นเป็นฐาน · กด \"แก้\" ปีนี้ ⇒ เขียนยอดยกมางวดแรกของรอบ + ยกเลิกแถวยกยอดรุ่นเก่าของงวด ม.ค. "
                    + "(เฉพาะแถวที่การปิดปีเขียน · แถวนำเข้าไม่ถูกแตะ · บันทึกประวัติทุกแถว)";
            var janId = await _db.FiscalPeriods.AsNoTracking()
                .Where(p => p.CompanyId == companyId && p.Year == nextStart.Year && p.Month == nextStart.Month)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync();
            if (janId == null && legacyRowIds.Count == 0)
            {
                result.Add(new(new OpeningRollCheckYear(year, null, false, "ไม่มีงวดแรกของรอบบัญชีนี้ (ยังไม่เคยยกยอด)", new()), null, null,
                    nextStart, Array.Empty<Guid>()));
                continue;
            }
            var storedRows = janId == null
                ? new()
                : await _db.OpeningBalances.AsNoTracking()
                    .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == janId.Value)
                    .Select(o => new { o.AccountId, o.OpeningDebit, o.OpeningCredit, o.Notes })
                    .ToListAsync();
            if (storedRows.Any(o => OpeningRoll.IsImported(o.Notes)))
            {
                // ผู้ใช้ลงยอดยกมาจากระบบเดิมในงวดนี้เอง — ยอดที่เก็บคือความตั้งใจของผู้ใช้ (ปีถัดไปใช้ยอดที่เก็บเป็นฐาน) · ไม่ซ่อมยอด
                // รอบ 4 P2-d: แต่แถวยกยอดรุ่นเก่าไป ม.ค. ยังต้องรายงาน + ยกเลิกได้ (เดิมคืน 0 แถว ⇒ หน้าตรวจข้อมูลข้าม ⇒ ซ่อนอยู่ และด่านปิดปีบล็อกโดยไม่มีทางซ่อม)
                // — การซ่อมยกเลิกเฉพาะแถวรุ่นเก่าของงวด ม.ค. · แถวนำเข้าของงวดนี้ไม่ถูกแตะ
                var importedNote = legacyHere.Count == 0 ? null
                    : $"พบยอดยกมารุ่นเก่าที่การปิดปี {closed.FiscalYear} เขียนไว้งวด ม.ค. {legacyHere[0].Start.Year + 543} ({legacyRowIds.Count} แถว) ทั้งที่รอบบัญชีเริ่มเดือน "
                        + $"{Accounting.Helpers.FiscalYear.NormalizeStartMonth(startMonth)} — งบทดลอง ม.ค.–ก่อนต้นรอบอ่านยอดนี้ (คิดแบบปีปฏิทิน) · งวดแรกของรอบ "
                        + $"({ThaiDate.ToThaiDisplayString(nextStart)}) มียอดยกมาที่นำเข้า ⇒ กด \"แก้\" ปีนี้ = ยกเลิกเฉพาะแถวยกยอดรุ่นเก่าของงวด ม.ค. "
                        + "(แถวนำเข้าไม่ถูกแตะ · ยอดงวดแรกของรอบไม่เปลี่ยน · บันทึกประวัติทุกแถว)";
                result.Add(new(new OpeningRollCheckYear(year, janId, true,
                    "งวดนี้มียอดยกมาที่นำเข้าจากระบบเดิม — ไม่ซ่อมยอดอัตโนมัติ (ตรวจกับงบทดลองแล้วแก้ที่หน้าย้ายระบบ)", new(), importedNote, legacyRowIds.Count),
                    janId, null, nextStart, legacyRowIds));
                continue;
            }

            Dictionary<Guid, (decimal Debit, decimal Credit)> expected;
            try
            {
                expected = await ComputeRolledOpeningsAsync(companyId, nextStart, closed.RetainedEarningsAccountId, corrected, legacyPeriodIds);
            }
            catch (BusinessRuleException ex)
            {
                // ผังที่ถูกลบแต่ยังมียอด / ไม่รู้ผังกำไรสะสม — รายงานบอกเหตุผล + ทางไปต่อ (ซ่อมปีนี้ไม่ได้จนกว่าจะแก้) · ไม่ข้ามเงียบ (F2 ข้อ 7) ·
                // รอบ 4 P2-b: ผังที่ถูกลบส่งต่อให้หน้าตรวจข้อมูลมีปุ่มกู้คืนรายผัง
                result.Add(new(new OpeningRollCheckYear(year, janId, false, ex.Message, new(), legacyNote, legacyRowIds.Count,
                        (ex as DeletedAccountsWithBalanceException)?.Accounts), janId, null,
                    nextStart, Array.Empty<Guid>()));
                continue;
            }
            if (janId != null) corrected[janId.Value] = expected;
            var stored = storedRows.GroupBy(o => o.AccountId)
                .ToDictionary(g => g.Key, g => (Debit: g.Sum(x => x.OpeningDebit), Credit: g.Sum(x => x.OpeningCredit)));
            var diff = OpeningRoll.Diff(stored, expected);

            var ids = diff.Select(d => d.AccountId).ToList();
            var accounts = new Dictionary<Guid, (string Code, string Name)>();
            if (ids.Count > 0)
                // รวมผังที่ถูกลบ (แถวที่เก็บไว้ของผังที่ลบแล้ว) — รายงานต้องบอกชื่อผัง ไม่ใช่ช่องว่าง (รอบ 3 P2-6)
                foreach (var acct in await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
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
            result.Add(new(new OpeningRollCheckYear(year, janId, false, null, mismatches, legacyNote, legacyRowIds.Count), janId, expected,
                nextStart, legacyRowIds));
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
            // รอบ 4 P2-d: งวดแรกของรอบมียอดนำเข้า (ไม่ซ่อมยอด) แต่มีแถวยกยอดรุ่นเก่าไป ม.ค. ⇒ ยกเลิกเฉพาะแถวรุ่นเก่า (แถวนำเข้าไม่ถูกแตะ)
            if (ev.Expected == null && !(ev.Report.HasImportedRows && ev.LegacyRowIds.Count > 0))
            {
                skipped.Add($"{ev.Report.Year}: {ev.Report.SkipReason}");
                continue;
            }
            if (ev.Report.Mismatches.Count == 0 && ev.LegacyRowIds.Count == 0)
            {
                skipped.Add($"{ev.Report.Year}: ยอดถูกอยู่แล้ว");
                continue;
            }
            // งวดเป้าหมายยังไม่มี (บริษัทรุ่นเก่าที่ยกยอดไป ม.ค.) ⇒ สร้างงวดแรกของรอบ
            var periodId = ev.PeriodId ?? (await FindOrCreatePeriodStartingAsync(companyId, ev.TargetStart, actor)).Id;
            // รอบ 3 P2-4: ยกเลิกแถวยกยอดรุ่นเก่าของงวด ม.ค. (เฉพาะแถวที่การปิดปีเขียน — ตัดสินแล้วใน EvaluateOpeningRollChainAsync) + audit ทุกแถว
            if (ev.LegacyRowIds.Count > 0)
            {
                var legacyIds = ev.LegacyRowIds.ToList();
                var legacyRows = await _db.OpeningBalances
                    .Include(o => o.FiscalPeriod)
                    .Where(o => o.CompanyId == companyId && legacyIds.Contains(o.Id))
                    .ToListAsync();
                foreach (var lr in legacyRows.Where(r => OpeningRoll.IsRolled(r.Notes)))
                {
                    _db.AddChainedAuditLog(new AuditLog
                    {
                        CompanyId = companyId,
                        UserId = actorId,
                        EntityType = nameof(OpeningBalance),
                        EntityId = lr.Id.ToString(),
                        Action = AuditAction.Delete,
                        OldValues = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            period = lr.FiscalPeriod.Name, accountId = lr.AccountId, debit = lr.OpeningDebit, credit = lr.OpeningCredit, notes = lr.Notes,
                        }),
                        NewValues = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            action = OpeningRoll.LegacyJanNeutralizedAction, year = ev.Report.Year, isDeleted = true,
                            ruleCode = OpeningRoll.RuleCode, by = actor,
                        }),
                        Timestamp = DateTime.UtcNow,
                    });
                    lr.IsDeleted = true;
                    lr.UpdatedBy = actor;
                    lr.UpdatedAt = DateTime.UtcNow;
                    repairedRows++;
                }
            }
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
