using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Portal;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

// Admin endpoints
/// <summary>รอบ 257 D-H1: ให้/แก้/ปิดสิทธิ์พอร์ทัล = เปิดเอกสารทั้งหมดของผู้ติดต่อให้อีเมลภายนอก ⇒ ต้องมีสิทธิ์แก้ผู้ติดต่อ (Contact.Edit) ·
/// คีย์ API ห้าม (ไม่มีผู้ใช้ให้ประทับใน audit) · ทุกการเขียนลง audit chain (ผู้ทำ · ธงก่อน/หลัง) ใน service — เดิมมีแค่ [Authorize] ⇒ สมาชิกคนไหนก็
/// ให้สิทธิ์พอร์ทัลของผู้ติดต่อใดก็ได้ถึงอีเมลตัวเองแล้วดาวน์โหลดเอกสารทั้งหมด</summary>
[ApiController]
[Route("api/companies/{companyId:guid}/portal")]
[Authorize]
public class PortalAdminController : ControllerBase
{
    private readonly IPortalService _service;
    public PortalAdminController(IPortalService service) => _service = service;

    private Guid ActorId => Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User);

    [HttpPost("access")]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.ContactEdit)]
    [Accounting.Filters.RejectApiKey("ให้สิทธิ์พอร์ทัลลูกค้า")]
    public async Task<ActionResult<ApiResponse<PortalAccessResponse>>> CreateAccess(Guid companyId, [FromBody] CreatePortalAccessRequest request)
        => StatusCode(201, new ApiResponse<PortalAccessResponse>(true, await _service.CreateAccessAsync(companyId, request, ActorId)));

    [HttpGet("access")]
    public async Task<ActionResult<ApiResponse<List<PortalAccessResponse>>>> GetAccesses(Guid companyId)
        => Ok(new ApiResponse<List<PortalAccessResponse>>(true, await _service.GetAccessesAsync(companyId)));

    [HttpPut("access/{accessId:guid}")]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.ContactEdit)]
    [Accounting.Filters.RejectApiKey("แก้สิทธิ์พอร์ทัลลูกค้า")]
    public async Task<ActionResult<ApiResponse<PortalAccessResponse>>> UpdateAccess(Guid companyId, Guid accessId, [FromBody] UpdatePortalAccessRequest request)
        => Ok(new ApiResponse<PortalAccessResponse>(true, await _service.UpdateAccessAsync(companyId, accessId, request, ActorId)));

    [HttpPost("access/{accessId:guid}/deactivate")]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.ContactEdit)]
    [Accounting.Filters.RejectApiKey("ปิดสิทธิ์พอร์ทัลลูกค้า")]
    public async Task<ActionResult<ApiResponse<bool>>> Deactivate(Guid companyId, Guid accessId)
    { await _service.DeactivateAccessAsync(companyId, accessId, ActorId); return Ok(new ApiResponse<bool>(true, true)); }
}

// Public portal endpoints
[ApiController]
[Route("api/portal")]
public class PortalPublicController : ControllerBase
{
    private readonly IPortalService _service;
    /// <summary>รอบ 255 WP-12B ข้อ 8: เพดานการขอ/แลกลิงก์วิเศษ (ตัวนับในฐาน — หลายเครื่องใช้ตัวเดียว) · null (สร้างนอก DI) = ปฏิเสธเส้นลิงก์วิเศษ</summary>
    private readonly IChatRateLimiter? _limiter;
    public PortalPublicController(IPortalService service, IChatRateLimiter? limiter = null) { _service = service; _limiter = limiter; }

    public record MagicLinkRequest(Guid CompanyId, string? Email);
    public record MagicRedeemRequest(Guid CompanyId, string? Token);

    /// <summary>เวลาตอบขั้นต่ำของการขอลิงก์ (กันวัดเวลาแยก "อีเมลมีสิทธิ์/ไม่มี")</summary>
    private static readonly TimeSpan MagicMinimumDuration = TimeSpan.FromMilliseconds(800);

