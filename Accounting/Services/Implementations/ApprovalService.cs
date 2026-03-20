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
    private readonly INotificationService _notificationService;

    // Escalation timeout in hours
    private const int EscalationTimeoutHours = 48;

    public ApprovalService(AccountingDbContext db, INotificationService notificationService)
    {
        _db = db;
        _notificationService = notificationService;
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
            MaxAmount = request.MaxAmount
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
        // Find the most specific matching rule (by document type and amount range)
        var rules = await _db.ApprovalRules
            .Include(r => r.Steps).ThenInclude(s => s.ApproverUser)
            .Where(r => r.CompanyId == companyId && r.IsActive)
            .OrderByDescending(r => r.MinAmount) // Most specific first
            .ToListAsync();

        var rule = rules.FirstOrDefault(r => r.DocumentType == null ||
            (entityType == "Document" && r.DocumentType != null))
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

        await _db.SaveChangesAsync();

        // Notify first approver
        var firstStep = rule.Steps.OrderBy(s => s.StepOrder).First();
        await _notificationService.SendAsync(firstStep.ApproverUserId, companyId,
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

        var action = request.Actions
            .FirstOrDefault(a => a.ApproverUserId == userId && a.StepOrder == request.CurrentStep && a.Status == ApprovalStatus.Pending)
            ?? throw new InvalidOperationException("คุณไม่มีสิทธิ์อนุมัติขั้นตอนนี้");

        action.Status = actionRequest.Status;
        action.ActionAt = DateTime.UtcNow;
        action.Comments = actionRequest.Comments;

        if (actionRequest.Status == ApprovalStatus.Rejected)
        {
            request.OverallStatus = ApprovalStatus.Rejected;

            await _notificationService.SendAsync(request.RequestedByUserId, companyId,
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

                await _notificationService.SendAsync(request.RequestedByUserId, companyId,
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
                    await _notificationService.SendAsync(nextAction.ApproverUserId, companyId,
                        NotificationType.ApprovalRequired,
                        "มีรายการรอการอนุมัติ",
                        $"มี {request.EntityType} รอการอนุมัติจากคุณ (ขั้นตอนที่ {request.CurrentStep})",
                        $"/approvals/{request.Id}",
                        request.EntityType, request.EntityId);
                }
            }
        }

        await _db.SaveChangesAsync();
        return MapRequestToResponse(request);
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
            await _notificationService.SendAsync(currentAction.ApproverUserId, request.CompanyId,
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
                new ApprovalStepResponse(s.StepOrder, s.ApproverUserId, s.ApproverUser?.FullName ?? "", s.IsRequired)).ToList());

    private static ApprovalRequestResponse MapRequestToResponse(ApprovalRequest r) =>
        new(r.Id, r.EntityType, r.EntityId, r.RequestedByUser?.FullName ?? "",
            r.RequestedAt, r.OverallStatus, r.CurrentStep,
            r.Actions.OrderBy(a => a.StepOrder).Select(a =>
                new ApprovalActionResponse(a.StepOrder, a.ApproverUser?.FullName ?? "",
                    a.Status, a.ActionAt, a.Comments)).ToList());
}
