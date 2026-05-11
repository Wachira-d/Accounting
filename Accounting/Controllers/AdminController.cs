using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Email;
using Accounting.Models.DTOs.Settings;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.DTOs.Company;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Helpers;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Email;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accounting.Controllers;

/// <summary>
/// Admin API สำหรับจัดการ Platform: Dashboard, Customers, Plans, Payments, Trials
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "SystemAdmin")]
public class AdminController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IRecurringTransactionService _recurringService;
    private readonly IEmailSenderFactory _emailSenderFactory;
    private readonly IOcrQuotaService _ocrQuota;

    public AdminController(
        AccountingDbContext db,
        ISubscriptionService subscriptionService,
        IRecurringTransactionService recurringService,
        IEmailSenderFactory emailSenderFactory,
        IOcrQuotaService ocrQuota)
    {
        _db = db;
        _subscriptionService = subscriptionService;
        _recurringService = recurringService;
        _emailSenderFactory = emailSenderFactory;
        _ocrQuota = ocrQuota;
    }

    // ===== Dashboard Analytics =====

    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResponse<object>>> GetDashboard()
    {
        var now = DateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonth = thisMonth.AddMonths(-1);

        var totalUsers = await _db.Users.CountAsync();
        var totalCompanies = await _db.Companies.CountAsync();
        var newUsersThisMonth = await _db.Users.CountAsync(u => u.CreatedAt >= thisMonth);
        var newUsersLastMonth = await _db.Users.CountAsync(u => u.CreatedAt >= lastMonth && u.CreatedAt < thisMonth);

        // Subscriptions
        var subscriptions = await _db.Subscriptions.ToListAsync();
        var activeSubs = subscriptions.Count(s => s.Status == SubscriptionStatus.Active);
        var trialSubs = subscriptions.Count(s => s.Status == SubscriptionStatus.Trial);
        var expiredSubs = subscriptions.Count(s => s.Status == SubscriptionStatus.Expired);
        var cancelledSubs = subscriptions.Count(s => s.Status == SubscriptionStatus.Cancelled);

        // MRR (Monthly Recurring Revenue) - from active subscriptions
        var mrr = subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active)
            .Sum(s => s.BillingCycle switch
            {
                BillingCycle.Monthly => s.PricePerCycle,
                BillingCycle.Quarterly => s.PricePerCycle / 3m,
                BillingCycle.SemiAnnual => s.PricePerCycle / 6m,
                BillingCycle.Annual => s.PricePerCycle / 12m,
                _ => s.PricePerCycle
            });

        // Plan distribution
        var planDistribution = subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trial)
            .GroupBy(s => s.Plan)
            .Select(g => new { plan = g.Key.ToString(), count = g.Count() })
            .ToList();

        // Payments
        var pendingPayments = await _db.SubscriptionPayments.CountAsync(p => p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview);
        var approvedThisMonth = await _db.SubscriptionPayments.CountAsync(p => p.Status == SubscriptionPaymentStatus.Approved && p.ReviewedAt >= thisMonth);
        var revenueThisMonth = await _db.SubscriptionPayments
            .Where(p => p.Status == SubscriptionPaymentStatus.Approved && p.ReviewedAt >= thisMonth)
            .SumAsync(p => p.Amount);
        var revenueLastMonth = await _db.SubscriptionPayments
            .Where(p => p.Status == SubscriptionPaymentStatus.Approved && p.ReviewedAt >= lastMonth && p.ReviewedAt < thisMonth)
            .SumAsync(p => p.Amount);

        // Recent signups
        var recentUsers = await _db.Users
            .OrderByDescending(u => u.CreatedAt)
            .Take(10)
            .Select(u => new { u.Id, u.Email, u.FullName, u.IsSystemAdmin, u.Status, u.CreatedAt, u.LastLoginAt })
            .ToListAsync();

        return Ok(new ApiResponse<object>(true, new
        {
            overview = new
            {
                totalUsers,
                totalCompanies,
                newUsersThisMonth,
                newUsersLastMonth,
                userGrowthPercent = newUsersLastMonth > 0 ? Math.Round((decimal)(newUsersThisMonth - newUsersLastMonth) / newUsersLastMonth * 100, 1) : 0
            },
            subscriptions = new
            {
                active = activeSubs,
                trial = trialSubs,
                expired = expiredSubs,
                cancelled = cancelledSubs,
                total = subscriptions.Count
            },
            revenue = new
            {
                mrr = Math.Round(mrr, 2),
                arr = Math.Round(mrr * 12, 2),
                revenueThisMonth = Math.Round(revenueThisMonth, 2),
                revenueLastMonth = Math.Round(revenueLastMonth, 2),
                revenueGrowthPercent = revenueLastMonth > 0 ? Math.Round((revenueThisMonth - revenueLastMonth) / revenueLastMonth * 100, 1) : 0
            },
            payments = new
            {
                pendingReview = pendingPayments,
                approvedThisMonth
            },
            planDistribution,
            recentUsers
        }));
    }

    // ===== Customer / Tenant Management =====

    [HttpGet("customers")]
    public async Task<ActionResult<ApiResponse<object>>> GetCustomers(
        [FromQuery] string? search,
        [FromQuery] string? plan,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var query = _db.Companies
            .Include(c => c.CompanyUsers).ThenInclude(cu => cu.User)
            .Include(c => c.Subscription)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(c => c.Name.Contains(search) || c.TaxId.Contains(search) ||
                c.CompanyUsers.Any(cu => cu.User.Email.Contains(search)));
        }

        if (Enum.TryParse<SubscriptionPlan>(plan, true, out var planEnum))
        {
            query = query.Where(c => c.Subscription != null && c.Subscription.Plan == planEnum);
        }

        if (Enum.TryParse<SubscriptionStatus>(status, true, out var statusEnum))
        {
            query = query.Where(c => c.Subscription != null && c.Subscription.Status == statusEnum);
        }

        var total = await query.CountAsync();
        var companies = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.TaxId,
                c.Status,
                c.CreatedAt,
                owner = c.CompanyUsers
                    .Where(cu => cu.Role == UserRole.Owner)
                    .Select(cu => new { cu.User.Email, cu.User.FullName })
                    .FirstOrDefault(),
                userCount = c.CompanyUsers.Count,
                subscription = c.Subscription == null ? null : new
                {
                    c.Subscription.Plan,
                    c.Subscription.Status,
                    c.Subscription.BillingCycle,
                    c.Subscription.PricePerCycle,
                    c.Subscription.StartDate,
                    c.Subscription.EndDate
                }
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>(true, new
        {
            items = companies,
            total,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling(total / (double)pageSize)
        }));
    }

    [HttpGet("customers/{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> GetCustomerDetail(Guid companyId)
    {
        var company = await _db.Companies
            .Include(c => c.CompanyUsers).ThenInclude(cu => cu.User)
            .Include(c => c.Subscription)
            .FirstOrDefaultAsync(c => c.Id == companyId);

        if (company == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบบริษัท"));

        var payments = await _db.SubscriptionPayments
            .Where(p => p.SubscriptionId == (company.Subscription != null ? company.Subscription.Id : Guid.Empty))
            .OrderByDescending(p => p.CreatedAt)
            .Take(20)
            .ToListAsync();

        var trial = company.Subscription != null
            ? await _db.TrialConfigs.FirstOrDefaultAsync(t => t.SubscriptionId == company.Subscription.Id)
            : null;

        var docCount = await _db.Documents.CountAsync(d => d.CompanyId == companyId);
        var journalCount = await _db.JournalEntries.CountAsync(j => j.CompanyId == companyId);

        return Ok(new ApiResponse<object>(true, new
        {
            company = new
            {
                company.Id, company.Name, company.NameEn, company.TaxId, company.BranchCode,
                company.BusinessType, company.Status, company.Address, company.Province,
                company.Phone, company.Email, company.BaseCurrency, company.CreatedAt
            },
            users = company.CompanyUsers.Select(cu => new
            {
                cu.User.Id, cu.User.Email, cu.User.FullName, cu.Role,
                cu.User.Status, cu.User.LastLoginAt, cu.JoinedAt
            }),
            subscription = company.Subscription == null ? null : new
            {
                company.Subscription.Id, company.Subscription.Plan, company.Subscription.Status,
                company.Subscription.BillingCycle, company.Subscription.PricePerCycle,
                company.Subscription.StartDate, company.Subscription.EndDate,
                company.Subscription.EnabledFeatures
            },
            trial = trial == null ? null : new
            {
                trial.TrialStartDate, trial.TrialEndDate, trial.ExtensionsUsed,
                trial.MaxExtensions, trial.IsTrialExpired, trial.GracePeriodEndDate
            },
            usage = new { documents = docCount, journalEntries = journalCount },
            recentPayments = payments.Select(p => new
            {
                p.Id, p.PaymentNumber, p.Amount, p.PaymentMethod,
                p.Status, p.CreatedAt, p.ReviewedAt
            })
        }));
    }

    [HttpPut("customers/{companyId:guid}/status")]
    public async Task<ActionResult<ApiResponse<string>>> UpdateCustomerStatus(
        Guid companyId, [FromBody] UpdateCustomerStatusRequest request)
    {
        var company = await _db.Companies.FindAsync(companyId);
        if (company == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบบริษัท"));

        company.Status = request.Status;
        company.UpdatedAt = DateTime.UtcNow;
        company.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<string>(true, null, $"อัพเดทสถานะบริษัทเป็น {request.Status} สำเร็จ"));
    }

    // ===== User Management =====

    [HttpGet("users")]
    public async Task<ActionResult<ApiResponse<object>>> GetUsers(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var query = _db.Users.Include(u => u.CompanyUsers).ThenInclude(cu => cu.Company).AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(u => u.Email.Contains(search) || u.FullName.Contains(search));
        }

        var total = await query.CountAsync();
        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                u.Id, u.Email, u.FullName, u.Phone, u.Status, u.IsSystemAdmin,
                u.LastLoginAt, u.CreatedAt,
                companies = u.CompanyUsers.Select(cu => new { cu.Company.Id, cu.Company.Name, cu.Role })
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>(true, new
        {
            items = users, total, page, pageSize,
            totalPages = (int)Math.Ceiling(total / (double)pageSize)
        }));
    }

    [HttpPut("users/{userId:guid}/status")]
    public async Task<ActionResult<ApiResponse<string>>> UpdateUserStatus(
        Guid userId, [FromBody] UpdateUserStatusRequest request)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบผู้ใช้"));

        user.Status = request.Status;
        user.UpdatedAt = DateTime.UtcNow;
        user.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<string>(true, null, $"อัพเดทสถานะผู้ใช้เป็น {request.Status} สำเร็จ"));
    }

    [HttpPut("users/{userId:guid}/admin")]
    public async Task<ActionResult<ApiResponse<string>>> ToggleAdminRole(
        Guid userId, [FromBody] ToggleAdminRequest request)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบผู้ใช้"));

        user.IsSystemAdmin = request.IsAdmin;
        user.UpdatedAt = DateTime.UtcNow;
        user.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<string>(true, null, request.IsAdmin ? "กำหนดเป็น Admin สำเร็จ" : "ยกเลิกสิทธิ์ Admin สำเร็จ"));
    }

    [HttpPut("companies/{companyId:guid}/users/{userId:guid}/role")]
    public async Task<ActionResult<ApiResponse<string>>> ChangeCompanyUserRole(
        Guid companyId, Guid userId, [FromBody] UpdateUserRoleRequest request)
    {
        var cu = await _db.CompanyUsers
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == userId);
        if (cu == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบสมาชิกในบริษัทนี้"));

        cu.Role = request.Role;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<string>(true, null, $"เปลี่ยน Role เป็น {request.Role} สำเร็จ"));
    }

    // ===== Plan Template Management =====

    [HttpGet("features")]
    public ActionResult<ApiResponse<List<FeatureFlagInfo>>> GetAllFeatures()
    {
        var result = FeatureFlagsHelper.AllFeatures();
        return Ok(new ApiResponse<List<FeatureFlagInfo>>(true, result));
    }

    [HttpPost("plans")]
    public async Task<ActionResult<ApiResponse<PlanTemplateResponse>>> CreatePlanTemplate([FromBody] CreatePlanTemplateRequest request)
    {
        var result = await _subscriptionService.CreatePlanTemplateAsync(request);
        return StatusCode(201, new ApiResponse<PlanTemplateResponse>(true, result, "สร้าง plan template สำเร็จ"));
    }

    [HttpGet("plans")]
    public async Task<ActionResult<ApiResponse<List<PlanTemplateResponse>>>> GetPlanTemplates([FromQuery] bool includeInactive = false)
    {
        var result = await _subscriptionService.GetPlanTemplatesAsync(includeInactive);
        return Ok(new ApiResponse<List<PlanTemplateResponse>>(true, result));
    }

    [HttpPut("plans/{templateId:guid}")]
    public async Task<ActionResult<ApiResponse<PlanTemplateResponse>>> UpdatePlanTemplate(Guid templateId, [FromBody] UpdatePlanTemplateRequest request)
    {
        var result = await _subscriptionService.UpdatePlanTemplateAsync(templateId, request);
        return Ok(new ApiResponse<PlanTemplateResponse>(true, result, "อัพเดท plan template สำเร็จ"));
    }

    // ===== Trial Config Management =====

    [HttpGet("companies/{companyId:guid}/trial")]
    public async Task<ActionResult<ApiResponse<TrialStatusResponse>>> GetTrialStatus(Guid companyId)
    {
        var result = await _subscriptionService.GetTrialStatusAsync(companyId);
        return Ok(new ApiResponse<TrialStatusResponse>(true, result));
    }

    [HttpPut("companies/{companyId:guid}/trial")]
    public async Task<ActionResult<ApiResponse<TrialStatusResponse>>> UpdateTrialConfig(Guid companyId, [FromBody] UpdateTrialConfigRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.UpdateTrialConfigAsync(companyId, request, userId);
        return Ok(new ApiResponse<TrialStatusResponse>(true, result, "อัพเดท trial config สำเร็จ"));
    }

    [HttpPost("companies/{companyId:guid}/trial/extend")]
    public async Task<ActionResult<ApiResponse<TrialStatusResponse>>> ExtendTrial(Guid companyId, [FromBody] ExtendTrialRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.ExtendTrialAsync(companyId, request, userId);
        return Ok(new ApiResponse<TrialStatusResponse>(true, result, "ขยายเวลา trial สำเร็จ"));
    }

    [HttpPost("companies/{companyId:guid}/trial/expire")]
    public async Task<ActionResult<ApiResponse<string>>> ExpireTrial(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _subscriptionService.ExpireTrialAsync(companyId, userId);
        return Ok(new ApiResponse<string>(true, null, "Expire trial สำเร็จ"));
    }

    [HttpPost("trial/process-expired")]
    public async Task<ActionResult<ApiResponse<string>>> ProcessExpiredTrials()
    {
        await _subscriptionService.ProcessExpiredTrialsAsync();
        return Ok(new ApiResponse<string>(true, null, "ประมวลผล expired trials สำเร็จ"));
    }

    // ===== Admin: Direct Subscription Management =====

    /// <summary>
    /// Admin: เปลี่ยนแพ็กเกจ (ไม่จำเป็นต้อง Active ก็เปลี่ยนได้)
    /// </summary>
    [HttpPut("companies/{companyId:guid}/subscription/plan")]
    public async Task<ActionResult<ApiResponse<object>>> AdminChangePlan(
        Guid companyId, [FromBody] AdminChangePlanRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null)
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบ subscription"));

        var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == request.Plan && p.IsActive);

        var oldPlan = sub.Plan;
        sub.Plan = request.Plan;

        if (request.BillingCycle.HasValue)
            sub.BillingCycle = request.BillingCycle.Value;

        // Use template pricing if available, or allow explicit price
        if (request.PricePerCycle.HasValue)
        {
            sub.PricePerCycle = request.PricePerCycle.Value;
        }
        else if (template != null)
        {
            sub.PricePerCycle = sub.BillingCycle switch
            {
                BillingCycle.Monthly => template.MonthlyPrice,
                BillingCycle.Quarterly => template.QuarterlyPrice,
                BillingCycle.SemiAnnual => template.SemiAnnualPrice,
                BillingCycle.Annual => template.AnnualPrice,
                _ => template.MonthlyPrice
            };
        }

        // Update limits from template if not overridden
        if (template != null && !request.KeepCurrentLimits)
        {
            sub.EnabledFeatures = template.EnabledFeatures;
            sub.MaxUsers = template.MaxUsers;
            sub.MaxCompanies = template.MaxCompanies;
            sub.MaxDocumentsPerMonth = template.MaxDocumentsPerMonth;
            sub.MaxJournalEntriesPerMonth = template.MaxJournalEntriesPerMonth;
            sub.MaxStorageBytes = template.MaxStorageBytes;
        }

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "AdminPlanChanged",
            FromPlan = oldPlan,
            ToPlan = request.Plan,
            Notes = $"Admin changed plan from {oldPlan} to {request.Plan}" + (request.Notes != null ? $": {request.Notes}" : ""),
            PerformedBy = userId
        });

        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { sub.Plan, sub.BillingCycle, sub.PricePerCycle }, "เปลี่ยนแพ็กเกจสำเร็จ"));
    }

    /// <summary>
    /// Admin: เปลี่ยนสถานะ subscription (Active, Trial, Expired, Cancelled, Suspended)
    /// </summary>
    [HttpPut("companies/{companyId:guid}/subscription/status")]
    public async Task<ActionResult<ApiResponse<object>>> AdminChangeSubscriptionStatus(
        Guid companyId, [FromBody] AdminChangeSubStatusRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null)
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบ subscription"));

        var oldStatus = sub.Status;
        sub.Status = request.Status;

        if (request.Status == SubscriptionStatus.Cancelled)
            sub.CancelledAt = DateTime.UtcNow;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "AdminStatusChanged",
            FromStatus = oldStatus,
            ToStatus = request.Status,
            Notes = $"Admin changed status from {oldStatus} to {request.Status}" + (request.Notes != null ? $": {request.Notes}" : ""),
            PerformedBy = userId
        });

        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { sub.Status }, "เปลี่ยนสถานะ subscription สำเร็จ"));
    }

    /// <summary>
    /// Admin: ปรับวันหมดอายุ / ต่ออายุ
    /// </summary>
    [HttpPut("companies/{companyId:guid}/subscription/dates")]
    public async Task<ActionResult<ApiResponse<object>>> AdminChangeDates(
        Guid companyId, [FromBody] AdminChangeDatesRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null)
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบ subscription"));

        var oldEnd = sub.EndDate;

        if (request.StartDate.HasValue)
            sub.StartDate = request.StartDate.Value;
        if (request.EndDate.HasValue)
            sub.EndDate = request.EndDate.Value;
        if (request.NextBillingDate.HasValue)
            sub.NextBillingDate = request.NextBillingDate.Value;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "AdminDatesChanged",
            Notes = $"Admin changed end date from {oldEnd:yyyy-MM-dd} to {sub.EndDate:yyyy-MM-dd}" + (request.Notes != null ? $": {request.Notes}" : ""),
            PerformedBy = userId
        });

        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { sub.StartDate, sub.EndDate, sub.NextBillingDate }, "ปรับวันที่สำเร็จ"));
    }

    /// <summary>
    /// Admin: ปรับ Usage Limits (จำนวนผู้ใช้, เอกสาร, storage ฯลฯ)
    /// </summary>
    [HttpPut("companies/{companyId:guid}/subscription/limits")]
    public async Task<ActionResult<ApiResponse<object>>> AdminChangeLimits(
        Guid companyId, [FromBody] AdminChangeLimitsRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null)
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบ subscription"));

        if (request.MaxUsers.HasValue) sub.MaxUsers = request.MaxUsers.Value;
        if (request.MaxDocumentsPerMonth.HasValue) sub.MaxDocumentsPerMonth = request.MaxDocumentsPerMonth.Value;
        if (request.MaxJournalEntriesPerMonth.HasValue) sub.MaxJournalEntriesPerMonth = request.MaxJournalEntriesPerMonth.Value;
        if (request.MaxStorageBytes.HasValue) sub.MaxStorageBytes = request.MaxStorageBytes.Value;
        if (request.MaxCompanies.HasValue) sub.MaxCompanies = request.MaxCompanies.Value;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "AdminLimitsChanged",
            Notes = $"Admin adjusted usage limits" + (request.Notes != null ? $": {request.Notes}" : ""),
            PerformedBy = userId
        });

        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new {
            sub.MaxUsers, sub.MaxDocumentsPerMonth, sub.MaxJournalEntriesPerMonth,
            sub.MaxStorageBytes, sub.MaxCompanies
        }, "ปรับ limits สำเร็จ"));
    }

    /// <summary>
    /// Admin: ดูประวัติการเปลี่ยนแปลง subscription
    /// </summary>
    [HttpGet("companies/{companyId:guid}/subscription/history")]
    public async Task<ActionResult<ApiResponse<object>>> GetSubscriptionHistory(Guid companyId)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null)
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบ subscription"));

        var history = await _db.SubscriptionHistories
            .Where(h => h.SubscriptionId == sub.Id)
            .OrderByDescending(h => h.CreatedAt)
            .Take(50)
            .Select(h => new
            {
                h.Id, h.Action, h.Notes,
                fromPlan = h.FromPlan.HasValue ? h.FromPlan.Value.ToString() : null,
                toPlan = h.ToPlan.HasValue ? h.ToPlan.Value.ToString() : null,
                fromStatus = h.FromStatus.HasValue ? h.FromStatus.Value.ToString() : null,
                toStatus = h.ToStatus.HasValue ? h.ToStatus.Value.ToString() : null,
                h.PerformedBy, h.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>(true, history));
    }

    // ===== Subscription Payment Review =====

    [HttpGet("subscription-payments/pending")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentListResponse>>> GetPendingPayments()
    {
        var result = await _subscriptionService.GetAllPendingPaymentsAsync();
        return Ok(new ApiResponse<SubscriptionPaymentListResponse>(true, result));
    }

    [HttpGet("subscription-payments/all")]
    public async Task<ActionResult<ApiResponse<object>>> GetAllPayments(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var query = _db.SubscriptionPayments
            .Include(p => p.Subscription).ThenInclude(s => s!.Company)
            .AsQueryable();

        if (Enum.TryParse<SubscriptionPaymentStatus>(status, true, out var statusEnum))
        {
            query = query.Where(p => p.Status == statusEnum);
        }

        var total = await query.CountAsync();
        var payments = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id, p.PaymentNumber, p.Amount, p.PaymentMethod, p.Status,
                p.RequestedPlan, p.CreatedAt, p.ReviewedAt,
                p.ReviewNotes, p.SubscriptionExtendedTo,
                company = p.Subscription != null ? new { p.Subscription.Company.Id, p.Subscription.Company.Name } : null
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>(true, new
        {
            items = payments, total, page, pageSize,
            totalPages = (int)Math.Ceiling(total / (double)pageSize)
        }));
    }

    [HttpGet("subscription-payments/{paymentId:guid}")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentResponse>>> GetPaymentDetail(Guid paymentId)
    {
        var result = await _subscriptionService.GetPaymentAsync(paymentId);
        return Ok(new ApiResponse<SubscriptionPaymentResponse>(true, result));
    }

    [HttpPost("subscription-payments/{paymentId:guid}/review")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentResponse>>> ReviewPayment(
        Guid paymentId, [FromBody] ReviewSubscriptionPaymentRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.ReviewPaymentAsync(paymentId, request, userId);
        var message = request.Approve ? "อนุมัติการชำระเงินสำเร็จ ต่ออายุ Subscription แล้ว" : "ปฏิเสธการชำระเงินแล้ว";
        return Ok(new ApiResponse<SubscriptionPaymentResponse>(true, result, message));
    }

    [HttpGet("companies/{companyId:guid}/subscription-payments")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentListResponse>>> GetCompanyPayments(Guid companyId)
    {
        var result = await _subscriptionService.GetPaymentsAsync(companyId);
        return Ok(new ApiResponse<SubscriptionPaymentListResponse>(true, result));
    }

    // ===== Subscription Notification Settings (Admin) =====

    [HttpGet("companies/{companyId:guid}/subscription-notifications")]
    public async Task<ActionResult<ApiResponse<SubscriptionNotificationSettingsResponse>>> GetNotificationSettings(Guid companyId)
    {
        var result = await _subscriptionService.GetNotificationSettingsAsync(companyId);
        return Ok(new ApiResponse<SubscriptionNotificationSettingsResponse>(true, result));
    }

    [HttpPut("companies/{companyId:guid}/subscription-notifications")]
    public async Task<ActionResult<ApiResponse<SubscriptionNotificationSettingsResponse>>> UpdateNotificationSettings(
        Guid companyId, [FromBody] UpdateSubscriptionNotificationRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.UpdateNotificationSettingsAsync(companyId, request, userId);
        return Ok(new ApiResponse<SubscriptionNotificationSettingsResponse>(true, result, "อัพเดทการตั้งค่าแจ้งเตือนสำเร็จ"));
    }

    // ===== Background Processing (Scheduler) =====

    [HttpPost("subscription/process-notifications")]
    public async Task<ActionResult<ApiResponse<string>>> ProcessSubscriptionNotifications()
    {
        await _subscriptionService.ProcessSubscriptionNotificationsAsync();
        return Ok(new ApiResponse<string>(true, null, "ประมวลผลแจ้งเตือน subscription สำเร็จ"));
    }

    [HttpPost("subscription/process-expired")]
    public async Task<ActionResult<ApiResponse<string>>> ProcessExpiredSubscriptions()
    {
        await _subscriptionService.ProcessExpiredSubscriptionsAsync();
        return Ok(new ApiResponse<string>(true, null, "ประมวลผล expired subscriptions สำเร็จ"));
    }

    [HttpPost("recurring/process")]
    public async Task<ActionResult<ApiResponse<string>>> ProcessRecurringTransactions()
    {
        await _recurringService.ProcessDueRecurringTransactionsAsync();
        return Ok(new ApiResponse<string>(true, null, "ประมวลผลรายการที่เกิดซ้ำสำเร็จ"));
    }

    // ===== Site Settings (Global) =====

    [HttpGet("site-settings")]
    public async Task<ActionResult<ApiResponse<SiteSettingsResponse>>> GetSiteSettings()
    {
        var settings = await _db.SiteSettings.FirstOrDefaultAsync();
        if (settings == null)
            return Ok(new ApiResponse<SiteSettingsResponse>(true, new SiteSettingsResponse(
                null, null, null, null, null, null, null, null, null, null,
                new List<LandingServiceItem>(), null, null, null, null, null,
                null, null, null, null, null, true, false, null, "th")));

        var services = DeserializeServices(settings.ServicesJson);
        return Ok(new ApiResponse<SiteSettingsResponse>(true, new SiteSettingsResponse(
            settings.Id, settings.SiteName, settings.SiteDescription, settings.SiteLogoUrl,
            settings.FaviconUrl, settings.LoginBackgroundUrl, settings.PrimaryColor,
            settings.HeroTitle, settings.HeroSubtitle, settings.FooterCopyright,
            services, settings.ContactPhone, settings.ContactLine, settings.ContactEmail,
            settings.PricingSectionTitle, settings.PricingSectionSubtitle,
            settings.FacebookUrl, settings.LineOfficialUrl, settings.WebsiteUrl,
            settings.YouTubeUrl, settings.InstagramUrl,
            settings.RegistrationEnabled, settings.MaintenanceMode,
            settings.MaintenanceMessage, settings.DefaultLanguage)));
    }

    [HttpPut("site-settings")]
    public async Task<ActionResult<ApiResponse<SiteSettingsResponse>>> UpdateSiteSettings([FromBody] UpdateSiteSettingsRequest request)
    {
        var settings = await _db.SiteSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new SiteSettings();
            _db.SiteSettings.Add(settings);
        }

        settings.SiteName = request.SiteName;
        settings.SiteDescription = request.SiteDescription;
        settings.SiteLogoUrl = request.SiteLogoUrl;
        settings.FaviconUrl = request.FaviconUrl;
        settings.LoginBackgroundUrl = request.LoginBackgroundUrl;
        settings.PrimaryColor = request.PrimaryColor;
        settings.HeroTitle = request.HeroTitle;
        settings.HeroSubtitle = request.HeroSubtitle;
        settings.FooterCopyright = request.FooterCopyright;
        settings.ContactPhone = request.ContactPhone;
        settings.ContactLine = request.ContactLine;
        settings.ContactEmail = request.ContactEmail;
        settings.PricingSectionTitle = request.PricingSectionTitle;
        settings.PricingSectionSubtitle = request.PricingSectionSubtitle;
        settings.FacebookUrl = request.FacebookUrl;
        settings.LineOfficialUrl = request.LineOfficialUrl;
        settings.WebsiteUrl = request.WebsiteUrl;
        settings.YouTubeUrl = request.YouTubeUrl;
        settings.InstagramUrl = request.InstagramUrl;

        if (request.RegistrationEnabled.HasValue)
            settings.RegistrationEnabled = request.RegistrationEnabled.Value;
        if (request.MaintenanceMode.HasValue)
            settings.MaintenanceMode = request.MaintenanceMode.Value;
        settings.MaintenanceMessage = request.MaintenanceMessage;
        if (request.DefaultLanguage != null)
            settings.DefaultLanguage = request.DefaultLanguage;

        if (request.Services != null)
        {
            settings.ServicesJson = JsonSerializer.Serialize(request.Services,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }

        settings.UpdatedAt = DateTime.UtcNow;
        settings.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _db.SaveChangesAsync();

        var services = DeserializeServices(settings.ServicesJson);
        return Ok(new ApiResponse<SiteSettingsResponse>(true, new SiteSettingsResponse(
            settings.Id, settings.SiteName, settings.SiteDescription, settings.SiteLogoUrl,
            settings.FaviconUrl, settings.LoginBackgroundUrl, settings.PrimaryColor,
            settings.HeroTitle, settings.HeroSubtitle, settings.FooterCopyright,
            services, settings.ContactPhone, settings.ContactLine, settings.ContactEmail,
            settings.PricingSectionTitle, settings.PricingSectionSubtitle,
            settings.FacebookUrl, settings.LineOfficialUrl, settings.WebsiteUrl,
            settings.YouTubeUrl, settings.InstagramUrl,
            settings.RegistrationEnabled, settings.MaintenanceMode,
            settings.MaintenanceMessage, settings.DefaultLanguage),
            "บันทึกการตั้งค่าสำเร็จ"));
    }

    [HttpPost("upload-logo")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<object>>> UploadLogo(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "กรุณาเลือกไฟล์"));

        var allowedTypes = new[] { "image/png", "image/jpeg", "image/svg+xml", "image/webp", "image/gif" };
        if (!allowedTypes.Contains(file.ContentType))
            return BadRequest(new ApiResponse<object>(false, null, "รองรับเฉพาะไฟล์ PNG, JPG, SVG, WebP, GIF"));

        var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
        Directory.CreateDirectory(uploadsDir);

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName = $"logo_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
        var filePath = Path.Combine(uploadsDir, fileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var url = $"/uploads/{fileName}";
        return Ok(new ApiResponse<object>(true, new { url }, "อัพโหลดโลโก้สำเร็จ"));
    }

    [HttpPost("upload-image")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<object>>> UploadImage(IFormFile file, [FromQuery] string type = "general")
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "กรุณาเลือกไฟล์"));

        var allowedTypes = new[] { "image/png", "image/jpeg", "image/svg+xml", "image/webp", "image/gif", "image/x-icon" };
        if (!allowedTypes.Contains(file.ContentType))
            return BadRequest(new ApiResponse<object>(false, null, "รองรับเฉพาะไฟล์รูปภาพ"));

        var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
        Directory.CreateDirectory(uploadsDir);

        var allowedImageTypes = new[] { "general", "logo", "icon", "banner", "favicon" };
        var safeType = allowedImageTypes.Contains(type) ? type : "general";

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName = $"{safeType}_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
        var filePath = Path.Combine(uploadsDir, fileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var url = $"/uploads/{fileName}";
        return Ok(new ApiResponse<object>(true, new { url }, "อัพโหลดสำเร็จ"));
    }

    // Public endpoint for landing page
    [HttpGet("/api/site/landing")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<LandingPageResponse>>> GetLandingPage()
    {
        var settings = await _db.SiteSettings.FirstOrDefaultAsync();

        var services = DeserializeServices(settings?.ServicesJson);

        // Also get active plans for pricing section
        var plans = await _db.PlanTemplates
            .Where(p => p.IsActive)
            .OrderBy(p => p.MonthlyPrice)
            .ToListAsync();

        return Ok(new ApiResponse<LandingPageResponse>(true, new LandingPageResponse(
            settings?.SiteName, settings?.SiteDescription, settings?.SiteLogoUrl,
            settings?.FaviconUrl, settings?.PrimaryColor,
            settings?.HeroTitle, settings?.HeroSubtitle, settings?.FooterCopyright,
            settings?.ContactPhone, settings?.ContactLine, settings?.ContactEmail,
            services,
            settings?.PricingSectionTitle, settings?.PricingSectionSubtitle,
            settings?.FacebookUrl, settings?.LineOfficialUrl, settings?.WebsiteUrl,
            settings?.YouTubeUrl, settings?.InstagramUrl,
            settings?.RegistrationEnabled ?? true,
            settings?.DefaultLanguage ?? "th")));
    }

    private static List<LandingServiceItem> DeserializeServices(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new List<LandingServiceItem>();
        try
        {
            return JsonSerializer.Deserialize<List<LandingServiceItem>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Failed to deserialize landing services JSON: {ex.Message}"); return new(); }
    }

    // ===== Integration Overview (Admin) =====

    [HttpGet("integrations")]
    public async Task<ActionResult<ApiResponse<object>>> GetAllIntegrations()
    {
        var integrations = await _db.ExternalIntegrations
            .Where(i => !i.IsDeleted)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new
            {
                i.Id, i.SystemName, i.SystemType, i.SystemVersion, i.BaseUrl,
                i.ApiKeyPrefix, i.IsActive, i.LastSyncAt,
                i.TotalSyncCount, i.ErrorCount, i.ConsecutiveErrors,
                i.RateLimitPerMinute, i.CreatedAt,
                CompanyId = i.CompanyId,
                CompanyName = _db.Companies.Where(c => c.Id == i.CompanyId).Select(c => c.Name).FirstOrDefault()
            })
            .ToListAsync();

        var totalSyncsToday = await _db.IntegrationSyncLogs
            .Where(l => l.CreatedAt >= DateTime.UtcNow.Date)
            .CountAsync();

        var errorsToday = await _db.IntegrationSyncLogs
            .Where(l => l.CreatedAt >= DateTime.UtcNow.Date && l.Status == "Failed")
            .CountAsync();

        var recentLogs = await _db.IntegrationSyncLogs
            .Include(l => l.Integration)
            .OrderByDescending(l => l.CreatedAt)
            .Take(30)
            .Select(l => new
            {
                l.Id, l.EventType, l.ExternalRef, l.Status, l.ErrorMessage,
                l.ProcessingTimeMs, l.CreatedAt,
                SystemName = l.Integration.SystemName,
                CompanyId = l.CompanyId
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>(true, new
        {
            TotalIntegrations = integrations.Count,
            ActiveIntegrations = integrations.Count(i => i.IsActive),
            TotalSyncsToday = totalSyncsToday,
            ErrorsToday = errorsToday,
            Integrations = integrations,
            RecentLogs = recentLogs
        }));
    }

    // ===== System Email (System-wide SMTP/API config managed by admin) =====
    // ใช้สำหรับส่งคำเชิญ, รีเซ็ตรหัสผ่าน, และ system notifications

    [HttpGet("system-email")]
    public async Task<ActionResult<ApiResponse<SystemEmailConfigResponse>>> GetSystemEmail()
    {
        var s = await GetOrCreateSiteSettings();
        return Ok(new ApiResponse<SystemEmailConfigResponse>(true, BuildSystemEmailResponse(s)));
    }

    [HttpPut("system-email")]
    public async Task<ActionResult<ApiResponse<SystemEmailConfigResponse>>> UpdateSystemEmail(
        [FromBody] UpdateSystemEmailConfigRequest req)
    {
        var s = await GetOrCreateSiteSettings();
        s.SystemEmailProvider = req.Provider;
        if (req.FromAddress != null) s.SystemEmailFromAddress = req.FromAddress;
        if (req.FromName != null) s.SystemEmailFromName = req.FromName;
        if (req.ReplyTo != null) s.SystemEmailReplyTo = req.ReplyTo;
        if (req.AppBaseUrl != null) s.AppBaseUrl = req.AppBaseUrl;

        if (req.Smtp != null)
        {
            if (req.Smtp.Host != null) s.SystemSmtpHost = req.Smtp.Host;
            if (req.Smtp.Port.HasValue) s.SystemSmtpPort = req.Smtp.Port.Value;
            if (req.Smtp.Username != null) s.SystemSmtpUsername = req.Smtp.Username;
            if (!string.IsNullOrEmpty(req.Smtp.Password)) s.SystemSmtpPassword = req.Smtp.Password;
            if (req.Smtp.UseSsl.HasValue) s.SystemSmtpUseSsl = req.Smtp.UseSsl.Value;
        }
        if (req.Microsoft != null)
        {
            if (req.Microsoft.TenantId != null) s.SystemMsTenantId = req.Microsoft.TenantId;
            if (req.Microsoft.ClientId != null) s.SystemMsClientId = req.Microsoft.ClientId;
            if (!string.IsNullOrEmpty(req.Microsoft.ClientSecret)) s.SystemMsClientSecret = req.Microsoft.ClientSecret;
            if (req.Microsoft.SenderUpn != null) s.SystemMsSenderUpn = req.Microsoft.SenderUpn;
        }
        if (req.Gmail != null)
        {
            if (req.Gmail.ClientId != null) s.SystemGmailClientId = req.Gmail.ClientId;
            if (!string.IsNullOrEmpty(req.Gmail.ClientSecret)) s.SystemGmailClientSecret = req.Gmail.ClientSecret;
            if (!string.IsNullOrEmpty(req.Gmail.RefreshToken)) s.SystemGmailRefreshToken = req.Gmail.RefreshToken;
        }

        s.SystemEmailConfigured = false;  // must re-test after change
        s.UpdatedAt = DateTime.UtcNow;
        s.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<SystemEmailConfigResponse>(true, BuildSystemEmailResponse(s),
            "บันทึกการตั้งค่าอีเมลระบบเรียบร้อย กรุณากด \"ทดสอบส่งอีเมล\" เพื่อยืนยัน"));
    }

    [HttpPost("system-email/test")]
    public async Task<ActionResult<ApiResponse<EmailTestResult>>> TestSystemEmail([FromBody] TestEmailRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ToAddress))
            return BadRequest(new ApiResponse<EmailTestResult>(false, null, "กรุณาระบุอีเมลผู้รับ"));

        var s = await GetOrCreateSiteSettings();
        var sender = _emailSenderFactory.GetGlobalFallbackSender();

        var msg = new EmailMessage
        {
            FromAddress = s.SystemEmailFromAddress ?? "noreply@nextacc.com",
            FromName = s.SystemEmailFromName ?? s.SiteName ?? "Next Acc",
            Subject = "[ทดสอบ] การตั้งค่าอีเมลระบบ Next Acc",
            HtmlBody = $@"<div style='font-family:sans-serif;max-width:600px;margin:0 auto'>
                <h2 style='color:#10b981'>ตั้งค่าอีเมลระบบสำเร็จ</h2>
                <p>หากคุณได้รับอีเมลฉบับนี้ แสดงว่าการตั้งค่าผ่าน <strong>{s.SystemEmailProvider}</strong> ใช้งานได้</p>
                <p>อีเมลระบบนี้จะใช้สำหรับ:</p>
                <ul>
                    <li>ส่งคำเชิญถึงผู้ใช้ที่ยังไม่ได้สมัครสมาชิก</li>
                    <li>รีเซ็ตรหัสผ่าน</li>
                    <li>การแจ้งเตือนของระบบ</li>
                </ul>
                <p style='color:#64748b;font-size:13px'>เวลาทดสอบ: {DateTime.UtcNow.AddHours(7):yyyy-MM-dd HH:mm:ss} (UTC+7)</p>
            </div>"
        };
        msg.To.Add(req.ToAddress);

        var result = await sender.SendAsync(msg);

        s.SystemEmailLastTestedAt = DateTime.UtcNow;
        s.SystemEmailLastTestStatus = result.Success ? "OK" : (result.ErrorMessage ?? "Failed");
        s.SystemEmailConfigured = result.Success;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<EmailTestResult>(result.Success,
            new EmailTestResult(result.Success, result.ErrorMessage, s.SystemEmailLastTestedAt.Value),
            result.Success ? "ส่งอีเมลทดสอบสำเร็จ" : (result.ErrorMessage ?? "ส่งทดสอบไม่สำเร็จ")));
    }

    private async Task<SiteSettings> GetOrCreateSiteSettings()
    {
        var s = await _db.SiteSettings.FirstOrDefaultAsync();
        if (s == null)
        {
            s = new SiteSettings();
            _db.SiteSettings.Add(s);
            await _db.SaveChangesAsync();
        }
        return s;
    }

    private static SystemEmailConfigResponse BuildSystemEmailResponse(SiteSettings s) => new(
        Provider: s.SystemEmailProvider,
        FromAddress: s.SystemEmailFromAddress,
        FromName: s.SystemEmailFromName,
        ReplyTo: s.SystemEmailReplyTo,
        AppBaseUrl: s.AppBaseUrl,
        Configured: s.SystemEmailConfigured,
        LastTestedAt: s.SystemEmailLastTestedAt,
        LastTestStatus: s.SystemEmailLastTestStatus,
        Smtp: new SmtpConfigDto(s.SystemSmtpHost, s.SystemSmtpPort, s.SystemSmtpUsername,
            !string.IsNullOrEmpty(s.SystemSmtpPassword), s.SystemSmtpUseSsl),
        Microsoft: new MicrosoftGraphConfigDto(s.SystemMsTenantId, s.SystemMsClientId,
            !string.IsNullOrEmpty(s.SystemMsClientSecret), s.SystemMsSenderUpn),
        Gmail: new GmailConfigDto(s.SystemGmailClientId,
            !string.IsNullOrEmpty(s.SystemGmailClientSecret),
            !string.IsNullOrEmpty(s.SystemGmailRefreshToken)));

    // ===== Azure Document Intelligence Config =====

    [HttpGet("ocr-config")]
    public async Task<ActionResult<ApiResponse<OcrConfigResponse>>> GetOcrConfig()
    {
        var s = await GetOrCreateSiteSettings();
        return Ok(new ApiResponse<OcrConfigResponse>(true, new OcrConfigResponse(
            AzureDiEndpoint: s.AzureDiEndpoint,
            AzureDiHasKey: !string.IsNullOrEmpty(s.AzureDiApiKey),
            AzureDiModelId: s.AzureDiModelId,
            AzureDiApiVersion: s.AzureDiApiVersion,
            AzureDiEnabled: s.AzureDiEnabled,
            AzureDiLastTestedAt: s.AzureDiLastTestedAt,
            AzureDiLastTestStatus: s.AzureDiLastTestStatus,
            OcrProvider: s.OcrProvider,
            OcrLocalServiceUrl: s.OcrLocalServiceUrl,
            OcrAutoCreateThreshold: s.OcrAutoCreateThreshold,
            OcrFreePagesTrial: s.OcrFreePagesTrial,
            OcrFreePagesBasic: s.OcrFreePagesBasic,
            OcrFreePagesPro: s.OcrFreePagesPro,
            OcrFreePagesEnterprise: s.OcrFreePagesEnterprise,
            OcrCreditPricePerPage: s.OcrCreditPricePerPage,
            OcrCreditMinPurchase: s.OcrCreditMinPurchase,
            OcrGatewayMaxPenalty: s.OcrGatewayMaxPenalty,
            OcrGatewayMathTolerance: s.OcrGatewayMathTolerance,
            OcrGatewayTaxIdPenalty: s.OcrGatewayTaxIdPenalty,
            OcrGatewayMathPenalty: s.OcrGatewayMathPenalty,
            OcrGatewayDatePenalty: s.OcrGatewayDatePenalty,
            OcrGatewayVatRatePenalty: s.OcrGatewayVatRatePenalty,
            OcrGatewayLowConfidencePenalty: s.OcrGatewayLowConfidencePenalty,
            OcrMaxRetriesPerScan: s.OcrMaxRetriesPerScan)));
    }

    [HttpPut("ocr-config")]
    public async Task<ActionResult<ApiResponse<object>>> UpdateOcrConfig([FromBody] UpdateOcrConfigRequest req)
    {
        var s = await GetOrCreateSiteSettings();

        if (req.AzureDiEndpoint != null) s.AzureDiEndpoint = req.AzureDiEndpoint;
        if (req.AzureDiApiKey != null) s.AzureDiApiKey = req.AzureDiApiKey;
        if (req.AzureDiModelId != null) s.AzureDiModelId = req.AzureDiModelId;
        if (req.AzureDiApiVersion != null) s.AzureDiApiVersion = req.AzureDiApiVersion;
        if (req.AzureDiEnabled.HasValue) s.AzureDiEnabled = req.AzureDiEnabled.Value;
        if (req.OcrProvider != null) s.OcrProvider = req.OcrProvider;
        if (req.OcrLocalServiceUrl != null) s.OcrLocalServiceUrl = req.OcrLocalServiceUrl;
        if (req.OcrAutoCreateThreshold.HasValue) s.OcrAutoCreateThreshold = req.OcrAutoCreateThreshold.Value;
        if (req.OcrFreePagesTrial.HasValue) s.OcrFreePagesTrial = req.OcrFreePagesTrial.Value;
        if (req.OcrFreePagesBasic.HasValue) s.OcrFreePagesBasic = req.OcrFreePagesBasic.Value;
        if (req.OcrFreePagesPro.HasValue) s.OcrFreePagesPro = req.OcrFreePagesPro.Value;
        if (req.OcrFreePagesEnterprise.HasValue) s.OcrFreePagesEnterprise = req.OcrFreePagesEnterprise.Value;
        if (req.OcrCreditPricePerPage.HasValue) s.OcrCreditPricePerPage = req.OcrCreditPricePerPage.Value;
        if (req.OcrCreditMinPurchase.HasValue) s.OcrCreditMinPurchase = req.OcrCreditMinPurchase.Value;
        if (req.OcrGatewayMaxPenalty.HasValue) s.OcrGatewayMaxPenalty = req.OcrGatewayMaxPenalty.Value;
        if (req.OcrGatewayMathTolerance.HasValue) s.OcrGatewayMathTolerance = req.OcrGatewayMathTolerance.Value;
        if (req.OcrGatewayTaxIdPenalty.HasValue) s.OcrGatewayTaxIdPenalty = req.OcrGatewayTaxIdPenalty.Value;
        if (req.OcrGatewayMathPenalty.HasValue) s.OcrGatewayMathPenalty = req.OcrGatewayMathPenalty.Value;
        if (req.OcrGatewayDatePenalty.HasValue) s.OcrGatewayDatePenalty = req.OcrGatewayDatePenalty.Value;
        if (req.OcrGatewayVatRatePenalty.HasValue) s.OcrGatewayVatRatePenalty = req.OcrGatewayVatRatePenalty.Value;
        if (req.OcrGatewayLowConfidencePenalty.HasValue) s.OcrGatewayLowConfidencePenalty = req.OcrGatewayLowConfidencePenalty.Value;
        if (req.OcrMaxRetriesPerScan.HasValue) s.OcrMaxRetriesPerScan = req.OcrMaxRetriesPerScan.Value;

        // Track which fields changed (omit secrets — audit log shouldn't contain raw keys)
        var changedFields = new List<string>();
        if (req.AzureDiEndpoint != null) changedFields.Add("AzureDiEndpoint");
        if (req.AzureDiApiKey != null) changedFields.Add("AzureDiApiKey:[redacted]");
        if (req.AzureDiEnabled.HasValue) changedFields.Add($"AzureDiEnabled={req.AzureDiEnabled.Value}");
        if (req.OcrProvider != null) changedFields.Add($"OcrProvider={req.OcrProvider}");

        await _db.SaveChangesAsync();
        await LogAuditAsync(null, "OcrConfigUpdated", string.Join(", ", changedFields));
        return Ok(new ApiResponse<object>(true, null, "บันทึกการตั้งค่า OCR สำเร็จ"));
    }

    [HttpPost("ocr-config/test-local")]
    public async Task<ActionResult<ApiResponse<object>>> TestLocalOcrService(
        [FromServices] IHttpClientFactory httpClientFactory)
    {
        var s = await GetOrCreateSiteSettings();
        var url = string.IsNullOrEmpty(s.OcrLocalServiceUrl) ? "http://localhost:8501" : s.OcrLocalServiceUrl;
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync($"{url.TrimEnd('/')}/health");
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                return Ok(new ApiResponse<object>(true, new { url, status = (int)response.StatusCode, body },
                    $"เชื่อมต่อ Local OCR Service สำเร็จ ({url})"));
            }
            return Ok(new ApiResponse<object>(false, null,
                $"Local OCR Service ตอบ HTTP {(int)response.StatusCode} — ตรวจสอบว่า microservice รันอยู่ที่ {url}"));
        }
        catch (Exception ex)
        {
            return Ok(new ApiResponse<object>(false, null,
                $"เชื่อมต่อ Local OCR Service ไม่ได้: {ex.Message} — ตรวจสอบ URL/firewall/microservice"));
        }
    }

    /// <summary>
    /// SystemAdmin trains the system-wide OCR knowledge base. Writes to the
    /// SystemOcrCategoryMappings + SystemOcrVendorIntelligence tables (no
    /// CompanyId) so every tenant gains the learned mapping as a fallback
    /// when they have no prior history with the same vendor.
    ///
    /// Tenant-specific data — built from each company's own approved
    /// documents — always wins at prediction time; this seeded knowledge is
    /// consulted only when no tenant row matches.
    /// </summary>
    [HttpPost("ocr-config/train")]
    public async Task<ActionResult<ApiResponse<object>>> TrainOcrFromSystemAdmin(
        [FromBody] SystemAdminTrainRequest req,
        [FromServices] Services.Implementations.Ocr.ExpenseCategoryLearner learner,
        [FromServices] Services.Implementations.Ocr.VendorIntelligenceService vendorIntel)
    {
        if (string.IsNullOrWhiteSpace(req.VendorName) && string.IsNullOrWhiteSpace(req.VendorTaxId))
            return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุชื่อหรือเลขประจำตัวผู้ขาย"));

        // System-level training has no per-tenant Chart-of-Accounts to resolve
        // AccountName from — admin supplies the AccountCode (e.g. "5402") and
        // the human-readable name they remember; tenant-side lookup at predict
        // time can enrich the display if needed.
        var accountName = string.IsNullOrWhiteSpace(req.AccountName) ? null : req.AccountName.Trim();
        var weight = Math.Max(1, req.Weight ?? 1);

        // 1. ExpenseCategoryLearner (system-wide) — per-line records
        var trainedLines = 0;
        if (req.Lines != null && req.Lines.Count > 0 && !string.IsNullOrEmpty(req.AccountCode))
        {
            foreach (var line in req.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Description)) continue;
                await learner.RecordSystemAsync(req.VendorTaxId, req.VendorName,
                    line.Description, req.AccountCode, accountName, weight: weight);
                trainedLines++;
            }
        }
        else if (!string.IsNullOrEmpty(req.AccountCode))
        {
            await learner.RecordSystemAsync(req.VendorTaxId, req.VendorName,
                req.Description ?? "", req.AccountCode, accountName, weight: weight);
            trainedLines = 1;
        }

        // 2. VendorIntelligence (system-wide) — per-vendor stats
        Models.Enums.DocumentType? docType = null;
        if (!string.IsNullOrEmpty(req.DocumentType)
            && Enum.TryParse<Models.Enums.DocumentType>(req.DocumentType, ignoreCase: true, out var dt))
            docType = dt;

        await vendorIntel.TrainFromAdminSystemAsync(
            req.VendorTaxId, req.VendorName,
            docType, req.AccountCode, accountName,
            req.WhtRate, req.PaymentTermsDays,
            weight: weight);

        await LogAuditAsync(null, "OcrTrainedFromSystemAdmin",
            $"SystemAdmin trained SYSTEM vendor '{req.VendorName ?? req.VendorTaxId}' " +
            $"→ {req.AccountCode}{(docType.HasValue ? $" + {docType.Value}" : "")} " +
            $"({trainedLines} line(s), weight {weight})");

        return Ok(new ApiResponse<object>(true, new
        {
            scope = "system",
            vendorKey = req.VendorTaxId ?? req.VendorName,
            accountName,
            trainedLines,
            trainedDocumentType = docType?.ToString(),
            trainedWhtRate = req.WhtRate,
            trainedPaymentTerms = req.PaymentTermsDays
        }, $"สอนระบบกลางเรียบร้อย: ผู้ขาย '{req.VendorName ?? req.VendorTaxId}' → {req.AccountCode}" +
           $"{(docType.HasValue ? $" + {docType.Value}" : "")}{(req.WhtRate.HasValue ? $" + WHT {req.WhtRate}%" : "")} (ใช้ได้ทุกบริษัท)"));
    }

    public record SystemAdminTrainRequest(
        string? VendorName,
        string? VendorTaxId,
        string? Description,
        string? AccountCode,
        string? AccountName,            // optional — admin-supplied label since CoA is per-tenant
        string? DocumentType,           // "PurchaseInvoice" | "Expense" | ...
        decimal? WhtRate,
        int? PaymentTermsDays,
        int? Weight,                    // confidence — default 1
        List<TrainLineItem>? Lines);    // per-line records (optional)

    public record TrainLineItem(string Description, string? AccountCode, decimal? Amount);

    /// <summary>
    /// Run the system-wide Apriori-style basket-analysis miner. Scans every
    /// approved/paid Document in the last `sinceMonths` months across all
    /// tenants, discovers (vendor + keyword) → account association rules,
    /// and persists the top 2000 by lift to SystemOcrAssociationRules.
    ///
    /// Intended to be run on a schedule (e.g. nightly) or after a bulk
    /// approval — not per scan. The scan-time consumer pulls the cached
    /// rules from the DB and uses them as one signal in the category
    /// resolver fusion.
    /// </summary>
    [HttpPost("ocr-config/mine-association-rules")]
    public async Task<ActionResult<ApiResponse<object>>> MineAssociationRules(
        [FromServices] Services.Implementations.Ocr.AssociationRuleMiner miner,
        [FromQuery] decimal minSupport = 0.005m,
        [FromQuery] decimal minConfidence = 0.5m,
        [FromQuery] int sinceMonths = 24)
    {
        var result = await miner.MineSystemWideAsync(minSupport, minConfidence, sinceMonths);
        await LogAuditAsync(null, "OcrAssociationRulesMined",
            $"Scanned {result.TransactionsScanned} docs → {result.RulesDiscovered} rules " +
            $"(persisted {result.RulesPersisted}) in {result.Duration.TotalSeconds:F1}s");
        return Ok(new ApiResponse<object>(true, new
        {
            transactionsScanned = result.TransactionsScanned,
            rulesDiscovered = result.RulesDiscovered,
            rulesPersisted = result.RulesPersisted,
            durationSeconds = result.Duration.TotalSeconds,
            minSupport,
            minConfidence,
            sinceMonths,
        }, $"ขุดกฎเสร็จ: {result.RulesPersisted} กฎจาก {result.TransactionsScanned} เอกสาร"));
    }

    /// <summary>
    /// Run K-means vendor clustering across the entire system. Each
    /// vendor's debit-account distribution becomes a feature vector;
    /// vendors that cluster together share spend patterns. Useful for
    /// "similar vendor" suggestions and cold-start predictions on new
    /// suppliers.
    /// </summary>
    [HttpPost("ocr-config/cluster-vendors")]
    public async Task<ActionResult<ApiResponse<object>>> ClusterVendors(
        [FromServices] Services.Implementations.Ocr.VendorClusteringService clustering,
        [FromQuery] int k = 8,
        [FromQuery] int maxIterations = 50)
    {
        var result = await clustering.ClusterAsync(k, maxIterations);
        return Ok(new ApiResponse<object>(true, new
        {
            k = result.K,
            vendorsClustered = result.VendorsClustered,
            iterations = result.Iterations,
            durationSeconds = result.Duration.TotalSeconds,
        }, $"จัดกลุ่ม vendor ด้วย K-means สำเร็จ ({result.VendorsClustered} vendors → {result.K} กลุ่ม)"));
    }

    /// <summary>
    /// Benford's Law fraud / data-quality check across all approved
    /// document totals for a company. Reports the χ² statistic against
    /// the expected logarithmic distribution of leading digits — high
    /// χ² values warrant investigation for data fabrication or systemic
    /// rounding.
    /// </summary>
    [HttpGet("ocr-config/benford/{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> RunBenford(Guid companyId)
    {
        var amounts = await _db.Documents.AsNoTracking()
            .Where(d => !d.IsDeleted && d.CompanyId == companyId
                && (d.Status == Models.Enums.DocumentStatus.Approved
                    || d.Status == Models.Enums.DocumentStatus.Paid)
                && d.TotalAmount > 0)
            .Select(d => d.TotalAmount)
            .ToListAsync();
        var result = Services.Implementations.Ocr.BenfordsLawAnalyzer.Analyze(amounts);
        if (result == null)
            return Ok(new ApiResponse<object>(true, new { sampleSize = amounts.Count },
                $"ตัวอย่างน้อยเกินไป (n={amounts.Count}, ต้อง ≥30)"));
        return Ok(new ApiResponse<object>(true, result, result.Interpretation));
    }

    /// <summary>
    /// System-level OCR test — SystemAdmin uploads a file from the /admin
    /// shell, runs it through the requested provider (no quota, no tenant
    /// context, no training), and gets back raw text + parsed fields for
    /// inspection. Lets the SystemAdmin verify "is OCR actually working in
    /// production?" without having to switch into a tenant login.
    /// </summary>
    [HttpPost("ocr-config/test-scan")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<object>>> TestScanSystemLevel(
        IFormFile file,
        [FromQuery] string? provider,  // "embedded" (default) | "azure" | "python"
        [FromServices] Services.Implementations.Ocr.EmbeddedTesseractOcrService embedded,
        [FromServices] Services.Implementations.Ocr.AzureDocumentIntelligenceService azureDi,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] IConfiguration configuration)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "กรุณาเลือกไฟล์"));

        byte[] fileBytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            fileBytes = ms.ToArray();
        }

        string rawText = "";
        decimal confidence = 0;
        string providerUsed = "";
        string? error = null;

        try
        {
            switch ((provider ?? "embedded").ToLowerInvariant())
            {
                case "embedded":
                    providerUsed = "Embedded Tesseract";
                    var emb = await embedded.ExtractTextAsync(fileBytes, file.ContentType ?? "", file.FileName);
                    rawText = emb.Text;
                    confidence = emb.Confidence;
                    error = emb.Success ? null : emb.Error;
                    break;

                case "azure":
                {
                    providerUsed = "Azure DI";
                    var s = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync();
                    if (s?.AzureDiEnabled != true || string.IsNullOrEmpty(s.AzureDiEndpoint) || string.IsNullOrEmpty(s.AzureDiApiKey))
                    {
                        error = "Azure DI ยังไม่ได้ตั้งค่าหรือปิดอยู่";
                        break;
                    }
                    var azResult = await azureDi.AnalyzeAsync(fileBytes, file.ContentType ?? "application/octet-stream", s);
                    rawText = azResult?.RawText ?? "";
                    confidence = azResult?.OverallConfidence ?? 0m;
                    break;
                }

                case "python":
                {
                    providerUsed = "Python local service";
                    var s = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync();
                    var pyUrl = string.IsNullOrEmpty(s?.OcrLocalServiceUrl)
                        ? (configuration["Ocr:LocalServiceUrl"] ?? "")
                        : s.OcrLocalServiceUrl;
                    if (string.IsNullOrEmpty(pyUrl))
                    {
                        error = "Python service URL ไม่ได้ตั้งค่า";
                        break;
                    }
                    try
                    {
                        var client = httpClientFactory.CreateClient();
                        client.Timeout = TimeSpan.FromSeconds(60);
                        using var content = new MultipartFormDataContent();
                        var byteContent = new ByteArrayContent(fileBytes);
                        byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");
                        content.Add(byteContent, "file", file.FileName ?? "upload");
                        var resp = await client.PostAsync($"{pyUrl.TrimEnd('/')}/ocr", content);
                        var body = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode)
                        {
                            error = $"Python service HTTP {(int)resp.StatusCode}";
                            break;
                        }
                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("text", out var t)) rawText = t.GetString() ?? "";
                        else if (doc.RootElement.TryGetProperty("raw_text", out var rt)) rawText = rt.GetString() ?? "";
                        if (doc.RootElement.TryGetProperty("confidence", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number)
                            confidence = (decimal)c.GetDouble();
                    }
                    catch (Exception ex) { error = $"Python service error: {ex.Message}"; }
                    break;
                }

                default:
                    error = $"Unknown provider: {provider}";
                    break;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // Run rule-based parser on whatever text we got — same regex as the
        // tenant test playground so SystemAdmin sees the same field extraction
        // preview that production gives.
        var parsed = string.IsNullOrEmpty(rawText) ? null : ParseAdminPreview(rawText);

        return Ok(new ApiResponse<object>(error == null, new
        {
            provider = providerUsed,
            embeddedAvailable = embedded.IsAvailable,
            embeddedLanguages = embedded.Languages,
            rawText,
            confidence,
            parsedFields = parsed,
            error
        }, error ?? "OCR สำเร็จ"));
    }

    /// <summary>
    /// Minimal regex-based parser duplicated from OcrController — keeps this
    /// admin endpoint self-contained so SystemAdmin testing doesn't require a
    /// tenant context to extract preview fields.
    /// </summary>
    private static object ParseAdminPreview(string text)
    {
        var upperText = text.ToUpperInvariant();
        string? docType = null;
        if (text.Contains("ใบกำกับภาษี") || upperText.Contains("TAX INVOICE")) docType = "TaxInvoice";
        else if (text.Contains("ใบรับรองแทนใบเสร็จ") || upperText.Contains("CERTIFICATE IN LIEU")) docType = "CertificateInLieu";
        else if (text.Contains("ใบลดหนี้") || upperText.Contains("CREDIT NOTE")) docType = "CreditNote";
        else if (text.Contains("ใบเพิ่มหนี้") || upperText.Contains("DEBIT NOTE")) docType = "DebitNote";
        else if (text.Contains("ใบสั่งซื้อ") || upperText.Contains("PURCHASE ORDER")) docType = "PurchaseOrder";
        else if (text.Contains("ใบแจ้งหนี้") || (upperText.Contains("INVOICE") && !upperText.Contains("TAX INVOICE"))) docType = "Invoice";
        else if (text.Contains("ใบเสร็จรับเงิน") || upperText.Contains("RECEIPT")) docType = "Receipt";

        var taxIdPattern = @"(\d{1}\s*-?\s*\d{4}\s*-?\s*\d{5}\s*-?\s*\d{2}\s*-?\s*\d{1})";
        var taxIds = System.Text.RegularExpressions.Regex.Matches(text, taxIdPattern)
            .Select(m => new string(m.Value.Where(char.IsDigit).ToArray()))
            .Where(s => s.Length == 13).Distinct().Take(5).ToArray();
        if (taxIds.Length == 0)
            taxIds = System.Text.RegularExpressions.Regex.Matches(text, @"\d{13}")
                .Select(m => m.Value).Distinct().Take(5).ToArray();

        var amounts = System.Text.RegularExpressions.Regex.Matches(text, @"(\d{1,3}(?:,\d{3})*\.\d{2}|\d+\.\d{2})")
            .Select(m => m.Value).Take(20).ToArray();
        var dates = System.Text.RegularExpressions.Regex.Matches(text, @"\d{1,2}[/\-\.]\d{1,2}[/\-\.]\d{2,4}")
            .Select(m => m.Value).Take(10).ToArray();
        var docNumbers = System.Text.RegularExpressions.Regex.Matches(text,
            @"(?:เลขที่|No\.?|INV|TAX|REF)\s*[:\#]?\s*([A-Z0-9\-/]{4,20})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value).Take(5).ToArray();

        return new
        {
            documentType = docType,
            taxIds,
            amounts,
            dates,
            documentNumbers = docNumbers,
            textLength = text.Length
        };
    }

    /// <summary>
    /// Status of the in-process embedded Tesseract OCR engine. Unlike the
    /// optional Python service, this engine ships with the .NET app — its
    /// only requirement is the tessdata language files in wwwroot/tessdata.
    /// </summary>
    [HttpGet("ocr-config/embedded-status")]
    public ActionResult<ApiResponse<object>> GetEmbeddedOcrStatus(
        [FromServices] Services.Implementations.Ocr.EmbeddedTesseractOcrService embedded)
    {
        return Ok(new ApiResponse<object>(true, new
        {
            available = embedded.IsAvailable,
            languages = embedded.Languages,
            tessdataPath = embedded.TessdataPath,
            installCommand = "scripts/download-tessdata.sh fast",
        }, embedded.IsAvailable
            ? $"Embedded Tesseract พร้อมใช้งาน ({string.Join("+", embedded.Languages)})"
            : "Embedded Tesseract ใช้ไม่ได้ — ต้อง install tessdata files (eng + tha)"));
    }

    [HttpPost("ocr-config/test-azure")]
    public async Task<ActionResult<ApiResponse<object>>> TestAzureDi()
    {
        var s = await GetOrCreateSiteSettings();
        if (string.IsNullOrEmpty(s.AzureDiEndpoint) || string.IsNullOrEmpty(s.AzureDiApiKey))
            return BadRequest(new ApiResponse<object>(false, null, "กรุณากรอก Azure DI Endpoint และ API Key ก่อน"));

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", s.AzureDiApiKey);
            var apiVersion = s.AzureDiApiVersion ?? "2024-11-30";
            var endpoint = s.AzureDiEndpoint.TrimEnd('/');
            // v4.0 path; fall back to v3.x path for older endpoints
            var response = await client.GetAsync($"{endpoint}/documentintelligence/info?api-version={apiVersion}");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                response = await client.GetAsync($"{endpoint}/formrecognizer/info?api-version=2023-07-31");

            s.AzureDiLastTestedAt = DateTime.UtcNow;
            s.AzureDiLastTestStatus = response.IsSuccessStatusCode ? "OK" : $"Error: {response.StatusCode}";
            await _db.SaveChangesAsync();

            return Ok(new ApiResponse<object>(response.IsSuccessStatusCode,
                new { StatusCode = (int)response.StatusCode },
                response.IsSuccessStatusCode ? "เชื่อมต่อ Azure DI สำเร็จ" : $"ไม่สามารถเชื่อมต่อได้: {response.StatusCode}"));
        }
        catch (Exception ex)
        {
            s.AzureDiLastTestedAt = DateTime.UtcNow;
            s.AzureDiLastTestStatus = $"Error: {ex.Message}";
            await _db.SaveChangesAsync();
            return Ok(new ApiResponse<object>(false, null, $"เชื่อมต่อไม่สำเร็จ: {ex.Message}"));
        }
    }

    // ===== OCR Credit Management (Admin) =====

    [HttpGet("ocr-credits/pending")]
    public async Task<ActionResult<ApiResponse<List<OcrCreditPurchaseResponse>>>> GetPendingOcrCredits()
        => Ok(new ApiResponse<List<OcrCreditPurchaseResponse>>(true, await _ocrQuota.GetPendingPurchasesAsync()));

    [HttpPost("ocr-credits/{purchaseId:guid}/review")]
    public async Task<ActionResult<ApiResponse<OcrCreditPurchaseResponse>>> ReviewOcrCredit(
        Guid purchaseId, [FromBody] ReviewOcrCreditRequest req)
    {
        var result = await _ocrQuota.ReviewCreditPurchaseAsync(
            purchaseId, req.Approve, req.Notes, User.Identity?.Name ?? "");

        await LogAuditAsync(result.CompanyId,
            req.Approve ? "OcrCreditApproved" : "OcrCreditRejected",
            $"Pages: {result.PagesPurchased}, Amount: {result.AmountPaid:N2} THB, Notes: {req.Notes ?? "n/a"}",
            entityId: purchaseId.ToString());

        return Ok(new ApiResponse<OcrCreditPurchaseResponse>(true, result,
            req.Approve ? "อนุมัติเครดิต OCR สำเร็จ" : "ปฏิเสธเครดิต OCR"));
    }

    [HttpGet("ocr-usage-summary")]
    public async Task<ActionResult<ApiResponse<object>>> GetOcrUsageSummary()
    {
        var now = DateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var totalScans = await _db.Set<OcrScanResult>().CountAsync();
        var thisMonthScans = await _db.Set<OcrScanResult>().CountAsync(s => s.CreatedAt >= thisMonth);
        var activeCredits = await _db.OcrCreditPurchases
            .Where(p => p.Status == "Approved" && p.PagesRemaining > 0)
            .SumAsync(p => p.PagesRemaining);
        var pendingCredits = await _db.OcrCreditPurchases.CountAsync(p => p.Status == "Pending");
        var totalCreditRevenue = await _db.OcrCreditPurchases
            .Where(p => p.Status == "Approved")
            .SumAsync(p => p.AmountPaid);

        return Ok(new ApiResponse<object>(true, new
        {
            totalScans,
            thisMonthScans,
            activeCredits,
            pendingCredits,
            totalCreditRevenue,
        }));
    }

    [HttpPost("ocr-credits/{companyId:guid}/grant-bonus")]
    public async Task<ActionResult<ApiResponse<object>>> GrantOcrBonus(Guid companyId, [FromBody] GrantOcrBonusRequest req)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบ Subscription"));

        sub.OcrBonusPages += req.Pages;
        // Default to 12-month expiration when admin doesn't specify, to prevent
        // perpetual bonus inflation across plan changes.
        sub.OcrBonusExpiresAt = req.ExpiresAt ?? DateTime.UtcNow.AddMonths(12);
        await _db.SaveChangesAsync();

        await LogAuditAsync(companyId, "OcrBonusGranted",
            $"Granted {req.Pages} bonus OCR pages (expires {sub.OcrBonusExpiresAt:yyyy-MM-dd}). Reason: {req.Reason ?? "n/a"}");

        return Ok(new ApiResponse<object>(true,
            new { sub.OcrBonusPages, sub.OcrBonusExpiresAt },
            $"เพิ่มโบนัส {req.Pages} หน้าให้บริษัท (หมดอายุ {sub.OcrBonusExpiresAt:yyyy-MM-dd})"));
    }

    private async Task LogAuditAsync(Guid? companyId, string action, string details, string? entityId = null)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            CompanyId = companyId,
            UserId = JwtHelper.GetUserIdFromClaims(User),
            UserEmail = User.Identity?.Name ?? "system-admin",
            Action = AuditAction.Update,
            EntityType = $"OcrAdmin.{action}",
            EntityId = entityId,
            NewValues = details,
            IpAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString(),
            UserAgent = HttpContext?.Request?.Headers["User-Agent"].ToString(),
        });
        await _db.SaveChangesAsync();
    }

    // ===== OCR Self-Correction & Accuracy =====

    [HttpGet("ocr-accuracy")]
    public async Task<ActionResult<ApiResponse<object>>> GetOcrAccuracy(
        [FromServices] Services.Implementations.Ocr.OcrSelfCorrectionService selfCorrection)
    {
        var reports = await selfCorrection.ComputeAccuracyAsync();
        return Ok(new ApiResponse<object>(true, reports));
    }

    [HttpPost("ocr-maintenance/run")]
    public async Task<ActionResult<ApiResponse<object>>> RunOcrMaintenance(
        [FromServices] Services.Implementations.Ocr.OcrSelfCorrectionService selfCorrection)
    {
        await selfCorrection.RunMaintenanceAsync();
        return Ok(new ApiResponse<object>(true, null, "เริ่ม OCR self-correction maintenance สำเร็จ"));
    }
}

