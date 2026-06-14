using Accounting.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Accounting.Services.Implementations.Security;

/// <summary>
/// F1 — Tenant safety guard. ตรวจว่า user ที่ authenticated มีสิทธิ์
/// เข้าถึง companyId ใน route. ป้องกันบั๊ก class "Insecure Direct Object
/// Reference (IDOR)" — user ของบริษัท A ส่ง URL ของบริษัท B แล้วระบบ
/// คืน data ของ B ให้ (เพราะ controller ใช้ companyId จาก route ตรง ๆ
/// โดยไม่ตรวจ membership).
///
/// แนวคิดทางเลือก:
/// (a) EF Global Query Filter ระดับ DbSet — เสี่ยงต่อ cross-tenant admin
///     operations (system admin / consolidation report) ที่ต้อง union
///     ข้าม company. ต้อง IgnoreQueryFilters() ทุกที่ — error-prone.
/// (b) Explicit guard ที่ controller — caller intent ชัด, รู้เมื่อไหร่
///     ที่ตั้งใจ cross-tenant (ไม่เรียก guard). อ่านง่าย, refactor ง่าย.
///
/// เลือก (b). Helper นี้ถูกเรียกที่ต้น controller endpoint — throws 403
/// ถ้า user ไม่ใช่ member ของ company.
///
/// ใช้:
///   var guard = await _tenantGuard.EnsureMembershipAsync(companyId, User);
///   if (guard.IsBlocked) return Forbid();
///
/// Caching: result เก็บใน-memory ของ instance — DbContext scoped ต่อ
/// request → cache ตามอายุ request เท่านั้น.
/// </summary>
public interface ITenantGuard
{
    /// <summary>คืน true เมื่อ user ใน claims เป็น member ของ companyId.
    /// Owner/Admin ของบริษัท B จะถูก block จาก companyId ของบริษัท A
    /// (ยกเว้น SystemAdmin role ที่ทำ cross-tenant audit ได้).</summary>
    Task<TenantCheckResult> CheckAsync(Guid companyId, ClaimsPrincipal user);
}

public sealed record TenantCheckResult(bool IsAllowed, string? Reason)
{
    public bool IsBlocked => !IsAllowed;
}

public class TenantGuard : ITenantGuard
{
    private readonly AccountingDbContext _db;
    private readonly Dictionary<(Guid CompanyId, Guid UserId), bool> _cache = new();

    public TenantGuard(AccountingDbContext db) { _db = db; }

    public async Task<TenantCheckResult> CheckAsync(Guid companyId, ClaimsPrincipal user)
    {
        // SystemAdmin = bypass (audit-cross-tenant flows)
        if (user.IsInRole("SystemAdmin"))
            return new TenantCheckResult(true, "SystemAdmin bypass");

        var userIdStr = user.FindFirst("sub")?.Value
                     ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdStr, out var userId))
            return new TenantCheckResult(false, "ไม่พบ user id ใน claims");

        var key = (companyId, userId);
        if (_cache.TryGetValue(key, out var cached))
            return new TenantCheckResult(cached, cached ? null : "ไม่ใช่สมาชิกของบริษัทนี้");

        // ตรวจ CompanyUsers — ตารางที่ระบบใช้ track membership อยู่แล้ว
        var isMember = await _db.Set<Models.Entities.CompanyUser>().AsNoTracking()
            .AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == userId);
        _cache[key] = isMember;
        return new TenantCheckResult(isMember, isMember ? null : "user ไม่ใช่สมาชิกของ company นี้");
    }
}