    /// <summary>รอบ 255 WP-12B ข้อ 8 — ขอลิงก์เข้าสู่ระบบทางอีเมล (ไม่ต้องใช้รหัสผ่าน) · <b>คำตอบเดียวทุกกรณี</b> 202 ·
    /// เพดานต่ออีเมล + ต่อ IP (fail-closed · ฝ่ายค้าน M7) · ส่งเฉพาะอีเมลที่ผู้ขายเปิดสิทธิ์พอร์ทัลไว้ · อีเมลเข้าคิว (M6) · เวลาตอบขั้นต่ำคงเดิม</summary>
    [HttpPost("magic-link")]
    [RequestSizeLimit(4 * 1024)]
    public async Task<ActionResult<ApiResponse<object>>> RequestMagicLink([FromBody] MagicLinkRequest req, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var email = Accounting.Helpers.PortalMagicLinkPolicy.NormalizeEmail(req?.Email);
        if (req == null || req.CompanyId == Guid.Empty || _limiter == null)
            return BadRequest(new ApiResponse<object>(false, null, "คำขอไม่ถูกต้อง"));
        // รอบ 255 ฝ่ายค้าน M7: ต่อ IP + ต่ออีเมล (ไม่มีถังรวมของบริษัท — คนนอกทำให้ลูกค้าทุกคนของผู้ขายขอลิงก์ไม่ได้)
        var ip = HttpContext?.Connection?.RemoteIpAddress?.ToString();
        if (!await _limiter.TryConsumeAsync(Accounting.Helpers.PortalMagicLinkPolicy.IpBucket("req", ip),
                Accounting.Helpers.PortalMagicLinkPolicy.PerIpPerMinute, Accounting.Helpers.PortalMagicLinkPolicy.PerIpPerDay, ct, failClosed: true)
            || !await _limiter.TryConsumeAsync(Accounting.Helpers.PortalMagicLinkPolicy.EmailBucket(req.CompanyId, email),
                Accounting.Helpers.PortalMagicLinkPolicy.PerEmailPerMinute, Accounting.Helpers.PortalMagicLinkPolicy.PerEmailPerDay, ct, failClosed: true))
            return StatusCode(429, new ApiResponse<object>(false, null, Accounting.Helpers.PortalMagicLinkPolicy.RateMessage));
        try { await _service.RequestMagicLinkAsync(req.CompanyId, email, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ล้ม (ฐาน/อีเมล) ⇒ บันทึกฝั่งเซิร์ฟเวอร์ · คำตอบถึงผู้ขอยังเหมือนเดิม (ไม่รั่วว่าอีเมลมีสิทธิ์)
            System.Diagnostics.Trace.TraceError("Portal magic link request failed: {0}", ex);
        }
        var rest = MagicMinimumDuration - sw.Elapsed;
        if (rest > TimeSpan.Zero) await Task.Delay(rest, ct);
        return StatusCode(202, new ApiResponse<object>(true, null, Accounting.Helpers.PortalMagicLinkPolicy.AckMessage));
    }

    /// <summary>รอบ 255 WP-12B ข้อ 8 — แลกลิงก์วิเศษเป็นเซสชันพอร์ทัล (ใช้ครั้งเดียว · อายุ 15 นาที) · ข้อความเดียวทุกเหตุที่แลกไม่ได้</summary>
    [HttpPost("magic-link/redeem")]
    [RequestSizeLimit(4 * 1024)]
    public async Task<ActionResult<ApiResponse<PortalLoginResponse>>> RedeemMagicLink([FromBody] MagicRedeemRequest req, CancellationToken ct)
    {
        if (req == null || req.CompanyId == Guid.Empty || _limiter == null)
            return BadRequest(new ApiResponse<PortalLoginResponse>(false, null, Accounting.Helpers.PortalMagicLinkPolicy.InvalidMessage));
        // รอบ 255 ฝ่ายค้าน M7: ต่อ IP + ต่อโทเคน (เดิมถังเดียวต่อบริษัท — คนนอกยิงขยะ 30 ครั้ง/นาทีแล้วลูกค้าจริงแลกลิงก์ไม่ได้)
        var ip = HttpContext?.Connection?.RemoteIpAddress?.ToString();
        if (!await _limiter.TryConsumeAsync(Accounting.Helpers.PortalMagicLinkPolicy.IpBucket("redeem", ip),
                Accounting.Helpers.PortalMagicLinkPolicy.RedeemPerIpPerMinute, Accounting.Helpers.PortalMagicLinkPolicy.RedeemPerIpPerDay, ct, failClosed: true)
            || !await _limiter.TryConsumeAsync(Accounting.Helpers.PortalMagicLinkPolicy.TokenBucket(req.Token),
                Accounting.Helpers.PortalMagicLinkPolicy.RedeemPerTokenPerMinute, Accounting.Helpers.PortalMagicLinkPolicy.RedeemPerTokenPerDay, ct, failClosed: true))
            return StatusCode(429, new ApiResponse<PortalLoginResponse>(false, null, Accounting.Helpers.PortalMagicLinkPolicy.RateMessage));
        try
        {
            return Ok(new ApiResponse<PortalLoginResponse>(true, await _service.RedeemMagicLinkAsync(req.CompanyId, req.Token, ct)));
        }
        catch (Accounting.Helpers.BusinessRuleException ex)
        {
            return BadRequest(new ApiResponse<PortalLoginResponse>(false, null, ex.Message));
        }
    }

    /// <summary>รอบ 255 WP-12B ข้อ 8 — ส่งออก Excel (รูปแบบทั่วไป) ให้นักบัญชีของลูกค้า — ต้องมีโทเคนพอร์ทัล · เอกสารของผู้ติดต่อในโทเคนเท่านั้น</summary>
    [HttpGet("{companyId:guid}/export/accountant")]
    [Authorize(AuthenticationSchemes = Accounting.Helpers.PortalTokenPolicy.Scheme)]
    public async Task<ActionResult> ExportAccountant(Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate, CancellationToken ct)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized();
        if (await DenyPortalAsync(companyId, "export", ct) is { } deny) return Forbidden<object>(deny);
        try
        {
            var bytes = await _service.ExportAccountantLedgerAsync(companyId, contactId.Value, fromDate, toDate, ct);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"documents-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.xlsx");
        }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<PortalLoginResponse>>> Login([FromBody] PortalLoginRequest request)
        => Ok(new ApiResponse<PortalLoginResponse>(true, await _service.LoginAsync(request)));

    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<PortalLoginResponse>>> Refresh([FromBody] PortalRefreshRequest request)
        => Ok(new ApiResponse<PortalLoginResponse>(true, await _service.RefreshTokenAsync(request.RefreshToken)));

