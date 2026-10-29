using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 · P1 (หน้าร้านฝั่งลูกค้า) — ส่วนที่ <c>CreateOrderAsync</c> เรียก: คีย์กันกดสั่งซ้ำ (A9) · ลูกค้าของออเดอร์ · โทเคนสาธารณะ (A1) · ผู้ขนส่ง (B4) ·
/// ตัวตัดสินทั้งหมดอยู่ที่ <see cref="SiteOrderPublic"/> / <see cref="StorefrontCustomerAuth"/> — ไฟล์นี้ดึงข้อมูล (กรอง CompanyId) แล้วส่งเข้าตัวตัดสิน
/// </summary>
public partial class CmsCommerceService
{
    /// <summary>A9: คีย์ซ้ำ ⇒ ออเดอร์เดิมของเว็บนี้ (คืนให้ผู้เรียกแทนการสร้างใหม่) · ไม่มีคีย์/ไม่เคยใช้ ⇒ null</summary>
    private async Task<Guid?> FindIdempotentOrderIdAsync(Guid companyId, Guid siteId, string? key)
    {
        if (key == null) return null;
        return await _db.SiteOrders.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.SiteId == siteId && o.IdempotencyKey == key)
            .Select(o => (Guid?)o.Id).FirstOrDefaultAsync();
    }

    /// <summary>ลูกค้าของออเดอร์ (controller ตัดสินแล้วด้วย <see cref="StorefrontCustomerAuth.OrderCustomerId"/>) — ยืนยันว่าเป็นลูกค้าของบริษัท+เว็บนี้จริง ·
    /// ไม่ใช่ ⇒ null (ออเดอร์แขก — ไม่ผูกเข้าบัญชีของเว็บอื่น/บริษัทอื่น)</summary>
    private async Task<Guid?> VerifiedOrderCustomerAsync(Guid companyId, Guid siteId, Guid? customerId)
    {
        if (customerId is not Guid cid) return null;
        var ok = await _db.SiteCustomers.AsNoTracking().AnyAsync(c => c.Id == cid && c.CompanyId == companyId && c.SiteId == siteId);
        return ok ? cid : null;
    }

    /// <summary>B4: ผู้ขนส่งจากอัตราที่เลือก (กรอง CompanyId) — มารับเอง/ไม่มีอัตรา ⇒ null</summary>
    private async Task<string?> CarrierOfRateAsync(Guid companyId, Guid? rateId, string? fulfillmentMethod)
    {
        if (rateId is not Guid rid || fulfillmentMethod == FulfillmentMethod.Pickup) return null;
        var name = await _db.Set<SiteShippingRate>().AsNoTracking()
            .Where(r => r.Id == rid && r.CompanyId == companyId)
            .Select(r => r.CarrierName).FirstOrDefaultAsync();
        return SiteOrderPublic.CarrierAtOrder(fulfillmentMethod, name);
    }

    /// <summary>A9: บันทึกครั้งแรกของออเดอร์ใหม่ — คำขอซ้อนที่ใช้คีย์เดียวกันชน unique index ⇒ ถอยของครึ่งทางออกจาก context แล้วคืนออเดอร์ของคำขอแรก ·
    /// ชนอย่างอื่น ⇒ โยนตามเดิม (ไม่กลืน)</summary>
    private async Task<Guid?> SaveNewOrderOrReplayAsync(Guid companyId, Guid siteId, SiteOrder order)
    {
        try
        {
            await _db.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateException) when (order.IdempotencyKey != null)
        {
            _db.ChangeTracker.Clear();
            var existing = await FindIdempotentOrderIdAsync(companyId, siteId, order.IdempotencyKey);
            if (existing == null) throw;
            _logger.LogInformation("Order create replayed by idempotency key on site {SiteId} → {OrderId}", siteId, existing);
            return existing;
        }
    }
}
