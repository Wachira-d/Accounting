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
    /// <param name="pricedLines">บรรทัดที่มีเงินพร้อมยอด — เมื่อส่งมา หมวดที่ชนะด้วยคำบน<b>บรรทัดส่วนน้อยของมูลค่า</b>
    /// (<see cref="Accounting.Helpers.OcrLineValueShare"/>) จะ<b>ไม่ได้ตัดสินหมวดของทั้งใบ</b> เว้นแต่มีหลักฐานจากชื่อผู้ขาย/หัวเรื่อง ·
    /// null = พฤติกรรมเดิม (ไม่รู้ยอด ⇒ ไม่ตัดสินแทน)</param>
    /// <param name="minorityWhtParts">รับส่วนน้อยที่หมวดมีอัตราหักตามกฎหมายและยอดถึงเกณฑ์ 1,000 บาท — ผู้เรียกติดแท็ก
    /// <see cref="Accounting.Helpers.OcrMinorityWhtPart.Tag"/> + กดความมั่นใจช่องอัตราหัก</param>
    /// <param name="diagnostics">รับเหตุผลของหมวดที่ถูกตัดเพราะเป็นบรรทัดส่วนน้อย (พร้อมเกณฑ์ 1,000 บาทของยอดส่วนนั้น) —
    /// ได้เสมอแม้ไม่มีหมวดไหนชนะ (ผู้เรียกเขียนลง trace · ห้ามเงียบ)</param>
    public static CategoryResult? Resolve(
        string? vendorName,
        string? headerDescription,
        IEnumerable<string?>? lineDescriptions,
        string? rawText,
        IndustryType? industry = null,
        BusinessType? businessType = null,
        IEnumerable<string?>? zeroAmountLineDescriptions = null,
        IReadOnlyList<Accounting.Helpers.OcrPricedLine>? pricedLines = null,
        List<string>? diagnostics = null,
        List<Accounting.Helpers.OcrMinorityWhtPart>? minorityWhtParts = null)
    {
        var reasons = new List<string>();
        // สามชั้น: ข้อมูลที่ **ผูกกับเงิน** (ผู้ขาย/หัวเรื่อง/บรรทัดที่มียอด)
        // น้ำหนักเต็ม · บรรทัดยอด 0 + rawText ทั้งใบเป็นหลักฐานอ่อน (มีข้อความแฝง
        // เยอะ เช่น เงื่อนไขท้ายบิลที่มีคำว่า "ที่ปรึกษา"/"ภาษีอากร") นับครึ่งเดียว
        // ชั้นผูกกับเงินแยกเป็น "หัวใบ" (ผู้ขาย/หัวเรื่อง) กับ "บรรทัด" เพื่อรู้ว่าหมวดชนะด้วยบรรทัดล้วนไหม
        var anchorCorpus = BuildCorpus(vendorName, headerDescription, null, null).ToLowerInvariant();
        var lineCorpus = BuildCorpus(null, null, lineDescriptions, null).ToLowerInvariant();
        var primaryCorpus = (anchorCorpus + " " + lineCorpus).ToLowerInvariant();
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
            // ── บรรทัดส่วนน้อยของมูลค่าห้ามตัดสินหมวดทั้งใบ (สแกนจริง f1690d11: ค่าขนส่ง 37.38 จาก 2,991.59) ──
            var share = pricedLines == null
                ? (Accounting.Helpers.OcrLineValueShare.Share?)null
                : Accounting.Helpers.OcrLineValueShare.Measure(pricedLines, d => RuleKeywordIn(rule, d));
            var lineMinority = share is { IsMinority: true };
            bool anchored = false;
            // วลีที่ "มีคำของกฎแต่ไม่ใช่เรื่องของกฎ" (ประกันสังคม · สินค้ารับประกัน · เงินประกันสัญญา) ถูกลบออกจากคลังของกฎนั้นก่อนนับ
            var ruleAnchor = StripExclusions(rule, anchorCorpus);
            var ruleLines = StripExclusions(rule, lineCorpus);
            var ruleAll = StripExclusions(rule, corpus);
            foreach (var kw in rule.Keywords)
            {
                var k = kw.ToLowerInvariant();
                if (ruleAnchor.Contains(k)) { kwScore += kw.Length >= 6 ? 2m : 1m; moneyBacked = true; anchored = true; }
                else if (ruleLines.Contains(k))
                {
                    // คำบนบรรทัดส่วนน้อย = ไม่นับเลย (รวมถึงเสียงสะท้อนของบรรทัดเดียวกันใน rawText)
                    if (!lineMinority) { kwScore += kw.Length >= 6 ? 2m : 1m; moneyBacked = true; }
                }
                else if (ruleAll.Contains(k)) kwScore += kw.Length >= 6 ? 1m : 0.5m;
            }
            // คำที่บอก "ซื้อของ" ของหมวดนั้น (อะไหล่) — ให้คะแนนหมวด/ผังได้ แต่**ไม่ใช่หลักฐานของค่าบริการ** ⇒ ไม่ทำให้ผูกกับเงิน
            // (ใบซื้ออะไหล่จากร้านอะไหล่เคยได้ข้อเสนอหัก 3% 40(8) ของกฎซ่อม — การซื้อสินค้าไม่อยู่ในข่าย ท.ป.4/2528)
            foreach (var kw in rule.GoodsKeywords ?? Array.Empty<string>())
            {
                var k = kw.ToLowerInvariant();
                if (ruleAnchor.Contains(k) || (ruleLines.Contains(k) && !lineMinority)) kwScore += kw.Length >= 6 ? 2m : 1m;
                else if (ruleAll.Contains(k)) kwScore += kw.Length >= 6 ? 1m : 0.5m;
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
            bool brandFuzzyHit = false;
            if (!brandExactHit && !string.IsNullOrEmpty(vendorName))
            {
                foreach (var brand in rule.VendorBrands)
                {
                    var sim = CharNgramSimilarity.Similarity(vendorName, brand);
                    if (sim >= 0.65)
                    {
                        kwScore += 2;     // half of exact match's +4
                        moneyBacked = true;   // ชื่อผู้ขายคือหลักฐานที่ผูกกับเงินเสมอ
                        brandFuzzyHit = true;
                        break;
                    }
                }
            }
            if (lineMinority && !anchored && !brandExactHit && !brandFuzzyHit)
            {
                // หลักฐานผูกกับเงินของหมวดนี้มีแค่บรรทัดส่วนน้อย ⇒ ไม่ให้ตัดสินหมวด/ประเภทเงินได้ของทั้งใบ · ต้องบอกเหตุผล
                var minorityReason = MinorityLineReason(rule, share!.Value);
                diagnostics?.Add(minorityReason);
                // หมวดที่ถูกตัดมีอัตราหักตามกฎหมาย + ยอดส่วนนั้นถึงเกณฑ์ ⇒ ผู้เรียกต้องทำให้มองเห็น (ห้ามหายเงียบ = หักขาด)
                if (Accounting.Helpers.OcrMinorityWhtPart.NeedsReview(rule.StatutoryWhtRate, share.Value))
                    minorityWhtParts?.Add(new Accounting.Helpers.OcrMinorityWhtPart(
                        rule.Category, rule.WhtIncomeTypeCode, rule.StatutoryWhtRate!.Value,
                        share.Value.MatchedValue, minorityReason));
                continue;
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
        // (คำยาว ≥ 6 ตัวบนบรรทัดที่มีเงิน 1 คำ = 2 คะแนน = 0.60 = ผ่านเกณฑ์ WhtSuggestionMinConfidence พอดี — โดยตั้งใจ:
        //  คำยาวที่แปลว่า "จ่ายค่า…" คือหลักฐานพอ · คำสั้นคำเดียว (ซ่อม · ดูแล) 0.50 ไม่พอ)
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

    /// <summary>คำอธิบายบรรทัดนี้มีคำของหมวดนั้นไหม — กติกาเดียวกับการให้คะแนน (contains แบบไม่สนตัวพิมพ์)</summary>
    private static bool RuleKeywordIn(CategoryRule rule, string description)
    {
        var d = StripExclusions(rule, description.ToLowerInvariant());
        foreach (var kw in rule.Keywords.Concat(rule.GoodsKeywords ?? Array.Empty<string>()))
            if (d.Contains(kw.ToLowerInvariant())) return true;
        return false;
    }

    /// <summary>ลบวลียกเว้นของกฎออกจากข้อความ (ตัวพิมพ์เล็กแล้ว) — ตัวเดียวของทั้งการให้คะแนนและการวัดบรรทัดส่วนน้อย</summary>
    private static string StripExclusions(CategoryRule rule, string lowered)
    {
        if (rule.Exclusions is not { Length: > 0 } ex || string.IsNullOrEmpty(lowered)) return lowered;
        foreach (var pattern in ex)
            lowered = Regex.Replace(lowered, pattern, " ", RegexOptions.CultureInvariant);
        return lowered;
    }

    /// <summary>เหตุผลของหมวดที่ถูกตัดเพราะชนะด้วยบรรทัดส่วนน้อย — บอกเกณฑ์ 1,000 บาทของ<b>ยอดส่วนนั้น</b>
    /// (ท.ป.4/2528 ข้อ 12 · ตัวตัดสินเกณฑ์ตัวเดียว <see cref="Accounting.Helpers.ThaiWhtRateTable.ShouldWithhold"/>)</summary>
    private static string MinorityLineReason(CategoryRule rule, Accounting.Helpers.OcrLineValueShare.Share share)
    {
        var lines = string.Join(", ", share.MatchedDescriptions.Take(3).Select(d => $"'{d}'"));
        var text = $"บรรทัด {lines} ยอด {share.MatchedValue:N2} = {share.Ratio:P1} ของมูลค่ารายการ "
            + $"{share.TotalValue:N2} — เป็นส่วนน้อยของใบ (< {Accounting.Helpers.OcrLineValueShare.MinDominantShare:P0}) "
            + $"จึงไม่ใช้ตัดสินหมวด '{rule.Category}' ของทั้งใบ";
        if (rule.StatutoryWhtRate is not > 0m) return text;
        var it = Accounting.Helpers.ThaiWhtRateTable.Find(rule.WhtIncomeTypeCode);
        var label = it != null ? $"{it.TaxSection} {it.Name}" : rule.Category;
        return Accounting.Helpers.OcrMinorityWhtPart.NeedsReview(rule.StatutoryWhtRate, share)
            ? text + $" · ส่วนนี้ ({label}) ถึงเกณฑ์ 1,000 บาท — ตรวจว่าต้องหัก ณ ที่จ่าย {rule.StatutoryWhtRate}% "
                + $"เฉพาะยอด {share.MatchedValue:N2} (ไม่ใช่ทั้งใบ)"
            : text + $" · ยอดส่วนนี้ ({label}) ต่ำกว่าเกณฑ์ 1,000 บาท (ท.ป.4/2528 ข้อ 12) — ไม่ต้องหัก ณ ที่จ่าย";
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
    /// <see cref="Accounting.Helpers.ThaiWhtRateTable"/> — **ตัวเดียวที่กำหนดอัตราของกฎ**: หนังสือรับรอง 50 ทวิ และ
    /// ภ.ง.ด.3/53 ต้องระบุประเภทเงินได้ ไม่ใช่แค่อัตรา (ผลตรวจ 2026-09-06 · T4-06) · null = กฎนี้ไม่เสนอหัก ณ ที่จ่าย</param>
    /// <param name="GoodsKeywords">คำที่บอกว่า "ซื้อของ" ของหมวดนี้ (อะไหล่) — ให้คะแนนหมวด/ผังได้ แต่<b>ไม่ใช่หลักฐานที่ผูกกับเงินของค่าบริการ</b>
    /// ⇒ ชนะด้วยคำพวกนี้ล้วนไม่เสนอประเภทเงินได้/อัตราหัก (การซื้อสินค้าไม่อยู่ในข่าย ท.ป.4/2528)</param>
    /// <param name="Exclusions">regex (ตัวพิมพ์เล็ก) ของวลีที่มีคำของกฎแต่<b>ไม่ใช่เรื่องของกฎ</b> — ลบออกจากข้อความก่อนนับคะแนนของกฎนี้เท่านั้น</param>
    private record CategoryRule(
        string Category,
        string AccountCode,
        string AccountName,
        string[] Keywords,
        string[] VendorBrands,
        string? WhtIncomeTypeCode = null,
        string[]? GoodsKeywords = null,
        string[]? Exclusions = null)
    {
        /// <summary>อัตราตามกฎหมายของประเภทเงินได้ — <b>ถามตารางเจ้าของ</b> (<see cref="Accounting.Helpers.ThaiWhtRateTable"/>) ห้ามฝังตัวเลขในแถวกฎ
        /// (ตัวตั้งตัวเดียว · ฝ่ายค้าน f5b10674 2026-10-16) · อัตราที่ขึ้นกับชนิดผู้รับ (ดอกเบี้ย 4a: บุคคล 15 · นิติบุคคล 1) คืนอัตราบุคคล
        /// เหมือนค่าเดิมของแถว — <see cref="ApplyTo"/> ไม่ใช้ค่านี้ตรง ๆ เมื่ออัตราขึ้นกับชนิดผู้รับ (<c>RateDependsOnPayeeKind</c>)</summary>
        public decimal? StatutoryWhtRate
            => Accounting.Helpers.ThaiWhtRateTable.Find(WhtIncomeTypeCode) is { } t ? t.IndividualRate ?? t.JuristicRate : null;
    }

    // ─── Rule library ───────────────────────────────────────────────────
    // Order doesn't matter — we score every rule and pick the best.
    // Keywords use lower-case Thai/English; brand matches look at vendor name.
    // อัตราหัก ณ ที่จ่ายมาจากรหัสประเภทเงินได้ (WhtIncomeTypeCode) ผ่าน ThaiWhtRateTable เท่านั้น — null = กฎนี้ไม่เสนอ
    //
    // ⚠️ คำยาว ≥ 6 ตัวอักษร = 2 คะแนน (มั่นใจ 0.60 = ผ่านเกณฑ์เสนออัตราหัก) · คำสั้น = 1 คะแนน (0.50 ไม่ผ่าน)
    // ⇒ ทุกกฎที่มีอัตราหัก ต้องมี "คำยาวที่แปลว่าจ่ายค่าอะไร" (ค่าซ่อม · ค่าโฆษณา · ค่าขนส่ง …) — เดิมค่าตั้งต้น "ค่าบริการ" (+2) ของชนิดกระดาษ
    //    กลบช่องนี้ไว้ (ใบเสร็จทุกใบได้ 3% 40(8)) พอถอดค่าตั้งต้น (f5b10674) ใบค่าซ่อม/ค่าทนาย/ค่ารับเหมา 13 แบบเสียข้อเสนอที่ถูก และ
    //    ค่าโฆษณา/ค่าขนส่งที่เคย "ถูกด้วยความบังเอิญผิดอัตรา" (3%) ไม่ได้อะไรเลย (ฝ่ายค้าน 2026-10-16 · OcrExpenseCategoryRegressionTests)
    // ⚠️ ห้ามเติมคำเดี่ยวที่อยู่ในชื่อร้าน/ชื่อสินค้า ("ก่อสร้าง" ⇒ ร้านวัสดุก่อสร้าง · "ตัดหญ้า" ⇒ เครื่องตัดหญ้า · "ขนย้าย" ⇒ รถเข็นขนย้าย ·
    //    "รักษาความปลอดภัย" ⇒ กล้องระบบรักษาความปลอดภัย · "ทำบัญชี" ⇒ โปรแกรมทำบัญชี · "ซ่อมแซม" ⇒ วัสดุซ่อมแซม · "ประกัน" ⇒ สินค้ารับประกัน)
    //    — ใช้วลี "ค่า…/บริการ…/จ้าง…" แทน (เทสต์ทิศตรงข้ามใน OcrExpenseCategoryRegressionTests)
    private static readonly CategoryRule[] Rules = new[]
    {
        // ─── น้ำมันเชื้อเพลิง — 5402 ───
        new CategoryRule("ค่าน้ำมันเชื้อเพลิง", "5402", "ค่าน้ำมันเชื้อเพลิง",
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
            Keywords: new[] { "ค่าไฟ", "ไฟฟ้า", "การไฟฟ้า", "electricity", "electric bill", "energy" },
            VendorBrands: new[] { "การไฟฟ้านครหลวง", "การไฟฟ้าส่วนภูมิภาค", "mea", "pea" }),

        // ─── ค่าน้ำประปา — 5302 ───
        new CategoryRule("ค่าน้ำประปา", "5302", "ค่าน้ำประปา",
            Keywords: new[] { "ค่าน้ำ", "น้ำประปา", "การประปา", "water bill", "water supply" },
            VendorBrands: new[] { "การประปานครหลวง", "การประปาส่วนภูมิภาค", "mwa", "pwa" }),

        // ─── ค่าโทรศัพท์/อินเทอร์เน็ต — 5304 (40(8) ค่าบริการ) ───
        new CategoryRule("ค่าโทรศัพท์ / อินเทอร์เน็ต", "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต",
            Keywords: new[] {
                "โทรศัพท์", "phone", "internet", "อินเทอร์เน็ต", "บรอดแบนด์",
                "fiber", "ไฟเบอร์", "wifi", "ค่ามือถือ", "ค่าสัญญาณ"
            },
            VendorBrands: new[] {
                "ais", "เอไอเอส", "true", "ทรู", "dtac", "ดีแทค", "tot", "ทีโอที",
                "cat", "3bb", "nt", "เอ็นที"
            },
            WhtIncomeTypeCode: "8"),

        // ─── ค่าเช่า — 5301 (40(5)) ───
        new CategoryRule("ค่าเช่า", "5301", "ค่าเช่า",
            Keywords: new[] {
                "ค่าเช่า", "rent", "rental", "lease", "เช่า", "เช่าสำนักงาน", "เช่าอาคาร",
                "เช่ารถ", "เช่าเครื่อง", "ค่าที่จอด"
            },
            VendorBrands: Array.Empty<string>(),
            WhtIncomeTypeCode: "5"),

        // ─── ค่าโฆษณา — 5101 (40(8) ค่าโฆษณา) ───
        new CategoryRule("ค่าโฆษณาและส่งเสริมการขาย", "5101", "ค่าโฆษณาและส่งเสริมการขาย",
            Keywords: new[] {
                "โฆษณา", "ค่าโฆษณา", "advertise", "advertising", "ads", "google ads", "facebook ads",
                "tiktok ads", "promotion", "ส่งเสริมการขาย", "ป้าย", "บิลบอร์ด",
                "เผยแพร่", "marketing"
            },
            VendorBrands: new[] { "google", "facebook", "meta", "tiktok", "line ads" },
            WhtIncomeTypeCode: "8ad"),

        // ─── ค่าขนส่ง / จัดส่ง — 5102 (40(8) ค่าขนส่งที่ไม่ใช่ขนส่งสาธารณะ) ───
        // ค่าขนย้าย = จ้างขนส่งของ ⇒ ประเภทเดียวกัน (ตาราง "8tr") · งานที่รวมแรงงานแพ็ก/ติดตั้งเป็นหลัก ผู้ใช้เปลี่ยนเป็น 40(8) ค่าบริการได้ (ช่องขึ้นเหลือง)
        new CategoryRule("ค่าขนส่ง / ค่าจัดส่ง", "5102", "ค่าขนส่ง",
            Keywords: new[] {
                "ขนส่ง", "ค่าขนส่ง", "transport", "shipping", "freight", "ส่งของ", "จัดส่ง",
                "ค่าส่ง", "delivery", "logistic", "ขนถ่าย", "ค่าขนย้าย", "บริการขนย้าย", "จ้างขนย้าย"
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

        // ─── ค่าบริการ / ค่าแรง — 5300 generic (40(8) ค่าจ้างทำของ/ค่าบริการ) ───
        // คำยาวเพิ่ม 2026-10-16: งานบริการที่ไม่มีหมวดเฉพาะ (รปภ. · ออกแบบ · งานพิมพ์ · ดูแลสวน) — เดิมได้ 3% เพราะค่าตั้งต้น "ค่าบริการ"
        new CategoryRule("ค่าบริการ", "5300", "ค่าใช้จ่ายบริหาร",
            Keywords: new[] {
                "ค่าบริการ", "service fee", "consulting service", "ค่าจ้าง", "รับจ้าง",
                "ค่าแรง", "labor",
                "ค่ารักษาความปลอดภัย", "บริการรักษาความปลอดภัย", "พนักงานรักษาความปลอดภัย", "security guard", "รปภ",
                "ค่าออกแบบ", "บริการออกแบบ", "ค่าพิมพ์", "ค่าตัดหญ้า", "บริการตัดหญ้า", "จ้างตัดหญ้า", "ค่าดูแลสวน", "ค่าจัดสวน"
            },
            VendorBrands: Array.Empty<string>(),
            WhtIncomeTypeCode: "8"),

        // ─── ค่ารับเหมา — 5300 (40(7) ค่ารับเหมาที่ผู้รับเหมาจัดหาสัมภาระ) ───
        // 2026-10-16 (ฝ่ายค้าน f5b10674): ไม่มีกฎนี้มาก่อน — ใบรับเหมา/มัดจำค่าก่อสร้างได้ 3% จากค่าตั้งต้น "ค่าบริการ" เท่านั้น
        // ผัง 5300 = ผังเดียวกับที่ใบพวกนี้เคยได้ (งานที่เป็นสินทรัพย์ §65 ตรี(5) ผู้ใช้เลือกผังสินทรัพย์เอง) ·
        // ⚠️ ไม่มีคำเดี่ยว "ก่อสร้าง" — ชื่อร้าน "วัสดุก่อสร้าง" = ร้านขายของ (เทสต์ทิศตรงข้ามใน OcrExpenseCategoryRegressionTests)
        new CategoryRule("ค่ารับเหมา", "5300", "ค่าใช้จ่ายบริหาร",
            Keywords: new[] {
                "รับเหมา", "ค่ารับเหมา", "ผู้รับเหมา", "จ้างเหมา", "ค่าก่อสร้าง", "contractor"
            },
            VendorBrands: Array.Empty<string>(),
            WhtIncomeTypeCode: "7"),

        // ─── ค่าที่ปรึกษากฎหมาย / ตรวจสอบบัญชี — 5502 (40(6) วิชาชีพอิสระ: กฎหมาย · การบัญชี) ───
        new CategoryRule("ค่าที่ปรึกษากฎหมาย / บัญชี", "5502", "ค่าที่ปรึกษา",
            Keywords: new[] {
                "ที่ปรึกษา", "consultant", "consultancy", "audit", "ตรวจสอบบัญชี",
                "นักบัญชี", "นิติกร", "ทนาย", "lawyer", "auditor", "ภาษีอากร", "tax service",
                "ทนายความ", "ค่าวิชาชีพ", "ค่าทำบัญชี", "รับทำบัญชี", "บริการทำบัญชี", "สอบบัญชี", "bookkeeping"
            },
            VendorBrands: Array.Empty<string>(),
            WhtIncomeTypeCode: "6"),

        // ─── ค่าธรรมเนียมธนาคาร — 5503 (no WHT, banks deduct themselves) ───
        new CategoryRule("ค่าธรรมเนียมธนาคาร", "5503", "ค่าธรรมเนียมธนาคาร",
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
            Keywords: new[] {
                "วัสดุสำนักงาน", "office supplies", "กระดาษ", "ปากกา", "หมึก", "ink",
                "toner", "stationery", "เครื่องเขียน", "แฟ้ม", "เทป", "กาว"
            },
            VendorBrands: new[] {
                "office mate", "ออฟฟิศเมท", "b2s", "บีทูเอส", "se-ed", "ซีเอ็ด"
            }),

        // ─── ค่าซ่อมแซมและบำรุงรักษา — 5306 (40(8) ค่าบริการซ่อม) ───
        // "อะไหล่"/"spare part" ย้ายไป GoodsKeywords (2026-10-16): ใบซื้ออะไหล่จากร้านอะไหล่ยังได้หมวด/ผังซ่อมเหมือนเดิม แต่ไม่ได้ข้อเสนอหัก 3%
        new CategoryRule("ค่าซ่อมแซมและบำรุงรักษา", "5306", "ค่าซ่อมแซมและบำรุงรักษา",
            Keywords: new[] {
                "ซ่อม", "repair", "maintenance", "บำรุง", "บำรุงรักษา",
                "ดูแล", "เปลี่ยน", "ติดตั้ง", "install",
                "ค่าซ่อม", "บริการซ่อม", "ค่าบำรุงรักษา", "ค่าดูแลระบบ", "บริการดูแลระบบ"
            },
            VendorBrands: Array.Empty<string>(),
            WhtIncomeTypeCode: "8",
            GoodsKeywords: new[] { "อะไหล่", "spare part" }),

        // ─── ค่าเดินทาง — 5401 (no WHT for personal travel reimbursement) ───
        new CategoryRule("ค่าเดินทาง", "5401", "ค่าเดินทาง",
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
            Keywords: new[] { "ค่ารับรอง", "entertain", "entertainment", "เลี้ยงรับรอง" },
            VendorBrands: Array.Empty<string>()),

        // ─── ค่าทำความสะอาด — 5307 (40(8)) ───
        new CategoryRule("ค่าทำความสะอาด", "5307", "ค่าทำความสะอาด",
            Keywords: new[] {
                "ทำความสะอาด", "cleaning", "แม่บ้าน", "housekeeping", "กำจัดปลวก",
                "pest control"
            },
            VendorBrands: Array.Empty<string>(),
            WhtIncomeTypeCode: "8"),

        // ─── ค่าประกัน — 5800 ───
        // ⚠️ (2026-10-16 · เคส "ค่าประกัน" บนใบที่ไม่ใช่ประกัน) เดิมมีคำเดี่ยว "ประกัน" + "premium" ⇒ ข้อความท้ายใบ "สินค้ารับประกัน 1 ปี" ·
        // "สมาชิกประกันสังคม" · "Premium Quality" (หลักฐานอ่อนชั้น rawText) ทำให้กฎนี้ชนะเมื่อบรรทัดไม่ตรงกฎไหน (มั่นใจ 0.50) แล้วเติม
        // ผังเดบิต 5800 ให้สแกนที่ยังไม่มีผัง (เส้น Azure) · ใช้เฉพาะคำที่หมายถึง "ซื้อความคุ้มครอง" จริง
        // ⚠️ (ฝ่ายค้าน f5b10674) การบีบนั้นทิ้งใบประกันจริงที่ไม่พิมพ์ เบี้ย/ประกันภัย/กรมธรรม์ ("ค่าประกันรถยนต์ ชั้น 1" · "ต่อประกันรถ" ·
        // "ประกันอุบัติเหตุกลุ่มพนักงาน" · "ประกันอัคคีภัย") ⇒ เติมวลี "ค่าประกัน/ประกัน<สิ่งที่คุ้มครอง>" + ตัดวลีที่ไม่ใช่การซื้อความคุ้มครอง
        // ด้วย Exclusions (ประกันสังคม · รับประกัน<สินค้า/งาน> · เงินประกันสัญญา/ซอง) — "ผู้รับประกันภัย" ยังเป็นใบประกัน
        // เทสต์สองทิศ OcrExpenseCategoryRemovalTests + OcrExpenseCategoryRegressionTests
        new CategoryRule("ค่าประกัน", "5800", "ค่าใช้จ่ายในการประกัน",
            Keywords: new[] {
                "insurance", "เบี้ยประกัน", "พรบ", "ประกันภัย",
                "ประกันชีวิต", "ประกันสุขภาพ", "กรมธรรม์",
                "ค่าประกัน", "ประกันรถ", "ประกันอุบัติเหตุ", "ประกันอัคคีภัย", "ประกันทรัพย์สิน", "ประกันกลุ่ม"
            },
            VendorBrands: new[] {
                "เมืองไทยประกัน", "muang thai", "aia", "เอไอเอ", "thai life", "ไทยประกันชีวิต",
                "viriyah", "วิริยะ", "axa", "fwd"
            },
            Exclusions: new[] {
                "ประกันสังคม",
                "รับประกัน(?!ภัย|ชีวิต|วินาศภัย)",
                "เงินประกัน(?!ภัย|ชีวิต|สุขภาพ)",
            }),

        // ─── ดอกเบี้ยจ่าย — 5701 (40(4)(ก) — อัตราขึ้นกับชนิดผู้รับ: ApplyTo ตัดสิน) ───
        new CategoryRule("ดอกเบี้ยจ่าย", "5701", "ดอกเบี้ยจ่าย",
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
    /// <summary>ความมั่นใจขั้นต่ำของหมวดที่จะ<b>เสนอประเภทเงินได้ ม.40 / อัตราหัก ณ ที่จ่าย</b> (และเขียนเหตุผล "กฎหมายกำหนดให้หัก") —
    /// ตัวเลขเดียวของทุกสาขาใน <see cref="ApplyTo"/> · 0.60 = คำยาว ≥ 6 ตัวหนึ่งคำบนบรรทัดที่มีเงิน</summary>
    internal const decimal WhtSuggestionMinConfidence = 0.6m;

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
        // เดิมกฎ "ดอกเบี้ยจ่าย" ฝังอัตรา 15% ไว้ในแถวกฎ ⇒ ดอกเบี้ยเงินกู้
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

        if (ambiguousByPayee && effectiveWhtRate is null && result.Confidence >= WhtSuggestionMinConfidence
            && result.MoneyBackedEvidence)
        {
            var it = Accounting.Helpers.ThaiWhtRateTable.Find(result.WhtIncomeTypeCode);
            data.WhtIncomeTypeCode ??= result.WhtIncomeTypeCode;
            data.ReasoningTrace.Add(
                $"[Category] หมวด '{result.Category}' อัตราหัก ณ ที่จ่าย**ขึ้นกับชนิดผู้รับ** "
                + (it != null ? $"({it.TaxSection} {it.Name}: บุคคลธรรมดา {it.IndividualRate}% · นิติบุคคล {it.JuristicRate}%) " : "")
                + "— ยังระบุชนิดผู้รับไม่ได้จากกระดาษ จึงไม่เติมอัตราให้ กรุณาเลือกคู่ค้าก่อน");
        }

        if (effectiveWhtRate is > 0m && result.Confidence >= WhtSuggestionMinConfidence
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
            && effectiveWhtRate.Value > 0 && result.Confidence >= WhtSuggestionMinConfidence
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
                 && result.MoneyBackedEvidence && result.Confidence < WhtSuggestionMinConfidence)
        {
            // ข้อความที่อ้าง "สาเหตุ" (กฎหมายกำหนดให้หัก) เขียนได้เฉพาะเมื่อสาเหตุนั้นถูกตัดสินแล้ว (CLAUDE.md F2 ข้อ 7 · ฝ่ายค้าน f5b10674) —
            // หมวดชนะด้วยหลักฐานอ่อน (คำสั้นคำเดียว) ⇒ ไม่เสนออัตรา และบอกตรง ๆ ว่ายังไม่รู้ ไม่ใช่ "กฎหมายกำหนดให้หัก"
            data.ReasoningTrace.Add(
                $"[Category] หมวด '{result.Category}' อาจเข้าข่ายหัก ณ ที่จ่าย แต่หลักฐานยังไม่พอ "
                + $"(มั่นใจ {result.Confidence:0.00} < {WhtSuggestionMinConfidence:0.00}) — ไม่เสนอประเภทเงินได้/อัตรา · ตรวจว่าเป็นค่าบริการหรือไม่");
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
