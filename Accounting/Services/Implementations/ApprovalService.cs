using Accounting.Data;
using Accounting.Models.DTOs.Approval;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ApprovalService : IApprovalService
{
    private readonly AccountingDbContext _db;
    /// <summary>Legacy inbox writer — เหลือไว้สำหรับ user-specific event ที่
    /// ยังไม่ migrate. Production code ทุกแห่ง dispatch ผ่าน _notify แล้ว.</summary>
    private readonly INotificationService _notificationService;
    private readonly INotificationEngine? _notify;
    /// <summary>Resolved lazily to avoid the circular ctor dependency
    /// IDocumentService ↔ IApprovalService (DocumentService may also want
    /// to call SubmitForApprovalAsync in the future).</summary>
    private readonly IServiceProvider _services;

    // Escalation timeout in hours
    private const int EscalationTimeoutHours = 48;

    public ApprovalService(AccountingDbContext db, INotificationService notificationService,
        IServiceProvider services, INotificationEngine? notify = null)
    {
        _db = db;
        _notificationService = notificationService;
        _services = services;
        _notify = notify;
    }

    /// <summary>Migration helper — route ผ่าน NotificationEngine ถ้า inject
    /// แล้ว (production), fallback ไป NotificationService ถ้าไม่ (test fixture).
    /// duplicate audit #7 phase 1: consolidate dispatch path.</summary>
    private async Task NotifyUserAsync(Guid recipientUserId, Guid companyId,
        string eventKey, Models.Enums.NotificationType type, string title, string message,
        string? actionUrl, string? entityType, Guid? entityId)
    {
        if (_notify != null)
        {
            await _notify.DispatchAsync(companyId, eventKey, new NotificationContext
            {
                Title = title, Message = message, ActionUrl = actionUrl,
                EntityType = entityType, EntityId = entityId,
                BellType = type, RecipientUserId = recipientUserId,
            });
        }
        else
        {
            await _notificationService.SendAsync(recipientUserId, companyId, type,
                title, message, actionUrl, entityType, entityId);
        }
    }

    // ==================== Rules ====================

    public async Task<ApprovalRuleResponse> CreateRuleAsync(Guid companyId, CreateApprovalRuleRequest request)
    {
        var rule = new ApprovalRule
        {
            CompanyId = companyId,
            Name = request.Name,
            Description = request.Description,
            DocumentType = request.DocumentType,
            MinAmount = request.MinAmount,
            MaxAmount = request.MaxAmount,
            ProjectId = request.ProjectId
        };

        _db.ApprovalRules.Add(rule);

        foreach (var step in request.Steps)
        {
            _db.ApprovalSteps.Add(new ApprovalStep
            {
                ApprovalRuleId = rule.Id,
                StepOrder = step.StepOrder,
                ApproverUserId = step.ApproverUserId,
                IsRequired = step.IsRequired
            });
        }

        await _db.SaveChangesAsync();
        return await GetRuleResponseAsync(rule.Id);
    }

    public async Task<List<ApprovalRuleResponse>> GetRulesAsync(Guid companyId)
    {
        var rules = await _db.ApprovalRules
            .Include(r => r.Steps).ThenInclude(s => s.ApproverUser)
            .Where(r => r.CompanyId == companyId && r.IsActive)
            .OrderBy(r => r.Name)
            .ToListAsync();

        return rules.Select(MapRuleToResponse).ToList();
    }

    public async Task<ApprovalRuleResponse> UpdateRuleAsync(Guid companyId, Guid ruleId, CreateApprovalRuleRequest request)
    {
        var rule = await _db.ApprovalRules
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกฎการอนุมัติ");

        rule.Name = request.Name;
        rule.Description = request.Description;
        rule.DocumentType = request.DocumentType;
        rule.MinAmount = request.MinAmount;
        rule.MaxAmount = request.MaxAmount;
        rule.ProjectId = request.ProjectId;

        _db.ApprovalSteps.RemoveRange(rule.Steps);
        foreach (var step in request.Steps)
        {
            _db.ApprovalSteps.Add(new ApprovalStep
            {
                ApprovalRuleId = rule.Id,
                StepOrder = step.StepOrder,
                ApproverUserId = step.ApproverUserId,
                IsRequired = step.IsRequired
            });
        }

        await _db.SaveChangesAsync();
        return await GetRuleResponseAsync(rule.Id);
    }

    public async Task DeleteRuleAsync(Guid companyId, Guid ruleId)
    {
        var rule = await _db.ApprovalRules
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกฎการอนุมัติ");

        rule.IsActive = false;
        rule.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ==================== Requests ====================

    public async Task<ApprovalRequestResponse> SubmitForApprovalAsync(
        Guid companyId, string entityType, Guid entityId, Guid requestedByUserId)
    {
        // Pull the entity's amount + projectId + (for Documents) DocumentType
        // so rule selection can factor in all three filters. Other entity
        // types (JournalEntry, Payment, ExpenseClaim) skip docType matching
        // but still honour amount + project scope.
        decimal? entityAmount = null;
        Guid? entityProjectId = null;
        DocumentType? entityDocType = null;

        if (entityType == "Document")
        {
            var doc = await _db.Documents
                .Where(d => d.Id == entityId && d.CompanyId == companyId)
                .Select(d => new { d.TotalAmount, d.ProjectId, d.DocumentType })
                .FirstOrDefaultAsync();
            if (doc != null)
            {
                entityAmount = doc.TotalAmount;
                entityProjectId = doc.ProjectId;
                entityDocType = doc.DocumentType;
            }
        }
        else if (entityType == "JournalEntry")
        {
            var je = await _db.JournalEntries
                .Where(j => j.Id == entityId && j.CompanyId == companyId)
                .Select(j => new { j.TotalDebit, j.ProjectId })
                .FirstOrDefaultAsync();
            if (je != null)
            {
                entityAmount = je.TotalDebit;
                entityProjectId = je.ProjectId;
            }
        }
        else if (entityType == "Payment")
        {
            var p = await _db.Payments
                .Where(x => x.Id == entityId && x.CompanyId == companyId)
                .Select(x => new { x.Amount, x.ProjectId })
                .FirstOrDefaultAsync();
            if (p != null)
            {
                entityAmount = p.Amount;
                entityProjectId = p.ProjectId;
            }
        }
        else if (entityType == "ExpenseClaim")
        {
            var ec = await _db.ExpenseClaims
                .Where(x => x.Id == entityId && x.CompanyId == companyId)
                .Select(x => new { x.TotalAmount, x.ProjectId })
                .FirstOrDefaultAsync();
            if (ec != null)
            {
                entityAmount = ec.TotalAmount;
                entityProjectId = ec.ProjectId;
            }
        }

        var rules = await _db.ApprovalRules
            .Include(r => r.Steps).ThenInclude(s => s.ApproverUser)
            .Where(r => r.CompanyId == companyId && r.IsActive)
            .ToListAsync();

        // Score rules by specificity: rules that explicitly match
        // project + docType + amount range beat catch-all rules.
        // Highest score wins; ties broken by MinAmount (most specific
        // tier first).
        var rule = rules
            .Select(r => new { Rule = r, Score = ScoreRule(r, entityDocType, entityAmount, entityProjectId) })
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Rule.MinAmount ?? 0)
            .Select(x => x.Rule)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("ไม่พบกฎการอนุมัติที่เหมาะสม");

        var request = new ApprovalRequest
        {
            CompanyId = companyId,
            ApprovalRuleId = rule.Id,
            EntityType = entityType,
            EntityId = entityId,
            RequestedByUserId = requestedByUserId,
            OverallStatus = ApprovalStatus.Pending,
            CurrentStep = 1
        };

        _db.ApprovalRequests.Add(request);

        foreach (var step in rule.Steps.OrderBy(s => s.StepOrder))
        {
            _db.ApprovalActions.Add(new ApprovalAction
            {
                ApprovalRequestId = request.Id,
                StepOrder = step.StepOrder,
                ApproverUserId = step.ApproverUserId,
                Status = ApprovalStatus.Pending
            });
        }

        // Flip the underlying entity into WaitingApproval so the UI
        // surfaces "รออนุมัติ" and ApproveDocumentAsync's status guard
        // recognises the document as already-routed. Only Document is
        // wired today; other entity types stay untouched.
        if (entityType == "Document")
        {
            var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == entityId && d.CompanyId == companyId);
            if (doc != null && doc.Status == DocumentStatus.Draft)
            {
                doc.Status = DocumentStatus.WaitingApproval;
                doc.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();

        // Notify first approver
        var firstStep = rule.Steps.OrderBy(s => s.StepOrder).First();
        await NotifyUserAsync(firstStep.ApproverUserId, companyId,
            Models.Constants.NotificationEvents.ApprovalRequired,
            NotificationType.ApprovalRequired,
            "มีรายการรอการอนุมัติ",
            $"มี {entityType} รอการอนุมัติจากคุณ",
            $"/approvals/{request.Id}",
            entityType, entityId);

        return await GetApprovalRequestAsync(companyId, request.Id);
    }

    public async Task<ApprovalRequestResponse> GetApprovalRequestAsync(Guid companyId, Guid requestId)
    {
        var request = await _db.ApprovalRequests
            .Include(r => r.RequestedByUser)
            .Include(r => r.Actions).ThenInclude(a => a.ApproverUser)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบคำขออนุมัติ");

        return MapRequestToResponse(request);
    }

    /// <summary>คำขออนุมัติล่าสุดของ entity — ใช้ให้หน้าจอบอกสถานะ "รอใคร ขั้นไหน"
    /// และตัดสินว่าปุ่มควรเป็น "ส่งขออนุมัติ" หรือ "ส่งซ้ำ/เตือนผู้อนุมัติ"</summary>
    public async Task<ApprovalRequestResponse?> GetLatestForEntityAsync(
        Guid companyId, string entityType, Guid entityId)
    {
        var request = await _db.ApprovalRequests
            .Include(r => r.RequestedByUser)
            .Include(r => r.Actions).ThenInclude(a => a.ApproverUser)
            .Where(r => r.CompanyId == companyId && r.EntityType == entityType && r.EntityId == entityId)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync();
        return request == null ? null : MapRequestToResponse(request);
    }

    public async Task<List<ApprovalRequestResponse>> GetPendingApprovalsAsync(Guid companyId, Guid userId)
    {
        var requests = await _db.ApprovalRequests
            .Include(r => r.RequestedByUser)
            .Include(r => r.Actions).ThenInclude(a => a.ApproverUser)
            .Where(r => r.CompanyId == companyId && r.OverallStatus == ApprovalStatus.Pending)
            .Where(r => r.Actions.Any(a => a.ApproverUserId == userId && a.Status == ApprovalStatus.Pending && a.StepOrder == r.CurrentStep))
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync();

        return requests.Select(MapRequestToResponse).ToList();
    }

    public async Task<ApprovalRequestResponse> SubmitActionAsync(
        Guid companyId, Guid requestId, Guid userId, SubmitApprovalActionRequest actionRequest)
    {
        var request = await _db.ApprovalRequests
            .Include(r => r.Actions).ThenInclude(a => a.ApproverUser)
            .Include(r => r.RequestedByUser)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบคำขออนุมัติ");

        if (request.OverallStatus != ApprovalStatus.Pending)
            throw new InvalidOperationException("คำขอนี้ไม่อยู่ในสถานะรอการอนุมัติ");

        if (userId == request.RequestedByUserId)
            throw new InvalidOperationException("ผู้ขอไม่สามารถอนุมัติคำขอของตนเองได้");

        var action = request.Actions
            .FirstOrDefault(a => a.ApproverUserId == userId && a.StepOrder == request.CurrentStep && a.Status == ApprovalStatus.Pending)
            ?? throw new InvalidOperationException("คุณไม่มีสิทธิ์อนุมัติขั้นตอนนี้");

        // รอบ 193 (ฝ่ายค้าน C5): ขั้นสุดท้ายของเอกสาร = การอนุมัติเอกสารจริง ⇒ คำเตือนก่อนอนุมัติต้องถึงตาคนกด (422) ก่อนบันทึกอะไร
        // — เดิม finalize ส่ง acknowledgeWarnings:true ในนามระบบ ⇒ audit บอกว่า "รับทราบ" ทั้งที่ไม่มีใครเห็น [Σ-GAP]
        //   และเว็บกับมือถือ (ที่หยุดให้กดรับทราบ) ทำตัวคนละแบบ
        if (actionRequest.Status == ApprovalStatus.Approved && request.EntityType == "Document"
            && request.CurrentStep >= request.Actions.Max(a => a.StepOrder) && !actionRequest.AcknowledgeWarnings
            && _services.GetService(typeof(IDocumentService)) is IDocumentService previewSvc)
        {
            var pending = await previewSvc.PreviewApprovalWarningsAsync(companyId, request.EntityId);
            if (pending.Count > 0)
                throw new DocumentApprovalWarningsException(pending);
        }

        action.Status = actionRequest.Status;
        action.ActionAt = DateTime.UtcNow;
        action.Comments = actionRequest.Comments;

        if (actionRequest.Status == ApprovalStatus.Rejected)
        {
            request.OverallStatus = ApprovalStatus.Rejected;

            // Bounce the underlying entity back to Draft so the requester
            // can fix + resubmit. Only Document wired today.
            if (request.EntityType == "Document")
            {
                var doc = await _db.Documents.FirstOrDefaultAsync(d =>
                    d.Id == request.EntityId && d.CompanyId == companyId);
                if (doc != null && doc.Status == DocumentStatus.WaitingApproval)
                {
                    doc.Status = DocumentStatus.Draft;
                    doc.UpdatedAt = DateTime.UtcNow;
                }
            }

            await NotifyUserAsync(request.RequestedByUserId, companyId,
                Models.Constants.NotificationEvents.ApprovalRejected,
                NotificationType.ApprovalRequired,
                "คำขออนุมัติถูกปฏิเสธ",
                $"คำขออนุมัติ {request.EntityType} ถูกปฏิเสธ: {actionRequest.Comments}",
                $"/approvals/{request.Id}",
                request.EntityType, request.EntityId);
        }
        else if (actionRequest.Status == ApprovalStatus.Approved)
        {
            var maxStep = request.Actions.Max(a => a.StepOrder);
            if (request.CurrentStep >= maxStep)
            {
                request.OverallStatus = ApprovalStatus.Approved;

                await NotifyUserAsync(request.RequestedByUserId, companyId,
                    Models.Constants.NotificationEvents.ApprovalGranted,
                    NotificationType.ApprovalRequired,
                    "คำขออนุมัติได้รับการอนุมัติแล้ว",
                    $"คำขออนุมัติ {request.EntityType} ได้รับการอนุมัติแล้ว",
                    $"/approvals/{request.Id}",
                    request.EntityType, request.EntityId);
            }
            else
            {
                request.CurrentStep++;

                var nextAction = request.Actions.FirstOrDefault(a => a.StepOrder == request.CurrentStep);
                if (nextAction != null)
                {
                    await NotifyUserAsync(nextAction.ApproverUserId, companyId,
                        Models.Constants.NotificationEvents.ApprovalRequired,
                        NotificationType.ApprovalRequired,
                        "มีรายการรอการอนุมัติ",
                        $"มี {request.EntityType} รอการอนุมัติจากคุณ (ขั้นตอนที่ {request.CurrentStep})",
                        $"/approvals/{request.Id}",
                        request.EntityType, request.EntityId);
                }
            }
        }

        await _db.SaveChangesAsync();

        // After the approval-side state is persisted, finalize the
        // underlying entity (e.g. push a Document from WaitingApproval
        // through ApproveDocumentAsync → JE post + stock + cost feed).
        // Done outside the SaveChanges window so a finalize failure
        // doesn't roll back the approval audit trail; the failure logs
        // and the document just sits at WaitingApproval for manual
        // intervention.
        if (request.OverallStatus == ApprovalStatus.Approved)
        {
            await TryFinalizeApprovedEntityAsync(companyId, request.EntityType, request.EntityId, actionRequest, userId);
        }

        return MapRequestToResponse(request);
    }

    /// <summary>Dispatch to the entity-specific approval gateway when an
    /// ApprovalRequest reaches OverallStatus = Approved. Today only
    /// "Document" is wired — JournalEntry / Payment / ExpenseClaim /
    /// SalaryAdvance hooks are stubs and can be added later by
    /// resolving the appropriate service. Errors are caught + logged
    /// (via console for now) so a failed downstream finalize never
    /// blocks the approval state save.</summary>
    private async Task TryFinalizeApprovedEntityAsync(Guid companyId, string entityType, Guid entityId,
        SubmitApprovalActionRequest actionRequest, Guid finalApproverUserId)
    {
        try
        {
            switch (entityType)
            {
                case "Document":
                {
                    var docSvc = _services.GetService(typeof(IDocumentService)) as IDocumentService;
                    if (docSvc == null) return;
                    // รอบ 193 (ฝ่ายค้าน C5): ผู้อนุมัติขั้นสุดท้ายตัวจริง (ไม่ใช่ป้าย "approval-rule") · รับทราบคำเตือนได้เฉพาะเมื่อ
                    // คนกด "รับทราบ" บนหน้าจอแล้ว (SubmitActionAsync หยุดให้เห็นรายการก่อน) — ห้ามประทับแทน
                    var who = finalApproverUserId.ToString();
                    await docSvc.ApproveDocumentAsync(companyId, entityId, who,
                        actionRequest.AcknowledgeWarnings ? Accounting.Helpers.ApprovalAckSource.User
                                                          : Accounting.Helpers.ApprovalAckSource.None,
                        withAiHints: false);
                    break;
                }
                // Future hooks: case "JournalEntry": ...
                //               case "ExpenseClaim": ...
                //               case "SalaryAdvance": ...
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ApprovalService] Auto-finalize failed for {entityType} {entityId}: {ex.Message}");
        }
    }

    // ==================== Escalation (called by background job) ====================

    /// <summary>
    /// Check for pending approvals that have exceeded timeout and escalate them
    /// </summary>
    public async Task<int> EscalateOverdueApprovalsAsync()
    {
        var cutoffTime = DateTime.UtcNow.AddHours(-EscalationTimeoutHours);

        var overdueRequests = await _db.ApprovalRequests
            .Include(r => r.Actions).ThenInclude(a => a.ApproverUser)
            .Where(r => r.OverallStatus == ApprovalStatus.Pending
                && r.RequestedAt < cutoffTime)
            .ToListAsync();

        var escalatedCount = 0;

        foreach (var request in overdueRequests)
        {
            var currentAction = request.Actions
                .FirstOrDefault(a => a.StepOrder == request.CurrentStep && a.Status == ApprovalStatus.Pending);

            if (currentAction == null) continue;

            // Send reminder notification
            await NotifyUserAsync(currentAction.ApproverUserId, request.CompanyId,
                Models.Constants.NotificationEvents.ApprovalRequired,
                NotificationType.SecurityAlert,
                "แจ้งเตือน: คำขออนุมัติค้าง",
                $"คำขออนุมัติ {request.EntityType} รอการดำเนินการเกิน {EscalationTimeoutHours} ชั่วโมง",
                $"/approvals/{request.Id}",
                request.EntityType, request.EntityId);

            escalatedCount++;
        }

        return escalatedCount;
    }

    // ==================== Private Helpers ====================

    // Returns -1 if the rule does NOT match (hard filter violated);
    // otherwise returns a positive score where bigger = more specific.
    // - +3 if rule pins a project and it matches
    // - +2 if rule pins a docType and it matches
    // - +1 if rule pins an amount range and entity amount falls inside
    // Catch-all rules (all nullable filters null) score 0 — they only win
    // when no more-specific rule applies.
    private static int ScoreRule(ApprovalRule r, DocumentType? entityDocType,
        decimal? entityAmount, Guid? entityProjectId)
    {
        var score = 0;
        if (r.ProjectId.HasValue)
        {
            if (r.ProjectId != entityProjectId) return -1;
            score += 3;
        }
        if (r.DocumentType.HasValue)
        {
            if (r.DocumentType != entityDocType) return -1;
            score += 2;
        }
        if (r.MinAmount.HasValue || r.MaxAmount.HasValue)
        {
            var amt = entityAmount ?? 0;
            if (r.MinAmount.HasValue && amt < r.MinAmount.Value) return -1;
            if (r.MaxAmount.HasValue && amt > r.MaxAmount.Value) return -1;
            score += 1;
        }
        return score;
    }

    private async Task<ApprovalRuleResponse> GetRuleResponseAsync(Guid ruleId)
    {
        var rule = await _db.ApprovalRules
            .Include(r => r.Steps).ThenInclude(s => s.ApproverUser)
            .FirstAsync(r => r.Id == ruleId);
        return MapRuleToResponse(rule);
    }

    private static ApprovalRuleResponse MapRuleToResponse(ApprovalRule rule) =>
        new(rule.Id, rule.Name, rule.Description, rule.DocumentType,
            rule.MinAmount, rule.MaxAmount, rule.IsActive,
            rule.Steps.OrderBy(s => s.StepOrder).Select(s =>
                new ApprovalStepResponse(s.StepOrder, s.ApproverUserId, s.ApproverUser?.FullName ?? "", s.IsRequired)).ToList(),
            rule.ProjectId);

    private static ApprovalRequestResponse MapRequestToResponse(ApprovalRequest r) =>
        new(r.Id, r.EntityType, r.EntityId, r.RequestedByUser?.FullName ?? "",
            r.RequestedAt, r.OverallStatus, r.CurrentStep,
            r.Actions.OrderBy(a => a.StepOrder).Select(a =>
                new ApprovalActionResponse(a.StepOrder, a.ApproverUser?.FullName ?? "",
                    a.Status, a.ActionAt, a.Comments)).ToList());

    public async Task<ApprovalRule?> FindMatchingRuleAsync(
        Guid companyId, DocumentType docType, decimal amount, Guid? projectId)
    {
        return await _db.ApprovalRules.AsNoTracking()
            .Include(r => r.Steps)
            .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.IsActive
                && (r.DocumentType == null || r.DocumentType == docType)
                && (r.MinAmount == null || amount >= r.MinAmount.Value)
                && (r.MaxAmount == null || amount <= r.MaxAmount.Value)
                && (r.ProjectId == null || r.ProjectId == projectId))
            // เลือก rule ที่ specific ที่สุดก่อน — type-specific + amount band
            // แคบสุด > type-only > generic
            .OrderByDescending(r => r.DocumentType != null ? 1 : 0)
            .ThenByDescending(r => r.MinAmount ?? 0)
            .FirstOrDefaultAsync();
    }

    public async Task<ApprovalWorkflowGate> CheckGateAsync(Guid companyId, Guid documentId)
    {
        var existing = await _db.ApprovalRequests.AsNoTracking()
            .Where(r => r.CompanyId == companyId
                && r.EntityType == "Document" && r.EntityId == documentId
                && !r.IsDeleted)
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new { r.Id, r.OverallStatus })
            .FirstOrDefaultAsync();
        if (existing == null)
            return new ApprovalWorkflowGate(true, null, null);
        if (existing.OverallStatus == ApprovalStatus.Approved)
            return new ApprovalWorkflowGate(true, null, existing.Id);
        if (existing.OverallStatus == ApprovalStatus.Rejected)
            return new ApprovalWorkflowGate(false, "เอกสารถูกปฏิเสธในขั้น approval workflow — ส่งใหม่หรือแก้ไขก่อน", existing.Id);
        return new ApprovalWorkflowGate(false,
            $"เอกสารต้องผ่าน multi-level approval workflow ก่อน (ขั้นปัจจุบัน: รออนุมัติ)",
            existing.Id);
    }
}
