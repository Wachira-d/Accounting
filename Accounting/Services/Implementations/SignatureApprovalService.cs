using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
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
    private readonly INotificationEngine? _notify;

    private readonly IPermissionService _perms;
    public SignatureApprovalService(AccountingDbContext db, IDocumentService docService,
        Accounting.Services.Implementations.Ocr.VendorIntelligenceService vendorIntel,
        IPermissionService perms,
        INotificationEngine? notify = null)
    {
        _db = db;
        _docService = docService;
        _vendorIntel = vendorIntel;
        _perms = perms;
        _notify = notify;
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

    /// <summary>ด่านสิทธิ์เดียวของทุกทางเข้าในบริการนี้ — เดิมทั้ง 3 controller มีแค่ [Authorize]
    /// ⇒ สมาชิกคนไหนก็ตั้งขั้นเอง/อนุมัติแทน/ยิง "อนุมัติจากลูกค้า" ได้ ทำให้ด่าน Document.Approve
    /// ใน DocumentController เป็นโมฆะ (ERP_REVIEW G-01). กติกาเดียวกับ DocumentController.DenyDocAsync</summary>
    private async Task RequireApproveAsync(Guid companyId, string userId, DocumentType type, string verb)
    {
        if (Guid.TryParse(userId, out var uid)
            && await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, uid, type))
            return;
        var dir = DocumentPermissionHelper.IsRevenue(type) ? "Revenue"
            : DocumentPermissionHelper.IsPurchase(type) ? "Purchase" : null;
        throw new BusinessRuleException(
            $"ไม่มีสิทธิ์{verb}เอกสาร {type} (ต้องการ Document.Approve"
            + (dir == null ? ")" : $" หรือ Document.{dir}.Approve)"),
            "PERM-DOC-APPROVE", 403);
    }

    // ==================== DOCUMENT APPROVAL SETUP ====================

    public async Task<List<DocumentApprovalResponse>> SetupApprovalAsync(Guid companyId, SetupDocumentApprovalRequest request, string userId)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == request.DocumentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        await RequireApproveAsync(companyId, userId, doc.DocumentType, "ตั้งขั้นอนุมัติ");

        // ⚠️ ตั้งขั้นอนุมัติ = พลิกสถานะเป็น WaitingApproval ⇒ ทำกับเอกสารที่
        // **ออกเลขแล้ว**ไม่ได้: §86/4 ห้ามแก้เลขที่ออกแล้วย้อนหลัง · JE ลงไปแล้ว ·
        // และ WaitingApproval อยู่ใน DocumentStatusRules.NotIssued ⇒ ใบจะ
        // **หายจากรายงานภาษี/แบบยื่นทุกแบบพร้อมกันโดยไม่มีอะไรบอก**
        // (ภ.พ.30 ที่เคยนับใบนี้ไปแล้วจะไม่ตรงกับ GL ทันที)
        if (doc.Status != DocumentStatus.Draft && doc.Status != DocumentStatus.Rejected)
            throw new Accounting.Helpers.BusinessRuleException(
                $"เอกสาร {doc.DocumentNumber} อยู่สถานะ {doc.Status} — ตั้งขั้นอนุมัติย้อนหลัง"
                + "ไม่ได้ เพราะเลขที่เอกสารออกแล้ว (§86/4) และรายการบัญชีลงไปแล้ว · "
                + "ถ้าต้องการให้ผ่านสายอนุมัติ ให้ยกเลิกใบนี้แล้วออกใบใหม่ "
                + "หรือตั้งขั้นอนุมัติตั้งแต่ตอนที่ยังเป็นร่าง",
                "DOC-APPROVAL-SETUP-TOO-LATE");

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
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate แยก
        await _db.HydrateContactAsync(companyId, doc);

        var approvals = await _db.Set<DocumentApproval>()
            .Include(a => a.Document)
            .Where(a => a.DocumentId == documentId && !a.IsDeleted)
            .OrderBy(a => a.StepOrder)
            .ToListAsync();

        var signatures = await _db.Set<DocumentSignature>()
            .Where(s => s.DocumentId == documentId && !s.IsDeleted)
            .OrderBy(s => s.SignedAt)
            .ToListAsync();

        // R4-1: ลายเซ็นลูกค้าที่เซ็นกับเนื้อหาก่อนแก้ — แสดงแถวพร้อมเหตุผล และไม่ส่งภาพลายเซ็นนั้นเป็น "ลายเซ็นบนเอกสาร"
        var stale = await StaleSignatureApprovalIdsAsync(companyId, documentId);
        return new DocumentWithApprovalsResponse(
            doc.Id, doc.DocumentNumber, doc.DocumentType.ToString(), doc.Status.ToString(),
            doc.TotalAmount, doc.Contact?.Name,
            approvals.Select(a => stale.Contains(a.Id)
                ? MapApproval(a) with { SignatureStaleReason = DocumentSignedContent.StaleReason }
                : MapApproval(a)).ToList(),
            signatures.Where(s => !stale.Contains(s.DocumentApprovalId)).Select(MapDocSig).ToList());
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
        await RequireApproveAsync(companyId, userId, approval.Document.DocumentType, "อนุมัติ");

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
            // ★ F-13: เดิมค้นด้วย SignatureId อย่างเดียว ⇒ ส่ง GUID ของลายเซ็นคนอื่น
            // มาก็แนบภาพลายเซ็นเขาลงเอกสารที่ส่งให้ลูกค้าได้ = ปลอมลายเซ็น
            // (และเป็นข้อมูลชีวมาตรตาม PDPA ม.26) — ต้องเป็นลายเซ็น "ของผู้อนุมัติเอง"
            var actorId = Guid.Parse(userId);
            var userSig = await _db.Set<UserSignature>()
                .FirstOrDefaultAsync(s => s.Id == sigId.Value && s.UserId == actorId
                    && s.IsActive && !s.IsDeleted);
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

        // รอบ 193 (ฝ่ายค้านรอบสอง N6): ลายเซ็นขั้นสุดท้าย = การอนุมัติเอกสารจริง ⇒ ถามคำเตือนก่อน "บันทึกลายเซ็น" — เดิมบันทึกลายเซ็นก่อน
        // แล้วค่อยอนุมัติ ⇒ เจอ [Σ-GAP] ได้ 500 ข้อความกลาง ๆ · ลายเซ็นครบแต่เอกสารค้างโดยไม่มีใครบอก · ตอนนี้ 422 พร้อมรายการ
        // ให้ผู้เซ็นกดรับทราบ (ส่งซ้ำพร้อม acknowledgeWarnings) เหมือนหน้าเอกสาร/workflow/มือถือ · ยังไม่มีอะไรถูกบันทึก
        var lastStep = !await _db.Set<DocumentApproval>()
            .AnyAsync(a => a.DocumentId == approval.DocumentId && a.Id != approval.Id
                && a.Status == ApprovalStatus.Pending && !a.IsDeleted);
        if (lastStep && !request.AcknowledgeWarnings)
        {
            var pendingWarnings = await _docService.PreviewApprovalWarningsAsync(companyId, approval.DocumentId);
            if (pendingWarnings.Count > 0)
                throw new DocumentApprovalWarningsException(pendingWarnings);
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
        // ผู้เซ็นกดรับทราบเอง = User · ไม่ได้กด (ไม่มีคำเตือนตอนถาม) = ระบบส่งผ่านคำเตือนทั่วไปที่เกิดใหม่ได้ แต่ [Σ-GAP] ไม่ได้
        await CheckAllApprovedAndProcessAsync(companyId, approval.DocumentId, userId,
            request.AcknowledgeWarnings ? Accounting.Helpers.ApprovalAckSource.User : Accounting.Helpers.ApprovalAckSource.SystemWorkflow);

        return MapApproval(approval);
    }

    public async Task<DocumentApprovalResponse> RejectAsync(Guid companyId, Guid approvalId, RejectDocumentRequest request, string userId)
    {
        var approval = await _db.Set<DocumentApproval>()
            .Include(a => a.Document)
            .FirstOrDefaultAsync(a => a.Id == approvalId && a.CompanyId == companyId && !a.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการอนุมัติ");
        await RequireApproveAsync(companyId, userId, approval.Document.DocumentType, "ตีกลับ");

        if (approval.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException("รายการนี้ดำเนินการแล้ว");

        approval.Status = ApprovalStatus.Rejected;
        approval.RejectedAt = DateTime.UtcNow;
        approval.Comments = request.Comments;

        // ตีกลับ = เด้งเอกสารกลับ Draft ให้แก้แล้วส่งใหม่ (กติกาเดียวกับ ApprovalService/มือถือ)
        // — เดิมตั้ง Rejected ซึ่งเป็นสถานะปลายทาง: แก้ได้แต่อนุมัติ/ส่งใหม่ไม่ได้ (ERP_REVIEW B-03)
        if (approval.Document.Status is DocumentStatus.WaitingApproval or DocumentStatus.Rejected)
            approval.Document.Status = DocumentStatus.Draft;

        await _db.SaveChangesAsync();

        // Best-effort notify — never block the rejection on a notification fail.
        if (_notify != null)
        {
            try
            {
                Guid.TryParse(userId, out var actorId);
                await _notify.DispatchAsync(companyId, NotificationEvents.DocumentVoided, new NotificationContext
                {
                    Title = $"เอกสาร {approval.Document.DocumentNumber} ถูกปฏิเสธ",
                    Message = $"ผู้พิจารณา: {approval.ApproverName ?? "-"}\nเหตุผล: {request.Comments ?? "-"}",
                    EntityType = "Document",
                    EntityId = approval.DocumentId,
                    ActorUserId = actorId == Guid.Empty ? null : actorId,
                    ActionUrl = $"/pages/document-detail.html?id={approval.DocumentId}",
                });
            }
            catch { /* swallow — notification must not affect business txn */ }
        }
        return MapApproval(approval);
    }

    // ==================== EXTERNAL API APPROVE ====================

    public async Task<QuotationApprovalResult> ExternalApproveQuotationAsync(Guid companyId, Guid documentId, ExternalApproveRequest request, string? ipAddress, string actingUserId)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        // ผู้บันทึก "ลูกค้าอนุมัติแล้ว" คือสมาชิกที่ล็อกอิน — ต้องมีสิทธิ์อนุมัติเอง ไม่งั้นพิมพ์
        // ชื่อ/ลายเซ็นลูกค้าเองแล้วอนุมัติข้ามผู้จัดการได้ (G-01)
        await RequireApproveAsync(companyId, actingUserId, DocumentType.Quotation, "บันทึกการอนุมัติจากลูกค้าของ");

        if (doc.DocumentType != DocumentType.Quotation)
            throw new InvalidOperationException("เอกสารนี้ไม่ใช่ใบเสนอราคา");

        if (doc.Status == DocumentStatus.Approved)
            throw new InvalidOperationException("ใบเสนอราคานี้อนุมัติแล้ว");

        if (string.IsNullOrWhiteSpace(request.SignatureData))
            throw new InvalidOperationException("กรุณาระบุลายเซ็น");

        // ขั้นภายในที่ยัง Pending ต้องผ่านก่อน — เส้นภายใน (ApproveAsync/CheckAllApprovedAndProcessAsync)
        // ตรวจอยู่แล้ว แต่เส้นนี้เดิมข้ามไป ApproveDocumentAsync ทันที (G-01)
        var internalPending = await _db.Set<DocumentApproval>()
            .AnyAsync(a => a.DocumentId == documentId && a.ApproverRole != "Customer"
                && a.Status == ApprovalStatus.Pending && !a.IsDeleted);
        if (internalPending)
            throw new InvalidOperationException("ยังมีขั้นอนุมัติภายในที่รออยู่ — อนุมัติภายในให้ครบก่อน แล้วจึงบันทึกการอนุมัติจากลูกค้า");

        // รอบ 193 (ฝ่ายค้านรอบสอง N6): ถามคำเตือนก่อนบันทึกลายเซ็นลูกค้า (ผู้บันทึกคือสมาชิกที่ล็อกอินและมีสิทธิ์อนุมัติ ⇒ รับทราบแทน
        // ตัวเองได้) — เดิมบันทึกลายเซ็นก่อนแล้วค่อยเจอคำเตือน ⇒ 500 · กดซ้ำได้ขั้นลูกค้าใหม่ + ลายเซ็นซ้ำ
        if (!request.AcknowledgeWarnings)
        {
            var pendingWarnings = await _docService.PreviewApprovalWarningsAsync(companyId, documentId);
            if (pendingWarnings.Count > 0)
                throw new DocumentApprovalWarningsException(pendingWarnings);
        }

        // รอบ 193 ฝ่ายค้านรอบสาม R3-3: ลายเซ็นลูกค้าเดิมใช้ซ้ำได้เฉพาะเมื่อ "เนื้อหาเอกสารไม่เปลี่ยนตั้งแต่เซ็น" (hash ตัวเดียว
        // Helpers/DocumentSignedContent ทั้งตอนเขียนและตอนตรวจ) และคำขอเป็นการเรียกซ้ำของผู้เซ็นคนเดิมด้วยลายเซ็นเดิม —
        // เดิมเช็คแค่ "มีขั้นลูกค้าที่ Approved แล้วไหม" ⇒ แก้ราคาแล้วเรียกซ้ำ ใบถูกอนุมัติด้วยลายเซ็นที่ลูกค้าให้กับราคาเดิม
        // และลายเซ็น/ชื่อในคำขอใหม่ถูกทิ้งเงียบ · ลายเซ็นที่ใช้ไม่ได้ถูก "แทนที่" (soft-delete + หมายเหตุ ⇒ ไม่ขึ้นบน PDF อีก)
        // แล้วบันทึกลายเซ็นใหม่จากคำขอนี้
        // B9 (race): เดิมกันซ้ำด้วย AnyAsync อย่างเดียว ⇒ เรียกพร้อมกันสองครั้งได้ขั้นลูกค้า + ลายเซ็นซ้ำ ⇒ ล็อกแถวเอกสาร
        // (FOR UPDATE ใน transaction) รอบ "ตรวจ → บันทึก" · ไม่ใช้ unique index เพราะฐานที่ใช้งานอยู่มีแถวซ้ำจากพฤติกรรมเดิมแล้ว
        // (CREATE UNIQUE INDEX จะล้มตอน migrate)
        var reusedSignature = false;
        string signerName = request.ApproverName;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var signTx = await _db.Database.BeginTransactionAsync();
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE",
                documentId, companyId);
            await _db.Entry(doc).ReloadAsync();   // เนื้อหา/สถานะล่าสุดใต้ล็อก
            if (doc.Status == DocumentStatus.Approved)
                throw new InvalidOperationException("ใบเสนอราคานี้อนุมัติแล้ว");

            var lines = await _db.DocumentLines.AsNoTracking()
                .Where(l => l.DocumentId == documentId && !l.IsDeleted)
                .ToListAsync();
            var contentHash = DocumentSignedContent.Hash(doc, lines);

            var signedSteps = await _db.Set<DocumentApproval>()
                .Where(a => a.CompanyId == companyId && a.DocumentId == documentId && a.ApproverRole == "Customer"
                    && a.Status == ApprovalStatus.Approved && !a.IsDeleted)
                .ToListAsync();
            var reusable = signedSteps.FirstOrDefault(a => DocumentSignedContent.CanReuseSignature(
                a.SignedContentHash, contentHash, a.SignatureData, a.ApproverName,
                request.SignatureData, request.ApproverName));

            if (reusable != null)
            {
                // เรียกซ้ำด้วยเนื้อหาเดิม + ลายเซ็นเดิม ⇒ ไม่สร้างขั้น/ลายเซ็นซ้ำ · ผู้อนุมัติ = ผู้เซ็นที่เก็บไว้ (ไม่ใช่ชื่อในคำขอ)
                reusedSignature = true;
                signerName = reusable.ApproverName ?? request.ApproverName;
            }
            else
            {
                foreach (var stale in signedSteps)
                    DocumentSignedContent.Supersede(stale, string.Equals(stale.SignedContentHash, contentHash, StringComparison.Ordinal)
                        ? "ลูกค้าเซ็นใหม่ (ลายเซ็น/ชื่อผู้เซ็นต่างจากเดิม)"
                        : DocumentSignedContent.StaleReason, DateTime.UtcNow);
                if (signedSteps.Count > 0)
                {
                    var staleIds = signedSteps.Select(a => a.Id).ToList();
                    var staleSigs = await _db.Set<DocumentSignature>()
                        .Where(s => s.CompanyId == companyId && s.DocumentId == documentId
                            && staleIds.Contains(s.DocumentApprovalId) && !s.IsDeleted)
                        .ToListAsync();
                    foreach (var staleSig in staleSigs) { staleSig.IsDeleted = true; staleSig.UpdatedAt = DateTime.UtcNow; }
                }

                // Find or create customer approval step
                var customerApproval = await _db.Set<DocumentApproval>()
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.DocumentId == documentId && a.ApproverRole == "Customer"
                        && a.Status == ApprovalStatus.Pending && !a.IsDeleted);

                if (customerApproval == null)
                {
                    // Auto-create customer approval step
                    var maxStep = await _db.Set<DocumentApproval>()
                        .Where(a => a.CompanyId == companyId && a.DocumentId == documentId && !a.IsDeleted)
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
                customerApproval.SignedContentHash = contentHash;   // ลูกค้าเซ็น "เนื้อหานี้" — ตัวตรวจตอนเรียกซ้ำ

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
            }

            await _db.SaveChangesAsync();   // เก็บลายเซ็น/approval row ก่อนอนุมัติ (อนุมัติมี transaction + ล็อกของตัวเอง)
            await signTx.CommitAsync();
        });

        // อนุมัติผ่าน "pipeline เต็ม" ของ DocumentService — ห้ามตั้ง Status ตรง ๆ
        // (เดิมข้าม JE/สต๊อก/tax point/§86-4/§65ตรี ทั้งหมด → เอกสารการเงิน
        // ที่อนุมัติผ่านลายเซ็น "หายจากบัญชี" ทั้งใบ). acknowledgeWarnings=true
        // เพราะผ่านการเซ็นหลายคนแล้ว = ระดับการยืนยันสูงกว่า warning dialog;
        // hard block (§86/4 ไม่ครบ ฯลฯ) ยัง throw ตามปกติ.
        // รอบ 193 (ฝ่ายค้าน C5): ผู้เซ็นไม่เคยเห็นรายการคำเตือน ⇒ ลงร่องรอยว่า "ระบบ workflow ส่งผ่าน" ไม่ใช่ผู้ใช้รับทราบ ·
        // คำเตือน "ยอดจากสแกนไม่ตรงกระดาษ" ระบบส่งผ่านไม่ได้ (ต้องมีคนรับทราบ) ⇒ หยุดพร้อมรายการ
        var approvedByConcurrentCall = false;
        try
        {
            await _docService.ApproveDocumentAsync(
                companyId, documentId, $"external:{signerName}",
                request.AcknowledgeWarnings ? Accounting.Helpers.ApprovalAckSource.User
                                            : Accounting.Helpers.ApprovalAckSource.SystemWorkflow,
                withAiHints: false);
        }
        catch (Exception ex) when (ex is DocumentApprovalWarningsException or InvalidOperationException
                                       or Accounting.Helpers.BusinessRuleException)
        {
            // B9: คำขอพร้อมกันอีกตัวอนุมัติใบนี้ไปแล้วระหว่างรอ ⇒ ไม่ใช่ความล้มเหลว — ห้ามตอบ 422 "อนุมัติไม่ได้" ทั้งที่ใบอนุมัติแล้ว
            // และห้ามแปลงเอกสารซ้ำ (คำขอที่อนุมัติจริงเป็นผู้แปลง)
            var nowStatus = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == documentId && d.CompanyId == companyId)
                .Select(d => d.Status).FirstAsync();
            if (nowStatus != DocumentStatus.Approved)
            {
                // ลายเซ็นลูกค้าถูกเก็บแล้ว — ข้อความต้องไม่สัญญาว่า "ใช้ลายเซ็นเดิม" แบบไม่มีเงื่อนไข (R3-3): ใช้ซ้ำได้เฉพาะเนื้อหาเดิม
                throw new Accounting.Helpers.BusinessRuleException(
                    "บันทึกลายเซ็นลูกค้าแล้ว แต่ระบบอนุมัติใบเสนอราคายังไม่ได้: " + DocumentApprovalWarningsException.DescribeForUser(ex)
                    + " — แก้ตามข้อความแล้วเรียกซ้ำ หรือกด \"อนุมัติ\" ที่หน้าเอกสาร · ลายเซ็นนี้นับเฉพาะเนื้อหาที่ลูกค้าเซ็น: ถ้าแก้เนื้อหาใบ "
                    + "(บรรทัด/ราคา/ยอด/ผู้ซื้อ/เงื่อนไข/ข้อความบนใบ) ลายเซ็นนี้ใช้ไม่ได้ทั้งสองทาง (ด่านอนุมัติและ PDF ไม่นับ) ต้องให้ลูกค้าเซ็นใหม่กับเนื้อหาที่แก้",
                    "SIGN-APPROVE-PENDING", 422);
            }
            approvedByConcurrentCall = true;
        }
        doc = await _db.Documents.FirstAsync(d => d.Id == documentId && d.CompanyId == companyId);
        await _vendorIntel.TryTrainAsync(doc.CompanyId, doc.Id);

        // Auto-convert if requested
        Guid? convertedDocId = null;
        string? convertedDocNum = null;
        string? convertedDocType = null;

        if (request.AutoConvert && !approvedByConcurrentCall)
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
            (convertedDocId.HasValue
                ? $"อนุมัติใบเสนอราคาสำเร็จ และสร้าง{convertedDocType}แล้ว"
                : approvedByConcurrentCall
                    ? "ใบเสนอราคานี้อยู่ในสถานะอนุมัติแล้ว (น่าจะโดยคำขอที่ส่งมาพร้อมกัน) — ไม่แปลงเอกสารซ้ำ"
                    : "อนุมัติใบเสนอราคาสำเร็จ")
                + (reusedSignature ? " · ใช้ลายเซ็นลูกค้าที่บันทึกไว้แล้วของเนื้อหาเดียวกัน (ไม่บันทึกซ้ำ)" : ""));
    }

    // ==================== DOCUMENT SIGNATURES ====================

    public async Task<List<DocumentSignatureResponse>> GetDocumentSignaturesAsync(Guid companyId, Guid documentId)
    {
        var sigs = await _db.Set<DocumentSignature>()
            .Where(s => s.DocumentId == documentId && s.CompanyId == companyId && !s.IsDeleted)
            .OrderBy(s => s.SignedAt)
            .ToListAsync();
        var stale = await StaleSignatureApprovalIdsAsync(companyId, documentId);
        return sigs.Where(s => !stale.Contains(s.DocumentApprovalId)).Select(MapDocSig).ToList();
    }

    /// <summary>แถวอนุมัติที่ลายเซ็นลูกค้า "ไม่นับ" กับเนื้อหาตอนนี้ (R4-1) — ตัดสินด้วย <c>DocumentSignedContent.IsSignatureCurrent</c> ตัวเดียว</summary>
    private async Task<HashSet<Guid>> StaleSignatureApprovalIdsAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId);
        if (doc == null) return new HashSet<Guid>();
        var approvals = await _db.Set<DocumentApproval>().AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.DocumentId == documentId && !a.IsDeleted
                && a.Status == ApprovalStatus.Approved)
            .ToListAsync();
        return approvals.Where(a => !DocumentSignedContent.IsSignatureCurrent(a, doc, doc.Lines))
            .Select(a => a.Id).ToHashSet();
    }

    // ==================== HELPERS ====================

    private async Task CheckAllApprovedAndProcessAsync(Guid companyId, Guid documentId, string userId,
        Accounting.Helpers.ApprovalAckSource ackSource)
    {
        var allApprovals = await _db.Set<DocumentApproval>()
            .Where(a => a.DocumentId == documentId && !a.IsDeleted)
            .ToListAsync();

        var allApproved = allApprovals.All(a => a.Status == ApprovalStatus.Approved);
        if (!allApproved) return;

        // Defence-in-depth: caller already passed companyId — make sure the
        // approval flow can't be tricked into mutating a foreign document by
        // mismatched IDs.
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId);
        if (doc == null) return;

        // รอบ 193 ฝ่ายค้านรอบสี่ R4-1: ขั้นลูกค้าที่เซ็นกับเนื้อหาก่อนแก้ ไม่นับว่า "เซ็นครบ" (ตัวตัดสินตัวเดียว DocumentSignedContent.IsSignatureCurrent)
        // — ลายเซ็นขั้นนี้บันทึกแล้ว ⇒ บอกเหตุผล + ทางไปต่อ (ไม่ใช่ปล่อยให้ด่านใน ApproveDocumentAsync ตอบข้อความกลาง ๆ)
        if (allApprovals.Any(a => !DocumentSignedContent.IsSignatureCurrent(a, doc, doc.Lines)))
            throw new Accounting.Helpers.BusinessRuleException(
                "บันทึกลายเซ็นขั้นนี้แล้ว แต่ลายเซ็นลูกค้าในเอกสารนี้ไม่นับ: " + DocumentSignedContent.StaleReason
                + " — ส่งให้ลูกค้าเซ็นใหม่ (บันทึกการอนุมัติจากลูกค้าอีกครั้ง) แล้วจึงอนุมัติเอกสาร",
                "SIGN-CUSTOMER-STALE", 422);

        // อนุมัติผ่าน pipeline เต็ม (JE + สต๊อก + tax point + ออกเลข + validation)
        // — เดิมตั้ง Status ตรง ๆ ทำให้เอกสารข้ามการลงบัญชีทั้งหมด
        // รอบ 193 (ฝ่ายค้าน C5): ผู้เซ็นครบทุกขั้นแต่ไม่มีใครเห็นคำเตือน ⇒ "ระบบ workflow ส่งผ่าน" (ไม่ประทับว่าผู้ใช้รับทราบ)
        // (ฝ่ายค้านรอบสอง N6) ลายเซ็นครบถูกบันทึกแล้ว ⇒ ถ้าอนุมัติไม่ผ่าน ต้องบอกผู้เซ็นว่าเอกสาร "เซ็นครบ รออนุมัติด้วยมือ" พร้อมเหตุผล
        // และทางไปต่อ (422 ข้อความไทย) — ไม่ใช่ 500 ข้อความกลาง ๆ ที่ทำให้ไม่มีใครรู้ว่าต้องไปกดอนุมัติที่หน้าเอกสาร
        try
        {
            await _docService.ApproveDocumentAsync(companyId, documentId, userId,
                ackSource == Accounting.Helpers.ApprovalAckSource.User
                    ? Accounting.Helpers.ApprovalAckSource.User
                    : Accounting.Helpers.ApprovalAckSource.SystemWorkflow,
                withAiHints: false);
        }
        catch (Exception ex) when (ex is DocumentApprovalWarningsException or InvalidOperationException
                                       or Accounting.Helpers.BusinessRuleException)
        {
            throw new Accounting.Helpers.BusinessRuleException(
                "ลายเซ็นครบทุกขั้นถูกบันทึกแล้ว แต่ระบบอนุมัติเอกสารยังไม่ได้: "
                + DocumentApprovalWarningsException.DescribeForUser(ex)
                + " — เอกสารอยู่ในสถานะ \"เซ็นครบ รออนุมัติด้วยมือ\" · ให้ผู้มีสิทธิ์เปิดหน้าเอกสารแล้วกด \"อนุมัติ\" "
                + "(ระบบจะถามให้รับทราบคำเตือน) เพื่อออกเลขและลงบัญชี",
                "SIGN-APPROVE-PENDING", 422);
        }
        doc = await _db.Documents.FirstAsync(d => d.Id == documentId && d.CompanyId == companyId);
        await _vendorIntel.TryTrainAsync(doc.CompanyId, doc.Id);

        // Notify on full approval — best-effort.
        if (_notify != null)
        {
            try
            {
                Guid.TryParse(userId, out var actorId);
                await _notify.DispatchAsync(companyId, NotificationEvents.DocumentApproved, new NotificationContext
                {
                    Title = $"เอกสาร {doc.DocumentNumber} อนุมัติครบทุกขั้นแล้ว",
                    Message = $"ประเภท: {doc.DocumentType}\nยอดรวม: {doc.TotalAmount:N2} บาท",
                    EntityType = "Document",
                    EntityId = doc.Id,
                    ActorUserId = actorId == Guid.Empty ? null : actorId,
                    ActionUrl = $"/pages/document-detail.html?id={doc.Id}",
                });
            }
            catch { /* swallow */ }
        }

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
