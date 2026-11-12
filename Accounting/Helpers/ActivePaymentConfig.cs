using Accounting.Data;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 260-3 (ฝ่ายค้าน L4) — <b>ตัวเลือก config ผู้ให้บริการรับชำระที่ใช้จริง — ตัวเดียว (OWNER)</b>: เปิดใช้ + ไม่ถูกลบ เรียง <c>SortOrder</c> แล้ว <c>CreatedAt</c>
/// (ตัวแรก = ตัวที่ใช้) · ผู้อ่าน: <c>DocumentPayLinkService.ActiveConfigAsync</c> (ลิงก์ชำระ) · <c>PaymentIntentService</c> (สร้างรายการรับชำระ) ·
/// <see cref="PaymentChannelReadiness.GatewayLiveAsync"/> (การ์ดความพร้อม + รายการประจำ) — ห้ามเขียนเงื่อนไข/ลำดับนี้ซ้ำที่อื่น · tenant
/// </summary>
public static class ActivePaymentConfig
{
    /// <summary><paramref name="providerCode"/> null = ทุกเจ้า · ระบุ = เฉพาะเจ้านั้น</summary>
    public static IQueryable<PaymentProviderConfig> Ordered(AccountingDbContext db, Guid companyId, string? providerCode = null)
    {
        return db.PaymentProviderConfigs
            .Where(c => c.CompanyId == companyId && c.IsActive && !c.IsDeleted && (providerCode == null || c.ProviderCode == providerCode))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt);
    }
}
