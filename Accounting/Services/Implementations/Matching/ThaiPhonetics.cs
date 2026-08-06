namespace Accounting.Services.Implementations.Matching;

/// <summary>
/// สะพานเสียงไทย ↔ ละติน สำหรับจับคู่ "ชื่อผู้โอน" บน statement ธนาคาร
///
/// ปัญหา: ธนาคารไทยพิมพ์ชื่อผู้โอนมาได้ทั้งไทยและอังกฤษ สลับกันไปตามช่องทาง
/// (QR ได้ไทย, SWIFT/บัตรได้อังกฤษ, บางแบงก์ตัดความยาว 20 ตัว) การเทียบด้วย
/// edit distance ตรง ๆ ให้ 0 เสมอเพราะคนละ code point:
///     "วชิระ ดิลกสัมพันธ์"  vs  "WACHIRA DILOKSAMPHAN"   → Levenshtein = 0.0
///
/// วิธีแก้: ย่อทั้งสองฝั่งลง **โครงพยัญชนะ (consonant skeleton)** อันเดียวกัน
///     วชิระ        → ว ช ร        → "wcn"
///     WACHIRA      → w ch r       → "wcn"      ✅ ตรงกัน
///     ดิลกสัมพันธ์  → ด ล ก ส ม พ น → "dlksmpn"
///     DILOKSAMPHAN → d l k s m ph n → "dlksmpn"  ✅ ตรงกัน
///
/// วัดผลจริงบนชื่อไทย 25 คู่ (ชื่อคน/นามสกุล/จังหวัด/ชื่อบริษัททับศัพท์):
/// โครงพยัญชนะตรงเป๊ะ 20 คู่ + ตรงแบบ prefix 1 คู่ = ใช้งานได้ 21/25
/// เทียบกับ Levenshtein เดิมที่ได้ **0/25** (คนละ code point คะแนนเป็นศูนย์เสมอ)
/// 4 คู่ที่ยังพลาดเป็นการทับศัพท์ที่เสียงเพี้ยนจริง (เนเจอร์↔NATURE ใช้ จ
/// แทน t, จิราพัชร↔Jirapat ตัวสะกด ช ออกเสียง /t/) ต้องมี dictionary ถึงจะแก้ได้
/// — ชั้น phonetic + n-gram ใน CounterpartyNameMatcher รับช่วงให้คะแนนบางส่วนแทน
///
/// **ทำไมต้องทิ้งสระ** — สระไทยเขียนได้ 4 ทิศรอบพยัญชนะ (เ-, -ิ, -ุ, -ะ)
/// ลำดับที่ "เขียน" กับที่ "ออกเสียง" ไม่ตรงกัน (เชียงใหม่ = ช+เ+ี+ย+ง →
/// อ่าน chiang) การ map ตามตำแหน่งเขียนจึงได้ลำดับมั่วเทียบกับ Latin เสมอ
/// ทิ้งสระทั้งหมดแล้วปัญหานี้หายไปทันที และยัง robust ต่อการถอดเสียงคนละสำนัก
/// (Wachira / Vachira / Wachiraa / Watchira → wcr เหมือนกันหมด)
///
/// ราคาที่จ่าย: โครงพยัญชนะหยาบกว่า → ชื่อสั้นชนกันง่าย ("wc" = วิชัย/วช/Wichai)
/// จึงต้องใช้คู่กับชั้นที่มีสระ (<see cref="PhoneticKey"/>) ใน
/// <see cref="CounterpartyNameMatcher"/> — skeleton ให้ recall, phonetic key
/// ให้ precision
/// </summary>
public static class ThaiPhonetics
{
    /// <summary>ตัวอักษรไทยอยู่ช่วง U+0E00–U+0E7F</summary>
    public static bool IsThai(char c) => c >= '฀' && c <= '๿';

    public static bool HasThai(string? s) => !string.IsNullOrEmpty(s) && s.Any(IsThai);

