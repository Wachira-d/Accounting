using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

public partial class AccountingService : IAccountingService
{
    private readonly AccountingDbContext _db;
    private readonly IBankService? _bankService;
    private readonly ISensitivityService? _sensitivity;
    // optional — cleanup ที่เป็น best-effort ต้อง log ได้ ห้ามเงียบสนิท
    // (ค่า default null เพื่อไม่กระทบจุดที่ new ตรง ๆ ในเทสต์/สคริปต์)
    private readonly ILogger<AccountingService>? _logger;

    public AccountingService(AccountingDbContext db, IBankService? bankService = null,
        ISensitivityService? sensitivity = null, ILogger<AccountingService>? logger = null)
    {
        _db = db;
        _bankService = bankService;
        _sensitivity = sensitivity;
        _logger = logger;
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
        // รอบ 206: บัญชีคุม = มีบัญชีย่อย (PostingAccountRule) — ส่งให้หน้าเว็บแทนการกรอง level >= 4 เอง
        var parents = (await _db.ChartOfAccounts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted && c.ParentAccountId != null)
                .Select(c => c.ParentAccountId!.Value).Distinct().ToListAsync())
            .ToHashSet();

        return accounts.Select(a => MapAccountToResponse(a) with { HasChildAccounts = parents.Contains(a.Id) }).ToList();
    }

    /// <summary>ช่องทางเงินสำหรับ dropdown — กติกา "ที่อยู่ของเงิน" ตัวเดียวที่ <see cref="Accounting.Helpers.MoneyAccountRule"/>
    /// (ผ่าน <see cref="MoneyAccountDirectory"/>) · เดิมเขียน prefix 111/1133/2123 + ตัด 1112 ไว้ที่นี่ และหน้าเว็บ 5 หน้ากรอง '1112' ซ้ำเอง
    /// (PC#11) · includeBank=false (ค่าเดิม) = เฉพาะกลุ่ม "other" (หน้าเดิมที่ยังกรอง '1112' ได้ผลเท่าเดิม) · true = ทุกแถว + ป้ายกลุ่ม</summary>
    public Task<List<PaymentChannelAccountResponse>> GetPaymentChannelAccountsAsync(Guid companyId, bool includeBank = false)
    {
        return _db.GetPaymentChannelsAsync(companyId, includeBank);
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

    /// <summary>ด่านเข้มของใบสำคัญที่ผู้ใช้บันทึกเอง (POST journals) — ผังทุกบรรทัด: ของบริษัทนี้ · เปิดใช้ · ไม่ใช่บัญชีคุม
    /// (ตัวตัดสินเดียว <see cref="Accounting.Helpers.PostingAccountRule"/>) + สาขา/มิติ/โครงการของบริษัทนี้ (2026-10-14 · ID#24) ·
    /// แยกจาก <see cref="CreateJournalEntryAsync"/> เพราะทางอัตโนมัติ (ปิดงวดสิ้นปี) ต้องล้างยอดผังที่ปิดใช้แล้วได้</summary>
    public Task ValidateManualJournalAsync(Guid companyId, CreateJournalEntryRequest request)
        => ValidateJournalReferencesAsync(companyId, request.Lines, request.BranchId, request.DimensionId, request.ProjectId, strictAccounts: true);

    /// <summary>ตรวจการอ้างอิงของใบสำคัญ — ผัง (ไม่พบ = บล็อกเสมอ · เปิดใช้/บัญชีคุม เมื่อ <paramref name="strictAccounts"/>) และ
    /// สาขา/มิติ/โครงการ ทั้งระดับหัวและรายบรรทัด ต้องเป็นของบริษัทนี้ · ข้อความบอกบรรทัด + ที่แก้ (ไม่มี GUID)</summary>
    private async Task ValidateJournalReferencesAsync(Guid companyId, IReadOnlyList<JournalLineRequest> lines,
        Guid? branchId, Guid? dimensionId, Guid? projectId, bool strictAccounts,
        IReadOnlyCollection<Guid>? allowDeletedAccountIds = null)
    {
        var facts = await _db.LoadPostingFactsAsync(companyId, lines.Select(l => l.AccountId));
        for (var i = 0; i < lines.Count; i++)
        {
            facts.TryGetValue(lines[i].AccountId, out var f);
            var problem = Accounting.Helpers.PostingAccountRule.Check(f);
            // รอบ 4 P2-b (ฝ่ายค้านรอบส่งออก): ใบปิดปีต้องล้างผังรายได้/ค่าใช้จ่ายที่ถูกลบแบบ soft แต่มีรายการในรอบ — ผู้เรียก (YearEndCloseAsync)
            // ส่งเฉพาะชุดที่ตรวจแล้วว่าเป็นผังของบริษัทนี้ที่ถูกลบ · ทางอื่นทั้งหมดไม่ส่ง ⇒ ผังที่ลบยังลงรายการไม่ได้เหมือนเดิม
            if (problem == Accounting.Helpers.PostingAccountProblem.NotFound && allowDeletedAccountIds != null
                && allowDeletedAccountIds.Contains(lines[i].AccountId)) continue;
            if (!strictAccounts && problem != Accounting.Helpers.PostingAccountProblem.NotFound) continue;
            var msg = Accounting.Helpers.PostingAccountRule.Message(problem, f, $"บรรทัดที่ {i + 1}");
            if (msg != null) throw new Accounting.Helpers.BusinessRuleException(msg, Accounting.Helpers.PostingAccountRule.RuleCode);
        }

        static List<Guid> Ids(Guid? head, IEnumerable<Guid?> perLine)
            => perLine.Append(head).Where(g => g.HasValue && g.Value != Guid.Empty).Select(g => g!.Value).Distinct().ToList();

        // ฝ่ายค้านรอบ 2 P2(d): ทางอัตโนมัติ/ทางที่ยกค่าเดิม (strictAccounts=false — รายการประจำ · แก้ JE · ปิดงวด) ตรวจแค่ "เป็นของบริษัทนี้"
        // รวมแถวที่ถูกลบแบบ soft (สาขา/โครงการ/มิติที่บันทึกไว้แล้วถูกลบทีหลังเคยทำให้รายการประจำ/แก้ JE ล้ม) · ทางบันทึกมือ = เลือกใหม่ต้องยังไม่ถูกลบ
        var branches = strictAccounts ? _db.Branches.AsNoTracking() : _db.Branches.IgnoreQueryFilters().AsNoTracking();
        var dimensions = strictAccounts ? _db.AccountingDimensions.AsNoTracking() : _db.AccountingDimensions.IgnoreQueryFilters().AsNoTracking();
        var projects = strictAccounts ? _db.Projects.AsNoTracking() : _db.Projects.IgnoreQueryFilters().AsNoTracking();
        var branchIds = Ids(branchId, lines.Select(l => l.BranchId));
        if (branchIds.Count > 0)
        {
            var ok = await branches.CountAsync(b => b.CompanyId == companyId && branchIds.Contains(b.Id));
            if (ok != branchIds.Count)
                throw new Accounting.Helpers.BusinessRuleException(
                    "ไม่พบสาขาที่เลือกในบริษัทนี้ (อาจถูกลบหรือมาจากบริษัทอื่น) — เลือกสาขาใหม่จากรายการ", "GL-JOURNAL-BRANCH");
        }
        var dimIds = Ids(dimensionId, lines.Select(l => l.DimensionId));
        if (dimIds.Count > 0)
        {
            var ok = await dimensions.CountAsync(d => d.CompanyId == companyId && dimIds.Contains(d.Id));
            if (ok != dimIds.Count)
                throw new Accounting.Helpers.BusinessRuleException(
                    "ไม่พบศูนย์ต้นทุน/มิติบัญชีที่เลือกในบริษัทนี้ (อาจถูกลบหรือมาจากบริษัทอื่น) — เลือกใหม่จากรายการ", "GL-JOURNAL-DIMENSION");
        }
        var projIds = Ids(projectId, lines.Select(l => l.ProjectId));
        if (projIds.Count > 0)
        {
            var ok = await projects.CountAsync(p => p.CompanyId == companyId && projIds.Contains(p.Id));
            if (ok != projIds.Count)
                throw new Accounting.Helpers.BusinessRuleException(
                    "ไม่พบโครงการที่เลือกในบริษัทนี้ (อาจถูกลบหรือมาจากบริษัทอื่น) — เลือกโครงการใหม่จากรายการ", "GL-JOURNAL-PROJECT");
        }
    }

    public async Task<JournalEntryResponse> CreateJournalEntryAsync(Guid companyId, CreateJournalEntryRequest request, string createdBy)
    {
        var entryId = await CreateJournalEntryCoreAsync(companyId, request, createdBy, null);
        return await GetJournalEntryAsync(companyId, entryId);
    }

    /// <summary>ตัวสร้างใบสำคัญ (คืน Id) · <paramref name="allowDeletedAccountIds"/> = ผังที่ถูกลบซึ่งใบปิดปีต้องล้าง (รอบ 4 P2-b — ผู้เรียกเดียว
    /// <c>YearEndCloseAsync</c> · ผู้เรียกอื่นส่ง null) · ไม่ map คำตอบ (บรรทัดของผังที่ลบไม่มี Account ให้ map)</summary>
    private async Task<Guid> CreateJournalEntryCoreAsync(Guid companyId, CreateJournalEntryRequest request, string createdBy,
        IReadOnlyCollection<Guid>? allowDeletedAccountIds)
    {
        // Validate at least 2 lines (double-entry)
        if (request.Lines.Count < 2)
            throw new InvalidOperationException("ต้องมีรายการอย่างน้อย 2 รายการ (ระบบบัญชีคู่)");

        // Validate EntryDate is not in the future
        if (request.EntryDate > DateTime.UtcNow.Date.AddDays(1))
            throw new InvalidOperationException("วันที่ลงบัญชีต้องไม่เป็นวันที่ในอนาคต");

        // Validate each line — ผัง/สาขา/มิติ/โครงการต้องเป็นของบริษัทนี้ (2026-10-14 · ID#18/#24: เดิมข้อความโชว์ GUID ดิบ
        // และสาขา/มิติ/โครงการรายบรรทัดไม่ถูกตรวจ tenant) · ทางอัตโนมัติไม่ตรวจ "เปิดใช้/บัญชีคุม" (ปิดงวดสิ้นปีต้องล้างผังที่ปิดใช้แล้วได้)
        // — ด่านเข้มของการบันทึกด้วยมือคือ ValidateManualJournalAsync ที่ endpoint เรียกก่อน
        await ValidateJournalReferencesAsync(companyId, request.Lines, request.BranchId, request.DimensionId, request.ProjectId, strictAccounts: false,
            allowDeletedAccountIds: allowDeletedAccountIds);

        foreach (var line in request.Lines)
        {
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

        // เข้าร่วม transaction ที่ caller เปิดไว้ (ProcessPaymentAsync/Settle SSO
        // ฯลฯ ครอบ JE creation ด้วย tx ของตัวเอง) — ถ้ามี ambient tx อยู่แล้ว
        // ห้ามเปิดใหม่ (Npgsql: "connection is already in a transaction"). เปิด
        // เองเฉพาะตอนถูกเรียกเดี่ยว ๆ. commit เฉพาะ tx ที่เราเปิดเอง.
        var existingTransaction = _db.Database.CurrentTransaction;
        await using var transaction = existingTransaction == null
            ? await _db.Database.BeginTransactionAsync()
            : null;
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
            if (transaction != null) await transaction.CommitAsync();

            return entry.Id;
        }
        catch
        {
            if (transaction != null) await transaction.RollbackAsync();
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

    // เหตุผลการปิดบัง — ตัวตั้งเดียวกับบัญชีแยกประเภท (Helpers/LedgerRedaction · ฝ่ายค้านรอบส่งออก P2-7)
    private static string SensitivityReason(SensitivityKind kind) => Accounting.Helpers.LedgerRedaction.Reason(kind);

    /// <summary>บัญชีแยกประเภทตามสิทธิ์ผู้ใช้ (ฝ่ายค้านรอบส่งออก 2026-10-15 P2-7) — ใบสำคัญที่สมุดรายวันปิดบังสำหรับผู้ใช้คนนี้
    /// (ตัวตัดสินเดียวกัน: <c>ISensitivityService.GetVisibleKindsAsync</c>) ถูกรวมเป็นแถวเดียวต่อใบ + "[ซ่อน]" ตาม
    /// <see cref="Accounting.Helpers.LedgerRedaction"/> · ยอดยกมา/ยกไปไม่เปลี่ยน (ต้องเท่างบทดลอง) · หน้า + ไฟล์ส่งออกเรียกตัวนี้</summary>
    public async Task<GeneralLedgerResponse> GetGeneralLedgerForUserAsync(Guid companyId, Guid userId, DateTime fromDate, DateTime toDate,
        Guid? accountId = null, Guid? dimensionId = null, Guid? branchId = null, Guid? projectId = null)
    {
        var gl = await GetGeneralLedgerAsync(companyId, fromDate, toDate, accountId, dimensionId, branchId, projectId);
        if (_sensitivity == null) return gl;
        var entryIds = gl.Accounts.SelectMany(a => a.Transactions).Select(t => t.JournalEntryId).Distinct().ToList();
        if (entryIds.Count == 0) return gl;
        var sensitive = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && entryIds.Contains(j.Id) && j.Sensitivity != SensitivityKind.None)
            .Select(j => new { j.Id, j.Sensitivity })
            .ToListAsync();
        if (sensitive.Count == 0) return gl;
        var visible = await _sensitivity.GetVisibleKindsAsync(companyId, userId);
        var hidden = sensitive.Where(x => !visible.Contains(x.Sensitivity)).ToDictionary(x => x.Id, x => x.Sensitivity);
        return Accounting.Helpers.LedgerRedaction.Redact(gl, hidden);
    }

    public async Task<JournalEntryResponse> GetJournalEntryAsync(Guid companyId, Guid entryId)
    {
        // ฝ่ายค้านรอบส่งออก รอบ 5 P1: ไม่ ThenInclude ผัง (INNER JOIN ผ่านตัวกรอง soft-delete ⇒ บรรทัดผังที่ลบหาย — ใบปิดปีโชว์ Dr 8,000/Cr 7,000)
        // ⇒ โหลดบรรทัดทั้งหมดแล้วเติมผังรวมผังที่ลบ (Helpers/JournalLineAccounts)
        var entry = await _db.JournalEntries
            .Include(j => j.Lines)
            .Include(j => j.Project)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");
        await Accounting.Helpers.JournalLineAccounts.HydrateAsync(_db, companyId, entry);

        var srcMap = await LoadSourceDocumentMapAsync(companyId, new[] { entry });
        string? srcNumber = null, srcType = null;
        if (entry.SourceDocumentId.HasValue && srcMap.TryGetValue(entry.SourceDocumentId.Value, out var info))
        { srcNumber = info.Number; srcType = info.Type; }
        return MapJournalEntryToResponse(entry, srcNumber, srcType);
    }

    public async Task<PagedResponse<JournalEntryResponse>> GetJournalEntriesAsync(Guid companyId, PagedRequest request, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, string? journalType = null, Guid? dimensionId = null, Guid? branchId = null, Guid? projectId = null, string? tag = null, Guid? sourceDocumentId = null, string? sourceDocumentNumber = null)
    {
        // รอบ 5 P1: บรรทัดผังที่ลบต้องอยู่ในรายการด้วย (ไม่ ThenInclude ผัง · เติมหลังแบ่งหน้า)
        var query = _db.JournalEntries
            .Include(j => j.Lines)
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
        {
            // ค้นแบบ %...% ครอบ เลขที่รายการ + คำอธิบาย + อ้างอิง (เดิมขาด Reference →
            // ค้นด้วยเลขเอกสารต้นทาง เช่น REC-... ที่โชว์คอลัมน์ "อ้างอิง" ไม่เจอ)
            var s = request.Search.Trim();
            query = query.Where(j => j.EntryNumber.Contains(s)
                || (j.Description != null && j.Description.Contains(s))
                || (j.Reference != null && j.Reference.Contains(s)));
        }

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

        // ตัวกรองสาขา — กรองจาก BranchId บนหัว JE หรือบนบรรทัด ให้ตรงกับที่
        // GL / งบทดลอง / งบกำไรขาดทุน ใช้อยู่แล้ว
        //
        // ⚠️ ของเดิมกรองผ่าน `Branch.DimensionId` ซึ่ง **ไม่มีโค้ดตรงไหนในเรพเซ็ตเลย**
        // ⇒ `branchDimId` เป็น null เสมอ ⇒ เงื่อนไขทั้งก้อนถูกข้าม ⇒ คืน "ทุกแถว"
        // เหมือนไม่ได้กรอง — ตัวกรองสาขาในหน้าสมุดรายวันจึงไม่เคยกรองอะไรเลย
        // ตั้งแต่เขียนมา (silent no-op: ผู้ใช้เลือกสาขาแล้วเห็นรายการของทุกสาขา
        // โดยไม่มีอะไรบอก) — คงเส้นทางมิติไว้เป็น fallback ให้ข้อมูลเก่าที่เคย
        // ผูกสาขาเข้ากับมิติด้วยมือยังกรองเจอ
        if (branchId.HasValue)
        {
            var bId = branchId.Value;
            var branchDimId = await _db.Set<Branch>()
                .Where(b => b.Id == bId && b.CompanyId == companyId)
                .Select(b => b.DimensionId)
                .FirstOrDefaultAsync();

            var branchLineEntryIds = _db.JournalEntryLines
                .Where(l => l.BranchId == bId)
                .Select(l => l.JournalEntryId);

            if (branchDimId.HasValue)
            {
                var bLineIds = _db.Set<JournalLineDimension>()
                    .Where(d => d.DimensionId == branchDimId.Value)
                    .Select(d => d.JournalEntryLineId);
                var bEntryIds = _db.JournalEntryLines
                    .Where(l => bLineIds.Contains(l.Id))
                    .Select(l => l.JournalEntryId);
                query = query.Where(j => j.BranchId == bId
                    || branchLineEntryIds.Contains(j.Id)
                    || bEntryIds.Contains(j.Id));
            }
            else
            {
                query = query.Where(j => j.BranchId == bId || branchLineEntryIds.Contains(j.Id));
            }
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(j => j.EntryDate)
            .ThenByDescending(j => j.CreatedAt)
            .ThenBy(j => j.Id)   // ฝ่ายค้านรอบส่งออก P2-9: ลำดับตายตัว — ส่งออกวนทุกหน้า (CreatedAt ซ้ำได้ เช่น ลงพร้อมกันในธุรกรรมเดียว) ไม่ซ้ำ/หาย
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();
        await Accounting.Helpers.JournalLineAccounts.HydrateAsync(_db, companyId, items);

        var srcMap = await LoadSourceDocumentMapAsync(companyId, items);
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
            .Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");
        await Accounting.Helpers.JournalLineAccounts.HydrateAsync(_db, companyId, entry);   // รอบ 5 P1: บรรทัดผังที่ลบไม่หายจากคำตอบ

        // Idempotent: CreateJournalEntryAsync สร้าง JE เป็น Posted ตั้งแต่แรกแล้ว
        // (ไม่มีขั้น Draft แยก) — ผู้เรียกหลายที่ (payroll pay / settle SSO /
        // severance / นำส่งภาษี) เรียก Post ตามหลัง Create เป็น pattern เดิม.
        // ถ้า Posted อยู่แล้ว → no-op สำเร็จ (ไม่ throw) เพื่อให้ pattern นั้นทำงาน
        // ได้. ยังคงโยน error สำหรับสถานะอื่น (Voided/Reversed) ที่ post ไม่ได้.
        if (entry.Status == JournalEntryStatus.Posted)
            return MapJournalEntryToResponse(entry);
        if (entry.Status != JournalEntryStatus.Draft)
            throw new InvalidOperationException("สามารถ post ได้เฉพาะใบสำคัญที่เป็น Draft เท่านั้น");

        // งวดต้องเปิด — เดิมเช็คเฉพาะ Closed ทำให้ **Locked ยัง post ได้** (งวดที่
        // ยื่นแบบไปแล้วถูกล็อกด้วยสถานะนี้) และเช็คเฉพาะเมื่อมี FK ⇒ JE ที่ยังไม่มี
        // งวดผูกลอดทั้งด่าน. ยึดวันที่เป็นหลักให้ตรงกับ Update/Void (X-9)
        await ValidateFiscalPeriodOpenAsync(entry.FiscalPeriodId);
        await ValidateFiscalPeriodOpenForDateAsync(companyId, entry.EntryDate);

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
        // ยึดวันที่เป็นหลักด้วย — FK ที่เก็บไว้เป็น null ได้ (JE ที่ลงตอนยังไม่มีงวด)
        // แล้วด่านข้างบนจะปล่อยผ่านทั้งที่งวดของวันนั้นถูกปิดไปแล้ว (X-9)
        await ValidateFiscalPeriodOpenForDateAsync(companyId, entry.EntryDate);

        // Scalar updates (only when caller actually sent the field).
        if (request.EntryDate.HasValue)
        {
            if (request.EntryDate.Value > DateTime.UtcNow.Date.AddDays(1))
                throw new InvalidOperationException("วันที่ลงบัญชีต้องไม่เป็นวันที่ในอนาคต");
            var newDate = NormalizeDate(request.EntryDate.Value);
            if (newDate.Date != entry.EntryDate.Date)
            {
                // งวดปลายทางต้องเปิดด้วย ไม่งั้นย้ายรายการเข้างวดที่ปิดแล้วได้
                await ValidateFiscalPeriodOpenForDateAsync(companyId, newDate);
                // และ FK ต้องตามวันที่ไป ไม่งั้นรายการค้างชี้งวดเดิม → รายงานราย
                // งวดกับสมุดรายวันไม่ตรงกันอย่างถาวร
                entry.FiscalPeriodId = (await FindFiscalPeriodByDateAsync(companyId, newDate))?.Id;
            }
            entry.EntryDate = newDate;
        }
        if (request.Description != null) entry.Description = request.Description;
        if (request.Reference != null) entry.Reference = request.Reference;
        if (request.JournalType.HasValue) entry.JournalType = request.JournalType.Value;
        // สาขา/มิติ/โครงการระดับหัวต้องเป็นของบริษัทนี้ (ID#24 · เดิมไม่ตรวจ — ไม่มี FK ⇒ id บริษัทอื่นเก็บได้)
        await ValidateJournalReferencesAsync(companyId, Array.Empty<JournalLineRequest>(),
            request.BranchId, request.DimensionId, request.ProjectId, strictAccounts: false);
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

            // แก้ใบสำคัญได้เฉพาะทางมือ (ใบที่ผูกเอกสารถูกปฏิเสธข้างบน) ⇒ ด่านเข้ม: ของบริษัทนี้ · เปิดใช้ · ไม่ใช่บัญชีคุม (ID#24)
            // ฝ่ายค้านรอบ 2 P2(d): สาขา/มิติ/โครงการระดับหัวที่<b>ไม่ได้เปลี่ยน</b>ไม่ตรวจซ้ำแบบเข้ม (ตรวจ tenant แล้วข้างบน) — ของที่ถูกลบทีหลังเคยทำให้แก้บรรทัดไม่ได้
            await ValidateJournalReferencesAsync(companyId, request.Lines,
                request.BranchId, request.DimensionId, request.ProjectId,
                strictAccounts: true);

            foreach (var l in request.Lines)
            {
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

        // ฝ่ายค้านรอบส่งออก รอบ 5 P1: ใบปิดบัญชีสิ้นปียกเลิกไม่ได้ (ข้อความ + ทางไปต่อจากด่านตัวเดียว · ก่อนตรวจงวด — บอกเหตุที่แท้จริง)
        await Accounting.Helpers.ClosingEntryLock.EnsureNotClosingEntriesAsync(_db, companyId, new[] { entry.Id }, "ยกเลิก");

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
        await ValidateFiscalPeriodOpenForDateAsync(companyId, entry.EntryDate);

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

        // รอบ 5 P1: ลบใบปิดบัญชีสิ้นปี = กำไรไม่ถูกโอนแต่ปียัง "ปิดแล้ว" (YearEndClosing ชี้ใบที่ถูกลบ) — ด่านตัวเดียว
        await Accounting.Helpers.ClosingEntryLock.EnsureNotClosingEntriesAsync(_db, companyId, new[] { entry.Id }, "ลบ");

        await ValidateFiscalPeriodOpenAsync(entry.FiscalPeriodId);

        var toDelete = new List<JournalEntry> { entry };

        // ── รักษาคู่ "ใบเดิม ↔ ตัวกลับรายการ" ให้สอดคล้องเสมอ ──
        // (1) ลบ "ตัวกลับรายการ" (REV) → คืนสถานะใบเดิมเป็น Posted + ตัด link
        //     (เดิมใบเดิมค้าง Reversed ชี้หา JE ที่ถูกลบ → กลับรายการใหม่ก็ไม่ได้
        //     รายงานก็โชว์ "กลับรายการแล้ว" ทั้งที่ผลกลับหายไปจาก GL แล้ว)
        if (entry.OriginalEntryId.HasValue)
        {
            var original = await _db.JournalEntries.FirstOrDefaultAsync(j =>
                j.Id == entry.OriginalEntryId.Value && j.CompanyId == companyId && !j.IsDeleted);
            if (original != null && original.ReversedByEntryId == entry.Id)
            {
                original.ReversedByEntryId = null;
                if (original.Status == JournalEntryStatus.Reversed)
                    original.Status = JournalEntryStatus.Posted;
                original.UpdatedAt = DateTime.UtcNow;
            }
        }

        // (2) ลบ "ใบเดิมที่ถูกกลับรายการแล้ว" → ลบตัวกลับรายการตามไปด้วย —
        //     คู่นี้ net ศูนย์ ปล่อย REV ค้างเดี่ยวจะเหลือผลกลับรายการลอยใน GL
        if (entry.ReversedByEntryId.HasValue)
        {
            var rev = await _db.JournalEntries
                .Include(j => j.Lines)
                .FirstOrDefaultAsync(j => j.Id == entry.ReversedByEntryId.Value
                    && j.CompanyId == companyId && !j.IsDeleted);
            if (rev != null)
            {
                await ValidateFiscalPeriodOpenAsync(rev.FiscalPeriodId);
                toDelete.Add(rev);
            }
        }

        var lineIds = toDelete.SelectMany(e => e.Lines.Select(l => l.Id)).ToList();
        var dims = await _db.JournalLineDimensions
            .Where(d => lineIds.Contains(d.JournalEntryLineId))
            .ToListAsync();
        foreach (var d in dims)
        {
            d.IsDeleted = true;
            d.UpdatedAt = DateTime.UtcNow;
        }

        foreach (var e in toDelete)
        {
            foreach (var line in e.Lines)
            {
                line.IsDeleted = true;
                line.UpdatedAt = DateTime.UtcNow;
            }
            e.IsDeleted = true;
            e.UpdatedAt = DateTime.UtcNow;
        }
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
        // ฝ่ายค้านรอบส่งออก รอบ 5 P1: โหลดบรรทัด<b>ทั้งหมด</b> (ไม่ ThenInclude ผัง — INNER JOIN ผ่านตัวกรอง soft-delete ทิ้งบรรทัดผังที่ลบ ⇒ ตัวกลับไม่ลงตัว)
        var original = await _db.JournalEntries
            .Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");

        // รอบ 5 P1: ใบปิดบัญชีสิ้นปีกลับรายการไม่ได้ทุกทาง (รวม systemTriggered — ยกเลิก/ยกเลิกหลายใบ/แก้ไขด้วยการกลับรายการ) · ด่านตัวเดียว
        await Accounting.Helpers.ClosingEntryLock.EnsureNotClosingEntriesAsync(_db, companyId, new[] { original.Id }, "กลับรายการ");

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

        // ทีม S 2026-10-26 ทิศกลับ: ผู้ใช้กลับรายการ JE มัดจำของการจอง (เช่น มัดจำที่ระบบภายนอกลงเป็นสมุดรายวัน) ทั้งที่มี JE อื่นตัดมัดจำนั้นผ่านรายการอยู่ ⇒ 409
        // พร้อมทางไปต่อ · systemTriggered (ยกเลิกเอกสาร — ตรวจที่ VoidDocumentAsync แล้ว · resync ที่กลับแล้วลงใหม่ในคำสั่งเดียว) ไม่ตรวจซ้ำ ·
        // กลับ JE ที่ "ตัด" มัดจำได้เสมอ (ยอดคืน ไม่ติดลบ)
        if (!systemTriggered
            && await Accounting.Helpers.BookingDepositLedger.RemovalProblemAsync(_db, companyId, new[] { original.Id },
                new[] { original.Reference, original.Description }, $"กลับรายการ {original.EntryNumber}") is string drawnBlock)
            throw new Accounting.Helpers.BusinessRuleException(drawnBlock, Accounting.Helpers.BookingDepositDrawdown.RemovalRuleCode, 409);

        // รอบ 5 P1 (ด่านป้องกัน): ตัวกลับ = บรรทัดที่บันทึกไว้จริงทุกบรรทัด สลับด้าน และลงตัว — บรรทัดหายระหว่างโหลด (เช่น ตัวกรองผังที่ลบ)
        // = ล้มก่อนเขียน ไม่ใช่ตัวกลับไม่ลงตัวใน GL · เทียบกับยอดรวมบรรทัดในฐาน (ไม่ join ผัง) — ไม่เทียบหัวใบ เพราะใบเก่าบางทางไม่ได้ปรับหัวใบตามบรรทัด
        // (บล็อกยกเลิกเอกสารของใบเหล่านั้นโดยไม่มีทางไปต่อ) · ดู EXPORT_AUDIT รอบ 5
        var persisted = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntryId == original.Id)
            .GroupBy(l => 1)
            .Select(g => new { Count = g.Count(), Debit = g.Sum(l => l.DebitAmount), Credit = g.Sum(l => l.CreditAmount) })
            .FirstOrDefaultAsync();
        var reversalMismatch = Accounting.Helpers.JournalLineAccounts.ReversalMismatch(original.EntryNumber,
            original.Lines.Count, original.Lines.Sum(l => l.DebitAmount), original.Lines.Sum(l => l.CreditAmount),
            persisted?.Count ?? 0, persisted?.Debit ?? 0m, persisted?.Credit ?? 0m);
        if (reversalMismatch != null)
            throw new Accounting.Helpers.BusinessRuleException(reversalMismatch, Accounting.Helpers.JournalLineAccounts.ReversalRuleCode);

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
                // best-effort — cleanup ห้ามทำให้การกลับรายการที่ commit แล้วพัง
                // แต่ต้อง **ไม่เงียบสนิท**: ถ้า bank service ล้มเป็นระบบ อาการที่
                // เห็นคือ "bank txn ค้าง Matched กับ JE ที่ถูกกลับไปแล้ว" โดยไม่มี
                // ร่องรอยให้ตาม (อาการเดียวกับเคส ConfirmPayment ที่เคย catch{} เปล่า)
                // — จุดอื่นที่ทำงานเดียวกันใน DocumentService log ครบทุกจุดแล้ว
                try { await _bankService.UnwindGroupsContainingItemAsync(companyId, ReconciliationItemType.JournalEntry, original.Id); }
                catch (Exception ex)
                {
                    _logger?.LogError(ex,
                        "ปลดการจับคู่ธนาคารของใบสำคัญ {Entry} ไม่สำเร็จหลังกลับรายการ — " +
                        "รายการธนาคารอาจยังชี้ใบที่ถูกกลับแล้ว (ตรวจที่หน้ากระทบยอดธนาคาร)",
                        original.EntryNumber);
                }
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
        // ฝ่ายค้านรอบส่งออก รอบ 5 P1: บรรทัดทั้งหมด (รวมผังที่ลบ — เดิม ThenInclude ทิ้ง ⇒ ฉบับร่างที่คัดลอกขาดบรรทัด)
        var original = await _db.JournalEntries
            .Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.Id == entryId && j.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");
        await Accounting.Helpers.JournalLineAccounts.HydrateAsync(_db, companyId, original);
        await Accounting.Helpers.ClosingEntryLock.EnsureNotClosingEntriesAsync(_db, companyId, new[] { original.Id }, "แก้ไขด้วยการกลับรายการ");

        if (original.Status != JournalEntryStatus.Posted)
            throw new InvalidOperationException("สามารถแก้ไขด้วยการกลับรายการได้เฉพาะใบที่ Posted แล้วเท่านั้น");

        if (original.ReversedByEntryId.HasValue)
            throw new InvalidOperationException("รายการนี้ถูกกลับรายการไปแล้ว");

        // ใบที่ผูกเอกสารต้นทาง: ReverseJournalEntryAsync จะโยนทิ้งอยู่แล้วในขั้น
        // ถัดไป แต่ข้อความจะเป็นของ "กลับรายการ" ซึ่งไม่ตอบคำถามที่ผู้ใช้กำลังถาม
        // ("ลงผังบัญชีผิด จะแก้ตรงไหน") — ดักตั้งแต่ต้นทางพร้อมบอกทางที่ใช้ได้จริง
        // (UI ซ่อนปุ่มนี้ให้แล้ว กันไว้อีกชั้นสำหรับ API/สคริปต์)
        if (original.SourceDocumentId.HasValue)
            throw new InvalidOperationException(
                "รายการนี้ระบบลงให้อัตโนมัติจากเอกสารต้นทาง — แก้ที่สมุดรายวันไม่ได้ " +
                "เพราะบัญชีต้องตรงกับเอกสารเสมอ · ลงผังบัญชีผิด ให้เปิดเอกสารแล้วกด " +
                "'เปลี่ยนผัง' ท้ายบรรทัดที่ผิด (ระบบลงรายการย้ายบัญชีให้ในงวดเดิม) · " +
                "ถ้าไม่ควรมีรายการนี้เลย ให้ใช้ 'ยกเลิกเอกสาร'");

        // รอบ 5 P1: ฉบับร่างที่คัดลอกลงผังที่ถูกลบไม่ได้ (ด่านผังล้ม NotFound) — เดิมล้ม<b>หลัง</b>กลับรายการไปแล้ว ⇒ ใบเดิมถูกกลับแต่ไม่มีร่างให้แก้ ·
        // ล้มก่อนเขียนพร้อมทางไปต่อ
        var deletedOnLines = original.Lines.Where(l => l.Account.IsDeleted).Select(l => l.Account.AccountCode)
            .Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (deletedOnLines.Count > 0)
            throw new Accounting.Helpers.BusinessRuleException(
                $"แก้ไขด้วยการกลับรายการไม่ได้: ใบนี้มีบรรทัดลงผังที่ถูกลบแล้ว ({string.Join(", ", deletedOnLines)}) — ฉบับร่างที่คัดลอกจะลงผังที่ลบไม่ได้ · "
                + "ยังไม่มีอะไรถูกบันทึก · ทางไปต่อ: กดกลับรายการใบนี้ (ตัวกลับลงผังเดิมได้) แล้วบันทึกใบใหม่เอง หรือให้เจ้าของบริษัทกู้คืนผังนั้นก่อน "
                + "(หน้า \"ตรวจข้อมูลจากบั๊กที่แก้แล้ว\") แล้วแก้ไขอีกครั้ง",
                Accounting.Helpers.PostingAccountRule.RuleCode);

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
        // รอบ 5 P1: ตรวจทั้งชุดก่อนเริ่ม — ใบปิดบัญชีในชุด ⇒ ไม่ยกเลิกใบไหนเลย (เดิมจะกลับใบก่อนหน้าไปแล้วค่อยล้มที่ใบปิด)
        await Accounting.Helpers.ClosingEntryLock.EnsureNotClosingEntriesAsync(_db, companyId, allRequested.Select(j => j.Id).ToList(), "ยกเลิก");

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
        // รอบ 5 P1: ใบปิดบัญชีสิ้นปีลบไม่ได้ (เดิมทางหลายใบไม่ตรวจงวดเลย ⇒ ลบใบปิดปีทิ้งได้) — ตรวจทั้งชุดก่อนเริ่ม
        await Accounting.Helpers.ClosingEntryLock.EnsureNotClosingEntriesAsync(_db, companyId, entryIds, "ลบ");
        var entries = await _db.JournalEntries
            .Include(j => j.Lines)
            .Where(j => j.CompanyId == companyId && entryIds.Contains(j.Id)
                && !j.SourceDocumentId.HasValue)
            .ToListAsync();

        var deleteIds = entries.Select(e => e.Id).ToHashSet();

        // ── รักษาคู่ "ใบเดิม ↔ ตัวกลับรายการ" เหมือน delete เดี่ยว ──
        // ลบ REV → คืนสถานะใบเดิม (ถ้าใบเดิมไม่ได้ถูกลบในชุดเดียวกัน)
        var originalIds = entries
            .Where(e => e.OriginalEntryId.HasValue && !deleteIds.Contains(e.OriginalEntryId.Value))
            .Select(e => e.OriginalEntryId!.Value).Distinct().ToList();
        if (originalIds.Count > 0)
        {
            var originals = await _db.JournalEntries
                .Where(j => originalIds.Contains(j.Id) && j.CompanyId == companyId && !j.IsDeleted)
                .ToListAsync();
            foreach (var original in originals)
            {
                if (!original.ReversedByEntryId.HasValue || !deleteIds.Contains(original.ReversedByEntryId.Value))
                    continue;
                original.ReversedByEntryId = null;
                if (original.Status == JournalEntryStatus.Reversed)
                    original.Status = JournalEntryStatus.Posted;
                original.UpdatedAt = DateTime.UtcNow;
            }
        }

        // ลบใบเดิมที่ถูกกลับแล้ว → ลากตัวกลับรายการที่ไม่อยู่ในชุดมาลบด้วย (คู่ net ศูนย์)
        var danglingRevIds = entries
            .Where(e => e.ReversedByEntryId.HasValue && !deleteIds.Contains(e.ReversedByEntryId.Value))
            .Select(e => e.ReversedByEntryId!.Value).Distinct().ToList();
        if (danglingRevIds.Count > 0)
        {
            var revs = await _db.JournalEntries
                .Include(j => j.Lines)
                .Where(j => danglingRevIds.Contains(j.Id) && j.CompanyId == companyId && !j.IsDeleted)
                .ToListAsync();
            entries.AddRange(revs);
            foreach (var r in revs) deleteIds.Add(r.Id);
        }

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

        // ตัวกรองชุดเดียวกับงบทดลอง (ฝ่ายค้านรอบส่งออก 2026-10-15 P2-6 · ResolveLedgerFilterAsync) — เดิมบัญชีแยกประเภทแปลงสาขาเป็นมิติ
        // แล้วกรอง "สาขา และ มิติของสาขา" ขณะที่งบทดลองกรอง "สาขา" อย่างเดียวและไม่ดูการปันส่วนมิติ ⇒ ยอดยกไปสองรายงานไม่ต่อกัน
        // · รอบ 2 P2-5: ยอดของบรรทัดที่ปันส่วน = ส่วนที่ปันเข้าตัวกรอง (Helpers/LedgerFilterRule — ไม่ใช่เต็มบรรทัดในทุกมิติ)
        var filter = await ResolveLedgerFilterAsync(companyId, accountId, projectId, branchId, dimensionId);

        // Query posted + reversed entry IDs. Reversed entries are historically valid
        // transactions that were later offset by a reversal JE — both must appear in
        // the ledger for the running balance to net to zero after a void.
        var postedEntryIds = _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                && j.EntryDate >= fromDateStart
                && j.EntryDate < toDateEnd)
            .Select(j => j.Id);

        // บรรทัดในช่วง (ช่วงจำกัด — ดึงเป็นแถวได้) ผ่านตัวกรองชุดเดียว: บรรทัดเต็มจำนวน + ส่วนที่ปันเข้าตัวกรอง
        var lines = await LoadLedgerSliceAsync(_db.JournalEntryLines.Where(l => postedEntryIds.Contains(l.JournalEntryId)), filter);

        // ═══ ยอดยกมา ═══ (2026-10-14 ทีม X3 · erp-review/2026-10-14/EXPORT_AUDIT.md §X3)
        // เดิม (1) ยอดยกมา **ไม่กรองโครงการ/สาขา/มิติ** ขณะที่รายการเคลื่อนไหวกรอง ⇒ เลือกสาขาแล้ว "ยอดคงเหลือ"
        // = ยอดยกมาทั้งบริษัท + เคลื่อนไหวของสาขาเดียว (ตัวเลขที่ไม่มีความหมาย) · (2) ไม่อ่านยอดยกมาที่นำเข้า
        // (OpeningBalances) ซึ่งงบทดลองอ่าน ⇒ บริษัทที่ย้ายระบบมา ยอดคงเหลือในบัญชีแยกประเภท ≠ งบทดลอง ·
        // (3) บัญชีที่มียอดยกมาแต่ไม่เคลื่อนไหวในช่วงหายจากรายงาน
        // ใหม่: ตัวกรองบรรทัดชุดเดียวกับรายการเคลื่อนไหว (ApplyLedgerLineFilters) · ไม่กรองมิติใด ⇒ ใช้ฐานยอดยกมา
        // แบบเดียวกับงบทดลอง (งวดล่าสุดที่มี OpeningBalances และเริ่มไม่หลังวันเริ่มช่วง + รายการตั้งแต่วันเริ่มงวดนั้น)
        // · กรองมิติ ⇒ ไม่บวกฐานยอดยกมา (ยอดนำเข้าไม่มีสาขา/โครงการ — บวกทั้งก้อนเข้าสาขาเดียว = ผิด)
        // ฐานยอดยกมา: ตัวตัดสินเดียวกับงบทดลอง/ยอด ณ วันที่ (LoadLedgerBaseAsync) — กรองมิติใด ⇒ ไม่บวกยอดนำเข้า (ไม่มีสาขา/โครงการ)
        var (anchorStart, baseline) = await LoadLedgerBaseAsync(companyId, fromDateStart, filter);

        // Get opening movements (posted/reversed entries before fromDate — ตั้งแต่วันเริ่มงวดฐานถ้ามี)
        var openingEntryIds = _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                && j.EntryDate < fromDateStart
                && (!anchorStart.HasValue || j.EntryDate >= anchorStart.Value))
            .Select(j => j.Id);

        // P4: รวมยอดยกมา **ในฐานข้อมูล** ไม่ใช่ดึงทุกบรรทัดมารวมในหน่วยความจำ (SumLedgerAsync = GroupBy+Sum ในฐาน + ส่วนที่ปัน)
        // รอบ 2 P2-5: กรอง ⇒ รายได้/ค่าใช้จ่ายเริ่มที่ต้นรอบบัญชีของวันเริ่มช่วง (เท่างบทดลองแบบกรอง — ใบปิดปีไม่มีสาขา จึงไม่ล้างยอดของสาขา)
        var openingBase = _db.JournalEntryLines.Where(l => openingEntryIds.Contains(l.JournalEntryId));
        var notes = new List<string>();
        if (filter.Filtered)
        {
            var startMonth = await FiscalStartMonthAsync(companyId);
            var pnlFrom = Accounting.Helpers.LedgerFilterRule.PnlWindowStart(fromDateStart, startMonth);
            openingBase = openingBase.Where(l => (l.Account.AccountType != AccountType.Revenue && l.Account.AccountType != AccountType.Expense)
                || l.JournalEntry.EntryDate >= pnlFrom);
            var lastFyStart = Accounting.Helpers.LedgerFilterRule.PnlWindowStart(toDate.Date, startMonth);
            if (lastFyStart > fromDateStart)
                notes.Add(Accounting.Helpers.LedgerFilterRule.CrossesFiscalYearNote(lastFyStart));
        }

        var openingGross = new Dictionary<Guid, (decimal Debit, decimal Credit)>(baseline);
        foreach (var (accId, amount) in await SumLedgerAsync(openingBase, filter))
        {
            (decimal Debit, decimal Credit) cur = openingGross.TryGetValue(accId, out var found) ? found : (0m, 0m);
            openingGross[accId] = (cur.Debit + amount.Debit, cur.Credit + amount.Credit);
        }

        // ข้อมูลบัญชีของทุกบัญชีที่มียอดยกมา (บัญชีที่ไม่มีรายการเคลื่อนไหวในช่วงก็ต้องแสดง — เดิมหายจากรายงาน)
        var openingAccountIds = openingGross.Keys.ToList();
        var openingAccounts = openingAccountIds.Count == 0
            ? new List<ChartOfAccount>()
            : await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && openingAccountIds.Contains(a.Id))
                .ToListAsync();

        static decimal Net(AccountType t, decimal debit, decimal credit)
            => (t == AccountType.Asset || t == AccountType.Expense) ? debit - credit : credit - debit;

        // Group by account (filter out lines with missing accounts)
        var grouped = lines
            .Where(l => l.Account != null)
            .GroupBy(l => new { l.AccountId, l.Account.AccountCode, l.Account.AccountName, l.Account.AccountType })
            .ToList();

        var accounts = new List<GeneralLedgerAccount>();
        foreach (var g in grouped)
        {
            (decimal Debit, decimal Credit) og = openingGross.TryGetValue(g.Key.AccountId, out var ogFound) ? ogFound : (0m, 0m);
            var opening = Net(g.Key.AccountType, og.Debit, og.Credit);
            var isDebitNormal = g.Key.AccountType == AccountType.Asset || g.Key.AccountType == AccountType.Expense;
            var runningBalance = opening;

            var transactions = new List<GeneralLedgerTransaction>();
            foreach (var l in g.OrderBy(l => l.JournalEntry.EntryDate).ThenBy(l => l.JournalEntry.EntryNumber).ThenBy(l => l.Id))
            {
                runningBalance += isDebitNormal ? (l.DebitAmount - l.CreditAmount) : (l.CreditAmount - l.DebitAmount);
                transactions.Add(new GeneralLedgerTransaction(
                    l.JournalEntryId, l.JournalEntry.EntryNumber, l.JournalEntry.EntryDate,
                    l.JournalEntry.JournalType, l.Description ?? l.JournalEntry.Description,
                    l.DebitAmount, l.CreditAmount, runningBalance,
                    JournalLineId: l.Id,
                    EntryReference: l.JournalEntry.Reference,
                    SourceDocumentId: l.JournalEntry.SourceDocumentId,
                    ProjectId: l.ProjectId ?? l.JournalEntry.ProjectId,
                    BranchId: l.BranchId ?? l.JournalEntry.BranchId,
                    DimensionId: l.DimensionId ?? l.JournalEntry.DimensionId));
            }

            accounts.Add(new GeneralLedgerAccount(
                g.Key.AccountId, g.Key.AccountCode, g.Key.AccountName, g.Key.AccountType, (int)g.Key.AccountType,
                opening, g.Sum(l => l.DebitAmount), g.Sum(l => l.CreditAmount),
                runningBalance, transactions));
        }

        // บัญชีที่มียอดยกมา ≠ 0 แต่ไม่มีรายการในช่วง — แสดงยอดยกมา = ยอดยกไป (เดิมหายเงียบ ⇒ ผลรวมบัญชีแยกประเภท ≠ งบทดลอง)
        var withMovement = grouped.Select(g => g.Key.AccountId).ToHashSet();
        foreach (var a in openingAccounts)
        {
            if (withMovement.Contains(a.Id)) continue;
            var og = openingGross[a.Id];
            var opening = Net(a.AccountType, og.Debit, og.Credit);
            if (opening == 0m) continue;
            accounts.Add(new GeneralLedgerAccount(
                a.Id, a.AccountCode, a.AccountName, a.AccountType, (int)a.AccountType,
                opening, 0m, 0m, opening, new List<GeneralLedgerTransaction>()));
        }

        // ยอดยกมาที่นำเข้า (ย้ายระบบ · OpeningRoll.IsImported — ยอดจากการปิดปีต่อเนื่องกับรายการอยู่แล้ว) ที่เริ่ม "กลางช่วง" — งบทดลอง ณ สิ้นช่วงใช้ยอดนั้นเป็นฐาน แต่บัญชีแยกประเภทเริ่มก่อนฐาน ⇒ ยอดยกไปต่างกันได้
        // บอกให้เห็นบนหน้า/ไฟล์ (คู่กับคอลัมน์ "ผลต่างความต่อเนื่อง" ของงบทดลอง) ไม่กลบด้วยการแต่งรายการ
        if (!filter.Filtered)
        {
            var inside = await (
                from p in _db.FiscalPeriods.AsNoTracking()
                join o in _db.OpeningBalances.AsNoTracking() on p.Id equals o.FiscalPeriodId
                where p.CompanyId == companyId && o.CompanyId == companyId && p.StartDate > fromDateStart && p.StartDate < toDateEnd
                    && o.Notes != null && o.Notes.StartsWith(Accounting.Helpers.OpeningRoll.ImportedNotePrefix)
                orderby p.StartDate descending
                select (DateTime?)p.StartDate
            ).FirstOrDefaultAsync();
            if (inside.HasValue)
                notes.Add(LedgerAnchorInsideRangeNote(inside.Value));
        }

        return new GeneralLedgerResponse(accounts.OrderBy(a => a.AccountCode).ToList(), fromDate, toDate, notes);
    }

    /// <summary>ข้อความเมื่อมียอดยกมา (นำเข้า/ปิดปี) เริ่มกลางช่วงของบัญชีแยกประเภท — ตัวตั้งเดียวของหน้า/ไฟล์</summary>
    public static string LedgerAnchorInsideRangeNote(DateTime anchorStart)
        => $"⚠ มียอดยกมา ณ {Accounting.Helpers.ThaiDate.ToThaiDisplayString(anchorStart)} อยู่กลางช่วงที่เลือก — งบทดลองตั้งแต่วันนั้นใช้ยอดยกมานั้นเป็นฐาน "
            + "แต่บัญชีแยกประเภทช่วงนี้นับรายการต่อเนื่องจากก่อนวันนั้น ⇒ ยอดยกไปอาจไม่เท่างบทดลอง · "
            + $"เลือกวันเริ่มตั้งแต่ {Accounting.Helpers.ThaiDate.ToThaiDisplayString(anchorStart)} เพื่อให้ตรงกับงบทดลอง";

    // ═══ ตัวกรองบรรทัด + ฐานยอดยกมาของรายงานบัญชี — เจ้าของเดียว (ฝ่ายค้านรอบส่งออก 2026-10-15 P2-6 · รอบ 2 P2-5 Helpers/LedgerFilterRule) ═══
    // บัญชีแยกประเภท · งบทดลอง (→ งบดุล) · งบกำไรขาดทุน · งบกระแสเงินสด · ยอด ณ วันที่ ใช้ชุดนี้ชุดเดียว (ห้ามเขียนเงื่อนไขสาขา/มิติเอง)

    /// <summary>ตัวกรองที่ตีความแล้ว — สาขาที่ผูกมิติ (<c>Branch.DimensionId</c>) = รายการของสาขานั้น <b>หรือ</b> ที่ติดมิติของสาขา (บรรทัดที่ไม่มีสาขา)</summary>
    private sealed record LedgerFilter(Guid? AccountId, Guid? ProjectId, Guid? BranchId, Guid? BranchDimensionId, Guid? DimensionId)
    {
        /// <summary>กรองสาขา/โครงการ/มิติ ⇒ ไม่ใช้ฐานยอดยกมานำเข้า (ยอดนำเข้าไม่มีสาขา/โครงการ — บวกทั้งก้อนเข้าสาขาเดียว = ผิด)</summary>
        public bool Filtered => ProjectId.HasValue || BranchId.HasValue || DimensionId.HasValue;

        /// <summary>มิติที่แถวปันส่วน (JournalLineDimension) ตัดสินยอด — กรองมิติ = มิตินั้น · กรองสาขาอย่างเดียว = มิติของสาขา (LedgerFilterRule ข้อ 2–3)</summary>
        public Guid? AllocationDimensionId => DimensionId ?? (BranchId.HasValue ? BranchDimensionId : null);
    }

    /// <summary>ส่วนของบรรทัดที่ปันเข้าตัวกรอง (คำนวณด้วย <see cref="Accounting.Helpers.LedgerFilterRule.AllocationShare"/>)</summary>
    private sealed record LedgerAllocationPart(Guid LineId, Guid AccountId, decimal Debit, decimal Credit);

    private async Task<LedgerFilter> ResolveLedgerFilterAsync(Guid companyId, Guid? accountId, Guid? projectId, Guid? branchId, Guid? dimensionId)
    {
        Guid? branchDim = null;
        if (branchId.HasValue)
            branchDim = await _db.Set<Branch>()
                .Where(b => b.Id == branchId.Value && b.CompanyId == companyId)
                .Select(b => b.DimensionId)
                .FirstOrDefaultAsync();
        return new LedgerFilter(accountId, projectId, branchId, branchDim, dimensionId);
    }

    /// <summary>เดือนเริ่มรอบบัญชีของบริษัท (ค่าดิบ — <see cref="Accounting.Helpers.FiscalYear"/> ตีความค่า 0/นอกช่วงเป็นปีปฏิทิน)</summary>
    private Task<int> FiscalStartMonthAsync(Guid companyId)
        => _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.FiscalYearStartMonth).FirstOrDefaultAsync();

    /// <summary>ฐานยอดยกมา ณ <paramref name="onOrBefore"/>: งวดล่าสุดที่มีแถวยอดยกมาและเริ่มไม่หลังวันนั้น + ยอดต่อบัญชี ·
    /// กรองมิติใด ⇒ ไม่มีฐาน (รายการตั้งแต่เปิดบริษัทที่ผ่านตัวกรอง)</summary>
    private async Task<(DateTime? AnchorStart, Dictionary<Guid, (decimal Debit, decimal Credit)> Baseline)> LoadLedgerBaseAsync(
        Guid companyId, DateTime onOrBefore, LedgerFilter filter)
    {
        var baseline = new Dictionary<Guid, (decimal Debit, decimal Credit)>();
        if (filter.Filtered) return (null, baseline);
        var anchor = await (
            from p in _db.FiscalPeriods.AsNoTracking()
            join o in _db.OpeningBalances.AsNoTracking() on p.Id equals o.FiscalPeriodId
            where p.CompanyId == companyId && o.CompanyId == companyId && p.StartDate <= onOrBefore
            orderby p.StartDate descending
            select new { p.Id, p.StartDate }
        ).FirstOrDefaultAsync();
        if (anchor == null) return (null, baseline);
        var obQuery = _db.OpeningBalances.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.FiscalPeriodId == anchor.Id);
        if (filter.AccountId.HasValue)
        {
            var onlyAccount = filter.AccountId.Value;
            obQuery = obQuery.Where(o => o.AccountId == onlyAccount);
        }
        baseline = await obQuery
            .GroupBy(o => o.AccountId)
            .Select(g => new { AccountId = g.Key, Debit = g.Sum(x => x.OpeningDebit), Credit = g.Sum(x => x.OpeningCredit) })
            .ToDictionaryAsync(x => x.AccountId, x => (x.Debit, x.Credit));
        return (anchor.StartDate, baseline);
    }

    /// <summary>บัญชี + โครงการ (บรรทัดหรือหัวใบ) — ส่วนร่วมของบรรทัดเต็มจำนวนและบรรทัดที่ปันส่วน</summary>
    private static IQueryable<JournalEntryLine> ApplyLedgerAccountProjectFilters(IQueryable<JournalEntryLine> q, LedgerFilter f)
    {
        if (f.AccountId.HasValue)
        {
            var accountId = f.AccountId.Value;
            q = q.Where(l => l.AccountId == accountId);
        }
        if (f.ProjectId.HasValue)
        {
            var projectId = f.ProjectId.Value;
            q = q.Where(l => l.ProjectId == projectId || l.JournalEntry.ProjectId == projectId);
        }
        return q;
    }

    /// <summary>กรองทั้งมิติและสาขา: สาขาเป็นตัวคัดเข้า — สาขาของบรรทัด (บรรทัด ?? หัวใบ) ตรง หรือไม่มีสาขาแต่ติด/ปันเข้ามิติของสาขา</summary>
    private IQueryable<JournalEntryLine> ApplyLedgerBranchInclusion(IQueryable<JournalEntryLine> q, LedgerFilter f)
    {
        if (!f.BranchId.HasValue) return q;
        var branchId = f.BranchId.Value;
        if (!f.BranchDimensionId.HasValue)
            return q.Where(l => (l.BranchId ?? l.JournalEntry.BranchId) == branchId);
        var bd = f.BranchDimensionId.Value;
        var allocations = _db.Set<JournalLineDimension>();
        return q.Where(l => (l.BranchId ?? l.JournalEntry.BranchId) == branchId
            || ((l.BranchId ?? l.JournalEntry.BranchId) == null
                && (allocations.Any(a => a.JournalEntryLineId == l.Id && a.DimensionId == bd)
                    || l.DimensionId == bd || l.JournalEntry.DimensionId == bd)));
    }

    /// <summary>บรรทัดที่นับ <b>เต็มจำนวน</b> ภายใต้ตัวกรอง (Helpers/LedgerFilterRule) — ใช้ร่วมทุกรายงาน · บรรทัดที่มีแถวปันส่วนของมิติที่กรอง
    /// มาทาง <see cref="LoadAllocationSharesAsync"/> (ตามสัดส่วน) สองชุดไม่ทับกัน · สาขา = <c>l.BranchId ?? หัวใบ</c> (บรรทัดที่มีสาขานับในสาขานั้นสาขาเดียว)</summary>
    private IQueryable<JournalEntryLine> ApplyLedgerLineFilters(IQueryable<JournalEntryLine> q, LedgerFilter f)
    {
        q = ApplyLedgerAccountProjectFilters(q, f);
        var allocations = _db.Set<JournalLineDimension>();
        if (f.DimensionId.HasValue)
        {
            var dimId = f.DimensionId.Value;
            q = q.Where(l => !allocations.Any(a => a.JournalEntryLineId == l.Id)
                && (l.DimensionId == dimId || l.JournalEntry.DimensionId == dimId));
            q = ApplyLedgerBranchInclusion(q, f);
        }
        else if (f.BranchId.HasValue)
        {
            var branchId = f.BranchId.Value;
            if (f.BranchDimensionId.HasValue)
            {
                var bd = f.BranchDimensionId.Value;
                q = q.Where(l => (l.BranchId ?? l.JournalEntry.BranchId) == branchId
                    || ((l.BranchId ?? l.JournalEntry.BranchId) == null
                        && !allocations.Any(a => a.JournalEntryLineId == l.Id)
                        && (l.DimensionId == bd || l.JournalEntry.DimensionId == bd)));
            }
            else
                q = q.Where(l => (l.BranchId ?? l.JournalEntry.BranchId) == branchId);
        }
        return q;
    }

    /// <summary>ส่วนของบรรทัดที่ปันเข้ามิติของตัวกรอง (เฉพาะแถวปันส่วนของมิตินั้น — ไม่ใช่ทั้งสมุด) · กรองสาขาอย่างเดียว = เฉพาะบรรทัดที่ไม่มีสาขา ·
    /// สัดส่วนคิดด้วย <see cref="Accounting.Helpers.LedgerFilterRule.AllocationShare"/> ตัวเดียวกับรายงานกำไรขาดทุนตามมิติ</summary>
    private async Task<List<LedgerAllocationPart>> LoadAllocationSharesAsync(IQueryable<JournalEntryLine> baseLines, LedgerFilter f)
    {
        if (f.AllocationDimensionId is not Guid dim) return new List<LedgerAllocationPart>();
        var lines = ApplyLedgerAccountProjectFilters(baseLines, f);
        lines = f.DimensionId.HasValue
            ? ApplyLedgerBranchInclusion(lines, f)
            : lines.Where(l => (l.BranchId ?? l.JournalEntry.BranchId) == null);
        var rows = await (
            from a in _db.Set<JournalLineDimension>().AsNoTracking()
            join l in lines on a.JournalEntryLineId equals l.Id
            where a.DimensionId == dim
            select new { LineId = l.Id, l.AccountId, l.DebitAmount, l.CreditAmount, a.AllocatedAmount, a.AllocatedPercent }
        ).ToListAsync();
        return rows.Select(r =>
        {
            var (debit, credit) = Accounting.Helpers.LedgerFilterRule.AllocationShare(r.DebitAmount, r.CreditAmount, r.AllocatedAmount, r.AllocatedPercent);
            return new LedgerAllocationPart(r.LineId, r.AccountId, debit, credit);
        }).ToList();
    }

    /// <summary>ยอดรวมต่อบัญชีภายใต้ตัวกรอง — บรรทัดเต็มจำนวนรวมในฐานข้อมูล (GroupBy+Sum) + ส่วนที่ปัน · ไม่ดึงทุกบรรทัดเข้าหน่วยความจำ</summary>
    private async Task<Dictionary<Guid, (decimal Debit, decimal Credit)>> SumLedgerAsync(IQueryable<JournalEntryLine> baseLines, LedgerFilter f)
    {
        var result = (await ApplyLedgerLineFilters(baseLines, f)
                .GroupBy(l => l.AccountId)
                .Select(g => new { AccountId = g.Key, Debit = g.Sum(x => x.DebitAmount), Credit = g.Sum(x => x.CreditAmount) })
                .ToListAsync())
            .ToDictionary(x => x.AccountId, x => (x.Debit, x.Credit));
        foreach (var part in await LoadAllocationSharesAsync(baseLines, f))
        {
            result.TryGetValue(part.AccountId, out var cur);
            result[part.AccountId] = (cur.Debit + part.Debit, cur.Credit + part.Credit);
        }
        return result;
    }

    /// <summary>บรรทัด (พร้อมผังและหัวใบ · ไม่ติดตาม) ของช่วงที่จำกัดแล้ว ภายใต้ตัวกรอง — บรรทัดที่ปันส่วนถูกแทนยอดด้วยส่วนที่ปันเข้าตัวกรอง
    /// (instance แบบ AsNoTracking — ไม่มีวันถูกบันทึกกลับ) · ใช้กับรายงานที่ต้องเห็นรายบรรทัด (บัญชีแยกประเภท · งบกระแสเงินสด)</summary>
    private async Task<List<JournalEntryLine>> LoadLedgerSliceAsync(IQueryable<JournalEntryLine> baseLines, LedgerFilter f)
    {
        var lines = await ApplyLedgerLineFilters(baseLines, f)
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .AsNoTracking()
            .ToListAsync();
        var parts = await LoadAllocationSharesAsync(baseLines, f);
        if (parts.Count == 0) return lines;
        var ids = parts.Select(p => p.LineId).Distinct().ToList();
        var allocated = await _db.JournalEntryLines.AsNoTracking()
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => ids.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id);
        foreach (var g in parts.GroupBy(p => p.LineId))
        {
            if (!allocated.TryGetValue(g.Key, out var line)) continue;
            line.DebitAmount = g.Sum(p => p.Debit);
            line.CreditAmount = g.Sum(p => p.Credit);
            lines.Add(line);
        }
        return lines;
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

        // Load account mappings by journal type — ตัวเลือกผังตั้งต้นตามรหัสต้นตัวเดียวของระบบ (PostingAccountGuard.DefaultFallbackCandidates ·
        // ฝ่ายค้านรอบส่งออก รอบ 3 P2-7): เดิม FirstOrDefault ไม่เรียง ⇒ ได้ผังตามลำดับที่ฐานข้อมูลคืน (บัญชีคุม/ผังเฉพาะกิจก็ได้)
        Task<ChartOfAccount?> Pick(string prefix)
            => _db.ChartOfAccounts.DefaultFallbackCandidates(_db)
                .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith(prefix) && a.IsActive);
        var cashAccount = await Pick("111");
        var bankAccount = await Pick("1112");
        var arAccount = await Pick("113");
        var apAccount = await Pick("212");
        var revenueAccount = await Pick("41");
        var expenseAccount = await Pick("51");
        var depositAccount = await Pick("215");

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
            // แถวใหม่ต้อง Add ตรง ๆ — ผ่านคอลเลกชันของ parent ที่ติดตามอยู่ EF ตีเป็น Modified ⇒ UPDATE 0 แถว (DbUpdateConcurrencyException · บทเรียน PayrollDetail 2026-10-08)
            _db.Set<JournalEntryLine>().AddRange(entry.Lines);
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
        // ฐานเดียวกับงบทดลอง (LoadLedgerBaseAsync — เจ้าของเดียว)
        var (anchorStart, openings) = await LoadLedgerBaseAsync(companyId, asOfDate.Date,
            new LedgerFilter(null, null, null, null, null));

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
        // stores the year-end trial balance (A/L/E only — P&L folded into RE ·
        // Helpers/OpeningRoll), so reading the latest one and adding the
        // movements within its period up to asOfDate yields the correct
        // closing balance — and crucially, picks up balances imported via the
        // migration wizard that have no inception JE history. When no opening
        // exists (a company that's only ever used the system from day one),
        // fall back to summing every JE from inception (legacy behaviour).
        var asOf = asOfDate.Date.AddDays(1);
        // ฐาน + ตัวกรองชุดเดียวกับบัญชีแยกประเภท (ฝ่ายค้านรอบส่งออก 2026-10-15 P2-6): เดิมงบทดลองกรองสาขาแล้วยังบวกยอดยกมานำเข้า
        // ทั้งบริษัท + ไม่ดูการปันส่วนมิติ ⇒ ยอดยกไปของบัญชีแยกประเภทที่กรองสาขาเดียวกันไม่เท่างบทดลอง · ตอนนี้กรองมิติใด ⇒ ไม่มีฐานนำเข้า
        // (รายการตั้งแต่เปิดบริษัทที่ผ่านตัวกรอง) เหมือนบัญชีแยกประเภททุกประการ
        var filter = await ResolveLedgerFilterAsync(companyId, null, projectId, branchId, dimensionId);
        var (anchorStart, openingByAccount) = await LoadLedgerBaseAsync(companyId, asOfDate.Date, filter);

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
        var baseLines = _db.JournalEntryLines.Where(l => postedEntryIds.Contains(l.JournalEntryId));

        // ═══ รอบ 2 P2-5 (Helpers/LedgerFilterRule) ═══ รวมยอดในฐานข้อมูล (เดิม ToList ทุกบรรทัดตั้งแต่เปิดบริษัทเมื่อกรอง) ·
        // กรอง ⇒ รายได้/ค่าใช้จ่ายเริ่มที่ต้นรอบบัญชี (ใบปิดปีไม่มีสาขา ⇒ เดิมยอดของสาขาสะสมข้ามปี) + กำไรขาดทุนรอบก่อนเป็นแถวทุนแถวเดียว ⇒ ลงตัว ·
        // ไม่กรอง ⇒ ตัวเลขเดิมทุกตัว
        Dictionary<Guid, (decimal Debit, decimal Credit)> movements;
        decimal priorPnlNetDebit = 0m;
        if (filter.Filtered)
        {
            var pnlFrom = Accounting.Helpers.LedgerFilterRule.PnlWindowStart(asOfDate.Date, await FiscalStartMonthAsync(companyId));
            movements = await SumLedgerAsync(baseLines.Where(l =>
                (l.Account.AccountType != AccountType.Revenue && l.Account.AccountType != AccountType.Expense)
                || l.JournalEntry.EntryDate >= pnlFrom), filter);
            var prior = await SumLedgerAsync(baseLines.Where(l =>
                (l.Account.AccountType == AccountType.Revenue || l.Account.AccountType == AccountType.Expense)
                && l.JournalEntry.EntryDate < pnlFrom), filter);
            priorPnlNetDebit = prior.Values.Sum(v => v.Debit - v.Credit);
        }
        else
            movements = await SumLedgerAsync(baseLines, filter);

        // Merge movements with the OpeningBalance baseline — every account that
        // either has an opening row or saw movement in the window appears.
        var accountIds = movements.Keys.Union(openingByAccount.Keys).ToList();
        var coa = accountIds.Count == 0
            ? new List<(Guid Id, string AccountCode, string AccountName, AccountType AccountType)>()
            : (await _db.ChartOfAccounts.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && accountIds.Contains(a.Id))
                    .Select(a => new { a.Id, a.AccountCode, a.AccountName, a.AccountType })
                    .ToListAsync())
                .Select(a => (a.Id, a.AccountCode, a.AccountName, a.AccountType))
                .ToList();

        var items = new List<TrialBalanceItem>();
        foreach (var (accountId, code, name, type) in coa)
        {
            movements.TryGetValue(accountId, out var mv);
            openingByAccount.TryGetValue(accountId, out var ob);
            items.Add(new TrialBalanceItem(code, name, type, (int)type, mv.Debit + ob.Debit, mv.Credit + ob.Credit));
        }

        var grouped = items.OrderBy(i => i.AccountCode).ToList();
        // กำไร(ขาดทุน)ของรอบก่อน ๆ ของตัวกรอง — ไม่มีผัง (ใบปิดปีโอนเข้ากำไรสะสมทั้งบริษัท ไม่มีสาขา) · ขาดทุน = เดบิต
        if (priorPnlNetDebit != 0m)
            grouped.Add(new TrialBalanceItem("", Accounting.Helpers.LedgerFilterRule.PriorPnlRowName, AccountType.Equity, (int)AccountType.Equity,
                priorPnlNetDebit > 0m ? priorPnlNetDebit : 0m, priorPnlNetDebit < 0m ? -priorPnlNetDebit : 0m));

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
                && !j.IsClosingEntry          // ★ C-T02 — เหตุผลเดียวกับ GetProfitAndLossAsync
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
                // ★ C-T02: ใบปิดบัญชีเป็นรายการ GL ที่ถูกต้อง แต่มันคือรายการ
                // *ปิด* งบกำไรขาดทุน ไม่ใช่ผลการดำเนินงาน — รวมเข้ามาเมื่อไร
                // ยอดของงวดที่ปิดแล้วกลายเป็น 0 ทันที
                // (งบดุล/งบทดลอง/บัญชีแยกประเภทยังเห็นครบ — ห้ามซ่อนรายการจริง)
                && !j.IsClosingEntry
                && j.EntryDate >= fromDate.Date
                && j.EntryDate < toDate.Date.AddDays(1))
            .Select(j => j.Id);

        var baseLines = _db.JournalEntryLines
            .Where(l => postedEntryIds.Contains(l.JournalEntryId)
                && (l.Account!.AccountType == AccountType.Revenue || l.Account!.AccountType == AccountType.Expense));
        // ตัวกรองสาขา/มิติ/โครงการชุดเดียวกับงบทดลอง/บัญชีแยกประเภท (P2-6) · รอบ 2 P2-5: ส่วนที่ปันเข้ามิติตามสัดส่วน (เท่ารายงานกำไรขาดทุนตามมิติ)
        // + รวมยอดในฐานข้อมูล (เดิม ToList ทุกบรรทัดในช่วง)
        var sums = await SumLedgerAsync(baseLines,
            await ResolveLedgerFilterAsync(companyId, null, projectId, branchId, dimensionId));

        var ids = sums.Keys.ToList();
        var coa = ids.Count == 0
            ? new List<ChartOfAccount>()
            : await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && ids.Contains(a.Id))
                .ToListAsync();

        var revenue = coa.Where(a => a.AccountType == AccountType.Revenue)
            .Select(a => new PnlSection(a.AccountCode, a.AccountName, sums[a.Id].Credit - sums[a.Id].Debit, a.Id))
            .OrderBy(r => r.AccountCode).ToList();

        var expenses = coa.Where(a => a.AccountType == AccountType.Expense)
            .Select(a => new PnlSection(a.AccountCode, a.AccountName, sums[a.Id].Debit - sums[a.Id].Credit, a.Id))
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
                // ★ C-T02: ใบปิดบัญชีย้าย P&L → กำไรสะสม ไม่แตะเงินสดเลย
                // แต่ถ้านับเข้ามา กำไรสุทธิที่เป็นฐานของงบกระแสเงินสดจะเป็น 0
                && !j.IsClosingEntry
                && j.EntryDate >= fromDate.Date
                && j.EntryDate < toDate.Date.AddDays(1))
            .Select(j => j.Id);

        // ตัวกรองสาขา/มิติ/โครงการชุดเดียวกับงบทดลอง/บัญชีแยกประเภท (P2-6) · รอบ 2 P2-5: บรรทัดที่ปันส่วน = ส่วนที่ปันเข้าตัวกรอง (LoadLedgerSliceAsync)
        var allLines = await LoadLedgerSliceAsync(_db.JournalEntryLines.Where(l => postedEntryIds.Contains(l.JournalEntryId)),
            await ResolveLedgerFilterAsync(companyId, null, projectId, branchId, dimensionId));

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
            issues.Add($"ยังมีใบสำคัญ Draft {draftCount} รายการ (ต้อง Post หรือ Void ก่อน)");

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

        // ═══ "ปิดงวด" = **ล็อกไม่ให้โพสต์เพิ่ม** เท่านั้น — ไม่ปิดบัญชี (C-T02) ═══
        //
        // เดิมที่นี่เรียก CreateClosingEntriesAsync ⇒ ปิดงวด**รายเดือน** สร้างใบ
        // CL-yyyyMM ล้างรายได้/ค่าใช้จ่ายเข้ากำไรสะสมทันที ⇒ เปิดงบกำไรขาดทุน
        // ม.ค.–ธ.ค. แล้ว **ยอดของเดือนที่ปิดไปเป็น 0** (ใบปิดกลับด้านทุกบรรทัดพอดี)
        // แล้วตัวเลขนี้ไหลต่อไปที่ XBRL ที่ยื่น DBD · ฐาน ภ.ง.ด.51 · รายงาน CIT
        // ⇒ งบการเงินที่ยื่นออกไปผิดโดยไม่มีอะไรเตือน
        //
        // ตามหลักบัญชี การปิด P&L เข้ากำไรสะสมทำ **ปีละครั้ง** ตอนสิ้นรอบ —
        // `YearEndCloseAsync` เป็นที่เดียวที่ทำ (และติดธง `IsClosingEntry` ให้
        // งบกำไรขาดทุนคัดออกได้) · การปิดงวดรายเดือนมีไว้กันคนโพสต์ย้อนหลัง
        // ไม่ใช่การปิดบัญชี
        period.Status = FiscalPeriodStatus.Closed;
        await _db.SaveChangesAsync();
    }

    // ── ลบ CreateClosingEntriesAsync ทิ้ง (C-T02) ──
    // เมธอดนี้สร้างใบปิดบัญชีตอนปิดงวด**รายเดือน** ซึ่งเป็นต้นเหตุที่งบกำไรขาดทุน
    // ของงวดที่ปิดแล้วกลายเป็น 0 · การปิด P&L เข้ากำไรสะสมทำปีละครั้งที่
    // `YearEndCloseAsync` ซึ่งมีตรรกะของตัวเองครบอยู่แล้ว (และติดธง IsClosingEntry)
    // — เก็บโค้ดที่ไม่มีใครเรียกไว้เฉย ๆ จะถูกอ่านว่า "ระบบมีฟีเจอร์นี้" ทั้งที่ไม่มี

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
            l.Id, l.AccountId, l.Account.AccountCode, Accounting.Helpers.JournalLineAccounts.DisplayName(l.Account),
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

    /// <summary>เลขที่/ชนิดเอกสารต้นทางของใบสำคัญ — กรองบริษัท (กฎ M · ทีม O 2026-10-22: เดิมค้นเอกสารด้วย Id ล้วน ไม่มี CompanyId)</summary>
    private async Task<Dictionary<Guid, (string Number, string Type)>> LoadSourceDocumentMapAsync(Guid companyId, IEnumerable<JournalEntry> entries)
    {
        var ids = entries.Where(e => e.SourceDocumentId.HasValue)
            .Select(e => e.SourceDocumentId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, (string, string)>();
        var docs = await _db.Documents
            .Where(d => d.CompanyId == companyId && ids.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType })
            .ToListAsync();
        return docs.ToDictionary(d => d.Id, d => (d.DocumentNumber, d.DocumentType.ToString()));
    }

    private static FiscalPeriodResponse MapPeriodToResponse(FiscalPeriod f) => new(
        f.Id, f.Name, f.Year, f.Month, f.StartDate, f.EndDate, f.Status,
        f.ClosedAt, f.ClosedBy, f.LockedAt, f.LockedBy, f.YearEndJournalEntryId);

    /// <summary>เลขรัน JE — ผ่านตัวออกเลขกลางตัวเดียวของระบบ
    ///
    /// <para>⚠️ เดิมเมธอดนี้มี logic ของตัวเองพร้อม advisory lock ที่คิดคีย์จาก
    /// <c>Math.Abs($"je_number_{companyId}_{pattern}".GetHashCode())</c> —
    /// <b><c>string.GetHashCode()</c> ใน .NET Core randomize ต่อ process</b>
    /// ⇒ ค่าคีย์เปลี่ยนทุกครั้งที่รีสตาร์ต และสอง instance ได้คนละค่า
    /// = ล็อกนี้กันอะไรไม่ได้เลยข้ามเครื่อง (และกันตัวเองข้าม deploy ก็ไม่ได้)</para>
    ///
    /// <para>ส่วนที่ดีของตัวนี้ — การนับ JE ที่ Add ค้างใน change tracker —
    /// ถูกยกเข้าไปอยู่ใน <c>JournalEntryBuilder.NextJournalNumberAsync</c> แล้ว
    /// จึงไม่หายไปไหน</para></summary>
    private Task<string> GetNextEntryNumberAsync(Guid companyId, string prefix)
        => Journal.JournalEntryBuilder.NextJournalNumberAsync(_db, companyId, prefix, DateTime.UtcNow);

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

    /// <summary>ตรวจว่า "วันที่นี้" อยู่ในงวดที่เปิดอยู่ — ยึด **วันที่** เป็นหลัก
    /// ไม่ใช่ FK ที่เก็บไว้.
    /// <para>ที่มา (X-9): <c>ValidateFiscalPeriodOpenAsync</c> คืนทันทีเมื่อ
    /// <c>FiscalPeriodId == null</c> ซึ่งเป็นค่าปกติของ JE ที่ลงตอนบริษัทยังไม่ได้
    /// ตั้งงวด หรือ JE ที่ resolver หางวดไม่เจอ ⇒ พองวดถูกปิดย้อนหลัง JE เหล่านั้น
    /// **ยังแก้ได้อยู่** ทั้งที่งบของงวดนั้นออกไปแล้ว. และเมื่อผู้ใช้เปลี่ยนวันที่
    /// ระบบไม่เคยตรวจงวดของ "วันใหม่" เลย ⇒ ย้าย JE เข้างวดที่ปิดแล้วได้.</para>
    /// convention เดียวกับที่อื่นในระบบ: ไม่มีแถวงวดครอบวันนั้น = ถือว่าเปิด</summary>
    private async Task ValidateFiscalPeriodOpenForDateAsync(Guid companyId, DateTime date)
    {
        var d = date.Date;
        var period = await _db.FiscalPeriods.AsNoTracking().FirstOrDefaultAsync(f =>
            f.CompanyId == companyId && f.StartDate <= d && f.EndDate >= d);
        if (period != null && period.Status != FiscalPeriodStatus.Open)
            throw new InvalidOperationException(
                $"วันที่ {d:dd/MM/yyyy} อยู่ในงวด '{period.Name}' ที่ปิดแล้ว ({period.Status}) — " +
                "แก้ไขรายการในงวดที่ปิดไม่ได้ (กันงบย้อนหลังเปลี่ยนหลังออกรายงาน)");
    }

    /// <summary>งวดที่ครอบวันนี้ (null = ไม่มีแถวงวด) — ใช้ตั้ง FK ใหม่เมื่อวันที่เปลี่ยน</summary>
    private Task<FiscalPeriod?> FindFiscalPeriodByDateAsync(Guid companyId, DateTime date)
    {
        var d = date.Date;
        return _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId && f.StartDate <= d && f.EndDate >= d);
    }
}
