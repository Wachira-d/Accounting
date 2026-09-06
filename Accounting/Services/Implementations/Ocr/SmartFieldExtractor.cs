using System.Text.RegularExpressions;
// OcrExtractedData lives in Accounting.Services.Implementations (declared in
// OcrService.cs as an internal sibling type) — import that namespace so the
// signatures below resolve without a fully-qualified name everywhere.
using Accounting.Services.Implementations;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Apply real-world Thai-accounting constraints to fill in / validate the
/// fields returned by ANY OCR provider (Azure DI / Python microservice /
/// embedded Tesseract). Designed so all three sources end up with the same
/// data shape and the same quality bar — downstream code (gateway,
/// vendorIntel, document creation) can then treat them uniformly.
///
/// Design rules:
///   • Pure static — no DB, no DI, no IO.
///   • Inputs: a partial OcrExtractedData (whatever the provider produced)
///     + the raw text. Output: same object, mutated in place.
///   • Each rule is conservative: it only writes a field if confident, and
///     records the source/confidence so the UI can mark guesses.
///   • Real-world invariants are codified once here so every provider
///     benefits — embedded Tesseract gets the biggest boost.
///
/// Constraints applied (in this order):
///   1. Thai tax-id checksum filter
///   2. Vendor vs buyer mutual exclusion + role assignment
///   3. SubTotal + VAT = Total math
///   4. VAT = SubTotal × 7% cross-check (Thai standard)
///   5. WHT rate normalization (1/2/3/5/10/15 only)
///   6. Date validation + Buddhist-year conversion
///   7. Document-number plausibility (must contain ≥1 digit)
///   8. Line items sum ≈ SubTotal sanity
/// </summary>
internal static class SmartFieldExtractor
{
    private const decimal ThaiVatRate = 0.07m;
    private static readonly decimal[] ValidWhtRates = { 1m, 2m, 3m, 5m, 10m, 15m };

    /// <summary>Mutate <paramref name="data"/> in place by applying every
    /// constraint we can think of. Safe to call multiple times.</summary>
    public static void Enrich(OcrExtractedData data, string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) rawText = "";

        // 0a. Normalize Tesseract Thai output — collapse inter-character
        // spaces so every keyword regex below has a chance of matching.
        // Without this, "ก า ร ไฟ ฟ้า" silently misses every Thai anchor
        // and the entire extraction stack degrades to numeric-only data.
        rawText = ThaiTextNormalizer.Normalize(rawText);

        // 0b. Re-extract anchored amounts now that the text is normalized
        // — overrides any partial values the provider left behind.
        TryExtractAmountsFromRawText(rawText, data);
        TryExtractDocumentNumberFromRawText(rawText, data);
        TryExtractVendorNameFromRawText(rawText, data);

        // 0. Clear any pre-existing tax IDs that fail checksum (e.g. OCR
        // confused 0↔O, 1↔I, 8↔B). Cheaper to re-derive from raw text below
        // than to ship a wrong 13-digit number downstream.
        if (!string.IsNullOrEmpty(data.VendorTaxId) && !IsValidThaiTaxId(data.VendorTaxId))
        {
            data.ReasoningTrace.Add($"[SmartExtract] VendorTaxId {data.VendorTaxId} checksum ไม่ผ่าน → ล้างค่า");
            data.VendorTaxId = null;
        }
        if (!string.IsNullOrEmpty(data.BuyerTaxId) && !IsValidThaiTaxId(data.BuyerTaxId))
        {
            data.ReasoningTrace.Add($"[SmartExtract] BuyerTaxId {data.BuyerTaxId} checksum ไม่ผ่าน → ล้างค่า");
            data.BuyerTaxId = null;
        }

        // 1. Tax IDs — extract all 13-digit candidates with valid checksum
        //    (บาร์โค้ดสินค้าในตารางรายการถูกคัดออกแล้วใน ExtractTaxIdCandidates)
        var taxIds = ExtractTaxIdCandidates(rawText);

        // 2. Resolve vendor vs buyer with mutual exclusion
        AssignVendorBuyerRoles(data, rawText, taxIds);

        // 3. Math invariants — fill missing amounts (extended to include WHT)
        ApplyAmountMath(data, rawText);

        // 4. VAT rate cross-check (Thai standard 7%)
        ValidateOrInferVatRate(data);

        // 5. WHT rate normalization + statutory-rate inference from category
        NormalizeWhtRate(data, rawText);

        // 6. Date validation: Buddhist year, future-date rejection, plausibility
        ValidateAndNormalizeDate(data);

        // 7. Document number plausibility
        EnsureDocumentNumberPlausible(data, rawText);

        // 8. Line items sum sanity + per-line qty × unit-price check
        ValidateLineItemSum(data);
        ValidateLineItemPerRowMath(data);

        // 9. Tax-ID type classification (juristic vs personal) — feeds WHT/VAT inference
        ClassifyTaxIdTypes(data);

        // 10. Total amount math: Total = SubTotal + VAT - WHTAmount
        ApplyTotalWithWhtMath(data);

        // 11. Total cannot be less than VAT — sanity check
        ValidateAmountOrdering(data);

