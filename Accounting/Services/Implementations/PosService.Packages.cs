using Accounting.Models.DTOs.Pos;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class PosService
{
    // ==================== Service Package ====================

    public async Task<ServicePackageResponse> CreateServicePackageAsync(Guid companyId, CreateServicePackageRequest request)
    {
        var pkg = new ServicePackage
        {
            CompanyId = companyId,
            Name = request.Name,
            NameEn = request.NameEn,
            Description = request.Description,
            Sku = request.Sku,
            Category = request.Category,
            Price = request.Price,
            CostPrice = request.CostPrice,
            DurationMinutes = request.DurationMinutes,
            IsVatIncluded = request.IsVatIncluded,
            RevenueAccountId = request.RevenueAccountId,
            ImageUrl = request.ImageUrl,
            SortOrder = request.SortOrder
        };
        _db.ServicePackages.Add(pkg);
        await _db.SaveChangesAsync();

        if (request.Components != null)
        {
            foreach (var c in request.Components)
            {
                _db.ServiceComponents.Add(new ServiceComponent
                {
                    PackageId = pkg.Id,
                    StepOrder = c.StepOrder,
                    Name = c.Name,
                    NameEn = c.NameEn,
                    Description = c.Description,
                    DurationMinutes = c.DurationMinutes,
                    CommissionType = c.CommissionType,
                    CommissionValue = c.CommissionValue,
                    RequiresStaff = c.RequiresStaff
                });
            }
            await _db.SaveChangesAsync();
        }

        return await GetServicePackageAsync(companyId, pkg.Id);
    }

    public async Task<List<ServicePackageResponse>> GetServicePackagesAsync(Guid companyId, string? category = null)
    {
        var query = _db.ServicePackages.Include(p => p.Components)
            .Where(p => p.CompanyId == companyId);
        if (!string.IsNullOrEmpty(category)) query = query.Where(p => p.Category == category);
        var pkgs = await query.OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync();
        return pkgs.Select(MapPackage).ToList();
    }

    public async Task<ServicePackageResponse> GetServicePackageAsync(Guid companyId, Guid packageId)
    {
        var pkg = await _db.ServicePackages.Include(p => p.Components)
            .FirstOrDefaultAsync(p => p.Id == packageId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบแพ็คเกจบริการ");
        return MapPackage(pkg);
    }

    public async Task<ServicePackageResponse> UpdateServicePackageAsync(Guid companyId, Guid packageId, UpdateServicePackageRequest request)
    {
        var pkg = await _db.ServicePackages.FirstOrDefaultAsync(p => p.Id == packageId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบแพ็คเกจบริการ");
        if (request.Name != null) pkg.Name = request.Name;
        if (request.NameEn != null) pkg.NameEn = request.NameEn;
        if (request.Description != null) pkg.Description = request.Description;
        if (request.Sku != null) pkg.Sku = request.Sku;
        if (request.Category != null) pkg.Category = request.Category;
        if (request.Price.HasValue) pkg.Price = request.Price.Value;
        if (request.CostPrice.HasValue) pkg.CostPrice = request.CostPrice;
        if (request.DurationMinutes.HasValue) pkg.DurationMinutes = request.DurationMinutes.Value;
        if (request.IsVatIncluded.HasValue) pkg.IsVatIncluded = request.IsVatIncluded.Value;
        if (request.IsActive.HasValue) pkg.IsActive = request.IsActive.Value;
        if (request.RevenueAccountId.HasValue) pkg.RevenueAccountId = request.RevenueAccountId;
        if (request.ImageUrl != null) pkg.ImageUrl = request.ImageUrl;
        if (request.SortOrder.HasValue) pkg.SortOrder = request.SortOrder.Value;
        await _db.SaveChangesAsync();
        return await GetServicePackageAsync(companyId, packageId);
    }

    public async Task DeleteServicePackageAsync(Guid companyId, Guid packageId)
    {
        var pkg = await _db.ServicePackages.FirstOrDefaultAsync(p => p.Id == packageId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบแพ็คเกจบริการ");
        pkg.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ==================== Service Component ====================

    public async Task<ServicePackageResponse> AddComponentAsync(Guid companyId, Guid packageId, CreateServiceComponentRequest request)
    {
        var pkg = await _db.ServicePackages.FirstOrDefaultAsync(p => p.Id == packageId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบแพ็คเกจบริการ");

        _db.ServiceComponents.Add(new ServiceComponent
        {
            PackageId = packageId,
            StepOrder = request.StepOrder,
            Name = request.Name,
            NameEn = request.NameEn,
            Description = request.Description,
            DurationMinutes = request.DurationMinutes,
            CommissionType = request.CommissionType,
            CommissionValue = request.CommissionValue,
            RequiresStaff = request.RequiresStaff
        });
        await _db.SaveChangesAsync();
        return await GetServicePackageAsync(companyId, packageId);
    }

    public async Task<ServicePackageResponse> UpdateComponentAsync(Guid companyId, Guid packageId, Guid componentId, UpdateServiceComponentRequest request)
    {
        var comp = await _db.ServiceComponents.Include(c => c.Package)
            .FirstOrDefaultAsync(c => c.Id == componentId && c.PackageId == packageId && c.Package.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบขั้นตอนบริการ");

        if (request.StepOrder.HasValue) comp.StepOrder = request.StepOrder.Value;
        if (request.Name != null) comp.Name = request.Name;
        if (request.NameEn != null) comp.NameEn = request.NameEn;
        if (request.Description != null) comp.Description = request.Description;
        if (request.DurationMinutes.HasValue) comp.DurationMinutes = request.DurationMinutes.Value;
        if (request.CommissionType.HasValue) comp.CommissionType = request.CommissionType.Value;
        if (request.CommissionValue.HasValue) comp.CommissionValue = request.CommissionValue.Value;
        if (request.RequiresStaff.HasValue) comp.RequiresStaff = request.RequiresStaff.Value;
        await _db.SaveChangesAsync();
        return await GetServicePackageAsync(companyId, packageId);
    }

    public async Task RemoveComponentAsync(Guid companyId, Guid packageId, Guid componentId)
    {
        var comp = await _db.ServiceComponents.Include(c => c.Package)
            .FirstOrDefaultAsync(c => c.Id == componentId && c.PackageId == packageId && c.Package.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบขั้นตอนบริการ");
        comp.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ==================== Service Activity ====================

    public async Task<ServiceActivityResponse> UpdateServiceActivityAsync(Guid companyId, Guid activityId, UpdateServiceActivityRequest request)
    {
        var activity = await _db.PosServiceActivities
            .Include(a => a.Component)
            .Include(a => a.OrderItem).ThenInclude(i => i.Order)
            .FirstOrDefaultAsync(a => a.Id == activityId && a.OrderItem.Order.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกิจกรรมบริการ");

        if (request.StaffId.HasValue) activity.StaffId = request.StaffId;
        if (request.StaffName != null) activity.StaffName = request.StaffName;
        if (request.Notes != null) activity.Notes = request.Notes;
        if (request.Status.HasValue)
        {
            activity.Status = request.Status.Value;
            if (request.Status == ServiceActivityStatus.InProgress && activity.StartedAt == null)
                activity.StartedAt = DateTime.UtcNow;
            if (request.Status == ServiceActivityStatus.Completed)
                activity.CompletedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        return new ServiceActivityResponse(
            activity.Id, activity.ComponentId, activity.Component.Name, activity.Component.StepOrder,
            activity.StaffId, activity.StaffName, activity.Status,
            activity.StartedAt, activity.CompletedAt, activity.CommissionAmount, activity.Notes);
    }

    // ==================== Modifier Group ====================

    public async Task<ModifierGroupResponse> CreateModifierGroupAsync(Guid companyId, CreateModifierGroupRequest request)
    {
        var group = new ProductModifierGroup
        {
            CompanyId = companyId,
            Name = request.Name,
            NameEn = request.NameEn,
            IsRequired = request.IsRequired,
            AllowMultiple = request.AllowMultiple,
            SortOrder = request.SortOrder
        };
        _db.ProductModifierGroups.Add(group);
        await _db.SaveChangesAsync();

        // Link to products
        if (request.ProductIds != null)
        {
            foreach (var pid in request.ProductIds)
            {
                _db.ProductModifierGroupLinks.Add(new ProductModifierGroupLink
                {
                    ProductId = pid,
                    ModifierGroupId = group.Id
                });
            }
        }

        // Add options
        if (request.Options != null)
        {
            foreach (var opt in request.Options)
            {
                _db.ProductModifierOptions.Add(new ProductModifierOption
                {
                    GroupId = group.Id,
                    Name = opt.Name,
                    NameEn = opt.NameEn,
                    PriceAdjustment = opt.PriceAdjustment,
                    IsDefault = opt.IsDefault,
                    SortOrder = opt.SortOrder
                });
            }
        }
        await _db.SaveChangesAsync();

        return await GetModifierGroupResponseAsync(group.Id);
    }

    public async Task<List<ModifierGroupResponse>> GetModifierGroupsAsync(Guid companyId, Guid? productId = null)
    {
        var query = _db.ProductModifierGroups.Include(g => g.Options)
            .Where(g => g.CompanyId == companyId);

        if (productId.HasValue)
        {
            var groupIds = await _db.ProductModifierGroupLinks
                .Where(l => l.ProductId == productId.Value).Select(l => l.ModifierGroupId).ToListAsync();
            query = query.Where(g => groupIds.Contains(g.Id));
        }

        var groups = await query.OrderBy(g => g.SortOrder).ToListAsync();
        return groups.Select(MapModifierGroup).ToList();
    }

    public async Task<ModifierGroupResponse> UpdateModifierGroupAsync(Guid companyId, Guid groupId, UpdateModifierGroupRequest request)
    {
        var group = await _db.ProductModifierGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มตัวเลือก");
        if (request.Name != null) group.Name = request.Name;
        if (request.NameEn != null) group.NameEn = request.NameEn;
        if (request.IsRequired.HasValue) group.IsRequired = request.IsRequired.Value;
        if (request.AllowMultiple.HasValue) group.AllowMultiple = request.AllowMultiple.Value;
        if (request.SortOrder.HasValue) group.SortOrder = request.SortOrder.Value;
        await _db.SaveChangesAsync();
        return await GetModifierGroupResponseAsync(groupId);
    }

    public async Task DeleteModifierGroupAsync(Guid companyId, Guid groupId)
    {
        var group = await _db.ProductModifierGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มตัวเลือก");
        group.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ==================== Modifier Option ====================

    public async Task<ModifierGroupResponse> AddModifierOptionAsync(Guid companyId, Guid groupId, CreateModifierOptionRequest request)
    {
        var group = await _db.ProductModifierGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มตัวเลือก");
        _db.ProductModifierOptions.Add(new ProductModifierOption
        {
            GroupId = groupId,
            Name = request.Name,
            NameEn = request.NameEn,
            PriceAdjustment = request.PriceAdjustment,
            IsDefault = request.IsDefault,
            SortOrder = request.SortOrder
        });
        await _db.SaveChangesAsync();
        return await GetModifierGroupResponseAsync(groupId);
    }

    public async Task<ModifierGroupResponse> UpdateModifierOptionAsync(Guid companyId, Guid groupId, Guid optionId, UpdateModifierOptionRequest request)
    {
        var opt = await _db.ProductModifierOptions.Include(o => o.Group)
            .FirstOrDefaultAsync(o => o.Id == optionId && o.GroupId == groupId && o.Group.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบตัวเลือก");
        if (request.Name != null) opt.Name = request.Name;
        if (request.NameEn != null) opt.NameEn = request.NameEn;
        if (request.PriceAdjustment.HasValue) opt.PriceAdjustment = request.PriceAdjustment.Value;
        if (request.IsDefault.HasValue) opt.IsDefault = request.IsDefault.Value;
        if (request.IsActive.HasValue) opt.IsActive = request.IsActive.Value;
        if (request.SortOrder.HasValue) opt.SortOrder = request.SortOrder.Value;
        await _db.SaveChangesAsync();
        return await GetModifierGroupResponseAsync(groupId);
    }

    public async Task RemoveModifierOptionAsync(Guid companyId, Guid groupId, Guid optionId)
    {
        var opt = await _db.ProductModifierOptions.Include(o => o.Group)
            .FirstOrDefaultAsync(o => o.Id == optionId && o.GroupId == groupId && o.Group.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบตัวเลือก");
        opt.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ==================== Package Mappers ====================

    private async Task<ModifierGroupResponse> GetModifierGroupResponseAsync(Guid groupId)
    {
        var group = await _db.ProductModifierGroups.Include(g => g.Options).FirstAsync(g => g.Id == groupId);
        return MapModifierGroup(group);
    }

    private static ModifierGroupResponse MapModifierGroup(ProductModifierGroup g) => new(
        g.Id, g.Name, g.NameEn, g.IsRequired, g.AllowMultiple, g.SortOrder,
        g.Options.Where(o => !o.IsDeleted).OrderBy(o => o.SortOrder)
            .Select(o => new ModifierOptionResponse(o.Id, o.Name, o.NameEn, o.PriceAdjustment, o.IsDefault, o.SortOrder, o.IsActive))
            .ToList());

    private static ServicePackageResponse MapPackage(ServicePackage p) => new(
        p.Id, p.Name, p.NameEn, p.Description, p.Sku, p.Category,
        p.Price, p.CostPrice, p.DurationMinutes, p.IsActive, p.IsVatIncluded,
        p.RevenueAccountId, p.ImageUrl, p.SortOrder,
        p.Components.Where(c => !c.IsDeleted).OrderBy(c => c.StepOrder)
            .Select(c => new ServiceComponentResponse(c.Id, c.StepOrder, c.Name, c.NameEn, c.Description, c.DurationMinutes, c.CommissionType, c.CommissionValue, c.RequiresStaff))
            .ToList());
}
