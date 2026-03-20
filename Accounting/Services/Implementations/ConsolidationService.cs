using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accounting.Services.Implementations;

public class ConsolidationService : IConsolidationService
{
    private readonly AccountingDbContext _db;

    public ConsolidationService(AccountingDbContext db)
    {
        _db = db;
    }

    // ===== Group Management =====

    public async Task<ConsolidationGroupResponse> CreateGroupAsync(CreateConsolidationGroupRequest request, string createdBy)
    {
        var parentCompany = await _db.Companies
            .FirstOrDefaultAsync(c => c.Id == request.ParentCompanyId && !c.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบบริษัทแม่");

        var group = new ConsolidationGroup
        {
            Name = request.Name,
            Description = request.Description,
            ParentCompanyId = request.ParentCompanyId,
            Currency = request.Currency,
            FiscalYearStartMonth = request.FiscalYearStartMonth,
            CreatedBy = createdBy
        };

        _db.Set<ConsolidationGroup>().Add(group);
        await _db.SaveChangesAsync();

        return await GetGroupAsync(group.Id);
    }

    public async Task<ConsolidationGroupResponse> GetGroupAsync(Guid groupId)
    {
        var group = await _db.Set<ConsolidationGroup>()
            .Include(g => g.ParentCompany)
            .Include(g => g.Members).ThenInclude(m => m.Company)
            .FirstOrDefaultAsync(g => g.Id == groupId && !g.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มบริษัท");

        return MapToResponse(group);
    }

    public async Task<List<ConsolidationGroupResponse>> GetGroupsAsync(Guid parentCompanyId)
    {
        var groups = await _db.Set<ConsolidationGroup>()
            .Include(g => g.ParentCompany)
            .Include(g => g.Members).ThenInclude(m => m.Company)
            .Where(g => g.ParentCompanyId == parentCompanyId && g.IsActive && !g.IsDeleted)
            .OrderBy(g => g.Name)
            .ToListAsync();

        return groups.Select(MapToResponse).ToList();
    }

    public async Task<ConsolidationGroupResponse> AddMemberAsync(Guid groupId, AddConsolidationMemberRequest request)
    {
        var group = await _db.Set<ConsolidationGroup>()
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == groupId && !g.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มบริษัท");

        var alreadyMember = group.Members.Any(m => m.CompanyId == request.CompanyId && m.IsActive);
        if (alreadyMember)
            throw new InvalidOperationException("บริษัทนี้เป็นสมาชิกอยู่แล้ว");

        var company = await _db.Companies
            .FirstOrDefaultAsync(c => c.Id == request.CompanyId && !c.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        var member = new ConsolidationMember
        {
            ConsolidationGroupId = groupId,
            CompanyId = request.CompanyId,
            OwnershipPercent = request.OwnershipPercent,
            Method = request.Method
        };

        _db.Set<ConsolidationMember>().Add(member);
        await _db.SaveChangesAsync();

        return await GetGroupAsync(groupId);
    }

    public async Task RemoveMemberAsync(Guid groupId, Guid memberId)
    {
        var member = await _db.Set<ConsolidationMember>()
            .FirstOrDefaultAsync(m => m.Id == memberId && m.ConsolidationGroupId == groupId)
            ?? throw new KeyNotFoundException("ไม่พบสมาชิกกลุ่ม");

        member.IsActive = false;
        await _db.SaveChangesAsync();
    }

    // ===== Consolidated Reports =====

    public async Task<ConsolidatedReportResponse> GenerateConsolidatedBalanceSheetAsync(Guid groupId, DateTime asOfDate)
    {
        var group = await _db.Set<ConsolidationGroup>()
            .Include(g => g.Members).ThenInclude(m => m.Company)
            .FirstOrDefaultAsync(g => g.Id == groupId && !g.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มบริษัท");

        var reportData = new Dictionary<string, object>();

        foreach (var member in group.Members.Where(m => m.IsActive))
        {
            var balances = await _db.JournalEntryLines
                .Include(l => l.JournalEntry)
                .Include(l => l.Account)
                .Where(l => l.JournalEntry.CompanyId == member.CompanyId
                    && l.JournalEntry.Status == JournalEntryStatus.Posted
                    && l.JournalEntry.EntryDate <= asOfDate)
                .GroupBy(l => new { l.AccountId, l.Account!.AccountCode, l.Account.AccountName, l.Account.AccountType })
                .Select(g => new
                {
                    g.Key.AccountCode,
                    g.Key.AccountName,
                    g.Key.AccountType,
                    Balance = g.Sum(l => l.DebitAmount - l.CreditAmount)
                })
                .ToListAsync();

            var multiplier = member.Method == ConsolidationMethod.Full ? 1m
                : member.Method == ConsolidationMethod.Proportionate ? member.OwnershipPercent / 100m
                : 0m; // Equity and Cost methods handled differently

            var companyData = balances.Select(b => new
            {
                b.AccountCode,
                b.AccountName,
                AccountType = b.AccountType.ToString(),
                ConsolidatedBalance = b.Balance * multiplier
            }).ToList();

            reportData[member.Company.Name] = companyData;
        }

        // Save consolidated report
        var report = new ConsolidationReport
        {
            ConsolidationGroupId = groupId,
            ReportType = "BalanceSheet",
            AsOfDate = asOfDate,
            ReportDataJson = JsonSerializer.Serialize(reportData),
            Status = "Completed"
        };

        _db.Set<ConsolidationReport>().Add(report);
        await _db.SaveChangesAsync();

        return new ConsolidatedReportResponse(
            groupId, group.Name, "BalanceSheet", asOfDate, null, null,
            report.ReportDataJson, report.EliminationEntriesJson,
            report.MinorityInterestJson, DateTime.UtcNow);
    }

    public async Task<ConsolidatedReportResponse> GenerateConsolidatedPnLAsync(Guid groupId, DateTime fromDate, DateTime toDate)
    {
        var group = await _db.Set<ConsolidationGroup>()
            .Include(g => g.Members).ThenInclude(m => m.Company)
            .FirstOrDefaultAsync(g => g.Id == groupId && !g.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มบริษัท");

        var reportData = new Dictionary<string, object>();

        foreach (var member in group.Members.Where(m => m.IsActive))
        {
            var pnlData = await _db.JournalEntryLines
                .Include(l => l.JournalEntry)
                .Include(l => l.Account)
                .Where(l => l.JournalEntry.CompanyId == member.CompanyId
                    && l.JournalEntry.Status == JournalEntryStatus.Posted
                    && l.JournalEntry.EntryDate >= fromDate
                    && l.JournalEntry.EntryDate <= toDate
                    && (l.Account!.AccountType == AccountType.Revenue
                        || l.Account.AccountType == AccountType.Expense))
                .GroupBy(l => new { l.AccountId, l.Account!.AccountCode, l.Account.AccountName, l.Account.AccountType })
                .Select(g => new
                {
                    g.Key.AccountCode,
                    g.Key.AccountName,
                    g.Key.AccountType,
                    Balance = g.Sum(l => l.DebitAmount - l.CreditAmount)
                })
                .ToListAsync();

            var multiplier = member.Method == ConsolidationMethod.Full ? 1m
                : member.Method == ConsolidationMethod.Proportionate ? member.OwnershipPercent / 100m
                : 0m;

            var companyPnl = pnlData.Select(b => new
            {
                b.AccountCode,
                b.AccountName,
                AccountType = b.AccountType.ToString(),
                ConsolidatedAmount = b.Balance * multiplier
            }).ToList();

            reportData[member.Company.Name] = companyPnl;
        }

        var report = new ConsolidationReport
        {
            ConsolidationGroupId = groupId,
            ReportType = "ProfitLoss",
            AsOfDate = toDate,
            FromDate = fromDate,
            ToDate = toDate,
            ReportDataJson = JsonSerializer.Serialize(reportData),
            Status = "Completed"
        };

        _db.Set<ConsolidationReport>().Add(report);
        await _db.SaveChangesAsync();

        return new ConsolidatedReportResponse(
            groupId, group.Name, "ProfitLoss", toDate, fromDate, toDate,
            report.ReportDataJson, report.EliminationEntriesJson,
            report.MinorityInterestJson, DateTime.UtcNow);
    }

    public async Task<List<EliminationEntryResponse>> GetEliminationEntriesAsync(Guid groupId, DateTime asOfDate)
    {
        var group = await _db.Set<ConsolidationGroup>()
            .Include(g => g.Members).ThenInclude(m => m.Company)
            .FirstOrDefaultAsync(g => g.Id == groupId && !g.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มบริษัท");

        var memberCompanyIds = group.Members
            .Where(m => m.IsActive)
            .Select(m => m.CompanyId)
            .ToList();

        // Find intercompany transactions that need elimination
        var icTxns = await _db.Set<IntercompanyTransaction>()
            .Where(t => memberCompanyIds.Contains(t.SourceCompanyId)
                && memberCompanyIds.Contains(t.TargetCompanyId)
                && t.Status == IntercompanyStatus.Completed
                && !t.IsEliminated
                && t.TransactionDate <= asOfDate)
            .ToListAsync();

        var companies = await _db.Companies
            .Where(c => memberCompanyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        var eliminations = new List<EliminationEntryResponse>();
        foreach (var txn in icTxns)
        {
            var sourceName = companies.GetValueOrDefault(txn.SourceCompanyId, "Unknown");
            var targetName = companies.GetValueOrDefault(txn.TargetCompanyId, "Unknown");

            // Elimination of intercompany receivable/payable
            eliminations.Add(new EliminationEntryResponse(
                $"ตัดรายการระหว่างกัน: {txn.TransactionNumber}",
                sourceName, targetName, txn.Amount,
                "IC-ELIM", "Intercompany Elimination"));
        }

        return eliminations;
    }

    // ===== Mapping Helpers =====

    private static ConsolidationGroupResponse MapToResponse(ConsolidationGroup g) =>
        new(g.Id, g.Name, g.Description, g.ParentCompanyId,
            g.ParentCompany?.Name ?? "", g.Currency, g.IsActive,
            g.Members.Where(m => m.IsActive).Select(m => new ConsolidationMemberResponse(
                m.Id, m.CompanyId, m.Company?.Name ?? "", m.OwnershipPercent,
                m.Method, m.IsActive)).ToList());
}
