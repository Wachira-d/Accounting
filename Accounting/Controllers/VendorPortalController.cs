using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Portal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// Two surfaces:
///   1. Admin (authorized) — issue / revoke / list portal tokens.
///   2. Public (token in body / header) — vendor uses their magic-
///      link to see scoped data + submit invoices. Anonymous: NOT
///      gated by [Authorize], protected by token-hash validation.
/// </summary>
[ApiController]
public class VendorPortalController : ControllerBase
{
    private readonly IVendorPortalService _svc;
    private readonly Accounting.Data.AccountingDbContext _db;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;

    public VendorPortalController(IVendorPortalService svc, Accounting.Data.AccountingDbContext db,
        Microsoft.Extensions.Configuration.IConfiguration config)
    { _svc = svc; _db = db; _config = config; }

    // ── Admin surface (authorized) ───────────────────────────────────
    public sealed record IssueTokenRequest(Guid ContactId, string Role, int? ValidDays,
        string? RecipientEmail);

    /// <summary>รอบ 259-3 ฝ่ายค้าน M3: ออกลิงก์ = เปิดใบสั่งซื้อ/ใบแจ้งหนี้ของผู้ติดต่อให้คนนอก + ส่งอีเมลในนามบริษัท ⇒ สิทธิ์แก้ผู้ติดต่อ (Contact.Edit ·
    /// ระดับเดียวกับให้สิทธิ์พอร์ทัลลูกค้า) · คีย์ API ห้าม · อีเมลเข้าคิว (โควตา) ไม่ส่งตรงจากคำขอ</summary>
    [Authorize]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.ContactEdit)]
    [Accounting.Filters.RejectApiKey("ออกลิงก์พอร์ทัลผู้ขาย")]
    [HttpPost("api/companies/{companyId:guid}/vendor-portal/tokens")]
    public async Task<ActionResult<ApiResponse<object>>> Issue(
        Guid companyId, [FromBody] IssueTokenRequest req, CancellationToken ct)
    {
        try
        {
            var userId = Helpers.JwtHelper.GetUserIdFromClaims(User);
            var (raw, stored) = await _svc.IssueAsync(companyId, req.ContactId,
                req.Role, req.ValidDays, req.RecipientEmail, userId, ct);
            // รอบ 259-3 (E-H2 · E 4.6): ลิงก์เต็มจาก URL ของระบบ (SystemBaseUrl ตัวเดียว) — หน้าเว็บไม่ประกอบจาก location.origin
            var origin = await Helpers.SystemBaseUrl.OriginForRequestAsync(_db, _config, Request.Scheme, Request.Host.ToString(), ct);
            var url = $"{origin}/vendor-portal.html?t={raw}";
            // ช่องอีเมลผู้รับ = เข้าคิวส่งจริง (เดิมเก็บอย่างเดียว · silent no-op) · ลิงก์ในอีเมลเป็นของผู้รับ (ออกตอนส่ง) · ผลคืนให้หน้าแสดง
            var invite = await _svc.QueueInviteAsync(companyId, stored, ct);
            return Ok(new ApiResponse<object>(true, new
            {
                tokenId = stored.Id,
                rawToken = raw,            // shown ONCE — admin must copy/send
                expiresAt = stored.ExpiresAt,
                portalUrl = url,
                stored.Role,
                inviteRequested = invite.Requested,
                inviteQueued = invite.Queued,
                inviteNote = invite.Note,
            }, "ออกลิงก์แล้ว — ลิงก์แสดงครั้งเดียว · " + invite.Note));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    /// <summary>รอบ 259-3 (E-H2): ลิงก์ที่ออกแล้ว (ผู้ติดต่อ · หมดอายุ · ใช้ล่าสุด · สถานะ · เพิกถอนได้ไหม) — ไม่มี token ดิบ</summary>
    [Authorize]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.ContactEdit)]
    [HttpGet("api/companies/{companyId:guid}/vendor-portal/tokens")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<VendorPortalTokenRow>>>> List(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<IReadOnlyList<VendorPortalTokenRow>>(true, await _svc.ListAsync(companyId, ct)));

    public sealed record RevokeTokenRequest(string? Reason);

    /// <summary>รอบ 259-3 ฝ่ายค้าน L6: เพิกถอนลิงก์บทบาทลูกค้าเดิม (ก่อนรอบ 259) ทั้งหมด — ปิดประตูที่สองของลูกค้าก่อนหมดอายุ</summary>
    [Authorize]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.ContactEdit)]
    [Accounting.Filters.RejectApiKey("เพิกถอนลิงก์พอร์ทัล")]
    [HttpPost("api/companies/{companyId:guid}/vendor-portal/tokens/revoke-legacy-customer")]
    public async Task<ActionResult<ApiResponse<object>>> RevokeLegacyCustomer(Guid companyId, CancellationToken ct)
    {
        var n = await _svc.RevokeLegacyCustomerAsync(companyId, "", ct);
        return Ok(new ApiResponse<object>(true, new { revoked = n }, n > 0 ? $"เพิกถอนลิงก์ลูกค้าเดิม {n} รายการแล้ว" : "ไม่มีลิงก์ลูกค้าเดิมที่ยังใช้ได้"));
    }

    [Authorize]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.ContactEdit)]
    [Accounting.Filters.RejectApiKey("เพิกถอนลิงก์พอร์ทัล")]
    [HttpPost("api/companies/{companyId:guid}/vendor-portal/tokens/{tokenId:guid}/revoke")]
    public async Task<ActionResult<ApiResponse<object>>> Revoke(
        Guid companyId, Guid tokenId, [FromBody] RevokeTokenRequest? req, CancellationToken ct)
    {
        try
        {
            await _svc.RevokeAsync(companyId, tokenId, req?.Reason ?? "", ct);
            return Ok(new ApiResponse<object>(true, new { revoked = true }, "เพิกถอนลิงก์แล้ว — ผู้ขายเปิดลิงก์นี้ไม่ได้อีก"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    // ── Public surface (token-only, anonymous) ───────────────────────
    public sealed record TokenAuthRequest(string Token);

    /// <summary>Resolve a token from the URL fragment → return scoped
    /// summary the vendor can act on. No [Authorize] — token IS the
    /// auth.</summary>
    [HttpPost("api/portal/summary")]
    public async Task<ActionResult<ApiResponse<object>>> Summary(
        [FromBody] TokenAuthRequest req, CancellationToken ct)
    {
        var token = await _svc.ResolveAsync(req.Token, ct);
        if (token == null) return Unauthorized(new ApiResponse<object>(false, null,
            "Token หมดอายุหรือไม่ถูกต้อง — ขอ link ใหม่จากฝ่าย AP"));
        var summary = await _svc.GetSummaryAsync(token, ct);
        return Ok(new ApiResponse<object>(true, summary));
    }

    public sealed record SubmitInvoiceRequest(string Token,
        string InvoiceNumber, DateTime InvoiceDate, decimal TotalAmount,
        string? Notes, string? AttachmentUrl);

    [HttpPost("api/portal/invoice")]
    public async Task<ActionResult<ApiResponse<object>>> SubmitInvoice(
        [FromBody] SubmitInvoiceRequest req, CancellationToken ct)
    {
        var token = await _svc.ResolveAsync(req.Token, ct);
        if (token == null) return Unauthorized(new ApiResponse<object>(false, null, "Token หมดอายุ"));
        try
        {
            var draft = await _svc.SubmitInvoiceAsync(token, req.InvoiceNumber,
                req.InvoiceDate, req.TotalAmount, req.Notes, req.AttachmentUrl, ct);
            return Ok(new ApiResponse<object>(true, new
            {
                documentId = draft.DocumentId, documentNumber = draft.DocumentNumber, draft.Status,
            }, "ส่ง invoice ให้ AP review แล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }
}
