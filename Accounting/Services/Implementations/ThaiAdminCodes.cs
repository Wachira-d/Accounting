using System.Reflection;

namespace Accounting.Services.Implementations;

/// <summary>
/// Thai Industrial Standards Institute (TISI) 1099 administrative codes.
/// ETDA e-Tax XML XSD constrains CityName/CitySubDivisionName/CountrySubDivisionID
/// to numeric TISI 1099 codes — free-text Thai names FAIL XSD validation.
///
/// Data source: embedded resource Resources/ThaiAdmin.csv with columns
///   PostalCode, Province, District, SubDistrict, AddressCode (8-digit)
/// where AddressCode encodes:
///   chars 0-1 = Province TISI code (e.g. "10" Bangkok, "83" Phuket)
///   chars 0-3 = District TISI code (e.g. "1027" Bangkok Bueng Kum)
///   chars 0-5 = Sub-district TISI code (e.g. "102705" Bangkok Bueng Kum Nuanchan)
///
/// Lookup priority (per Resolve* method):
///   1. Exact match by PostalCode + name (most reliable)
///   2. Exact match by Province + District + SubDistrict names
///   3. Province name lookup → fallback district/sub-district codes
///   4. Postcode prefix → province → fallback codes
///   5. Final fallback: Bangkok Phra Borommaharatchawang ("10" / "1001" / "100101")
/// </summary>
public static class ThaiAdminCodes
{
    public const string FallbackProvince = "10";
    public const string FallbackDistrict = "1001";
    public const string FallbackSubDistrict = "100101";

