namespace Accounting.Helpers;

/// <summary>
/// **"ใบนี้พร้อมลงบัญชีเองโดยไม่ต้องให้คนดูไหม" — ตัวตัดสินตัวเดียวของทุกช่องทาง**
///
/// ═══ ที่มา (ผลตรวจไปป์ไลน์ OCR 2026-09-06 · T5-03 · T4-15/16) ═══
/// <para>เกณฑ์ auto-create เดิมดูแค่ <c>Confidence</c> + จับคู่คู่ค้าได้ + มีวันที่/
/// เลขที่/ยอด + ไม่ซ้ำ — <b>ไม่มี invariant เชิงตัวเลขตัวไหนระงับเลย</b> ⇒ ใบที่ระบบ
/// เองบอกว่า "Σ บรรทัดไม่ตรงหัวใบ" หรือ "ไม่รู้อัตราแลกเปลี่ยน" ก็ยังถูกอนุมัติ
/// อัตโนมัติได้ · และสามช่องทาง (เว็บ · LINE · มือถือ) ต่างคนต่างตัดสิน ⇒ LINE
/// อนุมัติ 1 แตะโดยไม่แสดง §82/5 เลย</para>
///
/// <para>กติกา: สัญญาณที่เซิร์ฟเวอร์ "พูดออกมาเองแล้ว" ว่ายังไม่แน่ใจ ต้องเป็นตัวหยุด
/// การอนุมัติอัตโนมัติเสมอ — ผู้ใช้ยังกดอนุมัติเองได้ (นั่นคือการตัดสินใจของคน)
/// แต่ระบบต้องไม่ตัดสินแทนเงียบ ๆ</para>
/// </summary>
public static class OcrPostingReadiness
{
    /// <summary>แท็กวันที่ไม่แน่นอน — ผู้เขียนคือ <c>OcrService</c> หลัง <c>OcrDateReader.CrossCheck</c></summary>
    public const string DateUnsureTag = "[DATE-UNSURE]";

