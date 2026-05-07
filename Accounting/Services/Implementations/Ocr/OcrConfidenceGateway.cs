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

    /// <summary>Default config — used when caller doesn't pass overrides.
    /// Production code should pass config from SiteSettings via Validate(...config).</summary>
    public sealed class GatewayConfig
    {
        public decimal MaxPenalty { get; init; } = 0.60m;
        public decimal MathTolerance { get; init; } = 2.0m;
        public decimal TaxIdPenalty { get; init; } = 0.15m;
        public decimal MathPenalty { get; init; } = 0.20m;
        public decimal DatePenalty { get; init; } = 0.15m;
        public decimal VatRatePenalty { get; init; } = 0.10m;
        public decimal LowConfidencePenalty { get; init; } = 0.05m;
        public static readonly GatewayConfig Default = new();
    }

    public static GatewayResult Validate(
        decimal modelConfidence,
        DateTime? documentDate,
        decimal? subTotal,
        decimal? vatAmount,
        decimal? total,
        string? vendorTaxId,
        string? buyerTaxId,
        Dictionary<string, decimal>? fieldConfidences = null,
        GatewayConfig? config = null)
    {
        config ??= GatewayConfig.Default;
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
                penalty += config.DatePenalty;
            }
            else if (documentDate.Value < now.AddYears(-7))
            {
                warnings.Add($"วันที่เอกสาร {documentDate.Value:yyyy-MM-dd} เก่ากว่า 7 ปี");
                penalty += config.DatePenalty * 0.67m; // older-date is less severe than future-date
            }
        }

        // 2. Math consistency: SubTotal + VAT ≈ Total
        if (subTotal.HasValue && vatAmount.HasValue && total.HasValue)
        {
            var expected = subTotal.Value + vatAmount.Value;
            var diff = Math.Abs(expected - total.Value);
            if (diff > config.MathTolerance)
            {
                warnings.Add($"คณิตศาสตร์ไม่ตรง: {subTotal:N2} + {vatAmount:N2} = {expected:N2} ≠ {total:N2} (ห่าง {diff:N2})");
                penalty += config.MathPenalty;
                mathConsistent = false;
            }
        }

        // 3. VAT rate sanity: should be ~7% in Thailand (or 0% / exempt)
        if (subTotal.HasValue && vatAmount.HasValue && subTotal.Value > 0 && vatAmount.Value > 0)
        {
            var rate = vatAmount.Value / subTotal.Value * 100m;
            if (rate < 6.5m || rate > 7.5m)
            {
                warnings.Add($"อัตรา VAT ผิดปกติ: {rate:N1}% (ปกติ 7%)");
                penalty += config.VatRatePenalty;
            }
        }

        // 4. Tax ID checksums — only penalize when extracted TaxId is 13 digits
        if (!string.IsNullOrEmpty(vendorTaxId) && new string(vendorTaxId.Where(char.IsDigit).ToArray()).Length == 13
            && !ValidateThaiTaxId(vendorTaxId))
        {
            warnings.Add($"เลขผู้เสียภาษีผู้ขาย {vendorTaxId} checksum ไม่ผ่าน");
            penalty += config.TaxIdPenalty;
        }
        if (!string.IsNullOrEmpty(buyerTaxId) && new string(buyerTaxId.Where(char.IsDigit).ToArray()).Length == 13
            && !ValidateThaiTaxId(buyerTaxId))
        {
            warnings.Add($"เลขผู้เสียภาษีผู้ซื้อ {buyerTaxId} checksum ไม่ผ่าน");
            penalty += config.TaxIdPenalty * 0.67m; // buyer mismatch less severe than vendor
        }

        // 5. Per-field confidence penalty
        if (fieldConfidences != null)
        {
            foreach (var critical in new[] { "InvoiceTotal", "InvoiceId", "VendorName", "VendorTaxId" })
            {
                if (fieldConfidences.TryGetValue(critical, out var c) && c < 0.5m)
                {
                    warnings.Add($"{critical} ความมั่นใจต่ำ ({c:P0})");
                    penalty += config.LowConfidencePenalty;
                }
            }
        }

        // 6. Total must be positive
        if (total.HasValue && total.Value < 0)
        {
            warnings.Add($"ยอดรวมติดลบ ({total:N2})");
            penalty += config.MathPenalty;
        }

        // Cap penalty
        if (penalty > config.MaxPenalty) penalty = config.MaxPenalty;

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
