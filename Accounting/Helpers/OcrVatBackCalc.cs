using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ผลของตัวแยก VAT ชุดที่สอง</summary>
public enum OcrVatBackCalcAction
{
    /// <summary>ข้อความไม่ได้พูดถึง VAT/ใบกำกับเลย — ไม่ทำอะไร (พฤติกรรมเดิม)</summary>
    NotApplicable = 0,
    /// <summary>ด่าน (<see cref="VatBackCalcGuard"/>) ปฏิเสธ — เว้น SubTotal/VatAmount ว่างไว้</summary>
    Skip = 1,
    /// <summary>แยก 7/107 ได้ — ค่าที่ได้เป็นค่า<b>คำนวณ</b> (ความมั่นใจตามหลักฐานของด่าน)</summary>
    Apply = 2,
}

/// <summary>คำตัดสินของตัวแยก VAT ชุดที่สอง</summary>
/// <param name="Trace">บรรทัด ReasoningTrace (ขึ้นต้นด้วย <see cref="VatBackCalcGuard.SkipTag"/>/<see cref="VatBackCalcGuard.BackCalcTag"/>) · null = ไม่ต้องเขียน</param>
public sealed record OcrVatBackCalcPlan(
    OcrVatBackCalcAction Action, decimal? SubTotal, decimal? VatAmount, double Confidence, string? Trace);

