using System.Security.Claims;
using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Notification;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>รอบ 245 L1 (LINE_MULTICHANNEL_DESIGN §7.1) — แท็บ "ช่องทางแจ้งเตือน" ของผู้ติดต่อ
/// <para>อ่าน: สมาชิกบริษัท (เท่ากับหน้าผู้ติดต่อ · <c>TenantAccessMiddleware</c>) · เขียนทุกตัว: <c>Contact.Edit</c> (ด่านเดียวกับแก้ผู้ติดต่อ — ตรวจในตัว action
/// ก่อนเรียก service · <c>tools/write_permission_gate_check.py</c>) · คีย์ API เขียนไม่ได้ · ทุก query ผ่าน service ที่มี <c>CompanyId == companyId</c></para></summary>
[ApiController]
[Route("api/companies/{companyId:guid}/contacts/{contactId:guid}/notify-endpoints")]
[Authorize]
public class ContactNotifyEndpointsController : ControllerBase
{
    private readonly IContactRecipientResolver _resolver;
    private readonly IContactNotifyEndpointService _endpoints;
    private readonly IPermissionService _permissions;
    private readonly IContactLineBindingService? _lineBinding;

    public ContactNotifyEndpointsController(IContactRecipientResolver resolver, IContactNotifyEndpointService endpoints, IPermissionService permissions,
        IContactLineBindingService? lineBinding = null)
    {
        _resolver = resolver; _endpoints = endpoints; _permissions = permissions; _lineBinding = lineBinding;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<ContactNotifyOverviewResponse>>> Get(Guid companyId, Guid contactId, CancellationToken ct)
    {
        // ฝ่ายค้าน N3: ปุ่มบนหน้าตามสิทธิ์จริง (ด่านเดียวกับ write)
        var canEdit = await DenyKeyAsync(companyId, "แก้ช่องทางแจ้งเตือนของผู้ติดต่อ") == null;
        var overview = await _resolver.GetOverviewAsync(companyId, contactId, canEdit, ct);
        if (overview == null) return NotFound(new ApiResponse<ContactNotifyOverviewResponse>(false, null, "ไม่พบผู้ติดต่อ"));
        return Ok(new ApiResponse<ContactNotifyOverviewResponse>(true, overview));
    }

    /// <summary>ผู้รับของเรื่องหนึ่ง — ตัวหาผู้รับตัวเดียวกับที่เส้นส่งจะใช้ (หน้า "ดูว่าใครจะได้รับ")</summary>
    [HttpGet("recipients")]
    public async Task<ActionResult<ApiResponse<ContactRecipientsResponse>>> Recipients(Guid companyId, Guid contactId, [FromQuery] string? topic, CancellationToken ct)
    {
        var (parsed, problem) = NotifyTopics.FromRequest(new[] { topic ?? "" }, allowEmpty: false);
        if (problem != null || parsed == null || parsed.Count != 1)
            return BadRequest(new ApiResponse<ContactRecipientsResponse>(false, null, problem ?? "ระบุเรื่องหนึ่งเรื่อง"));
        var t = parsed[0];
        var set = await _resolver.ResolveAsync(companyId, contactId, t, ct);
        return Ok(new ApiResponse<ContactRecipientsResponse>(true, new ContactRecipientsResponse(t.ToString(), NotifyTopics.Label(t), set.Emails.ToList(),
            set.Options.Select(o => new ContactRecipientOptionResponse(o.EndpointId, o.Kind, NotifyEndpointKind.Label(o.Kind), o.Display, o.Available,
                o.Reason, o.RuleCode, o.IsPrimary)).ToList())));
    }

    [HttpPost("email")]
    [RejectApiKey("เพิ่มอีเมลแจ้งเตือนของผู้ติดต่อ")]
    public async Task<ActionResult<ApiResponse<ContactNotifyOverviewResponse>>> AddEmail(Guid companyId, Guid contactId,
        [FromBody] AddContactNotifyEmailRequest request, CancellationToken ct)
    {
        if (await DenyKeyAsync(companyId, "เพิ่มช่องทางแจ้งเตือนของผู้ติดต่อ") is { } deny) return Forbid403(deny);
        var r = await _endpoints.AddEmailAsync(companyId, contactId, request, Actor(), ct);
        return StatusCode(201, new ApiResponse<ContactNotifyOverviewResponse>(true, r, "เพิ่มอีเมลแล้ว"));
    }

    [HttpPut("primary")]
    [RejectApiKey("ตั้งเรื่องที่อีเมลหลักรับ")]
    public async Task<ActionResult<ApiResponse<ContactNotifyOverviewResponse>>> UpdatePrimary(Guid companyId, Guid contactId,
        [FromBody] UpdateContactNotifyPrimaryRequest request, CancellationToken ct)
    {
        if (await DenyKeyAsync(companyId, "ตั้งช่องทางแจ้งเตือนของผู้ติดต่อ") is { } deny) return Forbid403(deny);
        var r = await _endpoints.UpdatePrimaryAsync(companyId, contactId, request, Actor(), ct);
        return Ok(new ApiResponse<ContactNotifyOverviewResponse>(true, r, "บันทึกเรื่องที่อีเมลหลักรับแล้ว"));
    }

    [HttpPut("{endpointId:guid}")]
    [RejectApiKey("แก้ช่องทางแจ้งเตือนของผู้ติดต่อ")]
    public async Task<ActionResult<ApiResponse<ContactNotifyOverviewResponse>>> Update(Guid companyId, Guid contactId, Guid endpointId,
        [FromBody] UpdateContactNotifyEndpointRequest request, CancellationToken ct)
    {
        if (await DenyKeyAsync(companyId, "แก้ช่องทางแจ้งเตือนของผู้ติดต่อ") is { } deny) return Forbid403(deny);
        var r = await _endpoints.UpdateAsync(companyId, contactId, endpointId, request, Actor(), ct);
        return Ok(new ApiResponse<ContactNotifyOverviewResponse>(true, r, "บันทึกแล้ว"));
    }

    [HttpDelete("{endpointId:guid}")]
    [RejectApiKey("ยกเลิกช่องทางแจ้งเตือนของผู้ติดต่อ")]
    public async Task<ActionResult<ApiResponse<ContactNotifyOverviewResponse>>> Unlink(Guid companyId, Guid contactId, Guid endpointId, CancellationToken ct)
    {
        if (await DenyKeyAsync(companyId, "ยกเลิกช่องทางแจ้งเตือนของผู้ติดต่อ") is { } deny) return Forbid403(deny);
        var r = await _endpoints.UnlinkAsync(companyId, contactId, endpointId, Actor(), ct);
        return Ok(new ApiResponse<ContactNotifyOverviewResponse>(true, r, "ยกเลิกปลายทางแล้ว"));
    }

    /// <summary>รอบ 254 WP-11 L2 (คำตอบเจ้าของ Q6 ทางลัด): สร้างลิงก์/QR ผูก LINE ของผู้ติดต่อ (ผ่าน LINE OA ของบริษัท · ผู้รับยินยอมบนมือถือ) — ด่าน Contact.Edit</summary>
    [HttpPost("line-invite")]
    [RejectApiKey("สร้างลิงก์ผูก LINE ของผู้ติดต่อ")]
    public async Task<ActionResult<ApiResponse<ContactLineInviteResponse>>> CreateLineInvite(Guid companyId, Guid contactId, CancellationToken ct)
    {
        if (await DenyKeyAsync(companyId, "สร้างลิงก์ผูก LINE ของผู้ติดต่อ") is { } deny)
            return StatusCode(403, new ApiResponse<ContactLineInviteResponse>(false, null, deny));
        if (_lineBinding == null)
            return StatusCode(503, new ApiResponse<ContactLineInviteResponse>(false, null, "บริการผูก LINE ของผู้ติดต่อยังไม่พร้อม"));
        var actor = Actor();
        var r = await _lineBinding.CreateInviteAsync(companyId, contactId, actor.UserId, actor.Email, ct);
        return StatusCode(201, new ApiResponse<ContactLineInviteResponse>(true, r, "สร้างลิงก์ผูก LINE แล้ว"));
    }

    private async Task<string?> DenyKeyAsync(Guid companyId, string verb)
    {
        var allowed = await _permissions.HasPermissionAsync(companyId, JwtHelper.GetUserIdFromClaims(User), PermissionKeys.ContactEdit);
        return allowed ? null : $"ไม่มีสิทธิ์{verb} (ต้องการ {PermissionKeys.ContactEdit.Replace("perm:", "")})";
    }

    private ActionResult Forbid403(string message)
        => StatusCode(403, new ApiResponse<ContactNotifyOverviewResponse>(false, null, message));

    private ContactNotifyActor Actor()
    {
        Guid? uid = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var u) ? u : null;
        return new ContactNotifyActor(uid, User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name,
            HttpContext?.Connection?.RemoteIpAddress?.ToString());
    }
}
