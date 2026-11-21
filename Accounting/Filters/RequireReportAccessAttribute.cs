using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Accounting.Filters;

/// <summary>
/// รอบ 284 (คำตัดสิน 284-7): ด่านสิทธิ์ของรายงานการเงินรวม — ตัวตัดสิน <see cref="FinancialReportAccess.AllowsAsync"/> (คีย์ใดคีย์หนึ่ง ·
/// สมาชิกที่ไม่ผูกบทบาทกำหนดเองผ่านตามเดิม) · ต่างจาก <see cref="RequirePermissionAttribute"/> (เข้มทุกคน) เพื่อไม่ทำให้พนักงานที่เจ้าของ
/// ไม่เคยตั้งบทบาทเสียหน้ารายงานที่ใช้อยู่
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireReportAccessAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly string[] _keys;
    public RequireReportAccessAttribute(params string[] keys)
    {
        if (keys == null || keys.Length == 0) throw new ArgumentException("ต้องระบุคีย์อย่างน้อยหนึ่งตัว", nameof(keys));
        _keys = keys;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        var user = ctx.HttpContext.User;
        if (user?.Identity?.IsAuthenticated != true) { ctx.Result = new UnauthorizedResult(); return; }
        if (!ctx.RouteData.Values.TryGetValue("companyId", out var raw) || !Guid.TryParse(raw?.ToString(), out var companyId))
        {
            ctx.Result = new ObjectResult(new ApiResponse<object>(false, null,
                "ไม่พบ companyId ใน route — RequireReportAccess ต้องใช้กับ tenant-scoped endpoint")) { StatusCode = 500 };
            return;
        }
        var perms = ctx.HttpContext.RequestServices.GetService(typeof(IPermissionService)) as IPermissionService;
        var db = ctx.HttpContext.RequestServices.GetService(typeof(AccountingDbContext)) as AccountingDbContext;
        if (perms == null || db == null)
        {
            ctx.Result = new ObjectResult(new ApiResponse<object>(false, null, "PermissionService unavailable")) { StatusCode = 503 };
            return;
        }
        if (!await FinancialReportAccess.AllowsAsync(db, perms, companyId, JwtHelper.GetUserIdFromClaims(user), _keys))
            ctx.Result = new ObjectResult(new ApiResponse<object>(false,
                new { requiredPermission = _keys[0].Replace("perm:", ""), anyOf = _keys.Select(k => k.Replace("perm:", "")) },
                FinancialReportAccess.DeniedMessage(_keys))) { StatusCode = 403 };
    }
}
