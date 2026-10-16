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
    //
    // ⚠️ ชื่อตำบล/แขวง/อำเภอ/เขต = <b>token เดียว</b> (ชื่อเขตปกครองไทยไม่มีช่องว่าง) — ประวัติ:
    //   • เดิม `[^\s,]+(?:\s+[^\s,เขตอำเภอจังหวัด]+)?` — character class (ห้ามตัวอักษร จ ั ง ห ว ด ทีละตัว) ไม่ใช่ "ห้ามคำ" ⇒
    //     "เขตคลองเตย กรุงเทพฯ" ได้ "คลองเตย กรุ" · แต่บังเอิญกัน token ที่สองส่วนใหญ่ไว้ (มี เ/อ/จ ฯลฯ)
    //   • 7084919a เปลี่ยนเป็น negative lookahead ทั้งคำ ⇒ token ที่สองเปิดกว้าง ⇒ อำเภอ/เขตที่<b>ไม่มีคำนำ</b>ถูกต่อเข้าตำบล
    //     ("1 แขวงลาดยาว จตุจักร กรุงเทพฯ" ⇒ แขวง "ลาดยาว จตุจักร" · "5 ต.ช้างเผือก เมือง เชียงใหม่" ⇒ "ช้างเผือก เมือง" แล้ว
    //     formatter พิมพ์ซ้ำ — ฝ่ายค้าน ภ.ง.ด.1 P2-1)
    //   • ตอนนี้: token เดียว · จังหวัดที่ไม่มี จ. นำ = ชื่อ 77 จังหวัดตัวสุดท้าย<b>หลัง</b> marker เขตปกครอง (FindProvinceAfter) ·
    //     อำเภอ/เขตไม่มีคำนำ = token ที่อยู่ระหว่างตำบลกับจังหวัด/รหัสไปรษณีย์พอดี (InferUndecoratedDistrict)
    private const string AreaStop = @"(?!แขวง|ตำบล|ต\.|เขต|อำเภอ|อ\.|จังหวัด|จ\.|กรุงเทพ|กทม|ถนน|ถ\.|ซอย|ซ\.|หมู่|ม\.|โทร|Tel|ประเทศ|[0-9๐-๙])";
    /// <summary>คำนี้<b>ไม่ใช่</b> marker/ตัวเลข (AreaStop เป็น negative lookahead ⇒ IsMatch = true เมื่อไม่ใช่คำหยุด)</summary>
    private static readonly Regex AreaStopRegex = new("^" + AreaStop, RegexOptions.Compiled);
    private static readonly Regex SubDistrictRegex = new(
        @"(?:แขวง|ตำบล|ต\.)\s*([^\s,]+)",
        RegexOptions.Compiled);
    private static readonly Regex DistrictRegex = new(
        @"(?:เขต|อำเภอ|อ\.)\s*([^\s,]+)",
        RegexOptions.Compiled);
    private static readonly Regex ProvinceRegex = new(
        @"(?:จังหวัด|จ\.)\s*([^\s,\d]+)",
        RegexOptions.Compiled);

    // 77 จังหวัด — ชื่อทุกจังหวัดเป็น token เดียว (ไม่มีช่องว่าง) จึง match
    // แบบ exact token ได้แม่นยำ. ใช้เป็น fallback เมื่อที่อยู่ไม่มีคำนำหน้า
    // "จ." (เคสจริง: contact/company ที่ import/OCR เก็บที่อยู่เป็นก้อนเดียว
    // "44 หมู่ 9 หนองเทียง พนัสนิคม ชลบุรี 20140" → parser เดิมแยกไม่ได้เลย
    // เพราะทุก regex ต้องมี marker นำ).
    private static readonly HashSet<string> ProvinceNames = new()
    {
        "กรุงเทพมหานคร", "กระบี่", "กาญจนบุรี", "กาฬสินธุ์", "กำแพงเพชร", "ขอนแก่น",
        "จันทบุรี", "ฉะเชิงเทรา", "ชลบุรี", "ชัยนาท", "ชัยภูมิ", "ชุมพร", "เชียงราย",
        "เชียงใหม่", "ตรัง", "ตราด", "ตาก", "นครนายก", "นครปฐม", "นครพนม",
        "นครราชสีมา", "นครศรีธรรมราช", "นครสวรรค์", "นนทบุรี", "นราธิวาส", "น่าน",
        "บึงกาฬ", "บุรีรัมย์", "ปทุมธานี", "ประจวบคีรีขันธ์", "ปราจีนบุรี", "ปัตตานี",
        "พระนครศรีอยุธยา", "พะเยา", "พังงา", "พัทลุง", "พิจิตร", "พิษณุโลก",
        "เพชรบุรี", "เพชรบูรณ์", "แพร่", "ภูเก็ต", "มหาสารคาม", "มุกดาหาร",
        "แม่ฮ่องสอน", "ยโสธร", "ยะลา", "ร้อยเอ็ด", "ระนอง", "ระยอง", "ราชบุรี",
        "ลพบุรี", "ลำปาง", "ลำพูน", "เลย", "ศรีสะเกษ", "สกลนคร", "สงขลา", "สตูล",
        "สมุทรปราการ", "สมุทรสงคราม", "สมุทรสาคร", "สระแก้ว", "สระบุรี", "สิงห์บุรี",
        "สุโขทัย", "สุพรรณบุรี", "สุราษฎร์ธานี", "สุรินทร์", "หนองคาย", "หนองบัวลำภู",
        "อ่างทอง", "อำนาจเจริญ", "อุดรธานี", "อุตรดิตถ์", "อุทัยธานี", "อุบลราชธานี",
    };
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

        // รหัสไปรษณีย์: ตัวตัดสินเดียวของระบบ (ContactDataHygiene.PostalCodeOf) — เดิม regex ของไฟล์นี้หยิบ "เลข 5 หลักตัวแรก"
        // ⇒ "บ้านเลขที่ 12345 …" กลายเป็นรหัสไปรษณีย์ แล้ว formatter ย้ายบ้านเลขที่ไปไว้ท้ายที่อยู่ (ฝ่ายค้าน ภ.ง.ด.1 P2-2)
        postcode = Accounting.Helpers.ContactDataHygiene.PostalCodeOf(null, text);

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

        // ตำแหน่งท้ายสุดของ marker เขตปกครองที่จับได้ — จังหวัด/อำเภอที่ไม่มีคำนำต้องอยู่ "หลัง" จุดนี้ (ลำดับที่อยู่ไทย ตำบล → อำเภอ → จังหวัด)
        var areaEnd = Math.Max(
            subDistMatch.Success ? subDistMatch.Index + subDistMatch.Length : -1,
            distMatch.Success ? distMatch.Index + distMatch.Length : -1);

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
            // ชื่อจังหวัดไม่มี จ. นำ หลังตำบล/อำเภอ ("อำเภอพนัสนิคม ชลบุรี") — เฉพาะเมื่อมี marker เขตปกครองแล้ว
            // (ไม่มี marker เลย = fallback ข้างล่างจัดการทั้งชุด)
            else if (areaEnd >= 0) province = FindProvinceAfter(text, areaEnd);
        }

        // อำเภอ/เขตที่ไม่มีคำนำ ("1 แขวงลาดยาว จตุจักร กรุงเทพฯ" · "5 ต.ช้างเผือก เมือง เชียงใหม่") — token ถัดจากตำบลพอดี
        // และตามด้วยจังหวัด/รหัสไปรษณีย์/จบข้อความ · ไม่ใช่ token ที่เป็น marker/ตัวเลข/ชื่อจังหวัด (เดิมก่อน 7084919a = null ·
        // 7084919a = ต่อเข้าชื่อตำบล) · มี marker อำเภอ/เขตแล้วไม่เดา
        if (district == null && subDistMatch.Success)
            district = InferUndecoratedDistrict(text, subDistMatch.Index + subDistMatch.Length, postcode);

        // Fallback: ที่อยู่ไม่มีคำนำหน้าเลย (ไม่มี ต./อ./จ./แขวง/เขต) — เคสจริงจาก
        // contact/company ที่ import/OCR เก็บเป็นก้อนเดียว. ยึด "ชื่อจังหวัด" ที่รู้จัก
        // (77 จังหวัด) เป็นหลัก แล้วอนุมานอำเภอ/ตำบลจากลำดับ token มาตรฐานที่อยู่ไทย:
        //   ... <ตำบล> <อำเภอ> <จังหวัด> <รหัสไปรษณีย์>
        // ทำงานเฉพาะเมื่อ "ไม่พบ marker ใด ๆ" เพื่อไม่รบกวนที่อยู่ที่มีคำนำหน้าถูกอยู่แล้ว.
        var noMarkers = !subDistMatch.Success && !distMatch.Success && !provMatch.Success
            && province == null;
        if (noMarkers)
        {
            // ตัดส่วนที่รู้แน่ (บ้านเลขที่/หมู่/รหัสไปรษณีย์/ถนน/เลขที่/อาคาร) ออก
            // เหลือเฉพาะ token ที่เป็นชื่อเขตปกครอง แล้วยึดจังหวัดจากท้าย.
            var cleaned = text;
            cleaned = Regex.Replace(cleaned, @"(?:อาคาร|Building)\s+\S+", " ");
            cleaned = Regex.Replace(cleaned, @"(?:ถนน|ถ\.|Road|Rd\.?)\s*\S+", " ");
            cleaned = Regex.Replace(cleaned, @"(?:หมู่ที่|หมู่|ม\.)\s*[0-9๐-๙]+", " ");
            cleaned = Regex.Replace(cleaned, @"(?:ซอย|ซ\.)\s*\S+", " ");
            cleaned = Regex.Replace(cleaned, @"เลขที่", " ");
            cleaned = Regex.Replace(cleaned, @"\b\d[\d/\-]*\b", " ");   // เลขทุกชุด (บ้านเลขที่/ไปรษณีย์)
            var tokens = cleaned.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().TrimEnd('ฯ'))
                .Where(t => t.Length > 0)
                .ToList();

            // จังหวัด = token สุดท้ายที่ตรงกับรายชื่อ 77 จังหวัด
            var provIdx = -1;
            for (var i = tokens.Count - 1; i >= 0; i--)
                if (ProvinceNames.Contains(tokens[i])) { provIdx = i; break; }

            if (provIdx >= 0)
            {
                province = tokens[provIdx];
                // อำเภอ = token ก่อนจังหวัด, ตำบล = token ก่อนอำเภอ (ลำดับ TH มาตรฐาน)
                if (provIdx - 1 >= 0) district = tokens[provIdx - 1];
                if (provIdx - 2 >= 0) subDistrict = tokens[provIdx - 2];
            }
            else if (!string.IsNullOrWhiteSpace(postcode) && tokens.Count > 0)
            {
                // ไม่มีแม้แต่ชื่อจังหวัดในข้อความ (เช่น "44 หมู่ 9 หนองเหียง พนัสนิคม
                // 20140") — รหัสไปรษณีย์ชี้จังหวัดได้แน่นอนจากทะเบียนราชการ ไม่ต้องเดา
                //
                // ⚠ ลำดับการลองสำคัญมาก: เมื่อไม่มีชื่อจังหวัด ลำดับที่อยู่ไทยคือ
                // "... <ตำบล> <อำเภอ> <รหัส>" → **token รองสุดท้ายคือตำบล** ต้องลอง
                // ก่อน. ถ้าลอง token สุดท้าย (= อำเภอ) ก่อน จะพลาดในอำเภอที่มีตำบล
                // ชื่อเดียวกัน — เคสจริง: อ.พนัสนิคม มี ต.พนัสนิคม อยู่ด้วย (20140)
                // → จะได้ "ต.พนัสนิคม" แล้วชื่อตำบลจริง (หนองเหียง) หายไปเงียบ ๆ
                ThaiAdminCodes.AdminEntry? hit = null;
                if (tokens.Count > 1) hit = ThaiAdminCodes.Lookup(null, null, tokens[^2], postcode);
                // เขียนแค่ตำบลไม่มีอำเภอ ("... หนองเหียง 20140") → token ท้ายคือตำบล
                hit ??= ThaiAdminCodes.Lookup(null, null, tokens[^1], postcode);
                if (hit != null)
                {
                    // ตรงทะเบียน → ใช้ชื่อทางการทั้งชุด (กันสะกดเพี้ยนบนเอกสารด้วย)
                    province = hit.Province;
                    district = hit.District;
                    subDistrict = hit.SubDistrict;
                }
                else
                {
                    // ไม่ตรงทะเบียนเลย — ยึดเฉพาะ "จังหวัด" จากรหัสไปรษณีย์
                    // (deterministic) ส่วนตำบล/อำเภอ ใช้ลำดับ token ตามปกติ
                    var byPost = ThaiAdminCodes.Lookup(null, null, null, postcode);
                    if (byPost != null)
                    {
                        province = byPost.Province;
                        district = tokens[^1];
                        if (tokens.Count > 1) subDistrict = tokens[^2];
                    }
                }
            }
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
        // คำนำแบบย่อ ต./อ./จ. ที่ต้นคำ (ฝ่ายค้าน ภ.ง.ด.1 P2-1: เดิมไม่อยู่ในรายการ ⇒ "5 ต.ช้างเผือก เมือง เชียงใหม่" ทั้งก้อนกลายเป็น
        // "ส่วนหัว" แล้ว formatter พิมพ์ตำบล/อำเภอซ้ำต่อท้าย) · ต้องอยู่ต้นข้อความหรือหลังช่องว่าง (ไม่ตัดกลางคำ)
        var abbr = AbbrevAreaMarker.Match(t);
        if (abbr.Success && (cut < 0 || abbr.Index < cut)) cut = abbr.Index;
        var head = (cut >= 0 ? t[..cut] : t).Trim().Trim(',').Trim();
        if (!string.IsNullOrWhiteSpace(buildingNumber)
            && head.StartsWith(buildingNumber!, StringComparison.Ordinal))
            head = head[buildingNumber!.Length..].Trim();
        if (!string.IsNullOrWhiteSpace(moo))
            head = Regex.Replace(head, @"(?:หมู่ที่|หมู่|ม\.)\s*[0-9๐-๙]+", " ").Trim();
        head = Regex.Replace(head, @"\s{2,}", " ").Trim().Trim(',').Trim();
        return string.IsNullOrWhiteSpace(head) ? null : head;
    }

    /// <summary>ชื่อนี้เป็นหนึ่งใน 77 จังหวัดไหม (ตัด ฯ . , ท้าย) — ตัวเดียวที่ <c>ContactDataHygiene.PostalCodeOf</c> ใช้ตัดสิน "ตามหลังจังหวัด"</summary>
    internal static bool IsProvinceName(string? token)
        => !string.IsNullOrWhiteSpace(token) && ProvinceNames.Contains(token.Trim().TrimEnd('ฯ', ',', '.'));

    private static readonly Regex AbbrevAreaMarker = new(@"(?<=^|\s)(?:ต|อ|จ)\.", RegexOptions.Compiled);

    private static readonly Regex Tokens = new(@"[^\s,]+", RegexOptions.Compiled);

    /// <summary>ชื่อจังหวัด (77 จังหวัด) ตัวสุดท้ายที่อยู่หลังตำแหน่ง <paramref name="from"/> · ไม่พบ = null</summary>
    private static string? FindProvinceAfter(string text, int from)
    {
        string? hit = null;
        foreach (Match m in Tokens.Matches(text[from..]))
        {
            var t = m.Value.TrimEnd('ฯ', '.', ',');
            if (ProvinceNames.Contains(t)) hit = t;
        }
        return hit;
    }

    /// <summary>token ถัดจากชื่อตำบล (ตำแหน่ง <paramref name="from"/>) เป็นอำเภอ/เขตที่ไม่มีคำนำไหม — ต้องตามด้วยจังหวัด
    /// (ชื่อ 77 จังหวัด · จ./จังหวัด · กรุงเทพ/กทม) หรือรหัสไปรษณีย์ หรือจบข้อความ · ตัวมันเองต้องไม่ใช่ marker/ตัวเลข/ชื่อจังหวัด</summary>
    private static string? InferUndecoratedDistrict(string text, int from, string? postcode)
    {
        var toks = Tokens.Matches(text[from..]).Select(m => m.Value).ToList();
        if (toks.Count == 0) return null;
        var cand = toks[0];
        if (!AreaStopRegex.IsMatch(cand) || IsProvinceName(cand) || cand.StartsWith("อำเภอ", StringComparison.Ordinal)
            || cand.StartsWith("เขต", StringComparison.Ordinal)) return null;
        var clean = CleanArea(cand);
        if (clean.Length == 0) return null;
        if (toks.Count == 1) return clean;
        var next = toks[1];
        var nextIsProvince = IsProvinceName(next)
            || next.StartsWith("จ.", StringComparison.Ordinal) || next.StartsWith("จังหวัด", StringComparison.Ordinal)
            || next.StartsWith("กรุงเทพ", StringComparison.Ordinal) || next.StartsWith("กทม", StringComparison.Ordinal);
        var nextIsPostcode = postcode != null && NormalizeThaiDigits(next).TrimEnd(',', '.') == postcode;
        return nextIsProvince || nextIsPostcode ? clean : null;
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

    /// <summary>Compose a free-text Address line from structured fields (for display).
    /// Delegate ไปที่ <see cref="ThaiAddressFormatter"/> — ตัวประกอบที่อยู่ตัวเดียว
    /// ของทั้งระบบ. เดิมเขียนซ้ำที่นี่และพลาด 2 เคส: กทม. ที่เขียนว่า "กทม"/
    /// "กรุงเทพฯ" (เทียบ == "กรุงเทพมหานคร" เท่านั้น) จะได้ "ต./อ." แทน
    /// "แขวง/เขต", และจังหวัดกรุงเทพฯ ถูกเติม "จ." นำหน้าซึ่งไม่ถูกต้อง</summary>
    public static string Compose(
        string? buildingNumber, string? buildingName, string? streetName,
        string? subDistrict, string? district, string? province, string? postalCode,
        string? moo = null)
        => ThaiAddressFormatter.Format(null, buildingNumber, buildingName, moo, streetName,
            subDistrict, district, province, postalCode);
}
