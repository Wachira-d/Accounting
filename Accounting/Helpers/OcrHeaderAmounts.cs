namespace Accounting.Helpers;

/// <summary>
/// **ยอดสามช่องบนหัวใบ (ก่อน VAT · VAT · รวมทั้งสิ้น) — ซ่อมป้ายที่ engine ติดสลับ**
///
/// <para>ที่มา (สแกนจริง 2026-09-10 · บริษัท ลักกี้ เวย์ · HS6909180): กระดาษพิมพ์
/// “จำนวนเงินรวมทั้งสิ้น 667.00” **ก่อน** “จำนวนภาษีมูลค่าเพิ่ม 43.64” และ
/// “ราคาสินค้า 623.36” ซึ่งกลับลำดับจากฟอร์มทั่วไป ⇒ Azure DI ติดป้าย
/// SubTotal=667 / Total=623.36 (สลับกัน) · ด่านคณิตศาสตร์ฟ้อง “667 + 43.64 ≠ 623.36”
/// แต่**ไม่มีใครลองสลับกลับ** ทั้งที่ 623.36 + 43.64 = 667.00 ลงตัวเป๊ะ ⇒ ตัวจำแนก
/// บรรทัดเห็น Σ บรรทัด 667 = SubTotal 667 จึงตีว่า “ราคาแยก VAT” แล้วเอกสารที่สร้าง
/// เอา 239 (ราคารวม VAT บนกระดาษ) ไปเป็นราคาก่อน VAT แล้วบวก VAT ซ้ำ</para>
///
/// <para>กติกา: นี่**ไม่ใช่การแต่งตัวเลข** — ค่าทั้งสามเป็นค่าที่ engine อ่านมาจริง แค่
/// ป้ายกลับด้าน จึงยอมสลับได้เมื่อ (1) ทั้งสามค่าเป็นบวก (2) “SubTotal” มากกว่า
/// “Total” ซึ่งเป็นไปไม่ได้เมื่อมี VAT (3) Total + VAT = SubTotal ภายในค่าเผื่อ
/// ⇒ ยืนยันด้วยคณิตศาสตร์ว่าเป็นการสลับ ไม่ใช่การอ่านผิดคนละเรื่อง</para>
///
/// <para>ใช้ตัวเดียวทั้งขั้นสกัด (SmartFieldExtractor) · ตัวจำแนกบรรทัด
/// (OcrLineReconciler) · เส้นสร้างเอกสาร (สแกนเก่าที่ persist ค่าสลับไว้แล้ว) —
/// ห้ามเขียนเงื่อนไขสลับซ้ำที่อื่น</para>
/// </summary>
public static class OcrHeaderAmounts
{
    /// <summary>ค่าเผื่อปัดเศษ (บาท) — เท่ากับ <see cref="OcrLineReconciler.Tolerance"/></summary>
    public const decimal Tolerance = 1m;

    /// <summary>true เมื่อ (sub, vat, total) เป็นการติดป้ายสลับของ (total, vat, sub)</summary>
    public static bool IsSwapped(decimal? subTotal, decimal? vat, decimal? total)
    {
        if (subTotal is not > 0m || vat is not > 0m || total is not > 0m) return false;
        if (subTotal.Value <= total.Value) return false;
        return Math.Abs(subTotal.Value - (total.Value + vat.Value)) <= Tolerance;
    }

    /// <summary>คืนสามค่าที่ป้ายถูกต้อง — ถ้าไม่ได้สลับ คืนค่าเดิมทุกตัว</summary>
    public static (decimal? SubTotal, decimal? Vat, decimal? Total, bool Swapped) Normalize(
        decimal? subTotal, decimal? vat, decimal? total)
        => IsSwapped(subTotal, vat, total)
            ? (total, vat, subTotal, true)
            : (subTotal, vat, total, false);

    /// <summary>ข้อความเดียวที่ทุกจุดใช้บันทึกใน trace/ProcessingNotes เมื่อสลับกลับ
    /// (สองที่แต่งข้อความเองจะ drift)</summary>
    public static string SwapNote(decimal subBefore, decimal totalBefore)
        => $"[Σ-SWAP] engine ติดป้าย SubTotal/Total สลับกัน ({subBefore:N2} ↔ {totalBefore:N2}) — "
         + $"สลับกลับเพราะ {totalBefore:N2} + VAT = {subBefore:N2} ลงตัวพอดี";
}
