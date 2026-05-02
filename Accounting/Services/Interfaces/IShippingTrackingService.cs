namespace Accounting.Services.Interfaces;

/// <summary>
/// บริการติดตามพัสดุ — รองรับ Thailand Post, Kerry, Flash, J&amp;T
/// เชื่อมต่อ API ของผู้ให้บริการขนส่งเพื่อดึงสถานะจัดส่ง
///
/// Thailand Post: https://track.thailandpost.co.th/
/// Kerry Express: https://th.kerryexpress.com/
/// Flash Express: https://www.flashexpress.co.th/
/// </summary>
public interface IShippingTrackingService
{
    Task<ShipmentTrackingResult?> TrackAsync(string trackingNumber, string? carrier = null);
    Task<List<ShipmentTrackingResult>> TrackBatchAsync(List<string> trackingNumbers);
    string? DetectCarrier(string trackingNumber);
    Task<List<ShippingRateQuote>> GetShippingRatesAsync(ShippingRateRequest request);
}

public record ShipmentTrackingResult(
    string TrackingNumber,
    string Carrier,
    string Status,
    string StatusDescription,
    DateTime? LastUpdate,
    string? CurrentLocation,
    DateTime? EstimatedDelivery,
    bool IsDelivered,
    List<TrackingEvent> Events);

public record TrackingEvent(
    DateTime Timestamp,
    string Status,
    string Description,
    string? Location);

public record ShippingRateRequest(
    string OriginPostalCode,
    string DestinationPostalCode,
    decimal WeightKg,
    decimal? LengthCm = null,
    decimal? WidthCm = null,
    decimal? HeightCm = null,
    bool IsCod = false);

public record ShippingRateQuote(
    string Carrier,
    string ServiceName,
    decimal Price,
    string EstimatedDays,
    bool IsCodAvailable);
