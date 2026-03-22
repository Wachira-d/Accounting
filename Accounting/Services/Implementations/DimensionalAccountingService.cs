using Accounting.Data;
using Accounting.Models.DTOs.Dimension;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class DimensionalAccountingService : IDimensionalAccountingService
{
    private readonly AccountingDbContext _db;

    public DimensionalAccountingService(AccountingDbContext db)
    {
        _db = db;
    }

    // ===== Dimensions =====

    public async Task<DimensionResponse> CreateDimensionAsync(Guid companyId, CreateDimensionRequest request)
    {
        var existing = await _db.Set<AccountingDimension>()
            .AnyAsync(d => d.CompanyId == companyId && d.Code == request.Code);
        if (existing)
            throw new InvalidOperationException($"รหัสมิติ {request.Code} ซ้ำ");

        var level = 1;
        if (request.ParentId.HasValue)
        {
            var parent = await _db.Set<AccountingDimension>()
                .FirstOrDefaultAsync(d => d.Id == request.ParentId.Value && d.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบมิติหลัก");
            level = parent.Level + 1;
        }

        var dimension = new AccountingDimension
        {
            CompanyId = companyId,
            Code = request.Code,
            Name = request.Name,
            NameEn = request.NameEn,
            DimensionType = request.DimensionType,
            ParentId = request.ParentId,
            Level = level,
            Description = request.Description,
            ManagerName = request.ManagerName,
            ManagerEmail = request.ManagerEmail,
            AnnualBudget = request.AnnualBudget
        };

        _db.Set<AccountingDimension>().Add(dimension);
        await _db.SaveChangesAsync();

        return await GetDimensionAsync(companyId, dimension.Id);
    }

    public async Task<List<DimensionResponse>> GetDimensionsAsync(Guid companyId, DimensionType? type = null)
    {
        var query = _db.Set<AccountingDimension>()
            .Include(d => d.Children)
            .Where(d => d.CompanyId == companyId && d.IsActive);

        if (type.HasValue)
            query = query.Where(d => d.DimensionType == type.Value);

        var dimensions = await query
            .Where(d => d.ParentId == null)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Code)
            .ToListAsync();

        return dimensions.Select(MapToDimensionResponse).ToList();
    }

    public async Task<DimensionResponse> GetDimensionAsync(Guid companyId, Guid dimensionId)
    {
        var dimension = await _db.Set<AccountingDimension>()
            .Include(d => d.Children)
            .FirstOrDefaultAsync(d => d.Id == dimensionId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบมิติบัญชี");

        return MapToDimensionResponse(dimension);
    }

    public async Task<DimensionResponse> UpdateDimensionAsync(Guid companyId, Guid dimensionId, UpdateDimensionRequest request)
    {
        var dimension = await _db.Set<AccountingDimension>()
            .FirstOrDefaultAsync(d => d.Id == dimensionId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบมิติบัญชี");

        if (request.Name != null) dimension.Name = request.Name;
        if (request.NameEn != null) dimension.NameEn = request.NameEn;
        if (request.Description != null) dimension.Description = request.Description;
        if (request.ManagerName != null) dimension.ManagerName = request.ManagerName;
        if (request.ManagerEmail != null) dimension.ManagerEmail = request.ManagerEmail;
        if (request.AnnualBudget.HasValue) dimension.AnnualBudget = request.AnnualBudget.Value;
        if (request.IsActive.HasValue) dimension.IsActive = request.IsActive.Value;
        if (request.SortOrder.HasValue) dimension.SortOrder = request.SortOrder.Value;

        await _db.SaveChangesAsync();
        return await GetDimensionAsync(companyId, dimensionId);
    }

    public async Task DeleteDimensionAsync(Guid companyId, Guid dimensionId)
    {
        var dimension = await _db.Set<AccountingDimension>()
            .Include(d => d.Children)
            .FirstOrDefaultAsync(d => d.Id == dimensionId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบมิติบัญชี");

        if (dimension.Children.Any())
            throw new InvalidOperationException("ไม่สามารถลบมิติที่มีมิติย่อยได้");

        dimension.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ===== Dimension Allocation =====

    public async Task AssignDimensionsAsync(Guid companyId, Guid journalEntryLineId, List<DimensionAllocationRequest> allocations)
    {
        var line = await _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .FirstOrDefaultAsync(l => l.Id == journalEntryLineId && l.JournalEntry.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการบันทึกบัญชี");

        // Remove existing allocations
        var existing = await _db.Set<JournalLineDimension>()
            .Where(d => d.JournalEntryLineId == journalEntryLineId)
            .ToListAsync();
        _db.Set<JournalLineDimension>().RemoveRange(existing);

        // Add new allocations
        foreach (var alloc in allocations)
        {
            _db.Set<JournalLineDimension>().Add(new JournalLineDimension
            {
                CompanyId = companyId,
                JournalEntryLineId = journalEntryLineId,
                DimensionId = alloc.DimensionId,
                AllocatedAmount = alloc.Amount,
                AllocatedPercent = alloc.Percent
            });
        }

        await _db.SaveChangesAsync();
    }

    public async Task<List<DimensionAllocationResponse>> GetLineDimensionsAsync(Guid companyId, Guid journalEntryLineId)
    {
        var allocations = await _db.Set<JournalLineDimension>()
            .Include(d => d.Dimension)
            .Where(d => d.JournalEntryLineId == journalEntryLineId && d.CompanyId == companyId)
            .ToListAsync();

        return allocations.Select(a => new DimensionAllocationResponse(
            a.DimensionId,
            a.Dimension.Code,
            a.Dimension.Name,
            a.Dimension.DimensionType,
            a.AllocatedAmount,
            a.AllocatedPercent
        )).ToList();
    }

    // ===== Reports =====

    public async Task<DimensionPnLResponse> GetDimensionPnLAsync(Guid companyId, Guid dimensionId, DateTime fromDate, DateTime toDate)
    {
        var dimension = await _db.Set<AccountingDimension>()
            .FirstOrDefaultAsync(d => d.Id == dimensionId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบมิติบัญชี");

        var lines = await _db.Set<JournalLineDimension>()
            .Include(jld => jld.JournalEntryLine).ThenInclude(l => l.JournalEntry)
            .Include(jld => jld.JournalEntryLine).ThenInclude(l => l.Account)
            .Where(jld => jld.DimensionId == dimensionId
                && jld.CompanyId == companyId
                && jld.JournalEntryLine.JournalEntry.Status == JournalEntryStatus.Posted
                && jld.JournalEntryLine.JournalEntry.EntryDate >= fromDate
                && jld.JournalEntryLine.JournalEntry.EntryDate <= toDate)
            .ToListAsync();

        var pnlLines = lines
            .GroupBy(l => new { l.JournalEntryLine.AccountId, l.JournalEntryLine.Account!.AccountCode, l.JournalEntryLine.Account.AccountName })
            .Select(g => new DimensionPnLLine(
                g.Key.AccountCode,
                g.Key.AccountName,
                g.Sum(l => l.AllocatedAmount ?? (l.JournalEntryLine.DebitAmount - l.JournalEntryLine.CreditAmount))
            ))
            .ToList();

        var totalRevenue = lines
            .Where(l => l.JournalEntryLine.Account?.AccountType == AccountType.Revenue)
            .Sum(l => l.AllocatedAmount ?? (l.JournalEntryLine.CreditAmount - l.JournalEntryLine.DebitAmount));

        var totalExpenses = lines
            .Where(l => l.JournalEntryLine.Account?.AccountType == AccountType.Expense)
            .Sum(l => l.AllocatedAmount ?? (l.JournalEntryLine.DebitAmount - l.JournalEntryLine.CreditAmount));

        return new DimensionPnLResponse(
            dimensionId, dimension.Name, fromDate, toDate,
            totalRevenue, totalExpenses, totalRevenue - totalExpenses, pnlLines);
    }

    public async Task<List<DimensionSummaryResponse>> GetDimensionSummaryAsync(Guid companyId, DimensionType type, DateTime fromDate, DateTime toDate)
    {
        var dimensions = await _db.Set<AccountingDimension>()
            .Where(d => d.CompanyId == companyId && d.DimensionType == type && d.IsActive)
            .ToListAsync();

        var allAllocations = await _db.Set<JournalLineDimension>()
            .Include(jld => jld.JournalEntryLine).ThenInclude(l => l.JournalEntry)
            .Include(jld => jld.JournalEntryLine).ThenInclude(l => l.Account)
            .Where(jld => jld.CompanyId == companyId
                && jld.JournalEntryLine.JournalEntry.Status == JournalEntryStatus.Posted
                && jld.JournalEntryLine.JournalEntry.EntryDate >= fromDate
                && jld.JournalEntryLine.JournalEntry.EntryDate <= toDate)
            .ToListAsync();

        var result = new List<DimensionSummaryResponse>();
        foreach (var dim in dimensions)
        {
            var dimAllocations = allAllocations.Where(a => a.DimensionId == dim.Id).ToList();

            var revenue = dimAllocations
                .Where(a => a.JournalEntryLine.Account?.AccountType == AccountType.Revenue)
                .Sum(a => a.AllocatedAmount ?? (a.JournalEntryLine.CreditAmount - a.JournalEntryLine.DebitAmount));

            var expenses = dimAllocations
                .Where(a => a.JournalEntryLine.Account?.AccountType == AccountType.Expense)
                .Sum(a => a.AllocatedAmount ?? (a.JournalEntryLine.DebitAmount - a.JournalEntryLine.CreditAmount));

            var netIncome = revenue - expenses;
            var variance = dim.AnnualBudget.HasValue ? dim.AnnualBudget.Value - expenses : (decimal?)null;

            result.Add(new DimensionSummaryResponse(
                dim.Id, dim.Code, dim.Name, dim.DimensionType,
                revenue, expenses, netIncome, dim.AnnualBudget, variance));
        }

        return result;
    }

    // ===== Branches =====

    public async Task<BranchResponse> CreateBranchAsync(Guid companyId, CreateBranchRequest request)
    {
        var existing = await _db.Set<Branch>()
            .AnyAsync(b => b.CompanyId == companyId && b.Code == request.Code);
        if (existing)
            throw new InvalidOperationException($"รหัสสาขา {request.Code} ซ้ำ");

        var branch = new Branch
        {
            CompanyId = companyId,
            Code = request.Code,
            Name = request.Name,
            NameEn = request.NameEn,
            Address = request.Address,
            SubDistrict = request.SubDistrict,
            District = request.District,
            Province = request.Province,
            PostalCode = request.PostalCode,
            Phone = request.Phone,
            Email = request.Email,
            TaxBranchCode = request.TaxBranchCode,
            IsHeadOffice = request.IsHeadOffice,
            ManagerName = request.ManagerName
        };

        _db.Set<Branch>().Add(branch);
        await _db.SaveChangesAsync();

        return MapToBranchResponse(branch);
    }

    public async Task<List<BranchResponse>> GetBranchesAsync(Guid companyId)
    {
        var branches = await _db.Set<Branch>()
            .Where(b => b.CompanyId == companyId && b.IsActive)
            .OrderBy(b => b.Code)
            .ToListAsync();

        return branches.Select(MapToBranchResponse).ToList();
    }

    public async Task<BranchResponse> UpdateBranchAsync(Guid companyId, Guid branchId, UpdateBranchRequest request)
    {
        var branch = await _db.Set<Branch>()
            .FirstOrDefaultAsync(b => b.Id == branchId && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสาขา");

        if (request.Name != null) branch.Name = request.Name;
        if (request.NameEn != null) branch.NameEn = request.NameEn;
        if (request.Address != null) branch.Address = request.Address;
        if (request.Phone != null) branch.Phone = request.Phone;
        if (request.Email != null) branch.Email = request.Email;
        if (request.TaxBranchCode != null) branch.TaxBranchCode = request.TaxBranchCode;
        if (request.ManagerName != null) branch.ManagerName = request.ManagerName;
        if (request.IsActive.HasValue) branch.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
        return MapToBranchResponse(branch);
    }

    // ===== Mapping Helpers =====

    private static DimensionResponse MapToDimensionResponse(AccountingDimension d) =>
        new(d.Id, d.Code, d.Name, d.NameEn, d.DimensionType, d.ParentId, d.Level,
            d.Description, d.ManagerName, d.AnnualBudget, d.IsActive,
            d.Children?.Select(MapToDimensionResponse).ToList());

    private static BranchResponse MapToBranchResponse(Branch b) =>
        new(b.Id, b.Code, b.Name, b.NameEn, b.Address, b.Province,
            b.TaxBranchCode, b.IsHeadOffice, b.IsActive, b.ManagerName);
}
