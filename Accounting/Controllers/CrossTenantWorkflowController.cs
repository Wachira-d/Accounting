using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Cross-tenant B2B document workflow:
///   • Trading partnership invite / accept / reject (handshake)
///   • Workflow automation config (per-company toggles)
///   • Incoming-document inbox (docs sent from a partner awaiting approval)
///   • Outgoing-document tracker (docs we sent to partners)
///   • Approve / reject incoming docs — triggers signature stamping +
///     auto-creation of downstream documents.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}")]
[Authorize]
public class CrossTenantWorkflowController : ControllerBase
{
    private readonly CrossTenantWorkflowService _service;
    private readonly AccountingDbContext _db;
    private readonly IPermissionService _perms;
    private readonly IChatRateLimiter _limiter;

    public CrossTenantWorkflowController(CrossTenantWorkflowService service, AccountingDbContext db, IPermissionService perms, IChatRateLimiter limiter)
    {
        _service = service;
        _db = db;
        _perms = perms;
        _limiter = limiter;
    }

    /// <summary>403 พร้อมข้อความบอกทางไปต่อ (ตัวสร้างข้อความตัวเดียว <see cref="PermissionKeys.DeniedMessage"/>)</summary>
    private ObjectResult Denied(string key)
        => StatusCode(403, new ApiResponse<object>(false, new { requiredPermission = key.Replace("perm:", "") }, PermissionKeys.DeniedMessage(key)));

    // ═══════════════════════════════════════════════════════════════════
    // TRADING PARTNERSHIPS
    // ═══════════════════════════════════════════════════════════════════

