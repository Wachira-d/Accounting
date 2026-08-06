namespace Accounting.Services.Implementations.Matching;

/// <summary>
/// จับคู่ "ชื่อผู้โอน/ผู้รับโอน" จาก statement ธนาคาร กับชื่อคู่ค้าในระบบ
///
/// ชื่อบน statement ไทยเพี้ยนได้พร้อมกันหลายทาง — ตัวจับคู่ต้องทนทุกแบบ:
///
/// | เคส | ตัวอย่าง statement | ในระบบ |
/// | --- | --- | --- |
/// | คนละภาษา | `WACHIRA DILOKSAMPHAN` | `วชิระ ดิลกสัมพันธ์` |
/// | ตัดกลางคำ (แบงก์จำกัดความยาว) | `WACHI` / `นาย วชิระ ดิลกสั` | `วชิระ ดิลกสัมพันธ์` |
/// | สลับชื่อ-สกุล | `DILOKSAMPHAN WACHIRA` | `วชิระ ดิลกสัมพันธ์` |
/// | ย่อเป็นอักษรแรก | `W. DILOKSAMPHAN` / `ว. ดิลกสัมพันธ์` | `วชิระ ดิลกสัมพันธ์` |
/// | ปิดบังบางส่วน | `WACHIRA D***` / `นาย ว*** ด***` | `วชิระ ดิลกสัมพันธ์` |
/// | คำนำหน้าเกิน/ขาด | `MR. WACHIRA` | `วชิระ` |
/// | ถอดเสียงคนละสำนัก | `VACHIRA` / `WATCHIRA` / `WACHIRAA` | `วชิระ` |
/// | นิติบุคคลคนละรูป | `TAKE TIME NATURE RESORT CO LTD` | `บริษัท เทค ไทม์ เนเจอร์ รีสอร์ท จำกัด` |
/// | มีขยะจากช่องทางปน | `Thai QR Payment \| KB00000181 TAKE TIME \| EDC/K SHOP/MYQR` | `Take Time Nature Resort` |
/// | ไทยไม่เว้นวรรค | `วชิระดิลกสัมพันธ์` | `วชิระ ดิลกสัมพันธ์` |
///
/// **สถาปัตยกรรม 3 ชั้น** (เร็ว → ช้า, หยุดทันทีที่มั่นใจพอ):
///   1. <b>Normalize</b> — ตัดขยะช่องทาง/เลขบัญชี/คำนำหน้า/รูปแบบนิติบุคคล
///   2. <b>Token align</b> — จับคู่ token ข้ามภาษาผ่านคีย์เสียง
///      (<see cref="ThaiPhonetics"/>) แบบ best-match ไม่สนลำดับ → แก้เคสสลับ
///      ชื่อ-สกุลและตัดคำในคราวเดียว
///   3. <b>Fallback n-gram</b> — เมื่อ token align ล้มเหลว (ไทยไม่เว้นวรรค,
///      ชื่อยาวผิดรูป) ใช้ความคล้ายระดับตัวอักษรบนคีย์เสียงทั้งก้อน
///
/// คืนคะแนน 0.0–1.0 พร้อมเหตุผลไทยสำหรับโชว์บน UI (ผู้ใช้ต้องเห็นว่า
/// "ทำไมระบบคิดว่าใช่" ไม่ใช่เชื่อตัวเลขลอย ๆ)
/// </summary>
public static class CounterpartyNameMatcher
{
    /// <summary>ผลการเทียบ 1 คู่</summary>
    /// <param name="Score">0.0–1.0 — ≥0.80 เชื่อได้, 0.60–0.79 ควรให้คนยืนยัน</param>
    /// <param name="Reason">คำอธิบายไทยสั้น ๆ ว่าตรงกันเพราะอะไร ("" = ไม่ตรง)</param>
    /// <param name="MatchedTokens">จำนวน token ที่จับคู่ได้</param>
    /// <param name="Truncated">true = มีฝั่งใดถูกตัดคำ (ตรงแบบ prefix)</param>
    /// <param name="CrossScript">true = เทียบข้ามภาษาไทย↔ละติน</param>
    public readonly record struct NameMatch(
        double Score, string Reason, int MatchedTokens, bool Truncated, bool CrossScript)
    {
        public static readonly NameMatch None = new(0.0, "", 0, false, false);
    }

    /// <summary>ยอมรับว่า "ใช่" โดยไม่ต้องถามคน</summary>
    public const double ConfidentThreshold = 0.80;

