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

    /// <summary>Line-item shape used by validation. Mirrors OcrExtractedLineItem
    /// — kept simple so callers can pass either internal or DTO objects.</summary>
    public sealed record LineItemForValidation(decimal? Quantity, decimal? UnitPrice, decimal? Amount);

    public static GatewayResult Validate(
        decimal modelConfidence,
        DateTime? documentDate,
        decimal? subTotal,
        decimal? vatAmount,
        decimal? total,
        string? vendorTaxId,
        string? buyerTaxId,
        Dictionary<string, decimal>? fieldConfidences = null,
        GatewayConfig? config = null,
        IReadOnlyList<LineItemForValidation>? lineItems = null,
        decimal? whtAmount = null,
        decimal? whtRatePercent = null,
        string? documentNumber = null,
        string? vendorName = null)
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

        // 2b. ── "ตัวเลขสามช่องขัดกันเอง" — ด่านที่ไม่ขึ้นกับ MathTolerance ──
        //
        // ที่มา (ใบจริงที่ผู้ใช้ส่งมา): SubTotal 922.44 · VAT 68.00 · Total 990.00
        //   • 922.44 + 68.00 = 990.44 ≠ 990.00 → ห่าง 0.44 ซึ่ง **น้อยกว่า** tolerance
        //     ค่าเริ่มต้น ฿2.00 ⇒ ข้อ 2 ไม่ฟ้อง
        //   • VAT 7% ของ 922.44 = 64.57 ≠ 68.00 → อัตราที่คำนวณได้ 7.37% ซึ่งยัง
        //     **อยู่ในกรอบ** 6.5–7.5% ของข้อ 3 ⇒ ข้อ 3 ก็ไม่ฟ้อง
        //   ⇒ ใบที่อย่างน้อยสองช่องอ่านผิด ผ่านทั้งสองด่านเงียบ ๆ ด้วย confidence 95%
        //
        // ทำไมต้องให้ **ทั้งสองเงื่อนไขจริงพร้อมกัน**: ใบที่มีหลายอัตรา (7% + 0% +
        // ยกเว้น) จะมี VAT ≠ 7% ของยอดรวมโดยชอบธรรม — แต่ใบพวกนั้น
        // `SubTotal + VAT = Total` **เป๊ะ** เพราะเป็นตัวเลขที่ผู้ขายพิมพ์ให้สอดคล้องกัน
        // ⇒ เงื่อนไขแรกกรองมันออกไปหมด ด่านนี้จึงฟ้องเฉพาะกระดาษที่ขัดกันเองจริง ๆ
        // ("checker ที่ฟ้องผิด = checker ที่พังแล้ว" ใช้กับคำเตือนถึงผู้ใช้ด้วย)
        //
        // ค่าคลาดเคลื่อนที่ยอมรับ: การปัดเศษให้ผลต่างได้ไม่เกิน 1 สตางค์ต่อการปัด
        // 1 ครั้ง — ผลรวมจึงยอม ฿0.02 และ VAT ยอม 1 สตางค์ต่อบรรทัด (ขั้นต่ำ ฿0.02)
        if (mathConsistent && subTotal.HasValue && vatAmount.HasValue && total.HasValue
            && subTotal.Value > 0m && vatAmount.Value > 0m)
        {
            var sumGap = Math.Abs(subTotal.Value + vatAmount.Value - total.Value);
            var expectedVat = Math.Round(subTotal.Value * 0.07m, 2, MidpointRounding.AwayFromZero);
            var vatGap = Math.Abs(vatAmount.Value - expectedVat);
            var vatSlack = Math.Max(0.02m, 0.01m * (lineItems?.Count ?? 0));
            if (sumGap > 0.02m && vatGap > vatSlack)
            {
                warnings.Add(
                    $"ตัวเลขสามช่องขัดกันเอง: {subTotal:N2} + {vatAmount:N2} = {(subTotal + vatAmount):N2} ≠ {total:N2} " +
                    $"และ VAT 7% ของ {subTotal:N2} ควรเป็น {expectedVat:N2} ไม่ใช่ {vatAmount:N2} — อย่างน้อยหนึ่งช่องอ่านผิด");
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
        //
        // ⚠️ เดิมมองหาชื่อชุดของ Azure ("InvoiceTotal"/"InvoiceId"/"VendorName"/
        // "VendorTaxId") ซึ่งมีเฉพาะบนเส้นทาง Azure DI ⇒ ด่านนี้ **ไม่เคยทำงาน
        // บนเส้นทาง Tesseract/Python เลย** ทั้งที่นั่นคือเส้นทางที่ความมั่นใจ
        // ต่ำที่สุดและควรถูกด่านนี้จับมากที่สุด — แปลงชื่อผ่านคำศัพท์กลาง
        // (Helpers/OcrFieldKeys.cs) ก่อนเทียบ ทุกเส้นทางจึงถูกตรวจเท่ากัน
        if (fieldConfidences != null)
        {
            var canon = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in fieldConfidences)
            {
                var ck = Accounting.Helpers.OcrFieldKeys.Canonical(k);
                if (!canon.TryGetValue(ck, out var prev) || v > prev) canon[ck] = v;
            }
            foreach (var critical in Accounting.Helpers.OcrFieldKeys.CriticalForTaxInvoice)
            {
                if (canon.TryGetValue(critical, out var c) && c < 0.5m)
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

        // 7. Line-item math: sum of Item.Amount should ≈ SubTotal
        if (lineItems != null && lineItems.Count > 0 && subTotal.HasValue)
        {
            var withAmounts = lineItems.Where(i => i.Amount.HasValue).ToList();
            if (withAmounts.Count > 0)
            {
                var lineSum = withAmounts.Sum(i => i.Amount!.Value);
                var diff = Math.Abs(lineSum - subTotal.Value);
                if (diff > config.MathTolerance)
                {
                    warnings.Add($"ผลรวมรายการ {lineSum:N2} ≠ ยอดก่อน VAT {subTotal:N2} (ห่าง {diff:N2})");
                    penalty += config.MathPenalty * 0.75m;
                    mathConsistent = false;
                }
            }

            // Per-line: Quantity × UnitPrice should ≈ Amount
            var brokenLines = 0;
            foreach (var item in lineItems)
            {
                if (item.Quantity.HasValue && item.UnitPrice.HasValue && item.Amount.HasValue)
                {
                    var expected = item.Quantity.Value * item.UnitPrice.Value;
                    if (Math.Abs(expected - item.Amount.Value) > config.MathTolerance)
                        brokenLines++;
                }
            }
            if (brokenLines > 0)
            {
                warnings.Add($"พบรายการที่ Qty × UnitPrice ≠ Amount จำนวน {brokenLines} บรรทัด");
                penalty += config.MathPenalty * 0.5m;
            }
        }

        // 8. WHT formula sanity: WhtAmount ≈ SubTotal × WhtRate / 100
        if (whtAmount.HasValue && whtRatePercent.HasValue && subTotal.HasValue
            && subTotal.Value > 0 && whtRatePercent.Value > 0)
        {
            var expectedWht = Math.Round(subTotal.Value * whtRatePercent.Value / 100m, 2);
            var diff = Math.Abs(expectedWht - whtAmount.Value);
            if (diff > config.MathTolerance)
            {
                warnings.Add($"ภาษีหัก ณ ที่จ่ายไม่ตรงสูตร: {whtRatePercent}% × {subTotal:N2} = {expectedWht:N2} ≠ {whtAmount:N2}");
                penalty += config.MathPenalty * 0.5m;
            }
        }

        // 9. Missing critical fields — penalize each one heavily so a scan
        // that can't extract date/number/total can't sail past the auto-
        // create threshold on TYPE-detection confidence alone. Previously
        // these were flag-only; auto-create then fired on a document with
        // every field null (just because "ใบกำกับภาษี" appeared on the
        // page and ParseThaiDocument set Confidence = 0.95 on that marker).
        const decimal MissingCriticalPenalty = 0.20m;
        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            warnings.Add("ไม่พบเลขที่เอกสาร — กรุณาตรวจสอบ");
            penalty += MissingCriticalPenalty;
        }
        if (!documentDate.HasValue)
        {
            warnings.Add("ไม่พบวันที่เอกสาร — กรุณาตรวจสอบ");
            penalty += MissingCriticalPenalty;
        }
        if (string.IsNullOrWhiteSpace(vendorName))
        {
            warnings.Add("ไม่พบชื่อผู้ขาย — กรุณาตรวจสอบ");
            penalty += MissingCriticalPenalty;
        }
        if (!total.HasValue || total.Value == 0)
        {
            warnings.Add("ไม่พบยอดรวม — กรุณาตรวจสอบ");
            penalty += MissingCriticalPenalty;
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