    /// <summary>
    /// 76-province name → 2-digit TISI code (extra to embedded CSV — covers cases where
    /// the user only provides a province name without district/sub-district detail).
    /// </summary>
    private static readonly Dictionary<string, string> ProvinceCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["กรุงเทพมหานคร"] = "10", ["กรุงเทพ"] = "10", ["กรุงเทพฯ"] = "10", ["กทม"] = "10",
        ["สมุทรปราการ"] = "11", ["นนทบุรี"] = "12", ["ปทุมธานี"] = "13",
        ["พระนครศรีอยุธยา"] = "14", ["อยุธยา"] = "14", ["อ่างทอง"] = "15",
        ["ลพบุรี"] = "16", ["สิงห์บุรี"] = "17", ["ชัยนาท"] = "18",
        ["สระบุรี"] = "19", ["ชลบุรี"] = "20", ["ระยอง"] = "21",
        ["จันทบุรี"] = "22", ["ตราด"] = "23", ["ฉะเชิงเทรา"] = "24",
        ["ปราจีนบุรี"] = "25", ["นครนายก"] = "26", ["สระแก้ว"] = "27",
        ["นครราชสีมา"] = "30", ["โคราช"] = "30", ["บุรีรัมย์"] = "31",
        ["สุรินทร์"] = "32", ["ศรีสะเกษ"] = "33", ["อุบลราชธานี"] = "34",
        ["ยโสธร"] = "35", ["ชัยภูมิ"] = "36", ["อำนาจเจริญ"] = "37",
        ["บึงกาฬ"] = "38", ["หนองบัวลำภู"] = "39", ["ขอนแก่น"] = "40",
        ["อุดรธานี"] = "41", ["เลย"] = "42", ["หนองคาย"] = "43",
        ["มหาสารคาม"] = "44", ["ร้อยเอ็ด"] = "45", ["กาฬสินธุ์"] = "46",
        ["สกลนคร"] = "47", ["นครพนม"] = "48", ["มุกดาหาร"] = "49",
        ["เชียงใหม่"] = "50", ["ลำพูน"] = "51", ["ลำปาง"] = "52",
        ["อุตรดิตถ์"] = "53", ["แพร่"] = "54", ["น่าน"] = "55",
        ["พะเยา"] = "56", ["เชียงราย"] = "57", ["แม่ฮ่องสอน"] = "58",
        ["นครสวรรค์"] = "60", ["อุทัยธานี"] = "61", ["กำแพงเพชร"] = "62",
        ["ตาก"] = "63", ["สุโขทัย"] = "64", ["พิษณุโลก"] = "65",
        ["พิจิตร"] = "66", ["เพชรบูรณ์"] = "67",
        ["ราชบุรี"] = "70", ["กาญจนบุรี"] = "71", ["สุพรรณบุรี"] = "72",
        ["นครปฐม"] = "73", ["สมุทรสาคร"] = "74", ["สมุทรสงคราม"] = "75",
        ["เพชรบุรี"] = "76", ["ประจวบคีรีขันธ์"] = "77",
        ["นครศรีธรรมราช"] = "80", ["กระบี่"] = "81", ["พังงา"] = "82",
        ["ภูเก็ต"] = "83", ["สุราษฎร์ธานี"] = "84", ["ระนอง"] = "85",
        ["ชุมพร"] = "86", ["สงขลา"] = "90", ["สตูล"] = "91",
        ["ตรัง"] = "92", ["พัทลุง"] = "93", ["ปัตตานี"] = "94",
        ["ยะลา"] = "95", ["นราธิวาส"] = "96",
    };

    public record AdminEntry(
        string PostalCode,
        string Province,
        string District,
        string SubDistrict,
        string AddressCode)
    {
        public string ProvinceCode => AddressCode.Length >= 2 ? AddressCode.Substring(0, 2) : "10";
        public string DistrictCode => AddressCode.Length >= 4 ? AddressCode.Substring(0, 4) : "1001";
        public string SubDistrictCode => AddressCode.Length >= 6 ? AddressCode.Substring(0, 6) : "100101";
    }

    private static readonly Lazy<List<AdminEntry>> _entries = new(LoadFromEmbedded);
    private static readonly Lazy<Dictionary<(string PostCode, string SubDistrict), AdminEntry>> _byPostcodeSubdist =
        new(() => _entries.Value
            .GroupBy(e => (e.PostalCode, NormalizeName(e.SubDistrict)))
            .ToDictionary(g => g.Key, g => g.First()));
    private static readonly Lazy<Dictionary<(string Province, string District, string SubDistrict), AdminEntry>> _byNames =
        new(() => _entries.Value
            .GroupBy(e => (NormalizeName(e.Province), NormalizeName(e.District), NormalizeName(e.SubDistrict)))
            .ToDictionary(g => g.Key, g => g.First()));

    private static List<AdminEntry> LoadFromEmbedded()
    {
        var list = new List<AdminEntry>();
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // Resource name format: <DefaultNamespace>.Resources.ThaiAdmin.csv
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("ThaiAdmin.csv", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) return list;

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return list;
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            var first = true;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (first) { first = false; continue; } // header
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 5) continue;
                list.Add(new AdminEntry(
                    PostalCode: parts[0].Trim(),
                    Province: parts[1].Trim(),
                    District: parts[2].Trim(),
                    SubDistrict: parts[3].Trim(),
                    AddressCode: parts[4].Trim()));
            }
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Failed to load Thai admin codes CSV — system will use fallback codes: {ex.Message}"); }
        return list;
    }

    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        return name.Trim()
            .Replace("จังหวัด", "").Replace("จ.", "")
            .Replace("อำเภอ", "").Replace("อ.", "")
            .Replace("ตำบล", "").Replace("ต.", "")
            .Replace("เขต", "").Replace("แขวง", "")
            .Trim().TrimEnd('ฯ', '.', ',', ' ');
    }

    /// <summary>
    /// Look up the full TISI entry by best-available data. Returns null if no match.
    /// </summary>
    public static AdminEntry? Lookup(string? provinceName, string? districtName,
        string? subDistrictName, string? postcode)
    {
        // Priority 1: postcode + sub-district (most specific)
        var sd = NormalizeName(subDistrictName);
        if (!string.IsNullOrEmpty(postcode) && !string.IsNullOrEmpty(sd))
        {
            if (_byPostcodeSubdist.Value.TryGetValue((postcode, sd), out var hit)) return hit;
        }
        // Priority 2: full name match
        var prov = NormalizeName(provinceName);
        var dist = NormalizeName(districtName);
        if (!string.IsNullOrEmpty(prov) && !string.IsNullOrEmpty(dist) && !string.IsNullOrEmpty(sd))
        {
            if (_byNames.Value.TryGetValue((prov, dist, sd), out var hit)) return hit;
        }
        // Priority 3: postcode-only first match (gives correct province at least)
        if (!string.IsNullOrEmpty(postcode))
        {
            var byPc = _entries.Value.FirstOrDefault(e => e.PostalCode == postcode);
            if (byPc != null) return byPc;
        }
        return null;
    }

    /// <summary>2-digit province code from name → CSV → prefix-of-postcode → fallback.</summary>
    public static string ResolveProvinceCode(string? provinceName, string? postcode)
    {
        var byName = LookupProvinceCodeByName(provinceName);
        if (byName != null) return byName;

        if (!string.IsNullOrEmpty(postcode) && postcode.Length >= 2 && postcode.All(char.IsDigit))
        {
            var prefix = postcode.Substring(0, 2);
            if (ProvinceCodes.Values.Contains(prefix)) return prefix;
        }
        return FallbackProvince;
    }

    /// <summary>4-digit district code via full TISI lookup, or constructed from province + "01".</summary>
    public static string ResolveDistrictCode(string? districtName, string? subDistrictName,
        string? postcode, string provinceCode, string? provinceName = null)
    {
        var hit = Lookup(provinceName, districtName, subDistrictName, postcode);
        if (hit != null) return hit.DistrictCode;
        return provinceCode + "01"; // safe placeholder (each province has a "01" district)
    }

    /// <summary>6-digit sub-district code via full TISI lookup, or district + "01".</summary>
    public static string ResolveSubDistrictCode(string? subDistrictName, string? districtName,
        string? postcode, string districtCode, string? provinceName = null)
    {
        var hit = Lookup(provinceName, districtName, subDistrictName, postcode);
        if (hit != null) return hit.SubDistrictCode;
        return districtCode + "01";
    }

    private static string? LookupProvinceCodeByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = NormalizeName(name);
        return ProvinceCodes.TryGetValue(normalized, out var code) ? code : null;
    }
}
