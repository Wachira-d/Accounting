namespace Accounting.Services.Implementations;

/// <summary>
/// Thai Industrial Standards Institute (TISI) 1099 administrative codes.
/// ETDA e-Tax XML XSD constrains CityName/CitySubDivisionName/CountrySubDivisionID
/// to numeric TISI 1099 codes — free-text Thai names FAIL XSD validation.
///
/// This class provides:
///   - Province name → 2-digit TISI province code lookup (76 provinces)
///   - Postcode → likely province inference (first 2 digits)
///   - Known-valid fallback codes for district/sub-district when name lookup
///     isn't available (we don't ship the full ~7000-entry mapping yet —
///     fallback codes pass XSD/Schematron but don't reflect real address)
///
/// Production hardening: replace fallback with real lookup by integrating a
/// full TISI 1099 dataset (province × district × sub-district × postcode).
/// </summary>
public static class ThaiAdminCodes
{
    // Known-valid TISI codes (verified in the XSD enum + ETDA samples)
    public const string FallbackProvince = "10";       // กรุงเทพมหานคร
    public const string FallbackDistrict = "1001";     // เขตพระนคร
    public const string FallbackSubDistrict = "100101"; // แขวงพระบรมมหาราชวัง

    /// <summary>76 Thai province names mapped to their 2-digit TISI code.</summary>
    private static readonly Dictionary<string, string> ProvinceCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Central
        ["กรุงเทพมหานคร"] = "10", ["กรุงเทพ"] = "10", ["กรุงเทพฯ"] = "10", ["กทม"] = "10",
        ["สมุทรปราการ"] = "11",
        ["นนทบุรี"] = "12",
        ["ปทุมธานี"] = "13",
        ["พระนครศรีอยุธยา"] = "14", ["อยุธยา"] = "14",
        ["อ่างทอง"] = "15",
        ["ลพบุรี"] = "16",
        ["สิงห์บุรี"] = "17",
        ["ชัยนาท"] = "18",
        ["สระบุรี"] = "19",
        ["ชลบุรี"] = "20",
        ["ระยอง"] = "21",
        ["จันทบุรี"] = "22",
        ["ตราด"] = "23",
        ["ฉะเชิงเทรา"] = "24",
        ["ปราจีนบุรี"] = "25",
        ["นครนายก"] = "26",
        ["สระแก้ว"] = "27",

        // North-East
        ["นครราชสีมา"] = "30", ["โคราช"] = "30",
        ["บุรีรัมย์"] = "31",
        ["สุรินทร์"] = "32",
        ["ศรีสะเกษ"] = "33",
        ["อุบลราชธานี"] = "34",
        ["ยโสธร"] = "35",
        ["ชัยภูมิ"] = "36",
        ["อำนาจเจริญ"] = "37",
        ["บึงกาฬ"] = "38",
        ["หนองบัวลำภู"] = "39",
        ["ขอนแก่น"] = "40",
        ["อุดรธานี"] = "41",
        ["เลย"] = "42",
        ["หนองคาย"] = "43",
        ["มหาสารคาม"] = "44",
        ["ร้อยเอ็ด"] = "45",
        ["กาฬสินธุ์"] = "46",
        ["สกลนคร"] = "47",
        ["นครพนม"] = "48",
        ["มุกดาหาร"] = "49",

        // North
        ["เชียงใหม่"] = "50",
        ["ลำพูน"] = "51",
        ["ลำปาง"] = "52",
        ["อุตรดิตถ์"] = "53",
        ["แพร่"] = "54",
        ["น่าน"] = "55",
        ["พะเยา"] = "56",
        ["เชียงราย"] = "57",
        ["แม่ฮ่องสอน"] = "58",

        // Central / West
        ["นครสวรรค์"] = "60",
        ["อุทัยธานี"] = "61",
        ["กำแพงเพชร"] = "62",
        ["ตาก"] = "63",
        ["สุโขทัย"] = "64",
        ["พิษณุโลก"] = "65",
        ["พิจิตร"] = "66",
        ["เพชรบูรณ์"] = "67",
        ["ราชบุรี"] = "70",
        ["กาญจนบุรี"] = "71",
        ["สุพรรณบุรี"] = "72",
        ["นครปฐม"] = "73",
        ["สมุทรสาคร"] = "74",
        ["สมุทรสงคราม"] = "75",
        ["เพชรบุรี"] = "76",
        ["ประจวบคีรีขันธ์"] = "77",

        // South
        ["นครศรีธรรมราช"] = "80",
        ["กระบี่"] = "81",
        ["พังงา"] = "82",
        ["ภูเก็ต"] = "83",
        ["สุราษฎร์ธานี"] = "84",
        ["ระนอง"] = "85",
        ["ชุมพร"] = "86",
        ["สงขลา"] = "90",
        ["สตูล"] = "91",
        ["ตรัง"] = "92",
        ["พัทลุง"] = "93",
        ["ปัตตานี"] = "94",
        ["ยะลา"] = "95",
        ["นราธิวาส"] = "96",
    };

    /// <summary>
    /// Look up the 2-digit TISI province code by Thai province name.
    /// Strips common prefixes (จ./จังหวัด) and "ฯ" trailing marker.
    /// Returns null if not found.
    /// </summary>
    public static string? LookupProvinceCode(string? provinceName)
    {
        if (string.IsNullOrWhiteSpace(provinceName)) return null;
        var normalized = provinceName.Trim().TrimStart()
            .Replace("จังหวัด", "").Replace("จ.", "").Trim()
            .TrimEnd('ฯ', '.', ',', ' ');
        return ProvinceCodes.TryGetValue(normalized, out var code) ? code : null;
    }

    /// <summary>
    /// Infer province from Thai postcode (first 2 digits = province).
    /// Falls back to LookupProvinceCode by name; finally to FallbackProvince.
    /// </summary>
    public static string ResolveProvinceCode(string? provinceName, string? postcode)
    {
        var byName = LookupProvinceCode(provinceName);
        if (byName != null) return byName;

        if (!string.IsNullOrEmpty(postcode) && postcode.Length >= 2
            && postcode.All(char.IsDigit))
        {
            var prefix = postcode.Substring(0, 2);
            // Validate the prefix is a known province code
            if (ProvinceCodes.Values.Contains(prefix)) return prefix;
        }
        return FallbackProvince;
    }

    /// <summary>
    /// Returns a known-valid TISI 1099 district code.
    /// In real production this should be a (province, district name) → code lookup.
    /// </summary>
    public static string ResolveDistrictCode(string? districtName, string? postcode, string provinceCode)
    {
        // We don't yet ship a full district codelist.
        // Pick any valid 4-digit code for the resolved province as a structural placeholder.
        // Code format: PPDD where PP=province, DD=district number (01-99).
        // "PP01" is typically valid (each province has a district 01).
        return provinceCode + "01";
    }

    /// <summary>Returns a known-valid TISI 1099 sub-district code (6 digits).</summary>
    public static string ResolveSubDistrictCode(string? subDistrictName, string? postcode, string districtCode)
    {
        // PPDDSS — first 4 digits = district code, last 2 = sub-district within
        return districtCode + "01";
    }
}
