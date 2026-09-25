using System;

namespace Accounting.Helpers;

/// <summary>VAT หัวใบที่ถืออยู่ มาจากไหน (เทียบกับกระดาษ) — ชั้นหลักฐานตาม DECISION_DOCTRINE §1</summary>
public enum OcrHeaderVatSource
{
    /// <summary>VAT หัวใบ = 0/ไม่มี — ไม่มีอะไรให้ตัดสิน</summary>
    NoVat = 0,
    /// <summary>ตัวเลขนี้พิมพ์บนกระดาษ<b>ในฐานะ VAT</b> (แถวป้าย VAT · ตารางกลุ่มภาษี · ป้ายแล้วตัวเลขบรรทัดถัดไป) หรือมาจาก
    /// e-Tax XML ที่ลงนาม — ใช้พิสูจน์ด้วยเลขคณิตได้</summary>
    Labelled = 1,
    /// <summary>ตัวเลขนี้มีอยู่บนกระดาษ แต่ไม่ได้อยู่คู่ป้าย VAT (อาจบังเอิญตรงยอดบรรทัด) — ยังไม่ใช่หลักฐานพอให้พิสูจน์ แต่ไม่ใช่ค่าแต่ง</summary>
    PrintedUnlabelled = 2,
    /// <summary>ตัวเลขนี้<b>ไม่มีบนกระดาษเลย</b> — ระบบคำนวณเอง (7/107 ของยอดรวม · ยอดรวม − ฐาน) หรืออ่านเพี้ยน</summary>
    NotOnPaper = 3,
}

/// <summary>
/// **"VAT หัวใบตัวนี้อ่านมาจากกระดาษ หรือระบบคำนวณเอง" — ตัวตัดสินตัวเดียว** (pure)
///
/// ═══ ที่มา (รอบ 195 ฝ่ายค้าน C1 · P1 ภาษีซื้อ) ═══
/// <para>ใบร้านผัก "ผักกาดขาว 500 · ผลไม้รวม 570" รวม 1,070 ไม่พิมพ์ VAT — ระบบแยก 7/107 เองได้ VAT 70.00
/// (<c>SmartFieldExtractor</c>/<c>VatBackCalcGuard</c>) แล้ว <see cref="OcrLineVatPlanner"/> "พิสูจน์" ว่าทั้งใบ 7% ด้วยสูตร
/// <c>round(T × 7/107) == VAT</c> ซึ่งเป็น<b>สูตรเดียวกับที่ผลิต VAT ตัวนั้น</b> ⇒ ผ่านทุกครั้ง (CLAUDE.md F2 ข้อ 6
/// "ด่านที่ป้อนผลของสูตรที่ตัวเองตรวจ = ผ่านตลอดกาล") ⇒ ตาข่าย <c>[Σ-GAP]</c> หาย ⇒ "สร้าง+อนุมัติ"/LINE ลงภาษีซื้อ 70 บาท
/// ที่ไม่มีอยู่จริงเข้า ภ.พ.30</para>
///
/// <para>⚠️ <b>ห้ามใช้ความมั่นใจของช่อง VatAmount แทน</b>: <c>SmartFieldExtractor.ValidateOrInferVatRate</c> ดัน VAT ที่
/// "= 7% ของฐาน" ขึ้นเป็น 0.95 — VAT ที่ระบบแยก 7/107 เองก็ = 7% ของฐานเสมอโดยการสร้าง (ความมั่นใจ = สูตรเดียวกันอีกรอบ)
/// และสแกนเก่าไม่มีความมั่นใจรายช่องเลย ⇒ ตัดสินจาก<b>ข้อความกระดาษ</b>ตรง ๆ (ใช้กับสแกนเก่าได้โดยไม่ต้อง migrate)</para>
///
/// <para>สองทิศใช้สองเกณฑ์ (DECISION_DOCTRINE §2 — เกณฑ์ต่างกันตามทิศของความเสียหาย):
/// <b>ตัวพิสูจน์ที่เขียนอัตราเอง</b> ต้อง <see cref="OcrHeaderVatSource.Labelled"/> เท่านั้น (เลขที่บังเอิญตรงยอดบรรทัดไม่ใช่หลักฐาน) ·
/// <b>ตัวหยุดการอนุมัติเอง</b> ติดเมื่อ <see cref="OcrHeaderVatSource.NotOnPaper"/> เท่านั้น (ไม่เตือนใบที่พิมพ์ VAT ไว้คนละที่กับป้าย
/// — คำเตือนที่ฟ้องใบถูก = ปิดด่านโดยไม่ตั้งใจ)</para>
/// </summary>
public static class OcrHeaderVatEvidence
{
    /// <summary>แท็กใน <c>ProcessingNotes</c>: VAT หัวใบไม่มีบนกระดาษ — <see cref="OcrPostingReadiness"/> ห้ามอนุมัติเอง ·
    /// ผู้เขียนคือตัวสร้างบรรทัดเอกสารจากสแกน (<c>OcrService.BuildScanLinesAsync</c>) · คำนวณใหม่ทุกครั้งที่สร้างบรรทัด
    /// (<see cref="OcrLineBuildNotes.StripRecomputed"/>)</summary>
    public const string DerivedTag = "[VAT-DERIVED]";

