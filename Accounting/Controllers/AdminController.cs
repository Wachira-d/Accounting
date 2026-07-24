using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Email;
using Accounting.Models.DTOs.Settings;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.DTOs.Company;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Helpers;
using Accounting.Services;
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
    private readonly ISecretProtector _secrets;
    private readonly IImageProcessingService _images;
    private readonly IWebHostEnvironment _env;
    private readonly ISaasBillingDocumentService _billing;

    public AdminController(
        AccountingDbContext db,
        ISubscriptionService subscriptionService,
        IRecurringTransactionService recurringService,
        IEmailSenderFactory emailSenderFactory,
        IOcrQuotaService ocrQuota,
        ISecretProtector secrets,
        IImageProcessingService images,
        IWebHostEnvironment env,
        ISaasBillingDocumentService billing)
    {
        _db = db;
        _subscriptionService = subscriptionService;
        _recurringService = recurringService;
        _emailSenderFactory = emailSenderFactory;
        _ocrQuota = ocrQuota;
        _secrets = secrets;
        _images = images;
        _env = env;
        _billing = billing;
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

        // Account Plans (User-level Licenses) — separate KPI block so support
        // staff can see how many Licenses are out, how many are about to expire,
        // and the user-side MRR vs company-side MRR.
        var accountPlans = await _db.AccountSubscriptions.Where(a => !a.IsDeleted).ToListAsync();
        var accountActive = accountPlans.Count(a => a.Status == SubscriptionStatus.Active);
        var accountTrial = accountPlans.Count(a => a.Status == SubscriptionStatus.Trial);
        var accountExpiring = accountPlans.Count(a => a.Status == SubscriptionStatus.Active
                                                  && a.EndDate <= now.AddDays(7));
        var accountCompaniesCovered = await _db.Subscriptions
            .CountAsync(s => s.AccountSubscriptionId != null && !s.IsDeleted);
        var accountMrr = accountPlans
            .Where(a => a.Status == SubscriptionStatus.Active)
            .Sum(a => a.BillingCycle switch
            {
                BillingCycle.Monthly => a.MonthlyPrice,
                BillingCycle.Annual => a.AnnualPrice / 12m,
                _ => a.MonthlyPrice,
            });

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
            accountPlans = new
            {
                active = accountActive,
                trial = accountTrial,
                expiringIn7d = accountExpiring,
                total = accountPlans.Count,
                companiesCovered = accountCompaniesCovered,
                companiesUnderAccount = accountCompaniesCovered,
                companiesStandalone = subscriptions.Count(s => s.AccountSubscriptionId == null),
                mrr = Math.Round(accountMrr, 2),
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
                .ThenInclude(s => s!.AccountSubscription)
                    .ThenInclude(a => a!.PlanTemplate)
            .Include(c => c.Subscription)
                .ThenInclude(s => s!.AccountSubscription)
                    .ThenInclude(a => a!.Owner)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(c => c.Name.Contains(search) || c.TaxId.Contains(search) ||
                c.CompanyUsers.Any(cu => cu.User.Email.Contains(search)));
        }

        // Filter by plan: match the EFFECTIVE plan. A company attached to an
        // Enterprise License should appear under the "Enterprise" filter even
        // though its per-company sub.Plan is still FreeTrial — that was the
        // original bug ("เน็ก แอค" attached to Enterprise still showed
        // ทดลองใช้" in the list).
        if (Enum.TryParse<SubscriptionPlan>(plan, true, out var planEnum))
        {
            query = query.Where(c => c.Subscription != null && (
                (c.Subscription.AccountSubscriptionId == null && c.Subscription.Plan == planEnum) ||
                (c.Subscription.AccountSubscription != null && c.Subscription.AccountSubscription.PlanTemplate.Plan == planEnum)
            ));
        }

        // Same overlay logic for status.
        if (Enum.TryParse<SubscriptionStatus>(status, true, out var statusEnum))
        {
            query = query.Where(c => c.Subscription != null && (
                (c.Subscription.AccountSubscriptionId == null && c.Subscription.Status == statusEnum) ||
                (c.Subscription.AccountSubscription != null && c.Subscription.AccountSubscription.Status == statusEnum)
            ));
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
                    // Effective plan + status + end-date come from the License
                    // when attached. Without this overlay the list view kept
                    // showing the stale per-company FreeTrial labels even after
                    // admin attached the company to an Enterprise License.
                    Plan = c.Subscription.AccountSubscription != null
                        ? c.Subscription.AccountSubscription.PlanTemplate.Plan
                        : c.Subscription.Plan,
                    Status = c.Subscription.AccountSubscription != null
                        ? c.Subscription.AccountSubscription.Status
                        : c.Subscription.Status,
                    // Billing fields stay per-company — they describe how
                    // THIS company is billed, which is independent of the
                    // License's pricing.
                    c.Subscription.BillingCycle,
                    c.Subscription.PricePerCycle,
                    c.Subscription.StartDate,
                    EndDate = c.Subscription.AccountSubscription != null
                        ? c.Subscription.AccountSubscription.EndDate
                        : c.Subscription.EndDate,
                    // Surface the License attachment so the UI can show a
                    // "via License: <ownerName>" badge if it wants to.
                    ViaLicense = c.Subscription.AccountSubscriptionId != null,
                    LicenseOwnerEmail = c.Subscription.AccountSubscription != null
                        ? c.Subscription.AccountSubscription.Owner.Email
                        : null,
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

        // When the company is under an Account Plan, surface who pays + the
        // plan name so support can jump there to renew / extend at the right
        // layer. Without this admin would extend Company.Subscription.EndDate
        // while the parent AccountSubscription.EndDate quietly expires.
        object? accountPlan = null;
        if (company.Subscription?.AccountSubscriptionId != null)
        {
            var acct = await _db.AccountSubscriptions
                .Include(a => a.Owner).Include(a => a.PlanTemplate)
                .FirstOrDefaultAsync(a => a.Id == company.Subscription.AccountSubscriptionId.Value && !a.IsDeleted);
            if (acct != null)
            {
                accountPlan = new
                {
                    id = acct.Id,
                    ownerUserId = acct.OwnerUserId,
                    ownerName = acct.Owner.FullName,
                    ownerEmail = acct.Owner.Email,
                    planName = acct.PlanTemplate.Name,
                    status = acct.Status.ToString(),
                    endDate = acct.EndDate,
                    maxCompanies = acct.MaxCompanies,
                };
            }
        }

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
                company.Subscription.EnabledFeatures,
                company.Subscription.AccountSubscriptionId,
            },
            accountPlan,
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

        // Per-user AccountSubscription badge — surface "🎫 Pro 2/3" so support
        // staff can see at a glance whose License is covering what before
        // touching company-level subscriptions.
        var userIds = users.Select(u => u.Id).ToList();
        var plans = await _db.AccountSubscriptions
            .Include(a => a.PlanTemplate)
            .Where(a => userIds.Contains(a.OwnerUserId) && !a.IsDeleted
                && (a.Status == SubscriptionStatus.Trial || a.Status == SubscriptionStatus.Active
                    || a.Status == SubscriptionStatus.PastDue || a.Status == SubscriptionStatus.Suspended))
            .ToListAsync();
        var planIds = plans.Select(p => p.Id).ToList();
        var coCounts = await _db.Subscriptions
            .Where(s => s.AccountSubscriptionId != null && planIds.Contains(s.AccountSubscriptionId.Value) && !s.IsDeleted)
            .GroupBy(s => s.AccountSubscriptionId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count);
        var byUser = plans.ToDictionary(p => p.OwnerUserId, p => new
        {
            planId = p.Id,
            planName = p.PlanTemplate.Name,
            status = p.Status.ToString(),
            maxCompanies = p.MaxCompanies,
            companiesUsed = coCounts.GetValueOrDefault(p.Id, 0),
            endDate = p.EndDate,
        });

        var withPlans = users.Select(u => new
        {
            u.Id, u.Email, u.FullName, u.Phone, u.Status, u.IsSystemAdmin,
            u.LastLoginAt, u.CreatedAt, u.companies,
            accountPlan = byUser.TryGetValue(u.Id, out var ap) ? ap : null,
        });

        return Ok(new ApiResponse<object>(true, new
        {
            items = withPlans, total, page, pageSize,
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

    /// <summary>System-wide Azure DI / Local OCR usage for the current
    /// billing month. Drives the "ดูการใช้งานเดือนนี้" widget on the
    /// admin OCR config page so the operator can tell when to top up the
    /// Azure Cognitive Services credit before the next page-load hits 429.
    ///
    /// Sources page counters from Subscription.CurrentMonthAzureOcrPages /
    /// CurrentMonthLocalOcrPages (incremented by OcrQuotaService on each
    /// successful scan). Linear forecast = pages-so-far × days-in-month /
    /// days-elapsed; rough but good enough to spot "we're burning credit
    /// 3× last month's rate" early.
    ///
    /// Pricing — Azure DI prebuilt-invoice S0 list price as of 2024-11:
    /// US$0.05 / page for the first 1M pages then sliding to $0.03 above
    /// 1M. We compute at the simple flat rate; the operator validates
    /// against their actual Azure invoice. F0 (Free) tenants are flagged
    /// "0 USD billed, 500-page free cap" so they see runway not cost.
    /// </summary>
    [HttpGet("ocr/azure-usage")]
    public async Task<ActionResult<ApiResponse<object>>> GetAzureOcrUsage(
        [FromQuery] decimal? pricePerPageUsd = 0.05m,
        [FromQuery] decimal? usdToThb = 36.5m,
        [FromQuery] int topN = 10)
    {
        // Sum across active (non-soft-deleted) subscriptions — counters are
        // reset by the lazy monthly-reset logic in OcrQuotaService, so a
        // sub whose UsageResetDate is in the past contributes 0 here even
        // though the column hasn't been zeroed yet (we mirror that logic).
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEnd = monthStart.AddMonths(1);
        var daysInMonth = (monthEnd - monthStart).TotalDays;
        var daysElapsed = Math.Max(1, (now - monthStart).TotalDays);

        var liveSubs = await _db.Subscriptions.AsNoTracking()
            .Where(s => !s.IsDeleted && s.UsageResetDate > now)
            .Select(s => new
            {
                s.CompanyId,
                s.CurrentMonthAzureOcrPages,
                s.CurrentMonthLocalOcrPages,
                s.CurrentMonthOcrPages,
            })
            .ToListAsync();

        var totalAzure = liveSubs.Sum(s => s.CurrentMonthAzureOcrPages);
        var totalLocal = liveSubs.Sum(s => s.CurrentMonthLocalOcrPages);
        var totalAll = liveSubs.Sum(s => s.CurrentMonthOcrPages);

        // Per-tenant top N for the "which company is burning credit" view.
        var topCompanyIds = liveSubs
            .Where(s => s.CurrentMonthAzureOcrPages > 0)
            .OrderByDescending(s => s.CurrentMonthAzureOcrPages)
            .Take(topN)
            .Select(s => s.CompanyId)
            .ToList();
        var companyNames = await _db.Companies.AsNoTracking()
            .Where(c => topCompanyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);
        var topRows = liveSubs
            .Where(s => topCompanyIds.Contains(s.CompanyId))
            .OrderByDescending(s => s.CurrentMonthAzureOcrPages)
            .Select(s => new
            {
                companyId = s.CompanyId,
                companyName = companyNames.GetValueOrDefault(s.CompanyId, "(no name)"),
                azurePages = s.CurrentMonthAzureOcrPages,
                localPages = s.CurrentMonthLocalOcrPages,
            })
            .ToList();

        var pricePerPage = pricePerPageUsd ?? 0.05m;
        var fx = usdToThb ?? 36.5m;
        var costUsd = Math.Round(totalAzure * pricePerPage, 2, MidpointRounding.AwayFromZero);
        var costThb = Math.Round(costUsd * fx, 2, MidpointRounding.AwayFromZero);
        var forecastPages = (int)Math.Ceiling(totalAzure * daysInMonth / daysElapsed);
        var forecastUsd = Math.Round(forecastPages * pricePerPage, 2, MidpointRounding.AwayFromZero);
        var forecastThb = Math.Round(forecastUsd * fx, 2, MidpointRounding.AwayFromZero);

        return Ok(new ApiResponse<object>(true, new
        {
            month = monthStart.ToString("yyyy-MM"),
            daysElapsed = (int)daysElapsed,
            daysInMonth = (int)daysInMonth,
            totalAzurePages = totalAzure,
            totalLocalPages = totalLocal,
            totalAllPages = totalAll,
            // Currently-billed (Azure DI charges per page consumed).
            pricing = new
            {
                pricePerPageUsd = pricePerPage,
                usdToThb = fx,
                currentMonthCostUsd = costUsd,
                currentMonthCostThb = costThb,
                forecastFullMonthPages = forecastPages,
                forecastFullMonthCostUsd = forecastUsd,
                forecastFullMonthCostThb = forecastThb,
            },
            // Burn-rate signals for the budget dashboard.
            burnRate = new
            {
                pagesPerDay = Math.Round(totalAzure / daysElapsed, 1),
                pagesPerDayLocal = Math.Round(totalLocal / daysElapsed, 1),
            },
            topCompanies = topRows,
            activeSubscriptionsTracked = liveSubs.Count,
        }, null));
    }

    /// <summary>ลบบริษัท (soft-delete) จาก admin console — SystemAdmin + พิมพ์ชื่อ
    /// บริษัทยืนยัน. ข้อมูลบัญชีคงอยู่ตาม พ.ร.บ.การบัญชี ม.10 (5 ปี); บริษัทหาย
    /// จากรายการ/สลับเข้าไม่ได้ทันทีทุก user (query filter !IsDeleted).</summary>
    [HttpDelete("companies/{companyId:guid}")]
    public async Task<ActionResult> DeleteCompany(Guid companyId, [FromQuery] string confirmName,
        [FromServices] ILogger<AdminController>? logger = null)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท (หรือถูกลบไปแล้ว)");
        if (!string.Equals((confirmName ?? "").Trim(), company.Name.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "ชื่อบริษัทที่พิมพ์ยืนยันไม่ตรง — กรุณาพิมพ์ชื่อบริษัทให้ตรงทุกตัวอักษร");
        company.IsDeleted = true;
        company.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        logger?.LogWarning("ADMIN ลบบริษัท {Name} ({Id}) โดย {Admin}",
            company.Name, companyId, User.Identity?.Name ?? "system-admin");
        return NoContent();
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
    public async Task<ActionResult<ApiResponse<List<PlanTemplateResponse>>>> GetPlanTemplates(
        [FromQuery] bool includeInactive = false,
        [FromServices] ILogger<AdminController>? logger = null)
    {
        try
        {
            var result = await _subscriptionService.GetPlanTemplatesAsync(includeInactive);
            return Ok(new ApiResponse<List<PlanTemplateResponse>>(true, result));
        }
        catch (Exception ex)
        {
            // Localize the failure so the admin can see WHICH plan template
            // triggered the issue — letting the middleware swallow turns
            // every problem into a generic "An internal server error occurred".
            logger?.LogError(ex, "GetPlanTemplates failed (includeInactive={Inc}): {Type} — {Msg}",
                includeInactive, ex.GetType().FullName, ex.Message);
            throw;
        }
    }

    [HttpPut("plans/{templateId:guid}")]
    public async Task<ActionResult<ApiResponse<PlanTemplateResponse>>> UpdatePlanTemplate(Guid templateId, [FromBody] UpdatePlanTemplateRequest request)
    {
        var result = await _subscriptionService.UpdatePlanTemplateAsync(templateId, request);
        return Ok(new ApiResponse<PlanTemplateResponse>(true, result, "อัพเดท plan template สำเร็จ"));
    }

    /// <summary>
    /// Force-resync every subscription on a given plan against the current
    /// template values. Use when admin edited the template but propagation
    /// didn't kick (rare — usually only needed for subscriptions whose
    /// status was hand-changed in the DB and never re-flowed through the
    /// upgrade path). Same semantics as Trial/Paid branches in
    /// UpdatePlanTemplateAsync — Trial subs get TrialMaxOcrPagesPerMonth,
    /// Active/etc. subs get the full per-engine quotas.
    /// </summary>
    [HttpPost("plans/{templateId:guid}/resync-subscriptions")]
    public async Task<ActionResult<ApiResponse<object>>> ResyncSubscriptions(Guid templateId)
    {
        var n = await _subscriptionService.ResyncSubscriptionsFromTemplateAsync(templateId);
        return Ok(new ApiResponse<object>(true, new { syncedCount = n },
            $"อัปเดต {n} subscription จาก template สำเร็จ"));
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
                p.ReceiptNumber, p.ReceiptIsTaxInvoice, p.Kind,
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

    /// <summary>WP-C1: admin บันทึกรับเงินเอง (เงินสด/โอนนอกระบบ) หรือยกเว้นค่าบริการ
    /// → สร้าง payment record + อนุมัติทันที + ต่ออายุ. แทนการต่ออายุแบบไร้ร่องรอย.</summary>
    [HttpPost("companies/{companyId:guid}/subscription-payments/record")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentResponse>>> RecordManualPayment(
        Guid companyId, [FromBody] RecordManualPaymentRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        try
        {
            var result = await _subscriptionService.RecordManualPaymentAsync(companyId, request, userId);
            var message = request.IsWaived
                ? "บันทึกการยกเว้นค่าบริการ + ต่ออายุแล้ว"
                : "บันทึกรับเงิน + ต่ออายุ Subscription แล้ว";
            return Ok(new ApiResponse<SubscriptionPaymentResponse>(true, result, message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<SubscriptionPaymentResponse>(false, null, ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiResponse<SubscriptionPaymentResponse>(false, null, ex.Message));
        }
    }

    /// <summary>WP-B2: ดาวน์โหลดใบเสร็จ/ใบกำกับค่าบริการ SaaS ของ payment.</summary>
    [HttpGet("subscription-payments/{paymentId:guid}/receipt")]
    public async Task<IActionResult> DownloadReceipt(Guid paymentId)
    {
        var pdf = await _billing.GetReceiptPdfAsync(paymentId);
        if (pdf == null)
            return NotFound(new ApiResponse<object>(false, null, "ยังไม่มีใบเสร็จสำหรับรายการนี้"));
        return File(pdf.Value.Bytes, "application/pdf", pdf.Value.FileName);
    }

    /// <summary>WP-B1: admin สั่งออก/ดาวน์โหลดใบแจ้งหนี้ต่ออายุของบริษัท.</summary>
    [HttpPost("companies/{companyId:guid}/renewal-invoice")]
    public async Task<ActionResult<ApiResponse<object>>> IssueRenewalInvoice(Guid companyId)
    {
        var subId = await _db.Subscriptions.Where(s => s.CompanyId == companyId)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync();
        if (subId == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ subscription"));
        var number = await _billing.GenerateRenewalInvoiceAsync(subId.Value);
        return number == null
            ? BadRequest(new ApiResponse<object>(false, null, "ออกใบแจ้งหนี้ไม่ได้ (อาจเป็นแพ็กเกจฟรี/ไม่มีราคา)"))
            : Ok(new ApiResponse<object>(true, new { invoiceNumber = number }, $"ออกใบแจ้งหนี้ {number} แล้ว"));
    }

    [HttpGet("companies/{companyId:guid}/renewal-invoice")]
    public async Task<IActionResult> DownloadRenewalInvoice(Guid companyId)
    {
        var subId = await _db.Subscriptions.Where(s => s.CompanyId == companyId)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync();
        if (subId == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ subscription"));
        var pdf = await _billing.GetRenewalInvoicePdfAsync(subId.Value);
        if (pdf == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่มีใบแจ้งหนี้ต่ออายุ"));
        return File(pdf.Value.Bytes, "application/pdf", pdf.Value.FileName);
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

    // ===== Platform Billing Seller Identity (WP-B2) =====
    public sealed record PlatformBillingSettingsDto(
        string? PlatformSellerName, string? PlatformSellerTaxId, string? PlatformSellerBranchCode,
        string? PlatformSellerAddress, string? PlatformSellerPhone, string? PlatformSellerEmail,
        bool PlatformIsVatRegistered, bool PlatformPriceIncludesVat);

    [HttpGet("platform-billing-settings")]
    public async Task<ActionResult<ApiResponse<PlatformBillingSettingsDto>>> GetPlatformBilling()
    {
        var s = await _db.SiteSettings.AsNoTracking().OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
        return Ok(new ApiResponse<PlatformBillingSettingsDto>(true, new PlatformBillingSettingsDto(
            s?.PlatformSellerName, s?.PlatformSellerTaxId, s?.PlatformSellerBranchCode ?? "00000",
            s?.PlatformSellerAddress, s?.PlatformSellerPhone, s?.PlatformSellerEmail,
            s?.PlatformIsVatRegistered ?? false, s?.PlatformPriceIncludesVat ?? true)));
    }

    [HttpPut("platform-billing-settings")]
    public async Task<ActionResult<ApiResponse<PlatformBillingSettingsDto>>> UpdatePlatformBilling(
        [FromBody] PlatformBillingSettingsDto request)
    {
        // จด VAT ต้องมีเลขภาษี 13 หลัก ไม่งั้นออกใบกำกับ §86/4 ไม่ได้ (กัน compliance ผิด)
        var taxId = request.PlatformSellerTaxId?.Trim();
        if (request.PlatformIsVatRegistered && (string.IsNullOrEmpty(taxId) || taxId.Length != 13 || !taxId.All(char.IsDigit)))
            return BadRequest(new ApiResponse<PlatformBillingSettingsDto>(false, null,
                "จด VAT ต้องระบุเลขประจำตัวผู้เสียภาษี 13 หลักที่ถูกต้อง"));

        var s = await _db.SiteSettings.FirstOrDefaultAsync();
        if (s == null) { s = new SiteSettings(); _db.SiteSettings.Add(s); }
        s.PlatformSellerName = request.PlatformSellerName?.Trim();
        s.PlatformSellerTaxId = taxId;
        s.PlatformSellerBranchCode = string.IsNullOrWhiteSpace(request.PlatformSellerBranchCode) ? "00000" : request.PlatformSellerBranchCode.Trim();
        s.PlatformSellerAddress = request.PlatformSellerAddress?.Trim();
        s.PlatformSellerPhone = request.PlatformSellerPhone?.Trim();
        s.PlatformSellerEmail = request.PlatformSellerEmail?.Trim();
        s.PlatformIsVatRegistered = request.PlatformIsVatRegistered;
        s.PlatformPriceIncludesVat = request.PlatformPriceIncludesVat;
        s.UpdatedAt = DateTime.UtcNow;
        s.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<PlatformBillingSettingsDto>(true, request, "บันทึกข้อมูลผู้ขาย (แพลตฟอร์ม) สำเร็จ"));
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

        var dir = Path.Combine(_env.WebRootPath, "uploads");
        await using var s = file.OpenReadStream();
        var processed = await _images.ProcessAndSaveAsync(s, file.ContentType, file.FileName, dir, "/uploads", ImageProfile.Logo);
        return Ok(new ApiResponse<object>(true, new { url = processed.RelativeUrl }, "อัพโหลดโลโก้สำเร็จ"));
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

        // Pick a sizing profile based on the caller's stated use.
        // "icon" / "favicon" → square avatar profile; "logo" → Logo; "banner" → wide hero.
        var profile = type switch
        {
            "logo"    => ImageProfile.Logo,
            "icon"    => ImageProfile.Avatar,
            "favicon" => ImageProfile.Avatar,
            "banner"  => ImageProfile.Banner,
            _         => ImageProfile.Generic
        };
        var dir = Path.Combine(_env.WebRootPath, "uploads");
        await using var s = file.OpenReadStream();
        var processed = await _images.ProcessAndSaveAsync(s, file.ContentType, file.FileName, dir, "/uploads", profile);
        return Ok(new ApiResponse<object>(true, new { url = processed.RelativeUrl }, "อัพโหลดสำเร็จ"));
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
            if (!string.IsNullOrEmpty(req.Smtp.Password)) s.SystemSmtpPassword = _secrets.Protect(req.Smtp.Password);
            if (req.Smtp.UseSsl.HasValue) s.SystemSmtpUseSsl = req.Smtp.UseSsl.Value;
        }
        if (req.Microsoft != null)
        {
            if (req.Microsoft.TenantId != null) s.SystemMsTenantId = req.Microsoft.TenantId;
            if (req.Microsoft.ClientId != null) s.SystemMsClientId = req.Microsoft.ClientId;
            if (!string.IsNullOrEmpty(req.Microsoft.ClientSecret)) s.SystemMsClientSecret = _secrets.Protect(req.Microsoft.ClientSecret);
            if (req.Microsoft.SenderUpn != null) s.SystemMsSenderUpn = req.Microsoft.SenderUpn;
        }
        if (req.Gmail != null)
        {
            if (req.Gmail.ClientId != null) s.SystemGmailClientId = req.Gmail.ClientId;
            if (!string.IsNullOrEmpty(req.Gmail.ClientSecret)) s.SystemGmailClientSecret = _secrets.Protect(req.Gmail.ClientSecret);
            if (!string.IsNullOrEmpty(req.Gmail.RefreshToken)) s.SystemGmailRefreshToken = _secrets.Protect(req.Gmail.RefreshToken);
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
            AzureDiMaxConcurrentSubmits: s.AzureDiMaxConcurrentSubmits,
            AzureDiPollIntervalMs: s.AzureDiPollIntervalMs,
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
            OcrMaxRetriesPerScan: s.OcrMaxRetriesPerScan,
            OcrMaxPagesPerScan: s.OcrMaxPagesPerScan)));
    }

    [HttpPut("ocr-config")]
    public async Task<ActionResult<ApiResponse<object>>> UpdateOcrConfig([FromBody] UpdateOcrConfigRequest req)
    {
        var s = await GetOrCreateSiteSettings();
        var hadKeyBefore = !string.IsNullOrEmpty(s.AzureDiApiKey);

        if (req.AzureDiEndpoint != null) s.AzureDiEndpoint = req.AzureDiEndpoint;
        // Defense-in-depth: only overwrite the API key when a non-empty
        // string is supplied. Some serializers bind missing/empty fields
        // to "" rather than null, which would silently wipe the stored
        // secret on every save where the user didn't re-type it.
        if (!string.IsNullOrEmpty(req.AzureDiApiKey)) s.AzureDiApiKey = req.AzureDiApiKey;
        if (req.AzureDiModelId != null) s.AzureDiModelId = req.AzureDiModelId;
        if (req.AzureDiApiVersion != null) s.AzureDiApiVersion = req.AzureDiApiVersion;
        if (req.AzureDiMaxConcurrentSubmits.HasValue)
            s.AzureDiMaxConcurrentSubmits = Math.Max(1, Math.Min(20, req.AzureDiMaxConcurrentSubmits.Value));
        if (req.AzureDiPollIntervalMs.HasValue)
            s.AzureDiPollIntervalMs = Math.Max(100, Math.Min(10000, req.AzureDiPollIntervalMs.Value));
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
        if (req.OcrMaxPagesPerScan.HasValue) s.OcrMaxPagesPerScan = req.OcrMaxPagesPerScan.Value;

        // Track which fields changed (omit secrets — audit log shouldn't contain raw keys)
        var changedFields = new List<string>();
        if (req.AzureDiEndpoint != null) changedFields.Add("AzureDiEndpoint");
        if (req.AzureDiApiKey != null) changedFields.Add("AzureDiApiKey:[redacted]");
        if (req.AzureDiEnabled.HasValue) changedFields.Add($"AzureDiEnabled={req.AzureDiEnabled.Value}");
        if (req.OcrProvider != null) changedFields.Add($"OcrProvider={req.OcrProvider}");

        await _db.SaveChangesAsync();
        await LogAuditAsync(null, "OcrConfigUpdated", string.Join(", ", changedFields));

        // Surface what happened to the API key so the UI can confirm
        // whether the stored secret was changed, kept, or never existed.
        // Eliminates the user-facing confusion where a save with an empty
        // password input looks identical to a save that wiped the key.
        var hasKeyAfter = !string.IsNullOrEmpty(s.AzureDiApiKey);
        var keyAction = !string.IsNullOrEmpty(req.AzureDiApiKey)
            ? "updated"
            : (hasKeyAfter ? "kept-existing" : "absent");
        var msg = keyAction switch
        {
            "updated" => "บันทึกการตั้งค่า OCR สำเร็จ + อัปเดต Azure DI API Key",
            "kept-existing" => "บันทึกการตั้งค่า OCR สำเร็จ (ใช้ Azure DI API Key เดิม)",
            _ => "บันทึกการตั้งค่า OCR สำเร็จ"
        };
        return Ok(new ApiResponse<object>(true, new
        {
            azureDiHasKey = hasKeyAfter,
            azureDiKeyAction = keyAction,
            azureDiEnabled = s.AzureDiEnabled,
            ocrProvider = s.OcrProvider,
        }, msg));
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
    /// Cold-start seeder for system-wide OCR knowledge. Populates the
    /// SystemOcrCategoryMappings, SystemOcrVendorIntelligence, and
    /// SystemOcrAssociationRules tables with hand-curated defaults for
    /// the top ~40 Thai SME vendor brands (fuel / utilities / telecom /
    /// logistics / advertising / travel / banking / insurance / cloud /
    /// office / hardware). After this runs, brand-new tenants get
    /// reasonable predictions on day one — before they've accumulated
    /// any history of their own.
    ///
    /// Idempotent: each row is added only when the same key combination
    /// doesn't already exist. Safe to re-run after schema upgrades.
    /// </summary>
    [HttpPost("ocr-config/seed-knowledge")]
    public async Task<ActionResult<ApiResponse<object>>> SeedSystemOcrKnowledge(
        [FromServices] Services.Implementations.Ocr.SystemOcrKnowledgeSeeder seeder)
    {
        var result = await seeder.SeedAsync();
        await LogAuditAsync(null, "SystemOcrKnowledgeSeeded",
            $"category+{result.CategoryMappings} vendorIntel+{result.VendorIntelligence} associationRules+{result.AssociationRules}");
        var totalAdded = result.CategoryMappings + result.VendorIntelligence + result.AssociationRules;
        var totalExisting = result.ExistingCategoryMappings + result.ExistingVendorIntelligence + result.ExistingAssociationRules;
        var msg = totalAdded > 0
            ? $"Seed สำเร็จ: เพิ่มใหม่ {totalAdded} รายการ (มีอยู่แล้ว {totalExisting})"
            : $"ไม่ได้เพิ่มอะไรใหม่ — ฐานข้อมูลมี seed ครบแล้ว ({totalExisting} รายการ). ระบบ auto-seed ตอน startup เมื่อตารางว่าง — ปกติแล้วครับ";
        return Ok(new ApiResponse<object>(true, new
        {
            categoryMappingsAdded = result.CategoryMappings,
            vendorIntelligenceAdded = result.VendorIntelligence,
            associationRulesAdded = result.AssociationRules,
            categoryMappingsExisting = result.ExistingCategoryMappings,
            vendorIntelligenceExisting = result.ExistingVendorIntelligence,
            associationRulesExisting = result.ExistingAssociationRules,
            totalAdded,
            totalExisting,
            alreadySeeded = totalAdded == 0 && totalExisting > 0
        }, msg));
    }

    /// <summary>
    /// Aggregate per-tenant OCR learning into the system-wide tables.
    /// Only patterns where ≥ minTenants distinct companies have used the
    /// same (vendor, keyword → account) combo are promoted — k-anonymity
    /// preserves single-tenant detail. Companies that opted out via
    /// CompanySettings.ShareTrainingDataAnonymously = false are
    /// excluded entirely.
    ///
    /// Re-run periodically (e.g. nightly) so the system layer reflects
    /// the latest cross-tenant consensus. New tenants benefit immediately.
    /// </summary>
    [HttpPost("ocr-config/aggregate-tenant-knowledge")]
    public async Task<ActionResult<ApiResponse<object>>> AggregateTenantKnowledge(
        [FromServices] Services.Implementations.Ocr.CrossTenantKnowledgeAggregator aggregator,
        [FromQuery] int minTenants = 2)
    {
        var result = await aggregator.AggregateAsync(minTenants);
        await LogAuditAsync(null, "CrossTenantKnowledgeAggregated",
            $"tenants={result.TenantsConsidered} category+{result.CategoryMappingsPromoted} " +
            $"vi+{result.VendorIntelligencePromoted} in {result.Duration.TotalSeconds:F1}s");
        return Ok(new ApiResponse<object>(true, new
        {
            tenantsConsidered = result.TenantsConsidered,
            categoryMappingsPromoted = result.CategoryMappingsPromoted,
            vendorIntelligencePromoted = result.VendorIntelligencePromoted,
            durationSeconds = result.Duration.TotalSeconds,
            minTenants,
        }, $"Aggregate สำเร็จ: cat+{result.CategoryMappingsPromoted} vi+{result.VendorIntelligencePromoted} จาก {result.TenantsConsidered} tenants"));
    }

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
                    var azResult = await azureDi.AnalyzeAsync(fileBytes,
                        file.ContentType ?? "application/octet-stream", s,
                        fileName: file.FileName);
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
            // Use the documentModels listing endpoint instead of /info.
            // The /info endpoint requires a "Cognitive Services Contributor"
            // role; analyze-capable keys typically have only "User" role
            // and 401 here even though they work fine for real scans
            // (the user reported exactly this — basic ping 401 but the
            // full analyze test succeeded). documentModels listing
            // accepts the user role and is the lightest endpoint that
            // proves both endpoint + key are valid for analyze.
            var response = await client.GetAsync($"{endpoint}/documentintelligence/documentModels?api-version={apiVersion}");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                response = await client.GetAsync($"{endpoint}/formrecognizer/documentModels?api-version=2023-07-31");

            s.AzureDiLastTestedAt = DateTime.UtcNow;
            s.AzureDiLastTestStatus = response.IsSuccessStatusCode ? "OK" : $"Error: {response.StatusCode}";
            // Auto-enable on successful test — admins who "test and save"
            // shouldn't also have to flip a separate toggle. Tier 1 in
            // OcrService.ScanAsync requires AzureDiEnabled=true; without
            // this auto-enable the next scan would still go to Tesseract
            // and the user can't tell why.
            bool wasAutoEnabled = false;
            if (response.IsSuccessStatusCode && !s.AzureDiEnabled)
            {
                s.AzureDiEnabled = true;
                wasAutoEnabled = true;
            }
            await _db.SaveChangesAsync();

            var msg = response.IsSuccessStatusCode
                ? (wasAutoEnabled ? "เชื่อมต่อ Azure DI สำเร็จ — เปิดใช้งานอัตโนมัติแล้ว (Tier 1 พร้อมใช้งานในการสแกนถัดไป)"
                                  : "เชื่อมต่อ Azure DI สำเร็จ")
                : $"ไม่สามารถเชื่อมต่อได้: {response.StatusCode}";
            return Ok(new ApiResponse<object>(response.IsSuccessStatusCode,
                new { StatusCode = (int)response.StatusCode, AutoEnabled = wasAutoEnabled, Enabled = s.AzureDiEnabled },
                msg));
        }
        catch (Exception ex)
        {
            s.AzureDiLastTestedAt = DateTime.UtcNow;
            s.AzureDiLastTestStatus = $"Error: {ex.Message}";
            await _db.SaveChangesAsync();
            return Ok(new ApiResponse<object>(false, null, $"เชื่อมต่อไม่สำเร็จ: {ex.Message}"));
        }
    }

    /// <summary>
    /// Full end-to-end Azure DI test — generates a small sample image and
    /// runs it through the exact same AnalyzeAsync pipeline production
    /// scans use. Catches issues the lightweight /test-azure ping can't
    /// surface, like:
    ///   • model + features incompatibility (HTTP 400 keyValuePairs)
    ///   • api-version mismatches
    ///   • multipart / content-type problems
    ///   • timeout / network reachability beyond the info endpoint
    /// Charges ~1 page against Azure quota when it succeeds. Returns the
    /// request plan (model, locale, features, query string) + the raw
    /// extraction so admin can verify exact settings.
    /// </summary>
    [HttpPost("ocr-config/test-azure-full")]
    public async Task<ActionResult<ApiResponse<object>>> TestAzureDiFull(
        [FromServices] Services.Implementations.Ocr.AzureDocumentIntelligenceService azureDi)
    {
        var s = await GetOrCreateSiteSettings();
        if (string.IsNullOrEmpty(s.AzureDiEndpoint) || string.IsNullOrEmpty(s.AzureDiApiKey))
            return BadRequest(new ApiResponse<object>(false, null, "กรุณากรอก Azure DI Endpoint และ API Key ก่อน"));
        if (!s.AzureDiEnabled)
            return BadRequest(new ApiResponse<object>(false, null, "Azure DI Enabled toggle ปิดอยู่ — เปิดก่อนทดสอบ"));

        // Generate a small synthetic receipt-like image. 400×600 px white
        // with a few black horizontal bars to look document-shaped to
        // Azure's preflight (it rejects truly blank inputs as "no
        // recognizable content"). We don't actually need the OCR to read
        // anything — the goal is to validate that the analyze endpoint
        // accepts our model + features + locale combination end-to-end.
        byte[] sampleBytes;
        try
        {
            // Direct pixel access — avoids the SixLabors.ImageSharp.Drawing
            // package (we don't depend on it) which is where Fill / DrawText
            // live. The image constructor defaults to transparent black; we
            // set every pixel to white, then overlay black horizontal bars
            // at receipt-layout positions so Azure DI's document detector
            // engages.
            const int W = 400, H = 600;
            var white = new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 255, 255, 255);
            var black = new SixLabors.ImageSharp.PixelFormats.Rgba32(0, 0, 0, 255);
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(W, H);
            for (int y = 0; y < H; y++)
            {
                bool isBar = (y >= 80 && y <= 90) || (y >= 200 && y <= 210)
                          || (y >= 320 && y <= 330) || (y >= 500 && y <= 515);
                for (int x = 0; x < W; x++)
                {
                    img[x, y] = (isBar && x >= 40 && x < 360) ? black : white;
                }
            }
            using var ms = new MemoryStream();
            // Explicit encoder avoids needing the SaveAsPng extension
            // method (which lives behind a `using SixLabors.ImageSharp;`
            // directive this controller doesn't pull in to keep its
            // namespace surface tight).
            img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            sampleBytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            return Ok(new ApiResponse<object>(false, null, $"สร้างภาพทดสอบไม่สำเร็จ: {ex.Message}"));
        }

        // Build the plan to surface what Azure was asked to do — same
        // planner the real cascade uses.
        var plan = Services.Implementations.Ocr.AzureDiRequestPlanner.Build(
            sampleBytes, "image/png", "test-sample.png", s);
        var apiVersion = string.IsNullOrEmpty(s.AzureDiApiVersion) ? "2024-11-30" : s.AzureDiApiVersion;
        var queryString = Services.Implementations.Ocr.AzureDiRequestPlanner.BuildQueryString(plan, apiVersion);

        var startedAt = DateTime.UtcNow;
        try
        {
            var result = await azureDi.AnalyzeAsync(sampleBytes, "image/png", s, "test-sample.png");
            var elapsedMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            if (result == null)
            {
                return Ok(new ApiResponse<object>(false, new
                {
                    plan.ModelId, plan.Locale, plan.Features, QueryString = queryString,
                    Reasons = plan.Reasons, ElapsedMs = elapsedMs,
                }, "AnalyzeAsync returned null — ตรวจสอบว่า toggle/endpoint/key ถูกต้อง"));
            }

            // AnalyzeAsync swallows HTTP failures into a Success=false result
            // (e.g. 401 Unauthorized = wrong API key → result.Success=false +
            // result.ErrorMessage = "HTTP 401: ..."). Previously the test
            // reported "success" on these because it only checked null — fix
            // is to inspect the Success flag explicitly.
            if (!result.Success)
            {
                s.AzureDiLastTestedAt = DateTime.UtcNow;
                s.AzureDiLastTestStatus = $"Full Error: {result.ErrorMessage}";
                await _db.SaveChangesAsync();
                return Ok(new ApiResponse<object>(false, new
                {
                    plan.ModelId, plan.Locale, plan.Features, QueryString = queryString,
                    Reasons = plan.Reasons,
                    ElapsedMs = elapsedMs,
                    Error = result.ErrorMessage,
                    Warnings = result.Warnings,
                }, $"ทดสอบสแกนจริงล้มเหลว: {result.ErrorMessage}"));
            }

            s.AzureDiLastTestedAt = DateTime.UtcNow;
            s.AzureDiLastTestStatus = $"Full OK ({elapsedMs}ms)";
            await _db.SaveChangesAsync();

            return Ok(new ApiResponse<object>(true, new
            {
                plan.ModelId, plan.Locale, plan.Features, QueryString = queryString,
                Reasons = plan.Reasons,
                ElapsedMs = elapsedMs,
                result.OverallConfidence,
                Warnings = result.Warnings,
                TextLength = result.RawText?.Length ?? 0,
                FoundDocument = result.MultiDocumentCount > 0,
            }, $"ทดสอบสแกนจริงสำเร็จ — Azure DI ตอบสนองภายใน {elapsedMs}ms (model={plan.ModelId})"));
        }
        catch (Exception ex)
        {
            var elapsedMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            s.AzureDiLastTestedAt = DateTime.UtcNow;
            s.AzureDiLastTestStatus = $"Full Error: {ex.Message}";
            await _db.SaveChangesAsync();
            // Surface the FULL exception text — that's where the
            // keyValuePairs-class errors live (Azure returns them in the
            // HTTP 400 response body, our service propagates them).
            return Ok(new ApiResponse<object>(false, new
            {
                plan.ModelId, plan.Locale, plan.Features, QueryString = queryString,
                Reasons = plan.Reasons,
                ElapsedMs = elapsedMs,
                Error = ex.Message,
                ExceptionType = ex.GetType().Name,
            }, $"ทดสอบสแกนจริงล้มเหลว: {ex.Message}"));
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

    // ===================================================================
    // Master Chart of Accounts — admin-editable system-wide COA template.
    //
    // Rows are partitioned into SCOPES via the businessType / industryType
    // query params (mutually exclusive):
    //   • neither set            → common block (codes 1/2/4/5)
    //   • businessType=<enum>    → equity (code 3) block for that type
    //   • industryType=<enum>    → industry-specific block for that type
    // Every endpoint below operates on exactly one scope.
    // ===================================================================

    /// <summary>Validate the businessType/industryType scope pair.
    /// Returns an error message, or null when the scope is valid.</summary>
    private static string? ValidateCoaScope(BusinessType? businessType, IndustryType? industryType)
    {
        if (businessType.HasValue && industryType.HasValue)
            return "ระบุได้เพียง businessType หรือ industryType อย่างใดอย่างหนึ่ง";
        if (industryType == IndustryType.General)
            return "อุตสาหกรรม 'ทั่วไป' ไม่มีผังบัญชีเฉพาะ — ใช้ขอบเขตส่วนกลางแทน";
        return null;
    }

    /// <summary>List the master Chart of Accounts template for one scope.</summary>
    [HttpGet("coa-template")]
    public async Task<ActionResult<ApiResponse<List<SystemAccountTemplateDto>>>> GetCoaTemplate(
        [FromQuery] BusinessType? businessType = null, [FromQuery] IndustryType? industryType = null)
    {
        var scopeErr = ValidateCoaScope(businessType, industryType);
        if (scopeErr != null)
            return BadRequest(new ApiResponse<List<SystemAccountTemplateDto>>(false, null, scopeErr));

        var rows = await _db.SystemAccountTemplates
            .AsNoTracking()
            .Where(t => !t.IsDeleted && t.BusinessType == businessType && t.IndustryType == industryType)
            .OrderBy(t => t.AccountCode)
            .Select(t => new SystemAccountTemplateDto(
                t.AccountCode, t.AccountNameTh, t.AccountNameEn,
                t.AccountType.ToString(), t.Level, t.IsActive))
            .ToListAsync();
        return Ok(new ApiResponse<List<SystemAccountTemplateDto>>(true, rows));
    }

    /// <summary>Bulk-replace the rows of ONE scope with the supplied list.
    /// Only the targeted scope is touched — other scopes are untouched.
    /// An empty list clears the scope (seeding then falls back to built-in).</summary>
    [HttpPut("coa-template")]
    public async Task<ActionResult<ApiResponse<object>>> SaveCoaTemplate(
        [FromBody] List<SystemAccountTemplateDto> rows,
        [FromQuery] BusinessType? businessType = null, [FromQuery] IndustryType? industryType = null)
    {
        var scopeErr = ValidateCoaScope(businessType, industryType);
        if (scopeErr != null)
            return BadRequest(new ApiResponse<object>(false, null, scopeErr));
        rows ??= new List<SystemAccountTemplateDto>();

        // Validate codes unique within the scope + types parseable.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.AccountCode))
                return BadRequest(new ApiResponse<object>(false, null, "พบรายการที่ไม่มีรหัสบัญชี"));
            if (!seen.Add(r.AccountCode))
                return BadRequest(new ApiResponse<object>(false, null, $"รหัสบัญชีซ้ำ: {r.AccountCode}"));
            if (!Enum.TryParse<AccountType>(r.AccountType, out _))
                return BadRequest(new ApiResponse<object>(false, null, $"ประเภทบัญชีไม่ถูกต้อง: {r.AccountType} ({r.AccountCode})"));
        }

        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        // Replace-the-scope is wrapped in a transaction so a failure mid-save
        // never leaves the scope wiped (which would silently fall back to the
        // built-in template for every company seeded afterwards).
        await using var tx = await _db.Database.BeginTransactionAsync();
        await _db.SystemAccountTemplates
            .Where(t => t.BusinessType == businessType && t.IndustryType == industryType)
            .ExecuteDeleteAsync();
        foreach (var r in rows)
        {
            _db.SystemAccountTemplates.Add(new SystemAccountTemplate
            {
                AccountCode = r.AccountCode.Trim(),
                AccountNameTh = r.AccountNameTh?.Trim() ?? r.AccountCode,
                AccountNameEn = string.IsNullOrWhiteSpace(r.AccountNameEn) ? null : r.AccountNameEn.Trim(),
                AccountType = Enum.Parse<AccountType>(r.AccountType),
                Level = r.Level is >= 1 and <= 4 ? r.Level : 1,
                IsActive = r.IsActive,
                BusinessType = businessType,
                IndustryType = industryType,
                CreatedBy = userId,
            });
        }
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return Ok(new ApiResponse<object>(true, new { count = rows.Count },
            $"บันทึกผังบัญชีต้นแบบ {rows.Count} รายการสำเร็จ"));
    }

    /// <summary>Initialise ONE scope from its built-in code-based block,
    /// giving the admin an editable starting point.</summary>
    [HttpPost("coa-template/seed-builtin")]
    public async Task<ActionResult<ApiResponse<object>>> SeedCoaTemplateFromBuiltin(
        [FromQuery] BusinessType? businessType = null, [FromQuery] IndustryType? industryType = null)
    {
        var scopeErr = ValidateCoaScope(businessType, industryType);
        if (scopeErr != null)
            return BadRequest(new ApiResponse<object>(false, null, scopeErr));

        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var builtin = businessType.HasValue
            ? ChartOfAccountTemplates.GetEquityForBusinessType(businessType.Value)
            : industryType.HasValue
                ? ChartOfAccountTemplates.GetIndustryAccounts(industryType.Value)
                : ChartOfAccountTemplates.GetCommonAccounts();

        await using var tx = await _db.Database.BeginTransactionAsync();
        await _db.SystemAccountTemplates
            .Where(t => t.BusinessType == businessType && t.IndustryType == industryType)
            .ExecuteDeleteAsync();
        foreach (var t in builtin)
        {
            _db.SystemAccountTemplates.Add(new SystemAccountTemplate
            {
                AccountCode = t.Code,
                AccountNameTh = t.NameTh,
                AccountNameEn = t.NameEn,
                AccountType = t.Type,
                Level = t.Level,
                IsActive = true,
                BusinessType = businessType,
                IndustryType = industryType,
                CreatedBy = userId,
            });
        }
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return Ok(new ApiResponse<object>(true, new { count = builtin.Count },
            $"นำเข้าผังบัญชีมาตรฐาน {builtin.Count} รายการจากระบบสำเร็จ"));
    }

    /// <summary>List the editable scopes (common + each business type +
    /// each industry) with Thai labels and how many master rows each has.</summary>
    [HttpGet("coa-template/scopes")]
    public async Task<ActionResult<ApiResponse<object>>> GetCoaTemplateScopes()
    {
        var counts = await _db.SystemAccountTemplates
            .AsNoTracking()
            .Where(t => !t.IsDeleted)
            .GroupBy(t => new { t.BusinessType, t.IndustryType })
            .Select(g => new { g.Key.BusinessType, g.Key.IndustryType, Count = g.Count() })
            .ToListAsync();

        int CommonCount() => counts.FirstOrDefault(c => c.BusinessType == null && c.IndustryType == null)?.Count ?? 0;
        int BizCount(BusinessType b) => counts.FirstOrDefault(c => c.BusinessType == b)?.Count ?? 0;
        int IndCount(IndustryType i) => counts.FirstOrDefault(c => c.IndustryType == i)?.Count ?? 0;

        var businessTypes = ChartOfAccountTemplates.GetAllBusinessTypes()
            .Where(b => b.Type != BusinessType.Other)
            .Select(b => new { b.Type, name = b.NameTh, equityLabel = b.EquityLabel, count = BizCount(b.Type) });
        var industryTypes = ChartOfAccountTemplates.GetAllIndustryTypes()
            .Where(i => i.Type is not (IndustryType.General or IndustryType.Other))
            .Select(i => new { i.Type, name = i.NameTh, icon = i.Icon, count = IndCount(i.Type) });

        return Ok(new ApiResponse<object>(true, new
        {
            common = new { count = CommonCount() },
            businessTypes,
            industryTypes,
        }));
    }

    // ===================================================================
    // Audit Log — cross-company / system-wide activity feed
    // ===================================================================

    /// <summary>Paged system-wide audit log. Optional filters: entityType,
    /// action, free-text (matches user email / entity id).</summary>
    [HttpGet("audit-logs")]
    public async Task<ActionResult<ApiResponse<object>>> GetAuditLogs(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] string? entityType = null, [FromQuery] AuditAction? action = null,
        [FromQuery] string? q = null,
        [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);
        var query = _db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(a => a.EntityType == entityType);
        if (action.HasValue) query = query.Where(a => a.Action == action.Value);
        if (fromDate.HasValue) query = query.Where(a => a.Timestamp >= fromDate.Value);
        if (toDate.HasValue) query = query.Where(a => a.Timestamp <= toDate.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(a => (a.UserEmail != null && a.UserEmail.Contains(term))
                || (a.EntityId != null && a.EntityId.Contains(term)));
        }
        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new {
                a.Id, a.CompanyId, a.UserEmail, Action = a.Action.ToString(),
                a.EntityType, a.EntityId, a.IpAddress, a.Timestamp,
                a.OldValues, a.NewValues })
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, new {
            items, total, page, pageSize,
            totalPages = (int)Math.Ceiling(total / (double)pageSize) }));
    }

    // ===================================================================
    // Error Log — system exception feed for debugging
    // ===================================================================

    /// <summary>Paged system error log, newest first. Optional filter by
    /// HTTP status code and free-text on path / message / exception type.</summary>
    [HttpGet("error-logs")]
    public async Task<ActionResult<ApiResponse<object>>> GetErrorLogs(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] int? statusCode = null, [FromQuery] string? q = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);
        var query = _db.ErrorLogs.AsNoTracking().AsQueryable();
        if (statusCode.HasValue) query = query.Where(e => e.StatusCode == statusCode.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(e => (e.RequestPath != null && e.RequestPath.Contains(term))
                || e.Message.Contains(term) || e.ExceptionType.Contains(term));
        }
        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(e => e.Timestamp)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, new {
            items, total, page, pageSize,
            totalPages = (int)Math.Ceiling(total / (double)pageSize) }));
    }

    /// <summary>Delete error-log rows older than the given number of days
    /// (housekeeping). Default 90.</summary>
    [HttpDelete("error-logs/purge")]
    public async Task<ActionResult<ApiResponse<object>>> PurgeErrorLogs([FromQuery] int olderThanDays = 90)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, olderThanDays));
        var deleted = await _db.ErrorLogs.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync();
        return Ok(new ApiResponse<object>(true, new { deleted },
            $"ลบ error log เก่ากว่า {olderThanDays} วัน — {deleted} รายการ"));
    }
}

