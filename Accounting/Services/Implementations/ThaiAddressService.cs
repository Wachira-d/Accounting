using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

/// <summary>
/// Thai Address Autocomplete — backed by the embedded
/// Resources/ThaiAdmin.csv that ships with the app (7,436 tambon rows,
/// every postal code in Thailand). Loaded into memory once at first
/// request, served from a static cache thereafter.
///
/// Was previously fetching a remote db.json from GitHub which:
///   • required outbound internet from the production host,
///   • broke silently when the URL went away, and
///   • degraded to a 5-row "minimal fallback" that listed only
///     Bangkok / Chonburi / Chiang Mai — making "พิมพ์จังหวัด"
///     show three options and postal codes outside that tiny set
///     come back as "not found".
/// Same CSV that ThaiAdminCodes already uses for the e-Tax XML
/// TISI 1099 codes — single source of truth.
///
/// Columns in the CSV: PostalCode, Province, District, SubDistrict,
/// AddressCode (8 digits where the first 2 = province TISI, the
/// first 4 = district TISI, the first 6 = subdistrict TISI).
/// </summary>
public class ThaiAddressService : IThaiAddressService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ThaiAddressService> _logger;
    private static readonly ConcurrentDictionary<string, ThaiAddressResult> _addressCache = new();
    private static List<ThaiAddressResult>? _allAddresses;
    private static readonly SemaphoreSlim _loadLock = new(1, 1);

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
        // GroupBy → first-of-group keeps a single canonical row per province
        // (the CSV has one row per subdistrict so 7K rows collapse to 77).
        return addresses
            .GroupBy(a => a.ProvinceCode)
            .Select(g => new ThaiProvinceInfo(g.Key, g.First().ProvinceNameTh, g.First().ProvinceNameEn))
            .OrderBy(p => p.NameTh)
            .ToList();
    }

    public async Task<List<ThaiDistrictInfo>> GetDistrictsAsync(string provinceCode)
    {
        var addresses = await EnsureLoadedAsync();
        return addresses
            .Where(a => a.ProvinceCode == provinceCode)
            .GroupBy(a => a.DistrictCode)
            .Select(g => new ThaiDistrictInfo(g.Key, g.First().DistrictNameTh, g.First().DistrictNameEn, provinceCode))
            .OrderBy(d => d.NameTh)
            .ToList();
    }

    public async Task<List<ThaiSubDistrictInfo>> GetSubDistrictsAsync(string districtCode)
    {
        var addresses = await EnsureLoadedAsync();
        return addresses
            .Where(a => a.DistrictCode == districtCode)
            .GroupBy(a => a.SubDistrictCode)
            .Select(g => new ThaiSubDistrictInfo(g.Key, g.First().SubDistrictNameTh, g.First().SubDistrictNameEn, districtCode, g.First().PostalCode))
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

            _logger.LogInformation("Loading Thai address database from embedded CSV...");
            _allAddresses = LoadFromEmbeddedCsv();
            _logger.LogInformation("Loaded {Count} Thai addresses from embedded CSV", _allAddresses.Count);
            return _allAddresses;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Load Resources/ThaiAdmin.csv from the assembly's embedded
    /// resources. Same file ThaiAdminCodes uses — single source of truth.
    /// Columns: PostalCode, Province, District, SubDistrict, AddressCode.</summary>
    private List<ThaiAddressResult> LoadFromEmbeddedCsv()
    {
        var list = new List<ThaiAddressResult>();
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("ThaiAdmin.csv", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
            {
                _logger.LogError("ThaiAdmin.csv embedded resource not found — address autocomplete will be empty");
                return list;
            }

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return list;
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

            var first = true;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (first) { first = false; continue; }
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 5) continue;

                var postal     = parts[0].Trim();
                var province   = parts[1].Trim();
                var district   = parts[2].Trim();
                var subDist    = parts[3].Trim();
                var addrCode   = parts[4].Trim();

                // The CSV doesn't carry English names — fall back to the
                // Thai name for the En field. Callers using English search
                // will still hit the Thai name; better than empty.
                list.Add(new ThaiAddressResult(
                    SubDistrictCode: addrCode.Length >= 6 ? addrCode.Substring(0, 6) : addrCode,
                    SubDistrictNameTh: subDist,
                    SubDistrictNameEn: subDist,
                    DistrictCode: addrCode.Length >= 4 ? addrCode.Substring(0, 4) : addrCode,
                    DistrictNameTh: district,
                    DistrictNameEn: district,
                    ProvinceCode: addrCode.Length >= 2 ? addrCode.Substring(0, 2) : addrCode,
                    ProvinceNameTh: province,
                    ProvinceNameEn: province,
                    PostalCode: postal));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse embedded Thai address CSV");
        }
        return list;
    }
}
