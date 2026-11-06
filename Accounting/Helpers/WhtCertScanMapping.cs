using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 252 (WP-09) — แปลงผลสแกน OCR ของใบ 50 ทวิ เป็นค่าที่กล่องรับใช้ (pure · ตัวเดียวของทุกทางที่อ่านใบจากสแกน) ·
/// ย้ายจาก <c>OcrService.EnsureWhtCreditFromCertAsync</c> เดิม — ยอดภาษีผ่าน <see cref="WhtCertAmountResolver"/> (ห้ามเอายอดรวมทั้งใบเป็นยอดภาษี) ·
/// ประเภทเงินได้ผ่าน <see cref="WhtCertIncomeType.Infer"/> · แบบที่ผู้จ่ายยื่นตัดสินจาก <b>ผู้ถูกหัก = เรา</b> (บุคคลธรรมดา ⇒ ภ.ง.ด.3)
/// </summary>
public static class WhtCertScanMapping
{
    /// <summary>ช่องที่ใช้ตัดสินความมั่นใจ (ชื่อกลางจาก <see cref="OcrFieldKeys"/>) — ช่องเงิน + เลขผู้เสียภาษีผู้จ่าย + เลขที่/วันที่ใบ
    /// (ฝ่ายค้าน WP-09 M4: เลขที่/วันที่เป็นกุญแจกันซ้ำและงวดเครดิต — อ่านเพี้ยนแล้ว auto = ผิดงวด/ซ้ำ)</summary>
    public static readonly IReadOnlyList<string> ConfidenceKeys = new[]
    {
        OcrFieldKeys.SellerTaxId, OcrFieldKeys.SubTotal, OcrFieldKeys.TotalAmount, OcrFieldKeys.WhtRate,
        OcrFieldKeys.DocumentNumber, OcrFieldKeys.DocumentDate,
    };

    /// <param name="AmountNeedsReview">ยอดภาษีไม่ได้มาจาก เงินได้ × อัตรา (อ่านจากตัวอักษร) หรืออ่านไม่ได้ ⇒ ด่าน <c>WhtCertIntakeVerdict.Gates</c> ไม่ให้ auto</param>
    /// <param name="ExtractionConfidence">ความมั่นใจต่ำสุด 0–1 · <b>ไม่รู้ = 0</b> (สแกนที่ไม่บอกความมั่นใจ ไม่ใช่สแกนที่มั่นใจ — ฝ่ายค้าน WP-09 M4)</param>
    public sealed record Result(
        decimal IncomeAmount, decimal WhtRate, decimal WhtAmount, bool AmountUnknown, bool AmountNeedsReview,
        string? IncomeTypeCode, WhtPayerFormType PayerFormType, decimal ExtractionConfidence);

    /// <param name="fieldConfidenceJson"><c>OcrScanResult.FieldConfidenceJson</c> — {"SellerTaxId":0.95,…}</param>
    /// <param name="overallConfidence"><c>OcrScanResult.Confidence</c> — ใช้เมื่อไม่มีความมั่นใจรายช่อง (ค่า ≥ 1 ถือเป็นสเกล 0–100: 1 = 1%)</param>
    public static Result Map(decimal? subTotal, decimal? rate, decimal? totalAmount, string? rawText,
        string? fieldConfidenceJson, decimal overallConfidence, BusinessType ourBusinessType)
    {
        var amt = WhtCertAmountResolver.Resolve(subTotal, rate, totalAmount, rawText);
        var baseAmount = subTotal ?? 0m;
        var conf = MinConfidence(fieldConfidenceJson) ?? NormalizeSingle(overallConfidence) ?? 0m;
        return new Result(
            baseAmount, amt.Rate, amt.Amount, amt.IsUnknown, amt.NeedsReview,
            WhtCertIncomeType.Infer(rawText, baseAmount > 0m ? baseAmount : null),
            ourBusinessType == BusinessType.Individual ? WhtPayerFormType.Pnd3 : WhtPayerFormType.Pnd53,
            conf);
    }

    /// <summary>ความมั่นใจต่ำสุดของ <see cref="ConfidenceKeys"/> ที่มีใน JSON · ไม่มีเลย/อ่านไม่ได้ = null ·
    /// สเกลตัดสินทั้งชุด: มีค่าใดเกิน 1 ⇒ ทั้งชุดเป็น 0–100 (1 = 1%) · ไม่งั้น 0–1</summary>
    public static decimal? MinConfidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(json);
            if (map == null) return null;
            var vals = ConfidenceKeys.Where(k => map.ContainsKey(k) && !double.IsNaN(map[k]) && !double.IsInfinity(map[k]))
                .Select(k => (decimal)map[k]).ToList();
            if (vals.Count == 0) return null;
            var percent = vals.Any(v => v > 1m);
            return Clamp(vals.Min() / (percent ? 100m : 1m));
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>ค่าเดี่ยว (ไม่มีบริบทสเกล): ≥ 1 ⇒ สเกล 0–100 (1 = 1% — ฝ่ายค้าน WP-09 M4: เดิม 1 กลายเป็น 100%) · ≤ 0 = null (ไม่รู้)</summary>
    private static decimal? NormalizeSingle(decimal v)
        => v <= 0m ? null : Clamp(v >= 1m ? v / 100m : v);

    private static decimal Clamp(decimal x) => Math.Round(Math.Max(0m, Math.Min(1m, x)), 4, MidpointRounding.AwayFromZero);
}
