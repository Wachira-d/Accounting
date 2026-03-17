using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Freelance;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class FreelanceService : IFreelanceService
{
    private readonly AccountingDbContext _db;
    private readonly INotificationService _notificationService;

    public FreelanceService(AccountingDbContext db, INotificationService notificationService)
    {
        _db = db;
        _notificationService = notificationService;
    }

    // ==================== Invitations ====================

    public async Task<InvitationResponse> InviteFreelanceAsync(Guid companyId, InviteFreelanceRequest request, Guid invitedByUserId)
    {
        // Check company settings allow freelance
        var settings = await _db.Set<CompanySettings>().FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (settings != null && !settings.AllowFreelanceAccess)
            throw new InvalidOperationException("บริษัทนี้ยังไม่เปิดใช้การเข้าถึงจาก Freelance กรุณาเปิดในการตั้งค่า");

        // Check max freelance limit
        var currentFreelanceCount = await _db.Set<FreelanceAccess>()
            .CountAsync(fa => fa.CompanyId == companyId && fa.IsActive);
        var maxFreelance = settings?.MaxFreelanceUsers ?? 3;
        if (currentFreelanceCount >= maxFreelance)
            throw new InvalidOperationException($"จำนวน Freelance สูงสุด ({maxFreelance}) เต็มแล้ว");

        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            + Convert.ToBase64String(Guid.NewGuid().ToByteArray());

        var invitation = new FreelanceInvitation
        {
            CompanyId = companyId,
            InvitedByUserId = invitedByUserId,
            InviteeEmail = request.InviteeEmail,
            InviteeName = request.InviteeName,
            InviteePhone = request.InviteePhone,
            InvitationToken = token,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedBy = invitedByUserId.ToString()
        };

        _db.Set<FreelanceInvitation>().Add(invitation);
        await _db.SaveChangesAsync();

        return MapInvitationToResponse(invitation);
    }

    public async Task<InvitationResponse> AcceptInvitationAsync(AcceptInvitationRequest request)
    {
        var invitation = await _db.Set<FreelanceInvitation>()
            .Include(i => i.Company)
            .FirstOrDefaultAsync(i => i.InvitationToken == request.Token && i.Status == FreelanceInvitationStatus.Pending)
            ?? throw new KeyNotFoundException("ไม่พบคำเชิญ หรือคำเชิญหมดอายุ");

        if (invitation.ExpiresAt < DateTime.UtcNow)
        {
            invitation.Status = FreelanceInvitationStatus.Expired;
            await _db.SaveChangesAsync();
            throw new InvalidOperationException("คำเชิญหมดอายุแล้ว");
        }

        // Find or create user
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == invitation.InviteeEmail);
        if (user == null)
        {
            if (string.IsNullOrEmpty(request.Password))
                throw new InvalidOperationException("ต้องระบุรหัสผ่านเพื่อสร้างบัญชีใหม่");

            user = new User
            {
                Email = invitation.InviteeEmail,
                FullName = invitation.InviteeName,
                Phone = invitation.InviteePhone,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password)
            };
            _db.Users.Add(user);
        }

        // Create FreelanceAccess
        var access = new FreelanceAccess
        {
            CompanyId = invitation.CompanyId,
            UserId = user.Id,
            AccessStartDate = DateTime.UtcNow,
            AccessEndDate = DateTime.UtcNow.AddMonths(3), // default 3 months
            IsActive = true,
            CreatedBy = invitation.InvitedByUserId.ToString()
        };

        _db.Set<FreelanceAccess>().Add(access);

        invitation.Status = FreelanceInvitationStatus.Accepted;
        invitation.AcceptedAt = DateTime.UtcNow;
        invitation.FreelanceAccessId = access.Id;

        // Add as ExternalAccountant role
        if (!await _db.CompanyUsers.AnyAsync(cu => cu.CompanyId == invitation.CompanyId && cu.UserId == user.Id))
        {
            _db.CompanyUsers.Add(new CompanyUser
            {
                UserId = user.Id,
                CompanyId = invitation.CompanyId,
                Role = UserRole.ExternalAccountant
            });
        }

        await _db.SaveChangesAsync();

        // Notify company owner
        var owner = await _db.CompanyUsers
            .Where(cu => cu.CompanyId == invitation.CompanyId && cu.Role == UserRole.Owner)
            .Select(cu => cu.UserId)
            .FirstOrDefaultAsync();
        if (owner != default)
        {
            await _notificationService.SendAsync(owner, invitation.CompanyId,
                NotificationType.FreelanceInvite,
                "Freelance ตอบรับคำเชิญแล้ว",
                $"{invitation.InviteeName} ({invitation.InviteeEmail}) ตอบรับคำเชิญเข้าทำงานแล้ว");
        }

        return MapInvitationToResponse(invitation);
    }

    public async Task RevokeInvitationAsync(Guid companyId, Guid invitationId)
    {
        var invitation = await _db.Set<FreelanceInvitation>()
            .FirstOrDefaultAsync(i => i.Id == invitationId && i.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบคำเชิญ");

        invitation.Status = FreelanceInvitationStatus.Revoked;
        await _db.SaveChangesAsync();
    }

    public async Task<List<InvitationResponse>> GetInvitationsAsync(Guid companyId)
    {
        var invitations = await _db.Set<FreelanceInvitation>()
            .Where(i => i.CompanyId == companyId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();

        return invitations.Select(MapInvitationToResponse).ToList();
    }

    // ==================== Access Management ====================

    public async Task<FreelanceAccessResponse> GetFreelanceAccessAsync(Guid companyId, Guid accessId)
    {
        var access = await _db.Set<FreelanceAccess>()
            .Include(fa => fa.User)
            .Include(fa => fa.Company)
            .Include(fa => fa.Tasks)
            .FirstOrDefaultAsync(fa => fa.Id == accessId && fa.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลการเข้าถึง");

        return MapAccessToResponse(access);
    }

    public async Task<List<FreelanceAccessResponse>> GetCompanyFreelancersAsync(Guid companyId)
    {
        var accesses = await _db.Set<FreelanceAccess>()
            .Include(fa => fa.User)
            .Include(fa => fa.Company)
            .Include(fa => fa.Tasks)
            .Where(fa => fa.CompanyId == companyId)
            .OrderByDescending(fa => fa.CreatedAt)
            .ToListAsync();

        return accesses.Select(MapAccessToResponse).ToList();
    }

    public async Task<FreelanceAccessResponse> UpdateFreelanceAccessAsync(Guid companyId, Guid accessId, UpdateFreelanceAccessRequest request)
    {
        var access = await _db.Set<FreelanceAccess>()
            .Include(fa => fa.User)
            .Include(fa => fa.Company)
            .Include(fa => fa.Tasks)
            .FirstOrDefaultAsync(fa => fa.Id == accessId && fa.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลการเข้าถึง");

        if (request.AccessEndDate.HasValue) access.AccessEndDate = request.AccessEndDate.Value;
        if (request.IsActive.HasValue) access.IsActive = request.IsActive.Value;

        if (request.AccessConfig != null)
        {
            var c = request.AccessConfig;
            access.GeneralScope = c.GeneralScope;
            access.CanViewChartOfAccounts = c.CanViewChartOfAccounts;
            access.CanEditChartOfAccounts = c.CanEditChartOfAccounts;
            access.CanViewJournalEntries = c.CanViewJournalEntries;
            access.CanCreateJournalEntries = c.CanCreateJournalEntries;
            access.CanPostJournalEntries = c.CanPostJournalEntries;
            access.CanViewDocuments = c.CanViewDocuments;
            access.CanCreateDocuments = c.CanCreateDocuments;
            access.CanApproveDocuments = c.CanApproveDocuments;
            access.CanViewTaxReports = c.CanViewTaxReports;
            access.CanCreateTaxReports = c.CanCreateTaxReports;
            access.CanFileTaxReports = c.CanFileTaxReports;
            access.CanViewBankAccounts = c.CanViewBankAccounts;
            access.CanReconcile = c.CanReconcile;
            access.CanViewReports = c.CanViewReports;
            access.CanExportData = c.CanExportData;
            access.CanCloseFiscalPeriod = c.CanCloseFiscalPeriod;
            access.CanManageContacts = c.CanManageContacts;
            access.CanViewPayments = c.CanViewPayments;
            access.CanCreatePayments = c.CanCreatePayments;
            access.CanManageFixedAssets = c.CanManageFixedAssets;
            access.CanViewSensitiveData = c.CanViewSensitiveData;
            access.CanViewCostData = c.CanViewCostData;
            access.AllowedIpAddresses = c.AllowedIpAddresses;
            access.MaxActionsPerDay = c.MaxActionsPerDay;
            access.AllowedFiscalYear = c.AllowedFiscalYear;
            access.AllowedFiscalMonth = c.AllowedFiscalMonth;
            access.AllowedDaysOfWeek = c.AllowedDaysOfWeek;

            if (!string.IsNullOrEmpty(c.AccessStartTime))
                access.AccessStartTime = TimeOnly.Parse(c.AccessStartTime);
            if (!string.IsNullOrEmpty(c.AccessEndTime))
                access.AccessEndTime = TimeOnly.Parse(c.AccessEndTime);
        }

        await _db.SaveChangesAsync();
        return MapAccessToResponse(access);
    }

    public async Task DeactivateFreelanceAccessAsync(Guid companyId, Guid accessId)
    {
        var access = await _db.Set<FreelanceAccess>()
            .FirstOrDefaultAsync(fa => fa.Id == accessId && fa.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลการเข้าถึง");

        access.IsActive = false;

        // Also remove from CompanyUser
        var cu = await _db.CompanyUsers.FirstOrDefaultAsync(
            x => x.CompanyId == companyId && x.UserId == access.UserId && x.Role == UserRole.ExternalAccountant);
        if (cu != null) _db.CompanyUsers.Remove(cu);

        await _db.SaveChangesAsync();
    }

    // ==================== Tasks ====================

    public async Task<FreelanceTaskResponse> CreateTaskAsync(Guid companyId, CreateFreelanceTaskRequest request, Guid createdBy)
    {
        var task = new FreelanceTask
        {
            FreelanceAccessId = request.FreelanceAccessId,
            CompanyId = companyId,
            Title = request.Title,
            Description = request.Description,
            TaskType = request.TaskType,
            DueDate = request.DueDate,
            FiscalYear = request.FiscalYear,
            FiscalMonth = request.FiscalMonth,
            DataFromDate = request.DataFromDate,
            DataToDate = request.DataToDate,
            EstimatedHours = request.EstimatedHours,
            AgreedRate = request.AgreedRate,
            RateType = request.RateType,
            CreatedBy = createdBy.ToString()
        };

        _db.Set<FreelanceTask>().Add(task);
        await _db.SaveChangesAsync();

        // Notify freelance
        var access = await _db.Set<FreelanceAccess>().FindAsync(request.FreelanceAccessId);
        if (access != null)
        {
            await _notificationService.SendAsync(access.UserId, companyId,
                NotificationType.TaskAssigned,
                "มีงานใหม่",
                $"คุณได้รับมอบหมายงาน: {request.Title}");
        }

        return await GetTaskAsync(companyId, task.Id);
    }

    public async Task<FreelanceTaskResponse> GetTaskAsync(Guid companyId, Guid taskId)
    {
        var task = await _db.Set<FreelanceTask>()
            .Include(t => t.Comments).ThenInclude(c => c.User)
            .Include(t => t.TimeLogs)
            .FirstOrDefaultAsync(t => t.Id == taskId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงาน");

        return MapTaskToResponse(task);
    }

    public async Task<List<FreelanceTaskSummary>> GetTasksAsync(Guid companyId, Guid? freelanceAccessId = null)
    {
        var query = _db.Set<FreelanceTask>().Where(t => t.CompanyId == companyId);
        if (freelanceAccessId.HasValue)
            query = query.Where(t => t.FreelanceAccessId == freelanceAccessId.Value);

        var tasks = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();
        return tasks.Select(MapTaskToSummary).ToList();
    }

    public async Task<FreelanceTaskResponse> UpdateTaskAsync(Guid companyId, Guid taskId, UpdateFreelanceTaskRequest request, Guid userId)
    {
        var task = await _db.Set<FreelanceTask>()
            .FirstOrDefaultAsync(t => t.Id == taskId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงาน");

        if (request.Status.HasValue) task.Status = request.Status.Value;
        if (request.DueDate.HasValue) task.DueDate = request.DueDate.Value;
        if (request.ReviewNotes != null) task.ReviewNotes = request.ReviewNotes;
        if (request.ActualHours.HasValue) task.ActualHours = request.ActualHours.Value;

        task.UpdatedBy = userId.ToString();
        await _db.SaveChangesAsync();

        return await GetTaskAsync(companyId, taskId);
    }

    public async Task<FreelanceTaskResponse> SubmitTaskAsync(Guid companyId, Guid taskId, Guid userId)
    {
        var task = await _db.Set<FreelanceTask>()
            .FirstOrDefaultAsync(t => t.Id == taskId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงาน");

        task.Status = FreelanceTaskStatus.Submitted;
        task.SubmittedAt = DateTime.UtcNow;

        // Calculate total cost
        if (task.AgreedRate.HasValue)
        {
            task.TotalCost = task.RateType == "hourly"
                ? task.ActualHours * task.AgreedRate.Value
                : task.AgreedRate.Value;
        }

        await _db.SaveChangesAsync();

        // Notify company owner
        var owner = await _db.CompanyUsers
            .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner)
            .Select(cu => cu.UserId)
            .FirstOrDefaultAsync();
        if (owner != default)
        {
            await _notificationService.SendAsync(owner, companyId,
                NotificationType.ApprovalRequired,
                "Freelance ส่งงานแล้ว",
                $"งาน '{task.Title}' ถูกส่งมาเพื่อตรวจสอบ");
        }

        return await GetTaskAsync(companyId, taskId);
    }

    public async Task<FreelanceTaskResponse> ReviewTaskAsync(Guid companyId, Guid taskId, bool approve, string? notes, Guid reviewerId)
    {
        var task = await _db.Set<FreelanceTask>()
            .Include(t => t.FreelanceAccess)
            .FirstOrDefaultAsync(t => t.Id == taskId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงาน");

        task.Status = approve ? FreelanceTaskStatus.Completed : FreelanceTaskStatus.Rejected;
        task.ReviewedByUserId = reviewerId;
        task.ReviewedAt = DateTime.UtcNow;
        task.ReviewNotes = notes;
        if (approve) task.CompletedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // Notify freelance
        await _notificationService.SendAsync(task.FreelanceAccess.UserId, companyId,
            NotificationType.TaskAssigned,
            approve ? "งานได้รับการอนุมัติ" : "งานถูกปฏิเสธ",
            $"งาน '{task.Title}' {(approve ? "ได้รับการอนุมัติ" : "ถูกปฏิเสธ")}{(notes != null ? $": {notes}" : "")}");

        return await GetTaskAsync(companyId, taskId);
    }

    // ==================== Comments & Time Logs ====================

    public async Task<TaskCommentResponse> AddCommentAsync(Guid taskId, Guid userId, AddTaskCommentRequest request)
    {
        var user = await _db.Users.FindAsync(userId) ?? throw new KeyNotFoundException("ไม่พบผู้ใช้");

        var comment = new FreelanceTaskComment
        {
            FreelanceTaskId = taskId,
            UserId = userId,
            Content = request.Content,
            CreatedBy = userId.ToString()
        };

        _db.Set<FreelanceTaskComment>().Add(comment);
        await _db.SaveChangesAsync();

        return new TaskCommentResponse(comment.Id, userId, user.FullName, comment.Content, comment.CreatedAt);
    }

    public async Task<TimeLogResponse> AddTimeLogAsync(Guid taskId, Guid userId, CreateTimeLogRequest request)
    {
        var log = new FreelanceTimeLog
        {
            FreelanceTaskId = taskId,
            UserId = userId,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            Hours = request.Hours,
            Description = request.Description,
            CreatedBy = userId.ToString()
        };

        _db.Set<FreelanceTimeLog>().Add(log);

        // Update task actual hours
        var task = await _db.Set<FreelanceTask>().FindAsync(taskId);
        if (task != null) task.ActualHours += request.Hours;

        await _db.SaveChangesAsync();

        return new TimeLogResponse(log.Id, log.StartTime, log.EndTime, log.Hours, log.Description);
    }

    // ==================== Activity Log ====================

    public async Task<PagedResponse<FreelanceActivityLogResponse>> GetActivityLogsAsync(
        Guid companyId, Guid? freelanceAccessId, PagedRequest request)
    {
        var query = _db.Set<FreelanceActivityLog>().Where(l => l.CompanyId == companyId);
        if (freelanceAccessId.HasValue)
            query = query.Where(l => l.FreelanceAccessId == freelanceAccessId.Value);

        var total = await query.CountAsync();
        var logs = await query
            .OrderByDescending(l => l.Timestamp)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<FreelanceActivityLogResponse>(
            logs.Select(l => new FreelanceActivityLogResponse(
                l.Id, l.UserId, null, l.Timestamp, l.Action,
                l.EntityType, l.EntityId, l.Details, l.IpAddress)).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    // ==================== Freelance Dashboard ====================

    public async Task<FreelanceDashboardResponse> GetFreelanceDashboardAsync(Guid userId)
    {
        var accesses = await _db.Set<FreelanceAccess>()
            .Include(fa => fa.User)
            .Include(fa => fa.Company)
            .Include(fa => fa.Tasks)
            .Where(fa => fa.UserId == userId && fa.IsActive)
            .ToListAsync();

        var allTasks = accesses.SelectMany(a => a.Tasks).ToList();
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1);

        return new FreelanceDashboardResponse(
            accesses.Select(MapAccessToResponse).ToList(),
            allTasks.Where(t => t.Status < FreelanceTaskStatus.Submitted).Select(MapTaskToSummary).ToList(),
            allTasks.Where(t => t.DueDate.HasValue && t.DueDate < now && t.Status < FreelanceTaskStatus.Submitted).Select(MapTaskToSummary).ToList(),
            allTasks.SelectMany(t => t.TimeLogs ?? new List<FreelanceTimeLog>())
                .Where(tl => tl.StartTime >= monthStart).Sum(tl => tl.Hours),
            allTasks.Where(t => t.CompletedAt >= monthStart && t.TotalCost.HasValue).Sum(t => t.TotalCost!.Value));
    }

    // ==================== Mappers ====================

    private static InvitationResponse MapInvitationToResponse(FreelanceInvitation i) => new(
        i.Id, i.CompanyId, i.InviteeEmail, i.InviteeName,
        i.Status, i.ExpiresAt, i.AcceptedAt, i.CreatedAt);

    private static FreelanceAccessResponse MapAccessToResponse(FreelanceAccess a) => new(
        a.Id, a.CompanyId, a.Company.Name, a.UserId, a.User.FullName, a.User.Email,
        a.AccessStartDate, a.AccessEndDate, a.IsActive, a.GeneralScope,
        new FreelancePermissions(
            a.CanViewChartOfAccounts, a.CanEditChartOfAccounts,
            a.CanViewJournalEntries, a.CanCreateJournalEntries, a.CanPostJournalEntries,
            a.CanViewDocuments, a.CanCreateDocuments, a.CanApproveDocuments,
            a.CanViewTaxReports, a.CanCreateTaxReports, a.CanFileTaxReports,
            a.CanViewBankAccounts, a.CanReconcile,
            a.CanViewReports, a.CanExportData, a.CanCloseFiscalPeriod,
            a.CanManageContacts, a.CanViewPayments, a.CanCreatePayments, a.CanManageFixedAssets),
        new FreelanceRestrictions(
            a.AllowedIpAddresses,
            a.AccessStartTime.HasValue && a.AccessEndTime.HasValue
                ? $"{a.AccessStartTime.Value} - {a.AccessEndTime.Value}" : null,
            a.AllowedDaysOfWeek, a.MaxActionsPerDay, a.TodayActionCount,
            a.AllowedFiscalYear, a.AllowedFiscalMonth),
        a.Tasks.Where(t => t.Status < FreelanceTaskStatus.Completed).Select(MapTaskToSummary).ToList());

    private static FreelanceTaskSummary MapTaskToSummary(FreelanceTask t) => new(
        t.Id, t.Title, t.TaskType, t.Status, t.DueDate, t.EstimatedHours, t.ActualHours);

    private static FreelanceTaskResponse MapTaskToResponse(FreelanceTask t) => new(
        t.Id, t.FreelanceAccessId, t.Title, t.Description, t.TaskType, t.Status,
        t.DueDate, t.StartedAt, t.SubmittedAt, t.CompletedAt,
        t.FiscalYear, t.FiscalMonth, t.EstimatedHours, t.ActualHours,
        t.AgreedRate, t.RateType, t.TotalCost, t.IsPaid, t.ReviewNotes,
        t.Comments.Select(c => new TaskCommentResponse(c.Id, c.UserId, c.User.FullName, c.Content, c.CreatedAt)).ToList(),
        t.TimeLogs.Select(l => new TimeLogResponse(l.Id, l.StartTime, l.EndTime, l.Hours, l.Description)).ToList());
}
