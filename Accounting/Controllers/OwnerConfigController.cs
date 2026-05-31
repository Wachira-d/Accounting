using System.Text.Json;
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
/// Owner-only endpoints for the company-level feature + menu opt-out.
///
/// Two distinct things the Owner can toggle:
///   1. Disable a feature — the entire feature surface (every menu
///      item carrying that feature flag, every API gate that checks
///      it) goes dark. Subtracted from Subscription.EnabledFeatures
///      via CompanySettings.OwnerDisabledFeatures.
///   2. Hide a specific menu — feature stays enabled (the API still
///      works for code that needs it) but the sidebar entry
///      disappears for everyone in this company. Useful when a small
///      team finds a sub-menu noisy. Stored as
///      CompanySettings.OwnerHiddenMenuIdsJson.
///
/// Owner-only: SystemAdmin can override via the existing plan/admin
/// flows, but day-to-day customisation belongs to the company owner.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/owner-config")]
[Authorize]
public class OwnerConfigController : ControllerBase
{
    private readonly AccountingDbContext _db;

    public OwnerConfigController(AccountingDbContext db) => _db = db;

    /// <summary>GET — current state. Returns the Subscription plan's
    /// total feature set, what the Owner has disabled, what's
    /// effectively on, and the hidden-menu list. UI uses this to
    /// render the toggle grid.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(Guid companyId, CancellationToken ct)
    {
        if (!await IsOwnerAsync(companyId, ct)) return Forbid();
        var sub = await _db.Subscriptions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        if (sub == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบ subscription"));
        var settings = await _db.Set<CompanySettings>()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);

        var planFeatures = sub.EnabledFeatures;
        var disabled = settings?.OwnerDisabledFeatures ?? FeatureFlags.None;
        var effective = planFeatures & ~disabled;

        List<string> hiddenMenuIds = new();
        if (!string.IsNullOrWhiteSpace(settings?.OwnerHiddenMenuIdsJson))
        {
            try { hiddenMenuIds = JsonSerializer.Deserialize<List<string>>(settings.OwnerHiddenMenuIdsJson) ?? new(); }
            catch { /* tolerate malformed */ }
        }

        return Ok(new ApiResponse<object>(true, new
        {
            planFeatures = FeatureFlagsHelper.ToNameList(planFeatures),
            disabledFeatures = FeatureFlagsHelper.ToNameList(disabled),
            effectiveFeatures = FeatureFlagsHelper.ToNameList(effective),
            hiddenMenuIds,
        }));
    }

    public sealed record UpdateOwnerConfigRequest(
        // Names of features to disable. Server intersects with the plan
        // so the Owner can't accidentally re-enable a feature their
        // plan doesn't include.
        List<string>? DisabledFeatureNames,
        // Menu nav ids to hide. Free-form — must match layout.js ids.
        List<string>? HiddenMenuIds);

    /// <summary>PUT — replace the whole config. Two arrays:
    /// disabledFeatureNames (the features to switch OFF on top of the
    /// plan) and hiddenMenuIds. Each is a complete replacement so the
    /// UI can do "save all" semantics.</summary>
    [HttpPut]
    public async Task<ActionResult<ApiResponse<object>>> Update(
        Guid companyId, [FromBody] UpdateOwnerConfigRequest req, CancellationToken ct)
    {
        if (!await IsOwnerAsync(companyId, ct)) return Forbid();
        var settings = await _db.Set<CompanySettings>()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        if (settings == null)
        {
            settings = new CompanySettings { CompanyId = companyId };
            _db.Set<CompanySettings>().Add(settings);
        }

        // Disabled features: convert names → flags. Intersect with the
        // plan's flags so the Owner can't somehow target features they
        // never had (defence in depth — a buggy frontend can't bypass
        // the plan).
        var disabledFlags = FeatureFlagsHelper.FromNameList(req.DisabledFeatureNames ?? new());
        var sub = await _db.Subscriptions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        if (sub != null) disabledFlags &= sub.EnabledFeatures;
        settings.OwnerDisabledFeatures = disabledFlags;

        // Hidden menus: serialise the list verbatim. UI is the source
        // of truth for menu ids.
        var hidden = (req.HiddenMenuIds ?? new())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct()
            .ToList();
        settings.OwnerHiddenMenuIdsJson = hidden.Count == 0
            ? null
            : JsonSerializer.Serialize(hidden);

        settings.UpdatedBy = User.Identity?.Name;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new ApiResponse<object>(true, new
        {
            disabledFeatures = FeatureFlagsHelper.ToNameList(disabledFlags),
            hiddenMenuIds = hidden,
        }, "บันทึกสำเร็จ"));
    }

    private async Task<bool> IsOwnerAsync(Guid companyId, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var role = await _db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
            .Select(cu => (UserRole?)cu.Role)
            .FirstOrDefaultAsync(ct);
        return role == UserRole.Owner || role == UserRole.SystemAdmin;
    }
}
