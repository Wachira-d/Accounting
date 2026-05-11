using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
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

    public CrossTenantWorkflowController(CrossTenantWorkflowService service, AccountingDbContext db)
    {
        _service = service;
        _db = db;
    }

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

    public record InviteRequest(string PartnerTaxId, string? Message);

    [HttpPost("trading-partners/invite")]
    public async Task<ActionResult<ApiResponse<object>>> Invite(Guid companyId, [FromBody] InviteRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        var p = await _service.InvitePartnerAsync(companyId, req.PartnerTaxId, userId, req.Message);
        if (p == null)
            return NotFound(new ApiResponse<object>(false, null,
                $"ไม่พบบริษัทที่มีเลขประจำตัวผู้เสียภาษี {req.PartnerTaxId} ในระบบ — แจ้ง partner สมัครใช้งานก่อน"));
        return Ok(new ApiResponse<object>(true, new { p.Id, Status = p.Status.ToString() },
            "ส่งคำเชิญพาร์ทเนอร์เรียบร้อย — รอ partner ตอบรับ"));
    }

    [HttpPost("trading-partners/{partnershipId:guid}/accept")]
    public async Task<ActionResult<ApiResponse<object>>> Accept(Guid companyId, Guid partnershipId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var p = await _service.AcceptPartnershipAsync(companyId, partnershipId, userId);
        return Ok(new ApiResponse<object>(true, new { p.Id, Status = p.Status.ToString() }, "ยืนยันความเป็นพาร์ทเนอร์เรียบร้อย"));
    }

    public record RejectRequest(string? Reason);

    [HttpPost("trading-partners/{partnershipId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<object>>> Reject(Guid companyId, Guid partnershipId, [FromBody] RejectRequest req)
    {
        var p = await _service.RejectPartnershipAsync(companyId, partnershipId, req?.Reason);
        return Ok(new ApiResponse<object>(true, new { p.Id, Status = p.Status.ToString() }, "ปฏิเสธความเป็นพาร์ทเนอร์เรียบร้อย"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // WORKFLOW AUTOMATION CONFIG
    // ═══════════════════════════════════════════════════════════════════

    [HttpGet("workflow-automation")]
    public async Task<ActionResult<ApiResponse<object>>> GetConfig(Guid companyId)
    {
        var c = await _service.GetOrCreateConfigAsync(companyId);
        return Ok(new ApiResponse<object>(true, new
        {
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
        Guid? DefaultApproverUserId,
        Guid? DefaultSignatureId,
        bool? NotifyOnIncomingDocument,
        string? NotifyEmail);

    [HttpPut("workflow-automation")]
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
        if (req.DefaultApproverUserId.HasValue) c.DefaultApproverUserId = req.DefaultApproverUserId.Value;
        if (req.DefaultSignatureId.HasValue) c.DefaultSignatureId = req.DefaultSignatureId.Value;
        if (req.NotifyOnIncomingDocument.HasValue) c.NotifyOnIncomingDocument = req.NotifyOnIncomingDocument.Value;
        if (req.NotifyEmail != null) c.NotifyEmail = req.NotifyEmail;
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
        var query = _db.CrossTenantDocumentLinks.AsNoTracking()
            .Include(l => l.SourceDocument).ThenInclude(d => d.Contact)
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
        var link = await _db.CrossTenantDocumentLinks.AsNoTracking()
            .Include(l => l.SourceDocument).ThenInclude(d => d.Lines)
            .Include(l => l.SourceDocument).ThenInclude(d => d.Contact)
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

    [HttpPost("incoming-documents/{linkId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<object>>> ApproveIncoming(
        Guid companyId, Guid linkId, [FromBody] ApproveIncomingRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        var link = await _service.ApproveIncomingAsync(
            approvingCompanyId: companyId,
            linkId: linkId,
            actingUserId: userId,
            signatureUserId: req.SignatureUserId,
            overrideAutoCreatePo: req.AutoCreatePo,
            comment: req.Comment);
        return Ok(new ApiResponse<object>(true, new
        {
            link.Id,
            Status = link.Status.ToString(),
            link.ApprovedAt,
            link.TargetDocumentId,
        }, "อนุมัติเอกสารเรียบร้อย"));
    }

    public record RejectIncomingRequest(string Reason);

    [HttpPost("incoming-documents/{linkId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<object>>> RejectIncoming(
        Guid companyId, Guid linkId, [FromBody] RejectIncomingRequest req)
    {
        var link = await _service.RejectIncomingAsync(companyId, linkId, req.Reason);
        return Ok(new ApiResponse<object>(true, new { link.Id, Status = link.Status.ToString() }, "ปฏิเสธเอกสารเรียบร้อย"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // OUTGOING DOCUMENTS (tracker — docs WE sent to partners)
    // ═══════════════════════════════════════════════════════════════════

    [HttpGet("outgoing-documents")]
    public async Task<ActionResult<ApiResponse<object>>> Outgoing(
        Guid companyId,
        [FromQuery] CrossTenantLinkStatus? status = null)
    {
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