/// <summary>
/// **ตัวแยก VAT จากยอดรวมชุดที่สอง (<c>SmartFieldExtractor.ApplyAmountMath</c>) — ต้องถามด่านเดียวกับชุดแรก** (pure)
///
/// ═══ ที่มา (รอบ 195 ฝ่ายค้าน C1 · ต้นเหตุร่วม) ═══
/// <para>back-calc มีสองชุด: <c>OcrService.ParseThaiDocument</c> ผ่าน <see cref="VatBackCalcGuard.Decide"/> (เลขผู้ขาย · สินค้ายกเว้น ม.81 ·
/// คำเชิญชวน · VAT ที่พิมพ์ขัด) และ <c>SmartFieldExtractor.ApplyAmountMath</c> ซึ่งถามแค่ "ข้อความมีคำ VAT/ภาษีมูลค่าเพิ่มที่ไหนก็ได้" —
/// รวมคำว่า "<b>ยกเว้น</b>ภาษีมูลค่าเพิ่ม"/"NON VAT" ที่บอกตรงข้าม · และ <c>Enrich</c> รัน<b>หลัง</b> <c>ParseThaiDocument</c>
/// ⇒ ด่านปฏิเสธแล้วเว้น VAT ว่าง ⇒ ชุดที่สองมาเติมทับ ⇒ ใบผัก 1,070 ได้ VAT แต่ง 70.00 (ภาษีซื้อที่ไม่มีอยู่จริง) ·
/// ชุดที่สองไม่ติดแท็กด้วย ⇒ <c>ValidateOrInferVatRate</c> ดันความมั่นใจของค่าแต่งขึ้น 0.95 เพราะ "= 7% ของฐาน" (สูตรเดียวกันอีกรอบ)</para>
///
/// <para>กติกา: (1) คำที่บอกว่า<b>ไม่มี</b> VAT ("ยกเว้นภาษีมูลค่าเพิ่ม" · "ไม่มี/ไม่เสีย/ไม่ได้จดภาษีมูลค่าเพิ่ม" · "NON VAT" ·
/// "VAT exempt") ไม่นับเป็นหลักฐานว่ามี VAT (2) ด่านเคยปฏิเสธใบนี้แล้ว (<see cref="VatBackCalcGuard.SkipTag"/> ใน trace) = ไม่แยกซ้ำ
/// (3) ไม่เคยถาม ⇒ ถาม <see cref="VatBackCalcGuard.Decide"/> ตัวเดียวกัน (4) แยกแล้วติด <see cref="VatBackCalcGuard.BackCalcTag"/> เสมอ</para>
/// </summary>
public static class OcrVatBackCalc
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>วลีที่บอกว่า<b>ไม่มี</b> VAT — ตัดทิ้งก่อนถามว่า "ข้อความพูดถึง VAT ไหม" ("ไม่รวมภาษีมูลค่าเพิ่ม" = ราคาก่อน VAT ⇒ มี VAT จึงไม่อยู่ในนี้)</summary>
    private static readonly Regex NoVatPhrases = new(
        @"(?:ได้รับ)?(?:การ)?ยกเว้น[ \t]*ภาษีมูลค่าเพิ่ม|ไม่(?:ต้อง)?(?:มี|เสีย|ได้จด(?:ทะเบียน)?|จด(?:ทะเบียน)?)[ \t]*ภาษีมูลค่าเพิ่ม|"
        + @"(?<![A-Za-z])non[- \t]?vat(?![A-Za-z])|(?<![A-Za-z])vat[- \t]?exempt\w*|(?<![A-Za-z])exempt\w*[ \t]+(?:from[ \t]+)?vat(?![A-Za-z])|"
        + @"(?<![A-Za-z])no[ \t]+vat(?![A-Za-z])", Opt);

    /// <summary>ข้อความพูดถึงใบกำกับภาษี/VAT <b>ในความหมายว่ามี</b> ไหม (แทน <c>LooksLikeVatDoc</c> เดิม — คำเดิมทุกคำ ลบเฉพาะวลีปฏิเสธ)</summary>
    internal static bool MentionsVat(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = NoVatPhrases.Replace(text, " ");
        return t.Contains("ใบกำกับภาษี", StringComparison.Ordinal) || t.Contains("ใบกํากับภาษี", StringComparison.Ordinal)
            || t.ToUpperInvariant().Contains("TAX INVOICE", StringComparison.Ordinal)
            || t.Contains("ภาษีมูลค่าเพิ่ม", StringComparison.Ordinal) || t.Contains("VAT", StringComparison.Ordinal);
    }

    /// <summary>ด่านแยก VAT เคยปฏิเสธใบนี้แล้ว (ตัวแยกชุดแรก/รอบก่อนของ Enrich)</summary>
    private static bool GuardRefused(IEnumerable<string>? trace)
        => trace?.Any(t => t != null && t.StartsWith(VatBackCalcGuard.SkipTag, StringComparison.Ordinal)) == true;

    /// <summary>VAT/ฐานของใบนี้ถูกคำนวณจากยอดรวม (ไม่ได้อ่านจากกระดาษ) — ห้ามใช้เลขคณิต 7% "ยืนยัน" ค่านั้น</summary>
    public static bool WasBackCalculated(IEnumerable<string>? trace)
        => trace?.Any(t => t != null && t.StartsWith(VatBackCalcGuard.BackCalcTag, StringComparison.Ordinal)) == true;

    /// <summary>ตัดสินการแยก VAT เมื่ออ่านได้แต่ยอดรวม (ไม่มีฐาน/VAT)</summary>
    /// <param name="rawText">ข้อความกระดาษ (normalize แล้ว)</param>
    /// <param name="total">ยอดรวมทั้งสิ้นที่อ่านได้</param>
    /// <param name="vendorTaxId">เลขผู้ขายที่ถืออยู่</param>
    /// <param name="lineDescriptions">คำบรรยายรายการ (ด่านสินค้ายกเว้น ม.81)</param>
    /// <param name="trace">ReasoningTrace ที่มีอยู่ — อ่านคำตัดสินเดิมของด่าน</param>
    public static OcrVatBackCalcPlan Plan(
        string? rawText, decimal total, string? vendorTaxId, IEnumerable<string?>? lineDescriptions, IEnumerable<string>? trace)
    {
        if (total <= 0m || !MentionsVat(rawText))
            return new(OcrVatBackCalcAction.NotApplicable, null, null, 0d, null);
        if (GuardRefused(trace))
            return new(OcrVatBackCalcAction.Skip, null, null, 0d,
                VatBackCalcGuard.SkipTag + " ด่านแยก VAT ปฏิเสธใบนี้ไปแล้ว — ตัวเติมยอดชุดที่สองไม่แยก VAT ซ้ำ (เว้น SubTotal/VAT ว่างให้ตรวจ)");
        var decision = VatBackCalcGuard.Decide(rawText, vendorTaxId, lineDescriptions, totalAmount: total);
        if (!decision.Allowed)
            return new(OcrVatBackCalcAction.Skip, null, null, 0d, VatBackCalcGuard.SkipTag + " " + decision.Reason);
        var sub = Math.Round(total / 1.07m, 2, MidpointRounding.AwayFromZero);
        return new(OcrVatBackCalcAction.Apply, sub, total - sub, decision.Confidence,
            VatBackCalcGuard.BackCalcTag + " " + decision.Reason);
    }
}
