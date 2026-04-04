using System.Text.RegularExpressions;
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
        if (string.IsNullOrWhiteSpace(request.AccountCode))
            throw new InvalidOperationException("รหัสบัญชีห้ามว่าง");

        if (!Regex.IsMatch(request.AccountCode, @"^\d{2,}$"))
            throw new InvalidOperationException("รหัสบัญชีต้องเป็นตัวเลขอย่างน้อย 2 หลัก");

        if (string.IsNullOrWhiteSpace(request.AccountName))
            throw new InvalidOperationException("ชื่อบัญชีห้ามว่าง");

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
        // Get company to determine business type and industry type
        var company = await _db.Companies.FindAsync(companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลบริษัท");

        await SeedDefaultAccountsAsync(companyId, company.BusinessType, company.IndustryType);
    }

    public async Task SeedDefaultAccountsAsync(Guid companyId, BusinessType businessType)
    {
        await SeedDefaultAccountsAsync(companyId, businessType, IndustryType.General);
    }

    public async Task SeedDefaultAccountsAsync(Guid companyId, BusinessType businessType, IndustryType industryType)
    {
        // Use raw SQL for deletes to guarantee hard-delete (bypass any EF soft-delete behavior)
        // The unique index IX_ChartOfAccounts_CompanyId_AccountCode has no IsDeleted filter,
        // so we must truly remove rows from the table.

        // Check if any system accounts are in use (have journal entry lines)
        var usedSystemAccountCount = await _db.JournalEntryLines
            .IgnoreQueryFilters()
            .CountAsync(l => _db.ChartOfAccounts
                .IgnoreQueryFilters()
                .Where(a => a.CompanyId == companyId && a.IsSystemAccount)
                .Select(a => a.Id)
                .Contains(l.AccountId));

        if (usedSystemAccountCount > 0)
            throw new InvalidOperationException("ไม่สามารถรีเซ็ตผังบัญชีได้ เนื่องจากมีบัญชีที่ถูกใช้งานแล้ว");

        var templates = ChartOfAccountTemplates.GetTemplateByBusinessType(businessType, industryType);
        var templateCodes = templates.Select(t => t.Code).ToList();

        // Hard-delete system accounts using raw SQL (use {0} placeholders for EF Core)
        await _db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""ChartOfAccounts"" WHERE ""CompanyId"" = {0} AND ""IsSystemAccount"" = true",
            companyId);

        // Hard-delete any soft-deleted accounts whose codes conflict with template
        foreach (var code in templateCodes)
        {
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""ChartOfAccounts"" WHERE ""CompanyId"" = {0} AND ""AccountCode"" = {1} AND ""IsDeleted"" = true",
                companyId, code);
        }

        // Clear the change tracker to avoid stale entities
        _db.ChangeTracker.Clear();

        // Get ALL remaining account codes to skip duplicates
        var existingCodes = await _db.ChartOfAccounts
            .IgnoreQueryFilters()
            .Where(a => a.CompanyId == companyId)
            .Select(a => a.AccountCode)
            .ToListAsync();
        var existingCodeSet = new HashSet<string>(existingCodes, StringComparer.OrdinalIgnoreCase);

        // Pre-load existing accounts for parent lookup
        var codeToId = new Dictionary<string, Guid>();
        var existingAccounts = await _db.ChartOfAccounts
            .IgnoreQueryFilters()
            .Where(a => a.CompanyId == companyId)
            .Select(a => new { a.AccountCode, a.Id })
            .ToListAsync();
        foreach (var ea in existingAccounts)
            codeToId[ea.AccountCode] = ea.Id;

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            foreach (var tpl in templates)
            {
                if (existingCodeSet.Contains(tpl.Code))
                    continue;

                var account = new ChartOfAccount
                {
                    CompanyId = companyId,
                    AccountCode = tpl.Code,
                    AccountName = tpl.NameTh,
                    AccountNameEn = tpl.NameEn,
                    AccountType = tpl.Type,
                    Level = tpl.Level,
                    IsSystemAccount = true,
                    IsActive = true
                };

                string? parentCode = tpl.Level switch
                {
                    2 => tpl.Code[..1],
                    3 => tpl.Code[..2],
                    4 => tpl.Code[..3],
                    _ => null
                };

                if (parentCode != null && codeToId.TryGetValue(parentCode, out var parentId))
                {
                    account.ParentAccountId = parentId;
                }

                _db.ChartOfAccounts.Add(account);
                codeToId[tpl.Code] = account.Id;
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    // ==================== Journal Entries ====================

    public async Task<JournalEntryResponse> CreateJournalEntryAsync(Guid companyId, CreateJournalEntryRequest request, string createdBy)
    {
        // Validate at least 2 lines (double-entry)
        if (request.Lines.Count < 2)
            throw new InvalidOperationException("ต้องมีรายการอย่างน้อย 2 รายการ (ระบบบัญชีคู่)");

        // Validate EntryDate is not in the future
        if (request.EntryDate > DateTime.UtcNow.Date.AddDays(1))
            throw new InvalidOperationException("วันที่ลงบัญชีต้องไม่เป็นวันที่ในอนาคต");

        // Validate each line
        var companyAccountIds = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId)
            .Select(a => a.Id)
            .ToListAsync();

        foreach (var line in request.Lines)
        {
            if (!companyAccountIds.Contains(line.AccountId))
                throw new InvalidOperationException($"ไม่พบบัญชี {line.AccountId} ในผังบัญชีของบริษัท");

            if (line.DebitAmount > 0 && line.CreditAmount > 0)
                throw new InvalidOperationException("แต่ละรายการต้องมียอดเดบิตหรือเครดิตเพียงด้านเดียว");
        }

        // Validate debit = credit
        var totalDebit = request.Lines.Sum(l => l.DebitAmount);
        var totalCredit = request.Lines.Sum(l => l.CreditAmount);
        if (totalDebit != totalCredit)
            throw new InvalidOperationException($"ยอดเดบิต ({totalDebit:N2}) ไม่เท่ากับยอดเครดิต ({totalCredit:N2})");

        if (totalDebit == 0)
            throw new InvalidOperationException("ต้องมียอดเดบิต/เครดิตอย่างน้อย 1 รายการ");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // Generate entry number with journal type prefix
            var prefix = request.JournalType switch
            {
                JournalType.Sales => "SV",
                JournalType.Purchase => "UV",
                JournalType.CashReceipts => "RV",
                JournalType.CashPayments => "PV",
                _ => "JV"
            };
            var count = await _db.JournalEntries.CountAsync(j => j.CompanyId == companyId && j.JournalType == request.JournalType);
            var entryNumber = $"{prefix}-{DateTime.UtcNow:yyyyMM}-{(count + 1):D4}";

            var entry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = request.EntryDate,
                JournalType = request.JournalType,
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
            await transaction.CommitAsync();

            return await GetJournalEntryAsync(companyId, entry.Id);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<JournalEntryResponse> GetJournalEntryAsync(Guid companyId, Guid entryId)
    {
        var entry = await _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        return MapJournalEntryToResponse(entry);
    }

    public async Task<PagedResponse<JournalEntryResponse>> GetJournalEntriesAsync(Guid companyId, PagedRequest request, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, string? journalType = null)
    {
        var query = _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Where(j => j.CompanyId == companyId);

        if (!string.IsNullOrEmpty(journalType) && Enum.TryParse<Models.Enums.JournalType>(journalType, true, out var parsedType))
            query = query.Where(j => j.JournalType == parsedType);

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

        // Validate fiscal period is Open
        if (entry.FiscalPeriodId.HasValue)
        {
            var period = await _db.FiscalPeriods.FindAsync(entry.FiscalPeriodId.Value);
            if (period != null && period.Status == FiscalPeriodStatus.Closed)
                throw new InvalidOperationException("ไม่สามารถ post ได้เนื่องจากงวดบัญชีปิดแล้ว");
        }

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

    // ==================== General Ledger ====================

    public async Task<GeneralLedgerResponse> GetGeneralLedgerAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? accountId = null)
    {
        // Get all posted journal lines in date range
        var query = _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fromDate
                && l.JournalEntry.EntryDate <= toDate);

        if (accountId.HasValue)
            query = query.Where(l => l.AccountId == accountId.Value);

        var lines = await query.OrderBy(l => l.Account.AccountCode)
            .ThenBy(l => l.JournalEntry.EntryDate)
            .ThenBy(l => l.JournalEntry.EntryNumber)
            .ToListAsync();

        // Get opening balances (all posted entries before fromDate)
        var openingQuery = _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate < fromDate);

        if (accountId.HasValue)
            openingQuery = openingQuery.Where(l => l.AccountId == accountId.Value);

        var openingLines = await openingQuery.ToListAsync();

        var openingBalances = openingLines
            .GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g =>
            {
                var acctType = g.First().Account.AccountType;
                var debit = g.Sum(l => l.DebitAmount);
                var credit = g.Sum(l => l.CreditAmount);
                // Debit-normal: Asset, Expense; Credit-normal: Liability, Equity, Revenue
                return (acctType == AccountType.Asset || acctType == AccountType.Expense)
                    ? debit - credit : credit - debit;
            });

        // Group by account
        var grouped = lines.GroupBy(l => new { l.AccountId, l.Account.AccountCode, l.Account.AccountName, l.Account.AccountType });

        var accounts = new List<GeneralLedgerAccount>();
        foreach (var g in grouped.OrderBy(g => g.Key.AccountCode))
        {
            var opening = openingBalances.GetValueOrDefault(g.Key.AccountId, 0);
            var isDebitNormal = g.Key.AccountType == AccountType.Asset || g.Key.AccountType == AccountType.Expense;
            var runningBalance = opening;

            var transactions = new List<GeneralLedgerTransaction>();
            foreach (var l in g.OrderBy(l => l.JournalEntry.EntryDate).ThenBy(l => l.JournalEntry.EntryNumber))
            {
                runningBalance += isDebitNormal ? (l.DebitAmount - l.CreditAmount) : (l.CreditAmount - l.DebitAmount);
                transactions.Add(new GeneralLedgerTransaction(
                    l.JournalEntryId, l.JournalEntry.EntryNumber, l.JournalEntry.EntryDate,
                    l.JournalEntry.JournalType, l.Description ?? l.JournalEntry.Description,
                    l.DebitAmount, l.CreditAmount, runningBalance));
            }

            accounts.Add(new GeneralLedgerAccount(
                g.Key.AccountId, g.Key.AccountCode, g.Key.AccountName, g.Key.AccountType,
                opening, g.Sum(l => l.DebitAmount), g.Sum(l => l.CreditAmount),
                runningBalance, transactions));
        }

        return new GeneralLedgerResponse(accounts, fromDate, toDate);
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

        // Depreciation add-back (non-cash expense) - 56xxx ค่าเสื่อมราคาและค่าตัดจำหน่าย
        var depreciation = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("56"))
            .Sum(l => l.DebitAmount - l.CreditAmount);
        if (depreciation != 0)
            operatingItems.Add(new CashFlowLineItem("ค่าเสื่อมราคา (บวกกลับ)", "56", depreciation));

        // Changes in AR - 113xx ลูกหนี้การค้า
        var arChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("113"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (arChange != 0)
            operatingItems.Add(new CashFlowLineItem("ลูกหนี้การค้า (เพิ่มขึ้น)/ลดลง", "113", arChange));

        // Changes in Inventory - 115xx สินค้าคงเหลือ
        var inventoryChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("115"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (inventoryChange != 0)
            operatingItems.Add(new CashFlowLineItem("สินค้าคงเหลือ (เพิ่มขึ้น)/ลดลง", "115", inventoryChange));

        // Changes in AP - 212xx เจ้าหนี้การค้า
        var apChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("212"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (apChange != 0)
            operatingItems.Add(new CashFlowLineItem("เจ้าหนี้การค้า เพิ่มขึ้น/(ลดลง)", "212", apChange));

        // Tax payable changes - 219xx ภาษีค้างจ่าย
        var taxPayableChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("219"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (taxPayableChange != 0)
            operatingItems.Add(new CashFlowLineItem("ภาษีค้างจ่าย เพิ่มขึ้น/(ลดลง)", "219", taxPayableChange));

        var operatingTotal = operatingItems.Sum(i => i.Amount);

        // Investing Activities: Fixed asset accounts - 122xx ที่ดิน อาคาร อุปกรณ์
        var investingItems = new List<CashFlowLineItem>();
        var fixedAssetChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("122") || l.Account.AccountCode.StartsWith("123"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (fixedAssetChange != 0)
            investingItems.Add(new CashFlowLineItem("ซื้อ/ขาย ที่ดิน อาคาร อุปกรณ์", "122", fixedAssetChange));

        var investingTotal = investingItems.Sum(i => i.Amount);

        // Financing Activities: Long-term liabilities (221xxx) + Equity (31xxx)
        var financingItems = new List<CashFlowLineItem>();
        var longTermDebtChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("22"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (longTermDebtChange != 0)
            financingItems.Add(new CashFlowLineItem("เงินกู้ยืมระยะยาว เพิ่มขึ้น/(ลดลง)", "221", longTermDebtChange));

        var equityChange = postedLines
            .Where(l => l.Account.AccountCode.StartsWith("31"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (equityChange != 0)
            financingItems.Add(new CashFlowLineItem("ทุนจดทะเบียน เพิ่มขึ้น/(ลดลง)", "31", equityChange));

        var financingTotal = financingItems.Sum(i => i.Amount);

        // Cash balances
        var netCashChange = operatingTotal + investingTotal + financingTotal;

        // Opening cash = cash accounts before fromDate (111xxx = เงินสดและเงินฝากธนาคาร)
        var cashAccountCodes = new[] { "111" }; // Cash + Bank deposits (all under 111xxx)
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

        if (period.Status != FiscalPeriodStatus.Open)
            throw new InvalidOperationException("สามารถปิดได้เฉพาะงวดบัญชีที่เปิดอยู่เท่านั้น");

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
        j.Id, j.EntryNumber, j.EntryDate, j.JournalType, j.Description, j.Reference,
        j.Status, j.IsAutoGenerated, j.TotalDebit, j.TotalCredit,
        j.Lines.OrderBy(l => l.LineOrder).Select(l => new JournalLineResponse(
            l.Id, l.AccountId, l.Account.AccountCode, l.Account.AccountName,
            l.DebitAmount, l.CreditAmount, l.Description, l.LineOrder)).ToList(),
        j.CreatedAt);

    private static FiscalPeriodResponse MapPeriodToResponse(FiscalPeriod f) => new(
        f.Id, f.Name, f.Year, f.Month, f.StartDate, f.EndDate, f.Status);
}