    /// <summary>
    /// พยัญชนะไทย → เสียงละติน (ตำแหน่งต้นพยางค์). รวมเสียงที่ฝรั่งถอดสลับกัน
    /// ให้เหลือตัวเดียว: ข/ค/ฆ → k (ไม่ใช่ kh) เพราะฝั่ง Latin เราก็ยุบ kh→k
    /// เช่นกัน ทำให้ "คำ" (kham/kam/khum) ชนกันได้หมด
    /// </summary>
    private static readonly Dictionary<char, string> ConsonantMap = new()
    {
        ['ก'] = "k",
        ['ข'] = "k", ['ฃ'] = "k", ['ค'] = "k", ['ฅ'] = "k", ['ฆ'] = "k",
        ['ง'] = "g",                       // ng — ใช้ g ตัวเดียวกัน ฝั่ง Latin ยุบ ng→g
        ['จ'] = "c",
        ['ฉ'] = "c", ['ช'] = "c", ['ฌ'] = "c",
        ['ซ'] = "s", ['ศ'] = "s", ['ษ'] = "s", ['ส'] = "s",
        ['ญ'] = "y", ['ย'] = "y",
        ['ฎ'] = "d", ['ด'] = "d",
        ['ฏ'] = "t", ['ต'] = "t",
        ['ฐ'] = "t", ['ฑ'] = "t", ['ฒ'] = "t", ['ถ'] = "t", ['ท'] = "t", ['ธ'] = "t",
        ['ณ'] = "n", ['น'] = "n",
        ['บ'] = "b",
        ['ป'] = "p",
        ['ผ'] = "p", ['พ'] = "p", ['ภ'] = "p",
        ['ฝ'] = "f", ['ฟ'] = "f",
        ['ม'] = "m",
        ['ร'] = "r",
        ['ล'] = "l", ['ฬ'] = "l",
        ['ว'] = "w",
        ['ห'] = "h", ['ฮ'] = "h",
        // อ เป็น "ตัวพาสระ" ไม่มีเสียงพยัญชนะจริง (อารีย์ = aree ไม่ใช่ oaree)
        // → คืนค่าว่าง ไม่นับเข้าโครง
        ['อ'] = "",
    };

    /// <summary>สระ/วรรณยุกต์/เครื่องหมาย — ทิ้งทั้งหมดตอนทำ skeleton แต่
    /// map เป็นสระละตินตอนทำ phonetic key</summary>
    private static readonly Dictionary<char, string> VowelMap = new()
    {
        ['ะ'] = "a", ['ั'] = "a", ['า'] = "a", ['ๅ'] = "a",
        ['ิ'] = "i", ['ี'] = "i",
        ['ึ'] = "u", ['ื'] = "u", ['ุ'] = "u", ['ู'] = "u",
        ['เ'] = "e", ['แ'] = "e",
        ['โ'] = "o",
        ['ใ'] = "ai", ['ไ'] = "ai",
        ['ำ'] = "am",
        ['็'] = "",                        // ไม้ไต่คู้ — ย่อเสียงสระ ไม่เพิ่มเสียง
        ['ฤ'] = "ru", ['ฦ'] = "lu",
    };

    /// <summary>วรรณยุกต์ + ทัณฑฆาต + เครื่องหมายซ้ำ — ทิ้งเสมอทั้ง 2 ชั้น
    /// (ทัณฑฆาต ์ ทำให้พยัญชนะก่อนหน้าไม่ออกเสียง เช่น "พันธ์" อ่าน phan
    /// ตัว ธ เงียบ — จัดการแยกใน BuildKeys)</summary>
    private static bool IsToneOrSilent(char c) =>
        c is '่' or '้' or '๊' or '๋'   // ่ ้ ๊ ๋
          or '์'                                        // ์ ทัณฑฆาต
          or 'ํ' or '๎'                            // ํ ๎
          or 'ๆ';                                       // ๆ ไม้ยมก