    /// <summary>ชื่อ engine ของ e-Tax XML ที่ลงนาม (<c>OcrScanResult.OcrEngine</c>) — ตัวเลขมาจากเอกสารทางกฎหมาย ไม่ใช่การอ่านภาพ</summary>
    public const string SignedXmlEngine = "EtaxXml";

    /// <param name="rawText">ข้อความกระดาษตามที่ engine คืน</param>
    /// <param name="normalizedText">ข้อความหลัง <c>ThaiTextNormalizer.Normalize</c> (Tesseract เว้นวรรคระหว่างตัวอักษร) · null = ใช้ rawText อย่างเดียว</param>
    /// <param name="headerVat">VAT หัวใบที่จะใช้สร้างเอกสาร</param>
    /// <param name="ocrEngine"><c>OcrScanResult.OcrEngine</c> — <see cref="SignedXmlEngine"/> = ตัวเลขจาก XML ที่ลงนาม</param>
    public static OcrHeaderVatSource Classify(string? rawText, string? normalizedText, decimal headerVat, string? ocrEngine)
    {
        if (headerVat <= 0m) return OcrHeaderVatSource.NoVat;
        if (string.Equals(ocrEngine, SignedXmlEngine, StringComparison.Ordinal)) return OcrHeaderVatSource.Labelled;
        if (OcrPaperAmounts.IsVatLabelled(rawText, headerVat)
            || (normalizedText != null && OcrPaperAmounts.IsVatLabelled(normalizedText, headerVat)))
            return OcrHeaderVatSource.Labelled;
        if (OcrPaperAmounts.IsPrinted(OcrPaperAmounts.AllPrinted(rawText), headerVat)
            || (normalizedText != null && OcrPaperAmounts.IsPrinted(OcrPaperAmounts.AllPrinted(normalizedText), headerVat)))
            return OcrHeaderVatSource.PrintedUnlabelled;
        return OcrHeaderVatSource.NotOnPaper;
    }

    /// <summary>ข้อความ (ไม่รวมแท็ก) เมื่อ VAT หัวใบไม่มีบนกระดาษ · null = ไม่ต้องเตือน</summary>
    public static string? DerivedNote(OcrHeaderVatSource source, decimal headerVat)
        => source == OcrHeaderVatSource.NotOnPaper
            ? $"VAT {headerVat:N2} ไม่ได้พิมพ์อยู่บนกระดาษ (ระบบคำนวณจากยอดรวม หรืออ่านตัวเลขไม่ตรงกระดาษ) — "
              + "ภาษีซื้อ/ภาษีขายต้องมาจากตัวเลขบนใบกำกับจริง (ม.86/4 · ม.82/5) ตรวจยอด VAT กับกระดาษก่อนอนุมัติ "
              + "(ใบที่ไม่มี VAT เช่นสินค้ายกเว้น ม.81 ให้ตั้ง VAT เป็น 0)"
            : null;
}
