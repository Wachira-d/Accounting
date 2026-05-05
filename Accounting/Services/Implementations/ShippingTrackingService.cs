using System.Net.Http.Json;
using System.Text.Json;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

/// <summary>
/// ติดตามพัสดุจากหลายผู้ให้บริการ:
///
/// 1. Thailand Post — track.thailandpost.co.th API
///    ต้องลงทะเบียน API key ที่ track.thailandpost.co.th/developer
///    Free: 1,000 requests/day
///
/// 2. Kerry Express — th.kerryexpress.com/th/track
///
/// 3. Flash Express — flashexpress.co.th tracking API
///
/// 4. J&amp;T Express — jtexpress.co.th
///
/// ระบบจะ auto-detect carrier จากรูปแบบ tracking number
/// </summary>
public class ShippingTrackingService : IShippingTrackingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ShippingTrackingService> _logger;

    public ShippingTrackingService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<ShippingTrackingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<ShipmentTrackingResult?> TrackAsync(string trackingNumber, string? carrier = null)
    {
        carrier ??= DetectCarrier(trackingNumber);
        if (string.IsNullOrEmpty(carrier)) carrier = "ThailandPost";

        return carrier.ToLowerInvariant() switch
        {
            "thailandpost" or "thpost" or "ems" => await TrackThailandPostAsync(trackingNumber),
            "kerry" or "kerryexpress" => await TrackKerryAsync(trackingNumber),
            "flash" or "flashexpress" => await TrackFlashAsync(trackingNumber),
            "jt" or "jtexpress" or "j&t" => await TrackJtAsync(trackingNumber),
            _ => await TrackThailandPostAsync(trackingNumber)
        };
    }

    public async Task<List<ShipmentTrackingResult>> TrackBatchAsync(List<string> trackingNumbers)
    {
        var tasks = trackingNumbers.Select(t => TrackAsync(t));
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null).Cast<ShipmentTrackingResult>().ToList();
    }

    public string? DetectCarrier(string trackingNumber)
    {
        if (string.IsNullOrWhiteSpace(trackingNumber)) return null;
        var t = trackingNumber.Trim().ToUpperInvariant();

        // Thailand Post: EMS = E + 9 digits + TH, Registered = R + 9 digits + TH
        if (t.Length == 13 && t.EndsWith("TH") && (t[0] == 'E' || t[0] == 'R' || t[0] == 'C' || t[0] == 'P'))
            return "ThailandPost";

        // Thailand Post domestic: starts with TH or numeric 10+ digits
        if (t.StartsWith("TH") || (t.Length >= 10 && t.All(char.IsDigit)))
            return "ThailandPost";

        // Kerry Express: SMKD or KEX prefix
        if (t.StartsWith("SMKD") || t.StartsWith("KEX"))
            return "Kerry";

        // Flash Express: TH prefix + 15 alphanumeric
        if (t.StartsWith("TH") && t.Length >= 14)
            return "Flash";

        // J&T Express: 8 + 11 digits or JT prefix
        if (t.StartsWith("8") && t.Length == 12 && t.All(char.IsDigit))
            return "JT";
        if (t.StartsWith("JT"))
            return "JT";

        return null;
    }

    public Task<List<ShippingRateQuote>> GetShippingRatesAsync(ShippingRateRequest request)
    {
        var quotes = new List<ShippingRateQuote>();

        // Thailand Post EMS rates (approximate, based on weight zones)
        var thpPrice = request.WeightKg switch
        {
            <= 0.5m => 37m,
            <= 1.0m => 52m,
            <= 2.0m => 72m,
            <= 3.0m => 92m,
            <= 5.0m => 132m,
            <= 10.0m => 232m,
            <= 15.0m => 332m,
            <= 20.0m => 432m,
            _ => 432m + (Math.Ceiling(request.WeightKg - 20) * 20)
        };

        quotes.Add(new ShippingRateQuote("ThailandPost", "EMS", thpPrice, "1-3 วัน", false));
        quotes.Add(new ShippingRateQuote("ThailandPost", "ลงทะเบียน", thpPrice * 0.7m, "3-5 วัน", false));

        // Kerry Express rates
        var kerryPrice = request.WeightKg switch
        {
            <= 1.0m => 50m,
            <= 3.0m => 70m,
            <= 5.0m => 90m,
            <= 10.0m => 130m,
            <= 15.0m => 180m,
            <= 20.0m => 230m,
            _ => 230m + (Math.Ceiling(request.WeightKg - 20) * 15)
        };
        quotes.Add(new ShippingRateQuote("Kerry", "Express", kerryPrice, "1-2 วัน", true));

        // Flash Express rates
        var flashPrice = request.WeightKg switch
        {
            <= 2.0m => 30m,
            <= 5.0m => 45m,
            <= 10.0m => 70m,
            <= 15.0m => 100m,
            <= 20.0m => 140m,
            _ => 140m + (Math.Ceiling(request.WeightKg - 20) * 10)
        };
        quotes.Add(new ShippingRateQuote("Flash", "Flash Express", flashPrice, "1-3 วัน", true));

        // J&T Express rates
        var jtPrice = request.WeightKg switch
        {
            <= 2.0m => 35m,
            <= 5.0m => 50m,
            <= 10.0m => 80m,
            <= 15.0m => 110m,
            <= 20.0m => 150m,
            _ => 150m + (Math.Ceiling(request.WeightKg - 20) * 12)
        };
        quotes.Add(new ShippingRateQuote("J&T", "J&T Express", jtPrice, "2-4 วัน", true));

        return Task.FromResult(quotes);
    }

    // ============================================================
    // Carrier-specific tracking implementations
    // ============================================================

    private async Task<ShipmentTrackingResult?> TrackThailandPostAsync(string trackingNumber)
    {
        try
        {
            var apiKey = _config["Shipping:ThailandPost:ApiKey"];
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("Thailand Post API key not configured. Register at track.thailandpost.co.th/developer");
                return CreatePendingResult(trackingNumber, "ThailandPost");
            }

            var client = _httpClientFactory.CreateClient();

            // Step 1: Get token
            client.DefaultRequestHeaders.Add("Authorization", $"Token {apiKey}");
            var tokenResponse = await client.PostAsync(
                "https://trackapi.thailandpost.co.th/post/api/v1/authenticate/token", null);

            if (!tokenResponse.IsSuccessStatusCode)
                return CreatePendingResult(trackingNumber, "ThailandPost");

            var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
            var token = tokenJson.TryGetProperty("token", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(token))
                return CreatePendingResult(trackingNumber, "ThailandPost");

            // Step 2: Track item
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"Token {token}");

            var trackPayload = new { status = "all", language = "TH", barcode = new[] { trackingNumber } };
            var trackContent = new StringContent(
                JsonSerializer.Serialize(trackPayload), System.Text.Encoding.UTF8, "application/json");

            var trackResponse = await client.PostAsync(
                "https://trackapi.thailandpost.co.th/post/api/v1/track", trackContent);

            if (!trackResponse.IsSuccessStatusCode)
                return CreatePendingResult(trackingNumber, "ThailandPost");

            var trackJson = await trackResponse.Content.ReadFromJsonAsync<JsonElement>();
            return ParseThailandPostResponse(trackingNumber, trackJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thailand Post tracking failed for {Tracking}", trackingNumber);
            return CreatePendingResult(trackingNumber, "ThailandPost");
        }
    }

    private Task<ShipmentTrackingResult?> TrackKerryAsync(string trackingNumber)
    {
        _logger.LogInformation("Kerry tracking for {Tracking} — requires official API partnership", trackingNumber);
        return Task.FromResult<ShipmentTrackingResult?>(CreatePendingResult(trackingNumber, "Kerry"));
    }

    private async Task<ShipmentTrackingResult?> TrackFlashAsync(string trackingNumber)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var apiKey = _config["Shipping:Flash:ApiKey"];

            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("Flash Express API key not configured");
                return CreatePendingResult(trackingNumber, "Flash");
            }

            var url = $"https://restapi.flashexpress.com/api/v3/tracking?trackingNo={Uri.EscapeDataString(trackingNumber)}";
            client.DefaultRequestHeaders.Add("api-key", apiKey);

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return CreatePendingResult(trackingNumber, "Flash");

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return ParseFlashResponse(trackingNumber, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flash tracking failed for {Tracking}", trackingNumber);
            return CreatePendingResult(trackingNumber, "Flash");
        }
    }

    private Task<ShipmentTrackingResult?> TrackJtAsync(string trackingNumber)
    {
        _logger.LogInformation("J&T tracking for {Tracking} — requires official API partnership", trackingNumber);
        return Task.FromResult<ShipmentTrackingResult?>(CreatePendingResult(trackingNumber, "J&T"));
    }

    // ============================================================
    // Response Parsers
    // ============================================================

    private ShipmentTrackingResult? ParseThailandPostResponse(string trackingNumber, JsonElement json)
    {
        var events = new List<TrackingEvent>();
        var isDelivered = false;
        DateTime? lastUpdate = null;
        string? currentLocation = null;
        string status = "Pending";

        if (json.TryGetProperty("response", out var resp) &&
            resp.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateObject())
            {
                if (!item.Value.TryGetProperty("status", out var statusArr)) continue;

                foreach (var s in statusArr.EnumerateArray())
                {
                    var dateStr = s.TryGetProperty("status_date", out var d) ? d.GetString() : null;
                    DateTime.TryParse(dateStr, out var date);

                    var desc = s.TryGetProperty("status_description", out var sd) ? sd.GetString() ?? "" : "";
                    var loc = s.TryGetProperty("location", out var l) ? l.GetString() : null;
                    var code = s.TryGetProperty("status", out var sc) ? sc.GetString() : null;

                    events.Add(new TrackingEvent(date, code ?? "", desc, loc));

                    if (date > (lastUpdate ?? DateTime.MinValue))
                    {
                        lastUpdate = date;
                        currentLocation = loc;
                        status = desc;
                    }

                    if (code == "501") isDelivered = true;
                }
            }
        }

        return new ShipmentTrackingResult(
            trackingNumber, "ThailandPost", status, status,
            lastUpdate, currentLocation, null, isDelivered,
            events.OrderByDescending(e => e.Timestamp).ToList());
    }

    private ShipmentTrackingResult? ParseFlashResponse(string trackingNumber, JsonElement json)
    {
        var events = new List<TrackingEvent>();

        if (json.TryGetProperty("data", out var data) &&
            data.TryGetProperty("trackingList", out var trackingList))
        {
            foreach (var item in trackingList.EnumerateArray())
            {
                var dateStr = item.TryGetProperty("dateTime", out var d) ? d.GetString() : null;
                DateTime.TryParse(dateStr, out var date);

                events.Add(new TrackingEvent(
                    date,
                    item.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
                    item.TryGetProperty("detail", out var det) ? det.GetString() ?? "" : "",
                    item.TryGetProperty("location", out var l) ? l.GetString() : null));
            }
        }

        var latest = events.OrderByDescending(e => e.Timestamp).FirstOrDefault();

        return new ShipmentTrackingResult(
            trackingNumber, "Flash",
            latest?.Status ?? "Pending",
            latest?.Description ?? "รอข้อมูล",
            latest?.Timestamp, latest?.Location,
            null,
            latest?.Status?.Contains("Delivered", StringComparison.OrdinalIgnoreCase) == true,
            events);
    }

    private static ShipmentTrackingResult CreatePendingResult(string trackingNumber, string carrier)
    {
        return new ShipmentTrackingResult(
            trackingNumber, carrier,
            "Pending", "รอข้อมูลจากผู้ให้บริการ",
            null, null, null, false,
            new List<TrackingEvent>());
    }
}