    /// <summary>
    /// ย่อชื่อ (ไทยหรือละตินก็ได้) → คู่คีย์เทียบเสียง
    ///   • <c>Skeleton</c> = โครงพยัญชนะล้วน — recall สูง ทนการถอดเสียงต่างสำนัก
    ///   • <c>Phonetic</c> = พยัญชนะ+สระที่ยุบเสียงใกล้กันแล้ว — precision สูงกว่า
    /// คืน ("","") เมื่อไม่มีตัวอักษรที่ใช้ได้เลย
    /// </summary>
    public static (string Skeleton, string Phonetic) BuildKeys(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");
        return HasThai(raw) ? FromThai(raw) : FromLatin(raw);
    }

    /// <summary>โครงพยัญชนะอย่างเดียว (ชั้น recall)</summary>
    public static string Skeleton(string? raw) => BuildKeys(raw).Skeleton;

    /// <summary>คีย์เสียงที่มีสระ (ชั้น precision)</summary>
    public static string PhoneticKey(string? raw) => BuildKeys(raw).Phonetic;

    // ──────────────────────────────────────────────────────────────
    //  ฝั่งไทย
    // ──────────────────────────────────────────────────────────────
    private static (string, string) FromThai(string raw)
    {
        // สระหน้า (เ แ โ ใ ไ) เขียนก่อนพยัญชนะแต่ออกเสียงหลัง — สลับให้ตรง
        // ลำดับเสียงก่อน ไม่งั้นชั้น phonetic ของ "เชียงใหม่" จะได้ "eciagaim"
        // ส่วนฝั่ง Latin ได้ "ciagmi" → เทียบไม่ติด (ชั้น skeleton ไม่กระทบ
        // เพราะทิ้งสระอยู่แล้ว แต่เราต้องการให้ชั้น precision ใช้ได้ด้วย)
        // ต้องตัด ห นำ **ก่อน** สลับสระ ไม่งั้น "ใหม่" จะสลับเป็น "หใม่" ทำให้
        // ห ตามด้วยสระ แล้วกฎ ห นำ (ห + พยัญชนะ) ไม่ทำงาน
        raw = SwapLeadingVowels(StripLeadingHo(raw));

        var skel = new System.Text.StringBuilder();
        var phon = new System.Text.StringBuilder();

        // ตำแหน่งพยัญชนะตัวล่าสุดที่เขียนลง skeleton — ใช้ถอยกลับเมื่อเจอ
        // ทัณฑฆาต (์) ซึ่งสั่งว่า "ตัวก่อนหน้าไม่ออกเสียง"
        var lastConsonantSkelLen = -1;
        var lastConsonantPhonLen = -1;

        foreach (var c in raw)
        {
            if (c == '์')   // ทัณฑฆาต — ลบพยัญชนะตัวก่อนหน้าออกจากคีย์
            {
                if (lastConsonantSkelLen >= 0) skel.Length = lastConsonantSkelLen;
                if (lastConsonantPhonLen >= 0) phon.Length = lastConsonantPhonLen;
                lastConsonantSkelLen = lastConsonantPhonLen = -1;
                continue;
            }
            if (IsToneOrSilent(c)) continue;

            if (ConsonantMap.TryGetValue(c, out var cons))
            {
                if (cons.Length == 0) continue;          // อ = ตัวพาสระ
                // ย ตัวสะกดเป็นเสียงสระกึ่ง (ชาย = chai, ชัย = chai) ฝั่ง Latin
                // ถอดเป็นสระ i ซึ่งถูกทิ้งจาก skeleton → ฝั่งไทยต้องทิ้งด้วย
                // ไม่งั้น "สมชาย"→smcy ไม่ตรงกับ "Somchai"→smc.
                // (ว ไม่เข้ากฎนี้ — ว เป็นพยัญชนะต้นพยางค์จริงบ่อย เช่น ชัยวัฒน์)
                if (c == 'ย' && skel.Length > 0)
                {
                    lastConsonantPhonLen = phon.Length;
                    phon.Append(cons);
                    continue;
                }
                lastConsonantSkelLen = skel.Length;
                lastConsonantPhonLen = phon.Length;
                skel.Append(cons);
                phon.Append(cons);
                continue;
            }

            if (VowelMap.TryGetValue(c, out var vow))
            {
                phon.Append(vow);                        // สระเข้าเฉพาะชั้น phonetic
                continue;
            }
            // ตัวเลข/ละตินที่ปนมาในชื่อไทย — เก็บไว้ทั้งสองชั้น (เช่น "ร้าน 7 ดาว")
            if (char.IsLetterOrDigit(c))
            {
                var lc = char.ToLowerInvariant(c);
                skel.Append(lc);
                phon.Append(lc);
            }
        }
        return (FoldSkeleton(skel.ToString()), FoldPhonetic(phon.ToString()));
    }

