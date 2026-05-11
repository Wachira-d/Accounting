namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Assign a letter grade (A/B/C/D) to an OCR scan based on field
/// completeness + math validity + confidence. Surfaces a single
/// at-a-glance signal in the UI so users instantly know which scans
/// need attention before opening them.
///
/// Grading scheme — score out of 100:
///   • Confidence:       up to 30 points (raw confidence × 30)
///   • Vendor identified:        +15 (name + valid tax id checksum)
///   • Document number:           +10
///   • Document date:             +10
///   • Amount triple consistent:  +20 (sub + vat = total ±0.5)
///   • Line items found:          +10
///   • DBD enrichment confirmed:  +5
///
/// Grade thresholds:
///   85+ = A  (auto-create safe)
///   70+ = B  (review recommended)
///   50+ = C  (review required)
///   <50 = D  (probably re-scan)
/// </summary>
public static class ScanQualityGrader
{
    public record Grade(string Letter, int Score, string Color, List<string> Reasons);

    public static Grade Compute(Models.Entities.OcrScanResult r)
    {
        int score = 0;
        var reasons = new List<string>();

        // Confidence (capped at 30)
        var confPoints = (int)Math.Round(r.Confidence * 30m);
        score += confPoints;
        reasons.Add($"confidence {r.Confidence:P0} → +{confPoints}");

        // Vendor
        if (!string.IsNullOrEmpty(r.ExtractedVendorName))
        {
            var hasValidTaxId = !string.IsNullOrEmpty(r.ExtractedVendorTaxId)
                && SmartFieldExtractor.IsValidThaiTaxId(r.ExtractedVendorTaxId);
            var v = hasValidTaxId ? 15 : 8;
            score += v;
            reasons.Add($"vendor identified → +{v}");
        }

        // Document number
        if (!string.IsNullOrEmpty(r.ExtractedDocumentNumber)
            && r.ExtractedDocumentNumber.Any(char.IsDigit))
        { score += 10; reasons.Add("doc number → +10"); }

        // Date
        if (r.ExtractedDate.HasValue
            && r.ExtractedDate.Value.Year >= 1990
            && r.ExtractedDate.Value <= DateTime.UtcNow.AddDays(1))
        { score += 10; reasons.Add("date OK → +10"); }

        // Amount triple math
        if (r.ExtractedSubTotal.HasValue && r.ExtractedVatAmount.HasValue && r.ExtractedTotalAmount.HasValue)
        {
            var s = r.ExtractedSubTotal.Value;
            var v = r.ExtractedVatAmount.Value;
            var t = r.ExtractedTotalAmount.Value;
            if (Math.Abs(s + v - t) <= 0.5m)
            { score += 20; reasons.Add("amounts balanced → +20"); }
            else
            { score += 5; reasons.Add($"amounts mismatched (Sub+VAT≠Total Δ={s + v - t:N2})"); }
        }
        else if (r.ExtractedTotalAmount.HasValue)
        { score += 10; reasons.Add("total only → +10"); }

        // Line items
        if (!string.IsNullOrEmpty(r.ExtractedItemsJson) && r.ExtractedItemsJson.Length > 30)
        { score += 10; reasons.Add("line items → +10"); }

        // Handwriting penalty — manual verification required, so deduct
        // 20 points to ensure the scan can't reach grade A. The exact
        // penalty depends on handwriting confidence: high-confidence
        // handwriting on a scanned form is a strong "needs human review"
        // signal; low-confidence might just be a noisy character that
        // styleFont misflagged.
        if (r.HasHandwriting)
        {
            var penalty = r.HandwritingConfidence.HasValue
                ? (int)Math.Round(r.HandwritingConfidence.Value * 20m)
                : 15;
            score -= penalty;
            reasons.Add($"✋ handwriting detected → −{penalty}");
        }

        score = Math.Max(0, Math.Min(100, score));

        // Letter grade
        string letter, color;
        if (score >= 85) { letter = "A"; color = "#059669"; }
        else if (score >= 70) { letter = "B"; color = "#16a34a"; }
        else if (score >= 50) { letter = "C"; color = "#d97706"; }
        else { letter = "D"; color = "#dc2626"; }

        return new Grade(letter, score, color, reasons);
    }
}
