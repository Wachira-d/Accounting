namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Cross-validates extracted fields for mathematical and logical consistency.
/// Subtotal + VAT should equal Total; date should be reasonable; etc.
/// </summary>
public static class CrossValidator
{
    public class ValidationResult
    {
        public bool IsValid { get; set; } = true;
        public List<string> Issues { get; set; } = new();
        public List<string> Corrections { get; set; } = new();
        public double ConfidenceAdjustment { get; set; } = 1.0;
    }

    public static ValidationResult ValidateAmounts(
        decimal? subTotal, decimal? vatAmount, decimal? totalAmount,
        bool isVatInclusive = false, decimal vatRate = 0.07m)
    {
        var r = new ValidationResult();

        if (totalAmount is null && subTotal is null)
        {
            r.IsValid = false;
            r.Issues.Add("ไม่พบยอดเงินใดๆ");
            r.ConfidenceAdjustment = 0.5;
            return r;
        }

        // If we have subtotal + vat, total should match
        if (subTotal.HasValue && vatAmount.HasValue && totalAmount.HasValue)
        {
            var expected = subTotal.Value + vatAmount.Value;
            var diff = Math.Abs(expected - totalAmount.Value);
            if (diff > 0.5m) // more than 50 satang difference
            {
                r.Issues.Add($"Subtotal({subTotal}) + VAT({vatAmount}) ≠ Total({totalAmount}), ต่างกัน {diff:F2}");
                r.ConfidenceAdjustment *= 0.7;
            }
        }

        // VAT rate sanity check: VAT / SubTotal should be ~7%
        if (subTotal.HasValue && vatAmount.HasValue && subTotal.Value > 0)
        {
            var actualRate = vatAmount.Value / subTotal.Value;
            if (Math.Abs(actualRate - vatRate) > 0.01m)
            {
                r.Issues.Add($"อัตรา VAT ผิดปกติ: {actualRate:P2} (คาดว่า {vatRate:P0})");
                r.ConfidenceAdjustment *= 0.8;
            }
        }

        return r;
    }

    public static ValidationResult ValidateDate(DateTime? date, DateTime? minDate = null, DateTime? maxDate = null)
    {
        var r = new ValidationResult();
        if (!date.HasValue)
        {
            r.Issues.Add("ไม่พบวันที่");
            r.ConfidenceAdjustment = 0.6;
            return r;
        }
        var min = minDate ?? new DateTime(2000, 1, 1);
        var max = maxDate ?? DateTime.UtcNow.AddYears(1);
        if (date.Value < min || date.Value > max)
        {
            r.Issues.Add($"วันที่อยู่นอกช่วงที่ยอมรับ: {date:yyyy-MM-dd} (ต้องอยู่ระหว่าง {min:yyyy-MM-dd} ถึง {max:yyyy-MM-dd})");
            r.ConfidenceAdjustment = 0.4;
            r.IsValid = false;
        }
        return r;
    }

    /// <summary>
    /// Compute missing amounts from known ones: SubTotal + Vat = Total, etc.
    ///
    /// <para>⚠️ รอบ 195 ฝ่ายค้านรอบสอง R2-2: <b>ไม่ถอด VAT ออกจากยอดรวมเมื่อรู้แค่ยอดรวม</b> — เดิมกิ่งสุดท้ายถอด 7/107 ทุกครั้ง
    /// (ไม่ถามคำ VAT · เลขผู้ขาย · สินค้ายกเว้น ม.81 · ไม่ติดแท็ก · ปัดแบบ banker's) แล้วเส้น ZoneFallback
    /// (<c>OcrService.ApplyZoneAnalysisFallbackAsync</c>) เติมเป็น VAT ของใบ ⇒ ภาษีซื้อที่ไม่มีบนกระดาษเข้า ภ.พ.30 ·
    /// การถอดต้องผ่าน <c>Helpers/OcrVatBackCalc.Plan</c> (→ <c>VatBackCalcGuard.Decide</c> + แท็ก <c>[VAT back-calc]</c>) ที่ผู้เรียก ·
    /// ที่นี่เหลือแต่ "ยอดที่พิมพ์สองตัว ⇒ ตัวที่สาม" (ยังเป็นเลขคณิตของตัวเลขบนกระดาษ ไม่ใช่การเดาอัตรา)</para>
    /// </summary>
    public static (decimal? sub, decimal? vat, decimal? total) FillMissingAmounts(
        decimal? sub, decimal? vat, decimal? total)
    {
        if (sub.HasValue && vat.HasValue && !total.HasValue)
            total = sub.Value + vat.Value;
        else if (total.HasValue && vat.HasValue && !sub.HasValue)
            sub = total.Value - vat.Value;
        else if (total.HasValue && sub.HasValue && !vat.HasValue)
            vat = total.Value - sub.Value;
        return (sub, vat, total);
    }
}
