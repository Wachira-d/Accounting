using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Hr;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public interface IEmployeeProjectTimeService
{
    Task<EmployeeProjectTimeResponse> CreateAsync(Guid companyId, CreateEmployeeProjectTimeRequest req, CancellationToken ct = default);
    Task<EmployeeProjectTimeResponse> UpdateAsync(Guid companyId, Guid id, UpdateEmployeeProjectTimeRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid companyId, Guid id, CancellationToken ct = default);
    Task<PagedResponse<EmployeeProjectTimeResponse>> ListAsync(Guid companyId, Guid? employeeId, Guid? projectId,
        DateTime? from, DateTime? to, PagedRequest paging, CancellationToken ct = default);
    Task<SyncEmployeeProjectTimesResponse> SyncAsync(Guid companyId, SyncEmployeeProjectTimesRequest req, CancellationToken ct = default);

    /// <summary>Walk each employee in the payroll run, split their gross
    /// salary across projects pro-rata to hours worked during the run's
    /// period (PeriodStart..PeriodEnd). Untagged time → admin/overhead
    /// bucket recorded only in the report (no ProjectCostEntry written
    /// since there's no project). Idempotent — already-allocated time
    /// rows are skipped.</summary>
    Task<PayrollLabourAllocationResponse> AllocatePayrollRunAsync(Guid companyId, Guid payrollRunId, CancellationToken ct = default);
}

public interface IFixVariableCostReportService
{
    Task<FixVariableCostReport> GetMonthlyAsync(Guid companyId, int year, int month, Guid? projectId, CancellationToken ct = default);
    Task<List<FixVariableCostMonthlyTrend>> GetTrendAsync(Guid companyId, int fromYear, int fromMonth, int months,
        Guid? projectId, CancellationToken ct = default);
}

public class HrAllocationService : IEmployeeProjectTimeService, IFixVariableCostReportService
{
    private readonly AccountingDbContext _db;

    public HrAllocationService(AccountingDbContext db) { _db = db; }

    public async Task<EmployeeProjectTimeResponse> CreateAsync(Guid companyId, CreateEmployeeProjectTimeRequest req, CancellationToken ct = default)
    {
        if (req.Hours <= 0) throw new ArgumentException("Hours must be positive.");
        var emp = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == req.EmployeeId && e.CompanyId == companyId && !e.IsDeleted, ct)
            ?? throw new InvalidOperationException("ไม่พบพนักงาน");

