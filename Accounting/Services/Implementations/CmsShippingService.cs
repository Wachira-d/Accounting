using Accounting.Data;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CmsShippingService : ICmsShippingService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CmsShippingService> _logger;

    public CmsShippingService(AccountingDbContext db, ILogger<CmsShippingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ShippingZoneResponse> CreateZoneAsync(Guid companyId, Guid siteId, CreateShippingZoneRequest request, string userId)
    {
        var zone = new SiteShippingZone
        {
            CompanyId = companyId, SiteId = siteId,
            Name = request.Name, Description = request.Description,
            CountryCodes = request.CountryCodes, Provinces = request.Provinces,
            PostalCodePatterns = request.PostalCodePatterns,
            SortOrder = request.SortOrder, CreatedBy = userId
        };
        _db.SiteShippingZones.Add(zone);

        if (request.Rates?.Any() == true)
        {
            foreach (var r in request.Rates)
            {
                _db.SiteShippingRates.Add(new SiteShippingRate
                {
                    CompanyId = companyId, ZoneId = zone.Id,
                    Name = r.Name, CarrierName = r.CarrierName, RateType = r.RateType,
                    BaseRate = r.BaseRate, PerKgRate = r.PerKgRate,
                    FreeAboveAmount = r.FreeAboveAmount,
                    MinWeightKg = r.MinWeightKg, MaxWeightKg = r.MaxWeightKg,
                    EstimatedDeliveryDaysMin = r.EstimatedDeliveryDaysMin,
                    EstimatedDeliveryDaysMax = r.EstimatedDeliveryDaysMax,
                    SortOrder = r.SortOrder, CreatedBy = userId
                });
            }
        }
        await _db.SaveChangesAsync();
        return await GetZoneResponseAsync(zone.Id);
    }

    public async Task<ShippingZoneResponse> UpdateZoneAsync(Guid companyId, Guid siteId, Guid zoneId, CreateShippingZoneRequest request, string userId)
    {
        var zone = await _db.SiteShippingZones.FirstOrDefaultAsync(z => z.Id == zoneId && z.SiteId == siteId && z.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Shipping zone not found.");

        zone.Name = request.Name;
        zone.Description = request.Description;
        zone.CountryCodes = request.CountryCodes;
        zone.Provinces = request.Provinces;
        zone.PostalCodePatterns = request.PostalCodePatterns;
        zone.SortOrder = request.SortOrder;
        zone.UpdatedBy = userId;

        await _db.SaveChangesAsync();
        return await GetZoneResponseAsync(zoneId);
    }

    public async Task<List<ShippingZoneResponse>> GetZonesAsync(Guid companyId, Guid siteId)
    {
        return await _db.SiteShippingZones.AsNoTracking()
            .Where(z => z.SiteId == siteId && z.CompanyId == companyId && !z.IsDeleted)
            .OrderBy(z => z.SortOrder)
            .Select(z => new ShippingZoneResponse
            {
                Id = z.Id, Name = z.Name, Description = z.Description,
                CountryCodes = z.CountryCodes, Provinces = z.Provinces,
                PostalCodePatterns = z.PostalCodePatterns,
                IsActive = z.IsActive, SortOrder = z.SortOrder,
                // รอบ 209 P2: อัตราที่ลบแล้วไม่ขึ้นในหน้าตั้งค่า · echo น้ำหนักต่ำ/สูงสุดที่รับไว้ (กฎเหล็ก #4 A — เดิมเก็บแต่ไม่ส่งกลับ)
                Rates = z.Rates.Where(r => !r.IsDeleted).OrderBy(r => r.SortOrder).Select(r => new ShippingRateResponse
                {
                    Id = r.Id, Name = r.Name, CarrierName = r.CarrierName, RateType = r.RateType,
                    BaseRate = r.BaseRate, PerKgRate = r.PerKgRate, FreeAboveAmount = r.FreeAboveAmount,
                    MinWeightKg = r.MinWeightKg, MaxWeightKg = r.MaxWeightKg, SortOrder = r.SortOrder,
                    EstimatedDeliveryDaysMin = r.EstimatedDeliveryDaysMin,
                    EstimatedDeliveryDaysMax = r.EstimatedDeliveryDaysMax,
                    IsActive = r.IsActive
                }).ToList()
            })
            .ToListAsync();
    }

    public async Task<bool> DeleteZoneAsync(Guid companyId, Guid siteId, Guid zoneId)
    {
        var zone = await _db.SiteShippingZones.FirstOrDefaultAsync(z => z.Id == zoneId && z.SiteId == siteId && z.CompanyId == companyId);
        if (zone == null) return false;
        zone.IsActive = false;
        zone.IsDeleted = true;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<ShippingRateResponse> AddRateAsync(Guid companyId, Guid siteId, Guid zoneId, CreateShippingRateRequest request, string userId)
    {
        var zone = await _db.SiteShippingZones.FirstOrDefaultAsync(z => z.Id == zoneId && z.SiteId == siteId && z.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Shipping zone not found.");

        var rate = new SiteShippingRate
        {
            CompanyId = companyId, ZoneId = zoneId,
            Name = request.Name, CarrierName = request.CarrierName, RateType = request.RateType,
            BaseRate = request.BaseRate, PerKgRate = request.PerKgRate,
            FreeAboveAmount = request.FreeAboveAmount,
            MinWeightKg = request.MinWeightKg, MaxWeightKg = request.MaxWeightKg,
            EstimatedDeliveryDaysMin = request.EstimatedDeliveryDaysMin,
            EstimatedDeliveryDaysMax = request.EstimatedDeliveryDaysMax,
            SortOrder = request.SortOrder, CreatedBy = userId
        };
        _db.SiteShippingRates.Add(rate);
        await _db.SaveChangesAsync();
        return MapRateResponse(rate);
    }

    public async Task<ShippingRateResponse> UpdateRateAsync(Guid companyId, Guid siteId, Guid zoneId, Guid rateId, CreateShippingRateRequest request, string userId)
    {
        var rate = await _db.SiteShippingRates.Include(r => r.Zone)
            .FirstOrDefaultAsync(r => r.Id == rateId && r.ZoneId == zoneId && r.Zone.SiteId == siteId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Shipping rate not found.");

        rate.Name = request.Name;
        rate.CarrierName = request.CarrierName;
        rate.RateType = request.RateType;
        rate.BaseRate = request.BaseRate;
        rate.PerKgRate = request.PerKgRate;
        rate.FreeAboveAmount = request.FreeAboveAmount;
        rate.MinWeightKg = request.MinWeightKg;
        rate.MaxWeightKg = request.MaxWeightKg;
        rate.EstimatedDeliveryDaysMin = request.EstimatedDeliveryDaysMin;
        rate.EstimatedDeliveryDaysMax = request.EstimatedDeliveryDaysMax;
        rate.SortOrder = request.SortOrder;
        rate.UpdatedBy = userId;

        await _db.SaveChangesAsync();
        return MapRateResponse(rate);
    }

    public async Task<bool> DeleteRateAsync(Guid companyId, Guid siteId, Guid zoneId, Guid rateId)
    {
        var rate = await _db.SiteShippingRates.Include(r => r.Zone)
            .FirstOrDefaultAsync(r => r.Id == rateId && r.ZoneId == zoneId && r.Zone.SiteId == siteId && r.CompanyId == companyId);
        if (rate == null) return false;
        rate.IsActive = false;
        rate.IsDeleted = true;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<ShippingOptionResponse>> CalculateShippingOptionsAsync(Guid companyId, Guid siteId, CalculateShippingRequest request)
    {
        var zones = await _db.SiteShippingZones.AsNoTracking()
            .Include(z => z.Rates.Where(r => r.IsActive))
            .Where(z => z.SiteId == siteId && z.CompanyId == companyId && z.IsActive)
            .OrderBy(z => z.SortOrder)
            .ToListAsync();

        var options = new List<ShippingOptionResponse>();
        foreach (var zone in zones)
        {
            if (!ZoneMatchesAddress(zone, request)) continue;

            foreach (var rate in zone.Rates.OrderBy(r => r.SortOrder))
            {
                // ทีม K — ตัวคิดค่าส่งตัวเดียว (Helpers/ShippingFeeRules) ร่วมกับยอดที่ลงคำสั่งซื้อ · ปัด AwayFromZero
                // (เดิมคิดเองที่นี่ด้วย Math.Round ค่าเริ่มต้น = banker's และไม่มีใครใช้ยอดนี้ตอนสั่งจริง)
                var quote = Accounting.Helpers.ShippingFeeRules.Quote(new Accounting.Helpers.ShippingRateFacts(
                    rate.RateType, rate.BaseRate, rate.PerKgRate, rate.FreeAboveAmount, rate.MinWeightKg, rate.MaxWeightKg),
                    request.SubTotal, request.TotalWeightKg ?? 0m);
                if (!quote.Applicable) continue;

                options.Add(new ShippingOptionResponse
                {
                    RateId = rate.Id, ZoneId = zone.Id, ZoneName = zone.Name,
                    Name = rate.Name, CarrierName = rate.CarrierName,
                    Cost = quote.Fee, IsFree = quote.IsFree,
                    EstimatedDeliveryDaysMin = rate.EstimatedDeliveryDaysMin,
                    EstimatedDeliveryDaysMax = rate.EstimatedDeliveryDaysMax
                });
            }
        }

        return options.OrderBy(o => o.Cost).ToList();
    }

    /// <summary>ที่อยู่อยู่ในโซนนี้ไหม — internal ให้การคิดค่าส่งตอนสั่งซื้อ (<c>CmsCommerceService</c>) ใช้ตัวเดียวกับหน้าร้าน</summary>
    internal static bool ZoneMatchesAddress(SiteShippingZone zone, CalculateShippingRequest request)
    {
        // ฝ่ายค้านทีม K ข้อ 2: โซนที่จำกัดพื้นที่ + ที่อยู่ว่าง ⇒ <b>ไม่ตรง</b> (เดิมช่องว่างตรงทุกโซน ⇒ ไม่กรอกจังหวัดได้อัตราถูกสุดเสมอ) ·
        // ประเทศว่าง = TH (หน้าร้านขายในประเทศ) · ชื่อจังหวัดเทียบรูปมาตรฐาน (ThaiAddressParser.NormalizeProvince — กทม./กรุงเทพฯ)
        var country = string.IsNullOrWhiteSpace(request.CountryCode) ? "TH" : request.CountryCode.Trim();
        if (!string.IsNullOrEmpty(zone.CountryCodes))
        {
            var codes = zone.CountryCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (codes.Length > 0 && !codes.Contains(country, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(zone.Provinces))
        {
            var provs = zone.Provinces.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ThaiAddressParser.NormalizeProvince).Where(p => p != null).ToList();
            if (provs.Count > 0)
            {
                var want = ThaiAddressParser.NormalizeProvince(request.Province);
                if (want == null || !provs.Contains(want, StringComparer.OrdinalIgnoreCase)) return false;
            }
        }

        if (!string.IsNullOrEmpty(zone.PostalCodePatterns))
        {
            var patterns = zone.PostalCodePatterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (patterns.Length > 0)
            {
                var postal = (request.PostalCode ?? "").Trim();
                if (postal.Length == 0) return false;
                var matched = patterns.Any(p =>
                {
                    if (p.EndsWith('*'))
                        return postal.StartsWith(p.TrimEnd('*'), StringComparison.Ordinal);
                    return string.Equals(p, postal, StringComparison.OrdinalIgnoreCase);
                });
                if (!matched) return false;
            }
        }

        return true;
    }

    /// <summary>โซนนี้ต้องรู้ที่อยู่ไหม (จำกัดจังหวัด/รหัสไปรษณีย์) — ใช้บอกลูกค้าว่าต้องกรอกอะไรก่อนคิดค่าส่ง</summary>
    internal static bool ZoneNeedsAddress(SiteShippingZone zone)
        => !string.IsNullOrWhiteSpace(zone.Provinces) || !string.IsNullOrWhiteSpace(zone.PostalCodePatterns);

    private async Task<ShippingZoneResponse> GetZoneResponseAsync(Guid zoneId)
    {
        var z = await _db.SiteShippingZones.AsNoTracking()
            .Include(x => x.Rates)
            .FirstAsync(x => x.Id == zoneId);
        return new ShippingZoneResponse
        {
            Id = z.Id, Name = z.Name, Description = z.Description,
            CountryCodes = z.CountryCodes, Provinces = z.Provinces,
            PostalCodePatterns = z.PostalCodePatterns,
            IsActive = z.IsActive, SortOrder = z.SortOrder,
            Rates = z.Rates.Where(r => !r.IsDeleted).OrderBy(r => r.SortOrder).Select(MapRateResponse).ToList()
        };
    }

    private static ShippingRateResponse MapRateResponse(SiteShippingRate r) => new()
    {
        Id = r.Id, Name = r.Name, CarrierName = r.CarrierName, RateType = r.RateType,
        BaseRate = r.BaseRate, PerKgRate = r.PerKgRate, FreeAboveAmount = r.FreeAboveAmount,
        MinWeightKg = r.MinWeightKg, MaxWeightKg = r.MaxWeightKg, SortOrder = r.SortOrder,
        EstimatedDeliveryDaysMin = r.EstimatedDeliveryDaysMin,
        EstimatedDeliveryDaysMax = r.EstimatedDeliveryDaysMax,
        IsActive = r.IsActive
    };
}
