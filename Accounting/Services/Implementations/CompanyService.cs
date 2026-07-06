using Accounting.Data;
using Accounting.Models.DTOs.Company;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

public class CompanyService : ICompanyService
{
    private readonly AccountingDbContext _db;
    private readonly IAccountingService _accountingService;
    private readonly ISubscriptionService? _subscriptionService;
    private readonly IEmailService? _emailService;
    private readonly ILogger<CompanyService>? _logger;
    private readonly IConfiguration? _config;

    public CompanyService(AccountingDbContext db, IAccountingService accountingService,
        ISubscriptionService? subscriptionService = null,
        IEmailService? emailService = null,
        ILogger<CompanyService>? logger = null,
        IConfiguration? config = null)
    {
        _db = db;
        _accountingService = accountingService;
        _subscriptionService = subscriptionService;
        _emailService = emailService;
        _logger = logger;
        _config = config;
    }

    /// <summary>Lowercase + trim — the canonical form we store, search, and
    /// compare emails by. Without this an Owner inviting "Alice@Example.COM"
    /// would never match Alice's account registered as "alice@example.com".</summary>
    private static string NormalizeEmail(string? email) =>
        (email ?? string.Empty).Trim().ToLowerInvariant();

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
            Moo = request.Moo,
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
    public async Task EnsureSubscriptionForNewCompanyAsync(Guid companyId, Guid userId)
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
        if (request.Moo != null) company.Moo = request.Moo;
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

    public async Task<AddUserResult> AddUserAsync(Guid companyId, Guid ownerId, AddCompanyUserRequest request)
    {
        await EnsureOwnerAccessAsync(companyId, ownerId);

        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrEmpty(email))
            throw new InvalidOperationException("กรุณาระบุอีเมล");

