using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **"ใบกำกับภาษีที่ทุกบรรทัด VAT 0% — เป็นใบอัตราศูนย์จริง หรือระบบอ่าน VAT ไม่ได้" — ตัวตัดสินตัวเดียว** (pure)
///
/// ═══ ที่มา (ฝ่ายค้านรอบ 269 ข้อ 3 · คำตัดสิน 269-2-8 (ก) → 270-1-5) ═══
/// <para>ใบกำกับจากสแกนที่ engine อ่านแถว VAT ไม่ได้ ⇒ บรรทัดสรุปของ <c>OcrService.BuildScanLinesAsync</c> ได้
/// <c>VatRate = ExtractedVatAmount &gt; 0 ? 7 : 0</c> = <b>0</b> ⇒ ใบถูกนับเป็นยอดขาย/ซื้ออัตราศูนย์ (ช่อง 2 ของ ภ.พ.30) และภาษีขาย/ซื้อหายทั้งก้อน ·
/// ที่รายงานแยกไม่ได้ เพราะรูปเดียวกับใบกำกับส่งออกจริง (§80/1) ⇒ ต้องกันที่ OCR: ใบกำกับที่ VAT หายไปทั้งใบ <b>และกระดาษไม่บอกว่าเป็นอัตราศูนย์</b>
/// ห้ามผ่านโดยไม่มีคนดู (ไม่แต่งอัตรา 7% ให้เอง — F2 ข้อ 3)</para>
/// <para>กติกา (ทุกข้อต้องจริง ⇒ ติดแท็ก <see cref="Tag"/> ซึ่ง <see cref="OcrPostingReadiness.BlockingTags"/> หยุดการอนุมัติเอง/LINE/สร้าง+อนุมัติ — คนยังอนุมัติเองได้):
/// <list type="number">
/// <item>เป็นใบกำกับภาษี: ชนิดเอกสาร = ใบกำกับ หรือกระดาษพิมพ์ "ใบกำกับภาษี/TAX INVOICE" (ตัดวลีปฏิเสธ "ไม่ใช่ใบกำกับภาษี" ออกก่อน)</item>
/// <item>มีบรรทัด และ<b>ทุกบรรทัด</b> VatRate = 0 · VAT บรรทัดรวม = 0 · VAT หัวใบ 0/ไม่มี</item>
/// <item>กระดาษ<b>ไม่มี</b>เครื่องหมายอัตราศูนย์/ส่งออก (<see cref="HasZeroRatedMarker"/>)</item>
/// </list>
/// ทิศตรงข้าม: ใบส่งออก/อัตราศูนย์ที่พิมพ์ไว้ ⇒ ไม่ติด · ใบที่อ่าน VAT ได้/มีบรรทัด 7% ⇒ ไม่ติด · ใบเสร็จ/ใบแจ้งหนี้ที่ไม่ใช่ใบกำกับ (ผู้ขายไม่จด VAT) ⇒ ไม่ติด ·
/// บรรทัดยกเว้น (VatRate &lt; 0) ⇒ ไม่ใช่ "0% ทั้งใบ" ⇒ ไม่ติด</para>
/// </summary>
public static class OcrZeroVatTaxInvoiceGuard
{
    public const string Tag = "[VAT-ZERO-UNREAD]";
    public const string RuleCode = "OCR-TAXINV-ZERO-VAT";
    public const string LegalReference = "ป.รัษฎากร ม.86/4 (ใบกำกับต้องแสดงจำนวนภาษี) · ม.80/1 (อัตราศูนย์) · ม.82/5(1)";

    private static readonly Regex Negation = new(@"(ไม่ใช่|มิใช่|ไม่ถือเป็น)\s*ใบกำกับภาษี|not\s+(a\s+)?tax\s+invoice", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TaxInvoiceWord = new(@"ใบกำกับภาษี|tax\s*invoice", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ZeroRated = new(
        @"ส่งออก|export|zero[\s-]*rat|อัตรา\s*(ร้อยละ\s*)?(ศูนย์|0(\.0+)?\s*%?)(?!\d)|ร้อยละ\s*(ศูนย์|0(\.0+)?)(?![\d.,])|(มาตรา|ม\.|§)\s*80/1(?!\d)|" +
        @"(vat|ภาษีมูลค่าเพิ่ม|ภ\.?\s*พ\.?)\s*(อัตรา\s*)?0(\.0+)?\s*%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>กระดาษเป็นใบกำกับภาษี (คำบนกระดาษ หลังตัดวลีปฏิเสธ)</summary>
    private static bool PaperSaysTaxInvoice(string? rawText)
        => !string.IsNullOrWhiteSpace(rawText) && TaxInvoiceWord.IsMatch(Negation.Replace(rawText, " "));

    /// <summary>กระดาษบอกว่าเป็นอัตราศูนย์/ส่งออก (§80/1) — "VAT 0%" · "ภาษีมูลค่าเพิ่มอัตราร้อยละ 0" · "ส่งออก/EXPORT" · "Zero-rated" · "80/1"</summary>
    private static bool HasZeroRatedMarker(string? rawText) => !string.IsNullOrWhiteSpace(rawText) && ZeroRated.IsMatch(rawText);

    /// <summary>คืนหมายเหตุ (ขึ้นต้นด้วย <see cref="Tag"/>) เมื่อต้องให้คนตรวจ · null = ผ่าน</summary>
    /// <param name="documentIsTaxInvoice">ชนิดเอกสารที่จะสร้าง = ใบกำกับภาษี (ฝั่งขาย)</param>
    /// <param name="lineVatRates">อัตรา VAT ของบรรทัดที่สร้างแล้ว (−1 = ยกเว้น)</param>
    public static string? Check(bool documentIsTaxInvoice, string? rawText, decimal? headerVat, IReadOnlyList<decimal> lineVatRates, decimal lineVatTotal)
    {
        if (!(documentIsTaxInvoice || PaperSaysTaxInvoice(rawText))) return null;
        if (lineVatRates.Count == 0 || lineVatRates.Any(r => r != 0m)) return null;
        if (lineVatTotal != 0m || (headerVat ?? 0m) != 0m) return null;
        if (HasZeroRatedMarker(rawText)) return null;
        return $"{Tag} ใบกำกับภาษีแต่ทุกบรรทัดเป็น VAT 0% และกระดาษไม่มีเครื่องหมายอัตราศูนย์/ส่งออก (§80/1) — ระบบน่าจะอ่านแถวภาษีมูลค่าเพิ่มไม่ได้ · "
               + "ตรวจกับกระดาษ: ใบ 7% ⇒ แก้อัตรา/VAT ของบรรทัดตามกระดาษ · ใบส่งออก/อัตราศูนย์จริง ⇒ อนุมัติเองได้ [" + RuleCode + "]";
    }
}