    /// <summary>ต่ำกว่านี้ถือว่าไม่เกี่ยวกันเลย ไม่ต้องเสนอ</summary>
    public const double MinimumUsefulThreshold = 0.45;

    // ──────────────────────────────────────────────────────────────
    //  API หลัก
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// เทียบชื่อ 2 ก้อน. <paramref name="statementText"/> คือข้อความดิบจาก
    /// statement (ใส่ description ทั้งบรรทัดได้เลย — ตัวจับคู่ตัดขยะเอง)
    /// </summary>
    public static NameMatch Match(string? statementText, string? knownName)
    {
        if (string.IsNullOrWhiteSpace(statementText) || string.IsNullOrWhiteSpace(knownName))
            return NameMatch.None;

        var left = Tokenize(statementText);
        var right = Tokenize(knownName);
        if (left.Count == 0 || right.Count == 0) return NameMatch.None;

        // ตรงเป๊ะหลัง normalize — ทางลัดที่พบบ่อยสุด
        if (left.Count == right.Count
            && left.Select(t => t.Raw).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(right.Select(t => t.Raw).OrderBy(x => x, StringComparer.Ordinal)))
            return new NameMatch(1.0, "ชื่อตรงกันทุกคำ", left.Count, false, false);

        return AlignTokens(left, right);
    }

    /// <summary>
    /// เลือกชื่อที่ตรงที่สุดจากรายชื่อคู่ค้าที่มี — ใช้ตอนหา contact จาก
    /// บรรทัด statement. คืน null เมื่อไม่มีตัวไหนถึง
    /// <see cref="MinimumUsefulThreshold"/>
    /// </summary>
    public static (T Item, NameMatch Match)? Best<T>(
        string? statementText, IEnumerable<T> candidates, Func<T, string?> nameSelector)
    {
        (T Item, NameMatch Match)? best = null;
        foreach (var c in candidates)
        {
            var m = Match(statementText, nameSelector(c));
            if (m.Score < MinimumUsefulThreshold) continue;
            if (best == null || m.Score > best.Value.Match.Score) best = (c, m);
        }
        return best;
    }

    // ──────────────────────────────────────────────────────────────
    //  ชั้น 1 — Normalize + tokenize
    // ──────────────────────────────────────────────────────────────

    /// <summary>1 คำในชื่อ พร้อมคีย์เสียงที่คำนวณไว้ล่วงหน้า</summary>
    private readonly record struct NameToken(
        string Raw, string Skeleton, string Phonetic, bool IsThai, bool WasMasked);

