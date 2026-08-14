namespace Accounting.Services.Implementations;

using System.Text;

/// <summary>
/// ถอดอักษรไทย → โรมันตามแนว RTGS (ราชบัณฑิตยสภา) สำหรับ "ชื่อสถานที่/ที่อยู่"
/// บนเอกสารภาษาอังกฤษ — ใช้ตอนบริษัท/ผู้ติดต่อไม่ได้กรอกที่อยู่อังกฤษไว้เอง
///
/// ขอบเขตโดยตั้งใจ:
///  • จังหวัด 77 ชื่อใช้ **ตารางสะกดทางการ** (ไม่ผ่านตัวถอด) — ผิดไม่ได้
///  • ตำบล/อำเภอ/ถนน/อาคาร ใช้ตัวถอดแบบ rule-based — RTGS แท้ต้องรู้พยางค์/
///    เสียงอ่าน ซึ่งต้องมีพจนานุกรม; ตัวถอดนี้เป็นการประมาณที่ "อ่านรู้เรื่อง"
///    (Nong Hiang, Phanat Nikhom) และผู้ใช้แก้ทับได้เสมอผ่าน Company.AddressEn
///  • ห้ามใช้กับชื่อบุคคล/ชื่อบริษัท (การสะกดชื่อเฉพาะเป็นสิทธิ์ของเจ้าของชื่อ —
///    บริษัทกรอก NameEn เอง)
///
/// อ้างอิงกติกา RTGS ที่ implement: พยัญชนะต้น/ท้ายตามตารางราชบัณฑิตฯ,
/// ห นำ เงียบ, อ นำเงียบ, ตัดวรรณยุกต์/การันต์, สระลดรูป (CC = o ปิดท้าย,
/// พยางค์นำ = a), ทร ท้ายพยางค์ = t
/// </summary>
public static class ThaiRomanizer
{
    /// <summary>สะกดทางการของ 77 จังหวัด (ราชกิจจานุเบกษา) + ชื่อย่อที่พบบ่อย</summary>
    private static readonly Dictionary<string, string> ProvinceEnMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["กรุงเทพมหานคร"] = "Bangkok", ["กรุงเทพ"] = "Bangkok", ["กรุงเทพฯ"] = "Bangkok", ["กทม"] = "Bangkok", ["กทม."] = "Bangkok",
        ["สมุทรปราการ"] = "Samut Prakan", ["นนทบุรี"] = "Nonthaburi", ["ปทุมธานี"] = "Pathum Thani",
        ["พระนครศรีอยุธยา"] = "Phra Nakhon Si Ayutthaya", ["อยุธยา"] = "Phra Nakhon Si Ayutthaya",
        ["อ่างทอง"] = "Ang Thong", ["ลพบุรี"] = "Lopburi", ["สิงห์บุรี"] = "Sing Buri", ["ชัยนาท"] = "Chai Nat",
        ["สระบุรี"] = "Saraburi", ["ชลบุรี"] = "Chon Buri", ["ระยอง"] = "Rayong", ["จันทบุรี"] = "Chanthaburi",
        ["ตราด"] = "Trat", ["ฉะเชิงเทรา"] = "Chachoengsao", ["ปราจีนบุรี"] = "Prachin Buri", ["นครนายก"] = "Nakhon Nayok",
        ["สระแก้ว"] = "Sa Kaeo", ["นครราชสีมา"] = "Nakhon Ratchasima", ["โคราช"] = "Nakhon Ratchasima",
        ["บุรีรัมย์"] = "Buri Ram", ["สุรินทร์"] = "Surin", ["ศรีสะเกษ"] = "Si Sa Ket", ["อุบลราชธานี"] = "Ubon Ratchathani",
        ["ยโสธร"] = "Yasothon", ["ชัยภูมิ"] = "Chaiyaphum", ["อำนาจเจริญ"] = "Amnat Charoen", ["บึงกาฬ"] = "Bueng Kan",
        ["หนองบัวลำภู"] = "Nong Bua Lam Phu", ["ขอนแก่น"] = "Khon Kaen", ["อุดรธานี"] = "Udon Thani", ["เลย"] = "Loei",
        ["หนองคาย"] = "Nong Khai", ["มหาสารคาม"] = "Maha Sarakham", ["ร้อยเอ็ด"] = "Roi Et", ["กาฬสินธุ์"] = "Kalasin",
        ["สกลนคร"] = "Sakon Nakhon", ["นครพนม"] = "Nakhon Phanom", ["มุกดาหาร"] = "Mukdahan",
        ["เชียงใหม่"] = "Chiang Mai", ["ลำพูน"] = "Lamphun", ["ลำปาง"] = "Lampang", ["อุตรดิตถ์"] = "Uttaradit",
        ["แพร่"] = "Phrae", ["น่าน"] = "Nan", ["พะเยา"] = "Phayao", ["เชียงราย"] = "Chiang Rai", ["แม่ฮ่องสอน"] = "Mae Hong Son",
        ["นครสวรรค์"] = "Nakhon Sawan", ["อุทัยธานี"] = "Uthai Thani", ["กำแพงเพชร"] = "Kamphaeng Phet", ["ตาก"] = "Tak",
        ["สุโขทัย"] = "Sukhothai", ["พิษณุโลก"] = "Phitsanulok", ["พิจิตร"] = "Phichit", ["เพชรบูรณ์"] = "Phetchabun",
        ["ราชบุรี"] = "Ratchaburi", ["กาญจนบุรี"] = "Kanchanaburi", ["สุพรรณบุรี"] = "Suphan Buri", ["นครปฐม"] = "Nakhon Pathom",
        ["สมุทรสาคร"] = "Samut Sakhon", ["สมุทรสงคราม"] = "Samut Songkhram", ["เพชรบุรี"] = "Phetchaburi",
        ["ประจวบคีรีขันธ์"] = "Prachuap Khiri Khan", ["นครศรีธรรมราช"] = "Nakhon Si Thammarat", ["กระบี่"] = "Krabi",
        ["พังงา"] = "Phang Nga", ["ภูเก็ต"] = "Phuket", ["สุราษฎร์ธานี"] = "Surat Thani", ["ระนอง"] = "Ranong",
        ["ชุมพร"] = "Chumphon", ["สงขลา"] = "Songkhla", ["สตูล"] = "Satun", ["ตรัง"] = "Trang", ["พัทลุง"] = "Phatthalung",
        ["ปัตตานี"] = "Pattani", ["ยะลา"] = "Yala", ["นราธิวาส"] = "Narathiwat",
    };

    /// <summary>ชื่อจังหวัดอังกฤษทางการ — null เมื่อไม่ใช่ชื่อจังหวัดที่รู้จัก</summary>
    public static string? ProvinceEn(string? thai)
    {
        if (string.IsNullOrWhiteSpace(thai)) return null;
        var t = thai.Trim();
        // ตัดคำนำหน้า "จ." / "จังหวัด" ที่อาจติดมา
        if (t.StartsWith("จังหวัด")) t = t["จังหวัด".Length..].Trim();
        else if (t.StartsWith("จ.")) t = t[2..].Trim();
        return ProvinceEnMap.TryGetValue(t, out var en) ? en : null;
    }

    // ===== ตัวถอดอักษร (RTGS approximation) =====

    private static readonly Dictionary<char, string> Initial = new()
    {
        ['ก'] = "k", ['ข'] = "kh", ['ฃ'] = "kh", ['ค'] = "kh", ['ฅ'] = "kh", ['ฆ'] = "kh",
        ['ง'] = "ng", ['จ'] = "ch", ['ฉ'] = "ch", ['ช'] = "ch", ['ฌ'] = "ch",
        ['ซ'] = "s", ['ศ'] = "s", ['ษ'] = "s", ['ส'] = "s", ['ญ'] = "y",
        ['ฎ'] = "d", ['ด'] = "d", ['ฏ'] = "t", ['ต'] = "t",
        ['ฐ'] = "th", ['ฑ'] = "th", ['ฒ'] = "th", ['ถ'] = "th", ['ท'] = "th", ['ธ'] = "th",
        ['ณ'] = "n", ['น'] = "n", ['บ'] = "b", ['ป'] = "p",
        ['ผ'] = "ph", ['พ'] = "ph", ['ภ'] = "ph", ['ฝ'] = "f", ['ฟ'] = "f",
        ['ม'] = "m", ['ย'] = "y", ['ร'] = "r", ['ล'] = "l", ['ฬ'] = "l",
        ['ว'] = "w", ['ห'] = "h", ['ฮ'] = "h", ['อ'] = "",
    };

    private static readonly Dictionary<char, string> Final = new()
    {
        ['ก'] = "k", ['ข'] = "k", ['ค'] = "k", ['ฆ'] = "k", ['ง'] = "ng",
        ['จ'] = "t", ['ช'] = "t", ['ซ'] = "t", ['ศ'] = "t", ['ษ'] = "t", ['ส'] = "t",
        ['ฎ'] = "t", ['ฏ'] = "t", ['ฐ'] = "t", ['ฑ'] = "t", ['ฒ'] = "t",
        ['ด'] = "t", ['ต'] = "t", ['ถ'] = "t", ['ท'] = "t", ['ธ'] = "t",
        ['ญ'] = "n", ['ณ'] = "n", ['น'] = "n", ['ร'] = "n", ['ล'] = "n", ['ฬ'] = "n",
        ['บ'] = "p", ['ป'] = "p", ['พ'] = "p", ['ฟ'] = "p", ['ภ'] = "p",
        ['ม'] = "m", ['ย'] = "i", ['ว'] = "o",
    };

    private static bool IsThaiConsonant(char c) => c is >= 'ก' and <= 'ฮ';
    private static bool IsToneOrKiller(char c) => c is '่' or '้' or '๊' or '๋' or '็' or '์' or 'ๆ' or 'ฯ';
    private static bool IsAboveBelowVowel(char c) => c is 'ั' or 'ิ' or 'ี' or 'ึ' or 'ื' or 'ุ' or 'ู';
    private static bool IsLeadVowel(char c) => c is 'เ' or 'แ' or 'โ' or 'ใ' or 'ไ';

    /// <summary>ถอดอักษรไทยเป็นโรมัน 1 token (คั่นพยางค์ด้วยช่องว่างตามธรรมเนียม
    /// ชื่อสถานที่ เช่น "หนองเหียง" → "Nong Hiang"). อักษรละติน/ตัวเลขผ่านตามเดิม</summary>
    public static string Transliterate(string thai)
    {
        if (string.IsNullOrWhiteSpace(thai)) return "";
        var syllables = new List<string>();
        var s = thai.Trim();
        var i = 0;
        var latin = new StringBuilder();   // สะสมอักษรที่ไม่ใช่ไทย (เลขที่/ชื่ออังกฤษปน)

        void FlushLatin() { if (latin.Length > 0) { syllables.Add(latin.ToString()); latin.Clear(); } }

        while (i < s.Length)
        {
            var c = s[i];
            if (!IsThaiConsonant(c) && !IsLeadVowel(c) && c != 'ฤ')
            {
                if (!IsToneOrKiller(c) && !char.IsWhiteSpace(c)) latin.Append(c);
                else if (char.IsWhiteSpace(c)) FlushLatin();
                i++;
                continue;
            }
            FlushLatin();

            // ---- อ่าน 1 พยางค์ ----
            string lead = "";                       // เ แ โ ใ ไ
            if (IsLeadVowel(c)) { lead = c.ToString(); i++; if (i >= s.Length) break; c = s[i]; }

            if (c == 'ฤ') { syllables.Add("rue"); i++; continue; }
            if (!IsThaiConsonant(c)) continue;       // สระนำโดดไม่มีพยัญชนะ — ข้าม

            // การันต์: ตัดทั้งกลุ่ม — C์ (สิงห์), C+C์ (จันทร์ = ทร์ เงียบทั้งคู่),
            // C+สระ+์ (ศักดิ์ = ดิ์)
            if (i + 1 < s.Length && s[i + 1] == '์') { i += 2; continue; }
            if (i + 2 < s.Length && IsThaiConsonant(s[i + 1]) && s[i + 2] == '์') { i += 3; continue; }
            if (i + 2 < s.Length && IsAboveBelowVowel(s[i + 1]) && s[i + 2] == '์') { i += 3; continue; }

            var init = Initial.GetValueOrDefault(c, "");
            i++;

            // ศร/สร → ร เงียบ (ศรี = si, สร้อย = soi)
            if ((c == 'ศ' || c == 'ส') && i < s.Length && s[i] == 'ร'
                && !(i + 1 < s.Length && s[i + 1] == '์'))
            {
                i++;
            }
            // ห นำ / อ นำ (ห+โซโนแรนต์, อ+ย) → ตัวนำเงียบ
            if ((c == 'ห' || c == 'อ') && i < s.Length && IsThaiConsonant(s[i])
                && "งญนมยรลว".IndexOf(s[i]) >= 0
                && !(i + 1 < s.Length && s[i + 1] == '์'))
            {
                init = Initial.GetValueOrDefault(s[i], "");
                i++;
            }
            // ควบกล้ำ ร ล ว (กร ปล ขว ...)
            else if (i < s.Length && "รลว".IndexOf(s[i]) >= 0 && "กขคตปผพ".IndexOf(c) >= 0
                && !(i + 1 < s.Length && s[i + 1] == '์')
                // ตามด้วยสระเท่านั้นจึงเป็นควบ — "ทร" ท้ายคำ (สมุทร) ไม่ใช่
                && i + 1 < s.Length && (IsAboveBelowVowel(s[i + 1]) || "าอ".IndexOf(s[i + 1]) >= 0
                    || IsToneOrKiller(s[i + 1]) || lead != ""))
            {
                init += s[i] == 'ว' ? "w" : Initial.GetValueOrDefault(s[i], "");
                i++;
            }

            // ข้ามวรรณยุกต์
            while (i < s.Length && IsToneOrKiller(s[i]) && s[i] != '์') i++;

            // ---- สระ ----
            string vowel = "";
            bool vowelClosed = false;   // สระที่จบพยางค์ในตัว (เ-าะ ฯลฯ)
            bool tailI = false;          // เ-ย → เติม i ท้าย (oei)
            if (i < s.Length)
            {
                var v = s[i];
                switch (v)
                {
                    case 'ั':
                        i++; vowel = "a";
                        // -ัว = ua (หัว) — ว ไม่มีสระตาม = ส่วนของสระ ไม่ใช่ตัวสะกด
                        if (i < s.Length && s[i] == 'ว'
                            && !(i + 1 < s.Length && (IsAboveBelowVowel(s[i + 1]) || s[i + 1] is 'า' or 'ะ' or 'ำ')))
                        { i++; vowel = "ua"; }
                        break;
                    case 'ิ': i++; vowel = "i"; break;
                    case 'ี': i++; vowel = "i"; break;
                    case 'ึ': i++; vowel = "ue"; break;
                    case 'ื': i++; vowel = "ue"; if (i < s.Length && s[i] == 'อ') i++; break;
                    case 'ุ': i++; vowel = "u"; break;
                    case 'ู': i++; vowel = "u"; break;
                    case 'า': i++; vowel = "a"; break;
                    case 'ำ': i++; vowel = "am"; vowelClosed = true; break;
                    case 'อ':
                        // อ เป็นสระ (นอง=น+อ+ง) เฉพาะเมื่อไม่มีสระนำ
                        if (lead == "") { i++; vowel = "o"; }
                        break;
                }
                while (i < s.Length && IsToneOrKiller(s[i]) && s[i] != '์') i++;
            }

            // ผสมสระนำ
            if (lead != "")
            {
                if (lead == "เ")
                {
                    if (vowel == "i")
                    {
                        // เ-ีย(ะ) → ia
                        if (i < s.Length && s[i] == 'ย') { i++; vowel = "ia"; }
                        else vowel = "i";
                    }
                    else if (vowel == "ue")
                    {
                        // เ-ือ → uea
                        if (i < s.Length && s[i] == 'อ') i++;
                        vowel = "uea";
                    }
                    else if (vowel == "a")   // เ-า → ao (า อ่านแล้ว)
                        vowel = "ao";
                    else if (i < s.Length && s[i] == 'อ') { i++; vowel = "oe"; }      // เ-อ
                    else if (i < s.Length && s[i] == 'ะ') { i++; vowel = "e"; vowelClosed = true; }
                    else if (vowel == "" && i < s.Length && s[i] == 'ย'
                        && !(i + 1 < s.Length && (IsAboveBelowVowel(s[i + 1]) || s[i + 1] is 'า' or 'ะ' or 'ำ')))
                    { i++; vowel = "oe"; vowelClosed = true; tailI = true; }   // เ-ย = oei
                    else if (vowel == "") vowel = "e";
                }
                else if (lead == "แ")
                {
                    if (i < s.Length && s[i] == 'ะ') { i++; vowelClosed = true; }
                    vowel = "ae";
                }
                else if (lead == "โ")
                {
                    if (i < s.Length && s[i] == 'ะ') { i++; vowelClosed = true; }
                    vowel = "o";
                }
                else vowel = "ai";           // ใ ไ
            }
            else if (i < s.Length && s[i] == 'ะ') { i++; if (vowel == "") vowel = "a"; vowelClosed = true; }
            else if (i < s.Length && s[i] == 'ว' && vowel == ""
                && i + 1 < s.Length && IsThaiConsonant(s[i + 1]) && Final.ContainsKey(s[i + 1])
                && !(i + 2 < s.Length && (IsAboveBelowVowel(s[i + 2]) || s[i + 2] is 'า' or 'ะ' or 'ำ')))
            {
                i++; vowel = "ua";           // -ว- กลางพยางค์ (สวน = suan)
            }

            // ---- ตัวสะกด ----
            string fin = "";
            if (!vowelClosed && i < s.Length && IsThaiConsonant(s[i]) && Final.ContainsKey(s[i]))
            {
                var f = s[i];
                var nextIsVowelOwner =
                    i + 1 < s.Length && (IsAboveBelowVowel(s[i + 1]) || s[i + 1] is 'า' or 'ะ' or 'ำ'
                        || (s[i + 1] == 'ว' && !(i + 2 < s.Length
                            && (IsAboveBelowVowel(s[i + 2]) || s[i + 2] is 'า' or 'ะ' or 'ำ')))
                        || IsToneOrKiller(s[i + 1]) && s[i + 1] != '์');
                var killed = i + 1 < s.Length && s[i + 1] == '์';
                if (vowel != "")
                {
                    // เหลือพยัญชนะโดด 1 ตัวหลัง f → f ควรเป็นต้นพยางค์ CoC ท้ายคำ
                    // (สีลม = Si Lom ไม่ใช่ Sin Ma) — อย่ากินเป็นตัวสะกด
                    var rest = 0;
                    for (var k = i + 1; k < s.Length; k++)
                    {
                        if (IsToneOrKiller(s[k])) continue;
                        rest = IsThaiConsonant(s[k]) ? rest + 1 : 99;
                        if (rest > 1) break;
                    }
                    if (!nextIsVowelOwner && !killed && rest != 1) { fin = Final[f]; i++; }
                }
                else
                {
                    // ไม่มีสระเขียน: CC ท้าย = o (นคร → khon), นำหน้า = a (สกล → sa)
                    var isLastPair = true;
                    for (var k = i + 1; k < s.Length; k++)
                    {
                        if (IsToneOrKiller(s[k])) continue;
                        isLastPair = false; break;
                    }
                    if (isLastPair && !killed) { vowel = "o"; fin = Final[f]; i++; }
                    else vowel = "a";
                }
            }
            else if (vowel == "") vowel = "a";   // พยัญชนะเดี่ยวท้าย token (สระลดรูป)

            var syl = init + vowel + (tailI ? "i" : "") + fin;
            if (syl.Length > 0) syllables.Add(syl);
        }
        FlushLatin();

        // Capitalize ทีละพยางค์ตามธรรมเนียมชื่อสถานที่ (Nong Hiang)
        var parts = syllables.Where(x => x.Length > 0)
            .Select(x => char.IsLetter(x[0]) ? char.ToUpperInvariant(x[0]) + x[1..] : x);
        return string.Join(" ", parts);
    }

    /// <summary>ประกอบที่อยู่ภาษาอังกฤษจาก field ที่อยู่ไทย (โครง structured) ตาม
    /// ธรรมเนียมจดหมายอังกฤษ: เลขที่ อาคาร, Moo N, ถนน Rd., ตำบล, อำเภอ,
    /// จังหวัด(ตารางทางการ) รหัสไปรษณีย์ — ชิ้นที่เป็นละตินอยู่แล้วผ่านตามเดิม</summary>
    public static string ComposeEnglishAddress(
        string? buildingNumber, string? buildingName, string? moo, string? streetName,
        string? subDistrict, string? district, string? province, string? postalCode,
        string? freeTextFallback = null)
    {
        static string? Clean(string? x) => string.IsNullOrWhiteSpace(x) ? null : x.Trim();
        static bool HasThai(string x) => x.Any(ch => ch is >= 'ก' and <= '๛');
        // ชิ้นละตินล้วนผ่านตรง / ชิ้นมีอักษรไทยถอดเสียง
        static string? T(string? x)
        {
            x = Clean(x);
            if (x == null) return null;
            return HasThai(x) ? Transliterate(x) : x;
        }

        var bits = new List<string>();
        var first = new List<string?> { Clean(buildingNumber), T(buildingName) }
            .Where(x => x != null).ToList();
        if (first.Count > 0) bits.Add(string.Join(" ", first));
        var mooClean = Clean(moo)?.Replace("หมู่ที่", "").Replace("หมู่", "").Replace("ม.", "").Trim();
        if (!string.IsNullOrEmpty(mooClean)) bits.Add($"Moo {mooClean}");
        var street = Clean(streetName);
        if (street != null)
        {
            street = street.Replace("ถนน", "").Replace("ถ.", "").Trim();
            var soi = street.StartsWith("ซอย") || street.StartsWith("ซ.");
            street = street.Replace("ซอย", "").Replace("ซ.", "").Trim();
            var en = T(street);
            if (!string.IsNullOrEmpty(en)) bits.Add(soi ? $"Soi {en}" : $"{en} Rd.");
        }
        var sd = Clean(subDistrict);
        if (sd != null)
            bits.Add(T(sd.Replace("ตำบล", "").Replace("ต.", "").Replace("แขวง", "").Trim())!);
        var d = Clean(district);
        if (d != null)
            bits.Add(T(d.Replace("อำเภอ", "").Replace("อ.", "").Replace("เขต", "").Trim())!);
        var provEn = ProvinceEn(province) ?? T(province);
        var tail = new List<string?> { provEn, Clean(postalCode) }.Where(x => x != null).ToList();
        if (tail.Count > 0) bits.Add(string.Join(" ", tail));

        if (bits.Count > 0) return string.Join(", ", bits.Where(b => !string.IsNullOrWhiteSpace(b)));

        // ไม่มี structured เลย → ถอดจาก free-text ทั้งบรรทัด (ดีกว่าปล่อยไทยบนใบอังกฤษ)
        var ft = Clean(freeTextFallback);
        return ft == null ? "" : (HasThai(ft) ? Transliterate(ft) : ft);
    }
}
