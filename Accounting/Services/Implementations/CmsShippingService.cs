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
            .Where(z => z.SiteId == siteId && z.CompanyId == companyId)
            .OrderBy(z => z.SortOrder)
            .Select(z => new ShippingZoneResponse
            {
                Id = z.Id, Name = z.Name, Description = z.Description,
                CountryCodes = z.CountryCodes, Provinces = z.Provinces,
                PostalCodePatterns = z.PostalCodePatterns,
                IsActive = z.IsActive, SortOrder = z.SortOrder,
                Rates = z.Rates.OrderBy(r => r.SortOrder).Select(r => new ShippingRateResponse
                {
                    Id = r.Id, Name = r.Name, CarrierName = r.CarrierName, RateType = r.RateType,
                    BaseRate = r.BaseRate, PerKgRate = r.PerKgRate, FreeAboveAmount = r.FreeAboveAmount,
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
        var zone = await _db.SiteShippingZones.FirstOrDefaultAsync(z => z.Id == zoneId && z.SiteId == siteId)
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
            .FirstOrDefaultAsync(r => r.Id == rateId && r.ZoneId == zoneId && r.Zone.SiteId == siteId)
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
            .FirstOrDefaultAsync(r => r.Id == rateId && r.ZoneId == zoneId && r.Zone.SiteId == siteId);
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
                var weight = request.TotalWeightKg ?? 0;
                if (rate.MinWeightKg.HasValue && weight < rate.MinWeightKg.Value) continue;
                if (rate.MaxWeightKg.HasValue && weight > rate.MaxWeightKg.Value) continue;

                bool isFree = rate.RateType == ShippingRateType.Free
                    || (rate.FreeAboveAmount.HasValue && request.SubTotal >= rate.FreeAboveAmount.Value);

                decimal cost = 0m;
                if (!isFree)
                {
                    cost = rate.RateType switch
                    {
                        ShippingRateType.Flat => rate.BaseRate,
                        ShippingRateType.ByWeight => rate.BaseRate + (rate.PerKgRate ?? 0) * weight,
                        ShippingRateType.Calculated => rate.BaseRate,
                        _ => rate.BaseRate
                    };
                }

                options.Add(new ShippingOptionResponse
                {
                    RateId = rate.Id, ZoneId = zone.Id, ZoneName = zone.Name,
                    Name = rate.Name, CarrierName = rate.CarrierName,
                    Cost = Math.Round(cost, 2), IsFree = isFree,
                    EstimatedDeliveryDaysMin = rate.EstimatedDeliveryDaysMin,
                    EstimatedDeliveryDaysMax = rate.EstimatedDeliveryDaysMax
                });
            }
        }

        return options.OrderBy(o => o.Cost).ToList();
    }

    private static bool ZoneMatchesAddress(SiteShippingZone zone, CalculateShippingRequest request)
    {
        if (!string.IsNullOrEmpty(zone.CountryCodes) && !string.IsNullOrEmpty(request.CountryCode))
        {
            var codes = zone.CountryCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (codes.Length > 0 && !codes.Contains(request.CountryCode, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(zone.Provinces) && !string.IsNullOrEmpty(request.Province))
        {
            var provs = zone.Provinces.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (provs.Length > 0 && !provs.Contains(request.Province, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(zone.PostalCodePatterns) && !string.IsNullOrEmpty(request.PostalCode))
        {
            var patterns = zone.PostalCodePatterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (patterns.Length > 0)
            {
                var matched = patterns.Any(p =>
                {
                    if (p.EndsWith('*'))
                        return request.PostalCode.StartsWith(p.TrimEnd('*'));
                    return string.Equals(p, request.PostalCode, StringComparison.OrdinalIgnoreCase);
                });
                if (!matched) return false;
            }
        }

        return true;
    }

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
            Rates = z.Rates.OrderBy(r => r.SortOrder).Select(MapRateResponse).ToList()
        };
    }

    private static ShippingRateResponse MapRateResponse(SiteShippingRate r) => new()
    {
        Id = r.Id, Name = r.Name, CarrierName = r.CarrierName, RateType = r.RateType,
        BaseRate = r.BaseRate, PerKgRate = r.PerKgRate, FreeAboveAmount = r.FreeAboveAmount,
        EstimatedDeliveryDaysMin = r.EstimatedDeliveryDaysMin,
        EstimatedDeliveryDaysMax = r.EstimatedDeliveryDaysMax,
        IsActive = r.IsActive
    };
}
