using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CmsWishlistService : ICmsWishlistService
{
    private readonly AccountingDbContext _db;

    public CmsWishlistService(AccountingDbContext db) { _db = db; }

    public async Task<List<WishlistItemResponse>> GetWishlistAsync(Guid companyId, Guid siteId, Guid customerId)
    {
        var rows = await _db.SiteWishlistItems.AsNoTracking()
            .Include(w => w.SiteProduct).ThenInclude(p => p.Product)
            .Where(w => w.CustomerId == customerId && w.CompanyId == companyId && w.SiteProduct.SiteId == siteId)
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync();

        return rows.Select(w => new WishlistItemResponse
        {
            Id = w.Id, SiteProductId = w.SiteProductId,
            ProductName = w.SiteProduct.DisplayName ?? w.SiteProduct.Product.Name,
            ProductSlug = w.SiteProduct.Slug,
            ImageUrl = ExtractFirstImage(w.SiteProduct.ImageUrlsJson),
            Price = w.SiteProduct.OverrideSellingPrice ?? w.SiteProduct.Product.SellingPrice,
            CompareAtPrice = w.SiteProduct.CompareAtPrice,
            AddedAt = w.CreatedAt
        }).ToList();
    }

    private static string? ExtractFirstImage(string? imageUrlsJson)
    {
        if (string.IsNullOrWhiteSpace(imageUrlsJson)) return null;
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(imageUrlsJson);
            return arr?.FirstOrDefault();
        }
        catch
        {
            return imageUrlsJson;
        }
    }

    public async Task<WishlistItemResponse> AddToWishlistAsync(Guid companyId, Guid siteId, Guid customerId, AddToWishlistRequest request)
    {
        var product = await _db.SiteProducts.AsNoTracking()
            .Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == request.SiteProductId && p.SiteId == siteId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Product not found.");

        var existing = await _db.SiteWishlistItems
            .FirstOrDefaultAsync(w => w.CustomerId == customerId && w.SiteProductId == request.SiteProductId);
        if (existing != null)
        {
            return new WishlistItemResponse
            {
                Id = existing.Id, SiteProductId = existing.SiteProductId,
                ProductName = product.DisplayName ?? product.Product.Name,
                ProductSlug = product.Slug, ImageUrl = ExtractFirstImage(product.ImageUrlsJson),
                Price = product.OverrideSellingPrice ?? product.Product.SellingPrice,
                CompareAtPrice = product.CompareAtPrice, AddedAt = existing.CreatedAt
            };
        }

        var item = new SiteWishlistItem
        {
            CompanyId = companyId, CustomerId = customerId, SiteProductId = request.SiteProductId
        };
        _db.SiteWishlistItems.Add(item);
        await _db.SaveChangesAsync();

        return new WishlistItemResponse
        {
            Id = item.Id, SiteProductId = item.SiteProductId,
            ProductName = product.DisplayName ?? product.Product.Name,
            ProductSlug = product.Slug, ImageUrl = ExtractFirstImage(product.ImageUrlsJson),
            Price = product.OverrideSellingPrice ?? product.Product.SellingPrice,
            CompareAtPrice = product.CompareAtPrice, AddedAt = item.CreatedAt
        };
    }

    public async Task<bool> RemoveFromWishlistAsync(Guid companyId, Guid siteId, Guid customerId, Guid siteProductId)
    {
        var item = await _db.SiteWishlistItems
            .FirstOrDefaultAsync(w => w.CustomerId == customerId && w.SiteProductId == siteProductId && w.CompanyId == companyId);
        if (item == null) return false;
        _db.SiteWishlistItems.Remove(item);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> IsInWishlistAsync(Guid companyId, Guid siteId, Guid customerId, Guid siteProductId)
    {
        return await _db.SiteWishlistItems.AnyAsync(w =>
            w.CustomerId == customerId && w.SiteProductId == siteProductId && w.CompanyId == companyId);
    }
}
