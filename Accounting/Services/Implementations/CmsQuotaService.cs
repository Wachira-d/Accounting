using Accounting.Data;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CmsQuotaService : ICmsQuotaService
{
    private readonly AccountingDbContext _db;
    /// <summary>รอบ 229 (QUOTA_ADDON แพ็ก 2 · §C): เพดานต่อเว็บจากแพ็กเกจ/override เมื่อเว็บไม่ได้ตั้งเอง (<c>Site.MaxPages/MaxProducts</c> = override รายเว็บ ชนะ) ·
    /// null (เทสต์เดิม) = พฤติกรรมเดิม (ไม่ตั้งที่เว็บ = ไม่จำกัด)</summary>
    private readonly IQuotaGate? _gate;

    public CmsQuotaService(AccountingDbContext db, IQuotaGate? gate = null)
    {
        _db = db;
        _gate = gate;
    }

    /// <summary>เพดานต่อเว็บที่มีผล: ค่าที่ตั้งบนเว็บ (override รายเว็บ) ชนะ ⇒ เพดานแพ็กเกจ/รายบริษัท ⇒ ไม่จำกัด</summary>
    private async Task<int?> PerSiteLimitAsync(Guid companyId, int? siteOverride, string resourceKey)
    {
        if (siteOverride != null || _gate == null) return siteOverride;
        return (await _gate.LimitAsync(companyId, resourceKey)).Effective;
    }

    // ── โควตาหน้า/สินค้า ────────────────────────────────────────────────
    // สูตรอยู่ที่ `PageUsageAsync`/`ProductUsageAsync` ที่เดียว · ด่าน (CanAdd*),
    // ข้อความปฏิเสธ (BlockReason) และจำนวนที่เหลือ (Remaining) ล้วนคำนวณจาก
    // ผลนั้น — ห้ามมีจุดไหนนับเอง (เดิม GetQuotaStatusAsync นับซ้ำอีกชุด)
    //
    // หมายเหตุ: `SitePage` มี global query filter `!IsDeleted` อยู่แล้ว
    // (AccountingDbContext.cs) ⇒ หน้าที่ลูกค้าลบทิ้งไม่กินโควตา ซึ่งถูกต้อง

    public async Task<CmsQuotaUsage> PageUsageAsync(Guid companyId, Guid siteId)
    {
        var site = await _db.Sites
            .AsNoTracking()
            .Where(s => s.Id == siteId && s.CompanyId == companyId)
            .Select(s => new { s.MaxPages })
            .FirstOrDefaultAsync();

        if (site == null) return new CmsQuotaUsage { SiteFound = false, Used = 0, Limit = 0 };

        var used = await _db.SitePages
            .CountAsync(p => p.SiteId == siteId && p.CompanyId == companyId);

        return new CmsQuotaUsage { SiteFound = true, Used = used,
            Limit = await PerSiteLimitAsync(companyId, site.MaxPages, Accounting.Models.Constants.QuotaResources.CmsPagesPerSite) };
    }

    public async Task<CmsQuotaUsage> ProductUsageAsync(Guid companyId, Guid siteId)
    {
        var site = await _db.Sites
            .AsNoTracking()
            .Where(s => s.Id == siteId && s.CompanyId == companyId)
            .Select(s => new { s.MaxProducts })
            .FirstOrDefaultAsync();

        if (site == null) return new CmsQuotaUsage { SiteFound = false, Used = 0, Limit = 0 };

        var used = await _db.SiteProducts
            .CountAsync(p => p.SiteId == siteId && p.CompanyId == companyId);

        return new CmsQuotaUsage { SiteFound = true, Used = used,
            Limit = await PerSiteLimitAsync(companyId, site.MaxProducts, Accounting.Models.Constants.QuotaResources.CmsProductsPerSite) };
    }

    public async Task<bool> CanAddPageAsync(Guid companyId, Guid siteId)
        => (await PageUsageAsync(companyId, siteId)).CanAdd();

    public async Task<bool> CanAddProductAsync(Guid companyId, Guid siteId)
        => (await ProductUsageAsync(companyId, siteId)).CanAdd();

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

        // ตัวเลขที่ผู้ใช้เห็น ต้องมาจากตัวนับเดียวกับที่ด่านใช้ — ไม่งั้นหน้าจอบอก
        // "ยังเหลือ" แต่กดแล้วถูกปฏิเสธ (หรือกลับกัน) ซึ่งเป็น drift ที่หาสาเหตุยาก
        var pageUsage = await PageUsageAsync(companyId, siteId);
        var productUsage = await ProductUsageAsync(companyId, siteId);

        return new CmsQuotaStatus
        {
            StorageUsedBytes = site.CurrentStorageUsed,
            StorageLimitBytes = site.MaxStorageBytes,
            PagesCount = pageUsage.Used,
            PagesLimit = pageUsage.Limit,
            ProductsCount = productUsage.Used,
            ProductsLimit = productUsage.Limit,
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
