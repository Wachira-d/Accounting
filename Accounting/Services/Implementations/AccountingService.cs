using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class AccountingService : IAccountingService
{
    private readonly AccountingDbContext _db;

    public AccountingService(AccountingDbContext db)
    {
        _db = db;
    }

    // ==================== Chart of Accounts ====================

    public async Task<AccountResponse> CreateAccountAsync(Guid companyId, CreateAccountRequest request)
    {
        if (await _db.ChartOfAccounts.AnyAsync(a => a.CompanyId == companyId && a.AccountCode == request.AccountCode))
            throw new InvalidOperationException($"รหัสบัญชี {request.AccountCode} ซ้ำ");

        var level = 1;
        if (request.ParentAccountId.HasValue)
        {
            var parent = await _db.ChartOfAccounts.FindAsync(request.ParentAccountId.Value);
            if (parent != null) level = parent.Level + 1;
        }

        var account = new ChartOfAccount
        {
            CompanyId = companyId,
            AccountCode = request.AccountCode,
            AccountName = request.AccountName,
            AccountNameEn = request.AccountNameEn,
            AccountType = request.AccountType,
            ParentAccountId = request.ParentAccountId,
            Level = level,
            Description = request.Description
        };

        _db.ChartOfAccounts.Add(account);
        await _db.SaveChangesAsync();

        return MapAccountToResponse(account);
    }

    public async Task<List<AccountResponse>> GetAccountsAsync(Guid companyId)
    {
        var accounts = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId)
            .OrderBy(a => a.AccountCode)
            .ToListAsync();

        return accounts.Select(MapAccountToResponse).ToList();
    }

    public async Task<AccountResponse> UpdateAccountAsync(Guid companyId, Guid accountId, UpdateAccountRequest request)
    {
        var account = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชี");

        if (account.IsSystemAccount)
            throw new InvalidOperationException("ไม่สามารถแก้ไขบัญชีระบบได้");

        if (request.AccountName != null) account.AccountName = request.AccountName;
        if (request.AccountNameEn != null) account.AccountNameEn = request.AccountNameEn;
        if (request.Description != null) account.Description = request.Description;
        if (request.IsActive.HasValue) account.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
        return MapAccountToResponse(account);
    }

    public async Task SeedDefaultAccountsAsync(Guid companyId)
    {
        var defaults = new List<(string code, string name, string nameEn, AccountType type)>
        {
            ("1000", "สินทรัพย์", "Assets", AccountType.Asset),
            ("1100", "เงินสดและเงินฝากธนาคาร", "Cash and Bank", AccountType.Asset),
            ("1110", "เงินสด", "Cash", AccountType.Asset),
            ("1120", "เงินฝากธนาคาร", "Bank Deposits", AccountType.Asset),
            ("1200", "ลูกหนี้การค้า", "Accounts Receivable", AccountType.Asset),
            ("1300", "สินค้าคงเหลือ", "Inventory", AccountType.Asset),
            ("1400", "สินทรัพย์หมุนเวียนอื่น", "Other Current Assets", AccountType.Asset),
            ("1500", "ที่ดิน อาคาร อุปกรณ์", "Property, Plant & Equipment", AccountType.Asset),
            ("2000", "หนี้สิน", "Liabilities", AccountType.Liability),
            ("2100", "เจ้าหนี้การค้า", "Accounts Payable", AccountType.Liability),
            ("2200", "ภาษีมูลค่าเพิ่มค้างจ่าย", "VAT Payable", AccountType.Liability),
            ("2300", "ภาษีหัก ณ ที่จ่ายค้างจ่าย", "WHT Payable", AccountType.Liability),
            ("2400", "หนี้สินหมุนเวียนอื่น", "Other Current Liabilities", AccountType.Liability),
            ("2500", "หนี้สินระยะยาว", "Long-term Liabilities", AccountType.Liability),
            ("3000", "ส่วนของเจ้าของ", "Equity", AccountType.Equity),
            ("3100", "ทุนจดทะเบียน", "Registered Capital", AccountType.Equity),
            ("3200", "กำไรสะสม", "Retained Earnings", AccountType.Equity),
            ("4000", "รายได้", "Revenue", AccountType.Revenue),
            ("4100", "รายได้จากการขาย", "Sales Revenue", AccountType.Revenue),
            ("4200", "รายได้จากการให้บริการ", "Service Revenue", AccountType.Revenue),
            ("4900", "รายได้อื่น", "Other Revenue", AccountType.Revenue),
            ("5000", "ค่าใช้จ่าย", "Expenses", AccountType.Expense),
            ("5100", "ต้นทุนขาย", "Cost of Goods Sold", AccountType.Expense),
            ("5200", "เงินเดือนและค่าจ้าง", "Salaries and Wages", AccountType.Expense),
            ("5300", "ค่าเช่า", "Rent Expense", AccountType.Expense),
            ("5400", "ค่าสาธารณูปโภค", "Utilities Expense", AccountType.Expense),
            ("5500", "ค่าเสื่อมราคา", "Depreciation Expense", AccountType.Expense),
            ("5900", "ค่าใช้จ่ายอื่น", "Other Expenses", AccountType.Expense),
        };

        foreach (var (code, name, nameEn, type) in defaults)
        {
            _db.ChartOfAccounts.Add(new ChartOfAccount
            {
                CompanyId = companyId,
                AccountCode = code,
                AccountName = name,
                AccountNameEn = nameEn,
                AccountType = type,
                Level = code.Length == 4 && code.EndsWith("000") ? 1 : 2,
                IsSystemAccount = true,
                IsActive = true
            });
        }

        await _db.SaveChangesAsync();
    }

    // ==================== Journal Entries ====================

    public async Task<JournalEntryResponse> CreateJournalEntryAsync(Guid companyId, CreateJournalEntryRequest request, string createdBy)
    {
        // Validate debit = credit
        var totalDebit = request.Lines.Sum(l => l.DebitAmount);
        var totalCredit = request.Lines.Sum(l => l.CreditAmount);
        if (totalDebit != totalCredit)
            throw new InvalidOperationException($"ยอดเดบิต ({totalDebit:N2}) ไม่เท่ากับยอดเครดิต ({totalCredit:N2})");

        if (totalDebit == 0)
            throw new InvalidOperationException("ต้องมียอดเดบิต/เครดิตอย่างน้อย 1 รายการ");

        // Generate entry number
        var count = await _db.JournalEntries.CountAsync(j => j.CompanyId == companyId);
        var entryNumber = $"JV-{DateTime.UtcNow:yyyyMM}-{(count + 1):D4}";

        var entry = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = request.EntryDate,
            Description = request.Description,
            Reference = request.Reference,
            TotalDebit = totalDebit,
            TotalCredit = totalCredit,
            CreatedBy = createdBy
        };

        // Find fiscal period
        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId &&
            f.StartDate <= request.EntryDate &&
            f.EndDate >= request.EntryDate &&
            f.Status == FiscalPeriodStatus.Open);
        if (period != null)
            entry.FiscalPeriodId = period.Id;

        _db.JournalEntries.Add(entry);

        var order = 1;
        foreach (var line in request.Lines)
        {
            _db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = entry.Id,
                AccountId = line.AccountId,
                DebitAmount = line.DebitAmount,
                CreditAmount = line.CreditAmount,
                Description = line.Description,
                LineOrder = order++
            });
        }

        await _db.SaveChangesAsync();
        return await GetJournalEntryAsync(companyId, entry.Id);
    }

    public async Task<JournalEntryResponse> GetJournalEntryAsync(Guid companyId, Guid entryId)
    {
        var entry = await _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        return MapJournalEntryToResponse(entry);
    }

    public async Task<PagedResponse<JournalEntryResponse>> GetJournalEntriesAsync(Guid companyId, PagedRequest request, string? status = null, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var query = _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Where(j => j.CompanyId == companyId);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(j => j.EntryNumber.Contains(request.Search) || (j.Description != null && j.Description.Contains(request.Search)));

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<Models.Enums.JournalEntryStatus>(status, true, out var parsedStatus))
            query = query.Where(j => j.Status == parsedStatus);

        if (fromDate.HasValue)
            query = query.Where(j => j.EntryDate >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(j => j.EntryDate <= toDate.Value);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(j => j.EntryDate)
            .ThenByDescending(j => j.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<JournalEntryResponse>(
            items.Select(MapJournalEntryToResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<JournalEntryResponse> PostJournalEntryAsync(Guid companyId, Guid entryId)
    {
        var entry = await _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        if (entry.Status != JournalEntryStatus.Draft)
            throw new InvalidOperationException("สามารถ post ได้เฉพาะใบสำคัญที่เป็น Draft เท่านั้น");

        entry.Status = JournalEntryStatus.Posted;
        await _db.SaveChangesAsync();

        return MapJournalEntryToResponse(entry);
    }

    public async Task VoidJournalEntryAsync(Guid companyId, Guid entryId)
    {
        var entry = await _db.JournalEntries.FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        entry.Status = JournalEntryStatus.Voided;
        await _db.SaveChangesAsync();
    }

    // ==================== Reports ====================

    public async Task<TrialBalanceResponse> GetTrialBalanceAsync(Guid companyId, DateTime asOfDate)
    {
        var postedLines = await _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate <= asOfDate)
            .ToListAsync();

        var grouped = postedLines
            .GroupBy(l => new { l.AccountId, l.Account.AccountCode, l.Account.AccountName, l.Account.AccountType })
            .Select(g => new TrialBalanceItem(
                g.Key.AccountCode,
                g.Key.AccountName,
                g.Key.AccountType,
                g.Sum(l => l.DebitAmount),
                g.Sum(l => l.CreditAmount)))
            .OrderBy(i => i.AccountCode)
            .ToList();

        return new TrialBalanceResponse(
            asOfDate, grouped,
            grouped.Sum(i => i.DebitBalance),
            grouped.Sum(i => i.CreditBalance));
    }

    public async Task<BalanceSheetResponse> GetBalanceSheetAsync(Guid companyId, DateTime asOfDate)
    {
        var trial = await GetTrialBalanceAsync(companyId, asOfDate);

        var assets = trial.Items.Where(i => i.AccountType == AccountType.Asset)
            .Select(i => new BalanceSheetSection(i.AccountCode, i.AccountName, i.DebitBalance - i.CreditBalance, null)).ToList();
        var liabilities = trial.Items.Where(i => i.AccountType == AccountType.Liability)
            .Select(i => new BalanceSheetSection(i.AccountCode, i.AccountName, i.CreditBalance - i.DebitBalance, null)).ToList();
        var equity = trial.Items.Where(i => i.AccountType == AccountType.Equity)
            .Select(i => new BalanceSheetSection(i.AccountCode, i.AccountName, i.CreditBalance - i.DebitBalance, null)).ToList();

        // Add net income to equity
        var revenue = trial.Items.Where(i => i.AccountType == AccountType.Revenue).Sum(i => i.CreditBalance - i.DebitBalance);
        var expenses = trial.Items.Where(i => i.AccountType == AccountType.Expense).Sum(i => i.DebitBalance - i.CreditBalance);
        var netIncome = revenue - expenses;
        equity.Add(new BalanceSheetSection("", "กำไร(ขาดทุน)สุทธิ", netIncome, null));

        return new BalanceSheetResponse(
            asOfDate, assets, liabilities, equity,
            assets.Sum(a => a.Amount),
            liabilities.Sum(l => l.Amount),
            equity.Sum(e => e.Amount));
    }

    public async Task<ProfitAndLossResponse> GetProfitAndLossAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var postedLines = await _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fromDate
                && l.JournalEntry.EntryDate <= toDate
                && (l.Account.AccountType == AccountType.Revenue || l.Account.AccountType == AccountType.Expense))
            .ToListAsync();

        var grouped = postedLines
            .GroupBy(l => new { l.Account.AccountCode, l.Account.AccountName, l.Account.AccountType })
            .ToList();

        var revenue = grouped.Where(g => g.Key.AccountType == AccountType.Revenue)
            .Select(g => new PnlSection(g.Key.AccountCode, g.Key.AccountName, g.Sum(l => l.CreditAmount - l.DebitAmount)))
            .OrderBy(r => r.AccountCode).ToList();

        var expenses = grouped.Where(g => g.Key.AccountType == AccountType.Expense)
            .Select(g => new PnlSection(g.Key.AccountCode, g.Key.AccountName, g.Sum(l => l.DebitAmount - l.CreditAmount)))
            .OrderBy(e => e.AccountCode).ToList();

        var totalRevenue = revenue.Sum(r => r.Amount);
        var totalExpenses = expenses.Sum(e => e.Amount);

        return new ProfitAndLossResponse(
            fromDate, toDate, revenue, expenses,
            totalRevenue, totalExpenses, totalRevenue - totalExpenses);
    }

    // ==================== Cash Flow Statement ====================

    public async Task<CashFlowStatementResponse> GetCashFlowStatementAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var postedLines = await _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fromDate
                && l.JournalEntry.EntryDate <= toDate)
            .ToListAsync();

        // Operating Activities: Revenue & Expense accounts + changes in current assets/liabilities
        var operatingItems = new List<CashFlowLineItem>();

        // Net income
        var revenue = postedLines.Where(l => l.Account.AccountType == AccountType.Revenue)
            .Sum(l => l.CreditAmount - l.DebitAmount);
        var expenses = postedLines.Where(l => l.Account.AccountType == AccountType.Expense)
            .Sum(l => l.DebitAmount - l.CreditAmount);
        var netIncome = revenue - expenses;
        operatingItems.Add(new CashFlowLineItem("กำไร(ขาดทุน)สุทธิ", null, netIncome));

        // Depreciation add-back (non-cash expense)
        var depreciation = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("55"))
            .Sum(l => l.DebitAmount - l.CreditAmount);
        if (depreciation != 0)
            operatingItems.Add(new CashFlowLineItem("ค่าเสื่อมราคา (บวกกลับ)", "5500", depreciation));

        // Changes in AR
        var arChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("12"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (arChange != 0)
            operatingItems.Add(new CashFlowLineItem("ลูกหนี้การค้า (เพิ่มขึ้น)/ลดลง", "1200", arChange));

        // Changes in Inventory
        var inventoryChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("13"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (inventoryChange != 0)
            operatingItems.Add(new CashFlowLineItem("สินค้าคงเหลือ (เพิ่มขึ้น)/ลดลง", "1300", inventoryChange));

        // Changes in AP
        var apChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("21"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (apChange != 0)
            operatingItems.Add(new CashFlowLineItem("เจ้าหนี้การค้า เพิ่มขึ้น/(ลดลง)", "2100", apChange));

        // Tax payable changes
        var taxPayableChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("22") || l.Account.AccountCode.StartsWith("23"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (taxPayableChange != 0)
            operatingItems.Add(new CashFlowLineItem("ภาษีค้างจ่าย เพิ่มขึ้น/(ลดลง)", "2200", taxPayableChange));

        var operatingTotal = operatingItems.Sum(i => i.Amount);

        // Investing Activities: Fixed asset accounts (15xx)
        var investingItems = new List<CashFlowLineItem>();
        var fixedAssetChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("15"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (fixedAssetChange != 0)
            investingItems.Add(new CashFlowLineItem("ซื้อ/ขาย ที่ดิน อาคาร อุปกรณ์", "1500", fixedAssetChange));

        var investingTotal = investingItems.Sum(i => i.Amount);

        // Financing Activities: Long-term liabilities (25xx) + Equity (3xxx)
        var financingItems = new List<CashFlowLineItem>();
        var longTermDebtChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("25"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (longTermDebtChange != 0)
            financingItems.Add(new CashFlowLineItem("เงินกู้ยืมระยะยาว เพิ่มขึ้น/(ลดลง)", "2500", longTermDebtChange));

        var equityChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("31"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (equityChange != 0)
            financingItems.Add(new CashFlowLineItem("ทุนจดทะเบียน เพิ่มขึ้น/(ลดลง)", "3100", equityChange));

        var financingTotal = financingItems.Sum(i => i.Amount);

        // Cash balances
        var netCashChange = operatingTotal + investingTotal + financingTotal;

        // Opening cash = cash accounts before fromDate
        var cashAccountCodes = new[] { "111", "112" }; // Cash + Bank deposits
        var openingCash = await _db.JournalEntryLines
            .Include(l => l.Account).Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate < fromDate
                && cashAccountCodes.Any(c => l.Account.AccountCode.StartsWith(c)))
            .SumAsync(l => l.DebitAmount - l.CreditAmount);

        return new CashFlowStatementResponse(
            fromDate, toDate,
            new CashFlowSection("กิจกรรมดำเนินงาน", operatingItems, operatingTotal),
            new CashFlowSection("กิจกรรมลงทุน", investingItems, investingTotal),
            new CashFlowSection("กิจกรรมจัดหาเงิน", financingItems, financingTotal),
            netCashChange, openingCash, openingCash + netCashChange);
    }

    // ==================== Fiscal Period ====================

    public async Task<FiscalPeriodResponse> CreateFiscalPeriodAsync(Guid companyId, CreateFiscalPeriodRequest request)
    {
        if (await _db.FiscalPeriods.AnyAsync(f => f.CompanyId == companyId && f.Year == request.Year && f.Month == request.Month))
            throw new InvalidOperationException($"งวดบัญชี {request.Year}/{request.Month} มีอยู่แล้ว");

        var period = new FiscalPeriod
        {
            CompanyId = companyId,
            Name = $"{request.Year}/{request.Month:D2}",
            Year = request.Year,
            Month = request.Month,
            StartDate = request.StartDate,
            EndDate = request.EndDate
        };

        _db.FiscalPeriods.Add(period);
        await _db.SaveChangesAsync();

        return MapPeriodToResponse(period);
    }

    public async Task<List<FiscalPeriodResponse>> GetFiscalPeriodsAsync(Guid companyId)
    {
        var periods = await _db.FiscalPeriods
            .Where(f => f.CompanyId == companyId)
            .OrderByDescending(f => f.Year).ThenByDescending(f => f.Month)
            .ToListAsync();

        return periods.Select(MapPeriodToResponse).ToList();
    }

    public async Task CloseFiscalPeriodAsync(Guid companyId, Guid periodId)
    {
        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f => f.Id == periodId && f.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงวดบัญชี");

        // Check for draft entries
        var hasDrafts = await _db.JournalEntries.AnyAsync(j =>
            j.FiscalPeriodId == periodId && j.Status == JournalEntryStatus.Draft);
        if (hasDrafts)
            throw new InvalidOperationException("ยังมีใบสำคัญที่เป็น Draft อยู่ ต้อง post หรือ void ก่อน");

        period.Status = FiscalPeriodStatus.Closed;
        await _db.SaveChangesAsync();
    }

    // ==================== Helpers ====================

    private static AccountResponse MapAccountToResponse(ChartOfAccount a) => new(
        a.Id, a.AccountCode, a.AccountName, a.AccountNameEn,
        a.AccountType, a.ParentAccountId, a.Level, a.IsActive, a.IsSystemAccount, a.Description);

    private static JournalEntryResponse MapJournalEntryToResponse(JournalEntry j) => new(
        j.Id, j.EntryNumber, j.EntryDate, j.Description, j.Reference,
        j.Status, j.IsAutoGenerated, j.TotalDebit, j.TotalCredit,
        j.Lines.OrderBy(l => l.LineOrder).Select(l => new JournalLineResponse(
            l.Id, l.AccountId, l.Account.AccountCode, l.Account.AccountName,
            l.DebitAmount, l.CreditAmount, l.Description, l.LineOrder)).ToList(),
        j.CreatedAt);

    private static FiscalPeriodResponse MapPeriodToResponse(FiscalPeriod f) => new(
        f.Id, f.Name, f.Year, f.Month, f.StartDate, f.EndDate, f.Status);
}
