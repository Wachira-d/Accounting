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

    public ProjectAccountingService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<ProjectResponse> CreateAsync(Guid companyId, CreateProjectRequest request)
    {
        var existing = await _db.Projects.AnyAsync(p => p.CompanyId == companyId && p.Code == request.Code);
        if (existing)
            throw new InvalidOperationException($"รหัสโครงการ {request.Code} ซ้ำ");

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
            Status = "Active"
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        return MapToResponse(project);
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

        if (request.Name != null) project.Name = request.Name;
        if (request.Description != null) project.Description = request.Description;
        if (request.EndDate.HasValue) project.EndDate = request.EndDate.Value;
        if (request.BudgetAmount.HasValue) project.BudgetAmount = request.BudgetAmount.Value;
        if (request.ContractAmount.HasValue) project.ContractAmount = request.ContractAmount.Value;
        if (request.CompletionPercent.HasValue) project.CompletionPercent = request.CompletionPercent.Value;
        if (request.Status != null) project.Status = request.Status;

        await _db.SaveChangesAsync();
        return MapToResponse(project);
    }

    public async Task<ProjectResponse> CompleteAsync(Guid companyId, Guid projectId)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบโครงการ");

        project.Status = "Completed";
        project.CompletionPercent = 100;
        project.ActualEndDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return MapToResponse(project);
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
            IsBillable = request.IsBillable
        };

        _db.ProjectCostEntries.Add(entry);

        // Update project actual cost
        project.ActualCost += entry.Amount;

        await _db.SaveChangesAsync();

        return new ProjectCostEntryResponse(
            entry.Id, entry.ProjectId, entry.EntryDate, entry.CostType,
            entry.Description, entry.Quantity, entry.UnitCost, entry.Amount,
            entry.IsBillable, entry.IsBilled);
    }

    public async Task<PagedResponse<ProjectCostEntryResponse>> GetCostEntriesAsync(Guid companyId, Guid projectId, PagedRequest request)
    {
        var query = _db.ProjectCostEntries
            .Where(e => e.ProjectId == projectId && e.CompanyId == companyId);

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(e => e.EntryDate)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);

        return new PagedResponse<ProjectCostEntryResponse>(
            items.Select(e => new ProjectCostEntryResponse(
                e.Id, e.ProjectId, e.EntryDate, e.CostType,
                e.Description, e.Quantity, e.UnitCost, e.Amount,
                e.IsBillable, e.IsBilled)).ToList(),
            totalCount,
            request.Page,
            request.PageSize,
            totalPages);
    }

    // ===== Reports =====

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

        var costBreakdown = costEntries
            .GroupBy(e => e.CostType)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));

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
        p.CompletionPercent, p.BillingMethod, p.CreatedAt);

    private static ProjectTaskResponse MapTaskToResponse(ProjectTask t) => new(
        t.Id, t.ProjectId, t.Name, t.Description,
        t.StartDate, t.EndDate, t.EstimatedHours, t.ActualHours,
        t.EstimatedCost, t.ActualCost, t.CompletionPercent, t.Status, t.AssignedTo);
}