        // 12. เทียบยอดกับ "จำนวนเงินตัวอักษร" บนกระดาษ — ด่านที่แรงที่สุด
        CrossCheckAmountInWords(data, rawText);
    }

    /// <summary>
    /// เทียบยอดรวมกับ "จำนวนเงินรวมทั้งสิ้น (ตัวอักษร)" ที่พิมพ์บนกระดาษ
    ///
    /// <para>ใบเสร็จ/ใบกำกับไทยเกือบทุกใบพิมพ์ยอดไว้สองรูปแบบ ซึ่งหน้าตาต่างกัน
    /// สิ้นเชิง ⇒ OCR แทบไม่มีทางอ่านผิด<b>เหมือนกัน</b>ทั้งคู่ นี่จึงเป็นการ
    /// ตรวจซ้ำที่แรงที่สุดที่มีอยู่บนกระดาษ และจับความผิดพลาดชนิดที่ด่านคณิต
    /// อื่นจับไม่ได้เลย — จุดทศนิยม/ลูกน้ำหาย (6,420.00 → 642000), หลักเกิน,
    /// ตัวเลขสลับ. ระบบมีตัวแปลง "เลข → ตัวอักษร" มานานแล้ว (ขาพิมพ์เอกสาร)
    /// แต่ไม่เคยมีขากลับ ⇒ ข้อมูลที่พิมพ์อยู่บนกระดาษทุกใบถูกทิ้งเปล่า ๆ</para>
    ///
    /// <para>สองหน้าที่: (1) <b>เติม</b>ยอดเมื่ออ่านตัวเลขไม่ได้เลย
    /// (2) <b>เตือน</b>เมื่อสองค่าขัดกัน — ไม่ทับค่าตัวเลขเงียบ ๆ เพราะยังไม่รู้
    /// ว่าฝั่งไหนถูก ให้คนตัดสิน</para>
    /// </summary>
    private static void CrossCheckAmountInWords(OcrExtractedData data, string rawText)
    {
        var words = Accounting.Helpers.ThaiAmountInWords.FindInText(rawText);
        if (words is null or 0) return;

        if (data.TotalAmount is null or 0)
        {
            data.TotalAmount = words;
            data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.TotalAmount] = 0.9;
            data.ReasoningTrace.Add(
                $"[AmountWords] อ่านยอดตัวเลขไม่ได้ — ใช้จำนวนเงินตัวอักษรบนกระดาษแทน: {words:N2}");
            return;
        }

        if (Accounting.Helpers.ThaiAmountInWords.Matches(data.TotalAmount, words))
        {
            // ตรงกัน = หลักฐานสองทาง → ดันความมั่นใจขึ้น
            data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.TotalAmount] = 0.99;
            data.ReasoningTrace.Add($"[AmountWords] ยอดตัวเลขตรงกับตัวอักษรบนกระดาษ ({words:N2}) ✓");
            return;
        }

        data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.TotalAmount] = 0.35;
        data.ReasoningTrace.Add(
            $"[AmountWords] ⚠️ ยอดตัวเลข {data.TotalAmount:N2} ไม่ตรงกับจำนวนเงินตัวอักษรบนกระดาษ "
            + $"({words:N2}) — ตรวจจุดทศนิยม/ลูกน้ำก่อนอนุมัติ");
    }

    // ─── 1. Tax-ID with checksum filter ──────────────────────────────────
    // Thai 13-digit tax ID validation algorithm:
    //   sum = Σ digit[i] * (13 - i)  for i = 0..11
    //   checksum = (11 - (sum % 11)) % 10
    //   digit[12] must equal checksum
    /// <summary>ผู้สมัครเป็นเลขผู้เสียภาษี 1 ตัวที่เจอในข้อความ —
    /// <c>Labelled</c> = มีป้าย "เลขประจำตัวผู้เสียภาษี"/"Tax ID"
    /// นำหน้าในระยะสายตา ซึ่งเป็นหลักฐานที่หนักกว่า checksum มาก</summary>
    internal readonly record struct TaxIdCandidate(string Id, int Position, bool Labelled);

    /// <summary>ป้ายกำกับที่บอกว่า "เลขก้อนถัดไปคือเลขผู้เสียภาษี" — เทียบแบบตัด
    /// ช่องว่างออกทั้งสองฝั่ง เพราะ Tesseract ไทยแทรกช่องว่างระหว่างสระ/วรรณยุกต์</summary>
    private static readonly string[] TaxIdLabels =
    {
        "เลขประจำตัวผู้เสียภาษี", "เลขประจําตัวผู้เสียภาษี", "เลขผู้เสียภาษี",
        "ผู้เสียภาษีอากร", "เลขประจำตัว", "เลขประจําตัว",
        // ไม่ใส่ "tin" — สั้นเกินไป พอตัดช่องว่างแล้วไปโผล่กลางคำอื่นได้
        // ("Printing Co., Ltd." → "printingcoltd" มี "tin") ⇒ ติดธงมีป้ายผิด
        // ซึ่งจะปล่อยบาร์โค้ดผ่านด่านคัดออก
        "taxid", "taxidentificationno", "vatreg", "vatregistrationno",
    };

    /// <summary>มองย้อนหลังกี่ตัวอักษรเพื่อหาป้ายกำกับ — พอสำหรับ "เลขประจำตัว
    /// ผู้เสียภาษีอากร : " + ช่องว่างที่ OCR แทรก แต่ไม่ไกลจนคว้าป้ายของบรรทัดอื่น</summary>
    private const int TaxIdLabelLookBehind = 60;

    /// <summary>
    /// มองไปข้างหน้ากี่ตัวอักษร — **ป้ายไม่ได้อยู่ก่อนเลขเสมอ**
    ///
    /// แบบฟอร์มพิมพ์สำเร็จ (ใบเสร็จ/ใบกำกับเล่มมีสำเนา) มักมีเส้นประให้เขียน
    /// แล้วค่อยมีป้ายอยู่ใต้เส้น ⇒ เลขที่พิมพ์ลงไปอยู่ **บรรทัดก่อน** ป้าย:
    /// <code>
    ///                       0 2055 65017 74 1
    ///   ..........เลขประจำตัวผู้เสียภาษีอากร..........
    /// </code>
    /// ถ้ามองย้อนหลังอย่างเดียว เลขผู้ซื้อบนใบพวกนี้จะถูกตัดสินว่า "ไม่มีป้าย"
    /// แล้วแพ้เลขผู้ขาย (ซึ่งอยู่หลังป้ายตามปกติ) ⇒ ช่องผู้ซื้อว่าง แล้วระบบ
    /// เตือนว่า "ควรระบุเลขผู้เสียภาษีของผู้ซื้อ" ทั้งที่กระดาษมีเลขอยู่เต็ม ๆ
    /// (เคสจริง: ใบเสร็จ/ใบกำกับภาษี หจก.สหกลชลบุรี เล่ม 007 เลขที่ 0339)
    ///
    /// <para>สั้นกว่าฝั่งย้อนหลังตั้งใจ — ป้ายที่ตามหลังค่าเป็นความสัมพันธ์เชิง
    /// เลย์เอาต์ที่แน่นกว่า ถ้าเปิดกว้างเท่ากันจะเสี่ยงไปคว้าป้ายของบล็อกถัดไป</para>
    /// </summary>
    private const int TaxIdLabelLookAhead = 45;

    public static List<(string Id, int Position)> ExtractValidThaiTaxIds(string text)
        => ExtractTaxIdCandidates(text).Select(c => (c.Id, c.Position)).ToList();

    /// <summary>
    /// หาเลข 13 หลักที่ "เป็นเลขผู้เสียภาษีได้จริง" ในข้อความทั้งหน้า
    ///
    /// ═══ ทำไมต้องคัดบาร์โค้ดออก (บั๊กจริง PI-20260820-0005) ═══
    /// ใบกำกับของร้านค้าวัสดุมีบาร์โค้ด EAN-13 พิมพ์อยู่ในตารางสินค้าทุกบรรทัด
    /// ซึ่งเป็นเลข 13 หลักเหมือนเลขผู้เสียภาษี และเลขสุ่มมีโอกาส ~1/10 ที่จะผ่าน
    /// mod-11 ไทยด้วย ⇒ หน้าเดียวมีบาร์โค้ด 10 ตัว = แทบการันตีว่าจะมีตัวหนึ่ง
    /// ถูกหยิบไปเป็น "เลขผู้ซื้อ" แล้วระบบเตือนว่า "อาจอัพโหลดผิดบริษัท"
    /// ทั้งที่กระดาษถูกต้องทุกอย่าง (เกิดจริงกับ 8885009199627)
    ///
    /// กติกา: ตัวที่มี<b>ป้ายกำกับ</b>นำหน้าเก็บไว้เสมอ (ป้ายหนักกว่า checksum) ·
    /// ตัวที่ไม่มีป้ายและ<b>หน้าตาเป็นบาร์โค้ดสินค้า</b> (EAN-13 + GS1 prefix) ทิ้ง
    /// </summary>
    internal static List<TaxIdCandidate> ExtractTaxIdCandidates(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<TaxIdCandidate>();
        // pattern มาจากตัวกลางตัวเดียวของระบบ — ห้ามคัดลอกมาวางที่นี่
        // (เหตุผลเรื่องตัวคั่นห้ามครอบ \n อยู่ใน doc ของ ThaiTaxId.Pattern)
        return Regex.Matches(text, Accounting.Helpers.ThaiTaxId.Pattern)
            .Cast<Match>()
            .Select(m => new TaxIdCandidate(
                Regex.Replace(m.Groups[1].Value, @"[-\s]", ""),
                m.Index,
                HasTaxIdLabelNear(text, m.Index, m.Length)))
            .Where(c => c.Id.Length == 13 && IsValidThaiTaxId(c.Id))
            // ด่านบาร์โค้ด **ไม่มีข้อยกเว้น** — ป้ายกำกับช่วยไม่ได้ตรงนี้
            //
            // เดิมเขียน `c.Labelled || !LooksLikeProductBarcode(...)` คือให้ตัวที่มี
            // ป้ายผ่านไปได้ แต่ "ป้ายอยู่ใกล้" เป็นสัญญาณอ่อน: บาร์โค้ดบรรทัดแรกของ
            // ตารางสินค้าอยู่ห่างจากบล็อกเลขผู้ซื้อไม่กี่สิบตัวอักษร ⇒ ติดธงมีป้าย
            // โดยบังเอิญแล้วรอดด่านไปเป็น "เลขผู้เสียภาษี" (จับได้ตอน simulate
            // การแก้ป้ายสองทิศ) — เลขที่ผ่าน EAN-13 **และ** มี GS1 prefix ของสินค้า
            // คือบาร์โค้ด ไม่ว่าข้อความรอบ ๆ จะเขียนว่าอะไร
            .Where(c => !Accounting.Helpers.ThaiTaxId.LooksLikeProductBarcode(c.Id))
            .GroupBy(c => c.Id)
            // ตัวที่มีป้ายชนะตัวที่ไม่มีป้ายเสมอ (เลขเดียวกันอาจโผล่หลายที่)
            .Select(g => g.OrderByDescending(c => c.Labelled).ThenBy(c => c.Position).First())
            .ToList();
    }

    /// <summary>มีป้าย "เลขประจำตัวผู้เสียภาษี" อยู่ใกล้ ๆ เลขก้อนนี้ไหม —
    /// ดู<b>ทั้งสองทิศ</b> (ดูเหตุผลที่ <see cref="TaxIdLabelLookAhead"/>)</summary>
    private static bool HasTaxIdLabelNear(string text, int index, int length)
        => WindowHasLabel(text, Math.Max(0, index - TaxIdLabelLookBehind), index)
        || WindowHasLabel(text, index + length,
               Math.Min(text.Length, index + length + TaxIdLabelLookAhead));

    private static bool WindowHasLabel(string text, int start, int end)
    {
        if (end <= start) return false;
        var window = text.Substring(start, end - start);
        // ตัดช่องว่าง/ตัวคั่นออกก่อนเทียบ — OCR ไทยแทรกช่องว่างกลางคำเป็นปกติ
        // และแบบฟอร์มมีเส้นประ ".........." คั่นระหว่างป้ายกับค่าเสมอ
        var squashed = new string(window
            .Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '_' or '.' or ':' or '·'))
            .ToArray()).ToLowerInvariant();
        return TaxIdLabels.Any(l => squashed.Contains(
            new string(l.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant(),
            StringComparison.Ordinal));
    }

    public static bool IsValidThaiTaxId(string? id) => Accounting.Helpers.ThaiTaxId.IsValid(id);

    // ─── 2. Vendor vs buyer role assignment with mutual exclusion ───────
    private static void AssignVendorBuyerRoles(OcrExtractedData data, string text, List<TaxIdCandidate> taxIds)
    {
        // If both already populated AND distinct, leave them alone.
        var hasVendor = !string.IsNullOrEmpty(data.VendorTaxId) || !string.IsNullOrEmpty(data.VendorName);
        var hasBuyer = !string.IsNullOrEmpty(data.BuyerTaxId) || !string.IsNullOrEmpty(data.BuyerName);
        var bothDistinct = hasVendor && hasBuyer
            && data.VendorTaxId != data.BuyerTaxId
            && !NamesEqual(data.VendorName, data.BuyerName);
        if (bothDistinct) return;

        // Find seller/buyer keyword positions in raw text
        var sellerKeywords = new[] { "ผู้ขาย", "ผู้ออกใบ", "ผู้ให้บริการ", "ผู้ออก", "SELLER", "FROM" };
        var buyerKeywords = new[] { "ผู้ซื้อ", "ลูกค้า", "นามผู้ซื้อ", "BUYER", "CUSTOMER", "BILL TO", "SOLD TO", "ส่งถึง" };
        int sellerPos = FindFirstKeyword(text, sellerKeywords);
        int buyerPos = FindFirstKeyword(text, buyerKeywords);

        // ตำแหน่งจริงของคำว่า "ผู้ซื้อ/ลูกค้า" — **ห้ามปลอมเป็นท้ายหน้า**
        //
        // เดิมเขียนว่า `if (buyerPos < 0) buyerPos = text.Length;` ซึ่งแปลว่า
        // "ถ้าไม่เจอคำว่าผู้ซื้อ ให้ถือว่าผู้ซื้ออยู่ท้ายหน้า" ⇒ เลข 13 หลัก
        // ตัวสุดท้ายของหน้า (= บาร์โค้ดบรรทัดล่างสุดของตารางสินค้า) กลายเป็น
        // เลขผู้ซื้อทุกครั้ง แล้วกฎที่ 7 ก็ฟันธงว่า "อาจอัพโหลดผิดบริษัท"
        // ทั้งที่กระดาษถูกต้อง (บั๊กจริง PI-20260820-0005)
        //
        // ไม่มีหลักฐานว่าเลขไหนเป็นของผู้ซื้อ = **ไม่เดา** ปล่อยว่างไว้ให้
        // RdComplianceValidator กฎที่ 3 ไปค้นเลขบริษัทเราในข้อความทั้งหน้าเอง
        // ซึ่งเป็นวิธีที่ถูกต้องกว่าและไม่สร้างข้อมูลผิดขึ้นมาใหม่
        var buyerAnchor = buyerPos;      // -1 = ไม่มีคำว่าผู้ซื้อบนกระดาษ
        if (sellerPos < 0) sellerPos = 0;
        if (buyerPos < 0) buyerPos = text.Length;   // ใช้กับการเดา "ชื่อ" เท่านั้น

        // ผู้สมัครที่มีป้าย "เลขประจำตัวผู้เสียภาษี" นำหน้า ชนะตัวที่ไม่มีป้ายเสมอ —
        // ถ้าหน้านี้มีตัวที่มีป้ายอยู่แล้ว ตัวไม่มีป้ายไม่ต้องเอามาพิจารณาเลย
        var labelled = taxIds.Where(t => t.Labelled).ToList();
        var pool = labelled.Count > 0 ? labelled : taxIds;

        // Assign tax IDs: closest to seller-keyword → vendor; closest to
        // buyer-keyword → buyer. Mutual exclusion enforced — once assigned to
        // one role, the same ID cannot also be assigned to the other.
        string? newVendorTaxId = data.VendorTaxId;
        string? newBuyerTaxId = data.BuyerTaxId;
        if (pool.Count >= 2 && string.IsNullOrEmpty(newVendorTaxId) && string.IsNullOrEmpty(newBuyerTaxId))
        {
            var byDistToSeller = pool.OrderBy(t => Math.Abs(t.Position - sellerPos)).First();
            newVendorTaxId = byDistToSeller.Id;
            var remaining = pool.Where(t => t.Id != newVendorTaxId).ToList();
            if (remaining.Count > 0 && buyerAnchor >= 0)
                newBuyerTaxId = remaining.OrderBy(t => Math.Abs(t.Position - buyerAnchor)).First().Id;
        }
        else if (pool.Count == 1)
        {
            // Single tax id — bias toward vendor unless an explicit buyer
            // keyword sits closer to the tax id than any seller keyword.
            var only = pool[0];
            var distSeller = Math.Abs(only.Position - sellerPos);
            var distBuyer = buyerAnchor >= 0 ? Math.Abs(only.Position - buyerAnchor) : int.MaxValue;
            if (string.IsNullOrEmpty(newVendorTaxId) && distSeller <= distBuyer)
                newVendorTaxId = only.Id;
            // ยอมให้เป็นเลขผู้ซื้อได้เฉพาะเมื่อมี**หลักฐาน**: มีคำว่า "ผู้ซื้อ/ลูกค้า"
            // บนกระดาษ หรือเลขนั้นมีป้าย "เลขประจำตัวผู้เสียภาษี" นำหน้า —
            // ไม่มีทั้งสองอย่าง = เลขลอย ๆ กลางหน้า (บาร์โค้ด/เลขอ้างอิง) ห้ามเดา
            else if (string.IsNullOrEmpty(newBuyerTaxId) && (buyerAnchor >= 0 || only.Labelled))
                newBuyerTaxId = only.Id;
        }

        // Defensive: if both tax IDs ended up identical, drop the buyer one
        // (Vendor ≠ Buyer is a hard invariant — two different parties).
        if (!string.IsNullOrEmpty(newVendorTaxId) && newVendorTaxId == newBuyerTaxId)
            newBuyerTaxId = null;
        data.VendorTaxId = newVendorTaxId;
        data.BuyerTaxId = newBuyerTaxId;

        // Name resolution: extract company names, attach to nearest keyword
        var names = ExtractCompanyNames(text);
        if (names.Count >= 1)
        {
            if (string.IsNullOrEmpty(data.VendorName))
                data.VendorName = names.OrderBy(n => Math.Abs(n.Position - sellerPos)).First().FullName;
            if (string.IsNullOrEmpty(data.BuyerName) && names.Count >= 2)
            {
                data.BuyerName = names
                    .Where(n => !NamesEqual(n.FullName, data.VendorName))
                    .OrderBy(n => Math.Abs(n.Position - buyerPos))
                    .Select(n => n.FullName)
                    .FirstOrDefault();
            }
        }

        // Final invariant: Vendor name and Buyer name must differ.
        if (!string.IsNullOrEmpty(data.VendorName) && NamesEqual(data.VendorName, data.BuyerName))
            data.BuyerName = null;

        // Confidence reporting — high when a keyword anchored the choice
        if (!string.IsNullOrEmpty(data.VendorName))
            data.FieldConfidence["SellerName"] = sellerPos > 0 ? 0.85 : 0.6;
        if (!string.IsNullOrEmpty(data.VendorTaxId))
            data.FieldConfidence["SellerTaxId"] = 0.95;  // checksum-validated
        if (!string.IsNullOrEmpty(data.BuyerTaxId))
            data.FieldConfidence["BuyerTaxId"] = 0.95;
    }

    private static List<(string FullName, int Position)> ExtractCompanyNames(string text)
    {
        var pattern = @"(บริษัท|ห้างหุ้นส่วน(?:จำกัด|สามัญ)?|หจก\.?|ร้าน)\s*(.+?)(?:\s*จำกัด(?:\s*\(มหาชน\))?|\s*\(|(?=\s*เลข|\s*สาขา|\s*ที่อยู่|\s*\d{1}[- \t]?\d{4})|$)";
        var matches = Regex.Matches(text, pattern, RegexOptions.Multiline);
        var results = new List<(string, int)>();
        foreach (Match m in matches)
        {
            var prefix = m.Groups[1].Value;
            var name = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2) continue;
            var afterMatch = text.Substring(m.Index, Math.Min(m.Length + 30, text.Length - m.Index));
            var suffix = "";
            if (afterMatch.Contains("จำกัด"))
                suffix = afterMatch.Contains("มหาชน") ? " จำกัด (มหาชน)" : " จำกัด";
            results.Add(($"{prefix} {name}{suffix}".Trim(), m.Index));
        }
        return results;
    }

    private static int FindFirstKeyword(string text, string[] keywords)
    {
        foreach (var kw in keywords)
        {
            var idx = text.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return idx;
        }
        return -1;
    }

    private static bool NamesEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var na = Normalize(a);
        var nb = Normalize(b);
        return na == nb;
    }

    private static string Normalize(string s)
        => new string((s ?? "").ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c != '.' && c != ',').ToArray()).Trim();

    // ─── 3. Math invariants: SubTotal + VAT = Total ─────────────────────
    private static void ApplyAmountMath(OcrExtractedData data, string text)
    {
        var sub = data.SubTotal;
        var vat = data.VatAmount;
        var total = data.TotalAmount;

        // Derive the third value from the other two
        if (sub.HasValue && vat.HasValue && !total.HasValue)
        {
            data.TotalAmount = sub.Value + vat.Value;
            data.FieldConfidence["TotalAmount"] = Math.Max(data.FieldConfidence.GetValueOrDefault("TotalAmount", 0), 0.9);
        }
        else if (total.HasValue && vat.HasValue && !sub.HasValue && total.Value > vat.Value)
        {
            data.SubTotal = total.Value - vat.Value;
            data.FieldConfidence["SubTotal"] = 0.9;
        }
        else if (total.HasValue && sub.HasValue && !vat.HasValue && total.Value > sub.Value)
        {
            data.VatAmount = total.Value - sub.Value;
            data.FieldConfidence["VatAmount"] = 0.9;
        }
        else if (total.HasValue && !sub.HasValue && !vat.HasValue && LooksLikeVatDoc(text))
        {
            // Tax invoice with only the total visible — derive SubTotal/VAT
            // assuming standard 7% Thai VAT (Total = SubTotal × 1.07).
            data.SubTotal = Math.Round(total.Value / (1m + ThaiVatRate), 2);
            data.VatAmount = total.Value - data.SubTotal.Value;
            data.FieldConfidence["SubTotal"] = 0.7;     // derived, not extracted
            data.FieldConfidence["VatAmount"] = 0.7;
        }

        // Validate: VAT cannot exceed SubTotal in any realistic Thai doc
        // (VAT rate is 7%, max conceivable would be ~7% of SubTotal). If
        // someone parsed VAT > SubTotal, the labels probably got swapped.
        if (data.SubTotal.HasValue && data.VatAmount.HasValue
            && data.VatAmount.Value > data.SubTotal.Value * 0.5m)
        {
            // Don't auto-swap — just lower confidence so the gateway flags it
            data.FieldConfidence["VatAmount"] = 0.3;
            data.ReasoningTrace.Add("[SmartExtract] VAT > 50% of SubTotal — ค่าผิดปกติ กรุณาตรวจสอบ");
        }
    }

    private static bool LooksLikeVatDoc(string text)
        => text.Contains("ใบกำกับภาษี") || text.Contains("ใบกํากับภาษี")
        || text.ToUpperInvariant().Contains("TAX INVOICE")
        || text.Contains("ภาษีมูลค่าเพิ่ม") || text.Contains("VAT");

    // ─── 4. VAT 7% cross-check ──────────────────────────────────────────
    private static void ValidateOrInferVatRate(OcrExtractedData data)
    {
        if (!data.SubTotal.HasValue || data.SubTotal.Value <= 0) return;
        if (!data.VatAmount.HasValue || data.VatAmount.Value <= 0) return;

        var rate = data.VatAmount.Value / data.SubTotal.Value;
        // Within ±0.5pp of 7% = consistent
        if (Math.Abs(rate - ThaiVatRate) <= 0.005m)
            data.FieldConfidence["VatAmount"] = Math.Max(data.FieldConfidence.GetValueOrDefault("VatAmount", 0), 0.95);
        // Not 7% and not 0% → suspicious
        else if (rate > 0.01m && rate < 0.06m)
            data.ReasoningTrace.Add($"[SmartExtract] VAT {rate:P1} ไม่ใช่ 7% มาตรฐาน — โปรดตรวจสอบ");
    }

    // ─── 5. WHT rate normalization ──────────────────────────────────────
    // Thai WHT statutory rates: 1, 2, 3, 5, 10, 15. Anything else is OCR error
    // (e.g. parsed "7" as WHT when 7 is actually the VAT). Snap to the nearest
    // valid rate when within tolerance, otherwise clear.
    private static void NormalizeWhtRate(OcrExtractedData data, string text)
    {
        if (!data.HasWht && !data.WhtRate.HasValue) return;
        if (!data.WhtRate.HasValue) return;

        var r = data.WhtRate.Value;
        if (ValidWhtRates.Contains(r))
        {
            data.FieldConfidence["WhtRate"] = 0.95;
            return;
        }

        // 7% is VAT, not WHT — almost certainly a misread
        if (r == 7m)
        {
            data.WhtRate = null;
            data.HasWht = false;
            data.ReasoningTrace.Add("[SmartExtract] WHT 7% ตีความผิด (7% = VAT ไม่ใช่ WHT) → ล้างค่า");
            return;
        }

        // Snap to nearest valid within 0.5pp
        var nearest = ValidWhtRates.OrderBy(v => Math.Abs(v - r)).First();
        if (Math.Abs(nearest - r) <= 0.5m)
        {
            data.WhtRate = nearest;
            data.FieldConfidence["WhtRate"] = 0.7;
            data.ReasoningTrace.Add($"[SmartExtract] WHT {r}% → snap เป็น {nearest}% (ค่ามาตรฐาน)");
        }
        else
        {
            data.WhtRate = null;
            data.HasWht = false;
            data.ReasoningTrace.Add($"[SmartExtract] WHT {r}% ไม่ใช่อัตราตามกฎหมาย → ล้างค่า");
        }
    }

    // ─── 6. Date validation + Buddhist year conversion ──────────────────
    private static void ValidateAndNormalizeDate(OcrExtractedData data)
    {
        if (!data.DocumentDate.HasValue) return;
        var d = data.DocumentDate.Value;
        // Buddhist year (พ.ศ.) ranges 2400–2700 commonly. Convert if so.
        if (d.Year >= 2400 && d.Year <= 2700)
        {
            try
            {
                // BUGFIX (2025-05): create with Kind=Utc so PostgreSQL +
                // ASP.NET JSON serialisers don't apply local-timezone
                // offset that previously made "29 พค 2569" round-trip
                // as "28 พค 2569" (UTC+7 → previous calendar day).
                // The DocumentDate is conceptually a calendar date, not
                // a moment in time, so pinning Kind=Utc with the same
                // y/m/d preserves the human-meaningful day.
                data.DocumentDate = new DateTime(d.Year - 543, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc);
            }
            catch { data.DocumentDate = null; }
        }
        else if (d.Kind != DateTimeKind.Utc)
        {
            // Non-BE year but maybe Unspecified/Local — re-pin to Utc
            // for the same reason (round-trip stability across timezone).
            data.DocumentDate = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc);
        }
        // Reject impossibly old / future dates
        var year = data.DocumentDate?.Year ?? 0;
        if (year < 1990 || year > DateTime.UtcNow.Year + 1)
        {
            data.ReasoningTrace.Add($"[SmartExtract] วันที่ {data.DocumentDate:yyyy-MM-dd} ไม่สมเหตุสมผล → ล้างค่า");
            data.DocumentDate = null;
            data.FieldConfidence["DocumentDate"] = 0;
        }
        else if (data.DocumentDate.HasValue)
        {
            data.FieldConfidence["DocumentDate"] = Math.Max(data.FieldConfidence.GetValueOrDefault("DocumentDate", 0), 0.9);
        }
    }

    // ─── 7. Document number plausibility ────────────────────────────────
    private static void EnsureDocumentNumberPlausible(OcrExtractedData data, string text)
    {
        if (!string.IsNullOrEmpty(data.DocumentNumber))
        {
            // Reject pure-word, no-digit "numbers" (likely a label like "Invoice")
            if (!data.DocumentNumber.Any(char.IsDigit))
            {
                data.ReasoningTrace.Add($"[SmartExtract] DocumentNumber '{data.DocumentNumber}' ไม่มีตัวเลข → ล้างค่า");
                data.DocumentNumber = null;
            }
            else
                data.FieldConfidence["DocumentNumber"] = Math.Max(data.FieldConfidence.GetValueOrDefault("DocumentNumber", 0), 0.85);
        }

        if (string.IsNullOrEmpty(data.DocumentNumber))
        {
            // Try to recover from raw text using anchored patterns. The "เลขที่"
            // anchor produces the strongest signal; the bare INV/REC/PO prefix
            // patterns are last-resort because they sometimes match product codes.
            string[] patterns =
            {
                @"เลขที่\s*(?:เอกสาร)?\s*[:：]?\s*([A-Za-z0-9][\w\-/]{2,})",
                @"(?:Invoice|Receipt|Document)\s*(?:No\.?|Number)?\s*[:：]?\s*([A-Za-z0-9][\w\-/]{2,})",
                @"\b((?:INV|REC|TAX|TX|IV|PO|CN|DN)[\-/]?\d[\d\-/A-Za-z]*)\b",
            };
            foreach (var p in patterns)
            {
                var m = Regex.Match(text, p, RegexOptions.IgnoreCase);
                if (m.Success && m.Groups[1].Value.Any(char.IsDigit))
                {
                    data.DocumentNumber = m.Groups[1].Value.Trim();
                    data.FieldConfidence["DocumentNumber"] = 0.7;
                    break;
                }
            }
        }
    }

    // ─── 8. Line items sum sanity ───────────────────────────────────────
    private static void ValidateLineItemSum(OcrExtractedData data)
    {
        if (data.Items == null || data.Items.Count == 0) return;
        if (!data.SubTotal.HasValue || data.SubTotal.Value <= 0) return;

        var lineSum = data.Items.Where(i => i.Amount.HasValue).Sum(i => i.Amount!.Value);
        if (lineSum <= 0) return;

        var diff = Math.Abs(lineSum - data.SubTotal.Value);
        var tolerance = Math.Max(1m, data.SubTotal.Value * 0.02m);  // 2% or ฿1
        if (diff <= tolerance)
        {
            data.FieldConfidence["SubTotal"] = Math.Max(data.FieldConfidence.GetValueOrDefault("SubTotal", 0), 0.97);
        }
        else
        {
            data.ReasoningTrace.Add(
                $"[SmartExtract] ยอด line items รวม {lineSum:N2} ≠ SubTotal {data.SubTotal:N2} (ต่าง {diff:N2})");
        }
    }

    // ─── 8b. Per-line: Quantity × UnitPrice ≈ Amount ────────────────────
    // OCR commonly confuses commas/decimals — this picks up the row where
    // 5 × 100 = 500 was read as "5 × 100 = 50.0" or similar.
    private static void ValidateLineItemPerRowMath(OcrExtractedData data)
    {
        if (data.Items == null) return;
        foreach (var line in data.Items)
        {
            if (!line.Quantity.HasValue || !line.UnitPrice.HasValue || !line.Amount.HasValue) continue;
            if (line.Quantity.Value <= 0 || line.UnitPrice.Value <= 0 || line.Amount.Value <= 0) continue;
            var expected = line.Quantity.Value * line.UnitPrice.Value;
            var diff = Math.Abs(expected - line.Amount.Value);
            var tolerance = Math.Max(0.5m, expected * 0.02m);
            if (diff > tolerance)
            {
                data.ReasoningTrace.Add(
                    $"[SmartExtract] รายการ '{line.Description}': qty×price = {expected:N2} ≠ amount {line.Amount:N2}");
            }
        }
    }

    // ─── 9. Tax-ID type classification ──────────────────────────────────
    // First digit of a 13-digit Thai tax ID encodes the holder type. We use
    // this to:
    //   • Bias VAT-registration expectation (juristic codes 0/8/9 are almost
    //     always VAT-registered; individuals usually not)
    //   • Decide statutory WHT rate when ambiguous (Section 50 has different
    //     rates for individuals vs juristic recipients).
    //
    // Reference: Thai Civil Registration Act + Revenue Department rules:
    //   1   = Thai national ID issued post-1984 (บัตรประชาชนสมัยใหม่)
    //   2   = Thai national ID issued pre-1984 / supplementary
    //   3   = Permanent resident card (พร.)
    //   4–7 = Various historical legacy registration types
    //   8   = Juristic ID — partnership / private limited / public limited
    //   0/9 = Other juristic registrations (state enterprise, foundation)
    private static void ClassifyTaxIdTypes(OcrExtractedData data)
    {
        ClassifyOne(data, data.VendorTaxId, "Vendor");
        ClassifyOne(data, data.BuyerTaxId, "Buyer");
    }

    private static void ClassifyOne(OcrExtractedData data, string? id, string role)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 13) return;
        var first = id[0];
        var isJuristic = first == '0' || first == '8' || first == '9';
        var key = $"{role}TaxIdType";
        if (isJuristic)
        {
            data.FieldConfidence[key] = 0.9;
            // Juristic vendors → very likely VAT-registered → expect 7% VAT.
            // Don't auto-set HasWht here; that depends on what we BUY from them.
        }
        else
        {
            data.FieldConfidence[key] = 0.9;
            // Personal (1-7) → typically NOT VAT-registered; expect 0% VAT.
            // If extraction said VAT > 0 for a personal vendor, flag it.
            if (role == "Vendor" && data.VatAmount.HasValue && data.VatAmount.Value > 0)
            {
                data.ReasoningTrace.Add(
                    $"[SmartExtract] ผู้ขายมีรหัสบัตรบุคคลธรรมดา (ขึ้นต้น {first}) แต่มี VAT {data.VatAmount:N2} — ตรวจสอบ");
            }
        }
    }

    public static bool IsJuristicTaxId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 13) return false;
        return id[0] == '0' || id[0] == '8' || id[0] == '9';
    }

    // ─── 10. Total = SubTotal + VAT - WHT math ──────────────────────────
    // When the document shows WHT separately at the bottom (common on
    // service invoices), the "ยอดที่ต้องชำระ" / "Net Payable" already
    // reflects WHT deduction. Compute and validate when we have the rate.
    private static void ApplyTotalWithWhtMath(OcrExtractedData data)
    {
        if (!data.SubTotal.HasValue) return;
        if (!data.HasWht || !data.WhtRate.HasValue) return;

        var whtAmount = Math.Round(data.SubTotal.Value * data.WhtRate.Value / 100m, 2);
        var vat = data.VatAmount ?? 0m;
        var netPayable = data.SubTotal.Value + vat - whtAmount;

        if (data.TotalAmount.HasValue)
        {
            // If extracted Total matches gross (sub+vat) AND WHT >0, the user
            // is going to be paying NetPayable — flag for review so downstream
            // doesn't book the wrong amount to AP/cash.
            var gross = data.SubTotal.Value + vat;
            if (Math.Abs(data.TotalAmount.Value - gross) < 1m
                && Math.Abs(data.TotalAmount.Value - netPayable) > 1m)
            {
                data.ReasoningTrace.Add(
                    $"[SmartExtract] Total {data.TotalAmount:N2} = gross, แต่หลังหัก WHT จ่ายจริง ≈ {netPayable:N2}");
            }
        }
    }

    // ─── 11. Amount ordering sanity ─────────────────────────────────────
    private static void ValidateAmountOrdering(OcrExtractedData data)
    {
        if (data.TotalAmount.HasValue && data.VatAmount.HasValue
            && data.TotalAmount.Value < data.VatAmount.Value)
        {
            data.ReasoningTrace.Add(
                $"[SmartExtract] Total {data.TotalAmount:N2} < VAT {data.VatAmount:N2} — ค่าสลับกันหรือเปล่า?");
            data.FieldConfidence["TotalAmount"] = 0.3;
            data.FieldConfidence["VatAmount"] = 0.3;
        }
        if (data.TotalAmount.HasValue && data.TotalAmount.Value < 0)
        {
            data.ReasoningTrace.Add($"[SmartExtract] Total {data.TotalAmount:N2} ติดลบ — ล้างค่า");
            data.TotalAmount = null;
        }
        if (data.VatAmount.HasValue && data.VatAmount.Value < 0)
        {
            data.VatAmount = null;
        }
    }

    // ─── Phone, postal code, email extraction (for vendor/buyer enrichment) ─
    // Not used by Enrich itself yet — exposed so the caller can pull contact
    // info to attach to the matched Contact entity.

    public static string? ExtractFirstThaiPhone(string text)
    {
        // 9-10 digits, optionally with dashes/spaces; first digit must be 0
        var m = Regex.Match(text, @"\b(0\d[ \t\-]?\d{3}[ \t\-]?\d{4})\b");
        return m.Success ? Regex.Replace(m.Groups[1].Value, @"[\s\-]", "") : null;
    }

    public static string? ExtractFirstThaiPostalCode(string text)
    {
        // 5 digits in Thai range 10000-96999; bias toward those following
        // a province name or "รหัสไปรษณีย์" keyword to avoid mistaking an
        // amount for a postal code.
        var anchored = Regex.Match(text, @"(?:รหัสไปรษณีย์|Postal\s*Code)\s*[:：]?\s*(\d{5})", RegexOptions.IgnoreCase);
        if (anchored.Success) return anchored.Groups[1].Value;
        // Free-floating: only accept when the 5-digit number stands by itself
        // (no decimal, not part of a longer number).
        foreach (Match m in Regex.Matches(text, @"(?<!\d)(\d{5})(?!\d)"))
        {
            var code = int.Parse(m.Groups[1].Value);
            if (code >= 10000 && code <= 96999) return m.Groups[1].Value;
        }
        return null;
    }

    public static string? ExtractFirstEmail(string text)
    {
        var m = Regex.Match(text, @"\b([A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})\b");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ─── Anchored amount extraction (post-normalize) ──────────────────────
    // Delegates to AmountTripleExtractor which uses arithmetic invariants
    // (sub + vat = total, vat ≈ 7% × sub) instead of relying solely on
    // keyword regex. This works on Thai invoices that use any of dozens of
    // amount-label spellings ("จํานวนเงินทั้งสิ้น", "รวมเงิน", "ยอดสุทธิ",
    // "(Total)", ...) and survives Tesseract OCR errors in the labels.
    private static void TryExtractAmountsFromRawText(string text, OcrExtractedData data)
    {
        // Pre-check: are existing values consistent with the 7% rule?
        // If yes, the OCR provider got it right — keep it.
        bool existingConsistent = false;
        if (data.SubTotal.HasValue && data.VatAmount.HasValue && data.TotalAmount.HasValue
            && data.SubTotal.Value > 0)
        {
            var s = data.SubTotal.Value; var v = data.VatAmount.Value; var t = data.TotalAmount.Value;
            existingConsistent = Math.Abs(s + v - t) < 1m
                && (Math.Abs(v / s - 0.07m) < 0.01m
                    // ใบ 0%/ยกเว้น (ส่งออก · สินค้าเกษตร · ค่าเช่า): VAT = 0 และ sub = total
                    // คือค่าที่ถูกต้องตามกฎหมาย ไม่ใช่ "ไม่สอดคล้อง" — เดิมตกไปแต่ง VAT 7/107 ทับ
                    || (v == 0m && Math.Abs(s - t) < 1m));
        }
        if (existingConsistent) return;

        var (sub, vat, total) = AmountTripleExtractor.Extract(text);
        // When the extractor produced a complete arithmetic-valid triple,
        // OVERRIDE existing partial / inconsistent values — the math check
        // is more trustworthy than whatever single keyword regex picked up.
        //
        // ⚠️ ด่านกัน "สามค่าที่ลงตัวแต่เป็นคนละเรื่อง" (ผลตรวจ 2026-09-05 T2-09): engine
        // อ่านหัวใบถูก (Makro 1,000 / 49 / 1,049 — บิลผสม 7%/ยกเว้น จึงไม่ใช่ 7% พอดี)
        // แต่ตัวค้นสามค่าไปจับ (951 / 49 / 1,000) ที่ลงตัวเพราะ 1,000 คือ "เงินสดรับ"
        // และ 951 คือ "เงินทอน" ⇒ ทับค่าถูกด้วยค่าแต่ง แล้วตั้ง confidence 0.95.
        // กติกา: ถ้า engine อ่านยอดรวมมาแล้ว สามค่าใหม่ต้อง "ตกลง" กับยอดรวมนั้น
        // ถึงจะทับได้ — ไม่งั้นเติมเฉพาะช่องว่าง (partial fallback ข้างล่าง)
        var engineTotal = data.TotalAmount;
        var agreesWithEngineTotal = engineTotal is null or 0m
            || (total.HasValue && Math.Abs(total.Value - engineTotal.Value) < 1m);
        if (!agreesWithEngineTotal && total.HasValue)
            data.ReasoningTrace.Add(
                $"[AmountTriple] สามค่าที่ลงตัว (Total={total:N2}) ไม่ตรงยอดรวมที่ engine อ่าน ({engineTotal:N2}) — ไม่ทับ");
        if (agreesWithEngineTotal && sub.HasValue && vat.HasValue && total.HasValue
            && sub.Value > 0 && Math.Abs(sub.Value + vat.Value - total.Value) < 1m)
        {
            data.SubTotal = sub;
            data.VatAmount = vat;
            data.TotalAmount = total;
            data.FieldConfidence["SubTotal"] = 0.95;
            data.FieldConfidence["VatAmount"] = 0.95;
            data.FieldConfidence["TotalAmount"] = 0.95;
            data.ReasoningTrace.Add(
                $"[AmountTriple] math-consistent: SubTotal={sub:N2} + VAT={vat:N2} = Total={total:N2} (VAT {vat / sub:P1})");
            return;
        }

        // Partial fallback — fill only the nulls
        if (total.HasValue && (data.TotalAmount is null or 0m))
        {
            data.TotalAmount = total;
            data.FieldConfidence["TotalAmount"] = 0.8;
        }
        if (sub.HasValue && (data.SubTotal is null or 0m))
        {
            data.SubTotal = sub;
            data.FieldConfidence["SubTotal"] = 0.8;
        }
        if (vat.HasValue && (data.VatAmount is null or 0m))
        {
            data.VatAmount = vat;
            data.FieldConfidence["VatAmount"] = 0.8;
        }
    }

    private static void TryExtractDocumentNumberFromRawText(string text, OcrExtractedData data)
    {
        // Skip when we already have something plausible
        if (!string.IsNullOrEmpty(data.DocumentNumber) && data.DocumentNumber.Length >= 4
            && data.DocumentNumber.Any(char.IsDigit))
            return;

        // Anchor patterns, ordered by reliability. The grabbed value must
        // contain ≥3 digits — kills the "T"/"TX" false positives the bare
        // prefix regex produced.
        //
        // ⚠️ ลำดับสำคัญ (บั๊กจริง — บิล กฟภ.): เอกสารราชการ/สาธารณูปโภคพิมพ์
        // **ทั้งสองเลข** บนใบเดียว — "เลขที่ (No.)" = เลขที่ใบกำกับ/ใบเสร็จ
        // (ตัวที่ใช้เคลม ภ.พ.30) กับ "เลขที่ใบแจ้งหนี้ (Invoice No.)" = เลขอ้างอิง
        // รอบบิล. เดิม "Invoice No." อยู่บนสุด ⇒ เลขใบแจ้งหนี้ชนะเลขใบกำกับทุก
        // ครั้งบนใบพวกนี้. เลขที่หลัก (เลขที่/No. เดี่ยว ๆ) ต้องมาก่อน —
        // "Invoice No." เหลือเป็น fallback สำหรับใบแจ้งหนี้จริงที่ไม่มีเลขอื่น
        // ── แบบฟอร์มเล่มมีสำเนา: "เล่มที่ 007  เลขที่ 0339" ──
        //
        // ⚠️ เดิมไม่มี pattern รองรับรูปแบบนี้เลย ทั้งที่เป็นแบบฟอร์มที่พบบ่อย
        // ที่สุดของร้านค้า/ผู้รับเหมารายย่อย (และเป็นใบที่เพิ่งเป็นบั๊กเรื่อง
        // เลขผู้ซื้อ — หจก.สหกลชลบุรี เล่ม 007 เลขที่ 0339) ⇒ เลขที่เอกสารว่าง
        // หรือหยิบเลขอื่นมาแทน · คอลัมน์ "เล่มที่/เลขที่" ในรายงานภาษีซื้อ
        // (§87) จึงกรอกได้ครึ่งเดียวตลอด
        //
        // เก็บเป็น "เล่ม/เลข" เพื่อให้ระบุใบได้จริง — เลขที่ 4 หลักซ้ำกันข้าม
        // เล่มเป็นเรื่องปกติของแบบฟอร์มชนิดนี้
        var bookMatch = Regex.Match(text,
            @"เล่ม\s*ที่?\s*[:：]?\s*(\d{1,6})[\s\S]{0,40}?เลข\s*ที่?\s*[:：]?\s*([A-Za-z0-9][A-Za-z0-9\-/]{1,})",
            RegexOptions.IgnoreCase);
        if (bookMatch.Success)
        {
            var book = bookMatch.Groups[1].Value.Trim();
            var no = bookMatch.Groups[2].Value.Trim().TrimEnd('.', ',', ';');
            data.DocumentNumber = $"{book}/{no}";
            data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.DocumentNumber] = 0.9;
            data.ReasoningTrace.Add($"[SmartExtract] แบบฟอร์มเล่มมีสำเนา — เล่มที่ {book} เลขที่ {no}");
            return;
        }

        // ⚠️ "เลขที่" ของ**ที่อยู่** (เลขที่ 99/1 ถ.สุขุมวิท · No. 123/45 Sukhumvit Rd.)
        // ใช้คำเดียวกับเลขที่เอกสาร และมักอยู่**ก่อน**เลขที่ใบในหัวกระดาษ ⇒ เดิมชนะ
        // ทุกใบที่พิมพ์ที่อยู่แบบนี้ (ผลตรวจ 2026-09-05 T2-10). สองด่าน: (1) ค่าที่ตาม
        // ด้วยคำบอกที่อยู่ (ถ./ถนน/หมู่/ซอย/ต./อ./แขวง/เขต/Rd/Road/Soi/Moo) ถูกคัดออก
        // (2) ป้ายเฉพาะของเอกสาร (เลขที่ใบกำกับ/ใบเสร็จ/Tax Invoice No.) มาก่อนป้ายทั่วไป
        // ⚠️ ต้องปิดท้ายกลุ่มด้วย `(?![A-Za-z0-9\-/])` **ก่อน** ด่านที่อยู่ ไม่งั้น regex
        // จะ backtrack ให้กลุ่มสั้นลงจนด่านผ่าน: "เลขที่ 123/45 ถนนพระราม 4" คืน "123/4"
        // (ผลตรวจ 2026-09-06 · T5-N2 — บั๊กที่เกิดตอนแก้บั๊ก T2-10 รอบเดียวกัน)
        const string tokenEnd = @"(?![A-Za-z0-9\-/])";
        const string notAddress =
            @"(?!\s*(?:ถ\.|ถนน|หมู่|ม\.\s*\d|ซ\.|ซอย|ต\.|ตำบล|อ\.|อำเภอ|แขวง|เขต|จ\.|จังหวัด|Rd\b|Road\b|Soi\b|Moo\b|Street\b|St\.))";
        var patterns = new[]
        {
            @"(?:เลขที่ใบกำกับ(?:ภาษี)?|เลขที่ใบเสร็จ(?:รับเงิน)?|Tax\s*Invoice\s*No\.?|Receipt\s*No\.?)\s*[:：]?\s*([A-Za-z0-9][A-Za-z0-9\-/]{2,})",
            @"(?:เลขที่|เลขที|เลข\s?ที่|No\.?)\s*\(\s*No\.?\s*\)\s*([A-Za-z0-9][A-Za-z0-9\-/]{2,})" + tokenEnd + notAddress,
            @"เลขที่(?!ใบแจ้งหนี้|สัญญา|บัญชี|ผู้เสียภาษี)\s*\(?\s*(?:No\.?)?\s*\)?\s*[:：]?\s*([A-Za-z0-9][A-Za-z0-9\-/]{2,})" + tokenEnd + notAddress,
            @"(?:^|\s)No\.\s*([A-Za-z0-9][A-Za-z0-9\-/]{2,})" + tokenEnd + notAddress,
            @"(?:Invoice\s*No\.?|เลขที่ใบแจ้งหนี้)\s*[:：]?\s*([A-Za-z0-9][A-Za-z0-9\-/]{2,})",
        };
        foreach (var p in patterns)
        {
            foreach (Match m in Regex.Matches(text, p, RegexOptions.IgnoreCase))
            {
                var v = m.Groups[1].Value.Trim().TrimEnd('.', ',', ';');
                if (v.Count(char.IsDigit) < 3) continue;
                data.DocumentNumber = v;
                data.FieldConfidence["DocumentNumber"] = 0.85;
                return;
            }
        }
    }

    private static void TryExtractVendorNameFromRawText(string text, OcrExtractedData data)
    {
        if (!string.IsNullOrEmpty(data.VendorName)) return;

        // Thai government entities don't use บริษัท/ห้างหุ้นส่วน prefixes —
        // pattern-match them explicitly because the static company-name
        // regex (which requires those prefixes) would silently miss them.
        var govPatterns = new[]
        {
            @"(การไฟฟ้า(?:นครหลวง|ส่วนภูมิภาค))",
            @"(การประปา(?:นครหลวง|ส่วนภูมิภาค))",
            @"(บริษัท\s+ท่าอากาศยานไทย[^\n]{0,40})",
            @"(ไปรษณีย์ไทย)",
            @"(การรถไฟแห่งประเทศไทย)",
            @"(บริษัท\s+ปตท\.[^\n]{0,40})",
        };
        foreach (var p in govPatterns)
        {
            var m = Regex.Match(text, p);
            if (m.Success)
            {
                data.VendorName = m.Groups[1].Value.Trim();
                data.FieldConfidence["SellerName"] = 0.9;
                return;
            }
        }

        // Generic บริษัท/ห้างหุ้นส่วน — re-run with the now-normalized text
        // so the prefix anchor matches even when Tesseract had spaced it out.
        var named = Regex.Match(text,
            @"(บริษัท|ห้างหุ้นส่วนจำกัด|ห้างหุ้นส่วน|หจก\.?)\s*([฀-๿0-9A-Za-z][^\n]{2,80}?)\s*(จำกัด\s*\(?(?:มหาชน)?\)?|$)",
            RegexOptions.Multiline);
        if (named.Success)
        {
            var prefix = named.Groups[1].Value.Trim();
            var core = named.Groups[2].Value.Trim();
            var suffix = named.Groups[3].Value.Trim();
            var full = $"{prefix} {core}{(string.IsNullOrEmpty(suffix) ? "" : " " + suffix)}".Trim();
            if (full.Length > 6 && full.Length < 200)
            {
                data.VendorName = full;
                data.FieldConfidence["SellerName"] = 0.85;
            }
        }
    }
}