    [HttpGet("trading-partners")]
    public async Task<ActionResult<ApiResponse<object>>> List(Guid companyId)
    {
        var raw = await _db.TradingPartnerships.AsNoTracking()
            .Include(p => p.CompanyA)
            .Include(p => p.CompanyB)
            .Where(p => p.CompanyAId == companyId || p.CompanyBId == companyId)
            // รอบ 250 WP-02b (P-1): ฝั่งผู้เชิญเห็นคู่ค้าเมื่อปลายทางตอบรับแล้วเท่านั้น — แถว Pending/Rejected ที่เราเชิญเองบอก "มีบริษัทเลขนี้ในระบบ"
            // (ชื่อ + เลขภาษี) ซึ่งเป็นสิ่งเดียวกับที่คำตอบของ invite ปิดไว้ · ฝั่งผู้รับเชิญเห็นคำเชิญทุกสถานะตามเดิม (ต้องกดตอบรับได้)
            // รอบ 264: ผู้ขอติดตามคำขอของตัวเองที่ "คำขอที่เราส่ง" (trading-partners/requests/outgoing — ไม่รั่วสมาชิกภาพ) แทน
            .Where(p => p.InvitedByCompanyId != companyId
                || p.Status == TradingPartnershipStatus.Accepted || p.Status == TradingPartnershipStatus.Suspended)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new
            {
                p.Id,
                p.Status,
                Partner = p.CompanyAId == companyId
                    ? new { p.CompanyB.Id, p.CompanyB.Name, p.CompanyB.TaxId, p.CompanyB.BranchCode }
                    : new { p.CompanyA.Id, p.CompanyA.Name, p.CompanyA.TaxId, p.CompanyA.BranchCode },
                IsInviter = p.InvitedByCompanyId == companyId,
                p.InvitationMessage,
                p.AcceptedAt,
                p.RejectedAt,
                p.RejectionReason,
                p.AutoApproveAmountLimit,
                // รอบ 255 WP-12B ข้อ 7: เพดานใบซื้อร่างที่ "เรา" รับจากคู่นี้ (ฝั่งผู้รับ)
                PurchaseDraftLimit = p.CompanyAId == companyId ? p.PurchaseDraftLimitA : p.PurchaseDraftLimitB,
                p.InvitedAt,
                p.CreatedAt,
            })
            .ToListAsync();
        // รอบ 264: ป้าย/ปุ่ม/วันหมดอายุจากตัวตัดสินเดียว (PartnerLinkRequestRules) — หน้าแสดงอย่างเดียว · ส่ง enum เป็นชื่อ
        var now = DateTime.UtcNow;
        var rows = raw.Select(p =>
        {
            var expired = PartnerLinkRequestRules.IsExpired(p.Status, p.InvitedAt, p.CreatedAt, now);
            return new
            {
                p.Id,
                Status = p.Status.ToString(),
                Partner = new { p.Partner.Id, p.Partner.Name, p.Partner.TaxId, BranchLabel = TaxBranchCode.Label(p.Partner.BranchCode) },
                p.IsInviter,
                p.InvitationMessage,
                p.AcceptedAt,
                p.RejectedAt,
                p.RejectionReason,
                p.AutoApproveAmountLimit,
                p.PurchaseDraftLimit,
                StatusLabel = PartnerLinkRequestRules.PartnershipLabel(p.Status, p.IsInviter, expired),
                CanRespond = PartnerLinkRequestRules.CanRespond(p.Status, p.IsInviter, expired),
                Expired = expired,
                ExpiresAt = p.Status == TradingPartnershipStatus.Pending ? PartnerLinkRequestRules.RequestExpiryUtc(p.InvitedAt, p.CreatedAt) : (DateTime?)null,
                p.CreatedAt,
            };
        }).ToList();
        return Ok(new ApiResponse<object>(true, rows));
    }

    /// <summary>รอบ 264 — การ์ดยอมรับของปลายทาง (ลิงก์ในกระดิ่ง/อีเมล/LINE: <c>partners.html?request=&lt;id&gt;</c>) · เฉพาะบริษัทผู้รับของคำขอนี้ ·
    /// ไม่ใช่ผู้รับ/ไม่มีแถว ⇒ 404 ข้อความเดียว (ไม่บอกว่ามีแถว) · สิทธิ์เดียวกับปุ่มยอมรับ</summary>
    [HttpGet("trading-partners/requests/{partnershipId:guid}")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    public async Task<ActionResult<ApiResponse<object>>> GetIncomingRequest(Guid companyId, Guid partnershipId)
    {
        var r = await _service.GetIncomingLinkRequestAsync(companyId, partnershipId, DateTime.UtcNow);
        return r == null
            ? NotFound(new ApiResponse<object>(false, null, IncomingRequestNotFoundMessage))
            : Ok(new ApiResponse<object>(true, r));
    }

    /// <summary>ข้อความเมื่อเปิดลิงก์คำขอแล้วไม่พบในบริษัทที่เลือก — บอกทางไปต่อ (สลับบริษัท) โดยไม่บอกว่ามีแถวในบริษัทอื่น</summary>
    public const string IncomingRequestNotFoundMessage =
        "ไม่พบคำขอเชื่อมนี้ในบริษัทที่เลือกอยู่ — ตรวจว่าเลือกบริษัทที่ได้รับคำขอ (มุมบนของจอ) แล้วเปิดลิงก์อีกครั้ง หรือดูรายการ \"คู่ค้าที่เชื่อม / คำขอที่รอเรายอมรับ\"";

    /// <summary>รอบ 264 — "คำขอที่เราส่ง" (ผู้ขอ) · สถานะ รอตอบ/เชื่อมแล้ว/หมดอายุ เท่านั้น — ไม่บอกว่าเลขนั้นใช้ NextAcc หรือถูกปฏิเสธ (คำตัดสิน 264-1-2)</summary>
    [HttpGet("trading-partners/requests/outgoing")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    public async Task<ActionResult<ApiResponse<object>>> ListOutgoingRequests(Guid companyId)
        => Ok(new ApiResponse<object>(true, await _service.ListOutgoingLinkRequestsAsync(companyId, DateTime.UtcNow)));

    /// <param name="PartnerBranchCode">รหัสสาขา 5 หลักของคู่ค้า (ว่าง = ไม่ระบุ · 00000 = สำนักงานใหญ่) — จับคู่ด้วยเลขภาษี + สาขา (P-2)</param>
    public record InviteRequest(string PartnerTaxId, string? Message, string? PartnerBranchCode = null);

    /// <summary>
    /// เชิญคู่ค้า — รอบ 250 WP-02b (AUTOPILOT_BILLING_DESIGN §6.1 P-1/P-3 · คำตัดสินข้อ 3 PartnerDiscoverable ตั้งต้นปิด)
    /// <para><b>คำตอบเหมือนกันทุกไบต์ทุกกรณี</b> (202 + <see cref="PartnerCompanyMatch.InviteAckMessage"/> · ไม่มี id) — เดิม 404 "ไม่พบบริษัท…" vs 200
    /// ⇒ สมาชิกบริษัทใดก็ไล่เลขภาษีหาว่าใครใช้ NextAcc ได้ · เลขผิดรูป ⇒ 400 จากรูปแบบอย่างเดียว (ไม่ดูฐาน)</para>
    /// สิทธิ์ตั้งค่าบริษัท + ไม่รับ API key (การผูกบริษัทเข้ากับบริษัทอื่นคือการตัดสินใจของคน ไม่ใช่งานของคีย์ — P-3)
    /// </summary>
    [HttpPost("trading-partners/invite")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("เชิญคู่ค้าเชื่อมระบบ")]
    public async Task<ActionResult<ApiResponse<object>>> Invite(Guid companyId, [FromBody] InviteRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        // ฝ่ายค้าน H3: เพดานคำเชิญต่อบริษัทต่อวัน (นับในฐาน — หลายเครื่องใช้ตัวนับเดียว) · ตารางนับพัง = ปฏิเสธ (กันไล่/สแปม) ·
        // คำตอบไม่ขึ้นกับปลายทาง ⇒ ไม่รั่วสมาชิกภาพ
        if (!await _limiter.TryConsumeAsync($"xt-invite:{companyId:N}", CrossTenantWorkflowService.InvitesPerCompanyPerDay,
                CrossTenantWorkflowService.InvitesPerCompanyPerDay, failClosed: true))
            return StatusCode(429, new ApiResponse<object>(false, null,
                $"ส่งคำเชิญครบ {CrossTenantWorkflowService.InvitesPerCompanyPerDay} ครั้งของวันนี้แล้ว — ลองใหม่พรุ่งนี้"));
        await _service.InvitePartnerAsync(companyId, req.PartnerTaxId, req.PartnerBranchCode, userId, req.Message);
        return StatusCode(202, new ApiResponse<object>(true, null, PartnerCompanyMatch.InviteAckMessage));
    }

    [HttpPost("trading-partners/{partnershipId:guid}/accept")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ยอมรับคู่ค้าเชื่อมระบบ")]
    public async Task<ActionResult<ApiResponse<object>>> Accept(Guid companyId, Guid partnershipId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var p = await _service.AcceptPartnershipAsync(companyId, partnershipId, userId);
        return Ok(new ApiResponse<object>(true, new { p.Id, Status = p.Status.ToString() }, AcceptedMessage));
    }

    /// <summary>รอบ 264: ข้อความหลังยอมรับ (การ์ดยอมรับแสดงตามนี้) — บอกผลและสิ่งที่ยังไม่เกิดจนกว่าจะเปิดค่าตั้ง</summary>
    public const string AcceptedMessage =
        "เชื่อมต่อเสร็จสิ้น — เชื่อมกับคู่ค้ารายนี้รายเดียว (ไม่ได้เปิดให้บริษัทอื่นค้นเจอ) · การส่งเอกสาร/ใบซื้อร่าง/50 ทวิ อัตโนมัติยังเป็นไปตามค่าตั้งของเรา (ค่าตั้งต้นปิด)";

    /// <param name="BlockRequester">รอบ 264 (คำตัดสิน 264-1-13): "ปฏิเสธและไม่รับคำขอจากบริษัทนี้อีก"</param>
    public record RejectRequest(string? Reason, bool BlockRequester = false);

    [HttpPost("trading-partners/{partnershipId:guid}/reject")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ปฏิเสธ/ยกเลิกคู่ค้าเชื่อมระบบ")]
    public async Task<ActionResult<ApiResponse<object>>> Reject(Guid companyId, Guid partnershipId, [FromBody] RejectRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var p = await _service.RejectPartnershipAsync(companyId, partnershipId, req?.Reason, userId == Guid.Empty ? null : userId, req?.BlockRequester ?? false);
        return Ok(new ApiResponse<object>(true, new { p.Id, Status = p.Status.ToString() },
            req?.BlockRequester == true ? "ปฏิเสธแล้ว — จะไม่รับคำขอจากบริษัทนี้อีก (ปลดได้โดยส่งคำขอถึงเขาเอง)" : "ปฏิเสธคำขอแล้ว"));
    }

    public record SuspendRequest(string? Reason);

    /// <summary>รอบ 257 ชุด 3 (D-H5): ระงับการเชื่อม (คู่ที่เชื่อมแล้ว · ฝ่ายใดก็ได้) — หยุดเส้นอัตโนมัติทุกเส้น · audit · ทางกลับ = เชิญใหม่ ·
    /// สิทธิ์ตั้งค่าบริษัท + ไม่รับ API key (การตัดการเชื่อมกับบริษัทอื่นคือการตัดสินใจของคน — ชุดเดียวกับเชิญ/ยอมรับ)</summary>
    [HttpPost("trading-partners/{partnershipId:guid}/suspend")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ระงับการเชื่อมคู่ค้า")]
    public async Task<ActionResult<ApiResponse<object>>> Suspend(Guid companyId, Guid partnershipId, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] SuspendRequest? req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            var p = await _service.SuspendPartnershipAsync(companyId, partnershipId, userId, req?.Reason);
            return Ok(new ApiResponse<object>(true, new { p.Id, Status = p.Status.ToString() }, PartnerExchangeRules.SuspendedMessage));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return StatusCode(ex.StatusCode, new ApiResponse<object>(false, null, ex.Message)); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // WORKFLOW AUTOMATION CONFIG
    // ═══════════════════════════════════════════════════════════════════

    [HttpGet("workflow-automation")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]   // ฝ่ายค้าน L2: ค่าตั้งผู้ลงนามในนามบริษัท = ชุดสิทธิ์เดียวกับการแก้
    public async Task<ActionResult<ApiResponse<object>>> GetConfig(Guid companyId)
    {
        // ฝ่ายค้าน L2: GET ห้ามสร้างแถว — ไม่มี = ค่าตั้งต้นที่ยังไม่บันทึก
        var c = await _service.GetConfigReadOnlyAsync(companyId);
        var discoverable = await _db.Companies.AsNoTracking().Where(x => x.Id == companyId).Select(x => x.PartnerDiscoverable).FirstOrDefaultAsync();
        // ID#10 (รอบ 2026-10-14 ทีม F4): ผู้อนุมัติเริ่มต้นออกจากบริษัทแล้ว ⇒ ระบบหยุดอนุมัติ/ลงนามอัตโนมัติ — หน้าจอต้องเห็นเหตุและทางไปต่อ
        var defaultApproverNotice = await _service.GetDefaultApproverNoticeAsync(c);
        return Ok(new ApiResponse<object>(true, new
        {
            DefaultApproverNotice = defaultApproverNotice,
            PartnerDiscoverable = discoverable,
            c.AutoApproveIncomingQuotations,
            c.AutoApproveMinAmount,
            c.AutoApproveMaxAmount,
            c.AutoCreatePoOnQuotationApproval,
            c.AutoCreateInvoiceFromIncomingPo,
            c.AutoCreateReceiptFromIncomingPayment,
            c.AutoStampSignature,
            c.DefaultApproverUserId,
            c.DefaultSignatureId,
            c.NotifyOnIncomingDocument,
            c.NotifyEmail,
            // รอบ 255 WP-12B ข้อ 7–8 (ค่าตั้งต้นปิด)
            c.CreatePurchaseDraftsFromPartners,
            c.SendWhtCertsToPartners,
            c.SendWhtCertsToPartnersSince,
        }));
    }

    public record WorkflowConfigUpdate(
        bool? AutoApproveIncomingQuotations,
        decimal? AutoApproveMinAmount,
        decimal? AutoApproveMaxAmount,
        bool? AutoCreatePoOnQuotationApproval,
        bool? AutoCreateInvoiceFromIncomingPo,
        bool? AutoCreateReceiptFromIncomingPayment,
        bool? AutoStampSignature,
        // ID#10: ต้องเป็นสมาชิกที่ใช้งานอยู่ของบริษัทนี้ · Guid ว่าง (000…) = ล้างค่า
        Guid? DefaultApproverUserId,
        // ID#10: ต้องเป็นลายเซ็นของผู้อนุมัติเริ่มต้นคนนั้น · Guid ว่าง = ล้างค่า
        Guid? DefaultSignatureId,
        bool? NotifyOnIncomingDocument,
        string? NotifyEmail,
        // ฝ่ายค้าน H3 (คำตัดสิน AUTOPILOT ข้อ 3): เปิดให้คู่ค้าที่ใช้ NextAcc ส่งคำเชิญถึงบริษัทนี้ · null = ไม่แก้ · ตั้งต้นปิด
        bool? PartnerDiscoverable = null,
        // รอบ 255 WP-12B ข้อ 7: ใบซื้อร่างจากเอกสารของคู่ค้า (null = ไม่แก้ · ตั้งต้นปิด)
        bool? CreatePurchaseDraftsFromPartners = null,
        // รอบ 255 WP-12B ข้อ 8: ส่ง 50 ทวิ ให้คู่ค้าอัตโนมัติ (null = ไม่แก้ · ตั้งต้นปิด · เปิด = ประทับเวลาเริ่ม — ไม่ส่งย้อนหลัง)
        bool? SendWhtCertsToPartners = null);

    // ID#10: ค่าตั้งนี้คุมการอนุมัติอัตโนมัติ + ผู้ลงนามในนามบริษัท = ด่านควบคุมภายในชุดเดียวกับกฎการอนุมัติ (สิทธิ์ตั้งค่าบริษัท · ไม่รับ API key)
    [HttpPut("workflow-automation")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ตั้งค่า workflow ข้ามบริษัท")]
    public async Task<ActionResult<ApiResponse<object>>> UpdateConfig(Guid companyId, [FromBody] WorkflowConfigUpdate req)
    {
        var c = await _service.GetOrCreateConfigAsync(companyId);
        // รอบ 264 P1 (C-06): ภาพก่อนแก้ — การเปิด/ปิดงานอัตโนมัติข้ามบริษัทต้องตอบได้ว่าใคร/เมื่อไร/จากอะไรเป็นอะไร (audit chain ของบริษัทนี้)
        var discoverableBefore = await _db.Companies.Where(x => x.Id == companyId).Select(x => (bool?)x.PartnerDiscoverable).FirstOrDefaultAsync();
        var before = PartnerConsent.ConfigSnapshot(c, discoverableBefore);
        if (req.AutoApproveIncomingQuotations.HasValue) c.AutoApproveIncomingQuotations = req.AutoApproveIncomingQuotations.Value;
        if (req.AutoApproveMinAmount.HasValue) c.AutoApproveMinAmount = req.AutoApproveMinAmount.Value;
        if (req.AutoApproveMaxAmount.HasValue) c.AutoApproveMaxAmount = req.AutoApproveMaxAmount.Value;
        if (req.AutoCreatePoOnQuotationApproval.HasValue) c.AutoCreatePoOnQuotationApproval = req.AutoCreatePoOnQuotationApproval.Value;
        if (req.AutoCreateInvoiceFromIncomingPo.HasValue) c.AutoCreateInvoiceFromIncomingPo = req.AutoCreateInvoiceFromIncomingPo.Value;
        if (req.AutoCreateReceiptFromIncomingPayment.HasValue) c.AutoCreateReceiptFromIncomingPayment = req.AutoCreateReceiptFromIncomingPayment.Value;
        if (req.AutoStampSignature.HasValue) c.AutoStampSignature = req.AutoStampSignature.Value;
        // ID#10: เดิมเขียน Guid ที่ส่งมาตรง ๆ (ผู้ใช้ของบริษัทอื่น/ที่ลาออกแล้ว · ลายเซ็นของคนอื่น) — ตรวจผ่านตัวตัดสินเดียวก่อนตั้ง
        if (req.DefaultApproverUserId.HasValue || req.DefaultSignatureId.HasValue)
            await _service.ApplyDefaultSignerAsync(c, req.DefaultApproverUserId, req.DefaultSignatureId);
        if (req.NotifyOnIncomingDocument.HasValue) c.NotifyOnIncomingDocument = req.NotifyOnIncomingDocument.Value;
        if (req.NotifyEmail != null) c.NotifyEmail = req.NotifyEmail;
        if (req.CreatePurchaseDraftsFromPartners.HasValue) c.CreatePurchaseDraftsFromPartners = req.CreatePurchaseDraftsFromPartners.Value;
        if (req.SendWhtCertsToPartners.HasValue)
        {
            // เปิดจากปิด = ประทับเวลาเริ่มใหม่ (ส่งเฉพาะใบที่ออกหลังจากนี้) · ปิด = ล้าง
            if (req.SendWhtCertsToPartners.Value && !c.SendWhtCertsToPartners) c.SendWhtCertsToPartnersSince = DateTime.UtcNow;
            if (!req.SendWhtCertsToPartners.Value) c.SendWhtCertsToPartnersSince = null;
            c.SendWhtCertsToPartners = req.SendWhtCertsToPartners.Value;
        }
        if (req.PartnerDiscoverable.HasValue)
        {
            var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == companyId)
                ?? throw new KeyNotFoundException("ไม่พบบริษัท");
            company.PartnerDiscoverable = req.PartnerDiscoverable.Value;
        }
        c.UpdatedAt = DateTime.UtcNow;
        var after = PartnerConsent.ConfigSnapshot(c, req.PartnerDiscoverable ?? discoverableBefore);
        if (PartnerConsent.ConfigDiff(before, after) is { Count: > 0 } changed)
            _db.AddChainedAuditLog(new Accounting.Models.Entities.AuditLog
            {
                CompanyId = companyId, UserId = JwtHelper.GetUserIdFromClaims(User), Action = AuditAction.Update,
                EntityType = nameof(Accounting.Models.Entities.WorkflowAutomationConfig), EntityId = c.Id.ToString(),
                OldValues = System.Text.Json.JsonSerializer.Serialize(changed.ToDictionary(k => k, k => before[k])),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @event = "WorkflowAutomationConfigChanged", changed = changed.ToDictionary(k => k, k => after[k]),
                    ruleCode = PartnerConsent.RuleCode, legalReference = PartnerConsent.LegalReference,
                }),
                Timestamp = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, "บันทึก workflow automation config เรียบร้อย"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // INCOMING DOCUMENTS (inbox)
    // ═══════════════════════════════════════════════════════════════════

    [HttpGet("incoming-documents")]
    public async Task<ActionResult<ApiResponse<object>>> Incoming(
        Guid companyId,
        [FromQuery] CrossTenantLinkType? type = null,
        [FromQuery] CrossTenantLinkStatus? status = null)
    {
        // รอบ 250 WP-02b (P-3): เอกสารเข้าจากคู่ค้า = เอกสารฝั่งซื้อของเรา — ผู้ที่ถูกจำกัดให้เห็นเฉพาะฝั่งขายไม่เห็นกล่องนี้ (ตัวตัดสินเดียว DocumentPermissionHelper)
        if (!(await DocumentPermissionHelper.VisibleDirectionsAsync(_perms, companyId, JwtHelper.GetUserIdFromClaims(User))).Purchase)
            return Denied(PermissionKeys.DocumentPurchaseView);
        var query = _db.CrossTenantDocumentLinks.AsNoTracking()
            // .ThenInclude(Contact) ถอดออก: projection ด้านล่างไม่ได้ใช้ SourceDocument.Contact
            // (EF drop join อยู่แล้ว = dead include) — ถอดเพื่อไม่ให้สับสนกับบั๊ก INNER JOIN
            .Include(l => l.SourceDocument)
            .Where(l => l.TargetCompanyId == companyId);
        if (type.HasValue) query = query.Where(l => l.LinkType == type.Value);
        if (status.HasValue) query = query.Where(l => l.Status == status.Value);

        var rows = await query
            .OrderByDescending(l => l.CreatedAt)
            .Take(200)
            .Select(l => new
            {
                l.Id,
                LinkType = l.LinkType.ToString(),
                Status = l.Status.ToString(),
                Source = new
                {
                    CompanyId = l.SourceCompanyId,
                    DocumentId = l.SourceDocumentId,
                    l.SourceDocument.DocumentNumber,
                    DocumentType = l.SourceDocument.DocumentType.ToString(),
                    l.SourceDocument.DocumentDate,
                    l.SourceDocument.TotalAmount,
                },
                l.ApprovedAt,
                l.RejectionReason,
                l.TargetDocumentId,
                l.CreatedAt,
            })
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, rows));
    }

    [HttpGet("incoming-documents/{linkId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> IncomingDetail(Guid companyId, Guid linkId)
    {
        if (!(await DocumentPermissionHelper.VisibleDirectionsAsync(_perms, companyId, JwtHelper.GetUserIdFromClaims(User))).Purchase)
            return Denied(PermissionKeys.DocumentPurchaseView);
        // ไม่ ThenInclude Contact: (1) response ด้านล่างไม่ได้ใช้ SourceDocument.Contact
        // เลย และ (2) Contact เป็น required nav + !IsDeleted filter → INNER JOIN
        // ตัด link ทั้งใบทิ้งถ้า contact ต้นทางถูกลบ (= 404 ผิด ๆ). ตัดออกพอ.
        var link = await _db.CrossTenantDocumentLinks.AsNoTracking()
            .Include(l => l.SourceDocument).ThenInclude(d => d.Lines)
            .FirstOrDefaultAsync(l => l.Id == linkId && l.TargetCompanyId == companyId);
        if (link == null) return NotFound();
        return Ok(new ApiResponse<object>(true, new
        {
            link.Id,
            LinkType = link.LinkType.ToString(),
            Status = link.Status.ToString(),
            Source = new
            {
                link.SourceCompanyId,
                link.SourceDocument.DocumentNumber,
                DocumentType = link.SourceDocument.DocumentType.ToString(),
                link.SourceDocument.DocumentDate,
                link.SourceDocument.SubTotal,
                link.SourceDocument.VatAmount,
                link.SourceDocument.TotalAmount,
                Lines = link.SourceDocument.Lines.Select(ln => new
                {
                    ln.Description, ln.Quantity, ln.UnitPrice, ln.Amount, ln.VatRate, ln.VatAmount
                }),
            },
            link.ApprovedAt,
            link.RejectionReason,
            link.SnapshotJson,
            link.Comment,
        }));
    }

    public record ApproveIncomingRequest(Guid? SignatureUserId, bool? AutoCreatePo, string? Comment);

    /// <summary>อนุมัติเอกสารเข้าจากคู่ค้า — <b>ลงนามในนามบริษัท</b> (ลายเซ็นตั้งต้น/ของผู้ลงนาม) + สร้าง PO ปลายน้ำได้ ⇒ ต้องมีสิทธิ์อนุมัติเอกสารฝั่งซื้อ
    /// (Document.Approve หรือ Document.Purchase.Approve — ตัวตัดสินเดียว <see cref="DocumentPermissionHelper.CanApproveAsync"/>) + ไม่รับ API key
    /// (การลงนามคือการตัดสินใจของคน) — รอบ 250 WP-02b P-3 (เดิมสมาชิกคนไหนก็กดได้)</summary>
    [HttpPost("incoming-documents/{linkId:guid}/approve")]
    [Accounting.Filters.RejectApiKey("อนุมัติและลงนามเอกสารเข้าจากคู่ค้า")]
    public async Task<ActionResult<ApiResponse<object>>> ApproveIncoming(
        Guid companyId, Guid linkId, [FromBody] ApproveIncomingRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        if (!await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, DocumentType.PurchaseOrder))
            return Denied(PermissionKeys.DocumentPurchaseApprove);
        var link = await _service.ApproveIncomingAsync(
            approvingCompanyId: companyId,
            linkId: linkId,
            actingUserId: userId,
            signatureUserId: req.SignatureUserId,
            overrideAutoCreatePo: req.AutoCreatePo,
            comment: req.Comment);
        // ID#10: ผู้อนุมัติเริ่มต้นออกไปแล้ว ⇒ ลงนามในชื่อผู้กดแทน — บอกผู้กดพร้อมทางไปต่อ (ไม่เงียบ)
        var notice = !Accounting.Helpers.CrossTenantSignerResolver.IsSignerRequested(req.SignatureUserId)
            ? await _service.GetDefaultApproverNoticeAsync(await _service.GetOrCreateConfigAsync(companyId))
            : null;
        return Ok(new ApiResponse<object>(true, new
        {
            link.Id,
            Status = link.Status.ToString(),
            link.ApprovedAt,
            link.TargetDocumentId,
            SignerNotice = notice,
        }, notice == null ? "อนุมัติเอกสารเรียบร้อย" : "อนุมัติเอกสารเรียบร้อย (ลงนามในชื่อคุณ) — " + notice));
    }

    public record RejectIncomingRequest(string Reason);

    /// <summary>ปฏิเสธเอกสารเข้าจากคู่ค้า — สิทธิ์ชุดเดียวกับการอนุมัติ (การตัดสินใจคู่กัน) + ไม่รับ API key · รอบ 250 WP-02b P-3</summary>
    [HttpPost("incoming-documents/{linkId:guid}/reject")]
    [Accounting.Filters.RejectApiKey("ปฏิเสธเอกสารเข้าจากคู่ค้า")]
    public async Task<ActionResult<ApiResponse<object>>> RejectIncoming(
        Guid companyId, Guid linkId, [FromBody] RejectIncomingRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        if (!await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, DocumentType.PurchaseOrder))
            return Denied(PermissionKeys.DocumentPurchaseApprove);
        var link = await _service.RejectIncomingAsync(companyId, linkId, req.Reason);
        return Ok(new ApiResponse<object>(true, new { link.Id, Status = link.Status.ToString() }, "ปฏิเสธเอกสารเรียบร้อย"));
    }

    /// <summary>
    /// ส่งเอกสารที่อนุมัติแล้วเข้าระบบคู่ค้าอีกครั้ง (ปุ่มบนแถบเตือน "ส่งเอกสารเข้าระบบคู่ค้าไม่สำเร็จ") — รอบ 250 WP-02b P-6
    /// <para>สิทธิ์ = อนุมัติเอกสารชนิดนั้น (ตัวตัดสินเดียว <see cref="DocumentPermissionHelper.CanApproveAsync"/> — ทางเดียวกับการอนุมัติที่ส่งรอบแรก) ·
    /// ล้มอีก ⇒ 409 พร้อมเหตุ + ป้ายบนเอกสารยังอยู่ · สำเร็จ ⇒ ป้ายถูกล้าง</para>
    /// </summary>
    [HttpPost("documents/{documentId:guid}/partner-routing/retry")]
    public async Task<ActionResult<ApiResponse<object>>> RetryPartnerRouting(Guid companyId, Guid documentId, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        var docType = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => (DocumentType?)d.DocumentType)
            .FirstOrDefaultAsync(ct);
        if (docType == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, docType.Value))
            return Denied(DocumentPermissionHelper.IsPurchase(docType.Value) ? PermissionKeys.DocumentPurchaseApprove : PermissionKeys.DocumentRevenueApprove);
        var outcome = await _service.RetryRoutingAsync(companyId, documentId, ct);
        if (outcome.FailureReason != null)
            return StatusCode(409, new ApiResponse<object>(false, new { routed = false, partnerRoutingFailed = true }, outcome.FailureReason));
        return Ok(new ApiResponse<object>(true, new { routed = outcome.Routed, partnerRoutingFailed = false },
            outcome.Routed ? "ส่งเอกสารเข้าระบบคู่ค้าแล้ว" : "ไม่มีคู่ค้าที่เชื่อมระบบสำหรับเอกสารนี้ — ล้างแถบเตือนแล้ว"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // OUTGOING DOCUMENTS (tracker — docs WE sent to partners)
    // ═══════════════════════════════════════════════════════════════════

    [HttpGet("outgoing-documents")]
    public async Task<ActionResult<ApiResponse<object>>> Outgoing(
        Guid companyId,
        [FromQuery] CrossTenantLinkStatus? status = null)
    {
        // ฝ่ายค้าน L2: เอกสารที่เราส่งให้คู่ค้า = ฝั่งขายเป็นหลัก — ผู้ที่ถูกจำกัดให้เห็นเฉพาะฝั่งซื้อไม่เห็นตัวติดตามนี้
        if (!(await DocumentPermissionHelper.VisibleDirectionsAsync(_perms, companyId, JwtHelper.GetUserIdFromClaims(User))).Revenue)
            return Denied(PermissionKeys.DocumentRevenueView);
        var query = _db.CrossTenantDocumentLinks.AsNoTracking()
            .Include(l => l.SourceDocument)
            .Where(l => l.SourceCompanyId == companyId);
        if (status.HasValue) query = query.Where(l => l.Status == status.Value);

        var rows = await query
            .OrderByDescending(l => l.CreatedAt)
            .Take(200)
            .Select(l => new
            {
                l.Id,
                LinkType = l.LinkType.ToString(),
                Status = l.Status.ToString(),
                Document = new
                {
                    l.SourceDocument.DocumentNumber,
                    DocumentType = l.SourceDocument.DocumentType.ToString(),
                    l.SourceDocument.DocumentDate,
                    l.SourceDocument.TotalAmount,
                },
                l.TargetCompanyId,
                l.ApprovedAt,
                l.RejectionReason,
                l.TargetDocumentId,
                l.CreatedAt,
            })
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, rows));
    }
}
