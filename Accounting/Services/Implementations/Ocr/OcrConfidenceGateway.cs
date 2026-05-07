using Accounting.Helpers;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Validates OCR results against business rules before accepting them.
/// Catches obvious errors that high model confidence can hide
/// (e.g. bad TaxId checksum, SubTotal+VAT != Total, future date).
/// </summary>
public static class OcrConfidenceGateway
{
    public record GatewayResult(decimal AdjustedConfidence, List<string> Warnings, bool MathConsistent);

    public static GatewayResult Validate(
        decimal modelConfidence,
        DateTime? documentDate,
        decimal? subTotal,
        decimal? vatAmount,
        decimal? total,
        string? vendorTaxId,
        string? buyerTaxId,
        Dictionary<string, decimal>? fieldConfidences = null)
    {
        var warnings = new List<string>();
        var penalty = 0m;
        var mathConsistent = true;

        // 1. Date sanity: not in future, not too old
        if (documentDate.HasValue)
        {
            var now = DateTime.UtcNow;
            if (documentDate.Value > now.AddDays(2))
            {
                warnings.Add($"วันที่เอกสาร {documentDate.Value:yyyy-MM-dd} อยู่ในอนาคต");
                penalty += 0.15m;
            }
            else if (documentDate.Value < now.AddYears(-7))
            {
                warnings.Add($"วันที่เอกสาร {documentDate.Value:yyyy-MM-dd} เก่ากว่า 7 ปี");
                penalty += 0.10m;
            }
        }

        // 2. Math consistency: SubTotal + VAT ≈ Total (within 1 baht for rounding)
        if (subTotal.HasValue && vatAmount.HasValue && total.HasValue)
        {
            var expected = subTotal.Value + vatAmount.Value;
            var diff = Math.Abs(expected - total.Value);
            if (diff > 1.0m)
            {
                warnings.Add($"คณิตศาสตร์ไม่ตรง: {subTotal:N2} + {vatAmount:N2} = {expected:N2} ≠ {total:N2} (ห่าง {diff:N2})");
                penalty += 0.20m;
                mathConsistent = false;
            }
        }

        // 3. VAT rate sanity: should be ~7% in Thailand (or 0% / exempt)
        if (subTotal.HasValue && vatAmount.HasValue && subTotal.Value > 0)
        {
            var rate = vatAmount.Value / subTotal.Value * 100m;
            if (rate > 0.5m && (rate < 6.5m || rate > 7.5m))
            {
                warnings.Add($"อัตรา VAT ผิดปกติ: {rate:N1}% (ปกติ 7% หรือ 0%)");
                penalty += 0.10m;
            }
        }

        // 4. Tax ID checksums
        if (!string.IsNullOrEmpty(vendorTaxId) && !ValidateThaiTaxId(vendorTaxId))
        {
            warnings.Add($"เลขผู้เสียภาษีผู้ขาย {vendorTaxId} checksum ไม่ผ่าน");
            penalty += 0.15m;
        }
        if (!string.IsNullOrEmpty(buyerTaxId) && !ValidateThaiTaxId(buyerTaxId))
        {
            warnings.Add($"เลขผู้เสียภาษีผู้ซื้อ {buyerTaxId} checksum ไม่ผ่าน");
            penalty += 0.10m;
        }

        // 5. Penalize if any critical field has very low per-field confidence
        if (fieldConfidences != null)
        {
            foreach (var critical in new[] { "InvoiceTotal", "InvoiceId", "VendorName", "VendorTaxId" })
            {
                if (fieldConfidences.TryGetValue(critical, out var c) && c < 0.5m)
                {
                    warnings.Add($"{critical} ความมั่นใจต่ำ ({c:P0})");
                    penalty += 0.05m;
                }
            }
        }

        // 6. Total must be positive
        if (total.HasValue && total.Value < 0)
        {
            warnings.Add($"ยอดรวมติดลบ ({total:N2})");
            penalty += 0.20m;
        }

        var adjusted = Math.Max(0m, Math.Min(1m, modelConfidence - penalty));
        return new GatewayResult(adjusted, warnings, mathConsistent);
    }

    private static bool ValidateThaiTaxId(string taxId)
    {
        if (string.IsNullOrEmpty(taxId)) return false;
        var digits = new string(taxId.Where(char.IsDigit).ToArray());
        if (digits.Length != 13) return false;

        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (digits[i] - '0') * (13 - i);

        var checkDigit = (11 - (sum % 11)) % 10;
        return checkDigit == (digits[12] - '0');
    }
}
