using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Pick the (SubTotal, VatAmount, Total) triple from a Thai-language
/// invoice using math invariants instead of relying solely on keyword
/// anchors. Real Thai invoices use a wide variety of amount labels
/// ("จํานวนเงินทั้งสิ้น", "รวมเงิน", "ยอดสุทธิ", "(Total)", ...) and
/// Tesseract frequently OCRs them inconsistently — anchor-based regex
/// alone misses or mis-picks. The arithmetic relationships
///   SubTotal + VAT = Total
///   VAT ≈ 7% × SubTotal   (Thai standard)
///   Total ≥ SubTotal ≥ VAT > 0
/// hold reliably regardless of how the labels are spelled.
///
/// Algorithm:
///   1. Extract every "x.yz" or "x,xxx.yz" candidate from the text.
///   2. For each candidate combination (s, v, t) check the math.
///   3. Score each math-passing combination by:
///      • how close vat/sub is to 0.07 (Thai VAT rate)
///      • proximity of each value to its expected label keywords
///      • freshness in the document (later occurrences usually win
///        because totals appear at the bottom of invoices)
///   4. Return the highest-scoring triple, or the Total alone when no
///      triple exists (รอบ 195: ไม่แต่ง VAT 7/107 ที่นี่อีก — ดูข้อ 4 ในโค้ด).
/// </summary>
internal static class AmountTripleExtractor
{
    private const decimal ThaiVat = 0.07m;
    private const decimal MathTolerance = 0.5m;     // ±0.50 baht — handles rounding
    private const decimal VatRateTolerance = 0.005m; // ±0.5pp around 7%

    private record AmountCandidate(decimal Value, int Position);

