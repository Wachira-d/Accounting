using Accounting.Data;
using Accounting.Models.DTOs.Organization;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class OrganizationService : IOrganizationService
{
    private readonly AccountingDbContext _db;

    public OrganizationService(AccountingDbContext db) => _db = db;

    // ===== Departments =====

    public async Task<DepartmentResponse> CreateDepartmentAsync(Guid companyId, CreateDepartmentRequest request, string createdBy)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
            throw new InvalidOperationException("กรุณาระบุรหัสแผนก");
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new InvalidOperationException("กรุณาระบุชื่อแผนก");

        var dup = await _db.Departments.AnyAsync(d => d.CompanyId == companyId && d.Code == request.Code);
        if (dup) throw new InvalidOperationException($"รหัสแผนก '{request.Code}' ซ้ำ");

        var dept = new Department
        {
            CompanyId = companyId,
            Code = request.Code.Trim(),
            Name = request.Name.Trim(),
            NameEn = request.NameEn,
            Description = request.Description,
            ParentDepartmentId = request.ParentDepartmentId,
            BranchId = request.BranchId,
            DimensionId = request.DimensionId,
            DefaultExpenseAccountId = request.DefaultExpenseAccountId,
            ManagerEmployeeId = request.ManagerEmployeeId,
            IsActive = true,
            CreatedBy = createdBy
        };
        _db.Departments.Add(dept);
        await _db.SaveChangesAsync();
        return await GetDepartmentAsync(companyId, dept.Id);
    }

    public async Task<DepartmentResponse> GetDepartmentAsync(Guid companyId, Guid departmentId)
    {
        var dept = await _db.Departments
            .Include(d => d.ParentDepartment)
            .Include(d => d.Branch)
            .Include(d => d.Dimension)
            .Include(d => d.DefaultExpenseAccount)
            .FirstOrDefaultAsync(d => d.Id == departmentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบแผนก");
        return await MapDepartmentAsync(dept);
    }

    public async Task<List<DepartmentResponse>> GetDepartmentsAsync(Guid companyId, bool includeInactive = false)
    {
        var query = _db.Departments
            .Include(d => d.ParentDepartment)
            .Include(d => d.Branch)
            .Include(d => d.Dimension)
            .Include(d => d.DefaultExpenseAccount)
            .Where(d => d.CompanyId == companyId);
        if (!includeInactive) query = query.Where(d => d.IsActive);

        var depts = await query.OrderBy(d => d.Code).ToListAsync();
        var result = new List<DepartmentResponse>(depts.Count);
        foreach (var d in depts) result.Add(await MapDepartmentAsync(d));
        return result;
    }

    public async Task<DepartmentResponse> UpdateDepartmentAsync(Guid companyId, Guid departmentId, UpdateDepartmentRequest request)
    {
        var dept = await _db.Departments
            .FirstOrDefaultAsync(d => d.Id == departmentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบแผนก");

        if (request.Code != null && request.Code != dept.Code)
        {
            var dup = await _db.Departments.AnyAsync(d => d.CompanyId == companyId && d.Code == request.Code && d.Id != departmentId);
            if (dup) throw new InvalidOperationException($"รหัสแผนก '{request.Code}' ซ้ำ");
            dept.Code = request.Code.Trim();
        }
        if (request.Name != null) dept.Name = request.Name.Trim();
        if (request.NameEn != null) dept.NameEn = request.NameEn;
        if (request.Description != null) dept.Description = request.Description;
        if (request.IsActive.HasValue) dept.IsActive = request.IsActive.Value;
        if (request.ParentDepartmentId.HasValue)
        {
            if (request.ParentDepartmentId.Value == departmentId)
                throw new InvalidOperationException("แผนกไม่สามารถเป็นแผนกแม่ของตัวเองได้");
            dept.ParentDepartmentId = request.ParentDepartmentId;
        }
        if (request.BranchId.HasValue) dept.BranchId = request.BranchId;
        if (request.DimensionId.HasValue) dept.DimensionId = request.DimensionId;
        if (request.DefaultExpenseAccountId.HasValue) dept.DefaultExpenseAccountId = request.DefaultExpenseAccountId;
        if (request.ManagerEmployeeId.HasValue) dept.ManagerEmployeeId = request.ManagerEmployeeId;
        dept.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return await GetDepartmentAsync(companyId, dept.Id);
    }

    public async Task DeleteDepartmentAsync(Guid companyId, Guid departmentId)
    {
        var dept = await _db.Departments
            .FirstOrDefaultAsync(d => d.Id == departmentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบแผนก");

        // Soft-delete; leave any employees pointing at the row unaffected
        // but block deletion if employees are still actively assigned.
        var inUse = await _db.Employees.AnyAsync(e => e.CompanyId == companyId && e.DepartmentId == departmentId && !e.IsDeleted);
        if (inUse)
            throw new InvalidOperationException("ไม่สามารถลบแผนกที่มีพนักงานอยู่ — กรุณาย้ายพนักงานก่อนหรือเปลี่ยนเป็น Inactive แทน");

        dept.IsDeleted = true;
        dept.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ===== Positions =====

    public async Task<PositionResponse> CreatePositionAsync(Guid companyId, CreatePositionRequest request, string createdBy)
    {
        if (string.IsNullOrWhiteSpace(request.Code)) throw new InvalidOperationException("กรุณาระบุรหัสตำแหน่ง");
        if (string.IsNullOrWhiteSpace(request.Title)) throw new InvalidOperationException("กรุณาระบุชื่อตำแหน่ง");

        var dup = await _db.Positions.AnyAsync(p => p.CompanyId == companyId && p.Code == request.Code);
        if (dup) throw new InvalidOperationException($"รหัสตำแหน่ง '{request.Code}' ซ้ำ");

        var pos = new Position
        {
            CompanyId = companyId,
            Code = request.Code.Trim(),
            Title = request.Title.Trim(),
            TitleEn = request.TitleEn,
            Description = request.Description,
            Band = request.Band,
            MinSalary = request.MinSalary,
            MaxSalary = request.MaxSalary,
            IsActive = true,
            CreatedBy = createdBy
        };
        _db.Positions.Add(pos);
        await _db.SaveChangesAsync();
        return await GetPositionAsync(companyId, pos.Id);
    }

    public async Task<PositionResponse> GetPositionAsync(Guid companyId, Guid positionId)
    {
        var pos = await _db.Positions
            .FirstOrDefaultAsync(p => p.Id == positionId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบตำแหน่ง");
        return await MapPositionAsync(pos);
    }

    public async Task<List<PositionResponse>> GetPositionsAsync(Guid companyId, bool includeInactive = false)
    {
        var query = _db.Positions.Where(p => p.CompanyId == companyId);
        if (!includeInactive) query = query.Where(p => p.IsActive);
        var positions = await query.OrderBy(p => p.Code).ToListAsync();
        var result = new List<PositionResponse>(positions.Count);
        foreach (var p in positions) result.Add(await MapPositionAsync(p));
        return result;
    }

    public async Task<PositionResponse> UpdatePositionAsync(Guid companyId, Guid positionId, UpdatePositionRequest request)
    {
        var pos = await _db.Positions
            .FirstOrDefaultAsync(p => p.Id == positionId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบตำแหน่ง");

        if (request.Code != null && request.Code != pos.Code)
        {
            var dup = await _db.Positions.AnyAsync(p => p.CompanyId == companyId && p.Code == request.Code && p.Id != positionId);
            if (dup) throw new InvalidOperationException($"รหัสตำแหน่ง '{request.Code}' ซ้ำ");
            pos.Code = request.Code.Trim();
        }
        if (request.Title != null) pos.Title = request.Title.Trim();
        if (request.TitleEn != null) pos.TitleEn = request.TitleEn;
        if (request.Description != null) pos.Description = request.Description;
        if (request.Band != null) pos.Band = request.Band;
        if (request.IsActive.HasValue) pos.IsActive = request.IsActive.Value;
        if (request.MinSalary.HasValue) pos.MinSalary = request.MinSalary;
        if (request.MaxSalary.HasValue) pos.MaxSalary = request.MaxSalary;
        pos.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return await GetPositionAsync(companyId, pos.Id);
    }

    public async Task DeletePositionAsync(Guid companyId, Guid positionId)
    {
        var pos = await _db.Positions
            .FirstOrDefaultAsync(p => p.Id == positionId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบตำแหน่ง");

        var inUse = await _db.Employees.AnyAsync(e => e.CompanyId == companyId && e.PositionId == positionId && !e.IsDeleted);
        if (inUse)
            throw new InvalidOperationException("ไม่สามารถลบตำแหน่งที่มีพนักงานอยู่ — กรุณาเปลี่ยนเป็น Inactive แทน");

        pos.IsDeleted = true;
        pos.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ===== Reporting lines =====

    public async Task<Guid?> GetDirectManagerUserIdAsync(Guid companyId, Guid employeeId)
    {
        var managerId = await _db.Employees
            .Where(e => e.Id == employeeId && e.CompanyId == companyId && !e.IsDeleted)
            .Select(e => e.DirectManagerId)
            .FirstOrDefaultAsync();
        if (managerId == null) return null;

        return await _db.Employees
            .Where(e => e.Id == managerId.Value && e.CompanyId == companyId && !e.IsDeleted)
            .Select(e => e.UserId)
            .FirstOrDefaultAsync();
    }

    public async Task<DirectManagerInfo> GetDirectManagerInfoAsync(Guid companyId, Guid employeeId)
    {
        var emp = await _db.Employees
            .Include(e => e.DirectManager)
            .Include(e => e.DepartmentRef)
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        string? managerName = null;
        Guid? managerUserId = null;
        string? managerEmail = null;
        if (emp.DirectManager != null)
        {
            managerName = $"{emp.DirectManager.TitleTh}{emp.DirectManager.FirstNameTh} {emp.DirectManager.LastNameTh}".Trim();
            managerUserId = emp.DirectManager.UserId;
            managerEmail = emp.DirectManager.Email;
        }

        // Department-head fallback when no direct manager is set.
        Guid? deptHeadEmployeeId = emp.DepartmentRef?.ManagerEmployeeId;
        string? deptHeadName = null;
        if (deptHeadEmployeeId.HasValue && deptHeadEmployeeId.Value != employeeId)
        {
            deptHeadName = await _db.Employees
                .Where(e => e.Id == deptHeadEmployeeId.Value)
                .Select(e => $"{e.TitleTh}{e.FirstNameTh} {e.LastNameTh}".Trim())
                .FirstOrDefaultAsync();
        }
        else
        {
            deptHeadEmployeeId = null;
        }

        return new DirectManagerInfo(
            emp.Id,
            $"{emp.TitleTh}{emp.FirstNameTh} {emp.LastNameTh}".Trim(),
            emp.DirectManagerId, managerName,
            managerUserId, managerEmail,
            deptHeadEmployeeId, deptHeadName);
    }

    public async Task<List<OrgChartNode>> GetOrgChartAsync(Guid companyId)
    {
        var allEmployees = await _db.Employees
            .Include(e => e.PositionRef)
            .Include(e => e.DepartmentRef)
            .Where(e => e.CompanyId == companyId && !e.IsDeleted)
            .OrderBy(e => e.EmployeeCode)
            .ToListAsync();

        // Group children by manager id for an O(n) tree build.
        var byManager = allEmployees
            .Where(e => e.DirectManagerId.HasValue)
            .GroupBy(e => e.DirectManagerId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        OrgChartNode Build(Employee emp) => new OrgChartNode(
            emp.Id,
            emp.EmployeeCode,
            $"{emp.TitleTh}{emp.FirstNameTh} {emp.LastNameTh}".Trim(),
            emp.PositionRef?.Title ?? emp.Position,
            emp.DepartmentRef?.Name ?? emp.Department,
            emp.IsActive,
            byManager.TryGetValue(emp.Id, out var reports)
                ? reports.Select(Build).ToList()
                : new List<OrgChartNode>());

        // Roots = employees with no direct manager.
        return allEmployees
            .Where(e => !e.DirectManagerId.HasValue)
            .Select(Build)
            .ToList();
    }

    // ===== Mapping helpers =====

    private async Task<DepartmentResponse> MapDepartmentAsync(Department d)
    {
        var employeeCount = await _db.Employees
            .CountAsync(e => e.CompanyId == d.CompanyId && e.DepartmentId == d.Id && !e.IsDeleted);

        string? managerName = null;
        if (d.ManagerEmployeeId.HasValue)
        {
            managerName = await _db.Employees
                .Where(e => e.Id == d.ManagerEmployeeId.Value)
                .Select(e => $"{e.TitleTh}{e.FirstNameTh} {e.LastNameTh}".Trim())
                .FirstOrDefaultAsync();
        }

        return new DepartmentResponse(
            d.Id, d.Code, d.Name, d.NameEn, d.Description, d.IsActive,
            d.ParentDepartmentId, d.ParentDepartment?.Name,
            d.BranchId, d.Branch?.Name,
            d.DimensionId, d.Dimension?.Code, d.Dimension?.Name,
            d.DefaultExpenseAccountId, d.DefaultExpenseAccount?.AccountCode,
            d.ManagerEmployeeId, managerName,
            employeeCount,
            d.CreatedAt);
    }

    private async Task<PositionResponse> MapPositionAsync(Position p)
    {
        var employeeCount = await _db.Employees
            .CountAsync(e => e.CompanyId == p.CompanyId && e.PositionId == p.Id && !e.IsDeleted);
        return new PositionResponse(
            p.Id, p.Code, p.Title, p.TitleEn, p.Description, p.Band, p.IsActive,
            p.MinSalary, p.MaxSalary, employeeCount, p.CreatedAt);
    }
}
