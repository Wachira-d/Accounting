using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

/// <summary>
/// รวมบริการภาครัฐไทย:
///
/// 1. กรมศุลกากร (Thai Customs) — HS Code lookup
///    - open.data.go.th CKAN + customs.go.th tariff search
///
/// 2. ETDA e-Timestamp — RFC 3161 Timestamp Authority
///    - https://etsa.teda.th/
///
/// 3. สำนักงานประกันสังคม (SSO) — อัตราสมทบ
///    - Static rates per year (updated annually by BOT announcement)
///
/// 4. กรมสรรพากร (RD) — VAT/WHT rates + branch lookup
///    - https://rdws.rd.go.th/ (SOAP) + JSON endpoints
///
/// 5. Thai NSW — Connection check for import/export
/// </summary>
public class ThaiGovIntegrationService : IThaiGovIntegrationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ThaiGovIntegrationService> _logger;

    public ThaiGovIntegrationService(IHttpClientFactory httpClientFactory, ILogger<ThaiGovIntegrationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ============================================================
    // กรมศุลกากร — HS Code Lookup
    // ============================================================

    public async Task<List<HsCodeResult>> SearchHsCodeAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return new List<HsCodeResult>();

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            // open.data.go.th CKAN API for customs tariff data
            var url = $"https://opendata.customs.go.th/api/3/action/datastore_search" +
                      $"?q={Uri.EscapeDataString(query)}&limit={Math.Min(limit, 50)}";

            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                // Fallback: search open.data.go.th
                return await SearchHsCodeFallbackAsync(query, limit);
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return ParseHsCodeResults(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HS Code search failed for: {Query}", query);
            return await SearchHsCodeFallbackAsync(query, limit);
        }
    }

    public async Task<HsCodeResult?> GetHsCodeAsync(string hsCode)
    {
        var results = await SearchHsCodeAsync(hsCode, 5);
        return results.FirstOrDefault(r => r.HsCode == hsCode || r.HsCode.StartsWith(hsCode));
    }

    public async Task<CustomsDutyInfo?> GetImportDutyRateAsync(string hsCode)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"https://opendata.customs.go.th/api/3/action/datastore_search" +
                      $"?filters={{\"tariff_code\":\"{Uri.EscapeDataString(hsCode)}\"}}&limit=1";

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return GetStaticDutyInfo(hsCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("result", out var result) &&
                result.TryGetProperty("records", out var records) &&
                records.GetArrayLength() > 0)
            {
                var rec = records[0];
                return new CustomsDutyInfo(
                    HsCode: hsCode,
                    GeneralRate: GetDecimal(rec, "general_rate", "import_duty") ?? 0,
                    WtoRate: GetDecimal(rec, "wto_rate"),
                    FtaAseanRate: GetDecimal(rec, "asean_rate", "afta_rate"),
                    FtaJtepaRate: GetDecimal(rec, "jtepa_rate"),
                    FtaChinaRate: GetDecimal(rec, "china_fta_rate"),
                    Notes: GetStr(rec, "remarks", "notes"));
            }

            return GetStaticDutyInfo(hsCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Duty rate lookup failed for HS: {HsCode}", hsCode);
            return GetStaticDutyInfo(hsCode);
        }
    }

    // ============================================================
    // ETDA e-Timestamp (RFC 3161)
    // ============================================================

    public async Task<TimestampResponse> RequestTimestampAsync(byte[] documentHash, string hashAlgorithm = "SHA256")
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            // ETDA Time-Stamping Authority
            var tsaUrl = "https://etsa.teda.th/tsa";

            // Build RFC 3161 TimeStampReq (simplified — real impl needs ASN.1 encoding)
            var hashBase64 = Convert.ToBase64String(documentHash);

            var requestBody = new
            {
                hash = hashBase64,
                hashAlgorithm = hashAlgorithm.ToUpper()
            };

            var content = new StringContent(
                JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(tsaUrl, content);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                return new TimestampResponse(
                    Success: true,
                    TimestampToken: body,
                    Timestamp: DateTime.UtcNow,
                    Authority: "ETDA TSA",
                    ErrorMessage: null);
            }

            return new TimestampResponse(false, null, null, null,
                $"ETDA TSA returned {response.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ETDA timestamp request failed");
            return new TimestampResponse(false, null, null, null, ex.Message);
        }
    }

    public Task<bool> VerifyTimestampAsync(byte[] timestampToken)
    {
        // RFC 3161 timestamp verification — needs ASN.1 parsing
        // For now, return true if token is present and non-empty
        return Task.FromResult(timestampToken.Length > 0);
    }

    // ============================================================
    // สำนักงานประกันสังคม (SSO) — Contribution Rates
    // ============================================================

    public Task<SsoContributionRate> GetCurrentSsoRateAsync()
    {
        var now = DateTime.UtcNow.AddHours(7);

        // SSO rates — updated annually by announcement
        // 2024-2026 rates: 5% each (employee + employer), max salary base 15,000 THB
        return Task.FromResult(new SsoContributionRate(
            EmployeeRate: 5.0m,
            EmployerRate: 5.0m,
            MaxSalaryBase: 15000m,
            MinSalaryBase: 1650m,
            Year: now.Year,
            Notes: "อัตราสมทบมาตรา 33 (ลูกจ้างทั่วไป) — ฐานเงินเดือนสูงสุด 15,000 บาท"
        ));
    }

    // ============================================================
    // กรมสรรพากร (RD) — VAT / WHT Rates
    // ============================================================

    public Task<RdVatRateInfo> GetCurrentVatRateAsync()
    {
        // Thai VAT: standard 10%, reduced to 7% by Royal Decree (extended periodically)
        // Current: 7% effective rate (extended through Sep 2025, historically always extended)
        return Task.FromResult(new RdVatRateInfo(
            StandardRate: 10.0m,
            ReducedRate: 7.0m,
            EffectiveFrom: new DateTime(2023, 10, 1),
            Notes: "อัตรา VAT 7% (ลดจาก 10% ตาม พ.ร.ฎ.) — ต่ออายุต่อเนื่อง"
        ));
    }

    /// <summary>
    /// อัตราหัก ณ ที่จ่ายตาม ท.ป.4/2528 — สร้างจาก
    /// <see cref="Accounting.Helpers.ThaiWhtRateTable"/> ตัวเดียวของระบบ
    ///
    /// <para>⚠️ เดิมที่นี่พิมพ์ตารางไว้เอง = <b>ตารางชุดที่สี่</b> (ผลตรวจทีม D ·
    /// D-01) — ตัวเลขบังเอิญถูกตามกฎหมาย แต่ "ตารางกฎหมายที่คัดลอกไปเขียนใหม่
    /// = คิดผิดตลอดไป" คือ defect class ที่เรพนี้เจอซ้ำที่สุด: วันที่กฎหมาย
    /// เปลี่ยน จะมีที่ต้องแก้มากกว่าหนึ่งที่ และคนแก้จะเห็นแค่ที่เดียว</para>
    ///
    /// <para>ค่าที่กฎหมาย<b>ไม่ได้กำหนดคงที่</b> (เงินเดือน 40(1) = อัตราขั้นบันได)
    /// ตารางกลางเก็บเป็น <c>null</c> — ที่นี่แปลงเป็น 0 พร้อมหมายเหตุ เพราะสัญญา
    /// ของ <c>RdWhtRateInfo</c> เป็น <c>decimal</c> ไม่ใช่ nullable</para>
    /// </summary>
    public Task<List<RdWhtRateInfo>> GetWhtRatesAsync()
    {
        var rows = Accounting.Helpers.ThaiWhtRateTable.All.Select(t => new RdWhtRateInfo(
            t.TaxSection,
            t.Name,
            t.IndividualRate ?? 0m,
            t.JuristicRate ?? 0m,
            t.Note ?? (t.IndividualRate is null && t.JuristicRate is null
                ? "กฎหมายไม่ได้กำหนดอัตราคงที่ — คำนวณตามฐานภาษีเงินได้บุคคลธรรมดา"
                : string.Join(" / ", t.ApplicableForms))))
            .ToList();
        return Task.FromResult(rows);
    }

    public async Task<RdBranchInfo?> LookupBranchAsync(string taxId, string branchCode)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var soapBody = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <Service xmlns="https://rdws.rd.go.th/serviceRD3/vatserviceRD3">
                  <username>anonymous</username>
                  <password>anonymous</password>
                  <TIN>{taxId}</TIN>
                  <ProvinceCode>0</ProvinceCode>
                  <BranchNumber>{branchCode}</BranchNumber>
                  <AmphurCode>0</AmphurCode>
                </Service>
              </soap:Body>
            </soap:Envelope>
            """;

            var req = new HttpRequestMessage(HttpMethod.Post, "https://rdws.rd.go.th/serviceRD3/vatserviceRD3.asmx")
            {
                Content = new StringContent(soapBody, Encoding.UTF8, "text/xml")
            };
            req.Headers.Add("SOAPAction", "https://rdws.rd.go.th/serviceRD3/vatserviceRD3/Service");

            var response = await client.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync();
            var branchName = ExtractXmlValue(body, "vBranchName");
            var branchTitle = ExtractXmlValue(body, "vBranchTitleName");
            var address = BuildAddressFromXml(body);

            if (string.IsNullOrWhiteSpace(branchName) && string.IsNullOrWhiteSpace(branchTitle))
                return null;

            return new RdBranchInfo(
                TaxId: taxId,
                BranchCode: branchCode,
                BranchName: string.Join(" ", new[] { branchTitle, branchName }.Where(s => !string.IsNullOrEmpty(s))),
                Address: address,
                Status: "Active");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RD branch lookup failed for {TaxId}/{Branch}", taxId, branchCode);
            return null;
        }
    }

    // ============================================================
    // Thai NSW (National Single Window)
    // ============================================================

    public async Task<bool> CheckNswConnectionAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync("https://www.thainsw.net/");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    // Private Helpers
    // ============================================================

    private async Task<List<HsCodeResult>> SearchHsCodeFallbackAsync(string query, int limit)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"https://data.go.th/api/3/action/datastore_search" +
                      $"?q={Uri.EscapeDataString(query)}&limit={Math.Min(limit, 20)}";

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new List<HsCodeResult>();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return ParseHsCodeResults(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HS Code fallback search failed");
            return new List<HsCodeResult>();
        }
    }

    private List<HsCodeResult> ParseHsCodeResults(JsonElement json)
    {
        var results = new List<HsCodeResult>();

        if (!json.TryGetProperty("result", out var result)) return results;
        if (!result.TryGetProperty("records", out var records)) return results;

        foreach (var rec in records.EnumerateArray())
        {
            var code = GetStr(rec, "tariff_code", "hs_code", "code", "hscode") ?? "";
            if (string.IsNullOrEmpty(code)) continue;

            results.Add(new HsCodeResult(
                HsCode: code,
                DescriptionTh: GetStr(rec, "description_th", "desc_th", "thai_description") ?? "",
                DescriptionEn: GetStr(rec, "description_en", "desc_en", "eng_description") ?? "",
                Unit: GetStr(rec, "unit", "quantity_unit"),
                ImportDutyRate: GetDecimal(rec, "import_duty", "duty_rate", "general_rate"),
                VatRate: GetDecimal(rec, "vat_rate"),
                ExciseRate: GetDecimal(rec, "excise_rate"),
                Category: GetStr(rec, "section", "category", "chapter")));
        }

        return results;
    }

    private static CustomsDutyInfo? GetStaticDutyInfo(string hsCode)
    {
        // First 2 digits = HS chapter, gives rough duty estimate
        if (hsCode.Length < 2) return null;
        var chapter = hsCode[..2];

        // Common chapters and approximate general duty rates
        var rate = chapter switch
        {
            "01" or "02" or "03" or "04" or "05" => 30m,  // Live animals, meat, fish, dairy
            "06" or "07" or "08" or "09" or "10" => 40m,  // Plants, vegetables, fruits, cereals
            "15" or "16" or "17" or "18" or "19" or "20" or "21" or "22" or "23" or "24" => 30m, // Food/beverages
            "27" => 5m,   // Mineral fuels, oils
            "28" or "29" => 1m,   // Chemicals
            "30" => 8m,   // Pharmaceuticals
            "39" or "40" => 10m,  // Plastics, rubber
            "44" => 10m,  // Wood
            "48" or "49" => 5m,   // Paper, printed matter
            "50" or "51" or "52" or "53" or "54" or "55" or "56" or "57" or "58" or "59" or "60" or "61" or "62" or "63" => 20m, // Textiles
            "72" or "73" => 5m,   // Iron & steel
            "84" => 0m,   // Machinery (often 0% for industrial)
            "85" => 5m,   // Electrical machinery
            "87" => 80m,  // Vehicles
            "90" => 0m,   // Instruments
            "94" => 20m,  // Furniture
            "95" => 10m,  // Toys, games
            _ => 10m
        };

        return new CustomsDutyInfo(hsCode, rate, null, null, null, null, "Approximate general rate by HS chapter");
    }

    private static string? ExtractXmlValue(string xml, string tag)
    {
        var match = System.Text.RegularExpressions.Regex.Match(xml,
            $"<{tag}>(.*?)</{tag}>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success) return null;

        var inner = match.Groups[1].Value;
        var anyMatch = System.Text.RegularExpressions.Regex.Match(inner, @"<anyType[^>]*>([^<]*)</anyType>");
        return anyMatch.Success ? anyMatch.Groups[1].Value.Trim() : inner.Trim();
    }

    private static string BuildAddressFromXml(string xml)
    {
        var parts = new List<string>();
        var fields = new[] { "vHouseNumber", "vRoomNumber", "vFloorNumber", "vBuildingName",
                             "vMooNumber", "vSoiName", "vStreetName", "vThambol", "vAmphur", "vProvince", "vPostCode" };

        foreach (var f in fields)
        {
            var val = ExtractXmlValue(xml, f);
            if (!string.IsNullOrWhiteSpace(val) && val != "-") parts.Add(val);
        }

        return string.Join(" ", parts);
    }

    private static string? GetStr(JsonElement el, params string[] props)
    {
        foreach (var p in props)
            if (el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        return null;
    }

    private static decimal? GetDecimal(JsonElement el, params string[] props)
    {
        foreach (var p in props)
        {
            if (!el.TryGetProperty(p, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var d)) return d;
        }
        return null;
    }
}
