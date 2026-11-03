using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Services.Implementations;

namespace Accounting.Helpers;

/// <summary>รอบ 229 (QUOTA_ADDON §B.4 · แพ็ก 5): เพดานพื้นที่ที่มีผล = ช่องเดิมของแพ็กเกจ/License → override รายบริษัท (GB · -1 = ไม่จำกัด) ชนะ →
/// + GB ที่บริษัทนี้ซื้อและยังมีผล · ผู้เรียกเดียว <c>SubscriptionService.GetStorageStatusAsync</c> (ตัวตัดสินเดิมของด่านสื่อ CMS + คำเตือนไฟล์แนบ)</summary>
public static class QuotaStorageLimit
{
    /// <summary>pure — ใช้ทั้งเทสต์และ <see cref="ApplyAsync"/></summary>
    public static long Compute(long planMaxBytes, int? overrideGb, int purchasedGb)
    {
        var max = overrideGb is int g ? (g < 0 ? QuotaResources.UnlimitedBytes : g * QuotaResources.BytesPerGb) : planMaxBytes;
        if (max >= QuotaResources.UnlimitedBytes) return QuotaResources.UnlimitedBytes;
        if (purchasedGb > 0) max += purchasedGb * QuotaResources.BytesPerGb;
        return max;
    }

    public static async Task<long> ApplyAsync(AccountingDbContext db, Guid companyId, long planMaxBytes, CancellationToken ct = default)
        => Compute(planMaxBytes,
            await QuotaStore.OverrideAsync(db, companyId, QuotaResources.StorageGb, ct),
            await QuotaStore.LiveUnitsAsync(db, companyId, QuotaResources.StorageGb, DateTime.UtcNow, ct));
}
