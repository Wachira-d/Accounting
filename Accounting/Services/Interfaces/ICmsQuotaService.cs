namespace Accounting.Services.Interfaces;

public interface ICmsQuotaService
{
    Task<bool> CanAddPageAsync(Guid companyId, Guid siteId);
    Task<bool> CanAddProductAsync(Guid companyId, Guid siteId);
    Task<bool> CanUploadMediaAsync(Guid companyId, Guid siteId, long fileSizeBytes);
    Task<CmsQuotaStatus> GetQuotaStatusAsync(Guid companyId, Guid siteId);
    Task UpdateStorageUsageAsync(Guid companyId, Guid siteId, long deltaBytes);
}

public class CmsQuotaStatus
{
    public long StorageUsedBytes { get; set; }
    public long? StorageLimitBytes { get; set; }
    public int PagesCount { get; set; }
    public int? PagesLimit { get; set; }
    public int ProductsCount { get; set; }
    public int? ProductsLimit { get; set; }
    public long BandwidthUsedBytes { get; set; }
    public long? BandwidthLimitBytes { get; set; }
    public double StoragePercentUsed => StorageLimitBytes > 0 ? (double)StorageUsedBytes / StorageLimitBytes.Value * 100 : 0;
    public double PagesPercentUsed => PagesLimit > 0 ? (double)PagesCount / PagesLimit.Value * 100 : 0;
    public double ProductsPercentUsed => ProductsLimit > 0 ? (double)ProductsCount / ProductsLimit.Value * 100 : 0;
}