        // Hard limit: check effective MaxUsers — License's MaxUsersPerCompany
        // wins when the company is attached, otherwise per-company sub.MaxUsers.
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
            // Pending invitations count towards the quota too — once accepted
            // they become CompanyUsers, so a tenant can't sneak past the cap
            // by spamming invites.
            var currentCount = await _db.CompanyUsers.CountAsync(cu => cu.CompanyId == companyId);
            var pendingInvites = await _db.CompanyInvitations.CountAsync(i =>
                i.CompanyId == companyId && i.Status == InvitationStatus.Pending && !i.IsDeleted);
            if (currentCount + pendingInvites >= maxUsers)
                throw new InvalidOperationException(
                    $"จำนวนผู้ใช้เต็มแล้ว ({currentCount}/{maxUsers}, มีคำเชิญรออยู่ {pendingInvites}) กรุณาอัปเกรดแพ็กเกจ");
        }

        // Case-insensitive user lookup. EF Core translates ToLower() to LOWER()
        // on the server — works without an index but indexed lookups on a
        // computed-column would be faster at scale. (Out of scope here.)
        var targetUser = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email);

        if (targetUser != null)
        {
            if (await _db.CompanyUsers.AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == targetUser.Id))
                throw new InvalidOperationException("ผู้ใช้นี้เป็นสมาชิกอยู่แล้ว");
            _db.CompanyUsers.Add(new CompanyUser
            {
                UserId = targetUser.Id,
                CompanyId = companyId,
                Role = request.Role
            });
            await _db.SaveChangesAsync();
            // Existing platform user added directly — no email needed (they
            // already have an account + will see the company on next login).
            return new AddUserResult(WasInvited: false, Email: email, InvitationId: null, EmailSent: false);
        }

        // Invitee isn't a member of the platform yet — create an invitation
        // record + email a signup link. Owner doesn't have to wait for the
        // invitee to sign up first.
        // De-dupe: if a pending invitation already exists for this (company,
        // email), reuse it (refresh expiry + re-send email) instead of
        // creating a parallel row.
        var existing = await _db.CompanyInvitations.FirstOrDefaultAsync(i =>
            i.CompanyId == companyId && i.Email == email
            && i.Status == InvitationStatus.Pending && !i.IsDeleted);

        var inv = existing ?? new CompanyInvitation
        {
            CompanyId = companyId,
            Email = email,
            Role = request.Role,
            InvitedByUserId = ownerId,
            Token = GenerateInviteToken(),
            CreatedBy = ownerId.ToString(),
        };
        inv.Role = request.Role;       // refresh role on re-invite
        inv.Status = InvitationStatus.Pending;
        inv.ExpiresAt = DateTime.UtcNow.AddDays(7);
        inv.UpdatedAt = DateTime.UtcNow;
        inv.UpdatedBy = ownerId.ToString();
        if (existing == null) _db.CompanyInvitations.Add(inv);
        await _db.SaveChangesAsync();

        // Build the shareable accept-invite link up front so we can always
        // return it (the owner can copy/paste it even when email isn't set up).
        var baseUrl = (_config?["App:BaseUrl"] ?? "").TrimEnd('/');
        var inviteLink = $"{baseUrl}/accept-invitation.html?token={inv.Token}";

        // Email is best-effort. Distinguish THREE outcomes so the owner always
        // knows the real state instead of falsely believing a mail went out:
        //   • email configured + sent OK   → EmailSent = true
        //   • email NOT configured         → EmailSent = false + return link
        //     (was the silent-fail bug: SendAsync returned without sending and
        //      the owner was told nothing)
        //   • email configured but send threw → surface the SMTP error
        var emailSent = false;
        if (_emailService != null)
        {
            var configured = await _emailService.IsSystemEmailConfiguredAsync();
            if (configured)
            {
                try
                {
                    var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
                    var inviter = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ownerId);
                    await _emailService.SendInvitationAsync(
                        to: email,
                        inviteeName: email.Split('@')[0],
                        inviterName: inviter?.FullName ?? "Owner",
                        companyName: company?.Name ?? "บริษัท",
                        invitationToken: inv.Token,
                        role: RoleLabel(request.Role));
                    emailSent = true;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Invitation email send failed for {Email}", email);
                    throw new InvalidOperationException(
                        "สร้างคำเชิญแล้วแต่ส่งอีเมลไม่สำเร็จ — โปรดตรวจการตั้งค่า SMTP แล้วลองส่งซ้ำ " +
                        $"หรือคัดลอกลิงก์นี้ส่งให้ผู้รับเอง: {inviteLink}");
                }
            }
            else
            {
                // No system SMTP — don't pretend we emailed. Return the link so
                // the owner can share it manually.
                _logger?.LogInformation(
                    "Invitation created for {Email} but system email is not configured — returning manual link", email);
            }
        }
        return new AddUserResult(WasInvited: true, Email: email, InvitationId: inv.Id,
            EmailSent: emailSent, InviteLink: inviteLink);
    }

    private static string GenerateInviteToken()
        => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string RoleLabel(UserRole r) => r switch
    {
        UserRole.Owner => "เจ้าของ",
        UserRole.Accountant => "นักบัญชี",
        UserRole.Staff => "พนักงาน",
        UserRole.Auditor => "ผู้ตรวจสอบ",
        UserRole.Viewer => "ผู้ดู",
        UserRole.ExternalAccountant => "นักบัญชีภายนอก",
        _ => r.ToString(),
    };

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

    /// <summary>เจ้าของแก้ชื่อ-นามสกุลของสมาชิกในบริษัท (รวมของตัวเอง) — ใช้
    /// แก้ชื่อที่พิมพ์ผิดซึ่งไปโผล่บนลายเซ็น/เอกสาร (ชื่อบนเอกสาร =
    /// SignatureName ?? FullName). เปลี่ยน FullName ของ account จริง จึงจำกัด
    /// เฉพาะ Owner + log audit. target ต้องเป็นสมาชิกของบริษัทนี้.</summary>
    public async Task UpdateMemberNameAsync(Guid companyId, Guid ownerId, Guid targetUserId, string newFullName)
    {
        await EnsureOwnerAccessAsync(companyId, ownerId);

        var name = (newFullName ?? "").Trim();
        if (name.Length < 2 || name.Length > 100)
            throw new ArgumentException("ชื่อ-นามสกุลต้องมีความยาว 2-100 ตัวอักษร");

        var isMember = await _db.CompanyUsers.AnyAsync(x => x.CompanyId == companyId && x.UserId == targetUserId);
        if (!isMember) throw new KeyNotFoundException("ไม่พบผู้ใช้ในบริษัท");

        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ใช้");
        var old = target.FullName;
        target.FullName = name;
        await _db.SaveChangesAsync();
        _logger?.LogInformation("Owner {Owner} แก้ชื่อสมาชิก {Target} ในบริษัท {Company}: '{Old}' → '{New}'",
            ownerId, targetUserId, companyId, old, name);
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
            c.Address, c.BuildingNumber, c.BuildingName, c.Moo, c.StreetName,
            c.SubDistrict, c.District, c.Province,
            c.PostalCode, c.Phone, c.Fax, c.Email, c.Website,
            c.FiscalYearStartMonth, c.IsSetupComplete, sub);
    }

    private static string? ComposeAddress(Company c)
    {
        var parts = new[] { c.BuildingNumber, c.BuildingName,
            string.IsNullOrEmpty(c.Moo) ? null : "หมู่ " + c.Moo,
            string.IsNullOrEmpty(c.StreetName) ? null : "ถ." + c.StreetName,
            c.SubDistrict, c.District, c.Province, c.PostalCode }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var joined = string.Join(" ", parts);
        return string.IsNullOrEmpty(joined) ? null : joined;
    }
}