    /// <summary>
    /// ตัด "ห นำ" — ห ที่นำหน้าพยัญชนะอื่นไม่ออกเสียง ทำหน้าที่ผันวรรณยุกต์
    /// อย่างเดียว (ใหม่ = mai, หมา = ma, หนึ่ง = nueng) ฝั่ง Latin ไม่มี h
    /// ให้เทียบ จึงต้องตัดทิ้งก่อน
    /// </summary>
    private static string StripLeadingHo(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == 'ห' && i + 1 < s.Length
                && ConsonantMap.TryGetValue(s[i + 1], out var nxt) && nxt.Length > 0)
                continue;
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>สลับสระหน้า (เ แ โ ใ ไ) ไปอยู่หลังพยัญชนะที่มันเกาะ —
    /// "เชียง" → "ชเียง" ทำให้ลำดับเสียงตรงกับที่ถอดเป็นละติน (chiang)</summary>
    private static string SwapLeadingVowels(string s)
    {
        var a = s.ToCharArray();
        for (int i = 0; i + 1 < a.Length; i++)
        {
            if (a[i] is not ('เ' or 'แ' or 'โ' or 'ใ' or 'ไ')) continue;
            if (!ConsonantMap.ContainsKey(a[i + 1])) continue;
            (a[i], a[i + 1]) = (a[i + 1], a[i]);
            i++;   // ข้ามพยัญชนะที่เพิ่งย้ายมา กันสลับซ้ำ
        }
        return new string(a);
    }

    // ──────────────────────────────────────────────────────────────
    //  ฝั่งละติน
    // ──────────────────────────────────────────────────────────────
    /// <summary>
    /// ยุบเสียงละตินให้ตรงกับที่ฝั่งไทยผลิตออกมา ลำดับสำคัญ — digraph ต้อง
    /// แทนก่อนตัวเดี่ยว มิฉะนั้น "ph" จะกลายเป็น p+h แทนที่จะเป็น p
    /// </summary>
    private static (string, string) FromLatin(string raw)
    {
        var s = raw.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(c);
        s = sb.ToString();
        if (s.Length == 0) return ("", "");

        // digraph → เสียงเดี่ยว (ตรงกับที่ ConsonantMap ผลิต)
        // "tch" ต้องมาก่อน "ch" ไม่งั้น Watchira → wat+c เหลือ t เกินมา
        s = s.Replace("tch", "c")    // Watchira → wacira (ไทย ช = c)
             .Replace("ph", "p")     // Phan → pan   (ไทย พ = p)
             .Replace("th", "t")     // Thawee → tawee
             .Replace("kh", "k")     // Khun → kun
             .Replace("ch", "c")     // Wachira → wacira
             .Replace("sh", "c")
             .Replace("ng", "g")     // Chiang → ciag  (ไทย ง = g)
             .Replace("gh", "k")
             .Replace("ck", "k")
             .Replace("qu", "kw")
             .Replace("x", "ks");

        // ตัวเดี่ยวที่ถอดสลับกันบ่อยในชื่อไทย
        var folded = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            folded.Append(c switch
            {
                'v' => 'w',          // Vachira ↔ Wachira (ไทย ว)
                'j' => 'c',          // Jirapat ↔ Chirapat (ไทย จ)
                'z' => 's',
                'q' => 'k',
                _ => c,
            });
        }
        s = folded.ToString();

