using System.Text.RegularExpressions;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Map a Thai accounting document (vendor name + line-item descriptions +
/// header text) to (1) an expense category label, (2) the typical Thai
/// chart-of-account code, and (3) the statutory withholding-tax rate per
/// Revenue Code Section 50.
///
/// Why this exists separately from ExpenseCategoryLearner:
///   ExpenseCategoryLearner learns from each company's historical bookings
///   (per-(vendor,description) → account). It's company-specific. Here we
///   encode Thailand-wide DEFAULT classification — "any vendor whose name
///   contains 'PTT' is most likely fuel" — that bootstraps brand-new tenants
///   before they have any history of their own.
///
/// Per-industry weighting: the company's Industry context (Trading /
/// Service / Manufacturing / Restaurant / ...) biases which categories
/// score higher. A manufacturing company's invoice for "วัสดุ" should
/// resolve to raw-material expense; a service company's same description
/// should lean to office supplies. The boost is multiplicative on top of
/// the keyword score, so industry context only nudges — it never flips a
/// strong keyword match.
///
/// The resolver returns a confidence score so the caller can decide whether
/// to overwrite an already-extracted category or only fill in gaps.
/// </summary>
internal static class ExpenseCategoryResolver
{
    public record CategoryResult(
        string Category,          // Thai display label ("ค่าน้ำมัน")
        string AccountCode,       // CoA code ("5402")
        string AccountName,       // Account name ("ค่าน้ำมันเชื้อเพลิง")
        decimal? StatutoryWhtRate,// 0/1/2/3/5/10/15 — null when not applicable
        string? WhtIncomeTypeCode,// รหัส ม.40 (ThaiWhtRateTable) — คู่กับอัตราเสมอ
        decimal Confidence,
        List<string> Reasons,
        /// <summary>หลักฐานที่ทำให้กฎนี้ชนะ **ผูกกับเงิน**หรือไม่ (เจอในชื่อผู้ขาย/
        /// หัวเรื่อง/บรรทัดที่มียอด) — false = เจอเฉพาะใน rawText หรือแถวยอด 0
        /// ⇒ เสนอหมวดได้ แต่**ห้ามเสนอประเภทเงินได้ ม.40 / อัตราหัก ณ ที่จ่าย**</summary>
        bool MoneyBackedEvidence = true);

    /// <summary>Returns null when no rule matches.</summary>
    /// <param name="lineDescriptions">คำอธิบายของบรรทัดที่ **มีจำนวนเงิน** —
    /// หลักฐานน้ำหนักเต็ม</param>
    /// <param name="zeroAmountLineDescriptions">คำอธิบายของบรรทัดที่ยอด = 0
    /// (แถวที่แบบฟอร์มพิมพ์ไว้ตายตัว เช่น "ค่าจัดส่ง / Shipping Fee 0.00" ที่ขึ้น
    /// ทุกใบไม่ว่าจะมีค่าส่งจริงหรือไม่) — **หลักฐานอ่อน** ชั้นเดียวกับ rawText
    /// และ**ห้ามใช้ตัดสินประเภทเงินได้ ม.40** เพราะยอด 0 ไม่ได้บอกว่าจ่ายอะไรจริง
    /// (บทเรียนเดียวกับฟอร์ม 50 ทวิ ที่พิมพ์ทุกประเภทไว้ให้ติ๊ก: สิ่งที่ตอบคำถาม
    /// คือ**แถวที่มีจำนวนเงิน** ไม่ใช่คำที่ปรากฏบนหน้า)</param>
    public static CategoryResult? Resolve(
        string? vendorName,
        string? headerDescription,
        IEnumerable<string?>? lineDescriptions,
        string? rawText,
        IndustryType? industry = null,
        BusinessType? businessType = null,
        IEnumerable<string?>? zeroAmountLineDescriptions = null)
    {
        var reasons = new List<string>();
        // สามชั้น: ข้อมูลที่ **ผูกกับเงิน** (ผู้ขาย/หัวเรื่อง/บรรทัดที่มียอด)
        // น้ำหนักเต็ม · บรรทัดยอด 0 + rawText ทั้งใบเป็นหลักฐานอ่อน (มีข้อความแฝง
        // เยอะ เช่น เงื่อนไขท้ายบิลที่มีคำว่า "ที่ปรึกษา"/"ภาษีอากร") นับครึ่งเดียว
        var primaryCorpus = BuildCorpus(vendorName, headerDescription, lineDescriptions, null).ToLowerInvariant();
        var weakParts = BuildCorpus(null, null, zeroAmountLineDescriptions, rawText);
        var corpus = (primaryCorpus + " " + weakParts).ToLowerInvariant();

        // Score every rule against the corpus; highest wins.
        CategoryRule? best = null;
        decimal bestScore = 0m;
        bool bestMoneyBacked = false;
        foreach (var rule in Rules)
        {
            decimal kwScore = 0m;
            // "หลักฐานผูกกับเงิน" = คำที่เจอในชั้นน้ำหนักเต็ม (ชื่อผู้ขาย/หัวเรื่อง/
            // บรรทัดที่มียอด) — ใช้เป็นเงื่อนไขของการเสนอ **ประเภทเงินได้ ม.40**
            // ห้ามให้คำที่เจอเฉพาะใน rawText/แถวยอด 0 ตัดสินอัตราหัก ณ ที่จ่าย
            bool moneyBacked = false;
            foreach (var kw in rule.Keywords)
            {
                var k = kw.ToLowerInvariant();
                if (primaryCorpus.Contains(k)) { kwScore += kw.Length >= 6 ? 2m : 1m; moneyBacked = true; }
                else if (corpus.Contains(k)) kwScore += kw.Length >= 6 ? 1m : 0.5m;
            }
            // Vendor-brand matches outweigh single keyword hits because they're
            // far more discriminating ("ปตท." in a vendor name strongly
            // implies fuel; "ปตท." in a random product description doesn't).
            bool brandExactHit = false;
            foreach (var brand in rule.VendorBrands)
                if (!string.IsNullOrEmpty(vendorName)
                    && vendorName.ToLowerInvariant().Contains(brand.ToLowerInvariant()))
                { kwScore += 4; brandExactHit = true; moneyBacked = true; }

            // Fuzzy brand fallback: when exact substring missed, use char
            // n-gram cosine to catch OCR variants of brand names ("ปตท."
            // misread as "ปตธ.", "บริษัท ปตท จำกัด" vs short "ปตท") —
            // half-weight because fuzzy is less reliable than exact.
            if (!brandExactHit && !string.IsNullOrEmpty(vendorName))
            {
                foreach (var brand in rule.VendorBrands)
                {
                    var sim = CharNgramSimilarity.Similarity(vendorName, brand);
                    if (sim >= 0.65)
                    {
                        kwScore += 2;     // half of exact match's +4
                        moneyBacked = true;   // ชื่อผู้ขายคือหลักฐานที่ผูกกับเงินเสมอ
                        break;
                    }
                }
            }
            if (kwScore == 0) continue;

            // Industry bias: multiply the raw keyword score by the
            // per-industry weight. Default weight is 1.0 (no change).
            var industryWeight = IndustryWeight(rule.Category, industry);
            decimal score = kwScore * industryWeight;
            if (score > bestScore) { bestScore = score; best = rule; bestMoneyBacked = moneyBacked; }
        }

        if (best == null || bestScore <= 0) return null;

        // Confidence: 0.5 for ~1 keyword, ramping to 0.95 at score≥6
        var conf = Math.Min(0.95m, 0.4m + 0.1m * bestScore);
        var reasonText = industry.HasValue && industry != IndustryType.General
            ? $"จับคู่ '{best.Category}' จาก score {bestScore:F1} (industry={industry.Value})"
            : $"จับคู่ '{best.Category}' จาก score {bestScore:F1}";
        reasons.Add(reasonText);
        if (!bestMoneyBacked && best.StatutoryWhtRate is > 0m)
            reasons.Add($"หลักฐานของ '{best.Category}' อยู่นอกบรรทัดที่มีจำนวนเงิน "
                + "(ข้อความทั้งใบ/แถวยอด 0) → เสนอหมวดได้ แต่ไม่เสนอประเภทเงินได้/อัตราหัก ณ ที่จ่าย");
        return new CategoryResult(best.Category, best.AccountCode, best.AccountName,
            best.StatutoryWhtRate, best.WhtIncomeTypeCode, conf, reasons, bestMoneyBacked);
    }

    /// <summary>Per-industry weighting factor for a category. Returns 1.0
    /// when the industry is unknown / General. Values > 1.0 boost
    /// categories that are typical for that industry; values < 1.0
    /// dampen ones that are atypical.</summary>
    private static decimal IndustryWeight(string category, IndustryType? industry)
    {
        if (!industry.HasValue || industry.Value == IndustryType.General) return 1.0m;

        // ── "ซื้อสินค้า / วัตถุดิบ" (51110 ต้นทุนสินค้า) กับกิจการที่ไม่ถือสต๊อก ──
        //
        // ⚠️ rule นี้ชนะด้วย **ชื่อผู้ขายเป็นแบรนด์ค้าส่ง** (+4 คะแนน) โดยไม่ดูเลย
        // ว่าบริษัทเราขายอะไร ⇒ บริษัทซอฟต์แวร์/คลินิก/สำนักงานบัญชีที่ซื้อกาแฟ ·
        // กระดาษ A4 · สายไฟ ที่ Makro/HomePro/ไทวัสดุ ได้ผัง "ต้นทุนสินค้า" ทุกใบ
        // ⇒ กำไรขั้นต้นในงบเพี้ยน และ §65 ตรี(3) เครื่องดื่มพนักงานอาจเป็นสวัสดิการ
        // (ผลตรวจ 2026-09-06 · T1-16) — resolver รับ industry เข้ามาแล้วแต่ rule
        // นี้ไม่ได้ใช้
        //
        // ทำไม 0.25 ไม่ใช่ 0: คอมเมนต์ของ rule นั้นบอกเจตนาไว้ถูกว่ามันมีไว้กัน
        // "บิล Makro ที่อ่านรายการไม่ได้ แล้วแพ้ keyword หลง ๆ ในข้อความท้ายบิล" —
        // ตัดทิ้งเลยจะคืนบั๊กนั้นกลับมา · 0.25 ทำให้แบรนด์เดี่ยว (4 → 1.0) **แพ้**
        // หมวดที่มี keyword จริงบนบรรทัด (คำยาว ≥6 ตัวในคลังหลัก = 2 คะแนน/คำ)
        // แต่ยัง**ชนะเมื่อไม่มีอะไรอื่นเลย** และ confidence ตกเหลือ 0.50 ⇒ หน้า
        // review ไฮไลต์เหลืองตามกฎเหล็ก #3 ให้คนดูแทนที่จะเงียบ
        if (category == "ซื้อสินค้า / วัตถุดิบ"
            && !Accounting.Helpers.InventoryIndustry.KeepsInventory(industry))
            return 0.25m;

        // Bias matrix — keys are subsets of the rule's Category Thai labels.
        // Values are multiplicative factors applied to the raw keyword score.
        var weights = (industry.Value, category) switch
        {
            // Manufacturing — material + repair + utility lean
            (IndustryType.Manufacturing, "ค่าน้ำมันเชื้อเพลิง") => 1.4m,
            (IndustryType.Manufacturing, "ค่าซ่อมแซมและบำรุงรักษา") => 1.5m,
            (IndustryType.Manufacturing, "ค่าไฟฟ้า") => 1.4m,
            (IndustryType.Manufacturing, "ค่าน้ำประปา") => 1.3m,
            (IndustryType.Manufacturing, "ค่าขนส่ง / ค่าจัดส่ง") => 1.4m,
            (IndustryType.Manufacturing, "ค่ารับรอง") => 0.7m,

            // Service — consulting + telephony + travel lean
            (IndustryType.Service, "ค่าที่ปรึกษากฎหมาย / บัญชี") => 1.4m,
            (IndustryType.Service, "ค่าบริการ") => 1.3m,
            (IndustryType.Service, "ค่าโทรศัพท์ / อินเทอร์เน็ต") => 1.3m,
            (IndustryType.Service, "ค่าเดินทาง") => 1.3m,
            (IndustryType.Service, "ค่าซ่อมแซมและบำรุงรักษา") => 0.8m,

            // Restaurant / Cafe — supplies + utilities + rent lean
            (IndustryType.Restaurant, "ค่าวัสดุสำนักงาน") => 1.3m,
            (IndustryType.Restaurant, "ค่าไฟฟ้า") => 1.5m,
            (IndustryType.Restaurant, "ค่าน้ำประปา") => 1.5m,
            (IndustryType.Restaurant, "ค่าทำความสะอาด") => 1.5m,
            (IndustryType.Restaurant, "ค่าเช่า") => 1.4m,
            (IndustryType.Cafe, "ค่าไฟฟ้า") => 1.5m,
            (IndustryType.Cafe, "ค่าน้ำประปา") => 1.4m,
            (IndustryType.Cafe, "ค่าเช่า") => 1.5m,
            (IndustryType.Cafe, "ค่าทำความสะอาด") => 1.4m,

            // Trading / Retail / Ecommerce — transport + advertising lean
            (IndustryType.Trading, "ค่าขนส่ง / ค่าจัดส่ง") => 1.5m,
            (IndustryType.Trading, "ค่าโฆษณาและส่งเสริมการขาย") => 1.4m,
            (IndustryType.Retail, "ค่าโฆษณาและส่งเสริมการขาย") => 1.4m,
            (IndustryType.Retail, "ค่าเช่า") => 1.4m,
            (IndustryType.Retail, "ค่าทำความสะอาด") => 1.3m,
            (IndustryType.Ecommerce, "ค่าขนส่ง / ค่าจัดส่ง") => 1.6m,
            (IndustryType.Ecommerce, "ค่าโฆษณาและส่งเสริมการขาย") => 1.5m,

            // Construction — fuel + repair + transport lean
            (IndustryType.Construction, "ค่าน้ำมันเชื้อเพลิง") => 1.5m,
            (IndustryType.Construction, "ค่าซ่อมแซมและบำรุงรักษา") => 1.4m,
            (IndustryType.Construction, "ค่าขนส่ง / ค่าจัดส่ง") => 1.4m,
            (IndustryType.Construction, "ค่าวัสดุสำนักงาน") => 0.8m,

            // Transportation — heavy fuel + repair + insurance
            (IndustryType.Transportation, "ค่าน้ำมันเชื้อเพลิง") => 1.7m,
            (IndustryType.Transportation, "ค่าซ่อมแซมและบำรุงรักษา") => 1.5m,
            (IndustryType.Transportation, "ค่าประกัน") => 1.4m,

            // Technology — internet + consulting lean
            (IndustryType.Technology, "ค่าโทรศัพท์ / อินเทอร์เน็ต") => 1.5m,
            (IndustryType.Technology, "ค่าที่ปรึกษากฎหมาย / บัญชี") => 1.3m,
            (IndustryType.Technology, "ค่าโฆษณาและส่งเสริมการขาย") => 1.3m,

            // Healthcare — utilities + cleaning + insurance lean
            (IndustryType.Healthcare, "ค่าทำความสะอาด") => 1.5m,
            (IndustryType.Healthcare, "ค่าไฟฟ้า") => 1.3m,
            (IndustryType.Healthcare, "ค่าประกัน") => 1.3m,

            // Hotel — utilities + cleaning + maintenance lean
            (IndustryType.Hotel, "ค่าไฟฟ้า") => 1.5m,
            (IndustryType.Hotel, "ค่าน้ำประปา") => 1.5m,
            (IndustryType.Hotel, "ค่าทำความสะอาด") => 1.6m,
            (IndustryType.Hotel, "ค่าซ่อมแซมและบำรุงรักษา") => 1.3m,

            _ => 1.0m
        };
        return weights;
    }

    private static string BuildCorpus(string? v, string? h, IEnumerable<string?>? lines, string? raw)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(v)) parts.Add(v);
        if (!string.IsNullOrEmpty(h)) parts.Add(h);
        if (lines != null) parts.AddRange(lines.Where(l => !string.IsNullOrEmpty(l))!);
        if (!string.IsNullOrEmpty(raw)) parts.Add(raw);
        return string.Join(" ", parts);
    }

    /// <param name="WhtIncomeTypeCode">รหัสประเภทเงินได้ ม.40 ตาม
    /// <see cref="Accounting.Helpers.ThaiWhtRateTable"/> — ต้องมาคู่กับ
    /// <paramref name="StatutoryWhtRate"/> เสมอ เพราะ **หนังสือรับรอง 50 ทวิ และ
    /// ภ.ง.ด.3/53 ต้องระบุประเภทเงินได้** ไม่ใช่แค่อัตรา (ผลตรวจ 2026-09-06 · T4-06:
    /// ระบบไม่มีช่องนี้ทั้งสาย ⇒ ฉากหักภาษี ณ ที่จ่ายทำ 1-click ไม่ได้จริง)</param>
    private record CategoryRule(
        string Category,
        string AccountCode,
        string AccountName,
        decimal? StatutoryWhtRate,
        string[] Keywords,
        string[] VendorBrands,
        string? WhtIncomeTypeCode = null);

    // ─── Rule library ───────────────────────────────────────────────────
    // Order doesn't matter — we score every rule and pick the best.
    // Keywords use lower-case Thai/English; brand matches look at vendor name.
    // Statutory WHT rates per Revenue Code Section 50; null = not standard
    // (the user will fill in based on the vendor's tax status).
    private static readonly CategoryRule[] Rules = new[]
    {
        // ─── น้ำมันเชื้อเพลิง — 5402 ───
        new CategoryRule("ค่าน้ำมันเชื้อเพลิง", "5402", "ค่าน้ำมันเชื้อเพลิง",
            StatutoryWhtRate: null,
            Keywords: new[] {
                "น้ำมัน", "เบนซิน", "ดีเซล", "แก๊สโซฮอล์", "fuel", "petrol", "diesel",
                "gasoline", "ปั๊มน้ำมัน", "เติมน้ำมัน", "บริการสถานี"
            },
            VendorBrands: new[] {
                "ปตท", "ptt", "บางจาก", "bangchak", "esso", "shell", "เชลล์",
                "caltex", "คาลเท็กซ์", "จุฬากรณ์", "susco"
            }),

        // ─── ค่าไฟฟ้า — 5303 (WHT 1% only for energy-saving service contracts) ───
        new CategoryRule("ค่าไฟฟ้า", "5303", "ค่าไฟฟ้า",
            StatutoryWhtRate: null,
            Keywords: new[] { "ค่าไฟ", "ไฟฟ้า", "การไฟฟ้า", "electricity", "electric bill", "energy" },
            VendorBrands: new[] { "การไฟฟ้านครหลวง", "การไฟฟ้าส่วนภูมิภาค", "mea", "pea" }),

        // ─── ค่าน้ำประปา — 5302 ───
        new CategoryRule("ค่าน้ำประปา", "5302", "ค่าน้ำประปา",
            StatutoryWhtRate: null,
            Keywords: new[] { "ค่าน้ำ", "น้ำประปา", "การประปา", "water bill", "water supply" },
            VendorBrands: new[] { "การประปานครหลวง", "การประปาส่วนภูมิภาค", "mwa", "pwa" }),

        // ─── ค่าโทรศัพท์/อินเทอร์เน็ต — 5304 (3% WHT for telecom services) ───
        new CategoryRule("ค่าโทรศัพท์ / อินเทอร์เน็ต", "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต",
            StatutoryWhtRate: 3m,
            Keywords: new[] {
                "โทรศัพท์", "phone", "internet", "อินเทอร์เน็ต", "บรอดแบนด์",
                "fiber", "ไฟเบอร์", "wifi", "ค่ามือถือ", "ค่าสัญญาณ"
            },
            VendorBrands: new[] {
                "ais", "เอไอเอส", "true", "ทรู", "dtac", "ดีแทค", "tot", "ทีโอที",
                "cat", "3bb", "nt", "เอ็นที"
            },
            WhtIncomeTypeCode: "8"),

        // ─── ค่าเช่า — 5301 (5% WHT mandatory) ───
        new CategoryRule("ค่าเช่า", "5301", "ค่าเช่า",
            StatutoryWhtRate: 5m,
            Keywords: new[] {
                "ค่าเช่า", "rent", "rental", "lease", "เช่า", "เช่าสำนักงาน", "เช่าอาคาร",
                "เช่ารถ", "เช่าเครื่อง", "ค่าที่จอด"
            },
            VendorBrands: Array.Empty<string>(),
            WhtIncomeTypeCode: "5"),

        // ─── ค่าโฆษณา — 5101 (2% WHT) ───
        new CategoryRule("ค่าโฆษณาและส่งเสริมการขาย", "5101", "ค่าโฆษณาและส่งเสริมการขาย",
            StatutoryWhtRate: 2m,
            Keywords: new[] {
                "โฆษณา", "advertise", "advertising", "ads", "google ads", "facebook ads",
                "tiktok ads", "promotion", "ส่งเสริมการขาย", "ป้าย", "บิลบอร์ด",
                "เผยแพร่", "marketing"
            },
            VendorBrands: new[] { "google", "facebook", "meta", "tiktok", "line ads" },
            WhtIncomeTypeCode: "8ad"),

        // ─── ค่าขนส่ง / จัดส่ง — 5102 (1% WHT for transport services) ───
        new CategoryRule("ค่าขนส่ง / ค่าจัดส่ง", "5102", "ค่าขนส่ง",
            StatutoryWhtRate: 1m,
            Keywords: new[] {
                "ขนส่ง", "transport", "shipping", "freight", "ส่งของ", "จัดส่ง",
                "ค่าส่ง", "delivery", "logistic", "ขนถ่าย"
            },
            VendorBrands: new[] {
                "kerry", "เคอรี่", "flash", "แฟลช", "j&t", "เจแอนด์ที", "thai post",
                "ไปรษณีย์ไทย", "dhl", "fedex", "ems", "ninja"
            },
            WhtIncomeTypeCode: "8tr"),

        // ─── ซื้อสินค้า / วัตถุดิบ — ค้าส่ง/ค้าปลีกรายใหญ่ (ไม่มี WHT) ───
        // Makro/Lotus/BigC ฯลฯ = ซื้อของเข้าร้าน/วัตถุดิบเกือบเสมอ — ก่อนมี
        // rule นี้ บิล Makro ที่อ่านรายการไม่ได้จะแพ้ให้ keyword หลง ๆ ใน
        // rawText (เช่น "ที่ปรึกษา" ในข้อความท้ายบิล) แล้วหมวดเพี้ยนทั้งใบ
        new CategoryRule("ซื้อสินค้า / วัตถุดิบ", "51110", "ต้นทุนสินค้า",
            StatutoryWhtRate: null,
            Keywords: new[] {
                "ซื้อสินค้า", "วัตถุดิบ", "สินค้าเพื่อขาย", "wholesale", "ของเข้าร้าน"
            },
            VendorBrands: new[] {
                "makro", "แม็คโคร", "แมคโคร", "siam makro", "lotus", "โลตัส",
                "big c", "บิ๊กซี", "bigc", "cj more", "cj express", "ซีเจ",
                "homepro", "โฮมโปร", "ไทวัสดุ", "thai watsadu", "thaiwatsadu",
                "global house", "โกลบอลเฮ้าส์", "dohome", "ดูโฮม",
                "villa market", "gourmet market", "tops", "ท็อปส์"
            }),

        // ─── ค่าบริการ / ค่าแรง — 5300 generic, 3% WHT ───
        new CategoryRule("ค่าบริการ", "5300", "ค่าใช้จ่ายบริหาร",
            StatutoryWhtRate: 3m,
            Keywords: new[] {
                "ค่าบริการ", "service fee", "consulting service", "ค่าจ้าง", "รับจ้าง",
                "ค่าแรง", "labor"
            },
            VendorBrands: Array.Empty<string>(),
            WhtIncomeTypeCode: "8"),

        // ─── ค่าที่ปรึกษากฎหมาย / ตรวจสอบบัญชี — 5502 (3% WHT) ───
        new CategoryRule("ค่าที่ปรึกษากฎหมาย / บัญชี", "5502", "ค่าที่ปรึกษา",
            StatutoryWhtRate: 3m,
            Keywords: new[] {
                "ที่ปรึกษา", "consultant", "consultancy", "audit", "ตรวจสอบบัญชี",
                "นักบัญชี", "นิติกร", "ทนาย", "lawyer", "auditor", "ภาษีอากร", "tax service"
            },
            VendorBrands: Array.Empty<string>(),
            WhtIncomeTypeCode: "6"),

        // ─── ค่าธรรมเนียมธนาคาร — 5503 (no WHT, banks deduct themselves) ───
        new CategoryRule("ค่าธรรมเนียมธนาคาร", "5503", "ค่าธรรมเนียมธนาคาร",
            StatutoryWhtRate: null,
            Keywords: new[] {
                "ค่าธรรมเนียม", "bank fee", "bank charge", "service charge",
                "atm fee", "ค่าโอน", "ค่าทวงถาม"
            },
            VendorBrands: new[] {
                "ธนาคาร", "bank", "scb", "ไทยพาณิชย์", "kbank", "กสิกร", "bbl", "กรุงเทพ",
                "ktb", "กรุงไทย", "tmb", "ทีเอ็มบี", "uobt", "krungsri", "กรุงศรี"
            }),

        // ─── ค่าวัสดุสำนักงาน — 5305 ───
        new CategoryRule("ค่าวัสดุสำนักงาน", "5305", "ค่าวัสดุสำนักงาน",
            StatutoryWhtRate: null,
            Keywords: new[] {
                "วัสดุสำนักงาน", "office supplies", "กระดาษ", "ปากกา", "หมึก", "ink",
                "toner", "stationery", "เครื่องเขียน", "แฟ้ม", "เทป", "กาว"
            },
            VendorBrands: new[] {
                "office mate", "ออฟฟิศเมท", "b2s", "บีทูเอส", "se-ed", "ซีเอ็ด"
            }),

        // ─── ค่าซ่อมแซมและบำรุงรักษา — 5306 (3% WHT for repair services) ───
        new CategoryRule("ค่าซ่อมแซมและบำรุงรักษา", "5306", "ค่าซ่อมแซมและบำรุงรักษา",
            StatutoryWhtRate: 3m,
            Keywords: new[] {
                "ซ่อม", "repair", "maintenance", "บำรุง", "บำรุงรักษา", "อะไหล่",
                "spare part", "ดูแล", "เปลี่ยน", "ติดตั้ง", "install"
            },
            VendorBrands: Array.Empty<string>(),
            WhtIncomeTypeCode: "8"),

        // ─── ค่าเดินทาง — 5401 (no WHT for personal travel reimbursement) ───
        new CategoryRule("ค่าเดินทาง", "5401", "ค่าเดินทาง",
            StatutoryWhtRate: null,
            Keywords: new[] {
                "ค่าเดินทาง", "travel", "taxi", "แท็กซี่", "grab", "เกร็บ", "uber",
                "bts", "mrt", "รถไฟฟ้า", "airline", "สายการบิน", "ticket", "ตั๋ว",
                "hotel", "โรงแรม", "ที่พัก"
            },
            VendorBrands: new[] {
                "grab", "bolt", "robinhood", "true", "thai airways", "การบินไทย",
                "airasia", "แอร์เอเชีย", "nokair", "นกแอร์", "agoda", "booking.com"
            }),

        // ─── ค่ารับรอง — 5403 ───
        new CategoryRule("ค่ารับรอง", "5403", "ค่ารับรอง",
            StatutoryWhtRate: null,
            Keywords: new[] { "ค่ารับรอง", "entertain", "entertainment", "เลี้ยงรับรอง" },
            VendorBrands: Array.Empty<string>()),

        // ─── ค่าทำความสะอาด — 5307 (3% WHT) ───
        new CategoryRule("ค่าทำความสะอาด", "5307", "ค่าทำความสะอาด",
            StatutoryWhtRate: 3m,
            Keywords: new[] {
                "ทำความสะอาด", "cleaning", "แม่บ้าน", "housekeeping", "กำจัดปลวก",
                "pest control"
            },
            VendorBrands: Array.Empty<string>(),
            WhtIncomeTypeCode: "8"),

        // ─── ค่าประกัน — 5800 ───
        new CategoryRule("ค่าประกัน", "5800", "ค่าใช้จ่ายในการประกัน",
            StatutoryWhtRate: null,
            Keywords: new[] {
                "ประกัน", "insurance", "premium", "เบี้ยประกัน", "พรบ", "ประกันภัย",
                "ประกันชีวิต", "ประกันสุขภาพ"
            },
            VendorBrands: new[] {
                "เมืองไทยประกัน", "muang thai", "aia", "เอไอเอ", "thai life", "ไทยประกันชีวิต",
                "viriyah", "วิริยะ", "axa", "fwd"
            }),

        // ─── ดอกเบี้ยจ่าย — 5701 (15% WHT) ───
        new CategoryRule("ดอกเบี้ยจ่าย", "5701", "ดอกเบี้ยจ่าย",
            StatutoryWhtRate: 15m,
            Keywords: new[] { "ดอกเบี้ย", "interest", "loan interest", "ดอกเบี้ยเงินกู้" },
            VendorBrands: Array.Empty<string>(),
            WhtIncomeTypeCode: "4a"),
    };

    /// <summary>Apply the resolver's output to an OcrExtractedData, filling in
    /// expense category, debit account, and inferring statutory WHT when the
    /// extracted rate was missing. Conservative — only fills empty fields.
    /// <param name="rawText">Raw OCR text. When supplied, WHT is only inferred
    /// from the statutory rate when the document ALSO explicitly mentions WHT
    /// (keyword "หัก ณ ที่จ่าย" / "WHT" / "Withholding Tax" / "ภงด" etc.) —
    /// otherwise a clean invoice that happens to fall into a WHT-eligible
    /// category gets a misleading auto-3% added when the actual receipt
    /// shows no withholding line.</param></summary>
    public static void ApplyTo(OcrExtractedData data, CategoryResult result, string? rawText = null)
    {
        if (string.IsNullOrEmpty(data.ExpenseCategory))
        {
            data.ExpenseCategory = result.Category;
            data.FieldConfidence["ExpenseCategory"] = (double)result.Confidence;
        }
        if (string.IsNullOrEmpty(data.DebitAccountCode))
        {
            data.DebitAccountCode = result.AccountCode;
            data.DebitAccountName = result.AccountName;
            data.FieldConfidence["DebitAccount"] = (double)result.Confidence;
        }
        // Infer WHT only when (1) the doc didn't already pick one up,
        // (2) the category has a statutory rate, AND (3) the raw text
        // explicitly mentions WHT. The user-visible rule: if the actual
        // receipt shows no WHT line, don't auto-suggest 1%/3%/etc. just
        // because the expense category technically allows it.
        bool docMentionsWht = rawText != null && ContainsWhtKeyword(rawText);

        // ── ข้อเสนอตามกฎหมาย (แยกจาก "ยอดที่พิมพ์บนกระดาษ") · T1-07 ──────────
        // ผู้ขายไทยเกือบทั้งหมด**ไม่พิมพ์** WHT บนใบแจ้งหนี้ เพราะหน้าที่หักเป็นของ
        // ผู้จ่าย ⇒ กติกาเดิม (ต้องมีคำว่า WHT บนกระดาษ) แทบไม่เคยเป็นจริง และผู้จ่าย
        // รับผิด ม.54 ถ้าลืมหัก. เก็บเป็น **ข้อเสนอ** ไม่ตั้ง HasWht ให้เอง —
        // หักเกินก็ผิด (ผู้รับต้องไปขอคืน) จึงให้คนกดยืนยัน
        //
        // ⚠️ ต้องมี **หลักฐานที่ผูกกับเงิน** ด้วย — ใบจริง (TXE05202609T000434) มีแถว
        // "ค่าจัดส่ง / Shipping Fee 0.00" ที่แบบฟอร์มพิมพ์ไว้ทุกใบ ส่วนแถวที่มีเงิน
        // จริง 2,137.38 อ่านคำอธิบายไม่ออก ("0") ⇒ กฎ "ค่าขนส่ง" ชนะจากคำบนแถวยอด 0
        // แล้วระบบเสนอ "40(8) ค่าขนส่ง · หัก 1%" ทั้งที่กระดาษไม่ได้บอกว่าจ่ายค่าขนส่ง
        // — หมวดเดาผิดแค่ให้ผู้ใช้เลือกใหม่ แต่ประเภทเงินได้ผิดไหลไป 50 ทวิ + ภ.ง.ด.3/53
        // ⚠️ **อัตราที่ขึ้นกับชนิดผู้รับ ห้ามเสนอตัวเลขเมื่อยังไม่รู้ชนิดผู้รับ**
        // ดอกเบี้ย 40(4)(ก) = บุคคลธรรมดา 15% · นิติบุคคลไทย 1% (ต่างกัน 15 เท่า)
        // เดิมกฎ "ดอกเบี้ยจ่าย" ฝัง `StatutoryWhtRate: 15m` ไว้ในแถวกฎ ⇒ ดอกเบี้ยเงินกู้
        // ธนาคาร (นิติบุคคล) ถูกเสนอ 15% · ขณะที่รายงานอีกเส้นสมมติเป็นนิติบุคคลเสมอ
        // ⇒ **รหัสเดียวกันได้สองคำตอบที่ขัดกันเอง** ขึ้นกับว่าเข้าทางไหน (ผลตรวจรอบ 180)
        // ตอนนี้: รู้ชนิดผู้รับ → ใช้อัตราของชนิดนั้น · ไม่รู้ → **ไม่เสนออัตรา**
        // แต่บอกผู้ใช้ว่าทำไม (ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ)
        var ambiguousByPayee =
            Accounting.Helpers.ThaiWhtRateTable.RateDependsOnPayeeKind(result.WhtIncomeTypeCode);
        var payeeIsJuristic = ambiguousByPayee
            ? Accounting.Helpers.WhtPayeeKind.Detect(
                data.VendorTaxId, Models.Enums.ContactType.Individual, data.VendorName)
            : (bool?)null;
        var effectiveWhtRate = ambiguousByPayee
            ? (payeeIsJuristic is bool j
                ? Accounting.Helpers.ThaiWhtRateTable.RateFor(result.WhtIncomeTypeCode, j)
                : null)
            : result.StatutoryWhtRate;

        if (ambiguousByPayee && effectiveWhtRate is null && result.Confidence >= 0.6m
            && result.MoneyBackedEvidence)
        {
            var it = Accounting.Helpers.ThaiWhtRateTable.Find(result.WhtIncomeTypeCode);
            data.WhtIncomeTypeCode ??= result.WhtIncomeTypeCode;
            data.ReasoningTrace.Add(
                $"[Category] หมวด '{result.Category}' อัตราหัก ณ ที่จ่าย**ขึ้นกับชนิดผู้รับ** "
                + (it != null ? $"({it.TaxSection} {it.Name}: บุคคลธรรมดา {it.IndividualRate}% · นิติบุคคล {it.JuristicRate}%) " : "")
                + "— ยังระบุชนิดผู้รับไม่ได้จากกระดาษ จึงไม่เติมอัตราให้ กรุณาเลือกคู่ค้าก่อน");
        }

        if (effectiveWhtRate is > 0m && result.Confidence >= 0.6m
            && result.MoneyBackedEvidence)
        {
            data.SuggestedWhtRate = effectiveWhtRate;
            // ── ที่มาต้อง "เห็นได้" ไม่ใช่เดาจากช่องที่มีค่า (D-3 รอบ 184) ──
            // ข้อความ [WHT-SUGGEST] เดิมเลือกประโยคด้วย `it != null` (มีรหัส ม.40 ไหม)
            // ⇒ เดาที่มาจากช่องที่บังเอิญมีค่า. ตอนนี้ประกาศตรง ๆ ว่าใครเสนอ
            data.SuggestedWhtSource = Accounting.Helpers.WhtEvidenceSource.Statute;
            // ข้อเสนอ ≠ สิ่งที่กระดาษพิมพ์ ⇒ ต้องต่ำกว่า 0.85 เพื่อให้ช่องอัตราขึ้น
            // ไฮไลต์เหลือง "ตรวจสอบอีกครั้ง" (กฎเหล็ก #3 ข้อ 3) — ตัวเลขเดียวกับ
            // ฝั่งประวัติผู้ขาย เพื่อไม่ให้ "ข้อเสนอสองชนิด" ดูต่างกันโดยไม่มีเหตุ
            data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.WhtRate] =
                Accounting.Helpers.OcrWhtSuggestionGate.SuggestionFieldConfidence;
            data.Note(Accounting.Helpers.OcrFieldKeys.SuggestedWhtRate, effectiveWhtRate,
                Accounting.Helpers.OcrFieldSource.Statute, result.Confidence,
                $"อัตราตามกฎหมายของหมวด '{result.Category}'"
                + (string.IsNullOrEmpty(result.WhtIncomeTypeCode) ? "" : $" (ม.40 {result.WhtIncomeTypeCode})"));
            data.WhtIncomeTypeCode ??= result.WhtIncomeTypeCode;
        }

        if (!data.HasWht && !data.WhtRate.HasValue && effectiveWhtRate.HasValue
            && effectiveWhtRate.Value > 0 && result.Confidence >= 0.6m
            && result.MoneyBackedEvidence && docMentionsWht)
        {
            data.HasWht = true;
            data.WhtRate = effectiveWhtRate;
            data.FieldConfidence["WhtRate"] = 0.6;  // inferred, not extracted
            data.ReasoningTrace.Add(
                $"[Category] อนุมาน WHT {effectiveWhtRate}% จากหมวด '{result.Category}' (ป.รัษฎากร ม.50, เอกสารกล่าวถึง WHT)");
        }
        else if (!data.HasWht && effectiveWhtRate.HasValue
                 && effectiveWhtRate.Value > 0 && rawText != null && !docMentionsWht
                 && result.MoneyBackedEvidence)
        {
            // กระดาษไม่พิมพ์ WHT — ไม่ตั้งค่าให้เอง แต่ **ต้องบอกผู้ใช้ว่ากฎหมายให้หัก**
            // (เดิมเขียนว่า "ใช้ไม่ได้" ซึ่งไม่จริง: หน้าที่หักเป็นของผู้จ่าย ไม่ใช่ของ
            // กระดาษ — ผู้ใช้ที่เชื่อบรรทัดนี้จะลืมหักแล้วรับผิด ม.54)
            var incomeType = Accounting.Helpers.ThaiWhtRateTable.Find(result.WhtIncomeTypeCode);
            data.ReasoningTrace.Add(
                $"[Category] กระดาษไม่ได้พิมพ์ยอดหัก ณ ที่จ่าย — แต่หมวด '{result.Category}' "
                + $"กฎหมายกำหนดให้ผู้จ่ายหัก {effectiveWhtRate}%"
                + (incomeType != null ? $" (ประเภทเงินได้ {incomeType.TaxSection} {incomeType.Name})" : "")
                + " · ตรวจสอบก่อนยืนยัน (ท.ป.4/2528 · ไม่หัก = ผู้จ่ายรับผิด ม.54)");
        }
        foreach (var r in result.Reasons)
            data.ReasoningTrace.Add("[Category] " + r);
    }

    /// <summary>True when the raw OCR text contains any phrase that
    /// indicates the document itself has a withholding-tax line item.
    /// Conservative match — bare "ภาษี" alone (which appears on every
    /// VAT invoice) does NOT count; needs the specific WHT phrasing.</summary>
    private static bool ContainsWhtKeyword(string rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return false;
        // Thai phrases — case-insensitive isn't needed for Thai, but the
        // English needles below are.
        if (rawText.Contains("หัก ณ ที่จ่าย")
            || rawText.Contains("หักภาษี ณ ที่จ่าย")
            || rawText.Contains("ภาษีหัก ณ ที่จ่าย")
            || rawText.Contains("ภ.ง.ด.3")
            || rawText.Contains("ภ.ง.ด.53")
            || rawText.Contains("ภงด3")
            || rawText.Contains("ภงด53")
            || rawText.Contains("50 ทวิ")
            || rawText.Contains("หนังสือรับรองการหักภาษี"))
            return true;
        var lower = rawText.ToLowerInvariant();
        // `\bwht\b` แทน "wht " (ช่องว่างตามหลัง) — ของเดิมพลาด "WHT" ท้ายบรรทัด
        // (ตามด้วย \n) และ "WHT3%" ⇒ ไม่อนุมานอัตราหัก ณ ที่จ่าย ⇒ ผู้ใช้ลืมหัก
        // = บริษัทรับผิดภาษีที่ไม่ได้หัก + เบี้ยปรับ ภ.ง.ด.3/53. boundary กัน
        // "what"/"weight" ให้แล้ว
        return lower.Contains("withholding tax")
            || lower.Contains("w/h tax")
            || System.Text.RegularExpressions.Regex.IsMatch(lower, @"\bwht\b");
    }
}
