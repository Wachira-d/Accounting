using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <param name="Allowed">แยก VAT ออกจากยอดรวมได้หรือไม่</param>
/// <param name="Confidence">ความมั่นใจของค่าที่จะเขียนลง SubTotal/VatAmount
/// (ใช้ตั้ง <c>FieldConfidence</c> ให้หน้า review ไฮไลต์เหลืองตามกฎเหล็ก #3)</param>
/// <param name="Reason">เหตุผลภาษาไทยสำหรับ trace/ป้ายเตือน</param>
public sealed record VatBackCalcDecision(bool Allowed, double Confidence, string Reason);

/// <summary>
/// "ใบนี้แยก VAT 7% ออกจากยอดรวมได้ไหม" — ด่านของการ<b>แต่งตัวเลขภาษี</b>
///
/// ═══ ทำไมต้องมี (ผลตรวจ OCR 2026-09-06 · T1-22) ═══
/// เส้น Tesseract แต่งยอด VAT ขึ้นเอง (7/107 ของยอดรวม) เมื่อกระดาษมีคำว่า
/// "ใบกำกับภาษี" + ผู้ขายมีเลข 13 หลัก — โดยไม่มีตัวเลข VAT บนกระดาษเลย
/// สามทางที่พัง:
/// <list type="number">
/// <item>คำว่า "ใบกำกับภาษี" มาจากข้อความ<b>ทั้งหน้า</b> — ใบเสร็จร้านที่พิมพ์
///   ท้ายบิลว่า "ขอใบกำกับภาษีได้ที่เคาน์เตอร์" ก็เข้าเงื่อนไข</item>
/// <item>ผู้ขายจด VAT แต่ขายสินค้า<b>ยกเว้น §81</b> (ผัก/ผลไม้สด/หนังสือ/
///   ค่าเช่าอสังหา) — ยอดรวมไม่มี VAT อยู่ข้างใน แต่ระบบแยก 7/107 ออกมา</item>
/// <item>ไม่มี key ใน <c>FieldConfidence</c> ให้ค่าที่คำนวณ ⇒ ป้ายเหลืองไม่ขึ้น
///   ผู้ใช้เห็นตัวเลขที่แต่งขึ้นเหมือนตัวเลขที่อ่านมาจริง</item>
/// </list>
/// แล้วตัวเลขนี้ไหลไป ภ.พ.30 เป็น "ภาษีซื้อ" ที่ไม่มีอยู่จริง
///
/// ═══ ทำไมไม่ปิดการ back-calc ทิ้งไปเลย ═══
/// ใบกำกับไทยจำนวนมากพิมพ์แต่ยอดรวม และเส้นนี้คือ tier สุดท้าย — ปิดทิ้ง =
/// ผู้ใช้ต้องกรอกเองทุกใบ (ขัดกฎเหล็ก #3) · ทางที่ถูกคือ <b>ยังคำนวณ แต่ติดป้าย
/// ความมั่นใจตามหลักฐานที่มีจริง</b> และ<b>ไม่คำนวณเลย</b>เมื่อมีสัญญาณว่า
/// ยอดนั้นไม่มี VAT อยู่ข้างใน
/// </summary>
public static class VatBackCalcGuard
{
    /// <summary>แท็กใน <c>ReasoningTrace</c> เมื่อด่าน<b>ปฏิเสธ</b>การแยก VAT — ผู้อ่านคือ <see cref="OcrVatBackCalc"/> (ตัวแยกชุดที่สอง
    /// ใน <c>SmartFieldExtractor</c> ต้องเคารพคำตัดสินนี้ · รอบ 195 ฝ่ายค้าน C1)</summary>
    public const string SkipTag = "[VAT skip]";

    /// <summary>แท็กใน <c>ReasoningTrace</c> เมื่อ VAT/ฐาน<b>ถูกคำนวณจากยอดรวม</b> (ไม่ได้อ่านจากกระดาษ) — ผู้อ่านคือ
    /// <see cref="OcrVatBackCalc.WasBackCalculated"/> (ห้ามดันความมั่นใจของค่าที่คำนวณเองขึ้นด้วยสูตรเดียวกัน)</summary>
    public const string BackCalcTag = "[VAT back-calc]";

