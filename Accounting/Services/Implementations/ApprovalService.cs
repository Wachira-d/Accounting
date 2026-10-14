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

    /// <summary>รอบ 200 (A07/G2-09): ตรวจชื่อ · ช่วงเงิน · ขั้น · ผู้อนุมัติเป็นสมาชิกบริษัทนี้ — ก่อนแตะข้อมูล (สร้าง/แก้ใช้ตัวเดียวกัน)</summary>
    private async Task ValidateRuleAsync(Guid companyId, CreateApprovalRuleRequest request)
    {
        var steps = (request.Steps ?? new List<ApprovalStepRequest>())
            .Select(s => (s.StepOrder, s.ApproverUserId)).ToList();
        // ฝ่ายค้าน F4: นิยาม "สมาชิก" ตัวเดียวกับตอนส่งคำขอ (Helpers/CompanyMembership — ตัดบัญชีที่ถูกปิด/ระงับ) — เดิมนับแถว CompanyUsers อย่างเดียว
        // ⇒ บันทึกกฎด้วยผู้อนุมัติที่ถูกระงับได้ แล้วทุกคำขอโดน 409 "ผู้อนุมัติออกแล้ว" โดยหน้าแก้กฎไม่เคยเตือน
        var members = await Accounting.Helpers.CompanyMembership.ActiveMemberIdsAsync(
            _db, companyId, steps.Select(s => s.ApproverUserId));
        var problem = Accounting.Helpers.ApprovalRuleValidation.Problem(
            request.Name, request.MinAmount, request.MaxAmount, steps, members);
        if (problem != null) throw new Accounting.Helpers.BusinessRuleException(problem);
        // รอบ 200 ทีม RF (R200-X3): ขอบเขตโครงการต้องเป็นโครงการของบริษัทนี้ (tenant) — เดิมรับ Guid อะไรก็ได้ ⇒ กฎที่ไม่มีวันทำงาน
        if (request.ProjectId is { } pid
            && !await _db.Projects.AsNoTracking().AnyAsync(p => p.Id == pid && p.CompanyId == companyId))
            throw new Accounting.Helpers.BusinessRuleException("ไม่พบโครงการที่เลือกในบริษัทนี้ — เลือกโครงการจากรายการใหม่ หรือเลือก “ทุกโครงการ”");
    }

    public async Task<ApprovalRuleResponse> CreateRuleAsync(Guid companyId, CreateApprovalRuleRequest request)
    {
        await ValidateRuleAsync(companyId, request);
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

        var result = new List<ApprovalRuleResponse>(rules.Count);
        foreach (var r in rules)
            result.Add(MapRuleToResponse(r) with { Warning = await FinalApproverPermissionWarningAsync(companyId, r) });
        return result;
    }

    /// <summary>ฝ่ายค้าน F4 (ID#9): ขั้นสุดท้ายของเอกสารต้องมีสิทธิ์อนุมัติเอกสาร (ด่านใน SubmitActionAsync) — หน้ากฎเตือนล่วงหน้า (ไม่บล็อกการบันทึก ·
    /// คำตัดสินข้อ 150) เพื่อไม่ให้ผู้ดูแลรู้ตอนคำขอจริงติด 403 · ตัวตัดสินสิทธิ์ตัวเดียวกับด่าน (DocumentPermissionHelper)</summary>
    private async Task<string?> FinalApproverPermissionWarningAsync(Guid companyId, ApprovalRule rule)
    {
        var last = rule.Steps.OrderBy(s => s.StepOrder).LastOrDefault();
        if (last == null) return null;
        if (_services.GetService(typeof(IPermissionService)) is not IPermissionService perms) return null;
        var can = rule.DocumentType is { } t
            ? await Accounting.Helpers.DocumentPermissionHelper.CanApproveAsync(perms, companyId, last.ApproverUserId, t)
            : await perms.HasPermissionAsync(companyId, last.ApproverUserId, Models.Constants.PermissionKeys.DocumentApprove);
        return can ? null
            : $"ผู้อนุมัติขั้นสุดท้าย ({(string.IsNullOrWhiteSpace(last.ApproverUser?.FullName) ? "ขั้นที่ " + last.StepOrder : last.ApproverUser!.FullName)}) "
              + "ยังไม่มีสิทธิ์อนุมัติเอกสาร — คำขอที่เป็นเอกสารจะอนุมัติขั้นสุดท้ายไม่ได้: เพิ่มสิทธิ์ “อนุมัติเอกสาร” ให้บทบาทของคนนั้น หรือเลือกผู้อนุมัติขั้นสุดท้ายคนอื่น";
    }

    public async Task<ApprovalRuleResponse> UpdateRuleAsync(Guid companyId, Guid ruleId, CreateApprovalRuleRequest request)
    {
        var rule = await _db.ApprovalRules
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกฎการอนุมัติ");
        await ValidateRuleAsync(companyId, request);

        rule.Name = request.Name;
        // รอบ 200 ทีม RF (R200-X3): ไม่มีคีย์/null = คงเดิม · "" = ล้าง (เดิมเขียนทับด้วย null ⇒ กฎจำกัดโครงการกลายเป็นทุกโครงการเงียบ ๆ)
        rule.Description = Accounting.Helpers.ApprovalRuleValidation.PatchDescription(rule.Description, request.Description);
        rule.DocumentType = request.DocumentType;
        rule.MinAmount = request.MinAmount;
        rule.MaxAmount = request.MaxAmount;
        rule.ProjectId = Accounting.Helpers.ApprovalRuleValidation.PatchProjectId(rule.ProjectId, request.ProjectId, request.ClearProjectId);

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

        // รอบ 2026-10-14 ทีม F4 (ID#11): ผู้อนุมัติของกฎต้องยังเป็นสมาชิกบริษัท — เดิมส่งคำขอไปหาคนที่ออกแล้ว ⇒ ไม่มีใครกดได้
        // (SubmitActionAsync รับเฉพาะผู้อนุมัติของขั้น) ⇒ เอกสารค้าง "รออนุมัติ" ตลอดกาลเงียบ ๆ · ไม่ข้ามขั้น (= ไม่ลดการแบ่งแยกหน้าที่)
        // แต่บอกให้แก้กฎก่อนส่ง (ทางไปต่อ) — ตัวตัดสินเดียว Helpers/ApprovalApproverCheck + Helpers/CompanyMembership
        var orderedRuleSteps = rule.Steps.OrderBy(s => s.StepOrder).ToList();
        var activeRuleApprovers = await Accounting.Helpers.CompanyMembership.ActiveMemberIdsAsync(
            _db, companyId, orderedRuleSteps.Select(s => s.ApproverUserId));
        var departedProblem = Accounting.Helpers.ApprovalApproverCheck.DepartedStepsProblem(rule.Name,
            orderedRuleSteps.Select(s => (s.StepOrder, s.ApproverUserId, s.ApproverUser?.FullName)).ToList(), activeRuleApprovers);
        if (departedProblem != null)
            throw new Accounting.Helpers.BusinessRuleException(departedProblem,
                Accounting.Helpers.ApprovalApproverCheck.DepartedRuleCode, 409);

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

        return (await MapWithMembershipAsync(companyId, new[] { request }))[0];
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
        return request == null ? null : (await MapWithMembershipAsync(companyId, new[] { request }))[0];
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

        return await MapWithMembershipAsync(companyId, requests);
    }

    /// <summary>ID#11 — คำขอที่รออนุมัติแต่ผู้อนุมัติของขั้นที่ยังไม่ได้ทำ (ขั้นปัจจุบันหรือขั้นถัดไป) ไม่ได้เป็นสมาชิกบริษัทแล้ว ·
    /// ให้ผู้ดูแลเห็นและกด "โอนผู้อนุมัติ" (ทางไปต่อ — เดิมไม่มีใครรู้ว่าค้าง)</summary>
    public async Task<List<ApprovalRequestResponse>> GetStalledRequestsAsync(Guid companyId)
    {
        var pending = await _db.ApprovalRequests
            .Include(r => r.RequestedByUser)
            .Include(r => r.Actions).ThenInclude(a => a.ApproverUser)
            .Where(r => r.CompanyId == companyId && r.OverallStatus == ApprovalStatus.Pending)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync();
        var mapped = await MapWithMembershipAsync(companyId, pending);
        return mapped.Where(r => r.StaleApproverNotice != null).ToList();
    }

    /// <summary>ID#11 — ผู้ดูแล (สิทธิ์ตั้งค่าบริษัท · ด่านที่ controller) โอนขั้นที่ค้างให้สมาชิกคนใหม่ · แก้แถว ApprovalAction ที่รออยู่
    /// (ไม่สร้างแถวใหม่) · โอนได้เฉพาะขั้นที่ผู้อนุมัติเดิมออกไปแล้ว และห้ามโอนให้ผู้ขอ (SoD) — ตัวตัดสิน Helpers/ApprovalApproverCheck.ReassignProblem
    /// · ร่องรอย: หมายเหตุบนขั้น + audit log (append-only)</summary>
    public async Task<ApprovalRequestResponse> ReassignApproverAsync(
        Guid companyId, Guid requestId, Guid actorUserId, ReassignApproverRequest req)
    {
        var request = await _db.ApprovalRequests
            .Include(r => r.Actions).ThenInclude(a => a.ApproverUser)
            .Include(r => r.RequestedByUser)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบคำขออนุมัติ — อาจถูกลบไปแล้ว รีเฟรชรายการ");
        var stepOrder = req.StepOrder ?? request.CurrentStep;
        var action = request.Actions.FirstOrDefault(a => a.StepOrder == stepOrder
            && a.Status == ApprovalStatus.Pending && a.StepOrder >= request.CurrentStep);
        var members = await Accounting.Helpers.CompanyMembership.ActiveMemberIdsAsync(_db, companyId,
            new[] { req.NewApproverUserId, action?.ApproverUserId ?? Guid.Empty });
        var problem = Accounting.Helpers.ApprovalApproverCheck.ReassignProblem(
            request.OverallStatus == ApprovalStatus.Pending, action != null,
            action != null && members.Contains(action.ApproverUserId),
            req.NewApproverUserId, members.Contains(req.NewApproverUserId), request.RequestedByUserId,
            request.Actions.Any(a => a.Status == ApprovalStatus.Approved && a.ApproverUserId == req.NewApproverUserId));
        if (problem != null)
            throw new Accounting.Helpers.BusinessRuleException(problem, Accounting.Helpers.ApprovalApproverCheck.ReassignRuleCode, 409);

        var previousApproverId = action!.ApproverUserId;
        var previousName = action.ApproverUser?.FullName;
        // ผู้ใช้คนใหม่แบบ tracked — ตั้งทั้ง FK และ navigation ให้ตรงกัน (navigation เดิมยังชี้คนเก่าที่โหลดมากับ Include)
        var newUser = await _db.Users.FirstAsync(u => u.Id == req.NewApproverUserId);
        var note = $"[โอนผู้อนุมัติ {DateTime.UtcNow.AddHours(7):dd/MM/yyyy HH:mm}] จาก {previousName ?? "(ผู้ใช้ที่ออกแล้ว)"} → {newUser.FullName}"
            + (string.IsNullOrWhiteSpace(req.Reason) ? "" : $" · เหตุผล: {req.Reason!.Trim()}");
        action.ApproverUserId = req.NewApproverUserId;
        action.ApproverUser = newUser;
        action.Comments = string.IsNullOrWhiteSpace(action.Comments) ? note : action.Comments + "\n" + note;
        action.UpdatedAt = DateTime.UtcNow;
        action.UpdatedBy = actorUserId.ToString();
        request.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorUserId,
            Action = AuditAction.Update,
            EntityType = nameof(ApprovalRequest),
            EntityId = request.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { stepOrder, approverUserId = previousApproverId }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "reassign-approver", stepOrder, approverUserId = req.NewApproverUserId, reason = req.Reason,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        if (stepOrder == request.CurrentStep)
            await NotifyUserAsync(req.NewApproverUserId, companyId,
                Models.Constants.NotificationEvents.ApprovalRequired,
                NotificationType.ApprovalRequired,
                "มีรายการรอการอนุมัติ (โอนมาให้คุณ)",
                $"มี {request.EntityType} รอการอนุมัติจากคุณ (ขั้นตอนที่ {request.CurrentStep} — โอนจากผู้อนุมัติที่ออกจากบริษัทแล้ว)",
                $"/approvals/{request.Id}",
                request.EntityType, request.EntityId);

        return await GetApprovalRequestAsync(companyId, request.Id);
    }

    public async Task<ApprovalRequestResponse> SubmitActionAsync(
        Guid companyId, Guid requestId, Guid userId, SubmitApprovalActionRequest actionRequest)
    {
        // รอบ 2026-10-14 ทีม F4 (ID#9): เมธอดนี้เป็นเจ้าของกติกาอนุมัติเพียงตัวเดียวของทั้งเว็บและมือถือ (MobileApiService เรียกตัวนี้) —
        // ผลต้องเป็นอนุมัติ/ปฏิเสธเท่านั้น (เดิมส่ง Pending มาได้ ⇒ ไม่มีอะไรเกิดขึ้นเงียบ ๆ) · ข้อผิดพลาดเป็นสถานะ HTTP + ข้อความไทยพร้อมทางไปต่อ
        if (actionRequest.Status is not (ApprovalStatus.Approved or ApprovalStatus.Rejected))
            throw new Accounting.Helpers.BusinessRuleException("ผลการพิจารณาต้องเป็น “อนุมัติ” หรือ “ปฏิเสธ” เท่านั้น", "APPROVAL-ACTION", 400);

        var request = await _db.ApprovalRequests
            .Include(r => r.Actions).ThenInclude(a => a.ApproverUser)
            .Include(r => r.RequestedByUser)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบคำขออนุมัติ — อาจถูกลบไปแล้ว รีเฟรชรายการ");

        if (request.OverallStatus != ApprovalStatus.Pending)
            throw new Accounting.Helpers.BusinessRuleException(
                "คำขอนี้ไม่อยู่ในสถานะรออนุมัติแล้ว (อาจมีผู้อนุมัติหรือปฏิเสธไปก่อน) — รีเฟรชรายการ", "APPROVAL-NOT-PENDING", 409);

        if (userId == request.RequestedByUserId)
            throw new Accounting.Helpers.BusinessRuleException(
                "ผู้ขอไม่สามารถอนุมัติหรือปฏิเสธคำขอของตนเองได้ — ให้ผู้อนุมัติที่กำหนดในกฎเป็นผู้ดำเนินการ", "APPROVAL-SOD-SELF", 403);

        // ผู้กดต้องเป็นผู้อนุมัติของขั้นปัจจุบัน — ทุกขั้น รวมการปฏิเสธ (มือถือเดิมไม่ตรวจเลย: ใครก็เลื่อนขั้น/ตีกลับได้)
        var action = request.Actions
            .FirstOrDefault(a => a.ApproverUserId == userId && a.StepOrder == request.CurrentStep && a.Status == ApprovalStatus.Pending)
            ?? throw new Accounting.Helpers.BusinessRuleException(
                Accounting.Helpers.ApprovalApproverCheck.NotAssignedMessage(request.CurrentStep),
                Accounting.Helpers.ApprovalApproverCheck.NotAssignedRuleCode, 403);

        // ขั้นสุดท้ายของเอกสาร = อนุมัติเอกสารจริง ⇒ ผู้กดต้องมีสิทธิ์อนุมัติเอกสารชนิดนั้นด้วย (ด่านเดียวกับปุ่มอนุมัติบนหน้าเอกสาร) —
        // เดิมมีเฉพาะช่องมือถือ (ERP_REVIEW G-05) ⇒ เว็บกับมือถือเดินกติกาต่างกัน · ตรวจก่อนบันทึกอะไร
        if (actionRequest.Status == ApprovalStatus.Approved && request.EntityType == "Document"
            && request.CurrentStep >= request.Actions.Max(a => a.StepOrder))
            await EnsureMayApproveDocumentAsync(companyId, userId, request.EntityId);

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
        string? finalizeError = null;
        if (request.OverallStatus == ApprovalStatus.Approved)
        {
            finalizeError = await TryFinalizeApprovedEntityAsync(companyId, request.EntityType, request.EntityId, actionRequest, userId);
        }

        // PP36_REVIEW P0-2: ขั้นสุดท้ายอนุมัติเอกสารไม่สำเร็จ ⇒ คำตอบต้องบอก (เดิมตอบ "อนุมัติแล้ว" ขณะเอกสารยังรออนุมัติ — error อยู่แค่ Console)
        return (await MapWithMembershipAsync(companyId, new[] { request }))[0] with { FinalizeError = finalizeError };
    }

    /// <summary>ด่านสิทธิ์อนุมัติเอกสารของขั้นสุดท้าย — ตัวเดียวกับปุ่มอนุมัติบนหน้าเอกสาร (<c>DocumentPermissionHelper.CanApproveAsync</c>)</summary>
    private async Task EnsureMayApproveDocumentAsync(Guid companyId, Guid userId, Guid documentId)
    {
        var perms = _services.GetService(typeof(IPermissionService)) as IPermissionService
            ?? throw new InvalidOperationException("ไม่พบบริการสิทธิ์ (IPermissionService) — อนุมัติขั้นสุดท้ายไม่ได้");
        var docType = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => (DocumentType?)d.DocumentType)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("ไม่พบเอกสารของคำขอนี้ (อาจถูกลบแล้ว) — ให้ผู้ขอส่งขออนุมัติใหม่");
        if (!await Accounting.Helpers.DocumentPermissionHelper.CanApproveAsync(perms, companyId, userId, docType))
            throw new Accounting.Helpers.BusinessRuleException(
                $"คุณเป็นผู้อนุมัติขั้นสุดท้าย แต่ยังไม่มีสิทธิ์อนุมัติ{Accounting.Helpers.DocumentTypeNames.Title(docType, "th")} — "
                + "ขอให้ผู้ดูแลเพิ่มสิทธิ์ “อนุมัติเอกสาร” ให้บทบาทของคุณที่หน้าตั้งค่าบทบาทและสิทธิ์ หรือแก้กฎการอนุมัติให้ขั้นสุดท้ายเป็นผู้ที่มีสิทธิ์",
                "PERM-DOC-APPROVE", 403);
    }

    /// <summary>Dispatch to the entity-specific approval gateway when an
    /// ApprovalRequest reaches OverallStatus = Approved. Today only
    /// "Document" is wired — JournalEntry / Payment / ExpenseClaim /
    /// SalaryAdvance hooks are stubs and can be added later by
    /// resolving the appropriate service. A failed downstream finalize never
    /// blocks the approval state save — แต่ต้องล้มดัง (PP36_REVIEW P0-2): หมายเหตุบนเอกสาร + คืนข้อความให้คำตอบของ workflow
    /// (เดิม Console.Error อย่างเดียว ⇒ ผู้อนุมัติขั้นสุดท้ายเห็น "อนุมัติแล้ว" ขณะเอกสารยังรออนุมัติ/ไม่มี JE) · คืน null = สำเร็จ/ไม่มีอะไรต้องทำ</summary>
    private async Task<string?> TryFinalizeApprovedEntityAsync(Guid companyId, string entityType, Guid entityId,
        SubmitApprovalActionRequest actionRequest, Guid finalApproverUserId)
    {
        var docSvc = entityType == "Document" ? _services.GetService(typeof(IDocumentService)) as IDocumentService : null;
        try
        {
            switch (entityType)
            {
                case "Document":
                {
                    if (docSvc == null) return null;
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (docSvc != null)
            {
                // ApproveDocumentAsync ถอยค่าค้างใน context แล้ว ⇒ หมายเหตุลงเอกสารด้วยสถานะจริง (ตัวเดียวของทุกทางเข้า)
                var outcome = await docSvc.RecordAutoApproveFailureAsync(companyId, entityId, "ลำดับอนุมัติครบทุกขั้น", ex);
                return outcome.Message;
            }
            Console.Error.WriteLine($"[ApprovalService] Auto-finalize failed for {entityType} {entityId}: {ex.Message}");
            return $"อนุมัติ {entityType} ขั้นสุดท้ายไม่สำเร็จ: {ex.Message}";
        }
        return null;
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

        // ID#11: สมาชิกภาพของผู้อนุมัติขั้นปัจจุบัน — query เดียวต่อบริษัท (ฝ่ายค้าน F4: เดิมยิงทีละคำขอข้ามทุก tenant)
        var activeByCompany = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var g in overdueRequests.GroupBy(r => r.CompanyId))
            activeByCompany[g.Key] = await Accounting.Helpers.CompanyMembership.ActiveMemberIdsAsync(_db, g.Key,
                g.SelectMany(r => r.Actions).Where(a => a.Status == ApprovalStatus.Pending).Select(a => a.ApproverUserId));
        const string departedTitle = "คำขออนุมัติค้าง: ผู้อนุมัติออกจากบริษัทแล้ว";
        var noticeSince = DateTime.UtcNow.AddHours(-24);

        foreach (var request in overdueRequests)
        {
            var currentAction = request.Actions
                .FirstOrDefault(a => a.StepOrder == request.CurrentStep && a.Status == ApprovalStatus.Pending);

            if (currentAction == null) continue;

            // ID#11: ผู้อนุมัติของขั้นปัจจุบันออกจากบริษัทแล้ว ⇒ เตือนคนนั้นไม่มีประโยชน์ (ไม่มีใครเห็น) — แจ้งผู้ขอพร้อมทางไปต่อแทน
            // ไม่เกินวันละครั้งต่อคำขอ (งานนี้รันทุก 15 นาที — ฝ่ายค้าน F4: เดิมจะเตือนซ้ำ ~96 ครั้ง/วัน)
            if (!activeByCompany[request.CompanyId].Contains(currentAction.ApproverUserId))
            {
                var alreadyNotified = await _db.Notifications.AsNoTracking().AnyAsync(n =>
                    n.UserId == request.RequestedByUserId && n.CompanyId == request.CompanyId
                    && n.EntityId == request.EntityId && n.Title == departedTitle && n.CreatedAt >= noticeSince);
                if (alreadyNotified) continue;
                var notice = Accounting.Helpers.ApprovalApproverCheck.StalledNotice(
                    new[] { (currentAction.StepOrder, currentAction.ApproverUser?.FullName) });
                await NotifyUserAsync(request.RequestedByUserId, request.CompanyId,
                    Models.Constants.NotificationEvents.ApprovalRequired,
                    NotificationType.SecurityAlert,
                    departedTitle,
                    notice ?? "",
                    $"/approvals/{request.Id}",
                    request.EntityType, request.EntityId);
                escalatedCount++;
                continue;
            }

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
        return MapRuleToResponse(rule) with { Warning = await FinalApproverPermissionWarningAsync(rule.CompanyId, rule) };
    }

    private static ApprovalRuleResponse MapRuleToResponse(ApprovalRule rule) =>
        new(rule.Id, rule.Name, rule.Description, rule.DocumentType,
            rule.MinAmount, rule.MaxAmount, rule.IsActive,
            rule.Steps.OrderBy(s => s.StepOrder).Select(s =>
                new ApprovalStepResponse(s.StepOrder, s.ApproverUserId, s.ApproverUser?.FullName ?? "", s.IsRequired)).ToList(),
            rule.ProjectId);

    /// <summary>map คำขอพร้อมตรวจว่าผู้อนุมัติของขั้นที่ยังไม่ได้ทำยังเป็นสมาชิกบริษัทไหม (ID#11) — query เดียวต่อชุด</summary>
    private async Task<List<ApprovalRequestResponse>> MapWithMembershipAsync(Guid companyId, IReadOnlyCollection<ApprovalRequest> requests)
    {
        var active = await Accounting.Helpers.CompanyMembership.ActiveMemberIdsAsync(_db, companyId,
            requests.SelectMany(r => r.Actions).Select(a => a.ApproverUserId));
        return requests.Select(r => MapRequestToResponse(r, active)).ToList();
    }

    private static ApprovalRequestResponse MapRequestToResponse(ApprovalRequest r, IReadOnlySet<Guid> activeMembers)
    {
        var departedPending = r.OverallStatus == ApprovalStatus.Pending
            ? r.Actions.Where(a => a.Status == ApprovalStatus.Pending && a.StepOrder >= r.CurrentStep
                && !activeMembers.Contains(a.ApproverUserId)).ToList()
            : new List<ApprovalAction>();
        var departedIds = departedPending.Select(a => a.Id).ToHashSet();
        return new(r.Id, r.EntityType, r.EntityId, r.RequestedByUser?.FullName ?? "",
            r.RequestedAt, r.OverallStatus, r.CurrentStep,
            r.Actions.OrderBy(a => a.StepOrder).Select(a =>
                new ApprovalActionResponse(a.StepOrder, a.ApproverUser?.FullName ?? "",
                    a.Status, a.ActionAt, a.Comments, ApproverDeparted: departedIds.Contains(a.Id))).ToList(),
            StaleApproverNotice: Accounting.Helpers.ApprovalApproverCheck.StalledNotice(
                departedPending.Select(a => (a.StepOrder, a.ApproverUser?.FullName)).ToList()));
    }

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