    /// <summary>แท็กใน <c>ProcessingNotes</c> ที่แปลว่า "ระบบยังไม่แน่ใจ" → ห้ามอนุมัติเอง</summary>
    public static readonly (string Tag, string Why)[] BlockingTags =
    {
        ("[Σ-GAP]",           "ผลรวมรายการไม่ตรงกับยอดบนหัวใบ"),
        // ★ ด่านคณิตศาสตร์ของ OcrConfidenceGateway (D3-3) — เขียนตอน**สแกน**
        // (ต่างจาก [Σ-GAP] ที่เขียนตอน**สร้างบรรทัดเอกสาร**) ธงนี้ตั้งเมื่อยอดหัวใบ
        // ไม่ลงตัว · สามช่องขัดกันเอง · หรือ Σ บรรทัดไม่ตรงหัวใบ = อย่างน้อยหนึ่งช่อง
        // อ่านผิด ⇒ ตัวเลขที่จะกลายเป็นรายการบัญชีจริงยังไม่ควรผ่านโดยไม่มีคนดู
        // (เดิม `GatewayResult.MathConsistent` ไม่มีผู้อ่านทั้งเรพ)
        ("[MATH]",            "ตัวเลขบนใบขัดกันเอง (ยอดหัวใบไม่ลงตัว หรือ Σ บรรทัดไม่ตรงหัวใบ)"),
        ("[DATE-UNKNOWN]",    "อ่านวันที่บนกระดาษไม่ได้ (ระบบเติมวันนี้ให้ชั่วคราว)"),
        // รอบ 190: วันที่ที่ OcrDateReader เติม/ทับ/สงสัยด้วยความมั่นใจ < 0.85 (ไม่ใช่ "ตรงกับป้าย")
        (DateUnsureTag,       "วันที่เอกสารไม่แน่นอน (ระบบเลือกจากตัวเลขบนกระดาษ หรือขัดกับที่ engine อ่าน)"),
        ("[FX-UNKNOWN]",      "เอกสารสกุลต่างประเทศแต่ยังไม่มีอัตราแลกเปลี่ยน"),
        ("[TAX-INV-PENDING]", "กระดาษยังไม่ใช่ใบกำกับภาษี — VAT พักไว้รอใบจริง"),
        ("[WHT-CERT]",        "เป็นหนังสือรับรองหัก ณ ที่จ่าย ไม่ใช่เอกสารขาย/ซื้อใบใหม่"),
        // รอบ 192 (Total-first): ยอดรวมที่มีหลักฐานแข็งขัดกันและอธิบายกันไม่ได้ — ผู้เขียน Helpers/OcrTotalAnchor
        (OcrTotalAnchor.ConflictTag, "พบยอดรวมทั้งสิ้นสองค่าที่ต่างมีหลักฐานบนกระดาษ — ยังไม่รู้ว่ายอดไหนถูก"),
        // รอบ 192: ยอดที่จ่ายจริง ≠ ยอดใบกำกับ (คูปองแพลตฟอร์ม/ค่าส่งหลังยอดรวม) — ผู้เขียน Helpers/OcrTotalDecomposer
        // รอบ 193: เจ้าของตัดสินวิธีลงแล้ว (บรรทัดปรับตอนชำระ 51120/51150) — ยังหยุดจนกว่าบรรทัดปรับจะถูกบันทึกลงเอกสาร
        // (แท็ก OcrSettlementProposal.SettledTag ปลดตัวนี้ตัวเดียว ดู Evaluate)
        // รอบ 195 ฝ่ายค้าน C1: VAT หัวใบไม่มีบนกระดาษเลย (ระบบแยก 7/107 เอง/อ่านเพี้ยน) — ภาษีซื้อที่ไม่มีบนใบกำกับห้ามลงเอง
        // (ผู้เขียน OcrService.BuildScanLinesAsync ผ่าน Helpers/OcrHeaderVatEvidence · ใบที่พิมพ์ VAT ไว้ไม่ติดแท็กนี้)
        (OcrHeaderVatEvidence.DerivedTag, "VAT ที่จะลงบัญชีไม่ได้พิมพ์อยู่บนกระดาษ (ระบบคำนวณเอง)"),
        (OcrTotalDecomposer.PayNotTotalTag, "ยอดที่ชำระจริงไม่เท่ายอดตามใบกำกับ (ส่วนลด/ค่าส่งหลังยอดรวม) และระบบยังตรวจส่วนต่างไม่ได้ — ตรวจยอดกับกระดาษ แล้วอนุมัติเองที่หน้าเอกสาร (ใบสำคัญจ่าย: ใส่ \"ยอดชำระจริง\" + บรรทัดปรับที่ 'ปรับปรุงรายการบัญชี' ก่อน)"),
    };

    /// <summary>ผลการตัดสิน</summary>
    /// <param name="CanAutoApprove">อนุมัติอัตโนมัติได้ไหม</param>
    /// <param name="Reason">เหตุผลที่อนุมัติเองไม่ได้ (null เมื่อผ่าน) — เอาไปโชว์ได้ตรง ๆ</param>
    public readonly record struct Verdict(bool CanAutoApprove, string? Reason);

