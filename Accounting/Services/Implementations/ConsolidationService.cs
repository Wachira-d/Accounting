using Accounting.Data;
using Accounting.Models.DTOs.Consolidation;
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

    public async Task<ConsolidationGroupResponse> UpdateGroupAsync(Guid groupId, UpdateConsolidationGroupRequest request)
    {
        var group = await _db.Set<ConsolidationGroup>()
            .FirstOrDefaultAsync(g => g.Id == groupId && !g.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มบริษัท");

        if (request.Name != null) group.Name = request.Name;
        if (request.Description != null) group.Description = request.Description;
        if (request.Currency != null) group.Currency = request.Currency;
        if (request.IsActive.HasValue) group.IsActive = request.IsActive.Value;

        group.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await GetGroupAsync(groupId);
    }

    public async Task DeleteGroupAsync(Guid groupId)
    {
        var group = await _db.Set<ConsolidationGroup>()
            .FirstOrDefaultAsync(g => g.Id == groupId && !g.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มบริษัท");

        group.IsDeleted = true;
        group.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
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
            .Include(g => g.ParentCompany)
            .FirstOrDefaultAsync(g => g.Id == groupId && !g.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มบริษัท");

        var reportData = new Dictionary<string, object>();
        var minorityInterestData = new Dictionary<string, decimal>();
        var eliminationData = new List<object>();

        foreach (var member in group.Members.Where(m => m.IsActive))
        {
            var balances = await GetAccountBalances(member.CompanyId, null, asOfDate);

            var (consolidatedBalances, minorityInterest) = ApplyConsolidationMethod(
                member, balances, asOfDate);

            reportData[member.Company.Name] = consolidatedBalances;

            if (minorityInterest != 0)
            {
                minorityInterestData[member.Company.Name] = minorityInterest;
            }
        }

        // Generate elimination entries
        var eliminations = await GenerateEliminationEntries(group, asOfDate);
        if (eliminations.Count > 0)
        {
            eliminationData.AddRange(eliminations.Select(e => new
            {
                e.Description, e.SourceCompany, e.TargetCompany,
                e.Amount, e.AccountCode, e.AccountName
            }));
        }

        var report = new ConsolidationReport
        {
            ConsolidationGroupId = groupId,
            ReportType = "BalanceSheet",
            AsOfDate = asOfDate,
            ReportDataJson = JsonSerializer.Serialize(reportData),
            EliminationEntriesJson = eliminationData.Count > 0
                ? JsonSerializer.Serialize(eliminationData)
                : null,
            MinorityInterestJson = minorityInterestData.Count > 0
                ? JsonSerializer.Serialize(minorityInterestData)
                : null,
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
        var minorityInterestData = new Dictionary<string, decimal>();

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

            var netIncome = pnlData.Sum(b =>
                b.AccountType == AccountType.Revenue
                    ? -(b.Balance) // Revenue is credit-positive
                    : -(b.Balance)); // Expense is debit-positive

            var (multiplier, miPercent) = GetConsolidationMultiplier(member);

            var companyPnl = pnlData.Select(b => new
            {
                b.AccountCode,
                b.AccountName,
                AccountType = b.AccountType.ToString(),
                ConsolidatedAmount = b.Balance * multiplier
            }).ToList();

            reportData[member.Company.Name] = companyPnl;

            // Minority interest in net income
            if (miPercent > 0)
            {
                var totalNetIncome = pnlData.Sum(b => b.Balance);
                minorityInterestData[member.Company.Name] = totalNetIncome * miPercent;
            }
        }

        var report = new ConsolidationReport
        {
            ConsolidationGroupId = groupId,
            ReportType = "ProfitLoss",
            AsOfDate = toDate,
            FromDate = fromDate,
            ToDate = toDate,
            ReportDataJson = JsonSerializer.Serialize(reportData),
            MinorityInterestJson = minorityInterestData.Count > 0
                ? JsonSerializer.Serialize(minorityInterestData)
                : null,
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

        return await GenerateEliminationEntries(group, asOfDate);
    }

    // ===== Consolidation Method Logic =====

    /// <summary>
    /// Returns (multiplier, minorityInterestPercent) based on consolidation method
    /// </summary>
    private static (decimal multiplier, decimal minorityInterestPercent) GetConsolidationMultiplier(ConsolidationMember member)
    {
        return member.Method switch
        {
            // Full Consolidation: include 100% of subsidiary's accounts
            // Minority interest = (100% - ownership%) of net assets
            ConsolidationMethod.Full => (1m, (100m - member.OwnershipPercent) / 100m),

            // Proportionate Consolidation: include ownership% of each account
            ConsolidationMethod.Proportionate => (member.OwnershipPercent / 100m, 0m),

            // Equity Method: only include share of net income as single line
            // Investment recorded at cost + share of post-acquisition profits
            ConsolidationMethod.Equity => (member.OwnershipPercent / 100m, 0m),

            // Cost Method: investment at original cost (use ownership% for initial recognition)
            ConsolidationMethod.Cost => (member.OwnershipPercent / 100m, 0m),

            _ => (0m, 0m)
        };
    }

    private (List<object> consolidatedBalances, decimal minorityInterest) ApplyConsolidationMethod(
        ConsolidationMember member,
        List<(string AccountCode, string AccountName, string AccountType, decimal Balance)> balances,
        DateTime asOfDate)
    {
        var (multiplier, miPercent) = GetConsolidationMultiplier(member);

        if (member.Method == ConsolidationMethod.Equity)
        {
            // Equity method: show investment as single line with share of net income
            var totalEquity = balances
                .Where(b => b.AccountType == "Equity")
                .Sum(b => b.Balance);

            var shareOfEquity = totalEquity * (member.OwnershipPercent / 100m);

            var result = new List<object>
            {
                new
                {
                    AccountCode = "INVEST-EQ",
                    AccountName = $"Investment in {member.Company.Name} (Equity Method)",
                    AccountType = "Asset",
                    ConsolidatedBalance = shareOfEquity
                }
            };

            return (result, 0m);
        }

        if (member.Method == ConsolidationMethod.Cost)
        {
            // Cost method: investment at original cost (use total equity as proxy)
            var investmentCost = balances
                .Where(b => b.AccountType == "Equity")
                .Sum(b => b.Balance) * (member.OwnershipPercent / 100m);

            var result = new List<object>
            {
                new
                {
                    AccountCode = "INVEST-COST",
                    AccountName = $"Investment in {member.Company.Name} (Cost Method)",
                    AccountType = "Asset",
                    ConsolidatedBalance = investmentCost
                }
            };

            return (result, 0m);
        }

        // Full or Proportionate consolidation
        var consolidatedBalances = balances.Select(b => (object)new
        {
            b.AccountCode,
            b.AccountName,
            b.AccountType,
            ConsolidatedBalance = b.Balance * multiplier
        }).ToList();

        // Calculate minority interest for Full consolidation
        decimal minorityInterest = 0;
        if (member.Method == ConsolidationMethod.Full && miPercent > 0)
        {
            var totalNetAssets = balances
                .Where(b => b.AccountType == "Equity")
                .Sum(b => b.Balance);
            minorityInterest = totalNetAssets * miPercent;
        }

        return (consolidatedBalances, minorityInterest);
    }

    private async Task<List<(string AccountCode, string AccountName, string AccountType, decimal Balance)>> GetAccountBalances(
        Guid companyId, DateTime? fromDate, DateTime asOfDate)
    {
        var query = _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Include(l => l.Account)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate <= asOfDate);

        if (fromDate.HasValue)
            query = query.Where(l => l.JournalEntry.EntryDate >= fromDate.Value);

        var results = await query
            .GroupBy(l => new { l.AccountId, l.Account!.AccountCode, l.Account.AccountName, l.Account.AccountType })
            .Select(g => new
            {
                g.Key.AccountCode,
                g.Key.AccountName,
                AccountType = g.Key.AccountType.ToString(),
                Balance = g.Sum(l => l.DebitAmount - l.CreditAmount)
            })
            .ToListAsync();

        return results.Select(b =>
            (b.AccountCode, b.AccountName, b.AccountType, b.Balance)).ToList();
    }

    private async Task<List<EliminationEntryResponse>> GenerateEliminationEntries(ConsolidationGroup group, DateTime asOfDate)
    {
        var memberCompanyIds = group.Members
            .Where(m => m.IsActive)
            .Select(m => m.CompanyId)
            .ToList();

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

            // Eliminate intercompany receivable/payable
            eliminations.Add(new EliminationEntryResponse(
                $"ตัดรายการระหว่างกัน: {txn.TransactionNumber}",
                sourceName, targetName, txn.Amount,
                "IC-ELIM-AR", "ลูกหนี้ระหว่างกัน (Intercompany AR Elimination)"));

            eliminations.Add(new EliminationEntryResponse(
                $"ตัดรายการระหว่างกัน: {txn.TransactionNumber}",
                targetName, sourceName, txn.Amount,
                "IC-ELIM-AP", "เจ้าหนี้ระหว่างกัน (Intercompany AP Elimination)"));
        }

        // Eliminate intercompany revenue/expense
        var icRevenue = await _db.Documents
            .Where(d => memberCompanyIds.Contains(d.CompanyId)
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                // เอกสารรับรู้รายได้เท่านั้น: ใบแจ้งหนี้/ใบกำกับ + ใบเสร็จขายสด
                // standalone. ใบเสร็จตัดชำระ (RelatedDocumentId) = ไม่ใช่รายได้ →
                // ตัดออก กันตัดรายการระหว่างกันซ้ำ (over-elimination).
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice
                    || ((d.DocumentType == DocumentType.Receipt || d.DocumentType == DocumentType.ReceiptVoucher)
                        && d.RelatedDocumentId == null))
                && d.DocumentDate <= asOfDate
                && d.Contact != null
                && memberCompanyIds.Contains(d.Contact.CompanyId))
            .ToListAsync();

        foreach (var doc in icRevenue)
        {
            var sourceName = companies.GetValueOrDefault(doc.CompanyId, "Unknown");
            eliminations.Add(new EliminationEntryResponse(
                $"ตัดรายได้/ค่าใช้จ่ายระหว่างกัน: {doc.DocumentNumber}",
                sourceName, "", doc.TotalAmount,
                "IC-ELIM-REV", "รายได้ระหว่างกัน (Intercompany Revenue Elimination)"));
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
