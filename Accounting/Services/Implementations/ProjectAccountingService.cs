using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Project;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ProjectAccountingService : IProjectAccountingService
{
    private readonly AccountingDbContext _db;
    private readonly IWebhookService? _webhooks;
    private readonly ILogger<ProjectAccountingService>? _logger;

    public ProjectAccountingService(AccountingDbContext db,
        IWebhookService? webhooks = null,
        ILogger<ProjectAccountingService>? logger = null)
    {
        _db = db;
        _webhooks = webhooks;
        _logger = logger;
    }

    /// <summary>Fire an outbound webhook. Wrapped in try/catch so a
    /// slow / failed delivery NEVER blocks the parent API call from
    /// returning to the user. WebhookService internally enqueues the
    /// HTTP calls + handles retry-with-backoff; failures appear in the
    /// admin webhook-deliveries dashboard.</summary>
    private async Task FireWebhookAsync(Guid companyId, string eventType, object payload)
    {
        if (_webhooks == null) return;
        try { await _webhooks.TriggerAsync(companyId, eventType, payload); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Webhook {Event} fire-and-forget failed", eventType); }
    }

    public async Task<ProjectResponse> CreateAsync(Guid companyId, CreateProjectRequest request)
    {
        var existing = await _db.Projects.AnyAsync(p => p.CompanyId == companyId && p.Code == request.Code);
        if (existing)
            throw new InvalidOperationException($"รหัสโครงการ {request.Code} ซ้ำ");

        // Idempotency on partner-system identity: if the same
        // (externalSystem, externalId) already exists, return that
        // project instead of failing — partner retries on flaky
        // network should be safe.
        if (!string.IsNullOrEmpty(request.ExternalSystem) && !string.IsNullOrEmpty(request.ExternalId))
        {
            var existingByExt = await _db.Projects.FirstOrDefaultAsync(p =>
                p.CompanyId == companyId && !p.IsDeleted
                && p.ExternalSystem == request.ExternalSystem
                && p.ExternalId == request.ExternalId);
            if (existingByExt != null) return MapToResponse(existingByExt);
        }

        string? customerName = null;
        if (request.ContactId.HasValue)
        {
            var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == request.ContactId.Value && c.CompanyId == companyId);
            customerName = contact?.Name;
        }

        var project = new Project
        {
            CompanyId = companyId,
            Code = request.Code,
            Name = request.Name,
            NameEn = request.NameEn,
            Description = request.Description,
            ContactId = request.ContactId,
            CustomerName = customerName,
            ProjectManagerName = request.ProjectManagerName,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            BudgetAmount = request.BudgetAmount,
            ContractAmount = request.ContractAmount,
            BillingMethod = request.BillingMethod,
            RevenueRecognitionMethod = request.RevenueRecognitionMethod,
            DimensionId = request.DimensionId,
            Status = "Active",
            ExternalId = request.ExternalId,
            ExternalSystem = request.ExternalSystem,
            ExternalUrl = request.ExternalUrl,
            LastSyncedAt = (request.ExternalSystem != null) ? DateTime.UtcNow : null,
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        var resp = MapToResponse(project);
        await FireWebhookAsync(companyId, "project.created", resp);
        return resp;
    }

    public async Task<ProjectResponse> GetByIdAsync(Guid companyId, Guid projectId)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบโครงการ");
        return MapToResponse(project);
    }

    public async Task<PagedResponse<ProjectResponse>> GetAllAsync(Guid companyId, string? status, PagedRequest request)
    {
        var query = _db.Projects.Where(p => p.CompanyId == companyId);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(p => p.Status == status);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(p => p.Code.Contains(request.Search) || p.Name.Contains(request.Search));

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);

        return new PagedResponse<ProjectResponse>(
            items.Select(MapToResponse).ToList(),
            totalCount,
            request.Page,
            request.PageSize,
            totalPages);
    }

    public async Task<ProjectResponse> UpdateAsync(Guid companyId, Guid projectId, UpdateProjectRequest request)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบโครงการ");

        var prevStatus = project.Status;
        if (request.Name != null) project.Name = request.Name;
        if (request.Description != null) project.Description = request.Description;
        if (request.StartDate.HasValue && request.StartDate.Value != DateTime.MinValue)
            project.StartDate = request.StartDate.Value;
        // MinValue = ล้าง ("ไม่กำหนดวันสิ้นสุด") — เดิมล้างไม่ได้เลย
        if (request.EndDate.HasValue)
            project.EndDate = request.EndDate.Value == DateTime.MinValue ? null : request.EndDate.Value;
        if (request.BudgetAmount.HasValue) project.BudgetAmount = request.BudgetAmount.Value;
        if (request.ContractAmount.HasValue) project.ContractAmount = request.ContractAmount.Value;
        if (request.CompletionPercent.HasValue) project.CompletionPercent = request.CompletionPercent.Value;
        if (request.Status != null) project.Status = request.Status;
        if (request.ExternalUrl != null) project.ExternalUrl = request.ExternalUrl;

        await _db.SaveChangesAsync();
        var resp = MapToResponse(project);
        await FireWebhookAsync(companyId, "project.updated", resp);
        if (request.Status != null && prevStatus != request.Status)
            await FireWebhookAsync(companyId, "project.status_changed",
                new { project = resp, from = prevStatus, to = request.Status });
        return resp;
    }

    public async Task<ProjectResponse> CompleteAsync(Guid companyId, Guid projectId)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบโครงการ");

        var prevStatus = project.Status;
        project.Status = "Completed";
        project.CompletionPercent = 100;
        project.ActualEndDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        var resp = MapToResponse(project);
        await FireWebhookAsync(companyId, "project.status_changed",
            new { project = resp, from = prevStatus, to = "Completed" });
        return resp;
    }

    public async Task<ProjectResponse> ChangeStatusAsync(Guid companyId, Guid projectId,
        string status, string? reason)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบโครงการ");
        var prevStatusForStatusChange = project.Status;

        // Closing transitions (Completed | Cancelled) stamp ActualEndDate
        // so reporting can age-filter retired projects.
        project.Status = status;
        if (status == "Completed")
        {
            project.CompletionPercent = 100;
            project.ActualEndDate ??= DateTime.UtcNow;
        }
        else if (status == "Cancelled")
        {
            project.ActualEndDate ??= DateTime.UtcNow;
        }
        else if (status == "Active" || status == "OnHold")
        {
            // Re-opening a previously-closed project clears the actual
            // end date so the timeline reflects current state.
            if (status == "Active" && project.ActualEndDate.HasValue)
                project.ActualEndDate = null;
        }
        if (!string.IsNullOrWhiteSpace(reason))
            project.Description = (project.Description ?? "") + $"\n[{DateTime.UtcNow:yyyy-MM-dd}] Status → {status}: {reason}";
        await _db.SaveChangesAsync();
        var resp = MapToResponse(project);
        if (prevStatusForStatusChange != status)
            await FireWebhookAsync(companyId, "project.status_changed",
                new { project = resp, from = prevStatusForStatusChange, to = status, reason });
        return resp;
    }

    public async Task<ProjectResponse> AttachExternalAsync(Guid companyId, Guid projectId,
        string externalSystem, string externalId, string? externalUrl, DateTime? lastSyncedAt)
    {
        if (string.IsNullOrWhiteSpace(externalSystem) || string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("ExternalSystem + ExternalId required.");
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบโครงการ");

        // Guard against accidentally over-writing another project's
        // existing external link: if (system,id) already points to a
        // DIFFERENT project, reject — partner must explicitly detach.
        var clash = await _db.Projects.AnyAsync(p =>
            p.CompanyId == companyId && !p.IsDeleted
            && p.Id != projectId
            && p.ExternalSystem == externalSystem
            && p.ExternalId == externalId);
        if (clash) throw new InvalidOperationException(
            $"(System={externalSystem}, Id={externalId}) ถูกใช้กับโปรเจคอื่นแล้ว — detach ก่อน");

        project.ExternalSystem = externalSystem;
        project.ExternalId = externalId;
        project.ExternalUrl = externalUrl;
        project.LastSyncedAt = lastSyncedAt ?? DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return MapToResponse(project);
    }

    public async Task<ProjectResponse?> GetByExternalAsync(Guid companyId,
        string externalSystem, string externalId)
    {
        var project = await _db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && !p.IsDeleted
                && p.ExternalSystem == externalSystem
                && p.ExternalId == externalId);
        return project == null ? null : MapToResponse(project);
    }

    public async Task DeleteAsync(Guid companyId, Guid projectId)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบโครงการ");

        // Block delete if project is referenced by posted journals or has cost entries
        var hasJournalRef = await _db.JournalEntries.AnyAsync(j => j.CompanyId == companyId && j.ProjectId == projectId)
            || await _db.JournalEntryLines.AnyAsync(l => l.ProjectId == projectId);
        var hasCosts = await _db.ProjectCostEntries.AnyAsync(c => c.ProjectId == projectId);

        if (hasJournalRef || hasCosts)
        {
            // Soft-delete to preserve history
            project.IsDeleted = true;
            project.Status = "Cancelled";
        }
        else
        {
            // No references → safe hard delete (still soft-delete for audit)
            project.IsDeleted = true;
        }

        await _db.SaveChangesAsync();
        await FireWebhookAsync(companyId, "project.deleted", new
        {
            id = project.Id, code = project.Code, name = project.Name,
            externalId = project.ExternalId, externalSystem = project.ExternalSystem,
        });
    }

    public async Task<List<ProjectResponse>> GetActiveListAsync(Guid companyId)
    {
        var list = await _db.Projects
            .Where(p => p.CompanyId == companyId && p.Status == "Active")
            .OrderBy(p => p.Code)
            .ToListAsync();
        return list.Select(MapToResponse).ToList();
    }

    // ===== Tasks =====

    public async Task<ProjectTaskResponse> CreateTaskAsync(Guid companyId, Guid projectId, CreateProjectTaskRequest request)
    {
        var project = await _db.Projects
            .AnyAsync(p => p.Id == projectId && p.CompanyId == companyId);
        if (!project)
            throw new KeyNotFoundException("ไม่พบโครงการ");

        var maxSort = await _db.ProjectTasks
            .Where(t => t.ProjectId == projectId)
            .MaxAsync(t => (int?)t.SortOrder) ?? 0;

        var task = new ProjectTask
        {
            CompanyId = companyId,
            ProjectId = projectId,
            Name = request.Name,
            Description = request.Description,
            ParentTaskId = request.ParentTaskId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            EstimatedHours = request.EstimatedHours,
            EstimatedCost = request.EstimatedCost,
            AssignedTo = request.AssignedTo,
            SortOrder = maxSort + 1,
            Status = "Open"
        };

        _db.ProjectTasks.Add(task);
        await _db.SaveChangesAsync();
        return MapTaskToResponse(task);
    }

    public async Task<List<ProjectTaskResponse>> GetTasksAsync(Guid companyId, Guid projectId)
    {
        var tasks = await _db.ProjectTasks
            .Where(t => t.ProjectId == projectId && t.CompanyId == companyId)
            .OrderBy(t => t.SortOrder)
            .ToListAsync();

        return tasks.Select(MapTaskToResponse).ToList();
    }

    public async Task<ProjectTaskResponse> UpdateTaskAsync(Guid companyId, Guid taskId, UpdateProjectTaskRequest request)
    {
        var task = await _db.ProjectTasks
            .FirstOrDefaultAsync(t => t.Id == taskId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงาน");

        if (request.Name != null) task.Name = request.Name;
        if (request.ActualHours.HasValue) task.ActualHours = request.ActualHours.Value;
        if (request.ActualCost.HasValue) task.ActualCost = request.ActualCost.Value;
        if (request.CompletionPercent.HasValue) task.CompletionPercent = request.CompletionPercent.Value;
        if (request.Status != null) task.Status = request.Status;

        await _db.SaveChangesAsync();
        return MapTaskToResponse(task);
    }

    public async Task DeleteTaskAsync(Guid companyId, Guid taskId)
    {
        var task = await _db.ProjectTasks
            .FirstOrDefaultAsync(t => t.Id == taskId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงาน");
        task.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteCostEntryAsync(Guid companyId, Guid costEntryId)
    {
        var entry = await _db.ProjectCostEntries
            .FirstOrDefaultAsync(c => c.Id == costEntryId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการต้นทุน");

        if (entry.IsBilled)
            throw new InvalidOperationException("ไม่สามารถลบรายการต้นทุนที่ออกบิลแล้ว");
        // Same immutability rule as Update: entries auto-posted from a
        // JE or sourced from a Document cannot be deleted via the
        // partner API — they'd orphan the source. Reverse the source
        // first.
        if (entry.JournalEntryId.HasValue)
            throw new InvalidOperationException(
                "รายการนี้ผูกกับ Journal Entry แล้ว — ลบที่ JE ต้นทาง หรือ reverse JE ก่อน");
        if (entry.DocumentId.HasValue)
            throw new InvalidOperationException(
                "รายการนี้ผูกกับเอกสาร — ยกเลิกเอกสารต้นทางก่อน");

        // Roll back actual cost
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == entry.ProjectId && p.CompanyId == companyId);
        if (project != null)
            project.ActualCost = Math.Max(0, project.ActualCost - entry.Amount);

        entry.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    public async Task<ProjectGlSummaryResponse> GetGlSummaryAsync(Guid companyId, Guid projectId, DateTime? fromDate, DateTime? toDate)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบโครงการ");

        var from = fromDate?.Date ?? DateTime.UtcNow.AddYears(-1).Date;
        var to = (toDate?.Date ?? DateTime.UtcNow.Date).AddDays(1);

        var lines = await _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && (l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed)
                && l.JournalEntry.EntryDate >= from
                && l.JournalEntry.EntryDate < to
                && (l.ProjectId == projectId || l.JournalEntry.ProjectId == projectId))
            .ToListAsync();

        var byAccount = lines
            .Where(l => l.Account != null)
            .GroupBy(l => new { l.Account.AccountCode, l.Account.AccountName, l.Account.AccountType })
            .Select(g => new ProjectGlAccountSummary(
                g.Key.AccountCode, g.Key.AccountName, g.Key.AccountType.ToString(),
                g.Sum(l => l.DebitAmount), g.Sum(l => l.CreditAmount),
                g.Key.AccountType == AccountType.Asset || g.Key.AccountType == AccountType.Expense
                    ? g.Sum(l => l.DebitAmount - l.CreditAmount)
                    : g.Sum(l => l.CreditAmount - l.DebitAmount)))
            .OrderBy(a => a.AccountCode)
            .ToList();

        var revenue = lines.Where(l => l.Account != null && l.Account.AccountType == AccountType.Revenue)
            .Sum(l => l.CreditAmount - l.DebitAmount);
        var expense = lines.Where(l => l.Account != null && l.Account.AccountType == AccountType.Expense)
            .Sum(l => l.DebitAmount - l.CreditAmount);

        var entryCount = lines.Select(l => l.JournalEntryId).Distinct().Count();

        return new ProjectGlSummaryResponse(
            project.Id, project.Name, from, to.AddDays(-1),
            revenue, expense, revenue - expense,
            entryCount, lines.Count, byAccount);
    }

    // ===== Cost Entries =====

    public async Task<ProjectCostEntryResponse> AddCostEntryAsync(Guid companyId, Guid projectId, CreateProjectCostEntryRequest request)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบโครงการ");

        var entry = new ProjectCostEntry
        {
            CompanyId = companyId,
            ProjectId = projectId,
            ProjectTaskId = request.ProjectTaskId,
            EntryDate = request.EntryDate,
            CostType = request.CostType,
            Description = request.Description,
            Quantity = request.Quantity,
            UnitCost = request.UnitCost,
            Amount = request.Quantity * request.UnitCost,
            EmployeeId = request.EmployeeId,
            IsBillable = request.IsBillable,
            CostBehavior = request.CostBehavior
        };

        _db.ProjectCostEntries.Add(entry);

        // Update project actual cost
        project.ActualCost += entry.Amount;

        await _db.SaveChangesAsync();

        return MapCost(entry);
    }

    public async Task<ProjectCostEntryResponse> UpdateCostEntryAsync(Guid companyId, Guid costEntryId, UpdateProjectCostEntryRequest request)
    {
        var entry = await _db.ProjectCostEntries
            .FirstOrDefaultAsync(e => e.Id == costEntryId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการ cost");
        if (entry.IsBilled)
            throw new InvalidOperationException("รายการที่ออกบิลแล้วแก้ไขไม่ได้ — ต้องยกเลิกบิลก่อน");
        // Entries auto-posted from a payroll allocation / document / JE
        // are immutable from the partner API — editing them would let
        // ProjectCostEntry drift away from the GL. Partners should adjust
        // the underlying JE / payroll instead.
        if (entry.JournalEntryId.HasValue)
            throw new InvalidOperationException(
                "รายการนี้ผูกกับรายการบัญชี (Journal Entry) แล้ว — แก้ไขที่ JE ต้นทางแทน");
        if (entry.DocumentId.HasValue)
            throw new InvalidOperationException(
                "รายการนี้ผูกกับเอกสาร (Invoice/Bill) แล้ว — แก้ไขที่เอกสารต้นทางแทน");

        var oldAmount = entry.Amount;

        if (request.EntryDate.HasValue) entry.EntryDate = request.EntryDate.Value;
        if (request.CostType != null) entry.CostType = request.CostType;
        if (request.Description != null) entry.Description = request.Description;
        if (request.Quantity.HasValue) entry.Quantity = request.Quantity.Value;
        if (request.UnitCost.HasValue) entry.UnitCost = request.UnitCost.Value;
        entry.Amount = entry.Quantity * entry.UnitCost;
        if (request.IsBillable.HasValue) entry.IsBillable = request.IsBillable.Value;
        if (request.CostBehavior != null) entry.CostBehavior = request.CostBehavior;

        // Roll the delta onto the project's actual cost so the dashboard
        // stays in sync without a recompute.
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == entry.ProjectId && p.CompanyId == companyId);
        if (project != null) project.ActualCost += (entry.Amount - oldAmount);

        entry.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return MapCost(entry);
    }

    public async Task<PagedResponse<ProjectCostEntryResponse>> GetCostEntriesAsync(Guid companyId, Guid projectId, PagedRequest request,
        string? costType = null, string? costBehavior = null, DateTime? from = null, DateTime? to = null)
    {
        var query = _db.ProjectCostEntries
            .Where(e => e.ProjectId == projectId && e.CompanyId == companyId && !e.IsDeleted);

        if (!string.IsNullOrEmpty(costType)) query = query.Where(e => e.CostType == costType);
        if (!string.IsNullOrEmpty(costBehavior)) query = query.Where(e => e.CostBehavior == costBehavior);
        if (from.HasValue) query = query.Where(e => e.EntryDate >= from.Value);
        if (to.HasValue) query = query.Where(e => e.EntryDate <= to.Value);

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(e => e.EntryDate)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);

        return new PagedResponse<ProjectCostEntryResponse>(
            items.Select(MapCost).ToList(),
            totalCount,
            request.Page,
            request.PageSize,
            totalPages);
    }

    private static ProjectCostEntryResponse MapCost(ProjectCostEntry e) =>
        new(e.Id, e.ProjectId, e.EntryDate, e.CostType,
            e.Description, e.Quantity, e.UnitCost, e.Amount,
            e.IsBillable, e.IsBilled, e.CostBehavior);

    // ===== Reports =====

    /// <summary>Walk every labour ProjectCostEntry tagged to this project
    /// in the window and aggregate by employee. Hours come from the
    /// EmployeeProjectTime rows that spawned each entry (linked via the
    /// payroll-run allocation); when missing (manually-created Labour
    /// entries) the entry's Quantity field is used as the hour count
    /// fallback. Billable/non-billable split is read off
    /// EmployeeProjectTime.Category.</summary>
    public async Task<ProjectLabourBreakdown> GetLabourBreakdownAsync(
        Guid companyId, Guid projectId, DateTime? from, DateTime? to)
    {
        var project = await _db.Projects
            .Where(p => p.Id == projectId && p.CompanyId == companyId)
            .Select(p => new { p.Id, p.Code, p.Name })
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("ไม่พบโครงการ");

        // Labour entries scoped to this project + window.
        var pceQuery = _db.ProjectCostEntries
            .Where(c => c.CompanyId == companyId
                && c.ProjectId == projectId
                && !c.IsDeleted
                && c.CostType == "Labor"
                && c.EmployeeId.HasValue);
        if (from.HasValue) pceQuery = pceQuery.Where(c => c.EntryDate >= from.Value);
        if (to.HasValue) pceQuery = pceQuery.Where(c => c.EntryDate < to.Value.Date.AddDays(1));

        var pces = await pceQuery
            .Select(c => new { c.Id, c.EmployeeId, c.Quantity, c.Amount, c.JournalEntryId })
            .ToListAsync();
        if (pces.Count == 0)
        {
            return new ProjectLabourBreakdown(project.Id, project.Code, project.Name,
                from, to, 0, 0, 0, new List<ProjectLabourByEmployee>());
        }

        // Map ProjectCostEntryId → list of EmployeeProjectTime rows so we
        // can read true hours + billable category instead of relying on
        // the rounded Quantity field. EmployeeProjectTime carries the
        // billable / non-billable split per row.
        var pceIds = pces.Select(p => p.Id).ToList();
        var timeRows = await _db.EmployeeProjectTimes
            .Where(t => t.CompanyId == companyId
                && t.ProjectCostEntryId.HasValue
                && pceIds.Contains(t.ProjectCostEntryId!.Value)
                && !t.IsDeleted)
            .Select(t => new { t.ProjectCostEntryId, t.EmployeeId, t.Hours, t.Category })
            .ToListAsync();

        var hoursByPce = timeRows
            .GroupBy(t => t.ProjectCostEntryId!.Value)
            .ToDictionary(g => g.Key, g => new {
                Hours = g.Sum(t => t.Hours),
                Billable = g.Where(t => t.Category == "Billable").Sum(t => t.Hours),
                NonBillable = g.Where(t => t.Category != "Billable").Sum(t => t.Hours),
            });

        // Per-employee aggregation.
        var employeeIds = pces.Where(p => p.EmployeeId.HasValue)
            .Select(p => p.EmployeeId!.Value).Distinct().ToList();
        var employees = await _db.Employees
            .Where(e => e.CompanyId == companyId && employeeIds.Contains(e.Id))
            .Select(e => new {
                e.Id, e.EmployeeCode, e.FirstNameTh, e.LastNameTh,
                e.Department, e.Position, e.CostBehavior,
            })
            .ToDictionaryAsync(e => e.Id);

        var byEmployee = pces
            .Where(p => p.EmployeeId.HasValue)
            .GroupBy(p => p.EmployeeId!.Value)
            .Select(g =>
            {
                var emp = employees.GetValueOrDefault(g.Key);
                var pceHours = g.Sum(p => hoursByPce.TryGetValue(p.Id, out var h) ? h.Hours : p.Quantity);
                var billable = (int)g.Sum(p => hoursByPce.TryGetValue(p.Id, out var h) ? h.Billable : 0);
                var nonBillable = (int)g.Sum(p => hoursByPce.TryGetValue(p.Id, out var h) ? h.NonBillable : 0);
                var amount = g.Sum(p => p.Amount);
                var runCount = g.Where(p => p.JournalEntryId.HasValue)
                    .Select(p => p.JournalEntryId!.Value).Distinct().Count();
                return new ProjectLabourByEmployee(
                    g.Key,
                    emp?.EmployeeCode ?? "(unknown)",
                    emp != null ? $"{emp.FirstNameTh} {emp.LastNameTh}".Trim() : "(unknown)",
                    emp?.Department, emp?.Position,
                    pceHours, amount,
                    pceHours > 0 ? Math.Round(amount / pceHours, 2, MidpointRounding.AwayFromZero) : 0,
                    emp?.CostBehavior ?? "Fixed",
                    runCount, billable, nonBillable);
            })
            .OrderByDescending(e => e.Amount)
            .ToList();

        return new ProjectLabourBreakdown(
            project.Id, project.Code, project.Name, from, to,
            byEmployee.Sum(e => e.Hours), byEmployee.Sum(e => e.Amount),
            byEmployee.Count, byEmployee);
    }

    public async Task<ProjectProfitabilityResponse> GetProfitabilityAsync(Guid companyId, Guid projectId)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบโครงการ");

        var costEntries = await _db.ProjectCostEntries
            .Where(e => e.ProjectId == projectId && e.CompanyId == companyId)
            .ToListAsync();

        var totalCost = costEntries.Sum(e => e.Amount);
        var totalRevenue = project.ActualRevenue;
        var grossProfit = totalRevenue - totalCost;
        var grossProfitPercent = totalRevenue != 0 ? (grossProfit / totalRevenue) * 100 : 0;
        var budgetVariance = project.BudgetAmount - totalCost;

        // เลขเอกสารต้นทางสำหรับ drill-down (รายการ auto จากใบซื้อ/ใบสำคัญจ่าย)
        var docIds = costEntries.Where(e => e.DocumentId.HasValue)
            .Select(e => e.DocumentId!.Value).Distinct().ToList();
        var docNumbers = docIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && docIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.DocumentNumber);

        static string LabelOf(string t) => t switch
        {
            "Labor" => "ค่าแรง",
            "Material" => "ค่าวัสดุ/อุปกรณ์",
            "Subcontract" => "ค่าจ้างเหมา",
            "Overhead" => "โสหุ้ย/ค่าใช้จ่ายทั่วไป",
            "Travel" => "ค่าเดินทาง/ที่พัก",
            _ => string.IsNullOrWhiteSpace(t) ? "อื่น ๆ" : t,
        };
        // สัดส่วนต่อหมวด (เรียงมาก→น้อย) + รายละเอียดรายรายการต่อหมวด
        // (ล่าสุดก่อน, cap 200/หมวด กัน payload บวมในโครงการใหญ่)
        var costBreakdown = costEntries
            .GroupBy(e => string.IsNullOrWhiteSpace(e.CostType) ? "Other" : e.CostType)
            .Select(g => new ProjectCostBreakdownItem(
                g.Key, LabelOf(g.Key),
                g.Sum(e => e.Amount),
                totalCost > 0 ? Math.Round(g.Sum(e => e.Amount) / totalCost * 100, 1) : 0,
                g.Count(),
                g.OrderByDescending(e => e.EntryDate)
                    .Take(200)
                    .Select(e => new ProjectCostEntryDetail(
                        e.EntryDate, e.Description, e.Quantity, e.UnitCost, e.Amount,
                        e.DocumentId,
                        e.DocumentId.HasValue ? docNumbers.GetValueOrDefault(e.DocumentId.Value) : null))
                    .ToList()))
            .OrderByDescending(b => b.Amount)
            .ToList();

        return new ProjectProfitabilityResponse(
            project.Id, project.Name, project.ContractAmount,
            totalCost, totalRevenue, grossProfit, grossProfitPercent,
            budgetVariance, project.CompletionPercent, costBreakdown);
    }

    public async Task<List<ProjectSummaryResponse>> GetProjectSummaryAsync(Guid companyId)
    {
        var projects = await _db.Projects
            .Where(p => p.CompanyId == companyId)
            .ToListAsync();

        var result = new List<ProjectSummaryResponse>();
        foreach (var p in projects)
        {
            var profitPercent = p.ContractAmount != 0
                ? ((p.ActualRevenue - p.ActualCost) / p.ContractAmount) * 100
                : 0;

            result.Add(new ProjectSummaryResponse(
                p.Id, p.Code, p.Name, p.Status,
                p.BudgetAmount, p.ActualCost,
                p.CompletionPercent, profitPercent));
        }
        return result;
    }

    // ===== Mappers =====

    private static ProjectResponse MapToResponse(Project p) => new(
        p.Id, p.Code, p.Name, p.Description, p.CustomerName,
        p.StartDate, p.EndDate, p.Status,
        p.BudgetAmount, p.ContractAmount, p.ActualCost, p.ActualRevenue,
        p.CompletionPercent, p.BillingMethod, p.CreatedAt,
        p.ExternalId, p.ExternalSystem, p.ExternalUrl, p.LastSyncedAt);

    private static ProjectTaskResponse MapTaskToResponse(ProjectTask t) => new(
        t.Id, t.ProjectId, t.Name, t.Description,
        t.StartDate, t.EndDate, t.EstimatedHours, t.ActualHours,
        t.EstimatedCost, t.ActualCost, t.CompletionPercent, t.Status, t.AssignedTo);
}
