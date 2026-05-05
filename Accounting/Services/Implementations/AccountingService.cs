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
        // Payment channel accounts: cash, bank deposits, director advance, e-wallet, etc.
        // These are GL accounts used as money source/destination in transactions.
        var prefixes = new[] { "111", "112", "115", "119", "219" };
        var accounts = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive
                && a.Level >= 3
                && prefixes.Any(p => a.AccountCode.StartsWith(p)))
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

            // Re-sync support: void existing posted journals linked to this source document
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
                    old.Status = JournalEntryStatus.Voided;
                    old.UpdatedAt = DateTime.UtcNow;
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

        await ValidateFiscalPeriodOpenAsync(entry.FiscalPeriodId);

        if (entry.Status == JournalEntryStatus.Posted)
        {
            // Posted JE → must create reversal entry (proper double-entry accounting)
            await ReverseJournalEntryAsync(companyId, entryId,
                reversalDate: DateTime.UtcNow.Date,
                description: $"ยกเลิกใบสำคัญ {entry.EntryNumber}");
            // ReverseJournalEntryAsync sets the original to Reversed; nothing more to do here.
            return;
        }

        // Draft / unposted → simple status flip is fine; no GL impact yet
        entry.Status = JournalEntryStatus.Voided;
        entry.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// ลบใบสำคัญ — เฉพาะ Draft ที่ยังไม่ Posted เท่านั้น (audit-safe)
    /// Posted/Reversed ใช้ "ยกเลิก" (VoidJournalEntryAsync) ซึ่งจะสร้าง reversal ให้
    /// </summary>
    public async Task DeleteJournalEntryAsync(Guid companyId, Guid entryId)
    {
        var entry = await _db.JournalEntries
            .Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        if (entry.Status == JournalEntryStatus.Posted)
            throw new InvalidOperationException(
                "ไม่สามารถลบใบสำคัญที่ Posted แล้วได้ — กรุณาใช้คำสั่ง 'ยกเลิก' " +
                "เพื่อสร้างรายการกลับบัญชีตามมาตรฐาน (รักษา audit trail)");

        if (entry.Status == JournalEntryStatus.Reversed)
            throw new InvalidOperationException("ไม่สามารถลบรายการที่ถูกกลับรายการแล้ว");

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

    public async Task<JournalEntryResponse> ReverseJournalEntryAsync(Guid companyId, Guid entryId, DateTime? reversalDate = null, string? description = null)
    {
        var original = await _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        if (original.Status != JournalEntryStatus.Posted)
            throw new InvalidOperationException("สามารถกลับรายการได้เฉพาะใบสำคัญที่มีสถานะ Posted เท่านั้น");

        if (original.ReversedByEntryId.HasValue)
            throw new InvalidOperationException("รายการนี้ถูกกลับรายการไปแล้ว");

        await ValidateFiscalPeriodOpenAsync(original.FiscalPeriodId);

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
                IsAutoGenerated = original.IsAutoGenerated,
                OriginalEntryId = original.Id,
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
                f.EndDate >= effectiveDate &&
                f.Status == FiscalPeriodStatus.Open);
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

            original.Status = JournalEntryStatus.Reversed;
            original.ReversedByEntryId = reversal.Id;
            original.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            if (transaction != null) await transaction.CommitAsync();

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

    public async Task<int> BatchVoidJournalEntriesAsync(Guid companyId, List<Guid> entryIds)
    {
        var entries = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && entryIds.Contains(j.Id) && j.Status != JournalEntryStatus.Voided)
            .ToListAsync();

        foreach (var entry in entries)
        {
            entry.Status = JournalEntryStatus.Voided;
            entry.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return entries.Count;
    }

    public async Task<int> BatchDeleteJournalEntriesAsync(Guid companyId, List<Guid> entryIds)
    {
        var entries = await _db.JournalEntries
            .Include(j => j.Lines)
            .Where(j => j.CompanyId == companyId && entryIds.Contains(j.Id)
                && j.Status != JournalEntryStatus.Reversed
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

        foreach (var entry in entries)
        {
            entry.Status = JournalEntryStatus.Posted;
            entry.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return entries.Count;
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
            .Where(l => l.TransactionDate.Year < 1900)
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
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("112") && a.IsActive && !a.IsDeleted);
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

    public async Task<TrialBalanceResponse> GetTrialBalanceAsync(Guid companyId, DateTime asOfDate, Guid? projectId = null, Guid? branchId = null, Guid? dimensionId = null)
    {
        var postedEntryIds = _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                && j.EntryDate < asOfDate.Date.AddDays(1))
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

        var grouped = postedLines
            .Where(l => l.Account != null)
            .GroupBy(l => new { l.AccountId, l.Account.AccountCode, l.Account.AccountName, l.Account.AccountType })
            .Select(g => new TrialBalanceItem(
                g.Key.AccountCode,
                g.Key.AccountName,
                g.Key.AccountType,
                (int)g.Key.AccountType,
                g.Sum(l => l.DebitAmount),
                g.Sum(l => l.CreditAmount)))
            .OrderBy(i => i.AccountCode)
            .ToList();

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
            .Where(l => l.Account.AccountCode.StartsWith("221"))
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
        a.AccountType, (int)a.AccountType, a.ParentAccountId, a.Level, a.IsActive, a.IsSystemAccount, a.Description);

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
        j.SourceDocumentId, srcDocNumber, srcDocType);

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