    [HttpGet("{companyId:guid}/payments")]
    [Authorize(AuthenticationSchemes = Accounting.Helpers.PortalTokenPolicy.Scheme)]
    public async Task<ActionResult<ApiResponse<List<PortalPaymentResponse>>>> GetPayments(Guid companyId)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized(new ApiResponse<List<PortalPaymentResponse>>(false, null!, "Invalid portal token"));
        if (await DenyPortalAsync(companyId, "payments") is { } deny) return Forbidden<List<PortalPaymentResponse>>(deny);
        return Ok(new ApiResponse<List<PortalPaymentResponse>>(true, await _service.GetMyPaymentsAsync(companyId, contactId.Value)));
    }

    [HttpGet("{companyId:guid}/documents")]
    [Authorize(AuthenticationSchemes = Accounting.Helpers.PortalTokenPolicy.Scheme)]
    public async Task<ActionResult<ApiResponse<List<PortalDocumentResponse>>>> GetDocuments(Guid companyId, [FromQuery] string? type)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized(new ApiResponse<List<PortalDocumentResponse>>(false, null!, "Invalid portal token"));
        if (await DenyPortalAsync(companyId, "documents") is { } deny) return Forbidden<List<PortalDocumentResponse>>(deny);
        var docs = await _service.GetMyDocumentsAsync(companyId, contactId.Value, type);
        // ปุ่ม "ชำระ" ต้องตรงธงชำระของสิทธิ์นี้ด้วย (ไม่โชว์ปุ่มที่กดแล้วได้ 403)
        if (await DenyPortalAsync(companyId, "pay") != null) docs = docs.Select(d => d with { CanPay = false }).ToList();
        return Ok(new ApiResponse<List<PortalDocumentResponse>>(true, docs));
    }

    [HttpGet("{companyId:guid}/documents/{documentId:guid}")]
    [Authorize(AuthenticationSchemes = Accounting.Helpers.PortalTokenPolicy.Scheme)]
    public async Task<ActionResult<ApiResponse<PortalDocumentResponse>>> GetDocument(Guid companyId, Guid documentId)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized(new ApiResponse<PortalDocumentResponse>(false, null!, "Invalid portal token"));
        if (await DenyPortalAsync(companyId, "document") is { } deny) return Forbidden<PortalDocumentResponse>(deny);
        return Ok(new ApiResponse<PortalDocumentResponse>(true, await _service.GetDocumentAsync(companyId, contactId.Value, documentId)));
    }

    [HttpGet("{companyId:guid}/documents/{documentId:guid}/pdf")]
    [Authorize(AuthenticationSchemes = Accounting.Helpers.PortalTokenPolicy.Scheme)]
    public async Task<ActionResult> DownloadPdf(Guid companyId, Guid documentId)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized();
        if (await DenyPortalAsync(companyId, "pdf") is { } deny) return Forbidden<object>(deny);
        var pdf = await _service.DownloadDocumentPdfAsync(companyId, contactId.Value, documentId);
        return File(pdf, "application/pdf");
    }

    [HttpGet("{companyId:guid}/statement")]
    [Authorize(AuthenticationSchemes = Accounting.Helpers.PortalTokenPolicy.Scheme)]
    public async Task<ActionResult<ApiResponse<PortalStatementResponse>>> GetStatement(Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized(new ApiResponse<PortalStatementResponse>(false, null!, "Invalid portal token"));
        if (await DenyPortalAsync(companyId, "statement") is { } deny) return Forbidden<PortalStatementResponse>(deny);
        return Ok(new ApiResponse<PortalStatementResponse>(true, await _service.GetMyStatementAsync(companyId, contactId.Value, fromDate, toDate)));
    }

    /// <summary>Return PromptPay + bank-account details for the
    /// company so the portal can render QR + transfer instructions
    /// to the customer. Anonymous (no token required) — the same
    /// gateway info is also rendered on the public storefront's
    /// order-success page, so exposing it via portal is no broader.</summary>
    [HttpGet("{companyId:guid}/payment-options")]
    public async Task<ActionResult<ApiResponse<Models.DTOs.Cms.StorefrontPaymentOptions>>> GetPaymentOptions(Guid companyId)
        => Ok(new ApiResponse<Models.DTOs.Cms.StorefrontPaymentOptions>(true, await _service.GetCompanyPaymentOptionsAsync(companyId)));

    /// <summary>รอบ 250 WP-03 (A G-01 · D §9.1): ตัวเลือกชำระของใบนี้ + QR พร้อมเพย์ที่เซิร์ฟเวอร์สร้าง (data URI · ฝังยอดค้างของใบจากฐาน)
    /// — ต้องมีโทเค็นพอร์ทัล และใบต้องเป็นของผู้ติดต่อในโทเค็น (ยอดค้างเป็นข้อมูลของลูกค้ารายนั้น)</summary>
    [HttpGet("{companyId:guid}/documents/{documentId:guid}/payment-options")]
    [Authorize(AuthenticationSchemes = Accounting.Helpers.PortalTokenPolicy.Scheme)]
    public async Task<ActionResult<ApiResponse<Models.DTOs.Cms.StorefrontPaymentOptions>>> GetDocumentPaymentOptions(
        Guid companyId, Guid documentId, CancellationToken ct)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized(new ApiResponse<Models.DTOs.Cms.StorefrontPaymentOptions>(false, null, "Invalid portal token"));
        if (await DenyPortalAsync(companyId, "pay", ct) is { } deny) return Forbidden<Models.DTOs.Cms.StorefrontPaymentOptions>(deny);
        try
        {
            return Ok(new ApiResponse<Models.DTOs.Cms.StorefrontPaymentOptions>(true,
                await _service.GetDocumentPaymentOptionsAsync(companyId, contactId.Value, documentId, ct)));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<Models.DTOs.Cms.StorefrontPaymentOptions>(false, null, ex.Message)); }
    }

    /// <summary>Customer uploads a payment slip for one of their
    /// outstanding invoices. Server enforces the ownership check —
    /// the document's ContactId must match the JWT's contact id, so
    /// one customer can't upload to another customer's document.</summary>
    [HttpPost("{companyId:guid}/documents/{documentId:guid}/upload-slip")]
    [Authorize(AuthenticationSchemes = Accounting.Helpers.PortalTokenPolicy.Scheme)]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<PortalSlipUploadResponse>>> UploadDocumentSlip(
        Guid companyId, Guid documentId, IFormFile? file, [FromForm] decimal? amount, [FromForm] string? bankRef = null)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized(new ApiResponse<PortalSlipUploadResponse>(false, null!, "Invalid portal token"));
        if (await DenyPortalAsync(companyId, "slip") is { } deny) return Forbidden<PortalSlipUploadResponse>(deny);
        if (file == null) return BadRequest(new ApiResponse<PortalSlipUploadResponse>(false, null!, "กรุณาเลือกไฟล์สลิป"));
        try
        {
            var result = await _service.UploadDocumentSlipAsync(companyId, contactId.Value, documentId, file, amount, bankRef);
            return Ok(new ApiResponse<PortalSlipUploadResponse>(true, result, "ส่งสลิปแล้ว — ทางร้านจะตรวจสอบและยืนยันการชำระเงิน"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<PortalSlipUploadResponse>(false, null!, ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<PortalSlipUploadResponse>(false, null!, ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<PortalSlipUploadResponse>(false, null!, ex.Message)); }
        // รอบ 250 ฝ่ายค้าน L4: ข้อผิดพลาดเชิงกฎ (ชำระครบแล้ว · ยอดเกินยอดค้าง · บันทึกไม่สำเร็จ) — สถานะ + ข้อความไทยจากตัวตัดสิน
        catch (Accounting.Helpers.BusinessRuleException ex) { return StatusCode(ex.StatusCode, new ApiResponse<PortalSlipUploadResponse>(false, null!, ex.Message)); }
    }

    /// <summary>รอบ 255 WP-12B ฝ่ายค้าน H1: ตัวตนจาก scheme "Portal" (PortalAuthenticationHandler ตรวจโทเคน access · สิทธิ์ยังเปิด · บริษัทในเส้นทางตรงโทเคนแล้ว)</summary>
    private Guid? GetContactIdFromToken()
        => Guid.TryParse(User.FindFirst(Accounting.Helpers.PortalTokenPolicy.ClaimContactId)?.Value, out var id) ? id : null;

    private Guid? GetAccessIdFromToken()
        => Guid.TryParse(User.FindFirst(Accounting.Helpers.PortalTokenPolicy.ClaimAccessId)?.Value, out var id) ? id : null;

    /// <summary>รอบ 257 D-H3: ธงสิทธิ์ของ<b>สิทธิ์พอร์ทัลในโทเคน</b> (อ่านจากฐานทุกคำขอ) ตามตารางเส้นเดียว <c>PortalAccessPermissions.Route</c> ·
    /// null = ผ่าน · ข้อความ = ปฏิเสธ 403 · ไม่พบสิทธิ์ = ปฏิเสธ (fail-closed)</summary>
    private async Task<string?> DenyPortalAsync(Guid companyId, string route, CancellationToken ct = default)
    {
        var accessId = GetAccessIdFromToken();
        var flags = accessId is Guid a ? await _service.GetAccessFlagsAsync(companyId, a, ct) : null;
        return Accounting.Helpers.PortalAccessPermissions.Refusal(flags, Accounting.Helpers.PortalAccessPermissions.Route(route));
    }

    private ObjectResult Forbidden<T>(string message) => StatusCode(403, new ApiResponse<T>(false, default, message));
}
