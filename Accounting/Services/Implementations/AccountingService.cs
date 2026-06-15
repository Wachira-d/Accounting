using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class AccountingService : IAccountingService
{
    private readonly AccountingDbContext _db;
    private readonly IBankService? _bankService;
    private readonly ISensitivityService? _sensitivity;

    public AccountingService(AccountingDbContext db, IBankService? bankService = null, ISensitivityService? sensitivity = null)
    {
        _db = db;
        _bankService = bankService;
        _sensitivity = sensitivity;
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
            var parent = await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.Id == request.ParentAccountId.Value && a.CompanyId == companyId);
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
            Description = request.Description,
            InputVatClaimable = request.InputVatClaimable
        };

        _db.ChartOfAccounts.Add(account);
        await _db.SaveChangesAsync();

        return MapAccountToResponse(account);
    }

    public async Task<List<AccountResponse>> GetAccountsAsync(Guid companyId, AccountType? type = null)
    {
        var query = _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId);

        if (type.HasValue)
            query = query.Where(a => a.AccountType == type.Value);

        var accounts = await query
            .OrderBy(a => a.AccountCode)
            .ToListAsync();

        return accounts.Select(MapAccountToResponse).ToList();
    }

    public async Task<List<AccountResponse>> GetPaymentChannelAccountsAsync(Guid companyId)
    {
        var prefixes = new[] { "111", "1133", "2123" };
        var accounts = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive
                && a.Level >= 4
                && prefixes.Any(p => a.AccountCode.StartsWith(p))
                && !a.AccountCode.StartsWith("1112"))
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
        if (request.InputVatClaimable.HasValue) account.InputVatClaimable = request.InputVatClaimable.Value;
        if (request.CostBehavior != null)
            account.CostBehavior = request.CostBehavior == "" ? null : request.CostBehavior;

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

    /// <summary>
    /// Build the chart-of-accounts seed list for a business/industry type by
    /// composing the admin-managed master template (SystemAccountTemplates)
    /// with built-in fallbacks per scope. This is the SINGLE source of truth
    /// shared by company seeding (<see cref="SeedDefaultAccountsAsync"/>) and
    /// the registration preview (<see cref="GetSeedTemplatePreviewAsync"/>) so
    /// the preview can never diverge from what is actually seeded.
    /// </summary>
    private async Task<List<ChartOfAccountTemplates.AccountTemplate>> BuildSeedTemplateAsync(
        BusinessType businessType, IndustryType industryType)
    {
        var master = await _db.SystemAccountTemplates
            .AsNoTracking()
            .Where(t => !t.IsDeleted && t.IsActive)
            .Select(t => new { t.AccountCode, t.AccountNameTh, t.AccountNameEn,
                t.AccountType, t.Level, t.BusinessType, t.IndustryType })
            .ToListAsync();

        var commonRows = master
            .Where(t => t.BusinessType == null && t.IndustryType == null)
            .Select(t => new ChartOfAccountTemplates.AccountTemplate(
                t.AccountCode, t.AccountNameTh, t.AccountNameEn ?? "", t.AccountType, t.Level))
            .ToList();
        var equityRows = master
            .Where(t => t.BusinessType == businessType)
            .Select(t => new ChartOfAccountTemplates.AccountTemplate(
                t.AccountCode, t.AccountNameTh, t.AccountNameEn ?? "", t.AccountType, t.Level))
            .ToList();
        var industryRows = master
            .Where(t => t.IndustryType == industryType)
            .Select(t => new ChartOfAccountTemplates.AccountTemplate(
                t.AccountCode, t.AccountNameTh, t.AccountNameEn ?? "", t.AccountType, t.Level))
            .ToList();

        return ChartOfAccountTemplates.Compose(
            commonRows.Count > 0 ? commonRows : null,
            equityRows.Count > 0 ? equityRows : null,
            industryType == IndustryType.General
                ? new List<ChartOfAccountTemplates.AccountTemplate>()
                : (industryRows.Count > 0 ? industryRows : null),
            businessType, industryType);
    }

    /// <summary>Preview the exact chart of accounts a new company of this
    /// business/industry type would be seeded with — reflects any admin
    /// customisation of the master template.</summary>
    public Task<List<ChartOfAccountTemplates.AccountTemplate>> GetSeedTemplatePreviewAsync(
        BusinessType businessType, IndustryType industryType)
        => BuildSeedTemplateAsync(businessType, industryType);

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

        // Compose the seed list from the admin-managed master Chart of Accounts
        // (same logic the registration preview uses — see BuildSeedTemplateAsync).
        var templates = await BuildSeedTemplateAsync(businessType, industryType);
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
                existingCodeSet.Add(tpl.Code); // Track to prevent duplicate template codes

                var account = new ChartOfAccount
                {
                    CompanyId = companyId,
                    AccountCode = tpl.Code,
                    AccountName = tpl.NameTh,
                    AccountNameEn = tpl.NameEn,
                    AccountType = tpl.Type,
                    Level = tpl.Level,
                    IsSystemAccount = true,
                    IsActive = true,
                    // Entertainment (ค่ารับรอง) input VAT is prohibited — see Rule §82/5.
                    InputVatClaimable = !ChartOfAccountTemplates.IsProhibitedInputVatAccount(tpl.Code)
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

            if (line.DebitAmount < 0 || line.CreditAmount < 0)
                throw new InvalidOperationException("ยอดเดบิตและเครดิตต้องไม่ติดลบ");

            if (line.DebitAmount > 0 && line.CreditAmount > 0)
                throw new InvalidOperationException("แต่ละรายการต้องมียอดเดบิตหรือเครดิตเพียงด้านเดียว");
        }

        // Validate debit = credit
        var totalDebit = request.Lines.Sum(l => l.DebitAmount);
        var totalCredit = request.Lines.Sum(l => l.CreditAmount);
        if (Math.Round(totalDebit, 2, MidpointRounding.AwayFromZero) != Math.Round(totalCredit, 2, MidpointRounding.AwayFromZero))
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
            var entryNumber = await GetNextEntryNumberAsync(companyId, prefix);

            // Resolve source document — accept either Id or DocumentNumber
            Guid? sourceDocId = request.SourceDocumentId;
            string? sourceDocNumber = null;
            if (sourceDocId.HasValue)
            {
                var srcDoc = await _db.Documents
                    .Where(d => d.Id == sourceDocId.Value && d.CompanyId == companyId && !d.IsDeleted)
                    .Select(d => new { d.Id, d.DocumentNumber })
                    .FirstOrDefaultAsync();
                if (srcDoc == null)
                    throw new InvalidOperationException("ไม่พบเอกสารต้นทางที่ระบุในบริษัทนี้");
                sourceDocNumber = srcDoc.DocumentNumber;
            }
            else if (!string.IsNullOrWhiteSpace(request.SourceDocumentNumber))
            {
                var srcDoc = await _db.Documents
                    .Where(d => d.DocumentNumber == request.SourceDocumentNumber && d.CompanyId == companyId && !d.IsDeleted)
                    .Select(d => new { d.Id, d.DocumentNumber })
                    .FirstOrDefaultAsync();
                if (srcDoc == null)
                    throw new InvalidOperationException($"ไม่พบเอกสารหมายเลข {request.SourceDocumentNumber} ในบริษัทนี้");
                sourceDocId = srcDoc.Id;
                sourceDocNumber = srcDoc.DocumentNumber;
            }

            // Reference fallback: if not provided but linked to a source document, default to its number
            var reference = request.Reference;
            if (string.IsNullOrWhiteSpace(reference) && !string.IsNullOrWhiteSpace(sourceDocNumber))
                reference = sourceDocNumber;

            // Re-sync support: reverse existing posted journals linked to this source document
            // before creating a new one. This enables idempotent re-syncs from external systems.
            if (request.ReplaceExistingForSource && sourceDocId.HasValue)
            {
                var existing = await _db.JournalEntries
                    .Where(j => j.CompanyId == companyId
                             && j.SourceDocumentId == sourceDocId.Value
                             && j.Status == JournalEntryStatus.Posted)
                    .ToListAsync();
                foreach (var old in existing)
                {
                    await ReverseJournalEntryAsync(companyId, old.Id,
                        reversalDate: request.EntryDate,
                        description: $"Re-sync reversal: {old.EntryNumber}",
                        systemTriggered: true);
                }
            }

            var entry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = NormalizeDate(request.EntryDate),
                JournalType = request.JournalType,
                Description = request.Description,
                Reference = reference,
                Status = JournalEntryStatus.Posted,
                TotalDebit = totalDebit,
                TotalCredit = totalCredit,
                CreatedBy = createdBy,
                ProjectId = request.ProjectId,
                BranchId = request.BranchId,
                DimensionId = request.DimensionId,
                Note = request.Note,
                Tags = request.Tags,
                SourceDocumentId = sourceDocId
            };

            // Validate project belongs to company (if provided)
            if (request.ProjectId.HasValue)
            {
                var projOk = await _db.Projects.AnyAsync(p => p.Id == request.ProjectId.Value && p.CompanyId == companyId);
                if (!projOk)
                    throw new InvalidOperationException("ไม่พบโครงการที่ระบุในบริษัทนี้");
            }

            // Find fiscal period — reject if closed
            var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
                f.CompanyId == companyId &&
                f.StartDate <= request.EntryDate &&
                f.EndDate >= request.EntryDate);
            if (period != null && period.Status != FiscalPeriodStatus.Open)
                throw new InvalidOperationException(
                    $"ไม่สามารถบันทึกรายการในงวด {period.Name} ได้ เนื่องจากงวดถูกปิดแล้ว");
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
                    LineOrder = order++,
                    ProjectId = line.ProjectId ?? request.ProjectId,
                    BranchId = line.BranchId ?? request.BranchId,
                    DimensionId = line.DimensionId ?? request.DimensionId,
                    Tags = line.Tags
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

    public async Task<JournalEntryResponse> GetJournalEntryForUserAsync(Guid companyId, Guid entryId, Guid userId)
    {
        var full = await GetJournalEntryAsync(companyId, entryId);
        if (full.Sensitivity == SensitivityKind.None || _sensitivity == null) return full;
        var allowed = await _sensitivity.CanViewAsync(companyId, userId, full.Sensitivity);
        if (allowed) return full;
        return RedactJournalEntry(full, SensitivityReason(full.Sensitivity));
    }

    public async Task<PagedResponse<JournalEntryResponse>> GetJournalEntriesForUserAsync(Guid companyId, Guid userId, PagedRequest request, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, string? journalType = null, Guid? dimensionId = null, Guid? branchId = null, Guid? projectId = null, string? tag = null, Guid? sourceDocumentId = null, string? sourceDocumentNumber = null)
    {
        var page = await GetJournalEntriesAsync(companyId, request, status, fromDate, toDate, journalType, dimensionId, branchId, projectId, tag, sourceDocumentId, sourceDocumentNumber);
        if (_sensitivity == null) return page;
        var visible = await _sensitivity.GetVisibleKindsAsync(companyId, userId);
        var items = page.Items.Select(it =>
            it.Sensitivity == SensitivityKind.None || visible.Contains(it.Sensitivity)
                ? it
                : RedactJournalEntry(it, SensitivityReason(it.Sensitivity))
        ).ToList();
        return new PagedResponse<JournalEntryResponse>(items, page.TotalCount, page.Page, page.PageSize, page.TotalPages);
    }

    private static string SensitivityReason(SensitivityKind kind) => kind switch
    {
        SensitivityKind.Payroll      => "ต้องมีสิทธิ์ดูข้อมูลเงินเดือน",
        SensitivityKind.ExecutivePay => "ต้องมีสิทธิ์ดูข้อมูลค่าตอบแทนผู้บริหาร",
        SensitivityKind.HrPersonal   => "ต้องมีสิทธิ์ดูข้อมูลบุคลากร",
        SensitivityKind.Confidential => "ต้องมีสิทธิ์ดูเอกสารลับ",
        _                            => "ต้องมีสิทธิ์เพิ่มเติม"
    };

    public async Task<JournalEntryResponse> GetJournalEntryAsync(Guid companyId, Guid entryId)
    {
        var entry = await _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Include(j => j.Project)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        var srcMap = await LoadSourceDocumentMapAsync(new[] { entry });
        string? srcNumber = null, srcType = null;
        if (entry.SourceDocumentId.HasValue && srcMap.TryGetValue(entry.SourceDocumentId.Value, out var info))
        { srcNumber = info.Number; srcType = info.Type; }
        return MapJournalEntryToResponse(entry, srcNumber, srcType);
    }

    public async Task<PagedResponse<JournalEntryResponse>> GetJournalEntriesAsync(Guid companyId, PagedRequest request, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, string? journalType = null, Guid? dimensionId = null, Guid? branchId = null, Guid? projectId = null, string? tag = null, Guid? sourceDocumentId = null, string? sourceDocumentNumber = null)
    {
        var query = _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Include(j => j.Project)
            .Where(j => j.CompanyId == companyId);

        if (projectId.HasValue)
            query = query.Where(j => j.ProjectId == projectId.Value || j.Lines.Any(l => l.ProjectId == projectId.Value));

        if (!string.IsNullOrWhiteSpace(tag))
            query = query.Where(j => (j.Tags != null && j.Tags.Contains(tag)) || j.Lines.Any(l => l.Tags != null && l.Tags.Contains(tag)));

        if (sourceDocumentId.HasValue)
            query = query.Where(j => j.SourceDocumentId == sourceDocumentId.Value);

        if (!string.IsNullOrWhiteSpace(sourceDocumentNumber))
        {
            var docIds = _db.Documents
                .Where(d => d.CompanyId == companyId && d.DocumentNumber == sourceDocumentNumber)
                .Select(d => d.Id);
            query = query.Where(j => j.SourceDocumentId.HasValue && docIds.Contains(j.SourceDocumentId.Value));
        }

        if (!string.IsNullOrEmpty(journalType) && Enum.TryParse<Models.Enums.JournalType>(journalType, true, out var parsedType))
            query = query.Where(j => j.JournalType == parsedType);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(j => j.EntryNumber.Contains(request.Search) || (j.Description != null && j.Description.Contains(request.Search)));

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<Models.Enums.JournalEntryStatus>(status, true, out var parsedStatus))
            query = query.Where(j => j.Status == parsedStatus);

        if (fromDate.HasValue)
            query = query.Where(j => j.EntryDate >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(j => j.EntryDate < toDate.Value.Date.AddDays(1));

        if (dimensionId.HasValue)
        {
            var dimLineIds = _db.Set<JournalLineDimension>()
                .Where(d => d.DimensionId == dimensionId.Value)
                .Select(d => d.JournalEntryLineId);
            var dimEntryIds = _db.JournalEntryLines
                .Where(l => dimLineIds.Contains(l.Id))
                .Select(l => l.JournalEntryId);
            query = query.Where(j => dimEntryIds.Contains(j.Id));
        }

        if (branchId.HasValue)
        {
            var branchDimId = await _db.Set<Branch>()
                .Where(b => b.Id == branchId.Value && b.CompanyId == companyId)
                .Select(b => b.DimensionId)
                .FirstOrDefaultAsync();
            if (branchDimId.HasValue)
            {
                var bLineIds = _db.Set<JournalLineDimension>()
                    .Where(d => d.DimensionId == branchDimId.Value)
                    .Select(d => d.JournalEntryLineId);
                var bEntryIds = _db.JournalEntryLines
                    .Where(l => bLineIds.Contains(l.Id))
                    .Select(l => l.JournalEntryId);
                query = query.Where(j => bEntryIds.Contains(j.Id));
            }
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(j => j.EntryDate)
            .ThenByDescending(j => j.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var srcMap = await LoadSourceDocumentMapAsync(items);
        return new PagedResponse<JournalEntryResponse>(
            items.Select(j =>
            {
                string? n = null, t = null;
                if (j.SourceDocumentId.HasValue && srcMap.TryGetValue(j.SourceDocumentId.Value, out var info))
                { n = info.Number; t = info.Type; }
                return MapJournalEntryToResponse(j, n, t);
            }).ToList(),
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

    /// <summary>
    /// แก้ไขใบสำคัญ — รองรับ scalar fields (วันที่/รายละเอียด/อ้างอิง/มิติ) และ
    /// การแทนที่บรรทัดทั้งชุดเมื่อส่ง Lines มา. นโยบาย operator-override:
    /// แก้ได้ทุกสถานะ (รวม Posted) เพื่อรองรับการตามแก้ตัวเลข/ผังบัญชีที่บันทึกผิด.
    /// ยังคงห้ามแก้รายการที่สร้างอัตโนมัติจากเอกสารต้นทาง (IsAutoGenerated) และ
    /// งวดบัญชีต้องเปิดอยู่. ผู้ที่ต้องการ audit trail แบบเต็มให้ใช้ Correct
    /// (สร้าง reversal + draft ใหม่) แทน.
    /// </summary>
    public async Task<JournalEntryResponse> UpdateJournalEntryAsync(
        Guid companyId, Guid entryId, UpdateJournalEntryRequest request, string updatedBy)
    {
        var entry = await _db.JournalEntries
            .Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        if (entry.IsAutoGenerated)
            throw new InvalidOperationException(
                "รายการนี้สร้างอัตโนมัติจากเอกสารต้นทาง — แก้ที่เอกสารแทน");

        // Also block any JE that's still linked to a source document, even if
        // IsAutoGenerated wasn't set (legacy data from before that flag was
        // populated). The document's lifecycle assumes its JE mirrors it;
        // letting the operator hot-edit the JE diverges the two.
        if (entry.SourceDocumentId.HasValue)
            throw new InvalidOperationException(
                "รายการนี้ผูกกับเอกสารต้นทาง — กรุณาแก้ไขที่เอกสารแทน " +
                "เพื่อให้สถานะของเอกสารกับบัญชีตรงกันเสมอ");

        await ValidateFiscalPeriodOpenAsync(entry.FiscalPeriodId);

        // Scalar updates (only when caller actually sent the field).
        if (request.EntryDate.HasValue)
        {
            if (request.EntryDate.Value > DateTime.UtcNow.Date.AddDays(1))
                throw new InvalidOperationException("วันที่ลงบัญชีต้องไม่เป็นวันที่ในอนาคต");
            entry.EntryDate = NormalizeDate(request.EntryDate.Value);
        }
        if (request.Description != null) entry.Description = request.Description;
        if (request.Reference != null) entry.Reference = request.Reference;
        if (request.JournalType.HasValue) entry.JournalType = request.JournalType.Value;
        if (request.ProjectId.HasValue) entry.ProjectId = request.ProjectId;
        if (request.BranchId.HasValue) entry.BranchId = request.BranchId;
        if (request.DimensionId.HasValue) entry.DimensionId = request.DimensionId;
        if (request.Note != null) entry.Note = request.Note;
        if (request.Tags != null) entry.Tags = request.Tags;
        entry.UpdatedAt = DateTime.UtcNow;
        entry.UpdatedBy = updatedBy;

        // Optional full-replacement of lines (numbers / accounts / per-line dim).
        if (request.Lines != null)
        {
            if (request.Lines.Count < 2)
                throw new InvalidOperationException("ต้องมีรายการอย่างน้อย 2 รายการ (ระบบบัญชีคู่)");

            var companyAccountIds = await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId)
                .Select(a => a.Id)
                .ToListAsync();

            foreach (var l in request.Lines)
            {
                if (!companyAccountIds.Contains(l.AccountId))
                    throw new InvalidOperationException($"ไม่พบบัญชี {l.AccountId} ในผังบัญชีของบริษัท");
                if (l.DebitAmount < 0 || l.CreditAmount < 0)
                    throw new InvalidOperationException("ยอดเดบิตและเครดิตต้องไม่ติดลบ");
                if (l.DebitAmount > 0 && l.CreditAmount > 0)
                    throw new InvalidOperationException("แต่ละรายการต้องมียอดเดบิตหรือเครดิตเพียงด้านเดียว");
            }
            var totalDebit = request.Lines.Sum(l => l.DebitAmount);
            var totalCredit = request.Lines.Sum(l => l.CreditAmount);
            if (Math.Round(totalDebit, 2, MidpointRounding.AwayFromZero) != Math.Round(totalCredit, 2, MidpointRounding.AwayFromZero))
                throw new InvalidOperationException(
                    $"ยอดเดบิต ({totalDebit:N2}) ไม่เท่ากับยอดเครดิต ({totalCredit:N2})");
            if (totalDebit == 0)
                throw new InvalidOperationException("ต้องมียอดเดบิต/เครดิตอย่างน้อย 1 รายการ");

            // Hard-replace lines: remove dimension allocations first, then the
            // line rows, then re-insert from the request.
            var oldLineIds = entry.Lines.Select(l => l.Id).ToList();
            var dims = await _db.JournalLineDimensions
                .Where(d => oldLineIds.Contains(d.JournalEntryLineId))
                .ToListAsync();
            _db.JournalLineDimensions.RemoveRange(dims);
            _db.JournalEntryLines.RemoveRange(entry.Lines);

            var order = 1;
            foreach (var l in request.Lines)
            {
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    JournalEntryId = entry.Id,
                    AccountId = l.AccountId,
                    DebitAmount = l.DebitAmount,
                    CreditAmount = l.CreditAmount,
                    Description = l.Description,
                    LineOrder = order++,
                    ProjectId = l.ProjectId ?? entry.ProjectId,
                    BranchId = l.BranchId ?? entry.BranchId,
                    DimensionId = l.DimensionId ?? entry.DimensionId,
                    Tags = l.Tags
                });
            }
            entry.TotalDebit = totalDebit;
            entry.TotalCredit = totalCredit;
        }

        await _db.SaveChangesAsync();
        return await GetJournalEntryAsync(companyId, entry.Id);
    }

    /// <summary>
    /// ยกเลิกใบสำคัญ — สำหรับ Draft จะตั้ง Status=Voided เฉยๆ
    /// สำหรับ Posted จะสร้าง reversal entry อัตโนมัติ (Dr↔Cr) แล้ว set Status=Reversed
    /// ตามมาตรฐานบัญชีไทย — ห้ามแก้ไข/ลบรายการที่ผ่านเข้าบัญชีแล้ว ต้องสร้าง counter-entry
    /// </summary>
    public async Task VoidJournalEntryAsync(Guid companyId, Guid entryId)
    {
        var entry = await _db.JournalEntries.FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        if (entry.Status == JournalEntryStatus.Voided || entry.Status == JournalEntryStatus.Reversed)
            throw new InvalidOperationException("รายการนี้ถูกยกเลิก/กลับรายการไปแล้ว");

        // Mirror the user-direct guards on Reverse — otherwise users could
        // bypass them via Void→Reverse cascade (Void hands systemTriggered=true
        // to Reverse to avoid blocking legitimate internal flows, but the same
        // policy must be enforced here at the user entry point).
        if (entry.OriginalEntryId.HasValue)
            throw new InvalidOperationException(
                "ไม่สามารถยกเลิก 'ตัวกลับ' (REV) ได้ — " +
                "หากต้องการให้กลับมาเหมือนเดิม กรุณาสร้างใบสำคัญปรับปรุงใหม่");

        if (entry.SourceDocumentId.HasValue)
            throw new InvalidOperationException(
                "รายการนี้สร้างจากเอกสารต้นทาง — กรุณาใช้คำสั่ง 'ยกเลิกเอกสาร' " +
                "เพื่อยกเลิกทั้งเอกสารและรายการบัญชีพร้อมกัน");

        await ValidateFiscalPeriodOpenAsync(entry.FiscalPeriodId);

        if (entry.Status == JournalEntryStatus.Posted)
        {
            // Posted JE → must create reversal entry (proper double-entry accounting).
            // systemTriggered=true so the doc-source / reversal-of-reversal guards
            // don't block this internal Void→Reverse cascade — the equivalent
            // guards above already rejected forbidden inputs at the public entry.
            await ReverseJournalEntryAsync(companyId, entryId,
                reversalDate: DateTime.UtcNow.Date,
                description: $"ยกเลิกใบสำคัญ {entry.EntryNumber}",
                systemTriggered: true);
            return;
        }

        // Draft / unposted → simple status flip is fine; no GL impact yet
        entry.Status = JournalEntryStatus.Voided;
        entry.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// ลบใบสำคัญ — soft-delete ใช้ได้ทุกสถานะ (Draft/Posted/Voided/Reversed)
    /// ตามนโยบาย operator-override; ตัว Audit/GL ก็ filter ออกด้วย IsDeleted
    /// อยู่แล้ว. ยังคงห้ามลบรายการที่สร้างอัตโนมัติจากเอกสาร — เพราะเอกสาร
    /// ต้นทางจะ pointing ไปหา ghost JE; ให้ไปลบที่เอกสารแทน. ปิดงวดบัญชี
    /// (FiscalPeriod = Closed) ยังกั้นอยู่เพื่อรักษาความถูกต้องของรายงานภาษี.
    /// ผู้ที่อยากให้ trail ครบให้ใช้ Reverse / Correct.
    /// </summary>
    public async Task DeleteJournalEntryAsync(Guid companyId, Guid entryId)
    {
        var entry = await _db.JournalEntries
            .Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        if (entry.SourceDocumentId.HasValue)
            throw new InvalidOperationException("ไม่สามารถลบรายการที่สร้างจากเอกสาร ให้ลบที่เอกสารต้นทางแทน");

        await ValidateFiscalPeriodOpenAsync(entry.FiscalPeriodId);

        // Soft-delete dimension allocations for each line
        var lineIds = entry.Lines.Select(l => l.Id).ToList();
        var dims = await _db.JournalLineDimensions
            .Where(d => lineIds.Contains(d.JournalEntryLineId))
            .ToListAsync();
        foreach (var d in dims)
        {
            d.IsDeleted = true;
            d.UpdatedAt = DateTime.UtcNow;
        }

        // Soft-delete lines
        foreach (var line in entry.Lines)
        {
            line.IsDeleted = true;
            line.UpdatedAt = DateTime.UtcNow;
        }

        // Soft-delete the entry itself
        entry.IsDeleted = true;
        entry.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// กลับรายการ — สร้างใบสำคัญใหม่ที่มีลายมือ Dr↔Cr สลับด้านจากใบเดิม. รายการ
    /// เดิมไม่ถูกแก้ไข ยกเว้นการตั้ง Status=Reversed และ link ReversedByEntryId
    /// เพื่อรักษา audit trail (immutable original record) ตามมาตรฐาน TFRS/TAS
    /// และมาตรา 86/3 ประมวลรัษฎากร.
    ///
    /// systemTriggered: true เมื่อถูกเรียกจาก flow ภายในระบบ
    /// (VoidDocument → VoidPayment / Re-sync / Void JE / Correct cascade) —
    /// bypass guards ที่กันการกลับรายการจากเอกสารต้นทาง/ตัวกลับซ้ำ
    /// (user-direct path ใช้ default false).
    /// </summary>
    public async Task<JournalEntryResponse> ReverseJournalEntryAsync(Guid companyId, Guid entryId, DateTime? reversalDate = null, string? description = null, bool systemTriggered = false)
    {
        var original = await _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        if (original.Status != JournalEntryStatus.Posted)
            throw new InvalidOperationException("สามารถกลับรายการได้เฉพาะใบสำคัญที่มีสถานะ Posted เท่านั้น");

        if (original.ReversedByEntryId.HasValue)
            throw new InvalidOperationException("รายการนี้ถูกกลับรายการไปแล้ว");

        // FIX #3: ห้ามผู้ใช้กดกลับ "ตัวกลับ" ซ้ำ — ใบที่มี OriginalEntryId คือ
        // reversal entry; การกลับซ้ำเท่ากับใส่ amounts กลับมาเหมือนเดิม ทำให้
        // audit trail สับสน. ถ้าต้องการ "undo การกลับ" ให้สร้างใบปรับปรุงใหม่
        // ระบบภายในยังทำได้ (เช่น re-sync cascade) เพื่อไม่ให้ flow ภายในตัน.
        if (original.OriginalEntryId.HasValue && !systemTriggered)
            throw new InvalidOperationException(
                "ไม่สามารถกลับรายการของใบสำคัญที่เป็น 'ตัวกลับ' (REV) ได้ — " +
                "หากต้องการให้กลับมาเหมือนเดิม กรุณาสร้างใบสำคัญปรับปรุงใหม่");

        // FIX #5: ห้ามผู้ใช้กดกลับ JE ที่ผูกกับเอกสารต้นทาง — เพื่อให้สถานะ
        // ของเอกสารและ GL สอดคล้องกันเสมอ. ผู้ใช้ต้องไปยกเลิกเอกสารแทน
        // (Document.VoidAsync จะเรียก reverse ตัวนี้แบบ systemTriggered=true
        // พร้อมเก็บ cascade ต่างๆ ให้).
        if (original.SourceDocumentId.HasValue && !systemTriggered)
            throw new InvalidOperationException(
                "รายการนี้สร้างจากเอกสารต้นทาง — กรุณาใช้คำสั่ง 'ยกเลิกเอกสาร' " +
                "เพื่อกลับทั้งเอกสารและรายการบัญชีพร้อมกัน");

        // FIX #1: ไม่ check งวดของ original — TAS 8 อนุญาตให้กลับรายการของงวด
        // ปิดได้ ถ้าตัวกลับลงในงวดปัจจุบันที่ยังเปิดอยู่ (เจอผิดของปี 2024 ใน
        // ปี 2025 → ลง reversal ใน 2025). การตรวจงวดของ effectiveDate ยังคง
        // อยู่ใน flow ด้านล่าง.

        var effectiveDate = NormalizeDate(reversalDate ?? DateTime.UtcNow.Date);

        var prefix = original.JournalType switch
        {
            JournalType.Sales => "SV",
            JournalType.Purchase => "UV",
            JournalType.CashReceipts => "RV",
            JournalType.CashPayments => "PV",
            _ => "JV"
        };
        var entryNumber = await GetNextEntryNumberAsync(companyId, prefix);

        var existingTransaction = _db.Database.CurrentTransaction;
        var transaction = existingTransaction == null
            ? await _db.Database.BeginTransactionAsync()
            : null;
        try
        {
            var reversal = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = effectiveDate,
                JournalType = original.JournalType,
                Description = description ?? $"กลับรายการ {original.EntryNumber}: {original.Description}",
                Reference = original.Reference,
                Status = JournalEntryStatus.Posted,
                // IsAutoGenerated flags "เป็นส่วนหนึ่งของ document chain หรือไม่"
                // — ไม่ใช่ "ใครคลิก". สืบทอดจาก original ให้ตรง: ถ้า original
                // มาจากเอกสาร reversal ก็เป็นส่วนของเอกสารเดียวกัน, ถ้า original
                // เป็น manual reversal ที่ถูกกดเองก็ manual. (Direct-user reverse
                // ของ doc-sourced JE ถูกบล็อกที่ guard ด้านบนแล้ว เคสที่ user
                // คลิกแต่ถูก mark API ที่เคยกังวลจึงเกิดไม่ได้.)
                IsAutoGenerated = original.IsAutoGenerated,
                OriginalEntryId = original.Id,
                // SourceDocumentId คงไว้สำหรับ trace — query ตามเอกสารต้นทาง
                // จะเห็น JE ทั้งคู่ (original Reversed + reversal Posted) ที่ net = 0
                SourceDocumentId = original.SourceDocumentId,
                ProjectId = original.ProjectId,
                BranchId = original.BranchId,
                DimensionId = original.DimensionId,
                Note = original.Note,
                Tags = original.Tags,
                TotalDebit = original.TotalCredit,
                TotalCredit = original.TotalDebit
            };

            var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
                f.CompanyId == companyId &&
                f.StartDate <= effectiveDate &&
                f.EndDate >= effectiveDate);
            if (period != null && period.Status != FiscalPeriodStatus.Open)
                throw new InvalidOperationException(
                    $"ไม่สามารถกลับรายการในงวด {period.Name} ได้ เนื่องจากงวดถูกปิดแล้ว");
            if (period != null)
                reversal.FiscalPeriodId = period.Id;

            _db.JournalEntries.Add(reversal);

            // Create reversed lines: swap debit <-> credit + copy dimensions
            var originalLineIds = original.Lines.Select(l => l.Id).ToList();
            var originalDims = await _db.JournalLineDimensions
                .Where(d => originalLineIds.Contains(d.JournalEntryLineId))
                .ToListAsync();

            int order = 1;
            foreach (var line in original.Lines.OrderBy(l => l.LineOrder))
            {
                var newLine = new JournalEntryLine
                {
                    JournalEntryId = reversal.Id,
                    AccountId = line.AccountId,
                    DebitAmount = line.CreditAmount,
                    CreditAmount = line.DebitAmount,
                    Description = line.Description,
                    LineOrder = order++,
                    // Preserve line-level traceability
                    ProjectId = line.ProjectId,
                    BranchId = line.BranchId,
                    DimensionId = line.DimensionId,
                    Tags = line.Tags
                };
                _db.JournalEntryLines.Add(newLine);

                // Copy dimension allocations from original line
                foreach (var dim in originalDims.Where(d => d.JournalEntryLineId == line.Id))
                {
                    _db.JournalLineDimensions.Add(new JournalLineDimension
                    {
                        CompanyId = companyId,
                        JournalEntryLineId = newLine.Id,
                        DimensionId = dim.DimensionId,
                        AllocatedAmount = dim.AllocatedAmount,
                        AllocatedPercent = dim.AllocatedPercent
                    });
                }
            }

            // FIX #4: ตัดความเชื่อมโยง bank reconciliation ที่ชี้มาที่ original.
            // ถ้าไม่ทำ รายงาน reconciliation จะแสดง "matched" กับ JE ที่ถูกกลับไป
            // แล้ว (Reversed) ผลคือยอดเงินสดในรายงานไม่ตรงกับยอดในธนาคารจริง.
            // ผู้ใช้ต้อง re-match กับ reversal เอง (หรือกับ payment ใหม่) — ระบบ
            // เลือก unmatch แทน auto-rematch เพราะ semantic ของ rematch ขึ้นกับ
            // case (อาจเป็น payment ใหม่ที่กำลังจะลงแทน).
            var linkedBankTxns = await _db.Set<BankTransaction>()
                .Where(t => t.CompanyId == companyId
                    && t.MatchedJournalEntryId == original.Id
                    && !t.IsDeleted)
                .ToListAsync();
            foreach (var t in linkedBankTxns)
            {
                t.MatchedJournalEntryId = null;
                t.ReconciliationStatus = ReconciliationStatus.Unmatched;
                t.ReconciledAt = null;
                t.ReconciledBy = null;
                t.MatchedEntryIdsJson = null;
                t.UpdatedAt = DateTime.UtcNow;
            }

            original.Status = JournalEntryStatus.Reversed;
            original.ReversedByEntryId = reversal.Id;
            original.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            if (transaction != null) await transaction.CommitAsync();

            // Cascade-unwind any ReconciliationGroup that contained the original JE.
            // The legacy 1:1 / M:1 bank-txn unmatch above only covers
            // BankTransaction.MatchedJournalEntryId; M:N group membership lives in
            // ReconciliationGroupItem and would otherwise leave stale bank txns
            // pointing at a Reversed JE inside a now-imbalanced group.
            if (_bankService != null)
            {
                try { await _bankService.UnwindGroupsContainingItemAsync(companyId, ReconciliationItemType.JournalEntry, original.Id); }
                catch { /* best-effort — bank cleanup never breaks the reverse */ }
            }

            return await GetJournalEntryAsync(companyId, reversal.Id);
        }
        catch
        {
            if (transaction != null) await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            if (transaction != null) await transaction.DisposeAsync();
        }
    }

    public async Task<CorrectJournalEntryResponse> CorrectJournalEntryAsync(Guid companyId, Guid entryId, string createdBy)
    {
        var original = await _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        if (original.Status != JournalEntryStatus.Posted)
            throw new InvalidOperationException("สามารถแก้ไขด้วยการกลับรายการได้เฉพาะใบที่ Posted แล้วเท่านั้น");

        if (original.ReversedByEntryId.HasValue)
            throw new InvalidOperationException("รายการนี้ถูกกลับรายการไปแล้ว");

        // Step 1: Reverse the original entry
        var reversalEntry = await ReverseJournalEntryAsync(companyId, entryId, null,
            $"แก้ไข (กลับรายการ) {original.EntryNumber}");

        // Step 2: Create a new Draft clone with same data for user to correct
        var cloneRequest = new CreateJournalEntryRequest(
            EntryDate: original.EntryDate,
            Description: $"แก้ไขจาก {original.EntryNumber}: {original.Description}",
            Reference: original.Reference,
            Lines: original.Lines.OrderBy(l => l.LineOrder).Select(l => new JournalLineRequest(
                AccountId: l.AccountId,
                DebitAmount: l.DebitAmount,
                CreditAmount: l.CreditAmount,
                Description: l.Description,
                ProjectId: l.ProjectId,
                BranchId: l.BranchId,
                DimensionId: l.DimensionId,
                Tags: l.Tags
            )).ToList(),
            JournalType: original.JournalType,
            ProjectId: original.ProjectId,
            BranchId: original.BranchId,
            DimensionId: original.DimensionId,
            Note: original.Note,
            Tags: original.Tags,
            SourceDocumentId: original.SourceDocumentId,
            SourceDocumentNumber: null,
            ReplaceExistingForSource: false
        );

        var draftEntry = await CreateJournalEntryAsync(companyId, cloneRequest, createdBy);

        return new CorrectJournalEntryResponse(reversalEntry, draftEntry);
    }

    public async Task<int> BatchVoidJournalEntriesAsync(Guid companyId, List<Guid> entryIds)
    {
        // Mirror VoidJournalEntryAsync's user-direct guards. Silently skipping
        // forbidden rows would hide problems from the operator; refuse the
        // entire batch with a summary so they know exactly what to fix.
        var allRequested = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && entryIds.Contains(j.Id))
            .Select(j => new { j.Id, j.EntryNumber, j.OriginalEntryId, j.SourceDocumentId })
            .ToListAsync();
        var reversals = allRequested.Where(j => j.OriginalEntryId.HasValue).ToList();
        if (reversals.Count > 0)
            throw new InvalidOperationException(
                "ไม่สามารถยกเลิก 'ตัวกลับ' (REV) ได้: " +
                string.Join(", ", reversals.Take(5).Select(j => j.EntryNumber)) +
                (reversals.Count > 5 ? $" และอีก {reversals.Count - 5} รายการ" : ""));
        var docSourced = allRequested.Where(j => j.SourceDocumentId.HasValue).ToList();
        if (docSourced.Count > 0)
            throw new InvalidOperationException(
                "รายการที่สร้างจากเอกสารต้นทางต้องใช้ 'ยกเลิกเอกสาร' แทน: " +
                string.Join(", ", docSourced.Take(5).Select(j => j.EntryNumber)) +
                (docSourced.Count > 5 ? $" และอีก {docSourced.Count - 5} รายการ" : ""));

        var entries = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && entryIds.Contains(j.Id) && j.Status != JournalEntryStatus.Voided)
            .ToListAsync();

        foreach (var entry in entries)
        {
            if (entry.Status == JournalEntryStatus.Posted)
            {
                await ReverseJournalEntryAsync(companyId, entry.Id,
                    reversalDate: DateTime.UtcNow.Date,
                    description: $"Batch void: {entry.EntryNumber}",
                    systemTriggered: true);
            }
            else
            {
                entry.Status = JournalEntryStatus.Voided;
                entry.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();
        return entries.Count;
    }

    public async Task<int> BatchDeleteJournalEntriesAsync(Guid companyId, List<Guid> entryIds)
    {
        // Mirrors DeleteJournalEntryAsync: any status, but not document-sourced.
        var entries = await _db.JournalEntries
            .Include(j => j.Lines)
            .Where(j => j.CompanyId == companyId && entryIds.Contains(j.Id)
                && !j.SourceDocumentId.HasValue)
            .ToListAsync();

        var allLineIds = entries.SelectMany(e => e.Lines.Select(l => l.Id)).ToList();
        var dims = await _db.JournalLineDimensions
            .Where(d => allLineIds.Contains(d.JournalEntryLineId))
            .ToListAsync();
        foreach (var d in dims)
        {
            d.IsDeleted = true;
            d.UpdatedAt = DateTime.UtcNow;
        }

        foreach (var entry in entries)
        {
            foreach (var line in entry.Lines)
            {
                line.IsDeleted = true;
                line.UpdatedAt = DateTime.UtcNow;
            }
            entry.IsDeleted = true;
            entry.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return entries.Count;
    }

    public async Task<int> BatchPostJournalEntriesAsync(Guid companyId)
    {
        var entries = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && j.Status == JournalEntryStatus.Draft)
            .ToListAsync();

        var closedPeriodIds = await _db.FiscalPeriods
            .Where(f => f.CompanyId == companyId && f.Status != FiscalPeriodStatus.Open)
            .Select(f => f.Id)
            .ToListAsync();

        foreach (var entry in entries)
        {
            if (entry.FiscalPeriodId.HasValue && closedPeriodIds.Contains(entry.FiscalPeriodId.Value))
                continue;
            entry.Status = JournalEntryStatus.Posted;
            entry.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return entries.Count(e => e.Status == JournalEntryStatus.Posted);
    }

    // ==================== General Ledger ====================

    public async Task<GeneralLedgerResponse> GetGeneralLedgerAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? accountId = null, Guid? dimensionId = null, Guid? branchId = null, Guid? projectId = null)
    {
        var fromDateStart = fromDate.Date;
        var toDateEnd = toDate.Date.AddDays(1);

        Guid? effectiveDimId = dimensionId;
        if (branchId.HasValue && !effectiveDimId.HasValue)
        {
            effectiveDimId = await _db.Set<Branch>()
                .Where(b => b.Id == branchId.Value && b.CompanyId == companyId)
                .Select(b => b.DimensionId)
                .FirstOrDefaultAsync();
        }

        // Query posted + reversed entry IDs. Reversed entries are historically valid
        // transactions that were later offset by a reversal JE — both must appear in
        // the ledger for the running balance to net to zero after a void.
        var postedEntryIds = _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                && j.EntryDate >= fromDateStart
                && j.EntryDate < toDateEnd)
            .Select(j => j.Id);

        // Get all lines for posted entries, joined with accounts
        var lineQuery = _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => postedEntryIds.Contains(l.JournalEntryId));

        if (accountId.HasValue)
            lineQuery = lineQuery.Where(l => l.AccountId == accountId.Value);

        if (projectId.HasValue)
            lineQuery = lineQuery.Where(l => l.ProjectId == projectId.Value || l.JournalEntry.ProjectId == projectId.Value);

        if (branchId.HasValue)
            lineQuery = lineQuery.Where(l => l.BranchId == branchId.Value || l.JournalEntry.BranchId == branchId.Value);

        if (effectiveDimId.HasValue)
        {
            var dimLineIds = _db.Set<JournalLineDimension>()
                .Where(d => d.DimensionId == effectiveDimId.Value)
                .Select(d => d.JournalEntryLineId);
            lineQuery = lineQuery.Where(l => dimLineIds.Contains(l.Id) || l.DimensionId == effectiveDimId.Value || l.JournalEntry.DimensionId == effectiveDimId.Value);
        }

        var lines = await lineQuery
            .OrderBy(l => l.Account!.AccountCode)
            .ThenBy(l => l.JournalEntry.EntryDate)
            .ThenBy(l => l.JournalEntry.EntryNumber)
            .ToListAsync();

        // Get opening balances (all posted/reversed entries before fromDate)
        var openingEntryIds = _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                && j.EntryDate < fromDateStart)
            .Select(j => j.Id);

        var openingLineQuery = _db.JournalEntryLines
            .Include(l => l.Account)
            .Where(l => openingEntryIds.Contains(l.JournalEntryId));

        if (accountId.HasValue)
            openingLineQuery = openingLineQuery.Where(l => l.AccountId == accountId.Value);

        var openingLines = await openingLineQuery.ToListAsync();

        var openingBalances = openingLines
            .Where(l => l.Account != null)
            .GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g =>
            {
                var acctType = g.First().Account.AccountType;
                var debit = g.Sum(l => l.DebitAmount);
                var credit = g.Sum(l => l.CreditAmount);
                return (acctType == AccountType.Asset || acctType == AccountType.Expense)
                    ? debit - credit : credit - debit;
            });

        // Group by account (filter out lines with missing accounts)
        var grouped = lines
            .Where(l => l.Account != null)
            .GroupBy(l => new { l.AccountId, l.Account.AccountCode, l.Account.AccountName, l.Account.AccountType });

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
                g.Key.AccountId, g.Key.AccountCode, g.Key.AccountName, g.Key.AccountType, (int)g.Key.AccountType,
                opening, g.Sum(l => l.DebitAmount), g.Sum(l => l.CreditAmount),
                runningBalance, transactions));
        }

        return new GeneralLedgerResponse(accounts, fromDate, toDate);
    }

    public async Task<object> GetGlDebugAsync(Guid companyId, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var from = fromDate?.Date ?? DateTime.UtcNow.Date.AddMonths(-6);
        var to = toDate?.Date.AddDays(1) ?? DateTime.UtcNow.Date.AddDays(1);

        var totalEntries = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId)
            .CountAsync();

        var postedEntries = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && j.Status == JournalEntryStatus.Posted)
            .CountAsync();

        var postedInRange = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && j.Status == JournalEntryStatus.Posted
                && j.EntryDate >= from
                && j.EntryDate < to)
            .CountAsync();

        var totalLines = await _db.JournalEntryLines
            .CountAsync(l => _db.JournalEntries
                .Where(j => j.CompanyId == companyId)
                .Select(j => j.Id)
                .Contains(l.JournalEntryId));

        var postedLines = await _db.JournalEntryLines
            .CountAsync(l => _db.JournalEntries
                .Where(j => j.CompanyId == companyId && j.Status == JournalEntryStatus.Posted)
                .Select(j => j.Id)
                .Contains(l.JournalEntryId));

        var postedLinesInRange = await _db.JournalEntryLines
            .CountAsync(l => _db.JournalEntries
                .Where(j => j.CompanyId == companyId
                    && j.Status == JournalEntryStatus.Posted
                    && j.EntryDate >= from
                    && j.EntryDate < to)
                .Select(j => j.Id)
                .Contains(l.JournalEntryId));

        var sampleEntry = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && j.Status == JournalEntryStatus.Posted)
            .OrderByDescending(j => j.EntryDate)
            .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.Status, j.TotalDebit, j.TotalCredit })
            .FirstOrDefaultAsync();

        object? sampleLines = null;
        if (sampleEntry != null)
        {
            var lineCount = await _db.JournalEntryLines
                .CountAsync(l => l.JournalEntryId == sampleEntry.Id);
            var lines = await _db.JournalEntryLines
                .Where(l => l.JournalEntryId == sampleEntry.Id)
                .Select(l => new { l.Id, l.AccountId, l.DebitAmount, l.CreditAmount, l.Description })
                .Take(5)
                .ToListAsync();
            sampleLines = new { lineCount, lines };
        }

        var statusBreakdown = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId)
            .GroupBy(j => j.Status)
            .Select(g => new { status = g.Key.ToString(), count = g.Count() })
            .ToListAsync();

        var dateRange = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId)
            .GroupBy(j => 1)
            .Select(g => new { minDate = g.Min(j => j.EntryDate), maxDate = g.Max(j => j.EntryDate) })
            .FirstOrDefaultAsync();

        return new
        {
            queryRange = new { from, to },
            totalEntries,
            postedEntries,
            postedInRange,
            totalLines,
            postedLines,
            postedLinesInRange,
            statusBreakdown,
            dateRange,
            sampleEntry,
            sampleLines
        };
    }

    public async Task<int> RepairBuddhistDatesAsync(Guid companyId)
    {
        int totalFixed = 0;

        var badEntries = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && j.EntryDate.Year < 1900)
            .ToListAsync();
        foreach (var entry in badEntries)
        {
            entry.EntryDate = entry.EntryDate.AddYears(543);
            entry.UpdatedAt = DateTime.UtcNow;
        }
        totalFixed += badEntries.Count;

        var badDocs = await _db.Documents
            .Where(d => d.CompanyId == companyId && d.DocumentDate.Year < 1900)
            .ToListAsync();
        foreach (var doc in badDocs)
        {
            doc.DocumentDate = doc.DocumentDate.AddYears(543);
            if (doc.DueDate.HasValue && doc.DueDate.Value.Year < 1900)
                doc.DueDate = doc.DueDate.Value.AddYears(543);
            doc.UpdatedAt = DateTime.UtcNow;
        }
        totalFixed += badDocs.Count;

        var badPayments = await _db.Payments
            .Where(p => p.CompanyId == companyId && p.PaymentDate.Year < 1900)
            .ToListAsync();
        foreach (var payment in badPayments)
        {
            payment.PaymentDate = payment.PaymentDate.AddYears(543);
            payment.UpdatedAt = DateTime.UtcNow;
        }
        totalFixed += badPayments.Count;

        var badBankTxns = await _db.BankTransactions
            .Where(b => b.CompanyId == companyId && b.TransactionDate.Year < 1900)
            .ToListAsync();
        foreach (var bt in badBankTxns)
        {
            bt.TransactionDate = bt.TransactionDate.AddYears(543);
            bt.UpdatedAt = DateTime.UtcNow;
        }
        totalFixed += badBankTxns.Count;

        var badRecurring = await _db.RecurringTransactions
            .Where(r => r.CompanyId == companyId && r.StartDate.Year < 1900)
            .ToListAsync();
        foreach (var r in badRecurring)
        {
            r.StartDate = r.StartDate.AddYears(543);
            if (r.EndDate.HasValue && r.EndDate.Value.Year < 1900)
                r.EndDate = r.EndDate.Value.AddYears(543);
            if (r.NextRunDate.Year < 1900)
                r.NextRunDate = r.NextRunDate.AddYears(543);
            if (r.LastRunDate.HasValue && r.LastRunDate.Value.Year < 1900)
                r.LastRunDate = r.LastRunDate.Value.AddYears(543);
            r.UpdatedAt = DateTime.UtcNow;
        }
        totalFixed += badRecurring.Count;

        var badAssets = await _db.FixedAssets
            .Where(a => a.CompanyId == companyId && a.PurchaseDate.Year < 1900)
            .ToListAsync();
        foreach (var a in badAssets)
        {
            a.PurchaseDate = a.PurchaseDate.AddYears(543);
            if (a.DisposalDate.HasValue && a.DisposalDate.Value.Year < 1900)
                a.DisposalDate = a.DisposalDate.Value.AddYears(543);
            a.UpdatedAt = DateTime.UtcNow;
        }
        totalFixed += badAssets.Count;

        var badDeposits = await _db.DepositTransactions
            .Where(d => d.CompanyId == companyId && d.TransactionDate.Year < 1900)
            .ToListAsync();
        foreach (var d in badDeposits)
        {
            d.TransactionDate = d.TransactionDate.AddYears(543);
            if (d.ExpectedReturnDate.HasValue && d.ExpectedReturnDate.Value.Year < 1900)
                d.ExpectedReturnDate = d.ExpectedReturnDate.Value.AddYears(543);
            d.UpdatedAt = DateTime.UtcNow;
        }
        totalFixed += badDeposits.Count;

        var badStockMoves = await _db.StockMovements
            .Where(s => s.CompanyId == companyId && s.MovementDate.Year < 1900)
            .ToListAsync();
        foreach (var s in badStockMoves)
        {
            s.MovementDate = s.MovementDate.AddYears(543);
            s.UpdatedAt = DateTime.UtcNow;
        }
        totalFixed += badStockMoves.Count;

        var badTaxLines = await _db.TaxReportLines
            .Where(l => _db.TaxReports.Any(r => r.Id == l.TaxReportId && r.CompanyId == companyId)
                && l.TransactionDate.Year < 1900)
            .ToListAsync();
        foreach (var l in badTaxLines)
        {
            l.TransactionDate = l.TransactionDate.AddYears(543);
            l.UpdatedAt = DateTime.UtcNow;
        }
        totalFixed += badTaxLines.Count;

        await _db.SaveChangesAsync();
        return totalFixed;
    }

    public async Task<int> RebuildMissingLinesAsync(Guid companyId)
    {
        // Find entries that have no lines
        var entriesWithoutLines = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && !j.Lines.Any())
            .ToListAsync();

        if (!entriesWithoutLines.Any()) return 0;

        // Load account mappings by journal type
        var cashAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.IsActive && !a.IsDeleted);
        var bankAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("1112") && a.IsActive && !a.IsDeleted);
        var arAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("113") && a.IsActive && !a.IsDeleted);
        var apAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("212") && a.IsActive && !a.IsDeleted);
        var revenueAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("41") && a.IsActive && !a.IsDeleted);
        var expenseAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("51") && a.IsActive && !a.IsDeleted);
        var depositAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("215") && a.IsActive && !a.IsDeleted);

        var defaultDebit = cashAccount ?? bankAccount;
        var defaultCredit = revenueAccount ?? arAccount;

        int rebuilt = 0;
        foreach (var entry in entriesWithoutLines)
        {
            var amount = entry.TotalDebit > 0 ? entry.TotalDebit : entry.TotalCredit;
            if (amount <= 0) continue;

            Guid? debitAccountId = null;
            Guid? creditAccountId = null;

            switch (entry.JournalType)
            {
                case JournalType.Sales:
                    debitAccountId = arAccount?.Id;
                    creditAccountId = revenueAccount?.Id;
                    break;
                case JournalType.CashReceipts:
                    debitAccountId = (bankAccount ?? cashAccount)?.Id;
                    // Check if description mentions deposit
                    if (entry.Description?.Contains("มัดจำ") == true)
                        creditAccountId = depositAccount?.Id ?? arAccount?.Id;
                    else
                        creditAccountId = arAccount?.Id;
                    break;
                case JournalType.CashPayments:
                    debitAccountId = expenseAccount?.Id ?? apAccount?.Id;
                    creditAccountId = (bankAccount ?? cashAccount)?.Id;
                    break;
                case JournalType.Purchase:
                    debitAccountId = expenseAccount?.Id;
                    creditAccountId = apAccount?.Id;
                    break;
                default:
                    debitAccountId = defaultDebit?.Id;
                    creditAccountId = defaultCredit?.Id;
                    break;
            }

            if (debitAccountId == null || creditAccountId == null) continue;

            entry.Lines = new List<JournalEntryLine>
            {
                new()
                {
                    AccountId = debitAccountId.Value,
                    DebitAmount = amount,
                    CreditAmount = 0,
                    Description = entry.Description,
                    LineOrder = 1
                },
                new()
                {
                    AccountId = creditAccountId.Value,
                    DebitAmount = 0,
                    CreditAmount = amount,
                    Description = entry.Description,
                    LineOrder = 2
                }
            };
            rebuilt++;
        }

        await _db.SaveChangesAsync();
        return rebuilt;
    }

    // ==================== Reports ====================

    /// <summary>
    /// คืน Dictionary&lt;AccountId, balance&gt; ตามวันที่ที่ระบุ โดยใช้
    /// OpeningBalance baseline เดียวกับ GetTrialBalanceAsync — Dashboard /
    /// CashFlow / ExecutiveReports เรียกตัวนี้แทนการ sum JE จาก inception
    /// เพื่อให้ทุกหน้าโชว์ยอดเงินสด/AR/AP เท่ากันสำหรับบริษัทที่ migrate มา.
    /// คืนค่า balance net ตามสัญลักษณ์ของ AccountType:
    ///   Asset/Expense → Debit > 0
    ///   Liability/Equity/Revenue → Credit > 0 (เก็บเป็นเลขบวก ผู้เรียก
    ///   ตีความเอง). Dictionary มีทุกบัญชีที่มี opening หรือ movement.
    /// </summary>
    public async Task<Dictionary<Guid, decimal>> GetAccountBalanceAsOfAsync(
        Guid companyId, DateTime asOfDate)
    {
        var asOf = asOfDate.Date.AddDays(1);
        var anchorPeriodId = await (
            from p in _db.FiscalPeriods.AsNoTracking()
            join o in _db.OpeningBalances.AsNoTracking() on p.Id equals o.FiscalPeriodId
            where p.CompanyId == companyId && p.StartDate <= asOfDate.Date
            orderby p.StartDate descending
            select (Guid?)p.Id
        ).FirstOrDefaultAsync();

        DateTime? anchorStart = null;
        var openings = new Dictionary<Guid, (decimal Debit, decimal Credit)>();
        if (anchorPeriodId.HasValue)
        {
            anchorStart = await _db.FiscalPeriods.AsNoTracking()
                .Where(p => p.Id == anchorPeriodId.Value).Select(p => p.StartDate).FirstAsync();
            openings = await _db.OpeningBalances.AsNoTracking()
                .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == anchorPeriodId.Value)
                .GroupBy(o => o.AccountId)
                .Select(g => new { AccountId = g.Key, Debit = g.Sum(x => x.OpeningDebit), Credit = g.Sum(x => x.OpeningCredit) })
                .ToDictionaryAsync(x => x.AccountId, x => (x.Debit, x.Credit));
        }

        var movementStart = anchorStart;
        var movements = await _db.JournalEntryLines.AsNoTracking()
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && (l.JournalEntry.Status == JournalEntryStatus.Posted
                    || l.JournalEntry.Status == JournalEntryStatus.Reversed)
                && l.JournalEntry.EntryDate < asOf
                && (!movementStart.HasValue || l.JournalEntry.EntryDate >= movementStart.Value))
            .GroupBy(l => new { l.AccountId, l.Account.AccountType })
            .Select(g => new
            {
                g.Key.AccountId, g.Key.AccountType,
                Debit = g.Sum(x => x.DebitAmount),
                Credit = g.Sum(x => x.CreditAmount),
            })
            .ToListAsync();

        var result = new Dictionary<Guid, decimal>();
        var allIds = openings.Keys.Union(movements.Select(m => m.AccountId)).Distinct();
        var accountTypes = movements.ToDictionary(m => m.AccountId, m => m.AccountType);
        foreach (var id in allIds)
        {
            openings.TryGetValue(id, out var op);
            var mv = movements.FirstOrDefault(x => x.AccountId == id);
            var totalDebit = op.Debit + (mv?.Debit ?? 0);
            var totalCredit = op.Credit + (mv?.Credit ?? 0);
            if (!accountTypes.TryGetValue(id, out var atype))
            {
                atype = await _db.ChartOfAccounts.AsNoTracking()
                    .Where(a => a.Id == id)
                    .Select(a => a.AccountType).FirstOrDefaultAsync();
            }
            // Asset/Expense lives on the debit side; Liability/Equity/Revenue
            // on the credit side. Return the *signed* normal balance.
            var debitNormal = atype == AccountType.Asset || atype == AccountType.Expense;
            result[id] = debitNormal ? (totalDebit - totalCredit) : (totalCredit - totalDebit);
        }
        return result;
    }

    public async Task<TrialBalanceResponse> GetTrialBalanceAsync(Guid companyId, DateTime asOfDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null)
    {
        // Pick the OpeningBalance baseline anchored to the most recent fiscal
        // period that has begun on/before asOfDate. RollOpeningBalancesAsync
        // stores compounded balances (year N+1 opening = year N opening +
        // year N net movements), so reading the latest one and adding the
        // movements within its period up to asOfDate yields the correct
        // closing balance — and crucially, picks up balances imported via the
        // migration wizard that have no inception JE history. When no opening
        // exists (a company that's only ever used the system from day one),
        // fall back to summing every JE from inception (legacy behaviour).
        var asOf = asOfDate.Date.AddDays(1);
        // Find the latest fiscal period that BOTH started on/before asOfDate AND
        // actually has OpeningBalance rows. Without the "actually has rows"
        // filter, monthly-period companies (whose only OpeningBalances live on
        // the first period of each fiscal year) would never trigger the baseline
        // path because the nearest period contains no rows.
        var anchorPeriodId = await (
            from p in _db.FiscalPeriods.AsNoTracking()
            join o in _db.OpeningBalances.AsNoTracking() on p.Id equals o.FiscalPeriodId
            where p.CompanyId == companyId && p.StartDate <= asOfDate.Date
            orderby p.StartDate descending
            select (Guid?)p.Id
        ).FirstOrDefaultAsync();

        DateTime? anchorStart = null;
        var openingByAccount = new Dictionary<Guid, (decimal Debit, decimal Credit)>();
        if (anchorPeriodId.HasValue)
        {
            anchorStart = await _db.FiscalPeriods.AsNoTracking()
                .Where(p => p.Id == anchorPeriodId.Value).Select(p => p.StartDate).FirstAsync();
            openingByAccount = await _db.OpeningBalances.AsNoTracking()
                .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == anchorPeriodId.Value)
                .GroupBy(o => o.AccountId)
                .Select(g => new { AccountId = g.Key, Debit = g.Sum(x => x.OpeningDebit), Credit = g.Sum(x => x.OpeningCredit) })
                .ToDictionaryAsync(x => x.AccountId, x => (x.Debit, x.Credit));
        }

        // Posted JE lines — when an OpeningBalance baseline is present, limit
        // movements to the period [anchor.StartDate, asOfDate]; otherwise pull
        // everything up to asOfDate (legacy mode).
        var movementStart = anchorStart;

        var postedEntryIds = _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                && j.EntryDate < asOf
                && (!movementStart.HasValue || j.EntryDate >= movementStart.Value))
            .Select(j => j.Id);

        var lineQuery = _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => postedEntryIds.Contains(l.JournalEntryId));

        if (projectId.HasValue)
            lineQuery = lineQuery.Where(l => l.ProjectId == projectId.Value || l.JournalEntry.ProjectId == projectId.Value);
        if (branchId.HasValue)
            lineQuery = lineQuery.Where(l => l.BranchId == branchId.Value || l.JournalEntry.BranchId == branchId.Value);
        if (dimensionId.HasValue)
            lineQuery = lineQuery.Where(l => l.DimensionId == dimensionId.Value || l.JournalEntry.DimensionId == dimensionId.Value);

        var postedLines = await lineQuery.ToListAsync();

        // Merge movements with the OpeningBalance baseline — every account that
        // either has an opening row or saw movement in the window appears.
        var byAccount = postedLines
            .Where(l => l.Account != null)
            .GroupBy(l => new { l.AccountId, l.Account.AccountCode, l.Account.AccountName, l.Account.AccountType })
            .ToDictionary(g => g.Key, g => (Debit: g.Sum(l => l.DebitAmount), Credit: g.Sum(l => l.CreditAmount)));

        // For accounts that have opening but no movement, fetch their COA detail
        // so they still appear in the report (otherwise the opening balance
        // would be silently dropped).
        var openingOnlyAccountIds = openingByAccount.Keys
            .Where(aid => !byAccount.Keys.Any(k => k.AccountId == aid)).ToList();
        var coaDetail = new List<(Guid AccountId, string AccountCode, string AccountName, AccountType AccountType)>();
        if (openingOnlyAccountIds.Count > 0)
        {
            var rows = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && openingOnlyAccountIds.Contains(a.Id))
                .Select(a => new { a.Id, a.AccountCode, a.AccountName, a.AccountType })
                .ToListAsync();
            coaDetail = rows.Select(a => (a.Id, a.AccountCode, a.AccountName, a.AccountType)).ToList();
        }

        var items = new List<TrialBalanceItem>();
        foreach (var (key, value) in byAccount)
        {
            var (od, oc) = openingByAccount.TryGetValue(key.AccountId, out var ob) ? ob : (0m, 0m);
            items.Add(new TrialBalanceItem(
                key.AccountCode, key.AccountName, key.AccountType, (int)key.AccountType,
                value.Debit + od, value.Credit + oc));
        }
        foreach (var (accountId, code, name, type) in coaDetail)
        {
            var (od, oc) = openingByAccount[accountId];
            items.Add(new TrialBalanceItem(code, name, type, (int)type, od, oc));
        }

        var grouped = items.OrderBy(i => i.AccountCode).ToList();

        return new TrialBalanceResponse(
            asOfDate, grouped,
            grouped.Sum(i => i.DebitBalance),
            grouped.Sum(i => i.CreditBalance));
    }

    public async Task<BalanceSheetResponse> GetBalanceSheetAsync(Guid companyId, DateTime asOfDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null)
    {
        var trial = await GetTrialBalanceAsync(companyId, asOfDate, projectId, branchId, dimensionId);

        var assets = trial.Items.Where(i => i.AccountType == AccountType.Asset)
            .Select(i => new BalanceSheetSection(i.AccountCode, i.AccountName, i.DebitBalance - i.CreditBalance, null)).ToList();
        var liabilities = trial.Items.Where(i => i.AccountType == AccountType.Liability)
            .Select(i => new BalanceSheetSection(i.AccountCode, i.AccountName, i.CreditBalance - i.DebitBalance, null)).ToList();
        var equity = trial.Items.Where(i => i.AccountType == AccountType.Equity)
            .Select(i => new BalanceSheetSection(i.AccountCode, i.AccountName, i.CreditBalance - i.DebitBalance, null)).ToList();

        // Current period P&L not yet closed to Retained Earnings — show as separate equity line
        // This follows standard ERP practice for interim financial statements (TAS 34)
        var revenue = trial.Items.Where(i => i.AccountType == AccountType.Revenue).Sum(i => i.CreditBalance - i.DebitBalance);
        var expenses = trial.Items.Where(i => i.AccountType == AccountType.Expense).Sum(i => i.DebitBalance - i.CreditBalance);
        var netIncome = revenue - expenses;
        if (Math.Abs(netIncome) > 0.01m)
            equity.Add(new BalanceSheetSection("", "กำไร(ขาดทุน)สุทธิงวดปัจจุบัน", netIncome, null));

        return new BalanceSheetResponse(
            asOfDate, assets, liabilities, equity,
            assets.Sum(a => a.Amount),
            liabilities.Sum(l => l.Amount),
            equity.Sum(e => e.Amount));
    }

    public async Task<(decimal Revenue, decimal Expense)> GetSnapshotTotalsAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        // Single GROUP BY on JournalEntryLines joined to ChartOfAccounts.
        // No Includes, no per-line hydration — returns 2 scalars and that's it.
        // Roughly 10–50× faster than GetProfitAndLossAsync at the scale where
        // a busy month has thousands of posted lines.
        var toEnd = toDate.Date.AddDays(1);
        var grouped = await (
            from l in _db.JournalEntryLines.AsNoTracking()
            join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
            join a in _db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
            where j.CompanyId == companyId
                && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                && j.EntryDate >= fromDate && j.EntryDate < toEnd
                && (a.AccountType == AccountType.Revenue || a.AccountType == AccountType.Expense)
            group new { l.DebitAmount, l.CreditAmount } by a.AccountType into g
            select new { Type = g.Key, NetDebit = g.Sum(x => x.DebitAmount), NetCredit = g.Sum(x => x.CreditAmount) }
        ).ToListAsync();

        decimal rev = 0m, exp = 0m;
        foreach (var g in grouped)
        {
            if (g.Type == AccountType.Revenue) rev = g.NetCredit - g.NetDebit;
            else if (g.Type == AccountType.Expense) exp = g.NetDebit - g.NetCredit;
        }
        return (rev, exp);
    }

    public async Task<ProfitAndLossResponse> GetProfitAndLossAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null)
    {
        var postedEntryIds = _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                && j.EntryDate >= fromDate.Date
                && j.EntryDate < toDate.Date.AddDays(1))
            .Select(j => j.Id);

        var lineQuery = _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => postedEntryIds.Contains(l.JournalEntryId)
                && (l.Account!.AccountType == AccountType.Revenue || l.Account!.AccountType == AccountType.Expense));

        if (projectId.HasValue)
            lineQuery = lineQuery.Where(l => l.ProjectId == projectId.Value || l.JournalEntry.ProjectId == projectId.Value);
        if (branchId.HasValue)
            lineQuery = lineQuery.Where(l => l.BranchId == branchId.Value || l.JournalEntry.BranchId == branchId.Value);
        if (dimensionId.HasValue)
            lineQuery = lineQuery.Where(l => l.DimensionId == dimensionId.Value || l.JournalEntry.DimensionId == dimensionId.Value);

        var postedLines = await lineQuery.ToListAsync();

        var grouped = postedLines
            .Where(l => l.Account != null)
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

    public async Task<CashFlowStatementResponse> GetCashFlowStatementAsync(Guid companyId, DateTime fromDate, DateTime toDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null)
    {
        var postedEntryIds = _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                && j.EntryDate >= fromDate.Date
                && j.EntryDate < toDate.Date.AddDays(1))
            .Select(j => j.Id);

        var lineQuery = _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => postedEntryIds.Contains(l.JournalEntryId));

        if (projectId.HasValue)
            lineQuery = lineQuery.Where(l => l.ProjectId == projectId.Value || l.JournalEntry.ProjectId == projectId.Value);
        if (branchId.HasValue)
            lineQuery = lineQuery.Where(l => l.BranchId == branchId.Value || l.JournalEntry.BranchId == branchId.Value);
        if (dimensionId.HasValue)
            lineQuery = lineQuery.Where(l => l.DimensionId == dimensionId.Value || l.JournalEntry.DimensionId == dimensionId.Value);

        var allLines = await lineQuery.ToListAsync();

        var postedLines = allLines.Where(l => l.Account != null).ToList();

        // Operating Activities: Revenue & Expense accounts + changes in current assets/liabilities
        var operatingItems = new List<CashFlowLineItem>();

        // Net income
        var revenue = postedLines.Where(l => l.Account.AccountType == AccountType.Revenue)
            .Sum(l => l.CreditAmount - l.DebitAmount);
        var expenses = postedLines.Where(l => l.Account.AccountType == AccountType.Expense)
            .Sum(l => l.DebitAmount - l.CreditAmount);
        var netIncome = revenue - expenses;
        operatingItems.Add(new CashFlowLineItem("กำไร(ขาดทุน)สุทธิ", null, netIncome));

        // F23 — แยกบรรทัด postedLines ตาม CashFlowSection override ก่อน.
        // บัญชีที่ admin ตั้ง override ไว้ (CashFlowSection != None) ใช้
        // override นั้น และ exclude จากการ match prefix ด้านล่าง (ป้องกัน
        // double-count). บัญชี None ปล่อยให้ prefix heuristic เดิมจับ.
        var overrideOperating = postedLines
            .Where(l => l.Account.CashFlowSection == CashFlowSectionType.Operating).ToList();
        var overrideInvesting = postedLines
            .Where(l => l.Account.CashFlowSection == CashFlowSectionType.Investing).ToList();
        var overrideFinancing = postedLines
            .Where(l => l.Account.CashFlowSection == CashFlowSectionType.Financing).ToList();
        var noOverride = postedLines
            .Where(l => l.Account.CashFlowSection == CashFlowSectionType.None).ToList();

        // Depreciation add-back (non-cash expense) - 56xxx
        var depreciation = noOverride
            .Where(l => l.Account.AccountCode.StartsWith("56"))
            .Sum(l => l.DebitAmount - l.CreditAmount);
        if (depreciation != 0)
            operatingItems.Add(new CashFlowLineItem("ค่าเสื่อมราคา (บวกกลับ)", "56", depreciation));

        // Changes in AR - 113xx ลูกหนี้การค้า
        var arChange = noOverride
            .Where(l => l.Account.AccountCode.StartsWith("113"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (arChange != 0)
            operatingItems.Add(new CashFlowLineItem("ลูกหนี้การค้า (เพิ่มขึ้น)/ลดลง", "113", arChange));

        // Changes in Inventory - 115xx สินค้าคงเหลือ
        var inventoryChange = noOverride
            .Where(l => l.Account.AccountCode.StartsWith("115"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (inventoryChange != 0)
            operatingItems.Add(new CashFlowLineItem("สินค้าคงเหลือ (เพิ่มขึ้น)/ลดลง", "115", inventoryChange));

        // Changes in AP - 212xx เจ้าหนี้การค้า
        var apChange = noOverride
            .Where(l => l.Account.AccountCode.StartsWith("212"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (apChange != 0)
            operatingItems.Add(new CashFlowLineItem("เจ้าหนี้การค้า เพิ่มขึ้น/(ลดลง)", "212", apChange));

        // Tax payable changes - 219xx ภาษีค้างจ่าย
        var taxPayableChange = noOverride
            .Where(l => l.Account.AccountCode.StartsWith("219"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (taxPayableChange != 0)
            operatingItems.Add(new CashFlowLineItem("ภาษีค้างจ่าย เพิ่มขึ้น/(ลดลง)", "219", taxPayableChange));

        // F23 — รวม override accounts ที่กำหนดเป็น Operating ด้วยตนเอง
        foreach (var grp in overrideOperating.GroupBy(l => new { l.Account.AccountCode, l.Account.AccountName }))
        {
            var amt = grp.Sum(l => l.CreditAmount - l.DebitAmount);
            if (amt != 0)
                operatingItems.Add(new CashFlowLineItem($"{grp.Key.AccountName} (ตั้งค่าเป็น Operating)", grp.Key.AccountCode, amt));
        }

        var operatingTotal = operatingItems.Sum(i => i.Amount);

        // Investing Activities: Fixed asset accounts - 122xx ที่ดิน อาคาร อุปกรณ์
        var investingItems = new List<CashFlowLineItem>();
        var fixedAssetChange = noOverride
            .Where(l => l.Account.AccountCode.StartsWith("122") || l.Account.AccountCode.StartsWith("123"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (fixedAssetChange != 0)
            investingItems.Add(new CashFlowLineItem("ซื้อ/ขาย ที่ดิน อาคาร อุปกรณ์", "122", fixedAssetChange));

        foreach (var grp in overrideInvesting.GroupBy(l => new { l.Account.AccountCode, l.Account.AccountName }))
        {
            var amt = grp.Sum(l => l.CreditAmount - l.DebitAmount);
            if (amt != 0)
                investingItems.Add(new CashFlowLineItem($"{grp.Key.AccountName} (ตั้งค่าเป็น Investing)", grp.Key.AccountCode, amt));
        }

        var investingTotal = investingItems.Sum(i => i.Amount);

        // Financing Activities: Long-term liabilities (221xxx) + Equity (31xxx)
        var financingItems = new List<CashFlowLineItem>();
        var longTermDebtChange = noOverride
            .Where(l => l.Account.AccountCode.StartsWith("221"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (longTermDebtChange != 0)
            financingItems.Add(new CashFlowLineItem("เงินกู้ยืมระยะยาว เพิ่มขึ้น/(ลดลง)", "221", longTermDebtChange));

        var equityChange = noOverride
            .Where(l => l.Account.AccountCode.StartsWith("31"))
            .Sum(l => l.CreditAmount - l.DebitAmount);
        if (equityChange != 0)
            financingItems.Add(new CashFlowLineItem("ทุนจดทะเบียน เพิ่มขึ้น/(ลดลง)", "31", equityChange));

        foreach (var grp in overrideFinancing.GroupBy(l => new { l.Account.AccountCode, l.Account.AccountName }))
        {
            var amt = grp.Sum(l => l.CreditAmount - l.DebitAmount);
            if (amt != 0)
                financingItems.Add(new CashFlowLineItem($"{grp.Key.AccountName} (ตั้งค่าเป็น Financing)", grp.Key.AccountCode, amt));
        }

        var financingTotal = financingItems.Sum(i => i.Amount);

        // Cash balances
        var netCashChange = operatingTotal + investingTotal + financingTotal;

        // Opening cash = cash accounts before fromDate (111xxx = เงินสดและเงินฝากธนาคาร)
        var cashAccountCodes = new[] { "111" }; // Cash + Bank deposits (all under 111xxx)
        var openingCash = await _db.JournalEntryLines
            .Include(l => l.Account).Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && (l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed)
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

    /// <summary>
    /// Create every missing monthly period (1-12) for a calendar year in one
    /// shot, so the user never has to add periods by hand each year. Existing
    /// periods are left untouched. Returns the number created.
    /// </summary>
    public async Task<int> EnsureFiscalYearPeriodsAsync(Guid companyId, int year)
    {
        if (year < 2000 || year > 2200)
            throw new InvalidOperationException("ปีไม่ถูกต้อง");

        var existingMonths = (await _db.FiscalPeriods
            .Where(f => f.CompanyId == companyId && f.Year == year)
            .Select(f => f.Month)
            .ToListAsync())
            .ToHashSet();

        var created = 0;
        for (var m = 1; m <= 12; m++)
        {
            if (existingMonths.Contains(m)) continue;
            var start = new DateTime(year, m, 1);
            _db.FiscalPeriods.Add(new FiscalPeriod
            {
                CompanyId = companyId,
                Name = $"{year}/{m:D2}",
                Year = year,
                Month = m,
                StartDate = start,
                EndDate = start.AddMonths(1).AddDays(-1),
                Status = FiscalPeriodStatus.Open,
            });
            created++;
        }
        if (created > 0) await _db.SaveChangesAsync();
        return created;
    }

    /// <summary>
    /// Fix a fiscal period that was created with the wrong start/end dates
    /// (or year/month). Two modes:
    ///
    ///   <paramref name="forceReassignEntries"/> = false (default, safe)
    ///     Block when ANY linked JE / OpeningBalance exists, OR when
    ///     entries' EntryDate falls in either the OLD or the NEW window
    ///     of this period. Caller must void / unlink first.
    ///
    ///   <paramref name="forceReassignEntries"/> = true (intentional cascade)
    ///     For the typo-fix workflow ("created with wrong year, has 324
    ///     JEs to migrate"): change the dates AND walk every JE / OB
    ///     row that was previously linked to this period OR whose date
    ///     falls in the OLD/NEW window, re-binding each to whichever
    ///     fiscal period now contains its date (or null when no period
    ///     covers it). All in one transaction so we never have partial
    ///     state. The Filed-status JEs aren't moved across into other
    ///     companies, just re-linked within this company's period table.
    ///
    /// Closed/Locked periods always refuse — caller must Reopen first.
    /// (Year, Month) uniqueness within the company is preserved. New
    /// date window is also checked against other periods' ranges to
    /// catch accidental overlap that would otherwise leave the date
    /// → period mapping ambiguous.
    /// </summary>
    public async Task<FiscalPeriodResponse> UpdateFiscalPeriodAsync(
        Guid companyId, Guid periodId, CreateFiscalPeriodRequest request,
        bool forceReassignEntries = false)
    {
        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f => f.Id == periodId && f.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงวดบัญชี");

        if (period.Status != FiscalPeriodStatus.Open)
            throw new InvalidOperationException("แก้ไขได้เฉพาะงวดบัญชีที่สถานะเป็น Open เท่านั้น — กรุณา Reopen งวดก่อน");

        if (request.StartDate > request.EndDate)
            throw new ArgumentException("วันเริ่มต้นต้องไม่หลังวันสิ้นสุด");

        // (Year, Month) uniqueness
        if ((period.Year != request.Year || period.Month != request.Month)
            && await _db.FiscalPeriods.AnyAsync(f => f.CompanyId == companyId
                && f.Year == request.Year && f.Month == request.Month && f.Id != periodId))
        {
            throw new InvalidOperationException($"งวดบัญชี {request.Year}/{request.Month} มีอยู่แล้ว");
        }

        // New-date overlap with another period — always refuse, even
        // with force. Overlapping windows make the date→period mapping
        // ambiguous, which would break cascading reassignment + future
        // posting.
        var overlap = await _db.FiscalPeriods.AsNoTracking()
            .Where(f => f.CompanyId == companyId && f.Id != periodId
                     && f.StartDate <= request.EndDate && f.EndDate >= request.StartDate)
            .Select(f => f.Name)
            .FirstOrDefaultAsync();
        if (overlap != null)
            throw new InvalidOperationException(
                $"ช่วงวันที่ทับซ้อนกับงวด {overlap} — กรุณาเลือกช่วงที่ไม่ทับซ้อน");

        var oldStart = period.StartDate; var oldEnd = period.EndDate;
        var newStart = request.StartDate; var newEnd = request.EndDate;

        if (!forceReassignEntries)
        {
            // Safe path — refuse if anything is in either window.
            var jeCount = await _db.JournalEntries.AsNoTracking()
                .CountAsync(j => j.CompanyId == companyId
                    && (j.FiscalPeriodId == periodId
                        || (j.EntryDate >= oldStart && j.EntryDate <= oldEnd)
                        || (j.EntryDate >= newStart && j.EntryDate <= newEnd)));
            if (jeCount > 0)
                throw new InvalidOperationException(
                    $"ไม่สามารถแก้ไขช่วงวันที่ของงวดได้ — มีใบสำคัญ {jeCount} รายการที่กระทบงวด " +
                    $"ถ้ายืนยันว่าต้องการแก้ ระบบจะผูกใบสำคัญใหม่ตามวันที่ให้อัตโนมัติ (เลือก 'แก้ไข + ผูกใหม่')");
            var obCount = await _db.OpeningBalances.AsNoTracking()
                .CountAsync(o => o.CompanyId == companyId && o.FiscalPeriodId == periodId);
            if (obCount > 0)
                throw new InvalidOperationException(
                    $"ไม่สามารถแก้ไขได้ — งวดนี้มี Opening Balance {obCount} รายการอยู่ — เลือก 'แก้ไข + ผูกใหม่' เพื่อย้ายอัตโนมัติ");
        }

        // ────────────────────────────────────────────────────────────
        // Force path — wrap the whole thing in a transaction so a
        // partial cascade can't leave JEs pointing at the period they
        // just got reassigned away from.
        // ────────────────────────────────────────────────────────────
        await using var tx = forceReassignEntries
            ? await _db.Database.BeginTransactionAsync()
            : null;
        try
        {
            // Apply the new dates / year / month first so the upcoming
            // bucket-lookup includes this period's new range.
            period.Year = request.Year;
            period.Month = request.Month;
            period.Name = $"{request.Year}/{request.Month:D2}";
            period.StartDate = newStart;
            period.EndDate = newEnd;
            period.UpdatedAt = DateTime.UtcNow;

            if (forceReassignEntries)
            {
                // Snapshot of all periods (post-edit) so a single in-
                // memory scan can re-bucket without N+1 queries.
                var allPeriods = await _db.FiscalPeriods
                    .Where(f => f.CompanyId == companyId)
                    .ToListAsync();
                Guid? PeriodForDate(DateTime d)
                {
                    var match = allPeriods.FirstOrDefault(p => d >= p.StartDate && d <= p.EndDate);
                    return match?.Id;
                }

                // JEs in either window or previously linked to this period
                var affectedJes = await _db.JournalEntries
                    .Where(j => j.CompanyId == companyId
                        && (j.FiscalPeriodId == periodId
                            || (j.EntryDate >= oldStart && j.EntryDate <= oldEnd)
                            || (j.EntryDate >= newStart && j.EntryDate <= newEnd)))
                    .ToListAsync();
                foreach (var j in affectedJes)
                {
                    var target = PeriodForDate(j.EntryDate);
                    if (j.FiscalPeriodId != target)
                    {
                        j.FiscalPeriodId = target;
                        j.UpdatedAt = DateTime.UtcNow;
                    }
                }

                // OpeningBalance: rows already point at THIS period via
                // a non-nullable FK. The period itself moved (dates
                // changed) but the FK relationship stays valid — OB
                // represents "opening balance for THIS period", and
                // moving the period's dates moves the meaning of
                // "opening" with it. No reassignment needed.
            }

            await _db.SaveChangesAsync();
            if (tx != null) await tx.CommitAsync();
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync();
            throw;
        }

        return MapPeriodToResponse(period);
    }

    /// <summary>
    /// Delete a fiscal period — only for periods accidentally created with
    /// wrong dates that haven't been used yet. Refuses when:
    ///   * Status != Open (Closed / Locked / year-end-locked stays for audit)
    ///   * Any JournalEntry references this period via FiscalPeriodId or
    ///     falls within its date window
    ///   * Any OpeningBalance row points at it
    /// Hard delete (not soft) — accidentally-created periods carry no
    /// historical value, and a soft-deleted period would still bloat the
    /// uniqueness check for (Year, Month).
    /// </summary>
    public async Task DeleteFiscalPeriodAsync(Guid companyId, Guid periodId)
    {
        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f => f.Id == periodId && f.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงวดบัญชี");

        if (period.Status != FiscalPeriodStatus.Open)
            throw new InvalidOperationException("ลบได้เฉพาะงวดที่สถานะเป็น Open เท่านั้น — กรุณา Reopen ก่อน");

        var jeCount = await _db.JournalEntries.AsNoTracking()
            .CountAsync(j => j.CompanyId == companyId
                && (j.FiscalPeriodId == periodId
                    || (j.EntryDate >= period.StartDate && j.EntryDate <= period.EndDate)));
        if (jeCount > 0)
            throw new InvalidOperationException(
                $"ไม่สามารถลบงวดที่มีใบสำคัญ {jeCount} รายการได้ — กรุณา void ใบสำคัญในงวดก่อน");

        var obCount = await _db.OpeningBalances.AsNoTracking()
            .CountAsync(o => o.CompanyId == companyId && o.FiscalPeriodId == periodId);
        if (obCount > 0)
            throw new InvalidOperationException(
                $"ไม่สามารถลบได้ — งวดนี้มี Opening Balance {obCount} รายการอยู่");

        _db.FiscalPeriods.Remove(period);
        await _db.SaveChangesAsync();
    }

    public async Task CloseFiscalPeriodAsync(Guid companyId, Guid periodId)
    {
        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f => f.Id == periodId && f.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงวดบัญชี");

        if (period.Status != FiscalPeriodStatus.Open)
            throw new InvalidOperationException("สามารถปิดได้เฉพาะงวดบัญชีที่เปิดอยู่เท่านั้น");

        // ===== Pre-closing checklist =====
        var issues = new List<string>();

        // 1. Draft journal entries
        var draftCount = await _db.JournalEntries.CountAsync(j =>
            j.FiscalPeriodId == periodId && j.Status == JournalEntryStatus.Draft);
        if (draftCount > 0)
            issues.Add($"ยังมีใบสำคั��� Draft {draftCount} รายการ (ต้อง Post หรือ Void ก่อน)");

        // 2. Draft documents in this period
        var draftDocs = await _db.Documents.CountAsync(d =>
            d.CompanyId == companyId && !d.IsDeleted
            && d.DocumentDate >= period.StartDate && d.DocumentDate <= period.EndDate
            && d.Status == DocumentStatus.Draft);
        if (draftDocs > 0)
            issues.Add($"ยังมีเอกสาร Draft {draftDocs} ฉบับในงวดนี้");

        // 3. Unbalanced journal entries (Dr != Cr)
        var unbalanced = await _db.JournalEntries.CountAsync(j =>
            j.FiscalPeriodId == periodId
            && j.Status == JournalEntryStatus.Posted
            && Math.Abs(j.TotalDebit - j.TotalCredit) > 0.01m);
        if (unbalanced > 0)
            issues.Add($"พบใบสำคัญไม่สมดุล (เดบิต≠เครดิต) {unbalanced} รายการ");

        // 4. Unreconciled bank transactions
        var unreconciledBank = await _db.BankTransactions.CountAsync(t =>
            t.CompanyId == companyId
            && t.TransactionDate >= period.StartDate && t.TransactionDate <= period.EndDate
            && t.ReconciliationStatus == ReconciliationStatus.Unmatched);
        if (unreconciledBank > 0)
            issues.Add($"ยังมีรายการธนาคารที่ยังไม่กระทบยอด {unreconciledBank} รายการ (แนะนำให้กระทบยอดก่อน)");

        if (issues.Count > 0)
            throw new InvalidOperationException(
                "ไม่สามารถปิดงวดได้ — พบปัญหาที่ต้องแก้ไข:\n• " + string.Join("\n• ", issues));

        // ===== Create closing journal entries (ปิดบัญชีรายได้/ค่าใช้จ่ายเข้ากำไรสะสม) =====
        await CreateClosingEntriesAsync(companyId, period);

        period.Status = FiscalPeriodStatus.Closed;
        await _db.SaveChangesAsync();
    }

    private async Task CreateClosingEntriesAsync(Guid companyId, FiscalPeriod period)
    {
        var retainedEarningsAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId
                && a.AccountCode.StartsWith("32020") && a.IsActive)
            ?? await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.CompanyId == companyId
                    && a.AccountCode.StartsWith("3202") && a.IsActive)
            ?? await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.CompanyId == companyId
                    && a.AccountType == AccountType.Equity
                    && a.AccountCode.StartsWith("32") && a.Level >= 4 && a.IsActive);

        if (retainedEarningsAccount == null)
            throw new InvalidOperationException(
                "ไม่พบบัญชีกำไรสะสม (32020) ในผังบัญชี — กรุณาเพิ่มก่อนปิดงวด");

        // F9 — รวมเฉพาะ Posted (ที่ยังมีผล). Status==Reversed = entry ต้นที่
        // ถูก reverse แล้ว — มี reversal entry คู่ขนานที่ post กลับด้านอยู่
        // แล้ว ถ้านับ Reversed ด้วยจะ double-count: ต้น + reversal = 0 net
        // แต่ใส่ Reversed อันต้นเข้าไปอีก = +1 ทับ. ตัด Reversed ทิ้ง — เหลือ
        // เฉพาะ Posted ทั้ง original + reversal (สอง entries post normal +
        // post กลับ — net = 0 ถ้า cancel กันพอดี).
        var postedLines = await _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= period.StartDate
                && l.JournalEntry.EntryDate <= period.EndDate
                && (l.Account!.AccountType == AccountType.Revenue
                    || l.Account!.AccountType == AccountType.Expense))
            .ToListAsync();

        if (postedLines.Count == 0) return;

        var grouped = postedLines
            .Where(l => l.Account != null)
            .GroupBy(l => new { l.AccountId, l.Account!.AccountCode, l.Account.AccountName, l.Account.AccountType })
            .Select(g => new
            {
                g.Key.AccountId,
                g.Key.AccountCode,
                g.Key.AccountName,
                g.Key.AccountType,
                TotalDebit = g.Sum(l => l.DebitAmount),
                TotalCredit = g.Sum(l => l.CreditAmount)
            })
            .Where(g => g.TotalDebit != 0 || g.TotalCredit != 0)
            .ToList();

        if (grouped.Count == 0) return;

        var closingLines = new List<JournalEntryLine>();

        foreach (var acct in grouped)
        {
            if (acct.AccountType == AccountType.Revenue)
            {
                var netCredit = acct.TotalCredit - acct.TotalDebit;
                if (netCredit == 0) continue;
                closingLines.Add(new JournalEntryLine
                {
                    AccountId = acct.AccountId,
                    DebitAmount = netCredit > 0 ? netCredit : 0,
                    CreditAmount = netCredit < 0 ? Math.Abs(netCredit) : 0,
                    Description = $"ปิดบัญชี {acct.AccountCode} {acct.AccountName}"
                });
            }
            else
            {
                var netDebit = acct.TotalDebit - acct.TotalCredit;
                if (netDebit == 0) continue;
                closingLines.Add(new JournalEntryLine
                {
                    AccountId = acct.AccountId,
                    DebitAmount = netDebit < 0 ? Math.Abs(netDebit) : 0,
                    CreditAmount = netDebit > 0 ? netDebit : 0,
                    Description = $"ปิดบัญชี {acct.AccountCode} {acct.AccountName}"
                });
            }
        }

        if (closingLines.Count == 0) return;

        var totalClosingDebit = closingLines.Sum(l => l.DebitAmount);
        var totalClosingCredit = closingLines.Sum(l => l.CreditAmount);
        var netToRE = totalClosingCredit - totalClosingDebit;

        closingLines.Add(new JournalEntryLine
        {
            AccountId = retainedEarningsAccount.Id,
            DebitAmount = netToRE > 0 ? netToRE : 0,
            CreditAmount = netToRE < 0 ? Math.Abs(netToRE) : 0,
            Description = "ปิดกำไร(ขาดทุน)สุทธิเข้ากำไรสะสม"
        });

        var finalDebit = closingLines.Sum(l => l.DebitAmount);
        var finalCredit = closingLines.Sum(l => l.CreditAmount);
        if (Math.Abs(finalDebit - finalCredit) > 0.01m)
            throw new InvalidOperationException(
                $"Closing entries ไม่สมดุล: Dr={finalDebit:N2} Cr={finalCredit:N2}");

        var yearMonth = period.EndDate.ToString("yyyyMM");
        var entryNumber = $"CL-{yearMonth}-{Guid.NewGuid().ToString()[..4].ToUpper()}";

        var closingEntry = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = period.EndDate,
            JournalType = JournalType.General,
            Description = $"ปิดบัญชีรายได้/ค่าใช้จ่ายประจำงวด {period.Name}",
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            TotalDebit = finalDebit,
            TotalCredit = finalCredit,
            CreatedBy = "System",
            FiscalPeriodId = period.Id
        };

        for (var i = 0; i < closingLines.Count; i++)
        {
            closingLines[i].JournalEntryId = closingEntry.Id;
            closingLines[i].LineOrder = i + 1;
            closingEntry.Lines.Add(closingLines[i]);
        }

        _db.JournalEntries.Add(closingEntry);
    }

    // ==================== Helpers ====================

    private static AccountResponse MapAccountToResponse(ChartOfAccount a) => new(
        a.Id, a.AccountCode, a.AccountName, a.AccountNameEn,
        a.AccountType, (int)a.AccountType, a.ParentAccountId, a.Level, a.IsActive, a.IsSystemAccount, a.Description,
        a.InputVatClaimable,
        a.CostBehavior);

    private static JournalEntryResponse MapJournalEntryToResponse(JournalEntry j) =>
        MapJournalEntryToResponse(j, null, null);

    private static JournalEntryResponse MapJournalEntryToResponse(JournalEntry j, string? srcDocNumber, string? srcDocType) => new(
        j.Id, j.EntryNumber, j.EntryDate, j.JournalType, j.Description, j.Reference,
        j.Status, j.IsAutoGenerated, j.TotalDebit, j.TotalCredit,
        j.Lines.OrderBy(l => l.LineOrder).Select(l => new JournalLineResponse(
            l.Id, l.AccountId, l.Account.AccountCode, l.Account.AccountName,
            l.DebitAmount, l.CreditAmount, l.Description, l.LineOrder,
            l.ProjectId, null, l.BranchId, l.DimensionId, l.Tags)).ToList(),
        j.CreatedAt, j.ReversedByEntryId, j.OriginalEntryId,
        j.ProjectId, j.Project?.Name, j.BranchId, j.DimensionId, j.Note, j.Tags,
        j.SourceDocumentId, srcDocNumber, srcDocType,
        j.Sensitivity);

    /// <summary>Replace amounts/lines/description with a redacted placeholder
    /// when the user can't see the JE. We keep entry number + dates so the GL
    /// page can still show a "[ซ่อน]" row in chronological order — the totals
    /// the user sees won't match the GL totals, which is expected.</summary>
    private static JournalEntryResponse RedactJournalEntry(JournalEntry j, string reason) => new(
        j.Id, j.EntryNumber, j.EntryDate, j.JournalType, "[ซ่อน] " + reason, null,
        j.Status, j.IsAutoGenerated, 0, 0, new List<JournalLineResponse>(), j.CreatedAt,
        Sensitivity: j.Sensitivity, IsRedacted: true, RedactedReason: reason);

    private static JournalEntryResponse RedactJournalEntry(JournalEntryResponse je, string reason) => new(
        je.Id, je.EntryNumber, je.EntryDate, je.JournalType, "[ซ่อน] " + reason, null,
        je.Status, je.IsAutoGenerated, 0, 0, new List<JournalLineResponse>(), je.CreatedAt,
        Sensitivity: je.Sensitivity, IsRedacted: true, RedactedReason: reason);

    private async Task<Dictionary<Guid, (string Number, string Type)>> LoadSourceDocumentMapAsync(IEnumerable<JournalEntry> entries)
    {
        var ids = entries.Where(e => e.SourceDocumentId.HasValue)
            .Select(e => e.SourceDocumentId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, (string, string)>();
        var docs = await _db.Documents
            .Where(d => ids.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType })
            .ToListAsync();
        return docs.ToDictionary(d => d.Id, d => (d.DocumentNumber, d.DocumentType.ToString()));
    }

    private static FiscalPeriodResponse MapPeriodToResponse(FiscalPeriod f) => new(
        f.Id, f.Name, f.Year, f.Month, f.StartDate, f.EndDate, f.Status);

    private async Task<string> GetNextEntryNumberAsync(Guid companyId, string prefix)
    {
        var yearMonth = DateTime.UtcNow.ToString("yyyyMM");
        var pattern = $"{prefix}-{yearMonth}-";

        // Advisory lock to prevent duplicate entry numbers under concurrency
        var lockKey = Math.Abs($"je_number_{companyId}_{pattern}".GetHashCode());
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", lockKey);

        var lastEntry = await _db.JournalEntries
            .IgnoreQueryFilters()
            .Where(j => j.CompanyId == companyId && j.EntryNumber.StartsWith(pattern))
            .OrderByDescending(j => j.EntryNumber)
            .Select(j => j.EntryNumber)
            .FirstOrDefaultAsync();

        int nextSeq = 1;
        if (lastEntry != null)
        {
            var lastPart = lastEntry[pattern.Length..];
            if (int.TryParse(lastPart, out var lastNum))
                nextSeq = lastNum + 1;
        }

        return $"{pattern}{nextSeq:D4}";
    }

    private static DateTime NormalizeDate(DateTime date)
    {
        if (date.Year < 1900)
            return date.AddYears(543);
        if (date.Year > 2400)
            return new DateTime(date.Year - 543, date.Month, date.Day, date.Hour, date.Minute, date.Second, date.Kind);
        return date;
    }

    private async Task ValidateFiscalPeriodOpenAsync(Guid? fiscalPeriodId)
    {
        if (!fiscalPeriodId.HasValue) return;
        var period = await _db.FiscalPeriods.FindAsync(fiscalPeriodId.Value);
        if (period is { Status: FiscalPeriodStatus.Closed or FiscalPeriodStatus.Locked })
            throw new InvalidOperationException("ไม่สามารถดำเนินการได้เนื่องจากงวดบัญชีปิดแล้ว");
    }
}
