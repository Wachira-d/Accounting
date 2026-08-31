using Accounting.Data;
using Accounting.Models.DTOs.FinancialManagement;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class FinancialManagementService : IFinancialManagementService
{
    private readonly AccountingDbContext _db;
    public FinancialManagementService(AccountingDbContext db) => _db = db;

    /// <summary>เลข JV — ผ่านตัวออกเลขกลางตัวเดียวของระบบ
    ///
    /// <para>⚠️ เดิมเมธอดนี้มี logic + advisory lock ของตัวเอง โดยใช้คีย์
    /// <c>HashCode.Combine(companyId, "JV", ym)</c> ซึ่ง<b>คนละค่า</b>กับคีย์ของ
    /// <c>JournalEntryBuilder</c> ที่ <c>DocumentService</c> ใช้ ⇒ ทั้งสองลงเลข
    /// ใน number space เดียวกัน (<c>JV-yyyyMM-NNNN</c> + unique index
    /// <c>(CompanyId, EntryNumber)</c>) แต่<b>ไม่บล็อกกัน</b> ⇒ approve เอกสาร
    /// พร้อมกับ post JE ปันส่วน/ค่าเสื่อม = อ่าน max ได้เลขเดียวกัน → ชน unique
    /// → operation หนึ่งล้มด้วย DbUpdateException แบบสุ่ม</para></summary>
    private Task<string> NextJvNumberAsync(Guid companyId)
        => Journal.JournalEntryBuilder.NextJournalNumberAsync(_db, companyId, "JV", DateTime.UtcNow);

    private async Task<Guid?> GetFiscalPeriodIdAsync(Guid companyId, DateTime date)
    {
        var fp = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId && f.StartDate <= date && f.EndDate >= date && f.Status == FiscalPeriodStatus.Open);
        return fp?.Id;
    }

    private async Task<string> NextRefNoAsync(Guid companyId, string prefix)
    {
        var ym = DateTime.UtcNow.ToString("yyyyMM");
        var pat = $"{prefix}-{ym}-";
        // Race-safe: advisory lock + numeric MAX. CountAsync was wrong even
        // single-threaded — a voided ref leaves a gap that count+1 reuses
        // (= duplicate). Use the actual max suffix instead.
        var lockKey = HashCode.Combine(companyId, prefix, ym);
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", lockKey);
        var suffixes = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && j.Reference != null && j.Reference.StartsWith(pat))
            .OrderByDescending(j => j.CreatedAt).Take(2000)
            .Select(j => j.Reference!.Substring(pat.Length))
            .ToListAsync();
        int seq = 1;
        foreach (var s in suffixes)
            if (int.TryParse(s, out var n) && n >= seq) seq = n + 1;
        return seq <= 9999 ? $"{pat}{seq:D4}" : $"{pat}{seq:D5}";
    }

    // ==================== 1. PREPAID EXPENSES ====================

    public async Task<PrepaidExpenseResponse> CreatePrepaidExpenseAsync(Guid companyId, CreatePrepaidExpenseRequest request, string userId)
    {
        var refNo = $"PPD-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..4].ToUpper()}";
        var amountPerPeriod = Math.Round(request.TotalAmount / request.TotalPeriods, 2, MidpointRounding.AwayFromZero);

        var entity = new PrepaidExpense
        {
            CompanyId = companyId, ReferenceNo = refNo, Description = request.Description,
            StartDate = request.StartDate, EndDate = request.EndDate,
            TotalPeriods = request.TotalPeriods, TotalAmount = request.TotalAmount,
            AmortizedAmount = 0, RemainingAmount = request.TotalAmount,
            PrepaidAccountId = request.PrepaidAccountId, ExpenseAccountId = request.ExpenseAccountId,
            CreatedBy = userId
        };

        // Generate amortization schedule
        for (int i = 0; i < request.TotalPeriods; i++)
        {
            var schedDate = request.StartDate.AddMonths(i);
            var amt = (i == request.TotalPeriods - 1)
                ? request.TotalAmount - (amountPerPeriod * (request.TotalPeriods - 1)) // last period gets remainder
                : amountPerPeriod;

            entity.Schedules.Add(new PrepaidAmortizationSchedule
            {
                CompanyId = companyId, PeriodNumber = i + 1,
                ScheduledDate = schedDate, Amount = amt,
                IsProcessed = false, CreatedBy = userId
            });
        }

        _db.Set<PrepaidExpense>().Add(entity);
        await _db.SaveChangesAsync();
        return await MapPrepaidAsync(entity);
    }

    public async Task<List<PrepaidExpenseResponse>> GetPrepaidExpensesAsync(Guid companyId)
    {
        var items = await _db.Set<PrepaidExpense>()
            .Include(p => p.PrepaidAccount).Include(p => p.ExpenseAccount)
            .Where(p => p.CompanyId == companyId && !p.IsDeleted)
            .OrderByDescending(p => p.CreatedAt).ToListAsync();
        var results = new List<PrepaidExpenseResponse>();
        foreach (var i in items) results.Add(await MapPrepaidAsync(i));
        return results;
    }

    public async Task<PrepaidExpenseResponse> GetPrepaidExpenseDetailAsync(Guid companyId, Guid id)
    {
        var entity = await _db.Set<PrepaidExpense>()
            .Include(p => p.PrepaidAccount).Include(p => p.ExpenseAccount)
            .Include(p => p.Schedules)
            .FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลค่าใช้จ่ายจ่ายล่วงหน้า");
        return await MapPrepaidAsync(entity);
    }

    public async Task<int> ProcessPrepaidAmortizationAsync(Guid companyId, DateTime asOfDate, string userId)
    {
        var schedules = await _db.Set<PrepaidAmortizationSchedule>()
            .Include(s => s.PrepaidExpense).ThenInclude(p => p.PrepaidAccount)
            .Include(s => s.PrepaidExpense).ThenInclude(p => p.ExpenseAccount)
            .Where(s => s.CompanyId == companyId && !s.IsProcessed && s.ScheduledDate <= asOfDate
                && s.PrepaidExpense.Status == "Active")
            .OrderBy(s => s.ScheduledDate).ToListAsync();

        if (schedules.Count == 0) return 0;

        // ATOMICITY FIX: previously each schedule's JE insert + schedule
        // update used a separate SaveChangesAsync. If the process crashed
        // mid-loop the JE was already posted but the schedule was still
        // marked unprocessed → next run would re-post → DOUBLE-AMORTISATION.
        // Now wrap the whole loop in one transaction so either ALL schedules
        // post atomically or NONE do.
        await using var txn = await _db.Database.BeginTransactionAsync();
        int count = 0;
        foreach (var sched in schedules)
        {
            var p = sched.PrepaidExpense;
            var jeNum = await NextJvNumberAsync(companyId);
            var fpId = await GetFiscalPeriodIdAsync(companyId, sched.ScheduledDate);

            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = sched.ScheduledDate,
                JournalType = JournalType.General,
                Description = $"ตัดจ่ายล่วงหน้า: {p.Description} (งวด {sched.PeriodNumber}/{p.TotalPeriods})",
                Reference = p.ReferenceNo, Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = sched.Amount, TotalCredit = sched.Amount,
                Lines = new List<JournalEntryLine>
                {
                    new() { AccountId = p.ExpenseAccountId, DebitAmount = sched.Amount, Description = $"ค่าใช้จ่าย - {p.Description}", LineOrder = 1 },
                    new() { AccountId = p.PrepaidAccountId, CreditAmount = sched.Amount, Description = $"ตัดจ่ายล่วงหน้า - {p.Description}", LineOrder = 2 }
                }
            };
            _db.JournalEntries.Add(je);

            sched.IsProcessed = true;
            sched.JournalEntry = je;     // navigation so EF wires the FK after JE.Id is generated
            p.AmortizedAmount += sched.Amount;
            p.RemainingAmount = p.TotalAmount - p.AmortizedAmount;
            if (p.RemainingAmount <= 0) p.Status = "FullyAmortized";
            count++;
        }
        await _db.SaveChangesAsync();
        await txn.CommitAsync();
        return count;
    }

    private Task<PrepaidExpenseResponse> MapPrepaidAsync(PrepaidExpense p)
    {
        return Task.FromResult(new PrepaidExpenseResponse(
            p.Id, p.ReferenceNo, p.Description, p.StartDate, p.EndDate, p.TotalPeriods,
            p.TotalAmount, p.AmortizedAmount, p.RemainingAmount, p.Status,
            p.PrepaidAccountId, p.PrepaidAccount?.AccountName ?? "",
            p.ExpenseAccountId, p.ExpenseAccount?.AccountName ?? "",
            p.Schedules?.OrderBy(s => s.PeriodNumber).Select(s => new PrepaidScheduleResponse(
                s.Id, s.PeriodNumber, s.ScheduledDate, s.Amount, s.IsProcessed, s.JournalEntryId)).ToList()));
    }

    // ==================== 2. DEPOSIT MANAGEMENT ====================

    public async Task<DepositResponse> CreateDepositAsync(Guid companyId, CreateDepositRequest request, string userId)
    {
        var refNo = $"DEP-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..4].ToUpper()}";

        var entity = new DepositTransaction
        {
            CompanyId = companyId, ReferenceNo = refNo, Description = request.Description,
            Direction = request.Direction, DepositType = request.DepositType,
            Amount = request.Amount, RefundedAmount = 0, RemainingAmount = request.Amount,
            TransactionDate = request.TransactionDate, ExpectedReturnDate = request.ExpectedReturnDate,
            ContactId = request.ContactId, ContactName = request.ContactName,
            DepositAccountId = request.DepositAccountId, CashAccountId = request.CashAccountId,
            CreatedBy = userId
        };

        await using var txn = await _db.Database.BeginTransactionAsync();
        try
        {
            if (request.CashAccountId.HasValue)
            {
                var jeNum = await NextJvNumberAsync(companyId);
                var fpId = await GetFiscalPeriodIdAsync(companyId, request.TransactionDate);
                var lines = new List<JournalEntryLine>();

                if (request.Direction == "Paid")
                {
                    lines.Add(new() { AccountId = request.DepositAccountId, DebitAmount = request.Amount, Description = $"เงินมัดจำจ่าย - {request.Description}", LineOrder = 1 });
                    lines.Add(new() { AccountId = request.CashAccountId.Value, CreditAmount = request.Amount, Description = "จ่ายเงินมัดจำ", LineOrder = 2 });
                }
                else
                {
                    lines.Add(new() { AccountId = request.CashAccountId.Value, DebitAmount = request.Amount, Description = "รับเงินมัดจำ", LineOrder = 1 });
                    lines.Add(new() { AccountId = request.DepositAccountId, CreditAmount = request.Amount, Description = $"เงินมัดจำรับ - {request.Description}", LineOrder = 2 });
                }

                var je = new JournalEntry
                {
                    CompanyId = companyId, EntryNumber = jeNum, EntryDate = request.TransactionDate,
                    JournalType = JournalType.General, Description = $"{(request.Direction == "Paid" ? "จ่าย" : "รับ")}เงินมัดจำ: {request.Description}",
                    Reference = refNo, Status = JournalEntryStatus.Posted, IsAutoGenerated = true,
                    FiscalPeriodId = fpId, TotalDebit = request.Amount, TotalCredit = request.Amount, Lines = lines
                };
                _db.JournalEntries.Add(je);
                await _db.SaveChangesAsync();
                entity.JournalEntryId = je.Id;
            }

            _db.Set<DepositTransaction>().Add(entity);
            await _db.SaveChangesAsync();
            await txn.CommitAsync();
        }
        catch
        {
            await txn.RollbackAsync();
            throw;
        }
        return MapDeposit(entity);
    }

    public async Task<List<DepositResponse>> GetDepositsAsync(Guid companyId, string? direction)
    {
        var q = _db.Set<DepositTransaction>()
            .Include(d => d.DepositAccount).Include(d => d.Refunds)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted);
        if (!string.IsNullOrEmpty(direction)) q = q.Where(d => d.Direction == direction);
        var items = await q.OrderByDescending(d => d.TransactionDate).ToListAsync();
        return items.Select(MapDeposit).ToList();
    }

    public async Task<DepositResponse> RefundDepositAsync(Guid companyId, Guid depositId, DepositRefundRequest request, string userId)
    {
        var dep = await _db.Set<DepositTransaction>()
            .Include(d => d.DepositAccount).Include(d => d.Refunds)
            .FirstOrDefaultAsync(d => d.Id == depositId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเงินมัดจำ");

        if (request.Amount > dep.RemainingAmount)
            throw new InvalidOperationException("จำนวนเงินคืนมากกว่ายอดคงเหลือ");

        var refund = new DepositRefund
        {
            CompanyId = companyId, DepositTransactionId = dep.Id,
            RefundDate = DateTime.UtcNow, Amount = request.Amount, Notes = request.Notes, CreatedBy = userId
        };

        await using var txn = await _db.Database.BeginTransactionAsync();
        try
        {
            if (dep.CashAccountId.HasValue)
            {
                var jeNum = await NextJvNumberAsync(companyId);
                var fpId = await GetFiscalPeriodIdAsync(companyId, DateTime.UtcNow);
                var lines = new List<JournalEntryLine>();

                if (dep.Direction == "Paid")
                {
                    lines.Add(new() { AccountId = dep.CashAccountId.Value, DebitAmount = request.Amount, Description = "รับคืนเงินมัดจำ", LineOrder = 1 });
                    lines.Add(new() { AccountId = dep.DepositAccountId, CreditAmount = request.Amount, Description = $"คืนเงินมัดจำ - {dep.Description}", LineOrder = 2 });
                }
                else
                {
                    lines.Add(new() { AccountId = dep.DepositAccountId, DebitAmount = request.Amount, Description = $"คืนเงินมัดจำ - {dep.Description}", LineOrder = 1 });
                    lines.Add(new() { AccountId = dep.CashAccountId.Value, CreditAmount = request.Amount, Description = "จ่ายคืนเงินมัดจำ", LineOrder = 2 });
                }

                var je = new JournalEntry
                {
                    CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                    JournalType = JournalType.General, Description = $"คืนเงินมัดจำ: {dep.Description}",
                    Reference = dep.ReferenceNo, Status = JournalEntryStatus.Posted, IsAutoGenerated = true,
                    FiscalPeriodId = fpId, TotalDebit = request.Amount, TotalCredit = request.Amount, Lines = lines
                };
                _db.JournalEntries.Add(je);
                await _db.SaveChangesAsync();
                refund.JournalEntryId = je.Id;
            }

            dep.RefundedAmount += request.Amount;
            dep.RemainingAmount -= request.Amount;
            dep.Status = dep.RemainingAmount <= 0 ? "FullyRefunded" : "PartiallyRefunded";
            dep.Refunds.Add(refund);
            await _db.SaveChangesAsync();
            await txn.CommitAsync();
        }
        catch
        {
            await txn.RollbackAsync();
            throw;
        }
        return MapDeposit(dep);
    }

    private static DepositResponse MapDeposit(DepositTransaction d) => new(
        d.Id, d.ReferenceNo, d.Description, d.Direction, d.DepositType,
        d.Amount, d.RefundedAmount, d.RemainingAmount, d.Status,
        d.TransactionDate, d.ExpectedReturnDate, d.ContactName,
        d.DepositAccountId, d.DepositAccount?.AccountName ?? "",
        d.Refunds?.Select(r => new DepositRefundResponse(r.Id, r.RefundDate, r.Amount, r.Notes, r.JournalEntryId)).ToList());

    // ==================== 3. BAD DEBT ALLOWANCE ====================

    public async Task<BadDebtAllowanceResponse> CreateBadDebtAllowanceAsync(Guid companyId, CreateBadDebtAllowanceRequest request, string userId)
    {
        var refNo = $"BDA-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..4].ToUpper()}";

        // Get current AR balance for aging calculation
        decimal totalReceivable = 0;
        var lines = new List<BadDebtAllowanceLine>();

        if (request.Method == "Aging" && (request.Lines == null || request.Lines.Count == 0))
        {
            // Auto-calculate from aging report
            var agingBuckets = new[] {
                ("Current", 0m), ("1-30", 1m), ("31-60", 2m), ("61-90", 5m), ("90+", 10m)
            };
            foreach (var (bucket, pct) in agingBuckets)
            {
                lines.Add(new BadDebtAllowanceLine
                {
                    CompanyId = companyId, AgingBucket = bucket,
                    OutstandingAmount = 0, AllowancePercentage = pct, AllowanceAmount = 0
                });
            }
        }
        else if (request.Lines != null)
        {
            foreach (var l in request.Lines)
            {
                var amt = Math.Round(l.OutstandingAmount * l.AllowancePercentage / 100, 2, MidpointRounding.AwayFromZero);
                totalReceivable += l.OutstandingAmount;
                lines.Add(new BadDebtAllowanceLine
                {
                    CompanyId = companyId, ContactId = l.ContactId, ContactName = l.ContactName,
                    AgingBucket = l.AgingBucket, OutstandingAmount = l.OutstandingAmount,
                    AllowancePercentage = l.AllowancePercentage, AllowanceAmount = amt
                });
            }
        }

        var allowanceTotal = lines.Sum(l => l.AllowanceAmount);

        // Get previous allowance balance
        var prevAllowance = await _db.Set<BadDebtAllowance>()
            .Where(b => b.CompanyId == companyId && b.Status == "Posted" && !b.IsDeleted)
            .OrderByDescending(b => b.AllowanceDate)
            .Select(b => b.AllowanceAmount).FirstOrDefaultAsync();

        var entity = new BadDebtAllowance
        {
            CompanyId = companyId, ReferenceNo = refNo, AllowanceDate = DateTime.UtcNow,
            Method = request.Method, TotalReceivable = totalReceivable,
            AllowanceAmount = allowanceTotal, PreviousAllowance = prevAllowance,
            AdjustmentAmount = allowanceTotal - prevAllowance,
            Notes = request.Notes, CreatedBy = userId, Lines = lines
        };

        _db.Set<BadDebtAllowance>().Add(entity);
        await _db.SaveChangesAsync();
        return MapBadDebt(entity);
    }

    public async Task<List<BadDebtAllowanceResponse>> GetBadDebtAllowancesAsync(Guid companyId)
    {
        var items = await _db.Set<BadDebtAllowance>()
            .Include(b => b.Lines)
            .Where(b => b.CompanyId == companyId && !b.IsDeleted)
            .OrderByDescending(b => b.AllowanceDate).ToListAsync();
        return items.Select(MapBadDebt).ToList();
    }

    public async Task<BadDebtAllowanceResponse> PostBadDebtAllowanceAsync(Guid companyId, Guid id, string userId)
    {
        var entity = await _db.Set<BadDebtAllowance>()
            .Include(b => b.Lines)
            .FirstOrDefaultAsync(b => b.Id == id && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูล");

        if (entity.Status == "Posted") throw new InvalidOperationException("บันทึกแล้ว");
        if (entity.AdjustmentAmount == 0) { entity.Status = "Posted"; await _db.SaveChangesAsync(); return MapBadDebt(entity); }

        // Dr 57130 หนี้สงสัยจะสูญ / Cr 18100 ค่าเผื่อหนี้สงสัยจะสูญ (or reverse)
        var badDebtExpenseAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("571") && a.IsActive);
        var allowanceAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("181") && a.IsActive);

        if (badDebtExpenseAcc != null && allowanceAcc != null)
        {
            var jeNum = await NextJvNumberAsync(companyId);
            var fpId = await GetFiscalPeriodIdAsync(companyId, DateTime.UtcNow);
            var absAmt = Math.Abs(entity.AdjustmentAmount);
            var lines = new List<JournalEntryLine>();

            if (entity.AdjustmentAmount > 0)
            {
                lines.Add(new() { AccountId = badDebtExpenseAcc.Id, DebitAmount = absAmt, Description = "หนี้สงสัยจะสูญ", LineOrder = 1 });
                lines.Add(new() { AccountId = allowanceAcc.Id, CreditAmount = absAmt, Description = "ค่าเผื่อหนี้สงสัยจะสูญ", LineOrder = 2 });
            }
            else
            {
                lines.Add(new() { AccountId = allowanceAcc.Id, DebitAmount = absAmt, Description = "กลับรายการค่าเผื่อหนี้สงสัยจะสูญ", LineOrder = 1 });
                lines.Add(new() { AccountId = badDebtExpenseAcc.Id, CreditAmount = absAmt, Description = "กลับรายการหนี้สงสัยจะสูญ", LineOrder = 2 });
            }

            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                JournalType = JournalType.General, Description = $"ปรับปรุงค่าเผื่อหนี้สงสัยจะสูญ ({entity.ReferenceNo})",
                Reference = entity.ReferenceNo, Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = absAmt, TotalCredit = absAmt, Lines = lines
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.JournalEntryId = je.Id;
        }

        entity.Status = "Posted";
        await _db.SaveChangesAsync();
        return MapBadDebt(entity);
    }

    private static BadDebtAllowanceResponse MapBadDebt(BadDebtAllowance b) => new(
        b.Id, b.ReferenceNo, b.AllowanceDate, b.Method,
        b.TotalReceivable, b.AllowanceAmount, b.PreviousAllowance, b.AdjustmentAmount,
        b.Status, b.Notes, b.JournalEntryId,
        b.Lines?.Select(l => new BadDebtAllowanceLineResponse(
            l.ContactId, l.ContactName, l.AgingBucket,
            l.OutstandingAmount, l.AllowancePercentage, l.AllowanceAmount)).ToList());

    // ==================== 4. INVENTORY OBSOLESCENCE ====================

    public async Task<InventoryObsolescenceResponse> CreateInventoryObsolescenceAsync(Guid companyId, CreateInventoryObsolescenceRequest request, string userId)
    {
        var refNo = $"IOB-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..4].ToUpper()}";
        var today = DateTime.UtcNow.Date;

        // Get products with stock
        var products = await _db.Products
            .Where(p => p.CompanyId == companyId && p.TrackStock && !p.IsDeleted && p.CurrentStock > 0)
            .ToListAsync();
        var productIds = products.Select(p => p.Id).ToList();

        // Last movement dates
        var lastMoves = await _db.StockMovements
            .Where(m => productIds.Contains(m.ProductId) && !m.IsDeleted)
            .GroupBy(m => m.ProductId)
            .Select(g => new { ProductId = g.Key, LastDate = g.Max(m => m.MovementDate) })
            .ToListAsync();
        var lastMoveLookup = lastMoves.ToDictionary(m => m.ProductId, m => m.LastDate);

        // Avg costs
        var costs = await _db.StockMovements
            .Where(m => productIds.Contains(m.ProductId) && m.MovementType == "IN" && !m.IsDeleted)
            .GroupBy(m => m.ProductId)
            .Select(g => new { ProductId = g.Key, TotalCost = g.Sum(m => m.Quantity * m.UnitCost), TotalQty = g.Sum(m => m.Quantity) })
            .ToListAsync();
        var costLookup = costs.ToDictionary(c => c.ProductId);

        var lines = new List<InventoryObsolescenceLine>();
        foreach (var p in products)
        {
            var lastMove = lastMoveLookup.GetValueOrDefault(p.Id, p.CreatedAt);
            var days = (int)(today - lastMove).TotalDays;
            var bucket = days switch { <= 90 => "0-90", <= 180 => "91-180", <= 365 => "181-365", _ => "365+" };
            var pct = days switch { <= 90 => 0m, <= 180 => 5m, <= 365 => 15m, _ => 30m };

            var avgCost = p.CostPrice;
            if (costLookup.TryGetValue(p.Id, out var cd) && cd.TotalQty > 0) avgCost = cd.TotalCost / cd.TotalQty;
            var stockValue = p.CurrentStock * avgCost;
            var allowanceAmt = Math.Round(stockValue * pct / 100, 2, MidpointRounding.AwayFromZero);

            lines.Add(new InventoryObsolescenceLine
            {
                CompanyId = companyId, ProductId = p.Id, AgingBucket = bucket,
                CurrentStock = p.CurrentStock, StockValue = stockValue,
                AllowancePercentage = pct, AllowanceAmount = allowanceAmt
            });
        }

        var totalInvValue = lines.Sum(l => l.StockValue);
        var totalAllowance = lines.Sum(l => l.AllowanceAmount);

        var prevAllowance = await _db.Set<InventoryObsolescenceAllowance>()
            .Where(i => i.CompanyId == companyId && i.Status == "Posted" && !i.IsDeleted)
            .OrderByDescending(i => i.AllowanceDate)
            .Select(i => i.AllowanceAmount).FirstOrDefaultAsync();

        var entity = new InventoryObsolescenceAllowance
        {
            CompanyId = companyId, ReferenceNo = refNo, AllowanceDate = DateTime.UtcNow,
            Method = request.Method, TotalInventoryValue = totalInvValue,
            AllowanceAmount = totalAllowance, PreviousAllowance = prevAllowance,
            AdjustmentAmount = totalAllowance - prevAllowance,
            Notes = request.Notes, CreatedBy = userId, Lines = lines
        };

        _db.Set<InventoryObsolescenceAllowance>().Add(entity);
        await _db.SaveChangesAsync();
        return MapObsolescence(entity);
    }

    public async Task<List<InventoryObsolescenceResponse>> GetInventoryObsolescencesAsync(Guid companyId)
    {
        var items = await _db.Set<InventoryObsolescenceAllowance>()
            .Include(i => i.Lines).ThenInclude(l => l.Product)
            .Where(i => i.CompanyId == companyId && !i.IsDeleted)
            .OrderByDescending(i => i.AllowanceDate).ToListAsync();
        return items.Select(MapObsolescence).ToList();
    }

    public async Task<InventoryObsolescenceResponse> PostInventoryObsolescenceAsync(Guid companyId, Guid id, string userId)
    {
        var entity = await _db.Set<InventoryObsolescenceAllowance>()
            .Include(i => i.Lines).ThenInclude(l => l.Product)
            .FirstOrDefaultAsync(i => i.Id == id && i.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูล");

        if (entity.Status == "Posted") throw new InvalidOperationException("บันทึกแล้ว");
        if (entity.AdjustmentAmount == 0) { entity.Status = "Posted"; await _db.SaveChangesAsync(); return MapObsolescence(entity); }

        // Dr 51xxx (ต้นทุน) / Cr 18200 (ค่าเผื่อสินค้าล้าสมัย)
        var cogsAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("511") && a.IsActive);
        var allowanceAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("182") && a.IsActive);

        if (cogsAcc != null && allowanceAcc != null)
        {
            var jeNum = await NextJvNumberAsync(companyId);
            var fpId = await GetFiscalPeriodIdAsync(companyId, DateTime.UtcNow);
            var absAmt = Math.Abs(entity.AdjustmentAmount);
            var lines = new List<JournalEntryLine>();

            if (entity.AdjustmentAmount > 0)
            {
                lines.Add(new() { AccountId = cogsAcc.Id, DebitAmount = absAmt, Description = "ค่าเผื่อสินค้าล้าสมัย/ด้อยค่า", LineOrder = 1 });
                lines.Add(new() { AccountId = allowanceAcc.Id, CreditAmount = absAmt, Description = "ค่าเผื่อการด้อยค่าสินค้า", LineOrder = 2 });
            }
            else
            {
                lines.Add(new() { AccountId = allowanceAcc.Id, DebitAmount = absAmt, Description = "กลับรายการค่าเผื่อสินค้า", LineOrder = 1 });
                lines.Add(new() { AccountId = cogsAcc.Id, CreditAmount = absAmt, Description = "กลับรายการค่าเผื่อ", LineOrder = 2 });
            }

            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                JournalType = JournalType.General, Description = $"ปรับปรุงค่าเผื่อสินค้าล้าสมัย ({entity.ReferenceNo})",
                Reference = entity.ReferenceNo, Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = absAmt, TotalCredit = absAmt, Lines = lines
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.JournalEntryId = je.Id;
        }

        entity.Status = "Posted";
        await _db.SaveChangesAsync();
        return MapObsolescence(entity);
    }

    private static InventoryObsolescenceResponse MapObsolescence(InventoryObsolescenceAllowance i) => new(
        i.Id, i.ReferenceNo, i.AllowanceDate, i.Method,
        i.TotalInventoryValue, i.AllowanceAmount, i.PreviousAllowance, i.AdjustmentAmount,
        i.Status, i.Notes, i.JournalEntryId,
        i.Lines?.Select(l => new InventoryObsolescenceLineResponse(
            l.ProductId, l.Product?.Code ?? "", l.Product?.Name ?? "",
            l.AgingBucket, l.CurrentStock, l.StockValue, l.AllowancePercentage, l.AllowanceAmount)).ToList());

    // ==================== 5. ACCRUED EXPENSES ====================

    public async Task<AccruedExpenseResponse> CreateAccruedExpenseAsync(Guid companyId, CreateAccruedExpenseRequest request, string userId)
    {
        var refNo = $"ACR-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..4].ToUpper()}";

        var entity = new AccruedExpense
        {
            CompanyId = companyId, ReferenceNo = refNo, Description = request.Description,
            ExpenseType = request.ExpenseType, AccrualDate = DateTime.UtcNow,
            Amount = request.Amount, PaidAmount = 0, RemainingAmount = request.Amount,
            IsRecurring = request.IsRecurring, RecurringFrequency = request.RecurringFrequency,
            ExpenseAccountId = request.ExpenseAccountId, AccruedAccountId = request.AccruedAccountId,
            CreatedBy = userId
        };

        await using var txn = await _db.Database.BeginTransactionAsync();
        try
        {
            var jeNum = await NextJvNumberAsync(companyId);
            var fpId = await GetFiscalPeriodIdAsync(companyId, DateTime.UtcNow);
            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                JournalType = JournalType.General,
                Description = $"ตั้งค้างจ่าย: {request.Description}",
                Reference = refNo, Status = JournalEntryStatus.Posted, IsAutoGenerated = true,
                FiscalPeriodId = fpId, TotalDebit = request.Amount, TotalCredit = request.Amount,
                Lines = new List<JournalEntryLine>
                {
                    new() { AccountId = request.ExpenseAccountId, DebitAmount = request.Amount, Description = $"ค่าใช้จ่าย - {request.Description}", LineOrder = 1 },
                    new() { AccountId = request.AccruedAccountId, CreditAmount = request.Amount, Description = $"ค้างจ่าย - {request.Description}", LineOrder = 2 }
                }
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.AccrualJournalId = je.Id;

            _db.Set<AccruedExpense>().Add(entity);
            await _db.SaveChangesAsync();
            await txn.CommitAsync();
        }
        catch
        {
            await txn.RollbackAsync();
            throw;
        }
        return MapAccrued(entity);
    }

    public async Task<List<AccruedExpenseResponse>> GetAccruedExpensesAsync(Guid companyId)
    {
        var items = await _db.Set<AccruedExpense>()
            .Include(a => a.ExpenseAccount).Include(a => a.AccruedAccount)
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .OrderByDescending(a => a.AccrualDate).ToListAsync();
        return items.Select(MapAccrued).ToList();
    }

    public async Task<AccruedExpenseResponse> PayAccruedExpenseAsync(Guid companyId, Guid id, PayAccruedExpenseRequest request, string userId)
    {
        var entity = await _db.Set<AccruedExpense>()
            .Include(a => a.ExpenseAccount).Include(a => a.AccruedAccount)
            .FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูล");

        if (request.Amount > entity.RemainingAmount)
            throw new InvalidOperationException("จำนวนเงินมากกว่ายอดค้างจ่าย");

        var cashAccId = request.CashAccountId
            ?? (await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.IsActive))?.Id
            ?? throw new InvalidOperationException("ไม่พบบัญชีเงินสด");

        // Journal: Dr Accrued / Cr Cash
        var jeNum = await NextJvNumberAsync(companyId);
        var fpId = await GetFiscalPeriodIdAsync(companyId, DateTime.UtcNow);
        var je = new JournalEntry
        {
            CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
            JournalType = JournalType.General,
            Description = $"จ่ายค้างจ่าย: {entity.Description}",
            Reference = entity.ReferenceNo, Status = JournalEntryStatus.Posted, IsAutoGenerated = true,
            FiscalPeriodId = fpId, TotalDebit = request.Amount, TotalCredit = request.Amount,
            Lines = new List<JournalEntryLine>
            {
                new() { AccountId = entity.AccruedAccountId, DebitAmount = request.Amount, Description = $"จ่ายค้างจ่าย - {entity.Description}", LineOrder = 1 },
                new() { AccountId = cashAccId, CreditAmount = request.Amount, Description = "จ่ายเงิน", LineOrder = 2 }
            }
        };
        _db.JournalEntries.Add(je);
        await _db.SaveChangesAsync();

        entity.PaidAmount += request.Amount;
        entity.RemainingAmount -= request.Amount;
        entity.PaymentJournalId = je.Id;
        entity.Status = entity.RemainingAmount <= 0 ? "Paid" : "PartiallyPaid";
        await _db.SaveChangesAsync();
        return MapAccrued(entity);
    }

    private static AccruedExpenseResponse MapAccrued(AccruedExpense a) => new(
        a.Id, a.ReferenceNo, a.Description, a.ExpenseType,
        a.AccrualDate, a.Amount, a.PaidAmount, a.RemainingAmount, a.Status,
        a.IsRecurring, a.RecurringFrequency,
        a.ExpenseAccountId, a.ExpenseAccount?.AccountName ?? "",
        a.AccruedAccountId, a.AccruedAccount?.AccountName ?? "",
        a.AccrualJournalId, a.PaymentJournalId);
}
