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
        var rows = await _db.TradingPartnerships.AsNoTracking()
            .Include(p => p.CompanyA)
            .Include(p => p.CompanyB)
            .Where(p => p.CompanyAId == companyId || p.CompanyBId == companyId)
            // รอบ 250 WP-02b (P-1): ฝั่งผู้เชิญเห็นคู่ค้าเมื่อปลายทางตอบรับแล้วเท่านั้น — แถว Pending/Rejected ที่เราเชิญเองบอก "มีบริษัทเลขนี้ในระบบ"
            // (ชื่อ + เลขภาษี) ซึ่งเป็นสิ่งเดียวกับที่คำตอบของ invite ปิดไว้ · ฝั่งผู้รับเชิญเห็นคำเชิญทุกสถานะตามเดิม (ต้องกดตอบรับได้)
            .Where(p => p.InvitedByCompanyId != companyId
                || p.Status == TradingPartnershipStatus.Accepted || p.Status == TradingPartnershipStatus.Suspended)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new
            {
                p.Id,
                Status = p.Status.ToString(),
                Partner = p.CompanyAId == companyId
                    ? new { p.CompanyB.Id, p.CompanyB.Name, p.CompanyB.TaxId }
                    : new { p.CompanyA.Id, p.CompanyA.Name, p.CompanyA.TaxId },
                IsInviter = p.InvitedByCompanyId == companyId,
                p.InvitationMessage,
                p.AcceptedAt,
                p.RejectedAt,
                p.RejectionReason,
                p.AutoApproveAmountLimit,
                p.CreatedAt,
            })
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, rows));
    }

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
        return Ok(new ApiResponse<object>(true, new { p.Id, Status = p.Status.ToString() }, "ยืนยันความเป็นพาร์ทเนอร์เรียบร้อย"));
    }

    public record RejectRequest(string? Reason);

    [HttpPost("trading-partners/{partnershipId:guid}/reject")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ปฏิเสธ/ยกเลิกคู่ค้าเชื่อมระบบ")]
    public async Task<ActionResult<ApiResponse<object>>> Reject(Guid companyId, Guid partnershipId, [FromBody] RejectRequest req)
    {
        var p = await _service.RejectPartnershipAsync(companyId, partnershipId, req?.Reason);
        return Ok(new ApiResponse<object>(true, new { p.Id, Status = p.Status.ToString() }, "ปฏิเสธความเป็นพาร์ทเนอร์เรียบร้อย"));
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
        bool? PartnerDiscoverable = null);

    // ID#10: ค่าตั้งนี้คุมการอนุมัติอัตโนมัติ + ผู้ลงนามในนามบริษัท = ด่านควบคุมภายในชุดเดียวกับกฎการอนุมัติ (สิทธิ์ตั้งค่าบริษัท · ไม่รับ API key)
    [HttpPut("workflow-automation")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ตั้งค่า workflow ข้ามบริษัท")]
    public async Task<ActionResult<ApiResponse<object>>> UpdateConfig(Guid companyId, [FromBody] WorkflowConfigUpdate req)
    {
        var c = await _service.GetOrCreateConfigAsync(companyId);
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
        if (req.PartnerDiscoverable.HasValue)
        {
            var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == companyId)
                ?? throw new KeyNotFoundException("ไม่พบบริษัท");
            company.PartnerDiscoverable = req.PartnerDiscoverable.Value;
        }
        c.UpdatedAt = DateTime.UtcNow;
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
