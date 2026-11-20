using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 272 แพ็ก P3/P4 — หน้า "สลิปภาษีของลูกค้า" ของ<b>บริษัทผู้ให้บริการ</b> (<c>/pages/tax-slip-desk.html</c>): ส่งเอกสารชำระภาษี (ใบขอชำระ · §9.1) ·
/// คิว "สลิปภาษีรอตรวจ" (§9.2 · 1 คลิกของพนักงาน 272-1-5/6) · สวิตช์รับสลิป · ใช้ได้เฉพาะบริษัทผู้ให้บริการของแพลตฟอร์ม · เขียน = สิทธิ์ยื่นภาษี · ไม่รับ API key
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/tax-slip-desk")]
[Authorize]
public class TaxSlipDeskController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ProviderConsentService _consent;
    private readonly TaxPaymentRequestService _requests;
    private readonly TaxSlipIntakeService _intake;
    private readonly IPermissionService _perms;

    public TaxSlipDeskController(AccountingDbContext db, ProviderConsentService consent, TaxPaymentRequestService requests, TaxSlipIntakeService intake, IPermissionService perms)
    { _db = db; _consent = consent; _requests = requests; _intake = intake; _perms = perms; }

    private ObjectResult Fail(BusinessRuleException ex) => StatusCode(ex.StatusCode, new ApiResponse<object>(false, null, ex.Message));

    private async Task<ObjectResult?> NotProviderAsync(Guid companyId, CancellationToken ct)
        => await _consent.ProviderIdAsync(ct) == companyId ? null
            : StatusCode(403, new ApiResponse<object>(false, null, "หน้านี้ใช้ได้เฉพาะบริษัทผู้ให้บริการ (สำนักงานบัญชี NextAcc)"));

    public sealed record Overview(bool Enabled, bool ReaderAvailable, string? ReaderNote, List<TaxPaymentRequestService.ClientOption> Clients,
        List<TaxPaymentRequestService.RequestRow> Requests, List<TaxSlipIntakeService.StaffRow> Queue, IReadOnlyList<ProviderConsent.TaxTypeDef> TaxTypes);

    [HttpGet]
    [Accounting.Filters.RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<Overview>>> Get(Guid companyId, [FromQuery] bool all, [FromServices] ISlipImageReader reader, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        var enabled = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId).Select(s => (bool?)s.TaxSlipIntakeEnabled).FirstOrDefaultAsync(ct) ?? true;
        return Ok(new ApiResponse<Overview>(true, new Overview(enabled, reader.IsAvailable,
            reader.IsAvailable ? null : "เครื่องนี้อ่านข้อความจากรูปไม่ได้ (ไม่มี tessdata) — ทุกสลิปจากกลุ่มที่ผูกแล้วเข้าคิวพนักงานพร้อมเหตุ \"" + TaxSlipIntakeService.OcrOffReason + "\"",
            await _requests.ClientsAsync(companyId, ct), await _requests.ListAsync(companyId, ct), await _intake.StaffQueueAsync(companyId, !all, ct),
            ProviderConsent.TaxTypes.Where(t => t.ReaderWired).ToList())));
    }

    [HttpGet("clients/{contactId:guid}/pending")]
    [Accounting.Filters.RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<object>>> Pending(Guid companyId, Guid contactId, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        try { return Ok(new ApiResponse<object>(true, await _requests.PendingAsync(companyId, contactId, ct))); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    [HttpPost("requests")]
    [Accounting.Filters.RequirePermission(PermissionKeys.TaxFile)]
    [Accounting.Filters.RejectApiKey("ส่งเอกสารชำระภาษีให้ลูกค้า")]
    public async Task<ActionResult<ApiResponse<object>>> CreateRequest(Guid companyId, [FromBody] TaxPaymentRequestService.CreateInput req, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            var r = await _requests.CreateAsync(companyId, userId, req, ct);
            return Ok(new ApiResponse<object>(true, r, "สร้างใบขอชำระแล้ว · " + (string.IsNullOrWhiteSpace(r.SendOutcome) ? "ไม่มีปลายทางให้ส่ง" : r.SendOutcome)
                + (r.BooksMismatch ? " · ⚠ สมุดไม่ตรง: สลิปของใบนี้จะเข้าคิวพนักงาน" : "")));
        }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    [HttpPost("requests/{requestId:guid}/cancel")]
    [Accounting.Filters.RequirePermission(PermissionKeys.TaxFile)]
    [Accounting.Filters.RejectApiKey("ยกเลิกใบขอชำระภาษี")]
    public async Task<ActionResult<ApiResponse<object>>> CancelRequest(Guid companyId, Guid requestId, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        try { await _requests.CancelAsync(companyId, JwtHelper.GetUserIdFromClaims(User), requestId, ct); return Ok(new ApiResponse<object>(true, null, "ยกเลิกใบขอชำระแล้ว")); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    public sealed record RemitInput(int CandidateIndex, Guid BankAccountId);

    /// <summary>พนักงาน 1 คลิก — เลือกจากผู้สมัครที่ระบบเสนอ + บัญชีที่จ่าย · ด่านความยินยอม/หนังสือมอบอำนาจ/สมาชิกผู้ยื่นภาษีตรวจใต้ล็อก</summary>
    [HttpPost("slips/{slipId:guid}/remit")]
    [Accounting.Filters.RequirePermission(PermissionKeys.TaxFile)]
    [Accounting.Filters.RejectApiKey("บันทึกนำส่งจากสลิปภาษีของลูกค้า")]
    public async Task<ActionResult<ApiResponse<object>>> Remit(Guid companyId, Guid slipId, [FromBody] RemitInput req, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            // ฝ่ายค้าน 272 M4: สิทธิ์ผู้ยื่นภาษีของลูกค้าตรวจใน service กับบริษัทของผู้สมัครที่เลือก
            await _intake.StaffRemitAsync(companyId, userId, slipId, req.CandidateIndex, req.BankAccountId, ct);
            return Ok(new ApiResponse<object>(true, null, "บันทึกนำส่งในบัญชีลูกค้าแล้ว · แจ้งผลในกลุ่ม/อีเมลแล้ว"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    /// <summary>รอบ 275 แพ็ก D (H1): ตัวเลือกของ "บันทึกเอง" / "ผูกกับรายการนำส่งที่มีอยู่" (server คำนวณ · หน้าแสดง)</summary>
    [HttpGet("slips/{slipId:guid}/manual-options")]
    [Accounting.Filters.RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<object>>> ManualOptions(Guid companyId, Guid slipId, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        try { return Ok(new ApiResponse<object>(true, await _intake.ManualOptionsAsync(companyId, slipId, ct))); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    /// <summary>รอบ 275 แพ็ก D (H1 · คำตัดสิน 4.3): พนักงานบันทึกนำส่งเองจากสลิปที่ระบบจับคู่ไม่ได้ — ยอดต้องเท่าสลิปทุกสตางค์ · audit · ไม่มีผู้อนุมัติคนที่สอง</summary>
    [HttpPost("slips/{slipId:guid}/manual-remit")]
    [Accounting.Filters.RequirePermission(PermissionKeys.TaxFile)]
    [Accounting.Filters.RejectApiKey("บันทึกนำส่งเองจากสลิปภาษีของลูกค้า")]
    public async Task<ActionResult<ApiResponse<object>>> ManualRemit(Guid companyId, Guid slipId, [FromBody] TaxSlipIntakeService.ManualInput req, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            await _intake.StaffManualRemitAsync(companyId, userId, slipId, req, ct);
            return Ok(new ApiResponse<object>(true, null, "บันทึกนำส่งในบัญชีลูกค้าแล้ว (พนักงานเลือกแบบ/งวด/ยอดเอง) · แจ้งผลในกลุ่ม/อีเมลแล้ว"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    public sealed record LinkInput(Guid ClientCompanyId, Guid RemittanceId, bool ConfirmTaxId = false);

    /// <summary>รอบ 275 แพ็ก D (H1): ปิดสลิปด้วยรายการนำส่งที่บันทึกไว้แล้ว — แนบสลิปเป็นหลักฐาน · การ์ด "บันทึกแล้ว" ตามจริง</summary>
    [HttpPost("slips/{slipId:guid}/link")]
    [Accounting.Filters.RequirePermission(PermissionKeys.TaxFile)]
    [Accounting.Filters.RejectApiKey("ผูกสลิปภาษีกับรายการนำส่งของลูกค้า")]
    public async Task<ActionResult<ApiResponse<object>>> Link(Guid companyId, Guid slipId, [FromBody] LinkInput req, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            await _intake.StaffLinkAsync(companyId, userId, slipId, req.ClientCompanyId, req.RemittanceId, ct, req.ConfirmTaxId);
            return Ok(new ApiResponse<object>(true, null, "ผูกสลิปกับรายการนำส่งแล้ว · แจ้งผลในกลุ่ม/อีเมลแล้ว"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    public sealed record RejectInput(string? Reason);

    [HttpPost("slips/{slipId:guid}/reject")]
    [Accounting.Filters.RequirePermission(PermissionKeys.TaxFile)]
    [Accounting.Filters.RejectApiKey("ปิดสลิปภาษีโดยไม่บันทึก")]
    public async Task<ActionResult<ApiResponse<object>>> Reject(Guid companyId, Guid slipId, [FromBody] RejectInput req, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        try { await _intake.StaffRejectAsync(companyId, JwtHelper.GetUserIdFromClaims(User), slipId, req.Reason ?? "", ct); return Ok(new ApiResponse<object>(true, null, "ปิดสลิปแล้ว · แจ้งผลในกลุ่มแล้ว")); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    /// <summary>ปุ่ม "ส่งคำขออนุญาตอีกครั้ง" (ทาง A เป็นทางเสริมของทาง B) — กระดิ่ง/อีเมลถึงเจ้าของลูกค้า + การ์ดในกลุ่ม</summary>
    [HttpPost("clients/{contactId:guid}/request-consent")]
    [Accounting.Filters.RequirePermission(PermissionKeys.TaxFile)]
    [Accounting.Filters.RejectApiKey("ส่งคำขออนุญาตถึงลูกค้า")]
    public async Task<ActionResult<ApiResponse<object>>> RequestConsent(Guid companyId, Guid contactId, [FromServices] TaxSlipChannelService channel, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        var client = (await _requests.ClientsAsync(companyId, ct)).FirstOrDefault(c => c.ContactId == contactId);
        if (client == null) return NotFound(new ApiResponse<object>(false, null, "ผู้ติดต่อนี้ไม่ใช่บริษัทลูกค้า"));
        var (req, isNew) = await _consent.EnsureRequestAsync(client.ClientCompanyId, companyId, ProviderConsentService.ViaStaff, JwtHelper.GetUserIdFromClaims(User), ct);
        if (req == null) return Ok(new ApiResponse<object>(true, null, "บริษัทนี้อนุญาตไว้แล้ว — ไม่ต้องขอ"));
        // รอบ 279 (279-3): ลิงก์จริงของหน้ายืนยัน (เดิมชี้เมนู "ค่าบริการ" ที่ไม่มี) — ตัวประกอบเดียว ProviderConsent.GrantLinkOrMenu
        var link = ProviderConsent.GrantLinkOrMenu(await SystemBaseUrl.ResolveAsync(_db, null, ct), client.ClientCompanyId);
        var sent = await channel.PostToClientAsync(companyId, client.ClientCompanyId,
            _ => TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.BoundAskConsent, new(CompanyName: client.Name, Link: link), false), ct);
        return Ok(new ApiResponse<object>(true, new { isNew, sent }, (isNew ? "ส่งคำขอแล้ว" : "มีคำขอเปิดอยู่แล้ว — ส่งข้อความเตือนในกลุ่ม") + " · " + sent));
    }

    /// <summary>272-1-6: แนบหนังสือมอบอำนาจที่ลูกค้าลงนาม ⇒ พนักงานกดบันทึกทีละใบได้ (ไม่เปิดการบันทึกเอง)</summary>
    [HttpPost("clients/{contactId:guid}/power-of-attorney")]
    [RequestSizeLimit(15 * 1024 * 1024)]
    [Accounting.Filters.RequirePermission(PermissionKeys.TaxFile)]
    [Accounting.Filters.RejectApiKey("แนบหนังสือมอบอำนาจของลูกค้า")]
    public async Task<ActionResult<ApiResponse<object>>> PowerOfAttorney(Guid companyId, Guid contactId, IFormFile file, [FromServices] IFileAttachmentService files, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        if (file == null || file.Length == 0) return BadRequest(new ApiResponse<object>(false, null, "ไม่พบไฟล์"));
        var client = (await _requests.ClientsAsync(companyId, ct)).FirstOrDefault(c => c.ContactId == contactId);
        if (client == null) return NotFound(new ApiResponse<object>(false, null, "ผู้ติดต่อนี้ไม่ใช่บริษัทลูกค้า"));
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var kind = UploadFileType.SniffAttachment(bytes.AsSpan(0, Math.Min(bytes.Length, UploadFileType.HeaderBytes)), file.FileName)
            ?? throw new UnsupportedUploadException(UploadFileType.AttachmentRejectMessage);
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var att = await files.UploadBytesAsync(companyId, "Contact", contactId, "power-of-attorney" + kind.Extension, kind.ContentType, bytes, userId);
        try
        {
            var row = await _consent.RecordPowerOfAttorneyAsync(companyId, client.ClientCompanyId, userId, att.Id, ct);
            return Ok(new ApiResponse<object>(true, new { id = row.Id }, "บันทึกหนังสือมอบอำนาจแล้ว — รอพนักงานอีกคนอนุมัติ · ไม่เปิดการบันทึกเอง (กดบันทึกทีละสลิป)"));
        }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    /// <summary>ฝ่ายค้าน 272 M4: ผู้อนุมัติคนที่สองของหนังสือมอบอำนาจ (ไม่ใช่ผู้แนบ) — กระดิ่งเจ้าของลูกค้า</summary>
    [HttpPost("clients/{contactId:guid}/power-of-attorney/{consentId:guid}/approve")]
    [Accounting.Filters.RequirePermission(PermissionKeys.TaxFile)]
    [Accounting.Filters.RejectApiKey("อนุมัติหนังสือมอบอำนาจของลูกค้า")]
    public async Task<ActionResult<ApiResponse<object>>> ApprovePowerOfAttorney(Guid companyId, Guid contactId, Guid consentId, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        var client = (await _requests.ClientsAsync(companyId, ct)).FirstOrDefault(c => c.ContactId == contactId);
        if (client == null) return NotFound(new ApiResponse<object>(false, null, "ผู้ติดต่อนี้ไม่ใช่บริษัทลูกค้า"));
        try
        {
            await _consent.ApprovePowerOfAttorneyAsync(companyId, client.ClientCompanyId, JwtHelper.GetUserIdFromClaims(User), consentId, ct);
            return Ok(new ApiResponse<object>(true, null, "อนุมัติหนังสือมอบอำนาจแล้ว — พนักงานกดบันทึกทีละสลิปได้ · แจ้งเจ้าของลูกค้าแล้ว"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    public sealed record SettingsInput(bool Enabled);

    /// <summary>สวิตช์รับสลิปภาษี (ดีไซน์ §8.1) — ผู้อ่าน <see cref="TaxSlipIntakeService"/></summary>
    [HttpPut("settings")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ตั้งค่ารับสลิปภาษี")]
    public async Task<ActionResult<ApiResponse<object>>> Settings(Guid companyId, [FromBody] SettingsInput req, CancellationToken ct)
    {
        if (await NotProviderAsync(companyId, ct) is { } np) return np;
        var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId, ct);
        if (s == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบค่าตั้งบริษัท"));
        var before = s.TaxSlipIntakeEnabled;
        s.TaxSlipIntakeEnabled = req.Enabled;
        _db.AddChainedAuditLog(new Accounting.Models.Entities.AuditLog
        {
            CompanyId = companyId, UserId = JwtHelper.GetUserIdFromClaims(User), Action = Accounting.Models.Enums.AuditAction.Update, EntityType = "CompanySettings",
            EntityId = companyId.ToString(), OldValues = "{\"taxSlipIntakeEnabled\":" + (before ? "true" : "false") + "}",
            NewValues = "{\"taxSlipIntakeEnabled\":" + (req.Enabled ? "true" : "false") + "}", Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, new { enabled = s.TaxSlipIntakeEnabled }, req.Enabled ? "เปิดรับสลิปภาษีแล้ว" : "ปิดรับสลิปภาษีแล้ว — ลูกค้าที่เคยอนุญาตจะได้คำตอบว่าปิดรับชั่วคราว"));
    }
}
