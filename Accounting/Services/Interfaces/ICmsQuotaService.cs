namespace Accounting.Services.Interfaces;

public interface ICmsQuotaService
{
    Task<bool> CanAddPageAsync(Guid companyId, Guid siteId);
    Task<bool> CanAddProductAsync(Guid companyId, Guid siteId);
    Task<bool> CanUploadMediaAsync(Guid companyId, Guid siteId, long fileSizeBytes);
    Task<CmsQuotaStatus> GetQuotaStatusAsync(Guid companyId, Guid siteId);
    Task UpdateStorageUsageAsync(Guid companyId, Guid siteId, long deltaBytes);

    /// <summary>
    /// การใช้โควตา "หน้าเว็บ" ของเว็บนี้ (ใช้ไป/เพดาน) — ตัวนับตัวเดียวของทุกเส้น
    /// (สร้างหน้าเดี่ยว · เติมเทมเพลต · หน้าสรุปโควตา) ห้ามนับเองที่จุดเรียก
    /// </summary>
    Task<CmsQuotaUsage> PageUsageAsync(Guid companyId, Guid siteId);

    /// <summary>การใช้โควตา "สินค้าบนเว็บ" — ดู <see cref="PageUsageAsync"/></summary>
    Task<CmsQuotaUsage> ProductUsageAsync(Guid companyId, Guid siteId);
}

/// <summary>
/// ผลการนับโควตาหนึ่งชนิด — <c>Limit == null</c> แปลว่าไม่จำกัด
///
/// เหตุผลที่ต้องมีชนิดนี้: เดิมโควตาถูก **แสดง** อย่างเดียว
/// (<c>GetQuotaStatusAsync</c>) ส่วน <c>CanAddPageAsync</c>/<c>CanAddProductAsync</c>
/// **ไม่มีใครเรียกเลยทั้งเรพ** ⇒ เพดาน <c>MaxPages</c>/<c>MaxProducts</c> ของแพ็กเกจ
/// ที่โชว์อยู่บนหน้าจอไม่เคยกั้นอะไร (defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้")
/// ตัวนี้ให้ทั้งด่าน (<see cref="CanAdd"/>) และจำนวนที่เหลือ (<see cref="Remaining"/>)
/// สำหรับเส้นที่ต้อง **ตัดให้พอดีโควตา** แทนการโยน (เช่น auto-publish ตอนสร้างเว็บ)
/// </summary>
public sealed class CmsQuotaUsage
{
    public bool SiteFound { get; init; }
    public int Used { get; init; }
    public int? Limit { get; init; }

    /// <summary>เพิ่มได้อีกกี่รายการ — null = ไม่จำกัด</summary>
    public int? Remaining => Limit == null ? null : Math.Max(0, Limit.Value - Used);

    public bool CanAdd(int adding = 1)
        => SiteFound && (Limit == null || Used + adding <= Limit.Value);

    /// <summary>
    /// คืน null เมื่อเพิ่มได้ · คืน **ข้อความไทยพร้อมทางไปต่อ** เมื่อเต็ม —
    /// ด่านที่ปฏิเสธต้องบอกเสมอว่าผู้ใช้ทำอะไรได้แทน (กฎ "สถานะปลายทางที่ไปต่อไม่ได้")
    /// </summary>
    public string? BlockReason(string what, int adding = 1)
    {
        if (!SiteFound) return "ไม่พบเว็บไซต์";
        if (CanAdd(adding)) return null;
        var remain = Remaining ?? 0;
        return $"เพิ่ม{what}ไม่ได้ — โควตาของแพ็กเกจนี้เต็มแล้ว "
             + $"(ใช้ไป {Used}/{Limit} · เพิ่มได้อีก {remain} · ต้องการ {adding}) "
             + $"กรุณาลบ{what}ที่ไม่ใช้ หรืออัปเกรดแพ็กเกจ";
    }
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