    /// <summary>กระดาษบอกเองว่าราคารวมภาษีแล้ว — หลักฐานที่แข็งที่สุด</summary>
    private static readonly Regex InclusiveWords = new(
        @"ราคา(?:นี้)?รวม(?:ภาษี|vat)|รวมภาษีมูลค่าเพิ่ม|รวม[ \t]*vat|vat[ \t]*included|include[sd]?[ \t]*vat|inclusive[ \t]*of[ \t]*vat",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>คำว่า "ใบกำกับภาษี" ที่เป็น<b>คำเชิญชวน</b> ไม่ใช่หัวเอกสาร —
    /// "ขอใบกำกับภาษีได้ที่เคาน์เตอร์" · "ใบกำกับภาษีจะจัดส่งทางไปรษณีย์"</summary>
    private static readonly Regex OfferWords = new(
        @"(?:ขอ|ติดต่อขอ|รับ)[ \t]*ใบกำกับภาษี|ใบกำกับภาษี[ \t]*(?:จะ)?(?:จัดส่ง|ส่งให้|ตามมา|ออกให้ภายหลัง)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="totalAmount">ยอดรวมที่จะถูกแยก 7/107 — ส่งมาเมื่อรู้ เพื่อให้ด่านเทียบกับ VAT ที่<b>พิมพ์อยู่แล้ว</b>
    /// (<see cref="PrintedVatContradicts"/>) · null = พฤติกรรมเดิม</param>
    public static VatBackCalcDecision Decide(
        string? rawText, string? vendorTaxId, IEnumerable<string?>? lineDescriptions, decimal? totalAmount = null)
    {
        var text = rawText ?? "";

        // กระดาษพิมพ์ยอด VAT ไว้แล้ว (ตารางสรุปตามกลุ่มภาษี/แถว VAT) และไม่ใช่ 7/107 ของยอดรวม ⇒ ห้ามแต่ง
        if (totalAmount is decimal tot && PrintedVatContradicts(text, tot) is string printedWhy)
            return new(false, 0d, printedWhy);

        if (!ThaiTaxId.IsValid(vendorTaxId))
            return new(false, 0d,
                "เอกสารพูดถึง “ใบกำกับภาษี” แต่ผู้ขายไม่มีเลขประจำตัวผู้เสียภาษี 13 หลักที่ถูกต้อง "
                + "— ผู้ไม่จด VAT ออกใบกำกับไม่ได้ (§86) จึงไม่แยก VAT ให้");

        // สินค้ายกเว้น §81 — ยอดรวมไม่มี VAT อยู่ข้างใน การแยก 7/107 คือการแต่งภาษีซื้อ
        var lines = (lineDescriptions ?? Enumerable.Empty<string?>()).ToList();
        if (lines.Count > 0 && lines.All(d => string.IsNullOrWhiteSpace(d) || ThaiVatTypeRule.LooksExempt(d)))
            return new(false, 0d,
                "ทุกรายการบนใบเข้าข่ายสินค้ายกเว้น VAT (§81) — ยอดรวมไม่น่ามี VAT อยู่ข้างใน จึงไม่แยกให้");

        // คำว่า "ใบกำกับภาษี" ที่พบเป็น**คำเชิญชวน**ล้วน ๆ (ไม่มีที่อื่นบนหน้า)
        if (OfferWords.IsMatch(text) && CountTaxInvoiceMentions(text) <= CountOffers(text))
            return new(false, 0d,
                "คำว่า “ใบกำกับภาษี” บนเอกสารเป็นข้อความเชิญชวน (ขอ/จะจัดส่ง) ไม่ใช่หัวเอกสาร "
                + "— ใบนี้น่าจะเป็นใบเสร็จธรรมดา จึงไม่แยก VAT ให้");

        if (InclusiveWords.IsMatch(text))
            return new(true, 0.75d, "กระดาษระบุว่าราคารวมภาษีมูลค่าเพิ่มแล้ว → แยก VAT 7% ออกจากยอดรวม");

        // ไม่มีหลักฐานตรง ๆ แต่ก็ไม่มีสัญญาณค้าน — คำนวณให้เพื่อให้ 1-click ทำงานต่อ
        // **แต่ต้องติดป้ายว่าเป็นค่าที่คำนวณ ไม่ใช่ค่าที่อ่านมาจากกระดาษ**
        return new(true, 0.50d,
            "กระดาษไม่ได้พิมพ์ยอด VAT ไว้ — ระบบคำนวณจากยอดรวม (7/107) ให้เป็นค่าเริ่มต้น "
            + "กรุณาตรวจกับใบจริงก่อนอนุมัติ");
    }

    /// <summary>
    /// **VAT ที่จะแต่ง (7/107 ของยอดรวม) ขัดกับ VAT ที่กระดาษพิมพ์ไว้แล้วไหม** — null = ไม่ขัด (หรือกระดาษไม่พิมพ์ VAT)
    ///
    /// <para>ที่มา (รอบ 192 · ทีม B #3): ใบ Makro หน้า 3/3 พิมพ์ VAT 1,148.28 ไว้ใน<b>ตารางสรุปตามรหัส ภ.พ.</b>
    /// (ไม่มีป้าย VAT บนแถวเดียวกับตัวเลข) ⇒ regex VAT ไม่เจอ ⇒ เส้น Tesseract แต่ง 7/107 ของ "TOTAL 24,110.00"
    /// = <b>1,577.29</b> แล้ว AmountTriple ถือว่า "สอดคล้อง 7%" จึงไม่ค้นต่อ ⇒ ภาษีซื้อเกินจริง 429.01 โดยไม่มีด่านไหนหยุด ·
    /// back-calc มีสองชุด (<c>OcrService.ParseThaiDocument</c> ผ่าน <see cref="Decide"/> ·
    /// <c>SmartFieldExtractor.ApplyAmountMath</c> ไม่ผ่าน) — ทั้งคู่ต้องถามคำถามเดียวกันนี้</para>
    ///
    /// <para>หลักฐาน "VAT ที่พิมพ์" = แถวที่มีป้าย VAT + VAT รวมของตารางสรุปตามกลุ่มภาษี (<see cref="OcrPaperAmounts.VatAmounts"/>)
    /// · มีตัวใดตัวหนึ่งเท่ากับ 7/107 (±0.02) = ไม่ขัด (ใบ Wine Pro ที่ VAT 235.06 = 7/107 ของ 3,593 ยังแยกได้ตามเดิม)</para>
    /// </summary>
    internal static string? PrintedVatContradicts(string? rawText, decimal totalAmount)
    {
        if (totalAmount <= 0m || string.IsNullOrWhiteSpace(rawText)) return null;
        var printed = OcrPaperAmounts.VatAmounts(rawText);
        if (printed.Count == 0) return null;
        var guessed = Math.Round(totalAmount * 7m / 107m, 2, MidpointRounding.AwayFromZero);
        if (printed.Any(v => Math.Abs(v.Amount - guessed) <= OcrPaperAmounts.ExactTol)) return null;
        var shown = string.Join(" / ", printed.Select(v => v.Amount.ToString("N2")).Distinct());
        return $"กระดาษพิมพ์ VAT {shown} ไว้แล้ว แต่ 7/107 ของยอดรวม {totalAmount:N2} = {guessed:N2} "
            + "— ไม่แต่ง VAT ที่ขัดกับกระดาษ (ใบอาจผสมสินค้ายกเว้น หรือยอดรวมที่อ่านได้เป็นยอดก่อนหักส่วนลด)";
    }

    private static int CountTaxInvoiceMentions(string text)
        => Regex.Matches(text, "ใบกำกับภาษี", RegexOptions.IgnoreCase).Count;

    private static int CountOffers(string text) => OfferWords.Matches(text).Count;
}