    /// <summary>คำนำหน้าบุคคล — ตัดทิ้งเพราะไม่ช่วยแยกคน และมักมี/ไม่มีไม่ตรงกัน</summary>
    private static readonly HashSet<string> PersonTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "นาย", "นาง", "นางสาว", "น.ส.", "นส", "ด.ช.", "ด.ญ.", "เด็กชาย", "เด็กหญิง",
        "ว่าที่", "ร.ต.", "ร.ท.", "ร.อ.", "พ.ต.", "พ.ท.", "พ.อ.", "พล.ต.", "จ.ส.อ.",
        "ดร.", "ดร", "ศ.", "รศ.", "ผศ.", "นพ.", "พญ.", "ทพ.", "ภญ.", "คุณ",
        "mr", "mrs", "ms", "miss", "dr", "prof", "mr.", "mrs.", "ms.", "dr.",
    };

    /// <summary>รูปแบบนิติบุคคล — ตัดทิ้งเช่นกัน ("บริษัท x จำกัด" ต้องตรงกับ "X CO LTD")</summary>
    private static readonly HashSet<string> EntityForms = new(StringComparer.OrdinalIgnoreCase)
    {
        "บริษัท", "บจก", "บมจ", "บ", "หจก", "หสน", "ห้างหุ้นส่วนจำกัด", "ห้างหุ้นส่วน",
        "จำกัด", "มหาชน", "ร้าน", "สำนักงาน", "กลุ่ม", "คณะบุคคล", "มูลนิธิ", "สมาคม",
        "co", "ltd", "limited", "company", "corp", "corporation", "inc", "plc", "pcl",
        "public", "partnership", "lp", "llp", "group", "shop", "store", "foundation",
    };

    /// <summary>
    /// ขยะจากช่องทางการโอน — คำพวกนี้โผล่ทุกบรรทัดจึง "ตรงกัน" เสมอ
    /// ถ้าไม่ตัดออกจะดัน score ปลอมให้ทุกคู่
    /// </summary>
    private static readonly HashSet<string> ChannelNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "โอนเงินจาก", "โอนเงิน", "รับเงินจากการขายด้วย", "รับเงินจาก", "รับโอน", "เงินโอน",
        "โอนเข้า", "ฝากเงิน", "ถอนเงิน", "จ่ายเงิน", "ชำระเงิน", "ค่าสินค้า", "ค่าบริการ",
        "จาก", "ถึง", "ผ่าน", "สาขา", "บัญชี", "เลขที่",
        "thai", "qr", "payment", "promptpay", "prompt", "pay", "edc", "myqr", "atm",
        "transfer", "from", "to", "via", "ref", "no", "acc", "account", "branch",
        "deposit", "withdrawal", "inward", "outward", "credit", "debit", "smart",
        "bahtnet", "swift", "mobile", "banking", "internet", "shop", "pos", "cdm",
        "kplus", "scbeasy", "bualuang", "krungthai", "kbank", "scb", "bbl", "ktb",
        "tmb", "ttb", "uob", "cimb", "gsb", "baac", "lhbank", "kkp", "tisco",
    };

    /// <summary>ตัวปิดบังที่แบงก์ใช้ — token ที่มีตัวพวกนี้ถือว่า "ตัดคำ"</summary>
    private static bool IsMaskChar(char c) => c is '*' or 'x' or 'X' or '●' or '•' or '×';

    private static List<NameToken> Tokenize(string raw)
    {
        // แยกด้วยตัวคั่นทุกแบบที่เจอบน statement (| / , - _ . : ; ( ) เว้นวรรค)
        var parts = raw.Split(
            new[] { ' ', '\t', '\n', '\r', '|', '/', '\\', ',', '-', '_', '.', ':', ';',
                    '(', ')', '[', ']', '{', '}', '"', '\'', '+', '=', '@', '#' },
            StringSplitOptions.RemoveEmptyEntries);

        var result = new List<NameToken>();
        foreach (var p0 in parts)
        {
            var p = p0.Trim();
            if (p.Length == 0) continue;

            // ตัดตัวเลขล้วน / รหัสที่มีเลขปน (KB000001813229, X1234, 064-1-70621-3)
            // — ไม่ใช่ชื่อคน แต่ระวัง: ชื่อร้านมีเลขได้ ("7 ดาว") จึงตัดเฉพาะ
            // ที่ยาว ≥4 และมีเลขตั้งแต่ครึ่งหนึ่งขึ้นไป
            var digits = p.Count(char.IsDigit);
            if (digits > 0 && p.Length >= 4 && digits * 2 >= p.Length) continue;
            if (p.All(char.IsDigit)) continue;

            var masked = p.Any(IsMaskChar);
            // ตัดตัวปิดบังออก เหลือเฉพาะส่วนที่อ่านได้ ("D***" → "D")
            var visible = new string(p.Where(c => !IsMaskChar(c)).ToArray()).Trim();
            if (visible.Length == 0) continue;

            var lower = visible.ToLowerInvariant();
            if (PersonTitles.Contains(lower) || PersonTitles.Contains(visible)) continue;
            if (EntityForms.Contains(lower)) continue;
            if (ChannelNoise.Contains(lower)) continue;

            var (skel, phon) = ThaiPhonetics.BuildKeys(visible);
            if (skel.Length == 0 && phon.Length == 0) continue;

            result.Add(new NameToken(lower, skel, phon, ThaiPhonetics.HasThai(visible), masked));
        }

        // ไทยไม่เว้นวรรค: ถ้าเหลือ token ไทยตัวเดียวที่ยาวผิดปกติ (≥8 อักษร)
        // แปลว่าน่าจะเป็น "ชื่อ+สกุล" ติดกัน — ปล่อยไว้ทั้งก้อน แล้วให้ชั้น 3
        // (n-gram บนคีย์เสียง) จัดการ เพราะเราตัดคำไทยเองไม่ได้โดยไม่มี dictionary
        return result;
    }

    // ──────────────────────────────────────────────────────────────
    //  ชั้น 2 — จับคู่ token ข้ามภาษา (greedy best-match, ไม่สนลำดับ)
    // ──────────────────────────────────────────────────────────────
    private static NameMatch AlignTokens(List<NameToken> left, List<NameToken> right)
    {
        // จับคู่จากฝั่งที่ token น้อยกว่า → ทุก token ของฝั่งนั้นควรหาคู่ได้
        // (statement มักมีคำเกิน เช่นชื่อสาขาปน ส่วนชื่อในระบบสั้นกว่า)
        var (few, many) = left.Count <= right.Count ? (left, right) : (right, left);

        var usedInMany = new bool[many.Count];
        double sum = 0;
        int matched = 0;
        bool anyTruncated = false, anyCross = false, anyInitial = false;

        foreach (var t in few)
        {
            double bestScore = 0;
            int bestIdx = -1;
            bool bestTrunc = false, bestCross = false, bestInitial = false;

            for (int i = 0; i < many.Count; i++)
            {
                if (usedInMany[i]) continue;
                var (s, trunc, initial) = TokenScore(t, many[i]);
                if (s <= bestScore) continue;
                bestScore = s; bestIdx = i;
                bestTrunc = trunc; bestInitial = initial;
                bestCross = t.IsThai != many[i].IsThai;
            }

            if (bestIdx < 0 || bestScore < 0.5) continue;
            usedInMany[bestIdx] = true;
            sum += bestScore;
            matched++;
            anyTruncated |= bestTrunc || t.WasMasked;
            anyCross |= bestCross;
            anyInitial |= bestInitial;
        }

        if (matched == 0) return FallbackWholeString(left, right);

        // คะแนนดิบ = ค่าเฉลี่ยของคู่ที่จับได้ × สัดส่วนที่จับได้ของฝั่งสั้น
        // (จับได้ 1 จาก 2 คำ ต้องไม่ได้คะแนนเท่าจับได้ 2 จาก 2)
        var avg = sum / matched;
        var coverage = (double)matched / few.Count;
        var score = avg * (0.55 + 0.45 * coverage);

        // token ตัวเดียวเสี่ยงชนสูง (สกุลไทยซ้ำกันเยอะ) → หักไว้ก่อน
        // ให้ระบบไปหาสัญญาณอื่น (ยอด/วันที่) มาช่วยตัดสินแทน
        if (few.Count == 1 && matched == 1) score *= 0.82;

        // ตรงแค่อักษรแรก (W. ↔ วชิระ) เป็นหลักฐานอ่อน — เพดานไว้
        if (anyInitial && matched <= 1) score = Math.Min(score, 0.62);

        score = Math.Clamp(score, 0, 1);
        if (score < MinimumUsefulThreshold) return NameMatch.None;

        var reason = BuildReason(matched, few.Count, anyCross, anyTruncated, anyInitial);
        return new NameMatch(Math.Round(score, 4), reason, matched, anyTruncated, anyCross);
    }

    /// <summary>
    /// เทียบ token 2 ตัว. คืน (คะแนน, ถูกตัดคำ, ตรงแค่อักษรแรก)
    ///
    /// ลำดับการตัดสิน — เข้มไปหลวม หยุดที่เจอก่อน:
    ///   1. ตัวสะกดเดียวกันเป๊ะ                    → 1.00
    ///   2. คีย์เสียง (มีสระ) ตรง                  → 0.96
    ///   3. โครงพยัญชนะตรง                        → 0.92
    ///   4. โครงพยัญชนะเป็น prefix ของอีกฝั่ง       → 0.72–0.90 ตามความยาวที่เหลือ
    ///   5. อักษรแรกตรง + ฝั่งหนึ่งเป็นตัวย่อ       → 0.55
    ///   6. ความคล้ายระดับตัวอักษรบนคีย์เสียง      → ตามสัดส่วน
    /// </summary>
    private static (double Score, bool Truncated, bool Initial) TokenScore(NameToken a, NameToken b)
    {
        if (a.Raw == b.Raw) return (1.0, false, false);

        if (a.Phonetic.Length > 0 && a.Phonetic == b.Phonetic) return (0.96, false, false);
        if (a.Skeleton.Length > 0 && a.Skeleton == b.Skeleton) return (0.92, false, false);

        // ── ตัดคำ: โครงสั้นเป็นคำขึ้นต้นของโครงยาว ──
        // "WACHI"→"wc" กับ "วชิระ"→"wcr": "wc" เป็น prefix ของ "wcr" ✓
        var (shortK, longK) = a.Skeleton.Length <= b.Skeleton.Length
            ? (a.Skeleton, b.Skeleton) : (b.Skeleton, a.Skeleton);
        if (shortK.Length >= 2 && longK.StartsWith(shortK, StringComparison.Ordinal))
        {
            // ยิ่งเหลือน้อยยิ่งมั่นใจ — ตรง 2 ใน 3 ตัว ดีกว่าตรง 2 ใน 6
            var ratio = (double)shortK.Length / longK.Length;
            return (0.72 + 0.18 * ratio, true, false);
        }
        // prefix บนคีย์ที่มีสระด้วย — จับ "Wachi" ↔ "Wachira" ที่ skeleton
        // ยุบไปเหมือนกันหมดจนแยกไม่ออก
        var (shortP, longP) = a.Phonetic.Length <= b.Phonetic.Length
            ? (a.Phonetic, b.Phonetic) : (b.Phonetic, a.Phonetic);
        if (shortP.Length >= 3 && longP.StartsWith(shortP, StringComparison.Ordinal))
        {
            var ratio = (double)shortP.Length / longP.Length;
            return (0.70 + 0.20 * ratio, true, false);
        }

        // ── ตัวย่ออักษรแรก: "W." ↔ "Wachira" / "ว." ↔ "วชิระ" ──
        if (shortK.Length == 1 && longK.Length >= 2 && longK[0] == shortK[0])
            return (0.55, true, true);

        // ── ความคล้ายระดับตัวอักษรบนคีย์เสียง (typo / ถอดเสียงเพี้ยน) ──
        if (a.Phonetic.Length >= 3 && b.Phonetic.Length >= 3)
        {
            var sim = Ocr.FuzzyMatcher.Similarity(a.Phonetic, b.Phonetic);
            if (sim >= 0.75) return (sim * 0.88, false, false);
        }
        return (0, false, false);
    }

    // ──────────────────────────────────────────────────────────────
    //  ชั้น 3 — ทั้งก้อน (ใช้เมื่อ token align ไม่ติดเลย)
    // ──────────────────────────────────────────────────────────────
    /// <summary>
    /// เคสที่ token align แพ้: ไทยเขียนติดกัน ("วชิระดิลกสัมพันธ์") หรือ
    /// statement ยัดชื่อทั้งหมดเป็นคำเดียว ต่อคีย์เสียงทุก token เข้าด้วยกัน
    /// แล้ววัดความคล้ายระดับตัวอักษร — ลำดับยังสำคัญอยู่ จึงเป็นชั้นท้ายสุด
    /// </summary>
    private static NameMatch FallbackWholeString(List<NameToken> left, List<NameToken> right)
    {
        var l = string.Concat(left.Select(t => t.Skeleton));
        var r = string.Concat(right.Select(t => t.Skeleton));
        if (l.Length < 3 || r.Length < 3) return NameMatch.None;

        // ฝั่งสั้นเป็นคำขึ้นต้นของฝั่งยาว = ถูกตัดท้าย (แบงก์จำกัดความยาว)
        var (s, g) = l.Length <= r.Length ? (l, r) : (r, l);
        if (s.Length >= 3 && g.StartsWith(s, StringComparison.Ordinal))
        {
            var ratio = (double)s.Length / g.Length;
            var sc = 0.66 + 0.24 * ratio;
            return new NameMatch(Math.Round(sc, 4), "ชื่อถูกตัดท้าย — ส่วนต้นตรงกัน", 1, true,
                left.Any(t => t.IsThai) != right.Any(t => t.IsThai));
        }
        // ฝั่งสั้นโผล่อยู่กลางฝั่งยาว = ชื่อในระบบซ่อนอยู่ในบรรทัด statement
        if (s.Length >= 4 && g.Contains(s, StringComparison.Ordinal))
        {
            var ratio = (double)s.Length / g.Length;
            var sc = 0.62 + 0.22 * ratio;
            return new NameMatch(Math.Round(sc, 4), "พบชื่อในข้อความรายการ", 1, true,
                left.Any(t => t.IsThai) != right.Any(t => t.IsThai));
        }

        var sim = Ocr.FuzzyMatcher.Similarity(l, r);
        if (sim < 0.70) return NameMatch.None;
        var score = Math.Round(sim * 0.80, 4);
        if (score < MinimumUsefulThreshold) return NameMatch.None;
        return new NameMatch(score, "เสียงชื่อใกล้เคียงกัน", 1, false,
            left.Any(t => t.IsThai) != right.Any(t => t.IsThai));
    }

    private static string BuildReason(int matched, int total, bool cross, bool trunc, bool initial)
    {
        var parts = new List<string>();
        parts.Add(matched >= total ? $"ชื่อตรงครบ {matched} คำ" : $"ชื่อตรง {matched}/{total} คำ");
        if (cross) parts.Add("เทียบข้ามภาษา ไทย↔อังกฤษ");
        if (initial) parts.Add("บางคำเป็นตัวย่อ");
        else if (trunc) parts.Add("บางคำถูกตัด/ปิดบัง");
        return string.Join(" · ", parts);
    }
}
