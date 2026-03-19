using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class FixedAssetService : IFixedAssetService
{
    private readonly AccountingDbContext _db;

    public FixedAssetService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<FixedAssetResponse> CreateAsync(Guid companyId, CreateFixedAssetRequest request, string createdBy)
    {
        var existing = await _db.FixedAssets.AnyAsync(a => a.CompanyId == companyId && a.AssetCode == request.AssetCode);
        if (existing)
            throw new InvalidOperationException($"รหัสสินทรัพย์ {request.AssetCode} ซ้ำ");

        var asset = new FixedAsset
        {
            CompanyId = companyId,
            AssetCode = request.AssetCode,
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            Location = request.Location,
            SerialNumber = request.SerialNumber,
            PurchaseDate = request.PurchaseDate,
            PurchaseCost = request.PurchaseCost,
            SalvageValue = request.SalvageValue,
            UsefulLifeMonths = request.UsefulLifeMonths,
            DepreciationMethod = request.DepreciationMethod,
            NetBookValue = request.PurchaseCost,
            AssetAccountId = request.AssetAccountId,
            DepreciationExpenseAccountId = request.DepreciationExpenseAccountId,
            AccumulatedDepreciationAccountId = request.AccumulatedDepreciationAccountId,
            CreatedBy = createdBy
        };

        _db.FixedAssets.Add(asset);
        await _db.SaveChangesAsync();
        return MapToResponse(asset);
    }

    public async Task<FixedAssetResponse> GetByIdAsync(Guid companyId, Guid assetId)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");
        return MapToResponse(asset);
    }

    public async Task<PagedResponse<FixedAssetResponse>> GetAllAsync(Guid companyId, PagedRequest request)
    {
        var query = _db.FixedAssets.Where(a => a.CompanyId == companyId);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(a => a.Name.Contains(request.Search) || a.AssetCode.Contains(request.Search));

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(a => a.AssetCode)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<FixedAssetResponse>(
            items.Select(MapToResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<FixedAssetResponse> UpdateAsync(Guid companyId, Guid assetId, UpdateFixedAssetRequest request)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (request.Name != null) asset.Name = request.Name;
        if (request.Description != null) asset.Description = request.Description;
        if (request.Category != null) asset.Category = request.Category;
        if (request.Location != null) asset.Location = request.Location;
        if (request.SerialNumber != null) asset.SerialNumber = request.SerialNumber;
        if (request.AssetAccountId.HasValue) asset.AssetAccountId = request.AssetAccountId;
        if (request.DepreciationExpenseAccountId.HasValue) asset.DepreciationExpenseAccountId = request.DepreciationExpenseAccountId;
        if (request.AccumulatedDepreciationAccountId.HasValue) asset.AccumulatedDepreciationAccountId = request.AccumulatedDepreciationAccountId;

        await _db.SaveChangesAsync();
        return MapToResponse(asset);
    }

    public async Task<FixedAssetResponse> DisposeAsync(Guid companyId, Guid assetId, DisposeAssetRequest request, string performedBy)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (asset.Status != AssetStatus.Active && asset.Status != AssetStatus.FullyDepreciated)
            throw new InvalidOperationException("สินทรัพย์นี้ไม่สามารถจำหน่ายได้");

        asset.Status = AssetStatus.Disposed;
        asset.DisposalDate = request.DisposalDate;
        asset.DisposalAmount = request.DisposalAmount;

        await _db.SaveChangesAsync();
        return MapToResponse(asset);
    }

    public async Task<List<DepreciationResponse>> GetDepreciationsAsync(Guid companyId, Guid assetId)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        var depreciations = await _db.AssetDepreciations
            .Where(d => d.FixedAssetId == assetId)
            .OrderBy(d => d.Year).ThenBy(d => d.Month)
            .ToListAsync();

        return depreciations.Select(d => new DepreciationResponse(
            d.Id, d.FixedAssetId, d.Year, d.Month,
            d.Amount, d.AccumulatedAmount, d.NetBookValue, d.IsPosted)).ToList();
    }

    public async Task<List<DepreciationResponse>> CalculateDepreciationAsync(
        Guid companyId, CalculateDepreciationRequest request, string performedBy)
    {
        var assets = await _db.FixedAssets
            .Where(a => a.CompanyId == companyId && a.Status == AssetStatus.Active)
            .ToListAsync();

        var results = new List<AssetDepreciation>();

        foreach (var asset in assets)
        {
            // Skip if already calculated for this period
            var exists = await _db.AssetDepreciations
                .AnyAsync(d => d.FixedAssetId == asset.Id && d.Year == request.Year && d.Month == request.Month);
            if (exists) continue;

            var monthlyDepreciation = asset.DepreciationMethod switch
            {
                DepreciationMethod.StraightLine =>
                    (asset.PurchaseCost - asset.SalvageValue) / asset.UsefulLifeMonths,
                DepreciationMethod.DecliningBalance =>
                    asset.NetBookValue * (2.0m / asset.UsefulLifeMonths) / 2,
                DepreciationMethod.DoubleDecliningBalance =>
                    asset.NetBookValue * (2.0m / asset.UsefulLifeMonths),
                _ => 0
            };

            // Don't depreciate below salvage value
            if (asset.NetBookValue - monthlyDepreciation < asset.SalvageValue)
                monthlyDepreciation = asset.NetBookValue - asset.SalvageValue;

            if (monthlyDepreciation <= 0) continue;

            asset.AccumulatedDepreciation += monthlyDepreciation;
            asset.NetBookValue -= monthlyDepreciation;

            if (asset.NetBookValue <= asset.SalvageValue)
                asset.Status = AssetStatus.FullyDepreciated;

            var depreciation = new AssetDepreciation
            {
                CompanyId = companyId,
                FixedAssetId = asset.Id,
                Year = request.Year,
                Month = request.Month,
                Amount = monthlyDepreciation,
                AccumulatedAmount = asset.AccumulatedDepreciation,
                NetBookValue = asset.NetBookValue,
                CreatedBy = performedBy
            };

            _db.AssetDepreciations.Add(depreciation);
            results.Add(depreciation);
        }

        await _db.SaveChangesAsync();

        return results.Select(d => new DepreciationResponse(
            d.Id, d.FixedAssetId, d.Year, d.Month,
            d.Amount, d.AccumulatedAmount, d.NetBookValue, d.IsPosted)).ToList();
    }

    private static FixedAssetResponse MapToResponse(FixedAsset a) =>
        new(a.Id, a.AssetCode, a.Name, a.Description, a.Category,
            a.Location, a.SerialNumber, a.PurchaseDate, a.PurchaseCost,
            a.SalvageValue, a.UsefulLifeMonths, a.DepreciationMethod,
            a.AccumulatedDepreciation, a.NetBookValue, a.Status,
            a.DisposalDate, a.DisposalAmount, a.CreatedAt);
}