// ===== Admin-specific DTOs =====

public record UpdateCustomerStatusRequest(CompanyStatus Status);
public record UpdateUserStatusRequest(UserStatus Status);
public record ToggleAdminRequest(bool IsAdmin);

public record OcrConfigResponse(
    string? AzureDiEndpoint, bool AzureDiHasKey, string? AzureDiModelId, string? AzureDiApiVersion,
    int AzureDiMaxConcurrentSubmits, int AzureDiPollIntervalMs,
    bool AzureDiEnabled, DateTime? AzureDiLastTestedAt, string? AzureDiLastTestStatus,
    string? OcrProvider, string? OcrLocalServiceUrl,
    decimal OcrAutoCreateThreshold,
    int OcrFreePagesTrial, int OcrFreePagesBasic, int OcrFreePagesPro, int OcrFreePagesEnterprise,
    decimal OcrCreditPricePerPage, int OcrCreditMinPurchase,
    decimal OcrGatewayMaxPenalty, decimal OcrGatewayMathTolerance,
    decimal OcrGatewayTaxIdPenalty, decimal OcrGatewayMathPenalty,
    decimal OcrGatewayDatePenalty, decimal OcrGatewayVatRatePenalty,
    decimal OcrGatewayLowConfidencePenalty, int OcrMaxRetriesPerScan,
    int? OcrMaxPagesPerScan = null);

