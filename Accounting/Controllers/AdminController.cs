using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Settings;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Helpers;
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

    public AdminController(
        AccountingDbContext db,
        ISubscriptionService subscriptionService,
        IRecurringTransactionService recurringService)
    {
        _db = db;
        _subscriptionService = subscriptionService;
        _recurringService = recurringService;
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

    // ===== Plan Template Management =====

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
                null, null, null, null, new List<LandingServiceItem>(),
                null, null, null, null, null, null, null, null)));

        var services = DeserializeServices(settings.ServicesJson);
        return Ok(new ApiResponse<SiteSettingsResponse>(true, new SiteSettingsResponse(
            settings.Id, settings.SiteName, settings.SiteDescription, settings.SiteLogoUrl,
            services, settings.ContactPhone, settings.ContactLine, settings.ContactEmail,
            settings.PricingSectionTitle, settings.PricingSectionSubtitle,
            settings.FacebookUrl, settings.LineOfficialUrl, settings.WebsiteUrl)));
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
        settings.ContactPhone = request.ContactPhone;
        settings.ContactLine = request.ContactLine;
        settings.ContactEmail = request.ContactEmail;
        settings.PricingSectionTitle = request.PricingSectionTitle;
        settings.PricingSectionSubtitle = request.PricingSectionSubtitle;
        settings.FacebookUrl = request.FacebookUrl;
        settings.LineOfficialUrl = request.LineOfficialUrl;
        settings.WebsiteUrl = request.WebsiteUrl;

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
            services, settings.ContactPhone, settings.ContactLine, settings.ContactEmail,
            settings.PricingSectionTitle, settings.PricingSectionSubtitle,
            settings.FacebookUrl, settings.LineOfficialUrl, settings.WebsiteUrl),
            "บันทึกการตั้งค่าสำเร็จ"));
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
            settings?.ContactPhone, settings?.ContactLine, settings?.ContactEmail,
            services,
            settings?.PricingSectionTitle, settings?.PricingSectionSubtitle,
            settings?.FacebookUrl, settings?.LineOfficialUrl, settings?.WebsiteUrl)));
    }

    private static List<LandingServiceItem> DeserializeServices(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new List<LandingServiceItem>();
        try
        {
            return JsonSerializer.Deserialize<List<LandingServiceItem>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { return new(); }
    }
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
}

// ===== Admin-specific DTOs =====

public record UpdateCustomerStatusRequest(CompanyStatus Status);
public record UpdateUserStatusRequest(UserStatus Status);
public record ToggleAdminRequest(bool IsAdmin);
