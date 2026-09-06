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
    /// <summary>แท็กใน <c>ProcessingNotes</c> ที่แปลว่า "ระบบยังไม่แน่ใจ" → ห้ามอนุมัติเอง</summary>
    public static readonly (string Tag, string Why)[] BlockingTags =
    {
        ("[Σ-GAP]",           "ผลรวมรายการไม่ตรงกับยอดบนหัวใบ"),
        ("[DATE-UNKNOWN]",    "อ่านวันที่บนกระดาษไม่ได้ (ระบบเติมวันนี้ให้ชั่วคราว)"),
        ("[FX-UNKNOWN]",      "เอกสารสกุลต่างประเทศแต่ยังไม่มีอัตราแลกเปลี่ยน"),
        ("[TAX-INV-PENDING]", "กระดาษยังไม่ใช่ใบกำกับภาษี — VAT พักไว้รอใบจริง"),
        ("[WHT-CERT]",        "เป็นหนังสือรับรองหัก ณ ที่จ่าย ไม่ใช่เอกสารขาย/ซื้อใบใหม่"),
    };

    /// <summary>ผลการตัดสิน</summary>
    /// <param name="CanAutoApprove">อนุมัติอัตโนมัติได้ไหม</param>
    /// <param name="Reason">เหตุผลที่อนุมัติเองไม่ได้ (null เมื่อผ่าน) — เอาไปโชว์ได้ตรง ๆ</param>
    public readonly record struct Verdict(bool CanAutoApprove, string? Reason);

    /// <summary>ตัดสินจากสิ่งที่เซิร์ฟเวอร์บันทึกไว้บนแถวสแกน
    ///
    /// <para><paramref name="hasUsableDate"/> = <c>ExtractedDate != null</c> —
    /// แยกจากแท็กเพราะแถวเก่าที่สแกนก่อนรอบนี้ยังไม่มี <c>[DATE-UNKNOWN]</c></para></summary>
    public static Verdict Evaluate(string? processingNotes, bool hasUsableDate)
    {
        if (!hasUsableDate)
            return new Verdict(false,
                "อ่านวันที่บนกระดาษไม่ได้ (ระบบเติมวันนี้เป็นค่าเริ่มต้น) — "
                + "เปิดใบ Draft ยืนยันวันที่ก่อนกดอนุมัติ");

        var notes = processingNotes ?? "";
        foreach (var (tag, why) in BlockingTags)
        {
            if (notes.Contains(tag, StringComparison.Ordinal))
                return new Verdict(false, why + " — เปิดใบ Draft ตรวจแล้วกดอนุมัติเอง");
        }
        return new Verdict(true, null);
    }
}