    /// <summary>ตัดสินจากสิ่งที่เซิร์ฟเวอร์บันทึกไว้บนแถวสแกน
    ///
    /// <para><paramref name="hasUsableDate"/> = <c>ExtractedDate != null</c> —
    /// แยกจากแท็กเพราะแถวเก่าที่สแกนก่อนรอบนี้ยังไม่มี <c>[DATE-UNKNOWN]</c></para></summary>
    /// <summary>
    /// ตัดสินจากสัญญาณ **ทุกชุด** ที่เซิร์ฟเวอร์มีอยู่แล้ว — แท็กใน
    /// <c>ProcessingNotes</c> · ผลตรวจ §86/4 (<c>ComplianceIssues</c>) · เกรดคุณภาพ
    ///
    /// <para>⚠️ ที่มา (ผลตรวจทีม E · E-03): doc-comment ข้างบนเขียนเองว่าเป็น
    /// "ตัวตัดสินตัวเดียวของทุกช่องทาง" แต่มันดูแค่ 5 แท็ก ขณะที่เว็บมีเกณฑ์
    /// เพิ่มอีกชุดใน JS ⇒ ใบที่มี issue ระดับ <c>error</c> ("ผู้ซื้อในเอกสารไม่ตรง
    /// กับบริษัท — อาจเป็นเอกสารของบริษัทอื่น") บนเว็บ<b>ไม่มีแม้ช่องให้ติ๊ก</b>
    /// แต่บน LINE ขึ้นปุ่มเขียว "อนุมัติเลย" ให้กดลง JE + เข้ารายงานภาษีซื้อ</para>
    /// </summary>
    /// <param name="issueSeverities">ค่า <c>Severity</c> ของทุก issue —
    /// <b><c>null</c> = ยังไม่ได้ประเมิน</b> ซึ่งไม่ใช่ "ไม่มีปัญหา" (สัญญา
    /// เดียวกับที่หน้าเว็บใช้) ⇒ ห้ามอนุมัติเอง</param>
    /// <param name="qualityLetter">เกรดคุณภาพภาพ — <c>"D"</c> = ควรถ่ายใหม่</param>
    public static Verdict Evaluate(
        string? processingNotes,
        bool hasUsableDate,
        IEnumerable<string>? issueSeverities,
        string? qualityLetter)
    {
        var baseVerdict = Evaluate(processingNotes, hasUsableDate);
        if (!baseVerdict.CanAutoApprove) return baseVerdict;

        if (issueSeverities is null)
            return new Verdict(false,
                "ยังไม่ได้ตรวจความครบถ้วนตามสรรพากร — เปิดใบ Draft ตรวจแล้วกดอนุมัติเอง");

        if (issueSeverities.Any(sev => string.Equals(sev, "error", StringComparison.OrdinalIgnoreCase)))
            return new Verdict(false,
                "เอกสารมีข้อผิดพลาดตามข้อกำหนดสรรพากร (เช่น ผู้ซื้อไม่ใช่บริษัทนี้) — "
                + "เปิดใบ Draft ตรวจแล้วกดอนุมัติเอง");

        if (string.Equals(qualityLetter, "D", StringComparison.OrdinalIgnoreCase))
            return new Verdict(false,
                "คุณภาพภาพต่ำ (เกรด D) — ถ่ายใหม่ให้ชัดขึ้น หรือเปิดใบ Draft ตรวจก่อน");

        return new Verdict(true, null);
    }

    public static Verdict Evaluate(string? processingNotes, bool hasUsableDate)
    {
        if (!hasUsableDate)
            return new Verdict(false,
                "อ่านวันที่บนกระดาษไม่ได้ (ระบบเติมวันนี้เป็นค่าเริ่มต้น) — "
                + "เปิดใบ Draft ยืนยันวันที่ก่อนกดอนุมัติ");

        var notes = processingNotes ?? "";
        // รอบ 193 (คำตัดสินเจ้าของข้อ 1): [PAY≠TOTAL] หยุดการอนุมัติเอง "จนกว่าบรรทัดปรับส่วนต่างจะถูกบันทึก"
        // — ผู้เขียน [PAY-SETTLED] คือเส้นสร้างเอกสารจากสแกนเมื่อลงบรรทัดปรับ (Helpers/OcrSettlementProposal) แล้วเท่านั้น
        // · หรือ [PAY-AT-PAYMENT] (เอกสารตั้งหนี้ — ส่วนต่างเป็นเรื่องขั้นชำระ ยอดเอกสารถูกแล้ว · ฝ่ายค้าน C8)
        var paySettled = notes.Contains(OcrSettlementProposal.SettledTag, StringComparison.Ordinal)
            || notes.Contains(OcrSettlementProposal.DeferredTag, StringComparison.Ordinal);
        foreach (var (tag, why) in BlockingTags)
        {
            if (paySettled && tag == OcrTotalDecomposer.PayNotTotalTag) continue;
            if (notes.Contains(tag, StringComparison.Ordinal))
                return new Verdict(false, why + " — เปิดใบ Draft ตรวจแล้วกดอนุมัติเอง");
        }
        return new Verdict(true, null);
    }
}