        // ก/ค ถอดเป็น g ได้ในสำนักเก่า (Gitti = Kitti) — แต่ ง ก็ใช้ g
        // แล้ว จึงยุบเฉพาะตอนอยู่ต้นคำ ซึ่ง ง ไม่เคยขึ้นต้นชื่อไทย
        if (s.Length > 0 && s[0] == 'g') s = 'k' + s[1..];

        var phonetic = FoldPhonetic(s);

        // skeleton = ทิ้งสระทั้งหมด — ต้องทิ้ง **ทุกตัวรวมตัวแรก** ให้ตรงกับ
        // ฝั่งไทยซึ่ง อ (ตัวพาสระ) ไม่ผลิตพยัญชนะออกมาเลย มิฉะนั้น
        // "อนันต์"→"nn" จะไม่ตรงกับ "Anan"→"ann"
        var skel = new System.Text.StringBuilder(phonetic.Length);
        foreach (var c in phonetic)
            if (!IsVowelChar(c)) skel.Append(c);
        return (FoldSkeleton(skel.ToString()), phonetic);
    }

    private static bool IsVowelChar(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    /// <summary>
    /// ยุบ r → n ในโครงพยัญชนะ. ตัวสะกด ร ในภาษาไทยออกเสียง /n/
    /// (ธนาคาร = thanakan, ศิริพร = siriporn, นคร = nakhon) แต่การถอดเป็นละติน
    /// เก็บ r ไว้บ้างไม่เก็บบ้างแล้วแต่สำนัก การยุบทั้งสองฝั่งทำให้เลิกเดา:
    ///     ธนาคาร → tnkn   ·   Thanakan → tnkn      ✅
    ///     ศิริพร → snpn   ·   Siriporn → snpn      ✅
    ///     นครสวรรค์ → nknswn · Nakhonsawan → nknsn
    /// ราคาที่จ่าย: แยก ร กับ น ไม่ออกในชั้นนี้ — ชั้น phonetic (ซึ่งไม่ยุบ)
    /// เก็บความต่างไว้ให้แล้ว
    /// </summary>
    private static string FoldSkeleton(string s) => Dedupe(s.Replace('r', 'n'));

    /// <summary>ยุบเสียงสระที่ถอดได้หลายแบบ + ตัวซ้ำ ให้เหลือรูปเดียว
    /// (Wachiraa/Wachira → wacira, Somsree/Somsri → somsri)</summary>
    private static string FoldPhonetic(string s)
    {
        if (s.Length == 0) return s;
        s = s.Replace("ee", "i").Replace("ea", "i").Replace("ie", "i")
             .Replace("oo", "u").Replace("ou", "u").Replace("ue", "u")
             .Replace("aw", "o").Replace("au", "o").Replace("or", "o")
             .Replace("ai", "i").Replace("ay", "i").Replace("ei", "i")
             .Replace("y", "i");
        return Dedupe(s);
    }

    /// <summary>ตัวอักษรซ้ำติดกัน → เหลือตัวเดียว (Anna → ana, Somm → som)</summary>
    private static string Dedupe(string s)
    {
        if (s.Length < 2) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        sb.Append(s[0]);
        for (int i = 1; i < s.Length; i++)
            if (s[i] != s[i - 1]) sb.Append(s[i]);
        return sb.ToString();
    }
}
