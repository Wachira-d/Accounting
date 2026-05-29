using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class SensitivityService : ISensitivityService
{
    private readonly AccountingDbContext _db;

    public SensitivityService(AccountingDbContext db)
    {
        _db = db;
    }

    // Default allow-list applied when the company hasn't customized rules yet.
    // Owner always sees everything (enforced separately). Accountant sees the
    // payroll stream because they post the JEs. Staff/Viewer/Auditor are
    // denied by default — owner can opt them in via the settings UI.
    private static readonly Dictionary<SensitivityKind, HashSet<UserRole>> _defaults = new()
    {
        [SensitivityKind.Payroll]      = new() { UserRole.Owner, UserRole.Accountant, UserRole.SystemAdmin },
        [SensitivityKind.ExecutivePay] = new() { UserRole.Owner, UserRole.SystemAdmin },
        [SensitivityKind.HrPersonal]   = new() { UserRole.Owner, UserRole.SystemAdmin },
        [SensitivityKind.Confidential] = new() { UserRole.Owner, UserRole.SystemAdmin },
    };

    public async Task<bool> CanViewAsync(Guid companyId, Guid userId, SensitivityKind kind)
    {
        if (kind == SensitivityKind.None) return true;

        // System admin override — always sees everything.
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return false;
        if (user.IsSystemAdmin) return true;

        // Resolve the user's role in this company.
        var membership = await _db.CompanyUsers.AsNoTracking()
            .FirstOrDefaultAsync(cu => cu.CompanyId == companyId && cu.UserId == userId && !cu.IsDeleted);
        if (membership == null) return false;
        if (membership.Role == UserRole.Owner) return true;

        // Look up the company's customized rule first; fall back to the built-in default.
        var rule = await _db.SensitivityAccessRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Kind == kind && r.Role == membership.Role && !r.IsDeleted);
        if (rule != null) return rule.CanView;

        return _defaults.TryGetValue(kind, out var allowed) && allowed.Contains(membership.Role);
    }

    public async Task<HashSet<SensitivityKind>> GetVisibleKindsAsync(Guid companyId, Guid userId)
    {
        var result = new HashSet<SensitivityKind> { SensitivityKind.None };
        foreach (var kind in Enum.GetValues<SensitivityKind>())
        {
            if (kind == SensitivityKind.None) continue;
            if (await CanViewAsync(companyId, userId, kind)) result.Add(kind);
        }
        return result;
    }

    public async Task SetRuleAsync(Guid companyId, SensitivityKind kind, UserRole role, bool canView, string updatedByUserId)
    {
        if (role == UserRole.Owner)
            throw new InvalidOperationException("เจ้าของบริษัทเห็นทุกอย่างเสมอ ไม่สามารถปิดสิทธิ์ได้");

        var existing = await _db.SensitivityAccessRules
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Kind == kind && r.Role == role);
        if (existing != null)
        {
            existing.CanView = canView;
            existing.UpdatedBy = updatedByUserId;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.IsDeleted = false;
        }
        else
        {
            _db.SensitivityAccessRules.Add(new SensitivityAccessRule
            {
                CompanyId = companyId, Kind = kind, Role = role, CanView = canView,
                CreatedBy = updatedByUserId
            });
        }
        await _db.SaveChangesAsync();
    }

    public async Task<List<SensitivityRuleDto>> GetRulesAsync(Guid companyId)
    {
        var custom = await _db.SensitivityAccessRules.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted)
            .ToDictionaryAsync(r => (r.Kind, r.Role), r => r.CanView);

        var rows = new List<SensitivityRuleDto>();
        foreach (var kind in Enum.GetValues<SensitivityKind>())
        {
            if (kind == SensitivityKind.None) continue;
            foreach (var role in Enum.GetValues<UserRole>())
            {
                bool effective = role == UserRole.Owner
                    || role == UserRole.SystemAdmin
                    || (custom.TryGetValue((kind, role), out var v)
                        ? v
                        : (_defaults.TryGetValue(kind, out var ds) && ds.Contains(role)));
                rows.Add(new SensitivityRuleDto(kind, role, effective));
            }
        }
        return rows;
    }
}
