using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Account-level subscription endpoints — the paying user manages their plan
/// here, then attaches or detaches Companies (subject to MaxCompanies on the
/// plan). All endpoints scope to the calling User; admins / system staff use
/// the admin endpoints to override.
/// </summary>
[ApiController]
[Route("api/account-subscription")]
[Authorize]
public class AccountSubscriptionController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public AccountSubscriptionController(AccountingDbContext db) { _db = db; }

    public record AccountSubDto(
        Guid Id, Guid OwnerUserId, Guid PlanTemplateId, string PlanName,
        SubscriptionStatus Status, DateTime StartDate, DateTime EndDate,
        int MaxCompanies, int CompaniesUsed, int MaxUsersPerCompany,
        int MaxDocumentsPerMonth, int MaxJournalEntriesPerMonth,
        long MaxStorageBytes, int MaxOcrPagesPerMonth,
        long EnabledFeaturesValue, decimal MonthlyPrice, decimal AnnualPrice,
        BillingCycle BillingCycle, int GracePeriodDays);

    /// <summary>Current user's account plan + the list of Companies attached.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> GetMine()
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var acct = await _db.AccountSubscriptions
            .Include(a => a.PlanTemplate)
            .Where(a => a.OwnerUserId == userId && !a.IsDeleted
                && a.Status != SubscriptionStatus.Cancelled
                && a.Status != SubscriptionStatus.Expired)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();

        if (acct == null)
        {
            // Surface the companies the user owns so the UI can prompt
            // "start an Account Plan to cover these in one bill".
            var owned = await _db.CompanyUsers
                .Where(cu => cu.UserId == userId && cu.Role == UserRole.Owner && !cu.IsDeleted)
                .Include(cu => cu.Company)
                .Select(cu => new { cu.Company.Id, cu.Company.Name })
                .ToListAsync();
            return Ok(new ApiResponse<object>(true, new
            {
                hasAccountPlan = false,
                ownedCompanies = owned,
            }, "ยังไม่มี Account Plan — แต่ละบริษัทใช้ subscription ของตัวเอง"));
        }

        var usedCount = await _db.Subscriptions.CountAsync(s => s.AccountSubscriptionId == acct.Id && !s.IsDeleted);
        var attached = await _db.Subscriptions
            .Where(s => s.AccountSubscriptionId == acct.Id && !s.IsDeleted)
            .Include(s => s.Company)
            .Select(s => new { s.CompanyId, CompanyName = s.Company.Name, s.Status })
            .ToListAsync();

        var dto = new AccountSubDto(acct.Id, acct.OwnerUserId, acct.PlanTemplateId, acct.PlanTemplate.PlanName,
            acct.Status, acct.StartDate, acct.EndDate, acct.MaxCompanies, usedCount,
            acct.MaxUsersPerCompany, acct.MaxDocumentsPerMonth, acct.MaxJournalEntriesPerMonth,
            acct.MaxStorageBytes, acct.MaxOcrPagesPerMonth,
            (long)acct.EnabledFeatures, acct.MonthlyPrice, acct.AnnualPrice,
            acct.BillingCycle, acct.GracePeriodDays);

        return Ok(new ApiResponse<object>(true, new
        {
            hasAccountPlan = true,
            plan = dto,
            attachedCompanies = attached,
        }));
    }

    public record StartTrialRequest(Guid PlanTemplateId);

    /// <summary>Start the account-level trial. Picks limits from PlanTemplate
    /// at create time so admin plan changes don't silently shrink an
    /// established account.</summary>
    [HttpPost("start-trial")]
    public async Task<ActionResult<ApiResponse<AccountSubDto>>> StartTrial([FromBody] StartTrialRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var existing = await _db.AccountSubscriptions
            .FirstOrDefaultAsync(a => a.OwnerUserId == userId && !a.IsDeleted
                && (a.Status == SubscriptionStatus.Trial || a.Status == SubscriptionStatus.Active || a.Status == SubscriptionStatus.PastDue || a.Status == SubscriptionStatus.Suspended));
        if (existing != null)
            return BadRequest(new ApiResponse<AccountSubDto>(false, null!, "มี Account Plan ที่ active อยู่แล้ว"));

        var tpl = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Id == req.PlanTemplateId && p.IsActive);
        if (tpl == null) return BadRequest(new ApiResponse<AccountSubDto>(false, null!, "ไม่พบ plan template"));

        var acct = new AccountSubscription
        {
            OwnerUserId = userId,
            PlanTemplateId = tpl.Id,
            Status = SubscriptionStatus.Trial,
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddDays(tpl.TrialDurationDays > 0 ? tpl.TrialDurationDays : 14),
            MaxCompanies = Math.Max(1, tpl.MaxCompanies),
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
        };
        _db.AccountSubscriptions.Add(acct);
        await _db.SaveChangesAsync();

        return StatusCode(201, new ApiResponse<AccountSubDto>(true,
            new AccountSubDto(acct.Id, acct.OwnerUserId, tpl.Id, tpl.Name,
                acct.Status, acct.StartDate, acct.EndDate, acct.MaxCompanies, 0,
                acct.MaxUsersPerCompany, acct.MaxDocumentsPerMonth, acct.MaxJournalEntriesPerMonth,
                acct.MaxStorageBytes, acct.MaxOcrPagesPerMonth,
                (long)acct.EnabledFeatures, acct.MonthlyPrice, acct.AnnualPrice,
                acct.BillingCycle, acct.GracePeriodDays),
            "เริ่มทดลองใช้ Account Plan สำเร็จ"));
    }

    public record AttachRequest(Guid CompanyId);

    /// <summary>Attach a Company under the user's Account Plan. The user must
    /// be Owner of the Company. Reduces the company to the account's quota
    /// (account features win). Respects MaxCompanies.</summary>
    [HttpPost("attach")]
    public async Task<ActionResult<ApiResponse<string>>> Attach([FromBody] AttachRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var acct = await _db.AccountSubscriptions
            .FirstOrDefaultAsync(a => a.OwnerUserId == userId && !a.IsDeleted
                && (a.Status == SubscriptionStatus.Trial || a.Status == SubscriptionStatus.Active || a.Status == SubscriptionStatus.PastDue));
        if (acct == null) return BadRequest(new ApiResponse<string>(false, null, "ไม่มี Account Plan ที่ใช้งานได้"));

        // Ownership check — only Owner of the Company can attach it.
        var isOwner = await _db.CompanyUsers.AnyAsync(cu => cu.UserId == userId && cu.CompanyId == req.CompanyId
            && cu.Role == UserRole.Owner && !cu.IsDeleted);
        if (!isOwner) return Forbid();

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == req.CompanyId && !s.IsDeleted);
        if (sub == null) return BadRequest(new ApiResponse<string>(false, null, "บริษัทยังไม่มี Subscription"));
        if (sub.AccountSubscriptionId == acct.Id)
            return Ok(new ApiResponse<string>(true, null, "ผูกกับ Account Plan อยู่แล้ว"));

        // Quota check — count companies currently attached (excluding the one
        // being attached if it's already counted).
        var used = await _db.Subscriptions.CountAsync(s => s.AccountSubscriptionId == acct.Id
            && s.CompanyId != req.CompanyId && !s.IsDeleted);
        if (used >= acct.MaxCompanies)
            return BadRequest(new ApiResponse<string>(false, null,
                $"Account Plan ครอบได้สูงสุด {acct.MaxCompanies} บริษัท — ปัจจุบันใช้ {used} บริษัท · อัพเกรดเพื่อเพิ่ม"));

        sub.AccountSubscriptionId = acct.Id;
        sub.UpdatedBy = userId.ToString();
        sub.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, null, "ผูกบริษัทเข้า Account Plan สำเร็จ"));
    }

    /// <summary>Detach the Company — falls back to the per-company Subscription
    /// row's own limits (which may be the original FreeTrial or an older paid
    /// plan that's still on file). Useful when selling a company or when a
    /// holding decides to split bills.</summary>
    [HttpPost("detach")]
    public async Task<ActionResult<ApiResponse<string>>> Detach([FromBody] AttachRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var isOwner = await _db.CompanyUsers.AnyAsync(cu => cu.UserId == userId && cu.CompanyId == req.CompanyId
            && cu.Role == UserRole.Owner && !cu.IsDeleted);
        if (!isOwner) return Forbid();

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == req.CompanyId && !s.IsDeleted);
        if (sub == null) return NotFound();
        sub.AccountSubscriptionId = null;
        sub.UpdatedBy = userId.ToString();
        sub.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, null, "ถอดบริษัทออกจาก Account Plan แล้ว — บริษัทกลับไปใช้ subscription ของตัวเอง"));
    }

    /// <summary>Effective plan resolution for a Company — useful for the UI to
    /// show which plan is currently driving the limits (account vs company).</summary>
    [HttpGet("effective/{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Effective(Guid companyId, [FromServices] Services.Interfaces.ISubscriptionService subSvc)
    {
        var eff = await subSvc.GetEffectivePlanAsync(companyId);
        if (eff == null) return Ok(new ApiResponse<object>(true, new { hasPlan = false }));
        return Ok(new ApiResponse<object>(true, new
        {
            hasPlan = true,
            source = eff.Source,
            isActive = eff.IsActive,
            inGrace = eff.InGrace,
            status = eff.Status.ToString(),
            endDate = eff.EndDate,
            maxUsers = eff.MaxUsers,
            maxCompanies = eff.MaxCompanies,
            maxDocumentsPerMonth = eff.MaxDocumentsPerMonth,
            maxJournalEntriesPerMonth = eff.MaxJournalEntriesPerMonth,
            maxStorageBytes = eff.MaxStorageBytes,
            maxOcrPagesPerMonth = eff.MaxOcrPagesPerMonth,
            azureOcrPagesPerMonth = eff.AzureOcrPagesPerMonth,
            localOcrPagesPerMonth = eff.LocalOcrPagesPerMonth,
            enabledFeatures = (long)eff.EnabledFeatures,
        }));
    }
}
