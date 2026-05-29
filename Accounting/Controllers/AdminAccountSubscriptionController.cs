using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// SystemAdmin-only endpoints for managing AccountSubscriptions (user-level
/// licenses). Sibling to the per-company subscription endpoints on
/// AdminController. Without these, support staff can see "company X" needs
/// renewal but can't see / extend / suspend the parent account plan covering
/// it — a regression once we shipped Account Plans.
/// </summary>
[ApiController]
[Route("api/admin/account-subscriptions")]
[Authorize]
public class AdminAccountSubscriptionController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ISubscriptionService _subSvc;
    public AdminAccountSubscriptionController(AccountingDbContext db, ISubscriptionService subSvc)
    {
        _db = db; _subSvc = subSvc;
    }

    private async Task<bool> IsSystemAdminAsync()
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == uid);
        return u?.IsSystemAdmin == true;
    }

    public record AccountSubAdminDto(
        Guid Id, Guid OwnerUserId, string OwnerName, string OwnerEmail,
        Guid PlanTemplateId, string PlanName,
        SubscriptionStatus Status, DateTime StartDate, DateTime EndDate,
        int MaxCompanies, int CompaniesUsed,
        int MaxUsersPerCompany,
        int MaxDocumentsPerMonth, int MaxJournalEntriesPerMonth,
        long MaxStorageBytes, int MaxOcrPagesPerMonth,
        decimal MonthlyPrice, decimal AnnualPrice,
        BillingCycle BillingCycle, int GracePeriodDays,
        DateTime? LastPaidAt);

    /// <summary>List all account plans across users. Filter by status / search
    /// by owner email + name + plan name.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<AccountSubAdminDto>>>> List(
        [FromQuery] SubscriptionStatus? status = null, [FromQuery] string? search = null,
        [FromQuery] bool includeInactive = false)
    {
        if (!await IsSystemAdminAsync()) return Forbid();

        var q = _db.AccountSubscriptions
            .Include(a => a.Owner)
            .Include(a => a.PlanTemplate)
            .Where(a => !a.IsDeleted);
        if (!includeInactive)
            q = q.Where(a => a.Status != SubscriptionStatus.Cancelled && a.Status != SubscriptionStatus.Expired);
        if (status.HasValue) q = q.Where(a => a.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(a => a.Owner.Email.Contains(search)
                || a.Owner.FullName.Contains(search)
                || a.PlanTemplate.Name.Contains(search));

        var rows = await q.OrderByDescending(a => a.CreatedAt).ToListAsync();
        // Per-row used-companies count — done as a follow-up query so the
        // initial list query stays index-friendly.
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await _db.Subscriptions
            .Where(s => s.AccountSubscriptionId != null && ids.Contains(s.AccountSubscriptionId.Value) && !s.IsDeleted)
            .GroupBy(s => s.AccountSubscriptionId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count);

        var dto = rows.Select(a => new AccountSubAdminDto(
            a.Id, a.OwnerUserId, a.Owner.FullName, a.Owner.Email,
            a.PlanTemplateId, a.PlanTemplate.Name, a.Status, a.StartDate, a.EndDate,
            a.MaxCompanies, counts.GetValueOrDefault(a.Id, 0),
            a.MaxUsersPerCompany,
            a.MaxDocumentsPerMonth, a.MaxJournalEntriesPerMonth,
            a.MaxStorageBytes, a.MaxOcrPagesPerMonth,
            a.MonthlyPrice, a.AnnualPrice, a.BillingCycle, a.GracePeriodDays,
            a.LastPaidAt)).ToList();
        return Ok(new ApiResponse<List<AccountSubAdminDto>>(true, dto));
    }

    /// <summary>Detail with attached companies + aggregate usage.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Detail(Guid id)
    {
        if (!await IsSystemAdminAsync()) return Forbid();

        var a = await _db.AccountSubscriptions
            .Include(x => x.Owner)
            .Include(x => x.PlanTemplate)
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (a == null) return NotFound();

        var attached = await _db.Subscriptions
            .Where(s => s.AccountSubscriptionId == id && !s.IsDeleted)
            .Include(s => s.Company)
            .Select(s => new
            {
                s.CompanyId, CompanyName = s.Company.Name, s.Company.TaxId, s.Status,
                s.CurrentMonthDocuments, s.CurrentMonthJournalEntries,
                s.CurrentStorageUsed, s.CurrentMonthOcrPages,
            })
            .ToListAsync();

        var agg = await _subSvc.GetAggregateUsageAsync(id);

        return Ok(new ApiResponse<object>(true, new
        {
            id = a.Id, ownerUserId = a.OwnerUserId,
            ownerName = a.Owner.FullName, ownerEmail = a.Owner.Email, ownerPhone = a.Owner.Phone,
            planTemplateId = a.PlanTemplateId, planName = a.PlanTemplate.Name,
            status = a.Status, startDate = a.StartDate, endDate = a.EndDate,
            maxCompanies = a.MaxCompanies, companiesUsed = attached.Count,
            maxUsersPerCompany = a.MaxUsersPerCompany,
            maxDocumentsPerMonth = a.MaxDocumentsPerMonth,
            maxJournalEntriesPerMonth = a.MaxJournalEntriesPerMonth,
            maxStorageBytes = a.MaxStorageBytes,
            maxOcrPagesPerMonth = a.MaxOcrPagesPerMonth,
            azureOcrPagesPerMonth = a.AzureOcrPagesPerMonth,
            localOcrPagesPerMonth = a.LocalOcrPagesPerMonth,
            enabledFeatures = (long)a.EnabledFeatures,
            monthlyPrice = a.MonthlyPrice, annualPrice = a.AnnualPrice,
            billingCycle = a.BillingCycle, gracePeriodDays = a.GracePeriodDays,
            lastPaidAt = a.LastPaidAt,
            attachedCompanies = attached,
            usage = agg,
        }));
    }

    public record ChangeStatusRequest(SubscriptionStatus Status, string? Reason);
    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult<ApiResponse<string>>> ChangeStatus(Guid id, [FromBody] ChangeStatusRequest req)
    {
        if (!await IsSystemAdminAsync()) return Forbid();
        var a = await _db.AccountSubscriptions.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (a == null) return NotFound();
        a.Status = req.Status;
        a.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, null, $"เปลี่ยนสถานะเป็น {req.Status}"));
    }

    public record ExtendDatesRequest(DateTime EndDate);
    /// <summary>Extend EndDate without going through the slip approval flow —
    /// used by support to honor goodwill credits, enterprise contracts, etc.</summary>
    [HttpPut("{id:guid}/extend")]
    public async Task<ActionResult<ApiResponse<string>>> Extend(Guid id, [FromBody] ExtendDatesRequest req)
    {
        if (!await IsSystemAdminAsync()) return Forbid();
        var a = await _db.AccountSubscriptions.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (a == null) return NotFound();
        if (req.EndDate <= a.StartDate)
            return BadRequest(new ApiResponse<string>(false, null, "วันสิ้นสุดต้องมากกว่าวันเริ่ม"));
        a.EndDate = req.EndDate;
        // Returning to Active makes sense when extending past a recent expiry.
        if (a.Status == SubscriptionStatus.Expired || a.Status == SubscriptionStatus.PastDue)
            a.Status = SubscriptionStatus.Active;
        a.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, null, "ขยายอายุ Account Plan สำเร็จ"));
    }

    public record ChangeLimitsRequest(
        int? MaxCompanies, int? MaxUsersPerCompany,
        int? MaxDocumentsPerMonth, int? MaxJournalEntriesPerMonth,
        long? MaxStorageBytes, int? MaxOcrPagesPerMonth,
        int? AzureOcrPagesPerMonth, int? LocalOcrPagesPerMonth);
    /// <summary>Override plan limits for an individual account — typical for
    /// negotiated enterprise contracts. Only sets the fields actually
    /// provided; null leaves the existing value alone.</summary>
    [HttpPut("{id:guid}/limits")]
    public async Task<ActionResult<ApiResponse<string>>> ChangeLimits(Guid id, [FromBody] ChangeLimitsRequest req)
    {
        if (!await IsSystemAdminAsync()) return Forbid();
        var a = await _db.AccountSubscriptions.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (a == null) return NotFound();
        if (req.MaxCompanies.HasValue) a.MaxCompanies = Math.Max(1, req.MaxCompanies.Value);
        if (req.MaxUsersPerCompany.HasValue) a.MaxUsersPerCompany = Math.Max(1, req.MaxUsersPerCompany.Value);
        if (req.MaxDocumentsPerMonth.HasValue) a.MaxDocumentsPerMonth = Math.Max(0, req.MaxDocumentsPerMonth.Value);
        if (req.MaxJournalEntriesPerMonth.HasValue) a.MaxJournalEntriesPerMonth = Math.Max(0, req.MaxJournalEntriesPerMonth.Value);
        if (req.MaxStorageBytes.HasValue) a.MaxStorageBytes = Math.Max(0, req.MaxStorageBytes.Value);
        if (req.MaxOcrPagesPerMonth.HasValue) a.MaxOcrPagesPerMonth = Math.Max(0, req.MaxOcrPagesPerMonth.Value);
        if (req.AzureOcrPagesPerMonth.HasValue) a.AzureOcrPagesPerMonth = req.AzureOcrPagesPerMonth.Value;
        if (req.LocalOcrPagesPerMonth.HasValue) a.LocalOcrPagesPerMonth = req.LocalOcrPagesPerMonth.Value;
        a.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, null, "บันทึก limits ใหม่แล้ว"));
    }

    /// <summary>Admin-side detach — sets a Company's Subscription.AccountSubscriptionId
    /// back to null so the company stops riding the parent License. Used when
    /// support handles "we're selling Company X to a different owner" or
    /// "Holding wants separate billing for B5". Different from the user-side
    /// /api/account-subscription/detach which requires Owner role of the
    /// company — admin can detach any company.</summary>
    [HttpPost("/api/admin/companies/{companyId:guid}/detach-from-account-plan")]
    public async Task<ActionResult<ApiResponse<string>>> AdminDetach(Guid companyId)
    {
        if (!await IsSystemAdminAsync()) return Forbid();
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted);
        if (sub == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบ Subscription ของบริษัท"));
        if (sub.AccountSubscriptionId == null)
            return Ok(new ApiResponse<string>(true, null, "บริษัทนี้ไม่ได้ผูกกับ License อยู่แล้ว"));
        var oldId = sub.AccountSubscriptionId.Value;
        sub.AccountSubscriptionId = null;
        sub.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        sub.UpdatedAt = DateTime.UtcNow;

        // Audit row at both layers — easier to spot in /admin/account-subscriptions
        // history when support is investigating "where did this company go".
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            AccountSubscriptionId = oldId,
            Action = "DetachedFromAccountPlan",
            Notes = $"Admin detached company {companyId} from Account Plan {oldId}",
            PerformedBy = JwtHelper.GetUserIdFromClaims(User).ToString()
        });
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, null, "ถอดออกจาก License สำเร็จ"));
    }

    public record AdminAttachRequest(Guid AccountSubscriptionId);
    /// <summary>Admin-side attach — binds a Company's Subscription to an
    /// existing AccountSubscription (User License). The customers.html detail
    /// page calls this from the "🔗 ผูกเข้า License" action. Enforces
    /// MaxCompanies slot count + active license check.</summary>
    [HttpPost("/api/admin/companies/{companyId:guid}/attach-to-account-plan")]
    public async Task<ActionResult<ApiResponse<string>>> AdminAttach(Guid companyId, [FromBody] AdminAttachRequest req)
    {
        if (!await IsSystemAdminAsync()) return Forbid();

        var ap = await _db.AccountSubscriptions.FirstOrDefaultAsync(a => a.Id == req.AccountSubscriptionId && !a.IsDeleted);
        if (ap == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบ License ที่ระบุ"));
        if (ap.Status != SubscriptionStatus.Trial && ap.Status != SubscriptionStatus.Active && ap.Status != SubscriptionStatus.PastDue)
            return BadRequest(new ApiResponse<string>(false, null, $"License นี้สถานะ {ap.Status} ไม่สามารถผูกบริษัทเพิ่มได้"));

        var used = await _db.Subscriptions.CountAsync(s => s.AccountSubscriptionId == ap.Id && !s.IsDeleted);
        if (used >= ap.MaxCompanies)
            return BadRequest(new ApiResponse<string>(false, null, $"License ใช้ครบ {ap.MaxCompanies} บริษัทแล้ว — ต้องเพิ่ม MaxCompanies ก่อน"));

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted);
        if (sub == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบ Subscription ของบริษัท"));
        if (sub.AccountSubscriptionId == ap.Id)
            return Ok(new ApiResponse<string>(true, null, "บริษัทผูกกับ License นี้อยู่แล้ว"));
        if (sub.AccountSubscriptionId != null)
            return BadRequest(new ApiResponse<string>(false, null, "บริษัทผูกกับ License อื่นอยู่ — ต้องถอดก่อน"));

        sub.AccountSubscriptionId = ap.Id;
        sub.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        sub.UpdatedAt = DateTime.UtcNow;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            AccountSubscriptionId = ap.Id,
            Action = "AttachedToAccountPlan",
            Notes = $"Admin attached company {companyId} to Account Plan {ap.Id}",
            PerformedBy = JwtHelper.GetUserIdFromClaims(User).ToString()
        });
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, null, "ผูกบริษัทเข้า License สำเร็จ"));
    }

    /// <summary>History feed for an Account Plan — pulls SubscriptionHistory
    /// rows where AccountSubscriptionId matches, plus the cross-cascade ones
    /// where the row's company-level Subscription belongs to this account.
    /// Surfaces "slip-approved → cascaded" + "reminder sent" + status flips so
    /// support can answer "why did this account auto-extend".</summary>
    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<ApiResponse<object>>> History(Guid id, [FromQuery] int take = 100)
    {
        if (!await IsSystemAdminAsync()) return Forbid();

        var rows = await _db.SubscriptionHistories.AsNoTracking()
            .Where(h => h.AccountSubscriptionId == id)
            .OrderByDescending(h => h.CreatedAt)
            .Take(Math.Clamp(take, 10, 500))
            .Select(h => new
            {
                h.Id, h.SubscriptionId, h.AccountSubscriptionId,
                h.Action, fromStatus = h.FromStatus, toStatus = h.ToStatus,
                h.Notes, h.PerformedBy, h.CreatedAt
            })
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, new { items = rows }));
    }

    public record CreateAccountSubRequest(Guid OwnerUserId, Guid PlanTemplateId, int? OverrideMaxCompanies, DateTime? CustomEndDate);
    /// <summary>Provision a plan for a user — used when sales closes an
    /// enterprise deal outside the self-serve trial flow.</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<Guid>>> Create([FromBody] CreateAccountSubRequest req)
    {
        if (!await IsSystemAdminAsync()) return Forbid();

        var exists = await _db.AccountSubscriptions.AnyAsync(a =>
            a.OwnerUserId == req.OwnerUserId && !a.IsDeleted
            && (a.Status == SubscriptionStatus.Trial || a.Status == SubscriptionStatus.Active
                || a.Status == SubscriptionStatus.PastDue || a.Status == SubscriptionStatus.Suspended));
        if (exists) return BadRequest(new ApiResponse<Guid>(false, default, "ผู้ใช้นี้มี Account Plan ที่ active อยู่แล้ว"));

        var tpl = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Id == req.PlanTemplateId && p.IsActive);
        if (tpl == null) return BadRequest(new ApiResponse<Guid>(false, default, "ไม่พบ plan template"));

        var a = new AccountSubscription
        {
            OwnerUserId = req.OwnerUserId,
            PlanTemplateId = tpl.Id,
            Status = SubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            EndDate = req.CustomEndDate ?? DateTime.UtcNow.AddMonths(1),
            MaxCompanies = req.OverrideMaxCompanies ?? Math.Max(1, tpl.MaxCompanies),
            MaxUsersPerCompany = tpl.MaxUsers,
            MaxDocumentsPerMonth = tpl.MaxDocumentsPerMonth,
            MaxJournalEntriesPerMonth = tpl.MaxJournalEntriesPerMonth,
            MaxStorageBytes = tpl.MaxStorageBytes,
            MaxOcrPagesPerMonth = tpl.MaxOcrPagesPerMonth,
            AzureOcrPagesPerMonth = tpl.AzureOcrPagesPerMonth,
            LocalOcrPagesPerMonth = tpl.LocalOcrPagesPerMonth,
            EnabledFeatures = tpl.EnabledFeatures,
            MonthlyPrice = tpl.MonthlyPrice,
            AnnualPrice = tpl.AnnualPrice,
            BillingCycle = BillingCycle.Monthly,
            GracePeriodDays = 7,
            CreatedBy = JwtHelper.GetUserIdFromClaims(User).ToString(),
        };
        _db.AccountSubscriptions.Add(a);
        await _db.SaveChangesAsync();
        return StatusCode(201, new ApiResponse<Guid>(true, a.Id, "สร้าง Account Plan สำเร็จ"));
    }
}
