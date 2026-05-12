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
        decimal Confidence,
        List<string> Reasons);

    /// <summary>Returns null when no rule matches.</summary>
    public static CategoryResult? Resolve(
        string? vendorName,
        string? headerDescription,
        IEnumerable<string?>? lineDescriptions,
        string? rawText,
        IndustryType? industry = null,
        BusinessType? businessType = null)
    {
        var reasons = new List<string>();
        var corpus = BuildCorpus(vendorName, headerDescription, lineDescriptions, rawText).ToLowerInvariant();

        // Score every rule against the corpus; highest wins.
        CategoryRule? best = null;
        decimal bestScore = 0m;
        foreach (var rule in Rules)
        {
            int kwScore = 0;
            foreach (var kw in rule.Keywords)
                if (corpus.Contains(kw.ToLowerInvariant())) kwScore += kw.Length >= 6 ? 2 : 1;
            // Vendor-brand matches outweigh single keyword hits because they're
            // far more discriminating ("ปตท." in a vendor name strongly
            // implies fuel; "ปตท." in a random product description doesn't).
            bool brandExactHit = false;
            foreach (var brand in rule.VendorBrands)
                if (!string.IsNullOrEmpty(vendorName)
                    && vendorName.ToLowerInvariant().Contains(brand.ToLowerInvariant()))
                { kwScore += 4; brandExactHit = true; }

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
                        break;
                    }
                }
            }
            if (kwScore == 0) continue;

            // Industry bias: multiply the raw keyword score by the
            // per-industry weight. Default weight is 1.0 (no change).
            var industryWeight = IndustryWeight(rule.Category, industry);
            decimal score = kwScore * industryWeight;
            if (score > bestScore) { bestScore = score; best = rule; }
        }

        if (best == null || bestScore <= 0) return null;

        // Confidence: 0.5 for ~1 keyword, ramping to 0.95 at score≥6
        var conf = Math.Min(0.95m, 0.4m + 0.1m * bestScore);
        var reasonText = industry.HasValue && industry != IndustryType.General
            ? $"จับคู่ '{best.Category}' จาก score {bestScore:F1} (industry={industry.Value})"
            : $"จับคู่ '{best.Category}' จาก score {bestScore:F1}";
        reasons.Add(reasonText);
        return new CategoryResult(best.Category, best.AccountCode, best.AccountName,
            best.StatutoryWhtRate, conf, reasons);
    }

    /// <summary>Per-industry weighting factor for a category. Returns 1.0
    /// when the industry is unknown / General. Values > 1.0 boost
    /// categories that are typical for that industry; values < 1.0
    /// dampen ones that are atypical.</summary>
    private static decimal IndustryWeight(string category, IndustryType? industry)
    {
        if (!industry.HasValue || industry.Value == IndustryType.General) return 1.0m;
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

    private record CategoryRule(
        string Category,
        string AccountCode,
        string AccountName,
        decimal? StatutoryWhtRate,
        string[] Keywords,
        string[] VendorBrands);

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
            }),

        // ─── ค่าเช่า — 5301 (5% WHT mandatory) ───
        new CategoryRule("ค่าเช่า", "5301", "ค่าเช่า",
            StatutoryWhtRate: 5m,
            Keywords: new[] {
                "ค่าเช่า", "rent", "rental", "lease", "เช่า", "เช่าสำนักงาน", "เช่าอาคาร",
                "เช่ารถ", "เช่าเครื่อง", "ค่าที่จอด"
            },
            VendorBrands: Array.Empty<string>()),

        // ─── ค่าโฆษณา — 5101 (2% WHT) ───
        new CategoryRule("ค่าโฆษณาและส่งเสริมการขาย", "5101", "ค่าโฆษณาและส่งเสริมการขาย",
            StatutoryWhtRate: 2m,
            Keywords: new[] {
                "โฆษณา", "advertise", "advertising", "ads", "google ads", "facebook ads",
                "tiktok ads", "promotion", "ส่งเสริมการขาย", "ป้าย", "บิลบอร์ด",
                "เผยแพร่", "marketing"
            },
            VendorBrands: new[] { "google", "facebook", "meta", "tiktok", "line ads" }),

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
            }),

        // ─── ค่าบริการ / ค่าแรง — 5300 generic, 3% WHT ───
        new CategoryRule("ค่าบริการ", "5300", "ค่าใช้จ่ายบริหาร",
            StatutoryWhtRate: 3m,
            Keywords: new[] {
                "ค่าบริการ", "service fee", "consulting service", "ค่าจ้าง", "รับจ้าง",
                "ค่าแรง", "labor"
            },
            VendorBrands: Array.Empty<string>()),

        // ─── ค่าที่ปรึกษากฎหมาย / ตรวจสอบบัญชี — 5502 (3% WHT) ───
        new CategoryRule("ค่าที่ปรึกษากฎหมาย / บัญชี", "5502", "ค่าที่ปรึกษา",
            StatutoryWhtRate: 3m,
            Keywords: new[] {
                "ที่ปรึกษา", "consultant", "consultancy", "audit", "ตรวจสอบบัญชี",
                "นักบัญชี", "นิติกร", "ทนาย", "lawyer", "auditor", "ภาษีอากร", "tax service"
            },
            VendorBrands: Array.Empty<string>()),

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
            VendorBrands: Array.Empty<string>()),

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
            VendorBrands: Array.Empty<string>()),

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
            VendorBrands: Array.Empty<string>()),
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
        if (!data.HasWht && !data.WhtRate.HasValue && result.StatutoryWhtRate.HasValue
            && result.StatutoryWhtRate.Value > 0 && result.Confidence >= 0.6m
            && docMentionsWht)
        {
            data.HasWht = true;
            data.WhtRate = result.StatutoryWhtRate;
            data.FieldConfidence["WhtRate"] = 0.6;  // inferred, not extracted
            data.ReasoningTrace.Add(
                $"[Category] อนุมาน WHT {result.StatutoryWhtRate}% จากหมวด '{result.Category}' (ป.รัษฎากร ม.50, เอกสารกล่าวถึง WHT)");
        }
        else if (!data.HasWht && result.StatutoryWhtRate.HasValue
                 && result.StatutoryWhtRate.Value > 0 && rawText != null && !docMentionsWht)
        {
            // Surface the deliberate skip so admin can see WHY no WHT was
            // suggested even though the category rule would have set it.
            data.ReasoningTrace.Add(
                $"[Category] ข้าม WHT inference — เอกสารไม่มียอดหัก ณ ที่จ่าย (statutory {result.StatutoryWhtRate}% สำหรับ '{result.Category}' ใช้ไม่ได้)");
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
        return lower.Contains("withholding tax")
            || lower.Contains("wht ")
            || lower.Contains("wht%")
            || lower.Contains("wht:");
    }
}
