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
    /// <param name="processingNotes"><c>OcrScanResult.ProcessingNotes</c> — มีร่องรอย <see cref="VatBackCalcGuard.BackCalcTag"/> (ระบบถอด 7/107 เอง)
    /// ⇒ ไม่ใช่ <see cref="OcrHeaderVatSource.Labelled"/> = <see cref="OcrHeaderVatSource.NotOnPaper"/> เสมอ (รอบ 195 ฝ่ายค้านรอบสอง R2-4) · null = ไม่รู้ (พฤติกรรมเดิม)</param>
    /// <param name="paperTotal">ยอดรวมทั้งสิ้นของสแกน — ใช้ยืนยันว่า VAT ที่ถืออยู่ยังเป็นค่าที่ถอดจากยอดนี้ (ผู้ใช้แก้ VAT แล้ว ⇒ ไม่ถือร่องรอยเก่า) ·
    /// null = ถือร่องรอยอย่างเดียว</param>
    public static OcrHeaderVatSource Classify(string? rawText, string? normalizedText, decimal headerVat, string? ocrEngine,
        string? processingNotes = null, decimal? paperTotal = null)
    {
        if (headerVat <= 0m) return OcrHeaderVatSource.NoVat;
        if (string.Equals(ocrEngine, SignedXmlEngine, StringComparison.Ordinal)) return OcrHeaderVatSource.Labelled;
        if (OcrPaperAmounts.IsVatLabelled(rawText, headerVat)
            || (normalizedText != null && OcrPaperAmounts.IsVatLabelled(normalizedText, headerVat)))
            return OcrHeaderVatSource.Labelled;
        // ★ รอบ 195 ฝ่ายค้านรอบสอง R2-4: VAT ที่ระบบถอด 7/107 เองแล้ว "บังเอิญ" ตรงกับเลขอื่นบนใบ (ค่าส่ง 70 · เงินทอน · ยอดบรรทัด) เดิมได้
        // PrintedUnlabelled ⇒ ไม่ติด [VAT-DERIVED] ⇒ อนุมัติเองได้ · ร่องรอยการถอดคือหลักฐานตรงว่าตัวเลขนี้มาจากสูตร ไม่ใช่จากกระดาษ
        if (WasBackCalculated(processingNotes, headerVat, paperTotal))
            return OcrHeaderVatSource.NotOnPaper;
        if (OcrPaperAmounts.IsPrinted(OcrPaperAmounts.AllPrinted(rawText), headerVat)
            || (normalizedText != null && OcrPaperAmounts.IsPrinted(OcrPaperAmounts.AllPrinted(normalizedText), headerVat)))
            return OcrHeaderVatSource.PrintedUnlabelled;
        return OcrHeaderVatSource.NotOnPaper;
    }

    /// <summary>ร่องรอย "ระบบถอด VAT จากยอดรวม" อยู่ในหมายเหตุ และ VAT ที่ถืออยู่ยังเท่ากับค่าที่ถอดจากยอดรวมนั้น
    /// (ยอดรวมไม่รู้ ⇒ ถือร่องรอยอย่างเดียว) — ร่องรอยอยู่ในส่วน <c>[Reasoning]</c> จึงค้นแบบ "มีอยู่" ไม่ใช่ "ขึ้นต้นบรรทัด"</summary>
    private static bool WasBackCalculated(string? processingNotes, decimal headerVat, decimal? paperTotal)
    {
        if (string.IsNullOrEmpty(processingNotes)
            || !processingNotes.Contains(VatBackCalcGuard.BackCalcTag, StringComparison.Ordinal)) return false;
        if (paperTotal is not decimal t || t <= 0m) return true;
        return Math.Abs(OcrVatBackCalc.SplitInclusive(t).VatAmount - headerVat) <= OcrPaperAmounts.ExactTol;
    }

    /// <summary>
    /// ข้อความ (ไม่รวมแท็ก) เมื่อ VAT หัวใบไม่มีบนกระดาษ · null = ไม่ต้องเตือน — ตัวตั้งตัวเดียวของทั้งแท็ก <see cref="DerivedTag"/> (ตอนสร้างบรรทัด)
    /// และคำเตือนตอนอนุมัติ (<see cref="OcrApprovalGapWarning.Build"/>)
    ///
    /// <para>รอบ 195 ฝ่ายค้านรอบสอง (PLAUSIBLE ก): ใบเต็มรูปที่พิมพ์แค่ "ราคารวมภาษีมูลค่าเพิ่มแล้ว" (ไม่แยกจำนวน VAT) — ด่านยอมให้ถอด
    /// ⇒ อนุมัติด้วยมือแล้วเคลม 7/107 ได้ · ข้อความเดิมให้ทางไปต่อทางเดียว ("ยกเว้น ม.81 ตั้ง 0") ไม่ได้บอกว่าใบกำกับที่ไม่แสดงจำนวนภาษี
    /// แยก<b>ไม่ครบ ม.86/4(6)</b> ⇒ <b>ภาษีซื้อต้องห้าม ม.82/5(1)</b> · ระบบ<b>ไม่บล็อกและไม่เปลี่ยนค่าเอง</b> (รอเจ้าของตัดสินว่าจะบังคับไหม) —
    /// บอกความจริงและทางเลือกให้ครบ แล้วให้คนตัดสินตอนกดรับทราบ</para>
    /// </summary>
    public static string? DerivedNote(OcrHeaderVatSource source, decimal headerVat)
        => source == OcrHeaderVatSource.NotOnPaper
            ? $"VAT {headerVat:N2} ไม่ได้พิมพ์อยู่บนกระดาษ (ระบบคำนวณจากยอดรวม หรืออ่านตัวเลขไม่ตรงกระดาษ) — ตรวจกับกระดาษก่อนอนุมัติ: "
              + "(1) กระดาษพิมพ์ยอด VAT แยกไว้แต่ระบบอ่านผิด ⇒ แก้ยอดตามกระดาษ · "
              + "(2) ใบกำกับภาษีที่ไม่แสดงจำนวนภาษีแยก (เช่นพิมพ์แค่ “ราคารวมภาษีมูลค่าเพิ่มแล้ว”) = ไม่ครบรายการ ม.86/4(6) "
              + "⇒ ถ้าเป็นเอกสารซื้อ ภาษีซื้อต้องห้าม ม.82/5(1) — ทางเลือก: ตั้ง VAT เป็น 0 แล้วลงค่าใช้จ่ายเต็มจำนวน "
              + "หรือขอใบกำกับฉบับที่แสดงภาษีแยกจากผู้ขาย · "
              + "(3) สินค้า/บริการยกเว้น ม.81 ⇒ ตั้ง VAT เป็น 0"
            : null;
}
