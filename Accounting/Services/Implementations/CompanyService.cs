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
    private readonly ISubscriptionService? _subscriptionService;

    public CompanyService(AccountingDbContext db, IAccountingService accountingService, ISubscriptionService? subscriptionService = null)
    {
        _db = db;
        _accountingService = accountingService;
        _subscriptionService = subscriptionService;
    }

    public async Task<CompanyResponse> CreateAsync(Guid userId, CreateCompanyRequest request)
    {
        var company = new Company
        {
            Name = request.Name,
            NameEn = request.NameEn,
            TaxId = request.TaxId,
            BranchCode = request.BranchCode ?? "00000",
            BranchName = request.BranchName,
            BusinessType = request.BusinessType,
            IndustryType = request.IndustryType,
            JuristicId = request.JuristicId,
            IsVatRegistered = request.IsVatRegistered,
            VatRate = request.VatRate,
            IsWhtRegistered = request.IsWhtRegistered,
            IsSocialSecurityRegistered = request.IsSocialSecurityRegistered,
            SocialSecurityAccountNo = request.SocialSecurityAccountNo,
            BuildingNumber = request.BuildingNumber,
            BuildingName = request.BuildingName,
            StreetName = request.StreetName,
            SubDistrict = request.SubDistrict,
            District = request.District,
            Province = request.Province,
            PostalCode = request.PostalCode,
            Phone = request.Phone,
            Fax = request.Fax,
            Email = request.Email,
            Website = request.Website,
            FiscalYearStartMonth = request.FiscalYearStartMonth,
            CreatedBy = userId.ToString()
        };

        company.Address = request.Address ?? ComposeAddress(company);

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

        // Every Company needs a Subscription row so the per-month usage
        // counters have somewhere to live. If the creator already has an
        // active AccountSubscription, point the Subscription at it so
        // limits + features come from the account plan (License is on the
        // User, not the Company — Case 1: owner subscribes / Case 2:
        // accountant subscribes both work out of the box).
        await EnsureSubscriptionForNewCompanyAsync(company.Id, userId);

        return await GetByIdAsync(company.Id, userId);
    }

    /// <summary>Creates the Subscription row for a brand-new Company and, when
    /// the creating user has an active AccountSubscription with quota free,
    /// auto-attaches so the Company immediately rides the user's account plan.
    /// Failures are logged but never blow up the create flow — a missing
    /// Subscription row is recoverable later, but a failed Company create
    /// because of a billing hiccup is not.</summary>
    private async Task EnsureSubscriptionForNewCompanyAsync(Guid companyId, Guid userId)
    {
        try
        {
            var existing = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted);
            if (existing == null)
            {
                // Seed a minimal FreeTrial Subscription so usage counters / quota
                // checks don't NRE. The detailed plan-template-driven setup lives
                // in SubscriptionService.StartTrialAsync; we replicate the minimal
                // shape here to avoid a circular dep on the full service.
                _db.Subscriptions.Add(new Subscription
                {
                    CompanyId = companyId,
                    Plan = SubscriptionPlan.FreeTrial,
                    Status = SubscriptionStatus.Trial,
                    StartDate = DateTime.UtcNow,
                    EndDate = DateTime.UtcNow.AddYears(100),  // Free perpetual
                    MaxUsers = 1, MaxCompanies = 1,
                    MaxDocumentsPerMonth = 30, MaxJournalEntriesPerMonth = 50,
                    MaxStorageBytes = 100L * 1024 * 1024,
                });
                await _db.SaveChangesAsync();
            }

            // Auto-attach to creator's AccountSubscription if it has a slot.
            var acct = await _db.AccountSubscriptions
                .Where(a => a.OwnerUserId == userId && !a.IsDeleted
                    && (a.Status == SubscriptionStatus.Trial
                        || a.Status == SubscriptionStatus.Active
                        || a.Status == SubscriptionStatus.PastDue))
                .FirstOrDefaultAsync();
            if (acct == null) return;

            var used = await _db.Subscriptions.CountAsync(s => s.AccountSubscriptionId == acct.Id && !s.IsDeleted);
            if (used >= acct.MaxCompanies) return;  // out of slots — user manages manually

            var sub = await _db.Subscriptions.FirstAsync(s => s.CompanyId == companyId && !s.IsDeleted);
            if (sub.AccountSubscriptionId == null)
            {
                sub.AccountSubscriptionId = acct.Id;
                await _db.SaveChangesAsync();
            }
        }
        catch
        {
            // Subscription wiring failures are non-fatal — the Company still
            // exists, the user can finish setup from the billing page later.
        }
    }

    public async Task<CompanyResponse> GetByIdAsync(Guid companyId, Guid userId)
    {
        var company = await _db.Companies
            .Include(c => c.Subscription)
            .ThenInclude(s => s!.TrialConfig)
            .FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        var cu = await _db.CompanyUsers
            .Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
            .Select(cu => new { cu.Role })
            .FirstOrDefaultAsync();
        if (cu == null) throw new UnauthorizedAccessException("ไม่มีสิทธิ์เข้าถึงบริษัทนี้");

        return MapToResponse(company) with { MyRole = cu.Role.ToString() };
    }

    public async Task<List<CompanyResponse>> GetUserCompaniesAsync(Guid userId)
    {
        // Project only the columns we need so a missing CompanyRoleId column
        // (e.g. before DatabaseMigrationHelper has applied the new migration)
        // can never break the company-list endpoint.
        var companyUsers = await _db.CompanyUsers
            .Where(cu => cu.UserId == userId)
            .Select(cu => new { cu.CompanyId, cu.Role })
            .ToListAsync();

        var companyIds = companyUsers.Select(cu => cu.CompanyId).ToList();
        var roleMap = companyUsers.ToDictionary(cu => cu.CompanyId, cu => cu.Role.ToString());

        var companies = await _db.Companies
            .Include(c => c.Subscription)
            .ThenInclude(s => s!.TrialConfig)
            .Where(c => companyIds.Contains(c.Id))
            .ToListAsync();

        return companies.Select(c => {
            var resp = MapToResponse(c);
            roleMap.TryGetValue(c.Id, out var role);
            return resp with { MyRole = role };
        }).ToList();
    }

    public async Task<CompanyResponse> UpdateAsync(Guid companyId, Guid userId, UpdateCompanyRequest request)
    {
        await EnsureOwnerAccessAsync(companyId, userId);

        var company = await _db.Companies.FindAsync(companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        if (request.Name != null) company.Name = request.Name;
        if (request.NameEn != null) company.NameEn = request.NameEn;
        if (request.TaxId != null) company.TaxId = request.TaxId;
        if (request.BranchCode != null) company.BranchCode = request.BranchCode;
        if (request.BranchName != null) company.BranchName = request.BranchName;
        if (request.BusinessType.HasValue) company.BusinessType = request.BusinessType.Value;
        if (request.IndustryType.HasValue) company.IndustryType = request.IndustryType.Value;
        if (request.JuristicId != null) company.JuristicId = request.JuristicId;
        if (request.IsVatRegistered.HasValue) company.IsVatRegistered = request.IsVatRegistered.Value;
        if (request.VatRate.HasValue) company.VatRate = request.VatRate.Value;
        if (request.IsWhtRegistered.HasValue) company.IsWhtRegistered = request.IsWhtRegistered.Value;
        if (request.IsSocialSecurityRegistered.HasValue) company.IsSocialSecurityRegistered = request.IsSocialSecurityRegistered.Value;
        if (request.SocialSecurityAccountNo != null) company.SocialSecurityAccountNo = request.SocialSecurityAccountNo;
        if (request.BuildingNumber != null) company.BuildingNumber = request.BuildingNumber;
        if (request.BuildingName != null) company.BuildingName = request.BuildingName;
        if (request.StreetName != null) company.StreetName = request.StreetName;
        if (request.SubDistrict != null) company.SubDistrict = request.SubDistrict;
        if (request.District != null) company.District = request.District;
        if (request.Province != null) company.Province = request.Province;
        if (request.PostalCode != null) company.PostalCode = request.PostalCode;
        company.Address = request.Address ?? ComposeAddress(company);
        if (request.Phone != null) company.Phone = request.Phone;
        if (request.Fax != null) company.Fax = request.Fax;
        if (request.Email != null) company.Email = request.Email;
        if (request.Website != null) company.Website = request.Website;
        if (request.FiscalYearStartMonth.HasValue) company.FiscalYearStartMonth = request.FiscalYearStartMonth.Value;
        if (request.IsSetupComplete.HasValue) company.IsSetupComplete = request.IsSetupComplete.Value;

        company.UpdatedBy = userId.ToString();
        await _db.SaveChangesAsync();

        return await GetByIdAsync(companyId, userId);
    }

    public async Task<List<CompanyMemberResponse>> GetMembersAsync(Guid companyId, Guid userId)
    {
        // Verify the requesting user belongs to this company
        var isMember = await _db.CompanyUsers.AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == userId);
        if (!isMember) throw new UnauthorizedAccessException("คุณไม่ได้เป็นสมาชิกของบริษัทนี้");

        return await _db.CompanyUsers
            .Where(cu => cu.CompanyId == companyId)
            .Include(cu => cu.User)
            .Include(cu => cu.CompanyRole)
            .OrderBy(cu => cu.Role).ThenBy(cu => cu.User.FullName)
            .Select(cu => new CompanyMemberResponse(
                cu.UserId,
                cu.User.FullName,
                cu.User.Email,
                cu.User.Phone,
                cu.Role,
                cu.CompanyRoleId,
                cu.CompanyRoleId != null ? cu.CompanyRole!.Name : null,
                cu.JoinedAt,
                cu.User.LastLoginAt,
                cu.User.Status))
            .ToListAsync();
    }

    public async Task AddUserAsync(Guid companyId, Guid ownerId, AddCompanyUserRequest request)
    {
        await EnsureOwnerAccessAsync(companyId, ownerId);

        // Hard limit: check effective MaxUsers — License's MaxUsersPerCompany
        // wins when the company is attached, otherwise per-company sub.MaxUsers.
        // Without the overlay an Enterprise License (e.g. 100 users) is
        // capped at the stale per-company FreeTrial limit (1).
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted);
        if (sub != null)
        {
            int maxUsers = sub.MaxUsers;
            if (sub.AccountSubscriptionId.HasValue)
            {
                var acct = await _db.AccountSubscriptions.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == sub.AccountSubscriptionId.Value && !a.IsDeleted);
                if (acct != null) maxUsers = acct.MaxUsersPerCompany;
            }
            var currentCount = await _db.CompanyUsers.CountAsync(cu => cu.CompanyId == companyId);
            if (currentCount >= maxUsers)
                throw new InvalidOperationException($"จำนวนผู้ใช้เต็มแล้ว ({currentCount}/{maxUsers}) กรุณาอัปเกรดแพ็กเกจ");
        }

        var targetUser = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email)
            ?? throw new KeyNotFoundException($"ไม่พบผู้ใช้อีเมล {request.Email} ในระบบ (ผู้ใช้ต้องสมัครสมาชิกก่อน)");

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

        // Refuse to leave the company without an Owner. If this is the last
        // Owner standing, removal would orphan the company — block here.
        if (cu.Role == UserRole.Owner)
        {
            var ownerCount = await _db.CompanyUsers
                .CountAsync(x => x.CompanyId == companyId && x.Role == UserRole.Owner);
            if (ownerCount <= 1)
                throw new InvalidOperationException("ลบไม่ได้ — บริษัทต้องมีเจ้าของอย่างน้อย 1 คน");
        }

        _db.CompanyUsers.Remove(cu);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateUserRoleAsync(Guid companyId, Guid ownerId, Guid targetUserId, UserRole newRole)
    {
        await EnsureOwnerAccessAsync(companyId, ownerId);

        if (ownerId == targetUserId)
            throw new InvalidOperationException("ไม่สามารถเปลี่ยน Role ของตัวเองได้");

        var cu = await _db.CompanyUsers.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == targetUserId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ใช้ในบริษัท");

        // Demoting the last Owner would lock the company. Block if target is
        // currently Owner, the new role isn't Owner, and they're the only one.
        if (cu.Role == UserRole.Owner && newRole != UserRole.Owner)
        {
            var ownerCount = await _db.CompanyUsers
                .CountAsync(x => x.CompanyId == companyId && x.Role == UserRole.Owner);
            if (ownerCount <= 1)
                throw new InvalidOperationException("ลดสิทธิ์ไม่ได้ — บริษัทต้องมีเจ้าของอย่างน้อย 1 คน");
        }

        cu.Role = newRole;
        await _db.SaveChangesAsync();
    }

    public async Task EnsureOwnerAccessAsync(Guid companyId, Guid userId)
    {
        // Platform SystemAdmin bypasses company-level owner check
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user?.IsSystemAdmin == true) return;

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
            c.Id, c.Name, c.NameEn, c.TaxId, c.BranchCode, c.BranchName,
            c.BusinessType, c.IndustryType, c.Status, c.JuristicId,
            c.IsVatRegistered, c.VatRate, c.IsWhtRegistered,
            c.IsSocialSecurityRegistered, c.SocialSecurityAccountNo,
            c.Address, c.BuildingNumber, c.BuildingName, c.StreetName,
            c.SubDistrict, c.District, c.Province,
            c.PostalCode, c.Phone, c.Fax, c.Email, c.Website,
            c.FiscalYearStartMonth, c.IsSetupComplete, sub);
    }

    private static string? ComposeAddress(Company c)
    {
        var parts = new[] { c.BuildingNumber, c.BuildingName,
            string.IsNullOrEmpty(c.StreetName) ? null : "ถ." + c.StreetName,
            c.SubDistrict, c.District, c.Province, c.PostalCode }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var joined = string.Join(" ", parts);
        return string.IsNullOrEmpty(joined) ? null : joined;
    }
}