    /// <summary>Returns (subTotal, vat, total) when at least the Total can be
    /// determined. Any field may be null when there isn't enough signal.</summary>
    public static (decimal? Sub, decimal? Vat, decimal? Total) Extract(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText)) return (null, null, null);

        // 1. Pull every decimal-like number with at least 2 fractional digits.
        // Filter out tiny values that are obviously quantities/percentages,
        // and extreme outliers that are probably barcodes/SKUs.
        var candidates = Regex.Matches(normalizedText, @"(?<![\d.])(\d{1,3}(?:[,.]\d{3})*\.\d{2})(?![\d])")
            .Cast<Match>()
            .Select(m => new AmountCandidate(ParseAmount(m.Groups[1].Value), m.Index))
            .Where(c => c.Value >= 1m && c.Value <= 100_000_000m)
            .ToList();
        if (candidates.Count == 0) return (null, null, null);

        // Distinct values only (same amount appearing multiple times still
        // counts once for triple search; we keep the earliest position).
        var distinct = candidates
            .GroupBy(c => c.Value)
            .Select(g => g.OrderBy(c => c.Position).First())
            .ToList();

        // 2. Anchor positions for each role
        int totalAnchor = FindFirstAnchor(normalizedText, TotalAnchors);
        int subAnchor = FindFirstAnchor(normalizedText, SubAnchors);
        int vatAnchor = FindFirstAnchor(normalizedText, VatAnchors);

        // 3. Brute-force triple search. ≤30 candidates means 30³ = 27k —
        // negligible CPU, runs in microseconds.
        decimal bestScore = -1m;
        (decimal? Sub, decimal? Vat, decimal? Total) best = (null, null, null);
        foreach (var t in distinct)
        {
            foreach (var s in distinct)
            {
                if (s.Value >= t.Value) continue;        // SubTotal must be < Total
                foreach (var v in distinct)
                {
                    if (v.Value >= s.Value) continue;     // VAT < SubTotal
                    if (Math.Abs(s.Value + v.Value - t.Value) > MathTolerance) continue;

                    // Math passes — score this combination
                    decimal score = 100;
                    // Bonus when VAT is precisely 7%
                    var vatRate = v.Value / s.Value;
                    if (Math.Abs(vatRate - ThaiVat) <= VatRateTolerance) score += 100;
                    else if (Math.Abs(vatRate - ThaiVat) <= 0.02m) score += 30;
                    else continue;  // wildly wrong rate → not a Thai invoice triple

                    // Anchor proximity: closer to expected anchor = better
                    score += AnchorBonus(t.Position, totalAnchor);
                    score += AnchorBonus(s.Position, subAnchor);
                    score += AnchorBonus(v.Position, vatAnchor);

                    // Totals tend to appear later in the document
                    score += (decimal)t.Position / Math.Max(1, normalizedText.Length) * 20m;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = (s.Value, v.Value, t.Value);
                    }
                }
            }
        }
        if (best.Total.HasValue) return best;

        // 4. Fallback: no triple found — คืน<b>ยอดรวมอย่างเดียว</b>
        //
        // ★ รอบ 195 ฝ่ายค้าน C1: เดิมตรงนี้แต่ง Sub/VAT ด้วย 7/107 เองเมื่อข้อความมีคำ "ใบกำกับภาษี/ภาษีมูลค่าเพิ่ม/VAT" ที่ไหนก็ได้
        // (รวม "ยกเว้นภาษีมูลค่าเพิ่ม") แล้ว SmartFieldExtractor.TryExtractAmountsFromRawText รับสามค่านั้นเป็น "สามค่าที่ลงตัว"
        // ความมั่นใจ 0.95 ที่มา PaperLabel — ค่าที่ระบบคำนวณเองถูกติดป้ายว่าอ่านจากกระดาษ และ<b>ไม่ผ่านด่าน VatBackCalcGuard เลย</b>
        // (ใบผัก 1,070 ⇒ VAT แต่ง 70.00) · การถอด VAT จากยอดรวมมีที่เดียวคือ SmartFieldExtractor.ApplyAmountMath
        // → Helpers/OcrVatBackCalc → VatBackCalcGuard.Decide (เลขผู้ขาย · สินค้ายกเว้น ม.81 · คำเชิญชวน · VAT ที่พิมพ์ขัด) + แท็ก [VAT back-calc]
        var likelyTotal = distinct.OrderByDescending(c => c.Value).FirstOrDefault();
        if (likelyTotal == null) return (null, null, null);
        return (null, null, likelyTotal.Value);
    }

    private static int FindFirstAnchor(string text, string[] anchors)
    {
        foreach (var a in anchors)
        {
            var idx = text.IndexOf(a, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return idx;
        }
        return -1;
    }

    private static decimal AnchorBonus(int amountPos, int anchorPos)
    {
        if (anchorPos < 0) return 0m;
        var dist = Math.Abs(amountPos - anchorPos);
        if (dist <= 100) return 50m;       // same line
        if (dist <= 300) return 20m;       // nearby
        return 0m;
    }

    private static decimal ParseAmount(string raw)
    {
        // OCR sometimes confuses "," with "." inside thousand separators
        // ("1,009.35" vs. "1.009.35"). Normalize: keep only the LAST "." as
        // the decimal point, strip every other dot/comma.
        var lastDot = raw.LastIndexOf('.');
        if (lastDot < 0) return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
        var intPart = raw.Substring(0, lastDot).Replace(",", "").Replace(".", "");
        var fracPart = raw.Substring(lastDot + 1);
        var clean = $"{intPart}.{fracPart}";
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }

    // ─── Anchor keyword libraries — covers common Thai invoice spellings ─
    // including OCR drop variants ("เพิ่ม"→"เพื่", "ทั้งสิ้น"→"ทั้งสน").
    private static readonly string[] TotalAnchors = new[]
    {
        "จํานวนเงินทั้งสิ้น", "จำนวนเงินทั้งสิ้น", "รวมทั้งสิ้น", "ยอดรวมสุทธิ",
        "ยอดสุทธิ", "ยอดที่ต้องชำระ", "ราคารวมสุทธิ", "Grand Total", "Net Total",
        "(Total)", "Total Amount", "Amount Due", "ทั้งสิน", "ทั้งสิ้น",
        "รวมเป็นเงิน", "มูลค่ารวม",
    };

    private static readonly string[] SubAnchors = new[]
    {
        "จํานวนเงินรวม", "จำนวนเงินรวม", "รวมเงิน", "รวมราคา", "ราคาก่อนภาษี",
        "ราคารวม", "ก่อน VAT", "Sub Total", "Subtotal", "(Sub Total)",
        "หลังหักส่วนลด", "Total Before",
    };

    private static readonly string[] VatAnchors = new[]
    {
        "ภาษีมูลค่าเพิ่ม", "ภาษีมูลค่าเพื่", "ภาษีมูลค่า", "ภาษี 7%", "VAT",
        "Vat", "(VAT)", "ภาษี 7 %", "+VAT",
    };
}
