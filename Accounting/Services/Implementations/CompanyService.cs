using Accounting.Data;
using Accounting.Models.DTOs.Company;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CompanyService : ICompanyService
{
    private readonly AccountingDbContext _db;
    private readonly IAccountingService _accountingService;

    public CompanyService(AccountingDbContext db, IAccountingService accountingService)
    {
        _db = db;
        _accountingService = accountingService;
    }

    public async Task<CompanyResponse> CreateAsync(Guid userId, CreateCompanyRequest request)
    {
        var company = new Company
        {
            Name = request.Name,
            NameEn = request.NameEn,
            TaxId = request.TaxId,
            BranchCode = request.BranchCode ?? "00000",
            BusinessType = request.BusinessType,
            Address = request.Address,
            SubDistrict = request.SubDistrict,
            District = request.District,
            Province = request.Province,
            PostalCode = request.PostalCode,
            Phone = request.Phone,
            Email = request.Email,
            FiscalYearStartMonth = request.FiscalYearStartMonth,
            CreatedBy = userId.ToString()
        };

        _db.Companies.Add(company);

        // Add creator as Owner
        _db.CompanyUsers.Add(new CompanyUser
        {
            UserId = userId,
            CompanyId = company.Id,
            Role = UserRole.Owner,
            IsDefault = true
        });

        await _db.SaveChangesAsync();

        // Seed default chart of accounts
        await _accountingService.SeedDefaultAccountsAsync(company.Id);

        return await GetByIdAsync(company.Id, userId);
    }

    public async Task<CompanyResponse> GetByIdAsync(Guid companyId, Guid userId)
    {
        var company = await _db.Companies
            .Include(c => c.Subscription)
            .ThenInclude(s => s!.TrialConfig)
            .FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        // Check access
        var hasAccess = await _db.CompanyUsers.AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == userId);
        if (!hasAccess) throw new UnauthorizedAccessException("ไม่มีสิทธิ์เข้าถึงบริษัทนี้");

        return MapToResponse(company);
    }

    public async Task<List<CompanyResponse>> GetUserCompaniesAsync(Guid userId)
    {
        var companyIds = await _db.CompanyUsers
            .Where(cu => cu.UserId == userId)
            .Select(cu => cu.CompanyId)
            .ToListAsync();

        var companies = await _db.Companies
            .Include(c => c.Subscription)
            .ThenInclude(s => s!.TrialConfig)
            .Where(c => companyIds.Contains(c.Id))
            .ToListAsync();

        return companies.Select(MapToResponse).ToList();
    }

    public async Task<CompanyResponse> UpdateAsync(Guid companyId, Guid userId, UpdateCompanyRequest request)
    {
        await EnsureOwnerAccessAsync(companyId, userId);

        var company = await _db.Companies.FindAsync(companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        if (request.Name != null) company.Name = request.Name;
        if (request.NameEn != null) company.NameEn = request.NameEn;
        if (request.Address != null) company.Address = request.Address;
        if (request.SubDistrict != null) company.SubDistrict = request.SubDistrict;
        if (request.District != null) company.District = request.District;
        if (request.Province != null) company.Province = request.Province;
        if (request.PostalCode != null) company.PostalCode = request.PostalCode;
        if (request.Phone != null) company.Phone = request.Phone;
        if (request.Email != null) company.Email = request.Email;
        if (request.FiscalYearStartMonth.HasValue) company.FiscalYearStartMonth = request.FiscalYearStartMonth.Value;

        company.UpdatedBy = userId.ToString();
        await _db.SaveChangesAsync();

        return await GetByIdAsync(companyId, userId);
    }

    public async Task AddUserAsync(Guid companyId, Guid ownerId, AddCompanyUserRequest request)
    {
        await EnsureOwnerAccessAsync(companyId, ownerId);

        var targetUser = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email)
            ?? throw new KeyNotFoundException($"ไม่พบผู้ใช้อีเมล {request.Email}");

        if (await _db.CompanyUsers.AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == targetUser.Id))
            throw new InvalidOperationException("ผู้ใช้นี้เป็นสมาชิกอยู่แล้ว");

        _db.CompanyUsers.Add(new CompanyUser
        {
            UserId = targetUser.Id,
            CompanyId = companyId,
            Role = request.Role
        });

        await _db.SaveChangesAsync();
    }

    public async Task RemoveUserAsync(Guid companyId, Guid ownerId, Guid targetUserId)
    {
        await EnsureOwnerAccessAsync(companyId, ownerId);

        if (ownerId == targetUserId)
            throw new InvalidOperationException("ไม่สามารถลบตัวเองออกจากบริษัทได้");

        var cu = await _db.CompanyUsers.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == targetUserId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ใช้ในบริษัท");

        _db.CompanyUsers.Remove(cu);
        await _db.SaveChangesAsync();
    }

    private async Task EnsureOwnerAccessAsync(Guid companyId, Guid userId)
    {
        var cu = await _db.CompanyUsers.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == userId);
        if (cu == null || (cu.Role != UserRole.Owner && cu.Role != UserRole.SystemAdmin))
            throw new UnauthorizedAccessException("ต้องเป็น Owner เท่านั้น");
    }

    private static CompanyResponse MapToResponse(Company c)
    {
        SubscriptionSummary? sub = null;
        if (c.Subscription != null)
        {
            TrialSummary? trial = null;
            if (c.Subscription.TrialConfig != null)
            {
                var tc = c.Subscription.TrialConfig;
                trial = new TrialSummary(
                    tc.TrialStatus,
                    tc.TrialEndDate,
                    Math.Max(0, (int)(tc.TrialEndDate - DateTime.UtcNow).TotalDays),
                    tc.ExtensionsUsed,
                    tc.MaxExtensions);
            }
            sub = new SubscriptionSummary(c.Subscription.Plan, c.Subscription.Status, c.Subscription.EndDate, trial);
        }

        return new CompanyResponse(
            c.Id, c.Name, c.NameEn, c.TaxId, c.BranchCode,
            c.BusinessType, c.Status, c.Address, c.Province,
            c.FiscalYearStartMonth, sub);
    }
}