public record UpdateOcrConfigRequest(
    string? AzureDiEndpoint = null, string? AzureDiApiKey = null,
    string? AzureDiModelId = null, string? AzureDiApiVersion = null,
    int? AzureDiMaxConcurrentSubmits = null, int? AzureDiPollIntervalMs = null,
    bool? AzureDiEnabled = null,
    string? OcrProvider = null, string? OcrLocalServiceUrl = null,
    decimal? OcrAutoCreateThreshold = null,
    int? OcrFreePagesTrial = null, int? OcrFreePagesBasic = null,
    int? OcrFreePagesPro = null, int? OcrFreePagesEnterprise = null,
    decimal? OcrCreditPricePerPage = null, int? OcrCreditMinPurchase = null,
    decimal? OcrGatewayMaxPenalty = null, decimal? OcrGatewayMathTolerance = null,
    decimal? OcrGatewayTaxIdPenalty = null, decimal? OcrGatewayMathPenalty = null,
    decimal? OcrGatewayDatePenalty = null, decimal? OcrGatewayVatRatePenalty = null,
    decimal? OcrGatewayLowConfidencePenalty = null, int? OcrMaxRetriesPerScan = null,
    int? OcrMaxPagesPerScan = null);

public record ReviewOcrCreditRequest(bool Approve, string? Notes = null);
public record GrantOcrBonusRequest(int Pages, DateTime? ExpiresAt = null, string? Reason = null);

public record SystemAccountTemplateDto(
    string AccountCode, string AccountNameTh, string? AccountNameEn,
    string AccountType, int Level, bool IsActive);
