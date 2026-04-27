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
            client.Timeout = TimeSpan.FromSeconds(10);

            // Try DBD Open API first
            DbdCompanyResult? apiResult = null;
            try
            {
                var url = $"https://openapi.dbd.go.th/api/v1/juristic_person/{Uri.EscapeDataString(juristicId)}";
                var response = await client.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("DBD Open API response ({Status}): {Body}",
                    response.StatusCode, body.Length > 500 ? body[..500] : body);

                if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body))
                {
                    var json = JsonSerializer.Deserialize<JsonElement>(body);
                    apiResult = MapDbdApiResponse(json, juristicId);
                    if (!string.IsNullOrWhiteSpace(apiResult.NameTh))
                        return apiResult;
                    _logger.LogWarning("DBD Open API returned empty name fields for {Id}", juristicId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DBD Open API call failed for {Id}, trying CKAN", juristicId);
            }

            // Fallback: CKAN datastore search by ID
            try
            {
                var ckanUrl = $"https://opendata.dbd.go.th/api/3/action/datastore_search" +
                              $"?resource_id=08a1d598-2df0-4d37-9661-08e2555041e4" +
                              $"&q={Uri.EscapeDataString(juristicId)}" +
                              $"&limit=5";

                var ckanResponse = await client.GetAsync(ckanUrl);
                var ckanBody = await ckanResponse.Content.ReadAsStringAsync();
                _logger.LogInformation("CKAN response ({Status}): {Body}",
                    ckanResponse.StatusCode, ckanBody.Length > 500 ? ckanBody[..500] : ckanBody);

                if (ckanResponse.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(ckanBody))
                {
                    var ckanJson = JsonSerializer.Deserialize<JsonElement>(ckanBody);
                    if (ckanJson.TryGetProperty("result", out var result) &&
                        result.TryGetProperty("records", out var records) &&
                        records.GetArrayLength() > 0)
                    {
                        return MapCkanRecord(records[0]);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CKAN search failed for {Id}", juristicId);
            }

            // Fallback: try DBD DataWarehouse
            try
            {
                var dwUrl = $"https://datawarehouse.dbd.go.th/api/juristic/{Uri.EscapeDataString(juristicId)}";
                var dwResponse = await client.GetAsync(dwUrl);
                if (dwResponse.IsSuccessStatusCode)
                {
                    var dwBody = await dwResponse.Content.ReadAsStringAsync();
                    _logger.LogInformation("DBD DataWarehouse response: {Body}",
                        dwBody.Length > 500 ? dwBody[..500] : dwBody);

                    if (!string.IsNullOrWhiteSpace(dwBody))
                    {
                        var dwJson = JsonSerializer.Deserialize<JsonElement>(dwBody);
                        var dwResult = MapDbdApiResponse(dwJson, juristicId);
                        if (!string.IsNullOrWhiteSpace(dwResult.NameTh))
                            return dwResult;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DBD DataWarehouse call failed for {Id}", juristicId);
            }

            // Return the partial result from primary API if we got one
            return apiResult;
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
        // Many government APIs wrap data in "data" array or object — unwrap if needed
        var el = UnwrapData(json);

        return new DbdCompanyResult(
            JuristicId: juristicId,
            NameTh: GetStr(el, "juristicNameTH", "JURISTIC_NAME_TH", "juristicNameTh",
                        "juristic_name_th", "name_th", "nameTh", "companyName",
                        "juristicName") ?? "",
            NameEn: GetStr(el, "juristicNameEN", "JURISTIC_NAME_EN", "juristicNameEn",
                        "juristic_name_en", "name_en", "nameEn"),
            JuristicType: GetStr(el, "juristicType", "JURISTIC_TYPE", "juristic_type",
                        "CD_JURISTIC_TYPE", "cd_juristic_type", "entityType"),
            Status: GetStr(el, "juristicStatus", "JURISTIC_STATUS", "juristic_status",
                        "status", "companyStatus"),
            RegisteredCapital: GetDecimal(el, "registeredCapital", "REGISTERED_CAPITAL",
                        "registered_capital"),
            Address: GetStr(el, "address", "ADDRESS", "headOfficeAddress",
                        "addr_name", "ADDR_NAME", "fullAddress"),
            RegisterDate: GetStr(el, "registerDate", "REGISTER_DATE", "register_date",
                        "registeredDate"),
            Objective: GetStr(el, "objective", "OBJECTIVE")
        );
    }

    private static DbdCompanyResult MapCkanRecord(JsonElement rec)
    {
        return new DbdCompanyResult(
            JuristicId: GetStr(rec, "juristic_id", "juristicID", "JURISTIC_ID",
                        "juristicId", "CD_JURISTIC", "_id") ?? "",
            NameTh: GetStr(rec, "juristic_name_th", "juristicNameTH", "JURISTIC_NAME_TH",
                        "juristicNameTh", "name_th", "nameTh", "name") ?? "",
            NameEn: GetStr(rec, "juristic_name_en", "juristicNameEN", "JURISTIC_NAME_EN",
                        "juristicNameEn", "name_en", "nameEn"),
            JuristicType: GetStr(rec, "juristic_type", "JURISTIC_TYPE", "CD_JURISTIC_TYPE",
                        "juristicType", "cd_juristic_type"),
            Status: GetStr(rec, "juristic_status", "JURISTIC_STATUS", "juristicStatus",
                        "status"),
            RegisteredCapital: GetDecimal(rec, "registered_capital", "REGISTERED_CAPITAL",
                        "registeredCapital"),
            Address: GetStr(rec, "address", "ADDRESS", "addr_name", "ADDR_NAME",
                        "headOfficeAddress", "fullAddress"),
            RegisterDate: GetStr(rec, "register_date", "REGISTER_DATE", "registerDate",
                        "registeredDate"),
            Objective: GetStr(rec, "objective", "OBJECTIVE")
        );
    }

    private static JsonElement UnwrapData(JsonElement json)
    {
        // Government APIs often wrap data: { "data": [...] } or { "data": { ... } }
        if (json.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                return data[0];
            if (data.ValueKind == JsonValueKind.Object)
                return data;
        }
        if (json.TryGetProperty("result", out var result))
        {
            if (result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0)
                return result[0];
            if (result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("records", out var records) &&
                    records.ValueKind == JsonValueKind.Array && records.GetArrayLength() > 0)
                    return records[0];
                return result;
            }
        }
        return json;
    }

    private static string? GetStr(JsonElement el, params string[] props)
    {
        foreach (var prop in props)
        {
            if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            {
                var s = val.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    private static decimal? GetDecimal(JsonElement el, params string[] props)
    {
        foreach (var prop in props)
        {
            if (!el.TryGetProperty(prop, out var val)) continue;
            if (val.ValueKind == JsonValueKind.Number) return val.GetDecimal();
            if (val.ValueKind == JsonValueKind.String && decimal.TryParse(val.GetString(), out var d)) return d;
        }
        return null;
    }
}
