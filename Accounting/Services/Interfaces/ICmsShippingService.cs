using Accounting.Models.DTOs.Cms;

namespace Accounting.Services.Interfaces;

public interface ICmsShippingService
{
    Task<ShippingZoneResponse> CreateZoneAsync(Guid companyId, Guid siteId, CreateShippingZoneRequest request, string userId);
    Task<ShippingZoneResponse> UpdateZoneAsync(Guid companyId, Guid siteId, Guid zoneId, CreateShippingZoneRequest request, string userId);
    Task<List<ShippingZoneResponse>> GetZonesAsync(Guid companyId, Guid siteId);
    Task<bool> DeleteZoneAsync(Guid companyId, Guid siteId, Guid zoneId);

    Task<ShippingRateResponse> AddRateAsync(Guid companyId, Guid siteId, Guid zoneId, CreateShippingRateRequest request, string userId);
    Task<ShippingRateResponse> UpdateRateAsync(Guid companyId, Guid siteId, Guid zoneId, Guid rateId, CreateShippingRateRequest request, string userId);
    Task<bool> DeleteRateAsync(Guid companyId, Guid siteId, Guid zoneId, Guid rateId);

    Task<List<ShippingOptionResponse>> CalculateShippingOptionsAsync(Guid companyId, Guid siteId, CalculateShippingRequest request);
}
