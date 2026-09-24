using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Accounting.Services.Implementations;

public class DbdLookupService : IDbdLookupService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DbdLookupService> _logger;
    private readonly IMemoryCache _cache;

    /// <summary>Cached DBD lookups for 24h. Government data changes slowly,
    /// and even 1h cache transforms a 4-API-call hot path (~2-5s) into a memory hit (~10µs).
    /// Negative results cached for 1h to throttle API hammering on bogus IDs.</summary>
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromHours(1);

    public DbdLookupService(IHttpClientFactory httpClientFactory, ILogger<DbdLookupService> logger,
        IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cache = cache;
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

        // Memory cache layer — DBD/RD APIs are slow (2-5s combined) and rate-limited.
        // Repeat scans of same vendor return instantly.
        var cacheKey = $"dbd:{juristicId}";
        if (_cache.TryGetValue<DbdCompanyResult?>(cacheKey, out var cached))
        {
            _logger.LogDebug("DBD cache hit for {Id}", juristicId);
            return cached;
        }

        var result = await LookupJuristicIdLiveAsync(juristicId);
        // Positive hits cached longer than misses — TIN data changes rarely
        var ttl = result != null && !string.IsNullOrWhiteSpace(result.NameTh) ? PositiveTtl : NegativeTtl;
        _cache.Set(cacheKey, result, ttl);
        return result;
    }

    private async Task<DbdCompanyResult?> LookupJuristicIdLiveAsync(string juristicId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Dbd");
            client.Timeout = TimeSpan.FromSeconds(15);

            // Strategy: try multiple sources, return the first one that has a NAME.
            // We accept "found ID with no name" only as a last resort.
            DbdCompanyResult? bestPartial = null;

            // 1) Revenue Department VAT lookup — most reliable free source for Thai businesses
            //    (works for VAT-registered companies — covers nearly all juristic persons)
            try
            {
                var rdResult = await LookupRdVatAsync(client, juristicId);
                if (rdResult != null)
                {
                    if (!string.IsNullOrWhiteSpace(rdResult.NameTh) || !string.IsNullOrWhiteSpace(rdResult.NameEn))
                        return rdResult;
                    bestPartial ??= rdResult;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "RD VAT lookup failed for {Id}", juristicId); }

            // 2) DBD Open API (often empty without API key, but try anyway)
            try
            {
                var url = $"https://openapi.dbd.go.th/api/v1/juristic_person/{Uri.EscapeDataString(juristicId)}";
                var response = await client.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("DBD Open API ({Status}): {Len} chars", response.StatusCode, body.Length);

                if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{'))
                {
                    var json = JsonSerializer.Deserialize<JsonElement>(body);
                    var r = MapDbdApiResponse(json, juristicId);
                    if (!string.IsNullOrWhiteSpace(r.NameTh) || !string.IsNullOrWhiteSpace(r.NameEn))
                        return r;
                    bestPartial ??= r;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "DBD Open API failed for {Id}", juristicId); }

            // 2) CKAN datastore — use FILTERS parameter for exact ID match
            //    This is the most reliable free public source.
            try
            {
                var filters = JsonSerializer.Serialize(new Dictionary<string, string> { ["juristic_id"] = juristicId });
                var ckanUrl = $"https://opendata.dbd.go.th/api/3/action/datastore_search" +
                              $"?resource_id=08a1d598-2df0-4d37-9661-08e2555041e4" +
                              $"&filters={Uri.EscapeDataString(filters)}" +
                              $"&limit=1";
                var ckanResponse = await client.GetAsync(ckanUrl);
                var ckanBody = await ckanResponse.Content.ReadAsStringAsync();
                _logger.LogInformation("CKAN filter ({Status}): {Len} chars", ckanResponse.StatusCode, ckanBody.Length);

                if (ckanResponse.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(ckanBody))
                {
                    var ckanJson = JsonSerializer.Deserialize<JsonElement>(ckanBody);
                    if (ckanJson.TryGetProperty("result", out var result) &&
                        result.TryGetProperty("records", out var records) &&
                        records.GetArrayLength() > 0)
                    {
                        var r = MapCkanRecord(records[0]);
                        if (!string.IsNullOrWhiteSpace(r.NameTh) || !string.IsNullOrWhiteSpace(r.NameEn))
                            return r;
                        bestPartial ??= r;
                    }
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "CKAN filter failed for {Id}", juristicId); }

            // 3) CKAN datastore — fallback to text search (resource may have updated structure)
            try
            {
                var ckanUrl = $"https://opendata.dbd.go.th/api/3/action/datastore_search" +
                              $"?resource_id=08a1d598-2df0-4d37-9661-08e2555041e4" +
                              $"&q={Uri.EscapeDataString(juristicId)}" +
                              $"&limit=5";
                var ckanResponse = await client.GetAsync(ckanUrl);
                var ckanBody = await ckanResponse.Content.ReadAsStringAsync();

                if (ckanResponse.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(ckanBody))
                {
                    var ckanJson = JsonSerializer.Deserialize<JsonElement>(ckanBody);
                    if (ckanJson.TryGetProperty("result", out var result) &&
                        result.TryGetProperty("records", out var records))
                    {
                        // Pick the record whose juristic_id exactly matches
                        foreach (var rec in records.EnumerateArray())
                        {
                            var r = MapCkanRecord(rec);
                            if (r.JuristicId == juristicId &&
                                (!string.IsNullOrWhiteSpace(r.NameTh) || !string.IsNullOrWhiteSpace(r.NameEn)))
                                return r;
                            if (r.JuristicId == juristicId)
                                bestPartial ??= r;
                        }
                    }
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "CKAN search failed for {Id}", juristicId); }

            // 4) DataWarehouse public endpoint (no auth required for basic info)
            try
            {
                var dwUrl = $"https://datawarehouse.dbd.go.th/api/searchJuristicInfoByID/{Uri.EscapeDataString(juristicId)}";
                var dwResponse = await client.GetAsync(dwUrl);
                if (dwResponse.IsSuccessStatusCode)
                {
                    var dwBody = await dwResponse.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(dwBody) && dwBody.TrimStart().StartsWith('{'))
                    {
                        var dwJson = JsonSerializer.Deserialize<JsonElement>(dwBody);
                        var r = MapDbdApiResponse(dwJson, juristicId);
                        if (!string.IsNullOrWhiteSpace(r.NameTh) || !string.IsNullOrWhiteSpace(r.NameEn))
                            return r;
                        bestPartial ??= r;
                    }
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "DBD DataWarehouse failed for {Id}", juristicId); }

            return bestPartial;
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

    public async Task<DbdCompanyResult?> GetBranchAsync(string juristicId, string? branchCode)
    {
        if (string.IsNullOrWhiteSpace(juristicId) || juristicId.Length != 13) return null;
        // สำนักงานใหญ่/ไม่ระบุ = เส้นเดิมทุกประการ (ไม่เปลี่ยนพฤติกรรมของผู้เรียกเดิม)
        if (Accounting.Helpers.TaxBranchCode.IsHeadOffice(branchCode))
            return await GetByJuristicIdAsync(juristicId);
        if (!Accounting.Helpers.TaxBranchCode.TryNormalize(branchCode, out var code, out _) || code == null)
            return null;

        var cacheKey = $"dbd:{juristicId}:b{code}";
        if (_cache.TryGetValue<DbdCompanyResult?>(cacheKey, out var cached)) return cached;

        DbdCompanyResult? result = null;
        try
        {
            var client = _httpClientFactory.CreateClient("Dbd");
            client.Timeout = TimeSpan.FromSeconds(15);
            var body = await PostRdVatAsync(client, juristicId, int.Parse(code));
            var records = Accounting.Helpers.RdVatBranchRecords.Parse(body);
            var rec = Accounting.Helpers.RdVatBranchRecords.PickBranch(records, code);
            if (rec != null)
                result = new DbdCompanyResult(
                    JuristicId: juristicId,
                    NameTh: rec.Name,
                    NameEn: null,
                    JuristicType: null,
                    Status: "Active",
                    RegisteredCapital: null,
                    Address: rec.Address,
                    RegisterDate: null,
                    Objective: null,
                    BranchCode: rec.BranchCode,
                    BranchName: string.Join(" ", new[] { rec.BranchTitle, rec.BranchName }
                        .Where(s => !string.IsNullOrWhiteSpace(s))));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "RD VAT branch lookup failed for {Id}/{Branch}", juristicId, code); }

        _cache.Set(cacheKey, result, result != null ? PositiveTtl : NegativeTtl);
        return result;
    }

    /// <summary>ยิง SOAP ของทะเบียน VAT กรมสรรพากรด้วยเลขสาขาที่ระบุ — คืนเนื้อ XML ดิบ (null = ไม่สำเร็จ)</summary>
    private async Task<string?> PostRdVatAsync(HttpClient client, string tin, int branchNumber)
    {
        var soapBody = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <Service xmlns="https://rdws.rd.go.th/serviceRD3/vatserviceRD3">
              <username>anonymous</username>
              <password>anonymous</password>
              <TIN>{tin}</TIN>
              <ProvinceCode>0</ProvinceCode>
              <BranchNumber>{branchNumber}</BranchNumber>
              <AmphurCode>0</AmphurCode>
            </Service>
          </soap:Body>
        </soap:Envelope>
        """;
        var req = new HttpRequestMessage(HttpMethod.Post, "https://rdws.rd.go.th/serviceRD3/vatserviceRD3.asmx")
        {
            Content = new StringContent(soapBody, System.Text.Encoding.UTF8, "text/xml")
        };
        req.Headers.Add("SOAPAction", "https://rdws.rd.go.th/serviceRD3/vatserviceRD3/Service");
        var response = await client.SendAsync(req);
        var body = await response.Content.ReadAsStringAsync();
        return response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body) ? body : null;
    }

    /// <summary>
    /// Revenue Department VAT lookup — public SOAP service, no API key needed.
    /// Returns full company name + branch + address for VAT-registered juristic persons.
    /// </summary>
    private async Task<DbdCompanyResult?> LookupRdVatAsync(HttpClient client, string tin)
    {
        var soapBody = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <Service xmlns="https://rdws.rd.go.th/serviceRD3/vatserviceRD3">
              <username>anonymous</username>
              <password>anonymous</password>
              <TIN>{tin}</TIN>
              <ProvinceCode>0</ProvinceCode>
              <BranchNumber>0</BranchNumber>
              <AmphurCode>0</AmphurCode>
            </Service>
          </soap:Body>
        </soap:Envelope>
        """;

        var req = new HttpRequestMessage(HttpMethod.Post, "https://rdws.rd.go.th/serviceRD3/vatserviceRD3.asmx")
        {
            Content = new StringContent(soapBody, System.Text.Encoding.UTF8, "text/xml")
        };
        req.Headers.Add("SOAPAction", "https://rdws.rd.go.th/serviceRD3/vatserviceRD3/Service");

        var response = await client.SendAsync(req);
        var body = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("RD VAT ({Status}): {Len} chars", response.StatusCode, body.Length);
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body)) return null;

        // Lightweight XML parsing — extract values without bringing in System.Xml.Linq overhead
        string PickFirst(string tag)
        {
            // Match <tag>...<anyValue>VAL</anyValue>...</tag> or direct text
            var m = System.Text.RegularExpressions.Regex.Match(body,
                $"<{tag}>(.*?)</{tag}>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) return "";
            var inner = m.Groups[1].Value;
            // RD wraps results inside <anyType xsi:type="xsd:string">VAL</anyType>
            var anyMatches = System.Text.RegularExpressions.Regex.Matches(inner,
                @"<anyType[^>]*>([^<]*)</anyType>");
            if (anyMatches.Count == 0) return inner.Trim();
            foreach (System.Text.RegularExpressions.Match am in anyMatches)
            {
                var v = am.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(v) && v != "-") return v;
            }
            return "";
        }

        var titleTh = PickFirst("vtitleName");
        var nameTh = PickFirst("vName");
        var surnameTh = PickFirst("vSurname");
        var fullNameTh = string.Join(" ", new[] { titleTh, nameTh, surnameTh }.Where(s => !string.IsNullOrEmpty(s)));

        if (string.IsNullOrWhiteSpace(fullNameTh)) return null;

        var building = PickFirst("vBuildingName");
        var floor = PickFirst("vFloorNumber");
        var village = PickFirst("vVillageName");
        var room = PickFirst("vRoomNumber");
        var house = PickFirst("vHouseNumber");
        var moo = PickFirst("vMooNumber");
        var soi = PickFirst("vSoiName");
        var street = PickFirst("vStreetName");
        var thambol = PickFirst("vThambol");
        var amphur = PickFirst("vAmphur");
        var province = PickFirst("vProvince");
        var postcode = PickFirst("vPostCode");

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(house)) parts.Add(house);
        if (!string.IsNullOrEmpty(room)) parts.Add($"ห้อง {room}");
        if (!string.IsNullOrEmpty(floor)) parts.Add($"ชั้น {floor}");
        if (!string.IsNullOrEmpty(building)) parts.Add($"อาคาร {building}");
        if (!string.IsNullOrEmpty(village)) parts.Add($"หมู่บ้าน {village}");
        if (!string.IsNullOrEmpty(moo)) parts.Add($"หมู่ {moo}");
        if (!string.IsNullOrEmpty(soi)) parts.Add($"ซอย {soi}");
        if (!string.IsNullOrEmpty(street)) parts.Add($"ถนน {street}");
        if (!string.IsNullOrEmpty(thambol)) parts.Add($"ตำบล {thambol}");
        if (!string.IsNullOrEmpty(amphur)) parts.Add($"อำเภอ {amphur}");
        if (!string.IsNullOrEmpty(province)) parts.Add($"จังหวัด {province}");
        if (!string.IsNullOrEmpty(postcode)) parts.Add(postcode);

        var address = string.Join(" ", parts);

        var branchTitle = PickFirst("vBranchTitleName");
        var branchName = PickFirst("vBranchName");
        var branch = string.Join(" ", new[] { branchTitle, branchName }.Where(s => !string.IsNullOrEmpty(s)));

        return new DbdCompanyResult(
            JuristicId: tin,
            NameTh: fullNameTh,
            NameEn: null,
            JuristicType: titleTh,
            Status: "Active",
            RegisteredCapital: null,
            Address: address,
            RegisterDate: null,
            Objective: string.IsNullOrEmpty(branch) ? null : $"สาขา: {branch}"
        );
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
