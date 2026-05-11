using Accounting.Data;
using Accounting.Models.DTOs.Signature;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class SignatureApprovalService : ISignatureApprovalService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docService;
    private readonly Accounting.Services.Implementations.Ocr.VendorIntelligenceService _vendorIntel;

    public SignatureApprovalService(AccountingDbContext db, IDocumentService docService,
        Accounting.Services.Implementations.Ocr.VendorIntelligenceService vendorIntel)
    {
        _db = db;
        _docService = docService;
        _vendorIntel = vendorIntel;
    }

    // ==================== USER SIGNATURES ====================

    public async Task<UserSignatureResponse> UploadSignatureAsync(Guid userId, UploadSignatureRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SignatureData))
            throw new InvalidOperationException("กรุณาระบุข้อมูลลายเซ็น");

        // If setting as default, unset existing defaults
        if (request.IsDefault)
        {
            var existing = await _db.Set<UserSignature>()
                .Where(s => s.UserId == userId && s.IsDefault && s.IsActive)
                .ToListAsync();
            foreach (var s in existing) s.IsDefault = false;
        }

        var sig = new UserSignature
        {
            UserId = userId,
            SignatureData = request.SignatureData,
            SignatureFormat = request.SignatureFormat,
            Label = request.Label,
            IsDefault = request.IsDefault,
            CreatedBy = userId.ToString()
        };

        _db.Set<UserSignature>().Add(sig);
        await _db.SaveChangesAsync();
        return MapSignature(sig);
    }

    public async Task<List<UserSignatureResponse>> GetUserSignaturesAsync(Guid userId)
    {
        var sigs = await _db.Set<UserSignature>()
            .Where(s => s.UserId == userId && s.IsActive && !s.IsDeleted)
            .OrderByDescending(s => s.IsDefault).ThenByDescending(s => s.CreatedAt)
            .ToListAsync();
        return sigs.Select(MapSignature).ToList();
    }

    public async Task<UserSignatureResponse?> GetDefaultSignatureAsync(Guid userId)
    {
        var sig = await _db.Set<UserSignature>()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.IsDefault && s.IsActive && !s.IsDeleted);
        return sig == null ? null : MapSignature(sig);
    }

    public async Task DeleteSignatureAsync(Guid userId, Guid signatureId)
    {
        var sig = await _db.Set<UserSignature>()
            .FirstOrDefaultAsync(s => s.Id == signatureId && s.UserId == userId)
            ?? throw new KeyNotFoundException("ไม่พบลายเซ็น");
        sig.IsActive = false;
        sig.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    public async Task SetDefaultSignatureAsync(Guid userId, Guid signatureId)
    {
        var sigs = await _db.Set<UserSignature>()
            .Where(s => s.UserId == userId && s.IsActive && !s.IsDeleted)
            .ToListAsync();
        foreach (var s in sigs) s.IsDefault = s.Id == signatureId;
        await _db.SaveChangesAsync();
    }

    // ==================== DOCUMENT APPROVAL SETUP ====================

    public async Task<List<DocumentApprovalResponse>> SetupApprovalAsync(Guid companyId, SetupDocumentApprovalRequest request, string userId)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == request.DocumentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        // Remove existing pending approvals
        var existing = await _db.Set<DocumentApproval>()
            .Where(a => a.DocumentId == request.DocumentId && a.Status == ApprovalStatus.Pending && !a.IsDeleted)
            .ToListAsync();
        foreach (var e in existing) e.IsDeleted = true;

        var approvals = new List<DocumentApproval>();
        foreach (var step in request.Steps.OrderBy(s => s.StepOrder))
        {
            approvals.Add(new DocumentApproval
            {
                CompanyId = companyId,
                DocumentId = request.DocumentId,
                ApprovalType = step.ApprovalType,
                ApproverRole = step.ApproverRole,
                StepOrder = step.StepOrder,
                ApproverUserId = step.ApproverUserId,
                ApproverName = step.ApproverName,
                ApproverEmail = step.ApproverEmail,
                ApproverTitle = step.ApproverTitle,
                PostApprovalAction = step.PostApprovalAction,
                CreatedBy = userId
            });
        }

        _db.Set<DocumentApproval>().AddRange(approvals);
        doc.Status = DocumentStatus.WaitingApproval;
        await _db.SaveChangesAsync();

        return approvals.Select(MapApproval).ToList();
    }

    public async Task<List<DocumentApprovalResponse>> GetDocumentApprovalsAsync(Guid companyId, Guid documentId)
    {
        var items = await _db.Set<DocumentApproval>()
            .Include(a => a.Document)
            .Where(a => a.DocumentId == documentId && a.CompanyId == companyId && !a.IsDeleted)
            .OrderBy(a => a.StepOrder)
            .ToListAsync();
        return items.Select(MapApproval).ToList();
    }

    public async Task<DocumentWithApprovalsResponse> GetDocumentWithApprovalsAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents
            .Include(d => d.Contact)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var approvals = await _db.Set<DocumentApproval>()
            .Include(a => a.Document)
            .Where(a => a.DocumentId == documentId && !a.IsDeleted)
            .OrderBy(a => a.StepOrder)
            .ToListAsync();

        var signatures = await _db.Set<DocumentSignature>()
            .Where(s => s.DocumentId == documentId && !s.IsDeleted)
            .OrderBy(s => s.SignedAt)
            .ToListAsync();

        return new DocumentWithApprovalsResponse(
            doc.Id, doc.DocumentNumber, doc.DocumentType.ToString(), doc.Status.ToString(),
            doc.TotalAmount, doc.Contact?.Name,
            approvals.Select(MapApproval).ToList(),
            signatures.Select(MapDocSig).ToList());
    }

    public async Task<List<DocumentApprovalResponse>> GetPendingApprovalsAsync(Guid companyId, Guid userId)
    {
        var userIdGuid = userId;
        var items = await _db.Set<DocumentApproval>()
            .Include(a => a.Document)
            .Where(a => a.CompanyId == companyId && a.ApproverUserId == userIdGuid
                && a.Status == ApprovalStatus.Pending && !a.IsDeleted)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
        return items.Select(MapApproval).ToList();
    }

    // ==================== INTERNAL APPROVE / REJECT ====================

    public async Task<DocumentApprovalResponse> ApproveAsync(Guid companyId, Guid approvalId, ApproveDocumentRequest request, string userId, string? ipAddress)
    {
        var approval = await _db.Set<DocumentApproval>()
            .Include(a => a.Document)
            .FirstOrDefaultAsync(a => a.Id == approvalId && a.CompanyId == companyId && !a.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการอนุมัติ");

        if (approval.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException("รายการนี้ดำเนินการแล้ว");

        // Verify it's the right approver (internal)
        if (approval.ApproverUserId.HasValue && approval.ApproverUserId.Value.ToString() != userId)
            throw new InvalidOperationException("คุณไม่มีสิทธิ์อนุมัติรายการนี้");

        // Check previous steps are approved
        var previousPending = await _db.Set<DocumentApproval>()
            .AnyAsync(a => a.DocumentId == approval.DocumentId && a.StepOrder < approval.StepOrder
                && a.Status == ApprovalStatus.Pending && !a.IsDeleted);
        if (previousPending)
            throw new InvalidOperationException("ยังมีขั้นตอนก่อนหน้าที่ยังไม่อนุมัติ");

        // Resolve signature
        string? sigData = request.SignatureData;
        string? sigFormat = request.SignatureFormat;
        Guid? sigId = request.SignatureId;

        if (sigData == null && sigId.HasValue)
        {
            var userSig = await _db.Set<UserSignature>()
                .FirstOrDefaultAsync(s => s.Id == sigId.Value && s.IsActive && !s.IsDeleted);
            if (userSig != null)
            {
                sigData = userSig.SignatureData;
                sigFormat = userSig.SignatureFormat;
            }
        }
        else if (sigData == null)
        {
            // Try default signature
            var defaultSig = await _db.Set<UserSignature>()
                .FirstOrDefaultAsync(s => s.UserId == Guid.Parse(userId) && s.IsDefault && s.IsActive && !s.IsDeleted);
            if (defaultSig != null)
            {
                sigData = defaultSig.SignatureData;
                sigFormat = defaultSig.SignatureFormat;
                sigId = defaultSig.Id;
            }
        }

        // Update approval
        approval.Status = ApprovalStatus.Approved;
        approval.ApprovedAt = DateTime.UtcNow;
        approval.Comments = request.Comments;
        approval.SignatureId = sigId;
        approval.SignatureData = sigData;
        approval.SignatureFormat = sigFormat ?? "PNG";
        approval.IpAddress = ipAddress;

        if (request.ApproverName != null) approval.ApproverName = request.ApproverName;
        if (request.ApproverEmail != null) approval.ApproverEmail = request.ApproverEmail;
        if (request.ApproverTitle != null) approval.ApproverTitle = request.ApproverTitle;

        // Create document signature record
        if (sigData != null)
        {
            var user = await _db.Users.FindAsync(Guid.Parse(userId));
            _db.Set<DocumentSignature>().Add(new DocumentSignature
            {
                CompanyId = companyId,
                DocumentId = approval.DocumentId,
                DocumentApprovalId = approval.Id,
                SignerRole = approval.ApproverRole,
                SignerName = approval.ApproverName ?? user?.FullName ?? "",
                SignerTitle = approval.ApproverTitle,
                SignatureData = sigData,
                SignatureFormat = sigFormat ?? "PNG",
                SignedAt = DateTime.UtcNow,
                IpAddress = ipAddress
            });
        }

        await _db.SaveChangesAsync();

        // Check if all steps approved → update document + run post-action
        await CheckAllApprovedAndProcessAsync(companyId, approval.DocumentId, userId);

        return MapApproval(approval);
    }

    public async Task<DocumentApprovalResponse> RejectAsync(Guid companyId, Guid approvalId, RejectDocumentRequest request, string userId)
    {
        var approval = await _db.Set<DocumentApproval>()
            .Include(a => a.Document)
            .FirstOrDefaultAsync(a => a.Id == approvalId && a.CompanyId == companyId && !a.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการอนุมัติ");

        if (approval.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException("รายการนี้ดำเนินการแล้ว");

        approval.Status = ApprovalStatus.Rejected;
        approval.RejectedAt = DateTime.UtcNow;
        approval.Comments = request.Comments;

        // Update document status
        approval.Document.Status = DocumentStatus.Rejected;

        await _db.SaveChangesAsync();
        return MapApproval(approval);
    }

    // ==================== EXTERNAL API APPROVE ====================

    public async Task<QuotationApprovalResult> ExternalApproveQuotationAsync(Guid companyId, Guid documentId, ExternalApproveRequest request, string? ipAddress)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (doc.DocumentType != DocumentType.Quotation)
            throw new InvalidOperationException("เอกสารนี้ไม่ใช่ใบเสนอราคา");

        if (doc.Status == DocumentStatus.Approved)
            throw new InvalidOperationException("ใบเสนอราคานี้อนุมัติแล้ว");

        if (string.IsNullOrWhiteSpace(request.SignatureData))
            throw new InvalidOperationException("กรุณาระบุลายเซ็น");

        // Find or create customer approval step
        var customerApproval = await _db.Set<DocumentApproval>()
            .FirstOrDefaultAsync(a => a.DocumentId == documentId && a.ApproverRole == "Customer"
                && a.Status == ApprovalStatus.Pending && !a.IsDeleted);

        if (customerApproval == null)
        {
            // Auto-create customer approval step
            var maxStep = await _db.Set<DocumentApproval>()
                .Where(a => a.DocumentId == documentId && !a.IsDeleted)
                .MaxAsync(a => (int?)a.StepOrder) ?? 0;

            customerApproval = new DocumentApproval
            {
                CompanyId = companyId,
                DocumentId = documentId,
                ApprovalType = "Customer",
                ApproverRole = "Customer",
                StepOrder = maxStep + 1,
                ApproverName = request.ApproverName,
                ApproverEmail = request.ApproverEmail,
                ApproverTitle = request.ApproverTitle,
                PostApprovalAction = request.AutoConvert ? "ConvertToInvoice" : null
            };
            _db.Set<DocumentApproval>().Add(customerApproval);
            await _db.SaveChangesAsync();
        }

        // Approve
        customerApproval.Status = ApprovalStatus.Approved;
        customerApproval.ApprovedAt = DateTime.UtcNow;
        customerApproval.ApproverName = request.ApproverName;
        customerApproval.ApproverEmail = request.ApproverEmail;
        customerApproval.ApproverTitle = request.ApproverTitle;
        customerApproval.SignatureData = request.SignatureData;
        customerApproval.SignatureFormat = request.SignatureFormat;
        customerApproval.Comments = request.Comments;
        customerApproval.IpAddress = ipAddress;

        // Create document signature
        _db.Set<DocumentSignature>().Add(new DocumentSignature
        {
            CompanyId = companyId,
            DocumentId = documentId,
            DocumentApprovalId = customerApproval.Id,
            SignerRole = "ผู้ซื้อ",
            SignerName = request.ApproverName,
            SignerTitle = request.ApproverTitle,
            SignatureData = request.SignatureData,
            SignatureFormat = request.SignatureFormat,
            SignedAt = DateTime.UtcNow,
            IpAddress = ipAddress
        });

        // Update document
        doc.Status = DocumentStatus.Approved;
        await _db.SaveChangesAsync();
        await _vendorIntel.TryTrainAsync(doc.CompanyId, doc.Id);

        // Auto-convert if requested
        Guid? convertedDocId = null;
        string? convertedDocNum = null;
        string? convertedDocType = null;

        if (request.AutoConvert)
        {
            try
            {
                var targetType = doc.DocumentType switch
                {
                    DocumentType.Quotation => DocumentType.Invoice,
                    DocumentType.PurchaseRequisition => DocumentType.PurchaseOrder,
                    _ => (DocumentType?)null
                };

                if (targetType.HasValue)
                {
                    var converted = await _docService.ConvertDocumentAsync(companyId, documentId, targetType.Value, "system-auto");
                    convertedDocId = converted.Id;
                    convertedDocNum = converted.DocumentNumber;
                    convertedDocType = targetType.Value.ToString();
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Best-effort document conversion failed for document {documentId}: {ex.Message}"); }
        }

        return new QuotationApprovalResult(
            doc.Id, doc.DocumentNumber,
            ApprovalStatus.Approved,
            convertedDocId, convertedDocNum, convertedDocType,
            convertedDocId.HasValue
                ? $"อนุมัติใบเสนอราคาสำเร็จ และสร้าง{convertedDocType}แล้ว"
                : "อนุมัติใบเสนอราคาสำเร็จ");
    }

    // ==================== DOCUMENT SIGNATURES ====================

    public async Task<List<DocumentSignatureResponse>> GetDocumentSignaturesAsync(Guid companyId, Guid documentId)
    {
        var sigs = await _db.Set<DocumentSignature>()
            .Where(s => s.DocumentId == documentId && s.CompanyId == companyId && !s.IsDeleted)
            .OrderBy(s => s.SignedAt)
            .ToListAsync();
        return sigs.Select(MapDocSig).ToList();
    }

    // ==================== HELPERS ====================

    private async Task CheckAllApprovedAndProcessAsync(Guid companyId, Guid documentId, string userId)
    {
        var allApprovals = await _db.Set<DocumentApproval>()
            .Where(a => a.DocumentId == documentId && !a.IsDeleted)
            .ToListAsync();

        var allApproved = allApprovals.All(a => a.Status == ApprovalStatus.Approved);
        if (!allApproved) return;

        var doc = await _db.Documents.FindAsync(documentId);
        if (doc == null) return;

        doc.Status = DocumentStatus.Approved;
        await _db.SaveChangesAsync();
        await _vendorIntel.TryTrainAsync(doc.CompanyId, doc.Id);

        // Run post-approval action from last step
        var lastAction = allApprovals
            .Where(a => a.PostApprovalAction != null)
            .OrderByDescending(a => a.StepOrder)
            .FirstOrDefault();

        if (lastAction?.PostApprovalAction != null)
        {
            try
            {
                var targetType = lastAction.PostApprovalAction switch
                {
                    "ConvertToInvoice" => DocumentType.Invoice,
                    "ConvertToReceipt" => DocumentType.Receipt,
                    "ConvertToTaxInvoice" => DocumentType.TaxInvoice,
                    "CreatePO" => DocumentType.PurchaseOrder,
                    _ => (DocumentType?)null
                };

                if (targetType.HasValue)
                    await _docService.ConvertDocumentAsync(companyId, documentId, targetType.Value, userId);
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Best-effort post-approval document conversion failed for document {documentId}: {ex.Message}"); }
        }
    }

    private static UserSignatureResponse MapSignature(UserSignature s) => new(
        s.Id, s.UserId, s.SignatureData, s.SignatureFormat,
        s.Label, s.IsDefault, s.IsActive, s.CreatedAt);

    private static DocumentApprovalResponse MapApproval(DocumentApproval a) => new(
        a.Id, a.DocumentId, a.Document?.DocumentNumber ?? "",
        a.ApprovalType, a.ApproverRole, a.StepOrder,
        a.Status, a.ApproverUserId, a.ApproverName, a.ApproverEmail, a.ApproverTitle,
        a.SignatureData != null,
        a.ApprovedAt, a.RejectedAt, a.Comments, a.PostApprovalAction);

    private static DocumentSignatureResponse MapDocSig(DocumentSignature s) => new(
        s.Id, s.SignerRole, s.SignerName, s.SignerTitle,
        s.SignatureData, s.SignatureFormat, s.SignedAt);
}
