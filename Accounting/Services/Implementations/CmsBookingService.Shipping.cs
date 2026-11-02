using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 228 (<c>erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md</c> แพ็ก C · §1 ข้อ 3) — กลุ่ม "การจัดส่ง / รับเอง" ของศูนย์ตั้งค่าเว็บ:
/// ค่าส่งเหมาจ่าย · ส่งฟรีเมื่อครบ · ใช้ส่งฟรีกับโซนด้วย (ย้ายจาก ตั้งค่าร้านค้า › การสั่งซื้อ — ที่เดียว) + จำนวนโซน + คำเตือน "ส่งฟรีเงียบ"
/// (คำตัดสิน Q3: เตือนค้างจนเจ้าของกด "ส่งฟรีจริง") — ใช้ได้ทั้งเว็บคำสั่งซื้อและเว็บการจอง (เดิมเว็บจองหาที่ตั้งค่าส่งไม่เจอ)
/// </summary>
public partial class CmsBookingService
{
    /// <summary>คำเตือนส่งฟรีเงียบของเว็บ (ตัวตัดสิน <see cref="ShopDeliveryFee.FreeByOmissionWarning"/>) — จัดส่งเปิด = คำสั่งซื้อเปิดจัดส่ง (ร้านที่มีสินค้า) หรือการจองเปิดจัดส่ง</summary>
    private async Task<(string? Warning, bool OrdersDelivery, bool BookingDelivery, int ZoneCount, SiteCommerceConfig? Cfg)> FreeDeliveryStateAsync(Guid companyId, Guid siteId)
    {
        var cfg = await _db.SiteCommerceConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId);
        var zoneCount = (await ShopDeliveryFee.LoadZonesAsync(_db, companyId, siteId)).Count;
        var hasProducts = await _db.SiteProducts.AnyAsync(p => p.CompanyId == companyId && p.SiteId == siteId && !p.IsDeleted);
        var ordersDelivery = hasProducts && (cfg?.EnableShipping ?? true);
        var row = await _db.SiteBookingSettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        var services = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId && s.IsActive && !s.IsDeleted).ToListAsync();
        var bookingDelivery = services.Any(s => FulfillmentOf(s, row).Delivery);
        return (ShopDeliveryFee.FreeByOmissionWarning(ordersDelivery || bookingDelivery, zoneCount, cfg?.FlatShippingRate, cfg?.FreeDeliveryConfirmed ?? false),
            ordersDelivery, bookingDelivery, zoneCount, cfg);
    }

    public async Task<SiteShippingSettingsDto> GetShippingSettingsAsync(Guid companyId, Guid siteId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var st = await FreeDeliveryStateAsync(companyId, siteId);
        return new SiteShippingSettingsDto
        {
            FlatShippingRate = st.Cfg?.FlatShippingRate, FreeShippingThreshold = st.Cfg?.FreeShippingThreshold,
            FreeShippingOverridesZones = st.Cfg?.FreeShippingOverridesZones ?? false,
            FreeDeliveryConfirmed = st.Cfg?.FreeDeliveryConfirmed ?? false,
            ZoneCount = st.ZoneCount, OrdersDelivery = st.OrdersDelivery, BookingDelivery = st.BookingDelivery,
            FreeDeliveryWarning = st.Warning,
            ShippingRevenueAccountLabel = await ShippingAccountLabelAsync(companyId, siteId),
        };
    }

    public async Task<SiteShippingSettingsDto> UpdateShippingSettingsAsync(Guid companyId, Guid siteId, SiteShippingSettingsDto request, string userId)
    {
        await EnsureSiteAsync(companyId, siteId);
        if (request.FlatShippingRate is < 0m || request.FreeShippingThreshold is < 0m)
            throw new BusinessRuleException("ค่าส่งเหมาจ่าย/ยอดส่งฟรีต้องไม่ติดลบ", ShopDeliveryFee.RuleCode);
        if (request.FlatShippingRate is decimal f && decimal.Round(f, 2) != f || request.FreeShippingThreshold is decimal t && decimal.Round(t, 2) != t)
            throw new BusinessRuleException("ค่าส่ง/ยอดส่งฟรีทศนิยมไม่เกิน 2 ตำแหน่ง", ShopDeliveryFee.RuleCode);
        var cfg = await _db.SiteCommerceConfigs.FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId);
        if (cfg == null)
        {
            cfg = new SiteCommerceConfig { CompanyId = companyId, SiteId = siteId };
            _db.SiteCommerceConfigs.Add(cfg);
        }
        // ค่าว่าง = ล้าง (หน้าเดียวที่แก้ค่าชุดนี้ — ส่งครบทุกช่องเสมอ)
        cfg.FlatShippingRate = request.FlatShippingRate is > 0m ? request.FlatShippingRate : null;
        cfg.FreeShippingThreshold = request.FreeShippingThreshold is > 0m ? request.FreeShippingThreshold : null;
        cfg.FreeShippingOverridesZones = request.FreeShippingOverridesZones;
        // คำตัดสิน Q3: ตั้งค่าส่งเหมาจ่ายแล้ว ⇒ การยืนยันส่งฟรีเดิมหมดความหมาย (ล้างค่าส่งอีกครั้งต้องยืนยันใหม่)
        cfg.FreeDeliveryConfirmed = cfg.FlatShippingRate == null && request.FreeDeliveryConfirmed;
        await _db.SaveChangesAsync();
        _logger.LogInformation("บันทึกค่าส่งของเว็บ {SiteId} โดย {User}", siteId, userId);
        return await GetShippingSettingsAsync(companyId, siteId);
    }
}
