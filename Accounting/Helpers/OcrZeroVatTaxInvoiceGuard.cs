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
    // ฝ่ายค้าน 270 M2 (คำตัดสิน 270-2-3): เครื่องหมายต้อง "ชัด" — เดิมคำว่า ส่งออก/EXPORT ที่ไหนก็ได้ (ชื่อผู้ขาย "IMPORT EXPORT" · ที่อยู่ "นิคมฯ เพื่อการส่งออก")
    // ปิดด่านใบ 7% ที่อ่าน VAT ไม่ได้ · ตอนนี้รับเฉพาะ (1) ป้ายกฎหมาย ม.80/1 / "อัตราศูนย์" / zero-rated (2) "0%"/"ร้อยละ 0" บนบรรทัด VAT
    // (3) ส่งออก/EXPORT บนบรรทัด VAT/อัตรา หรือกำกับชนิดใบกำกับ ("TAX INVOICE (EXPORT)") หรือใบขนสินค้าขาออก · ตัด import-export/อิมพอร์ต-เอ็กซ์ปอร์ต ก่อนเสมอ
    private static readonly Regex LegalLabel = new(@"(มาตรา|ม\.|§)\s*80/1(?!\d)|อัตราศูนย์|zero[\s-]*rat", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VatLine = new(@"\bvat\b|ภาษีมูลค่าเพิ่ม|ภ\.\s*พ\.|value\s+added", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ZeroPercent = new(@"(?<![\d.,])0(\.0+)?\s*%|ร้อยละ\s*(0(\.0+)?|ศูนย์)(?![\d.,])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ExportWord = new(@"ส่งออก|\bexport\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ImportExport = new(@"import\s*[-–/&]?\s*export(er|ing)?|อิมพอร์ต\s*[-–/&]?\s*เอ็กซ์ปอร์ต|นำเข้า\s*[-–/&]?\s*ส่งออก|เพื่อการส่งออก|อุตสาหกรรมส่งออก|เขตอุตสาหกรรม\S*ส่งออก",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InvoiceKindExport = new(@"(tax\s*invoice|ใบกำกับภาษี)\s*[/(\[\-–]*\s*(\(\s*)?(export|ส่งออก)\b|ใบขนสินค้าขาออก|export\s+entry", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>กระดาษเป็นใบกำกับภาษี (คำบนกระดาษ หลังตัดวลีปฏิเสธ)</summary>
    private static bool PaperSaysTaxInvoice(string? rawText)
        => !string.IsNullOrWhiteSpace(rawText) && TaxInvoiceWord.IsMatch(Negation.Replace(rawText, " "));

    /// <summary>กระดาษบอกว่าเป็นอัตราศูนย์/ส่งออก (§80/1) — ป้าย ม.80/1/อัตราศูนย์ · 0% บนบรรทัด VAT · ส่งออก/EXPORT บนบรรทัด VAT หรือกำกับชนิดใบ (ไม่นับชื่อ/ที่อยู่)</summary>
    private static bool HasZeroRatedMarker(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        foreach (var raw in rawText.Split('\n'))
        {
            var line = ImportExport.Replace(raw, " ");
            if (LegalLabel.IsMatch(line) || InvoiceKindExport.IsMatch(line)) return true;
            if (VatLine.IsMatch(line) && (ZeroPercent.IsMatch(line) || ExportWord.IsMatch(line))) return true;
        }
        return false;
    }

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

    // ═══ รอบ 270 ฝ่ายค้าน M3 (คำตัดสิน 270-2-5): ตัวตรวจย้อนหลังของใบที่อนุมัติไปแล้ว — อ่านอย่างเดียว ไม่แก้เอง ═══

    /// <summary>ข้อความของแถวตรวจย้อนหลัง (ใบที่อนุมัติแล้ว) — เหตุ</summary>
    public const string ApprovedMessage = "ใบกำกับจากการสแกนที่อนุมัติแล้ว ทุกบรรทัด VAT 0% และกระดาษไม่บอกว่าเป็นอัตราศูนย์/ส่งออก — ระบบอาจอ่านแถวภาษีมูลค่าเพิ่มไม่ได้ (ใบนี้นับเป็นยอดขาย/ซื้ออัตราศูนย์)";

    /// <summary>ทางไปต่อ — ฝั่งขาย: ใบที่ออกแล้วห้ามแก้ย้อนหลัง (§86/4) ⇒ ยกเลิกแล้วออกใบใหม่ หรือใบเพิ่มหนี้ · ฝั่งซื้อ: ยกเลิกใบซื้อแล้วบันทึกใหม่ตามกระดาษ ·
    /// งวดยื่นแล้ว ⇒ ยื่น ภ.พ.30 เพิ่มเติม · ใบส่งออกจริง ⇒ ไม่ต้องทำอะไร</summary>
    public static string HowToFix(bool salesSide, bool periodFiled)
        => (salesSide
               ? "ตรวจกับกระดาษ — ถ้าเป็นใบ 7%: ยกเลิกใบนี้แล้วออกใบกำกับใหม่ที่ถูกต้อง (ใบที่ออกแล้วแก้ย้อนหลังไม่ได้ §86/4)"
               : "ตรวจกับกระดาษ — ถ้าเป็นใบ 7%: ยกเลิกใบซื้อนี้แล้วบันทึกใหม่ตามกระดาษ (หรือแกะสแกนใหม่) เพื่อให้ภาษีซื้อเข้า ภ.พ.30")
           + (periodFiled ? " · งวด ภ.พ.30 ของใบนี้ยื่นแล้ว ⇒ พิจารณายื่นแบบเพิ่มเติม" : "")
           + " · ถ้าเป็นใบส่งออก/อัตราศูนย์จริง ไม่ต้องทำอะไร";
}
