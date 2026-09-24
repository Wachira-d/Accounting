using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Filters;

/// <summary>
/// **action นี้ทำได้เฉพาะเจ้าของบริษัท (ที่ล็อกอินด้วยตัวเอง)** — ฝ่ายค้านรอบ 193 รอบสอง W2-C3/W2-C4
///
/// <para>ตัวตัดสินคือ <see cref="OwnerGateDecision.Decide"/> ตัวเดียว (ไฟล์นี้แค่อ่าน role จากฐานข้อมูลแล้วต่อสายเข้า MVC) ·
/// ใช้กับงานที่ doc เขียนว่า "Owner only" แต่เดิมไม่มีด่าน: คีย์ลับ/โหมด live ของ payment gateway · สิทธิ์ดูเอกสารลับ ·
/// และเป็นด่านเดียวกับ <c>SubscriptionController</c> (เรียก <see cref="DenyAsync"/> แบบ inline)</para>
///
/// <para>ปฏิเสธเป็น 403 + ข้อความไทย (ไม่ใช่ 401 ที่หน้าเว็บตีเป็น "session หมด") · ต้องใช้กับ route ที่มี <c>{companyId}</c></para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireOwnerAttribute : Attribute, IAsyncAuthorizationFilter
{
    public string Verb { get; }
    public string? Why { get; }

    public RequireOwnerAttribute(string verb = "ทำงานนี้", string? why = null) { Verb = verb; Why = why; }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        if (!ctx.RouteData.Values.TryGetValue("companyId", out var raw)
            || !Guid.TryParse(raw?.ToString(), out var companyId))
        {
            ctx.Result = new ObjectResult(new ApiResponse<object>(false, null,
                "ไม่พบ companyId ใน route — RequireOwner ต้องใช้กับ endpoint ของบริษัท")) { StatusCode = 500 };
            return;
        }
        var db = ctx.HttpContext.RequestServices.GetService(typeof(AccountingDbContext)) as AccountingDbContext;
        if (db == null)
        {
            // fail closed — ไม่มีฐานข้อมูลให้ตรวจ = ไม่ปล่อย
            ctx.Result = new ObjectResult(new ApiResponse<object>(false, null, "ตรวจสิทธิ์เจ้าของไม่ได้ในขณะนี้"))
            { StatusCode = 503 };
            return;
        }
        if (await DenyAsync(ctx.HttpContext, db, companyId, Verb, Why) is { } deny)
            ctx.Result = deny;
    }

    /// <summary>ด่านเจ้าของแบบ inline (null = ผ่าน) — ใช้เมื่อ companyId มาจากที่อื่นนอก route</summary>
    public static async Task<ObjectResult?> DenyAsync(HttpContext http, AccountingDbContext db, Guid companyId,
        string verb, string? why = null)
    {
        var isKey = OwnerActionGuard.IsApiKeyRequest(http);
        var isPlatformAdmin = http.User?.IsInRole("SystemAdmin") == true;
        UserRole? role = null;
        if (!isKey && !isPlatformAdmin)
        {
            var userId = JwtHelper.GetUserIdFromClaims(http.User!);
            role = await db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
                .Select(cu => (UserRole?)cu.Role)
                .FirstOrDefaultAsync();
        }
        return OwnerGateDecision.Decide(isKey, isPlatformAdmin, role) switch
        {
            OwnerGateDecision.Outcome.Allow => null,
            OwnerGateDecision.Outcome.DenyApiKey => OwnerActionGuard.DenyResult(http, verb),
            _ => new ObjectResult(new ApiResponse<object>(false, new { requiredRole = "Owner" },
                OwnerGateDecision.NotOwnerMessage(verb, why))) { StatusCode = 403 },
        };
    }
}