// ===== Admin-specific DTOs =====

public record UpdateCustomerStatusRequest(CompanyStatus Status);
public record UpdateUserStatusRequest(UserStatus Status);
public record ToggleAdminRequest(bool IsAdmin);

public record OcrConfigResponse(
    string? AzureDiEndpoint, bool AzureDiHasKey, string? AzureDiModelId, string? AzureDiApiVersion,
    bool AzureDiEnabled, DateTime? AzureDiLastTestedAt, string? AzureDiLastTestStatus,
    string? OcrProvider, string? OcrLocalServiceUrl,
    decimal OcrAutoCreateThreshold,
    int OcrFreePagesTrial, int OcrFreePagesBasic, int OcrFreePagesPro, int OcrFreePagesEnterprise,
    decimal OcrCreditPricePerPage, int OcrCreditMinPurchase,
    decimal OcrGatewayMaxPenalty, decimal OcrGatewayMathTolerance,
    decimal OcrGatewayTaxIdPenalty, decimal OcrGatewayMathPenalty,
    decimal OcrGatewayDatePenalty, decimal OcrGatewayVatRatePenalty,
    decimal OcrGatewayLowConfidencePenalty, int OcrMaxRetriesPerScan);

public record UpdateOcrConfigRequest(
    string? AzureDiEndpoint = null, string? AzureDiApiKey = null,
    string? AzureDiModelId = null, string? AzureDiApiVersion = null,
    bool? AzureDiEnabled = null,
    string? OcrProvider = null, string? OcrLocalServiceUrl = null,
    decimal? OcrAutoCreateThreshold = null,
    int? OcrFreePagesTrial = null, int? OcrFreePagesBasic = null,
    int? OcrFreePagesPro = null, int? OcrFreePagesEnterprise = null,
    decimal? OcrCreditPricePerPage = null, int? OcrCreditMinPurchase = null,
    decimal? OcrGatewayMaxPenalty = null, decimal? OcrGatewayMathTolerance = null,
    decimal? OcrGatewayTaxIdPenalty = null, decimal? OcrGatewayMathPenalty = null,
    decimal? OcrGatewayDatePenalty = null, decimal? OcrGatewayVatRatePenalty = null,
    decimal? OcrGatewayLowConfidencePenalty = null, int? OcrMaxRetriesPerScan = null);

public record ReviewOcrCreditRequest(bool Approve, string? Notes = null);
public record GrantOcrBonusRequest(int Pages, DateTime? ExpiresAt = null, string? Reason = null);
