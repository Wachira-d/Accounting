using System.Collections.Concurrent;
using System.Text.Json;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

/// <summary>
/// Thai Address Autocomplete — uses static JSON data from Thailand Post / DOPA
/// Data source: https://raw.githubusercontent.com/ApisitKawor662/Thai-Address-Database/master/db.json
/// Alternative: data.go.th CKAN API for official DOPA subdivision data
///
/// ข้อมูลตำบล/อำเภอ/จังหวัด/รหัสไปรษณีย์ ทั้ง 7,255 ตำบล
/// โหลดครั้งเดียวเก็บใน memory สำหรับ autocomplete แบบ instant
/// </summary>
public class ThaiAddressService : IThaiAddressService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ThaiAddressService> _logger;
    private static readonly ConcurrentDictionary<string, ThaiAddressResult> _addressCache = new();
    private static List<ThaiAddressResult>? _allAddresses;
    private static readonly SemaphoreSlim _loadLock = new(1, 1);

    private const string DataUrl = "https://raw.githubusercontent.com/ApisitKawor662/Thai-Address-Database/master/db.json";

    public ThaiAddressService(IHttpClientFactory httpClientFactory, ILogger<ThaiAddressService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<ThaiAddressResult>> SearchAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return new List<ThaiAddressResult>();

        var addresses = await EnsureLoadedAsync();
        var q = query.Trim().ToLowerInvariant();

        return addresses
            .Where(a =>
                a.SubDistrictNameTh.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.DistrictNameTh.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.ProvinceNameTh.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.PostalCode.StartsWith(q) ||
                a.SubDistrictNameEn.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.DistrictNameEn.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.ProvinceNameEn.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }

    public async Task<List<ThaiAddressResult>> GetByPostalCodeAsync(string postalCode)
    {
        if (string.IsNullOrWhiteSpace(postalCode) || postalCode.Length != 5)
            return new List<ThaiAddressResult>();

        var addresses = await EnsureLoadedAsync();
        return addresses.Where(a => a.PostalCode == postalCode).ToList();
    }

    public async Task<List<ThaiProvinceInfo>> GetProvincesAsync()
    {
        var addresses = await EnsureLoadedAsync();
        return addresses
            .Select(a => new ThaiProvinceInfo(a.ProvinceCode, a.ProvinceNameTh, a.ProvinceNameEn))
            .Distinct()
            .OrderBy(p => p.NameTh)
            .ToList();
    }

    public async Task<List<ThaiDistrictInfo>> GetDistrictsAsync(string provinceCode)
    {
        var addresses = await EnsureLoadedAsync();
        return addresses
            .Where(a => a.ProvinceCode == provinceCode)
            .Select(a => new ThaiDistrictInfo(a.DistrictCode, a.DistrictNameTh, a.DistrictNameEn, a.ProvinceCode))
            .Distinct()
            .OrderBy(d => d.NameTh)
            .ToList();
    }

    public async Task<List<ThaiSubDistrictInfo>> GetSubDistrictsAsync(string districtCode)
    {
        var addresses = await EnsureLoadedAsync();
        return addresses
            .Where(a => a.DistrictCode == districtCode)
            .Select(a => new ThaiSubDistrictInfo(a.SubDistrictCode, a.SubDistrictNameTh, a.SubDistrictNameEn, a.DistrictCode, a.PostalCode))
            .Distinct()
            .OrderBy(s => s.NameTh)
            .ToList();
    }

    private async Task<List<ThaiAddressResult>> EnsureLoadedAsync()
    {
        if (_allAddresses != null) return _allAddresses;

        await _loadLock.WaitAsync();
        try
        {
            if (_allAddresses != null) return _allAddresses;

            _logger.LogInformation("Loading Thai address database...");

            // Try loading from embedded resource first, then from URL
            var loaded = await LoadFromUrlAsync() ?? GenerateMinimalFallback();

            _allAddresses = loaded;
            _logger.LogInformation("Loaded {Count} Thai addresses", _allAddresses.Count);
            return _allAddresses;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<List<ThaiAddressResult>?> LoadFromUrlAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var json = await client.GetStringAsync(DataUrl);
            var doc = JsonDocument.Parse(json);

            var results = new List<ThaiAddressResult>();
            var root = doc.RootElement;

            // Parse the Thai address database format
            // Format varies — handle both flat array and nested structures
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var addr = ParseAddressEntry(item);
                    if (addr != null) results.Add(addr);
                }
            }
            else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var addr = ParseAddressEntry(item);
                    if (addr != null) results.Add(addr);
                }
            }

            return results.Count > 0 ? results : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Thai address database from URL");
            return null;
        }
    }

    private static ThaiAddressResult? ParseAddressEntry(JsonElement item)
    {
        var subDistrictTh = GetStr(item, "district", "sub_district", "tambon", "tumbol", "subDistrict") ?? "";
        var districtTh = GetStr(item, "amphoe", "amphur", "district_name", "amphoe_name") ?? "";
        var provinceTh = GetStr(item, "province", "changwat", "province_name") ?? "";
        var zipcode = GetStr(item, "zipcode", "postal_code", "zip", "postcode") ?? "";

        if (string.IsNullOrEmpty(subDistrictTh) && string.IsNullOrEmpty(districtTh))
            return null;

        return new ThaiAddressResult(
            SubDistrictCode: GetStr(item, "district_code", "sub_district_code", "tambon_code") ?? "",
            SubDistrictNameTh: subDistrictTh,
            SubDistrictNameEn: GetStr(item, "district_en", "sub_district_en", "tambon_en") ?? "",
            DistrictCode: GetStr(item, "amphoe_code", "amphur_code", "district_code_parent") ?? "",
            DistrictNameTh: districtTh,
            DistrictNameEn: GetStr(item, "amphoe_en", "amphur_en", "amphoe_name_en") ?? "",
            ProvinceCode: GetStr(item, "province_code", "changwat_code") ?? "",
            ProvinceNameTh: provinceTh,
            ProvinceNameEn: GetStr(item, "province_en", "changwat_en", "province_name_en") ?? "",
            PostalCode: zipcode);
    }

    private static List<ThaiAddressResult> GenerateMinimalFallback()
    {
        // Minimal fallback for offline operation — major cities only
        return new List<ThaiAddressResult>
        {
            new("100101", "พระบรมมหาราชวัง", "Phra Borom Maha Ratchawang", "1001", "พระนคร", "Phra Nakhon", "10", "กรุงเทพมหานคร", "Bangkok", "10200"),
            new("100201", "ดุสิต", "Dusit", "1002", "ดุสิต", "Dusit", "10", "กรุงเทพมหานคร", "Bangkok", "10300"),
            new("100301", "กระทุ่มแบน", "Krathum Baen", "1003", "หนองแขม", "Nong Khaem", "10", "กรุงเทพมหานคร", "Bangkok", "10160"),
            new("500101", "ศรีภูมิ", "Si Phum", "5001", "เมืองเชียงใหม่", "Mueang Chiang Mai", "50", "เชียงใหม่", "Chiang Mai", "50200"),
            new("200101", "บางปลาสร้อย", "Bang Pla Soi", "2001", "เมืองชลบุรี", "Mueang Chon Buri", "20", "ชลบุรี", "Chon Buri", "20000"),
        };
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
}