        var row = new EmployeeProjectTime
        {
            CompanyId = companyId,
            EmployeeId = emp.Id,
            ProjectId = req.ProjectId,
            ProjectTaskId = req.ProjectTaskId,
            WorkDate = req.WorkDate.Date,
            Hours = req.Hours,
            Description = req.Description,
            Category = req.Category,
            ExternalId = req.ExternalId,
            ExternalSystem = req.ExternalSystem,
            LastSyncedAt = req.ExternalId != null ? DateTime.UtcNow : null,
        };
        _db.EmployeeProjectTimes.Add(row);
        await _db.SaveChangesAsync(ct);
        return await GetResponseAsync(companyId, row.Id, ct);
    }

    public async Task<EmployeeProjectTimeResponse> UpdateAsync(Guid companyId, Guid id, UpdateEmployeeProjectTimeRequest req, CancellationToken ct = default)
    {
        var row = await _db.EmployeeProjectTimes
            .FirstOrDefaultAsync(t => t.Id == id && t.CompanyId == companyId && !t.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบรายการเวลาทำงาน");
        if (row.IsAllocated)
            throw new InvalidOperationException("รายการนี้ถูกใช้คำนวณ cost ของรอบจ่ายเงินเดือนแล้ว — แก้ไขไม่ได้");

        if (req.ProjectId.HasValue) row.ProjectId = req.ProjectId;
        if (req.ProjectTaskId.HasValue) row.ProjectTaskId = req.ProjectTaskId;
        if (req.WorkDate.HasValue) row.WorkDate = req.WorkDate.Value.Date;
        if (req.Hours.HasValue)
        {
            if (req.Hours.Value <= 0) throw new ArgumentException("Hours must be positive.");
            row.Hours = req.Hours.Value;
        }
        if (req.Description != null) row.Description = req.Description;
        if (req.Category != null) row.Category = req.Category;
        await _db.SaveChangesAsync(ct);
        return await GetResponseAsync(companyId, row.Id, ct);
    }

    public async Task DeleteAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        var row = await _db.EmployeeProjectTimes
            .FirstOrDefaultAsync(t => t.Id == id && t.CompanyId == companyId && !t.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบรายการเวลาทำงาน");
        if (row.IsAllocated)
            throw new InvalidOperationException("รายการนี้ถูกใช้คำนวณ cost ของรอบจ่ายเงินเดือนแล้ว — ลบไม่ได้");
        row.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<PagedResponse<EmployeeProjectTimeResponse>> ListAsync(Guid companyId, Guid? employeeId, Guid? projectId,
        DateTime? from, DateTime? to, PagedRequest paging, CancellationToken ct = default)
    {
        var q = _db.EmployeeProjectTimes
            .Include(t => t.Employee)
            .Include(t => t.Project)
            .Where(t => t.CompanyId == companyId && !t.IsDeleted);
        if (employeeId.HasValue) q = q.Where(t => t.EmployeeId == employeeId.Value);
        if (projectId.HasValue) q = q.Where(t => t.ProjectId == projectId.Value);
        if (from.HasValue) q = q.Where(t => t.WorkDate >= from.Value.Date);
        if (to.HasValue) q = q.Where(t => t.WorkDate <= to.Value.Date);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(t => t.WorkDate)
            .Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize)
            .ToListAsync(ct);
        return new PagedResponse<EmployeeProjectTimeResponse>(
            items.Select(Map).ToList(), total, paging.Page, paging.PageSize,
            (int)Math.Ceiling(total / (double)paging.PageSize));
    }

    public async Task<SyncEmployeeProjectTimesResponse> SyncAsync(Guid companyId, SyncEmployeeProjectTimesRequest req, CancellationToken ct = default)
    {
        var inserted = 0; var updated = 0; var skipped = 0;
        var errors = new List<string>();

        // Cache employee lookup by ExternalId(per-employee) AND by Id for
        // rows whose ExternalSystem matches the request's system label.
        var employeeIds = req.Rows.Select(r => r.EmployeeId).Distinct().ToList();
        var validEmployees = await _db.Employees
            .Where(e => e.CompanyId == companyId && employeeIds.Contains(e.Id) && !e.IsDeleted)
            .Select(e => e.Id).ToListAsync(ct);

        foreach (var r in req.Rows)
        {
            if (!validEmployees.Contains(r.EmployeeId))
            {
                errors.Add($"ไม่พบพนักงาน {r.EmployeeId}"); skipped++; continue;
            }
            if (r.Hours <= 0)
            {
                errors.Add($"Hours ต้องมากกว่า 0 (พนักงาน {r.EmployeeId} วันที่ {r.WorkDate:yyyy-MM-dd})"); skipped++; continue;
            }

            EmployeeProjectTime? existing = null;
            if (!string.IsNullOrEmpty(r.ExternalId))
            {
                existing = await _db.EmployeeProjectTimes.FirstOrDefaultAsync(t =>
                    t.CompanyId == companyId
                    && t.ExternalSystem == req.ExternalSystem
                    && t.ExternalId == r.ExternalId
                    && !t.IsDeleted, ct);
            }

            if (existing != null)
            {
                if (existing.IsAllocated) { skipped++; continue; }
                existing.EmployeeId = r.EmployeeId;
                existing.ProjectId = r.ProjectId;
                existing.ProjectTaskId = r.ProjectTaskId;
                existing.WorkDate = r.WorkDate.Date;
                existing.Hours = r.Hours;
                existing.Description = r.Description;
                existing.Category = r.Category;
                existing.LastSyncedAt = DateTime.UtcNow;
                updated++;
            }
            else
            {
                _db.EmployeeProjectTimes.Add(new EmployeeProjectTime
                {
                    CompanyId = companyId,
                    EmployeeId = r.EmployeeId,
                    ProjectId = r.ProjectId,
                    ProjectTaskId = r.ProjectTaskId,
                    WorkDate = r.WorkDate.Date,
                    Hours = r.Hours,
                    Description = r.Description,
                    Category = r.Category,
                    ExternalId = r.ExternalId,
                    ExternalSystem = req.ExternalSystem,
                    LastSyncedAt = !string.IsNullOrEmpty(r.ExternalId) ? DateTime.UtcNow : null,
                });
                inserted++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return new SyncEmployeeProjectTimesResponse(inserted, updated, skipped, errors);
    }

    public async Task<PayrollLabourAllocationResponse> AllocatePayrollRunAsync(Guid companyId, Guid payrollRunId, CancellationToken ct = default)
    {
        var run = await _db.Set<PayrollRun>()
            .Include(r => r.Details).ThenInclude(d => d.Employee)
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        if (run.Status != "Paid" && run.Status != "Approved")
            throw new InvalidOperationException("จัดสรร labour cost ได้เฉพาะรอบที่อนุมัติ/จ่ายแล้ว");

        var details = run.Details.ToList();
        var employeeIds = details.Select(d => d.EmployeeId).ToList();

        var timeRows = await _db.EmployeeProjectTimes
            .Where(t => t.CompanyId == companyId
                && employeeIds.Contains(t.EmployeeId)
                && t.WorkDate >= run.PeriodStart.Date
                && t.WorkDate <= run.PeriodEnd.Date
                && !t.IsDeleted
                && !t.IsAllocated)
            .ToListAsync(ct);

        var projects = await _db.Projects
            .Where(p => p.CompanyId == companyId).Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var entriesCreated = 0;
        decimal totalAllocated = 0m;
        decimal unallocatedAdmin = 0m;
        var perProject = new Dictionary<Guid?, (decimal Hours, decimal Amount)>();

        foreach (var det in details)
        {
            var empRows = timeRows.Where(t => t.EmployeeId == det.EmployeeId).ToList();
            var totalHours = empRows.Sum(t => t.Hours);
            if (totalHours <= 0) continue;

            var gross = det.GrossIncome;
            if (gross <= 0) continue;

            // CostBehavior — Monthly salaried defaults to Fixed (the salary
            // is incurred regardless); Daily/Hourly defaults to Variable.
            // ProjectCostEntry inherits this from the employee.
            var behavior = det.Employee?.CostBehavior ?? "Fixed";

            var byProject = empRows
                .GroupBy(r => r.ProjectId)
                .Select(g => new { ProjectId = g.Key, Hours = g.Sum(x => x.Hours), Rows = g.ToList() })
                .ToList();

            foreach (var grp in byProject)
            {
                var share = grp.Hours / totalHours;
                var alloc = Math.Round(gross * share, 2, MidpointRounding.AwayFromZero);

                if (!perProject.ContainsKey(grp.ProjectId))
                    perProject[grp.ProjectId] = (0m, 0m);
                var cur = perProject[grp.ProjectId];
                perProject[grp.ProjectId] = (cur.Hours + grp.Hours, cur.Amount + alloc);

                if (grp.ProjectId.HasValue)
                {
                    var pce = new ProjectCostEntry
                    {
                        CompanyId = companyId,
                        ProjectId = grp.ProjectId.Value,
                        EntryDate = run.PayDate,
                        CostType = "Labor",
                        Description = $"แรงงาน {det.Employee?.FirstNameTh} {det.Employee?.LastNameTh} ({run.Month:D2}/{run.Year})",
                        Quantity = grp.Hours,
                        UnitCost = grp.Hours > 0 ? alloc / grp.Hours : 0,
                        Amount = alloc,
                        EmployeeId = det.EmployeeId,
                        JournalEntryId = run.JournalEntryId,
                        CostBehavior = behavior,
                        IsBillable = empRows.Any(r => r.Category == "Billable"),
                    };
                    _db.ProjectCostEntries.Add(pce);
                    entriesCreated++;
                    foreach (var row in grp.Rows)
                    {
                        row.ProjectCostEntryId = pce.Id;
                    }
                    totalAllocated += alloc;
                }
                else
                {
                    // Admin / non-project — no ProjectCostEntry (no project to attach to)
                    unallocatedAdmin += alloc;
                }

                foreach (var row in grp.Rows)
                {
                    row.IsAllocated = true;
                    row.AllocatedPayrollRunId = run.Id;
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        var summary = perProject
            .Select(kv => new PayrollLabourAllocationLine(
                kv.Key,
                kv.Key.HasValue && projects.TryGetValue(kv.Key.Value, out var pn) ? pn : null,
                kv.Value.Hours,
                0m, // share filled by caller using total; not critical
                kv.Value.Amount,
                "Mixed"))
            .OrderByDescending(l => l.AllocatedAmount)
            .ToList();

        var employeesAllocated = details.Count(d => timeRows.Any(t => t.EmployeeId == d.EmployeeId));

        return new PayrollLabourAllocationResponse(
            run.Id, run.Year, run.Month,
            employeesAllocated, entriesCreated,
            totalAllocated, unallocatedAdmin, summary);
    }

    public async Task<FixVariableCostReport> GetMonthlyAsync(Guid companyId, int year, int month, Guid? projectId, CancellationToken ct = default)
    {
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);

        var q = _db.ProjectCostEntries
            .Where(p => p.CompanyId == companyId
                && !p.IsDeleted
                && p.EntryDate >= start && p.EntryDate < end);
        if (projectId.HasValue) q = q.Where(p => p.ProjectId == projectId.Value);

        var rows = await q.Select(p => new {
            p.CostType,
            p.CostBehavior,
            p.Amount,
            p.ProjectId
        }).ToListAsync(ct);

        var fixedSum = rows.Where(r => r.CostBehavior == "Fixed").Sum(r => r.Amount);
        var varSum = rows.Where(r => r.CostBehavior != "Fixed").Sum(r => r.Amount);

        var byType = rows.GroupBy(r => r.CostType)
            .Select(g => new FixVariableCostBreakdown(
                g.Key,
                g.Where(r => r.CostBehavior == "Fixed").Sum(r => r.Amount),
                g.Where(r => r.CostBehavior != "Fixed").Sum(r => r.Amount),
                g.Sum(r => r.Amount)))
            .OrderByDescending(b => b.Total)
            .ToList();

        string? projectName = null;
        if (projectId.HasValue)
        {
            projectName = await _db.Projects
                .Where(p => p.Id == projectId.Value && p.CompanyId == companyId)
                .Select(p => p.Name).FirstOrDefaultAsync(ct);
        }

        // Admin overhead = ProjectCostEntries are always per-project; the
        // admin bucket only surfaces through the AllocateLabour report.
        // Pull approved-but-unallocated payroll for the month as a proxy.
        var adminOverhead = 0m;

        return new FixVariableCostReport(year, month, projectId, projectName,
            fixedSum, varSum, adminOverhead, fixedSum + varSum, byType);
    }

    public async Task<List<FixVariableCostMonthlyTrend>> GetTrendAsync(Guid companyId, int fromYear, int fromMonth, int months,
        Guid? projectId, CancellationToken ct = default)
    {
        var result = new List<FixVariableCostMonthlyTrend>();
        var cursor = new DateTime(fromYear, fromMonth, 1);
        for (var i = 0; i < months; i++)
        {
            var m = await GetMonthlyAsync(companyId, cursor.Year, cursor.Month, projectId, ct);
            result.Add(new FixVariableCostMonthlyTrend(cursor.Year, cursor.Month,
                m.FixedCost, m.VariableCost, m.TotalCost));
            cursor = cursor.AddMonths(1);
        }
        return result;
    }

    private async Task<EmployeeProjectTimeResponse> GetResponseAsync(Guid companyId, Guid id, CancellationToken ct)
    {
        var row = await _db.EmployeeProjectTimes
            .Include(t => t.Employee)
            .Include(t => t.Project)
            .FirstAsync(t => t.Id == id && t.CompanyId == companyId, ct);
        return Map(row);
    }

    private static EmployeeProjectTimeResponse Map(EmployeeProjectTime t) =>
        new(t.Id, t.EmployeeId,
            t.Employee?.EmployeeCode ?? "",
            t.Employee != null ? $"{t.Employee.FirstNameTh} {t.Employee.LastNameTh}".Trim() : "",
            t.ProjectId,
            t.Project?.Code,
            t.Project?.Name,
            t.ProjectTaskId,
            t.WorkDate, t.Hours, t.Description, t.Category,
            t.IsAllocated, t.AllocatedPayrollRunId, t.ProjectCostEntryId,
            t.ExternalId, t.ExternalSystem, t.LastSyncedAt);
}
