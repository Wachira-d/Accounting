using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 267 แพ็ก P5 (คำตัดสินเจ้าของ 267-1-13 · DOCUMENT_FLOW §2.12n-P5) — <b>เข้าด้วย LINE เพื่อเติมข้อมูลผู้ลงนาม</b> บนหน้าเอกสาร ·
/// ทุกการตัดสินอยู่ที่ <see cref="CustomerSignerProfileService"/>/<see cref="CustomerSignerProfileRule"/> — ที่นี่ส่งต่อเท่านั้น (ไม่มี <c>_db</c>) ·
/// เพดานต่อ IP ก่อนแตะฐานทุกเส้น · ข้อความล้มเดียว · ตอบ <c>no-store</c> · ไม่มีเส้นนี้ (ปิด LINE Login) = หน้าไม่แสดงปุ่ม ลงนามได้ตามปกติ
/// </summary>
public partial class PublicDocumentPageController
{
    private CustomerSignerProfileService? Profiles => HttpContext?.RequestServices.GetService(typeof(CustomerSignerProfileService)) as CustomerSignerProfileService;

    /// <summary>ที่อยู่เส้น LINE Login บนหน้า — เฉพาะเมื่อหน้ามีฟอร์มตอบรับที่เปิดอยู่และ LINE Login ของระบบเปิดใช้</summary>
    private async Task<CustomerDocPageView> WithSignerPathsAsync(CustomerDocPageView page, string basePath, CancellationToken ct)
    {
        if (page.Respond == null || Profiles is not { } profiles || !await profiles.AvailableAsync(ct)) return page;
        return page with
        {
            SignerLoginPath = basePath + "/signer-login",
            SignerLoginCompletePath = basePath + "/signer-login/complete",
            SignerForgetPath = basePath + "/signer-profile/forget",
        };
    }

    public sealed record SignerLoginCompleteRequest(string? Code, string? State);
    public sealed record SignerTicketRequest(string? Ticket);

    /// <summary>เริ่มเข้าด้วย LINE — คืน URL หน้าอนุญาตของ LINE (state ผูก token ของใบนี้ · 10 นาที)</summary>
    [HttpPost("documents/{token}/signer-login")]
    public async Task<ActionResult<ApiResponse<object>>> SignerLogin(Guid companyId, string token, CancellationToken ct)
    {
        NoStore();
        if (await OverIpAsync("signer-login", failClosed: true, ct)) return TooMany<object>();
        var start = Profiles == null ? null : await Profiles.LoginUrlAsync(companyId, token, ct);
        if (start == null) return NotFound(new ApiResponse<object>(false, null, CustomerDocPage.NotFoundMessage));
        // ฝ่ายค้าน 267-P5 LOW-1: ผูก state กับเบราว์เซอร์นี้ฝั่งเซิร์ฟเวอร์ (cookie HttpOnly · SameSite=Lax — กลับจาก LINE เป็น top-level ยังส่ง) — กัน login CSRF
        Response.Cookies.Append(CustomerSignerProfileRule.NonceCookie, start.BrowserNonce, new Microsoft.AspNetCore.Http.CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            Path = "/api/companies/", MaxAge = TimeSpan.FromMinutes(CustomerSignerProfileRule.StateTtlMinutes),
        });
        return Ok(new ApiResponse<object>(true, new { url = start.Url }, null));
    }

    /// <summary>LINE คืน code/state มาที่หน้าเอกสาร ⇒ ตั๋วผู้ลงนาม (30 นาที · ไม่เก็บในฐาน) + ค่าที่จำไว้ (ถ้ายินยอมไว้) — <b>ไม่มีช่องรับรอง</b></summary>
    [HttpPost("documents/{token}/signer-login/complete")]
    public async Task<ActionResult<ApiResponse<object>>> SignerLoginComplete(Guid companyId, string token, [FromBody] SignerLoginCompleteRequest? request, CancellationToken ct)
    {
        NoStore();
        if (await OverIpAsync("signer-login", failClosed: true, ct)) return TooMany<object>();
        if (Profiles == null) return NotFound(new ApiResponse<object>(false, null, CustomerDocPage.NotFoundMessage));
        var r = await Profiles.CompleteLoginAsync(companyId, token, request?.Code, request?.State,
            Request.Cookies.TryGetValue(CustomerSignerProfileRule.NonceCookie, out var nonce) ? nonce : null, ct);
        Response.Cookies.Delete(CustomerSignerProfileRule.NonceCookie, new Microsoft.AspNetCore.Http.CookieOptions { Path = "/api/companies/" });
        return r.Ticket == null
            ? BadRequest(new ApiResponse<object>(false, null, r.Error))
            : Ok(new ApiResponse<object>(true, new { ticket = r.Ticket, prefill = r.Prefill }, null));
    }

    /// <summary>ถอนความยินยอม + ลบข้อมูลที่จำไว้กับ LINE (ต้องมีตั๋วของลิงก์นี้) · แถวหลักฐานการลงนามไม่ถูกแตะ</summary>
    [HttpPost("documents/{token}/signer-profile/forget")]
    public async Task<ActionResult<ApiResponse<object>>> SignerForget(Guid companyId, string token, [FromBody] SignerTicketRequest? request, CancellationToken ct)
    {
        NoStore();
        if (await OverIpAsync("signer-login", failClosed: true, ct)) return TooMany<object>();
        if (Profiles == null) return NotFound(new ApiResponse<object>(false, null, CustomerDocPage.NotFoundMessage));
        var error = await Profiles.WithdrawAsync(companyId, token, request?.Ticket, ct);
        return error != null
            ? BadRequest(new ApiResponse<object>(false, null, error))
            : Ok(new ApiResponse<object>(true, null, "ลบข้อมูลที่จำไว้และถอนความยินยอมแล้ว"));
    }
}
