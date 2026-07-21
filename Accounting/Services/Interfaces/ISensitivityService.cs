using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

/// <summary>
/// Gates access to records flagged with a <see cref="SensitivityKind"/>.
/// Returns the user's allow-set for the current company so callers (Document /
/// JE / Payroll services) can both filter their queries AND build redacted
/// stubs for any record the user can't see. Owner / SystemAdmin always see
/// everything.
/// </summary>
public interface ISensitivityService
{
    /// <summary>True if the user can read records of the given Kind in this company.</summary>
    Task<bool> CanViewAsync(Guid companyId, Guid userId, SensitivityKind kind);

    /// <summary>The set of Kinds visible to the user. None is always implicit.</summary>
    Task<HashSet<SensitivityKind>> GetVisibleKindsAsync(Guid companyId, Guid userId);

    /// <summary>Update which roles can see a kind. Owner/SystemAdmin only.</summary>
    Task SetRuleAsync(Guid companyId, SensitivityKind kind, UserRole role, bool canView, string updatedByUserId);

    /// <summary>Read the full rule matrix for the settings UI.</summary>
    Task<List<SensitivityRuleDto>> GetRulesAsync(Guid companyId);
}

public record SensitivityRuleDto(SensitivityKind Kind, UserRole Role, bool CanView);
