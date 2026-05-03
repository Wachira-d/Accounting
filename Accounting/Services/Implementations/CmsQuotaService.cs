using Accounting.Data;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CmsQuotaService : ICmsQuotaService
{
    private readonly AccountingDbContext _db;

    public CmsQuotaService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<bool> CanAddPageAsync(Guid companyId, Guid siteId)
    {
        var site = await _db.Sites
            .AsNoTracking()
            .Where(s => s.Id == siteId && s.CompanyId == companyId)
            .Select(s => new { s.MaxPages })
            .FirstOrDefaultAsync();

        if (site == null) return false;
        if (site.MaxPages == null) return true;

        var currentCount = await _db.SitePages
            .CountAsync(p => p.SiteId == siteId && p.CompanyId == companyId);

        return currentCount < site.MaxPages.Value;
    }

    public async Task<bool> CanAddProductAsync(Guid companyId, Guid siteId)
    {
        var site = await _db.Sites
            .AsNoTracking()
            .Where(s => s.Id == siteId && s.CompanyId == companyId)
            .Select(s => new { s.MaxProducts })
            .FirstOrDefaultAsync();

        if (site == null) return false;
        if (site.MaxProducts == null) return true;

        var currentCount = await _db.SiteProducts
            .CountAsync(p => p.SiteId == siteId && p.CompanyId == companyId);

        return currentCount < site.MaxProducts.Value;
    }

    public async Task<bool> CanUploadMediaAsync(Guid companyId, Guid siteId, long fileSizeBytes)
    {
        var site = await _db.Sites
            .AsNoTracking()
            .Where(s => s.Id == siteId && s.CompanyId == companyId)
            .Select(s => new { s.MaxStorageBytes, s.CurrentStorageUsed })
            .FirstOrDefaultAsync();

        if (site == null) return false;
        if (site.MaxStorageBytes == null) return true;

        return (site.CurrentStorageUsed + fileSizeBytes) <= site.MaxStorageBytes.Value;
    }

    public async Task<CmsQuotaStatus> GetQuotaStatusAsync(Guid companyId, Guid siteId)
    {
        var site = await _db.Sites
            .AsNoTracking()
            .Where(s => s.Id == siteId && s.CompanyId == companyId)
            .Select(s => new
            {
                s.MaxStorageBytes,
                s.CurrentStorageUsed,
                s.MaxPages,
                s.MaxProducts,
                s.MaxBandwidthBytesPerMonth,
                s.CurrentBandwidthUsed
            })
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("ไม่พบเว็บไซต์");

        var pagesCount = await _db.SitePages.CountAsync(p => p.SiteId == siteId && p.CompanyId == companyId);
        var productsCount = await _db.SiteProducts.CountAsync(p => p.SiteId == siteId && p.CompanyId == companyId);

        return new CmsQuotaStatus
        {
            StorageUsedBytes = site.CurrentStorageUsed,
            StorageLimitBytes = site.MaxStorageBytes,
            PagesCount = pagesCount,
            PagesLimit = site.MaxPages,
            ProductsCount = productsCount,
            ProductsLimit = site.MaxProducts,
            BandwidthUsedBytes = site.CurrentBandwidthUsed,
            BandwidthLimitBytes = site.MaxBandwidthBytesPerMonth
        };
    }

    public async Task UpdateStorageUsageAsync(Guid companyId, Guid siteId, long deltaBytes)
    {
        var site = await _db.Sites.FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId);
        if (site == null) return;

        site.CurrentStorageUsed = Math.Max(0, site.CurrentStorageUsed + deltaBytes);
        await _db.SaveChangesAsync();
    }
}
