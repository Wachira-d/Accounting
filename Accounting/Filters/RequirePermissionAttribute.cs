using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Accounting.Filters;

/// <summary>
/// Action filter: 403 the request when the authenticated user lacks the
/// named permission key on the route's <c>companyId</c>. Apply at the
/// controller or action level — Owner / SystemAdmin auto-pass via
/// <see cref="IPermissionService"/> so admins never see the gate.
///
/// Usage: <c>[RequirePermission(PermissionKeys.ReportsDashboard)]</c> on
/// the controller covers every endpoint inside it with one declaration —
/// far less noisy than per-action <c>if (!await ...) return Forbid</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequirePermissionAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly string _key;
    public RequirePermissionAttribute(string permissionKey) { _key = permissionKey; }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        // Need an authenticated user — [Authorize] should have run first.
        var user = ctx.HttpContext.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            ctx.Result = new UnauthorizedResult();
            return;
        }

        // Route value parser — companyId can sit in {companyId} or be
        // bound from query string for non-tenant routes. We require the
        // tenant-scoped form because permissions are per-company.
        if (!ctx.RouteData.Values.TryGetValue("companyId", out var raw) ||
            !Guid.TryParse(raw?.ToString(), out var companyId))
        {
            ctx.Result = new ObjectResult(new ApiResponse<object>(false, null,
                "ไม่พบ companyId ใน route — RequirePermission ต้องใช้กับ tenant-scoped endpoint"))
            { StatusCode = 500 };
            return;
        }

        var userId = JwtHelper.GetUserIdFromClaims(user);
        var perms = ctx.HttpContext.RequestServices.GetService(typeof(IPermissionService)) as IPermissionService;
        if (perms == null)
        {
            // Service unavailable — fail closed (don't accidentally grant access).
            ctx.Result = new ObjectResult(new ApiResponse<object>(false, null,
                "PermissionService unavailable"))
            { StatusCode = 503 };
            return;
        }

        if (!await perms.HasPermissionAsync(companyId, userId, _key))
        {
            // ข้อความบอกทางไปต่อ (W-C7) — ตัวสร้างข้อความตัวเดียวที่ PermissionKeys
            ctx.Result = new ObjectResult(new ApiResponse<object>(false,
                new { requiredPermission = _key.Replace("perm:", "") },
                Accounting.Models.Constants.PermissionKeys.DeniedMessage(_key)))
            { StatusCode = 403 };
        }
    }
}
