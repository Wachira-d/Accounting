using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CmsVariantService : ICmsVariantService
{
    private readonly AccountingDbContext _db;

    public CmsVariantService(AccountingDbContext db) { _db = db; }

    public async Task<ProductVariantResponse> AddVariantAsync(Guid companyId, Guid siteId, Guid siteProductId, CreateProductVariantRequest request, string userId)
    {
        var product = await _db.SiteProducts.FirstOrDefaultAsync(p => p.Id == siteProductId && p.SiteId == siteId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Site product not found.");

        if (await _db.SiteProductVariants.AnyAsync(v => v.SiteProductId == siteProductId && v.Sku == request.Sku))
            throw new InvalidOperationException("SKU already exists for this product.");

        var variant = new SiteProductVariant
        {
            CompanyId = companyId, SiteProductId = siteProductId,
            Sku = request.Sku, Name = request.Name,
            OptionsJson = request.Options != null ? JsonSerializer.Serialize(request.Options) : null,
            Price = request.Price, CompareAtPrice = request.CompareAtPrice,
            Stock = request.Stock, WeightKg = request.WeightKg,
            ImageUrl = request.ImageUrl, Barcode = request.Barcode,
            ErpProductId = request.ErpProductId,
            SortOrder = request.SortOrder, CreatedBy = userId
        };
        _db.SiteProductVariants.Add(variant);
        await _db.SaveChangesAsync();
        return MapVariant(variant);
    }

    public async Task<ProductVariantResponse> UpdateVariantAsync(Guid companyId, Guid siteId, Guid siteProductId, Guid variantId, CreateProductVariantRequest request, string userId)
    {
        var variant = await _db.SiteProductVariants
            .Include(v => v.SiteProduct)
            .FirstOrDefaultAsync(v => v.Id == variantId && v.SiteProductId == siteProductId && v.SiteProduct.SiteId == siteId)
            ?? throw new KeyNotFoundException("Variant not found.");

        variant.Sku = request.Sku;
        variant.Name = request.Name;
        variant.OptionsJson = request.Options != null ? JsonSerializer.Serialize(request.Options) : null;
        variant.Price = request.Price;
        variant.CompareAtPrice = request.CompareAtPrice;
        variant.Stock = request.Stock;
        variant.WeightKg = request.WeightKg;
        variant.ImageUrl = request.ImageUrl;
        variant.Barcode = request.Barcode;
        variant.ErpProductId = request.ErpProductId;
        variant.SortOrder = request.SortOrder;
        variant.UpdatedBy = userId;

        await _db.SaveChangesAsync();
        return MapVariant(variant);
    }

    public async Task<List<ProductVariantResponse>> GetVariantsAsync(Guid companyId, Guid siteId, Guid siteProductId)
    {
        return await _db.SiteProductVariants.AsNoTracking()
            .Include(v => v.SiteProduct)
            .Where(v => v.SiteProductId == siteProductId && v.SiteProduct.SiteId == siteId && v.SiteProduct.CompanyId == companyId)
            .OrderBy(v => v.SortOrder)
            .Select(v => new ProductVariantResponse
            {
                Id = v.Id, SiteProductId = v.SiteProductId, Sku = v.Sku, Name = v.Name,
                OptionsJson = v.OptionsJson, Price = v.Price, CompareAtPrice = v.CompareAtPrice,
                Stock = v.Stock, WeightKg = v.WeightKg,
                ImageUrl = v.ImageUrl, Barcode = v.Barcode,
                IsActive = v.IsActive, SortOrder = v.SortOrder, ErpProductId = v.ErpProductId
            })
            .ToListAsync();
    }

    public async Task<bool> DeleteVariantAsync(Guid companyId, Guid siteId, Guid siteProductId, Guid variantId)
    {
        var variant = await _db.SiteProductVariants
            .Include(v => v.SiteProduct)
            .FirstOrDefaultAsync(v => v.Id == variantId && v.SiteProductId == siteProductId && v.SiteProduct.SiteId == siteId);
        if (variant == null) return false;
        variant.IsActive = false;
        variant.IsDeleted = true;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<ProductOptionResponse> AddOptionAsync(Guid companyId, Guid siteId, Guid siteProductId, CreateProductOptionRequest request, string userId)
    {
        var product = await _db.SiteProducts.FirstOrDefaultAsync(p => p.Id == siteProductId && p.SiteId == siteId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Site product not found.");

        var option = new SiteProductOption
        {
            CompanyId = companyId, SiteProductId = siteProductId,
            Name = request.Name, NameEn = request.NameEn,
            DisplayType = request.DisplayType, SortOrder = request.SortOrder, CreatedBy = userId
        };
        _db.SiteProductOptions.Add(option);

        if (request.Values?.Any() == true)
        {
            foreach (var v in request.Values)
            {
                _db.SiteProductOptionValues.Add(new SiteProductOptionValue
                {
                    CompanyId = companyId, OptionId = option.Id,
                    Value = v.Value, ValueEn = v.ValueEn,
                    ColorHex = v.ColorHex, ImageUrl = v.ImageUrl,
                    SortOrder = v.SortOrder, CreatedBy = userId
                });
            }
        }
        await _db.SaveChangesAsync();
        return await GetOptionResponseAsync(option.Id);
    }

    public async Task<List<ProductOptionResponse>> GetOptionsAsync(Guid companyId, Guid siteId, Guid siteProductId)
    {
        return await _db.SiteProductOptions.AsNoTracking()
            .Include(o => o.SiteProduct)
            .Where(o => o.SiteProductId == siteProductId && o.SiteProduct.SiteId == siteId && o.SiteProduct.CompanyId == companyId)
            .OrderBy(o => o.SortOrder)
            .Select(o => new ProductOptionResponse
            {
                Id = o.Id, Name = o.Name, NameEn = o.NameEn,
                DisplayType = o.DisplayType, SortOrder = o.SortOrder,
                Values = o.Values.OrderBy(v => v.SortOrder).Select(v => new ProductOptionValueResponse
                {
                    Id = v.Id, Value = v.Value, ValueEn = v.ValueEn,
                    ColorHex = v.ColorHex, ImageUrl = v.ImageUrl, SortOrder = v.SortOrder
                }).ToList()
            })
            .ToListAsync();
    }

    public async Task<bool> DeleteOptionAsync(Guid companyId, Guid siteId, Guid siteProductId, Guid optionId)
    {
        var option = await _db.SiteProductOptions
            .Include(o => o.SiteProduct)
            .FirstOrDefaultAsync(o => o.Id == optionId && o.SiteProductId == siteProductId && o.SiteProduct.SiteId == siteId);
        if (option == null) return false;
        option.IsDeleted = true;
        await _db.SaveChangesAsync();
        return true;
    }

    private async Task<ProductOptionResponse> GetOptionResponseAsync(Guid optionId)
    {
        var o = await _db.SiteProductOptions.AsNoTracking()
            .Include(x => x.Values)
            .FirstAsync(x => x.Id == optionId);
        return new ProductOptionResponse
        {
            Id = o.Id, Name = o.Name, NameEn = o.NameEn,
            DisplayType = o.DisplayType, SortOrder = o.SortOrder,
            Values = o.Values.OrderBy(v => v.SortOrder).Select(v => new ProductOptionValueResponse
            {
                Id = v.Id, Value = v.Value, ValueEn = v.ValueEn,
                ColorHex = v.ColorHex, ImageUrl = v.ImageUrl, SortOrder = v.SortOrder
            }).ToList()
        };
    }

    private static ProductVariantResponse MapVariant(SiteProductVariant v) => new()
    {
        Id = v.Id, SiteProductId = v.SiteProductId, Sku = v.Sku, Name = v.Name,
        OptionsJson = v.OptionsJson, Price = v.Price, CompareAtPrice = v.CompareAtPrice,
        Stock = v.Stock, WeightKg = v.WeightKg,
        ImageUrl = v.ImageUrl, Barcode = v.Barcode,
        IsActive = v.IsActive, SortOrder = v.SortOrder, ErpProductId = v.ErpProductId
    };
}
