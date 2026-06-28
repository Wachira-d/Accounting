using System.Text.RegularExpressions;
using Accounting.Models.DTOs.Document;

namespace Accounting.Services.Implementations;

/// <summary>
/// Parses a free-text Thai address into structured fields per ETDA Schematron.
/// Heuristic — handles common formats but is not 100% accurate; user can adjust
/// the parsed values in the form.
///
/// Examples it handles:
///   "123/45 ถนนสุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพฯ 10110"
///   "99 หมู่ 5 ต.บางพลี อ.บางพลี จ.สมุทรปราการ 10540"
///   "อาคาร XYZ ชั้น 3 ห้อง 301 เลขที่ 88 ถ.พระราม 4 แขวงสีลม เขตบางรัก กรุงเทพฯ 10500"
/// </summary>
public static class ThaiAddressParser
{
    private static readonly Regex PostcodeRegex = new(@"\b(\d{5})\b", RegexOptions.Compiled);
    private static readonly Regex BuildingNumberRegex = new(
        @"^(?:เลขที่\s*)?(\d+(?:[/-]\d+)*)\s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // หยุดจับเมื่อ token ถัดไปเป็น marker (ถนน/ตำบล/อำเภอ/จังหวัด/หมู่) — เดิม
    // จับเลย marker ไปทำให้ buildingName กลืน "ถนน บางนาตราด ตำบล" เข้ามา.
    private static readonly Regex BuildingNameRegex = new(
        @"(?:อาคาร|Building)\s+([^\s,]+(?:\s+(?!ถนน|ถ\.|ตำบล|แขวง|อำเภอ|เขต|จังหวัด|ต\.|อ\.|จ\.|หมู่|ม\.)[^\s,]+){0,3})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // หมู่ที่ — matches "หมู่ 5", "หมู่ที่ 5", "ม.5", "ม. 5", and Thai-numeral
    // variants like "หมู่ ๕". The captured group is the number itself.
    private static readonly Regex MooRegex = new(
        @"(?:หมู่ที่|หมู่|ม\.)\s*([0-9๐-๙]+)",
        RegexOptions.Compiled);

    // Address keyword patterns — match Thai administrative-area markers
    private static readonly Regex SubDistrictRegex = new(
        @"(?:แขวง|ตำบล|ต\.)\s*([^\s,]+(?:\s+[^\s,เขตอำเภอจังหวัด]+)?)",
        RegexOptions.Compiled);
    private static readonly Regex DistrictRegex = new(
        @"(?:เขต|อำเภอ|อ\.)\s*([^\s,]+(?:\s+[^\s,จังหวัด]+)?)",
        RegexOptions.Compiled);
    private static readonly Regex ProvinceRegex = new(
        @"(?:จังหวัด|จ\.)\s*([^\s,\d]+)",
        RegexOptions.Compiled);
    // หยุดจับเมื่อ token ถัดไปเป็น marker เขตปกครอง — เดิมจับ "บางนาตราด ตำบล
    // บางนา" รวมชื่อตำบลเข้ามาในชื่อถนน.
    private static readonly Regex StreetRegex = new(
        @"(?:ถนน|ถ\.|Road|Rd\.?)\s*([^\s,]+(?:\s+(?!ตำบล|แขวง|อำเภอ|เขต|จังหวัด|ต\.|อ\.|จ\.|หมู่|ม\.)[^\s,]+){0,2})",
        RegexOptions.Compiled);

    public static ParsedAddressResponse Parse(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new ParsedAddressResponse(null, null, null, null, null, null, null);

        var text = address.Trim();
        string? buildingNumber = null, buildingName = null, street = null;
        string? subDistrict = null, district = null, province = null, postcode = null;
        string? moo = null;

        var pcMatch = PostcodeRegex.Match(text);
        if (pcMatch.Success) postcode = pcMatch.Groups[1].Value;

        var bnMatch = BuildingNumberRegex.Match(text);
        if (bnMatch.Success) buildingNumber = bnMatch.Groups[1].Value;

        var bldgMatch = BuildingNameRegex.Match(text);
        if (bldgMatch.Success) buildingName = bldgMatch.Groups[1].Value.Trim();

        var streetMatch = StreetRegex.Match(text);
        if (streetMatch.Success) street = streetMatch.Groups[1].Value.Trim().TrimEnd(',', ' ');

        var mooMatch = MooRegex.Match(text);
        if (mooMatch.Success) moo = NormalizeThaiDigits(mooMatch.Groups[1].Value);

        var subDistMatch = SubDistrictRegex.Match(text);
        if (subDistMatch.Success) subDistrict = CleanArea(subDistMatch.Groups[1].Value);

        var distMatch = DistrictRegex.Match(text);
        if (distMatch.Success) district = CleanArea(distMatch.Groups[1].Value);

        var provMatch = ProvinceRegex.Match(text);
        if (provMatch.Success)
        {
            province = CleanArea(provMatch.Groups[1].Value);
        }
        else
        {
            // Special case: "กรุงเทพฯ" or "กรุงเทพมหานคร" without จ. prefix
            if (text.Contains("กรุงเทพมหานคร")) province = "กรุงเทพมหานคร";
            else if (text.Contains("กรุงเทพฯ") || text.Contains("กทม")) province = "กรุงเทพมหานคร";
        }

        return new ParsedAddressResponse(
            buildingNumber, buildingName, street,
            subDistrict, district, province, postcode, moo);
    }

    /// <summary>ดึง "ส่วนหัว" ของที่อยู่ = ทุกอย่างก่อน marker เขตปกครองแรก
    /// (ตำบล/แขวง/อำเภอ/เขต/จังหวัด) — บ้านเลขที่/ห้อง/ชั้น/อาคาร/ซอย/ถนน — โดย
    /// ตัด buildingNumber + วลีหมู่ที่ ออก (เก็บแยกในฟิลด์ของตัวเอง). ใช้เก็บลง
    /// StreetName เพื่อรักษารายละเอียดที่ field-parser รายฟิลด์จับไม่ครบ (ห้อง/
    /// ชั้น/ชื่ออาคาร) ไม่ให้หายตอน render เป็น structured address. คืน null เมื่อ
    /// ไม่เหลืออะไร.</summary>
    public static string? ExtractStreetHead(string? freeText, string? buildingNumber, string? moo)
    {
        if (string.IsNullOrWhiteSpace(freeText)) return null;
        var t = freeText.Trim();
        int cut = -1;
        // marker เขตปกครอง + Bangkok ที่เขียนตรง ๆ ไม่มี "จังหวัด" นำ (กรุงเทพฯ/กทม)
        foreach (var m in new[] { "ตำบล", "แขวง", "อำเภอ", "เขต", "จังหวัด", "กรุงเทพ", "กทม" })
        {
            var idx = t.IndexOf(m, StringComparison.Ordinal);
            if (idx >= 0 && (cut < 0 || idx < cut)) cut = idx;
        }
        var head = (cut >= 0 ? t[..cut] : t).Trim().Trim(',').Trim();
        if (!string.IsNullOrWhiteSpace(buildingNumber)
            && head.StartsWith(buildingNumber!, StringComparison.Ordinal))
            head = head[buildingNumber!.Length..].Trim();
        if (!string.IsNullOrWhiteSpace(moo))
            head = Regex.Replace(head, @"(?:หมู่ที่|หมู่|ม\.)\s*[0-9๐-๙]+", " ").Trim();
        head = Regex.Replace(head, @"\s{2,}", " ").Trim().Trim(',').Trim();
        return string.IsNullOrWhiteSpace(head) ? null : head;
    }

    private static string CleanArea(string raw) =>
        raw.Trim().TrimEnd('ฯ', ',', '.', ' ');

    /// <summary>Map Thai numerals (๐-๙) to Arabic so the form receives "5"
    /// not "๕" — keeps downstream e-Tax XML serializers happy.</summary>
    private static string NormalizeThaiDigits(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c >= '๐' && c <= '๙') sb.Append((char)('0' + (c - '๐')));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Compose a free-text Address line from structured fields (for display)</summary>
    public static string Compose(
        string? buildingNumber, string? buildingName, string? streetName,
        string? subDistrict, string? district, string? province, string? postalCode,
        string? moo = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(buildingNumber)) parts.Add(buildingNumber);
        if (!string.IsNullOrWhiteSpace(buildingName)) parts.Add($"อาคาร{buildingName}");
        if (!string.IsNullOrWhiteSpace(moo)) parts.Add($"หมู่ {moo}");
        if (!string.IsNullOrWhiteSpace(streetName)) parts.Add($"ถ.{streetName}");
        if (!string.IsNullOrWhiteSpace(subDistrict))
        {
            var prefix = (province ?? "") == "กรุงเทพมหานคร" ? "แขวง" : "ต.";
            parts.Add($"{prefix}{subDistrict}");
        }
        if (!string.IsNullOrWhiteSpace(district))
        {
            var prefix = (province ?? "") == "กรุงเทพมหานคร" ? "เขต" : "อ.";
            parts.Add($"{prefix}{district}");
        }
        if (!string.IsNullOrWhiteSpace(province)) parts.Add($"จ.{province}");
        if (!string.IsNullOrWhiteSpace(postalCode)) parts.Add(postalCode);
        return string.Join(' ', parts);
    }
}
