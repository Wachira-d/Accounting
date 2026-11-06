using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 252 (WP-09) — แปลงผลสแกน OCR ของใบ 50 ทวิ เป็นค่าที่กล่องรับใช้ (pure · ตัวเดียวของทุกทางที่อ่านใบจากสแกน) ·
/// ย้ายจาก <c>OcrService.EnsureWhtCreditFromCertAsync</c> เดิม — ยอดภาษีผ่าน <see cref="WhtCertAmountResolver"/> (ห้ามเอายอดรวมทั้งใบเป็นยอดภาษี) ·
/// ประเภทเงินได้ผ่าน <see cref="WhtCertIncomeType.Infer"/> · แบบที่ผู้จ่ายยื่นตัดสินจาก <b>ผู้ถูกหัก = เรา</b> (บุคคลธรรมดา ⇒ ภ.ง.ด.3)
/// </summary>
public static class WhtCertScanMapping
{
    /// <summary>ช่องที่ใช้ตัดสินความมั่นใจ (ชื่อกลางจาก <see cref="OcrFieldKeys"/>) — ช่องเงิน + เลขผู้เสียภาษีผู้จ่าย</summary>
    public static readonly IReadOnlyList<string> ConfidenceKeys = new[]
    {
        OcrFieldKeys.SellerTaxId, OcrFieldKeys.SubTotal, OcrFieldKeys.TotalAmount, OcrFieldKeys.WhtRate,
    };

    public sealed record Result(
        decimal IncomeAmount, decimal WhtRate, decimal WhtAmount, bool AmountUnknown, bool AmountNeedsReview,
        string? IncomeTypeCode, WhtPayerFormType PayerFormType, decimal? ExtractionConfidence);

    /// <param name="fieldConfidenceJson"><c>OcrScanResult.FieldConfidenceJson</c> — {"SellerTaxId":0.95,…}</param>
    /// <param name="overallConfidence"><c>OcrScanResult.Confidence</c> (0–1 หรือ 0–100) — ใช้เมื่อไม่มีความมั่นใจรายช่อง</param>
    public static Result Map(decimal? subTotal, decimal? rate, decimal? totalAmount, string? rawText,
        string? fieldConfidenceJson, decimal overallConfidence, BusinessType ourBusinessType)
    {
        var amt = WhtCertAmountResolver.Resolve(subTotal, rate, totalAmount, rawText);
        var baseAmount = subTotal ?? 0m;
        var conf = MinConfidence(fieldConfidenceJson) ?? Normalize(overallConfidence);
        // ยอดที่ไม่ได้มาจากการคำนวณ (อ่านจากตัวอักษร) หรืออ่านไม่ได้ ⇒ ต้องให้คนตรวจ — ความมั่นใจต้องไม่ถึงเกณฑ์ auto
        if (amt.NeedsReview && conf is decimal c && c >= WhtCertIntakeVerdict.ExtractionThreshold)
            conf = Math.Round(WhtCertIntakeVerdict.ExtractionThreshold - 0.01m, 4, MidpointRounding.AwayFromZero);
        return new Result(
            baseAmount, amt.Rate, amt.Amount, amt.IsUnknown, amt.NeedsReview,
            WhtCertIncomeType.Infer(rawText, baseAmount > 0m ? baseAmount : null),
            ourBusinessType == BusinessType.Individual ? WhtPayerFormType.Pnd3 : WhtPayerFormType.Pnd53,
            conf);
    }

    /// <summary>ความมั่นใจต่ำสุดของ <see cref="ConfidenceKeys"/> ที่มีใน JSON · ไม่มีเลย/อ่านไม่ได้ = null</summary>
    public static decimal? MinConfidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(json);
            if (map == null) return null;
            decimal? min = null;
            foreach (var k in ConfidenceKeys)
                if (map.TryGetValue(k, out var v) && !double.IsNaN(v))
                {
                    var d = Normalize((decimal)v);
                    if (d is decimal dd && (min == null || dd < min)) min = dd;
                }
            return min;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>0–100 ⇒ 0–1 · ค่าติดลบ/ศูนย์ = null (ไม่รู้)</summary>
    private static decimal? Normalize(decimal v)
    {
        if (v <= 0m) return null;
        var x = v > 1m ? v / 100m : v;
        return Math.Round(Math.Min(1m, x), 4, MidpointRounding.AwayFromZero);
    }
}
