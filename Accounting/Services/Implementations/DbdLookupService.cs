using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

public class DbdLookupService : IDbdLookupService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DbdLookupService> _logger;

    public DbdLookupService(IHttpClientFactory httpClientFactory, ILogger<DbdLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<DbdCompanyResult>> SearchByNameAsync(string query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return new List<DbdCompanyResult>();

        try
        {
            var client = _httpClientFactory.CreateClient("Dbd");
            // Use CKAN DataStore search API on opendata.dbd.go.th
            var url = $"https://opendata.dbd.go.th/api/3/action/datastore_search" +
                      $"?resource_id=08a1d598-2df0-4d37-9661-08e2555041e4" +
                      $"&q={Uri.EscapeDataString(query)}" +
                      $"&limit={Math.Min(limit, 20)}";

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("DBD CKAN search failed: {Status}", response.StatusCode);
                return new List<DbdCompanyResult>();
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var results = new List<DbdCompanyResult>();

            if (json.TryGetProperty("result", out var result) &&
                result.TryGetProperty("records", out var records))
            {
                foreach (var rec in records.EnumerateArray())
                {
                    results.Add(MapCkanRecord(rec));
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DBD name search failed for query: {Query}", query);
            return new List<DbdCompanyResult>();
        }
    }

    public async Task<DbdCompanyResult?> GetByJuristicIdAsync(string juristicId)
    {
        if (string.IsNullOrWhiteSpace(juristicId) || juristicId.Length != 13)
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient("Dbd");

            // Try DBD Open API first
            var url = $"https://openapi.dbd.go.th/api/v1/juristic_person/{Uri.EscapeDataString(juristicId)}";
            var response = await client.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                return MapDbdApiResponse(json, juristicId);
            }

            // Fallback: CKAN datastore search by ID
            _logger.LogInformation("DBD Open API failed ({Status}), falling back to CKAN", response.StatusCode);
            var ckanUrl = $"https://opendata.dbd.go.th/api/3/action/datastore_search" +
                          $"?resource_id=08a1d598-2df0-4d37-9661-08e2555041e4" +
                          $"&q={Uri.EscapeDataString(juristicId)}" +
                          $"&limit=1";

            var ckanResponse = await client.GetAsync(ckanUrl);
            if (!ckanResponse.IsSuccessStatusCode) return null;

            var ckanJson = await ckanResponse.Content.ReadFromJsonAsync<JsonElement>();
            if (ckanJson.TryGetProperty("result", out var result) &&
                result.TryGetProperty("records", out var records) &&
                records.GetArrayLength() > 0)
            {
                return MapCkanRecord(records[0]);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DBD lookup failed for ID: {Id}", juristicId);
            return null;
        }
    }

    public async Task<TinCheckResult> VerifyTinAsync(string tin)
    {
        if (string.IsNullOrWhiteSpace(tin) || tin.Length != 13)
            return new TinCheckResult(false, false, tin);

        try
        {
            var client = _httpClientFactory.CreateClient("Dbd");

            // Use RD JSON Web Service
            var url = $"https://rdws.rd.go.th/jsonRD/checktinpinservice.asmx/ServiceTIN" +
                      $"?TIN={Uri.EscapeDataString(tin)}";

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return new TinCheckResult(false, false, tin);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            var isExist = false;
            var digitOk = false;

            if (json.TryGetProperty("IsExist", out var existProp))
            {
                var val = existProp.ValueKind == JsonValueKind.Array
                    ? existProp[0].GetString()
                    : existProp.GetString();
                isExist = val?.Equals("Yes", StringComparison.OrdinalIgnoreCase) == true;
            }

            if (json.TryGetProperty("DigitOk", out var digitProp))
            {
                var val = digitProp.ValueKind == JsonValueKind.Array
                    ? digitProp[0].GetString()
                    : digitProp.GetString();
                digitOk = val?.Equals("True", StringComparison.OrdinalIgnoreCase) == true;
            }

            return new TinCheckResult(digitOk, isExist, tin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TIN verification failed for: {Tin}", tin);
            return new TinCheckResult(false, false, tin);
        }
    }

    private static DbdCompanyResult MapDbdApiResponse(JsonElement json, string juristicId)
    {
        return new DbdCompanyResult(
            JuristicId: juristicId,
            NameTh: GetStr(json, "juristicNameTH") ?? GetStr(json, "name_th") ?? "",
            NameEn: GetStr(json, "juristicNameEN") ?? GetStr(json, "name_en"),
            JuristicType: GetStr(json, "juristicType") ?? GetStr(json, "juristic_type"),
            Status: GetStr(json, "juristicStatus") ?? GetStr(json, "status"),
            RegisteredCapital: GetDecimal(json, "registeredCapital") ?? GetDecimal(json, "registered_capital"),
            Address: GetStr(json, "address") ?? GetStr(json, "headOfficeAddress"),
            RegisterDate: GetStr(json, "registerDate") ?? GetStr(json, "register_date"),
            Objective: GetStr(json, "objective")
        );
    }

    private static DbdCompanyResult MapCkanRecord(JsonElement rec)
    {
        // CKAN field names may vary - try common patterns
        return new DbdCompanyResult(
            JuristicId: GetStr(rec, "juristic_id") ?? GetStr(rec, "juristicID") ??
                        GetStr(rec, "JURISTIC_ID") ?? GetStr(rec, "_id")?.ToString() ?? "",
            NameTh: GetStr(rec, "juristic_name_th") ?? GetStr(rec, "juristicNameTH") ??
                    GetStr(rec, "JURISTIC_NAME_TH") ?? GetStr(rec, "name") ?? "",
            NameEn: GetStr(rec, "juristic_name_en") ?? GetStr(rec, "juristicNameEN") ??
                    GetStr(rec, "JURISTIC_NAME_EN"),
            JuristicType: GetStr(rec, "juristic_type") ?? GetStr(rec, "JURISTIC_TYPE"),
            Status: GetStr(rec, "juristic_status") ?? GetStr(rec, "JURISTIC_STATUS"),
            RegisteredCapital: GetDecimal(rec, "registered_capital") ??
                               GetDecimal(rec, "REGISTERED_CAPITAL"),
            Address: GetStr(rec, "address") ?? GetStr(rec, "ADDRESS"),
            RegisterDate: GetStr(rec, "register_date") ?? GetStr(rec, "REGISTER_DATE"),
            Objective: GetStr(rec, "objective") ?? GetStr(rec, "OBJECTIVE")
        );
    }

    private static string? GetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }

    private static decimal? GetDecimal(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var val)) return null;
        if (val.ValueKind == JsonValueKind.Number) return val.GetDecimal();
        if (val.ValueKind == JsonValueKind.String && decimal.TryParse(val.GetString(), out var d)) return d;
        return null;
    }
}
